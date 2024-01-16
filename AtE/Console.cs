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

		private void ImGui_SelectableProfile(CoreSettings settings, string profile) {
			bool selected = settings.SelectedProfile?.Equals(profile) ?? false;
			if ( ImGui.Selectable(profile, selected) && !selected ) {
				settings.SelectedProfile = profile;
				PluginBase.LoadIniFiles();
			}
		}
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
					ImGui.SameLine();
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"Profile: {settings.SelectedProfile}");
					/*
					ImGui.SameLine();
					if( ImGui.BeginCombo("##SelectedProfile", settings.SelectedProfile) ) {
						var profiles = CoreSettings.GetProfiles();
						ImGui_SelectableProfile(settings, "default");
						foreach(var profile in profiles) {
							ImGui_SelectableProfile(settings, profile);
						}
						if( ImGui.Selectable("Create new...", false) ) {
							string newProfileName = GetPlayer()?.GetComponent<Player>()?.Name ?? new string('\0', 64);
							string errorText = null;
							bool showCreateProfile = true;
							Run("Create Profile", (self, _) => {
								if ( !showCreateProfile ) return null;
								ImGui.SetNextWindowPos(Center(Overlay.RenderForm.ClientRectangle), ImGuiCond.Appearing);
								ImGui.SetNextWindowSize(new Vector2(-1f, 0f));
								if ( ImGui.Begin("Create Profile", ref showCreateProfile) ) {
									try {
										ImGui.AlignTextToFramePadding();
										ImGui.Text("Name:");
										ImGui.SameLine();
										ImGui.InputText("", ref newProfileName, 64);
										ImGui.SameLine();
										if ( ImGui.Button("Create##CreateProfileButton") ) {
											errorText = null;
											if( CoreSettings.CreateProfile(newProfileName) ) {
												settings.SelectedProfile = newProfileName;
												PluginBase.SaveIniFile();
												return null;
											} else {
												errorText = "Profile already exists";
											}
										}
										if( errorText != null ) {
											ImGui.Text(errorText);
										}
									} finally {
										ImGui.End();
									}
								}
								return self;
							});
						}
						ImGui.EndCombo();
					}


					ImGui.SameLine();
					ImGui.Checkbox("Auto", ref settings.AutoProfile);
					*/

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
						if ( !plugin.Hidden ) {
							ImGui.Checkbox($"##{plugin.Name}", ref plugin.Enabled);
							ImGui.SameLine();
							if ( ImGui.Selectable(plugin.Name, selected) ) {
								selectedIndex = pluginIndex;
							}
							pluginIndex += 1;
						}
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
