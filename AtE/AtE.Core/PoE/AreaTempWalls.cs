using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Short-lived walls that block walking but are not in the game's walkable grid (neither the copy Terrain made when the
	/// area loaded, nor the live one): eg, the ring of ice a Blight frost tower's Ice Prison raises around the player.
	/// Seen live (3.30.0d, Mud Geyser): 20 Metadata/Effects/.../ice_prison/FrostWall entities (Animated, BaseEvents, Buffs,
	/// DiesAfterTime, Life, Pathfinding, Positioned, Render, Stats) about 5 cells apart on a ring ~20 cells around the
	/// player, and the live walkable grid fully open under them, so the Navigator planned paths through them.
	/// Terrain blocks the cells within each kind's Radius of a standing one (see Terrain.TempBlocked), so the Navigator,
	/// the DangerMap and straight-line walking go around them, until they are gone.
	/// </summary>
	public static class AreaTempWalls {

		public class Kind {
			public string PathPart;
			/// <summary>Cells within this distance of the entity are blocked.</summary>
			public float Radius;
			/// <summary>
			/// Taken as gone once its animated object's clock (ClientAnimationController.TimeSpentAnimating) is older than this
			/// (an entity can stay listed after it is gone; a wall that is not there must not shut the player in). 0: no limit.
			/// </summary>
			public float MaxAgeSec;
		}

		public static readonly List<Kind> Kinds = new List<Kind>() {
			// Blight frost tower's Ice Prison: pieces ~5 cells apart, so 2.5 closes the gaps between them; how long it lasts
			// is not known yet (the log says, when they go): 10 s is a guess at the most
			new Kind() { PathPart = "ice_prison/FrostWall", Radius = 2.5f, MaxAgeSec = 10 },
		};

		/// <summary>One wall piece standing now.</summary>
		public struct Wall {
			public uint EntityId;
			public Offsets.Vector2i GridPos;
			public float Radius;
		}

		/// <summary>The kind of temporary wall ent is (by its path), or null.</summary>
		public static Kind KindOf(Entity ent) {
			string path = ent?.Path;
			if ( path == null || !path.StartsWith("Metadata/Effects/", StringComparison.Ordinal) ) return null;
			foreach ( var k in Kinds ) if ( path.Contains(k.PathPart) ) return k;
			return null;
		}

		private static float Age(Entity e) {
			var anim = e.GetComponent<Animated>();
			var cac = IsValid(anim) ? anim.AnimatedObject?.GetComponent<ClientAnimationController>() : null;
			return IsValid(cac) ? cac.TimeSpentAnimating : float.NaN;
		}

		/// <summary>The temporary walls standing now (alive, and not older than their kind's MaxAgeSec).</summary>
		public static List<Wall> Standing() {
			var walls = new List<Wall>();
			foreach ( var ent in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				if ( !IsValid(ent) ) continue;
				var kind = KindOf(ent);
				if ( kind == null ) continue;
				var life = ent.GetComponent<Life>();
				if ( IsValid(life) && life.MaxHP > 0 && life.CurHP <= 0 ) continue;
				if ( kind.MaxAgeSec > 0 && Age(ent) is float age && age > kind.MaxAgeSec ) continue; // (NaN: no clock, taken as standing)
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				walls.Add(new Wall() { EntityId = ent.Id, GridPos = pos.GridPos, Radius = kind.Radius });
			}
			return walls;
		}
	}
}
