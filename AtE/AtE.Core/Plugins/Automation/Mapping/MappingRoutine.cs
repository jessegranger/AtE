using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Runs maps from the hideout, over and over:
	///
	///   Run maps
	///     Read the game's key bindings (once: the Portal Scroll and inventory keys)
	///     Run the next map (whichever of these holds)
	///       not in the hideout or a map (a town): go to the hideout (/hideout)
	///       in the hideout or a map: the map loop (MapLoop, see MappingLoop.cs), lap after lap:
	///         StashEverything, PickNextMap, RollMap, OpenMap (Activate, EnterMap, WaitForNewArea)
	///         ClearArea (the map's MapRules, then clear until fewer than MonstersLeft remain and "Map Complete")
	///         ReturnHome (a Portal Scroll, its portal, WaitForNewArea)
	///
	/// Switched on in a map, the lap starts at ClearArea (SkipToClear). The loop's chain is kept across areas by MapLoop,
	/// and when it stops (a step that can't go on, with a log line) the routine is switched off. Fighting, looting (rares,
	/// uniques, and what is not equipment) and clicking things in passing are the same as the Campaign's (see GoalRoutine),
	/// in maps.
	/// </summary>
	public partial class MappingRoutine : GoalRoutine {
		public override string Name => "Mapping";

		/// <summary>The map is cleared once the game says fewer than this many monsters remain (it shows the count from 50 down).</summary>
		public int MonstersLeft = 50;

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
			// everything, normal and magic equipment too (it is all stashed, see StashEverything; the Campaign's
			// RareOrEmptySlot rule, equipment only when rare or for an empty slot, is not for here); not life and mana
			// flasks (taken in the Campaign for the FlaskUpgrader, which does not run here)
			Looter.Want = item => item == null || !FlaskUpgrader.TryRecovery(item, out _, out _);
		}

		// (set by BuildRoot, from the base constructor)
		private MapLoop mapLoop;

		protected override Sequence BuildRoot() {
			mapLoop = new MapLoop(this);
			return new Sequence("Run maps",
				new ReadGameKeys(),
				new FirstOf("Run the next map")
					.When("not in the hideout or a map", null, () => !InHideout() && !InMap(), new GoToHideout(this))
					.When("in the hideout or a map", null, () => true, mapLoop)
			);
		}

		// (flask upgrades are for the Campaign)
		protected override bool UpgradesFlasks => false;

		// switched on: a new lap (see MapLoop)
		protected override void OnStartRun() { LastFailure = null; mapLoop?.Restart(); }

		// (MapLoop, as it builds the first lap) switched on in a map: start the lap at ClearArea
		internal void StartLoop() {
			SkipToClear = InMap();
			// (switched on in the hideout with a map's portals all up: they are a map not run yet, entered at once)
			// (or, after leaving a map while clearing it, eg dying, any of its portals that are left)
			int need = ReEnter ? 1 : MapPortalCount;
			ReEnter = false;
			Clearing = false;
			SkipToPortals = !SkipToClear && InHideout() && MapDevice.MapPortals().Count() >= need;
			Log($"{Name}: the map loop starts {(SkipToClear ? $"in {GetCurrentArea()?.Name ?? "a map"}: clearing it" : SkipToPortals ? $"in the hideout, with {MapDevice.MapPortals().Count()} map portals up: entering the nearest" : "in the hideout")}");
		}

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
			// the goal inside the map loop's chain (eg, ClearMap), while no input of its own is going out
			if ( g is MapLoop loop && loop.Current is CampaignState && loop.ActiveGoal is Goal active ) return active;
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
		/// <summary>How many maps ClearArea finished (objectives done) since the overlay started.</summary>
		public int MapsCompleted { get; private set; }

		/// <summary>
		/// A report for the memory server's /mapping/status: the routine (on or off, chosen, its status, the test), the
		/// character (level, XP, life, energy shield, mana), maps entered and completed, the area, and where the loop is
		/// (the chain from the state being ticked to the end of its step).
		/// </summary>
		public string StatusReport(bool chosen) {
			var sb = new System.Text.StringBuilder();
			void Line(string label, string value) => sb.Append(label.PadRight(14)).Append(value).Append('\n');
			Line("routine", $"{Name}: {(IsActive ? Paused ? "on, PAUSED" : "on" : "off")}{(chosen ? "" : " (not the chosen routine)")}, status: {Status}");
			Line("test", TestStatus);
			var game = PoEMemory.GameRoot?.InGameState;
			Line("game", $"{(game == null ? "(not readable)" : game.IsPaused ? "PAUSED" : "running")}{(GamePausedForFocus ? ", paused by us until it has the focus" : "")}{(PoEMemory.TargetHasFocus ? "" : ", not focused")}{(AuguryIsOpen() ? ", the augury card window is open" : "")}");
			var player = GetPlayer();
			var p = player?.GetComponent<Player>();
			var life = player?.GetComponent<Life>();
			if ( IsValid(p) ) {
				double pct = Experience.PercentOfLevel(p.Level, p.XP);
				Line("character", $"{p.Name}, level {p.Level}, XP {p.XP:N0}{(double.IsNaN(pct) ? "" : $" ({pct:F1}% of the level)")}");
			} else Line("character", "(not readable)");
			if ( IsValid(life) ) {
				static string Pool(int cur, int max) => max <= 0 ? "none" : $"{cur}/{max} ({100.0 * cur / max:F0}%)";
				Line("life", $"{Pool(life.CurHP, life.MaxHP)}, ES {Pool(life.CurES, life.MaxES)}, mana {Pool(life.CurMana, life.MaxMana)}{(life.TotalReservedMana > 0 ? $" ({life.TotalReservedMana} reserved)" : "")}{(IsAlive(player) ? "" : ", DEAD")}");
			}
			Line("maps", $"{MapsCompleted} completed, {MapsRun} entered (since the overlay started)");
			var area = GetCurrentArea();
			string where = InHideout() ? "the hideout" : InMap() ? "a map" : area?.IsTown ?? false ? "a town" : "elsewhere";
			Line("area", $"{area?.Name ?? "(not readable)"} ({where}){(CurrentMap != null ? $", running {CurrentMap}" : "")}");
			if ( InMap() ) Line("objectives", $"{(MonstersRemaining is int m ? $"{m}" : "more than 50")} monsters remain (fewer than {MonstersLeft} wanted), Map Complete: {MapComplete}, uniques killed: {UniquesKilled.Count}");
			Line("next map", $"{MapArea} (tier {MapTier}){(NextMap is { } n ? $", in the backpack: {n.area}" : "")}");
			Line("goal", CurrentGoal == null ? "(none)" : Working(CurrentGoal)?.ToString() ?? CurrentGoal.ToString());
			var current = mapLoop?.Current;
			if ( current == null ) Line("step", "(the loop has not started)");
			else {
				if ( SkipToClear ) Line("skipping", "switched on in a map: the hideout steps skip to Clear Area");
				if ( SkipToPortals ) Line("skipping", $"switched on with {MapPortalCount} map portals up: the hideout steps skip to Enter the map");
				// (the chain from the state ticked now, along Next, to the end of what it is part of)
				var s = current;
				for ( int i = 0; s != null && i < 12; i++, s = s.Next ) Line(i == 0 ? "step" : "", s.ToString());
				if ( s != null ) Line("", "...");
			}
			return sb.ToString();
		}

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
		public static bool IsMapBoss(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)
			&& e.GetComponent<ObjectMagicProperties>()?.Rarity == Offsets.MonsterRarity.Unique;

		// ---- showing it ----

		protected override void RenderSettings() {
			ImGui.SliderInt("Monsters Left", ref MonstersLeft, 0, 50);
			ImGui.SameLine();
			ImGui_HelpMarker("The map counts as cleared once the game says fewer than this many monsters remain (it shows the count from 50 down). 0: kill all of them.");
			RenderStepSettings();
			RenderTests();
			ImGui.Text($"Hideout: {HideoutName ?? "(not seen yet)"}; map: {CurrentMap ?? "(none)"}; next: {NextMap?.area ?? "(none)"}");
			ImGui.Text($"Monsters remaining: {(MonstersRemaining is int m ? m.ToString() : "more than 50")}; map complete: {MapComplete}; uniques killed here: {UniquesKilled.Count}");
			ImGui.Text($"Loop: {mapLoop?.Current?.ToString() ?? "(not started)"}{(SkipToClear ? " (switched on in a map: skipping to Clear Area)" : "")}");
		}
	}
}
