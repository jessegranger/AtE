using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;


namespace AtE {
	public static partial class Globals {
		public static bool IsValid<T>(Component<T> c) where T : unmanaged => c != null && IsValid(c.Address) && !c.IsDisposed;

		public static bool IsAlive(Entity ent) => IsValid(ent) && IsAlive(ent.GetComponent<Life>());
		public static bool IsAlive(Life life) => (life?.CurHP ?? 0) > 0;
	}
	public abstract class Component<T> : MemoryObject<T>, IDisposable where T : unmanaged {

		public bool IsDisposed = false;
		private bool isDisposing = false;
		public override void Dispose() {
			if ( isDisposing || IsDisposed ) return;
			isDisposing = true;
			base.Dispose();
			IsDisposed = true;
		}

	}

	public class Life : Component<Offsets.Component_Life> {

		public int MaxHP => Cache.MaxHP;
		public int MaxES => Cache.MaxES;
		public int MaxMana => Cache.MaxMana;

		public int CurHP => Cache.CurHP;
		public int CurES => Cache.CurES;
		public int CurMana => Cache.CurMana;

		public float CurManaRegen => Cache.ManaRegen;
		public float CurHPRegen => Cache.CurHPRegen;
		public int TotalReservedHP => Cache.ReservedFlatHP + (int)(0.5f + (Cache.MaxHP * (Cache.ReservedPercentHP / 10000d)));
		public int TotalReservedMana => Cache.ReservedFlatMana + (int)(0.5f + (Cache.MaxMana * (Cache.ReservedPercentMana / 10000d)));

	}
	public static partial class Globals {
		public static uint CurrentEHP(Entity ent) => CurrentEHP(ent?.GetComponent<Life>());
		public static uint CurrentEHP(Life life) => !IsValid(life) ? 0 : (uint)Math.Max(0, life.CurES) + (uint)Math.Max(0, life.CurHP);
		public static uint MaxEHP(Entity ent) => MaxEHP(ent?.GetComponent<Life>(), HasBuff(ent, "petrified_blood"));
		public static uint MaxEHP(Life life, bool petrified = false) => IsValid(life) ?
			(uint)Math.Max(0, life.MaxES) + MaxLife(life, petrified) : 0;
		public static uint MaxLife(Entity ent) => MaxLife(ent?.GetComponent<Life>(), HasBuff(ent, "petrified_blood"));
		public static uint MaxLife(Life life, bool petrified = false) => IsValid(life) ? 
			(uint)Math.Max(0, 
				petrified ? Math.Min(life.MaxHP - life.TotalReservedHP, life.MaxHP / 2)
				: life.MaxHP - life.TotalReservedHP) : 0;
		public static bool IsMissingEHP(Entity ent, float pct = .10f, bool petrified = false) {
			var life = ent?.GetComponent<Life>();
			return IsAlive(life) && CurrentEHP(life) < MaxEHP(life, petrified) * (1.0f - pct);
		}
		public static bool IsFullEHP(Life life, bool petrified = false) => IsValid(life) 
			? CurrentEHP(life) == MaxEHP(life, petrified) : false;
		public static bool IsFullEHP(Entity ent) => IsFullEHP(ent?.GetComponent<Life>());
		public static bool IsFullLife(Life life, bool petrified = false) => IsValid(life) && life.CurHP >= MaxLife(life, petrified);
		public static bool IsFullLife(Entity ent) => IsFullLife(ent?.GetComponent<Life>(), HasBuff(ent, "petrified_blood"));
		public static bool HasEnoughRage(Entity ent, int rage) => IsValid(ent) && ent.GetComponent<Buffs>().TryGetBuffValue("rage", out int cur) && cur >= rage;
	}

	public class Actor : Component<Offsets.Component_Actor> {

		public Offsets.Component_ActionFlags ActionFlags => Cache.ActionFlags;

		public int AnimationId => Cache.AnimationId;

		public ActorAction CurrentAction => IsValid(Address) && IsValid(Cache.ptrAction) ?
			new ActorAction() { Address = Cache.ptrAction } : null;

