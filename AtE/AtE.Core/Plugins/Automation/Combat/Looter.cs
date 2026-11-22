using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Picks up the items on the ground that show a label (the loot filter lets them through): walk near the nearest,
	/// click its label, and it is gone once picked up. An item whose label is still there after MaxClicks clicks from
	/// close by (or MaxClicks + 1 from anywhere) is taken not to fit in the inventory, and left (blacklisted) until the
	/// next area. Quest items (their item's path has /QuestItems/) are picked up first; one that does not fit is reported
	/// (NoRoomForQuestItem) for the routine to stop for help.
	/// An item clicked with no room for it in the backpack goes on the cursor for a frame, and the game drops it again by
	/// itself, as a new entity (its label goes, as if picked up; seen live, 3.30.0d: picked up again and again, round and
	/// round): its size counts as one that did not go in (see MightFit, CheckCursor).
	/// </summary>
	public class Looter {
		/// <summary>Click the label (the game walks the rest of the way) when within this many cells of the item; walk nearer first when further.</summary>
		public float ClickReach = 35;
		/// <summary>A click from within this many cells that leaves the item on the ground counts toward MaxClicks.</summary>
		public float Reach = 12;
		/// <summary>
		/// The name of a quest item that stayed on the ground after MaxClicks clicks (no room for it), until the routine
		/// takes it (see TakeNoRoomForQuestItem); null if none.
		/// </summary>
		public string NoRoomForQuestItem { get; private set; }
		/// <summary>NoRoomForQuestItem, cleared.</summary>
		public string TakeNoRoomForQuestItem() { var s = NoRoomForQuestItem; NoRoomForQuestItem = null; return s; }

		/// <summary>Is the item on the ground (a WorldItem) a quest item?</summary>
		public static bool IsQuestItem(Entity worldItem) => worldItem?.GetComponent<WorldItem>()?.Item?.Path?.Contains("/QuestItems/") ?? false;

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
		/// Equipment worth picking up: a rare, or anything for a worn slot that is empty. A life or mana flask only if it
		/// recovers more than one of its kind equipped (see FlaskUpgrader, which then puts it in that one's place). Other
		/// items that are not equipment (currency, gems, other flasks, quest items, ...) are always worth it; so is an item
		/// that can't be read.
		/// </summary>
		public static bool RareOrEmptySlot(Entity item) {
			if ( item == null ) return true;
			if ( FlaskUpgrader.TryRecovery(item, out _, out _) ) return FlaskUpgrader.IsUpgrade(item);
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

		/// <summary>Called with an item's metadata path (the item, not the WorldItem) when one clicked on is gone from the ground (picked up).</summary>
		public System.Action<string> PickedUp;
		private uint pickingId; // the last item clicked, until its label is gone
		private string pickingPath;

		/// <summary>After an item's label is gone, how long to look for it on the cursor (no room in the backpack for it).</summary>
		public long CursorCheckMs = 1500;
		private string pickedPath; // the item whose label went last, while it may still show up on the cursor
		private string pickingText, goneText; // the label text of the item clicked last; of the one whose label went last
		private Offsets.Vector2i pickingGrid, goneGrid;
		private uint goneId;
		private long goneAt = -100000;
		/// <summary>A label with the same text as one just gone, this near where it was and this soon, is that item dropped again (no room for it).</summary>
		public float BackAgainCells = 4;
		public long BackAgainMs = 3000;
		private long pickedAt;

		private readonly Dictionary<uint, int> clicks = new Dictionary<uint, int>();
		private readonly Dictionary<uint, int> allClicks = new Dictionary<uint, int>();
		private readonly HashSet<uint> blacklist = new HashSet<uint>();
		// how long each item has been the one worked on (walking to it, clicking it), in this area instance
		private readonly Dictionary<uint, long> workedMs = new Dictionary<uint, long>();
		private long lastWorkAt;
		private uint lastWorked;

		/// <summary>
		/// Leave an item that has been the one worked on for this long in all (walking to it and clicking it, counted across
		/// turns: seen live with a full backpack, it went round the labels of items A, B, C, A... for hours, never reaching
		/// MaxClicks on any one of them).
		/// </summary>
		public long MaxWorkMs = 8000;
		private uint current; // the item being picked up, so it doesn't switch between two at the same distance
		private long lastClick, lastClickWait;
		private uint lastClicked;

		/// <summary>Forget the blacklist (on entering a new area instance).</summary>
		public void Reset() {
			clicks.Clear();
			allClicks.Clear();
			blacklist.Clear();
			workedMs.Clear();
			noRoom.Clear();
			failedSizes.Clear();
			lastWorked = 0;
			lastClicked = 0;
			current = 0;
			pickingId = 0;
			pickedPath = null;
		}

		/// <summary>Where the item to pick up next is (the one being picked up, else the nearest); false if there is none.</summary>
		public bool TryGetTarget(CombatContext ctx, out Offsets.Vector2i grid) {
			CheckPickedUp(); // (asked first each frame, also after the last item is gone)
			CheckCursor();
			var found = Target(ctx);
			grid = found.grid;
			return found.item != null;
		}

		// the item being picked up, if it is still there, else the nearest; quest items first
		private (LabelOnGround label, Entity item, Offsets.Vector2i grid, float distance) Target(CombatContext ctx) {
			var items = Items(ctx).ToList();
			if ( items.Count == 0 ) return default;
			var quest = items.Where(i => IsQuestItem(i.item)).ToList();
			if ( quest.Count > 0 ) items = quest;
			var found = items.FirstOrDefault(i => i.item.Id == current);
			return found.item != null ? found : items.OrderBy(i => i.distance).First();
		}

		/// <summary>Work on picking up the nearest item; false if there is none to pick up.</summary>
		public bool TryLoot(CombatContext ctx) {
			CheckPickedUp();
			CheckCursor();
			var (label, item, grid, d) = Target(ctx);
			if ( item == null ) { current = 0; return false; }
			current = item.Id;
			string name = Ground.LabelText(label.Label) ?? "an item";

			// (time spent on it, however it goes: given up on after MaxWorkMs in all)
			long now = Time.ElapsedMilliseconds;
			if ( lastWorked == item.Id && now - lastWorkAt < 500 ) workedMs[item.Id] = workedMs.GetValueOrDefault(item.Id) + (now - lastWorkAt);
			lastWorked = item.Id;
			lastWorkAt = now;
			if ( workedMs.GetValueOrDefault(item.Id) > MaxWorkMs ) {
				Log($"Looter: {name} was not picked up after {MaxWorkMs / 1000} s on it ({allClicks.GetValueOrDefault(item.Id)} clicks, {d:F0} cells away), leaving it");
				if ( IsQuestItem(item) ) NoRoomForQuestItem = name;
				blacklist.Add(item.Id);
				current = 0;
				return true;
			}

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
			if ( n >= MaxClicks || all >= MaxClicks + 1 ) { // (+1: a click from further away, that never got it close, eg, it can't be walked to)
				// clicked from close by, and still on the ground: no room for it
				Log($"Looter: {name} was not picked up after {all} clicks ({n} from close by), leaving it (no room in the inventory?)");
				// (clicked from close by and still there: no room for its size; anything as big is left from now on, see MightFit)
				if ( n >= MaxClicks ) NoteTooBig(item.GetComponent<WorldItem>()?.Item);
				if ( IsQuestItem(item) ) NoRoomForQuestItem = name;
				blacklist.Add(item.Id);
				current = 0;
				return true;
			}
			if ( ctx.Driver.Send(click) ) {
				lastClick = Time.ElapsedMilliseconds;
				lastClicked = item.Id;
				pickingId = item.Id;
				pickingPath = item.GetComponent<WorldItem>()?.Item?.Path;
				pickingText = name;
				pickingGrid = grid;
				lastClickWait = ClickPace + (long)(ClickPacePerCell * d);
				if ( d <= Reach ) clicks[item.Id] = n + 1;
				allClicks[item.Id] = all + 1;
				if ( all > 0 ) Log($"Looter: clicking {name} again (click {all + 1}, {d:F0} cells away)");
			}
			return true;
		}

		// the item clicked last is gone from the ground: tell PickedUp
		private void CheckPickedUp() {
			if ( pickingId == 0 ) return;
			var labels = GetUI()?.LabelsOnGround?.GetAllLabels();
			if ( labels == null || labels.Any(l => l.Item?.Id == pickingId) ) return;
			uint pickingIdWas = pickingId;
			pickingId = 0;
			pickedPath = pickingPath;
			pickedAt = Time.ElapsedMilliseconds;
			goneText = pickingText; goneGrid = pickingGrid; goneAt = pickedAt; goneId = pickingIdWas;
			// (on the cursor: no room for it, see CheckCursor, looked at now too, as it is there only a frame; not picked up)
			bool onCursor = pickingPath != null && CursorItem()?.Path == pickingPath;
			CheckCursor();
			if ( pickingPath != null && !onCursor ) PickedUp?.Invoke(pickingPath);
		}

		// the item held on the cursor (server side), or null
		private static Entity CursorItem() {
			var igs = PoEMemory.GameRoot?.InGameState;
			if ( igs == null || !igs.HasCursorItem ) return null;
			var a = igs.CursorItem;
			return IsValid(a) ? new Entity() { Address = a } : null;
		}

		// an item clicked on is on the cursor (its label gone CursorCheckMs ago at most): there was no room for it in the
		// backpack (the game drops it again by itself, a frame later, as a new entity): its size is noted (NoteTooBig)
		private void CheckCursor() {
			if ( pickedPath == null ) return;
			if ( Time.ElapsedMilliseconds - pickedAt > CursorCheckMs ) { pickedPath = null; return; }
			var held = CursorItem();
			if ( held == null || held.Path != pickedPath ) return;
			pickedPath = null;
			goneText = null; // (seen here: not again when it is back on the ground)
			Log($"Looter: {Equipment.ItemName(held) ?? held.Path.Split('/').Last()} went on the cursor, not in the backpack (no room for it)");
			NoteTooBig(held);
		}

		// an item that did not go in (left on the ground after clicks from close by, or put on the cursor): anything at
		// least as wide and as tall is left from now on, see MightFit (stackables and gold aside)
		private void NoteTooBig(Entity inner) {
			if ( SizeFree(inner) || !TrySize(inner, out int fw, out int fh) || failedSizes.Any(f => fw >= f.w && fh >= f.h) ) return;
			failedSizes.RemoveAll(f => f.w >= fw && f.h >= fh);
			failedSizes.Add((fw, fh));
			Log($"Looter: a {fw}x{fh} item did not go in: leaving items at least {fw} wide and {fh} tall (too big: {string.Join(", ", failedSizes.Select(f => $"{f.w}x{f.h}"))})");
		}

		// the items left for want of room (logged once each)
		private readonly HashSet<uint> noRoom = new HashSet<uint>();
		// the sizes (w, h) of items that did not go in (clicked from close by, still on the ground), in this area instance
		private readonly List<(int w, int h)> failedSizes = new List<(int, int)>();

		// an item's size in cells (its Base), or false if it can't be read
		private static bool TrySize(Entity item, out int w, out int h) {
			w = h = 0;
			var b = item?.GetComponent<Base>();
			if ( !IsValid(b) ) return false;
			w = b.CellSizeX; h = b.CellSizeY;
			return w > 0 && h > 0 && w <= 5 && h <= 5;
		}

		// stackable (currency and the like: it may go on a stack already held) or gold (not in the backpack): no size rule
		private static bool SizeFree(Entity item) => item?.Path == null || item.Path.Contains("/Gold") || IsValid(item.GetComponent<Stack>());

		/// <summary>
		/// Might item (inside a WorldItem) still go in the backpack? The backpack itself is not read (it is only known while
		/// open); instead, once an item of w x h did not go in, every item at least as wide and as tall is left: eg, a 2x3 bow
		/// failed, so 2x2 and smaller (and 1x4) are still tried; then a 2x2 fails, and 1x3 wands still are; then a 1x3, and
		/// 1x1 rings and the like still are. Stackables and gold are always tried. Forgotten on a new area instance.
		/// </summary>
		public bool MightFit(Entity item) {
			if ( failedSizes.Count == 0 || SizeFree(item) || !TrySize(item, out int w, out int h) ) return true;
			return !failedSizes.Any(f => w >= f.w && h >= f.h);
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
				var inner = item.GetComponent<WorldItem>()?.Item;
				if ( Want != null && !Want(inner) ) continue;
				// (back on the ground as a new entity, where the one just picked up was: it went on the cursor for a moment,
				// no room for it, and the game dropped it again; a backstop to CheckCursor, which may miss that moment)
				if ( goneText != null && item.Id != goneId && Time.ElapsedMilliseconds - goneAt <= BackAgainMs
					&& Ground.LabelText(label.Label) == goneText && Combat.Distance(pos.GridPos, goneGrid) <= BackAgainCells ) {
					Log($"Looter: {goneText} is back on the ground (a new entity, where it was): there was no room for it");
					goneText = null;
					NoteTooBig(inner);
				}
				if ( !MightFit(inner) ) {
					if ( noRoom.Add(item.Id) ) Log($"Looter: {Ground.LabelText(label.Label) ?? "an item"} is too big for the room left (as big as one that did not go in): leaving it");
					continue;
				}
				float d = Combat.Distance(ctx.Grid, pos.GridPos);
				if ( d <= MaxDistance ) yield return (label, item, pos.GridPos, d);
			}
		}
	}
}
