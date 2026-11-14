using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Drawing;
using static AtE.Globals;
using System.Text;

namespace AtE {
	public static partial class Globals {
		public static bool IsValid(Element e) => e != null && e.IsValid;

		public static void DrawTextAt(Element e, string text, Color color) {
			if( !IsValid(e)) {
				return;
			}
			var rect = e.GetClientRect();
			DrawTextAt(new Vector2(rect.X, rect.Y), text, color);
		}
	}

	public class Element : MemoryObject<Offsets.Element> {

		public Element() : base() { }

		public virtual bool IsValid => IsValid(Address) && Cache.elemSelf == Address;

		public Element Self => IsValid(Address) && ElementCache.TryGetElement(Cache.elemSelf, out Element elem) ? elem : null;
		public Element Parent => IsValid(Address) && ElementCache.TryGetElement(Cache.elemParent, out Element elem) ? elem : null;


		private ArrayHandle<IntPtr> lastKnownChildren;
		private long lastKnownChildrenUpdated = 0;
		private const uint lastKnownChildrenInterval = 1000;

		public IEnumerable<Element> Children {
			get {
				if ( lastKnownChildren == null || lastKnownChildrenUpdated < Time.ElapsedMilliseconds + lastKnownChildrenInterval ) {
					lastKnownChildren = new ArrayHandle<IntPtr>(Cache.Children);
					lastKnownChildrenUpdated = Time.ElapsedMilliseconds;
				}
				return lastKnownChildren.Select(a => ElementCache.TryGetElement(a, out Element elem) ? elem : null);
			}
		}

		private IntPtr GetOwnChildPtr(int index) {
			if ( lastKnownChildren == null || lastKnownChildrenUpdated < Time.ElapsedMilliseconds + lastKnownChildrenInterval ) {
				lastKnownChildren = new ArrayHandle<IntPtr>(Cache.Children);
				lastKnownChildrenUpdated = Time.ElapsedMilliseconds;
			}
			return index < 0 ? IntPtr.Zero : lastKnownChildren[index];
		}

		/// <summary>
		/// Follows a path of child indices: GetChildPtr(2, 0, 1) is the pointer to
		/// GetChild(2)?.GetChild(0)?.GetChild(1). Returns IntPtr.Zero if any step is missing.
		/// </summary>
		internal IntPtr GetChildPtr(params int[] indices) {
			if ( indices == null || indices.Length == 0 ) {
				return IsValid(Address) ? Address : IntPtr.Zero;
			}
			IntPtr ptr = GetOwnChildPtr(indices[0]);
			for ( int i = 1; i < indices.Length; i++ ) {
				if ( !ElementCache.TryGetElement(ptr, out Element elem) ) {
					return IntPtr.Zero;
				}
				ptr = elem.GetOwnChildPtr(indices[i]);
			}
			return ptr;
		}
		public Element GetChild(params int[] indices) => ElementCache.TryGetElement(GetChildPtr(indices), out Element child) ? child : null;


		public Vector2 Position => Cache.Position;
		/// <summary>The element's id in the game's UI layout (eg "login_button"), or null when it has none</summary>
		public string Name => Address == IntPtr.Zero || string.IsNullOrEmpty(Cache.strName.Value) ? null : Cache.strName.Value;
		/// <summary>
		/// The scale the element is drawn at. The game stores 1.0 for "the same as the parent" (3.29.4), so a 1.0 takes the nearest
		/// ancestor's that is not 1.0 (the UI root's 0.75 at the top).
		/// </summary>
		public float Scale {
			get {
				if ( Address == IntPtr.Zero ) return 1.0f;
				float own = Cache.Scale;
				if ( own != 1.0f ) return own;
				var cursor = this;
				for ( int hops = 0; hops < 40; hops++ ) {
					var parent = cursor.Parent;
					if ( parent == null || parent.Address == cursor.Address ) break;
					float s = parent.Cache.Scale;
					if ( s != 1.0f ) return s;
					cursor = parent;
				}
				return 1.0f;
			}
		}

		public Vector2 ScrollOffset => Address == IntPtr.Zero ? Vector2.Zero : Cache.ScrollOffset;

		public string Text => !IsValid(Address) ? null :
			PoEMemory.TryRead(Address + Offsets.Element_Text, out Offsets.StringHandle str) ? str.Value : null;

		/* correct and available but costly at the moment
		 * (they increase the IO cost of the Cache object for now)
		public string LongText => Cache.strLongText.Value;
		public string InputText => Cache.inputMask2 == Offsets.Element.inputMask2_HasInput
			? Cache.strInputText.Value : null;
		public IEnumerable<string> GetInnerText() {
			if ( !string.IsNullOrWhiteSpace(Text) ) yield return Text;
			foreach ( var child in Children ) {
				foreach ( var text in child.GetInnerText() ) {
					yield return text;
				}
			}
		}
		*/

