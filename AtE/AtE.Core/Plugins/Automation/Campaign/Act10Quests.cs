using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 10's quests: stubs, not written yet.
	/// </summary>
	public partial class CampaignRoutine {
		private void Act10Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("Safe Passage"));
			quests.Handlers.Add(Stub("Map to Tsoatha"));
			quests.Handlers.Add(Stub("No Love for Old Ghosts"));
			quests.Handlers.Add(Stub("Vilenta's Vengeance"));
			quests.Handlers.Add(Stub("Death and Rebirth"));
			quests.Handlers.Add(Stub("An End to Hunger"));
			quests.Handlers.Add(Stub("Toward the Future"));
			quests.Handlers.Add(Stub("Aiding an Archivist"));
		}
	}
}
