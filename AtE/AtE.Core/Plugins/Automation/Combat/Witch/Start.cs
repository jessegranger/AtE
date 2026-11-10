using System.Collections.Generic;

namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class WitchStart : StartBase {
		public override string Name => "Witch Start";
		protected override string StartingWeapon => "Driftwood Wand";
		protected override string StartingSkill => "Fireball";
		protected override string StartingSupport => "Arcane Surge Support";
		protected override Dictionary<string, string> QuestRewards => new Dictionary<string, string> {
			["Enemy at the Gate"] = "Rolling Magma",
		};
	}
}
