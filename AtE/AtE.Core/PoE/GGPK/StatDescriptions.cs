using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AtE {

	/// <summary>One stat of a mod with its roll range, by stat Id (e.g. "base_maximum_mana").</summary>
	public struct StatValue {
		public string Id;
		public long Min;
		public long Max;
		public StatValue(string id, long min, long max) { Id = id; Min = min; Max = max; }
	}

	/// <summary>
	/// The display text for stats, parsed from the client's <c>Metadata/StatDescriptions/*.txt</c>
	/// files. Each file is a list of blocks:
	///
	///   description
	///   	1 base_maximum_mana              (count, then that many stat Ids)
	///   	1                                (number of lines for the language that follows)
	///   		# "{0:+d} to maximum Mana"   (one condition per stat, the template, then handlers)
	///   	lang "German"                    (further languages repeat the count + lines)
	///
	/// A condition tests the raw stat value: <c>#</c> any, <c>N</c> exactly N, <c>N|#</c> at least N,
	/// <c>#|N</c> at most N, <c>N|M</c> between, <c>!N</c> not N; the first line whose conditions all
	/// hold is used. Handlers after the template (<c>negate 1</c>, <c>divide_by_one_hundred 1</c>)
	/// transform the 1-based stat they name before it is substituted into <c>{0}</c>, <c>{1}</c> ...
	/// <c>no_description X</c> marks a stat that is deliberately never displayed.
	///
	/// Only one language is kept per parse (plus English as the fallback), so memory stays modest.
	/// </summary>
	public sealed class StatDescriptions {

		public const string Folder = "metadata/statdescriptions/";

		private sealed class Handler {
			public string Name;
			public readonly List<int> Args = new List<int>();
		}

		private struct Condition {
			public long Lo, Hi;
			public bool Not;
			public bool Matches(long v) => Not ? !(v >= Lo && v <= Hi) : v >= Lo && v <= Hi;
		}

		private sealed class Entry {
			public Condition[] Conditions;
			public string Template;
			public readonly List<Handler> Handlers = new List<Handler>();
		}

		private sealed class Block {
			public string[] Ids;
			public readonly List<Entry> English = new List<Entry>();
			public readonly List<Entry> Wanted = new List<Entry>();
			public List<Entry> Entries => Wanted.Count > 0 ? Wanted : English;
		}

		private sealed class DescFile {
			public string Name;
			public readonly Dictionary<string, Block> ById = new Dictionary<string, Block>(StringComparer.Ordinal);
			public readonly HashSet<string> Silent = new HashSet<string>(StringComparer.Ordinal);
		}

		private readonly List<DescFile> files = new List<DescFile>();
		public int BlockCount { get; private set; }
		/// <summary>Files that could not be read or parsed, as "name: reason".</summary>
		public readonly List<string> Problems = new List<string>();

		/// <summary>Reads every stat description file from the index, keeping <paramref name="language"/> (null = English).</summary>
		public static StatDescriptions Load(BundleIndex index, string language) {
			var set = new StatDescriptions();
			foreach ( string path in index.EnumeratePaths(Folder.TrimEnd('/')).OrderBy(p => p, StringComparer.Ordinal) ) {
				if ( !path.EndsWith("_descriptions.txt", StringComparison.OrdinalIgnoreCase) ) continue;
				if ( path.IndexOf('/', Folder.Length) >= 0 ) continue;   // subfolders hold other things
				try {
					string text = index.ReadAllText(path);
					set.files.Add(ParseFile(Path.GetFileNameWithoutExtension(path), text, language ?? "English"));
				} catch ( Exception e ) {
					// One unreadable file should not take the rest down; its stats just stay undescribed.
					set.Problems.Add($"{Path.GetFileName(path)}: {e.Message}");
				}
			}
			set.BlockCount = set.files.Sum(f => f.ById.Values.Distinct().Count());
			return set;
		}

		/// <summary>Parses one file's text. Exposed for tests.</summary>
		public static StatDescriptions Parse(string name, string text, string language = "English") {
			var set = new StatDescriptions();
			set.files.Add(ParseFile(name, text, language));
			set.BlockCount = set.files[0].ById.Values.Distinct().Count();
			return set;
		}

		private static DescFile ParseFile(string name, string text, string wanted) {
			var file = new DescFile { Name = name };
			Block cur = null;
			bool needHeader = false;
			string lang = "English";

			foreach ( string raw in text.Split('\n') ) {
				string line = raw.TrimEnd('\r');
				if ( line.Length == 0 ) continue;
				if ( line[0] == '﻿' ) line = line.Substring(1);
				if ( line.Length == 0 ) continue;

				if ( line[0] != '\t' ) {
					if ( line == "description" ) {
						cur = new Block();
						needHeader = true;
						lang = "English";
					} else if ( line.StartsWith("no_description ", StringComparison.Ordinal) ) {
						file.Silent.Add(line.Substring("no_description ".Length).Trim());
						cur = null;
					}
					continue;
				}
				if ( cur == null ) continue;

				if ( line.StartsWith("\t\t", StringComparison.Ordinal) ) {
					Entry entry;
					try { entry = ParseEntry(line); }
					catch ( FormatException ) { continue; }   // skip a malformed line, keep the file
					if ( entry == null ) continue;
					if ( lang == "English" ) cur.English.Add(entry);
					if ( lang == wanted && wanted != "English" ) cur.Wanted.Add(entry);
					continue;
				}

				string t = line.Trim();
				if ( needHeader ) {
					needHeader = false;
					string[] parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
					if ( parts.Length < 2 || !int.TryParse(parts[0], out int n) || n != parts.Length - 1 ) { cur = null; continue; }
					cur.Ids = parts.Skip(1).ToArray();
					foreach ( string id in cur.Ids ) file.ById.TryAdd(id, cur);
				} else if ( t.StartsWith("lang ", StringComparison.Ordinal) ) {
					lang = t.Substring(5).Trim().Trim('"');
				}
				// anything else at one tab is a per-language line count, which the entries make redundant
			}
			return file;
		}

		private static Entry ParseEntry(string line) {
			int q1 = line.IndexOf('"');
			if ( q1 < 0 ) return null;
			int q2 = q1 + 1;
			while ( q2 < line.Length && line[q2] != '"' ) q2 += line[q2] == '\\' ? 2 : 1;
			if ( q2 >= line.Length ) return null;

			var entry = new Entry();
			entry.Conditions = line.Substring(0, q1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(ParseCondition).ToArray();
			entry.Template = Unescape(line.Substring(q1 + 1, q2 - q1 - 1));

			Handler last = null;
			foreach ( string tok in line.Substring(q2 + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries) ) {
				if ( int.TryParse(tok, out int arg) ) {
					last?.Args.Add(arg);
				} else {
					last = new Handler { Name = tok };
					entry.Handlers.Add(last);
				}
			}
			return entry;
		}

		private static Condition ParseCondition(string tok) {
			var c = new Condition { Lo = long.MinValue, Hi = long.MaxValue };
			if ( tok == "#" ) return c;
			if ( tok[0] == '!' ) {
				c.Not = true;
				c.Lo = c.Hi = long.Parse(tok.Substring(1), CultureInfo.InvariantCulture);
				return c;
			}
			int bar = tok.IndexOf('|');
			if ( bar < 0 ) {
				c.Lo = c.Hi = long.Parse(tok, CultureInfo.InvariantCulture);
				return c;
			}
			// "lo|hi"; the data has one stray "a|b|#" which is read as just "a|b".
			string[] parts = tok.Split('|');
			if ( parts[0] != "#" ) c.Lo = long.Parse(parts[0], CultureInfo.InvariantCulture);
			if ( parts[1] != "#" ) c.Hi = long.Parse(parts[1], CultureInfo.InvariantCulture);
			return c;
		}

		private static string Unescape(string s) {
			if ( s.IndexOf('\\') < 0 ) return s;
			var sb = new StringBuilder(s.Length);
			for ( int i = 0; i < s.Length; i++ ) {
				if ( s[i] == '\\' && i + 1 < s.Length ) {
					char n = s[++i];
					sb.Append(n == 'n' ? '\n' : n);
				} else sb.Append(s[i]);
			}
			return sb.ToString();
		}

		#region Rendering

		/// <summary>Which description file to try first for a mod domain; the main file and the rest follow.</summary>
		private static string PreferredFile(string domain) {
			if ( domain == null ) return null;
			if ( domain.StartsWith("HEIST", StringComparison.Ordinal) ) return "heist_equipment_stat_descriptions";
			switch ( domain ) {
				case "MONSTER": return "monster_stat_descriptions";
				case "AREA": case "MAP_DEVICE": return "map_stat_descriptions";
				case "CHEST": return "chest_stat_descriptions";
				case "ATLAS": return "atlas_stat_descriptions";
				case "LEAGUESTONE": return "leaguestone_stat_descriptions";
				case "SANCTUM_RELIC": return "sanctum_relic_stat_descriptions";
				case "EXPEDITION_RELIC": return "expedition_relic_stat_descriptions";
				case "SENTINEL": return "sentinel_stat_descriptions";
				case "TINCTURE": return "tincture_stat_descriptions";
				case "NECROPOLIS_MONSTER": return "necropolis_stat_descriptions";
				case "GRAFT": return "graft_stat_descriptions";
				default: return null;
			}
		}

		private bool TryFind(string id, string preferred, out Block block, out bool silent) {
			block = null;
			silent = false;
			// Preferred file, then the main file, then everything else in a fixed order.
			foreach ( var f in files.OrderBy(f => f.Name == preferred ? 0 : f.Name == "stat_descriptions" ? 1 : 2) ) {
				if ( f.ById.TryGetValue(id, out block) ) return true;
				if ( f.Silent.Contains(id) ) silent = true;
			}
			return false;
		}

		/// <summary>
		/// Turns a mod's stats into its display text, one line per description block, joined with
		/// newlines. Ranges show as "(8-12)". Stats that no file describes appear as "id: value" so a
		/// gap in the data is visible rather than silently dropped.
		/// </summary>
		public string Render(IList<StatValue> stats, string domain = null) {
			if ( stats.Count == 0 ) return "";
			string preferred = PreferredFile(domain);
			var done = new bool[stats.Count];
			var lines = new List<string>();

			for ( int i = 0; i < stats.Count; i++ ) {
				if ( done[i] ) continue;
				if ( !TryFind(stats[i].Id, preferred, out Block block, out bool silent) ) {
					done[i] = true;
					if ( !silent ) lines.Add($"{stats[i].Id}: {Range(stats[i].Min, stats[i].Max)}");
					continue;
				}

				// A block can cover several stats at once; gather this mod's values for all of them.
				var values = new StatValue[block.Ids.Length];
				for ( int k = 0; k < block.Ids.Length; k++ ) {
					values[k] = new StatValue(block.Ids[k], 0, 0);   // a stat the mod lacks counts as 0
					for ( int j = i; j < stats.Count; j++ ) {
						if ( done[j] || stats[j].Id != block.Ids[k] ) continue;
						values[k] = stats[j];
						done[j] = true;
						break;
					}
				}
				done[i] = true;
				string text = FormatBlock(block, values);
				if ( !string.IsNullOrEmpty(text) ) lines.Add(text);
			}
			return string.Join("\n", lines);
		}

		private static string Range(long min, long max) => min == max ? min.ToString(CultureInfo.InvariantCulture) : $"({min}-{max})";

		private static string FormatBlock(Block block, StatValue[] values) {
			var entries = block.Entries;
			if ( entries.Count == 0 ) return null;

			// Conditions test raw values. A rolled range is tested at its low end, then its high end.
			Entry chosen = Pick(entries, values, useMax: false) ?? Pick(entries, values, useMax: true) ?? entries[0];
			var shown = new Shown[values.Length];
			for ( int i = 0; i < values.Length; i++ ) shown[i] = Transform(chosen, i + 1, values[i]);

			var sb = new StringBuilder();
			string t = chosen.Template;
			int nextAuto = 0;
			for ( int p = 0; p < t.Length; p++ ) {
				if ( t[p] != '{' ) { sb.Append(t[p]); continue; }
				int close = t.IndexOf('}', p);
				if ( close < 0 ) { sb.Append(t, p, t.Length - p); break; }
				string spec = t.Substring(p + 1, close - p - 1);
				p = close;

				string idxText = spec, fmt = "";
				int colon = spec.IndexOf(':');
				if ( colon >= 0 ) { idxText = spec.Substring(0, colon); fmt = spec.Substring(colon + 1); }
				int idx = idxText.Length == 0 ? nextAuto++ : int.TryParse(idxText, out int n) ? n : -1;
				if ( idx < 0 || idx >= shown.Length ) { sb.Append('{').Append(spec).Append('}'); continue; }
				sb.Append(shown[idx].Text(fmt.StartsWith("+", StringComparison.Ordinal)));
			}
			return sb.ToString();
		}

		private static Entry Pick(List<Entry> entries, StatValue[] values, bool useMax) {
			foreach ( var e in entries ) {
				if ( e.Conditions.Length != values.Length ) continue;
				bool ok = true;
				for ( int i = 0; i < values.Length && ok; i++ )
					ok = e.Conditions[i].Matches(useMax ? values[i].Max : values[i].Min);
				if ( ok ) return e;
			}
			return null;
		}

		/// <summary>A value after its handlers ran, ready to print.</summary>
		private struct Shown {
			public double Min, Max;
			public int Dp;            // -1 = free
			public bool IfRequired;

			private string Num(double v) {
				string s;
				if ( Dp < 0 ) s = v.ToString("0.##", CultureInfo.InvariantCulture);
				else {
					s = v.ToString("F" + Dp, CultureInfo.InvariantCulture);
					if ( IfRequired && s.IndexOf('.') >= 0 ) s = s.TrimEnd('0').TrimEnd('.');
				}
				return s == "-0" ? "0" : s;
			}

			public string Text(bool signed) {
				string body = Min == Max ? Num(Min) : $"({Num(Min)}-{Num(Max)})";
				return signed && Min >= 0 ? "+" + body : body;
			}
		}

		private static readonly Regex DpSuffix = new Regex(@"_(\d)dp(_if_required)?$", RegexOptions.Compiled);

		private static Shown Transform(Entry entry, int oneBased, StatValue v) {
			var s = new Shown { Min = v.Min, Max = v.Max, Dp = -1 };
			foreach ( var h in entry.Handlers ) {
				if ( !h.Args.Contains(oneBased) ) continue;
				string name = h.Name;
				double scale = 1, add = 0;
				bool known = true;

				var dp = DpSuffix.Match(name);
				if ( dp.Success ) {
					s.Dp = dp.Groups[1].Value[0] - '0';
					s.IfRequired = dp.Groups[2].Success;
					name = name.Substring(0, dp.Index);
				} else if ( name.EndsWith("_if_required", StringComparison.Ordinal) ) {
					s.IfRequired = true;
					name = name.Substring(0, name.Length - "_if_required".Length);
				}

				switch ( name ) {
					case "negate": scale = -1; break;
					case "double": scale = 2; break;
					case "negate_and_double": scale = -2; break;
					case "divide_by_one_hundred_and_negate": scale = -0.01; break;
					case "divide_by_one_hundred": scale = 0.01; Default(ref s, 2); break;
					case "divide_by_one_thousand": scale = 0.001; Default(ref s, 3); break;
					case "divide_by_ten": scale = 0.1; Default(ref s, 1); break;
					case "milliseconds_to_seconds": scale = 0.001; Default(ref s, 3); break;
					case "deciseconds_to_seconds": scale = 0.1; Default(ref s, 1); break;
					case "per_minute_to_per_second": scale = 1.0 / 60; Default(ref s, 2); break;
					case "permyriad_per_minute_to_%_per_second": scale = 1.0 / 6000; Default(ref s, 2); break;
					case "locations_to_metres": scale = 0.1; Default(ref s, 1); break;
					case "divide_by_two": scale = 1.0 / 2; Default(ref s, 1); break;
					case "divide_by_three": scale = 1.0 / 3; Default(ref s, 1); break;
					case "divide_by_four": scale = 1.0 / 4; Default(ref s, 2); break;
					case "divide_by_five": scale = 1.0 / 5; Default(ref s, 1); break;
					case "divide_by_six": scale = 1.0 / 6; Default(ref s, 1); break;
					case "divide_by_twelve": scale = 1.0 / 12; Default(ref s, 1); break;
					case "divide_by_fifteen": scale = 1.0 / 15; Default(ref s, 1); break;
					case "divide_by_twenty": scale = 1.0 / 20; Default(ref s, 1); break;
					case "divide_by_twenty_then_double": scale = 0.1; Default(ref s, 1); break;
					case "times_twenty": scale = 20; break;
					case "times_one_point_five": scale = 1.5; Default(ref s, 1); break;
					case "30%_of_value": scale = 0.3; Default(ref s, 1); break;
					case "60%_of_value": scale = 0.6; Default(ref s, 1); break;
					case "plus_two_hundred": add = 200; break;
					case "multiplicative_damage_modifier": add = 100; break;
					case "old_leech_percent": scale = 0.2; Default(ref s, 2); break;
					case "old_leech_permyriad": scale = 0.02; Default(ref s, 2); break;
					default: known = false; break;   // display-only hints (canonical_line, reminderstring, ...)
				}
				if ( !known ) continue;
				s.Min = s.Min * scale + add;
				s.Max = s.Max * scale + add;
			}
			if ( s.Min > s.Max ) { double t = s.Min; s.Min = s.Max; s.Max = t; }   // negate flips a range
			return s;
		}

		/// <summary>Handlers without an explicit "_Ndp" still need a sensible number of decimals.</summary>
		private static void Default(ref Shown s, int dp) {
			if ( s.Dp >= 0 ) return;
			s.Dp = dp;
			s.IfRequired = true;
		}

		#endregion
	}

	/// <summary>
	/// Loads the stat descriptions once in the background and shares them: re-opening a table
	/// should not re-decode and re-parse tens of megabytes of text.
	/// </summary>
	public sealed class StatDescriptionCache {

		private static readonly object Gate = new object();
		private static BundleIndex cachedIndex;
		private static readonly Dictionary<string, StatDescriptionCache> Cache = new Dictionary<string, StatDescriptionCache>();

		private volatile StatDescriptions set;
		private volatile bool failed;

		public bool Ready => set != null;
		public bool Failed => failed;
		public StatDescriptions Set => set;

		/// <summary>The (possibly still loading) descriptions for a language; "English" for the default.</summary>
		public static StatDescriptionCache Get(BundleIndex index, string language) {
			lock ( Gate ) {
				if ( !ReferenceEquals(cachedIndex, index) ) {
					Cache.Clear();
					cachedIndex = index;
				}
				if ( Cache.TryGetValue(language, out var existing) ) return existing;
				var c = new StatDescriptionCache();
				Cache[language] = c;
				var t = new Thread(() => {
					try { c.set = StatDescriptions.Load(index, language); }
					catch ( Exception ) { c.failed = true; }
				}) { IsBackground = true, Name = "AtE BundleBrowser stat descriptions" };
				t.Start();
				return c;
			}
		}
	}
}