		public IEnumerable<ActorSkill> Skills =>
			new ArrayHandle<Offsets.ActorSkillArrayEntry>(Cache.ActorSkillsHandle)
				.ToArray(limit: 100) // anything more than that is corrupt nonsense
				.Select(x => new ActorSkill(this) { Address = x.ActorSkillPtr })
				.Where(x => x.IsValid())
			;

		public IEnumerable<Offsets.ActorVaalSkillArrayEntry> VaalSkills =>
			new ArrayHandle<Offsets.ActorVaalSkillArrayEntry>(Cache.ActorVaalSkillsHandle);

		public IEnumerable<DeployedObject> DeployedObjects =>
			new ArrayHandle<Offsets.DeployedObjectsArrayEntry>(Cache.DeployedObjectsHandle)
				.Select(x => new DeployedObject(this, x));

		public IEnumerable<Offsets.ActorSkillUIState> ActorSkillUIStates =>
			new ArrayHandle<Offsets.ActorSkillUIState>(Cache.ActorSkillUIStatesHandle);

		internal bool IsOnCooldown(ushort skillId) => ActorSkillUIStates
			.Where(s => s.SkillId == skillId)
			.Any(s => s.IsOnCooldown);
				// (s.CooldownHigh - s.CooldownLow) >> 4 >= s.NumberOfUses
			// );
	}

	public class ActorAction : MemoryObject<Offsets.Component_Actor_Action> {

		public Entity Target => IsValid(Address) && EntityCache.TryGetEntity(Address, out Entity ent) ? ent : null;

		public long Skill => Cache.Skill;
		public Vector2 Destination => Cache.Destination;
	}

	public class ActorSkill : MemoryObject<Offsets.ActorSkill>, IDisposable {
		public Cached<Offsets.GemEffects> GemEffects;
		public Cached<Offsets.SkillGem> SkillGem;
		public Cached<Offsets.ActiveSkill> ActiveSkill;

		public Actor Actor;
		public ActorSkill(Actor actor) => Actor = actor;

		public override IntPtr Address { get => base.Address;
			set {
				if ( value == base.Address ) {
					return;
				}

				base.Address = value;

				if ( value == IntPtr.Zero ) {
					return;
				}

				GemEffects = CachedStruct<Offsets.GemEffects>(() => Cache.ptrGemEffects);
				SkillGem = CachedStruct<Offsets.SkillGem>(() => GemEffects.Value.ptrSkillGem);
				ActiveSkill = CachedStruct<Offsets.ActiveSkill>(() => SkillGem.Value.ptrActiveSkill);

				// if this is a Vaal skill, it will have an entry in the Owner's VaalSkillsArray
				VaalEntry = new Cached<Offsets.ActorVaalSkillArrayEntry>(() =>
					Actor?.VaalSkills.Where(v => v.ptrActiveSkill == SkillGem.Value.ptrActiveSkill).FirstOrDefault() ?? default);

			}
		}

		private bool isDisposing = false;
		private bool isDisposed = false;
		public override void Dispose() {
			if ( isDisposed || isDisposing ) return;
			isDisposing = true;
			Actor = null;
			GemEffects?.Dispose();
			GemEffects = null;
			SkillGem?.Dispose();
			SkillGem = null;
			ActiveSkill?.Dispose();
			ActiveSkill = null;
			base.Dispose();
			isDisposed = true;
		}

		public bool IsValid() => (!isDisposing) && (!isDisposed) && (SkillGem?.Value.NamePtr ?? default) != IntPtr.Zero;

		public ushort Id => Cache.Id;

		private string internalName = null;
		public string InternalName => internalName != null
			? internalName
			: PoEMemory.TryReadString(ActiveSkill.Value.Names.InternalName, Encoding.Unicode, out internalName)
			? internalName
			: null;

		private string displayName = null;
		public string DisplayName => displayName != null
			? displayName
			: PoEMemory.TryReadString(ActiveSkill.Value.Names.DisplayName, Encoding.Unicode, out displayName)
			? displayName
			: null;

		public bool IsOnCooldown => GetPlayer()?.GetComponent<Actor>()?.IsOnCooldown(Cache.Id) ?? false;

