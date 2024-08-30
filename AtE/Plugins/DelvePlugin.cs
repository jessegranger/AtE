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
			var player = GetPlayer();
			if( IsValid(player) && TryGetBuffValue(player, "delve_degen_buff", out int stacks ) ) {
				ImGui.Text($"Current Stacks: {stacks}");
			}
		}

		private long lastFlare = 0;

		private Vector2 Rotate(Vector2 v, double radians) {
			double cosB = Math.Cos(radians);
			double sinB = Math.Sin(radians);
			return new Vector2(
				 (float)((cosB * v.X) - (sinB * v.Y)),
				 (float)((sinB * v.X) + (cosB * v.Y))
			);
		}

		private void DrawLineAndLabel(Vector2 playerScreenPos, Entity ent, Color color, string label) {
			Vector3 entPos = Position(ent);
			Vector2 entScreenPos = WorldToScreen(entPos);
			Vector2 dv = entScreenPos - playerScreenPos;
			float mag = dv.Length();
			dv = Vector2.Normalize(dv);
			Vector2 textLabelPos = playerScreenPos + Vector2.Multiply(dv, Math.Min(mag, 200));
			Vector2 lineEndPosA = playerScreenPos + Vector2.Multiply(dv, Math.Min(mag, 400));
			Vector2 lineEndPosB = playerScreenPos + Rotate(Vector2.Multiply(dv, Math.Min(mag, 390)), .02);
			Vector2 lineEndPosC = playerScreenPos + Rotate(Vector2.Multiply(dv, Math.Min(mag, 390)), -.02);
			DrawLine(playerScreenPos, lineEndPosA, color);
			DrawLine(lineEndPosA, lineEndPosB, color);
			DrawLine(lineEndPosA, lineEndPosC, color);
			DrawTextAt(textLabelPos, label, color);
		}

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

				Vector2 playerScreenPos = WorldToScreen(playerPos);
				if( playerScreenPos == Vector2.Zero ) {
					return this;
				}

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
						// debug:
						// DrawTextAt(ent, ent.Path, Color.White);
						if ( path.EndsWith("DelveWall") ) {
							if ( IsTargetable(ent) ) {
								// ImGui_Object("DelveWall", "DelveWall", ent, new HashSet<int>());
								DrawLineAndLabel(playerScreenPos, ent, Color.Cyan, "Wall");
								icon = SpriteIcon.DelveWall;
								iconSize = 2f;
							} else {
								// DrawTextAt(ent, "NotTargetable", Color.White);
							}
						} else if ( path.StartsWith("Metadata/Chests/DelveChests") ) {
							var chest = ent.GetComponent<Chest>();
							if( !IsValid(chest) ) {
								continue;
							}
							if( chest.IsOpened ) {
								// DrawTextAt(ent, "IsOpened", Color.White);
								continue;
							}
							if( chest.IsLocked ) {
								// DrawTextAt(ent, "IsLocked", Color.White);
								// ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
								// string label = $"Chest{ent.Id}";
								// ImGui.Begin(label);
								// ImGui_Object(label, label, chest, new HashSet<int>());
								// ImGui.End();
								continue;
							}
							if( !IsTargetable(ent) ) {
								// DrawTextAt(ent, "NotTargetable", Color.White);
								continue;
							}
							if ( !IsValid(chest) || chest.IsOpened || chest.IsLocked || !IsTargetable(ent) ) {
								continue;
							}

							if ( HighlightSupplies && path.Contains("SuppliesFlares") ) {
								DrawLineAndLabel(playerScreenPos, ent, Color.Gold, "Flares");
								icon = SpriteIcon.YellowX;
								iconSize = 1f;
							} else if ( HighlightSupplies && path.Contains("SuppliesDynamite") ) {
								DrawLineAndLabel(playerScreenPos, ent, Color.Orange, "Dynamite");
								icon = SpriteIcon.OrangeX;
								iconSize = 1f;
							} else if ( HighlightFossils && path.Contains("Fossil") ) {
								DrawLineAndLabel(playerScreenPos, ent, Color.Yellow, "Fossil");
								icon = SpriteIcon.MediumGreenStar;
								iconSize = 2f;
							} else if ( HighlightResonators && path.Contains("Resonator") ) {
								DrawLineAndLabel(playerScreenPos, ent, Color.Yellow, "Resonator");
								icon = SpriteIcon.MediumPurpleStar;
								iconSize = 2f;
							} else if ( HighlightCurrency && path.Contains("Currency") ) {
								DrawLineAndLabel(playerScreenPos, ent, Color.Yellow, "Currency");
								icon = SpriteIcon.MediumYellowStar;
								iconSize = 2f;
							} else if (  HighlightAzurite &&  path.Contains("AzuriteVein") ) {
								DrawLineAndLabel(playerScreenPos, ent, Color.Cyan, "Azurite");
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
