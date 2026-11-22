using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Plays the campaign, Build-neutral: one master list of goals (Root), each of which checks itself.
	///
	///   Complete the campaign (done: the AreaInstanceUI offers the Epilogue)
	///     Read the campaign progress (at the start of each run: open the AreaInstanceUI, read it, close it)
	///     Complete Part 1 (done: "Part 2" is offered)
	///       Complete Act 1 (done: "Act 2" is offered, or the player is in a later act)
	///         Exit The Twilight Strand (done: not in The Twilight Strand)
	///         Finish all quests (from the quest tracker; in town, talk to the NPCs with something to say)
	///         Route to The Cavern of Anger (the waypoint "Merveil's Caverns" is in The Cavern of Wrath, before it), kill Merveil, enter Act 2
	///       Complete Act 2: route to The Ancient Pyramid, up the stairs to the Pyramid Apex, the Dark Altar, its boss, on to Act 3
	///       Complete Acts 3, 4, 5 (not specified yet)
	///     Complete Part 2: Acts 6 to 10 (not specified yet)
	///
	/// A goal that is done is skipped without looking at what is under it, so nothing is keyed to the area the player
	/// is in, and nothing needs saving: a new run reads the progress, and goes on from there. Goals that have to be done
	/// somewhere say so (Goal.Where), and travel there first. What to socket, bind and take as rewards comes from the
	/// Build section's PoB build (see BuildPlan, RewardChooser): its "Level 1" skill set names the starting skill and support.
	///
	/// The running (choosing the leaf goal, ticking its chain, looting, dying, the on-screen list) is GoalRoutine's.
	/// </summary>
	public partial class CampaignRoutine : GoalRoutine {
		public override string Name => "Campaign";

		/// <summary>Rewards to take before what the Build wants (see RewardChooser): "Quest: reward, reward; Quest: reward".</summary>
		public string RewardOverrides = "";

		public CampaignRoutine() {
			// equipment only when rare (or unique), or for a slot with nothing in it
			Looter.Want = Looter.RareOrEmptySlot;
		}

		protected override Sequence BuildRoot() => Campaign();

		private IReadOnlyDictionary<string, string> Overrides() => RewardChooser.ParseOverrides(RewardOverrides);

		// ---- the master list ----

		private const string TwilightStrand = "The Twilight Strand", LioneyesWatch = "Lioneye's Watch", TheCoast = "The Coast",
			MudFlats = "The Mud Flats", TidalIsland = "The Tidal Island", FloodedDepths = "The Flooded Depths", AncientPyramid = "The Ancient Pyramid",
			PrisonersGate = "Prisoner's Gate", SubmergedPassage = "The Submerged Passage", WesternForest = "The Western Forest", UpperPrison = "The Upper Prison",
			ShipGraveyard = "The Ship Graveyard", ShipGraveyardCave = "The Ship Graveyard Cave",
			LowerPrison = "The Lower Prison", BrokenBridge = "The Broken Bridge", Wetlands = "The Wetlands", Den = "The Den",
			ChamberOfSins2 = "The Chamber of Sins Level 2", WeaversChambers = "The Weaver's Chambers",
			Crypt2 = "The Crypt Level 2", VaalRuins = "The Vaal Ruins", NorthernForest = "The Northern Forest", Caverns = "The Caverns",
			CityOfSarn = "The City of Sarn", SarnEncampment = "The Sarn Encampment", Slums = "The Slums", Crematorium = "The Crematorium",
			Sewers = "The Sewers", Docks = "The Docks", SolarisTemple2 = "The Solaris Temple Level 2", EbonyBarracks = "The Ebony Barracks",
			LunarisTemple2 = "The Lunaris Temple Level 2", ImperialGardens = "The Imperial Gardens",
			SceptreOfGod = "The Sceptre of God", UpperSceptreOfGod = "The Upper Sceptre of God", Aqueduct = "The Aqueduct";

		/// <summary>Each act's town, as WorldArea names it with its act (several share a name).</summary>
		public static string TownOf(int act) => act switch {
			1 => "Lioneye's Watch (Act 1)", 2 => "The Forest Encampment (Act 2)", 3 => "The Sarn Encampment (Act 3)",
			4 => "Highgate (Act 4)", 5 => "Overseer's Tower (Act 5)", 6 => "Lioneye's Watch (Act 6)",
			7 => "The Bridge Encampment (Act 7)", 8 => "The Sarn Encampment (Act 8)", 9 => "Highgate (Act 9)",
			10 => "Oriath Docks (Act 10)", _ => null,
		};

		private Sequence Campaign() => new Sequence("Complete the campaign",
			new ReadCampaignProgress(),
			new ReadGameKeys(), // (the inventory key, for TrySyncInventory; the Portal Scroll key)
			new Sequence("Complete Part 1", Act1(), Act2(), Act3(), Act4(), Act5()) { DoneWhen = () => CampaignProgress.Part1Done },
			new Sequence("Complete Part 2", Act6(), Act(7), Act(8), Act(9), Act(10)) { DoneWhen = () => CampaignProgress.CampaignDone }
		) { DoneWhen = () => CampaignProgress.CampaignDone };

		// an act whose goals are not written yet
		private static Sequence Act(int n) => new Sequence($"Complete Act {n}", new NotSpecified($"Complete Act {n}")) { DoneWhen = () => CampaignProgress.ActDone(n) };

		private Sequence Act1() {
			Func<string> skill = BuildPlan.StartingSkill, support = BuildPlan.StartingSupport, main = BuildPlan.MainSkill;
			var quests = Quests(1);
			return new Sequence("Complete Act 1",
				new Sequence("Exit The Twilight Strand",
					new ClickButton("Skip All Tutorials", () => GetUI()?.SkipAllTutorials),
					new PickUp("a weapon", Equipment.IsWeapon) { DoneWhen = Equipment.HasWeapon },
					new BindSkill("Default Attack", BuildPlan.MainSlot),
					new PickUp("Dying Exile", "Click"),
					new Kill("the Dying Exile's zombie", "Monsters/Zombies/ZombieBite", "Default Attack", range: 25),
					new PickUp("the starting skill gem", Equipment.IsSkillGem) { Proves = true, DoneWhen = () => Equipment.OwnedGems().Any(Equipment.IsSkillGem) },
					new SocketGem(skill),
					new BindSkill(skill, BuildPlan.MainSlot),
					// on the way to Hillock, who stands in the only way out: walk past the monsters
					new PickUp("Large Chest", "Open") { SearchAt = Search.TheOnlyExit },
					new PickUp("the starting support gem", Equipment.IsSupportGem) { SearchAt = Search.TheOnlyExit, Proves = true, DoneWhen = () => Equipment.OwnedGems().Any(Equipment.IsSupportGem) },
					new SocketGem(support, skill),
					new Orbit("Hillock", "Hillock", skill) { SearchAt = Search.TheOnlyExit },
					new EnterArea(LioneyesWatch)
				) { DoneWhen = () => GetCurrentArea() is WorldArea here && !GoToArea.Is(here, TwilightStrand) },
				quests,
				new Sequence("Route to The Cavern of Anger",
					// (TravelTo takes the waypoint "Merveil's Caverns", to The Cavern of Wrath, if it can, and walks on)
					new RouteTo("The Cavern of Anger"),
					// her arena is through the door labelled "Merveil's Lair", on the Cave/Features/caveup_exit tile (3.29.4): walk
					// straight there (no exploring), take it, and kill her inside; neither is done while The Siren's Cadence says
					// "Kill Merveil" (her summons dying once marked her killed)
					// (the door teleports within The Cavern of Anger: in the arena is told by where the player can walk, see InMerveilsLair)
					new TakeTransition(MerveilsLair) { Where = "The Cavern of Anger", SearchAt = Search.Tile("/caveup_exit"),
						NotDoneWhile = () => MustKillMerveil() && !InMerveilsLair(), DoneWhen = InMerveilsLair },
					// (she covers the ground near her with damaging areas, one kind in each phase: stay out of them, see AvoidSpam)
					new AvoidSpam("Merveil", IsMerveil, main) { Where = "The Cavern of Anger", NotDoneWhile = MustKillMerveil },
					new EnterArea("The Southern Forest"))
			) { DoneWhen = () => CampaignProgress.ActDone(1) };
		}

		// an act's quests, with the gear kept up in town: in town, talk to every NPC with a quest marker over its head, and
		// to the NPC a quest step "Talk to X" names (eg, The Way Forward's "Talk to Silk"; see Town.RewardNpc)
		private FinishAllQuests Quests(int act) {
			var quests = new FinishAllQuests(act, TownOf(act), Overrides);
			quests.Upkeep.Add(new UseBooks());
			quests.Upkeep.Add(new EquipFlask("Quicksilver Flask", 4));
			quests.Upkeep.Add(new ArrangeGems());
			quests.Upkeep.Add(new BindPlannedSkills());
			AddQuestHandlers(quests);
			return quests;
		}

		private Sequence Act2() {
			Func<string> main = BuildPlan.MainSkill;
			return new Sequence("Complete Act 2",
				Quests(2),
				// (Shadow of the Vaal does it, while in the tracker: this is for when it is not)
				new Sequence("Route to The City of Sarn", new Goal[] { new RouteTo(AncientPyramid) }.Concat(PyramidGoals(main)).ToArray())
			) { DoneWhen = () => CampaignProgress.ActDone(2) };
		}

		// Act 3: its quests (the ones written so far), then the rest, not written yet
		private Sequence Act3() => new Sequence("Complete Act 3", Quests(3), new NotSpecified("the rest of Act 3")) { DoneWhen = () => CampaignProgress.ActDone(3) };

		// Act 4: its quests (The Eternal Nightmare, then Return to Oriath, whose Oriath Portal ends the act; see Act4Quests)
		// (the quests with nothing to do for a moment, eg a step the tracker has not updated yet, is not the end: wait a while
		// before stopping, as FinishAllQuests is asked again each time and comes back once a step shows)
		private Sequence Act4() => new Sequence("Complete Act 4", Quests(4), StopAfter(10000, "the way on to Act 5 (the quests did not get there)")) {
			DoneWhen = () => CampaignProgress.ActDone(4) };

		// Act 5: its quests (see Act5Quests; The Ravenous God's last step, talking to Lilly Roth, is the way on to Act 6)
		private Sequence Act5() => new Sequence("Complete Act 5", Quests(5), StopAfter(10000, "the way on to Act 6 (the quests did not get there)")) {
			DoneWhen = () => CampaignProgress.ActDone(5) };

		// Act 6: its quests (see Act6Quests; The Brine King's last step, talking to Weylam Roth, is the way on to Act 7)
		private Sequence Act6() => new Sequence("Complete Act 6", Quests(6), StopAfter(10000, "the way on to Act 7 (the quests did not get there)")) {
			DoneWhen = () => CampaignProgress.ActDone(6) };

		/// <summary>Stand still for ms (counted again each time it is come back to), then stop the routine, as NotSpecified does.</summary>
		private static Goal StopAfter(long ms, string what) {
			long since = 0, lastStep = 0;
			return new OneOff($"{what} (not specified yet)", _ => false, (self, ctx) => {
				long now = Time.ElapsedMilliseconds;
				if ( now - lastStep > 1000 ) since = now; // (not worked on lately: start counting again)
				lastStep = now;
				if ( now - since < ms ) {
					ctx.Driver.StopWalking("waiting for the quest tracker");
					self.Detail = $"nothing to do: waiting {(ms - (now - since)) / 1000} s for the quest tracker";
					return self;
				}
				ctx.Driver.Stop($"the goals for \"{what}\" are not specified yet");
				return self;
			});
		}

		private static bool IsHailrake(Entity e) => e.Path is string path && path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)
			&& string.Equals(e.GetComponent<Render>()?.Name, "Hailrake", StringComparison.OrdinalIgnoreCase);
		private const string MerveilsLair = "Merveil's Lair";
		/// <summary>
		/// In Merveil's arena: the door "Merveil's Lair" teleports within The Cavern of Anger (3.29.4, the area keeps its
		/// name), so it is told apart by where the player can walk: not to the way back to The Cavern of Wrath (the
		/// Cave/Features/cavedown_exit tile; the arena is walled off from it). Or Merveil herself is in sight.
		/// </summary>
		private static bool InMerveilsLair() {
			if ( !GoToArea.Is(GetCurrentArea(), "The Cavern of Anger") ) return false;
			if ( GetEntities()?.Any(e => e != null && IsMerveil(e)) ?? false ) return true;
			var terrain = GetTerrain();
			var pos = GetPlayer()?.GetComponent<Positioned>();
			if ( terrain == null || !IsValid(pos) ) return false;
			var back = terrain.TileFeatures("/cavedown_exit").FirstOrDefault();
			return back != null && !DefaultNavigator.IsReachable(terrain, pos.GridPos, back.GridPos);
		}
		private static bool MustKillMerveil() =>
			Town.QuestStep("The Siren's Cadence")?.IndexOf("Kill Merveil", StringComparison.OrdinalIgnoreCase) >= 0;

		// Merveil herself (a unique: "Merveil, the Siren", seawitchillusion/BossMerveil, then in her second phase "Merveil,
		// the Twisted", Seawitch/BossMerveil2, a new entity at the same place): not her summons, "Merveil's Attendant", "Merveil's
		// Daughter" (Seawitch/SeaWitchSpawn), whose deaths marked her killed (3.29.4)
		private static bool IsMerveil(Entity e) => e.Path is string path && path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)
			&& e.GetComponent<Render>()?.Name is string name && name.StartsWith("Merveil", StringComparison.OrdinalIgnoreCase)
			&& !name.StartsWith("Merveil's", StringComparison.OrdinalIgnoreCase)
			&& e.GetComponent<ObjectMagicProperties>()?.Rarity == Offsets.MonsterRarity.Unique;


		// ---- running, and showing it ----

		protected override void OnStartRun() => CampaignProgress.Forget();

		public override string ProgressSummary => CampaignProgress.Summary;
		protected override string CompleteText => "(the campaign is complete)";

		/// <remarks>
		/// Until the campaign progress is read, only that goal (nothing is known yet about what is left). Then a Sequence
		/// that is done (a part, an act, "Exit The Twilight Strand", a quest) shows as one line, not its goals.
		/// </remarks>
		public override IEnumerable<(Sequence parent, Goal goal, int depth)> Leaves() {
			if ( !CampaignProgress.Scanned ) return Root.Goals.OfType<ReadCampaignProgress>().Select(g => (Root, (Goal)g, 0));
			return base.Leaves();
		}

		protected override void RenderSettings() {
			ImGui.InputText("Reward Overrides", ref RewardOverrides, 500);
			ImGui.SameLine();
			ImGui_HelpMarker("Quest rewards to take before what the Build wants, eg \"Enemy at the Gate: Caustic Arrow; Mercy Mission: Quicksilver Flask\". Otherwise: a gem the Build uses, else a Quicksilver Flask, else the first offered.");
			var problem = BuildPlan.Problem();
			if ( problem != null ) ImGui.TextColored(new Vector4(1, .4f, .4f, 1), $"Build: {problem}");
			else ImGui.Text($"Starting skill: {BuildPlan.StartingSkill()}, support: {BuildPlan.StartingSupport()}; main skill: {BuildPlan.MainSkill()}");
			ImGui.Text($"Progress: {CampaignProgress.Summary}; in act {CampaignProgress.CurrentAct}");
		}
	}
}
