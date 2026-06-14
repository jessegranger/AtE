namespace AtE {

	/// <summary>
	/// The Windows <see cref="IPlatform"/> composition root. It constructs and owns one instance
	/// of each Windows seam backend; everything else reaches them through
	/// <see cref="Platform.Current"/>. This replaces the old transitional static composition
	/// points (the former <c>Input</c>/<c>Rendering</c>/<c>Windowing</c> statics and the
	/// <c>PoEMemory.Memory = new WindowsProcessMemory()</c> field), which have been removed;
	/// <see cref="PoEMemory.Memory"/> is now a thin accessor onto <see cref="Memory"/>.
	///
	/// <c>Program.Main</c> passes one of these to <see cref="Platform.Initialise"/> at startup.
	/// </summary>
	public sealed class WindowsPlatform : IPlatform {
		public IProcessMemory Memory { get; } = new WindowsProcessMemory();
		public IInputSink Input { get; } = new WindowsInputSink();
		public IInputSource Keys { get; } = new WindowsInputSource();
		public IOverlayWindow Window { get; } = new WindowsOverlayWindow();
		public IRenderer Renderer { get; } = new WindowsRenderer();
	}
}
