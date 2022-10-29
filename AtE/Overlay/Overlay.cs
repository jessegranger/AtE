﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public static class Overlay {
		public static OverlayForm RenderForm;
		public static long FrameCount;

		public static void Close() => RenderForm?.Close();
		public static void Initialise() {

			Log("Creating window...");
			RenderForm = new OverlayForm {
				Text = "Assistant to the Exile",
				Width = 800,
				Height = 600
			};
			RenderForm.ShowInTaskbar = false;
			RenderForm.Load += (sender, args) => {
				Log("RenderForm: OnLoad...");
				// Set full transparency, where an undrawable (by windows) margin fills the whole form
				RenderForm.ExtendFrameIntoClientArea(-1, -1, -1, -1);
				RenderForm.IsTransparent = true;
				var screen = Screen.FromHandle(RenderForm.Handle);
				RenderForm.Size = new Size(screen.Bounds.Width, screen.Bounds.Height);
			};

			D3DController.Initialise(RenderForm);

			ImGuiController.Initialise(RenderForm);

			D3DController.CreateRenderStates(RenderForm.Width, RenderForm.Height);

			// Sets the initial window styles, with RenderForm on top at first.
			// Later, OnLoad will toggle this, and put RenderForm "behind" the DirectX render surface.
			RenderForm.IsTransparent = false;

			Log("Binding resize event...");
			RenderForm.UserResized += (sender, args) => {
				D3DController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);
				Log($"RenderForm: UserResized {RenderForm.ClientRectangle}");

				ImGuiController.Resize(RenderForm.Left, RenderForm.Top, RenderForm.Width, RenderForm.Height);

			};

			StateMachine.DefaultMachine.EnableLogging((s) => Log(s));

		}

		public static int Height => RenderForm.Height;
		public static int Width => RenderForm.Width;
		public static int Top => RenderForm.Top;
		public static int Left => RenderForm.Left;
		public static void Resize(long left, long top, long right, long bottom) {
			long width = right - left;
			long height = bottom - top;
			RenderForm.Size = new Size((int)width, (int)height);
			RenderForm.Location = new Point((int)left, (int)top);
		}

		public static double FPS { get; private set; }

		public static void RenderLoop() {
			Log("Starting Render loop...");
			long lastRenderTime = Time.ElapsedMilliseconds - 16;
			SharpDX.Windows.RenderLoop.Run(RenderForm, async () => {
				FrameCount += 1;
				float msPerFrame = (float)Math.Round(1000f / CoreSettings.FpsCap);
				long dt = Time.ElapsedMilliseconds - lastRenderTime;
				if ( CoreSettings.EnforceFpsCap && dt < msPerFrame ) {
					await Task.Delay(3);
					return;
				}
				FPS = dt == 0 ? 999d : 1000f / dt;
				lastRenderTime += dt;

				// Clear the prior frame
				D3DController.NewFrame();

				// Start a new frame
				ImGuiController.NewFrame(dt);

				// Advance all the States by one frame
				StateMachine.DefaultMachine.OnTick(dt);

				// Render the ImGui layer to vertexes and Draw them to the GPU buffers
				ImGuiController.Render(dt);

				// TODO: Render a Sprite layer?

				// Finalize the rendering to the screen
				D3DController.Render();

			}, true);
		}
	}
}
