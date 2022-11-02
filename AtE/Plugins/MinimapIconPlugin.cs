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

		public bool ShowMinions = true;
		public bool ShowAllies = true;
		public bool ShowEnemies = true;
		public bool ShowChests = true;

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
			ImGui.Checkbox("Show Allies", ref ShowAllies);
			ImGui.Checkbox("Show Minions", ref ShowMinions);
			ImGui.Checkbox("Show Strongboxes", ref ShowChests);
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled ) return this;
			var ui = GetUI();
			if ( !IsValid(ui) ) return this;

			var map = GetUI().Map;
			if ( !IsValid(map) ) return this;
			if ( map.LargeMap.IsVisibleLocal && !ShowOnLargemap ) return this;
			if ( map.MiniMap.IsVisibleLocal && !ShowOnMinimap ) return this;

			foreach ( var ent in GetEntities().Where(IsValid) ) {
				bool hostile = ent.IsHostile;
				if ( ShowAllies && !hostile && ent.HasComponent<Player>() ) {
					DrawSprite(SpriteIcon.BlueDot, map.WorldToMap(ent), IconSize, IconSize);
					continue;
				}
				if ( ShowEnemies && hostile && ent.HasComponent<Targetable>() && IsAlive(ent) ) {
					SpriteIcon icon = SpriteIcon.RedDot;
					switch( ent.GetComponent<ObjectMagicProperties>().Rarity ) {
						case Offsets.MonsterRarity.Unique: icon = SpriteIcon.OrangeDot; break;
						case Offsets.MonsterRarity.Rare: icon = SpriteIcon.YellowDot; break;
						case Offsets.MonsterRarity.Magic: icon = SpriteIcon.BlueDot; break;
						case Offsets.MonsterRarity.White: icon = SpriteIcon.RedDot; break;
					}
					DrawSprite(icon, map.WorldToMap(ent), IconSize, IconSize);
					continue;
				}
				if ( ShowChests && ent.HasComponent<Chest>() ) {
					var chest = ent.GetComponent<Chest>();
					if ( chest.IsOpened || ! chest.IsStrongbox ) continue;
					ImGui.SetNextWindowPos(WorldToScreen(ent.GetComponent<Render>()?.Position ?? Vector3.Zero));
					ImGui.Begin($"Debug Chest##{ent.Id}");
					ImGui_Object($"Chest##{ent.Id}", "Chest", chest, new HashSet<int>());
					ImGui.End();
					DrawSprite(SpriteIcon.Chest, map.WorldToMap(ent), IconSize*2, IconSize*2);
				}
			}

			if( ShowMinions ) {
				foreach(var obj in GetPlayer()?.GetComponent<Actor>()?.DeployedObjects ?? Empty<DeployedObject>() ) {
					var ent = obj.GetEntity();
					if( IsValid(ent) ) {
						if( IsAlive(ent) ) {
							DrawSprite(SpriteIcon.GreenDot, map.WorldToMap(ent), IconSize, IconSize);
						} else {
							DrawSprite(SpriteIcon.Skull, map.WorldToMap(ent), IconSize, IconSize);
						}
					}
				}
			}
			return this;
		}
	}
}
