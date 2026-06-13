using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE.Plugins {
	public class BackpackPlugin : PluginBase {

		public override string Name => "Backpack";

		public HotKey DumpKey = new HotKey(Keys.OemOpenBrackets);
		public bool ShowDebugMarkers = false;
		public bool OpenStashedDecks = false;
		public bool ApplyIncubators = false;

		public bool ShouldIdentify = true;
		public bool ShouldIdentifyMaps = true;
		public bool ShouldIdentifyCorrupt = false;
		public bool ShouldIdentifyUnique = false;

		public override void Render() {
			base.Render();

			ImGui_HotKeyButton("Deposit All Loot", ref DumpKey);
			ImGui.Checkbox("Open Stashed Decks", ref OpenStashedDecks);
			ImGui.Checkbox("Apply Incubators", ref ApplyIncubators);
			ImGui.Separator();
			ImGui.Checkbox("Use Scroll of Wisdom to Identify", ref ShouldIdentify);
			ImGui.Indent();
			ImGui.Checkbox("Identify Maps", ref ShouldIdentifyMaps);
			ImGui.Checkbox("Identify Corrupt", ref ShouldIdentifyCorrupt);
			ImGui.Checkbox("Idenfity Unique", ref ShouldIdentifyUnique);
			ImGui.Unindent();
			ImGui.Separator();
			ImGui.Checkbox("Show Debug Markers", ref ShowDebugMarkers);
		}

		private uint InputLatency => (uint)GetPlugin<CoreSettings>().InputLatency;

		private IState RightClickItem(InventoryItem item, IState next = null) => IsValid(item) ? new RightClickAt(Center(item.GetClientRect()), InputLatency, next) : null;
		private IState LeftClickItem(InventoryItem item, uint count = 1, IState next = null) => IsValid(item) ? new LeftClickAt(Center(item.GetClientRect()), InputLatency, count, next) : null;

		public IState PlanUseItemFromBackpack(string usePath, IState next = null, IState fail = null) {
			var useItem = BackpackItems().Where(i => i.Entity?.Path?.Equals(usePath) ?? false).FirstOrDefault();
			if ( !IsValid(useItem) ) {
				Notify($"Could not find any {usePath} to use.");
				return fail;
			}
			return OnlyIfBackpackIsOpen(RightClickItem(useItem, next), ifClosed: fail);
		}

		private IState OnlyIfStashIsOpen(IState ifOpen, IState ifClosed = null) => NewState("CheckStashIsOpen", (self, dt) => BackpackIsOpen() && StashIsOpen() ? ifOpen : ifClosed, next: ifOpen);
		private IState OnlyIfBackpackIsOpen(IState ifOpen, IState ifClosed = null) => NewState("CheckBackpackIsOpen", (self, dt) => BackpackIsOpen() ? ifOpen : ifClosed, next: ifOpen);

		private IEnumerable<InventoryItem> ItemsToIdentify() => BackpackItems().Where(e => {
			if( ! ShouldIdentify ) {
				return false;
			}
			var mods = e?.Entity?.GetComponent<Mods>();
			if ( !IsValid(mods) ) {
				return false;
			}
			if( mods.Rarity == Offsets.ItemRarity.Unique && ! ShouldIdentifyUnique) {
				return false;
			}
			bool isCorrupted = IsCorrupted(e);
			if( isCorrupted && ! ShouldIdentifyCorrupt ) {
				return false;
			}
			var level = GetItemLevel(mods);
			if ( mods.Rarity == Offsets.ItemRarity.Rare && level >= 50 && level <= 74 ) {
				return false; // dont identify the chaos recipe range
			}
			if ( IsIdentified(mods) ) {
				return false;
			}
			return true;
		});
		private IState PlanIdentifyAll(IState next = null) {
			var ui = GetUI();
			if ( IsValid(ui) ) {
				var itemsToIdentify = ItemsToIdentify().ToArray();
				if ( itemsToIdentify.Length == 0 ) {
					// no work to do
					return next;
				}
				// the real plan is:
				// right click a wisdom scroll
				// keydown shift
				// left click on each item to identify
				// keyup shift
				IState head = PlanUseItemFromBackpack(Offsets.PATH_SCROLL_WISDOM,
					new Delay(InputLatency,
					new KeyDown(Keys.LShiftKey,
					new Delay(InputLatency,
					null))));
				if ( head == null ) {
					return null;
				}
				IState tail = head.Tail();
				IState keyUp = new KeyUp(Keys.LShiftKey, next);
				foreach ( var item in itemsToIdentify ) {
					tail.Next = OnlyIfBackpackIsOpen(
						ifOpen: new LeftClickAt(item.GetClientRect(), InputLatency, 1,
							new Delay(InputLatency,
							null)),
						ifClosed: keyUp);
					tail = tail.Tail();
				}
				tail.Next = keyUp;
				return head;
			}
			return next;
		}

		// items in here are left in the backpack, and not stashed
		private Dictionary<string, int> restockNeeds = new Dictionary<string, int>() {
			{ Offsets.PATH_SCROLL_WISDOM, 40 },
			{ Offsets.PATH_SCROLL_PORTAL, 40 },
			{ Offsets.PATH_VAAL_ORB, 20 },
			// { Offsets.PATH_OMEN_OF_RETURN, 1 },
			// { Offsets.PATH_OMEN_OF_DEATH_DANCING, 1 },
			{ Offsets.PATH_OMEN_OF_AMELIORATION, 1 }
		};

		private IState PlanStashAll(IState next = null) {
			var ui = GetUI();
			if ( !IsValid(ui) ) {
				return null;
			}
			// make a copy to modify, as needs are satisfied (aka, left in backpack, not stashed) they can be removed from this dict
			var needs = new Dictionary<string, int>(restockNeeds);
			// then, construct a chain of steps for stashing each item
			// first, hold Control
			IState head = new KeyDown(Keys.LControlKey, null);
			IState tail = head.Tail();
			foreach ( var item in BackpackItems() ) {
				var ent = item.Entity;
				if ( !IsValid(ent) ) {
					continue;
				}
				if ( needs.TryGetValue(ent.Path, out int needed) && needed > 0 ) {
					needs[ent.Path] -= ent.GetComponent<Stack>()?.CurSize ?? 1;
					continue;
				}
				if ( OpenStashedDecks && ent.Path.Equals(Offsets.PATH_STACKED_DECK) ) {
					continue;
				}
				if ( ApplyIncubators && ent.Path.StartsWith(Offsets.PATH_INCUBATOR_PREFIX) ) {
					continue;
				}
				tail.Next = OnlyIfStashIsOpen(
					ifOpen: LeftClickItem(item, 1, null),
					ifClosed: new KeyUp(Keys.LControlKey, null)
				);
				tail = tail.Tail();
			}
			tail.Next = new KeyUp(Keys.LControlKey, next);
			head = OnlyIfStashIsOpen(head, ifClosed: NewState(() => Notify("Not open")));
			/*
			IState cursor = head;
			while( cursor != null ) {
				Log($"Final plan: {cursor.Name}");
				cursor = cursor.Next;
			}
			*/
			return head;
		}

		/// <summary>
		/// ExpectedItem is a filler item used to mark backpack slots as full,
		/// when we know they are or will be very soon (concurrency is hard without locks)
		/// </summary>
		private class ExpectedItem : InventoryItem {
			public ExpectedItem(int x, int y, int w, int h):base() {
				_x = x; _y = y; _w = w; _h = h;
				Address = IntPtr.Zero;
			}
			private int _x; public override int X => _x;
			private int _y; public override int Y => _y;
			private int _w; public override int Width => _w;
			private int _h; public override int Height => _h;
		}
		private const uint BACKPACK_WIDTH = 12;
		private const uint BACKPACK_HEIGHT = 5;
		// This 2D array tracks which InventoryItem is located where
		private static InventoryItem[,] inventoryMap = new InventoryItem[BACKPACK_WIDTH,BACKPACK_HEIGHT];
		// Fill the inventory map with references in the right slots for this one item
		private static void MarkOccupied(InventoryItem item) {
			if ( item == null ) return; // dont use IsValid here because we pass in fake items on purpose
			int x = item.X;
			int y = item.Y;
			int w = item.Width;
			int h = item.Height;
			if ( x < 0 || x >= BACKPACK_WIDTH || y < 0 || y >= BACKPACK_HEIGHT || w > 5 || h > 5 ) return;
			for(uint i = 0; i < w; i++ ) {
				for(uint j = 0; j < h; j++ ) {
					inventoryMap[x + i, y + j] = item;
				}
			}
		}

		private static void RefreshBackpack() {
			inventoryMap = new InventoryItem[BACKPACK_WIDTH, BACKPACK_HEIGHT];
			foreach ( var item in BackpackItems() ) {
				MarkOccupied(item);
			}
		}
		public bool IsOccupied(int x, int y, int w, int h) {
			if ( x < 0 || x >= BACKPACK_WIDTH || y < 0 || y >= BACKPACK_HEIGHT ) {
				return true; // anything out of bounds is occupied
			}
			int ex = x + w;
			int ey = y + h;
			if ( ex > BACKPACK_WIDTH|| ey > BACKPACK_HEIGHT ) { // any rect that overflows the edge is occupied
				return true;
			}
			for ( int dy = y; dy < ey; dy++ ) {
				for ( int dx = x; dx < ex; dx++ ) {
					if ( inventoryMap[dx, dy] != null ) {
						return true;
					}
				}
			}
			return false;
		}
		private static Vector2 GetSlotPositionAbsolute(int x, int y) {
			var elem = GetUI()?.InventoryPanel?.Backpack;
			if( !IsValid(elem) ) {
				return Vector2.Zero;
			}
			RectangleF rect = elem.GetClientRect(); // applies zoom/scale from UI settings
			Vector2 topLeft = new Vector2(rect.X, rect.Y);
			float tileWidth = rect.Width / BACKPACK_WIDTH;
			float tileHeight = rect.Height / BACKPACK_HEIGHT;
			return topLeft + new Vector2(tileWidth * (x + 0.5f), tileHeight * (y + 0.5f));
		}
		// returns an {X, Y} vector of slot indices
		// so, X is 0-11 and Y is 0-5
		public Vector2 GetFreeSlot(int w, int h) {
			for( int x = 0; x < BACKPACK_WIDTH; x++ ) {
				for( int y = 0; y < BACKPACK_HEIGHT; y++ ) {
					if( !IsOccupied(x, y, w, h) ) {
						return new Vector2(x, y);
					}
				}
			}
			return new Vector2(-1, -1);
		}
		public Vector2 GetSlotCenter(Offsets.Vector2i slot) {
			return GetSlotPositionAbsolute(slot.X, slot.Y);
		}

		private IState PlanOpenDeck(InventoryItem deckItem, IState next) {
			var deckPos = deckItem.GetClientRect();
			var stackSize = deckItem.Entity?.GetComponent<Stack>()?.CurSize ?? 0;
			if( stackSize < 1 ) {
				return next;
			}
			return next;
		}

		private IState PlanOpenOneDeck(InventoryItem deckItem, IState next) {
			if ( !BackpackIsOpen() ) {
				Log("OpenOneDeck: canceled, backpack is not open.");
				return next;
			}
			RefreshBackpack();
			// var freeSlot = GetFreeSlot(1, 1);
			// if ( freeSlot == Vector2.Zero ) {
			// return next; // no more free slots
			// }
			var stackSize = deckItem.Entity?.GetComponent<Stack>()?.CurSize ?? 0;
			if ( stackSize <= 0 ) {
				Log($"OpenOneDeck: canceled, stack is empty {stackSize}.");
				return next;
			}
			Log($"OpenOneDeck: Opening a stack of {stackSize} cards...");
			IState head = new State.Empty();
			IState tail = head.Tail();
			while ( stackSize > 0 ) {
				var freeSlot = GetFreeSlot(1, 1);
				if ( freeSlot.X < 0 || freeSlot.Y < 0 || freeSlot.X >= BACKPACK_WIDTH || freeSlot.Y >= BACKPACK_HEIGHT ) {
					Log($"OpenOneDeck: no more free slots");
					tail.Next = next;
					return head;
				}
				Log($"OpenOneDeck: marking occupied {freeSlot.X},{freeSlot.Y}");
				// mark it so future calls to GetFreeSlot dont return the same slot
				MarkOccupied(new ExpectedItem((int)freeSlot.X, (int)freeSlot.Y, 1, 1));
				// get the screen position to click the free slot
				var slotPos = GetSlotPositionAbsolute((int)freeSlot.X, (int)freeSlot.Y);
				if ( slotPos != Vector2.Zero ) {
					Log($"OpenOneDeck: appending one step to the plan");
					// append one step: right click, left click
					tail.Next = OnlyIfBackpackIsOpen(
						ifOpen: new RightClickAt(deckItem.GetClientRect(), InputLatency, OnlyIfBackpackIsOpen(
							ifOpen: new LeftClickAt(slotPos, InputLatency, 1, null),
							ifClosed: null)
						),
						ifClosed: null
					);
					tail = tail.Tail();
				}
				stackSize -= 1;
			}
			Log($"OpenOneDeck: returning established plan...");

			IState debug = head;
			while( debug != null ) {
				Log($"OpenOneDeck: then {debug.Name}");
				debug = debug.Next;
			}
			Log($"OpenOneDeck: modifying step {tail.Name} setting .Next = next");
			tail.Tail().Next = next;
			return head;
		}

		private IState PlanOpenAllDecks(IState next) {
			if( ! OpenStashedDecks ) {
				return next;
			}
			// find a deck, find a free slot
			// right click the deck, left click the free slot
			// repeat
			return NewState((nextDeck, dt) => {
				RefreshBackpack();
				// find a deck
				var deckItem = BackpackItems().FirstOrDefault(i =>
					i.Entity?.Path?.Equals(Offsets.PATH_STACKED_DECK) ?? false);
				if ( !IsValid(deckItem) ) {
					return next; // no more decks
				}
				if ( deckItem.X < 0 || deckItem.X > 11 ) {
					Notify($"Invalid deck position data: XY = {deckItem.X}, {deckItem.Y}");
					return next; // invalid position
				}
				if ( deckItem.Y < 0 || deckItem.Y > 5 ) {
					Notify($"Invalid deck position data: XY = {deckItem.X}, {deckItem.Y}");
					return next; // invalid position
				}
				Notify($"Next Deck at {deckItem.X},{deckItem.Y} {deckItem.Width}x{deckItem.Height} path: {deckItem.Entity.Path}");
				return PlanOpenOneDeck(deckItem, new Delay(100, nextDeck));
			});
		}

		public static IEnumerable<InventoryItem> EquippedItems() {
			var root = GetUI()?.InventoryPanel;
			if ( !IsValid(root) ) yield break;
			yield return root.Helm?.VisibleItems.FirstOrDefault();
			yield return root.Amulet?.VisibleItems.FirstOrDefault();
			yield return root.Chest?.VisibleItems.FirstOrDefault();
			yield return root.LWeapon?.VisibleItems.FirstOrDefault();
			yield return root.RWeapon?.VisibleItems.FirstOrDefault();
			yield return root.LRing?.VisibleItems.FirstOrDefault();
			yield return root.RRing?.VisibleItems.FirstOrDefault();
			yield return root.Gloves?.VisibleItems.FirstOrDefault();
			yield return root.Belt?.VisibleItems.FirstOrDefault();
			yield return root.Boots?.VisibleItems.FirstOrDefault();
			yield return root.Trinket?.VisibleItems.FirstOrDefault();
		}

		private bool FilterIncubatableEquipmentItem(InventoryItem item) {
			if ( !IsValid(item) ) return false;
			var ent = item.Entity;
			if ( !IsValid(ent) ) return false;
			// cannot incubate into transformed items (like Energy Blade)
			if ( ent.HasComponent("Transformed") ) return false;
			var mods = ent.GetComponent<Mods>();
			if ( !IsValid(mods) ) return false;
			if ( mods.HasIncubator ) return false;
			return true;
		}

		private IState PlanIncubateAll(IState next) {
			if ( !BackpackIsOpen() ) {
				return null;
			}

			if ( !ApplyIncubators ) {
				return next;
			}
			var incubatable = new Stack<InventoryItem>(EquippedItems()
				.Where(FilterIncubatableEquipmentItem));
			var incubators = BackpackItems().Where((ent) => ent?.Entity?.Path?.StartsWith(Offsets.PATH_INCUBATOR_PREFIX) ?? false).ToArray();

			Log($"IncubateAll: Incubators: {string.Join(" ", incubators.Select(i => i.Entity.Path.Split('/').Last()))}"); 
			Log($"IncubateAll: Incubatable Equipment: {string.Join(" ", incubatable.Select(i => i.Entity.Path.Split('/').Last()))}");
			if( incubatable.Count > 0 && incubators.Count() > 0 ) {
				IState start = NewState(() => { }, null);
				IState tail = start;
				uint inputLatency = (uint)GetPlugin<CoreSettings>().InputLatency;
				foreach(var item in incubators) {
					var ent = item.Entity;
					int count = ent.GetComponent<Stack>()?.CurSize ?? 0;
					Log($"Step: {ent.Path.Split('/').Last()} ({count})");
					var itemRect = item.GetClientRect();
					while ( count > 0 && incubatable.Count > 0 ) {
						tail = tail.Tail();
						var target = incubatable.Pop();
						tail.Next = OnlyIfBackpackIsOpen(
							NewState(() => Notify($"Incubating into {target?.Entity?.Path?.Split('/').Last() ?? "null"}"),
							new RightClickAt(item?.GetClientRect() ?? RectangleF.Empty, inputLatency,
							new LeftClickAt(target?.GetClientRect() ?? RectangleF.Empty, inputLatency, 1,
							null))),
							ifClosed: null
						);
						count -= 1;
					}
				}
				tail.Tail().Next = next;
				return start;
			}
			Log($"IncubateAll: {incubators.Count()} / {incubatable.Count}");
			return next;
		}

		public override IState OnTick(long dt) {
			if( Enabled && !Paused && PoEMemory.IsAttached ) {

				if( ShowDebugMarkers ) {
					RefreshBackpack();
					if ( BackpackIsOpen() ) {
						for ( int x = 0; x < BACKPACK_WIDTH; x++ ) {
							for ( int y = 0; y < BACKPACK_HEIGHT; y++ ) {
								DrawCircle(GetSlotPositionAbsolute(x, y), 6,
									IsOccupied(x, y, 1, 1) ? Color.Red : Color.Gray);
							}
						}
						var freeSlot = GetFreeSlot(1, 1);
						freeSlot = GetSlotPositionAbsolute((int)freeSlot.X, (int)freeSlot.Y);
						DrawCircle(freeSlot, 8, Color.Yellow);

					}

					var incubatable = BackpackItems().Where(IsValid).ToList();
					ImGui.Begin("Debug BackpackItems");
					ImGui.Text($"Items in backpack: {incubatable.Count}");
					foreach(var item in incubatable) {
						var ent = item.Entity;
						var mods = ent.GetComponent<Mods>();
						var stack = ent.GetComponent<Stack>();
						ImGui.Text($"Item #{ent.Id} [ {ent.Path} ]");
						ImGui_Object($"Mods##{ent.Id}", $"Mods##{ent.Id}", mods, new HashSet<int>());
						ImGui_Object($"Stack##{ent.Id}", $"Stack##{ent.Id}", stack, new HashSet<int>());
					}
					ImGui.End();


					var equipped = new Stack<InventoryItem>(EquippedItems()
						.Where(FilterIncubatableEquipmentItem));
					ImGui.Begin("Debug EquippedItems");
					ImGui.Text($"(Incubatable) Items Equipped: {equipped.Count}");
					foreach ( var item in equipped ) {
						var ent = item.Entity;
						ImGui.Text($"Item #{ent.Id} [ {ent.Path} ]");
						ImGui.SameLine();
						if( ImGui.Button($"B##Browse_{ent.Address.ToInt64()}") ) {
							Run_ObjectBrowser($"Item at 0x{item.Address:X}", item);
						}
					}
					ImGui.End();
				}

				if ( PoEMemory.TargetHasFocus && DumpKey.IsReleased ) {
					Notify("Depositing all your loot.");
					Run(PlanIdentifyAll(PlanStashAll(PlanOpenAllDecks(PlanIncubateAll(null)))));
					return this;
				}

			}
			return this;
		}


	}
}
