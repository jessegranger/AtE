using System.Linq;
using AtE.FilterGen;
using Xunit;

namespace AtE.Tests {

	/// <summary>The loot filter generator: Style / SetStyle expansion and style extraction.</summary>
	public class FilterGenTests {

		private static string Lines(params string[] lines) => string.Join("\n", lines);

		private static string Compile(string src) {
			var res = FilterCompiler.Compile(src);
			Assert.True(res.Ok, string.Join("; ", res.Errors));
			return res.Output;
		}

		private static readonly string GreyCircleAlert = Lines(
			"Style GreyCircleAlert",
			"\tSetFontSize 45",
			"\tSetTextColor 0 0 0 255",
			"\tSetBorderColor 0 0 0 255",
			"\tSetBackgroundColor 210 178 135 255",
			"\tPlayAlertSound 2 300",
			"\tPlayEffect Grey",
			"\tMinimapIcon 2 Grey Circle",
			"");

		[Fact]
		public void ExpandsStyleInPlace() {
			string src = Lines(GreyCircleAlert,
				"Show",
				"\tClass == \"Stackable Currency\"",
				"\tBaseType == \"Orb of Transmutation\"",
				"\tAreaLevel <= 34",
				"\tSetStyle GreyCircleAlert",
				"");
			Assert.Equal(Lines(
				"Show",
				"\tClass == \"Stackable Currency\"",
				"\tBaseType == \"Orb of Transmutation\"",
				"\tAreaLevel <= 34",
				"\tSetFontSize 45",
				"\tSetTextColor 0 0 0 255",
				"\tSetBorderColor 0 0 0 255",
				"\tSetBackgroundColor 210 178 135 255",
				"\tPlayAlertSound 2 300",
				"\tPlayEffect Grey",
				"\tMinimapIcon 2 Grey Circle",
				""), Compile(src));
		}

		[Fact]
		public void BlockActionsOverrideStyleWhereverTheyAre() {
			string src = Lines(GreyCircleAlert,
				"Show",
				"\tSetFontSize 30",
				"\tSetStyle GreyCircleAlert",
				"\tMinimapIcon 0 Red Star",
				"\tPlayAlertSoundPositional 6 200",
				"");
			var outLines = Compile(src).Split('\n');
			Assert.Equal("\tSetFontSize 45", outLines[1]); // the style came later, so it wins
			Assert.Contains("\tMinimapIcon 0 Red Star", outLines);
			Assert.Contains("\tPlayAlertSoundPositional 6 200", outLines);
			Assert.DoesNotContain("\tPlayAlertSound 2 300", outLines); // same sound slot
			Assert.Single(outLines, l => l.StartsWith("\tMinimapIcon"));
		}

		[Fact]
		public void StylesComposeAndBuildOnEachOther() {
			string src = Lines(
				"Style Tan",
				"\tSetFontSize 45",
				"\tSetBackgroundColor 210 178 135 255",
				"",
				"Style GreyCircle",
				"\tPlayEffect Grey",
				"\tMinimapIcon 2 Grey Circle",
				"",
				"Style TanAlert",
				"\tSetStyle Tan GreyCircle",
				"\tPlayAlertSound 2 300",
				"",
				"Show",
				"\tSetStyle TanAlert",
				"\tContinue",
				"",
				"Hide",
				"\tSetStyle Tan",
				"\tSetFontSize 18",
				"");
			Assert.Equal(Lines(
				"Show",
				"\tSetFontSize 45",
				"\tSetBackgroundColor 210 178 135 255",
				"\tPlayEffect Grey",
				"\tMinimapIcon 2 Grey Circle",
				"\tPlayAlertSound 2 300",
				"\tContinue",
				"",
				"Hide",
				"\tSetFontSize 18",
				"\tSetBackgroundColor 210 178 135 255",
				""), Compile(src));
		}

		[Fact]
		public void SourceOnlyLinesAreDroppedWithTheirBlankLine() {
			string src = Lines(
				"# kept",
				"",
				"// dropped",
				"",
				"Style A",
				"\tSetFontSize 40",
				"",
				"Show # $tier->x !a",
				"\tSetStyle A",
				"");
			Assert.Equal(Lines("# kept", "", "Show # $tier->x !a", "\tSetFontSize 40", ""), Compile(src));
		}

		[Fact]
		public void ReportsMistakes() {
			string src = Lines(
				"Style A",
				"\tSetStyle B",
				"Style B",
				"\tSetStyle A",
				"Style C",
				"\tRarity Rare",
				"Style C",
				"\tSetFontSize 1",
				"Show",
				"\tSetStyle A Nope",
				"SetStyle C");
			var errors = FilterCompiler.Compile(src).Errors.Select(e => e.ToString()).ToList();
			Assert.Contains(errors, e => e.Contains("uses itself"));
			Assert.Contains(errors, e => e.StartsWith("line 6:") && e.Contains("'Rarity'"));
			Assert.Contains(errors, e => e.StartsWith("line 7:") && e.Contains("already defined"));
			Assert.Contains(errors, e => e.StartsWith("line 10:") && e.Contains("unknown style 'Nope'"));
			Assert.Contains(errors, e => e.StartsWith("line 11:") && e.Contains("outside"));
		}

