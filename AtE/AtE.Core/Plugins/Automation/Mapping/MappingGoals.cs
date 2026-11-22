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
		/// <summary>Explore it all (or until Map Complete), however few monsters remain (a later round's closer pass, see ClearRound).</summary>
		public bool Sweep;
		public override bool CheckDone(CombatContext ctx) => Sweep
			? routine.MapComplete || (ctx.Driver.ExploreDone && !Fight.AnyNear(ctx, 0))
			: (routine.MonstersRemaining is int m && m < routine.MonstersLeft) || (ctx.Driver.ExploreDone && !Fight.AnyNear(ctx, 0));
		protected override IState Step(CombatContext ctx) {
			Detail = routine.MonstersRemaining is int m ? $"{m} monsters remain" : "";
			routine.NoteBoss(ctx);
			return FightFirst(ctx) ?? WaitForHarvest(ctx) ?? GoToIcon(ctx) ?? Call(new Explore(null));
		}

		/// <summary>Wait this long at most for one growing Harvest seed (or a started tree with none yet) to hatch.</summary>
		public long HarvestWaitMs = 20000;
		/// <summary>Only a started Harvest tree this near (cells) is waited at.</summary>
		public float HarvestWaitReach = 60;
		// when each growing thing (a tree's id, a seed's id) was first seen growing, so one that never hatches is not waited on for good
		private readonly Dictionary<uint, long> harvestGrowingSince = new Dictionary<uint, long>();
		private bool harvestWaiting;

		/// <summary>
		/// After Interactables clicks a Harvest tree its seeds grow a while before their monsters come, in waves: don't
		/// explore away meanwhile, wait by it (fighting what has come: FightFirst runs before this). Seen live (3.30):
		///   the tree HarvestEncounterTreePurple: state active = 0 (targetable) before the click, 1 (not targetable) while
		///     its first seeds grow, 2 once monsters are out (the encounter running, more waves still coming)
		///   each Metadata/MiscellaneousObjects/Harvest/MonsterSeed: hatched = 1 while it grows, 2 once its monster is out
		///     (withered = 0 all along, colour = 1 on a red tree); 4 seeds at first, 29 a few waves later
		///   the monsters: Metadata/Monsters/LeagueHarvest/Red/... (Wild Thornwolf, Wild Ursaling, ...), with invisible
		///     helpers Harvest*Daemon and LeagueHarvest/Red/Objects/HarvestRedExplodingPod
		/// Waits while a started tree is near and it is still at active = 1 or any seed is still growing (hatched = 1, not
		/// withered), each for HarvestWaitMs at most from when it was first seen so.
		/// </summary>
		private IState WaitForHarvest(CombatContext ctx) {
			var ents = GetEntities();
			if ( ents == null ) return null;
			long now = Time.ElapsedMilliseconds;
			bool treeNear = false;
			var growing = new List<uint>();
			foreach ( var e in ents ) {
				if ( !IsValid(e) || e.Path == null ) continue;
				if ( e.Path.Contains("Leagues/Harvest/Objects/HarvestEncounterTree") ) {
					if ( !(e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("active", out long active) && active != 0) ) continue;
					if ( !(e.GetComponent<Positioned>() is Positioned pos && IsValid(pos) && Combat.Distance(ctx.Grid, pos.GridPos) <= HarvestWaitReach) ) continue;
					treeNear = true;
					if ( active == 1 ) growing.Add(e.Id);
				} else if ( e.Path.EndsWith("/Harvest/MonsterSeed", StringComparison.Ordinal) ) {
					if ( e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm)
						&& sm.TryGetState("hatched", out long hatched) && hatched < 2
						&& !(sm.TryGetState("withered", out long withered) && withered != 0) )
						growing.Add(e.Id);
				}
			}
			int waiting = 0;
			if ( treeNear ) foreach ( uint id in growing ) {
				if ( !harvestGrowingSince.TryGetValue(id, out long since) ) harvestGrowingSince[id] = since = now;
				if ( now - since <= HarvestWaitMs ) waiting++;
			}
			if ( waiting == 0 ) {
				if ( harvestWaiting ) { harvestWaiting = false; Log($"{routine.Name}: done waiting at the Harvest tree"); }
				if ( !treeNear ) harvestGrowingSince.Clear();
				return null;
			}
			if ( !harvestWaiting ) { harvestWaiting = true; Log($"{routine.Name}: the Harvest tree is started, waiting for its monsters"); }
			Detail = $"waiting for the Harvest monsters ({waiting} growing)";
			ctx.Driver.StopWalking(Detail);
			return this;
		}

		/// <summary>Minimap icons (MinimapIcons.dat ids) of a boss that is not in the entity list until the player is near.</summary>
		public static readonly string[] BossIcons = { "AuguryBossObject" };
		/// <summary>
		/// Minimap icons of things worth going to in every round, before exploring further: the Augury cards' encounters
		/// ("Select a Fate": its AuguryCardObject is clicked by Interactables once near). Seen live (3.30.0d, Mud Geyser):
		/// two AuguryEncounter icons, their entities (ids 1353, 1318) out of the entity list's range, so they were skipped
		/// unless the explorer happened to pass near.
		/// </summary>
		public static readonly string[] EncounterIcons = { "AuguryEncounter" };

		/// <summary>Near enough to an icon's place (its entity is in range, and fought or clicked as any other) to count it as visited.</summary>
		public float BossIconReach = 25;
		/// <summary>Time to get to an icon's place before giving up on it.</summary>
		public long BossIconMs = 30000;
		private uint iconGoing;
		private long iconSince;

		// walk to each icon's place not visited yet, nearest first, fighting on the way (FightFirst comes before this; and
		// the explorer marks what is passed as explored): the EncounterIcons in every round, and in the sweep (a later
		// round) the BossIcons too (eg, a frozen augury boss, far off the entity list, that the explorer counted as seen)
		private IState GoToIcon(CombatContext ctx) {
			var icon = MinimapIcons.All()
				.Where(i => (System.Array.IndexOf(EncounterIcons, i.Icon) >= 0 || (Sweep && System.Array.IndexOf(BossIcons, i.Icon) >= 0))
					&& !routine.IconDone(i))
				.OrderBy(i => System.Numerics.Vector2.Distance(i.GridPos, new System.Numerics.Vector2(ctx.Grid.X, ctx.Grid.Y)))
				.FirstOrDefault();
			if ( icon == null ) return null;
			var grid = new Offsets.Vector2i((int)icon.GridPos.X, (int)icon.GridPos.Y);
			float d = Combat.Distance(ctx.Grid, grid);
			long now = Time.ElapsedMilliseconds;
			if ( iconGoing != icon.EntityId ) { iconGoing = icon.EntityId; iconSince = now; Log($"{routine.Name}: going to the {icon.Icon} icon at {grid} ({d:F0} cells away)"); }
			if ( d <= BossIconReach || now - iconSince > BossIconMs ) {
				if ( d > BossIconReach ) Log($"{routine.Name}: could not get to the {icon.Icon} icon at {grid} in {BossIconMs} ms ({d:F0} cells away): giving up on it");
				routine.MarkIconDone(icon);
				return null;
			}
			var spot = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, grid, out var snapped) ? snapped : grid;
			Detail = $"going to the {icon.Icon} icon, {d:F0} cells away";
			ctx.Driver.WalkTo(spot, Detail);
			return this;
		}
	}

	/// <summary>
	/// Kill the map's boss (the default plan's: any unique monster, see MappingRoutine.IsMapBoss), staying out of what it
	/// puts on the ground (see AvoidSpam). Done once the map panel says "Map Complete" (see MapStats), or a unique is seen
	/// dead in this map, or the map is all explored with no unique alive in sight.
	/// </summary>
	public class KillMapBoss : AvoidSpam {
		public KillMapBoss(MappingRoutine routine) : base("the map boss", MappingRoutine.IsMapBoss, BuildPlan.MainSkill) {
			// (not in sight: to where it was seen in this map, if it was, see MappingRoutine.BossSeenAt; else explore)
			SearchAt = ctx => { routine.NoteBoss(ctx); return routine.BossSeenAt; };
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

	/// <summary>
	/// Take Count fates (see MapRules.FatesFirst): walk to each Augury encounter's minimap icon (ClearMap.EncounterIcons),
	/// nearest first, fighting on the way (the map boss left alone meanwhile: MappingRoutine.HoldBoss), where Interactables
	/// clicks its "Select a Fate" and TryAugury chooses a card; with none known, explore for more (the explorer marks what is
	/// passed as explored, and where the boss is seen is remembered, see MappingRoutine.NoteBoss). Done once Count fates
	/// are taken in this area, or the map is explored with no encounter left to go to (then the boss is fought anyway).
	/// </summary>
	public class TakeFates : Goal {
		private readonly MappingRoutine routine;
		public readonly int Count;
		/// <summary>Near enough to an encounter's icon to count it as visited (its card is clicked by Interactables from there).</summary>
		public float Reach = 15;
		/// <summary>After reaching an icon, how long to wait there for its window and the fate (before going on).</summary>
		public long AtIconMs = 6000;
		/// <summary>Time to get to an icon before giving up on it.</summary>
		public long IconMs = 30000;
		private uint going;
		private long since, arrivedAt = -1;
		private int fatesAtArrival;
		public TakeFates(MappingRoutine routine, int count) { this.routine = routine; Count = count; }
		public override string Name => $"Take {Count} fates before the boss ({routine.FatesHere} taken)";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); going = 0; arrivedAt = -1; }

		private static IEnumerable<MinimapIconMarker> Encounters() =>
			MinimapIcons.All().Where(i => System.Array.IndexOf(ClearMap.EncounterIcons, i.Icon) >= 0);

		private bool loggedShort;
		public override bool CheckDone(CombatContext ctx) {
			bool done = routine.FatesHere >= Count
				|| (ctx.Driver.ExploreDone && !Encounters().Any(i => !routine.IconDone(i)) && !Fight.AnyNear(ctx, 0));
			// (the map boss is up and can be hurt, not frozen waiting for its cards: the fates are moot now, it is fought)
			if ( !done && GetEntities()?.FirstOrDefault(e => MappingRoutine.IsMapBoss(e) && IsAlive(e) && !Combat.IsFrozenAuguryBoss(e)) is Entity boss ) {
				done = true;
				if ( !loggedShort ) { loggedShort = true; Log($"{routine.Name}: the map boss is up, not frozen ({routine.FatesHere} of {Count} fates taken): fighting it"); }
			}
			routine.HoldBoss = !done;
			if ( done && routine.FatesHere < Count && !loggedShort ) {
				loggedShort = true;
				Log($"{routine.Name}: {routine.FatesHere} of {Count} fates taken, and no Augury encounter left to find: going to the boss");
			}
			return done;
		}

		protected override IState Step(CombatContext ctx) {
			routine.HoldBoss = true;
			routine.NoteBoss(ctx);
			Detail = $"{routine.FatesHere} of {Count} taken";
			if ( FightFirst(ctx) is IState fight ) return fight;
			var me = new System.Numerics.Vector2(ctx.Grid.X, ctx.Grid.Y);
			var icon = Encounters().Where(i => !routine.IconDone(i)).OrderBy(i => System.Numerics.Vector2.Distance(i.GridPos, me)).FirstOrDefault();
			if ( icon == null ) { Detail = $"{routine.FatesHere} of {Count} taken: exploring for more"; return Call(new Explore(null)); }
			var grid = new Offsets.Vector2i((int)icon.GridPos.X, (int)icon.GridPos.Y);
			float d = Combat.Distance(ctx.Grid, grid);
			long now = Time.ElapsedMilliseconds;
			if ( going != icon.EntityId ) {
				going = icon.EntityId; since = now; arrivedAt = -1;
				Log($"{routine.Name}: going to the Augury encounter at {grid} ({d:F0} cells away; {routine.FatesHere} of {Count} fates taken)");
			}
			if ( d <= Reach ) {
				// (there: Interactables clicks its card, and TryAugury works on the window; wait a while for a fate to be taken)
				if ( arrivedAt < 0 ) { arrivedAt = now; fatesAtArrival = routine.FatesHere; }
				if ( routine.FatesHere == fatesAtArrival && now - arrivedAt < AtIconMs ) {
					Detail = $"at the Augury encounter ({routine.FatesHere} of {Count} taken)";
					return this;
				}
				routine.MarkIconDone(icon);
				return this;
			}
			if ( now - since > IconMs ) {
				Log($"{routine.Name}: could not get to the Augury encounter at {grid} in {IconMs} ms ({d:F0} cells away): giving up on it");
				routine.MarkIconDone(icon);
				return this;
			}
			var spot = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, grid, out var snapped) ? snapped : grid;
			Detail = $"going to an Augury encounter, {d:F0} cells away ({routine.FatesHere} of {Count} taken)";
			ctx.Driver.WalkTo(spot, Detail);
			return this;
		}
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
