using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A door (an entity with a TriggerableBlockage) in the current area.
	///
	/// While a door is closed, the game marks it as blocked in the walkable grid, as a slab a few cells thick.
	/// When it opens, the game rewrites those cells as walkable, but Terrain only reads the grid when the area loads,
	/// so the slab is found here instead (as Passage) and the Navigator treats it as walkable,
	/// with a cost to open the door while it is closed.
	///
	/// While it is closed, the Passage is a guess (FindPassage). That suits a door, but not every blocker is a door:
	/// eg, the invisible wall around a boss arena has a big box with rocks in it, which the guess takes for passage too.
	/// So once a blocker is open, its Passage is replaced by the cells in its box that the game has opened (PassageIsExact).
	/// </summary>
	public class AreaDoor {
		public uint EntityId;
		public Offsets.Vector2i GridPos;
		/// <summary>The inclusive box of cells the door may block (from TriggerableBlockage).</summary>
		public Offsets.Vector2i GridMin, GridMax;
		public bool IsClosed;
		/// <summary>The cells that are blocked in Terrain while the door is closed, and become walkable when it opens.</summary>
		public List<Offsets.Vector2i> Passage = new List<Offsets.Vector2i>();
		/// <summary>True once Passage was read from the game's grid after the blocker opened (rather than guessed).</summary>
		public bool PassageIsExact;
		/// <summary>When it was first seen open (Time.ElapsedMilliseconds), or 0.</summary>
		public long OpenSince;

		/// <summary>The door's entity, if it is still in range.</summary>
		public Entity GetEntity() => GetEntities().FirstOrDefault(e => e.Id == EntityId);

		/// <summary>
		/// The door's label on the screen (eg, "Door"), which is what to click to open it; null if not shown.
		/// </summary>
		public Element GetLabel() =>
			GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Id == EntityId)?.Label;

		public override string ToString() => $"Door {EntityId} at {GridPos} ({(IsClosed ? "closed" : "open")}, {Passage.Count} passage cells)";
	}

	public static class AreaDoors {

		/// <summary>
		/// Add (or refresh) the doors in range now. Returns true if a new passage was found (so navigation data must be rebuilt).
		/// </summary>
		public static bool UpdateFromEntities(Terrain terrain, List<AreaDoor> doors) {
			bool newPassage = false;
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				var blockage = ent.GetComponent<TriggerableBlockage>();
				if ( !IsValid(blockage) ) continue;
				var door = doors.FirstOrDefault(d => d.EntityId == ent.Id);
				if ( door == null ) {
					var pos = ent.GetComponent<Positioned>();
					if ( !IsValid(pos) ) continue;
					door = new AreaDoor() {
						EntityId = ent.Id,
						GridPos = pos.GridPos,
						GridMin = blockage.GridMin,
						GridMax = blockage.GridMax,
					};
					door.Passage = FindPassage(terrain, door.GridMin, door.GridMax);
					doors.Add(door);
					newPassage |= door.Passage.Count > 0;
				}
				door.IsClosed = blockage.IsClosed;
				if ( door.IsClosed ) door.OpenSince = 0;
				else if ( door.OpenSince == 0 ) door.OpenSince = Time.ElapsedMilliseconds;
				if ( !door.IsClosed && !door.PassageIsExact && TryReadOpenedPassage(terrain, door, out var opened) ) {
					door.PassageIsExact = true;
					if ( !SameCells(opened, door.Passage) ) {
						Log($"Door {door.EntityId}: opened {opened.Count} cells (the guess was {door.Passage.Count})");
						door.Passage = opened;
						newPassage = true;
					}
				}
			}
			return newPassage;
		}

		/// <summary>
		/// The cells in the door's box that the game has made walkable since the area loaded. False if the grid can't
		/// be read, or if nothing has changed yet (the game may not have rewritten it in the same frame).
		/// </summary>
		private static bool TryReadOpenedPassage(Terrain terrain, AreaDoor door, out List<Offsets.Vector2i> opened) {
			opened = new List<Offsets.Vector2i>();
			if ( !terrain.TryReadLiveWalkable(door.GridMin, door.GridMax, out var live) ) return false;
			for ( int y = door.GridMin.Y; y <= door.GridMax.Y; y++ ) {
				for ( int x = door.GridMin.X; x <= door.GridMax.X; x++ ) {
					if ( !terrain.IsWalkable(x, y) && live(x, y) ) opened.Add(new Offsets.Vector2i(x, y));
				}
			}
			// nothing opened: it was open when the area loaded, or the grid isn't rewritten yet (give it a moment),
			// or it never blocked the walkable grid at all (eg, the invisible wall of Hillock's arena): then no cells are its passage
			return opened.Count > 0 || door.Passage.Count == 0 || Time.ElapsedMilliseconds - door.OpenSince > 1500;
		}

		private static bool SameCells(List<Offsets.Vector2i> a, List<Offsets.Vector2i> b) =>
			a.Count == b.Count && new HashSet<long>(a.Select(c => c.Id)).SetEquals(b.Select(c => c.Id));

		/// <summary>
		/// Find the door's slab in the grid: in each row (or column) of the box, a run of blocked cells
		/// with walkable cells on both sides. The door frame makes a few longer runs, so only the runs
		/// as thick as the slab (the most common length) are kept. Empty if the door was already open when the grid was read.
		/// </summary>
		/// <summary>A door's slab is a few cells thick; runs of blocked cells thicker than this are something else.</summary>
		public static int MaxSlabThickness = 8;
		/// <summary>A door's slab spans at least this many rows (or columns).</summary>
		public static int MinSlabLines = 3;

		public static List<Offsets.Vector2i> FindPassage(Terrain terrain, Offsets.Vector2i min, Offsets.Vector2i max) {
			var best = new List<Offsets.Vector2i>();
			int bestLines = 0;
			foreach ( bool alongX in new[] { true, false } ) {
				// (line, start, length) of each run; a "line" is a row when alongX, else a column
				var runs = new List<(int line, int start, int length)>();
				int lineMin = alongX ? min.Y : min.X, lineMax = alongX ? max.Y : max.X;
				int posMin = alongX ? min.X : min.Y, posMax = alongX ? max.X : max.Y;
				bool Walkable(int line, int p) => alongX ? terrain.IsWalkable(p, line) : terrain.IsWalkable(line, p);
				for ( int line = lineMin; line <= lineMax; line++ ) {
					int start = -1;
					for ( int p = posMin; p <= posMax + 1; p++ ) {
						bool walkable = Walkable(line, p);
						if ( !walkable && start < 0 && Walkable(line, p - 1) ) start = p;
						else if ( walkable && start >= 0 ) {
							runs.Add((line, start, p - start));
							start = -1;
						}
					}
				}
				if ( runs.Count == 0 ) continue;
				// the slab is the most common run length (ties go to the thinner one); a thick one is not a door, but eg rocks
				int slab = runs.GroupBy(r => r.length).OrderByDescending(grp => grp.Count()).ThenBy(grp => grp.Key).First().Key;
				if ( slab > MaxSlabThickness ) continue;
				var kept = runs.Where(r => Math.Abs(r.length - slab) <= 1).ToList();
				if ( kept.Count < MinSlabLines || kept.Count <= bestLines ) continue; // a door is wider than a couple of lines
				bestLines = kept.Count;
				best = new List<Offsets.Vector2i>();
				foreach ( var (line, start, length) in kept ) {
					for ( int p = start; p < start + length; p++ ) {
						best.Add(alongX ? new Offsets.Vector2i(p, line) : new Offsets.Vector2i(line, p));
					}
				}
			}
			return best;
		}
	}
}
