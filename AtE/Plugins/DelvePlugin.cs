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
		public bool HighlightSupplies = true;
		public bool HighlightCurrency = true;
		public bool HighlightFossils = true;
		public bool HighlightResonators = true;
		public bool HighlightAzurite = true;

		public bool UseFlaresInDarkness = true;
		public int UseFlaresAtDarkness = 12;
		public HotKey FlareKey = new HotKey(Keys.D6);

		// public bool ShowHighestCorpseLife = true;

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Highlight Walls", ref HighlightWalls);
			ImGui.Checkbox("Highlight Supplies", ref HighlightSupplies);
			ImGui.Checkbox("Highlight Currency", ref HighlightCurrency);
			ImGui.Checkbox("Highlight Fossils", ref HighlightFossils);
			ImGui.Checkbox("Highlight Resonators", ref HighlightResonators);
			ImGui.Checkbox("Highlight Azurite", ref HighlightAzurite);
			ImGui.Checkbox("##Use Flares Checkbox", ref UseFlaresInDarkness);
			ImGui.SameLine();
			ImGui_HotKeyButton("Use Flares", ref FlareKey);
			ImGui.AlignTextToFramePadding();
			ImGui.Text("At darkness stacks:");
			ImGui.SameLine();
			ImGui.SliderInt("##useFlaresAtDarkness", ref UseFlaresAtDarkness, 2, 20);
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

				var playerGridPos = GridPosition(player);
				if( playerGridPos == Vector2.Zero ) {
					return this;
				}

				if( UseFlaresInDarkness && TryGetBuffValue(player, "delve_degen_buff", out int stacks ) && stacks >= UseFlaresAtDarkness ) {
					long sinceLastFlare = Time.ElapsedMilliseconds - lastFlare;
					if( sinceLastFlare > 1000 ) {
						lastFlare += sinceLastFlare;
						FlareKey.PressImmediate();
					}
				}
				if( ! (HighlightWalls || HighlightAzurite || HighlightCurrency || HighlightFossils || HighlightResonators || HighlightSupplies) ) {
					return this;
				}

				SpriteIcon icon = SpriteIcon.None;
				float iconSize = 1f;

				float maxCorpseLife = 0;
				Vector3 maxCorpseLocation = Vector3.Zero;

				foreach ( var ent in GetEntities() ) {
					string path = ent.Path;
					if ( path == null || path.Length < 16 ) {
						continue;
					}
					icon = SpriteIcon.None;
					iconSize = 1f;
					bool isMonster = path.StartsWith("Metadata/Monsters");

					/*
					if( ShowHighestCorpseLife && isMonster && !IsAlive(ent) ) {
						int maxLife = ent?.GetComponent<Life>()?.MaxHP ?? 0;
						Vector3 loc = Position(ent);
						float dist = DistanceSq(loc, playerPos);
						if( dist < 250000 && maxLife > maxCorpseLife ) {
							maxCorpseLife = maxLife;
							maxCorpseLocation = Position(ent);
						}
					}
					*/

					if ( path.Contains("Delve") ) {
						if ( isMonster || path.EndsWith("DelveLight") ) {
							continue;
						}
						// DrawTextAt(ent, ent.Path, Color.White);
						if ( path.EndsWith("DelveWall") ) {
							if ( IsTargetable(ent) ) {
								// ImGui_Object("DelveWall", "DelveWall", ent, new HashSet<int>());
								icon = SpriteIcon.DelveWall;
								iconSize = 2f;
							}
						} else if ( path.StartsWith("Metadata/Chests/DelveChests") ) {
							var chest = ent.GetComponent<Chest>();
							if ( !IsValid(chest) || chest.IsOpened || chest.IsLocked || !IsTargetable(ent) ) {
								continue;
							}

							if ( HighlightSupplies && path.Contains("SuppliesFlares") ) {
								icon = SpriteIcon.YellowX;
								iconSize = 1f;
							} else if ( HighlightSupplies && path.Contains("SuppliesDynamite") ) {
								icon = SpriteIcon.OrangeX;
								iconSize = 1f;
							} else if ( HighlightFossils && path.Contains("Fossil") ) {
								icon = SpriteIcon.MediumGreenStar;
								iconSize = 2f;
							} else if ( HighlightResonators && path.Contains("Resonator") ) {
								icon = SpriteIcon.MediumPurpleStar;
								iconSize = 2f;
							} else if ( HighlightCurrency && path.Contains("Currency") ) {
								icon = SpriteIcon.MediumYellowStar;
								iconSize = 2f;
							} else if (  HighlightAzurite &&  path.Contains("AzuriteVein") ) {
								icon = SpriteIcon.MediumCyanStar;
								iconSize = 2f;
							}

						}
					}

					if ( icon != SpriteIcon.None && iconSize > 0f ) {
						ent.MinimapIcon = new Entity.Icon() { Size = iconSize, Sprite = icon };
					}
				}

				if( maxCorpseLocation != Vector3.Zero ) {
					DrawTextAt(maxCorpseLocation, $"{maxCorpseLife}", Color.White);
				}
			}
			return this;
		}
	}
}
