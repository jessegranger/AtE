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
		private const string CraftingRecipe = "Crafting Recipe";
		/// <summary>How long Piety must be gone (or not targetable) after the fight in The Crematorium to count as teleported away.</summary>
		private const long PietyGoneMs = 3000;

		/// <summary>The Vaal Oversoul (Metadata/Monsters/IncaShadowBoss/..., not its spawner, the Dark Altar).</summary>
		private static bool IsVaalOversoul(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& ((path.IndexOf("/IncaShadowBoss/", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf("Spawner", StringComparison.OrdinalIgnoreCase) < 0)
				|| (e.GetComponent<Render>()?.Name?.Contains("Vaal Oversoul", StringComparison.OrdinalIgnoreCase) ?? false));

		/// <summary>True once no label with text in it has been on the ground for ms (counted from when it is first asked).</summary>
		private static Func<bool> NoLabelFor(string text, long ms) {
			long since = -1;
			return () => {
				bool shown = GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => (Ground.LabelText(l.Label)?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) ?? false;
				if ( shown ) { since = -1; return false; }
				if ( since < 0 ) since = Time.ElapsedMilliseconds;
				return Time.ElapsedMilliseconds - since > ms;
			};
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

		private void Act3Quests(FinishAllQuests quests) {
			Func<string> main = BuildPlan.MainSkill;
			// The City of Sarn's way to town: the exit on the RuinedCity/AreaTransitions/act3_docks_to_town_lower tile (3.29.4)
			// (done in town, or in any of later, the areas past it)
			Goal ToSarnEncampment(params string[] later) => new TakeTransition(SarnEncampment) { Where = CityOfSarn, SearchAt = Search.Exit(SarnEncampment, "/act3_docks_to_town_lower"),
				DoneWhen = () => InAny(TownOf(3)) || InAny(later) };
			// Seeing Clarissa in The City of Sarn gives it (before Shadow of the Vaal's step there: it comes first). "Defeat the
			// guard and rescue the girl": kill the Guard Captain (Metadata/Monsters/Axis/GuardLieutenantClarissa, a unique,
			// with Guards around him, by Clarissa: seen live, 3.29.4, at <564, 1298>, Clarissa, Metadata/NPC/Act3/ClarissaSlums,
			// at <560, 1278>), then talk to her once she can be targeted.
			// "Find Tolman in the Crematorium": on to town, from town to The Slums, from The Slums through the exit to The
			// Crematorium (on the AreaTransitions/act3_prison_entrance tile). There, toward the Terrain/quest_marker tile until
			// Piety (Axis/PietyEscape) shows; wait for her to be targetable, and fight her (Orbit) until she teleports away
			// (gone, or not targetable any more, for a few seconds). Then click "Tolman", pick up Tolman's Bracelet, and collect
			// a Crafting Recipe if one shows near.
			// "Travel to the Sarn Encampment" (once the Crematorium's goals are done): a trip (by a Portal Scroll, see
			// FinishAllQuests). "Talk to Clarissa" (she gives the Sewer Keys) and "Talk to Maramoa for your reward" (a gem):
			// talks in town (see TakeQuestRewards).
			// TODO: confirm live (not seen yet): the exits' labels, Piety's teleport, Tolman's label, the Sewer Keys as a reward
			const string love = "Lost in Love";
			bool rescueStep() => StepOf(love)?.StartsWith("Defeat the guard", StringComparison.OrdinalIgnoreCase) ?? false;
			bool tolmanStep() => StepOf(love)?.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase) ?? false;
			bool isPiety(Entity e) => e?.Path?.IndexOf("Axis/PietyEscape", StringComparison.OrdinalIgnoreCase) >= 0;
			// Piety gone: seen targetable (fought), then not there or not targetable for PietyGoneMs (latched)
			bool pietyFought = false, pietyGone = false;
			long pietyLostAt = 0;
			bool PietyGone() {
				if ( pietyGone ) return true;
				var piety = GetEntities()?.FirstOrDefault(e => e != null && isPiety(e));
				bool targetable = piety != null && IsAlive(piety) && (piety.GetComponent<Targetable>()?.IsTargetable ?? false);
				if ( targetable ) { pietyFought = true; pietyLostAt = 0; return false; }
				// (after a restart past the fight: Tolman's label shows, and no Piety in range)
				bool tolman = GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), "Tolman", StringComparison.OrdinalIgnoreCase)) ?? false;
				if ( !pietyFought && !(tolman && piety == null) ) return false;
				if ( pietyLostAt == 0 ) pietyLostAt = Time.ElapsedMilliseconds;
				if ( Time.ElapsedMilliseconds - pietyLostAt < PietyGoneMs ) return false;
				Log($"Campaign: {love}: Piety is gone ({(pietyFought ? "after the fight" : "not in range, and Tolman's label shows")})");
				return pietyGone = true;
			}
			var questMarker = Search.Tile("/quest_marker");
			var recipe = new PickUp(CraftingRecipe) { Where = Crematorium, SearchAt = questMarker, DoneWhen = NoLabelFor(CraftingRecipe, 5000) };
			var clarissaNpc = TalkTo.NpcNamed("Clarissa");
			bool clarissa(Entity e) => clarissaNpc(e) && (e.GetComponent<Targetable>()?.IsTargetable ?? false);
			bool clarissaFree() => GetEntities()?.Any(e => e != null && clarissa(e)) ?? false;
			bool isCaptain(Entity e) => e?.Path?.StartsWith("Metadata/Monsters/Axis/GuardLieutenantClarissa", StringComparison.OrdinalIgnoreCase) ?? false;
			// (look for him by her, if she is in range)
			Offsets.Vector2i? byClarissa(CombatContext ctx) => GetEntities()?.FirstOrDefault(e => e != null && clarissaNpc(e))?.GetComponent<Positioned>()?.GridPos;
			quests.Handlers.Add(new QuestHandler(love, new Sequence(love,
				new Orbit("the Guard Captain", isCaptain, main) { Where = CityOfSarn, SearchAt = byClarissa, DoneWhen = () => !rescueStep() || clarissaFree() },
				new TalkTo("Clarissa", clarissa) { Where = CityOfSarn, SearchAt = byClarissa, DoneWhen = () => !rescueStep() },
				ToSarnEncampment(Slums, Crematorium),
				new EnterArea(Slums) { Where = TownOf(3), DoneWhen = () => InAny(Slums, Crematorium) },
				new TakeTransition(Crematorium) { Where = Slums, SearchAt = Search.Exit(Crematorium, "/act3_prison_entrance"), DoneWhen = () => InAny(Crematorium) },
				new Orbit("Piety", isPiety, main) { Where = Crematorium, SearchAt = questMarker, DoneWhen = () => PietyGone() || !tolmanStep() },
				new Activate("Tolman") { Where = Crematorium, SearchAt = questMarker, DoneWhen = () => !tolmanStep() },
				new PickUpQuestItem("Tolman's Bracelet") { Where = Crematorium },
				recipe)) {
				Active = step => step != null && (
					(InAny(CityOfSarn) && (step.StartsWith("Defeat the guard", StringComparison.OrdinalIgnoreCase) || step.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase)))
					|| (InAny(TownOf(3), Slums) && step.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase))
					// (the step may move on to the trip to town before the bracelet and the recipe are had)
					|| (InAny(Crematorium) && (step.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase)
						|| (step.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) && !recipe.IsComplete)))),
			});
			// Act 2's Shadow of the Vaal, on in The City of Sarn: "Find the Sarn encampment". Collect a Crafting Recipe if one
			// is in sight on the way (looking for a moment, toward the exit), then on to town.
			const string vaal = "Shadow of the Vaal";
			quests.Handlers.Add(new QuestHandler(vaal, new Sequence($"{vaal}: the Sarn encampment",
				new PickUp(CraftingRecipe) { Where = CityOfSarn, SearchAt = Search.Exit(SarnEncampment, "/act3_docks_to_town_lower"), DoneWhen = NoLabelFor(CraftingRecipe, 5000) },
				ToSarnEncampment())) {
				Active = step => step != null && InAny(CityOfSarn) && step.StartsWith("Find the Sarn encampment", StringComparison.OrdinalIgnoreCase),
			});
			SeverTheRightHand(quests);
			quests.Handlers.Add(Stub("The Gemling Queen"));
			quests.Handlers.Add(Stub("Victario's Secrets"));
			quests.Handlers.Add(Stub("The Ribbon Spool"));
			quests.Handlers.Add(Stub("Fiery Dust"));
			quests.Handlers.Add(Stub("A Swig of Hope"));
			PietysPets(quests);
			quests.Handlers.Add(Stub("A Fixture of Fate"));
			SceptreOfGodQuest(quests);
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("Strange Growths"));
		}

		/// <summary>
		/// Sever the Right Hand. Its step "Travel to The Ebony Barracks" means the way there (through The Sewers) is blocked,
		/// and opening it is a chain of quest items, each got and then used, in order: Tolman's Bracelet (The Crematorium,
		/// see Lost in Love), given to Clarissa for the Sewer Keys; the Sewer Keys, used on the Sewer Grating in The Slums
		/// (the Transitions/slum_sewer_entrance tile); the Thaumetic Sulphite (a chest on The Docks' templeruinforest_questcart
		/// tile), given to Lady Dialla in The Solaris Temple Level 2 (by the Features/gemling_queen_throne tile) for the
		/// Infernal Talc; the Infernal Talc, used on the Undying Blockage in The Sewers (by its exit to The Ebony Barracks, the
		/// Features/sewerexit tile). Which item is in the backpack (Equipment.HasQuestItem) says which step is next. With none
		/// of them: the sulphite, if The Ebony Barracks' waypoint is locked and The Battlefront's unlocked; else (the talc
		/// used) the step is a trip to The Ebony Barracks (FinishAllQuests). There, "Kill General Gravicius" (by the
		/// Floor/temple_carpet_oneside tile), with AvoidSpam; then the town's talks.
		/// TODO: confirm live (not seen yet): the items' names, Dialla's name and that a talk gives the talc, the blockage's
		/// label, Gravicius's name
		/// </summary>
		private void SeverTheRightHand(FinishAllQuests quests) {
			const string sever = "Sever the Right Hand";
			bool travelStep(string step) => step != null && step.StartsWith("Travel to the Ebony Barracks", StringComparison.OrdinalIgnoreCase);
			bool has(string item) => Equipment.HasQuestItem(item);
			// with none of the items: the sulphite is next while The Ebony Barracks' waypoint is locked and The Battlefront's
			// is unlocked (the way on to The Docks is open: the grating is behind); else nothing here (eg, the talc used:
			// the trip to The Ebony Barracks)
			bool sulphiteNext() => WaypointPanel.IsKnownLocked(EbonyBarracks) && WaypointPanel.IsKnownUnlocked("The Battlefront");
			// to an area, done only while there (so it is gone to again, eg after a trip to town)
			Goal To(string area) => new EnterArea(area) { NotDoneWhile = () => !InAny(area) };
			// (active only while it has something left to do: else the step is a trip, as any other)
			void Add(string what, Func<bool> when, params Goal[] goals) {
				var seq = new Sequence($"{sever}: {what}", goals);
				quests.Handlers.Add(new QuestHandler(sever, seq) {
					Active = step => travelStep(step) && when() && CampaignState.Context is CombatContext ctx && seq.Select(ctx) != null,
				});
			}
			Add("the Infernal Talc on the Undying Blockage", () => has("Infernal Talc"),
				To(Sewers),
				new Activate("Undying Blockage") { Where = Sewers, SearchAt = Search.Tile("/sewerexit"), DoneWhen = () => !has("Infernal Talc") });
			Add("the Thaumetic Sulphite to Lady Dialla", () => !has("Infernal Talc") && has("Thaumetic Sulphite"),
				To(SolarisTemple2),
				new TalkTo("Lady Dialla", TalkTo.NpcNamed("Dialla")) { Where = SolarisTemple2, SearchAt = Search.Tile("/gemling_queen_throne"), DoneWhen = () => has("Infernal Talc") });
			Add("the Sewer Keys on the Sewer Grating", () => !has("Infernal Talc") && !has("Thaumetic Sulphite") && has("Sewer Keys"),
				To(Slums),
				new TakeTransition("Sewer Grating") { Where = Slums, SearchAt = Search.Tile("/slum_sewer_entrance"), DoneWhen = () => !has("Sewer Keys") });
			Add("the Thaumetic Sulphite", () => !has("Infernal Talc") && !has("Thaumetic Sulphite") && !has("Sewer Keys") && !has("Tolman's Bracelet") && sulphiteNext(),
				To(Docks),
				new OpenChest("the chest with the Thaumetic Sulphite") { Where = Docks, SearchAt = Search.Tile("/templeruinforest_questcart"), DoneWhen = () => has("Thaumetic Sulphite") },
				new PickUpQuestItem("the Thaumetic Sulphite") { Where = Docks, DoneWhen = () => has("Thaumetic Sulphite") });
			// "Kill General Gravicius", in The Ebony Barracks
			var gravicius = MonsterNamed("Gravicius");
			quests.Handlers.Add(new QuestHandler(sever, new Sequence($"{sever}: kill General Gravicius",
				new AvoidSpam("General Gravicius", gravicius, BuildPlan.MainSkill) { Where = EbonyBarracks, SearchAt = Search.Tile("/temple_carpet_oneside") })) {
				Active = step => IsKillStep(step, "General Gravicius"),
			});
		}

		/// <summary>
		/// Piety's Pets. Done once /passives names it (its reward is a Book of Skill: see QuestHandler.IsActive). Its step is
		/// "Find Piety" until her arena is entered; after that the step does not matter: in The Lunaris Temple Level 2, with
		/// Piety there, she is to kill. Through the "Portal" to her arena (by the Features/templeclean_prepiety_roundtop_center
		/// tile), wait for her to turn hostile (Kill waits while she is not targetable), and kill her (Kill: a plain fight,
		/// not Orbit or AvoidSpam). She drops the Tower Key: pick it up, and collect a Crafting Recipe if one shows near.
		/// Then, with the Tower Key (it is kept: The Sceptre of God's door takes it) and no passive point for the quest,
		/// back to The Sarn Encampment to talk to Grigor for the Book of Skill (UseBooks reads it).
		/// TODO: confirm live (not seen yet): Piety's monster (path, name), the Tower Key's name, that Grigor's talk gives the book
		/// </summary>
		private void PietysPets(FinishAllQuests quests) {
			const string pets = "Piety's Pets";
			Func<string> main = BuildPlan.MainSkill;
			bool hasKey() => Equipment.HasQuestItem("Tower Key");
			bool keyOnGround() => GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => (Ground.LabelText(l.Label)?.IndexOf("Tower Key", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) ?? false;
			bool named(Entity e) => string.Equals(e?.GetComponent<Render>()?.Name?.Trim(), "Piety", StringComparison.OrdinalIgnoreCase);
			bool pietyMonster(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/") && named(e);
			bool inSight() => GetEntities()?.Any(e => e != null && pietyMonster(e)) ?? false;
			bool killed() => hasKey() || keyOnGround();
			var arena = Search.Tile("/templeclean_prepiety_roundtop_center");
			var recipe = new PickUp(CraftingRecipe) { Where = LunarisTemple2, SearchAt = arena, DoneWhen = NoLabelFor(CraftingRecipe, 5000) };
			quests.Handlers.Add(new QuestHandler(pets, new Sequence($"{pets}: kill Piety",
				new TakeTransition("Portal") { Where = LunarisTemple2, SearchAt = arena, DoneWhen = () => inSight() || killed() },
				new Kill("Piety", pietyMonster, main) { Where = LunarisTemple2, SearchAt = arena, DoneWhen = killed },
				new PickUpQuestItem("the Tower Key") { Where = LunarisTemple2, DoneWhen = hasKey },
				recipe)) {
				// ("Find Piety", without the key; and in The Lunaris Temple Level 2, until the recipe is looked for, whatever the
				// step: it changes once her arena is entered)
				Active = step => (step != null && !hasKey() && step.StartsWith("Find Piety", StringComparison.OrdinalIgnoreCase))
					|| (InAny(LunarisTemple2) && !recipe.IsComplete),
				Untracked = () => InAny(LunarisTemple2) && !recipe.IsComplete,
			});
			// the Tower Key, and no passive point for the quest (else this handler is not active): Grigor has the Book of Skill
			var grigor = new TalkTo("Grigor", TalkTo.NpcNamed("Grigor")) { Where = TownOf(3), MaxTalks = 2,
				DoneWhen = () => BackpackItems().Any(i => UseBooks.IsBook(i.Entity)) };
			quests.Handlers.Add(new QuestHandler(pets, new Sequence($"{pets}: the Book of Skill from Grigor",
				new UsePortalScroll(TownOf(3)) { DoneWhen = () => InAny(TownOf(3)) || !UsePortalScroll.CanTry(CampaignState.Context?.Driver) },
				grigor) { DoneWhen = () => grigor.IsComplete }) {
				Active = _ => hasKey() && !grigor.IsComplete,
				Untracked = () => hasKey() && !grigor.IsComplete,
			});
		}

		/// <summary>
		/// Sceptre of God, the end of Act 3: done once Act 4 is reached (CampaignProgress.ActDone(3): The Aqueduct is
		/// Act 4's, and the waypoint panel only tells of the current act). It begins with the Tower Key (Piety's Pets).
		/// "Travel to the Imperial Gardens": the area is the first goal's (TravelTo: by the waypoint in The Ebony Barracks,
		/// and walk). "Unlock the Sceptre of God": by the Transitions/Act3_EpicDoor tile, click the "Locked Door" (it takes
		/// the Tower Key). "Travel to The Sceptre of God": the exit on that tile. "Ascend the Sceptre of God": up the
		/// Features/tower_transition_up stairs (labelled "Stairs"; the last, "The Upper Sceptre of God", an exit to that
		/// area), each floor cut off from the others, so the next stairs are the ones that can be walked to (ClimbStairs).
		/// In The Upper Sceptre of God, two more "Stairs", until the Features/tower_totowertop tile can be walked to: take
		/// its "Tower Rooftop" to Dominus's arena (the Tower/Act3_tower tile can be walked to). There, fight the hostiles
		/// (with none, wait on the Sceptre/Blank_invisible tile for them to wake up) until the exit to The Aqueduct is
		/// targetable, and take it.
		/// TODO: confirm live (not seen yet): the quest's name in the tracker, the exit labels "The Sceptre of God" and
		/// "The Aqueduct"
		/// </summary>
		private void SceptreOfGodQuest(FinishAllQuests quests) {
			const string sceptre = "Sceptre of God";
			Func<string> main = BuildPlan.MainSkill;
			bool done() => CampaignProgress.ActDone(3);
			bool hasKey() => Equipment.HasQuestItem("Tower Key");
			// (a tile of the part that can be walked to from the player, in this frame's terrain)
			bool reachable(string tilePathPart) => CampaignState.Context is CombatContext ctx && ctx.Terrain != null
				&& ctx.Terrain.TileFeatures(tilePathPart).Any(f => DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, f.GridPos, out _, 25));
			bool inArena() => InAny(UpperSceptreOfGod) && reachable("Tower/Act3_tower");
			// the exit to The Aqueduct targetable: Dominus is dead
			bool aqueductOpen() => GetEntities()?.Any(e => IsValid(e) && IsValid(e.GetComponent<AreaTransition>())
				&& string.Equals(e.GetComponent<Render>()?.Name, Aqueduct, StringComparison.OrdinalIgnoreCase)
				&& (e.GetComponent<Targetable>()?.IsTargetable ?? false)) ?? false;
			bool stepIs(string prefix) => StepOf(sceptre)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;
			bool unlocked() => InAny(SceptreOfGod, UpperSceptreOfGod) || stepIs("Travel to The Sceptre of God") || stepIs("Ascend");
			var epicDoor = Search.Tile("/Act3_EpicDoor");
			quests.Handlers.Add(new QuestHandler(sceptre, new Sequence(sceptre,
				new Activate("Locked Door") { Where = ImperialGardens, SearchAt = epicDoor, DoneWhen = unlocked },
				new TakeTransition(SceptreOfGod) { Where = ImperialGardens, SearchAt = epicDoor, DoneWhen = () => InAny(SceptreOfGod, UpperSceptreOfGod) },
				new ClimbStairs(SceptreOfGod, "/tower_transition_up", "/tower_totowertop", "Stairs", UpperSceptreOfGod) { Where = SceptreOfGod,
					DoneWhen = () => InAny(UpperSceptreOfGod) },
				new ClimbStairs(UpperSceptreOfGod, "/tower_transition_up", "/tower_totowertop", "Stairs") { Where = UpperSceptreOfGod, DoneWhen = inArena },
				new TakeTransition("Tower Rooftop") { Where = UpperSceptreOfGod, SearchAt = Search.Tile("/tower_totowertop"), DoneWhen = inArena },
				new FightUntil("Dominus's arena", aqueductOpen, main) { Where = UpperSceptreOfGod, SearchAt = Search.Tile("Sceptre/Blank_invisible") },
				new TakeTransition(Aqueduct) { Where = UpperSceptreOfGod, DoneWhen = done }) { DoneWhen = done }) {
				// (any step but a reward, until Act 4; "Travel to the Imperial Gardens" too: the first goal's Where goes there)
				Active = step => step != null && !done() && !step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase),
				// (not in the tracker, eg under another name: with the Tower Key, or in the Sceptre's areas)
				Untracked = () => !done() && (hasKey() || InAny(SceptreOfGod, UpperSceptreOfGod)),
			});
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
