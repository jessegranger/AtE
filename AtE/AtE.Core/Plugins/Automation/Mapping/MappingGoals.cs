using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static AtE.Globals;

namespace AtE {

	// The goals of the Mapping routine's loop (see MappingRoutine). None is sticky: each is asked again every time,
	// as each area entered starts its part of the loop over.

	/// <summary>
	/// Put everything in the backpack in the stash, but maps, scrolls, and what MappingRoutine.KeepInBackpack names: walk
	/// to the stash and click it, then Ctrl + click each item (into the tab that shows). An item still there after
	/// MaxClicks is left (eg, the tab is full). Done with nothing left to stash, and the stash closed again.
	/// TODO: confirm live (not seen yet): the stash's entity and label in a hideout, Escape closing it
	/// </summary>
	public class StashLoot : Goal {
		private readonly MappingRoutine routine;
		public float Reach = 12;
		public int MaxClicks = 3;
		private readonly Dictionary<uint, int> clicks = new Dictionary<uint, int>();
		private long lastClick, lastAction;

		public StashLoot(MappingRoutine routine) => this.routine = routine;
		public override string Name => "Stash the loot";
		public override bool Sticky => false;
		public override bool UsesInventory => true; // (the stash's window comes with the inventory)
		public override void Reset() { base.Reset(); clicks.Clear(); lastClick = lastAction = 0; }

