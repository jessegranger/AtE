using ImGuiNET;
using ProcessMemoryUtilities.Native;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;
using static AtE.Win32;
using static ProcessMemoryUtilities.Managed.NativeWrapper;

namespace AtE {
	public static partial class Globals {

		public static bool IsValid(Process proc) => proc != null && !proc.HasExited;

		public static bool IsValid<T>(ArrayHandle<T> handle, int maxEntries = 10000) where T : unmanaged =>
			Offsets.IsValid(handle.Handle, Marshal.SizeOf<T>(), maxEntries);

		public static string Describe<T>(ArrayHandle<T> array) where T : unmanaged {
			return $"Head: {Describe(array.Handle.Head)} Tail: {Describe(array.Handle.Tail)} Len:{array.Length}";
		}
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

		public static ArrayHandle<T> Empty() => new ArrayHandle<T>(new Offsets.ArrayHandle());
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
		/// The game's data files, each has a name and a base ptr.
		/// </summary>
		public static Dictionary<string, IntPtr> FileRoots;
		private static IntPtr fileRootMatch;

		/// <summary>
		/// Get the sequence of records stored in a game data file.
		/// </summary>
		/// <typeparam name="T">a struct that defines one item in the sequence</typeparam>
		/// <param name="fileName">the game file, eg "Data/Stats.dat"</param>
		/// <returns></returns>
		public static ArrayHandle<T> GetFileContents<T>(string fileName) where T : unmanaged {
			// the FileRoots array is kept up to date via OnAreaChange
			if ( FileRoots != null && FileRoots.TryGetValue(fileName, out IntPtr fileBasePtr) ) {
				// each file starts with a File_InfoBlock, which points to a File_RecordSet
				if ( TryRead(fileBasePtr, out Offsets.File_InfoBlock fileInfo) ) {
					if ( TryRead(fileInfo.Records, out Offsets.File_RecordSet recordSet) ) {
						return new ArrayHandle<T>(recordSet.recordsArray);
					} else {
						Log($"PoEMemory: failed to read File_RecordSet from {Describe(fileInfo.Records)}");
					}
				} else {
					Log($"PoEMemory: failed to read File_InfoBlock from {Describe(fileBasePtr)}");
				}
			} else {
				Log($"PoEMemory: file not found {fileName}");
			}
			return ArrayHandle<T>.Empty();
		}

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
			if( !IsValid(loc) ) {
				return false;
			}
			if( ReadProcessMemory(Handle, loc, ref result) ) {
				return true;
			}
			if( LastError != 6 ) Log($"TryRead: failed to read address {Describe(loc)} error: {LastError}");
			return false;
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
		// if we need to search many patterns, we can do them all in one pass over exeImage for better cache usage
		// eg, we don't currently scan for the file patterns to parse the data files yet
		// TODO: scan for file base pattern

		internal static bool TryFindPatternInExe(out IntPtr result, IntPtr startAddress, IntPtr endAddress, string mask, params byte[] pattern) {
			result = IntPtr.Zero;
			if ( mask.Length != pattern.Length ) {
				throw new ArgumentException("mask and pattern should have the same Length");
			}


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
				Log($"PoEMemory: Reading {size / (1024 * 1024)}M executable image (base={Describe(baseAddress)},ms={Time.ElapsedMilliseconds - started}).");
			} else {
				size = exeImage.Length;
			}

			if ( pattern.Length > size ) {
				return false;
			}

			// convert the input addresses into indexes of the exeImage array we read above (where index 0 == baseAddress)
			long startOffset = startAddress.ToInt64() - baseAddress.ToInt64();
			long strictSizeLimit = size - pattern.Length;
			if( startOffset < 0 ) {
				startOffset = 0;
			}
			if( startOffset > strictSizeLimit ) {
				return false;
			}

			long endOffset = endAddress.ToInt64() - baseAddress.ToInt64();
			if ( endOffset <= 0 || endOffset > strictSizeLimit ) {
				endOffset = strictSizeLimit;
			}
			Log($"PoEMemory: FindPattern of {pattern.Length} bytes within {endOffset - startOffset} byte range");

