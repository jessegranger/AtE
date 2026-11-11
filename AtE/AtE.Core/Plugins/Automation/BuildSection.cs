using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The Path of Building build to play: picked from the builds saved in Documents/Path of Building/Builds, or the
	/// examples that come with AtE (the Builds folder beside the exe). The Passive Tree section follows its trees, and its
	/// skill sets make the GemPlan.
	/// </summary>
	public class BuildSection : AutomationSection {
		public override string Name => "Build";

		/// <summary>What an example build's BuildFile starts with.</summary>
		public const string ExamplePrefix = "(AtE example) ";

		/// <summary>The build: relative to PobBuild.DefaultFolder (or a full path), or ExamplePrefix and a file in ExamplesFolder.</summary>
		public string BuildFile = "";
		/// <summary>Draw each item's score on it, in the inventory and the purchase window (see ItemScorer).</summary>
		public bool ShowItemScores = true;
		/// <summary>The defences that matter most, first (see ItemScorer.DefenceWeights).</summary>
		public string DefencePriority = string.Join(", ", ItemScorer.DefaultDefencePriority);

		public PobBuild Build { get; private set; }
		public GemPlan Gems { get; private set; }
		/// <summary>Changes each time the build is (re)loaded, so others can notice.</summary>
		public int Version { get; private set; }
		private string error, loaded;
		private string[] buildFiles;

		/// <summary>The example builds that come with AtE.</summary>
		public static string ExamplesFolder => Path.Combine(AppContext.BaseDirectory, "Builds");

		public override void Render() {
			buildFiles ??= FindBuildFiles();
			int current = Array.IndexOf(buildFiles, BuildFile);
			if ( ImGui.Combo("Build", ref current, buildFiles, buildFiles.Length) ) BuildFile = buildFiles[current];
			ImGui.SameLine();
			if ( ImGui.Button("Refresh") ) { buildFiles = FindBuildFiles(); loaded = null; }
			EnsureLoaded();
			if ( error != null ) Warning(error);
			if ( Build == null ) return;
			ImGui.TextUnformatted($"{Build.ClassName} {Build.AscendClassName}, level {Build.Level}: {Build.Specs.Count} trees, {Build.SkillSets.Count} skill sets");
			if ( Gems != null ) RenderGems(Gems);
			ImGui.Checkbox("Show item scores", ref ShowItemScores);
			ImGui.InputText("Defence priority", ref DefencePriority, 200);
			if ( Gems != null && PoEMemory.IsAttached ) RenderScores();
		}

		private IList<string> Priority() => DefencePriority.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

		// the worn items: each one's score, and the loadout's
		private void RenderScores() {
			if ( !ImGui.TreeNode("Item scores (worn)") ) return;
			var (loadout, _) = WornLoadout();
			var ctx = Context(loadout);
			ImGui.TextUnformatted($"Cached: {infos.Count} items, {scores.Count} scores");
			ImGui.TextUnformatted($"Main skill tags: {string.Join(", ", ctx.MainTags)}; base resistances: {string.Join(", ", ctx.BaseResist.Select(r => $"{r.Key} {r.Value:F0}"))}");
			ImGui.TextUnformatted($"Loadout: {ItemScorer.Score(ctx, loadout)}");
			foreach ( var (slot, item) in loadout ) {
				if ( item == null ) continue;
				ImGui.TextUnformatted($"  {slot}: {item.Name}{(item.Identified ? "" : " (unidentified)")}: {ItemScorer.ItemScore(ctx, loadout, slot):F1}");
			}
			ImGui.TreePop();
		}

		/// <summary>The worn items, by slot, and their elements (to draw on).</summary>
		public (Dictionary<string, ItemInfo> loadout, Dictionary<string, InventoryItem> elements) WornLoadout() {
			var loadout = new Dictionary<string, ItemInfo>();
			var elements = new Dictionary<string, InventoryItem>();
			wornKey = "";
			foreach ( var (name, inventory) in Equipment.WornSlots() ) {
				var item = inventory.Items.FirstOrDefault(i => i?.Entity != null);
				if ( item == null ) { loadout[name] = null; continue; }
				loadout[name] = Info(item.Entity, name, out string key);
				elements[name] = item;
				wornKey += $"{name}={key};";
			}
			return (loadout, elements);
		}

		// Caches, cleared on area change (the game makes new item entities then, and their addresses can be reused):
		// item info by item key (see ItemKey), and each candidate's label, for as long as the context (worn items, level,
		// build, defence priority) stays the same
		private readonly Dictionary<string, ItemInfo> infos = new Dictionary<string, ItemInfo>();
		private readonly Dictionary<string, (string text, Color color)> scores = new Dictionary<string, (string, Color)>();
		private string wornKey = "", contextKey;
		private ScoringContext context;
		private bool areaHooked;

		private void ClearCaches() { infos.Clear(); scores.Clear(); contextKey = null; context = null; }

		/// <summary>
		/// A key for an item while in this area: its entity's address, and what changes when it is crafted on (where its
		/// mods are kept, whether it is identified, its sockets' colours and links).
		/// </summary>
		public static string ItemKey(Entity e) {
			var mods = e.GetComponent<Mods>();
			var sockets = e.GetComponent<Sockets>();
			string m = IsValid(mods) ? $"{mods.Cache.ImplicitModsArray.Head.ToInt64():X}.{mods.Cache.ExplicitModsArray.Head.ToInt64():X}.{mods.Cache.ExplicitModsArray.Tail.ToInt64():X}.{mods.IsIdentified}" : "";
			string sk = IsValid(sockets) ? string.Concat(Enumerable.Range(0, Sockets.Max).Select(i => (int)sockets.Color(i))) + "." + string.Join("", sockets.LinkGroups) : "";
			return $"{e.Address.ToInt64():X}/{m}/{sk}";
		}

		private ItemInfo Info(Entity e, string slot, out string key) {
			key = ItemKey(e);
			string k = $"{slot}/{key}";
			if ( !infos.TryGetValue(k, out var info) ) infos[k] = info = ItemInfo.From(e, slot);
			return info;
		}

		// the scoring context, made again (and the scores forgotten) when the worn items, level, build or priority change
		private ScoringContext Context(Dictionary<string, ItemInfo> loadout) {
			string key = $"{wornKey}|{GemPlan.CharacterLevel()}|{Version}|{DefencePriority}";
			if ( key != contextKey || context == null ) {
				contextKey = key;
				scores.Clear();
				context = ScoringContext.FromGame(Gems, loadout.Values, Priority());
			}
			return context;
		}

		// the scores to draw, worked out every so often (each one solves the sockets a few times)
		private readonly List<(RectangleF rect, string text, Color color)> labels = new List<(RectangleF, string, Color)>();
		private long labelsTime;

		private void DrawItemScores() {
			var panel = GetUI()?.InventoryPanel;
			bool inventory = IsValid(panel) && panel.IsVisible;
			bool purchase = Town.IsOpen(Town.PurchaseWindow);
			if ( !inventory && !purchase ) { labels.Clear(); return; }
			if ( Time.ElapsedMilliseconds - labelsTime > 500 ) {
				labelsTime = Time.ElapsedMilliseconds;
				labels.Clear();
				try {
					MakeLabels(inventory, purchase);
				} catch ( Exception e ) {
					Log($"{Name}: scoring items: {e.Message}");
				}
			}
			foreach ( var (rect, text, color) in labels ) DrawTextAt(new Vector2(rect.X + 2, rect.Y + 2), text, color);
		}

		private void MakeLabels(bool inventory, bool purchase) {
			var (loadout, elements) = WornLoadout();
			var ctx = Context(loadout);
			if ( inventory ) {
				foreach ( var (slot, element) in elements ) {
					if ( loadout[slot] == null ) continue;
					string key = $"worn {slot}";
					if ( !scores.TryGetValue(key, out var label) ) scores[key] = label = ($"{ItemScorer.ItemScore(ctx, loadout, slot):F0}", Color.White);
					labels.Add((element.GetClientRect(), label.text, label.color));
				}
				foreach ( var item in GetUI()?.InventoryPanel?.Backpack?.Items ?? Enumerable.Empty<InventoryItem>() ) Label(ctx, loadout, item);
			}
			if ( purchase ) {
				foreach ( var item in ItemElements(Town.PurchaseWindow, 12) ) Label(ctx, loadout, item);
			}
		}

		// a candidate's score: green if wearing it would be better, gray if not; 0 in red if it cannot be used (level or attributes)
		private void Label(ScoringContext ctx, Dictionary<string, ItemInfo> loadout, InventoryItem element) {
			if ( !(element?.Entity is Entity e) || ItemInfo.KindOf(e.Path) == ItemKind.None ) return;
			var info = Info(e, null, out string key);
			if ( info == null ) return;
			if ( !scores.TryGetValue(key, out var label) ) {
				// (one the character cannot use scores 0, in red)
				if ( ItemScorer.Unusable(ctx, info) != null ) label = ("0", Color.IndianRed);
				else {
					var (_, score, gain) = ItemScorer.Candidate(ctx, loadout, info);
					label = ($"{score:F0}{(info.Identified ? "" : "?")}", gain > 0.5 ? Color.LimeGreen : Color.Gray);
				}
				scores[key] = label;
			}
			labels.Add((element.GetClientRect(), label.text, label.color));
		}

		/// <summary>The visible item elements under root (eg, the purchase window's items).</summary>
		public static IEnumerable<InventoryItem> ItemElements(Element root, int depth) {
			if ( !IsValid(root) || !root.IsVisibleLocal ) yield break;
			var item = new InventoryItem(root.Address);
			if ( item.Entity != null ) { yield return item; yield break; }
			if ( depth <= 0 ) yield break;
			foreach ( var child in root.Children ) {
				foreach ( var found in ItemElements(child, depth - 1) ) yield return found;
			}
		}

		private void RenderGems(GemPlan plan) {
			int level = PoEMemory.IsAttached ? GemPlan.CharacterLevel() : 0;
			var active = level > 0 ? plan.SetFor(level) : null;
			if ( level > 0 ) {
				ImGui.TextUnformatted($"Character level {level}: skill set {active?.ToString() ?? "(none)"}");
				var needed = plan.Needed(level);
				ImGui.TextUnformatted($"Gems needed: {(needed.Count == 0 ? "(none)" : string.Join(", ", needed.Select(g => g.ItemName)))}");
				RenderSockets(plan, level);
			}
			ImGui.TextUnformatted($"Gems wanted (any stage): {string.Join(", ", plan.AllGems.Select(g => g.Name))}");
			foreach ( var set in plan.Stages ) {
				string label = $"{set}{(set == active ? " (now)" : "")}###set{set.Title}";
				if ( !ImGui.TreeNode(label) ) continue;
				bool first = true;
				foreach ( var group in set.EnabledGroups ) {
					int slot = GemPlan.SlotOf(group.Label);
					ImGui.TextUnformatted($"{(first ? "main" : "utility")}, {(slot >= 0 ? $"slot {slot}" : "no slot")}: {group}");
					first = false;
				}
				ImGui.TreePop();
			}
			var unused = plan.Build.SkillSets.Except(plan.Stages).ToList();
			if ( unused.Count > 0 ) ImGui.TextUnformatted($"Not followed (no \"Level N\" in the title): {string.Join(", ", unused)}");
		}

		// the best arrangement of the gems in the worn items, and how the gems sit now
		private void RenderSockets(GemPlan plan, int level) {
			if ( !ImGui.TreeNode("Sockets (worn items)") ) return;
			var items = SocketItem.Worn();
			var groups = plan.GroupsFor(level).ToList();
			var best = SocketSolver.Solve(items, groups, GemPlan.ColorOf);
			ImGui.TextUnformatted($"Items: {(items.Count == 0 ? "(no sockets)" : string.Join("; ", items))}");
			ImGui.TextUnformatted($"Best arrangement: {best.Score:F1} points; gem colours: {string.Join(", ", groups.SelectMany(g => g.EnabledGems).Select(g => $"{g.Name} {GemPlan.ColorOf(g)}"))}");
			if ( PobGemData.Error != null ) Warning($"PoB gem data: {PobGemData.Error} (colours of gems we have not got are not known)");
			for ( int g = 0; g < groups.Count; g++ ) {
				var placed = best.Groups[g];
				var missing = groups[g].EnabledGems.Where(gem => !placed.Any(p => p.Gem == gem)).ToList();
				ImGui.TextUnformatted($"  {(g == 0 ? "main" : "utility")} {groups[g].Label}: {(placed.Count == 0 ? "(no socket)" : string.Join(", ", placed))}"
					+ (missing.Count > 0 ? $"; no room for {string.Join(", ", missing.Select(m => m.Name))}" : ""));
			}
			ImGui.TreePop();
		}

		public override IState OnTick(long dt) {
			if ( !areaHooked ) { areaHooked = true; OnAreaChange += (_, __) => ClearCaches(); }
			EnsureLoaded();
			if ( ShowItemScores && Gems != null && PoEMemory.IsAttached ) DrawItemScores();
			return this;
		}

		/// <summary>Load BuildFile if it changed (or was never loaded).</summary>
		public void EnsureLoaded() {
			if ( loaded == BuildFile ) return;
			loaded = BuildFile;
			Build = null; Gems = null; error = null;
			Version++;
			if ( string.IsNullOrEmpty(BuildFile) ) return;
			try {
				Build = PobBuild.Load(FullPath(BuildFile));
				Gems = new GemPlan(Build);
			} catch ( Exception e ) {
				error = $"{BuildFile}: {e.Message}";
				Log($"{Name}: {error}");
			}
		}

		public static string FullPath(string buildFile) =>
			buildFile.StartsWith(ExamplePrefix) ? Path.Combine(ExamplesFolder, buildFile.Substring(ExamplePrefix.Length))
			: Path.IsPathRooted(buildFile) ? buildFile
			: Path.Combine(PobBuild.DefaultFolder, buildFile);

		private static string[] FindBuildFiles() {
			string[] find(string root) => Directory.Exists(root)
				? Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
				: Array.Empty<string>();
			return find(PobBuild.DefaultFolder).Concat(find(ExamplesFolder).Select(f => ExamplePrefix + f)).ToArray();
		}

		// not ImGui.TextColored: that is printf-style
		internal static void Warning(string message) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.3f, 1f));
			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(message);
			ImGui.PopTextWrapPos();
			ImGui.PopStyleColor();
		}
	}
}