		public bool CanBeUsed =>
			Cache.CanBeUsed == 1
			&& Cache.CanBeUsedWithWeapon == 1;

		// if there is an entry in the Actor VaalSkillsArray with the same
		// ptrActiveSkill as ours, cache that data here
		private Cached<Offsets.ActorVaalSkillArrayEntry> VaalEntry;
		public int CurVaalSouls => VaalEntry.Value.CurVaalSouls;
		public int MaxVaalSouls => VaalEntry.Value.MaxVaalSouls;
		public int SoulsPerUse => GemEffects.Value.VaalSoulsPerUse;
		public int SoulGainPreventionMS => GemEffects.Value.VaalSoulGainPreventionMS;


	}

	// Not a MemoryObject like the others, constructed directly from an Entry struct
	public class DeployedObject : IDisposable {
		private Offsets.DeployedObjectsArrayEntry Entry;
		private Actor Actor;
		public DeployedObject(Actor actor, Offsets.DeployedObjectsArrayEntry entry) {
			Actor = actor;
			Entry = entry;
		}

		public ushort SkillId => Entry.SkillId;
		public ushort EntityId => Entry.EntityId;
		public ActorSkill GetSkill() => Actor?.Skills.FirstOrDefault(s => s.Id == Entry.SkillId);
		public Entity GetEntity() => GetEntityById(Entry.EntityId);

		public void Dispose() {
			Actor = null;
		}
	}

	public class Animated : Component<Offsets.Component_Animated> {

		public Entity AnimatedObject => IsValid(Address) && EntityCache.TryGetEntity(Cache.ptrToAnimatedEntity, out Entity ent) ? ent : null;
	}


	public class AreaTransition : Component<Offsets.Component_AreaTransition> {
		public Offsets.AreaTransitionType TransitionType => Cache.TransitionType;
	}

	public class Armour : Component<Offsets.Component_Armour> {
		public Cached<Offsets.ArmourValues> ArmourValues;
		public Armour() : base() =>
			ArmourValues = CachedStruct<Offsets.ArmourValues>(() => Cache.ptrToArmourValues);
	}

	public class AttributeRequirements : Component<Offsets.Component_AttributeRequirements> {
		public Cached<Offsets.AttributeValues> AttributeValues;
		public AttributeRequirements() : base() =>
			AttributeValues = CachedStruct<Offsets.AttributeValues>(() => Cache.ptrToAttributeValues);

		public int Strength => AttributeValues.Value.Strength;
		public int Dexterity => AttributeValues.Value.Dexterity;
		public int Intelligence => AttributeValues.Value.Intelligence;
	}

	/// <summary>
	/// This refers to an item base (ie, something in your inventory).
	/// </summary>
	public class Base : Component<Offsets.Component_Base> {

		public Cached<Offsets.Component_Base_Info> Info;
		public Base() : base() => Info = CachedStruct<Offsets.Component_Base_Info>(() => Cache.ptrToBaseInfo);

		public byte CellSizeX => Info.Value.ItemCellSizeX;
		public byte CellSizeY => Info.Value.ItemCellSizeY;

		public string PublicPrice => PoEMemory.TryReadString(Cache.strPublicPrice, Encoding.Unicode, out string price)
			? price
			: null;

		public Offsets.InfluenceTypes Influences => Cache.Influences;

		public bool IsCorrupted => Cache.IsCorrupted;

	}

	public class BaseEvents : Component<Offsets.Component_Empty> { }

	public class Beam : Component<Offsets.Component_Beam> {
		public Vector3 Start => Cache.BeamStart;
		public Vector3 End => Cache.BeamEnd;
	}

	public class BlightTower : Component<Offsets.Component_BlightTower> {
		public Cached<Offsets.BlightDetails> Details;
		public BlightTower() : base() => Details = CachedStruct<Offsets.BlightDetails>(() => Cache.ptrToBlightDetails);

		public string Id => PoEMemory.TryReadString(Details.Value.strId, Encoding.Unicode, out string id) ? id : null;
		public string Name => PoEMemory.TryReadString(Details.Value.strName, Encoding.Unicode, out string name) ? name : null;
		public string Icon => PoEMemory.TryReadString(Details.Value.strIcon, Encoding.Unicode, out string icon) ? icon : null;

	}

