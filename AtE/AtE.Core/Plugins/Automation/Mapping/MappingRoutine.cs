using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Runs maps from the hideout, over and over. Which part of the loop to do is read from where the player is, so it
	/// can be switched on anywhere:
	///
	///   Run maps
	///     Read the game's key bindings (once: the Portal Scroll and inventory keys)
	///     Run the next map (whichever of these holds, asked again all the time)
	///       in the hideout: Hideout maintenance
	///         Stash the loot (everything in the backpack but maps and scrolls, see KeepInBackpack)
	///         Choose the next map (from the backpack: one with a plan of its own first, see MapPlans)
	///         Open the map (put it in the Map Device, Activate)
	///         Enter the map (a portal the device opened)
	///       in a map: the map's plan (by its name, see MapPlans; the default for a map with none):
	///         Clear the map (until fewer than MonstersLeft monsters remain)
	///         Kill the map boss
	///         Return to the hideout (a Portal Scroll)
	///       anywhere else (a town): go to the hideout (/hideout)
	///
	/// Each area entered starts that part over (Root.Forget): nothing is carried from one map to the next but the
	/// hideout's name. Fighting, looting (rares, uniques, and what is not equipment) and clicking things in passing are
	/// the same as the Campaign's (see GoalRoutine).
	/// </summary>
	public partial class MappingRoutine : GoalRoutine {
		public override string Name => "Mapping";

		/// <summary>The map is cleared once the game says fewer than this many monsters remain (it shows the count from 50 down).</summary>
		public int MonstersLeft = 50;
		/// <summary>Paths (or parts of them) of backpack items not to stash, separated by commas (maps and Portal / Wisdom scrolls are always kept).</summary>
		public string KeepInBackpack = "";
		/// <summary>Only run maps that have a plan of their own (see MapPlans); otherwise any map, those with a plan first.</summary>
		public bool OnlyPlannedMaps = false;

		/// <summary>The hideout's name, seen last time the player was in it (null until then).</summary>
		public string HideoutName { get; private set; }
		/// <summary>The map chosen to run next, by ChooseNextMap: its entity id in the backpack, and its area's name (eg, "Silo").</summary>
		public (uint id, string area)? NextMap { get; internal set; }
		/// <summary>The map area being run (its name), once entered from the hideout; null in the hideout.</summary>
		public string CurrentMap { get; private set; }
		/// <summary>The map portals there were when the map device was last activated (eg, to the map before): not taken.</summary>
		[NoPersist] public readonly HashSet<uint> StalePortals = new HashSet<uint>();
		/// <summary>The map device was activated in this visit to the hideout (see OpenMap).</summary>
		public bool MapLaunched { get; internal set; }
		private bool enteringMap; // a map portal was clicked (see EnteringMap)
		/// <summary>Unique monsters seen dead in this map (by id; see WatchForBossKills).</summary>
		[NoPersist] public readonly HashSet<uint> UniquesKilled = new HashSet<uint>();
		/// <summary>When the last unique monster was seen dead in this map (Time.ElapsedMilliseconds).</summary>
		public long LastUniqueKillAt { get; private set; } = -1000000;
		/// <summary>The monsters remaining, as the map panel shows it (null while it says more than 50, or not read yet; see MapStats).</summary>
		public int? MonstersRemaining { get; private set; }
		/// <summary>The map panel says "Map Complete" (the boss is dead), and since when (Time.ElapsedMilliseconds).</summary>
		public bool MapComplete { get; private set; }
		public long MapCompleteAt { get; private set; } = -1000000;
		private uint mapHash; // the area hash of CurrentMap
		private long lastWatch;

		public MappingRoutine() {
			// equipment only when rare (or unique), or for a slot with nothing in it; everything else (currency, ...)
			Looter.Want = Looter.RareOrEmptySlot;
		}

		protected override Sequence BuildRoot() => new Sequence("Run maps",
			new ReadGameKeys(),
			new FirstOf("Run the next map")
				.When("in the hideout", null, InHideout, Hideout())
				.When("in a map", null, InMap, RunMap())
				.When("not in the hideout or a map", null, () => !InHideout() && !InMap(), new GoToHideout(this))
		);

		private Sequence Hideout() => new Sequence("Hideout maintenance",
			new StashLoot(this),
			new ChooseNextMap(this),
			new AtE.OpenMap(this), // (AtE.: MappingRoutine.OpenMap is a step, see MappingSteps.cs)
			new EnterMap(this));

		// the map's own plan, by its name (see MapPlans), else the default
		private Goal RunMap() {
			var byName = new FirstOf("Run the map");
			plans = MapPlans().ToList();
			foreach ( var (map, plan) in plans ) byName.When(map, map, () => true, plan);
			return byName.When("any other map", null, InMap, DefaultPlan("the map"));
		}

		/// <summary>
		/// What most maps take: clear it (until fewer than MonstersLeft remain), kill its boss, and go back to the hideout.
		/// A map's own plan (see MapPlans) can start from this one's goals.
		/// </summary>
		public Sequence DefaultPlan(string map) => new Sequence($"Run {map}",
			new ClearMap(this),
			new KillMapBoss(this),
			new ReturnToHideout(this));

		// (set by BuildRoot, from the base constructor)
		private List<(string map, Sequence plan)> plans;
		/// <summary>The maps with plans of their own (see MapPlans.cs).</summary>
		public IEnumerable<string> PlannedMaps => plans?.Select(p => p.map) ?? Enumerable.Empty<string>();

		// ---- where the player is ----

		public static bool IsHideout(WorldArea area) => area?.Name != null && Offsets.IsHideout(area.Name);
		public bool InHideout() => IsHideout(GetCurrentArea());

		/// <summary>
		/// A map: the area entered from the hideout's map portals (CurrentMap), or any area whose id says it is one
		/// (WorldAreas' "MapWorlds..." rows; eg, switched on in a map).
		/// </summary>
		public bool InMap() {
			var area = GetCurrentArea();
			if ( area == null || IsHideout(area) || area.IsTown ) return false;
			if ( CurrentMap != null && (PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0) == mapHash ) return true;
			return area.Id?.StartsWith("Map", StringComparison.OrdinalIgnoreCase) ?? false;
		}

		// ---- running ----

		protected override Goal Working(Goal leaf) {
			// (the FirstOfs and Sequences inside the chosen leaf, down to the goal worked on)
			var g = leaf;
			for ( int i = 0; i < 8 && g != null; i++ ) {
				var inner = g is FirstOf rules ? rules.Last : g is Sequence seq ? seq.Current : null;
				if ( inner == null || inner.IsComplete ) break;
				g = inner;
			}
			return g ?? leaf;
		}

		public override IEnumerable<(Sequence parent, Goal goal, int depth)> Leaves() => Expand(Root, 0);

		// a Sequence's goals, and inside a FirstOf, the goals of the one it is on (so the list shows what is being done)
		private IEnumerable<(Sequence parent, Goal goal, int depth)> Expand(Sequence seq, int depth) {
			foreach ( var child in seq.Goals ) {
				if ( child is Sequence inner && !inner.IsComplete ) {
					foreach ( var x in Expand(inner, depth + 1) ) yield return x;
					continue;
				}
				var picked = child is FirstOf rules ? Picked(rules) : null;
				if ( picked is Sequence plan ) { foreach ( var x in Expand(plan, depth + 1) ) yield return x; }
				else if ( picked != null ) yield return (seq, picked, depth);
				else yield return (seq, child, depth);
			}
		}

		// the goal a FirstOf is on, through FirstOfs inside it
		private static Goal Picked(FirstOf rules) {
			var g = rules.Last;
			for ( int i = 0; i < 8 && g is FirstOf inner; i++ ) g = inner.Last ?? g;
			return g == null || g.IsComplete || g is FirstOf ? null : g;
		}

		public override string ProgressSummary {
			get {
				string where = CurrentMap != null ? $"in {CurrentMap}" : InHideout() ? "in the hideout" : "";
				string next = NextMap is { } n && CurrentMap == null ? $", next: {n.area}" : "";
				string left = MapComplete ? ", Map Complete" : MonstersRemaining is int m ? $", {m} monsters remain" : "";
				return $"{MapsRun} maps run{(where.Length > 0 ? $", {where}" : "")}{next}{left}";
			}
		}
		protected override string CompleteText => "(nothing to do here)";

		/// <summary>How many maps were entered since the overlay started.</summary>
		public int MapsRun { get; private set; }

		protected override void OnNewArea() {
			// each area starts its part of the loop over (and a Sequence marked done on leaving the last area is not done here)
			Root.Forget();
			UniquesKilled.Clear();
			LastUniqueKillAt = MapCompleteAt = -1000000;
			MonstersRemaining = null;
			MapComplete = false;
			MapLaunched = false;
			var area = GetCurrentArea();
			bool fromPortal = enteringMap;
			enteringMap = false;
			if ( IsHideout(area) ) {
				HideoutName = area.Name;
				CurrentMap = null;
			} else if ( fromPortal && area != null && !area.IsTown ) EnteredMap(area);
			// (switched on in a map, or entered it some other way: run it all the same)
			else if ( CurrentMap == null && InMap() ) CurrentMap = area.Name;
		}

		/// <summary>Called by EnterMap as it clicks a map portal: the next area entered (not a town or hideout) is the map.</summary>
		internal void EnteringMap() => enteringMap = true;

		// gone through a map portal into area (a new map instance)
		private void EnteredMap(WorldArea area) {
			CurrentMap = area?.Name;
			mapHash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			NextMap = null;
			MapsRun += 1;
			Log($"{Name}: entered {CurrentMap} (map {MapsRun})");
		}

		public override IState OnTick(long dt) {
			WatchTest();
			if ( PoEMemory.IsAttached && IsActive ) WatchMap();
			return base.OnTick(dt);
		}

		/// <summary>How often to look for dead uniques and read the monsters remaining.</summary>
		public long WatchMs = 500;

		// in a map: note unique monsters seen dead, and read the map panel (monsters remaining, Map Complete; see MapStats)
		private void WatchMap() {
			long now = Time.ElapsedMilliseconds;
			if ( now - lastWatch < WatchMs || !InMap() ) return;
			lastWatch = now;
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				if ( !IsMapBoss(e) || UniquesKilled.Contains(e.Id) ) continue;
				var life = e.GetComponent<Life>();
				if ( !IsValid(life) || life.CurHP > 0 ) continue;
				UniquesKilled.Add(e.Id);
				LastUniqueKillAt = now;
				Log($"{Name}: {e.GetComponent<Render>()?.Name ?? e.Path} (unique) is dead");
			}
			var lines = MapStats.Lines().ToList();
			MonstersRemaining = MapStats.MonstersRemaining(lines);
			if ( !MapComplete && MapStats.MapComplete(lines) ) {
				MapComplete = true;
				MapCompleteAt = now;
				Log($"{Name}: {CurrentMap ?? "the map"}: Map Complete");
			}
		}

		/// <summary>
		/// A map boss, for the default plan: a unique monster. Other uniques count too (eg, a
		/// Rogue Exile): a map's own plan names its boss (see MapPlans).
		/// </summary>
		public static bool IsMapBoss(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/")
			&& e.GetComponent<ObjectMagicProperties>()?.Rarity == Offsets.MonsterRarity.Unique;

		// ---- showing it ----

		protected override void RenderSettings() {
			ImGui.SliderInt("Monsters Left", ref MonstersLeft, 0, 50);
			ImGui.SameLine();
			ImGui_HelpMarker("The map counts as cleared once the game says fewer than this many monsters remain (it shows the count from 50 down). 0: kill all of them.");
			ImGui.InputText("Keep In Backpack", ref KeepInBackpack, 500);
			ImGui.SameLine();
			ImGui_HelpMarker("Items not to stash, by a part of their metadata path, separated by commas. Maps, Portal Scrolls and Scrolls of Wisdom are always kept.");
			ImGui.Checkbox("Only Planned Maps", ref OnlyPlannedMaps);
			ImGui.SameLine();
			ImGui_HelpMarker($"Only run maps with a plan of their own ({string.Join(", ", PlannedMaps)}); otherwise any map in the backpack, those first.");
			RenderStepSettings();
			RenderTests();
			ImGui.Text($"Hideout: {HideoutName ?? "(not seen yet)"}; map: {CurrentMap ?? "(none)"}; next: {NextMap?.area ?? "(none)"}");
			ImGui.Text($"Monsters remaining: {(MonstersRemaining is int m ? m.ToString() : "more than 50")}; map complete: {MapComplete}; uniques killed here: {UniquesKilled.Count}");
		}
	}
}
