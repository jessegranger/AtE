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
			/// <summary>Its minimap icons (MinimapIcons.dat ids), by which one far outside the entity list's range is known (see Known).</summary>
			public string[] Icons;
			/// <summary>
			/// Click and hold, not click: keep the button down until Holding says it has begun and HoldMs more (MaxHoldMs at
			/// most in all), then let go (eg, Verisium's "Click and hold to channel"). 0: a plain click.
			/// </summary>
			public uint HoldMs;
			/// <summary>With HoldMs: has what the hold works on begun (eg, the channel)? null: from the first frame held.</summary>
			public Func<Entity, bool> Holding;
			/// <summary>With HoldMs: the longest to hold in all, if Holding never says it has begun.</summary>
			public uint MaxHoldMs = 4000;
			/// <summary>
			/// After a click (or hold) on one, stay by it this long for the monsters it brings, fighting them, and a little
			/// longer while they keep coming (see Interactables.TryStay). 0: go on at once.
			/// </summary>
			public long StayMs;
		}

		public static readonly List<Kind> Kinds = new List<Kind>() {
			new Kind() { Name = "Augury Card (Select a Fate)", PathPart = CombatDriver.PATH_AUGURY_CARD_OBJECT, Used = e => !CombatDriver.IsUnusedAuguryCard(e),
				MaxClicks = 5, ClickMs = 1000, Clicked = driver => driver.FateClicked() },
			new Kind() { Name = "Demonic Evocator", PathPart = "BeyondDemons/BeyondInitiator" },
			new Kind() { Name = "Crafting Recipe", LabelText = "Crafting Recipe" },
			new Kind() { Name = "Voltaxic Sulphite Vein", PathPart = "Delve/Objects/DelveMineralVein" },
			new Kind() { Name = "Shrine", PathPart = "Metadata/Shrines/", Used = e => e.GetComponent<Shrine>() is Shrine s && IsValid(s) && !s.IsAvailable },
			new Kind() { Name = "Grasping Coffers (Breach)", PathPart = "Chests/Breach/BreachBoxChest", Used = e => e.GetComponent<Chest>() is Chest c && IsValid(c) && c.IsOpened },
			// (its minimap icon is "Strongbox", MinimapIcons.dat row 637: seen live 3.30, two on the map out of the entity
			// list's range, one with EntityId 848, the farther one with EntityId 0)
			new Kind() { Name = "Strongbox", PathPart = "Metadata/Chests/StrongBoxes/", Used = IsStrongboxUsed, Icons = new[] { "Strongbox" },
				Blocked = e => AreaObstacles.ChestSpawnsNear(e) ? "monsters a strongbox summoned are alive near it" : null },
			new Kind() { Name = "Sentinel", PathPart = "Metadata/MiscellaneousObjects/Sentinel/",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("interacted", out long done) && done != 0 },
			new Kind() { Name = "Crimson Iron", PathPart = "Settlers/Node/Objects/NodeTypes/", PathEnd = "/CrimsonIron",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("ore_empty", out long empty) && empty != 0 },
			new Kind() { Name = "Bismuth Deposit", PathPart = "Settlers/Node/Objects/NodeTypes/", PathEnd = "/Bismuth",
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("ore_empty", out long empty) && empty != 0 },
			// Verisium (3.30, seen live): the node .../NodeTypes/Verisium exactly, minimap icon "OreTypeVerisium" (MinimapIcons.dat
			// row 644, EntityId 0 in the icon record from afar); label "Click and hold to channel energy into the Verisium outcrop,
			// intensifying the challenge and enriching your reward"; targetable, states activated = 0, in_progress = 1,
			// ore_empty = 0, encounter = 0, begun_channelling = 0, channelling = 0, in_zone = 0 before use.
			// Held, not clicked: until channelling is set, then 2 s more and let go (from the user: a short channel, not a full
			// charge). Not begun_channelling: it stays 1 after a channel ends (seen live: 1 after an earlier hold of no time,
			// channelling 0), so it does not say a channel is going now. Monsters come once the channel ends (from the user):
			// stay and fight them (StayMs).
			new Kind() { Name = "Verisium", PathPart = "Settlers/Node/Objects/NodeTypes/", PathEnd = "/Verisium", Icons = new[] { "OreTypeVerisium" },
				HoldMs = 2000, MaxHoldMs = 5000, ClickMs = 8000, StayMs = 6000,
				Holding = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm) && sm.TryGetState("channelling", out long ch) && ch != 0,
				Used = e => e.GetComponent<StateMachine>() is StateMachine sm && IsValid(sm)
					&& ((sm.TryGetState("activated", out long act) && act != 0) || (sm.TryGetState("ore_empty", out long empty) && empty != 0)) },
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

		/// <summary>Forget the clicks and the known ones (eg, the routine started over).</summary>
		public void Reset() { clicks.Clear(); passedOver.Clear(); known.Clear(); done.Clear(); going = null; staying = null; area = 0; }

		// a new area instance: forget all that was about the last
		private void CheckArea() {
			uint hash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			if ( hash != area ) { area = hash; clicks.Clear(); passedOver.Clear(); known.Clear(); done.Clear(); going = null; staying = null; }
		}

		// every entity (or label's item) of an enabled kind, with its kind and label
		private static IEnumerable<(Entity ent, Kind kind, LabelOnGround label)> Candidates(List<LabelOnGround> labels) {
			var ents = GetEntities()?.ToList() ?? new List<Entity>();
			foreach ( var kind in Kinds.Where(IsEnabled) ) {
				if ( kind.LabelText != null ) {
					foreach ( var l in labels ) {
						if ( (Ground.LabelText(l.Label)?.IndexOf(kind.LabelText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ) yield return (l.Item, kind, l);
					}
				} else {
					foreach ( var e in ents ) {
						if ( e != null && (e.Path?.IndexOf(kind.PathPart, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
							&& (kind.PathEnd == null || e.Path.EndsWith(kind.PathEnd, StringComparison.OrdinalIgnoreCase)) )
							yield return (e, kind, labels.FirstOrDefault(l => l.Item?.Address == e.Address));
					}
				}
			}
		}

		// why e can't be used (again): given up on, not targetable, or used up; else null. A used one's clicks are forgotten.
		private string UsedUp(Entity e, Kind kind) {
			if ( clicks.TryGetValue(e.Id, out int n) && n >= (kind.MaxClicks > 0 ? kind.MaxClicks : MaxClicks) ) return $"clicked {n} times";
			if ( !(e.GetComponent<Targetable>()?.IsTargetable ?? true) ) return "not targetable";
			if ( kind.Used?.Invoke(e) ?? false ) {
				// (the clicks worked: forget them, so one that becomes usable again starts afresh, eg a Strongbox that can
				// be opened a second time, seen live 3.30: it reads opened = 0, started = 0, targetable again)
				clicks.Remove(e.Id);
				return "used";
			}
			return null;
		}

		/// <summary>One known to be in the area and not used yet: its entity seen, or its minimap icon from afar (see Known).</summary>
		public class KnownInteractable {
			public string Kind;
			/// <summary>Its entity's id; 0 when not known (a far minimap icon can have EntityId 0, seen live 3.30).</summary>
			public uint EntityId;
			public Offsets.Vector2i GridPos;
			/// <summary>How it became known: "seen" (its entity) or "minimap icon".</summary>
			public string Source;
			public override string ToString() => $"{Kind} at {GridPos}{(EntityId != 0 ? $", entity {EntityId}" : "")} ({Source})";
		}

		/// <summary>
		/// The ones known in this area instance and not used yet, in the order they became known: any entity of an enabled
		/// kind seen (at any distance), and any minimap icon in a kind's Icons (the icons are known for the whole area, also
		/// out of the entity list's range). Where a routine would only be exploring it walks to the first of these instead
		/// (TryGoToKnown, from CombatDriver.TryExplore; the explorer marks what is passed as explored), and TryUse clicks it
		/// once it is near. One leaves the queue once it is seen used up, or is reached with nothing to use there, or is not
		/// reached in KnownGiveUpMs; one blocked (Kind.Blocked) stays, passed over for the next until it is not.
		/// </summary>
		public IReadOnlyList<KnownInteractable> Known => known;
		/// <summary>Any known ones left to go to (see Known), not counting those blocked for now?</summary>
		public bool HasKnown => known.Any(k => !IsBlocked(k));

		// its entity is in range and its kind says not to click it yet (Kind.Blocked)
		private static bool IsBlocked(KnownInteractable k) => k.EntityId != 0 && EntityCache.TryGetEntity(k.EntityId, out var e) && IsValid(e)
			&& Kinds.FirstOrDefault(kd => kd.Name == k.Kind)?.Blocked?.Invoke(e) != null;
		/// <summary>Within this many cells of a known one's place with nothing to use there: it leaves the queue.</summary>
		public float KnownArriveCells = 12;
		/// <summary>Time to get to a known one before giving up on it.</summary>
		public long KnownGiveUpMs = 45000;
		private readonly List<KnownInteractable> known = new List<KnownInteractable>();
		// those that left the queue (used up, or given up on): not queued again from an icon (from its entity, once usable again)
		private readonly List<(string kind, uint id, Offsets.Vector2i grid)> done = new List<(string, uint, Offsets.Vector2i)>();
		private KnownInteractable going;
		private long goingSince, lastKnownUpdate;
		private const float SamePlaceCells = 4; // (an icon and an entity of the same kind this near are the same one)

		private static bool Same(string kind, uint id, Offsets.Vector2i grid, string kind2, uint id2, Offsets.Vector2i grid2) =>
			kind == kind2 && ((id != 0 && id == id2) || Combat.Distance(grid, grid2) <= SamePlaceCells);

		private void Drop(KnownInteractable k, string why) {
			known.Remove(k);
			done.Add((k.Kind, k.EntityId, k.GridPos));
			if ( going == k ) going = null;
			Log($"Interactables: {k} leaves the queue: {why} ({known.Count} known)");
		}

		/// <summary>Bring Known up to date (twice a second at most, unless force).</summary>
		public void UpdateKnown(bool force = false) {
			CheckArea();
			long now = Time.ElapsedMilliseconds;
			if ( !force && now - lastKnownUpdate < 500 ) return;
			lastKnownUpdate = now;
			var labels = GetUI()?.LabelsOnGround?.GetAllLabels()?.ToList() ?? new List<LabelOnGround>();
			foreach ( var (e, kind, _) in Candidates(labels) ) {
				if ( !IsValid(e) || !(e.GetComponent<Positioned>() is Positioned pos && IsValid(pos)) ) continue;
				var k = known.FirstOrDefault(x => Same(x.Kind, x.EntityId, x.GridPos, kind.Name, e.Id, pos.GridPos));
				if ( UsedUp(e, kind) is string why ) {
					if ( k != null ) Drop(k, why);
					else if ( !done.Any(d => Same(d.kind, d.id, d.grid, kind.Name, e.Id, pos.GridPos)) ) done.Add((kind.Name, e.Id, pos.GridPos));
					continue;
				}
				// (usable: one seen usable again after it was used up, eg a Strongbox opened a second time, is queued again)
				done.RemoveAll(d => Same(d.kind, d.id, d.grid, kind.Name, e.Id, pos.GridPos));
				if ( k == null ) {
					known.Add(k = new KnownInteractable() { Kind = kind.Name, EntityId = e.Id, GridPos = pos.GridPos, Source = "seen" });
					Log($"Interactables: queued the {k} ({known.Count} known)");
				} else { k.EntityId = e.Id; k.GridPos = pos.GridPos; }
			}
			var iconKinds = Kinds.Where(kind => kind.Icons != null && IsEnabled(kind)).ToList();
			if ( iconKinds.Count == 0 ) return;
			foreach ( var icon in MinimapIcons.All() ) {
				var kind = iconKinds.FirstOrDefault(kd => System.Array.IndexOf(kd.Icons, icon.Icon) >= 0);
				if ( kind == null ) continue;
				if ( icon.EntityId != 0 && IsValid(icon.Entity) ) continue; // (its entity is in range: decided above)
				var grid = new Offsets.Vector2i((int)icon.GridPos.X, (int)icon.GridPos.Y);
				if ( done.Any(d => Same(d.kind, d.id, d.grid, kind.Name, icon.EntityId, grid)) ) continue;
				var k = known.FirstOrDefault(x => Same(x.Kind, x.EntityId, x.GridPos, kind.Name, icon.EntityId, grid));
				if ( k == null ) {
					known.Add(k = new KnownInteractable() { Kind = kind.Name, EntityId = icon.EntityId, GridPos = grid, Source = "minimap icon" });
					Log($"Interactables: queued the {k} ({known.Count} known)");
				} else if ( k.EntityId == 0 ) k.EntityId = icon.EntityId;
			}
		}

		/// <summary>
		/// Walk to the first known one (see Known) that is not blocked; false if there is none (so exploring goes on).
		/// Called where a routine would explore (CombatDriver.TryExplore).
		/// </summary>
		public bool TryGoToKnown(CombatDriver driver, CombatContext ctx) {
			if ( !AnyEnabled ) return false;
			UpdateKnown();
			long now = Time.ElapsedMilliseconds;
			foreach ( var k in known.ToList() ) {
				if ( IsBlocked(k) ) continue; // (eg, a strongbox while monsters a box summoned live)
				Entity e = k.EntityId != 0 && EntityCache.TryGetEntity(k.EntityId, out var ent) && IsValid(ent) ? ent : null;
				float d = Combat.Distance(ctx.Grid, k.GridPos);
				if ( d <= KnownArriveCells ) {
					// (here, and TryUse, which runs before exploring, did not take it: nothing to use here)
					Drop(k, e == null ? "reached, and nothing there" : "reached, and not usable");
					continue;
				}
				if ( going != k ) { going = k; goingSince = now; Log($"Interactables: going to the {k}, {d:F0} cells away ({known.Count} known)"); }
				if ( now - goingSince > KnownGiveUpMs ) { Drop(k, $"not reached in {KnownGiveUpMs} ms ({d:F0} cells away)"); continue; }
				if ( !DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, k.GridPos, out var spot) ) { Drop(k, "can't be walked to"); continue; }
				Status = $"going to a {k.Kind} at {k.GridPos}, {d:F0} cells away ({known.Count} known)";
				driver.WalkTo(spot, Status);
				return true;
			}
			return false;
		}

		/// <summary>The queue (Known) as text, first first (brought up to date now).</summary>
		public string KnownText() {
			UpdateKnown(force: true);
			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"{known.Count} known interactables, first first:");
			foreach ( var k in known ) sb.AppendLine("  " + k);
			if ( done.Count > 0 ) sb.AppendLine($"{done.Count} used up or given up: " + string.Join(", ", done.Select(d => $"{d.kind} at {d.grid}")));
			return sb.ToString();
		}

		/// <summary>The one to use next: the nearest, not used, not given up on, that can be walked to; null if none.</summary>
		private (Entity ent, Kind kind, LabelOnGround label, Offsets.Vector2i grid, float distance) Next(CombatContext ctx) {
			var labels = GetUI()?.LabelsOnGround?.GetAllLabels()?.ToList() ?? new List<LabelOnGround>();
			var found = new List<(Entity, Kind, LabelOnGround, Offsets.Vector2i, float)>();
			void Consider(Entity e, Kind kind, LabelOnGround label) {
				if ( !IsValid(e) ) return;
				string why = null;
				var pos = e.GetComponent<Positioned>();
				float d = IsValid(pos) ? Combat.Distance(ctx.Grid, pos.GridPos) : float.MaxValue;
				if ( UsedUp(e, kind) is string usedUp ) why = usedUp;
				else if ( kind.Blocked?.Invoke(e) is string blocked ) why = blocked;
				else if ( !IsValid(pos) ) why = "no position";
				else if ( d > MaxDistance ) why = null; // (too far: not said, it comes nearer)
				else if ( !DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, pos.GridPos, out _, 15) ) why = "can't be walked to";
				else { found.Add((e, kind, label, pos.GridPos, d)); passedOver.Remove(e.Id); return; }
				if ( why != null && passedOver.Add(e.Id) ) Log($"Interactables: passing over the {kind.Name} {e.Id} at {(IsValid(pos) ? pos.GridPos.ToString() : "?")}: {why}");
			}
			foreach ( var (e, kind, label) in Candidates(labels) ) Consider(e, kind, label);
			return found.OrderBy(f => f.Item5).FirstOrDefault();
		}

		/// <summary>Work on using the nearest one (walking to it, clicking it); false if there is none.</summary>
		public bool TryUse(CombatDriver driver, CombatContext ctx) {
			UpdateKnown();
			if ( TryStay(driver, ctx) ) return true;
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
			IState click = null;
			if ( kind.HoldMs > 0 ) {
				// (held: where a click would go, its label's middle else where it stands)
				if ( Ground.TryGetClickPoint(ent, out var at) && PathFollower.IsSafeToClick(at) ) {
					var held = ent;
					click = Inputs.HoldClick(at, () => kind.Holding?.Invoke(held) ?? true, kind.HoldMs, Math.Max(kind.MaxHoldMs, kind.HoldMs));
				}
			} else {
				click = label != null ? Ground.ClickLabel(label) : null;
				click ??= Ground.ClickEntity(ent);
			}
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
				Log($"{driver.Name}: {(kind.HoldMs > 0 ? "holding a click on" : "clicked")} the {kind.Name} {ent.Id} at {grid} (click {clicks[ent.Id]})");
				if ( kind.StayMs > 0 ) { staying = kind; stayPending = true; }
			}
			return true;
		}

		// after a click on a kind with StayMs: the kind, whether the click (a hold) is still going out, and when to stop waiting
		private Kind staying;
		private bool stayPending;
		private long stayUntil;
		/// <summary>Staying for a kind's monsters (Kind.StayMs): each time monsters are near, wait at least this much longer once they are gone.</summary>
		public long StayAfterFightMs = 2000;

		/// <summary>
		/// After a click on a kind with StayMs, stay by it (StayMs after the click or hold ended) for the monsters it brings:
		/// stand still while none are near; with one within the driver's EngageRadius, hand back (false) so it is fought
		/// (the goal's fighting), and wait StayAfterFightMs more once they are gone (for the next wave). True while standing.
		/// </summary>
		private bool TryStay(CombatDriver driver, CombatContext ctx) {
			if ( staying == null ) return false;
			long now = Time.ElapsedMilliseconds;
			if ( stayPending ) {
				if ( driver.InputBusy ) return true; // (the hold is still going out)
				stayPending = false;
				stayUntil = now + staying.StayMs;
				Log($"Interactables: staying by the {staying.Name} up to {staying.StayMs} ms for its monsters");
			}
			if ( ctx.Hostiles.Any(h => h.Distance <= driver.EngageRadius) ) {
				stayUntil = Math.Max(stayUntil, now + StayAfterFightMs);
				return false; // (fight them)
			}
			if ( now < stayUntil ) {
				Status = $"waiting by the {staying.Name} for its monsters ({(stayUntil - now) / 1000 + 1}s)";
				driver.StopWalking(Status);
				return true;
			}
			Log($"Interactables: done staying by the {staying.Name}");
			staying = null;
			return false;
		}
	}
}
