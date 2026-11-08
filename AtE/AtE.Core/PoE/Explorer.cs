using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	public enum ExploreTargetKind { None, Explore, Monster }

	/// <summary>
	/// Where to go next while clearing an area.
	/// </summary>
	public class ExploreTarget {
		public ExploreTargetKind Kind;
		public Offsets.Vector2i GridPos;
		/// <summary>Monster: the monster to go fight.</summary>
		public Entity Monster;
		/// <summary>The approximate travel cost to get there (Explore), or the straight distance (Monster).</summary>
		public float Cost;
		public double ElapsedMs;
		public override string ToString() => Kind == ExploreTargetKind.Monster ? $"monster {Monster?.Id} at {GridPos}"
			: Kind == ExploreTargetKind.Explore ? $"explore {GridPos} (cost {Cost:F0})" : "nothing left";
	}

	/// <summary>
	/// Tracks which walkable cells of an area the player has been near (within RevealRadius),
	/// and picks where to go next to see the rest of it.
	///
	/// This is our own record, not the game's minimap: it only covers what was seen while the overlay was running.
	/// It is kept per area instance (by area hash), so a trip to town and back keeps it.
	/// </summary>
	public class Explorer {

		/// <summary>Walkable cells within this many cells of the player count as explored.</summary>
		public int RevealRadius = 45;
		/// <summary>A region (one connected part of an 8x8 block) is worth visiting if it has this many unexplored cells (or half its cells, if smaller).</summary>
		public int MinUnexplored = 16;
		/// <summary>Hostile monsters within this many cells (straight line) are targets before exploring.</summary>
		public int MonsterRadius = 60;

		private class Map {
			public int Width, Height, TerrainGeneration;
			public ulong[] Bits; // one bit per cell, row-major
			public int Walkable, Explored;
			public int BlocksX; // per 8x8 block of cells (the Navigator's blocks), how many walkable cells are not explored yet
			public int[] BlockUnexplored;
			public Offsets.Vector2i LastMarked = new Offsets.Vector2i(int.MinValue / 2, int.MinValue / 2);
			public long LastUsed;
		}
		private readonly Dictionary<uint, Map> maps = new Dictionary<uint, Map>();
		private Map current;
		private uint currentArea;

		/// <summary>The fraction (0..1) of the area's walkable cells that have been explored.</summary>
		public float ExploredFraction => current == null || current.Walkable == 0 ? 0 : (float)current.Explored / current.Walkable;

		public bool IsExplored(int x, int y) {
			var m = current;
			if ( m == null || x < 0 || y < 0 || x >= m.Width || y >= m.Height ) return false;
			int i = (y * m.Width) + x;
			return (m.Bits[i >> 6] & (1UL << (i & 63))) != 0;
		}
		public bool IsExplored(Offsets.Vector2i pos) => IsExplored(pos.X, pos.Y);

		/// <summary>Forget what has been explored in the current area.</summary>
		public void Reset() {
			if ( current == null ) return;
			maps.Remove(currentArea);
			current = null; // the next Update starts a new map
		}

		/// <summary>
		/// Mark the cells around the player as explored. Cheap when the player has not moved much; call every frame.
		/// </summary>
		public void Update(Terrain terrain, uint areaHash, Offsets.Vector2i player) {
			if ( terrain == null || !terrain.IsLoaded ) return;
			if ( current == null || currentArea != areaHash || current.Width != terrain.Width || current.Height != terrain.Height ) {
				Select(terrain, areaHash);
			}
			current.LastUsed = Time.ElapsedMilliseconds;
			int dx0 = player.X - current.LastMarked.X, dy0 = player.Y - current.LastMarked.Y;
			if ( (dx0 * dx0) + (dy0 * dy0) < 9 ) return; // moved less than 3 cells
			current.LastMarked = player;
			int r = RevealRadius;
			for ( int dy = -r; dy <= r; dy++ ) {
				int y = player.Y + dy;
				if ( y < 0 || y >= current.Height ) continue;
				int span = (int)Math.Sqrt((r * r) - (dy * dy));
				int xMin = Math.Max(0, player.X - span), xMax = Math.Min(current.Width - 1, player.X + span);
				for ( int x = xMin; x <= xMax; x++ ) {
					int i = (y * current.Width) + x;
					ulong bit = 1UL << (i & 63);
					if ( (current.Bits[i >> 6] & bit) != 0 || !terrain.IsWalkable(x, y) ) continue;
					current.Bits[i >> 6] |= bit;
					current.Explored += 1;
					current.BlockUnexplored[((y >> 3) * current.BlocksX) + (x >> 3)] -= 1;
				}
			}
		}

		private void Select(Terrain terrain, uint areaHash) {
			currentArea = areaHash;
			if ( !maps.TryGetValue(areaHash, out current) || current.Width != terrain.Width || current.Height != terrain.Height ) {
				current = new Map() {
					Width = terrain.Width,
					Height = terrain.Height,
					TerrainGeneration = terrain.Generation,
					Bits = new ulong[((terrain.Width * terrain.Height) + 63) / 64],
					BlocksX = (terrain.Width + 7) >> 3,
				};
				current.BlockUnexplored = new int[current.BlocksX * ((terrain.Height + 7) >> 3)];
				for ( int y = 0; y < terrain.Height; y++ ) {
					for ( int x = 0; x < terrain.Width; x++ ) {
						if ( !terrain.IsWalkable(x, y) ) continue;
						current.Walkable += 1;
						current.BlockUnexplored[((y >> 3) * current.BlocksX) + (x >> 3)] += 1;
					}
				}
				maps[areaHash] = current;
				// keep a few recent areas (eg, the map and its town), forget the rest
				while ( maps.Count > 6 ) maps.Remove(maps.OrderBy(kv => kv.Value.LastUsed).First().Key);
			}
		}

		/// <summary>
		/// Where to go next: the nearest reachable hostile monster within MonsterRadius,
		/// else the nearest (by travel) region with enough unexplored cells, else None.
		/// </summary>
		public ExploreTarget FindNextTarget(Navigator nav, Terrain terrain, Offsets.Vector2i player, bool monsters = true) {
			var timer = System.Diagnostics.Stopwatch.StartNew();
			var result = new ExploreTarget() { Kind = ExploreTargetKind.None };
			if ( monsters ) {
				float best = MonsterRadius;
				foreach ( var ent in GetEnemies() ) {
					if ( !IsAlive(ent) || !IsTargetable(ent) ) continue;
					var pos = ent.GetComponent<Positioned>();
					if ( !IsValid(pos) ) continue;
					var grid = pos.GridPos;
					float d = MathF.Sqrt(((grid.X - player.X) * (grid.X - player.X)) + ((grid.Y - player.Y) * (grid.Y - player.Y)));
					if ( d < best && nav.IsReachable(terrain, player, grid) ) {
						best = d;
						result = new ExploreTarget() { Kind = ExploreTargetKind.Monster, GridPos = grid, Monster = ent, Cost = d };
					}
				}
			}
			if ( result.Kind == ExploreTargetKind.None && current != null ) {
				int Needed(int regionSize) => Math.Min(MinUnexplored, Math.Max(1, regionSize / 2));
				bool Wanted(List<Offsets.Vector2i> cells) {
					int unexplored = 0;
					foreach ( var c in cells ) if ( !IsExplored(c) ) unexplored += 1;
					return unexplored >= Needed(cells.Count);
				}
				// skip a region without looking at its cells when its whole block has too few unexplored ones
				var map = current;
				bool MightWant(int bx, int by, int regionSize) => map.BlockUnexplored[(by * map.BlocksX) + bx] >= Needed(regionSize);
				if ( nav.FindNearestRegion(terrain, player, Wanted, out var target, out float cost, MightWant) ) {
					result = new ExploreTarget() { Kind = ExploreTargetKind.Explore, GridPos = target, Cost = cost };
				}
			}
			result.ElapsedMs = timer.Elapsed.TotalMilliseconds;
			return result;
		}
	}

	public static partial class Globals {
		/// <summary>
		/// Tracks what the player has explored in each area; updated every frame by the InGameState.
		/// </summary>
		public static readonly Explorer DefaultExplorer = new Explorer();
	}
}
