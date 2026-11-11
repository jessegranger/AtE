using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Testing mode for Offsets.cs: on a fresh character, plays through a script of OffsetChecks (see OffsetChecks) and keeps what
	/// passed, failed, was blocked and is queued for a fix. The results are listed on screen (not clickable) and by the MemoryServer
	/// (/offsets/report), which also queues failures for fixing (/offsets/queue) and confirms golden values (/offsets/confirm).
	/// </summary>
	public class OffsetTestSection : AutomationSection {
		public override string Name => "Offset Tests";

		/// <summary>Draw the list of checks on the screen while testing.</summary>
		public bool ShowList = true;

		private List<OffsetCheck> checks = new List<OffsetCheck>();
		private int index;
		private bool running;
		private bool finished;
		private string status = "off";
		private long startedAt;
		private long waitingSince;
		private long lastFocusTry;
		private string version;

		public bool IsRunning => running;
		public IReadOnlyList<OffsetCheck> Checks => checks;

		private static string QueueFile => Path.Combine(GoldenStore.Directory, "queue.json");

		public void Start() {
			var queued = LoadQueue();
			checks = OffsetChecks.All();
			foreach ( var c in checks ) {
				if ( queued.Contains(c.Id) ) { c.Status = CheckStatus.Queued; c.Detail = "queued for a fix"; }
			}
			index = 0;
			running = true;
			finished = false;
			startedAt = Time.ElapsedMilliseconds;
			waitingSince = startedAt;
			version = Offsets.PoEVersion;
			status = "running";
			Log($"OffsetTests: starting {checks.Count} checks (Offsets for PoE {version})");
		}

		public void Stop(string why = "stopped") {
			if ( running ) {
				var c = Current;
				if ( c != null && c.Status == CheckStatus.Running ) { SafeTeardown(c); c.Status = CheckStatus.Waiting; }
			}
			running = false;
			status = why;
		}

		private OffsetCheck Current => index >= 0 && index < checks.Count ? checks[index] : null;

		public override void Render() {
			ImGui.Checkbox("Show list on screen", ref ShowList);
			if ( !running ) {
				if ( ImGui.Button("Start testing") ) Start();
			} else if ( ImGui.Button("Stop") ) Stop();
			ImGui.SameLine();
			ImGui.Text(status);
			ImGui.Text(Summary());
			ImGui.Text($"Golden values: {GoldenStore.All.Count()} ({GoldenStore.All.Count(g => !g.Value.Confirmed)} unconfirmed)");
			if ( ImGui.Button("Copy report") ) ImGui.SetClipboardText(Report());
		}

		public string Summary() {
			int Count(CheckStatus s) => checks.Count(c => c.Status == s);
			return $"{Count(CheckStatus.Passed)} passed, {Count(CheckStatus.Failed)} failed, {Count(CheckStatus.Blocked)} blocked, {Count(CheckStatus.Queued)} queued for fix, {Count(CheckStatus.Inconclusive)} inconclusive, {Count(CheckStatus.Waiting) + Count(CheckStatus.Running)} to do";
		}

		public override IState OnTick(long dt) {
			if ( checks.Count == 0 ) return this;
			if ( ShowList && (running || finished) ) DrawList();
			if ( !running ) return this;
			if ( !PoEMemory.IsAttached ) { status = "waiting for the game"; return this; }

			// instant checks go through in one frame; stop at the first that has to wait
			for ( int steps = 0; steps < 60 && running; steps++ ) {
				var check = Current;
				if ( check == null ) {
					running = false;
					finished = true;
					status = "done: " + Summary();
					Log("OffsetTests: " + status);
					SaveResults();
					return this;
				}
				if ( !Step(check) ) break;
			}
			return this;
		}

		/// <summary>Work on the current check; true if it is finished (and the next is to be started).</summary>
		private bool Step(OffsetCheck check) {
			if ( check.Status != CheckStatus.Running ) {
				var unmet = check.DependsOn.Select(id => checks.FirstOrDefault(c => c.Id == id)).FirstOrDefault(d => d != null && d.Status != CheckStatus.Passed);
				if ( unmet != null ) {
					check.Status = CheckStatus.Blocked;
					check.Detail = $"blocked by {unmet.Id} ({unmet.Status})";
					index++;
					return true;
				}
				if ( check.NeedsPlayer && !(IsValid(GetPlayer()) && !(PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true)) ) {
					status = $"waiting for the player ({check.Id})";
					return false;
				}
				if ( !PoEMemory.TargetHasFocus && (check.Setup != null) ) {
					// a step that sends keys or the mouse would fail without the focus: take it
					if ( Time.ElapsedMilliseconds - lastFocusTry > 750 ) {
						lastFocusTry = Time.ElapsedMilliseconds;
						if ( !Platform.Current.Memory.TryFocusTarget() ) Log($"OffsetTests: could not focus the game for {check.Id}");
					}
					status = $"{check.Id}: focusing the game";
					return false;
				}
				check.Status = CheckStatus.Running;
				check.StartedAt = Time.ElapsedMilliseconds;
				status = check.Id;
			}
			if ( !check.SetupDone ) {
				check.SetupDone = true;
				try { check.Setup?.Invoke(); } catch ( Exception e ) { return Finish(check, CheckStatus.Failed, $"Setup threw {e.GetType().Name}: {e.Message}"); }
			}
			long elapsed = Time.ElapsedMilliseconds - check.StartedAt;
			if ( elapsed < check.SettleMs ) return false;

			Outcome outcome;
			var ctx = new TestContext(check, elapsed);
			try {
				outcome = check.Run(ctx);
			} catch ( Exception e ) {
				return Finish(check, CheckStatus.Failed, $"threw {e.GetType().Name}: {e.Message}");
			}
			switch ( outcome.Result ) {
				case Outcome.Kind.Pass: return Finish(check, CheckStatus.Passed, outcome.Detail);
				case Outcome.Kind.Fail: return Finish(check, CheckStatus.Failed, outcome.Detail);
				default:
					check.Detail = outcome.Detail;
					if ( elapsed > check.TimeoutMs + check.SettleMs ) {
						return Finish(check, check.OptionalTarget ? CheckStatus.Inconclusive : CheckStatus.Failed, $"timed out: {outcome.Detail}");
					}
					return false;
			}
		}

		private bool Finish(OffsetCheck check, CheckStatus result, string detail) {
			SafeTeardown(check);
			check.Status = result;
			check.Detail = detail ?? "";
			index++;
			return true;
		}

		private static void SafeTeardown(OffsetCheck check) {
			try { check.Teardown?.Invoke(); } catch ( Exception e ) { Log($"OffsetTests: Teardown of {check.Id} threw {e.Message}"); }
		}

		// ---------------------------------------------------------------- on screen

		private const uint listId = 0x0FF5E7;

		private static Color ColorOf(OffsetCheck c) {
			switch ( c.Status ) {
				case CheckStatus.Passed: return Color.LimeGreen;
				case CheckStatus.Failed: return Color.Red;
				case CheckStatus.Blocked: return Color.IndianRed;
				case CheckStatus.Queued: return Color.Orange;
				case CheckStatus.Running: return Color.Yellow;
				case CheckStatus.Inconclusive: return Color.Cyan;
				default: return Color.Gray;
			}
		}

		private static string TagOf(CheckStatus s) {
			switch ( s ) {
				case CheckStatus.Passed: return "PASS";
				case CheckStatus.Failed: return "FAIL";
				case CheckStatus.Blocked: return "BLOCKED";
				case CheckStatus.Queued: return "FIX";
				case CheckStatus.Running: return "...";
				case CheckStatus.Inconclusive: return "?";
				default: return "";
			}
		}

		// drawn as plain overlay text, so it takes no clicks
		private void DrawList() {
			var origin = new Vector2(8, 60);
			DrawTextAt(listId, origin, $"Offset tests (PoE {version}): {Summary()}", Color.White, 14f);
			foreach ( var c in checks ) {
				string detail = c.Status == CheckStatus.Passed || c.Status == CheckStatus.Waiting || string.IsNullOrEmpty(c.Detail) ? ""
					: " - " + (c.Detail.Length > 70 ? c.Detail.Substring(0, 70) + "..." : c.Detail);
				DrawTextAt(listId, origin, $"{TagOf(c.Status),-7} {c.Id}{detail}", ColorOf(c), 14f);
			}
		}

		// ---------------------------------------------------------------- results, queue

		public string Report() {
			var sb = new StringBuilder();
			sb.AppendLine($"# Offset tests, PoE {Offsets.PoEVersion} (Offsets {Offsets.VersionMajor}.{Offsets.VersionMinor})");
			sb.AppendLine(Summary());
			sb.AppendLine($"status: {status}; game focused: {PoEMemory.TargetHasFocus}");
			sb.AppendLine();
			foreach ( var group in checks.GroupBy(c => c.Group) ) {
				sb.AppendLine($"## {group.Key}");
				foreach ( var c in group ) {
					sb.AppendLine($"- {c.Status.ToString().ToUpperInvariant()} {c.Id}: {c.Detail}");
					if ( c.Status != CheckStatus.Passed && c.Covers.Length > 0 ) sb.AppendLine($"    covers: {string.Join(", ", c.Covers)}");
				}
			}
			return sb.ToString();
		}

		private void SaveResults() {
			try {
				Directory.CreateDirectory(GoldenStore.Directory);
				var rows = checks.Select(c => new { c.Id, Status = c.Status.ToString(), c.Detail, Version = Offsets.PoEVersion });
				File.WriteAllText(Path.Combine(GoldenStore.Directory, "results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
			} catch ( Exception e ) { Log($"OffsetTests: could not save results: {e.Message}"); }
		}

		private static HashSet<string> LoadQueue() {
			try {
				if ( File.Exists(QueueFile) ) return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QueueFile)) ?? new List<string>());
			} catch ( Exception e ) { Log($"OffsetTests: could not read the queue: {e.Message}"); }
			return new HashSet<string>();
		}

		private static void SaveQueue(IEnumerable<string> ids) {
			try {
				Directory.CreateDirectory(GoldenStore.Directory);
				File.WriteAllText(QueueFile, JsonSerializer.Serialize(ids.OrderBy(i => i).ToList()));
			} catch ( Exception e ) { Log($"OffsetTests: could not save the queue: {e.Message}"); }
		}

		/// <summary>
		/// Queue checks for fixing: "failed" (every failed or blocked one), "clear" (forget the queue), or ids separated by commas
		/// (a group like "A2" queues all of its checks). Returns what the queue is now.
		/// </summary>
		public string Queue(string what) {
			var queue = LoadQueue();
			what = (what ?? "").Trim();
			if ( what == "clear" ) {
				queue.Clear();
			} else if ( what == "failed" ) {
				foreach ( var c in checks.Where(c => c.Status == CheckStatus.Failed || c.Status == CheckStatus.Blocked) ) queue.Add(c.Id);
			} else if ( what.Length > 0 ) {
				foreach ( var id in what.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ) {
					var matches = checks.Where(c => c.Id == id || c.Group == id).ToList();
					if ( matches.Count == 0 ) return $"error: no check or group {id}\n";
					foreach ( var m in matches ) queue.Add(m.Id);
				}
			}
			SaveQueue(queue);
			foreach ( var c in checks ) {
				if ( queue.Contains(c.Id) && c.Status != CheckStatus.Passed ) { c.Status = CheckStatus.Queued; c.Detail = "queued for a fix: " + c.Detail; }
				else if ( !queue.Contains(c.Id) && c.Status == CheckStatus.Queued ) c.Status = CheckStatus.Waiting;
			}
			return $"queued for fix ({queue.Count}): {string.Join(", ", queue.OrderBy(i => i))}\n";
		}
	}
}
