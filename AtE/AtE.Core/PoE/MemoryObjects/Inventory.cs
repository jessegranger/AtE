using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// This is the root of Elements that hold the worn equipment,
	/// and items carried in the backpack.
	/// </summary>
	public class InventoryRoot : Element {
		public readonly Cached<Offsets.Element_InventoryRoot> Details;
		public readonly Cached<Offsets.InventoryArray> Inventories;
		public InventoryRoot() : base() {
			Details = CachedStruct<Offsets.Element_InventoryRoot>(() => Address);
			Inventories = new Cached<Offsets.InventoryArray>(() => Details.Value.InventoryList);
		}

		// public Inventory None => IsValid(Inventories.Value.None) ? new Inventory() { Address = Inventories.Value.None } : null;
		public Inventory Helm => IsValid(Inventories.Value.Helm) ? new Inventory() { Address = Inventories.Value.Helm } : null;
		public Inventory Amulet => IsValid(Inventories.Value.Amulet) ? new Inventory() { Address = Inventories.Value.Amulet } : null;
		public Inventory Chest => IsValid(Inventories.Value.Chest) ? new Inventory() { Address = Inventories.Value.Chest } : null;
		public Inventory LWeapon => IsValid(Inventories.Value.LWeapon) ? new Inventory() { Address = Inventories.Value.LWeapon } : null;
		public Inventory RWeapon => IsValid(Inventories.Value.RWeapon) ? new Inventory() { Address = Inventories.Value.RWeapon } : null;
		public Inventory LWeaponSwap => IsValid(Inventories.Value.LWeaponSwap) ? new Inventory() { Address = Inventories.Value.LWeaponSwap } : null;
		public Inventory RWeaponSwap => IsValid(Inventories.Value.RWeaponSwap) ? new Inventory() { Address = Inventories.Value.RWeaponSwap } : null;
		public Inventory LRing => IsValid(Inventories.Value.LRing) ? new Inventory() { Address = Inventories.Value.LRing } : null;
		public Inventory RRing => IsValid(Inventories.Value.RRing) ? new Inventory() { Address = Inventories.Value.RRing } : null;
		public Inventory Gloves => IsValid(Inventories.Value.Gloves) ? new Inventory() { Address = Inventories.Value.Gloves } : null;
		public Inventory Belt => IsValid(Inventories.Value.Belt) ? new Inventory() { Address = Inventories.Value.Belt } : null;
		public Inventory Boots => IsValid(Inventories.Value.Boots) ? new Inventory() { Address = Inventories.Value.Boots } : null;
		public Inventory Backpack => IsValid(Inventories.Value.Backpack) ? new Inventory() { Address = Inventories.Value.Backpack } : null;
		public Inventory LeagueBackpack => IsValid(Inventories.Value.LeagueBackpack) ? new Inventory() { Address = Inventories.Value.LeagueBackpack } : null;
		public Inventory Flasks => IsValid(Inventories.Value.Flask) ? new Inventory() { Address = Inventories.Value.Flask } : null;
		public Inventory Trinket => IsValid(Inventories.Value.Trinket) ? new Inventory() { Address = Inventories.Value.Trinket } : null;
		public Inventory LeveledGems => IsValid(Inventories.Value.LeveledGems) ? new Inventory() { Address = Inventories.Value.LeveledGems } : null;
		public Inventory LWeaponSwapTabPanel => IsValid(Inventories.Value.LWeaponSwapTabPanel) ? new Inventory() { Address = Inventories.Value.LWeaponSwapTabPanel } : null;
		public Inventory RWeaponSwapTabPanel => IsValid(Inventories.Value.RWeaponSwapTabPanel) ? new Inventory() { Address = Inventories.Value.RWeaponSwapTabPanel } : null;
	}

	// most inventories by default contain InventoryItem objects
	public class Inventory : Inventory<InventoryItem> { }


	public class Inventory<T> : Element where T : InventoryItem {
		// public Cached<Offsets.Element_Inventory> Details;
		// public Inventory() : base() => Details = CachedStruct<Offsets.Element_Inventory>(() => Address);

		/// <summary>
		/// This is the generic slow way that all Inventory nodes can use.
		/// More specific code will be needed for specific panels, Maps, Exotic currency, etc
		/// </summary>
		public IEnumerable<T> VisibleItems {
			get {
				// get the constructor of <T> that accepts an IntPtr address
				var ctor = typeof(T).GetConstructor(new Type[] { typeof(IntPtr) });
				return AllVisibleChildren(this, new HashSet<int>())
					.Where(e => Globals.IsValid(e))
					.Select(e => (T)ctor.Invoke(new object[] { e.Address })) //  eg, new InventoryItem() { Address = e.Address })
					.Where(e => Globals.IsValid(e) && e.IsVisible);
			}
		}

		/// <summary>
		/// The items in it, whether or not its panel is open (like VisibleItems, without asking the parents' visibility:
		/// a closed inventory panel keeps its items, only the panel is hidden).
		/// </summary>
		public IEnumerable<T> Items {
			get {
				var ctor = typeof(T).GetConstructor(new Type[] { typeof(IntPtr) });
				return AllVisibleChildren(this, new HashSet<int>())
					.Where(e => Globals.IsValid(e))
					.Select(e => (T)ctor.Invoke(new object[] { e.Address }))
					.Where(e => Globals.IsValid(e));
			}
		}
	
		private static IEnumerable<Element> AllVisibleChildren(Element cursor, HashSet<int> seen) {
			int index = 0;
			int hashCode = 0;
			foreach ( Element child in cursor.Children ) {
				if( ! IsValid(child) ) {
					continue;
				}
				hashCode = child.GetHashCode();
				if ( !seen.Contains(hashCode) ) {
					seen.Add(hashCode);
					if ( IsValid(child) && child.IsVisibleLocal ) {
						yield return child;
						foreach ( var grandchild in AllVisibleChildren(child, seen) ) {
							yield return grandchild;
						}
					}
				}
				index += 1;
			}
		}
	}

	public static partial class Globals {
		public static bool IsValid(InventoryItem item) => item != null && IsValid((Element)item) && item.IsValid;
		public static bool IsIdentified(InventoryItem item) => IsValid(item) && IsIdentified(item.Entity);
		public static bool IsIdentified(Entity item) => IsValid(item) && (item.GetComponent<Mods>()?.IsIdentified ?? true);
		public static bool IsIdentified(Mods mods) => mods?.IsIdentified ?? true;  // an item with null mods is considered identified by default
		public static bool IsCorrupted(InventoryItem item) => IsValid(item) && IsCorrupted(item.Entity);
		public static bool IsCorrupted(Entity item) => IsValid(item) && (item.GetComponent<Base>()?.IsCorrupted ?? true);

		/// <summary>
		/// The items in the backpack, whether or not the inventory panel is open (3.30, seen live: with the panel closed, its
		/// root is not visible but the backpack's item elements are still there, each visible on its own, so Items finds
		/// them; VisibleItems, which asks every parent, found none). Clicking one still needs the panel open.
		/// </summary>
		public static IEnumerable<InventoryItem> BackpackItems(bool includeLeagueBackpack = false) {
			var ui = GetUI();
			if( !IsValid(ui) ) {
				yield break;
			}
			var panel = ui.InventoryPanel;
			if( !IsValid(panel) ) {
				yield break;
			}
			if ( includeLeagueBackpack ) {
				foreach ( var item in panel.LeagueBackpack?.Items.Take(20) ?? Empty<InventoryItem>() ) {
					if ( IsValid(item) ) {
						yield return item;
					}
				}
			}
			foreach ( var item in panel.Backpack?.Items.Take(60) ?? Empty<InventoryItem>() ) {
				if( IsValid(item) ) {
					yield return item;
				}
			}
		}
		public static IEnumerable<StashItem> StashItems() => IsValid(GetUI()?.StashElement?.Address ?? IntPtr.Zero)
			? new Inventory<StashItem>() { Address = GetUI().StashElement.Address }.VisibleItems
			: Empty<StashItem>();

		public static bool BackpackIsOpen() => GetUI()?.InventoryPanel?.IsVisibleLocal ?? false;
		public static bool StashIsOpen() => GetUI()?.StashElement?.IsVisibleLocal ?? false;
	}

	public class InventoryItem : Element {
		public Cached<Offsets.Element_InventoryItem> Details;
		public InventoryItem() : base() => Details = CachedStruct<Offsets.Element_InventoryItem>(() => Address);
		public InventoryItem(IntPtr address) : base() {
			Address = address;
			Details = CachedStruct<Offsets.Element_InventoryItem>(() => Address);
		}
		public override bool IsValid {
			get {
				var details = Details.Value;
				return base.IsValid
					&& IsValid(details.entItem)
					// && details.Width > 0
					// && details.Height > 0
					// && details.InventPosition.X >= 0
					// && details.InventPosition.Y >= 0
					&& EntityCache.TryGetEntity(details.entItem, out _) //  IsValid(new Entity() { Address = details.entItem })
					;
			}
		}

		public Entity Entity => IsValid(Address) && EntityCache.TryGetEntity(Details.Value.entItem, out Entity ent) ? ent : null;

		public virtual int X => (int)(Position.X / ((Parent?.Size.X ?? 1) / 12));
		public virtual int Y => (int)(Position.Y / ((Parent?.Size.Y ?? 1) / 5));
		public virtual int Width => (int)(Size.X / ((Parent?.Size.X ?? 1) / 12)); // Math.Min(Size.X, Size.Y)); // Details.Value.Width;
		public virtual int Height => (int)(Size.Y / ((Parent?.Size.Y ?? 1) / 5)); // Math.Min(Size.X, Size.Y)); // Details.Value.Height;

	}

	public class StashItem : InventoryItem {
		public StashItem(IntPtr addr) : base(addr) { }
		public override int X => 0;
		public override int Y => 0;
		// with a StashItem, the Parent is in the real Position
		// 3.24: no longer true (in a grid tab: the parent is the whole grid)
		// 3.30.0d: true again in a tab of fixed slots (eg, a Currency tab): the item sits in its slot element (.N, one cell,
		// placed where it is drawn), and its own position is a grid position on top of that, so its rect lands whole cells
		// to the right (Orb of Alchemy 5 cells, Orb of Scouring 10: seen live, the slots' rects are where they are drawn).
		// So: the parent's rect, when the parent is a slot of the item's own size; else its own.
		public override RectangleF GetClientRect() {
			var own = base.GetClientRect();
			var slot = Parent?.GetClientRect() ?? RectangleF.Empty;
			bool inSlot = slot.Width > 0 && Math.Abs(slot.Width - own.Width) < 1f && Math.Abs(slot.Height - own.Height) < 1f;
			return inSlot ? slot : own;
		}
		// and the item count is held in a Child element
		public int Count => int.Parse(GetChild(0)?.Text ?? "1");
	}


	/// <summary>
	/// The stash window (UIElements.StashElement). Layout confirmed live 3.30, a personal stash with 18 tabs
	/// (by clicking "$$" then "dump" and diffing the tree):
	///   .0 close button, .1 "Stash" title
	///   .2.0.0.1.0 the horizontal tab bar (scrolls; not used here). Its LAST child is the selected tab's button, and
	///      the other buttons get reshuffled when the selection changes.
	///   .2.0.0.1.1 the tab pages, one per tab in display order; only the selected tab's page is visible. A page has
	///      no children until its tab has been shown once.
	///   .2.0.0.1.4.2 the vertical tab list (does not scroll): row N is tab N in display order, the same N as the
	///      page; the row's name is row.GetChild(0, 1).Text ("dump"), and the row's rect is where to click.
	///      .4.0 / .4.1 are small arrow buttons beside it (hidden while seen).
	/// </summary>
	public class StashRoot : Element {
		public Element CloseButton => GetChild(0);

		private Element TabPages => GetChild(2, 0, 0, 1, 1);
		/// <summary>The selected tab's page (the only visible one), or null.</summary>
		public Element VisibleTabPage => TabPages?.Children.FirstOrDefault(p => p != null && p.IsVisibleLocal);
		private Element TabList => GetChild(2, 0, 0, 1, 4, 2);
		private static string RowName(Element row) => row?.GetChild(0, 1)?.Text;

		private string SelectedTabName() {
			var pages = TabPages?.Children.ToList();
			int i = pages?.FindIndex(p => p != null && p.IsVisibleLocal) ?? -1;
			return i < 0 ? null : RowName(TabList?.GetChild(i));
		}

		private Element FindRow(string name) => (TabList?.Children ?? Empty<Element>())
			.FirstOrDefault(r => IsValid(r) && string.Equals(RowName(r), name, StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// Click the tab named name (case-insensitive) in the vertical tab list, wait for its page to show, then next.
		/// Decided when it runs, not when built: nothing to do if that tab is already shown. Stops the chain (null)
		/// with a log if the stash is closed, the tab is missing, or it did not show within 2 s.
		/// </summary>
		public IState ActivateTab(string name, IState next = null) => State.NewState($"ActivateTab({name})", (self, dt) => {
			if ( !IsValid(Address) || !IsVisibleLocal ) { Log($"Stash: can't activate tab \"{name}\", the stash is not open."); return null; }
			if ( string.Equals(SelectedTabName(), name, StringComparison.OrdinalIgnoreCase) ) return next;
			var row = FindRow(name);
			if ( !IsValid(row) || !row.IsVisible ) { Log($"Stash: no tab named \"{name}\" in the tab list."); return null; }
			return Inputs.Click(row.GetClientRect(),
				State.WaitFor(2000, () => string.Equals(SelectedTabName(), name, StringComparison.OrdinalIgnoreCase), next,
					fail: State.NewState(() => Log($"Stash: tab \"{name}\" did not show after clicking it."), null)));
		}, next);
	}
}
