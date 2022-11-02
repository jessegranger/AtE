﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {

	public static partial class Globals {
		public static Entity GetFlask(int index) => GetUI()?.Flasks?.GetFlask(index)?.Item;
		public static IEnumerable<FlaskEntity> GetFlasks() => GetUI()?.Flasks?.Flasks?.Select(f => f.Item) ?? Empty<FlaskEntity>();
	}
	public class FlaskElement : Element {
		public Cached<Offsets.Element_InventoryItem> Details;
		public FlaskElement() : base() => Details = CachedStruct<Offsets.Element_InventoryItem>(() => Address);
		public FlaskElement(int flaskIndex) : this() => FlaskIndex = flaskIndex;

		public int FlaskIndex;

		public FlaskEntity Item => new FlaskEntity(FlaskIndex) { Address = Details.Value.entItem };
	}

	/// <summary>
	/// FlaskEntity are Entities with an expected set of Components present:
	/// Charges
	/// Flask
	/// Quality
	/// Mods
	/// The Path should match one of the keys from FlaskData.
	/// </summary>
	public class FlaskEntity : Entity {
		public FlaskData BaseData;

		// bunch of fields that we have to parse from iterating ItemMods, so we only do it when Address changes
		public bool IsInstant;
		public bool IsInstantOnLowLife;
		public int Charges_Max; // the value in Charges component is unscaled by ItemMods like "Increased Charges"
		public int Charges_Cur;
		public int Charges_Per;
		public int Duration; // similarly, Duration is (base duration * quality) * ItemMods like "Increased Duration"
		public int LifeHealAmount;
		public int ManaHealAmount;
		// we have to iterate the mods anyway, so dont force everyone else to do it too
		public bool Enchanted_UseWhenFull;
		public bool Enchanted_UseWhenHitRare;
		public bool Effect_NotRemovedOnFullMana;
		public bool Cures_Poisoned;
		public bool Cures_Bleeding;
		public bool Cures_Frozen;
		public bool Cures_Ignited;
		public bool Cures_Shocked;
		public bool Cures_Curse;

		public int FlaskIndex;
		public Keys Key => (Keys.D1 + FlaskIndex);

		public FlaskEntity() : base() { }
		public FlaskEntity(int flaskIndex) : this() => FlaskIndex = flaskIndex;

		// Not the same as the flask itself being active
		// To find that, we would need to know the parent element, and find the little progress bar UI element
		// which is a sibling of the Element that emitted this instance
		public bool IsBuffActive => HasBuff(GetPlayer(), BaseData?.BuffName);

		public Mods Mods => GetComponent<Mods>();

		public new IntPtr Address { get => base.Address;
			set {
				if ( value == base.Address ) {
					return;
				}

				base.Address = value;

				if( value != IntPtr.Zero ) {
					float qualityFactor = (100 + GetComponent<Quality>().ItemQuality) / 100f;
					string path = Path.Split('/').Last();
					if( FlaskData.Records.TryGetValue(path, out BaseData) ) {
						Duration = (int)(BaseData.Duration * qualityFactor);
						LifeHealAmount = (int)(BaseData.HealAmount * qualityFactor);
						ManaHealAmount = (int)(BaseData.ManaAmount * qualityFactor);
					}
					var charges = GetComponent<Charges>();
					Charges_Max = charges.Max;
					Charges_Cur = charges.Current;
					Charges_Per = charges.PerUse;
					var mods = GetComponent<Mods>();
					foreach(var mod in mods.EnchantedMods ) {
						string groupName = mod.GroupName;
						if ( groupName.StartsWith("FlaskEnchantmentInjectorOnFullCharges") ) {
							Enchanted_UseWhenFull = true;
						} else if ( groupName.StartsWith("FlaskEnchantmentInjectorOnHittingRareOrUnique") ) {
							Enchanted_UseWhenHitRare = true;
						}
					}
					foreach ( var mod in mods.ExplicitMods ) {
						string groupName = mod.GroupName;
						if ( groupName.StartsWith("FlaskIncreasedDuration") ) {
							Duration = (int)(Duration * ((100 + mod.Values.First()) / 100f));
						} else if ( groupName.StartsWith("FlaskExtraCharges") ) {
							Charges_Max += mod.Values.First();
						} else if ( groupName.StartsWith("FlaskInstantRecoveryOnLowLife") ) {
							IsInstantOnLowLife = true;
							int lessRecovery = mod.Values.Skip(1).First(); // like -27
							float recoveryFactor = (100 + lessRecovery) / 100f;
							LifeHealAmount = (int)(LifeHealAmount * recoveryFactor);
						} else if ( groupName.StartsWith("FlaskFullInstantRecovery") || groupName.StartsWith("FlaskPartialInstantRecovery") ) {
							IsInstant = true;
							int lessRecovery = mod.Values.Skip(1).First(); // like -27
							float recoveryFactor = (100 + lessRecovery) / 100f;
							LifeHealAmount = (int)(LifeHealAmount * recoveryFactor);
						} else if ( groupName.StartsWith("FlaskPoisonImmunity") ) {
							Cures_Poisoned = true;
						} else if ( groupName.StartsWith("FlaskBleedCorruptingBloodImmunity") ) {
							Cures_Bleeding = true;
						} else if ( groupName.StartsWith("FlaskShockImmunity") ) {
							Cures_Shocked = true;
						} else if ( groupName.StartsWith("FlaskIgniteImmunity") ) {
							Cures_Ignited = true;
						} else if ( groupName.StartsWith("FlaskChillFreezeImmunity") ) {
							Cures_Frozen = true;
						} else if ( groupName.StartsWith("FlaskEffectNotRemovedOnFullMana") ) {
							Effect_NotRemovedOnFullMana = true;
							ManaHealAmount = (int)(ManaHealAmount * .70f);
							Duration = (int)(Duration * .70f);
						} else if ( groupName.StartsWith("FlaskCurseImmunity") ) {
							Cures_Curse = true;
						}

					}
				}
			}
		}

	}

	public class FlaskPanel : Element {

		private int[] flaskIndexToChildIndex = new int[] {
			1, 2, 3, 4, 5 // we have to scan Children to get the real value
		};

		public new IntPtr Address {
			get => base.Address;
			set {
				if ( value == base.Address ) return;
				base.Address = value;
				if ( value != IntPtr.Zero ) {
					updateFlaskIndex();
				}
			}
		}

		private void updateFlaskIndex() {
			int flaskChildIndex = 1; // the first child is a background/placeholder, so start at 1
			foreach ( var flaskChild in Children.First()?.Children.Skip(1) ?? Empty<Element>()) {
				int flaskIndex = (int)(flaskChild.Position.X / flaskChild.Size.X);
				flaskIndexToChildIndex[flaskIndex] = flaskChildIndex;
				flaskChildIndex += 1;
			}
		}

		public FlaskElement GetFlask(int flaskIndex) => new FlaskElement(flaskIndex) {
			Address = GetChild(0)?.GetChild(flaskIndexToChildIndex[flaskIndex])?.Address ?? IntPtr.Zero
		};

		public IEnumerable<FlaskElement> Flasks => Range(0, 5).Select(i => GetFlask((int)i));

	}

	/// <summary>
	/// Some data to make flasks useful is stored manually.
	/// Access to the game data files is spotty and complex.
	/// </summary>
	public class FlaskData {
		public string Path; // just the last chunk
		public int HealAmount;
		public int ManaAmount;
		public int Duration;
		public string BuffName; // the buff you gain when a flask of this type is in effect
		public FlaskData(string path, int heal, int mana, int dur, string buff) {
			Path = path;
			HealAmount = heal;
			ManaAmount = mana;
			Duration = dur;
			BuffName = buff;
		}
		public static readonly Dictionary<string, FlaskData> Records = new Dictionary<string, FlaskData>() {
			{  "FlaskLife1", new FlaskData("FlaskLife1", 70, 0, 3000, "flask_effect_life") }, // the Life durations are a bit off after 3.19
			{  "FlaskLife2", new FlaskData("FlaskLife2", 150, 0, 3000, "flask_effect_life") }, // but the buff names are the most useful part
			{  "FlaskLife3", new FlaskData("FlaskLife3", 250, 0, 3000, "flask_effect_life") },
			{  "FlaskLife4", new FlaskData("FlaskLife4", 360, 0, 3000, "flask_effect_life") },
			{  "FlaskLife5", new FlaskData("FlaskLife5", 640, 0, 3000, "flask_effect_life") },
			{  "FlaskLife6", new FlaskData("FlaskLife6", 830, 0, 3000, "flask_effect_life") },
			{  "FlaskLife7", new FlaskData("FlaskLife7", 1000, 0, 3000, "flask_effect_life") },
			{  "FlaskLife8", new FlaskData("FlaskLife8", 1200, 0, 3000, "flask_effect_life") },
			{  "FlaskLife9", new FlaskData("FlaskLife9", 1990, 0, 3000, "flask_effect_life") },
			{  "FlaskLife10", new FlaskData("FlaskLife10", 1460, 0, 3000, "flask_effect_life") },
			{  "FlaskLife11", new FlaskData("FlaskLife11", 2400, 0, 3000, "flask_effect_life") },
			{  "FlaskLife12", new FlaskData("FlaskLife12", 2080, 0, 3000, "flask_effect_life") },
			{  "FlaskMana1", new FlaskData("FlaskMana1", 0, 50, 4000, "flask_effect_mana") },
			{  "FlaskMana2", new FlaskData("FlaskMana2", 0, 70, 4000, "flask_effect_mana") },
			{  "FlaskMana3", new FlaskData("FlaskMana3", 0, 90, 4000, "flask_effect_mana") },
			{  "FlaskMana4", new FlaskData("FlaskMana4", 0, 120, 4000, "flask_effect_mana") },
			{  "FlaskMana5", new FlaskData("FlaskMana5", 0, 170, 4000, "flask_effect_mana") },
			{  "FlaskMana6", new FlaskData("FlaskMana6", 0, 250, 4500, "flask_effect_mana") },
			{  "FlaskMana7", new FlaskData("FlaskMana7", 0, 350, 5000, "flask_effect_mana") },
			{  "FlaskMana8", new FlaskData("FlaskMana8", 0, 480, 5500, "flask_effect_mana") },
			{  "FlaskMana9", new FlaskData("FlaskMana9", 0, 700, 6000, "flask_effect_mana") },
			{  "FlaskMana10", new FlaskData("FlaskMana10", 0, 1100, 6500, "flask_effect_mana") },
			{  "FlaskMana11", new FlaskData("FlaskMana11", 0, 1400, 6000, "flask_effect_mana") },
			{  "FlaskMana12", new FlaskData("FlaskMana12", 0, 1800, 4000, "flask_effect_mana") },
			{  "FlaskHybrid1", new FlaskData("FlaskHybrid1", 0, 0, 5000, "flask_effect_mana") },
			{  "FlaskHybrid2", new FlaskData("FlaskHybrid2", 0, 0, 5000, "flask_effect_mana") },
			{  "FlaskHybrid3", new FlaskData("FlaskHybrid3", 0, 0, 5000, "flask_effect_mana") },
			{  "FlaskHybrid4", new FlaskData("FlaskHybrid4", 0, 0, 5000, "flask_effect_mana") },
			{  "FlaskHybrid5", new FlaskData("FlaskHybrid5", 0, 0, 5000, "flask_effect_mana") },
			{  "FlaskHybrid6", new FlaskData("FlaskHybrid6", 0, 0, 5000, "flask_effect_mana") },
			{  "FlaskUtility1", new FlaskData("FlaskUtility1", 0, 0, 4000, "flask_utility_critical_strike_chance") },
			{  "FlaskUtility2", new FlaskData("FlaskUtility2", 0, 0, 4000, "flask_utility_resist_fire") },
			{  "FlaskUtility3", new FlaskData("FlaskUtility3", 0, 0, 4000, "flask_utility_resist_cold") },
			{  "FlaskUtility4", new FlaskData("FlaskUtility4", 0, 0, 4000, "flask_utility_resist_lightning") },
			{  "FlaskUtility5", new FlaskData("FlaskUtility5", 0, 0, 4000, "flask_utility_ironskin") },
			{  "FlaskUtility6", new FlaskData("FlaskUtility6", 0, 0, 4000, "flask_utility_sprint") },
			{  "FlaskUtility7", new FlaskData("FlaskUtility7", 0, 0, 3500, "flask_utility_resist_chaos") },
			{  "FlaskUtility8", new FlaskData("FlaskUtility8", 0, 0, 4000, "flask_utility_phase") },
			{  "FlaskUtility9", new FlaskData("FlaskUtility9", 0, 0, 4000, "flask_utility_evasion") },
			{  "FlaskUtility10", new FlaskData("FlaskUtility10", 0, 0, 4500, "flask_utility_stone") },
			{  "FlaskUtility11", new FlaskData("FlaskUtility11", 0, 0, 4000, "flask_utility_aquamarine") },
			{  "FlaskUtility12", new FlaskData("FlaskUtility12", 0, 0, 5000, "flask_utility_smoke") },
			{  "FlaskUtility13", new FlaskData("FlaskUtility13", 0, 0, 4000, "flask_utility_consecrate") },
			{  "FlaskUtility14", new FlaskData("FlaskUtility14", 0, 0, 5000, "flask_utility_haste") },
			{  "FlaskUtility15", new FlaskData("FlaskUtility15", 0, 0, 5000, "flask_utility_prismatic") },
			{  "FlaskUtility16", new FlaskData("FlaskUtility16", 0, 0, 4000, "flask_utility_rarity") },
			{  "FlaskUtility17", new FlaskData("FlaskUtility17", 0, 0, 4000, "flask_utility_stun_protection") },
		};
	}


}
