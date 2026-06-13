using System;
using System.Windows.Forms;
using static AtE.Win32;

namespace AtE {

	// NOTE: This used to inherit SharpDX.Windows.RenderForm, but that type's
	// constructor loads its window icon from a BinaryFormatter-serialized embedded
	// resource, and BinaryFormatter was removed in .NET 9 (throws at runtime). We
	// only ever used RenderForm's UserResized event, so we inherit Form directly and
	// reproduce that event plus the paint styles RenderForm set for D3D hosting.
	public class OverlayForm : Form {
		private readonly ContextMenuStrip contextMenu1 = new ContextMenuStrip();
		private readonly NotifyIcon notifyIcon = new NotifyIcon();

		/// <summary>Raised whenever the form is resized (replaces RenderForm.UserResized).</summary>
		public event EventHandler UserResized;

		public OverlayForm() {
			// Host a Direct3D swap chain: let our renderer own all painting so WinForms
			// never clears the surface underneath it. (These are the styles RenderForm set.)
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
			ResizeRedraw = true;

			// let the Designer have it's auto properties, so the VS editor works
			InitializeComponent();

			var menuExit = new ToolStripMenuItem() {
				Text = "Exit"
			};
			menuExit.Click += (sender, args) => Close();
			contextMenu1.Items.Add(menuExit);

			notifyIcon.ContextMenuStrip = contextMenu1;
			notifyIcon.Icon = Icon;
			notifyIcon.Text = "Assistant to the Exile";
			notifyIcon.Visible = true;

			BringToFront();

		}

		protected override void OnResize(EventArgs e) {
			base.OnResize(e);
			UserResized?.Invoke(this, EventArgs.Empty);
		}

		private bool trans = false;
		/// <summary>
		/// Toggles the WS_EX_TRANSPARENT flag on the window.
		/// WS_EX_TRANSPARENT: The window should not be painted until 
		/// siblings beneath the window(that were created by the same thread)
		/// have been painted. The window appears transparent because the bits
		/// of underlying sibling windows have already been painted.
		/// 
		/// The RenderForm itself will appear transparent regardless of this value,
		/// due to ExtendFrameIntoClientArea.
		/// 
		/// When this value is true, ImGui windows cannot "capture" the keyboard and mouse.
		/// </summary>
		public bool IsTransparent {
			get => trans;
			set {
				if( value != trans ) {
					trans = value;
					if( trans ) {
						SetWindowLong(Handle, GWL_STYLE, new IntPtr(WS_VISIBLE));
						SetWindowLong(Handle, GWL_EXSTYLE, new IntPtr(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST));
					} else {
						SetWindowLong(Handle, GWL_STYLE, new IntPtr(WS_VISIBLE));
						SetWindowLong(Handle, GWL_EXSTYLE, new IntPtr(WS_EX_LAYERED | WS_EX_TOPMOST));
					}
				}
			}
		}

		/// <summary>
		/// Tell the Window Manager that the window frame covers part of the Form.
		/// By passing in -1, -1, -1, -1 (using a custom Margin struct that allows this),
		/// we are able to have a margin that covers the whole form no matter the size.
		/// </summary>
		public void ExtendFrameIntoClientArea(int top, int left, int right, int bottom) {
			var margins = new Margins {
				Left = left,
				Right = right,
				Top = top,
				Bottom = bottom
			};
			DwmExtendFrameIntoClientArea(Handle, ref margins);
		}

		protected override void Dispose(bool disposing) {
			if ( notifyIcon != null ) {
				notifyIcon.Icon = null;
				notifyIcon.Dispose();
			}
			contextMenu1?.Dispose();
			base.Dispose(disposing);
		}

		private void InitializeComponent() {
			System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(OverlayForm));
			this.SuspendLayout();
			// 
			// OverlayForm
			// 
			this.BackColor = System.Drawing.Color.Gray;
			this.ClientSize = new System.Drawing.Size(800, 600);
			this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
			this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
			this.Name = "OverlayForm";
			this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
			this.Text = "Assistant to the Exile";
			this.TopMost = true;
			this.ResumeLayout(false);

		}
	}
}
