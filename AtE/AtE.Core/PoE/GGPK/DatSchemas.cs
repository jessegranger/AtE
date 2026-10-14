using System;
using System.Globalization;
using System.Text;

namespace AtE {

	/// <summary>
	/// The table definitions the Bundle Browser recognises, kept in dat-schema syntax (see
	/// <see cref="DatSchema"/>) so more tables can be added by pasting from
	/// https://github.com/poe-tool-dev/dat-schema/blob/main/dat-schema/_Core.gql.
	/// Column order and types must match the installed game version exactly: one wrong column size
	/// shifts everything after it, which the viewer reports as a row-size mismatch.
	/// </summary>
	public static class DatSchemas {

		public static readonly DatSchema Default = DatSchema.Parse(Text);

		/// <summary>The schema text. Mods is from dat-schema (PoE 1), fetched 2026-10.</summary>
		public const string Text = """
enum ModDomains @indexing(first: 1) {
  ITEM FLASK MONSTER CHEST AREA _ SANCTUM_RELIC _ CRAFTED BASE_JEWEL ATLAS LEAGUESTONE ABYSS_JEWEL MAP_DEVICE
  DUMMY DELVE DELVE_AREA SYNTHESIS_A SYNTHESIS_GLOBALS SYNTHESIS_BONUS AFFLICTION_JEWEL HEIST_AREA HEIST_NPC
  HEIST_TRINKET WATCHSTONE VEILED EXPEDITION_RELIC UNVEILED ELDRITCH_ALTAR SENTINEL MEMORY_LINE SANCTUM_SPECIAL
  CRUCIBLE_MAP TINCTURE ANIMAL_CHARM NECROPOLIS_MONSTER IDOL GRAFT
}

enum ModGenerationType @indexing(first: 1) {
  PREFIX SUFFIX UNIQUE NEMESIS CORRUPTED BLOODLINES TORMENT TEMPEST TALISMAN ENCHANTMENT ESSENCE _ BESTIARY
  DELVE_AREA SYNTHESIS_A SYNTHESIS_GLOBALS SYNTHESIS_BONUS BLIGHT BLIGHT_TOWER MONSTER_AFFLICTION
  FLASK_ENCHANTMENT_ENKINDLING FLASK_ENCHANTMENT_INSTILLING EXPEDITION_LOGBOOK SCOURGE_UPSIDE SCOURGE_DOWNSIDE
  SCOURGE_MAP _ EXARCH_IMPLICIT EATER_IMPLICIT _ WEAPON_TREE WEAPON_TREE_RECOMBINED _ NECROPOLIS_HAUNTED
  NECROPOLIS_DEVOTED MEMORY_ALTAR
}

enum InfluenceTypes @indexing(first: 0) {
  SHAPER ELDER CRUSADER EYRIE BASILISK ADJUDICATOR NONE
}

enum ModAuraFlags @indexing(first: 0) { _ }

enum GameModes @indexing(first: 0) {
  ALL NORMAL RUTHLESS
}

type Mods @tags(list: ["crafting"]) {
  Id: string @unique
  HASH16: u16
  ModTypeKey: ModType
  Level: i32
  StatsKey1: Stats
  StatsKey2: Stats
  StatsKey3: Stats
  StatsKey4: Stats
  Domain: ModDomains
  Name: string
  GenerationType: ModGenerationType
  Families: [ModFamily]
  Stat1Min: i32
  Stat1Max: i32
  Stat2Min: i32
  Stat2Max: i32
  Stat3Min: i32
  Stat3Max: i32
  Stat4Min: i32
  Stat4Max: i32
  SpawnWeight_TagsKeys: [Tags]
  SpawnWeight_Values: [i32]
  TagsKeys: [Tags]
  GrantedEffectsPerLevelKeys: [GrantedEffectsPerLevel]
  AuraFlags: [ModAuraFlags]
  MonsterMetadata: string
  MonsterKillAchievements: [AchievementItems]
  ArchnemesisType: [ModType]
  Stat5Min: i32
  Stat5Max: i32
  StatsKey5: Stats
  FullAreaClear_AchievementItemsKey: [AchievementItems]
  AchievementItemsKey: [AchievementItems]
  GenerationWeight_TagsKeys: [Tags]
  GenerationWeight_Values: [i32]
  ModifyMapsAchievements: [AchievementItems]
  IsEssenceOnlyModifier: bool
  Stat6Min: i32
  Stat6Max: i32
  StatsKey6: Stats
  MaxLevel: i32
  _: bool
  CraftingItemClassRestrictions: [ItemClasses]
  MonsterOnDeath: string
  _: i32
  HeistAchievements: [AchievementItems]
  Stat7Min: i32
  Stat7Max: i32
  StatsKey7: Stats
  StatsKey8: Stats
  Stat8Min: i32
  Stat8Max: i32
  InfluenceTypes: InfluenceTypes
  ImplicitTagsKeys: [Tags]
  HasHardmodeStatValues: bool
  Stat1MinHardmode: i32
  Stat1MaxHardmode: i32
  Stat2MinHardmode: i32
  Stat2MaxHardmode: i32
  Stat3MinHardmode: i32
  Stat3MaxHardmode: i32
  Stat4MinHardmode: i32
  Stat4MaxHardmode: i32
  Stat5MinHardmode: i32
  Stat5MaxHardmode: i32
  Stat6MinHardmode: i32
  Stat6MaxHardmode: i32
  Stat7MinHardmode: i32
  Stat7MaxHardmode: i32
  Stat8MinHardmode: i32
  Stat8MaxHardmode: i32
  BuffTemplate1: BuffTemplates
  ArchnemesisMinionMod: Mods
  HASH32: u32
  BuffTemplate2: [BuffTemplates]
  GameMode: GameModes
  GrantedEffectRuthless: [GrantedEffectsPerLevel]
  IsUnscalable: bool
}
""";

