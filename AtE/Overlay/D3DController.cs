using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Drawing;
using System.Threading;
using static AtE.Globals;
using Device = SharpDX.Direct3D11.Device;
using Format = SharpDX.DXGI.Format;

namespace AtE {
	internal static class D3DController {
		public static bool Enabled = true;
		public static Device Device;
		private static SwapChain deviceSwapChain;
		private static DeviceContext deviceContext;
		private static Factory deviceFactory;
		private static Texture2D backBuffer;
		private static RenderTargetView renderTargetView;
		// each of the layers has their own blend and stencil state
		// private static BlendState deviceBlendState;
		// private static DepthStencilState deviceDepthStencilState;
		private static RawColor4 clearColor = new RawColor4(0, 0, 0, 0);

		public static void Initialise(OverlayForm RenderForm) {

			Log("Creating DirectX 11 device...");
			Bounds = new Rectangle(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);

			var swapChainDesc = new SwapChainDescription {
				Usage = Usage.RenderTargetOutput,
				OutputHandle = RenderForm.Handle,
				BufferCount = 1,
				IsWindowed = true,
				Flags = SwapChainFlags.AllowModeSwitch,
				SwapEffect = SwapEffect.Discard,
				SampleDescription = new SampleDescription(1, 0),
				ModeDescription = new ModeDescription {
					Format = Format.R8G8B8A8_UNorm,
					Width = Bounds.Width,
					Height = Bounds.Height,
					Scaling = DisplayModeScaling.Unspecified,
					RefreshRate = new Rational(60, 1),
					ScanlineOrdering = DisplayModeScanlineOrder.Unspecified
				}
			};

			try {
				SharpDX.Direct3D11.Device.CreateWithSwapChain(DriverType.Hardware,
					DeviceCreationFlags.None,
					new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 },
					swapChainDesc,
					out Device,
					out deviceSwapChain
				);
			} catch ( System.AccessViolationException e ) {
				Log($"DirectX: Failed to create Device");
				Log(e.Message);
				return;
			} catch ( Exception e ) {
				Log($"DirectX: Failed to create Device");
				Log(e.Message);
				return;
			}
			deviceContext = Device.ImmediateContext;
			deviceFactory = deviceSwapChain.GetParent<Factory>();
			deviceFactory.MakeWindowAssociation(RenderForm.Handle, WindowAssociationFlags.IgnoreAll);

			CreateRenderTarget();
		}
		private static void CreateRenderTarget() {
			Log($"DirectX: Creating render target view...");
			backBuffer = SharpDX.Direct3D11.Resource.FromSwapChain<Texture2D>(deviceSwapChain, 0);
			if ( backBuffer == null || backBuffer.IsDisposed ) {
				throw new Exception("Failed to create DirectX Resource 'backBuffer = Resource.FromSwapChain'");
			}
			renderTargetView = new RenderTargetView(Device, backBuffer);
		}
		private static void CleanupRenderTarget() {
			renderTargetView?.Dispose();
			backBuffer?.Dispose();
		}

		public static void CreateRenderStates(int Width, int Height) {

			/* None of this is used, since each layer sets its own:
			Log($"DirectX: Setting device blend state...");
			var blendStateDesc = new BlendStateDescription();
			blendStateDesc.RenderTarget[0].IsBlendEnabled = true;
			blendStateDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
			blendStateDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
			blendStateDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
			blendStateDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.InverseSourceAlpha;
			blendStateDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.Zero;
			blendStateDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
			blendStateDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;

			deviceBlendState = new BlendState(Device, blendStateDesc);
			deviceContext.OutputMerger.BlendFactor = new SharpDX.Mathematics.Interop.RawColor4(1f, 1f, 1f, 1f); // Color.White;
			deviceContext.OutputMerger.SetBlendState(deviceBlendState);

			Log($"DirectX: Setting device depth stencil state...");
			var depthStencilDesc = new DepthStencilStateDescription {
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
			};

			deviceDepthStencilState = new DepthStencilState(Device, depthStencilDesc);
			deviceContext.OutputMerger.SetDepthStencilState(deviceDepthStencilState);
			Log("DirectX: Setting Viewport...");
			deviceContext.Rasterizer.SetViewport(0, 0, Width, Height, 0f, 1f);
			deviceContext.OutputMerger.SetRenderTargets(renderTargetView);

			Log("DirectX: Setting RasterizerState...");
			deviceContext.Rasterizer.State = new RasterizerState(Device,
				new RasterizerStateDescription {
					FillMode = FillMode.Solid,
					CullMode = CullMode.None
				});
			*/

		}

		public static void NewFrame() => Clear();

		public static void Clear() => deviceContext.ClearRenderTargetView(renderTargetView, clearColor);

		public static bool VSync = true;

		public static void Render(bool force = false) {
			// other layers (ImGui, Sprites) should have called things like DrawIndexed() to fill up the current swap chain before now
			// so all that's left to do is swap the buffers and show the result
			if ( Enabled || force ) {
				try {
					deviceSwapChain.Present(VSync ? 1 : 0, PresentFlags.None);
				} catch ( Exception e ) {
					Log(e.Message);
					Log(e.StackTrace);
					Overlay.Close();
				}
			} else { // normally, the above will make the CPU stall, to wait for 60Hz vsync
				// but, if Enabled = false, then we can get CPU spin lock
				Thread.Sleep(100);
			}
		}

		public static Rectangle Bounds { get; private set; }

		public static void Resize(int Left, int Top, int Width, int Height) {
			CleanupRenderTarget();
			deviceSwapChain.ResizeBuffers(1, Width, Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
			CreateRenderTarget();
			Bounds = new Rectangle(Left, Top, Width, Height);

			deviceContext.Rasterizer.SetViewport(0, 0, Width, Height);
			deviceContext.OutputMerger.SetRenderTargets(renderTargetView);
		}


	}
}
