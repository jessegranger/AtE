using SharpDX.Windows;
using System;
using System.Windows.Forms;
using static AtE.Win32;

namespace AtE {

	public class OverlayForm : RenderForm {
		private readonly ContextMenu contextMenu1 = new ContextMenu();
		private readonly NotifyIcon notifyIcon = new NotifyIcon();
		public OverlayForm() {
			// let the Designer have it's auto properties, so the VS editor works
			InitializeComponent();

			var menuExit = new MenuItem() {
				Index = 0,
				Text = "Exit"
			};
			menuExit.Click += (sender, args) => Close();
			contextMenu1.MenuItems.Add(menuExit);

			notifyIcon.ContextMenu = contextMenu1;
			notifyIcon.Icon = Icon;
			notifyIcon.Text = "Assistant to the Exile";
			notifyIcon.Visible = true;

			BringToFront();

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
