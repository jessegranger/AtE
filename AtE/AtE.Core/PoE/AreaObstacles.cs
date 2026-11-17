using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Something that blocks walking but is not in the walkable grid (Terrain): a chest or barrel (Metadata/Chests/...).
	/// Seen live (3.29.3, a Labyrinth trial): Barrel1old..Barrel4old stand on cells the grid has fully walkable, so a path
	/// goes straight through them and the player walks into them. Paths are planned as if they were not there; the
	/// Navigator adds a SkipObstacle step where a path passes one, and the follower uses a travel skill past it (which
	/// breaks or skips it).
	/// </summary>
	public class AreaObstacle {
		public uint EntityId;
		public string Path;
		public Offsets.Vector2i GridPos;

		/// <summary>False once it is opened or broken (or its entity is gone).</summary>
		public bool IsStanding() {
			if ( !EntityCache.TryGetEntity(EntityId, out var ent) || !IsValid(ent) ) return false;
			return AreaObstacles.IsStanding(ent);
		}

		public override string ToString() => $"Obstacle {EntityId} at {GridPos} ({Path?.Split('/').LastOrDefault()})";
	}

	public static class AreaObstacles {
		public static bool IsObstaclePath(string path) => path != null && path.Contains("/Chests/");

		/// <summary>Not opened or broken yet: still targetable, and its Chest not opened.</summary>
		public static bool IsStanding(Entity ent) {
			var chest = ent.GetComponent<Chest>();
			if ( IsValid(chest) && chest.IsOpened ) return false;
			var targetable = ent.GetComponent<Targetable>();
			return !IsValid(targetable) || targetable.IsTargetable;
		}

		/// <summary>Add any standing obstacles in range now; drop those opened, broken or gone.</summary>
		public static void Update(List<AreaObstacle> obstacles) {
			var standing = new HashSet<uint>();
			int entities = 0;
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				entities += 1;
				string path = ent.Path;
				if ( !IsObstaclePath(path) || !IsStanding(ent) ) continue;
				standing.Add(ent.Id);
				if ( obstacles.Exists(o => o.EntityId == ent.Id) ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				obstacles.Add(new AreaObstacle() { EntityId = ent.Id, Path = path, GridPos = pos.GridPos });
			}
			if ( entities > 0 ) obstacles.RemoveAll(o => !standing.Contains(o.EntityId));
		}
	}
}
