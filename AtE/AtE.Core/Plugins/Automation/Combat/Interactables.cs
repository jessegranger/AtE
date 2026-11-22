using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Things out in an area worth clicking whenever a routine passes them (see CombatDriver.TryInteract): walk near the
	/// nearest within MaxDistance (that can be walked to), click it (its label, else the entity), and go on. One is used
	/// once it is no longer targetable, its label is gone, or its kind says so (Kind.Used); one clicked MaxClicks times
	/// and still not used is left until the next area instance.
	///
	/// The kinds (Kinds; add more there):
	///  - an Augury Card: "Select a Fate" on an unused Metadata/Terrain/Leagues/Augury/Objects/AuguryCardObject (see
	///    CombatDriver.IsUnusedAuguryCard), clicked until its window opens (CombatDriver.TryAugury then chooses a card;
	///    nothing is clicked while the window is open, see CombatDriver.TryInteract)
	///  - the Demonic Evocator: Metadata/Monsters/BeyondDemons/BeyondInitiator (3.30, seen live: no name, its label the
	///    text "Interact to cause slaying enemies close together to attract monsters from Beyond this realm"; it has
	///    InteractionAction, Targetable, StateMachine, Transitionable)
	///  - a Crafting Recipe: its label "Crafting Recipe"
	///  - a Voltaxic Sulphite Vein: Metadata/Terrain/Leagues/Delve/Objects/DelveMineralVein (3.30, seen live: three in an
	///    area, each with the label "Interact to acquire Voltaxic Sulphite, used within the Azurite Mine"; InteractionAction,
	///    StateMachine, Targetable; once used, Targetable.IsTargetable goes 1 -> 0 and its label is gone, seen live)
	///  - a Shrine: Metadata/Shrines/..., used once its Shrine component says it is taken
	///  - Grasping Coffers: Metadata/Chests/Breach/BreachBoxChestEstablishedThreads (3.30, seen live: four in one area, with
	///    Chest, InteractionAction, StateMachine, Targetable); used once Chest.IsOpened
	///  - a Strongbox: Metadata/Chests/StrongBoxes/... (3.30, seen live: .../Ornate, with Chest, InteractionAction,
	///    StateMachine (states opened, started; both 0 unopened), ObjectMagicProperties, Targetable; Chest.IsOpened 0).
	///    Used once Chest.IsOpened or either state is set: a click sets started 0 -> 1 (seen live; opened stayed 0, and
	///    it stayed targetable). One with a mod in AvoidSpam.StrongboxModHazards (eg, ChestIceNova) is opened all the
	///    same: the danger grid then marks the ground around it for a few seconds, so the routine flees and comes back
	///  - a Sentinel (eg, "Obsidian Apex Sentinel"): Metadata/MiscellaneousObjects/Sentinel/SentinelRandomEncounterObject
	///    (3.30, seen live: InteractionAction, StateMachine (interacted = 0 unused), Targetable, MinimapIcon, Functions,
	///    HeistRewardDisplay); any under .../Sentinel/. Used once interacted is set: a click sets it 0 -> 1 and Targetable
	///    1 -> 0 (seen live). The drone button it brings up is pressed by CombatDriver.TryActivateSentinelDrone
	///  - a Crimson Iron ore node: Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/CrimsonIron exactly (3.30, seen
	///    live: InteractionAction, Targetable, StateMachine, MinimapIcon, ControlZone). Its decorations
	///    .../NodeTypes/CrimsonIronObjects/SmallGrowth (only BaseEvents, Positioned) share the prefix, hence PathEnd.
	///    Once clicked (seen live): Targetable.IsTargetable 1 -> 0, StateMachine in_progress = 1, growths_alive = 4,
	///    interactable = 0, activated = 0, ore_empty = 0; its CrimsonIron/SmallGrowthMaps "Corrupted Growth" monsters
	///    come alive and are fought like any others. Not clicked while untargetable; used once ore_empty is set.
	///    TODO: confirm live what happens once the growths are dead (whether it goes targetable again to claim the
	///    iron, and ore_empty)
	///  - a Bismuth Deposit ore node: .../Settlers/Node/Objects/NodeTypes/Bismuth exactly (3.30.0d, seen live: label
	///    "Interact to mark this node for mining / Unleashes Elemental energy into the area"; InteractionAction, Targetable,
	///    StateMachine (activated, in_progress, ore_empty, effect_off), MinimapIcon, ControlZone, a buff bismuth_epk; its
	///    BismuthMineralTornadoFire/Cold/Lightning invisible monsters near). Used once ore_empty is set, as Crimson Iron.
	///    TODO: confirm live what changes once clicked
	/// TODO: confirm live: the Crafting Recipe's entity and what changes once it is used; a shrine's path, label, and
	/// Shrine.IsTaken (not seen yet); what the Evocator's state is once used (here: Targetable going false, or its label)
	/// </summary>
	public class Interactables {
		public class Kind {
			public string Name;
			/// <summary>A part of the entity's metadata path (null: matched by LabelText).</summary>
			public string PathPart;
			/// <summary>With PathPart: the path must also end with this (eg, the Crimson Iron node, not its CrimsonIronObjects/...).</summary>
			public string PathEnd;
			/// <summary>A part of its label's text (null: matched by PathPart).</summary>
			public string LabelText;
			/// <summary>Used up, from the entity itself (eg, a shrine taken); null: by Targetable and the label only.</summary>
			public Func<Entity, bool> Used;
			/// <summary>Clicks on one before it is given up on; 0: Interactables.MaxClicks.</summary>
			public int MaxClicks;
			/// <summary>Time for a click to work before clicking again; 0: Interactables.ClickMs.</summary>
			public long ClickMs;
			/// <summary>Called after each click sent on one (eg, so the window it opens is known to be its own).</summary>
			public Action<CombatDriver> Clicked;
			/// <summary>Why not to click it yet (eg, a strongbox while monsters a box summoned are alive), else null; not used up.</summary>
			public Func<Entity, string> Blocked;
		}

		public static readonly List<Kind> Kinds = new List<Kind>() {
			new Kind() { Name = "Augury Card (Select a Fate)", PathPart = CombatDriver.PATH_AUGURY_CARD_OBJECT, Used = e => !CombatDriver.IsUnusedAuguryCard(e),
				MaxClicks = 5, ClickMs = 1000, Clicked = driver => driver.FateClicked() },
			new Kind() { Name = "Demonic Evocator", PathPart = "BeyondDemons/BeyondInitiator" },
			new Kind() { Name = "Crafting Recipe", LabelText = "Crafting Recipe" },
			new Kind() { Name = "Voltaxic Sulphite Vein", PathPart = "Delve/Objects/DelveMineralVein" },
			new Kind() { Name = "Shrine", PathPart = "Metadata/Shrines/", Used = e => e.GetComponent<Shrine>() is Shrine s && IsValid(s) && !s.IsAvailable },
			new Kind() { Name = "Grasping Coffers (Breach)", PathPart = "Chests/Breach/BreachBoxChest", Used = e => e.GetComponent<Chest>() is Chest c && IsValid(c) && c.IsOpened },
			new Kind() { Name = "Strongbox", PathPart = "Metadata/Chests/StrongBoxes/", Used = IsStrongboxUsed,
				Blocked = e => AreaObstacles.ChestSpawnsNear(e) ? "monsters a strongbox summoned are alive near it" : null },
			new Kind() { Name = "Sentinel", PathPart = "Metadata/MiscellaneousObjects/Sentinel/",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("interacted", out long done) && done != 0 },
			new Kind() { Name = "Crimson Iron", PathPart = "Settlers/Node/Objects/NodeTypes/", PathEnd = "/CrimsonIron",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("ore_empty", out long empty) && empty != 0 },
			new Kind() { Name = "Bismuth Deposit", PathPart = "Settlers/Node/Objects/NodeTypes/", PathEnd = "/Bismuth",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("ore_empty", out long empty) && empty != 0 },
			// Orichalcum Ore (3.30, seen live): the node .../NodeTypes/DemonCopper exactly (its parts are DemonCopperObjects/...);
			// once clicked it read activated = 2, in_progress = 1, ore_empty = 0 (encounter, type, lights too) and was not
			// targetable. Then its DemonCopperObjects/copper_altar is clicked (label "Interact to begin the encounter.",
			// targetable, states encounter = 0, type = 1, claimed = 0 before; after its click encounter = 1, not targetable, and
			// the node's encounter = 1 too), and the monsters that come are fought as any
			new Kind() { Name = "Orichalcum Ore", PathPart = "Settlers/Node/Objects/NodeTypes/", PathEnd = "/DemonCopper",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm)
					&& ((sm.TryGetState("activated", out long act) && act != 0) || (sm.TryGetState("ore_empty", out long empty) && empty != 0)) },
			new Kind() { Name = "Orichalcum Altar", PathPart = "Settlers/Node/Objects/NodeTypes/DemonCopperObjects/copper_altar",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm)
					&& ((sm.TryGetState("encounter", out long enc) && enc != 0) || (sm.TryGetState("claimed", out long claimed) && claimed != 0)) },
			// Harvest: the tree that starts the encounter (HarvestEncounterTreePurple, and its Yellow/Blue colours), seen 3.30
			// targetable with the one state active = 0 before it was clicked, active = 1 and not targetable after (its seeds then
			// grow), 2 once monsters are out (ClearMap.WaitForHarvest waits for them); its roots are HarvestRootFissure* (not clickable)
			new Kind() { Name = "Harvest Tree", PathPart = "Leagues/Harvest/Objects/HarvestEncounterTree",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("active", out long active) && active != 0 },
		};

		/// <summary>A strongbox is used once opened, or started (its monsters come while it opens: don't click it again). Some
		/// can be opened again: seen live (3.30) a box opened once read opened = 0, started = 0, Chest.IsOpened = 0 and targetable
		/// again (the same as a fresh box), so it is clicked again (its clicks are forgotten once it reads used, see Next).</summary>
		private static bool IsStrongboxUsed(Entity e) {
			if ( e.GetComponent<Chest>() is Chest c && IsValid(c) && c.IsOpened ) return true;
			return e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm)
				&& ((sm.TryGetState("opened", out long opened) && opened != 0) || (sm.TryGetState("started", out long started) && started != 0));
		}

		/// <summary>Is the kind switched on (Combat's Interactables settings, see CombatSection)?</summary>
		public static bool IsEnabled(Kind kind) => GetPlugin<AutomationPlugin>()?.Combat.IsInteractableEnabled(kind.Name) ?? true;
		/// <summary>Is any kind switched on?</summary>
		public static bool AnyEnabled => Kinds.Any(IsEnabled);

		/// <summary>Leave ones further than this many cells away.</summary>
		public float MaxDistance = 60;
		/// <summary>Click when within this many cells (the click walks the rest of the way).</summary>
		public float Reach = 15;
		/// <summary>Click its label when within this many cells and the label's middle is on screen (the game walks the rest).</summary>
		public float LabelReach = 20;
		/// <summary>Clicks on one before it is given up on (for this area instance).</summary>
		public int MaxClicks = 3;
		/// <summary>Time for a click to work before clicking the same one again.</summary>
		public long ClickMs = 1500;

		/// <summary>What it is doing, for the status line.</summary>
		public string Status { get; private set; } = "";

		private uint area;
		private readonly Dictionary<uint, int> clicks = new Dictionary<uint, int>(); // entity id: clicks, in this area instance
		private readonly HashSet<uint> passedOver = new HashSet<uint>(); // (logged once each, in this area instance)
		private long lastClick;

		/// <summary>Forget the clicks (eg, the routine started over).</summary>
		public void Reset() { clicks.Clear(); passedOver.Clear(); area = 0; }

		/// <summary>The one to use next: the nearest, not used, not given up on, that can be walked to; null if none.</summary>
		private (Entity ent, Kind kind, LabelOnGround label, Offsets.Vector2i grid, float distance) Next(CombatContext ctx) {
			var labels = GetUI()?.LabelsOnGround?.GetAllLabels()?.ToList() ?? new List<LabelOnGround>();
			var found = new List<(Entity, Kind, LabelOnGround, Offsets.Vector2i, float)>();
			void Consider(Entity e, Kind kind, LabelOnGround label) {
				if ( !IsValid(e) ) return;
				string why = null;
				var pos = e.GetComponent<Positioned>();
				float d = IsValid(pos) ? Combat.Distance(ctx.Grid, pos.GridPos) : float.MaxValue;
				if ( clicks.TryGetValue(e.Id, out int n) && n >= (kind.MaxClicks > 0 ? kind.MaxClicks : MaxClicks) ) why = $"clicked {n} times";
				else if ( !(e.GetComponent<Targetable>()?.IsTargetable ?? true) ) why = "not targetable";
				else if ( kind.Used?.Invoke(e) ?? false ) {
					// (the clicks worked: forget them, so one that becomes usable again starts afresh, eg a Strongbox that can
					// be opened a second time, seen live 3.30: it reads opened = 0, started = 0, targetable again)
					clicks.Remove(e.Id);
					why = "used";
				}
				else if ( kind.Blocked?.Invoke(e) is string blocked ) why = blocked;
				else if ( !IsValid(pos) ) why = "no position";
				else if ( d > MaxDistance ) why = null; // (too far: not said, it comes nearer)
				else if ( !DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, pos.GridPos, out _, 15) ) why = "can't be walked to";
				else { found.Add((e, kind, label, pos.GridPos, d)); passedOver.Remove(e.Id); return; }
				if ( why != null && passedOver.Add(e.Id) ) Log($"Interactables: passing over the {kind.Name} {e.Id} at {(IsValid(pos) ? pos.GridPos.ToString() : "?")}: {why}");
			}
			foreach ( var kind in Kinds.Where(IsEnabled) ) {
				if ( kind.LabelText != null ) {
					foreach ( var l in labels ) {
						if ( (Ground.LabelText(l.Label)?.IndexOf(kind.LabelText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ) Consider(l.Item, kind, l);
					}
				} else {
					foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
						if ( e != null && (e.Path?.IndexOf(kind.PathPart, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
							&& (kind.PathEnd == null || e.Path.EndsWith(kind.PathEnd, StringComparison.OrdinalIgnoreCase)) )
							Consider(e, kind, labels.FirstOrDefault(l => l.Item?.Address == e.Address));
					}
				}
			}
			return found.OrderBy(f => f.Item5).FirstOrDefault();
		}

		/// <summary>Work on using the nearest one (walking to it, clicking it); false if there is none.</summary>
		public bool TryUse(CombatDriver driver, CombatContext ctx) {
			uint hash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			if ( hash != area ) { area = hash; clicks.Clear(); passedOver.Clear(); }
			var (ent, kind, label, grid, d) = Next(ctx);
			if ( ent == null ) return false;
			// (not when its label is within LabelReach and the middle of the label is on screen: an Augury Card on ground the
			// player can't walk onto was walked to forever, never within Reach; clicking its label, the game walks the rest)
			bool labelClickable = label != null && d <= LabelReach && Ground.ClickLabel(label) != null;
			if ( d > Reach && !labelClickable ) {
				var spot = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, grid, out var s) ? s : grid;
				Status = $"walking to a {kind.Name}, {d:F0} cells away";
				driver.WalkTo(spot, Status);
				return true;
			}
			Status = $"using a {kind.Name}";
			driver.StopWalking(Status);
			if ( driver.InputBusy || Time.ElapsedMilliseconds - lastClick < (kind.ClickMs > 0 ? kind.ClickMs : ClickMs) ) return true;
			var click = label != null ? Ground.ClickLabel(label) : null;
			click ??= Ground.ClickEntity(ent);
			if ( click == null ) {
				Status = $"walking to a {kind.Name} (can't click it from here)";
				driver.WalkTo(grid, Status);
				return true;
			}
			DefaultFollower.ReleaseMouse();
			if ( driver.Send(click) ) {
				lastClick = Time.ElapsedMilliseconds;
				clicks[ent.Id] = clicks.TryGetValue(ent.Id, out int n) ? n + 1 : 1;
				kind.Clicked?.Invoke(driver);
				Log($"{driver.Name}: clicked the {kind.Name} {ent.Id} at {grid} (click {clicks[ent.Id]})");
			}
			return true;
		}
	}
}
