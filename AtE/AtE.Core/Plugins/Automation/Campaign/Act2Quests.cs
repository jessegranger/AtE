using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 2's quests, one handler each (in the order the wiki lists them; see QuestHandlers for the shared helpers), and the way up The Ancient Pyramid (PyramidGoals, also the act's route).
	/// </summary>
	public partial class CampaignRoutine {
		/// <summary>
		/// The Ancient Pyramid, to The City of Sarn (seen live, 3.29.4): up three Dungeon/Stairs/dungeon_stairs_up tiles (the
		/// stairs on them labelled "Stairs", "Stairs" and "Pyramid Apex", each to the next floor of the same area: only the
		/// floor the player is on can be walked to, see ClimbStairs) to the apex, where the Dark Altar (entity
		/// Metadata/Monsters/IncaShadowBoss/IncaBossSpawner, at <2231, 437>) is on the Dungeon/Features/inca_boss_emerge tile
		/// (<2227, 434>). Click it, wait there (a long time) for the Vaal Oversoul to come out, kill him (Orbit), collect the
		/// "Crafting Recipe" he leaves near, and take the door to The City of Sarn (entity
		/// Metadata/QuestObjects/Inca/IncaPyramidTransition, on the Dungeon/Features/dungeon_huangdoor tile at <2230, 535>),
		/// which may take a few seconds to show after he dies. That door targetable is the boss dead (eg, after a restart).
		/// </summary>
		private static Goal[] PyramidGoals(Func<string> skill) {
			// (the door is told by its entity being targetable, in case it is there before he dies; remembered for the area
			// instance, for when it is out of range)
			uint openIn = 0;
			bool sarnOpen() {
				uint hash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
				if ( hash != 0 && hash == openIn ) return true;
				bool open = GetEntities()?.Any(e => IsValid(e) && IsValid(e.GetComponent<AreaTransition>())
					&& string.Equals(e.GetComponent<Render>()?.Name, CityOfSarn, StringComparison.OrdinalIgnoreCase)
					&& (e.GetComponent<Targetable>()?.IsTargetable ?? false)) ?? false;
				if ( open && hash != 0 ) { openIn = hash; Log($"Campaign: the door to {CityOfSarn} is open: the Vaal Oversoul is dead"); }
				return open;
			}
			bool bossInSight() => GetEntities()?.Any(e => e != null && IsVaalOversoul(e)) ?? false;
			var emerge = Search.Tile("/inca_boss_emerge");
			return new Goal[] {
				new ClimbStairs("the pyramid", "/Stairs/dungeon_stairs_up", "/inca_boss_emerge", "Stairs", "Pyramid Apex") { Where = AncientPyramid, DoneWhen = sarnOpen },
				new Activate("Dark Altar") { Where = AncientPyramid, SearchAt = emerge, DoneWhen = () => bossInSight() || sarnOpen() },
				// (not in sight while he has not come out: Search waits at the tile)
				new Orbit("the Vaal Oversoul", IsVaalOversoul, skill) { Where = AncientPyramid, SearchAt = emerge, DoneWhen = sarnOpen },
				// (none to be seen for a while, near where he died: picked up already, eg before a restart)
				new PickUp(CraftingRecipe) { Where = AncientPyramid, SearchAt = emerge, DoneWhen = NoLabelFor(CraftingRecipe, 8000) },
				new TakeTransition(CityOfSarn) { Where = AncientPyramid, SearchAt = Search.Tile("/dungeon_huangdoor"), DoneWhen = () => InAny(CityOfSarn) },
			};
		}

		/// <summary>The Vaal Oversoul (Metadata/Monsters/IncaShadowBoss/..., not its spawner, the Dark Altar).</summary>
		private static bool IsVaalOversoul(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& ((path.IndexOf("/IncaShadowBoss/", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf("Spawner", StringComparison.OrdinalIgnoreCase) < 0)
				|| (e.GetComponent<Render>()?.Name?.Contains("Vaal Oversoul", StringComparison.OrdinalIgnoreCase) ?? false));

		private void Act2Quests(FinishAllQuests quests) {
			Func<string> main = BuildPlan.MainSkill;
			// The Way Forward, from Act 1 (it shows on Act 2's quests): "Travel to The Western Forest" (a trip), then down
			// the road south to the blocked exit: kill Captain Arteri for the Thaumetic Emblem, click the Thaumetic Seal
			// beside it (it opens the way to Prisoner's Gate), and "Talk to Bestel for your reward", in Lioneye's Watch.
			// Seen live (3.29.4): "Captain Arteri" is the name; he stands near the mountain pass (around <620, 880>, the
			// pass tile at <379, 896>); no emblem dropped (Pick up timed out after 5 s, harmlessly), and once he died the
			// step was "Travel to Lioneye's Watch". TODO: the seal's label (not seen: the step had moved on already).
			bool roadDone() => AtReward("The Way Forward");
			// (the blocked exit is the mountain pass tile, forest_mountainpass, the way to Prisoner's Gate: look there first,
			// instead of exploring the whole forest)
			var pass = Search.Tile("/forest_mountainpass");
			quests.Handlers.Add(new QuestHandler("The Way Forward", new Sequence("The Way Forward: the Thaumetic Seal",
				new Orbit("Captain Arteri", MonsterNamed("Captain Arteri"), BuildPlan.MainSkill) { Where = WesternForest, DoneWhen = roadDone, SearchAt = pass },
				new PickUpQuestItem("the Thaumetic Emblem") { Where = WesternForest, DoneWhen = roadDone },
				new Activate("Thaumetic Seal") { Where = WesternForest, DoneWhen = roadDone, SearchAt = pass })) {
				Active = step => QuestHandler.DefaultActive(step) && !IsEmperorsRoadStep(step),
				RewardTown = LioneyesWatch,
			});
			// Eramir gives it after Through Sacred Ground (or on talking to a bandit leader). Kill all three, and pick up their
			// quest items: Kraityn in The Broken Bridge (The Old Fields, The Crossroads), Alira in The Western Forest and
			// Oak in The Wetlands (both from The Riverways). Each is talked to first, and killed by choosing "Kill [name]"
			// (see KillBandit). The step "Travel to The Broken Bridge, The Western Forest or The Wetlands" is the handler's
			// (not a trip to one area). Then the three to Eramir (a talk in town): he makes them The Apex (for the Dark
			// Altar, see Act2), and gives a passive point.
			// A bandit is done once their area is not in the step "Travel to ..." any more (3.29.4, seen live: "Travel to The
			// Western Forest or The Wetlands" after Kraityn), or they were seen dead.
			// TODO: confirm live: the other steps' text, the bandits' names (Render.Name), the "Kill [name]" button's text
			const string bandits = "Deal with the Bandits";
			// The step "Eramir wants you to kill [name]" (3.29.4, seen live: "... kill Alira") names the one to go for: the
			// others count as done while it shows (DoneWhen is asked again each frame, so they come back after).
			bool banditDone(string name, string area) => AtReward(bandits)
				|| (StepOf(bandits) is string s && (
					(s.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) && s.IndexOf(Bare(area), StringComparison.OrdinalIgnoreCase) < 0)
					|| (s.IndexOf(" kill ", StringComparison.OrdinalIgnoreCase) >= 0 && s.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)));
			Goal Bandit(string name, string area) => new Sequence($"{bandits}: {name}",
				new KillBandit(name, main) { Where = area, DoneWhen = () => banditDone(name, area) },
				new PickUpQuestItem($"{name}'s quest item") { Where = area, DoneWhen = () => banditDone(name, area) });
			quests.Handlers.Add(new QuestHandler(bandits, new Sequence(bandits,
				Bandit("Kraityn", BrokenBridge), Bandit("Alira", WesternForest), Bandit("Oak", Wetlands))) {
				Active = step => step != null && !FinishAllQuests.IsToTown(step) && !step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase)
					&& !(step.StartsWith("Take ", StringComparison.OrdinalIgnoreCase) || step.StartsWith("Talk to ", StringComparison.OrdinalIgnoreCase)),
			});
			// (each bandit's own quest shows when they are met: the bandits are Deal with the Bandits's)
			quests.Handlers.Add(QuestHandler.Elsewhere("The Bandit Lord Alira", bandits));
			quests.Handlers.Add(QuestHandler.Elsewhere("The Bandit Lord Oak", bandits));
			quests.Handlers.Add(QuestHandler.Elsewhere("The Bandit Lord Kraityn", bandits));
			// Yeena gives it. "Travel to The Den" (a trip, from The Old Fields), "Hunt The Great White Beast" there, then "Talk
			// to Yeena for your reward": the Quicksilver Flask
			var beast = KillBoss("The Great White Beast", "the Great White Beast", Den);
			beast.PreferredReward = "Quicksilver Flask";
			quests.Handlers.Add(beast);
			// Greust gives it once The Way Forward reopens the way in The Western Forest. "Investigate the Blackguards" (in The
			// Chamber of Sins), "Kill Fidelitas", talk to the girl (Helena) hiding there, "Pick up the Baleful Gem" from the
			// Strange Device: all in The Chamber of Sins Level 2. Then "Meet Helena in Town" (a talk in town), and Greust's reward.
			// TODO: confirm live (not seen yet): the steps' text, the level Fidelitas is on, Helena's NPC there, the device's label
			const string intruders = "Intruders in Black";
			// (the stage of the quest, by its step; a step not known is the start: eg "Explore the Chamber of Sins", 3.29.4,
			// on entering Level 1, means going on to Level 2, not that it is done)
			int sins() => StepOf(intruders) is string s
				? (s.StartsWith("Kill ", StringComparison.OrdinalIgnoreCase) ? 1
					: s.StartsWith("Talk to ", StringComparison.OrdinalIgnoreCase) && !s.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase) ? 2
					: s.StartsWith("Pick up", StringComparison.OrdinalIgnoreCase) ? 3
					: s.StartsWith("Meet ", StringComparison.OrdinalIgnoreCase) || s.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase) ? 4 : 0)
				: -1;
			// (Level 2's quest tile, RuinedTempleForest/Features/templeruinforest_questcart, is where to look for all of them,
			// 3.29.4: one tile, at <333, 1115>, not explored to yet)
			var questCart = Search.Tile("/templeruinforest_questcart");
			quests.Handlers.Add(new QuestHandler(intruders, new Sequence(intruders,
				new Orbit("Fidelitas", MonsterNamed("Fidelitas"), main) { Where = ChamberOfSins2, SearchAt = questCart, DoneWhen = () => sins() >= 2 },
				new TalkTo("the girl (Helena)", TalkTo.NpcNamed("Helena")) { Where = ChamberOfSins2, SearchAt = questCart, DoneWhen = () => sins() >= 3 },
				new Activate("Strange Device") { Where = ChamberOfSins2, SearchAt = questCart, DoneWhen = () => sins() >= 4 },
				new PickUpQuestItem("the Baleful Gem") { Where = ChamberOfSins2, DoneWhen = () => sins() >= 4 })) {
				FieldNpcs = new[] { "Helena", "the girl" },
			});
			// Silk gives it (or it starts on killing the Weaver). The Weaver (a unique giant spider) is in The Weaver's
			// Chambers (from The Western Forest) and drops Maligaro's Spike (kept: The Root of the Problem uses it); then
			// "Take Maligaro's Spike to Silk" (a talk in town)
			// TODO: confirm live (not seen yet): the steps' text, the Weaver's name
			// the Weaver is in his arena, through the door labelled "The Weaver's Nest" (3.29.4, seen live: a spidergrove_exit
			// tile, see AreaExits.PlacedExitTiles), not found by exploring the chambers: walk straight to that door
			quests.Handlers.Add(KillForQuestItem("Sharp and Cruel", "Maligaro's Spike", "the Weaver", WeaversChambers, TownOf(2),
				door: "The Weaver's Nest", doorAt: Search.Exit("The Weaver's Nest", "/spidergrove_exit_v")));
			// Yeena gives it after Intruders in Black and The Great White Beast (or it starts on taking the hand). "Find the
			// Golden Hand in the Crypt": the altar in The Crypt Level 2 (south of The Crossroads) gives it; then "Take the
			// Golden Hand to Yeena" (a talk in town): a Skill Book (UseBooks reads it)
			// TODO: confirm live (not seen yet): the steps' text, the altar's label
			quests.Handlers.Add(new QuestHandler("Through Sacred Ground", new Sequence("Through Sacred Ground: the Golden Hand", new Goal[] {
				new Activate("Altar") { Where = Crypt2 },
				new PickUpQuestItem("the Golden Hand") { Where = Crypt2 },
			}.Concat(PortalToTown(TownOf(2))).ToArray())) {
				Active = step => step != null && step.StartsWith("Find ", StringComparison.OrdinalIgnoreCase),
			});
			// Helena gives it (after Intruders in Black). "Travel to The Wetlands" (a trip, from The Riverways), poison the tree
			// (Lorrata) there with Maligaro's Spike (Sharp and Cruel) and the Baleful Gem (Intruders in Black): click its
			// roots, which opens the way to The Vaal Ruins; then "Find a way through the Vaal Ruins": click the "Ancient Seal"
			// (seen live, 3.29.4: entity Metadata/QuestObjects/Inca/IncaDarknessRelease at <437, 1092>, on the area's only
			// Dungeon/Features/dungeon_bossrelease_v01_01 tile, at <436, 1092>), so walk straight to that tile. Touching it
			// leaves this quest's step as it was, but adds "Shadow of the Vaal" to the tracker: that is the seal done, and
			// the handler then only takes the exit labelled "The Northern Forest" (seen live: on a dungeon_inca_exit tile at
			// <321, 1488>, see AreaExits.PlacedExitTiles), and is not active anywhere else.
			// TODO: confirm live (not seen yet): the roots' label, the exit's label
			const string root = "The Root of the Problem";
			// (any step naming The Vaal Ruins: the tree is poisoned; a step not known is the tree still to poison)
			bool poisoned() => StepOf(root) is string s && s.IndexOf("Vaal Ruins", StringComparison.OrdinalIgnoreCase) >= 0;
			bool sealDone() => StepOf("Shadow of the Vaal") != null;
			quests.Handlers.Add(new QuestHandler(root, new Sequence($"{root}: poison the tree",
				new Activate("Tree Roots") { Where = Wetlands, DoneWhen = poisoned },
				new TakeTransition(VaalRuins) { Where = Wetlands, DoneWhen = () => InAny(VaalRuins) },
				new Activate("Ancient Seal") { Where = VaalRuins, DoneWhen = sealDone, SearchAt = Search.Tile("/dungeon_bossrelease") },
				new TakeTransition(NorthernForest) { Where = VaalRuins, DoneWhen = () => InAny(NorthernForest) })) {
				// (a "Travel to The Vaal Ruins" too is the handler's: the way is the roots' new exit, not a known route)
				Active = step => (QuestHandler.DefaultActive(step) || poisoned()) && !(sealDone() && !InAny(VaalRuins)),
			});
			// Touching the Ancient Seal (The Root of the Problem) gives it. Its step is "Travel to The Forest Encampment" all the
			// way, which is ignored in the areas on the way (any step there is the handler's): in The Northern Forest, walk to
			// its waypoint (to discover it), then take the exit to The Caverns (Forest/Transitions/waterfall_cave_entrance);
			// in The Caverns, the exit to The Ancient Pyramid (Dungeon/Stairs/dungeon_stairs_up); then up the pyramid, the
			// Dark Altar, the Vaal Oversoul, and on to The City of Sarn (see PyramidGoals)
			const string vaal = "Shadow of the Vaal";
			quests.Handlers.Add(new QuestHandler(vaal, new Sequence(vaal, new Goal[] {
				new GoToWaypoint() { Where = NorthernForest, DoneWhen = () => WaypointPanel.IsKnownUnlocked(NorthernForest) || InAny(Caverns, AncientPyramid) },
				new TakeTransition(Caverns) { Where = NorthernForest, SearchAt = Search.Exit(Caverns, "/waterfall_cave_entrance"), DoneWhen = () => InAny(Caverns, AncientPyramid) },
				new TakeTransition(AncientPyramid) { Where = Caverns, SearchAt = Search.Exit(AncientPyramid, "/dungeon_stairs_up"), DoneWhen = () => InAny(AncientPyramid) },
			}.Concat(PyramidGoals(main)).ToArray())) {
				Active = step => step != null && InAny(NorthernForest, Caverns, AncientPyramid),
				// (once the boss is dead the quest may leave the tracker: the recipe and the way on to Act 3 are still to do)
				Untracked = () => InAny(AncientPyramid),
			});
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("Einhar's Hunt"));
			quests.Handlers.Add(Stub("Einhar's Menagerie"));
			quests.Handlers.Add(Stub("Safe and Sound"));
		}
	}
}
