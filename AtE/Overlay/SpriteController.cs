using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SharpDX.Mathematics;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Format = SharpDX.DXGI.Format;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using static AtE.Globals;
using SharpDX.WIC;
using SharpDX;
using System.Drawing;
using ImGuiNET;

namespace AtE {
	public static partial class Globals {
		public static void DrawSprite(SpriteIcon icon, Vector2 pos, float w, float h) =>
			SpriteController.DrawSprite(icon, new RectangleF(pos.X - (w/2), pos.Y - (h/2), w, h));

	}
	public static class SpriteController {

		public const int MaxSprites = 2000;

		public static bool Enabled = true;

		[StructLayout(LayoutKind.Sequential)]
		private readonly struct Vertex {
			public readonly Vector2 Position;
			public readonly Vector2 TexC;
			public readonly uint Color; // from ToRGBA
			public Vertex(Vector2 p, Vector2 uv, uint c) {
				Position = p;
				TexC = uv;
				Color = c;
			}
		}

		private static OverlayForm RenderForm;
		private static Device Device;

		private static ImagingFactory2 imagingFactory;

		private static BlendState blendState;
		private static DepthStencilState depthStencilState;
		private static VertexShader vertexShader;
		private static PixelShader pixelShader;
		private static SamplerState samplerState;
		private static RasterizerState solidRasterState;
		private static ShaderResourceView mainTexture;
		private static Buffer ConstantBuffer;
		private static Buffer VertexBuffer;
		private static Buffer IndexBuffer;
		private static InputLayout inputLayout;

		private static Vertex[] Vertices = new Vertex[MaxSprites * 4];
		private static int[] Indices = new int[MaxSprites * 6];


		private static int sizeOfVertexBuffer = MaxSprites * Utilities.SizeOf<Vertex>() * 4;
		private static int sizeOfIndexBuffer = MaxSprites * Utilities.SizeOf<int>() * 6;


		// these are reset each frame, and track how much of each buffer to draw in one frame
		private static int Frame_SpriteCount = 0;

