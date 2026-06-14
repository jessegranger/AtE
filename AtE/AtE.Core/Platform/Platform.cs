using System;

namespace AtE {

	/// <summary>
	/// The process-wide holder for the selected <see cref="IPlatform"/>. <c>Program.Main</c>
	/// (per platform) calls <see cref="Initialise"/> exactly once at startup with the backend
	/// for the current OS; everything else reads <see cref="Current"/>.
	///
	/// Living in the neutral <c>AtE.Core</c> assembly means both the Windows and Linux hosts
	/// — and the portable core, once it is extracted here — share one composition root.
	/// </summary>
	public static class Platform {

		private static IPlatform _current;

		/// <summary>The platform backend chosen at startup. Throws if read before <see cref="Initialise"/>.</summary>
		public static IPlatform Current =>
			_current ?? throw new InvalidOperationException(
				"Platform.Current read before Platform.Initialise(...) was called at startup.");

		/// <summary>True once a backend has been selected.</summary>
		public static bool IsInitialised => _current != null;

		/// <summary>Select the platform backend. Call once, at the top of Main.</summary>
		public static void Initialise(IPlatform platform) {
			if ( platform == null ) throw new ArgumentNullException(nameof(platform));
			if ( _current != null ) throw new InvalidOperationException("Platform already initialised.");
			_current = platform;
		}
	}
}
