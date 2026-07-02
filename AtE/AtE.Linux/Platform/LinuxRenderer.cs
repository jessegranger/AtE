using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A Linux-backed <see cref="ITextureHandle"/>. On this backend the <see cref="Id"/> is the
	/// OpenGL texture *name* (a <c>GLuint</c>) widened to an <see cref="IntPtr"/> — the same value
	/// ImGui carries as a draw command's <c>TextureId</c> and that <see cref="LinuxRenderer"/> binds
	/// directly with <c>glBindTexture</c> (no separate handle→resource map is needed the way the
	/// Windows backend needs one for its SharpDX <c>ShaderResourceView</c>).
	/// </summary>
	internal sealed class LinuxTextureHandle : ITextureHandle {
		public IntPtr Id { get; }
		public int Width { get; }
		public int Height { get; }
		public LinuxTextureHandle(IntPtr id, int width, int height) {
			Id = id;
			Width = width;
			Height = height;
		}
	}

	/// <summary>
	/// Linux implementation of <see cref="IRenderer"/> (GPU device + ImGui backend).
	///
	/// Design (PORTING.md cheat-sheet + step 4/6): a single, self-contained OpenGL backend bound to
	/// the X11 overlay window via GLX, with no new managed dependencies — the same dependency-free,
	/// raw-P/Invoke approach the sibling Linux seams take (<c>LinuxOverlayWindow</c>/libX11,
	/// <c>LinuxInputSink</c>/uinput, <c>LinuxProcessMemory</c>/process_vm_readv). Rather than pull in
	/// Veldrid/Silk.NET and a modern shader pipeline, it is modelled on Dear ImGui's legacy
	/// <c>imgui_impl_opengl2</c> backend: fixed-function OpenGL (client vertex arrays + an
	/// orthographic projection) which uses only GL 1.x/1.4 entry points that libGL exports directly,
	/// so there is no GL function-pointer loading to do and nothing version-gated to negotiate. The
	/// ImGui vertex layout (<see cref="ImDrawVert"/>: pos/uv/col) maps one-to-one onto
	/// glVertexPointer/glTexCoordPointer/glColorPointer.
	///
	/// Responsibilities mirror the three Windows controllers this seam replaced:
	///  - device + present: <c>D3DController</c> → GLX context creation, glClear, glXSwapBuffers;
	///  - ImGui backend: <c>ImGuiController</c> → context/IO, font atlas upload, draw-data submission,
	///    and the Draw* draw-list helpers;
	///  - sprite atlas: <c>SpriteController</c> → Sprites.png decoded (a small built-in PNG reader,
	///    since System.Drawing.Common / WIC are not available on Linux) and drawn as textured quads.
	///
	/// Unlike the Windows backend (which reaches the concrete <c>OverlayForm</c> for WinForms input
	/// wiring), this binds to the native window via <see cref="IOverlayWindow.Handle"/> (the X11 XID)
	/// and reaches the Xlib <c>Display*</c> through the sibling <see cref="LinuxOverlayWindow.DisplayHandle"/>
	/// (a within-assembly shortcut between two Linux backends, the analogue of
	/// <c>WindowsRenderer</c> → <c>WindowsOverlayWindow.Form</c>). There is no WinForms event pump on
	/// Linux, so ImGui input is fed each frame from the polling <see cref="IInputSource"/>
	/// (cursor + the three mouse buttons); full keyboard/text routing would need the overlay's input
	/// region toggled and X key events forwarded, and is left as a follow-up.
	/// </summary>
	public sealed class LinuxRenderer : IRenderer, IDisposable {

		// ---- libGL / GLX interop ---------------------------------------------------------------
		// GLX lives in libGL on a desktop GL stack; the core GL entry points used below are the
		// fixed-function GL 1.x/1.4 functions exported directly by the same library.

		private const string GL = "libGL.so.1";

		[DllImport(GL)] private static extern IntPtr glXGetFBConfigs(IntPtr dpy, int screen, out int nelements);
		[DllImport(GL)] private static extern int glXGetFBConfigAttrib(IntPtr dpy, IntPtr config, int attribute, out int value);
		[DllImport(GL)] private static extern IntPtr glXCreateNewContext(IntPtr dpy, IntPtr config, int renderType, IntPtr shareList, bool direct);
		[DllImport(GL)] private static extern bool glXMakeCurrent(IntPtr dpy, IntPtr drawable, IntPtr ctx);
		[DllImport(GL)] private static extern void glXSwapBuffers(IntPtr dpy, IntPtr drawable);
		[DllImport(GL)] private static extern void glXDestroyContext(IntPtr dpy, IntPtr ctx);

		// Vsync via GLX_EXT_swap_control. It is an extension entry point and may be absent; calls are
		// guarded so a missing symbol just disables explicit swap-interval control.
		[DllImport(GL, EntryPoint = "glXSwapIntervalEXT")] private static extern void glXSwapIntervalEXT(IntPtr dpy, IntPtr drawable, int interval);

		// Core fixed-function GL.
		[DllImport(GL)] private static extern void glClear(uint mask);
		[DllImport(GL)] private static extern void glClearColor(float r, float g, float b, float a);
		[DllImport(GL)] private static extern void glViewport(int x, int y, int w, int h);
		[DllImport(GL)] private static extern void glScissor(int x, int y, int w, int h);
		[DllImport(GL)] private static extern void glEnable(uint cap);
		[DllImport(GL)] private static extern void glDisable(uint cap);
		[DllImport(GL)] private static extern void glBlendFuncSeparate(uint srcRGB, uint dstRGB, uint srcA, uint dstA);
		[DllImport(GL)] private static extern void glGenTextures(int n, out uint textures);
		[DllImport(GL)] private static extern void glDeleteTextures(int n, ref uint textures);
		[DllImport(GL)] private static extern void glBindTexture(uint target, uint texture);
		[DllImport(GL)] private static extern void glTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr pixels);
		[DllImport(GL)] private static extern void glTexParameteri(uint target, uint pname, int param);
		[DllImport(GL)] private static extern void glTexEnvi(uint target, uint pname, int param);
		[DllImport(GL)] private static extern void glPixelStorei(uint pname, int param);
		[DllImport(GL)] private static extern void glMatrixMode(uint mode);
		[DllImport(GL)] private static extern void glLoadIdentity();
		[DllImport(GL)] private static extern void glPushMatrix();
		[DllImport(GL)] private static extern void glPopMatrix();
		[DllImport(GL)] private static extern void glOrtho(double l, double r, double b, double t, double n, double f);
		[DllImport(GL)] private static extern void glEnableClientState(uint cap);
		[DllImport(GL)] private static extern void glDisableClientState(uint cap);
		[DllImport(GL)] private static extern void glVertexPointer(int size, uint type, int stride, IntPtr ptr);
		[DllImport(GL)] private static extern void glTexCoordPointer(int size, uint type, int stride, IntPtr ptr);
		[DllImport(GL)] private static extern void glColorPointer(int size, uint type, int stride, IntPtr ptr);
		[DllImport(GL)] private static extern void glDrawElements(uint mode, int count, uint type, IntPtr indices);

		// ---- libX11 interop (visual lookup only; the window/Display belong to LinuxOverlayWindow) --

		private const string X11 = "libX11.so.6";

		[DllImport(X11)] private static extern int XDefaultScreen(IntPtr display);
		[DllImport(X11)] private static extern int XGetWindowAttributes(IntPtr display, IntPtr w, out XWindowAttributes attrs);
		[DllImport(X11)] private static extern IntPtr XVisualIDFromVisual(IntPtr visual);
		[DllImport(X11)] private static extern int XFree(IntPtr data);

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

		// ---- GL / GLX constants ----------------------------------------------------------------

		private const uint GL_COLOR_BUFFER_BIT = 0x4000;
		private const uint GL_BLEND = 0x0BE2;
		private const uint GL_SRC_ALPHA = 0x0302;
		private const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
		private const uint GL_ONE = 1;
		private const uint GL_TEXTURE_2D = 0x0DE1;
		private const uint GL_DEPTH_TEST = 0x0B71;
		private const uint GL_STENCIL_TEST = 0x0B90;
		private const uint GL_CULL_FACE = 0x0B44;
		private const uint GL_LIGHTING = 0x0B50;
		private const uint GL_ALPHA_TEST = 0x0BC0;
		private const uint GL_SCISSOR_TEST = 0x0C11;
		private const uint GL_FLOAT = 0x1406;
		private const uint GL_UNSIGNED_BYTE = 0x1401;
		private const uint GL_UNSIGNED_SHORT = 0x1403;
		private const uint GL_TRIANGLES = 0x0004;
		private const uint GL_VERTEX_ARRAY = 0x8074;
		private const uint GL_TEXTURE_COORD_ARRAY = 0x8078;
		private const uint GL_COLOR_ARRAY = 0x8076;
		private const uint GL_PROJECTION = 0x1701;
		private const uint GL_MODELVIEW = 0x1700;
		private const int GL_RGBA = 0x1908;
		private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
		private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
		private const int GL_LINEAR = 0x2601;
		private const uint GL_TEXTURE_WRAP_S = 0x2802;
		private const uint GL_TEXTURE_WRAP_T = 0x2803;
		private const int GL_CLAMP_TO_EDGE = 0x812F;
		private const uint GL_UNPACK_ALIGNMENT = 0x0CF5;
		private const uint GL_UNPACK_ROW_LENGTH = 0x0CF2;
		private const uint GL_TEXTURE_ENV = 0x2300;
		private const uint GL_TEXTURE_ENV_MODE = 0x2200;
		private const int GL_MODULATE = 0x2100;

		private const int GLX_VISUAL_ID = 0x800B;
		private const int GLX_RGBA_TYPE = 0x8014;

		// ---- device / window state -------------------------------------------------------------

		private IntPtr display;     // borrowed from LinuxOverlayWindow (we do not close it)
		private IntPtr window;      // X11 XID of the overlay
		private IntPtr glContext;   // our GLX context
		private bool initialised;

		private LinuxOverlayWindow overlayWindow;  // sibling backend: source of forwarded input + Display*
		private int renderThreadId;                // thread the GLX context is current on (all GL must run here)

		private int width, height;  // framebuffer size, tracked across Resize
		private bool contentEnabled = true;
		private bool presentEnabled = true;

		// Input fed to ImGui from the overlay window's X event queue (no WinForms pump on Linux).
		private readonly List<LinuxOverlayInput> inputScratch = new List<LinuxOverlayInput>();
		private readonly bool[] mouseDown = new bool[5]; // event-driven button state (L,R,M,X1,X2)

		// Every GL texture name we create (font atlas, sprite atlas, LoadTexture results), so they
		// can be released in Shutdown.
		private readonly List<uint> glTextures = new List<uint>();

		// ImGui state.
		private IntPtr imguiContext;
		private ImGuiIOPtr io;
		private uint fontTexture;
		private int vtxStride, posOffset, uvOffset, colOffset;
		private ImDrawListPtr backgroundDrawList;
		private readonly Dictionary<uint, float> drawTextAtOffsets = new Dictionary<uint, float>();

		// Sprite atlas state (mirrors SpriteController).
		private const int MaxSprites = 2000;
		private const int spriteTilesWidth = 14;   // 14 sprite columns in Sprites.png
		private const int spriteTilesHeight = 16;  // 16 sprite rows

		[StructLayout(LayoutKind.Sequential)]
		private struct SpriteVertex {
			public Vector2 Position;
			public Vector2 TexC;
			public uint Color; // packed by ToRGBA (R,G,B,A byte order)
		}

		private uint spriteTexture;
		private readonly SpriteVertex[] spriteVertices = new SpriteVertex[MaxSprites * 4];
		private readonly ushort[] spriteIndices = new ushort[MaxSprites * 6];
		private int spriteCount;

		// ---- lifecycle -------------------------------------------------------------------------

		public void Initialise(IntPtr windowHandle, int width, int height) {
			if ( initialised ) {
				return;
			}

			this.window = windowHandle;
			this.width = Math.Max(1, width);
			this.height = Math.Max(1, height);

			// Reach the Xlib Display* through the sibling window backend (both Linux-only types),
			// the analogue of WindowsRenderer sourcing the concrete OverlayForm.
			if ( !(Platform.Current.Window is LinuxOverlayWindow linuxWindow) ) {
				throw new InvalidOperationException(
					"LinuxRenderer.Initialise: the active IOverlayWindow is not a LinuxOverlayWindow; " +
					"the GLX backend needs the Xlib Display* it owns.");
			}
			overlayWindow = linuxWindow;
			display = linuxWindow.DisplayHandle;
			if ( display == IntPtr.Zero || window == IntPtr.Zero ) {
				throw new InvalidOperationException(
					"LinuxRenderer.Initialise: no X11 Display/Window (was the overlay created first?).");
			}

			CreateGlContext();

			// The context is now current on this thread; record it so LoadTexture (and teardown) can
			// assert they run here — a fixed-function GLX context is single-threaded, unlike the
			// free-threaded D3D11 device on Windows.
			renderThreadId = Thread.CurrentThread.ManagedThreadId;

			Log("LinuxRenderer: configuring GL state and ImGui...");
			// Try to enable vsync so the present paces the loop, the way the D3D present blocks on
			// vsync on Windows. Best-effort: the extension may be unavailable.
			TryEnableVSync();

			InitialiseImGui();
			LoadSpriteAtlas();

			initialised = true;
			Log($"LinuxRenderer: initialised ({this.width}x{this.height}).");
		}

		// Bind a GLX context to the *existing* window. The overlay already chose its (ARGB) visual,
		// so we must pick the GLXFBConfig whose GLX_VISUAL_ID matches the window's visual — otherwise
		// glXMakeCurrent fails with BadMatch.
		private void CreateGlContext() {
			int screen = XDefaultScreen(display);

			if ( XGetWindowAttributes(display, window, out XWindowAttributes wa) == 0 || wa.visual == IntPtr.Zero ) {
				throw new InvalidOperationException("LinuxRenderer: XGetWindowAttributes failed for the overlay window.");
			}
			IntPtr wantVisualId = XVisualIDFromVisual(wa.visual);

			IntPtr configs = glXGetFBConfigs(display, screen, out int n);
			if ( configs == IntPtr.Zero || n <= 0 ) {
				throw new InvalidOperationException(
					"LinuxRenderer: glXGetFBConfigs returned none (is GLX available on this X server?).");
			}

			IntPtr chosen = IntPtr.Zero;
			try {
				for ( int i = 0; i < n; i++ ) {
					IntPtr cfg = Marshal.ReadIntPtr(configs, i * IntPtr.Size);
					if ( cfg == IntPtr.Zero ) {
						continue;
					}
					if ( glXGetFBConfigAttrib(display, cfg, GLX_VISUAL_ID, out int vid) == 0 &&
						 (IntPtr)vid == wantVisualId ) {
						chosen = cfg;
						break;
					}
				}
			} finally {
				XFree(configs);
			}

			if ( chosen == IntPtr.Zero ) {
				throw new InvalidOperationException(
					$"LinuxRenderer: no GLXFBConfig matches the overlay's visual (0x{wantVisualId.ToInt64():X}). " +
					"The window's ARGB visual must be GLX-renderable.");
			}

			glContext = glXCreateNewContext(display, chosen, GLX_RGBA_TYPE, IntPtr.Zero, true);
			if ( glContext == IntPtr.Zero ) {
				throw new InvalidOperationException("LinuxRenderer: glXCreateNewContext failed.");
			}
			if ( !glXMakeCurrent(display, window, glContext) ) {
				throw new InvalidOperationException("LinuxRenderer: glXMakeCurrent failed.");
			}
			Log("LinuxRenderer: GLX context current.");
		}

		private void TryEnableVSync() {
			try {
				glXSwapIntervalEXT(display, window, 1);
				Log("LinuxRenderer: vsync enabled via GLX_EXT_swap_control.");
			} catch ( EntryPointNotFoundException ) {
				Log("LinuxRenderer: glXSwapIntervalEXT unavailable; relying on the compositor for vsync.");
			} catch ( DllNotFoundException ) {
				Log("LinuxRenderer: libGL not found for vsync control.");
			}
		}

		private void InitialiseImGui() {
			imguiContext = ImGui.CreateContext();
			ImGui.SetCurrentContext(imguiContext);
			io = ImGui.GetIO();
			io.DisplaySize = new Vector2(width, height);
			io.DisplayFramebufferScale = Vector2.One;

			// Map ImGuiKey codes onto VK values, exactly as the Windows ImGuiController does, so the
			// VKey-indexed io.KeysDown writes from forwarded X key events drive ImGui's text editing
			// and keyboard navigation.
			SetupKeyMap();

			// ImDrawVert layout for the fixed-function client arrays (computed, not assumed).
			vtxStride = Marshal.SizeOf<ImDrawVert>();
			posOffset = (int)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos));
			uvOffset = (int)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv));
			colOffset = (int)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col));

			Log("LinuxRenderer: building ImGui font atlas...");
			unsafe {
				io.Fonts.AddFontDefault();
				io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int fw, out int fh, out int _);
				fontTexture = UploadTexture((IntPtr)pixels, fw, fh, clampToEdge: true);
				io.Fonts.SetTexID((IntPtr)(long)fontTexture);
				io.Fonts.ClearTexData();
			}
		}

		// Mirror ImGuiController's KeyMap: ImGui's internal key ids -> VK codes. io.KeysDown is then
		// indexed by VK code (the value forwarded from X key events), so the two stay consistent.
		private void SetupKeyMap() {
			io.KeyMap[(int)ImGuiKey._1] = (int)VKey.D1;
			io.KeyMap[(int)ImGuiKey._2] = (int)VKey.D2;
			io.KeyMap[(int)ImGuiKey._3] = (int)VKey.D3;
			io.KeyMap[(int)ImGuiKey._4] = (int)VKey.D4;
			io.KeyMap[(int)ImGuiKey._5] = (int)VKey.D5;
			io.KeyMap[(int)ImGuiKey._6] = (int)VKey.D6;
			io.KeyMap[(int)ImGuiKey._7] = (int)VKey.D7;
			io.KeyMap[(int)ImGuiKey._8] = (int)VKey.D8;
			io.KeyMap[(int)ImGuiKey._9] = (int)VKey.D9;
			io.KeyMap[(int)ImGuiKey._0] = (int)VKey.D0;
			io.KeyMap[(int)ImGuiKey.Backspace] = (int)VKey.Back;
			io.KeyMap[(int)ImGuiKey.LeftArrow] = (int)VKey.Left;
			io.KeyMap[(int)ImGuiKey.RightArrow] = (int)VKey.Right;
			io.KeyMap[(int)ImGuiKey.UpArrow] = (int)VKey.Up;
			io.KeyMap[(int)ImGuiKey.DownArrow] = (int)VKey.Down;
			io.KeyMap[(int)ImGuiKey.LeftBracket] = (int)VKey.OemOpenBrackets;
			io.KeyMap[(int)ImGuiKey.RightBracket] = (int)VKey.Oem6;
			io.KeyMap[(int)ImGuiKey.LeftCtrl] = (int)VKey.LControlKey;
			io.KeyMap[(int)ImGuiKey.RightCtrl] = (int)VKey.RControlKey;
			io.KeyMap[(int)ImGuiKey.LeftAlt] = (int)VKey.LMenu;
			io.KeyMap[(int)ImGuiKey.RightAlt] = (int)VKey.RMenu;

			// Map every ImGuiKey/VKey pair that shares a name (A, B, ..., Enter, Tab, Escape, etc.).
			HashSet<string> imGuiKeyNames = new HashSet<string>(Enum.GetNames(typeof(ImGuiKey)));
			foreach ( string keyName in Enum.GetNames(typeof(VKey)) ) {
				if ( imGuiKeyNames.Contains(keyName) ) {
					io.KeyMap[(int)Enum.Parse(typeof(ImGuiKey), keyName)] = (int)Enum.Parse(typeof(VKey), keyName);
				}
			}
		}

		// Decode Static/Sprites.png (no System.Drawing.Common/WIC on Linux) and upload it as the
		// sprite atlas texture. A failure here is non-fatal: the sprite layer simply draws nothing.
		private void LoadSpriteAtlas() {
			const string path = "Static/Sprites.png";
			try {
				if ( !PngDecoder.TryDecode(path, out int w, out int h, out byte[] rgba) ) {
					Log($"LinuxRenderer: could not decode {path}; sprite layer disabled.");
					return;
				}
				GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
				try {
					spriteTexture = UploadTexture(pin.AddrOfPinnedObject(), w, h, clampToEdge: true);
				} finally {
					pin.Free();
				}
				Log($"LinuxRenderer: loaded sprite atlas {w}x{h} (tex {spriteTexture}).");
			} catch ( Exception e ) {
				Log($"LinuxRenderer: failed to load {path}: {e.Message}");
			}
		}

		// Create a 2D RGBA texture from a tightly-packed 32bpp buffer and return its GL name.
		private uint UploadTexture(IntPtr rgbaPixels, int w, int h, bool clampToEdge) {
			glGenTextures(1, out uint tex);
			glBindTexture(GL_TEXTURE_2D, tex);
			glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
			glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
			if ( clampToEdge ) {
				glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
				glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
			}
			glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
			glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
			glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, w, h, 0, (uint)GL_RGBA, GL_UNSIGNED_BYTE, rgbaPixels);
			glTextures.Add(tex); // track for release in Shutdown
			return tex;
		}

		// ---- per-frame -------------------------------------------------------------------------

		public void BeginFrame(long dt) {
			if ( !initialised ) {
				return;
			}
			ImGui.SetCurrentContext(imguiContext);

			// Reset the sprite list and the per-id text stacking for the new frame.
			spriteCount = 0;
			drawTextAtOffsets.Clear();

			ClearTarget();

			io.DisplaySize = new Vector2(width, height);
			// ImGui asserts DeltaTime > 0; clamp a 0ms frame to 1ms.
			io.DeltaTime = (dt <= 0 ? 1 : dt) / 1000f;

			// Cursor position is polled globally (the overlay's shaped-out input region means X does
			// not deliver motion events to it). It is screen-space; make it window-local.
			Vector2 cursor = Platform.Current.Keys.GetCursorPos();
			io.MousePos = new Vector2(cursor.X - Platform.Current.Window.Left, cursor.Y - Platform.Current.Window.Top);

			// Apply the keyboard/text/button/wheel events forwarded from the overlay window since the
			// last frame (the Linux analogue of OverlayForm's WinForms input handlers). Buttons and
			// keys arrive only as real ButtonPress/KeyPress events delivered to the overlay — which X
			// only does while click-through is off — so ImGui no longer treats a click meant for the
			// game as its own (the fix for reading the global pointer mask).
			io.MouseWheel = 0f;
			if ( overlayWindow != null ) {
				overlayWindow.DrainInput(inputScratch);
				foreach ( LinuxOverlayInput ev in inputScratch ) {
					switch ( ev.Kind ) {
						case LinuxOverlayInputKind.Key:
							io.KeyShift = ev.Shift;
							io.KeyCtrl = ev.Ctrl;
							io.KeyAlt = ev.Alt;
							if ( ev.VKeyCode > 0 && ev.VKeyCode < io.KeysDown.Count ) {
								io.KeysDown[ev.VKeyCode] = ev.Down;
							}
							break;
						case LinuxOverlayInputKind.Char:
							io.AddInputCharacter(ev.Char);
							break;
						case LinuxOverlayInputKind.MouseButton:
							if ( ev.Button >= 0 && ev.Button < mouseDown.Length ) {
								mouseDown[ev.Button] = ev.Down;
							}
							break;
						case LinuxOverlayInputKind.MouseWheel:
							io.MouseWheel += ev.Wheel;
							break;
					}
				}
			}
			for ( int i = 0; i < mouseDown.Length; i++ ) {
				io.MouseDown[i] = mouseDown[i];
			}

			// When the pointer is over an ImGui element, ImGui sets WantCapture* (from the prior
			// frame). Turn click-through OFF so input lands on the overlay, ON otherwise — exactly
			// the gating the Windows backend does in ImGuiController.NewFrame.
			bool wantInput = io.WantCaptureKeyboard || io.WantCaptureMouse;
			Platform.Current.Window.SetClickThrough(!wantInput);

			// While click-through is on, the overlay receives no button events, so a release can fall
			// through to the game and leave a button stuck "down". Clear the held state in that case.
			if ( !wantInput ) {
				Array.Clear(mouseDown, 0, mouseDown.Length);
			}

			ImGui.NewFrame();
			// The Draw* helpers paint onto the background draw list, beneath ImGui windows.
			backgroundDrawList = ImGui.GetBackgroundDrawList();
		}

		public void EndFrame() {
			if ( !initialised ) {
				return;
			}

			backgroundDrawList = default; // becomes unsafe once Render() runs

			// Always let ImGui build (and thus consume) its draw data, even when content is gated
			// off — matches ImGuiController.Render returning after ImGui.Render().
			ImGui.Render();

			if ( contentEnabled && width > 0 && height > 0 ) {
				SetupRenderState();
				RenderSprites();   // sprites first, beneath the ImGui layer
				RenderImGui(ImGui.GetDrawData());
				TeardownRenderState();
			}

			Present();
		}

		public void Resize(int left, int top, int width, int height) {
			this.width = Math.Max(1, width);
			this.height = Math.Max(1, height);
			if ( initialised ) {
				io.DisplaySize = new Vector2(this.width, this.height);
				glViewport(0, 0, this.width, this.height);
			}
		}

		// Present the frame. With vsync on, glXSwapBuffers blocks ~one refresh and so paces the
		// render loop (as the D3D present does on Windows). When presenting is gated off, sleep
		// briefly instead so the loop does not spin a core — mirroring D3DController.Render.
		private void Present() {
			if ( presentEnabled ) {
				glXSwapBuffers(display, window);
			} else {
				Thread.Sleep(100);
			}
		}

		private void ClearTarget() {
			glClearColor(0f, 0f, 0f, 0f); // transparent
			glClear(GL_COLOR_BUFFER_BIT);
		}

		// ---- GL render-state (imgui_impl_opengl2 style) ----------------------------------------

		private void SetupRenderState() {
			glViewport(0, 0, width, height);

			glEnable(GL_BLEND);
			// Straight-alpha colour, additive coverage on the alpha channel so the result composites
			// correctly against the transparent overlay (same intent as the D3D blend states).
			glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
			glDisable(GL_CULL_FACE);
			glDisable(GL_DEPTH_TEST);
			glDisable(GL_STENCIL_TEST);
			glDisable(GL_LIGHTING);
			glDisable(GL_ALPHA_TEST);
			glEnable(GL_TEXTURE_2D);
			glEnable(GL_SCISSOR_TEST);
			glTexEnvi(GL_TEXTURE_ENV, GL_TEXTURE_ENV_MODE, GL_MODULATE);

			// Pixel-space orthographic projection, origin top-left to match ImGui/screen coords.
			glMatrixMode(GL_PROJECTION);
			glPushMatrix();
			glLoadIdentity();
			glOrtho(0.0, width, height, 0.0, -1.0, 1.0);
			glMatrixMode(GL_MODELVIEW);
			glPushMatrix();
			glLoadIdentity();

			glEnableClientState(GL_VERTEX_ARRAY);
			glEnableClientState(GL_TEXTURE_COORD_ARRAY);
			glEnableClientState(GL_COLOR_ARRAY);
		}

		private void TeardownRenderState() {
			glDisableClientState(GL_COLOR_ARRAY);
			glDisableClientState(GL_TEXTURE_COORD_ARRAY);
			glDisableClientState(GL_VERTEX_ARRAY);
			glMatrixMode(GL_MODELVIEW);
			glPopMatrix();
			glMatrixMode(GL_PROJECTION);
			glPopMatrix();
			glDisable(GL_SCISSOR_TEST);
		}

		private void RenderImGui(ImDrawDataPtr data) {
			if ( data.CmdListsCount == 0 || data.TotalVtxCount == 0 ) {
				return;
			}
			Vector2 pos = data.DisplayPos;
			for ( int i = 0; i < data.CmdListsCount; i++ ) {
				ImDrawListPtr cmdList = data.CmdListsRange[i];
				IntPtr vtx = cmdList.VtxBuffer.Data;
				IntPtr idx = cmdList.IdxBuffer.Data;

				glVertexPointer(2, GL_FLOAT, vtxStride, vtx + posOffset);
				glTexCoordPointer(2, GL_FLOAT, vtxStride, vtx + uvOffset);
				glColorPointer(4, GL_UNSIGNED_BYTE, vtxStride, vtx + colOffset);

				for ( int j = 0; j < cmdList.CmdBuffer.Size; j++ ) {
					ImDrawCmdPtr cmd = cmdList.CmdBuffer[j];

					// Scissor: ImGui clip rect is (minX,minY,maxX,maxY), top-left origin; GL scissor
					// is bottom-left origin.
					float clipMinX = cmd.ClipRect.X - pos.X;
					float clipMinY = cmd.ClipRect.Y - pos.Y;
					float clipMaxX = cmd.ClipRect.Z - pos.X;
					float clipMaxY = cmd.ClipRect.W - pos.Y;
					if ( clipMaxX <= clipMinX || clipMaxY <= clipMinY ) {
						continue;
					}
					glScissor((int)clipMinX, (int)(height - clipMaxY),
						(int)(clipMaxX - clipMinX), (int)(clipMaxY - clipMinY));

					glBindTexture(GL_TEXTURE_2D, (uint)cmd.TextureId.ToInt64());
					// ImDrawIdx is 16-bit; index pointer advances by IdxOffset entries. We do not set
					// RendererHasVtxOffset, so VtxOffset is always 0 here.
					IntPtr idxPtr = idx + (int)cmd.IdxOffset * sizeof(ushort);
					glDrawElements(GL_TRIANGLES, (int)cmd.ElemCount, GL_UNSIGNED_SHORT, idxPtr);
				}
			}
		}

		private void RenderSprites() {
			if ( spriteCount == 0 || spriteTexture == 0 ) {
				return;
			}
			// Sprites draw across the whole window (no per-sprite clip).
			glScissor(0, 0, width, height);
			glBindTexture(GL_TEXTURE_2D, spriteTexture);

			GCHandle vpin = GCHandle.Alloc(spriteVertices, GCHandleType.Pinned);
			GCHandle ipin = GCHandle.Alloc(spriteIndices, GCHandleType.Pinned);
			try {
				IntPtr v = vpin.AddrOfPinnedObject();
				int stride = Marshal.SizeOf<SpriteVertex>();
				glVertexPointer(2, GL_FLOAT, stride, v + 0);
				glTexCoordPointer(2, GL_FLOAT, stride, v + 8);
				glColorPointer(4, GL_UNSIGNED_BYTE, stride, v + 16);
				glDrawElements(GL_TRIANGLES, spriteCount * 6, GL_UNSIGNED_SHORT, ipin.AddrOfPinnedObject());
			} finally {
				vpin.Free();
				ipin.Free();
			}
		}

		// ---- texture loading -------------------------------------------------------------------

		public ITextureHandle LoadTexture(string path) {
			if ( !initialised ) {
				throw new InvalidOperationException("LinuxRenderer.LoadTexture called before Initialise.");
			}
			// The GLX context is current only on the render-loop thread, so the glGenTextures/
			// glTexImage2D upload below must run there too (unlike the free-threaded D3D11 device on
			// Windows). No caller does this today — the only textures are the font + sprite atlases,
			// uploaded during Initialise — so warn loudly rather than fail if that ever changes; the
			// fix would be to marshal the upload onto the render thread or use a shared GL context.
			if ( Thread.CurrentThread.ManagedThreadId != renderThreadId ) {
				Log($"LinuxRenderer.LoadTexture: WARNING — called off the render thread " +
					$"(got {Thread.CurrentThread.ManagedThreadId}, expected {renderThreadId}); the GL upload may fail. " +
					"Marshal texture loads onto the render-loop thread.");
			}
			if ( !PngDecoder.TryDecode(path, out int w, out int h, out byte[] rgba) ) {
				throw new InvalidOperationException($"LinuxRenderer.LoadTexture: could not decode '{path}' (expected an 8-bit PNG).");
			}
			GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
			try {
				uint tex = UploadTexture(pin.AddrOfPinnedObject(), w, h, clampToEdge: true);
				return new LinuxTextureHandle((IntPtr)(long)tex, w, h);
			} finally {
				pin.Free();
			}
		}

		// ---- gating ----------------------------------------------------------------------------

		public bool ContentEnabled {
			set => contentEnabled = value;
		}

		public bool PresentEnabled {
			set => presentEnabled = value;
		}

		public void Clear() {
			if ( initialised ) {
				ClearTarget();
			}
		}

		// ---- teardown --------------------------------------------------------------------------

		/// <summary>
		/// Release the GL textures, the GLX context, and the ImGui context. Idempotent. Must run on
		/// the render-loop thread (the only thread the GLX context is current on), so it is invoked
		/// from <see cref="LinuxOverlayWindow.RunMessageLoop"/>'s finally — on that thread, while the
		/// Display* and drawable are still valid — rather than from an arbitrary caller of
		/// <c>Overlay.Close</c>.
		/// </summary>
		internal void Shutdown() {
			if ( !initialised ) {
				return;
			}
			initialised = false;

			try {
				for ( int i = 0; i < glTextures.Count; i++ ) {
					uint tex = glTextures[i];
					glDeleteTextures(1, ref tex);
				}
			} catch ( Exception e ) {
				Log($"LinuxRenderer.Shutdown: glDeleteTextures failed: {e.Message}");
			}
			glTextures.Clear();
			fontTexture = 0;
			spriteTexture = 0;

			if ( display != IntPtr.Zero ) {
				// Detach the context from this thread, then destroy it. The Display* belongs to
				// LinuxOverlayWindow, which closes it right after this returns — we only release ours.
				glXMakeCurrent(display, IntPtr.Zero, IntPtr.Zero);
				if ( glContext != IntPtr.Zero ) {
					glXDestroyContext(display, glContext);
					glContext = IntPtr.Zero;
				}
			}

			if ( imguiContext != IntPtr.Zero ) {
				ImGui.DestroyContext(imguiContext);
				imguiContext = IntPtr.Zero;
			}
			Log("LinuxRenderer: shut down (GL textures + context + ImGui context released).");
		}

		// No finalizer: unlike LinuxInputSink (whose finalizer just close()s an fd), GL/GLX teardown
		// is only valid on the thread the context is current on. A finalizer runs on the GC thread,
		// where the context is not current, so calling GL there would be invalid — teardown must come
		// through Shutdown() on the render-loop thread. Dispose is provided for explicit/symmetric use.
		public void Dispose() => Shutdown();

		// ---- draw helpers (ImGui background draw list, mirroring ImGuiController) ---------------

		public float GetNextTextOffset(uint id) => drawTextAtOffsets.TryGetValue(id, out float v) ? v : 0f;

		// These paint onto the background draw list captured in BeginFrame, so (like the Windows
		// ImGuiController) they are only valid between BeginFrame and EndFrame — exactly when the
		// plugins call them, from Machine.OnTick inside the render loop.
		public void DrawText(string text, Vector2 pos, Color color) =>
			backgroundDrawList.AddText(pos, (uint)ToRGBA(color), text);

		public void DrawTextAt(uint id, Vector2 pos, string text, Color color, float lineHeight) {
			drawTextAtOffsets.TryGetValue(id, out float offset);
			pos.Y += offset;
			DrawText(text, pos, color);
			drawTextAtOffsets[id] = offset + lineHeight;
		}

		public void DrawCircle(Vector2 pos, float radius, Color color, int thickness) {
			if ( IsValid(pos) ) {
				backgroundDrawList.AddCircle(pos, radius, (uint)ToRGBA(color), 0, thickness);
			}
		}

		public void DrawFrame(Vector2 topLeft, Vector2 bottomRight, Color color, int thickness) {
			if ( IsValid(topLeft) && IsValid(bottomRight) ) {
				backgroundDrawList.AddRect(topLeft, bottomRight, (uint)ToRGBA(color), 0f, ImDrawFlags.None, thickness);
			}
		}

		public void DrawLine(Vector2 start, Vector2 end, Color color, int thickness) =>
			backgroundDrawList.AddLine(start, end, (uint)ToRGBA(color), thickness);

		// ---- sprites (atlas quad accumulation, mirroring SpriteController.DrawSprite) -----------

		public void DrawSprite(SpriteIcon icon, RectangleF rect) {
			if ( spriteCount >= MaxSprites - 1 ) {
				Log("LinuxRenderer: ignoring attempt to draw too many sprites.");
				return;
			}
			RectangleF uv = GetUV(icon);
			int v = spriteCount * 4;
			int i = spriteCount * 6;
			uint white = (uint)ToRGBA(Color.White);

			spriteVertices[v] = new SpriteVertex {
				Position = new Vector2(rect.X, rect.Y),
				TexC = new Vector2(uv.X, uv.Y),
				Color = white
			};
			spriteVertices[v + 1] = new SpriteVertex {
				Position = new Vector2(rect.X + rect.Width, rect.Y),
				TexC = new Vector2(uv.X + uv.Width, uv.Y),
				Color = white
			};
			spriteVertices[v + 2] = new SpriteVertex {
				Position = new Vector2(rect.X, rect.Y + rect.Height),
				TexC = new Vector2(uv.X, uv.Y + uv.Height),
				Color = white
			};
			spriteVertices[v + 3] = new SpriteVertex {
				Position = new Vector2(rect.X + rect.Width, rect.Y + rect.Height),
				TexC = new Vector2(uv.X + uv.Width, uv.Y + uv.Height),
				Color = white
			};

			spriteIndices[i] = (ushort)v;
			spriteIndices[i + 1] = (ushort)(v + 1);
			spriteIndices[i + 2] = (ushort)(v + 2);
			spriteIndices[i + 3] = (ushort)(v + 2);
			spriteIndices[i + 4] = (ushort)(v + 1);
			spriteIndices[i + 5] = (ushort)(v + 3);
			spriteCount++;
		}

		// UVs (all in [0,1]) for one tile of the atlas; identical layout to SpriteController.
		private static RectangleF GetUV(SpriteIcon icon) {
			int index = (int)icon - 1;
			float x = index % spriteTilesWidth;
			float y = index / spriteTilesWidth;
			return new RectangleF(x / spriteTilesWidth, y / spriteTilesHeight,
				1f / spriteTilesWidth, 1f / spriteTilesHeight);
		}

		// ---- minimal PNG decoder ---------------------------------------------------------------

		/// <summary>
		/// A small, dependency-free PNG reader sufficient for the project's image assets
		/// (Sprites.png and any textures loaded via <see cref="LoadTexture"/>). System.Drawing.Common
		/// is unavailable on Linux and SharpDX's WIC path is Windows-only, so this decodes the common
		/// case directly: 8-bit, non-interlaced truecolour (RGB) or truecolour-with-alpha (RGBA),
		/// always producing a tightly-packed 32bpp RGBA buffer. The compressed image data is a zlib
		/// stream whose DEFLATE payload is inflated with <see cref="DeflateStream"/> (skipping the
		/// 2-byte zlib header and the trailing Adler-32). Other colour types/bit depths are rejected.
		/// </summary>
		private static class PngDecoder {

			private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

			public static bool TryDecode(string path, out int width, out int height, out byte[] rgba) {
				width = height = 0;
				rgba = null;
				if ( !File.Exists(path) ) {
					Log($"PngDecoder: file not found: {path}");
					return false;
				}

				byte[] file = File.ReadAllBytes(path);
				if ( file.Length < 8 ) {
					return false;
				}
				for ( int i = 0; i < Signature.Length; i++ ) {
					if ( file[i] != Signature[i] ) {
						Log($"PngDecoder: {path} is not a PNG.");
						return false;
					}
				}

				int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
				bool haveHeader = false;
				var idat = new MemoryStream();

				int p = 8;
				while ( p + 8 <= file.Length ) {
					int len = ReadBE32(file, p);
					string type = System.Text.Encoding.ASCII.GetString(file, p + 4, 4);
					int dataStart = p + 8;
					if ( dataStart + len + 4 > file.Length ) {
						break; // truncated
					}

					switch ( type ) {
						case "IHDR":
							w = ReadBE32(file, dataStart);
							h = ReadBE32(file, dataStart + 4);
							bitDepth = file[dataStart + 8];
							colorType = file[dataStart + 9];
							interlace = file[dataStart + 12];
							haveHeader = true;
							break;
						case "IDAT":
							idat.Write(file, dataStart, len);
							break;
						case "IEND":
							p = file.Length; // stop
							break;
					}
					p = dataStart + len + 4; // skip data + CRC
				}

				if ( !haveHeader || w <= 0 || h <= 0 ) {
					return false;
				}
				if ( bitDepth != 8 || interlace != 0 || (colorType != 2 && colorType != 6) ) {
					Log($"PngDecoder: unsupported PNG ({path}): bitDepth={bitDepth}, colorType={colorType}, interlace={interlace}. " +
						"Only 8-bit non-interlaced RGB/RGBA is supported.");
					return false;
				}

				int channels = colorType == 6 ? 4 : 3;
				byte[] raw = Inflate(idat.ToArray());
				int stride = w * channels;
				if ( raw.Length < (stride + 1) * h ) {
					Log($"PngDecoder: inflated data too short for {path} ({raw.Length} < {(stride + 1) * h}).");
					return false;
				}

				byte[] outRgba = Unfilter(raw, w, h, channels);
				width = w;
				height = h;
				rgba = outRgba;
				return true;
			}

			// Inflate a zlib stream: 2-byte header + raw DEFLATE + 4-byte Adler-32 trailer. We hand
			// DeflateStream the raw DEFLATE bytes (skipping the header); it stops at the final block,
			// leaving the Adler bytes unread, which is fine.
			private static byte[] Inflate(byte[] zlib) {
				if ( zlib.Length <= 2 ) {
					return Array.Empty<byte>();
				}
				using ( var input = new MemoryStream(zlib, 2, zlib.Length - 2) )
				using ( var deflate = new DeflateStream(input, CompressionMode.Decompress) )
				using ( var output = new MemoryStream() ) {
					deflate.CopyTo(output);
					return output.ToArray();
				}
			}

			// Reverse the per-scanline PNG filters and expand to packed RGBA.
			private static byte[] Unfilter(byte[] raw, int w, int h, int channels) {
				int stride = w * channels;
				byte[] cur = new byte[stride];
				byte[] prev = new byte[stride];
				byte[] outRgba = new byte[w * h * 4];

				int src = 0;
				for ( int y = 0; y < h; y++ ) {
					byte filter = raw[src++];
					Buffer.BlockCopy(raw, src, cur, 0, stride);
					src += stride;

					for ( int x = 0; x < stride; x++ ) {
						int a = x >= channels ? cur[x - channels] : 0; // left
						int b = prev[x];                                // up
						int c = x >= channels ? prev[x - channels] : 0; // up-left
						int val = cur[x];
						switch ( filter ) {
							case 0: break;                              // None
							case 1: val += a; break;                    // Sub
							case 2: val += b; break;                    // Up
							case 3: val += (a + b) / 2; break;          // Average
							case 4: val += Paeth(a, b, c); break;       // Paeth
							default: break;
						}
						cur[x] = (byte)val;
					}

					int dst = y * w * 4;
					for ( int x = 0; x < w; x++ ) {
						int s = x * channels;
						outRgba[dst++] = cur[s];                        // R
						outRgba[dst++] = cur[s + 1];                    // G
						outRgba[dst++] = cur[s + 2];                    // B
						outRgba[dst++] = channels == 4 ? cur[s + 3] : (byte)255; // A
					}

					(prev, cur) = (cur, prev); // current row becomes previous for the next iteration
				}
				return outRgba;
			}

			private static int Paeth(int a, int b, int c) {
				int pp = a + b - c;
				int pa = Math.Abs(pp - a);
				int pb = Math.Abs(pp - b);
				int pc = Math.Abs(pp - c);
				if ( pa <= pb && pa <= pc ) return a;
				return pb <= pc ? b : c;
			}

			private static int ReadBE32(byte[] b, int off) =>
				(b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
		}
	}
}
