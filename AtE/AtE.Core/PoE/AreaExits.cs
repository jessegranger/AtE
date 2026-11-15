using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Something in the current area that is found two ways: when the area loads, from the tiles cut from its .tdt
	/// (position is approximate), and exactly once its entity comes into network range.
	/// </summary>
	public class AreaFeature {
		/// <summary>Exact once Seen, otherwise the walkable cell of the feature's tiles closest to their center.</summary>
		public Offsets.Vector2i GridPos;
		/// <summary>True once the entity has been seen.</summary>
		public bool Seen;
		/// <summary>The .tdt the tiles were cut from, or null if it was only found as an entity.</summary>
		public string TileFile;
		/// <summary>The bounds of the tiles, in tile coordinates (inclusive).</summary>
		public int TileMinX, TileMinY, TileMaxX, TileMaxY;

		public override string ToString() => $"{GridPos}{(Seen ? "" : " (approx)")}";
	}

	/// <summary>
	/// A way out of the current area.
	/// Exits are found two ways:
	///  - when the area loads, from tiles cut from a "transition" .tdt (position is approximate, destination unknown)
	///  - when an AreaTransition entity comes into network range (exact position and destination)
	/// </summary>
	public class AreaExit : AreaFeature {
		/// <summary>
		/// The label on the door, eg "The Mud Flats"; null until the entity has been seen, unless the tile names it (see
		/// AreaExits.KnownExitTiles).
		/// </summary>
		public string Destination;

		/// <summary>The area on the other side, if the destination is known and is a neighbor of the current area.</summary>
		public WorldArea DestinationArea => Destination == null ? null :
			GetCurrentArea()?.Connections.FirstOrDefault(a => a.Name == Destination);

		public override string ToString() => $"{Destination ?? "(unknown exit)"} at {base.ToString()}";
	}

	public static class AreaExits {

		/// <summary>
		/// Tiles whose path has one of these is an exit too, though not a "transition" .tdt (found with the memory server's
		/// /tiles, 3.29.4):
		///  - The Climb's way to The Lower Prison: "Metadata/Terrain/Beach/Special/beach_prisonback.tdt"
		///  - The Upper Prison (Metadata/Terrain/Prison/Features/...): prison_entrance_down (stairs down; one is a way
		///    between the area's levels, labelled "The Upper Prison"), prison_exit_up, prison_door_up (and the quest-blocked
		///    shavronne_prison_door_up), prison_boss_exit (beside Brutus's cell)
		///  - Prisoner's Gate's way to The Ship Graveyard: ".../Beach/Transitions/ShipgraveyardPassageway/..."
		///  - The Cavern of Wrath (Metadata/Terrain/Cave/Features/...): caveup_exit (to The Cavern of Anger), caveskeleton_exit
		///    (back to The Ship Graveyard); in The Cavern of Anger, caveup_exit is the door to Merveil's Lair
		///  - The Chamber of Sins Level 1's way down to Level 2: "Metadata/Terrain/RuinedTempleForest/Features/templeruinforest_exit_down_v01_01.tdt",
		///    and Level 2's way back up: ".../templeruinforest_exit_up_v01_01.tdt"
		///  - The Crypt Level 1 (Metadata/Terrain/Church/Features/...): church_exit_down (to Level 2), church_exit_up (back to
		///    The Fellshrine Ruins)
		/// Some may lead to another level of the same area: walking there names them, and the right one is taken.
		/// </summary>
		public static readonly string[] OtherExitTiles = {
			"/beach_prisonback",
			"/prison_entrance_down", "/prison_exit_up", "prison_door_up", "/prison_boss_exit",
			"/ShipgraveyardPassageway/",
			"/caveup_exit", "/caveskeleton_exit",
			"/templeruinforest_exit_down", "/templeruinforest_exit_up",
			"/church_exit_down", "/church_exit_up",
			"/spidergrove_exit",
		};

		/// <summary>
		/// "Transition" tiles that are not exits (ground between two kinds of terrain): Prisoner's Gate's
		/// Beach/Transitions/beach_passageblock and Beach/Transitions/BeachToSwamp/ tiles.
		/// </summary>
		public static readonly string[] NotExitTiles = { "/beach_passageblock", "/BeachToSwamp/" };

		/// <summary>
		/// Exit tiles whose destination is known from the tile alone (a part of the .tdt path, without the version
		/// numbers, so it lasts through updates), in the area named (null: any area): the exit is named from the start,
		/// before its entity is in range. The same tile can lead to different places in different areas.
		/// </summary>
		public static readonly (string tile, string destination, string area)[] KnownExitTiles = {
			("ShipgraveyardPassageway/shipgraveyard_passageway", "The Ship Graveyard", null), // Prisoner's Gate (3.29.4)
			("Cave/Features/caveup_exit", "The Cavern of Anger", "The Cavern of Wrath"), // (3.29.4; from the tile alone, not yet seen as an entity)
			("Cave/Features/caveskeleton_exit", "The Ship Graveyard", "The Cavern of Wrath"), // (3.29.4)
			// the door to Merveil's arena: an AreaTransition labelled "Merveil's Lair" at <597, 1141>, on the caveup_exit
			// tile at <596, 1138> (3.29.4, walked there by hand; not cavedown_boss_exit, which can't be walked to)
			("Cave/Features/caveup_exit", "Merveil's Lair", "The Cavern of Anger"),
			// the dock to the camp, at <793, 1520>; the area's other transition tile, Forest/Transitions/forest_caveentrance,
			// is beside the waypoint where Act 2 starts, the way back to Act 1 (3.29.4, from the tiles alone)
			("Forest/Transitions/forestcamp_dock", "The Forest Encampment", "The Southern Forest"),
			// The Crossroads (3.29.4, seen live): the road gate north, at <1000, 264>, is The Fellshrine Ruins (the same
			// road_wall_gate tile, west at <219, 678>, is the way back to The Old Fields: the tile alone can't name either);
			// the temple entrance south, at <931, 1545>, is The Chamber of Sins (the last unnamed one, by elimination); the
			// bridge connection east, at <1653, 563>, is The Broken Bridge (from the tiles alone, not seen as an entity yet)
			("Forest/Transitions/temple_entrance", "The Chamber of Sins Level 1", "The Crossroads"),
			("Forest/Transitions/bridgeconnection", "The Broken Bridge", "The Crossroads"),
			// The Riverways (3.29.4, from the tiles alone, not seen as entities yet): besides the camp (forestcamp_left, at
			// <1941, 695>), the bridge connection at <931, 1883> is The Wetlands (across the river), and the road at
			// <424, 2104>, roadtothickforest_entrance, is The Western Forest (the road's end)
			("Forest/Transitions/bridgeconnection", "The Wetlands", "The Riverways"),
			("Forest/Transitions/forestcamp_left", "The Forest Encampment", "The Riverways"), // (seen live as an entity, 3.29.4)
			// The Fellshrine Ruins (3.29.4): the church's dungeon entrance at <829, 781> is The Crypt Level 1 (its only other
			// exit, road_wall_gate at <678, 2679>, is The Crossroads, seen as an entity)
			("Forest/Transitions/church_dungeon_entrance", "The Crypt Level 1", "The Fellshrine Ruins"),
			("Forest/Transitions/road_wall_gate", "The Crossroads", "The Fellshrine Ruins"),
			("Forest/Transitions/roadtothickforest_entrance", "The Western Forest", "The Riverways"),
			// The Chamber of Sins Level 1's only exit tile, at <712, 201> (3.29.4, from the tiles alone)
			("RuinedTempleForest/Features/templeruinforest_exit_down", "The Chamber of Sins Level 2", "The Chamber of Sins Level 1"),
			// The Chamber of Sins Level 2's way back up, at <1259, 827> (3.29.4, from the tiles alone; Level 2 has no waypoint tile)
			("RuinedTempleForest/Features/templeruinforest_exit_up", "The Chamber of Sins Level 1", "The Chamber of Sins Level 2"),
			// The Crypt Level 1 (3.29.4, from the tiles): church_exit_up at <1080, 1097> is the way in from The Fellshrine Ruins
			// (seen as an entity), church_exit_down at <429, 229> the way down to Level 2 (the only other exit tile; the
			// labyrinth_catacombs_door at <1000, 551> is the Trial of Ascendancy, not an exit). Level 2's way back up, by analogy
			("Church/Features/church_exit_down", "The Crypt Level 2", "The Crypt Level 1"),
			("Church/Features/church_exit_up", "The Fellshrine Ruins", "The Crypt Level 1"),
			("Church/Features/church_exit_up", "The Crypt Level 1", "The Crypt Level 2"), // (confirmed live)
			// The Western Forest (3.29.4): the road east, roadtothickforest_entrance at <1953, 1341>, is The Riverways (seen
			// live); the spider grove at <1655, 448> is The Weaver's Chambers, and the mountain pass west at <379, 896> is
			// Prisoner's Gate (Act 1), from the tiles alone
			("Forest/Transitions/roadtothickforest_entrance", "The Riverways", "The Western Forest"),
			("Forest/Transitions/spidergrove_entrance", "The Weaver's Chambers", "The Western Forest"),
			("Forest/Transitions/forest_mountainpass", "Prisoner's Gate", "The Western Forest"),
			// The Wetlands (3.29.4, seen live as entities): the bridge at <655, 254> is The Riverways, and the cave entrance at
			// <655, 1566> The Vaal Ruins
			("Forest/Transitions/bridgeconnection", "The Riverways", "The Wetlands"),
			("Forest/Transitions/forest_caveentrance_inca", "The Vaal Ruins", "The Wetlands"),
			// The Weaver's Chambers (3.29.4, from the tiles): the lit spidergrove_exit at <172, 483> is the way back out to The
			// Western Forest (the area's only connection); the plain spidergrove_exit tiles are doors within the area (see
			// PlacedExitTiles)
			("SpiderGrove/Features/spidergrove_exit_lit", "The Western Forest", "The Weaver's Chambers"),
		};

		/// <summary>
		/// Exit tiles that lead to one of several places in the area named, the tile alone can't tell which (eg, The
		/// Crossroads' two road gates, 3.29.4: one north to The Fellshrine Ruins, one west back to The Old Fields). Going to
		/// one of those places, GoToArea walks to these exits in turn until the right one shows its name, instead of stopping.
		/// </summary>
		public static readonly (string tile, string area, string[] destinations)[] AmbiguousExitTiles = {
			("Forest/Transitions/road_wall_gate", "The Crossroads", new[] { "The Fellshrine Ruins", "The Old Fields" }),
		};

		/// <summary>Is the tile one of the area's exits that may lead to destination, the tile alone can't tell (see AmbiguousExitTiles)?</summary>
		public static bool MayLeadTo(string tilePath, string destination) {
			if ( tilePath == null ) return false;
			string here = GetCurrentArea()?.Name;
			return AmbiguousExitTiles.Any(k => tilePath.IndexOf(k.tile, StringComparison.OrdinalIgnoreCase) >= 0
				&& string.Equals(k.area, here, StringComparison.OrdinalIgnoreCase)
				&& k.destinations.Contains(destination, StringComparer.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Exit tiles that lead to different places in the same area, told apart by where they are (the area's layout is the
		/// same each time): the tile, in the area named, within PlacedExitReach cells of near, leads to destination. If the
		/// layout turns out different, none is near: the exit stays unnamed (see AmbiguousExitTiles for that case).
		/// </summary>
		public static readonly (string tile, string area, int x, int y, string destination)[] PlacedExitTiles = {
			// The Crossroads' two road gates (3.29.4, seen live twice, at the same places): north, The Fellshrine Ruins;
			// west, the way back to The Old Fields
			("Forest/Transitions/road_wall_gate", "The Crossroads", 1000, 265, "The Fellshrine Ruins"),
			("Forest/Transitions/road_wall_gate", "The Crossroads", 219, 678, "The Old Fields"),
			// The Weaver's Chambers' two doors within the area (3.29.4, seen live as entities, both on spidergrove_exit tiles):
			// "The Weaver's Nest" (his arena) at <954, 715>, and one labelled "The Weaver's Chambers" at <784, 770>
			("SpiderGrove/Features/spidergrove_exit_v", "The Weaver's Chambers", 954, 713, "The Weaver's Nest"),
			("SpiderGrove/Features/spidergrove_exit_v", "The Weaver's Chambers", 782, 770, "The Weaver's Chambers"),
		};
		/// <summary>How near (cells) an exit must be to a PlacedExitTiles position to be named by it.</summary>
		public const float PlacedExitReach = 40;

		/// <summary>The destination a tile names in the current area (see KnownExitTiles, and PlacedExitTiles by its position), or null.</summary>
		public static string KnownDestination(string tilePath, Offsets.Vector2i? at = null) {
			if ( tilePath == null ) return null;
			string here = GetCurrentArea()?.Name;
			if ( at is Offsets.Vector2i pos ) {
				var placed = PlacedExitTiles.FirstOrDefault(k => tilePath.IndexOf(k.tile, StringComparison.OrdinalIgnoreCase) >= 0
					&& string.Equals(k.area, here, StringComparison.OrdinalIgnoreCase)
					&& Combat.Distance(pos, new Offsets.Vector2i(k.x, k.y)) <= PlacedExitReach).destination;
				if ( placed != null ) return placed;
			}
			return KnownExitTiles.FirstOrDefault(k => tilePath.IndexOf(k.tile, StringComparison.OrdinalIgnoreCase) >= 0
				&& (k.area == null || string.Equals(k.area, here, StringComparison.OrdinalIgnoreCase))).destination;
		}

		public static bool IsTransitionTile(string tilePath) =>
			tilePath != null
			&& !NotExitTiles.Any(t => tilePath.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
			&& (tilePath.IndexOf("transition", StringComparison.OrdinalIgnoreCase) >= 0
				|| OtherExitTiles.Any(t => tilePath.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));

		/// <summary>
		/// 3.29.3, The Coast: the waypoint stands on the only tile cut from "Metadata/Terrain/Beach/Features/beach_waypoint_v01_01.tdt",
		/// at the center of that tile (exactly the Waypoint entity's GridPos).
		/// </summary>
		public static bool IsWaypointTile(string tilePath) =>
			tilePath != null && tilePath.IndexOf("waypoint", StringComparison.OrdinalIgnoreCase) >= 0;

		/// <summary>
		/// Find exit candidates from the area's tiles: each connected group of tiles cut from the same "transition" .tdt.
		/// </summary>
		public static List<AreaExit> FromTiles(Offsets.TerrainData data, Terrain terrain) {
			var exits = FromTiles<AreaExit>(data, terrain, IsTransitionTile);
			foreach ( var e in exits ) e.Destination ??= KnownDestination(e.TileFile, e.GridPos);
			return exits;
		}

		/// <summary>
		/// Find features from the area's tiles: each connected group of tiles cut from the same .tdt, whose path is wanted.
		/// </summary>
		public static List<T> FromTiles<T>(Offsets.TerrainData data, Terrain terrain, Func<string, bool> wanted) where T : AreaFeature, new() {
			var result = new List<T>();
			int tilesX = (int)data.TilesX, tilesY = (int)data.TilesY;
			int count = tilesX * tilesY;
			if ( count <= 0 || data.TileDetails.ItemCount(Offsets.TileDetails.Size) != count ) return result;
			var tiles = new Offsets.TileDetails[count];
			if ( PoEMemory.TryRead(data.TileDetails.Head, tiles) != count * Offsets.TileDetails.Size ) return result; // TryRead returns bytes, not items

			// many tiles share one TileFile, so only read each name once
			var paths = new Dictionary<IntPtr, string>();
			string PathOf(IntPtr tileFile) {
				if ( !paths.TryGetValue(tileFile, out string path) ) {
					path = PoEMemory.TryRead(tileFile, out Offsets.TileFile file) ? file.Path.Value : null;
					paths[tileFile] = path;
				}
				return path;
			}

			var visited = new bool[count];
			var stack = new Stack<int>();
			for ( int i = 0; i < count; i++ ) {
				if ( visited[i] ) continue;
				visited[i] = true;
				IntPtr file = tiles[i].ptrTileFile;
				string path = PathOf(file);
				if ( !wanted(path) ) continue;
				var exit = new T() {
					TileFile = path,
					TileMinX = int.MaxValue, TileMinY = int.MaxValue,
					TileMaxX = int.MinValue, TileMaxY = int.MinValue,
				};
				long sumX = 0, sumY = 0, n = 0;
				var clusterTiles = new List<int>();
				// flood fill the neighboring tiles from the same file
				stack.Push(i);
				while ( stack.Count > 0 ) {
					int t = stack.Pop();
					clusterTiles.Add(t);
					int tx = t % tilesX, ty = t / tilesX;
					exit.TileMinX = Math.Min(exit.TileMinX, tx);
					exit.TileMinY = Math.Min(exit.TileMinY, ty);
					exit.TileMaxX = Math.Max(exit.TileMaxX, tx);
					exit.TileMaxY = Math.Max(exit.TileMaxY, ty);
					sumX += tx; sumY += ty; n += 1;
					foreach ( var (nx, ny) in new[] { (tx + 1, ty), (tx - 1, ty), (tx, ty + 1), (tx, ty - 1) } ) {
						if ( nx < 0 || ny < 0 || nx >= tilesX || ny >= tilesY ) continue;
						int nt = (ny * tilesX) + nx;
						if ( visited[nt] || tiles[nt].ptrTileFile != file ) continue;
						visited[nt] = true;
						stack.Push(nt);
					}
				}
				const int cellsPerTile = Offsets.TerrainData.CellsPerTile;
				var center = new Offsets.Vector2i(
					(int)(sumX * cellsPerTile / n) + (cellsPerTile / 2),
					(int)(sumY * cellsPerTile / n) + (cellsPerTile / 2));
				exit.GridPos = ClosestWalkable(terrain, clusterTiles, tilesX, center);
				result.Add(exit);
			}
			return result;
		}

		/// <summary>
		/// The center of a group of tiles is often off the walkable ground (eg, in the cliff around a door),
		/// so use the walkable cell in those tiles that is closest to the center, so that paths can reach it.
		/// </summary>
		private static Offsets.Vector2i ClosestWalkable(Terrain terrain, List<int> tiles, int tilesX, Offsets.Vector2i center) {
			const int cellsPerTile = Offsets.TerrainData.CellsPerTile;
			var best = center;
			long bestDistance = long.MaxValue;
			foreach ( int t in tiles ) {
				int x0 = (t % tilesX) * cellsPerTile, y0 = (t / tilesX) * cellsPerTile;
				for ( int y = y0; y < y0 + cellsPerTile; y++ ) {
					for ( int x = x0; x < x0 + cellsPerTile; x++ ) {
						long dx = x - center.X, dy = y - center.Y;
						long d = (dx * dx) + (dy * dy);
						if ( d < bestDistance && terrain.IsWalkable(x, y) ) {
							bestDistance = d;
							best = new Offsets.Vector2i(x, y);
						}
					}
				}
			}
			return best;
		}

		/// <summary>
		/// Merge the AreaTransition entities that are in range now into the list of exits.
		/// </summary>
		public static void UpdateFromEntities(List<AreaExit> exits) {
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				var transition = ent.GetComponent<AreaTransition>();
				if ( !IsValid(transition) ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				var grid = pos.GridPos;
				string destination = ent.GetComponent<Render>()?.Name;

				var exit = exits.FirstOrDefault(e => e.Seen && Distance(e.GridPos, grid) < 10)
					?? exits.FirstOrDefault(e => !e.Seen && e.TileFile != null && TileBoundsContain(e, grid, 1));
				if ( exit == null ) {
					exit = new AreaExit();
					exits.Add(exit);
				}
				if ( !exit.Seen && exit.TileFile != null && destination != null && KnownDestination(exit.TileFile, exit.GridPos) != destination ) {
					// a tile whose exit was not known (or known wrong) now shows its name: log it, to add to KnownExitTiles
					Log($"AreaExits: learned: in {GetCurrentArea()?.Name}, the tile {exit.TileFile} at {exit.GridPos} leads to {destination}"
						+ $" (was {KnownDestination(exit.TileFile, exit.GridPos) ?? "unknown"}; entity at {grid})");
				}
				exit.Seen = true;
				exit.GridPos = grid;
				exit.Destination = destination;
			}
		}

		public const string PATH_WAYPOINT = "Metadata/MiscellaneousObjects/Waypoint";

		/// <summary>
		/// The waypoint (as found in the tiles, or null), made exact from the Waypoint entity if it is in range now.
		/// </summary>
		public static AreaFeature UpdateWaypoint(AreaFeature waypoint) {
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) || ent.Path != PATH_WAYPOINT ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				waypoint ??= new AreaFeature();
				waypoint.Seen = true;
				waypoint.GridPos = pos.GridPos;
				break;
			}
			return waypoint;
		}

		private static float Distance(Offsets.Vector2i a, Offsets.Vector2i b) {
			float dx = a.X - b.X, dy = a.Y - b.Y;
			return MathF.Sqrt((dx * dx) + (dy * dy));
		}

		private static bool TileBoundsContain(AreaFeature exit, Offsets.Vector2i grid, int marginTiles) {
			int tx = grid.X / Offsets.TerrainData.CellsPerTile, ty = grid.Y / Offsets.TerrainData.CellsPerTile;
			return tx >= exit.TileMinX - marginTiles && tx <= exit.TileMaxX + marginTiles
				&& ty >= exit.TileMinY - marginTiles && ty <= exit.TileMaxY + marginTiles;
		}
	}
}
