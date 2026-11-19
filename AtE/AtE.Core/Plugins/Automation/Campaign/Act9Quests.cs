using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 9's quests: stubs, not written yet.
	/// </summary>
	public partial class CampaignRoutine {
		private void Act9Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("The Storm Blade"));
			quests.Handlers.Add(Stub("Fastis Fortuna"));
			quests.Handlers.Add(Stub("The Ruler of Highgate"));
			quests.Handlers.Add(Stub("Queen of the Sands"));
			quests.Handlers.Add(Stub("Recurring Nightmare"));
			quests.Handlers.Add(Stub("The Immortal Syndicate"));
		}
	}
}
