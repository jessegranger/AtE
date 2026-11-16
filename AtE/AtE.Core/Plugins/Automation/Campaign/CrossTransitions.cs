using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Get to where a place can be walked to, through the transitions within the area: doors that move the player to a
	/// part of the area cut off from the rest in the walkable grid (eg, The Grand Arena: a network of arenas and
	/// passageways, each colosseum_entrance into an arena, each colosseum_wall_door out of it to the next passageway, until
	/// the one into The Ring of Blades; The Harvest: from one boss arena back out and into the next). Done once Target can be
	/// walked to from the player.
	///
	/// A multi-stage path: the parts of the grid (Navigator.ComponentOf) are linked by the transitions, planned over
	/// breadth first, and the first transition on the way is taken. What a transition links is
	///  - learned, by taking it (the part it was taken from, and the part it put the player in; and the transition by
	///    where the player arrived is taken as the way back),
	///  - else assumed from its tile: a door tile touching two parts (within TileReach) links them.
	/// With no way known to Target, the nearest transition not tried yet is taken (going back through the ones tried, to a
	/// part that has one), so the network is explored.
	///
	/// The transitions are the tiles of TilePathParts (known when the area loads), and the AreaTransition entities in range
	/// (other than the exits to other areas); a transition is clicked by its label, the nearest AreaTransition label to it.
	/// </summary>
	public class CrossTransitions : Goal {
		public readonly string Label;
		/// <summary>Where to get to (eg, a tile; null: not known yet, explore).</summary>
		public readonly Func<CombatContext, Offsets.Vector2i?> Target;
		/// <summary>Parts of the .tdt paths of the tiles the transitions are on (eg, "/colosseum_entrance", "/colosseum_wall_door").</summary>
		public readonly string[] TilePathParts;
		/// <summary>How near (cells) a transition's walkable cells (and its label) are to its tile, and Target to the floor it is on.</summary>
		public int TileReach = 25;
		/// <summary>Click a transition's label from within this many cells.</summary>
		public float Reach = 12;
		public float FightRadius = 60;
		/// <summary>Clicks on a transition that leave the player where they were, before it is given up as not working from there.</summary>
		public int MaxClicks = 3;
		/// <summary>Transitions not to take, by their name (eg, The Harvest's "The Black Core", the boss's arena, while exploring).</summary>
		public string[] Avoid = Array.Empty<string>();

		/// <summary>A transition taken (or assumed): from the cell From, near the transition At, it put the player at To.</summary>
		private class Hop {
			public Offsets.Vector2i At, From, To;
			public bool Assumed; // the way back, by where the player arrived (not taken yet)
			public bool Dead; // clicked MaxClicks times from From, and nothing happened
			public override string ToString() => $"{At}: {From} -> {(Dead ? "nowhere" : To.ToString())}{(Assumed ? " (assumed)" : "")}";
		}
		private readonly List<Hop> hops = new List<Hop>();
		private uint hopsHash;

		// the click waiting to move the player
		private bool pending;
		private Offsets.Vector2i pendingAt, pendingFrom;
		private int pendingClicks;
		private long lastClick;
		private Offsets.Vector2i lastChoice;
		private bool hasChoice;

		// the parts of the grid each transition touches: cached while the grid and doors stay the same
		private int sidesGeneration = -1, sidesDoorGeneration = -1;
		private Terrain sidesTerrain;
		private readonly Dictionary<Offsets.Vector2i, List<(int comp, Offsets.Vector2i cell)>> sides = new Dictionary<Offsets.Vector2i, List<(int, Offsets.Vector2i)>>();

		public CrossTransitions(string label, Func<CombatContext, Offsets.Vector2i?> target, params string[] tilePathParts) {
			Label = label; Target = target; TilePathParts = tilePathParts;
		}
		public override string Name => $"Find the way to {Label}";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); pending = false; pendingClicks = 0; hasChoice = false; }

		private static uint AreaHash => PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
		private static int Comp(CombatContext ctx, Offsets.Vector2i cell) => DefaultNavigator.ComponentOf(ctx.Terrain, cell);

		public override bool CheckDone(CombatContext ctx) {
			var t = Target(ctx);
			return t.HasValue && DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, t.Value, out _, TileReach);
		}

		/// <summary>The parts of the grid with walkable cells within TileReach of p, each with its cell nearest p.</summary>
		private List<(int comp, Offsets.Vector2i cell)> Sides(CombatContext ctx, Offsets.Vector2i p) {
			var terrain = ctx.Terrain;
			if ( sidesTerrain != terrain || sidesGeneration != terrain.Generation || sidesDoorGeneration != terrain.DoorGeneration ) {
				sides.Clear();
				sidesTerrain = terrain;
				sidesGeneration = terrain.Generation;
				sidesDoorGeneration = terrain.DoorGeneration;
			}
			if ( sides.TryGetValue(p, out var found) ) return found;
			var best = new Dictionary<int, (Offsets.Vector2i cell, int d)>();
			for ( int dy = -TileReach; dy <= TileReach; dy += 2 ) {
				for ( int dx = -TileReach; dx <= TileReach; dx += 2 ) {
					int d = (dx * dx) + (dy * dy);
					if ( d > TileReach * TileReach ) continue;
					var cell = new Offsets.Vector2i(p.X + dx, p.Y + dy);
					if ( !terrain.InBounds(cell.X, cell.Y) || !terrain.IsWalkable(cell) ) continue;
					int comp = Comp(ctx, cell);
					if ( comp < 0 ) continue;
					if ( !best.TryGetValue(comp, out var b) || d < b.d ) best[comp] = (cell, d);
				}
			}
			found = best.Select(kv => (kv.Key, kv.Value.cell)).ToList();
			sides[p] = found;
			return found;
		}

		/// <summary>
		/// Is the AreaTransition entity not one to take: an exit to another area (its name is one of the area's neighbors, not
		/// this area), or one named in Avoid?
		/// </summary>
		private bool LeadsElsewhere(Entity e) {
			var here = GetCurrentArea();
			string name = e.GetComponent<Render>()?.Name?.Trim();
			if ( string.IsNullOrEmpty(name) ) return false;
			if ( Avoid.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ) return true;
			if ( here == null || GoToArea.Is(here, name) ) return false;
			return here.Connections.Any(a => GoToArea.Is(a, name));
		}

		/// <summary>The transitions within the area in range: (entity, where it is).</summary>
		private IEnumerable<(Entity ent, Offsets.Vector2i pos)> TransitionEntities() {
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				if ( !IsValid(e) || !IsValid(e.GetComponent<AreaTransition>()) || LeadsElsewhere(e) ) continue;
				var pos = e.GetComponent<Positioned>();
				if ( IsValid(pos) ) yield return (e, pos.GridPos);
			}
		}

		/// <summary>The transitions known: the tiles', and the entities' in range not on one of those tiles.</summary>
		private List<Offsets.Vector2i> Points(CombatContext ctx) {
			var points = TilePathParts.SelectMany(part => ctx.Terrain.TileFeatures(part)).Select(f => f.GridPos).ToList();
			foreach ( var (_, pos) in TransitionEntities() ) {
				if ( !points.Any(p => Combat.Distance(p, pos) <= TileReach) ) points.Add(pos);
			}
			return points;
		}

		// the hops known from p, taken from part comp
		private IEnumerable<Hop> HopsFrom(CombatContext ctx, Offsets.Vector2i p, int comp) =>
			hops.Where(h => Combat.Distance(h.At, p) <= TileReach && Comp(ctx, h.From) == comp);

		/// <summary>
		/// The transition to take next from part me, and why: the first on the way to a part where Target is (by the hops
		/// learned, and the tiles that touch two parts), else the first on the way to a part with one not tried yet (or that
		/// one, here). Null if neither is known.
		/// </summary>
		private (Offsets.Vector2i? at, string why) Choose(CombatContext ctx, List<Offsets.Vector2i> points, int me, HashSet<int> goal) {
			// edges: part -> (transition, part), from the hops taken (or assumed), and from tiles touching two parts
			var edges = new Dictionary<int, List<(Offsets.Vector2i at, int to)>>();
			void Edge(int from, Offsets.Vector2i at, int to) {
				if ( from < 0 || to < 0 || from == to ) return;
				if ( !edges.TryGetValue(from, out var list) ) edges[from] = list = new List<(Offsets.Vector2i, int)>();
				list.Add((at, to));
			}
			foreach ( var h in hops.Where(h => !h.Dead) ) Edge(Comp(ctx, h.From), h.At, Comp(ctx, h.To));
			foreach ( var p in points ) {
				var s = Sides(ctx, p);
				if ( s.Count < 2 ) continue;
				foreach ( var a in s ) foreach ( var b in s ) {
					// (taken from a already: what it did is known)
					if ( a.comp != b.comp && !HopsFrom(ctx, p, a.comp).Any(h => !h.Assumed) ) Edge(a.comp, p, b.comp);
				}
			}
			// breadth first from me: the first step to the nearest part that satisfies wanted
			Offsets.Vector2i? FirstStep(Func<int, bool> wanted, out int reached) {
				reached = -1;
				var firstHop = new Dictionary<int, Offsets.Vector2i?> { [me] = null };
				var queue = new Queue<int>();
				queue.Enqueue(me);
				while ( queue.Count > 0 ) {
					int c = queue.Dequeue();
					if ( wanted(c) ) { reached = c; return firstHop[c]; }
					if ( !edges.TryGetValue(c, out var list) ) continue;
					// (nearer transitions first, from where the player is)
					foreach ( var (at, to) in list.OrderBy(e => Combat.Distance(ctx.Grid, e.at)) ) {
						if ( firstHop.ContainsKey(to) ) continue;
						firstHop[to] = firstHop[c] ?? at;
						queue.Enqueue(to);
					}
				}
				return null;
			}
			var step = FirstStep(c => goal.Contains(c), out int reachedGoal);
			if ( reachedGoal >= 0 && step.HasValue ) return (step, $"on the way to {Label}");
			// not known: a transition not tried yet, here or the nearest part that has one
			Offsets.Vector2i? Untried(int comp) => points
				.Where(p => Sides(ctx, p).Any(s => s.comp == comp) && !HopsFrom(ctx, p, comp).Any())
				.OrderBy(p => comp == me ? Combat.Distance(ctx.Grid, p) : 0)
				.Select(p => (Offsets.Vector2i?)p).FirstOrDefault();
			step = FirstStep(c => Untried(c).HasValue, out int reachedUntried);
			if ( reachedUntried == me ) return (Untried(me), "not tried yet");
			if ( reachedUntried >= 0 && step.HasValue ) return (step, "on the way to one not tried yet");
			return (null, null);
		}

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( hopsHash != AreaHash ) { hopsHash = AreaHash; hops.Clear(); pending = false; }
			// a click that moved the player (to another part, or far): learn what the transition did
			if ( pending ) {
				bool moved = Comp(ctx, ctx.Grid) != Comp(ctx, pendingFrom) || Combat.Distance(ctx.Grid, pendingFrom) > 30;
				if ( moved ) {
					pending = false;
					pendingClicks = 0;
					hops.RemoveAll(h => Combat.Distance(h.At, pendingAt) <= TileReach && Comp(ctx, h.From) == Comp(ctx, pendingFrom));
					hops.Add(new Hop() { At = pendingAt, From = pendingFrom, To = ctx.Grid });
					// the transition by where the player arrived: assumed to be the way back
					var back = Points(ctx).Where(p => Combat.Distance(p, ctx.Grid) <= TileReach && Sides(ctx, p).Any(s => s.comp == Comp(ctx, ctx.Grid)))
						.OrderBy(p => Combat.Distance(p, ctx.Grid)).Select(p => (Offsets.Vector2i?)p).FirstOrDefault();
					if ( back.HasValue && !HopsFrom(ctx, back.Value, Comp(ctx, ctx.Grid)).Any() ) {
						hops.Add(new Hop() { At = back.Value, From = ctx.Grid, To = pendingFrom, Assumed = true });
					}
					Log($"Campaign: {Name}: the transition at {pendingAt} took the player from {pendingFrom} to {ctx.Grid}{(back.HasValue ? $" (the way back: {back.Value})" : "")}");
				} else if ( now - lastClick > 3000 ) {
					pending = false;
					if ( pendingClicks >= MaxClicks ) {
						hops.Add(new Hop() { At = pendingAt, From = pendingFrom, To = pendingFrom, Dead = true });
						pendingClicks = 0;
						Log($"Campaign: {Name}: the transition at {pendingAt} did nothing after {MaxClicks} clicks from {pendingFrom}: not trying it from there again");
					}
				}
			}
			if ( FightFirst(ctx, FightRadius) is IState fight ) { Detail = "fighting"; return fight; }

			var target = Target(ctx);
			if ( !target.HasValue ) {
				Detail = $"{Label} is not known yet: exploring";
				return Call(new Explore(Label) { FightRadius = IgnoreMonsters ? -1 : 0 });
			}
			int me = Comp(ctx, ctx.Grid);
			var goal = new HashSet<int>(Sides(ctx, target.Value).Select(s => s.comp));
			var points = Points(ctx);
			var (at, why) = Choose(ctx, points, me, goal);
			if ( !at.HasValue ) {
				Detail = "no way known, and no transition left to try: exploring";
				return Call(new Explore(Label) { FightRadius = IgnoreMonsters ? -1 : 0 });
			}
			var p = at.Value;
			if ( !hasChoice || !lastChoice.Equals(p) ) {
				hasChoice = true;
				lastChoice = p;
				pendingClicks = 0;
				Log($"Campaign: {Name}: taking the transition at {p} ({why}); hops known: {string.Join("; ", hops)}");
			}
			// its label: the nearest AreaTransition label to it
			var label = (GetUI()?.LabelsOnGround?.GetAllLabels() ?? Enumerable.Empty<LabelOnGround>())
				.Where(l => IsValid(l.Item?.GetComponent<AreaTransition>()) && !LeadsElsewhere(l.Item))
				.Select(l => (label: l, pos: l.Item.GetComponent<Positioned>()))
				.Where(x => IsValid(x.pos) && Combat.Distance(x.pos.GridPos, p) <= TileReach)
				.OrderBy(x => Combat.Distance(x.pos.GridPos, p))
				.FirstOrDefault();
			var clickAt = label.label != null ? label.pos.GridPos : p;
			if ( !IsNear(ctx, clickAt, Reach, out _, out float d) ) {
				Detail = $"to the transition at {p} ({why}), {d:F0} cells away";
				return Call(new Navigate($"the transition at {p}", c => Approach(c, clickAt), Reach) { FightRadius = IgnoreMonsters ? -1 : FightRadius, LegMs = 1000 });
			}
			if ( label.label == null ) {
				// (eg, a door that is not targetable while the arena is locked)
				ctx.Driver.StopWalking("waiting for the transition's label");
				Detail = $"at the transition at {p}: no label shows";
				if ( now - lastClick > 5000 ) {
					lastClick = now;
					pendingAt = p; pendingFrom = ctx.Grid; pending = true;
					pendingClicks = Math.Max(pendingClicks, MaxClicks); // (gives it up, if nothing changes)
				}
				return this;
			}
			ctx.Driver.StopWalking($"taking the {Ground.LabelText(label.label.Label)?.Trim()}");
			if ( now - lastClick < 2000 || !(Ground.ClickLabel(label.label, this) is IState click) ) return this;
			lastClick = now;
			pending = true;
			pendingAt = p;
			pendingFrom = ctx.Grid;
			pendingClicks += 1;
			Detail = $"taking the {Ground.LabelText(label.label.Label)?.Trim()} at {label.pos.GridPos} ({why})";
			return click;
		}
	}
}
