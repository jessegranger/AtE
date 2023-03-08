using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;
using static AtE.Offsets;

namespace AtE.Plugins {

	public class DefenseDisplay : PluginBase {

		// will inherit: bool Enabled

		public override string Name => "Display Defenses";

		private string inputFilter = "";

		public bool ShowPlayerDefenses = false;
		public bool ShowEnemyResistFire = false;
		public bool ShowEnemyResistCold = false;
		public bool ShowEnemyResistLightning = false;
		public bool ShowEnemyResistChaos = false;
		public bool ShowEnemyPoisonStacks = false;
		public bool ShowEnemyWitherStacks = false;

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Player", ref ShowPlayerDefenses);
			ImGui.Checkbox("Show Enemy Chaos", ref ShowEnemyResistChaos);
			ImGui.Checkbox("Show Enemy Fire", ref ShowEnemyResistFire);
			ImGui.Checkbox("Show Enemy Cold", ref ShowEnemyResistCold);
			ImGui.Checkbox("Show Enemy Lightning", ref ShowEnemyResistLightning);
			if ( false ) {
				ImGui.BeginListBox("", new Vector2(-1, -2));

				ImGui.InputText("filter:", ref inputFilter, 32);

				var p = GetPlayer();
				if ( IsValid(p) ) {
					var stats = p.GetComponent<Stats>();
					foreach ( var entry in stats.Entries.Where( (kv) => kv.Key.ToString().Contains(inputFilter))) {
						ImGui.Text(entry.Key + " = " + entry.Value);
					}
				}
				ImGui.EndListBox();
			}
		}

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached ) {
				var ui = GetUI();
				if ( ShowPlayerDefenses && IsValid(ui) ) {
					var bubble = ui.LifeBubble?.GetClientRect() ?? RectangleF.Empty;
					if ( bubble != RectangleF.Empty ) {
						var spot = new Vector2(bubble.X + bubble.Width + 4, bubble.Y + 1);
						var size = ImGui.GetFontSize() - 1f;
						var stats = GetPlayer()?.GetComponent<Stats>()?.GetStats();
						if ( stats != null ) {
							stats.TryGetValue(GameStat.FireDamageResistancePct, out int fireRes);
							stats.TryGetValue(GameStat.UncappedFireDamageResistancePct, out int uncappedFireRes);
							DrawTextAt(1, spot, $"Fire:      {fireRes}% ({uncappedFireRes}%)", Color.Coral, size);

							stats.TryGetValue(GameStat.ColdDamageResistancePct, out int coldRes);
							stats.TryGetValue(GameStat.UncappedColdDamageResistancePct, out int uncappedColdRes);
							DrawTextAt(1, spot, $"Cold:      {coldRes}% ({uncappedColdRes}%)", Color.Cyan, size);

							stats.TryGetValue(GameStat.LightningDamageResistancePct, out int lightningRes);
							stats.TryGetValue(GameStat.UncappedLightningDamageResistancePct, out int uncappedLightningRes);
							DrawTextAt(1, spot, $"Lightning: {lightningRes}% ({uncappedLightningRes}%)", Color.Yellow, size);

							stats.TryGetValue(GameStat.ChaosDamageResistancePct, out int chaosRes);
							stats.TryGetValue(GameStat.UncappedChaosDamageResistancePct, out int uncappedChaosRes);
							DrawTextAt(1, spot, $"Chaos:     {chaosRes}% ({uncappedChaosRes}%)", Color.Violet, size);

							stats.TryGetValue(GameStat.PhysicalDamageReductionRating, out int armourRating);
							stats.TryGetValue(GameStat.DisplayEstimatedPhysicalDamageReducitonPct, out int armourPct);
							DrawTextAt(1, spot, $"Armour:    {armourRating} ({armourPct}%)", Color.Lavender, size);

							stats.TryGetValue(GameStat.EvasionRating, out int evasionRating);
							stats.TryGetValue(GameStat.ChanceToEvadePct, out int evadeChance);
							DrawTextAt(1, spot, $"Evasion:   {evasionRating} ({evadeChance}%)", Color.Cornsilk, size);
						}
					}

					if( ShowEnemyResistChaos || ShowEnemyResistCold || ShowEnemyResistFire || ShowEnemyResistLightning || ShowEnemyWitherStacks || ShowEnemyPoisonStacks ) {
						foreach( var enemy in NearbyEnemies(100f) ) {
							if ( IsAlive(enemy) ) {
								var s = enemy.GetComponent<Stats>()?.GetStats();
								if ( s != null ) {
									var str = "";
									if ( ShowEnemyResistChaos && s.TryGetValue(GameStat.ChaosDamageResistancePct, out int chaos) ) {
										str += $"C:{chaos} ";
									}
									if ( ShowEnemyResistFire && s.TryGetValue(GameStat.FireDamageResistancePct, out int fire) ) {
										str += $"F:{fire}";
									}
									if ( ShowEnemyResistCold && s.TryGetValue(GameStat.ColdDamageResistancePct, out int cold) ) {
										str += $"C:{cold} ";
									}
									if ( ShowEnemyResistLightning && s.TryGetValue(GameStat.LightningDamageResistancePct, out int lightning) ) {
										str += $"L:{lightning} ";
									}
									// TODO: Wither and Poison (count the buffs)
									if ( str.Length > 0 ) {
										DrawTextAt(enemy, str, Color.White);
									}
								}
							}
						}
					}
				}
			}
			return this;
		}
	}
}
