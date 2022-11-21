using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AtE.Globals;

namespace AtE {
	public class FlasksPlugin : PluginBase {

		// Feature: Life Flasks
		public bool UseLifeFlask = true;
		public float UseLifeFlaskAtPctLife = 50f;

		// Feature: Mana Flasks
		public bool UseManaFlask = true;
		public float UseManaFlaskAtPctMana = 25f;

		// Feature: Cure conditions, eg bleeding or frozen
		public bool CureConditions = true;

		// Feature: Use When Full
		public bool UseUtilityWhenFull = true;
		public bool UseUtilityWhenHitRareOrUnique = true;

		// Feature: Highlight flasks with a colored status border
		public bool ShowFlaskStatus = false;


		public override string Name => "Flasks";


		private long[] mostRecentUse = new long[] { 0, 0, 0, 0, 0 };

		public override void Render() {
			base.Render();

			ImGui.Checkbox("Use Life Flask at %:", ref UseLifeFlask);
			ImGui.SameLine();
			ImGui.SliderFloat("", ref UseLifeFlaskAtPctLife, 25f, 75f);

			ImGui.Checkbox("Use Mana flask at %:", ref UseManaFlask);
			ImGui.SameLine();
			ImGui.SliderFloat("", ref UseManaFlaskAtPctMana, 2f, 99f);

			ImGui.Checkbox("Use flasks to cure conditions", ref CureConditions);
			ImGui.SameLine();
			ImGui_HelpMarker("E.g bleeding, frozen");

			ImGui.Checkbox("Use Utility flasks when full", ref UseUtilityWhenFull);
			ImGui.SameLine();
			ImGui_HelpMarker("If not already in effect.");
			ImGui.Checkbox("Use Utility flasks when hit a Rare or Unique", ref UseUtilityWhenHitRareOrUnique);
			ImGui.SameLine();
			ImGui_HelpMarker("If not already in effect.");


			ImGui.Checkbox("Show Flask Status", ref ShowFlaskStatus);
			ImGui.SameLine();
			if ( ImGui.Button("B##browse-flasks") ) {
				Run_ObjectBrowser("Flasks", GetUI()?.Flasks);
			}

		}
		private long mostRecentFlask = 0;

		public bool IsUsable(FlaskEntity flaskEnt) => IsValid(flaskEnt)
			&& flaskEnt.Charges_Cur >= flaskEnt.Charges_Per
			&& (Time.ElapsedMilliseconds - mostRecentFlask) > 170 // a global throttle on all flasks, so it will only use one at a time
			&& (Time.ElapsedMilliseconds - mostRecentUse[flaskEnt.FlaskIndex]) > 250 // a throttle on each flask
			;

		public IState UseFlask(FlaskEntity flaskEnt) {
			if ( IsUsable(flaskEnt) ) {
				var now = Time.ElapsedMilliseconds;
				mostRecentFlask = now;
				mostRecentUse[flaskEnt.FlaskIndex] = now;
				if ( PoEMemory.TargetHasFocus ) {
					Notify($"Using flask {flaskEnt.Key}");
					Win32.SendInput(Win32.INPUT_KeyDown(flaskEnt.Key),
						Win32.INPUT_KeyUp(flaskEnt.Key));
				}
			}
			return this;
		}

		public bool TryGetUsableFlask(Func<FlaskEntity, bool> predicate, out FlaskEntity flask) {
			flask = GetFlasks().Where(IsUsable).Where(predicate).FirstOrDefault();
			return flask != null;
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled ) {
				return this; // keep it in the PluginBase.Machine, but dont do anything
			}

			if ( !PoEMemory.IsAttached || !PoEMemory.TargetHasFocus ) {
				return this;
			}

			if ( PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true ) {
				return this;
			}

			if ( PoEMemory.GameRoot?.InGameState?.HasInputFocus ?? true ) {
				return this;
			}

			if ( PoEMemory.GameRoot?.InGameState?.WorldData?.IsTown ?? true ) {
				return this;
			}

			var ui = GetUI();
			if ( !IsValid(ui) ) { return this; }
			if ( ui.PurchaseWindow.IsVisibleLocal || ui.SellWindow.IsVisibleLocal || ui.TradeWindow.IsVisibleLocal ) {
				return this;
			}

