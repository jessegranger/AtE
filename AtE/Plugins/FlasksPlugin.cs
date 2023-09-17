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
			ImGui.SliderFloat("##Life", ref UseLifeFlaskAtPctLife, 25f, 75f);

			ImGui.Checkbox("Use Mana flask at %:", ref UseManaFlask);
			ImGui.SameLine();
			ImGui.SliderFloat("##Mana", ref UseManaFlaskAtPctMana, 2f, 99f);

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

		public IState UseFlask(FlaskEntity flaskEnt, string reason) {
			if ( IsUsable(flaskEnt) ) {
				var now = Time.ElapsedMilliseconds;
				mostRecentFlask = now;
				mostRecentUse[flaskEnt.FlaskIndex] = now;
				if ( PoEMemory.TargetHasFocus ) {
					Notify($"Using flask {flaskEnt.Key} because {reason}");
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

			if ( !PoEMemory.IsAttached ) {
				return this;
			}

			if ( !(PoEMemory.TargetHasFocus || Overlay.HasFocus) ) {
				return this;
			}

			if ( PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true ) {
				DrawBottomLeftText("Flasks are paused on loading screens.", Color.LightGray);
				return this;
			}

			if ( PoEMemory.GameRoot?.InGameState?.HasInputFocus ?? true ) {
				DrawBottomLeftText("Flasks are paused during text input.", Color.LightGray);
				return this;
			}

			if ( PoEMemory.GameRoot?.InGameState?.WorldData?.IsTown ?? true ) {
				DrawBottomLeftText("Flasks are paused in towns.", Color.LightGray);
				return this;
			}

			string areaName = PoEMemory.GameRoot?.AreaLoadingState.AreaName ?? null;
			if( Offsets.IsHideout(areaName) ) {
				DrawBottomLeftText("Flasks are paused in towns and hideouts.", Color.LightGray);
				return this;
			}

			var ui = GetUI();
			if ( !IsValid(ui) ) { return this; }
			bool sellIsOpen = (ui.SellWindow?.IsVisibleLocal ?? true);
			bool buyIsOpen = (ui.PurchaseWindow?.IsVisibleLocal ?? true);
			bool tradeIsOpen = (ui.TradeWindow?.IsVisibleLocal ?? true);
			if ( buyIsOpen || sellIsOpen || tradeIsOpen ) {
				DrawBottomLeftText($"Flasks are paused while trade windows are open.", Color.LightGray);
				return this;
			}

			var player = GetPlayer();
			if ( !IsValid(player) ) {
				DrawBottomLeftText("Cannot show flask status while player is invalid.", Color.LightGray);
				return this;
			}

			var buffs = player.GetComponent<Buffs>();
			if ( HasBuff(buffs, "grace_period") ) {
				DrawBottomLeftText("Flasks are paused during grace period.", Color.LightGray);
				return this;
			}

			if ( ShowFlaskStatus ) {
				foreach ( var elem in GetUI()?.Flasks?.Flasks ?? Empty<FlaskElement>() ) {
					if ( !IsValid(elem) ) {
						continue;
					}
					var ent = elem.Entity;
					if( !IsValid(ent) ) {
						continue;
					}
					var rect = elem.GetClientRect();
					if ( !rect.IsEmpty ) {
						var color = ent.Charges_Cur < ent.Charges_Per ? Color.Red :
							ent.IsBuffActive ? Color.Yellow :
							Color.Green;
						DrawFrame(rect, color, 2);
					}
				}
			}
			FlaskEntity flaskToUse = null;

			var life = player.GetComponent<Life>();
			if ( IsValid(life) ) {
				if( life.CurHP < 1 ) {
					return this;
				}
				// Feature: Use Life flask
				if ( UseLifeFlask ) {
					uint maxHp = MaxLife(life, HasBuff(player, "petrified_blood")); // life.MaxHP - life.TotalReservedHP;
					if ( maxHp == 0 ) return this;
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
							return UseFlask(flaskToUse, $"Life is low ({pctHp} < { UseLifeFlaskAtPctLife })");
						}
					}
				}

				// Feature: Use Mana flask
				if ( UseManaFlask ) {
					int maxMana = life.MaxMana - life.TotalReservedMana;
					if ( maxMana > 0 ) {
						float pctMana = 100 * life.CurMana / maxMana;
						if ( pctMana < UseManaFlaskAtPctMana ) {
							foreach ( FlaskEntity flask in GetFlasks().Where(IsUsable) ) {
								if ( flask.ManaHealAmount <= 0 ) {
									continue;
								}
								if ( flask.IsBuffActive ) {
									continue;
								}
								return UseFlask(flask, $"Mana is low ({pctMana} < {UseManaFlaskAtPctMana})");
							}
						}
					}
				}
			}

			// Feature: Cure Conditions
			if ( CureConditions ) {
				if ( IsValid(buffs) ) {
					if ( HasBuff(buffs, "frozen") && TryGetUsableFlask(f => f.Cures_Frozen, out flaskToUse) ) {
						return UseFlask(flaskToUse, "player is frozen");
					}
					if ( (HasBuff(buffs, "bleeding") || HasBuff(buffs, "corrupted_blood")) && TryGetUsableFlask(f => f.Cures_Bleeding, out flaskToUse) ) {
						return UseFlask(flaskToUse, "player is bleeding");
					}
					if ( HasBuff(buffs, "poisoned") && TryGetUsableFlask(f => f.Cures_Poisoned, out flaskToUse) ) {
						return UseFlask(flaskToUse, "player is poisoned");
					}
					if ( (HasBuff(buffs, "ignited") || HasBuff(buffs, "burning")) && TryGetUsableFlask(f => f.Cures_Ignited, out flaskToUse) ) {
						return UseFlask(flaskToUse, "player is ignited");
					}
					if ( HasBuff(buffs, "cursed") && TryGetUsableFlask(f => f.Cures_Curse, out flaskToUse) ) {
						return UseFlask(flaskToUse, "player is cursed");
					}
				}
			}

			if ( UseUtilityWhenFull ) {
				if ( TryGetUsableFlask(f => f.LifeHealAmount == 0 && f.ManaHealAmount == 0
					&& f.Charges_Cur == f.Charges_Max
					&& !f.Enchanted_UseWhenHitRare
					&& !f.Enchanted_UseWhenFull
					&& !f.IsBuffActive, out flaskToUse) ) {
					return UseFlask(flaskToUse, "it's full");
				}
			}

			if ( UseUtilityWhenHitRareOrUnique ) {
				bool hasHitNearbyRare = NearbyEnemies(100, Offsets.MonsterRarity.Rare)
					// If there are any hurt
					.Any(e => {
						var e_life = e.GetComponent<Life>();
						return IsValid(e_life) && e_life.CurHP > 0 && e_life.CurHP < e_life.MaxHP;
					});
				if ( hasHitNearbyRare ) {
					if ( TryGetUsableFlask(f => f.LifeHealAmount == 0
						&& f.ManaHealAmount == 0
						&& !f.Enchanted_UseWhenHitRare
						&& !f.IsBuffActive
						, out flaskToUse) ) {
						return UseFlask(flaskToUse, "of a nearby rare");
					}
				}
			}
			return base.OnTick(dt);
		}



	}
}
