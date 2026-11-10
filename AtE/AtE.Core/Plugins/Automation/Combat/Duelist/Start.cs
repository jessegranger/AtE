using System.Collections.Generic;

namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class DuelistStart : StartBase {
		public override string Name => "Duelist Start";
		protected override string StartingWeapon => "Rusted Sword";
		protected override string StartingSkill => "Double Strike";
		protected override string StartingSupport => "Chance to Bleed Support";
		protected override Dictionary<string, string> QuestRewards => new Dictionary<string, string> {
			["Enemy at the Gate"] = "Splitting Steel",
		};
	}
}

