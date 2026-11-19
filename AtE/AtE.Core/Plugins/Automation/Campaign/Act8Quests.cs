using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 8's quests, as tables of rules (FirstOf, as in Act4Quests): Essence of the Hag (its long step "Talk to Hargan
	/// for your Reward" covers Doedre, Love is Dead, The Gemling Legion and the Sun Orb), The Wings of Vastiri (Hector
	/// Titucius, then Yugul: Reflection of Terror) and Lunar Eclipse (the Moon Orb, Dusk, and the Sky Shrine).
	/// TODO: confirm live (not seen yet): the labels, names and tiles here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string SarnEncampment8 = "The Sarn Encampment", ToxicConduits = "The Toxic Conduits", DoedresCesspool = "Doedre's Cesspool",
			Quay = "The Quay", GrainGate = "The Grain Gate", ImperialFields = "The Imperial Fields", SolarisTemple1_8 = "The Solaris Temple Level 1",
			SolarisTemple2_8 = "The Solaris Temple Level 2", SolarisConcourse = "The Solaris Concourse", HarbourBridge = "The Harbour Bridge",
			LunarisConcourse = "The Lunaris Concourse", BathHouse = "The Bath House", HighGardens = "The High Gardens",
			LunarisTemple1_8 = "The Lunaris Temple Level 1", LunarisTemple2_8 = "The Lunaris Temple Level 2", BloodAqueduct = "The Blood Aqueduct";
		private const string EssenceOfTheHag = "Essence of the Hag", LoveIsDead = "Love is Dead", GemlingLegion = "The Gemling Legion",
			ReflectionOfTerror = "Reflection of Terror", WingsOfVastiri = "The Wings of Vastiri", LunarEclipse = "Lunar Eclipse",
			SolarEclipse = "Solar Eclipse";

		private void Act8Quests(FinishAllQuests quests) {
			EssenceOfTheHagQuest(quests);
			// (Clarissa, Tolman and the Gemling Legion are on Essence of the Hag's way; their rewards are still taken)
			var love = QuestHandler.Elsewhere(LoveIsDead, EssenceOfTheHag);
			love.FieldNpcs = new[] { "Clarissa" };
			quests.Handlers.Add(love);
			quests.Handlers.Add(QuestHandler.Elsewhere(GemlingLegion, EssenceOfTheHag));
			// (Yugul is on The Wings of Vastiri's way)
			quests.Handlers.Add(QuestHandler.Elsewhere(ReflectionOfTerror, WingsOfVastiri));
			WingsOfVastiriQuest(quests);
			LunarEclipseQuest(quests);
			// (the Sun Orb, from Dawn, is on Essence of the Hag's way)
			quests.Handlers.Add(QuestHandler.Elsewhere(SolarEclipse, EssenceOfTheHag));
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
		}

		// ---- helpers for the rules ----

		/// <summary>
		/// A boss in an arena behind a door (eg, Dawn, through the "Portal" in The Solaris Temple Level 2), three rules in
		/// area while when holds: kill the boss (AvoidSpam) once it (alive) can be walked to; once through the door, fight
		/// what is there until it can, or it is seen dead; else through the door (done once the boss can be walked to).
		/// </summary>
		private static void BossArena(FirstOf rules, string area, Func<bool> when, string bossName, Func<Entity, bool> boss, TakeTransition door) {
			Func<string> main = BuildPlan.MainSkill;
			bool alive(Entity e) => boss(e) && IsAlive(e);
			string arena = door.LabelText ?? door.What;
			door.Where = area;
			door.DoneWhen = () => CanReach(boss);
			rules.When($"kill {bossName}", area, () => when() && CanReach(alive), new AvoidSpam(bossName, boss, main) { Where = area });
			rules.When($"the rest of {bossName}'s fight", area, () => when() && door.Taken && !SeenDead(boss),
				new FightUntil(arena, () => !when() || CanReach(alive) || SeenDead(boss), main) { Where = area });
			rules.When($"into {arena}", area, () => when() && !SeenDead(boss), door);
		}

		/// <summary>A latch: true once f has been true (this run).</summary>
		private static Func<bool> Latch(Func<bool> f) {
			bool seen = false;
			return () => seen = seen || f();
		}

		/// <summary>
		/// Essence of the Hag (the user's table):
		///  - The Sarn Ramparts, "Meet Sin at the Sarn Encampment": up the "Stairs" (ramparts_wall_accesss_v01_01) until the
		///    act8_docks_v01_01 tile can be walked to, and take its exit to The Sarn Encampment
		///  - "Travel to the Sewers": to The Toxic Conduits; "Kill Doedre": to Doedre's Cesspool (sewerwall_thin_entrance_v02_02_blocked);
		///    there, by the aqueduct_sewer_entrance_v01_01 tile, the "Loose Grate" into "The Cauldron"; click the "Valve", wait for
		///    Doedre, and kill her (AvoidSpam)
		///  - "Talk to Hargan for your Reward" (the step all the way to the Sun Orb), by area:
		///     - Doedre's Cesspool: out of The Cauldron by the "Sewer Outlet"; to The Quay (sewerwall_exit_v01_01)
		///     - The Quay: without the Ankh of Eternity, toward quay_dock_A_convex_v01_01 until the QuestObject icon whose entity
		///       is an /AnkhChest is in range, open it, and pick up the Ankh; with it, into the "Resurrection Site"
		///       (market_thinwall_T_v01_01): talk to Clarissa, fight the Sleepy Dead until Tolman shows, kill him (AvoidSpam),
		///       talk to Clarissa, and out by "The Quay". Once Love is Dead is at its reward, to The Grain Gate (Act3_Largecliff_v02_01)
		///     - The Grain Gate: to the waypoint; with no Gemling Legion yet, toward warehouse_transition_slums_v01_01 (and
		///       fight there while it is on); once it is at its reward, to The Imperial Fields (act8_grain_gate_transition_v01_01)
		///     - The Imperial Fields: to the waypoint; to The Solaris Temple Level 1 (act8_temple_entrance_v01_01)
		///     - The Solaris Temple Level 1: to the waypoint, and the exit to Level 2 near it
		///     - The Solaris Temple Level 2: without the Sun Orb, through the "Portal" (gemling_queen_throne_p2_v01_01), kill Dawn
		///       (AvoidSpam), and pick up the Sun Orb; with it, back to town
		/// TODO: Doedre's, Tolman's and Dawn's effects (AvoidSpam's hazards); confirm live: the Sleepy Dead, the Ankh chest's icon,
		/// Love is Dead's steps
		/// </summary>
		private void EssenceOfTheHagQuest(FinishAllQuests quests) {
			const string eh = EssenceOfTheHag;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(8);
			bool at(string prefix) => StepStarts(eh, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			bool hargan() => AtReward(eh);
			var rules = new FirstOf(eh);

			// The Sarn Ramparts
			bool meetSin() => at("Meet Sin");
			const string docks = "act8_docks_v01_01";
			rules.When("to The Sarn Encampment", SarnRamparts, () => meetSin() && TileReachable(docks),
				new TakeTransition(SarnEncampment8) { Where = SarnRamparts, SearchAt = Search.Exit(SarnEncampment8, docks) });
			rules.When("up the Stairs", SarnRamparts, meetSin, new ClimbStairs(SarnRamparts, "ramparts_wall_accesss_v01_01", docks, "Stairs") { Where = SarnRamparts });

			// to the sewers, and Doedre (TODO: her effects, for AvoidSpam)
			rules.When("to The Toxic Conduits", town, () => at("Travel to the Sewers"), new TakeTransition(ToxicConduits) { Where = town });
			bool kill() => at("Kill Doedre");
			rules.When("to Doedre's Cesspool", ToxicConduits, kill,
				new TakeTransition(DoedresCesspool) { Where = ToxicConduits, SearchAt = Search.Exit(DoedresCesspool, "sewerwall_thin_entrance_v02_02_blocked") });
			var doedre = UniqueNamed("Doedre");
			bool doedreAlive(Entity e) => doedre(e) && IsAlive(e);
			var grate = Search.Tile("aqueduct_sewer_entrance_v01_01");
			var cauldronDoor = new TakeTransition("The Cauldron") { Where = DoedresCesspool, SearchAt = grate };
			var outlet = new TakeTransition("Sewer Outlet") { Where = DoedresCesspool };
			// in The Cauldron: its door taken, its Valve in sight, or Doedre can be walked to; until the Sewer Outlet is taken
			bool inCauldron = false;
			bool InCauldron() => inCauldron = (inCauldron || cauldronDoor.Taken || TargetableLabelShown("Valve") || CanReach(doedre)) && !outlet.Taken;
			rules.When("kill Doedre", DoedresCesspool, () => kill() && CanReach(doedreAlive), new AvoidSpam("Doedre the Vile", doedre, main) { Where = DoedresCesspool });
			rules.When("turn the Valve", DoedresCesspool, () => kill() && InCauldron() && TargetableLabelShown("Valve"),
				new Activate("Valve") { Where = DoedresCesspool, DoneWhen = () => CanReach(doedreAlive) });
			rules.When("wait for Doedre", DoedresCesspool, () => kill() && InCauldron(),
				new FightUntil("The Cauldron", () => !kill() || CanReach(doedreAlive), main) { Where = DoedresCesspool });
			rules.When("into The Cauldron", DoedresCesspool, () => kill() && TargetableLabelShown("The Cauldron"), cauldronDoor);
			rules.When("open the Loose Grate", DoedresCesspool, kill,
				new Activate("Loose Grate") { Where = DoedresCesspool, SearchAt = grate, DoneWhen = () => TargetableLabelShown("The Cauldron") || InCauldron() });
			rules.When("out by the Sewer Outlet", DoedresCesspool, () => hargan() && InCauldron(), outlet);
			rules.When("to The Quay", DoedresCesspool, hargan,
				new TakeTransition(Quay) { Where = DoedresCesspool, SearchAt = Search.Exit(Quay, "sewerwall_exit_v01_01") });

			// The Quay: the Ankh of Eternity, Love is Dead (TODO: Tolman's effects, for AvoidSpam)
			bool loveDone() => AtReward(LoveIsDead) || StepStarts(LoveIsDead, "Talk to Clarissa in town") || QuestFinished(LoveIsDead);
			const string ankh = "Ankh of Eternity";
			var clarissa = TalkTo.NpcNamed("Clarissa");
			var tolman = UniqueNamed("Tolman");
			bool tolmanAlive(Entity e) => tolman(e) && IsAlive(e);
			var tolmanDead = Latch(() => SeenDead(tolman));
			var siteDoor = new TakeTransition("Resurrection Site") { Where = Quay, SearchAt = Search.Tile("market_thinwall_T_v01_01"), DoneWhen = () => CanReach(clarissa) };
			var siteOut = new TakeTransition(Quay) { Where = Quay };
			bool inSite = false;
			bool InSite() => inSite = (inSite || siteDoor.Taken || CanReach(clarissa)) && !siteOut.Taken;
			// (the fight started: Tolman, or any hostile, near)
			var fightStarted = Latch(() => CanReach(tolman) || (CampaignState.Context is CombatContext c && Fight.AnyNear(c, 60)));
			rules.When("out of the Resurrection Site", Quay, () => hargan() && loveDone() && InSite(), siteOut);
			rules.When("to The Grain Gate", Quay, () => hargan() && loveDone(),
				new TakeTransition(GrainGate) { Where = Quay, SearchAt = Search.Exit(GrainGate, "Act3_Largecliff_v02_01") });
			rules.When("talk to Clarissa (Tolman dead)", Quay, () => hargan() && InSite() && tolmanDead(),
				new TalkTo("Clarissa", clarissa) { Where = Quay, DoneWhen = loveDone });
			rules.When("kill Tolman", Quay, () => hargan() && InSite() && CanReach(tolmanAlive), new AvoidSpam("Tolman", tolman, main) { Where = Quay });
			rules.When("talk to Clarissa", Quay, () => hargan() && InSite() && !fightStarted(),
				new TalkTo("Clarissa", clarissa) { Where = Quay, MaxTalks = 1, DoneWhen = fightStarted });
			rules.When("fight the Sleepy Dead until Tolman shows", Quay, () => hargan() && InSite(),
				new FightUntil("the Resurrection Site", () => CanReach(tolmanAlive) || tolmanDead() || loveDone(), main) { Where = Quay });
			rules.When("into the Resurrection Site", Quay, () => hargan() && has(ankh), siteDoor);
			Entity ankhChest() => MinimapIcons.QuestObjects().Select(m => m.Entity)
				.FirstOrDefault(e => e?.Path?.IndexOf("/AnkhChest", StringComparison.OrdinalIgnoreCase) >= 0);
			bool ankhOpened() => ankhChest() is Entity e && (e.GetComponent<Chest>()?.IsOpened ?? false);
			Offsets.Vector2i? ankhAt(CombatContext c) => ankhChest()?.GetComponent<Positioned>()?.GridPos;
			bool wantAnkh() => hargan() && !has(ankh);
			rules.When("pick up the Ankh of Eternity", Quay, () => wantAnkh() && ItemShown(ankh), new PickUp(ankh) { Where = Quay });
			rules.When("open the Ankh chest", Quay, () => wantAnkh() && ankhChest() != null && !ankhOpened(),
				new OpenChest("the Ankh chest") { Where = Quay, SearchAt = ankhAt, Radius = 10, DoneWhen = ankhOpened });
			rules.When("pick up the Ankh of Eternity", Quay, () => wantAnkh() && ankhOpened(), new PickUpQuestItem(ankh) { Where = Quay });
			rules.When("toward the Ankh chest", Quay, wantAnkh,
				new ApproachTiles("the Ankh chest", "quay_dock_A_convex_v01_01") { Where = Quay, Reach = 20, DoneWhen = () => ankhChest() != null });

			// The Grain Gate: the Gemling Legion
			bool legionDone() => AtReward(GemlingLegion) || StepStarts(GemlingLegion, "Talk to Clarissa") || QuestFinished(GemlingLegion);
			var gateWp = WaypointKnownLatch(GrainGate);
			var warehouse = Search.Tile("warehouse_transition_slums_v01_01");
			rules.When("to the waypoint (to discover it)", GrainGate, () => hargan() && !gateWp(), new GoToWaypoint() { Where = GrainGate, DoneWhen = gateWp });
			rules.When("to The Imperial Fields", GrainGate, () => hargan() && legionDone(),
				new TakeTransition(ImperialFields) { Where = GrainGate, SearchAt = Search.Exit(ImperialFields, "act8_grain_gate_transition_v01_01") });
			rules.When("fight the Gemling Legion", GrainGate, () => hargan() && StepOf(GemlingLegion) != null,
				new FightUntil("the Gemling Legion", legionDone, main) { Where = GrainGate, SearchAt = warehouse });
			rules.When("toward the Gemling Legion", GrainGate, hargan,
				new ApproachTiles("the Gemling Legion", "warehouse_transition_slums_v01_01") { Where = GrainGate, DoneWhen = () => StepOf(GemlingLegion) != null });

			// The Imperial Fields, The Solaris Temple
			var fieldsWp = WaypointKnownLatch(ImperialFields);
			rules.When("to the waypoint (to discover it)", ImperialFields, () => hargan() && !fieldsWp(), new GoToWaypoint() { Where = ImperialFields, DoneWhen = fieldsWp });
			rules.When("to The Solaris Temple Level 1", ImperialFields, hargan,
				new TakeTransition(SolarisTemple1_8) { Where = ImperialFields, SearchAt = Search.Exit(SolarisTemple1_8, "act8_temple_entrance_v01_01") });
			Offsets.Vector2i? waypoint(CombatContext c) => c.Terrain.Waypoint?.GridPos;
			var solarisWp = WaypointKnownLatch(SolarisTemple1_8);
			rules.When("to the waypoint (to discover it)", SolarisTemple1_8, () => hargan() && !solarisWp(), new GoToWaypoint() { Where = SolarisTemple1_8, DoneWhen = solarisWp });
			rules.When("to The Solaris Temple Level 2", SolarisTemple1_8, hargan,
				new TakeTransition(SolarisTemple2_8) { Where = SolarisTemple1_8, SearchAt = waypoint });
			// Dawn (TODO: her effects, for AvoidSpam)
			const string sun = "Sun Orb";
			var dawn = UniqueNamed("Dawn");
			ToTown(rules, 8, "back to town with the Sun Orb", SolarisTemple2_8, () => hargan() && has(sun));
			rules.When("pick up the Sun Orb", SolarisTemple2_8, () => hargan() && ItemShown(sun), new PickUp(sun) { Where = SolarisTemple2_8 });
			rules.When("pick up the Sun Orb", SolarisTemple2_8, () => hargan() && SeenDead(dawn), new PickUpQuestItem($"the {sun}") { Where = SolarisTemple2_8 });
			BossArena(rules, SolarisTemple2_8, () => hargan() && !has(sun), "Dawn, Harbinger of Solaris", dawn,
				new TakeTransition("Portal") { SearchAt = Search.Tile("gemling_queen_throne_p2_v01_01") });

			quests.Handlers.Add(new QuestHandler(eh, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Clarissa" },
			});
		}

		/// <summary>
		/// The Wings of Vastiri (the user's table):
		///  - "Travel to the Bath House": from town by waypoint to The Solaris Temple Level 1; to The Solaris Concourse (the
		///    temple_to_battlefront_v01_01 tile with two statue_gold_large_female_03 within 100 cells); there, to the waypoint,
		///    then to The Harbour Bridge (act3_battlefront_to_harbor_v01_01); to The Lunaris Concourse
		///    (act3_riverbridge_transition_v01_01); there, to the waypoint, then to The Bath House (EbonyBarracks_arch_v01_01)
		///  - "Kill Hector Titucius": by the bathhouse_rosette_v03_02 tile, kill him, click the "Tomb of Hector Titucius", and
		///    pick up the Wings of Vastiri
		///  - "Deliver the Wings of Vastiri to Hargan": with no Reflection of Terror yet, to The High Gardens (the
		///    bathhouse_transition_v01_01 tile with two eleganthouse_gardenpath_straight_v01_01 within 150 cells), into "The
		///    Pools of Terror" (garden_thickwall_straight_v01_01), and kill Yugul (AvoidSpam); then back to town
		/// TODO: Yugul's effects (AvoidSpam's hazards); whether Hector comes out of the tomb (clicked first) or guards it
		/// </summary>
		private void WingsOfVastiriQuest(FinishAllQuests quests) {
			const string wv = WingsOfVastiri;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(8);
			bool at(string prefix) => StepStarts(wv, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(wv);

			// "Travel to the Bath House"
			bool travel() => at("Travel to the Bath House");
			rules.When("by waypoint to The Solaris Temple Level 1", town, travel, new UseWaypoint(SolarisTemple1_8) { Where = town });
			rules.When("to The Solaris Concourse", SolarisTemple1_8, travel,
				new TakeTransition(SolarisConcourse) { Where = SolarisTemple1_8, SearchAt = Search.TileWithNeighbors("temple_to_battlefront_v01_01", "statue_gold_large_female_03", 2, 100) });
			var concourseWp = WaypointKnownLatch(SolarisConcourse);
			rules.When("to the waypoint (to discover it)", SolarisConcourse, () => travel() && !concourseWp(), new GoToWaypoint() { Where = SolarisConcourse, DoneWhen = concourseWp });
			rules.When("to The Harbour Bridge", SolarisConcourse, travel,
				new TakeTransition(HarbourBridge) { Where = SolarisConcourse, SearchAt = Search.Exit(HarbourBridge, "act3_battlefront_to_harbor_v01_01") });
			rules.When("to The Lunaris Concourse", HarbourBridge, travel,
				new TakeTransition(LunarisConcourse) { Where = HarbourBridge, SearchAt = Search.Exit(LunarisConcourse, "act3_riverbridge_transition_v01_01") });
			var lunarisWp = WaypointKnownLatch(LunarisConcourse);
			rules.When("to the waypoint (to discover it)", LunarisConcourse, () => travel() && !lunarisWp(), new GoToWaypoint() { Where = LunarisConcourse, DoneWhen = lunarisWp });
			rules.When("to The Bath House", LunarisConcourse, travel,
				new TakeTransition(BathHouse) { Where = LunarisConcourse, SearchAt = Search.Exit(BathHouse, "EbonyBarracks_arch_v01_01") });

			// The Bath House: Hector Titucius
			const string wings = "Wings of Vastiri", tomb = "Tomb of Hector Titucius";
			bool kill() => at("Kill Hector") && !has(wings);
			var hector = UniqueNamed("Hector");
			bool hectorAlive(Entity e) => hector(e) && IsAlive(e);
			var hectorDead = Latch(() => SeenDead(hector));
			var rosette = Search.Tile("bathhouse_rosette_v03_02");
			rules.When("pick up the Wings of Vastiri", BathHouse, () => kill() && ItemShown(wings), new PickUp(wings) { Where = BathHouse });
			rules.When("kill Hector Titucius", BathHouse, () => kill() && CanReach(hectorAlive), new Orbit("Hector Titucius", hector, main) { Where = BathHouse });
			rules.When("open the Tomb of Hector Titucius", BathHouse, () => kill() && TargetableLabelShown(tomb),
				new Activate(tomb) { Where = BathHouse, SearchAt = rosette, DoneWhen = () => ItemShown(wings) || has(wings) });
			rules.When("pick up the Wings of Vastiri", BathHouse, () => kill() && hectorDead(), new PickUpQuestItem($"the {wings}") { Where = BathHouse });
			rules.When("find Hector Titucius", BathHouse, kill, new Orbit("Hector Titucius", hector, main) { Where = BathHouse, SearchAt = rosette });

			// "Deliver the Wings of Vastiri to Hargan": Yugul first (TODO: his effects, for AvoidSpam)
			bool deliver() => at("Deliver the Wings");
			bool terrorDone() => StepOf(ReflectionOfTerror) != null || QuestFinished(ReflectionOfTerror);
			rules.When("to The High Gardens", BathHouse, () => deliver() && !terrorDone(),
				new TakeTransition(HighGardens) { Where = BathHouse, SearchAt = Search.TileWithNeighbors("bathhouse_transition_v01_01", "eleganthouse_gardenpath_straight_v01_01", 2, 150) });
			ToTown(rules, 8, "back to town with the Wings of Vastiri", HighGardens, () => deliver() && terrorDone());
			ToTown(rules, 8, "back to town with the Wings of Vastiri", BathHouse, () => deliver() && terrorDone());
			BossArena(rules, HighGardens, () => deliver() && !terrorDone(), "Yugul, Reflection of Terror", UniqueNamed("Yugul"),
				new TakeTransition("The Pools of Terror") { SearchAt = Search.Tile("garden_thickwall_straight_v01_01") });

			quests.Handlers.Add(new QuestHandler(wv, rules) { Active = _ => rules.Holding() != null });
		}

		/// <summary>
		/// Lunar Eclipse (the user's table):
		///  - "Travel to the Lunaris Temple": from town by waypoint to The Lunaris Temple Level 1 if it is unlocked, else to The
		///    Lunaris Concourse, and to The Lunaris Temple Level 1 (act3_temple_entrance_v01_01)
		///  - "Find the Moon Orb": Level 1, to the waypoint and the exit to Level 2 near it; Level 2, through the "Portal"
		///    (templeclean_prepiety_roundtop_center_01), kill Dusk (AvoidSpam), and pick up the Moon Orb
		///  - "Take the Moon Orb to the Harbour Bridge": back to town, by waypoint to The Lunaris Concourse, to The Harbour
		///    Bridge (act3_riverbridge_transition_v01_01_barracks); there, toward <1961, 599>, into "The Sky Shrine", click
		///    the "Statue of the Sisters", kill Solaris and Lunaris (AvoidSpam), and take the exit to The Blood Aqueduct
		/// TODO: Dusk's and the sisters' effects (AvoidSpam's hazards); the sisters' names (taken as "Solaris" and "Lunaris")
		/// </summary>
		private void LunarEclipseQuest(FinishAllQuests quests) {
			const string le = LunarEclipse;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(8);
			bool at(string prefix) => StepStarts(le, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(le);

			// "Travel to the Lunaris Temple"
			bool travel() => at("Travel to the Lunaris Temple");
			rules.When("by waypoint to The Lunaris Temple Level 1", town, () => travel() && WaypointPanel.IsKnownUnlocked(LunarisTemple1_8),
				new UseWaypoint(LunarisTemple1_8) { Where = town });
			rules.When("by waypoint to The Lunaris Concourse", town, travel, new UseWaypoint(LunarisConcourse) { Where = town });
			rules.When("to The Lunaris Temple Level 1", LunarisConcourse, travel,
				new TakeTransition(LunarisTemple1_8) { Where = LunarisConcourse, SearchAt = Search.Exit(LunarisTemple1_8, "act3_temple_entrance_v01_01") });

			// "Find the Moon Orb" (TODO: Dusk's effects, for AvoidSpam)
			const string moon = "Moon Orb";
			bool find() => at("Find the Moon Orb");
			Offsets.Vector2i? waypoint(CombatContext c) => c.Terrain.Waypoint?.GridPos;
			var templeWp = WaypointKnownLatch(LunarisTemple1_8);
			rules.When("to the waypoint (to discover it)", LunarisTemple1_8, () => find() && !templeWp(), new GoToWaypoint() { Where = LunarisTemple1_8, DoneWhen = templeWp });
			rules.When("to The Lunaris Temple Level 2", LunarisTemple1_8, find, new TakeTransition(LunarisTemple2_8) { Where = LunarisTemple1_8, SearchAt = waypoint });
			var dusk = UniqueNamed("Dusk");
			rules.When("pick up the Moon Orb", LunarisTemple2_8, () => find() && !has(moon) && ItemShown(moon), new PickUp(moon) { Where = LunarisTemple2_8 });
			rules.When("pick up the Moon Orb", LunarisTemple2_8, () => find() && !has(moon) && SeenDead(dusk), new PickUpQuestItem($"the {moon}") { Where = LunarisTemple2_8 });
			BossArena(rules, LunarisTemple2_8, () => find() && !has(moon), "Dusk, Harbinger of Lunaris", dusk,
				new TakeTransition("Portal") { SearchAt = Search.Tile("templeclean_prepiety_roundtop_center_01") });

			// "Take the Moon Orb to the Harbour Bridge"
			bool take() => at("Take the Moon Orb");
			ToTown(rules, 8, "back to town with the Moon Orb", LunarisTemple2_8, take);
			rules.When("by waypoint to The Lunaris Concourse", town, take, new UseWaypoint(LunarisConcourse) { Where = town });
			rules.When("to The Harbour Bridge", LunarisConcourse, take,
				new TakeTransition(HarbourBridge) { Where = LunarisConcourse, SearchAt = Search.Exit(HarbourBridge, "act3_riverbridge_transition_v01_01_barracks") });
			// The Sky Shrine: Solaris and Lunaris (TODO: their effects, for AvoidSpam)
			const string statue = "Statue of the Sisters";
			var solaris = UniqueNamed("Solaris");
			var lunaris = UniqueNamed("Lunaris");
			var sisters = UniqueNamedAny("Solaris", "Lunaris");
			bool sistersAlive(Entity e) => sisters(e) && IsAlive(e);
			var solarisDead = Latch(() => SeenDead(solaris));
			var lunarisDead = Latch(() => SeenDead(lunaris));
			bool bothDead() => solarisDead() && lunarisDead();
			var shrineDoor = new TakeTransition("The Sky Shrine") { Where = HarbourBridge, SearchAt = Search.At(1961, 599),
				DoneWhen = () => CanReach(sisters) || TargetableLabelShown(statue) };
			var inShrine = Latch(() => shrineDoor.Taken || CanReach(sisters));
			bool shrine() => take() || inShrine();
			rules.When("to The Blood Aqueduct", HarbourBridge, () => shrine() && bothDead(), new TakeTransition(BloodAqueduct) { Where = HarbourBridge });
			rules.When("kill Solaris and Lunaris", HarbourBridge, () => shrine() && CanReach(sistersAlive),
				new AvoidSpam("Solaris and Lunaris", sisters, main) { Where = HarbourBridge });
			rules.When("click the Statue of the Sisters", HarbourBridge, () => shrine() && TargetableLabelShown(statue),
				new Activate(statue) { Where = HarbourBridge, DoneWhen = () => CanReach(sistersAlive) });
			rules.When("the rest of the sisters' fight", HarbourBridge, () => shrine() && inShrine(),
				new FightUntil("The Sky Shrine", () => bothDead() || CanReach(sistersAlive), main) { Where = HarbourBridge });
			rules.When("into The Sky Shrine", HarbourBridge, take, shrineDoor);

			quests.Handlers.Add(new QuestHandler(le, rules) { Active = _ => rules.Holding() != null });
		}
	}
}
