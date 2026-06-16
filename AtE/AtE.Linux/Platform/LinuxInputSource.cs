using System;
using System.Numerics;
using System.Runtime.InteropServices;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IInputSource"/> (the polling side of input).
	///
	/// Design (PORTING.md cheat-sheet):
	///  - <see cref="IsKeyDown"/>: translate the core <see cref="VKey"/> to an X11 KeySym, then to
	///    the live KeyCode with <c>XKeysymToKeycode</c>, and test the bit for that KeyCode in the
	///    256-bit vector returned by <c>XQueryKeymap</c>. The three real mouse buttons are not in
	///    the key vector, so they are read from the button mask of <c>XQueryPointer</c> instead.
	///  - <see cref="GetCursorPos"/>: <c>XQueryPointer</c> against the root window, returning the
	///    root-relative (screen) coordinates.
	///  - <see cref="IsTargetForeground"/>: read the <c>_NET_ACTIVE_WINDOW</c> hint off the root
	///    window and compare that window's <c>_NET_WM_PID</c> against the pid the memory backend
	///    attached to (<see cref="IProcessMemory.Pid"/>). Overlay focus is intentionally *not*
	///    folded in here — callers combine it explicitly, matching the Windows backend.
	///
	/// This opens its own private Xlib connection (separate from the overlay's) and serialises
	/// access to it with a lock, so polling from the HotKey/plugin threads is safe regardless of
	/// whether <c>XInitThreads</c> ran first. Wayland without XWayland has no X server, so every
	/// method degrades gracefully to "nothing pressed / origin / not foreground".
	/// </summary>
	public sealed class LinuxInputSource : IInputSource {

		// ---- libX11 interop -----------------------------------------------------------------

		private const string X11 = "libX11.so.6";

		[DllImport(X11)] private static extern int XInitThreads();
		[DllImport(X11)] private static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPStr)] string display);
		[DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);

		// char keys_return[32] — a 256-bit vector, one bit per KeyCode.
		[DllImport(X11)] private static extern int XQueryKeymap(IntPtr display, [Out] byte[] keys_return);

		// KeyCode (unsigned char) for a KeySym (unsigned long) in the current mapping; 0 if none.
		[DllImport(X11)] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);

		[DllImport(X11)]
		private static extern int XQueryPointer(
			IntPtr display, IntPtr window,
			out IntPtr root_return, out IntPtr child_return,
			out int root_x_return, out int root_y_return,
			out int win_x_return, out int win_y_return,
			out uint mask_return);

		[DllImport(X11)]
		private static extern IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPStr)] string name, int only_if_exists);

		// Atom/format-32 properties come back as an array of C `long` (8 bytes each on LP64),
		// regardless of the on-the-wire 32-bit format — read them with Marshal.ReadInt64.
		[DllImport(X11)]
		private static extern int XGetWindowProperty(
			IntPtr display, IntPtr window, IntPtr property,
			IntPtr long_offset, IntPtr long_length, int delete, IntPtr req_type,
			out IntPtr actual_type_return, out int actual_format_return,
			out IntPtr nitems_return, out IntPtr bytes_after_return,
			out IntPtr prop_return);

		[DllImport(X11)] private static extern int XFree(IntPtr data);

		// X pointer button-state masks (ButtonNMask = 1 << (7 + N)).
		private const uint Button1Mask = 1 << 8;   // left
		private const uint Button2Mask = 1 << 9;   // middle
		private const uint Button3Mask = 1 << 10;  // right

		private const int AnyPropertyType = 0;

		// ---- connection state ---------------------------------------------------------------

		private readonly object gate = new object();
		private IntPtr display;          // our private Xlib connection (lazy)
		private bool displayTried;       // only attempt XOpenDisplay once
		private IntPtr netActiveWindow;  // cached _NET_ACTIVE_WINDOW atom
		private IntPtr netWmPid;         // cached _NET_WM_PID atom

		/// <summary>
		/// Lazily open (once) the private X display. Returns <see cref="IntPtr.Zero"/> when there
		/// is no reachable X server, in which case all queries no-op. Caller must hold <see cref="gate"/>.
		/// </summary>
		private IntPtr Display() {
			if ( display != IntPtr.Zero || displayTried ) return display;
			displayTried = true;
			try {
				XInitThreads(); // idempotent; make our connection safe to share across threads
				display = XOpenDisplay(null);
				if ( display == IntPtr.Zero ) {
					Log("LinuxInputSource: XOpenDisplay failed (no X server / DISPLAY unset). Input polling disabled.");
				} else {
					netActiveWindow = XInternAtom(display, "_NET_ACTIVE_WINDOW", 0);
					netWmPid = XInternAtom(display, "_NET_WM_PID", 0);
				}
			} catch ( DllNotFoundException e ) {
				Log($"LinuxInputSource: libX11 not available: {e.Message}");
				display = IntPtr.Zero;
			}
			return display;
		}

		// ---- IInputSource -------------------------------------------------------------------

		public bool IsKeyDown(VKey key) {
			// Strip any modifier flags packed alongside the base key (Shift/Control/Alt bits).
			VKey baseKey = key & VKey.KeyCode;
			if ( baseKey == VKey.None ) return false;

			lock ( gate ) {
				IntPtr d = Display();
				if ( d == IntPtr.Zero ) return false;

				// Mouse buttons are not part of the key vector — read the pointer button mask.
				switch ( baseKey ) {
					case VKey.LButton: return PointerMaskHeld(d, Button1Mask);
					case VKey.MButton: return PointerMaskHeld(d, Button2Mask);
					case VKey.RButton: return PointerMaskHeld(d, Button3Mask);
				}

				IntPtr keysym = VKeyToKeysym(baseKey);
				if ( keysym == IntPtr.Zero ) return false;

				byte keycode = XKeysymToKeycode(d, keysym);
				if ( keycode == 0 ) return false; // key not on the current physical layout

				byte[] keys = new byte[32];
				if ( XQueryKeymap(d, keys) == 0 ) return false;
				return (keys[keycode >> 3] & (1 << (keycode & 7))) != 0;
			}
		}

		public Vector2 GetCursorPos() {
			lock ( gate ) {
				IntPtr d = Display();
				if ( d == IntPtr.Zero ) return Vector2.Zero;
				IntPtr root = XDefaultRootWindow(d);
				if ( XQueryPointer(d, root, out _, out _, out int rootX, out int rootY, out _, out _, out _) == 0 ) {
					// Pointer is on a different screen of the display; root_x/root_y are still set,
					// but be conservative and report the origin.
					return new Vector2(rootX, rootY);
				}
				return new Vector2(rootX, rootY);
			}
		}

		public bool IsTargetForeground() {
			int targetPid = Platform.Current.Memory.Pid;
			if ( targetPid == 0 ) return false; // not attached to the game yet

			lock ( gate ) {
				IntPtr d = Display();
				if ( d == IntPtr.Zero || netActiveWindow == IntPtr.Zero ) return false;

				IntPtr root = XDefaultRootWindow(d);
				IntPtr active = ReadWindowProperty(d, root, netActiveWindow);
				if ( active == IntPtr.Zero ) return false;

				if ( netWmPid == IntPtr.Zero ) return false;
				IntPtr activePid = ReadWindowProperty(d, active, netWmPid);
				return activePid.ToInt64() == targetPid;
			}
		}

		// ---- helpers ------------------------------------------------------------------------

		/// <summary>True when any of <paramref name="mask"/>'s bits are set in the live pointer mask.</summary>
		private bool PointerMaskHeld(IntPtr d, uint mask) {
			IntPtr root = XDefaultRootWindow(d);
			if ( XQueryPointer(d, root, out _, out _, out _, out _, out _, out _, out uint state) == 0 ) return false;
			return (state & mask) != 0;
		}

		/// <summary>
		/// Read the first element of a format-32 window property (an XID or CARDINAL) as a
		/// pointer-sized value. Returns <see cref="IntPtr.Zero"/> when the property is absent.
		/// </summary>
		private IntPtr ReadWindowProperty(IntPtr d, IntPtr window, IntPtr property) {
			if ( XGetWindowProperty(d, window, property,
					IntPtr.Zero /*offset*/, (IntPtr)1 /*length, in 32-bit words*/, 0 /*delete*/, (IntPtr)AnyPropertyType,
					out _, out int format, out IntPtr nitems, out _, out IntPtr prop) != 0 /*Success==0*/ ) {
				return IntPtr.Zero;
			}
			try {
				if ( prop == IntPtr.Zero || nitems.ToInt64() < 1 || format != 32 ) return IntPtr.Zero;
				// format==32 entries are stored one-per-C-long (8 bytes on LP64).
				return (IntPtr)Marshal.ReadInt64(prop);
			} finally {
				if ( prop != IntPtr.Zero ) XFree(prop);
			}
		}

		/// <summary>
		/// Map a base <see cref="VKey"/> (Windows VK numbering) to the X11 KeySym for the same
		/// key. Returns <see cref="IntPtr.Zero"/> for keys with no sensible X equivalent.
		/// KeySym values are from keysymdef.h; <c>XKeysymToKeycode</c> resolves them to the
		/// current layout's KeyCode.
		/// </summary>
		private static IntPtr VKeyToKeysym(VKey k) {
			// Letters and digits: VK codes already equal their ASCII uppercase / digit values,
			// and the Latin-1 KeySyms coincide with ASCII for these ranges.
			int v = (int)k;
			if ( v >= (int)VKey.A && v <= (int)VKey.Z ) return (IntPtr)(v + 0x20); // -> XK_a..XK_z (0x61..0x7a)
			if ( v >= (int)VKey.D0 && v <= (int)VKey.D9 ) return (IntPtr)v;        // -> XK_0..XK_9 (0x30..0x39)
			if ( v >= (int)VKey.NumPad0 && v <= (int)VKey.NumPad9 )
				return (IntPtr)(0xFFB0 + (v - (int)VKey.NumPad0));                 // XK_KP_0..XK_KP_9
			if ( v >= (int)VKey.F1 && v <= (int)VKey.F24 )
				return (IntPtr)(0xFFBE + (v - (int)VKey.F1));                      // XK_F1..XK_F24

			switch ( k ) {
				case VKey.Back: return (IntPtr)0xFF08;        // XK_BackSpace
				case VKey.Tab: return (IntPtr)0xFF09;         // XK_Tab
				case VKey.Return: return (IntPtr)0xFF0D;      // XK_Return
				case VKey.Escape: return (IntPtr)0xFF1B;      // XK_Escape
				case VKey.Space: return (IntPtr)0x0020;       // XK_space
				case VKey.Prior: return (IntPtr)0xFF55;       // XK_Prior (PageUp)
				case VKey.Next: return (IntPtr)0xFF56;        // XK_Next (PageDown)
				case VKey.End: return (IntPtr)0xFF57;         // XK_End
				case VKey.Home: return (IntPtr)0xFF50;        // XK_Home
				case VKey.Left: return (IntPtr)0xFF51;        // XK_Left
				case VKey.Up: return (IntPtr)0xFF52;          // XK_Up
				case VKey.Right: return (IntPtr)0xFF53;       // XK_Right
				case VKey.Down: return (IntPtr)0xFF54;        // XK_Down
				case VKey.Insert: return (IntPtr)0xFF63;      // XK_Insert
				case VKey.Delete: return (IntPtr)0xFFFF;      // XK_Delete
				case VKey.Pause: return (IntPtr)0xFF13;       // XK_Pause
				case VKey.Snapshot: return (IntPtr)0xFF61;    // XK_Print
				case VKey.Capital: return (IntPtr)0xFFE5;     // XK_Caps_Lock
				case VKey.NumLock: return (IntPtr)0xFF7F;     // XK_Num_Lock
				case VKey.Scroll: return (IntPtr)0xFF14;      // XK_Scroll_Lock

				// Modifiers: generic VK -> left-hand KeySym; explicit L/R map to their side.
				case VKey.ShiftKey:
				case VKey.LShiftKey: return (IntPtr)0xFFE1;   // XK_Shift_L
				case VKey.RShiftKey: return (IntPtr)0xFFE2;   // XK_Shift_R
				case VKey.ControlKey:
				case VKey.LControlKey: return (IntPtr)0xFFE3; // XK_Control_L
				case VKey.RControlKey: return (IntPtr)0xFFE4; // XK_Control_R
				case VKey.Menu:
				case VKey.LMenu: return (IntPtr)0xFFE9;       // XK_Alt_L
				case VKey.RMenu: return (IntPtr)0xFFEA;       // XK_Alt_R
				case VKey.LWin: return (IntPtr)0xFFEB;        // XK_Super_L
				case VKey.RWin: return (IntPtr)0xFFEC;        // XK_Super_R
				case VKey.Apps: return (IntPtr)0xFF67;        // XK_Menu

				// Keypad operators.
				case VKey.Multiply: return (IntPtr)0xFFAA;    // XK_KP_Multiply
				case VKey.Add: return (IntPtr)0xFFAB;         // XK_KP_Add
				case VKey.Separator: return (IntPtr)0xFFAC;   // XK_KP_Separator
				case VKey.Subtract: return (IntPtr)0xFFAD;    // XK_KP_Subtract
				case VKey.Decimal: return (IntPtr)0xFFAE;     // XK_KP_Decimal
				case VKey.Divide: return (IntPtr)0xFFAF;      // XK_KP_Divide

				// OEM punctuation, assuming a US layout (the KeySyms are the ASCII glyphs).
				case VKey.Oem1: return (IntPtr)0x3B;          // ';' XK_semicolon
				case VKey.Oemplus: return (IntPtr)0x3D;       // '=' XK_equal
				case VKey.Oemcomma: return (IntPtr)0x2C;      // ',' XK_comma
				case VKey.OemMinus: return (IntPtr)0x2D;      // '-' XK_minus
				case VKey.OemPeriod: return (IntPtr)0x2E;     // '.' XK_period
				case VKey.Oem2: return (IntPtr)0x2F;          // '/' XK_slash
				case VKey.Oem3: return (IntPtr)0x60;          // '`' XK_grave
				case VKey.Oem4: return (IntPtr)0x5B;          // '[' XK_bracketleft
				case VKey.Oem5: return (IntPtr)0x5C;          // '\' XK_backslash
				case VKey.Oem6: return (IntPtr)0x5D;          // ']' XK_bracketright
				case VKey.Oem7: return (IntPtr)0x27;          // ''' XK_apostrophe

				default: return IntPtr.Zero;
			}
		}
	}
}
