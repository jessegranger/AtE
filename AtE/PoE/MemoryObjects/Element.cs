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
				if( lastKnownChildren == null || lastKnownChildrenUpdated < Time.ElapsedMilliseconds + lastKnownChildrenInterval ) {
					lastKnownChildren = new ArrayHandle<IntPtr>(Cache.Children);
					lastKnownChildrenUpdated = Time.ElapsedMilliseconds;
				}
				return lastKnownChildren.Select(a => ElementCache.TryGetElement(a, out Element elem) ? elem : null);
			}
		}

		internal IntPtr GetChildPtr(int index) {
			if ( lastKnownChildren == null || lastKnownChildrenUpdated < Time.ElapsedMilliseconds + lastKnownChildrenInterval ) {
				lastKnownChildren = new ArrayHandle<IntPtr>(Cache.Children);
				lastKnownChildrenUpdated = Time.ElapsedMilliseconds;
			}
			return index < 0 ? IntPtr.Zero : lastKnownChildren[index];
		}
		public Element GetChild(int index) => ElementCache.TryGetElement(GetChildPtr(index), out Element child) ? child : null;


		public Vector2 Position => Cache.Position;
		public float Scale => Address == IntPtr.Zero ? 1.0f : Cache.Scale;

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

		public bool IsVisible => IsVisibleLocal && (!IsValid(Parent) || Parent == Self || Parent.IsVisible);

		public Vector2 Size => Cache.Size;

		private Vector2 GetAbsolutePosition() => GetAbsolutePosition(Vector2.Zero, PoEMemory.GameRoot.InGameState.UIRoot?.Scale ?? .75f);
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

		public RectangleF GetClientRect() {
			if ( Address == IntPtr.Zero ) return RectangleF.Empty;
			Offsets.Vector2i size = PoEMemory.GameRoot.InGameState.WorldData.Camera.Size;
			float width = size.X;
			float height = size.Y;
			var rootScale = PoEMemory.GameRoot?.InGameState?.UIRoot?.Scale ?? .75f;
			// the UI is developed against a virtual 2560x1600 screen, (a 1.6 aspect ratio)
			// so all UI coordinates are scaled by rootScale,
			// and the ratio is adjusted with ratioFixMulti
			var ratioFixMult = width / height / 1.6f;
			Vector2 screenScale = new Vector2(
				width / 2560f / ratioFixMult,
				height / 1600f);

			var vPos = screenScale * GetAbsolutePosition();
			var vSize = screenScale * (Size * Scale / rootScale);
			return new RectangleF(vPos.X, vPos.Y, vSize.X, vSize.Y);
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
			do {
				if ( PoEMemory.TryRead(cursor, out Offsets.ItemsOnGroundLabelEntry entry) ) {
					var label = new LabelOnGround(entry);
					if ( IsValid(label.Label) ) {
						yield return label;
					}
					cursor = entry.nextEntry;
				}
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
