using System;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.WIC;
using static AtE.Globals;
using Device = SharpDX.Direct3D11.Device;
using Format = SharpDX.DXGI.Format;

namespace AtE {

	/// <summary>
	/// A SharpDX-backed <see cref="ITextureHandle"/>. The <see cref="Id"/> is the native
	/// pointer of the underlying <c>ShaderResourceView</c> — the value ImGui carries as a draw
	/// command's TextureId and that <see cref="TextureCache"/> resolves back to the view.
	/// </summary>
	internal sealed class WindowsTextureHandle : ITextureHandle {
		public IntPtr Id { get; }
		public int Width { get; }
		public int Height { get; }
		public WindowsTextureHandle(IntPtr id, int width, int height) {
			Id = id;
			Width = width;
			Height = height;
		}
	}

	/// <summary>
	/// The Windows implementation of <see cref="IRenderer"/>. It does not contain any GPU code
	/// itself — it composes the existing SharpDX Direct3D 11 + ImGui.NET backend
	/// (<see cref="D3DController"/>, <see cref="ImGuiController"/>, <see cref="SpriteController"/>,
	/// <see cref="TextureCache"/>), which are kept verbatim as the first implementation.
	///
	/// Until the <c>IOverlayWindow</c> seam (PORTING.md step 5) exists, the per-layer
	/// <c>Initialise</c> calls still need the concrete <see cref="OverlayForm"/> (for the WinForms
	/// input wiring and device association), so this reaches <see cref="Overlay.RenderForm"/>
	/// directly — the same transitional shortcut <see cref="WindowsInputSource"/> takes.
	/// </summary>
	public sealed class WindowsRenderer : IRenderer {

		public void Initialise(IntPtr windowHandle, int width, int height) {
			OverlayForm form = Overlay.RenderForm;

			// D3D first (it creates the device the other two layers borrow), then the
			// ImGui and sprite backends.
			D3DController.Initialise(form);
			ImGuiController.Initialise(form);
			SpriteController.Initialise(form);
		}

		public void BeginFrame(long dt) {
			// Clear the prior frame, then open fresh ImGui + sprite draw lists.
			D3DController.NewFrame();
			ImGuiController.NewFrame(dt);
			SpriteController.NewFrame(dt);
		}

		public void EndFrame() {
			// Turn the accumulated draw lists into GPU work, sprites under ImGui, then present.
			SpriteController.Render(0);
			ImGuiController.Render(0);
			using ( Perf.Section("VSync") ) {
				// The present blocks on vsync; keep it in its own perf section as before.
				D3DController.Render();
			}
		}

		public void Resize(int left, int top, int width, int height) {
			D3DController.Resize(left, top, width, height);
			ImGuiController.Resize(width, height);
			SpriteController.Resize(width, height);
		}

		public ITextureHandle LoadTexture(string path) {
			Device device = D3DController.Device;
			if ( device == null ) {
				throw new InvalidOperationException("WindowsRenderer.LoadTexture called before Initialise.");
			}

			// Same WIC path SpriteController uses for Sprites.png: decode -> 32bpp PRGBA ->
			// upload as an immutable Texture2D -> wrap in a ShaderResourceView.
			using ( var imagingFactory = new ImagingFactory2() )
			using ( var decoder = new BitmapDecoder(imagingFactory, path, DecodeOptions.CacheOnDemand) )
			using ( var frame = decoder.GetFrame(0) )
			using ( var formatConverter = new FormatConverter(imagingFactory) ) {
				formatConverter.Initialize(frame, PixelFormat.Format32bppPRGBA,
					BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

				int w = formatConverter.Size.Width;
				int h = formatConverter.Size.Height;
				int stride = w * 4;
				using ( var buffer = new DataStream(h * stride, true, true) ) {
					formatConverter.CopyPixels(stride, buffer);
					var view = new ShaderResourceView(device, new Texture2D(device, new Texture2DDescription {
						Width = w,
						Height = h,
						ArraySize = 1,
						BindFlags = BindFlags.ShaderResource,
						Usage = ResourceUsage.Immutable,
						CpuAccessFlags = CpuAccessFlags.None,
						Format = Format.R8G8B8A8_UNorm,
						MipLevels = 1,
						OptionFlags = ResourceOptionFlags.None,
						SampleDescription = new SampleDescription(1, 0)
					}, new DataRectangle(buffer.DataPointer, stride)));
					TextureCache.Add(path, view);
					return new WindowsTextureHandle(view.NativePointer, w, h);
				}
			}
		}

		public bool ContentEnabled {
			set {
				ImGuiController.Enabled = value;
				SpriteController.Enabled = value;
			}
		}

		public bool PresentEnabled {
			set => D3DController.Enabled = value;
		}

		public void Clear() => D3DController.Clear();
	}
}
