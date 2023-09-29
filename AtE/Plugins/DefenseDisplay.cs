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
		public bool ShowHighestCorpseLife = false;

		private bool ShowStatDebugControls = false;

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
			ImGui.Checkbox("Show Highest Corpse Life", ref ShowHighestCorpseLife);
			ImGui.Separator();
			ImGui.Checkbox("Debug Game Stats", ref ShowStatDebugControls);
			if ( ShowStatDebugControls ) {
				ImGui.BeginListBox("", new Vector2(-1, -2));

				ImGui.InputText("filter:", ref inputFilter, 32);

				var p = GetPlayer();
				if ( IsValid(p) ) {
					var stats = p.GetComponent<Stats>();
					foreach ( var entry in stats.Entries.Where( (kv) => kv.Key.ToString().Contains(inputFilter) || kv.Value.ToString().Contains(inputFilter) )) {
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
			if ( Paused || !Enabled ) {
				return this; // keep it in the PluginBase.Machine, but dont do anything
			}

			if ( !PoEMemory.IsAttached ) {
				return this;
			}

			if ( !(PoEMemory.TargetHasFocus || Overlay.HasFocus) ) {
				return this;
			}

			if ( PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true ) {
				return this;
			}

			var ui = GetUI();
			if ( !IsValid(ui) ) {
				return this;
			}

			if ( ShowPlayerDefenses ) {
				var bubble = ui.LifeBubble?.GetClientRect() ?? RectangleF.Empty;
				if ( bubble != RectangleF.Empty ) {
					var spot = new Vector2(bubble.X + bubble.Width + 4, bubble.Y - 7);
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

						stats.TryGetValue(GameStat.AttackBlockPct, out int attackBlock);
						stats.TryGetValue(GameStat.SpellBlockPct, out int spellBlock);
						DrawTextAt(1, spot, $"Block:     {attackBlock}% {spellBlock}%", Color.Cornsilk, size);
					}
				}

				if ( ShowEnemyResistChaos || ShowEnemyResistCold || ShowEnemyResistFire || ShowEnemyResistLightning || ShowEnemyWitherStacks || ShowEnemyPoisonStacks || ShowHighestCorpseLife ) {
					float maxCorpseLife = 0;
					Vector3 maxCorpseLocation = Vector3.Zero;
					var player = GetPlayer();
					if ( !IsValid(player) ) {
						return this;
					}
					Vector3 playerPos = player.Position;
					foreach ( var ent in GetEntities().Take(200) ) {
						bool isMonster = ent?.Path?.StartsWith("Metadata/Monsters") ?? false;
						if ( !isMonster ) {
							continue;
						}

						bool isAlive = IsAlive(ent);
						if ( isAlive && IsHostile(ent) ) {
							var s = ent.GetComponent<Stats>()?.GetStats();
							if ( s != null ) {
								var str = "";
								if ( ShowEnemyResistChaos && s.TryGetValue(GameStat.ChaosDamageResistancePct, out int chaos) ) {
									str += $"C:{chaos} ";
								}
								if ( ShowEnemyResistFire && s.TryGetValue(GameStat.FireDamageResistancePct, out int fire) ) {
									str += $"F:{fire} ";
								}
								if ( ShowEnemyResistCold && s.TryGetValue(GameStat.ColdDamageResistancePct, out int cold) ) {
									str += $"C:{cold} ";
								}
								if ( ShowEnemyResistLightning && s.TryGetValue(GameStat.LightningDamageResistancePct, out int lightning) ) {
									str += $"L:{lightning} ";
								}
								// TODO: Wither and Poison (count the buffs)
								if ( str.Length > 0 ) {
									DrawTextAt(ent, str, Color.White);
								}
							}
						} else if ( (!isAlive) && ShowHighestCorpseLife ) {
							int maxLife = ent?.GetComponent<Life>()?.MaxHP ?? 0;
							Vector3 loc = Position(ent);
							float dist = DistanceSq(loc, playerPos);
							if ( dist < 250000 && maxLife > maxCorpseLife ) {
								maxCorpseLife = maxLife;
								maxCorpseLocation = Position(ent);
							}
						}
					}
					if ( ShowHighestCorpseLife && maxCorpseLocation != Vector3.Zero ) {
						DrawTextAt(maxCorpseLocation, $"hp:{maxCorpseLife}", Color.White);
					}
				}
			}

			return this;
		}
	}
}
