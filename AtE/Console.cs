using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {

	public class Console : State {
		public bool Show = true;
		public HotKey HotKey = new HotKey(Keys.F12);
		public override IState OnTick(long dt) {
			if( HotKey.IsReleased ) {
				Show = !Show;
			}
			if ( Show && ImGui.Begin("Console", ref Show) ) {
				foreach ( var plugin in PluginBase.Plugins.OrderBy(p => p.SortIndex) ) {
					if( ImGui.TreeNode(plugin.Name) ) {
						if( ImGui.BeginChild("PluginFrame" + plugin.Name, new Vector2(-1f, -1f), true, ImGuiWindowFlags.AlwaysAutoResize) ) {
							plugin.Render();
							ImGui.EndChild();
						}
						ImGui.TreePop();
					}
				}
				ImGui.End();
			}
			return base.OnTick(dt);
		}
	}
}
