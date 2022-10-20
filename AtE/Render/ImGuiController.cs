using ImGuiNET;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace AtE {
	public static partial class Globals {

		public static void DrawText(string text, Vector2 pos, Color color) => ImGuiController.DrawText(text, pos, color);

		public static void DrawCircle(Vector2 pos, float radius, Color color) => ImGuiController.DrawCircle(pos, radius, color);

		public static void DrawFrame(Vector2 topLeft, Vector2 bottomRight, Color color) => ImGuiController.DrawFrame(topLeft, bottomRight, color);

		public static int ToRBGA(Color color) => (color.R << 24) | (color.B << 16) | (color.G << 8) | color.A;
		public static int ToRGBA(Color color) => (color.R << 24) | (color.G << 16) | (color.B << 8) | color.A;
	}

	internal static class ImGuiController {

		private static IntPtr Context;
		private static ImGuiIOPtr IO;
		private static InputLayout inputLayout;
		private static SamplerState imGuiSamplerState;
		private static RasterizerState imGuiSolidRasterState;
		private static BlendState imGuiBlendState;
		private static DepthStencilState imGuiDepthStencilState;
		private static VertexBufferBinding imGuiVertexBufferBinding;
		private static VertexShader imGuiVertexShader;
		private static PixelShader imGuiPixelShader;
		private static int vertexBufferSize = 8 * 1024;
		private static int indexBufferSize = 24 * 1024;
		private static Buffer vertexBuff;
		private static Buffer indexBuffer;
		private static Buffer constantBuffer;

		private static Device Device;
		private static AppForm RenderForm;

		private static int sizeofImDrawVert = Utilities.SizeOf<ImDrawVert>();
		private static int sizeofImDrawIdx = Utilities.SizeOf<ushort>();
		private static int sizeofMatrix = Utilities.SizeOf<Matrix4x4>();

		public static void Initialise(AppForm form) {

			RenderForm = form;
			Device = D3DController.Device;

			Log("ImGui: Creating context...");
			Context = ImGui.CreateContext();
			ImGui.SetCurrentContext(Context);
			IO = ImGui.GetIO();
			IO.DisplaySize = new Vector2(RenderForm.Width, RenderForm.Height);
			Log("ImGui: Creating Font texture...");
			ShaderResourceView fontTexture;
			unsafe {
				// use the default font
				IO.Fonts.AddFontDefault();
				// output the font data as a texture
				IO.Fonts.GetTexDataAsRGBA32(
					out byte* pixelData,
					out int font_tex_width,
					out int font_tex_height,
					out int bytesPerPixel);
				var rect = new DataRectangle(new IntPtr(pixelData), font_tex_width * bytesPerPixel);
				// upload that texture to the GPU
				fontTexture = new ShaderResourceView(Device, new Texture2D(Device, new Texture2DDescription {
					Width = font_tex_width,
					Height = font_tex_height,
					MipLevels = 1,
					ArraySize = 1,
					Format = Format.R8G8B8A8_UNorm,
					SampleDescription = new SampleDescription(1, 0),
					Usage = ResourceUsage.Default,
					BindFlags = BindFlags.ShaderResource,
					CpuAccessFlags = CpuAccessFlags.None,
					OptionFlags = ResourceOptionFlags.None
				}, rect));
				// register the Texture (used during Render)
				TextureCache.Add("ImGui Font", fontTexture);
				IO.Fonts.SetTexID(fontTexture.NativePointer);
				// clear the texture data from regular memory (only in GPU now)
				IO.Fonts.ClearTexData();
			}

			Log("ImGui: Initializing the input system...");

			// Pass along mouse events:
			RenderForm.MouseDown += (sender, args) => {
				switch ( args.Button ) {
					case MouseButtons.Left: IO.MouseDown[0] = true; break;
					case MouseButtons.Right: IO.MouseDown[1] = true; break;
					case MouseButtons.Middle: IO.MouseDown[2] = true; break;
					case MouseButtons.XButton1: IO.MouseDown[3] = true; break;
					case MouseButtons.XButton2: IO.MouseDown[4] = true; break;
				}
			};

			RenderForm.MouseUp += (sender, args) => {
				switch ( args.Button ) {
					case MouseButtons.Left: IO.MouseDown[0] = false; break;
					case MouseButtons.Right: IO.MouseDown[1] = false; break;
					case MouseButtons.Middle: IO.MouseDown[2] = false; break;
					case MouseButtons.XButton1: IO.MouseDown[3] = false; break;
					case MouseButtons.XButton2: IO.MouseDown[4] = false; break;
				}
			};

			RenderForm.MouseWheel += (sender, args) => IO.MouseWheel = args.Delta / 100f;

			// not sure we need to set many of these up?
			IO.KeyMap[(int)ImGuiKey.F12] = (int)Keys.F12;
			IO.KeyMap[(int)ImGuiKey.Space] = (int)Keys.Space;

			RenderForm.KeyDown += (sender, args) => {
				IO.KeyAlt = args.Alt;
				IO.KeyShift = args.Shift;
				IO.KeyCtrl = args.Control;
				IO.KeysDown[args.KeyValue] = true;
			};
			RenderForm.KeyPress += (sender, args) => IO.AddInputCharacter(args.KeyChar);
			RenderForm.KeyUp += (sender, args) => {
				IO.KeyAlt = args.Alt;
				IO.KeyShift = args.Shift;
				IO.KeyCtrl = args.Control;
				IO.KeysDown[args.KeyValue] = false;
			};

			Log($"ImGui: Creating vertex buffer...{vertexBufferSize * sizeofImDrawVert} bytes");
			vertexBuff = new Buffer(Device, new BufferDescription {
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.VertexBuffer,
				OptionFlags = ResourceOptionFlags.None,
				CpuAccessFlags = CpuAccessFlags.Write,
				SizeInBytes = vertexBufferSize * sizeofImDrawVert
			});

			Log($"ImGui: Creating indexbuffer...{indexBufferSize * sizeofImDrawIdx} bytes");
			indexBuffer = new Buffer(Device, new BufferDescription {
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.IndexBuffer,
				OptionFlags = ResourceOptionFlags.None,
				CpuAccessFlags = CpuAccessFlags.Write,
				SizeInBytes = indexBufferSize * sizeofImDrawIdx
			});

			Log($"ImGui: Creating constant buffer... {sizeofMatrix} bytes");
			constantBuffer = new Buffer(Device, new BufferDescription {
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.ConstantBuffer,
				OptionFlags = ResourceOptionFlags.None,
				CpuAccessFlags = CpuAccessFlags.Write,
				SizeInBytes = sizeofMatrix
			});

			Log("ImGui: compiling shaders...");
			CompilationResult vertexShaderByteCode = ShaderBytecode.Compile(vertexShaderSource, "VS", "vs_4_0");
			imGuiVertexShader = new VertexShader(Device, vertexShaderByteCode);
			CompilationResult indexShaderByteCode = ShaderBytecode.Compile(pixelShaderSource, "PS", "ps_4_0");
			imGuiPixelShader = new PixelShader(Device, indexShaderByteCode);

			Log("ImGui: creating InputLayout...");
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

			Log("ImGui: Creating sampler state...");
			imGuiSamplerState = new SamplerState(Device, new SamplerStateDescription {
				Filter = Filter.MinMagMipLinear,
				AddressU = TextureAddressMode.Wrap,
				AddressV = TextureAddressMode.Wrap,
				AddressW = TextureAddressMode.Wrap,
				MipLodBias = 0.0f,
				ComparisonFunction = Comparison.Always,
				MinimumLod = 0.0f,
				MaximumLod = 0.0f
			});

			Log("ImGui: Creating blend state...");
			var blendDesc = new BlendStateDescription {
				AlphaToCoverageEnable = false
			};
			blendDesc.RenderTarget[0].IsBlendEnabled = true;
			blendDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
			blendDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
			blendDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
			blendDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
			blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.One;
			blendDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
			blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;

			imGuiBlendState = new BlendState(Device, blendDesc);
			if ( !imGuiBlendState.Description.RenderTarget[0].IsBlendEnabled ) {
				throw new Exception("AssertionError: blend state did not apply description");
			}

			Log("ImGui: Creating depth stencil state...");
			imGuiDepthStencilState = new DepthStencilState(Device, new DepthStencilStateDescription {
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

			Log("ImGui: Creating solid raster state...");
			imGuiSolidRasterState = new RasterizerState(Device, new RasterizerStateDescription {
				FillMode = FillMode.Solid,
				CullMode = CullMode.None,
				IsScissorEnabled = true,
				IsDepthClipEnabled = true
			});

			Log("ImGui: Create constant buffer...");
			UpdateConstantBuffer();


		}

		internal static void Resize(int left, int top, int width, int height) {
			IO.DisplaySize = new Vector2(width, height);
			UpdateConstantBuffer();
		}
		private static Matrix4x4 orthoProj;
		private static void UpdateConstantBuffer() {
			orthoProj = Matrix4x4.CreateOrthographicOffCenter(0f, IO.DisplaySize.X, IO.DisplaySize.Y, 0.0f, -1.0f, 1.0f);
			var context = Device.ImmediateContext;
			context.MapSubresource(constantBuffer, MapMode.WriteDiscard, MapFlags.None, out var buffer);
			buffer.Write(orthoProj);
			context.UnmapSubresource(constantBuffer, 0);
		}

		public static void NewFrame(long dt) {
			if ( IO.DisplaySize.X <= 0 || IO.DisplaySize.Y <= 0 ) {
				return;
			}

			// InputUpdate:
			IO.DeltaTime = dt;
			Win32.GetCursorPos(out Point mousePoint);
			IO.MousePos = new Vector2(mousePoint.X, mousePoint.Y);

			// Not sure why we do this?:
			if ( (IO.WantCaptureKeyboard || IO.WantCaptureMouse) && RenderForm.IsTransparent ) {
				RenderForm.IsTransparent = false;
			} else if ( !(IO.WantCaptureKeyboard || IO.WantCaptureMouse || RenderForm.IsTransparent) ) {
				RenderForm.IsTransparent = true;
			}

			ImGui.NewFrame();

			ImGui.SetNextWindowContentSize(IO.DisplaySize);
			ImGui.SetNextWindowPos(Vector2.Zero);
			ImGui.Begin("Background Layer",
					ImGuiWindowFlags.NoTitleBar |
					ImGuiWindowFlags.NoResize |
					ImGuiWindowFlags.NoMove |
					ImGuiWindowFlags.NoScrollbar |
					ImGuiWindowFlags.NoScrollWithMouse |
					ImGuiWindowFlags.NoCollapse |
					ImGuiWindowFlags.NoSavedSettings |
					ImGuiWindowFlags.NoInputs |
					ImGuiWindowFlags.NoFocusOnAppearing |
					ImGuiWindowFlags.NoBringToFrontOnFocus |
					ImGuiWindowFlags.NoBackground);
			// backgroundDrawListPtr = ImGui.GetWindowDrawList();
			ImGui.End();

			ImGui.SetNextWindowContentSize(IO.DisplaySize);
			ImGui.SetNextWindowPos(Vector2.Zero);
			ImGui.Begin("Text Layer",
					ImGuiWindowFlags.NoTitleBar |
					ImGuiWindowFlags.NoResize |
					ImGuiWindowFlags.NoMove |
					ImGuiWindowFlags.NoScrollbar |
					ImGuiWindowFlags.NoScrollWithMouse |
					ImGuiWindowFlags.NoCollapse |
					ImGuiWindowFlags.NoSavedSettings |
					ImGuiWindowFlags.NoInputs |
					ImGuiWindowFlags.NoFocusOnAppearing |
					ImGuiWindowFlags.NoBackground);
			textDrawListPtr = ImGui.GetWindowDrawList();
		}

		private static ImDrawListPtr textDrawListPtr;
		// private static ImDrawListPtr backgroundDrawListPtr;


		public static void DrawText(string text, Vector2 pos, Color color) => textDrawListPtr.AddText(pos, (uint)ToRBGA(color), text);

		public static void DrawFrame(Vector2 topLeft, Vector2 bottomRight, Color color, int thickness = 1) => textDrawListPtr.AddRect(topLeft, bottomRight, (uint)ToRBGA(color), 0f, ImDrawFlags.None, thickness);

		public static void DrawCircle(Vector2 pos, float radius, Color color, int thickness = 1) => textDrawListPtr.AddCircle(pos, radius, (uint)ToRBGA(color), 0, thickness);

		public static void Render(long dt) {

			if ( IO.DisplaySize.X <= 0 || IO.DisplaySize.Y <= 0 ) {
				return;
			}
		
			// End the background text window created by NewFrame()
			ImGui.End();

			// Render the scene to a frame
			ImGui.Render();
			ImDrawDataPtr data = ImGui.GetDrawData();

			if ( data.TotalVtxCount == 0 ) {
				return;
			}

			// Resize the vertex buffer if needed
			if ( data.TotalVtxCount > vertexBufferSize ) {
				vertexBuff.Dispose();
				vertexBufferSize = data.TotalVtxCount * 2;
				Log($"ImGui: Re-sizing vertex buffer...{vertexBufferSize * sizeofImDrawVert} bytes");
				vertexBuff = new Buffer(Device, new BufferDescription {
					Usage = ResourceUsage.Dynamic,
					BindFlags = BindFlags.VertexBuffer,
					OptionFlags = ResourceOptionFlags.None,
					CpuAccessFlags = CpuAccessFlags.Write,
					SizeInBytes = vertexBufferSize * sizeofImDrawVert
				});
			}

			// Resize the index buffer if needed
			if ( data.TotalIdxCount > indexBufferSize ) {
				indexBuffer.Dispose();
				indexBufferSize = data.TotalIdxCount * 2;
				Log($"ImGui: Re-sizing indexbuffer...{indexBufferSize * sizeofImDrawIdx} bytes");
				indexBuffer = new Buffer(Device, new BufferDescription {
					Usage = ResourceUsage.Dynamic,
					BindFlags = BindFlags.IndexBuffer,
					OptionFlags = ResourceOptionFlags.None,
					CpuAccessFlags = CpuAccessFlags.Write,
					SizeInBytes = indexBufferSize * Utilities.SizeOf<ushort>()
				});
			}

			data.ScaleClipRects(IO.DisplayFramebufferScale);

			var deviceContext = Device.ImmediateContext;

			// Upload vertex/index data into one GPU buffer each
			var vertexMap = deviceContext.MapSubresource(vertexBuff, 0, MapMode.WriteDiscard, MapFlags.None);
			IntPtr vertexOffset = vertexMap.DataPointer;

			var indexMap = deviceContext.MapSubresource(indexBuffer, 0, MapMode.WriteDiscard, MapFlags.None);
			IntPtr indexOffset = indexMap.DataPointer;

			for ( int i = 0; i < data.CmdListsCount; i++ ) {
				var cmdlist = data.CmdListsRange[i];
				Utilities.CopyMemory(
					vertexOffset,
					cmdlist.VtxBuffer.Data,
					cmdlist.VtxBuffer.Size * sizeofImDrawVert);
				Utilities.CopyMemory(
					indexOffset,
					cmdlist.IdxBuffer.Data,
					cmdlist.IdxBuffer.Size * sizeofImDrawIdx);
				vertexOffset += cmdlist.VtxBuffer.Size * sizeofImDrawVert;
				indexOffset += cmdlist.IdxBuffer.Size * sizeofImDrawIdx;
			}

			deviceContext.UnmapSubresource(vertexBuff, 0);
			deviceContext.UnmapSubresource(indexBuffer, 0);

			// Set the rendering states using the ImGui buffers and blend/depth states
			deviceContext.Rasterizer.State = imGuiSolidRasterState;
			deviceContext.InputAssembler.InputLayout = inputLayout;
			imGuiVertexBufferBinding = new VertexBufferBinding {
				Buffer = vertexBuff,
				Stride = sizeofImDrawVert,
				Offset = 0
			};
			deviceContext.InputAssembler.SetVertexBuffers(0, imGuiVertexBufferBinding);
			deviceContext.InputAssembler.SetIndexBuffer(indexBuffer, Format.R16_UInt, 0);
			deviceContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
			deviceContext.VertexShader.Set(imGuiVertexShader);
			deviceContext.VertexShader.SetConstantBuffer(0, constantBuffer);
			deviceContext.PixelShader.Set(imGuiPixelShader);
			deviceContext.PixelShader.SetSampler(0, imGuiSamplerState);

			deviceContext.OutputMerger.SetBlendState(imGuiBlendState);
			deviceContext.OutputMerger.SetDepthStencilState(imGuiDepthStencilState);

			var pos = data.DisplayPos;
			// Because we copied everything into two GPU buffers, we need two indexes over those arrays
			int vertexLocation = 0;
			int indexLocation = 0;
			for ( var i = 0; i < data.CmdListsCount; i++ ) {
				ImDrawListPtr cmdList = data.CmdListsRange[i];
				for ( var j = 0; j < cmdList.CmdBuffer.Size; j++ ) {
					ImDrawCmdPtr drawCmd = cmdList.CmdBuffer[j];
					deviceContext.Rasterizer.SetScissorRectangle(
						(int)(drawCmd.ClipRect.X - pos.X),
						(int)(drawCmd.ClipRect.Y - pos.Y),
						(int)(drawCmd.ClipRect.Z - pos.X),
						(int)(drawCmd.ClipRect.W - pos.Y));
					if ( TextureCache.TryGetValue(drawCmd.TextureId, out var tex) ) {
						deviceContext.PixelShader.SetShaderResource(0, tex);
						var drawCmdElemCount = (int)drawCmd.ElemCount;
						deviceContext.DrawIndexed(drawCmdElemCount,
							indexLocation,
							vertexLocation);
						indexLocation += drawCmdElemCount;
					} else {
						Log($"Attempt to render unknown texture: {drawCmd.TextureId} {drawCmd.GetTexID()}");
					}
				}
				// indexLocation += cmdList.IdxBuffer.Size;
				vertexLocation += cmdList.VtxBuffer.Size;
			}

		}

		private static string vertexShaderSource = @"
cbuffer vertexBuffer : register(b0) {
            float4x4 ProjectionMatrix; 
};
struct VS_INPUT {
            float2 pos : POSITION;
            float2 uv  : TEXCOORD0;
            float4 col : COLOR0;

};
            
struct PS_INPUT {
            float4 pos : SV_POSITION;
            float4 col : COLOR0;
            float2 uv  : TEXCOORD0;
};
            
PS_INPUT VS(VS_INPUT input) {
            PS_INPUT output;
            output.pos = mul( ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
            output.col = input.col;
            output.uv  = input.uv;
            return output;
}
";
		private static string pixelShaderSource = @"
struct PS_INPUT {
	float4 pos : SV_POSITION;
	float4 col : COLOR0;
	float2 uv  : TEXCOORD0;
};
sampler sampler0;
Texture2D texture0;
            
float4 PS(PS_INPUT input) : SV_Target
{
	float4 out_col = input.col * texture0.Sample(sampler0, input.uv); 
	return out_col; 
}
";
	}
}
