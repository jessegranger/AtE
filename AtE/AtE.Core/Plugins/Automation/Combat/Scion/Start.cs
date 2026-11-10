using System.Collections.Generic;

namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class ScionStart : StartBase {
		public override string Name => "Scion Start";
		protected override string StartingWeapon => "Rusted Sword";
		protected override string StartingSkill => "Spectral Throw";
		protected override string StartingSupport => "Prismatic Burst Support";
		protected override Dictionary<string, string> QuestRewards => new Dictionary<string, string> {
			["Enemy at the Gate"] = "Splitting Steel",
		};
	}
}

