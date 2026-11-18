using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// One room of the current Labyrinth run (see Offsets.LabyrinthRoom). The game knows the room's type (eg "OutdoorQuad"),
	/// which can be filled by one of a few areas (Mansion Atrium, Domain Crossing, Estate Crossing); which one is learned by
	/// being in it, or from the labels of the exits that lead to it.
	/// </summary>
	public class LabyrinthRoom {
		public IntPtr Address;
		public string Section; // eg "End_1_BranchN"
		public int SectionIndex; // 1-based, within the section
		public string RoomType; // eg "OutdoorQuad", "Boss1", "FinalBoss2"
		public List<WorldArea> Candidates = new List<WorldArea>(); // the areas that can fill this room
		public List<LabyrinthRoom> Neighbours = new List<LabyrinthRoom>();
		public WorldArea Area; // the area that fills it, once known
		public float MapX, MapY; // its place on the Labyrinth Map panel, within its section (+x right, +y down; see Offsets.LabyrinthSectionLayoutRow)
		public int SectionOrder; // 0 for the first section of the run (Entry), 1 for the next, ...
		/// <summary>Its place on the Labyrinth Map panel across the whole run: MapX shifted right past the sections before it.</summary>
		public Vector2 MapPos;

		internal IntPtr[] neighbourPtrs;

		public bool IsTrial => RoomType != null && RoomType.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0;
		public bool IsFinalTrial => RoomType != null && RoomType.StartsWith("FinalBoss", StringComparison.OrdinalIgnoreCase);

		public IEnumerable<string> CandidateNames => Area != null ? new[] { Area.Name } : Candidates.Select(a => a.Name).Distinct();

		/// <summary>The area's name when known, else the names it may have (eg "Mansion Atrium/Domain Crossing/Estate Crossing").</summary>
		public string DisplayName => string.Join("/", CandidateNames);

		public override string ToString() => $"{DisplayName} ({Section} #{SectionIndex} {RoomType})";

		/// <summary>True if the goal names this room: its area (or any area it may be), its room type or section, or "Izaro" for the final trial.</summary>
		public bool Matches(string goal) {
			bool Eq(string s) => string.Equals(s, goal, StringComparison.OrdinalIgnoreCase);
			return CandidateNames.Any(Eq) || Eq(RoomType) || Eq($"{Section} #{SectionIndex}")
				|| (Area != null && (Eq(Area.Id) || Eq(Area.DisplayName)))
				|| (IsFinalTrial && Eq("Izaro"));
		}
	}

	public static class Labyrinth {

		private static IntPtr cachedHead, cachedTail;
		private static List<LabyrinthRoom> cachedRooms;

		/// <summary>
		/// The rooms of the Labyrinth run the player is in, or null when not in the Labyrinth.
		/// Read once per run (the vector moves when a new run starts); what is learned about them (Area) is kept on them.
		/// </summary>
		private static bool TryReadData(out Offsets.LabyrinthData data) {
			data = default;
			var ptr = PoEMemory.GameRoot?.InGameState?.Data.Value.ptrLabyrinthData ?? IntPtr.Zero;
			return Offsets.IsValid(ptr) && PoEMemory.TryRead(ptr, out data);
		}

		public static List<LabyrinthRoom> Rooms() {
			if ( !TryReadData(out var data) ) return null;
			var handle = data.Rooms;
			if ( handle.Head == cachedHead && handle.Tail == cachedTail && cachedRooms != null ) return cachedRooms;
			var rooms = Read(handle); // not cached when null: outside the Labyrinth this is read again each time, but fails fast
			if ( rooms != null ) {
				cachedHead = handle.Head;
				cachedTail = handle.Tail;
				cachedRooms = rooms;
			}
			return rooms;
		}

		private static List<LabyrinthRoom> Read(Offsets.ArrayHandle handle) {
			const int size = 0x60;
			if ( !Offsets.IsValid(handle.Head) ) return null;
			int count = handle.ItemCount(size);
			if ( count < 1 || count > 64 || (handle.Tail.ToInt64() - handle.Head.ToInt64()) % size != 0 ) return null;
			var nodes = new Offsets.LabyrinthRoom[count];
			if ( PoEMemory.TryRead(handle.Head, nodes) != count * size ) return null;
			var rooms = new List<LabyrinthRoom>();
			for ( int i = 0; i < count; i++ ) {
				var node = nodes[i];
				if ( !PoEMemory.TryRead(node.ptrSectionLayout, out Offsets.LabyrinthSectionLayoutRow layout) ) return null; // not the Labyrinth (or garbage)
				if ( !PoEMemory.TryRead(layout.ptrSection, out IntPtr strSection) || !PoEMemory.TryReadString(strSection, Encoding.Unicode, out string section, 64) ) return null;
				if ( !PoEMemory.TryRead(layout.ptrRoomType, out Offsets.LabyrinthAreasRow type) || !PoEMemory.TryReadString(type.strId, Encoding.Unicode, out string typeId, 64) ) return null;
				var room = new LabyrinthRoom() {
					Address = handle.GetRecordPtr(i, size),
					Section = section,
					SectionIndex = layout.Index,
					RoomType = typeId,
					MapX = layout.MapX,
					MapY = layout.MapY,
					neighbourPtrs = new[] { node.ptrNeighbour0, node.ptrNeighbour1, node.ptrNeighbour2, node.ptrNeighbour3 },
				};
				foreach ( var (n, ptr) in new[] { (type.NormalCount, type.ptrNormal), (type.CruelCount, type.ptrCruel),
						(type.MercilessCount, type.ptrMerciless), (type.EternalCount, type.ptrEternal) } ) {
					if ( n < 1 || n > 32 ) continue;
					var refs = new IntPtr[n * 2]; // (WorldAreaDetails, table) pairs
					if ( PoEMemory.TryRead(ptr, refs) != refs.Length * IntPtr.Size ) continue;
					for ( int k = 0; k < refs.Length; k += 2 ) {
						var area = WorldArea.FromAddress(refs[k]);
						if ( area != null && !room.Candidates.Contains(area) ) room.Candidates.Add(area);
					}
				}
				rooms.Add(room);
			}
			// sections in run order (the vector lists them Entry, Middle, End); each starts right of the one before
			var sections = rooms.Select(r => r.Section).Distinct().ToList();
			float offset = 0;
			foreach ( var section in sections ) {
				var inSection = rooms.Where(r => r.Section == section).ToList();
				foreach ( var r in inSection ) {
					r.SectionOrder = sections.IndexOf(section);
					r.MapPos = new Vector2(offset + r.MapX, r.MapY);
				}
				offset += inSection.Max(r => r.MapX) + 1;
			}
			var byAddress = rooms.ToDictionary(r => r.Address);
			foreach ( var room in rooms ) {
				room.Neighbours = room.neighbourPtrs.Where(p => p != IntPtr.Zero && byAddress.ContainsKey(p)).Select(p => byAddress[p]).Distinct().ToList();
			}
			return rooms;
		}

		/// <summary>
		/// The room the player is in, or null when not in the Labyrinth. Also records the current area as that room's Area.
		/// </summary>
		public static LabyrinthRoom CurrentRoom() {
			var rooms = Rooms();
			if ( rooms == null ) return null;
			if ( !TryReadData(out var data) ) return null;
			var room = rooms.FirstOrDefault(r => r.Address == data.ptrCurrentRoom);
			var here = GetCurrentArea();
			if ( room == null || here == null || !room.Candidates.Contains(here) ) return null; // eg, in Aspirants' Plaza, or not in the Labyrinth
			room.Area = here;
			return room;
		}

		/// <summary>The room the player was in before this one, when it is known (it is noted as each area's terrain loads), else null.</summary>
		public static LabyrinthRoom PreviousRoom { get; private set; }
		private static LabyrinthRoom lastRoom;

		/// <summary>
		/// Called by Terrain each time it loads an area's tiles: note the room change (for PreviousRoom), and mark the room exit the
		/// player stands near as the one they arrived through.
		/// </summary>
		public static void OnTerrainLoaded(List<AreaExit> exits, Vector2 playerGrid) {
			var room = CurrentRoom();
			if ( room == null ) return; // not in the Labyrinth (or the area is not known yet: the terrain loads again after OnAreaChange)
			if ( room != lastRoom ) {
				PreviousRoom = lastRoom != null && room.Neighbours.Contains(lastRoom) ? lastRoom : null;
				lastRoom = room;
			}
			foreach ( var e in exits ) e.IsArrival = false;
			var arrival = RoomExits(exits)
				.Select(e => (e, d: Vector2.Distance(new Vector2(e.GridPos.X, e.GridPos.Y), playerGrid)))
				.OrderBy(t => t.d).FirstOrDefault();
			if ( arrival.e != null && arrival.d < ArrivalReach ) arrival.e.IsArrival = true;
		}

		/// <summary>How near (cells) the player must be to an exit, when the area loads, for it to be the one they arrived through.</summary>
		public const float ArrivalReach = 100;

		/// <summary>
		/// The exits of a Labyrinth room that lead to other rooms: the "transition" tiles, without the seams between two kinds of ground
		/// (eg ElegantHouse/Feature/transition_house_to_drygarden, whose file names have "_to_").
		/// </summary>
		public static IEnumerable<AreaExit> RoomExits(IEnumerable<AreaExit> exits) =>
			exits.Where(e => e.TileFile == null
				|| System.IO.Path.GetFileName(e.TileFile).IndexOf("_to_", StringComparison.OrdinalIgnoreCase) < 0);

		/// <summary>
		/// Name each exit of the room by the room it leads to (AreaExit.LeadsTo), before any is seen, from the Labyrinth Map's order:
		/// going clockwise around the room, the neighbours come in the same order on the map (+y down) as their exits do on screen,
		/// around the exits' centroid. The map is no projection of the room (checked live, 3.30); only this order holds (4 of 4 rooms).
		/// The circle is lined up by an anchor: the exit the player arrived through (it leads to PreviousRoom, or to the Aspirants' Plaza
		/// from the run's first room), or an exit already seen whose label names a single neighbour. Nothing is named when the number of
		/// room exits does not match the number of ways out, or the anchors disagree.
		/// </summary>
		public static void NameExitsByOrder(LabyrinthRoom here, IEnumerable<AreaExit> allExits) {
			if ( here == null ) return;
			var exits = RoomExits(allExits).ToList();
			foreach ( var e in exits ) e.LeadsTo = null;
			var rooms = Rooms();
			bool first = rooms != null && rooms.Count > 0 && rooms[0] == here;
			// the ways out on the map; a null room is the Aspirants' Plaza, left of the run's first room
			var ways = here.Neighbours.Select(r => (room: r, dir: r.MapPos - here.MapPos)).ToList();
			if ( first ) ways.Add((null, new Vector2(-1, 0)));
			if ( ways.Count < 2 || exits.Count != ways.Count ) return;

			float cx = (float)exits.Average(e => e.GridPos.X), cy = (float)exits.Average(e => e.GridPos.Y);
			// grid to screen turns by 45 degrees and flips handedness; the order around a point needs no more of the projection than that
			float ScreenAngle(AreaExit e) {
				float dx = e.GridPos.X - cx, dy = e.GridPos.Y - cy;
				return MathF.Atan2(-(dx + dy), dx - dy); // y down, like the map
			}
			var byScreen = exits.OrderBy(ScreenAngle).ToList();
			var byMap = ways.OrderBy(w => MathF.Atan2(w.dir.Y, w.dir.X)).ToList();
			int n = ways.Count;

			var shifts = new HashSet<int>();
			void Anchor(AreaExit e, int wayIndex) {
				if ( wayIndex >= 0 ) shifts.Add(((wayIndex - byScreen.IndexOf(e)) % n + n) % n);
			}
			var arrival = byScreen.FirstOrDefault(e => e.IsArrival);
			if ( arrival != null ) {
				if ( PreviousRoom != null ) Anchor(arrival, byMap.FindIndex(w => w.room == PreviousRoom));
				else if ( first ) Anchor(arrival, byMap.FindIndex(w => w.room == null));
			}
			foreach ( var e in byScreen.Where(e => e.Seen && e.Destination != null) ) {
				var named = byMap.Select((w, i) => (w, i)).Where(t => t.w.room != null && t.w.room.CandidateNames.Contains(e.Destination)).ToList();
				if ( named.Count == 1 ) Anchor(e, named[0].i);
			}
			if ( shifts.Count != 1 ) return; // no anchor, or they disagree
			int shift = shifts.First();
			for ( int i = 0; i < n; i++ ) byScreen[i].LeadsTo = byMap[(i + shift) % n].room;
		}

		/// <summary>
		/// Learn which areas fill the neighbouring rooms from the labels of the exits (an exit labelled "Mansion Halls" leads to the
		/// only neighbour that can be Mansion Halls).
		/// </summary>
		public static void LearnFromExits(LabyrinthRoom here, IEnumerable<AreaExit> exits) {
			if ( here == null ) return;
			NameExitsByOrder(here, exits);
			foreach ( var e in exits.Where(e => e.Seen && e.Destination != null && e.LeadsTo != null && e.LeadsTo.Area == null) ) {
				// the room this exit leads to is filled by the area on its label
				e.LeadsTo.Area = e.LeadsTo.Candidates.FirstOrDefault(a => a.Name == e.Destination);
			}
			foreach ( var name in exits.Select(e => e.Destination).Where(d => d != null).Distinct() ) {
				var fits = here.Neighbours.Where(n => n.Area == null && n.Candidates.Any(a => a.Name == name)).ToList();
				if ( fits.Count == 1 && !here.Neighbours.Any(n => n.Area?.Name == name) ) {
					fits[0].Area = fits[0].Candidates.First(a => a.Name == name);
				}
			}
		}

		/// <summary>
		/// Breadth-first search over the rooms, from the current one; returns the rooms to walk through (starting with the current one,
		/// ending at the nearest that matches the goal, see LabyrinthRoom.Matches), or null.
		/// </summary>
		public static List<LabyrinthRoom> FindRoute(string goal) {
			var from = CurrentRoom();
			if ( from == null || string.IsNullOrWhiteSpace(goal) ) return null;
			goal = goal.Trim();
			var cameFrom = new Dictionary<LabyrinthRoom, LabyrinthRoom>() { { from, null } };
			var queue = new Queue<LabyrinthRoom>();
			queue.Enqueue(from);
			while ( queue.Count > 0 ) {
				var room = queue.Dequeue();
				if ( room != from && room.Matches(goal) ) {
					var route = new List<LabyrinthRoom>();
					for ( var r = room; r != null; r = cameFrom[r] ) route.Add(r);
					route.Reverse();
					return route;
				}
				foreach ( var next in room.Neighbours ) {
					if ( !cameFrom.ContainsKey(next) ) {
						cameFrom[next] = room;
						queue.Enqueue(next);
					}
				}
			}
			return null;
		}

		/// <summary>
		/// The exit (of the current room's) that leads to the neighbouring room next, or null if none of the known exits is labelled
		/// with a name it can have. When two neighbours could have the same name, the exit is only taken once next's area is known.
		/// </summary>
		public static AreaExit ExitTo(LabyrinthRoom here, LabyrinthRoom next, IEnumerable<AreaExit> exits) {
			if ( here == null || next == null ) return null;
			LearnFromExits(here, exits);
			AreaExit byLabel;
			if ( next.Area != null ) byLabel = exits.FirstOrDefault(e => e.Destination == next.Area.Name);
			else {
				var names = next.CandidateNames.ToHashSet();
				var others = here.Neighbours.Where(n => n != next).SelectMany(n => n.CandidateNames).ToHashSet();
				byLabel = exits.FirstOrDefault(e => e.Destination != null && names.Contains(e.Destination) && !others.Contains(e.Destination));
			}
			return byLabel ?? exits.FirstOrDefault(e => e.LeadsTo == next); // else by the map's order (see NameExitsByOrder)
		}
	}
}
