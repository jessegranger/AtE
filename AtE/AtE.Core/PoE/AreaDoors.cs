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
		/// <summary>The door's entity, or 0 for a door known only from its tile (see FromTile).</summary>
		public uint EntityId;
		/// <summary>
		/// Known only from a door tile (AreaDoors.FromTiles), its entity not seen yet: replaced by the entity's door once that
		/// comes into range, or dropped if no door entity shows up there when the player is near enough to have it loaded.
		/// </summary>
		public bool FromTile => EntityId == 0;
		/// <summary>The .tdt of the door's tile, for a door FromTile; else null.</summary>
		public string TileFile;
		/// <summary>The entity's metadata path (eg, "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Door_Closed").</summary>
		public string Path;
		public Offsets.Vector2i GridPos;
		/// <summary>
		/// A door that clicking does not open: something else (a lever, see AreaLever) unlocks it. The game says nothing about
		/// which lever opens which door (3.29, checked live in a Trial of Ascendancy: no reference either way, see AreaLever).
		/// </summary>
		public bool IsLocked => AreaDoors.IsLockedDoorPath(Path);
		/// <summary>The inclusive box of cells the door may block (from TriggerableBlockage).</summary>
		public Offsets.Vector2i GridMin, GridMax;
		public bool IsClosed;
		/// <summary>The cells that are blocked in Terrain while the door is closed, and become walkable when it opens.</summary>
		public List<Offsets.Vector2i> Passage = new List<Offsets.Vector2i>();
		/// <summary>True once Passage was read from the game's grid after the blocker opened (rather than guessed).</summary>
		public bool PassageIsExact;
		/// <summary>When it was first seen open (Time.ElapsedMilliseconds), or 0.</summary>
		public long OpenSince;
		/// <summary>
		/// When it closed after being seen open (Time.ElapsedMilliseconds), or 0: what an arena's gates do when an
		/// encounter starts (see ArenaLock). A door closed since it was first seen has 0.
		/// </summary>
		public long ClosedSince;
		/// <summary>When its entity was first found missing while the player was near enough to have it loaded, or 0.</summary>
		public long MissingSince;

		/// <summary>The door's entity, if it is still in range.</summary>
		public Entity GetEntity() => GetEntities().FirstOrDefault(e => e.Id == EntityId);

		/// <summary>
		/// The door's label on the screen (eg, "Door"), which is what to click to open it; null if not shown.
		/// </summary>
		public Element GetLabel() =>
			GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Id == EntityId)?.Label;

		public override string ToString() => $"Door {(FromTile ? "(tile)" : EntityId.ToString())} at {GridPos} ({(IsClosed ? "closed" : "open")}{(IsLocked ? ", locked" : "")}, {Passage.Count} passage cells)";
	}

	/// <summary>
	/// A lever that unlocks a locked door (AreaDoor.IsLocked) somewhere nearby, used once.
	///
	/// 3.29, checked live in The Crypt's Trial of Ascendancy (a Puzzle_Parts/Door_Closed and a Puzzle_Parts/Switch_Once):
	/// neither entity refers to the other (no entity id, no shared object, nothing in the tiles), so which lever opens which
	/// door is the server's business. Pulling the lever took both its Transitionable.State and the door's from 1 to 2,
	/// cleared both Targetable.IsTargetable, and cleared the door's TriggerableBlockage.IsClosed; a Puzzle_Parts/Switch_Multi
	/// next to the door did not change and did not open it.
	/// </summary>
	public class AreaLever {
		public uint EntityId;
		public string Path;
		public Offsets.Vector2i GridPos;
		/// <summary>Already pulled (Transitionable.State moved on from 1, or no longer targetable).</summary>
		public bool IsUsed;

		public Entity GetEntity() => GetEntities().FirstOrDefault(e => e.Id == EntityId);

		/// <summary>The lever's label on the screen (eg, "Lever"), which is what to click to pull it; null if not shown.</summary>
		public Element GetLabel() =>
			GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Id == EntityId)?.Label;

		/// <summary>Read IsUsed from the entity now, if it is in range.</summary>
		public void Refresh() {
			var ent = GetEntity();
			if ( IsValid(ent) ) IsUsed = AreaDoors.IsLeverUsed(ent);
		}

		public override string ToString() => $"Lever {EntityId} at {GridPos} ({(IsUsed ? "used" : "unused")})";
	}

	public static class AreaDoors {

		/// <summary>Entity paths (substrings) of doors that a lever unlocks, rather than a click.</summary>
		public static string[] LockedDoorPaths = { "/Puzzle_Parts/Door_Closed" };
		/// <summary>Entity paths (substrings) of the levers that unlock them.</summary>
		public static string[] LeverPaths = { "/Puzzle_Parts/Switch_Once" };

		public static bool IsLockedDoorPath(string path) => path != null && LockedDoorPaths.Any(p => path.Contains(p));
		public static bool IsLeverPath(string path) => path != null && LeverPaths.Any(p => path.Contains(p));

		public static bool IsLeverUsed(Entity ent) {
			var t = ent.GetComponent<Transitionable>();
			if ( IsValid(t) && t.State > 1 ) return true;
			var target = ent.GetComponent<Targetable>();
			return IsValid(target) && !target.IsTargetable;
		}

		/// <summary>Add (or refresh) the levers in range now.</summary>
		public static void UpdateLevers(List<AreaLever> levers) {
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				var lever = levers.FirstOrDefault(l => l.EntityId == ent.Id);
				if ( lever == null ) {
					string path = ent.Path;
					if ( !IsLeverPath(path) ) continue;
					var pos = ent.GetComponent<Positioned>();
					if ( !IsValid(pos) ) continue;
					lever = new AreaLever() { EntityId = ent.Id, Path = path, GridPos = pos.GridPos };
					levers.Add(lever);
				}
				lever.IsUsed = IsLeverUsed(ent);
			}
		}

		/// <summary>
		/// Add (or refresh) the doors in range now. Returns true if a new passage was found (so navigation data must be rebuilt).
		/// </summary>
		/// <summary>
		/// A door whose entity is missing while the player is this close (in cells) is gone (eg, a statue spawner in The
		/// Imperial Gardens is removed once its statues come alive), so its record is dropped rather than left closed forever.
		/// Entities stay loaded much farther away than this (over 200 cells).
		/// </summary>
		public static float VanishedRadius = 100f;
		/// <summary>How long a nearby door's entity must stay missing before the door is dropped.</summary>
		public static long VanishedAfterMs = 1000;

		public static bool UpdateFromEntities(Terrain terrain, List<AreaDoor> doors) {
			bool newPassage = false;
			var seen = new HashSet<uint>();
			int entities = 0;
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				entities += 1;
				var blockage = ent.GetComponent<TriggerableBlockage>();
				if ( !IsValid(blockage) ) continue;
				seen.Add(ent.Id);
				var door = doors.FirstOrDefault(d => d.EntityId == ent.Id);
				if ( door == null ) {
					var pos = ent.GetComponent<Positioned>();
					if ( !IsValid(pos) ) continue;
					door = new AreaDoor() {
						EntityId = ent.Id,
						Path = ent.Path,
						GridPos = pos.GridPos,
						GridMin = blockage.GridMin,
						GridMax = blockage.GridMax,
					};
					door.Passage = FindPassage(terrain, door.GridMin, door.GridMax);
					doors.Add(door);
					newPassage |= door.Passage.Count > 0;
					// the door the tile promised: the entity's door takes its place
					int replaced = doors.RemoveAll(d => d.FromTile && TileBoxContains(d, door.GridPos));
					if ( replaced > 0 ) newPassage = true;
				}
				// a blocker whose box is only set when it closes (3.30, confirmed live: the PitGates of a pit arena in the
				// Mountain tileset have GridMin = GridMax = <0, 0> while open, and their box when they close): take the new
				// box, and find its slab again (below)
				var min = blockage.GridMin; var max = blockage.GridMax;
				if ( (min.Id != 0 || max.Id != 0) && (min.Id != door.GridMin.Id || max.Id != door.GridMax.Id) ) {
					Log($"Door {door.EntityId}: its box is now {min}..{max} (was {door.GridMin}..{door.GridMax})");
					door.GridMin = min;
					door.GridMax = max;
					door.Passage = new List<Offsets.Vector2i>();
					door.PassageIsExact = false;
					newPassage = true;
				}
				bool wasOpen = door.OpenSince != 0;
				door.IsClosed = blockage.IsClosed;
				if ( !door.IsClosed ) door.ClosedSince = 0;
				else if ( wasOpen && door.ClosedSince == 0 ) {
					door.ClosedSince = Time.ElapsedMilliseconds;
					Log($"Door {door.EntityId} at {door.GridPos}: closed again");
				}
				// closed, but no slab in the grid as read when the area loaded: the game may have stamped it in since
				if ( door.IsClosed && door.Passage.Count == 0 && terrain.MergeLiveBlocked(door.GridMin, door.GridMax) ) {
					door.Passage = FindPassage(terrain, door.GridMin, door.GridMax);
					Log($"Door {door.EntityId}: blocked in the game's grid since the area loaded, {door.Passage.Count} passage cells");
					newPassage = true; // (the grid changed: the Navigator re-reads it)
				}
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
				door.MissingSince = 0;
			}
			if ( entities > 0 ) newPassage |= DropVanished(doors, seen); // (no entities read: can't tell gone from not loaded)
			return newPassage;
		}

		/// <summary>
		/// Drop the doors whose entity has been missing for a while although the player is close enough to have it loaded.
		/// Returns true if any were dropped (so navigation data must be rebuilt).
		/// </summary>
		private static bool DropVanished(List<AreaDoor> doors, HashSet<uint> seen) {
			var player = GetPlayer()?.GetComponent<Positioned>();
			if ( !IsValid(player) ) return false;
			var at = player.GridPos;
			long now = Time.ElapsedMilliseconds;
			int dropped = doors.RemoveAll(d => {
				if ( seen.Contains(d.EntityId) ) return false;
				float dx = d.GridPos.X - at.X, dy = d.GridPos.Y - at.Y;
				if ( dx * dx + dy * dy > VanishedRadius * VanishedRadius ) { d.MissingSince = 0; return false; }
				if ( d.MissingSince == 0 ) { d.MissingSince = now; return false; }
				if ( now - d.MissingSince < VanishedAfterMs ) return false;
				Log(d.FromTile ? $"Door from tile {d.TileFile} at {d.GridPos}: no door entity there, dropped"
					: $"Door {d.EntityId} at {d.GridPos}: its entity is gone ({(d.IsClosed ? "closed" : "open")} when last seen), dropped");
				return true;
			});
			return dropped > 0;
		}

		/// <summary>
		/// Tile paths (substrings, any case) cut from a door's .tdt, checked one by one: 3.30, The Labyrinth's prison rooms,
		/// "Metadata/Terrain/Prison/Features/dungeon_prison_door_v01_01.tdt". Not a general "_door_v": the Labyrinth's garden
		/// rooms have dozens of "garden_*_path_door" tiles that are open crossings, where the slab finder takes the ends of
		/// the walls for doors (and would let paths through the walls); "dungeon_prison_doorway_only" has no door; and some
		/// "door" tiles are exits (eg, "labyrinth_dry_garden_door"), which are never doors here (see IsDoorTile).
		/// </summary>
		public static string[] DoorTilePaths = { "/dungeon_prison_door_v" };

		public static bool IsDoorTile(string path) =>
			path != null
			&& DoorTilePaths.Any(p => path.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
			&& !AreaExits.IsTransitionTile(path);

		/// <summary>
		/// The doors in the area's door tiles (IsDoorTile) that are closed in the grid as read when the area loaded. A door's
		/// entity only comes into range when the player is near it, and until then its slab cuts the walkable grid in two, so
		/// the far side of every door looks unreachable; these let the Navigator plan through them meanwhile.
		/// </summary>
		public static List<AreaDoor> FromTiles(Offsets.TerrainData data, Terrain terrain) {
			const int cellsPerTile = Offsets.TerrainData.CellsPerTile;
			var result = new List<AreaDoor>();
			foreach ( var tile in AreaExits.FromTiles<AreaFeature>(data, terrain, IsDoorTile) ) {
				var min = new Offsets.Vector2i(tile.TileMinX * cellsPerTile, tile.TileMinY * cellsPerTile);
				var max = new Offsets.Vector2i(((tile.TileMaxX + 1) * cellsPerTile) - 1, ((tile.TileMaxY + 1) * cellsPerTile) - 1);
				var passage = FindPassage(terrain, min, max);
				if ( passage.Count == 0 ) continue; // open when the area loaded (or its slab is only stamped when the entity arrives)
				var center = new Offsets.Vector2i((int)passage.Average(c => c.X), (int)passage.Average(c => c.Y));
				result.Add(new AreaDoor() {
					TileFile = tile.TileFile,
					GridPos = center,
					GridMin = min,
					GridMax = max,
					IsClosed = true,
					Passage = passage,
				});
			}
			return result;
		}

		private static bool TileBoxContains(AreaDoor door, Offsets.Vector2i pos) =>
			pos.X >= door.GridMin.X && pos.X <= door.GridMax.X && pos.Y >= door.GridMin.Y && pos.Y <= door.GridMax.Y;

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
