using System.Numerics;

namespace AtE {

	/// <summary>
	/// The polling side of input: hotkey state, cursor position, and whether the target game
	/// window currently has the foreground. Wraps GetAsyncKeyState / GetCursorPos
	/// / GetForegroundWindow so <c>HotKey</c> and the key-polling plugins never touch the OS.
	///
	/// Windows impl: <c>WindowsInputSource</c>. Linux impl (TODO): X11
	/// <c>XQueryKeymap</c>/<c>XQueryPointer</c>, or evdev, with <c>_NET_ACTIVE_WINDOW</c> for
	/// the foreground check.
	/// </summary>
	public interface IInputSource {

		/// <summary>True while the given key is held down right now.</summary>
		bool IsKeyDown(VKey key);

		/// <summary>The cursor position in screen coordinates.</summary>
		Vector2 GetCursorPos();

		/// <summary>
		/// True when the target game window itself is the active/foreground window. Overlay focus
		/// is intentionally not folded in — callers that also care about the overlay being focused
		/// combine it in explicitly (e.g. <c>IsTargetForeground() || Overlay.HasFocus</c>).
		/// </summary>
		bool IsTargetForeground();
	}
}
