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
		/// <summary>Click the label (the game walks the rest of the way) when within this many cells of the item; walk nearer first when further.</summary>
		public float ClickReach = 35;
		/// <summary>A click from within this many cells that leaves the item on the ground counts toward MaxClicks.</summary>
		public float Reach = 12;
		/// <summary>Leave items further than this many cells away.</summary>
		public float MaxDistance = 80;
		/// <summary>Clicks on one item's label, from within Reach, before giving up on it.</summary>
		public int MaxClicks = 2;
		/// <summary>Time for a click on an item to pick it up before clicking it again: this, plus ClickPacePerCell for each cell it was away.</summary>
		public long ClickPace = 400;
		public long ClickPacePerCell = 25;

		/// <summary>Leave an item when this is true of its label text (eg, an objective will pick it up, and see it go).</summary>
		public System.Func<string, bool> Leave;

		/// <summary>Pick up an item only when this is true of it (the item itself, inside the WorldItem; null if it can't be read). Null: every item.</summary>
		public System.Func<Entity, bool> Want;

		/// <summary>
		/// Equipment worth picking up: a rare, or anything for a worn slot that is empty. Items that are not equipment
		/// (currency, gems, flasks, quest items, ...) are always worth it; so is an item that can't be read.
		/// </summary>
		public static bool RareOrEmptySlot(Entity item) {
			if ( item == null ) return true;
			var slots = SlotsFor(item.Path);
			if ( slots == null ) return true; // not equipment
			var mods = item.GetComponent<Mods>();
			if ( IsValid(mods) && (mods.Rarity == Offsets.ItemRarity.Rare || mods.Rarity == Offsets.ItemRarity.Unique) ) return true;
			var worn = Equipment.WornSlots().ToDictionary(s => s.name, s => s.inventory);
			// a slot that can't be read counts as empty
			return slots.Any(s => !worn.TryGetValue(s, out var inv) || !inv.Items.Any(i => i?.Entity != null));
		}

		// the worn slots an item goes in (by its metadata path); null if it is not equipment
		private static string[] SlotsFor(string path) {
			if ( path == null ) return null;
			if ( path.Contains("/Items/Armours/Helmets/") ) return new[] { "Helm" };
			if ( path.Contains("/Items/Armours/BodyArmours/") ) return new[] { "Chest" };
			if ( path.Contains("/Items/Armours/Gloves/") ) return new[] { "Gloves" };
			if ( path.Contains("/Items/Armours/Boots/") ) return new[] { "Boots" };
			if ( path.Contains("/Items/Armours/Shields/") || path.Contains("/Items/Quivers/") ) return new[] { "RWeapon" };
			if ( path.Contains("/Items/Weapons/") ) return new[] { "LWeapon" };
			if ( path.Contains("/Items/Amulets/") ) return new[] { "Amulet" };
			if ( path.Contains("/Items/Rings/") ) return new[] { "LRing", "RRing" };
			if ( path.Contains("/Items/Belts/") ) return new[] { "Belt" };
			return null;
		}

		/// <summary>What it is doing, for the status line.</summary>
		public string Status { get; private set; } = "";

		private readonly Dictionary<uint, int> clicks = new Dictionary<uint, int>();
		private readonly Dictionary<uint, int> allClicks = new Dictionary<uint, int>();
		private readonly HashSet<uint> blacklist = new HashSet<uint>();
		private uint current; // the item being picked up, so it doesn't switch between two at the same distance
		private long lastClick, lastClickWait;
		private uint lastClicked;

		/// <summary>Forget the blacklist (on entering a new area instance).</summary>
		public void Reset() {
			clicks.Clear();
			allClicks.Clear();
			blacklist.Clear();
			lastClicked = 0;
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

			// (a click on a label walks there and picks it up, so it is clicked from a distance, and the next item is
			// clicked as soon as the last one is gone; only the same item waits for the walk before it is clicked again)
			var click = d <= ClickReach ? Ground.ClickLabel(label) : null;
			if ( click == null ) {
				if ( d <= Reach ) { Status = $"picking up {name} (its label can't be clicked)"; ctx.Driver.StopWalking(Status); return true; }
				var goal = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, grid, out var spot) ? spot : grid;
				Status = $"walking to {name}, {d:F0} cells away";
				ctx.Driver.WalkTo(goal, Status);
				return true;
			}
			Status = $"picking up {name}";
			if ( item.Id == lastClicked && Time.ElapsedMilliseconds - lastClick < lastClickWait ) return true;
			ctx.Driver.StopWalking(Status);
			clicks.TryGetValue(item.Id, out int n);
			allClicks.TryGetValue(item.Id, out int all);
			if ( n >= MaxClicks || all >= MaxClicks + 4 ) { // (+4: clicks from further away, that never got it close, eg, it can't be walked to)
				// clicked from close by, and still on the ground: no room for it
				Log($"Looter: {name} was not picked up after {n} clicks, leaving it (no room in the inventory?)");
				blacklist.Add(item.Id);
				current = 0;
				return true;
			}
			if ( ctx.Driver.Send(click) ) {
				lastClick = Time.ElapsedMilliseconds;
				lastClicked = item.Id;
				lastClickWait = ClickPace + (long)(ClickPacePerCell * d);
				if ( d <= Reach ) clicks[item.Id] = n + 1;
				allClicks[item.Id] = all + 1;
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
				if ( Want != null && !Want(item.GetComponent<WorldItem>()?.Item) ) continue;
				float d = Combat.Distance(ctx.Grid, pos.GridPos);
				if ( d <= MaxDistance ) yield return (label, item, pos.GridPos, d);
			}
		}
	}
}
