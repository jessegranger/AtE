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

		public Inventory Helm => new Inventory() { Address = Inventories.Value.Helm };
		public Inventory Amulet => new Inventory() { Address = Inventories.Value.Amulet };
		public Inventory Chest => new Inventory() { Address = Inventories.Value.Chest };
		public Inventory LWeapon => new Inventory() { Address = Inventories.Value.LWeapon };
		public Inventory RWeapon => new Inventory() { Address = Inventories.Value.RWeapon };
		public Inventory LWeaponSwap => new Inventory() { Address = Inventories.Value.LWeaponSwap };
		public Inventory RWeaponSwap => new Inventory() { Address = Inventories.Value.RWeaponSwap };
		public Inventory LRing => new Inventory() { Address = Inventories.Value.LRing };
		public Inventory RRing => new Inventory() { Address = Inventories.Value.RRing };
		public Inventory Gloves => new Inventory() { Address = Inventories.Value.Gloves };
		public Inventory Belt => new Inventory() { Address = Inventories.Value.Belt };
		public Inventory Boots => new Inventory() { Address = Inventories.Value.Boots };
		public Inventory Backpack => new Inventory() { Address = Inventories.Value.Backpack };
		public Inventory Flasks => new Inventory() { Address = Inventories.Value.Flask };
		public Inventory Trinket => new Inventory() { Address = Inventories.Value.Trinket };
		public Inventory LeveledGems => new Inventory() { Address = Inventories.Value.LeveledGems };
		public Inventory LWeaponSwapTabPanel => new Inventory() { Address = Inventories.Value.LWeaponSwapTabPanel };
		public Inventory RWeaponSwapTabPanel => new Inventory() { Address = Inventories.Value.RWeaponSwapTabPanel };
	}

	public class Inventory : Element {
		// public Cached<Offsets.Element_Inventory> Details;
		// public Inventory() : base() => Details = CachedStruct<Offsets.Element_Inventory>(() => Address);

		/// <summary>
		/// This is the generic slow way that all Inventory nodes can use.
		/// More specific code will be needed for specific panels, Maps, Exotic currency, etc
		/// </summary>
		public IEnumerable<InventoryItem> VisibleItems {
			get {
				return AllVisibleChildren(this, new HashSet<int>())
					.Select(e => new InventoryItem() { Address = e.Address })
					.Where(e => Globals.IsValid(e) && e.IsVisible);
			}
		}

		private static IEnumerable<Element> AllVisibleChildren(Element cursor, HashSet<int> seen) {
			int index = 0;
			foreach ( Element child in cursor.Children ) {
				if ( !seen.Contains(child.GetHashCode()) ) {
					seen.Add(child.GetHashCode());
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

		public static IEnumerable<InventoryItem> BackpackItems() => GetUI()?.InventoryPanel?.Backpack?.VisibleItems.Take(60).Where(IsValid) ?? Empty<InventoryItem>();
		public static IEnumerable<InventoryItem> StashItems() => new Inventory() { Address = GetUI()?.StashElement?.Address ?? IntPtr.Zero }.VisibleItems;

		public static bool BackpackIsOpen() => GetUI()?.InventoryPanel?.Backpack?.IsVisibleLocal ?? false;
		public static bool StashIsOpen() => GetUI()?.StashElement?.IsVisibleLocal ?? false;
	}

	public class InventoryItem : Element {
		public Cached<Offsets.Element_InventoryItem> Details;
		public InventoryItem() : base() => Details = CachedStruct<Offsets.Element_InventoryItem>(() => Address);
		public override bool IsValid {
			get {
				var details = Details.Value;
				return base.IsValid
					&& IsValid(details.entItem)
					&& details.Width > 0
					&& details.Height > 0
					&& details.InventPosition.X >= 0
					&& details.InventPosition.Y >= 0
					&& EntityCache.TryGetEntity(details.entItem, out _) //  IsValid(new Entity() { Address = details.entItem })
					;
			}
		}

		public Entity Entity => IsValid(Address) && EntityCache.TryGetEntity(Details.Value.entItem, out Entity ent) ? ent : null;

		public int X => Details.Value.InventPosition.X;
		public int Y => Details.Value.InventPosition.Y;
		public int Width => Details.Value.Width;
		public int Height => Details.Value.Height;

	}


	public class StashRoot : Element {
		public Element CloseButton => GetChild(0);

	}
}
