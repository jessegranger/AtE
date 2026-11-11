using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AtE.Tests {

	/// <summary>The ItemScorer on made-up items (stat ids as the game's mods have them, see /items).</summary>
	public class ItemScorerTests {

		private static PobSkillGroup Fireball() {
			var g = new PobSkillGroup { Label = "Q", Enabled = true };
			g.Gems.Add(new PobGem { Name = "Fireball", SkillId = "Fireball", Enabled = true });
			g.Gems.Add(new PobGem { Name = "Arcane Surge", SkillId = "SupportArcaneSurge", Enabled = true });
			return g;
		}

		private static ScoringContext Witch(int level = 10) => new ScoringContext {
			Level = level, Str = 20, Dex = 20, Int = 40, Life = 200,
			Groups = new List<PobSkillGroup> { Fireball() },
			MainTags = new HashSet<string> { "spell", "fire", "projectile", "area", "intelligence" },
		};

		private static ItemInfo Item(ItemKind kind, string sockets, params (string stat, int value)[] stats) {
			var item = new ItemInfo { Name = kind.ToString(), Kind = kind, Sockets = sockets == null ? null : new SocketItem(kind.ToString(), sockets) };
			foreach ( var (stat, value) in stats ) item.Stats.Add((stat, value, stat.StartsWith("local_")));
			return item;
		}

		private static Dictionary<string, ItemInfo> Loadout(params (string slot, ItemInfo item)[] items) => items.ToDictionary(i => i.slot, i => i.item);

		[Fact]
		public void An_empty_slot_scores_nothing_and_an_item_scores_what_it_adds() {
			var ctx = Witch();
			var wand = Item(ItemKind.OneHand, "B-B", ("spell_damage_+%", 10));
			var loadout = Loadout(("LWeapon", wand), ("Boots", null));
			Assert.Equal(0, ItemScorer.ItemScore(ctx, loadout, "Boots"));
			// the wand holds the main skill: without it, nothing of the main group is left
			Assert.Equal(ItemScorer.Score(ctx, loadout).Total, ItemScorer.ItemScore(ctx, loadout, "LWeapon"), 6);
		}

		[Fact]
		public void Matching_damage_counts_and_other_damage_does_not() {
			var ctx = Witch();
			var wand = Item(ItemKind.OneHand, "B-B");
			double plain = ItemScorer.Score(ctx, Loadout(("LWeapon", wand))).Total;
			var spellRing = Item(ItemKind.Ring, null, ("fire_damage_+%", 20));
			var coldRing = Item(ItemKind.Ring, null, ("cold_damage_+%", 20));
			var attackRing = Item(ItemKind.Ring, null, ("attack_minimum_added_physical_damage", 1), ("attack_maximum_added_physical_damage", 4));
			Assert.True(ItemScorer.Score(ctx, Loadout(("LWeapon", wand), ("LRing", spellRing))).Total > plain);
			Assert.Equal(plain, ItemScorer.Score(ctx, Loadout(("LWeapon", wand), ("LRing", coldRing))).Total, 6);
			Assert.Equal(plain, ItemScorer.Score(ctx, Loadout(("LWeapon", wand), ("LRing", attackRing))).Total, 6);
		}

		[Fact]
		public void Gem_levels_apply_only_with_matching_tags() {
			var fire = new HashSet<string> { "spell", "fire" };
			Assert.True(ItemScorer.GemLevelApplies("fire_spell_skill_gem_level_+", fire));
			Assert.True(ItemScorer.GemLevelApplies("spell_skill_gem_level_+", fire));
			Assert.False(ItemScorer.GemLevelApplies("cold_spell_skill_gem_level_+", fire));
			Assert.False(ItemScorer.GemLevelApplies("local_socketed_gem_level_+", fire));
		}

		[Fact]
		public void Resistance_counts_double_below_the_cap_and_half_beyond_105() {
			Assert.Equal(20, ItemScorer.ResistValue(0, 10));
			Assert.Equal(10 + 5, ItemScorer.ResistValue(70, 10)); // 5 below 75 (double), 5 above
			Assert.Equal(5, ItemScorer.ResistValue(110, 10));
			Assert.Equal(-20, ItemScorer.ResistValue(0, -10));
			Assert.Equal(new[] { "fire", "cold", "lightning" }, ItemScorer.ResistElements("base_resist_all_elements_%"));
			Assert.Equal(new[] { "fire", "cold" }, ItemScorer.ResistElements("fire_and_cold_damage_resistance_%"));
			Assert.Empty(ItemScorer.ResistElements("base_maximum_fire_damage_resistance_%"));
		}

		[Fact]
		public void A_better_item_in_the_backpack_is_a_gain() {
			var ctx = Witch();
			var loadout = Loadout(("LWeapon", Item(ItemKind.OneHand, "B-B")), ("LRing", null), ("RRing", null), ("Boots", Item(ItemKind.Boots, null, ("base_maximum_life", 10))));
			var boots = Item(ItemKind.Boots, null, ("base_maximum_life", 30), ("base_cold_damage_resistance_%", 12));
			var (slot, score, gain) = ItemScorer.Candidate(ctx, loadout, boots);
			Assert.Equal("Boots", slot);
			Assert.True(gain > 0);
			Assert.True(score > ItemScorer.ItemScore(ctx, loadout, "Boots"));
			var worse = Item(ItemKind.Boots, null, ("base_maximum_life", 5));
			Assert.True(ItemScorer.Candidate(ctx, loadout, worse).gain < 0);
		}

		[Fact]
		public void Local_defences_and_weapon_damage_use_local_mods() {
			var shoes = new ItemInfo { Kind = ItemKind.Boots, BaseEnergyShield = 6 };
			shoes.Stats.Add(("local_energy_shield_+%", 50, true));
			Assert.Equal(9, shoes.EnergyShield, 6);
			var sword = new ItemInfo { Kind = ItemKind.OneHand, WeaponMin = 4, WeaponMax = 8, AttackTimeMs = 500 };
			sword.Stats.Add(("local_physical_damage_+%", 50, true));
			sword.Stats.Add(("local_minimum_added_fire_damage", 2, true));
			sword.Stats.Add(("local_maximum_added_fire_damage", 4, true));
			Assert.Equal(6 * 1.5 + 3, sword.WeaponAverageHit, 6);
			Assert.Equal(2, sword.AttacksPerSecond, 6);
		}

		[Fact]
		public void Defences_count_for_more_where_there_is_little() {
			// (evasion does little until it is a fair part of the monsters' accuracy: 300 at level 10 is enough to count)
			var chest = new ItemInfo { Name = "chest", Kind = ItemKind.Chest, BaseArmour = 50, BaseEvasion = 300 };
			chest.Stats.Add(("local_additional_block_chance_%", 5, true));
			double score(double baseArmour, double baseEvasion, double baseBlock) {
				var ctx = Witch();
				ctx.DefencePriority = new[] { "Armour", "Evasion", "Block" };
				ctx.BaseArmour = baseArmour; ctx.BaseEvasion = baseEvasion; ctx.BaseBlock = baseBlock;
				return ItemScorer.ItemScore(ctx, Loadout(("Chest", chest)), "Chest");
			}
			Assert.True(score(0, 0, 0) > 0);
			Assert.True(score(0, 0, 0) > score(500, 0, 0));
			Assert.True(score(0, 100, 0) > score(0, 2000, 0));
			// block compounds (50% to 55% takes 10% fewer hits, 0% to 5% only 5%), but nothing beyond the 75% cap
			Assert.True(score(0, 0, 50) > score(0, 0, 0));
			Assert.True(score(0, 0, 75) < score(0, 0, 0));
		}

		[Fact]
		public void Items_above_the_characters_level_or_attributes_are_unusable() {
			var ctx = Witch(level: 5);
			Assert.NotNull(ItemScorer.Unusable(ctx, new ItemInfo { ReqLevel = 8 }));
			Assert.NotNull(ItemScorer.Unusable(ctx, new ItemInfo { ReqStr = 50 }));
			Assert.Null(ItemScorer.Unusable(ctx, new ItemInfo { ReqLevel = 5, ReqInt = 40 }));
			// and scores 0, however good it is
			var wand = Item(ItemKind.OneHand, "B-B-B", ("spell_damage_+%", 50));
			wand.ReqLevel = 6;
			var (_, score, gain) = ItemScorer.Candidate(ctx, Loadout(("LWeapon", null)), wand);
			Assert.Equal(0, score);
			Assert.Equal(0, gain);
		}

		[Fact]
		public void Added_damage_to_spells_and_attacks_counts_for_a_spell() {
			var ctx = Witch();
			var wand = Item(ItemKind.OneHand, "B-B");
			var both = Item(ItemKind.Ring, null, ("spell_and_attack_minimum_added_fire_damage", 1), ("spell_and_attack_maximum_added_fire_damage", 3));
			Assert.True(ItemScorer.Score(ctx, Loadout(("LWeapon", wand), ("LRing", both))).Total > ItemScorer.Score(ctx, Loadout(("LWeapon", wand))).Total);
		}
	}
}
