using ImGuiNET;
using System;
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
			EnsureLoaded();
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
