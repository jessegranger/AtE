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

			// Set up the transparent overly (with ImGui to draw on top of it)
			Overlay.Initialise();

			// Enable the Setting window
			OnRelease(Keys.F12, Settings.Toggle);
			RunForever("Settings Window", Settings.Render);

			RunForever("Demo Window", ImGui.ShowDemoWindow);

			RunForever("PoEMemory", PoEMemory.OnTick);

			// Render until we drop
			Overlay.RenderLoop();

		}


	}
}
