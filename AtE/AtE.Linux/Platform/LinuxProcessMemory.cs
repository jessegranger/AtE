using System;
using System.Text;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IProcessMemory"/> — stub.
	///
	/// Target design (PORTING.md "Linux backend cheat-sheet"):
	///  - Process discovery: scan <c>/proc/*/comm</c> (or <c>/proc/*/cmdline</c>) for the PoE
	///    executable name.
	///  - Bulk reads (<see cref="TryRead{T}(IntPtr,out T)"/> / array / string): the
	///    <c>process_vm_readv(2)</c> syscall against the attached pid. Fall back to
	///    <c>pread</c> on <c>/proc/&lt;pid&gt;/mem</c> if needed.
	///  - <see cref="BaseAddress"/> and <see cref="TryReadImage"/>: parse <c>/proc/&lt;pid&gt;/maps</c>
	///    for the main executable mapping (and read its bytes through the above).
	///  - <see cref="TryQueryRegion"/>: map a <c>/proc/&lt;pid&gt;/maps</c> line onto
	///    <see cref="MemoryRegion"/>, translating the rwxp perms onto the Windows-style
	///    <c>MEM_COMMIT</c>/<c>PAGE_READWRITE</c> constants the core filters on.
	///
	/// Permissions caveat: cross-process reads require <c>CAP_SYS_PTRACE</c>, a matching uid,
	/// or a permissive <c>/proc/sys/kernel/yama/ptrace_scope</c>.
	/// </summary>
	public sealed class LinuxProcessMemory : IProcessMemory {

		private const string NYI = "LinuxProcessMemory is not yet implemented (PORTING.md step 6).";

		public bool TryAttach(out int pid) {
			pid = 0;
			throw new NotImplementedException(NYI + " TODO: discover the PoE pid via /proc/*/comm and open it.");
		}

		public bool IsAttached => false;

		public void Detach() {
			// No-op until attach is implemented. (Filled in: close the /proc/<pid>/mem fd, if any.)
		}

		public IntPtr BaseAddress =>
			throw new NotImplementedException(NYI + " TODO: read the main module base from /proc/<pid>/maps.");

		public int Pid =>
			throw new NotImplementedException(NYI + " TODO: return the attached pid.");

		public long ModuleSize =>
			throw new NotImplementedException(NYI + " TODO: sum the main executable's mappings in /proc/<pid>/maps.");

		public bool TryGetTargetWindowRect(out int left, out int top, out int width, out int height) {
			left = top = width = height = 0;
			throw new NotImplementedException(NYI + " TODO: query the window manager (e.g. XGetWindowAttributes / _NET_FRAME_EXTENTS) for the game window geometry.");
		}

		public bool TryReadImage(out byte[] image) {
			image = null;
			throw new NotImplementedException(NYI + " TODO: locate the main executable mapping in /proc/<pid>/maps and read its bytes.");
		}

		public bool TryRead<T>(IntPtr addr, out T value) where T : unmanaged {
			value = default;
			throw new NotImplementedException(NYI + " TODO: read via process_vm_readv(2).");
		}

		public int TryRead<T>(IntPtr addr, T[] buffer) where T : unmanaged {
			throw new NotImplementedException(NYI + " TODO: bulk-read the array via process_vm_readv(2); return bytes read.");
		}

		public bool TryReadString(IntPtr addr, Encoding enc, out string s, int maxLen = 256) {
			s = null;
			throw new NotImplementedException(NYI + " TODO: read up to maxLen bytes via process_vm_readv(2) and decode with enc.");
		}

		public bool TryQueryRegion(IntPtr addr, out MemoryRegion region) {
			region = default;
			throw new NotImplementedException(NYI + " TODO: find the containing /proc/<pid>/maps line and map it onto MemoryRegion.");
		}
	}
}