		private const ulong NullRow = 0xFEFEFEFEFEFEFEFEUL;
		private const int MaxArrayItemsShown = 8;

		/// <summary>
		/// Renders one cell as text. Never throws: bad data shows up as a marker in the cell.
		/// With <paramref name="export"/> set the text is meant for a file rather than the screen:
		/// arrays are complete and joined with "; " (no count, no "..."), and null references are blank.
		/// </summary>
		public static string FormatCell(DatFile file, DatColumn col, int row, int rowSize, DatRefResolver refs = null, bool export = false) {
			int at = file.RowOffset(row) + col.Offset;
			if ( col.Offset + col.Size > rowSize ) return "";   // schema is wider than the file's rows
			try {
				if ( !col.IsArray ) return FormatValue(file, col, at, refs, export);

				int elem = col.ElementSize;
				if ( !file.TryReadArray(at, elem, out int count, out int start) )
					return elem == 0 ? "[?]" : "<bad array>";
				if ( count == 0 ) return export ? "" : "[]";
				if ( elem == 0 ) return export ? "" : $"[{count}]";
				if ( export ) {
					var all = new StringBuilder();
					for ( int i = 0; i < count; i++ ) {
						if ( i > 0 ) all.Append("; ");
						all.Append(FormatValue(file, col, start + i * elem, refs, true));
					}
					return all.ToString();
				}
				var sb = new StringBuilder();
				sb.Append('[').Append(count).Append("] ");
				int shown = Math.Min(count, MaxArrayItemsShown);
				for ( int i = 0; i < shown; i++ ) {
					if ( i > 0 ) sb.Append(", ");
					sb.Append(FormatValue(file, col, start + i * elem, refs, false));
				}
				if ( count > shown ) sb.Append(", ...");
				return sb.ToString();
			} catch ( Exception e ) {
				return $"<{e.GetType().Name}>";
			}
		}

		private static string FormatValue(DatFile file, DatColumn col, int at, DatRefResolver refs, bool export) {
			switch ( col.Type ) {
				case DatType.Bool: return file.Bytes[at] != 0 ? "true" : "false";
				case DatType.I8: return file.ReadInt(at, 1, true).ToString(CultureInfo.InvariantCulture);
				case DatType.U8: return file.ReadInt(at, 1, false).ToString(CultureInfo.InvariantCulture);
				case DatType.I16: return file.ReadInt(at, 2, true).ToString(CultureInfo.InvariantCulture);
				case DatType.U16: return file.ReadInt(at, 2, false).ToString(CultureInfo.InvariantCulture);
				case DatType.I32: return file.ReadInt(at, 4, true).ToString(CultureInfo.InvariantCulture);
				case DatType.U32: return file.ReadInt(at, 4, false).ToString(CultureInfo.InvariantCulture);
				case DatType.I64: return file.ReadInt(at, 8, true).ToString(CultureInfo.InvariantCulture);
				case DatType.U64: return file.ReadU64(at).ToString(CultureInfo.InvariantCulture);
				case DatType.F32: return file.ReadFloat(at).ToString("0.###", CultureInfo.InvariantCulture);
				case DatType.String: return file.ReadStringAt(at);
				case DatType.Enum: return col.Enum.NameOf(file.ReadInt(at, 4, false));
				case DatType.Ref: {
					ulong row = file.ReadU64(at);
					if ( row == NullRow ) return export ? "" : "null";
					return refs?.Label(col.RefTable, row) ?? $"{col.RefTable}[{row}]";
				}
				default: return "?";
			}
		}
	}
}
