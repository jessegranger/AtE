using System;
using System.IO;
using System.Text;

namespace AtE.FilterGen {

	public static class Program {

		private const string Usage =
@"AtE.FilterGen - Path of Exile loot filter generator

  compile <in.filtersrc> [out.filter]    expand Style/SetStyle macros into a plain filter
  extract <in.filter> [out.filtersrc]    pull the style lines of a filter out into named Styles,
                                         written to <out>.styles.filtersrc and imported by <out>
    --inline                             keep the styles at the top of <out> instead

Without an output path, the input's name is used with the other extension.";

		public static int Main(string[] args) {
			bool inline = Array.IndexOf(args, "--inline") >= 0;
			args = Array.FindAll(args, a => a != "--inline");
			if ( args.Length < 2 || args.Length > 3 ) {
				Console.Error.WriteLine(Usage);
				return 2;
			}
			string input = args[1];
			if ( !File.Exists(input) ) {
				Console.Error.WriteLine($"{input}: not found");
				return 1;
			}
			// Filters are plain ASCII/UTF-8 without a BOM; keep it that way.
			var utf8 = new UTF8Encoding(false);
			string text = File.ReadAllText(input, utf8);

			switch ( args[0] ) {
				case "compile": {
					string output = args.Length > 2 ? args[2] : Path.ChangeExtension(input, ".filter");
					var res = FilterCompiler.Compile(text, input, p => File.ReadAllText(p, utf8));
					foreach ( var e in res.Errors ) Console.Error.WriteLine($"{e.File ?? input}({e.Line}): {e.Message}");
					if ( !res.Ok ) return 1;
					File.WriteAllText(output, res.Output, utf8);
					Console.WriteLine($"wrote {output}");
					return 0;
				}
				case "extract": {
					string output = args.Length > 2 ? args[2] : Path.ChangeExtension(input, ".filtersrc");
					string stylesPath = inline ? null : Path.ChangeExtension(output, ".styles.filtersrc");
					var res = StyleExtractor.Extract(text, stylesPath == null ? null : Path.GetFileName(stylesPath));
					File.WriteAllText(output, res.Output, utf8);
					if ( stylesPath != null ) {
						File.WriteAllText(stylesPath, res.StylesOutput, utf8);
						Console.WriteLine($"wrote {stylesPath}");
					}
					Console.WriteLine($"wrote {output}: {res.StyleCount} styles, {res.BlocksStyled} blocks use SetStyle, {res.BlocksLeftInline} kept their own style lines");
					if ( !res.RoundTrips ) {
						Console.Error.WriteLine("warning: compiling the result does not give back the input exactly");
						return 1;
					}
					Console.WriteLine("round trip ok: compiling it gives back the input exactly");
					return 0;
				}
				default:
					Console.Error.WriteLine(Usage);
					return 2;
			}
		}
	}
}
