using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The gems Path of Building knows (its Data/Gems.lua): each one's colour and tags, for gems we have not seen in the
	/// game yet. Entries look like:
	///
	///   ["Metadata/Items/Gems/SkillGemSupportCombustion"] = {
	///     name = "Combustion", gameId = "Metadata/Items/Gems/SupportGemChanceToIgnite",
	///     tags = { fire = true, intelligence = true, support = true, }, reqStr = 40, reqDex = 0, reqInt = 60, ... },
	///
	/// The colour is the attribute tag (strength: red, dexterity: green, intelligence: blue, none: white), not the
	/// biggest req (Combustion's reqStr is 40, and it is blue).
	/// </summary>
	public static class PobGemData {
		public sealed class Gem {
			public string Name;
			public string GameId;
			public SocketColor Color;
			public HashSet<string> Tags;
		}

		/// <summary>Where Gems.lua is looked for: a Data folder beside the exe, then PoB's (AppData/Path of Building Community/Data).</summary>
		public static IEnumerable<string> Files() {
			yield return Path.Combine(AppContext.BaseDirectory, "Data", "Gems.lua");
			yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Path of Building Community", "Data", "Gems.lua");
		}

		private static Dictionary<string, Gem> byId;
		/// <summary>Why the gem data could not be read, or null.</summary>
		public static string Error { get; private set; }
		public static int Count => All().Count;

		/// <summary>A gem by its gameId (the item's metadata path, as in a build's gemId), PoB's own id, or its name; null if unknown.</summary>
		public static Gem Find(string idOrName) =>
			idOrName != null && All().TryGetValue(idOrName.ToLowerInvariant(), out var gem) ? gem : null;

		public static Gem Find(PobGem gem) => Find(gem?.GemId) ?? Find(gem?.Name);

		/// <summary>A gem's colour, or None if it is not known.</summary>
		public static SocketColor ColorOf(PobGem gem) => Find(gem)?.Color ?? SocketColor.None;

		private static Dictionary<string, Gem> All() {
			if ( byId != null ) return byId;
			byId = new Dictionary<string, Gem>();
			var file = Files().FirstOrDefault(File.Exists);
			if ( file == null ) { Error = $"no Gems.lua in: {string.Join("; ", Files())}"; return byId; }
			try {
				Load(File.ReadAllText(file), byId);
			} catch ( Exception e ) {
				Error = $"{file}: {e.Message}";
				Log($"PobGemData: {Error}");
			}
			return byId;
		}

		/// <summary>Read the text of a Gems.lua into index (by gameId, PoB id and name, lower case; the first one of a name wins).</summary>
		public static void Load(string text, Dictionary<string, Gem> index) {
			if ( !(LuaData.Parse(text) is LuaTable root) ) throw new FormatException("not a Lua table");
			foreach ( var (key, value) in root.Fields ) {
				if ( !(value is LuaTable t) ) continue;
				var tags = new HashSet<string>((t.Table("tags")?.Fields.Where(f => f.Value is bool b && b).Select(f => f.Key as string) ?? Enumerable.Empty<string>())
					.Where(s => s != null), StringComparer.OrdinalIgnoreCase);
				var gem = new Gem {
					Name = t.String("name"),
					GameId = t.String("gameId") ?? key as string,
					Tags = tags,
					Color = tags.Contains("strength") ? SocketColor.Red : tags.Contains("dexterity") ? SocketColor.Green
						: tags.Contains("intelligence") ? SocketColor.Blue : SocketColor.White,
				};
				foreach ( var id in new[] { gem.GameId, key as string, gem.Name } ) {
					if ( id != null ) index.TryAdd(id.ToLowerInvariant(), gem);
				}
			}
		}
	}
}
