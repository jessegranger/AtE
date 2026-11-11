using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>The character, as item scoring sees it (see ScoringContext.FromGame, or make one up for tests).</summary>
	public sealed class ScoringContext {
		public int Level = 1;
		public int Str, Dex, Int;
		/// <summary>The character's maximum life and energy shield (to turn "% increased" into points).</summary>
		public double Life = 50, EnergyShield;
		/// <summary>The resistances without any gear (the character's uncapped resistance, less what the worn items give).</summary>
		public readonly Dictionary<string, double> BaseResist = new Dictionary<string, double> { ["fire"] = 0, ["cold"] = 0, ["lightning"] = 0, ["chaos"] = 0 };
		/// <summary>The armour, evasion rating and block chance (%) without any gear (the character's, less what the worn items give).</summary>
		public double BaseArmour, BaseEvasion, BaseBlock;
		/// <summary>The socket groups to have (main first; see GemPlan).</summary>
		public IList<PobSkillGroup> Groups = new List<PobSkillGroup>();
		public Func<PobGem, SocketColor> ColorOf = _ => SocketColor.None;
		/// <summary>The main skill's tags (from PoB's gem data, eg "spell", "fire", "projectile").</summary>
		public HashSet<string> MainTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		/// <summary>Defences, most important first (see ItemScorer.DefenceWeights).</summary>
		public IList<string> DefencePriority = ItemScorer.DefaultDefencePriority;

		/// <summary>The character in the game, the build's groups for its level, and the worn items (to take out of the resistances).</summary>
		public static ScoringContext FromGame(GemPlan plan, IEnumerable<ItemInfo> worn, IList<string> defencePriority = null) {
			var ctx = new ScoringContext();
			var player = GetPlayer();
			var p = player?.GetComponent<Player>();
			if ( p != null ) { ctx.Level = Math.Max(1, (int)p.Level); ctx.Str = (int)p.Str; ctx.Dex = (int)p.Dex; ctx.Int = (int)p.Int; }
			var stats = player?.GetStats();
			int stat(Offsets.GameStat s) => stats != null && stats.TryGetValue(s, out int v) ? v : 0;
			ctx.Life = Math.Max(1, stat(Offsets.GameStat.MaximumLife));
			ctx.EnergyShield = stat(Offsets.GameStat.MaximumEnergyShield);
			var gear = ItemScorer.Resistances(worn);
			ctx.BaseResist["fire"] = stat(Offsets.GameStat.UncappedFireDamageResistancePct) - gear["fire"];
			ctx.BaseResist["cold"] = stat(Offsets.GameStat.UncappedColdDamageResistancePct) - gear["cold"];
			ctx.BaseResist["lightning"] = stat(Offsets.GameStat.UncappedLightningDamageResistancePct) - gear["lightning"];
			ctx.BaseResist["chaos"] = stat(Offsets.GameStat.UncappedChaosDamageResistancePct) - gear["chaos"];
			var wornItems = (worn ?? Enumerable.Empty<ItemInfo>()).Where(i => i != null).ToList();
			ctx.BaseArmour = Math.Max(0, stat(Offsets.GameStat.PhysicalDamageReductionRating) - ItemScorer.GearArmour(wornItems));
			ctx.BaseEvasion = Math.Max(0, stat(Offsets.GameStat.EvasionRating) - ItemScorer.GearEvasion(wornItems));
			ctx.BaseBlock = Math.Max(0, stat(Offsets.GameStat.AttackBlockPct) - ItemScorer.GearBlock(wornItems));
			if ( plan != null ) {
				ctx.Groups = plan.GroupsFor(ctx.Level).ToList();
				var main = ctx.Groups.FirstOrDefault()?.ActiveGems.FirstOrDefault();
				if ( PobGemData.Find(main)?.Tags is HashSet<string> tags ) ctx.MainTags = tags;
			}
			ctx.ColorOf = GemPlan.ColorOf;
			if ( defencePriority != null ) ctx.DefencePriority = defencePriority;
			return ctx;
		}
	}

	/// <summary>How a set of worn items scores, part by part.</summary>
	public sealed class LoadoutScore {
		public SocketArrangement Sockets;
		/// <summary>The main group's socket points, and the rest.</summary>
		public double Main, Utility;
		/// <summary>The main skill's damage, relative to a character of this level with nothing on (see ItemScorer.DamageMultiplier).</summary>
		public double Damage = 1;
		public double Defence;
		public readonly Dictionary<string, double> DefenceParts = new Dictionary<string, double>();
		public double Total => Main * Damage + Utility + Defence;
		public override string ToString() =>
			$"{Total:F0} = main {Main:F0} x damage {Damage:F2} + utility {Utility:F0} + defence {Defence:F0} ({string.Join(", ", DefenceParts.Where(d => Math.Abs(d.Value) >= 0.5).Select(d => $"{d.Key} {d.Value:F0}"))})";
	}

	/// <summary>
	/// Scores items: a set of worn items (a loadout) gets points for its sockets (see SocketSolver), the main skill's
	/// damage (multiplying the main group's socket points), and its defences. An item's score is what the loadout loses
	/// without it, so an empty slot scores 0; an item in the backpack or for sale scores as if it were worn in its slot
	/// (in place of what is there), and is better if the loadout's total goes up.
	///
	/// Damage, for a spell: (1 + added damage to spells / the skill's base damage at this level) x (1 + increased damage
	/// that matches the skill's tags, against an assumed AssumedIncreased% from elsewhere) x cast speed x LevelFactor for
	/// each "+1 to level of [tag] skill gems" that matches. For an attack: the weapon's damage per second (its local mods,
	/// plus added damage to attacks, increased damage and attack speed) against a reference for this level.
	///
	/// Defences, each as how much more damage the character can take (a percentage, like damage), times the weight of the
	/// defence's place in the priority (DefenceWeights), and PoolPointsPerPercent units for each 1% more:
	///   life and energy shield: more of the pool (life + energy shield), so they count for more while it is small;
	///   armour: less damage from a physical hit of this level's monsters (A / (A + 5 x hit));
	///   evasion: fewer attacks hitting, against this level's monster accuracy (1.25 x acc / (acc + (eva / 5) ^ 0.9));
	///   block: fewer attacks hitting (up to 75%).
	/// Each from the character's own (without gear) to that with the items: an item adds more where there is little.
	/// Resistances: 1 unit for each 1% (times the weight); below 75% counts double, beyond 105% half; chaos always half. Elemental resistance below 75% counts double, and beyond 105%
	/// half; chaos resistance always counts half.
	///
	/// Unidentified items are scored on what can be seen (their implicits, base and sockets); items the character cannot
	/// use (level or attributes) are not scored.
	/// </summary>
	public static class ItemScorer {
		public static readonly string[] DefaultDefencePriority = { "Resistance", "Block", "Energy Shield", "Life", "Armour", "Evasion" };
		public static double[] DefenceWeights = { 3, 2.5, 2, 1.5, 1, 0.5 };
		/// <summary>A monster's physical hit at character level 1, and how much it grows each level (for armour).</summary>
		public static double MonsterHitAt1 = 8, MonsterHitGrowth = 1.075;
		/// <summary>A monster's accuracy at character level 1, and how much it grows each level (for evasion).</summary>
		public static double MonsterAccuracyAt1 = 18, MonsterAccuracyGrowth = 1.07;
		public static double BlockCap = 75;
		/// <summary>Units for each 1% more life + energy shield (with the Life weight 1.5, 1% more life is 0.75 points; about what 1% more damage is).</summary>
		public static double PoolPointsPerPercent = 0.5;
		public static double ResistCap = 75, ResistOverCap = 105;
		/// <summary>Increased damage the character is assumed to have from elsewhere (the tree), so that an item's adds less.</summary>
		public static double AssumedIncreased = 50;
		/// <summary>A spell's average base damage at character level 1, and how much it grows each level.</summary>
		public static double SpellDamageAt1 = 10, SpellDamageGrowth = 1.07;
		/// <summary>The damage per second an attack is compared with at character level 1, and how much it grows each level.</summary>
		public static double AttackDpsAt1 = 10, AttackDpsGrowth = 1.06;
		/// <summary>Unarmed: average hit and attack time.</summary>
		public static double UnarmedHit = 4, UnarmedAttackTimeMs = 1200;
		/// <summary>Each matching "+1 to level of ... skill gems".</summary>
		public static double LevelFactor = 1.15;

		private static readonly string[] Elements = { "fire", "cold", "lightning" };
		private static readonly string[] DamageTypes = { "physical", "fire", "cold", "lightning", "chaos" };

		/// <summary>The worn slots (as Equipment.WornSlots names them) an item of a kind can go in.</summary>
		public static string[] SlotsFor(ItemKind kind) => kind switch {
			ItemKind.Helm => new[] { "Helm" },
			ItemKind.Amulet => new[] { "Amulet" },
			ItemKind.Chest => new[] { "Chest" },
			ItemKind.Gloves => new[] { "Gloves" },
			ItemKind.Belt => new[] { "Belt" },
			ItemKind.Boots => new[] { "Boots" },
			ItemKind.Ring => new[] { "LRing", "RRing" },
			ItemKind.OneHand => new[] { "LWeapon" },
			ItemKind.TwoHand => new[] { "LWeapon" },
			ItemKind.OffHand => new[] { "RWeapon" },
			_ => Array.Empty<string>(),
		};

		/// <summary>Can the character use the item (its level and attributes)? If not, why not.</summary>
		public static string Unusable(ScoringContext ctx, ItemInfo item) {
			if ( item.ReqLevel > ctx.Level ) return $"needs level {item.ReqLevel}";
			if ( item.ReqStr > ctx.Str ) return $"needs {item.ReqStr} Str";
			if ( item.ReqDex > ctx.Dex ) return $"needs {item.ReqDex} Dex";
			if ( item.ReqInt > ctx.Int ) return $"needs {item.ReqInt} Int";
			return null;
		}

		/// <summary>The score of a loadout (slot name to item; missing or null is empty).</summary>
		public static LoadoutScore Score(ScoringContext ctx, IReadOnlyDictionary<string, ItemInfo> loadout) {
			var items = loadout.Values.Where(i => i != null).ToList();
			var result = new LoadoutScore();
			result.Sockets = SocketSolver.Solve(items.Select(i => i.Sockets).Where(s => s != null), ctx.Groups, ctx.ColorOf);
			result.Main = result.Sockets.GroupScores.FirstOrDefault();
			result.Utility = result.Sockets.GroupScores.Skip(1).Sum();
			loadout.TryGetValue("LWeapon", out var weapon);
			result.Damage = DamageMultiplier(ctx, items, weapon);
			Defence(ctx, items, result);
			return result;
		}

		/// <summary>What the loadout loses without the item in slot: the item's score.</summary>
		public static double ItemScore(ScoringContext ctx, IReadOnlyDictionary<string, ItemInfo> loadout, string slot) {
			if ( !loadout.TryGetValue(slot, out var item) || item == null ) return 0;
			var without = loadout.ToDictionary(kv => kv.Key, kv => kv.Key == slot ? null : kv.Value);
			return Score(ctx, loadout).Total - Score(ctx, without).Total;
		}

		/// <summary>
		/// An item not worn: the best slot for it, its score there (as if worn), and how much the loadout's total would
		/// change (more than 0: it is better). A two handed weapon empties the off hand. An item the character cannot use
		/// (see Unusable) scores 0, and is no gain.
		/// </summary>
		public static (string slot, double score, double gain) Candidate(ScoringContext ctx, IReadOnlyDictionary<string, ItemInfo> loadout, ItemInfo item) {
			if ( Unusable(ctx, item) != null ) return (null, 0, 0);
			double current = Score(ctx, loadout).Total;
			(string slot, double score, double gain) best = (null, 0, double.NegativeInfinity);
			foreach ( var slot in SlotsFor(item.Kind) ) {
				var with = loadout.ToDictionary(kv => kv.Key, kv => kv.Value);
				with[slot] = item;
				if ( item.Kind == ItemKind.TwoHand ) with["RWeapon"] = null;
				// (an off hand with a two handed weapon worn takes its place)
				if ( item.Kind == ItemKind.OffHand && with.TryGetValue("LWeapon", out var main) && main?.Kind == ItemKind.TwoHand ) with["LWeapon"] = null;
				double total = Score(ctx, with).Total;
				double gain = total - current;
				if ( gain > best.gain ) best = (slot, ItemScore(ctx, with, slot), gain);
			}
			return best;
		}

		/// <summary>Each element's resistance from items (fire, cold, lightning, chaos).</summary>
		public static Dictionary<string, double> Resistances(IEnumerable<ItemInfo> items) {
			var res = new Dictionary<string, double> { ["fire"] = 0, ["cold"] = 0, ["lightning"] = 0, ["chaos"] = 0 };
			foreach ( var item in items ?? Enumerable.Empty<ItemInfo>() ) {
				if ( item == null ) continue;
				foreach ( var (stat, value, _) in item.Stats ) {
					foreach ( var el in ResistElements(stat) ) res[el] += value;
				}
			}
			return res;
		}

		// which resistances a stat adds to: eg, base_fire_damage_resistance_%, base_resist_all_elements_%, fire_and_cold_damage_resistance_%
		public static IEnumerable<string> ResistElements(string stat) {
			if ( !(stat.Contains("resist") && stat.EndsWith("%")) ) yield break;
			if ( stat.Contains("maximum") || stat.Contains("penetrat") || stat.Contains("enemy") || stat.Contains("minion") || stat.Contains("totem")
				|| stat.Contains("ignore") || stat.Contains("while") || stat.Contains("per_") ) yield break;
			var words = stat.Split('_');
			if ( words.Contains("elements") || words.Contains("elemental") ) {
				foreach ( var el in Elements ) yield return el;
				yield break;
			}
			foreach ( var el in Elements.Append("chaos") ) if ( words.Contains(el) ) yield return el;
		}

		/// <summary>The main skill's damage with these items, relative to the character with nothing on.</summary>
		public static double DamageMultiplier(ScoringContext ctx, IList<ItemInfo> items, ItemInfo weapon) {
			var tags = ctx.MainTags;
			if ( tags.Count == 0 ) return 1;
			double sum(Func<string, bool> match) => items.Sum(i => i.Stats.Where(s => !s.local && match(s.stat)).Sum(s => (double)s.value));
			double stat(string id) => sum(s => s == id);
			bool spell = tags.Contains("spell"), attack = tags.Contains("attack");

			// increased damage that applies: damage, spell / attack damage, its damage types (elemental for any element)
			double increased = stat("damage_+%");
			if ( spell ) increased += stat("spell_damage_+%");
			if ( attack ) increased += stat("attack_damage_+%") + (tags.Contains("melee") ? stat("melee_damage_+%") : 0);
			foreach ( var type in DamageTypes ) {
				if ( tags.Contains(type) || (attack && type == "physical") ) increased += stat($"{type}_damage_+%");
			}
			if ( Elements.Any(tags.Contains) ) increased += stat("elemental_damage_+%");
			double incFactor = (100 + AssumedIncreased + increased) / (100 + AssumedIncreased);

			// +levels
			double levels = 1;
			foreach ( var item in items ) {
				foreach ( var (s, value, local) in item.Stats ) {
					if ( !local && value > 0 && GemLevelApplies(s, tags) ) levels *= Math.Pow(LevelFactor, value);
				}
			}

			if ( spell ) {
				double added = AddedDamage(sum, "spell");
				double baseDamage = SpellDamageAt1 * Math.Pow(SpellDamageGrowth, ctx.Level - 1);
				double speed = 1 + sum(s => s.EndsWith("cast_speed_+%")) / 100;
				return (1 + added / baseDamage) * incFactor * speed * levels;
			}
			if ( attack ) {
				double hit = weapon != null && weapon.AttackTimeMs > 0 ? weapon.WeaponAverageHit : UnarmedHit;
				double aps = weapon != null && weapon.AttackTimeMs > 0 ? weapon.AttacksPerSecond : 1000 / UnarmedAttackTimeMs;
				double added = AddedDamage(sum, "attack");
				double speed = 1 + stat("attack_speed_+%") / 100;
				double dps = (hit + added) * aps * speed * incFactor * levels;
				return dps / (AttackDpsAt1 * Math.Pow(AttackDpsGrowth, ctx.Level - 1));
			}
			return incFactor * levels;
		}

		// the average added damage to spells or attacks: stats like spell_minimum_added_fire_damage and
		// spell_maximum_added_fire_damage, and ones for both ("Adds 1 to 3 Fire Damage to Spells and Attacks")
		private static double AddedDamage(Func<Func<string, bool>, double> sum, string kind) =>
			DamageTypes.Sum(t => sum(s => s.Contains(kind) && (s.EndsWith($"minimum_added_{t}_damage") || s.EndsWith($"maximum_added_{t}_damage")))) / 2;

		// "+1 to level of all fire spell skill gems" (fire_spell_skill_gem_level_+) applies to a skill with all its words as tags
		private static readonly HashSet<string> GemLevelWords = new HashSet<string> { "skill", "gem", "gems", "level", "+", "all", "global", "active", "" };
		public static bool GemLevelApplies(string stat, ICollection<string> tags) {
			if ( !stat.Contains("gem_level") || stat.Contains("local") || stat.Contains("support") || stat.Contains("socketed") ) return false;
			var words = stat.Replace("gem_level_+", "").Split('_').Where(w => !GemLevelWords.Contains(w)).ToList();
			return words.All(tags.Contains);
		}

		private static void Defence(ScoringContext ctx, IList<ItemInfo> items, LoadoutScore result) {
			double sum(Func<string, bool> match) => items.Sum(i => i.Stats.Where(s => !s.local && match(s.stat)).Sum(s => (double)s.value));
			double stat(string id) => sum(s => s == id);
			double weight(string defence) {
				int i = ctx.DefencePriority.IndexOf(defence);
				return i < 0 ? 0 : DefenceWeights[Math.Min(i, DefenceWeights.Length - 1)];
			}
			void add(string name, double points) { result.DefenceParts[name] = points; result.Defence += points; }

			// resistances: below the cap counts double, beyond 105% half; chaos always half
			var res = Resistances(items);
			double resPoints = 0;
			foreach ( var el in Elements ) resPoints += ResistValue(ctx.BaseResist[el], res[el]);
			resPoints += res["chaos"] * 0.5;
			add("resistance", resPoints * weight("Resistance"));

			// (how much more damage can be taken, as points: 1% more is PoolPointsPerPercent units)
			double points(double moreTaken) => (moreTaken - 1) * 100 * PoolPointsPerPercent;

			double blockOf(double b) => Math.Clamp(b, 0, BlockCap) / 100;
			add("block", points((1 - blockOf(ctx.BaseBlock)) / (1 - blockOf(ctx.BaseBlock + GearBlock(items)))) * weight("Block"));

			// life: flat, % of the character's life, and half of the strength
			double strength = sum(s => s.StartsWith("additional_") && (s.Contains("strength") || s.Contains("all_attributes")));
			double life = stat("base_maximum_life") + stat("maximum_life_+%") * ctx.Life / 100 + strength / 2;
			double pool = Math.Max(1, ctx.Life + ctx.EnergyShield);
			add("life", life / pool * 100 * PoolPointsPerPercent * weight("Life"));

			double es = items.Sum(i => i.EnergyShield) + stat("base_maximum_energy_shield") + stat("maximum_energy_shield_+%") * ctx.EnergyShield / 100;
			add("energy shield", es / pool * 100 * PoolPointsPerPercent * weight("Energy Shield"));
			double hit = MonsterHitAt1 * Math.Pow(MonsterHitGrowth, ctx.Level - 1);
			double taken(double armour) => 1 - armour / (armour + 5 * hit);
			add("armour", points(taken(ctx.BaseArmour) / taken(ctx.BaseArmour + GearArmour(items))) * weight("Armour"));

			double accuracy = MonsterAccuracyAt1 * Math.Pow(MonsterAccuracyGrowth, ctx.Level - 1);
			double hitChance(double evasion) => Math.Clamp(1.25 * accuracy / (accuracy + Math.Pow(evasion / 5, 0.9)), 0.05, 1);
			add("evasion", points(hitChance(ctx.BaseEvasion) / hitChance(ctx.BaseEvasion + GearEvasion(items))) * weight("Evasion"));
		}

		private static double Global(IEnumerable<ItemInfo> items, Func<string, bool> match) =>
			items.Where(i => i != null).Sum(i => i.Stats.Where(s => !s.local && match(s.stat)).Sum(s => (double)s.value));

		/// <summary>The armour items give (their own, after local mods, and global flat armour).</summary>
		public static double GearArmour(IEnumerable<ItemInfo> items) =>
			items.Where(i => i != null).Sum(i => i.Armour) + Global(items, s => s == "base_physical_damage_reduction_rating");

		/// <summary>The evasion rating items give (their own, after local mods, and global flat evasion).</summary>
		public static double GearEvasion(IEnumerable<ItemInfo> items) =>
			items.Where(i => i != null).Sum(i => i.Evasion) + Global(items, s => s == "base_evasion_rating");

		/// <summary>The chance to block attacks (%) items give (a shield's local block too).</summary>
		public static double GearBlock(IEnumerable<ItemInfo> items) =>
			items.Where(i => i != null).Sum(i => i.Stats.Where(s => s.stat.Contains("block") && s.stat.EndsWith("%")
				&& !s.stat.Contains("spell") && !s.stat.Contains("enemy") && !s.stat.Contains("maximum") && !s.stat.Contains("per_")).Sum(s => (double)s.value));

		/// <summary>The points for adding gear resistance to a base resistance: double below the cap, single to 105%, half beyond.</summary>
		public static double ResistValue(double baseResist, double gear) {
			double lo = baseResist, hi = baseResist + gear;
			double sign = 1;
			if ( hi < lo ) { (lo, hi) = (hi, lo); sign = -1; }
			double part(double a, double b) => Math.Max(0, Math.Min(hi, b) - Math.Max(lo, a));
			return sign * (2 * part(double.NegativeInfinity, ResistCap) + part(ResistCap, ResistOverCap) + 0.5 * part(ResistOverCap, double.PositiveInfinity));
		}
	}
}
