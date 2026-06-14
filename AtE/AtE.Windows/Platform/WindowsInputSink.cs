using System.Numerics;

namespace AtE {

	/// <summary>
	/// The Windows implementation of <see cref="IInputSink"/>: builds INPUT records and
	/// pushes them through SendInput (via <see cref="Win32"/>). This is the only place in
	/// the input-sending path that touches the OS.
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

		public void KeyDown(VKey key) => Win32.SendInput(Win32.INPUT_KeyDown(key));

		public void KeyUp(VKey key) => Win32.SendInput(Win32.INPUT_KeyUp(key));
	}
}
