using System;

namespace AtE {

	/// <summary>
	/// The transparent, click-through, topmost overlay window we draw the game's HUD onto.
	/// Wraps the OS windowing tricks (on Windows: a borderless <c>OverlayForm</c> with a DWM
	/// "sheet of glass" frame, the <c>WS_EX_LAYERED|TRANSPARENT|TOPMOST</c> styles, and the
	/// message loop) so the renderer and the rest of the app only ever see a native
	/// <see cref="Handle"/>, a geometry, a focus flag, and a resize event.
	///
	/// Windows impl: <c>WindowsOverlayWindow</c> (wrapping <c>OverlayForm</c> + DWM +
	/// <c>SetWindowLong</c>). Linux impl (TODO): an X11 override-redirect / <c>_NET_WM_STATE_ABOVE</c>
	/// ARGB window with a compositor for transparency, click-through via the XShape input region.
	/// This is the seam most likely to need iteration on Linux (see PORTING.md).
	///
	/// Deviations from the PORTING.md sketch (kept deliberate, mirroring how <see cref="IRenderer"/>
	/// deviated):
	///  - <c>TargetClientRect</c> is dropped. Today nothing tracks the <em>game</em> window through
	///    this seam; the overlay sizes itself and is repositioned over the game by
	///    <c>PoEMemory</c> (which reads the game rect via <see cref="IProcessMemory"/>) calling
	///    <see cref="SetBounds"/>. Callers (HotKey, the bottom-left text helper) only ever need the
	///    overlay's own geometry, so it is exposed as plain ints — no <c>System.Drawing.Rectangle</c>
	///    in the platform-neutral core.
	///  - <c>SetClickThrough</c> takes the place of the sketch's name and is the one style toggled at
	///    runtime (ImGui flips it as the mouse enters/leaves widgets). <see cref="SetTransparent"/>
	///    (per-pixel alpha) and <see cref="SetTopmost"/> are applied once at create time.
	///  - <c>PumpEvents</c> became <see cref="RunMessageLoop"/>: the existing Windows loop is a
	///    blocking pump that also invokes a per-frame callback, so the seam takes that callback
	///    rather than expecting the caller to spin its own loop.
	///  - <see cref="HasFocus"/> and <see cref="Close"/>/<see cref="IsOpen"/> are surfaced here because
	///    they are pure window concerns the <c>Overlay</c> orchestrator and <see cref="IInputSource"/>
	///    foreground check were previously reaching into the concrete form for.
	/// </summary>
	public interface IOverlayWindow {

		/// <summary>
		/// Create the window (borderless, hidden) with the given title and initial size, force its
		/// native handle into existence so the renderer can bind to it, and arrange the load-time
		/// transparency setup. The window is not shown until <see cref="RunMessageLoop"/>.
		/// </summary>
		void Create(string title, int width, int height);

		/// <summary>The native window handle the renderer presents to (HWND on Windows).</summary>
		IntPtr Handle { get; }

		/// <summary>
		/// Toggle click-through (Windows: the <c>WS_EX_TRANSPARENT</c> bit). When enabled, mouse and
		/// keyboard events pass through to the game; ImGui disables it while it wants to capture input.
		/// </summary>
		void SetClickThrough(bool enabled);

		/// <summary>Keep the window above all others (Windows: <c>WS_EX_TOPMOST</c>).</summary>
		void SetTopmost(bool enabled);

		/// <summary>
		/// Make the window per-pixel transparent (Windows: a negative-margin
		/// <c>DwmExtendFrameIntoClientArea</c> "sheet of glass"), so only what the renderer draws is
		/// visible.
		/// </summary>
		void SetTransparent(bool enabled);

		int Width { get; }
		int Height { get; }
		int Left { get; }
		int Top { get; }

		/// <summary>Move/resize the overlay in screen coordinates (used to track the game window).</summary>
		void SetBounds(int left, int top, int width, int height);

		/// <summary>True while the overlay window itself is the foreground/active window.</summary>
		bool HasFocus { get; }

		/// <summary>False once the window has been closed or disposed (used as a liveness check).</summary>
		bool IsOpen { get; }

		/// <summary>Raised when the window is resized, carrying the new client width and height.</summary>
		event Action<int, int> Resized;

		/// <summary>
		/// Show the window and run the blocking platform message loop, invoking
		/// <paramref name="onFrame"/> once per iteration to render a frame. Returns when the window
		/// closes. (Replaces the WinForms/SharpDX message pump.)
		/// </summary>
		void RunMessageLoop(Action onFrame);

		/// <summary>Close the window and end the message loop.</summary>
		void Close();
	}
}
