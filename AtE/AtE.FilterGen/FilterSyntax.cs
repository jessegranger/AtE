using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AtE.FilterGen {

	/// <summary>
	/// The bits of PoE item filter syntax both the compiler and the extractor need.
	/// A filter is a list of blocks: a line at column 0 starting with Show / Hide / Minimal,
	/// then indented lines (conditions and actions), ended by a blank or unindented line.
	/// </summary>
	public static class FilterSyntax {

		public static readonly Regex BlockHeader = new Regex(@"^(Show|Hide|Minimal)\b");

		/// <summary>
		/// Actions that set how a matched item looks or sounds. Actions in the same slot replace each other
		/// (a block plays one alert sound, whichever keyword set it), so a later SetStyle or explicit line wins.
		/// </summary>
		private static readonly Dictionary<string, string> ActionSlots = new Dictionary<string, string>(StringComparer.Ordinal) {
			{ "SetFontSize", "SetFontSize" },
			{ "SetTextColor", "SetTextColor" },
			{ "SetBorderColor", "SetBorderColor" },
			{ "SetBackgroundColor", "SetBackgroundColor" },
			{ "PlayAlertSound", "AlertSound" },
			{ "PlayAlertSoundPositional", "AlertSound" },
			{ "CustomAlertSound", "AlertSound" },
			{ "CustomAlertSoundOptional", "AlertSound" },
			{ "DisableDropSound", "DropSound" },
			{ "EnableDropSound", "DropSound" },
			{ "DisableDropSoundIfAlertSound", "DropSoundIfAlert" },
			{ "EnableDropSoundIfAlertSound", "DropSoundIfAlert" },
			{ "MinimapIcon", "MinimapIcon" },
			{ "PlayEffect", "PlayEffect" },
		};

		public const string Continue = "Continue";

		/// <summary>First word of a trimmed line ("" for an empty one).</summary>
		public static string Keyword(string trimmed) {
			int end = 0;
			while ( end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '#' ) end++;
			return trimmed.Substring(0, end);
		}

		public static bool IsAction(string keyword) => ActionSlots.ContainsKey(keyword);

		/// <summary>The slot a style line fills, or null if it is not a style line. Continue is its own slot.</summary>
		public static string SlotOf(string keyword) =>
			keyword == Continue ? Continue
			: ActionSlots.TryGetValue(keyword, out var slot) ? slot
			: null;

		public static bool IsIndented(string line) => line.Length > 0 && (line[0] == '\t' || line[0] == ' ');

		public static bool IsBlank(string line) => line.Trim().Length == 0;

		/// <summary>Splits text into lines and reports which newline it used, so output can match the input.</summary>
		public static string[] SplitLines(string text, out string newline) {
			newline = text.Contains("\r\n") ? "\r\n" : "\n";
			return text.Replace("\r\n", "\n").Split('\n');
		}
	}
}
