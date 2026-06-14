using System;

namespace AtE {

	/// <summary>
	/// A Linux-backed <see cref="ITextureHandle"/> — stub. On Linux the <see cref="Id"/> would
	/// be whatever the chosen GPU layer hands ImGui as a draw command's TextureId (e.g. a
	/// Veldrid/Silk.NET texture binding mapped to an IntPtr).
	/// </summary>
	internal sealed class LinuxTextureHandle : ITextureHandle {
		public IntPtr Id { get; }
		public int Width { get; }
		public int Height { get; }
		public LinuxTextureHandle(IntPtr id, int width, int height) {
			Id = id;
			Width = width;
			Height = height;
		}
	}

	/// <summary>
	/// Linux implementation of <see cref="IRenderer"/> (GPU device + ImGui backend) — stub.
	///
	/// Target design (PORTING.md cheat-sheet + step 4 follow-up): a Vulkan or OpenGL device via
	/// a cross-platform GPU layer (Veldrid or Silk.NET) with the matching ImGui backend. The
	/// portable ImGui *draw* helpers (DrawText/DrawSprite/...) sit above this seam and are
	/// shared; only the upload/present backend lives here. Unlike the Windows backend (which
	/// reaches the concrete OverlayForm), a Linux renderer binds to the native
	/// <see cref="IOverlayWindow.Handle"/> only.
	/// </summary>
	public sealed class LinuxRenderer : IRenderer {

		private const string NYI = "LinuxRenderer is not yet implemented (PORTING.md step 6).";

		public void Initialise(IntPtr windowHandle, int width, int height) =>
			throw new NotImplementedException(NYI + " TODO: create a Vulkan/OpenGL device (Veldrid/Silk.NET) bound to windowHandle and init the ImGui backend.");

		public void BeginFrame(long dt) =>
			throw new NotImplementedException(NYI + " TODO: clear the target and start new ImGui/sprite draw lists.");

		public void EndFrame() =>
			throw new NotImplementedException(NYI + " TODO: render the accumulated draw data and present the swap chain.");

		public void Resize(int left, int top, int width, int height) =>
			throw new NotImplementedException(NYI + " TODO: resize the swap chain / viewport.");

		public ITextureHandle LoadTexture(string path) =>
			throw new NotImplementedException(NYI + " TODO: decode the image, upload it to the GPU, and wrap it as a LinuxTextureHandle.");

		public bool ContentEnabled {
			set => throw new NotImplementedException(NYI + " TODO: gate the ImGui/sprite content layers.");
		}

		public bool PresentEnabled {
			set => throw new NotImplementedException(NYI + " TODO: gate the present/vsync step.");
		}

		public void Clear() =>
			throw new NotImplementedException(NYI + " TODO: clear the render target to transparent.");

		// A layout query that should degrade gracefully rather than throw: with no ImGui text
		// state yet, there is no stacked offset, so report 0 (same as the Windows backend when
		// the id is unknown).
		public float GetNextTextOffset(uint id) => 0f;
	}
}
