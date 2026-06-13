using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {
	public static class ElementCache {
		private static ConcurrentDictionary<IntPtr, Element> Elements = new ConcurrentDictionary<IntPtr, Element>();
		private static int selfOffset = GetOffset<Offsets.Element>("elemSelf");

		public static bool Probe(IntPtr address) => PoEMemory.TryRead(address + selfOffset, out IntPtr self) && self == address;

		public static bool TryGetElement(IntPtr address, out Element element) {
			element = null;
			if ( IsValid(address) ) {
				element = Elements.GetOrAdd(address, (ptr) => new Element() { Address = ptr });
				return IsValid(element);
			}
			return false;
		}
	}
}
