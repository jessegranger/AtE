using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using static AtE.Globals;

namespace AtE {

	public enum CheckStatus {
		/// <summary>not reached yet</summary>
		Waiting,
		/// <summary>was queued for a fix by the last run, and has not been run again since</summary>
		Queued,
		Running,
		Passed,
		Failed,
		/// <summary>not run: a check it depends on did not pass</summary>
		Blocked,
		/// <summary>not run to a result: what it needs was never there (eg, no monster came by)</summary>
		Inconclusive,
	}

	/// <summary>What one poll of a check's Run found.</summary>
	public readonly struct Outcome {
		public enum Kind { Pass, Fail, NotYet }
		public readonly Kind Result;
		public readonly string Detail;
		private Outcome(Kind result, string detail) { Result = result; Detail = detail; }
		public static Outcome Pass(string detail = "") => new Outcome(Kind.Pass, detail);
		public static Outcome Fail(string detail) => new Outcome(Kind.Fail, detail);
		/// <summary>Not decided: poll again next frame (until the check's TimeoutMs).</summary>
		public static Outcome NotYet(string detail = "") => new Outcome(Kind.NotYet, detail);
	}

	/// <summary>
	/// One test of some Offsets, run by the OffsetTestSection: Setup (eg, press a key), wait SettleMs, then poll Run each frame
	/// until it passes or fails (or TimeoutMs passes), then Teardown (eg, press the key again).
	/// </summary>
	public class OffsetCheck {
		/// <summary>Unique and stable, eg "A2.Terrain" (the group, then what it checks).</summary>
		public string Id;
		/// <summary>Which Offsets members it exercises, as "Struct.Field" (or "Struct.*"): the coverage the tests are held to.</summary>
		public string[] Covers = Array.Empty<string>();
		/// <summary>The Ids of checks that must pass first (else this one is Blocked: its offsets can't be tested until those are right).</summary>
		public string[] DependsOn = Array.Empty<string>();
		/// <summary>Wait for a player (and its area) before starting? False for the checks of the player itself.</summary>
		public bool NeedsPlayer = true;
		public int SettleMs = 0;
		public int TimeoutMs = 3000;
		/// <summary>On timing out, be Inconclusive (what it needs never came) instead of Failed.</summary>
		public bool OptionalTarget = false;
		public Action Setup;
		public Action Teardown;
		public Func<TestContext, Outcome> Run;

		public string Group => Id.Contains('.') ? Id.Substring(0, Id.IndexOf('.')) : Id;

		// what the runner keeps
		public CheckStatus Status = CheckStatus.Waiting;
		public string Detail = "";
		internal long StartedAt;
		internal bool SetupDone;
		internal readonly Dictionary<string, object> Scratch = new Dictionary<string, object>();
	}

	/// <summary>What a check's Run works with: the game, and assertions that collect what is wrong.</summary>
	public class TestContext {
		public readonly OffsetCheck Check;
		public readonly long Elapsed;
		/// <summary>For an observer row made for a key (eg, one skill of several): the key.</summary>
		public string Key;
		private readonly List<string> failures = new List<string>();
		private readonly List<string> notes = new List<string>();

		public TestContext(OffsetCheck check, long elapsed) { Check = check; Elapsed = elapsed; }

		public PlayerEntity Player => GetPlayer();
		public InGameState Game => PoEMemory.GameRoot?.InGameState;
		/// <summary>Values that live across the polls of one check.</summary>
		public Dictionary<string, object> Scratch => Check.Scratch;

		/// <summary>The character class (eg, "Witch": the player's Render name; its path says only the attribute, "Int"), for keeping goldens apart.</summary>
		public string Class => Player?.GetComponent<Render>()?.Name is string name && name.Length > 0 ? name : "Unknown";

		public void That(bool condition, string failure) { if ( !condition ) failures.Add(failure); }
		public void Note(string note) => notes.Add(note);

		public void Eq<T>(string what, T observed, T expected) {
			if ( !EqualityComparer<T>.Default.Equals(observed, expected) ) failures.Add($"{what} is {observed}, expected {expected}");
			else notes.Add($"{what}={observed}");
		}

		public void Near(string what, double observed, double expected, double tolerance) {
			if ( double.IsNaN(observed) || Math.Abs(observed - expected) > tolerance ) failures.Add($"{what} is {observed}, expected {expected} +-{tolerance}");
			else notes.Add($"{what}={observed:0.###}");
		}

