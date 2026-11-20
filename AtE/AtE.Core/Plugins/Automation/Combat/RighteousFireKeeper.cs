using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Keep Righteous Fire burning, when the loaded build has it (an enabled gem in the current skill set), it is socketed,
	/// and bound to a skill bar key. Unlike a reservation skill (AuraKeeper), it goes out on each area change, so it is
	/// cast again: whenever the player has neither its buff ("righteous_fire") nor the grace period ("grace_period", on
	/// arriving in an area: casting then would end it early). If a cast does not light it, it is pressed again at most once
	/// every RetryMs, never spammed.
	///
	/// A State that runs forever (OnTick returns this), on the default Machine: Start puts the one Instance there, once.
	/// It works only while the combat section's chosen driver is on, sending the key through that driver's input.
	/// </summary>
	public class RighteousFireKeeper : State {
		public const string SkillName = "Righteous Fire";
		public const string BuffName = "righteous_fire";

		/// <summary>The one instance (see Start).</summary>
		public static readonly RighteousFireKeeper Instance = new RighteousFireKeeper();
		private static bool started;
		private CombatSection section;
		/// <summary>Run the Instance on the default Machine, if not already, for the combat section's driver.</summary>
		public static void Start(CombatSection section) {
			Instance.section = section;
			if ( started ) return;
			started = true;
			Run(Instance);
		}

		/// <summary>The least time between presses.</summary>
		public long RetryMs = 10000;
		/// <summary>How often to look at the build and the skill bar again.</summary>
		public long RefreshMs = 5000;
		private long lastPress = -1000000, refreshedAt = -1000000;
		private bool wanted;

		public override IState OnTick(long dt) {
			if ( !PoEMemory.IsAttached ) return this;
			var driver = section?.Current;
			if ( driver == null || !driver.IsActive || driver.InputBusy ) return this;
			long now = Time.ElapsedMilliseconds;
			if ( now - lastPress < RetryMs ) return this;
			if ( SkillsPausedHere() ) return this; // (no buffs in a town or hideout)
			var player = GetPlayer();
			if ( !IsValid(player) || HasBuff(player, BuffName) || HasBuff(player, "grace_period") ) return this;
			if ( now - refreshedAt > RefreshMs ) {
				refreshedAt = now;
				bool before = wanted;
				wanted = InBuild();
				if ( wanted != before ) Log($"RighteousFireKeeper: {(wanted ? "the build has" : "the build does not have")} {SkillName}");
			}
			if ( !wanted ) return this;
			var slot = SkillBar.FindSlot(SkillName);
			var skill = slot?.Skill;
			if ( skill == null || slot.Key == VKey.None || !skill.CanBeUsed ) return this;
			// (an instant self-cast: the cursor stays where it is)
			if ( !driver.Send(Inputs.UseSlotHere(slot, 40)) ) return this;
			lastPress = now;
			Log($"RighteousFireKeeper: no {BuffName} buff: casting {SkillName} ({SkillBar.KeyName(slot.Key)})");
			return this;
		}

		/// <summary>Is Righteous Fire an enabled gem of the build's skill set at the character's level.</summary>
		private static bool InBuild() {
			var plan = BuildPlan.Gems();
			return plan != null && plan.GroupsFor(GemPlan.CharacterLevel()).SelectMany(g => g.EnabledGems)
				.Any(g => string.Equals(g.Name, SkillName, StringComparison.OrdinalIgnoreCase));
		}
	}
}
