using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Oodle compressors used inside PoE's bundles. The value is what the bundle header
	/// stores in its <c>first_file_encode</c> field.
	/// </summary>
	public enum OodleCompressor : uint {
		Lzh = 0,
		Lzhlw = 1,
		Lznib = 2,
		None = 3,
		Lzb16 = 4,
		Lzblw = 5,
		Lza = 6,
		Lzna = 7,
		Kraken = 8,
		Mermaid = 9,
		Bitknit = 10,
		Selkie = 11,
		Hydra = 12,
		Leviathan = 13,
	}

	/// <summary>
	/// A late-bound binding to a decoder for the Oodle streams inside PoE's bundles.
	///
	/// Path of Exile links Oodle statically and ships no loose <c>oo2core_*.dll</c>, so the
	/// decoder has to come from us. The repo carries the open-source ooz build
	/// (<c>AtE.Core/Native/&lt;rid&gt;/libooz.*</c> from https://github.com/zao/ooz), which each
	/// host copies next to its executable; <see cref="TryInitialise"/> loads it from there by
	/// name and binds <c>Ooz_Decompress</c>.
	///
	/// <see cref="OverridePath"/> (surfaced as a Bundle Browser setting) points at a different
	/// library instead. Genuine Oodle is accepted there too: any <c>oo2core_*_win64.dll</c>
	/// exporting <c>OodleLZ_Decompress</c> binds through the same path, which is useful for
	/// checking ooz's output against the real decoder.
	///
	/// Binding goes through <see cref="NativeLibrary"/>, which behaves the same on Windows and
	/// Linux, so this stays in the portable core. <see cref="SearchReport"/> records what was
	/// tried, because "not found" with no detail is useless.
	///
	/// Usage: <see cref="TryInitialise"/> once, then <see cref="Decompress"/>. Both are safe to
	/// call from several threads; the decode entry point is re-entrant and loading is guarded.
	/// </summary>
	public static class Oodle {

		/// <summary>
		/// Oodle may write up to 64 bytes past the end of the decoded data, so every output
		/// buffer must have this much slack beyond the size passed as <c>rawLen</c>.
		/// (Documented on the bundle schema page; skipping it corrupts the heap.)
		/// </summary>
		public const int SafeMargin = 64;

		// OodleLZ_Decompress(compBuf, compBufSize, rawBuf, rawLen, fuzzSafe, checkCRC, verbosity,
		//   decBufBase, decBufSize, fpCallback, callbackUserData, decoderMemory, decoderMemorySize,
		//   threadPhase) -> bytes written
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate long OodleLZDecompressFn(
			IntPtr compBuf, IntPtr compBufSize,
			IntPtr rawBuf, IntPtr rawLen,
			int fuzzSafe, int checkCRC, int verbosity,
			IntPtr decBufBase, IntPtr decBufSize,
			IntPtr fpCallback, IntPtr callbackUserData,
			IntPtr decoderMemory, IntPtr decoderMemorySize,
			int threadPhase);

		// Ooz_Decompress(src, srcLen, dst, dstLen) -> bytes written
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int OozDecompressFn(IntPtr src, int srcLen, IntPtr dst, int dstLen);

		private const int FuzzSafeYes = 1;
		private const int CheckCrcNo = 0;
		private const int VerbosityNone = 0;
		private const int ThreadPhaseAll = 3;

		private static readonly object sync = new object();
		private static IntPtr library;
		private static OodleLZDecompressFn oodleDecompress;
		private static OozDecompressFn oozDecompress;

		/// <summary> Path of the native library currently bound, or null. </summary>
		public static string LibraryPath { get; private set; }

		/// <summary>
		/// What the last <see cref="TryInitialise"/> looked at and why each candidate failed.
		/// Shown in the UI when loading fails, so the fix is obvious instead of guesswork.
		/// </summary>
		public static string SearchReport { get; private set; } = "(no search yet)";

		/// <summary>
		/// The message to show a user when no decoder could be found. Names the two things that
		/// actually work, rather than pointing at the game folder.
		/// </summary>
		public const string NotFoundAdvice =
			"No Oodle decoder loaded, so the bundles cannot be decompressed. The build should have " +
			"copied libooz next to the executable; if it did not, check that the binary for this " +
			"platform exists under AtE.Core/Native/ (see the README there). As a workaround you can " +
			"point the \"Oodle DLL\" setting in the Bundle Browser at any libooz or oo2core_*_win64.dll.";

		/// <summary> The loaded module, kept mapped for the process lifetime (see <see cref="Unload"/>). </summary>
		internal static IntPtr LibraryHandle => library;

		/// <summary> True once a decompressor has been bound. </summary>
		public static bool IsAvailable => oodleDecompress != null || oozDecompress != null;

		/// <summary>
		/// The bundled decoder's base name. The runtime decorates it per platform
		/// (<c>libooz.dll</c> on Windows, <c>libooz.so</c> on Linux, <c>libooz.dylib</c> on
		/// macOS), so this one name covers every host.
		/// </summary>
		public const string BundledLibraryName = "libooz";

		/// <summary>The shared-library extension for the running OS.</summary>
		private static string PlatformLibrarySuffix =>
			RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll" :
			RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";

		/// <summary>
		/// An explicit library to use instead of the bundled one. Set it before the first
		/// <see cref="TryInitialise"/> — the Bundle Browser exposes it as a setting so an
		/// oo2core DLL can be used without rebuilding.
		/// </summary>
		public static string OverridePath { get; set; }

		/// <summary>
		/// Bind the decoder: <see cref="OverridePath"/> if one is set, otherwise the
		/// <c>libooz</c> shipped next to the executable. No directory scanning — the library is
		/// a build output of this repo, so there is exactly one place it should be.
		///
		/// Returns true if a usable entry point was bound; idempotent once bound.
		/// </summary>
		public static bool TryInitialise() {
			lock ( sync ) {
				if ( IsAvailable ) return true;
				var report = new StringBuilder();

				string over = OverridePath;
				if ( !string.IsNullOrWhiteSpace(over) ) {
					if ( TryBindPath(over) ) {
						SearchReport = $"Oodle: bound the override at {over}";
						return true;
					}
					report.AppendLine($"Oodle: override \"{over}\" could not be loaded or has no known export.");
				}

				// The one place the build puts it. Loading by full path (rather than by name)
				// keeps LibraryPath a real path, which every later diagnostic quotes.
				string decorated = BundledLibraryName + PlatformLibrarySuffix;
				string beside = null;
				try {
					string dir = AppContext.BaseDirectory;
					if ( !string.IsNullOrEmpty(dir) ) beside = Path.Combine(dir, decorated);
				} catch ( Exception ) { }

				if ( beside != null && File.Exists(beside) ) {
					if ( TryBindPath(beside) ) {
						SearchReport = Describe($"bound {beside}", report);
						return true;
					}
					report.AppendLine($"Oodle: {beside} exists but would not load or bind. " +
						"Is it 64-bit, and does it have its C runtime alongside?");
				} else {
					report.AppendLine($"Oodle: no {decorated} beside the executable ({beside ?? "unknown directory"}). " +
						"The build should have copied it there — check AtE.Core/Native/.");
				}

				// Fallback: let the runtime resolve the bare name. This covers a single-file
				// publish (where there is no file to stat) and a system-installed copy.
				IntPtr handle;
				if ( NativeLibrary.TryLoad(BundledLibraryName, typeof(Oodle).Assembly,
						DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.ApplicationDirectory
							| DllImportSearchPath.System32,
						out handle) && handle != IntPtr.Zero ) {
					// No resolved path to report in this case, so say how it was found.
					if ( BindHandle(handle, $"{decorated} (resolved by the loader)") ) {
						SearchReport = Describe($"bound {LibraryPath}", report);
						return true;
					}
					report.AppendLine($"Oodle: loaded {BundledLibraryName} but it exports neither " +
						"OodleLZ_Decompress nor Ooz_Decompress.");
				}

				SearchReport = report.ToString().TrimEnd();
				Log(SearchReport);
				Log(NotFoundAdvice);
				return false;
			}
		}

		/// <summary>
		/// Lead with the outcome, and only mention earlier failures if there were any — a success
		/// report should not open with the line about something that did not work.
		/// </summary>
		private static string Describe(string outcome, StringBuilder earlierFailures) {
			string prior = earlierFailures.ToString().TrimEnd();
			return prior.Length == 0 ? $"Oodle: {outcome}" : $"Oodle: {outcome} (after: {prior})";
		}

		/// <summary> Bind one specific library file, replacing any current binding. </summary>
		public static bool TryLoadFrom(string libraryPath) {
			if ( string.IsNullOrWhiteSpace(libraryPath) ) return false;
			lock ( sync ) {
				Unload();
				bool ok = TryBindPath(libraryPath);
				SearchReport = ok
					? $"Oodle: bound explicitly from {libraryPath}"
					: $"Oodle: could not load \"{libraryPath}\". Check it exists, is 64-bit, and exports " +
						"OodleLZ_Decompress or Ooz_Decompress.";
				if ( !ok ) Log(SearchReport);
				return ok;
			}
		}

		/// <summary>
		/// Drop the binding, so a later call rebinds (possibly to a different library).
		///
		/// The native module is deliberately left mapped: a decode in flight on another thread
		/// holds a function pointer into it, and unmapping under that thread would crash the
		/// process rather than raise anything catchable. Leaking one small DLL for the lifetime
		/// of the process is the cheaper trade.
		/// </summary>
		public static void Unload() {
			lock ( sync ) {
				oodleDecompress = null;
				oozDecompress = null;
				LibraryPath = null;
			}
		}

		private static bool TryBindPath(string nameOrPath) {
			IntPtr handle;
			if ( !NativeLibrary.TryLoad(nameOrPath, out handle) || handle == IntPtr.Zero ) return false;
			return BindHandle(handle, nameOrPath);
		}

		/// <summary>
		/// Look for a decode entry point in an already-loaded module and bind it. Accepts either
		/// ooz's <c>Ooz_Decompress</c> or Oodle's own <c>OodleLZ_Decompress</c>, so an oo2core DLL
		/// still works as an override. Frees the module if it is neither.
		/// </summary>
		private static bool BindHandle(IntPtr handle, string describedAs) {
			IntPtr export;
			if ( NativeLibrary.TryGetExport(handle, "OodleLZ_Decompress", out export) && export != IntPtr.Zero ) {
				oodleDecompress = Marshal.GetDelegateForFunctionPointer<OodleLZDecompressFn>(export);
				library = handle;
				LibraryPath = describedAs;
				Log($"Oodle: bound OodleLZ_Decompress from {describedAs}");
				return true;
			}
			if ( NativeLibrary.TryGetExport(handle, "Ooz_Decompress", out export) && export != IntPtr.Zero ) {
				oozDecompress = Marshal.GetDelegateForFunctionPointer<OozDecompressFn>(export);
				library = handle;
				LibraryPath = describedAs;
				Log($"Oodle: bound Ooz_Decompress from {describedAs}");
				return true;
			}
			// Loaded, but not a decompressor we know.
			try {
				NativeLibrary.Free(handle);
			} catch ( Exception ) { }
			return false;
		}

		/// <summary>
		/// Decompress one Oodle block.
		///
		/// <paramref name="destination"/> must have at least
		/// <paramref name="decompressedSize"/> + <see cref="SafeMargin"/> bytes available from
		/// <paramref name="destinationOffset"/>, because Oodle overwrites past the end of the
		/// decoded data. Returns the number of bytes actually decoded.
		/// </summary>
		public static int Decompress(byte[] source, int sourceOffset, int compressedSize,
				byte[] destination, int destinationOffset, int decompressedSize) {

			if ( source == null ) throw new ArgumentNullException(nameof(source));
			if ( destination == null ) throw new ArgumentNullException(nameof(destination));
			if ( sourceOffset < 0 || compressedSize < 0 || compressedSize > source.Length - sourceOffset )
				throw new ArgumentOutOfRangeException(nameof(compressedSize));
			if ( destinationOffset < 0 || decompressedSize < 0 )
				throw new ArgumentOutOfRangeException(nameof(decompressedSize));
			if ( decompressedSize + SafeMargin > destination.Length - destinationOffset )
				throw new ArgumentException(
					$"Oodle needs {SafeMargin} bytes of slack past the {decompressedSize} decoded bytes; " +
					$"the destination only has {destination.Length - destinationOffset - decompressedSize}.",
					nameof(destination));

			// Snapshot the binding under the lock and call through the locals, so a concurrent
			// Unload/TryLoadFrom cannot null the field out from under this call.
			OodleLZDecompressFn oodle;
			OozDecompressFn ooz;
			lock ( sync ) {
				if ( !IsAvailable && !TryInitialise() )
					throw new InvalidOperationException(NotFoundAdvice + "\n\n" + SearchReport);
				oodle = oodleDecompress;
				ooz = oozDecompress;
			}

			var srcPin = GCHandle.Alloc(source, GCHandleType.Pinned);
			var dstPin = GCHandle.Alloc(destination, GCHandleType.Pinned);
			try {
				IntPtr src = srcPin.AddrOfPinnedObject() + sourceOffset;
				IntPtr dst = dstPin.AddrOfPinnedObject() + destinationOffset;
				long written;
				if ( oodle != null ) {
					written = oodle(
						src, (IntPtr)compressedSize, dst, (IntPtr)decompressedSize,
						FuzzSafeYes, CheckCrcNo, VerbosityNone,
						IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
						ThreadPhaseAll);
				} else {
					written = ooz(src, compressedSize, dst, decompressedSize);
				}
				if ( written != decompressedSize )
					throw new InvalidDataException(
						$"Oodle decoded {written} bytes, expected {decompressedSize} " +
						$"(from {compressedSize} compressed bytes via {LibraryPath}).");
				return (int)written;
			} finally {
				srcPin.Free();
				dstPin.Free();
			}
		}
	}
}
