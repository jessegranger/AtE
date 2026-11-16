using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 5's quests, as tables of rules (FirstOf, as in Act4Quests): Return to Oriath (out of The Slave Pens), The Key to
	/// Freedom (through The Control Blocks and Oriath Square to The Templar Courts), Death to Purity (Avarius, in The
	/// Chamber of Innocence), The Ravenous God (the Sign of Purity, from The Ossuary, then Kitava on The Cathedral Rooftop)
	/// and Kitava's Torments (the three Karui Relic Cases in The Reliquary).
	/// TODO: confirm live (not seen yet): the labels, names and tiles here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string SlavePens = "The Slave Pens", ControlBlocks = "The Control Blocks", OriathSquare = "Oriath Square",
			TemplarCourts = "The Templar Courts", ChamberOfInnocence = "The Chamber of Innocence", TorchedCourts = "The Torched Courts",
			RuinedSquare = "The Ruined Square", Ossuary = "The Ossuary", Reliquary = "The Reliquary", CathedralRooftop = "The Cathedral Rooftop";
		private const string KeyToFreedom = "The Key to Freedom", DeathToPurity = "Death to Purity", RavenousGod = "The Ravenous God",
			KitavasTorments = "Kitava's Torments", InServiceToScience = "In Service to Science";
		private static readonly string[] Torments = { "Valako's Jaw", "Tukohama's Tooth", "Hinekora's Hair" };

		private void Act5Quests(FinishAllQuests quests) {
			ReturnToOriathAct5Quest(quests);
			KeyToFreedomQuest(quests);
			DeathToPurityQuest(quests);
			RavenousGodQuest(quests);
			KitavasTormentsQuest(quests);
			// (its Miasmeter is picked up on the way through The Control Blocks, see KeyToFreedomQuest; its reward is a talk in town)
			quests.Handlers.Add(QuestHandler.Elsewhere(InServiceToScience, KeyToFreedom));
			quests.Handlers.Add(Stub("The King's Feast"));
		}

		// ---- helpers for the rules ----

		/// <summary>Is a label with this text (exactly, ignoring case) in sight, on something targetable (eg, a lever not pulled yet)?</summary>
		private static bool TargetableLabelShown(string text) => GetUI()?.LabelsOnGround?.GetAllLabels()?
			.Any(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), text, StringComparison.OrdinalIgnoreCase)
				&& (l.Item?.GetComponent<Targetable>()?.IsTargetable ?? true)) ?? false;

		/// <summary>Is a label with this text (exactly, ignoring case) in sight, on something the player can walk to (eg, in the arena the player is in)?</summary>
		private static bool LabelReachable(string text) {
			var terrain = GetTerrain();
			var p = GetPlayer()?.GetComponent<Positioned>();
			if ( terrain == null || !IsValid(p) ) return false;
			return GetUI()?.LabelsOnGround?.GetAllLabels()?
				.Any(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), text, StringComparison.OrdinalIgnoreCase)
					&& l.Item?.GetComponent<Positioned>() is Positioned lp && IsValid(lp)
					&& DefaultNavigator.TrySnapReachable(terrain, p.GridPos, lp.GridPos, out _, 15)) ?? false;
		}

		/// <summary>A unique monster whose name has any of names in it (eg, a boss with a name for each phase).</summary>
		private static Func<Entity, bool> UniqueNamedAny(params string[] names) {
			var each = names.Select(UniqueNamed).ToArray();
			return e => each.Any(m => m(e));
		}

		/// <summary>
		/// Return to Oriath, Act 5's part: in The Slave Pens, "Escape the Slave Pens": to the waypoint (to discover it); then
		/// to the Features/tower_v01_01 tile, kill Overseer Krow, and take the exit "Overseer's Tower" (the town).
		/// </summary>
		private void ReturnToOriathAct5Quest(FinishAllQuests quests) {
			const string rto = ReturnToOriath;
			Func<string> main = BuildPlan.MainSkill;
			var rules = new FirstOf($"{rto} (Act 5)");
			bool escape() => StepStarts(rto, "Escape the Slave Pens");
			bool wpVisited = false;
			bool WaypointKnown() => wpVisited = wpVisited || WaypointPanel.IsKnownUnlocked(SlavePens) || (InAny(SlavePens) && AtWaypoint());
			var tower = Search.Tile("/tower_v01");
			var krow = UniqueNamed("Krow");
			bool krowDead = false;
			bool KrowDead() => krowDead = krowDead || SeenDead(krow);
			rules.When("to the waypoint (to discover it)", SlavePens, () => escape() && !WaypointKnown(), new GoToWaypoint() { Where = SlavePens, DoneWhen = WaypointKnown });
			rules.When("kill Overseer Krow", SlavePens, () => escape() && !KrowDead(),
				new Orbit("Overseer Krow", krow, main) { Where = SlavePens, SearchAt = tower, DoneWhen = KrowDead });
			rules.When("to Overseer's Tower", SlavePens, escape,
				new TakeTransition("Overseer's Tower") { Where = SlavePens, SearchAt = Search.Exit("Overseer's Tower", "/tower_v01") });
			quests.Handlers.Add(new QuestHandler(rto, rules) { Active = _ => rules.Holding() != null });
		}

		/// <summary>
		/// The Key to Freedom:
		///  - Overseer's Tower: "Travel to the Control Blocks": take the exit to The Control Blocks
		///  - The Control Blocks: on the way, the "Experimental Supplies" (In Service to Science's Miasmeter), when in sight and
		///    not had yet; "Kill Justicar Casticus": to the Features/slave_crater_v01_01 tile, kill him (and pick up the Eyes
		///    of Zeal); "Use the Eyes of Zeal to access the Templar Courts": to Oriath Square (the Features/security_exit tile)
		///  - Oriath Square: to the Stonewall/Oriath_AreaTransition_v01_03 tile, click the "Templar Courts Entrance", and take
		///    the exit to The Templar Courts
		///  - The Templar Courts: "Talk to Lani for your reward": by waypoint to Overseer's Tower (the reward is a talk there)
		/// </summary>
		private void KeyToFreedomQuest(FinishAllQuests quests) {
			const string ktf = KeyToFreedom;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(5);
			bool at(string prefix) => StepStarts(ktf, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(ktf);

			rules.When("to The Control Blocks", town, () => at("Travel to the Control Blocks"), new TakeTransition(ControlBlocks) { Where = town });

			// The Control Blocks
			bool inBlocks() => at("Travel to the Control Blocks") || at("Kill Justicar Casticus") || at("Use the Eyes of Zeal");
			bool wantsMiasmeter() => !has("Miasmeter") && !CampaignProgress.HasPassiveFrom(InServiceToScience) && !AtReward(InServiceToScience);
			rules.When("pick up the Miasmeter", ControlBlocks, () => inBlocks() && wantsMiasmeter() && ItemShown("Miasmeter"),
				new PickUp("Miasmeter") { Where = ControlBlocks });
			rules.When("open the Experimental Supplies", ControlBlocks, () => inBlocks() && wantsMiasmeter() && TargetableLabelShown("Experimental Supplies"),
				new Activate("Experimental Supplies") { Where = ControlBlocks });
			var casticus = UniqueNamed("Casticus");
			var crater = Search.Tile("/slave_crater_v01");
			rules.When("kill Justicar Casticus", ControlBlocks, () => at("Kill Justicar Casticus"),
				new Orbit("Justicar Casticus", casticus, main) { Where = ControlBlocks, SearchAt = crater });
			rules.When("pick up the Eyes of Zeal", ControlBlocks, () => (at("Kill Justicar Casticus") || at("Use the Eyes of Zeal")) && !has("Eyes of Zeal") && ItemShown("Eyes of Zeal"),
				new PickUp("Eyes of Zeal") { Where = ControlBlocks });
			rules.When("to Oriath Square", ControlBlocks, () => at("Use the Eyes of Zeal"),
				new TakeTransition(OriathSquare) { Where = ControlBlocks, SearchAt = Search.Exit(OriathSquare, "/security_exit") });

			// Oriath Square
			var entrance = Search.Tile("/Oriath_AreaTransition_v01_03");
			bool courtsOpen() => LabelShown(TemplarCourts);
			rules.When("open the Templar Courts Entrance", OriathSquare, () => at("Use the Eyes of Zeal") && !courtsOpen(),
				new Activate("Templar Courts Entrance") { Where = OriathSquare, SearchAt = entrance, DoneWhen = courtsOpen });
			rules.When("to The Templar Courts", OriathSquare, () => at("Use the Eyes of Zeal"),
				new TakeTransition(TemplarCourts) { Where = OriathSquare, SearchAt = entrance });

			// The Templar Courts: the reward, in town
			rules.When("to Overseer's Tower for the reward", TemplarCourts, () => AtReward(ktf), new EnterArea(town));

			quests.Handlers.Add(new QuestHandler(ktf, rules) { Active = _ => rules.Holding() != null });
		}

		/// <summary>
		/// Death to Purity:
		///  - Overseer's Tower: "Find High Templar Avarius and kill him": by waypoint to The Templar Courts
		///  - The Templar Courts: to The Chamber of Innocence (the Features/templar_to_innocents tile)
		///  - The Chamber of Innocence: to the Features/transition_chamber_to_boss tile, into the "Sanctum of Innocence", and
		///    kill Avarius (AvoidSpam; he has phases, and names: the fight goes on until the step moves on); "Talk to Sin";
		///    "Talk to Lani for your reward": back out through the door "The Chamber of Innocence" (CrossTransitions) to the
		///    Features/transition_chamber_to_courts tile, and the exit to The Torched Courts (on the way on: the reward is
		///    taken in town later)
		/// TODO: Avarius's phases and their names (here: any unique named Avarius or Innocence)
		/// </summary>
		private void DeathToPurityQuest(FinishAllQuests quests) {
			const string dtp = DeathToPurity;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(5);
			bool find() => StepStarts(dtp, "Find High Templar Avarius");
			var rules = new FirstOf(dtp);

			rules.When("to The Templar Courts", town, find, new EnterArea(TemplarCourts));
			rules.When("to The Chamber of Innocence", TemplarCourts, find,
				new TakeTransition(ChamberOfInnocence) { Where = TemplarCourts, SearchAt = Search.Exit(ChamberOfInnocence, "/templar_to_innocents") });

			// Avarius
			var avarius = UniqueNamedAny("Avarius", "Innocence");
			var sanctumDoor = Search.Tile("/transition_chamber_to_boss");
			bool inSanctum() => CanReach(avarius);
			rules.When("kill Avarius", ChamberOfInnocence, () => find() && inSanctum(), new AvoidSpam("Avarius", avarius, main) { Where = ChamberOfInnocence });
			// (a phase killed, the next not in sight yet: fight on until the step moves on)
			rules.When("the rest of Avarius's fight", ChamberOfInnocence, () => find() && inSanctum(),
				new FightUntil("Avarius", () => !find(), main) { Where = ChamberOfInnocence });
			rules.When("into the Sanctum of Innocence", ChamberOfInnocence, find,
				new TakeTransition("Sanctum of Innocence") { Where = ChamberOfInnocence, SearchAt = sanctumDoor, DoneWhen = inSanctum });

			rules.When("talk to Sin", ChamberOfInnocence, () => StepStarts(dtp, "Talk to Sin"), new TalkTo("Sin", TalkTo.NpcNamed("Sin")) { Where = ChamberOfInnocence });

			// on to The Torched Courts (Lani's reward is taken in town later)
			var courtsExit = Search.Exit(TorchedCourts, "/transition_chamber_to_courts");
			rules.When("out of the Sanctum, to the way to The Torched Courts", ChamberOfInnocence, () => AtReward(dtp),
				new CrossTransitions("the way to The Torched Courts", Search.Tile("/transition_chamber_to_courts"), "/transition_chamber_to_boss") { Where = ChamberOfInnocence });
			rules.When("to The Torched Courts", ChamberOfInnocence, () => AtReward(dtp),
				new TakeTransition(TorchedCourts) { Where = ChamberOfInnocence, SearchAt = courtsExit });

			quests.Handlers.Add(new QuestHandler(dtp, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Sin" },
			});
		}

		/// <summary>Kitava's Torments: the three torments not all had yet, and not delivered (see KitavasTormentsQuest).</summary>
		private bool tormentsSeen;
		private bool TormentsDone() {
			tormentsSeen = tormentsSeen || StepOf(KitavasTorments) != null;
			// (its passive point, as /passives said at the start of the run; or the quest gone from the tracker after being in it)
			return CampaignProgress.HasPassiveFrom(KitavasTorments) || (tormentsSeen && StepOf(KitavasTorments) == null);
		}
		private static int TormentsHad() => Torments.Count(Equipment.HasQuestItem);
		/// <summary>The torments to deliver to Lani in town: all three had, or the tracker says to deliver them (or take the reward).</summary>
		private bool TormentsToDeliver() => !TormentsDone() && (TormentsHad() == 3 || StepStarts(KitavasTorments, "Deliver") || AtReward(KitavasTorments));

		/// <summary>
		/// The Ravenous God:
		///  - The Torched Courts: "Travel back through the Torched Courts": the exit to The Ruined Square (the
		///    Features/templar_oriath_transition tile)
		///  - The Ruined Square: "Find the entrance to the Ossuary": the exit on the Stonewall/Oriath_AreaTransition_v01_04 tile
		///  - The Ossuary: "Search the Ossuary for the Sign of Purity": to the Features/ossuary_quest tile, click the "Tomb of
		///    the First Templar", pick up the Sign of Purity
		///  - "Access the Cathedral Rooftop from the Ruined Square", with the Sign of Purity: in The Ossuary, pull the "Lever"
		///    (when in sight and targetable), else the exit to The Ruined Square (Features/Ossuary_Part2_transition); in The
		///    Ruined Square, while Kitava's Torments is not done, to The Reliquary (Stonewall/Oriath_AreaTransition_v01_02),
		///    or to town with the torments; once done, the exit to The Cathedral Rooftop (Features/chitus_statuewall_transition);
		///    in Overseer's Tower, by waypoint to The Ruined Square
		///  - The Cathedral Rooftop: "Use the Sign of Purity to defeat Kitava": to <1736, 2974>, into the "Cathedral Apex",
		///    click the "Cradle of Purity", and kill Kitava (AvoidSpam); "Talk to Sin": to the
		///    OriathSquare/Canal/CanalDock_v01_01 tile, and talk to Lilly Roth (the way on to Act 6)
		/// </summary>
		private void RavenousGodQuest(FinishAllQuests quests) {
			const string rg = RavenousGod;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(5);
			bool at(string prefix) => StepStarts(rg, prefix);
			bool sign() => Equipment.HasQuestItem("Sign of Purity");
			var rules = new FirstOf(rg);

			rules.When("to The Ruined Square", TorchedCourts, () => at("Travel back through the Torched Courts"),
				new TakeTransition(RuinedSquare) { Where = TorchedCourts, SearchAt = Search.Exit(RuinedSquare, "/templar_oriath_transition") });
			rules.When("to The Ossuary", RuinedSquare, () => at("Find the entrance to the Ossuary"),
				new TakeTransition(Ossuary) { Where = RuinedSquare, SearchAt = Search.Exit(Ossuary, "/Oriath_AreaTransition_v01_04") });

			// The Ossuary: the Sign of Purity
			bool search() => at("Search the Ossuary") && !sign();
			var tomb = Search.Tile("/ossuary_quest");
			rules.When("pick up the Sign of Purity", Ossuary, () => search() && ItemShown("Sign of Purity"), new PickUp("Sign of Purity") { Where = Ossuary });
			rules.When("open the Tomb of the First Templar", Ossuary, search,
				new Activate("Tomb of the First Templar") { Where = Ossuary, SearchAt = tomb, DoneWhen = () => ItemShown("Sign of Purity") });
			rules.When("pick up the Sign of Purity", Ossuary, search, new PickUpQuestItem("Sign of Purity") { Where = Ossuary, SearchAt = tomb });

			// "Access the Cathedral Rooftop from the Ruined Square"
			bool access() => at("Access the Cathedral Rooftop") && sign();
			rules.When("pull the Lever", Ossuary, () => access() && TargetableLabelShown("Lever"), new Activate("Lever") { Where = Ossuary });
			rules.When("back to The Ruined Square", Ossuary, access,
				new TakeTransition(RuinedSquare) { Where = Ossuary, SearchAt = Search.Exit(RuinedSquare, "/Ossuary_Part2_transition") });
			rules.When("to town with Kitava's Torments", RuinedSquare, () => access() && TormentsToDeliver(), new EnterArea(town));
			rules.When("to The Reliquary for Kitava's Torments", RuinedSquare, () => access() && !TormentsDone(),
				new TakeTransition(Reliquary) { Where = RuinedSquare, SearchAt = Search.Exit(Reliquary, "/Oriath_AreaTransition_v01_02") });
			rules.When("to The Cathedral Rooftop", RuinedSquare, access,
				new TakeTransition(CathedralRooftop) { Where = RuinedSquare, SearchAt = Search.Exit(CathedralRooftop, "/chitus_statuewall_transition") });
			rules.When("to The Ruined Square", town, () => access() && !TormentsToDeliver(), new EnterArea(RuinedSquare));

			// The Cathedral Rooftop: Kitava
			bool defeat() => at("Use the Sign of Purity");
			var kitava = UniqueNamed("Kitava");
			bool inApex() => CanReach(kitava) || LabelReachable("Cradle of Purity");
			rules.When("kill Kitava", CathedralRooftop, () => defeat() && CanReach(kitava), new AvoidSpam("Kitava", kitava, main) { Where = CathedralRooftop });
			rules.When("click the Cradle of Purity", CathedralRooftop, () => defeat() && LabelReachable("Cradle of Purity"),
				new Activate("Cradle of Purity") { Where = CathedralRooftop, DoneWhen = () => CanReach(kitava) });
			// (Kitava killed, or not out yet: fight on until the step moves on)
			rules.When("the rest of Kitava's fight", CathedralRooftop, () => defeat() && inApex(),
				new FightUntil("Kitava", () => !defeat(), main) { Where = CathedralRooftop });
			rules.When("into the Cathedral Apex", CathedralRooftop, defeat,
				new TakeTransition("Cathedral Apex") { Where = CathedralRooftop, SearchAt = Search.At(1736, 2974), DoneWhen = inApex });
			rules.When("talk to Lilly Roth", CathedralRooftop, () => at("Talk to Sin"),
				new TalkTo("Lilly Roth", TalkTo.NpcNamed("Lilly Roth")) { Where = CathedralRooftop, SearchAt = Search.Tile("/CanalDock_v01") });

			quests.Handlers.Add(new QuestHandler(rg, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Sin", "Lilly Roth" },
			});
		}

		/// <summary>
		/// Kitava's Torments (done: its passive point, in /passives): in The Reliquary, "Find Kitava's Torments": with fewer
		/// than three of them (Valako's Jaw, Tukohama's Tooth, Hinekora's Hair), explore for the three "Karui Relic Case"s and
		/// open them (picking up what they drop); "Deliver Kitava's Torments to Lani": to Overseer's Tower (the talk is done
		/// there by the town's quest givers).
		/// TODO: the relic cases' metadata path and tile (not seen yet: any quest chest, found by exploring)
		/// </summary>
		private void KitavasTormentsQuest(FinishAllQuests quests) {
			const string kt = KitavasTorments;
			string town = TownOf(5);
			var rules = new FirstOf(kt);
			rules.When("open the Karui Relic Cases", Reliquary, () => !TormentsDone() && TormentsHad() < 3 && !TormentsToDeliver(),
				new OpenQuestChests("Karui Relic Case", "/karui_relic", "/QuestChests/", 3) { Where = Reliquary, DoneWhen = () => TormentsHad() == 3 });
			rules.When("to Overseer's Tower with the torments", Reliquary, TormentsToDeliver, new EnterArea(town));
			quests.Handlers.Add(new QuestHandler(kt, rules) {
				Active = _ => rules.Holding() != null,
				// (The Ravenous God sends the player here before this quest may be in the tracker)
				Untracked = () => rules.Holding() != null,
			});
		}
	}
}