		public bool IsVisibleLocal => Cache.IsVisibleLocal;

		/// <summary>Is it a button (see Offsets.Element.IsButton), not text, an icon or a container.</summary>
		public bool IsButton => Cache.IsButton;

		public bool IsVisible => IsVisibleLocal && (!IsValid(Parent) || Parent == Self || Parent.IsVisible);

		public Vector2 Size => Cache.Size;

		private Vector2 GetAbsolutePosition() => GetAbsolutePosition(Vector2.Zero, PoEMemory.GameRoot?.InGameState?.UIRoot?.Scale ?? .75f);
		private Vector2 GetAbsolutePosition(Vector2 pos, float rootScale) {
			Vector2 pPos = Position;
			float pScale = Scale / rootScale;
			pos.X += pPos.X * pScale;
			pos.Y += pPos.Y * pScale;
			if ( IsValid(Parent) && Parent.Address != Address ) {
				return Parent.GetAbsolutePosition(pos, rootScale);
			} else {
				return pos;
			}
		}

		private static Vector2 ScreenScale() {
			// Offsets.Vector2i size = PoEMemory.GameRoot.InGameState?.WorldData?.Camera.Size ?? default;
			float width = WindowSize.Value.X;
			float height = WindowSize.Value.Y;
			// the UI is developed against a virtual 2560x1600 screen, (a 1.6 aspect ratio)
			// so all UI coordinates are scaled by rootScale,
			// and the ratio is adjusted with ratioFixMulti
			var ratioFixMult = width / height / 1.6f;
			return new Vector2(
				width / 2560f / ratioFixMult,
				height / 1600f);
		}

		public virtual RectangleF GetClientRect() {
			if ( Address == IntPtr.Zero ) return RectangleF.Empty;
			var rootScale = PoEMemory.GameRoot?.InGameState?.UIRoot?.Scale ?? .75f;
			Vector2 screenScale = ScreenScale();
			var vPos = screenScale * GetAbsolutePosition();
			var vSize = screenScale * (Size * Scale / rootScale);
			return new RectangleF(vPos.X, vPos.Y, vSize.X, vSize.Y);
		}

		/// <summary>
		/// Where a point in this element's own coordinates (the space its children are placed in, moved by ScrollOffset)
		/// is on the screen; and how many screen pixels one unit of that space is.
		/// </summary>
		public Vector2 LocalToScreen(Vector2 local) {
			var rootScale = PoEMemory.GameRoot?.InGameState?.UIRoot?.Scale ?? .75f;
			return ScreenScale() * (GetAbsolutePosition() + ((ScrollOffset + local) * Scale / rootScale));
		}
		public float LocalScaleToScreen() {
			var rootScale = PoEMemory.GameRoot?.InGameState?.UIRoot?.Scale ?? .75f;
			return ScreenScale().Y * Scale / rootScale;
		}

	}


	/// <summary>
	/// This is a special Element, of which there is only one.
	/// In native memory, there is a doubly linked list of all the ground labels,
	/// and this Element holds a reference to the head of this list,
	/// and can Enumerate it.
	/// </summary>
	public class LabelsOnGroundRoot : Element {
		public Cached<Offsets.Element_ItemsOnGroundLabelRoot> Details;
		public LabelsOnGroundRoot() : base() => Details = CachedStruct<Offsets.Element_ItemsOnGroundLabelRoot>(() => Address);

		public LabelOnGround HoveredLabel => new LabelOnGround(Details.Value.hoverLabel);

		public IEnumerable<LabelOnGround> GetAllLabels() {
			IntPtr cursor = Details.Value.labelsOnGroundHead;
			// a stale offset can lead into a cycle of valid pointers that never comes back to the head: stop at a repeat, or after a sane number
			var visited = new HashSet<IntPtr>();
			do {
				if ( !visited.Add(cursor) || visited.Count > 5000 ) break;
				if ( PoEMemory.TryRead(cursor, out Offsets.ItemsOnGroundLabelEntry entry) ) {
					var label = new LabelOnGround(entry);
					if ( IsValid(label.Label) && label.Label.IsVisibleLocal ) {
						yield return label;
					}
					cursor = entry.nextEntry;
				} else break;
			} while ( IsValid(cursor) && cursor != Details.Value.labelsOnGroundHead );
		}
	}

	public class LabelOnGround {
		private readonly Offsets.ItemsOnGroundLabelEntry Cache;
		public LabelOnGround(Offsets.ItemsOnGroundLabelEntry entry) => Cache = entry;
		public Element Label => IsValid(Cache.elemLabel) ? new Element() { Address = Cache.elemLabel } : null;
		public Entity Item => IsValid(Cache.entItem) && EntityCache.TryGetEntity(Cache.entItem, out Entity ent) ? ent : null;
	}

}
