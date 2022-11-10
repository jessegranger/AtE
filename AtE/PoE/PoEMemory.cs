using ImGuiNET;
using ProcessMemoryUtilities.Native;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;
using static ProcessMemoryUtilities.Managed.NativeWrapper;

namespace AtE {
	public static partial class Globals {

		public static bool IsValid(Process proc) => proc != null && !proc.HasExited;

		public static bool IsValid<T>(ArrayHandle<T> handle, int maxEntries = 10000) where T : unmanaged =>
			Offsets.IsValid(handle.Handle, Marshal.SizeOf<T>(), maxEntries);

	}

	/// <summary>
	/// The managed side of an Offsets.ArrayHandle native array.
	/// </summary>
	/// <typeparam name="T">A struct for the layout of each record in the array.</typeparam>
	public class ArrayHandle<T> : IEnumerable<T> where T : unmanaged {
		public Offsets.ArrayHandle Handle;
		public ArrayHandle(Offsets.ArrayHandle handle) {
			Handle = handle;
			sizeOfContainedType = Marshal.SizeOf(typeof(T));
		}

		private int sizeOfContainedType;

		public T this[int index] =>
			Offsets.IsValid(Handle) && PoEMemory.TryRead(Handle.GetRecordPtr(index, sizeOfContainedType), out T result)
			? result : default;

		public int Length => Offsets.IsValid(Handle) ? Handle.ItemCount(sizeOfContainedType) : 0;

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
		public IEnumerator<T> GetEnumerator() => !Offsets.IsValid(Handle) ? Empty<T>().GetEnumerator() :
			Handle.GetRecordPtrs(sizeOfContainedType)
				.Select(ptr => PoEMemory.TryRead(ptr, out T result) ? result : default)
				.GetEnumerator();

		public T[] ToArray(int limit = 2000) {
			long count = Handle.ItemCount(sizeOfContainedType);
			if ( count <= 0 || count > limit ) { return new T[] { }; }
			T[] result = new T[count];
			PoEMemory.TryRead(Handle.Head, result);
			return result;
		}
	}

	public static class PoEMemory {

		/// <summary>
		/// Once attached, this is the Target process we will read memory from.
		/// </summary>
		public static Process Target;
		/// <summary>
		/// A Handle with PROCESS_VM_READ permissions to the Target Process
		/// </summary>
		public static IntPtr Handle;

		/// <summary>
		/// The GameStateBase structure, with the main GameState array pointers
		/// </summary>
		public static GameRoot GameRoot;

		/// <summary>
		/// Try to read an array of unmanaged objects from the attached Process.
		/// Returns 0 if not attached.
		/// </summary>
		/// <param name="address">A location in virtual memory of the attached process.</param>
		/// <param name="buf">An array to read the data into</param>
		/// <returns>The number of bytes read into buf</returns>
		public static int TryRead<T>(IntPtr address, T[] buf) where T : unmanaged {
			if ( !IsValid(address) ) return 0;
			ReadProcessMemoryArray(Handle, address, buf, out IntPtr read);
			return read.ToInt32();
		}

		/// <summary>
		/// Try to read a single unmanaged value from the Target process.
		/// </summary>
		/// <typeparam name="T">An unmanaged type: float, long, etc.</typeparam>
		/// <param name="loc">The address in virtual memory of the attached process.</param>
		/// <param name="result">The resulting value</param>
		/// <returns>true if successful</returns>
		public static bool TryRead<T>(IntPtr loc, out T result) where T : unmanaged {
			result = new T();
			return IsValid(loc) && ReadProcessMemory(Handle, loc, ref result);
		}

		/// <summary>
		/// Reads a byte[maxLen] buffer from loc, and tries to use Encoding to get a string from it.
		/// </summary>
		public static bool TryReadString(IntPtr loc, Encoding enc, out string result, int maxLen = 256) {
			byte[] buf = new byte[maxLen];
			if( 0 == TryRead(loc, buf) ) {
				result = "";
				return false;
			}
			result = enc.GetString(buf);
			int end = result.IndexOf('\0');
			if( end != -1 ) {
				result = result.Substring(0, end);
			}
			result = result.TrimStart('?');
			return !string.IsNullOrEmpty(result);
		}

