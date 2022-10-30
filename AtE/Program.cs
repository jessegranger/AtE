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
	static partial class Program {


		static void Main() {

			// Set up the transparent overly (with Direct3D and ImGui to draw on top of it)
			Overlay.Initialise();

			// Set up the memory hooks
			RunForever("PoEMemory", PoEMemory.OnTick);

			// Enable the Plugin system
			Run(PluginBase.Machine);

			// Enable the Console
			Run(new Console());

			// Render until we drop
			Overlay.RenderLoop();

		}


	}
}
