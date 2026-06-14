namespace AtE {

	/// <summary>
	/// The transitional composition point for the overlay-window seam, mirroring
	/// <see cref="Input"/> and <see cref="Rendering"/>. The <c>Overlay</c> orchestrator, the
	/// renderer, and the foreground check reach the window only through <see cref="Window"/>.
	///
	/// (Named <c>Windowing</c> rather than <c>Window</c> to avoid shadowing other <c>Window</c>
	/// types and to read consistently with <see cref="Rendering"/>.)
	///
	/// Defaults to the Windows implementation; the future IPlatform composition root
	/// (PORTING.md step 6) will pick the backend at startup instead.
	/// </summary>
	public static class Windowing {
		public static IOverlayWindow Window = new WindowsOverlayWindow();
	}
}
