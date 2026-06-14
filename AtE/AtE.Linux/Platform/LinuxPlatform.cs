namespace AtE {

	/// <summary>
	/// The Linux <see cref="IPlatform"/> composition root. It owns one instance of each Linux
	/// seam backend (currently the stubs from PORTING.md step 6) and hands them out. Unlike
	/// <c>WindowsPlatform</c>, there are no transitional statics to mirror on Linux, so this
	/// class constructs and holds the backends directly — the shape the Windows side will
	/// converge on once its statics are deleted.
	///
	/// <c>Program.Main</c> (AtE.Linux) passes one of these to <see cref="Platform.Initialise"/>.
	/// </summary>
	public sealed class LinuxPlatform : IPlatform {
		public IProcessMemory Memory { get; } = new LinuxProcessMemory();
		public IInputSink Input { get; } = new LinuxInputSink();
		public IInputSource Keys { get; } = new LinuxInputSource();
		public IOverlayWindow Window { get; } = new LinuxOverlayWindow();
		public IRenderer Renderer { get; } = new LinuxRenderer();
	}
}
