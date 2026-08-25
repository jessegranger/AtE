using System;
using System.Runtime.InteropServices;
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
	/// The per-layer <c>Initialise</c> calls still need the concrete <see cref="OverlayForm"/> (for
	/// the WinForms input wiring and device association), so this sources it from the sibling
	/// Windows window backend (<see cref="WindowsOverlayWindow.Form"/>) — a within-assembly
	/// shortcut between two Windows-only backends, not a dependency on the portable core.
	/// </summary>
	public sealed class WindowsRenderer : IRenderer {

		public void Initialise(IntPtr windowHandle, int width, int height) {
			// The D3D/ImGui/sprite backends still wire WinForms events to the concrete form, so
			// source it from the sibling Windows window backend (both are Windows-only types).
			OverlayForm form = ((WindowsOverlayWindow)Platform.Current.Window).Form;

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

		/// <summary>
		/// D3D11 samples the BC formats in hardware, so bundle art can be uploaded exactly as it
		/// is stored — no software block decoding on this backend.
		/// </summary>
		public bool SupportsBlockCompressedTextures => true;

		public ITextureHandle CreateTexture(string name, TextureFormat format, int width, int height, byte[] data, int rowPitch) {
			Device device = D3DController.Device;
			if ( device == null ) {
				throw new InvalidOperationException("WindowsRenderer.CreateTexture called before Initialise.");
			}
			if ( data == null ) throw new ArgumentNullException(nameof(data));
			if ( width <= 0 || height <= 0 ) throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}");
			if ( rowPitch <= 0 ) throw new ArgumentOutOfRangeException(nameof(rowPitch));

			Format dxgi = ToDxgiFormat(format);
			if ( dxgi == Format.Unknown ) {
				throw new NotSupportedException($"WindowsRenderer.CreateTexture: no DXGI format for {format}.");
			}

			// D3D reads straight out of the pinned buffer, so a short array would have it walk
			// off the end. Rows are rows of 4x4 blocks for the BC formats.
			bool blockCompressed = DdsImage.IsBlockFormat(format);
			int rows = blockCompressed ? Math.Max(1, (height + 3) / 4) : height;
			long needed = (long)rowPitch * rows;
			if ( data.Length < needed ) {
				throw new ArgumentException(
					$"WindowsRenderer.CreateTexture: {width}x{height} {format} needs {needed} bytes " +
					$"({rowPitch} x {rows}), got {data.Length}.", nameof(data));
			}
			// A block-compressed mip 0 must be a whole number of blocks, or D3D returns E_INVALIDARG.
			if ( blockCompressed && ((width & 3) != 0 || (height & 3) != 0) ) {
				throw new NotSupportedException(
					$"WindowsRenderer.CreateTexture: {format} needs dimensions that are multiples of 4, got {width}x{height}.");
			}

			// Pin the managed bytes just long enough for D3D to copy them into the immutable
			// texture; nothing keeps the pointer past the constructor.
			GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
			try {
				using ( var texture = new Texture2D(device, new Texture2DDescription {
					Width = width,
					Height = height,
					ArraySize = 1,
					BindFlags = BindFlags.ShaderResource,
					Usage = ResourceUsage.Immutable,
					CpuAccessFlags = CpuAccessFlags.None,
					Format = dxgi,
					MipLevels = 1,
					OptionFlags = ResourceOptionFlags.None,
					SampleDescription = new SampleDescription(1, 0)
				}, new DataRectangle(pin.AddrOfPinnedObject(), rowPitch)) ) {
					// The view holds its own reference, so the Texture2D wrapper can go.
					var view = new ShaderResourceView(device, texture);
					TextureCache.Add(string.IsNullOrEmpty(name) ? $"texture_{view.NativePointer:X}" : name, view);
					return new WindowsTextureHandle(view.NativePointer, width, height);
				}
			} finally {
				pin.Free();
			}
		}

		private static Format ToDxgiFormat(TextureFormat format) {
			switch ( format ) {
				case TextureFormat.R8_UNorm: return Format.R8_UNorm;
				case TextureFormat.R8G8B8A8_UNorm: return Format.R8G8B8A8_UNorm;
				case TextureFormat.B8G8R8A8_UNorm: return Format.B8G8R8A8_UNorm;
				case TextureFormat.R16G16B16A16_Float: return Format.R16G16B16A16_Float;
				case TextureFormat.R32G32B32A32_Float: return Format.R32G32B32A32_Float;
				case TextureFormat.BC1_UNorm: return Format.BC1_UNorm;
				case TextureFormat.BC2_UNorm: return Format.BC2_UNorm;
				case TextureFormat.BC3_UNorm: return Format.BC3_UNorm;
				case TextureFormat.BC4_UNorm: return Format.BC4_UNorm;
				case TextureFormat.BC5_UNorm: return Format.BC5_UNorm;
				case TextureFormat.BC6H_UF16: return Format.BC6H_Uf16;
				case TextureFormat.BC7_UNorm: return Format.BC7_UNorm;
				default: return Format.Unknown;
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

		public float GetNextTextOffset(uint id) => ImGuiController.GetNextOffsetForTextAt(id);

		// Draw helpers: delegate to the existing ImGui.NET / sprite backend verbatim.
		public void DrawText(string text, System.Numerics.Vector2 pos, System.Drawing.Color color) =>
			ImGuiController.DrawText(text, pos, color);

		public void DrawTextAt(uint id, System.Numerics.Vector2 pos, string text, System.Drawing.Color color, float lineHeight) =>
			ImGuiController.DrawTextAt(id, pos, text, color, lineHeight);

		public void DrawCircle(System.Numerics.Vector2 pos, float radius, System.Drawing.Color color, int thickness) =>
			ImGuiController.DrawCircle(pos, radius, color, thickness);

		public void DrawFrame(System.Numerics.Vector2 topLeft, System.Numerics.Vector2 bottomRight, System.Drawing.Color color, int thickness) =>
			ImGuiController.DrawFrame(topLeft, bottomRight, color, thickness);

		public void DrawLine(System.Numerics.Vector2 start, System.Numerics.Vector2 end, System.Drawing.Color color, int thickness) =>
			ImGuiController.DrawLine(start, end, color, thickness);

		public void DrawSprite(SpriteIcon icon, System.Drawing.RectangleF rect) =>
			SpriteController.DrawSprite(icon, rect);
	}
}
