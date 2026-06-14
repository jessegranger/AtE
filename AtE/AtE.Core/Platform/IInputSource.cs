using System.Numerics;

namespace AtE {

	/// <summary>
	/// The polling side of input: hotkey state, cursor position, and whether the target
	/// (or our overlay) currently has the foreground. Wraps GetAsyncKeyState / GetCursorPos
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

		/// <summary>True when the game window (or our overlay) is the active/foreground window.</summary>
		bool IsTargetForeground();
	}
}
