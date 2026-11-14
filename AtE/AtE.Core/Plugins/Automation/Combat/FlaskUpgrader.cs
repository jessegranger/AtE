using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Life and mana flasks bigger than one equipped: the looter picks only those up (see IsUpgrade), and this puts one in
	/// the place of the smallest equipped flask of its kind, then drops the old one on the ground. With the inventory
	/// open: click the new flask in the backpack (it is on the cursor once it has left the backpack), click the old
	/// flask's slot (they swap: the new one is in the slot, the old one on the cursor), click the player (it drops).
	/// "Bigger" is the base type's recovery amount (FlaskData; eg, a Greater Mana Flask 120 mana, a Small one 50).
	/// </summary>
	public class FlaskUpgrader {
		/// <summary>Time between clicks (for each to land, and the inventory to show it).</summary>
		public long Pace = 500;
		/// <summary>Give up on a swap that takes longer than this (and leave that flask for CooldownMs).</summary>
		public long MaxMs = 10000;
		public long CooldownMs = 60000;

		/// <summary>What it is doing, for the status line.</summary>
		public string Status { get; private set; } = "";
		/// <summary>True between its first click and the old flask being dropped (the inventory must stay open).</summary>
		public bool Busy => phase != Phase.None;

		private enum Phase { None, Lifting, Swapping, Dropping }
		private Phase phase;
		private long started, lastClick, cooldownUntil, clickedNewAt, lastWaitLog, expectUntil, lastExpectOpen;
		private string expectPath;
		/// <summary>After a bigger flask is picked up, look for it in the backpack this long (opening the inventory, so it is read fresh).</summary>
		public long ExpectMs = 6000;
		private string newPath, oldPath;
		private int slot, backpackCountBefore;

		/// <summary>A life or mana flask's kind ("Life" or "Mana") and base recovery amount; false for any other item (hybrids included).</summary>
		public static bool TryRecovery(Entity item, out string kind, out int amount) => TryRecovery(item?.Path, out kind, out amount);
		public static bool TryRecovery(string path, out string kind, out int amount) {
			kind = null; amount = 0;
			string last = path?.Split('/').Last();
			if ( last == null || !FlaskData.Records.TryGetValue(last, out var data) ) return false;
			if ( last.StartsWith("FlaskLife") ) { kind = "Life"; amount = data.HealAmount; }
			else if ( last.StartsWith("FlaskMana") ) { kind = "Mana"; amount = data.ManaAmount; }
			return kind != null;
		}

		/// <summary>The equipped flask of this kind with the smallest recovery (its slot 0 to 4, and amount); slot -1 if none is equipped.</summary>
		public static (int slot, int amount, Entity item) SmallestEquipped(string kind) {
			var best = (slot: -1, amount: int.MaxValue, item: (Entity)null);
			for ( int i = 0; i < 5; i++ ) {
				var e = GetFlask(i)?.realEnt;
				if ( TryRecovery(e, out var k, out var a) && k == kind && a < best.amount ) best = (i, a, e);
			}
			return best;
		}

		/// <summary>Is the item a life or mana flask that recovers more than one of its kind equipped.</summary>
		public static bool IsUpgrade(Entity item) =>
			TryRecovery(item, out var kind, out var amount) && SmallestEquipped(kind) is var s && s.slot >= 0 && amount > s.amount;

		/// <summary>Forget a swap in progress (eg, a new area).</summary>
		public void Reset() { phase = Phase.None; expectPath = null; }

		/// <summary>
		/// An item was just picked up (by its metadata path): if it is a life or mana flask bigger than one equipped, it is
		/// looked for in the backpack with the inventory open (the backpack read with it closed may not show it yet).
		/// </summary>
		public void PickedUp(string path) {
			if ( !TryRecovery(path, out var kind, out var amount) || SmallestEquipped(kind) is var s && (s.slot < 0 || amount <= s.amount) ) return;
			expectPath = path;
			expectUntil = Time.ElapsedMilliseconds + ExpectMs;
			Log($"FlaskUpgrader: picked up {path.Split('/').Last()}, bigger than the {Equipment.ItemName(s.item)} in flask slot {s.slot + 1}");
		}

		/// <summary>Work on putting a bigger flask from the backpack in place of a smaller one; false if there is none to (and it is not busy).</summary>
		/// <param name="safe">No monsters near: start a swap only then (one started is finished).</param>
		public bool Tick(CombatContext ctx, bool safe) {
			long now = Time.ElapsedMilliseconds;
			if ( phase == Phase.None ) {
				if ( now < cooldownUntil ) return false;
				if ( !TryFindUpgrade(out var found, out int s, out var old) ) {
					// just picked up: open the inventory to see it there
					if ( expectPath == null ) return false;
					if ( now > expectUntil ) {
						Log($"FlaskUpgrader: {expectPath.Split('/').Last()} was not seen in the backpack (with the inventory open: {BackpackIsOpen()})");
						expectPath = null;
						return false;
					}
					if ( !safe ) return false;
					ctx.Driver.StopWalking(Status = "looking for the new flask in the backpack");
					if ( !BackpackIsOpen() && now - lastExpectOpen > 1000 && ctx.Driver.Send(Inputs.Press(VKey.I)) ) lastExpectOpen = now;
					return true;
				}
				expectPath = null;
				if ( !safe ) {
					if ( now - lastWaitLog > 5000 ) { lastWaitLog = now; Log($"FlaskUpgrader: the {Equipment.ItemName(found.Entity)} waits: monsters near"); }
					return false;
				}
				newPath = found.Entity.Path;
				oldPath = old.Path;
				slot = s;
				started = now;
				lastClick = clickedNewAt = 0;
				phase = Phase.Lifting;
				Log($"FlaskUpgrader: putting the {Equipment.ItemName(found.Entity)} in flask slot {slot + 1}, in place of the {Equipment.ItemName(old)}");
			}
			if ( now - started > MaxMs ) {
				Log($"FlaskUpgrader: gave up {phase} after {MaxMs} ms (an item may be left on the cursor)");
				cooldownUntil = now + CooldownMs;
				phase = Phase.None;
				return false;
			}
			ctx.Driver.StopWalking(Status = "upgrading a flask");
			if ( now - lastClick < Pace ) return true;
			if ( !BackpackIsOpen() ) {
				Status = "opening the inventory";
				if ( ctx.Driver.Send(Inputs.Press(VKey.I)) ) lastClick = now;
				return true;
			}
			var panel = GetUI()?.InventoryPanel;
			var flasks = panel?.Flasks;
			if ( flasks == null ) { Status = "can't find the flask slots"; return true; }
			switch ( phase ) {
				case Phase.Lifting: {
					int inBackpack = CountInBackpack(newPath);
					if ( clickedNewAt == 0 ) {
						var item = BackpackItems().FirstOrDefault(i => i.Entity?.Path == newPath);
						if ( item == null ) { Log("FlaskUpgrader: the new flask is not in the backpack"); phase = Phase.None; return false; }
						backpackCountBefore = inBackpack;
						Status = "picking up the new flask";
						if ( ctx.Driver.Send(Inputs.Click(item.GetClientRect())) ) lastClick = clickedNewAt = now;
						return true;
					}
					// on the cursor: it left the backpack
					if ( inBackpack >= backpackCountBefore ) {
						Status = "waiting for the new flask to be on the cursor";
						if ( now - clickedNewAt > 3 * Pace ) clickedNewAt = 0; // (click it again)
						return true;
					}
					Status = $"putting it in flask slot {slot + 1}";
					if ( ctx.Driver.Send(Inputs.Click(EquipFlask.SlotRect(flasks.GetClientRect(), slot))) ) { lastClick = now; phase = Phase.Swapping; }
					return true;
				}
				case Phase.Swapping: {
					// swapped: the new one is in the slot (and the old one on the cursor)
					if ( GetFlask(slot)?.realEnt?.Path != newPath ) { Status = "waiting for the swap"; return true; }
					var player = GetPlayer();
					if ( player == null || !TryWorldToScreen(Position(player), out Vector2 at) ) { Status = "can't find the player on screen"; return true; }
					Status = "dropping the old flask";
					if ( ctx.Driver.Send(Inputs.Click(at)) ) { lastClick = now; phase = Phase.Dropping; }
					return true;
				}
				case Phase.Dropping:
					Log($"FlaskUpgrader: flask slot {slot + 1} now holds {newPath.Split('/').Last()} (dropped {oldPath.Split('/').Last()})");
					phase = Phase.None;
					return true;
			}
			return true;
		}

		private static int CountInBackpack(string path) => GetUI()?.InventoryPanel?.Backpack?.Items.Count(i => i?.Entity?.Path == path) ?? 0;

		// the backpack flask that recovers the most more than the smallest equipped one of its kind
		private static bool TryFindUpgrade(out InventoryItem item, out int slot, out Entity old) {
			item = null; slot = -1; old = null;
			int gain = 0;
			foreach ( var i in GetUI()?.InventoryPanel?.Backpack?.Items ?? Enumerable.Empty<InventoryItem>() ) {
				if ( !TryRecovery(i?.Entity, out var kind, out var amount) ) continue;
				var s = SmallestEquipped(kind);
				if ( s.slot < 0 || amount - s.amount <= gain ) continue;
				gain = amount - s.amount;
				item = i; slot = s.slot; old = s.item;
			}
			return item != null;
		}
	}
}
