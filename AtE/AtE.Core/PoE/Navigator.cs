using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The result of a path search, in grid cells.
	/// </summary>
	public class NavPath {
		/// <summary>Every cell along the path, from start to goal.</summary>
		public List<Offsets.Vector2i> Cells;
		/// <summary>A reduced set of points, where each straight leg is walkable with enough clearance.</summary>
		public List<Offsets.Vector2i> Waypoints;
		/// <summary>The index in Cells of each of the Waypoints.</summary>
		public List<int> WaypointIndices;
		/// <summary>
		/// The cells of the last full search (shared by the paths repaired from it), and how far along it the player has been seen.
		/// UpdatePath only ever splices onto this plan, never onto an earlier repair.
		/// </summary>
		public List<Offsets.Vector2i> Plan;
		public int PlanProgress;
		public Offsets.Vector2i Start, Goal;
		public float Cost;
		public int Expanded; // how many cells the search visited
		public double ElapsedMs;
		/// <summary>How this path was made: "corridor", "full" (the corridor search failed), or "repair" (re-used a previous path).</summary>
		public string Method;
		/// <summary>The Terrain.Generation (and DoorGeneration) this path was planned on.</summary>
		public int Generation, DoorGeneration;
		/// <summary>
		/// What to do to follow the path: walk to each waypoint in turn, and open each closed door on the way
		/// (the waypoint before an OpenDoor step is the cell just in front of the door).
		/// </summary>
		public List<NavStep> Steps;
	}

	public enum NavStepKind { Walk, OpenDoor }

	public class NavStep {
		public NavStepKind Kind;
		/// <summary>Walk: where to walk to. OpenDoor: the door's position.</summary>
		public Offsets.Vector2i Target;
		/// <summary>OpenDoor: the door to open, by clicking its label (AreaDoor.GetLabel).</summary>
		public AreaDoor Door;
		public override string ToString() => Kind == NavStepKind.Walk ? $"walk to {Target}" : $"open door {Door?.EntityId} at {Target}";
	}

	/// <summary>
	/// A* over the walkable grid of a Terrain.
	/// Steps cost more near walls (low clearance), so paths prefer the middle of corridors.
	///
	/// Long paths are planned in two passes: first over a coarse graph of regions (the connected parts of 8x8 cell blocks),
	/// then over the cells again, but only inside a corridor of regions around the coarse route.
	/// UpdatePath re-uses a previous path while the player stays near it, so following a path
	/// only costs a small local search per update.
	///
	/// All the search state is re-used across searches, and rebuilt when the Terrain changes.
	/// Not thread safe.
	/// </summary>
	public class Navigator {

		/// <summary>Extra cost multiplier for walking next to a wall; 0 ignores clearance.</summary>
		public float WallPenalty = 3f;
		/// <summary>Give up after visiting this many cells.</summary>
		public int MaxExpanded = 500_000;
		/// <summary>Straight waypoint legs must keep at least this much clearance (1..5).</summary>
		public int SmoothClearance = 3;
		/// <summary>The longest straight leg (in path cells) that smoothing will consider.</summary>
		public int MaxLeg = 100;
		/// <summary>How far to search for a walkable cell when the start or goal is blocked.</summary>
		public int SnapRadius = 20;
		/// <summary>Searches shorter than this (in cells) use a corridor at least 1 block wide.</summary>
		public int ShortTripDistance = 64;
		/// <summary>How many blocks around the coarse route the fine search may also use; 0 is fastest, 1 finds slightly cheaper paths.</summary>
		public int CorridorRadius = 0;
		/// <summary>Values over 1 make the cell search greedier: faster, but the path can cost up to this many times the best one.</summary>
		public float HeuristicWeight = 1f;
		/// <summary>UpdatePath re-uses the previous path while the start is within this many cells of it.</summary>
		public int RepairRadius = 12;
		/// <summary>UpdatePath rejoins the previous path this many cells ahead of the closest point.</summary>
		public int RepairAhead = 16;
		/// <summary>The extra cost (in cells walked) of going through a closed door, so open ways are preferred.</summary>
		public float DoorPenalty = 30f;
		/// <summary>The clearance given to the cells of a door's passage.</summary>
		public byte DoorClearance = 3;

		private static readonly int[] DX = { 0, 1, 1, 1, 0, -1, -1, -1 };
		private static readonly int[] DY = { 1, 1, 0, -1, -1, -1, 0, 1 };
		private const float Sqrt2 = 1.41421356f;
		private const int BlockShift = 3; // 8x8 cell blocks
		private const int BlockSize = 1 << BlockShift;

		// the terrain these arrays were built from
		private Terrain prepared;
		private int preparedGeneration = -1, preparedDoorGeneration = -1;
		private float preparedWallPenalty = float.NaN;

		// doors: the door (index into Terrain.Doors, plus 1) whose passage each padded cell is in, or 0
		private ushort[] doorAt;
		private IReadOnlyList<AreaDoor> doors = Array.Empty<AreaDoor>();
		private bool[] doorClosed = Array.Empty<bool>(); // a snapshot taken before each search

		// fine grid, padded by one blocked cell on every side so neighbors never need a bounds check
		// cell (x,y) is at index (y + 1) * pw + (x + 1)
		private int pw, ph;
		private byte[] clear; // clearance 0..5
		private readonly float[] penalty = new float[6]; // step cost multiplier by clearance
		private int[] dirOffset = new int[8];
		private float[] g;
		private byte[] from;
		private int[] stamp; // == 2*gen when open, 2*gen+1 when closed
		private int gen = 0;
		private readonly PriorityQueue<int, float> open = new PriorityQueue<int, float>();

		// coarse graph: each 8x8 block of cells is split into regions (4-connected walkable cells),
		// so two cells in the same region are always connected, and two regions are linked only where their cells touch
		private int bw, bh;
		private int[] cellRegion; // per padded cell, the region it is in, or -1
		private int regionCount;
		private int[] blockFirstRegion; // the regions of block b are blockFirstRegion[b] .. blockFirstRegion[b+1]-1
		private int[] regionBlock;
		private float[] regionX, regionY; // the center of the region, in cells
		private float[] regionPenalty; // the average step cost multiplier of its cells
		private int[] regionSize; // how many cells it has
		private int[] regionComponent; // regions with different components are not connected at all
		private int[] adjStart, adjTo; // the neighbors of region r are adjTo[adjStart[r] .. adjStart[r+1]-1]
		private float[] rg;
		private int[] rfrom;
		private int[] rstamp;
		private int rgen = 0;
		private int[] corridor; // == corridorGen if the region is part of the current corridor
		private int corridorGen = 0;
		private readonly PriorityQueue<int, float> ropen = new PriorityQueue<int, float>();

		private float StepPenalty(int clearance) => 1f + (WallPenalty * (5 - Math.Min(5, clearance)) / 4f);

		private static float Octile(float dx, float dy) {
			dx = Math.Abs(dx);
			dy = Math.Abs(dy);
			return dx > dy ? dx + ((Sqrt2 - 1) * dy) : dy + ((Sqrt2 - 1) * dx);
		}

		/// <summary>
		/// Build the search arrays for terrain, if they are not already built.
		/// This is the slow part of the first search in a new area (~40ms Release, ~85ms Debug on The Coast), so it is
		/// worth calling as soon as the area loads. Searches call it too, so calling it is optional.
		/// </summary>
		public void Prepare(Terrain terrain) {
			var terrainDoors = terrain.Doors; // (reading Doors can find new ones, so read it before checking DoorGeneration)
			if ( prepared == terrain && preparedGeneration == terrain.Generation && preparedDoorGeneration == terrain.DoorGeneration
				&& preparedWallPenalty == WallPenalty ) return;
			for ( int c = 0; c < penalty.Length; c++ ) penalty[c] = StepPenalty(c);
			int width = terrain.Width, height = terrain.Height;
			if ( clear == null || pw != width + 2 || ph != height + 2 ) {
				pw = width + 2;
				ph = height + 2;
				int n = pw * ph;
				clear = new byte[n];
				doorAt = new ushort[n];
				g = new float[n];
				from = new byte[n];
				stamp = new int[n];
				gen = 0;
			} else {
				Array.Clear(clear, 0, clear.Length);
				Array.Clear(doorAt, 0, doorAt.Length);
			}
			for ( int d = 0; d < 8; d++ ) dirOffset[d] = (DY[d] * pw) + DX[d];
			terrain.UnpackClearance(clear, pw + 1, pw);
			// door passages are walkable here (Terrain still has them blocked as they were when the area loaded)
			doors = terrainDoors;
			for ( int i = 0; i < doors.Count && i < ushort.MaxValue; i++ ) {
				foreach ( var cell in doors[i].Passage ) {
					if ( !terrain.InBounds(cell.X, cell.Y) ) continue;
					int idx = ((cell.Y + 1) * pw) + cell.X + 1;
					clear[idx] = Math.Max(clear[idx], DoorClearance);
					doorAt[idx] = (ushort)(i + 1);
				}
			}
			PrepareRegions(width, height);
			prepared = terrain;
			preparedGeneration = terrain.Generation;
			preparedDoorGeneration = terrain.DoorGeneration;
			preparedWallPenalty = WallPenalty;
		}

		private void PrepareRegions(int width, int height) {
			bw = (width + BlockSize - 1) >> BlockShift;
			bh = (height + BlockSize - 1) >> BlockShift;
			if ( cellRegion == null || cellRegion.Length != clear.Length ) cellRegion = new int[clear.Length];
			Array.Fill(cellRegion, -1);
			blockFirstRegion = new int[(bw * bh) + 1];
			var blockOf = new List<int>();
			var sumX = new List<float>();
			var sumY = new List<float>();
			var sumPenalty = new List<float>();
			var counts = new List<int>();
			var local = new int[BlockSize * BlockSize]; // the region of each cell of the current block, or -1
			var stack = new int[BlockSize * BlockSize];

			// flood fill each block into regions, in block-local coordinates (lx, ly), li = ly * BlockSize + lx
			for ( int by = 0; by < bh; by++ ) {
				for ( int bx = 0; bx < bw; bx++ ) {
					int b = (by * bw) + bx;
					blockFirstRegion[b] = blockOf.Count;
					int x0 = (bx << BlockShift) + 1, y0 = (by << BlockShift) + 1; // padded
					int sizeX = Math.Min(BlockSize, width + 1 - x0), sizeY = Math.Min(BlockSize, height + 1 - y0);
					Array.Fill(local, -1);
					for ( int start = 0; start < local.Length; start++ ) {
						int sx0 = start & (BlockSize - 1), sy0 = start >> BlockShift;
						if ( sx0 >= sizeX || sy0 >= sizeY || local[start] != -1 ) continue;
						if ( clear[((y0 + sy0) * pw) + x0 + sx0] == 0 ) continue;
						int r = blockOf.Count;
						float sx = 0, sy = 0, sp = 0;
						int count = 0, top = 0;
						local[start] = r;
						stack[top++] = start;
						while ( top > 0 ) {
							int li = stack[--top];
							int lx = li & (BlockSize - 1), ly = li >> BlockShift;
							int idx = ((y0 + ly) * pw) + x0 + lx;
							cellRegion[idx] = r;
							sx += x0 + lx - 1; sy += y0 + ly - 1; sp += penalty[clear[idx]]; count += 1;
							if ( lx + 1 < sizeX && local[li + 1] == -1 && clear[idx + 1] > 0 ) { local[li + 1] = r; stack[top++] = li + 1; }
							if ( lx > 0 && local[li - 1] == -1 && clear[idx - 1] > 0 ) { local[li - 1] = r; stack[top++] = li - 1; }
							if ( ly + 1 < sizeY && local[li + BlockSize] == -1 && clear[idx + pw] > 0 ) { local[li + BlockSize] = r; stack[top++] = li + BlockSize; }
							if ( ly > 0 && local[li - BlockSize] == -1 && clear[idx - pw] > 0 ) { local[li - BlockSize] = r; stack[top++] = li - BlockSize; }
						}
						blockOf.Add(b);
						sumX.Add(sx); sumY.Add(sy); sumPenalty.Add(sp); counts.Add(count);
					}
				}
			}
			regionCount = blockOf.Count;
			blockFirstRegion[bw * bh] = regionCount;
			regionBlock = blockOf.ToArray();
			regionX = new float[regionCount];
			regionY = new float[regionCount];
			regionPenalty = new float[regionCount];
			regionSize = counts.ToArray();
			for ( int r = 0; r < regionCount; r++ ) {
				regionX[r] = sumX[r] / counts[r];
				regionY[r] = sumY[r] / counts[r];
				regionPenalty[r] = sumPenalty[r] / counts[r];
			}

			// link the regions whose cells touch across a block border (orthogonally, or diagonally at a corner without cutting it)
			var links = new List<long>(); // (from << 32 | to); not a HashSet<long>, whose hash (from ^ to) collides badly here
			void Link(int a, int c) {
				if ( a < 0 || c < 0 || a == c ) return;
				links.Add(((long)a << 32) | (uint)c);
				links.Add(((long)c << 32) | (uint)a);
			}
			for ( int by = 0; by < bh; by++ ) {
				for ( int bx = 0; bx < bw; bx++ ) {
					int x0 = (bx << BlockShift) + 1, y0 = (by << BlockShift) + 1; // padded
					int x1 = Math.Min(x0 + BlockSize - 1, width), y1 = Math.Min(y0 + BlockSize - 1, height);
					// along a border the same pair of regions repeats, so only link when the pair changes
					long last = -1;
					for ( int y = y0; y <= y1; y++ ) { // east border
						int idx = (y * pw) + x1;
						int r = cellRegion[idx], s = cellRegion[idx + 1];
						long pair = ((long)r << 32) | (uint)s;
						if ( r >= 0 && s >= 0 && pair != last ) { Link(r, s); last = pair; }
					}
					last = -1;
					for ( int x = x0; x <= x1; x++ ) { // north border
						int idx = (y1 * pw) + x;
						int r = cellRegion[idx], s = cellRegion[idx + pw];
						long pair = ((long)r << 32) | (uint)s;
						if ( r >= 0 && s >= 0 && pair != last ) { Link(r, s); last = pair; }
					}
					int ne = (y1 * pw) + x1;
					if ( clear[ne] > 0 && clear[ne + 1] > 0 && clear[ne + pw] > 0 ) Link(cellRegion[ne], cellRegion[ne + pw + 1]);
					int se = (y0 * pw) + x1;
					if ( clear[se] > 0 && clear[se + 1] > 0 && clear[se - pw] > 0 ) Link(cellRegion[se], cellRegion[se - pw + 1]);
				}
			}
			// sort the (from, to) pairs and drop duplicates, then they are already grouped by region
			links.Sort();
			int unique = 0;
			for ( int i = 0; i < links.Count; i++ ) {
				if ( unique == 0 || links[i] != links[unique - 1] ) links[unique++] = links[i];
			}
			adjStart = new int[regionCount + 1];
			adjTo = new int[unique];
			for ( int i = 0; i < unique; i++ ) {
				adjStart[(int)(links[i] >> 32) + 1] += 1;
				adjTo[i] = (int)(links[i] & 0xFFFFFFFF);
			}
			for ( int r = 0; r < regionCount; r++ ) adjStart[r + 1] += adjStart[r];

			// label connected components, so an unreachable goal is known without searching
			regionComponent = new int[regionCount];
			Array.Fill(regionComponent, -1);
			int component = 0;
			var queue = new Queue<int>();
			for ( int r = 0; r < regionCount; r++ ) {
				if ( regionComponent[r] != -1 ) continue;
				regionComponent[r] = component;
				queue.Enqueue(r);
				while ( queue.Count > 0 ) {
					int a = queue.Dequeue();
					for ( int i = adjStart[a]; i < adjStart[a + 1]; i++ ) {
						if ( regionComponent[adjTo[i]] == -1 ) {
							regionComponent[adjTo[i]] = component;
							queue.Enqueue(adjTo[i]);
						}
					}
				}
				component += 1;
			}

			rg = new float[regionCount];
			rfrom = new int[regionCount];
			rstamp = new int[regionCount];
			corridor = new int[regionCount];
			rgen = corridorGen = 0;
		}

		/// <summary>
		/// Find the closest walkable cell to pos, within SnapRadius, searching outward in square rings.
		/// </summary>
		public bool TrySnap(Terrain terrain, Offsets.Vector2i pos, out Offsets.Vector2i result) {
			result = pos;
			if ( terrain.IsWalkable(pos) ) return true;
			for ( int r = 1; r <= SnapRadius; r++ ) {
				int best = int.MaxValue;
				for ( int dx = -r; dx <= r; dx++ ) {
					for ( int dy = -r; dy <= r; dy++ ) {
						if ( Math.Max(Math.Abs(dx), Math.Abs(dy)) != r ) continue; // only the ring
						int d = (dx * dx) + (dy * dy);
						if ( d < best && terrain.IsWalkable(pos.X + dx, pos.Y + dy) ) {
							best = d;
							result = new Offsets.Vector2i(pos.X + dx, pos.Y + dy);
						}
					}
				}
				if ( best != int.MaxValue ) return true;
			}
			return false;
		}

		/// <summary>
		/// Find a path from start to goal. Blocked start or goal cells are moved to the nearest walkable cell.
		/// Returns null if there is no path (or the search gave up after MaxExpanded cells).
		/// </summary>
		public NavPath FindPath(Terrain terrain, Offsets.Vector2i start, Offsets.Vector2i goal) {
			if ( terrain == null || !terrain.IsLoaded ) return null;
			if ( !TrySnap(terrain, start, out start) || !TrySnap(terrain, goal, out goal) ) return null;
			var timer = Stopwatch.StartNew();
			Prepare(terrain);
			SnapshotDoors();

			int startR = cellRegion[((start.Y + 1) * pw) + start.X + 1];
			int goalR = cellRegion[((goal.Y + 1) * pw) + goal.X + 1];
			if ( regionComponent[startR] != regionComponent[goalR] ) return null; // not connected at all

			string method = "corridor";
			List<Offsets.Vector2i> cells = null;
			int expanded = 0;
			float cost = 0;
			// short trips can afford a wider corridor (and a better path)
			int radius = Octile(goal.X - start.X, goal.Y - start.Y) <= ShortTripDistance ? Math.Max(1, CorridorRadius) : CorridorRadius;
			if ( SearchRegions(startR, goalR, goal, radius) ) {
				cells = Search(start, goal, true, null, ref expanded, out cost);
			}
			if ( cells == null ) { // should not happen, the regions are connected so the corridor contains a path; but just in case
				method = "full";
				cells = Search(start, goal, false, null, ref expanded, out cost);
			}
			if ( cells == null ) return null;
			return Finish(new NavPath() {
				Cells = cells,
				Plan = cells,
				Start = start,
				Goal = goal,
				Cost = cost,
				Expanded = expanded,
				Method = method,
				Generation = terrain.Generation,
				DoorGeneration = terrain.DoorGeneration,
			}, timer);
		}

		/// <summary>
		/// Like FindPath, but re-use previous (a path to the same goal) when start is still close to it:
		/// a small local search re-joins the previous path a little way ahead, instead of planning it all again.
		/// Use this when re-planning every frame while following a path.
		/// </summary>
		public NavPath UpdatePath(NavPath previous, Terrain terrain, Offsets.Vector2i start, Offsets.Vector2i goal) {
			if ( terrain == null || !terrain.IsLoaded ) return null;
			_ = terrain.Doors; // (finds any new doors, which changes DoorGeneration)
			if ( previous == null || previous.Generation != terrain.Generation || previous.DoorGeneration != terrain.DoorGeneration
				|| previous.Cells.Count == 0
				|| !TrySnap(terrain, goal, out goal) || goal.Id != previous.Goal.Id
				|| !TrySnap(terrain, start, out start) ) {
				return FindPath(terrain, start, goal);
			}
			var timer = Stopwatch.StartNew();
			Prepare(terrain);
			SnapshotDoors();
			// measure the player against the plan itself, not against the previous (repaired) path:
			// otherwise, when the player walks away from the plan, each repair re-joins the head of the one before
			// (which leads back to the plan), and the path never gets re-planned
			var plan = previous.Plan ?? previous.Cells;
			int from = Math.Max(0, previous.PlanProgress - RepairRadius);
			int window = Math.Min(plan.Count, previous.PlanProgress + (RepairRadius * 8));
			int closest = -1;
			int best = int.MaxValue;
			for ( int i = from; i < window; i++ ) {
				int dx = plan[i].X - start.X, dy = plan[i].Y - start.Y;
				int d = (dx * dx) + (dy * dy);
				if ( d < best ) { best = d; closest = i; }
			}
			if ( best > RepairRadius * RepairRadius ) return FindPath(terrain, start, goal);

			int rejoin = Math.Min(closest + RepairAhead, plan.Count - 1);
			var target = plan[rejoin];
			int expanded = 0;
			List<Offsets.Vector2i> head;
			if ( best == 0 ) {
				head = new List<Offsets.Vector2i>() { start };
				rejoin = closest;
			} else {
				// keep the local search in a box around both ends
				int margin = RepairRadius + 8;
				var box = (Math.Min(start.X, target.X) - margin, Math.Min(start.Y, target.Y) - margin,
					Math.Max(start.X, target.X) + margin, Math.Max(start.Y, target.Y) + margin);
				head = Search(start, target, false, box, ref expanded, out _);
				if ( head == null ) return FindPath(terrain, start, goal);
			}
			var result = new List<Offsets.Vector2i>(head.Count + plan.Count - rejoin);
			result.AddRange(head);
			for ( int i = rejoin + 1; i < plan.Count; i++ ) result.Add(plan[i]);
			// smooth it all again (in Finish): re-using the old waypoints past the splice left stray one-cell hops behind,
			// and smoothing a whole cross-map path is still under 1ms (Debug build)
			return Finish(new NavPath() {
				Cells = result,
				Plan = plan,
				PlanProgress = closest,
				Start = start,
				Goal = goal,
				Cost = previous.Cost, // approximate
				Expanded = expanded,
				Method = "repair",
				Generation = terrain.Generation,
				DoorGeneration = terrain.DoorGeneration,
			}, timer);
		}

		private void SnapshotDoors() {
			if ( doorClosed.Length != doors.Count ) doorClosed = new bool[doors.Count];
			for ( int i = 0; i < doors.Count; i++ ) doorClosed[i] = doors[i].IsClosed;
		}

		private int Index(Offsets.Vector2i cell) => ((cell.Y + 1) * pw) + cell.X + 1;

		/// <summary>The closed door (index into doors, plus 1) whose passage the cell is in, or 0.</summary>
		private int ClosedDoorAt(int idx) {
			int d = doorAt[idx];
			return d != 0 && doorClosed[d - 1] ? d : 0;
		}

		/// <summary>
		/// Make the Waypoints and Steps of a path from its Cells: the path is smoothed in pieces between the closed doors it
		/// goes through, so the cell in front of each door (where to stand to open it) and the cell behind it are waypoints.
		/// </summary>
		private NavPath Finish(NavPath path, Stopwatch timer) {
			var cells = path.Cells;
			var waypoints = new List<int>() { 0 };
			var steps = new List<NavStep>();
			void WalkTo(int index) {
				waypoints.Add(index);
				steps.Add(new NavStep() { Kind = NavStepKind.Walk, Target = cells[index] });
			}
			int pieceStart = 0;
			for ( int i = 1; i < cells.Count; i++ ) {
				int door = ClosedDoorAt(Index(cells[i]));
				if ( door == 0 || doorAt[Index(cells[i - 1])] == door ) continue;
				int exit = i;
				while ( exit < cells.Count - 1 && doorAt[Index(cells[exit])] == door ) exit += 1;
				// walk up to the door
				var piece = SmoothIndices(cells, pieceStart, i - 1);
				for ( int k = 1; k < piece.Count; k++ ) WalkTo(piece[k]);
				// open it, then walk through it
				steps.Add(new NavStep() { Kind = NavStepKind.OpenDoor, Target = doors[door - 1].GridPos, Door = doors[door - 1] });
				WalkTo(exit);
				pieceStart = exit;
				i = exit;
			}
			var last = SmoothIndices(cells, pieceStart, cells.Count - 1);
			for ( int k = 1; k < last.Count; k++ ) WalkTo(last[k]);
			path.WaypointIndices = waypoints;
			path.Waypoints = Pick(cells, waypoints);
			path.Steps = steps;
			path.ElapsedMs = timer.Elapsed.TotalMilliseconds;
			return path;
		}

		/// <summary>
		/// True if a path from a to b exists (through doors, open or not). Cheap: no search.
		/// </summary>
		/// <summary>
		/// The walkable cell nearest pos (within maxRadius cells) that can be walked to from from. Unlike TrySnap, which
		/// takes the nearest walkable cell, this never picks one on the far side of a wall (eg, for a door or an area
		/// transition set into a wall, whose own cell is blocked).
		/// </summary>
		public bool TrySnapReachable(Terrain terrain, Offsets.Vector2i from, Offsets.Vector2i pos, out Offsets.Vector2i result, int maxRadius = 40) {
			result = pos;
			if ( terrain == null || !terrain.IsLoaded || !TrySnap(terrain, from, out from) ) return false;
			Prepare(terrain);
			int want = regionComponent[cellRegion[Index(from)]];
			bool Ok(int x, int y) {
				if ( x < 0 || y < 0 || x >= terrain.Width || y >= terrain.Height || !terrain.IsWalkable(x, y) ) return false;
				int region = cellRegion[Index(new Offsets.Vector2i(x, y))];
				return region >= 0 && regionComponent[region] == want;
			}
			if ( Ok(pos.X, pos.Y) ) return true;
			for ( int r = 1; r <= maxRadius; r++ ) {
				int best = int.MaxValue;
				for ( int dx = -r; dx <= r; dx++ ) {
					for ( int dy = -r; dy <= r; dy++ ) {
						if ( Math.Max(Math.Abs(dx), Math.Abs(dy)) != r ) continue; // only the ring
						int d = (dx * dx) + (dy * dy);
						if ( d < best && Ok(pos.X + dx, pos.Y + dy) ) {
							best = d;
							result = new Offsets.Vector2i(pos.X + dx, pos.Y + dy);
						}
					}
				}
				if ( best != int.MaxValue ) return true;
			}
			return false;
		}

		public bool IsReachable(Terrain terrain, Offsets.Vector2i a, Offsets.Vector2i b) {
			if ( terrain == null || !terrain.IsLoaded ) return false;
			if ( !TrySnap(terrain, a, out a) || !TrySnap(terrain, b, out b) ) return false;
			Prepare(terrain);
			return regionComponent[cellRegion[Index(a)]] == regionComponent[cellRegion[Index(b)]];
		}

		/// <summary>
		/// Search outward from start, nearest first by travel cost (over the coarse regions, so the cost is approximate),
		/// for the first region whose cells satisfy wanted. Returns false if no reachable region does.
		/// target is the cell of that region closest to its center; cost is the approximate travel cost to it.
		/// mightWant, if given, is a cheap test (block x, block y, region size) to skip regions without listing their cells.
		/// </summary>
		public bool FindNearestRegion(Terrain terrain, Offsets.Vector2i start, Func<List<Offsets.Vector2i>, bool> wanted,
			out Offsets.Vector2i target, out float cost, Func<int, int, int, bool> mightWant = null) {
			target = default;
			cost = 0;
			if ( terrain == null || !terrain.IsLoaded || !TrySnap(terrain, start, out start) ) return false;
			Prepare(terrain);
			int startR = cellRegion[Index(start)];
			rgen += 1;
			int openStamp = 2 * rgen, closedStamp = (2 * rgen) + 1;
			ropen.Clear();
			rg[startR] = 0;
			rstamp[startR] = openStamp;
			ropen.Enqueue(startR, 0);
			var cells = new List<Offsets.Vector2i>(BlockSize * BlockSize);
			bool found = false;
			while ( ropen.TryDequeue(out int r, out _) ) {
				if ( rstamp[r] == closedStamp ) continue;
				rstamp[r] = closedStamp;
				int b = regionBlock[r];
				bool maybe = mightWant == null || mightWant(b % bw, b / bw, regionSize[r]);
				if ( maybe ) {
					// the cells of region r
					cells.Clear();
					int x0 = (b % bw) << BlockShift, y0 = (b / bw) << BlockShift;
					for ( int y = y0; y < y0 + BlockSize && y < ph - 2; y++ ) {
						for ( int x = x0; x < x0 + BlockSize && x < pw - 2; x++ ) {
							if ( cellRegion[((y + 1) * pw) + x + 1] == r ) cells.Add(new Offsets.Vector2i(x, y));
						}
					}
				}
				if ( maybe && wanted(cells) ) {
					float best = float.MaxValue;
					foreach ( var c in cells ) {
						float d = Octile(c.X - regionX[r], c.Y - regionY[r]);
						if ( d < best ) { best = d; target = c; }
					}
					cost = rg[r];
					found = true;
					break;
				}
				for ( int i = adjStart[r]; i < adjStart[r + 1]; i++ ) {
					int n = adjTo[i];
					if ( rstamp[n] == closedStamp ) continue;
					float step = Octile(regionX[n] - regionX[r], regionY[n] - regionY[r]) * (regionPenalty[r] + regionPenalty[n]) / 2f;
					float gNext = rg[r] + step;
					if ( rstamp[n] == openStamp && gNext >= rg[n] ) continue;
					rg[n] = gNext;
					rstamp[n] = openStamp;
					ropen.Enqueue(n, gNext);
				}
			}
			ropen.Clear();
			return found;
		}

		/// <summary>
		/// A* over the regions; on success, marks the regions on the route, and all regions in the blocks
		/// within radius of it, as the corridor. The route's regions alone always contain a path.
		/// </summary>
		private bool SearchRegions(int startR, int goalR, Offsets.Vector2i goal, int radius) {
			rgen += 1;
			int openStamp = 2 * rgen, closedStamp = (2 * rgen) + 1;
			ropen.Clear();
			rg[startR] = 0;
			rstamp[startR] = openStamp;
			ropen.Enqueue(startR, 0);
			bool found = false;
			while ( ropen.TryDequeue(out int r, out _) ) {
				if ( rstamp[r] == closedStamp ) continue;
				rstamp[r] = closedStamp;
				if ( r == goalR ) { found = true; break; }
				for ( int i = adjStart[r]; i < adjStart[r + 1]; i++ ) {
					int n = adjTo[i];
					if ( rstamp[n] == closedStamp ) continue;
					float step = Octile(regionX[n] - regionX[r], regionY[n] - regionY[r]) * (regionPenalty[r] + regionPenalty[n]) / 2f;
					float gNext = rg[r] + step;
					if ( rstamp[n] == openStamp && gNext >= rg[n] ) continue;
					rg[n] = gNext;
					rfrom[n] = r;
					rstamp[n] = openStamp;
					ropen.Enqueue(n, gNext + Octile(goal.X - regionX[n], goal.Y - regionY[n]));
				}
			}
			ropen.Clear();
			if ( !found ) return false;
			corridorGen += 1;
			for ( int r = goalR; ; r = rfrom[r] ) {
				corridor[r] = corridorGen;
				int b = regionBlock[r];
				int bx = b % bw, by = b / bw;
				for ( int dy = -radius; dy <= radius; dy++ ) {
					for ( int dx = -radius; dx <= radius; dx++ ) {
						int nx = bx + dx, ny = by + dy;
						if ( nx < 0 || ny < 0 || nx >= bw || ny >= bh ) continue;
						int nb = (ny * bw) + nx;
						for ( int nr = blockFirstRegion[nb]; nr < blockFirstRegion[nb + 1]; nr++ ) corridor[nr] = corridorGen;
					}
				}
				if ( r == startR ) break;
			}
			return true;
		}

		/// <summary>
		/// A* over the cells, optionally limited to the current corridor of regions, or to a box (minX, minY, maxX, maxY) of cells.
		/// </summary>
		private List<Offsets.Vector2i> Search(Offsets.Vector2i start, Offsets.Vector2i goal, bool inCorridor,
			(int, int, int, int)? box, ref int expanded, out float cost) {
			cost = 0;
			gen += 1;
			if ( gen >= int.MaxValue / 2 ) { // wrapped; reset all the stamps
				Array.Clear(stamp, 0, stamp.Length);
				gen = 1;
			}
			int openStamp = 2 * gen, closedStamp = (2 * gen) + 1;
			// box limits, in padded coordinates
			int minX = 1, minY = 1, maxX = pw - 2, maxY = ph - 2;
			if ( box.HasValue ) {
				var (bx0, by0, bx1, by1) = box.Value;
				minX = Math.Max(minX, bx0 + 1); minY = Math.Max(minY, by0 + 1);
				maxX = Math.Min(maxX, bx1 + 1); maxY = Math.Min(maxY, by1 + 1);
			}
			int startIdx = ((start.Y + 1) * pw) + start.X + 1;
			int goalIdx = ((goal.Y + 1) * pw) + goal.X + 1;
			int goalX = goal.X + 1, goalY = goal.Y + 1;
			open.Clear();
			g[startIdx] = 0;
			stamp[startIdx] = openStamp;
			open.Enqueue(startIdx, 0);

			bool found = false;
			int count = 0;
			while ( open.TryDequeue(out int idx, out _) ) {
				if ( stamp[idx] == closedStamp ) continue; // a stale duplicate entry
				stamp[idx] = closedStamp;
				if ( idx == goalIdx ) { found = true; break; }
				if ( ++count > MaxExpanded ) break;
				int y = idx / pw, x = idx - (y * pw);
				float gHere = g[idx];
				for ( int dir = 0; dir < 8; dir++ ) {
					int nIdx = idx + dirOffset[dir];
					int c = clear[nIdx];
					if ( c == 0 || stamp[nIdx] == closedStamp ) continue;
					int nx = x + DX[dir], ny = y + DY[dir];
					if ( nx < minX || ny < minY || nx > maxX || ny > maxY ) continue;
					if ( inCorridor && corridor[cellRegion[nIdx]] != corridorGen ) continue;
					bool diagonal = (dir & 1) == 1;
					// don't cut corners: both orthogonal neighbors must be walkable too
					if ( diagonal && (clear[idx + DX[dir]] == 0 || clear[idx + (DY[dir] * pw)] == 0) ) continue;
					float gNext = gHere + ((diagonal ? Sqrt2 : 1f) * penalty[c]);
					int door = doorAt[nIdx];
					if ( door != 0 && door != doorAt[idx] && doorClosed[door - 1] ) gNext += DoorPenalty; // stepping into a closed door
					if ( stamp[nIdx] == openStamp && gNext >= g[nIdx] ) continue;
					g[nIdx] = gNext;
					from[nIdx] = (byte)dir;
					stamp[nIdx] = openStamp;
					open.Enqueue(nIdx, gNext + (HeuristicWeight * Octile(goalX - nx, goalY - ny)));
				}
			}
			open.Clear();
			expanded += count;
			if ( !found ) return null;
			cost = g[goalIdx];

			var cells = new List<Offsets.Vector2i>();
			for ( int idx = goalIdx; idx != startIdx; ) {
				int y = idx / pw, x = idx - (y * pw);
				cells.Add(new Offsets.Vector2i(x - 1, y - 1));
				idx -= dirOffset[from[idx]];
			}
			cells.Add(start);
			cells.Reverse();
			return cells;
		}

		/// <summary>
		/// Reduce a cell path to waypoints: from each waypoint, skip ahead to the furthest cell
		/// that can be reached in a straight line without dropping below SmoothClearance
		/// (or below the clearance of the cells being skipped, where the path itself is that tight).
		/// </summary>
		public List<Offsets.Vector2i> Smooth(Terrain terrain, List<Offsets.Vector2i> cells) {
			var result = new List<Offsets.Vector2i>();
			if ( cells == null || cells.Count == 0 ) return result;
			Prepare(terrain);
			SnapshotDoors();
			foreach ( int i in SmoothIndices(cells, 0, cells.Count - 1) ) result.Add(cells[i]);
			return result;
		}

		private readonly int[] legClearance = new int[1024];

		/// <summary>
		/// Smooth cells[from .. to], returning the indices of the waypoints (always including from and to).
		/// </summary>
		private List<int> SmoothIndices(List<Offsets.Vector2i> cells, int from, int to) {
			var result = new List<int>() { from };
			int maxLeg = Math.Min(MaxLeg, legClearance.Length - 1);
			int i = from;
			while ( i < to ) {
				int last = Math.Min(to, i + maxLeg);
				// legClearance[k] = the clearance needed for the leg i -> i+k
				// the leg's start counts too: IsLineClear checks it, so from a cell next to a wall no leg would ever pass,
				// and the path would be smoothed into one-cell hops
				int minClearance = Math.Min(SmoothClearance, clear[Index(cells[i])]);
				for ( int j = i + 1; j <= last; j++ ) {
					minClearance = Math.Min(minClearance, clear[Index(cells[j])]);
					legClearance[j - i] = Math.Max(1, minClearance);
				}
				// try the longest leg first: in the open it is clear right away
				int next = i + 1;
				for ( int j = last; j > i + 1; j-- ) {
					if ( IsLineClear(cells[i], cells[j], legClearance[j - i]) ) {
						next = j;
						break;
					}
				}
				result.Add(next);
				i = next;
			}
			return result;
		}

		/// <summary>
		/// Like Terrain.IsLineClear, but on this grid, where door passages are walkable; a closed door still blocks the line.
		/// </summary>
		private bool IsLineClear(Offsets.Vector2i a, Offsets.Vector2i b, int minClearance) {
			int x = a.X, y = a.Y;
			int dx = Math.Abs(b.X - x), dy = -Math.Abs(b.Y - y);
			int sx = x < b.X ? 1 : -1, sy = y < b.Y ? 1 : -1;
			int err = dx + dy;
			bool Clear(int cx, int cy) {
				int idx = ((cy + 1) * pw) + cx + 1;
				return clear[idx] >= minClearance && ClosedDoorAt(idx) == 0;
			}
			while ( true ) {
				if ( !Clear(x, y) ) return false;
				if ( x == b.X && y == b.Y ) return true;
				int e2 = 2 * err;
				bool stepX = e2 >= dy, stepY = e2 <= dx;
				// a diagonal step also needs both of the cells it squeezes between
				if ( stepX && stepY && !(Clear(x + sx, y) && Clear(x, y + sy)) ) return false;
				if ( stepX ) { err += dy; x += sx; }
				if ( stepY ) { err += dx; y += sy; }
			}
		}

		private static List<Offsets.Vector2i> Pick(List<Offsets.Vector2i> cells, List<int> indices) {
			var result = new List<Offsets.Vector2i>(indices.Count);
			foreach ( int i in indices ) result.Add(cells[i]);
			return result;
		}
	}

	public static partial class Globals {

		/// <summary>
		/// A shared Navigator for plugins; only use it from the main (OnTick) thread.
		/// </summary>
		public static readonly Navigator DefaultNavigator = new Navigator();

		/// <summary>
		/// Find a path from the player to goal over the current area's terrain, or null.
		/// </summary>
		public static NavPath FindPath(Offsets.Vector2i goal) {
			var pos = GetPlayer()?.GetComponent<Positioned>();
			return IsValid(pos) ? DefaultNavigator.FindPath(GetTerrain(), pos.GridPos, goal) : null;
		}

		/// <summary>
		/// Re-plan from the player to goal, re-using previous while the player is still near it (cheap enough for every frame).
		/// </summary>
		public static NavPath UpdatePath(NavPath previous, Offsets.Vector2i goal) {
			var pos = GetPlayer()?.GetComponent<Positioned>();
			return IsValid(pos) ? DefaultNavigator.UpdatePath(previous, GetTerrain(), pos.GridPos, goal) : null;
		}

		/// <summary>
		/// The (inverse of Offsets.GridToWorld) grid cell that contains a world position.
		/// </summary>
		public static Offsets.Vector2i WorldToGrid(Vector3 pos) => new Offsets.Vector2i(
			(int)Math.Floor(pos.X * 0.092f),
			(int)Math.Floor(pos.Y * 0.092f));

		/// <summary>
		/// Un-project a screen position onto the horizontal plane at height z (in world units).
		/// Returns false if the camera is not available.
		/// </summary>
		public static bool TryScreenToWorld(Vector2 screen, float z, out Vector3 world) {
			world = Vector3.Zero;
			var camera = GetCamera();
			var size = WindowSize.Value;
			if ( size.X <= 0 || size.Y <= 0 || !Matrix4x4.Invert(camera.Matrix, out Matrix4x4 inverse) ) return false;
			float ndcX = (2f * screen.X / size.X) - 1f;
			float ndcY = 1f - (2f * screen.Y / size.Y);
			Vector3 Unproject(float ndcZ) {
				var v = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), inverse);
				return new Vector3(v.X, v.Y, v.Z) / v.W;
			}
			Vector3 a = Unproject(0.1f), b = Unproject(0.9f);
			float dz = b.Z - a.Z;
			if ( Math.Abs(dz) < 1e-6f ) return false;
			world = Vector3.Lerp(a, b, (z - a.Z) / dz);
			return true;
		}

		public static bool TryScreenToGrid(Vector2 screen, float z, out Offsets.Vector2i grid) {
			bool ok = TryScreenToWorld(screen, z, out Vector3 world);
			grid = ok ? WorldToGrid(world) : default;
			return ok;
		}
	}
}
