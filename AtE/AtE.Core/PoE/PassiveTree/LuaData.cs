using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AtE {

	/// <summary>
	/// A Lua table read by <see cref="LuaData"/>: keyed entries ([key]= value, name = value) and list entries (value,
	/// value, ...), which Lua numbers from 1. Values are LuaTable, string, double, bool or null.
	/// </summary>
	public sealed class LuaTable {
		public readonly Dictionary<object, object> Fields = new Dictionary<object, object>();
		public readonly List<object> List = new List<object>();

		/// <summary>The value at key (a string, or a whole number for [123]= ...), or null.</summary>
		public object this[object key] => Fields.TryGetValue(Key(key), out var v) ? v : null;

		public LuaTable Table(object key) => this[key] as LuaTable;
		public string String(object key) => this[key] as string;
		public double Number(object key, double missing = 0) => this[key] is double d ? d : missing;
		public bool Bool(object key) => this[key] is bool b && b;

		// numeric keys are stored as long, so [3]= and 3 find the same entry
		internal static object Key(object key) => key switch {
			double d when d == Math.Floor(d) => (long)d,
			int i => (long)i,
			_ => key,
		};
	}

	/// <summary>
	/// Reads a Lua file that is one table literal, like Path of Building's TreeData/*/tree.lua (`return { ... }`).
	/// Only literals: tables, strings, numbers, true / false / nil, and -- comments; no expressions.
	/// </summary>
	public sealed class LuaData {
		private readonly string text;
		private int pos;

		private LuaData(string text) => this.text = text;

		/// <summary>The value of the file (after an optional `return`).</summary>
		public static object Parse(string text) {
			var p = new LuaData(text);
			p.SkipSpace();
			if ( p.text.AsSpan(p.pos).StartsWith("return") ) { p.pos += "return".Length; p.SkipSpace(); }
			var value = p.Value();
			p.SkipSpace();
			if ( p.pos < p.text.Length ) throw p.Error("expected the end of the file");
			return value;
		}

		private object Value() {
			SkipSpace();
			if ( pos >= text.Length ) throw Error("expected a value");
			char c = text[pos];
			if ( c == '{' ) return Table();
			if ( c == '"' || c == '\'' ) return String();
			if ( c == '-' || c == '.' || char.IsDigit(c) ) return Number();
			string word = Word();
			switch ( word ) {
				case "true": return true;
				case "false": return false;
				case "nil": return null;
				default: throw Error($"unexpected '{word}'");
			}
		}

		private LuaTable Table() {
			var table = new LuaTable();
			pos++; // {
			while ( true ) {
				SkipSpace();
				if ( pos >= text.Length ) throw Error("unclosed table");
				if ( text[pos] == '}' ) { pos++; return table; }
				if ( text[pos] == '[' ) {
					// [key]= value
					pos++;
					var key = Value();
					SkipSpace();
					Expect(']');
					SkipSpace();
					Expect('=');
					table.Fields[LuaTable.Key(key)] = Value();
				} else if ( char.IsLetter(text[pos]) || text[pos] == '_' ) {
					// name = value, or a bare true / false / nil in a list
					int start = pos;
					string word = Word();
					SkipSpace();
					if ( pos < text.Length && text[pos] == '=' ) {
						pos++;
						table.Fields[word] = Value();
					} else {
						pos = start;
						table.List.Add(Value());
					}
				} else {
					table.List.Add(Value());
				}
				SkipSpace();
				if ( pos < text.Length && (text[pos] == ',' || text[pos] == ';') ) pos++;
			}
		}

		private string String() {
			char quote = text[pos++];
			var sb = new StringBuilder();
			while ( true ) {
				if ( pos >= text.Length ) throw Error("unclosed string");
				char c = text[pos++];
				if ( c == quote ) return sb.ToString();
				if ( c != '\\' ) { sb.Append(c); continue; }
				char e = text[pos++];
				switch ( e ) {
					case 'n': sb.Append('\n'); break;
					case 't': sb.Append('\t'); break;
					case 'r': sb.Append('\r'); break;
					case '\n': sb.Append('\n'); break;
					default:
						if ( char.IsDigit(e) ) {
							// \ddd, a decimal byte
							int n = e - '0';
							for ( int i = 0; i < 2 && pos < text.Length && char.IsDigit(text[pos]); i++ ) n = (n * 10) + (text[pos++] - '0');
							sb.Append((char)n);
						} else {
							sb.Append(e); // \\ \" \'
						}
						break;
				}
			}
		}

		private double Number() {
			int start = pos;
			if ( text[pos] == '-' ) pos++;
			while ( pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '.'
				|| ((text[pos] == '-' || text[pos] == '+') && (text[pos - 1] == 'e' || text[pos - 1] == 'E'))) ) pos++;
			var s = text.Substring(start, pos - start);
			if ( double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ) return d;
			bool negative = s.StartsWith("-");
			var hex = negative ? s.Substring(1) : s;
			if ( hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				&& long.TryParse(hex.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h) ) return negative ? -h : h;
			throw Error($"bad number '{s}'");
		}

		private string Word() {
			int start = pos;
			while ( pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_') ) pos++;
			return text.Substring(start, pos - start);
		}

		private void SkipSpace() {
			while ( pos < text.Length ) {
				if ( char.IsWhiteSpace(text[pos]) ) { pos++; continue; }
				if ( pos + 1 < text.Length && text[pos] == '-' && text[pos + 1] == '-' ) {
					// -- to the end of the line ([[ ]] block comments are not used in the tree data)
					while ( pos < text.Length && text[pos] != '\n' ) pos++;
					continue;
				}
				return;
			}
		}

		private void Expect(char c) {
			if ( pos >= text.Length || text[pos] != c ) throw Error($"expected '{c}'");
			pos++;
		}

		private FormatException Error(string what) {
			int line = 1;
			for ( int i = 0; i < pos && i < text.Length; i++ ) if ( text[i] == '\n' ) line++;
			return new FormatException($"Lua data, line {line}: {what}");
		}
	}
}