		public static void Initialise(OverlayForm form) {
			RenderForm = form;
			Device = D3DController.Device;

			Log($"Sprites: Compiling shaders...");
			CompilationResult vertexShaderByteCode = ShaderBytecode.Compile(VertexShaderSource, "VS", "vs_4_0");
			vertexShader = new VertexShader(Device, vertexShaderByteCode);
			CompilationResult pixelShaderByteCode = ShaderBytecode.Compile(PixelShaderSource, "PS", "ps_4_0");
			pixelShader = new PixelShader(Device, pixelShaderByteCode);

			Log($"Sprites: Allocating GPU buffers...");
			VertexBuffer = new Buffer(Device, new BufferDescription() {
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.VertexBuffer,
				OptionFlags = ResourceOptionFlags.None,
				CpuAccessFlags = CpuAccessFlags.Write,
				SizeInBytes = sizeOfVertexBuffer * 4
			});

			IndexBuffer = new Buffer(Device, new BufferDescription() {
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.IndexBuffer,
				OptionFlags = ResourceOptionFlags.None,
				CpuAccessFlags = CpuAccessFlags.Write,
				SizeInBytes = sizeOfIndexBuffer * 4
			});

			ConstantBuffer = new Buffer(Device, new BufferDescription() {
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.ConstantBuffer,
				OptionFlags = ResourceOptionFlags.None,
				CpuAccessFlags = CpuAccessFlags.Write,
				SizeInBytes = Marshal.SizeOf<Vector2>() * 2
			});

			Log($"Sprites: Defining input layout...");
			inputLayout = new InputLayout(Device, ShaderSignature.GetInputSignature(vertexShaderByteCode), new InputElement[] {
				new InputElement {
					SemanticName = "POSITION",
					SemanticIndex = 0,
					Format = Format.R32G32_Float,
					Slot = 0,
					AlignedByteOffset = 0,
					Classification = InputClassification.PerVertexData,
					InstanceDataStepRate = 0
				},
				new InputElement {
					SemanticName = "TEXCOORD",
					SemanticIndex = 0,
					Format = Format.R32G32_Float,
					Slot = 0,
					AlignedByteOffset = InputElement.AppendAligned,
					Classification = InputClassification.PerVertexData,
					InstanceDataStepRate = 0
				},
				new InputElement {
					SemanticName = "COLOR",
					SemanticIndex = 0,
					Format = Format.R8G8B8A8_UNorm,
					Slot = 0,
					AlignedByteOffset = InputElement.AppendAligned,
					Classification = InputClassification.PerVertexData,
					InstanceDataStepRate = 0
				}
			});

			Log($"Sprites: Creating sampler state...");
			samplerState = new SamplerState(Device, new SamplerStateDescription() {
				AddressU = TextureAddressMode.Clamp,
				AddressV = TextureAddressMode.Clamp,
				AddressW = TextureAddressMode.Wrap
			});

			Log($"Sprites: Creating blend state...");
			var blendStateDesc = new BlendStateDescription {
				AlphaToCoverageEnable = false
			};
			blendStateDesc.RenderTarget[0].IsBlendEnabled = true;
			blendStateDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
			blendStateDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
			blendStateDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
			blendStateDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
			blendStateDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.One;
			blendStateDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
			blendStateDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
			blendState = new BlendState(Device, blendStateDesc);

			Log($"Sprites: Creating depth stencil state...");
			depthStencilState = new DepthStencilState(Device, new DepthStencilStateDescription {
				IsDepthEnabled = false,
				IsStencilEnabled = false,
				DepthWriteMask = DepthWriteMask.All,
				DepthComparison = Comparison.Always,
				FrontFace = {
					FailOperation = StencilOperation.Keep,
					DepthFailOperation = StencilOperation.Keep,
					PassOperation = StencilOperation.Keep,
					Comparison = Comparison.Always
				},
				BackFace = {
					FailOperation = StencilOperation.Keep,
					DepthFailOperation = StencilOperation.Keep,
					PassOperation = StencilOperation.Keep,
					Comparison = Comparison.Always
				}
			});

			Log($"Sprites: Creating rasterizer...");
			solidRasterState = new RasterizerState(Device, new RasterizerStateDescription() {
				FillMode = FillMode.Solid,
				CullMode = CullMode.None,
				IsScissorEnabled = true,
				IsDepthClipEnabled = true
			});

			Resize(RenderForm.Width, RenderForm.Height);

			vertexShaderByteCode.Dispose();
			pixelShaderByteCode.Dispose();

			Log($"Sprites: Reading sprites.png...");
			imagingFactory = new ImagingFactory2();

			// build a few pieces that will stream our PNG file in as a texture
			var formatConverter = new FormatConverter(imagingFactory);
			formatConverter.Initialize(
				new BitmapDecoder(imagingFactory, @"Static\Sprites.png", DecodeOptions.CacheOnDemand).GetFrame(0),
				PixelFormat.Format32bppPRGBA, BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

			var stride = formatConverter.Size.Width * 4;
			using ( var buffer = new DataStream(formatConverter.Size.Height * stride, true, true) ) {
				// convert the file bytes into texture format in a buffer
				formatConverter.CopyPixels(stride, buffer);

				Log($"Sprites: Adding texture to cache...");
				// copy that buffer into the GPU and get a texture id and store it
				TextureCache.Add("SpriteController_MainTexture", mainTexture = new ShaderResourceView(Device, new Texture2D(Device, new Texture2DDescription() {
					Width = formatConverter.Size.Width,
					Height = formatConverter.Size.Height,
					ArraySize = 1,
					BindFlags = BindFlags.ShaderResource,
					Usage = ResourceUsage.Immutable,
					CpuAccessFlags = CpuAccessFlags.None,
					Format = Format.R8G8B8A8_UNorm,
					MipLevels = 1,
					OptionFlags = ResourceOptionFlags.None,
					SampleDescription = new SampleDescription(1, 0)
				}, new DataRectangle(buffer.DataPointer, stride))));
				Log($"Sprites: Main Texture = {mainTexture.DebugName}");

			}

		}

		internal static void Resize(int w, int h) {
			Log($"Sprites: Updating constant buffer: {w} x {h}");
			Device.ImmediateContext.MapSubresource(ConstantBuffer, MapMode.WriteDiscard, MapFlags.None, out var buffer);
			buffer.Write(new Vector2(w, h));
			Device.ImmediateContext.UnmapSubresource(ConstantBuffer, 0);
		}
		
		internal static void NewFrame(long dt) {
			Frame_SpriteCount = 0;
			Array.Clear(Vertices, 0, Vertices.Length);
			Array.Clear(Indices, 0, Indices.Length);
		}

		public static void DrawSprite(SpriteIcon icon, RectangleF pos) {
			// TODO: resize buffers if needed

			if( Frame_SpriteCount >= MaxSprites - 1 ) {
				Log("Ignoring attempt to draw too many sprites.");
				return;
			}

			var uv = GetUV(icon);
			var v = Frame_SpriteCount * 4;
			var i = Frame_SpriteCount * 6;
			uint white = (uint)ToRGBA(Color.White);
			Vertices[v] = new Vertex( 
				new Vector2(pos.X, pos.Y), // to screen, top left
				new Vector2(uv.X, uv.Y), // from texture, top left
				white
			);
			Vertices[v + 1] = new Vertex( 
				new Vector2(pos.X + pos.Width, pos.Y), // to screen, top right
				new Vector2(uv.X + uv.Width, uv.Y), // from texture, top right
				white
			);
			Vertices[v + 2] = new Vertex( 
				new Vector2(pos.X, pos.Y + pos.Height), // to screen, bottom left
				new Vector2(uv.X, uv.Y + uv.Height), // from texture, bottom left
				white
			);
			Vertices[v + 3] = new Vertex( 
				new Vector2(pos.X + pos.Width, pos.Y + pos.Height), // to screen, bottom right
				new Vector2(uv.X + uv.Width, uv.Y + uv.Height), // from texture, bottom right
				white
			);
			Indices[i] = v;
			Indices[i + 1] = v + 1; // draw the square using two triangles
			Indices[i + 2] = v + 2; // that overlap on index 2
			Indices[i + 3] = v + 2;
			Indices[i + 4] = v + 1;
			Indices[i + 5] = v + 3;
			Frame_SpriteCount += 1;
		}

		internal static void Render(long dt) {
			if ( !Enabled ) return;

			var context = Device.ImmediateContext;

			// set up this layer's rendering states in the device context
			context.OutputMerger.SetBlendState(blendState);
			context.OutputMerger.SetDepthStencilState(depthStencilState);
			context.Rasterizer.State = solidRasterState;

			context.InputAssembler.InputLayout = inputLayout;
			context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding {
				Buffer = VertexBuffer, Stride = Utilities.SizeOf<Vertex>(), Offset = 0
			});
			context.InputAssembler.SetIndexBuffer(IndexBuffer, Format.R32_UInt, 0);
			context.VertexShader.Set(vertexShader);
			context.PixelShader.Set(pixelShader);
			context.VertexShader.SetConstantBuffer(0, ConstantBuffer);
			context.PixelShader.SetSampler(0, samplerState);

			Utilities.Write(
				context.MapSubresource(VertexBuffer, 0, MapMode.WriteDiscard, MapFlags.None).DataPointer,
				Vertices, 0, Frame_SpriteCount * 4);
			Utilities.Write(
				context.MapSubresource(IndexBuffer, 0, MapMode.WriteDiscard, MapFlags.None).DataPointer,
				Indices, 0, Frame_SpriteCount * 6);
			context.UnmapSubresource(VertexBuffer, 0);
			context.UnmapSubresource(IndexBuffer, 0);

			// every DrawImage command adds 6 indices to be drawn
			for(int i = 0; i < Frame_SpriteCount; i++) {
				context.PixelShader.SetShaderResource(0, mainTexture);
				context.DrawIndexed(6, i * 6, 0);
			}

		}

