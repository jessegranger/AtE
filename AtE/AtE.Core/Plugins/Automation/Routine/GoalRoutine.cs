using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A routine run from a list of goals (Root), each of which checks itself: the machinery the Campaign and Mapping share.
	///
	/// How it runs: four times a second it chooses the leaf goal (Sequence.Select). Each frame it ticks that goal as a
	/// state (CampaignRunner): the goal does one thing, or hands its work to sub-states (TravelTo, Navigate, interrupted
	/// by Fight, ...) and input states (Inputs: a key press, a click, spread over frames), which hand back to it. Input
	/// the driver queues (eg, using a skill) goes into the chain the same way. If the goal chosen changes, the chain is
	/// cancelled. Dying: resurrect, and start the current goal again.
	///
	/// Around the goal, the same for every routine: loot (Looter), bigger flasks (FlaskUpgrader), auras (AuraKeeper),
	/// gem level-ups, things worth clicking in passing (Interactables), augury windows, arena lockdowns, and opening and
	/// closing the inventory once in each new area (to keep it in step with the server).
	///
	/// A routine says what its goals are (BuildRoot), and may add to what happens on a new run (OnStartRun) or in a new
	/// area (OnNewArea).
	/// </summary>
	public abstract class GoalRoutine : CombatDriver {

		public bool ShowObjectives = true;
		/// <summary>Pick up the items that show a label on the ground (see Looter), when no monster is within AttackRange.</summary>
		public bool PickUpItems = true;
		/// <summary>The key that opens the game's Options panel (to read its key bindings once, see GameKeys).</summary>
		public HotKey OptionsKey = new HotKey(VKey.O);

		/// <summary>The master list (see BuildRoot).</summary>
		[NoPersist] public readonly Sequence Root;
		[NoPersist] public readonly CampaignRunner Runner = new CampaignRunner();
		protected readonly Looter Looter = new Looter();
		protected readonly FlaskUpgrader FlaskUpgrader = new FlaskUpgrader();
		protected readonly AuraKeeper AuraKeeper = new AuraKeeper();

		/// <summary>The leaf goal chosen (null if all are done).</summary>
		public Goal CurrentGoal { get; private set; }

		private uint areaHash;
		// a new area: open and close the inventory once (see TrySyncInventory)
		private bool inventorySyncDue;
		private long lastSelect;
		private bool wasActive;

		protected GoalRoutine() {
			// an item a goal is to pick up is left to it, so it sees the item go and knows it is done
			Looter.Leave = text => CurrentGoal is PickUp p && p.LabelText != null && text.IndexOf(p.LabelText, StringComparison.OrdinalIgnoreCase) >= 0;
			Looter.PickedUp = path => FlaskUpgrader.PickedUp(path);
			// (BuildRoot runs after the subclass's field initializers, before its constructor body)
			Root = BuildRoot();
			Root.AssignIds();
		}

		/// <summary>The master list of goals (called once, from the constructor).</summary>
		protected abstract Sequence BuildRoot();

		protected override bool TakesInput => true;

		/// <summary>One line on how far it has got, at the top of the objectives on screen (eg, the campaign's acts done).</summary>
		public virtual string ProgressSummary => "";
		/// <summary>Shown under the objectives once every goal is done.</summary>
		protected virtual string CompleteText => "(all done)";

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
			// (the overlay's own windows too: one under the mouse takes the routine's clicks)
			Overlay.CloseAllWindows();
			CloseOpenPanels();
			OnStartRun();
			Root.Forget();
			StartGoalOver();
		}

		/// <summary>
		/// Switched on with a panel open that would be in the way (the inventory, the Options, the world / waypoint panel, the
		/// escape menu, the passive tree): press Escape once (it closes them all). Noted as ours (Inputs.LastEscapeMs), so
		/// if it opens the escape menu instead, that is closed too. Not while the augury card selection window is open (an
		/// Escape would close it; it is worked on instead, see TryAugury).
		/// </summary>
		private void CloseOpenPanels() {
			var ui = GetUI();
			if ( ui == null || AuguryIsOpen() ) return;
			static bool Open(Element e) => IsValid(e) && e.IsVisible;
			var open = new List<string>();
			if ( Open(ui.InventoryPanel) ) open.Add("the inventory");
			if ( Open(ui.OptionsPanel) ) open.Add("the Options");
			if ( Open(ui.AreaInstanceUi) ) open.Add("the world panel");
			if ( Open(ui.EscapeMenu) ) open.Add("the escape menu");
			if ( Open(ui.SkillTree) ) open.Add("the passive tree");
			if ( open.Count == 0 ) return;
			Log($"{Name}: switched on with {string.Join(", ", open)} open: Escape to close {(open.Count == 1 ? "it" : "them")}");
			Inputs.LastEscapeMs = Time.ElapsedMilliseconds;
			Run(Inputs.Press(VKey.Escape, null));
		}

		/// <summary>Called when the routine is switched on, before the goals are all marked not done (eg, forget what was read from the game).</summary>
		protected virtual void OnStartRun() { }

		// a new area instance: what the goals were doing there is gone (not whether they are done)
		private void ForgetOnNewArea() {
			uint hash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			if ( hash == areaHash ) return;
			areaHash = hash;
			inventorySyncDue = true;
			OnNewArea();
			StartGoalOver();
		}

		/// <summary>Called on entering a new area instance, before the current goal is started over.</summary>
		protected virtual void OnNewArea() { }

		/// <summary>Worked on in place of the goal while locked in an arena (see Select).</summary>
		private readonly ArenaLockdown arenaLockdown = new ArenaLockdown(BuildPlan.MainSkill);

		protected void StartGoalOver() {
			Runner.Cancel();
			ForgetInput();
			lastSelect = 0;
			arenaLockdown.Reset();
			Looter.Reset();
			FlaskUpgrader.Reset();
			AuraKeeper.Reset();
			Root.Reset();
		}

		// choose the leaf goal; start its chain if it changed
		private void Select(CombatContext ctx) {
			var leaf = Root.Select(ctx);
			// locked in an arena (its doors closed around the player): fight the waves until they open, whatever the goal
			// wanted (it would walk at the doors), unless the goal fights here itself (eg, a boss: AvoidSpam is a Kill)
			CurrentGoal = FightsItself(leaf, ctx) || !ArenaLock.IsLockedIn(ctx) ? leaf : arenaLockdown;
			if ( CurrentGoal != Runner.Leaf ) {
				if ( Runner.Leaf != null ) Log($"{Name}: now on {CurrentGoal?.Name ?? "(nothing)"} (was {Runner.Leaf.Name})");
				Runner.Run(CurrentGoal);
				ForgetInput();
			}
		}

		/// <summary>
		/// Does the goal fight where it is itself (a Kill, eg a boss with AvoidSpam, or FightUntil), also when it is one chosen
		/// inside another: the quest goal FinishAllQuests works on, the goal a FirstOf rule picks, a Sequence's current one.
		/// </summary>
		protected static bool FightsItself(Goal g, CombatContext ctx, int depth = 0) => g != null && depth < 8 && (g is Kill || g is FightUntil
			|| (g is FinishAllQuests quests && FightsItself(quests.Current, ctx, depth + 1))
			|| (g is FirstOf rules && FightsItself(rules.Pick(ctx), ctx, depth + 1))
			|| (g is Sequence seq && FightsItself(seq.Current, ctx, depth + 1)));

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

		private string tracedState;
		private void DecideGoal(CombatContext ctx) {
			if ( Tracing && Runner.Current?.Name != tracedState ) { tracedState = Runner.Current?.Name; Trace($"running {tracedState ?? "(nothing)"}"); }
			// input going out (any state in the chain that is not the routine's own: input states, Delays, the end of
			// input that clears InputBusy): let it finish before anything else (choosing again, looting), so a goal change
			// does not cut a key press in half, and the looter does not wait forever on InputBusy
			if ( Runner.Current != null && !(Runner.Current is CampaignState) ) {
				// (waiting for the focus is not being stuck: mouse states wait for it, see WaitForFocus)
				if ( Runner.Current != stuckState || !GameFocused ) { stuckState = Runner.Current; stuckSince = Time.ElapsedMilliseconds; }
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
			// the augury card selection window (opened by clicking a frozen augury boss, or a fate's; or already open when
			// switched on): choose / apply the cards first, before the inventory sync's key presses (its clicks go into the
			// chain, see Decide); then the routine goes on
			if ( TryAugury() ) return;
			if ( TrySyncInventory() ) return;
			if ( TryLevelGems(ctx) ) return;
			if ( Runner.Current == null || Time.ElapsedMilliseconds - lastSelect > 250 ) {
				lastSelect = Time.ElapsedMilliseconds;
				Select(ctx);
			}
			if ( CurrentGoal == null ) {
				// all done: what a combat driver does
				if ( !TryUpgradeFlask(ctx) && !TryKeepAuras(ctx) && !TryLoot(ctx) && !TryInteract(ctx) ) base.Decide(ctx);
				return;
			}
			// (the goal whose UsesInventory and IgnoreMonsters count: see Working)
			var goal = Working(CurrentGoal);
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
					Send(GameKeys.Press(GameKeys.InventoryKey, null));
				}
				return;
			}
			if ( !goal.UsesInventory && !goal.IgnoreMonsters && TryLoot(ctx) ) return;
			// a thing worth clicking in passing (an AuguryCardObject's "Select a Fate", whose window is worked on by TryAugury,
			// above; a Demonic Evocator, a Crafting Recipe, a Shrine, ...; see Interactables)
			if ( !goal.UsesInventory && !goal.IgnoreMonsters && !FightsItself(goal, ctx) && TryInteract(ctx) ) { SetStatus($"{goal.Name}: {Status}"); return; }
			if ( !Runner.Tick(ctx, FrameDt) ) lastSelect = 0; // the goal handed back (done): choose again next frame
			goal = Working(CurrentGoal ?? goal);
			var top = Runner.Stack().OfType<CampaignState>().FirstOrDefault();
			if ( top != null && top != goal ) SetStatus($"{goal.Name}: {top}");
			else if ( !string.IsNullOrEmpty(goal.Detail) ) SetStatus($"{goal.Name}: {goal.Detail}");
		}
		/// <summary>
		/// The goal being worked on, for what the routine does around it (looting, closing the inventory, ...) and the
		/// on-screen list: the chosen leaf itself (the Campaign), or, for a routine whose leaf picks among goals of its own
		/// (eg, Mapping's FirstOf of where the player is), the one it picked. Never null for a goal that is not.
		/// </summary>
		protected virtual Goal Working(Goal leaf) => leaf;

		private long lastInventoryClose;
		/// <summary>An input state current for longer than this is given up on (the chain is cancelled).</summary>
		public long MaxInputMs = 3000;
		private IState stuckState;
		private long stuckSince;

		// for fights where the goal names no skill
		protected override void Attack(Hostile target, Vector2 screenPos) => UseSkillPaced(BuildPlan.MainSkill(), screenPos);
		protected override SkillSlot MainSkillSlot() => SkillBar.FindSlot(BuildPlan.MainSkill()) ?? base.MainSkillSlot();

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
			OnResurrected();
			StartGoalOver();
		}

		/// <summary>Called once alive again after dying, before the current goal is started over.</summary>
		protected virtual void OnResurrected() { }

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
			// (a channelled skill held, eg Scorching Ray on a mouse button: let go first, or the click never gets to the button)
			ReleaseChannel("leveling up a gem");
			if ( !Send(Inputs.Click(rect)) ) return false;
			lastGemLevel = now;
			SetStatus("leveling up a gem");
			Log($"{Name}: clicked a gem level-up button at {rect}");
			return true;
		}

		// the first visible button under e (e included), depth first in child order; not in a gem's row that says "Gem
		// cannot level up" (3.29.4, seen live: its button still shows, but clicking it does nothing, eg the character's
		// level is too low)
		private static Element FirstButton(Element e, int depth) {
			if ( !IsValid(e) || !e.IsVisibleLocal ) return null;
			if ( e.IsButton ) return e;
			if ( depth <= 0 ) return null;
			if ( e.Children.Any(c => IsValid(c) && c.IsVisibleLocal && string.Equals(c.Text?.Trim(), "Gem cannot level up", StringComparison.OrdinalIgnoreCase)) ) return null;
			foreach ( var child in e.Children ) {
				if ( FirstButton(child, depth - 1) is Element found ) return found;
			}
			return null;
		}

		/// <summary>Put a bigger life or mana flask picked up in place of a smaller one (see FlaskUpgrader): the Campaign's, not every routine's.</summary>
		protected virtual bool UpgradesFlasks => true;

		private bool TryUpgradeFlask(CombatContext ctx) {
			if ( !PickUpItems || !UpgradesFlasks ) return false;
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

		/// <summary>
		/// At the start of each area: press the inventory key (GameKeys.InventoryKey, from the Options panel) twice, to open
		/// the inventory and close it again, with no wait between beyond the key presses' own, so the game keeps the worn
		/// items and the backpack in step with the server. Not if the inventory is open already. Waits until the key bindings
		/// are read (VKey.I if they can't be). True while it goes out.
		/// </summary>
		private bool TrySyncInventory() {
			if ( !inventorySyncDue ) return false;
			if ( BackpackIsOpen() ) { inventorySyncDue = false; return false; }
			if ( InputBusy ) return false;
			// (the key from the Options panel: wait for it, read at the start of the run by ReadGameKeys, unless it can't be)
			if ( !GameKeys.Loaded && !GameKeys.Unreadable && !GameKeys.TryRead() ) return false;
			var key = GameKeys.InventoryKey;
			if ( !Send(GameKeys.Press(key, GameKeys.Press(key, null))) ) return false;
			inventorySyncDue = false;
			Log($"{Name}: a new area: opening and closing the inventory ({SkillBar.KeyName(key)}) to sync it");
			SetStatus("opening and closing the inventory");
			return true;
		}

		// pick up an item, if there is one; true if doing so, or fighting the monsters near it (or near the player) first
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
			if ( Looter.TakeNoRoomForQuestItem() is string questItem ) {
				// (a quest item that won't go in: the player makes room)
				string why = $"no room for the quest item {questItem}: make room in the inventory, then switch the routine on again";
				Log($"{Name}: stopping: {why}");
				ctx.Driver.Stop(why);
				return true;
			}
			SetStatus(Looter.Status);
			return true;
		}

		// ---- showing it ----

		/// <summary>The leaf goals of the master list, in order, with the Sequence each is in, and how deep.</summary>
		/// <remarks>A Sequence that is done shows as one line, not its goals.</remarks>
		public virtual IEnumerable<(Sequence parent, Goal goal, int depth)> Leaves() => Leaves(Root, 0);
		protected static IEnumerable<(Sequence parent, Goal goal, int depth)> Leaves(Sequence seq, int depth) {
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
			DrawTextAt(pos, $"{Name}: {ProgressSummary}{(IsActive ? "" : " (off)")}", Color.Orange);
			pos.Y += line;
			var problem = BuildPlan.Problem();
			if ( problem != null ) { DrawTextAt(pos, $"Build: {problem}", Color.Red); pos.Y += line; }
			var leaves = Leaves().ToList();
			var working = CurrentGoal == null ? null : Working(CurrentGoal);
			int current = working == null ? leaves.Count : leaves.FindIndex(l => l.goal == working);
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
					foreach ( var s in Runner.Stack().OfType<CampaignState>().TakeWhile(s => s != goal).Reverse().Take(4) ) {
						DrawTextAt(pos, $"{indent}      > {s}", Color.Khaki);
						pos.Y += line;
					}
				}
			}
			if ( CurrentGoal == null ) DrawTextAt(pos, $"  {CompleteText}", Color.LightGreen);
		}

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Objectives", ref ShowObjectives);
			ImGui.Checkbox("Pick Up Items", ref PickUpItems);
			ImGui_HotKeyButton("Options Key", ref OptionsKey);
			ImGui.SameLine();
			ImGui_HelpMarker("The key bound to open the game's Options: they are opened once, when a game key is needed (eg, Use Portal Scroll), to read the key bindings.");
			RenderSettings();
			if ( ImGui.TreeNode("Running") ) {
				ImGui.Text($"Goal: {CurrentGoal?.Id ?? "(none)"}");
				foreach ( var s in Runner.Stack() ) ImGui.BulletText(s.ToString());
				if ( Runner.Current != null && ImGui.Button("Browse the running state") ) Run_ObjectBrowser($"{Name}: {Runner.Current.Name}", Runner.Current);
				ImGui.SameLine();
				if ( CurrentGoal != null && ImGui.Button("Browse the goal") ) Run_ObjectBrowser($"{Name} goal: {CurrentGoal.Name}", CurrentGoal);
				ImGui.TreePop();
			}
			if ( ImGui.TreeNode("Master List") ) {
				foreach ( var g in Root.Goals ) RenderGoal(g);
				ImGui.TreePop();
			}
		}

		/// <summary>The routine's own controls, between the shared ones and the Running / Master List trees.</summary>
		protected virtual void RenderSettings() { }

		private void RenderGoal(Goal g) {
			string mark = g.IsComplete ? "[x]" : g == CurrentGoal ? "[>]" : "[ ]";
			string where = g.Where == null ? "" : $" ({g.Where})";
			string text = $"{mark} {g.Name}{where}{(string.IsNullOrEmpty(g.Detail) ? "" : $" - {g.Detail}")}";
			var children = g.Children.ToList();
			if ( children.Count == 0 ) {
				ImGui.BulletText(text);
				if ( ImGui.IsItemClicked() ) Run_ObjectBrowser($"{Name} goal: {g.Name}", g);
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
