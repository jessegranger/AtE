using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AtE {

	/// <summary>
	/// A build saved by Path of Building (an .xml in Documents/Path of Building/Builds): the class, its passive trees, and
	/// its skill sets (the gems to link, see PobSkillSet).
	///
	///   &lt;Build className="Ranger" ascendClassName="Pathfinder" level="97" .../&gt;
	///   &lt;Tree activeSpec="2"&gt;
	///     &lt;Spec title="Level 12 {03}" treeVersion="3_28" classId="2" ascendClassId="3" secondaryAscendClassId="5"
	///           nodes="1648,10829,..." masteryEffects="{50757,57074},..."&gt;
	///       &lt;URL&gt;...&lt;/URL&gt; &lt;Sockets&gt;&lt;Socket nodeId=".." itemId=".."/&gt;&lt;/Sockets&gt; &lt;Overrides/&gt;
	///     &lt;/Spec&gt; ...
	/// </summary>
	public sealed class PobBuild {
		public string FilePath;
		public string ClassName;
		public string AscendClassName;
		public int Level;
		/// <summary>The trees, in the order PoB lists them (leveling builds often title them "Level 12 {03}" and so on).</summary>
		public readonly List<PobTreeSpec> Specs = new List<PobTreeSpec>();
		/// <summary>The tree selected in PoB (an index into Specs).</summary>
		public int ActiveSpec;
		/// <summary>The skill sets, in the order PoB lists them (leveling builds title them "Level 1", "Level 8" and so on).</summary>
		public readonly List<PobSkillSet> SkillSets = new List<PobSkillSet>();
		/// <summary>The skill set selected in PoB (an index into SkillSets).</summary>
		public int ActiveSkillSet;

		public string Name => Path.GetFileNameWithoutExtension(FilePath);

		/// <summary>Documents/Path of Building/Builds, where PoB saves builds.</summary>
		public static string DefaultFolder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Path of Building", "Builds");

		public static PobBuild Load(string path) {
			var root = XDocument.Load(path).Root;
			if ( root?.Name != "PathOfBuilding" ) throw new FormatException($"{path}: not a Path of Building build");
			var b = root.Element("Build");
			var build = new PobBuild {
				FilePath = path,
				ClassName = (string)b?.Attribute("className"),
				AscendClassName = (string)b?.Attribute("ascendClassName"),
				Level = Int(b?.Attribute("level"), 0),
			};
			var tree = root.Element("Tree");
			foreach ( var s in tree?.Elements("Spec") ?? Enumerable.Empty<XElement>() ) build.Specs.Add(PobTreeSpec.Parse(s));
			build.ActiveSpec = Math.Clamp(Int(tree?.Attribute("activeSpec"), 1) - 1, 0, Math.Max(0, build.Specs.Count - 1));
			var skills = root.Element("Skills");
			if ( skills != null ) {
				var sets = skills.Elements("SkillSet").ToList();
				int active = Int(skills.Attribute("activeSkillSet"), 1);
				// (builds from before skill sets have their skills right under Skills: one untitled set)
				if ( sets.Count == 0 ) build.SkillSets.Add(PobSkillSet.Parse(skills));
				else foreach ( var set in sets ) {
					if ( Int(set.Attribute("id"), -1) == active ) build.ActiveSkillSet = build.SkillSets.Count;
					build.SkillSets.Add(PobSkillSet.Parse(set));
				}
			}
			return build;
		}

		// a whole number, or missing (also for "nil", as PoB writes an unset id)
		internal static int Int(XAttribute a, int missing) =>
			a != null && int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : missing;
	}

	/// <summary>One passive tree of a build: which nodes are taken (in no particular order), and the mastery choices.</summary>
	public sealed class PobTreeSpec {
		public string Title;
		/// <summary>The TreeData folder it was made with, eg "3_28".</summary>
		public string TreeVersion;
		/// <summary>The class, an index into PassiveTree.Classes (0 is Scion).</summary>
		public int ClassId;
		/// <summary>The ascendancy, from 1 (see PassiveTree.AscendancyName); 0 for none.</summary>
		public int AscendClassId;
		/// <summary>The bloodline, from 1 (see PassiveTree.BloodlineName); 0 for none.</summary>
		public int SecondaryAscendClassId;
		/// <summary>The node ids taken, ascendancy nodes and cluster jewel nodes (65536 and up) included.</summary>
		public readonly List<int> Nodes = new List<int>();
		/// <summary>Mastery node id to the effect chosen on it.</summary>
		public readonly Dictionary<int, int> MasteryEffects = new Dictionary<int, int>();
		/// <summary>Jewel socket node id to the id of the jewel (an item of the build) in it.</summary>
		public readonly Dictionary<int, int> Jewels = new Dictionary<int, int>();
		public string Url;

		public override string ToString() => string.IsNullOrEmpty(Title) ? "(untitled)" : Title;

		internal static PobTreeSpec Parse(XElement s) {
			var spec = new PobTreeSpec {
				Title = (string)s.Attribute("title") ?? "",
				TreeVersion = (string)s.Attribute("treeVersion"),
				ClassId = PobBuild.Int(s.Attribute("classId"), 0),
				AscendClassId = PobBuild.Int(s.Attribute("ascendClassId"), 0),
				SecondaryAscendClassId = PobBuild.Int(s.Attribute("secondaryAscendClassId"), 0),
				Url = ((string)s.Element("URL"))?.Trim(),
			};
			foreach ( Match m in Regex.Matches((string)s.Attribute("nodes") ?? "", @"\d+") ) spec.Nodes.Add(int.Parse(m.Value, CultureInfo.InvariantCulture));
			foreach ( Match m in Regex.Matches((string)s.Attribute("masteryEffects") ?? "", @"\{(\d+),(\d+)\}") ) {
				spec.MasteryEffects[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
			}
			foreach ( var j in s.Element("Sockets")?.Elements("Socket") ?? Enumerable.Empty<XElement>() ) {
				int node = PobBuild.Int(j.Attribute("nodeId"), -1), item = PobBuild.Int(j.Attribute("itemId"), 0);
				if ( node >= 0 && item > 0 ) spec.Jewels[node] = item;
			}
			return spec;
		}

		/// <summary>
		/// Sort the node ids against tree: the ones it has, the cluster jewel nodes (not in the tree data), and the ones it
		/// doesn't know (the build is from another version of the tree).
		/// </summary>
		public (List<PassiveNode> known, List<int> cluster, List<int> unknown) Resolve(PassiveTree tree) {
			var known = new List<PassiveNode>();
			var cluster = new List<int>();
			var unknown = new List<int>();
			foreach ( int id in Nodes ) {
				if ( PassiveTree.IsClusterNode(id) ) cluster.Add(id);
				else if ( tree.Node(id) is PassiveNode n ) known.Add(n);
				else unknown.Add(id);
			}
			return (known, cluster, unknown);
		}
	}

	/// <summary>
	/// One skill set of a build: its socket groups, in the order PoB lists them. A leveling build titles them by the
	/// character level they start at ("Level 1", "Level 8", ...), and lists the main (damaging) group first.
	///
	///   &lt;SkillSet id="3" title="Level 8"&gt;
	///     &lt;Skill label="Q" enabled="true" slot="Body Armour" ...&gt;
	///       &lt;Gem nameSpec="Fireball" gemId="Metadata/Items/Gems/SkillGemFireball" skillId="Fireball" enabled="true" level="1" quality="0" .../&gt;
	///       &lt;Gem nameSpec="Combustion" gemId="Metadata/Items/Gems/SupportGemChanceToIgnite" skillId="SupportCombustion" .../&gt;
	///     &lt;/Skill&gt; ...
	/// </summary>
	public sealed class PobSkillSet {
		public string Title;
		/// <summary>The character level from the title ("Level 8" is 8), or -1 if it has none.</summary>
		public int Level = -1;
		/// <summary>All the socket groups, disabled ones included.</summary>
		public readonly List<PobSkillGroup> Groups = new List<PobSkillGroup>();

		/// <summary>The groups to use: enabled, with an enabled gem. The first is the main (damaging) one.</summary>
		public IEnumerable<PobSkillGroup> EnabledGroups => Groups.Where(g => g.Enabled && g.EnabledGems.Any());

		public override string ToString() => string.IsNullOrEmpty(Title) ? "(untitled)" : Title;

		internal static PobSkillSet Parse(XElement set) {
			var s = new PobSkillSet { Title = (string)set.Attribute("title") ?? "" };
			var m = Regex.Match(s.Title, @"\bLevel\s*(\d+)", RegexOptions.IgnoreCase);
			if ( m.Success ) s.Level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
			foreach ( var g in set.Elements("Skill") ) s.Groups.Add(PobSkillGroup.Parse(g));
			return s;
		}
	}

	/// <summary>A socket group: gems to link together; the label is the key to bind its skill to (eg, "Q", "RMB").</summary>
	public sealed class PobSkillGroup {
		public string Label;
		/// <summary>The item slot PoB put it in (eg, "Body Armour"), or null. Not used to place gems: the sockets are solved for.</summary>
		public string Slot;
		public bool Enabled;
		/// <summary>All the gems, disabled ones included, in PoB's order.</summary>
		public readonly List<PobGem> Gems = new List<PobGem>();

		public IEnumerable<PobGem> EnabledGems => Gems.Where(g => g.Enabled);
		/// <summary>The skills (not supports): the first is the one to bind to Label.</summary>
		public IEnumerable<PobGem> ActiveGems => EnabledGems.Where(g => !g.IsSupport);
		public IEnumerable<PobGem> Supports => EnabledGems.Where(g => g.IsSupport);

		public override string ToString() => $"{(string.IsNullOrEmpty(Label) ? "(no label)" : Label)}: {string.Join(" + ", EnabledGems.Select(g => g.Name))}";

		internal static PobSkillGroup Parse(XElement g) {
			var group = new PobSkillGroup {
				Label = (string)g.Attribute("label") ?? "",
				Slot = (string)g.Attribute("slot"),
				Enabled = !string.Equals((string)g.Attribute("enabled"), "false", StringComparison.OrdinalIgnoreCase),
			};
			foreach ( var gem in g.Elements("Gem") ) group.Gems.Add(PobGem.Parse(gem));
			return group;
		}
	}

	/// <summary>A gem of a socket group. Its level and quality in PoB are not kept (the plan does not level gems).</summary>
	public sealed class PobGem {
		/// <summary>The gem's name as PoB shows it; a support's is without " Support" (eg, "Combustion").</summary>
		public string Name;
		/// <summary>The item's metadata path (eg, "Metadata/Items/Gems/SupportGemChanceToIgnite"): what the game's gem entity's Path is.</summary>
		public string GemId;
		/// <summary>PoB's skill id (eg, "SupportCombustion").</summary>
		public string SkillId;
		public bool Enabled;

		public bool IsSupport => (SkillId?.StartsWith("Support", StringComparison.Ordinal) ?? false) || (GemId?.Contains("/SupportGem", StringComparison.Ordinal) ?? false);
		/// <summary>The item's name in the game: a support's has " Support" after it.</summary>
		public string ItemName => IsSupport && !Name.EndsWith(" Support", StringComparison.OrdinalIgnoreCase) ? $"{Name} Support" : Name;

		public override string ToString() => Name;

		internal static PobGem Parse(XElement g) => new PobGem {
			Name = (string)g.Attribute("nameSpec") ?? (string)g.Attribute("skillId") ?? "?",
			GemId = (string)g.Attribute("gemId"),
			SkillId = (string)g.Attribute("skillId"),
			Enabled = !string.Equals((string)g.Attribute("enabled"), "false", StringComparison.OrdinalIgnoreCase),
		};
	}
}
