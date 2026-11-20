using System;
using System.Collections.Generic;
using System.Linq;

namespace AtE {

	/// <summary>
	/// What ClearArea does in one map before it clears it: a route (tiles to walk to, doors or transitions to go through,
	/// in order) and the boss arenas (tiles to walk to, then kill the map boss there). Built with Tile, Door and BossArena,
	/// eg new MapRules().Tile("/silo_entrance").Door("Silo Door").BossArena("/silo_boss").
	/// </summary>
	public class MapRules {
		private readonly List<(string kind, string what)> route = new List<(string, string)>();
		private readonly List<string> arenas = new List<string>();

		/// <summary>Walk to the tiles cut from a .tdt whose path has tilePathPart (see ApproachTiles).</summary>
		public MapRules Tile(string tilePathPart) { route.Add(("tile", tilePathPart)); return this; }
		/// <summary>Go through a door or transition, by its label (see TakeTransition).</summary>
		public MapRules Door(string labelText) { route.Add(("door", labelText)); return this; }
		/// <summary>A boss arena: walk to its tiles (a part of the .tdt path), then kill the map boss (KillMapBoss).</summary>
		public MapRules BossArena(string tilePathPart) { arenas.Add(tilePathPart); return this; }

		/// <summary>The route, then each boss arena (and its boss), then next: goals chained by Next, built when it is run.</summary>
		public IState Plan(MappingRoutine routine, IState next) {
			IState chain = next;
			foreach ( var arena in Enumerable.Reverse(arenas) ) {
				chain = new KillMapBoss(routine) { Next = chain };
				chain = new ApproachTiles($"the boss arena ({arena})", arena) { Next = chain };
			}
			foreach ( var (kind, what) in Enumerable.Reverse(route) ) {
				chain = kind == "door" ? new TakeTransition(what) { Next = chain } : new ApproachTiles(what, what) { Next = chain };
			}
			return chain;
		}

		public override string ToString() => string.Join(", ", route.Select(r => $"{r.kind} {r.what}").Concat(arenas.Select(a => $"boss arena {a}")));
	}

	public partial class MappingRoutine {

		/// <summary>
		/// The maps with rules of their own, by the area's name (as the Atlas and the area show it, eg "Silo"). A map not
		/// listed is just explored and cleared (see ClearArea).
		/// </summary>
		[NoPersist] public static readonly Dictionary<string, MapRules> Rules = new Dictionary<string, MapRules>(StringComparer.OrdinalIgnoreCase) {
			// TODO: Silo's and Mud Geyser's own rules (to be specified), eg:
			// ["Silo"] = new MapRules().Tile("/some_tile").Door("Some Door").BossArena("/boss_tile"),
		};
	}
}
