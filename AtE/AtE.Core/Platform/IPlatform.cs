namespace AtE {

	/// <summary>
	/// Seam #6 from PORTING.md: the composition root. It hands out the one chosen
	/// implementation of each platform seam so the rest of the app reaches OS services only
	/// through these properties and never constructs a concrete backend itself.
	///
	/// One concrete <see cref="IPlatform"/> is selected once at startup
	/// (<c>WindowsPlatform</c> / <c>LinuxPlatform</c>) and stored on <see cref="Platform.Current"/>.
	///
	/// This is the eventual replacement for the transitional, hard-coded static composition
	/// points introduced in steps 2–5 (<c>PoEMemory.Memory</c>, <c>Input.Sink</c>/<c>Source</c>,
	/// <c>Rendering.Renderer</c>, <c>Windowing.Window</c>). During the scaffold phase those
	/// statics still exist and the Windows backend simply exposes the same instances through
	/// here; rerouting every caller onto <see cref="Platform.Current"/> and deleting the
	/// statics is the follow-up.
	/// </summary>
	public interface IPlatform {

		/// <summary>Process attach + target-memory reads (<c>process_vm_readv</c> / ReadProcessMemory).</summary>
		IProcessMemory Memory { get; }

		/// <summary>Synthetic input <em>into</em> the game (SendInput / uinput).</summary>
		IInputSink Input { get; }

		/// <summary>The polling side of input: hotkeys, cursor, foreground (GetAsyncKeyState / XQuery*).</summary>
		IInputSource Keys { get; }

		/// <summary>The transparent, click-through, topmost overlay window.</summary>
		IOverlayWindow Window { get; }

		/// <summary>The GPU device + ImGui backend.</summary>
		IRenderer Renderer { get; }
	}
}
