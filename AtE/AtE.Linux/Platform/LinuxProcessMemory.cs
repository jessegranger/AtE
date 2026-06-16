using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IProcessMemory"/>.
	///
	/// Mirrors <c>WindowsProcessMemory</c> (the only place in the memory path that touches the
	/// OS) using the Linux equivalents from PORTING.md's "Linux backend cheat-sheet":
	///  - Process discovery: scan <c>/proc/*/comm</c> and <c>/proc/*/cmdline</c> for the PoE
	///    executable, confirming the candidate by looking for the exe mapping in its maps.
	///  - Bulk reads (<see cref="TryRead{T}(IntPtr,out T)"/> / array / string): the
	///    <c>process_vm_readv(2)</c> syscall against the attached pid, falling back to
	///    <c>pread</c> on an fd held open over <c>/proc/&lt;pid&gt;/mem</c>.
	///  - <see cref="BaseAddress"/> / <see cref="ModuleSize"/> / <see cref="TryReadImage"/>:
	///    parse <c>/proc/&lt;pid&gt;/maps</c> for the main executable's mappings.
	///  - <see cref="TryQueryRegion"/>: reproduce VirtualQueryEx semantics over the maps —
	///    return the mapping that contains the address (or a synthetic free gap up to the next
	///    mapping), translating the rwxp perms onto the Windows-style MEM_COMMIT / PAGE_*
	///    constants the portable core filters on.
	///
	/// Permissions caveat: cross-process reads require <c>CAP_SYS_PTRACE</c>, a matching uid,
	/// or a permissive <c>/proc/sys/kernel/yama/ptrace_scope</c>.
	/// </summary>
	public sealed class LinuxProcessMemory : IProcessMemory {

		// ---- libc syscalls ------------------------------------------------------------------

		[StructLayout(LayoutKind.Sequential)]
		private unsafe struct iovec {
			public void* iov_base;
			public UIntPtr iov_len;
		}

		[DllImport("libc", SetLastError = true)]
		private static extern unsafe long process_vm_readv(
			int pid, iovec* local_iov, UIntPtr liovcnt, iovec* remote_iov, UIntPtr riovcnt, UIntPtr flags);

		[DllImport("libc", SetLastError = true)]
		private static extern unsafe long pread(int fd, void* buf, UIntPtr count, long offset);

		[DllImport("libc", SetLastError = true)]
		private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

		[DllImport("libc", SetLastError = true)]
		private static extern int close(int fd);

		private const int O_RDONLY = 0;

		// ---- attached-process state ---------------------------------------------------------

		private int pid;
		private int memFd = -1;        // fd over /proc/<pid>/mem, the pread fallback
		private IntPtr baseAddress;    // main module base (lowest mapping of the exe)
		private long moduleSize;       // span of the main module's mappings
		private byte[] exeImage;       // captured once per attach

		// Window names the Windows backend matches via FindWindow; reused here only as the
		// substring to recognise the PoE executable in /proc.
		private const string PoEExeMatch = "pathofexile";

		public bool IsAttached => pid != 0;

		public int Pid => pid;

		public IntPtr BaseAddress => baseAddress;

		public long ModuleSize => moduleSize;

		// ---- attach / detach ----------------------------------------------------------------

		public bool TryAttach(out int pid) {
			pid = 0;
			if ( IsAttached ) {
				Detach();
			}

			if ( !TryFindPoEPid(out int found) ) {
				return false;
			}

			// Resolve the main module from the maps so BaseAddress/ModuleSize are ready and the
			// candidate is confirmed to be the game (and not a wine helper of the same name).
			if ( !TryFindMainModule(found, out IntPtr modBase, out long modSize, out _) ) {
				Log($"LinuxProcessMemory: pid {found} has no '{PoEExeMatch}' mapping; not attaching.");
				return false;
			}

			// Open /proc/<pid>/mem for the pread fallback. process_vm_readv is the primary path
			// and doesn't need this fd, so a failure here is non-fatal.
			int fd = open($"/proc/{found}/mem", O_RDONLY);

			memFd = fd;
			baseAddress = modBase;
			moduleSize = modSize;
			exeImage = null;
			this.pid = found;
			Log($"LinuxProcessMemory: attached to pid {found} (base={Describe(modBase)}, size={modSize / (1024 * 1024)}M, mem fd={fd}).");
			return true;
		}

		public void Detach() {
			if ( pid != 0 ) {
				Log($"LinuxProcessMemory: Detaching from pid {pid}...");
			}
			if ( memFd >= 0 ) {
				close(memFd);
				memFd = -1;
			}
			pid = 0;
			baseAddress = IntPtr.Zero;
			moduleSize = 0;
			exeImage = null;
		}

		// ---- primitive reads ----------------------------------------------------------------

		/// <summary>
		/// Read <paramref name="len"/> bytes from the target at <paramref name="remote"/> into
		/// <paramref name="local"/>. Tries process_vm_readv(2) first, then pread on the mem fd.
		/// Returns the number of bytes read (0 on failure).
		/// </summary>
		private unsafe int ReadRaw(IntPtr remote, void* local, int len) {
			if ( len <= 0 || pid == 0 ) {
				return 0;
			}

			iovec localIov = new iovec { iov_base = local, iov_len = (UIntPtr)(uint)len };
			iovec remoteIov = new iovec { iov_base = (void*)remote, iov_len = (UIntPtr)(uint)len };
			long n = process_vm_readv(pid, &localIov, (UIntPtr)1u, &remoteIov, (UIntPtr)1u, (UIntPtr)0u);
			if ( n >= 0 ) {
				return (int)n;
			}

			if ( memFd >= 0 ) {
				long r = pread(memFd, local, (UIntPtr)(uint)len, remote.ToInt64());
				if ( r >= 0 ) {
					return (int)r;
				}
			}
			return 0;
		}

		public unsafe bool TryRead<T>(IntPtr addr, out T value) where T : unmanaged {
			value = default;
			if ( !IsValid(addr) ) {
				return false;
			}
			int size = Unsafe.SizeOf<T>();
			T local = default;
			int read = ReadRaw(addr, Unsafe.AsPointer(ref local), size);
			if ( read == size ) {
				value = local;
				return true;
			}
			return false;
		}

		public int TryRead<T>(IntPtr addr, T[] buffer) where T : unmanaged {
			if ( buffer == null || buffer.Length == 0 || !IsValid(addr) ) {
				return 0;
			}
			int len = buffer.Length * Unsafe.SizeOf<T>();
			GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
			try {
				unsafe {
					return ReadRaw(addr, (void*)handle.AddrOfPinnedObject(), len);
				}
			} finally {
				handle.Free();
			}
		}

		public bool TryReadString(IntPtr addr, Encoding enc, out string s, int maxLen = 256) {
			byte[] buf = new byte[maxLen];
			if ( 0 == TryRead(addr, buf) ) {
				s = "";
				return false;
			}
			s = enc.GetString(buf);
			int end = s.IndexOf('\0');
			if ( end != -1 ) {
				s = s.Substring(0, end);
			}
			s = s.TrimStart('?');
			return !string.IsNullOrEmpty(s);
		}

		// ---- main module image --------------------------------------------------------------

		public bool TryReadImage(out byte[] image) {
			if ( exeImage != null ) {
				image = exeImage;
				return true;
			}
			image = null;
			if ( !IsAttached || baseAddress == IntPtr.Zero || moduleSize <= 0 ) {
				return false;
			}

			long started = Time.ElapsedMilliseconds;
			byte[] buf = new byte[moduleSize];
			long baseAddr = baseAddress.ToInt64();

			// The module spans several mappings (.text/.data/...) with possible unmapped gaps,
			// so read each of its mappings into the right offset rather than one bulk read that
			// would fault on a gap. Gaps stay zero-filled.
			foreach ( var m in EnumerateMaps(pid) ) {
				if ( !PathIsMainModule(m.path) ) {
					continue;
				}
				long offset = m.start - baseAddr;
				if ( offset < 0 || offset >= buf.Length ) {
					continue;
				}
				int want = (int)Math.Min(m.end - m.start, buf.Length - offset);
				if ( want <= 0 ) {
					continue;
				}
				unsafe {
					fixed ( byte* p = &buf[offset] ) {
						ReadRaw(new IntPtr(m.start), p, want);
					}
				}
			}

			exeImage = buf;
			image = exeImage;
			Log($"LinuxProcessMemory: Read {moduleSize / (1024 * 1024)}M executable image (base={Describe(baseAddress)}, ms={Time.ElapsedMilliseconds - started}).");
			return true;
		}

		// ---- region query (VirtualQueryEx equivalent) ---------------------------------------

		public bool TryQueryRegion(IntPtr addr, out MemoryRegion region) {
			region = default;
			if ( pid == 0 ) {
				return false;
			}

			long target = addr.ToInt64();
			long nextStart = long.MaxValue;

			foreach ( var m in EnumerateMaps(pid) ) {
				if ( target >= m.start && target < m.end ) {
					// addr falls inside this mapping — return it.
					region = new MemoryRegion {
						BaseAddress = new IntPtr(m.start),
						RegionSize = m.end - m.start,
						State = MemoryRegion.MEM_COMMIT,
						Protect = PermsToProtect(m.perms),
					};
					return true;
				}
				if ( m.start > target && m.start < nextStart ) {
					nextStart = m.start;
				}
			}

			// addr is in a gap: report a synthetic free region up to the next mapping so the
			// core's address-space walk can step over it (State != MEM_COMMIT => skipped).
			if ( nextStart != long.MaxValue ) {
				region = new MemoryRegion {
					BaseAddress = addr,
					RegionSize = nextStart - target,
					State = 0,
					Protect = 0,
				};
				return true;
			}

			// Nothing at or after addr — past the end of the address space.
			return false;
		}

		// ---- window geometry ----------------------------------------------------------------

		public bool TryGetTargetWindowRect(out int left, out int top, out int width, out int height) {
			left = top = width = height = 0;
			// The game window's geometry belongs to the display-server layer, not the memory
			// backend — it requires an X11/Wayland binding (XGetWindowAttributes /
			// _NET_FRAME_EXTENTS, or the layer-shell surface) that lives with the IOverlayWindow
			// seam (LinuxOverlayWindow, PORTING.md step 6: window). Until that lands there's no
			// connection here to query, so report "unavailable" rather than guess; callers
			// (Globals.WindowSize, the overlay-resize path) already handle a false result.
			return false;
		}

		// ---- /proc helpers ------------------------------------------------------------------

		private struct MapEntry {
			public long start;
			public long end;
			public string perms;   // e.g. "rw-p"
			public string path;
		}

		private static List<MapEntry> EnumerateMaps(int pid) {
			var entries = new List<MapEntry>();
			string path = $"/proc/{pid}/maps";
			string[] lines;
			try {
				lines = File.ReadAllLines(path);
			} catch ( Exception e ) {
				Log($"LinuxProcessMemory: failed to read {path}: {e.Message}");
				return entries;
			}
			foreach ( string line in lines ) {
				if ( TryParseMapLine(line, out MapEntry entry) ) {
					entries.Add(entry);
				}
			}
			return entries;
		}

		// A maps line: "start-end perms offset dev inode  pathname"
		// e.g. "7f3c00000000-7f3c00021000 rw-p 00000000 00:00 0   /path/PathOfExile.exe"
		private static bool TryParseMapLine(string line, out MapEntry entry) {
			entry = default;
			if ( string.IsNullOrEmpty(line) ) {
				return false;
			}
			string[] f = line.Split(new[] { ' ', '\t' }, 6, StringSplitOptions.RemoveEmptyEntries);
			if ( f.Length < 5 ) {
				return false;
			}
			int dash = f[0].IndexOf('-');
			if ( dash <= 0 ) {
				return false;
			}
			if ( !TryParseHex(f[0].Substring(0, dash), out long start) ||
				 !TryParseHex(f[0].Substring(dash + 1), out long end) ) {
				return false;
			}
			entry = new MapEntry {
				start = start,
				end = end,
				perms = f[1],
				path = f.Length >= 6 ? f[5].Trim() : "",
			};
			return true;
		}

		private static bool TryParseHex(string s, out long value) =>
			long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

		private static bool PathIsMainModule(string mapPath) {
			if ( string.IsNullOrEmpty(mapPath) ) {
				return false;
			}
			string name = BaseName(mapPath).ToLowerInvariant();
			return name.Contains(PoEExeMatch) && name.EndsWith(".exe");
		}

		private static string BaseName(string p) {
			int slash = p.LastIndexOfAny(new[] { '/', '\\' });
			return slash >= 0 ? p.Substring(slash + 1) : p;
		}

		/// <summary>Span the main module's mappings: lowest start to highest end.</summary>
		private static bool TryFindMainModule(int pid, out IntPtr modBase, out long modSize, out string modPath) {
			modBase = IntPtr.Zero;
			modSize = 0;
			modPath = null;
			long min = long.MaxValue, max = long.MinValue;
			foreach ( var m in EnumerateMaps(pid) ) {
				if ( !PathIsMainModule(m.path) ) {
					continue;
				}
				if ( m.start < min ) min = m.start;
				if ( m.end > max ) max = m.end;
				modPath = m.path;
			}
			if ( modPath == null || max <= min ) {
				return false;
			}
			modBase = new IntPtr(min);
			modSize = max - min;
			return true;
		}

		/// <summary>
		/// Discover the PoE pid by scanning /proc: match comm/cmdline against the executable
		/// name, then confirm the candidate actually maps the exe (filters out wine helpers).
		/// </summary>
		private static bool TryFindPoEPid(out int pid) {
			pid = 0;
			string[] dirs;
			try {
				dirs = Directory.GetDirectories("/proc");
			} catch ( Exception e ) {
				Log($"LinuxProcessMemory: failed to enumerate /proc: {e.Message}");
				return false;
			}

			foreach ( string dir in dirs ) {
				string name = BaseName(dir);
				if ( !int.TryParse(name, out int candidate) ) {
					continue; // not a pid directory
				}
				if ( !LooksLikePoE(candidate) ) {
					continue;
				}
				// Confirm by checking the candidate genuinely maps the exe.
				if ( TryFindMainModule(candidate, out _, out _, out _) ) {
					pid = candidate;
					Log($"LinuxProcessMemory: found PoE at pid {candidate}.");
					return true;
				}
			}
			Log("LinuxProcessMemory: no PoE process found in /proc.");
			return false;
		}

		private static bool LooksLikePoE(int pid) {
			// /proc/<pid>/comm is truncated to 15 chars; cmdline holds the full, NUL-separated
			// argv. Match either against the executable name.
			try {
				string comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
				if ( BaseName(comm).ToLowerInvariant().Contains(PoEExeMatch) ) {
					return true;
				}
			} catch {
				// process may have exited or be unreadable; fall through to cmdline
			}
			try {
				string cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
				foreach ( string arg in cmdline.Split('\0') ) {
					if ( string.IsNullOrEmpty(arg) ) {
						continue;
					}
					string b = BaseName(arg).ToLowerInvariant();
					if ( b.Contains(PoEExeMatch) && b.EndsWith(".exe") ) {
						return true;
					}
				}
			} catch {
				// unreadable; treat as non-match
			}
			return false;
		}

		// Map the rwxp perms of a maps line onto the Windows page-protection constants the
		// portable core filters on (it only tests == PAGE_READWRITE, but keep the others
		// distinct so the equality never matches the wrong region).
		private const int PAGE_NOACCESS = 0x01;
		private const int PAGE_READONLY = 0x02;
		private const int PAGE_READWRITE = 0x04;       // == MemoryRegion.PAGE_READWRITE
		private const int PAGE_EXECUTE = 0x10;
		private const int PAGE_EXECUTE_READ = 0x20;
		private const int PAGE_EXECUTE_READWRITE = 0x40;

		private static int PermsToProtect(string perms) {
			if ( string.IsNullOrEmpty(perms) || perms.Length < 3 ) {
				return PAGE_NOACCESS;
			}
			bool r = perms[0] == 'r';
			bool w = perms[1] == 'w';
			bool x = perms[2] == 'x';
			if ( x ) {
				if ( w ) return PAGE_EXECUTE_READWRITE;
				if ( r ) return PAGE_EXECUTE_READ;
				return PAGE_EXECUTE;
			}
			if ( w ) return PAGE_READWRITE;   // rw-p heap/data — the regions the core wants
			if ( r ) return PAGE_READONLY;
			return PAGE_NOACCESS;
		}
	}
}
