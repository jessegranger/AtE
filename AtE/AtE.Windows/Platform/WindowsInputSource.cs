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
			// True when the GAME (PoE) window itself is frontmost. Overlay focus is a separate
			// concern that callers combine in explicitly (|| Overlay.HasFocus), so it is not
			// folded in here. Source the game window handle straight from the Windows memory
			// backend (a within-assembly shortcut between Windows-only backends); the portable
			// core no longer exposes the target Process.
			var target = (Platform.Current.Memory as WindowsProcessMemory)?.Target;
			return IsValid(target) && target.MainWindowHandle == Win32.GetForegroundWindow();
		}
	}
}
