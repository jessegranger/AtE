using ImGuiNET;
using System;
using static AtE.Globals;

namespace AtE {

	public static partial class Globals {

		public static bool IsValid(IntPtr p) {
			long a = p.ToInt64();
			return a > 0x0000000100000000 && a < 0x00007FFFFFFFFFFF;
		}

		public static string Format(IntPtr ptr) => $"<0x{ptr.ToInt64():X}>";

		public static void ImGui_Address(IntPtr a, string label) {
			ImGui.AlignTextToFramePadding();
			ImGui.Text(label); ImGui.SameLine(0f, 2f);
			var str = Format(a);
			ImGui.Text(str);
			ImGui.SameLine();
			if( ImGui.Button($"M##{str}") ) {
				Log($"{a} launching debugger...");
				Run(new Debugger(a).WithKnownAddress(label, a));
			}
		}

		public static bool IsValid<T>(MemoryObject<T> m) where T : unmanaged => m != null && m.Address != IntPtr.Zero;
	}

	/// <summary>
	/// A MemoryObject represents some structure in the target process.
	/// The only value we hold directly is the Address, other operations
	/// should use CachedStruct to read data from offsets once per frame.
	/// Most of the time you want to use MemoryObject<T>.
	/// </summary>
	public class MemoryObject : IEquatable<MemoryObject>, IDisposable {

		public IntPtr Address { get; set; } = IntPtr.Zero;
		public MemoryObject() { }

		public bool Equals(MemoryObject other) => Address.Equals(other.Address);

		public override int GetHashCode() => Address.GetHashCode();

		private bool isDisposing = false;
		private bool isDisposed = false;
		public virtual void Dispose() {
			if ( !isDisposing && !isDisposed ) {
				isDisposing = true;
				Address = IntPtr.Zero;
				isDisposed = true;
			}
		}
	}

	/// <summary>
	/// Manage an object (of structure T) at some address in PoEMemory.
	/// </summary>
	/// <typeparam name="T">Defines the native layout of the object to be managed. Available as `base.Cache`.</typeparam>
	public class MemoryObject<T> : MemoryObject, IEquatable<MemoryObject<T>> where T : unmanaged {
		private Cached<T> cache;
		public T Cache => cache?.Value ?? default;
		public bool Equals(MemoryObject<T> other) => Address.Equals(other.Address);

		public new IntPtr Address {
			get => base.Address;
			set {
				if ( value == base.Address ) {
					return;
				}

				base.Address = value;

				if ( IsValid(value) ) {
					cache = CachedStruct<T>(this);
				} else {
					cache = null;
				}
			}
		}

		public MemoryObject() : base() => cache = CachedStruct<T>(this);

	}

	public class Cached<T> : IDisposable {
		private T val;
		private long lastFrame = -1; // starts at -1 so the very first access is always due, even if created on frame 0
		private Func<T> Producer;
		public T Value {
			get {
				if ( lastFrame < Overlay.FrameCount ) {
					lastFrame = Overlay.FrameCount;
					val = UncachedValue;
				}
				return val;
			}
		}
		public T UncachedValue => val = Producer != null ? Producer() : default;
		public void Flush() => lastFrame = -1;
		public Cached(Func<T> producer) => Producer = producer;
		public void Dispose() {
			Producer = null;
			val = default;
			lastFrame = long.MaxValue;
		}
	}

	public static partial class Globals {
		public static Cached<T> CachedStruct<T>(MemoryObject obj) where T : unmanaged
			=> new Cached<T>(() => PoEMemory.TryRead(obj.Address, out T ret) ? ret : default);
		public static Cached<T> CachedStruct<T>(Func<IntPtr> fOffset) where T : unmanaged
			=> new Cached<T>(() => PoEMemory.TryRead(fOffset(), out T ret) ? ret : default);
	}

}
