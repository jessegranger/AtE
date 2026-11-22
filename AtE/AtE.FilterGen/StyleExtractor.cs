using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AtE.FilterGen {

	/// <summary>
	/// Turns a plain .filter into a .filtersrc: each block's style lines become a named Style plus a SetStyle line.
	///
	/// Styles are named from the "!tag" FilterBlade writes on each block header (Show # %D5 $type->6l $tier->others !links_6lt2),
	/// which is already NeverSink's name for the block's look: "links_6lt2" becomes "Links6lt2". A tag used with more than one
	/// set of style lines gets one style per set; the extra ones are named by what they add (usually ...Alert: the same look
	/// with a sound, beam and minimap icon). Blocks without a tag, or whose style lines are not all together, keep their lines.
	///
	/// Compiling the result gives back the input exactly; <see cref="Extract"/> checks that and reports if it does not.
	/// </summary>
	public static class StyleExtractor {

		private static readonly Regex Tag = new Regex(@"!(\S+)");

		public sealed class ExtractResult {
			public string Output;
			public string StylesOutput; // the styles file, when one was asked for (null otherwise)
			public int StyleCount;
			public int BlocksStyled;
			public int BlocksLeftInline;
			public bool RoundTrips;
		}

		private sealed class Variant {
			public string Tag;
			public List<string> Lines;
			public string Key;
			public int Count;
			public int FirstSeen;
			public string Name;
		}

		private sealed class Block {
			public int Header;      // line index of Show/Hide/Minimal
			public int First, Last; // line index range of the body (inclusive), Last < First when empty
			public int ActionStart, ActionEnd; // the contiguous run of action lines, -1 if none or not contiguous
			public Variant Variant;
		}

		/// <param name="stylesFile">
		/// When set, the styles go into a file of that name (in <see cref="ExtractResult.StylesOutput"/>) and the
		/// main source imports it. When null, the styles go at the top of the main source.
		/// </param>
		public static ExtractResult Extract(string filter, string stylesFile = null) {
			var lines = FilterSyntax.SplitLines(filter, out string newline);
			var blocks = new List<Block>();
			var variants = new Dictionary<string, Variant>(StringComparer.Ordinal);
			var res = new ExtractResult();

			for ( int i = 0; i < lines.Length; i++ ) {
				if ( !FilterSyntax.BlockHeader.IsMatch(lines[i]) ) continue;
				var b = new Block { Header = i, First = i + 1, Last = i, ActionStart = -1, ActionEnd = -1 };
				while ( b.Last + 1 < lines.Length && FilterSyntax.IsIndented(lines[b.Last + 1]) && !FilterSyntax.IsBlank(lines[b.Last + 1]) ) b.Last++;
				blocks.Add(b);
				i = b.Last;

				var actionIdx = Enumerable.Range(b.First, b.Last - b.First + 1)
					.Where(j => FilterSyntax.IsAction(FilterSyntax.Keyword(lines[j].Trim()))).ToList();
				var tag = Tag.Match(lines[b.Header]);
				bool contiguous = actionIdx.Count > 0 && actionIdx.Last() - actionIdx.First() == actionIdx.Count - 1;
				bool slotsUnique = actionIdx.Select(j => FilterSyntax.SlotOf(FilterSyntax.Keyword(lines[j].Trim()))).Distinct().Count() == actionIdx.Count;
				if ( !tag.Success || !contiguous || !slotsUnique ) {
					if ( actionIdx.Count > 0 ) res.BlocksLeftInline++;
					continue;
				}
				b.ActionStart = actionIdx.First();
				b.ActionEnd = actionIdx.Last();
				var blockStyle = actionIdx.Select(j => lines[j].Trim()).ToList();
				string key = tag.Groups[1].Value + "\n" + string.Join("\n", blockStyle);
				if ( !variants.TryGetValue(key, out var v) ) {
					variants[key] = v = new Variant { Tag = tag.Groups[1].Value, Lines = blockStyle, Key = key, FirstSeen = variants.Count };
				}
				v.Count++;
				b.Variant = v;
			}

			NameVariants(variants.Values);
			res.StyleCount = variants.Count;

			// Write the styles section after the opening comment banner, then the filter with SetStyle in place of style lines.
			var outLines = new List<string>();
			int insertAt = 0;
			while ( insertAt < lines.Length && lines[insertAt].StartsWith("#") ) insertAt++;
			while ( insertAt < lines.Length && FilterSyntax.IsBlank(lines[insertAt]) ) insertAt++;
			var byBlockStart = blocks.Where(b => b.Variant != null).ToDictionary(b => b.ActionStart);

			var stylesSection = new List<string>();
			WriteStyles(stylesSection, variants.Values);
			void InsertStyles() {
				if ( stylesFile == null ) {
					outLines.AddRange(stylesSection);
				} else {
					outLines.Add($"Import \"{stylesFile}\"");
					outLines.Add("");
				}
			}

			for ( int i = 0; i < lines.Length; i++ ) {
				if ( i == insertAt ) InsertStyles();
				if ( byBlockStart.TryGetValue(i, out var b) ) {
					outLines.Add("\tSetStyle " + b.Variant.Name);
					i = b.ActionEnd;
					res.BlocksStyled++;
					continue;
				}
				outLines.Add(lines[i]);
			}
			if ( insertAt >= lines.Length ) InsertStyles();

			res.Output = string.Join(newline, outLines);
			if ( stylesFile != null ) res.StylesOutput = string.Join(newline, stylesSection);
			var back = FilterCompiler.Compile(res.Output, null, p => res.StylesOutput
				?? throw new IOException("no styles file"));
			res.RoundTrips = back.Ok && back.Output == filter;
			return res;
		}

		private static void WriteStyles(List<string> outLines, IEnumerable<Variant> variants) {
			outLines.Add("// ==============================================================================");
			outLines.Add("// STYLES - named from each block's FilterBlade !tag. Source only: Style blocks and");
			outLines.Add("// '//' lines are not written to the .filter. Use one in a block with: SetStyle Name");
			outLines.Add("// ==============================================================================");
			outLines.Add("");
			foreach ( var v in variants.OrderBy(v => v.FirstSeen) ) {
				outLines.Add("Style " + v.Name + (v.Count == 1 ? "" : $" # used by {v.Count} blocks"));
				foreach ( var l in v.Lines ) outLines.Add("\t" + l);
				outLines.Add("");
			}
		}

		private static void NameVariants(IEnumerable<Variant> all) {
			var used = new HashSet<string>(StringComparer.Ordinal);
			foreach ( var group in all.GroupBy(v => v.Tag) ) {
				var ordered = group.OrderByDescending(v => v.Count).ThenBy(v => v.FirstSeen).ToList();
				string baseName = Pascal(group.Key);
				var main = ordered[0];
				main.Name = Unique(baseName, used);
				foreach ( var v in ordered.Skip(1) ) {
					string suffix = HasAlert(v.Lines) && !HasAlert(main.Lines) ? "Alert"
						: !HasAlert(v.Lines) && HasAlert(main.Lines) ? "Quiet"
						: "Alt";
					v.Name = Unique(baseName + suffix, used);
				}
			}
		}

		private static bool HasAlert(List<string> lines) => lines.Any(l => {
			string slot = FilterSyntax.SlotOf(FilterSyntax.Keyword(l));
			return slot == "AlertSound" || slot == "MinimapIcon" || slot == "PlayEffect";
		});

		private static string Unique(string name, HashSet<string> used) {
			string n = name;
			for ( int k = 2; !used.Add(n); k++ ) n = name + k;
			return n;
		}

		/// <summary>"links_6lt2" -> "Links6lt2", "currency-e" -> "CurrencyE".</summary>
		public static string Pascal(string tag) {
			var sb = new StringBuilder();
			foreach ( var part in Regex.Split(tag, @"[^A-Za-z0-9]+").Where(p => p.Length > 0) ) {
				sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
			}
			if ( sb.Length == 0 || char.IsDigit(sb[0]) ) sb.Insert(0, "S");
			return sb.ToString();
		}
	}
}
