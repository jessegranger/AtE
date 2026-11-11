using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Where the gems are in the worn items, against where the SocketSolver's arrangement of the gem plan wants them (see
	/// InventoryPlans.PlanSocketGems, which moves them).
	/// </summary>
	public static class GemArrangement {

		/// <summary>
		/// The gem placements still to do, each with the socket to put it in: for the SocketSolver's arrangement of the
		/// worn items, those whose gem is had (in the backpack or a worn socket) and not in its link group yet. A gem in the
		/// right link group, but not in the socket the solver chose, is left there (another socket of the group is used).
		/// </summary>
		public static List<(string slot, int socket, PobGem gem)> Targets(GemPlan plan, int level) {
			var taken = Satisfied(plan.SolveWorn(level), out var todo);
			var result = new List<(string, int, PobGem)>();
			foreach ( var p in todo ) {
				if ( !Has(p.Gem) ) continue;
				var sockets = WornItem(p.Item.Slot)?.Entity?.GetComponent<Sockets>();
				if ( !IsValid(sockets) ) continue;
				var gemColor = GemPlan.ColorOf(p.Gem);
				// the socket chosen, if free; else another of the link group, empty first, of the gem's colour first
				var free = sockets.LinkedWith(p.Socket).Where(s => !taken.Contains((p.Item.Slot, s)))
					.OrderBy(s => s == p.Socket ? 0 : 1).ThenBy(s => sockets.Gem(s) == null ? 0 : 1).ThenBy(s => sockets.Color(s) == gemColor ? 0 : 1).ToList();
				if ( free.Count == 0 ) continue;
				taken.Add((p.Item.Slot, free[0]));
				result.Add((p.Item.Slot, free[0], p.Gem));
			}
			return result;
		}

		/// <summary>The gems in worn sockets that are not where the arrangement wants them.</summary>
		public static IEnumerable<(string slot, int socket, Entity gem)> Misplaced(GemPlan plan, int level) {
			var satisfied = Satisfied(plan.SolveWorn(level), out _);
			return SocketedGems().Where(s => !satisfied.Contains((s.slot, s.socket)));
		}

		// the sockets holding a gem the arrangement wants in that link group; and the placements not done so
		private static HashSet<(string slot, int socket)> Satisfied(SocketArrangement arrangement, out List<GemPlacement> todo) {
			var used = new HashSet<(string, int)>();
			todo = new List<GemPlacement>();
			foreach ( var p in arrangement.Placements.OrderBy(p => p.Group) ) {
				var sockets = WornItem(p.Item.Slot)?.Entity?.GetComponent<Sockets>();
				int found = -1;
				if ( IsValid(sockets) ) {
					found = sockets.LinkedWith(p.Socket).Where(s => !used.Contains((p.Item.Slot, s)) && GemPlan.Matches(p.Gem, sockets.Gem(s)))
						.OrderBy(s => s == p.Socket ? 0 : 1).DefaultIfEmpty(-1).First();
				}
				if ( found >= 0 ) used.Add((p.Item.Slot, found));
				else todo.Add(p);
			}
			return used;
		}

		/// <summary>Is the gem in the backpack, or a worn socket?</summary>
		public static bool Has(PobGem gem) =>
			BackpackItems().Any(i => GemPlan.Matches(gem, i.Entity)) || SocketedGems().Any(s => GemPlan.Matches(gem, s.gem));

		/// <summary>The gems in the worn items' sockets, and where.</summary>
		public static IEnumerable<(string slot, int socket, Entity gem)> SocketedGems() {
			foreach ( var (slot, inventory) in Equipment.WornSlots() ) {
				var sockets = inventory.Items.FirstOrDefault(i => i?.Entity != null)?.Entity?.GetComponent<Sockets>();
				if ( !IsValid(sockets) ) continue;
				for ( int i = 0; i < sockets.Count; i++ ) {
					if ( sockets.Gem(i) is Entity gem ) yield return (slot, i, gem);
				}
			}
		}

		/// <summary>The item worn in slot (eg, "LWeapon"), or null.</summary>
		public static InventoryItem WornItem(string slot) =>
			slot == null ? null : Equipment.WornSlots().FirstOrDefault(s => s.name == slot).inventory?.Items.FirstOrDefault(i => i?.Entity != null);

		/// <summary>A worn slot's box on screen (empty if not found).</summary>
		public static RectangleF SlotRect(string slot) =>
			Equipment.WornSlots().FirstOrDefault(s => s.name == slot).inventory?.GetClientRect() ?? RectangleF.Empty;
	}
}
