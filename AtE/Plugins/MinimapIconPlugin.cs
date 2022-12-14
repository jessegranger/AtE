using ImGuiNET;
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
				return this;
			}

			var map = ui.Map;
			if ( !IsValid(map) ) {
				return this;
			}
			bool largeMapVisible = map.LargeMap?.IsVisibleLocal ?? false;

			if ( largeMapVisible && !ShowOnLargemap ) {
				return this;
			}

			bool smallMapVisible = map.MiniMap?.IsVisibleLocal ?? false;

			if ( smallMapVisible && !ShowOnMinimap ) {
				return this;
			}

			if( !( ShowChests || ShowEnemies || ShowMinions || ShowDelvePath )) { // nothing to show
				return this;
			}

			var player = GetPlayer();
			if( !IsValid(player) ) {
				return this;
			}

			ushort[] deployed = null;
			if ( ShowMinions ) { // get this list ahead of time so we can only iterate GetEntities once
				deployed = (GetPlayer()?.GetComponent<Actor>()?.DeployedObjects.Select(d => d.EntityId) ?? Empty<ushort>()).ToArray();
			}
			foreach ( var ent in GetEntities().Take(2000).Where(IsValid) ) {

				// each entity picks an icon to display
				SpriteIcon icon = SpriteIcon.None;
				float iconSize = 1f;

				var path = ent.Path;
				if( path == null ) {
					continue;
				}

				if( ShowMinions && deployed.Contains((ushort)ent.Id) && IsAlive(ent) ) {
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
			if( Regex.IsMatch(path, "^Metadata/Chests/(?:Urn|Basket|Barrel|Chest|Pot|Boulder|Vase|SnowCairn|TemplarChest|InfestationEgg|TribalChest|Labratory/RatCrate)") ) { 
				ent.MinimapIcon = new Entity.Icon() { Size = 1f, Sprite = SpriteIcon.None };
				return false;
			}
			icon = SpriteIcon.None; // with size = 1f and Icon = None, we only fall through this path scanning once, then assign to ent.MinimapIcon
			iconSize = 1f; // once that assigns 1f, the next frame will hit the branch above and return ent.MinimapIcon values
			if ( path.StartsWith("Metadata/Chests") ) {
				if ( path.Contains("Abyss/AbyssFinal") ) {
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
					} else if ( path.Contains("/DelveAzuriteVein") ) {
						icon = SpriteIcon.SmallBlueTriangle;
						iconSize = 1.25f;
					} else if ( path.EndsWith("Essence") ) {
						icon = SpriteIcon.RewardEssences;
						iconSize = 1.85f;
					} else if ( path.EndsWith("ShaperItem") ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 1.75f;
					}
				} else if ( path.StartsWith("Metadata/Chests/LeagueSanctum") ) {
					icon = SpriteIcon.RewardGenericItems;
					iconSize = 1.75f;
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
					} else {
						ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
						ImGui.Begin($"Unknown Strongbox##{ent.Id}");
						ImGui_Object($"Strongbox##{ent.Id}", "Strongbox", ent, new HashSet<int>());
						ImGui.End();
					}
				} else {
					ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
					ImGui.Begin($"Unknown /Chest##{ent.Id}");
					ImGui.Text(path);
					ImGui_Object($"Chest##{ent.Id}", "Chest", ent, new HashSet<int>());
					ImGui.End();
				}
			} else {
				ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
				ImGui.Begin($"Unknown Chest##{ent.Id}");
				ImGui.Text(path);
				ImGui_Object($"Chest##{ent.Id}", "Chest", ent, new HashSet<int>());
				ImGui.End();
			}
			ent.MinimapIcon = new Entity.Icon() { Size = iconSize, Sprite = icon };
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
				ent.MinimapIcon = new Entity.Icon() { Size = 1f, Sprite = SpriteIcon.None };
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
			if ( ShowRareOverhead && rarity >= Offsets.MonsterRarity.Rare ) {
				var render = ent.GetComponent<Render>();
				if ( IsValid(render) ) {
					var overhead = WorldToScreen(render.Position + new Vector3(0, 0, -1.5f * render.Bounds.Z));
					DrawSprite(icon, overhead, IconSize * 4, IconSize * 4);
				}
			}
			return true;
		}

		private bool TryGetMinionIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.GreenX;
			iconSize = 1f;
			return IsAlive(ent);
		}
	}
}
