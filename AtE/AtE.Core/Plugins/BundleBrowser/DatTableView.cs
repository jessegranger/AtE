using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AtE.Plugins {

	/// <summary>
	/// A scrolling grid over one .datc64 file, laid out by a <see cref="DatTable"/> schema. Render
	/// thread only. Only the rows and columns actually on screen are formatted each frame.
	/// </summary>
	internal sealed class DatTableView {

		public readonly string Path;
		private readonly DatFile file;
		private readonly DatTable table;   // null when no schema is known for this file

		/// <summary>The columns that fit inside the file's real row size.</summary>
		private readonly List<DatColumn> columns = new List<DatColumn>();

		private readonly DatRefResolver refs = new DatRefResolver();

		private string filter = "";
		private string appliedFilter = "";
		private bool filterHasText;
		private int[] rows;
		private bool showLayout;

		// ---- Mods only: the display text of each mod, derived from its stats ---------------------
		private const string ModTextHeader = "Mod text";
		private StatDescriptionCache descriptions;
		private DatColumn[] statKeyCols, statMinCols, statMaxCols;
		private DatColumn domainCol;
		private readonly object textGate = new object();
		private bool textStarted;
		private volatile string[] modText;   // one entry per row, set once everything it needs is loaded
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
			}
			ApplyFilter();
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
		}

		/// <summary>"data/spanish" -> "Spanish"; the plain data folder is English.</summary>
		private static string LanguageOf(string folder) {
			const string prefix = "data/";
			if ( !folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folder.Length == prefix.Length ) return "English";
			return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(folder.Substring(prefix.Length).ToLowerInvariant());
		}

		/// <summary>
		/// Once the descriptions and the Stats table (for stat Ids) are in, renders every mod's text
		/// on a worker and publishes the array. Safe to call repeatedly from any thread.
		/// </summary>
		private void TryStartModText() {
			if ( !HasModText || modText != null || descriptions.Failed ) return;
			if ( !descriptions.Ready || !refs.IsLoaded("Stats") ) return;
			lock ( textGate ) {
				if ( textStarted ) return;
				textStarted = true;
			}
			var t = new System.Threading.Thread(() => {
				var set = descriptions.Set;
				var result = new string[file.RowCount];
				var stats = new List<StatValue>(8);
				for ( int r = 0; r < result.Length; r++ ) {
					stats.Clear();
					int row = file.RowOffset(r);
					for ( int k = 0; k < 8; k++ ) {
						ulong target = file.ReadU64(row + statKeyCols[k].Offset);
						if ( target == 0xFEFEFEFEFEFEFEFEUL ) continue;
						string id = refs.Label("Stats", target) ?? $"Stats[{target}]";
						stats.Add(new StatValue(id, file.ReadInt(row + statMinCols[k].Offset, 4, true), file.ReadInt(row + statMaxCols[k].Offset, 4, true)));
					}
					string domain = domainCol == null ? null : DatSchemas.FormatCell(file, domainCol, r, file.RowSize);
					result[r] = set.Render(stats, domain);
				}
				modText = result;
			}) { IsBackground = true, Name = "AtE BundleBrowser mod text" };
			t.Start();
		}

		private string ModTextStatus {
			get {
				if ( modText != null ) return null;
				if ( descriptions.Failed ) return "stat descriptions could not be loaded";
				return "building mod text ...";
			}
		}

		public string Summary =>
			$"{file.RowCount:N0} rows x {file.RowSize} bytes" +
			(table != null ? $", schema \"{table.Name}\" ({table.Columns.Count} columns, {table.RowSize} bytes)" : ", no schema for this file");

		/// <summary>True when a saved schema laid this file out, i.e. there are named columns to export.</summary>
		public bool HasSchema => table != null;

		/// <summary>
		/// Writes every row and every schema column to a CSV (UTF-8 with a BOM so Excel reads the
		/// text correctly). Blocks, so call it off the render thread. Waits for the referenced
		/// tables to finish loading first, so references export as names rather than "Table[n]".
		/// Returns the number of rows written.
		/// </summary>
		public int ExportCsv(string outPath) {
			for ( int waited = 0; waited < 600; waited++ ) {
				TryStartModText();
				bool refsDone = refs.Pending == 0;
				bool textDone = !HasModText || modText != null || descriptions.Failed;
				if ( refsDone && textDone ) break;
				System.Threading.Thread.Sleep(100);
			}
			string[] text = modText;

			string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
			if ( !string.IsNullOrEmpty(dir) ) System.IO.Directory.CreateDirectory(dir);

			using ( var w = new System.IO.StreamWriter(outPath, false, new System.Text.UTF8Encoding(true)) ) {
				w.Write("#");
				if ( HasModText ) { w.Write(','); w.Write("ModText"); }
				foreach ( var c in columns ) { w.Write(','); w.Write(CsvEscape(c.Name)); }
				w.Write("\r\n");
				for ( int r = 0; r < file.RowCount; r++ ) {
					w.Write(r);
					if ( HasModText ) { w.Write(','); w.Write(CsvEscape(text?[r] ?? "")); }
					foreach ( var c in columns ) {
						w.Write(',');
						w.Write(CsvEscape(DatSchemas.FormatCell(file, c, r, file.RowSize, refs, export: true)));
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

		public unsafe void Draw() {
			ImGui.TextUnformatted(Summary);

			if ( table == null ) {
				ImGui.TextWrapped("This file has no saved schema, so its columns are unknown. " +
					"Add its table to DatSchemas.Text to browse it.");
				return;
			}
			if ( !SchemaMatches ) {
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.3f, 1f));
				ImGui.TextWrapped($"Row size mismatch: the schema expects {table.RowSize} bytes per row but this file has " +
					$"{file.RowSize}. The game version differs from the saved schema, so columns after the first change " +
					"may be misaligned" + (columns.Count < table.Columns.Count ? $" ({table.Columns.Count - columns.Count} trailing column(s) do not fit and are hidden)." : "."));
				ImGui.PopStyleColor();
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
				ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f),
					$"{descriptions.Set.Problems.Count} stat description file(s) failed to load: {descriptions.Set.Problems[0]}");
			}
			ImGui.SameLine();
			ImGui.Checkbox("Column layout##datlayout", ref showLayout);

			if ( showLayout ) {
				ImGui.BeginChild("datlayoutlist", new Vector2(0, 160), true);
				for ( int i = 0; i < table.Columns.Count; i++ ) {
					var c = table.Columns[i];
					ImGui.TextUnformatted($"+0x{c.Offset:X3}  {c.Size,2}b  {c.Name}: {c.TypeName}");
				}
				ImGui.EndChild();
			}

			var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY
				| ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
			// Column 0 is the row number; with mod text, column 1 is that, then the schema columns.
			int lead = HasModText ? 2 : 1;
			if ( !ImGui.BeginTable("dattable##" + Path, columns.Count + lead, flags, new Vector2(0, 0)) ) return;

			ImGui.TableSetupScrollFreeze(1, 1);
			ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 56f);
			if ( HasModText ) ImGui.TableSetupColumn(ModTextHeader, ImGuiTableColumnFlags.WidthFixed, 420f);
			foreach ( var c in columns )
				ImGui.TableSetupColumn(c.Name, ImGuiTableColumnFlags.WidthFixed, InitialWidth(c));
			ImGui.TableHeadersRow();

			var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
			clipper.Begin(rows.Length);
			while ( clipper.Step() ) {
				for ( int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++ ) {
					int r = rows[i];
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.TextUnformatted(r.ToString());
					if ( HasModText && ImGui.TableSetColumnIndex(1) ) {
						string[] text = modText;
						// Lines of a multi-line mod are shown on one row, separated by " / ".
						ImGui.TextUnformatted(text == null ? "..." : text[r].Replace("\n", " / "));
					}
					for ( int c = 0; c < columns.Count; c++ ) {
						// Returns false for columns scrolled out of view; skip formatting those.
						if ( !ImGui.TableSetColumnIndex(c + lead) ) continue;
						ImGui.TextUnformatted(DatSchemas.FormatCell(file, columns[c], r, file.RowSize, refs));
					}
				}
			}
			clipper.End();
			ImGuiNative.ImGuiListClipper_destroy(clipper.NativePtr);
			ImGui.EndTable();
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
