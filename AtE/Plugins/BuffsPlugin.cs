﻿using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE.Plugins {

	/// <summary>
	/// This plugin helps to maintain some buffs on you, by casting them for you.
	/// </summary>
	public class BuffsPlugin : PluginBase {

		public override string Name => "Use Skills";

		public override string HelpText => "Only if found in the skill bar, and it's up to you to match the Key here to the game binding";

		// A configuration holder for each buff we want to manage
		public abstract class SkillData {
			/// <summary>
			/// The friendly display name used in our UI and Settings.ini
			/// </summary>
			public string DisplayName;
			/// <summary>
			/// The internal name of the skill (has to match the name used in the skillbar UI data)
			/// </summary>
			public string SkillBarName;
			/// <summary>
			/// The name of the Buff that will result from using the skill
			/// </summary>
			public string BuffName;
			public bool Enabled = false;
			public HotKey Key = new HotKey(Keys.None);
			public SkillData(string displayName, string skillName, string buffName) {
				DisplayName = displayName;
				SkillBarName = skillName;
				BuffName = buffName;
			}
			public override string ToString() => $"{DisplayName}={(Enabled ? "" : Key.ToString())}";
			public virtual bool Predicate(PlayerEntity p) {
				return Enabled && !HasBuff(p, BuffName);
			}
			public virtual void Configure() {
				ImGui.TableNextColumn();
				ImGui.Checkbox($"##{DisplayName}", ref Enabled);
				ImGui.TableNextColumn();
				ImGui_HotKeyButton(DisplayName, ref Key);
				ImGui.TableNextColumn();
			}

			public static long lastGlobalSkillUse = 0;
			public long lastSkillUse = 0;
			public bool TryUseSkill(PlayerEntity p) {
				long sinceLastGlobal = Time.ElapsedMilliseconds - lastGlobalSkillUse;
				long sinceLast = Time.ElapsedMilliseconds - lastSkillUse;
				if ( sinceLastGlobal > 150 
					&& sinceLast > 1000
					&& SkillIsReady(p)
					&& Key.PressImmediate() ) {
					lastGlobalSkillUse = lastSkillUse = Time.ElapsedMilliseconds;
					return true;
				}
				return false;
			}
			public bool SkillIsReady(PlayerEntity p) {
				try {
					if ( !IsValid(p) ) {
						return false;
					}

					var actor = p.GetComponent<Actor>();
					if ( !IsValid(actor) ) {
						return false;
					}

					foreach ( ActorSkill s in actor.Skills.Where(IsValid) ) {
						if ( s.InternalName?.Equals(SkillBarName) ?? false ) {
							return !actor.IsOnCooldown(s.Id);
						}
					}
					return false;
				} finally {
				}
			}
		}

		public class BloodRageData : SkillData {
			public BloodRageData() : base("Blood Rage", "blood_rage", "blood_rage") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && IsFullLife(p);
			public override void Configure() {
				base.Configure();
				ImGui_HelpMarker("Will re-apply Blood Rage once you are full life.");
			}
		}

		public class CorruptingFeverData : SkillData {
			public CorruptingFeverData() : base("Corrupting Fever", "CorruptingFever", "blood_surge") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && IsFullLife(p);
		}

		/// <summary>
		/// Steelskin also can clear bleeding
		/// </summary>
		public class SteelskinData : SkillData {
			public int UseAtLifePercent = 90;
			public SteelskinData() : base("Steelskin", "steelskin", "quick_guard") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && HasBuff(p, "bleeding") || IsMissingEHP(p, 1.0f - (UseAtLifePercent / 100f), HasBuff(p, "petrified_blood"));
			public override void Configure() {
				base.Configure();
				ImGui.Text(" at ");
				ImGui.SameLine();
				ImGui.SliderInt("% eHP##Steelskin", ref UseAtLifePercent, 2, 99);
			}
		}

		public class ImmortalCallData : SkillData {
			public int UseAtLifePercent = 90;
			public ImmortalCallData() : base("Immortal Call", "mortal_call", "mortal_call") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && IsMissingEHP(p, 1.0f - (UseAtLifePercent / 100f), HasBuff(p, "petrified_blood"));
			public override void Configure() {
				base.Configure();
				ImGui.Text(" at ");
				ImGui.SameLine();
				ImGui.SliderInt("% eHP##Immortal Call", ref UseAtLifePercent, 2, 99);
			}
		}

		public class MoltenShellData : SkillData {
			public int UseAtLifePercent = 90;
			public MoltenShellData() : base("Molten Shell", "molten_shell_barrier", "molten_shell_shield") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && IsMissingEHP(p, 1.0f - (UseAtLifePercent / 100f), HasBuff(p, "petrified_blood")) && !HasBuff(p, "vaal_molten_shell");
			public override void Configure() {
				base.Configure();
				ImGui.Text(" at ");
				ImGui.SameLine();
				ImGui.SliderInt("% eHP##Molten Shell", ref UseAtLifePercent, 2, 99);
			}
		}

		public class EnduringCryData : SkillData {
			public int UseAtLifePercent = 90;
			public EnduringCryData() : base("Enduring Cry", "enduring_cry", "enduring_cry_endurance_charge_benefits") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && IsMissingEHP(p, 1.0f - (UseAtLifePercent / 100f), HasBuff(p, "petrified_blood"));
			public override void Configure() {
				base.Configure();
				ImGui.SameLine();
				ImGui.Text("at");
				ImGui.SameLine();
				ImGui.SliderInt("% eHP##Enduring Cry", ref UseAtLifePercent, 2, 99);
			}
		}

		public class BerserkData : SkillData {
			public int MinRage = 25;
			public BerserkData() : base("Berserk", "berserk", "berserk") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && HasEnoughRage(p, MinRage);
			public override void Configure() {
				base.Configure();
				ImGui.SliderInt("Minimum Rage", ref MinRage, 10, 70);
			}
		}

		public class WitheringStepData : SkillData {
			public WitheringStepData() : base("Withering Step", "Slither", "slither") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) && false; // TODO: should check NearbyEnemies for rares with no wither stacks
		}

		public class PlagueBearerData : SkillData {
			public PlagueBearerData() : base("Plague Bearer", "CorrosiveShroud", "corrosive_shroud_accumulating_damage") { }
			public override bool Predicate(PlayerEntity p) {
				// TODO: this may not be quite right yet, PB uses three buffs and I haven't tested this logic yet
				// once the simpler skills are working, I will look more into it
				var buffs = p.GetComponent<Buffs>();
				if ( !IsValid(buffs) ) return false;
				return base.Predicate(p) && HasBuff(buffs, "corrosive_shroud_at_max_damage") || !HasBuff(buffs, "corrossive_shroud_buff");
			}
		}

		/// <summary>
		/// Banner skills all follow the same pattern: fill up to 50 charges, then plant.
		/// </summary>
		public abstract class BannerSkill : SkillData {
			// base.BuffName is the main buff you always have when the banner has mana reserved
			public string StageBuffName; // each banner tracks the stages with a separate buff with charges
			public BannerSkill(string displayName, string skillName, string auraBuffName, string stageBuffName):base(displayName, skillName, auraBuffName) {
				StageBuffName = stageBuffName;
			}
			public override bool Predicate(PlayerEntity p) =>
				base.Predicate(p) ||
				(TryGetBuffValue(p, StageBuffName, out int stage) && stage >= 50
					&& NearbyEnemies(500, Offsets.MonsterRarity.Rare).Any(IsAlive)
				);
		}

		public class DefianceBanner : BannerSkill { public DefianceBanner() : base("Defiance Banner", "banner_armour_evasion", "armour_evasion_banner_buff_aura", "armour_evasion_banner_stage") { } }
		public class DreadBanner : BannerSkill { public DreadBanner() : base("Dread Banner", "banner_dread", "puresteel_banner_buff_aura", "puresteel_banner_stage") { } }
		public class WarBanner : BannerSkill { public WarBanner() : base("War Banner", "banner_war", "bloodstained_banner_buff_aura", "bloodstained_banner_stage") { } }

		public class TemporalRift : SkillData {
			public TemporalRift() : base("Temporal Rift", "temporal_rift", "chronomancer") { }

			public int UseForGain = 40;

			private long lastRiftSample = 0;
			// what was the Player life at each second in the recent past
			private int[] ehpSamples = new int[4];

			public override bool Predicate(PlayerEntity p) {
				if ( !Enabled ) {
					return false;
				}

				if ( !HasBuff(p, "chronomancer") ) {
					return true;
				}

				var life = p.GetComponent<Life>();
				int hp = (int)CurrentEHP(life);
				long now = Time.ElapsedMilliseconds;
				// how much absolute ehp would be gained if we used Temporal Rift right now:
				var gain = ehpSamples[0] - hp;
				var maxHp = MaxEHP(life, HasBuff(p, "petrified_blood"));
				var pctGain = 100 * (gain / (float)maxHp);
				if( now - lastRiftSample > 1000 ) {
					ehpSamples[0] = ehpSamples[1];
					ehpSamples[1] = ehpSamples[2];
					ehpSamples[2] = ehpSamples[3];
					ehpSamples[3] = hp;
					lastRiftSample = now;
				}
				return pctGain >= UseForGain;
			}

			public override void Configure() {
				base.Configure();
				ImGui_HelpMarker("if you would gain % eHP");
				ImGui.SameLine();
				ImGui.SliderInt("% gain##Temporal Rift", ref UseForGain, 5, 90);
			}
		}

		private Dictionary<string, SkillData> KnownSkills = new Dictionary<string, SkillData>() {
			{ "Blood Rage", new BloodRageData() },
			/// many of those are not quite right yet, like they need their SkillBarName checked/corrected, eg
			{ "Steelskin", new SteelskinData() },
			{ "Immortal Call", new ImmortalCallData() },
			{ "Molten Shell", new MoltenShellData() },
			{ "Defiance Banner", new DefianceBanner() },
			{ "War Banner", new WarBanner() },
			{ "Dread Banner", new DreadBanner() },
			{ "Temporal Rift", new TemporalRift() },
			{ "Berserk", new BerserkData() },
			// { "Corrupting Fever", new CorruptingFeverData() },
			// { "Enduring Cry", new EnduringCryData() },
			// { "Plague Bearer", new PlagueBearerData() },
			// { "Withering Step", new WitheringStepData() },
			// { "Vaal Grace", new SkillData("Vaal Grace", "vaal_grace", "vaal_aura_dodge", null, (p) => true) },
			// { "Vaal Haste", new SkillData("Vaal Haste", "vaal_haste", "vaal_aura_speed", null, (p) => true) },
			// { "Vaal Cold Snap", new SkillData("Vaal Cold Snap", "new_vaal_cold_snap", "vaal_cold_snap_degen", null, (p) => false) }, // HasNearbyRares( },
			// { "Vaal Discipline", new SkillData("Vaal Discipline", "vaal_discipline", "vaal_aura_energy_shield", null, (p) => IsMissingEHP(p, .5f) )},
		};

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.BeginTable("Table.KnownBuffs", 3, ImGuiTableFlags.SizingFixedFit);
			foreach(var buff in KnownSkills.Values) {
				ImGui.TableNextRow();
				buff.Configure();
			}
			ImGui.EndTable();
			var player = GetPlayer();
			var life = player.GetComponent<Life>();
			/*
			ImGui.Text($"MaxLife: {MaxLife(player)}");
			ImGui.Text($"MaxEHP: {MaxEHP(life, HasBuff(player, "petrified_blood"))}");
			ImGui.Text($"CurEHP: {CurrentEHP(life)}");
			ImGui.Text($"IsMissingEHP(.01f): {IsMissingEHP(player, .01f, HasBuff(player, "petrified_blood"))}");
			ImGui.Text($"IsMissingEHP(.1f): {IsMissingEHP(player, .1f, HasBuff(player, "petrified_blood"))}");
			*/
		}

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.TargetHasFocus ) {

				if( PoEMemory.GameRoot.InGameState.WorldData.IsTown ) {
					return this;
				}

				if( PoEMemory.GameRoot.InGameState.HasInputFocus ) {
					return this;
				}

				if( PoEMemory.GameRoot.AreaLoadingState.IsLoading ) {
					return this;
				}

				if( PoEMemory.GameRoot.AreaLoadingState.AreaName?.EndsWith(Offsets.HIDEOUT_SUFFIX) ?? true ) {
					return this;
				}

				var ui = GetUI();
				if ( !IsValid(ui) ) {
					return this;
				}

				if ( ui.PurchaseWindow.IsVisibleLocal || ui.SellWindow.IsVisibleLocal || ui.TradeWindow.IsVisibleLocal ) {
					return this;
				}

				var player = GetPlayer();
				if ( !IsValid(player) ) {
					return this;
				}

				var buffs = player.GetComponent<Buffs>();
				if( HasBuff(buffs, "grace_period") ) {
					return this;
				}

				/*
				DrawTextAt(player, $"HasBuff(bloodstained_banner_buff_aura) = {HasBuff(player, "bloodstained_banner_buff_aura")}", Color.Orange);
				DrawTextAt(player, $"TryGetBuffValue(bloodstained_banner_stage) = {(TryGetBuffValue(player, "bloodstained_banner_stage", out int stage) ? stage : -1)}", Color.Orange);
				DrawTextAt(player, $"NearbyEnemies(500, Offsets.MonsterRarity.Rare) = {NearbyEnemies(500, Offsets.MonsterRarity.Rare).Count()}", Color.Orange);
				*/


				foreach(var buff in KnownSkills.Values) {
					bool pred = buff.Predicate(player);
					// DrawTextAt(player, $"Checking skill: {buff.DisplayName} => {pred}", Color.Orange);
					if ( pred && buff.TryUseSkill(player) ) {
						return this; // continue with any other buffs next frame
					}
				}
			}
			return this;
		}

		/// <summary>
		/// Used to save this plugin's section of Settings.ini
		/// </summary>
		public override string[] Save() {
			string[] result = new string[KnownSkills.Count + 1];
			result[0] = $"Enabled={Enabled}";
			int i = 1;
			foreach(var buff in KnownSkills.Values) {
				result[i++] = $"{buff.DisplayName}={(buff.Enabled ? buff.Key.ToString() : "None")}";
			}
			return result;
		}

		public override bool Load(string key, string value) {
			if ( KnownSkills.TryGetValue(key, out SkillData buff) ) {
				buff.Key = HotKey.Parse(value);
				buff.Enabled = buff.Key.Key != Keys.None;
				return true;
			}
			return false;
		}
	}
}
