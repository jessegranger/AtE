using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;

namespace AtE {


	/// <summary>
	/// This class is as close as I can get to a complete map of Path of Exile
	/// memory layout.
	/// Most of this was extracted from now defunct https://github.com/queuete/ExileApi
	/// </summary>
	public static partial class Offsets {
		// partial because GameStat.cs is too large to put inline

		/// <summary>
		/// The current version of this file.
		/// </summary>
		public const int VersionMajor = 1;
		public const int VersionMinor = 15;

		/// <summary>
		/// The most recent version of PoE where at least some of this was tested.
		/// </summary>
		public const string PoEVersion = "3.30.0c";

		/// <summary>
		///  Used as a placeholder where we dont know which struct yet.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Empty { }

		public static bool IsValid(IntPtr p) {
			long a = p.ToInt64();
			return a > 0x0000000100000001 && a < 0x00007FFFFFFFFFFF;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Vector2i {
			[FieldOffset(0x0)] public readonly long Id;
			[FieldOffset(0x0)] public readonly int X;
			[FieldOffset(0x0)] public readonly uint uX;
			[FieldOffset(0x4)] public readonly int Y;
			[FieldOffset(0x4)] public readonly uint uY;
			public Vector2i(int x, int y) {
				Id = uX = uY = 0;
				X = x;
				Y = y;
			}

			public Vector2i(uint x, uint y) {
				Id = X = Y = 0;
				uX = x;
				uY = y;
			}

			public Vector2i(long id) {
				X = Y = 0;
				uX = uY = 0;
				Id = id;
			}

			public override string ToString() {
				return $"<{X}, {Y}>";
			}
		}

		// the PoE engine sometimes stores a string in this format that is either
		// inline (if the string is short) or a pointer once capacity grows past 7
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StringHandle {
			[FieldOffset(0x00)] private readonly IntPtr strFullText; // when the capacity is > 7, this is a ptr to the full text
			[FieldOffset(0x00)] private readonly byte byte0; // Unfortunately, C# cannot store byte[15] in a fixed struct :(
			[FieldOffset(0x01)] private readonly byte byte1;
			[FieldOffset(0x02)] private readonly byte byte2;
			[FieldOffset(0x03)] private readonly byte byte3;
			[FieldOffset(0x04)] private readonly byte byte4;
			[FieldOffset(0x05)] private readonly byte byte5;
			[FieldOffset(0x06)] private readonly byte byte6;
			[FieldOffset(0x07)] private readonly byte byte7;
			[FieldOffset(0x08)] private readonly byte byte8; // once Capacity > 7, the last 8 bytes are used for different things in different places,
			[FieldOffset(0x09)] private readonly byte byte9; // sometimes it holds a pointer to function, sometimes empty
			[FieldOffset(0x0a)] private readonly byte byte10;
			[FieldOffset(0x0b)] private readonly byte byte11;
			[FieldOffset(0x0c)] private readonly byte byte12;
			[FieldOffset(0x0d)] private readonly byte byte13;
			[FieldOffset(0x0e)] private readonly byte byte14; // never read as byte, once capacity is high enough to use this it starts using the pointer
			[FieldOffset(0x0f)] private readonly byte byte15; // never read as byte
			[FieldOffset(0x10)] public readonly long Length; // the current Length
			[FieldOffset(0x18)] public readonly long Capacity; // the current Capacity

			public string Value =>
				// check for invalid sizes, return null
				(Length <= 0 || Length >= 65535 || Capacity <= 0 || Capacity >= 65535 || Length > Capacity) ? null :
				// if the string is small, read it directly from the bytes
				Capacity < 8 ? Encoding.Unicode.GetString(new byte[] {
					byte0, byte1, byte2, byte3,
					byte4, byte5, byte6, byte7,
					byte8, byte9, byte10, byte11,
					byte12, byte13 }, 0, (int)(Length * 2))
				// otherwise, read the full string
				: PoEMemory.TryReadString(strFullText, Encoding.Unicode, out string value, (int)(Length * 2)) ? value : null;
			// this is not ideal, this ref to PoEMemory is the only thing keeping
			// this file from being directly portable to another project
		}

		/// <summary>
		/// Array control structures used throughout PoE.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ArrayHandle {
			[FieldOffset(0x00)] public IntPtr Head;
			[FieldOffset(0x08)] public IntPtr Tail;

			/// <summary>
			/// Record Size is usually something like, Marshal.SizeOf(typeof(T)) where T is the contained type.
			/// </summary>
			/// <summary>The most entries GetRecordPtrs will walk: a longer array is garbage (and yields nothing).</summary>
			public const long MaxEnumerated = 200000;

			public int ItemCount(int recordSize) => (int)((Tail.ToInt64() - Head.ToInt64()) / recordSize);

			/// <summary>
			/// Get a pointer to the start of the i-th entry in the array.
			/// </summary>
			/// <param name="recordSize">Usually from sizeof or Marshal.SizeOf</param>
			/// <returns></returns>
			public IntPtr GetRecordPtr(int i, int recordSize) {
				if ( Head == IntPtr.Zero || Tail == IntPtr.Zero ) return IntPtr.Zero;
				long head = Head.ToInt64();
				var len = Tail.ToInt64() - head;
				var n = i * recordSize;
				if ( n < 0 || n >= len ) return IntPtr.Zero;
				return new IntPtr(head + (i * recordSize));
			}

			/// <summary>
			/// Yield all the entries (as pointers to their start address in memory).
			/// </summary>
			/// <param name="recordSize">Usually from sizeof or Marshal.SizeOf</param>
			/// <returns></returns>
			public IEnumerable<IntPtr> GetRecordPtrs(int recordSize) {
				if ( !(IsValid(Head) && IsValid(Tail)) ) {
					yield break;
				}

				long head = Head.ToInt64();
				long tail = Tail.ToInt64();
				// a garbage handle (two valid pointers a terabyte apart, from a stale offset) would loop for hours on the game's thread:
				// no real array in the game has this many entries
				if ( recordSize <= 0 || tail < head || (tail - head) / recordSize > MaxEnumerated ) {
					yield break;
				}
				while(head < tail) {
					yield return new IntPtr(head);
					head += recordSize;
				}
			}

		}

		/// <summary>
		/// Check that this ArrayHandle has valid Head and Tail pointers.
		/// </summary>
		public static bool IsValid(ArrayHandle handle) => IsValid(handle.Head) && IsValid(handle.Tail);

		/// <summary>
		/// Check that this ArrayHandle, of some Type, has valid pointers and a reasonable count.
		/// </summary>
		public static bool IsValid<T>(ArrayHandle handle, int limit) => IsValid(handle)
			&& (handle.ItemCount(Marshal.SizeOf(typeof(T))) <= limit);

		/// <summary>
		/// Check that this ArrayHandle, of some Type, has valid pointers and a reasonable count.
		/// </summary>
		public static bool IsValid(ArrayHandle handle, int recordSize, int maxEntries) => IsValid(handle) && handle.ItemCount(recordSize) < maxEntries;
	

		/// <summary>
		/// The PoE Game Engine is structured around having one or more
		/// "GameState" objects running at a time.
		/// </summary>
		public enum GameStateType : byte {
			AreaLoadingState,
			WaitingState,
			CreditsState,
			EscapeState, // runs the "Escape" menu on its own layer (the logout menu)
			InGameState, // runs the main game itself
			ChangePasswordState,
			LoginState, // runs the Login form
			PreGameState, // coordinates(?) Login, Create, Select, ChangePassword
			CreateCharacterState,
			SelectCharacterState,
			DeleteCharacterState,
			LoadingState, // not sure the difference from AreaLoading
			InvalidState
		}

		public readonly static string WindowClass = "POEWindowClass";
		public readonly static string WindowTitle = "Path of Exile";

		public readonly static string GameRoot_SearchMask = "xxxxxxxx";
		public readonly static byte[] GameRoot_SearchPattern = new byte[] {
			0x48, 0x8b, 0xf1, 0x33, 0xed, 0x48, 0x39, 0x2d
		};
		// after you find the pattern match, skip to the end of the match, read a local offset found there in the instruction
		// eg, localOffset = Read<int>(match + pattern.Length)
		// then, ptr = Read<IntPtr>(match + localOffset) is address of a GameRoot_Ref struct
		// so, the final game state base ptr is, Read<GameRoot_Ref>(ptr).ptrToGameStateBasePtr

		// 3.30.0c: the running game's build string, "release tags/3.30.0c 329528" (branch/tag, then the build number), is a global
		// narrow std::string (MSVC layout: inline buffer or ptr at 0x00, length 0x10, capacity 0x18) at base+0x3ACBE18, beside the other
		// startup globals (the command line, the Documents path and the install folder, all std::wstrings). It is the "Build\n%s"
		// line of the crash report; this pattern is that snprintf call (unique in 3.30.0c, at base+0xDF1EFB):
		//   lea r9, [build]  /  cmp qword [build.capacity], 0Fh  /  cmova r9, [build]  /  lea r8, ["\nBuild\n%s"]  /  mov edx, 200h
		// The global is at match + 7 + Read<int>(match + 3). If the pattern stops matching, PoEMemory falls back to looking for a
		// std::string in the module image whose text starts with "release " (exactly one in 3.30.0c).
		// The login screen's bottom-right label shows the same version as "PoE1 3.30.0c" (see LoginGameState.elemVersionLabel).
		// A second global std::string, "PoE release/tags/3.30.0c Windows x64" (the HTTP User-Agent), is at base+0x47A93A8.
		public readonly static string GameBuild_SearchMask = "xxx????xxx????xxxxx????xxx????xxxxx";
		public readonly static byte[] GameBuild_SearchPattern = new byte[] {
			0x4c, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00,
			0x48, 0x83, 0x3d, 0x00, 0x00, 0x00, 0x00, 0x0f,
			0x4c, 0x0f, 0x47, 0x0d, 0x00, 0x00, 0x00, 0x00,
			0x4c, 0x8d, 0x05, 0x00, 0x00, 0x00, 0x00,
			0xba, 0x00, 0x02, 0x00, 0x00
		};
		public readonly static string GameBuild_Prefix = "release ";

		public readonly static string FileRoot_SearchMask = "xxxxxx????xxx";
		public readonly static byte[] FileRoot_SearchPattern = new byte[] {
			  /* From the original ExileApi notes: FileRoot Pointer
        00007FF6C47EED01  | 48 8D 0D A8 23 7F 00               | lea rcx,qword ptr ds:[7FF6C4FE10B0]        | <--FileRootPtr
        00007FF6C47EED08  | E8 E3 5C 56 FF                     | call pathofexile_x64.7FF6C3D549F0          |
        00007FF6C47EED0D  | 48 8B 3D A4 23 7F 00               | mov rdi,qword ptr ds:[7FF6C4FE10B8]        |
        00007FF6C47EED14  | 48 8B 1F                           | mov rbx,qword ptr ds:[rdi]                 |
        00007FF6C47EED17  | 48 3B DF                           | cmp rbx,rdi                                |
        00007FF6C47EED1A  | 0F 84 26 01 00 00                  | je pathofexile_x64.7FF6C47EEE46            |
        */
			0x48, 0x8b, 0x08,
			0x48, 0x8d, 0x05,
			0x00, 0x00, 0x00, 0x00,
			0x48, 0x89, 0x0a
			/* How to use Ghidra to find this pattern, if it changes.
			 * Project -> New Project
			 * File -> Import File... PathOfExile.exe
			 *   Analyze? Yes
			 * Search -> For Instruction Pattern
			 *   Pencil Icon [Edit Manually], Enter the full pattern above
			 *   00's are masks (wildcards)
			 *   Search, no results.
			 *   Replace trailing values with 00's until you get a single match.
			 *   In 3.23.2, this had a single match at:
			 *   48 8b 08
			 *   48 8d 00 00 00 00 00
			 *   00 00 00
			 *   (having replaced four previous (wrong) values with 00, it now matches)
			 *   The real four 00's you are looking for, when viewed in the Code Listing, should look something like:
			 *    48 8b 08        MOV        param_1,qword ptr [RAX]
       *    48 8d 05        LEA        RAX,[DAT_14003b5c0]
       *    fc c9 01 00 <-- this here is the local offset that gets read everytime
       *    48 39 01        CMP        qword ptr [param_1],RAX
       *    
       *    Once you see what the real full pattern should be,
       *    use the Code Listing to fill in the right values for the pattern:
       *    48 8b 08
       *    48 8d 05
       *    00 00 00 00
       *    48 39 01
       *    
			 *  But, none of this worked in 3.23.2 update... still working on it.
			 */ 
		};


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameRoot_Ref {
			[FieldOffset(0x0C)] public readonly IntPtr ptrToGameRootPtr;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct FileRoot_Ref {
			[FieldOffset(0x08)] public readonly IntPtr ptrToFileRootPtr;
		}

		// GameRoot members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameRoot {
			[FieldOffset(0x10)] public readonly ArrayHandle CurrentGameStates;
			[FieldOffset(0x28)] public readonly ArrayHandle ActiveGameStates;
			[FieldOffset(0x50)] public readonly GameStateArrayEntry AreaLoadingState;
			[FieldOffset(0x60)] public readonly GameStateArrayEntry WaitingState;
			[FieldOffset(0x70)] public readonly GameStateArrayEntry CreditsState;
			[FieldOffset(0x80)] public readonly GameStateArrayEntry EscapeState;
			[FieldOffset(0x90)] public readonly GameStateArrayEntry InGameState;
			[FieldOffset(0xa0)] public readonly GameStateArrayEntry ChangePasswordState;
			[FieldOffset(0xb0)] public readonly GameStateArrayEntry LoginState;
			[FieldOffset(0xc0)] public readonly GameStateArrayEntry PreGameState;
			[FieldOffset(0xd0)] public readonly GameStateArrayEntry CreateCharacterState;
			[FieldOffset(0xe0)] public readonly GameStateArrayEntry SelectCharacterState;
			[FieldOffset(0xf0)] public readonly GameStateArrayEntry DeleteCharacterState;
			[FieldOffset(0x100)] public readonly GameStateArrayEntry LoadingState;
			[FieldOffset(0x110)] public readonly GameStateArrayEntry ProbeState; // not a real state, put here to probe for the game adding new kinds of states
			// if you look up the ProbeState in the Game UI and find that it has a real GameStateArrayEntry (instead of a terminating 0x00)
			// then a new GameState has been added to the game
		}

		// members of AllGameStates array:
		[StructLayout(LayoutKind.Explicit)] public struct GameStateArrayEntry {
			[FieldOffset(0x0)] public readonly IntPtr ptrToGameState;
			[FieldOffset(0x8)] public readonly IntPtr ptrToHandle; // always pts to 16 bytes before ptrToGameState

			public bool IsValid => ptrToHandle.Equals(ptrToGameState - 16);
		}

		[StructLayout(LayoutKind.Explicit)] public struct GameStateArrayEntryHandle {
			[FieldOffset(0x0)] public readonly IntPtr vtable;
			[FieldOffset(0x8)] public readonly GameStateHandleStatus Status;
			[FieldOffset(0xC)] public readonly int Unknown1;
		}

		[Flags]
		public enum GameStateHandleStatus : int {
			Invalid = 0,
			Idle = 1, // ??
			Paused = 2,
			Running = 4,
			AlsoRunning = 8, // ? only InGameState gets it
			FlickerWhileRunning = 9 // maybe an idle frame for fps cap?
		}

		// GameState members:
		public readonly static int GameState_Kind = 0x209;
		// members of InGameState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState {
			// 3.23.2: 510 new bytes here
			[FieldOffset(0x209)] public readonly GameStateType Kind;
			// 3.23.2: 2 new bytes here
			[FieldOffset(0x218)] public readonly IntPtr ptrData; // ptr to InGameState_Data struct
			// 3.28.0: 8 fewer bytes here
			[FieldOffset(0x270)] public readonly IntPtr ptrWorldData; // ptr to WorldData
			[FieldOffset(0x290)] public readonly IntPtr ptrUIElements; // ptr to InGameState_UIElements
			// NOTE: ptrUIElement is also a ptr to Element,
			//   and is accessible at UIRoot.Children[1] if this offset changes but UIRoot is known

