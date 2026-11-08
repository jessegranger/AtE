using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A monster that can be fought: alive, targetable and hostile.
	/// </summary>
	public class Hostile {
		public Entity Entity;
		public Offsets.Vector2i GridPos;
		public Vector3 WorldPos;
		public Offsets.MonsterRarity Rarity;
		/// <summary>Straight-line distance from where GetHostiles was asked, in cells.</summary>
		public float Distance;
		public override string ToString() => $"{Entity?.Path?.Split('/').LastOrDefault()} ({Rarity}) at {GridPos}";
	}

	/// <summary>
	/// The building blocks for combat drivers: who to fight, where to stand, and how to get there.
	/// Positions are grid cells (Positioned.GridPos).
	/// </summary>
	public static class Combat {

		/// <summary>
		/// The hostile monsters within radius cells of from, nearest first.
		/// </summary>
		public static List<Hostile> GetHostiles(Offsets.Vector2i from, float radius) {
			var result = new List<Hostile>();
			foreach ( var ent in GetEnemies() ) {
				if ( !IsAlive(ent) || !IsTargetable(ent) ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				var grid = pos.GridPos;
				float d = Distance(from, grid);
				if ( d > radius ) continue;
				result.Add(new Hostile() {
					Entity = ent,
					GridPos = grid,
					WorldPos = Position(ent),
					Rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White,
					Distance = d,
				});
			}
			result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			return result;
		}

		public static float Distance(Offsets.Vector2i a, Offsets.Vector2i b) {
			float dx = a.X - b.X, dy = a.Y - b.Y;
			return MathF.Sqrt((dx * dx) + (dy * dy));
		}

		/// <summary>
		/// Can a projectile fly from a to b (see Terrain.HasLineOfSight).
		/// </summary>
		public static bool HasLineOfSight(Terrain terrain, Offsets.Vector2i a, Offsets.Vector2i b) =>
			terrain != null && terrain.HasLineOfSight(a, b);

		/// <summary>
		/// The nearest cell (by walking, from from) that is within range of target and, if needsLineOfSight,
		/// has line of sight to it. Searches at most maxCells cells. If from already qualifies, that is the answer.
		/// </summary>
		public static bool FindAttackSpot(Terrain terrain, Offsets.Vector2i from, Offsets.Vector2i target, float range,
			bool needsLineOfSight, out Offsets.Vector2i spot, int maxCells = 20000) =>
			Nearest(terrain, from, c => Distance(c, target) <= range && (!needsLineOfSight || terrain.HasLineOfSight(c, target)),
				out spot, maxCells);

		/// <summary>
		/// The nearest cell (by walking, from from) that is at least safeDistance from all the threats, with some room around it.
		/// Searches at most maxCells cells.
		/// </summary>
		public static bool NearestSafeSpot(Terrain terrain, Offsets.Vector2i from, IEnumerable<Offsets.Vector2i> threats, float safeDistance,
			out Offsets.Vector2i spot, int maxCells = 20000) {
			var list = threats.ToList();
			return Nearest(terrain, from, c => terrain.Clearance(c) >= 2 && list.All(t => Distance(c, t) >= safeDistance), out spot, maxCells);
		}

		/// <summary>
		/// Walk the player to goal (with DefaultFollower, on behalf of owner). See PathFollower.
		/// </summary>
		public static void PathTo(Offsets.Vector2i goal, object owner) => DefaultFollower.SetGoal(goal, owner);

		// breadth-first over walkable cells (8 directions, no corner cutting), so the first match is about the nearest by walking
		private static bool Nearest(Terrain terrain, Offsets.Vector2i from, Func<Offsets.Vector2i, bool> wanted, out Offsets.Vector2i spot, int maxCells) {
			spot = from;
			if ( terrain == null || !terrain.IsLoaded ) return false;
			if ( terrain.IsWalkable(from) && wanted(from) ) return true;
			var seen = new HashSet<long>() { from.Id };
			var queue = new Queue<Offsets.Vector2i>();
			queue.Enqueue(from);
			while ( queue.Count > 0 && seen.Count < maxCells ) {
				var c = queue.Dequeue();
				for ( int dy = -1; dy <= 1; dy++ ) {
					for ( int dx = -1; dx <= 1; dx++ ) {
						if ( dx == 0 && dy == 0 ) continue;
						var n = new Offsets.Vector2i(c.X + dx, c.Y + dy);
						if ( !terrain.IsWalkable(n) || !seen.Add(n.Id) ) continue;
						if ( dx != 0 && dy != 0 && !(terrain.IsWalkable(c.X + dx, c.Y) && terrain.IsWalkable(c.X, c.Y + dy)) ) continue;
						if ( wanted(n) ) {
							spot = n;
							return true;
						}
						queue.Enqueue(n);
					}
				}
			}
			return false;
		}
	}
}
