using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;


namespace AtE {
	public abstract class Component<T> : MemoryObject<T> where T : unmanaged {


	}

	public class Life : Component<Offsets.Component_Life> {

		public int MaxHP => Struct.MaxHP;
		public int MaxES => Struct.MaxES;
		public int MaxMana => Struct.MaxMana;

		public int CurHP => Struct.CurHP;
		public int CurES => Struct.CurES;
		public int CurMana => Struct.CurMana;

		public float CurManaRegen => Struct.ManaRegen;
		public float CurHPRegen => Struct.Regen;
		public int TotalReservedHP => Struct.ReservedFlatHP + (int)(1 + (Struct.MaxHP * (Struct.ReservedPercentHP / 10000d)));
		public int TotalReservedMana => Struct.ReservedFlatMana + (int)(1 + (Struct.MaxMana * (Struct.ReservedPercentMana / 10000d)));

	}

	public class Actor : Component<Offsets.Component_Actor> {

		public Offsets.Component_ActionFlags ActionFlags => Struct.ActionFlags;

		public int AnimationId => Struct.AnimationId;

		public ActorAction CurrentAction => Address == IntPtr.Zero || Struct.ptrAction == IntPtr.Zero ? null :
			new ActorAction() { Address = Struct.ptrAction };

		public IEnumerable<ActorSkill> Skills =>
			Struct.ActorSkillsHandle.GetItems<Offsets.ActorSkillArrayEntry>()
				.Select(x => new ActorSkill(this) { Address = x.ActorSkillPtr })
				.Where(x => x.IsValid());

		internal IEnumerable<Offsets.ActorVaalSkillArrayEntry> VaalSkills =>
			Struct.ActorVaalSkillsHandle.GetItems<Offsets.ActorVaalSkillArrayEntry>();

		public IEnumerable<DeployedObject> DeployedObjects =>
			Struct.DeployedObjectsHandle.GetItems<Offsets.DeployedObjectsArrayEntry>()
				.Select(x => new DeployedObject(this, x));

		internal IEnumerable<Offsets.ActorSkillUIState> ActorSkillUIStates =>
			Struct.ActorSkillUIStatesHandle.GetItems<Offsets.ActorSkillUIState>();

		internal bool IsOnCooldown(ushort skillId)  => ActorSkillUIStates
			.Where(s => s.SkillId == skillId)
			.Any(s =>
				(s.CooldownHigh - s.CooldownLow) >> 4 >= s.NumberOfUses
			);
	}

	public class ActorAction : MemoryObject {
		public Cached<Offsets.Component_Actor_Action> Cache;
		public ActorAction() => Cache = CachedStruct<Offsets.Component_Actor_Action>(this);

		public Entity Target => Address == IntPtr.Zero ? null :
			new Entity() { Address = Cache.Value.Target };

		public long Skill => Cache.Value.Skill;
		public Vector2 Destination => Cache.Value.Destination;
	}

	public class ActorSkill : MemoryObject, IDisposable {
		public Cached<Offsets.ActorSkill> Cache;
		public Cached<Offsets.GemEffects> GemEffects;
		public Cached<Offsets.SkillGem> SkillGem;
		public Cached<Offsets.ActiveSkill> ActiveSkill;

		public Actor Actor;
		public ActorSkill(Actor actor) => Actor = actor;

		public new IntPtr Address { get => base.Address;
			set {
				if ( value == base.Address ) return;

				var _actor = Actor;
				Dispose(); // the real Dispose makes it null, but we need it
				Actor = _actor;

				base.Address = value;
				if ( value == IntPtr.Zero ) return;

				Cache = CachedStruct<Offsets.ActorSkill>(this);
				GemEffects = CachedStruct<Offsets.GemEffects>(() => Cache.Value.ptrGemEffects);
				SkillGem = CachedStruct<Offsets.SkillGem>(() => GemEffects.Value.ptrSkillGem);
				ActiveSkill = CachedStruct<Offsets.ActiveSkill>(() => SkillGem.Value.ptrActiveSkill);

				// if this is a Vaal skill, it will have an entry in the Owner's VaalSkillsArray
				VaalEntry = new Cached<Offsets.ActorVaalSkillArrayEntry>(() => // default);
					Actor?.VaalSkills.Where(v => v.ptrActiveSkill == SkillGem.Value.ptrActiveSkill).FirstOrDefault() ?? default);

			}
		}

