using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Picks up the items on the ground that show a label (the loot filter lets them through): walk near the nearest,
	/// click its label, and it is gone once picked up. An item whose label is still there after MaxClicks clicks from
	/// close by is taken not to fit in the inventory, and left (blacklisted) until the next area.
	/// </summary>
	public class Looter {
		/// <summary>Click the label when within this many cells of the item.</summary>
		public float Reach = 12;
		/// <summary>Leave items further than this many cells away.</summary>
		public float MaxDistance = 80;
		/// <summary>Clicks on one item's label, from within Reach, before giving up on it.</summary>
		public int MaxClicks = 2;
		/// <summary>Time for a click to pick the item up (or not) before the next click.</summary>
		public long ClickPace = 1000;

		/// <summary>Leave an item when this is true of its label text (eg, an objective will pick it up, and see it go).</summary>
		public System.Func<string, bool> Leave;

		/// <summary>What it is doing, for the status line.</summary>
		public string Status { get; private set; } = "";

		private readonly Dictionary<uint, int> clicks = new Dictionary<uint, int>();
		private readonly HashSet<uint> blacklist = new HashSet<uint>();
		private uint current; // the item being picked up, so it doesn't switch between two at the same distance
		private long lastClick;

		/// <summary>Forget the blacklist (on entering a new area instance).</summary>
		public void Reset() {
			clicks.Clear();
			blacklist.Clear();
			current = 0;
		}

		/// <summary>Where the item to pick up next is (the one being picked up, else the nearest); false if there is none.</summary>
		public bool TryGetTarget(CombatContext ctx, out Offsets.Vector2i grid) {
			var found = Target(ctx);
			grid = found.grid;
			return found.item != null;
		}

		// the item being picked up, if it is still there, else the nearest
		private (LabelOnGround label, Entity item, Offsets.Vector2i grid, float distance) Target(CombatContext ctx) {
			var items = Items(ctx).ToList();
			if ( items.Count == 0 ) return default;
			var found = items.FirstOrDefault(i => i.item.Id == current);
			return found.item != null ? found : items.OrderBy(i => i.distance).First();
		}

		/// <summary>Work on picking up the nearest item; false if there is none to pick up.</summary>
		public bool TryLoot(CombatContext ctx) {
			var (label, item, grid, d) = Target(ctx);
			if ( item == null ) { current = 0; return false; }
			current = item.Id;
			string name = Ground.LabelText(label.Label) ?? "an item";

			if ( d > Reach ) {
				var goal = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, grid, out var spot) ? spot : grid;
				Status = $"walking to {name}, {d:F0} cells away";
				ctx.Driver.WalkTo(goal, Status);
				return true;
			}
			ctx.Driver.StopWalking("picking up an item");
			Status = $"picking up {name}";
			if ( Time.ElapsedMilliseconds - lastClick < ClickPace ) return true;
			clicks.TryGetValue(item.Id, out int n);
			if ( n >= MaxClicks ) {
				// clicked, and still on the ground: no room for it
				Log($"Looter: {name} was not picked up after {n} clicks, leaving it (no room in the inventory?)");
				blacklist.Add(item.Id);
				current = 0;
				return true;
			}
			if ( Ground.TryClickLabel(label) ) {
				lastClick = Time.ElapsedMilliseconds;
				clicks[item.Id] = n + 1;
			}
			return true;
		}

		// the items on the ground with a label showing, not blacklisted, within MaxDistance
		private IEnumerable<(LabelOnGround label, Entity item, Offsets.Vector2i grid, float distance)> Items(CombatContext ctx) {
			var labels = GetUI()?.LabelsOnGround?.GetAllLabels();
			if ( labels == null ) yield break;
			foreach ( var label in labels ) {
				var item = label.Item;
				if ( item == null || !(item.Path?.Contains("/WorldItem") ?? false) || blacklist.Contains(item.Id) ) continue;
				var pos = item.GetComponent<Positioned>();
				if ( !IsValid(pos) || (Leave?.Invoke(Ground.LabelText(label.Label) ?? "") ?? false) ) continue;
				float d = Combat.Distance(ctx.Grid, pos.GridPos);
				if ( d <= MaxDistance ) yield return (label, item, pos.GridPos, d);
			}
		}
	}
}
