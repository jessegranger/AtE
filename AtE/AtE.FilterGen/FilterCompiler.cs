using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AtE.FilterGen {

	public sealed class FilterError {
		public string File; // the file the line is in; null for the top-level source when it has no path
		public int Line;    // 1-based
		public string Message;
		public override string ToString() => File == null ? $"line {Line}: {Message}" : $"{File}({Line}): {Message}";
	}

	public sealed class CompileResult {
		public string Output;
		public List<FilterError> Errors = new List<FilterError>();
		public bool Ok => Errors.Count == 0;
	}

	/// <summary>
	/// Compiles a .filtersrc into a .filter the game reads. The source is a normal filter plus:
	///
	///   Style Name                 a named set of actions (indented lines below it), defined at column 0.
	///   	SetFontSize 45           Style bodies hold actions, Continue, and SetStyle (to build on other styles).
	///   	SetStyle OtherStyle
	///
	///   	SetStyle A B             inside a block: expands to the actions of A, then B. Later wins per slot
	///                              (font size, text colour, alert sound, ...), and so does any action the
	///                              block writes itself, whether before or after the SetStyle line.
	///
	///   // comment                 source-only comment, not written out. (# comments are written out.)
	///
	///   Import "styles.filtersrc"  at column 0: replaced by the named file's lines, as if pasted in (its final
	///                              newline aside). The path is relative to the importing file. Styles defined
	///                              in any file can be used in any other.
	///
	/// Style definitions and // lines are removed, each with the one blank line that follows it.
	/// An action keeps the position where its slot was first filled, so expanding a style in place gives
	/// the same line order as writing the actions out by hand.
	/// </summary>
	public static class FilterCompiler {

		private static readonly Regex StyleHeader = new Regex(@"^Style\s+(\S+)\s*(#.*)?$");
		private static readonly Regex StyleName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");
		private static readonly Regex ImportLine = new Regex(@"^Import\s+(?:""([^""]+)""|([^\s#""]+))\s*(#.*)?$");

		/// <summary>One line of the combined source, and where it came from.</summary>
		private struct SourceLine {
			public string Text;
			public string File;
			public int Line; // 0-based
		}

		private sealed class StyleDef {
			public string Name;
			public int Line;
			public List<(int line, string text)> Body = new List<(int, string)>();
			public List<string> Resolved; // flattened action lines, null until resolved
			public bool Resolving;
		}

		/// <param name="source">The .filtersrc text.</param>
		/// <param name="path">Where the source is, for resolving Import paths and naming files in errors. Null: the current directory.</param>
		/// <param name="readFile">Reads an imported file by full path (File.ReadAllText when null); tests and the extractor pass their own.</param>
		public static CompileResult Compile(string source, string path = null, Func<string, string> readFile = null) {
			var result = new CompileResult();
			readFile ??= p => File.ReadAllText(p);
			FilterSyntax.SplitLines(source, out string newline);
			var combined = new List<SourceLine>();
			Flatten(source, path, readFile, combined, new List<string>(), result.Errors);
			var lines = combined.Select(l => l.Text).ToArray();
			var styles = new Dictionary<string, StyleDef>(StringComparer.Ordinal);
			void Error(int i, string msg) => result.Errors.Add(new FilterError { File = combined[i].File, Line = combined[i].Line + 1, Message = msg });
			string Where(int i) => combined[i].File == null ? $"line {combined[i].Line + 1}" : $"{combined[i].File}({combined[i].Line + 1})";

			// Pass 1: collect Style definitions, so a style can be used before it is defined.
			for ( int i = 0; i < lines.Length; i++ ) {
				var m = StyleHeader.Match(lines[i]);
				if ( !m.Success ) continue;
				var def = new StyleDef { Name = m.Groups[1].Value, Line = i };
				if ( !StyleName.IsMatch(def.Name) ) Error(i, $"bad style name '{def.Name}' (letters, digits and _ only)");
				else if ( styles.ContainsKey(def.Name) ) Error(i, $"style '{def.Name}' is already defined at {Where(styles[def.Name].Line)}");
				else styles[def.Name] = def;
				for ( int j = i + 1; j < lines.Length && FilterSyntax.IsIndented(lines[j]) && !FilterSyntax.IsBlank(lines[j]); j++ ) {
					def.Body.Add((j, lines[j].Trim()));
				}
			}
			// Check every style, used or not, so a mistake shows up where it is written.
			foreach ( var def in styles.Values ) Resolve(def, styles, Error);

			// Pass 2: write everything else out, expanding SetStyle inside blocks.
			var sb = new StringBuilder();
			bool first = true;
			void Emit(string line) {
				if ( !first ) sb.Append(newline);
				sb.Append(line);
				first = false;
			}

			bool dropNextBlank = false;
			for ( int i = 0; i < lines.Length; i++ ) {
				string line = lines[i];

				if ( StyleHeader.IsMatch(line) ) {
					while ( i + 1 < lines.Length && FilterSyntax.IsIndented(lines[i + 1]) && !FilterSyntax.IsBlank(lines[i + 1]) ) i++;
					dropNextBlank = true;
					continue;
				}
				if ( line.TrimStart().StartsWith("//") ) {
					dropNextBlank = true;
					continue;
				}
				if ( FilterSyntax.IsBlank(line) && dropNextBlank ) {
					dropNextBlank = false;
					continue;
				}
				dropNextBlank = false;

				if ( FilterSyntax.BlockHeader.IsMatch(line) ) {
					Emit(line);
					var body = new List<(int line, string text)>();
					while ( i + 1 < lines.Length && FilterSyntax.IsIndented(lines[i + 1]) && !FilterSyntax.IsBlank(lines[i + 1]) ) {
						i++;
						body.Add((i, lines[i].Trim()));
					}
					foreach ( var outLine in ExpandBlock(body, styles, Error) ) Emit("\t" + outLine);
					continue;
				}

				if ( FilterSyntax.Keyword(line.Trim()) == "SetStyle" ) Error(i, "SetStyle outside a Show/Hide block");
				Emit(line);
			}

			result.Output = sb.ToString();
			return result;
		}

		/// <summary>
		/// Appends the source's lines to <paramref name="output"/>, with each Import replaced by the imported file's lines.
		/// </summary>
		private static void Flatten(string source, string path, Func<string, string> readFile, List<SourceLine> output, List<string> importing, List<FilterError> errors) {
			var lines = FilterSyntax.SplitLines(source, out _);
			string name = path == null ? null : Path.GetFileName(path);
			if ( importing.Count > 0 && lines.Length > 1 && lines[lines.Length - 1].Length == 0 ) {
				Array.Resize(ref lines, lines.Length - 1); // an imported file's final newline ends its last line, it is not a blank line
			}
			for ( int i = 0; i < lines.Length; i++ ) {
				var m = ImportLine.Match(lines[i]);
				if ( m.Success ) {
					string rel = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
					string full = Path.GetFullPath(Path.Combine(path == null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(path)), rel));
					string text = null;
					if ( importing.Contains(full, StringComparer.OrdinalIgnoreCase) || (path != null && string.Equals(full, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) ) {
						errors.Add(new FilterError { File = name, Line = i + 1, Message = $"'{rel}' imports itself" });
					} else {
						try { text = readFile(full); }
						catch ( Exception e ) when ( e is IOException || e is UnauthorizedAccessException ) {
							errors.Add(new FilterError { File = name, Line = i + 1, Message = $"cannot import '{rel}': {e.Message}" });
						}
					}
					if ( text != null ) {
						importing.Add(full);
						Flatten(text, full, readFile, output, importing, errors);
						importing.RemoveAt(importing.Count - 1);
					}
					continue;
				}
				output.Add(new SourceLine { Text = lines[i], File = name, Line = i });
			}
		}

		/// <summary>The block's lines with SetStyle expanded and each action slot filled once (last value wins).</summary>
		private static List<string> ExpandBlock(List<(int line, string text)> body, Dictionary<string, StyleDef> styles, Action<int, string> error) {
			var output = new List<string>();
			var slotIndex = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach ( var (lineNo, text) in body ) {
				if ( FilterSyntax.Keyword(text) == "SetStyle" ) {
					foreach ( var styleLine in ResolveSetStyle(lineNo, text, styles, error) ) Merge(output, slotIndex, styleLine);
				} else {
					Merge(output, slotIndex, text);
				}
			}
			// Continue must be the block's last line wherever it came from.
			if ( slotIndex.TryGetValue(FilterSyntax.Continue, out int c) && c != output.Count - 1 ) {
				output.RemoveAt(c);
				output.Add(FilterSyntax.Continue);
			}
			return output;
		}

		private static void Merge(List<string> output, Dictionary<string, int> slotIndex, string text) {
			string slot = FilterSyntax.SlotOf(FilterSyntax.Keyword(text));
			if ( slot == null ) {
				output.Add(text); // a condition or comment: may repeat (HasExplicitMod, ...), keep as is
			} else if ( slotIndex.TryGetValue(slot, out int at) ) {
				output[at] = text;
			} else {
				slotIndex[slot] = output.Count;
				output.Add(text);
			}
		}

		private static IEnumerable<string> ResolveSetStyle(int lineNo, string text, Dictionary<string, StyleDef> styles, Action<int, string> error) {
			var names = StripComment(text).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
			if ( names.Count == 0 ) error(lineNo, "SetStyle needs at least one style name");
			foreach ( var name in names ) {
				if ( !styles.TryGetValue(name, out var def) ) {
					error(lineNo, $"unknown style '{name}'");
					continue;
				}
				foreach ( var l in Resolve(def, styles, error) ) yield return l;
			}
		}

		private static List<string> Resolve(StyleDef def, Dictionary<string, StyleDef> styles, Action<int, string> error) {
			if ( def.Resolved != null ) return def.Resolved;
			if ( def.Resolving ) {
				error(def.Line, $"style '{def.Name}' uses itself (through SetStyle)");
				return new List<string>();
			}
			def.Resolving = true;
			var output = new List<string>();
			var slotIndex = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach ( var (lineNo, text) in def.Body ) {
				string kw = FilterSyntax.Keyword(text);
				if ( text.StartsWith("#") || text.StartsWith("//") ) continue;
				if ( kw == "SetStyle" ) {
					foreach ( var l in ResolveSetStyle(lineNo, text, styles, error) ) Merge(output, slotIndex, l);
				} else if ( FilterSyntax.SlotOf(kw) != null ) {
					Merge(output, slotIndex, text);
				} else {
					error(lineNo, $"style '{def.Name}' can only hold actions, Continue and SetStyle, not '{kw}'");
				}
			}
			def.Resolving = false;
			def.Resolved = output;
			return output;
		}

		private static string StripComment(string text) {
			int hash = text.IndexOf('#');
			return hash < 0 ? text : text.Substring(0, hash);
		}
	}
}
