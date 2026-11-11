using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AtE {

	/// <summary>
	/// Which members of Offsets the tests exercise. Every field of every explicit-layout struct in Offsets (and the loose offsets, like
	/// Element_Text) is named "Struct.Field". The checks and observers say which they cover (OffsetCheck.Covers, "Struct.*" for all
	/// of a struct); what they do not cover is kept, with a reason, in unchecked.txt beside this file: a ratchet. A new offset with no
	/// check and no entry there is a failure, so is a listed one that has come to be covered, and so is a Covers name that is not an offset.
	/// This needs no game: it reads the checks' declarations, not memory.
	/// </summary>
	public static class OffsetCoverage {

		/// <summary>Every offset: "Struct.Field" for the fields of the explicit-layout structs, and "Offsets.Name" for the loose static ints.</summary>
		public static SortedSet<string> AllOffsets() {
			var all = new SortedSet<string>(StringComparer.Ordinal);
			foreach ( var type in typeof(Offsets).GetNestedTypes(BindingFlags.Public) ) {
				if ( !type.IsValueType || type.IsEnum ) continue;
				var layout = type.StructLayoutAttribute;
				if ( layout == null || layout.Value != LayoutKind.Explicit ) continue;
				foreach ( var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ) {
					if ( f.GetCustomAttribute<FieldOffsetAttribute>() != null ) all.Add($"{type.Name}.{f.Name}");
				}
			}
			foreach ( var f in typeof(Offsets).GetFields(BindingFlags.Public | BindingFlags.Static) ) {
				if ( f.FieldType == typeof(int) && (f.IsInitOnly || f.IsLiteral) && (f.Name.StartsWith("Element_") || f.Name.StartsWith("GameState_") || f.Name.StartsWith("SkillCellIcon_")) ) all.Add($"Offsets.{f.Name}");
			}
			return all;
		}

		/// <summary>What the checks and observers say they cover (as written: names, and "Struct.*" wildcards).</summary>
		public static List<string> DeclaredCovers() =>
			OffsetChecks.All().SelectMany(c => c.Covers).Concat(OffsetObservers.All().SelectMany(o => o.Covers)).Distinct().ToList();

		/// <summary>The declared covers, wildcards expanded; names that are no offset go to unknown.</summary>
		public static SortedSet<string> Covered(SortedSet<string> all, out List<string> unknown) {
			var covered = new SortedSet<string>(StringComparer.Ordinal);
			unknown = new List<string>();
			foreach ( var name in DeclaredCovers() ) {
				if ( name.EndsWith(".*") ) {
					string prefix = name.Substring(0, name.Length - 1);
					var matches = all.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToList();
					if ( matches.Count == 0 ) unknown.Add(name); else foreach ( var m in matches ) covered.Add(m);
				} else if ( all.Contains(name) ) covered.Add(name);
				else unknown.Add(name);
			}
			return covered;
		}

		/// <summary>unchecked.txt, found beside the tests in the repo: "Struct.Field # reason" per line, # alone for a comment.</summary>
		public static string UncheckedFile {
			get {
				for ( var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent ) {
					if ( File.Exists(Path.Combine(dir.FullName, "AtE.sln")) ) return Path.Combine(dir.FullName, "AtE.Core", "Plugins", "Automation", "OffsetTests", "unchecked.txt");
				}
				return null;
			}
		}

		public static SortedSet<string> ReadUnchecked() {
			var set = new SortedSet<string>(StringComparer.Ordinal);
			string file = UncheckedFile;
			if ( file == null || !File.Exists(file) ) return set;
			foreach ( var line in File.ReadAllLines(file) ) {
				string text = line.Split('#')[0].Trim();
				if ( text.Length > 0 ) set.Add(text);
			}
			return set;
		}

		public class Result {
			public int Total, Covered;
			/// <summary>Offsets with no check and no entry in unchecked.txt: add a check, or list them with a reason.</summary>
			public List<string> NewGaps = new List<string>();
			/// <summary>Listed in unchecked.txt, but covered now: take them off the list.</summary>
			public List<string> NowCovered = new List<string>();
			/// <summary>Listed in unchecked.txt, but no longer an offset: take them off the list.</summary>
			public List<string> ListedButGone = new List<string>();
			/// <summary>Covers names (in checks and observers) that are no offset: a typo, or an offset that was removed.</summary>
			public List<string> UnknownCovers = new List<string>();
			public bool Ok => NewGaps.Count == 0 && NowCovered.Count == 0 && ListedButGone.Count == 0 && UnknownCovers.Count == 0;

			public override string ToString() {
				var sb = new StringBuilder();
				sb.AppendLine($"{Covered} of {Total} offsets are covered by a check or observer ({Total - Covered} not: {Total - Covered - NewGaps.Count} listed in unchecked.txt)");
				void Section(string title, List<string> names) {
					if ( names.Count == 0 ) return;
					sb.AppendLine($"{title} ({names.Count}):");
					foreach ( var n in names ) sb.AppendLine("  " + n);
				}
				Section("NEW GAPS: no check covers these and unchecked.txt does not list them (add a check, or list them there with a reason)", NewGaps);
				Section("NOW COVERED: remove these from unchecked.txt", NowCovered);
				Section("LISTED BUT GONE: no such offset, remove these from unchecked.txt", ListedButGone);
				Section("UNKNOWN COVERS: a check or observer claims these, but they are no offset", UnknownCovers);
				return sb.ToString();
			}
		}

		public static Result Compute() {
			var all = AllOffsets();
			var covered = Covered(all, out List<string> unknown);
			var listed = ReadUnchecked();
			var result = new Result { Total = all.Count, Covered = covered.Count, UnknownCovers = unknown.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList() };
			result.NewGaps = all.Except(covered).Except(listed).ToList();
			result.NowCovered = listed.Intersect(covered).ToList();
			result.ListedButGone = listed.Except(all).ToList();
			return result;
		}

		/// <summary>The offsets nothing covers, one per line, grouped by struct, as the body of an unchecked.txt.</summary>
		public static string UncoveredAsList() {
			var all = AllOffsets();
			var covered = Covered(all, out _);
			var sb = new StringBuilder();
			foreach ( var group in all.Except(covered).GroupBy(n => n.Substring(0, n.IndexOf('.'))) ) {
				sb.AppendLine($"# {group.Key}");
				foreach ( var n in group ) sb.AppendLine(n);
				sb.AppendLine();
			}
			return sb.ToString();
		}
	}
}
