using System;
using System.Text;

namespace AtE {

	/// <summary>
	/// A platform-neutral description of one region of the target's virtual address
	/// space. On Windows this is filled from MEMORY_BASIC_INFORMATION_64; on Linux it
	/// would come from a line of /proc/&lt;pid&gt;/maps.
	/// </summary>
	public struct MemoryRegion {
		public IntPtr BaseAddress;
		public long RegionSize;
		public int State;
		public int Protect;

		public IntPtr End => new IntPtr(BaseAddress.ToInt64() + RegionSize);

		// Windows page-protection / state values, kept here so the portable core can
		// filter regions without referencing Win32. Each backend maps its own values
		// onto these.
		public const int MEM_COMMIT = 0x00001000;
		public const int PAGE_READWRITE = 0x04;
	}

	/// <summary>
	/// The OS-touching part of memory access: process discovery, attach/detach, and the
	/// primitive reads. Everything above this (the entity/component model, ArrayHandle&lt;T&gt;,
	/// offset scanning, caching) is platform-neutral and only calls these methods.
	///
	/// Windows impl: <see cref="WindowsProcessMemory"/> (OpenProcess / ReadProcessMemory /
	/// VirtualQueryEx). Linux impl (TODO): process_vm_readv + /proc/&lt;pid&gt;/maps.
	/// </summary>
	public interface IProcessMemory {

		/// <summary>Find and open the target (PoE) process. Returns its pid on success.</summary>
		bool TryAttach(out int pid);

		/// <summary>True once a process is open and a read handle is held (independent of GameRoot).</summary>
		bool IsAttached { get; }

		/// <summary>Release the process handle and any cached per-process state.</summary>
		void Detach();

		/// <summary>Base address of the target's main module, for pattern scanning.</summary>
		IntPtr BaseAddress { get; }

		/// <summary>The bytes of the target's main module image (cached after the first read).</summary>
		bool TryReadImage(out byte[] image);

		/// <summary>Read a single unmanaged value out of the target.</summary>
		bool TryRead<T>(IntPtr addr, out T value) where T : unmanaged;

		/// <summary>Read an array of unmanaged values; returns the number of bytes read.</summary>
		int TryRead<T>(IntPtr addr, T[] buffer) where T : unmanaged;

		/// <summary>Read a string of up to maxLen bytes and decode it with enc.</summary>
		bool TryReadString(IntPtr addr, Encoding enc, out string s, int maxLen = 256);

		/// <summary>Query the region containing addr (the VirtualQueryEx equivalent).</summary>
		bool TryQueryRegion(IntPtr addr, out MemoryRegion region);
	}
}
