using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Which cells of the backpack are taken, read from the items in it, with cells marked taken or free by hand for what
	/// is about to move (eg, a cell an item is about to be put in, or an item about to be picked up); and where the free
	/// spaces are, and where each cell is on screen.
	/// </summary>
	public sealed class BackpackGrid {
		public const int Width = 12, Height = 5;

		// the item in each cell (or a stand-in for one about to be put there)
		private readonly object[,] cells = new object[Width, Height];
		private static readonly object Reserved = new object();

		/// <summary>The backpack as it is now (empty if the inventory panel can't be read).</summary>
		public static BackpackGrid Read() {
			var grid = new BackpackGrid();
			foreach ( var item in BackpackItems() ) grid.Mark(item.X, item.Y, item.Width, item.Height, item);
			return grid;
		}

		private void Mark(int x, int y, int w, int h, object what) {
			if ( x < 0 || y < 0 || w <= 0 || h <= 0 || w > 5 || h > 5 ) return;
			for ( int i = x; i < Math.Min(Width, x + w); i++ ) {
				for ( int j = y; j < Math.Min(Height, y + h); j++ ) cells[i, j] = what;
			}
		}

		/// <summary>Mark a w x h space at x, y taken (eg, something is about to be put there).</summary>
		public void Reserve(int x, int y, int w, int h) => Mark(x, y, w, h, Reserved);
		/// <summary>Mark an item's cells free (eg, it is about to be picked up).</summary>
		public void Free(InventoryItem item) {
			if ( item == null ) return;
			for ( int i = 0; i < Width; i++ ) {
				for ( int j = 0; j < Height; j++ ) {
					if ( cells[i, j] is InventoryItem it && it.Address == item.Address ) cells[i, j] = null;
				}
			}
		}

		/// <summary>Is any cell of the w x h space at x, y taken (or off the edge)?</summary>
		public bool IsOccupied(int x, int y, int w, int h) {
			if ( x < 0 || y < 0 || x + w > Width || y + h > Height ) return true;
			for ( int i = x; i < x + w; i++ ) {
				for ( int j = y; j < y + h; j++ ) {
					if ( cells[i, j] != null ) return true;
				}
			}
			return false;
		}

		/// <summary>The top left cell of the first free w x h space (column by column, as the game fills it), or false if there is none.</summary>
		public bool TryFindFree(int w, int h, out int x, out int y) {
			for ( x = 0; x < Width; x++ ) {
				for ( y = 0; y < Height; y++ ) {
					if ( !IsOccupied(x, y, w, h) ) return true;
				}
			}
			x = y = -1;
			return false;
		}

		/// <summary>How many cells are free.</summary>
		public int FreeCells => Enumerable.Range(0, Width * Height).Count(i => cells[i % Width, i / Width] == null);

		/// <summary>The backpack's box on screen (empty if it can't be read).</summary>
		public static RectangleF ScreenRect() {
			var elem = GetUI()?.InventoryPanel?.Backpack;
			return IsValid(elem) ? elem.GetClientRect() : RectangleF.Empty;
		}

		/// <summary>The size of a cell on screen (0 if the backpack can't be read).</summary>
		public static float CellSize => ScreenRect().Width / Width;

		/// <summary>The middle of cell x, y on screen (zero if the backpack can't be read).</summary>
		public static Vector2 CellCenter(int x, int y) {
			var rect = ScreenRect();
			if ( rect.Width <= 0 ) return Vector2.Zero;
			return new Vector2(rect.X + rect.Width / Width * (x + 0.5f), rect.Y + rect.Height / Height * (y + 0.5f));
		}

		/// <summary>The box of a w x h space at x, y on screen (to click in its middle).</summary>
		public static RectangleF SpaceRect(int x, int y, int w, int h) {
			var rect = ScreenRect();
			float cw = rect.Width / Width, ch = rect.Height / Height;
			return new RectangleF(rect.X + cw * x, rect.Y + ch * y, cw * w, ch * h);
		}
	}
}
