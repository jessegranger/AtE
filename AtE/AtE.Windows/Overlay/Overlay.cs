using System;
using System.Threading;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The render-loop orchestrator. The transparent, click-through, topmost window itself now
	/// lives behind <see cref="IOverlayWindow"/> (reached through <see cref="Windowing.Window"/>);
	/// this class keeps the portable render-loop bookkeeping (frame counter, frame lock, FPS) and
	/// exposes a thin facade over the window seam so existing callers (<c>HotKey</c>, the plugins,
	/// <c>PoEMemory</c>, <c>EntityCache</c>) keep working unchanged.
	/// </summary>
	public static class Overlay {

		public static long FrameCount;
		public static readonly AutoResetEvent FrameLock = new AutoResetEvent(false);

		private static bool closed;

		/// <summary>True once we've asked to close, or the window is gone.</summary>
		public static bool IsClosed => closed || !Windowing.Window.IsOpen;

		public static void Close() {
			closed = true;
			Windowing.Window.Close();
		}

		public static void Initialise() {

			// Create the (still hidden) window; the seam arranges the load-time transparency.
			Windowing.Window.Create("Assistant to the Exile", 800, 600);

			// Bring up the GPU/ImGui backend against the native handle.
			Rendering.Renderer.Initialise(Windowing.Window.Handle, Windowing.Window.Width, Windowing.Window.Height);

			// Initial styles: window on top of the render surface, click-through off, until OnLoad
			// (inside the seam) flips it once the device exists.
			Windowing.Window.SetClickThrough(false);

			Log("Binding resize event...");
			Windowing.Window.Resized += (w, h) => {
				int x = Windowing.Window.Left;
				int y = Windowing.Window.Top;
				Log($"RenderForm: UserResized {w}x{h}");
				Rendering.Renderer.Resize(x, y, w, h);
			};
		}

		public static bool HasFocus => Windowing.Window.HasFocus;

		public static int Height => Windowing.Window.Height;
		public static int Width => Windowing.Window.Width;
		public static int Top => Windowing.Window.Top;
		public static int Left => Windowing.Window.Left;

		public static void Resize(long left, long top, long right, long bottom) {
			Windowing.Window.SetBounds((int)left, (int)top, (int)(right - left), (int)(bottom - top));
		}

		public static double FPS { get; private set; }

		public static void RenderLoop() {
			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			Perf.Clear();
			try {
				Windowing.Window.RunMessageLoop(() => {
					FrameCount += 1;
					long dt = Time.ElapsedMilliseconds - lastRenderTime;
					var settings = PluginBase.GetPlugin<CoreSettings>();
					FPS = dt == 0 ? 999d : 1000f / dt;
					lastRenderTime += dt;

					// Clear the prior frame and start a new frame (ImGui + sprite draw lists)
					Rendering.Renderer.BeginFrame(dt);

					Perf.Render(ref settings.ShowPerformanceWindow); // data about prior frame

					// Advance all the States by one frame
					// in here they end up calling the ImGui/sprite Draw functions
					// this adds up some draw lists
					Machine.DefaultMachine.OnTick(dt);

					// Render the accumulated draw lists to the GPU and present (VSync inside)
					Rendering.Renderer.EndFrame();
					FrameLock.Set();
				});
			} finally {
				closed = true;
			}
		}
	}

}
