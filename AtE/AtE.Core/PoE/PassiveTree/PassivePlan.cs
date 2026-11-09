using System;
using System.Collections.Generic;
using System.Linq;

namespace AtE {

	/// <summary>One point to spend: allocate Node (on the way to Toward), or for a Mastery, choose MasteryEffect on it.</summary>
	public sealed class PassiveStep {
		public PassiveNode Node;
		/// <summary>The notable (or other node) this is on the way to; Node itself when it is the goal.</summary>
		public PassiveNode Toward;
		/// <summary>The stage (an index into PassivePlan.Stages) that wants it.</summary>
		public int Stage;
		/// <summary>For a Mastery, the effect to choose (as the build has it), and its text; else -1.</summary>
		public int MasteryEffect = -1;
		public string MasteryText;

		public override string ToString() =>
			MasteryEffect >= 0 ? $"{Node.Name}: {MasteryText}"
			: Toward != null && Toward != Node ? $"{Node.Name} (toward {Toward.Name})"
			: Node.Name;
	}

	/// <summary>One tree of the build, as a stage of leveling: the plan takes its nodes after the earlier stages'.</summary>
	public sealed class PassiveStage {
		public PobTreeSpec Spec;
		/// <summary>Its index in the build's Specs.</summary>
		public int SpecIndex;
		/// <summary>Nodes the stage before this one has, that this one does not: they would need refund points (not planned).</summary>
		public readonly List<PassiveNode> Refund = new List<PassiveNode>();
		/// <summary>Nodes it has that can't be reached from the rest of its tree (eg, taken with Thread of Hope or Impossible Escape).</summary>
		public readonly List<PassiveNode> Unreachable = new List<PassiveNode>();
		/// <summary>Nodes it has that are left out of the plan: cluster jewel nodes and bloodline nodes.</summary>
		public int ClusterNodes, BloodlineNodes;
		public override string ToString() => Spec.ToString();
	}

	/// <summary>
	/// The order to take a build's passives in, from a new character on: the trees of the build as stages (eg "Level 12",
	/// "Level 25", ...), each one's new nodes after the earlier ones'.
	///
	/// Within a stage, from what is taken so far: the nearest notable or keystone (fewest points to get to, through the
	/// stage's own nodes), and the path to it, one point at a time; again, until there are none left; then the other
	/// nodes, nearest first; then the jewel sockets. A mastery comes right after the first notable of its group.
	///
	/// The ascendancy is ordered the same way, on its own (its points come from the labyrinth).
	/// </summary>
	public sealed class PassivePlan {
		public PassiveTree Tree;
		public PobBuild Build;
		public readonly List<PassiveStage> Stages = new List<PassiveStage>();
		/// <summary>The main tree's points, in order.</summary>
		public readonly List<PassiveStep> Steps = new List<PassiveStep>();
		/// <summary>The ascendancy's points, in order.</summary>
		public readonly List<PassiveStep> AscendancySteps = new List<PassiveStep>();
		/// <summary>The ascendancy (eg "Pathfinder") of the last stage, or null.</summary>
		public string Ascendancy;

		/// <summary>
		/// Which trees to follow, by default: the tree selected in PoB, after the ones listed before it that are smaller
		/// (in size order; PoB's order breaks ties). A leveling build's "Level 12", "Level 25" ... trees grow, and it is
		/// saved with the one in use selected, so this skips the alternatives (eg, an "Endgame" tree listed after it).
		/// </summary>
		/// <param name="target">The tree to end with (an index into build.Specs); -1 for the one selected in PoB.</param>
		public static List<int> DefaultStages(PobBuild build, int target = -1) {
			if ( build.Specs.Count == 0 ) return new List<int>();
			if ( target < 0 || target >= build.Specs.Count ) target = build.ActiveSpec;
			int size(int i) => build.Specs[i].Nodes.Count;
			return Enumerable.Range(0, target)
				.Where(i => size(i) < size(target))
				.OrderBy(size)
				.Append(target)
				.ToList();
		}

