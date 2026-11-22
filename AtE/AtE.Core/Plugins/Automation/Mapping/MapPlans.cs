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
		/// <summary>Fates to take ("Select a Fate" encounters) before the map boss is fought (see FatesFirst); 0: none.</summary>
		public int Fates { get; private set; }

		/// <summary>Walk to the tiles cut from a .tdt whose path has tilePathPart (see ApproachTiles).</summary>
		public MapRules Tile(string tilePathPart) { route.Add(("tile", tilePathPart)); return this; }
		/// <summary>Go through a door or transition, by its label (see TakeTransition).</summary>
		public MapRules Door(string labelText) { route.Add(("door", labelText)); return this; }
		/// <summary>A boss arena: walk to its tiles (a part of the .tdt path), then kill the map boss (KillMapBoss).</summary>
		public MapRules BossArena(string tilePathPart) { arenas.Add(tilePathPart); return this; }
		/// <summary>
		/// Take n fates (the Augury cards' "Select a Fate", found by their minimap icons, exploring for more) before anything
		/// else, leaving the map boss alone until then (its encounters go once it is dead); where the boss is seen meanwhile
		/// is remembered, to go back to (see TakeFates, MappingRoutine.BossSeenAt).
		/// </summary>
		public MapRules FatesFirst(int n) { Fates = n; return this; }

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
			if ( Fates > 0 ) chain = new TakeFates(routine, Fates) { Next = chain };
			return chain;
		}

		public override string ToString() => string.Join(", ", (Fates > 0 ? new[] { $"{Fates} fates before the boss" } : Array.Empty<string>())
			.Concat(route.Select(r => $"{r.kind} {r.what}")).Concat(arenas.Select(a => $"boss arena {a}")));
	}

	public partial class MappingRoutine {

		/// <summary>
		/// The maps with rules of their own, by the area's name (as the Atlas and the area show it, eg "Silo"). A map not
		/// listed is just explored and cleared (see ClearArea).
		/// </summary>
		[NoPersist] public static readonly Dictionary<string, MapRules> Rules = new Dictionary<string, MapRules>(StringComparer.OrdinalIgnoreCase) {
			// Mud Geyser: take 3 fates before the boss (its Augury encounters go once it is dead); the boss (Tunneltrap,
			// buried until the player is near) is left alone until then, and where it is seen is remembered
			["Mud Geyser"] = new MapRules().FatesFirst(3),
			// eg ["Silo"] = new MapRules().Tile("/some_tile").Door("Some Door").BossArena("/boss_tile"),
		};
	}
}