		// dimensions of the sprites file
		private const int spriteTilesWidth = 7; // there are 7 sprites wide (each 64 pixels)
		private const int spriteTilesHeight = 8; // there are 8 rows of sprites

		/// <summary>
		/// Get uv coordinates (all in range [0-1]) for a portion of the mainTexture
		/// </summary>
		/// <param name="icon"></param>
		/// <returns></returns>
		private static RectangleF GetUV(SpriteIcon icon) => GetUV((int)icon - 1);
		private static RectangleF GetUV(int iconIndex) {
			float x = iconIndex % spriteTilesWidth;
			float y = iconIndex / spriteTilesWidth;
			return new RectangleF(x / spriteTilesWidth, y / spriteTilesHeight,
				1f / spriteTilesWidth, 1f / spriteTilesHeight);
		}

		// A map of CustomMapIcons.png
		// Each sprite is 64 x 64 pixels in the file.

		private static string VertexShaderSource = @"
cbuffer ConstBuffer { float2 windowSize; };
struct VertexInputType {
	float2 position : POSITION;
	float2 tex : TEXCOORD;
	float4 color: COLOR;
};
struct PixelInputType {
	float4 position : SV_POSITION;
	float4 color: COLOR0;
	float2 tex : TEXCOORD0;
};
PixelInputType VS(VertexInputType input) {
	PixelInputType output;
	// Calculate the position of the vertex monitor coord.
	output.position.x = 2.0f * (input.position.x) /(windowSize.x)-1;// (windowSize.x) - 1;
	output.position.y = -2.0f * (input.position.y) /(windowSize.y)+1;// (windowSize.y) +1;
	output.position.w = 1.0f;
	output.position.z = 1.0f;
  output.color = input.color;
	// Store the texture coordinates for the pixel shader to use.
	output.tex = input.tex;
	return output;
}";

