using System;
using System.Collections.Generic;
using System.Linq;
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

		/// <summary>
		/// Learn which areas fill the neighbouring rooms from the labels of the exits (an exit labelled "Mansion Halls" leads to the
		/// only neighbour that can be Mansion Halls).
		/// </summary>
		public static void LearnFromExits(LabyrinthRoom here, IEnumerable<AreaExit> exits) {
			if ( here == null ) return;
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
			if ( next.Area != null ) return exits.FirstOrDefault(e => e.Destination == next.Area.Name);
			var names = next.CandidateNames.ToHashSet();
			var others = here.Neighbours.Where(n => n != next).SelectMany(n => n.CandidateNames).ToHashSet();
			return exits.FirstOrDefault(e => e.Destination != null && names.Contains(e.Destination) && !others.Contains(e.Destination));
		}
	}
}
