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


		static void Main() {

			Overlay.Initialise();
			// RenderForm.IsTransparent = false;

			// Plan some tasks that will run forever

			float fpsCap = 30f;
			bool enforceFpsCap = true;
			{
				bool enabled = true;
				Run((self, dt) => {
					if ( !enabled ) return null;
					ImGui.Begin("Settings", ref enabled);
					ImGui.Checkbox("", ref enforceFpsCap);
					ImGui.SameLine();
					ImGui.SliderFloat("FPS Cap", ref fpsCap, 10, 60);
					ImGui.Checkbox("VSync", ref D3DController.VSync);
					if( ImGui.Button("Test") ) {
						long endTime = Time.ElapsedMilliseconds + 15000;
						Run((self2, dt2) => {
							if ( Time.ElapsedMilliseconds > endTime ) return null;
							// ImGui.Text("Hello World!");
							DrawText("Goodbye World!", new Vector2(100, 100), Color.Orange);

							DrawCircle(new Vector2(120, 120), 20, Color.Red);

							DrawFrame(new Vector2(200, 200), new Vector2(240, 240), Color.Orange);
							return self2;
						});
					}
					ImGui.SameLine();
					if( ImGui.Button("Exit") ) {
						Overlay.Close();
					}
					ImGui.End();
					return self;
				});
			}

			{
				bool enabled = true;
				Run((self, dt) => {
					if ( !enabled ) return null;
					double fps = dt == 0 ? 999d : 1000f / dt;
					ImGui.Begin("FPS", ref enabled);
					ImGui.Text($"FPS: {fps:F1}");
					ImGui.End();
					return self;
				});
			}

			Overlay.RenderLoop();

		}


	}
}
