using System;
using System.Drawing;
using System.Windows.Forms;
using SharpDX.Windows;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Windows implementation of <see cref="IOverlayWindow"/>. It owns the existing
	/// <see cref="OverlayForm"/> (borderless WinForms window) and reproduces the windowing
	/// behaviour that used to live in <c>Overlay.Initialise</c>: the load-time DWM "sheet of
	/// glass" transparency, the click-through style toggle, and the SharpDX message pump. It
	/// contains no GPU code — the renderer binds to <see cref="Handle"/>.
	///
	/// The concrete <see cref="Form"/> is exposed <c>internal</c> so the sibling Windows renderer
	/// backend (<see cref="WindowsRenderer"/> → <c>D3DController</c>/<c>ImGuiController</c>/
	/// <c>SpriteController</c>) can wire its WinForms input events to it — the same kind of
	/// within-assembly shortcut <see cref="WindowsInputSource"/> takes. A Linux backend has no
	/// such coupling; its renderer would bind to <see cref="Handle"/> only.
	/// </summary>
	public sealed class WindowsOverlayWindow : IOverlayWindow {

		internal OverlayForm Form { get; private set; }
		private bool closed;

		public event Action<int, int> Resized;

		public void Create(string title, int width, int height) {
			Log("Creating window...");
			Form = new OverlayForm {
				Text = title,
				Width = width,
				Height = height,
				BackColor = Color.Black,
				ShowInTaskbar = false
			};

			// Defer the transparency/sizing to OnLoad (i.e. when the window is shown), exactly as
			// before: the swap chain is created at the initial size first, and the resize-to-screen
			// then drives the renderer's Resize through the UserResized path.
			Form.Load += (sender, args) => {
				Log("RenderForm: OnLoad...");
				// Negative margins create the "sheet of glass" effect: the client area renders as a
				// solid transparent surface with no window border.
				SetTransparent(true);
				SetClickThrough(true);
				var screen = Screen.FromHandle(Form.Handle);
				Form.Size = new Size(screen.Bounds.Width, screen.Bounds.Height);
			};

			// Re-raise the form's resize as the platform-neutral event.
			Form.UserResized += (sender, args) => Resized?.Invoke(Form.ClientSize.Width, Form.ClientSize.Height);

			// Force the native handle into existence so the renderer can bind to it before we Show.
			_ = Form.Handle;
		}

		public IntPtr Handle => Form.Handle;

		// SetClickThrough maps to the OverlayForm.IsTransparent style toggle (the WS_EX_TRANSPARENT
		// bit); the setter there already guards against redundant SetWindowLong calls.
		public void SetClickThrough(bool enabled) => Form.IsTransparent = enabled;

		public void SetTopmost(bool enabled) => Form.TopMost = enabled;

		public void SetTransparent(bool enabled) {
			if ( enabled ) {
				// -1 margins => the whole client area is the "undrawable" (by Windows) glass sheet.
				Form.ExtendFrameIntoClientArea(-1, -1, -1, -1);
			} else {
				Form.ExtendFrameIntoClientArea(0, 0, 0, 0);
			}
		}

		public int Width => Form.Width;
		public int Height => Form.Height;
		public int Left => Form.Left;
		public int Top => Form.Top;

		public void SetBounds(int left, int top, int width, int height) {
			Form.Size = new Size(width, height);
			Form.Location = new Point(left, top);
		}

		public bool HasFocus => Win32.GetForegroundWindow() == Form.Handle;

		public bool IsOpen => !closed && Form != null && !Form.IsDisposed;

		public void RunMessageLoop(Action onFrame) {
			// SharpDX's pump shows the control and calls back once per iteration; the final 'true'
			// keeps the WinForms message queue (Application.DoEvents) drained as before.
			RenderLoop.Run(Form, () => onFrame(), true);
		}

		public void Close() {
			closed = true;
			Form?.Close();
		}
	}
}
