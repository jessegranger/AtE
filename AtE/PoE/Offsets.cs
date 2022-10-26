using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AtE {


	public static class Offsets {
		/// <summary>
		///  Used as a placeholder where we dont know which struct yet.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)]
		public struct Empty {
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
		public struct None { }

		public static readonly string WindowClass = "POEWindowClass";
		public static readonly string WindowTitle = "Path of Exile";

		public static readonly string GameStateBase_SearchMask = "xxxxxxxx";
		public static readonly byte[] GameStateBase_SearchPattern = new byte[] {
			0x48, 0x8b, 0xf1, 0x33, 0xed, 0x48, 0x39, 0x2d
		};
		public static readonly int GameStateBasePtrHop1 = 0x8;
		public static readonly int GameStateBasePtrHop2 = 0xC;

		// GameStateBase members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameStateBase {
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
		public static readonly int GameState_Kind = 0x0B;

		// members of AllGameStates:
		[StructLayout(LayoutKind.Explicit)] public struct GameStateArrayEntry {
			[FieldOffset(0x0)] public readonly IntPtr ptrToGameState;
			[FieldOffset(0x8)] public readonly long ptrToUnknown;
		}

		// members of InGameState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState {
			[FieldOffset(0x0B)] public readonly GameStateType Kind;
			[FieldOffset(0x018)] public readonly IntPtr ptrData; // ptr to InGameState_Data struct
			[FieldOffset(0x078)] public readonly IntPtr ptrWorldData;
			[FieldOffset(0x098)] public readonly IntPtr ptrEntityLabelMap;
			[FieldOffset(0x1A0)] public readonly IntPtr elemRoot;
			[FieldOffset(0x1D8)] public readonly IntPtr elemHover; // element which is currently hovered
			[FieldOffset(0x210)] public readonly int MousePosX;
			[FieldOffset(0x214)] public readonly int MousePosY;
			[FieldOffset(0x21C)] public readonly Vector2 UIHoverOffset; // mouse position offset in hovered UI element
			[FieldOffset(0x224)] public readonly Vector2 MousePos;
			[FieldOffset(0x450)] public readonly IntPtr ptrUIElements; // ptr to InGameState_UIElements
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct PreGameState {
			[FieldOffset(0x130)] public IntPtr UIRoot;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EscapeGameState {
			[FieldOffset(0x0B)] public GameStateType Kind;
			[FieldOffset(0x100)] public IntPtr elemRoot;
		}

		// members of AreaLoadingState
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AreaGameState {
			[FieldOffset(0xC8)] public long IsLoading;
			[FieldOffset(0x100)] public IntPtr elemRoot;
			[FieldOffset(0x3A8)] public IntPtr strAreaName;
		}

		// Element members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Element {
			// TODO: move this value to MapOffsets, the only place it's used
			// public const int OffsetBuffers = 0x6EC;

			[FieldOffset(0x28)] public IntPtr Self;
			[FieldOffset(0x30)] public ArrayHandle Children;

			// This was replaced with a GetRoot() function ptr which we cant call
			// [FieldOffset(0xD8)] public IntPtr Root; // Element
			[FieldOffset(0xE0)] public IntPtr elemParent; // Element
			[FieldOffset(0xE8)] public Vector2 Position;
			[FieldOffset(0xE8)] public float X;
			[FieldOffset(0xEC)] public float Y;
			[FieldOffset(0x158)] public float Scale;
			[FieldOffset(0x161)] public byte IsVisibleLocal;

			[FieldOffset(0x160)] public uint ElementBorderColor;
			[FieldOffset(0x164)] public uint ElementBackgroundColor;
			[FieldOffset(0x168)] public uint ElementOverlayColor;

			[FieldOffset(0x180)] public Vector2 Size;
			[FieldOffset(0x180)] public float Width;
			[FieldOffset(0x184)] public float Height;

			// everything below here is wrong I think
			[FieldOffset(0x190)] public uint TextBoxBorderColor;
			[FieldOffset(0x190)] public uint TextBoxBackgroundColor;
			[FieldOffset(0x194)] public uint TextBoxOverlayColor;

			[FieldOffset(0x1C0)] public uint HighlightBorderColor;
			[FieldOffset(0x1C3)] public bool isHighlighted;

			// incorrect [FieldOffset(0x3D0)] public long Tooltip;
		}

		[StructLayout(LayoutKind.Explicit)] public struct ChildrenArrayEntry {
			[FieldOffset(0x0)] public long ptrChild;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct WorldData {
			[FieldOffset(0xA0)] public IntPtr WorldAreaDetails; // TODO
			[FieldOffset(0xA8)] public Camera Camera;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Camera {
			[FieldOffset(0x8)] public int Width;
			[FieldOffset(0xC)] public int Height;
			[FieldOffset(0x1C4)] public float ZFar;

			//First value is changing when we change the screen size (ratio)
			//4 bytes before the matrix doesn't change
			[FieldOffset(0x80)] public Matrix4x4 Matrix; // 4x4 floats = 16 floats 128 bytes
																									 // the last 3 floats in the matrix are the camera position
																									 // 0x80 + (128 - 12) = 0xF4
			[FieldOffset(0xF4)] public Vector3 Position; // the last 3 floats of the matrix are the X,Y,Z
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState_Data {
			[FieldOffset(0x0A8)] public byte CurrentAreaLevel;
			[FieldOffset(0x10C)] public uint CurrentAreaHash;
			[FieldOffset(0x120)] public IntPtr MapStats;
			// [FieldOffset(0x260)] public long LabDataPtr; //May be incorrect
			[FieldOffset(0x778)] public IntPtr ServerData;
			[FieldOffset(0x780)] public IntPtr entPlayer; // ptr Entity
			[FieldOffset(0x830)] public IntPtr EntityList; // ptr EntityListNode
			[FieldOffset(0x838)] public long EntitiesCount;
			// [FieldOffset(0x9C8)] public long Terrain; // TODO: TerrainData struct
		}

    [StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InGameState_UIElements {
      [FieldOffset(0x250)] public readonly IntPtr GetQuests;
      [FieldOffset(0x288)] public readonly IntPtr GameUI;
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
			[FieldOffset(0x08)] public IntPtr ptrDetails;
			[FieldOffset(0x10)] public ArrayHandle ComponentsArray;
			[FieldOffset(0x50)] public Vector3 WorldPos; // possible
			[FieldOffset(0x60)] public uint Id;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EntityDetails {
			[FieldOffset(0x08)] public IntPtr ptrPath;
			[FieldOffset(0x30)] public IntPtr ptrComponentLookup;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct EntityListNode {
			[FieldOffset(0x00)] public IntPtr First;
			[FieldOffset(0x08)] public IntPtr Second;
			[FieldOffset(0x10)] public IntPtr Third;
			[FieldOffset(0x28)] public IntPtr Entity;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ComponentLookup {
			[FieldOffset(0x30)] public IntPtr ComponentArray;
			[FieldOffset(0x38)] public long Capacity;
			[FieldOffset(0x48)] public long Counter;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)] public struct ComponentArrayEntry {
			public byte Flag0;
			public byte Flag1;
			public byte Flag2;
			public byte Flag3;
			public byte Flag4;
			public byte Flag5;
			public byte Flag6;
			public byte Flag7;

			public ComponentNameAndIndexStruct Pointer0;
			public ComponentNameAndIndexStruct Pointer1;
			public ComponentNameAndIndexStruct Pointer2;
			public ComponentNameAndIndexStruct Pointer3;
			public ComponentNameAndIndexStruct Pointer4;
			public ComponentNameAndIndexStruct Pointer5;
			public ComponentNameAndIndexStruct Pointer6;
			public ComponentNameAndIndexStruct Pointer7;

			public static byte InvalidPointerFlagValue = byte.MaxValue;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)] public struct ComponentNameAndIndexStruct {
			public IntPtr ptrName;
			public int Index;
			public int Padding;
		}


		// Component members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component {
			[FieldOffset(0x08)] public IntPtr entOwner; // Entity
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Life {
			[FieldOffset(0x8)] public IntPtr entOwner;

			[FieldOffset(0x198)] public float ManaRegen;
			[FieldOffset(0x19C)] public int MaxMana;
			[FieldOffset(0x1A0)] public int CurMana;
			[FieldOffset(0x1A4)] public int ReservedFlatMana;
			[FieldOffset(0x1A8)] public int ReservedPercentMana;

			[FieldOffset(0x1D4)] public int MaxES;
			[FieldOffset(0x1D8)] public int CurES;

			[FieldOffset(0x230)] public float Regen;
			[FieldOffset(0x234)] public int MaxHP;
			[FieldOffset(0x238)] public int CurHP;
			[FieldOffset(0x23C)] public int ReservedFlatHP;
			[FieldOffset(0x240)] public int ReservedPercentHP;

		}

		// Actor members:
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Actor {
			[FieldOffset(0x008)] public IntPtr entOwner; // Entity
			[FieldOffset(0x1A8)] public IntPtr ptrAction; // ptr Component_Actor_Action
			[FieldOffset(0x208)] public Component_ActionFlags ActionFlags;

			[FieldOffset(0x234)] public int AnimationId;

			[FieldOffset(0x690)] public ArrayHandle ActorSkillsHandle; // of ActorSkillArrayEntry
			[FieldOffset(0x6A8)] public ArrayHandle ActorSkillUIStatesHandle; // of ActorSkillUIState
			[FieldOffset(0x6D8)] public ArrayHandle DeployedObjectsHandle; // ptr to DeployedObjectsArrayElement
			[FieldOffset(0x6C0)] public ArrayHandle ActorVaalSkillsHandle;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkillArrayEntry {
			[FieldOffset(0x00)] public int Padding;
			[FieldOffset(0x08)] public IntPtr ActorSkillPtr; // ptr ActorSkill
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
			[FieldOffset(0xB0)] public IntPtr Target; // ptr Entity?
			[FieldOffset(0x150)] public long Skill;
			[FieldOffset(0x170)] public Vector2 Destination;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkill {
			[FieldOffset(0x08)] public byte UseStage;
			// [FieldOffset(0x0C)] public byte KeyBindFlags; // unknown how this works but it changes when you change your skill key binds in the UI
			[FieldOffset(0x10)] public ushort Id;
			[FieldOffset(0x18)] public IntPtr ptrGemEffects;
			[FieldOffset(0x80)] public byte CanBeUsedWithWeapon;
			[FieldOffset(0x82)] public byte CanBeUsed;
			[FieldOffset(0x54)] public int TotalUses;
			[FieldOffset(0x5C)] public int CooldownMS;
			[FieldOffset(0x6C)] public int SoulsPerUse;
			[FieldOffset(0x70)] public int TotalVaalUses;
			[FieldOffset(0xB0)] public IntPtr ptrGameStats; // ptr to GameStatArray
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorSkillUIState {
			[FieldOffset(0x10)] public long CooldownLow;
			[FieldOffset(0x18)] public long CooldownHigh;
			[FieldOffset(0x30)] public int NumberOfUses;
			[FieldOffset(0x3C)] public ushort SkillId;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameStatArray {
			[FieldOffset(0xE8)] public ArrayHandle Items; // of GameStateArrayEntry
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GameStatArrayEntry {
			[FieldOffset(0x00)] public GameStat Key;
			[FieldOffset(0x04)] public int Value;
		}

		public enum GameStat : int {

		}

		// each skill gem has an array of these
		// at any time the GemEffectPtr points to one struct in the array
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct GemEffects {
			[FieldOffset(0x00)] public IntPtr ptrSkillGem; // ptr to SkillGem
			[FieldOffset(0x10)] public int Level;
			[FieldOffset(0x14)] public int RequiredLevel;
			[FieldOffset(0x18)] public int EffectivenessOfAddedDamage;
			[FieldOffset(0x20)] public int Cooldown;
			[FieldOffset(0x24)] public int StatsCount; // ?

			[FieldOffset(0x28)] public int StatsValue0; // embedded directly?
			[FieldOffset(0x28)] public int VaalSoulsPerUse;

			[FieldOffset(0x2c)] public int StatsValue1;
			[FieldOffset(0x2c)] public int VaalNumberOfUses;

			[FieldOffset(0x30)] public int StatsValue2;
			[FieldOffset(0x34)] public int StatsValue3;

			[FieldOffset(0x38)] public int StatsValue4;
			[FieldOffset(0x38)] public int VaalSoulGainPreventionMS;

			[FieldOffset(0x3c)] public int StatsValue5;
			[FieldOffset(0x40)] public int StatsValue6;
			[FieldOffset(0x44)] public long CostValuesCount;
			[FieldOffset(0x4c)] public IntPtr ptrCostValues; // ptr to int[CostCount]
			[FieldOffset(0x54)] public long CostTypesCount;
			[FieldOffset(0x5c)] public IntPtr ptrCostTypes; // ptr to (ptr CostTypeEntry) array
			// [FieldOffset(0x80)] public int ManaMultiplier;
			// [FieldOffset(0xE1)] public int SoulGainPreventionTime;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StatsArrayEntry {
			[FieldOffset(0x00)] public IntPtr ptrDataFile; // ptr to StatRecord in the Stats data files
			[FieldOffset(0x08)] public int Padding;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct CostTypeEntry {
			[FieldOffset(0x00)] public IntPtr ptrName; // ptr to string, like "Mana"
			[FieldOffset(0x08)] public IntPtr ptrDataFile; // ptr to StatRecord in the Stats data files
			// there are some other strings down here, like "{0} Mana"
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillGem {
			[FieldOffset(0x00)] public IntPtr NamePtr; // ptr to string
			[FieldOffset(0x08)] public long Padding0;
			[FieldOffset(0x10)] public long Padding1;
			[FieldOffset(0x18)] public byte Padding2;
			[FieldOffset(0x19)] public IntPtr UnknownPtr; // ptr to ActiveSkill
			[FieldOffset(0x63)] public IntPtr ptrActiveSkill; // ptr to ActiveSkill
			[FieldOffset(0x6b)] public IntPtr UnknownPtr3; // ptr to ActiveSkill
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActiveSkill {
			[FieldOffset(0x00)] public SkillNames Names;
			[FieldOffset(0x28)] public int CastTypeCount;
			[FieldOffset(0x30)] public IntPtr CastTypes; // ptr to int array
			[FieldOffset(0x38)] public int SkillTagCount;
			[FieldOffset(0x40)] public IntPtr SkillTags; // ptr to array of SkillTagEntry
			[FieldOffset(0x50)] public IntPtr LongDescription; // ptr to string unicode
			[FieldOffset(0x58)] public ArrayHandle UnknownArray;
			[FieldOffset(0x60)] public byte UnknownByte;
			[FieldOffset(0x61)] public IntPtr UnknownArrayPtr;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillTagEntry {
			[FieldOffset(0x00)] public IntPtr ptrSkillTagDesc; // ptr to string unicode
			[FieldOffset(0x08)] public IntPtr ptrDataFile;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillTagDesc {
			[FieldOffset(0x00)] public IntPtr DisplayName; // ptr to string unicode
			[FieldOffset(0x08)] public IntPtr InternalName; // ptr to string unicode
			[FieldOffset(0x10)] public IntPtr ptrDataFile; // ptr to a StatRecord in the data files
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct SkillNames {
			[FieldOffset(0x00)] public IntPtr InternalName; // ptr to string unicode
			[FieldOffset(0x08)] public IntPtr DisplayName; // ptr to string unicode
			[FieldOffset(0x10)] public IntPtr Description; // ptr to string unicode
			[FieldOffset(0x18)] public IntPtr SkillName; // ptr to string unicode
			[FieldOffset(0x20)] public IntPtr IconName; // ptr to string unicode
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ActorVaalSkillArrayEntry {
			[FieldOffset(0x00)] public IntPtr ptrActiveSkill; // ptr to ActiveSkill struct
			[FieldOffset(0x10)] public int MaxVaalSouls;
			[FieldOffset(0x14)] public int CurVaalSouls;
			// [FieldOffset(0x18)] public int SoulsPerUse; // no longer present
			[FieldOffset(0x18)] public IntPtr ptrUnknown; // possibly into DataFile, or a stat array

		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct DeployedObjectsArrayEntry {
			[FieldOffset(0x00)] public ushort Unknown0;
			[FieldOffset(0x02)] public ushort SkillId;
			[FieldOffset(0x04)] public ushort EntityId;
			[FieldOffset(0x06)] public ushort Unknown1;
		}

		/// <summary>
		/// Used to fill in Components where we don't know any other fields
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Empty {
			[FieldOffset(0x08)] public IntPtr entOwner;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Animated {
			[FieldOffset(0x008)] public IntPtr entOwner;
			[FieldOffset(0x1C8)] public IntPtr ptrToAnimatedEntityPtr;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_AreaTransition {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x28)] public ushort WorldAreaId;
			[FieldOffset(0x2A)] public AreaTransitionType TransitionType;
			[FieldOffset(0x40)] public IntPtr ptrToWorldArea;
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
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x10)] public IntPtr ptrToArmourValues;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ArmourValues {
			[FieldOffset(0x10)] public int EvasionMin;
			[FieldOffset(0x14)] public int EvasionMax;
			[FieldOffset(0x18)] public int ArmourMin;
			[FieldOffset(0x1C)] public int ArmourMax;
			[FieldOffset(0x20)] public int ESMin;
			[FieldOffset(0x24)] public int ESMax;
			[FieldOffset(0x28)] public int WardMin;
			[FieldOffset(0x2C)] public int WardMax;
			[FieldOffset(0x38)] public int MoveSpeed;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_AttributeRequirements {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x10)] public IntPtr ptrToAttributeValues;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct AttributeValues {
			[FieldOffset(0x10)] public int Strength;
			[FieldOffset(0x14)] public int Dexterity;
			[FieldOffset(0x18)] public int Intelligence;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Base {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x10)] public IntPtr ptrToBaseInfo;
			//[FieldOffset(0x18)] public long ItemVisualIdentityKey;
			//[FieldOffset(0x38)] public long FlavourTextKey;
			[FieldOffset(0x60)] public IntPtr strPublicPrice; // ptr to string unicode
			[FieldOffset(0xC6)] public InfluenceTypes Influences;
			[FieldOffset(0xC7)] public byte IsCorrupted;
			// [FieldOffset(0xC8)] public int UnspentAbsorbedCorruption;
			// [FieldOffset(0xCC)] public int ScourgedTier;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Base_Info {
			[FieldOffset(0x10)] public byte ItemCellSizeX;
			[FieldOffset(0x11)] public byte ItemCellSizeY;
			[FieldOffset(0x30)] public IntPtr strName;
			[FieldOffset(0x78)] public IntPtr strDescription; // ptr to string unicode
			[FieldOffset(0x80)] public IntPtr ptrEntityPath; // ptr to (ptr to?) string ascii "Metadata/..."
			[FieldOffset(0x88)] public IntPtr prtItemType; // ptr to (ptr to?) string unicode
			[FieldOffset(0x90)] public IntPtr ptrBaseItemTypes;
			[FieldOffset(0x98)] public IntPtr XBoxControllerItemDescription;
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
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x50)] public Vector3 BeamStart;
			[FieldOffset(0x5C)] public Vector3 BeamEnd;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_BlightTower {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x28)] public IntPtr ptrToBlightDetails;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct BlightDetails {
			[FieldOffset(0x00)] public IntPtr strId; // ptr to string unicode
			[FieldOffset(0x08)] public IntPtr strName; // ptr to string unicode
			[FieldOffset(0x18)] public IntPtr strIcon; // ptr to string unicode
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Buff {
			[FieldOffset(0x8)] public IntPtr ptrName; // ptr to (ptr to?) string unicode
			[FieldOffset(0x18)] public byte IsInvisible;
			[FieldOffset(0x19)] public byte IsRemovable;
			[FieldOffset(0x3E)] public byte Charges;
			[FieldOffset(0x18)] public float MaxTime;
			[FieldOffset(0x1C)] public float Timer;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Buffs {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x158)] public ArrayHandle Buffs; // of IntPtr to 
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Charges {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x10)] public IntPtr ptrToChargeDetails;
			[FieldOffset(0x18)] public int NumCharges;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct ChargeDetails {
			[FieldOffset(0x14)] public int PerUse;
			[FieldOffset(0x18)] public int Max;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Chest {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x158)] public IntPtr ptrToStrongboxDetails;
			[FieldOffset(0x160)] public bool IsOpened;
			[FieldOffset(0x161)] public bool IsLocked;
			[FieldOffset(0x164)] public byte Quality;
			[FieldOffset(0x1A0)] public bool IsStrongbox;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct StrongboxDetails {
			[FieldOffset(0x20)] public bool WillDestroyAfterOpen;
			[FieldOffset(0x21)] public bool IsLarge;
			[FieldOffset(0x22)] public bool Stompable;
			[FieldOffset(0x25)] public bool OpenOnDamage;
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_CurrencyInfo {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x58)] public int MaxStackSize;
		}
		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct Component_Inventories {
			[FieldOffset(0x08)] public IntPtr entOwner;
			[FieldOffset(0x38)] public IntPtr LeftHand; // ptr to InventoryVisual struct
			// wonder what's in all this space?
			[FieldOffset(0xa8)] public IntPtr RightHand; // ptr to InventoryVisual struct
			[FieldOffset(0x118)] public IntPtr Chest; // ptr to InventoryVisual struct
			[FieldOffset(0x188)] public IntPtr Helm; // ptr to InventoryVisual struct
			[FieldOffset(0x1f8)] public IntPtr Gloves; // ptr to InventoryVisual struct
			[FieldOffset(0x268)] public IntPtr Boots; // ptr to InventoryVisual struct
			[FieldOffset(0x2d8)] public IntPtr Unknown; // ptr to InventoryVisual struct
			[FieldOffset(0x348)] public IntPtr LeftRing; // ptr to InventoryVisual struct
			[FieldOffset(0x3b8)] public IntPtr RightRing; // ptr to InventoryVisual struct
			[FieldOffset(0x428)] public IntPtr Belt; // ptr to InventoryVisual struct
		}

		[StructLayout(LayoutKind.Explicit, Pack = 1)] public struct InventoryVisual {
			[FieldOffset(0x00)] public IntPtr ptrName;
			[FieldOffset(0x08)] public IntPtr ptrTexture;
			[FieldOffset(0x10)] public IntPtr ptrModel;
		}



	}

}
