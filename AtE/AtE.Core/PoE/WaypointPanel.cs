using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The waypoint panel (UIElements.AreaInstanceUi), which opens when the "Waypoint" label is clicked. Each destination
	/// on it is an element holding, at Offsets.Element_AreaInstanceDestination_Offset, a pointer to an
	/// Offsets.AreaInstanceDestination_Details (the area's name, id, WorldAreas row); clicking it goes there.
	/// </summary>
	public static class WaypointPanel {

		/// <summary>The waypoint panel, if it is open.</summary>
		public static Element Panel() {
			var panel = GetUI()?.AreaInstanceUi;
			return IsValid(panel) && panel.IsVisible ? panel : null;
		}

		/// <summary>
		/// The destination element for the area called name (eg, "The Mud Flats"), or null (eg, the panel is closed, or
		/// the waypoint is not on the act shown, or not discovered: shown but locked counts as not there, see IsUnlocked).
		/// </summary>
		public static Element FindWaypointElement(string name) {
			foreach ( var (element, destination) in Destinations() ) {
				if ( SameName(destination, name) && IsUnlocked(element) ) return element;
			}
			return null;
		}

		/// <summary>
		/// Do a panel name and an area name name the same place: equal ignoring case, or but for a leading "The " (the
		/// panel says "Prisoner's Gate" for The Prisoner's Gate), or a pair in PanelAliases (eg, "Merveil's Caverns" for
		/// The Cavern of Anger).
		/// </summary>
		public static bool SameName(string a, string b) {
			if ( a == null || b == null ) return false;
			static string Bare(string s) => s.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? s.Substring(4) : s;
			string x = Bare(a.Trim()), y = Bare(b.Trim());
			if ( string.Equals(x, y, StringComparison.OrdinalIgnoreCase) ) return true;
			return PanelAliases.Any(p => (string.Equals(x, p.panel, StringComparison.OrdinalIgnoreCase) && string.Equals(y, p.area, StringComparison.OrdinalIgnoreCase))
				|| (string.Equals(y, p.panel, StringComparison.OrdinalIgnoreCase) && string.Equals(x, p.area, StringComparison.OrdinalIgnoreCase)));
		}

		/// <summary>The panel's names for areas it calls something else (without a leading "The").</summary>
		public static readonly (string panel, string area)[] PanelAliases = {
			("Merveil's Caverns", "Cavern of Anger"),
		};

		/// <summary>Is the destination element the area the player is in (see Offsets.Element_AreaInstanceDestination_IsCurrentArea)?</summary>
		public static bool IsCurrentArea(Element e) =>
			IsValid(e) && PoEMemory.TryRead(e.Address + Offsets.Element_AreaInstanceDestination_IsCurrentArea, out byte b) && b != 0;

		/// <summary>
		/// Can the destination element be traveled to (its area visited)? The panel also shows areas next to visited ones,
		/// which can't be (eg, The Ledge before going there). See Offsets.Element_AreaInstanceDestination_Unlocked_Offset.
		/// </summary>
		public static bool IsUnlocked(Element e) =>
			IsValid(e) && PoEMemory.TryRead(e.Address + Offsets.Element_AreaInstanceDestination_Unlocked_Offset, out byte b) && b != 0;

		/// <summary>
		/// Where to click a destination element, from its top left: the element is a point (0.75 x 0.75, no children),
		/// with the waypoint's icon drawn below and to the right of it.
		/// </summary>
		public static readonly System.Numerics.Vector2 ClickOffset = new System.Numerics.Vector2(10, 10);

		/// <summary>
		/// Click the destination for the area called name (eg, "The Mud Flats") on the open waypoint panel, which takes the
		/// player there. False if it is not on the panel (eg, the panel is closed, or that waypoint is not discovered).
		/// </summary>
		public static bool ClickWaypoint(string name) {
			var element = FindWaypointElement(name);
			if ( element == null ) return false;
			var r = element.GetClientRect();
			SkillBar.ClickAt(new System.Drawing.RectangleF(r.X + ClickOffset.X, r.Y + ClickOffset.Y, 0, 0));
			return true;
		}

		/// <summary>
		/// The destination elements on the open panel, with their area names: from AreaInstanceUi.Children[2], each
		/// visible element (depth first) whose Element_AreaInstanceDestination_Offset pointer reads as a destination
		/// with a name. Empty if the panel is closed.
		/// </summary>
		public static IEnumerable<(Element element, string name)> Destinations() => DestinationsIn(Panel());

		/// <summary>
		/// The destinations as Destinations reads them, but whether or not the panel is open: its element tree stays in
		/// memory while it is closed, with each element's own visible flag (IsVisibleLocal) set as when it was last shown
		/// (the act last shown). Their screen rects mean nothing while it is closed. Empty if the panel was never built.
		/// </summary>
		public static IEnumerable<(Element element, string name)> DestinationsEvenClosed() {
			var panel = GetUI()?.AreaInstanceUi;
			return IsValid(panel) ? DestinationsIn(panel) : Array.Empty<(Element, string)>();
		}

		/// <summary>
		/// True if the panel's element tree (open or closed, see DestinationsEvenClosed) shows that the waypoint called
		/// name (as the panel names it, eg "Merveil's Caverns", or an area's name, see SameName) can't be taken now: it
		/// shows the current act (one destination is marked as the current area, see IsCurrentArea, or is named as it) and
		/// name is not an unlocked destination there. False when it can't tell (the tree shows another act, or was never
		/// built): try the waypoint then. The closed tree is as of when the panel was last open, so a waypoint unlocked
		/// since may still read as locked (it walks instead, which is only slower).
		/// </summary>
		public static bool IsKnownLocked(string name) {
			string here = GetCurrentArea()?.Name;
			if ( string.IsNullOrEmpty(name) ) return false;
			var list = DestinationsEvenClosed().ToList();
			if ( !list.Any(d => IsCurrentArea(d.element) || SameName(d.name, here)) ) return false;
			return !list.Any(d => SameName(d.name, name) && IsUnlocked(d.element));
		}

		private static IEnumerable<(Element element, string name)> DestinationsIn(Element panel) {
			var root = panel?.GetChild(2);
			foreach ( var e in UiSearch.Visible(root, 12, 5000) ) {
				if ( TryReadDestination(e, out string name) ) yield return (e, name);
			}
		}

		/// <summary>The area name of a destination element, read through its Element_AreaInstanceDestination_Offset pointer; false if it is not one.</summary>
		public static bool TryReadDestination(Element e, out string name) {
			name = null;
			if ( !IsValid(e) ) return false;
			if ( !PoEMemory.TryRead(e.Address + Offsets.Element_AreaInstanceDestination_Offset, out IntPtr ptr) || !IsValid(ptr) ) return false;
			if ( !PoEMemory.TryRead(ptr, out Offsets.AreaInstanceDestination_Details details) || !IsValid(details.ptrName) ) return false;
			if ( !PoEMemory.TryReadString(details.ptrName, Encoding.Unicode, out name, 64) ) return false;
			// (the pointer of an element that is not a destination reads as something else: keep only what looks like a name)
			return !string.IsNullOrWhiteSpace(name) && IsPrintable(name);
		}

		private static bool IsPrintable(string s) {
			foreach ( char c in s ) {
				// (area names are Latin script; other pointers read as runs of CJK / Hangul characters)
				if ( char.IsControl(c) || c == '�' || c > 'ɏ' ) return false;
			}
			return true;
		}
	}
}