		/// <summary>The plan for build's trees stages (indexes into build.Specs, in order; null for DefaultStages).</summary>
		public static PassivePlan Make(PassiveTree tree, PobBuild build, IList<int> stages = null) {
			var plan = new PassivePlan { Tree = tree, Build = build };
			stages ??= DefaultStages(build);
			if ( stages.Count == 0 ) return plan;
			var first = build.Specs[stages[0]];
			var start = tree.ClassStart(first.ClassId) ?? throw new ArgumentException($"no class {first.ClassId} in tree {tree.Version}");
			var last = build.Specs[stages[stages.Count - 1]];
			plan.Ascendancy = tree.AscendancyName(last.ClassId, last.AscendClassId);
			var ascStart = plan.Ascendancy == null ? null : tree.AscendancyStart(plan.Ascendancy);

			var taken = new HashSet<PassiveNode> { start };
			if ( ascStart != null ) taken.Add(ascStart);
			var masteriesDone = new HashSet<int>();
			var previous = new HashSet<PassiveNode>(); // the stage before's nodes
			for ( int s = 0; s < stages.Count; s++ ) {
				var spec = build.Specs[stages[s]];
				var stage = new PassiveStage { Spec = spec, SpecIndex = stages[s] };
				plan.Stages.Add(stage);
				var (known, cluster, _) = spec.Resolve(tree);
				stage.ClusterNodes = cluster.Count;
				stage.BloodlineNodes = known.Count(n => n.IsBloodline);

				bool mainTree(PassiveNode n) => n.Ascendancy == null;
				bool ascendancy(PassiveNode n) => plan.Ascendancy != null && n.Ascendancy == plan.Ascendancy && !n.IsBloodline;
				var wanted = known.Where(n => n.Kind != PassiveNodeKind.ClassStart && n.Kind != PassiveNodeKind.AscendancyStart).ToHashSet();
				stage.Refund.AddRange(previous.Where(n => mainTree(n) && !wanted.Contains(n)));
				previous = wanted;

				// masteries: after the first notable of their group (or at the end of the stage, if none is taken)
				var masteries = wanted.Where(n => n.Kind == PassiveNodeKind.Mastery && !masteriesDone.Contains(n.Id)).ToList();
				void afterTaking(PassiveNode node) {
					if ( node.Kind != PassiveNodeKind.Notable ) return;
					foreach ( var m in masteries.Where(m => m.Group == node.Group && !masteriesDone.Contains(m.Id)).ToList() ) {
						plan.Steps.Add(MasteryStep(m, spec, s));
						masteriesDone.Add(m.Id);
					}
				}

				var need = wanted.Where(n => mainTree(n) && n.Kind != PassiveNodeKind.Mastery && !taken.Contains(n)).ToHashSet();
				stage.Unreachable.AddRange(Order(taken, need, mainTree, s, plan.Steps, afterTaking));
				foreach ( var m in masteries.Where(m => !masteriesDone.Contains(m.Id)) ) {
					plan.Steps.Add(MasteryStep(m, spec, s));
					masteriesDone.Add(m.Id);
				}

				var needAsc = wanted.Where(n => ascendancy(n) && !taken.Contains(n)).ToHashSet();
				stage.Unreachable.AddRange(Order(taken, needAsc, ascendancy, s, plan.AscendancySteps, null));
			}
			return plan;
		}

		/// <summary>The first step not yet taken (allocated: the passive ids the character has), or null.</summary>
		public PassiveStep Next(ICollection<int> allocated, bool ascendancy = false) =>
			(ascendancy ? AscendancySteps : Steps).FirstOrDefault(s => !allocated.Contains(s.Node.Id));

		private static PassiveStep MasteryStep(PassiveNode mastery, PobTreeSpec spec, int stage) {
			int effect = spec.MasteryEffects.TryGetValue(mastery.Id, out int e) ? e : -1;
			var stats = mastery.MasteryEffects.FirstOrDefault(m => m.Effect == effect).Stats;
			return new PassiveStep {
				Node = mastery, Toward = mastery, Stage = stage, MasteryEffect = effect,
				MasteryText = stats != null ? string.Join(" / ", stats) : $"effect {effect} (not in this tree's data)",
			};
		}

		// add steps to take all of need, from taken (which grows), going only through inScope nodes that are taken or
		// needed; returns the needed nodes that can't be reached
		private static List<PassiveNode> Order(HashSet<PassiveNode> taken, HashSet<PassiveNode> need, Func<PassiveNode, bool> inScope,
			int stage, List<PassiveStep> steps, Action<PassiveNode> afterTaking) {
			while ( need.Count > 0 ) {
				// fewest points to each needed node, from anything taken
				var parent = new Dictionary<PassiveNode, PassiveNode>();
				var cost = new Dictionary<PassiveNode, int>();
				var queue = new Queue<PassiveNode>();
				foreach ( var t in taken.Where(inScope) ) { cost[t] = 0; queue.Enqueue(t); }
				while ( queue.Count > 0 ) {
					var n = queue.Dequeue();
					foreach ( var o in n.Links ) {
						if ( cost.ContainsKey(o) || !inScope(o) || !(taken.Contains(o) || need.Contains(o)) ) continue;
						cost[o] = cost[n] + (taken.Contains(o) ? 0 : 1);
						parent[o] = n;
						queue.Enqueue(o);
					}
				}
				var reachable = need.Where(cost.ContainsKey).ToList();
				if ( reachable.Count == 0 ) break;
				// notables and keystones first, then the rest, then jewel sockets; nearest first (ties: the lower id, so it is stable)
				int rank(PassiveNode n) => n.Kind == PassiveNodeKind.Notable || n.Kind == PassiveNodeKind.Keystone ? 0
					: n.Kind == PassiveNodeKind.JewelSocket ? 2 : 1;
				var goal = reachable.OrderBy(rank).ThenBy(n => cost[n]).ThenBy(n => n.Id).First();
				// the path to it, from the taken end
				var path = new List<PassiveNode>();
				for ( var n = goal; !taken.Contains(n); n = parent[n] ) path.Add(n);
				path.Reverse();
				foreach ( var n in path ) {
					steps.Add(new PassiveStep { Node = n, Toward = goal, Stage = stage });
					taken.Add(n);
					need.Remove(n);
					afterTaking?.Invoke(n);
				}
			}
			return need.ToList();
		}
	}
}
