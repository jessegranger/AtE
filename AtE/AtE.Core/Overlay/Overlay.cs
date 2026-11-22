using ImGuiNET;
using System;
using System.Threading;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The render-loop orchestrator. The transparent, click-through, topmost window itself now
	/// lives behind <see cref="IOverlayWindow"/> (reached through <see cref="Platform.Current"/>);
	/// this class keeps the portable render-loop bookkeeping (frame counter, frame lock, FPS) and
	/// exposes a thin facade over the window seam so existing callers (<c>HotKey</c>, the plugins,
	/// <c>PoEMemory</c>, <c>EntityCache</c>) keep working unchanged.
	/// </summary>
	public static class Overlay {

		public static long FrameCount;
		public static readonly AutoResetEvent FrameLock = new AutoResetEvent(false);

		private static bool closed;

		/// <summary>True once we've asked to close, or the window is gone.</summary>
		public static bool IsClosed => closed || !Platform.Current.Window.IsOpen;

		public static void Close() {
			try { HeldButtons.ReleaseAll("the overlay is closing"); } catch ( Exception ) { } // (no button left down after it is gone)
			closed = true;
			Platform.Current.Window.Close();
		}

		public static void Initialise() {

			// Create the (still hidden) window; the seam arranges the load-time transparency.
			Platform.Current.Window.Create("Assistant to the Exile", 800, 600);

			// Bring up the GPU/ImGui backend against the native handle.
			Platform.Current.Renderer.Initialise(Platform.Current.Window.Handle, Platform.Current.Window.Width, Platform.Current.Window.Height);

			// Initial styles: window on top of the render surface, click-through off, until OnLoad
			// (inside the seam) flips it once the device exists.
			Platform.Current.Window.SetClickThrough(false);

			Log("Binding resize event...");
			Platform.Current.Window.Resized += (w, h) => {
				int x = Platform.Current.Window.Left;
				int y = Platform.Current.Window.Top;
				Log($"RenderForm: UserResized {w}x{h}");
				Platform.Current.Renderer.Resize(x, y, w, h);
			};
		}

		public static bool HasFocus => Platform.Current.Window.HasFocus;

		/// <summary>True when the overlay or the game is the foreground window: the only time the
		/// overlay may turn click-through off, so it never swallows clicks meant for other apps.</summary>
		public static bool OverlayOrGameFocused => HasFocus || Platform.Current.Keys.IsTargetForeground();
		/// <summary>True while clicks go through the overlay to the game (see IOverlayWindow.IsClickThrough).</summary>
		public static bool IsClickThrough => Platform.Current.Window.IsClickThrough;
		/// <summary>Why the overlay takes the mouse now (null while it does not): set by the renderer each frame.</summary>
		public static string CaptureReason;

		private static long closeUntilFrame = -1;
		/// <summary>
		/// Close every overlay window that can be closed (the console, memory debuggers, object and file browsers, the
		/// Performance and Bundle Browser windows), eg when a routine starts, so none is under the mouse when it clicks. Each
		/// window checks ClosingWindows before it draws, over the next two frames (whatever order they draw in).
		/// </summary>
		public static void CloseAllWindows() => closeUntilFrame = FrameCount + 2;
		/// <summary>True for the frames after CloseAllWindows: a window seeing it closes itself.</summary>
		public static bool ClosingWindows => FrameCount <= closeUntilFrame;

		public static int Height => Platform.Current.Window.Height;
		public static int Width => Platform.Current.Window.Width;
		public static int Top => Platform.Current.Window.Top;
		public static int Left => Platform.Current.Window.Left;

		public static void Resize(long left, long top, long right, long bottom) {
			Platform.Current.Window.SetBounds((int)left, (int)top, (int)(right - left), (int)(bottom - top));
		}

		public static double FPS { get; private set; }

		/// <summary>True when the overlay needs the keyboard itself: ImGui is editing text, or a key binding modal is open.</summary>
		public static bool WantsKeyboard => ImGui.GetIO().WantTextInput || KeyPicker.IsOpen;

		private static long nextFocusAttempt;
		private static bool wantedKeyboard;
		/// <summary>
		/// Keys go to the foreground window, so the overlay only takes the foreground while it needs the keyboard
		/// itself (see WantsKeyboard); the rest of the time keys pass straight through to the game.
		/// Clicks on overlay widgets don't activate it (see IOverlayWindow.SetActivatable): swapping the foreground
		/// to the overlay and back on every click made the game flicker.
		/// </summary>
		private static void UpdateKeyboardFocus() {
			bool wants = WantsKeyboard;
			bool started = wants && !wantedKeyboard;
			wantedKeyboard = wants;
			Platform.Current.Window.SetActivatable(wants);
			if ( wants ) {
				// take the keyboard when a text field or key picker opens, which a click on the overlay just did;
				// only then, so clicking away to the game (with the text field still active) leaves the game in front
				if ( started && OverlayOrGameFocused ) Platform.Current.Window.TryTakeFocus();
				return;
			}
			if ( !HasFocus || !PoEMemory.IsAttached ) return;
			// not mid-click or mid-drag (moving a window, dragging a slider): the overlay keeps the mouse until it's let go
			if ( ImGui.IsAnyMouseDown() ) return;
			if ( Time.ElapsedMilliseconds < nextFocusAttempt ) return;
			if ( !Platform.Current.Memory.TryFocusTarget() ) nextFocusAttempt = Time.ElapsedMilliseconds + 500; // don't retry every frame if it's refused
		}

		public static void RenderLoop() {
			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			Perf.Clear();
			try {
				Platform.Current.Window.RunMessageLoop(() => {
					FrameCount += 1;
					long dt = Time.ElapsedMilliseconds - lastRenderTime;
					var settings = PluginBase.GetPlugin<CoreSettings>();
					FPS = dt == 0 ? 999d : 1000f / dt;
					lastRenderTime += dt;

					// Clear the prior frame and start a new frame (ImGui + sprite draw lists)
					Platform.Current.Renderer.BeginFrame(dt);

					if ( ClosingWindows ) settings.ShowPerformanceWindow = false;
					Perf.Render(ref settings.ShowPerformanceWindow); // data about prior frame

					// Advance all the States by one frame
					// in here they end up calling the ImGui/sprite Draw functions
					// this adds up some draw lists
					Machine.DefaultMachine.OnTick(dt);

					// Render the accumulated draw lists to the GPU and present (VSync inside)
					Platform.Current.Renderer.EndFrame();
					UpdateKeyboardFocus();
					FrameLock.Set();
				});
			} finally {
				closed = true;
			}
		}
	}

}