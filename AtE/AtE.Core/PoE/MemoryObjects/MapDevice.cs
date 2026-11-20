using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Map Device window (UIElements.MapDevice; also the Act 7 Maligaro's Orrery, "Map Receptacle").
	/// 3.30, seen live with Maligaro's Map in it:
	///   child 3.1   the title ("Map Receptacle")
	///   child 6     the Activate button (its child 0 reads "activate")
	///   child 8     the "atlas (g)" button
	///   child 10    the map's slot: child 10.0 is the slot's background, child 10.1 the map (an InventoryItem)
	///   child 11-15 more slots, hidden while empty
	/// </summary>
	public class MapDevice : Element {

		/// <summary>The map device's window is on the screen.</summary>
		public bool IsOpen => IsValid(Address) && IsVisible;

		/// <summary>The Activate button (opens the portals), or null.</summary>
		public Element ActivateButton => GetChild(6);

		/// <summary>The map in the device's map slot, or null if it is empty.</summary>
		public InventoryItem SocketedMap {
			get {
				var slot = GetChild(10);
				if ( !IsValid(slot) ) return null;
				// (child 0 is the slot's background; the item is the next one, but look at each, in case that changes)
				return slot.Children
					.Where(c => IsValid(c))
					.Select(c => new InventoryItem(c.Address))
					.FirstOrDefault(i => IsValid(i));
			}
		}

		// The hideout's Atlas map device (3.30.0d, seen live with a rare Map (Tier 1) loaded, "Silo" selected on the Atlas):
		//   child 2          the title ("Atlas")
		//   child 7          the MapLoader. Its visible flag is the one to trust: the game hides the loader (with a map still
		//                    loaded in it) and then only its own flag reads false; its children's flags stay true.
		//   child 7.0.1.0.0  the selected map's name ("Silo")
		//   child 7.0.2.0    the map's slot (the round one, above the row): child 0 its background, child 1 the map (an InventoryItem)
		//   child 7.0.2.1-5  more slots, hidden
		//   child 7.0.3      the Activate button (its child 0 reads "activate", the only text that does)

		/// <summary>The Atlas: the big panel of area nodes, dragged around like the passive tree. Its children 12-143 or so are area nodes.</summary>
		public Element Atlas => GetChild(0);

		private static readonly int AreaNodeOffset = (int)System.Runtime.InteropServices.Marshal.OffsetOf<Offsets.Element_AreaNode>(nameof(Offsets.Element_AreaNode.ptrAreaNode));

		/// <summary>The name of the area (eg "Silo") an Atlas child is the node of (Offsets.Element_AreaNode), or null if it isn't one.</summary>
		public static string AreaNodeName(Element e) {
			if ( !IsValid(e) ) return null;
			// (read the one field, not the whole 0x358 byte element, for each of the Atlas's ~200 children)
			if ( !PoEMemory.TryRead(e.Address + AreaNodeOffset, out IntPtr ptrAreaNode) || !IsValid(ptrAreaNode) ) return null;
			if ( !PoEMemory.TryRead(ptrAreaNode, out Offsets.AtlasNode_Entry entry) || !IsValid(entry.ptrWorldArea) ) return null;
			if ( !PoEMemory.TryRead(entry.ptrWorldArea, out Offsets.WorldAreaDetails area) ) return null;
			// (another kind of child can hold anything at 0x350: an area's id is "Map..." , eg "MapWorldsSilo")
			if ( !PoEMemory.TryReadString(area.strRawName, System.Text.Encoding.Unicode, out string id, 128) || id == null || !id.StartsWith("Map") ) return null;
			return PoEMemory.TryReadString(area.strName, System.Text.Encoding.Unicode, out string name, 128) ? name : null;
		}

		/// <summary>The Atlas's area nodes, with their area names.</summary>
		public IEnumerable<(Element node, string name)> AreaNodes =>
			(Atlas?.Children ?? Enumerable.Empty<Element>()).Select(c => (node: c, name: AreaNodeName(c))).Where(p => p.name != null);

		/// <summary>The Atlas's node for the area named name (eg "Silo"), or null.</summary>
		public Element FindAreaNode(string name) =>
			AreaNodes.FirstOrDefault(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)).node;

		/// <summary>The Atlas map device's MapLoader (the panel with the map slot and Activate button), or null.</summary>
		public Element MapLoader => GetChild(7);

		/// <summary>The MapLoader panel is on the screen (by its own flag: its children's stay true while it is hidden).</summary>
		public bool MapLoaderIsOpen => IsOpen && MapLoader is Element loader && loader.IsVisibleLocal;

		/// <summary>The name of the map selected on the Atlas, shown at the top of the MapLoader (eg "Silo"), or null.</summary>
		public string MapLoaderTitle => GetChild(7, 0, 1, 0, 0)?.Text?.Trim();

		/// <summary>The map in the MapLoader's map slot, or null if it is empty.</summary>
		public InventoryItem LoadedMap {
			get {
				var slot = GetChild(7, 0, 2, 0);
				if ( !IsValid(slot) ) return null;
				return slot.Children
					.Where(c => IsValid(c))
					.Select(c => new InventoryItem(c.Address))
					.FirstOrDefault(i => IsValid(i));
			}
		}

		/// <summary>The MapLoader's Activate button, or null (also if its label doesn't read "activate").</summary>
		public Element MapLoaderActivateButton {
			get {
				var button = GetChild(7, 0, 3);
				return string.Equals(button?.GetChild(0)?.Text?.Trim(), "activate", StringComparison.OrdinalIgnoreCase) ? button : null;
			}
		}

		/// <summary>Time after a click for the game to show what it did.</summary>
		public static uint Settle => InventoryPlans.Settle;

		/// <summary>The path of a portal opened by a map device.</summary>
		public const string PATH_MAP_PORTAL = Offsets.PATH_PORTAL; // Metadata/MiscellaneousObjects/MultiplexPortal

		/// <summary>Go on to ifOpen if the map device window is open when this step is reached, else ifClosed.</summary>
		public static IState OnlyIfMapDeviceIsOpen(IState ifOpen, IState ifClosed = null) =>
			State.NewState("CheckMapDeviceIsOpen", (self, dt) => MapDeviceIsOpen() ? ifOpen : ifClosed, next: ifOpen);

		/// <summary>
		/// Ctrl + left click a map in the backpack (it goes into the map device), then wait for it to show in the
		/// device's slot, then next. Null (fails at once) if the map device window is not open, or the item is not valid.
		/// </summary>
		public static IState PlanLoadMapFromInventory(InventoryItem inventoryItem, IState next = null) {
			if ( !MapDeviceIsOpen() ) { Log("MapDevice: can't load a map, the map device is not open."); return null; }
			if ( !IsValid(inventoryItem) ) { Log("MapDevice: can't load a map, the item is not valid."); return null; }
			string path = inventoryItem.Entity?.Path;
			return OnlyIfMapDeviceIsOpen(InventoryPlans.OnlyIfBackpackIsOpen(
				Inputs.CtrlClick(inventoryItem.GetClientRect(),
					State.WaitFor(2000, () => GetUI()?.MapDevice?.SocketedMap?.Entity?.Path is string p && p == path,
						next: new Delay(Settle, next),
						fail: State.NewState(() => Log($"MapDevice: {path} did not show in the map device."), null)))));
		}

		/// <summary>PlanLoadMapFromInventory, then click Activate, then next. Null if the map device window is not open.</summary>
		public static IState PlanLaunchMapFromInventory(InventoryItem inventoryItem, IState next = null) =>
			PlanLaunchMapFromInventory(inventoryItem, next, beforeClick: null);

		// (beforeClick runs just before Activate is clicked: eg, to note which portals are already open)
		private static IState PlanLaunchMapFromInventory(InventoryItem inventoryItem, IState next, Action beforeClick) {
			if ( !MapDeviceIsOpen() ) { Log("MapDevice: can't launch a map, the map device is not open."); return null; }
			return PlanLoadMapFromInventory(inventoryItem, OnlyIfMapDeviceIsOpen(ClickActivate(next, beforeClick)));
		}

		/// <summary>
		/// PlanLaunchMapFromInventory, then wait for a new map portal to open (one that was not there when Activate was
		/// clicked: a stale portal from an earlier Activate is never taken), click it (again every 2s), and go on to next
		/// when the area changes (within 15s, else stops). Null if the map device window is not open.
		/// </summary>
		public static IState PlanEnterMapFromInventory(InventoryItem inventoryItem, IState next = null) {
			if ( !MapDeviceIsOpen() ) { Log("MapDevice: can't enter a map, the map device is not open."); return null; }
			var stale = new HashSet<uint>();
			Func<Entity> newPortal = () => FindMapPortal(stale);
			return PlanLaunchMapFromInventory(inventoryItem,
				State.WaitFor(5000, () => newPortal() != null,
					next: EnterPortal(newPortal, next),
					fail: State.NewState(() => Log("MapDevice: no new map portal opened."), null)),
				beforeClick: () => { stale.Clear(); stale.UnionWith(MapPortals().Select(e => e.Id)); });
		}

		// click the Activate button (found when this step is reached)
		private static IState ClickActivate(IState next, Action beforeClick = null) => State.NewState("ClickActivate", (self, dt) => {
			var button = GetUI()?.MapDevice?.ActivateButton;
			if ( !IsValid(button) || !button.IsVisible ) { Log("MapDevice: no Activate button."); return null; }
			beforeClick?.Invoke();
			return Inputs.Click(button.GetClientRect(), new Delay(Settle, next));
		}, next);

		// click the portal (found again each try) until the area changes
		private static IState EnterPortal(Func<Entity> findPortal, IState next, uint timeoutMs = 15000, uint retryMs = 2000) {
			IntPtr startArea = GetCurrentArea()?.Address ?? IntPtr.Zero;
			long elapsed = 0, sinceClick = retryMs;
			return State.NewState("EnterMapPortal", (self, dt) => {
				var area = GetCurrentArea()?.Address ?? IntPtr.Zero;
				if ( area != IntPtr.Zero && area != startArea ) return next;
				if ( (elapsed += dt) > timeoutMs ) { Log("MapDevice: did not enter the map portal."); return null; }
				if ( (sinceClick += dt) < retryMs ) return self;
				var portal = findPortal();
				if ( portal == null ) return self; // (while loading, the portal is gone before the area changes)
				var click = Ground.ClickEntity(portal, self);
				if ( click == null ) return self;
				sinceClick = 0;
				return click;
			}, next);
		}

		/// <summary>
		/// The portals opened by map devices (MultiplexPortal; 3.30, seen live: Activate on Maligaro's Orrery opened six,
		/// entity ids in a row, one per pad around it).
		///
		/// What a portal knows (3.30, checked live on those six): its Portal component holds nothing but its owner and an
		/// empty vector at +0x20 (Component_Portal.ptrWorldArea, +0x30, is 0 on all six); its StateMachine ("standard_portal" = 1, "portal" =
		/// 0x1_00000003, "map_device_index" = 3, "map_device_large" = 1) was the same on all six; Transitionable.State = 2.
		/// No creation time or destination was found in the components. Where it goes is its ground label's text (the
		/// area's name: "Maligaro's Sanctum"), see MapPortalDestination. How old it is: its animated object's
		/// TimeSpentAnimating counts up from 0 when it opens (1.0/s, simulation time: stops when the game is paused; 155s
		/// on all six, within 0.7s), but the entity id is simpler: a portal that was not there before Activate is new.
		/// </summary>
		public static IEnumerable<Entity> MapPortals() => GetEntities()?.Where(e => IsValid(e) && e.Path == PATH_MAP_PORTAL) ?? Enumerable.Empty<Entity>();

		/// <summary>Where a map portal goes: the text of its label on the ground (the area's name), or null if it shows none.</summary>
		public static string MapPortalDestination(Entity portal) {
			if ( !IsValid(portal) ) return null;
			var label = GetUI()?.LabelsOnGround?.GetAllLabels()?.FirstOrDefault(l => l.Item?.Address == portal.Address);
			return label == null ? null : Ground.LabelText(label.Label);
		}

		/// <summary>The map portal nearest the player (not one of the ids in stale), or null.</summary>
		public static Entity FindMapPortal(ICollection<uint> stale = null) {
			var player = GetPlayer()?.GetComponent<Positioned>();
			if ( !IsValid(player) ) return null;
			var at = player.GridPos;
			return MapPortals()
				.Where(e => stale == null || !stale.Contains(e.Id))
				.Select(e => (e, pos: e.GetComponent<Positioned>()))
				.Where(x => IsValid(x.pos))
				.OrderBy(x => {
					float dx = x.pos.GridPos.X - at.X, dy = x.pos.GridPos.Y - at.Y;
					return (dx * dx) + (dy * dy);
				})
				.Select(x => x.e)
				.FirstOrDefault();
		}
	}

	public static partial class Globals {
		/// <summary>The map device window is open.</summary>
		public static bool MapDeviceIsOpen() => GetUI()?.MapDevice?.IsOpen ?? false;
	}
}
