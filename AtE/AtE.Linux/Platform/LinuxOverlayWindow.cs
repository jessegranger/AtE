using System;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IOverlayWindow"/> (the transparent, click-through,
	/// topmost overlay) — stub. This is the riskiest seam on Linux (PORTING.md), so it is
	/// filled in last (memory -> input -> render -> window).
	///
	/// Target design (PORTING.md cheat-sheet):
	///  - An X11 override-redirect (or <c>_NET_WM_STATE_ABOVE</c>) window with an ARGB visual,
	///    relying on a running compositor for per-pixel transparency
	///    (<see cref="SetTransparent"/>).
	///  - Click-through (<see cref="SetClickThrough"/>): an empty XShape *input* region so
	///    pointer/keyboard events fall through to the game; ImGui restores the input region
	///    while it wants to capture.
	///  - <see cref="SetTopmost"/>: the <c>_NET_WM_STATE_ABOVE</c> hint (or override-redirect).
	///  - <see cref="HasFocus"/>: compare <c>_NET_ACTIVE_WINDOW</c> against this window.
	///  - <see cref="RunMessageLoop"/>: drive the Xlib/XCB event queue, invoking onFrame once
	///    per iteration. Wayland is harder (the layer-shell protocol, compositor-dependent) —
	///    assume a borderless-windowed game rather than exclusive fullscreen.
	/// </summary>
	public sealed class LinuxOverlayWindow : IOverlayWindow {

		private const string NYI = "LinuxOverlayWindow is not yet implemented (PORTING.md step 6).";

		// Suppress the unused-event warning until the X11 resize handler raises it.
#pragma warning disable CS0067
		/// <summary>Raised with the new client width/height when the window is resized.</summary>
		public event Action<int, int> Resized;
#pragma warning restore CS0067

		public void Create(string title, int width, int height) =>
			throw new NotImplementedException(NYI + " TODO: create an override-redirect ARGB X11 window (hidden) and realise its native handle.");

		public IntPtr Handle =>
			throw new NotImplementedException(NYI + " TODO: return the X11 window XID as the native handle.");

		public void SetClickThrough(bool enabled) =>
			throw new NotImplementedException(NYI + " TODO: toggle the XShape input region (empty = click-through).");

		public void SetTopmost(bool enabled) =>
			throw new NotImplementedException(NYI + " TODO: set/clear _NET_WM_STATE_ABOVE.");

		public void SetTransparent(bool enabled) =>
			throw new NotImplementedException(NYI + " TODO: use an ARGB visual + compositor for per-pixel alpha.");

		public int Width =>
			throw new NotImplementedException(NYI + " TODO: track the window's client width.");

		public int Height =>
			throw new NotImplementedException(NYI + " TODO: track the window's client height.");

		public int Left =>
			throw new NotImplementedException(NYI + " TODO: track the window's screen-space left.");

		public int Top =>
			throw new NotImplementedException(NYI + " TODO: track the window's screen-space top.");

		public void SetBounds(int left, int top, int width, int height) =>
			throw new NotImplementedException(NYI + " TODO: XMoveResizeWindow to track the game window.");

		public bool HasFocus =>
			throw new NotImplementedException(NYI + " TODO: compare _NET_ACTIVE_WINDOW against this window.");

		public bool IsOpen => false;

		public void RunMessageLoop(Action onFrame) =>
			throw new NotImplementedException(NYI + " TODO: show the window and drive the Xlib/XCB event queue, calling onFrame per iteration.");

		public void Close() {
			// No-op until the window exists. (Filled in: destroy the X11 window and end the loop.)
		}
	}
}
