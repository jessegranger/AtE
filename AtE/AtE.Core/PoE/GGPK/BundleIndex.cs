using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Which hash function maps a file path to the 64-bit key in the bundle index. GGG changed
	/// this in 3.21.2, and <see cref="BundleIndex"/> detects which one an install uses rather
	/// than guessing from a version number.
	/// </summary>
	public enum PathHashScheme {
		Unknown = 0,
		/// <summary>3.21.2 and later: MurmurHash64A of the lower-cased path, seed 0x1337b33f.</summary>
		Murmur64A,
		/// <summary>3.11.2 to 3.21.1: FNV-1a 64 of the lower-cased path with "++" appended.</summary>
		Fnv1a64,
	}

	/// <summary> The two path-hash functions the bundle index has used. </summary>
	public static class PathHash {

		public const ulong MurmurSeed = 0x1337B33F;

		/// <summary>MurmurHash64A (Austin Appleby's 64-bit MurmurHash2 for x64).</summary>
		public static ulong Murmur64A(byte[] data, int offset, int length, ulong seed = MurmurSeed) {
			unchecked {
				const ulong m = 0xC6A4A7935BD1E995UL;
				const int r = 47;
				ulong h = seed ^ ((ulong)length * m);
				int blocks = length / 8;
				for ( int i = 0; i < blocks; i++ ) {
					ulong k = BitConverter.ToUInt64(data, offset + i * 8);
					k *= m;
					k ^= k >> r;
					k *= m;
					h ^= k;
					h *= m;
				}
				int tail = offset + blocks * 8;
				switch ( length & 7 ) {
					case 7: h ^= (ulong)data[tail + 6] << 48; goto case 6;
					case 6: h ^= (ulong)data[tail + 5] << 40; goto case 5;
					case 5: h ^= (ulong)data[tail + 4] << 32; goto case 4;
					case 4: h ^= (ulong)data[tail + 3] << 24; goto case 3;
					case 3: h ^= (ulong)data[tail + 2] << 16; goto case 2;
					case 2: h ^= (ulong)data[tail + 1] << 8; goto case 1;
					case 1:
						h ^= data[tail];
						h *= m;
						break;
				}
				h ^= h >> r;
				h *= m;
				h ^= h >> r;
				return h;
			}
		}

		/// <summary>FNV-1a, 64-bit.</summary>
		public static ulong Fnv1a64(byte[] data, int offset, int length) {
			unchecked {
				ulong hash = 0xCBF29CE484222325UL;
				for ( int i = 0; i < length; i++ ) {
					hash ^= data[offset + i];
					hash *= 0x100000001B3UL;
				}
				return hash;
			}
		}

		/// <summary>Hash a bundle file path under the given scheme.</summary>
		public static ulong ForPath(string path, PathHashScheme scheme) {
			if ( path == null ) throw new ArgumentNullException(nameof(path));
			string normalized = path.Replace('\\', '/').ToLowerInvariant();
			switch ( scheme ) {
				case PathHashScheme.Murmur64A: {
						byte[] bytes = Encoding.UTF8.GetBytes(normalized);
						return Murmur64A(bytes, 0, bytes.Length);
					}
				case PathHashScheme.Fnv1a64: {
						byte[] bytes = Encoding.UTF8.GetBytes(normalized + "++");
						return Fnv1a64(bytes, 0, bytes.Length);
					}
				default:
					throw new ArgumentException("Unknown path hash scheme.", nameof(scheme));
			}
		}
	}

	/// <summary>
	/// Where the bundle files come from. The standalone client keeps them inside Content.ggpk;
	/// the Steam build keeps them as loose files under Bundles2/. Both look the same from here.
	/// </summary>
	public interface IBundleSource : IDisposable {

		/// <summary>
		/// Open a file below <c>Bundles2/</c> — "_.index.bin", or "Art/Foo.bundle.bin".
		/// Returns false if it does not exist.
		/// </summary>
		bool TryOpen(string relativePath, out Stream stream);

		/// <summary>
		/// The game folder this source reads from. May be null. Informational — the Oodle
		/// decoder ships with AtE, so nothing needs to look for a DLL here.
		/// </summary>
		string GameDirectory { get; }

		/// <summary>Human-readable origin, for logs.</summary>
		string Description { get; }
	}

	/// <summary>Bundles read out of a Content.ggpk (standalone client).</summary>
	public sealed class GgpkBundleSource : IBundleSource {

		private readonly GGPKReader ggpk;
		private readonly bool ownsReader;

		public GgpkBundleSource(GGPKReader ggpk, bool ownsReader = false) {
			this.ggpk = ggpk ?? throw new ArgumentNullException(nameof(ggpk));
			this.ownsReader = ownsReader;
		}

		public string GameDirectory => Path.GetDirectoryName(ggpk.FilePath);

		public string Description => ggpk.FilePath;

		public bool TryOpen(string relativePath, out Stream stream) {
			stream = null;
			if ( !ggpk.TryFindFile("Bundles2/" + relativePath, out GGPKFile file) ) return false;
			stream = file.OpenRead();
			return true;
		}

		public void Dispose() {
			if ( ownsReader ) ggpk.Dispose();
		}
	}

	/// <summary>Bundles read as loose files from a game folder (Steam / Epic install).</summary>
	public sealed class DirectoryBundleSource : IBundleSource {

		private readonly string bundles2;

		public DirectoryBundleSource(string gameDirectory) {
			if ( gameDirectory == null ) throw new ArgumentNullException(nameof(gameDirectory));
			if ( gameDirectory.Trim().Length == 0 )
				throw new ArgumentException("Game directory is empty.", nameof(gameDirectory));
			GameDirectory = Path.GetFullPath(gameDirectory);
			bundles2 = Path.Combine(GameDirectory, "Bundles2");
			if ( !Directory.Exists(bundles2) )
				throw new DirectoryNotFoundException($"No Bundles2 folder under {GameDirectory}.");
		}

		public string GameDirectory { get; }

		public string Description => bundles2;

		public bool TryOpen(string relativePath, out Stream stream) {
			stream = null;
			string full = Path.Combine(bundles2, relativePath.Replace('/', Path.DirectorySeparatorChar));
			if ( !File.Exists(full) ) return false;
			stream = new FileStream(full, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
			return true;
		}

		public void Dispose() { }
	}

	/// <summary>
	/// The decoded <c>Bundles2/_.index.bin</c>: the map from a game file path to the bundle,
	/// offset and length holding its bytes — and the code to pull those bytes back out
	/// decompressed.
	///
	/// The index is itself a bundle. Decompressed, it holds:
	///   uint32 bundle_count;   { uint32 name_length; char name[]; uint32 uncompressed_size; }[]
	///   uint32 file_count;     { uint64 path_hash; uint32 bundle_index; uint32 offset; uint32 size; }[]
	///   uint32 path_rep_count; { uint64 hash; uint32 payload_offset, payload_size, payload_recursive_size; }[]
	///   ... and then a nested bundle holding the compressed path list.
	///
	/// File paths are not stored as strings. They are generated from that nested bundle by a
	/// tiny stack machine (see <see cref="DecodePaths"/>) and matched to file records by hash.
	///
	/// Usage:
	///   using ( var index = BundleIndex.Open(@"C:\Program Files (x86)\Grinding Gear Games\Path of Exile") ) {
	///       byte[] dds = index.ReadAllBytes("art/2dart/skillicons/passives/assassin/4k/deadly.dds");
	///       index.ExtractDirectory("art/2dart/skillicons", @"C:\dump");
	///   }
	/// </summary>
	public sealed class BundleIndex : IDisposable {

		/// <summary>One bundle listed in the index.</summary>
		public readonly struct BundleInfo {
			public readonly string Name;
			public readonly long UncompressedSize;
			public BundleInfo(string name, long uncompressedSize) {
				Name = name;
				UncompressedSize = uncompressedSize;
			}
			public override string ToString() => $"{Name} ({UncompressedSize} bytes)";
		}

		/// <summary>Where one game file lives: a slice of one bundle's decompressed content.</summary>
		public readonly struct FileRecord {
			public readonly ulong PathHash;
			public readonly int BundleIndex;
			public readonly int Offset;
			public readonly int Size;
			public FileRecord(ulong pathHash, int bundleIndex, int offset, int size) {
				PathHash = pathHash;
				BundleIndex = bundleIndex;
				Offset = offset;
				Size = size;
			}
			public override string ToString() => $"bundle {BundleIndex} +{Offset} {Size} bytes";
		}

		private const int MaxOpenBundles = 8;

		private readonly IBundleSource source;
		private readonly bool ownsSource;
		private readonly object gate = new object();

		private BundleInfo[] bundles;
		private FileRecord[] files;
		private Dictionary<string, FileRecord> byPath;
		private string[] paths;

		// A small LRU of open bundles: each holds a stream + block table, not decompressed data.
		// Decoding happens outside the lock (it is slow, and several threads decode at once), so
		// a bundle also carries a use count and is never evicted while a read is inside it.
		private readonly Dictionary<int, Bundle> openBundles = new Dictionary<int, Bundle>();
		private readonly Dictionary<int, int> bundleUsers = new Dictionary<int, int>();
		private readonly List<int> openOrder = new List<int>();

		// Built once, on whichever thread asks first. Its own lock, because `gate` is held across
		// blocking file I/O in AcquireBundle and the render thread reads Tree every frame.
		private readonly object treeGate = new object();
		private volatile BundleDirectory tree;

		private bool disposed;

		/// <summary>Which hash function this install's index uses.</summary>
		public PathHashScheme Scheme { get; private set; }

		/// <summary>Every bundle the index lists.</summary>
		public IReadOnlyList<BundleInfo> Bundles => bundles;

		/// <summary>Number of file records.</summary>
		public int FileCount => files.Length;

		/// <summary>Every file path, lower-cased as the index stores them.</summary>
		public IReadOnlyList<string> Paths => paths;

		/// <summary>Where these bundles came from, for logs.</summary>
		public string Description => source.Description;

		private BundleIndex(IBundleSource source, bool ownsSource) {
			this.source = source;
			this.ownsSource = ownsSource;
		}

		#region Opening

		/// <summary>
		/// Open the index for a game install: either a Content.ggpk (standalone) or a folder
		/// with a loose Bundles2 directory (Steam). Accepts the folder or the .ggpk path.
		/// </summary>
		public static BundleIndex Open(string gameDirectoryOrGgpk) {
			if ( string.IsNullOrWhiteSpace(gameDirectoryOrGgpk) )
				throw new ArgumentNullException(nameof(gameDirectoryOrGgpk));

			string path = Path.GetFullPath(gameDirectoryOrGgpk);
			string ggpkPath =
				path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase) ? path :
				File.Exists(Path.Combine(path, "Content.ggpk")) ? Path.Combine(path, "Content.ggpk") :
				null;

			if ( ggpkPath != null ) {
				var ggpk = new GGPKReader(ggpkPath);
				try {
					return Open(new GgpkBundleSource(ggpk, ownsReader: true), ownsSource: true);
				} catch {
					ggpk.Dispose();
					throw;
				}
			}
			return Open(new DirectoryBundleSource(path), ownsSource: true);
		}

		/// <summary>Open the index inside an already-open Content.ggpk.</summary>
		public static BundleIndex Open(GGPKReader ggpk) =>
			Open(new GgpkBundleSource(ggpk, ownsReader: false), ownsSource: true);

		/// <summary>Open the index over any bundle source.</summary>
		public static BundleIndex Open(IBundleSource source, bool ownsSource = false) {
			if ( source == null ) throw new ArgumentNullException(nameof(source));
			var index = new BundleIndex(source, ownsSource);
			try {
				// Every bundle — including the index itself — is Oodle-compressed, so without a
				// decoder there is nothing to read. Say so here, rather than letting it surface
				// from somewhere deep inside a block decode.
				if ( !Oodle.TryInitialise() && !Oodle.IsAvailable ) {
					throw new InvalidOperationException(Oodle.NotFoundAdvice + "\n\n" + Oodle.SearchReport);
				}
				index.Load();
			} catch {
				index.Dispose();
				throw;
			}
			return index;
		}

		private void Load() {
			Stream raw;
			if ( !source.TryOpen("_.index.bin", out raw) )
				throw new FileNotFoundException($"No Bundles2/_.index.bin in {source.Description}.");

			byte[] index;
			using ( raw )
			using ( var bundle = Bundle.Open(raw, ownsStream: false, name: "_.index.bin") ) {
				index = bundle.DecompressAll();
			}

			int at = 0;
			int bundleCount = ReadInt(index, ref at);
			if ( bundleCount < 0 || bundleCount > 1 << 22 )
				throw new InvalidDataException($"Index claims {bundleCount} bundles.");
			bundles = new BundleInfo[bundleCount];
			for ( int i = 0; i < bundleCount; i++ ) {
				int nameLength = ReadInt(index, ref at);
				if ( nameLength < 0 || nameLength > 4096 || at + nameLength > index.Length )
					throw new InvalidDataException($"Index bundle {i} has an implausible name length {nameLength}.");
				string name = Encoding.UTF8.GetString(index, at, nameLength);
				at += nameLength;
				bundles[i] = new BundleInfo(name, (uint)ReadInt(index, ref at));
			}

			int fileCount = ReadInt(index, ref at);
			if ( fileCount < 0 || (long)fileCount * 20 > index.Length - at )
				throw new InvalidDataException($"Index claims {fileCount} files, which does not fit.");
			files = new FileRecord[fileCount];
			for ( int i = 0; i < fileCount; i++ ) {
				ulong hash = BitConverter.ToUInt64(index, at); at += 8;
				int bundleIndex = ReadInt(index, ref at);
				int offset = ReadInt(index, ref at);
				int size = ReadInt(index, ref at);
				if ( bundleIndex < 0 || bundleIndex >= bundleCount )
					throw new InvalidDataException($"File record {i} points at bundle {bundleIndex}.");
				// These are uint32 on disk. Nothing near 2 GiB exists in a real bundle, so a
				// negative value here means the index is not what we think it is.
				if ( offset < 0 || size < 0 )
					throw new InvalidDataException($"File record {i} has offset {offset} and size {size}.");
				files[i] = new FileRecord(hash, bundleIndex, offset, size);
			}

			int pathRepCount = ReadInt(index, ref at);
			if ( pathRepCount < 0 || (long)pathRepCount * 20 > index.Length - at )
				throw new InvalidDataException($"Index claims {pathRepCount} path groups, which does not fit.");
			var reps = new (int Offset, int Size)[pathRepCount];
			for ( int i = 0; i < pathRepCount; i++ ) {
				at += 8; // the directory's own hash; we do not need it to generate paths
				int payloadOffset = ReadInt(index, ref at);
				int payloadSize = ReadInt(index, ref at);
				at += 4;  // payload_recursive_size: the same paths plus subdirectories, so skip it
				reps[i] = (payloadOffset, payloadSize);
			}

			// Everything left is a nested bundle holding the path list.
			byte[] pathPayload;
			using ( var pathBundle = Bundle.Open(new MemoryStream(index, at, index.Length - at, false), true, "_.index.bin paths") ) {
				pathPayload = pathBundle.DecompressAll();
			}

			var collected = new List<string>(fileCount);
			for ( int i = 0; i < reps.Length; i++ ) {
				var rep = reps[i];
				if ( rep.Offset < 0 || rep.Size < 0 || (long)rep.Offset + rep.Size > pathPayload.Length ) {
					Log($"BundleIndex: path group {i} is out of range ({rep.Offset}+{rep.Size} of {pathPayload.Length}); skipped.");
					continue;
				}
				DecodePaths(pathPayload, rep.Offset, rep.Size, collected);
			}
			paths = collected.ToArray();

			Scheme = DetectScheme();
			BuildPathMap();
			Log($"BundleIndex: {source.Description} — {bundles.Length} bundles, {files.Length} files, " +
				$"{paths.Length} paths, {Scheme} hashing.");
		}

		private static int ReadInt(byte[] data, ref int at) {
			if ( at + 4 > data.Length ) throw new InvalidDataException("Ran off the end of the bundle index.");
			int v = BitConverter.ToInt32(data, at);
			at += 4;
			return v;
		}

		#endregion

		#region Path decoding

		/// <summary>
		/// Turn one path group's payload into full paths.
		///
		/// The encoding alternates between two phases, flipped by a zero word. Decoding starts in
		/// the output phase, and since the payload opens with a zero word the first thing that
		/// actually happens is a switch into a fresh base phase. In either phase the stream is pairs of a
		/// 1-based index into the reference list and a NUL-terminated string. An in-range index
		/// prefixes that reference onto the string; an out-of-range index means no prefix. In the
		/// base phase the result is appended to the reference list; in the other phase it is a
		/// finished path. Each new base phase starts a fresh reference list.
		/// </summary>
		internal static void DecodePaths(byte[] payload, int start, int length, List<string> output) {
			var bases = new List<string>();
			bool baseMode = false;
			int at = start;
			int end = start + length;
			while ( at + 4 <= end ) {
				int command = BitConverter.ToInt32(payload, at);
				at += 4;
				if ( command == 0 ) {
					baseMode = !baseMode;
					if ( baseMode ) bases.Clear();
					continue;
				}
				int index = command - 1;
				int stop = at;
				while ( stop < end && payload[stop] != 0 ) stop++;
				string text = Encoding.UTF8.GetString(payload, at, stop - at);
				at = stop + 1;  // step over the NUL
				if ( index >= 0 && index < bases.Count ) text = bases[index] + text;
				if ( baseMode ) bases.Add(text);
				else output.Add(text);
			}
		}

		/// <summary>
		/// Work out which hash function this index uses by trying both against real records,
		/// rather than trusting a version number.
		/// </summary>
		private PathHashScheme DetectScheme() {
			if ( paths.Length == 0 || files.Length == 0 ) return PathHashScheme.Unknown;
			var known = new HashSet<ulong>();
			for ( int i = 0; i < files.Length; i++ ) known.Add(files[i].PathHash);

			int sampleCount = Math.Min(64, paths.Length);
			int stride = Math.Max(1, paths.Length / sampleCount);
			int murmur = 0, fnv = 0, tried = 0;
			for ( int i = 0; i < paths.Length && tried < sampleCount; i += stride, tried++ ) {
				if ( known.Contains(PathHash.ForPath(paths[i], PathHashScheme.Murmur64A)) ) murmur++;
				if ( known.Contains(PathHash.ForPath(paths[i], PathHashScheme.Fnv1a64)) ) fnv++;
			}
			if ( murmur == 0 && fnv == 0 ) {
				Log($"BundleIndex: neither hash scheme matched any of {tried} sampled paths. " +
					"Lookups by name will not work; file records are still readable by index.");
				return PathHashScheme.Unknown;
			}
			return murmur >= fnv ? PathHashScheme.Murmur64A : PathHashScheme.Fnv1a64;
		}

		private void BuildPathMap() {
			byPath = new Dictionary<string, FileRecord>(files.Length, StringComparer.OrdinalIgnoreCase);
			if ( Scheme == PathHashScheme.Unknown ) return;

			var byHash = new Dictionary<ulong, FileRecord>(files.Length);
			for ( int i = 0; i < files.Length; i++ ) byHash[files[i].PathHash] = files[i];

			int missed = 0;
			for ( int i = 0; i < paths.Length; i++ ) {
				ulong hash = PathHash.ForPath(paths[i], Scheme);
				if ( byHash.TryGetValue(hash, out FileRecord rec) ) byPath[paths[i]] = rec;
				else missed++;
			}
			if ( missed > 0 )
				Log($"BundleIndex: {missed} of {paths.Length} generated paths had no matching file record.");
		}

		#endregion

		#region Lookup and reading

		/// <summary>Find a file record by path (case-insensitive; '/' or '\' both work).</summary>
		public bool TryGetFile(string path, out FileRecord record) {
			record = default;
			if ( string.IsNullOrWhiteSpace(path) || byPath == null ) return false;
			return byPath.TryGetValue(path.Replace('\\', '/').TrimStart('/'), out record);
		}

		/// <summary>True if the index holds a file at that path.</summary>
		public bool Exists(string path) => TryGetFile(path, out _);

		/// <summary>Every path, or every path under a '/'-separated prefix.</summary>
		public IEnumerable<string> EnumeratePaths(string prefix = null) {
			if ( string.IsNullOrWhiteSpace(prefix) ) return paths;
			string p = prefix.Replace('\\', '/').Trim('/') + "/";
			return paths.Where(x => x.StartsWith(p, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Paths matching a wildcard: '?' is one character, '*' any run within a path segment,
		/// '**' spans segments. e.g. "art/2dart/skillicons/**/*.dds".
		/// </summary>
		public IEnumerable<string> Search(string wildcardPattern) {
			if ( string.IsNullOrWhiteSpace(wildcardPattern) ) return Enumerable.Empty<string>();
			var rx = GGPKReader.WildcardToRegex(wildcardPattern);
			// A lambda, not the `rx.IsMatch` method group: IsMatch(string) and IsMatch(string, int)
			// both fit Where's two overloads, so the group conversion is ambiguous.
			return paths.Where(p => rx.IsMatch(p));
		}

		/// <summary>The decompressed bytes of one file record.</summary>
		public byte[] Read(FileRecord record) {
			if ( record.BundleIndex < 0 || record.BundleIndex >= bundles.Length )
				throw new ArgumentOutOfRangeException(nameof(record), "Record points at a bundle that is not in the index.");
			Bundle bundle = AcquireBundle(record.BundleIndex);
			try {
				return bundle.Decompress(record.Offset, record.Size);
			} finally {
				ReleaseBundle(record.BundleIndex);
			}
		}

		/// <summary>The decompressed bytes of the file at <paramref name="path"/>.</summary>
		public byte[] ReadAllBytes(string path) {
			if ( !TryGetFile(path, out FileRecord record) )
				throw new FileNotFoundException($"No bundled file \"{path}\" in {source.Description}.", path);
			return Read(record);
		}

		public bool TryReadAllBytes(string path, out byte[] bytes) {
			bytes = null;
			if ( !TryGetFile(path, out FileRecord record) ) return false;
			try {
				bytes = Read(record);
				return true;
			} catch ( Exception e ) {
				Log($"BundleIndex: failed to read \"{path}\": {e.Message}");
				return false;
			}
		}

		/// <summary>Decode a bundled file as text (UTF-16LE with a BOM, else UTF-8).</summary>
		public string ReadAllText(string path) {
			byte[] bytes = ReadAllBytes(path);
			if ( bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE )
				return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
			if ( bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF )
				return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
			return new UTF8Encoding(false).GetString(bytes);
		}

		/// <summary>
		/// Get an open bundle and mark it in use. Every call must be paired with
		/// <see cref="ReleaseBundle"/>, or that bundle can never be evicted.
		/// </summary>
		private Bundle AcquireBundle(int bundleIndex) {
			lock ( gate ) {
				// Without this, a read that was waiting on the lock during Dispose would open a
				// fresh bundle into the emptied cache, and nothing would ever close it.
				if ( disposed ) throw new ObjectDisposedException(nameof(BundleIndex));
				if ( !openBundles.TryGetValue(bundleIndex, out Bundle bundle) ) {
					string name = bundles[bundleIndex].Name;
					Stream stream;
					if ( !source.TryOpen(name + ".bundle.bin", out stream) )
						throw new FileNotFoundException($"Bundle \"{name}.bundle.bin\" is missing from {source.Description}.");
					try {
						bundle = Bundle.Open(stream, ownsStream: true, name: name);
					} catch {
						stream.Dispose();
						throw;
					}
					openBundles[bundleIndex] = bundle;
				}
				// Most-recently-used goes to the back of the eviction queue.
				openOrder.Remove(bundleIndex);
				openOrder.Add(bundleIndex);
				bundleUsers.TryGetValue(bundleIndex, out int users);
				bundleUsers[bundleIndex] = users + 1;
				// Never let a failure to close some *other* bundle escape past the increment:
				// that would strand this use count and pin the bundle open forever.
				try {
					Evict();
				} catch ( Exception e ) {
					Log($"BundleIndex: evicting an idle bundle failed: {e.Message}");
				}
				return bundle;
			}
		}

		private void ReleaseBundle(int bundleIndex) {
			lock ( gate ) {
				if ( bundleUsers.TryGetValue(bundleIndex, out int users) ) {
					if ( users <= 1 ) bundleUsers.Remove(bundleIndex);
					else bundleUsers[bundleIndex] = users - 1;
				}
				Evict();
			}
		}

		/// <summary>Close the least-recently-used idle bundles. Caller holds <see cref="gate"/>.</summary>
		private void Evict() {
			for ( int i = 0; i < openOrder.Count && openBundles.Count > MaxOpenBundles; ) {
				int candidate = openOrder[i];
				if ( bundleUsers.ContainsKey(candidate) ) {
					i++;   // someone is decoding from it; leave it alone
					continue;
				}
				openOrder.RemoveAt(i);
				if ( openBundles.TryGetValue(candidate, out Bundle old) ) {
					openBundles.Remove(candidate);
					old.Dispose();
				}
			}
		}

		#endregion

		#region Extraction

		/// <summary>Write one bundled file out to disk.</summary>
		public void Extract(string path, string destinationPath) {
			if ( string.IsNullOrWhiteSpace(destinationPath) ) throw new ArgumentNullException(nameof(destinationPath));
			byte[] bytes = ReadAllBytes(path);
			string dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
			if ( !string.IsNullOrEmpty(dir) ) Directory.CreateDirectory(dir);
			File.WriteAllBytes(destinationPath, bytes);
		}

		/// <summary>
		/// Extract every file under a path prefix, preserving the layout below it. Files are
		/// pulled in (bundle, offset) order so each decompressed block gets reused instead of
		/// being decoded once per file. Returns the number of files written.
		/// </summary>
		public int ExtractDirectory(string prefix, string destinationDirectory) {
			if ( string.IsNullOrWhiteSpace(destinationDirectory) ) throw new ArgumentNullException(nameof(destinationDirectory));
			string root = Path.GetFullPath(destinationDirectory);
			string clean = string.IsNullOrWhiteSpace(prefix) ? "" : prefix.Replace('\\', '/').Trim('/');
			string strip = clean.Length == 0 ? "" : clean + "/";

			var wanted = new List<(string Path, FileRecord Record)>();
			foreach ( string p in EnumeratePaths(clean) ) {
				if ( TryGetFile(p, out FileRecord r) ) wanted.Add((p, r));
			}
			// Same bundle, ascending offset: consecutive files then share decompressed blocks.
			wanted.Sort((a, b) => a.Record.BundleIndex != b.Record.BundleIndex
				? a.Record.BundleIndex.CompareTo(b.Record.BundleIndex)
				: a.Record.Offset.CompareTo(b.Record.Offset));

			int written = 0;
			foreach ( var item in wanted ) {
				string relative = item.Path;
				if ( strip.Length > 0 && relative.StartsWith(strip, StringComparison.OrdinalIgnoreCase) )
					relative = relative.Substring(strip.Length);
				string outPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
				if ( !outPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
						StringComparison.OrdinalIgnoreCase) ) {
					Log($"BundleIndex: refusing to extract \"{item.Path}\" outside {root}");
					continue;
				}
				try {
					byte[] bytes = Read(item.Record);
					string dir = Path.GetDirectoryName(outPath);
					if ( !string.IsNullOrEmpty(dir) ) Directory.CreateDirectory(dir);
					File.WriteAllBytes(outPath, bytes);
					written++;
				} catch ( Exception e ) {
					Log($"BundleIndex: failed to extract \"{item.Path}\": {e.Message}");
				}
			}
			Log($"BundleIndex: extracted {written} file(s) from \"{(clean.Length == 0 ? "/" : clean)}\" to {root}");
			return written;
		}

		#endregion

		#region Directory tree (for browsing)

		/// <summary>
		/// The paths arranged as a tree, built once on first use. Bundled paths are flat strings,
		/// so this is what a file browser walks.
		/// </summary>
		public BundleDirectory Tree {
			get {
				var built = tree;
				if ( built != null ) return built;
				lock ( treeGate ) {
					if ( tree != null ) return tree;
					var root = new BundleDirectory(null, "");
					for ( int i = 0; i < paths.Length; i++ ) {
						string path = paths[i];
						if ( !TryGetFile(path, out FileRecord record) ) continue;
						int start = 0;
						BundleDirectory dir = root;
						while ( true ) {
							int slash = path.IndexOf('/', start);
							if ( slash < 0 ) break;
							dir = dir.GetOrAddDirectory(path.Substring(start, slash - start));
							start = slash + 1;
						}
						dir.AddFile(path.Substring(start), path, record);
					}
					return tree = root;
				}
			}
		}

		#endregion

		/// <summary>
		/// Close every open bundle and, if this index owns it, the source. Reads in flight on
		/// other threads will fail with <see cref="ObjectDisposedException"/> — stop them first.
		/// </summary>
		public void Dispose() {
			lock ( gate ) {
				disposed = true;
				foreach ( var bundle in openBundles.Values ) bundle.Dispose();
				openBundles.Clear();
				bundleUsers.Clear();
				openOrder.Clear();
				if ( ownsSource ) source?.Dispose();
			}
		}

		public override string ToString() =>
			$"BundleIndex {source.Description}: {files?.Length ?? 0} files in {bundles?.Length ?? 0} bundles ({Scheme})";
	}

	/// <summary>A folder in the bundle path tree.</summary>
	public sealed class BundleDirectory {

		public string Name { get; }
		public string FullPath { get; }
		public BundleDirectory Parent { get; }

		private readonly SortedDictionary<string, BundleDirectory> directories =
			new SortedDictionary<string, BundleDirectory>(StringComparer.OrdinalIgnoreCase);
		private readonly List<BundleFile> files = new List<BundleFile>();

		internal BundleDirectory(BundleDirectory parent, string name) {
			Parent = parent;
			Name = name ?? "";
			FullPath = parent == null || parent.FullPath.Length == 0 ? Name : parent.FullPath + "/" + Name;
		}

		public IEnumerable<BundleDirectory> Directories => directories.Values;
		public IReadOnlyList<BundleFile> Files => files;
		public int DirectoryCount => directories.Count;

		internal BundleDirectory GetOrAddDirectory(string name) {
			if ( !directories.TryGetValue(name, out BundleDirectory child) ) {
				child = new BundleDirectory(this, name);
				directories[name] = child;
			}
			return child;
		}

		internal void AddFile(string name, string fullPath, BundleIndex.FileRecord record) =>
			files.Add(new BundleFile(name, fullPath, record));

		public bool TryGetDirectory(string name, out BundleDirectory child) => directories.TryGetValue(name, out child);

		public override string ToString() => $"{(FullPath.Length == 0 ? "/" : FullPath)} ({directories.Count} dirs, {files.Count} files)";
	}

	/// <summary>A file in the bundle path tree.</summary>
	public sealed class BundleFile {

		public string Name { get; }
		public string FullPath { get; }
		public BundleIndex.FileRecord Record { get; }
		public int Size => Record.Size;

		internal BundleFile(string name, string fullPath, BundleIndex.FileRecord record) {
			Name = name;
			FullPath = fullPath;
			Record = record;
		}

		/// <summary>The file's extension, lower-cased, without the dot ("dds", "dat64", "").</summary>
		public string Extension {
			get {
				int dot = Name.LastIndexOf('.');
				return dot < 0 ? "" : Name.Substring(dot + 1);
			}
		}

		public override string ToString() => $"{FullPath} ({Size} bytes)";
	}
}
