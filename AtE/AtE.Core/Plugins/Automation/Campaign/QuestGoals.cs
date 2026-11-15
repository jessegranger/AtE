using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Open some quest chests and pick up the quest item each drops (eg, The Mud Flats: three Rhoa Nests, each drops a
	/// glyph). The chests are found from the area's tiles when it loads (Terrain.TileFeatures), so it walks to each in
	/// turn, nearest first, fighting the monsters within FightRadius on the way. At a chest: click its label (the
	/// Chest's IsOpened goes 0 -> 1), then pick up the quest item that drops near it. A chest is done once it is open
	/// and no quest item is on the ground near it. Count of ItemPathPart in the backpack is done too (eg, after a restart).
	///
	/// 3.29.3, The Mud Flats (confirmed live): a nest is the entity Metadata/Chests/QuestChests/RhoaChest3 (1, 2, 3),
	/// standing at the center of a 3x3 tile group cut from Metadata/Terrain/Beach/Features/rhoanest_v01_01.tdt; it
	/// drops Metadata/Items/QuestItems/Glyphs/Glyph3.
	/// </summary>
	public class OpenQuestChests : Goal {
		public readonly string Label;
		public readonly string TilePathPart;
		public readonly string ChestPathPart;
		public readonly int Count;
		/// <summary>Fight the monsters within this many cells on the way.</summary>
		public float FightRadius = 100;
		/// <summary>Click a label when within this many cells of it.</summary>
		public float Reach = 12;
		/// <summary>A quest item this near a chest is the one it dropped.</summary>
		public float DropRadius = 30;
		/// <summary>An open chest with no quest item near it for this long is done (time for the item to drop).</summary>
		public long SettleMs = 1500;
		/// <summary>A part of the quest items' metadata path (eg, "QuestItems/Glyphs/"): Count of them in the backpack is done too.</summary>
		public string ItemPathPart;

		private class Spot {
			public Offsets.Vector2i Pos;
			public bool Done;
			public long EmptySince; // when it was first seen open, with no quest item near it; 0 if not
		}
		private List<Spot> spots;
		private long lastClick;

		/// <param name="label">What it is called on the ground (eg, "Rhoa Nest"); for the on-screen list.</param>
		/// <param name="tilePathPart">A part of the path of the .tdt the chests' tiles are cut from (eg, "rhoanest").</param>
		/// <param name="chestPathPart">A part of the chests' metadata path (eg, "QuestChests/Rhoa").</param>
		/// <param name="count">How many there are.</param>
		public OpenQuestChests(string label, string tilePathPart, string chestPathPart, int count) {
			Label = label; TilePathPart = tilePathPart; ChestPathPart = chestPathPart; Count = count;
		}

		public override string Name => $"Open the {Count} {Label}s, and pick up what they drop" + (spots == null ? "" : $" ({spots.Count(s => s.Done)} done)");
		public override void Reset() { base.Reset(); spots = null; }
		public override bool ProvesEarlier => true;

		public override bool CheckDone(CombatContext ctx) => (spots != null && spots.Count(s => s.Done) >= Count) || QuestItemsInBackpack() >= Count;

		private int QuestItemsInBackpack() => ItemPathPart == null ? 0
			// (the Items, not BackpackItems(), which only has them while the inventory is shown)
			: GetUI()?.InventoryPanel?.Backpack?.Items.Count(i => i?.Entity?.Path?.Contains(ItemPathPart) ?? false) ?? 0;

		protected override IState Step(CombatContext ctx) {
			spots ??= ctx.Terrain.TileFeatures(TilePathPart).Select(f => new Spot() { Pos = f.GridPos }).ToList();
			var chests = Chests().ToList();
			// a chest not in the tiles (eg, a different tile set): add it once it is seen
			foreach ( var (ent, pos) in chests ) {
				if ( !spots.Any(s => Combat.Distance(s.Pos, pos) < DropRadius) ) spots.Add(new Spot() { Pos = pos });
			}
			var spot = spots.Where(s => !s.Done).OrderBy(s => Combat.Distance(ctx.Grid, s.Pos)).FirstOrDefault();
			if ( spot == null ) {
				// none known yet: look for them
				Detail = $"looking for a {Label}";
				return Call(new Explore($"a {Label}") { FightRadius = FightRadius });
			}
			if ( FightFirst(ctx, FightRadius) is IState fight ) { Detail = $"fighting on the way to a {Label}"; return fight; }

			// its quest item on the ground: pick it up
			var item = QuestItemLabels().FirstOrDefault(l => Combat.Distance(l.pos, spot.Pos) <= DropRadius);
			if ( item.label != null ) {
				spot.EmptySince = 0;
				return ClickWhenNear(ctx, item.label, item.pos, $"picking up {item.name}");
			}
			var chest = chests.FirstOrDefault(c => Combat.Distance(c.pos, spot.Pos) < DropRadius);
			if ( chest.ent == null ) {
				Detail = $"to the {Label} at {spot.Pos}, {Combat.Distance(ctx.Grid, spot.Pos):F0} cells away";
				return Call(new Navigate($"a {Label}", _ => spot.Pos, Reach) { FightRadius = FightRadius, LegMs = 1000 });
			}
			if ( chest.ent.GetComponent<Chest>()?.IsOpened ?? false ) {
				// open, and nothing near it to pick up: done, once the drop has had time to show
				if ( spot.EmptySince == 0 ) spot.EmptySince = Time.ElapsedMilliseconds;
				Detail = "open, waiting for its drop";
				ctx.Driver.StopWalking("waiting for a drop");
				if ( Time.ElapsedMilliseconds - spot.EmptySince >= SettleMs ) {
					spot.Done = true;
					Log($"{Name}: done with the {Label} at {spot.Pos}");
				}
				return this;
			}
			spot.EmptySince = 0;
			var label = GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Address == chest.ent.Address);
			if ( label == null ) {
				Detail = $"to the {Label}, {Combat.Distance(ctx.Grid, chest.pos):F0} cells away (no label yet)";
				return Call(new Navigate($"the {Label}", _ => chest.pos, 5) { FightRadius = FightRadius, LegMs = 1000 });
			}
			return ClickWhenNear(ctx, label, chest.pos, $"opening the {Label}");
		}

		// walk to pos, and click the label once within Reach, or as near as can be walked (at most once a second)
		private IState ClickWhenNear(CombatContext ctx, LabelOnGround label, Offsets.Vector2i pos, string what) {
			if ( !IsNear(ctx, pos, Reach, out _, out float d) ) {
				Detail = $"{what}, {d:F0} cells away";
				return Call(new Navigate(what, _ => pos, Reach) { FightRadius = FightRadius });
			}
			ctx.Driver.StopWalking(what);
			Detail = what;
			if ( Time.ElapsedMilliseconds - lastClick < 1000 || !(Ground.ClickLabel(label, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			return click;
		}

		// the chests in range
		private IEnumerable<(Entity ent, Offsets.Vector2i pos)> Chests() {
			foreach ( var e in GetEntities() ) {
				if ( !IsValid(e) || !(e.Path?.Contains(ChestPathPart) ?? false) ) continue;
				var pos = e.GetComponent<Positioned>();
				if ( IsValid(pos) ) yield return (e, pos.GridPos);
			}
		}

		/// <summary>The quest items on the ground (with a label), and where.</summary>
		public static IEnumerable<(LabelOnGround label, Offsets.Vector2i pos, string name)> QuestItemLabels(string pathPart = "/QuestItems/") {
			foreach ( var l in GetUI()?.LabelsOnGround?.GetAllLabels() ?? Enumerable.Empty<LabelOnGround>() ) {
				var ground = l.Item;
				var item = ground?.GetComponent<WorldItem>()?.Item;
				if ( !(item?.Path?.Contains(pathPart) ?? false) ) continue;
				var pos = ground.GetComponent<Positioned>();
				if ( IsValid(pos) ) yield return (l, pos.GridPos, Equipment.ItemName(item) ?? item.Path);
			}
		}
	}

	/// <summary>
	/// Pick up the quest item a boss dropped (eg, Hailrake's, on The Tidal Island): a label on the ground whose item's
	/// path contains ItemPathPart, within Radius cells. Done once one was seen and is gone (picked up, by this or the
	/// looter), or if none shows within WaitMs of starting (nothing dropped here).
	/// </summary>
	public class PickUpQuestItem : Goal {
		public readonly string Label;
		public string ItemPathPart = "/QuestItems/";
		public float Radius = 60;
		public float Reach = 12;
		public long WaitMs = 5000;
		/// <summary>Clicks on it from within Reach that leave it on the ground before stopping for help (no room for it).</summary>
		public int MaxClicks = 3;
		private int clicks;
		private long started, lastClick;
		private bool seen, gone;

		/// <param name="label">What it is, for the on-screen list (eg, "Hailrake's quest item").</param>
		public PickUpQuestItem(string label) => Label = label;
		public override string Name => $"Pick up {Label}";
		public override void Reset() { base.Reset(); started = 0; clicks = 0; seen = gone = false; }

		private (LabelOnGround label, Offsets.Vector2i pos) Find(CombatContext ctx) {
			var found = OpenQuestChests.QuestItemLabels(ItemPathPart).FirstOrDefault(l => Combat.Distance(ctx.Grid, l.pos) <= Radius);
			return (found.label, found.pos);
		}

		public override bool CheckDone(CombatContext ctx) {
			if ( gone ) return true;
			if ( started == 0 ) return false;
			bool there = Find(ctx).label != null;
			if ( there ) seen = true;
			else if ( seen || Time.ElapsedMilliseconds - started > WaitMs ) {
				if ( !seen ) Log($"{Name}: no quest item showed within {WaitMs} ms");
				gone = true;
			}
			return gone;
		}

		protected override IState Step(CombatContext ctx) {
			if ( started == 0 ) started = Time.ElapsedMilliseconds;
			var (label, pos) = Find(ctx);
			if ( label == null ) { ctx.Driver.StopWalking("waiting for a drop"); Detail = "waiting for it to drop"; return this; }
			seen = true;
			if ( !IsNear(ctx, pos, Reach, out _, out float d) ) {
				Detail = $"{d:F0} cells away";
				return Call(new Navigate(Label, _ => pos, Reach) { FightRadius = -1 });
			}
			ctx.Driver.StopWalking($"picking up {Label}");
			if ( Time.ElapsedMilliseconds - lastClick < 1000 ) return this;
			if ( clicks >= MaxClicks ) {
				clicks = 0; // (once the player made room and switched the routine on again, it tries again)
				return Call(new StopForHelp($"{Label} stayed on the ground after {MaxClicks} clicks: make room in the inventory"));
			}
			Detail = $"clicking it (click {clicks + 1})";
			if ( !(Ground.ClickLabel(label, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			clicks++;
			return click;
		}
	}

	/// <summary>
	/// Walk to within Reach cells of a group of tiles (Terrain.TileFeatures: the walkable cell nearest the middle of the
	/// tiles cut from a .tdt whose path has TilePathPart), fighting on the way. Done once there (eg, The Way Forward's
	/// "Follow the Emperor's Road": near Prisoner's Gate's Beach/Transitions/beach_passageblock tile).
	/// </summary>
	public class ApproachTiles : Goal {
		public readonly string Label;
		public readonly string TilePathPart;
		public float Reach = 50;
		public float FightRadius = 75;
		/// <summary>How far from the tiles (cells) to look for the nearest cell that can be walked to.</summary>
		public int SnapRadius = 200;
		private bool reached;
		// the walkable cell nearest the tiles that can be walked to from the player, for the tiles at spotFor
		private Offsets.Vector2i spot, spotFor;
		private bool hasSpot;

		/// <param name="label">What to call it on the on-screen list, eg "the Emperor's Road".</param>
		/// <param name="tilePathPart">A part of the path of the .tdt, eg "/beach_passageblock".</param>
		public ApproachTiles(string label, string tilePathPart) { Label = label; TilePathPart = tilePathPart; }

		public override string Name => $"Go to {Label}";
		public override void Reset() { base.Reset(); hasSpot = false; } // (a new area instance: a new map)

		private AreaFeature Nearest(CombatContext ctx) =>
			ctx.Terrain.TileFeatures(TilePathPart).OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).FirstOrDefault();

		public override bool CheckDone(CombatContext ctx) {
			if ( reached ) return true;
			if ( Where != null && !GoToArea.Is(GetCurrentArea(), Where) ) return false;
			var tiles = Nearest(ctx);
			if ( tiles == null ) return false;
			if ( Combat.Distance(ctx.Grid, tiles.GridPos) <= Reach ) {
				Log($"Campaign: within {Reach} cells of {Label} ({tiles.GridPos})");
				return reached = true;
			}
			// (the nearest cell that can be walked to may be further than Reach: being there is as near as it gets)
			if ( hasSpot && spotFor.Equals(tiles.GridPos) && Combat.Distance(ctx.Grid, spot) <= 3 ) {
				Log($"Campaign: at {spot}, the nearest walkable cell to {Label} ({tiles.GridPos}, {Combat.Distance(spot, tiles.GridPos):F0} cells away)");
				return reached = true;
			}
			return false;
		}

		// where to walk to: the cell nearest the tiles that can be walked to from here (searched once per tile group)
		private Offsets.Vector2i Spot(CombatContext ctx, Offsets.Vector2i target) {
			if ( !hasSpot || !spotFor.Equals(target) ) {
				spotFor = target;
				hasSpot = true;
				if ( !DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, target, out spot, SnapRadius) ) spot = target;
				Log($"Campaign: {Label} at {target}: walking to {spot}, the nearest cell that can be walked to ({Combat.Distance(spot, target):F0} cells from it)");
			}
			return spot;
		}

		protected override IState Step(CombatContext ctx) {
			var tiles = Nearest(ctx);
			if ( tiles == null ) {
				Detail = $"no tiles with \"{TilePathPart}\" in this area";
				return FightFirst(ctx, FightRadius) ?? Call(new Explore(Label) { FightRadius = FightRadius });
			}
			var to = Spot(ctx, tiles.GridPos);
			Detail = $"to {to} (by {tiles.GridPos}), {Combat.Distance(ctx.Grid, to):F0} cells away";
			// (no LegMs: a long walk handed back every second was started over each time, before the path was found, and
			// never moved; the walk ends once near, or for a fight on the way)
			return Call(new Navigate(Label, _ => to, 3) { FightRadius = FightRadius });
		}
	}

	/// <summary>
	/// Go up a building by stairs within the area (eg, The Ancient Pyramid, 3.29.4: three Dungeon/Stairs/dungeon_stairs_up
	/// tiles, whose stairs are labelled "Stairs", "Stairs" and "Pyramid Apex", each a transition to the next floor of the
	/// same area). Each floor is cut off from the others in the walkable grid, so the stairs to take next are on the one
	/// tile of TilePathPart the player can walk to: walk there, and click the label of the stairs on it. Done once the
	/// top's tile (TopTilePathPart, eg the boss's) can be walked to.
	/// </summary>
	public class ClimbStairs : Goal {
		public readonly string Label;
		public readonly string TilePathPart, TopTilePathPart;
		/// <summary>The labels of the stairs (eg, "Stairs", "Pyramid Apex").</summary>
		public readonly string[] Labels;
		/// <summary>How far (cells) from a tile's position the walkable floor beside it (or the stairs' label) may be.</summary>
		public int TileReach = 25;
		public float Reach = 12;
		private long lastClick;
		private Offsets.Vector2i lastTile;

		public ClimbStairs(string label, string tilePathPart, string topTilePathPart, params string[] labels) {
			Label = label; TilePathPart = tilePathPart; TopTilePathPart = topTilePathPart; Labels = labels;
		}
		public override string Name => $"Climb {Label}";
		public override bool ProvesEarlier => true;

		private bool CanWalkTo(CombatContext ctx, AreaFeature f) => DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, f.GridPos, out _, TileReach);

		public override bool CheckDone(CombatContext ctx) {
			if ( Where != null && !GoToArea.Is(GetCurrentArea(), Where) ) return false;
			return ctx.Terrain.TileFeatures(TopTilePathPart).Any(f => CanWalkTo(ctx, f));
		}

		protected override IState Step(CombatContext ctx) {
			var tile = ctx.Terrain.TileFeatures(TilePathPart).Where(f => CanWalkTo(ctx, f))
				.OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).FirstOrDefault();
			if ( tile == null ) {
				Detail = $"no \"{TilePathPart}\" tile can be walked to";
				return FightFirst(ctx) ?? Call(new Explore(Label));
			}
			if ( !tile.GridPos.Equals(lastTile) ) {
				lastTile = tile.GridPos;
				Log($"Campaign: {Label}: the stairs to take are on the tile at {tile.GridPos}");
			}
			// the stairs' label, on (near) that tile
			var label = (GetUI()?.LabelsOnGround?.GetAllLabels() ?? Enumerable.Empty<LabelOnGround>())
				.Where(l => Labels.Any(t => string.Equals(Ground.LabelText(l.Label)?.Trim(), t, StringComparison.OrdinalIgnoreCase)))
				.Select(l => (label: l, pos: l.Item?.GetComponent<Positioned>()))
				.Where(x => IsValid(x.pos) && Combat.Distance(x.pos.GridPos, tile.GridPos) <= TileReach)
				.OrderBy(x => Combat.Distance(x.pos.GridPos, tile.GridPos))
				.FirstOrDefault();
			if ( label.label == null ) {
				Detail = $"to the stairs at {tile.GridPos}, {Combat.Distance(ctx.Grid, tile.GridPos):F0} cells away";
				return Call(new Navigate(Label, c => Approach(c, tile.GridPos), 3) { FightRadius = IgnoreMonsters ? -1 : 0, LegMs = 1000 });
			}
			var at = label.pos.GridPos;
			if ( !IsNear(ctx, at, Reach, out _, out float d) ) {
				Detail = $"{Ground.LabelText(label.label.Label)?.Trim()}, {d:F0} cells away";
				return GoTo(Label, at, Reach);
			}
			if ( FightFirst(ctx) is IState fight ) return fight;
			ctx.Driver.StopWalking($"taking the {Ground.LabelText(label.label.Label)?.Trim()}");
			if ( Time.ElapsedMilliseconds - lastClick < 2000 || !(Ground.ClickLabel(label.label, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			Detail = $"taking the {Ground.LabelText(label.label.Label)?.Trim()} at {at}";
			Log($"Campaign: {Label}: taking the {Ground.LabelText(label.label.Label)?.Trim()} at {at}");
			return click;
		}
	}

	/// <summary>
	/// Click a quest object that unlocks something (eg, The Mud Flats' Strange Glyph Wall, which opens the way to The
	/// Submerged Passage, once the three glyphs are had). Walk toward its tiles (Terrain.TileFeatures) until the object
	/// is in range, then click its label. Done when the thing it unlocks is in range and targetable (sticky).
	///
	/// 3.29.3, The Mud Flats (confirmed live): the wall is Metadata/QuestObjects/WaterCave/GlyphWall, in the tiles cut from
	/// .../BeachToWatercave_v2/Beach_to_watercave_v2.tdt; the exit beside it,
	/// Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition, goes from Targetable 0 to 1 when the wall is used.
	/// </summary>
	public class UseQuestObject : Goal {
		public readonly string Label;
		public readonly string ObjectPathPart;
		public readonly string TilePathPart;
		public readonly string UnlocksPathPart;
		public float FightRadius = 100;
		public float Reach = 12;
		private long lastClick;
		private int clicks;

		/// <param name="label">Its label on the ground, eg "Strange Glyph Wall" (for the on-screen list).</param>
		/// <param name="objectPathPart">A part of its metadata path, eg "WaterCave/GlyphWall".</param>
		/// <param name="tilePathPart">A part of the path of the .tdt it is in, eg "watercave", to know where to go before it is in range.</param>
		/// <param name="unlocksPathPart">A part of the metadata path of what it unlocks, eg "WaterCave/WaterCaveAreaTransition".</param>
		public UseQuestObject(string label, string objectPathPart, string tilePathPart, string unlocksPathPart) {
			Label = label; ObjectPathPart = objectPathPart; TilePathPart = tilePathPart; UnlocksPathPart = unlocksPathPart;
		}

		public override string Name => $"Use the {Label}";
		public override bool ProvesEarlier => true;
		public override void Reset() { base.Reset(); clicks = 0; }

		private static Entity Find(string pathPart) => GetEntities().FirstOrDefault(e => IsValid(e) && (e.Path?.Contains(pathPart) ?? false));

		public override bool CheckDone(CombatContext ctx) {
			var unlocked = Find(UnlocksPathPart);
			return unlocked != null && (unlocked.GetComponent<Targetable>()?.IsTargetable ?? false);
		}

		protected override IState Step(CombatContext ctx) {
			if ( FightFirst(ctx, FightRadius) is IState fight ) { Detail = $"fighting on the way to the {Label}"; return fight; }
			var obj = Find(ObjectPathPart);
			var pos = obj?.GetComponent<Positioned>();
			if ( !IsValid(pos) ) {
				var tiles = ctx.Terrain.TileFeatures(TilePathPart).OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).FirstOrDefault();
				if ( tiles != null ) {
					Detail = $"to the {Label}, near {tiles.GridPos}";
					return Call(new Navigate($"the {Label}", _ => tiles.GridPos, Reach) { FightRadius = FightRadius, LegMs = 1000 });
				}
				Detail = $"looking for the {Label}";
				return Call(new Explore($"the {Label}") { FightRadius = FightRadius });
			}
			var label = GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Address == obj.Address);
			if ( !IsNear(ctx, pos.GridPos, Reach, out _, out float d) || label == null ) {
				Detail = $"to the {Label}, {d:F0} cells away{(label == null ? " (no label yet)" : "")}";
				return Call(new Navigate($"the {Label}", _ => pos.GridPos, label == null ? 3 : Reach) { FightRadius = FightRadius, LegMs = 1000 });
			}
			ctx.Driver.StopWalking($"using the {Label}");
			Detail = clicks == 0 ? "clicking it" : $"clicked it {clicks} times, waiting for it to unlock";
			if ( Time.ElapsedMilliseconds - lastClick < 2000 || !(Ground.ClickLabel(label, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			clicks += 1;
			return click;
		}
	}

	/// <summary>
	/// Which reward to take from a quest reward window: the only one, if there is one; else the first offered of (1) the
	/// gems the Build's gem plan uses (those needed at the character's level first), (2) the rewards named in Overrides
	/// (quest => "reward, reward"; any quest's, as the window does not say which quest it is for), (3) a Quicksilver
	/// Flask, (4) the first reward offered.
	/// </summary>
	public static class RewardChooser {
		public static (Element element, string name, string why) Choose(IReadOnlyDictionary<string, string> overrides) {
			var rewards = Town.QuestRewards().Select(r => (element: r, item: Town.RewardItem(r))).Where(r => r.item != null).ToList();
			if ( rewards.Count == 0 ) return (null, null, "no rewards shown");
			// (eg, a Book of Skill: nothing to choose)
			if ( rewards.Count == 1 ) return (rewards[0].element, Equipment.ItemName(rewards[0].item) ?? rewards[0].item.Path, "the only reward");
			string NameOf(Entity item) => Equipment.ItemName(item) ?? item.Path;
			// a gem the build uses comes first (the overrides are not per build: ones written for another build must not
			// win over the build's own gems)
			var plan = BuildPlan.Gems();
			if ( plan != null ) {
				int level = GemPlan.CharacterLevel();
				var needed = plan.Needed(level);
				var r = rewards.FirstOrDefault(x => needed.Any(g => GemPlan.Matches(g, x.item)));
				if ( r.element != null ) return (r.element, NameOf(r.item), "the build needs it now");
				r = rewards.FirstOrDefault(x => plan.Wants(x.item));
				if ( r.element != null ) return (r.element, NameOf(r.item), "the build uses it later");
			}
			foreach ( var wanted in overrides?.Values.SelectMany(SplitNames) ?? Enumerable.Empty<string>() ) {
				var r = rewards.FirstOrDefault(x => Equipment.IsGem(x.item, wanted) || string.Equals(Equipment.ItemName(x.item), wanted, StringComparison.OrdinalIgnoreCase));
				if ( r.element != null ) return (r.element, NameOf(r.item), "named in the reward overrides");
			}
			var flask = rewards.FirstOrDefault(x => string.Equals(Equipment.ItemName(x.item), "Quicksilver Flask", StringComparison.OrdinalIgnoreCase));
			if ( flask.element != null ) return (flask.element, NameOf(flask.item), "a Quicksilver Flask");
			return (rewards[0].element, NameOf(rewards[0].item), "nothing the build wants: the first one");
		}

		/// <summary>"a, b" => a, b.</summary>
		public static string[] SplitNames(string names) => (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		/// <summary>"Quest: reward, reward; Quest: reward" => quest => "reward, reward".</summary>
		public static Dictionary<string, string> ParseOverrides(string text) {
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach ( var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ) {
				int colon = part.IndexOf(':');
				if ( colon > 0 ) result[part.Substring(0, colon).Trim()] = part.Substring(colon + 1).Trim();
			}
			return result;
		}
	}

	/// <summary>
	/// Kill a bandit leader (Act 2: Kraityn, Alira, Oak): find them in their area (looking at their camp's tiles first,
	/// see Search.BanditCamp; a unique monster, named so, that
	/// CannotBeDamaged until a choice is made), click them to talk, and in the dialog that shows (HelpOrKillBandit, found
	/// by its text) click "Kill [name]" (never Help): they turn hostile (CannotBeDamaged 0), and are fought as any monster is (Kill: the driver's Engage).
	/// Done once seen dead (kept while the overlay runs, through new runs), or by DoneWhen (eg, the quest's step no longer
	/// naming their area). (Their quest items go to the quest inventory, not the backpack.)
	/// </summary>
	public class KillBandit : Goal {
		public readonly string Bandit;
		private readonly Func<string> skill;
		private long lastClick;
		private bool killed;
		public KillBandit(string bandit, Func<string> skill) { Bandit = bandit; this.skill = skill; SearchAt = AtE.Search.BanditCamp(); }
		public override string Name => $"Kill {Bandit} (the bandit)";

		// Unique only: their guards' names may hold theirs too (eg, the Stuck in The Western Forest after Alira was in sight)
		private Entity Find() => GetEntities()?.FirstOrDefault(e => e?.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& e.GetComponent<ObjectMagicProperties>()?.Rarity == Offsets.MonsterRarity.Unique
			&& (e.GetComponent<Render>()?.Name?.Contains(Bandit, StringComparison.OrdinalIgnoreCase) ?? false));

		/// <summary>
		/// The dialog's "Kill [name]" button (its text "kill oak", 3.29.4), if it shows: in UIElements.HelpOrKillBandit
		/// (searching from the UI root did not find it: Oak's was clicked on over and over instead).
		/// </summary>
		private Element KillButton() => Town.FindByText(GetUI()?.HelpOrKillBandit, $"Kill {Bandit}", 8);

		public override bool CheckDone(CombatContext ctx) => killed;

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( KillButton() is Element button ) {
				ctx.Driver.StopWalking($"choosing to kill {Bandit}");
				if ( now - lastClick < 1000 ) return this;
				lastClick = now;
				Detail = $"clicking Kill {Bandit}";
				Log($"Campaign: {Bandit}: clicking \"Kill {Bandit}\"");
				return Inputs.Click(button.GetClientRect(), this);
			}
			var bandit = Find();
			if ( bandit == null ) return Search(ctx, Bandit);
			if ( !IsAlive(bandit) ) { killed = true; Detail = $"{Bandit} is dead"; return this; }
			if ( !Combat.CannotBeDamaged(bandit) ) {
				Detail = $"fighting {Bandit}";
				return Call(new Kill(Bandit, e => e?.Address == bandit.Address, skill));
			}
			// not chosen yet: talk to them
			var pos = bandit.GetComponent<Positioned>();
			if ( !IsValid(pos) ) return this;
			if ( !IsNear(ctx, pos.GridPos, 12, out _, out float d) ) {
				Detail = $"to {Bandit}, {d:F0} cells away";
				return Call(new Navigate(Bandit, _ => pos.GridPos, 12) { FightRadius = 60 });
			}
			ctx.Driver.StopWalking($"talking to {Bandit}");
			if ( now - lastClick < 1500 || !(Ground.ClickEntity(bandit, this) is IState click) ) return this;
			lastClick = now;
			Detail = $"talking to {Bandit}";
			return click;
		}
	}

	/// <summary>
	/// Take the rewards of finished quests: for every quest in the quest tracker whose step is "Talk to [NPC] for your
	/// reward", talk to that NPC, choose its option ending in "Reward", and Ctrl+click the reward RewardChooser picks.
	/// While a reward window is open (an NPC with more than one reward opens it again after each), it keeps choosing.
	/// Done when no quest shows that step and no reward window is open.
	/// </summary>
	public class TakeQuestRewards : Goal {
		private readonly Func<IReadOnlyDictionary<string, string>> overrides;
		private long lastClick, retryAt;
		private int clicks;
		private string clicked;
		// the quest last talked for (the reward window does not say which quest it is for)
		private string talkedFor;

		/// <summary>The reward to take first for a quest (eg, The Great White Beast: "Quicksilver Flask"), if offered; null: none.</summary>
		public Func<string, string> Prefer;

		/// <param name="overrides">The rewards to prefer (see RewardChooser), asked when needed (eg, from the campaign's settings).</param>
		public TakeQuestRewards(Func<IReadOnlyDictionary<string, string>> overrides = null) => this.overrides = overrides;

		public override string Name => "Take the quest rewards";
		public override bool Sticky => false;
		// the quest reward window opens the inventory beside it: the campaign must not close that (it cancels the reward)
		public override bool UsesInventory => true;
		public override void Reset() { base.Reset(); clicks = 0; retryAt = 0; clicked = null; emptySince = 0; talks.Clear(); gaveUp.Clear(); }

		/// <summary>Talks to a quest's NPC that open no reward window before it is left (until a new area instance).</summary>
		public int MaxTalks = 3;
		private readonly Dictionary<string, int> talks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> gaveUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// the first quest waiting for its reward (and not given up on), and who has it
		/// <summary>Which quests (as the tracker names them) it takes the rewards of; null: all (eg, one town's, see QuestHandler.RewardTown).</summary>
		public Func<string, bool> Quests;

		private (string quest, string npc) Pending() {
			foreach ( var quest in Town.QuestNames() ) {
				if ( !gaveUp.Contains(quest) && (Quests?.Invoke(quest) ?? true) && Town.RewardNpc(quest) is string npc ) return (quest, npc);
			}
			return (null, null);
		}

		// (a reward window still showing, with no rewards in it, and none owed: done too, once that has lasted EmptyMs)
		public override bool CheckDone(CombatContext ctx) => Pending().quest == null
			&& (!Town.IsOpen(Town.QuestRewardWindow) || (emptySince > 0 && Time.ElapsedMilliseconds - emptySince > EmptyMs));
		/// <summary>How long a reward window showing no rewards is waited on.</summary>
		public long EmptyMs = 1500;
		private long emptySince; // when the open reward window was first seen with no rewards in it; 0 if not

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( Town.IsOpen(Town.QuestRewardWindow) ) {
				talks.Clear(); // (the talk worked)
				ctx.Driver.StopWalking("taking a quest reward");
				if ( now - lastClick < 800 ) return this;
				var (element, reward, why) = RewardChooser.Choose(overrides?.Invoke());
				if ( talkedFor != null && Prefer?.Invoke(talkedFor) is string want ) {
					var offered = Town.QuestRewards().FirstOrDefault(r => Town.RewardItem(r) is Entity item
						&& (Equipment.ItemName(item)?.Contains(want, StringComparison.OrdinalIgnoreCase) ?? false));
					if ( offered != null ) { element = offered; reward = Equipment.ItemName(Town.RewardItem(offered)); why = $"{talkedFor} prefers {want}"; }
				}
				if ( element == null ) {
					if ( emptySince == 0 ) emptySince = now;
					Detail = $"the reward window shows no rewards ({now - emptySince} ms)";
					return this;
				}
				emptySince = 0;
				if ( reward != clicked ) { clicked = reward; clicks = 0; }
				if ( clicks >= 3 ) { Detail = $"clicked {reward} {clicks} times, but it was not taken"; return this; }
				Detail = $"taking {reward} ({why})";
				Log($"{Name}: taking {reward} ({why}) from {Town.RewardNames()}: Ctrl + click {element.GetClientRect()} (click {clicks + 1})");
				// with Ctrl held, the game takes it at once
				lastClick = now;
				clicks += 1;
				return Inputs.CtrlClick(element.GetClientRect(), this);
			}
			// (the window closed, or is not open yet: a window opened again starts its clicks over)
			emptySince = 0;
			if ( now - lastClick > 1500 ) { clicked = null; clicks = 0; }
			if ( Town.NpcContinue() is Element more ) {
				// the NPC shows story text before the reward window: Continue to it
				ctx.Driver.StopWalking("talking for a quest reward");
				if ( now - lastClick < 800 ) return this;
				lastClick = now;
				retryAt = now + 2000;
				Detail = "clicking Continue";
				return Inputs.Click(more.GetClientRect(), this);
			}
			var (quest, npc) = Pending();
			talkedFor = quest ?? talkedFor;
			if ( quest == null ) return this;
			if ( Town.AnyWindowOpen ) {
				// eg, the purchase window, from the wrong option
				ctx.Driver.StopWalking("closing a window");
				if ( now - lastClick < 700 ) return this;
				lastClick = now;
				return Inputs.Click(new System.Drawing.RectangleF(1, 1, 0, 0), this); // (a click at the top left corner closes them)
			}
			if ( now < retryAt ) return this;
			talks[quest] = talks.TryGetValue(quest, out int n) ? n + 1 : 1;
			if ( talks[quest] > MaxTalks ) {
				gaveUp.Add(quest);
				Log($"{Name}: talked to {npc} {MaxTalks} times for {quest}, but no reward window opened: leaving it");
				return this;
			}
			Detail = $"{quest}: talking to {npc}";
			retryAt = now + 2000; // (if the talk hands back without the reward window: try again in a while)
			return Call(new TalkToNpc(npc, text => text.EndsWith(" Reward", StringComparison.OrdinalIgnoreCase), $"{quest} reward"));
		}
	}
}
