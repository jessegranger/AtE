using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Put a map that is a quest item in a map device and go through the portal it opens (eg, Essence of the Artist:
	/// Maligaro's Map in his Map Device in The Chamber of Sins Level 1, to Maligaro's Sanctum). Walk to the device's label
	/// (LabelText; looked for at SearchAt, eg the waypoint) and click it; once its window is open (with the inventory),
	/// MapDevice.PlanEnterMapFromInventory puts the map in, clicks Activate, and takes the new portal. Done once in Arrives.
	/// TODO: confirm live (not seen yet): the device's label, that clicking it opens the inventory too, the map's name
	/// </summary>
	public class UseMapDevice : Goal {
		public readonly string LabelText, MapName, Arrives;
		public float Reach = 12;
		private long lastClick, lastPlan;

		/// <param name="labelText">The device's label on the ground (eg, "Map Device").</param>
		/// <param name="mapName">The map's name (eg, "Maligaro's Map"): its item name has it, or its path has it without spaces and apostrophes.</param>
		/// <param name="arrives">The area the portal goes to (eg, "Maligaro's Sanctum").</param>
		public UseMapDevice(string labelText, string mapName, string arrives) { LabelText = labelText; MapName = mapName; Arrives = arrives; }
		public override string Name => $"Use {MapName} in the {LabelText}";
		public override bool Sticky => false;
		public override bool UsesInventory => true; // (the device's window comes with the inventory)
		public override void Reset() { base.Reset(); lastClick = lastPlan = 0; }
		public override bool CheckDone(CombatContext ctx) => GoToArea.Is(GetCurrentArea(), Arrives);

		private bool IsMap(InventoryItem i) => i?.Entity is Entity e
			&& ((Equipment.ItemName(e)?.Contains(MapName, StringComparison.OrdinalIgnoreCase) ?? false)
				|| (e.Path?.Contains(MapName.Replace(" ", "").Replace("'", ""), StringComparison.OrdinalIgnoreCase) ?? false));

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( MapDeviceIsOpen() ) {
				ctx.Driver.StopWalking("using the map device");
				if ( !BackpackIsOpen() ) {
					if ( now - lastClick < 1000 ) return this;
					lastClick = now;
					Detail = "opening the inventory";
					return Inputs.Press(VKey.I, this);
				}
				var map = BackpackItems().FirstOrDefault(IsMap);
				if ( map == null ) { Detail = $"no {MapName} in the backpack"; return this; }
				// (the plan clicks the map, Activate and the portal; if it fails part way, it ends, and this goal is run again)
				if ( now - lastPlan < 5000 ) return this;
				lastPlan = now;
				Detail = $"putting {MapName} in it, and taking the portal";
				return MapDevice.PlanEnterMapFromInventory(map, this) ?? this;
			}
			var label = GetUI()?.LabelsOnGround?.GetAllLabels()?
				.FirstOrDefault(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), LabelText, StringComparison.OrdinalIgnoreCase));
			var pos = label?.Item?.GetComponent<Positioned>();
			if ( label == null || !IsValid(pos) ) return Search(ctx, $"the {LabelText}");
			if ( !IsNear(ctx, pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"{d:F0} cells away";
				return GoTo(LabelText, pos.GridPos, Reach);
			}
			if ( FightFirst(ctx) is IState fight ) return fight;
			ctx.Driver.StopWalking($"opening the {LabelText}");
			if ( now - lastClick < 2000 || !(Ground.ClickLabel(label, this) is IState click) ) return this;
			lastClick = now;
			Detail = $"clicking the {LabelText}";
			return click;
		}
	}

	/// <summary>
	/// Open the area's quest objects and pick up what they drop (eg, Lighting the Way: the 7 Fireflies in The Dread
	/// Thicket). They are found from the area's minimap icons (MinimapIcons.QuestObjects: the whole area's, near or far),
	/// so it walks to each in turn, nearest of those that can be walked to first, fighting on the way. At one: click its
	/// label, then pick up the quest item that drops. One is done once it is open (a Chest's IsOpened, else not targetable)
	/// and nothing has dropped for SettleMs, or after GiveUpMs at it with nothing to click. Any quest item on the ground
	/// near the player is picked up first (its icon may be gone once it is open). Done with Count of ItemName in the
	/// backpack, or every icon done.
	/// TODO: confirm live (not seen yet): the Fireflies' item name and whether one stacks, that each icon is one Firefly
	/// </summary>
	public class OpenQuestObjects : Goal {
		public readonly string ItemName;
		public readonly int Count;
		public float Reach = 12;
		public float FightRadius = 60;
		/// <summary>A quest item this near the player is picked up.</summary>
		public float DropRadius = 30;
		public long SettleMs = 1500;
		/// <summary>At an icon this long with nothing to click (no entity or label): it is given up.</summary>
		public long GiveUpMs = 5000;
		private readonly HashSet<uint> done = new HashSet<uint>();
		private uint at;
		private long atSince, emptySince, lastClick;
		private bool anySeen;

		/// <param name="itemName">What each drops (eg, "Firefly"): its item name has it, or its path has it without spaces.</param>
		public OpenQuestObjects(string itemName, int count) { ItemName = itemName; Count = count; }
		public override string Name => $"Open the quest objects, and pick up {Count} {ItemName} ({Had()} had)";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); done.Clear(); at = 0; atSince = emptySince = 0; anySeen = false; }

		private int Had() => BackpackItems().Count(i => i.Entity?.Path is string path && path.Contains("/QuestItems/")
			&& ((Equipment.ItemName(i.Entity)?.Contains(ItemName, StringComparison.OrdinalIgnoreCase) ?? false)
				|| path.Contains(ItemName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)));

		private static Offsets.Vector2i Grid(MinimapIconMarker m) => new Offsets.Vector2i((int)m.GridPos.X, (int)m.GridPos.Y);

		private List<MinimapIconMarker> Left(CombatContext ctx) => MinimapIcons.QuestObjects()
			.Where(m => !done.Contains(m.EntityId) && DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, Grid(m), out _, 25))
			.ToList();

		public override bool CheckDone(CombatContext ctx) => Had() >= Count || (anySeen && Left(ctx).Count == 0);

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			// a quest item near: pick it up first
			var drop = OpenQuestChests.QuestItemLabels().Where(l => Combat.Distance(ctx.Grid, l.pos) <= DropRadius)
				.OrderBy(l => Combat.Distance(ctx.Grid, l.pos)).FirstOrDefault();
			if ( drop.label != null ) {
				emptySince = 0;
				return ClickWhenNear(ctx, drop.label, drop.pos, $"picking up {drop.name}");
			}
			var left = Left(ctx);
			if ( left.Count == 0 ) {
				Detail = "no quest objects known yet: exploring";
				return Call(new Explore("the quest objects") { FightRadius = FightRadius });
			}
			anySeen = true;
			if ( FightFirst(ctx, FightRadius) is IState fight ) { Detail = "fighting on the way"; return fight; }
			var marker = left.OrderBy(m => Combat.Distance(ctx.Grid, Grid(m))).First();
			if ( marker.EntityId != at ) { at = marker.EntityId; atSince = 0; emptySince = 0; }
			var pos = Grid(marker);
			var ent = marker.Entity;
			var label = ent == null ? null : GetUI()?.LabelsOnGround?.GetAllLabels()?.FirstOrDefault(l => l.Item?.Address == ent.Address);
			bool opened = ent != null && (IsValid(ent.GetComponent<Chest>()) ? ent.GetComponent<Chest>().IsOpened
				: !(ent.GetComponent<Targetable>()?.IsTargetable ?? true));
			if ( opened ) {
				if ( emptySince == 0 ) emptySince = now;
				ctx.Driver.StopWalking("waiting for a drop");
				Detail = "open, waiting for its drop";
				if ( now - emptySince >= SettleMs ) Finish(marker, "open");
				return this;
			}
			emptySince = 0;
			if ( label == null ) {
				if ( IsNear(ctx, pos, Reach, out _, out _) ) {
					if ( atSince == 0 ) atSince = now;
					if ( now - atSince > GiveUpMs ) { Finish(marker, "nothing to click there"); return this; }
					ctx.Driver.StopWalking("no label yet");
					Detail = "there, no label yet";
					return this;
				}
				Detail = $"to the quest object at {pos}, {Combat.Distance(ctx.Grid, pos):F0} cells away ({left.Count} left)";
				return Call(new Navigate("a quest object", c => Approach(c, pos), 5) { FightRadius = FightRadius, LegMs = 1000 });
			}
			return ClickWhenNear(ctx, label, ent.GetComponent<Positioned>()?.GridPos ?? pos, $"opening the quest object at {pos}");
		}

		private void Finish(MinimapIconMarker marker, string why) {
			done.Add(marker.EntityId);
			Log($"Campaign: {Name}: done with the quest object at <{marker.GridPos.X}, {marker.GridPos.Y}> ({why})");
		}

		// walk to pos, and click the label once within Reach (at most once a second)
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
	}
}
