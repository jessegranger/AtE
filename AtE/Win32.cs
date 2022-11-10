using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AtE {
	public static class Win32 {

		[DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
		[DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		[DllImport("user32.dll")] public static extern short GetAsyncKeyState(Keys key);
		[DllImport("user32.dll")] public static extern bool GetCursorPos(out Point lpPoint);
		[StructLayout(LayoutKind.Sequential)] public struct Margins {
			public int Left, Right, Top, Bottom;
		}
		[DllImport("dwmapi.dll")] public static extern IntPtr DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins pMarInset);
		[DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
		public static readonly int GWL_STYLE = -16;
		public static readonly int GWL_EXSTYLE = -20;

		public const int WS_EX_LAYERED = 0x80000;
		public const int WS_EX_TRANSPARENT = 0x20;
		public const int WS_EX_TOPMOST = 0x00000008;
		public const int WS_VISIBLE = 0x10000000;
	

		[DllImport("user32.dll", SetLastError = true)] public static extern UInt32 SendInput(UInt32 numberOfInputs, INPUT[] inputs, Int32 sizeOfInputStructure);
		[DllImport("user32.dll")] public static extern UInt32 MapVirtualKey(UInt32 uCode, UInt32 uMapType);

		[StructLayout(LayoutKind.Explicit)] public struct MOUSEKBHW_UNION {
			[FieldOffset(0)] public MOUSEINPUT Mouse;
			[FieldOffset(0)] public KEYBDINPUT Keyboard;
			[FieldOffset(0)] public HARDWAREINPUT HW;
		}
		public struct MOUSEINPUT {
			public Int32 X;
			public Int32 Y;
			public UInt32 MouseData;
			public MouseFlag Flags;
			public UInt32 Time;
			public IntPtr ExtraInfo;
		}
		[Flags] public enum MouseFlag : UInt32 {
			Move = 0x0001,
			LeftDown = 0x0002,
			LeftUp = 0x0004,
			RightDown = 0x0008,
			RightUp = 0x0010,
			MiddleDown = 0x0020,
			MiddleUp = 0x0040,
			XDown = 0x0080,
			XUp = 0x0100,
			VerticalWheel = 0x0800,
			HorizontalWheel = 0x1000,
			VirtualDesk = 0x4000, // "absolute" motion relative to virtual desktop
			Absolute = 0x8000, // "absolute" relative to a physical screen
		}
		public struct KEYBDINPUT {
			public UInt16 KeyCode;
			public UInt16 ScanCode;
			public KeyboardFlag Flags;
			public UInt32 Time;
			public IntPtr ExtraInfo;
		}
		[Flags] public enum KeyboardFlag : UInt32 {
			ExtendedKey = 0x0001,
			KeyUp = 0x0002,
			Unicode = 0x0004,
			ScanCode = 0x0008
		}
		public struct HARDWAREINPUT {
			public UInt32 Msg;
			public UInt16 ParamL;
			public UInt16 ParamH;
		}
		public struct INPUT {
			public InputType Type;
			public MOUSEKBHW_UNION Data;
		}
		public enum InputType : UInt32 {
			Mouse = 0,
			Keyboard = 1,
			Hardware = 2
		}

		// this is a bare-bones wrapper around all things SendInput()
		// built in a hurry because I needed to not have the real InputSimulator as a dependency
		// sends one or more input events, returns the number successful
		public static uint SendInput(params INPUT[] inputs) => SendInput((UInt32)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

		public static INPUT INPUT_Mouse(MouseFlag button, uint mouseData = 0) {
			var msg = new INPUT { Type = InputType.Mouse };
			msg.Data.Mouse.Flags = button;
			msg.Data.Mouse.MouseData = mouseData;
			return msg;
		}

		public static INPUT INPUT_KeyDown(Keys key) {
			int vkey = (int)(key & Keys.KeyCode);
			var msg = new INPUT { Type = InputType.Keyboard };
			msg.Data.Keyboard.KeyCode = (UInt16)vkey;
			msg.Data.Keyboard.ScanCode = (UInt16)(MapVirtualKey((UInt32)vkey, 0) & 0xFFU);
			msg.Data.Keyboard.Flags = 0;
			msg.Data.Keyboard.Time = 0;
			msg.Data.Keyboard.ExtraInfo = IntPtr.Zero;
			return msg;
		}

		public static INPUT INPUT_KeyUp(Keys key) {
			int vkey = (int)(key & Keys.KeyCode);
			var msg = new INPUT { Type = InputType.Keyboard };
			msg.Data.Keyboard.KeyCode = (UInt16)vkey;
			msg.Data.Keyboard.ScanCode = (UInt16)(MapVirtualKey((UInt32)vkey, 0) & 0xFFU);
			msg.Data.Keyboard.Flags = KeyboardFlag.KeyUp;
			msg.Data.Keyboard.Time = 0;
			msg.Data.Keyboard.ExtraInfo = IntPtr.Zero;
			return msg;
		}

		private static Vector2 WindowsApiNormalize(Rectangle window, Vector2 pos) {
			float X = pos.X;
			float Y = pos.Y;
			if ( X > window.Width || X < 0 || Y > window.Height || Y < 0 ) {
				return Vector2.Zero;
			}
			return new Vector2(
					(window.Left + X) * 65535 / window.Width,
					(window.Top + Y) * 65535 / window.Height);
		}

		public static INPUT INPUT_MouseMove(Vector2 pos) {
			// x,y and in window coordinates, we need to shift them and normalize to the windows pattern
			// for this part of the windows api, mouse events happen on a 65535 x 65535 grid
			// a bit hard to sort out how it all works on a complex virtual desktop
			Rectangle bounds = Screen.PrimaryScreen.Bounds;
			pos = WindowsApiNormalize(bounds, pos);
			var msg = new INPUT { Type = InputType.Mouse };
			msg.Data.Mouse.Flags = MouseFlag.Move | MouseFlag.Absolute;
			msg.Data.Mouse.X = (int)Math.Round(pos.X);
			msg.Data.Mouse.Y = (int)Math.Round(pos.Y);
			return msg;
		}

		// public static bool IsKeyDown(Keys key) => (GetAsyncKeyState(key) & 0x8000) == 0x8000;
		public static bool IsKeyDown(Keys key) => GetAsyncKeyState(key) < 0;
		/*  "If the most significant bit is set, the key is down, and if the least significant bit is set, the key was pressed after the previous call to GetAsyncKeyState." */


		[StructLayout(LayoutKind.Sequential)]
		public struct RECT {
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;
			public int Width => Right - Left;
			public int Height => Bottom - Top;
		}
		[DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
		[DllImport("user32.dll", SetLastError = true)] public static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
		[DllImport("user32.dll", SetLastError = true)] public static extern bool ScreenToClient(IntPtr hWnd, ref Point point);
		[DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetForegroundWindow();
		[DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetActiveWindow();


	}
}
