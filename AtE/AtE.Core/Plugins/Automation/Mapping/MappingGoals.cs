using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static AtE.Globals;

namespace AtE {

	// Goals and helpers the Mapping routine's loop uses (see MappingLoop.cs). None is sticky.

	/// <summary>Maps in the backpack and the stash.</summary>
	public static class ChooseNextMap {
		/// <summary>
		/// A map (an item, eg "Silo Map"; not a fragment or scarab). 3.30.0d, seen live in a Map stash tab: maps are map keys,
		/// Metadata/Items/Maps/MapKeyTier3 named "Map (Tier 3)", which do not say which area they open.
		/// </summary>
		public static bool IsMap(Entity item) => item?.Path is string path && path.StartsWith(Offsets.PATH_MAP_PREFIX)
			&& (path.StartsWith(Offsets.PATH_MAP_PREFIX + "MapKeyTier", StringComparison.Ordinal)
				|| (Equipment.ItemName(item)?.EndsWith(" Map", StringComparison.OrdinalIgnoreCase) ?? false));

		/// <summary>The area a map opens: its item name without " Map" (eg, "Silo Map": "Silo").</summary>
		public static string MapArea(Entity item) {
			var name = Equipment.ItemName(item);
			return name != null && name.EndsWith(" Map", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
		}
	}

	/// <summary>The hideout's Map Device and the portals it opens (see MappingRoutine.OpenMap).</summary>
	public static class OpenMap {
		/// <summary>The map portals opened since Activate was clicked in this visit to the hideout.</summary>
		public static IEnumerable<Entity> FreshPortals(MappingRoutine routine) =>
			routine.MapLaunched ? MapDevice.MapPortals().Where(p => !routine.StalePortals.Contains(p.Id)) : Enumerable.Empty<Entity>();

		// the Map Device: by its label, else by its path
		internal static Entity FindDevice(CombatContext ctx) {
			var label = GetUI()?.LabelsOnGround?.GetAllLabels()?
				.FirstOrDefault(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), "Map Device", StringComparison.OrdinalIgnoreCase));
			if ( IsValid(label?.Item) ) return label.Item;
			return GetEntities()?.Where(e => IsValid(e) && (e.Path?.IndexOf("MapDevice", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
					&& e.Path.IndexOf("Portal", StringComparison.OrdinalIgnoreCase) < 0)
				.Select(e => (e, pos: e.GetComponent<Positioned>())).Where(x => IsValid(x.pos))
				.OrderBy(x => Combat.Distance(ctx.Grid, x.pos.GridPos)).Select(x => x.e).FirstOrDefault();
		}
	}

	/// <summary>
	/// Fight and explore the map until fewer than MappingRoutine.MonstersLeft monsters remain (as the game shows it, see
	/// MapStats), or until it is all explored with no monsters near.
	/// </summary>
	public class ClearMap : Goal {
		private readonly MappingRoutine routine;
		public ClearMap(MappingRoutine routine) => this.routine = routine;
		public override string Name => routine.MonstersLeft > 0 ? $"Clear the map (until fewer than {routine.MonstersLeft} monsters remain)" : "Clear the map";
		public override bool Sticky => false;
		public override bool CheckDone(CombatContext ctx) => (routine.MonstersRemaining is int m && m < routine.MonstersLeft)
			|| (ctx.Driver.ExploreDone && !Fight.AnyNear(ctx, 0));
		protected override IState Step(CombatContext ctx) {
			Detail = routine.MonstersRemaining is int m ? $"{m} monsters remain" : "";
			return FightFirst(ctx) ?? Call(new Explore(null));
		}
	}

	/// <summary>
	/// Kill the map's boss (the default plan's: any unique monster, see MappingRoutine.IsMapBoss), staying out of what it
	/// puts on the ground (see AvoidSpam). Done once the map panel says "Map Complete" (see MapStats), or a unique is seen
	/// dead in this map, or the map is all explored with no unique alive in sight.
	/// </summary>
	public class KillMapBoss : AvoidSpam {
		public KillMapBoss(MappingRoutine routine) : base("the map boss", MappingRoutine.IsMapBoss, BuildPlan.MainSkill) {
			// (a unique killed while clearing, eg a Rogue Exile, does not show the map is cleared)
			Proves = false;
			// (a unique killed is not enough while a map boss is still alive in sight: Mud Geyser's map completed only after a
			// second RootSpiderHiddenMapBoss died, after Tunneltrap; one alive but burrowed is waited for, see AvoidSpam)
			DoneWhen = () => routine.MapComplete
				|| ((routine.UniquesKilled.Count > 0 || routine.ExploreDone)
					&& !(GetEntities()?.Any(e => MappingRoutine.IsMapBoss(e) && IsAlive(e)) ?? false));
		}
		public override bool Sticky => false;
	}

	/// <summary>Go to the hideout from a town (or anywhere else that is not a map): type /hideout in the chat.</summary>
	public class GoToHideout : Goal {
		private readonly MappingRoutine routine;
		public long RetryMs = 5000;
		private long lastSent;
		public GoToHideout(MappingRoutine routine) => this.routine = routine;
		public override string Name => "Go to the hideout";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); lastSent = 0; }
		public override bool CheckDone(CombatContext ctx) => routine.InHideout();
		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("going to the hideout");
			long now = Time.ElapsedMilliseconds;
			if ( now - lastSent < RetryMs ) return this;
			lastSent = now;
			Detail = "typing /hideout";
			return ChatCommand.Send("/hideout", this);
		}
	}

	/// <summary>
	/// What the game shows about the map, in the map panel's info lines, Map.GetChild(2, 0) (3.30, seen live in a Crater:
	/// [0] the area's name "Crater", [1] "Monster Level: 69", [3] the league, [4] "Free for All", [5] the realm, [6] "More
	/// than 50 monsters remain", [7] "Map Complete", hidden until the boss dies). Read by text, not by index, in case the
	/// lines move.
	/// TODO: confirm live: the wording once 50 or fewer remain (taken to be "N monsters remain")
	/// </summary>
	public static class MapStats {
		private static readonly Regex Remaining = new Regex(@"^(\d+)\s+monsters?\s+remains?", RegexOptions.IgnoreCase);

		/// <summary>The visible info lines (trimmed, not empty), or none if the panel can't be read.</summary>
		public static IEnumerable<string> Lines() {
			var info = GetUI()?.Map?.GetChild(2, 0);
			if ( !IsValid(info) ) return Enumerable.Empty<string>();
			return info.Children.Where(c => IsValid(c) && c.IsVisibleLocal).Select(c => c.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
		}

		/// <summary>The monsters remaining, from "N monsters remain"; null while it says "More than 50 monsters remain" (or nothing).</summary>
		public static int? MonstersRemaining() => MonstersRemaining(Lines());
		public static int? MonstersRemaining(IEnumerable<string> lines) {
			foreach ( var line in lines ) {
				var m = Remaining.Match(line);
				if ( m.Success && int.TryParse(m.Groups[1].Value, out int n) ) return n;
			}
			return null;
		}

		/// <summary>The panel shows "Map Complete" (the map's boss is dead).</summary>
		public static bool MapComplete() => MapComplete(Lines());
		public static bool MapComplete(IEnumerable<string> lines) => lines.Any(t => string.Equals(t, "Map Complete", StringComparison.OrdinalIgnoreCase));
	}
}
