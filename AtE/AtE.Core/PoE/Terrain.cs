using System;
using System.Collections.Generic;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A local copy of the current area's terrain grids (see Offsets.TerrainData).
	/// The grids are static for the life of an area instance, so they are read once
	/// and only re-read when the area changes (or the game moves the grid buffer).
	/// </summary>
	public class Terrain {

		public int Width { get; private set; } // in cells
		public int Height { get; private set; }
		public int BytesPerRow { get; private set; }
		public bool IsLoaded => walkable != null;
		/// <summary>
		/// Goes up each time new grids are loaded, so other code can tell when to rebuild what it derived from them.
		/// </summary>
		public int Generation { get; private set; } = 0;

		private byte[] walkable;
		private byte[] projectile;
		private IntPtr loadedFrom = IntPtr.Zero;

		public Terrain() {
			OnAreaChange += (sender, areaName) => Clear();
		}

		private List<AreaExit> exits = new List<AreaExit>();
		private long exitsUpdated = 0;
		/// <summary>
		/// The ways out of this area: approximate from the tiles at first, exact once their AreaTransition entity is seen.
		/// </summary>
		public IReadOnlyList<AreaExit> Exits {
			get {
				if ( IsLoaded && Time.ElapsedMilliseconds - exitsUpdated > 500 ) {
					AreaExits.UpdateFromEntities(exits);
					exitsUpdated = Time.ElapsedMilliseconds;
				}
				return exits;
			}
		}

		public void Clear() {
			exits = new List<AreaExit>();
			walkable = projectile = null;
			loadedFrom = IntPtr.Zero;
			Width = Height = BytesPerRow = 0;
		}

		/// <summary>
		/// Make sure the local grids match the ones in game memory, reading them if needed.
		/// Cheap to call every frame.
		/// </summary>
		public bool Update(Offsets.TerrainData data) {
			if ( !Offsets.IsValid(data.WalkableGrid) ) {
				Clear();
				return false;
			}
			if ( data.WalkableGrid.Head == loadedFrom ) return true;
			int width = (int)data.TilesX * Offsets.TerrainData.CellsPerTile;
			int height = (int)data.TilesY * Offsets.TerrainData.CellsPerTile;
			int bytesPerRow = data.BytesPerRow;
			int size = bytesPerRow * height;
			if ( width <= 0 || height <= 0 || bytesPerRow < (width + 1) / 2
				|| data.WalkableGrid.ItemCount(1) != size ) {
				return false; // probably still loading
			}
			var walk = new byte[size];
			if ( PoEMemory.TryRead(data.WalkableGrid.Head, walk) != size ) return false;
			var proj = new byte[size];
			if ( !(Offsets.IsValid(data.ProjectileGrid)
				&& data.ProjectileGrid.ItemCount(1) == size
				&& PoEMemory.TryRead(data.ProjectileGrid.Head, proj) == size) ) {
				proj = null;
			}
			SetGrids(width, height, bytesPerRow, walk, proj);
			loadedFrom = data.WalkableGrid.Head;
			exits = AreaExits.FromTiles(data, this);
			Log($"Terrain: loaded {Width}x{Height} cells ({size} bytes per grid), {exits.Count} exits in the tiles");
			return true;
		}

		private void SetGrids(int width, int height, int bytesPerRow, byte[] walk, byte[] proj) {
			walkable = walk;
			projectile = proj;
			Width = width;
			Height = height;
			BytesPerRow = bytesPerRow;
			Generation += 1;
		}

		/// <summary>
		/// Make a Terrain from grids already in hand (eg, saved from a game session), without reading game memory.
		/// The grids use the same packed 4-bit encoding as the game (see Offsets.TerrainData).
		/// </summary>
		public static Terrain FromGrids(int width, int height, int bytesPerRow, byte[] walkable, byte[] projectile = null) {
			var terrain = new Terrain();
			terrain.SetGrids(width, height, bytesPerRow, walkable, projectile);
			return terrain;
		}

		public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

		private int Cell(byte[] grid, int x, int y) {
			if ( grid == null || !InBounds(x, y) ) return 0;
			byte b = grid[(y * BytesPerRow) + (x >> 1)];
			return (x & 1) == 0 ? b & 0xF : b >> 4;
		}

		/// <summary>
		/// 0 means blocked, 1..5 means walkable; higher values are further from walls.
		/// </summary>
		public int Clearance(int x, int y) => Cell(walkable, x, y);
		public int Clearance(Offsets.Vector2i pos) => Clearance(pos.X, pos.Y);
		/// <summary>
		/// Unpack the walkable grid into dest, one byte of clearance per cell: cell (x,y) goes to dest[offset + y * stride + x].
		/// </summary>
		public void UnpackClearance(byte[] dest, int offset, int stride) {
			if ( walkable == null ) return;
			for ( int y = 0; y < Height; y++ ) {
				int src = y * BytesPerRow, dst = offset + (y * stride);
				for ( int x = 0; x < Width; x += 2 ) {
					byte b = walkable[src + (x >> 1)];
					dest[dst + x] = (byte)(b & 0xF);
					if ( x + 1 < Width ) dest[dst + x + 1] = (byte)(b >> 4);
				}
			}
		}

		public bool IsWalkable(int x, int y) => Cell(walkable, x, y) > 0;
		public bool IsWalkable(Offsets.Vector2i pos) => IsWalkable(pos.X, pos.Y);

		/// <summary>
		/// True if projectiles can pass over this cell (eg, water and low obstacles), see Offsets.TerrainData.
		/// </summary>
		public bool IsProjectilePassable(int x, int y) => Cell(projectile, x, y) > 0;
		public bool IsProjectilePassable(Offsets.Vector2i pos) => IsProjectilePassable(pos.X, pos.Y);

		/// <summary>
		/// Can you walk in a straight line from a to b, staying on cells with at least minClearance.
		/// </summary>
		public bool IsLineClear(Offsets.Vector2i a, Offsets.Vector2i b, int minClearance = 1) =>
			IsLineClear(walkable, a, b, minClearance);

		/// <summary>
		/// Can a projectile fly in a straight line from a to b.
		/// </summary>
		public bool HasLineOfSight(Offsets.Vector2i a, Offsets.Vector2i b) =>
			IsLineClear(projectile, a, b, 1);

		// Bresenham, but a diagonal step also needs both of the cells it squeezes between (so a line can't cut a corner)
		private bool IsLineClear(byte[] grid, Offsets.Vector2i a, Offsets.Vector2i b, int minClearance) {
			if ( grid == null ) return false;
			int x = a.X, y = a.Y;
			int dx = Math.Abs(b.X - x), dy = -Math.Abs(b.Y - y);
			int sx = x < b.X ? 1 : -1, sy = y < b.Y ? 1 : -1;
			int err = dx + dy;
			while ( true ) {
				if ( Cell(grid, x, y) < minClearance ) return false;
				if ( x == b.X && y == b.Y ) return true;
				int e2 = 2 * err;
				bool stepX = e2 >= dy, stepY = e2 <= dx;
				if ( stepX && stepY
					&& (Cell(grid, x + sx, y) < minClearance || Cell(grid, x, y + sy) < minClearance) ) {
					return false;
				}
				if ( stepX ) { err += dy; x += sx; }
				if ( stepY ) { err += dx; y += sy; }
			}
		}
	}

	public static partial class Globals {
		/// <summary>
		/// The terrain of the current area, or null if it is not available yet.
		/// </summary>
		public static Terrain GetTerrain() => PoEMemory.GameRoot?.InGameState?.Terrain;
	}
}
