using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 1's quests, one handler each (in the order the wiki lists them; see QuestHandlers for the shared helpers).
	/// </summary>
	public partial class CampaignRoutine {
		private void Act1Quests(FinishAllQuests quests) {
			Func<string> main = BuildPlan.MainSkill;
			// "Kill Hillock" (on The Twilight Strand, where Exit The Twilight Strand kills him on the way out anyway);
			// "Enter town for your reward" is a trip to town (see FinishAllQuests.Destination), "Talk to Tarkleigh for
			// your reward" a reward taken there (TakeQuestRewards)
			quests.Handlers.Add(KillBoss("Enemy at the Gate", "Hillock", TwilightStrand, e => e?.Path?.Contains("Hillock") ?? false,
				BuildPlan.StartingSkill, Search.TheOnlyExit));
			// "Talk to Nessa", then "Talk to Bestel" (talks in town), "Find the Medicine Chest on The Tidal Island" (Hailrake
			// drops it; The Tidal Island has no waypoint: by waypoint to The Coast, and walk), "Take the Medicine Chest to
			// Nessa" (a talk in town)
			quests.Handlers.Add(KillForQuestItem("Mercy Mission", "the Medicine Chest", "Hailrake", TidalIsland, LioneyesWatch, IsHailrake));
			// (optional: skipped)
			quests.Handlers.Add(Skip("A Dirty Job"));
			// "Find the Glyphs in the Rhoa Nests", "Take the Glyphs to the glyph wall", then "Talk to Tarkleigh for your
			// reward": first through the opened passage to its waypoint (the way to town), while still in either area.
			// The quest is the way into The Submerged Passage: it starts at the wall (or a glyph picked up), so before
			// that it is not in the quest tracker, and it is done once the passage's waypoint is unlocked. Not in the
			// tracker: still to do while the waypoint panel shows that waypoint locked.
			bool eggsDone() => AtReward("Breaking Some Eggs") || WaypointPanel.IsKnownUnlocked(SubmergedPassage);
			quests.Handlers.Add(new QuestHandler("Breaking Some Eggs", new Sequence("Breaking Some Eggs: the glyphs",
				// each nest drops a glyph: Haliotis (Glyph1), Ammonite (Glyph2), Roseus (Glyph3)
				// (at the reward, the glyphs are spent: not a reason to open the nests again)
				new OpenQuestChests("Rhoa Nest", "rhoanest", "QuestChests/Rhoa", 3) { Where = MudFlats, FightRadius = 100, ItemPathPart = "QuestItems/Glyphs/",
					DoneWhen = eggsDone },
				// with the three glyphs, the wall opens the exit beside it
				new UseQuestObject("Strange Glyph Wall", "WaterCave/GlyphWall", "watercave", "WaterCave/WaterCaveAreaTransition") { Where = MudFlats,
					DoneWhen = eggsDone },
				DiscoverWaypoint(SubmergedPassage))) {
				Active = step => QuestHandler.DefaultActive(step)
					|| (step != null && step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase) && InAny(MudFlats, SubmergedPassage)),
				Untracked = () => WaypointPanel.IsKnownLocked(SubmergedPassage),
			});
			// "Talk to Tarkleigh" (in town) after Breaking Some Eggs, "Kill the The Dweller of the Deep" in The Flooded Depths
			// (past The Submerged Passage), "Talk to Tarkleigh for your reward" (a skill book: UseBooks reads it)
			quests.Handlers.Add(KillBoss("The Dweller of the Deep", "the Dweller of the Deep", FloodedDepths));
			// Tarkleigh gives it on entering The Ledge; it finishes in Act 2 (see Act2Quests). "Follow the Emperor's Road":
			// through The Prison to Prisoner's Gate, where seeing the road closed updates it: walk to within 50 cells of
			// Prisoner's Gate's Beach/Transitions/beach_passageblock tile
			quests.Handlers.Add(new QuestHandler("The Way Forward", new Sequence("The Way Forward: the Emperor's Road",
				new ApproachTiles("the Emperor's Road", "/beach_passageblock") { Where = PrisonersGate, Reach = 90 })) {
				Active = IsEmperorsRoadStep,
			});
			// Tarkleigh gives it on entering The Ledge (or on meeting Brutus): "Kill Brutus", in the Warden's Chambers (a door
			// in The Upper Prison), then "Travel to Prisoner's Gate" (a trip) and "Talk to Tarkleigh for your reward"
			// TODO: confirm live (not seen yet): the step's text, Brutus's name, and that the Warden's Chambers is a door
			quests.Handlers.Add(KillBoss("The Caged Brute", "Brutus", UpperPrison, door: "Warden's Chambers"));
			// Captain Fairgraves, on the hill by The Ship Graveyard's waypoint, gives it (it is not in the quest tracker
			// before: do it when he shows his marker there). "Find the Allflame": on the Slave Girl's corpse, deepest in
			// The Ship Graveyard Cave, guarded by Stranglecharm. "Deliver the Allflame to Captain Fairgraves": back in The
			// Ship Graveyard (the cave's exit near the corpse is a one-way way back). "Kill Captain Fairgraves": talk to him,
			// he turns hostile. Then "Talk to Bestel" in town (a Book of Skill: UseBooks reads it).
			// TODO: confirm live (not seen yet): the steps' text, the NPC's path or name, the corpse's label, Stranglecharm's name
			const string mariner = "The Marooned Mariner";
			// (a step not known is taken as the Allflame still to find, not as the quest done)
			int stage() => StepOf(mariner) is string s
				? (s.StartsWith("Deliver ", StringComparison.OrdinalIgnoreCase) || (s.StartsWith("Talk to ", StringComparison.OrdinalIgnoreCase) && s.Contains("Fairgraves")) ? 2
					: s.StartsWith("Kill ", StringComparison.OrdinalIgnoreCase) ? 3
					: s.StartsWith("Talk to ", StringComparison.OrdinalIgnoreCase) ? 4 : 1)
				: 0;
			var fairgraves = TalkTo.NpcNamed("Fairgraves");
			quests.Handlers.Add(new QuestHandler(mariner, new Sequence(mariner,
				new TalkTo("Captain Fairgraves", fairgraves) { Where = ShipGraveyard, DoneWhen = () => stage() >= 1 },
				new Orbit("Stranglecharm", MonsterNamed("Stranglecharm"), main) { Where = ShipGraveyardCave, DoneWhen = () => stage() >= 2 },
				new Activate("Slave Girl") { Where = ShipGraveyardCave, DoneWhen = () => stage() >= 2 },
				new PickUpQuestItem("the Allflame") { Where = ShipGraveyardCave, DoneWhen = () => stage() >= 2 },
				new TalkTo("Captain Fairgraves", fairgraves) { Where = ShipGraveyard, DoneWhen = () => stage() >= 3 },
				// (hostile, he is a monster: talk again until he is)
				new TalkTo("Captain Fairgraves", fairgraves) { Where = ShipGraveyard, DoneWhen = () => stage() >= 4 || (GetEntities()?.Any(MonsterNamed("Captain Fairgraves")) ?? false) },
				new Orbit("Captain Fairgraves", MonsterNamed("Captain Fairgraves"), main) { Where = ShipGraveyard, DoneWhen = () => stage() >= 4 })) {
				FieldNpcs = new[] { "Captain Fairgraves", "Fairgraves" },
				Untracked = () => InAny(ShipGraveyard) && (GetEntities()?.Any(e => e != null && fairgraves(e) && (e.GetComponent<NPC>()?.HasIconOverhead ?? false)) ?? false),
			});
			// Nessa gives it on entering The Cavern of Wrath, and her reward (a gem) at once: "Talk to Nessa for your reward"
			// (by the waypoint to town, and back). "Travel to Merveil's Caverns" (see TravelAliases), "Kill Merveil" (in her
			// lair, through The Cavern of Anger) and "Enter the Forest" are the act's route (Route to The Cavern of Anger)
			quests.Handlers.Add(QuestHandler.Elsewhere("The Siren's Cadence", "Route to The Cavern of Anger"));
			// the Trial of Ascendancy in The Lower Prison. TODO: how to find it, get through its traps and challenges, and
			// reach its objective; for now the routine stops there once (switch it on again to go on past it)
			quests.Handlers.Add(new QuestHandler("The Lord's Labyrinth", new Sequence("The Lord's Labyrinth: the Trial of Ascendancy",
				new StopForHelp("in The Lower Prison: its Trial of Ascendancy is not written yet") { Where = LowerPrison })) {
				Active = _ => InAny(LowerPrison),
				Untracked = () => InAny(LowerPrison),
			});
		}
	}
}