			// [FieldOffset(0x298)] public readonly IntPtr ptrEntityLabelMap;
			// 3.21.2b: 8 new bytes added here
			// 3.22: 248 new bytes here?
			// 3.23: 128 new bytes here?
			// 3.29: 136 new bytes here
			[FieldOffset(0x800)] public readonly IntPtr elemRoot;
			[FieldOffset(0x810)] public readonly IntPtr elemInputFocus; // which element has input focus or null
			[FieldOffset(0x838)] public readonly IntPtr elemHover; // element which is currently hovered
			// 3.29.3: confirmed with MemoryServer /track over 10s (r2=1.0000, no flat intervals)
			// 3.29.4: the four clock fields moved +4 (/track: 1000/s at 0xb90, 0.017 at 0xb94, seconds at 0xb98, then the bytes 1,1,1 at 0xb9c..0xb9e)
			[FieldOffset(0xb90)] public readonly int GameTimeMs; // int32 counter, 1000/s (999.98 measured), ~52 million = 14.6 hours at test time
			[FieldOffset(0xb94)] public readonly float FrameDeltaSec; // jitters 0.016-0.019, last frame duration
			[FieldOffset(0xb98)] public readonly float GameTimeSec; // float32, == GameTimeMs / 1000, 1.0/s
			[FieldOffset(0xb9e)] public readonly bool IsRunning; // moved with them (+4); 3.29.4: reads 1 while running and 0 while the Escape menu is open; it does not change with the SkillTree open (InGameState_Data.IsPaused does)
			// 3.28.0: 40 new bytes here
			// 3.29.3: the mouse position, in client pixels (two int32; found by recording InGameState while the mouse was put over the
			// flasks, (467,1138), and the skill bar, (1509,1125)). The Vector2 of floats at 0x5b8 that was here is stale: it
			// holds a position (153, 863.25) that does not follow the mouse.
			[FieldOffset(0x8a0)] public readonly Vector2i MousePos;
			// 3.28.0: removed
			// [FieldOffset(0x5a0)] public readonly Vector2 UIHoverOffset; // mouse position offset in hovered UI element
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct PreGameState {
			[FieldOffset(0x130)] public readonly IntPtr elemRoot;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EscapeGameState {
			// 3.29: 136 new bytes here
			// 3.29.4: both moved +0x260: elemRoot (0x668) is the 2560x1600 "root"; elemMenu (0x6a0) is null until Escape is pressed, then the "escape_menu" element
			// (found by diffing the state's pointers while the check pressed Escape)
			[FieldOffset(0x668)] public readonly IntPtr elemRoot;
			[FieldOffset(0x6a0)] public readonly IntPtr elemMenu;
		}

		// members of AreaLoadingState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AreaGameState {
			// 3.23: 128 new bytes here
			// 3.23.2: 512 new bytes here
			// 3.29: 128 new bytes here
			// 3.30: 264 new bytes here
			[FieldOffset(0x4d0)] public readonly long IsLoading;
			// 3.29: 8 new bytes here
			[FieldOffset(0x668)] public readonly IntPtr elemRoot;
			// 3.22: 24 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.25: 256 new bytes here
			// 3.28: 8 fewer bytes here
			// 3.29: 16 new bytes here
			// 3.30.0c: 8 new bytes here
			[FieldOffset(0xa58)] public readonly IntPtr strAreaName;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct LoginGameState {
			// 3.30.0c, confirmed live on the login screen: elemRoot is an unnamed 2560x1600 element, child [1] of the shared "root"
			// element, and the parent of login_frame, connecting_frame, login_queue_frame and the version label. While the game is
			// in-game this field is stale: it then points at some small element inside the InGameState UI tree, so check that
			// LoginState is among GameRoot.ActiveGameStates before trusting it.
			[FieldOffset(0x2D0)] public readonly IntPtr elemRoot;
			[FieldOffset(0x2D8)] public readonly IntPtr elemLoginFrame; // "login_frame", elemRoot.Children[2]
			[FieldOffset(0x2E0)] public readonly IntPtr elemLoginButton; // "login_button"
			[FieldOffset(0x388)] public readonly IntPtr elemPasswordTextbox; // "password_textbox"
			[FieldOffset(0x398)] public readonly IntPtr elemUsernameTextbox; // "username_textbox", Element_Text is the remembered login
			[FieldOffset(0x3A8)] public readonly IntPtr elemLoginQueueFrame; // "login_queue_frame"
			[FieldOffset(0x3C0)] public readonly IntPtr elemConnectingFrame; // "connecting_frame"
			// unnamed 143x32 element at (2367, 1550), elemRoot.Children[6]; Element_Text is "PoE1 3.30.0c" (also at +0x498 and +0x540)
			[FieldOffset(0x3F0)] public readonly IntPtr elemVersionLabel;
		}

		// Element members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element {

			// 3.28: 136 new bytes maybe?
			// 3.29: 8 new bytes
			[FieldOffset(0xb8)] public readonly IntPtr elemSelf;
			[FieldOffset(0xc0)] public readonly ArrayHandle Children;
			[FieldOffset(0xf8)] public readonly ArrayHandle unkArrayHandle;

			[FieldOffset(0x138)] public readonly Vector2 ScrollOffset;

			// 3.23: 32 fewer bytes here
			// 3.23.1: 128 more bytes here
			// 3.24.3: 12 fewer bytes here
			// 3.29: 4 new bytes here
			// 3.29.4: Scale, elemParent, the flags and Size moved by 8 bytes (Scale up, the rest down); checked over a tree of 120 live elements
			// 3.29.4: the element's id ("root", "login_frame", "username_textbox"...), a unicode std::wstring; most elements have none (length 0)
			// 3.29.4: most elements now store 1.0 here, meaning "the same as the parent" (the orbs, the experience bar and the panels read 1.0 under the HUD's
			// 0.75: that only puts them in the window if they are drawn at 0.75). Element.Scale resolves it; this is the raw value.
			[FieldOffset(0x1a0)] public readonly float Scale; // should always be 0.75f? or is that just at my resolution?
			// 3.30.0c: 8 new bytes here
			[FieldOffset(0x1b8)] public readonly StringHandle strName;
			// 3.24.3: 8 new bytes here
			[FieldOffset(0x1e0)] public readonly IntPtr elemParent; // ptr to Element

			// 3.29.4: Position moved 0x150 -> 0x144 (-0xc): life_orb (18, 1285), mana_orb (2229, 1285) and experience_bar (816, 1570) read as the
			// orbs' and the bar's places in the 2560x1600 layout. 0x150 still holds a float (18, 16, 816) that is not the position.
			// 3.30.0c: 8 bytes removed here
			[FieldOffset(0x144)] public readonly Vector2 Position;
			[FieldOffset(0x144)] public readonly float X;
			[FieldOffset(0x148)] public readonly float Y;
			// 3.23: 32 fewer bytes here again
			[FieldOffset(0x1e9)] public readonly byte IsVisibleByte; // 3.29.4: bit 8 varies between elements here (0x2e vs 0x26), not yet checked against one known to be shown or hidden
			public bool IsVisibleLocal => (IsVisibleByte & 8) == 8;
			// 3.29.4: 0x61 on the GemLvlUpPanel's buttons (level up all, and each gem's), 0x01 on everything else there
			// (text, gem icons, bars, containers), compared over its 29 elements; not yet checked on other panels' buttons
			[FieldOffset(0x1eb)] public readonly byte IsButtonByte;
			public bool IsButton => IsButtonByte == 0x61;

			// [FieldOffset(0x160)] public readonly uint ElementBorderColor;
			// [FieldOffset(0x164)] public readonly uint ElementBackgroundColor;
			// [FieldOffset(0x168)] public readonly uint ElementOverlayColor;

			// 3.23: 16 new bytes (+ the 64 prior) added here
			// 3.26: 56 new bytes here
			[FieldOffset(0x268)] public readonly Vector2 Size;
			[FieldOffset(0x268)] public readonly float Width;
			[FieldOffset(0x26c)] public readonly float Height;

			// For elements used as Waypoint destination clicks on the AreaInstanceUI, this holds a link to the destination
			// [FieldOffset(0x300)] public readonly Vector2 AreaInstanceElementDestination;

			// everything below here is wrong I think
			// [FieldOffset(0x190)] public readonly uint TextBoxBorderColor;
			// [FieldOffset(0x190)] public readonly uint TextBoxBackgroundColor;
			// [FieldOffset(0x194)] public readonly uint TextBoxOverlayColor;
			// [FieldOffset(0x1C0)] public readonly uint HighlightBorderColor;
			// [FieldOffset(0x1C3)] public readonly bool isHighlighted;
			// [FieldOffset(0x1F0)] public readonly IntPtr unkNextRecord;
			/* items below here greatly increase memory size of this struct
			 * consider replacing with individual reads if they are needed
			 * see: Element_Text below.

			[FieldOffset(0x478)] public readonly StringHandle strText;

			[FieldOffset(0x5E0)] public readonly StringHandle strInputText;
			[FieldOffset(0x608)] public readonly StringHandle strLongText;
			[FieldOffset(0x628)] public readonly int inputMask1;
			// these are masks like: 1011 1010 1010 1010 0011 1111
			// inputMask1: changes with every change to strInput
			[FieldOffset(0x62c)] public readonly int inputMask2;
			// inputMask2 is always:
			// AB AA AA 3F (1068149419) when field is empty
			// 55 55 05 42 (1107645781) when strInput can be used
			public const int inputMask2_HasInput = 1107645781;

			*/

		}
		/// <summary>
		/// A StringHandle, offset from Element Address.
		/// </summary>
		// 3.28.0: 136 bytes above here (inherited from Element)
		// 3.29.4: moved 0x338 -> 0x358 (+0x20): the clock's label reads "13:56" there. The same text is also at 0x490 and 0x538 (and "Fontin", the font, at 0x310 and 0x570): not placed.
		// 3.30.0c: moved 0x358 -> 0x360 (+8, with the rest of Element): on the login screen every label, button caption and textbox
		// reads at 0x360 ("Email", "log in", "Texas (US)", "PoE1 3.30.0c"...) and 0x358 is empty or garbage. The copies moved to 0x498 and 0x540.
		public static readonly int Element_Text = 0x360;

		public static readonly int Element_AreaInstanceDestination_Offset = 0x300;

		// 3.29.4, found live (searched memory for the key codes, then for the pointers to them), on a freshly started game
		// before the Options panel was ever shown: the player's key bindings. The UI root element (InGameState.elemRoot)
		// + UIRoot_Input points at a UIInput, whose Bindings (a std::vector) holds a KeyBindingEntry for each action that
		// has a key. Path: ingame+0x800*+0xD8*+0x3D0. Not used: a static object of the game module (ingame+0x398*) holds
		// the default bindings at +0x528, as { int action; int key; int modifier }, all 69; and once the panel was shown,
		// its Input page has its own copy (ingame+0x290*+0x6A0*+0x428*+0x48*+0x110, 8-byte { key; modifier } entries in
		// action order).
		public static readonly int UIRoot_Input = 0xD8;

		/// <summary>3.29.4: what the UI root points at, at UIRoot_Input (the player's key bindings; see there).</summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct UIInput {
			/// <summary>A std::vector of KeyBindingEntry (begin, end; the capacity's end follows).</summary>
			[FieldOffset(0x3D0)] public ArrayHandle Bindings;
			/// <summary>How many entries Bindings has (one pointer after the vector).</summary>
			[FieldOffset(0x3F0)] public int BindingCount;
		}