	public class Buff : MemoryObject<Offsets.Buff> {

		private string name;
		public string Name => name ?? (name =
			Address == IntPtr.Zero ? null :
			PoEMemory.TryRead(Cache.ptrName, out IntPtr ptr)
			&& PoEMemory.TryReadString(ptr, Encoding.Unicode, out string value)
			? value 
			: null);

		public byte Charges => Cache.Charges;
		public float Timer => Cache.Timer;
		public float MaxTime => Cache.MaxTime;
	}

	public class Buffs : MemoryObject {
		// this is a heavily accessed object so it needs some optimization
		// the only operation we care about is HasBuff

		// for starters, we dont want the standard CachedStruct because in this case
		// the struct is 200 bytes of nothing useful (it's unknown data structure)
		// (i suspect we are reading the list of keys from within an unordered_flat_map from boost)
		// so we just read the one ArrayHandle we care about from one offset:
		private static int buffsOffset = GetOffset<Offsets.Component_Buffs>("Buffs");

		// from that arrayhandle we will get this array of ptr to the active buffs
		private IntPtr[] buffPtrs;
		// this cached reader lets us read the real current arrayhandle from PoE memory once per frame per entity
		private Cached<Offsets.ArrayHandle> currentHandle;
		// this copy of the arrayhandle is saved so it can be compared
		// if they dont match, we re-parse the whole buffs array
		private Offsets.ArrayHandle lastKnownHandle;
		// if HasBuff() is called once, we memoize all the strings here for future calls
		private HashSet<string> buffCache;

		public Buffs():base() {
			currentHandle = CachedStruct<Offsets.ArrayHandle>(() => Address + buffsOffset);
		}

		private bool UpdateBuffPtrs() {
			var buffsArray = currentHandle.Value;
			if ( buffsArray.Head == lastKnownHandle.Head && buffsArray.Tail == lastKnownHandle.Tail ) {
				return true;
			}
			int count = buffsArray.ItemCount(8); // sizeof(IntPtr)
			if ( count < 0 || count > 500 ) {
				Log($"Rejecting corrupt(?) buffs data with {count} elements.");
				return false;
			}
			lastKnownHandle = buffsArray;
			buffPtrs = new IntPtr[count];
			buffCache = null;
			if ( count > 0 ) {
				if ( 0 == PoEMemory.TryRead(buffsArray.Head, buffPtrs) ) {
					Log($"Failed to read buffs data from {Describe(buffsArray.Head)}");
					return false;
				}
			}
			return true;
		}

		public override IntPtr Address {
			get => base.Address;
			set {
				if( value == base.Address ) {
					return;
				}

				base.Address = value;

				if( IsValid(base.Address) ) {
					if( ! UpdateBuffPtrs() ) {
						base.Address = IntPtr.Zero;
					}
				}
			}
		}

		public IEnumerable<Buff> GetBuffs() => buffPtrs?.Select(ptr => new Buff() { Address = ptr }) ?? Empty<Buff>();

		public bool HasBuff(string buffName) {
			if( !IsValid(Address) ) {
				return false;
			}
			if( buffPtrs == null ) {
				return false;
			}
			// check if the buff array size changed, and if so, re-parse it
			UpdateBuffPtrs();
			if( buffCache == null ) {
				// collect all the buff names and store them for quicker access
				buffCache = new HashSet<string>(buffPtrs.Select(ptr => IsValid(ptr) ? new Buff() { Address = ptr }.Name : "invalid"));
			}
			return buffCache.Contains(buffName);
		}

		public bool TryGetBuffValue(string buffName, out int value) {
			value = 0;
			if( buffPtrs == null ) {
				return false;
			}
			Buff found = buffPtrs
				.Select(ptr => new Buff() { Address = ptr })
				.FirstOrDefault(buff => buff?.Name?.Equals(buffName) ?? false);
			if ( found != null ) {
				value = found.Charges;
				return true;
			}
			return false;
		}

