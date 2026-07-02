using System;
using System.Threading;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Linux host entry point. It mirrors <c>AtE.Windows/Program.Main</c>: select the platform
	/// backend at startup (seam #6 composition root), then drive the shared overlay/memory/plugin
	/// loop that now lives in <c>AtE.Core</c>.
	/// </summary>
	public static class LinuxProgram {

		public static void Main() {

			// Seam #6 composition root: choose the Linux backend once, at the top of Main.
			Platform.Initialise(new LinuxPlatform());

			// Set up the transparent overlay (with the GLX/OpenGL renderer + ImGui to draw on top of it).
			Overlay.Initialise();

			// Set up the memory hooks (attach + read target memory).
			RunForever("PoEMemory", PoEMemory.OnTick);

			// Enable the Plugin system.
			Run(PluginBase.Machine);

			// Enable the Console.
			Run(new Console());

			// This background thread runs forever (entity cache worker).
			var entThread = new Thread(new ThreadStart(EntityCache.MainThread));
			entThread.Start();

			// Render until exit.
			Overlay.RenderLoop();

			// Time to cleanup and exit.
			Log("Waiting for EntityCache thread to stop...");
			entThread.Join();
		}
	}
}
