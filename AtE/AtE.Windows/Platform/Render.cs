namespace AtE {

	/// <summary>
	/// The transitional composition point for the rendering seam, mirroring
	/// <see cref="Input"/> and <see cref="PoEMemory.Memory"/>. The overlay's render loop and the
	/// focus-based draw gating reach the GPU/ImGui backend only through <see cref="Renderer"/>.
	///
	/// (Named <c>Rendering</c> rather than <c>Render</c> to avoid colliding with the
	/// <c>Render</c> entity component.)
	///
	/// Defaults to the Windows implementation; the future IPlatform composition root
	/// (PORTING.md step 6) will pick the backend at startup instead.
	/// </summary>
	public static class Rendering {
		public static IRenderer Renderer = new WindowsRenderer();
	}
}
