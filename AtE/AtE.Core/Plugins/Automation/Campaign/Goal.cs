using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A State the Campaign runs: ticked with the frame's CombatContext (the player, terrain, hostiles, and the driver to
	/// walk / fight / click with). Return this to continue, Next when done, or another state to run first (with its
	/// Next pointing back here, see Call).
	///
	/// These are not run by the default Machine (which clears a state's Next when it moves on, and so would break the
	/// chains that loop back): the Campaign ticks the chain itself (see CampaignRunner).
	/// </summary>
	public abstract class CampaignState : State {
		/// <summary>This frame's context, set by the CampaignRunner before it ticks (null outside of a tick).</summary>
		public static CombatContext Context { get; internal set; }

		/// <summary>What it is doing right now (eg, "12 cells away"), for the on-screen stack and the debug view.</summary>
		public string Detail = "";

		protected CampaignState(IState next = null) : base(next) { }

		public override IState OnTick(long dt) => Context == null ? this : Tick(Context, dt);

		/// <summary>One frame of work.</summary>
		protected abstract IState Tick(CombatContext ctx, long dt);

		/// <summary>Run sub first, then come back here: sub's chain ends with this (eg, return Call(new Fight(...))).</summary>
		protected IState Call(IState sub) {
			if ( sub == null ) return this;
			// (a goal is one state, and may have been called before: its old Next is not followed)
			if ( sub is Goal ) sub.Next = this;
			else sub.Tail().Next = this;
			return sub;
		}

		public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : $"{Name}: {Detail}";
	}

	/// <summary>
	/// A node of the campaign: something to get done (eg, "Kill Hillock", "Open the 3 Rhoa Nests"), that can say whether
	/// it is done, read from the game where it can (so a campaign started again picks up where it left off).
	///
	/// While not done, the campaign runs it as a state: each frame its Step does one thing, or returns a sub-state to run
	/// first (Navigate, Fight, Explore, TravelTo, ...; see Call), which hands back to it when finished. A Goal is a leaf,
	/// or a Sequence of goals (which nest).
	///
	/// Nothing is keyed to the area the player is in: a goal that has to be done somewhere says so (Where), and if the
	/// player is elsewhere it travels there first. Once done a goal stays done (the campaign saves which are, per
	/// character), however often its area is visited again; only "Enter X" (EnterArea) is done by being somewhere.
	/// </summary>
	public abstract class Goal : CampaignState {
		public bool IsComplete { get; internal set; }
		/// <summary>Its place in the master list (eg, "Act 1/Mercy Mission/1.PickUpQuestItem"), which the saved progress is kept by.</summary>
		public string Id { get; internal set; }
		/// <summary>The area it is done in (eg, "The Tidal Island"), traveled to first if the player is elsewhere; null: wherever the player is.</summary>
		public string Where;
		/// <summary>True if, once done, it stays done (its done-ness can't be read back later); false to check it every time.</summary>
		public virtual bool Sticky => true;
		/// <summary>
		/// True if this being done (read from the game) shows the goals before it are done (eg, the support socketed: the
		/// chest was opened). Only for what stays true: not being in an area, or a window being shut.
		/// </summary>
		public virtual bool ProvesEarlier => false;
		/// <summary>Set to say whether this one proves the goals before it done, in place of its type's ProvesEarlier.</summary>
		public bool? Proves;
		/// <summary>True if a later goal being done shows this one was done (see ProvesEarlier); false if this must be checked itself.</summary>
		public virtual bool ProvenBy(IEnumerable<Goal> later) => true;
		/// <summary>True if it no longer matters, the campaign having got past it to later goals (eg, Default Attack on a slot, once Fireball is on it).</summary>
		public virtual bool SupersededBy(IEnumerable<Goal> later) => false;
		/// <summary>True if it works in the inventory panel; while on any other goal the campaign closes it.</summary>
		public virtual bool UsesInventory => false;
		/// <summary>Also done when this is true (eg, any weapon is equipped, for picking up the starting weapon).</summary>
		public Func<bool> DoneWhen;
		/// <summary>Walk past the monsters: no fighting, and no picking up items (which fights the monsters near them) while on this.</summary>
		public bool IgnoreMonsters;
		/// <summary>
		/// Where to look for what it is after (eg, Search.TheOnlyExit, for a boss in the way out): walk there, past the
		/// monsters. Null (or a null answer): explore the area, fighting on the way.
		/// </summary>
		public Func<CombatContext, Offsets.Vector2i?> SearchAt;

		/// <summary>
		/// Not done while this is true, whatever CheckDone says or was saved (eg, "Kill Merveil" while the quest tracker
		/// still says so: a goal marked done by mistake is taken up again).
		/// </summary>
		public Func<bool> NotDoneWhile;

		/// <summary>CheckDone, or DoneWhen; never while NotDoneWhile.</summary>
		public bool IsDone(CombatContext ctx) => !(NotDoneWhile?.Invoke() ?? false) && (CheckDone(ctx) || (DoneWhen?.Invoke() ?? false));
		/// <summary>Is it done? Asked several times a second until it says true.</summary>
		public abstract bool CheckDone(CombatContext ctx);
		/// <summary>One frame of work on it (it is not done): this to continue, or Call(a sub-state).</summary>
		protected abstract IState Step(CombatContext ctx);

		protected override IState Tick(CombatContext ctx, long dt) {
			if ( IsDone(ctx) ) {
				Detail = "";
				return Next;
			}
			if ( Where != null && !GoToArea.Is(GetCurrentArea(), Where) ) {
				Detail = $"going to {Where}";
				return Call(new TravelTo(Where) { IgnoreMonsters = IgnoreMonsters });
			}
			return Step(ctx);
		}

		/// <summary>Forget what it was doing (a new area instance, or starting over after dying); not whether it is complete.</summary>
		public virtual void Reset() => Detail = "";

		/// <summary>The goals inside it (a Sequence's), for the on-screen list and the debug view; none for a leaf.</summary>
		public virtual IEnumerable<Goal> Children => Enumerable.Empty<Goal>();

		// ---- helpers for Step ----

		/// <summary>Where to walk to reach pos: the nearest cell to it that can be walked to from the player.</summary>
		protected static Offsets.Vector2i Approach(CombatContext ctx, Offsets.Vector2i pos) =>
			DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, pos, out var spot) ? spot : pos;

		/// <summary>
		/// Is the player near enough to pos to click it: within reach cells, or at (within 3 cells of) the nearest cell
		/// that can be walked to (eg, a wall set into a cliff). approach is where to walk otherwise, and distance how far pos is.
		/// </summary>
		protected static bool IsNear(CombatContext ctx, Offsets.Vector2i pos, float reach, out Offsets.Vector2i approach, out float distance) =>
			Navigate.IsNear(ctx, pos, reach, out approach, out distance);

		/// <summary>Walk to within reach of pos, fighting on the way (unless IgnoreMonsters): a Navigate that comes back here.</summary>
		protected IState GoTo(string label, Offsets.Vector2i pos, float reach, float fightRadius = 0) =>
			Call(new Navigate(label, _ => pos, reach) { FightRadius = IgnoreMonsters ? -1 : fightRadius });

		/// <summary>Look for what, not in sight yet: walk toward SearchAt (past the monsters), or explore a while (fighting on the way).</summary>
		protected IState Search(CombatContext ctx, string what, string skill = null) {
			var at = SearchAt?.Invoke(ctx);
			if ( at.HasValue ) {
				Detail = $"looking for {what} on the way to {at.Value}";
				return Call(new Navigate($"look for {what}", _ => at, 3) { FightRadius = -1, LegMs = 1000 });
			}
			Detail = $"looking for {what}";
			return Call(new Explore($"look for {what}") { FightSkill = skill, FightRadius = IgnoreMonsters ? -1 : 0 });
		}

		/// <summary>Fight the monsters near (within radius; 0 for the driver's EngageRadius) first, if there are any and not IgnoreMonsters; else null.</summary>
		protected IState FightFirst(CombatContext ctx, float radius = 0, string skill = null) {
			if ( IgnoreMonsters || !Fight.AnyNear(ctx, radius) ) return null;
			return Call(new Fight(radius, skill));
		}
	}

	/// <summary>
	/// Goals done in order: works on the first that is not done (see Select). Its own DoneWhen (eg, "not in The Twilight
	/// Strand" for "Exit The Twilight Strand") skips all of it. Sequences nest (eg, Part 1, Act 1, a quest, its steps).
	/// </summary>
	public class Sequence : Goal {
		private readonly string name;
		public readonly List<Goal> Goals;
		/// <summary>How many goals past the first incomplete one to read from the game, for one that proves the earlier ones done.</summary>
		public int Lookahead = 8;

		public Sequence(string name, params Goal[] goals) { this.name = name; Goals = goals.Where(g => g != null).ToList(); }
		public override string Name => name;
		public override bool Sticky => false;
		public override IEnumerable<Goal> Children => Goals;
		/// <summary>The goal being worked on (after the last Select), or null if all are complete.</summary>
		public Goal Current { get; private set; }

		public override void Reset() {
			base.Reset();
			foreach ( var g in Goals ) g.Reset();
		}

		/// <summary>Give each goal in it an Id from its place (prefix/index.Type, or the name for a Sequence).</summary>
		public void AssignIds(string prefix = null) {
			Id = prefix == null ? Name : $"{prefix}/{Name}";
			for ( int i = 0; i < Goals.Count; i++ ) {
				if ( Goals[i] is Sequence seq ) seq.AssignIds(Id);
				else Goals[i].Id = $"{Id}/{i}.{Goals[i].GetType().Name}";
			}
		}

		/// <summary>Every goal under it (depth first, Sequences too).</summary>
		public IEnumerable<Goal> All() {
			foreach ( var g in Goals ) {
				yield return g;
				if ( g is Sequence seq ) foreach ( var x in seq.All() ) yield return x;
			}
		}

		public override bool CheckDone(CombatContext ctx) => Select(ctx) == null;

		/// <summary>
		/// Choose the leaf goal to work on, or null if it is done. First its own check (DoneWhen, eg "Complete Act 1": a
		/// later act is available): if that says done, none of its goals are looked at. Else its goals in order: a goal
		/// done (read from the game) is marked complete (and if it ProvesEarlier, so are the ones before it, looking at
		/// most Lookahead goals past the first not complete); a complete goal that is not Sticky is asked again. The first
		/// not done becomes Current (and, for a Sequence, chooses inside it).
		/// </summary>
		public Goal Select(CombatContext ctx) {
			Current = null;
			if ( DoneWhen?.Invoke() ?? false ) { MarkDone(this); return null; }
			int first = Goals.FindIndex(g => !g.IsComplete);
			if ( first >= 0 ) {
				// a goal a little further on that is done shows the ones before it are
				int last = Math.Min(Goals.Count - 1, first + Lookahead);
				for ( int i = last; i > first; i-- ) {
					var g = Goals[i];
					if ( g.IsComplete || !(g.Proves ?? g.ProvesEarlier) || !g.IsDone(ctx) ) continue;
					for ( int k = first; k < i; k++ ) {
						if ( Goals[k].ProvenBy(Goals.Skip(k + 1).Take(i - k)) ) MarkDone(Goals[k]);
					}
					break;
				}
			}
			for ( int i = 0; i < Goals.Count; i++ ) {
				var g = Goals[i];
				if ( g.IsComplete && g.Sticky && !(g.NotDoneWhile?.Invoke() ?? false) ) continue;
				if ( g.SupersededBy(Goals.Skip(i + 1)) ) { MarkDone(g); continue; }
				if ( g is Sequence inner ) {
					var leaf = inner.Select(ctx);
					if ( leaf == null ) continue; // (marked done by its Select)
					Current = g;
					return leaf;
				}
				if ( g.IsDone(ctx) ) { MarkDone(g); continue; }
				if ( g.IsComplete ) { g.IsComplete = false; Log($"Campaign: not done any more: {g.Name}"); }
				Current = g;
				return g;
			}
			MarkDone(this);
			return null;
		}

		/// <summary>Mark a goal complete (and, for a Sequence, all in it: eg, an act found complete, without doing its goals).</summary>
		public static void MarkDone(Goal g) {
			if ( g.IsComplete ) return;
			g.IsComplete = true;
			g.Detail = "";
			if ( g is Sequence seq ) foreach ( var x in seq.Goals ) MarkDone(x);
			Log($"Campaign: done: {g.Name}");
		}

		/// <summary>Mark it and all in it not complete (a new run).</summary>
		public void Forget() {
			IsComplete = false;
			foreach ( var g in All() ) g.IsComplete = false;
		}

		// (the runner runs its leaves, not it)
		protected override IState Step(CombatContext ctx) {
			var leaf = Select(ctx);
			if ( leaf == null ) return Next;
			leaf.Next = this;
			return leaf;
		}
	}

	/// <summary>
	/// Be in an area: done once the player is in it (the one goal that is done by being somewhere). Travels there (see TravelTo).
	/// </summary>
	public class EnterArea : Goal {
		public readonly string Area;
		public EnterArea(string area) => Area = area;
		public override string Name => $"Enter {Area}";
		public override bool CheckDone(CombatContext ctx) => GoToArea.Is(GetCurrentArea(), Area);
		protected override IState Step(CombatContext ctx) => Call(new TravelTo(Area) { IgnoreMonsters = IgnoreMonsters });
	}

	/// <summary>
	/// Get to an area, by the shorter way: walking the route of area exits, or taking a waypoint to the area with a
	/// waypoint nearest it (when this area has one, and that saves walking through at least one area), then walking on.
	/// A waypoint that is not on the panel (not discovered) is not tried again; it walks instead.
	/// </summary>
	public class TravelTo : CampaignState {
		public readonly string Area;
		public bool IgnoreMonsters;
		private static readonly HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // waypoints found not discovered
		private UseWaypoint waypoint;

		public TravelTo(string area, IState next = null) : base(next) => Area = area;
		public override string Name => $"Travel to {Area}";
		public override void OnCancel() => Context?.Driver.StopWalking("cancelled");

		protected override IState Tick(CombatContext ctx, long dt) {
			var here = GetCurrentArea();
			if ( GoToArea.Is(here, Area) ) return Next;
			if ( waypoint != null && waypoint.Missing ) {
				missing.Add(waypoint.Destination);
				Log($"Campaign: the waypoint to {waypoint.Destination} is not discovered: walking");
				waypoint = null;
			}
			var via = WaypointVia(here);
			if ( via != null && NearerTown(ctx, here) is AreaExit toTown ) {
				// (the town next door is nearer than this area's waypoint: its waypoint, then)
				Detail = $"by way of {toTown.Destination}'s waypoint to {via}";
				return Call(new GoToArea(toTown.Destination) { IgnoreMonsters = IgnoreMonsters });
			}
			if ( via != null ) {
				Detail = $"by waypoint to {via.Name}";
				waypoint = new UseWaypoint(via.Name) { IgnoreMonsters = IgnoreMonsters, GiveUpIfMissing = true, Act = via.Act == here.Act ? 0 : via.Act };
				return Call(waypoint);
			}
			Detail = "walking";
			return Call(new GoToArea(Area) { IgnoreMonsters = IgnoreMonsters });
		}

		// the exit to this act's town next door, if it is known and nearer than this area's waypoint (eg, in The Riverways,
		// the camp's exit beside it, rather than the waypoint 1000 cells away): else null
		private static AreaExit NearerTown(CombatContext ctx, WorldArea here) {
			if ( here.IsTown || ctx.Terrain == null ) return null;
			var town = here.Connections.FirstOrDefault(a => a.IsTown && a.Act == here.Act);
			if ( town == null ) return null;
			var exit = ctx.Terrain.Exits.Where(e => e.Destination == town.Name && DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, e.GridPos))
				.OrderBy(e => Combat.Distance(ctx.Grid, e.GridPos)).FirstOrDefault();
			if ( exit == null ) return null;
			var wp = ctx.Terrain.Waypoint;
			return wp == null || Combat.Distance(ctx.Grid, exit.GridPos) < Combat.Distance(ctx.Grid, wp.GridPos) ? exit : null;
		}

		// the area with a waypoint nearest to Area (by exits), if taking it saves walking: else null. In another act too
		// (UseWaypoint clicks its tab on the panel; eg, The Way Forward's "Travel to Lioneye's Watch" from The Western
		// Forest), if that act is reached (CampaignProgress.HighestAct, or before this one).
		private WorldArea WaypointVia(WorldArea here) {
			if ( here == null || !(here.HasWaypoint || here.IsTown) ) return null;
			var walk = WorldArea.FindRoute(here, Area);
			if ( walk == null || walk.Count < 3 ) return null; // (next door: walk)
			// breadth first out from the destination, to the nearest area with a waypoint, nearer than here
			var target = walk[walk.Count - 1];
			var seen = new HashSet<WorldArea> { target };
			var frontier = new List<WorldArea> { target };
			for ( int hops = 0; hops < walk.Count - 2 && frontier.Count > 0; hops++ ) {
				var found = frontier.FirstOrDefault(a => (a.HasWaypoint || a.IsTown) && !missing.Contains(a.Name)
					&& (a.Act == here.Act || (a.Act > 0 && a.Act <= Math.Max(here.Act, CampaignProgress.HighestAct)))
					&& (a == here || !WaypointPanel.IsKnownLocked(a.Name, a.Act)));
				if ( found != null ) return found == here ? null : found;
				frontier = frontier.SelectMany(a => a.Connections).Where(a => !a.IsHub && seen.Add(a)).ToList();
			}
			return null;
		}
	}

	/// <summary>Do Body over and over (Reset between), until Until is true (eg, "open the next nest" until all are open).</summary>
	public class Repeat : Goal {
		public readonly Goal Body;
		public readonly Func<CombatContext, bool> Until;
		private readonly string name;
		public int Times { get; private set; }
		public Repeat(string name, Goal body, Func<CombatContext, bool> until) { this.name = name; Body = body; Until = until; }
		public override string Name => Times == 0 ? name : $"{name} ({Times} done)";
		public override bool Sticky => false;
		public override IEnumerable<Goal> Children => new[] { Body };
		public override void Reset() { base.Reset(); Times = 0; Body.Reset(); }
		public override bool CheckDone(CombatContext ctx) => Until(ctx);
		protected override IState Step(CombatContext ctx) {
			if ( Body.IsDone(ctx) ) {
				Times += 1;
				Body.Reset();
				return this;
			}
			Body.Next = this;
			return Body;
		}
	}

	/// <summary>A goal made of two functions, for one-off steps.</summary>
	public class OneOff : Goal {
		private readonly string name;
		private readonly Func<CombatContext, bool> isDone;
		private readonly Func<OneOff, CombatContext, IState> step;
		public OneOff(string name, Func<CombatContext, bool> isDone, Func<OneOff, CombatContext, IState> step) {
			this.name = name; this.isDone = isDone; this.step = step;
		}
		public override string Name => name;
		public override bool Sticky => false;
		public override bool CheckDone(CombatContext ctx) => isDone(ctx);
		protected override IState Step(CombatContext ctx) => step(this, ctx) ?? this;
	}

	/// <summary>Places to look, for Goal.SearchAt.</summary>
	public static class Search {
		/// <summary>The way out, when the area has only one (eg, The Twilight Strand, with Hillock in front of it); else null.</summary>
		public static Offsets.Vector2i? TheOnlyExit(CombatContext ctx) {
			var exits = ctx.Terrain.Exits;
			return exits.Count == 1 ? exits[0].GridPos : (Offsets.Vector2i?)null;
		}

		/// <summary>
		/// The nearest group of tiles cut from a .tdt whose path has tilePathPart (eg, "/cavedown_boss_exit", the way into
		/// Merveil's arena in The Cavern of Anger); null if the area has none.
		/// </summary>
		public static Func<CombatContext, Offsets.Vector2i?> Tile(string tilePathPart) => ctx =>
			ctx.Terrain.TileFeatures(tilePathPart).OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).FirstOrDefault()?.GridPos;

		/// <summary>
		/// Tile groups to visit in turn (eg, a bandit leader's camp, where they show by name once near): the nearest not
		/// visited yet of the first tilePathParts that has any, each counted visited once within reach cells; null once all
		/// are visited (Goal.Search then explores). Each call makes its own record of the visited ones.
		/// </summary>
		public static Func<CombatContext, Offsets.Vector2i?> Tiles(float reach, params string[] tilePathParts) {
			var visited = new HashSet<AreaFeature>(); // TileFeatures' groups are new objects in each area
			return ctx => {
				foreach ( var part in tilePathParts ) {
					var left = ctx.Terrain.TileFeatures(part).Where(f => !visited.Contains(f)).ToList();
					foreach ( var f in left.Where(f => Combat.Distance(ctx.Grid, f.GridPos) <= reach).ToList() ) {
						visited.Add(f);
						left.Remove(f);
					}
					if ( left.Count > 0 ) return left.OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).First().GridPos;
				}
				return null;
			};
		}

		/// <summary>
		/// Where a bandit leader is: their camp's tiles, then any other BanditCamp tile. Seen live (3.29.4): Alira in The
		/// Western Forest at <1250, 553>, by BanditCamp/treewitch_camp_v01_* (<1241, 517>); Oak in The Wetlands at
		/// <652, 902>, among the three Features/Tents/forest_tent_* (<588, 906>, <659, 972>, <701, 885>; the area has no
		/// BanditCamp tiles). Kraityn's (The Broken Bridge) are not known yet: BanditCamp is a guess there.
		/// </summary>
		public static Func<CombatContext, Offsets.Vector2i?> BanditCamp() => Tiles(20, "/BanditCamp/treewitch_camp", "/Features/Tents/forest_tent", "/BanditCamp/");

		/// <summary>
		/// The exit named destination (eg, a door within the area, named by AreaExits.PlacedExitTiles, or seen); else the
		/// nearest exit not seen yet whose tile has tilePathPart (each is named once seen, and the next is tried); else null.
		/// </summary>
		public static Func<CombatContext, Offsets.Vector2i?> Exit(string destination, string tilePathPart) => ctx => {
			var exits = ctx.Terrain.Exits;
			var named = exits.FirstOrDefault(e => string.Equals(e.Destination, destination, StringComparison.OrdinalIgnoreCase));
			if ( named != null ) return named.GridPos;
			return exits.Where(e => !e.Seen && e.Destination == null && (e.TileFile?.IndexOf(tilePathPart, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
				.OrderBy(e => Combat.Distance(ctx.Grid, e.GridPos)).FirstOrDefault()?.GridPos;
		};
	}

	// ---------------------------------------------------------------------------------------------------------------
	// sub-states: what a goal hands its work to

	/// <summary>
	/// Walk to within Reach of a place (or as near as can be walked), then hand back. On the way, a monster within
	/// FightRadius interrupts: a Fight runs first, and comes back here. LegMs (if set) hands back after that long anyway,
	/// so the goal can look again (eg, while searching).
	/// </summary>
	public class Navigate : CampaignState {
		private readonly string label;
		private readonly Func<CombatContext, Offsets.Vector2i?> target;
		public float Reach;
		/// <summary>Fight monsters within this many cells on the way; 0 for the driver's EngageRadius; negative for none.</summary>
		public float FightRadius;
		public string FightSkill;
		/// <summary>Hand back after this many ms even if not there (0: only when there).</summary>
		public long LegMs;
		private long started;

		/// <param name="label">What to call it (eg, "the waypoint").</param>
		/// <param name="target">Where to go, asked each frame (null: nowhere to go, hand back).</param>
		public Navigate(string label, Func<CombatContext, Offsets.Vector2i?> target, float reach, IState next = null) : base(next) {
			this.label = label; this.target = target; Reach = reach;
		}
		public override string Name => $"Navigate to {label}";
		public override IState OnEnter() { started = Time.ElapsedMilliseconds; return this; }
		public override void OnCancel() => Context?.Driver.StopWalking("cancelled");

		protected override IState Tick(CombatContext ctx, long dt) {
			var pos = target(ctx);
			if ( !pos.HasValue ) { Detail = "nowhere to go"; return Next; }
			if ( IsNear(ctx, pos.Value, Reach, out var approach, out float d) ) {
				ctx.Driver.StopWalking($"at {label}");
				return Next;
			}
			if ( FightRadius >= 0 && Fight.AnyNear(ctx, FightRadius) ) return Call(new Fight(FightRadius, FightSkill));
			if ( LegMs > 0 && Time.ElapsedMilliseconds - started > LegMs ) return Next;
			Detail = $"{d:F0} cells away (at {pos.Value})";
			ctx.Driver.WalkTo(approach, $"walking to {label}");
			return this;
		}

		/// <summary>Within reach cells of pos, or at (within 3 cells of) the nearest cell to it that can be walked to.</summary>
		public static bool IsNear(CombatContext ctx, Offsets.Vector2i pos, float reach, out Offsets.Vector2i approach, out float distance) {
			approach = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, pos, out var spot) ? spot : pos;
			distance = Combat.Distance(ctx.Grid, pos);
			return distance <= reach || Combat.Distance(ctx.Grid, approach) <= 3;
		}
	}

	/// <summary>
	/// Fight the monsters within Radius (0: the driver's EngageRadius) that can be walked to, until there are none
	/// (retreating first if life is low), then hand back.
	/// </summary>
	public class Fight : CampaignState {
		public readonly float Radius;
		public readonly string Skill;
		public Fight(float radius = 0, string skill = null, IState next = null) : base(next) { Radius = radius; Skill = skill; }
		public override string Name => $"Fight{(Radius > 0 ? $" within {Radius:F0}" : "")}{(Skill == null ? "" : $" with {Skill}")}";
		public override void OnCancel() => Context?.Driver.StopWalking("cancelled");

		/// <summary>Is there a monster to fight within radius (0: EngageRadius)?</summary>
		public static bool AnyNear(CombatContext ctx, float radius) => radius <= 0
			? ctx.Hostiles.Count > 0 || ctx.MonstersToClick.Count > 0
			: radius > ctx.Driver.EngageRadius
				// (ctx.Hostiles only reaches EngageRadius: look further)
				? Combat.GetHostiles(ctx.Grid, radius).Any(h => DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, h.GridPos))
					|| ctx.MonstersToClick.Count > 0
				: ctx.Hostiles.Any(h => h.Distance <= radius) || ctx.MonstersToClick.Any(h => h.Distance <= radius);

		protected override IState Tick(CombatContext ctx, long dt) {
			if ( ctx.Driver.TryRetreat(ctx) ) { Detail = "retreating"; return this; }
			// a monster to click first (inside a monolith, a frozen augury boss): TryFight does, see CombatDriver.ClickMonster
			if ( ctx.MonstersToClick.Any(h => Radius <= 0 || h.Distance <= Radius) ) {
				ctx.Driver.TryFight(ctx, Radius > 0 ? Radius : ctx.Driver.EngageRadius, Skill);
				Detail = ctx.Driver.Status;
				return this;
			}
			// a unique: circle it (see Orbit) instead of standing and trading blows
			var unique = ctx.Hostiles.FirstOrDefault(h => h.Rarity == Offsets.MonsterRarity.Unique && (Radius <= 0 || h.Distance <= Radius));
			if ( unique != null ) return Call(OrbitUnique(unique, Skill));
			bool fighting = Radius > 0 ? ctx.Driver.TryFight(ctx, Radius, Skill) : ctx.Driver.TryFight(ctx, Skill);
			if ( !fighting ) return Next;
			Detail = ctx.Driver.Target?.ToString() ?? "";
			return this;
		}

		/// <summary>
		/// An Orbit of the unique monster target (with skill, else the build's main skill), that hands back once it is
		/// dead, or is no longer one of the hostiles near (out of reach, or it can't be damaged for now).
		/// </summary>
		public static Orbit OrbitUnique(Hostile target, string skill) {
			var ent = target.Entity;
			uint id = ent.Id;
			string label = ent.GetComponent<Render>()?.Name;
			if ( string.IsNullOrWhiteSpace(label) ) label = ent.Path?.Split('/').LastOrDefault() ?? "a unique";
			return new Orbit(label, e => e.Id == id, () => skill ?? BuildPlan.MainSkill()) {
				DoneWhen = () => Context == null || !Context.Hostiles.Any(h => h.Entity?.Id == id),
			};
		}
	}

	/// <summary>
	/// Explore for a while (LegMs), fighting on the way (unless FightRadius is negative), then hand back so the goal can
	/// look again. Hands back at once if everything is explored.
	/// </summary>
	public class Explore : CampaignState {
		private readonly string label;
		public float FightRadius;
		public string FightSkill;
		public long LegMs = 1000;
		private long started;
		public Explore(string label, IState next = null) : base(next) => this.label = label;
		public override string Name => label == null ? "Explore" : $"Explore: {label}";
		public override IState OnEnter() { started = Time.ElapsedMilliseconds; return this; }
		public override void OnCancel() => Context?.Driver.StopWalking("cancelled");

		protected override IState Tick(CombatContext ctx, long dt) {
			if ( Time.ElapsedMilliseconds - started > LegMs ) return Next;
			if ( FightRadius >= 0 && Fight.AnyNear(ctx, FightRadius) ) return Call(new Fight(FightRadius, FightSkill));
			if ( !ctx.Driver.TryExplore(ctx) ) { Detail = "explored everything"; return Next; }
			Detail = $"{DefaultExplorer.ExploredFraction:P0} explored";
			return this;
		}
	}

	/// <summary>Stand still for a while (eg, for a drop to show), then hand back.</summary>
	public class Wait : CampaignState {
		private readonly string why;
		private long remaining;
		public Wait(string why, long ms, IState next = null) : base(next) { this.why = why; remaining = ms; }
		public override string Name => $"Wait: {why}";
		protected override IState Tick(CombatContext ctx, long dt) {
			ctx.Driver.StopWalking(why);
			Detail = $"{remaining} ms";
			return (remaining -= dt) > 0 ? this : Next;
		}
	}

	/// <summary>
	/// Runs a chain of campaign states: the leaf goal the campaign chose, and whatever sub-states it calls (each
	/// pointing back to its caller by Next). Unlike the default Machine, it leaves Next alone, so chains can loop back.
	/// </summary>
	public class CampaignRunner {
		/// <summary>The state being ticked (the end of the chain: eg, a Fight called by a Navigate called by a goal).</summary>
		public IState Current { get; private set; }
		/// <summary>The leaf goal the chain was started from.</summary>
		public Goal Leaf { get; private set; }

		/// <summary>Start a new chain at leaf (cancelling the old one), unless it is the leaf running now.</summary>
		public void Run(Goal leaf) {
			if ( leaf == Leaf && Current != null ) return;
			Cancel();
			Leaf = leaf;
			if ( leaf == null ) return;
			leaf.Next = null;
			Current = leaf.OnEnter();
		}

		/// <summary>Cancel the chain (each state in it, from the end back to the leaf).</summary>
		public void Cancel() {
			foreach ( var s in Stack().ToList() ) {
				try { s.OnCancel(); } catch ( Exception e ) { Log($"Campaign: {s.Name}.OnCancel: {e.Message}"); }
			}
			Current = null;
			Leaf = null;
		}

		/// <summary>Run input (a chain of input states, see Inputs) first, then go on with the chain where it was.</summary>
		public void Push(IState input) {
			if ( input == null ) return;
			input.Tail().Next = Current;
			Current = input.OnEnter();
		}

		/// <summary>Tick the chain once; false once it has ended (the leaf handed back).</summary>
		public bool Tick(CombatContext ctx, long dt) {
			if ( Current == null ) return false;
			CampaignState.Context = ctx;
			try {
				var next = Current.OnTick(dt);
				if ( next != Current ) next = next?.OnEnter();
				Current = next;
			} finally {
				CampaignState.Context = null;
			}
			if ( Current == null ) Leaf = null;
			return Current != null;
		}

		/// <summary>The chain from the state running now back to the leaf (following Next), at most 20.</summary>
		public IEnumerable<IState> Stack() {
			var s = Current;
			for ( int i = 0; s != null && i < 20; i++, s = s.Next ) {
				yield return s;
				if ( s == Leaf ) yield break;
			}
		}
	}
}