		internal void Invalidate() {
			lastKnownHandle.Head = IntPtr.Zero;
			lastKnownHandle.Tail = IntPtr.Zero;
		}

	}

	public static partial class Globals {
		public static bool IsValid(Buffs buff) => buff != null && IsValid(buff.Address);
		public static bool HasBuff(Entity ent, string buffName) =>
			buffName != null && IsValid(ent) && HasBuff(ent.GetComponent<Buffs>(), buffName);
		public static bool HasBuff(Buffs buffs, string buffName) => buffs?.HasBuff(buffName) ?? false;

		public static bool TryGetBuffValue(Entity ent, string buffName, out int value) => TryGetBuffValue(ent?.GetComponent<Buffs>(), buffName, out value);
		public static bool TryGetBuffValue(Buffs buffs, string buffName, out int value) {
			value = 0;
			return IsValid(buffs) && buffs.TryGetBuffValue(buffName, out value);
		}
	}

	public class CapturedMonster : Component<Offsets.Component_Empty> { }


	public class Charges : Component<Offsets.Component_Charges> {
		public Cached<Offsets.ChargeDetails> Details;
		public Charges() : base() => Details = CachedStruct<Offsets.ChargeDetails>(() => Cache.ptrToChargeDetails);

		public int Current => Cache.NumCharges;
		public int Max => Details.Value.Max;
		public int PerUse => Details.Value.PerUse;
	}


	public class Chest : Component<Offsets.Component_Chest> {
		public Cached<Offsets.StrongboxDetails> Details;
		public Chest() : base() => Details = CachedStruct<Offsets.StrongboxDetails>(() => Cache.ptrToStrongboxDetails);

		public bool IsOpened => Cache.IsOpened;
		public bool IsLocked => Cache.IsLocked;
		public bool IsStrongbox => Cache.ptrToChestEffect != IntPtr.Zero;
		public byte Quality => Cache.Quality;
		public bool WillDestroyAfterOpen => Details.Value.WillDestroyAfterOpen;
		public bool IsLarge => Details.Value.IsLarge;
		public bool Stompable => Details.Value.Stompable;
		public bool OpenOnDamage => Details.Value.OpenOnDamage;
	}

	class ClientAnimationController : Component<Offsets.Component_ClientAnimationController> {
		public int AnimationId => Cache.AnimationId;
	}
	class ClientBetrayalChoice : Component<Offsets.Component_Empty> { }
	class Counter : Component<Offsets.Component_Empty> { }
	class CritterAI : Component<Offsets.Component_Empty> { }

	class CurrencyInfo : Component<Offsets.Component_CurrencyInfo> {

		public int MaxStackSize => Cache.MaxStackSize;
	}

	class DelveLight : Component<Offsets.Component_Empty> { }
	class DiesAfterTime : Component<Offsets.Component_Empty> { }
	class Flask : Component<Offsets.Component_Empty> { }
	class Functions : Component<Offsets.Component_Empty> { }
	class HeistBlueprint : Component<Offsets.Component_Empty> { }
	class HeistContract : Component<Offsets.Component_Empty> { }
	class HeistEquipment : Component<Offsets.Component_Empty> { }
	class HeistRewardDisplay : Component<Offsets.Component_Empty> { }
	class HideoutDoodad : Component<Offsets.Component_Empty> { }

	public class InventoryVisual : MemoryObject<Offsets.InventoryVisual> {
		public string Name => PoEMemory.TryReadString(Cache.ptrName, Encoding.Unicode, out string name) ? name : null;
		public string Texture => PoEMemory.TryReadString(Cache.ptrTexture, Encoding.ASCII, out string texture) ? texture : null;
		public string Model => PoEMemory.TryReadString(Cache.ptrModel, Encoding.ASCII, out string model) ? model : null;
	}
	public class Inventories : Component<Offsets.Component_Inventories> {

		public ArrayHandle<IntPtr> Unknown => new ArrayHandle<IntPtr>(Cache.UnknownArray);
	}

	public class LimitedLifeSpan : Component<Offsets.Component_Empty> { }
	public class LocalStats : Component<Offsets.Component_Empty> { }

