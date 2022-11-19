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

		private int selectedIndex = 0;

		public override IState OnTick(long dt) {
			var settings = PluginBase.GetPlugin<CoreSettings>();
			if( settings.ConsoleKey.IsReleased ) {
				Show = !Show;
				if ( !Show ) { PluginBase.SaveIniFile(); }
			}
			if ( Show && ImGui.Begin("Console", ref Show, ImGuiWindowFlags.AlwaysAutoResize) ) {
				try {
					if ( ImGui.Button("Save##iniSettings") ) {
						PluginBase.SaveIniFile();
					}
					ImGui.SameLine();
					ImGui_HelpMarker($"Settings auto-save when the console is toggled. ({settings.ConsoleKey.Key})");
					ImGui.SameLine();
					if ( ImGui.Button("Exit##exit") ) {
						PluginBase.SaveIniFile();
						Overlay.Close();
					}
					/*
					ImGui.SameLine();
					ImGui_HotKeyButton("Console", ref settings.ConsoleKey);
					// render all the plugins as a tree:
					foreach ( var plugin in PluginBase.Plugins.OrderBy(p => p.SortIndex) ) {
						if ( ImGui.TreeNode(plugin.Name) ) {
							if ( ImGui.BeginChildFrame((uint)plugin.Name.GetHashCode(), Vector2.Zero, ImGuiWindowFlags.AlwaysAutoResize) ) {
								plugin.Render();
								ImGui.EndChildFrame();
							}
							ImGui.TreePop();
						}
					}
					*/

					ImGui.BeginTable("Table.Console", 2, ImGuiTableFlags.BordersInnerV);
					ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 150f);
					ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch);
					ImGui.TableNextRow();
					ImGui.TableNextColumn(); // first column is the selectable list
					int pluginIndex = 0;
					PluginBase selectedPlugin = null;
					foreach ( var plugin in PluginBase.Plugins.OrderBy(p => p.SortIndex) ) {
						bool selected = selectedIndex == pluginIndex;
						if ( selected ) selectedPlugin = plugin;
						ImGui.Checkbox($"##{plugin.Name}", ref plugin.Enabled);
						ImGui.SameLine();
						if( ImGui.Selectable(plugin.Name, selected) ) {
							selectedIndex = pluginIndex;
						}
						pluginIndex += 1;
					}
					ImGui.TableNextColumn();
					if( selectedPlugin != null ) {
						// if ( ImGui.BeginChildFrame((uint)selectedPlugin.Name.GetHashCode(), Vector2.Zero, ImGuiWindowFlags.AlwaysAutoResize) ) {
							selectedPlugin.Render();
							// ImGui.EndChildFrame();
						// }
					}
					ImGui.EndTable();

				} finally {
					ImGui.End();
				}
			}
			return base.OnTick(dt);
		}
	}
}
