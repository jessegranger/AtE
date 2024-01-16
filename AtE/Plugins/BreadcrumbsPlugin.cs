using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE.Plugins {
	class BreadcrumbsPlugin : PluginBase {

		public override string Name => "Breadcrumbs";
		public override bool Hidden => false;

		public int BreadcrumbMode = 0;
		public int BreadcrumbInterval = 1; // seconds
		public int MinimumDistance = 2; // meters
		public int MaxTotal = 500;
		public bool ShowBreadcrumbsOnMap = false;

		public HotKey ToggleHotKey = new HotKey(Keys.OemPeriod);
		public HotKey ClearHotKey = new HotKey(Keys.Oemcomma);

		private const int UNITS_TO_METERS = 100; // poe docs lie about this and say its 10 but you can just look with your eye

		private string[] ModeLabels = new string[] {
			"Never",
			"Always",
			"In the Underground"
		};

		private class Breadcrumb {
			public Vector2 GridPos;
			public Vector3 EntPos;
		}
		private List<Breadcrumb> Breadcrumbs = new List<Breadcrumb>();
		private long MostRecentBreadcrumb = 0;

		public BreadcrumbsPlugin() : base() {
			OnAreaChange += (sender, areaName) => {
				Breadcrumbs.Clear();
			};
		}

		public override void Render() {
			base.Render();
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Leaves a trail of colored dots on the minimap.");
			ImGui.Text("");
			ImGui.BeginTable("breadcrumb_table", 2, ImGuiTableFlags.Borders);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Mode:");
			ImGui.SameLine();
			ImGui_HelpMarker("When should the tools record breadcrumbs at all");
			ImGui.TableNextColumn();
			ImGui.SetNextItemWidth(ImGui.CalcTextSize("In the Underground").X);
			ImGui.Combo("##breadcrumb_mode", ref BreadcrumbMode, ModeLabels, ModeLabels.Length);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Interval:");
			ImGui.TableNextColumn();
			ImGui.SliderInt("(secs)##breadcrumb_interval", ref BreadcrumbInterval, 1, 30);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Min. Distance:");
			ImGui.SameLine();
			ImGui_HelpMarker("Minimum distance between the last breadcrumb and the next.");
			ImGui.TableNextColumn();
			ImGui.SliderInt("(meters)##breadcrumb_distance", ref MinimumDistance, 1, 30);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Show on Map:");
			ImGui.TableNextColumn();
			ImGui.Checkbox("##bc_show_on_map", ref ShowBreadcrumbsOnMap);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Status:");
			ImGui.TableNextColumn();
			ImGui.Text($"Mode: {BreadcrumbMode}");
			ImGui.Text($"Count: {Breadcrumbs.Count}");
			if ( Breadcrumbs.Count > 0 ) {
				float distance = (Breadcrumbs[Breadcrumbs.Count - 1].EntPos - Position(GetPlayer())).Length();
				ImGui.Text($"Distance: {distance/UNITS_TO_METERS} m");
			}
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Hot Keys:");
			ImGui.TableNextColumn();
			ImGui_HotKeyButton("Show/Hide", ref ToggleHotKey);
			ImGui_HotKeyButton("Clear", ref ClearHotKey);
			ImGui.EndTable();
		}

		public override IState OnTick(long dt) {
			if( Paused || !Enabled ) {
				return this;
			}
			if( ToggleHotKey.IsReleased ) {
				switch( BreadcrumbMode ) {
					case 0: BreadcrumbMode = 1; break;
					case 1: BreadcrumbMode = 0; break;
					case 2: break;
				}
				ShowBreadcrumbsOnMap = BreadcrumbMode == 1;
			}
			if( ClearHotKey.IsReleased ) {
				Breadcrumbs.Clear();
			}
			bool enabled = false;
			switch( BreadcrumbMode ) {
				case 0: enabled = false; break; // never
				case 1: enabled = true; break; // always
				default:
				case 2:
					DrawBottomLeftText("BreadcrumbPlugin: Underground detection not working. Use 'Always' for now.", Color.Red);
					enabled = false; /* TODO: area name or something, how to know if we are in the mist? */
					break;
			}
			if ( enabled ) {
				long elapsed = Time.ElapsedMilliseconds - MostRecentBreadcrumb;
				if ( elapsed > (BreadcrumbInterval * 1000) ) {
					var p = GetPlayer();
					if ( IsValid(p) ) {
						var entPos = Position(p);
						bool should_add = true;
						if ( Breadcrumbs.Count > 0 ) {
							float distance_in_units = (Breadcrumbs[Breadcrumbs.Count - 1].EntPos - entPos).Length();
							if( distance_in_units < (MinimumDistance * UNITS_TO_METERS) ) {
								should_add = false;
							}
						}
						if ( should_add ) {
							Breadcrumbs.Add(new Breadcrumb() { EntPos = Position(p), GridPos = GridPosition(p) });
							if ( Breadcrumbs.Count > MaxTotal ) {
								Breadcrumbs.RemoveAt(0);
							}
							MostRecentBreadcrumb = Time.ElapsedMilliseconds;
						}
					}
				}
			}
			if( ShowBreadcrumbsOnMap ) {
				var p = GetPlayer();
				if ( IsValid(p) ) {
					var entPos = Position(p);
					if ( entPos != Vector3.Zero ) {
						var map = GetUI()?.Map;
						if ( IsValid(map) ) {
							foreach ( var bc in Breadcrumbs ) {
								float distance = (bc.EntPos - entPos).Length();
								if ( distance < (100 * UNITS_TO_METERS) ) {
									var iconPos = map.WorldToMap(bc.GridPos, bc.EntPos);
									int iconSize = 7;
									DrawSprite(SpriteIcon.SmallGreenCircle, iconPos, iconSize, iconSize);
								}
							}
						}
					}
				}
			}
			return base.OnTick(dt);
		}

	}
}
