using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A damaging area on the ground: its center, and radius in cells; or, for a sector (eg, a breath), its apex
	/// (Center), length (Radius), the direction the caster faces (Dir, normalized), and the slice it covers: from From to
	/// To radians off Dir, positive toward the caster's left; or, for a beam, a band Radius cells either side of the
	/// line from Center to End. Weight (0: DangerMap.DefaultWeight) and Source (a name, eg the catalog entry it came from)
	/// say how it goes on the danger grid.
	/// </summary>
	public readonly struct Hazard {
		public readonly Offsets.Vector2i Center;
		public readonly float Radius;
		public readonly Vector2 Dir;
		public readonly float From, To;
		public readonly bool IsSector;
		public readonly Offsets.Vector2i End;
		public readonly bool IsBeam;
		/// <summary>A ring: the cells from Inner to Radius cells from Center (not the middle).</summary>
		public readonly bool IsRing;
		public readonly float Inner;
		/// <summary>How much it adds to each cell it covers (half that in the Margin ring around it); 0 means DangerMap.DefaultWeight.</summary>
		public byte Weight { get; init; }
		/// <summary>What it is (eg, the catalog entry), for the map's debugging; hazards of one Source don't stack (see DangerMap).</summary>
		public string Source { get; init; }
		public Hazard(Offsets.Vector2i center, float radius) : this() { Center = center; Radius = radius; End = center; }
		public Hazard(Offsets.Vector2i apex, float length, Vector2 facing, float from, float to) : this() {
			Center = apex; Radius = length; Dir = facing.LengthSquared() > 0 ? Vector2.Normalize(facing) : new Vector2(0, -1);
			From = from; To = to; IsSector = true; End = apex;
		}
		/// <summary>A beam: the cells within halfWidth of the line from start to end.</summary>
		public Hazard(Offsets.Vector2i start, Offsets.Vector2i end, float halfWidth) : this() {
			Center = start; End = end; Radius = halfWidth; IsBeam = true;
		}
		/// <summary>A ring: the cells from inner to outer cells from center.</summary>
		public Hazard(Offsets.Vector2i center, float inner, float outer, bool ring) : this() {
			Center = center; End = center; Radius = outer; Inner = Math.Max(0, inner); IsRing = ring;
		}
		public byte EffectiveWeight => Weight == 0 ? DangerMap.DefaultWeight : Weight;
		/// <summary>The caster's left, when it faces Dir (Dir (0,-1): (1,0); see AvoidSpam.HazardSkills).</summary>
		public Vector2 Left => new Vector2(-Dir.Y, Dir.X);
		/// <summary>Cells from cell to the area's edge (negative inside); for a sector, to its circle's edge.</summary>
		public float EdgeDistance(Offsets.Vector2i cell) {
			if ( IsBeam ) return BeamDistance(cell) - Radius;
			float d = Combat.Distance(cell, Center);
			return IsRing && d < Inner ? Inner - d : d - Radius;
		}
		private float BeamDistance(Offsets.Vector2i cell) {
			var a = new Vector2(Center.X, Center.Y);
			var ab = new Vector2(End.X, End.Y) - a;
			var p = new Vector2(cell.X, cell.Y);
			float len2 = ab.LengthSquared();
			float t = len2 == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
			return Vector2.Distance(p, a + t * ab);
		}
		public bool Covers(Offsets.Vector2i cell, float margin) {
			if ( IsBeam ) return BeamDistance(cell) <= Radius + margin;
			float d = Combat.Distance(cell, Center);
			if ( d > Radius + margin ) return false;
			if ( IsRing ) return d >= Inner - margin;
			if ( !IsSector || d <= margin ) return true;
			var v = new Vector2(cell.X - Center.X, cell.Y - Center.Y);
			double angle = Math.Atan2(Vector2.Dot(v, Left), Vector2.Dot(v, Dir));
			// (the margin widens the slice by the angle it makes at this distance)
			double widen = Math.Atan2(margin, d);
			return angle >= From - widen && angle <= To + widen;
		}
		/// <summary>2 if it covers the cell (x, y), 1 if only its margin does (Covers with margin, not with 0), else 0: one test, for DangerMap.Build.</summary>
		public int Level(int x, int y, float margin) {
			float px = x - Center.X, py = y - Center.Y;
			float outer = Radius + margin;
			if ( IsBeam ) {
				float ax = End.X - Center.X, ay = End.Y - Center.Y;
				float len2 = (ax * ax) + (ay * ay);
				float t = len2 == 0 ? 0 : Math.Clamp(((px * ax) + (py * ay)) / len2, 0, 1);
				float dx = px - (t * ax), dy = py - (t * ay);
				float b2 = (dx * dx) + (dy * dy);
				return b2 <= Radius * Radius ? 2 : b2 <= outer * outer ? 1 : 0;
			}
			float d2 = (px * px) + (py * py);
			if ( d2 > outer * outer ) return 0;
			if ( IsRing ) {
				float innerEdge = Inner - margin;
				if ( innerEdge > 0 && d2 < innerEdge * innerEdge ) return 0;
				return d2 <= Radius * Radius && d2 >= Inner * Inner ? 2 : 1;
			}
			if ( !IsSector ) return d2 <= Radius * Radius ? 2 : 1;
			if ( d2 == 0 ) return 2;
			float d = MathF.Sqrt(d2);
			double angle = Math.Atan2((px * -Dir.Y) + (py * Dir.X), (px * Dir.X) + (py * Dir.Y)); // (off Dir, toward Left)
			if ( d <= Radius && angle >= From && angle <= To ) return 2;
			if ( d <= margin ) return 1;
			double widen = Math.Atan2(margin, d);
			return angle >= From - widen && angle <= To + widen ? 1 : 0;
		}
		/// <summary>The box of cells it may cover, margin included.</summary>
		public (int x0, int y0, int x1, int y1) Bounds(float margin) {
			float r = Radius + margin;
			return ((int)Math.Floor(Math.Min(Center.X, End.X) - r), (int)Math.Floor(Math.Min(Center.Y, End.Y) - r),
				(int)Math.Ceiling(Math.Max(Center.X, End.X) + r), (int)Math.Ceiling(Math.Max(Center.Y, End.Y) + r));
		}
		public string Shape => IsBeam ? $"beam {Center} to {End}, {Radius:F0} either side"
			: IsSector ? $"{Center} sector {Radius:F0} facing ({Dir.X:F2}, {Dir.Y:F2}), {From * 180 / Math.PI:F0} to {To * 180 / Math.PI:F0} deg"
			: IsRing ? $"{Center} ring r{Inner:F0}-{Radius:F0}"
			: $"{Center} r{Radius:F0}";
		public override string ToString() => $"{Source ?? "?"} {EffectiveWeight}: {Shape}";
	}

	/// <summary>
	/// How dangerous each cell near the player is: the danger grid, one byte per cell (Danger) over a square window of
	/// the grid centered on the player (HalfSize cells each way). Built (see Get) at most every RebuildMs from
	/// the hazards AvoidSpam knows (AvoidSpam.CatalogHazards) plus any a fight adds (SetExtra).
	///
	/// Each cell starts from the terrain: Blocked (0xFF) where it can't be walked, and else a little for being near a
	/// wall (WallWeight per step of clearance below 5: walls are where projectiles burst). Then each hazard adds its
	/// Weight to the cells it covers, and half that to the Margin ring around it (so a search is pushed away from edges).
	/// A hazard that hurts now (burning ground, a beam) has a high weight; one about to land or go off (a breath being
	/// cast, a bomb) a low one (AvoidSpam.WarningWeight: don't stop in it, but crossing it is fine).
	/// Hazards of the same Source don't stack (the most of them counts: eg, a network of overlapping beams is
	/// one danger), different ones do, saturating at Max. A cell is safe below SafeLevel (the walls alone stay under it).
	///
	/// Main thread only (hazards are read from entities). The maps are double-buffered: one from Get stays as it is until
	/// the second Get after it.
	/// </summary>
	public sealed class DangerMap {
		public const byte Blocked = 0xFF;
		public const byte Max = 0xFE;

		/// <summary>The window: this many cells each way from the player.</summary>
		public static int HalfSize = 160;
		/// <summary>Build a new map at most this often (the hazards are read from entities each build).</summary>
		public static long RebuildMs = 50;
		/// <summary>The weight of a hazard that gives none.</summary>
		public static byte DefaultWeight = 30;
		/// <summary>A cell below this is safe.</summary>
		public static byte SafeLevel = 10;
		/// <summary>A path crossing a cell with at least this much danger gets a SkipDanger step (a travel skill over it).</summary>
		public static byte SkipLevel = 30;
		/// <summary>The ring around each hazard (in cells) that gets half its weight.</summary>
		public static float Margin = 4;
		/// <summary>Per step of clearance below 5 (Terrain.Clearance), added to a walkable cell.</summary>
		public static int WallWeight = 1;
		/// <summary>A fight's extra hazards (SetExtra) not set again for this long are dropped.</summary>
		public static long ExtraExpireMs = 1000;

		public readonly int Size;
		/// <summary>The danger grid: each cell's danger, or Blocked; row by row, Size across (see DangerAt).</summary>
		public readonly byte[] Danger;
		/// <summary>The grid cell at Danger[0] (the window's top left).</summary>
		public int OriginX { get; private set; }
		public int OriginY { get; private set; }
		/// <summary>The player's cell when it was built.</summary>
		public Offsets.Vector2i Center { get; private set; }
		public IReadOnlyList<Hazard> Hazards { get; private set; } = Array.Empty<Hazard>();
		/// <summary>When it was built (Time.ElapsedMilliseconds), and how many maps were built before it.</summary>
		public long BuiltAt { get; private set; }
		public int Number { get; private set; }
		/// <summary>The Terrain.Generation it was built on.</summary>
		public int TerrainGeneration { get; private set; }
		/// <summary>How long it took to read the hazards, and to put them on the map.</summary>
		public double CollectMs { get; private set; }
		public double StampMs { get; private set; }

		private readonly byte[] scratch;

		public DangerMap(int halfSize) {
			Size = (2 * halfSize) + 1;
			Danger = new byte[Size * Size];
			scratch = new byte[Size * Size];
		}

		// ---- the current map ----

		private static DangerMap current, spare;
		private static int built;
		private static readonly Dictionary<object, (Func<IEnumerable<Hazard>> hazards, long at)> extras = new Dictionary<object, (Func<IEnumerable<Hazard>>, long)>();
		private static string lastError;

		/// <summary>The last map built (null: none yet); may be old, see Get.</summary>
		public static DangerMap Current => current;

		/// <summary>Add owner's hazards (eg, a fight's own) to the maps built in the next ExtraExpireMs; null hazards removes them.</summary>
		public static void SetExtra(object owner, Func<IEnumerable<Hazard>> hazards) {
			if ( hazards == null ) extras.Remove(owner);
			else extras[owner] = (hazards, Time.ElapsedMilliseconds);
		}

		/// <summary>The map around the player now: Current, or a new one if that is older than RebuildMs. Null with no terrain or player.</summary>
		public static DangerMap Get() {
			long now = Time.ElapsedMilliseconds;
			var terrain = GetTerrain();
			if ( terrain == null || !terrain.IsLoaded ) return null;
			if ( current != null && current.TerrainGeneration == terrain.Generation && now - current.BuiltAt < RebuildMs ) return current;
			var pos = GetPlayer()?.GetComponent<Positioned>();
			if ( !IsValid(pos) ) return current?.TerrainGeneration == terrain.Generation ? current : null;

			var timer = Stopwatch.StartNew();
			var hazards = new List<Hazard>();
			try {
				hazards.AddRange(AvoidSpam.CatalogHazards());
				foreach ( var owner in extras.Keys.ToList() ) {
					var (more, at) = extras[owner];
					if ( now - at > ExtraExpireMs ) { extras.Remove(owner); continue; }
					hazards.AddRange(more());
				}
				lastError = null;
			} catch ( Exception e ) {
				if ( lastError != e.Message ) Log($"DangerMap: reading the hazards failed: {e.Message}");
				lastError = e.Message;
			}
			double collectMs = timer.Elapsed.TotalMilliseconds;

			var map = spare != null && spare.Size == (2 * HalfSize) + 1 ? spare : new DangerMap(HalfSize);
			map.Build(terrain, pos.GridPos, hazards);
			map.CollectMs = collectMs;
			map.BuiltAt = now;
			map.Number = ++built;
			spare = current;
			current = map;
			return map;
		}

		// ---- building ----

		// the terrain's cells as they start each map (Blocked, or the wall weight), for the whole area
		private static byte[] seed;
		private static Terrain seedTerrain;
		private static int seedWidth, seedHeight, seedGeneration = -1, seedDoorGeneration = -1, seedWallWeight = -1;

		private static byte[] Seed(Terrain terrain) {
			if ( seed != null && seedTerrain == terrain && seedGeneration == terrain.Generation && seedDoorGeneration == terrain.DoorGeneration && seedWallWeight == WallWeight ) return seed;
			seedWidth = terrain.Width;
			seedHeight = terrain.Height;
			if ( seed == null || seed.Length != seedWidth * seedHeight ) seed = new byte[seedWidth * seedHeight];
			terrain.UnpackClearance(seed, 0, seedWidth);
			for ( int i = 0; i < seed.Length; i++ ) {
				int c = seed[i];
				seed[i] = c == 0 ? Blocked : (byte)Math.Min(SafeLevel - 1, Math.Max(0, 5 - c) * WallWeight);
			}
			seedTerrain = terrain;
			seedGeneration = terrain.Generation;
			seedDoorGeneration = terrain.DoorGeneration;
			seedWallWeight = WallWeight;
			return seed;
		}

		/// <summary>Build this map around center, on terrain, from hazards (see the class summary). Public for tests.</summary>
		public void Build(Terrain terrain, Offsets.Vector2i center, IEnumerable<Hazard> hazards) {
			var timer = Stopwatch.StartNew();
			int half = Size / 2;
			Center = center;
			OriginX = center.X - half;
			OriginY = center.Y - half;
			TerrainGeneration = terrain.Generation;
			var source = Seed(terrain);
			// the terrain, Blocked outside the area
			for ( int wy = 0; wy < Size; wy++ ) {
				int y = OriginY + wy, row = wy * Size;
				if ( y < 0 || y >= seedHeight ) { Array.Fill(Danger, Blocked, row, Size); continue; }
				int x0 = Math.Max(0, OriginX), x1 = Math.Min(seedWidth - 1, OriginX + Size - 1);
				if ( x1 < x0 ) { Array.Fill(Danger, Blocked, row, Size); continue; }
				if ( x0 > OriginX ) Array.Fill(Danger, Blocked, row, x0 - OriginX);
				Buffer.BlockCopy(source, (y * seedWidth) + x0, Danger, row + (x0 - OriginX), x1 - x0 + 1);
				if ( x1 < OriginX + Size - 1 ) Array.Fill(Danger, Blocked, row + (x1 - OriginX) + 1, OriginX + Size - 1 - x1);
			}

			// each Source's hazards are drawn into scratch, keeping the most at each cell, then added on
			var list = hazards.ToList();
			Hazards = list;
			foreach ( var group in list.GroupBy(h => h.Source) ) {
				int gx0 = int.MaxValue, gy0 = int.MaxValue, gx1 = int.MinValue, gy1 = int.MinValue;
				foreach ( var h in group ) {
					var (bx0, by0, bx1, by1) = h.Bounds(Margin);
					bx0 = Math.Max(bx0 - OriginX, 0); by0 = Math.Max(by0 - OriginY, 0);
					bx1 = Math.Min(bx1 - OriginX, Size - 1); by1 = Math.Min(by1 - OriginY, Size - 1);
					if ( bx1 < bx0 || by1 < by0 ) continue;
					gx0 = Math.Min(gx0, bx0); gy0 = Math.Min(gy0, by0); gx1 = Math.Max(gx1, bx1); gy1 = Math.Max(gy1, by1);
					byte full = h.EffectiveWeight, edge = (byte)(full / 2);
					for ( int wy = by0; wy <= by1; wy++ ) {
						int row = wy * Size;
						for ( int wx = bx0; wx <= bx1; wx++ ) {
							if ( Danger[row + wx] == Blocked ) continue;
							int level = h.Level(OriginX + wx, OriginY + wy, Margin);
							if ( level == 0 ) continue;
							byte v = level == 2 ? full : edge;
							if ( v > scratch[row + wx] ) scratch[row + wx] = v;
						}
					}
				}
				if ( gx0 == int.MaxValue ) continue;
				for ( int wy = gy0; wy <= gy1; wy++ ) {
					int row = wy * Size;
					for ( int i = row + gx0; i <= row + gx1; i++ ) {
						byte v = scratch[i];
						if ( v == 0 ) continue;
						scratch[i] = 0;
						Danger[i] = (byte)Math.Min(Max, Danger[i] + v);
					}
				}
			}
			StampMs = timer.Elapsed.TotalMilliseconds;
		}

		// ---- reading ----

		public bool InWindow(int x, int y) => x >= OriginX && y >= OriginY && x < OriginX + Size && y < OriginY + Size;
		private int Index(int x, int y) => ((y - OriginY) * Size) + (x - OriginX);
		/// <summary>The danger at a cell (Blocked outside the window, or where it can't be walked).</summary>
		public byte DangerAt(Offsets.Vector2i c) => InWindow(c.X, c.Y) ? Danger[Index(c.X, c.Y)] : Blocked;
		/// <summary>
		/// The hazards' danger at a cell, from the hazards themselves (as Build adds them up, without the walls): for a
		/// cell the grid blocks (eg, the player's own, near a wall).
		/// </summary>
		public int HazardDangerAt(Offsets.Vector2i c) => Math.Min(Max, Hazards.GroupBy(h => h.Source)
			.Sum(g => g.Max(h => h.Level(c.X, c.Y, Margin) switch { 2 => h.EffectiveWeight, 1 => h.EffectiveWeight / 2, _ => 0 })));
		/// <summary>The walls' share of a cell's danger (the terrain seed: 0 in the open, up to 4 * WallWeight by a wall; 0 if blocked).</summary>
		public static byte WallAt(Offsets.Vector2i c) {
			var s = seed;
			if ( s == null || c.X < 0 || c.Y < 0 || c.X >= seedWidth || c.Y >= seedHeight ) return 0;
			byte v = s[(c.Y * seedWidth) + c.X];
			return v == Blocked ? (byte)0 : v;
		}
		public bool IsBlocked(Offsets.Vector2i c) => DangerAt(c) == Blocked;
		/// <summary>Walkable and below SafeLevel.</summary>
		public bool IsSafe(Offsets.Vector2i c) => DangerAt(c) < SafeLevel;
		/// <summary>The hazards over a cell (or its Margin ring).</summary>
		public IEnumerable<Hazard> HazardsAt(Offsets.Vector2i c) => Hazards.Where(h => h.Covers(c, Margin));

		// ---- the safest place to stand ----

		/// <summary>Walking through a cell costs (1 + its danger / CrossDivisor) per cell walked.</summary>
		public static float CrossDivisor = 10;
		/// <summary>Ending on a cell costs its danger times this, in cells walked.</summary>
		public static float StandWeight = 2;
		/// <summary>Added to the score of a cell that isn't safe (see SafeLevel), or that the caller's good says is not good.</summary>
		public static float UnsafePenalty = 1000, NotGoodPenalty = 500;
		/// <summary>
		/// Ending on a cell also costs ClearWeight (in cells walked) for each unsafe cell (danger at least SafeLevel)
		/// within ClearRadius cells of it (a square), so a cell wedged between two hazards' edges loses to a clear one
		/// a few steps further.
		/// </summary>
		public static int ClearRadius = 3;
		public static float ClearWeight = 1;

		private float[] cost;
		private int[] costStamp;
		private int flood;
		private readonly PriorityQueue<int, float> queue = new PriorityQueue<int, float>();
		private static readonly (int dx, int dy, float len)[] Steps = {
			(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f),
		};

		// how many cells within ClearRadius (a square) of window cell (wx, wy) are unsafe (blocked ones are not)
		private int UnsafeNear(int wx, int wy) {
			int n = 0;
			int x0 = Math.Max(0, wx - ClearRadius), x1 = Math.Min(Size - 1, wx + ClearRadius);
			int y0 = Math.Max(0, wy - ClearRadius), y1 = Math.Min(Size - 1, wy + ClearRadius);
			for ( int y = y0; y <= y1; y++ ) {
				for ( int x = x0; x <= x1; x++ ) {
					int j = (y * Size) + x;
					if ( Danger[j] != Blocked && Danger[j] >= SafeLevel ) n += 1;
				}
			}
			return n;
		}

		/// <summary>
		/// The best cell to stand on that can be walked to from from (within maxDistance cells of it): the one with the
		/// lowest Score, where walking through danger costs more (CrossDivisor), and standing in it too (StandWeight) or
		/// next to it (ClearWeight), and
		/// a cell that is not safe, or not good (eg, out of range of the target), is only taken when there is no better.
		/// One flood out from from (Dijkstra), stopping when no cell further out can score better. True if best is safe
		/// (and false with best default if nothing was reached). Score and CostTo read the flood afterward.
		/// </summary>
		public bool TryFindSafest(Offsets.Vector2i from, Func<Offsets.Vector2i, bool> good, out Offsets.Vector2i best, out float bestScore, float maxDistance = 150) {
			best = default;
			bestScore = float.MaxValue;
			if ( !InWindow(from.X, from.Y) ) return false;
			if ( cost == null ) { cost = new float[Size * Size]; costStamp = new int[Size * Size]; }
			flood += 1;
			queue.Clear();
			int start = Index(from.X, from.Y);
			cost[start] = 0;
			costStamp[start] = flood;
			queue.Enqueue(start, 0);
			float max2 = maxDistance * maxDistance;
			bool bestSafe = false;
			while ( queue.TryDequeue(out int i, out float c) ) {
				if ( c > cost[i] ) continue; // (a stale entry)
				if ( c >= bestScore ) break; // (nothing further can score better: a score is at least its walk)
				int wx = i % Size, wy = i / Size;
				var cell = new Offsets.Vector2i(OriginX + wx, OriginY + wy);
				int total = Danger[i];
				bool safe = total < SafeLevel;
				float score = c + (StandWeight * total) + (safe ? 0 : UnsafePenalty);
				if ( score < bestScore && ClearWeight > 0 ) score += ClearWeight * UnsafeNear(wx, wy);
				if ( score < bestScore && (good == null || good(cell) || (score += NotGoodPenalty) < bestScore) ) {
					bestScore = score;
					best = cell;
					bestSafe = safe;
				}
				foreach ( var (dx, dy, len) in Steps ) {
					int nx = wx + dx, ny = wy + dy;
					if ( nx < 0 || ny < 0 || nx >= Size || ny >= Size ) continue;
					int n = (ny * Size) + nx;
					if ( Danger[n] == Blocked ) continue;
					// (a diagonal step needs both cells it squeezes between)
					if ( dx != 0 && dy != 0 && (Danger[(wy * Size) + nx] == Blocked || Danger[(ny * Size) + wx] == Blocked) ) continue;
					int ddx = OriginX + nx - from.X, ddy = OriginY + ny - from.Y;
					if ( (ddx * ddx) + (ddy * ddy) > max2 ) continue;
					float nc = c + (len * (1 + (Danger[n] / CrossDivisor)));
					if ( costStamp[n] == flood && cost[n] <= nc ) continue;
					costStamp[n] = flood;
					cost[n] = nc;
					queue.Enqueue(n, nc);
				}
			}
			return bestSafe;
		}

		/// <summary>The walking cost to a cell found by the last TryFindSafest (false if it was not reached).</summary>
		public bool CostTo(Offsets.Vector2i c, out float walk) {
			walk = 0;
			if ( cost == null || !InWindow(c.X, c.Y) ) return false;
			int i = Index(c.X, c.Y);
			if ( costStamp[i] != flood ) return false;
			walk = cost[i];
			return true;
		}

		/// <summary>A cell's score (as TryFindSafest scores it) after the last TryFindSafest; float.MaxValue if it was not reached.</summary>
		public float Score(Offsets.Vector2i c, bool good) {
			if ( !CostTo(c, out float walk) ) return float.MaxValue;
			int total = DangerAt(c);
			float clear = ClearWeight > 0 ? ClearWeight * UnsafeNear(c.X - OriginX, c.Y - OriginY) : 0;
			return walk + (StandWeight * total) + (total < SafeLevel ? 0 : UnsafePenalty) + clear + (good ? 0 : NotGoodPenalty);
		}

		// ---- debugging ----

		/// <summary>Describe with rotate: the grid cells across one character.</summary>
		public static float RotatedColumnCells = 0.75f;

		/// <summary>
		/// A text picture of the map r cells each way around at (default: Center), with its hazards: each cell is '#'
		/// blocked, '.' safe, or a digit for the danger in steps of 10% of Max, '@' the center; then the hazards over the middle cell, if cellInfo. Given project (a grid cell to
		/// its screen position, false if it can't), the picture gets a frame with N, E, S and W where the screen's top,
		/// right, bottom and left lie from the middle (the grid is turned on the screen), and a legend beside it; or, if
		/// rotate, the picture is drawn turned to match the screen instead (up is the screen's top; each character is about
		/// RotatedColumnCells across, a line twice that, as characters are about twice as tall as wide).
		/// </summary>
		public string Describe(Offsets.Vector2i? at, int r, bool cellInfo, Func<Offsets.Vector2i, (bool ok, Vector2 screen)> project = null, bool rotate = false) {
			var mid = at ?? Center;
			var sb = new StringBuilder();
			long age = Time.ElapsedMilliseconds - BuiltAt;
			sb.AppendLine($"danger map #{Number}, built {age} ms ago around {Center} (window {OriginX},{OriginY} + {Size}), terrain gen {TerrainGeneration}");
			sb.AppendLine($"read hazards {CollectMs:F2} ms, stamped {StampMs:F2} ms; SafeLevel {SafeLevel}, SkipLevel {SkipLevel}, Margin {Margin}, WallWeight {WallWeight}");
			sb.AppendLine($"{Hazards.Count} hazards:");
			foreach ( var h in Hazards ) sb.AppendLine($"  {h}");
			if ( cellInfo ) {
				sb.AppendLine($"cell {mid}: danger {DangerAt(mid)}{(IsSafe(mid) ? " (safe)" : "")}");
				foreach ( var h in HazardsAt(mid) ) sb.AppendLine($"  under {h}{(h.Covers(mid, 0) ? "" : " (margin)")}");
			}
			char Glyph(Offsets.Vector2i c) {
				if ( c.X == mid.X && c.Y == mid.Y ) return '@';
				int t = DangerAt(c);
				if ( t == Blocked ) return '#';
				if ( t < SafeLevel ) return '.';
				int d = Math.Min(9, t * 10 / Max);
				return (char)('0' + d);
			}
			// the grid per screen pixel along the screen's right and down (from project, at the middle)
			Vector2 perRight = default, perDown = default;
			bool turned = false;
			if ( project != null ) {
				var (ok0, s0) = project(mid);
				var (okx, sx) = project(new Offsets.Vector2i(mid.X + 10, mid.Y));
				var (oky, sy) = project(new Offsets.Vector2i(mid.X, mid.Y + 10));
				Vector2 ex = (sx - s0) / 10, ey = (sy - s0) / 10; // (screen per cell of +X, of +Y)
				float det = (ex.X * ey.Y) - (ex.Y * ey.X);
				if ( ok0 && okx && oky && Math.Abs(det) > 1e-6 ) {
					// the grid (a, b) that goes v on the screen: a * ex + b * ey = v
					Vector2 Grid(Vector2 v) => new Vector2(((v.X * ey.Y) - (v.Y * ey.X)) / det, ((ex.X * v.Y) - (ex.Y * v.X)) / det);
					perRight = Grid(new Vector2(1, 0));
					perDown = Grid(new Vector2(0, 1));
					turned = true;
				}
			}
			if ( rotate && turned ) {
				// each character RotatedColumnCells of grid along the screen's right; each line twice as many pixels down
				float px = RotatedColumnCells / perRight.Length();
				int cols = (int)Math.Ceiling(r / RotatedColumnCells), rows = (int)Math.Ceiling(r / (2 * px * perDown.Length()));
				sb.AppendLine($"turned to the screen (up: the screen's top; a character {RotatedColumnCells} cells across, a line {2 * px * perDown.Length():F1} cells down), about {r} cells around {mid}; # blocked, . safe, 0-9 danger/10%, @ {(at.HasValue ? "the cell" : "the player")}");
				sb.AppendLine("  screen top");
				for ( int v = -rows; v <= rows; v++ ) {
					sb.Append(v == 0 ? "L " : "  ");
					for ( int u = -cols; u <= cols; u++ ) {
						var g = (perRight * (u * px)) + (perDown * (v * 2 * px));
						var c = new Offsets.Vector2i(mid.X + (int)Math.Round(g.X), mid.Y + (int)Math.Round(g.Y));
						sb.Append(u == 0 && v == 0 ? '@' : Glyph(c));
					}
					sb.AppendLine(v == 0 ? " R" : "");
				}
				sb.AppendLine("  screen bottom (L: screen left, R: screen right)");
				return sb.ToString();
			}
			if ( rotate ) sb.AppendLine("(not turned: no camera)");
			sb.AppendLine($"x {mid.X - r}..{mid.X + r} (left to right), y {mid.Y - r}..{mid.Y + r} (top to bottom); # blocked, . safe, 0-9 danger/10%, @ {(at.HasValue ? "the cell" : "the player")}");
			// the frame: a letter where each screen direction leaves the picture
			var frame = new Dictionary<(int dx, int dy), char>();
			var legend = new List<string>();
			if ( turned ) {
				{
					foreach ( var (letter, v, name) in new[] { ('N', -perDown, "top"), ('E', perRight, "right"), ('S', perDown, "bottom"), ('W', -perRight, "left") } ) {
						float a = v.X, b = v.Y;
						float m = Math.Max(Math.Abs(a), Math.Abs(b));
						if ( m < 1e-6 ) continue;
						// where the ray from the middle leaves the frame (one ring outside the picture)
						int fx = (int)Math.Round(a / m * (r + 1)), fy = (int)Math.Round(b / m * (r + 1));
						frame[(fx, fy)] = letter;
						legend.Add($"{letter} = screen {name} (grid {a / m:+0.00;-0.00}, {b / m:+0.00;-0.00})");
					}
				}
			}
			if ( legend.Count == 0 && project != null ) legend.Add("(screen directions unknown: no camera)");
			string Ring(int dy) {
				var row = new StringBuilder("      ");
				for ( int dx = -r - 1; dx <= r + 1; dx++ ) row.Append(frame.TryGetValue((dx, dy), out char ch) ? ch : (dx == -r - 1 || dx == r + 1) ? '+' : '-');
				return row.ToString();
			}
			if ( project != null ) sb.AppendLine(Ring(-r - 1));
			for ( int y = mid.Y - r; y <= mid.Y + r; y++ ) {
				int dy = y - mid.Y;
				sb.Append($"{y,5} ");
				if ( project != null ) sb.Append(frame.TryGetValue((-r - 1, dy), out char left) ? left : '|');
				for ( int x = mid.X - r; x <= mid.X + r; x++ ) {
					sb.Append(Glyph(new Offsets.Vector2i(x, y)));
				}
				if ( project != null ) {
					sb.Append(frame.TryGetValue((r + 1, dy), out char right) ? right : '|');
					int li = dy + r; // (the legend down the right, from the top row)
					if ( li < legend.Count ) sb.Append("   ").Append(legend[li]);
				}
				sb.AppendLine();
			}
			if ( project != null ) {
				sb.AppendLine(Ring(r + 1));
				for ( int li = (2 * r) + 1; li < legend.Count; li++ ) sb.AppendLine(legend[li]); // (a picture too short for it)
			}
			return sb.ToString();
		}
	}
}