		private static string PixelShaderSource = @"
Texture2D shaderTexture;
SamplerState SampleType;

struct PixelInputType {
	float4 position : SV_POSITION;
	float4 color: COLOR0;
	float2 tex : TEXCOORD0;
};

float4 PS(PixelInputType input) : SV_TARGET {
	float4 textureColor;
	textureColor = input.color * shaderTexture.Sample(SampleType, input.tex);
	return textureColor;
}";

	}
		public enum SpriteIcon {
			None = 0,
			Rings = 1,
			Armour,
			Gear,
			Weapon,
			Map,
			Gem,
			Skull,
			Chest,
			RedDotShadow,
			BlackDotShadow,
			WhiteDotShadow,
			BlueTexturedDot,
			BlueTexturedEllipse,
			GreenTexturedDot,
			GreenTexturedEllipse,
			RedTexturedDot,
			RedTexturedEllipse,
			WhiteTexturedDot,
			WhiteTexturedEllipse,
			OrangeWithBlack,
			AquaWithBlack,
			GreenWithBlack,
			GrayWithBlack,
			YellowWithBlack,
			YellowDot,
			YellowWithBorder,
			YellowWithBorderAndGrayDot,
			BlueDot,
			BlueWithBorderAndGrayDot,
			PurpleDot,
			PurpleWithBorder,
			PurpleWithBorderAndGrayDot,
			OrangeDot,
			OrangeWithBorder,
			GreenDot,
			GreenWithBorder,
			AquaWithBorder,
			CyanWithBorder,
			LightPurpleWithBorder,
			WhiteWithBorder,
			RedDot,
			RedWithBorder,
			RedWithBorderAndGrayDot,
			RGBDot,
			CelestialDot,
			LightDot,
			BloodDot,
			DarkRedBlack,
			Ornate,
			Arcanist

		}
}