		private static bool TryOpenWindow(out Process result, out IntPtr hWnd) {
			result = null;
			hWnd = Win32.FindWindow(Offsets.WindowClass, Offsets.WindowTitle);
			Win32.GetWindowThreadProcessId(hWnd, out uint pid);
			if ( pid > 0 ) {
				var process = Process.GetProcessById((int)pid);
				if ( process != null ) {
					result = process;
					return true;
				}
			}
			return false;
		}

		// The exeImage portion of memory doesn't change after it's loaded, so we only capture it once
		private static byte[] exeImage;

		// TODO: might be helpful later to have a FindInHeap (that enumerates allocated pages and scans them)

		// TODO: if we need to search many patterns, we can do them all in one pass over exeImage for better cache usage
		// eg, we don't currently scan for the file patterns to parse the data files yet

		private static bool TryFindPatternInExe(out IntPtr result, string mask, params byte[] pattern) {
			result = IntPtr.Zero;
			if ( mask.Length != pattern.Length ) {
				throw new ArgumentException("mask and pattern should have the same Length");
			}

			Log($"PoEMemory: FindPattern of {pattern.Length} bytes");

			if ( !IsValid(Target) ) {
				ImGui.Text($"PoEMemory: No target.");
				return false;
			}

			long started = Time.ElapsedMilliseconds;
			IntPtr baseAddress = Target.MainModule.BaseAddress;
			long size;
			if ( exeImage == null ) {
				size = Target.MainModule.ModuleMemorySize;
				exeImage = new byte[size];
				ReadProcessMemoryArray(Handle, baseAddress, exeImage);
				Log($"PoEMemory: Seeking {pattern.Length} byte pattern from {size / (1024 * 1024)}M image (base={baseAddress:X},ms={Time.ElapsedMilliseconds - started}).");
			} else {
				size = exeImage.Length;
			}

			long offset = 0;
			long bestMatch = 0; // this is not the fastest way to search (it takes no shortcuts)
			long bestMatchScore = 0; // because for debugging, we want to be able to see the near matches
			for ( ; offset < size - pattern.Length; offset++ ) {
				int thisMatchScore = 0;
				for ( int i = 0; i < pattern.Length; i++ ) {
					if ( mask[i] == '?' || exeImage[offset + i] == pattern[i] ) {
						thisMatchScore += 1;
						if ( thisMatchScore > bestMatchScore ) {
							bestMatch = offset;
							bestMatchScore = thisMatchScore;
						}
						if ( thisMatchScore == pattern.Length ) {
							result = new IntPtr(baseAddress.ToInt64() + offset);
							Log($"PoEMemory: Found pattern at {Format(result)}");
							return true;
						}
					} else {
						break;
					}
				}
			}
			return false;

		}
		
		public static bool TargetHasFocus = false; // assigned once each frame in OnTick

		/// <summary>
		/// True if a valid Process is open, we have an open Handle to it,
		/// and we found a GameStateBase offset.
		/// </summary>
		public static bool IsAttached => IsValid(Target)
			&& Handle != IntPtr.Zero
			&& IsValid(GameRoot);

		public static EventHandler OnAttach;
		public static EventHandler OnDetach;

		private static long nextAttach = Time.ElapsedMilliseconds;
		private static long nextCheckResize = Time.ElapsedMilliseconds + 3000;

