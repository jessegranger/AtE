using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// One row of WorldAreas.dat, as loaded in game memory (see Offsets.WorldAreaDetails).
	/// The rows don't move once loaded, so they are cached by address and the Connections
	/// form a graph of the whole world, for planning a route from one area to another.
	/// </summary>
	public class WorldArea {
		public readonly IntPtr Address;
		public readonly string Id; // eg, "1_1_2"
		public readonly string Name; // eg, "The Coast"
		public readonly int Act;
		public readonly bool IsTown;
		public readonly bool HasWaypoint;
		private readonly IntPtr[] connections;

		private WorldArea(IntPtr address, Offsets.WorldAreaDetails details) {
			Address = address;
			Id = PoEMemory.TryReadString(details.strRawName, Encoding.Unicode, out string id) ? id : null;
			Name = PoEMemory.TryReadString(details.strName, Encoding.Unicode, out string name) ? name : null;
			Act = details.Act;
			IsTown = details.IsTown;
			HasWaypoint = details.HasWaypoint;
			long count = details.ConnectionsCount;
			connections = new IntPtr[count > 0 && count < 32 ? count : 0];
			if ( connections.Length > 0 && PoEMemory.TryRead(details.ptrConnections, connections) != connections.Length * IntPtr.Size ) { // TryRead returns bytes, not items
				connections = new IntPtr[0];
			}
		}

		public IEnumerable<WorldArea> Connections => connections.Select(FromAddress).Where(a => a != null);

		public override string ToString() => $"{Name} ({Id})";

		private static readonly Dictionary<IntPtr, WorldArea> cache = new Dictionary<IntPtr, WorldArea>();
		static WorldArea() {
			PoEMemory.OnAttach += (_, args) => cache.Clear(); // a new process has new addresses
		}

		public static WorldArea FromAddress(IntPtr address) {
			if ( !Offsets.IsValid(address) ) return null;
			if ( cache.TryGetValue(address, out WorldArea area) ) return area;
			if ( !PoEMemory.TryRead(address, out Offsets.WorldAreaDetails details) ) return null;
			area = new WorldArea(address, details);
			if ( area.Id == null ) return null;
			return cache[address] = area;
		}

		/// <summary>
		/// Breadth-first search over Connections, returns the areas to walk through (starting with from, ending at the goal),
		/// or null if no goal is reachable.
		/// </summary>
		public static List<WorldArea> FindRoute(WorldArea from, Func<WorldArea, bool> isGoal) {
			if ( from == null ) return null;
			var cameFrom = new Dictionary<WorldArea, WorldArea>() { { from, null } };
			var queue = new Queue<WorldArea>();
			queue.Enqueue(from);
			while ( queue.Count > 0 ) {
				var area = queue.Dequeue();
				if ( isGoal(area) ) {
					var route = new List<WorldArea>();
					for ( var a = area; a != null; a = cameFrom[a] ) route.Add(a);
					route.Reverse();
					return route;
				}
				foreach ( var next in area.Connections ) {
					if ( !cameFrom.ContainsKey(next) ) {
						cameFrom[next] = area;
						queue.Enqueue(next);
					}
				}
			}
			return null;
		}

		public static List<WorldArea> FindRoute(WorldArea from, string goalName) =>
			FindRoute(from, a => string.Equals(a.Name, goalName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(a.Id, goalName, StringComparison.OrdinalIgnoreCase));
	}

	public static partial class Globals {
		/// <summary>
		/// The area the player is in now, or null.
		/// </summary>
		public static WorldArea GetCurrentArea() =>
			WorldArea.FromAddress(PoEMemory.GameRoot?.InGameState?.WorldData?.AreaRef.Value.ptrToWorldAreaDetails ?? IntPtr.Zero);
	}
}
