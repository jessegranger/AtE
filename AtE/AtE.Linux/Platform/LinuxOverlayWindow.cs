using System;
using System.Runtime.InteropServices;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IOverlayWindow"/> (the transparent, click-through,
	/// topmost overlay). This is the riskiest seam on Linux (PORTING.md), filled in last
	/// (memory -> input -> render -> window).
	///
	/// Design (PORTING.md cheat-sheet):
	///  - A WM-managed X11 window with a 32-bit ARGB visual, relying on a running compositor
	///    for per-pixel transparency (<see cref="SetTransparent"/>). Decorations are stripped
	///    via <c>_MOTIF_WM_HINTS</c> and the window is typed <c>_NET_WM_WINDOW_TYPE_UTILITY</c>,
	///    rather than going override-redirect — that keeps the window under the WM so
	///    <see cref="HasFocus"/> (the <c>_NET_ACTIVE_WINDOW</c> comparison) and
	///    <see cref="SetTopmost"/> (the <c>_NET_WM_STATE_ABOVE</c> hint) remain meaningful. The
	///    cheat-sheet lists override-redirect as the alternative; switch the
	///    <c>CWOverrideRedirect</c> attribute below if a given compositor needs it.
	///  - Click-through (<see cref="SetClickThrough"/>): an empty XShape *input* region so
	///    pointer/keyboard events fall through to the game; ImGui restores the full input
	///    region while it wants to capture.
	///  - <see cref="RunMessageLoop"/> maps the window then drives the Xlib event queue with a
	///    non-blocking <c>XPending</c>/<c>XNextEvent</c> drain, invoking <c>onFrame</c> once per
	///    iteration (the renderer paces via VSync, as on Windows). <c>ConfigureNotify</c> raises
	///    <see cref="Resized"/> and refreshes geometry; the <c>WM_DELETE_WINDOW</c> client
	///    message ends the loop.
	///
	/// Wayland is out of scope here (layer-shell, compositor-dependent); this assumes an X11
	/// session (XWayland works) and a borderless-windowed game rather than exclusive fullscreen.
	///
	/// The native <see cref="Handle"/> exposed to the renderer is the X11 window XID. A real
	/// Linux GPU backend will also need the <c>Display*</c> — it can be reached via
	/// <see cref="DisplayHandle"/> (an AtE.Linux-internal convenience, mirroring how the Windows
	/// renderer reaches the concrete <c>OverlayForm</c>).
	/// </summary>
	public sealed class LinuxOverlayWindow : IOverlayWindow {

		// ---- libX11 / libXext interop -------------------------------------------------------

		private const string X11 = "libX11.so.6";
		private const string Xext = "libXext.so.6";

		[DllImport(X11)] private static extern int XInitThreads();
		[DllImport(X11)] private static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPStr)] string display);
		[DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
		[DllImport(X11)] private static extern int XDefaultScreen(IntPtr display);
		[DllImport(X11)] private static extern IntPtr XRootWindow(IntPtr display, int screen);
		[DllImport(X11)] private static extern IntPtr XDefaultVisual(IntPtr display, int screen);
		[DllImport(X11)] private static extern int XDefaultDepth(IntPtr display, int screen);
		[DllImport(X11)] private static extern int XMatchVisualInfo(IntPtr display, int screen, int depth, int @class, out XVisualInfo vinfo);
		[DllImport(X11)] private static extern IntPtr XCreateColormap(IntPtr display, IntPtr w, IntPtr visual, int alloc);
		[DllImport(X11)] private static extern IntPtr XCreateWindow(
			IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint border_width,
			int depth, uint @class, IntPtr visual, UIntPtr valuemask, ref XSetWindowAttributes attributes);
		[DllImport(X11)] private static extern int XDestroyWindow(IntPtr display, IntPtr w);
		[DllImport(X11)] private static extern int XStoreName(IntPtr display, IntPtr w, [MarshalAs(UnmanagedType.LPStr)] string window_name);
		[DllImport(X11)] private static extern int XMapWindow(IntPtr display, IntPtr w);
		[DllImport(X11)] private static extern int XMoveResizeWindow(IntPtr display, IntPtr w, int x, int y, uint width, uint height);
		[DllImport(X11)] private static extern int XSelectInput(IntPtr display, IntPtr w, IntPtr event_mask);
		[DllImport(X11)] private static extern int XGetWindowAttributes(IntPtr display, IntPtr w, out XWindowAttributes attrs);
		[DllImport(X11)] private static extern int XTranslateCoordinates(
			IntPtr display, IntPtr src_w, IntPtr dest_w, int src_x, int src_y, out int dest_x, out int dest_y, out IntPtr child);
		[DllImport(X11)] private static extern IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPStr)] string atom_name, int only_if_exists);
		[DllImport(X11)] private static extern int XChangeProperty(
			IntPtr display, IntPtr w, IntPtr property, IntPtr type, int format, int mode, IntPtr[] data, int nelements);
		[DllImport(X11)] private static extern int XGetWindowProperty(
			IntPtr display, IntPtr w, IntPtr property, IntPtr long_offset, IntPtr long_length, int delete, IntPtr req_type,
			out IntPtr actual_type, out int actual_format, out IntPtr nitems, out IntPtr bytes_after, out IntPtr prop);
		[DllImport(X11)] private static extern int XSetWMProtocols(IntPtr display, IntPtr w, IntPtr[] protocols, int count);
		[DllImport(X11)] private static extern int XSendEvent(IntPtr display, IntPtr w, int propagate, IntPtr event_mask, ref XEvent event_send);
		[DllImport(X11)] private static extern int XPending(IntPtr display);
		[DllImport(X11)] private static extern int XNextEvent(IntPtr display, out XEvent event_return);
		[DllImport(X11)] private static extern int XFlush(IntPtr display);
		[DllImport(X11)] private static extern int XFree(IntPtr data);

		// XShape (libXext): empty input region == click-through; reset == capture input.
		[DllImport(Xext)] private static extern void XShapeCombineRectangles(
			IntPtr display, IntPtr dest, int dest_kind, int x_off, int y_off, IntPtr rectangles, int n_rects, int op, int ordering);
		[DllImport(Xext)] private static extern void XShapeCombineMask(
			IntPtr display, IntPtr dest, int dest_kind, int x_off, int y_off, IntPtr src, int op);

		// ---- X11 constants ------------------------------------------------------------------

		private const int TrueColor = 4;
		private const int InputOutput = 1;
		private const int AllocNone = 0;
		private const int PropModeReplace = 0;
		private const int ClientMessage = 33;
		private const int ConfigureNotify = 22;

		// XSetWindowAttributes valuemask bits.
		private const ulong CWBackPixel = 1UL << 1;
		private const ulong CWBorderPixel = 1UL << 3;
		private const ulong CWOverrideRedirect = 1UL << 9;
		private const ulong CWEventMask = 1UL << 11;
		private const ulong CWColormap = 1UL << 13;

		// Event masks.
		private const long ExposureMask = 1L << 15;
		private const long StructureNotifyMask = 1L << 17;
		private const long SubstructureNotifyMask = 1L << 19;
		private const long SubstructureRedirectMask = 1L << 20;

		// Predefined atoms.
		private static readonly IntPtr XA_ATOM = (IntPtr)4;
		private static readonly IntPtr AnyPropertyType = IntPtr.Zero;

		// XShape kinds / ops.
		private const int ShapeInput = 2;
		private const int ShapeSet = 0;

		// _NET_WM_STATE client-message actions.
		private const int NET_WM_STATE_REMOVE = 0;
		private const int NET_WM_STATE_ADD = 1;

		// ---- interop structs ----------------------------------------------------------------

		[StructLayout(LayoutKind.Sequential)]
		private struct XVisualInfo {
			public IntPtr visual;
			public IntPtr visualid;
			public int screen;
			public int depth;
			public int @class;
			public UIntPtr red_mask;
			public UIntPtr green_mask;
			public UIntPtr blue_mask;
			public int colormap_size;
			public int bits_per_rgb;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct XSetWindowAttributes {
			public IntPtr background_pixmap;
			public UIntPtr background_pixel;
			public IntPtr border_pixmap;
			public UIntPtr border_pixel;
			public int bit_gravity;
			public int win_gravity;
			public int backing_store;
			public UIntPtr backing_planes;
			public UIntPtr backing_pixel;
			public int save_under;
			public IntPtr event_mask;
			public IntPtr do_not_propagate_mask;
			public int override_redirect;
			public IntPtr colormap;
			public IntPtr cursor;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct XWindowAttributes {
			public int x, y, width, height, border_width, depth;
			public IntPtr visual;
			public IntPtr root;
			public int @class;
			public int bit_gravity, win_gravity, backing_store;
			public UIntPtr backing_planes, backing_pixel;
			public int save_under;
			public IntPtr colormap;
			public int map_installed;
			public int map_state;
			public IntPtr all_event_masks;
			public IntPtr your_event_mask;
			public IntPtr do_not_propagate_mask;
			public int override_redirect;
			public IntPtr screen;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct XConfigureEvent {
			public int type;
			public IntPtr serial;
			public int send_event;
			public IntPtr display;
			public IntPtr @event;
			public IntPtr window;
			public int x, y, width, height, border_width;
			public IntPtr above;
			public int override_redirect;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct XClientMessageEvent {
			public int type;
			public IntPtr serial;
			public int send_event;
			public IntPtr display;
			public IntPtr window;
			public IntPtr message_type;
			public int format;
			public IntPtr data0, data1, data2, data3, data4;
		}

		// The Xlib XEvent union. The concrete events we read overlay it at offset 0; the
		// trailing pad forces the full 24-long (192-byte) union size so XNextEvent never
		// writes past our buffer.
		[StructLayout(LayoutKind.Explicit)]
		private struct XEvent {
			[FieldOffset(0)] public int type;
			[FieldOffset(0)] public XConfigureEvent xconfigure;
			[FieldOffset(0)] public XClientMessageEvent xclient;
#pragma warning disable CS0169 // pad only exists to force the full union size
			[FieldOffset(184)] private long pad;
#pragma warning restore CS0169
		}

		// ---- window state -------------------------------------------------------------------

		private IntPtr display;
		private IntPtr root;
		private IntPtr window;
		private IntPtr visual;
		private int depth;

		private IntPtr wmProtocols;
		private IntPtr wmDeleteWindow;
		private IntPtr netWmState;
		private IntPtr netWmStateAbove;
		private IntPtr netActiveWindow;

		private int left, top, width, height;
		private bool clickThrough;
		private bool topmost;
		private bool transparent;
		private bool mapped;
		private bool closed;
		private bool hasArgb;

		// Suppress the unused-event warning until the X11 resize handler raises it.
#pragma warning disable CS0067
		/// <summary>Raised with the new client width/height when the window is resized.</summary>
		public event Action<int, int> Resized;
#pragma warning restore CS0067

		// ---- IOverlayWindow ------------------------------------------------------------------

		public void Create(string title, int width, int height) {
			if ( window != IntPtr.Zero ) {
				return; // already created
			}

			// Xlib must be told we'll call it from more than one thread: SetBounds and the
			// runtime SetClickThrough toggle come off the PoEMemory/ImGui threads while the
			// render loop drives the event queue.
			XInitThreads();

			display = XOpenDisplay(null);
			if ( display == IntPtr.Zero ) {
				throw new InvalidOperationException(
					"LinuxOverlayWindow.Create: XOpenDisplay failed (is DISPLAY set / an X server running?).");
			}

			int screen = XDefaultScreen(display);
			root = XRootWindow(display, screen);

			// Prefer a 32-bit ARGB TrueColor visual for per-pixel transparency. Fall back to the
			// screen's default visual (opaque) if none is available — the overlay still works,
			// just without a transparent background.
			if ( XMatchVisualInfo(display, screen, 32, TrueColor, out XVisualInfo vinfo) != 0 ) {
				visual = vinfo.visual;
				depth = vinfo.depth;
				hasArgb = true;
			} else {
				Log("LinuxOverlayWindow: no 32-bit ARGB visual found; transparency needs a compositor and an ARGB visual.");
				visual = XDefaultVisual(display, screen);
				depth = XDefaultDepth(display, screen);
				hasArgb = false;
			}

			IntPtr colormap = XCreateColormap(display, root, visual, AllocNone);

			var attrs = new XSetWindowAttributes {
				colormap = colormap,
				border_pixel = UIntPtr.Zero,     // required when depth != parent depth (avoids BadMatch)
				background_pixel = UIntPtr.Zero, // fully transparent on an ARGB visual
				event_mask = (IntPtr)(StructureNotifyMask | ExposureMask),
				override_redirect = 0,           // WM-managed; flip to 1 for an override-redirect overlay
			};
			UIntPtr valuemask = (UIntPtr)(CWColormap | CWBorderPixel | CWBackPixel | CWEventMask);

			window = XCreateWindow(display, root, 0, 0, (uint)width, (uint)height, 0,
				depth, InputOutput, visual, valuemask, ref attrs);
			if ( window == IntPtr.Zero ) {
				throw new InvalidOperationException("LinuxOverlayWindow.Create: XCreateWindow failed.");
			}

			this.width = width;
			this.height = height;

			XStoreName(display, window, title ?? "");

			// Resolve the atoms we need once.
			wmProtocols = Atom("WM_PROTOCOLS");
			wmDeleteWindow = Atom("WM_DELETE_WINDOW");
			netWmState = Atom("_NET_WM_STATE");
			netWmStateAbove = Atom("_NET_WM_STATE_ABOVE");
			netActiveWindow = Atom("_NET_ACTIVE_WINDOW");

			// Type as a utility window and strip decorations so it reads as a bare overlay.
			IntPtr netWmWindowType = Atom("_NET_WM_WINDOW_TYPE");
			IntPtr netWmWindowTypeUtility = Atom("_NET_WM_WINDOW_TYPE_UTILITY");
			XChangeProperty(display, window, netWmWindowType, XA_ATOM, 32, PropModeReplace,
				new[] { netWmWindowTypeUtility }, 1);

			// _MOTIF_WM_HINTS { flags, functions, decorations, input_mode, status }:
			// flags=MWM_HINTS_DECORATIONS(1<<1), decorations=0 -> no titlebar/border.
			IntPtr motifHints = Atom("_MOTIF_WM_HINTS");
			XChangeProperty(display, window, motifHints, motifHints, 32, PropModeReplace,
				new[] { (IntPtr)2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero }, 5);

			// Cooperate with the WM's close button.
			XSetWMProtocols(display, window, new[] { wmDeleteWindow }, 1);

			// Apply the load-time styles the Windows backend sets in OnLoad. Per-pixel
			// transparency is inherent to the ARGB visual; the rest are real toggles.
			transparent = true;
			SetClickThrough(true);
			SetTopmost(true);

			// The renderer binds to the XID before we map (Overlay.Initialise pattern). The
			// window already exists post-XCreateWindow, so Handle/DisplayHandle are live now.
			XFlush(display);
		}

		/// <summary>The X11 window XID (the native handle the renderer presents to).</summary>
		public IntPtr Handle => window;

		/// <summary>
		/// The Xlib <c>Display*</c>. Not on <see cref="IOverlayWindow"/> (HWND-shaped); exposed
		/// internally so the sibling Linux renderer backend can bind its GL/Vulkan surface — the
		/// same within-assembly shortcut the Windows renderer takes to reach the concrete form.
		/// </summary>
		internal IntPtr DisplayHandle => display;

		public void SetClickThrough(bool enabled) {
			clickThrough = enabled;
			if ( display == IntPtr.Zero || window == IntPtr.Zero ) {
				return;
			}
			if ( enabled ) {
				// Empty input region: pointer/keyboard events pass through to whatever is below.
				XShapeCombineRectangles(display, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, 0);
			} else {
				// Reset the input region to the whole window (mask None) so ImGui can capture.
				XShapeCombineMask(display, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
			}
			XFlush(display);
		}

		public void SetTopmost(bool enabled) {
			topmost = enabled;
			if ( display == IntPtr.Zero || window == IntPtr.Zero ) {
				return;
			}
			if ( mapped ) {
				// Once the WM manages the window, _NET_WM_STATE changes go via a client message.
				SendNetWmState(enabled ? NET_WM_STATE_ADD : NET_WM_STATE_REMOVE, netWmStateAbove);
			} else {
				// Before mapping, set the property directly; the WM honours it at map time.
				XChangeProperty(display, window, netWmState, XA_ATOM, 32, PropModeReplace,
					enabled ? new[] { netWmStateAbove } : Array.Empty<IntPtr>(), enabled ? 1 : 0);
			}
			XFlush(display);
		}

		public void SetTransparent(bool enabled) {
			// Per-pixel alpha comes from the ARGB visual chosen in Create plus a running
			// compositor; there is no runtime style bit to flip as on Windows. Record intent
			// (and warn if asked for transparency we can't provide).
			transparent = enabled;
			if ( enabled && !hasArgb ) {
				Log("LinuxOverlayWindow.SetTransparent(true): no ARGB visual; the background will not be transparent.");
			}
		}

		public int Width => width;
		public int Height => height;
		public int Left => left;
		public int Top => top;

		public void SetBounds(int left, int top, int width, int height) {
			this.left = left;
			this.top = top;
			this.width = width;
			this.height = height;
			if ( display == IntPtr.Zero || window == IntPtr.Zero ) {
				return;
			}
			XMoveResizeWindow(display, window, left, top, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
			XFlush(display);
		}

		public bool HasFocus {
			get {
				if ( display == IntPtr.Zero || window == IntPtr.Zero ) {
					return false;
				}
				return GetActiveWindow() == window;
			}
		}

		public bool IsOpen => !closed && display != IntPtr.Zero && window != IntPtr.Zero;

		public void RunMessageLoop(Action onFrame) {
			if ( display == IntPtr.Zero || window == IntPtr.Zero ) {
				throw new InvalidOperationException("LinuxOverlayWindow.RunMessageLoop called before Create.");
			}

			// Show the window and (re)apply the styles now that it's mapped.
			XMapWindow(display, window);
			mapped = true;
			SetTopmost(topmost);
			SetClickThrough(clickThrough);
			SyncGeometry();
			XFlush(display);

			try {
				while ( !closed ) {
					// Drain everything queued without blocking, then render one frame. The
					// renderer's present/VSync paces the loop, mirroring the Windows pump.
					while ( !closed && XPending(display) > 0 ) {
						XNextEvent(display, out XEvent ev);
						Dispatch(ref ev);
					}
					if ( closed ) {
						break;
					}
					onFrame();
				}
			} finally {
				closed = true;
			}
		}

		public void Close() {
			closed = true;
			if ( display != IntPtr.Zero ) {
				if ( window != IntPtr.Zero ) {
					XDestroyWindow(display, window);
					window = IntPtr.Zero;
				}
				XFlush(display);
				XCloseDisplay(display);
				display = IntPtr.Zero;
			}
			mapped = false;
		}

		// ---- helpers ------------------------------------------------------------------------

		private IntPtr Atom(string name) => XInternAtom(display, name, 0);

		private void Dispatch(ref XEvent ev) {
			switch ( ev.type ) {
				case ConfigureNotify:
					XConfigureEvent c = ev.xconfigure;
					if ( c.width != width || c.height != height ) {
						width = c.width;
						height = c.height;
						Resized?.Invoke(width, height);
					}
					SyncGeometry();
					break;
				case ClientMessage:
					XClientMessageEvent cm = ev.xclient;
					if ( cm.message_type == wmProtocols && cm.data0 == wmDeleteWindow ) {
						closed = true;
					}
					break;
			}
		}

		// Refresh cached size (from the server) and screen-space position. ConfigureNotify
		// coordinates are parent-relative; translating our (0,0) to the root gives absolute
		// coords regardless of any WM reparenting.
		private void SyncGeometry() {
			if ( XGetWindowAttributes(display, window, out XWindowAttributes a) != 0 ) {
				width = a.width;
				height = a.height;
			}
			if ( XTranslateCoordinates(display, window, root, 0, 0, out int rx, out int ry, out _) != 0 ) {
				left = rx;
				top = ry;
			}
		}

		private void SendNetWmState(int action, IntPtr atom) {
			XEvent e = default;
			e.xclient.type = ClientMessage;
			e.xclient.window = window;
			e.xclient.message_type = netWmState;
			e.xclient.format = 32;
			e.xclient.data0 = (IntPtr)action;
			e.xclient.data1 = atom;
			e.xclient.data2 = IntPtr.Zero;
			e.xclient.data3 = (IntPtr)1; // source indication: normal application
			e.xclient.data4 = IntPtr.Zero;
			XSendEvent(display, root, 0, (IntPtr)(SubstructureNotifyMask | SubstructureRedirectMask), ref e);
		}

		// Read the _NET_ACTIVE_WINDOW XID from the root window's property (EWMH). Returns
		// IntPtr.Zero if unset/unreadable.
		private IntPtr GetActiveWindow() {
			if ( XGetWindowProperty(display, root, netActiveWindow, IntPtr.Zero, (IntPtr)1, 0, AnyPropertyType,
					out _, out int format, out IntPtr nitems, out _, out IntPtr prop) != 0 || prop == IntPtr.Zero ) {
				return IntPtr.Zero;
			}
			IntPtr active = IntPtr.Zero;
			// Format-32 properties are returned as an array of C long (8 bytes here).
			if ( format == 32 && nitems != IntPtr.Zero ) {
				active = Marshal.ReadIntPtr(prop);
			}
			XFree(prop);
			return active;
		}
	}
}
