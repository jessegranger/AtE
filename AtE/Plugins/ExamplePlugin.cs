using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {
	public class ExamplePlugin : PluginBase {

		public bool ShowDemoWindow = false;
		public bool ShowMetricsWindow = false;

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Demo Window", ref ShowDemoWindow);
			ImGui.Checkbox("Show Metrics Window", ref ShowMetricsWindow);
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled ) return this;
			if( ShowDemoWindow ) {
				ImGui.ShowDemoWindow();
			}
			if( ShowMetricsWindow ) {
				ImGui.ShowMetricsWindow();
			}
			return this;
		}
	}
}