			var player = GetPlayer();
			if ( !IsValid(player) ) {
				return this;
			}

			var buffs = player.GetComponent<Buffs>();
			if ( HasBuff(buffs, "grace_period") ) {
				return this;
			}

			if ( ShowFlaskStatus ) {
				foreach ( var elem in GetUI()?.Flasks?.Flasks ?? Empty<FlaskElement>() ) {
					if ( IsValid(elem) ) {
						var ent = elem.Item;
						var rect = elem.GetClientRect();
						if ( !rect.IsEmpty ) {
							var color = ent.Charges_Cur < ent.Charges_Per ? Color.Red :
								ent.IsBuffActive ? Color.Yellow :
								Color.Green;
							DrawFrame(rect, color, 2);
						}
					}
				}
			}
			FlaskEntity flaskToUse = null;

			var life = player.GetComponent<Life>();
			if ( IsValid(life) ) {
				// Feature: Use Life flask
				if ( UseLifeFlask ) {
					int maxHp = life.MaxHP - life.TotalReservedHP;
					// 0 - 100, to match the scale of the SliderFloat above
					float pctHp = 100 * life.CurHP / maxHp;
					if ( pctHp < UseLifeFlaskAtPctLife ) {
						foreach ( FlaskEntity flask in GetFlasks().Where(IsUsable).Where(f => f.LifeHealAmount > 0) ) {
							if ( flask.IsInstant || (flask.IsInstantOnLowLife && pctHp < 50) ) {
								flaskToUse = flask;
								break;
							} else if ( !flask.IsBuffActive ) {
								flaskToUse = flaskToUse ?? flask;
							}
						}

						if ( flaskToUse != null ) {
							return UseFlask(flaskToUse);
						}
					}
				}

				// Feature: Use Mana flask
				if ( UseManaFlask ) {
					int maxMana = life.MaxMana - life.TotalReservedMana;
					float pctMana = 100 * life.CurMana / maxMana;
					if ( pctMana < UseManaFlaskAtPctMana ) {
						foreach ( FlaskEntity flask in GetFlasks().Where(IsUsable) ) {
							if ( flask.ManaHealAmount <= 0 ) {
								continue;
							}
							if ( flask.IsBuffActive ) {
								continue;
							}
							return UseFlask(flask);
						}
					}
				}
			}

			// Feature: Cure Conditions
			if ( CureConditions ) {
				if ( IsValid(buffs) ) {
					if ( HasBuff(buffs, "frozen") && TryGetUsableFlask(f => f.Cures_Frozen, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
					if ( (HasBuff(buffs, "bleeding") || HasBuff(buffs, "corrupted_blood")) && TryGetUsableFlask(f => f.Cures_Bleeding, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
					if ( HasBuff(buffs, "poisoned") && TryGetUsableFlask(f => f.Cures_Poisoned, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
					if ( (HasBuff(buffs, "ignited") || HasBuff(buffs, "burning")) && TryGetUsableFlask(f => f.Cures_Ignited, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
					if ( HasBuff(buffs, "cursed") && TryGetUsableFlask(f => f.Cures_Curse, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
				}
			}

			if ( UseUtilityWhenFull ) {
				if ( TryGetUsableFlask(f => f.LifeHealAmount == 0 && f.ManaHealAmount == 0
					&& f.Charges_Cur == f.Charges_Max
					&& !f.IsBuffActive, out flaskToUse) ) {
					return UseFlask(flaskToUse);
				}
			}

			if ( UseUtilityWhenHitRareOrUnique ) {
				bool hasHitNearbyRare = GetEnemies().Where(e =>
					// Rare or greater:
					e.GetComponent<ObjectMagicProperties>()?.Rarity >= Offsets.MonsterRarity.Rare)
					// If there are any hurt
					.Any(e => {
						var e_life = e.GetComponent<Life>();
						return IsValid(e_life) && e_life.CurHP > 0 && e_life.CurHP < e_life.MaxHP;
					});
				if ( hasHitNearbyRare ) {
					if ( TryGetUsableFlask(f => f.LifeHealAmount == 0
						&& f.ManaHealAmount == 0
						&& !f.IsBuffActive
						, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
				}
			}
			return base.OnTick(dt);
		}



	}
}