		[Fact]
		public void ImportSplicesFilesRelativeToTheImporter() {
			var files = new System.Collections.Generic.Dictionary<string, string> {
				[System.IO.Path.GetFullPath("filters/styles/base.filtersrc")] = Lines("// palette", "", GreyCircleAlert),
				[System.IO.Path.GetFullPath("filters/styles.filtersrc")] = Lines("Import \"styles/base.filtersrc\"", "", "Style Big", "\tSetFontSize 45", ""),
				[System.IO.Path.GetFullPath("filters/currency.filtersrc")] = Lines("# currency", "Show", "\tSetStyle GreyCircleAlert Big", ""),
			};
			string main = Lines(
				"# my filter",
				"",
				"Import \"styles.filtersrc\"",
				"",
				"Import currency.filtersrc",
				"",
				"Hide",
				"\tSetStyle Big",
				"");
			var res = FilterCompiler.Compile(main, "filters/main.filtersrc", p => files[p]);
			Assert.True(res.Ok, string.Join("; ", res.Errors));
			Assert.Equal(Lines(
				"# my filter",
				"",
				"# currency",
				"Show",
				"\tSetFontSize 45",
				"\tSetTextColor 0 0 0 255",
				"\tSetBorderColor 0 0 0 255",
				"\tSetBackgroundColor 210 178 135 255",
				"\tPlayAlertSound 2 300",
				"\tPlayEffect Grey",
				"\tMinimapIcon 2 Grey Circle",
				"",
				"Hide",
				"\tSetFontSize 45",
				""), res.Output);
		}

		[Fact]
		public void ImportReportsMissingFilesCyclesAndWhereErrorsAre() {
			var files = new System.Collections.Generic.Dictionary<string, string> {
				[System.IO.Path.GetFullPath("a.filtersrc")] = Lines("Import \"b.filtersrc\""),
				[System.IO.Path.GetFullPath("b.filtersrc")] = Lines("Style X", "\tRarity Rare", "Import \"a.filtersrc\""),
			};
			string Read(string p) => files.TryGetValue(p, out var t) ? t : throw new System.IO.FileNotFoundException("not found", p);
			var errors = FilterCompiler.Compile(Lines("Import a.filtersrc", "Import nope.filtersrc"), "main.filtersrc", Read)
				.Errors.Select(e => e.ToString()).ToList();
			Assert.Contains(errors, e => e.StartsWith("b.filtersrc(3):") && e.Contains("imports itself"));
			Assert.Contains(errors, e => e.StartsWith("b.filtersrc(2):") && e.Contains("'Rarity'"));
			Assert.Contains(errors, e => e.StartsWith("main.filtersrc(2):") && e.Contains("cannot import 'nope.filtersrc'"));
		}

		[Fact]
		public void ExtractCanPutStylesInTheirOwnFile() {
			string filter = Lines(
				"# banner",
				"",
				"Show # !currency_e",
				"\tBaseType == \"Portal Scroll\"",
				"\tSetFontSize 45",
				"");
			var res = StyleExtractor.Extract(filter, "x.styles.filtersrc");
			Assert.True(res.RoundTrips);
			Assert.Equal(Lines("# banner", "", "Import \"x.styles.filtersrc\"", "", "Show # !currency_e", "\tBaseType == \"Portal Scroll\"", "\tSetStyle CurrencyE", ""), res.Output);
			Assert.Contains("Style CurrencyE\n\tSetFontSize 45", res.StylesOutput);
		}

		[Fact]
		public void ExtractNamesStylesFromTagsAndRoundTrips() {
			string filter = Lines(
				"# banner",
				"",
				"Show # %H4 $type->currency $tier->a !currency_e",
				"\tBaseType == \"Orb of Transmutation\"",
				"\tSetFontSize 45",
				"\tSetBackgroundColor 210 178 135 255",
				"\tPlayAlertSound 2 300",
				"\tMinimapIcon 2 Grey Circle",
				"",
				"Show # %H4 $type->currency $tier->b !currency_e",
				"\tBaseType == \"Orb of Augmentation\"",
				"\tSetFontSize 45",
				"\tSetBackgroundColor 210 178 135 255",
				"\tPlayAlertSound 2 300",
				"\tMinimapIcon 2 Grey Circle",
				"",
				"Show # %H4 $type->currency $tier->c !currency_e",
				"\tBaseType == \"Portal Scroll\"",
				"\tSetFontSize 45",
				"\tSetBackgroundColor 210 178 135 255",
				"",
				"Hide # no tag",
				"\tSetFontSize 18",
				"");
			var res = StyleExtractor.Extract(filter);
			Assert.True(res.RoundTrips);
			Assert.Equal(2, res.StyleCount);
			Assert.Equal(3, res.BlocksStyled);
			Assert.Contains("Style CurrencyE # used by 2 blocks", res.Output);
			Assert.Contains("Style CurrencyEQuiet", res.Output);
			Assert.Contains("\tSetFontSize 18", res.Output);
			Assert.Equal(filter, FilterCompiler.Compile(res.Output).Output);
		}
	}
}
