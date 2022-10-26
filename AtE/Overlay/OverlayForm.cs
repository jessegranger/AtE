using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpDX.Windows;
using System.Windows.Forms;
using System.Drawing;
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
		public bool IsTransparent {
			get => trans;
			set {
				if( value != trans ) {
					trans = value;
					if( trans ) {
						SetTransparent();
					} else {
						SetNoTransparent();
					}
				}
			}
		}
		public void SetTransparent() {
			SetWindowLong(Handle, GWL_STYLE, new IntPtr(WS_VISIBLE));
			SetWindowLong(Handle, GWL_EXSTYLE, new IntPtr(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST));
		}

		public void SetNoTransparent() {
			SetWindowLong(Handle, GWL_STYLE, new IntPtr(WS_VISIBLE));
			SetWindowLong(Handle, GWL_EXSTYLE, new IntPtr(WS_EX_LAYERED | WS_EX_TOPMOST));
		}

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
