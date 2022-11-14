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
	public class DelvePlugin : PluginBase {

		public override string Name => "Delve Settings";

		public bool HighlightWalls = true;

		private const string PATH_DELVE_WALL = "Metadata/Terrain/Leagues/Delve/Objects/DelveWall";

		public bool UseFlaresInDarkness = true;
		public int UseFlaresAtDarkness = 12;
		public HotKey FlareKey = new HotKey(Keys.D6);

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Highlight Walls", ref HighlightWalls);
			ImGui.Checkbox("##Use Flares Checkbox", ref UseFlaresInDarkness);
			ImGui.SameLine();
			ImGui_HotKeyButton("Use Flares", ref FlareKey);
			ImGui.SliderInt("At darkness stacks", ref UseFlaresAtDarkness, 2, 20);
		}

		private long lastFlare = 0;

		public override IState OnTick(long dt) {
			if( Enabled && !Paused && PoEMemory.IsAttached ) {

				if ( PoEMemory.GameRoot.AreaLoadingState.IsLoading ) {
					return this;
				}

				string areaName = PoEMemory.GameRoot.AreaLoadingState.AreaName;
				if( ! (areaName?.Equals("Azurite Mine") ?? false) ) {
					return this;
				}

				var player = GetPlayer();
				if ( !IsValid(player) ) {
					return this;
				}
				var playerPos = Position(player);
				if( playerPos == Vector3.Zero ) {
					return this;
				}

				if( UseFlaresInDarkness && TryGetBuffValue(player, "delve_degen_buff", out int stacks ) && stacks >= UseFlaresAtDarkness ) {
					long sinceLastFlare = Time.ElapsedMilliseconds - lastFlare;
					if( sinceLastFlare > 1000 ) {
						lastFlare += sinceLastFlare;
						FlareKey.PressImmediate();
					}
				}
				bool debugOnce = true;

				foreach(var ent in GetEntities().Where(IsValid) ) {
					string path = ent.Path;
					if ( path == null ) {
						continue;
					}
					Color lineColor = Color.White;
					string lineText = null;

					if ( path.Contains("Delve") ) {
						if( path.EndsWith("DelveLight") || path.StartsWith("Metadata/Monster") ) {
							continue;
						}
						if( path.EndsWith("DelveWall") ) {
							if( IsTargetable(ent) ) {
								// ImGui_Object("DelveWall", "DelveWall", ent, new HashSet<int>());
								lineText = "Wall";
								lineColor = Color.Cyan;
							}
						} else if ( path.StartsWith("Metadata/Chests/DelveChests") ) {
							var chest = ent.GetComponent<Chest>();
							if ( !IsValid(chest) ) continue;
							if ( chest.IsOpened || chest.IsLocked || !IsTargetable(ent) ) continue;
							if ( path.Contains("SuppliesFlares") ) {
								lineText = "Flares";
								lineColor = Color.Orange;
							} else if ( path.Contains("SuppliesDynamite") ) {
								lineText = "Dynamite";
								lineColor = Color.Orange;
							} else if ( path.Contains("Fossil") ) {
								lineText = "Fossil";
								lineColor = Color.Yellow;
							} else if ( path.Contains("Resonator") ) {
								lineText = "Resonator";
								lineColor = Color.Yellow;
							} else if ( path.Contains("Currency") ) {
								lineText = "Currency";
								lineColor = Color.Yellow;
							} else if ( path.Contains("AzuriteVein") ) {
								lineText = "Azurite";
								lineColor = Color.Cyan;
							}

						}
					}

					if ( lineText != null ) {
						var entPos = Position(ent);
						var textPos = (entPos - playerPos) * .15f;
						DrawLine(WorldToScreen(playerPos), WorldToScreen(entPos), lineColor);
						DrawTextAt(playerPos + textPos, lineText, lineColor);
					}
				}
			}
			return this;
		}
	}
}
