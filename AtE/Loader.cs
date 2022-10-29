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

			// Set up the memory hooks
			RunForever("PoEMemory", PoEMemory.OnTick);

			// Enable the Setting window
			RunForever("Settings Window", Settings.Render);
			OnRelease(Keys.F12, Settings.Toggle);

			// Render until we drop
			Overlay.RenderLoop();

		}


	}
}
