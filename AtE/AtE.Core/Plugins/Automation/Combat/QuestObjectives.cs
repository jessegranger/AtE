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
	/// and no quest item is on the ground near it (an item picked up goes to the backpack, but the client only knows
	/// that after the inventory is opened, so it is not looked for there).
	///
	/// 3.29.3, The Mud Flats (confirmed live): a nest is the entity Metadata/Chests/QuestChests/RhoaChest3 (1, 2, 3),
	/// standing at the center of a 3x3 tile group cut from Metadata/Terrain/Beach/Features/rhoanest_v01_01.tdt; it
	/// drops Metadata/Items/QuestItems/Glyphs/Glyph3.
	/// </summary>
	public class OpenQuestChests : Objective {
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

		/// <summary>
		/// A part of the quest items' metadata path (eg, "QuestItems/Glyphs/"): Count of them in the backpack is done too, so a
		/// routine started again (or after logging in) skips the chests. (The client only knows they are in the backpack
		/// once the inventory has been opened, so they are not looked for there after each pick up.)
		/// </summary>
		public string ItemPathPart;

		public override bool CheckDone(CombatContext ctx) => (spots != null && spots.Count(s => s.Done) >= Count) || QuestItemsInBackpack() >= Count;

		private int QuestItemsInBackpack() => ItemPathPart == null ? 0
			// (the Items, not BackpackItems(), which only has them while the inventory is shown)
			: GetUI()?.InventoryPanel?.Backpack?.Items.Count(i => i?.Entity?.Path?.Contains(ItemPathPart) ?? false) ?? 0;

		public override void Tick(CombatContext ctx) {
			spots ??= ctx.Terrain.TileFeatures(TilePathPart).Select(f => new Spot() { Pos = f.GridPos }).ToList();
			var chests = Chests().ToList();
			// a chest not in the tiles (eg, a different tile set): add it once it is seen
			foreach ( var (ent, pos) in chests ) {
				if ( !spots.Any(s => Combat.Distance(s.Pos, pos) < DropRadius) ) spots.Add(new Spot() { Pos = pos });
			}
			var spot = spots.Where(s => !s.Done).OrderBy(s => Combat.Distance(ctx.Grid, s.Pos)).FirstOrDefault();
			if ( spot == null ) {
				// none known yet: look for them
				if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, FightRadius) ) { Detail = "fighting"; return; }
				Detail = $"looking for a {Label}";
				if ( !ctx.Driver.TryExplore(ctx) ) Detail = $"explored everything, found {spots.Count} {Label}s";
				return;
			}
			if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, FightRadius) ) { Detail = $"fighting on the way to a {Label}"; return; }

			// its quest item on the ground: pick it up
			var item = QuestItemLabels().FirstOrDefault(l => Combat.Distance(l.pos, spot.Pos) <= DropRadius);
			if ( item.label != null ) {
				spot.EmptySince = 0;
				ClickWhenNear(ctx, item.label, item.pos, $"picking up {item.name}");
				return;
			}
			var chest = chests.FirstOrDefault(c => Combat.Distance(c.pos, spot.Pos) < DropRadius);
			if ( chest.ent == null ) {
				Detail = $"to the {Label} at {spot.Pos}, {Combat.Distance(ctx.Grid, spot.Pos):F0} cells away";
				ctx.Driver.WalkTo(Approach(ctx, spot.Pos), $"walking to a {Label}");
				return;
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
				return;
			}
			spot.EmptySince = 0;
			var label = GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Address == chest.ent.Address);
			if ( label == null ) {
				Detail = $"to the {Label}, {Combat.Distance(ctx.Grid, chest.pos):F0} cells away (no label yet)";
				ctx.Driver.WalkTo(Approach(ctx, chest.pos), $"walking to a {Label}");
				return;
			}
			ClickWhenNear(ctx, label, chest.pos, $"opening the {Label}");
		}

		// walk to pos, and click the label once within Reach, or as near as can be walked (at most once a second)
		private void ClickWhenNear(CombatContext ctx, LabelOnGround label, Offsets.Vector2i pos, string what) {
			if ( !IsNear(ctx, pos, Reach, out var approach, out float d) ) {
				Detail = $"{what}, {d:F0} cells away";
				ctx.Driver.WalkTo(approach, what);
				return;
			}
			ctx.Driver.StopWalking(what);
			Detail = what;
			if ( Time.ElapsedMilliseconds - lastClick > 1000 && Ground.TryClickLabel(label) ) lastClick = Time.ElapsedMilliseconds;
		}

		// the chests in range
		private IEnumerable<(Entity ent, Offsets.Vector2i pos)> Chests() {
			foreach ( var e in GetEntities() ) {
				if ( !IsValid(e) || !(e.Path?.Contains(ChestPathPart) ?? false) ) continue;
				var pos = e.GetComponent<Positioned>();
				if ( IsValid(pos) ) yield return (e, pos.GridPos);
			}
		}

		// the quest items on the ground (with a label), and where
		private static IEnumerable<(LabelOnGround label, Offsets.Vector2i pos, string name)> QuestItemLabels() {
			foreach ( var l in GetUI()?.LabelsOnGround?.GetAllLabels() ?? Enumerable.Empty<LabelOnGround>() ) {
				var ground = l.Item;
				var item = ground?.GetComponent<WorldItem>()?.Item;
				if ( !(item?.Path?.Contains("/QuestItems/") ?? false) ) continue;
				var pos = ground.GetComponent<Positioned>();
				if ( IsValid(pos) ) yield return (l, pos.GridPos, Equipment.ItemName(item) ?? item.Path);
			}
		}
	}

	/// <summary>
	/// Click a quest object that unlocks something (eg, The Mud Flats' Strange Glyph Wall, which opens the way to The
	/// Submerged Passage, once the three glyphs are had). Walk toward its tiles (Terrain.TileFeatures) until the object
	/// is in range, then click its label. Done when the thing it unlocks is in range and targetable (sticky).
	///
	/// 3.29.3, The Mud Flats (read live, before unlocking): the wall is Metadata/QuestObjects/WaterCave/GlyphWall
	/// (Targetable 1) at the west of the tiles cut from Metadata/Terrain/Beach/Transitions/BeachToWatercave_v2/Beach_to_watercave_v2.tdt;
	/// the exit beside it, Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition (an AreaTransition), has Targetable 0.
	/// (Assumed: the exit's Targetable goes to 1 when the wall is used; not seen yet.)
	/// </summary>
	public class UseQuestObject : Objective {
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
		public override void Reset() { base.Reset(); clicks = 0; }

		private static Entity Find(string pathPart) => GetEntities().FirstOrDefault(e => IsValid(e) && (e.Path?.Contains(pathPart) ?? false));

		public override bool CheckDone(CombatContext ctx) {
			var unlocked = Find(UnlocksPathPart);
			return unlocked != null && (unlocked.GetComponent<Targetable>()?.IsTargetable ?? false);
		}

		public override void Tick(CombatContext ctx) {
			if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, FightRadius) ) { Detail = $"fighting on the way to the {Label}"; return; }
			var obj = Find(ObjectPathPart);
			var pos = obj?.GetComponent<Positioned>();
			if ( !IsValid(pos) ) {
				var tiles = ctx.Terrain.TileFeatures(TilePathPart).OrderBy(f => Combat.Distance(ctx.Grid, f.GridPos)).FirstOrDefault();
				if ( tiles != null ) {
					Detail = $"to the {Label}, near {tiles.GridPos}, {Combat.Distance(ctx.Grid, tiles.GridPos):F0} cells away";
					ctx.Driver.WalkTo(Approach(ctx, tiles.GridPos), $"walking to the {Label}");
					return;
				}
				Detail = $"looking for the {Label}";
				if ( !ctx.Driver.TryExplore(ctx) ) Detail = $"explored everything without finding the {Label}";
				return;
			}
			var label = GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => l.Item?.Address == obj.Address);
			if ( !IsNear(ctx, pos.GridPos, Reach, out var approach, out float d) || label == null ) {
				Detail = $"to the {Label}, {d:F0} cells away{(label == null ? " (no label yet)" : "")}";
				ctx.Driver.WalkTo(approach, $"walking to the {Label}");
				return;
			}
			ctx.Driver.StopWalking($"using the {Label}");
			Detail = clicks == 0 ? "clicking it" : $"clicked it {clicks} times, waiting for it to unlock";
			if ( Time.ElapsedMilliseconds - lastClick > 2000 && Ground.TryClickLabel(label) ) {
				lastClick = Time.ElapsedMilliseconds;
				clicks += 1;
			}
		}
	}
}
