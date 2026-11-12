using System;
using System.Collections.Generic;
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
		/// the waypoint is not on the act shown, or not discovered).
		/// </summary>
		public static Element FindWaypointElement(string name) {
			foreach ( var (element, destination) in Destinations() ) {
				if ( string.Equals(destination, name, StringComparison.OrdinalIgnoreCase) ) return element;
			}
			return null;
		}

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
		public static IEnumerable<(Element element, string name)> Destinations() {
			var root = Panel()?.GetChild(2);
			if ( !IsValid(root) ) yield break;
			var stack = new Stack<(Element e, int depth)>();
			stack.Push((root, 0));
			int seen = 0;
			while ( stack.Count > 0 && seen++ < 5000 ) {
				var (e, depth) = stack.Pop();
				if ( TryReadDestination(e, out string name) ) yield return (e, name);
				if ( depth >= 12 ) continue;
				foreach ( var child in e.Children ) {
					if ( IsValid(child) && child.IsVisibleLocal ) stack.Push((child, depth + 1));
				}
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
				if ( char.IsControl(c) || c == '�' ) return false;
			}
			return true;
		}
	}
}
