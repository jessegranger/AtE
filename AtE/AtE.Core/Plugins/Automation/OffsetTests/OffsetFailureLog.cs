using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// What the Offsets tests leave behind for fixing: progress.log (each step is written before it runs, so after a lock-up the last
	/// line says what hung) and failure.log (written at the first failure: the check, what it said, the declared offsets it covers, and
	/// hex dumps of the live memory they point into, to find where an offset moved to).
	/// </summary>
	public static class OffsetFailureLog {
		private static string Dir => GoldenStore.Directory;
		public static string ProgressFile => Path.Combine(Dir, "progress.log");
		public static string FailureFile => Path.Combine(Dir, "failure.log");

		private static readonly HashSet<string> logged = new HashSet<string>();

		public static void Reset() {
			logged.Clear();
			try { Directory.CreateDirectory(Dir); File.WriteAllText(ProgressFile, ""); } catch ( Exception ) { }
			watchdog?.Dispose();
			watchdog = new Timer(_ => Watch(), null, 1000, 1000);
		}

		// what the main thread is doing now: if it stays on one thing for seconds, the game's thread is stuck in it (a loop over garbage),
		// and the watchdog says so in progress.log (the overlay then has to be killed; this line is what is left to find the cause)
		private static volatile string mark;
		private static long markSince;
		private static long reported;
		private static Timer watchdog;

		/// <summary>Say what is about to run (null when it is done), so a hang can be pinned on it.</summary>
		public static void Mark(string what) {
			mark = what;
			markSince = Environment.TickCount64;
		}

		private static void Watch() {
			string m = mark;
			if ( m == null ) return;
			long stuck = Environment.TickCount64 - markSince;
			if ( stuck > 3000 && markSince != reported ) {
				reported = markSince;
				Progress($"STUCK for {stuck / 1000} s in: {m}");
			}
		}

		/// <summary>Write a line now (flushed), once per key if given: before something that may hang.</summary>
		public static void Progress(string line, string onceKey = null) {
			if ( onceKey != null && !logged.Add(onceKey) ) return;
			try { File.AppendAllText(ProgressFile, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch ( Exception ) { }
		}

		/// <summary>Write failure.log for a failed check; returns its path.</summary>
		public static string WriteFailure(OffsetCheck check, IEnumerable<OffsetCheck> others) {
			var sb = new StringBuilder();
			try {
				sb.AppendLine($"FAILED {check.Id} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, Offsets for PoE {Offsets.PoEVersion}");
				sb.AppendLine($"what it said: {check.Detail}");
				sb.AppendLine($"it depends on: {string.Join(", ", check.DependsOn)}");
				var passed = others.Where(c => c.Status == CheckStatus.Passed).Select(c => c.Id).ToList();
				sb.AppendLine($"passed before it ({passed.Count}): {string.Join(", ", passed)}");
				sb.AppendLine();
				sb.AppendLine("Hunt for a shift first: the failing field has probably moved by a small multiple of 8 within its struct (and the fields after it too).");
				sb.AppendLine("Dump the struct from its owner below (or /read?at=...), find the value the check expected, and compare its offset with the declared one.");
				sb.AppendLine();

				// the offsets the check declares, then the whole table of each struct they belong to
				var structs = new List<string>();
				sb.AppendLine("== declared offsets this check covers");
				foreach ( var cover in check.Covers ) {
					int dot = cover.IndexOf('.');
					if ( dot < 0 ) continue;
					string s = cover.Substring(0, dot), f = cover.Substring(dot + 1);
					if ( s == "Offsets" ) {
						var field = typeof(Offsets).GetField(f, BindingFlags.Public | BindingFlags.Static);
						sb.AppendLine($"  {cover} = {field?.GetValue(null) ?? "?"}");
						continue;
					}
					if ( !structs.Contains(s) ) structs.Add(s);
					var type = typeof(Offsets).GetNestedType(s, BindingFlags.Public);
					var fi = f == "*" ? null : type?.GetField(f, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					var at = fi?.GetCustomAttribute<FieldOffsetAttribute>();
					sb.AppendLine(f == "*" ? $"  {cover} (the whole struct)" : $"  {cover}  +0x{at?.Value ?? -1:X}  {fi?.FieldType.Name}");
				}
				foreach ( var s in structs ) {
					var type = typeof(Offsets).GetNestedType(s, BindingFlags.Public);
					if ( type == null ) continue;
					sb.AppendLine();
					sb.AppendLine($"== {s} (all fields, by offset)");
					foreach ( var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
								.Select(f => (f, at: f.GetCustomAttribute<FieldOffsetAttribute>())).Where(p => p.at != null).OrderBy(p => p.at.Value).ThenBy(p => p.f.Name) )
						sb.AppendLine($"  +0x{f.at.Value:X3}  {f.f.Name}  ({f.f.FieldType.Name})");
				}

				// live memory around where those structs live
				sb.AppendLine();
				sb.AppendLine("== live memory");
				var game = PoEMemory.GameRoot;
				if ( game != null ) Dump(sb, "GameRoot", game.Address, 0x120);
				var ingame = game?.InGameState;
				if ( ingame != null ) Dump(sb, "InGameState", ingame.Address, 0x700);
				var player = GetPlayer();
				if ( player != null ) {
					Dump(sb, "player Entity", player.Address, 0x100);
					var comps = player.GetComponents();
					foreach ( var s in structs.Where(s => s.StartsWith("Component_")) ) {
						string name = s.Substring("Component_".Length);
						if ( comps != null && comps.TryGetValue(name, out var addr) ) Dump(sb, $"player component {name}", addr, 0x300);
					}
				}
				if ( structs.Any(s => s.StartsWith("Element") || s.StartsWith("InGameState_UIElements")) && ingame?.UIRoot != null )
					Dump(sb, "UIRoot Element", ingame.UIRoot.Address, 0x300);
			} catch ( Exception e ) {
				sb.AppendLine($"(writing this log threw {e.GetType().Name}: {e.Message})");
			}
			try {
				Directory.CreateDirectory(Dir);
				File.WriteAllText(FailureFile, sb.ToString());
			} catch ( Exception ) { }
			return FailureFile;
		}

		// rows of 8 bytes: offset, bytes, as two int32 and two floats, and whether the qword looks like a pointer to readable memory; all-zero rows are left out
		private static void Dump(StringBuilder sb, string what, IntPtr address, int length) {
			sb.AppendLine($"-- {what} at 0x{address.ToInt64():X}, 0x{length:X} bytes (all-zero rows left out)");
			var buf = new byte[length];
			if ( address == IntPtr.Zero || PoEMemory.TryRead(address, buf) <= 0 ) { sb.AppendLine("   (unreadable)"); return; }
			for ( int o = 0; o + 8 <= length; o += 8 ) {
				long q = BitConverter.ToInt64(buf, o);
				if ( q == 0 ) continue;
				string hex = BitConverter.ToString(buf, o, 8).Replace('-', ' ');
				int a = BitConverter.ToInt32(buf, o), b = BitConverter.ToInt32(buf, o + 4);
				float fa = BitConverter.ToSingle(buf, o), fb = BitConverter.ToSingle(buf, o + 4);
				string f(float x) => float.IsFinite(x) && (Math.Abs(x) > 1e-4 && Math.Abs(x) < 1e7) ? x.ToString("0.###") : "-";
				string ptr = q > 0x10000 && q < 0x7FFFFFFFFFFF && PoEMemory.TryRead(new IntPtr(q), out long _) ? " ptr" : "";
				sb.AppendLine($"   +0x{o:X3} {hex} | {a,11} {b,11} | {f(fa),9} {f(fb),9}{ptr}");
			}
			sb.AppendLine();
		}
	}
}
