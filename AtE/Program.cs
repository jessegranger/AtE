using ImGuiNET;
using SharpDX.Windows;
using System;
using System.Drawing;
using System.Linq;
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

			// this background thread runs forever
			var entThread = new Thread(new ThreadStart(EntityCache.MainThread));
			entThread.Start();

			// Render until exit
			Overlay.RenderLoop();

			// Time to cleanup and exit
			Log("Waiting for EntityCache thread to stop...");
			entThread.Join();

		}


	}
}
