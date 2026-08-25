using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A read-only reader for the game's Content.ggpk archive.
	///
	/// GGPK is a flat sequence of length-prefixed records, each starting with:
	///   uint32 Length;   // total bytes of this record, including these 8 header bytes
	///   char[4] Tag;     // "GGPK" | "PDIR" | "FILE" | "FREE"
	///
	/// The record at offset 0 is the GGPK header:
	///   uint32 Length; char[4] "GGPK"; uint32 Version; uint64[(Length-12)/8] Offsets;
	/// Those offsets point at the root PDIR and at the head of the FREE list.
	///
	/// A directory record (PDIR):
	///   uint32 Length; char[4] "PDIR"; uint32 NameLength; uint32 TotalEntries;
	///   byte[32] Hash; wchar_t[NameLength] Name;      // UTF-16LE, NameLength counts the '\0'
	///   { int32 NameHash; uint64 Offset; }[TotalEntries]
	///
	/// A file record (FILE):
	///   uint32 Length; char[4] "FILE"; uint32 NameLength;
	///   byte[32] Hash; wchar_t[NameLength] Name; byte[Length - 44 - NameLength*2] Data
	///
	/// A free-space record (FREE):
	///   uint32 Length; char[4] "FREE"; uint64 NextFreeOffset; byte[...] junk
	///
	/// Notes:
	/// - The child NameHash values in a PDIR are stored but deliberately not used for
	///   lookup here: the hash function changed between GGPK versions (SHA256-prefix,
	///   MurmurHash2A, FNV variants), so this reader resolves paths by comparing names.
	/// - Since ~3.11 most game content no longer lives in loose FILE records; it lives in
	///   Oodle-compressed bundles (Bundles2/_.index.bin plus *.bundle.bin). This class hands
	///   back the raw bytes of those bundle files; decoding them needs an Oodle decompressor
	///   and is a separate concern.
	/// - The file is opened with FileShare.ReadWrite so it can be read while the game runs.
	///
	/// Usage:
	///   using ( var ggpk = GGPKReader.Open(@"C:\PoE\Content.ggpk") ) {
	///       foreach ( var f in ggpk.EnumerateFiles("Metadata/StatDescriptions") )
	///           Log($"{f.FullPath} {f.Length} bytes");
	///       byte[] bytes = ggpk.ReadAllBytes("Bundles2/_.index.bin");
	///       ggpk.ExtractDirectory("Metadata/StatDescriptions", @"C:\dump");
	///   }
	/// </summary>
	public sealed class GGPKReader : IDisposable {

		public const string TagGgpk = "GGPK";
		public const string TagDirectory = "PDIR";
		public const string TagFile = "FILE";
		public const string TagFree = "FREE";

		// Header sizes, for validating a record before trusting its contents.
		private const int RecordHeaderSize = 8;   // Length + Tag
		private const int DirHeaderSize = 48;     // + NameLength + TotalEntries + Hash[32]
		private const int FileHeaderSize = 44;    // + NameLength + Hash[32]
		private const int DirEntrySize = 12;      // int32 NameHash + uint64 Offset

		// Sanity limits, so a corrupt length field produces an exception instead of an
		// out-of-memory allocation.
		private const int MaxNameChars = 4096;
		private const int MaxDirEntries = 1 << 20;

		/// <summary> Full path of the .ggpk file on disk. </summary>
		public string FilePath { get; }

		/// <summary> GGPK container version from the header record (2, 3 or 4 in the wild). </summary>
		public uint Version { get; private set; }

		/// <summary> Offset of the head of the FREE list, or 0 if the header had none. </summary>
		public long FirstFreeRecordOffset { get; private set; }

		/// <summary> The (unnamed) root directory of the archive. </summary>
		public GGPKDirectory Root { get; private set; }

		/// <summary> Total size of the .ggpk file, in bytes. </summary>
		public long FileLength { get; private set; }

		private readonly object sync = new object();
		private readonly byte[] scratch = new byte[64];
		private FileStream stream;

		public GGPKReader(string path) {
			if ( string.IsNullOrWhiteSpace(path) ) throw new ArgumentNullException(nameof(path));
			FilePath = System.IO.Path.GetFullPath(path);
			if ( !File.Exists(FilePath) ) throw new FileNotFoundException("GGPK file not found.", FilePath);
			// FileShare.ReadWrite: the game keeps Content.ggpk open while it runs.
			stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
			FileLength = stream.Length;
			try {
				ParseHeader();
			} catch {
				stream.Dispose();
				stream = null;
				throw;
			}
			Log($"GGPK: opened {FilePath} (version {Version}, {FileLength} bytes)");
		}

		public static GGPKReader Open(string path) => new GGPKReader(path);

		/// <summary>
		/// Look for a Content.ggpk in the given directories (and a few well-known install
		/// locations). Returns the first path that exists, or null.
		/// </summary>
		public static string FindContentGgpk(params string[] hintDirectories) {
			IEnumerable<string> candidates = (hintDirectories ?? Array.Empty<string>())
				.Where(d => !string.IsNullOrWhiteSpace(d))
				.Concat(new string[] {
					@"C:\Program Files (x86)\Grinding Gear Games\Path of Exile",
					@"C:\Program Files\Grinding Gear Games\Path of Exile",
					@"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile",
					@"C:\Program Files\Steam\steamapps\common\Path of Exile",
					@"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2",
					@"C:\Program Files\Steam\steamapps\common\Path of Exile 2",
				});
			foreach ( string dir in candidates ) {
				string full;
				try {
					// A hint may be either the folder or the .ggpk itself.
					full = dir.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)
						? dir : System.IO.Path.Combine(dir, "Content.ggpk");
				} catch ( ArgumentException ) {
					continue; // invalid characters in the hint
				}
				if ( File.Exists(full) ) return full;
			}
			return null;
		}

		public void Dispose() {
			lock ( sync ) {
				stream?.Dispose();
				stream = null;
			}
		}

		private FileStream Handle => stream ?? throw new ObjectDisposedException(nameof(GGPKReader));

		#region Header and record parsing

		private void ParseHeader() {
			lock ( sync ) {
				Seek(0);
				uint length = ReadU32();
				string tag = ReadTag();
				if ( tag != TagGgpk )
					throw new InvalidDataException($"Not a GGPK file: expected tag \"GGPK\" at offset 0, got \"{Printable(tag)}\".");
				if ( length < 12 || length > FileLength )
					throw new InvalidDataException($"GGPK header record has an implausible length: {length}.");
				Version = ReadU32();
				int count = (int)((length - 12) / 8);
				long[] offsets = new long[count];
				for ( int i = 0; i < count; i++ ) offsets[i] = ReadI64();

				// The header conventionally holds [root PDIR, first FREE], but rather than
				// rely on the order, look at the tag each offset actually points to.
				long rootOffset = -1;
				foreach ( long off in offsets ) {
					if ( off <= 0 || off + RecordHeaderSize > FileLength ) continue;
					Seek(off);
					ReadU32(); // record length, unused here
					switch ( ReadTag() ) {
						case TagDirectory: if ( rootOffset < 0 ) rootOffset = off; break;
						case TagFree: if ( FirstFreeRecordOffset == 0 ) FirstFreeRecordOffset = off; break;
					}
				}
				if ( rootOffset < 0 )
					throw new InvalidDataException("GGPK header does not point at a root PDIR record.");
				Root = (GGPKDirectory)ParseRecord(rootOffset, null);
			}
		}

		/// <summary> Parse one PDIR or FILE record. Caller must hold <see cref="sync"/>. </summary>
		private GGPKNode ParseRecord(long offset, GGPKDirectory parent) {
			if ( offset < 0 || offset + RecordHeaderSize > FileLength )
				throw new InvalidDataException($"Record offset {offset} is outside the file.");
			Seek(offset);
			long length = ReadU32();
			string tag = ReadTag();
			if ( length < RecordHeaderSize || offset + length > FileLength )
				throw new InvalidDataException($"Record at {offset} (\"{Printable(tag)}\") has an implausible length: {length}.");

			switch ( tag ) {
				case TagDirectory: {
						if ( length < DirHeaderSize )
							throw new InvalidDataException($"PDIR at {offset} is too short ({length} bytes).");
						int nameChars = (int)ReadU32();
						int entryCount = (int)ReadU32();
						if ( nameChars < 1 || nameChars > MaxNameChars )
							throw new InvalidDataException($"PDIR at {offset} has an implausible name length: {nameChars}.");
						if ( entryCount < 0 || entryCount > MaxDirEntries )
							throw new InvalidDataException($"PDIR at {offset} has an implausible entry count: {entryCount}.");
						long expected = DirHeaderSize + (long)nameChars * 2 + (long)entryCount * DirEntrySize;
						if ( expected > length )
							throw new InvalidDataException($"PDIR at {offset} claims {length} bytes but its name and {entryCount} entries need {expected}.");
						byte[] hash = ReadBytes(32);
						string name = ReadUtf16(nameChars);
						var entries = new GGPKDirectory.Entry[entryCount];
						for ( int i = 0; i < entryCount; i++ ) {
							entries[i].NameHash = ReadI32();
							entries[i].Offset = ReadI64();
						}
						return new GGPKDirectory(this, parent, offset, length, name, hash, entries);
					}
				case TagFile: {
						if ( length < FileHeaderSize )
							throw new InvalidDataException($"FILE at {offset} is too short ({length} bytes).");
						int nameChars = (int)ReadU32();
						if ( nameChars < 1 || nameChars > MaxNameChars )
							throw new InvalidDataException($"FILE at {offset} has an implausible name length: {nameChars}.");
						long needed = FileHeaderSize + (long)nameChars * 2;
						if ( needed > length )
							throw new InvalidDataException($"FILE at {offset} claims {length} bytes but its {nameChars}-char name needs {needed}.");
						byte[] hash = ReadBytes(32);
						string name = ReadUtf16(nameChars);
						long dataOffset = Handle.Position;
						long dataLength = (offset + length) - dataOffset;
						if ( dataLength < 0 )
							throw new InvalidDataException($"FILE at {offset} has a negative data length.");
						return new GGPKFile(this, parent, offset, length, name, hash, dataOffset, dataLength);
					}
				default:
					throw new InvalidDataException($"Unexpected record tag \"{Printable(tag)}\" at offset {offset}; expected PDIR or FILE.");
			}
		}

		/// <summary> Materialize (once) the children of a directory. </summary>
		internal GGPKNode[] LoadChildren(GGPKDirectory dir) {
			lock ( sync ) {
				var entries = dir.Entries;
				var children = new GGPKNode[entries.Length];
				for ( int i = 0; i < entries.Length; i++ )
					children[i] = ParseRecord(entries[i].Offset, dir);
				return children;
			}
		}

		#endregion

		#region Navigation

		/// <summary>
		/// Resolve a '/'- or '\'-separated path (relative to the archive root) to a node.
		/// Matching prefers an exact name, then falls back to case-insensitive.
		/// An empty path resolves to <see cref="Root"/>.
		/// </summary>
		public bool TryFind(string path, out GGPKNode node) {
			node = Root;
			if ( string.IsNullOrWhiteSpace(path) ) return node != null;
			foreach ( string part in SplitPath(path) ) {
				if ( !(node is GGPKDirectory dir) || !dir.TryGetChild(part, out node) ) {
					node = null;
					return false;
				}
			}
			return node != null;
		}

		public bool TryFindFile(string path, out GGPKFile file) {
			file = TryFind(path, out GGPKNode node) ? node as GGPKFile : null;
			return file != null;
		}

		public bool TryFindDirectory(string path, out GGPKDirectory dir) {
			dir = TryFind(path, out GGPKNode node) ? node as GGPKDirectory : null;
			return dir != null;
		}

		/// <summary> Resolve a path, or throw if it is missing or the wrong kind of node. </summary>
		public GGPKFile GetFile(string path) => TryFindFile(path, out var f) ? f
			: throw new FileNotFoundException($"No FILE record at \"{path}\" in {FilePath}.", path);

		public GGPKDirectory GetDirectory(string path) => TryFindDirectory(path, out var d) ? d
			: throw new DirectoryNotFoundException($"No PDIR record at \"{path}\" in {FilePath}.");

		public bool Exists(string path) => TryFind(path, out _);

		internal static IEnumerable<string> SplitPath(string path) =>
			path.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

		#endregion

		#region Listing

		/// <summary>
		/// Depth-first walk of every node under a directory (the directory itself excluded).
		/// Iterative, so a deep tree cannot blow the stack; lazy, so callers can stop early.
		/// </summary>
		public IEnumerable<GGPKNode> Walk(GGPKDirectory from = null) {
			var start = from ?? Root;
			if ( start == null ) yield break;
			var pending = new Stack<GGPKDirectory>();
			pending.Push(start);
			while ( pending.Count > 0 ) {
				var dir = pending.Pop();
				foreach ( var child in dir.Children ) {
					yield return child;
					if ( child is GGPKDirectory sub ) pending.Push(sub);
				}
			}
		}

		/// <summary> Every FILE record under <paramref name="underPath"/> (default: the whole archive). </summary>
		public IEnumerable<GGPKFile> EnumerateFiles(string underPath = null) {
			var dir = string.IsNullOrWhiteSpace(underPath) ? Root : GetDirectory(underPath);
			return Walk(dir).OfType<GGPKFile>();
		}

		/// <summary> Every PDIR record under <paramref name="underPath"/>. </summary>
		public IEnumerable<GGPKDirectory> EnumerateDirectories(string underPath = null) {
			var dir = string.IsNullOrWhiteSpace(underPath) ? Root : GetDirectory(underPath);
			return Walk(dir).OfType<GGPKDirectory>();
		}

		/// <summary> The immediate children of a directory path. </summary>
		public IReadOnlyList<GGPKNode> List(string path = null) =>
			(string.IsNullOrWhiteSpace(path) ? Root : GetDirectory(path)).Children;

		/// <summary>
		/// Files whose full path matches a wildcard pattern, case-insensitively:
		/// '?' is one character, '*' is any run of characters within one path segment, and
		/// '**' spans segments. e.g. Search("Metadata/**/*.dat64").
		/// </summary>
		public IEnumerable<GGPKFile> Search(string wildcardPattern, string underPath = null) {
			if ( string.IsNullOrWhiteSpace(wildcardPattern) ) return Enumerable.Empty<GGPKFile>();
			var rx = WildcardToRegex(wildcardPattern);
			return EnumerateFiles(underPath).Where(f => rx.IsMatch(f.FullPath));
		}

		/// <summary>
		/// Translate a wildcard pattern into a Regex over '/'-separated paths. Shared with
		/// <see cref="BundleIndex.Search"/> so both halves of the archive match the same way.
		/// </summary>
		internal static Regex WildcardToRegex(string pattern) {
			string p = pattern.Replace('\\', '/');
			var sb = new StringBuilder("^");
			for ( int i = 0; i < p.Length; i++ ) {
				char c = p[i];
				if ( c == '*' ) {
					if ( i + 1 < p.Length && p[i + 1] == '*' ) {
						// '**' crosses directory separators; '**/' also matches zero segments.
						if ( i + 2 < p.Length && p[i + 2] == '/' ) { sb.Append("(?:.*/)?"); i += 2; }
						else { sb.Append(".*"); i++; }
					} else sb.Append("[^/]*");
				} else if ( c == '?' ) sb.Append("[^/]");
				else sb.Append(Regex.Escape(c.ToString()));
			}
			return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		/// <summary>
		/// Walk the FREE list. Useful for reporting how much dead space the archive carries;
		/// loops or bad offsets terminate the enumeration rather than throwing.
		/// </summary>
		public IEnumerable<GGPKFreeRecord> EnumerateFreeRecords() {
			var seen = new HashSet<long>();
			long at = FirstFreeRecordOffset;
			while ( at > 0 && at + 16 <= FileLength && seen.Add(at) ) {
				long length, next;
				lock ( sync ) {
					Seek(at);
					length = ReadU32();
					if ( ReadTag() != TagFree ) break;
					next = ReadI64();
				}
				if ( length < 16 || at + length > FileLength ) break;
				yield return new GGPKFreeRecord(at, length, next);
				at = next;
			}
		}

		#endregion

		#region Reading and extracting

		/// <summary> Read the whole contents of a file record into a new array. </summary>
		public byte[] ReadAllBytes(GGPKFile file) {
			if ( file == null ) throw new ArgumentNullException(nameof(file));
			CheckOwnership(file);
			if ( file.Length > int.MaxValue )
				throw new IOException($"\"{file.FullPath}\" is {file.Length} bytes, too large for a single array; use OpenRead().");
			byte[] buffer = new byte[file.Length];
			lock ( sync ) {
				Seek(file.DataOffset);
				ReadExact(buffer, 0, buffer.Length);
			}
			return buffer;
		}

		public byte[] ReadAllBytes(string path) => ReadAllBytes(GetFile(path));

		/// <summary>
		/// Read part of a file record. Returns the number of bytes actually read (fewer than
		/// requested only at end of the record).
		/// </summary>
		public int Read(GGPKFile file, long fileOffset, byte[] buffer, int bufferOffset, int count) {
			if ( file == null ) throw new ArgumentNullException(nameof(file));
			if ( buffer == null ) throw new ArgumentNullException(nameof(buffer));
			if ( fileOffset < 0 ) throw new ArgumentOutOfRangeException(nameof(fileOffset));
			if ( bufferOffset < 0 || count < 0 || count > buffer.Length - bufferOffset )
				throw new ArgumentOutOfRangeException(nameof(count));
			CheckOwnership(file);
			long remaining = file.Length - fileOffset;
			if ( remaining <= 0 ) return 0;
			int want = (int)Math.Min(count, remaining);
			lock ( sync ) {
				Seek(file.DataOffset + fileOffset);
				ReadExact(buffer, bufferOffset, want);
			}
			return want;
		}

		/// <summary>
		/// A seekable, read-only stream over one file record. It opens its own OS handle, so
		/// it is independent of this reader's cursor and safe to use from another thread.
		/// </summary>
		public Stream OpenRead(GGPKFile file) {
			if ( file == null ) throw new ArgumentNullException(nameof(file));
			CheckOwnership(file);
			return new GGPKFileStream(FilePath, file.DataOffset, file.Length);
		}

		public Stream OpenRead(string path) => OpenRead(GetFile(path));

		/// <summary>
		/// Decode a file record as text. Honours a UTF-8 or UTF-16 BOM, and otherwise
		/// defaults to UTF-16LE, which is what most of PoE's loose text files use.
		/// </summary>
		public string ReadAllText(GGPKFile file) {
			byte[] bytes = ReadAllBytes(file);
			if ( bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF )
				return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
			if ( bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE )
				return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
			if ( bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF )
				return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
			return Encoding.Unicode.GetString(bytes);
		}

		public string ReadAllText(string path) => ReadAllText(GetFile(path));

		/// <summary> Write one file record out to disk, creating parent directories as needed. </summary>
		public void Extract(GGPKFile file, string destinationPath) {
			if ( file == null ) throw new ArgumentNullException(nameof(file));
			if ( string.IsNullOrWhiteSpace(destinationPath) ) throw new ArgumentNullException(nameof(destinationPath));
			CheckOwnership(file);
			string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destinationPath));
			if ( !string.IsNullOrEmpty(dir) ) Directory.CreateDirectory(dir);
			using ( var src = OpenRead(file) )
			using ( var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None) ) {
				src.CopyTo(dst, 1 << 16);
			}
		}

		public void Extract(string path, string destinationPath) => Extract(GetFile(path), destinationPath);

		/// <summary>
		/// Extract every file under an archive directory into <paramref name="destinationDirectory"/>,
		/// preserving the relative layout. Returns the number of files written.
		/// </summary>
		public int ExtractDirectory(string archivePath, string destinationDirectory, bool recursive = true) {
			if ( string.IsNullOrWhiteSpace(destinationDirectory) ) throw new ArgumentNullException(nameof(destinationDirectory));
			var dir = string.IsNullOrWhiteSpace(archivePath) ? Root : GetDirectory(archivePath);
			string root = System.IO.Path.GetFullPath(destinationDirectory);
			string prefix = dir.FullPath;
			int written = 0;
			IEnumerable<GGPKFile> files = recursive ? Walk(dir).OfType<GGPKFile>() : dir.Files;
			foreach ( var file in files ) {
				string relative = file.FullPath;
				if ( prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.Ordinal) )
					relative = relative.Substring(prefix.Length).TrimStart('/');
				string outPath = System.IO.Path.GetFullPath(
					System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
				// Record names come from the archive; refuse anything that escapes the destination.
				if ( !outPath.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
						StringComparison.OrdinalIgnoreCase) ) {
					Log($"GGPK: refusing to extract \"{file.FullPath}\" outside {root}");
					continue;
				}
				Extract(file, outPath);
				written++;
			}
			Log($"GGPK: extracted {written} file(s) from \"{(prefix.Length == 0 ? "/" : prefix)}\" to {root}");
			return written;
		}

		/// <summary>
		/// Compare the SHA256 of a file's data against the digest stored in its record.
		/// Older archives store exactly this; some newer versions store a different digest
		/// for bundled content, so a false result is inconclusive rather than proof of damage.
		/// </summary>
		public bool VerifySha256(GGPKFile file) {
			if ( file == null ) throw new ArgumentNullException(nameof(file));
			CheckOwnership(file);
			using ( var sha = SHA256.Create() )
			using ( var src = OpenRead(file) ) {
				byte[] actual = sha.ComputeHash(src);
				byte[] expected = file.Hash;
				if ( actual.Length != expected.Length ) return false;
				for ( int i = 0; i < actual.Length; i++ )
					if ( actual[i] != expected[i] ) return false;
				return true;
			}
		}

		private void CheckOwnership(GGPKNode node, string paramName = "file") {
			if ( !ReferenceEquals(node.Owner, this) )
				throw new ArgumentException("That node came from a different GGPKReader.", paramName);
		}

		#endregion

		#region Primitive reads (caller holds sync)

		private void Seek(long offset) => Handle.Position = offset;

		private void ReadExact(byte[] buffer, int offset, int count) {
			var s = Handle;
			int got = 0;
			while ( got < count ) {
				int read = s.Read(buffer, offset + got, count - got);
				if ( read <= 0 ) throw new EndOfStreamException($"Unexpected end of {FilePath} at offset {s.Position}.");
				got += read;
			}
		}

		private byte[] ReadBytes(int count) {
			byte[] buffer = new byte[count];
			ReadExact(buffer, 0, count);
			return buffer;
		}

		// x64 host, little-endian file: a straight BitConverter is correct here.
		private uint ReadU32() { ReadExact(scratch, 0, 4); return BitConverter.ToUInt32(scratch, 0); }
		private int ReadI32() { ReadExact(scratch, 0, 4); return BitConverter.ToInt32(scratch, 0); }
		private long ReadI64() { ReadExact(scratch, 0, 8); return BitConverter.ToInt64(scratch, 0); }
		private string ReadTag() { ReadExact(scratch, 0, 4); return Encoding.ASCII.GetString(scratch, 0, 4); }

		/// <summary> Read a UTF-16LE name of <paramref name="chars"/> code units, dropping the trailing '\0'. </summary>
		private string ReadUtf16(int chars) {
			byte[] buffer = ReadBytes(chars * 2);
			int bytes = buffer.Length;
			if ( bytes >= 2 && buffer[bytes - 2] == 0 && buffer[bytes - 1] == 0 ) bytes -= 2;
			return Encoding.Unicode.GetString(buffer, 0, bytes);
		}

		private static string Printable(string tag) {
			var sb = new StringBuilder(tag.Length);
			foreach ( char c in tag ) sb.Append(c >= 0x20 && c < 0x7F ? c : '.');
			return sb.ToString();
		}

		#endregion

		public override string ToString() => $"GGPK v{Version} {FilePath} ({FileLength} bytes)";
	}

	/// <summary> One entry in the FREE list: a stretch of unused bytes inside the archive. </summary>
	public readonly struct GGPKFreeRecord {
		public readonly long Offset;
		public readonly long Length;
		public readonly long NextOffset;
		public GGPKFreeRecord(long offset, long length, long nextOffset) {
			Offset = offset; Length = length; NextOffset = nextOffset;
		}
		public override string ToString() => $"FREE @{Offset} {Length} bytes -> {NextOffset}";
	}

	/// <summary> Base class for the two kinds of node in the GGPK tree. </summary>
	public abstract class GGPKNode {

		/// <summary> The reader this node was parsed from. </summary>
		public GGPKReader Owner { get; }

		/// <summary> The containing directory, or null for the root. </summary>
		public GGPKDirectory Parent { get; }

		/// <summary> Byte offset of this node's record header within the .ggpk file. </summary>
		public long RecordOffset { get; }

		/// <summary> Total size of this node's record, including its header. </summary>
		public long RecordLength { get; }

		/// <summary> The node's own name; empty for the root directory. </summary>
		public string Name { get; }

		private readonly byte[] hash;

		/// <summary> The 32-byte digest stored in the record (a copy). </summary>
		public byte[] Hash => (byte[])hash.Clone();

		private string fullPath;

		/// <summary> '/'-separated path from the archive root, with no leading slash. </summary>
		public string FullPath {
			get {
				if ( fullPath != null ) return fullPath;
				if ( Parent == null ) return fullPath = string.Empty;
				string parentPath = Parent.FullPath;
				return fullPath = parentPath.Length == 0 ? Name : parentPath + "/" + Name;
			}
		}

		internal GGPKNode(GGPKReader owner, GGPKDirectory parent, long recordOffset, long recordLength, string name, byte[] hash) {
			Owner = owner;
			Parent = parent;
			RecordOffset = recordOffset;
			RecordLength = recordLength;
			Name = name ?? string.Empty;
			this.hash = hash ?? new byte[32];
		}
	}

	/// <summary> A PDIR record: a named list of child records. Children load on first access. </summary>
	public sealed class GGPKDirectory : GGPKNode {

		internal struct Entry {
			public int NameHash;   // stored, but not used for lookup: the algorithm is version-dependent
			public long Offset;
		}

		internal readonly Entry[] Entries;

		private readonly object gate = new object();
		private GGPKNode[] children;

		internal GGPKDirectory(GGPKReader owner, GGPKDirectory parent, long recordOffset, long recordLength,
			string name, byte[] hash, Entry[] entries)
			: base(owner, parent, recordOffset, recordLength, name, hash) {
			Entries = entries ?? Array.Empty<Entry>();
		}

		/// <summary> Number of immediate children, available without parsing them. </summary>
		public int ChildCount => Entries.Length;

		/// <summary> The immediate children, in archive order. Parsed once, then cached. </summary>
		public IReadOnlyList<GGPKNode> Children {
			get {
				var c = children;
				if ( c != null ) return c;
				lock ( gate ) {
					return children ?? (children = Owner.LoadChildren(this));
				}
			}
		}

		public IEnumerable<GGPKDirectory> Directories => Children.OfType<GGPKDirectory>();

		public IEnumerable<GGPKFile> Files => Children.OfType<GGPKFile>();

		/// <summary> Find an immediate child by name: exact match first, then case-insensitive. </summary>
		public bool TryGetChild(string name, out GGPKNode child) {
			child = null;
			if ( string.IsNullOrEmpty(name) ) return false;
			var kids = Children;
			for ( int i = 0; i < kids.Count; i++ ) {
				if ( string.Equals(kids[i].Name, name, StringComparison.Ordinal) ) {
					child = kids[i];
					return true;
				}
			}
			for ( int i = 0; i < kids.Count; i++ ) {
				if ( string.Equals(kids[i].Name, name, StringComparison.OrdinalIgnoreCase) ) {
					child = kids[i];
					return true;
				}
			}
			return false;
		}

		public override string ToString() => $"PDIR \"{(FullPath.Length == 0 ? "/" : FullPath)}\" ({ChildCount} children)";
	}

	/// <summary> A FILE record: a name plus a run of bytes inside the .ggpk. </summary>
	public sealed class GGPKFile : GGPKNode {

		/// <summary> Absolute offset of this file's first data byte within the .ggpk. </summary>
		public long DataOffset { get; }

		/// <summary> Size of the file's data, in bytes. </summary>
		public long Length { get; }

		internal GGPKFile(GGPKReader owner, GGPKDirectory parent, long recordOffset, long recordLength,
			string name, byte[] hash, long dataOffset, long dataLength)
			: base(owner, parent, recordOffset, recordLength, name, hash) {
			DataOffset = dataOffset;
			Length = dataLength;
		}

		public byte[] ReadAllBytes() => Owner.ReadAllBytes(this);

		public string ReadAllText() => Owner.ReadAllText(this);

		public Stream OpenRead() => Owner.OpenRead(this);

		public void Extract(string destinationPath) => Owner.Extract(this, destinationPath);

		public bool VerifySha256() => Owner.VerifySha256(this);

		public override string ToString() => $"FILE \"{FullPath}\" ({Length} bytes @{DataOffset})";
	}

	/// <summary>
	/// A read-only window onto a slice of the .ggpk file. Holds its own OS handle so several
	/// of these can be read in parallel, and independently of the owning GGPKReader.
	/// </summary>
	internal sealed class GGPKFileStream : Stream {

		private readonly long start;
		private readonly long length;
		private FileStream inner;
		private long position;

		internal GGPKFileStream(string path, long dataOffset, long dataLength) {
			start = dataOffset;
			length = dataLength;
			inner = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
			inner.Position = start;
		}

		public override bool CanRead => inner != null;
		public override bool CanSeek => inner != null;
		public override bool CanWrite => false;
		public override long Length => length;

		public override long Position {
			get => position;
			set {
				if ( value < 0 ) throw new ArgumentOutOfRangeException(nameof(Position));
				position = value;
				Inner.Position = start + value;
			}
		}

		private FileStream Inner => inner ?? throw new ObjectDisposedException(nameof(GGPKFileStream));

		public override int Read(byte[] buffer, int offset, int count) {
			if ( buffer == null ) throw new ArgumentNullException(nameof(buffer));
			if ( offset < 0 || count < 0 || count > buffer.Length - offset )
				throw new ArgumentOutOfRangeException(nameof(count));
			long remaining = length - position;
			if ( remaining <= 0 ) return 0;
			int want = (int)Math.Min(count, remaining);
			var s = Inner;
			s.Position = start + position; // keep the handle honest even if someone seeked it
			int read = s.Read(buffer, offset, want);
			if ( read > 0 ) position += read;
			return read;
		}

		public override long Seek(long offset, SeekOrigin origin) {
			long target;
			switch ( origin ) {
				case SeekOrigin.Begin: target = offset; break;
				case SeekOrigin.Current: target = position + offset; break;
				case SeekOrigin.End: target = length + offset; break;
				default: throw new ArgumentOutOfRangeException(nameof(origin));
			}
			if ( target < 0 ) throw new IOException("Cannot seek before the start of the file record.");
			Position = target;
			return position;
		}

		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing) {
			if ( disposing ) {
				inner?.Dispose();
				inner = null;
			}
			base.Dispose(disposing);
		}
	}
}
