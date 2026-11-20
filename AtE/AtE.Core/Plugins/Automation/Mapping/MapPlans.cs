using System.Collections.Generic;

namespace AtE {

	public partial class MappingRoutine {

		/// <summary>
		/// The maps with plans of their own, by the map's area name (eg, "Silo", as "Silo Map" opens it): what to do in it,
		/// from entering to going back to the hideout. A map not listed runs DefaultPlan (clear, kill the boss, go back).
		/// A plan is a Sequence of goals, like the Campaign's (eg, new Kill("the boss", e => ..., BuildPlan.MainSkill),
		/// new ClearMap(this), new ReturnToHideout(this)); it starts over in each new instance of its map.
		/// </summary>
		private IEnumerable<(string map, Sequence plan)> MapPlans() => new[] {
			("Silo", Silo()),
			("Mud Geyser", MudGeyser()),
		};

		// TODO: Silo's own steps (to be specified); the default plan until then
		private Sequence Silo() => DefaultPlan("Silo");

		// TODO: Mud Geyser's own steps (to be specified); the default plan until then
		private Sequence MudGeyser() => DefaultPlan("Mud Geyser");
	}
}
