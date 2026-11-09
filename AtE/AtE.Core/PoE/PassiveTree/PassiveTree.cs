using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AtE {

	public enum PassiveNodeKind { Normal, Notable, Keystone, Mastery, JewelSocket, ClassStart, AscendancyStart }

	/// <summary>A node of the passive tree, as Path of Building has it (its id is the game's passive id).</summary>
	public sealed class PassiveNode {
		public int Id;
		public string Name;
		public string[] Stats = Array.Empty<string>();
		public PassiveNodeKind Kind;
		/// <summary>The ascendancy (or bloodline) it belongs to, eg "Pathfinder"; null for the main tree.</summary>
		public string Ascendancy;
		public bool IsBloodline;
		/// <summary>For a ClassStart, the class it starts (an index into PassiveTree.Classes); else -1.</summary>
		public int ClassStartIndex = -1;
		/// <summary>The group (cluster) it is drawn in; a Mastery is in the group of the notables it belongs to. -1 for none.</summary>
		public int Group = -1;
		/// <summary>Where it is on the tree, in Path of Building's (and the game's tree data's) units: x right, y down.</summary>
		public Vector2 Position;
		public bool HasPosition;
		/// <summary>The nodes it connects to, both ways. Links between a class start and its ascendancy's start are included.</summary>
		public readonly List<PassiveNode> Links = new List<PassiveNode>();
		/// <summary>For a Mastery: the effects to choose from (the effect id, as a build's masteryEffects has it, and its text).</summary>
		public List<(int Effect, string[] Stats)> MasteryEffects = new List<(int, string[])>();

		public override string ToString() => $"{Name} ({Id})";
	}

	public sealed class PassiveClass {
		public string Name;
		/// <summary>The ascendancy ids (eg "Pathfinder"), in order: a build's ascendClassId 1 is the first.</summary>
		public List<string> Ascendancies = new List<string>();
		public PassiveNode Start;
		public override string ToString() => Name;
	}

	/// <summary>
	/// The passive tree from Path of Building's tree data (TreeData/&lt;version&gt;/tree.lua): every node, how they link,
	/// and where each is. Node ids are the ids a PoB build lists, and the game's passive ids.
	///
	/// Ids of 65536 and up in a build are nodes of a cluster jewel's little tree, which PoB numbers itself (see
	/// IsClusterNode); they are not in the tree data.
	/// </summary>
	public sealed class PassiveTree {
		public string Version;
		public readonly Dictionary<int, PassiveNode> Nodes = new Dictionary<int, PassiveNode>();
		/// <summary>In order: a build's classId 0 is the first (Scion).</summary>
		public readonly List<PassiveClass> Classes = new List<PassiveClass>();
		/// <summary>The bloodlines, in order: a build's secondaryAscendClassId 1 is the first.</summary>
		public readonly List<string> Bloodlines = new List<string>();

		public static bool IsClusterNode(int id) => id >= 65536;

		public PassiveNode Node(int id) => Nodes.TryGetValue(id, out var n) ? n : null;

		/// <summary>The class start node for a build's classId, or null.</summary>
		public PassiveNode ClassStart(int classId) => classId >= 0 && classId < Classes.Count ? Classes[classId].Start : null;

		/// <summary>The ascendancy's start node (eg, for "Pathfinder"), or null.</summary>
		public PassiveNode AscendancyStart(string ascendancy) =>
			Nodes.Values.FirstOrDefault(n => n.Kind == PassiveNodeKind.AscendancyStart && n.Ascendancy == ascendancy);

		/// <summary>The name of a build's ascendClassId (1 is the first) for classId, or null for none.</summary>
		public string AscendancyName(int classId, int ascendClassId) =>
			classId >= 0 && classId < Classes.Count && ascendClassId >= 1 && ascendClassId <= Classes[classId].Ascendancies.Count
				? Classes[classId].Ascendancies[ascendClassId - 1] : null;

		/// <summary>The name of a build's secondaryAscendClassId (1 is the first bloodline), or null for none.</summary>
		public string BloodlineName(int secondaryAscendClassId) =>
			secondaryAscendClassId >= 1 && secondaryAscendClassId <= Bloodlines.Count ? Bloodlines[secondaryAscendClassId - 1] : null;

		/// <summary>Read a tree.lua file.</summary>
		public static PassiveTree Load(string treeLuaPath, string version = null) {
			var root = LuaData.Parse(File.ReadAllText(treeLuaPath)) as LuaTable
				?? throw new FormatException($"{treeLuaPath}: not a Lua table");
			var tree = new PassiveTree { Version = version ?? Path.GetFileName(Path.GetDirectoryName(treeLuaPath)) };

			foreach ( var c in Tables(root.Table("classes")) ) {
				var cls = new PassiveClass { Name = c.String("name") };
				foreach ( var a in Tables(c.Table("ascendancies")) ) cls.Ascendancies.Add(a.String("id"));
				tree.Classes.Add(cls);
			}
			foreach ( var a in Tables(root.Table("alternate_ascendancies")) ) tree.Bloodlines.Add(a.String("id"));

			// orbits: how far out each is, and the angle of each place on it
			var constants = root.Table("constants");
			var radii = constants?.Table("orbitRadii")?.List.OfType<double>().ToArray() ?? new double[] { 0, 82, 162, 335, 493, 662, 846 };
			var perOrbit = constants?.Table("skillsPerOrbit")?.List.OfType<double>().Select(d => (int)d).ToArray() ?? new[] { 1, 6, 16, 16, 40, 72, 72 };
			var angles = perOrbit.Select(OrbitAngles).ToArray();

			var groups = root.Table("groups");
			var nodes = root.Table("nodes") ?? throw new FormatException($"{treeLuaPath}: no nodes");
			var links = new List<(PassiveNode node, LuaTable outs, LuaTable ins)>();
			foreach ( var entry in nodes.Fields ) {
				if ( !(entry.Value is LuaTable n) || !(entry.Key is long) ) continue; // (the "root" node has a name key)
				var node = new PassiveNode {
					Id = (int)n.Number("skill", (long)entry.Key),
					Name = n.String("name") ?? "",
					Stats = n.Table("stats")?.List.OfType<string>().ToArray() ?? Array.Empty<string>(),
					Ascendancy = n.String("ascendancyName"),
					IsBloodline = n.Bool("isBloodline"),
				};
				if ( n["classStartIndex"] is double start ) {
					node.Kind = PassiveNodeKind.ClassStart;
					node.ClassStartIndex = (int)start;
				} else if ( n.Bool("isAscendancyStart") ) node.Kind = PassiveNodeKind.AscendancyStart;
				else if ( n.Bool("isMastery") ) node.Kind = PassiveNodeKind.Mastery;
				else if ( n.Bool("isJewelSocket") ) node.Kind = PassiveNodeKind.JewelSocket;
				else if ( n.Bool("isKeystone") ) node.Kind = PassiveNodeKind.Keystone;
				else if ( n.Bool("isNotable") ) node.Kind = PassiveNodeKind.Notable;
				foreach ( var e in Tables(n.Table("masteryEffects")) ) {
					node.MasteryEffects.Add(((int)e.Number("effect"), e.Table("stats")?.List.OfType<string>().ToArray() ?? Array.Empty<string>()));
				}
				// where: the group's center, plus orbitIndex's place on the orbit (as PoB's PassiveTree:ProcessNode)
				node.Group = (int)n.Number("group", -1);
				var group = groups?.Table(node.Group);
				int orbit = (int)n.Number("orbit", -1), index = (int)n.Number("orbitIndex", -1);
				if ( group != null && orbit >= 0 && orbit < radii.Length && index >= 0 && index < angles[orbit].Length ) {
					double angle = angles[orbit][index], r = radii[orbit];
					node.Position = new Vector2((float)(group.Number("x") + (Math.Sin(angle) * r)), (float)(group.Number("y") - (Math.Cos(angle) * r)));
					node.HasPosition = true;
				}
				tree.Nodes[node.Id] = node;
				links.Add((node, n.Table("out"), n.Table("in")));
			}
			foreach ( var (node, outs, ins) in links ) {
				foreach ( var other in Ids(outs).Concat(Ids(ins)).Select(tree.Node) ) {
					if ( other == null || other == node ) continue;
					if ( !node.Links.Contains(other) ) node.Links.Add(other);
					if ( !other.Links.Contains(node) ) other.Links.Add(node);
				}
			}
			foreach ( var node in tree.Nodes.Values.Where(n => n.Kind == PassiveNodeKind.ClassStart) ) {
				if ( node.ClassStartIndex < tree.Classes.Count ) tree.Classes[node.ClassStartIndex].Start = node;
			}
			return tree;
		}

		// the angle (radians, clockwise from up) of each place on an orbit of count places (as PoB's CalcOrbitAngles)
		private static double[] OrbitAngles(int count) {
			double[] degrees = count == 16 ? new double[] { 0, 30, 45, 60, 90, 120, 135, 150, 180, 210, 225, 240, 270, 300, 315, 330 }
				: count == 40 ? new double[] { 0, 10, 20, 30, 40, 45, 50, 60, 70, 80, 90, 100, 110, 120, 130, 135, 140, 150, 160, 170, 180,
					190, 200, 210, 220, 225, 230, 240, 250, 260, 270, 280, 290, 300, 310, 315, 320, 330, 340, 350 }
				: Enumerable.Range(0, count).Select(i => 360.0 * i / count).ToArray();
			return degrees.Select(d => d * Math.PI / 180).ToArray();
		}

		private static IEnumerable<LuaTable> Tables(LuaTable t) => t?.List.OfType<LuaTable>() ?? Enumerable.Empty<LuaTable>();
		private static IEnumerable<int> Ids(LuaTable t) {
			if ( t == null ) yield break;
			foreach ( var v in t.List ) {
				if ( v is string s && int.TryParse(s, out int id) ) yield return id;
				else if ( v is double d ) yield return (int)d;
			}
		}

		/// <summary>The folders that hold TreeData/&lt;version&gt;/tree.lua: AtE's own (beside the exe), then Path of Building's.</summary>
		public static IEnumerable<string> TreeDataFolders() {
			yield return Path.Combine(AppContext.BaseDirectory, "TreeData");
			yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Path of Building Community", "TreeData");
		}

		/// <summary>
		/// The tree.lua for version (eg "3_28"), or if there is none, the newest plain one (not ruthless or alternate)
		/// found in folders (default: TreeDataFolders); null if there is none at all.
		/// </summary>
		public static string FindTreeFile(string version, out string foundVersion, IEnumerable<string> folders = null) {
			var all = (folders ?? TreeDataFolders())
				.Where(Directory.Exists)
				.SelectMany(Directory.GetDirectories)
				.Select(dir => (dir, version: Path.GetFileName(dir), file: Path.Combine(dir, "tree.lua")))
				.Where(t => File.Exists(t.file))
				.ToList();
			var exact = all.FirstOrDefault(t => t.version == version);
			if ( exact.file == null ) {
				exact = all.Where(t => ParseVersion(t.version) != null).OrderByDescending(t => ParseVersion(t.version)).FirstOrDefault();
			}
			foundVersion = exact.version;
			return exact.file;
		}

		// "3_28" as (3, 28); null for "3_28_ruthless" and the like
		private static Tuple<int, int> ParseVersion(string v) {
			var parts = v?.Split('_');
			return parts?.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int a)
				&& int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int b) ? Tuple.Create(a, b) : null;
		}
	}
}
