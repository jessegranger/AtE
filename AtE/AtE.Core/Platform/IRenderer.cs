using System;
using System.Drawing;
using System.Numerics;

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
	/// A portable subset of the GPU pixel formats, so the core can hand a decoded texture to a
	/// backend without naming DXGI or GL types. Backends map these onto their own enums; the
	/// BC* members are the block-compressed families that PoE's .dds art uses, which GPUs
	/// sample natively (see <see cref="IRenderer.SupportsBlockCompressedTextures"/>).
	/// </summary>
	public enum TextureFormat {
		Unknown = 0,
		R8_UNorm,
		R8G8B8A8_UNorm,
		B8G8R8A8_UNorm,
		R16G16B16A16_Float,
		R32G32B32A32_Float,
		BC1_UNorm,
		BC2_UNorm,
		BC3_UNorm,
		BC4_UNorm,
		BC5_UNorm,
		BC6H_UF16,
		BC7_UNorm,
	}

	/// <summary>
	/// The GPU device + ImGui backend: the part of the overlay's rendering that uploads vertex
	/// buffers and textures and presents to the screen. The plugin draw calls themselves are
	/// ImGui (<c>ImGui.*</c>); the small <c>DrawText</c>/<c>DrawSprite</c>/... helpers are exposed
	/// to the portable core as the <c>Draw*</c> members below. They are thin — a backend's text/
	/// line/frame draws are just ImGui draw-list calls — but they depend on the backend's per-frame
	/// draw-list and text-offset state (and, for sprites, on real GPU work), so they route through
	/// this seam rather than living in the portable <c>Globals</c>. The portable wrappers in
	/// <c>Globals</c> (overloads, bottom-left stacking, <c>ToRGBA</c>) sit above this seam.
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
		/// Upload already-decoded texture data (one mip level) and register it as an ImGui
		/// texture. Used by the bundle browser to preview game art straight out of the GGPK
		/// without going through a file on disk.
		///
		/// <paramref name="rowPitch"/> is bytes per row — for block-compressed formats, per row
		/// of 4x4 blocks (<see cref="DdsImage.RowPitch"/> computes it). <paramref name="name"/>
		/// keys the backend's texture cache, so uploading the same name again replaces the
		/// previous texture.
		/// </summary>
		ITextureHandle CreateTexture(string name, TextureFormat format, int width, int height, byte[] data, int rowPitch);

		/// <summary>
		/// True if <see cref="CreateTexture"/> accepts the BC* formats directly. Backends that
		/// return false need the caller to decode to <see cref="TextureFormat.R8G8B8A8_UNorm"/>
		/// first (see <see cref="DdsImage.TryDecodeToRgba"/>).
		/// </summary>
		bool SupportsBlockCompressedTextures { get; }

		/// <summary>
		/// Enable/disable the content layers (ImGui + sprites). Toggled by the focus logic so
		/// that, when configured, nothing is drawn while the game is not focused.
		/// </summary>
		bool ContentEnabled { set; }

		/// <summary>Enable/disable the present/swap step (the layer that blocks on vsync).</summary>
		bool PresentEnabled { set; }

		/// <summary>Clear the render target to transparent (used to blank a stale frame).</summary>
		void Clear();

		/// <summary>
		/// The vertical offset (in pixels) at which the next line of stacked bottom-left text with
		/// the given id should be drawn, tracked by the ImGui backend across the frame. Portable
		/// callers (the bottom-left text/notify helpers) use this to stack lines without knowing
		/// the backend. Backends with no such state return 0.
		/// </summary>
		float GetNextTextOffset(uint id);

		/// <summary>Draw a single line of <paramref name="text"/> at a screen position.</summary>
		void DrawText(string text, Vector2 pos, Color color);

		/// <summary>
		/// Draw a line of <paramref name="text"/> at <paramref name="pos"/> for stack <paramref name="id"/>,
		/// advancing that stack's running offset by <paramref name="lineHeight"/> (negative grows upward)
		/// so successive calls with the same id stack. See <see cref="GetNextTextOffset"/>.
		/// </summary>
		void DrawTextAt(uint id, Vector2 pos, string text, Color color, float lineHeight);

		/// <summary>Draw a circle outline.</summary>
		void DrawCircle(Vector2 pos, float radius, Color color, int thickness);

		/// <summary>Draw a rectangle outline between two corners.</summary>
		void DrawFrame(Vector2 topLeft, Vector2 bottomRight, Color color, int thickness);

		/// <summary>Draw a line segment.</summary>
		void DrawLine(Vector2 start, Vector2 end, Color color, int thickness);

		/// <summary>Draw a sprite from the atlas filling the given screen rectangle.</summary>
		void DrawSprite(SpriteIcon icon, RectangleF rect);
	}
}
