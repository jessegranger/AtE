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
		public Inventory Flasks => IsValid(Inventories.Value.Flask) ? new Inventory() { Address = Inventories.Value.Flask } : null;
		public Inventory Trinket => IsValid(Inventories.Value.Trinket) ? new Inventory() { Address = Inventories.Value.Trinket } : null;
		public Inventory LeveledGems => IsValid(Inventories.Value.LeveledGems) ? new Inventory() { Address = Inventories.Value.LeveledGems } : null;
		public Inventory LWeaponSwapTabPanel => IsValid(Inventories.Value.LWeaponSwapTabPanel) ? new Inventory() { Address = Inventories.Value.LWeaponSwapTabPanel } : null;
		public Inventory RWeaponSwapTabPanel => IsValid(Inventories.Value.RWeaponSwapTabPanel) ? new Inventory() { Address = Inventories.Value.RWeaponSwapTabPanel } : null;
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
				if ( IsValid(child) && !seen.Contains(child.GetHashCode()) ) {
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

		public static bool BackpackIsOpen() => GetUI()?.InventoryPanel?.IsVisibleLocal ?? false;
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
					// && details.InventPosition.X >= 0
					// && details.InventPosition.Y >= 0
					&& EntityCache.TryGetEntity(details.entItem, out _) //  IsValid(new Entity() { Address = details.entItem })
					;
			}
		}

		public Entity Entity => IsValid(Address) && EntityCache.TryGetEntity(Details.Value.entItem, out Entity ent) ? ent : null;

		public virtual int X => (int)(Position.X / (Parent.Size.X / 12));
		public virtual int Y => (int)(Position.Y / (Parent.Size.Y / 5));
		public virtual int Width => Details.Value.Width;
		public virtual int Height => Details.Value.Height;

	}


	public class StashRoot : Element {
		public Element CloseButton => GetChild(0);

	}
}
