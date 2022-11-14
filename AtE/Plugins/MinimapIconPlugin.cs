using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE.Plugins {

	public class MinimapIconPlugin : PluginBase {

		public override string Name => "Minimap Icons";

		public override int SortIndex => 1;

		public bool ShowMinions = true;
		public bool ShowEnemies = true;
		public bool ShowChests = true;

		public bool ShowRareOverhead = true;

		public bool ShowOnMinimap = true;
		public bool ShowOnLargemap = true;

		public int IconSize = 10;

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Icons on Mini Map", ref ShowOnMinimap);
			ImGui.Checkbox("Show Icons on Large Map", ref ShowOnLargemap);
			ImGui.SliderInt("Icon Size", ref IconSize, 5, 20);
			ImGui.Separator();
			ImGui.Checkbox("Show Enemies", ref ShowEnemies);
			ImGui.SameLine();
			ImGui.Checkbox("Also Over Head", ref ShowRareOverhead);
			ImGui.Checkbox("Show Minions", ref ShowMinions);
			ImGui.SameLine();
			ImGui_HelpMarker("Currently expensive.");
			ImGui.Checkbox("Show Strongboxes", ref ShowChests);
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

			if ( map.LargeMap.IsVisibleLocal && !ShowOnLargemap ) {
				return this;
			}

			if ( map.MiniMap.IsVisibleLocal && !ShowOnMinimap ) {
				return this;
			}

			int totalEntityCount = 0;
			foreach ( var ent in GetEntities().Where(IsValid) ) {
				totalEntityCount += 1;
				if ( totalEntityCount > 2000 ) {
					break; // something probably wrong
				}
				if ( ent.HasComponent<MinimapIcon>() ) {
					continue;
				}

				bool hostile = ent.IsHostile;
				if ( ShowEnemies && hostile && ent.HasComponent<Targetable>() && IsAlive(ent) ) {
					if( ent.HasComponent<DiesAfterTime>() ) { // maybe not right, do some unique bosses have this?
						continue;
					}
					SpriteIcon icon = SpriteIcon.MediumRedCircle;
					bool isHidden = HasBuff(ent, "hidden_monster");
					var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity;
					switch( rarity ) {
						case Offsets.MonsterRarity.Unique:
							icon = isHidden ? SpriteIcon.SmallPurpleHexagon: SpriteIcon.MediumPurpleCircle; break;
						case Offsets.MonsterRarity.Rare:
							icon = isHidden ? SpriteIcon.SmallYellowHexagon: SpriteIcon.MediumYellowCircle; break;
						case Offsets.MonsterRarity.Magic:
							icon = isHidden ? SpriteIcon.SmallBlueHexagon: SpriteIcon.MediumBlueCircle; break;
						case Offsets.MonsterRarity.White:
							icon = isHidden ? SpriteIcon.SmallRedHexagon: SpriteIcon.MediumRedCircle; break;
					}
					DrawSprite(icon, map.WorldToMap(ent), IconSize, IconSize);
					if( ShowRareOverhead && rarity >= Offsets.MonsterRarity.Rare ) {
						var render = ent.GetComponent<Render>();
						var bounds = render.Bounds;
						DrawSprite(icon, WorldToScreen(render.Position + new Vector3(0, 0, -1.5f * bounds.Z)), IconSize*4, IconSize*4);

					}
					continue;
				}
				if ( ShowChests && ent.HasComponent<Chest>() ) {
					var path = ent.Path;
					var chest = ent.GetComponent<Chest>();
					if ( chest.IsOpened ) {
						continue;
					}
					var icon = SpriteIcon.RewardGenericItems;
					if ( path.StartsWith("Metadata/Chests/StrongBoxes") ) {
						if ( path.EndsWith("/Arcanist") ) {
							icon = SpriteIcon.RewardCurrency;
						} else if ( path.EndsWith("/Jeweller") ) {
							icon = SpriteIcon.RewardJewellery;
						} else if ( path.EndsWith("/Strongbox") || path.EndsWith("/Large") || path.EndsWith("/Ornate") ) {
							icon = SpriteIcon.RewardGenericItems;
						} else if ( path.EndsWith("/Armory") ) {
							icon = SpriteIcon.RewardArmour;
						} else if ( path.EndsWith("/StrongboxScarab") ) {
							icon = SpriteIcon.RewardScarabs;
						} else if ( path.EndsWith("/Artisan") ) {
							icon = SpriteIcon.RewardGenericItems;
						} else if ( path.EndsWith("/Arsenal") ) {
							icon = SpriteIcon.RewardWeapons;
						} else {
							ImGui.SetNextWindowPos(WorldToScreen(ent.GetComponent<Render>()?.Position ?? Vector3.Zero));
							ImGui.Begin($"Debug Chest##{ent.Id}");
							ImGui_Object($"Chest##{ent.Id}", "Chest", ent, new HashSet<int>());
							ImGui.End();
						}
						var mapPos = map.WorldToMap(ent);
						/*
						var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.Error;
						float raritySize = IconSize * 2f;
						switch ( rarity ) {
							case Offsets.MonsterRarity.White: DrawSprite(SpriteIcon.MediumCyanCircle, mapPos, raritySize, raritySize); break;
							case Offsets.MonsterRarity.Magic: DrawSprite(SpriteIcon.MediumBlueCircle, mapPos, raritySize, raritySize); break;
							case Offsets.MonsterRarity.Rare: DrawSprite(SpriteIcon.MediumYellowCircle, mapPos, raritySize, raritySize); break;
							case Offsets.MonsterRarity.Unique: DrawSprite(SpriteIcon.MediumPurpleCircle, mapPos, raritySize, raritySize); break;
						}
						*/
						float iconSize = IconSize * 1.75f;
						DrawSprite(icon, map.WorldToMap(ent), iconSize, iconSize);
					}
				}
			}

			if( ShowMinions ) {
				foreach(var obj in GetPlayer()?.GetComponent<Actor>()?.DeployedObjects ?? Empty<DeployedObject>() ) {
					var ent = obj.GetEntity();
					if ( IsAlive(ent) ) {
						DrawSprite(SpriteIcon.MediumGreenCircle, map.WorldToMap(ent), IconSize, IconSize);
					}
				}
			}
			return this;
		}
	}
}
