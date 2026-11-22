using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Mapping loop, one lap at a time:
	///   StashEverything, PickNextMap, RollMap, OpenMap (which ends with EnterMap and WaitForNewArea): from the hideout into a map
	///   ClearArea(name)       the map's rules (MapRules, by its name) if it has some, then clear it until fewer than
	///                         MonstersLeft monsters remain and the game says "Map Complete"
	///   ReturnHome            a Portal Scroll, enter its portal, WaitForNewArea
	///   then the next lap, from StashEverything.
	/// Switched on in a map, the hideout steps (and EnterMap, WaitForNewArea) skip themselves (SkipToClear) and the lap
	/// starts at ClearArea.
	///
	/// Each lap is built when the one before it ends (NextLap), never all at once, so nothing points back to an old lap
	/// and an old lap is let go of as soon as it is left: the chain does not grow from lap to lap.
	/// </summary>
	public partial class MappingRoutine {

		/// <summary>
		/// The routine was switched on in a map, and the loop has not got to ClearArea yet: StashEverything, PickNextMap,
		/// RollMap, OpenMap, EnterMap and WaitForNewArea skip themselves (go on to next).
		/// </summary>
		public bool SkipToClear { get; private set; }

		/// <summary>
		/// The routine was switched on in the hideout with MapPortalCount map portals up (a map opened and not entered yet),
		/// and the loop has not got to EnterMap yet: StashEverything, PickNextMap and RollMap skip themselves, and OpenMap goes
		/// straight to EnterMap, which takes the nearest of those portals. Only at the start: back from a map, its portals are
		/// up too, and lead back into it.
		/// </summary>
		public bool SkipToPortals { get; private set; }
		/// <summary>
		/// Between ClearArea and ReturnHome: the loop is clearing a map. Back in the hideout while this is set (eg, died and
		/// resurrected there), MapLoop starts a new lap that goes back in through the map's portals that are left (ReEnter).
		/// </summary>
		public bool Clearing { get; internal set; }
		/// <summary>Set by MapLoop for the next StartLoop: enter any of the map's portals that are left (one or more), not only all six.</summary>
		internal bool ReEnter;
		/// <summary>Set on entering a map the loop did not mean to (not through EnterMap, and not while clearing one); see LeaveStrayMap.</summary>
		internal bool Strayed;

		/// <summary>How long LeaveStrayMap waits for the map panel to say Map Complete.</summary>
		private const long StrayCompleteWaitMs = 3000;

		/// <summary>
		/// In a map entered by mistake (Strayed: eg, a portal beside the Map Device clicked instead of it): if it is done
		/// (Map Complete, within StrayCompleteWaitMs), back out through the portal it was entered by (a town portal near;
		/// with none, ReturnHome's Portal Scroll), then a new lap; if it is not done, a new lap that clears it (as if
		/// switched on in it).
		/// </summary>
		public IState LeaveStrayMap() {
			var newLap = new Step("Start a new lap", (s, ctx, dt) => {
				s.Pass();
				StartLoop();
				return MapLap();
			}, null);
			return new Step("Leave a map entered by mistake", (s, ctx, dt) => {
				if ( InHideout() ) return newLap;
				if ( !MapComplete && s.Elapsed < StrayCompleteWaitMs ) { s.Detail = "is it done?"; ctx.Driver.StopWalking("looking at the map entered by mistake"); return s; }
				string area = GetCurrentArea()?.Name ?? "the map";
				if ( !MapComplete ) {
					Log($"{Name}: {area}, entered by mistake, is not done: clearing it");
					return newLap;
				}
				if ( BackpackIsOpen() ) return GameKeys.Press(GameKeys.InventoryKey, new Delay(InventoryPlans.Settle, s));
				if ( TownPortals().Any() ) {
					Log($"{Name}: {area}, entered by mistake, is done (Map Complete): going back out through its portal");
					return EnterFreshPortal("the portal back", TownPortals, null, newLap);
				}
				Log($"{Name}: {area}, entered by mistake, is done (Map Complete), and no portal is near: a Portal Scroll");
				return ReturnHome(newLap);
			}, newLap);
		}
		/// <summary>How many portals a Map Device opens (see SkipToPortals).</summary>
		public const int MapPortalCount = 6;

		/// <summary>One lap of the loop, from the hideout back to the hideout, ending in the next lap (built when it is reached).</summary>
		public IState MapLap() =>
			StashEverything(PickNextMap(RollMap(OpenMap(
				ClearArea(null, ReturnHome(NextLap()))))));

		// the end of a lap: build the next one (and let go of this one)
		private IState NextLap() => new Step("Next lap", (s, ctx, dt) => {
			s.Pass();
			Log($"{Name}: back from the map ({MapsRun} run): the next lap");
			return MapLap();
		}, null);

		/// <summary>
		/// Walk to a fresh portal (one opened by this Activate, OpenMap.FreshPortals; the nearest), click it, then
		/// WaitForNewArea, then next. A portal that disappears on the way (eg, another player took it) is given up for
		/// another fresh one. Stops (null) with no fresh portal. Closes the Map Device first (it covers the portals).
		/// </summary>
		public IState EnterMap(IState next) => new Step("Enter the map", (s, ctx, dt) => {
			if ( SkipToClear ) return s.Pass();
			if ( MapDeviceIsOpen() ) return CloseWindow("the Map Device", s);
			// (switched on with the portals up: any of them, not only ones opened by an Activate of ours)
			bool all = SkipToPortals;
			SkipToPortals = false;
			return EnterFreshPortal("a map portal", () => all ? MapDevice.MapPortals() : AtE.OpenMap.FreshPortals(this), EnteringMap, s.Pass());
		}, next);

		/// <summary>How long WaitForNewArea waits for the loading to start (after a portal click), and to end.</summary>
		public long LoadStartMs = 20000, LoadEndMs = 60000; // (a map's portals can take a while to spawn)

		/// <summary>
		/// After a click that leaves the area (fromArea: its AreaHash then): wait for AreaLoadingState.IsLoading to become
		/// true, then false (with the player there), then next. Stops (null) if the loading does not start within LoadStartMs,
		/// or does not end within LoadEndMs. The routine is not ticked while the game shows the loading screen, so the area's
		/// hash having changed also counts as the loading having started (it may come and go between two ticks).
		/// </summary>
		public IState WaitForNewArea(uint fromArea, IState next) {
			bool started = false;
			return new Step("Wait for the new area", (s, ctx, dt) => {
				if ( SkipToClear ) return s.Pass();
				bool loading = PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? false;
				bool moved = AreaHash() != fromArea && AreaHash() != 0;
				if ( !started ) {
					if ( loading || moved ) {
						started = true;
						s.Elapsed = 0;
						Log($"{Name}: the new area is loading");
					} else if ( s.Elapsed > LoadStartMs ) return Fail($"the new area did not start loading within {LoadStartMs} ms");
					else { s.Detail = "waiting for the loading to start"; return s; }
				}
				if ( !loading && moved && IsValid(GetPlayer()) ) {
					Log($"{Name}: in {GetCurrentArea()?.Name ?? "the new area"} ({s.Elapsed} ms of loading seen)");
					return s.Pass();
				}
				if ( s.Elapsed > LoadEndMs ) return Fail($"the new area did not finish loading within {LoadEndMs} ms");
				s.Detail = "loading";
				return s;
			}, next);
		}

		// pick the nearest of fresh (eg, the portals the map device just opened), walk to it, click it, WaitForNewArea; if
		// the one picked goes on the way, pick another; none: stop. onClick is called as it is clicked (eg, EnteringMap)
		private IState EnterFreshPortal(string what, Func<IEnumerable<Entity>> fresh, Action onClick, IState next) {
			Step pick = null;
			pick = new Step($"Pick {what}", (s, ctx, dt) => {
				var portal = NearestOf(fresh());
				if ( portal == null ) return Fail($"no {what} to enter");
				Log($"{Name}: going to {what} ({portal.Id})");
				return ApproachPortal(what, portal.Id, fresh, onClick, pick, next);
			}, next);
			return pick;
		}

		/// <summary>How long a portal may take to open (targetable, its label showing) before it is clicked anyway.</summary>
		private const long PortalReadyMs = 10000;
		/// <summary>How long after a portal click the loading must start before it is clicked again, and how many clicks at most.</summary>
		private const long PortalRetryMs = 2000;
		private const int PortalClicks = 5;

		// walk to the portal id (one of fresh) and click it once it is open (targetable, its label showing: a click while it
		// is still opening, 0.3 s after Activate, did nothing); click again if no loading starts within PortalRetryMs, up to
		// PortalClicks clicks; back to pick if it is gone
		private IState ApproachPortal(string what, uint id, Func<IEnumerable<Entity>> fresh, Action onClick, IState pick, IState next) {
			int clicks = 0;
			long readyWait = 0;
			return new Step($"Go to {what}", (s, ctx, dt) => {
				var portal = fresh().FirstOrDefault(e => e.Id == id);
				var pos = portal?.GetComponent<Positioned>();
				if ( portal == null || !IsValid(pos) ) {
					Log($"{Name}: {what} ({id}) is gone: looking for another");
					return pick;
				}
				var grid = pos.GridPos;
				// (a short leg at a time, so a portal that goes is noticed on the way)
				if ( !Navigate.IsNear(ctx, grid, ClickReach, out _, out float d) ) {
					s.Detail = $"{d:F0} cells away";
					return new Navigate(what, _ => grid, ClickReach, s) { FightRadius = -1, LegMs = 500 };
				}
				ctx.Driver.StopWalking($"taking {what}");
				bool open = IsTargetable(portal) && GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => l.Item?.Address == portal.Address) == true;
				if ( !open && (readyWait += dt) < PortalReadyMs ) { s.Detail = "waiting for it to open"; return s; }
				if ( clicks >= PortalClicks ) return Fail($"{what} ({id}): no loading after {clicks} clicks");
				uint from = AreaHash();
				// (back here to click again if the loading has not started PortalRetryMs after this click)
				var self = s;
				var wait = new Step("Wait for the loading to start", (w, c2, d2) => {
					bool started = (PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? false) || (AreaHash() != from && AreaHash() != 0);
					if ( started ) return WaitForNewArea(from, next);
					return w.Elapsed > PortalRetryMs ? self : w;
				}, next);
				var click = Ground.ClickEntity(portal, wait);
				if ( click == null ) return s.Elapsed > 5000 ? Fail($"can't click {what} (off screen, or under a panel)") : s;
				clicks++;
				onClick?.Invoke();
				Log($"{Name}: clicking {what} ({id}){(clicks > 1 ? $" again (click {clicks} of {PortalClicks}: no loading yet)" : open ? "" : $" (not open after {PortalReadyMs} ms)")}");
				return click;
			}, next);
		}

		private static Entity NearestOf(IEnumerable<Entity> entities) {
			var player = GetPlayer()?.GetComponent<Positioned>();
			if ( !IsValid(player) ) return null;
			var at = player.GridPos;
			return entities.Where(IsValid).Select(e => (e, pos: e.GetComponent<Positioned>())).Where(x => IsValid(x.pos))
				.OrderBy(x => Combat.Distance(at, x.pos.GridPos)).Select(x => x.e).FirstOrDefault();
		}

		// ---- in the map ----

		/// <summary>How many times ClearArea clears and fights the boss again before it gives up on the objectives.</summary>
		private const int ClearRounds = 3;
		/// <summary>How long ClearArea waits after a round for the map panel to show the objectives done.</summary>
		private const long ObjectivesWaitMs = 3000;
		/// <summary>
		/// The reveal radius of the closer pass from the second round on (the first pass uses Explorer.RevealRadius): a
		/// buried map boss (eg, Mud Geyser's Tunneltrap) only comes out when the player is near, and the first pass can
		/// count its spot as explored from far off.
		/// </summary>
		public int SweepRevealRadius = 45;

		/// <summary>
		/// In a map (name: its area's name; null: the area the player is in when this is reached): if MapRules has rules
		/// for it, walk its route (tiles to go to, doors to go through, in order) and to each boss arena (fighting the boss
		/// there); then, rules or not, explore and kill (ClearMap, then KillMapBoss) until fewer than MonstersLeft monsters
		/// remain and the map panel says "Map Complete"; then next. Up to ClearRounds rounds, then it stops (null) with what
		/// is still missing. Ends SkipToClear.
		/// </summary>
		public IState ClearArea(string name, IState next) => new Step($"Clear {name ?? "the area"}", (s, ctx, dt) => {
			SkipToClear = false;
			if ( !InMap() ) return Fail($"clear {name ?? "the area"}: not in a map");
			Clearing = true;
			string area = name ?? GetCurrentArea()?.Name ?? "the area";
			var after = ClearRound(area, 1, s.Pass());
			if ( !Rules.TryGetValue(area, out var rules) ) {
				Log($"{Name}: clearing {area} (no rules for it: explore and kill)");
				return after;
			}
			Log($"{Name}: clearing {area} by its rules: {rules}");
			return rules.Plan(this, after);
		}, next);

		// clear (ClearMap), fight the boss (KillMapBoss), then check the objectives: done, next; else another round
		private IState ClearRound(string area, int round, IState next) {
			var check = new Step($"Check {area}'s objectives", (s, ctx, dt) => {
				if ( ObjectivesDone() ) {
					// (not done while monsters are near: fought first, then looked at again)
					if ( Fight.AnyNear(ctx, 0) ) { s.Detail = "fighting the monsters near"; return new Fight(0, null, s); }
					MapsCompleted += 1;
					Log($"{Name}: {area} is done: {MonstersRemaining} monsters remain, Map Complete (round {round}; {MapsCompleted} maps completed)");
					return s.Pass();
				}
				if ( s.Elapsed < ObjectivesWaitMs ) { s.Detail = "waiting for the map panel"; return s; }
				string missing = $"{(MonstersRemaining is int m ? $"{m}" : "more than 50")} monsters remain (fewer than {MonstersLeft} wanted), Map Complete: {MapComplete}";
				if ( round >= ClearRounds ) return Fail($"clear {area}: still not done after {round} rounds: {missing}");
				Log($"{Name}: {area}: {missing}: clearing again (round {round + 1} of {ClearRounds})");
				return ClearRound(area, round + 1, s.Pass());
			}, next);
			var boss = new KillMapBoss(this) { Next = check };
			var clear = new ClearMap(this) { Next = boss, Sweep = round >= 2 };
			if ( round != 2 ) return clear;
			// (the second round: explore it all again, more closely, whatever the monsters remaining)
			return new Step($"Explore {area} again, more closely", (s, ctx, dt) => {
				Log($"{Name}: {area}: exploring it again, counting only {SweepRevealRadius} cells around the player as explored (the first pass: {DefaultExplorer.RevealRadius})");
				DefaultExplorer.Rescan(SweepRevealRadius);
				ForgetExploreTarget();
				return s.Pass();
			}, clear);
		}

		/// <summary>The map's objectives: fewer than MonstersLeft monsters remain (none, for 0), and the panel says "Map Complete".</summary>
		public bool ObjectivesDone() => MapComplete && MonstersRemaining is int m && (m < MonstersLeft || m == 0);

		/// <summary>How long ReturnHome waits after "Map Complete" (or a unique's death) for the drops to be picked up.</summary>
		public long LootWaitMs = 2000;

		/// <summary>
		/// Back to the hideout: wait LootWaitMs after the boss died, close the inventory, press the Portal Scroll key, enter
		/// the portal it opens (EnterFreshPortal), WaitForNewArea, then next. Stops (null) if the key is not bound, the chat
		/// says "Out of Portal Scrolls", or no portal opens.
		/// </summary>
		public IState ReturnHome(IState next) => new Step("Return to the hideout", (s, ctx, dt) => {
			Clearing = false;
			if ( InHideout() ) return s.Pass();
			long now = Time.ElapsedMilliseconds;
			if ( now - Math.Max(LastUniqueKillAt, MapCompleteAt) < LootWaitMs ) { s.Detail = "waiting for the drops"; ctx.Driver.StopWalking("waiting for the drops"); return s; }
			if ( !GameKeys.Loaded && !GameKeys.Unreadable && !GameKeys.TryRead() ) { s.Detail = "reading the game's keys"; return s; }
			var key = UsePortalScroll.ScrollKey;
			if ( key == VKey.None ) return Fail("return to the hideout: no key for Use Portal Scroll in the game's Options");
			// (monsters near, eg come up during the wait for the drops: fought before the Portal Scroll)
			if ( Fight.AnyNear(ctx, 0) ) { s.Detail = "fighting the monsters near"; return new Fight(0, null, s); }
			ctx.Driver.StopWalking("using a Portal Scroll");
			if ( BackpackIsOpen() ) return GameKeys.Press(GameKeys.InventoryKey, new Delay(InventoryPlans.Settle, s));
			// (any portal in the map leads home, the map's own or a Portal Scroll's, however far: through the nearest one
			// open, and no Portal Scroll spent while there is one)
			var open = TownPortals().Where(e => e.GetComponent<Targetable>()?.IsTargetable ?? true).ToList();
			if ( open.Count > 0 ) {
				Log($"{Name}: {open.Count} portal{(open.Count == 1 ? "" : "s")} in the map: going home through the nearest, no Portal Scroll");
				return EnterFreshPortal("a portal home", TownPortals, null, s.Pass());
			}
			var mark = ChatCommand.LastMessage();
			Func<IEnumerable<Entity>> fresh = TownPortals;
			scrollPresses += 1;
			Log($"{Name}: pressing {GameKeys.Text("Use Portal Scroll")} (a Portal Scroll){(scrollPresses > 1 ? $", again (press {scrollPresses} of {ScrollPresses})" : "")}");
			var enter = EnterFreshPortal("the Portal Scroll's portal", fresh, null, s.Pass());
			return GameKeys.Press(key, new Step("Wait for the Portal Scroll's portal", (w, c2, d2) => {
				if ( fresh().Any() ) { scrollPresses = 0; return enter; }
				if ( mark != IntPtr.Zero && ChatCommand.MessagesAfter(mark).Any(m => m.Contains("Out of Portal Scrolls", StringComparison.OrdinalIgnoreCase)) )
					return Fail("return to the hideout: out of Portal Scrolls");
				if ( w.Elapsed <= 3000 ) return w;
				// (a press can be lost, eg to a cast going out at the same time: pressed again, then given up)
				if ( scrollPresses < ScrollPresses ) return s;
				scrollPresses = 0;
				return Fail($"return to the hideout: no portal opened after {ScrollPresses} Portal Scrolls");
			}, enter));
		}, next);

		/// <summary>Presses of the Portal Scroll key (each waited on 3 s) before ReturnHome gives up.</summary>
		private const int ScrollPresses = 2;
		private int scrollPresses;

		// the portals in this area that lead home (in a map: its own, MultiplexPortal, and any Portal Scroll's), however far:
		// none is ever "old" (a 40 cell limit once took the map's portal, just coming in range, for a new Portal Scroll's, and
		// then, carried off by a fight, took it for gone)
		private static IEnumerable<Entity> TownPortals() =>
			(GetEntities() ?? Enumerable.Empty<Entity>())
				.Where(e => IsValid(e) && e.Path is string path && path.StartsWith("Metadata/MiscellaneousObjects/", StringComparison.Ordinal) && path.Contains("Portal")
					&& e.GetComponent<Positioned>() is Positioned pos && IsValid(pos))
				.ToList();
	}

	/// <summary>
	/// The goal the Mapping routine runs in the hideout and in maps: it ticks the loop's chain (MappingRoutine.MapLap)
	/// itself, as the CampaignRunner would, and keeps it when the Runner's chain is cancelled (a new area, dying, an
	/// arena lockdown): the Runner only ticks this goal, so the loop goes on where it was. Never done. When the chain
	/// stops (a step returned null, with a log line), the routine is switched off.
	/// </summary>
	public class MapLoop : Goal {
		private readonly MappingRoutine routine;
		/// <summary>The state of the loop's chain being ticked (null before the first lap is built).</summary>
		public IState Current { get; private set; }

		public MapLoop(MappingRoutine routine) => this.routine = routine;
		public override string Name => "Run maps";
		public override bool Sticky => false;
		public override bool CheckDone(CombatContext ctx) => false;
		// (in the hideout, the steps work in the stash and the inventory; and input going out is not to be cut in on)
		public override bool UsesInventory => !routine.InMap() || !(Current is CampaignState);
		public override void OnCancel() => Context?.Driver.StopWalking("cancelled");

		/// <summary>Start again from a new lap (eg, the routine switched on again).</summary>
		public void Restart() => Current = null;

		/// <summary>The goal the chain is in (eg, ClearMap, while it explores), for what the routine does around it; null if none.</summary>
		public Goal ActiveGoal {
			get {
				var s = Current;
				for ( int i = 0; s != null && i < 20; i++, s = s.Next ) {
					if ( s is Goal g ) return g;
				}
				return null;
			}
		}

		protected override IState Step(CombatContext ctx) => this; // (see Tick)

		protected override IState Tick(CombatContext ctx, long dt) {
			// (back in the hideout in the middle of clearing a map, eg died and resurrected there: back in through its portals)
			if ( Current != null && routine.Clearing && routine.InHideout() ) {
				routine.Clearing = false;
				routine.ReEnter = true;
				Log($"{routine.Name}: back in the hideout while clearing {routine.CurrentMap ?? "a map"}: starting a new lap, through its portals if any are left");
				Current = null;
			}
			// (a map entered by mistake, eg a portal beside the Map Device clicked instead of it: out again, or clear it)
			if ( Current != null && routine.Strayed ) {
				routine.Strayed = false;
				Log($"{routine.Name}: entered {routine.CurrentMap ?? "a map"} by mistake (during {Current})");
				Current = routine.LeaveStrayMap().OnEnter();
			}
			if ( Current == null ) {
				routine.StartLoop();
				Current = routine.MapLap()?.OnEnter();
			}
			var next = Current?.OnTick(dt);
			if ( next != Current ) next = next?.OnEnter();
			Current = next;
			if ( Current == null ) {
				Detail = "stopped";
				ctx.Driver.Stop("the map loop stopped (see the log)");
				return this;
			}
			Detail = Current.ToString();
			return this;
		}
	}
}
