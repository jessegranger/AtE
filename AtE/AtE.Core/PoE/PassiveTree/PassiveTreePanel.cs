using System;
using System.Collections.Generic;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The passive tree panel, while it is open: where each passive is on the screen.
	/// The nodes are elements on a canvas (see Offsets.Element_PassiveNode); where they are on the canvas never changes,
	/// so that is read once, and only the canvas (moved by dragging, scaled by zooming) is read each frame.
	/// </summary>
	public static class PassiveTreePanel {

		public static Element Panel => GetUI()?.SkillTree;
		public static bool IsOpen => IsValid(Panel) && Panel.IsVisible;
		public static Element Canvas => IsOpen ? Panel.GetChild(1) : null;

		// passive id => its center, in canvas coordinates
		private static readonly Dictionary<int, Vector2> centers = new Dictionary<int, Vector2>();
		private static IntPtr centersCanvas;
		private static int centersChildCount;

		/// <summary>The passive ids on the canvas, and their centers in canvas coordinates (empty while the panel is closed).</summary>
		public static IReadOnlyDictionary<int, Vector2> Centers {
			get {
				var canvas = Canvas;
				if ( !IsValid(canvas) ) return new Dictionary<int, Vector2>();
				int count = new ArrayHandle<IntPtr>(canvas.Cache.Children).Length;
				if ( canvas.Address != centersCanvas || count != centersChildCount ) {
					centers.Clear();
					foreach ( var child in new ArrayHandle<IntPtr>(canvas.Cache.Children) ) AddNodes(child, Vector2.Zero, 0);
					centersCanvas = canvas.Address;
					centersChildCount = count;
				}
				return centers;
			}
		}

		// a group's position is added to its nodes'; only leaves are nodes
		private static void AddNodes(IntPtr address, Vector2 offset, int depth) {
			if ( !PoEMemory.TryRead(address, out Offsets.Element e) || e.elemSelf != address ) return;
			var pos = offset + e.Position;
			var children = new ArrayHandle<IntPtr>(e.Children);
			if ( children.Length > 0 ) {
				if ( depth < 2 ) foreach ( var child in children ) AddNodes(child, pos, depth + 1);
			} else if ( TryReadNodeId(address, out int id) ) {
				centers[id] = pos + (e.Size / 2);
			}
		}

		private static bool TryReadNodeId(IntPtr element, out int id) {
			id = 0;
			if ( PoEMemory.TryRead(element, out Offsets.Element_PassiveNode node) && IsValid(node.ptrNodeData)
				&& PoEMemory.TryRead(node.ptrNodeData, out Offsets.PassiveNodeData data) && IsValid(data.ptrPassiveSkill)
				&& PoEMemory.TryRead(data.ptrPassiveSkill, out Offsets.PassiveSkillRow row) ) {
				id = row.Id;
			}
			return id != 0;
		}

		/// <summary>Where a passive is on the screen, while the panel is open.</summary>
		public static bool TryGetScreenPosition(int id, out Vector2 pos) {
			pos = default;
			var canvas = Canvas;
			if ( !IsValid(canvas) || !Centers.TryGetValue(id, out var center) ) return false;
			pos = canvas.LocalToScreen(center);
			return true;
		}

		/// <summary>Screen pixels per unit of tree coordinates (changes with the zoom).</summary>
		public static float ScreenScale => IsValid(Canvas) ? Canvas.LocalScaleToScreen() : 0f;
	}
}
