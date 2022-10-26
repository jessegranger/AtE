using ProcessMemoryUtilities.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ProcessMemoryUtilities.Managed.NativeWrapper;
using static AtE.Globals;
using ImGuiNET;

namespace AtE {

	public static partial class Globals {
		public static bool IsValid(IntPtr p) => p != IntPtr.Zero;

		public static string ToString(IntPtr ptr) => $"IntPtr(0x{ptr.ToInt64():X})";

		public static void ImGui_Address(IntPtr a, string label) {
			ImGui.AlignTextToFramePadding();
			ImGui.Text(label); ImGui.SameLine();
			var str = ToString(a);
			ImGui.Text(str);
			ImGui.SameLine();
			if( ImGui.Button($"M##{str}") ) {
				Log($"{a} launching debugger...");
				Run(new Debugger(a).WithKnownAddress(label, a));
			}
		}
	}

	/*
	/// <summary>
	/// Address represents one absolute virtual memory address in a process.
	/// It holds two components, Base and Offset, but combines them freely.
	/// Using this class everywhere reduces a lot of confusion about whether
	/// code is dealing with an absolute or a relative address.
	/// </summary>
	public struct Address : IComparable<Address>, IEquatable<Address> {
		public long Base;
		public long Offset;
		public static readonly Address Zero = new Address(0, 0);
		public Address(long b, long offset = 0) {
			Base = b;
			Offset = offset;
		}
		public Address(IntPtr b, long offset = 0) {
			Base = b.ToInt64();
			Offset = offset;
		}
		/// <summary>
		/// Convenience constructor for the common case of a heap pointer,
		/// like new Address(0, ptr)
		/// </summary>
		public Address(long b, IntPtr offset) {
			Base = b;
			Offset = offset.ToInt64();
		}

		// allow downgrading an Address to a long or IntPtr (but not long to Address)
		public static implicit operator long(Address a) => a.Base + a.Offset;
		public static implicit operator IntPtr(Address a) => new IntPtr(a.Base + a.Offset);
		public static implicit operator Address(IntPtr p) => new Address(0, p.ToInt64());

		// define basic operations on Addresses that adjust the offset portion but preserve the base
		public static Address operator +(Address a, long x) => new Address(a.Base, a.Offset + x);
		public static Address operator -(Address a, long x) => new Address(a.Base, a.Offset - x);
		public static Address operator +(Address a, Address b) {
			if ( a.Base != b.Base && b.Base != 0 ) throw new ArgumentException("Can only add Addresses with the same Base, or a second Base of 0.");
			return new Address(a.Base, a.Offset + b.Offset);
		}
		public static Address operator -(Address a, Address b) {
			if ( a.Base != b.Base && b.Base != 0 ) throw new ArgumentException("Can only substract Addresses with the same Base, or a second Base of 0.");
			return new Address(a.Base, a.Offset - b.Offset);
		}

		public T AsObject<T>() where T : MemoryObject, new()
			=> new T() { Address = this };

		public void Render(string label) {
			ImGui.AlignTextToFramePadding();
			ImGui.Text(label); ImGui.SameLine();
			ImGui.Text(ToString());
			ImGui.SameLine();
			if( ImGui.Button($"M##{(long)this:X}") ) {
				Log($"{this} launching debugger...");
				Run(new Debugger(this).WithKnownAddress(label, this));
			}
		}

		public int CompareTo(Address other) => (Base + Offset).CompareTo(other);
		public bool Equals(Address other) => (Base + Offset) == (long)other;
		public override string ToString() => Base == 0 ? $"Address(0x{Offset:X})" : $"Address(0x{Base:X} + 0x{Offset:X})";

		public override int GetHashCode() => (int)(Base + Offset);
	}
	*/

	/// <summary>
	/// A MemoryObject represents some structure in the target process.
	/// The only value we hold directly is the Address, other operations
	/// should use CachedStruct to read data from offsets.
	/// </summary>
	public class MemoryObject : IEquatable<MemoryObject>, IDisposable {

		public IntPtr Address { get; set; } = IntPtr.Zero;
		public MemoryObject() { }
		public MemoryObject(IntPtr loc) => Address = loc;

		public T AsNew<T>() where T : MemoryObject, new() => new T() { Address = Address };
		public bool Equals(MemoryObject other) => Address.Equals(other.Address);

		public override int GetHashCode() => Address.GetHashCode();

		private bool isDisposing = false;
		private bool isDisposed = false;
		public virtual void Dispose() {
			if ( isDisposing || isDisposed ) return;
			isDisposing = true;
			Address = IntPtr.Zero;
			isDisposed = true;
		}
	}

	public class MemoryObject<T> : MemoryObject, IEquatable<MemoryObject<T>> where T : unmanaged {
		private Cached<T> Cache;
		protected T Struct => Cache.Value;
		public bool Equals(MemoryObject<T> other) => Address.Equals(other.Address);

		public MemoryObject():base() => Cache = CachedStruct<T>(this);

	}

	public class Cached<T> : IDisposable {
		private T val;
		private long lastFrame = -1;
		private Func<T> Producer;
		public T Value {
			get {
				if ( lastFrame < Overlay.FrameCount ) {
					lastFrame = Overlay.FrameCount;
					val = Producer();
				}
				return val;
			}
		}
		public void Flush() => lastFrame = -1;
		public Cached(Func<T> producer) {
			Producer = producer;
		}
		public void Dispose() {
			Producer = null;
			val = default;
			lastFrame = long.MaxValue;
		}
	}

	public static partial class Globals {
		public static Cached<T> CachedStructPtr<T>(Func<IntPtr> fOffset) where T : unmanaged
			=> new Cached<T>(() => {
				IntPtr offset = fOffset();
				ImGui.Text($"CachedStructPtr[{typeof(T).Name}]: trying 0x{offset.ToInt64():X}");
				if ( PoEMemory.TryRead(offset, out IntPtr deref) ) {
					ImGui.Text($"CachedStructPtr[{typeof(T).Name}]: deref 0x{deref.ToInt64():X}");
					if ( PoEMemory.TryRead(deref, out T result) ) {
						return result;
					}
				}
				return default;
			});
		public static Cached<T> CachedStruct<T>(MemoryObject obj) where T : unmanaged
			=> new Cached<T>(() => PoEMemory.TryRead(obj.Address, out T ret) ? ret : default);
		public static Cached<T> CachedStruct<T>(Func<IntPtr> fOffset) where T : unmanaged
			=> new Cached<T>(() => PoEMemory.TryRead(fOffset(), out T ret) ? ret : default);
	}

}
