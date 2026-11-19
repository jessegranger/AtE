using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 7's quests: stubs, not written yet.
	/// </summary>
	public partial class CampaignRoutine {
		private void Act7Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("The Silver Locket"));
			quests.Handlers.Add(Stub("Essence of the Artist"));
			quests.Handlers.Add(Stub("Web of Secrets"));
			quests.Handlers.Add(Stub("The Master of a Million Faces"));
			quests.Handlers.Add(Stub("In Memory of Greust"));
			quests.Handlers.Add(Stub("Queen of Despair"));
			quests.Handlers.Add(Stub("Kishara's Star"));
			quests.Handlers.Add(Stub("Lighting the Way"));
			quests.Handlers.Add(Stub("The Mother of Spiders"));
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("No Time like the Present"));
		}
	}
}