		private bool isDisposing = false;
		private bool isDisposed = false;
		public override void Dispose() {
			if ( isDisposed || isDisposing ) return;
			isDisposing = true;
			Actor = null;
			Cache?.Dispose();
			Cache = null;
			GemEffects?.Dispose();
			GemEffects = null;
			SkillGem?.Dispose();
			SkillGem = null;
			ActiveSkill?.Dispose();
			ActiveSkill = null;
			isDisposed = true;
			base.Dispose();
		}

		public bool IsValid() => SkillGem.Value.NamePtr != IntPtr.Zero;

		public ushort Id => Cache.Value.Id;

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

		public bool IsOnCooldown => GetPlayer().GetComponent<Actor>().IsOnCooldown(Cache.Value.Id);
		public bool CanBeUsed =>
			Cache.Value.CanBeUsed == 1
			&& Cache.Value.CanBeUsedWithWeapon == 1;

		// if there is an entry in the Actor VaalSkillsArray with the same
		// ptrActiveSkill as ours, cache that data here
		private Cached<Offsets.ActorVaalSkillArrayEntry> VaalEntry;
		public int CurVaalSouls => VaalEntry.Value.CurVaalSouls;
		public int MaxVaalSouls => VaalEntry.Value.MaxVaalSouls;
		public int SoulsPerUse => GemEffects.Value.VaalSoulsPerUse;
		public int SoulGainPreventionMS => GemEffects.Value.VaalSoulGainPreventionMS;


	}

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

