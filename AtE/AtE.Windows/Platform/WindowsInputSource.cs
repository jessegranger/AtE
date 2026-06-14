using System.Drawing;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Windows implementation of <see cref="IInputSource"/>: GetAsyncKeyState for hotkey
	/// state, GetCursorPos for the pointer, and GetForegroundWindow for the focus check (all
	/// via <see cref="Win32"/>).
	/// </summary>
	public sealed class WindowsInputSource : IInputSource {

		public bool IsKeyDown(VKey key) => Win32.IsKeyDown(key);

		public Vector2 GetCursorPos() =>
			Win32.GetCursorPos(out Point p) ? new Vector2(p.X, p.Y) : Vector2.Zero;

		public bool IsTargetForeground() {
			// The game (PoE) window, or our own overlay, being frontmost both count as
			// "the target has focus" for input purposes.
			if ( Overlay.HasFocus ) {
				return true;
			}
			var target = PoEMemory.Target;
			return IsValid(target) && target.MainWindowHandle == Win32.GetForegroundWindow();
		}
	}
}
