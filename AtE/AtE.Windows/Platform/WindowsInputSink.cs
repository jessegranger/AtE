using System;
using System.Numerics;

namespace AtE {

	/// <summary>
	/// The Windows implementation of <see cref="IInputSink"/>. This is the only place in
	/// the input-sending path that touches the OS.
	///
	/// Keys are posted straight to the game's window (WM_KEYDOWN / WM_KEYUP), so they work while
	/// the game is in the background, and reach the game within one frame (measured on 3.29.3:
	/// median 6ms, worst 17ms, from posting to the effect showing in game memory).
	///
	/// The mouse can't work that way: the game ignores posted mouse messages, and stops tracking
	/// the cursor while it does not have focus. So the mouse, and the modifier keys (which the game
	/// reads from the real keyboard state when it handles a click, eg Ctrl+click), go through SendInput,
	/// which only reaches the game while it has focus.
	/// </summary>
	public sealed class WindowsInputSink : IInputSink {

		public void MouseMove(Vector2 screenPos) =>
			Win32.SendInput(Win32.INPUT_MouseMove(screenPos));

		public void MouseButton(MouseBtn btn, bool down) {
			switch ( btn ) {
				case MouseBtn.Left:
					Win32.SendInput(Win32.INPUT_Mouse(down ? Win32.MouseFlag.LeftDown : Win32.MouseFlag.LeftUp));
					break;
				case MouseBtn.Right:
					Win32.SendInput(Win32.INPUT_Mouse(down ? Win32.MouseFlag.RightDown : Win32.MouseFlag.RightUp));
					break;
				case MouseBtn.Middle:
					Win32.SendInput(Win32.INPUT_Mouse(down ? Win32.MouseFlag.MiddleDown : Win32.MouseFlag.MiddleUp));
					break;
				case MouseBtn.X1:
					Win32.SendInput(Win32.INPUT_Mouse(down ? Win32.MouseFlag.XDown : Win32.MouseFlag.XUp, 1));
					break;
				case MouseBtn.X2:
					Win32.SendInput(Win32.INPUT_Mouse(down ? Win32.MouseFlag.XDown : Win32.MouseFlag.XUp, 2));
					break;
			}
		}

		public void KeyDown(VKey key) {
			if ( CanSendInBackground(key) ) PostKey(key, Win32.WM_KEYDOWN);
			else Win32.SendInput(Win32.INPUT_KeyDown(key));
		}

		public void KeyUp(VKey key) {
			if ( CanSendInBackground(key) ) PostKey(key, Win32.WM_KEYUP);
			else Win32.SendInput(Win32.INPUT_KeyUp(key));
		}

		public bool CanSendInBackground(VKey key) => !IsModifier(key) && GameWindow != IntPtr.Zero;

		private static IntPtr GameWindow => (Platform.Current.Memory as WindowsProcessMemory)?.Target?.MainWindowHandle ?? IntPtr.Zero;

		private static bool IsModifier(VKey key) {
			switch ( key & VKey.KeyCode ) {
				case VKey.ShiftKey: case VKey.LShiftKey: case VKey.RShiftKey:
				case VKey.ControlKey: case VKey.LControlKey: case VKey.RControlKey:
				case VKey.Menu: case VKey.LMenu: case VKey.RMenu:
				case VKey.LWin: case VKey.RWin:
					return true;
				default:
					return false;
			}
		}

		// keys whose scan codes come with the E0 prefix, flagged by bit 24 of the message's lParam
		private static bool IsExtended(VKey key) {
			switch ( key & VKey.KeyCode ) {
				case VKey.Insert: case VKey.Delete: case VKey.Home: case VKey.End:
				case VKey.PageUp: case VKey.PageDown:
				case VKey.Left: case VKey.Right: case VKey.Up: case VKey.Down:
				case VKey.NumLock: case VKey.Divide: case VKey.Snapshot:
					return true;
				default:
					return false;
			}
		}

		private static void PostKey(VKey key, uint message) {
			uint vk = (uint)(key & VKey.KeyCode);
			uint scan = Win32.MapVirtualKey(vk, 0) & 0xFF;
			// lParam: repeat count 1, scan code, extended flag; key up also sets "was down" (bit 30) and "being released" (bit 31)
			uint lParam = 1u | (scan << 16) | (IsExtended(key) ? 1u << 24 : 0u);
			if ( message == Win32.WM_KEYUP ) lParam |= (1u << 30) | (1u << 31);
			Win32.PostMessage(GameWindow, message, (IntPtr)vk, (IntPtr)lParam);
		}
	}
}
