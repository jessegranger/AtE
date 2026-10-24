using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AtE.Plugins {

	/// <summary>
	/// A scrolling grid over one .datc64 file, laid out by a <see cref="DatTable"/> schema. Render
	/// thread only. Only the rows and columns actually on screen are formatted each frame.
	///
	/// ImGui 1.87 tables hold at most 64 columns, and drawing past that corrupts memory rather than
	/// failing cleanly, so a wide table (Mods has 80+) shows at most <see cref="MaxVisibleColumns"/>
	/// of its columns at a time; the rest are switched on from the column list.
	/// </summary>
	internal sealed class DatTableView {

		/// <summary>ImGui's IMGUI_TABLE_MAX_COLUMNS is 64; this leaves room for the "#" column and some slack.</summary>
		private const int MaxVisibleColumns = 60;

		public readonly string Path;
		private readonly DatFile file;
		private readonly DatTable table;   // null when no schema is known for this file

		/// <summary>The schema columns that fit inside the file's real row size.</summary>
		private readonly List<DatColumn> columns = new List<DatColumn>();

		/// <summary>One grid column after "#": a schema column, or one derived from the stats (Mods only).</summary>
		private sealed class Col {
			public string Name;
			public DatColumn Schema;        // null for derived columns
			public bool IsModText;
			public int StatSlot = -1;       // 0-7 for a StatsKeyNText column
			public float Width;
			public bool Visible;
		}
		private readonly List<Col> display = new List<Col>();

		private readonly DatRefResolver refs = new DatRefResolver();

		private string filter = "";
		private string appliedFilter = "";
		private bool filterHasText;
		private int[] rows;
		private bool showColumns;

		// ---- Mods only: text derived from each mod's stats ------------------------------------
		private const string ModTextHeader = "Mod text";
		private StatDescriptionCache descriptions;          // in the table's language: for "Mod text"
		private StatDescriptionCache englishDescriptions;   // always English: for the raw format strings
		private DatColumn[] statKeyCols, statMinCols, statMaxCols;
		private DatColumn domainCol;
		private readonly object textGate = new object();
		private bool textStarted;
		private volatile string[] modText;       // one entry per row; set last, once everything below is ready
		private volatile string[][] statText;    // [slot 0-7][row]: the English format string of StatsKeyN
		private bool HasModText => descriptions != null;

		public DatTableView(string path, DatFile file, DatTable table, BundleIndex index) {
			Path = path;
			this.file = file;
			this.table = table;
			if ( table != null ) {
				var targets = new List<string>();
				foreach ( var c in table.Columns ) {
					if ( c.Offset + c.Size > file.RowSize ) continue;
					columns.Add(c);
					if ( c.Type == DatType.Ref && c.RefTable != "?" && !c.IsSelfRef && !targets.Contains(c.RefTable) )
						targets.Add(c.RefTable);
				}
				// The table's own rows label its self-references; everything else loads in the background.
				refs.Seed(table.Name, file);
				int slash = path.LastIndexOf('/');
				if ( index != null ) refs.Request(index, slash < 0 ? "" : path.Substring(0, slash), targets);
				if ( index != null && table.Name == "Mods" ) SetUpModText(index, slash < 0 ? "" : path.Substring(0, slash));
				BuildDisplay();
			}
			ApplyFilter();
		}

		/// <summary>Lays out the grid columns and picks which ones start out visible (the first <see cref="MaxVisibleColumns"/>).</summary>
		private void BuildDisplay() {
			if ( HasModText ) display.Add(new Col { Name = ModTextHeader, IsModText = true, Width = 420f });
			foreach ( var c in columns ) {
				display.Add(new Col { Name = c.Name, Schema = c, Width = InitialWidth(c) });
				// A StatsKeyN column is followed by the format string it points at.
				if ( HasModText && c.Name.Length == 9 && c.Name.StartsWith("StatsKey", StringComparison.Ordinal)
						&& c.Name[8] >= '1' && c.Name[8] <= '8' ) {
					int slot = c.Name[8] - '1';
					display.Add(new Col { Name = $"{c.Name}Text", StatSlot = slot, Width = 320f });
				}
			}
			ShowFirstColumns();
		}

		private void ShowFirstColumns() {
			for ( int i = 0; i < display.Count; i++ ) display[i].Visible = i < MaxVisibleColumns;
		}

		private int VisibleCount() {
			int n = 0;
			foreach ( var c in display ) if ( c.Visible ) n++;
			return n;
		}

		/// <summary>Finds the stat columns by name and starts loading the stat description files.</summary>
		private void SetUpModText(BundleIndex index, string folder) {
			DatColumn Find(string name) => columns.Find(c => c.Name == name);
			var keys = new DatColumn[8]; var mins = new DatColumn[8]; var maxs = new DatColumn[8];
			for ( int i = 0; i < 8; i++ ) {
				keys[i] = Find($"StatsKey{i + 1}");
				mins[i] = Find($"Stat{i + 1}Min");
				maxs[i] = Find($"Stat{i + 1}Max");
				if ( keys[i] == null || mins[i] == null || maxs[i] == null ) return;   // layout differs; skip rather than guess
			}
			statKeyCols = keys; statMinCols = mins; statMaxCols = maxs;
			domainCol = Find("Domain");
			descriptions = StatDescriptionCache.Get(index, LanguageOf(folder));
			englishDescriptions = StatDescriptionCache.Get(index, "English");
		}

		/// <summary>"data/spanish" -> "Spanish"; the plain data folder is English.</summary>
		private static string LanguageOf(string folder) {
			const string prefix = "data/";
			if ( !folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folder.Length == prefix.Length ) return "English";
			return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(folder.Substring(prefix.Length).ToLowerInvariant());
		}

		private bool TextFailed => HasModText && (descriptions.Failed || englishDescriptions.Failed);

		/// <summary>
		/// Once the descriptions and the Stats table (for stat Ids) are in, works out every mod's text
		/// and format strings on a worker and publishes them. Safe to call repeatedly from any thread.
		/// </summary>
		private void TryStartModText() {
			if ( !HasModText || modText != null || TextFailed ) return;
			if ( !descriptions.Ready || !englishDescriptions.Ready || !refs.IsLoaded("Stats") ) return;
			lock ( textGate ) {
				if ( textStarted ) return;
				textStarted = true;
			}
			var t = new System.Threading.Thread(() => {
				var localized = descriptions.Set;
				var english = englishDescriptions.Set;
				int n = file.RowCount;
				var text = new string[n];
				var formats = new string[8][];
				for ( int k = 0; k < 8; k++ ) formats[k] = new string[n];

				var stats = new List<StatValue>(8);
				var slotIndex = new int[8];
				for ( int r = 0; r < n; r++ ) {
					stats.Clear();
					int row = file.RowOffset(r);
					for ( int k = 0; k < 8; k++ ) {
						slotIndex[k] = -1;
						ulong target = file.ReadU64(row + statKeyCols[k].Offset);
						if ( target == 0xFEFEFEFEFEFEFEFEUL ) continue;
						string id = refs.Label("Stats", target) ?? $"Stats[{target}]";
						slotIndex[k] = stats.Count;
						stats.Add(new StatValue(id, file.ReadInt(row + statMinCols[k].Offset, 4, true), file.ReadInt(row + statMaxCols[k].Offset, 4, true)));
					}
					string domain = domainCol == null ? null : DatSchemas.FormatCell(file, domainCol, r, file.RowSize);
					text[r] = localized.Render(stats, domain);
					for ( int k = 0; k < 8; k++ )
						formats[k][r] = slotIndex[k] < 0 ? "" : english.FormatString(stats, slotIndex[k], domain);
				}
				statText = formats;
				modText = text;   // last: its being set means everything is
			}) { IsBackground = true, Name = "AtE BundleBrowser mod text" };
			t.Start();
		}

		private string ModTextStatus {
			get {
				if ( modText != null ) return null;
				if ( TextFailed ) return "stat descriptions could not be loaded";
				return "building mod text ...";
			}
		}

		public string Summary =>
			$"{file.RowCount:N0} rows x {file.RowSize} bytes" +
			(table != null ? $", schema \"{table.Name}\" ({table.Columns.Count} columns, {table.RowSize} bytes)" : ", no schema for this file");

		/// <summary>True when a saved schema laid this file out, i.e. there are named columns to export.</summary>
		public bool HasSchema => table != null;

		/// <summary>
		/// Writes every row and every column (visible or not) to a CSV (UTF-8 with a BOM so Excel
		/// reads the text correctly). Blocks, so call it off the render thread. Waits for the
		/// referenced tables and the mod text to finish loading first, so references export as names
		/// rather than "Table[n]". Returns the number of rows written.
		/// </summary>
		public int ExportCsv(string outPath) {
			for ( int waited = 0; waited < 600; waited++ ) {
				TryStartModText();
				bool refsDone = refs.Pending == 0;
				bool textDone = !HasModText || modText != null || TextFailed;
				if ( refsDone && textDone ) break;
				System.Threading.Thread.Sleep(100);
			}
			string[] text = modText;
			string[][] formats = statText;

			string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
			if ( !string.IsNullOrEmpty(dir) ) System.IO.Directory.CreateDirectory(dir);

			using ( var w = new System.IO.StreamWriter(outPath, false, new System.Text.UTF8Encoding(true)) ) {
				w.Write("#");
				foreach ( var col in display ) { w.Write(','); w.Write(CsvEscape(col.IsModText ? "ModText" : col.Name)); }
				w.Write("\r\n");
				for ( int r = 0; r < file.RowCount; r++ ) {
					w.Write(r);
					foreach ( var col in display ) {
						w.Write(',');
						string cell;
						if ( col.IsModText ) cell = text?[r] ?? "";
						else if ( col.StatSlot >= 0 ) cell = formats?[col.StatSlot][r] ?? "";
						else cell = DatSchemas.FormatCell(file, col.Schema, r, file.RowSize, refs, export: true);
						w.Write(CsvEscape(cell));
					}
					w.Write("\r\n");
				}
			}
			return file.RowCount;
		}

		private static readonly char[] CsvSpecial = { ',', (char)34, (char)13, (char)10 };

		private static string CsvEscape(string s) {
			if ( s.IndexOfAny(CsvSpecial) < 0 ) return s;
			return "\"" + s.Replace("\"", "\"\"") + "\"";
		}

		private bool SchemaMatches => table != null && table.RowSize == file.RowSize;

		private void ApplyFilter() {
			appliedFilter = filter;
			string[] text = modText;
			filterHasText = text != null;
			string term = filter.Trim();
			if ( term.Length == 0 ) {
				rows = new int[file.RowCount];
				for ( int i = 0; i < rows.Length; i++ ) rows[i] = i;
				return;
			}
			// Match against the row number, the mod text (once built) and the plain string columns.
			// Formatting every cell of a big table per keystroke would be far too slow, and the
			// strings are where names live.
			var stringCols = new List<DatColumn>();
			foreach ( var c in columns )
				if ( c.Type == DatType.String && !c.IsArray ) stringCols.Add(c);

			var hits = new List<int>();
			for ( int r = 0; r < file.RowCount; r++ ) {
				bool hit = r.ToString().Equals(term, StringComparison.Ordinal)
					|| (text != null && text[r].IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
				for ( int k = 0; !hit && k < stringCols.Count; k++ ) {
					string s = file.ReadStringAt(file.RowOffset(r) + stringCols[k].Offset);
					hit = s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
				}
				if ( hit ) hits.Add(r);
			}
			rows = hits.ToArray();
		}

		/// <summary>
		/// Text in a warning colour. Not ImGui.TextColored: that is printf-style, and these strings
		/// can carry arbitrary text such as an exception message containing a percent sign.
		/// </summary>
		private static void Warning(string message) {
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.3f, 1f));
			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(message);
			ImGui.PopTextWrapPos();
			ImGui.PopStyleColor();
		}

		public unsafe void Draw() {
			ImGui.TextUnformatted(Summary);

			if ( table == null ) {
				ImGui.TextUnformatted("This file has no saved schema, so its columns are unknown. " +
					"Add its table to DatSchemas.Text to browse it.");
				return;
			}
			if ( !SchemaMatches ) {
				Warning($"Row size mismatch: the schema expects {table.RowSize} bytes per row but this file has " +
					$"{file.RowSize}. The game version differs from the saved schema, so columns after the first change " +
					"may be misaligned" + (columns.Count < table.Columns.Count ? $" ({table.Columns.Count - columns.Count} trailing column(s) do not fit and are hidden)." : "."));
			}

			ImGui.SetNextItemWidth(220f);
			ImGui.InputText("Filter (row # or text)##datfilter", ref filter, 128);
			TryStartModText();
			// Re-filter when the mod text lands, so an active search starts covering it.
			if ( filter != appliedFilter || (modText != null && !filterHasText) ) ApplyFilter();
			ImGui.SameLine();
			ImGui.TextDisabled($"{rows.Length:N0} shown");
			if ( refs.Pending > 0 ) {
				ImGui.SameLine();
				ImGui.TextDisabled($"resolving {refs.Pending} referenced table(s) ...");
			}
			if ( HasModText && ModTextStatus != null ) {
				ImGui.SameLine();
				ImGui.TextDisabled(ModTextStatus);
			}
			if ( HasModText && descriptions.Ready && descriptions.Set.Problems.Count > 0 ) {
				Warning($"{descriptions.Set.Problems.Count} stat description file(s) failed to load: {descriptions.Set.Problems[0]}");
			}
			ImGui.SameLine();
			int visibleCount = VisibleCount();
			ImGui.Checkbox($"Columns ({visibleCount}/{display.Count})##datcolumns", ref showColumns);

			if ( showColumns ) DrawColumnList(visibleCount);

			var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY
				| ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
			var shown = new List<Col>(visibleCount);
			foreach ( var c in display ) if ( c.Visible ) shown.Add(c);
			if ( shown.Count > MaxVisibleColumns ) shown.RemoveRange(MaxVisibleColumns, shown.Count - MaxVisibleColumns );   // belt and braces
			if ( !ImGui.BeginTable("dattable##" + Path, shown.Count + 1, flags, new Vector2(0, 0)) ) return;

			ImGui.TableSetupScrollFreeze(1, 1);
			ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 56f);
			foreach ( var c in shown )
				ImGui.TableSetupColumn(c.Name, ImGuiTableColumnFlags.WidthFixed, c.Width);
			ImGui.TableHeadersRow();

			string[] text = modText;
			string[][] formats = statText;
			var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
			clipper.Begin(rows.Length);
			while ( clipper.Step() ) {
				for ( int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++ ) {
					int r = rows[i];
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.TextUnformatted(r.ToString());
					for ( int c = 0; c < shown.Count; c++ ) {
						// Returns false for columns scrolled out of view; skip formatting those.
						if ( !ImGui.TableSetColumnIndex(c + 1) ) continue;
						var col = shown[c];
						string cell;
						if ( col.IsModText ) cell = text == null ? "..." : text[r].Replace("\n", " / ");   // lines of a multi-line mod
						else if ( col.StatSlot >= 0 ) cell = formats == null ? "..." : formats[col.StatSlot][r];
						else cell = DatSchemas.FormatCell(file, col.Schema, r, file.RowSize, refs);
						ImGui.TextUnformatted(cell);
					}
				}
			}
			clipper.End();
			ImGuiNative.ImGuiListClipper_destroy(clipper.NativePtr);
			ImGui.EndTable();
		}

		/// <summary>Which columns the grid shows, with each one's place in the row for reference.</summary>
		private void DrawColumnList(int visibleCount) {
			if ( ImGui.Button("First " + MaxVisibleColumns + "##datcolsfirst") ) ShowFirstColumns();
			ImGui.SameLine();
			if ( ImGui.Button("None##datcolsnone") ) foreach ( var c in display ) c.Visible = false;
			ImGui.SameLine();
			ImGui.TextDisabled($"at most {MaxVisibleColumns} columns can be shown at once");

			ImGui.BeginChild("datcolumnlist", new Vector2(0, 200), true);
			for ( int i = 0; i < display.Count; i++ ) {
				var c = display[i];
				bool capped = !c.Visible && visibleCount >= MaxVisibleColumns;
				if ( capped ) ImGui.BeginDisabled();
				bool on = c.Visible;
				if ( ImGui.Checkbox($"##datcol{i}", ref on) ) {
					c.Visible = on;
					visibleCount += on ? 1 : -1;
				}
				if ( capped ) ImGui.EndDisabled();
				ImGui.SameLine();
				string info = c.Schema != null
					? $"+0x{c.Schema.Offset:X3}  {c.Schema.Size,2}b  {c.Name}: {c.Schema.TypeName}"
					: c.IsModText ? $"{c.Name}  (derived from the stats)"
					: $"{c.Name}  (English format string)";
				ImGui.TextUnformatted(info);
			}
			ImGui.EndChild();
		}

		private static float InitialWidth(DatColumn c) {
			if ( c.IsArray ) return 200f;
			switch ( c.Type ) {
				case DatType.String: return 180f;
				case DatType.Ref: return 130f;
				case DatType.Enum: return 130f;
				case DatType.Bool: return 60f;
				default: return 80f;
			}
		}
	}
}
