using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AtE {
	public static class Win32 {
		[DllImport("user32.dll")] public static extern bool GetCursorPos(out Point lpPoint);
		[StructLayout(LayoutKind.Sequential)] public struct Margins {
			public int Left, Right, Top, Bottom;
		}
		[DllImport("dwmapi.dll")] public static extern IntPtr DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins pMarInset);
		[DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
		private static readonly int GWL_STYLE = -16;
		private static readonly int GWL_EXSTYLE = -20;

		private const int WS_EX_LAYERED = 0x80000;
		private const int WS_EX_TRANSPARENT = 0x20;
		private const int WS_EX_TOPMOST = 0x00000008;
		private const int WS_VISIBLE = 0x10000000;
		public static void SetTransparent(IntPtr handle) {
			SetWindowLong(handle, GWL_STYLE, new IntPtr(WS_VISIBLE));
			SetWindowLong(handle, GWL_EXSTYLE, new IntPtr(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST));
		}

		public static void SetNoTransparent(IntPtr handle) {
			SetWindowLong(handle, GWL_STYLE, new IntPtr(WS_VISIBLE));
			SetWindowLong(handle, GWL_EXSTYLE, new IntPtr(WS_EX_LAYERED | WS_EX_TOPMOST));
		}

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
	}
}
