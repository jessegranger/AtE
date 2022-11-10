using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE.Plugins {

	/// <summary>
	/// This plugin helps to maintain some buffs on you, by casting them for you.
	/// </summary>
	public class BuffsPlugin : PluginBase {

		public override string Name => "Use Skills";

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
			public SkillData(string d, string s, string b) {
				DisplayName = d;
				SkillBarName = s;
				BuffName = b;
			}
			public override string ToString() => $"{DisplayName}={(Enabled ? "" : Key.ToString())}";
			public abstract bool Predicate(PlayerEntity p);
			public virtual void Configure() {
				ImGui.Checkbox($"##{DisplayName}", ref Enabled);
				ImGui.SameLine();
				ImGui_HotKeyButton(DisplayName, ref Key);
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
				// ImGui.Begin("SkillIsReady Debug");
				try {
					if ( !IsValid(p) ) {
						// ImGui.Text("Player Invalid");
						return false;
					}

					var actor = p.GetComponent<Actor>();
					if ( !IsValid(actor) ) {
						// ImGui.Text("Actor invalid");
						return false;
					}

					foreach ( ActorSkill s in actor.Skills.Where(IsValid) ) {
						// ImGui.Text($"{s.InternalName} == {SkillBarName} ({s.Id}) IsOnCooldown?: {actor.IsOnCooldown(s.Id)}");
						if ( s.InternalName?.Equals(SkillBarName) ?? false ) {
							// ImGui.Text($"MATCH");
							return !actor.IsOnCooldown(s.Id);
						}
					}
					return false;
				} finally {
					// ImGui.End();
				}
			}
		}

		public class BloodRageData : SkillData {
			public BloodRageData() : base("Blood Rage", "blood_rage", "blood_rage") { }
			public override bool Predicate(PlayerEntity p) => IsFullLife(p);
		}
		public class CorruptingFeverData : SkillData {
			public CorruptingFeverData() : base("Corrupting Fever", "CorruptingFever", "blood_surge") { }
			public override bool Predicate(PlayerEntity p) => IsFullLife(p);
		}
		/// <summary>
		/// Guard skills mostly follow the same pattern: Use if life drops below a threshold
		/// </summary>
		public class GuardSkillData : SkillData {
			public float UseAtLifePercent = .90f;
			public GuardSkillData(string displayName, string skillName, string buffName) : base(displayName, skillName, buffName) { }
			public override bool Predicate(PlayerEntity p) => IsMissingEHP(p, 1.0f - UseAtLifePercent);
			public override void Configure() {
				base.Configure();
				ImGui.SameLine();
				ImGui.SliderFloat("At Life %:", ref UseAtLifePercent, .2f, .99f);
			}
		}
		/// <summary>
		/// Steelskin also can clear bleeding
		/// </summary>
		public class SteelskinData : GuardSkillData {
			public SteelskinData() : base("Steelskin", "QuickGuard", "quick_guard") { }
			public override bool Predicate(PlayerEntity p) => base.Predicate(p) || HasBuff(p, "bleeding");
		}
		public class ImmortalCallData : GuardSkillData {
			public ImmortalCallData() : base("Immortal Call", "ImmortalCall", "mortal_call") { }
		}
		public class MoltenShellData : GuardSkillData {
			public MoltenShellData() : base("Molten Shell", "MoltenShell", "molten_shell_shield") { }
		}
		public class EnduringCryData : GuardSkillData {
			public EnduringCryData() : base("Enduring Cry", "EnduringCry", "enduring_cry_endurance_charge_benefits") { }
		}
		public class BerserkData : SkillData {
			public int MinRage = 25;
			public BerserkData() : base("Berserk", "Berserk", "berserk") { }
			public override bool Predicate(PlayerEntity p) => HasEnoughRage(p, MinRage);
			public override void Configure() {
				base.Configure();
				ImGui.SameLine();
				ImGui.SliderInt("Minimum Rage", ref MinRage, 10, 70);
			}
		}
		public class WitheringStepData : SkillData {
			public WitheringStepData() : base("Withering Step", "Slither", "slither") { }
			public override bool Predicate(PlayerEntity p) => false; // TODO: should check NearbyEnemies for rares with no wither stacks
		}
		public class PlagueBearerData : SkillData {
			public PlagueBearerData() : base("Plague Bearer", "CorrosiveShroud", "corrosive_shroud_accumulating_damage") { }
			public override bool Predicate(PlayerEntity p) {
				// TODO: this may not be quite right yet, PB uses three buffs and I haven't tested this logic yet
				// once the simpler skills are working, I will look more into it
				var buffs = p.GetComponent<Buffs>();
				if ( !IsValid(buffs) ) return false;
				return HasBuff(buffs, "corrosive_shroud_at_max_damage") || !HasBuff(buffs, "corrossive_shroud_buff");
			}
		}

		private Dictionary<string, SkillData> KnownSkills = new Dictionary<string, SkillData>() {
			{ "Blood Rage", new BloodRageData() },
			/// many of those are not quite right yet, like they need their SkillBarName checked/corrected, eg
			{ "Steelskin", new SteelskinData() },
			{ "Immortal Call", new ImmortalCallData() },
			{ "Molten Shell", new MoltenShellData() },
			// { "Corrupting Fever", new CorruptingFeverData() },
			// { "Enduring Cry", new EnduringCryData() },
			// { "Berserk", new BerserkData() },
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
			foreach(var buff in KnownSkills.Values) {
				buff.Configure();
			}
		}

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached ) {
				if( PoEMemory.GameRoot.InGameState.WorldData.IsTown ) {
					return this;
				}
				var ui = GetUI();
				if( ! IsValid(ui) ) { return this; }
				if( ui.PurchaseWindow.IsVisibleLocal || ui.SellWindow.IsVisibleLocal || ui.TradeWindow.IsVisibleLocal ) {
					return this;
				}
				var player = GetPlayer();
				var buffs = player.GetComponent<Buffs>();
				foreach(var buff in KnownSkills.Values) {
					if ( !buff.Enabled || HasBuff(buffs, buff.BuffName) ) {
						continue;
					}

					bool pred = buff.Predicate(player);
					// DrawTextAt(player, $"Checking skill: {buff.DisplayName} => {pred}", Color.Orange);
					if ( pred && buff.TryUseSkill(player) ) {
						return this; // cast at most one skill per tick
					}
				}
			}
			return this;
		}

		public override string[] Save() {
			string[] result = new string[KnownSkills.Count];
			int i = 0;
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
