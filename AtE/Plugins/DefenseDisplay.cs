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
		public bool ShowEnemyImpaleStacks = false;
		public bool ShowEnemyPoisonStacks = false;
		public bool ShowEnemyEnergyStacks = false;
		public bool ShowEnemyWitherStacks = false;
		public bool ShowEnemyGraspingVines = false;
		public bool ShowHighestCorpseLife = false;

		private bool ShowStatDebugControls = false;
		private bool ShowEnemyComponents = false;
		private bool ShowEnemyBuffs = false;

		private HashSet<string> seenBuffs = new HashSet<string>();

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Player Defenses", ref ShowPlayerDefenses);
			ImGui.Checkbox("Show Enemy Chaos", ref ShowEnemyResistChaos);
			ImGui.Checkbox("Show Enemy Fire", ref ShowEnemyResistFire);
			ImGui.Checkbox("Show Enemy Cold", ref ShowEnemyResistCold);
			ImGui.Checkbox("Show Enemy Lightning", ref ShowEnemyResistLightning);
			ImGui.Checkbox("Show Highest Corpse Life", ref ShowHighestCorpseLife);
			ImGui.Text("Debuffs:"); ImGui.SameLine();  ImGui_HelpMarker("shown on Rare and Unique");
			ImGui.Separator();
			ImGui.Checkbox("Show Enemy Energy Stacks", ref ShowEnemyEnergyStacks);
			ImGui.Checkbox("Show Enemy Impale Stacks", ref ShowEnemyImpaleStacks);
			ImGui.Checkbox("Show Enemy Poison Stacks", ref ShowEnemyPoisonStacks);
			ImGui.Checkbox("Show Enemy Wither Stacks", ref ShowEnemyWitherStacks);
			ImGui.Checkbox("Show Enemy Grasping Vines", ref ShowEnemyGraspingVines);
			ImGui.Text("Debugging:");
			ImGui.Separator();
			ImGui.Checkbox("Debug Enemy Components", ref ShowEnemyComponents);
			ImGui.Checkbox("Debug Enemy Buffs", ref ShowEnemyBuffs);
			ImGui.SameLine();
			if( ImGui.Button("Clear##seenBuffs") ) {
				seenBuffs.Clear();
			}
			ImGui.Checkbox("Debug Game Stats", ref ShowStatDebugControls);
			if ( ShowStatDebugControls ) {

				ImGui.Separator();
				ImGui.AlignTextToFramePadding();
				ImGui.Text("filter:"); ImGui.SameLine();
				ImGui.InputText("##filter:", ref inputFilter, 32);
				var p = GetPlayer();
				if( !IsValid(p) ) {
					ImGui.Text("Invalid Player");
					return;
				}
				var stats = p.GetComponent<Stats>();
				if( !IsValid(stats) ) {
					ImGui.Text("Invalid Stats component");
					return;
				}
				if ( inputFilter.Length > 0 ) {
					var entries = stats.Entries;
					var filteredItems = entries.Where((kv) => kv.Key.ToString().Contains(inputFilter) || kv.Value.ToString().Contains(inputFilter)).ToArray();
					float maxWidth = 0;
					if ( filteredItems.Length > 0 ) {
						maxWidth = filteredItems.Select((kv) => ImGui.CalcTextSize(kv.Key.ToString()).X).Max();
						// ImGui.Text($"Max Width: {maxWidth}");

						ImGui.BeginListBox("##debugStatsList", new Vector2(maxWidth + 60, ImGui.GetFontSize() * filteredItems.Length));
						foreach ( var entry in filteredItems ) {
							ImGui.Text(entry.Key + " = " + entry.Value);
						}
						ImGui.EndListBox();
					}
				}
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
						stats.TryGetValue(GameStat.ChaosDamageImmunity, out int chaosImmune);
						if ( chaosImmune == 1 ) {
							chaosRes = uncappedChaosRes = 100;
						}
						DrawTextAt(1, spot, $"Chaos:     {chaosRes}% ({uncappedChaosRes}%)", Color.Violet, size);

						stats.TryGetValue(GameStat.PhysicalDamageReductionRating, out int armourRating);
						stats.TryGetValue(GameStat.DisplayEstimatedPhysicalDamageReducitonPct, out int armourPct);
						DrawTextAt(1, spot, $"Armour:    {armourRating} ({armourPct}%)", Color.Lavender, size);

						stats.TryGetValue(GameStat.EvasionRating, out int evasionRating);
						stats.TryGetValue(GameStat.ChanceToEvadePct, out int evadeChance);
						DrawTextAt(1, spot, $"Evasion:   {evasionRating} ({evadeChance}%)", Color.Cornsilk, size);

						stats.TryGetValue(GameStat.AttackBlockPct, out int attackBlock);
						stats.TryGetValue(GameStat.SpellBlockPct, out int spellBlock);
						stats.TryGetValue(GameStat.SpellSuppressionChancePct, out int spellSuppress);
						DrawTextAt(1, spot, $"Blk: {attackBlock}% {spellBlock}% Sup: {spellSuppress}%", Color.Cornsilk, size);

						ImGui.SetNextWindowPos(new Vector2(spot.X, spot.Y + ImGuiController.GetNextOffsetForTextAt(1)));
						ImGui.Begin("Defense Global Checkbox", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground );
						ImGui.Checkbox("Defenses", ref ShowPlayerDefenses);
						ImGui.End();
						// ImGui.PopStyleVar(1);
						// ImGuiController.GetNextOffsetForTextAt(1);
					}
				}

				if ( ShowEnemyComponents || ShowEnemyBuffs || ShowEnemyResistChaos || ShowEnemyResistCold || ShowEnemyResistFire || ShowEnemyResistLightning || ShowEnemyWitherStacks || ShowEnemyPoisonStacks || ShowHighestCorpseLife || ShowEnemyImpaleStacks ) {
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
						bool isHostile = IsHostile(ent);
						if( ShowEnemyComponents && isHostile && isAlive ) {
							var actor = ent.GetComponent<Actor>();
							if( actor != null ) {
								var action = actor.CurrentAction;
								var textPos = WorldToScreen(Position(ent));
								DrawTextAt(ent.Id, textPos, $"Animation: {actor.AnimationId} {actor.ActionFlags}", Color.White);
								if( action != null ) {
									DrawTextAt(ent.Id, textPos,
										$"Action: {action.Skill} at {action.Destination} target {(IsValid(action.Target) ? "valid" : "invalid")}",
										Color.White);
								}
							}
						} 
						if ( (ShowEnemyBuffs || ShowEnemyPoisonStacks || ShowEnemyImpaleStacks || ShowEnemyWitherStacks || ShowEnemyGraspingVines) && isHostile && isAlive ) {
							if( IsRareOrUnique(ent) ) {
								var buffs = ent.GetComponent<Buffs>();
								if( !IsValid(buffs) ) {
									continue;
								}
								int poisonStacks = 0;
								int graspingVines = 0;
								int witherStacks = 0;
								int energyStacks = 0; // from penance brand of dissipation
								int impaleStacks = 0;
								string buffstr = "";
								/*
								var screenPos = WorldToScreen(Position(ent));
								ImGui.SetNextWindowPos(screenPos);
								ImGui.Begin($"Ent { ent.Id}");
								var components = ent.GetComponents();
								ImGui.Text(string.Join(" ", components.Keys));
								ImGui_Object($"Buffs##{ent.Id}", $"Buffs", ent.GetComponent<Buffs>(), new HashSet<int>());
								ImGui.End();
								*/
								foreach(var buff in buffs.GetBuffs()) {
									if( IsValid(buff) ) {
										string name = buff.Name;
										if ( name == null ) continue;
										seenBuffs.Add(buff.Name);
										if( name.Equals("poison") ) {
											poisonStacks += 1;
										} else if( name.Equals("wither") ) {
											witherStacks += 1;
										} else if( name.Equals("grasping_vines_buff") ) {
											graspingVines = buff.Charges;
										} else if( name.Equals("magma_pustule") ) {
											energyStacks = buff.Charges;
										} else if( name.Equals("impaled_debuff") ) {
											impaleStacks += Math.Max((int)buff.Charges, 1);
										}
									}
								}
								if( ShowEnemyPoisonStacks && poisonStacks > 0 ) {
									buffstr += $"Po {poisonStacks} ";
								}
								if( ShowEnemyImpaleStacks && impaleStacks > 0 ) {
									buffstr += $"Im {impaleStacks} ";
								}
								if( ShowEnemyWitherStacks && witherStacks > 0 ) {
									buffstr += $"Wi {witherStacks} ";
								}
								if( ShowEnemyGraspingVines ) {
									buffstr += $"Vn {graspingVines} ";
								}
								if( ShowEnemyEnergyStacks ) {
									buffstr += $"En {energyStacks}";
								}
								if ( buffstr.Length > 0 ) {
									DrawTextAt(ent.Id, WorldToScreen(Position(ent)), buffstr, Color.White);
									// DrawBottomLeftText($"{ent.Id}: {Position(ent)} {WorldToScreen(Position(ent))} {buffstr}", Color.White);
								}
							}
						}
						if ( isAlive && isHostile ) {
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
								maxCorpseLocation = loc;
							}
						}
					}

					if ( ShowHighestCorpseLife && maxCorpseLocation != Vector3.Zero ) {
						DrawTextAt(maxCorpseLocation, $"hp:{maxCorpseLife}", Color.White);
					}
				}
				if( ShowEnemyBuffs ) foreach(string buff in seenBuffs) {
					DrawBottomLeftText("Seen buff: " + buff, Color.White);
				}
			}

			return this;
		}
	}
}
