using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Drawing;
using static AtE.Globals;

namespace AtE {
	public static partial class Globals {
		public static bool IsValid(Element e) => e != null && e.IsValid();
	}

	public class Element : MemoryObject {

		public Cached<Offsets.Element> Cache;

		public Element() {
			Cache = CachedStruct<Offsets.Element>(this);
		}

		public Element Self => Address == IntPtr.Zero ? null : new Element() { Address = Cache.Value.Self };
		public Element Parent => Address == IntPtr.Zero ? null : new Element() { Address = Cache.Value.elemParent };

		public IEnumerable<Element> Children =>
			Cache.Value.Children.GetItems<IntPtr>()
				.Select(a => new Element() { Address = a });

		public Vector2 Position => Cache.Value.Position;
		public float Scale => Address == IntPtr.Zero ? 1.0f : Cache.Value.Scale;

		public bool IsVisibleLocal => (Cache.Value.IsVisibleLocal & 8) == 8;

		public Vector2 Size => Cache.Value.Size;

		private Vector2 GetAbsolutePosition() => GetAbsolutePosition(Vector2.Zero, PoEMemory.GameRoot.InGameState.UIRoot.Scale);
		private Vector2 GetAbsolutePosition(Vector2 pos, float rootScale) {
			Vector2 pPos = Position;
			float pScale = Scale / rootScale;
			pos.X += pPos.X * pScale;
			pos.Y += pPos.Y * pScale;
			if( Globals.IsValid(Parent) && Parent.Address != this.Address ) {
				return Parent.GetAbsolutePosition(pos, rootScale);
			} else {
				return pos;
			}
		}

		/*
		public Vector2 GetParentPos() {
			float num = 0;
			float num2 = 0;
			var rootScale = TheGame.IngameState.UIRoot.Scale;

			foreach ( var current in GetParentChain() ) {
				num += current.X * current.Scale / rootScale;
				num2 += current.Y * current.Scale / rootScale;
			}

			return new Vector2(num, num2);
		}
		*/


		public RectangleF GetClientRect() {
			if ( Address == IntPtr.Zero ) return RectangleF.Empty;
			float width = PoEMemory.GameRoot.InGameState.WorldData.Camera.Width;
			float height = PoEMemory.GameRoot.InGameState.WorldData.Camera.Height;
			var ratioFixMult = width / height / 1.6f;
			Vector2 screenScale = new Vector2(
				width / 2560f / ratioFixMult,
				height / 1600f);

			var rootScale = PoEMemory.GameRoot.InGameState.UIRoot.Scale;
			var vPos = screenScale * GetAbsolutePosition();
			var vSize = screenScale * (Size * Scale / rootScale);
			return new RectangleF(vPos.X, vPos.Y, vSize.X, vSize.Y);
		}

		public bool IsValid() => Address != IntPtr.Zero
			&& Cache.Value.Self == Address;

	}

}
