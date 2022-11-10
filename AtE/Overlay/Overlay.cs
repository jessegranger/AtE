using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public static class Overlay {
		public static OverlayForm RenderForm;
		public static long FrameCount;

		public static void Close() => RenderForm?.Close();
		public static void Initialise() {

			Log("Creating window...");
			RenderForm = new OverlayForm {
				Text = "Assistant to the Exile",
				Width = 800,
				Height = 600
			};
			RenderForm.ShowInTaskbar = false;
			RenderForm.Load += (sender, args) => {
				Log("RenderForm: OnLoad...");
				// Set full transparency, where an undrawable (by windows) margin fills the whole form
				RenderForm.ExtendFrameIntoClientArea(-1, -1, -1, -1);
				// MSDN: Negative margins have special meaning to DwmExtendFrameIntoClientArea.
				// Negative margins create the "sheet of glass" effect, where the client area
				// is rendered as a solid surface with no window border.
				RenderForm.IsTransparent = true;
				var screen = Screen.FromHandle(RenderForm.Handle);
				RenderForm.Size = new Size(screen.Bounds.Width, screen.Bounds.Height);
			};

			D3DController.Initialise(RenderForm);

			ImGuiController.Initialise(RenderForm);

			SpriteController.Initialise(RenderForm);

			// Not used: D3DController.CreateRenderStates(RenderForm.Width, RenderForm.Height);

			// Sets the initial window styles, with RenderForm on top at first.
			// Later, OnLoad will toggle this, and put RenderForm "behind" the DirectX render surface.
			RenderForm.IsTransparent = false;

			Log("Binding resize event...");
			RenderForm.UserResized += (sender, args) => {
				int x = RenderForm.Left;
				int y = RenderForm.Top;
				int w = RenderForm.Width;
				int h = RenderForm.Height;
				Log($"RenderForm: UserResized {RenderForm.ClientRectangle}");
				D3DController.Resize(x, y, w, h);

				ImGuiController.Resize(w, h);

				SpriteController.Resize(w, h);

			};

		}

		public static bool HasFocus => Win32.GetForegroundWindow() == RenderForm.Handle; // !RenderForm.IsTransparent;

		public static int Height => RenderForm.Height;
		public static int Width => RenderForm.Width;
		public static int Top => RenderForm.Top;
		public static int Left => RenderForm.Left;
		public static void Resize(long left, long top, long right, long bottom) {
			long width = right - left;
			long height = bottom - top;
			RenderForm.Size = new Size((int)width, (int)height);
			RenderForm.Location = new Point((int)left, (int)top);
		}

		public static double FPS { get; private set; }

		public static void RenderLoop() {
			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			SharpDX.Windows.RenderLoop.Run(RenderForm, async () => {
				FrameCount += 1;
				var settings = PluginBase.GetPlugin<CoreSettings>();
				int msPerFrame = (int)Math.Round(1000f / settings.FPS_Maximum);
				long dt = Time.ElapsedMilliseconds - lastRenderTime;
				if ( settings.Enable_FPS_Maximum && dt < msPerFrame ) {
					await Task.Delay((int)(msPerFrame - dt));
					return; // using await give much more accurate timing
					// but the return here is mandatory, as control resumes on a new thread after an await
					// so the return immediately ends that thread, and the next frame can begin on the old thread
				}
				FPS = dt == 0 ? 999d : 1000f / dt;
				lastRenderTime += dt;

				// Clear the prior frame
				D3DController.NewFrame();

				// Start a new frame
				ImGuiController.NewFrame(dt);
				SpriteController.NewFrame(dt);

				// Advance all the States by one frame
				// in here they end up calling ImGuiController and SpriteController Draw functions
				// this adds up some draw lists
				StateMachine.DefaultMachine.OnTick(dt);

				SpriteController.Render(dt);
				// Render the ImGui layer to vertexes and Draw them to the GPU buffers
				ImGuiController.Render(dt);

				// Finalize the rendering to the screen
				D3DController.Render();

			}, true);
		}
	}

}