		internal static void OnTick(long dt) {
			TargetHasFocus = false;

			if ( IsAttached ) {
				TargetHasFocus = Target.MainWindowHandle == Win32.GetForegroundWindow();
				SpriteController.Enabled =  // same as:
				ImGuiController.Enabled = !PluginBase.GetPlugin<CoreSettings>().OnlyRenderWhenFocused || Overlay.HasFocus || TargetHasFocus;

				// check if we need to resize the overlay based on target window changing
				if ( nextCheckResize < Time.ElapsedMilliseconds ) {
					if ( Win32.GetWindowRect(Target.MainWindowHandle, out var rect) ) {
						if ( rect.Width != Overlay.Width || rect.Height != Overlay.Height ) {
							Log($"Need to resize overlay: {Target.MainWindowHandle} to {rect.Top} {rect.Left} {rect.Right} {rect.Bottom}");
							Overlay.Resize(rect.Left, rect.Top, rect.Right, rect.Bottom);
							nextCheckResize = Time.ElapsedMilliseconds + 300;
						}
					}
				}

			} else {

				// try to find a window to attach to
				if ( Time.ElapsedMilliseconds > nextAttach ) {
					if ( TryOpenWindow(out Target, out IntPtr hWnd) ) {
						TryAttach(Target, hWnd);
					}
					nextAttach = Time.ElapsedMilliseconds + 5000;
				}

			}
			return;
		}

		public static void Detach() {
			OnAreaChange = null;
			if ( Target != null ) {
				Log($"PoEMemory: Detaching from process...");
				Target.Dispose();
				Target = null;
			}
			if ( Handle != IntPtr.Zero ) {
				Log($"PoEMemory: Closing process handle...");
				CloseHandle(Handle);
				Handle = IntPtr.Zero;
			}
			if ( GameRoot != null ) {
				GameRoot.Address = IntPtr.Zero;
				GameRoot.Dispose();
				GameRoot = null;
			}
			OnDetach?.Invoke(null, null);
		}

		private static void TryAttach(Process target, IntPtr hWnd) {
			if ( !IsValid(target) ) {
				Log($"TryAttach: Invalid target.");
				return;
			}
			Log($"PoEMemory: TryAttach(pid {target.Id})");

			if ( IsAttached ) {
				Detach();
			}

			Target = target;
			try {
				Handle = OpenProcess(ProcessAccessFlags.VirtualMemoryRead | ProcessAccessFlags.Read, Target.Id);
				if ( Handle == IntPtr.Zero ) {
					Log($"PoEMemory: OpenProcess failed.");
					Detach();
					return;
				}
			} catch( Exception e ) {
				Log($"PoEMemory: failed to attach to Process: {e.Message}");
				Detach();
				return;
			}

			// Patterns are based from ExileApi Memory.cs
			if ( !TryFindPatternInExe(out IntPtr matchAddress, Offsets.GameRoot_SearchMask, Offsets.GameRoot_SearchPattern) ) {
				Log($"PoEMemory: Failed to find search pattern in memory.");
				Detach();
				return;
			}
			Log($"PoEMemory: Game State Base Search Pattern matched at {Format(matchAddress)}");
			// this first location is in the code section where the pattern matched (at the start of the pattern)
			// so skip 8, read an integer from there in the code that is a local offset to a global in the code section
			if ( !TryRead(matchAddress + Offsets.GameRoot_SearchPattern.Length, out int localOffset) ) {
				Log($"PoEMemory: Failed to read a localOffset after the search pattern.");
				Detach();
				return;
			}

			// the place in code plus the array index + 0xC has the address of the GameStateBase
			if ( !TryRead(matchAddress + localOffset, out Offsets.GameRoot_Ref baseRefs) ) {
				Log($"PoEMemory: Failed to find GameRoot.");
				Detach();
				return;
			}

			GameRoot = new GameRoot() { Address = baseRefs.ptrToGameRootPtr };
			Log($"PoEMemory: Game State Base is {Format(GameRoot.Address)}");
			if ( !GameRoot.IsValid ) {
				Log($"PoEMemory: Game State Base address resulted in an invalid GameRoot.");
				Detach();
				return;
			}

			OnAreaChange += (sender, area) => Log("OnAreaChange: " + area);

			OnAttach?.Invoke(null, null);
		}

	}

}
