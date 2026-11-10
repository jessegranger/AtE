using System.Collections.Generic;

namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class MarauderStart : StartBase {
		public override string Name => "Marauder Start";
		protected override string StartingWeapon => "Driftwood Club";
		protected override string StartingSkill => "Heavy Strike";
		protected override string StartingSupport => "Ruthless Support";
		protected override Dictionary<string, string> QuestRewards => new Dictionary<string, string> {
			["Enemy at the Gate"] = "Ground Slam",
		};
	}
}
