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
	}

	public class Element : MemoryObject<Offsets.Element> {


		public bool IsValid => Address != IntPtr.Zero && Cache.Self == Address;

		public Element Self => Address == IntPtr.Zero || Cache.Self == IntPtr.Zero ? null
			: new Element() { Address = Cache.Self };

		public Element Parent => Address == IntPtr.Zero || Cache.elemParent == IntPtr.Zero ? null
			: new Element() { Address = Cache.elemParent };


		private ArrayHandle<IntPtr> children;
		public IEnumerable<Element> Children =>
			(children ?? (children = new ArrayHandle<IntPtr>(Cache.Children)))
			.Select(a => new Element() { Address = a });

		public Element GetChild(int index) => index < 0 ? null :
			new Element() { Address = (children ?? (children = new ArrayHandle<IntPtr>(Cache.Children)))[index] };
		internal IntPtr GetChildPtr(int index) => index < 0 ? default :
			(children ?? (children = new ArrayHandle<IntPtr>(Cache.Children)))[index];


		public Vector2 Position => Cache.Position;
		public float Scale => Address == IntPtr.Zero ? 1.0f : Cache.Scale;

		public string Text => Cache.strText.Value;
		public string LongText => Cache.strLongText.Value;
		public string InputText => Cache.inputMask2 == Offsets.Element.inputMask2_HasInput
			? Cache.strInputText.Value : null;

		public bool IsVisibleLocal => Cache.IsVisibleLocal;

		public bool IsVisible => IsVisibleLocal && (!IsValid(Parent) || Parent == Self || Parent.IsVisible);

		public IEnumerable<string> GetInnerText() {
			if ( !string.IsNullOrWhiteSpace(Text) ) yield return Text;
			foreach ( var child in Children ) {
				foreach ( var text in child.GetInnerText() ) {
					yield return text;
				}
			}
		}


		public Vector2 Size => Cache.Size;

		private Vector2 GetAbsolutePosition() => GetAbsolutePosition(Vector2.Zero, PoEMemory.GameRoot.InGameState.UIRoot.Scale);
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
			var rootScale = PoEMemory.GameRoot.InGameState.UIRoot.Scale;
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
			} while ( cursor != IntPtr.Zero && cursor != Details.Value.labelsOnGroundHead );
		}
	}

	public class LabelOnGround {
		private readonly Offsets.ItemsOnGroundLabelEntry Cache;
		public LabelOnGround(Offsets.ItemsOnGroundLabelEntry entry) => Cache = entry;
		public Element Label => new Element() { Address = Cache.elemLabel };
		public Entity Item => new Entity() { Address = Cache.entItem };
	}

}
