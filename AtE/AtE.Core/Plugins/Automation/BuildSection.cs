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
			if ( Gems != null && PoEMemory.IsAttached ) {
				RenderChanges();
				RenderScores();
			}
		}

		// putting on better items, and arranging the gems (see InventoryPlans): each as a plan, run, or shown in an Object Browser
		private void RenderChanges() {
			var best = BestUpgrade();
			if ( best.item == null ) ImGui.TextDisabled("No better item in the backpack");
			else {
				if ( ImGui.Button($"Put on {best.info.Name} ({best.slot}, {best.gain:+0})") ) Start(PlanPutOn(best.item, best.slot));
				ImGui.SameLine();
				if ( ImGui.Button("Preview Plan") ) Preview($"Put on {best.info.Name}", PlanPutOn(best.item, best.slot));
			}
			if ( ImGui.Button("Arrange gems") ) Start(InventoryPlans.PlanSocketGems(Gems));
			ImGui.SameLine();
			if ( ImGui.Button("Preview Plan##gems") ) Preview("Arrange gems (the next move)", InventoryPlans.PlanOneGemMove(Gems, GemPlan.CharacterLevel(), null));
			ImGui.TextDisabled("(closing the inventory, eg with Escape, stops a plan)");
		}

		/// <summary>Wear item (in the backpack) in slot, then arrange the gems.</summary>
		public IState PlanPutOn(InventoryItem item, string slot) => InventoryPlans.PlanEquip(slot, item, InventoryPlans.PlanSocketGems(Gems));

		private static void Start(IState plan) {
			if ( plan == null ) Notify("Nothing to do (or it can't be done: see the notices).");
			else Run(plan);
		}

		private static void Preview(string label, IState plan) {
			if ( plan == null ) Notify($"{label}: nothing to do.");
			else Run_ObjectBrowser(label, new { Steps = InventoryPlans.Steps(plan).Select(st => st.Name).ToArray(), Plan = plan });
		}

		/// <summary>
		/// The item in the backpack that would most improve the loadout (identified, usable, and better by more than 0.5),
		/// with the slot to put it in; or nulls. (The scores are as drawn: see ItemScorer.)
		/// </summary>
		public (InventoryItem item, ItemInfo info, string slot, double gain) BestUpgrade() {
			(InventoryItem, ItemInfo, string, double) best = (null, null, null, 0.5);
			var (loadout, _) = WornLoadout();
			var ctx = Context(loadout);
			foreach ( var element in BackpackItems() ) {
				if ( !(element.Entity is Entity e) || ItemInfo.KindOf(e.Path) == ItemKind.None ) continue;
				var info = Info(e, null, out _);
				if ( info == null || !info.Identified || ItemScorer.Unusable(ctx, info) != null ) continue;
				var (slot, _, gain) = ItemScorer.Candidate(ctx, loadout, info);
				if ( slot != null && gain > best.Item4 ) best = (element, info, slot, gain);
			}
			return best.Item1 == null ? (null, null, null, 0) : best;
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

		private void ClearCaches() { infos.Clear(); scores.Clear(); tips.Clear(); contextKey = null; context = null; }

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
				tips.Clear();
				context =ScoringContext.FromGame(Gems, loadout.Values, Priority());
			}
			return context;
		}

		// the scores to draw, worked out every so often (each one solves the sockets a few times)
		// (each with its cache key, and how to explain it when hovered: worked out then, and kept in tips)
		private readonly List<(RectangleF rect, string text, Color color, string key, Func<string> explain)> labels = new List<(RectangleF, string, Color, string, Func<string>)>();
		private readonly Dictionary<string, string> tips = new Dictionary<string, string>();
		private long labelsTime;

		/// <summary>
		/// How long the inventory (or a purchase window) must stay open before its items are scored: scoring them the first
		/// time takes the overlay ~1 s, which stalled a routine's open-and-close of the inventory (its sync, at the start of
		/// each area) with the inventory showing all the while.
		/// </summary>
		public const long ScoreAfterOpenMs = 300;
		private long panelOpenSince = -1;

		private void DrawItemScores() {
			var panel = GetUI()?.InventoryPanel;
			bool inventory = IsValid(panel) && panel.IsVisible;
			bool purchase = Town.IsOpen(Town.PurchaseWindow);
			// (never while the Mapping routine runs: it stashes everything, and the scores are for a person looking over the
			// dump tab later; scoring costs the overlay a frame of ~1 s each time a panel opens)
			bool mapping = GetPlugin<AutomationPlugin>()?.Combat?.Routines.OfType<MappingRoutine>().FirstOrDefault()?.IsActive ?? false;
			if ( mapping || (!inventory && !purchase) ) { labels.Clear(); panelOpenSince = -1; return; }
			if ( panelOpenSince < 0 ) panelOpenSince = Time.ElapsedMilliseconds;
			if ( Time.ElapsedMilliseconds - panelOpenSince < ScoreAfterOpenMs ) return;
			if ( Time.ElapsedMilliseconds - labelsTime > 500 ) {
				labelsTime = Time.ElapsedMilliseconds;
				labels.Clear();
				try {
					MakeLabels(inventory, purchase);
				} catch ( Exception e ) {
					Log($"{Name}: scoring items: {e.Message}");
				}
			}
			foreach ( var (rect, text, color, _, _) in labels ) DrawTextAt(new Vector2(rect.X + 2, rect.Y + 2), text, color);
			DrawHoveredTip();
		}

		// the hovered item's score, explained in a box under it
		private void DrawHoveredTip() {
			var cursor = Platform.Current.Keys.GetCursorPos();
			if ( PoEMemory.Memory.TryGetTargetWindowRect(out int wx, out int wy, out _, out _) ) cursor -= new Vector2(wx, wy);
			foreach ( var (rect, _, _, key, explain) in labels ) {
				if ( !rect.Contains(cursor.X, cursor.Y) ) continue;
				if ( !tips.TryGetValue(key, out var tip) ) {
					try { tip = explain(); } catch ( Exception e ) { tip = $"(could not explain: {e.Message})"; }
					tips[key] = tip;
				}
				if ( string.IsNullOrEmpty(tip) ) return;
				var size = ImGui.CalcTextSize(tip);
				const float pad = 6;
				var pos = new Vector2(rect.X, rect.Bottom + 4);
				pos.X = Math.Clamp(pos.X, 0, Math.Max(0, Overlay.Width - size.X - 2 * pad));
				if ( pos.Y + size.Y + 2 * pad > Overlay.Height ) pos.Y = rect.Top - size.Y - 2 * pad - 4;
				var draw = ImGui.GetForegroundDrawList();
				draw.AddRectFilled(pos, pos + size + new Vector2(2 * pad), (uint)ToRGBA(Color.FromArgb(230, 10, 10, 15)), 3f);
				draw.AddRect(pos, pos + size + new Vector2(2 * pad), (uint)ToRGBA(Color.FromArgb(255, 110, 100, 80)), 3f);
				draw.AddText(pos + new Vector2(pad), (uint)ToRGBA(Color.White), tip);
				return;
			}
		}

		// an item's score, part by part (see ItemScorer.Explain), as the tip's text
		private static string Explain(ScoringContext ctx, Dictionary<string, ItemInfo> loadout, string slot, string header) {
			var (parts, gems) = ItemScorer.Explain(ctx, loadout, slot);
			var lines = new List<string> { header };
			foreach ( var (part, points) in parts.OrderByDescending(p => Math.Abs(p.points)) ) lines.Add($"  {points,6:+0;-0} {part}");
			if ( parts.Count == 0 ) lines.Add("  nothing that counts for the build");
			if ( gems.Count > 0 ) lines.Add($"holds: {string.Join(", ", gems)}");
			return string.Join("\n", lines);
		}

		private void MakeLabels(bool inventory, bool purchase) {
			var (loadout, elements) = WornLoadout();
			var ctx = Context(loadout);
			if ( inventory ) {
				foreach ( var (slot, element) in elements ) {
					if ( loadout[slot] == null ) continue;
					string key = $"worn {slot}";
					if ( !scores.TryGetValue(key, out var label) ) scores[key] = label = ($"{ItemScorer.ItemScore(ctx, loadout, slot):F0}", Color.White);
					string s = slot;
					labels.Add((element.GetClientRect(), label.text, label.color, key,
						() => Explain(ctx, loadout, s, $"{loadout[s].Name}: {label.text} (worn, {s})")));
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
			labels.Add((element.GetClientRect(), label.text, label.color, key, () => ExplainCandidate(ctx, loadout, info)));
		}

		// a candidate: why it cannot be used, or its parts as if worn in its best slot, and what that does to the total
		private static string ExplainCandidate(ScoringContext ctx, Dictionary<string, ItemInfo> loadout, ItemInfo info) {
			string unidentified = info.Identified ? "" : "\n(unidentified: only its base, implicits and sockets are known)";
			if ( ItemScorer.Unusable(ctx, info) is string why ) return $"{info.Name}: 0, cannot be used ({why})";
			var (slot, score, gain) = ItemScorer.Candidate(ctx, loadout, info);
			if ( slot == null ) return null;
			var with = ItemScorer.WithItem(loadout, info, slot);
			loadout.TryGetValue(slot, out var worn);
			string vs = worn == null ? $"{slot} is empty" : $"replacing {worn.Name}";
			return Explain(ctx, with, slot, $"{info.Name}: {score:F0} in {slot}") + $"\nwearing it: {gain:+0;-0;0} ({vs})" + unidentified;
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
			var wanted = plan.AllGems.Select(g => g.Name).ToList();
			if ( ImGui.TreeNode($"Gems wanted (any stage): {wanted.Count}###gemsWanted") ) {
				foreach ( var name in wanted ) ImGui.BulletText(name);
				ImGui.TreePop();
			}
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
