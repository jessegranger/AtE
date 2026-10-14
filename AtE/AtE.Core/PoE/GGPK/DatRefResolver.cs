using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AtE {

	/// <summary>
	/// Turns foreign-row references into readable labels. For each referenced table it loads the
	/// .datc64 from the bundle index (on a worker thread — that is an Oodle decode) and keeps one
	/// label per row, taken from the table's leading string column. In dat-schema nearly every
	/// table leads with its <c>Id</c> (or <c>Name</c>), so that column is used; tables that do not
	/// lead with a string (GrantedEffectsPerLevel, say) are detected and simply stay unlabeled.
	///
	/// Safe to query from the render thread while workers fill it in: <see cref="Label"/> returns
	/// null until a table is ready, and callers fall back to "Table[row]".
	/// </summary>
	public sealed class DatRefResolver {

		private const int ValidationRows = 50;

		// Value null = loaded or attempted, but this table has no usable labels.
		private readonly ConcurrentDictionary<string, string[]> labels = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		private int pending;
		private int failed;

		/// <summary>How many referenced tables are still loading.</summary>
		public int Pending => Volatile.Read(ref pending);
		public int Failed => Volatile.Read(ref failed);

		/// <summary>Adds a table that is already in memory, such as the one being viewed (for self-references).</summary>
		public void Seed(string table, DatFile file) => labels[table] = BuildLabels(file);

		/// <summary>True once a table has been loaded (or has failed), so <see cref="Label"/> can be trusted for it.</summary>
		public bool IsLoaded(string table) => labels.ContainsKey(table);

		/// <summary>The label for a row, or null when the table is not loaded, has no labels, or the row is out of range.</summary>
		public string Label(string table, ulong row) {
			if ( !labels.TryGetValue(table, out string[] l) || l == null || row >= (ulong)l.Length ) return null;
			string s = l[row];
			return string.IsNullOrEmpty(s) ? null : s;
		}

		/// <summary>
		/// Starts loading the given tables, from <paramref name="folder"/> first (so a localized
		/// table's references use the same language) and the plain data folder as a fallback.
		/// </summary>
		public void Request(BundleIndex index, string folder, IEnumerable<string> tables) {
			var todo = new List<string>();
			foreach ( string t in tables )
				if ( !labels.ContainsKey(t) && !todo.Contains(t, StringComparer.OrdinalIgnoreCase) ) todo.Add(t);
			if ( todo.Count == 0 ) return;

			Interlocked.Add(ref pending, todo.Count);
			var worker = new Thread(() => {
				foreach ( string table in todo ) {
					try {
						labels[table] = Load(index, folder, table);
					} catch ( Exception ) {
						// Includes the index being unloaded mid-read. Leave the table unlabeled.
						labels[table] = null;
						Interlocked.Increment(ref failed);
					} finally {
						Interlocked.Decrement(ref pending);
					}
				}
			}) {
				IsBackground = true,
				Name = "AtE BundleBrowser dat refs",
			};
			worker.Start();
		}

		private static string[] Load(BundleIndex index, string folder, string table) {
			string file = table.ToLowerInvariant() + ".datc64";
			foreach ( string dir in new[] { folder, "data" } ) {
				string path = string.IsNullOrEmpty(dir) ? file : dir + "/" + file;
				if ( index.TryReadAllBytes(path, out byte[] bytes) && DatFile.TryParse(bytes, out DatFile dat, out _) )
					return BuildLabels(dat);
			}
			return null;
		}

		/// <summary>One label per row from the leading string column, or null if the table does not start with one.</summary>
		public static string[] BuildLabels(DatFile file) {
			if ( file.RowSize < 8 || file.RowCount == 0 ) return null;

			// A string cell is an offset into the data section; real strings start after the 8-byte
			// marker. If the first rows do not all look like that, column 0 is not a string.
			ulong varSize = (ulong)(file.Bytes.Length - file.VarStart);
			int check = Math.Min(file.RowCount, ValidationRows);
			for ( int r = 0; r < check; r++ ) {
				ulong off = file.ReadU64(file.RowOffset(r));
				if ( off < DatFile.MarkerLength || off >= varSize || (off & 1) != 0 ) return null;
			}

			var result = new string[file.RowCount];
			for ( int r = 0; r < result.Length; r++ ) {
				ulong off = file.ReadU64(file.RowOffset(r));
				result[r] = off >= DatFile.MarkerLength && off < varSize ? file.ReadStringOffset(off) : null;
			}
			return result;
		}
	}
}
