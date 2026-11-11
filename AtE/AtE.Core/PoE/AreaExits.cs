using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A way out of the current area.
	/// Exits are found two ways:
	///  - when the area loads, from tiles cut from a "transition" .tdt (position is approximate, destination unknown)
	///  - when an AreaTransition entity comes into network range (exact position and destination)
	/// </summary>
	public class AreaExit {
		/// <summary>The label on the door, eg "The Mud Flats"; null until the entity has been seen.</summary>
		public string Destination;
		/// <summary>Exact once Seen, otherwise the walkable cell of the transition tiles closest to their center.</summary>
		public Offsets.Vector2i GridPos;
		/// <summary>True once the AreaTransition entity has been seen.</summary>
		public bool Seen;
		/// <summary>The .tdt the exit tiles were cut from, or null if it was only found as an entity.</summary>
		public string TileFile;
		/// <summary>The bounds of the transition tiles, in tile coordinates (inclusive).</summary>
		public int TileMinX, TileMinY, TileMaxX, TileMaxY;

		/// <summary>The area on the other side, if the destination is known and is a neighbor of the current area.</summary>
		public WorldArea DestinationArea => Destination == null ? null :
			GetCurrentArea()?.Connections.FirstOrDefault(a => a.Name == Destination);

		public override string ToString() => $"{Destination ?? "(unknown exit)"} at {GridPos}{(Seen ? "" : " (approx)")}";
	}

	public static class AreaExits {

		public static bool IsTransitionTile(string tilePath) =>
			tilePath != null && tilePath.IndexOf("transition", StringComparison.OrdinalIgnoreCase) >= 0;

		/// <summary>
		/// Find exit candidates from the area's tiles: each connected group of tiles cut from the same "transition" .tdt.
		/// </summary>
		public static List<AreaExit> FromTiles(Offsets.TerrainData data, Terrain terrain) {
			var result = new List<AreaExit>();
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
				if ( !IsTransitionTile(path) ) continue;
				var exit = new AreaExit() {
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
				exit.Seen = true;
				exit.GridPos = grid;
				exit.Destination = destination;
			}
		}

		private static float Distance(Offsets.Vector2i a, Offsets.Vector2i b) {
			float dx = a.X - b.X, dy = a.Y - b.Y;
			return MathF.Sqrt((dx * dx) + (dy * dy));
		}

		private static bool TileBoundsContain(AreaExit exit, Offsets.Vector2i grid, int marginTiles) {
			int tx = grid.X / Offsets.TerrainData.CellsPerTile, ty = grid.Y / Offsets.TerrainData.CellsPerTile;
			return tx >= exit.TileMinX - marginTiles && tx <= exit.TileMaxX + marginTiles
				&& ty >= exit.TileMinY - marginTiles && ty <= exit.TileMaxY + marginTiles;
		}
	}
}
