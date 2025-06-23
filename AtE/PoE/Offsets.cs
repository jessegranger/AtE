﻿using ImGuiNET;
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
		public const int VersionMinor = 10;

		/// <summary>
		/// The most recent version of PoE where at least some of this was tested.
		/// </summary>
		public const string PoEVersion = "3.26.0c";

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

			public string Value => Length > 0 && Capacity > 0 && Length <= Capacity && Capacity < 8
				? Encoding.Unicode.GetString(new byte[] {
					byte0, byte1, byte2, byte3,
					byte4, byte5, byte6, byte7,
					byte8, byte9, byte10, byte11,
					byte12, byte13 }, 0, (int)(Length * 2))
				: PoEMemory.TryReadString(strFullText, Encoding.Unicode, out string value) ? value : null;
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

		public readonly static string FileRoot_SearchMask = "xxxxxx????";
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
			// 0x48, 0x39, 0x01
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
			[FieldOffset(0x08)] public readonly ArrayHandle CurrentGameStates;
			[FieldOffset(0x20)] public readonly ArrayHandle ActiveGameStates;
			[FieldOffset(0x48)] public readonly GameStateArrayEntry AreaLoadingState;
			[FieldOffset(0x58)] public readonly GameStateArrayEntry WaitingState;
			[FieldOffset(0x68)] public readonly GameStateArrayEntry CreditsState;
			[FieldOffset(0x78)] public readonly GameStateArrayEntry EscapeState;
			[FieldOffset(0x88)] public readonly GameStateArrayEntry InGameState;
			[FieldOffset(0x98)] public readonly GameStateArrayEntry ChangePasswordState;
			[FieldOffset(0xa8)] public readonly GameStateArrayEntry LoginState;
			[FieldOffset(0xb8)] public readonly GameStateArrayEntry PreGameState;
			[FieldOffset(0xc8)] public readonly GameStateArrayEntry CreateCharacterState;
			[FieldOffset(0xd8)] public readonly GameStateArrayEntry SelectCharacterState;
			[FieldOffset(0xe8)] public readonly GameStateArrayEntry DeleteCharacterState;
			[FieldOffset(0xf8)] public readonly GameStateArrayEntry LoadingState;
			[FieldOffset(0x108)] public readonly GameStateArrayEntry ProbeState; // not a real state, put here to probe for the game adding new kinds of states
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
			[FieldOffset(0x220)] public readonly int TicksPerLastFrame; // 1000 ticks = 1 ms
			[FieldOffset(0x278)] public readonly IntPtr ptrWorldData; // ptr to WorldData
			[FieldOffset(0x298)] public readonly IntPtr ptrUIElements; // ptr to InGameState_UIElements
			// NOTE: ptrUIElement is also a ptr to Element,
			//   and is accessible at UIRoot.Children[1] if this offset changes but UIRoot is known

			// [FieldOffset(0x298)] public readonly IntPtr ptrEntityLabelMap;
			// 3.21.2b: 8 new bytes added here
			// 3.22: 248 new bytes here?
			// 3.23: 128 new bytes here?
			[FieldOffset(0x520)] public readonly IntPtr elemRoot;
			[FieldOffset(0x530)] public readonly IntPtr elemInputFocus; // which element has input focus or null
			[FieldOffset(0x558)] public readonly IntPtr elemHover; // element which is currently hovered
			[FieldOffset(0x590)] public readonly int MousePosX;
			[FieldOffset(0x594)] public readonly int MousePosY;
			[FieldOffset(0x5a0)] public readonly Vector2 UIHoverOffset; // mouse position offset in hovered UI element
			[FieldOffset(0x5a4)] public readonly Vector2 MousePos;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct PreGameState {
			[FieldOffset(0x130)] public readonly IntPtr elemRoot;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EscapeGameState {
			[FieldOffset(0x100)] public readonly IntPtr elemRoot;
		}

		// members of AreaLoadingState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AreaGameState {
			// 3.23: 128 new bytes here
			// 3.23.2: 512 new bytes here
			[FieldOffset(0x348)] public readonly long IsLoading;
			[FieldOffset(0x380)] public readonly IntPtr elemRoot;
			// 3.22: 24 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.25: 256 new bytes here
			[FieldOffset(0x748)] public readonly IntPtr strAreaName;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct LoginGameState {
			[FieldOffset(0x2D0)] public readonly IntPtr elemRoot;
		}

		// Element members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element {

			[FieldOffset(0x28)] public readonly IntPtr elemSelf;
			[FieldOffset(0x30)] public readonly ArrayHandle Children;
			[FieldOffset(0x68)] public readonly ArrayHandle unkArrayHandle;

			[FieldOffset(0xA8)] public readonly Vector2 ScrollOffset;

			// 3.23: 32 fewer bytes here
			// 3.23.1: 128 more bytes here
			// 3.24.3: 12 fewer bytes here
			[FieldOffset(0x104)] public readonly float Scale;
			// 3.24.3: 8 new bytes here
			[FieldOffset(0x148)] public readonly IntPtr elemParent; // ptr to Element

			[FieldOffset(0xC0)] public readonly Vector2 Position;
			[FieldOffset(0xC0)] public readonly float X;
			[FieldOffset(0xC4)] public readonly float Y;
			// 3.23: 32 fewer bytes here again
			[FieldOffset(0x151)] public readonly byte IsVisibleByte;
			public bool IsVisibleLocal => (IsVisibleByte & 8) == 8;

			// [FieldOffset(0x160)] public readonly uint ElementBorderColor;
			// [FieldOffset(0x164)] public readonly uint ElementBackgroundColor;
			// [FieldOffset(0x168)] public readonly uint ElementOverlayColor;

			// 3.23: 16 new bytes (+ the 64 prior) added here
			// 3.26: 56 new bytes here
			[FieldOffset(0x1d0)] public readonly Vector2 Size;
			[FieldOffset(0x1d0)] public readonly float Width;
			[FieldOffset(0x1d4)] public readonly float Height;

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
		public static readonly int Element_Text = 0x430;

		[StructLayout(LayoutKind.Explicit)] public struct ChildrenArrayEntry {
			[FieldOffset(0x0)] public readonly IntPtr ptrChild;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldData {
			[FieldOffset(0xA0)] public readonly IntPtr ptrToWorldAreaRef;
			// 3.12.2b: some new bytes here
			// 3.32.2: 16 fewer bytes here?
			[FieldOffset(0x1A8)] public readonly Camera Camera;
			// Note to self: when trying to find the right Camera offset,
			// use the fact (seen below in Camera struct) that the Width and Height
			// are integers that are easy to spot in memory, and come after the end
			// of the main camera matrix
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldAreaRef {
			// 3.23: 40 new bytes here?
			// 3.24: 8 fewer bytes here
			// 3.24.3b: 8 bytes removed here
			[FieldOffset(0xa0)] public readonly IntPtr ptrToWorldAreaDetails;
			[FieldOffset(0xa8)] public readonly IntPtr ptrUnk90;
			// 3.24.3b: 8 bytes added here
			[FieldOffset(0x10c)] public readonly uint areaHash;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldAreaDetails {
			[FieldOffset(0x00)] public readonly IntPtr strRawName;
			[FieldOffset(0x08)] public readonly IntPtr strName;
			[FieldOffset(0x10)] public readonly int Act;
			[FieldOffset(0x14)] public readonly byte IsTownByte;
			public bool IsTown => IsTownByte == 1;
			[FieldOffset(0x15)] public readonly byte HasWaypointByte;
			public bool HasWaypoint => HasWaypointByte == 1;
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
			// 3.22.1b: 4 new bytes here
			// 3.23: 40 new bytes here
			// 3.24: 8 fewer bytes here
			[FieldOffset(0x0CC)] public readonly byte CurrentAreaLevel;

			// 3.22.1b: 28 bytes removed here
			// 3.24.3b: 8 bytes removed here
			[FieldOffset(0x108)] public readonly uint CurrentAreaHash;
			[FieldOffset(0x118)] public readonly IntPtr MapStats;
			// [FieldOffset(0x260)] public readonly long LabDataPtr; //May be incorrect

			// Crucible: 8 new bytes here
			// 3.22: 120 new bytes here
			// 3.22.1b: 128 new bytes here?
			// 3.22.2: 8 bytes removed here
			// 3.24: 128 new bytes here
			// 3.24.3b: 24 bytes removed here
			[FieldOffset(0x8c8)] public readonly IntPtr ServerData;
			[FieldOffset(0x8d0)] public readonly IntPtr entPlayer; // ptr Entity
			[FieldOffset(0x980)] public readonly IntPtr EntityListHead; // ptr EntityListNode
			[FieldOffset(0x988)] public readonly long EntitiesCount;
			// [FieldOffset(0x9C8)] public readonly long Terrain; // TODO: TerrainData struct
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
			[FieldOffset(0x298)] public readonly IntPtr GameUI;
			[FieldOffset(0x2b0)] public readonly IntPtr LifeBubble;
			[FieldOffset(0x2b8)] public readonly IntPtr ManaBubble;
			[FieldOffset(0x2d8)] public readonly IntPtr Flasks;
			[FieldOffset(0x2e0)] public readonly IntPtr ExperienceBar;
			[FieldOffset(0x2f8)] public readonly IntPtr OpenMenuPopoutButton ;
			[FieldOffset(0x310)] public readonly IntPtr CurrentTime;
			// 3.23.2: 8 fewer bytes here
			[FieldOffset(0x3a0)] public readonly IntPtr GreenShopButton;
			[FieldOffset(0x3a8)] public readonly IntPtr HelpPanelButton;
			[FieldOffset(0x3e0)] public readonly IntPtr Mouse;
			[FieldOffset(0x3e8)] public readonly IntPtr SkillBar;
			[FieldOffset(0x3f0)] public readonly IntPtr HiddenSkillBar;
			// Crucible: 8 new bytes here
			// 3.26: 8 new bytes here
			[FieldOffset(0x498)] public readonly IntPtr ChatBoxRoot;

			[FieldOffset(0x4c8)] public readonly IntPtr QuestTracker;
			[FieldOffset(0x550)] public readonly IntPtr OpenLeftPanel;
			[FieldOffset(0x558)] public readonly IntPtr OpenRightPanel;
			[FieldOffset(0x580)] public readonly IntPtr InventoryPanel;
			[FieldOffset(0x588)] public readonly IntPtr StashElement;
			[FieldOffset(0x590)] public readonly IntPtr GuildStashElement;
			[FieldOffset(0x5a8)] public readonly IntPtr SocialPanel;
			// [FieldOffset(0x618)] public readonly IntPtr AtlasPanel;
			// [FieldOffset(0x620)] public readonly IntPtr AtlasSkillPanel;
			// [FieldOffset(0x650)] public readonly IntPtr WorldMap;
			[FieldOffset(0x5d0)] public readonly IntPtr CharacterPanel;
			[FieldOffset(0x5d8)] public readonly IntPtr OptionsPanel;
			[FieldOffset(0x5e0)] public readonly IntPtr ChallengesPanel;
			[FieldOffset(0x5e8)] public readonly IntPtr PantheonPanel;
			[FieldOffset(0x5f0)] public readonly IntPtr PvPPanel;
			[FieldOffset(0x5f8)] public readonly IntPtr AreaInstanceUi;

			[FieldOffset(0x640)] public readonly IntPtr Map;
			[FieldOffset(0x648)] public readonly IntPtr ItemsOnGroundLabelElement;
			//[FieldOffset(0x628)] public readonly IntPtr GameViewport; // playable area not blocked by open left/right panel
			[FieldOffset(0x6c8)] public readonly IntPtr RootBuffPanel;
			[FieldOffset(0x6d0)] public readonly IntPtr NpcDialog;
			[FieldOffset(0x6d8)] public readonly IntPtr NpcOptions;
			// [FieldOffset(0x728)] public readonly IntPtr LeagueInteractButtonPanel;
			// [FieldOffset(0x728)] public readonly IntPtr QuestRewardWindow;
			// [FieldOffset(0x730)] public readonly IntPtr Unknown730;
			[FieldOffset(0x6f8)] public readonly IntPtr PurchaseWindow;
			// [FieldOffset(0x740)] public readonly IntPtr Unknown740; // LeagueSellPanel
			[FieldOffset(0x708)] public readonly IntPtr SellWindow;
			[FieldOffset(0x710)] public readonly IntPtr TradeWindow;
			// [FieldOffset(0x758)] public readonly IntPtr Unknown758;
			// [FieldOffset(0x758)] public readonly IntPtr LabyrinthDivineFontPanel;
			// [FieldOffset(0x768)] public readonly IntPtr Unknown768;
			[FieldOffset(0x738)] public readonly IntPtr MapDeviceWindow;
			// [FieldOffset(0x778)] public readonly IntPtr Unknown778;
			// [FieldOffset(0x780)] public readonly IntPtr Unknown780;
			// [FieldOffset(0x788)] public readonly IntPtr Unknown788;
			// [FieldOffset(0x790)] public readonly IntPtr Unknown790;
			// [FieldOffset(0x798)] public readonly IntPtr Unknown798;
			// [FieldOffset(0x7a0)] public readonly IntPtr Unknown7a0;
			// [FieldOffset(0x7a8)] public readonly IntPtr Unknown7a8;
			// [FieldOffset(0x7b0)] public readonly IntPtr Unknown7b0;
			// [FieldOffset(0x7b8)] public readonly IntPtr Unknown7b8;
			[FieldOffset(0x780)] public readonly IntPtr CardTradePanel;
			// [FieldOffset(0x7C8)] public readonly IntPtr Unknown7C8;
			// [FieldOffset(0x7c8)] public readonly IntPtr IncursionAltarOfSacrificePanel;
			// [FieldOffset(0x7D0)] public readonly IntPtr IncursionLapidaryLensPanel;
			// [FieldOffset(0x7E0)] public readonly IntPtr DelveWindow;
			// [FieldOffset(0x7F0)] public readonly IntPtr Unknown7F0;
			// [FieldOffset(0x7F0)] public readonly IntPtr ZanaMissionChoice; // KiracMissionPanel
			// [FieldOffset(0x800)] public readonly IntPtr Unknown800; // KiracMissionPanel
			// [FieldOffset(0x800)] public readonly IntPtr BetrayalWindow;
			// [FieldOffset(0x810)] public readonly IntPtr Unknown810;
			[FieldOffset(0x7d8)] public readonly IntPtr CraftBench;
			[FieldOffset(0x7e0)] public readonly IntPtr UnveilWindow;
			// [FieldOffset(0x828)] public readonly IntPtr Unknown828;
			// [FieldOffset(0x830)] public readonly IntPtr Unknown830;
			// [FieldOffset(0x838)] public readonly IntPtr Unknown838;
			// [FieldOffset(0x838)] public readonly IntPtr BlightAnointItemPanel;
			// [FieldOffset(0x840)] public readonly IntPtr MetamorphWindow;
			// [FieldOffset(0x848)] public readonly IntPtr TanesMetamorphPanel;
			// [FieldOffset(0x850)] public readonly IntPtr HorticraftingHideoutPanel;
			// [FieldOffset(0x858)] public readonly IntPtr HeistContractWindow;
			// [FieldOffset(0x860)] public readonly IntPtr HeistRevealWindow;
			// [FieldOffset(0x868)] public readonly IntPtr HeistAllyEquipmentWindow;
			// [FieldOffset(0x870)] public readonly IntPtr HeistBlueprintWindow;
			// [FieldOffset(0x878)] public readonly IntPtr HeistLockerWindow;
			// [FieldOffset(0x880)] public readonly IntPtr RitualWindow;
			// [FieldOffset(0x888)] public readonly IntPtr RitualFavourWindow;
			// [FieldOffset(0x890)] public readonly IntPtr UltimatumProgressWindow;
			// [FieldOffset(0x898)] public readonly IntPtr ExpeditionSelectPanel;
			// [FieldOffset(0x8A0)] public readonly IntPtr LogbookReceptaclePanel;
			// [FieldOffset(0x8a8)] public readonly IntPtr ExpeditionLockerPanel;
			// [FieldOffset(0x8B8)] public readonly IntPtr KalandraMirroredTabletPanel;
			// [FieldOffset(0x8C0)] public readonly IntPtr KalandraReflectionPanel;
			// [FieldOffset(0x8C8)] public readonly IntPtr Unknown8C8;
			// [FieldOffset(0x8D0)] public readonly IntPtr Unknown8D0;
			// [FieldOffset(0x8D8)] public readonly IntPtr Unknown8D8;
			// [FieldOffset(0x8E0)] public readonly IntPtr Unknown8E0;
			[FieldOffset(0x900)] public readonly IntPtr BuffsPanel;
			[FieldOffset(0x918)] public readonly IntPtr DelveDarkness; // Debuffs Panel
			// [FieldOffset(0x8F8)] public readonly IntPtr Unknown8F8;
			// [FieldOffset(0x900)] public readonly IntPtr Unknown900;
			// [FieldOffset(0x908)] public readonly IntPtr Unknown908;
			// [FieldOffset(0x910)] public readonly IntPtr Unknown910;
			// [FieldOffset(0x918)] public readonly IntPtr Unknown918;
			// [FieldOffset(0x920)] public readonly IntPtr Unknown920;
			// [FieldOffset(0x920)] public readonly IntPtr AreaInstanceUi;
			// [FieldOffset(0x930)] public readonly IntPtr Unknown930;
			// [FieldOffset(0x938)] public readonly IntPtr Unknown938;
			// [FieldOffset(0x940)] public readonly IntPtr Unknown940;
			// [FieldOffset(0x948)] public readonly IntPtr Unknown948;
			// [FieldOffset(0x950)] public readonly IntPtr Unknown950;
			// [FieldOffset(0x958)] public readonly IntPtr Unknown958;
			// [FieldOffset(0x960)] public readonly IntPtr Unknown960;
			// [FieldOffset(0x968)] public readonly IntPtr Unknown968;
			// [FieldOffset(0x970)] public readonly IntPtr Unknown970;
			// [FieldOffset(0x978)] public readonly IntPtr Unknown978;
			// [FieldOffset(0x980)] public readonly IntPtr Unknown980;
			// [FieldOffset(0x988)] public readonly IntPtr InteractButtonWrapper;
			// [FieldOffset(0x990)] public readonly IntPtr SkipAheadButton;
			// [FieldOffset(0x998)] public readonly IntPtr SyndicateHelpButton;
			// [FieldOffset(0x9a0)] public readonly IntPtr SyndicateReleasePanel;
			// [FieldOffset(0x9a8)] public readonly IntPtr LeagueInteractPanel;
			// [FieldOffset(0x9b0)] public readonly IntPtr MetamorphInteractPanel;
			// [FieldOffset(0x9b8)] public readonly IntPtr RitualInteractPanel;
			// [FieldOffset(0x9c0)] public readonly IntPtr ExpeditionInteractPanel;
			// [FieldOffset(0x9C8)] public readonly IntPtr Unknown9C8;
			// [FieldOffset(0x9D0)] public readonly IntPtr Unknown9D0;
			// [FieldOffset(0x9D8)] public readonly IntPtr Unknown9D8;
			// [FieldOffset(0x9E0)] public readonly IntPtr Unknown9E0;
			// [FieldOffset(0x9E8)] public readonly IntPtr Unknown9E8;
			// [FieldOffset(0x9F0)] public readonly IntPtr Unknown9F0;
			// [FieldOffset(0x9f8)] public readonly IntPtr InvitesPanel;
			[FieldOffset(0xA50)] public readonly IntPtr GemLvlUpPanel;
			// [FieldOffset(0xA58)] public readonly IntPtr SkillBarNotifyPanel1;
			[FieldOffset(0xB20)] public readonly IntPtr ItemOnGroundTooltip;

		}

		// Entity offsets
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Entity {
			[FieldOffset(0x00)] public readonly IntPtr vtable;
			[FieldOffset(0x08)] public readonly IntPtr ptrDetails;
			[FieldOffset(0x10)] public readonly ArrayHandle ComponentBasePtrs; // of IntPtr (to base address of a Component)
			// [FieldOffset(0x50)] public readonly Vector3 WorldPos; // possible
			// 3.23: 48 new bytes here
			// 3.24: 8 bytes removed here
			// 3.24.3b: 8 bytes removed here
			[FieldOffset(0x80)] public readonly uint Id;
		}

		public static bool IsValid(Entity ent) {
			return IsValid(ent.vtable)
				&& IsValid(ent.ptrDetails)
				&& IsValid<IntPtr>(ent.ComponentBasePtrs, 50) // at most N components
				;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EntityDetails {
			[FieldOffset(0x08)] public readonly IntPtr ptrPath;
			[FieldOffset(0x30)] public readonly IntPtr ptrComponentLookup; // used to find which Component is which in the ComponentsArray
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
			[FieldOffset(0x008)] public readonly IntPtr entOwner; // Entity
			// 3.22.1c: 8 new bytes here
			// 3.24: 8 new bytes here
			[FieldOffset(0x1B8)] public readonly IntPtr ptrAction; // ptr Component_Actor_Action
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
			[FieldOffset(0xB0)] public readonly IntPtr Target; // ptr Entity?
			[FieldOffset(0x150)] public readonly long Skill;
			[FieldOffset(0x170)] public readonly Vector2 Destination;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkill {
			[FieldOffset(0x08)] public readonly byte UseStage;
			// [FieldOffset(0x0C)] public readonly byte KeyBindFlags; // unknown how this works but it changes when you change your skill key binds in the UI
			[FieldOffset(0x10)] public readonly ushort Id;
			[FieldOffset(0x18)] public readonly IntPtr ptrGemEffects;
			[FieldOffset(0x80)] public readonly byte CanBeUsedWithWeapon;
			[FieldOffset(0x82)] public readonly byte CanBeUsed;
			// 3.25: 56 new bytes added here
			[FieldOffset(0x8c)] public readonly int TotalUses;
			[FieldOffset(0x90)] public readonly int CooldownMS;
			[FieldOffset(0xA4)] public readonly int SoulsPerUse;
			[FieldOffset(0xa8)] public readonly int TotalVaalUses;
			// 3.25: 24 new bytes added here
			[FieldOffset(0x100)] public readonly IntPtr ptrGameStats; // ptr to GameStatArray
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
			[FieldOffset(0x14)] public readonly int RequiredLevel;
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
			[FieldOffset(0x58)] public readonly IntPtr LongDescription; // ptr to string unicode
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
			[FieldOffset(0x18)] public readonly IntPtr SkillName; // ptr to string unicode
			[FieldOffset(0x20)] public readonly IntPtr IconName; // ptr to string unicode
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
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly IntPtr ptrAt0x20;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Animated {
			[FieldOffset(0x008)] public readonly IntPtr entOwner;
			// 3.22.1c: 72 new bytes here?
			// 3.25: 48 new bytes here?
			[FieldOffset(0x260)] public readonly IntPtr ptrToAnimatedEntity;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_AreaTransition {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x28)] public readonly ushort WorldAreaId;
			[FieldOffset(0x2A)] public readonly AreaTransitionType TransitionType;
			[FieldOffset(0x40)] public readonly IntPtr ptrToWorldArea;
		}

		public enum AreaTransitionType : byte {
			Normal = 0,
			Local = 1,
			NormalToCorrupted = 2,
			CorruptedToNormal = 3,
			Unknown4 = 4,
			Labyrinth = 5
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
			[FieldOffset(0x30)] public readonly IntPtr strName;
			[FieldOffset(0x78)] public readonly IntPtr strDescription; // ptr to string unicode
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
			[FieldOffset(0x50)] public readonly Vector3 BeamStart;
			[FieldOffset(0x5C)] public readonly Vector3 BeamEnd;
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
			[FieldOffset(0x42)] public readonly byte Charges;
			// [FieldOffset(0x44)] public readonly int UnkInt44;
			// [FieldOffset(0x48)] public readonly int UnkInt48;
			// [FieldOffset(0x4c)] public readonly int UnkInt4c;
			// [FieldOffset(0x50)] public readonly int UnkInt50;
			// [FieldOffset(0x54)] public readonly int UnkInt54;
			// [FieldOffset(0x58)] public readonly int UnkInt58;
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
			[FieldOffset(0x1A8)] public readonly bool IsStrongbox;
			[FieldOffset(0x1B0)] public readonly IntPtr ptrToChestEffect;
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
			[FieldOffset(0x248)] public readonly uint ItemLevel;
			[FieldOffset(0x24c)] public readonly uint RequiredLevel;
			// 3.22.1c: 8 new bytes here
			[FieldOffset(0x258)] public readonly IntPtr ptrIncubator; // ptr to IncubatorEntry
			[FieldOffset(0x268)] public readonly short IncubatorKillCount;
			// 3.22.1c: 1 new bytes here
			[FieldOffset(0x26e)] public readonly byte IsMirrored;
			[FieldOffset(0x26f)] public readonly byte IsSplit;
			[FieldOffset(0x270)] public readonly byte IsUsable;
			// 3.22.1c: 1 new bytes here
			[FieldOffset(0x273)] public readonly byte IsSynthesised;

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

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ModStats {
			[FieldOffset(0x008)] public readonly ArrayHandle ImplicitStatsArray; // of ItemStatEntry
			[FieldOffset(0x048)] public readonly ArrayHandle EnchantedStatsArray; // of ItemStatEntry
			[FieldOffset(0x088)] public readonly ArrayHandle ScourgeStatsArray; // of ItemStatEntry
			[FieldOffset(0x0C8)] public readonly ArrayHandle ExplicitStatsArray; // of ItemStatEntry
			[FieldOffset(0x108)] public readonly ArrayHandle CraftedStatsArray; // of ItemStatEntry
			[FieldOffset(0x148)] public readonly ArrayHandle FracturedStatsArray; // of ItemStatEntry
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemModEntry {
			[FieldOffset(0x00)] public ArrayHandle Values; // sometimes (always?) the values are not inlined
			[FieldOffset(0x28)] public readonly IntPtr ptrModsDatEntry; // ptr to File_ModsDat_Entry
			[FieldOffset(0x30)] public readonly IntPtr ptrModsDatFile; // ptr to base of "Data/Mods.dat" file in memory
		}

		// size should be 0x20
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemStatEntry {
			[FieldOffset(0x00)] public readonly IntPtr Pointer0;
			[FieldOffset(0x08)] public readonly IntPtr Pointer1;
			[FieldOffset(0x10)] public readonly int Padding;
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
			[FieldOffset(0x19c)] public readonly byte AllocatedLootId;
			[FieldOffset(0x1a0)] public readonly PantheonGod PantheonMinor;
			[FieldOffset(0x1a1)] public readonly PantheonGod PantheonMajor;
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
			[FieldOffset(0x290)] public readonly Vector2i GridPos;
			[FieldOffset(0x298)] public readonly float Rotation;
			[FieldOffset(0x2a8)] public readonly float Scale;
			[FieldOffset(0x2ac)] public readonly int Size;
			[FieldOffset(0x2b4)] public readonly Vector2 WorldPos;
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
			[FieldOffset(0xa0)] public readonly Vector3 Pos;
			[FieldOffset(0xac)] public readonly Vector3 Bounds;
			[FieldOffset(0xc8)] public readonly StringHandle Name; // of unicode bytes
			[FieldOffset(0xEC)] public readonly Vector3 Rotation;
			[FieldOffset(0xf0)] public readonly float RotationRadians;
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
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x30)] public readonly uint SocketColor;
			[FieldOffset(0x48)] public readonly uint MaxLevel;
			[FieldOffset(0x4c)] public readonly uint LimitLevel;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Sockets {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x18)] public readonly int Socket0;
			[FieldOffset(0x1c)] public readonly int Socket1;
			[FieldOffset(0x20)] public readonly int Socket2;
			[FieldOffset(0x24)] public readonly int Socket3;
			[FieldOffset(0x28)] public readonly int Socket4;
			[FieldOffset(0x2c)] public readonly int Socket5;
			[FieldOffset(0x30)] public readonly IntPtr entGem0; // ? an ent with a SkillGem component?
			[FieldOffset(0x38)] public readonly IntPtr entGem1; // ? an ent with a SkillGem component?
			[FieldOffset(0x40)] public readonly IntPtr entGem2; // ? an ent with a SkillGem component?
			[FieldOffset(0x48)] public readonly IntPtr entGem3; // ? an ent with a SkillGem component?
			[FieldOffset(0x50)] public readonly IntPtr entGem4; // ? an ent with a SkillGem component?
			[FieldOffset(0x58)] public readonly IntPtr entGem5; // ? an ent with a SkillGem component?
			[FieldOffset(0x60)] public readonly ArrayHandle Links; // not sure yet
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
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x20)] public readonly IntPtr GameStats; // ptr to GameStatArray
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Targetable {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x48)] public readonly bool IsTargetable;
			[FieldOffset(0x49)] public readonly bool IsHighlightable;
			[FieldOffset(0x4A)] public readonly bool IsTargeted;
		}


		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_TimerComponent {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x18)] public readonly float TimeLeft;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_TriggerableBlockage {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x30)] public readonly byte IsClosed;
			[FieldOffset(0x38)] public readonly ArrayHandle UnknownArray;
			[FieldOffset(0x50)] public readonly Vector2i GridMin;
			[FieldOffset(0x58)] public readonly Vector2i GridMax;
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
			[FieldOffset(0x2A0)] public readonly ItemsOnGroundLabelEntry hoverLabel; // 2 ptrs, one Element, one Entity
			[FieldOffset(0x2B8)] public readonly IntPtr labelsOnGroundHead;
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
			[FieldOffset(0x398)] public readonly InventoryArray InventoryList;
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
			[FieldOffset(0x48)] public readonly IntPtr Gloves;
			[FieldOffset(0x50)] public readonly IntPtr Belt;
			[FieldOffset(0x58)] public readonly IntPtr Boots;
			[FieldOffset(0x60)] public readonly IntPtr Backpack;
			[FieldOffset(0x68)] public readonly IntPtr Flask;
			[FieldOffset(0x70)] public readonly IntPtr Trinket;
			[FieldOffset(0x78)] public readonly IntPtr LeagueBackpack;
			[FieldOffset(0x80)] public readonly IntPtr LWeaponEffect; // nothing below here is verified/updated
			[FieldOffset(0x88)] public readonly IntPtr LWeaponAddedEffect;
			[FieldOffset(0x90)] public readonly IntPtr RWeaponSkin;
			[FieldOffset(0x98)] public readonly IntPtr RWeaponEffect;
			[FieldOffset(0xa0)] public readonly IntPtr RWeaponAddedEffect;
			[FieldOffset(0xa8)] public readonly IntPtr HelmSkin;
			[FieldOffset(0xb0)] public readonly IntPtr HelmAttachment;
			[FieldOffset(0xb8)] public readonly IntPtr HelmAttachment1;
			[FieldOffset(0xc0)] public readonly IntPtr BodySkin;
			[FieldOffset(0xc8)] public readonly IntPtr BodyAttachment;
			[FieldOffset(0xd0)] public readonly IntPtr GlovesSkin;
			[FieldOffset(0xd8)] public readonly IntPtr BootsSkin;
			[FieldOffset(0xe0)] public readonly IntPtr Footprints;
			[FieldOffset(0xe8)] public readonly IntPtr Apparition;
			[FieldOffset(0xf0)] public readonly IntPtr CharacterEffect;
			[FieldOffset(0xf8)] public readonly IntPtr Portrait;
			[FieldOffset(0x100)] public readonly IntPtr PortraitFrame;
			[FieldOffset(0x108)] public readonly IntPtr Pet1;
			[FieldOffset(0x110)] public readonly IntPtr Pet2;
			[FieldOffset(0x118)] public readonly IntPtr Portal;
			[FieldOffset(0x120)] public readonly IntPtr HelmAttachment2;
			[FieldOffset(0x128)] public readonly IntPtr Unknown37;
			[FieldOffset(0x130)] public readonly IntPtr Cursor;
			[FieldOffset(0x138)] public readonly IntPtr Unknown39;
			[FieldOffset(0x140)] public readonly IntPtr Unknown40;
			[FieldOffset(0x148)] public readonly IntPtr Unknown41;
			[FieldOffset(0x150)] public readonly IntPtr Unknown42;
			[FieldOffset(0x158)] public readonly IntPtr Unknown43;
			[FieldOffset(0x160)] public readonly IntPtr Unknown44;
			[FieldOffset(0x168)] public readonly IntPtr Unknown45;
			[FieldOffset(0x170)] public readonly IntPtr GemMTXScrollPanel;
			[FieldOffset(0x178)] public readonly IntPtr InventoryTabPanel;
			[FieldOffset(0x180)] public readonly IntPtr CosmeticTabPanel;
			[FieldOffset(0x188)] public readonly IntPtr GemMTX;
			[FieldOffset(0x190)] public readonly IntPtr FullInventoryPanel;
			[FieldOffset(0x198)] public readonly IntPtr FullCosmeticPanel;
			[FieldOffset(0x1a0)] public readonly IntPtr LeveledGems;
			[FieldOffset(0x1a8)] public readonly IntPtr LWeaponSwapTabPanel;
			[FieldOffset(0x1b0)] public readonly IntPtr RWeaponSwapTabPanel;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_Inventory {
			[FieldOffset(0x260)] public readonly int MoveItemHoverState;
			[FieldOffset(0x268)] public readonly IntPtr HoverItem; // to InventoryItem Element
			// [FieldOffset(0x270)] public readonly Vector2i HoveredGridPosition;
			[FieldOffset(0x270)] public readonly int XFake;
			[FieldOffset(0x274)] public readonly int YFake;
			// [FieldOffset(0x278)] public readonly Vector2i HoveredSlotPosition;
			[FieldOffset(0x278)] public readonly int XReal;
			[FieldOffset(0x27C)] public readonly int YReal;
			[FieldOffset(0x288)] public readonly short CursorInInventory;
			[FieldOffset(0x3E8)] public readonly long ItemCount;
			[FieldOffset(0x46C)] public readonly Vector2i Size;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_InventoryItem {
			// [FieldOffset(0x3F0)] public readonly IntPtr incorrectTooltip;
			// 3.23: 16 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer byte here
			[FieldOffset(0x388)] public readonly IntPtr entItem;
			// [FieldOffset(0x378)] public readonly IntPtr unkNearPtr0x378;
			// [FieldOffset(0x380)] public readonly int unkInt0x380;
			// [FieldOffset(0x384)] public readonly short unkShort0x384;
			// [FieldOffset(0x386)] public readonly short unkShort0x386;
			// [FieldOffset(0x388)] public readonly IntPtr unkFarPtr0x388;
			[FieldOffset(0x3a8)] public readonly IntPtr spriteDetails; // ptr to SpriteDetails struct
			// [FieldOffset(0x3D0)] public readonly int unkInt0x3D0;
			// 3.21.2b: 72 new bytes here?
			[FieldOffset(0x434)] public readonly int Width;
			[FieldOffset(0x438)] public readonly int Height;
			// [FieldOffset(0x3E0)] public readonly int unkInt0x3E0;
			[FieldOffset(0x40c)] public readonly Vector2i InventPosition;
			// [FieldOffset(0x3DC)] public readonly byte UnkByte3DC;
			// [FieldOffset(0x3DD)] public readonly byte UnkByte3DD;
			// [FieldOffset(0x3DE)] public readonly byte UnkByte3DE;
			// [FieldOffset(0x3DF)] public readonly byte UnkByte3DF;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SpriteDetails {
			[FieldOffset(0x0)] public readonly IntPtr strSpriteFile;
			[FieldOffset(0x8)] public readonly IntPtr strSpriteFileLong; // always points to enough bytes behind strSpriteFile, and should contain an extra 'path:'
			// 3.21.2b removed: [FieldOffset(0x10)] public readonly IntPtr elemUnknown; // some helper element, detached from the tree
			[FieldOffset(0x10)] public readonly IntPtr strSpriteFileAgain;
			[FieldOffset(0x18)] public readonly int unkFlags;
			[FieldOffset(0x20)] public readonly int Width;
			[FieldOffset(0x28)] public readonly int Height;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_Map {
			// 3.21.1: 8 new bytes added here
			// 3.23: 16 new bytes here
			// 3.24: 8 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer bytes here
			[FieldOffset(0x298)] public readonly IntPtr ptrToSubMap_Full;
			[FieldOffset(0x2a0)] public readonly IntPtr ptrToSubMap_Mini;
			// [FieldOffset(0x268)] public readonly IntPtr ptrToElement_OrangeWords;
			// [FieldOffset(0x2C0)] public readonly IntPtr ptrToElement_BlueWords;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_SubMap {
			// 3.23: 24 new bytes here
			// 3.24: 8 new bytes here
			// 3.24.3: 8 new bytes here
			// 3.26: 8 fewer bytes here
			[FieldOffset(0x288)] public readonly Vector2 Shift;
			[FieldOffset(0x290)] public readonly Vector2 DefaultShift; // historically, always < 0, -20 >
			[FieldOffset(0x2c8)] public readonly float Zoom; // from 0.5 (zoomed out) to 1.5 (zoomed in)
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
			[FieldOffset(0x30)] public readonly IntPtr Records;
			
			// 3.21 Crucible: 8 new bytes here?

			[FieldOffset(0x40)] public readonly int AreaCount; // the game increments this each time the area changes after the file is loaded
			[FieldOffset(0x44)] public readonly int Capacity; // ? a guess for now
			[FieldOffset(0x48)] public readonly int Count; // ? a guess for now, seems to count up forever as long as other players are around
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_RecordSet {
			[FieldOffset(0x00)] public readonly ArrayHandle recordsArray;
			[FieldOffset(0x40)] public readonly int recordCount;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct File_StatsDat_Entry {
			[FieldOffset(0x00)] public readonly IntPtr strName;
			[FieldOffset(0x08)] public readonly byte Flag0;
			[FieldOffset(0x09)] public readonly byte IsLocal;
			[FieldOffset(0x0A)] public readonly byte IsWeaponLocal;
			[FieldOffset(0x0B)] public readonly int Type;
			[FieldOffset(0x0F)] public readonly IntPtr longName;
			[FieldOffset(0x69)] public readonly long Padding;
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
			[FieldOffset(0x076)] public readonly IntPtr Family; // ptr to ptr to Unicode "Strength"
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
			[FieldOffset(0x285)] public readonly long Padding; // so that total size = 0x28F (655)
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
			[FieldOffset(0x008)] public readonly uint Hash;
			[FieldOffset(0x010)] public readonly IntPtr SellPrice; // ptr to unicode, like "Medium" or "High"
			[FieldOffset(0x018)] public readonly byte Padding; // unknown
		} 


		public const string PATH_STACKED_DECK = "Metadata/Items/DivinationCards/DivinationCardDeck";
		public const string PATH_SCROLL_WISDOM = "Metadata/Items/Currency/CurrencyIdentification";
		public const string PATH_SCROLL_PORTAL = "Metadata/Items/Currency/CurrencyPortal";
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
		public const string PATH_FUSING = "Metadata/Items/Currency/CurrencyRerollSocketLinks";
		public const string PATH_REMNANT_OF_CORRUPTION = "Metadata/Items/Currency/CurrencyCorruptMonolith";
		public const string PATH_OMEN_OF_DEATH_DANCING = "Metadata/Items/Currency/AncestralOmenOnCriticalLifeAvoidDamage";
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
		public static bool IsHideout(string areaName) {
			return areaName == null
					|| areaName.Equals(THE_ROGUE_HARBOUR)
					// || areaName.Equals(ANCESTOR_TOWN)
					|| (areaName.EndsWith(HIDEOUT_SUFFIX) && !areaName.Equals(SYNDICATE_HIDEOUT));
		}

	}

}
