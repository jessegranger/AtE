using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ProcessMemoryUtilities.Native;
using static AtE.Globals;
using static ProcessMemoryUtilities.Managed.NativeWrapper;

namespace AtE {

	/// <summary>
	/// The Windows implementation of <see cref="IProcessMemory"/>. This is the only place
	/// in the memory path that touches the OS: OpenProcess / ReadProcessMemory /
	/// VirtualQueryEx (via ProcessMemoryUtilities) plus FindWindow for discovery.
	/// </summary>
	public sealed class WindowsProcessMemory : IProcessMemory {

		/// <summary>
		/// The opened target process. Exposed (Windows-only, not on the interface) so the
		/// transitional window/foreground callers can still reach MainWindowHandle until the
		/// IOverlayWindow / IInputSource seams land. Do not use from the portable core.
		/// </summary>
		public Process Target { get; private set; }

		/// <summary>The PROCESS_VM_READ handle to <see cref="Target"/>. Windows-only.</summary>
		public IntPtr Handle { get; private set; }

		// The exe image doesn't change after load, so capture it once per attach.
		private byte[] exeImage;

		public bool IsAttached => IsValid(Target) && Handle != IntPtr.Zero;

		public IntPtr BaseAddress => IsValid(Target) ? Target.MainModule.BaseAddress : IntPtr.Zero;

		public bool TryAttach(out int pid) {
			pid = 0;
			if ( IsAttached ) {
				Detach();
			}

			IntPtr hWnd = Win32.FindWindow(Offsets.WindowClass, Offsets.WindowTitle);
			Win32.GetWindowThreadProcessId(hWnd, out uint upid);
			if ( upid == 0 ) {
				return false;
			}

			Process process;
			try {
				process = Process.GetProcessById((int)upid);
			} catch ( Exception e ) {
				Log($"WindowsProcessMemory: GetProcessById failed: {e.Message}");
				return false;
			}
			if ( process == null ) {
				return false;
			}

			IntPtr handle;
			try {
				handle = OpenProcess(ProcessAccessFlags.VirtualMemoryRead | ProcessAccessFlags.QueryInformation, false, process.Id);
			} catch ( Exception e ) {
				Log($"WindowsProcessMemory: OpenProcess threw: {e.Message}");
				return false;
			}
			if ( handle == IntPtr.Zero ) {
				Log($"WindowsProcessMemory: OpenProcess failed.");
				return false;
			}

			Target = process;
			Handle = handle;
			exeImage = null;
			pid = process.Id;
			Log($"WindowsProcessMemory: attached to pid {pid}.");
			return true;
		}

		public void Detach() {
			if ( Target != null ) {
				Log($"WindowsProcessMemory: Detaching from process...");
				Target.Dispose();
				Target = null;
			}
			if ( Handle != IntPtr.Zero ) {
				Log($"WindowsProcessMemory: Closing process handle...");
				CloseHandle(Handle);
				Handle = IntPtr.Zero;
			}
			exeImage = null;
		}

		public bool TryReadImage(out byte[] image) {
			if ( exeImage != null ) {
				image = exeImage;
				return true;
			}
			image = null;
			if ( !IsAttached ) {
				return false;
			}
			long started = Time.ElapsedMilliseconds;
			IntPtr baseAddress = Target.MainModule.BaseAddress;
			long size = Target.MainModule.ModuleMemorySize;
			byte[] buf = new byte[size];
			ReadProcessMemoryArray(Handle, baseAddress, buf);
			exeImage = buf;
			image = exeImage;
			Log($"WindowsProcessMemory: Read {size / (1024 * 1024)}M executable image (base={Describe(baseAddress)},ms={Time.ElapsedMilliseconds - started}).");
			return true;
		}

		public int TryRead<T>(IntPtr addr, T[] buffer) where T : unmanaged {
			if ( !IsValid(addr) ) return 0;
			ReadProcessMemoryArray(Handle, addr, buffer, out IntPtr read);
			return read.ToInt32();
		}

		public bool TryRead<T>(IntPtr addr, out T value) where T : unmanaged {
			value = new T();
			if ( !IsValid(addr) ) {
				return false;
			}
			if ( ReadProcessMemory(Handle, addr, ref value) ) {
				return true;
			}
			if ( LastError != 6 ) Log($"TryRead: failed to read address {Describe(addr)} error: {LastError}");
			return false;
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

		public bool TryQueryRegion(IntPtr addr, out MemoryRegion region) {
			region = default;
			if ( Handle == IntPtr.Zero ) {
				return false;
			}
			if ( 0 == VirtualQueryEx(Handle, addr, out Win32.MEMORY_BASIC_INFORMATION_64 info,
					(uint)Marshal.SizeOf(typeof(Win32.MEMORY_BASIC_INFORMATION_64))) ) {
				return false;
			}
			region = new MemoryRegion {
				BaseAddress = info.BaseAddress,
				RegionSize = info.RegionSize,
				State = info.State,
				Protect = info.Protect,
			};
			return true;
		}
	}
}
