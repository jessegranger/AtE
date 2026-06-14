namespace AtE {

	/// <summary>
	/// The transitional composition point for the input seams, mirroring
	/// <see cref="PoEMemory.Memory"/>. Hotkeys and input-sending code reach the OS only
	/// through <see cref="Sink"/> / <see cref="Source"/>.
	///
	/// Defaults to the Windows implementations; a future IPlatform composition root
	/// (PORTING.md step 6) will pick the backends at startup instead.
	/// </summary>
	public static class Input {
		public static IInputSink Sink = new WindowsInputSink();
		public static IInputSource Source = new WindowsInputSource();
	}
}
