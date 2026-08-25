using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AtE {

	/// <summary>
	/// Writes 8-bit RGBA pixels out as a PNG.
	///
	/// Small on purpose: the bundle browser needs to turn a decoded .dds into a file that any
	/// image viewer will open, and that is all this does — colour type 6 (truecolour + alpha),
	/// 8 bits per channel, no interlacing, filter 0 (None) on every scanline. Nothing here needs
	/// System.Drawing or WIC, so it works in the portable core and on both hosts.
	///
	/// Structure, for reference: an 8-byte signature, then chunks of
	/// {length:be32, type:4 ascii, data, crc32:be32} — IHDR, one IDAT holding a zlib stream of
	/// the filtered scanlines, and IEND. <see cref="ZLibStream"/> supplies the zlib framing
	/// (header plus Adler-32) that PNG's IDAT requires, so this only assembles chunks.
	/// </summary>
	public static class PngEncoder {

		private static readonly byte[] Signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

		private const int BytesPerPixel = 4;

		/// <summary>Encode to a new byte array.</summary>
		public static byte[] Encode(int width, int height, byte[] rgba, int rowPitch = 0) {
			using ( var ms = new MemoryStream() ) {
				Write(ms, width, height, rgba, rowPitch);
				return ms.ToArray();
			}
		}

		/// <summary>Encode straight to a file, creating parent directories as needed.</summary>
		public static void WriteFile(string path, int width, int height, byte[] rgba, int rowPitch = 0) {
			if ( path == null ) throw new ArgumentNullException(nameof(path));
			if ( path.Trim().Length == 0 ) throw new ArgumentException("Destination path is empty.", nameof(path));
			string dir = Path.GetDirectoryName(Path.GetFullPath(path));
			if ( !string.IsNullOrEmpty(dir) ) Directory.CreateDirectory(dir);
			using ( var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None) ) {
				Write(file, width, height, rgba, rowPitch);
			}
		}

		/// <summary>
		/// Write a PNG to <paramref name="stream"/>. <paramref name="rgba"/> is top-down, four
		/// bytes per pixel in R,G,B,A order; <paramref name="rowPitch"/> defaults to a tightly
		/// packed <c>width * 4</c>.
		/// </summary>
		public static void Write(Stream stream, int width, int height, byte[] rgba, int rowPitch = 0) {
			if ( stream == null ) throw new ArgumentNullException(nameof(stream));
			if ( rgba == null ) throw new ArgumentNullException(nameof(rgba));
			if ( width <= 0 || height <= 0 || (long)width * BytesPerPixel > int.MaxValue )
				throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height} is not an image this can write.");
			int pitch = rowPitch > 0 ? rowPitch : width * BytesPerPixel;
			if ( pitch < width * BytesPerPixel )
				throw new ArgumentOutOfRangeException(nameof(rowPitch),
					$"A row of {width} RGBA pixels needs {width * BytesPerPixel} bytes, not {pitch}.");
			long needed = (long)pitch * (height - 1) + (long)width * BytesPerPixel;
			if ( rgba.Length < needed )
				throw new ArgumentException(
					$"{width}x{height} at pitch {pitch} needs {needed} bytes, got {rgba.Length}.", nameof(rgba));

			stream.Write(Signature, 0, Signature.Length);

			byte[] ihdr = new byte[13];
			WriteBigEndian(ihdr, 0, (uint)width);
			WriteBigEndian(ihdr, 4, (uint)height);
			ihdr[8] = 8;    // bit depth
			ihdr[9] = 6;    // colour type 6: truecolour with alpha
			ihdr[10] = 0;   // compression method: deflate (the only one defined)
			ihdr[11] = 0;   // filter method 0 (the only one defined)
			ihdr[12] = 0;   // no interlacing
			WriteChunk(stream, "IHDR", ihdr, 0, ihdr.Length);

			byte[] idat;
			using ( var compressed = new MemoryStream() ) {
				using ( var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true) ) {
					// Every scanline is prefixed with its filter type. 0 (None) costs some size
					// versus Sub/Up/Paeth, but keeps this to something worth trusting on sight.
					byte[] noFilter = new byte[1];
					for ( int y = 0; y < height; y++ ) {
						zlib.Write(noFilter, 0, 1);
						zlib.Write(rgba, y * pitch, width * BytesPerPixel);
					}
				}
				idat = compressed.ToArray();
			}
			WriteChunk(stream, "IDAT", idat, 0, idat.Length);
			WriteChunk(stream, "IEND", Array.Empty<byte>(), 0, 0);
		}

		private static void WriteChunk(Stream stream, string type, byte[] data, int offset, int count) {
			if ( type == null || type.Length != 4 ) throw new ArgumentException("A PNG chunk type is 4 characters.", nameof(type));
			byte[] header = new byte[8];
			WriteBigEndian(header, 0, (uint)count);
			Encoding.ASCII.GetBytes(type, 0, 4, header, 4);
			stream.Write(header, 0, header.Length);
			if ( count > 0 ) stream.Write(data, offset, count);

			// The CRC covers the chunk type and its data, but not the length field.
			uint crc = Crc32.Start();
			crc = Crc32.Update(crc, header, 4, 4);
			if ( count > 0 ) crc = Crc32.Update(crc, data, offset, count);
			byte[] trailer = new byte[4];
			WriteBigEndian(trailer, 0, Crc32.Finish(crc));
			stream.Write(trailer, 0, trailer.Length);
		}

		// uint plus explicit masking, so this is correct whether or not the assembly is ever
		// compiled with CheckForOverflowUnderflow (a CRC ≥ 0x80000000 would throw otherwise).
		private static void WriteBigEndian(byte[] buffer, int offset, uint value) {
			buffer[offset + 0] = (byte)((value >> 24) & 0xFF);
			buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
			buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
			buffer[offset + 3] = (byte)(value & 0xFF);
		}

		/// <summary>The standard CRC-32 that PNG chunks carry.</summary>
		private static class Crc32 {

			private const uint Polynomial = 0xEDB88320;
			private static readonly uint[] Table = BuildTable();

			private static uint[] BuildTable() {
				var table = new uint[256];
				for ( uint i = 0; i < 256; i++ ) {
					uint c = i;
					for ( int k = 0; k < 8; k++ ) c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
					table[i] = c;
				}
				return table;
			}

			internal static uint Start() => 0xFFFFFFFF;

			internal static uint Update(uint crc, byte[] data, int offset, int count) {
				for ( int i = 0; i < count; i++ ) crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
				return crc;
			}

			internal static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
		}
	}
}
