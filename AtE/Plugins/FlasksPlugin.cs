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

		private static Dictionary<string, string> conditionToFlaskModMap = new Dictionary<string, string>() {
			{ "bleeding", "FlaskRemovesBleeding" },
			{ "frozen", "Flask" }
		};

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
			ImGui.Checkbox("Use Utility flasks when hit a Rare or Unique", ref UseUtilityWhenFull);
			ImGui.SameLine();
			ImGui_HelpMarker("If not already in effect.");


			ImGui.Checkbox("Show Flask Status", ref ShowFlaskStatus);
			ImGui.SameLine();
			if( ImGui.Button("B##browse-flasks") ) {
				Run_ObjectBrowser("Flasks", GetUI()?.Flasks);
			}

		}
		private long mostRecentFlask = 0;

		public bool IsUsable(FlaskEntity flaskEnt) => IsValid(flaskEnt)
			&& flaskEnt.Charges_Cur >= flaskEnt.Charges_Per
			&& (Time.ElapsedMilliseconds - mostRecentFlask) > 50 // a global throttle on all flasks, so it will only use one at a time
			&& (Time.ElapsedMilliseconds - mostRecentUse[flaskEnt.FlaskIndex]) > 100 // a throttle on each flask
			;

		public IState UseFlask(FlaskEntity flaskEnt) {
			if ( !IsUsable(flaskEnt) ) return this;
			var now = Time.ElapsedMilliseconds;
			mostRecentFlask = now;
			mostRecentUse[flaskEnt.FlaskIndex] = now;
			return new PressKey(flaskEnt.Key, 40, new Delay(100, this));
		}

		public bool TryGetUsableFlask(Func<FlaskEntity, bool> predicate, out FlaskEntity flask) {
			flask = GetFlasks().Where(IsUsable).Where(predicate).FirstOrDefault();
			return flask != null;
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled ) {
				return this; // keep it in the PluginBase.Machine, but dont do anything
			}

			if( ShowFlaskStatus ) {
				foreach(var elem in GetUI()?.Flasks?.Flasks ?? Empty<FlaskElement>()) {
					if ( !IsValid(elem) ) continue;
					var ent = elem.Item;
					var rect = elem.GetClientRect();
					if ( rect.IsEmpty ) continue;
					var color = ent.Charges_Cur < ent.Charges_Per ? Color.Red :
						ent.IsBuffActive ? Color.Gray :
						Color.Green;
					DrawFrame(rect, color, 2);
				}
			}
			FlaskEntity flaskToUse = null;

			var player = GetPlayer();
			if ( !IsValid(player) ) return this;

			var life = player.GetComponent<Life>();
			if ( IsValid(life) ) {

				// Feature: Use Life flask
				if ( UseLifeFlask ) {
					int maxHp = life.MaxHP - life.TotalReservedHP;
					// 0 - 100, to match the scale of the SliderFloat above
					float pctHp = 100 * life.CurHP / maxHp;
					if ( pctHp < UseLifeFlaskAtPctLife ) {
						foreach ( FlaskEntity flask in GetFlasks().Where(IsUsable) ) {
							if ( flask.LifeHealAmount <= 0 ) {
								continue;
							}
							if ( flask.IsBuffActive ) {
								continue;
							}
							if ( flask.IsInstant || (flask.IsInstantOnLowLife && pctHp < 50) ) {
								flaskToUse = flask;
								break;
							} else {
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
				var buffs = player.GetComponent<Buffs>();
				if ( IsValid(buffs) ) {
					if ( HasBuff(buffs, "frozen") && TryGetUsableFlask(f => f.Cures_Frozen, out flaskToUse) ) {
						return UseFlask(flaskToUse);
					}
					if ( HasBuff(buffs, "bleeding") && TryGetUsableFlask(f => f.Cures_Bleeding, out flaskToUse) ) {
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

			if( UseUtilityWhenFull ) {
				if( TryGetUsableFlask(f => f.Charges_Cur == f.Charges_Max, out flaskToUse) ) {
					return UseFlask(flaskToUse);
				}
			}

			if( UseUtilityWhenHitRareOrUnique ) {
				var nearbyEnemies = GetEntities().Where(e => IsValid(e) && e.IsHostile);
			}
			return base.OnTick(dt);
		}



	}
}
