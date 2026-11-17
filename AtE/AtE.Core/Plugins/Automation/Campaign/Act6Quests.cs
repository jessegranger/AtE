using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 6's quests, as tables of rules (FirstOf, as in Act4Quests): Fallen from Grace (clear The Twilight Strand) and The
	/// Brine King (the act's story: its long step "Search the Mud Flats for Nessa" covers the way through Tukohama,
	/// Shavronne and Brutus, Abberath and Ryslatha, to The Beacon and the Brine King's Reef).
	/// TODO: confirm live (not seen yet): the labels, names and tiles here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string Coast6 = "The Coast", MudFlats6 = "The Mud Flats", KaruiFortress = "The Karui Fortress", Ridge = "The Ridge",
			LowerPrison6 = "The Lower Prison", ShavronnesTower = "Shavronne's Tower", PrisonersGate6 = "Prisoner's Gate",
			WesternForest6 = "The Western Forest", Riverways6 = "The Riverways", Wetlands6 = "The Wetlands",
			SouthernForest6 = "The Southern Forest", CavernOfAnger6 = "The Cavern of Anger", Beacon = "The Beacon",
			BrineKingsReef = "The Brine King's Reef", TwilightStrand6 = "The Twilight Strand";
		private const string FallenFromGrace = "Fallen from Grace", BrineKing = "The Brine King", FatherOfWar = "The Father of War",
			EssenceOfUmbra = "Essence of Umbra", ClovenOne = "The Cloven One", PuppetMistress = "The Puppet Mistress";

		private void Act6Quests(FinishAllQuests quests) {
			FallenFromGraceQuest(quests);
			BrineKingQuest(quests);
			// (their work is The Brine King's step "Search the Mud Flats for Nessa"; their rewards and trips are still done)
			foreach ( var quest in new[] { FatherOfWar, EssenceOfUmbra, ClovenOne, PuppetMistress } ) {
				quests.Handlers.Add(QuestHandler.Elsewhere(quest, BrineKing));
			}
			quests.Handlers.Add(Stub("Bestel's Epic"));
			quests.Handlers.Add(Stub("The Rogue Harbour"));
			quests.Handlers.Add(Stub("The Lost Expedition"));
		}

		// ---- helpers for the rules ----

		private readonly HashSet<string> questsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		/// <summary>
		/// Is the quest done: its passive point had (as /passives said: at the start of the run, and again after each Book of
		/// Skill is used, see UseBooks), or gone from the tracker after being in it (this run; for the quests with no book)?
		/// </summary>
		private bool QuestFinished(string quest) {
			bool tracked = StepOf(quest) != null;
			if ( tracked ) questsSeen.Add(quest);
			return CampaignProgress.HasPassiveFrom(quest) || (!tracked && questsSeen.Contains(quest));
		}

		/// <summary>Can the player walk to any group of tiles cut from a .tdt whose path has tilePathPart?</summary>
		private static bool TileReachable(string tilePathPart) {
			var terrain = GetTerrain();
			var p = GetPlayer()?.GetComponent<Positioned>();
			if ( terrain == null || !IsValid(p) ) return false;
			return terrain.TileFeatures(tilePathPart).Any(f => DefaultNavigator.TrySnapReachable(terrain, p.GridPos, f.GridPos, out _, 25));
		}

		/// <summary>The nearest group of tiles with tilePathPart the player can walk to (eg, of two arena doors, the one on this side); else null.</summary>
		private static Func<CombatContext, Offsets.Vector2i?> ReachableTile(string tilePathPart) => ctx =>
			ctx.Terrain.TileFeatures(tilePathPart).Where(f => DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, f.GridPos, out _, 25))
				.OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).FirstOrDefault()?.GridPos;

		/// <summary>The area's waypoint discovered: the panel says so, or the player was at it (latched in seen, this run).</summary>
		private static Func<bool> WaypointKnownLatch(string area) {
			bool seen = false;
			return () => seen = seen || WaypointPanel.IsKnownUnlocked(area) || (InAny(area) && AtWaypoint());
		}

		/// <summary>
		/// Fallen from Grace: in Lioneye's Watch, "Clear the Twilight Strand": take the exit to The Twilight Strand; there,
		/// explore it all, killing every monster; "Talk to Lilly for your reward": back to Lioneye's Watch.
		/// </summary>
		private void FallenFromGraceQuest(FinishAllQuests quests) {
			const string ffg = FallenFromGrace;
			string town = TownOf(6);
			bool clear() => StepStarts(ffg, "Clear the Twilight Strand");
			var rules = new FirstOf(ffg);
			rules.When("to The Twilight Strand", town, clear, new TakeTransition(TwilightStrand6) { Where = town });
			rules.When("clear The Twilight Strand", TwilightStrand6, clear, new ClearArea() { Where = TwilightStrand6 });
			rules.When("to Lioneye's Watch for the reward", TwilightStrand6, () => AtReward(ffg), new EnterArea(town));
			quests.Handlers.Add(new QuestHandler(ffg, rules) { Active = _ => rules.Holding() != null });
		}

		/// <summary>
		/// The Brine King (the user's table, 3.30): "Search the Mud Flats for Nessa", by area:
		///  - Lioneye's Watch: to The Coast; once The Riverways' waypoint is known, there (or to The Wetlands, while The
		///    Puppet Mistress is not done)
		///  - The Coast: to the BeachToSwamp/act6_area2_transition tile and talk to Ailith; once another quest says "Burn
		///    away ...", back to that tile, and take the exit to The Mud Flats (once it can be clicked)
		///  - The Mud Flats: with the Eye of Conquest, to The Karui Fortress (Transitions/beach_karuipools); else explore and
		///    fight until it drops, and pick it up
		///  - The Karui Fortress: once The Father of War says "Talk to Tarkleigh", to The Ridge (Transitions/swamp_to_ridge;
		///    from inside the arena, out by its door "The Karui Fortress" first); else to the nearest
		///    Features/swamp_longbridge_v01_01 tile, into "Tukohama's Keep", and kill Tukohama
		///  - The Ridge: to The Lower Prison (Special/ledge_prisonback); The Lower Prison: to Shavronne's Tower (prison_exit_up)
		///  - Shavronne's Tower: up the "Stairs" (prison_exit_up tiles, CrossTransitions) to the prison_ladder_v01_01 tile, into
		///    the "Prison Rooftop", kill Brutus and Shavronne (AvoidSpam), then take the exit "The Warden's Chambers"
		///  - Prisoner's Gate: while The Cloven One is not done, Abberath: into "Valley of the Fire Drinker" (the
		///    beach_togoatboss_v01_03 tile that can be walked to) and fight him; when he leaves, out by "The Cloven Pass" (the
		///    beach_togoatboss_v01_01 tile that can be walked to), into "Valley of the Soul Drinker" (v01_03 again) and kill
		///    him (AvoidSpam). Once done (or "Talk to Bestel"), to The Western Forest (Road/beachroad_v02_07; from inside an
		///    arena, out by its door "Prisoner's Gate" first)
		///  - The Western Forest: to the waypoint (to discover it), then to The Riverways (Transitions/roadtothickforest_entrance)
		///  - The Riverways: to the waypoint; while The Puppet Mistress is not done, to The Wetlands (bridgeconnection), else to
		///    The Southern Forest (Transitions/forest_to_river)
		///  - The Wetlands: while The Puppet Mistress is not done, into "The Spawning Ground" (forest_caveentrance_v01_01), clear
		///    it until Ryslatha shows, and kill her (AvoidSpam); at its reward, to Lioneye's Watch
		///  - The Southern Forest: to The Cavern of Anger (forest_caveentrance)
		///  - The Cavern of Anger: without The Black Flag, open the "Flag Chest" and pick it up; with it, through the door
		///    "Passage" (cavedown_boss_exit_v01_01) when it can be walked to, else the exit "The Beacon" (by the three
		///    caveup_exit_v01_01 tiles in turn)
		/// Then The Beacon: "Use the Black Flag ..." and "Push the fuel carts ...": push the two payloads (PushBeaconPayload,
		/// toward the LedgeMosaic/mosaic_doubleledge_payload tile until one is in range); "Light the Beacon ...": click the
		/// "Ignition Switch" (when in sight), then "The Beacon"; "Talk to Weylam Roth at the Beacon": by the
		/// beach_walkablelargecliffwater_weylamwalk tile. The Brine King's Reef: "Search the Brine King's Reef for Nessa": to
		/// the beach_island_transition_brine_king tile, into "The Brine King's Throne"; "Defeat the Brine King": kill
		/// Tsoagoth (AvoidSpam); "Talk to Weylam Roth to access Act 7": wait for him, and talk to him.
		/// TODO: Tukohama's fight (his immunity phase, his totems); Abberath's effects (AvoidSpam's hazards); the Brine King's
		/// effects (AvoidSpam's hazards) and his immunity phases
		/// </summary>
		private void BrineKingQuest(FinishAllQuests quests) {
			const string bk = BrineKing;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(6);
			bool at(string prefix) => StepStarts(bk, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			bool search() => at("Search the Mud Flats for Nessa");
			var rules = new FirstOf(bk);
			bool puppetDone() => QuestFinished(PuppetMistress);
			bool puppetReward() => AtReward(PuppetMistress) || StepStarts(PuppetMistress, "Talk to Tarkleigh");
			bool clovenDone() => QuestFinished(ClovenOne) || StepStarts(ClovenOne, "Talk to Bestel");
			bool riverwaysKnown() => WaypointPanel.IsKnownUnlocked(Riverways6);

			// Lioneye's Watch
			rules.When("by waypoint to The Riverways", town, () => search() && riverwaysKnown() && puppetDone(), new EnterArea(Riverways6));
			rules.When("to The Wetlands", town, () => search() && riverwaysKnown() && !puppetReward(), new EnterArea(Wetlands6));
			rules.When("to The Coast", town, () => search() && !riverwaysKnown(), new TakeTransition(Coast6) { Where = town });

			// The Coast
			var swampWay = Search.Tile("/act6_area2_transition");
			rules.When("to The Mud Flats", Coast6, () => search() && AnyStepStarts("Burn away"),
				new TakeTransition(MudFlats6) { Where = Coast6, SearchAt = swampWay });
			rules.When("talk to Ailith", Coast6, search, new TalkTo("Ailith", TalkTo.NpcNamed("Ailith")) { Where = Coast6, SearchAt = swampWay });

			// The Mud Flats
			bool eye() => has("Eye of Conquest");
			rules.When("to The Karui Fortress", MudFlats6, () => search() && eye(),
				new TakeTransition(KaruiFortress) { Where = MudFlats6, SearchAt = Search.Exit(KaruiFortress, "/beach_karuipools") });
			rules.When("pick up the Eye of Conquest", MudFlats6, () => search() && ItemShown("Eye of Conquest"), new PickUp("Eye of Conquest") { Where = MudFlats6 });
			rules.When("fight for the Eye of Conquest", MudFlats6, search, new ClearArea() { Where = MudFlats6, DoneWhen = () => eye() || ItemShown("Eye of Conquest") });

			// The Karui Fortress: Tukohama (TODO: his immunity phase, his totems)
			bool fatherDone() => QuestFinished(FatherOfWar) || StepStarts(FatherOfWar, "Talk to Tarkleigh");
			var ridgeWay = Search.Exit(Ridge, "/swamp_to_ridge");
			rules.When("out of Tukohama's Keep", KaruiFortress, () => search() && fatherDone() && !TileReachable("/swamp_to_ridge"),
				new TakeTransition(KaruiFortress) { Where = KaruiFortress });
			rules.When("to The Ridge", KaruiFortress, () => search() && fatherDone(), new TakeTransition(Ridge) { Where = KaruiFortress, SearchAt = ridgeWay });
			var tukohama = UniqueNamed("Tukohama");
			var keepDoor = new TakeTransition("Tukohama's Keep") { Where = KaruiFortress, SearchAt = Search.Tile("/swamp_longbridge_v01_01") };
			bool inKeep() => keepDoor.Taken || CanReach(tukohama);
			keepDoor.DoneWhen = () => CanReach(tukohama);
			rules.When("kill Tukohama", KaruiFortress, () => search() && CanReach(tukohama), new AvoidSpam("Tukohama", tukohama, main) { Where = KaruiFortress });
			rules.When("the rest of Tukohama's fight", KaruiFortress, () => search() && inKeep(),
				new FightUntil("Tukohama's Keep", fatherDone, main) { Where = KaruiFortress });
			rules.When("into Tukohama's Keep", KaruiFortress, search, keepDoor);

			// The Ridge, The Lower Prison
			rules.When("to The Lower Prison", Ridge, search,
				new TakeTransition(LowerPrison6) { Where = Ridge, SearchAt = Search.Exit(LowerPrison6, "/ledge_prisonback") });
			rules.When("to Shavronne's Tower", LowerPrison6, search,
				new TakeTransition(ShavronnesTower) { Where = LowerPrison6, SearchAt = Search.Exit(ShavronnesTower, "prison_exit_up") });

			// Shavronne's Tower: Brutus and Shavronne
			var shavronne = UniqueNamed("Shavronne");
			var brutus = UniqueNamed("Brutus");
			var umbra = UniqueNamedAny("Shavronne", "Brutus");
			bool shavronneDead = false, brutusDead = false;
			bool umbraDone() {
				shavronneDead = shavronneDead || SeenDead(shavronne);
				brutusDead = brutusDead || SeenDead(brutus);
				return (shavronneDead && brutusDead) || QuestFinished(EssenceOfUmbra) || AtReward(EssenceOfUmbra);
			}
			var ladder = Search.Tile("/prison_ladder_v01_01");
			var roofDoor = new TakeTransition("Prison Rooftop") { Where = ShavronnesTower, SearchAt = ladder };
			bool onRoof() => roofDoor.Taken || CanReach(umbra);
			roofDoor.DoneWhen = () => CanReach(umbra);
			rules.When("to The Warden's Chambers", ShavronnesTower, () => search() && umbraDone(), new TakeTransition("The Warden's Chambers") { Where = ShavronnesTower });
			rules.When("kill Brutus and Shavronne", ShavronnesTower, () => search() && CanReach(umbra), new AvoidSpam("Brutus and Shavronne", umbra, main) { Where = ShavronnesTower });
			rules.When("the rest of the rooftop's fight", ShavronnesTower, () => search() && onRoof(),
				new FightUntil("Prison Rooftop", umbraDone, main) { Where = ShavronnesTower });
			rules.When("up the stairs to the Prison Rooftop", ShavronnesTower, search,
				new CrossTransitions("the Prison Rooftop", ladder, "prison_exit_up") { Where = ShavronnesTower });
			rules.When("onto the Prison Rooftop", ShavronnesTower, search, roofDoor);

			// Prisoner's Gate: Abberath (TODO: his effects, for AvoidSpam)
			var abberath = UniqueNamed("Abberath");
			bool fought = false;
			bool Fought() => fought = fought || CanReach(abberath);
			var passDoor = new TakeTransition("The Cloven Pass") { Where = PrisonersGate6, SearchAt = ReachableTile("/beach_togoatboss_v01_01") };
			var soulDoor = new TakeTransition("Valley of the Soul Drinker") { Where = PrisonersGate6, SearchAt = ReachableTile("/beach_togoatboss_v01_03") };
			var fireDoor = new TakeTransition("Valley of the Fire Drinker") { Where = PrisonersGate6, SearchAt = ReachableTile("/beach_togoatboss_v01_03") };
			fireDoor.DoneWhen = Fought;
			bool abberathGone() => search() && !clovenDone() && Fought() && !CanReach(abberath);
			var forestRoad = Search.Exit(WesternForest6, "Road/beachroad_v02_07");
			rules.When("out of Abberath's arena", PrisonersGate6, () => search() && clovenDone() && !TileReachable("Road/beachroad_v02_07"),
				new TakeTransition(PrisonersGate6) { Where = PrisonersGate6, SearchAt = ReachableTile("/beach_togoatboss_v01_01") });
			rules.When("to The Western Forest", PrisonersGate6, () => search() && clovenDone(),
				new TakeTransition(WesternForest6) { Where = PrisonersGate6, SearchAt = forestRoad });
			rules.When("kill Abberath", PrisonersGate6, () => search() && CanReach(abberath), new AvoidSpam("Abberath", abberath, main) { Where = PrisonersGate6 });
			rules.When("Abberath left: into The Cloven Pass", PrisonersGate6, () => abberathGone() && !passDoor.Taken, passDoor);
			rules.When("into the Valley of the Soul Drinker", PrisonersGate6, () => abberathGone() && passDoor.Taken, soulDoor);
			rules.When("wait for Abberath", PrisonersGate6, () => search() && !clovenDone() && (soulDoor.Taken || fireDoor.Taken),
				new FightUntil("Abberath's arena", () => clovenDone() || CanReach(abberath), main) { Where = PrisonersGate6 });
			rules.When("into the Valley of the Fire Drinker", PrisonersGate6, () => search() && !clovenDone(), fireDoor);

			// The Western Forest, The Riverways
			var westernWp = WaypointKnownLatch(WesternForest6);
			rules.When("to the waypoint (to discover it)", WesternForest6, () => search() && !westernWp(), new GoToWaypoint() { Where = WesternForest6, DoneWhen = westernWp });
			rules.When("to The Riverways", WesternForest6, search,
				new TakeTransition(Riverways6) { Where = WesternForest6, SearchAt = Search.Exit(Riverways6, "/roadtothickforest_entrance") });
			var riverwaysWp = WaypointKnownLatch(Riverways6);
			rules.When("to the waypoint (to discover it)", Riverways6, () => search() && !riverwaysWp(), new GoToWaypoint() { Where = Riverways6, DoneWhen = riverwaysWp });
			rules.When("to Lioneye's Watch for The Puppet Mistress's reward", Riverways6, () => search() && puppetReward(), new EnterArea(town));
			rules.When("to The Wetlands", Riverways6, () => search() && !puppetDone(),
				new TakeTransition(Wetlands6) { Where = Riverways6, SearchAt = Search.Exit(Wetlands6, "/bridgeconnection") });
			rules.When("to The Southern Forest", Riverways6, search,
				new TakeTransition(SouthernForest6) { Where = Riverways6, SearchAt = Search.Exit(SouthernForest6, "/forest_to_river") });

			// The Wetlands: Ryslatha
			var ryslatha = UniqueNamed("Ryslatha");
			var groundDoor = new TakeTransition("The Spawning Ground") { Where = Wetlands6, SearchAt = Search.Tile("/forest_caveentrance_v01_01") };
			bool inGround() => groundDoor.Taken || CanReach(ryslatha);
			rules.When("to Lioneye's Watch for the reward", Wetlands6, () => search() && puppetReward(), new EnterArea(town));
			rules.When("kill Ryslatha", Wetlands6, () => search() && !puppetDone() && CanReach(ryslatha), new AvoidSpam("Ryslatha", ryslatha, main) { Where = Wetlands6 });
			rules.When("clear The Spawning Ground", Wetlands6, () => search() && !puppetDone() && inGround(),
				new ClearArea() { Where = Wetlands6, DoneWhen = () => CanReach(ryslatha) });
			rules.When("wait for Ryslatha", Wetlands6, () => search() && !puppetDone() && inGround(),
				new FightUntil("The Spawning Ground", () => puppetReward() || CanReach(ryslatha), main) { Where = Wetlands6 });
			groundDoor.DoneWhen = inGround;
			rules.When("into The Spawning Ground", Wetlands6, () => search() && !puppetDone(), groundDoor);

			// The Southern Forest, The Cavern of Anger
			rules.When("to The Cavern of Anger", SouthernForest6, search,
				new TakeTransition(CavernOfAnger6) { Where = SouthernForest6, SearchAt = Search.Exit(CavernOfAnger6, "/forest_caveentrance") });
			bool flag() => has("The Black Flag");
			rules.When("pick up The Black Flag", CavernOfAnger6, () => search() && !flag() && ItemShown("The Black Flag"), new PickUp("The Black Flag") { Where = CavernOfAnger6 });
			rules.When("open the Flag Chest", CavernOfAnger6, () => search() && !flag(),
				new Activate("Flag Chest") { Where = CavernOfAnger6, DoneWhen = () => ItemShown("The Black Flag") });
			rules.When("pick up The Black Flag", CavernOfAnger6, () => search() && !flag(), new PickUpQuestItem("The Black Flag") { Where = CavernOfAnger6 });
			rules.When("through the Passage", CavernOfAnger6, () => search() && flag() && TileReachable("/cavedown_boss_exit_v01_01"),
				new TakeTransition("Passage") { Where = CavernOfAnger6, SearchAt = Search.Tile("/cavedown_boss_exit_v01_01") });
			rules.When("to The Beacon", CavernOfAnger6, () => search() && flag(),
				new TakeTransition(Beacon) { Where = CavernOfAnger6, SearchAt = Search.Tiles(20, "/caveup_exit_v01_01") });

			// The Beacon
			bool push() => at("Use the Black Flag at the Beacon") || at("Push the fuel carts");
			var payloadTile = Search.Tile("/mosaic_doubleledge_payload");
			rules.When("push a fuel cart", Beacon, push, new PushBeaconPayload(main) { Where = Beacon, SearchAt = payloadTile });
			rules.When("push the other fuel cart", Beacon, push, new PushBeaconPayload(main) { Where = Beacon, SearchAt = payloadTile });
			bool light() => at("Light the Beacon");
			rules.When("click the Ignition Switch", Beacon, () => light() && TargetableLabelShown("Ignition Switch"), new Activate("Ignition Switch") { Where = Beacon });
			rules.When("light the Beacon", Beacon, light, new Activate("The Beacon") { Where = Beacon });
			rules.When("talk to Weylam Roth", Beacon, () => at("Talk to Weylam Roth"),
				new TalkTo("Weylam Roth", TalkTo.NpcNamed("Weylam Roth")) { Where = Beacon, SearchAt = Search.Tile("/beach_walkablelargecliffwater_weylamwalk") });

			// The Brine King's Reef (TODO: his effects, for AvoidSpam; his immunity phases)
			var brineKing = UniqueNamed("Brine King");
			bool reefSearch() => at("Search the Brine King's Reef");
			bool defeat() => at("Defeat the Brine King");
			rules.When("kill the Brine King", BrineKingsReef, () => (reefSearch() || defeat()) && CanReach(brineKing),
				new AvoidSpam("Tsoagoth, The Brine King", brineKing, main) { Where = BrineKingsReef });
			rules.When("into The Brine King's Throne", BrineKingsReef, reefSearch,
				new TakeTransition("The Brine King's Throne") { Where = BrineKingsReef, SearchAt = Search.Tile("/beach_island_transition_brine_king"),
					DoneWhen = () => CanReach(brineKing) });
			rules.When("the rest of the Brine King's fight", BrineKingsReef, defeat,
				new FightUntil("The Brine King's Throne", () => !defeat() || CanReach(brineKing), main) { Where = BrineKingsReef });
			rules.When("talk to Weylam Roth", BrineKingsReef, () => at("Talk to Weylam Roth"),
				new TalkTo("Weylam Roth", TalkTo.NpcNamed("Weylam Roth")) { Where = BrineKingsReef });

			quests.Handlers.Add(new QuestHandler(bk, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Ailith", "Weylam Roth" },
			});
		}
	}
}
