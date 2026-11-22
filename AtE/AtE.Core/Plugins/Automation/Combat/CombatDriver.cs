using ImGuiNET;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The base for a combat driver: a plugin that clears an area by itself.
	///
	/// Each frame, while switched on (Start, by CombatSection's key): if life is low, retreat to a safe spot; else pick a monster
	/// (ChooseTarget), and if it is in range (with line of sight, if NeedsLineOfSight) Attack it, or else walk to a
	/// spot where it would be; with no monsters around, walk to the next unexplored place; with nothing left, stop.
	///
	/// A driver for a particular build overrides Attack (and usually AttackRange / NeedsLineOfSight / ChooseTarget).
	/// Moving the player and attacking both use the mouse, so this waits while the game is not focused.
	/// </summary>
	public abstract class CombatDriver : AutomationSection, ITrapDodger {

		/// <summary>Monsters within this many cells are fought before exploring further.</summary>
		public int EngageRadius = 60;
		/// <summary>Retreat (to a spot RetreatDistance from all monsters) when life + energy shield is below this percent; 0 never retreats.</summary>
		public int RetreatBelowLifePercent = 0;
		public int RetreatDistance = 40;

		/// <summary>How close (in cells) the player needs to be to Attack.</summary>
		public virtual float AttackRange => 30f;
		/// <summary>True if attacks need a clear line for projectiles (from the player to the target).</summary>
		public virtual bool NeedsLineOfSight => true;

		/// <summary>
		/// The monster to fight next, from the hostiles nearest first; null for none. The one of highest
		/// Combat.TargetPriority (a special target, eg a "Nearby Allies Cannot Die" totem, then unique, rare, magic, normal);
		/// of those, the target now if it is one, else the nearest. Asked every frame, so a higher one that comes near takes
		/// over before the target now is dead.
		/// </summary>
		protected virtual Hostile ChooseTarget(System.Collections.Generic.List<Hostile> hostiles) {
			Hostile best = null;
			int bestPriority = -1;
			uint current = Target?.Entity?.Id ?? 0;
			foreach ( var h in hostiles ) {
				int p = Combat.TargetPriority(h);
				if ( p > bestPriority || (p == bestPriority && current != 0 && h.Entity?.Id == current) ) { best = h; bestPriority = p; }
			}
			return best;
		}

		/// <summary>
		/// Attack target, which is in range (and in sight); screenPos is where it is on the screen (safe to click).
		/// Called every frame while it is the target, so throttle as needed.
		/// </summary>
		protected abstract void Attack(Hostile target, Vector2 screenPos);

		public bool IsActive { get; private set; }
		public string Status {
			get => status;
			private set {
				// (not one flickering back and forth each frame, eg a goal's name and its detail)
				if ( value != status && value != statusBefore && Tracing ) Log($"{Name} +{Time.ElapsedMilliseconds - startedAt} ms: {value}");
				if ( value != status ) statusBefore = status;
				status = value;
			}
		}
		private string status = "off", statusBefore;
		private long startedAt = -StartupTraceMs;
		/// <summary>For how long after Start every change of Status (and of the state running) is logged (to see where startup time goes).</summary>
		public const long StartupTraceMs = 5000;
		/// <summary>True within StartupTraceMs of Start (see Status).</summary>
		protected bool Tracing => Time.ElapsedMilliseconds - startedAt < StartupTraceMs;
		/// <summary>Log a line, with the ms since Start, while Tracing.</summary>
		protected void Trace(string what) { if ( Tracing ) Log($"{Name} +{Time.ElapsedMilliseconds - startedAt} ms: {what}"); }
		public Hostile Target { get; private set; }

		private bool binding;
		private ExploreTarget exploreTarget;
		private long exploreTargetTime;

		public override void Render() {
			base.Render();
			ImGui.SliderInt("Engage Radius (cells)", ref EngageRadius, 10, 120);
			ImGui.SliderInt("Retreat Below Life %", ref RetreatBelowLifePercent, 0, 90);
			ImGui.SliderInt("Retreat Distance (cells)", ref RetreatDistance, 10, 80);
			if ( ImGui.TreeNode("Skill Bar") ) {
				foreach ( var slot in SkillBar.GetSlots() ) {
					int ms = SkillBar.CastTimeMs(slot.Skill);
					ImGui.Text($"{slot}{(ms > 0 ? $" ({ms} ms)" : "")}");
				}
				ImGui.Text($"Available{(SkillBar.IsChoosing ? " (open)" : "")}: {string.Join(", ", SkillBar.GetAvailable())}");
				if ( !SkillBar.LeftClickIsMovement() ) ImGui.TextColored(new Vector4(1, .6f, .2f, 1), "Left click is not Movement: walking may attack");
				ImGui.TreePop();
			}
		}

		/// <summary>
		/// On, but doing nothing (no input, no walking) until this is cleared (eg, the memory server's /mapping/pause and
		/// /mapping/resume). Cleared by Start and Stop.
		/// </summary>
		[NoPersist] public bool Paused;

		public void Start() {
			IsActive = true;
			Paused = false;
			// (an augury window still open from before is looked at afresh)
			auguryCardsClicked.Clear();
			auguryConfirms = 0;
			auguryWindowSeen = false;
			startedAt = Time.ElapsedMilliseconds;
			Status = "starting";
		}

		public void Stop(string why) {
			IsActive = false;
			Paused = false;
			Status = why;
			ReleaseChannel(why);
			Target = null;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
			// (and let go of the left button whoever pressed it: a press still waiting to go out is not sent at all, see
			// MouseButtonDown.CancelIfReleased; one left down by a routine that stopped kept the player walking)
			DefaultFollower.ReleaseMouse();
			Run(MouseHold.Release(MouseBtn.Left));
			// (and every other button left down, the right one always: nothing should stay held once a routine stops)
			Run(MouseHold.Release(MouseBtn.Right));
			HeldButtons.ReleaseAll(why);
		}

		/// <summary>The ms since the last frame (for Decide).</summary>
		protected long FrameDt { get; private set; }

		/// <summary>
		/// Input to send (a chain of input states, see Inputs), queued by Send. A driver that runs states itself (the
		/// Campaign) takes it into its own chain (TakeInput); otherwise it runs on the default Machine.
		/// </summary>
		/// <summary>The escape menu opening this soon after an Escape of ours: ours to close.</summary>
		public long EscapeMenuMs = 3000;
		private long lastMenuEscape;
		/// <summary>An empty input (eg, the chat's) with the focus this long, nothing of ours typing: closed with Escape.</summary>
		public long ChatIdleCloseMs = 4000;
		private long emptyInputSince;
		private IState pendingInput;
		private bool inputRunning;
		/// <summary>True while input is queued or still going out: Send refuses more until it is done.</summary>
		public bool InputBusy => pendingInput != null || inputRunning;
		/// <summary>True if this driver takes the queued input into its own chain (see TakeInput) instead of the default Machine running it.</summary>
		protected virtual bool TakesInput => false;

		/// <summary>Queue input (eg, Inputs.UseSlot) to go out over the next frames; false (and not queued) if input is still going out.</summary>
		/// <summary>Use the skill bar's travel skills on straight stretches of the path when walking (TravelSkills, a State of its own, looks at this).</summary>
		public bool UseTravelSkills = true;

		public bool Send(IState input) {
			if ( input == null || InputBusy ) return false;
			ReleaseChannel("other input"); // (a click or key with the channelling key still down would go wrong)
			inputRunning = true;
			input.Tail().Next = State.NewState(() => inputRunning = false);
			pendingInput = input;
			return true;
		}

		/// <summary>The queued input (its chain's last state is open: set its Next), or null; it is not queued any more.</summary>
		protected IState TakeInput() {
			var input = pendingInput;
			pendingInput = null;
			return input;
		}

		/// <summary>Forget input queued or going out (eg, the chain it was in was cancelled).</summary>
		protected void ForgetInput() { pendingInput = null; inputRunning = false; }

		public override IState OnTick(long dt) {
			FrameDt = dt;
			if ( !TakesInput && pendingInput != null ) Run(TakeInput());
			if ( !PoEMemory.IsAttached ) { ReleaseChannel("detached"); return this; }
			if ( !IsActive ) { ReleaseChannel("off"); return this; }
			// (before the escape menu check below: the menu a pause opened is not to be closed as one of ours)
			if ( Paused ) {
				if ( DefaultFollower.IsActiveFor(this) ) StopWalking("paused");
				return Wait("paused");
			}

			var game = PoEMemory.GameRoot?.InGameState;
			var terrain = GetTerrain();
			var player = GetPlayer();
			var pos = player?.GetComponent<Positioned>();
			if ( game == null || terrain == null || !IsValid(pos) ) return Wait("no terrain or player");
			if ( !IsAlive(player) ) {
				ReleaseChannel("dead");
				if ( !OnPlayerDead() ) Stop("the player died");
				DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
				return this;
			}
			// (paused by WaitForFocus while another window is in front: its escape menu is left open until the focus is back)
			if ( GamePausedForFocus ) { ReleaseChannel("paused"); return Wait("paused until the game has the focus"); }
			if ( PoEMemory.GameRoot?.EscapeState?.Menu != null ) {
				// opened by an Escape of ours (eg, the dialog it was to close had closed already): Escape again to close it
				// (never a click while it is open: the chain waits here); one the player opened is left alone
				long now = Time.ElapsedMilliseconds;
				if ( now - Inputs.LastEscapeMs < EscapeMenuMs && now - lastMenuEscape > 1000 && CanSendKey(VKey.Escape) ) {
					lastMenuEscape = now;
					Log($"{Name}: the escape menu opened after our Escape: closing it");
					Platform.Current.Input.KeyDown(VKey.Escape);
					Platform.Current.Input.KeyUp(VKey.Escape);
				}
				return Wait("escape menu");
			}
			// (the augury card selection window pauses the game: it is worked on all the same, see TryAugury)
			// (so does the passive tree: points are spent with it open, see TrySpendPoints, eg after a restart with it open)
			if ( game.IsPaused && !AuguryIsOpen() && !PassiveTreePanel.IsOpen ) return Wait("the game is paused");
			// (the hidden skill bar takes the input focus too: not typing, and a click in it must get to its release)
			// (nor is a chat command of ours being typed, see ChatCommand)
			if ( game.HasInputFocus && !SkillBar.IsChoosing && !ChatCommand.IsTyping ) {
				// an empty input left open (3.29.4, seen after a relaunch: the chat input, open and empty, with nothing
				// typing): close it with Escape after ChatIdleCloseMs; one with text in it is someone typing, so wait
				long nowMs = Time.ElapsedMilliseconds;
				bool empty = string.IsNullOrEmpty(game.Focused?.Text);
				if ( !empty ) emptyInputSince = 0;
				else if ( emptyInputSince == 0 ) emptyInputSince = nowMs;
				else if ( nowMs - emptyInputSince > ChatIdleCloseMs && nowMs - lastMenuEscape > 1000 && CanSendKey(VKey.Escape) ) {
					lastMenuEscape = Inputs.LastEscapeMs = nowMs; // (ours: if it opens the escape menu instead, that is closed too)
					emptyInputSince = 0;
					Log($"{Name}: an empty input ({game.Focused?.GetClientRect()}) has had the focus for {ChatIdleCloseMs} ms: closing it with Escape");
					Run(Inputs.Press(VKey.Escape)); // (on the default Machine: the campaign's chain does not run while waiting here)
				}
				return Wait("typing in the game");
			}
			emptyInputSince = 0;
			// (no wait for the focus here: only the mouse needs it, and every mouse state waits for it, pausing the game
			// meanwhile, see WaitForFocus)
			if ( binding ) return Wait("changing the skill bar");
			// (the augury card selection window open, eg already open when switched on: it is worked on first, see TryAugury,
			// before anything that opens a panel of its own over it: the skill bar's choices, the passive tree)
			bool augury = AuguryIsOpen();
			// left click should only move (and interact), never attack
			if ( !augury && !SkillBar.LeftClickIsMovement() ) {
				Bind(SkillBar.LeftClickSlot, SkillBar.MovementName);
				return Wait("putting Movement on left click");
			}
			var ctx = new CombatContext(this, game, terrain, player, pos.GridPos, Position(player).Z);
			if ( !augury && TrySpendPoints(ctx) ) {
				DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
				return this;
			}
			try {
				Decide(ctx);
			} finally {
				CheckChannel();
				// walk, if walking was decided on
				if ( DefaultFollower.IsActiveFor(this) ) {
					DefaultFollower.Tick(DefaultNavigator, terrain, ctx.Grid, ctx.Z);
				}
			}
			DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
			return this;
		}

		// the points and passives there were when spending last stopped short (eg, at a mastery): not tried again until they change
		private string spendStoppedAt;

		/// <summary>
		/// With passive points to spend and no monsters within AttackRange (as for picking up items), spend them on the
		/// Passive Tree plan (see PassiveAllocator). True while doing so (instead of anything else). (The game is paused
		/// while the tree is open, so monsters can't come near once it has started.)
		/// </summary>
		public bool TrySpendPoints(CombatContext ctx) {
			var section = GetPlugin<AutomationPlugin>()?.PassiveTree;
			var allocator = section?.Allocator;
			if ( allocator == null || section.Plan == null ) return false;
			int points = GetUI()?.UnspentPassivePoints ?? 0;
			string at = $"{points} points, {ctx.Game.AllocatedPassives.Length} passives";
			if ( !allocator.IsActive ) {
				// it stopped: all spent, or stopped short
				if ( spending ) { spending = false; spendStoppedAt = at; }
				if ( points == 0 || at == spendStoppedAt ) return false;
				if ( ctx.Hostiles.Any(h => h.Distance <= AttackRange) ) return false; // (not with monsters near: once they are dealt with)
				allocator.Start();
				spending = true;
			}
			// (the Passive Tree section ticks the allocator)
			StopWalking("spending passive points");
			ReleaseChannel("spending passive points"); // (a channelled skill held keeps the clicks from the tree)
			Status = $"spending passive points: {allocator.Status}";
			return true;
		}
		private bool spending;

		/// <summary>
		/// Called each frame while the driver is on and the player is dead: true to stay on (eg, resurrecting), false to stop.
		/// </summary>
		protected virtual bool OnPlayerDead() => false;

		/// <summary>
		/// Called each frame while the driver is on (and the game is ready for input): decide what to do, by calling
		/// TryRetreat, TryFight, Engage, TryExplore, WalkTo, UseSkill... (they set Status). Walking happens after this returns.
		/// The default: retreat if low on life, else fight, else explore, else stop.
		/// </summary>
		protected virtual void Decide(CombatContext ctx) {
			if ( TryAugury() || TryEscapeDanger(ctx) || TryRetreat(ctx) || TryFight(ctx) || TryInteract(ctx) ) return;
			if ( !TryExplore(ctx) ) Stop("the area is explored and clear");
		}

		/// <summary>If life + energy shield is below RetreatBelowLifePercent with monsters near, walk away from them; true if retreating.</summary>
		public bool TryRetreat(CombatContext ctx) {
			var life = ctx.Player.GetComponent<Life>();
			if ( RetreatBelowLifePercent <= 0 || ctx.Hostiles.Count == 0 || !IsValid(life) ) return false;
			float max = life.MaxHP + life.MaxES;
			float percent = max <= 0 ? 100 : 100f * (life.CurHP + life.CurES) / max;
			if ( percent >= RetreatBelowLifePercent ) return false;
			if ( !Combat.NearestSafeSpot(ctx.Terrain, ctx.Grid, ctx.Hostiles.Select(h => h.GridPos), RetreatDistance, out var safe) ) return false;
			WalkTo(safe, $"retreating to {safe} ({percent:F0}% life)");
			return true;
		}

		/// <summary>Step out of danger (see TryEscapeDanger) before anything else, with monsters near.</summary>
		public bool EscapeDanger = true;
		/// <summary>Keep walking to the cell chosen before while it is safe and scores within this much of the best now (see AvoidSpam.GoalHysteresis).</summary>
		public float EscapeHysteresis = 8;
		/// <summary>Forget the cell chosen before after this long.</summary>
		public long EscapeGoalKeepMs = 2000;
		/// <summary>Log an escape at most this often.</summary>
		public long EscapeLogMs = 2000;
		private Offsets.Vector2i escapeGoal;
		private long escapeGoalAt = -1000000, lastEscapeLog = -1000000;
		private int escapeMap = -1;
		private Offsets.Vector2i escapeFrom;
		private bool escapeSafe;

		/// <summary>
		/// With monsters near and the player standing in danger (the DangerMap at SafeLevel or more, from any hazard, not
		/// only the target's), walk to the best cell out of it (DangerMap.TryFindSafest; one in range and sight of the
		/// monster being fought, if there is one that good), before fighting, looting or anything else. True if walking out;
		/// false if safe, or there is nowhere better (then the fight goes on where it is).
		/// </summary>
		public bool TryEscapeDanger(CombatContext ctx) {
			if ( !EscapeDanger || ctx.Hostiles.Count == 0 ) return false;
			var map = DangerMap.Get();
			if ( map == null ) return false;
			int here = map.IsBlocked(ctx.Grid) ? map.HazardDangerAt(ctx.Grid) : map.DangerAt(ctx.Grid); // (the grid may block the player's own cell, near a wall)
			if ( here < DangerMap.SafeLevel ) return false;
			// the monster being fought, where it is now (else the nearest): a cell in range and sight of it is better
			uint targetId = Target?.Entity?.Id ?? 0;
			var fought = (targetId != 0 ? ctx.Hostiles.FirstOrDefault(h => h.Entity?.Id == targetId) : null)
				?? ctx.Hostiles.OrderBy(h => h.Distance).First();
			float range = AttackRange * .8f;
			bool los = NeedsLineOfSight;
			bool Good(Offsets.Vector2i c) => Combat.Distance(c, fought.GridPos) <= range && (!los || ctx.Terrain.HasLineOfSight(c, fought.GridPos));
			long now = Time.ElapsedMilliseconds;
			Offsets.Vector2i cell;
			bool safe;
			if ( map.Number == escapeMap && ctx.Grid.Id == escapeFrom.Id ) {
				cell = escapeGoal; safe = escapeSafe; // (the same map from the same cell: the same answer)
			} else {
				safe = map.TryFindSafest(ctx.Grid, Good, out cell, out float score, 150);
				if ( now - escapeGoalAt < EscapeGoalKeepMs && escapeGoal.Id != cell.Id && map.IsSafe(escapeGoal) && safe
					&& map.Score(escapeGoal, Good(escapeGoal)) <= score + EscapeHysteresis ) cell = escapeGoal;
				escapeMap = map.Number; escapeFrom = ctx.Grid; escapeSafe = safe;
			}
			if ( cell.Id == ctx.Grid.Id || !(safe || map.DangerAt(cell) < here) ) return false;
			escapeGoal = cell;
			escapeGoalAt = now;
			if ( now - lastEscapeLog > EscapeLogMs ) {
				lastEscapeLog = now;
				var sources = map.Hazards.Where(h => h.EdgeDistance(ctx.Grid) <= DangerMap.Margin).Select(h => h.Source).Distinct().Take(3);
				Log($"{Name}: in danger {here} at {ctx.Grid} ({string.Join(", ", sources)}): {(safe ? "stepping out" : "nowhere safe near: to the least bad cell")}, to {cell} (danger {map.DangerAt(cell)})");
			}
			WalkTo(cell, safe ? $"stepping out of danger ({here}), to {cell}" : $"in danger ({here}), nowhere safe near: to the least bad cell, {cell}");
			return true;
		}

		/// <summary>
		/// Fight the monster ChooseTarget picks from those near (with skill, if given, else Attack); true if fighting.
		/// A monster near that has to be clicked first (inside a monolith, a frozen augury boss) is clicked (see ClickMonster).
		/// </summary>
		public bool TryFight(CombatContext ctx, string skill = null) {
			if ( ctx.MonstersToClick.FirstOrDefault() is Hostile toClick ) return ClickMonster(ctx, toClick);
			Target = ChooseTarget(ctx.Hostiles);
			return Target != null && Engage(ctx, Target, skill);
		}

		/// <summary>
		/// The monster TryFight would fight (ChooseTarget's pick), without fighting it: from those near (radius 0), or those
		/// within radius cells that can be walked to. Null if none.
		/// </summary>
		public Hostile PickTarget(CombatContext ctx, float radius = 0) => radius <= 0 ? ChooseTarget(ctx.Hostiles)
			: ChooseTarget(Combat.GetHostiles(ctx.Grid, radius).Where(h => Wanted(h) && DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, h.GridPos)).ToList());

		/// <summary>Monsters not to fight for now (eg, a map's boss until its rules allow, see MapRules.FatesFirst); null: none.</summary>
		[NoPersist] public Func<Entity, bool> Ignore;
		/// <summary>Is the monster one to fight (not one Ignore leaves alone)?</summary>
		public bool Wanted(Hostile h) => Ignore == null || h?.Entity == null || !Ignore(h.Entity);

		/// <summary>Fates chosen (a "Select a Fate" window closed after a card was chosen) in the area instance FatesArea.</summary>
		public int FatesTaken { get; private set; }
		/// <summary>The area instance (WorldData.AreaHash) FatesTaken counts in.</summary>
		public uint FatesArea { get; private set; }
		private bool auguryWasFate; // (the window open now is a fate's, with a card chosen)

		/// <summary>Fight the monsters within radius cells (that can be walked to), instead of EngageRadius; true if fighting.</summary>
		public bool TryFight(CombatContext ctx, float radius, string skill = null) {
			if ( ctx.MonstersToClick.FirstOrDefault(h => h.Distance <= radius) is Hostile toClick ) return ClickMonster(ctx, toClick);
			var near = Combat.GetHostiles(ctx.Grid, radius).Where(h => Wanted(h) && DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, h.GridPos)).ToList();
			Target = ChooseTarget(near);
			return Target != null && Engage(ctx, Target, skill);
		}

		/// <summary>
		/// Fight target: if it is within range (and in sight, if needed), attack it, with skill if given (see UseSkill),
		/// else with Attack; otherwise walk to a spot where it would be. False if there is no such spot.
		/// </summary>
		public bool Engage(CombatContext ctx, Hostile target, string skill = null, float range = 0, bool? needsLineOfSight = null) {
			if ( range <= 0 ) range = AttackRange;
			bool los = needsLineOfSight ?? NeedsLineOfSight;
			Target = target;
			float d = Combat.Distance(ctx.Grid, target.GridPos);
			bool inRange = d <= range && (!los || ctx.Terrain.HasLineOfSight(ctx.Grid, target.GridPos));
			if ( inRange && TryWorldToScreen(target.WorldPos, out Vector2 screen) && PathFollower.IsSafeToClick(screen) ) {
				if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "attacking");
				if ( TryPlaceTotem(ctx, screen) ) { Status = $"placing a Decoy Totem near {target}"; return true; }
				Status = $"attacking {target}{(skill == null ? "" : $" with {skill}")}";
				Stalemates.NoteAttack(target.Entity);
				if ( skill == null ) Attack(target, screen);
				else UseSkillPaced(skill, screen);
				return true;
			}
			if ( Combat.FindAttackSpot(ctx.Terrain, ctx.Grid, target.GridPos, range * 0.8f, los, out var spot) ) {
				WalkTo(spot, $"moving to attack {target} from {spot}");
				return true;
			}
			return false;
		}

		/// <summary>How often to click in the augury card selection window.</summary>
		public long AuguryClickMs = 300;
		// (the gap between clicks in the augury window, and before the first one: AuguryClickMs plus one InputLatency)
		private long AuguryClickGap => AuguryClickMs + Inputs.Latency;
		/// <summary>How long to wait for the window to close after clicking Confirm, before clicking it again.</summary>
		public long AuguryConfirmRetryMs = 1500;
		private readonly HashSet<long> auguryCardsClicked = new HashSet<long>();
		private long lastAuguryClick, lastAuguryConfirm;
		private bool auguryWindowSeen; // (for the log: once when it opens, once when it closes)
		private int auguryConfirms; // Confirm clicks in this window (see MaxAuguryConfirms)
		/// <summary>Confirm clicks in one augury window (applying a boss's cards) before the routine gives up and stops.</summary>
		public const int MaxAuguryConfirms = 5;
		/// <summary>
		/// A fate card chosen, and Confirm clicked, with the window still open this long after: that card was not a valid
		/// choice (3.29.4, seen live: Confirm did nothing for the first card), so choose the next one not tried yet.
		/// </summary>
		public long FateCardTimeoutMs = 8000;
		private long fateCardChosenAt;

		/// <summary>Is the augury card selection window open (see TryAugury)?</summary>
		public static bool AuguryIsOpen() {
			var window = GetUI()?.AuguryCardSelectionWindow;
			return IsValid(window) && window.IsVisible;
		}

		// the visible direct children of e (eg, the cards in the augury card selection window)
		private static List<Element> VisibleChildren(Element e) =>
			e?.Children?.Where(c => IsValid(c) && c.IsVisible).ToList() ?? new List<Element>();

		private static readonly System.Text.RegularExpressions.Regex AuguryCounter = new System.Text.RegularExpressions.Regex(@"^\d+\s*/\s*\d+$");

		/// <summary>
		/// The parts of the augury card selection window's body (its GetChild(2)), found by landmarks, not by index (they
		/// moved by one from 3.29.4 to 3.30.0d+): Confirm is the child whose child reads "confirm"; the fate cards are under
		/// the child after the one whose child 0 reads like "0/3" (the counter), and the cards held under the one after that.
		/// 3.30.0d+, seen live ("Select a Fate", three cards): 2.3 the title "Fates", 2.5 the counter (how many held, "1/3"),
		/// 2.6.0 the fate cards to choose from, 2.7.0 the cards held (shown below them: the one chosen at the last Augury Card,
		/// eg Stormcaller; none at the first), 2.10 Confirm (2.10.0 its text, 2.10.1 the same size). 3.29.4: 2.5.0, 2.6.0, 2.9.
		/// The held cards are the ones a frozen augury boss's window applies (bossCards); a fate window shows them too, so
		/// it is told apart by its title (invoking: a boss's window reads "Invoke Fates", seen live 3.30.0d; it also shows
		/// three empty, but visible, slots where the fate cards would be, so fate cards showing does not tell them apart).
		/// Null for a part not found.
		/// </summary>
		public static (Element fateCards, Element bossCards, Element confirm, bool invoking) AuguryParts(Element window) {
			var body = window?.GetChild(2)?.Children?.ToList();
			if ( body == null ) return (null, null, null, false);
			bool invoking = body.Any(c => IsValid(c) && (c.Text?.Trim() ?? "").StartsWith("Invoke", StringComparison.OrdinalIgnoreCase));
			Element confirm = body.FirstOrDefault(c => IsValid(c) && c.Children.Any(t => IsValid(t)
				&& string.Equals(t.Text?.Trim(), "confirm", StringComparison.OrdinalIgnoreCase)));
			int counter = body.FindIndex(c => IsValid(c) && AuguryCounter.IsMatch(c.GetChild(0)?.Text?.Trim() ?? ""));
			Element At(int i) => counter >= 0 && i < body.Count ? body[i]?.GetChild(0) : null;
			return (At(counter + 1), At(counter + 2), confirm, invoking);
		}

		/// <summary>
		/// While the augury card selection window is open, either:
		///  - opened by clicking a frozen augury boss (see ClickMonster): click each visible boss card (AuguryParts) once,
		///    best first (AuguryCardRank), to apply them to the boss, then click Confirm;
		///  - opened by "Select a Fate" on an AuguryCardObject (see Interactables, FateClicked; or, if not opened that way, when
		///    no boss cards show but fate cards do): Ctrl + click one of the visible fate cards (see ChooseFateCard), which
		///    needs no Confirm.
		/// Confirm is clicked again every AuguryConfirmRetryMs until the window closes. True while it is open.
		/// Called first thing in Decide (so a driver that TakesInput gets the clicks into its chain), even while the game
		/// is paused, which it is while the window is open (see OnTick).
		/// </summary>
		public bool TryAugury() {
			var window = GetUI()?.AuguryCardSelectionWindow;
			if ( !IsValid(window) || !window.IsVisible ) {
				if ( auguryWindowSeen ) Log($"{Name}: the augury card selection window closed: going on");
				if ( auguryWindowSeen && auguryWasFate ) {
					uint area = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
					if ( area != FatesArea ) { FatesArea = area; FatesTaken = 0; }
					FatesTaken += 1;
					Log($"{Name}: a fate taken ({FatesTaken} in this area)");
				}
				auguryWasFate = false;
				auguryWindowSeen = false;
				auguryCardsClicked.Clear();
				auguryConfirms = 0;
				lastAuguryConfirm = 0;
				if ( Time.ElapsedMilliseconds - lastFateClick > FateOpenMs ) selectingFate = false;
				return false;
			}
			StopWalking("the augury card selection window is open");
			long now = Time.ElapsedMilliseconds;
			var parts = AuguryParts(window);
			var bossCards = VisibleChildren(parts.bossCards);
			if ( !auguryWindowSeen ) {
				auguryWindowSeen = true;
				// (the first card click waits AuguryClickMs + InputLatency after the window shows, as between clicks: a click
				// right as it opened was often ignored)
				lastAuguryClick = now;
				Log($"{Name}: the augury card selection window ({(parts.invoking ? "a boss's: Invoke Fates" : "Select a Fate")}) is open{(selectingFate || Time.ElapsedMilliseconds - lastFateClick < FateOpenMs ? "" : " (not opened by us: eg, open when switched on)")}: {VisibleChildren(parts.fateCards).Count} fate cards to choose from, {bossCards.Count} held");
			}
			// (fate cards to choose from showing: a fate's window, whatever cards are held; 3.30.0d+, seen live: a second Augury
			// Card's window showed the first one's card held, and taking that for a boss card clicked it and then Confirm forever)
			// (a boss's window, titled "Invoke Fates", shows empty slots where the fate cards would be: never those)
			if ( !parts.invoking && (selectingFate || VisibleChildren(parts.fateCards).Count > 0) ) return TrySelectFateCard(window, now);
			// (applied best first, by AuguryCardRank: "Divine", then "Currency", then "Rarity", then the rest as they show)
			var cards = bossCards.OrderBy(AuguryCardRank).ToList();
			var card = cards.FirstOrDefault(c => !auguryCardsClicked.Contains(c.Address.ToInt64()));
			if ( card != null ) {
				Status = $"augury: applying card {auguryCardsClicked.Count + 1} of {cards.Count}";
				if ( InputBusy || now - lastAuguryClick < AuguryClickGap ) return true;
				var rect = card.GetClientRect();
				if ( rect.Width <= 0 || rect.Height <= 0 ) { auguryCardsClicked.Add(card.Address.ToInt64()); return true; } // (nothing to click)
				DefaultFollower.ReleaseMouse();
				if ( Send(Inputs.Click(rect, new Delay(Inputs.Latency))) ) {
					auguryCardsClicked.Add(card.Address.ToInt64());
					lastAuguryClick = now;
					Log($"{Name}: augury card {auguryCardsClicked.Count} of {cards.Count} clicked (rank {AuguryCardRank(card)}: {string.Join("; ", AuguryCardTexts(card).Take(4))})");
				}
				return true;
			}
			var confirm = parts.confirm;
			Status = $"augury: {auguryCardsClicked.Count} cards applied, confirming";
			if ( !IsValid(confirm) || !confirm.IsVisible ) { Status = "augury: no Confirm button (a child of GetChild(2) labelled \"confirm\")"; return true; }
			if ( TooManyConfirms() ) return true;
			if ( InputBusy || now - lastAuguryClick < AuguryClickGap || now - lastAuguryConfirm < AuguryConfirmRetryMs ) return true;
			var at = confirm.GetClientRect();
			if ( at.Width <= 0 || at.Height <= 0 ) return true;
			DefaultFollower.ReleaseMouse();
			if ( Send(Inputs.Click(at, new Delay(Inputs.Latency))) ) {
				lastAuguryClick = lastAuguryConfirm = now;
				Log($"{Name}: augury Confirm clicked ({auguryCardsClicked.Count} cards)");
					auguryConfirms += 1;
			}
			return true;
		}

		/// <summary>
		/// The reward texts of a fate card (a visible child of AuguryParts' fateCards): the tooltip of each of its
		/// reward slots, at GetChild(0, 0, 8, 0, i, 3) (hidden until hovered; eg, "Empowered Monster will drop additional
		/// Rare Ranged Weapons or Quivers"). The slot's icon (a rectangle of Augury/2.dds, at the slot's +0x230*+0x8*) says
		/// the same, less exactly. Only the visible slots (one per reward; three in all).
		/// </summary>
		public static IEnumerable<string> FateRewards(Element card) =>
			VisibleChildren(card?.GetChild(0, 0, 8, 0))
				.Select(slot => slot.GetChild(3)?.Text?.Trim())
				.Where(t => !string.IsNullOrEmpty(t));

		/// <summary>The words looked for in an augury card's texts, best first (see AuguryCardRank).</summary>
		public static readonly string[] AuguryCardPreference = { "Divine", "Stacked Deck", "Currency", "Rarity" };

		/// <summary>
		/// Every text under an augury card (a fate card or a held one), hidden ones too (a reward's text is a tooltip,
		/// hidden until hovered; see FateRewards).
		/// </summary>
		public static IEnumerable<string> AuguryCardTexts(Element card, int maxDepth = 10) {
			if ( !IsValid(card) ) yield break;
			var stack = new Stack<(Element e, int depth)>();
			stack.Push((card, 0));
			int seen = 0;
			while ( stack.Count > 0 && seen++ < 2000 ) {
				var (e, depth) = stack.Pop();
				var text = e.Text?.Trim();
				if ( !string.IsNullOrEmpty(text) ) yield return text;
				if ( depth >= maxDepth ) continue;
				foreach ( var c in e.Children ) if ( IsValid(c) ) stack.Push((c, depth + 1));
			}
		}

		/// <summary>An augury card's rank: the index in AuguryCardPreference of the first word its texts mention, or past them all.</summary>
		public static int AuguryCardRank(Element card) {
			var texts = AuguryCardTexts(card).ToList();
			for ( int i = 0; i < AuguryCardPreference.Length; i++ )
				if ( texts.Any(t => t.IndexOf(AuguryCardPreference[i], StringComparison.OrdinalIgnoreCase) >= 0) ) return i;
			return AuguryCardPreference.Length;
		}

		/// <summary>The fate card to choose: the best by AuguryCardRank ("Divine", then "Currency", then "Rarity"), else the first.</summary>
		public static Element ChooseFateCard(List<Element> cards) => cards.OrderBy(AuguryCardRank).FirstOrDefault();

		// "Select a Fate": Ctrl + click a fate card (see AuguryParts, ChooseFateCard), which chooses it with no Confirm; if
		// the window is still open FateCardTimeoutMs after, Ctrl + click the next card not tried yet; with none left, click
		// Confirm (as a plain click on a card needs)
		private bool TrySelectFateCard(Element window, long now) {
			bool timedOut = auguryCardsClicked.Count > 0 && now - fateCardChosenAt > FateCardTimeoutMs;
			var parts = AuguryParts(window);
			var untried = VisibleChildren(parts.fateCards).Where(c => !auguryCardsClicked.Contains(c.Address.ToInt64())).ToList();
			if ( auguryCardsClicked.Count == 0 || (timedOut && untried.Count > 0) ) {
				var card = ChooseFateCard(untried);
				Status = "augury: selecting a fate";
				if ( card == null ) { Status = "augury: no fate cards (after the \"0/3\" counter in GetChild(2))"; return true; }
				if ( InputBusy || now - lastAuguryClick < AuguryClickGap ) return true;
				var rect = card.GetClientRect();
				if ( rect.Width <= 0 || rect.Height <= 0 ) { Status = "augury: the first fate card has no size"; return true; }
				DefaultFollower.ReleaseMouse();
				if ( Send(Inputs.CtrlClick(rect, new Delay(Inputs.Latency))) ) {
					if ( timedOut ) Log($"{Name}: augury: the window is still open {FateCardTimeoutMs} ms after choosing a fate: that one is not valid, trying the next");
					auguryCardsClicked.Add(card.Address.ToInt64());
					auguryWasFate = true;
					lastAuguryClick = fateCardChosenAt = now;
					lastAuguryConfirm = 0;
					Log($"{Name}: augury fate chosen (Ctrl + click): {card.GetChild(0, 0, 4)?.Text?.Trim()} ({string.Join("; ", FateRewards(card))})");
				}
				return true;
			}
			// (a Ctrl + click needs no Confirm: wait for the window to close)
			if ( !timedOut ) { Status = "augury: fate chosen, waiting for the window to close"; return true; }
			// (the Confirm button is IsButton; its child 1, the same size, is hidden (a highlight?), and its child 0 the text "confirm")
			var confirm = parts.confirm?.GetChild(1);
			if ( !IsValid(confirm) || !confirm.IsVisible ) confirm = parts.confirm;
			Status = "augury: fate selected, confirming";
			if ( !IsValid(confirm) || !confirm.IsVisible ) { Status = "augury: no Confirm button (a child of GetChild(2) labelled \"confirm\")"; return true; }
			if ( TooManyConfirms() ) return true;
			if ( InputBusy || now - lastAuguryClick < AuguryClickGap || now - lastAuguryConfirm < AuguryConfirmRetryMs ) return true;
			var at = confirm.GetClientRect();
			if ( at.Width <= 0 || at.Height <= 0 ) return true;
			DefaultFollower.ReleaseMouse();
			if ( Send(Inputs.Click(at, new Delay(Inputs.Latency))) ) {
				lastAuguryClick = lastAuguryConfirm = now;
				Log($"{Name}: augury fate Confirm clicked");
					auguryConfirms += 1;
			}
			return true;
		}

		// (Confirm clicked MaxAuguryConfirms times and the window is still open: stop, rather than click it forever)
		private bool TooManyConfirms() {
			if ( auguryConfirms < MaxAuguryConfirms ) return false;
			Log($"{Name}: augury: Confirm clicked {auguryConfirms} times and the window is still open: stopping");
			Status = "augury: stuck (Confirm does not close the window)";
			auguryConfirms = 0;
			Stop("augury: Confirm does not close the window");
			return true;
		}

		public const string PATH_AUGURY_CARD_OBJECT = "Metadata/Terrain/Leagues/Augury/Objects/AuguryCardObject";
		/// <summary>How long after clicking an AuguryCardObject its window counts as opened by that click.</summary>
		public long FateOpenMs = 3000;
		private bool selectingFate;
		private long lastFateClick;

		/// <summary>
		/// "Select a Fate" was clicked on an AuguryCardObject (see Interactables' Augury Card): a window opening within
		/// FateOpenMs is the fate's (TryAugury chooses a card in it).
		/// </summary>
		public void FateClicked() {
			lastFateClick = Time.ElapsedMilliseconds;
			selectingFate = true;
		}

		/// <summary>Is e an AuguryCardObject whose fate has not been selected yet (its StateMachine's card_chosen is 0)?</summary>
		public static bool IsUnusedAuguryCard(Entity e) => IsValid(e) && e.Path == PATH_AUGURY_CARD_OBJECT
			&& (e.GetComponent<StateMachine>()?.TryGetState("card_chosen", out long chosen) ?? false) && chosen == 0
			&& (e.GetComponent<Targetable>()?.IsTargetable ?? true);

		private readonly Interactables interactables = new Interactables();

		/// <summary>
		/// Use the nearest thing worth clicking in range (see Interactables: an augury card's "Select a Fate", a Demonic
		/// Evocator, a Crafting Recipe, a Shrine, ...; each kind on or off in Combat's Interactables, see CombatSection):
		/// walk to it and click it. Not with monsters within AttackRange (fight them first), nor with the inventory or the
		/// augury window open. True while doing so.
		/// </summary>
		public bool TryInteract(CombatContext ctx) {
			if ( AuguryIsOpen() || BackpackIsOpen() ) return false;
			if ( TryActivateSentinelDrone() ) return true;
			if ( !Interactables.AnyEnabled ) return false;
			if ( ctx.Hostiles.Any(h => h.Distance <= AttackRange) ) return false;
			if ( !interactables.TryUse(this, ctx) ) return false;
			Status = interactables.Status;
			return true;
		}

		/// <summary>Press the Sentinel drone's key again only after this long (the button takes a moment to change).</summary>
		public long SentinelDroneKeyMs = 1500;
		private long lastSentinelDroneKey;
		private string sentinelKeyNotUnderstood;

		/// <summary>
		/// Once a Sentinel has been clicked (the Sentinel kind of Interactables, when switched on) its drone button shows
		/// the key that activates it (UIElementLibrary.SentinelDroneKey): press that key. Done with monsters near too
		/// (the drone empowers them). True when the key was sent.
		/// </summary>
		public bool TryActivateSentinelDrone() {
			if ( !Interactables.Kinds.Any(k => k.Name == "Sentinel" && Interactables.IsEnabled(k)) ) return false;
			string text = GetUI()?.SentinelDroneKey;
			if ( text == null || InputBusy || Time.ElapsedMilliseconds - lastSentinelDroneKey < SentinelDroneKeyMs ) return false;
			VKey key = GameKeys.Parse(text);
			if ( key == VKey.None || (key & VKey.Modifiers) != VKey.None ) {
				if ( sentinelKeyNotUnderstood != text ) Log($"{Name}: the Sentinel drone's key \"{text}\" is not one it can press");
				sentinelKeyNotUnderstood = text;
				return false;
			}
			if ( !Send(Inputs.Press(key)) ) return false;
			lastSentinelDroneKey = Time.ElapsedMilliseconds;
			Status = $"activating the Sentinel drone ({text})";
			Log($"{Name}: pressed {text} to activate the Sentinel drone");
			return true;
		}

		/// <summary>How near (in cells) to be to a monster to click it (see ClickMonster).</summary>
		public float MonsterClickReach = 20;
		/// <summary>How often to click a monster that has to be clicked, until it no longer has to be.</summary>
		public long MonsterClickMs = 300;
		private long lastMonsterClick;

		/// <summary>
		/// Click target, a monster that has to be clicked before it can be fought (see Combat.ClickReason): a monster
		/// inside a monolith (MonsterInsideMonolith = 1, and CannotBeDamaged until it is out; rare and unique only), or a unique frozen for an
		/// augury (which opens the augury card selection window). Walks near it, then clicks it every MonsterClickMs
		/// (the caller stops asking once it no longer has to be clicked). True while doing so.
		/// </summary>
		public bool ClickMonster(CombatContext ctx, Hostile target) {
			Target = target;
			string why = Combat.ClickReason(target.Entity) ?? "to click";
			if ( target.Distance > MonsterClickReach ) {
				var spot = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, target.GridPos, out var s) ? s : target.GridPos;
				if ( Combat.Distance(ctx.Grid, spot) > 3 ) {
					WalkTo(spot, $"walking to {target}, {why}");
					return true;
				}
			}
			StopWalking("clicking a monster");
			Status = $"clicking {target}, {why}";
			if ( InputBusy || binding || Time.ElapsedMilliseconds - lastMonsterClick < MonsterClickMs ) return true;
			var click = Ground.ClickEntity(target.Entity);
			if ( click == null ) {
				// off screen or under a panel: get nearer
				WalkTo(target.GridPos, $"walking to {target}, {why} (can't click it from here)");
				return true;
			}
			DefaultFollower.ReleaseMouse();
			if ( Send(click) ) lastMonsterClick = Time.ElapsedMilliseconds;
			return true;
		}

		/// <summary>The totem skills to keep placed while fighting (see PlaceTotem).</summary>
		public static readonly string[] TotemSkills = { "Decoy Totem" };
		/// <summary>After a totem is placed, its DeployedObjects entry shows up a moment later: no other totem cast until then.</summary>
		public long TotemSettleMs = 1000;
		private long lastTotem;

		/// <summary>While fighting: PlaceTotem for each of TotemSkills, until one is cast. True if one was.</summary>
		public bool TryPlaceTotem(CombatContext ctx, Vector2 targetScreen) {
			foreach ( var name in TotemSkills ) {
				if ( PlaceTotem(ctx, name, targetScreen) ) return true;
			}
			return false;
		}

		/// <summary>How many totems the skill may have out at once (its SkillDisplayNumberOfTotemsAllowed stat; 1 if it has none).</summary>
		public static int TotemsAllowed(ActorSkill skill) =>
			skill != null && skill.TryGetStat(Offsets.GameStat.SkillDisplayNumberOfTotemsAllowed, out int n) && n > 0 ? n : 1;

		/// <summary>
		/// How many totems of the skill are out: the player's DeployedObjects entries with its skill id. If no entry has
		/// the id of any of the player's skills (the entry's SkillId offset is not confirmed), the totem entities among
		/// them (path with "/Totems/"; eg, Decoy Totem's is not named for it) are counted instead.
		/// </summary>
		public static int TotemsPlaced(Actor actor, ActorSkill skill) {
			if ( !IsValid(actor) || skill == null ) return 0;
			var deployed = actor.DeployedObjects.ToList();
			int byId = deployed.Count(d => d.SkillId == skill.Id);
			if ( byId > 0 ) return byId;
			var ids = new HashSet<uint>(actor.Skills.Select(s => (uint)s.Id));
			if ( deployed.Any(d => ids.Contains(d.SkillId)) ) return 0; // (the ids read right: none of this skill's are out)
			return deployed.Count(d => d.GetEntity()?.Path?.Contains("/Totems/", StringComparison.OrdinalIgnoreCase) ?? false);
		}

		/// <summary>
		/// Keep the totem skill called name at its number of totems allowed (see TotemsAllowed): if it is on the skill
		/// bar, usable, off cooldown, and fewer of its totems are out (see TotemsPlaced), cast it halfway between the
		/// player and targetScreen (or at targetScreen, if halfway can't be clicked). True if it was cast.
		/// </summary>
		public bool PlaceTotem(CombatContext ctx, string name, Vector2 targetScreen) {
			if ( InputBusy || binding || Time.ElapsedMilliseconds - lastTotem < TotemSettleMs ) return false;
			var slot = SkillBar.FindSlot(name);
			var skill = slot?.Skill;
			if ( skill == null || !skill.CanBeUsed || skill.IsOnCooldown ) return false;
			var actor = ctx.Player?.GetComponent<Actor>();
			int placed = TotemsPlaced(actor, skill), allowed = TotemsAllowed(skill);
			if ( placed >= allowed ) return false;
			if ( !TryWorldToScreen(Position(ctx.Player), out Vector2 me) ) return false;
			var at = (me + targetScreen) / 2;
			if ( !PathFollower.IsSafeToClick(at) ) at = targetScreen;
			DefaultFollower.ReleaseMouse();
			if ( !Send(Inputs.UseSlot(slot, at, 40)) ) return false;
			lastTotem = Time.ElapsedMilliseconds;
			Log($"{Name}: placing {name} ({placed} of {allowed} out)");
			return true;
		}

		private long lastPacedUse;
		/// <summary>
		/// UseSkill, but no more often than the skill's cast time (and at least 150 ms apart). A channelling skill (see
		/// IsChannelling) is held down instead (see TryHoldChannel).
		/// </summary>
		public bool UseSkillPaced(string skill, Vector2 screenPos) {
			if ( TryHoldChannel(skill, screenPos, out bool held) ) return held;
			int interval = Math.Max(150, SkillBar.CastTimeMs(SkillBar.FindSlot(skill)?.Skill));
			if ( Time.ElapsedMilliseconds - lastPacedUse < interval ) return false;
			if ( !UseSkill(skill, screenPos) ) return false;
			lastPacedUse = Time.ElapsedMilliseconds;
			return true;
		}

		/// <summary>Is skill a channelling skill (the Channelling tag, or the SkillIsChannelled stat): used by holding its key down.</summary>
		public static bool IsChannelling(ActorSkill skill) {
			if ( skill == null ) return false;
			if ( skill.TryGetStat(Offsets.GameStat.SkillIsChannelled, out int c) && c != 0 ) return true;
			return SkillBar.SkillTags(skill).Any(t => string.Equals(t, "Channelling", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(t, "Channeling", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Channel", StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// The channelling skill's slot whose key (or mouse button) is held down now (see TryHoldChannel), or null. It is let
		/// go (ReleaseChannel) once the player walks, other input is sent, the driver waits or stops, or nothing has asked
		/// for it to be held for ChannelIdleMs.
		/// </summary>
		private SkillSlot heldSlot;
		private Vector2 heldAim;
		private long lastHoldCall, heldSince;
		// this hold: has the IsChannelling stat been seen at 1 yet; and since when it has read 0 after that (-1: not 0)
		private bool channelSeen;
		private long channelZeroSince = -1;
		/// <summary>
		/// A channelling skill held whose IsChannelling stat was seen at 1, and then reads 0 for this long, was interrupted
		/// (eg, by a stun: the key is still down but nothing is cast): let go of it, and the next TryHoldChannel presses it
		/// again. A hold whose stat was never seen at 1 is left alone (seen live: the re-press then never raised it either,
		/// and let go every 0.7 s, so Scorching Ray never ramped up). (The Stats are read at most a second old.)
		/// </summary>
		public long ChannelCheckMs = 1500;
		/// <summary>A held channelling skill not asked for (by UseSkillPaced) for this long is let go.</summary>
		public long ChannelIdleMs = 300;
		/// <summary>Move the cursor after the target (while channelling) once it is this many pixels from where it aims.</summary>
		public float ChannelAimPixels = 8;

		/// <summary>True while a channelling skill is held down (see TryHoldChannel).</summary>
		public bool IsChannellingNow => heldSlot != null;

		/// <summary>
		/// If skill is a channelling skill on the skill bar (on a key with no modifiers, or a mouse button): hold its key
		/// down aimed at screenPos, or, if it is held already, keep the cursor on screenPos; true (handled), with held true
		/// once it is down. False for any other skill (use it as usual).
		/// </summary>
		public bool TryHoldChannel(string skill, Vector2 screenPos, out bool held) {
			held = false;
			if ( binding || string.IsNullOrEmpty(skill) ) return false;
			var slot = SkillBar.FindSlot(skill);
			if ( slot?.Skill == null || slot.Key == VKey.None || (slot.Key & (VKey.Control | VKey.Shift | VKey.Alt)) != 0 ) return false;
			if ( !IsChannelling(slot.Skill) ) return false;
			lastHoldCall = Time.ElapsedMilliseconds;
			if ( heldSlot != null && heldSlot.Index == slot.Index && heldSlot.Key == slot.Key ) {
				if ( Vector2.Distance(screenPos, heldAim) > ChannelAimPixels ) {
					SkillBar.MouseTo(screenPos);
					heldAim = screenPos;
				}
				held = true;
				return true;
			}
			ReleaseChannel("another skill");
			if ( InputBusy ) return true; // (once the input going out is done)
			if ( !GameFocused ) { PauseUntilFocused(); return true; } // (nothing held while another window is in front)
			DefaultFollower.ReleaseMouse(); // (walking holds the left button)
			var button = SkillBar.ToMouseButton(slot.KeyCode);
			// (the press after the aim; and not at all if ReleaseChannel let go before it went out)
			if ( button.HasValue ) Run(Inputs.MoveTo(screenPos, MouseHold.Press(button.Value)));
			else { SkillBar.MouseTo(screenPos); Platform.Current.Input.KeyDown(slot.KeyCode); }
			heldSlot = slot;
			heldAim = screenPos;
			Log($"{Name}: channelling {skill}: holding {slot.KeyCode}");
			heldSince = Time.ElapsedMilliseconds;
			channelSeen = false;
			channelZeroSince = -1;
			held = true;
			return true;
		}

		/// <summary>Let go of the channelling skill's key, if one is held (see TryHoldChannel).</summary>
		public void ReleaseChannel(string why) {
			var slot = heldSlot;
			if ( slot == null ) return;
			heldSlot = null;
			var button = SkillBar.ToMouseButton(slot.KeyCode);
			if ( button.HasValue ) Run(MouseHold.Release(button.Value));
			else Platform.Current.Input.KeyUp(slot.KeyCode);
			Log($"{Name}: channelling stopped ({why}): released {slot.KeyCode}");
		}

		// after Decide: let go of a held channelling skill once the player walks, or nothing asked for it lately
		private void CheckChannel() {
			if ( heldSlot == null ) return;
			if ( !GameFocused ) ReleaseChannel("the game lost the focus");
			else if ( DefaultFollower.IsActiveFor(this) ) ReleaseChannel("walking");
			else if ( Time.ElapsedMilliseconds - lastHoldCall > ChannelIdleMs ) ReleaseChannel("not fighting");
			else if ( ChannelInterrupted() )
				ReleaseChannel("interrupted: the IsChannelling stat is 0; pressing it again");
		}

		// the held skill's channel was seen going, and has stopped for ChannelCheckMs since (see ChannelCheckMs)
		private bool ChannelInterrupted() {
			long now = Time.ElapsedMilliseconds;
			bool? on = PlayerChannelling();
			if ( on == true ) { channelSeen = true; channelZeroSince = -1; return false; }
			if ( on != false || !channelSeen ) return false;
			if ( channelZeroSince < 0 ) channelZeroSince = now;
			return now - channelZeroSince > ChannelCheckMs;
		}

		// the player's IsChannelling stat: true or false, or null if the stats can't be read
		private static bool? PlayerChannelling() {
			var stats = GetPlayer()?.GetStats();
			if ( stats == null || stats.Count == 0 ) return null;
			return stats.TryGetValue(Offsets.GameStat.IsChannelling, out int v) && v != 0;
		}

		/// <summary>
		/// True once TryExplore has found nothing left to explore in this area instance (its last pick was made here:
		/// "explored everything" from the last map does not carry over to the next, seen live 3.30.0d, two Mud Geysers in
		/// a row: the next one's fates and first clearing round ended at once).
		/// </summary>
		public bool ExploreDone => exploreTarget != null && exploreTarget.Kind == ExploreTargetKind.None && exploreTargetArea == CurrentAreaHash();
		private uint exploreTargetArea; // (the area instance, WorldData.AreaHash, exploreTarget was picked in)
		private static uint CurrentAreaHash() => PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;

		/// <summary>Forget the place TryExplore was walking to (eg, after DefaultExplorer.Rescan): the next call picks again.</summary>
		public void ForgetExploreTarget() { exploreTarget = null; exploreArrivedSince = -1; }

		/// <summary>The place TryExplore is walking to (null before the first call).</summary>
		public ExploreTarget ExploreTarget => exploreTarget;

		/// <summary>
		/// Exploring, the player not getting ExploreStuckCells nearer the place it is walking to for this long (jittering on
		/// the spot counts as not getting nearer): it can't be reached (eg, a spot the paths say is walkable but is not), so
		/// give up on it (DefaultExplorer.GiveUp) and explore somewhere else.
		/// </summary>
		public long ExploreStuckMs = 10000; // (long enough for a path that winds away from the place for a while)
		public float ExploreStuckCells = 8;
		/// <summary>The cells around a place given up on that count as explored (so it is not chosen again).</summary>
		public int ExploreGiveUpRadius = 24;
		private Offsets.Vector2i exploreProgressTarget;
		private float exploreBestDistance;
		private long exploreProgressTime, lastExploreCall;
		/// <summary>
		/// Arrived at the place exploring (within ExploreStuckCells), and it is still chosen this long after: give up on it
		/// at once, not after ExploreStuckMs (seen live: at the start of a map the first place chosen was 2 cells from the
		/// player, chosen again and again, and the player stood there for the whole 10 s).
		/// </summary>
		public long ExploreArrivedMs = 1000;
		private long exploreArrivedSince = -1;

		/// <summary>Walk to the next unexplored place; false if there is none left.</summary>
		public bool TryExplore(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			// progress: the nearest the player has been to the place, since it was chosen (a new place, more than
			// ExploreStuckCells from the last, starts over; time spent not exploring, eg fighting, does not count)
			if ( exploreTarget != null && exploreTarget.Kind == ExploreTargetKind.Explore ) {
				float d = Combat.Distance(ctx.Grid, exploreTarget.GridPos);
				if ( now - lastExploreCall > 1000 || Combat.Distance(exploreTarget.GridPos, exploreProgressTarget) > ExploreStuckCells
					|| d < exploreBestDistance - ExploreStuckCells ) {
					exploreProgressTarget = exploreTarget.GridPos;
					exploreBestDistance = d;
					exploreProgressTime = now;
				}
				bool arrived = d <= ExploreStuckCells && DefaultFollower.Status == FollowStatus.Arrived;
				if ( !arrived ) exploreArrivedSince = -1;
				else if ( exploreArrivedSince < 0 || now - lastExploreCall > 1000 ) exploreArrivedSince = now;
				if ( arrived && now - exploreArrivedSince > ExploreArrivedMs ) {
					Log($"{Name}: exploring: at {exploreTarget.GridPos} ({d:F0} cells away) for {ExploreArrivedMs} ms and it is still chosen: counting it as explored");
					DefaultExplorer.GiveUp(ctx.Terrain, exploreTarget.GridPos, ExploreGiveUpRadius);
					exploreTarget = null;
					exploreProgressTime = now;
					exploreArrivedSince = -1;
				} else if ( now - exploreProgressTime > ExploreStuckMs ) {
					Log($"{Name}: exploring: not {ExploreStuckCells:F0} cells nearer {exploreTarget.GridPos} in {ExploreStuckMs} ms (at {ctx.Grid}, {d:F0} cells away; {DefaultFollower.Status}: {DefaultFollower.Detail}): giving up on it, exploring somewhere else");
					DefaultExplorer.GiveUp(ctx.Terrain, exploreTarget.GridPos, ExploreGiveUpRadius);
					exploreTarget = null;
					exploreProgressTime = now;
				}
			}
			lastExploreCall = now;
			// look for the next place twice a second, it moves as the area is explored
			if ( exploreTarget == null || now - exploreTargetTime > 500 || DefaultFollower.Status == FollowStatus.Arrived ) {
				exploreTarget = DefaultExplorer.FindNextTarget(DefaultNavigator, ctx.Terrain, ctx.Grid, monsters: false);
				exploreTargetTime = now;
				exploreTargetArea = CurrentAreaHash();
				// (nothing found only because the explorer's record is not this terrain's yet, just after an area change: not done)
				if ( exploreTarget.Kind == ExploreTargetKind.None && !DefaultExplorer.Covers(ctx.Terrain) ) { exploreTarget = null; return true; }
			}
			if ( exploreTarget.Kind == ExploreTargetKind.None ) return false;
			WalkTo(exploreTarget.GridPos, $"exploring {exploreTarget.GridPos} ({DefaultExplorer.ExploredFraction:P0} explored)");
			return true;
		}

		/// <summary>Walk the player to goal (the walking happens after Decide returns).</summary>
		public void WalkTo(Offsets.Vector2i goal, string status) {
			Status = status;
			Combat.PathTo(goal, this);
		}

		/// <summary>Stop walking (eg, to click something).</summary>
		public void StopWalking(string why) {
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
		}

		/// <summary>The slot of the main skill, tapped to stop the player moving (see StopMoving): by default the slot on Q.</summary>
		protected virtual SkillSlot MainSkillSlot() => SkillBar.GetSlot(BuildPlan.MainSlot);

		/// <summary>Touching a trap while walking (see PathFollower.TrapTouchMs): a travel skill past it, if UseTravelSkills.</summary>
		public bool TryDodgeTrap(PathFollower follower, Terrain terrain, Offsets.Vector2i player, Offsets.Vector2i trap, float z) =>
			UseTravelSkills && TravelSkills.Instance.TryDodge(this, terrain, player, trap, z);

		public bool HasTravelSkill => UseTravelSkills && TravelSkills.Instance.HasAny;

		/// <summary>The path goes through a chest or barrel: a travel skill past it (see TravelSkills.TrySkip).</summary>
		public bool TrySkipObstacle(PathFollower follower, Terrain terrain, Offsets.Vector2i player, Offsets.Vector2i obstacle, int obstacleIndex, float z) =>
			UseTravelSkills && TravelSkills.Instance.TrySkip(this, () => InputBusy, Send, terrain, player, obstacle, obstacleIndex, z);

		/// <summary>The path crosses danger: a travel skill over it (see TravelSkills.TrySkipDanger).</summary>
		public bool TrySkipDanger(PathFollower follower, Terrain terrain, Offsets.Vector2i player, int landIndex, float z) =>
			UseTravelSkills && TravelSkills.Instance.TrySkipDanger(this, () => InputBusy, Send, terrain, player, landIndex, z);

		/// <summary>
		/// Stop the player at once: tap the main skill for one InputLatency (too short to cast, but it ends the walk), where
		/// the cursor is. False if there is no main skill on the bar (or it is Movement), or input is still going out.
		/// </summary>
		public bool StopMoving() {
			if ( SkillsPausedHere() ) return false; // (no spells in a town or hideout, not even a tap)
			var slot = MainSkillSlot();
			if ( slot?.Skill == null || SkillBar.IsMovement(slot.Skill) || slot.Key == VKey.None ) return false;
			DefaultFollower.ReleaseMouse();
			return Send(Inputs.UseSlotHere(slot, Inputs.Latency));
		}

		/// <summary>Set the status line (shown at the bottom left).</summary>
		public void SetStatus(string status) => Status = status;

		/// <summary>
		/// Use the skill called name (eg, "Fireball") aimed at screenPos: press the key of the slot it is on, or, if it is
		/// not on the skill bar, put it on a free slot first (then this returns false until that is done).
		/// </summary>
		public bool UseSkill(string name, Vector2 screenPos, uint holdMs = 40) {
			if ( binding ) return false;
			var slot = SkillBar.FindSlot(name);
			if ( slot == null ) {
				var free = SkillBar.FindFreeSlot();
				if ( free == null ) {
					Status = $"no free slot for {name}";
					return false;
				}
				Bind(free.Index, name);
				return false;
			}
			if ( InputBusy ) return false;
			DefaultFollower.ReleaseMouse(); // (walking holds the left button: let go of it to fight)
			return Send(Inputs.UseSlot(slot, screenPos, holdMs));
		}

		/// <summary>Put name on a slot (see SkillBar.BindSkill); the driver waits until it is done, and stops if it fails.</summary>
		public void Bind(int slotIndex, string name) {
			if ( binding ) return;
			ReleaseChannel("changing the skill bar");
			binding = true;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "changing the skill bar");
			Run(SkillBar.BindSkill(slotIndex, name, (ok, why) => {
				binding = false;
				if ( !ok ) Stop($"could not put {name} on slot {slotIndex}: {why}");
			}));
		}

		private IState Wait(string why) {
			ReleaseChannel(why); // (eg, a key held down while typing in the chat would type)
			Status = $"waiting: {why}";
			DrawBottomLeftText($"{Name}: {Status}", Color.Gray);
			return this;
		}
	}

	/// <summary>
	/// What a combat driver knows about the current frame, for deciding what to do.
	/// </summary>
	public class CombatContext {
		public readonly CombatDriver Driver;
		public readonly InGameState Game;
		public readonly Terrain Terrain;
		public readonly PlayerEntity Player;
		public readonly Offsets.Vector2i Grid;
		public readonly float Z;
		private List<Hostile> hostiles;
		public CombatContext(CombatDriver driver, InGameState game, Terrain terrain, PlayerEntity player, Offsets.Vector2i grid, float z) {
			Driver = driver; Game = game; Terrain = terrain; Player = player; Grid = grid; Z = z;
		}
		/// <summary>The hostile monsters within the driver's EngageRadius that can be walked to, nearest first.</summary>
		public List<Hostile> Hostiles => hostiles ??= Combat.GetHostiles(Grid, Driver.EngageRadius)
			.Where(h => Driver.Wanted(h) && DefaultNavigator.IsReachable(Terrain, Grid, h.GridPos)).ToList();
		private List<Hostile> monstersToClick;
		/// <summary>
		/// The monsters within the driver's EngageRadius to click before fighting (inside a monolith, or a frozen augury
		/// boss; see Combat.ClickReason), nearest first. Not those Driver.Ignore leaves alone: eg, the map's frozen augury
		/// boss while its fates are not all taken, which a click would open the card window of (seen live 3.30.0d, Mud
		/// Geyser: the boss clicked with 1 of 3 fates).
		/// </summary>
		public List<Hostile> MonstersToClick => monstersToClick ??= Combat.GetMonstersToClick(Grid, Driver.EngageRadius).Where(Driver.Wanted).ToList();
	}

	/// <summary>
	/// The simplest combat driver: aim the cursor at the nearest monster and press one skill key.
	/// Fits most single-skill builds (a spell, a bow attack, a melee strike with a short range).
	/// </summary>
	public class SkillAttackDriver : CombatDriver {
		public override string Name => "Skill Attack";

		/// <summary>The skill to attack with, by name (eg, Fireball); it is put on a free slot if needed. Empty: press AttackKey instead.</summary>
		public string AttackSkill = "";
		public HotKey AttackKey = new HotKey(VKey.Q);
		public int Range = 30; // cells
		public bool LineOfSight = true;
		public int AttackIntervalMs = 250;
		public int HoldMs = 40;

		public override float AttackRange => Range;
		public override bool NeedsLineOfSight => LineOfSight;

		private long lastAttack;

		public override void Render() {
			base.Render();
			ImGui.Separator();
			ImGui.InputText("Attack Skill", ref AttackSkill, 64);
			ImGui.SameLine();
			ImGui_HelpMarker("A skill's name, eg Fireball. It is put on a free slot if it is not on the skill bar, and used as fast as its cast time allows. Leave empty to press the Attack Skill Key instead.");
			if ( string.IsNullOrWhiteSpace(AttackSkill) ) ImGui_HotKeyButton("Attack Skill Key", ref AttackKey);
			ImGui.SliderInt("Attack Range (cells)", ref Range, 3, 80);
			ImGui.Checkbox("Needs Line of Sight", ref LineOfSight);
			ImGui.SliderInt("Attack Interval (ms)", ref AttackIntervalMs, 50, 1500);
			ImGui.SliderInt("Hold Key (ms)", ref HoldMs, 10, 500);
		}

		protected override void Attack(Hostile target, Vector2 screenPos) {
			if ( !string.IsNullOrWhiteSpace(AttackSkill) ) {
				if ( TryHoldChannel(AttackSkill.Trim(), screenPos, out _) ) return;
				var slot = SkillBar.FindSlot(AttackSkill.Trim());
				int interval = Math.Max(AttackIntervalMs, SkillBar.CastTimeMs(slot?.Skill));
				if ( Time.ElapsedMilliseconds - lastAttack < interval ) return;
				if ( UseSkill(AttackSkill.Trim(), screenPos, (uint)HoldMs) ) lastAttack = Time.ElapsedMilliseconds;
				return;
			}
			if ( AttackKey.Key == VKey.None || Time.ElapsedMilliseconds - lastAttack < AttackIntervalMs ) return;
			lastAttack = Time.ElapsedMilliseconds;
			// aim with the real cursor (the game only reads it while focused: MoveMouse waits for that), then press the
			// skill key (which can be posted)
			Run(new MoveMouse(screenPos + WindowOffset(), new PressKey(AttackKey.Key, (uint)HoldMs)));
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;
	}
}
