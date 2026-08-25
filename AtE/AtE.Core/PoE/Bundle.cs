using System;
using System.IO;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// One <c>*.bundle.bin</c> file: a header, a table of compressed block sizes, then the
	/// Oodle-compressed blocks themselves.
	///
	/// Layout (offsets from the start of the bundle):
	///   0   uint32 uncompressed_size            // 32-bit view of the 64-bit field below
	///   4   uint32 total_payload_size
	///   8   uint32 head_payload_size            // == 48 + 4*block_count
	///   12  uint32 first_file_encode            // OodleCompressor
	///   16  uint32 unknown
	///   20  int64  uncompressed_size            // authoritative
	///   28  int64  total_payload_size
	///   36  int32  block_count
	///   40  int32  uncompressed_block_granularity   // 256 KiB in practice
	///   44  uint32 unknown[4]
	///   60  int32  block_sizes[block_count]     // compressed size of each block
	///   60+4*block_count  the compressed blocks, back to back
	///
	/// Every block decodes to exactly <see cref="ChunkSize"/> bytes except the last, which is
	/// the remainder. Block boundaries have nothing to do with file boundaries, which is why
	/// <see cref="Decompress"/> can decode just the blocks a byte range touches instead of the
	/// whole bundle — for a 200 KiB texture inside a 50 MB bundle that is the difference between
	/// one block and two hundred.
	/// </summary>
	public sealed class Bundle : IDisposable {

		/// <summary>Bytes before the block-size table.</summary>
		public const int FixedHeaderSize = 60;

		/// <summary>What <c>head_payload_size</c> should be, less 4 bytes per block.</summary>
		private const int HeadPayloadBase = 48;

		private const int MaxBlockCount = 1 << 20;

		/// <summary>A label for diagnostics — usually the bundle's index name.</summary>
		public string Name { get; }

		public OodleCompressor Encoder { get; private set; }

		/// <summary>Total size of everything this bundle holds, decompressed.</summary>
		public long UncompressedSize { get; private set; }

		/// <summary>Total size of the compressed payload.</summary>
		public long CompressedSize { get; private set; }

		public int BlockCount { get; private set; }

		/// <summary>Decompressed size of every block but the last (256 KiB in practice).</summary>
		public int ChunkSize { get; private set; }

		private readonly object gate = new object();
		private int[] blockCompressedSizes;
		private long[] blockStarts;      // absolute stream offset of each block's compressed bytes
		private Stream stream;
		private readonly bool ownsStream;
		private readonly long origin;    // where this bundle starts within the stream

		// Most files are far smaller than a 256 KiB block, and bulk extraction walks a bundle in
		// offset order, so remembering the last single block decoded turns "decompress per file"
		// into "decompress per block" for the price of one chunk of memory.
		private int memoBlock = -1;
		private byte[] memoData;

		private Bundle(Stream stream, bool ownsStream, string name) {
			this.stream = stream;
			this.ownsStream = ownsStream;
			Name = name ?? "<bundle>";
			origin = stream.CanSeek ? stream.Position : 0;
		}

		/// <summary>
		/// Read a bundle's header from <paramref name="stream"/>, starting at its current
		/// position. The stream must be seekable and stays open for as long as the bundle is
		/// used (it is read from on every <see cref="Decompress"/>).
		/// </summary>
		public static Bundle Open(Stream stream, bool ownsStream = true, string name = null) {
			if ( stream == null ) throw new ArgumentNullException(nameof(stream));
			if ( !stream.CanSeek ) throw new ArgumentException("A bundle needs a seekable stream.", nameof(stream));
			var bundle = new Bundle(stream, ownsStream, name);
			try {
				bundle.ParseHeader();
			} catch {
				if ( ownsStream ) stream.Dispose();
				throw;
			}
			return bundle;
		}

		/// <summary>Read a bundle held entirely in memory.</summary>
		public static Bundle Open(byte[] bytes, string name = null) =>
			Open(new MemoryStream(bytes ?? throw new ArgumentNullException(nameof(bytes)), false), true, name);

		private void ParseHeader() {
			byte[] head = new byte[FixedHeaderSize];
			ReadAt(origin, head, 0, head.Length);

			int headPayloadSize = BitConverter.ToInt32(head, 8);
			Encoder = (OodleCompressor)BitConverter.ToUInt32(head, 12);
			UncompressedSize = BitConverter.ToInt64(head, 20);
			CompressedSize = BitConverter.ToInt64(head, 28);
			BlockCount = BitConverter.ToInt32(head, 36);
			ChunkSize = BitConverter.ToInt32(head, 40);

			if ( BlockCount < 0 || BlockCount > MaxBlockCount )
				throw new InvalidDataException($"Bundle \"{Name}\": implausible block count {BlockCount}.");
			if ( ChunkSize <= 0 )
				throw new InvalidDataException($"Bundle \"{Name}\": implausible chunk size {ChunkSize}.");
			if ( UncompressedSize < 0 || CompressedSize < 0 )
				throw new InvalidDataException($"Bundle \"{Name}\": negative size in header.");
			if ( headPayloadSize != HeadPayloadBase + 4 * BlockCount )
				throw new InvalidDataException(
					$"Bundle \"{Name}\": head_payload_size is {headPayloadSize}, expected " +
					$"{HeadPayloadBase + 4 * BlockCount} for {BlockCount} blocks. Not a bundle?");
			// The blocks must be able to cover the claimed output.
			long capacity = (long)BlockCount * ChunkSize;
			if ( UncompressedSize > capacity )
				throw new InvalidDataException(
					$"Bundle \"{Name}\": {BlockCount} blocks of {ChunkSize} cannot hold {UncompressedSize} bytes.");

			byte[] table = new byte[4 * BlockCount];
			ReadAt(origin + FixedHeaderSize, table, 0, table.Length);
			blockCompressedSizes = new int[BlockCount];
			blockStarts = new long[BlockCount];
			long at = origin + FixedHeaderSize + table.Length;
			long sum = 0;
			for ( int i = 0; i < BlockCount; i++ ) {
				int size = BitConverter.ToInt32(table, i * 4);
				if ( size < 0 ) throw new InvalidDataException($"Bundle \"{Name}\": block {i} has a negative size.");
				blockCompressedSizes[i] = size;
				blockStarts[i] = at;
				at += size;
				sum += size;
			}
			if ( sum != CompressedSize )
				Log($"Bundle \"{Name}\": block sizes sum to {sum} but the header says {CompressedSize}; using the block table.");
		}

		/// <summary>Decompressed size of one block.</summary>
		public int BlockUncompressedSize(int block) {
			if ( block < 0 || block >= BlockCount ) throw new ArgumentOutOfRangeException(nameof(block));
			if ( block < BlockCount - 1 ) return ChunkSize;
			long tail = UncompressedSize - (long)block * ChunkSize;
			return (int)Math.Min(tail, ChunkSize);
		}

		/// <summary>
		/// Decompress <paramref name="count"/> bytes starting at <paramref name="offset"/> in the
		/// bundle's decompressed content, decoding only the blocks that range touches.
		/// </summary>
		public byte[] Decompress(long offset, int count) {
			if ( offset < 0 ) throw new ArgumentOutOfRangeException(nameof(offset));
			if ( count < 0 ) throw new ArgumentOutOfRangeException(nameof(count));
			if ( offset + count > UncompressedSize )
				throw new ArgumentOutOfRangeException(nameof(count),
					$"Bundle \"{Name}\" holds {UncompressedSize} bytes; asked for {count} at {offset}.");
			if ( count == 0 ) return Array.Empty<byte>();

			int first = (int)(offset / ChunkSize);
			int last = (int)((offset + count - 1) / ChunkSize);

			if ( first == last ) {
				lock ( gate ) {
					if ( memoBlock == first && memoData != null ) {
						byte[] hit = new byte[count];
						Buffer.BlockCopy(memoData, (int)(offset - (long)first * ChunkSize), hit, 0, count);
						return hit;
					}
				}
			}

			// Total decoded size of the blocks we are about to touch.
			long span = 0;
			for ( int b = first; b <= last; b++ ) span += BlockUncompressedSize(b);
			if ( span + Oodle.SafeMargin > int.MaxValue )
				throw new IOException($"Bundle \"{Name}\": that range spans {span} bytes, too much for one buffer.");

			// Pull the compressed bytes out of the stream first, so the (slow) decode does not
			// hold the stream lock — several files from one bundle often decode in parallel.
			byte[][] compressed = new byte[last - first + 1][];
			lock ( gate ) {
				var s = stream ?? throw new ObjectDisposedException(nameof(Bundle));
				for ( int b = first; b <= last; b++ ) {
					byte[] buf = new byte[blockCompressedSizes[b]];
					ReadAt(blockStarts[b], buf, 0, buf.Length, s);
					compressed[b - first] = buf;
				}
			}

			// Oodle can scribble up to SafeMargin bytes past the end, so over-allocate.
			byte[] scratch = new byte[span + Oodle.SafeMargin];
			int written = 0;
			for ( int b = first; b <= last; b++ ) {
				byte[] src = compressed[b - first];
				int outSize = BlockUncompressedSize(b);
				if ( Encoder == OodleCompressor.None ) {
					// Stored, not compressed.
					if ( src.Length != outSize )
						throw new InvalidDataException(
							$"Bundle \"{Name}\": block {b} is stored uncompressed but is {src.Length} bytes, expected {outSize}.");
					Buffer.BlockCopy(src, 0, scratch, written, outSize);
				} else {
					Oodle.Decompress(src, 0, src.Length, scratch, written, outSize);
				}
				written += outSize;
			}

			if ( first == last ) {
				lock ( gate ) {
					memoBlock = first;
					memoData = scratch;
				}
			}

			byte[] result = new byte[count];
			Buffer.BlockCopy(scratch, (int)(offset - (long)first * ChunkSize), result, 0, count);
			return result;
		}

		/// <summary>Decompress the whole bundle.</summary>
		public byte[] DecompressAll() {
			if ( UncompressedSize > int.MaxValue - Oodle.SafeMargin )
				throw new IOException($"Bundle \"{Name}\" is {UncompressedSize} bytes, too large to decode in one array.");
			return Decompress(0, (int)UncompressedSize);
		}

		private void ReadAt(long position, byte[] buffer, int offset, int count) {
			lock ( gate ) {
				ReadAt(position, buffer, offset, count, stream ?? throw new ObjectDisposedException(nameof(Bundle)));
			}
		}

		private void ReadAt(long position, byte[] buffer, int offset, int count, Stream s) {
			s.Position = position;
			int got = 0;
			while ( got < count ) {
				int read = s.Read(buffer, offset + got, count - got);
				if ( read <= 0 )
					throw new EndOfStreamException($"Bundle \"{Name}\": unexpected end of stream at {position + got}.");
				got += read;
			}
		}

		public void Dispose() {
			lock ( gate ) {
				if ( ownsStream ) stream?.Dispose();
				stream = null;
			}
		}

		public override string ToString() =>
			$"Bundle \"{Name}\" {Encoder} {BlockCount} blocks, {UncompressedSize} bytes ({CompressedSize} compressed)";
	}
}
