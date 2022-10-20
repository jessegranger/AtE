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
		static OverlayForm RenderForm;

		public static void Close() => RenderForm?.Close();
		public static void Initialise() {

			Log("Creating window...");
			RenderForm = new OverlayForm {
				Text = "Assistant to the Exile",
				Width = 800,
				Height = 600
			};
			RenderForm.Load += (sender, args) => {
				Log("RenderForm: OnLoad...");
				// Set full transparency, where an undrawable margin fills the whole window
				RenderForm.ExtendFrameIntoClientArea(-1, -1, -1, -1);
				// Sets a combination of Windows window-styles that cause the OverlayForm to not list in the status bar
				RenderForm.IsTransparent = true;
				var screen = Screen.FromHandle(RenderForm.Handle);
				RenderForm.Size = new Size(screen.Bounds.Width, screen.Bounds.Height);
			};

			D3DController.Initialise(RenderForm);

			ImGuiController.Initialise(RenderForm);

			D3DController.CreateRenderStates(RenderForm.Width, RenderForm.Height);

			Log("Binding resize event...");
			RenderForm.UserResized += (sender, args) => {
				D3DController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);
				Log($"RenderForm: UserResized {RenderForm.ClientRectangle}");

				ImGuiController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);

			};

		}
		public static void RenderLoop() {
			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			SharpDX.Windows.RenderLoop.Run(RenderForm, async () => {
				float msPerFrame = (float)Math.Round(1000f / CoreSettings.FpsCap);
				long elapsed = Time.ElapsedMilliseconds - lastRenderTime;
				if ( CoreSettings.EnforceFpsCap && elapsed < msPerFrame ) {
					await Task.Delay(3);
					return;
				}
				lastRenderTime += elapsed;
				Globals.Tick(elapsed);
			}, true);
		}
	}
}
