﻿using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE.Plugins {

	public class MinimapIconPlugin : PluginBase {

		public override string Name => "Minimap Icons";

		public override int SortIndex => 1;

		public bool ShowMinions = true;
		public bool ShowEnemies = true;
		public bool ShowChests = true;
		public bool ShowDelvePath = true;

		public bool ShowRareOverhead = true;

		public bool ShowOnMinimap = true;
		public bool ShowOnLargemap = true;

		public int IconSize = 10;

		public override void Render() {
			base.Render();
			var ui = GetUI();
			var map = ui.Map;
			bool largeMapVisible = map.LargeMap?.IsVisibleLocal ?? false;
			bool miniMapVisible = map.MiniMap?.IsVisibleLocal ?? false;
			ImGui.Checkbox("Show Icons on Mini Map", ref ShowOnMinimap);
			if( miniMapVisible ) {
				ImGui.SameLine(); ImGui.TextDisabled("(visible)");
			}
			ImGui.Checkbox("Show Icons on Large Map", ref ShowOnLargemap);
			if( largeMapVisible ) {
				ImGui.SameLine(); ImGui.TextDisabled("(visible)");
			}
			ImGui.SliderInt("Icon Size", ref IconSize, 5, 20);
			ImGui.Separator();
			ImGui.Checkbox("Show Enemies", ref ShowEnemies);
			ImGui.SameLine();
			ImGui.Checkbox("Also Over Head", ref ShowRareOverhead);
			ImGui.Checkbox("Show Minions", ref ShowMinions);
			ImGui.Checkbox("Show Strongboxes", ref ShowChests);
			ImGui.Checkbox("Show Delve Path", ref ShowDelvePath);
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached ) {
				return this;
			}

			var ui = GetUI();
			if ( !IsValid(ui) ) {
				DrawBottomLeftText("MinimapIcon: invalid UI", Color.Yellow);
				return this;
			}

			var map = ui.Map;
			if ( !IsValid(map) ) {
				DrawBottomLeftText("MinimapIcon: invalid Map", Color.Yellow);
				return this;
			}
			bool largeMapVisible = map.LargeMap?.IsVisibleLocal ?? false;

			if ( largeMapVisible && !ShowOnLargemap ) {
				DrawBottomLeftText("MinimapIcon: large map not visible", Color.Yellow);
				return this;
			}

			bool smallMapVisible = map.MiniMap?.IsVisibleLocal ?? false;

			if ( smallMapVisible && !ShowOnMinimap ) {
				DrawBottomLeftText("MinimapIcon: small map not visible", Color.Yellow);
				return this;
			}

			if( !( ShowChests || ShowEnemies || ShowMinions || ShowDelvePath )) { // nothing to show
				return this;
			}

			var player = GetPlayer();
			if( !IsValid(player) ) {
				DrawBottomLeftText("MinimapIcon: invalid player", Color.Yellow);
				return this;
			}

			uint[] deployed = null;
			if ( ShowMinions ) { // get this list ahead of time so we can only iterate GetEntities once
				deployed = (GetPlayer()?.GetComponent<Actor>()?.DeployedObjects.Select(d => d.EntityId) ?? Empty<uint>()).ToArray();
			}
			foreach ( var ent in GetEntities().Take(2000).Where(IsValid) ) {

				// each entity picks an icon to display
				SpriteIcon icon = SpriteIcon.None;
				float iconSize = 1f;

				var path = ent.Path;
				if( path == null ) {
					continue;
				}


				/* Debug:
				if( path.StartsWith("Metadata/Monster") ) {
					// ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
					ImGui.Begin($"ent:{ent.Id} {ent.Path}");
					ImGui.Text($"IsAlive: {IsAlive(ent)}");
					ImGui.Text($"IsHostile: {IsHostile(ent)}");
					ImGui.Text($"IsTargetable: {IsTargetable(ent)}");
					// ImGui.Text("Positioned");
					// ImGui_Object($"Positioned-{ent.Id}", "Positioned", ent.GetComponent<Positioned>(), new HashSet<int>());
					// ImGui.Text("Render");
					// ImGui_Object($"Render-{ent.Id}", "Render", ent.GetComponent<Render>(), new HashSet<int>());
					ImGui.Text("ObjectMagicProperties");
					ImGui_Object($"ObjectMagicProperties-{ent.Id}", "ObjectMagicProperties", ent.GetComponent<ObjectMagicProperties>(), new HashSet<int>());
					ImGui.End();
				}
				if ( path.StartsWith("Metadata/Chest") ) {
			ImGui.Begin("Player Buffs");
			ImGui_Object("Buffs", "Buffs", GetPlayer()?.GetComponent<Buffs>(), new HashSet<int>());
			ImGui.End();
					// ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
					ImGui.Begin($"Unknown Box##{ent.Id}");
					ImGui_Object($"Box##{ent.Id}", "Box", ent, new HashSet<int>());
					ImGui.Text("Chest:");
					ImGui_Object("$Chest-##{ent.Id}", "Chest", ent.GetComponent<Chest>(), new HashSet<int>());
					ImGui.End();
				}
				*/
				if( ent.MinimapIcon.Expires < Time.ElapsedMilliseconds ) {
					ent.MinimapIcon = new Entity.Icon() { Size = 0f, Sprite = SpriteIcon.None };
				}

				if( ent.MinimapIcon.Size > 0f ) {
					icon = ent.MinimapIcon.Sprite;
					iconSize = ent.MinimapIcon.Size;
				} else if ( ShowMinions && deployed.Contains((ushort)ent.Id) ) {
					TryGetMinionIcon(ent, out icon, out iconSize);
				} else if ( ShowEnemies && path.StartsWith("Metadata/Monster") && IsAlive(ent) && IsHostile(ent) && IsTargetable(ent) ) {
					TryGetEnemyIcon(ent, out icon, out iconSize);
				} else if ( ShowChests && path.StartsWith("Metadata/Chest") ) {
					TryGetChestIcon(ent, out icon, out iconSize);
				} else if ( ShowDelvePath && path.EndsWith("DelveLight") ) {
					icon = SpriteIcon.BlightPath;
					iconSize = 1f;
				}

				if ( icon != SpriteIcon.None ) {
					DrawSprite(icon, map.WorldToMap(ent), IconSize * iconSize, IconSize * iconSize);
				}
			}

			return this;
		}

		private bool TryGetChestIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.None;
			iconSize = 1f;
			if ( ent.HasComponent<MinimapIcon>() ) {
				var minimapIcon = ent.GetComponent<MinimapIcon>();
				if( IsValid(minimapIcon) && IsValid(minimapIcon.Name, 4) ) {
					return false;
				}
			}
			var chest = ent.GetComponent<Chest>();
			if ( chest?.IsOpened ?? true ) {
				return false;
			}
			if( ent.MinimapIcon.Size != 0f ) {
				icon = ent.MinimapIcon.Sprite;
				iconSize = ent.MinimapIcon.Size;
				return icon != SpriteIcon.None;
			}
			var path = ent.Path;
			if( !IsValid(path, 10) ) {
				return false;
			}
			// TODO: pre-compile and cache this expression
			if( Regex.IsMatch(path, "^Metadata/Chests/(?:Urn|Basket|Barrel|Chest|SilverChest|PrimevalChest|StatueMakers|.*Rack|Amphora|.*Pot|.*Boulder|Vase|MapChest|.*Cairn|Crate|Cannibal|TemplarChest|InfestationEgg|.*Barrel|.*Wounded|FungalBloom|GoldenChest|KaomChest|C[ao]coon|.*Bundle|PordWounded|Tutorial|TribalChest|.*BonePile|Sarcophagi|GoldPot|CopperChest|Labyrinth/Izaro|VaalBoneChest|Betrayal|Laboratory/|LegionChests)") ) { 
				// set them to None, and never check them again
				ent.MinimapIcon = new Entity.Icon() { Size = 1f, Sprite = SpriteIcon.None };
				return false;
			}
			icon = SpriteIcon.None; // with size = 1f and Icon = None, we only fall through this path scanning once, then assign to ent.MinimapIcon
			iconSize = 1f; // once that assigns 1f, the next frame will hit the branch above and return ent.MinimapIcon values
			if ( path.StartsWith("Metadata/Chests") ) {
				// DrawTextAt(WorldToScreen(Position(ent)), $"Unknown /Chest: {ent.Path}", Color.White);
				if ( path.Contains("Breach/BreachChest") ) {
					icon = SpriteIcon.Breach;
					iconSize = 1.5f;
				} else if ( path.Contains("SideArea/SideAreaChest") ) {
					icon = SpriteIcon.VaalSideArea;
					iconSize = 1.5f;
				} else if ( path.Contains("Abyss/AbyssFinal") ) {
					icon = SpriteIcon.RewardAbyss;
					iconSize = 1.75f;
				} else if ( path.StartsWith("Metadata/Chests/LeaguesExpedition/") ) {
					icon = SpriteIcon.BlueFlag;
					iconSize = 1.1f;
				} else if ( path.EndsWith("/BootyChest") ) {
					icon = SpriteIcon.YellowExclamation;
					iconSize = 1.75f;
				} else if ( path.StartsWith("Metadata/Chests/DelveChests/") ) {
					if ( path.EndsWith("/PathWeapon") ) {
						icon = SpriteIcon.RewardWeapons;
						iconSize = 1.75f;
					} else if ( path.Contains("/DelveAzuriteVeinEncounter") ) {
						icon = SpriteIcon.None;
						iconSize = 1f;
					} else if ( path.Contains("AzuriteVein") ) {
						icon = SpriteIcon.SmallBlueTriangle;
						iconSize = 1.25f;
					} else if ( path.Contains("Currency") ) {
						icon = SpriteIcon.RewardCurrency;
						iconSize = 1.75f;
					} else if ( path.Contains("Trinkets") ) {
						icon = SpriteIcon.RewardJewellery;
						iconSize = 1.75f;
					} else if ( path.Contains("Weapon") ) {
						icon = SpriteIcon.RewardWeapons;
						iconSize = 1.75f;
					} else if ( path.Contains("Resonator") ) {
						icon = SpriteIcon.PCMapArrow;
						iconSize = 1.75f;
					} else if ( path.Contains("Fossil") ) {
						icon = SpriteIcon.RewardFossils;
						iconSize = 1.85f;
					} else if ( path.EndsWith("Essence") ) {
						icon = SpriteIcon.RewardEssences;
						iconSize = 1.95f;
					} else if ( path.Contains("Offering") ) {
						icon = SpriteIcon.RewardLabyrinth;
						iconSize = 1.75f;
					} else if ( path.EndsWith("ShaperItem") ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 1.75f;
					} else if ( path.Contains("Flares") ) {
						icon = SpriteIcon.SmallYellowTriangle;
						iconSize = 1.75f;
					} else if ( path.Contains("SuppliesDynamite") ) {
						icon = SpriteIcon.SmallRedTriangle;
						iconSize = 1.75f;
					}
				} else if ( path.StartsWith("Metadata/Chests/IncursionChest") ) {
					icon = SpriteIcon.RewardGenericItems;
					iconSize = 1.75f;
					if( path.Contains("Currency") ) {
						icon = SpriteIcon.RewardCurrency;
					}
				} else if ( path.StartsWith("Metadata/Chests/LeagueSanctum") ) {
					icon = SpriteIcon.RewardGenericItems;
					iconSize = 1.75f;
				} else if ( path.StartsWith("Metadata/Chests/LeagueHeist") ) {
					if ( path.Contains("PrimaryTarget") ) {
						icon = SpriteIcon.None;
						iconSize = 0f;
					}
				} else if ( path.StartsWith("Metadata/Chests/SynthesisChests") ) {
					icon = SpriteIcon.LargeCyanStar;
					iconSize = 1.5f;
				} else if ( path.StartsWith("Metadata/Chests/StrongBox") ) {
					if ( path.EndsWith("/Arcanist") ) {
						icon = SpriteIcon.RewardCurrency;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/Jeweller") ) {
						icon = SpriteIcon.RewardJewellery;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/Strongbox") || path.EndsWith("/Large") || path.EndsWith("/Ornate") || path.EndsWith("/Artisan") ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/Armory") ) {
						icon = SpriteIcon.RewardArmour;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/StrongboxScarab") ) {
						icon = SpriteIcon.RewardScarabs;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/StrongboxDivination") ) {
						icon = SpriteIcon.RewardDivinationCards;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/Arsenal") ) {
						icon = SpriteIcon.RewardWeapons;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/Gemcutter") ) {
						icon = SpriteIcon.RewardGems;
						iconSize = 1.75f;
					} else if ( path.Contains("/Cartographer") ) {
						icon = SpriteIcon.RewardMaps;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/VaultsOfAtziriUniqueChest") ) {
						icon = SpriteIcon.RewardFragments;
						iconSize = 1.75f;
					} else if ( path.EndsWith("/VaalTempleChest") ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 1.75f;
					} else {
						DrawTextAt(WorldToScreen(Position(ent)), $"Unknown Strongbox: {ent.Path}", Color.White);
						return false;
					}
				} else {
					DrawTextAt(WorldToScreen(Position(ent)), $"Unknown /Chest: {ent.Path}", Color.White);
					return false;
				}
			} else {
				DrawTextAt(WorldToScreen(Position(ent)), $"Unknown Chest: {ent.Path}", Color.White);
				return false;
			}
			// save for 1000ms, then check for opened status etc
			ent.MinimapIcon = new Entity.Icon(icon, iconSize, Time.ElapsedMilliseconds + 1000);
			return true;
		}
		private bool TryGetEnemyIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.MediumRedCircle;
			iconSize = 1f;
			if ( ent.HasComponent<MinimapIcon>() ) {
				return false;
			}
			string path = ent.Path;
			if( path == null ) {
				return false;
			}
			if( ent.MinimapIcon.Size != 0f ) {
				icon = ent.MinimapIcon.Sprite;
				iconSize = ent.MinimapIcon.Size;
				return icon != SpriteIcon.None;
			}
			if ( path.Contains("AfflictionVomitile") || path.Contains("AfflictionVolatile") ) {
				ent.MinimapIcon = new Entity.Icon(SpriteIcon.None, 1f);
				return false;
			}
			bool isHidden = HasBuff(ent, "hidden_monster");
			var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity;
			switch ( rarity ) {
				case Offsets.MonsterRarity.Unique: icon = isHidden ? SpriteIcon.SmallPurpleHexagon : SpriteIcon.MediumPurpleCircle; break;
				case Offsets.MonsterRarity.Rare: icon = isHidden ? SpriteIcon.SmallYellowHexagon : SpriteIcon.MediumYellowCircle; break;
				case Offsets.MonsterRarity.Magic: icon = isHidden ? SpriteIcon.SmallBlueHexagon : SpriteIcon.MediumBlueCircle; break;
				case Offsets.MonsterRarity.White: icon = isHidden ? SpriteIcon.SmallRedHexagon : SpriteIcon.MediumRedCircle; break;
			}
			// save the current icon for 300ms, then it will expire and check again for hidden status, etc
			// ent.MinimapIcon = new Entity.Icon(icon, iconSize, Time.ElapsedMilliseconds + 300);
			if ( ShowRareOverhead && rarity >= Offsets.MonsterRarity.Rare ) {
				var render = ent.GetComponent<Render>();
				if ( IsValid(render) ) {
					var overhead = WorldToScreen(render.Position + new Vector3(0, 0, -1.5f * render.Bounds.Z));
					DrawSprite(icon, overhead, IconSize * 4, IconSize * 4);
					/*
					ImGui.Begin("debug_overhead");
					ImGui.Text("Overhead vector result:");
					ImGui_Object("overhead", "overhead", overhead, new HashSet<int>());
					ImGui.Text("Render component:");
					ImGui_Object("render", "render", render, new HashSet<int>());
					ImGui.Text("Camera:");
					ImGui_Object("camera", "camera", PoEMemory.GameRoot.InGameState.WorldData.Camera, new HashSet<int>());
					ImGui_Address(PoEMemory.GameRoot.InGameState.WorldData.Address
						+ GetOffset<Offsets.WorldData>("Camera"),
						"Camera Address", "Camera");
					ImGui.End();
					*/
				}
			}
			return true;
		}

		private bool TryGetMinionIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.None;
			iconSize = 1f;

			if ( ent?.Path?.StartsWith("Metadata/Monsters/AnimatedItem") ?? false ) {
				icon = SpriteIcon.SmallGreenCircle;
			} else if ( IsAlive(ent) ) {
				icon = SpriteIcon.GreenX;
			}

			if ( icon != SpriteIcon.None ) {
				// cache the icon for 1 second
				ent.MinimapIcon = new Entity.Icon(icon, iconSize, Time.ElapsedMilliseconds + 1000);
				return true;
			}
			return false;
		}
	}
}
