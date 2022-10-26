using ImGuiNET;
using ProcessMemoryUtilities.Native;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;
using static ProcessMemoryUtilities.Managed.NativeWrapper;
using System.Collections;

namespace AtE {
	public static partial class Globals {

		public static bool IsValid(Process proc) => proc != null && !proc.HasExited;

	}


	/// <summary>
	/// These are array control structures used throughout PoE.
	/// </summary>
	public struct ArrayHandle {
		public IntPtr Head;
		public IntPtr Tail;
		public IEnumerable<T> GetItems<T>() where T : unmanaged {
			IntPtr cursor = Head;
			int size = Marshal.SizeOf(typeof(T));
			if ( !IsValid(cursor) || size == 0 ) yield break;
			IntPtr tail = Tail;
			while ( cursor.ToInt64() < tail.ToInt64() ) {
				if ( PoEMemory.TryRead(cursor, out T result) ) {
					yield return result;
				}
				cursor += size;
			}
		}
	}

	public static class PoEMemory {

		/// <summary>
		/// The Class of the window to search for.
		/// </summary>
		public static readonly string WindowClass = Offsets.WindowClass;
		/// <summary>
		/// The Title of the window to search for.
		/// </summary>
		public static readonly string WindowTitle = Offsets.WindowTitle;
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
		public static GameStateBase GameRoot;

		private static IntPtr GameStateBasePtr; // two temporary pointers that get followed to find GameStateBase
		private static IntPtr GameStateBasePtrPtr; // see: GameStateBasePtrHop1, Hop2, in Offsets


		/// <summary>
		/// Try to read an array of unmanaged objects from the attached Process.
		/// Returns 0 if not attached.
		/// </summary>
		/// <param name="address">A location in virtual memory of the attached process.</param>
		/// <param name="buf">An array to read the data into</param>
		/// <returns>The number of bytes read into buf</returns>
		public static int TryRead<T>(IntPtr address, T[] buf) where T : unmanaged {
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
			if ( loc == IntPtr.Zero ) return false;
			return ReadProcessMemory(Handle, loc, ref result);
		}

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
			return !string.IsNullOrEmpty(result);
		}

		/// <summary>
		/// Read one value immediately, and throw on fail.
		/// </summary>
		/// <typeparam name="T">An unmanaged type</typeparam>
		/// <param name="loc">A virtual memory address</param>
		/// <returns>The resulting value</returns>
		public static T Read<T>(IntPtr loc) where T : unmanaged {
			if ( TryRead(loc, out T result) ) {
				return result;
			}
			throw new ArgumentException($"TryRead failed from address {loc}");
		}

