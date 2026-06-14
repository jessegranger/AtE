using System;

namespace AtE {

	/// <summary>
	/// A platform-neutral handle to a GPU texture. The numeric <see cref="Id"/> is what the
	/// portable ImGui draw lists carry as their <c>TextureId</c>; each backend maps it back to
	/// its own resource (on Windows, the native pointer of a SharpDX <c>ShaderResourceView</c>).
	/// </summary>
	public interface ITextureHandle {

		/// <summary>The backend-native id ImGui stores as a draw command's TextureId.</summary>
		IntPtr Id { get; }

		int Width { get; }
		int Height { get; }
	}

	/// <summary>
	/// The GPU device + ImGui backend: the part of the overlay's rendering that uploads vertex
	/// buffers and textures and presents to the screen. The plugin draw calls themselves are
	/// ImGui (<c>ImGui.*</c>) and the small <c>DrawText</c>/<c>DrawSprite</c> helpers built on
	/// top of them are portable, so they sit <em>above</em> this seam and are untouched — only
	/// the backend that turns the resulting draw lists into GPU work lives here.
	///
	/// Windows impl: <c>WindowsRenderer</c>, wrapping the existing SharpDX D3D11 +
	/// ImGui.NET backend (<c>D3DController</c> / <c>ImGuiController</c> / <c>SpriteController</c> /
	/// <c>TextureCache</c>). Linux impl (TODO): a Vulkan/OpenGL device via Veldrid or Silk.NET
	/// with the matching ImGui backend.
	/// </summary>
	public interface IRenderer {

		/// <summary>
		/// Create the device and the ImGui/sprite backends for the given native window.
		///
		/// Takes a raw handle + size rather than the (not-yet-built) <c>IOverlayWindow</c> from
		/// PORTING.md step 5; once that seam lands this becomes <c>Initialise(IOverlayWindow)</c>.
		/// </summary>
		void Initialise(IntPtr windowHandle, int width, int height);

		/// <summary>Begin a frame: clear the target and start new ImGui/sprite draw lists.</summary>
		void BeginFrame(long dt);

		/// <summary>End a frame: render the accumulated draw lists to the GPU and present.</summary>
		void EndFrame();

		/// <summary>
		/// Resize the swap chain / viewport. <paramref name="left"/>/<paramref name="top"/> are
		/// carried because the D3D swap-chain bounds track the window's screen position.
		/// </summary>
		void Resize(int left, int top, int width, int height);

		/// <summary>Upload an image file to the GPU and register it for use as an ImGui texture.</summary>
		ITextureHandle LoadTexture(string path);

		/// <summary>
		/// Enable/disable the content layers (ImGui + sprites). Toggled by the focus logic so
		/// that, when configured, nothing is drawn while the game is not focused.
		/// </summary>
		bool ContentEnabled { set; }

		/// <summary>Enable/disable the present/swap step (the layer that blocks on vsync).</summary>
		bool PresentEnabled { set; }

		/// <summary>Clear the render target to transparent (used to blank a stale frame).</summary>
		void Clear();
	}
}
