using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The quest handlers, act by act, one for each quest of the campaign (in the order the wiki lists them): what to do
	/// for a quest at the steps the quest tracker shows (see QuestHandler, FinishAllQuests). A handler not written yet is
	/// a stub (QuestHandler.Stub): it does nothing, and its quest counts as having no goals.
	/// </summary>
	public partial class CampaignRoutine {

		private void AddQuestHandlers(FinishAllQuests quests) {
			switch ( quests.Act ) {
				case 1: Act1Quests(quests); break;
				case 2: Act2Quests(quests); break;
				case 3: Act3Quests(quests); break;
				case 4: Act4Quests(quests); break;
				case 5: Act5Quests(quests); break;
				case 6: Act6Quests(quests); break;
				case 7: Act7Quests(quests); break;
				case 8: Act8Quests(quests); break;
				case 9: Act9Quests(quests); break;
				case 10: Act10Quests(quests); break;
			}
		}

		private static QuestHandler Stub(string quest) => QuestHandler.Stub(quest);
		private static QuestHandler Skip(string quest) => QuestHandler.Skip(quest);

		/// <summary>
		/// A quest step "Kill [boss]" (eg, "Kill Hillock", "Kill the Dweller of the Deep"): find the boss in its area and
		/// kill it, circling it (Orbit). The boss is told by match, or by its name (see MonsterNamed). Done when the quest
		/// tracker moves on (the step no longer says to kill it).
		/// </summary>
		/// <param name="door">
		/// The label of a door to the boss's arena within the area (eg, Brutus's "Warden's Chambers" in The Upper Prison),
		/// taken first: done once the boss is in sight (eg, after a restart in the arena).
		/// </param>
		private static QuestHandler KillBoss(string quest, string boss, string area, Func<Entity, bool> match = null, Func<string> skill = null,
			Func<CombatContext, Offsets.Vector2i?> searchAt = null, string door = null) {
			var isBoss = match ?? MonsterNamed(boss);
			bool inSight() => GetEntities()?.Any(e => e != null && isBoss(e)) ?? false;
			return new QuestHandler(quest, new Sequence($"{quest}: kill {boss}",
				door == null ? null : new TakeTransition(door) { Where = area, DoneWhen = inSight },
				new Orbit(boss, isBoss, skill ?? BuildPlan.MainSkill) { Where = area, SearchAt = searchAt })) {
				Active = step => IsKillStep(step, boss),
			};
		}

		/// <summary>
		/// A step "Kill [boss]" (or "Hunt", "Slay"), however the boss is written (eg, The Dweller of the Deep's "Kill the The Dweller of the
		/// Deep"): "Kill" and the boss's name without a leading "the".
		/// </summary>
		private static bool IsKillStep(string step, string boss) => step != null
			&& (step.StartsWith("Kill ", StringComparison.OrdinalIgnoreCase) || step.StartsWith("Hunt ", StringComparison.OrdinalIgnoreCase)
				|| step.StartsWith("Slay ", StringComparison.OrdinalIgnoreCase))
			&& step.IndexOf(Bare(boss), StringComparison.OrdinalIgnoreCase) >= 0;
		private static string Bare(string name) => name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name.Substring(4) : name;

		/// <summary>
		/// A quest step "Find [item]" (eg, Mercy Mission's "Find the Medicine Chest on The Tidal Island") where a boss
		/// carries the quest item: find the boss in its area, kill it, pick up what it drops, and go back to town by a
		/// portal (picking up a Portal Scroll first if there is none). Handing it over ("Take the Medicine Chest to Nessa")
		/// is a talk in town (see Town.RewardNpc).
		/// </summary>
		/// <param name="door">
		/// The label of a door to the boss's arena within the area (eg, the Weaver's "The Weaver's Nest"), taken first (found
		/// by doorAt, else by looking): done once the boss is in sight (eg, after a restart in the arena).
		/// </param>
		private static QuestHandler KillForQuestItem(string quest, string item, string boss, string area, string town, Func<Entity, bool> match = null,
			Func<string> skill = null, string door = null, Func<CombatContext, Offsets.Vector2i?> doorAt = null) {
			var isBoss = match ?? MonsterNamed(boss);
			bool inSight() => GetEntities()?.Any(e => e != null && isBoss(e)) ?? false;
			return new QuestHandler(quest, new Sequence($"{quest}: {item}", new Goal[] {
				door == null ? null : new TakeTransition(door) { Where = area, DoneWhen = inSight, SearchAt = doorAt },
				new Orbit(boss, isBoss, skill ?? BuildPlan.MainSkill) { Where = area },
				new PickUpQuestItem(item) { Where = area },
			}.Where(g => g != null).Concat(PortalToTown(town)).ToArray())) {
				Active = step => step != null && (step.StartsWith("Find ", StringComparison.OrdinalIgnoreCase)
					|| IsKillStep(step, boss)),
			};
		}

		/// <summary>
		/// Walk into an area (eg, one a quest just opened) and to its waypoint, so the waypoint is discovered (eg, Breaking
		/// Some Eggs: The Submerged Passage, then the waypoint to town for the reward).
		/// </summary>
		private static Goal DiscoverWaypoint(string area) => new GoToWaypoint() { Where = area, DoneWhen = () => WaypointPanel.IsKnownUnlocked(area) };

		/// <summary>The quest tracker's step for the quest (its name with or without " (Optional)"), or null if it is not there.</summary>
		private static string StepOf(string quest) => Town.QuestStep(quest) ?? Town.QuestStep($"{quest} (Optional)");

		/// <summary>The quest is at its reward (its step "Talk to X for your reward"): what came before it is done.</summary>
		private static bool AtReward(string quest) => StepOf(quest)?.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase) ?? false;

		/// <summary>The player is in one of the areas (eg, a step's own area, and the one it opens).</summary>
		private static bool InAny(params string[] areas) => GetCurrentArea() is WorldArea here && areas.Any(a => GoToArea.Is(here, a));

		/// <summary>Back to town by a portal: pick up a Portal Scroll if there is none in the backpack, and read it.</summary>
		private static Goal[] PortalToTown(string town) => new Goal[] {
			new PickUp("Portal Scroll") { DoneWhen = () => Equipment.InBackpack(Offsets.PATH_SCROLL_PORTAL) },
			new UsePortalScroll(town),
		};

		/// <summary>A monster whose name (Render.Name) has name in it, without a leading "the" (eg, "the Dweller of the Deep").</summary>
		public static Func<Entity, bool> MonsterNamed(string name) {
			string bare = Bare(name);
			return e => e?.Path is string path && path.StartsWith("Metadata/Monsters/")
				&& (e.GetComponent<Render>()?.Name?.Contains(bare, StringComparison.OrdinalIgnoreCase) ?? false);
		}

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

		private static bool IsEmperorsRoadStep(string step) => step != null && step.IndexOf("Follow the Emperor's Road", StringComparison.OrdinalIgnoreCase) >= 0;

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
			bool banditDone(string area) => AtReward(bandits)
				|| (StepOf(bandits) is string s && s.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) && s.IndexOf(Bare(area), StringComparison.OrdinalIgnoreCase) < 0);
			Goal Bandit(string name, string area) => new Sequence($"{bandits}: {name}",
				new KillBandit(name, main) { Where = area, DoneWhen = () => banditDone(area) },
				new PickUpQuestItem($"{name}'s quest item") { Where = area, DoneWhen = () => banditDone(area) });
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
			// roots, which opens the way to The Vaal Ruins; then into The Vaal Ruins, which finishes it.
			// TODO: confirm live (not seen yet): the steps' text, the roots' label, the exit's label
			const string root = "The Root of the Problem";
			// (any step naming The Vaal Ruins: the tree is poisoned; a step not known is the tree still to poison)
			bool poisoned() => StepOf(root) is string s && s.IndexOf("Vaal Ruins", StringComparison.OrdinalIgnoreCase) >= 0;
			quests.Handlers.Add(new QuestHandler(root, new Sequence($"{root}: poison the tree",
				new Activate("Tree Roots") { Where = Wetlands, DoneWhen = poisoned },
				new TakeTransition(VaalRuins) { Where = Wetlands })) {
				// (a "Travel to The Vaal Ruins" too is the handler's: the way is the roots' new exit, not a known route)
				Active = step => QuestHandler.DefaultActive(step) || poisoned(),
			});
			quests.Handlers.Add(Stub("Shadow of the Vaal"));
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("Einhar's Hunt"));
			quests.Handlers.Add(Stub("Einhar's Menagerie"));
			quests.Handlers.Add(Stub("Safe and Sound"));
		}

		private void Act3Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("Lost in Love"));
			quests.Handlers.Add(Stub("Sever the Right Hand"));
			quests.Handlers.Add(Stub("The Gemling Queen"));
			quests.Handlers.Add(Stub("Victario's Secrets"));
			quests.Handlers.Add(Stub("The Ribbon Spool"));
			quests.Handlers.Add(Stub("Fiery Dust"));
			quests.Handlers.Add(Stub("A Swig of Hope"));
			quests.Handlers.Add(Stub("Piety's Pets"));
			quests.Handlers.Add(Stub("A Fixture of Fate"));
			quests.Handlers.Add(Stub("Sceptre of God"));
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("Strange Growths"));
		}

		private void Act4Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("Breaking the Seal"));
			quests.Handlers.Add(Stub("An Indomitable Spirit"));
			quests.Handlers.Add(Stub("The King of Fury"));
			quests.Handlers.Add(Stub("The King of Desire"));
			quests.Handlers.Add(Stub("Corpus Malachus"));
			quests.Handlers.Add(Stub("The Eternal Nightmare"));
			quests.Handlers.Add(Stub("Niko's Fuel"));
			quests.Handlers.Add(Stub("Niko's Mine"));
			quests.Handlers.Add(Stub("Niko's Explosives"));
		}

		private void Act5Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("Return to Oriath"));
			quests.Handlers.Add(Stub("The Key to Freedom"));
			quests.Handlers.Add(Stub("In Service to Science"));
			quests.Handlers.Add(Stub("Death to Purity"));
			quests.Handlers.Add(Stub("The King's Feast"));
			quests.Handlers.Add(Stub("Kitava's Torments"));
			quests.Handlers.Add(Stub("The Ravenous God"));
		}

		private void Act6Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("Fallen from Grace"));
			quests.Handlers.Add(Stub("Bestel's Epic"));
			quests.Handlers.Add(Stub("The Father of War"));
			quests.Handlers.Add(Stub("Essence of Umbra"));
			quests.Handlers.Add(Stub("The Cloven One"));
			quests.Handlers.Add(Stub("The Puppet Mistress"));
			quests.Handlers.Add(Stub("The Brine King"));
			quests.Handlers.Add(Stub("The Rogue Harbour"));
			quests.Handlers.Add(Stub("The Lost Expedition"));
		}

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

		private void Act9Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("The Storm Blade"));
			quests.Handlers.Add(Stub("Fastis Fortuna"));
			quests.Handlers.Add(Stub("The Ruler of Highgate"));
			quests.Handlers.Add(Stub("Queen of the Sands"));
			quests.Handlers.Add(Stub("Recurring Nightmare"));
			quests.Handlers.Add(Stub("The Immortal Syndicate"));
		}

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
