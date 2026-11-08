using System;
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

		private byte[] walkable;
		private byte[] projectile;
		private IntPtr loadedFrom = IntPtr.Zero;

		public Terrain() {
			OnAreaChange += (sender, areaName) => Clear();
		}

		public void Clear() {
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
			walkable = walk;
			projectile = proj;
			Width = width;
			Height = height;
			BytesPerRow = bytesPerRow;
			loadedFrom = data.WalkableGrid.Head;
			Log($"Terrain: loaded {Width}x{Height} cells ({size} bytes per grid)");
			return true;
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
		public bool IsWalkable(int x, int y) => Cell(walkable, x, y) > 0;
		public bool IsWalkable(Offsets.Vector2i pos) => IsWalkable(pos.X, pos.Y);

		/// <summary>
		/// True if projectiles can pass over this cell (eg, water and low obstacles), see Offsets.TerrainData.
		/// </summary>
		public bool IsProjectilePassable(int x, int y) => Cell(projectile, x, y) > 0;
		public bool IsProjectilePassable(Offsets.Vector2i pos) => IsProjectilePassable(pos.X, pos.Y);
	}

	public static partial class Globals {
		/// <summary>
		/// The terrain of the current area, or null if it is not available yet.
		/// </summary>
		public static Terrain GetTerrain() => PoEMemory.GameRoot?.InGameState?.Terrain;
	}
}