			long offset = startOffset;
			long bestMatch = 0; // this is not the fastest way to search (it takes no shortcuts)
			long bestMatchScore = 0; // because for debugging, we want to be able to see the near matches
			for ( ; offset < endOffset; offset++ ) {
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
							Log($"PoEMemory: Found pattern at {Describe(result)} offset {offset:X} from base {Describe(baseAddress)}");
							return true;
							break; // break the inner loop, continue at the next offset with a whole new match
						}
					} else {
						break;
					}
				}
			}
			if( result == IntPtr.Zero ) {
				Log($"PoEMemory: Pattern not found.");
				return false;
			} else {
				Log($"PoEMemory: Found pattern at {Describe(result)} offset {result.ToInt64() - baseAddress.ToInt64():X}");
				return true;
			}

		}
		
		internal static IEnumerable<IntPtr> TryFindAllPatternsInExe(IntPtr startAddress, IntPtr endAddress, string mask, params byte[] pattern) {
			if( endAddress == IntPtr.Zero && IsValid(Target) ) {
				endAddress = new IntPtr(Target.MainModule.BaseAddress.ToInt64() + Target.MainModule.ModuleMemorySize);
			}
			while( startAddress.ToInt64() < endAddress.ToInt64() ) {
				if( TryFindPatternInExe(out IntPtr nextMatch, startAddress, endAddress, mask, pattern) ) {
					yield return nextMatch;
					startAddress = new IntPtr(nextMatch.ToInt64() + 1);
				} else {
					yield break;
				}
			}
		}
		public static bool TargetHasFocus = false; // assigned once each frame in OnTick

		/// <summary>
		/// True if a valid Process is open, we have an open Handle to it,
		/// and we found a GameStateBase offset.
		/// </summary>
		public static bool IsAttached => IsValid(Target)
			&& Handle != IntPtr.Zero
			&& IsValid(GameRoot);
		private static bool wasAttached = false;
		private static bool wasFocused = false;

		public static EventHandler OnAttach;
		public static EventHandler OnDetach;

		private static long nextAttach = Time.ElapsedMilliseconds;
		private static long nextCheckResize = Time.ElapsedMilliseconds + 3000;

		internal static void OnTick(long dt) {

			if ( IsAttached ) {
				wasAttached = true;
				TargetHasFocus = Target.MainWindowHandle == Win32.GetForegroundWindow();
				bool hasFocus = Overlay.HasFocus || TargetHasFocus;
				bool enabled = true;
				if( !hasFocus ) {
					enabled = !PluginBase.GetPlugin<CoreSettings>().OnlyRenderWhenFocused;
				}
				// DrawBottomLeftText($"TargetHasFocus:{TargetHasFocus} Overlay Focus: {Overlay.HasFocus} Enabled: {enabled} Foreground: {Describe(Win32.GetForegroundWindow())}", Color.Yellow);
				SpriteController.Enabled =  // same as:
				ImGuiController.Enabled = enabled;// same as:
				D3DController.Enabled = wasFocused;
				if( wasFocused && !enabled ) {
					D3DController.Clear();
				}
				wasFocused = hasFocus;

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
				if ( TargetHasFocus ) {
					D3DController.Clear();
				}
				// if we aren't attached to any process, nothing can be focused
				D3DController.Enabled = TargetHasFocus; // draw one empty frame
				TargetHasFocus = false;
				// also, none of the layers should allow drawing/rendering
				SpriteController.Enabled = false;
				ImGuiController.Enabled = false;
				if( wasAttached ) {
					Detach();
				}
				wasAttached = false;
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

		private static List<IntPtr> debugScanResults = new List<IntPtr>();
		private static Dictionary<IntPtr, List<IntPtr>> debugSecondScanResults = new Dictionary<IntPtr, List<IntPtr>>();


		public static IEnumerable<MEMORY_BASIC_INFORMATION_64> EnumerateAllocatedRanges() {
			if( ! IsValid(Target) ) {
				yield break;
			}
			IntPtr pHandle = Target.Handle;
			SYSTEM_INFO sys_info = new SYSTEM_INFO();
			GetSystemInfo(out sys_info);

			Log($"System Info: {Describe(sys_info.minimumApplicationAddress)} - {Describe(sys_info.maximumApplicationAddress)}");

			// this will store any information we get from VirtualQueryEx()
			MEMORY_BASIC_INFORMATION_64 info = new MEMORY_BASIC_INFORMATION_64();
			IntPtr startAddress = sys_info.minimumApplicationAddress; // IntPtr.Zero; // Target.MainModule.BaseAddress;
			IntPtr endAddress = sys_info.maximumApplicationAddress; // Target.MainModule.BaseAddress + Target.MainModule.ModuleMemorySize;

			while ( startAddress.ToInt64() < endAddress.ToInt64() ) {
				// Log($"Query {Describe(Handle)} at {Describe(startAddress)}");
				if( 0 == VirtualQueryEx(Handle, startAddress, out info, (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION_64))) ) {
					Log($"Query failed (return 0) GetLastError: {GetLastError()}");
					break;
				}
				if ( info.Protect == PAGE_READWRITE && info.State == MEM_COMMIT ) {
					long lBase = info.BaseAddress.ToInt64();
					Log($"{Describe(new IntPtr(lBase))} - {Describe(new IntPtr(lBase + info.RegionSize))} protect {info.Protect} state {info.State}");
					yield return info;
				}
				startAddress = new IntPtr(startAddress.ToInt64() + info.RegionSize);
				if ( info.RegionSize == 0 ) {
					break;
				}
			}
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
				Handle = OpenProcess(ProcessAccessFlags.VirtualMemoryRead | ProcessAccessFlags.QueryInformation, false, Target.Id);
				if ( Handle == IntPtr.Zero ) {
					Log($"PoEMemory: OpenProcess failed.");
					Detach();
					return;
				}
			} catch ( Exception e ) {
				Log($"PoEMemory: failed to attach to Process: {e.Message}");
				Detach();
				return;
			}

			// Patterns are based from ExileApi Memory.cs
			if ( !TryFindPatternInExe(out IntPtr matchAddress, IntPtr.Zero, IntPtr.Zero, Offsets.GameRoot_SearchMask, Offsets.GameRoot_SearchPattern) ) {
				Log($"PoEMemory: Failed to find search pattern in memory.");
				Detach();
				return;
			}
			Log($"PoEMemory: Game State Base Search Pattern matched at {Describe(matchAddress)}");
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
			Log($"PoEMemory: Game State Base is {Describe(GameRoot.Address)}");
			if ( !GameRoot.IsValid ) {
				Log($"PoEMemory: Game State Base address resulted in an invalid GameRoot.");
				Detach();
				return;
			}

			OnAreaChange += (_, area) => Log("OnAreaChange: " + area);

			OnAttach?.Invoke(null, null);

			FileRoots = new Dictionary<string, IntPtr>();
			if ( false ) Run(State.NewState((self, dt) => {
				ImGui.Begin("Debug Files Root");
				foreach(var obj in Target.Modules) {
					ProcessModule module = (ProcessModule)obj;
					if( module.FileName.StartsWith("C:\\Windows\\") ) {
						continue;
					}
					ImGui.Text($"Module: {module.ModuleName} {module.FileName} {Describe(module.BaseAddress)} - {Describe(module.BaseAddress + module.ModuleMemorySize)}");
				}
				if ( ImGui.Button("Scan") && IsValid(Target) ) {
					int scanStride = 8;
					debugScanResults.Clear();
					foreach ( var mem_page in EnumerateAllocatedRanges() ) {
						IntPtr startAddress = mem_page.BaseAddress;
						IntPtr endAddress = new IntPtr(startAddress.ToInt64() + mem_page.RegionSize);
						while ( startAddress.ToInt64() < endAddress.ToInt64() ) {
							// if ( TryRead(startAddress, out IntPtr strPtr) ) { // find a ptr to "Data/"
							if ( TryReadString(startAddress, Encoding.Unicode, out string name) && name.Contains("Data/") ) {
								debugScanResults.Add(startAddress);
							}
							// }
							startAddress += scanStride;
						}
					}
				}
				ImGui.Text($"Results: {debugScanResults.Count}");
				foreach(IntPtr ptr in debugScanResults) {
					if( TryReadString(ptr, Encoding.Unicode, out string name) ) {
						ImGui_Address(ptr, name, "NameAndIndexStruct");
						ImGui.SameLine();
						if( ImGui.Button($"Scan##ptr{Describe(ptr)}") ) {
							IntPtr startAddress = Target.MainModule.BaseAddress;
							IntPtr endAddress = startAddress + Target.MainModule.ModuleMemorySize;
							List<IntPtr> matches = new List<IntPtr>();
							while ( startAddress.ToInt64() < endAddress.ToInt64() ) {
								if ( TryRead<IntPtr>(startAddress, out IntPtr ptrToPtr) && Math.Abs(ptrToPtr.ToInt64() - ptr.ToInt64()) <= 16 ) {
									matches.Add(ptrToPtr);
								}
								startAddress += 1;
							}
							debugSecondScanResults[ptr] = matches;
						}
						if( debugSecondScanResults.TryGetValue(ptr, out List<IntPtr> refs) ) {
							ImGui.Indent();
							ImGui.Text($"References: {refs.Count}");
							foreach(IntPtr ptrToPtr in refs) {
								ImGui_Address(ptrToPtr, "");
							}
							ImGui.Unindent();
						}
					}
				}
				ImGui.End();
				return self;
			}));

			/*
			OnAreaChange += (_, area) => {
				if( IsValid(fileRootMatch) ) {
					return;
				}
				Log($"PoEMemory: Searching for Files root address...");
				foreach(IntPtr fileRootMatch in TryFindAllPatternsInExe(IntPtr.Zero, IntPtr.Zero, Offsets.FileRoot_SearchMask, Offsets.FileRoot_SearchPattern) ) {
					if( ! IsValid(fileRootMatch) ) {
						Log($"PoEMemory: No match for Files root address...");
						continue;
					}
					long fileParseStarted = Time.ElapsedMilliseconds;
					long claimedCount = 0;
					// the fileRootMatch pattern is xxxxxx????x and the ???? int is the local offset we want to read
					// so we read an int from (match + 6)
					if ( ! TryRead(fileRootMatch + 0x6, out int localFileRootOffset) ) {
						Log($"PoEMemory: Failed to read a local offset from match + 6");
						continue;
					}
					Log($"PoEMemory: localFileRootOffset = {localFileRootOffset} (0x{localFileRootOffset:X})");
					// at this local offset (from the match address) is a structure,
					// that structure contains an array of exactly 16 elements, starting at offset 0xA
					// (the structure is the top node in a hierarchical hash table, probably boost's flat_map)
					IntPtr rootBlockArrayStart = fileRootMatch + 0x3 // add 3 for the size of the prior instruction (before the relative offset)
						+ localFileRootOffset +  0x8; // add 8 more after applying the relative offset, TODO: use offset of FileRoot_Ref.ptrToFileRootPtr here
					Offsets.File_RootHeader[] fileRoots = new Offsets.File_RootHeader[16];
					// read all 16 rootBlock array elements at once
					if ( TryRead(rootBlockArrayStart, fileRoots) <= 0 ) {
						Log($"PoEMemory: Failed to read rootBlockArrayStructure from {Describe(rootBlockArrayStart)}");
						continue;
					}
					int scanCount = 0;
					FileRoots.Clear();
					FileRoots["Address"] = fileRootMatch + localFileRootOffset + 0xA;
					for ( int rootIndex = 0; rootIndex < 16; rootIndex++ ) {
						var fileRoot = fileRoots[rootIndex];
						// Log($"PoEMemory: Parsing file root block # {rootIndex}");
						if ( fileRoot.Capacity <= 0 || fileRoot.Capacity > 9999 ) {
							Log($"PoEMemory: Invalid fileRoot Capacity: {fileRoot.Capacity}");
							break;
						}
						claimedCount += fileRoot.Count;
						// each fileRoot block contains up to Capacity buckets
						// its sparse, and each bucket is either full or empty
						// Log($"PoEMemory: File parsing progress: {scanCount} / {16 * 8 * (fileRoot.Capacity + 1) / 8}");
						for ( int bucketIndex = 0; bucketIndex < (fileRoot.Capacity + 1) / 8; bucketIndex++ ) {
							// this is the base ptr of one bucket in the hash table, each bucket holds 8 entries with 1-byte sub keys
							var basePtr = fileRoot.ptrFileNode + (bucketIndex * 0xc8);
							byte[] hashValues = new byte[8];
							if ( (!IsValid(basePtr)) || TryRead(basePtr, hashValues) <= 0 ) {
								Log($"PoEMemory: Failed to read hashValues from basePtr ({Describe(basePtr)})");
								break;
							}
							for ( int hashIndex = 0; hashIndex < 8; hashIndex++ ) {
								// each subkey has a value, but 0xFF is a special value that means an empty slot
								bool empty = hashValues[hashIndex] == 0xFF;
								scanCount += 1;
								if ( !empty ) {
									// read the fileInfoPtr from the array of data in the slot
									if ( ! TryRead(basePtr + 8 + (hashIndex * 0x18) + 8, out IntPtr fileInfoPtr) ) {
										// read the File_InfoBlock struct from that ptr
										if ( TryRead(fileInfoPtr, out Offsets.File_InfoBlock fileInfo) ) {
											string name = fileInfo.strName.Value;
											if ( IsValid(name, 1) ) {
												// Log($"PoEMemory: data file {name} at {Describe(fileInfoPtr)}");
												FileRoots[name] = fileInfoPtr;
											}
										}
									}
								}
							}

						}
					}
					Log($"PoEMemory: parsing ended after {Time.ElapsedMilliseconds - fileParseStarted} ms, found {FileRoots?.Count ?? 0} files of {claimedCount} claimed.");
					if ( FileRoots.Count >= 3 ) break;
				}
				if( FileRoots.Count < 3 ) {
					Log($"PoEMemory: Did not find the real files root.");
				}
			};
			*/
		}

	}

	public class ModEntry {

	}
}
