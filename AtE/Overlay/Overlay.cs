using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
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
				// Sets a combination of Windows window-styles that cause the OverlayForm to not list in the status bar
				RenderForm.IsTransparent = true;
				var screen = Screen.FromHandle(RenderForm.Handle);
				RenderForm.Size = new Size(screen.Bounds.Width, screen.Bounds.Height);
			};

			D3DController.Initialise(RenderForm);

			ImGuiController.Initialise(RenderForm);

			D3DController.CreateRenderStates(RenderForm.Width, RenderForm.Height);

			RenderForm.IsTransparent = false;

			Log("Binding resize event...");
			RenderForm.UserResized += (sender, args) => {
				D3DController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);
				Log($"RenderForm: UserResized {RenderForm.ClientRectangle}");

				ImGuiController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);

			};

			StateMachine.DefaultMachine.EnableLogging((s) => Log(s));

		}

		public static double FPS { get; private set; }

		public static void RenderLoop() {
			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			SharpDX.Windows.RenderLoop.Run(RenderForm, async () => {
				FrameCount += 1;
				float msPerFrame = (float)Math.Round(1000f / CoreSettings.FpsCap);
				long dt = Time.ElapsedMilliseconds - lastRenderTime;
				if ( CoreSettings.EnforceFpsCap && dt < msPerFrame ) {
					await Task.Delay(3);
					return;
				}
				FPS = dt == 0 ? 999d : 1000f / dt;
				lastRenderTime += dt;

				// Clear the prior frame
				D3DController.NewFrame();

				// Start a new frame
				ImGuiController.NewFrame(dt);

				// Advance all the States by one frame
				StateMachine.DefaultMachine.OnTick(dt);

				// Render the ImGui layer to vertexes and Draw them to the GPU buffers
				ImGuiController.Render(dt);

				// TODO: Render a Sprite layer?

				// Finalize the rendering to the screen
				D3DController.Render();

			}, true);
		}
	}
}
