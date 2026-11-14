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
	/// How it runs: four times a second it chooses the leaf goal (Sequence.Select). Each frame it ticks that goal as a
	/// state (CampaignRunner): the goal does one thing, or hands its work to sub-states (TravelTo, Navigate, interrupted
	/// by Fight, ...) and input states (Inputs: a key press, a click, spread over frames), which hand back to it. Input
	/// the driver queues (eg, using a skill) goes into the chain the same way. If the goal chosen changes, the chain is
	/// cancelled. Dying: resurrect, and start the current goal again.
	///
	/// On screen: the previous 3 goals, the current one (and what it is running), and the next 6. In the Combat section:
	/// the master list, and the running chain, which can be opened in an Object Browser.
	/// </summary>
	public class CampaignRoutine : CombatDriver {
		public override string Name => "Campaign";

		public bool ShowObjectives = true;
		/// <summary>Pick up the items that show a label on the ground (see Looter), when no monster is within AttackRange.</summary>
		public bool PickUpItems = true;
		/// <summary>The key the game uses a Portal Scroll with (None: right click one in the backpack instead).</summary>
		public HotKey PortalScrollKey = new HotKey(VKey.None);
		/// <summary>Rewards to take before what the Build wants (see RewardChooser): "Quest: reward, reward; Quest: reward".</summary>
		public string RewardOverrides = "";

		/// <summary>The master list.</summary>
		[NoPersist] public readonly Sequence Root;
		[NoPersist] public readonly CampaignRunner Runner = new CampaignRunner();
		protected readonly Looter Looter = new Looter();
		protected readonly FlaskUpgrader FlaskUpgrader = new FlaskUpgrader();
		protected readonly AuraKeeper AuraKeeper = new AuraKeeper();

		/// <summary>The leaf goal chosen (null if all are done).</summary>
		public Goal CurrentGoal { get; private set; }

		private uint areaHash;
		private long lastSelect;
		private bool wasActive;

		public CampaignRoutine() {
			// an item a goal is to pick up is left to it, so it sees the item go and knows it is done
			Looter.Leave = text => CurrentGoal is PickUp p && p.LabelText != null && text.IndexOf(p.LabelText, StringComparison.OrdinalIgnoreCase) >= 0;
			// equipment only when rare (or unique), or for a slot with nothing in it
			Looter.Want = Looter.RareOrEmptySlot;
			Looter.PickedUp = path => FlaskUpgrader.PickedUp(path);
			Root = Campaign();
			Root.AssignIds();
		}

		protected override bool TakesInput => true;

		private IReadOnlyDictionary<string, string> Overrides() => RewardChooser.ParseOverrides(RewardOverrides);

		// ---- the master list ----

		private const string TwilightStrand = "The Twilight Strand", LioneyesWatch = "Lioneye's Watch", TheCoast = "The Coast",
			MudFlats = "The Mud Flats", TidalIsland = "The Tidal Island", FloodedDepths = "The Flooded Depths", AncientPyramid = "The Ancient Pyramid",
			PrisonersGate = "Prisoner's Gate";

		/// <summary>Each act's town, as WorldArea names it with its act (several share a name).</summary>
		public static string TownOf(int act) => act switch {
			1 => "Lioneye's Watch (Act 1)", 2 => "The Forest Encampment (Act 2)", 3 => "The Sarn Encampment (Act 3)",
			4 => "Highgate (Act 4)", 5 => "Overseer's Tower (Act 5)", 6 => "Lioneye's Watch (Act 6)",
			7 => "The Bridge Encampment (Act 7)", 8 => "The Sarn Encampment (Act 8)", 9 => "Highgate (Act 9)",
			10 => "Oriath Docks (Act 10)", _ => null,
		};

		private Sequence Campaign() => new Sequence("Complete the campaign",
			new ReadCampaignProgress(),
			new Sequence("Complete Part 1", Act1(), Act2(), Act(3), Act(4), Act(5)) { DoneWhen = () => CampaignProgress.Part1Done },
			new Sequence("Complete Part 2", Act(6), Act(7), Act(8), Act(9), Act(10)) { DoneWhen = () => CampaignProgress.CampaignDone }
		) { DoneWhen = () => CampaignProgress.CampaignDone };

		// an act whose goals are not written yet
		private static Sequence Act(int n) => new Sequence($"Complete Act {n}", new NotSpecified($"Complete Act {n}")) { DoneWhen = () => CampaignProgress.ActDone(n) };

		private Sequence Act1() {
			Func<string> skill = BuildPlan.StartingSkill, support = BuildPlan.StartingSupport, main = BuildPlan.MainSkill;
			var quests = Quests(1);
			quests.Handlers.Add(new QuestHandler("Breaking Some Eggs", new Sequence("Breaking Some Eggs: the glyphs",
				// each nest drops a glyph: Haliotis (Glyph1), Ammonite (Glyph2), Roseus (Glyph3)
				new OpenQuestChests("Rhoa Nest", "rhoanest", "QuestChests/Rhoa", 3) { Where = MudFlats, FightRadius = 100, ItemPathPart = "QuestItems/Glyphs/" },
				// with the three glyphs, the wall opens the exit beside it
				new UseQuestObject("Strange Glyph Wall", "WaterCave/GlyphWall", "watercave", "WaterCave/WaterCaveAreaTransition") { Where = MudFlats })));
			quests.Handlers.Add(new QuestHandler("Mercy Mission", new Sequence("Mercy Mission: Hailrake's quest item",
				// (The Tidal Island has no waypoint: by waypoint to The Coast, and walk)
				new Orbit("Hailrake", IsHailrake, main) { Where = TidalIsland },
				new PickUpQuestItem("Hailrake's quest item") { Where = TidalIsland },
				new PickUp("Portal Scroll") { DoneWhen = () => Equipment.InBackpack(Offsets.PATH_SCROLL_PORTAL) },
				new UsePortalScroll(LioneyesWatch))));
			// "Kill the Dweller of the Deep": he is in The Flooded Depths (the quest tracker says to travel there first)
			quests.Handlers.Add(new QuestHandler("The Dweller of the Deep", new Sequence("The Dweller of the Deep",
				new Orbit("the Dweller of the Deep", IsDweller, main) { Where = FloodedDepths })));
			// "Follow the Emperor's Road": walk to within 50 cells of Prisoner's Gate's Beach/Transitions/beach_passageblock tile
			quests.Handlers.Add(new QuestHandler("The Way Forward", new Sequence("The Way Forward: the Emperor's Road",
				new ApproachTiles("the Emperor's Road", "/beach_passageblock") { Where = PrisonersGate, Reach = 90 })) {
				Active = step => step != null && step.IndexOf("Follow the Emperor's Road", StringComparison.OrdinalIgnoreCase) >= 0,
			});

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
			return quests;
		}

		private Sequence Act2() {
			Func<string> main = BuildPlan.MainSkill;
			return new Sequence("Complete Act 2",
				Quests(2),
				new Sequence("Route to The Ancient Pyramid",
					new RouteTo(AncientPyramid),
					new TakeTransition("Stairs") { Where = AncientPyramid },
					new TakeTransition("Stairs") { Where = AncientPyramid },
					new TakeTransition("Stairs") { Where = AncientPyramid },
					new TakeTransition("Pyramid Apex") { Where = AncientPyramid },
					new Activate("Dark Altar"),
					new Kill("the Vaal Oversoul", "BossIncaShadow", null),
					new EnterArea("The City of Sarn"))
			) { DoneWhen = () => CampaignProgress.ActDone(2) };
		}

		private static bool IsHailrake(Entity e) => e.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& string.Equals(e.GetComponent<Render>()?.Name, "Hailrake", StringComparison.OrdinalIgnoreCase);
		private static bool IsDweller(Entity e) => e.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& (e.GetComponent<Render>()?.Name?.Contains("Dweller of the Deep", StringComparison.OrdinalIgnoreCase) ?? false);
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
		private static bool IsMerveil(Entity e) => e.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& e.GetComponent<Render>()?.Name is string name && name.StartsWith("Merveil", StringComparison.OrdinalIgnoreCase)
			&& !name.StartsWith("Merveil's", StringComparison.OrdinalIgnoreCase)
			&& e.GetComponent<ObjectMagicProperties>()?.Rarity == Offsets.MonsterRarity.Unique;

		// ---- running ----

		public override IState OnTick(long dt) {
			if ( PoEMemory.IsAttached ) {
				if ( IsActive && !wasActive ) StartRun();
				wasActive = IsActive;
				ForgetOnNewArea();
				RestartAfterResurrect();
				if ( !IsActive ) SelectWhileOff();
				if ( ShowObjectives ) DrawObjectives();
			}
			return base.OnTick(dt);
		}

		// a new run: read the progress again, and choose from the top
		private void StartRun() {
			Log($"{Name}: a new run");
			CampaignProgress.Forget();
			Root.Forget();
			StartGoalOver();
		}

		// a new area instance: what the goals were doing there is gone (not whether they are done)
		private void ForgetOnNewArea() {
			uint hash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			if ( hash == areaHash ) return;
			areaHash = hash;
			StartGoalOver();
		}

		private void StartGoalOver() {
			Runner.Cancel();
			ForgetInput();
			lastSelect = 0;
			Looter.Reset();
			FlaskUpgrader.Reset();
			AuraKeeper.Reset();
			Root.Reset();
		}

		// choose the leaf goal; start its chain if it changed
		private void Select(CombatContext ctx) {
			CurrentGoal = Root.Select(ctx);
			if ( CurrentGoal != Runner.Leaf ) {
				if ( Runner.Leaf != null ) Log($"{Name}: now on {CurrentGoal?.Name ?? "(nothing)"} (was {Runner.Leaf.Name})");
				Runner.Run(CurrentGoal);
				ForgetInput();
			}
		}

		// while off: keep the list up to date (for the display), without running anything
		private void SelectWhileOff() {
			if ( Time.ElapsedMilliseconds - lastSelect < 500 ) return;
			lastSelect = Time.ElapsedMilliseconds;
			var game = PoEMemory.GameRoot?.InGameState;
			var terrain = GetTerrain();
			var player = GetPlayer();
			var pos = player?.GetComponent<Positioned>();
			if ( game == null || terrain == null || !IsValid(pos) ) return;
			Runner.Cancel();
			CurrentGoal = Root.Select(new CombatContext(this, game, terrain, player, pos.GridPos, Position(player).Z));
		}

		protected override void Decide(CombatContext ctx) {
			try {
				DecideGoal(ctx);
			} finally {
				// input queued this frame (eg, a skill used by a Fight, an item clicked by the Looter) goes into the chain
				Runner.Push(TakeInput());
			}
		}

		private void DecideGoal(CombatContext ctx) {
			// input going out (any state in the chain that is not the campaign's own: input states, Delays, the end of
			// input that clears InputBusy): let it finish before anything else (choosing again, looting), so a goal change
			// does not cut a key press in half, and the looter does not wait forever on InputBusy
			if ( Runner.Current != null && !(Runner.Current is CampaignState) ) {
				if ( Runner.Current != stuckState ) { stuckState = Runner.Current; stuckSince = Time.ElapsedMilliseconds; }
				else if ( Time.ElapsedMilliseconds - stuckSince > MaxInputMs ) {
					// input never takes this long (eg, a modifier key that can't be sent while the game is not focused)
					Log($"{Name}: {Runner.Current.Name} did not go out in {MaxInputMs} ms: starting {CurrentGoal?.Name ?? "the goal"} over");
					Runner.Cancel();
					ForgetInput();
					lastSelect = 0;
					return;
				}
				Runner.Tick(ctx, FrameDt);
				return;
			}
			stuckState = null;
			// the augury card selection window (opened by clicking a frozen augury boss): apply the cards and confirm first
			// (its clicks go into the chain, see Decide)
			if ( TryAugury() ) return;
			if ( TryLevelGems(ctx) ) return;
			if ( Runner.Current == null || Time.ElapsedMilliseconds - lastSelect > 250 ) {
				lastSelect = Time.ElapsedMilliseconds;
				Select(ctx);
			}
			var goal = CurrentGoal;
			if ( goal == null ) {
				// all done: what a combat driver does
				if ( !TryUpgradeFlask(ctx) && !TryKeepAuras(ctx) && !TryLoot(ctx) && !TrySelectFate(ctx) ) base.Decide(ctx);
				return;
			}
			SetStatus(goal.Name);
			// a bigger life or mana flask picked up: in place of the smaller one (it opens the inventory, so before closing it, below)
			if ( !goal.UsesInventory && !goal.IgnoreMonsters && TryUpgradeFlask(ctx) ) { SetStatus($"{goal.Name}: {FlaskUpgrader.Status}"); return; }
			// a reservation skill of the build's (eg, Clarity) off: on again
			if ( !goal.UsesInventory && !goal.IgnoreMonsters && !BackpackIsOpen() && TryKeepAuras(ctx) ) { SetStatus($"{goal.Name}: {AuraKeeper.Status}"); return; }
			if ( !goal.UsesInventory && BackpackIsOpen() && !(Runner.Current is CampaignState s && s != goal) ) {
				// eg, left open after socketing a gem: it covers the screen
				StopWalking("closing the inventory");
				if ( Time.ElapsedMilliseconds - lastInventoryClose > 700 ) {
					lastInventoryClose = Time.ElapsedMilliseconds;
					Send(Inputs.Press(VKey.I));
				}
				return;
			}
			if ( !goal.UsesInventory && !goal.IgnoreMonsters && TryLoot(ctx) ) return;
			// an AuguryCardObject's "Select a Fate" (its window is worked on by TryAugury, above)
			if ( !goal.UsesInventory && !goal.IgnoreMonsters && TrySelectFate(ctx) ) { SetStatus($"{goal.Name}: {Status}"); return; }
			if ( !Runner.Tick(ctx, FrameDt) ) lastSelect = 0; // the goal handed back (done): choose again next frame
			var top = Runner.Stack().OfType<CampaignState>().FirstOrDefault();
			if ( top != null && top != goal ) SetStatus($"{goal.Name}: {top}");
			else if ( !string.IsNullOrEmpty(goal.Detail) ) SetStatus($"{goal.Name}: {goal.Detail}");
		}
		private long lastInventoryClose;
		/// <summary>An input state current for longer than this is given up on (the chain is cancelled).</summary>
		public long MaxInputMs = 3000;
		private IState stuckState;
		private long stuckSince;

		// for fights where the goal names no skill
		protected override void Attack(Hostile target, Vector2 screenPos) => UseSkillPaced(BuildPlan.MainSkill(), screenPos);

		// ---- dying ----

		public const string ResurrectText = "Resurrect at Checkpoint";
		private long lastResurrectClick;
		private bool resurrecting;

		// dead: click "Resurrect at Checkpoint" (back at the area's entrance), and once alive, start the current goal over
		protected override bool OnPlayerDead() {
			resurrecting = true;
			Runner.Cancel();
			ForgetInput();
			var button = FindResurrectButton();
			if ( button == null ) { SetStatus($"dead: no \"{ResurrectText}\" button"); return true; }
			SetStatus($"dead: clicking \"{ResurrectText}\"");
			if ( Time.ElapsedMilliseconds - lastResurrectClick > 1500 && PoEMemory.TargetHasFocus ) {
				lastResurrectClick = Time.ElapsedMilliseconds;
				Run(Inputs.Click(button.GetClientRect())); // (the chain is not ticked while dead)
			}
			return true;
		}

		private void RestartAfterResurrect() {
			if ( !resurrecting || !IsAlive(GetPlayer()) ) return;
			resurrecting = false;
			Log($"{Name}: resurrected, starting {CurrentGoal?.Name ?? "the current goal"} over");
			StartGoalOver();
		}

		/// <summary>
		/// The visible "Resurrect at Checkpoint" button, or null. 3.29.3: the "You have died" panel is UIRoot.Children[1].Children[142]:
		/// [0] is the text "You have died!", [1] resurrect in town, [2] spectate (hidden), [3] resurrect at checkpoint
		/// (each button's label is its child). The panel is found by its first child's text (index 142 first), else the
		/// button by its text anywhere in the UI tree.
		/// </summary>
		public static Element FindResurrectButton() {
			var root = PoEMemory.GameRoot?.InGameState?.UIRoot;
			if ( !IsValid(root) ) return null;
			var panels = root.GetChild(1);
			if ( IsValid(panels) ) {
				static bool IsDeathPanel(Element p) => IsValid(p) && p.IsVisibleLocal
					&& string.Equals(p.GetChild(0)?.Text?.Trim(), "You have died!", StringComparison.OrdinalIgnoreCase);
				var panel = panels.GetChild(142);
				if ( !IsDeathPanel(panel) ) panel = panels.Children.FirstOrDefault(IsDeathPanel);
				var button = panel?.GetChild(3);
				if ( IsValid(button) && button.IsVisible ) return button;
			}
			// the button by its text, breadth first: the death panel is shallow, but the UI tree is big
			return UiSearch.Visible(root, 12, 20000).FirstOrDefault(e => string.Equals(e.Text?.Trim(), ResurrectText, StringComparison.OrdinalIgnoreCase)
				&& e.GetClientRect() is RectangleF r && r.Width > 0 && r.Height > 0);
		}

		// pick up an item, if there is one; true if doing so, or fighting the monsters near it (or near the player) first.
		// ---- leveling gems ----

		/// <summary>How often to click a gem level-up button (the panel takes a moment to update after one).</summary>
		public long GemLevelClickMs = 1000;
		private long lastGemLevel;

		/// <summary>
		/// Level up gems as they become ready: with no monsters within AttackRange, click the first visible button on the
		/// GemLvlUpPanel (depth first, in child order: "level up all" when it shows, else the only gem's own). At most once
		/// every GemLevelClickMs. True if it clicked.
		/// </summary>
		private bool TryLevelGems(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( now - lastGemLevel < GemLevelClickMs || InputBusy ) return false;
			var panel = GetUI()?.GemLvlUpPanel;
			if ( !IsValid(panel) || !panel.IsVisible ) return false;
			if ( ctx.Hostiles.Any(h => h.Distance <= AttackRange) ) return false;
			var button = FirstButton(panel, 10);
			if ( button == null ) return false;
			var rect = button.GetClientRect();
			if ( rect.Width <= 0 || rect.Height <= 0 ) return false;
			StopWalking("leveling up a gem");
			DefaultFollower.ReleaseMouse();
			if ( !Send(Inputs.Click(rect)) ) return false;
			lastGemLevel = now;
			SetStatus("leveling up a gem");
			Log($"{Name}: clicked a gem level-up button at {rect}");
			return true;
		}

		// the first visible button under e (e included), depth first in child order
		private static Element FirstButton(Element e, int depth) {
			if ( !IsValid(e) || !e.IsVisibleLocal ) return null;
			if ( e.IsButton ) return e;
			if ( depth <= 0 ) return null;
			foreach ( var child in e.Children ) {
				if ( FirstButton(child, depth - 1) is Element found ) return found;
			}
			return null;
		}

		private bool TryUpgradeFlask(CombatContext ctx) {
			if ( !PickUpItems ) return false;
			bool safe = !ctx.Hostiles.Any(h => h.Distance <= AttackRange);
			if ( !FlaskUpgrader.Tick(ctx, safe) ) return false;
			if ( CurrentGoal == null ) SetStatus(FlaskUpgrader.Status);
			return true;
		}

		private bool TryKeepAuras(CombatContext ctx) {
			bool safe = !ctx.Hostiles.Any(h => h.Distance <= AttackRange);
			if ( !AuraKeeper.Tick(ctx, safe) ) return false;
			if ( CurrentGoal == null ) SetStatus(AuraKeeper.Status);
			return true;
		}

		private bool TryLoot(CombatContext ctx) {
			if ( !PickUpItems || BackpackIsOpen() || !Looter.TryGetTarget(ctx, out var item) ) return false;
			var near = ctx.Hostiles.Where(h => h.Distance <= AttackRange || Combat.Distance(h.GridPos, item) <= AttackRange).ToList();
			if ( near.Count > 0 ) {
				// (a Kill fights them its own way, eg, Orbit; and a unique is fought by Orbit, see Fight)
				if ( CurrentGoal is Kill || Runner.Stack().OfType<Kill>().Any() || near.Any(h => h.Rarity == Offsets.MonsterRarity.Unique) ) return false;
				var target = ChooseTarget(near);
				if ( target != null && Engage(ctx, target) ) {
					SetStatus($"fighting {target} before picking up an item");
					return true;
				}
				return false;
			}
			if ( !Looter.TryLoot(ctx) ) return false;
			SetStatus(Looter.Status);
			return true;
		}

		// ---- showing it ----

		/// <summary>The leaf goals of the master list, in order (a FinishAllQuests opened to what it is working on), with the Sequence each is in, and how deep.</summary>
		/// <remarks>
		/// Until the campaign progress is read, only that goal (nothing is known yet about what is left). Then a Sequence
		/// that is done (a part, an act, "Exit The Twilight Strand", a quest) shows as one line, not its goals.
		/// </remarks>
		public IEnumerable<(Sequence parent, Goal goal, int depth)> Leaves() {
			if ( !CampaignProgress.Scanned ) return Root.Goals.OfType<ReadCampaignProgress>().Select(g => (Root, (Goal)g, 0));
			return Leaves(Root, 0);
		}
		private static IEnumerable<(Sequence parent, Goal goal, int depth)> Leaves(Sequence seq, int depth) {
			foreach ( var child in seq.Goals ) {
				if ( child is Sequence inner ) {
					if ( inner.IsComplete ) yield return (seq, inner, depth);
					else foreach ( var x in Leaves(inner, depth + 1) ) yield return x;
				} else yield return (seq, child, depth);
			}
		}

		/// <summary>The running chain, from the state ticked now back to the goal, as text lines.</summary>
		public IEnumerable<string> StackLines() => Runner.Stack().Select(s => s.ToString());

		private void DrawObjectives() {
			var size = WindowSize.Value;
			var pos = new Vector2(size.X * 0.30f, size.Y * 0.08f); // top middle-left: clear of the console, minimap and quest tracker
			const float line = 16f;
			DrawTextAt(pos, $"{Name}: {CampaignProgress.Summary}{(IsActive ? "" : " (off)")}", Color.Orange);
			pos.Y += line;
			var problem = BuildPlan.Problem();
			if ( problem != null ) { DrawTextAt(pos, $"Build: {problem}", Color.Red); pos.Y += line; }
			var leaves = Leaves().ToList();
			int current = CurrentGoal == null ? leaves.Count : leaves.FindIndex(l => l.goal == CurrentGoal);
			if ( current < 0 ) return;
			int from = Math.Max(0, current - 3), to = Math.Min(leaves.Count, current + 7);
			Sequence shown = null;
			for ( int i = from; i < to; i++ ) {
				var (parent, goal, depth) = leaves[i];
				if ( parent != shown && parent != Root ) {
					shown = parent;
					DrawTextAt(pos, $"{new string(' ', depth * 2)}{parent.Name}", parent.IsComplete ? Color.LightGreen : Color.White);
					pos.Y += line;
				}
				string mark; Color color;
				if ( goal.IsComplete ) { mark = "[x]"; color = Color.LightGreen; }
				else if ( i == current ) { mark = "[>]"; color = Color.Yellow; }
				else { mark = "[ ]"; color = Color.Gray; }
				string indent = new string(' ', 2 + (depth * 2));
				string where = goal.Where == null ? "" : $" ({goal.Where})";
				string detail = i == current && !string.IsNullOrEmpty(goal.Detail) ? $" - {goal.Detail}" : "";
				DrawTextAt(pos, $"{indent}{mark} {goal.Name}{where}{detail}", color);
				pos.Y += line;
				if ( i == current ) {
					// what it is running: the sub-states (innermost last), not the input
					foreach ( var s in Runner.Stack().OfType<CampaignState>().Where(s => s != goal).Reverse().Take(4) ) {
						DrawTextAt(pos, $"{indent}      > {s}", Color.Khaki);
						pos.Y += line;
					}
				}
			}
			if ( CurrentGoal == null ) DrawTextAt(pos, "  (the campaign is complete)", Color.LightGreen);
		}

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Objectives", ref ShowObjectives);
			ImGui.Checkbox("Pick Up Items", ref PickUpItems);
			ImGui_HotKeyButton("Portal Scroll Key", ref PortalScrollKey);
			ImGui.SameLine();
			ImGui_HelpMarker("The key bound to use a Portal Scroll in the game, if any; with None, one is right clicked in the backpack.");
			ImGui.InputText("Reward Overrides", ref RewardOverrides, 500);
			ImGui.SameLine();
			ImGui_HelpMarker("Quest rewards to take before what the Build wants, eg \"Enemy at the Gate: Caustic Arrow; Mercy Mission: Quicksilver Flask\". Otherwise: a gem the Build uses, else a Quicksilver Flask, else the first offered.");
			var problem = BuildPlan.Problem();
			if ( problem != null ) ImGui.TextColored(new Vector4(1, .4f, .4f, 1), $"Build: {problem}");
			else ImGui.Text($"Starting skill: {BuildPlan.StartingSkill()}, support: {BuildPlan.StartingSupport()}; main skill: {BuildPlan.MainSkill()}");
			ImGui.Text($"Progress: {CampaignProgress.Summary}; in act {CampaignProgress.CurrentAct}");
			if ( ImGui.TreeNode("Running") ) {
				ImGui.Text($"Goal: {CurrentGoal?.Id ?? "(none)"}");
				foreach ( var s in Runner.Stack() ) ImGui.BulletText(s.ToString());
				if ( Runner.Current != null && ImGui.Button("Browse the running state") ) Run_ObjectBrowser($"Campaign: {Runner.Current.Name}", Runner.Current);
				ImGui.SameLine();
				if ( CurrentGoal != null && ImGui.Button("Browse the goal") ) Run_ObjectBrowser($"Campaign goal: {CurrentGoal.Name}", CurrentGoal);
				ImGui.TreePop();
			}
			if ( ImGui.TreeNode("Master List") ) {
				foreach ( var g in Root.Goals ) RenderGoal(g);
				ImGui.TreePop();
			}
		}

		private void RenderGoal(Goal g) {
			string mark = g.IsComplete ? "[x]" : g == CurrentGoal ? "[>]" : "[ ]";
			string where = g.Where == null ? "" : $" ({g.Where})";
			string text = $"{mark} {g.Name}{where}{(string.IsNullOrEmpty(g.Detail) ? "" : $" - {g.Detail}")}";
			var children = g.Children.ToList();
			if ( children.Count == 0 ) {
				ImGui.BulletText(text);
				if ( ImGui.IsItemClicked() ) Run_ObjectBrowser($"Campaign goal: {g.Name}", g);
				return;
			}
			if ( !g.IsComplete && (g == CurrentGoal || (g is Sequence seq && seq.All().Contains(CurrentGoal))) ) ImGui.SetNextItemOpen(true, ImGuiCond.Once);
			if ( ImGui.TreeNode($"{text}##{g.Id ?? g.GetHashCode().ToString()}") ) {
				foreach ( var child in children ) RenderGoal(child);
				ImGui.TreePop();
			}
		}
	}
}