	public class Magnetic : Component<Offsets.Component_Magnetic> {
		public int Force => Cache.Force;
	}

	public class Map : Component<Offsets.Component_Map> {
		public Cached<Offsets.MapDetails> Details;
		public Map() : base() =>
			Details = CachedStruct<Offsets.MapDetails>(() => Cache.MapDetails);

		// Area = Files.GetWorldAreaByAddress(Details.ptrArea)

		public byte Tier => Cache.Tier;
		public byte Series => Cache.Series;

	}


	public class MinimapIcon : Component<Offsets.Component_MinimapIcon> {
		public string Name {
			get {
				if( PoEMemory.TryRead(Cache.strName, out Offsets.StringHandle name) ) {
					return name.Value;
				}
				return null;
			}
		}
		// public string Name => PoEMemory.TryReadString(Cache.strName, Encoding.Unicode, out string name) ? name : null;
		public bool IsVisible => Cache.IsVisible == 0x01;
		public bool IsHide => Cache.IsHide == 0x01;
	}

	public class Mods : Component<Offsets.Component_Mods> {
		public Cached<Offsets.ModStats> Stats;
		public Mods() : base() => Stats = CachedStruct<Offsets.ModStats>(() => Address);

		public bool IsIdentified => Cache.Identified;
		public Offsets.ItemRarity Rarity => Cache.ItemRarity;
		public uint Level => Cache.ItemLevel;
		public uint ReqLevel => Cache.RequiredLevel;
		public bool HasIncubator => Cache.ptrIncubator != IntPtr.Zero;
		public bool IsMirrored => Cache.IsMirrored == 1;
		public bool IsSplit => Cache.IsSplit == 1;
		public bool IsSynthesised => Cache.IsSynthesised == 1;
		public bool IsUsable => Cache.IsUsable == 1;

		public IEnumerable<Offsets.UniqueNameEntry> NameEntries =>
			new ArrayHandle<Offsets.UniqueNameEntry>(Cache.UniqueName);

		public IEnumerable<ItemMod> ExplicitMods => new ArrayHandle<Offsets.ItemModEntry>(Cache.ExplicitModsArray).Take(16).Select(e => new ItemMod(e));
		public IEnumerable<ItemMod> ImplicitMods => new ArrayHandle<Offsets.ItemModEntry>(Cache.ImplicitModsArray).Take(16).Select(e => new ItemMod(e));
		public IEnumerable<ItemMod> EnchantedMods => new ArrayHandle<Offsets.ItemModEntry>(Cache.EnchantedModsArray).Take(16).Select(e => new ItemMod(e));
		public IEnumerable<ItemMod> ScourgeMods => new ArrayHandle<Offsets.ItemModEntry>(Cache.ScourgeModsArray).Take(16).Select(e => new ItemMod(e));

		// TODO: A generic wrapper for a GameStatArray, as a Dictionar<GameStat, int>
		public IEnumerable<Offsets.ItemStatEntry> ExplicitStats => new ArrayHandle<Offsets.ItemStatEntry>(Stats.Value.ExplicitStatsArray);
		public IEnumerable<Offsets.ItemStatEntry> ImplicitStats => new ArrayHandle<Offsets.ItemStatEntry>(Stats.Value.ImplicitStatsArray);
		public IEnumerable<Offsets.ItemStatEntry> EnchantedStats => new ArrayHandle<Offsets.ItemStatEntry>(Stats.Value.EnchantedStatsArray);
		public IEnumerable<Offsets.ItemStatEntry> ScourgeStats => new ArrayHandle<Offsets.ItemStatEntry>(Stats.Value.ScourgeStatsArray);

	}
	public class ItemMod {
		private Offsets.ItemModEntry Entry;
		private Cached<Offsets.ItemModEntryNames> Names;
		public ItemMod(Offsets.ItemModEntry entry) {
			Entry = entry;
			Names = CachedStruct<Offsets.ItemModEntryNames>(() => entry.ptrItemModEntryNames);
		}
		public string GroupName => PoEMemory.TryReadString(Names.Value.strGroupName, Encoding.Unicode, out string name) ? name : null;
		public string DisplayName => PoEMemory.TryReadString(Names.Value.strDisplayName, Encoding.Unicode, out string name) ? name : null;
		public IEnumerable<int> Values =>
			Entry.Values.GetRecordPtrs(sizeof(int))
				.Select(a => PoEMemory.TryRead(a, out int value) ? value : 0);

	}
	public static partial class Globals {
		public static bool HasMod(Entity ent, string groupName) => HasMod(ent.GetComponent<Mods>(), groupName);
		public static bool HasMod(Mods mods, string groupName) => IsValid(mods) &&
			(mods.ExplicitMods.Any(m => HasMod(m, groupName))
			|| mods.ImplicitMods.Any(m => HasMod(m, groupName))
			|| mods.EnchantedMods.Any(m => HasMod(m, groupName)));
		public static bool HasMod(ItemMod mod, string groupName) => mod.GroupName.StartsWith(groupName);
	}

