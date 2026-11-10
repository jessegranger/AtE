using System.Collections.Generic;

namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class TemplarStart : StartBase {
		public override string Name => "Templar Start";
		protected override string StartingWeapon => "Driftwood Sceptre";
		protected override string StartingSkill => "Holy Strike";
		protected override string StartingSupport => "Hallow Support";
		protected override Dictionary<string, string> QuestRewards => new Dictionary<string, string> {
			["Enemy at the Gate"] = "Rolling Magma",
		};
	}
}