		public void InRange(string what, double observed, double min, double max) {
			if ( double.IsNaN(observed) || observed < min || observed > max ) failures.Add($"{what} is {observed}, expected {min}..{max}");
			else notes.Add($"{what}={observed:0.###}");
		}

		/// <summary>Compare with the recorded golden value (for this class): see GoldenStore.</summary>
		public void Golden(string key, object observed) {
			string note = GoldenStore.Compare($"{Class}.{key}", observed?.ToString() ?? "null", out bool ok);
			if ( ok ) notes.Add(note); else failures.Add(note);
		}

		/// <summary>Like Golden, for things that do not depend on the character class (a monster's life, an NPC's name): the key is used as given.</summary>
		public void GoldenAny(string key, object observed) {
			string note = GoldenStore.Compare(key, observed?.ToString() ?? "null", out bool ok);
			if ( ok ) notes.Add(note); else failures.Add(note);
		}

		public Outcome Result() => failures.Count > 0
			? Outcome.Fail(string.Join("; ", failures))
			: Outcome.Pass(string.Join(", ", notes));
	}

	/// <summary>
	/// The values a fresh character is known to have (starting life, flasks, sizes of fixed UI elements...), kept in
	/// offsettests/golden.json beside the exe. A value seen for the first time is recorded unconfirmed (the check passes, and says
	/// so); once the user confirms it, later runs fail if it differs.
	/// </summary>
	public static class GoldenStore {
		public class Entry {
			public string Value { get; set; }
			public bool Confirmed { get; set; }
			public string Version { get; set; }
		}

		/// <summary>Where the run's own files go (results, the fix queue): next to the exe, not in the repo.</summary>
		public static string Directory => Path.Combine(AppContext.BaseDirectory, "offsettests");

		/// <summary>
		/// The goldens are kept in the repo (beside these checks), found by walking up from the exe to AtE.sln, so they are committed
		/// with the code; without a repo around the exe, next to it with the rest.
		/// </summary>
		private static string FileName {
			get {
				for ( var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent ) {
					if ( File.Exists(Path.Combine(dir.FullName, "AtE.sln")) ) return Path.Combine(dir.FullName, "AtE.Core", "Plugins", "Automation", "OffsetTests", "golden.json");
				}
				return Path.Combine(Directory, "golden.json");
			}
		}
		private static SortedDictionary<string, Entry> entries;

		private static SortedDictionary<string, Entry> Entries {
			get {
				if ( entries != null ) return entries;
				entries = new SortedDictionary<string, Entry>();
				try {
					if ( File.Exists(FileName) ) {
						var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FileName));
						if ( loaded != null ) foreach ( var pair in loaded ) entries[pair.Key] = pair.Value;
					}
				} catch ( Exception e ) { Log($"GoldenStore: could not read {FileName}: {e.Message}"); }
				return entries;
			}
		}

		private static void Save() {
			try {
				System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FileName));
				File.WriteAllText(FileName, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
			} catch ( Exception e ) { Log($"GoldenStore: could not write {FileName}: {e.Message}"); }
		}

		public static string Compare(string key, string observed, out bool ok) {
			if ( Entries.TryGetValue(key, out Entry entry) && entry.Confirmed ) {
				ok = entry.Value == observed;
				return ok ? $"{key}={observed}" : $"{key} is {observed}, golden {entry.Value}";
			}
			Entries[key] = new Entry { Value = observed, Confirmed = false, Version = Offsets.PoEVersion };
			Save();
			ok = true;
			return $"{key}={observed} (golden unconfirmed)";
		}

		public static IEnumerable<KeyValuePair<string, Entry>> All => Entries;

		/// <summary>Confirm the goldens whose key contains the text (all of them for ""); returns how many.</summary>
		public static int Confirm(string keyContains) {
			int n = 0;
			foreach ( var pair in Entries.Where(p => !p.Value.Confirmed && p.Key.Contains(keyContains ?? "")) ) { pair.Value.Confirmed = true; n++; }
			if ( n > 0 ) Save();
			return n;
		}

		/// <summary>Forget the goldens whose key contains the text, so they are recorded afresh.</summary>
		public static int Forget(string keyContains) {
			var keys = Entries.Keys.Where(k => k.Contains(keyContains)).ToList();
			foreach ( var k in keys ) Entries.Remove(k);
			if ( keys.Count > 0 ) Save();
			return keys.Count;
		}
	}
}
