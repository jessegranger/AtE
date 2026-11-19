using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 8's quests: stubs, not written yet.
	/// </summary>
	public partial class CampaignRoutine {
		private void Act8Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("Essence of the Hag"));
			quests.Handlers.Add(Stub("Love is Dead"));
			quests.Handlers.Add(Stub("The Gemling Legion"));
			quests.Handlers.Add(Stub("Reflection of Terror"));
			quests.Handlers.Add(Stub("The Wings of Vastiri"));
			quests.Handlers.Add(Stub("Lunar Eclipse"));
			quests.Handlers.Add(Stub("Solar Eclipse"));
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
		}
	}
}
