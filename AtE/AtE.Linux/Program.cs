using System;

namespace AtE {

	/// <summary>
	/// The Linux host entry point — scaffold. It mirrors <c>AtE.Windows/Program.Main</c>: select
	/// the platform backend at startup, then drive the overlay/memory/plugin loop.
	///
	/// Only the first half is wired today. The orchestration the second half needs — <c>Overlay</c>,
	/// <c>PoEMemory.OnTick</c>, <c>PluginBase.Machine</c>, <c>Console</c>, <c>EntityCache</c>, and the
	/// <c>Run</c>/<c>RunForever</c> helpers — still lives in the <c>AtE.Windows</c> assembly. Making it
	/// run on Linux is blocked on extracting that platform-neutral core into <c>AtE.Core</c> (the
	/// "portable core" listed in PORTING.md "What stays untouched"), and on filling in the five
	/// Linux seam stubs (memory → input → render → window). Until then this composes the platform
	/// and then stops with a clear message rather than pretending to start.
	/// </summary>
	public static class LinuxProgram {

		public static void Main() {

			// Seam #6 composition root: choose the Linux backend once, at the top of Main.
			Platform.Initialise(new LinuxPlatform());

			// --- Intended startup sequence (mirrors AtE.Windows/Program.Main), enabled once the
			//     shared core is extracted into AtE.Core and the Linux seams are implemented: ---
			//
			//   Overlay.Initialise();                                   // transparent X11 overlay + renderer
			//   RunForever("PoEMemory", PoEMemory.OnTick);              // attach + read target memory
			//   Run(PluginBase.Machine);                                // plugin system
			//   Run(new Console());                                     // console
			//   var entThread = new Thread(EntityCache.MainThread);     // entity cache worker
			//   entThread.Start();
			//   Overlay.RenderLoop();                                   // blocking render loop until exit
			//   entThread.Join();
			//
			// These types are currently in AtE.Windows; see PORTING.md step 6 follow-ups.

			throw new NotImplementedException(
				"AtE.Linux host is a scaffold: the platform composition root is wired " +
				"(Platform.Current = LinuxPlatform), but the shared orchestration core has not yet " +
				"been extracted into AtE.Core and the Linux seam backends are stubs. See PORTING.md step 6.");
		}
	}
}
