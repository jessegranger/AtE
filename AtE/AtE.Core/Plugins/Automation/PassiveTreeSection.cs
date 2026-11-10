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
	/// Follow a Path of Building build's passive tree: pick a saved build (and which of its trees to end with), and see
	/// the order its points would be taken in (see PassivePlan). Nothing is allocated in the game yet.
	/// </summary>
	public class PassiveTreeSection : AutomationSection {
		public override string Name => "Passive Tree";

		/// <summary>The build, relative to PobBuild.DefaultFolder (or a full path).</summary>
		public string BuildFile = "";
		/// <summary>The tree to end with (an index into the build's trees); -1 for the one selected in PoB.</summary>
		public int TargetTree = -1;
		/// <summary>Another folder to look in for TreeData/&lt;version&gt;/tree.lua (eg, one with a newer tree than PoB has).</summary>
		public string TreeDataFolder = "";
		/// <summary>Mark the plan on the passive tree while it is open.</summary>
		public bool ShowOnTree = true;
		/// <summary>How many of the next planned points to mark.</summary>
		public int ShowNextCount = 10;

		public PobBuild Build { get; private set; }
		public PassivePlan Plan { get; private set; }
		private string error;
		private string[] buildFiles;
		private readonly Dictionary<string, PassiveTree> trees = new Dictionary<string, PassiveTree>();

		public override void Render() {
			base.Render();
			buildFiles ??= FindBuildFiles();
			int current = Array.IndexOf(buildFiles, BuildFile);
			if ( ImGui.Combo("Build", ref current, buildFiles, buildFiles.Length) ) {
				BuildFile = buildFiles[current];
				TargetTree = -1;
				Reload();
			}
			ImGui.SameLine();
			if ( ImGui.Button("Refresh") ) { buildFiles = FindBuildFiles(); trees.Clear(); Reload(); }
			if ( Plan == null && error == null && !string.IsNullOrEmpty(BuildFile) ) Reload();

			if ( Build != null && Build.Specs.Count > 0 ) {
				var titles = new[] { $"(selected in PoB: {Build.Specs[Build.ActiveSpec]})" }
					.Concat(Build.Specs.Select((s, i) => $"{i + 1}: {s} ({s.Nodes.Count} nodes)")).ToArray();
				int target = TargetTree + 1;
				if ( ImGui.Combo("End with tree", ref target, titles, titles.Length) ) {
					TargetTree = target - 1;
					Reload();
				}
			}
			string folder = TreeDataFolder ?? "";
			if ( ImGui.InputText("Extra TreeData folder", ref folder, 400) ) TreeDataFolder = folder;
			if ( ImGui.IsItemDeactivatedAfterEdit() ) { trees.Clear(); Reload(); }
			ImGui.Checkbox("Mark the plan on the open tree", ref ShowOnTree);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100);
			ImGui.InputInt("next points", ref ShowNextCount);
			if ( PassiveTreePanel.IsOpen ) ImGui.TextUnformatted($"Tree open: {PassiveTreePanel.Centers.Count} passives on it, zoom {PassiveTreePanel.ScreenScale:F3}");

			if ( error != null ) Warning(error);
			if ( Plan != null ) RenderPlan(Plan);
		}

		private void RenderPlan(PassivePlan plan) {
			var b = plan.Build;
			ImGui.TextUnformatted($"{b.ClassName} {plan.Ascendancy}, tree data {plan.Tree.Version}: {plan.Steps.Count} points, {plan.AscendancySteps.Count} ascendancy points");
			var versions = plan.Stages.Select(s => s.Spec.TreeVersion).Distinct().Where(v => v != plan.Tree.Version).ToList();
			if ( versions.Count > 0 ) Warning($"Made with tree {string.Join(", ", versions)}, but that tree data was not found: using {plan.Tree.Version}.");

			// the character's passives, against the plan
			var allocated = new HashSet<int>((PoEMemory.IsAttached ? PoEMemory.GameRoot?.InGameState?.AllocatedPassives : null)?.Select(id => (int)id)
				?? Enumerable.Empty<int>());
			if ( allocated.Count > 0 ) {
				int taken = plan.Steps.Count(s => allocated.Contains(s.Node.Id));
				int takenAsc = plan.AscendancySteps.Count(s => allocated.Contains(s.Node.Id));
				var planned = plan.Steps.Concat(plan.AscendancySteps).Select(s => s.Node.Id).ToHashSet();
				int offPlan = allocated.Count(id => !planned.Contains(id) && plan.Tree.Node(id)?.Kind != PassiveNodeKind.ClassStart);
				ImGui.TextUnformatted($"In game: {GetUI()?.UnspentPassivePoints ?? 0} to spend, {allocated.Count} allocated; {taken} of {plan.Steps.Count} planned points taken, {takenAsc} of {plan.AscendancySteps.Count} ascendancy; {offPlan} not in the plan");
				var next = plan.Next(allocated);
				var nextAsc = plan.Next(allocated, ascendancy: true);
				ImGui.TextUnformatted($"Next: {next?.ToString() ?? "(all taken)"}{(nextAsc != null ? $"; next ascendancy: {nextAsc}" : "")}");
			} else if ( PoEMemory.IsAttached ) {
				ImGui.TextUnformatted("In game: no allocated passives read");
			}

			int number = 0;
			for ( int s = 0; s < plan.Stages.Count; s++ ) {
				var stage = plan.Stages[s];
				var steps = plan.Steps.Where(x => x.Stage == s).ToList();
				var asc = plan.AscendancySteps.Where(x => x.Stage == s).ToList();
				string label = $"{s + 1}. {stage}: {steps.Count} points{(asc.Count > 0 ? $", {asc.Count} ascendancy" : "")}###stage{s}";
				if ( !ImGui.TreeNode(label) ) { number += steps.Count; continue; }
				// x: allocated in game
				foreach ( var step in steps ) ImGui.TextUnformatted($"{++number,4} {(allocated.Contains(step.Node.Id) ? "x" : " ")} {step}");
				foreach ( var step in asc ) ImGui.TextUnformatted($"   A {(allocated.Contains(step.Node.Id) ? "x" : " ")} {step}");
				if ( stage.Refund.Count > 0 ) Warning($"Not in this tree (needs refund points; not planned): {string.Join(", ", stage.Refund.Select(n => n.Name))}");
				if ( stage.Unreachable.Count > 0 ) Warning($"Not connected (eg, taken with a jewel; not planned): {string.Join(", ", stage.Unreachable.Select(n => n.Name))}");
				if ( stage.ClusterNodes > 0 || stage.BloodlineNodes > 0 ) Warning($"Left out: {stage.ClusterNodes} cluster jewel nodes, {stage.BloodlineNodes} bloodline nodes");
				ImGui.TreePop();
			}
		}

		public override IState OnTick(long dt) {
			if ( ShowOnTree && Plan != null && PoEMemory.IsAttached && PassiveTreePanel.IsOpen ) {
				MarkPlan(Plan);
			}
			return base.OnTick(dt);
		}

		// green: allocated; yellow: the next point; orange: the ones after it; gray: the rest of the plan
		private void MarkPlan(PassivePlan plan) {
			var allocated = new HashSet<int>((PoEMemory.GameRoot?.InGameState?.AllocatedPassives ?? Array.Empty<ushort>()).Select(id => (int)id));
			float radius = Math.Max(4f, 50f * PassiveTreePanel.ScreenScale);
			int next = 0;
			foreach ( var step in plan.Steps.Concat(plan.AscendancySteps) ) {
				if ( !PassiveTreePanel.TryGetScreenPosition(step.Node.Id, out var pos) ) continue;
				if ( allocated.Contains(step.Node.Id) ) {
					DrawCircle(pos, radius, Color.LimeGreen);
				} else if ( step.Node.Ascendancy == null && next++ < ShowNextCount ) {
					DrawCircle(pos, next == 1 ? radius * 1.4f : radius, next == 1 ? Color.Yellow : Color.Orange);
					DrawTextAt(pos + new Vector2(radius, -radius), $"{next}", next == 1 ? Color.Yellow : Color.Orange);
				} else {
					DrawCircle(pos, radius * 0.6f, Color.Gray);
				}
			}
		}

		private void Reload() {
			Build = null; Plan = null; error = null;
			if ( string.IsNullOrEmpty(BuildFile) ) return;
			try {
				var path = Path.IsPathRooted(BuildFile) ? BuildFile : Path.Combine(PobBuild.DefaultFolder, BuildFile);
				Build = PobBuild.Load(path);
				if ( Build.Specs.Count == 0 ) { error = "The build has no passive tree."; return; }
				var stages = PassivePlan.DefaultStages(Build, TargetTree);
				var version = Build.Specs[stages[stages.Count - 1]].TreeVersion;
				var tree = Tree(version);
				if ( tree == null ) { error = $"No tree data (TreeData/{version}/tree.lua) in: {string.Join("; ", Folders())}"; return; }
				Plan = PassivePlan.Make(tree, Build, stages);
			} catch ( Exception e ) {
				error = $"{BuildFile}: {e.Message}";
				Log($"{Name}: {error}");
			}
		}

		private IEnumerable<string> Folders() {
			var folders = PassiveTree.TreeDataFolders();
			return string.IsNullOrWhiteSpace(TreeDataFolder) ? folders : folders.Append(TreeDataFolder);
		}

		private PassiveTree Tree(string version) {
			var file = PassiveTree.FindTreeFile(version, out var found, Folders());
			if ( file == null ) return null;
			if ( !trees.TryGetValue(found, out var tree) ) trees[found] = tree = PassiveTree.Load(file, found);
			return tree;
		}

		private static string[] FindBuildFiles() {
			var root = PobBuild.DefaultFolder;
			if ( !Directory.Exists(root) ) return Array.Empty<string>();
			return Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories)
				.Select(f => Path.GetRelativePath(root, f))
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		// not ImGui.TextColored: that is printf-style, and node names have '%' in them
		private static void Warning(string message) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.3f, 1f));
			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(message);
			ImGui.PopTextWrapPos();
			ImGui.PopStyleColor();
		}
	}
}