	public class Monolith : Component<Offsets.Component_Monolith> {
		public byte OpenStage => Cache.OpenStage;
	}
	public class Monster : Component<Offsets.Component_Empty> {
	}
	public class NPC : Component<Offsets.Component_NPC> {
		public bool Hidden => Cache.Hidden == 0;
		public bool VisibleOnMiniMap => Cache.VisibleOnMinimap == 1;
		public bool HasIconOverhead => Cache.Icon != IntPtr.Zero;
	}

	public class ObjectMagicProperties : Component<Offsets.Component_ObjectMagicProperties> {

		public Offsets.MonsterRarity Rarity => Cache.Rarity;

		public IEnumerable<Offsets.ObjectMagicProperties_ModEntry> Mods => new ArrayHandle<Offsets.ObjectMagicProperties_ModEntry>(Cache.Mods);

		public IEnumerable<string> ModNames => Mods.Select(m =>
			PoEMemory.TryRead(m.ptrAt28, out IntPtr strName) &&
			PoEMemory.TryReadString(strName, Encoding.Unicode, out string name) ? name : $"ptr28:{m.ptrAt28}->{strName}");
	}

	public class Pathfinding : Component<Offsets.Component_Pathfinding> {
		public Offsets.Vector2i NextPos => Cache.ClickToNextPosition;
		public Offsets.Vector2i PrevPos => Cache.WasInThisPosition;
		public Offsets.Vector2i TargetPos => Cache.WantMoveToPosition;

		public float TimeSinceLastMovementStart => Cache.StayTime;
		public bool IsMoving => Cache.IsMoving > 0; // really it is flags, 2 = moving directly, 4 = pathfinding around an obstacle? maybe some other flags for bumping into ents
	}

	public class Player : Component<Offsets.Component_Player> {

		public string Name => IsValid(Address) ? Cache.strName.Value : null;
		public uint XP => Cache.XP;
		public uint Str => Cache.Strength;
		public uint Dex => Cache.Dexterity;
		public uint Int => Cache.Intelligence;
		public byte Level => Cache.Level;
		public byte LootAllocationId => Cache.AllocatedLootId;
	}

	public class Portal : Component<Offsets.Component_Portal> {
		public IntPtr WorldArea => Cache.ptrWorldArea;
	}

	public class Positioned : Component<Offsets.Component_Positioned> {
		public Offsets.Vector2i GridPos => Cache.GridPos;
		public Vector2 GridPosF => new Vector2(Cache.GridPos.X, Cache.GridPos.Y);
		public Vector2 WorldPos => Cache.WorldPos;
		public float Rotation => Cache.Rotation;
		public float Scale => Cache.Scale;
		public int Size => Cache.Size;
		public byte Reaction => Cache.Reaction;
		public bool IsHostile => Cache.IsHostile;
	}

	public static partial class Globals {
		public static bool IsHostile(Entity ent) => IsValid(ent) && (ent.GetComponent<Positioned>()?.IsHostile ?? false);
	}

	public class Quality : Component<Offsets.Component_Quality> {
		public int ItemQuality => Cache.ItemQuality;
	}

	public class Render : Component<Offsets.Component_Render> {
		public Vector3 Position => Cache.Pos;
		public Vector3 Bounds => Cache.Bounds;
		public Vector3 Rotation => Cache.Rotation;