		/// <summary>Kept in the backpack: maps (the next ones to run), Portal Scrolls, Scrolls of Wisdom, and KeepInBackpack's.</summary>
		public static bool Keeps(MappingRoutine routine, Entity e) {
			var path = e?.Path;
			if ( path == null ) return true; // (can't be read: leave it)
			if ( ChooseNextMap.IsMap(e) || path == Offsets.PATH_SCROLL_PORTAL || path == Offsets.PATH_SCROLL_WISDOM ) return true;
			return routine.KeepInBackpack.Split(',').Select(s => s.Trim()).Any(s => s.Length > 0 && path.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		private List<InventoryItem> ToStash() => BackpackItems()
			.Where(i => i?.Entity is Entity e && !Keeps(routine, e) && !(clicks.TryGetValue(e.Id, out int n) && n >= MaxClicks))
			.ToList();

		public override bool CheckDone(CombatContext ctx) => ToStash().Count == 0 && !StashIsOpen();

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			var items = ToStash();
			if ( StashIsOpen() ) {
				ctx.Driver.StopWalking("stashing");
				if ( items.Count == 0 ) {
					if ( now - lastAction < 700 ) return this;
					lastAction = now;
					Detail = "closing the stash";
					Inputs.LastEscapeMs = now; // (ours: if the stash had closed already, the escape menu that opens is closed too)
					return Inputs.Press(VKey.Escape, this);
				}
				if ( !BackpackIsOpen() || now - lastClick < 150 ) return this;
				var item = items[0];
				uint id = item.Entity.Id;
				clicks[id] = clicks.TryGetValue(id, out int n) ? n + 1 : 1;
				if ( clicks[id] >= MaxClicks ) Log($"{routine.Name}: {Equipment.ItemName(item.Entity) ?? item.Entity.Path} did not go in the stash: leaving it (is the tab full?)");
				lastClick = now;
				Detail = $"stashing {Equipment.ItemName(item.Entity) ?? "an item"} ({items.Count} left)";
				return Inputs.CtrlClick(item.GetClientRect(), new Delay(InventoryPlans.Settle, this));
			}
			if ( items.Count == 0 ) return this; // (done)
			var stash = (GetEntities() ?? Enumerable.Empty<Entity>()).Where(e => IsValid(e) && e.Path == Offsets.PATH_STASH)
				.Select(e => (e, pos: e.GetComponent<Positioned>())).Where(x => IsValid(x.pos))
				.OrderBy(x => Combat.Distance(ctx.Grid, x.pos.GridPos)).FirstOrDefault();
			if ( stash.e == null ) return Search(ctx, "the stash");
			if ( !IsNear(ctx, stash.pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"the stash is {d:F0} cells away";
				return GoTo("the stash", stash.pos.GridPos, Reach);
			}
			ctx.Driver.StopWalking("opening the stash");
			if ( now - lastAction < 1500 || !(Ground.ClickEntity(stash.e, this) is IState click) ) return this;
			lastAction = now;
			Detail = $"opening the stash ({items.Count} to stash)";
			return click;
		}
	}

	/// <summary>
	/// Choose the map to run next from the backpack (MappingRoutine.NextMap): one with a plan of its own first (see
	/// MapPlans), else any (unless OnlyPlannedMaps). With none, stop the routine. Done while the chosen map is in the backpack.
	/// </summary>
	public class ChooseNextMap : Goal {
		private readonly MappingRoutine routine;
		public ChooseNextMap(MappingRoutine routine) => this.routine = routine;
		public override string Name => routine.NextMap is { } n ? $"Choose the next map: {n.area}" : "Choose the next map";
		public override bool Sticky => false;

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

		// (also once the map device has opened it: the map is gone from the backpack then)
		public override bool CheckDone(CombatContext ctx) => (routine.NextMap is { } n && BackpackItems().Any(i => i?.Entity?.Id == n.id))
			|| OpenMap.FreshPortals(routine).Any();

		protected override IState Step(CombatContext ctx) {
			var planned = new HashSet<string>(routine.PlannedMaps, StringComparer.OrdinalIgnoreCase);
			var map = BackpackItems().Where(i => IsMap(i?.Entity))
				.Select(i => (id: i.Entity.Id, area: MapArea(i.Entity)))
				.Where(m => !routine.OnlyPlannedMaps || planned.Contains(m.area))
				.OrderBy(m => planned.Contains(m.area) ? 0 : 1)
				.FirstOrDefault();
			if ( map.area == null ) {
				string why = routine.OnlyPlannedMaps ? $"no maps with a plan ({string.Join(", ", planned)}) in the backpack" : "no maps in the backpack";
				Detail = why;
				Log($"{routine.Name}: stopping: {why}");
				ctx.Driver.Stop(why);
				return this;
			}
			routine.NextMap = map;
			Log($"{routine.Name}: the next map is {map.area}{(planned.Contains(map.area) ? "" : " (the default plan)")}");
			return this;
		}
	}

	/// <summary>
	/// Open the chosen map: walk to the Map Device and click it; once its window is open (with the inventory), put the map
	/// in and click Activate (see MapDevice.PlanLaunchMapFromInventory). Done once a map portal opens that was not there
	/// when Activate was clicked. Tried again every RetryMs until one does.
	/// TODO: confirm live (not seen yet): the hideout device's label ("Map Device") and entity path, that clicking it opens the inventory too
	/// </summary>
	public class OpenMap : Goal {
		private readonly MappingRoutine routine;
		public float Reach = 12;
		public long RetryMs = 6000;
		private long lastClick, lastPlan;

		public OpenMap(MappingRoutine routine) => this.routine = routine;
		public override string Name => $"Open {routine.NextMap?.area ?? "the map"} in the Map Device";
		public override bool Sticky => false;
		public override bool UsesInventory => true;
		public override void Reset() { base.Reset(); lastClick = lastPlan = 0; }

		/// <summary>The map portals opened since Activate was clicked in this visit to the hideout.</summary>
		public static IEnumerable<Entity> FreshPortals(MappingRoutine routine) =>
			routine.MapLaunched ? MapDevice.MapPortals().Where(p => !routine.StalePortals.Contains(p.Id)) : Enumerable.Empty<Entity>();

		public override bool CheckDone(CombatContext ctx) => FreshPortals(routine).Any();

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

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( MapDeviceIsOpen() ) {
				ctx.Driver.StopWalking("using the map device");
				if ( !BackpackIsOpen() ) {
					if ( now - lastClick < 1000 ) return this;
					lastClick = now;
					Detail = "opening the inventory";
					return GameKeys.Press(GameKeys.InventoryKey, this);
				}
				// (the map goes from the backpack when it goes in the device: wait for the portals a while before trying again)
				if ( now - lastPlan < RetryMs ) { Detail = "waiting for the portals"; return this; }
				var map = routine.NextMap is { } n ? BackpackItems().FirstOrDefault(i => i?.Entity?.Id == n.id) : null;
				if ( map == null ) {
					// (gone: choose again)
					Detail = "the chosen map is not in the backpack";
					routine.NextMap = null;
					return this;
				}
				lastPlan = now;
				// (the portals open now, eg to the map before, are not the new map's)
				routine.StalePortals.Clear();
				routine.StalePortals.UnionWith(MapDevice.MapPortals().Select(e => e.Id));
				routine.MapLaunched = true;
				Detail = $"putting {routine.NextMap?.area} in, and clicking Activate";
				return MapDevice.PlanLaunchMapFromInventory(map, this) ?? this;
			}
			var device = FindDevice(ctx);
			var pos = device?.GetComponent<Positioned>();
			if ( device == null || !IsValid(pos) ) return Search(ctx, "the Map Device");
			if ( !IsNear(ctx, pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"the Map Device is {d:F0} cells away";
				return GoTo("the Map Device", pos.GridPos, Reach);
			}
			ctx.Driver.StopWalking("opening the map device");
			if ( now - lastClick < 2000 || !(Ground.ClickEntity(device, this) is IState click) ) return this;
			lastClick = now;
			Detail = "clicking the Map Device";
			return click;
		}
	}

	/// <summary>Take a portal the map device opened (OpenMap.FreshPortals, the nearest): done once in the map.</summary>
	public class EnterMap : Goal {
		private readonly MappingRoutine routine;
		public float Reach = 12;
		private long lastClick, lastEscape;

		public EnterMap(MappingRoutine routine) => this.routine = routine;
		public override string Name => $"Enter {routine.NextMap?.area ?? "the map"}";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); lastClick = lastEscape = 0; }
		public override bool CheckDone(CombatContext ctx) => routine.InMap();

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( MapDeviceIsOpen() ) {
				// (its window covers the portals)
				ctx.Driver.StopWalking("closing the map device");
				if ( now - lastEscape < 1000 ) return this;
				lastEscape = Inputs.LastEscapeMs = now;
				Detail = "closing the map device";
				return Inputs.Press(VKey.Escape, this);
			}
			var portal = OpenMap.FreshPortals(routine).Select(e => (e, pos: e.GetComponent<Positioned>())).Where(x => IsValid(x.pos))
				.OrderBy(x => Combat.Distance(ctx.Grid, x.pos.GridPos)).FirstOrDefault();
			if ( portal.e == null ) { ctx.Driver.StopWalking("no portal"); Detail = "waiting for a portal"; return this; }
			if ( !IsNear(ctx, portal.pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"the portal is {d:F0} cells away";
				return Call(new Navigate("the map portal", _ => portal.pos.GridPos, Reach) { FightRadius = -1 });
			}
			ctx.Driver.StopWalking("taking the portal");
			if ( now - lastClick < 2000 || !(Ground.ClickEntity(portal.e, this) is IState click) ) return this;
			lastClick = now;
			routine.EnteringMap();
			Detail = "clicking the portal";
			return click;
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
			DoneWhen = () => routine.MapComplete || routine.UniquesKilled.Count > 0
				|| (routine.ExploreDone && !(GetEntities()?.Any(e => MappingRoutine.IsMapBoss(e) && IsAlive(e)) ?? false));
		}
		public override bool Sticky => false;
	}

