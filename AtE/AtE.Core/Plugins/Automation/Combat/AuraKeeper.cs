using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Keep the reservation skills of the build's current skill set turned on (eg, Clarity, Vitality): a skill with a
	/// mana or life reservation, whose gem is socketed, without its buff on the player. They need no slot of their own:
	/// Ctrl + left click any skill bar slot (the hidden skill bar opens), then Ctrl + right click the skill there
	/// (it toggles on). Not in town, where the player shows no aura buffs.
	/// </summary>
	public class AuraKeeper {
		/// <summary>Time between clicks.</summary>
		public long Pace = 500;
		/// <summary>Give up on a skill that is not on this long after its click (and leave it for CooldownMs).</summary>
		public long ConfirmMs = 2500;
		public long CooldownMs = 60000;
		/// <summary>After the click that opens the skills: wait this long for them to open before clicking again.</summary>
		public long OpenWaitMs = 1500;
		/// <summary>Once they are open: wait this long (while they appear) before clicking in them.</summary>
		public long SettleMs = 400;

		/// <summary>What it is doing, for the status line.</summary>
		public string Status { get; private set; } = "";

		private enum Phase { None, Opening, Toggling, Confirming }
		private Phase phase;
		private string skill;
		private long started, lastClick, openedAt;
		private readonly Dictionary<string, long> cooldown = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// The buff a reservation skill puts on the player, by the skill's internal name (eg, "vitality"); one not
		/// listed is looked for as "player_aura_" and the internal name.
		/// </summary>
		public static readonly Dictionary<string, string> BuffNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			{ "vitality", "player_aura_life_regen" },
			{ "clarity", "player_aura_mana_regen" },
		};

		public static string BuffName(ActorSkill skill) =>
			skill?.InternalName is string name ? (BuffNames.TryGetValue(name, out var buff) ? buff : $"player_aura_{name}") : null;

		/// <summary>Does the skill reserve mana or life (or is it an aura).</summary>
		public static bool IsReservation(ActorSkill skill) => skill != null
			&& (Positive(skill, Offsets.GameStat.ManaReservation) || Positive(skill, Offsets.GameStat.LifeReservation)
			|| Positive(skill, Offsets.GameStat.ManaReservationPermyriad) || Positive(skill, Offsets.GameStat.LifeReservationPermyriad)
			|| Positive(skill, Offsets.GameStat.SkillIsAuraSkill));
		private static bool Positive(ActorSkill skill, Offsets.GameStat stat) => skill.TryGetStat(stat, out int v) && v > 0;

		/// <summary>The reservation skills of the current skill set (at the character's level) the player has (the gem is socketed).</summary>
		public static IEnumerable<ActorSkill> Planned() {
			var plan = BuildPlan.Gems();
			var skills = GetPlayer()?.GetComponent<Actor>()?.Skills?.ToList();
			if ( plan == null || skills == null ) yield break;
			foreach ( var gem in plan.GroupsFor(GemPlan.CharacterLevel()).SelectMany(g => g.ActiveGems) ) {
				var skill = skills.FirstOrDefault(s => SkillBar.IsNamed(s, gem.Name));
				if ( IsReservation(skill) ) yield return skill;
			}
		}

		/// <summary>Is the skill's buff on the player.</summary>
		public static bool IsOn(ActorSkill skill) => HasBuff(GetPlayer(), BuffName(skill));

		/// <summary>The planned reservation skills that are off (and not in their cooldown).</summary>
		public IEnumerable<ActorSkill> Off() {
			long now = Time.ElapsedMilliseconds;
			return Planned().Where(s => !IsOn(s) && !(cooldown.TryGetValue(SkillBar.SkillName(s), out long until) && now < until));
		}

		public void Reset() { if ( phase != Phase.None ) Log($"AuraKeeper: reset while {phase} ({skill})"); phase = Phase.None; }

		/// <summary>Turn on a planned reservation skill that is off; false if none is (and it is not busy).</summary>
		/// <param name="safe">No monsters near: start only then (one started is finished).</param>
		public bool Tick(CombatContext ctx, bool safe) {
			long now = Time.ElapsedMilliseconds;
			if ( phase == Phase.None ) {
				if ( SkillsPausedHere() ) return false; // (no buffs in a town or hideout)
				var off = Off().FirstOrDefault();
				if ( off == null || !safe ) return false;
				skill = SkillBar.SkillName(off);
				started = now;
				lastClick = 0;
				openedAt = 0;
				phase = Phase.Opening;
				Log($"AuraKeeper: {skill} is off (no {BuffName(off)} buff): turning it on");
			}
			ctx.Driver.StopWalking(Status = $"turning {skill} on");
			if ( now - lastClick < Pace ) return true;
			var current = Planned().FirstOrDefault(s => SkillBar.IsNamed(s, skill));
			if ( current != null && IsOn(current) ) {
				Log($"AuraKeeper: {skill} is on");
				return Finish(ctx);
			}
			if ( now - started > ConfirmMs + 2 * OpenWaitMs + SettleMs ) {
				Log($"AuraKeeper: {skill} did not turn on; leaving it for {CooldownMs / 1000} s");
				cooldown[skill] = now + CooldownMs;
				return Finish(ctx);
			}
			switch ( phase ) {
				case Phase.Opening: {
					if ( SkillBar.IsChoosing ) {
						// (let it finish appearing before clicking in it)
						if ( openedAt == 0 ) openedAt = now;
						Status = "waiting for the skills to open";
						if ( now - openedAt >= SettleMs ) phase = Phase.Toggling;
						return true;
					}
					openedAt = 0;
					// one click, then wait for it to open: another click while it opens closes it again
					if ( lastClick > 0 && now - lastClick < OpenWaitMs ) { Status = "waiting for the skills to open"; return true; }
					// a key slot (eg, Q), not a mouse button's
					var slots = SkillBar.GetSlots().Where(s => s.IsVisible && s.Index != SkillBar.LeftClickSlot).ToList();
					var slot = slots.FirstOrDefault(s => !s.IsMouseButton && s.Key != VKey.None && s.Modifiers == VKey.None)
						?? slots.FirstOrDefault(s => !s.IsMouseButton) ?? slots.FirstOrDefault();
					if ( slot == null ) { Status = "no skill bar slot to click"; return true; }
					Status = $"opening the skills (Ctrl + click {SkillBar.KeyName(slot.Key)})";
					if ( ctx.Driver.Send(Inputs.CtrlClick(slot.Rect)) ) { Log($"AuraKeeper: Ctrl + click {SkillBar.KeyName(slot.Key)} at {now} (last {lastClick}, open {SkillBar.IsChoosing})"); lastClick = now; }
					return true;
				}
				case Phase.Toggling: {
					if ( !SkillBar.IsChoosing ) { phase = Phase.Opening; openedAt = 0; return true; }
					var cell = SkillBar.FindAvailable(skill);
					if ( cell == null ) {
						Log($"AuraKeeper: {skill} is not among the skills ({string.Join(", ", SkillBar.GetAvailable())})");
						cooldown[skill] = now + CooldownMs;
						return Finish(ctx);
					}
					Status = $"Ctrl + right clicking {skill}";
					if ( ctx.Driver.Send(Inputs.CtrlRightClick(cell.Rect)) ) { Log($"AuraKeeper: Ctrl + right click {skill} at {now}"); lastClick = now; phase = Phase.Confirming; }
					return true;
				}
				case Phase.Confirming:
					// (the buff, checked above)
					Status = $"waiting for {skill}'s buff";
					return true;
			}
			return true;
		}

		private bool Finish(CombatContext ctx) {
			phase = Phase.None;
			// (don't leave the skills open)
			if ( SkillBar.IsChoosing ) ctx.Driver.Send(Inputs.Press(VKey.Escape));
			return true;
		}
	}
}
