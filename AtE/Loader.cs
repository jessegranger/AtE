using ImGuiNET;
using SharpDX.Windows;
using System;
using System.Drawing;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	static partial class Loader {

		static AppForm RenderForm;

		static void Main() {

			Log("Creating window...");
			RenderForm = new AppForm {
				Text = "Assistant to the Exile",
				Width = 800,
				Height = 600
			};
			RenderForm.Load += (sender, args) => {
				Log("RenderForm: OnLoad...");
				// Set full transparency, where an undrawable margin fills the whole window
				RenderForm.ExtendFrameIntoClientArea(-1, -1, -1, -1);
				RenderForm.IsTransparent = true;
				var screen = Screen.FromHandle(RenderForm.Handle);
				RenderForm.Size = new Size(screen.Bounds.Width, screen.Bounds.Height);
			};

			D3DController.Initialise(RenderForm);

			ImGuiController.Initialise(RenderForm);

			D3DController.CreateRenderStates(RenderForm.Width, RenderForm.Height);

			Log("DirectX11 Renderer created?");

			Log("Binding resize event...");
			RenderForm.UserResized += (sender, args) => {
				D3DController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);
				Log($"RenderForm: UserResized {RenderForm.ClientRectangle}");

				ImGuiController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);

			};

			RenderForm.IsTransparent = false;

			// Plan some tasks that will run forever

			float fpsCap = 30f;
			bool enforceFpsCap = true;
			{
				bool enabled = true;
				Run((self) => {
					if ( !enabled ) return null;
					ImGui.ShowDemoWindow();
					ImGui.Begin("Settings", ref enabled);
					ImGui.Checkbox("", ref enforceFpsCap);
					ImGui.SameLine();
					ImGui.SliderFloat("FPS Cap", ref fpsCap, 10, 60);
					ImGui.Checkbox("VSync", ref D3DController.VSync);
					if( ImGui.Button("Test") ) {
						long endTime = Time.ElapsedMilliseconds + 15000;
						Run((self2) => {
							if ( Time.ElapsedMilliseconds > endTime ) return null;
							// ImGui.Text("Hello World!");
							DrawText("Goodbye World!", new Vector2(100, 100), Color.Orange);

							DrawCircle(new Vector2(120, 120), 20, Color.Red);

							DrawFrame(new Vector2(200, 200), new Vector2(240, 240), Color.Orange);
							return self2;
						});
					}
					ImGui.End();
					return self;
				});
			}

			{
				bool enabled = true;
				long lastFpsTime = Time.ElapsedMilliseconds - 16;
				Run((self) => {
					if ( !enabled ) return null;
					long dt = Time.ElapsedMilliseconds - lastFpsTime;
					lastFpsTime += dt;
					double fps = dt == 0 ? 999d : 1000f / dt;
					ImGui.Begin("FPS", ref enabled);
					ImGui.Text($"FPS: {fps:F1}");
					ImGui.End();
					return self;
				});
			}

			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			RenderLoop.Run(RenderForm, async () => {
				float msPerFrame = (float)Math.Round(1000f / fpsCap);
				long elapsed = Time.ElapsedMilliseconds - lastRenderTime;
				if( enforceFpsCap && elapsed < msPerFrame ) {
					await Task.Delay(3);
					return;
				}
				lastRenderTime += elapsed;
				Globals.Tick(elapsed);
			}, true);
		}


	}
}
