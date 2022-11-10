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
		public override IState OnTick(long dt) {
			var settings = PluginBase.GetPlugin<CoreSettings>();
			if( settings.ConsoleKey.IsReleased ) {
				Show = !Show;
				if ( !Show ) { PluginBase.SaveIniFile(); }
			}
			if ( Show && ImGui.Begin("Console", ref Show) ) {
				try {
					if ( ImGui.Button("Save##iniSettings") ) {
						PluginBase.SaveIniFile();
					}
					ImGui.SameLine();
					if ( ImGui.Button("Exit##exit") ) {
						PluginBase.SaveIniFile();
						Overlay.Close();
					}
					ImGui.SameLine();
					ImGui_HotKeyButton("Console", ref settings.ConsoleKey);
					foreach ( var plugin in PluginBase.Plugins.OrderBy(p => p.SortIndex) ) {
						if ( ImGui.TreeNode(plugin.Name) ) {
							if ( ImGui.BeginChildFrame((uint)plugin.Name.GetHashCode(), Vector2.Zero, ImGuiWindowFlags.AlwaysAutoResize) ) {
								plugin.Render();
								ImGui.EndChildFrame();
							}
							ImGui.TreePop();
						}
					}
				} finally {
					ImGui.End();
				}
			}
			return base.OnTick(dt);
		}
	}
}
