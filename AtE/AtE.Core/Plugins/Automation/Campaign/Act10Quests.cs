using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 10's quests, as tables of rules (FirstOf, as in Act4Quests): Safe Passage (Plaguewing on The Cathedral Rooftop,
	/// then Vilenta in The Control Blocks, for Vilenta's Vengeance), Death and Rebirth (Avarius in The Desecrated Chambers,
	/// for the Staff of Purity) and An End to Hunger (Innocence at Kitava's Horns, then Kitava in The Feeding Trough).
	/// TODO: confirm live (not seen yet): the labels, names and tiles here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string CathedralRooftop10 = "The Cathedral Rooftop", RavagedSquare10 = "The Ravaged Square",
			ControlBlocks10 = "The Control Blocks", TorchedCourts10 = "The Torched Courts", DesecratedChambers10 = "The Desecrated Chambers",
			Canals10 = "The Canals", FeedingTrough10 = "The Feeding Trough";
		private const string SafePassage = "Safe Passage", VilentasVengeance = "Vilenta's Vengeance", DeathAndRebirth = "Death and Rebirth",
			AnEndToHunger = "An End to Hunger";

		private void Act10Quests(FinishAllQuests quests) {
			SafePassageQuest(quests);
			quests.Handlers.Add(Stub("Map to Tsoatha"));
			quests.Handlers.Add(Stub("No Love for Old Ghosts"));
			// (Vilenta is on Safe Passage's way; the reward is still taken)
			quests.Handlers.Add(QuestHandler.Elsewhere(VilentasVengeance, SafePassage));
			DeathAndRebirthQuest(quests);
			AnEndToHungerQuest(quests);
			quests.Handlers.Add(Stub("Toward the Future"));
			quests.Handlers.Add(Stub("Aiding an Archivist"));
		}

		/// <summary>
		/// Safe Passage (the user's table):
		///  - "Find Bannon on the Cathedral Rooftop and save him": Oriath Docks, to The Cathedral Rooftop; there, toward
		///    cathedral_roof_transition_v01_01, and kill Plaguewing (AvoidSpam)
		///  - "Talk to Bannon": on The Cathedral Rooftop
		///  - "Meet Bannon at the docks" (the talk is in town):
		///     - The Cathedral Rooftop: to The Ravaged Square; there, to The Control Blocks (slaveden_entrance_steps_v01_01)
		///     - The Control Blocks: to the waypoint (to discover it); into the "Arena" (slave_iron_thinwall_end_v01_01), and
		///       kill Vilenta (AvoidSpam); at Vilenta's Vengeance's reward ("Talk to Lani"), to Oriath Docks
		/// TODO: Plaguewing's and Vilenta's effects (AvoidSpam's hazards); confirm live: Vilenta's name, Vilenta's Vengeance's steps
		/// </summary>
		private void SafePassageQuest(FinishAllQuests quests) {
			const string sp = SafePassage;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(10);
			bool at(string prefix) => StepStarts(sp, prefix);
			var rules = new FirstOf(sp);

			// "Find Bannon on the Cathedral Rooftop and save him": Plaguewing
			bool find() => at("Find Bannon");
			rules.When("to The Cathedral Rooftop", town, find, new TakeTransition(CathedralRooftop10) { Where = town });
			var plaguewing = UniqueNamed("Plaguewing");
			bool plaguewingAlive(Entity e) => plaguewing(e) && IsAlive(e);
			rules.When("kill Plaguewing", CathedralRooftop10, () => find() && CanReach(plaguewingAlive),
				new AvoidSpam("Plaguewing", plaguewing, main) { Where = CathedralRooftop10 });
			rules.When("find Plaguewing", CathedralRooftop10, find,
				new Orbit("Plaguewing", plaguewing, main) { Where = CathedralRooftop10, SearchAt = Search.Tile("cathedral_roof_transition_v01_01") });

			// "Talk to Bannon"
			rules.When("talk to Bannon", CathedralRooftop10, () => at("Talk to Bannon"),
				new TalkTo("Bannon", TalkTo.NpcNamed("Bannon")) { Where = CathedralRooftop10, DoneWhen = () => !at("Talk to Bannon") });

			// "Meet Bannon at the docks": by The Control Blocks, and Vilenta on the way
			bool meet() => at("Meet Bannon");
			bool vilentaReward() => AtReward(VilentasVengeance) || StepStarts(VilentasVengeance, "Talk to Lani");
			bool vilentaDone() => vilentaReward() || QuestFinished(VilentasVengeance);
			rules.When("to The Ravaged Square", CathedralRooftop10, meet, new TakeTransition(RavagedSquare10) { Where = CathedralRooftop10 });
			rules.When("to The Control Blocks", RavagedSquare10, () => meet() && !vilentaDone(),
				new TakeTransition(ControlBlocks10) { Where = RavagedSquare10, SearchAt = Search.Exit(ControlBlocks10, "slaveden_entrance_steps_v01_01") });
			ToTown(rules, 10, "to Oriath Docks for Vilenta's Vengeance's reward", ControlBlocks10, () => meet() && vilentaDone());
			var blocksWp = WaypointKnownLatch(ControlBlocks10);
			rules.When("to the waypoint (to discover it)", ControlBlocks10, () => meet() && !blocksWp(), new GoToWaypoint() { Where = ControlBlocks10, DoneWhen = blocksWp });
			BossArena(rules, ControlBlocks10, () => meet() && !vilentaDone(), "Vilenta", UniqueNamed("Vilenta"),
				new TakeTransition("Arena") { SearchAt = Search.Tile("slave_iron_thinwall_end_v01_01") });

			quests.Handlers.Add(new QuestHandler(sp, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Bannon" },
			});
		}

		/// <summary>
		/// Death and Rebirth (the user's table):
		///  - "Travel to the Desecrated Chamber of Innocence": Oriath Docks, by waypoint to The Control Blocks (or The
		///    Desecrated Chambers, or The Torched Courts, the furthest known); The Control Blocks, to the waypoint, and to The
		///    Torched Courts (Oriath_AreaTransition_v01_03); The Torched Courts, to The Desecrated Chambers (templar_to_innocents_v01_01)
		///  - "Find the Staff of Purity": in The Desecrated Chambers, to the waypoint; into the "Sanctum of Innocence"
		///    (transition_chamber_to_boss_shattered_v01_01), kill Avarius (AvoidSpam), and pick up the Staff of Purity
		///  - "Deliver the Staff of Purity to Bannon": to Oriath Docks (the talk is in town)
		/// TODO: Avarius's effects (AvoidSpam's hazards); confirm live: that the Staff of Purity drops (is picked up) after the fight
		/// </summary>
		private void DeathAndRebirthQuest(FinishAllQuests quests) {
			const string dr = DeathAndRebirth;
			string town = TownOf(10);
			bool at(string prefix) => StepStarts(dr, prefix);
			bool known(string area) => WaypointPanel.IsKnownUnlocked(area);
			const string staff = "Staff of Purity";
			bool has() => Equipment.HasQuestItem(staff);
			var rules = new FirstOf(dr);

			// Oriath Docks: by waypoint to the furthest known of The Desecrated Chambers, The Torched Courts and The Control Blocks
			bool go() => at("Travel to the Desecrated") || (at("Find the Staff of Purity") && !has());
			rules.When("by waypoint to The Desecrated Chambers", town, () => go() && known(DesecratedChambers10), new UseWaypoint(DesecratedChambers10) { Where = town });
			rules.When("by waypoint to The Torched Courts", town, () => go() && known(TorchedCourts10), new UseWaypoint(TorchedCourts10) { Where = town });
			rules.When("by waypoint to The Control Blocks", town, go, new UseWaypoint(ControlBlocks10) { Where = town });

			var blocksWp = WaypointKnownLatch(ControlBlocks10);
			rules.When("to the waypoint (to discover it)", ControlBlocks10, () => go() && !blocksWp(), new GoToWaypoint() { Where = ControlBlocks10, DoneWhen = blocksWp });
			rules.When("to The Torched Courts", ControlBlocks10, go,
				new TakeTransition(TorchedCourts10) { Where = ControlBlocks10, SearchAt = Search.Exit(TorchedCourts10, "Oriath_AreaTransition_v01_03") });
			rules.When("to The Desecrated Chambers", TorchedCourts10, go,
				new TakeTransition(DesecratedChambers10) { Where = TorchedCourts10, SearchAt = Search.Exit(DesecratedChambers10, "templar_to_innocents_v01_01") });

			// The Desecrated Chambers: Avarius, and the Staff of Purity
			bool wantStaff() => at("Find the Staff of Purity") && !has();
			var avarius = UniqueNamed("Avarius");
			rules.When("pick up the Staff of Purity", DesecratedChambers10, () => wantStaff() && ItemShown(staff), new PickUp(staff) { Where = DesecratedChambers10 });
			rules.When("pick up the Staff of Purity", DesecratedChambers10, () => wantStaff() && SeenDead(avarius),
				new PickUpQuestItem($"the {staff}") { Where = DesecratedChambers10 });
			var chambersWp = WaypointKnownLatch(DesecratedChambers10);
			rules.When("to the waypoint (to discover it)", DesecratedChambers10, () => wantStaff() && !chambersWp(),
				new GoToWaypoint() { Where = DesecratedChambers10, DoneWhen = chambersWp });
			BossArena(rules, DesecratedChambers10, wantStaff, "Avarius", avarius,
				new TakeTransition("Sanctum of Innocence") { SearchAt = Search.Tile("transition_chamber_to_boss_shattered_v01_01") });

			// "Deliver the Staff of Purity to Bannon"
			ToTown(rules, 10, "to Oriath Docks with the Staff of Purity", DesecratedChambers10, () => at("Deliver the Staff of Purity"));

			quests.Handlers.Add(new QuestHandler(dr, rules) {
				Active = _ => rules.Holding() != null,
			});
		}

		/// <summary>
		/// An End to Hunger (the user's table):
		///  - Oriath Docks ("Meet Innocence at Kitava's Horns", "Kill Kitava"): by waypoint to The Ravaged Square
		///  - The Ravaged Square: toward OriathBlockage_v02_01, talk to Innocence (while the step is to meet him), and to The
		///    Canals (the same tile)
		///  - The Canals: to The Feeding Trough (the one CanalBridgeTransition_v01_01 tile that can be walked to)
		///  - The Feeding Trough: toward chitus_rosette_defaced_v03_04; "Talk to Sin"; into the "Altar of Hunger" (the same
		///    tile), and kill Kitava (AvoidSpam)
		/// TODO: Kitava's effects (AvoidSpam's hazards) and his phases (here: the fight goes on until the step moves on);
		/// confirm live: whether the Altar of Hunger only opens after talking to Sin
		/// </summary>
		private void AnEndToHungerQuest(FinishAllQuests quests) {
			const string eh = AnEndToHunger;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(10);
			bool at(string prefix) => StepStarts(eh, prefix);
			bool meet() => at("Meet Innocence");
			bool kill() => at("Kill Kitava");
			var rules = new FirstOf(eh);

			rules.When("by waypoint to The Ravaged Square", town, () => meet() || kill(), new UseWaypoint(RavagedSquare10) { Where = town });

			// The Ravaged Square: Innocence, and The Canals
			var blockage = Search.Tile("OriathBlockage_v02_01");
			rules.When("talk to Innocence", RavagedSquare10, meet,
				new TalkTo("Innocence", TalkTo.NpcNamed("Innocence")) { Where = RavagedSquare10, SearchAt = blockage, DoneWhen = () => !meet() });
			rules.When("to The Canals", RavagedSquare10, kill,
				new TakeTransition(Canals10) { Where = RavagedSquare10, SearchAt = Search.Exit(Canals10, "OriathBlockage_v02_01") });

			// The Canals (two bridge tiles, only one of them can be walked to)
			rules.When("to The Feeding Trough", Canals10, kill,
				new TakeTransition(FeedingTrough10) { Where = Canals10, SearchAt = ReachableTile("CanalBridgeTransition_v01_01") });

			// The Feeding Trough: Sin, then Kitava (the arena's door is by the rosette tile, the way to Sin too)
			const string rosette = "chitus_rosette_defaced_v03_04";
			rules.When("talk to Sin", FeedingTrough10, () => at("Talk to Sin"),
				new TalkTo("Sin", TalkTo.NpcNamed("Sin")) { Where = FeedingTrough10, SearchAt = Search.Tile(rosette), DoneWhen = () => !at("Talk to Sin") });
			var kitava = UniqueNamed("Kitava");
			bool kitavaAlive(Entity e) => kitava(e) && IsAlive(e);
			var altar = new TakeTransition("Altar of Hunger") { Where = FeedingTrough10, SearchAt = Search.Tile(rosette), DoneWhen = () => CanReach(kitava) };
			var inAltar = Latch(() => altar.Taken || CanReach(kitava));
			rules.When("kill Kitava", FeedingTrough10, () => kill() && CanReach(kitavaAlive),
				new AvoidSpam("Kitava, the Insatiable", kitava, main) { Where = FeedingTrough10 });
			// (a phase over, Kitava not in sight yet: fight on until the step moves on)
			rules.When("the rest of Kitava's fight", FeedingTrough10, () => kill() && inAltar(),
				new FightUntil("the Altar of Hunger", () => !kill() || CanReach(kitavaAlive), main) { Where = FeedingTrough10 });
			rules.When("into the Altar of Hunger", FeedingTrough10, kill, altar);

			quests.Handlers.Add(new QuestHandler(eh, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Innocence", "Sin" },
			});
		}
	}
}