		private static bool TryOpenWindow(out Process result) {
			result = null;
			IntPtr hWnd = Win32.FindWindow(WindowClass, WindowTitle);
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
							Log($"PoEMemory: Found pattern at {result} sanity check: {exeImage[offset]:X} should equal {pattern[0]:X}");
							return true;
						}
					} else {
						break;
					}
				}
			}
			ImGui.Text($"Best Match: {bestMatch:X} score {bestMatchScore} out of {pattern.Length} bytes");
			return false;

		}

		/// <summary>
		/// True if a valid Process is open, we have an open Handle to it,
		/// and we found a GameStateBase offset.
		/// </summary>
		public static bool Attached => IsValid(Target)
			&& Handle != IntPtr.Zero
			&& GameRoot != null
			&& GameRoot.Address != IntPtr.Zero;

		private static long nextAttach = Time.ElapsedMilliseconds;

		public static void OnTick(long dt) {
			try {
				ImGui.Begin("PoEMemory");
				ImGui.AlignTextToFramePadding();
				ImGui.Text($"Target: {IsValid(Target)}");
				if ( IsValid(Target) ) {
					ImGui.SameLine();
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"PID: {Target.Id}");
					ImGui.SameLine();
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"Handle: {Handle}");
					ImGui.SameLine();
					if( ImGui.Button("Detach") ) {
						Detach();
						return;
					}
					
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"Process Base: 0x{Target.MainModule.BaseAddress.ToInt64():X}");
					if( GameRoot == null ) {
						ImGui.Text("GameRoot = null");
						return;
					}

					ImGui_Address(GameRoot.Address, "GameRoot Base:");
					ImGui.SameLine();
					if( ImGui.Button("B##GameRoot") ) {
						Run_ObjectBrowser("GameRoot", GameRoot);
					}

				} else {
					if ( Time.ElapsedMilliseconds > nextAttach ) {
						if ( TryOpenWindow(out Target) ) {
							TryAttach(Target);
						}
						nextAttach = Time.ElapsedMilliseconds + 5000;
					} else {
						ImGui.Text($"Attaching in {(int)(nextAttach - Time.ElapsedMilliseconds) / 1000}s...");
					}
				}
			} finally {
				ImGui.End();
			}
			return;
		}

		public static void Detach() {
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
		}

		public static void TryAttach(Process target) {
			if ( !IsValid(target) ) {
				Log($"TryAttach: Invalid target.");
				return;
			}
			Log($"PoEMemory: TryAttach(pid {target.Id})");

			if ( Attached ) {
				Detach();
			}

			Target = target;
			Handle = OpenProcess(ProcessAccessFlags.VirtualMemoryRead | ProcessAccessFlags.Read, Target.Id);
			if ( Handle == IntPtr.Zero ) {
				Log($"PoEMemory: OpenProcess failed.");
				Detach();
				return;
			}

			// Patterns are based from ExileApi Memory.cs
			if ( !TryFindPatternInExe(out GameStateBasePtrPtr, Offsets.GameStateBase_SearchMask, Offsets.GameStateBase_SearchPattern) ) {
				Log($"PoEMemory: Failed to find GameStateBasePtrPtr.");
				Detach();
				return;
			}

			if ( !TryRead(GameStateBasePtrPtr + Offsets.GameStateBasePtrHop1, out int tmpPtr) ) {
				Log($"PoEMemory: Failed to find GameStateBasePtr.");
				Detach();
				return;
			}

			GameStateBasePtr = GameStateBasePtrPtr + tmpPtr + Offsets.GameStateBasePtrHop2;
			if ( !TryRead(GameStateBasePtr, out IntPtr gameStateBase) ) {
				Log($"PoEMemory: Failed to find GameStateBase.");
				Detach();
				return;
			}

			GameRoot = new GameStateBase() { Address = gameStateBase };
			// GameStateBaseAddr = new Address(0, gameStateBase);
			// GameStateBase = Read<Offsets.GameStateBase>(GameStateBaseAddr);
			// Run_ObjectBrowser("GameRoot", GameRoot);
			Log($"PoEMemory: Game State Base is {GameRoot.Address}");
			Debugger.RegisterOffset("GameRoot", GameRoot.Address);
			Debugger.RegisterStructLabels<Offsets.GameStateBase>("GameRoot", GameRoot.Address);

			Debugger.RegisterOffset("InGameState", GameRoot.InGameState.Address);
			Debugger.RegisterStructLabels<Offsets.InGameState>("InGameState", GameRoot.InGameState.Address);
			// Debugger.RegisterStructLabels<Offsets.InGameState_Data>("InGameState.Data", GameRoot.InGameState.Cache.Value.ptrData);

			OnAreaChange += (sender, area) => {
				Log("OnAreaChange: " + area);
			};

			// The ActiveGameStates list is a PoEMemoryList (not fixed size)

		}

		private static void DecorateForDebugger(Element cursor, string label) {
			// walks the UI tree from a root, marking all the nodes with labels in the Debugger
			Debugger.RegisterOffset(label, cursor.Address);
			int index = 0;
			foreach(Element child in cursor.Children) {
				DecorateForDebugger(child, label + "." + index);
				index++;
			}
		}

	}

}