		public Entity AnimatedObject =>
			PoEMemory.TryRead(Struct.ptrToAnimatedEntityPtr, out IntPtr addr)
			? new Entity() { Address = addr }
			: null;
	}


	public class AreaTransition : Component<Offsets.Component_AreaTransition> {
		public Offsets.AreaTransitionType TransitionType => Struct.TransitionType;
	}
	public static partial class Globals {
		public static bool IsDoor(Entity ent) => ent.HasComponent<AreaTransition>();
	}

	public class Armour : Component<Offsets.Component_Armour> {
		public Cached<Offsets.ArmourValues> ArmourValues;
		public Armour() : base() =>
			ArmourValues = CachedStruct<Offsets.ArmourValues>(() => Struct.ptrToArmourValues);
	}

	public class AttributeRequirements : Component<Offsets.Component_AttributeRequirements> {
		public Cached<Offsets.AttributeValues> AttributeValues;
		public AttributeRequirements() : base() =>
			AttributeValues = CachedStruct<Offsets.AttributeValues>(() => Struct.ptrToAttributeValues);

		public int Strength => AttributeValues.Value.Strength;
		public int Dexterity => AttributeValues.Value.Dexterity;
		public int Intelligence => AttributeValues.Value.Intelligence;
	}

	/// <summary>
	/// This refers to an item base (ie, something in your inventory).
	/// </summary>
	public class Base : Component<Offsets.Component_Base> {

		public Cached<Offsets.Component_Base_Info> Info;
		public Base() : base() => Info = CachedStruct<Offsets.Component_Base_Info>(() => Struct.ptrToBaseInfo);

		public byte CellSizeX => Info.Value.ItemCellSizeX;
		public byte CellSizeY => Info.Value.ItemCellSizeY;

		public string PublicPrice => PoEMemory.TryReadString(Struct.strPublicPrice, Encoding.Unicode, out string price)
			? price
			: null;

		public Offsets.InfluenceTypes Influences => Struct.Influences;

		public bool IsCorrupted => (Struct.IsCorrupted & 0x01) == 0x01;

	}

	public class Beam : Component<Offsets.Component_Beam> {
		public Vector3 Start => Struct.BeamStart;
		public Vector3 End => Struct.BeamEnd;
	}

	public class BlightTower : Component<Offsets.Component_BlightTower> {
		public Cached<Offsets.BlightDetails> Details;
		public BlightTower() : base() => Details = CachedStruct<Offsets.BlightDetails>(() => Struct.ptrToBlightDetails);

		public string Id => PoEMemory.TryReadString(Details.Value.strId, Encoding.Unicode, out string id) ? id : null;
		public string Name => PoEMemory.TryReadString(Details.Value.strName, Encoding.Unicode, out string name) ? name : null;
		public string Icon => PoEMemory.TryReadString(Details.Value.strIcon, Encoding.Unicode, out string icon) ? icon : null;

	}

	public class Buff : MemoryObject<Offsets.Buff> {

		public string Name =>
			PoEMemory.TryRead(Struct.ptrName, out IntPtr ptr)
			&& PoEMemory.TryReadString(ptr, Encoding.Unicode, out string name)
			? name
			: null;

		public byte Charges => Struct.Charges;
		public float Timer => Struct.Timer;
		public float MaxTime => Struct.MaxTime;
	}

	public class Buffs : MemoryObject<Offsets.Component_Buffs>, IEnumerable<Buff> {

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
		public IEnumerator<Buff> GetEnumerator() => Struct.Buffs.GetItems<IntPtr>()
			.Select(a => new Buff() { Address = a })
			.GetEnumerator();
	}


	public class Charges : Component<Offsets.Component_Charges> {
		public Cached<Offsets.ChargeDetails> Details;
		public Charges() : base() => Details = CachedStruct<Offsets.ChargeDetails>(() => Struct.ptrToChargeDetails);

		public int Current => Struct.NumCharges;
		public int Max => Details.Value.Max;
		public int PerUse => Details.Value.PerUse;
	}


	public class Chest : Component<Offsets.Component_Chest> {
		public Cached<Offsets.StrongboxDetails> Details;
		public Chest() : base() => Details = CachedStruct<Offsets.StrongboxDetails>(() => Struct.ptrToStrongboxDetails);

		public bool IsOpened => Struct.IsOpened;
		public bool IsLocked => Struct.IsLocked;
		public bool IsStrongbox => Struct.IsStrongbox;
		public byte Quality => Struct.Quality;
		public bool WillDestroyAfterOpen => Details.Value.WillDestroyAfterOpen;
		public bool IsLarge => Details.Value.IsLarge;
		public bool Stompable => Details.Value.Stompable;
		public bool OpenOnDamage => Details.Value.OpenOnDamage;
	}

	class CurrencyInfo : Component<Offsets.Component_CurrencyInfo> {

		public int MaxStackSize => Struct.MaxStackSize;
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
		public string Name => PoEMemory.TryReadString(Struct.ptrName, Encoding.Unicode, out string name) ? name : null;
		public string Texture => PoEMemory.TryReadString(Struct.ptrTexture, Encoding.ASCII, out string texture) ? texture : null;
		public string Model => PoEMemory.TryReadString(Struct.ptrModel, Encoding.ASCII, out string model) ? model : null;
	}
	public class Inventories : Component<Offsets.Component_Inventories> {
		public InventoryVisual LeftHand => new InventoryVisual() { Address = Struct.LeftHand };
		public InventoryVisual RightHand => new InventoryVisual() { Address = Struct.RightHand };
		public InventoryVisual Chest => new InventoryVisual() { Address = Struct.Chest };
		public InventoryVisual Helm => new InventoryVisual() { Address = Struct.Helm };
		public InventoryVisual Gloves => new InventoryVisual() { Address = Struct.Gloves };
		public InventoryVisual Boots => new InventoryVisual() { Address = Struct.Boots };
		public InventoryVisual Unknown => new InventoryVisual() { Address = Struct.Unknown };
		public InventoryVisual LeftRing => new InventoryVisual() { Address = Struct.LeftRing };
		public InventoryVisual RightRing => new InventoryVisual() { Address = Struct.RightRing };
		public InventoryVisual Belt => new InventoryVisual() { Address = Struct.Belt };
	}

}
