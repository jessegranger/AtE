using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>What kind of slot an item goes in.</summary>
	public enum ItemKind { None, Helm, Amulet, Chest, Gloves, Belt, Boots, Ring, OneHand, TwoHand, OffHand }

	/// <summary>
	/// What item scoring needs to know about an item, read once from the game (see From) or made up (for tests): its
	/// kind, requirements, mods' stats (Stats.dat ids and values), base defences and weapon damage, and sockets.
	/// </summary>
	public sealed class ItemInfo {
		public string Name = "?";
		public string Path = "";
		public ItemKind Kind;
		public bool Identified = true;
		public int ReqLevel, ReqStr, ReqDex, ReqInt;
		/// <summary>Each mod's stats (implicit, explicit, enchant): Stats.dat id, value, and whether it is local (applies to this item only).</summary>
		public readonly List<(string stat, int value, bool local)> Stats = new List<(string, int, bool)>();
		/// <summary>The base type's defences (the middle of their range; before local mods).</summary>
		public double BaseArmour, BaseEvasion, BaseEnergyShield;
		/// <summary>The base type's physical damage, and attack time (0 if not a weapon).</summary>
		public double WeaponMin, WeaponMax, AttackTimeMs;
		public SocketItem Sockets;

		public override string ToString() => Name;

		/// <summary>The kind of slot for an item's metadata path, or None (gems, currency, flasks ...).</summary>
		public static ItemKind KindOf(string path) {
			if ( path == null ) return ItemKind.None;
			bool has(string s) => path.Contains(s, StringComparison.OrdinalIgnoreCase);
			if ( has("/Armours/BodyArmours/") ) return ItemKind.Chest;
			if ( has("/Armours/Helmets/") ) return ItemKind.Helm;
			if ( has("/Armours/Gloves/") ) return ItemKind.Gloves;
			if ( has("/Armours/Boots/") ) return ItemKind.Boots;
			if ( has("/Armours/Shields/") || has("/Quivers/") ) return ItemKind.OffHand;
			if ( has("/Belts/") ) return ItemKind.Belt;
			if ( has("/Amulets/") ) return ItemKind.Amulet;
			if ( has("/Rings/") ) return ItemKind.Ring;
			if ( has("/Weapons/OneHandWeapons/") ) return ItemKind.OneHand;
			if ( has("/Weapons/TwoHandWeapons/") ) return ItemKind.TwoHand;
			return ItemKind.None;
		}

		/// <summary>An item entity's info, or null if it is not something to wear.</summary>
		public static ItemInfo From(Entity e, string slot = null) {
			var kind = KindOf(e?.Path);
			if ( kind == ItemKind.None ) return null;
			var info = new ItemInfo { Name = Equipment.ItemName(e) ?? e.Path, Path = e.Path, Kind = kind };
			var mods = e.GetComponent<Mods>();
			if ( IsValid(mods) ) {
				info.Identified = mods.IsIdentified;
				info.ReqLevel = (int)mods.ReqLevel;
				foreach ( var mod in mods.ImplicitMods.Concat(mods.ExplicitMods).Concat(mods.EnchantedMods) ) info.Stats.AddRange(mod.Stats);
			}
			if ( e.GetComponent<AttributeRequirements>() is AttributeRequirements req && IsValid(req) ) {
				info.ReqStr = req.Strength; info.ReqDex = req.Dexterity; info.ReqInt = req.Intelligence;
			}
			if ( e.GetComponent<Armour>() is Armour armour && IsValid(armour) ) {
				var v = armour.ArmourValues.Value;
				info.BaseArmour = (v.ArmourMin + v.ArmourMax) / 2.0;
				info.BaseEvasion = (v.EvasionMin + v.EvasionMax) / 2.0;
				info.BaseEnergyShield = (v.ESMin + v.ESMax) / 2.0;
			}
			if ( e.GetComponent<Weapon>() is Weapon weapon && IsValid(weapon) ) {
				info.WeaponMin = weapon.DamageMin; info.WeaponMax = weapon.DamageMax; info.AttackTimeMs = weapon.AttackTime;
			}
			info.Sockets = SocketItem.From(slot ?? kind.ToString(), e);
			return info;
		}

		/// <summary>The sum of a stat's values over all its mods (local or not).</summary>
		public double Stat(string id) => Stats.Where(s => s.stat == id).Sum(s => (double)s.value);

		/// <summary>The sum of a stat's values over the mods that are not local.</summary>
		public double Global(string id) => Stats.Where(s => s.stat == id && !s.local).Sum(s => (double)s.value);

		/// <summary>The item's armour, after its local mods.</summary>
		public double Armour => Defence(BaseArmour, "local_base_physical_damage_reduction_rating", "local_physical_damage_reduction_rating_+%",
			"local_armour_and_evasion_+%", "local_armour_and_energy_shield_+%", "local_armour_and_evasion_and_energy_shield_+%");
		/// <summary>The item's evasion rating, after its local mods.</summary>
		public double Evasion => Defence(BaseEvasion, "local_base_evasion_rating", "local_evasion_rating_+%",
			"local_armour_and_evasion_+%", "local_evasion_and_energy_shield_+%", "local_armour_and_evasion_and_energy_shield_+%");
		/// <summary>The item's energy shield, after its local mods.</summary>
		public double EnergyShield => Defence(BaseEnergyShield, "local_energy_shield", "local_energy_shield_+%",
			"local_armour_and_energy_shield_+%", "local_evasion_and_energy_shield_+%", "local_armour_and_evasion_and_energy_shield_+%");

		// (base + local flat) * (1 + local increased)
		private double Defence(double baseValue, string flat, params string[] increased) {
			double total = baseValue + Stat(flat);
			if ( total <= 0 ) return 0;
			return total * (1 + increased.Sum(Stat) / 100.0);
		}

		/// <summary>The weapon's damage per hit (its physical after local mods, and its local added damage), or 0 if not a weapon.</summary>
		public double WeaponAverageHit {
			get {
				if ( AttackTimeMs <= 0 ) return 0;
				double phys = ((WeaponMin + WeaponMax) / 2 + (Stat("local_minimum_added_physical_damage") + Stat("local_maximum_added_physical_damage")) / 2)
					* (1 + Stat("local_physical_damage_+%") / 100);
				double added = new[] { "fire", "cold", "lightning", "chaos" }
					.Sum(el => (Stat($"local_minimum_added_{el}_damage") + Stat($"local_maximum_added_{el}_damage")) / 2);
				return phys + added;
			}
		}

		/// <summary>The weapon's attacks per second, after its local attack speed, or 0 if not a weapon.</summary>
		public double AttacksPerSecond => AttackTimeMs <= 0 ? 0 : 1000 / AttackTimeMs * (1 + Stat("local_attack_speed_+%") / 100);
	}
}
