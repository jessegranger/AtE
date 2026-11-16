using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A trap that hurts the player on touch (eg, a Labyrinth sawblade, which runs back and forth along a rail).
	///
	/// Seen live (3.29.3, The Chamber of Sins Level 2): Metadata/Terrain/Labyrinth/Traps/LabyrinthSawblade has Animated,
	/// BaseEvents, Buffs, Movement, Positioned, Render, Stats and Transitionable; its Positioned.GridPos moves (about 5
	/// cells/s along one axis) and Positioned.Size is 1. The rails it runs on (Traps/Rail_Visible, many short pieces) are
	/// scenery: only Animated, BaseEvents, Positioned and Render. So a trap is an entity under /Traps/ with Movement or Buffs.
	/// </summary>
	public class AreaTrap {
		public uint EntityId;
		public string Path;
		/// <summary>Where it is now (it moves: read again each frame by Refresh).</summary>
		public Offsets.Vector2i GridPos;

		/// <summary>Read where it is now; false if its entity is gone.</summary>
		public bool Refresh() {
			if ( !EntityCache.TryGetEntity(EntityId, out var ent) || !IsValid(ent) ) return false;
			var pos = ent.GetComponent<Positioned>();
			if ( !IsValid(pos) ) return false;
			GridPos = pos.GridPos;
			return true;
		}

		public override string ToString() => $"Trap {EntityId} at {GridPos} ({Path?.Split('/').LastOrDefault()})";
	}

	public static class AreaTraps {
		public static bool IsTrapPath(string path) => path != null && path.Contains("/Traps/");

		/// <summary>A trap has Movement or Buffs (a sawblade has both); its rails (scenery) have neither. (Stats did not resolve on a sawblade.)</summary>
		public static bool IsTrapBody(Entity ent) => ent.HasComponent("Movement") || ent.HasComponent("Buffs");

		public static bool IsTrap(Entity ent) => IsValid(ent) && IsTrapPath(ent.Path) && IsTrapBody(ent);

		/// <summary>Add any traps in range now, and drop those whose entity is gone.</summary>
		public static void Update(List<AreaTrap> traps) {
			var seen = new HashSet<uint>();
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				seen.Add(ent.Id);
				if ( traps.Exists(t => t.EntityId == ent.Id) ) continue;
				string path = ent.Path;
				if ( !IsTrapPath(path) || !IsTrapBody(ent) ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				traps.Add(new AreaTrap() { EntityId = ent.Id, Path = path, GridPos = pos.GridPos });
			}
			if ( seen.Count > 0 ) traps.RemoveAll(t => !seen.Contains(t.EntityId));
		}

		/// <summary>The nearest trap within radius cells of at (its position read now), or null.</summary>
		public static AreaTrap Nearest(IReadOnlyList<AreaTrap> traps, Offsets.Vector2i at, float radius) {
			AreaTrap best = null;
			float bestD = radius * radius;
			foreach ( var trap in traps ) {
				if ( !trap.Refresh() ) continue;
				float dx = trap.GridPos.X - at.X, dy = trap.GridPos.Y - at.Y;
				float d = (dx * dx) + (dy * dy);
				if ( d <= bestD ) { bestD = d; best = trap; }
			}
			return best;
		}
	}
}
