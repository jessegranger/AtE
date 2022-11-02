using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace AtE {


	/// <summary>
	/// This class is as close as I can get to a complete map of Path of Exile
	/// memory layout.
	/// Most of this was extracted from now defunct https://github.com/queuete/ExileApi
	/// </summary>
	public static partial class Offsets {

		/// <summary>
		/// The current version of this file.
		/// </summary>
		public const int VersionMajor = 1;
		public const int VersionMinor = 0;

		/// <summary>
		/// The most recent version of PoE where at least some of this was tested.
		/// </summary>
		public const string PoEVersion = "3.19d";

		/// <summary>
		///  Used as a placeholder where we dont know which struct yet.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Empty { }

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Vector2i {
			[FieldOffset(0x0)] public readonly int X;
			[FieldOffset(0x4)] public readonly int Y;
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
			private byte[] bytes(params byte[] b) => b;

			public string Value => Length > 0 && Capacity > 0 && Length <= Capacity && Capacity < 8
				? Encoding.Unicode.GetString(bytes(
					byte0, byte1, byte2, byte3,
					byte4, byte5, byte6, byte7,
					byte8, byte9, byte10, byte11,
					byte12, byte13), 0, (int)(Length * 2))
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
				if ( Head == IntPtr.Zero ) yield break;
				long head = Head.ToInt64();
				long tail = Tail.ToInt64();
				while(head < tail) {
					yield return new IntPtr(head);
					head += recordSize;
				}
			}
			// TODO: read the whole array all at once
		}
	

		public enum GameStateType : byte {
			AreaLoadingState,
			WaitingState,
			CreditsState,
			EscapeState,
			InGameState,
			ChangePasswordState,
			LoginState,
			PreGameState,
			CreateCharacterState,
			SelectCharacterState,
			DeleteCharacterState,
			LoadingState,
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
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameRoot_Ref {
			[FieldOffset(0x0C)] public readonly IntPtr ptrToGameRootPtr;
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
		}

		// GameState members:
		public readonly static int GameState_Kind = 0x0B;

		// members of AllGameStates array:
		[StructLayout(LayoutKind.Explicit)] public struct GameStateArrayEntry {
			[FieldOffset(0x0)] public readonly IntPtr ptrToGameState;
			[FieldOffset(0x8)] public readonly IntPtr ptrToUnknown;
		}

		// members of InGameState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState {
			[FieldOffset(0x0B)] public readonly GameStateType Kind;
			[FieldOffset(0x018)] public readonly IntPtr ptrData; // ptr to InGameState_Data struct
			[FieldOffset(0x078)] public readonly IntPtr ptrWorldData;
			[FieldOffset(0x098)] public readonly IntPtr ptrEntityLabelMap;
			[FieldOffset(0x1A0)] public readonly IntPtr elemRoot;
			[FieldOffset(0x1B0)] public readonly IntPtr elemInputFocus; // which element has input focus or null
			[FieldOffset(0x1D8)] public readonly IntPtr elemHover; // element which is currently hovered
			[FieldOffset(0x210)] public readonly int MousePosX;
			[FieldOffset(0x214)] public readonly int MousePosY;
			[FieldOffset(0x21C)] public readonly Vector2 UIHoverOffset; // mouse position offset in hovered UI element
			[FieldOffset(0x224)] public readonly Vector2 MousePos;
			[FieldOffset(0x450)] public readonly IntPtr ptrUIElements; // ptr to InGameState_UIElements
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct PreGameState {
			[FieldOffset(0x130)] public readonly IntPtr UIRoot;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EscapeGameState {
			[FieldOffset(0x0B)] public readonly GameStateType Kind;
			[FieldOffset(0x100)] public readonly IntPtr elemRoot;
		}

		// members of AreaLoadingState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AreaGameState {
			[FieldOffset(0xC8)] public readonly long IsLoading;
			[FieldOffset(0x100)] public readonly IntPtr elemRoot;
			[FieldOffset(0x3A8)] public readonly IntPtr strAreaName;
		}

		// Element members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element {
			// TODO: move this value to MapOffsets, the only place it's used
			// public readonly const int OffsetBuffers = 0x6EC;

			[FieldOffset(0x28)] public readonly IntPtr Self;
			[FieldOffset(0x30)] public readonly ArrayHandle Children;

			// This was replaced with a GetRoot() function ptr which we cant call
			// [FieldOffset(0xD8)] public readonly IntPtr Root; // Element
			[FieldOffset(0xE0)] public readonly IntPtr elemParent; // Element
			[FieldOffset(0xE8)] public readonly Vector2 Position;
			[FieldOffset(0xE8)] public readonly float X;
			[FieldOffset(0xEC)] public readonly float Y;
			[FieldOffset(0x158)] public readonly float Scale;
			[FieldOffset(0x161)] public readonly byte IsVisibleByte;
			public bool IsVisibleLocal => (IsVisibleByte & 8) == 8;

			[FieldOffset(0x160)] public readonly uint ElementBorderColor;
			[FieldOffset(0x164)] public readonly uint ElementBackgroundColor;
			[FieldOffset(0x168)] public readonly uint ElementOverlayColor;

			[FieldOffset(0x180)] public readonly Vector2 Size;
			[FieldOffset(0x180)] public readonly float Width;
			[FieldOffset(0x184)] public readonly float Height;

			// everything below here is wrong I think
			[FieldOffset(0x190)] public readonly uint TextBoxBorderColor;
			[FieldOffset(0x190)] public readonly uint TextBoxBackgroundColor;
			[FieldOffset(0x194)] public readonly uint TextBoxOverlayColor;

			[FieldOffset(0x1C0)] public readonly uint HighlightBorderColor;
			[FieldOffset(0x1C3)] public readonly bool isHighlighted;

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

			// incorrect [FieldOffset(0x3D0)] public readonly long Tooltip;
		}

		[StructLayout(LayoutKind.Explicit)] public struct ChildrenArrayEntry {
			[FieldOffset(0x0)] public readonly IntPtr ptrChild;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldData {
			[FieldOffset(0xA0)] public readonly IntPtr ptrToWorldAreaRef;
			[FieldOffset(0xA8)] public readonly Camera Camera;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldAreaRef {
			[FieldOffset(0x88)] public readonly IntPtr ptrToWorldAreaDetails;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldAreaDetails {
			[FieldOffset(0x00)] public readonly IntPtr strRawName;
			[FieldOffset(0x08)] public readonly IntPtr strName;
			[FieldOffset(0x10)] public readonly int Act;
			[FieldOffset(0x14)] public readonly byte IsTownByte;
			public bool IsTown => IsTownByte == 1;
			[FieldOffset(0x15)] public readonly byte HasWaypointByte;
			public bool HasWaypoint => HasWaypointByte == 1;
			[FieldOffset(0x26)] public readonly int MonsterLevel;
			[FieldOffset(0x2A)] public readonly int WorldAreaId;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Camera {
			[FieldOffset(0x8)] public readonly Vector2i Size;
			[FieldOffset(0x8)] public readonly int Width;
			[FieldOffset(0xC)] public readonly int Height;
			[FieldOffset(0x1C4)] public readonly float ZFar;

			//First value is changing when we change the screen size (ratio)
			//4 bytes before the matrix doesn't change
			[FieldOffset(0x80)] public readonly Matrix4x4 Matrix; // 4x4 floats = 16 floats 128 bytes
																														// the last 3 floats in the matrix are the camera position
																														// 0x80 + (128 - 12) = 0xF4
			[FieldOffset(0xF4)] public readonly Vector3 Position; // the last 3 floats of the matrix are the X,Y,Z

			public unsafe Vector2 WorldToScreen(Vector3 pos) {
				Vector2 result; // put a struct on the stack
				Vector4 coord = *(Vector4*)&pos;
				coord.W = 1;
				coord = Vector4.Transform(coord, Matrix);
				coord = Vector4.Divide(coord, coord.W);
				result.X = (coord.X + 1.0f) * (Width / 2f);
				result.Y = (1.0f - coord.Y) * (Height / 2f);
				return result;
			}
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState_Data {
			[FieldOffset(0x0A8)] public readonly byte CurrentAreaLevel;
			[FieldOffset(0x10C)] public readonly uint CurrentAreaHash;
			[FieldOffset(0x120)] public readonly IntPtr MapStats;
			// [FieldOffset(0x260)] public readonly long LabDataPtr; //May be incorrect
			[FieldOffset(0x778)] public readonly IntPtr ServerData;
			[FieldOffset(0x780)] public readonly IntPtr entPlayer; // ptr Entity
			[FieldOffset(0x830)] public readonly IntPtr EntityListHead; // ptr EntityListNode
			[FieldOffset(0x838)] public readonly long EntitiesCount;
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
			[FieldOffset(0x250)] public readonly IntPtr GetQuests;
			[FieldOffset(0x288)] public readonly IntPtr GameUI;
			[FieldOffset(0x2a0)] public readonly IntPtr LifeBubble;
			[FieldOffset(0x2a8)] public readonly IntPtr ManaBubble;
			[FieldOffset(0x2c8)] public readonly IntPtr Flasks;
			[FieldOffset(0x2d0)] public readonly IntPtr ExperienceBar;
			[FieldOffset(0x2e8)] public readonly IntPtr OpenMenuPopoutButton ;
			[FieldOffset(0x300)] public readonly IntPtr CurrentTime;
			[FieldOffset(0x3D8)] public readonly IntPtr Mouse;
			[FieldOffset(0x3E0)] public readonly IntPtr SkillBar;
			[FieldOffset(0x3E8)] public readonly IntPtr HiddenSkillBar;
			[FieldOffset(0x480)] public readonly IntPtr ChatBoxRoot;
			[FieldOffset(0x4B0)] public readonly IntPtr QuestTracker;
			[FieldOffset(0x538)] public readonly IntPtr OpenLeftPanel;
			[FieldOffset(0x540)] public readonly IntPtr OpenRightPanel;
			[FieldOffset(0x568)] public readonly IntPtr InventoryPanel;
			[FieldOffset(0x570)] public readonly IntPtr StashElement;
			[FieldOffset(0x578)] public readonly IntPtr GuildStashElement;
			[FieldOffset(0x618)] public readonly IntPtr AtlasPanel;
			[FieldOffset(0x620)] public readonly IntPtr AtlasSkillPanel;
			[FieldOffset(0x650)] public readonly IntPtr WorldMap;
			[FieldOffset(0x678)] public readonly IntPtr Map;
			[FieldOffset(0x680)] public readonly IntPtr ItemsOnGroundLabelElement;
			[FieldOffset(0x688)] public readonly IntPtr BanditDialog;
			[FieldOffset(0x708)] public readonly IntPtr RootBuffPanel;
			[FieldOffset(0x718)] public readonly IntPtr NpcDialog;
			[FieldOffset(0x720)] public readonly IntPtr LeagueNpcDialog;
			[FieldOffset(0x720)] public readonly IntPtr LeagueInteractButtonPanel;
			[FieldOffset(0x728)] public readonly IntPtr QuestRewardWindow;
			[FieldOffset(0x730)] public readonly IntPtr PurchaseWindow;
			[FieldOffset(0x738)] public readonly IntPtr HaggleWindow; // LeaguePurchasePanel
			[FieldOffset(0x740)] public readonly IntPtr SellWindow;
			[FieldOffset(0x748)] public readonly IntPtr ExpeditionSellWindow; // LeagueSellPanel
			[FieldOffset(0x750)] public readonly IntPtr TradeWindow;
			[FieldOffset(0x760)] public readonly IntPtr LabyrinthDivineFontPanel;
			[FieldOffset(0x770)] public readonly IntPtr MapDeviceWindow;
			[FieldOffset(0x7C0)] public readonly IntPtr CardTradePanel;
			[FieldOffset(0x7C0)] public readonly IntPtr IncursionWindow;
			[FieldOffset(0x7C8)] public readonly IntPtr IncursionCorruptionAltarPanel;
			[FieldOffset(0x7D0)] public readonly IntPtr IncursionAltarOfSacrificePanel;
			[FieldOffset(0x7D8)] public readonly IntPtr IncursionLapidaryLensPanel;
			[FieldOffset(0x7E8)] public readonly IntPtr DelveWindow;
			[FieldOffset(0x7E8)] public readonly IntPtr DelveOldSubterraneanChartPanel;
			[FieldOffset(0x7F8)] public readonly IntPtr ZanaMissionChoice; // KiracMissionPanel
			[FieldOffset(0x808)] public readonly IntPtr BetrayalWindow;
			[FieldOffset(0x818)] public readonly IntPtr CraftBench;
			[FieldOffset(0x820)] public readonly IntPtr UnveilWindow;
			[FieldOffset(0x840)] public readonly IntPtr BlightAnointItemPanel;
			[FieldOffset(0x848)] public readonly IntPtr MetamorphWindow;
			[FieldOffset(0x850)] public readonly IntPtr TanesMetamorphPanel;
			[FieldOffset(0x858)] public readonly IntPtr HorticraftingHideoutPanel;
			[FieldOffset(0x860)] public readonly IntPtr HeistContractWindow;
			[FieldOffset(0x868)] public readonly IntPtr HeistRevealWindow;
			[FieldOffset(0x870)] public readonly IntPtr HeistAllyEquipmentWindow;
			[FieldOffset(0x878)] public readonly IntPtr HeistBlueprintWindow;
			[FieldOffset(0x880)] public readonly IntPtr HeistLockerWindow;
			[FieldOffset(0x888)] public readonly IntPtr RitualWindow;
			[FieldOffset(0x890)] public readonly IntPtr RitualFavourWindow;
			[FieldOffset(0x898)] public readonly IntPtr UltimatumProgressWindow;
			[FieldOffset(0x8a0)] public readonly IntPtr ExpeditionSelectPanel;
			[FieldOffset(0x8A8)] public readonly IntPtr LogbookReceptaclePanel;
			[FieldOffset(0x8B0)] public readonly IntPtr ExpeditionLockerPanel;
			[FieldOffset(0x8B8)] public readonly IntPtr KalandraMirroredTabletPanel;
			[FieldOffset(0x8C0)] public readonly IntPtr KalandraReflectionPanel;
			[FieldOffset(0x8E8)] public readonly IntPtr BuffsPanel;
			[FieldOffset(0x8F0)] public readonly IntPtr DelveDarkness; // Debuffs Panel
			[FieldOffset(0x928)] public readonly IntPtr AreaInstanceUi;
			[FieldOffset(0x988)] public readonly IntPtr InteractButtonWrapper;
			[FieldOffset(0x990)] public readonly IntPtr SkipAheadButton;
			[FieldOffset(0x998)] public readonly IntPtr SyndicateHelpButton;
			[FieldOffset(0x9A0)] public readonly IntPtr SyndicateReleasePanel;
			[FieldOffset(0x9A8)] public readonly IntPtr LeagueInteractPanel;
			[FieldOffset(0x9B0)] public readonly IntPtr MetamorphInteractPanel;
			[FieldOffset(0x9B8)] public readonly IntPtr RitualInteractPanel;
			[FieldOffset(0x9C0)] public readonly IntPtr ExpeditionInteractPanel;
			[FieldOffset(0x9F8)] public readonly IntPtr InvitesPanel;
			[FieldOffset(0xA48)] public readonly IntPtr GemLvlUpPanel;
			[FieldOffset(0xA58)] public readonly IntPtr SkillBarNotifyPanel1;
			[FieldOffset(0xB18)] public readonly IntPtr ItemOnGroundTooltip;

		}

		// Entity offsets
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Entity {
			[FieldOffset(0x08)] public readonly IntPtr ptrDetails;
			[FieldOffset(0x10)] public readonly ArrayHandle ComponentsArray; // of IntPtr (to base address of a Component)
			[FieldOffset(0x50)] public readonly Vector3 WorldPos; // possible
			[FieldOffset(0x60)] public readonly uint Id;
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
			[FieldOffset(0x30)] public readonly IntPtr ComponentMap;
			[FieldOffset(0x38)] public readonly long Capacity;
			[FieldOffset(0x48)] public readonly long Counter;
		}

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

		}

		/// <summary>
		/// Once you know that some PointerX in a ComponentArrayEntry, each struct looks like this.
		/// Index refers to the Entity.ComponentsArray (not the ComponentMap)
		/// ie, ComponentsArray[Index] is the base address of the Component instance in memory
		/// </summary>
		[StructLayout(LayoutKind.Sequential, Pack = 1)] public struct ComponentNameAndIndexStruct {
			public readonly IntPtr ptrName;
			public readonly int Index;
			public readonly int Padding;
		}


		// Component members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component {
			[FieldOffset(0x08)] public readonly IntPtr entOwner; // Entity
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Life {
			[FieldOffset(0x8)] public readonly IntPtr entOwner;

			[FieldOffset(0x198)] public readonly float ManaRegen;
			[FieldOffset(0x19C)] public readonly int MaxMana;
			[FieldOffset(0x1A0)] public readonly int CurMana;
			[FieldOffset(0x1A4)] public readonly int ReservedFlatMana;
			[FieldOffset(0x1A8)] public readonly int ReservedPercentMana;

			[FieldOffset(0x1D4)] public readonly int MaxES;
			[FieldOffset(0x1D8)] public readonly int CurES;

			[FieldOffset(0x230)] public readonly float Regen;
			[FieldOffset(0x234)] public readonly int MaxHP;
			[FieldOffset(0x238)] public readonly int CurHP;
			[FieldOffset(0x23C)] public readonly int ReservedFlatHP;
			[FieldOffset(0x240)] public readonly int ReservedPercentHP;

		}

		// Actor members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Actor {
			[FieldOffset(0x008)] public readonly IntPtr entOwner; // Entity
			[FieldOffset(0x1A8)] public readonly IntPtr ptrAction; // ptr Component_Actor_Action
			[FieldOffset(0x208)] public readonly Component_ActionFlags ActionFlags;

			[FieldOffset(0x234)] public readonly int AnimationId;

			[FieldOffset(0x690)] public readonly ArrayHandle ActorSkillsHandle; // of ActorSkillArrayEntry
			[FieldOffset(0x6A8)] public readonly ArrayHandle ActorSkillUIStatesHandle; // of ActorSkillUIState
			[FieldOffset(0x6D8)] public readonly ArrayHandle DeployedObjectsHandle; // of ptr to DeployedObjectsArrayElement
			[FieldOffset(0x6C0)] public readonly ArrayHandle ActorVaalSkillsHandle; // of ActorVaalSkillArrayEntry
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
			[FieldOffset(0x54)] public readonly int TotalUses;
			[FieldOffset(0x5C)] public readonly int CooldownMS;
			[FieldOffset(0x6C)] public readonly int SoulsPerUse;
			[FieldOffset(0x70)] public readonly int TotalVaalUses;
			[FieldOffset(0xB0)] public readonly IntPtr ptrGameStats; // ptr to GameStatArray
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkillUIState {
			[FieldOffset(0x10)] public readonly long CooldownLow;
			[FieldOffset(0x18)] public readonly long CooldownHigh;
			[FieldOffset(0x30)] public readonly int NumberOfUses;
			[FieldOffset(0x3C)] public readonly ushort SkillId;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameStatArray {
			[FieldOffset(0xE8)] public readonly ArrayHandle Values; // of GameStateArrayEntry
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
			[FieldOffset(0x28)] public readonly int CastTypeCount;
			[FieldOffset(0x30)] public readonly IntPtr CastTypes; // ptr to int array
			[FieldOffset(0x38)] public readonly int SkillTagCount;
			[FieldOffset(0x40)] public readonly IntPtr SkillTags; // ptr to array of SkillTagEntry
			[FieldOffset(0x50)] public readonly IntPtr LongDescription; // ptr to string unicode
			[FieldOffset(0x58)] public readonly ArrayHandle UnknownArray;
			[FieldOffset(0x60)] public readonly byte UnknownByte;
			[FieldOffset(0x61)] public readonly IntPtr UnknownArrayPtr;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillTagEntry {
			[FieldOffset(0x00)] public readonly IntPtr ptrSkillTagDesc; // ptr to string unicode
			[FieldOffset(0x08)] public readonly IntPtr ptrDataFile;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillTagDesc {
			[FieldOffset(0x00)] public readonly IntPtr DisplayName; // ptr to string unicode
			[FieldOffset(0x08)] public readonly IntPtr InternalName; // ptr to string unicode
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
			[FieldOffset(0x10)] public readonly int MaxVaalSouls;
			[FieldOffset(0x14)] public readonly int CurVaalSouls;
			[FieldOffset(0x18)] public readonly IntPtr ptrUnknown; // possibly into DataFile, or a stat array

		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct DeployedObjectsArrayEntry {
			[FieldOffset(0x00)] public readonly ushort Unknown0;
			[FieldOffset(0x02)] public readonly ushort SkillId; // matches some Skill.Id in the player's skill array
			[FieldOffset(0x04)] public readonly ushort EntityId; // matches some Entity.Id in the world
			[FieldOffset(0x06)] public readonly ushort Unknown1;
		}

		/// <summary>
		/// Used to fill in Components where we don't know any other fields yet
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Empty {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Animated {
			[FieldOffset(0x008)] public readonly IntPtr entOwner;
			[FieldOffset(0x1E8)] public readonly IntPtr ptrToAnimatedEntity;
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
			[FieldOffset(0x18)] public readonly byte IsInvisible;
			[FieldOffset(0x19)] public readonly byte IsRemovable;
			[FieldOffset(0x3E)] public readonly byte Charges;
			[FieldOffset(0x18)] public readonly float MaxTime;
			[FieldOffset(0x1C)] public readonly float Timer;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Buffs {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x158)] public readonly ArrayHandle Buffs; // of IntPtr to Buff
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
			[FieldOffset(0x158)] public readonly IntPtr ptrToStrongboxDetails;
			[FieldOffset(0x160)] public readonly bool IsOpened;
			[FieldOffset(0x161)] public readonly bool IsLocked;
			[FieldOffset(0x164)] public readonly byte Quality;
			[FieldOffset(0x1A0)] public readonly bool IsStrongbox;
			[FieldOffset(0x1A8)] public readonly IntPtr ptrToChestEffect;
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
			[FieldOffset(0x18)] public readonly ArrayHandle UnknownArray;
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
			[FieldOffset(0x30)] public readonly ArrayHandle UniqueName; // of UniqueNameEntry
			[FieldOffset(0xA8)] public readonly bool Identified;
			[FieldOffset(0x0AC)] public readonly ItemRarity ItemRarity;
			[FieldOffset(0x0B8)] public readonly ArrayHandle ImplicitModsArray; // of ItemModEntry
			[FieldOffset(0x0D0)] public readonly ArrayHandle ExplicitModsArray; // of ItemModEntry
			[FieldOffset(0x0E8)] public readonly ArrayHandle EnchantedModsArray; // of ItemModEntry
			[FieldOffset(0x100)] public readonly ArrayHandle ScourgeModsArray; // of ItemModEntry
			[FieldOffset(0x1F0)] public readonly IntPtr ModStats;
			[FieldOffset(0x220)] public readonly uint ItemLevel;
			[FieldOffset(0x224)] public readonly uint RequiredLevel;
			[FieldOffset(0x228)] public readonly IntPtr ptrIncubator; // ptr to IncubatorEntry
			[FieldOffset(0x238)] public readonly short IncubatorKillCount;
			[FieldOffset(0x23D)] public readonly byte IsMirrored;
			[FieldOffset(0x23E)] public readonly byte IsSplit;
			[FieldOffset(0x23F)] public readonly byte IsUsable;
			[FieldOffset(0x241)] public readonly byte IsSynthesised;

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
			[FieldOffset(0x28)] public readonly IntPtr ptrItemModEntryNames;
			[FieldOffset(0x30)] public readonly IntPtr Padding; // so size = 0x38
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ItemModEntryNames {
			[FieldOffset(0x000)] public readonly IntPtr strGroupName;
			[FieldOffset(0x064)] public readonly IntPtr strDisplayName;
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
			[FieldOffset(0x13c)] public MonsterRarity Rarity;
			[FieldOffset(0x160)] public ArrayHandle Mods;
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
			[FieldOffset(0x2C)] public readonly Vector2i ClickToNextPosition;
			[FieldOffset(0x34)] public readonly Vector2i WasInThisPosition;
			[FieldOffset(0x470)] public readonly byte IsMoving; // ==2 when moving
			[FieldOffset(0x54C)] public readonly Vector2i WantMoveToPosition;
			[FieldOffset(0x554)] public readonly float StayTime;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Player {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x184)] public readonly uint XP;
			[FieldOffset(0x188)] public readonly uint Strength;
			[FieldOffset(0x18C)] public readonly uint Dexterity;
			[FieldOffset(0x190)] public readonly uint Intelligence;
			[FieldOffset(0x194)] public readonly byte AllocatedLootId;
			[FieldOffset(0x198)] public readonly PantheonGod PantheonMinor;
			[FieldOffset(0x199)] public readonly PantheonGod PantheonMajor;
			[FieldOffset(0x1A3)] public readonly byte Level;
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
			[FieldOffset(0x1D9)] public readonly byte Reaction;
			public bool IsHostile => (Reaction & 0x7F) != 1;
			[FieldOffset(0x260)] public readonly Vector2i GridPos;
			[FieldOffset(0x268)] public readonly float Rotation;
			[FieldOffset(0x27C)] public readonly float Scale;
			[FieldOffset(0x280)] public readonly int Size;
			[FieldOffset(0x288)] public readonly Vector2 WorldPos;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Quality {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x18)] public readonly int ItemQuality;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Render {
			[FieldOffset(0x08)] public readonly IntPtr entOwner;
			[FieldOffset(0x78)] public readonly Vector3 Pos;
			[FieldOffset(0x84)] public readonly Vector3 Bounds;
			[FieldOffset(0xa0)] public readonly StringHandle Name; // of unicode bytes
			[FieldOffset(0xBC)] public readonly Vector3 Rotation;
			[FieldOffset(0xc8)] public readonly float Height;
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
			[FieldOffset(0x18)] public readonly int CurSize;
			[FieldOffset(0x38)] public readonly int MaxSize;
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
			[FieldOffset(0x280)] public readonly ItemsOnGroundLabelEntry hoverLabel; // ptr to EntityLabel : Element
			[FieldOffset(0x2A8)] public readonly IntPtr labelsOnGroundHead;
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
			[FieldOffset(0x370)] public readonly InventoryArray InventoryList;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InventoryArray {
			[FieldOffset(0x00)] public readonly IntPtr None;
			[FieldOffset(0x08)] public readonly IntPtr Helm; // ptr to Element_Inventory
			[FieldOffset(0x10)] public readonly IntPtr Amulet;
			[FieldOffset(0x18)] public readonly IntPtr Chest;
			[FieldOffset(0x20)] public readonly IntPtr LWeapon;
			[FieldOffset(0x28)] public readonly IntPtr RWeapon;
			[FieldOffset(0x30)] public readonly IntPtr LWeaponSwap;
			[FieldOffset(0x38)] public readonly IntPtr RWeaponSwap;
			[FieldOffset(0x40)] public readonly IntPtr LRing;
			[FieldOffset(0x48)] public readonly IntPtr RRing;
			[FieldOffset(0x50)] public readonly IntPtr Gloves;
			[FieldOffset(0x58)] public readonly IntPtr Belt;
			[FieldOffset(0x60)] public readonly IntPtr Boots;
			[FieldOffset(0x68)] public readonly IntPtr Backpack;
			[FieldOffset(0x70)] public readonly IntPtr Flask;
			[FieldOffset(0x78)] public readonly IntPtr Trinket;
			[FieldOffset(0x80)] public readonly IntPtr LWeaponSkin;
			[FieldOffset(0x88)] public readonly IntPtr LWeaponEffect;
			[FieldOffset(0x90)] public readonly IntPtr LWeaponAddedEffect;
			[FieldOffset(0x98)] public readonly IntPtr RWeaponSkin;
			[FieldOffset(0xa0)] public readonly IntPtr RWeaponEffect;
			[FieldOffset(0xa8)] public readonly IntPtr RWeaponAddedEffect;
			[FieldOffset(0xb0)] public readonly IntPtr HelmSkin;
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
			[FieldOffset(0x3F8)] public readonly IntPtr incorrectTooltip;
			[FieldOffset(0x440)] public readonly IntPtr entItem;
			[FieldOffset(0x448)] public readonly Vector2i InventPosition;
			[FieldOffset(0x450)] public readonly int Width;
			[FieldOffset(0x454)] public readonly int Height;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_Map {
			[FieldOffset(0x280)] public readonly IntPtr ptrToSubMap_Full;
			[FieldOffset(0x288)] public readonly IntPtr ptrToSubMap_Mini;
			[FieldOffset(0x250)] public readonly IntPtr ptrToElement_OrangeWords;
			[FieldOffset(0x2A8)] public readonly IntPtr ptrToElement_BlueWords;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element_SubMap {
			[FieldOffset(0x270)] public readonly Vector2 Shift;
			[FieldOffset(0x278)] public readonly Vector2 DefaultShift;
			[FieldOffset(0x2B0)] public readonly float Zoom; // from 0.5 (zoomed out) to 1.5 (zoomed in)
		}
	}

}
