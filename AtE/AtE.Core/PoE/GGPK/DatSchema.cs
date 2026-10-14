using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AtE {

	public enum DatType { Bool, I8, U8, I16, U16, I32, U32, I64, U64, F32, String, Ref, Enum, Unknown }

	/// <summary>One column of a table: where it sits in the row and how to read it.</summary>
	public sealed class DatColumn {
		public string Name;
		public DatType Type;
		public bool IsArray;
		/// <summary>For <see cref="DatType.Ref"/>: the table the row index points into.</summary>
		public string RefTable;
		/// <summary>A reference to a row of the table that holds this column: 8 bytes, not 16.</summary>
		public bool IsSelfRef;
		public DatEnum Enum;
		public int Offset;
		/// <summary>Bytes the column occupies in a row (16 for any array).</summary>
		public int Size;

		public int ElementSize => IsSelfRef ? 8 : ElementSizeOf(Type);

		public string TypeName {
			get {
				string t = Type == DatType.Ref ? RefTable
					: Type == DatType.Enum ? Enum.Name
					: Type == DatType.Unknown ? "_"
					: Type.ToString().ToLowerInvariant();
				return IsArray ? $"[{t}]" : t;
			}
		}

		public static int ElementSizeOf(DatType type) {
			switch ( type ) {
				case DatType.Bool: case DatType.I8: case DatType.U8: return 1;
				case DatType.I16: case DatType.U16: return 2;
				case DatType.I32: case DatType.U32: case DatType.F32: case DatType.Enum: return 4;
				case DatType.I64: case DatType.U64: case DatType.String: return 8;
				case DatType.Ref: return 16;   // u64 row index + u64 filler
				default: return 0;
			}
		}
	}

	public sealed class DatEnum {
		public string Name;
		public int First;
		public string[] Names;
		public string NameOf(long value) {
			long i = value - First;
			if ( i < 0 || i >= Names.Length || Names[i] == "_" ) return value.ToString(CultureInfo.InvariantCulture);
			return Names[i];
		}
	}

	public sealed class DatTable {
		public string Name;
		public readonly List<DatColumn> Columns = new List<DatColumn>();
		/// <summary>Sum of the columns, i.e. the row size this schema expects of the file.</summary>
		public int RowSize;
	}

	/// <summary>
	/// A set of table definitions in the community dat-schema (GraphQL-style) syntax, as kept at
	/// https://github.com/poe-tool-dev/dat-schema. Only what is needed to size and read columns is
	/// interpreted: <c>type</c> and <c>enum</c> blocks, <c>[array]</c> fields, <c>_</c> for unnamed
	/// columns, and <c>@indexing(first: n)</c> on enums. Other directives, comments and field
	/// descriptions are ignored, so schema text can be pasted straight from the upstream repository.
	///
	/// A field type that names an <c>enum</c> is a 4-byte value. Any other non-primitive name is a
	/// foreign-row reference to that table (defined here or not): 16 bytes, or 8 when the table
	/// references itself, which the files store as a bare row index.
	/// </summary>
	public sealed class DatSchema {

		private readonly Dictionary<string, DatTable> tables = new Dictionary<string, DatTable>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, DatEnum> enums = new Dictionary<string, DatEnum>(StringComparer.Ordinal);

		public IEnumerable<string> TableNames => tables.Keys;

		/// <summary>Looks a table up by name, or by a path like "data/mods.datc64".</summary>
		public bool TryGetTable(string nameOrPath, out DatTable table) {
			string name = nameOrPath ?? "";
			int slash = name.LastIndexOfAny(new[] { '/', '\\' });
			if ( slash >= 0 ) name = name.Substring(slash + 1);
			int dot = name.IndexOf('.');
			if ( dot >= 0 ) name = name.Substring(0, dot);
			return tables.TryGetValue(name, out table);
		}

		private static readonly Regex Block = new Regex(@"\b(type|enum)\s+(\w+)\s*([^{}]*)\{([^}]*)\}", RegexOptions.Compiled);
		private static readonly Regex Field = new Regex(@"^\s*(\w+)\s*:\s*(\[)?\s*(\w+)\s*\]?", RegexOptions.Compiled);
		private static readonly Regex First = new Regex(@"first\s*:\s*(-?\d+)", RegexOptions.Compiled);

		public static DatSchema Parse(string text) {
			var schema = new DatSchema();
			var cleaned = new StringBuilder();
			foreach ( string raw in text.Split('\n') ) {
				string line = raw.Trim();
				if ( line.StartsWith("\"") ) continue;                // field description
				int hash = line.IndexOf('#');
				if ( hash >= 0 ) line = line.Substring(0, hash);
				cleaned.Append(line).Append('\n');
			}
			string clean = cleaned.ToString();

			var matches = Block.Matches(clean);
			// Enums first: field typing needs to know which names are enums wherever they are declared.
			foreach ( Match m in matches ) {
				if ( m.Groups[1].Value != "enum" ) continue;
				var m1 = First.Match(m.Groups[3].Value);
				schema.enums[m.Groups[2].Value] = new DatEnum {
					Name = m.Groups[2].Value,
					First = m1.Success ? int.Parse(m1.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
					Names = m.Groups[4].Value.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries),
				};
			}
			foreach ( Match m in matches ) {
				if ( m.Groups[1].Value != "type" ) continue;
				var table = new DatTable { Name = m.Groups[2].Value };
				int offset = 0;
				foreach ( string line in m.Groups[4].Value.Split('\n') ) {
					var f = Field.Match(line);
					if ( !f.Success ) continue;
					var col = schema.MakeColumn(table.Name, f.Groups[1].Value, f.Groups[3].Value, f.Groups[2].Success);
					col.Offset = offset;
					offset += col.Size;
					if ( col.Name == "_" ) col.Name = $"_{col.Offset:X}";   // unnamed: label by position
					table.Columns.Add(col);
				}
				table.RowSize = offset;
				schema.tables[table.Name] = table;
			}
			return schema;
		}

		private DatColumn MakeColumn(string tableName, string name, string type, bool array) {
			var col = new DatColumn { Name = name, IsArray = array };
			switch ( type ) {
				case "bool": col.Type = DatType.Bool; break;
				case "i8": col.Type = DatType.I8; break;
				case "u8": col.Type = DatType.U8; break;
				case "i16": col.Type = DatType.I16; break;
				case "u16": col.Type = DatType.U16; break;
				case "i32": col.Type = DatType.I32; break;
				case "u32": col.Type = DatType.U32; break;
				case "i64": col.Type = DatType.I64; break;
				case "u64": col.Type = DatType.U64; break;
				case "f32": col.Type = DatType.F32; break;
				case "string": col.Type = DatType.String; break;
				case "_": col.Type = DatType.Unknown; break;   // only meaningful inside an array: [_]
				default:
					if ( enums.TryGetValue(type, out DatEnum e) ) { col.Type = DatType.Enum; col.Enum = e; }
					else {
						col.Type = DatType.Ref;
						col.RefTable = type == "rid" ? "?" : type;
						col.IsSelfRef = type == tableName;
					}
					break;
			}
			col.Size = array ? 16 : col.ElementSize;
			return col;
		}
	}
}
