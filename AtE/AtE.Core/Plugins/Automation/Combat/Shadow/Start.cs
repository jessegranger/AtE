using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class ShadowStart : StartBase {
		public override string Name => "Shadow Start";
		protected override string StartingWeapon => "Glass Shank";
		protected override string StartingSkill => "Viper Strike";
		protected override string StartingSupport => "Chance To Poison Support";
		protected override Dictionary<string, string> QuestRewards => new Dictionary<string, string> {
			["Enemy at the Gate"] = "Cobra Lash",
		};
	}
}