		public string Name => Cache.Name.Value;
	}
	public static partial class Globals {
		public static Vector3 Position(Entity ent) => !IsValid(ent) ? Vector3.Zero :
			ent.GetComponent<Render>()?.Position ?? Vector3.Zero;
		public static Vector2 GridPosition(Entity ent) => !IsValid(ent) ? Vector2.Zero :
			ent.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
	}

	public class RenderItem : Component<Offsets.Component_RenderItem> {
		public string ResourceName => PoEMemory.TryReadString(Cache.strResourceName, Encoding.Unicode, out string name)
			? name
			: null;
	}

	public class Shrine : Component<Offsets.Component_Shrine> {
		public bool IsAvailable => Cache.IsTaken == 0;
	}

	public class SkillGem : Component<Offsets.Component_SkillGem> {
		public Cached<Offsets.SkillGemDetails> Details;
		public SkillGem() : base() => Details = CachedStruct<Offsets.SkillGemDetails>(() => Address);

		public uint XP => Cache.TotalExpGained;
		public uint Level => Cache.Level;
		public uint MaxLevel => Details.Value.MaxLevel;

		public Offsets.SkillGemQualityType QualityType => Cache.QualityType;

		public uint SocketColor => Details.Value.SocketColor;
	}

	public class Sockets : Component<Offsets.Component_Sockets> {

	}

	public class Stack : Component<Offsets.Component_Stack> {
		public int CurSize => Cache.CurSize;
		public int MaxSize => Cache.MaxSize;
	}

	public class Stats : Component<Offsets.Component_Stats> {
		public Cached<Offsets.GameStatArray> GameStats;

		public Stats() : base() => GameStats = CachedStruct<Offsets.GameStatArray>(() => Cache.GameStats);

		private Dictionary<Offsets.GameStat, int> stats;
		private long lastStatsTime;
		private const long maxAge = 1000; // ms
		public Dictionary<Offsets.GameStat, int> GetStats() {
			if ( stats == null || (Time.ElapsedMilliseconds - lastStatsTime) > maxAge ) {
				stats = new Dictionary<Offsets.GameStat, int>();
				foreach ( var entry in Entries ) {
					stats[entry.Key] = entry.Value;
				}
			}
			return stats;
		}

		public IEnumerable<Offsets.GameStatArrayEntry> Entries =>
			new ArrayHandle<Offsets.GameStatArrayEntry>(GameStats.Value.Values);

	}

	public class Targetable : Component<Offsets.Component_Targetable> {
		public bool IsTargetable => Cache.IsTargetable;
		public bool IsTargeted => Cache.IsTargeted;
		public bool IsHighlightable => Cache.IsHighlightable;
	}
	public static partial class Globals {
		public static bool IsTargetable(Entity ent) => IsValid(ent) && (ent.GetComponent<Targetable>()?.IsTargetable ?? false);
	}


	public class TimerComponent : Component<Offsets.Component_TimerComponent> {
		public float TimeLeft => Cache.TimeLeft;
	}

	public class TriggerableBlockage : Component<Offsets.Component_TriggerableBlockage> {
		public bool IsOpen => Cache.IsClosed == 0;
		public Offsets.Vector2i GridMin => Cache.GridMin;
		public Offsets.Vector2i GridMax => Cache.GridMax;
	}

	public class Weapon : Component<Offsets.Component_Weapon> {
		public Cached<Offsets.WeaponDetails> Details;
		public Weapon() : base() => Details = CachedStruct<Offsets.WeaponDetails>(() => Cache.Details);

		public int DamageMin => Details.Value.DamageMin;
		public int DamageMax => Details.Value.DamageMax;
		public int AttackTime => Details.Value.AttackTime;
		public int CritChance => Details.Value.CritChance;
	}

	public class WorldItem : Component<Offsets.Component_WorldItem> {
		public Entity Item => IsValid(Address) && EntityCache.TryGetEntity(Cache.entItem, out Entity ent) ? ent : null;
	}

	public class Usable : Component<Offsets.Component_Empty> {

	}
	
}
