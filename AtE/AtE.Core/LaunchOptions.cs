using System;
using System.Linq;

namespace AtE {
	/// <summary>
	/// Options given on the command line (none by default, so a normal launch behaves as before):
	///   --campaign       switch the Campaign routine on as soon as the game is ready (to resume after a relaunch)
	///   --explore-exits  with no exit known to lead to the next area, walk to the unnamed exits in turn (instead of stopping)
	/// </summary>
	public static class LaunchOptions {
		private static readonly string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
		private static bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
		public static readonly bool Campaign = Has("--campaign");
		public static readonly bool ExploreExits = Has("--explore-exits");
	}
}