		/// <summary>
		/// 3.29.4: one key binding in UIInput.Bindings: Key a virtual key code, Modifier 0x10 / 0x11 / 0x12 (Shift / Ctrl /
		/// Alt) or 0, Action the action's index (the Options panel's Input rows, in order, unbound ones included: see
		/// GameKeys.BindingActions).
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 12)] public struct KeyBindingEntry {
			[FieldOffset(0x0)] public int Key;
			[FieldOffset(0x4)] public int Modifier;
			[FieldOffset(0x8)] public int Action;
		}
		// 3.29.4, compared live across the Act 1 destination elements with the panel open in The Submerged Passage:
		// a byte that is 1 for each area visited (The Twilight Strand ... The Flooded Depths) and 0 for The Ledge (shown,
		// next door, not visited yet); taken as "can be traveled to". (+0x32C read 1 only on the current area.)
		public static readonly int Element_AreaInstanceDestination_Unlocked_Offset = 0x32A;
		// 3.29.4: 1 on the destination of the area the player is in, 0 on the others (seen on The Submerged Passage, and on
		// "The Prison" while in The Lower Prison: the panel's name for an area is not always the area's)
		public static readonly int Element_AreaInstanceDestination_IsCurrentArea = 0x32C;

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AreaInstanceDestination_Details {
			[FieldOffset(0x0)] public readonly IntPtr ptrAreaCodeName; // ptr to string like "1_town"
			[FieldOffset(0x8)] public readonly UInt16 intUnknown08; // int like "08 01 00 00"
			[FieldOffset(0xC)] public readonly UInt16 intUnknown0C; // int like "09 01 00 00"
			[FieldOffset(0x10)] public readonly IntPtr ptrNameCodePair; // ptr to { str, str } like { "1_1_town", "Lioneye's Watch" }
			[FieldOffset(0x18)] public readonly IntPtr ptrWorldAreasData; // ptr to an entry(?) in WorldAreas.dat
			[FieldOffset(0x20)] public readonly UInt32 intUnknown20; // int like "01 00 00 00 00 00 00 00"
			[FieldOffset(0x28)] public readonly IntPtr ptrUnknown28; // ptr to a { ptr, ptr } struct, unknown
			[FieldOffset(0x30)] public readonly IntPtr ptrName; // ptr to string like "Lioneye's Watch"
			[FieldOffset(0x38)] public readonly IntPtr ptrDesc; // ptr to string like "Cold, dank and reeking..."
		}

		// 3.29.3: the skill bar (UIElements.SkillBar): children [0..12] are the 13 skill slots, in the order of
		// use_bound_skill1..13 in production_Config.ini. A slot's child [0][0] is its key label, and the hidden
		// skill bar's children [1..] are the skills to choose from (child [0] is the "Always Attack Without Moving" option).
		/// <summary>ptr to the ActorSkill shown in a skill bar (or hidden skill bar) cell; 0 when empty.</summary>
		// 3.29.4: the skill cell and key label fields moved +0x28 (checked on all 13 cells: SkillId 0x4000 at 0x358 in slot 0, the keys 1, 6, 2, 81... at 0x2c0 of each label)
		public static readonly int Element_SkillCell_Skill = 0x350;
		/// <summary>ushort, the id of the ActorSkill in a skill cell (0x4000 Default Attack, ...); 0 when there is none.</summary>
		public static readonly int Element_SkillCell_SkillId = 0x358;
		/// <summary>
		/// ptr to a skill cell's icon. The hidden skill bar's Movement and Clear cells have no skill, only an icon:
		/// its name (SkillCellIcon_Name) ends with "SkillMoveOnly" or "SkillClear".
		/// </summary>
		public static readonly int Element_SkillCell_Icon = 0x230; // 3.29.4: was 0x238 (-8); checked on the open hidden skill bar: Default Attack's icon is "Art/2DArt/SkillIcons/iconbasicattack.dds"
		/// <summary>ptr to a unicode string, the name of a skill cell's icon (eg, "Art/2DArt/UIImages/InGame/SkillMoveOnly").</summary>
		// 3.29.4: moved 0x10 -> 0x08. The Movement and Clear cells' icons are no longer named ("SkillMoveOnly", "SkillClear"): they are rectangles of one atlas,
		// "Art/Textures/Interface/2D/2DArt/UIImages/InGame/16.dds|1be,5b7,1fe,5f7|0" (Movement) and "...16.dds|2ff,1d8,35f,238|0" (Clear), see SkillBar.MoveIconSprite
		public static readonly int SkillCellIcon_Name = 0x08;
		/// <summary>int, the Windows virtual key code (1 = left mouse, 0x51 = Q, ...) on a skill slot's key label; the modifier is not here.</summary>
		public static readonly int Element_SkillKeyLabel_VKey = 0x2c0;

		[StructLayout(LayoutKind.Explicit)] public struct ChildrenArrayEntry {
			[FieldOffset(0x0)] public readonly IntPtr ptrChild;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldData {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0xA0)] public readonly IntPtr ptrToWorldAreaRef;
			// 3.12.2b: some new bytes here
			// 3.32.2: 16 fewer bytes here?
			// 3.29.4: moved 0x1A8 -> 0x198 (-0x10): the 1920x1200 window size is at +0x170 of it, the player projects to (960,484) with the matrix, and
			// Position (+0x270) is 502 behind the player on both x and y. The struct itself is unchanged.
			[FieldOffset(0x198)] public readonly Camera Camera;
			// Note to self: when trying to find the right Camera offset,
			// use the fact (seen below in Camera struct) that the Width and Height
			// are integers that are easy to spot in memory, and come after the end
			// of the main camera matrix
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldAreaRef {
			// 3.23: 40 new bytes here?
			// 3.24: 8 fewer bytes here
			// 3.24.3b: 8 bytes removed here
			// 3.27: 8 new bytes here
			// 3.29: 8 new bytes here
			// 3.29.4: 16 new bytes here (found in the data struct this overlaps: the pointer to "1_1_1" / "The Twilight Strand" moved 0xb0 -> 0xc0)
			[FieldOffset(0xc0)] public readonly IntPtr ptrToWorldAreaDetails;
			[FieldOffset(0xc8)] public readonly IntPtr ptrUnk90; // moved with it (+0x10), unchecked
			// 3.24.3b: 8 bytes added here
			[FieldOffset(0x124)] public readonly uint areaHash; // 3.29.4: see InGameState_Data.CurrentAreaHash: 0x124 is a guess, 0x128 is the other candidate
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldAreaDetails {
			[FieldOffset(0x00)] public readonly IntPtr strRawName;
			[FieldOffset(0x08)] public readonly IntPtr strName;
			[FieldOffset(0x10)] public readonly int Act;
			[FieldOffset(0x14)] public readonly byte IsTownByte;
			public bool IsTown => IsTownByte == 1;
			[FieldOffset(0x15)] public readonly byte HasWaypointByte;
			public bool HasWaypoint => HasWaypointByte == 1;
			// 3.29.3: this struct is the in-memory row of WorldAreas.dat; Connections is a list of ptrs to the rows of the neighboring areas
			// (The Coast: Lioneye's Watch, The Mud Flats, The Tidal Island)
			[FieldOffset(0x16)] public readonly long ConnectionsCount;
			[FieldOffset(0x1E)] public readonly IntPtr ptrConnections; // ptr to IntPtr[ConnectionsCount], each a WorldAreaDetails
			[FieldOffset(0x26)] public readonly uint MonsterLevel;
			[FieldOffset(0x2A)] public readonly uint WorldAreaId;
			/* true but useless:
			[FieldOffset(0x36)] public readonly IntPtr strTextureLoadingImage;
			[FieldOffset(0x3e)] public readonly IntPtr ptrQuestData;
			[FieldOffset(0x46)] public readonly IntPtr ptrQuestDataFile;
			[FieldOffset(0x6a)] public readonly IntPtr ptrUnk6a;
			[FieldOffset(0x72)] public readonly IntPtr ptrWorldAreaDetailsOfNearbyTown;
			*/
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Camera {
			[FieldOffset(0x0)] public readonly Matrix4x4 Matrix;
			//  4x4 floats = 16 floats 64 (0x40) bytes
			[FieldOffset(0x170)] public readonly Vector2i Size;
			[FieldOffset(0x170)] public readonly int Width;
			[FieldOffset(0x174)] public readonly int Height;
			// [FieldOffset(0x2a0)] public readonly float ZFar;

			[FieldOffset(0x270)] public readonly Vector3 Position;

			/// <summary>
			/// The clip space position of a world position; W is the distance in front of the camera (negative when behind it).
			/// </summary>
			public Vector4 WorldToClip(Vector3 pos) => Vector4.Transform(new Vector4(pos, 1f), Matrix);

			public static Vector2 ClipToScreen(Vector4 coord, float width, float height) => new Vector2(
				(1.0f + (coord.X / coord.W)) * (width / 2f),
				(1.0f - (coord.Y / coord.W)) * (height / 2f));

			public unsafe Vector2 WorldToScreen(Vector3 pos, float width, float height) {
				Vector2 result; // put a struct on the stack
				Vector4 coord = *(Vector4*)&pos;
				coord.W = 1;
				coord = Vector4.Transform(coord, Matrix);
				coord = Vector4.Divide(coord, coord.W);
				// TODO: The game is developed against a virtual 2560x1600 background
				// for this reason, I think width and height should come from the window
				result.X = (1.0f + coord.X) * (width / 2f);
				result.Y = (1.0f - coord.Y) * (height / 2f);
				return result;
			}
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState_Data {
			// 3.30: found live in a Normal Labyrinth (see LabyrinthData). Not yet checked outside the Labyrinth (Labyrinth.Rooms validates what it reads).
			[FieldOffset(0x048)] public readonly IntPtr ptrLabyrinthData; // ptr LabyrinthData
			// 3.22.1b: 4 new bytes here
			// 3.23: 40 new bytes here
			// 3.24: 8 fewer bytes here
			// 3.29.4: moved 0xD4 -> 0xE4 (+0x10, like WorldAreaRef.ptrToWorldAreaDetails): reads 1 on The Twilight Strand, next to a 60 and a 1
			[FieldOffset(0x0E4)] public readonly byte CurrentAreaLevel;

			// 3.22.1b: 28 bytes removed here
			// 3.24.3b: 8 bytes removed here
			// 3.29.3: WorldData.ptrToWorldAreaRef points at this very struct (WorldAreaRef overlaps InGameState_Data), so this is
			// the same memory as WorldAreaRef.areaHash. It was at 0x110 until then (0x110 now holds unrelated bytes).
			// 3.29.4: the hash is no longer at 0x11c (reads 0). 0x124 and 0x128 both hold a different random-looking uint in each area
			// instance and are constant within one (0x12c holds 0x21 in every instance): 0x124 is a guess between them, NOT confirmed. Check it
			// in a second area instance: the one that is stable, nonzero and differs between instances (the other may be a seed).
			[FieldOffset(0x124)] public readonly uint CurrentAreaHash;
			[FieldOffset(0x120)] public readonly IntPtr MapStats;
			// [FieldOffset(0x260)] public readonly long LabDataPtr; //May be incorrect

			// 3.30: found live (Act 7, Fireflies quest), see MinimapIconList
			[FieldOffset(0x300)] public readonly IntPtr ptrMinimapIcons; // ptr MinimapIconList

			// 16 new bytes here
			// 3.27: 8 new bytes here
			// 3.29.3: the Running / Current / Paused pointers (0x8b8, 0x8c0, 0x8c8) read 0, and IsPaused is its own byte (below): removed.

			// Crucible: 8 new bytes here
			// 3.22: 120 new bytes here
			// 3.22.1b: 128 new bytes here?
			// 3.22.2: 8 bytes removed here
			// 3.24: 128 new bytes here
			// 3.24.3b: 24 bytes removed here
			// 3.27: 8 new bytes here
			// 3.29: 128 new bytes here
			// 3.30: 16 new bytes here
			[FieldOffset(0x978)] public readonly IntPtr ServerData;
			[FieldOffset(0x980)] public readonly IntPtr entPlayer; // ptr Entity
			// 3.27: 16 new bytes here
			// 3.30: 48 new bytes here?
			[FieldOffset(0xa58)] public readonly IntPtr EntityListHead; // ptr EntityListNode
			[FieldOffset(0xa60)] public readonly long EntitiesCount;

			// 3.29.3: confirmed with MemoryServer /track: int32 tick counter, ~9.5/s in steps (96% flat sampling at 20ms),
			// FREEZES while the game is paused (read 16322 constant while paused, resumed from there). Simulation tick, not real time.
			// 3.29.3: SimulationTicks (0xB18, a server tick counter) is removed: it read 218 (330 in another session) and never changed,
			// standing, walking 12 cells, or over 60 s of sampling, and no other slot of this struct, InGameState, WorldData or ServerData
			// counts like it. (The MemoryServer's /hunt and /dump still read 0xB18 by address, for their research.)
			// 3.29.3: found while looking for it: a plain 60 Hz counter (59.95/s, r2=1.0000 over 45 s) that runs while walking.
			// Not known whether it stops while the game is paused.
			// 3.29.4: moved 0x14A0 -> 0x1520 (+0x80; /track over 4 s: 57.6/s, r2=1.0000, the only slot that changed in 0x1400..0x1700)
			[FieldOffset(0x1520)] public readonly int FrameCount;
			// 3.29.4: moved 0xB9C -> 0xBCC (+0x30; found by watching the bytes near the old offset while the SkillTree was toggled)
			// 3.29.3 tentative: bytes at 0xB9C and 0xB9D read 1,1 while paused and 0 while running (single observation each)
			// 3.30 confirmed: bytes at 0xBCC and 0xBCD read 1,1 while paused
			[FieldOffset(0xBCC)] public readonly bool IsPaused;

			// 3.29.3: confirmed with MemoryServer in Oriath Docks (vtable at +0x0, 37x37 tiles, two 426x851 nibble grids),
			// and by drawing both grids in The Coast (blocked/projectile cells line up with walls and water)
			// 3.29.4: moved 0xBF0 -> 0xC48 (+0x58); the struct itself is unchanged (found by the TilesX/TilesY pair 96x30 followed by an array of 96*30*0x38 bytes)
			[FieldOffset(0xC48)] public readonly TerrainData Terrain;
		}

		// 3.30, InGameState_Data.ptrLabyrinthData. Found live in a Normal Labyrinth (Domain Crossing) by searching for pointers to the
		// LabyrinthSectionLayout rows: a vector (begin, end, capacity) of the run's rooms, 10 of them from Entry_1 to the final Izaro fight;
		// each room's neighbour pointers matched its layout row's connections, and the pointer after the vector was the room the player stood in.
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct LabyrinthData {
			[FieldOffset(0x00)] public readonly ArrayHandle Rooms; // of LabyrinthRoom
			[FieldOffset(0x18)] public readonly IntPtr ptrCurrentRoom; // ptr into Rooms
			// 0x20: two ints (0x5125, 16 in one run; 0x5127, 19 in another), not known. Nothing after 0x28 belongs to it, and it holds no positions:
			// the exits' grid positions come from Terrain's transition tiles (and MinimapIconList Entrance icons, but only once an exit has been seen;
			// LabyrinthDoor icons are puzzle doors, Labyrinth/Objects/Puzzle_Parts/Door_Closed, and that list grows as the room is revealed).
		}

		// 3.30, one room of the Labyrinth run (LabyrinthData.Rooms). Which of its room type's areas fills it is not known
		// (bytes 0x58, 0x59 differ between rooms of one type, maybe that); byte 0x5B equals the layout row's connection count.
		[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 0x60)] public struct LabyrinthRoom {
			[FieldOffset(0x00)] public readonly IntPtr ptrNeighbour0; // ptrs to the neighbouring rooms (in LabyrinthRooms), zero when fewer
			[FieldOffset(0x08)] public readonly IntPtr ptrNeighbour1;
			[FieldOffset(0x10)] public readonly IntPtr ptrNeighbour2;
			[FieldOffset(0x18)] public readonly IntPtr ptrNeighbour3;
			[FieldOffset(0x28)] public readonly IntPtr ptrSectionLayout; // a row of LabyrinthSectionLayout.dat
			[FieldOffset(0x5B)] public readonly byte ConnectionCount;
		}

		// 3.30, InGameState_Data.ptrMinimapIcons: every minimap icon of the area, including those of entities far outside the
		// entity list's range (the yellow quest markers on the large map). Found live in Act 7 with 5 FireflyChests on the map: a search for
		// the grid position (491,423) of the one loaded chest (entity 429) found its record, whose +0xA0 was 429; pointer search led
		// to this vector. 13 records: 6 QuestObject (ids 429..434), the area transitions (id 67 a loaded AreaTransition, Entrance icon), an NPC, ...
		// Stale copies of the array (with the ids shifted) are left in the heap: only read it through this pointer.
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct MinimapIconList {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x18)] public readonly ArrayHandle Icons; // of MinimapIconRecord
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1, Size = Size)] public struct MinimapIconRecord {
			public const int Size = 0xB0;
			[FieldOffset(0x00)] public readonly IntPtr ptrIconRow; // a row of MinimapIcons.dat (stride 0x22, its first field the Id, eg "QuestObject")
			[FieldOffset(0x08)] public readonly IntPtr ptrIconTable;
			[FieldOffset(0x10)] public readonly int IconIndex; // the row's index in MinimapIcons.dat: 4 NPC, 6 QuestObject, 7 QuestItem, 8 Shrine, 9 StoryGlyph, 19 Entrance, 23 LabyrinthDoor, 637 Strongbox
			[FieldOffset(0x14)] public readonly Vector2i GridPos; // the same cells as Positioned.GridPos
			[FieldOffset(0x60)] public readonly IntPtr ptrEntity;
			[FieldOffset(0xA0)] public readonly uint EntityId; // this id is set even when the Entity is not loaded yet (so ptrEntity can be null)
			// 0xA8, 0xA9: read 1, 1 in most records; 0, 0 on a far LabyrinthDoor never approached (3.30, Estate Walkways). Not known (seen/revealed?)
		}

		// 3.30, a row of LabyrinthSectionLayout.dat (stride 0x6C; rows are packed: unaligned)
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct LabyrinthSectionLayoutRow {
			[FieldOffset(0x00)] public readonly IntPtr ptrSection; // a row of LabyrinthSection.dat, whose first field is its Id (eg "End_1_BranchN")
			[FieldOffset(0x10)] public readonly int Index; // 1-based, within the section
			[FieldOffset(0x14)] public readonly long ConnectedCount; // of the Index of the rooms it connects to
			[FieldOffset(0x44)] public readonly IntPtr ptrRoomType; // a row of LabyrinthAreas.dat
			// 3.30: the room's position on the Labyrinth Map panel, within its section (+MapX right, +MapY down; checked against screenshots).
			// In one Normal run x ran 0..3 with every Boss/FinalBoss room at x=3 (eg Entry_2_SimpleS: EntranceBranch (1,1), IndoorStraightBottleneck
			// (2,1.5), Boss2 (3,1)). The map is no projection of the room's grid (no fixed rotation fits two rooms), but its ORDER holds: going
			// clockwise, a room's neighbours come in the same order on the map as their exit tiles do on screen (4 of 4 rooms; see
			// Labyrinth.NameExitsByOrder).
			[FieldOffset(0x54)] public readonly float MapX;
			[FieldOffset(0x58)] public readonly float MapY;
		}

		// 3.30, a row of LabyrinthAreas.dat (stride 0x4C): a room type and the areas that can fill it, per difficulty
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct LabyrinthAreasRow {
			[FieldOffset(0x00)] public readonly IntPtr strId; // eg "OutdoorQuad"
			[FieldOffset(0x08)] public readonly long NormalCount; // then 4 lists (count, ptr) of 16-byte refs (WorldAreaDetails ptr, table ptr):
			[FieldOffset(0x10)] public readonly IntPtr ptrNormal;  // Normal, Cruel, Merciless, Eternal
			[FieldOffset(0x18)] public readonly long CruelCount;
			[FieldOffset(0x20)] public readonly IntPtr ptrCruel;
			[FieldOffset(0x28)] public readonly long MercilessCount;
			[FieldOffset(0x30)] public readonly IntPtr ptrMerciless;
			[FieldOffset(0x38)] public readonly long EternalCount;
			[FieldOffset(0x40)] public readonly IntPtr ptrEternal;
		}

		// 3.29.3, InGameState_Data.ServerData. Found by searching ServerData's pointees for arrays of passive ids, and
		// confirmed by allocating Frost Walker (44955): the array went from 91 to 92 ids with 44955 in its sorted place.
		// ServerData+0xC2E8 points at the same array, 0x10 further on (at +0x190 of that object).
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ServerData {
			// 3.29.4: moved 0xC2E0 -> 0xC758, and AllocatedPassives 0x180 -> 0x190. Found with one passive allocated: the only array handle of exactly one ushort (id 57264) among
			// everything ServerData points at. 0xC760 holds a second pointer, 0x10 *before* this one (the old second pointer was 0x10 after), whose handle at +0x1A0 is the same array.
			[FieldOffset(0xC758)] public readonly IntPtr ptrPassives; // ptr ServerData_Passives
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ServerData_Passives {
			// of ushort: the passive ids the character has allocated (the ids of Path of Building's tree data), sorted
			[FieldOffset(0x190)] public readonly ArrayHandle AllocatedPassives;
			// the unspent and refund point counts were not found here (no 1 -> 0 change when the last point was spent)
		}

		// 3.29.3, the open passive tree: UIElements.SkillTree.Child[1] is a canvas in tree coordinates (Path of Building's
		// group x,y), offset by its ScrollOffset (half its size) and zoomed by its Scale. Its children are groups (at their
		// group's x,y) of nodes, and a few nodes of their own; a node's center is at its Position + Size/2 (orbit radius
		// and angle checked on group 141). Each node leads to its passive id, eg Witch nodes near her start, ids 8879 14419
		// 34750 at orbit 3 index 1, 3, 4.
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_PassiveNode {
			// 3.29.4: moved 0x298 -> 0x2c0 (+0x28): over 377 nodes the Witch's ids 8879, 14419 and 34750 read orbit 3 at indexes 1, 3 and 4 (as their goldens say); PassiveNodeData
			// and PassiveSkillRow are unchanged. The canvas (child 1 of the tree) has about 700 groups, so its children array is over 5 KB.
			[FieldOffset(0x2c0)] public readonly IntPtr ptrNodeData; // ptr PassiveNodeData
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct PassiveNodeData {
			[FieldOffset(0x08)] public readonly IntPtr ptrPassiveSkill; // a row of PassiveSkills.dat (rows are packed: unaligned)
			[FieldOffset(0x1C)] public readonly int Orbit;
			[FieldOffset(0x20)] public readonly int OrbitIndex;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct PassiveSkillRow {
			[FieldOffset(0x30)] public readonly ushort Id;
		}

		/// <summary>
		/// The static terrain of the current area. The grids are in the same cell coordinates as Positioned.GridPos.
		/// Each grid is 4 bits per cell: cell (x,y) is in byte (y * BytesPerRow + x / 2), even x in the low nibble.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct TerrainData {
			public const int CellsPerTile = 23;
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x18)] public readonly long TilesX;
			[FieldOffset(0x20)] public readonly long TilesY;
			[FieldOffset(0x28)] public readonly ArrayHandle TileDetails; // 0x38 bytes per tile, TilesX * TilesY of them
			// [FieldOffset(0x40)] public readonly long VerticesX; // TilesX + 1
			// [FieldOffset(0x48)] public readonly long VerticesY; // TilesY + 1
			// [FieldOffset(0x50)] public readonly ArrayHandle Vertices; // 3 bytes per vertex
			// [FieldOffset(0x68)] public readonly ArrayHandle TileMeshes; // refs to ".tmd" files
			// 0 = blocked, 1..5 = walkable, larger values have more clearance from walls
			[FieldOffset(0xD0)] public readonly ArrayHandle WalkableGrid;
			// a superset of WalkableGrid (includes water, excludes buildings): projectile / line-of-sight, same encoding
			[FieldOffset(0xE8)] public readonly ArrayHandle ProjectileGrid;
			[FieldOffset(0x100)] public readonly int BytesPerRow;
		}

		/// <summary>
		/// One entry of TerrainData.TileDetails, row-major (index = tileY * TilesX + tileX).
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct TileDetails {
			public const int Size = 0x38;
			[FieldOffset(0x00)] public readonly IntPtr ptrSubTiles;
			[FieldOffset(0x08)] public readonly IntPtr ptrTileFile; // shared by every tile cut from the same .tdt
			[FieldOffset(0x30)] private readonly long unk30; // makes sizeof(TileDetails) == Size, so arrays of it can be read in bulk
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct TileFile {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			// eg, "Metadata/Terrain/Beach/Transitions/BeachToSwamp/act1_area2_transition_v01_01.tdt"
			// 3.29.3: in The Coast, all three exits are cut from .tdt files with "transition" in their path
			[FieldOffset(0x08)] public readonly StringHandle Path;
		}

		/// <summary>
		/// In the global list of world entities, each node looks like this.
		/// Recursively follow all the First,Second,Third links for GetEntities()
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EntityListNode {
			[FieldOffset(0x00)] public readonly IntPtr First;
			[FieldOffset(0x08)] public readonly IntPtr Second;
			[FieldOffset(0x10)] public readonly IntPtr Third;
			[FieldOffset(0x28)] public readonly IntPtr Entity;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState_UIElements {
			// 3.22.2: 32 bytes removed here
			// 3.24: 32 bytes added here
			// [FieldOffset(0x250)] public readonly IntPtr GetQuests;
			// 3.23: 16 bytes added here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer bytes here
			// 3.28: 136 new bytes here (inherited from Element)
			// 3.29: 24 new bytes here
			// 3.29.4: every field from GameUI to RootBuffPanel moved +0x28 (checked by Element.strName and sizes: GameUI is "HUD", LifeBubble "life_orb" 315x315, Flasks "flask_bar" 340x136,
			// SkillBar "skills_bar", HiddenSkillBar "skills_list", ...). The fields after RootBuffPanel (NpcDialog...) are NOT shifted by the same amount (four item selectors were inserted) and are unverified.
			[FieldOffset(0x360)] public readonly IntPtr PopupManager; // Name = "PopupManager"
			// 3.30.0c: 8 new bytes above here (added PopupManager)
			[FieldOffset(0x368)] public readonly IntPtr GameUI; // Name = "HUD"
			[FieldOffset(0x370)] public readonly IntPtr autoHUDLeft; // Name = "HUDLeft"
			[FieldOffset(0x378)] public readonly IntPtr autoHUDRight; // Name = "HUDRight"
			[FieldOffset(0x380)] public readonly IntPtr LifeBubble; // Name = "life_orb"
			[FieldOffset(0x388)] public readonly IntPtr ManaBubble; // Name = "mana_orb"
			[FieldOffset(0x390)] public readonly IntPtr autoPlayerShield; // Name = "player_shield"
			[FieldOffset(0x398)] public readonly IntPtr autoPlayerShieldOverMana; // Name = "player_shield_over_mana"
			[FieldOffset(0x3a8)] public readonly IntPtr Flasks; // Name = "flask_bar"
			[FieldOffset(0x3b0)] public readonly IntPtr ExperienceBar; // Name = "experience_bar"
			[FieldOffset(0x3c0)] public readonly IntPtr autoMenu; // Name = "menu"
			[FieldOffset(0x3c8)] public readonly IntPtr OpenMenuPopoutButton; // Name = "menu_button"
			[FieldOffset(0x3e0)] public readonly IntPtr CurrentTime; // Name = "clock"
			[FieldOffset(0x3e8)] public readonly IntPtr autoOptionsButton; // Name = "optionsButton"
			[FieldOffset(0x3f0)] public readonly IntPtr autoSocialButton; // Name = "socialButton"
			[FieldOffset(0x3f8)] public readonly IntPtr autoCharacterButton; // Name = "characterButton"
			[FieldOffset(0x400)] public readonly IntPtr autoPassiveButton; // Name = "passiveButton"
			[FieldOffset(0x408)] public readonly IntPtr autoInventoryButton; // Name = "inventoryButton"
			[FieldOffset(0x410)] public readonly IntPtr autoCosmeticsButton; // Name = "cosmeticsButton"
			[FieldOffset(0x418)] public readonly IntPtr autoMtxstashButton; // Name = "mtxstashButton"
			[FieldOffset(0x420)] public readonly IntPtr autoChallengesButton; // Name = "challenges_button"
			[FieldOffset(0x428)] public readonly IntPtr autoEventsPanelButton; // Name = "events_panel_button"
			[FieldOffset(0x430)] public readonly IntPtr autoWorldButton; // Name = "worldButton"
			[FieldOffset(0x438)] public readonly IntPtr autoAtlasButton; // Name = "atlas_button"
			[FieldOffset(0x440)] public readonly IntPtr autoPantheonButton; // Name = "pantheon_button"
			[FieldOffset(0x448)] public readonly IntPtr autoBattlePassButton; // Name = "battle_pass_button"
			[FieldOffset(0x450)] public readonly IntPtr autoMarketButton; // Name = "market_button"
			[FieldOffset(0x458)] public readonly IntPtr autoMarketBrowserButton; // Name = "market_browser_button"
			[FieldOffset(0x460)] public readonly IntPtr autoMapButton; // Name = "mapButton"
			// 3.23.2: 8 fewer bytes here
			// 3.27: 8 new bytes here
			[FieldOffset(0x478)] public readonly IntPtr GreenShopButton; // Name = "buyButton"
			[FieldOffset(0x480)] public readonly IntPtr HelpPanelButton; // Name = "tutorial_panel_button"
			[FieldOffset(0x490)] public readonly IntPtr SkipAllTutorials; // Name = "skip_all_tutorials_button"
			[FieldOffset(0x4a8)] public readonly IntPtr autoChatButton; // Name = "chatButton"
			[FieldOffset(0x4b8)] public readonly IntPtr Mouse; // Name = null
			[FieldOffset(0x4c0)] public readonly IntPtr SkillBar; // Name = "skills_bar"
			[FieldOffset(0x4c8)] public readonly IntPtr HiddenSkillBar; // Name = "skills_list"
			[FieldOffset(0x4f0)] public readonly IntPtr autoPvpNotificationManager; // Name = "pvp_notification_manager"
			[FieldOffset(0x520)] public readonly IntPtr autoSpectatingBanner; // Name = "spectating_banner"
			[FieldOffset(0x528)] public readonly IntPtr autoWaitingBanner; // Name = "waiting_banner"
			[FieldOffset(0x530)] public readonly IntPtr autoHideoutEditModeBanner; // Name = "hideout_edit_mode_banner"
			[FieldOffset(0x538)] public readonly IntPtr autoCountdownManager; // Name = "countdown_manager"
			[FieldOffset(0x540)] public readonly IntPtr autoBotleftButtonsLayout; // Name = "botleft_buttons_layout"
			// 3.29.3: the level-up button (GameUI[3][0]), shown while there are passive points to spend: Child[1].Text is
			// how many ("1" with one point). +0x3D0 has another element with the same text, in a hidden panel.
			[FieldOffset(0x548)] public readonly IntPtr PassivePointsButton; // Name = "available_points_button"
			[FieldOffset(0x550)] public readonly IntPtr autoQuestStateChangedButton; // Name = "quest_state_changed_button"
			[FieldOffset(0x558)] public readonly IntPtr autoAtlasSkillPointsButton; // Name = "atlas_skill_points_button"
			[FieldOffset(0x560)] public readonly IntPtr autoAtlasPassUnclaimedRewardsButton; // Name = "atlas_pass_unclaimed_rewards_button"
			[FieldOffset(0x568)] public readonly IntPtr autoWeaponTreeSkillUnlockedButton; // Name = "weapon_tree_skill_unlocked_button"
			// Crucible: 8 new bytes here
			// 3.26: 8 new bytes here
			[FieldOffset(0x570)] public readonly IntPtr ChatBoxRoot; // Name = null
			[FieldOffset(0x590)] public readonly IntPtr autoBestiaryMasterMissionDisplay; // Name = "bestiary_master_mission_display"
			[FieldOffset(0x598)] public readonly IntPtr autoBestiaryCaptureNotification; // Name = "bestiary_capture_notification"
			[FieldOffset(0x5a0)] public readonly IntPtr autoResourceBarsLayout; // Name = "resource_bars_layout"
			[FieldOffset(0x5a8)] public readonly IntPtr autoDelveEnergyBar; // Name = "delve_energy_bar"

			// 3.30.0c: 16 new bytes here
			[FieldOffset(0x5b0)] public readonly IntPtr QuestTracker; // Name = null
			[FieldOffset(0x5f0)] public readonly IntPtr autoHideoutUnlockNotification; // Name = "hideout_unlock_notification"
			[FieldOffset(0x638)] public readonly IntPtr OpenLeftPanel; // Name = (not an Element: 0)
			[FieldOffset(0x640)] public readonly IntPtr OpenRightPanel; // Name = (not an Element: 0)
			[FieldOffset(0x668)] public readonly IntPtr InventoryPanel; // Name = null
			[FieldOffset(0x670)] public readonly IntPtr StashElement; // Name = null
			[FieldOffset(0x678)] public readonly IntPtr GuildStashElement; // Name = null
			[FieldOffset(0x688)] public readonly IntPtr SocialPanel; // Name = null
			[FieldOffset(0x690)] public readonly IntPtr SkillTree; // Name = null
			[FieldOffset(0x6a0)] public readonly IntPtr MapDevice; // Name = null
			[FieldOffset(0x6b0)] public readonly IntPtr CharacterPanel; // Name = null
			[FieldOffset(0x6b8)] public readonly IntPtr OptionsPanel; // Name = null
			[FieldOffset(0x6c0)] public readonly IntPtr ChallengesPanel; // Name = null
			[FieldOffset(0x6c8)] public readonly IntPtr PantheonPanel; // Name = null
			[FieldOffset(0x6d0)] public readonly IntPtr PvPPanel; // Name = null
			[FieldOffset(0x6d8)] public readonly IntPtr AreaInstanceUi; // Name = null
			[FieldOffset(0x6f0)] public readonly IntPtr autoTutorialPanel; // Name = "tutorial_panel"

			[FieldOffset(0x718)] public readonly IntPtr RelicPanel; // Name = null
			[FieldOffset(0x720)] public readonly IntPtr Map; // Name = "map_overlay"
			[FieldOffset(0x728)] public readonly IntPtr ItemsOnGroundLabelElement; // Name = null
			[FieldOffset(0x760)] public readonly IntPtr autoModalDialogOverlay; // Name = "modal_dialog_overlay"
			[FieldOffset(0x7a8)] public readonly IntPtr RootBuffPanel; // Name = (not a pointer: 0x000100003F800000)
			[FieldOffset(0x7b0)] public readonly IntPtr HelpOrKillBandit; // Name = (not an Element: points at a pair of equal pointers)
			// [FieldOffset(0x7d8)] public readonly IntPtr NpcDialog; // Name = (not a pointer: 0x7)
			// [FieldOffset(0x7e0)] public readonly IntPtr NpcOptions; // Name = (not a pointer: 0x8)
			[FieldOffset(0x7e8)] public readonly IntPtr autoTopIconsLayout; // Name = "top_icons_layout"
			[FieldOffset(0x7f0)] public readonly IntPtr QuestRewardWindow; // Name = null
			[FieldOffset(0x7f8)] public readonly IntPtr autoInventoryViewItemSelector; // Name = "InventoryViewItemSelector"
			[FieldOffset(0x800)] public readonly IntPtr PurchaseWindow; // Name = "InPlaceCraftingItemSlotItemSelector"
			[FieldOffset(0x808)] public readonly IntPtr autoItemSelector; // Name = "ItemSelector"
			[FieldOffset(0x810)] public readonly IntPtr SellWindow; // Name = "NumericValueEditor"
			[FieldOffset(0x818)] public readonly IntPtr TradeWindow; // Name = null
			[FieldOffset(0x820)] public readonly IntPtr NpcOptions; // Name = null
			[FieldOffset(0x828)] public readonly IntPtr autoLoreAudioInterface; // Name = "lore_audio_interface"
			[FieldOffset(0x848)] public readonly IntPtr CardTradePanel; // Name = null
			[FieldOffset(0x898)] public readonly IntPtr CraftBench; // Name = null
			[FieldOffset(0x8a0)] public readonly IntPtr UnveilWindow; // Name = null
			[FieldOffset(0x8e8)] public readonly IntPtr autoDelveMap; // Name = "delve_map_window"
			[FieldOffset(0x9b0)] public readonly IntPtr autoSentinelDroneDisplay; // Name = "sentinel_drone_display"
			[FieldOffset(0x9b8)] public readonly IntPtr BuffsPanel; // Name = null
			[FieldOffset(0x9d0)] public readonly IntPtr DelveDarkness; // Name = null; Debuffs Panel
			[FieldOffset(0xa18)] public readonly IntPtr autoAncestralNotificationManager; // Name = "ancestral_notification_manager"
			[FieldOffset(0xa48)] public readonly IntPtr autoKalguurRecruitment; // Name = "KalguurRecruitmentWindow"
			[FieldOffset(0xac0)] public readonly IntPtr autoBrequelSkillTree; // Name = "brequel_skill_tree_window"
			[FieldOffset(0xAD0)] public readonly IntPtr autoDeepWaterCharts; // Name = "deepwater_charts_window"; opened by clicking a unique with the augury_boss_frozen buff (3.29.4, from the user; not checked live yet)
			[FieldOffset(0xad8)] public readonly IntPtr autoDeepwaterCrafting; // Name = "deepwater_crafting_window"
			[FieldOffset(0xae0)] public readonly IntPtr autoDeepwaterExtraction; // Name = "deepwater_extraction_window"
			[FieldOffset(0xae8)] public readonly IntPtr autoDeepwaterCollection; // Name = "deepwater_collection_window"
			[FieldOffset(0xaf0)] public readonly IntPtr autoDeepwaterActiveVoyage; // Name = "deepwater_active_voyage_window"
			[FieldOffset(0xaf8)] public readonly IntPtr autoDeepwaterOpenActiveVoyageButton; // Name = "deepwater_open_active_voyage_button"
			[FieldOffset(0xb00)] public readonly IntPtr autoDeepwaterVoyage; // Name = "deepwater_voyage_window"
			[FieldOffset(0xb08)] public readonly IntPtr autoBottledItemReveal; // Name = "bottled_item_reveal_window"
			[FieldOffset(0xb10)] public readonly IntPtr AuguryCardSelectionWindow; // Name = "augury_card_selection_window"
			[FieldOffset(0xb18)] public readonly IntPtr autoAuguryPinnacleCardCollection; // Name = "augury_pinnacle_card_collection_window"
			[FieldOffset(0xb20)] public readonly IntPtr autoAuguryEffigyViscera; // Name = "augury_effigy_viscera_window"
			[FieldOffset(0xb58)] public readonly IntPtr autoAncestralTrialRespawnTotemHealthDisplay; // Name = "ancestral_trial_respawn_totem_health_display"
			[FieldOffset(0xb60)] public readonly IntPtr autoMinionDisplay; // Name = "minion_display"
			[FieldOffset(0xb68)] public readonly IntPtr autoSigilDisplay; // Name = "sigil_display"
			[FieldOffset(0xb70)] public readonly IntPtr autoBuffDisplay; // Name = "buff_display"
			[FieldOffset(0xb78)] public readonly IntPtr autoDebuffDisplay; // Name = "debuff_display"
			[FieldOffset(0xba0)] public readonly IntPtr autoTabEditor; // Name = "tab_editor"
			[FieldOffset(0xba8)] public readonly IntPtr autoItemNoteEditor; // Name = "item_note_editor"
			[FieldOffset(0xbb0)] public readonly IntPtr autoUnstack; // Name = "unstack_window"
			[FieldOffset(0xbb8)] public readonly IntPtr autoAsyncTradePriceItemDialog; // Name = "async_trade_price_item_dialog"
			[FieldOffset(0xbc0)] public readonly IntPtr autoAtlasTreeEditor; // Name = "atlas_tree_editor"
			[FieldOffset(0xbd0)] public readonly IntPtr autoInstanceChooser; // Name = "instance_chooser_window"
			// [FieldOffset(0xA58)] public readonly IntPtr SkillBarNotifyPanel1;
			[FieldOffset(0xBD8)] public readonly IntPtr autoWaitingForTrade; // Name = "waiting_for_trade_window"
			[FieldOffset(0xbe8)] public readonly IntPtr autoMysteryBox; // Name = "mystery_box_window"
			[FieldOffset(0xbf8)] public readonly IntPtr autoReclaimMtx; // Name = "reclaim_mtx_window"
			[FieldOffset(0xc00)] public readonly IntPtr autoMtxCombine; // Name = "mtx_combine_window"
			[FieldOffset(0xc30)] public readonly IntPtr autoDeathScreen; // Name = "death_screen"
			[FieldOffset(0xc38)] public readonly IntPtr autoShowIncursionTempleButton; // Name = "show_incursion_temple_button"
			[FieldOffset(0xc40)] public readonly IntPtr autoBetrayalLegendButton; // Name = "betrayal_legend_button"
			[FieldOffset(0xc48)] public readonly IntPtr autoSkipButtonLayout; // Name = "skip_button_layout"
			[FieldOffset(0xc50)] public readonly IntPtr autoAuguryShowCardsButton; // Name = "augury_show_cards_button"
			[FieldOffset(0xc58)] public readonly IntPtr autoDeliriumSkipDelayButton; // Name = "delirium_skip_delay_button"
			[FieldOffset(0xc60)] public readonly IntPtr autoBlightSkipDelayButton; // Name = "blight_skip_delay_button"
			[FieldOffset(0xc68)] public readonly IntPtr autoReturnFromUnownedHideoutButton; // Name = "return_from_unowned_hideout_button"
			[FieldOffset(0xc70)] public readonly IntPtr autoReturnFromUnownedHideoutButton2; // Name = "return_from_unowned_hideout_button"
			[FieldOffset(0xc78)] public readonly IntPtr autoToggleLakeMapButton; // Name = "toggle_lake_map_button"
			[FieldOffset(0xc80)] public readonly IntPtr autoKalguurTownMapButton; // Name = "kalguur_town_map_button"
			[FieldOffset(0xc90)] public readonly IntPtr autoOpenWindowButtonLayout; // Name = "open_window_button_layout"
			[FieldOffset(0xc98)] public readonly IntPtr autoRitualOpenShopButton; // Name = "ritual_open_shop_button"
			[FieldOffset(0xca0)] public readonly IntPtr autoExpeditionHudDisplay; // Name = "expedition_hud_display"
			[FieldOffset(0xcd0)] public readonly IntPtr autoChangingAreaMessage; // Name = "changing_area_message"
			[FieldOffset(0xcd8)] public readonly IntPtr autoNotificationDisplay; // Name = "notification_display"
			// 3.30: 104 new bytes here
			[FieldOffset(0xCE8)] public readonly IntPtr GemLvlUpPanel; // Name = (not a pointer: 0x000000083F800000)
			[FieldOffset(0xd30)] public readonly IntPtr autoTutorialDisplay; // Name = "tutorial_display"
			[FieldOffset(0xd38)] public readonly IntPtr autoAdditionalTutorialDisplay; // Name = "additional_tutorial_display"
			[FieldOffset(0xd40)] public readonly IntPtr autoTutorialPopup; // Name = "tutorial_popup"
			[FieldOffset(0xd48)] public readonly IntPtr autoTutorialPopupLabel; // Name = "tutorial_popup_label"
			[FieldOffset(0xd70)] public readonly IntPtr autoFadeFromBlack; // Name = "fade_from_black"
			[FieldOffset(0xe00)] public readonly IntPtr autoLegionCocoon; // Name = "legion_cocoon_window"
			[FieldOffset(0xe48)] public readonly IntPtr autoCadiroOffer; // Name = "cadiro_offer_window"
			[FieldOffset(0xe50)] public readonly IntPtr autoMtxStopWatchMapDeviceDisplay; // Name = "mtx_stop_watch_map_device_display"
			[FieldOffset(0xe58)] public readonly IntPtr autoMtxLeaderboardMapDeviceHudElement; // Name = "mtx_leaderboard_map_device_hud_element"
			[FieldOffset(0xe60)] public readonly IntPtr autoMtxMapDeviceKillTrackerHudElement; // Name = "mtx_map_device_kill_tracker_hud_element"
			[FieldOffset(0xe68)] public readonly IntPtr autoMtxMercilessMapDeviceKillTrackerHudElement; // Name = "mtx_merciless_map_device_kill_tracker_hud_element"
			[FieldOffset(0xe70)] public readonly IntPtr autoMtxSanctumHourglassMapDeviceHudElement; // Name = "mtx_sanctum_hourglass_map_device_hud_element"
			[FieldOffset(0xe78)] public readonly IntPtr autoMtxPrisonerAltarMapDeviceHudElement; // Name = "mtx_prisoner_altar_map_device_hud_element"
			[FieldOffset(0xe80)] public readonly IntPtr autoSacrificeHideoutHudElement; // Name = "SacrificeHideoutHudElement"
			[FieldOffset(0xe88)] public readonly IntPtr autoAtlasPass; // Name = "atlas_pass_window"
			[FieldOffset(0xe90)] public readonly IntPtr autoAtlasPassRewardReceivedModal; // Name = "atlas_pass_reward_received_modal"
			[FieldOffset(0xE98)] public readonly IntPtr LastElement;

		}

		// Entity offsets
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Entity {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x08)] public readonly IntPtr ptrDetails;
			[FieldOffset(0x10)] public readonly ArrayHandle ComponentBasePtrs; // of IntPtr (to base address of a Component)
			[FieldOffset(0x38)] public readonly uint SeedMaybe;
			// [FieldOffset(0x50)] public readonly Vector3 WorldPos; // possible
			// 3.23: 48 new bytes here
			// 3.24: 8 bytes removed here
			// 3.24.3b: 8 bytes removed here
			// 3.28.0: 8 bytes added here
			[FieldOffset(0x88)] public readonly uint Id;
		}

		public static bool IsValid(Entity ent) {
			return IsValid(ent.vtable)
				&& IsValid(ent.ptrDetails)
				&& IsValid<IntPtr>(ent.ComponentBasePtrs, 50) // at most N components
				;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EntityDetails {
			[FieldOffset(0x08)] public readonly IntPtr ptrPath;
			// 3.27: 8 fewer bytes here
			[FieldOffset(0x28)] public readonly IntPtr ptrComponentLookup; // used to find which Component is which in the ComponentsArray
		}

		/// <summary>
		/// EntityDetails has a ptrComponentLookup to one of these array control structures.
		/// ComponentMap is an array of (string, int) pairs in a special packed format.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ComponentLookup {
			[FieldOffset(0x28)] public readonly IntPtr ComponentMap;
			[FieldOffset(0x48)] public readonly long Capacity;
			[FieldOffset(0x50)] public readonly long Counter;
		}

		/* as of 3.23.2 this appears to be no longer correct:
		/// <summary>
		/// Each entry in a ComponentLookup.ComponentMap is a packed entry like this.
		/// Where each Flag0 indicates if Pointer0 is filled.
		/// FlagX can have multiple values, but only one is known: 0xFF
		/// If FlagX == 0xFF then PointerX is empty
		/// </summary>
		[StructLayout(LayoutKind.Sequential, Pack = 1)] public struct ComponentArrayEntry {
			public readonly byte Flag0;
			public readonly byte Flag1;
			public readonly byte Flag2;
			public readonly byte Flag3;
			public readonly byte Flag4;
			public readonly byte Flag5;
			public readonly byte Flag6;
			public readonly byte Flag7;

			public readonly ComponentNameAndIndexStruct Pointer0;
			public readonly ComponentNameAndIndexStruct Pointer1;
			public readonly ComponentNameAndIndexStruct Pointer2;
			public readonly ComponentNameAndIndexStruct Pointer3;
			public readonly ComponentNameAndIndexStruct Pointer4;
			public readonly ComponentNameAndIndexStruct Pointer5;
			public readonly ComponentNameAndIndexStruct Pointer6;
			public readonly ComponentNameAndIndexStruct Pointer7;

			// pretty sure this is one bucket entry from a flat_map < string, Component >
			// the FlagX bytes each contain either a reduced hash key or 0xFF
			// something like _mm_cmpeq_pi8 (x86 asm) is used to do an initial comparison of all the FlagX bytes
			// the result is a mask like 0x0000FF0000000000 which is used to read the right PointerX struct
			// and returns the matching index (which is always a fixed offset away).
			// (since we are currently not able to reconstruct the right hash key)
			// we have to parse this structure, pull out all the Keys and load them into a new map

			// Some x86 instructions that might be relevant
			// _mm_cmpeq_pi8 : compares all the flags at once, returns 8 bytes, like 0x0000FF0000000000
			// as long as we dont know to make the reduced hash key, we cant use this to skip the parsing altogether
			// but, could still use it to quickly eliminate all the (FlagX == 0xFF) entries
			// (where the PointerX would end up being invalid/empty)
			// but that is a very small benefit...
			// ... we could "cheat" and learn the hash keys instead of compute them, since we are only talking about Components and there are only a couple dozen
			// ... once a key has been learned, during ParseComponent, it could skip the string read, and use only the PointerX.Index

		}
		*/

		/// <summary>
		/// Once you know that some PointerX in a ComponentArrayEntry, each struct looks like this.
		/// Index refers to the Entity.ComponentsArray (not the ComponentMap)
		/// ie, ComponentsArray[Index] is the base address of the Component instance in memory
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct NameAndIndexStruct {
			[FieldOffset(0x00)] public readonly IntPtr ptrName;
			[FieldOffset(0x08)] public readonly uint Index;
			[FieldOffset(0x0c)] public readonly uint Padding;
		}


		// Component members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component {
			[FieldOffset(0x08)] public readonly IntPtr entOwner; // Entity
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Life {
			[FieldOffset(0x8)] public readonly IntPtr entOwner;
			[FieldOffset(0x188)] public readonly int ReservedFlatHP;
			[FieldOffset(0x18c)] public readonly int ReservedPercentHP;
			[FieldOffset(0x19c)] public readonly float CurHPRegen;
			[FieldOffset(0x1a4)] public readonly int MaxHP;
			[FieldOffset(0x1a8)] public readonly int CurHP;

			[FieldOffset(0x1d8)] public readonly int ReservedFlatMana;
			[FieldOffset(0x1dc)] public readonly int ReservedPercentMana;
			[FieldOffset(0x1f0)] public readonly float ManaRegen;
			[FieldOffset(0x1F4)] public readonly int MaxMana;
			[FieldOffset(0x1F8)] public readonly int CurMana;

			// 3.26: 16 new bytes here
			[FieldOffset(0x23c)] public readonly int MaxES;
			[FieldOffset(0x240)] public readonly int CurES;

			// 3.19 [FieldOffset(0x230)] public readonly float Regen;
			// 3.19 [FieldOffset(0x234)] public readonly int MaxHP;
			// 3.19 [FieldOffset(0x238)] public readonly int CurHP;

		}

		// Actor members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Actor {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x08)] public readonly IntPtr entOwner; // Entity
			// 3.22.1c: 8 new bytes here
			// 3.24: 8 new bytes here
			// 3.30.0c: moved 0x1B8 -> 0x1B0 (0x1B8 is now null while acting): Kitava, mid KitavaDeathBreathRight, points
			// from here at an object whose fields (see Component_Actor_Action) name his target, its cell and the skill
			[FieldOffset(0x1B0)] public readonly IntPtr ptrAction; // ptr Component_Actor_Action
			[FieldOffset(0x218)] public readonly Component_ActionFlags ActionFlags;

			[FieldOffset(0x244)] public readonly int AnimationId;

			// TODO: consider reading on demand
			// Crucible: 48 new bytes here
			// 3.24: 48 new bytes here
			[FieldOffset(0x6d8)] public readonly IntPtr entOwnerAgain;
			[FieldOffset(0x6f0)] public readonly ArrayHandle ActorSkillsHandle; // of ActorSkillArrayEntry
			[FieldOffset(0x708)] public readonly ArrayHandle ActorSkillUIStatesHandle; // of ActorSkillUIState
			[FieldOffset(0x720)] public readonly ArrayHandle ActorVaalSkillsHandle; // of ActorVaalSkillArrayEntry
			[FieldOffset(0x740)] public readonly ArrayHandle DeployedObjectsHandle; // of ptr to DeployedObjectsArrayEntry
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkillArrayEntry {
			[FieldOffset(0x00)] public readonly int Padding;
			[FieldOffset(0x08)] public readonly IntPtr ActorSkillPtr; // ptr ActorSkill
		}

		[Flags] public enum Component_ActionFlags : short {
			None = 0,
			Unknown1 = 1,
			UsingAbility = 2, // Using any ability
			Unknown4 = 4,
			Unknown8 = 8,
			AbilityCooldownActive = 16, // Locked in an ability animation, cannot start next action
			Unknown32 = 32,
			Dead = 64,
			Moving = 128,
			WashedUp = 256, // Washed up on the beach at the start, most controls disabled
			Unknown512 = 512,
			Unknown1024 = 1024,
			HasMines = 2048
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Actor_Action {
			// 3.30.0c, re-found live (the old Target 0xB0, Skill 0x150, Destination 0x170 read nothing meaningful):
			// ptrSkill: Kitava mid-cast, pointed at his ActorSkill KitavaDeathBreathRight (Id 16390 at +0x50; the pointer
			// matched entry 19 of his ActorSkillsHandle). Confirmed on one actor only.
			[FieldOffset(0xF8)] public readonly IntPtr ptrSkill; // ptr ActorSkill
			// Target / TargetGrid: Kitava -> the player entity and the player's cell; Sin and Innocence (idle) -> themselves
			// and their own cells. CasterGrid: the actor's own cell (Kitava, Sin, Innocence).
			[FieldOffset(0x128)] public readonly IntPtr Target; // ptr Entity
			[FieldOffset(0x130)] public readonly Vector2i TargetGrid;
			[FieldOffset(0x158)] public readonly Vector2i CasterGrid;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkill {
			[FieldOffset(0x08)] public readonly byte UseStage;
			// [FieldOffset(0x0C)] public readonly byte KeyBindFlags; // unknown how this works but it changes when you change your skill key binds in the UI
			[FieldOffset(0x48)] public readonly IntPtr ptrSelf;
			[FieldOffset(0x50)] public readonly ushort Id;
			// 3.29.4: ptrGemEffects moved 0x58 -> 0x68 (+0x10): the only ptr of Default Attack that leads (ptr, then ptr again, then a string) to "PlayerMelee".
			// 0x58 now points into a table of action names ("Melee", "SandSpitterProjectile"...) and is what the other 12 skills of a character (Move, Dance...) have
			[FieldOffset(0x68)] public readonly IntPtr ptrGemEffects;
			// 3.29.4: these two read 1 and 1 at 0xe8 and 0xea for Default Attack (0xe8 is 1 and 0xea 0 for the 12 other skills): moved +0x28, tentative.
			// The fields below them (TotalUses...) are not checked yet and have probably moved too.
			[FieldOffset(0xe8)] public readonly byte CanBeUsedWithWeapon;
			[FieldOffset(0xea)] public readonly byte CanBeUsed;
			// 3.25: 56 new bytes added here
			// 3.29.4: moved +0x28 with the flags (unchecked: Default Attack has used 0 times and no cooldown; the ints 4 and 36 at 0xfc and 0x100 are the old 0xd4 and 0xd8)
			[FieldOffset(0xf4)] public readonly int TotalUses;
			[FieldOffset(0xf8)] public readonly int CooldownMS;
			[FieldOffset(0x10c)] public readonly int SoulsPerUse;
			[FieldOffset(0x110)] public readonly int TotalVaalUses;
			// 3.25: 24 new bytes added here
			// 3.29.4: moved 0x148 -> 0x168 (+0x20): the same GameStatArray (20 stats of Default Attack at +0xf0, unchanged) is also pointed at from 0x128 (-0x20)
			[FieldOffset(0x168)] public readonly IntPtr ptrGameStats; // ptr to GameStatArray
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkillUIState {
			[FieldOffset(0x10)] public readonly long CooldownLow;
			[FieldOffset(0x18)] public readonly long CooldownHigh;
			[FieldOffset(0x30)] public readonly int NumberOfUses;
			[FieldOffset(0x34)] public readonly int CooldownMS; // the base cooldown, unscaled (not the same as shown in the UI)
			[FieldOffset(0x3C)] public readonly ushort SkillId;
			[FieldOffset(0x44)] public readonly int Padding; // total record size 0x48 is important so the ArrayHandle reads cleanly

			public uint CooldownsUsed => (uint)((CooldownHigh - CooldownLow) >> 4);
			public bool IsOnCooldown => CooldownsUsed >= NumberOfUses;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameStatArray {
			[FieldOffset(0xF0)] public readonly ArrayHandle Values; // of GameStateArrayEntry
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameStatArrayEntry {
			[FieldOffset(0x00)] public readonly GameStat Key;
			[FieldOffset(0x04)] public readonly int Value;
		}

		// each skill gem has an array of these
		// at any time the GemEffectPtr points to one struct in the array
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GemEffects {
			[FieldOffset(0x00)] public readonly IntPtr ptrSkillGem; // ptr to SkillGem
			[FieldOffset(0x10)] public readonly int Level;
			// 3.29.3: 0x14 reads the float 1.0 for both Default Attack and Fireball: not a level requirement, so RequiredLevel is removed
			[FieldOffset(0x18)] public readonly int EffectivenessOfAddedDamage;
			[FieldOffset(0x20)] public readonly int Cooldown;
			[FieldOffset(0x24)] public readonly int StatsCount; // ?

			[FieldOffset(0x28)] public readonly int StatsValue0; // embedded directly?
			[FieldOffset(0x28)] public readonly int VaalSoulsPerUse;

			[FieldOffset(0x2c)] public readonly int StatsValue1;
			[FieldOffset(0x2c)] public readonly int VaalNumberOfUses;

			[FieldOffset(0x30)] public readonly int StatsValue2;
			[FieldOffset(0x34)] public readonly int StatsValue3;

			[FieldOffset(0x38)] public readonly int StatsValue4;
			[FieldOffset(0x38)] public readonly int VaalSoulGainPreventionMS;

			[FieldOffset(0x3c)] public readonly int StatsValue5;
			[FieldOffset(0x40)] public readonly int StatsValue6;
			[FieldOffset(0x44)] public readonly long CostValuesCount;
			[FieldOffset(0x4c)] public readonly IntPtr ptrCostValues; // ptr to int[CostCount]
			[FieldOffset(0x54)] public readonly long CostTypesCount;
			[FieldOffset(0x5c)] public readonly IntPtr ptrCostTypes; // ptr to (ptr CostTypeEntry) array
																															 // [FieldOffset(0x80)] public readonly int ManaMultiplier;
																															 // [FieldOffset(0xE1)] public readonly int SoulGainPreventionTime;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StatsArrayEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrDataFile; // ptr to StatRecord in the Stats data files
			[FieldOffset(0x08)] public readonly int Padding;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct CostTypeEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrName; // ptr to string, like "Mana"
			[FieldOffset(0x08)] public readonly IntPtr ptrDataFile; // ptr to StatRecord in the Stats data files
																															// there are some other strings down here, like "{0} Mana"
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillGem {
			[FieldOffset(0x00)] public readonly IntPtr NamePtr; // ptr to string
			[FieldOffset(0x08)] public readonly long Padding0;
			[FieldOffset(0x10)] public readonly long Padding1;
			[FieldOffset(0x18)] public readonly byte Padding2; // this weird byte makes the next pointer not-aligned
			[FieldOffset(0x19)] public readonly IntPtr UnknownPtr; // ptr to ActiveSkill
			[FieldOffset(0x63)] public readonly IntPtr ptrActiveSkill; // ptr to ActiveSkill
			[FieldOffset(0x6b)] public readonly IntPtr UnknownPtr3; // ptr to ActiveSkill
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActiveSkill {
			[FieldOffset(0x00)] public readonly SkillNames Names;
			// 3.24.3: 8 new bytes here
			[FieldOffset(0x30)] public readonly int CastTypeCount;
			[FieldOffset(0x38)] public readonly IntPtr CastTypes; // ptr to int array
			[FieldOffset(0x40)] public readonly int SkillTagCount;
			[FieldOffset(0x48)] public readonly IntPtr SkillTags; // ptr to array of SkillTagEntry
			// 3.29.3: was at 0x58 (null now); the long description ("Tapping into the...") is 8 bytes further on, and 0x68 is a ptr to a video url
			[FieldOffset(0x60)] public readonly IntPtr LongDescription; // ptr to string unicode
			[FieldOffset(0x60)] public readonly ArrayHandle UnknownArray;
			[FieldOffset(0x68)] public readonly byte UnknownByte;
			[FieldOffset(0x69)] public readonly IntPtr UnknownArrayPtr;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillTagEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrSkillTagDesc; // ptr to string unicode
			[FieldOffset(0x08)] public readonly IntPtr ptrDataFile;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillTagDesc {
			[FieldOffset(0x00)] public readonly IntPtr DisplayName; // ptr to string unicode
			[FieldOffset(0x08)] public readonly IntPtr InternalName; // ptr to ptr to string unicode
			[FieldOffset(0x10)] public readonly IntPtr ptrDataFile; // ptr to a StatRecord in the data files
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillNames {
			[FieldOffset(0x00)] public readonly IntPtr InternalName; // ptr to string unicode
			[FieldOffset(0x08)] public readonly IntPtr DisplayName; // ptr to string unicode
			[FieldOffset(0x10)] public readonly IntPtr Description; // ptr to string unicode
			// 3.29.3: SkillName (0x18) and IconName (0x20) are removed: 0x18 is a ptr to something that is not a string, 0x20 a ptr to a data file
			// object; the icon path ("Art/2DArt/SkillIcons/...") is at 0x28
			[FieldOffset(0x28)] public readonly IntPtr IconName; // ptr to string unicode
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorVaalSkillArrayEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrActiveSkill; // ptr to ActiveSkill struct
			[FieldOffset(0x08)] public readonly IntPtr ptrDataFile; // ptr to a Data file entry
			[FieldOffset(0x10)] public readonly int MaxVaalSouls;
			[FieldOffset(0x14)] public readonly int CurVaalSouls;
			[FieldOffset(0x18)] public readonly long longUnknown; // possibly into DataFile, or a stat array
			// total entry size = 32 bytes
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct DeployedObjectsArrayEntry {
			[FieldOffset(0x00)] public readonly uint EntityId; // matches some Entity.Id in the world
			[FieldOffset(0x04)] public readonly uint SkillId; // matches some Skill.Id in the player's skill array
			[FieldOffset(0x08)] public readonly uint intUnknown;
		}

		/// <summary>
		/// Used to fill in Components where we don't know any other fields yet
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Empty {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_DiesAfterTime {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly IntPtr ptrAt0x20;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Animated {
			[FieldOffset(0x008)] public readonly IntPtr entOwner;
			// 3.22.1c: 72 new bytes here?
			// 3.25: 48 new bytes here?
			/// <summary>
			/// Points at an AnimatedObjectInfo: the animated object's name and its .ao file path, both unicode (eg, for General
			/// Gravicius's falling fireballs, "HellfireBarrageEffect" and
			/// "Metadata/Effects/Spells/monsters_effects/AtlasofWorldsBosses/Gravicius/ballistastorm.ao"). 3.29.3, the same
			/// on all 19 of those entities, whose own metadata path is only "Metadata/Effects/SleepableServerEffect".
			/// </summary>
			[FieldOffset(0x190)] public readonly IntPtr ptrToAnimatedObjectInfo;
			[FieldOffset(0x260)] public readonly IntPtr ptrToAnimatedEntity;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AnimatedObjectInfo {
			[FieldOffset(0x00)] public readonly IntPtr strName; // unicode, eg "HellfireBarrageEffect"
			[FieldOffset(0x08)] public readonly IntPtr strPath; // unicode, the .ao file
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_AnimatedRender {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			/// <summary>
			/// Per-entity constant: the animated object's creation time in seconds on the animation clock
			/// (ClientAnimationController.TimeSpentAnimating == animClockNow - CreationTime). Same information as
			/// TimeSpentAnimating, so it desyncs the same way for minions that refresh their duration.
			/// 3.29.3, verified over 27 AnimatedWeapon observations (slope 1.0/s vs real creation time).
			/// </summary>
			[FieldOffset(0x1E8)] public readonly float CreationTime;
		}

		// 3.29.3: the component is only 0x30 bytes (owner, two pointers, a pointer into itself) and the rest is zero. WorldAreaId (0x28), TransitionType (0x2A)
		// and ptrToWorldArea (0x40) read garbage (a piece of that pointer, 100, 0): removed. The destination is the entity's Render.Name (eg, "Lioneye's Watch").
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_AreaTransition {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Armour {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr ptrToArmourValues;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ArmourValues {
			[FieldOffset(0x10)] public readonly int EvasionMin;
			[FieldOffset(0x14)] public readonly int EvasionMax;
			[FieldOffset(0x18)] public readonly int ArmourMin;
			[FieldOffset(0x1C)] public readonly int ArmourMax;
			[FieldOffset(0x20)] public readonly int ESMin;
			[FieldOffset(0x24)] public readonly int ESMax;
			[FieldOffset(0x28)] public readonly int WardMin;
			[FieldOffset(0x2C)] public readonly int WardMax;
			[FieldOffset(0x38)] public readonly int MoveSpeed;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_AttributeRequirements {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr ptrToAttributeValues;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AttributeValues {
			[FieldOffset(0x10)] public readonly int Strength;
			[FieldOffset(0x14)] public readonly int Dexterity;
			[FieldOffset(0x18)] public readonly int Intelligence;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Base {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr ptrToBaseInfo;
			//[FieldOffset(0x18)] public readonly long ItemVisualIdentityKey;
			//[FieldOffset(0x38)] public readonly long FlavourTextKey;
			[FieldOffset(0x60)] public readonly IntPtr strPublicPrice; // ptr to string unicode
			[FieldOffset(0xC6)] public readonly InfluenceTypes Influences;
			[FieldOffset(0xC7)] public readonly byte IsCorruptedByte;
			public bool IsCorrupted => (IsCorruptedByte & 1) == 1;
			// [FieldOffset(0xC8)] public readonly int UnspentAbsorbedCorruption;
			// [FieldOffset(0xCC)] public readonly int ScourgedTier;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Base_Info {
			[FieldOffset(0x10)] public readonly byte ItemCellSizeX;
			[FieldOffset(0x11)] public readonly byte ItemCellSizeY;
			// 3.29.3: the base type's drop level: Goat's Horn 6, Chain Belt 2, Coral Ring 4, Small / Medium Mana Flask 1 / 3, Driftwood
			// Wand and Iron Greaves 1. Not the level requirement (a Coral Ring requires level 1): that is Component_Mods.RequiredLevel.
			[FieldOffset(0x12)] public readonly byte DropLevel;
			// 3.29.3: read from a Chance to Poison Support gem in the backpack (Metadata/Items/Gems/SupportGemLesserPoison):
			// +0x48 is a StringHandle: u"Chance to Poison" (gems are named without " Support"); a short name is kept in it,
			// not pointed at (a Cleave reward: u"Cleave" in place, Length 6, Capacity 7). +0x90 points at its description
			[FieldOffset(0x48)] public readonly StringHandle Name;
			[FieldOffset(0x90)] public readonly IntPtr strDescription; // ptr to string unicode
			[FieldOffset(0x80)] public readonly IntPtr ptrEntityPath; // ptr to (ptr to?) string ascii "Metadata/..."
			[FieldOffset(0x88)] public readonly IntPtr prtItemType; // ptr to (ptr to?) string unicode
			[FieldOffset(0x90)] public readonly IntPtr ptrBaseItemTypes;
			[FieldOffset(0x98)] public readonly IntPtr XBoxControllerItemDescription;
		}

		[Flags] public enum InfluenceTypes : byte {
			Shaper = 1,
			Elder = 2,
			Crusader = 4,
			Redeemer = 8,
			Hunter = 16,
			Warlord = 32,
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Beam {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// 3.30.0c: moved from 0x50 / 0x5C (zero and a pointer there now). On the Augury crow tunnel's beam
			// (SleepableServerBeamEffect, crow_beam.ao) 0x60 and 0x6C were the world positions of its two portals
			// (CrowTunnelObject), to within a cell; 0x78 / 0x84 repeat them
			[FieldOffset(0x60)] public readonly Vector3 BeamStart;
			[FieldOffset(0x6C)] public readonly Vector3 BeamEnd;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_BlightTower {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x28)] public readonly IntPtr ptrToBlightDetails;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct BlightDetails {
			[FieldOffset(0x00)] public readonly IntPtr strId; // ptr to string unicode
			[FieldOffset(0x08)] public readonly IntPtr strName; // ptr to string unicode
			[FieldOffset(0x18)] public readonly IntPtr strIcon; // ptr to string unicode
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Buff {
			[FieldOffset(0x8)] public readonly IntPtr ptrName; // ptr to (ptr to?) string unicode
			[FieldOffset(0x10)] public readonly IntPtr ptrData; // ptr to data file
			// [FieldOffset(0x18)] public readonly byte IsInvisible;
			// [FieldOffset(0x19)] public readonly byte IsRemovable;
			[FieldOffset(0x18)] public readonly float MaxTime;
			[FieldOffset(0x1C)] public readonly float Timer;
			// [FieldOffset(0x20)] public readonly float UnkFloat20;
			// [FieldOffset(0x24)] public readonly int UnkInt24;
			// [FieldOffset(0x28)] public readonly int UnkInt28;
			// [FieldOffset(0x2c)] public readonly int UnkInt2c;
			// [FieldOffset(0x30)] public readonly int UnkInt30;
			// [FieldOffset(0x34)] public readonly int UnkInt34;
			// [FieldOffset(0x38)] public readonly uint UnkInt38;
			// [FieldOffset(0x3c)] public readonly int UnkInt3c;
			// [FieldOffset(0x40)] public readonly int UnkInt40;
			[FieldOffset(0x40)] public readonly byte Charges; // 3.29.3: confirmed (endurance/frenzy_charge, fortification); impaled_debuff uses it as hits remaining (5 -> 0)
			// [FieldOffset(0x4a)] public readonly int UnkInt4A;
			// [FieldOffset(0x44)] public readonly int UnkInt44;
			// [FieldOffset(0x48)] public readonly int UnkInt48;
			// [FieldOffset(0x4c)] public readonly int UnkInt4c;
			// [FieldOffset(0x50)] public readonly int UnkInt50;
			// [FieldOffset(0x54)] public readonly int UnkInt54;
			[FieldOffset(0x58)] public readonly IntPtr entOwner;
			[FieldOffset(0x68)] public readonly IntPtr entOwnerAgain;
			[FieldOffset(0x80)] public readonly IntPtr ptrGameStatsHandleMaybe;
			// [FieldOffset(0x5c)] public readonly int UnkInt5c;
			// [FieldOffset(0x60)] public readonly IntPtr ptrUnknown60; // ptr to unknown, something art related?
			// [FieldOffset(0x70)] public readonly IntPtr ptrOwner; // ptr to Entity

		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Buffs {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// Crucible: 8 new bytes here
			[FieldOffset(0x160)] public readonly ArrayHandle Buffs; // of IntPtr to Buff
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Charges {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr ptrToChargeDetails;
			[FieldOffset(0x18)] public readonly int NumCharges;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ChargeDetails {
			[FieldOffset(0x14)] public readonly int Max;
			[FieldOffset(0x18)] public readonly int PerUse;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Chest {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// Crucible: 8 bytes added here?
			[FieldOffset(0x160)] public readonly IntPtr ptrToStrongboxDetails;
			[FieldOffset(0x168)] public readonly bool IsOpened;
			[FieldOffset(0x169)] public readonly bool IsLocked;
			[FieldOffset(0x16c)] public readonly byte Quality;
			// [FieldOffset(0x1A8)] public readonly bool IsStrongbox;
			[FieldOffset(0x1C8)] public readonly IntPtr ptrToChestEffect;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ChestEffect {
			[FieldOffset(0x08)] public readonly IntPtr strPath;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StrongboxDetails {
			[FieldOffset(0x20)] public readonly bool WillDestroyAfterOpen;
			[FieldOffset(0x21)] public readonly bool IsLarge;
			[FieldOffset(0x22)] public readonly bool Stompable;
			[FieldOffset(0x25)] public readonly bool OpenOnDamage;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_ClientAnimationController {
			[FieldOffset(0x08)] public readonly IntPtr entOwner; // Entity
			[FieldOffset(0x9C)] public readonly int AnimationId;
			[FieldOffset(0x1A4)] public readonly float TimeSpentAnimating;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_CurrencyInfo {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x28)] public readonly int MaxStackSize;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Flask {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly ArrayHandle arrayOfUnknown;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Inventories {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly ArrayHandle UnknownArray;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InventoryVisual {
			[FieldOffset(0x00)] public readonly IntPtr ptrName;
			[FieldOffset(0x08)] public readonly IntPtr ptrTexture;
			[FieldOffset(0x10)] public readonly IntPtr ptrModel;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Magnetic {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x30)] public readonly int Force;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Map {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr MapDetails;
			[FieldOffset(0x18)] public readonly byte Tier;
			[FieldOffset(0x19)] public readonly byte Series;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct MapDetails {
			[FieldOffset(0x20)] public readonly IntPtr ptrArea;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_MinimapIcon {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly IntPtr strName;
			[FieldOffset(0x30)] public readonly byte IsVisible;
			[FieldOffset(0x34)] public readonly byte IsHide;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Mods {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// 3.21 Crucible: some new 8 bytes added in here
			[FieldOffset(0x38)] public readonly ArrayHandle UniqueName; // of UniqueNameEntry
			// Crucible: some new 8 bytes added in here
			[FieldOffset(0xB0)] public readonly bool Identified;
			[FieldOffset(0x0B4)] public readonly ItemRarity ItemRarity;
			[FieldOffset(0x0C0)] public readonly ArrayHandle ImplicitModsArray; // of ItemModEntry
			[FieldOffset(0x0D8)] public readonly ArrayHandle ExplicitModsArray; // of ItemModEntry
			[FieldOffset(0x0F0)] public readonly ArrayHandle EnchantedModsArray; // of ItemModEntry
			[FieldOffset(0x108)] public readonly ArrayHandle ScourgeModsArray; // of ItemModEntry
			// 3.22.1c: 24 new bytes here
			[FieldOffset(0x210)] public readonly IntPtr ModStats;
			// Crucible: some new 32 bytes added in here
			// 3.24: 8 new bytes here
			// 3.29: 112 new bytes here
			[FieldOffset(0x2b8)] public readonly UInt32 ItemLevel;
			// 3.29.4: ItemLevel is a UInt32 and RequiredLevel is 4 bytes after it (corrected by hand, tested on items dropped by Hillock). On the two Driftwood Wands the golden
			// expects 1 and 1 for, 0x2b8 reads 1 and 0x2bc reads 0 (0x2c0 reads 1), so the wand's RequiredLevel golden will record 0 until that is looked at.
			[FieldOffset(0x2bc)] public readonly UInt32 RequiredLevel;
			// 3.22.1c: 8 new bytes here
			[FieldOffset(0x2c8)] public readonly IntPtr ptrIncubator; // ptr to IncubatorEntry
			[FieldOffset(0x2d8)] public readonly short IncubatorKillCount;
			// 3.22.1c: 1 new bytes here
			[FieldOffset(0x2de)] public readonly byte IsMirrored;
			[FieldOffset(0x2df)] public readonly byte IsSplit;
			[FieldOffset(0x2e0)] public readonly byte IsUsable;
			// 3.22.1c: 1 new bytes here
			[FieldOffset(0x2e3)] public readonly byte IsSynthesised;

			// public const int ItemModRecordSize = 0x38;
			// public const int NameOffset = 0x04;
			// public const int NameRecordSize = 0x10;
			// public const int StatRecordSize = 0x20;
		}
		public enum ItemRarity : uint {
			Normal,
			Magic,
			Rare,
			Unique,
			Gem,
			Currency,
			Quest,
			Prophecy
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct IncubatorEntry {
			[FieldOffset(0x28)] public readonly IntPtr strName; // unicode
		}

		// 3.29.3: each array is (head, tail, capacity) with room for one entry (0x28 bytes) kept inline after it: the first at 0x08, then
		// the next four at 0x50, 0x90, 0xd0 and 0x110 (8 more than they were). Read from the Driftwood Wand: its implicit "8% increased Spell
		// Damage" is the one entry of the first; the others are empty (the Explicit one is not checked against an item with an explicit mod yet).
		// A sixth array, at 0x160 (was FracturedStatsArray at 0x148), holds the same stat line as the first, so it is not fractured stats: removed.
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ModStats {
			[FieldOffset(0x008)] public readonly ArrayHandle ImplicitStatsArray; // of ItemStatEntry
			[FieldOffset(0x050)] public readonly ArrayHandle EnchantedStatsArray; // of ItemStatEntry
			[FieldOffset(0x090)] public readonly ArrayHandle ScourgeStatsArray; // of ItemStatEntry
			[FieldOffset(0x0D0)] public readonly ArrayHandle ExplicitStatsArray; // of ItemStatEntry
			[FieldOffset(0x110)] public readonly ArrayHandle CraftedStatsArray; // of ItemStatEntry
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemModEntry {
			[FieldOffset(0x00)] public ArrayHandle Values; // sometimes (always?) the values are not inlined
			[FieldOffset(0x28)] public readonly IntPtr ptrModsDatEntry; // ptr to File_ModsDat_Entry
			[FieldOffset(0x30)] public readonly IntPtr ptrModsDatFile; // ptr to base of "Data/Mods.dat" file in memory
		}

		// 3.29.3: 0x28 bytes. The first field points at the stat's text with its value in ("8% increased Spell Damage"), not at a Stats.dat entry;
		// the rest (0x08 is 0, 0x10 and 0x18 were 25 and 31, the length of the text and a position in it) is not read.
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemStatEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrText; // ptr to unicode
			[FieldOffset(0x20)] private readonly long unk20; // makes sizeof(ItemStatEntry) == 0x28, so arrays of it read cleanly
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct UniqueNameEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrWord; // ptr to WordEntry
			[FieldOffset(0x08)] public readonly IntPtr Padding; // to make size = 0x10
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WordEntry {
			[FieldOffset(0x04)] public readonly IntPtr ptrWord;
		}


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Monolith {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly ArrayHandle EssenceTypes;
			[FieldOffset(0x70)] public readonly byte OpenStage;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Monster {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly long padding;
			[FieldOffset(0x18)] public readonly IntPtr ptrMonsterData;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct MonsterData {
			[FieldOffset(0x08)] public readonly IntPtr ptrUnknown08;
			[FieldOffset(0x10)] public readonly IntPtr ptrUnknown10; // ptr to ptr to unicode string (the Path string of the monster type)
			[FieldOffset(0x18)] public readonly IntPtr ptrUnknown18;
			[FieldOffset(0x20)] public readonly int intUnknown;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EssenceTypeEntry {
			[FieldOffset(0x04)] public readonly IntPtr ptrType;
		}

		/// <summary>
		/// The Movement component of an object the server moves along a set path (3.30, confirmed live on the Act 6 beacon
		/// payloads, Metadata/QuestObjects/Act6/BeaconPayload, recorded every 100 ms over a 50 s push along its rail).
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Movement {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			/// <summary>
			/// The grid cell it is moving to next: one cell ahead of Positioned.GridPos while it moves (a payload moves
			/// about one cell per 100 ms), and equal to GridPos once it stops (at the end of its rail it stays there). Before
			/// it has ever moved it was (0, 0) on one payload and its own GridPos on the other, so moving is "not (0, 0) and
			/// not GridPos". A stop partway along the rail (the player leaving its circle) has not been seen.
			/// </summary>
			[FieldOffset(0x3C)] public readonly Vector2i NextStep;
			// +0x30: int 2, int 3 on both payloads (constant). +0x50: float 13, +0x58: float -1 (constant).
			// +0x74..+0x7B: bytes 00 01 00 01 | 00 01 01 00 while unfinished, 00 00 00 00 | 00 01 01 01 once at the end
			// of the rail (meaning unknown; Transitionable.State is the confirmed completion flag).
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_NPC {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly byte Hidden;
			[FieldOffset(0x21)] public readonly byte VisibleOnMinimap;
			[FieldOffset(0x48)] public readonly IntPtr Icon;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_ObjectMagicProperties {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// Crucible: 8 new bytes here
			[FieldOffset(0x144)] public MonsterRarity Rarity;
			[FieldOffset(0x150)] public ArrayHandle Mods;
		}
		public enum MonsterRarity : int {
			White,
			Magic,
			Rare,
			Unique,
			Error = 10000
		}


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ObjectMagicProperties_ModEntry {
			[FieldOffset(0x10)] public readonly IntPtr ptrAt10;
			[FieldOffset(0x18)] public readonly IntPtr ptrAt18;
			[FieldOffset(0x28)] public readonly IntPtr ptrAt28;
			[FieldOffset(0x30)] public readonly IntPtr Padding; // so size = 0x38
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Pathfinding {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// 3.22.1c: 168 new bytes here
			[FieldOffset(0xD4)] public readonly Vector2i ClickToNextPosition;
			[FieldOffset(0xdc)] public readonly Vector2i WasInThisPosition;
			// really, the above two Vector2u are the final two hops in whatever the last path was
			// following them, are all the other steps if the path was longer
			// so like, a 10 step path will have steps 9 and 10 at offsets 0xD4 and 0xDC like above
			// and then step 7 will be 0xE4
			// step 6 at 0xEC and so on, in "reverse" order
			// i.o.w, when their path search ends, they unroll the path from back to front, appending to an array that starts at 0xD4
			// the problem as of today is that they dont clean up this array between searches
			// and pointer to the "highest" valid element is missing as of yet (should be a count or a pointer somewhere)

			[FieldOffset(0x518)] public readonly byte IsMoving; // movement type flags, 2 = moving directly (no pathfinding needed)
			// 3.22.1c: 160 fewer bytes here
			[FieldOffset(0x550)] public readonly Vector2i WantMoveToPosition;
			[FieldOffset(0x55c)] public readonly float StayTime;

		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Player {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// Crucible: some new 8 bytes here
			[FieldOffset(0x168)] public readonly StringHandle strName; // unicode : the current character name
			[FieldOffset(0x18c)] public readonly uint XP;
			[FieldOffset(0x190)] public readonly uint Strength;
			[FieldOffset(0x194)] public readonly uint Dexterity;
			[FieldOffset(0x198)] public readonly uint Intelligence;
			// 3.29.3: AllocatedLootId (0x19c) and PantheonMinor / PantheonMajor (0x1a0 / 0x1a1) read 14 / 14 / 0 on a fresh Witch:
			// they land on a second copy of Strength, Dexterity, Intelligence (14, 14, 32 at 0x19c..0x1a4), so they are stale.
			// Not found yet: a character with a pantheon (and a party, for the loot id) is needed to tell where they went.
			[FieldOffset(0x1AC)] public readonly byte Level;
		}

		public enum PantheonGod : byte{
			None,
			TheBrineKing,
			Arakaali,
			Solaris,
			Lunaris,
			MinorGod1,
			MinorGod2,
			Abberath,
			MinorGod4,
			Gruthkul,
			Yugul,
			Shakari,
			Tukohama,
			MinorGod9,
			Ralakesh,
			Garukhan,
			Ryslatha
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Portal {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x30)] public readonly IntPtr ptrWorldArea;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Positioned {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x1E0)] public readonly byte Reaction;
			// [FieldOffset(0x1F1)] public readonly byte Reaction;
			public bool IsHostile => (Reaction & 0x7F) != 1;
			// 4 new bytes here?
			[FieldOffset(0x294)] public readonly Vector2i GridPos;
			[FieldOffset(0x29c)] public readonly float Rotation;
			[FieldOffset(0x2ac)] public readonly float Scale;
			[FieldOffset(0x2b0)] public readonly float Size; // 3.29.3: a float (1.0 on the player), it was declared int
			[FieldOffset(0x2b8)] public readonly Vector2 WorldPos;
		}

		public static Vector3 GridToWorld(Vector2i gridPos, float z) {
			const float gridScale = 0.092f;
			const float gridCenter = 5.434783f;
			return new Vector3(
				gridCenter + (gridPos.X / gridScale),
				gridCenter + (gridPos.Y / gridScale),
				z + 80f
			);
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Quality {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x18)] public readonly int ItemQuality;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Render {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// Crucible: 8 new bytes here
			// 3.23: 8 new bytes here
			// 3.25: 8 fewer bytes
			// 3.27: 128 new bytes here?
			[FieldOffset(0x120)] public readonly Vector3 Pos;
			[FieldOffset(0x12c)] public readonly Vector3 Bounds;
			[FieldOffset(0x148)] public readonly StringHandle Name; // of unicode bytes
			[FieldOffset(0x16C)] public readonly Vector3 Rotation;
			[FieldOffset(0x170)] public readonly float RotationRadians;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_RenderItem {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x28)] public readonly IntPtr strResourceName; // of unicode
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Shrine {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x24)] public readonly byte IsTaken;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_SkillGem {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly IntPtr Details; // ptr to SkillGemDetails
			[FieldOffset(0x28)] public readonly uint TotalExpGained;
			[FieldOffset(0x2C)] public readonly uint Level;
			[FieldOffset(0x30)] public readonly uint ExperiencePrevLevel;
			[FieldOffset(0x34)] public readonly uint ExperienceMaxLevel;
			[FieldOffset(0x38)] public readonly SkillGemQualityType QualityType;
		}
		public enum SkillGemQualityType : uint {
			Superior = 0,
			Anomalous,
			Divergent,
			Phantasmal
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillGemDetails {
			// 3.29.3: SocketColor and MaxLevel were at 0x30 and 0x48 (zeros now): read from Fireball's gem (blue, 3, max level 20, at 0x10 and 0x28)
			// and Arcane Surge Support's. entOwner (0x08) and LimitLevel (0x4c) are removed: unverified.
			[FieldOffset(0x10)] public readonly uint SocketColor;
			[FieldOffset(0x28)] public readonly uint MaxLevel;
		}

		// 3.29.3, read from the Driftwood Wand (2 linked sockets, Fireball in the first): the socket colors are 4 and 4 (white),
		// Links holds the one byte 2, entGem0 is the Fireball gem and entGem1 is 0.
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Sockets {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// the color of each socket, 0 for no socket, 4 for white (the default; see AtE.SocketColor)
			[FieldOffset(0x10)] public readonly int Socket0;
			[FieldOffset(0x14)] public readonly int Socket1;
			[FieldOffset(0x18)] public readonly int Socket2;
			[FieldOffset(0x1c)] public readonly int Socket3;
			[FieldOffset(0x20)] public readonly int Socket4;
			[FieldOffset(0x24)] public readonly int Socket5;
			[FieldOffset(0x28)] public readonly ArrayHandle Links; // of byte: the size of each group of linked sockets, in socket order
			// [FieldOffset(0x40)] an object (vtable at mod+0x35E9DE0), unknown
			[FieldOffset(0x48)] public readonly IntPtr entGem0; // the gem entity in each socket, 0 for empty
			[FieldOffset(0x50)] public readonly IntPtr entGem1;
			[FieldOffset(0x58)] public readonly IntPtr entGem2;
			[FieldOffset(0x60)] public readonly IntPtr entGem3;
			[FieldOffset(0x68)] public readonly IntPtr entGem4;
			[FieldOffset(0x70)] public readonly IntPtr entGem5;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Stack {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr ptrStackDetails; // ptr to Component_Stack_Details
			[FieldOffset(0x18)] public readonly int CurSize;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Stack_Details {
			[FieldOffset(0x28)] public readonly int MaxSize;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Stats {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly IntPtr GameStats; // ptr to GameStatArray
		}

		/// <summary>
		/// An object's named states (eg, an AuguryCardObject's "card_chosen"), each a number. Found 3.29.4 on an unused
		/// AuguryCardObject (the only entity in the area with one): one state, card_chosen = 0.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_StateMachine {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			/// <summary>The state definitions, shared by all entities of the type (see StateMachine_Info).</summary>
			[FieldOffset(0x158)] public readonly IntPtr ptrInfo;
			/// <summary>The current value of each state (long, 8 bytes each), in the order of StateMachine_Info.States.</summary>
			[FieldOffset(0x160)] public readonly ArrayHandle StateValues;
		}
		/// <summary>What Component_StateMachine.ptrInfo points to.</summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StateMachine_Info {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			/// <summary>The states, StateMachine_State records (0xC0 bytes each).</summary>
			[FieldOffset(0x10)] public readonly ArrayHandle States;
			/// <summary>
			/// Some other named records, 0x48 bytes each, a std::string name first (the AuguryCardObject has one,
			/// "wildcallout": maybe an event it can trigger). Not a state: StateValues has one value per States entry only.
			/// </summary>
			[FieldOffset(0x28)] public readonly ArrayHandle Others;
		}
		/// <summary>A state's definition: its name first, a std::string of single-byte chars (inline while Capacity &lt; 16, else a pointer).</summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StateMachine_State {
			public const int Size = 0xC0;
			[FieldOffset(0x00)] public readonly IntPtr ptrName; // the name's chars, if NameCapacity >= 16
			[FieldOffset(0x00)] public readonly long NameInline0; // else the chars are inline here (16 bytes)
			[FieldOffset(0x08)] public readonly long NameInline1;
			[FieldOffset(0x10)] public readonly long NameLength;
			[FieldOffset(0x18)] public readonly long NameCapacity;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Targetable {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// 8 new bytes here
			[FieldOffset(0x50)] public readonly bool IsTargetable;
			[FieldOffset(0x51)] public readonly bool IsHighlightable;
			[FieldOffset(0x52)] public readonly bool IsTargeted;
		}


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_TimerComponent {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x18)] public readonly float TimeLeft;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Transitionable {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			/// <summary>
			/// 3.30, confirmed live in a pit arena (Metadata/Terrain/Mountain/Arena/Pit) as its encounter started: the
			/// entity's current state, a small number whose meaning depends on the entity. PitGate and PitGateTransition 1 -> 2
			/// when the gates closed, and back to 1 when they opened after the last wave (PitSpawnerGate stayed 1); the CockerelEmergePitEncounter / CockerelBowEmergePitEncounter
			/// monsters are 2 while waiting in the pits for their wave and 3 once out (all 12 of each wave).
			/// 3.30, confirmed live on the Act 6 beacon payloads (Metadata/QuestObjects/Act6/BeaconPayload, two per area, no
			/// StateMachine): 1 while unfinished, including while being pushed along its rail; 1 -> 2 on the same 100 ms sample
			/// that it reached the end of the rail (recorded over a 50 s push); the other payload stayed 1.
			/// </summary>
			[FieldOffset(0x120)] public readonly int State;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_TriggerableBlockage {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			// 3.29.3: confirmed on a Door in The Crematorium: 1 -> 0 when the door was opened
			// (and the game then rewrote the walkable grid inside GridMin..GridMax to remove the door)
			[FieldOffset(0x30)] public readonly byte IsClosed;
			[FieldOffset(0x38)] public readonly ArrayHandle UnknownArray;
			// 3.29.3: confirmed on three Doors: an inclusive box of grid cells around the door's GridPos
			[FieldOffset(0x64)] public readonly Vector2i GridMin;
			[FieldOffset(0x6C)] public readonly Vector2i GridMax;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Usable  {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x10)] public readonly IntPtr ptrToUnknown;
			[FieldOffset(0x18)] public readonly long PaddingAlwaysZero;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Weapon {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x28)] public readonly IntPtr Details;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WeaponDetails {
			[FieldOffset(0x14)] public readonly int DamageMin;
			[FieldOffset(0x18)] public readonly int DamageMax;
			[FieldOffset(0x1C)] public readonly int AttackTime;
			[FieldOffset(0x20)] public readonly int CritChance;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_WorldItem {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x28)] public readonly IntPtr entItem;
		}


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_EntityLabel {
			[FieldOffset(0x378)] public readonly StringHandle textHandle;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_ItemsOnGroundLabelRoot {
			// 3.27: 432 bytes here
			// 3.29.4: both moved +0x28 (the list head at 0x510 leads to a circular list whose entries have a label Element at +0x10 and its entity at +0x18); hoverLabel unchecked
			[FieldOffset(0x478)] public readonly ItemsOnGroundLabelEntry hoverLabel; // 2 ptrs, one Element, one Entity (3.29.3: not checked, probably moved like the list below)
			// 3.29.3: the list moved from 0x468 (seen with a Driftwood Wand and the Dying Exile: each entry's +0x10 is the label, +0x18 its entity)
			[FieldOffset(0x510)] public readonly IntPtr labelsOnGroundHead;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemsOnGroundLabelEntry {
			[FieldOffset(0x00)] public readonly IntPtr nextEntry;
			[FieldOffset(0x08)] public readonly IntPtr prevEntry;
			[FieldOffset(0x10)] public readonly IntPtr elemLabel;
			[FieldOffset(0x18)] public readonly IntPtr entItem;
			// this is valid but disabled for now since it makes the struct size so much bigger for little value
			// [FieldOffset(0x398)] public readonly IntPtr Details; // ptr to ItemsOnGroundLabelEntryDetails
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemsOnGroundLabelEntryDetails {
			[FieldOffset(0x38)] public readonly int FutureTime; // a time, in ms, in the future, measured relative to Environment.TickCount
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_InventoryRoot {
			// 3.23: 32 new bytes here
			// 3.24: 8 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer bytes here
			// 3.28: 136 new bytes here (inherited from Element)
			// 3.29: 16 new bytes here
			// 3.29.4: moved 0x430 -> 0x458 (+0x28): the slots follow in the same order, with the sizes of their cells (Helm 156x156, Amulet 78x78, Chest 156x234,
			// the Backpack 936x390 = 12x5, the Flask slot 390x156 = 5x2)
			[FieldOffset(0x458)] public readonly InventoryArray InventoryList;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InventoryArray {
			[FieldOffset(0x00)] public readonly IntPtr Helm;
			[FieldOffset(0x08)] public readonly IntPtr Amulet; // ptr to Element_Inventory
			[FieldOffset(0x10)] public readonly IntPtr Chest;
			[FieldOffset(0x18)] public readonly IntPtr LWeapon;
			[FieldOffset(0x20)] public readonly IntPtr RWeapon;
			[FieldOffset(0x28)] public readonly IntPtr LWeaponSwap;
			[FieldOffset(0x30)] public readonly IntPtr RWeaponSwap;
			[FieldOffset(0x38)] public readonly IntPtr LRing;
			[FieldOffset(0x40)] public readonly IntPtr RRing;
			[FieldOffset(0x48)] public readonly IntPtr GraftOne;
			[FieldOffset(0x50)] public readonly IntPtr Gloves;
			[FieldOffset(0x58)] public readonly IntPtr Belt;
			[FieldOffset(0x60)] public readonly IntPtr Boots;
			[FieldOffset(0x68)] public readonly IntPtr Backpack;
			[FieldOffset(0x70)] public readonly IntPtr Flask;
			[FieldOffset(0x78)] public readonly IntPtr Trinket;
			[FieldOffset(0x80)] public readonly IntPtr LeagueBackpack;
			[FieldOffset(0x88)] public readonly IntPtr LWeaponEffect; // nothing below here is verified/updated
			[FieldOffset(0x90)] public readonly IntPtr LWeaponAddedEffect;
			[FieldOffset(0x98)] public readonly IntPtr RWeaponSkin;
			[FieldOffset(0xa0)] public readonly IntPtr RWeaponEffect;
			[FieldOffset(0xa8)] public readonly IntPtr RWeaponAddedEffect;
			[FieldOffset(0xb0)] public readonly IntPtr HelmSkin;
			[FieldOffset(0xb8)] public readonly IntPtr HelmAttachment;
			[FieldOffset(0xc0)] public readonly IntPtr HelmAttachment1;
			[FieldOffset(0xc8)] public readonly IntPtr BodySkin;
			[FieldOffset(0xd0)] public readonly IntPtr BodyAttachment;
			[FieldOffset(0xd8)] public readonly IntPtr GlovesSkin;
			[FieldOffset(0xe0)] public readonly IntPtr BootsSkin;
			[FieldOffset(0xe8)] public readonly IntPtr Footprints;
			[FieldOffset(0xf0)] public readonly IntPtr Apparition;
			[FieldOffset(0xf8)] public readonly IntPtr CharacterEffect;
			[FieldOffset(0x100)] public readonly IntPtr Portrait;
			[FieldOffset(0x108)] public readonly IntPtr PortraitFrame;
			[FieldOffset(0x110)] public readonly IntPtr Pet1;
			[FieldOffset(0x118)] public readonly IntPtr Pet2;
			[FieldOffset(0x120)] public readonly IntPtr Portal;
			[FieldOffset(0x128)] public readonly IntPtr HelmAttachment2;
			[FieldOffset(0x130)] public readonly IntPtr Unknown37;
			[FieldOffset(0x138)] public readonly IntPtr Cursor;
			[FieldOffset(0x140)] public readonly IntPtr Unknown39;
			[FieldOffset(0x148)] public readonly IntPtr Unknown40;
			[FieldOffset(0x150)] public readonly IntPtr Unknown41;
			[FieldOffset(0x158)] public readonly IntPtr Unknown42;
			[FieldOffset(0x160)] public readonly IntPtr Unknown43;
			[FieldOffset(0x168)] public readonly IntPtr Unknown44;
			[FieldOffset(0x170)] public readonly IntPtr Unknown45;
			[FieldOffset(0x178)] public readonly IntPtr GemMTXScrollPanel;
			[FieldOffset(0x180)] public readonly IntPtr InventoryTabPanel;
			[FieldOffset(0x188)] public readonly IntPtr CosmeticTabPanel;
			[FieldOffset(0x190)] public readonly IntPtr GemMTX;
			[FieldOffset(0x198)] public readonly IntPtr FullInventoryPanel;
			[FieldOffset(0x1a0)] public readonly IntPtr FullCosmeticPanel;
			[FieldOffset(0x1a8)] public readonly IntPtr LeveledGems;
			[FieldOffset(0x1b0)] public readonly IntPtr LWeaponSwapTabPanel;
			[FieldOffset(0x1b8)] public readonly IntPtr RWeaponSwapTabPanel;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_Inventory {
			// 3.29.3: the hover fields are all 0xA0 further on than they were (found by comparing the element with the mouse away and over the
			// Scroll of Wisdom in the last slot: HoverItem became the item's element, the cell became 11,4 twice, CursorInInventory 1). MoveItemHoverState
			// (0x300 here) is assumed to have moved the same way: it only changes while an item is carried on the cursor.
			// 3.29.4: all of them moved another +0x30 (backpack with the mouse away vs over the item at 11,4: HoverItem 0x338, the cell twice at 0x340 and 0x348, CursorInInventory 0x358 = 1,
			// Size (12, 5) at 0x618). MoveItemHoverState moved with them, unchecked.
			[FieldOffset(0x330)] public readonly int MoveItemHoverState;
			[FieldOffset(0x338)] public readonly IntPtr HoverItem; // to InventoryItem Element
			[FieldOffset(0x340)] public readonly int XFake;
			[FieldOffset(0x344)] public readonly int YFake;
			[FieldOffset(0x348)] public readonly int XReal;
			[FieldOffset(0x34c)] public readonly int YReal;
			[FieldOffset(0x358)] public readonly short CursorInInventory;
			// 3.29.3: Size (12, 5) is at 0x5e8 (was 0x46c). ItemCount (was 0x3e8) is removed: 5 items give a 5 at 0x5f0 (and 0x450, 0x490), unverified
			[FieldOffset(0x618)] public readonly Vector2i Size;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_InventoryItem {
			// [FieldOffset(0x3F0)] public readonly IntPtr incorrectTooltip;
			// 3.23: 16 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer byte here
			// 3.28: 136 new bytes here (inherited from Element)
			// 3.29: 16 new bytes here
			// 3.29.4: the four fields below moved +0x28 (the flask slots: entItem at 0x448 leads to Metadata/Items/Flasks/FlaskLife1 and FlaskMana1,
			// Width and Height read 1 and 2 at 0x4ec and 0x4f0). spriteDetails moved with them, unchecked.
			// 3.30.0c: 8 new bytes here
			[FieldOffset(0x450)] public readonly IntPtr entItem;
			// [FieldOffset(0x378)] public readonly IntPtr unkNearPtr0x378;
			// [FieldOffset(0x380)] public readonly int unkInt0x380;
			// [FieldOffset(0x384)] public readonly short unkShort0x384;
			// [FieldOffset(0x386)] public readonly short unkShort0x386;
			// [FieldOffset(0x388)] public readonly IntPtr unkFarPtr0x388;
			[FieldOffset(0x470)] public readonly IntPtr spriteDetails; // ptr to SpriteDetails struct
			// [FieldOffset(0x3D0)] public readonly int unkInt0x3D0;
			// 3.21.2b: 72 new bytes here?
			// 3.29.3: Width and Height (in cells: 1x1 scroll, 1x3 wand, 1x4 sword) were at 0x4cc and 0x4d0, 8 bytes further on than they are
			[FieldOffset(0x4f4)] public readonly int Width;
			[FieldOffset(0x4f8)] public readonly int Height;
			// [FieldOffset(0x3E0)] public readonly int unkInt0x3E0;
			// 3.29.3: InventPosition (0x4a4) read pointer halves, and no pair of ints holds the grid position of every item: removed. (The position
			// is Element.Position over the cell size, see InventoryItem.X and Y.)
			// [FieldOffset(0x3DC)] public readonly byte UnkByte3DC;
			// [FieldOffset(0x3DD)] public readonly byte UnkByte3DD;
			// [FieldOffset(0x3DE)] public readonly byte UnkByte3DE;
			// [FieldOffset(0x3DF)] public readonly byte UnkByte3DF;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SpriteDetails {
			// 3.29.4: a different object (vtable at 0x0): the string is at 0x8, as a ptr to unicode "Art/2DItems/Currency/CurrencyIdentification.dds|0,0,0,0|0" (the file, the
			// rectangle of its atlas, a number), then its length (0x18) and capacity (0x20). The size in pixels (Width, Height) and the "path:" form (strSpriteFileLong)
			// are gone: nothing reads a size from here (0x38..0x48 hold small ints, 3, 4 or 2, 2, that are not the size).
			[FieldOffset(0x08)] public readonly IntPtr strSpriteFile;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_SubMap {
			// 3.23: 24 new bytes here
			// 3.24: 8 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer bytes here
			// 3.28: 136 new bytes here (inherited from Element)
			// 3.29: 16 new bytes here
			// 3.29.4: all three moved +0x28. DefaultShift at 0x350 reads <0, 0> on the small map and <0, -20> on the large one, as their goldens say; Zoom (0.5 on the small
			// map) is at 0x388; Shift is placed next to DefaultShift as it was, unchecked (it reads <0, 0>). The vector at 0x320 (3320.65, 6798.91) is the player's world position.
			// 3.30.0c: 8 new bytes here
			[FieldOffset(0x350)] public readonly Vector2 Shift;
			[FieldOffset(0x358)] public readonly Vector2 DefaultShift; // historically, always < 0, -20 >
			[FieldOffset(0x390)] public readonly float Zoom; // from 0.5 (zoomed out) to 1.5 (zoomed in)
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_RootHeader {
			[FieldOffset(0x00)] public readonly int unk0x00;
			[FieldOffset(0x08)] public readonly IntPtr ptrFileNode; // ptr to File_RootNode
			[FieldOffset(0x10)] public readonly int Capacity; // should this be long? real nodes have small values like 4095
			[FieldOffset(0x18)] public readonly int unk0x18;
			[FieldOffset(0x20)] public readonly long Count; // use long here, so that total struct size comes out to 0x28
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_RootNode {
			[FieldOffset(0x00)] public readonly long unk0x00;
			[FieldOffset(0x08)] public readonly IntPtr ptrFileInfo;
			[FieldOffset(0x10)] public readonly long unk0x10;
			// so that size = 0x18
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_InfoBlock {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x08)] public readonly StringHandle strName;
			// 3.27: 8 bytes removed here?
			[FieldOffset(0x28)] public readonly IntPtr Records;
			
			// 3.21 Crucible: 8 new bytes here?

			[FieldOffset(0x38)] public readonly int AreaCount; // the game increments this each time the area changes after the file is loaded
			[FieldOffset(0x3c)] public readonly int Capacity; // ? a guess for now
			[FieldOffset(0x40)] public readonly int Count; // ? a guess for now, seems to count up forever as long as other players are around
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_RecordSet {
			[FieldOffset(0x00)] public readonly ArrayHandle recordsArray;
			// 3.29.3: recordCount (0x40) is removed: it reads a pointer. The count is the array's length, with the records' size (Mods.dat: 41324 records of 0x2b4 in 3.29.4)
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_StatsDat_Entry {
			[FieldOffset(0x00)] public readonly IntPtr strName;
			[FieldOffset(0x08)] public readonly byte Flag0;
			[FieldOffset(0x09)] public readonly byte IsLocal;
			[FieldOffset(0x0A)] public readonly byte IsWeaponLocal;
			[FieldOffset(0x0B)] public readonly int Type;
			[FieldOffset(0x0F)] public readonly IntPtr longName;
			[FieldOffset(0x61)] public readonly long Padding;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_ModsDat_Entry {
			[FieldOffset(0x000)] public readonly IntPtr strName;
			[FieldOffset(0x008)] public readonly short Hash;
			[FieldOffset(0x00a)] public readonly IntPtr ModTypeEntry; // ptr to File_ModTypeDat_Entry
			[FieldOffset(0x012)] public readonly IntPtr ModTypeFile; // ptr to base of Data/ModTypes.dat
			[FieldOffset(0x01a)] public readonly uint MinLevel;
			[FieldOffset(0x01e)] public readonly IntPtr Stat0Entry; // ptr to a File_StatsDat_Entry
			[FieldOffset(0x026)] public readonly IntPtr Stat0File; // ptr to a file base
			[FieldOffset(0x02e)] public readonly IntPtr Stat1Entry; // ptr to a File_StatsDat_Entry
			[FieldOffset(0x036)] public readonly IntPtr Stat1File;
			[FieldOffset(0x03e)] public readonly IntPtr Stat2Entry; // ptr to a File_StatsDat_Entry
			[FieldOffset(0x046)] public readonly IntPtr Stat2File;
			[FieldOffset(0x04e)] public readonly IntPtr Stat3Entry; // ptr to a File_StatsDat_Entry
			[FieldOffset(0x056)] public readonly IntPtr Stat3File;
			[FieldOffset(0x058)] public readonly uint Domain;
			[FieldOffset(0x062)] public readonly IntPtr displayName;
			[FieldOffset(0x06a)] public readonly AffixType AffixType;
			[FieldOffset(0x06e)] public readonly long Group;
			[FieldOffset(0x076)] public readonly IntPtr Family; // 3.29.3: ptr to ptr to ptr to Unicode, "SpellDamage" for the Driftwood Wand's implicit (one more pointer than was said)
			[FieldOffset(0x07e)] public readonly int Stat0Min;
			[FieldOffset(0x082)] public readonly int Stat0Max;
			[FieldOffset(0x086)] public readonly int Stat1Min;
			[FieldOffset(0x08a)] public readonly int Stat1Max;
			[FieldOffset(0x08e)] public readonly int Stat2Min;
			[FieldOffset(0x092)] public readonly int Stat2Max;
			[FieldOffset(0x096)] public readonly int Stat3Min;
			[FieldOffset(0x09a)] public readonly int Stat3Max;
			[FieldOffset(0x09e)] public readonly long TagCount;
			[FieldOffset(0x0A6)] public readonly IntPtr TagArray;
			[FieldOffset(0x0ae)] public readonly int TagChanceCount;
			[FieldOffset(0x0B6)] public readonly IntPtr TagChanceArray;
			[FieldOffset(0x0be)] public readonly int UnkTagsCount;
			[FieldOffset(0x0C6)] public readonly IntPtr UnkTagsArray;
			[FieldOffset(0x17e)] public readonly byte IsEssence;
			[FieldOffset(0x204)] public readonly IntPtr MainTag; // ?
			// 3.29.4: the records are 0x2b4 bytes now (41324 of them; they were 0x28e and 40355). Every field up to MainTag (0x204) is where it was (checked on "Strength1", a Strength mod
			// of +8 to 12: its name, type, MinLevel, Stat0, the name, Group, Family, the stat range and the tag arrays); the 0x26 new bytes are after it.
			[FieldOffset(0x2ac)] public readonly long Padding; // so that total size = 0x2b4 (692)
		}

		public enum AffixType : uint {
			None,
			Prefix,
			Suffix,
			Unique,
			Nemesis,
			Corrupted,
			BloodLines,
			Torment,
			Tempest,
			Talisman,
			Enchantment,
			EssenceMonster,
			Unknown12,
			Unknown13,
			DelveArea,
			SynthesisArea,
			SynthesisMonster,
			SynthesisMapCell,
			Unknown14,
			BlightRingAnoint,
			Unknown15,
			EnkindlingOrb,
			InstillingOrb,
			ExpeditionLogbook
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_ModsDat_TagArray_Entry {
			[FieldOffset(0x00)] public readonly IntPtr ptrEntry; // ptr to File_TagsDat_Entry
			[FieldOffset(0x08)] public readonly IntPtr ptrFileBase; // ptr to the base of "Data/Tags.dat"
		}


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_TagsDat_Entry {
			[FieldOffset(0x000)] public readonly IntPtr strName; // ptr to unicode
			[FieldOffset(0x008)] public readonly uint Hash;
			// [FieldOffset(0x00C)] public readonly IntPtr unkPtr0x0C;
			// [FieldOffset(0x014)] public readonly IntPtr unkPtr0x14;
			[FieldOffset(0x014)] public readonly IntPtr displayName;
			// [FieldOffset(0x024)] public readonly int unkInt0x24;
			// [FieldOffset(0x028)] public readonly IntPtr unkPtr0x28;
			// [FieldOffset(0x030)] public readonly IntPtr Padding;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_ModTypeDat_Entry {
			[FieldOffset(0x000)] public readonly IntPtr strName; // ptr to unicode
			// 3.29.3: Hash (0x08) and SellPrice (0x10) are removed: 0x08 holds 1 and 0x10 a pointer to something that is not a string (the record
			// is a name followed by (count, ptr) pairs of lists)
			[FieldOffset(0x018)] public readonly byte Padding; // unknown
		} 


		public const string PATH_STACKED_DECK = "Metadata/Items/DivinationCards/DivinationCardDeck";
		public const string PATH_SCROLL_WISDOM = "Metadata/Items/Currency/CurrencyIdentification";
		public const string PATH_SCROLL_PORTAL = "Metadata/Items/Currency/CurrencyPortal";
		public const string PATH_VAAL_ORB = "Metadata/Items/Currency/CurrencyCorrupt";
		public const string PATH_CHAOS = "Metadata/Items/Currency/CurrencyRerollRare";
		public const string PATH_CHISEL = "Metadata/Items/Currency/CurrencyMapQuality";
		public const string PATH_ALCHEMY = "Metadata/Items/Currency/CurrencyUpgradeToRare";
		public const string PATH_TRANSMUTATION = "Metadata/Items/Currency/CurrencyUpgradeToMagic";
		public const string PATH_ARMOUR_SCRAP = "Metadata/Items/Currency/CurrencyArmourQuality";
		public const string PATH_WHETSTONE = "Metadata/Items/Currency/CurrencyWeaponQuality";
		public const string PATH_ALTERATION = "Metadata/Items/Currency/CurrencyRerollMagic";
		public const string PATH_AUGMENT = "Metadata/Items/Currency/CurrencyAddModToMagic";
		public const string PATH_REGAL = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare";
		public const string PATH_SCOUR = "Metadata/Items/Currency/CurrencyConvertToNormal";
		public const string PATH_EXALT = "Metadata/Items/Currency/CurrencyAddModToRare";
		public const string PATH_FUSING = "Metadata/Items/Currency/CurrencyRerollSocketLinks";
		public const string PATH_REMNANT_OF_CORRUPTION = "Metadata/Items/Currency/CurrencyCorruptMonolith";
		public const string PATH_OMEN_OF_DEATH_DANCING = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeAvoidDamage";
		public const string PATH_OMEN_OF_AMELIORATION = "Metadata/Items/Currency/AncestralOmenOnDeathPreventExpLoss";
		public const string PATH_OMEN_OF_RETURN = "Metadata/Items/Currency/AncestralOmenOnDeathCreatePortal";
		public const string PATH_CLUSTER_SMALL = "Metadata/Items/Jewels/JewelPassiveTreeExpansionSmall";
		public const string PATH_CLUSTER_MEDIUM = "Metadata/Items/Jewels/JewelPassiveTreeExpansionMedium";
		public const string PATH_CLUSTER_LARGE = "Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge";
		public const string PATH_SORCERER_BOOTS = "Metadata/Items/Armours/Boots/BootsInt9";
		public const string PATH_PORTAL = "Metadata/MiscellaneousObjects/MultiplexPortal";
		public const string PATH_STASH = "Metadata/MiscellaneousObjects/Stash";
		public const string PATH_MAP_PREFIX = "Metadata/Items/Maps/";
		public const string PATH_INCUBATOR_PREFIX = "Metadata/Items/Currency/CurrencyIncubation";

		// some constant names of things and places (that I think might vary across language settings)
		public const string THE_ROGUE_HARBOUR = "The Rogue Harbour";
		public const string HIDEOUT_SUFFIX = "Hideout";
		public const string SYNDICATE_HIDEOUT = "Syndicate Hideout";
		public const string ANCESTOR_TOWN = "The Halls of the Dead";
		public const string KINGSMARCH = "Kingsmarch";
		public static bool IsHideout(string areaName) {
			return areaName == null
					|| areaName.Equals(THE_ROGUE_HARBOUR)
					|| areaName.Equals(KINGSMARCH)
					|| (areaName.EndsWith(HIDEOUT_SUFFIX) && !areaName.Equals(SYNDICATE_HIDEOUT));
		}

	}

}
