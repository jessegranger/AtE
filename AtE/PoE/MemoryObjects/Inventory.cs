using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

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
		public Cached<Offsets.Element_Inventory> Details;
		public Inventory() : base() => Details = CachedStruct<Offsets.Element_Inventory>(() => Address);

		/// <summary>
		/// This is the generic slow way that all Inventory nodes can use.
		/// More specific code will be needed for specific panels, Maps, Exotic currency, etc
		/// </summary>
		public IEnumerable<InventoryItem> VisibleItems => AllVisibleChildren(this, new HashSet<int>())
			.Select(e => new InventoryItem() { Address = e.Address })
			.Where(e => IsValid(e) && e.IsVisible);

		private IEnumerable<Element> AllVisibleChildren(Element cursor, HashSet<int> seen) {
			foreach(var child in Children) {
				if ( seen.Contains(child.GetHashCode()) ) continue;
				seen.Add(child.GetHashCode());
				if ( IsValid(child) && child.IsVisibleLocal ) {
					yield return child;
					foreach ( var grandchild in AllVisibleChildren(child, seen) ) {
						yield return grandchild;
					}
				}
			}
		}
	}

	public static partial class Globals {
		public static bool IsValid(InventoryItem item) => item != null && IsValid((Element)item) && item.IsValid;
	}

	public class InventoryItem : Element {
		public Cached<Offsets.Element_InventoryItem> Details;
		public InventoryItem() : base() => Details = CachedStruct<Offsets.Element_InventoryItem>(() => Address);
		public new bool IsValid => base.IsValid
			&& Details.Value.entItem != IntPtr.Zero
			&& Details.Value.Width > 0
			&& Details.Value.Height > 0
			&& Details.Value.InventPosition.X >= 0
			&& Details.Value.InventPosition.Y >= 0
			&& IsValid(new Entity() { Address = Details.Value.entItem });

		public Entity Item => Details.Value.entItem == IntPtr.Zero ? null
			: new Entity() { Address = Details.Value.entItem };

		public int X => Details.Value.InventPosition.X;
		public int Y => Details.Value.InventPosition.Y;
		public int Width => Details.Value.Width;
		public int Height => Details.Value.Height;

	}

}