	/// <summary>
	/// Go back to the hideout with a Portal Scroll (see UsePortalScroll), after LootWaitMs for the boss's drops (picked up
	/// meanwhile, see GoalRoutine). Out of Portal Scrolls: stop the routine.
	/// </summary>
	public class ReturnToHideout : Goal {
		private readonly MappingRoutine routine;
		public long LootWaitMs = 2000;
		private UsePortalScroll scroll;

		public ReturnToHideout(MappingRoutine routine) => this.routine = routine;
		public override string Name => "Return to the hideout";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); scroll = null; }
		public override bool CheckDone(CombatContext ctx) => routine.InHideout();

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( now - Math.Max(routine.LastUniqueKillAt, routine.MapCompleteAt) < LootWaitMs ) {
				ctx.Driver.StopWalking("waiting for the drops");
				Detail = "waiting for the boss's drops";
				return this;
			}
			if ( now - UsePortalScroll.OutOfScrollsAt < UsePortalScroll.OutForMs ) {
				const string why = "out of Portal Scrolls: put some in the backpack, then switch the routine on again";
				Detail = why;
				Log($"{routine.Name}: stopping: {why}");
				ctx.Driver.Stop(why);
				return this;
			}
			// (the scroll's portal goes back to where the map was opened: the hideout, by whatever name)
			string hideout = routine.HideoutName ?? "the hideout";
			if ( scroll == null || scroll.Destination != hideout ) scroll = new UsePortalScroll(hideout);
			return Call(scroll);
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
