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
	/// Fights that go nowhere (eg, a Necromancer raising skeletons as fast as they die): a watched monster (by the start of
	/// its path, see Rules) that stays within Radius cells of the player for StuckMs without dropping to low life
	/// (LowLife of its life) has its kind ignored for IgnoreMs: every monster whose path starts with one of the rule's
	/// prefixes (the Necromancer and its skeletons), so the player goes on past them. The list is cleared on area change.
	/// </summary>
	public static class Stalemates {
		/// <summary>The monsters to watch (a path prefix), and the path prefixes to ignore when one stalls.</summary>
		public static readonly List<(string watch, string[] ignore)> Rules = new List<(string, string[])> {
			("Metadata/Monsters/Necromancer", new[] { "Metadata/Monsters/Necromancer/", "Metadata/Monsters/Skeletons/" }),
		};
		public static float Radius = 75;
		public static long StuckMs = 30000;
		public static float LowLife = .5f;
		public static long IgnoreMs = 5 * 60000;
		/// <summary>Out of Radius this long, a watched monster's time starts over.</summary>
		public static long GapMs = 2000;

		// path prefix -> ignored until
		private static readonly Dictionary<string, long> ignored = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		// watched monster (by address) -> (first seen near, last seen near)
		private static readonly Dictionary<IntPtr, (long first, long last)> near = new Dictionary<IntPtr, (long, long)>();
		private static bool hooked;

		private static void Hook() {
			if ( hooked ) return;
			hooked = true;
			OnAreaChange += (_, __) => { ignored.Clear(); near.Clear(); ignoredMonsters.Clear(); attacked.Clear(); };
		}

		/// <summary>
		/// A monster attacked for this long (with gaps of at most GapMs) that has lost no life or energy shield can't be
		/// hurt from where it is attacked (eg, behind a palisade that stops projectiles, 3.29.4, a bandit guard in The
		/// Western Forest): it is ignored until the area changes.
		/// </summary>
		public static long UnhurtMs = 8000;
		private static readonly HashSet<IntPtr> ignoredMonsters = new HashSet<IntPtr>();
		// monster attacked (by address) -> (first attacked, last attacked, life + energy shield then)
		private static readonly Dictionary<IntPtr, (long first, long last, int hp)> attacked = new Dictionary<IntPtr, (long, long, int)>();

		/// <summary>Note an attack on ent (see CombatDriver.Engage); one that stays unhurt for UnhurtMs is ignored.</summary>
		public static void NoteAttack(Entity ent) {
			Hook();
			var life = ent?.GetComponent<Life>();
			if ( !IsValid(life) ) return;
			int hp = life.CurHP + life.CurES;
			long now = Time.ElapsedMilliseconds;
			if ( !attacked.TryGetValue(ent.Address, out var t) || now - t.last > GapMs || hp < t.hp ) t = (now, now, hp);
			t.last = now;
			attacked[ent.Address] = t;
			if ( now - t.first < UnhurtMs ) return;
			attacked.Remove(ent.Address);
			ignoredMonsters.Add(ent.Address);
			Log($"Stalemates: {ent.Path} (id {ent.Id}) attacked for {UnhurtMs / 1000} s without losing any life: ignoring it (until the area changes)");
		}

		/// <summary>Is ent ignored now (of a kind ignored, or itself, see NoteAttack)?</summary>
		public static bool IsIgnored(Entity ent) {
			Hook();
			if ( ent != null && ignoredMonsters.Contains(ent.Address) ) return true;
			if ( ignored.Count == 0 || !(ent?.Path is string path) ) return false;
			long now = Time.ElapsedMilliseconds;
			foreach ( var kv in ignored ) {
				if ( kv.Value > now && path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) ) return true;
			}
			return false;
		}

		/// <summary>Note a monster seen distance cells from the player (see GetHostiles); a stalled one starts its kind's ignoring.</summary>
		public static void Watch(Entity ent, float distance) {
			Hook();
			if ( !(ent?.Path is string path) ) return;
			var rule = Rules.FirstOrDefault(r => path.StartsWith(r.watch, StringComparison.OrdinalIgnoreCase));
			if ( rule.watch == null ) return;
			long now = Time.ElapsedMilliseconds;
			if ( distance > Radius ) return;
			if ( !near.TryGetValue(ent.Address, out var t) || now - t.last > GapMs ) t = (now, now);
			t.last = now;
			near[ent.Address] = t;
			if ( now - t.first < StuckMs ) return;
			var life = ent.GetComponent<Life>();
			if ( IsValid(life) && life.MaxHP > 0 && life.CurHP < LowLife * life.MaxHP ) return; // (nearly dead: keep at it)
			foreach ( var prefix in rule.ignore ) ignored[prefix] = now + IgnoreMs;
			near.Remove(ent.Address);
			Log($"Stalemates: {path} has been within {Radius:F0} cells for {StuckMs / 1000} s above {LowLife:P0} life: ignoring {string.Join(", ", rule.ignore)} for {IgnoreMs / 60000} minutes (or until the area changes)");
		}
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
				if ( !IsAlive(ent) || !IsTargetable(ent) || CannotBeDamaged(ent) ) continue;
				if ( Stalemates.IsIgnored(ent) ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				var grid = pos.GridPos;
				float d = Distance(from, grid);
				Stalemates.Watch(ent, d);
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

		/// <summary>
		/// Does ent have the stat CannotBeDamaged = 1 (eg, a monster that is invulnerable for now); not worth attacking.
		/// </summary>
		public static bool CannotBeDamaged(Entity ent) => StatIs(ent, Offsets.GameStat.CannotBeDamaged, 1);

		/// <summary>
		/// Does ent have the stat MonsterInsideMonolith = 1: a monster sealed in a monolith (and so CannotBeDamaged),
		/// which clicking it lets out (see CombatDriver.ClickMonster).
		/// </summary>
		public static bool InsideMonolith(Entity ent) => StatIs(ent, Offsets.GameStat.MonsterInsideMonolith, 1);

		/// <summary>Does ent's Stats component have stat = value.</summary>
		public static bool StatIs(Entity ent, Offsets.GameStat stat, int value) {
			var stats = ent?.GetComponent<Stats>();
			return IsValid(stats) && stats.GetStats().TryGetValue(stat, out int v) && v == value;
		}

		/// <summary>Is ent a rare or unique monster (by its ObjectMagicProperties).</summary>
		public static bool IsRareOrUnique(Entity ent) =>
			(ent?.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White) is Offsets.MonsterRarity.Rare or Offsets.MonsterRarity.Unique;

				public const string AuguryFrozenBuff = "augury_boss_frozen";

		/// <summary>Is ent a unique monster with the augury_boss_frozen buff, which clicking opens the augury card selection window for.</summary>
		public static bool IsFrozenAuguryBoss(Entity ent) =>
			(ent?.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White) == Offsets.MonsterRarity.Unique
			&& HasBuff(ent, AuguryFrozenBuff);

		/// <summary>
		/// Why ent has to be clicked before it can be fought ("inside a monolith", "frozen for an augury"), or null if it
		/// doesn't. Only a rare or unique monster inside a monolith is clicked (the others are left in it).
		/// </summary>
		public static string ClickReason(Entity ent) =>
			InsideMonolith(ent) && IsRareOrUnique(ent) ? "inside a monolith"
			: IsFrozenAuguryBoss(ent) ? "frozen for an augury"
			: null;

		/// <summary>
		/// The living monsters within radius cells of from that have to be clicked before they can be fought (see
		/// ClickReason: inside a monolith, or a frozen augury boss), nearest first.
		/// </summary>
		public static List<Hostile> GetMonstersToClick(Offsets.Vector2i from, float radius) {
			var result = new List<Hostile>();
			foreach ( var ent in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				if ( ent?.Path == null || !ent.Path.StartsWith("Metadata/Monsters/") || !IsAlive(ent) || ClickReason(ent) == null ) continue;
				var h = ToHostile(ent, from);
				if ( h != null && h.Distance <= radius ) result.Add(h);
			}
			result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			return result;
		}

		/// <summary>ent as a Hostile (whether or not it is hostile), with its distance from from; null if it has no position.</summary>
		public static Hostile ToHostile(Entity ent, Offsets.Vector2i from) {
			var pos = ent?.GetComponent<Positioned>();
			if ( !IsValid(pos) ) return null;
			return new Hostile() {
				Entity = ent,
				GridPos = pos.GridPos,
				WorldPos = Position(ent),
				Rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White,
				Distance = Distance(from, pos.GridPos),
			};
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
