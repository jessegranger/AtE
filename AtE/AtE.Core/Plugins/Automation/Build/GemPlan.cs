using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The gems a build means to have, from its PoB skill sets (see PobSkillSet): at each character level, the socket
	/// groups of the skill set with the highest "Level N" title not above it. The first group is the main (damaging) one,
	/// the rest are utility, in priority order. Each group's first skill is bound to the key its label names.
	///
	/// Every gem in any of the sets is wanted (picked up when seen); the active set's gems not owned (in the backpack or
	/// socketed) are needed (bought when we can), in priority order.
	/// </summary>
	public sealed class GemPlan {
		public readonly PobBuild Build;
		/// <summary>The skill sets to follow, by level. A build without "Level N" titles follows the set selected in PoB, from level 1.</summary>
		public readonly List<PobSkillSet> Stages;

		public GemPlan(PobBuild build) {
			Build = build;
			Stages = build.SkillSets.Where(s => s.Level >= 0).OrderBy(s => s.Level).ToList();
			if ( Stages.Count == 0 && build.SkillSets.Count > 0 ) Stages.Add(build.SkillSets[Math.Clamp(build.ActiveSkillSet, 0, build.SkillSets.Count - 1)]);
		}

		/// <summary>The skill set for a character level: the last stage starting at or below it (the first, below them all).</summary>
		public PobSkillSet SetFor(int level) => Stages.LastOrDefault(s => s.Level <= level) ?? Stages.FirstOrDefault();

		/// <summary>The socket groups to have at a character level, main group first.</summary>
		public IEnumerable<PobSkillGroup> GroupsFor(int level) => SetFor(level)?.EnabledGroups ?? Enumerable.Empty<PobSkillGroup>();

		/// <summary>Every gem in any stage, once each.</summary>
		public IEnumerable<PobGem> AllGems => Stages.SelectMany(s => s.EnabledGroups).SelectMany(g => g.EnabledGems)
			.GroupBy(Identity).Select(g => g.First());

		/// <summary>Is the gem entity one the plan uses, at any stage (if not, leave it on the ground).</summary>
		public bool Wants(Entity gem) => AllGems.Any(g => Matches(g, gem));

		/// <summary>The gems of the groups for a character level that are not in the backpack or socketed, in priority order.</summary>
		public List<PobGem> Needed(int level) {
			var owned = Equipment.BackpackGemEntities().Concat(Equipment.SocketedGems()).ToList();
			return GroupsFor(level).SelectMany(g => g.EnabledGems)
				.GroupBy(Identity).Select(g => g.First())
				.Where(g => !owned.Any(e => Matches(g, e)))
				.ToList();
		}

		/// <summary>Is the entity this gem: by its metadata path, or (if that differs, eg a transfigured gem) by its item name.</summary>
		public static bool Matches(PobGem gem, Entity e) {
			if ( e?.Path is not string path ) return false;
			int at = path.IndexOf('@');
			if ( at >= 0 ) path = path.Substring(0, at);
			if ( gem.GemId != null && string.Equals(path, gem.GemId, StringComparison.OrdinalIgnoreCase) ) return true;
			return Equipment.IsGem(e, gem.ItemName);
		}

		/// <summary>A gem's colour: read from one we own (in the backpack or socketed), else from PoB's gem data; None if neither knows.</summary>
		public static SocketColor ColorOf(PobGem gem) {
			var e = Equipment.BackpackGemEntities().Concat(Equipment.SocketedGems()).FirstOrDefault(x => Matches(gem, x));
			var color = e?.GetComponent<SkillGem>()?.Color ?? SocketColor.None;
			return color != SocketColor.None ? color : PobGemData.ColorOf(gem);
		}

		/// <summary>The best arrangement of the groups for a character level in the worn items' sockets (see SocketSolver).</summary>
		public SocketArrangement SolveWorn(int level) => SocketSolver.Solve(SocketItem.Worn(), GroupsFor(level).ToList(), ColorOf);

		private static string Identity(PobGem g) => (g.GemId ?? g.Name).ToLowerInvariant();

		/// <summary>The skill bar slots in the game's order (see SkillSlot.Index): PoB's labels name them.</summary>
		public static readonly string[] SlotLabels = { "LMB", "MMB", "RMB", "Q", "W", "E", "R", "T" };

		/// <summary>The skill bar slot a group's label names (eg, "Q" is 3), or -1.</summary>
		public static int SlotOf(string label) =>
			Array.FindIndex(SlotLabels, l => string.Equals(l, label?.Trim(), StringComparison.OrdinalIgnoreCase));

		/// <summary>The character's level, or 0.</summary>
		public static int CharacterLevel() => GetPlayer()?.GetComponent<Player>()?.Level ?? 0;
	}
}
