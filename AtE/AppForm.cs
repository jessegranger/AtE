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

	class AppForm : RenderForm {
		private readonly ContextMenu contextMenu1;
		private readonly NotifyIcon notifyIcon;
		public AppForm() {
			SuspendLayout();

			StartPosition = FormStartPosition.Manual;
			Location = new Point(0, 0);
			Size = new Size(800, 600);
			FormBorderStyle = FormBorderStyle.None;
			TopMost = true;

			contextMenu1 = new ContextMenu();
			var menuExit = new MenuItem() {
				Index = 0,
				Text = "Exit"
			};
			menuExit.Click += (sender, args) => Close();
			contextMenu1.MenuItems.Add(menuExit);

			notifyIcon = new NotifyIcon();
			notifyIcon.ContextMenu = contextMenu1;
			notifyIcon.Icon = Icon;
			notifyIcon.Text = "Assistant to the Exile";
			notifyIcon.Visible = true;
			ShowInTaskbar = true;
			BackColor = Color.Gray;

			ResumeLayout(false);
			BringToFront();

		}

		private bool trans = false;
		public bool IsTransparent {
			get => trans;
			set {
				if( value != trans ) {
					trans = value;
					if( trans ) {
						Win32.SetTransparent(Handle);
					} else {
						Win32.SetNoTransparent(Handle);
					}
				}
			}
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
	}
}
