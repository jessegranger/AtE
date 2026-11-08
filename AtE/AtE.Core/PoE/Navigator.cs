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
		public Offsets.Vector2i Start, Goal;
		public float Cost;
		public int Expanded; // how many cells the search visited
		public double ElapsedMs;
	}

	/// <summary>
	/// A* over the walkable grid of a Terrain.
	/// Steps cost more near walls (low clearance), so paths prefer the middle of corridors.
	/// The search arrays are re-used across searches, and only re-allocated when the area size changes.
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

		private static readonly int[] DX = { 0, 1, 1, 1, 0, -1, -1, -1 };
		private static readonly int[] DY = { 1, 1, 0, -1, -1, -1, 0, 1 };
		private const float Sqrt2 = 1.41421356f;

		// per-cell search state, indexed by y * width + x
		private float[] g;
		private byte[] from; // the direction (0..7) we came from
		private int[] stamp; // == open for this search when 2*gen, closed when 2*gen + 1
		private int gen = 0;
		private int width, height;
		private readonly PriorityQueue<int, float> open = new PriorityQueue<int, float>();

		private void Allocate(Terrain terrain) {
			if ( g != null && width == terrain.Width && height == terrain.Height ) return;
			width = terrain.Width;
			height = terrain.Height;
			int n = width * height;
			g = new float[n];
			from = new byte[n];
			stamp = new int[n];
			gen = 0;
		}

		private float StepPenalty(int clearance) => 1f + (WallPenalty * (5 - Math.Min(5, clearance)) / 4f);

		private static float Octile(int dx, int dy) {
			dx = Math.Abs(dx);
			dy = Math.Abs(dy);
			return dx > dy ? dx + ((Sqrt2 - 1) * dy) : dy + ((Sqrt2 - 1) * dx);
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
			Allocate(terrain);
			var timer = Stopwatch.StartNew();

			gen += 1;
			if ( gen >= int.MaxValue / 2 ) { // wrapped; reset all the stamps
				Array.Clear(stamp, 0, stamp.Length);
				gen = 1;
			}
			int openStamp = 2 * gen, closedStamp = (2 * gen) + 1;
			int startIdx = (start.Y * width) + start.X;
			int goalIdx = (goal.Y * width) + goal.X;
			open.Clear();
			g[startIdx] = 0;
			stamp[startIdx] = openStamp;
			open.Enqueue(startIdx, Octile(goal.X - start.X, goal.Y - start.Y));

			int expanded = 0;
			bool found = false;
			while ( open.TryDequeue(out int idx, out _) ) {
				if ( stamp[idx] == closedStamp ) continue; // a stale duplicate entry
				stamp[idx] = closedStamp;
				if ( idx == goalIdx ) { found = true; break; }
				if ( ++expanded > MaxExpanded ) break;
				int x = idx % width, y = idx / width;
				float gHere = g[idx];
				for ( int dir = 0; dir < 8; dir++ ) {
					int nx = x + DX[dir], ny = y + DY[dir];
					int clearance = terrain.Clearance(nx, ny);
					if ( clearance == 0 ) continue;
					bool diagonal = (dir & 1) == 1;
					// don't cut corners: both orthogonal neighbors must be walkable too
					if ( diagonal && !(terrain.IsWalkable(nx, y) && terrain.IsWalkable(x, ny)) ) continue;
					int nIdx = (ny * width) + nx;
					if ( stamp[nIdx] == closedStamp ) continue;
					float gNext = gHere + ((diagonal ? Sqrt2 : 1f) * StepPenalty(clearance));
					if ( stamp[nIdx] == openStamp && gNext >= g[nIdx] ) continue;
					g[nIdx] = gNext;
					from[nIdx] = (byte)dir;
					stamp[nIdx] = openStamp;
					open.Enqueue(nIdx, gNext + Octile(goal.X - nx, goal.Y - ny));
				}
			}
			open.Clear();
			if ( !found ) return null;

			var cells = new List<Offsets.Vector2i>();
			for ( int idx = goalIdx; idx != startIdx; ) {
				int x = idx % width, y = idx / width;
				cells.Add(new Offsets.Vector2i(x, y));
				int dir = from[idx];
				idx = ((y - DY[dir]) * width) + (x - DX[dir]);
			}
			cells.Add(start);
			cells.Reverse();
			return new NavPath() {
				Cells = cells,
				Waypoints = Smooth(terrain, cells),
				Start = start,
				Goal = goal,
				Cost = g[goalIdx],
				Expanded = expanded,
				ElapsedMs = timer.Elapsed.TotalMilliseconds,
			};
		}

		/// <summary>
		/// Reduce a cell path to waypoints: from each waypoint, skip ahead to the furthest cell
		/// that can be reached in a straight line without dropping below SmoothClearance
		/// (or below the clearance of the cells being skipped, where the path itself is that tight).
		/// </summary>
		public List<Offsets.Vector2i> Smooth(Terrain terrain, List<Offsets.Vector2i> cells) {
			var result = new List<Offsets.Vector2i>();
			if ( cells == null || cells.Count == 0 ) return result;
			int i = 0;
			result.Add(cells[0]);
			while ( i < cells.Count - 1 ) {
				int minClearance = SmoothClearance;
				int next = i + 1;
				int last = Math.Min(cells.Count - 1, i + MaxLeg);
				for ( int j = i + 1; j <= last; j++ ) {
					minClearance = Math.Min(minClearance, terrain.Clearance(cells[j]));
					if ( terrain.IsLineClear(cells[i], cells[j], Math.Max(1, minClearance)) ) {
						next = j;
					}
				}
				result.Add(cells[next]);
				i = next;
			}
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
