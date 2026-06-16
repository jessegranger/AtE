using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IInputSink"/> (synthetic input into the game).
	///
	/// Mirrors <c>WindowsInputSink</c> (the only place in the input-sending path that touches the
	/// OS) using the <c>uinput</c> target from PORTING.md's cheat-sheet. A single virtual device is
	/// created lazily on first use exposing keyboard keys, the five mouse buttons, and absolute
	/// X/Y axes; events are emitted by writing <c>struct input_event</c> records to the device fd,
	/// each batch terminated with a <c>SYN_REPORT</c>. uinput was chosen over XTEST so the backend
	/// works under both X11 and Wayland (XTEST needs an X server).
	///
	/// Positioning is absolute: the interface hands us a screen-space point, so the device's ABS
	/// range is set to the screen resolution (read once via libX11, so device units == pixels). A
	/// device exposing ABS_X/ABS_Y plus BTN_LEFT is treated by the input stack as an absolute
	/// pointer (the same shape as a QEMU/VirtualBox tablet), which maps the axis range across the
	/// whole desktop. When no X server is reachable (pure Wayland without XWayland) the resolution
	/// falls back to a default and positioning is approximate.
	///
	/// The <see cref="VKey"/> codes (Windows VK numbering) are translated to evdev <c>KEY_*</c>
	/// codes here, the send-side counterpart to <see cref="LinuxInputSource"/>'s VKey -> KeySym map.
	///
	/// Permissions caveat: opening <c>/dev/uinput</c> needs root or membership in the <c>input</c>
	/// group (plus a udev rule granting access). If the open fails the sink disables itself and
	/// every method no-ops, matching the graceful degradation of the other Linux backends.
	///
	/// Note: the ToS/anti-cheat implications of synthetic input are platform-independent
	/// (see PORTING.md "Biggest risks").
	/// </summary>
	public sealed class LinuxInputSink : IInputSink, IDisposable {

		// ---- libc interop -------------------------------------------------------------------

		[DllImport("libc", SetLastError = true)]
		private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

		[DllImport("libc", SetLastError = true)]
		private static extern int close(int fd);

		[DllImport("libc", SetLastError = true)]
		private static extern unsafe long write(int fd, void* buf, UIntPtr count);

		// ioctl is variadic in C; the uinput requests we issue take either a single `int` by value
		// (UI_SET_*BIT) or no argument (UI_DEV_CREATE/DESTROY, where the extra int is ignored).
		[DllImport("libc", SetLastError = true)]
		private static extern int ioctl(int fd, UIntPtr request, int arg);

		private const int O_WRONLY = 0x001;
		private const int O_NONBLOCK = 0x800;

		// ---- libX11 interop (screen geometry only) ------------------------------------------

		private const string X11 = "libX11.so.6";

		[DllImport(X11)] private static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPStr)] string display);
		[DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
		[DllImport(X11)] private static extern int XDefaultScreen(IntPtr display);
		[DllImport(X11)] private static extern int XDisplayWidth(IntPtr display, int screen);
		[DllImport(X11)] private static extern int XDisplayHeight(IntPtr display, int screen);

		// ---- uinput / evdev constants -------------------------------------------------------

		// Event types (linux/input-event-codes.h).
		private const ushort EV_SYN = 0x00;
		private const ushort EV_KEY = 0x01;
		private const ushort EV_ABS = 0x03;
		private const ushort SYN_REPORT = 0x00;

		// Absolute axes.
		private const ushort ABS_X = 0x00;
		private const ushort ABS_Y = 0x01;

		// Mouse buttons.
		private const ushort BTN_LEFT = 0x110;
		private const ushort BTN_RIGHT = 0x111;
		private const ushort BTN_MIDDLE = 0x112;
		private const ushort BTN_SIDE = 0x113;   // X1 (back)
		private const ushort BTN_EXTRA = 0x114;  // X2 (forward)

		// uinput ioctls (linux/uinput.h), pre-computed for the asm-generic encoding used on x86_64.
		// _IOW('U', n, T) = (1<<30) | (sizeof(T)<<16) | ('U'<<8) | n ; _IO('U', n) = ('U'<<8) | n.
		private const ulong UI_SET_EVBIT = 0x40045564;   // _IOW('U', 100, int)
		private const ulong UI_SET_KEYBIT = 0x40045565;  // _IOW('U', 101, int)
		private const ulong UI_SET_ABSBIT = 0x40045567;  // _IOW('U', 103, int)
		private const ulong UI_DEV_CREATE = 0x5501;      // _IO('U', 1)
		private const ulong UI_DEV_DESTROY = 0x5502;     // _IO('U', 2)

		private const int ABS_CNT = 64; // ABS_MAX + 1
		private const int UINPUT_MAX_NAME_SIZE = 80;

		// struct input_event on 64-bit Linux: struct timeval (two 8-byte longs) + type/code/value.
		[StructLayout(LayoutKind.Sequential)]
		private struct input_event {
			public long tv_sec;
			public long tv_usec;
			public ushort type;
			public ushort code;
			public int value;
		}

		// ---- device state -------------------------------------------------------------------

		private readonly object gate = new object();
		private int fd = -1;        // /dev/uinput fd; <0 means "no device" (everything no-ops)
		private bool createTried;   // only attempt creation once
		private int screenW = 1920; // ABS range (== pixels after the 1:1 axis mapping)
		private int screenH = 1080;

		// ---- IInputSink ---------------------------------------------------------------------

		public void MouseMove(Vector2 screenPos) {
			lock ( gate ) {
				if ( !EnsureDevice() ) return;
				int x = Clamp((int)screenPos.X, 0, screenW - 1);
				int y = Clamp((int)screenPos.Y, 0, screenH - 1);
				Emit(EV_ABS, ABS_X, x);
				Emit(EV_ABS, ABS_Y, y);
				Sync();
			}
		}

		public void MouseButton(MouseBtn btn, bool down) {
			ushort code;
			switch ( btn ) {
				case MouseBtn.Left: code = BTN_LEFT; break;
				case MouseBtn.Right: code = BTN_RIGHT; break;
				case MouseBtn.Middle: code = BTN_MIDDLE; break;
				case MouseBtn.X1: code = BTN_SIDE; break;
				case MouseBtn.X2: code = BTN_EXTRA; break;
				default: return;
			}
			lock ( gate ) {
				if ( !EnsureDevice() ) return;
				Emit(EV_KEY, code, down ? 1 : 0);
				Sync();
			}
		}

		public void KeyDown(VKey key) => SendKey(key, true);

		public void KeyUp(VKey key) => SendKey(key, false);

		private void SendKey(VKey key, bool down) {
			ushort code = VKeyToEvdev(key & VKey.KeyCode);
			if ( code == 0 ) return; // no evdev equivalent for this key
			lock ( gate ) {
				if ( !EnsureDevice() ) return;
				Emit(EV_KEY, code, down ? 1 : 0);
				Sync();
			}
		}

		// ---- device lifecycle ---------------------------------------------------------------

		/// <summary>
		/// Lazily create (once) the uinput virtual device. Returns false when uinput is unavailable
		/// (missing node, no permission), in which case every input call no-ops. Caller holds <see cref="gate"/>.
		/// </summary>
		private bool EnsureDevice() {
			if ( fd >= 0 ) return true;
			if ( createTried ) return false;
			createTried = true;

			ReadScreenSize();

			int d;
			try {
				d = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
			} catch ( DllNotFoundException e ) {
				Log($"LinuxInputSink: libc unavailable: {e.Message}");
				return false;
			}
			if ( d < 0 ) {
				Log("LinuxInputSink: open(/dev/uinput) failed (need root or 'input' group + udev rule). Synthetic input disabled.");
				return false;
			}

			try {
				// Capabilities: keyboard keys + mouse buttons (EV_KEY) and absolute X/Y (EV_ABS).
				Ioctl(d, UI_SET_EVBIT, EV_KEY);
				Ioctl(d, UI_SET_EVBIT, EV_ABS);

				// Enable the standard keyboard keycode range plus the five mouse buttons. Enabling a
				// few unused codes is harmless and keeps the VKey -> KEY_* map free to grow.
				for ( int code = 1; code <= 255; code++ ) {
					Ioctl(d, UI_SET_KEYBIT, code);
				}
				Ioctl(d, UI_SET_KEYBIT, BTN_LEFT);
				Ioctl(d, UI_SET_KEYBIT, BTN_RIGHT);
				Ioctl(d, UI_SET_KEYBIT, BTN_MIDDLE);
				Ioctl(d, UI_SET_KEYBIT, BTN_SIDE);
				Ioctl(d, UI_SET_KEYBIT, BTN_EXTRA);

				Ioctl(d, UI_SET_ABSBIT, ABS_X);
				Ioctl(d, UI_SET_ABSBIT, ABS_Y);

				// Legacy creation path: write a uinput_user_dev (carrying the abs ranges inline) then
				// UI_DEV_CREATE. Simpler than UI_DEV_SETUP + UI_ABS_SETUP and broadly supported.
				if ( !WriteUserDev(d) ) {
					Log("LinuxInputSink: write(uinput_user_dev) failed; aborting device creation.");
					close(d);
					return false;
				}
				if ( ioctl(d, (UIntPtr)UI_DEV_CREATE, 0) < 0 ) {
					Log($"LinuxInputSink: UI_DEV_CREATE failed (errno {Marshal.GetLastWin32Error()}).");
					close(d);
					return false;
				}
			} catch ( Exception e ) {
				Log($"LinuxInputSink: device setup failed: {e.Message}");
				close(d);
				return false;
			}

			fd = d;
			// Give udev a moment to create the device node and the input stack to bind it; events
			// emitted before that lands are silently dropped.
			Thread.Sleep(50);
			Log($"LinuxInputSink: created uinput device ({screenW}x{screenH} absolute range).");
			return true;
		}

		/// <summary>Read the desktop resolution once via libX11 so the ABS range maps 1:1 to pixels.</summary>
		private void ReadScreenSize() {
			IntPtr display = IntPtr.Zero;
			try {
				display = XOpenDisplay(null);
				if ( display == IntPtr.Zero ) {
					Log("LinuxInputSink: XOpenDisplay failed; using default 1920x1080 for absolute range (positioning approximate).");
					return;
				}
				int screen = XDefaultScreen(display);
				int w = XDisplayWidth(display, screen);
				int h = XDisplayHeight(display, screen);
				if ( w > 0 && h > 0 ) {
					screenW = w;
					screenH = h;
				}
			} catch ( DllNotFoundException ) {
				Log("LinuxInputSink: libX11 not available; using default 1920x1080 for absolute range.");
			} finally {
				if ( display != IntPtr.Zero ) XCloseDisplay(display);
			}
		}

		/// <summary>
		/// Build and write the legacy <c>struct uinput_user_dev</c>. Layout (LP64): name[80],
		/// input_id{u16 bustype,vendor,product,version}, u32 ff_effects_max, then four s32[ABS_CNT]
		/// arrays absmax/absmin/absfuzz/absflat. We only populate name, id and absmax/absmin for X/Y.
		/// </summary>
		private bool WriteUserDev(int d) {
			const int nameOff = 0;
			const int idOff = UINPUT_MAX_NAME_SIZE;        // 80
			const int absmaxOff = idOff + 8 + 4;           // after input_id (8) + ff_effects_max (4) = 92
			const int absminOff = absmaxOff + ABS_CNT * 4; // 348
			const int total = absmaxOff + 4 * ABS_CNT * 4; // 92 + 1024 = 1116

			byte[] dev = new byte[total];

			byte[] name = System.Text.Encoding.ASCII.GetBytes("AtE Virtual Input");
			Array.Copy(name, 0, dev, nameOff, Math.Min(name.Length, UINPUT_MAX_NAME_SIZE - 1));

			WriteU16(dev, idOff + 0, 0x03);   // bustype = BUS_USB
			WriteU16(dev, idOff + 2, 0x1209); // vendor  (generic/open-source range)
			WriteU16(dev, idOff + 4, 0xA7E0); // product
			WriteU16(dev, idOff + 6, 0x0001); // version

			WriteI32(dev, absmaxOff + ABS_X * 4, screenW - 1);
			WriteI32(dev, absmaxOff + ABS_Y * 4, screenH - 1);
			WriteI32(dev, absminOff + ABS_X * 4, 0);
			WriteI32(dev, absminOff + ABS_Y * 4, 0);

			unsafe {
				fixed ( byte* p = dev ) {
					return write(d, p, (UIntPtr)(uint)total) == total;
				}
			}
		}

		// ---- event emission -----------------------------------------------------------------

		/// <summary>Write one input_event. Caller holds <see cref="gate"/>. The timestamp is left
		/// zero — the kernel fills it in.</summary>
		private unsafe void Emit(ushort type, ushort code, int value) {
			if ( fd < 0 ) return;
			input_event ev = new input_event { type = type, code = code, value = value };
			write(fd, &ev, (UIntPtr)(uint)sizeof(input_event));
		}

		private void Sync() => Emit(EV_SYN, SYN_REPORT, 0);

		private static void Ioctl(int d, ulong request, int arg) => ioctl(d, (UIntPtr)request, arg);

		private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

		private static void WriteU16(byte[] b, int off, ushort v) {
			b[off] = (byte)v;
			b[off + 1] = (byte)(v >> 8);
		}

		private static void WriteI32(byte[] b, int off, int v) {
			b[off] = (byte)v;
			b[off + 1] = (byte)(v >> 8);
			b[off + 2] = (byte)(v >> 16);
			b[off + 3] = (byte)(v >> 24);
		}

		// ---- cleanup ------------------------------------------------------------------------

		public void Dispose() {
			lock ( gate ) {
				if ( fd >= 0 ) {
					ioctl(fd, (UIntPtr)UI_DEV_DESTROY, 0);
					close(fd);
					fd = -1;
				}
			}
			GC.SuppressFinalize(this);
		}

		~LinuxInputSink() => Dispose();

		// ---- VKey -> evdev KEY_* -------------------------------------------------------------

		/// <summary>
		/// Map a base <see cref="VKey"/> (Windows VK numbering) to the Linux evdev keycode
		/// (<c>KEY_*</c> from input-event-codes.h) for the same physical key. Returns 0 for keys
		/// with no sensible evdev equivalent. This is the send-side mirror of
		/// <see cref="LinuxInputSource"/>'s VKey -> X11 KeySym map.
		/// </summary>
		private static ushort VKeyToEvdev(VKey k) {
			int v = (int)k;

			// Letters A..Z (VK 0x41..0x5A): evdev codes are layout-position based, so use a table.
			if ( v >= (int)VKey.A && v <= (int)VKey.Z ) {
				return LetterKeys[v - (int)VKey.A];
			}
			// Top-row digits 1..9 -> KEY_1..KEY_9 (2..10), 0 -> KEY_0 (11).
			if ( v >= (int)VKey.D1 && v <= (int)VKey.D9 ) {
				return (ushort)(2 + (v - (int)VKey.D1));
			}
			if ( k == VKey.D0 ) return 11; // KEY_0
			// Numeric keypad.
			if ( v >= (int)VKey.NumPad0 && v <= (int)VKey.NumPad9 ) {
				return NumpadKeys[v - (int)VKey.NumPad0];
			}
			// Function keys: F1..F10 = 59..68, F11/F12 = 87/88, F13..F24 = 183..194.
			if ( v >= (int)VKey.F1 && v <= (int)VKey.F10 ) return (ushort)(59 + (v - (int)VKey.F1));
			if ( k == VKey.F11 ) return 87;
			if ( k == VKey.F12 ) return 88;
			if ( v >= (int)VKey.F13 && v <= (int)VKey.F24 ) return (ushort)(183 + (v - (int)VKey.F13));

			switch ( k ) {
				case VKey.Back: return 14;        // KEY_BACKSPACE
				case VKey.Tab: return 15;         // KEY_TAB
				case VKey.Return: return 28;      // KEY_ENTER
				case VKey.Escape: return 1;       // KEY_ESC
				case VKey.Space: return 57;       // KEY_SPACE
				case VKey.Prior: return 104;      // KEY_PAGEUP
				case VKey.Next: return 109;       // KEY_PAGEDOWN
				case VKey.End: return 107;        // KEY_END
				case VKey.Home: return 102;       // KEY_HOME
				case VKey.Left: return 105;       // KEY_LEFT
				case VKey.Up: return 103;         // KEY_UP
				case VKey.Right: return 106;      // KEY_RIGHT
				case VKey.Down: return 108;       // KEY_DOWN
				case VKey.Insert: return 110;     // KEY_INSERT
				case VKey.Delete: return 111;     // KEY_DELETE
				case VKey.Pause: return 119;      // KEY_PAUSE
				case VKey.Snapshot: return 99;    // KEY_SYSRQ (Print Screen)
				case VKey.Capital: return 58;     // KEY_CAPSLOCK
				case VKey.NumLock: return 69;     // KEY_NUMLOCK
				case VKey.Scroll: return 70;      // KEY_SCROLLLOCK

				// Modifiers: generic VK -> left-hand key; explicit L/R map to their side.
				case VKey.ShiftKey:
				case VKey.LShiftKey: return 42;   // KEY_LEFTSHIFT
				case VKey.RShiftKey: return 54;   // KEY_RIGHTSHIFT
				case VKey.ControlKey:
				case VKey.LControlKey: return 29; // KEY_LEFTCTRL
				case VKey.RControlKey: return 97; // KEY_RIGHTCTRL
				case VKey.Menu:
				case VKey.LMenu: return 56;       // KEY_LEFTALT
				case VKey.RMenu: return 100;      // KEY_RIGHTALT
				case VKey.LWin: return 125;       // KEY_LEFTMETA
				case VKey.RWin: return 126;       // KEY_RIGHTMETA
				case VKey.Apps: return 127;       // KEY_COMPOSE (menu)

				// Keypad operators.
				case VKey.Multiply: return 55;    // KEY_KPASTERISK
				case VKey.Add: return 78;         // KEY_KPPLUS
				case VKey.Subtract: return 74;    // KEY_KPMINUS
				case VKey.Decimal: return 83;     // KEY_KPDOT
				case VKey.Divide: return 98;      // KEY_KPSLASH

				// OEM punctuation, assuming a US layout.
				case VKey.Oem1: return 39;        // ';' KEY_SEMICOLON
				case VKey.Oemplus: return 13;     // '=' KEY_EQUAL
				case VKey.Oemcomma: return 51;    // ',' KEY_COMMA
				case VKey.OemMinus: return 12;    // '-' KEY_MINUS
				case VKey.OemPeriod: return 52;   // '.' KEY_DOT
				case VKey.Oem2: return 53;        // '/' KEY_SLASH
				case VKey.Oem3: return 41;        // '`' KEY_GRAVE
				case VKey.Oem4: return 26;        // '[' KEY_LEFTBRACE
				case VKey.Oem5: return 43;        // '\' KEY_BACKSLASH
				case VKey.Oem6: return 27;        // ']' KEY_RIGHTBRACE
				case VKey.Oem7: return 40;        // ''' KEY_APOSTROPHE

				default: return 0;
			}
		}

		// KEY_* codes for A..Z, indexed by (VKey - VKey.A).
		private static readonly ushort[] LetterKeys = {
			30, // A
			48, // B
			46, // C
			32, // D
			18, // E
			33, // F
			34, // G
			35, // H
			23, // I
			36, // J
			37, // K
			38, // L
			50, // M
			49, // N
			24, // O
			25, // P
			16, // Q
			19, // R
			31, // S
			20, // T
			22, // U
			47, // V
			17, // W
			45, // X
			21, // Y
			44, // Z
		};

		// KEY_KP* codes for NumPad0..NumPad9, indexed by (VKey - VKey.NumPad0).
		private static readonly ushort[] NumpadKeys = {
			82, // KP0
			79, // KP1
			80, // KP2
			81, // KP3
			75, // KP4
			76, // KP5
			77, // KP6
			71, // KP7
			72, // KP8
			73, // KP9
		};
	}
}
