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
		/// <summary>
		/// Hold the class routine (stop it, with testing carrying on) when a monster whose path contains this is seen dead: eg, Hillock,
		/// so the character stays by the exit, and the area transition is in range to be looked at. Empty for never.
		/// </summary>
		public string StopRoutineAfterDeathOf = "ZombieBossHillock";

		private List<OffsetCheck> checks = new List<OffsetCheck>();
		// observers (see OffsetObservers): they watch for monsters, items, NPCs... while the sequence and the character carry on
		private class ObservedRow { public OffsetCheck Check; public ObserverSpec Spec; public string Key; public long Since; }
		private readonly List<ObservedRow> observed = new List<ObservedRow>();
		private List<ObserverSpec> observers = new List<ObserverSpec>();
		private long lastObserved;
		private IEnumerable<OffsetCheck> AllRows => checks.Concat(observed.Select(o => o.Check));
		private int index;
		private bool running;
		private bool finished;
		private string status = "off";
		private long startedAt;
		private long waitingSince;
		private long lastFocusTry;
		// the combat routine that plays the character through the beach once the scripted checks are done, and the one it replaced
		private CombatDriver startedRoutine;
		private string previousRoutine;
		private readonly HashSet<uint> aliveWatched = new HashSet<uint>();
		private string version;

		public bool IsRunning => running;
		public IReadOnlyList<OffsetCheck> Checks => checks;

		private static string QueueFile => Path.Combine(GoldenStore.Directory, "queue.json");

		// a run of only some checks (see Start): no observers, and no routine at the end
		private bool sequenceOnly;

		/// <summary>
		/// Start testing. With only (comma separated ids, groups, or id prefixes: "A6", "A4.Life,A8"), just those checks run, with no
		/// observers and no routine afterwards: to try some checks again without playing the beach.
		/// </summary>
		public void Start(string only = null) {
			var queued = LoadQueue();
			checks = OffsetChecks.All();
			sequenceOnly = !string.IsNullOrWhiteSpace(only);
			if ( sequenceOnly ) {
				var parts = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				checks = checks.Where(c => parts.Any(p => c.Id == p || c.Group == p || c.Id.StartsWith(p, StringComparison.Ordinal))).ToList();
			}
			foreach ( var c in checks ) {
				if ( queued.Contains(c.Id) ) { c.Status = CheckStatus.Queued; c.Detail = "queued for a fix"; }
			}
			observers = sequenceOnly ? new List<ObserverSpec>() : OffsetObservers.All();
			aliveWatched.Clear();
			observed.Clear();
			foreach ( var spec in observers.Where(o => o.Key == null) ) AddRow(spec, null, queued);
			index = 0;
			running = true;
			finished = false;
			startedAt = Time.ElapsedMilliseconds;
			waitingSince = startedAt;
			version = Offsets.PoEVersion;
			status = "running";
			Log($"OffsetTests: starting {checks.Count} checks (Offsets for PoE {version})");
		}

		/// <summary>
		/// With the scripted checks done, play the beach with the character's own start routine (eg, WitchStart for a Witch), since the
		/// observers wait for what that routine meets: the right weapon on the ground, the Dying Exile's zombie, the chest, Hillock.
		/// (Not during the checks: both would be using the mouse.)
		/// </summary>
		private void StartClassRoutine() {
			if ( sequenceOnly ) return;
			var combat = GetPlugin<AutomationPlugin>()?.Combat;
			string cls = GetPlayer()?.GetComponent<Render>()?.Name;
			var routine = combat?.Routines.FirstOrDefault(r => r.Key == $"{cls}Start");
			if ( routine == null ) {
				Log($"OffsetTests: no {cls}Start combat routine, so the observers wait for the character to be played by hand");
				return;
			}
			previousRoutine = combat.Routine;
			combat.Routine = routine.Key;
			if ( !routine.IsActive ) routine.Start();
			startedRoutine = routine;
			Log($"OffsetTests: started the {routine.Key} combat routine");
		}

		/// <summary>Stop the class routine but keep testing (and remember it, to restore the routine choice at Stop).</summary>
		public string HoldRoutine(string why) {
			if ( startedRoutine == null ) return "there is no class routine\n";
			startedRoutine.Stop(why);
			return $"{startedRoutine.Key} stopped: {why}\n";
		}

		public string ResumeRoutine() {
			if ( startedRoutine == null ) return "there is no class routine\n";
			if ( !startedRoutine.IsActive ) startedRoutine.Start();
			return $"{startedRoutine.Key} started\n";
		}

		private void StopClassRoutine() {
			if ( startedRoutine == null ) return;
			startedRoutine.Stop("offset tests stopped");
			var combat = GetPlugin<AutomationPlugin>()?.Combat;
			if ( combat != null && previousRoutine != null ) combat.Routine = previousRoutine;
			startedRoutine = null;
		}

		public void Stop(string why = "stopped") {
			StopClassRoutine();
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
			int Seen(CheckStatus s) => observed.Count(o => o.Check.Status == s);
			string result = $"{Count(CheckStatus.Passed)} passed, {Count(CheckStatus.Failed)} failed, {Count(CheckStatus.Blocked)} blocked, {Count(CheckStatus.Queued)} queued for fix, {Count(CheckStatus.Inconclusive)} inconclusive, {Count(CheckStatus.Waiting) + Count(CheckStatus.Running)} to do";
			if ( observed.Count > 0 ) result += $"; observed: {Seen(CheckStatus.Passed)} passed, {Seen(CheckStatus.Failed)} failed, {Seen(CheckStatus.Waiting)} waiting";
			return result;
		}

		private ObservedRow AddRow(ObserverSpec spec, string key, HashSet<string> queued) {
			var check = new OffsetCheck { Id = key == null ? spec.Id : $"{spec.Id}:{key}", Covers = spec.Covers, NeedsPlayer = false };
			check.Detail = key == null ? spec.WaitingFor : "";
			if ( queued != null && queued.Contains(check.Id) ) { check.Status = CheckStatus.Queued; check.Detail = "queued for a fix"; }
			var row = new ObservedRow { Check = check, Spec = spec, Key = key, Since = Time.ElapsedMilliseconds };
			observed.Add(row);
			return row;
		}

		// every 100 ms: hand each observer the entities it watches for; a row is decided once, by the first run that passes or fails
		private void TickObservers() {
			if ( observers.Count == 0 || Time.ElapsedMilliseconds - lastObserved < 100 ) return;
			lastObserved = Time.ElapsedMilliseconds;
			if ( !IsValid(GetPlayer()) || (PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true) ) return;
			List<Entity> everything = null;
			var queued = LoadQueue();
			foreach ( var spec in observers ) {
				List<Entity> source;
				try {
					source = spec.Source != null ? spec.Source().ToList() : (everything ?? (everything = GetEntities().Where(e => IsValid(e)).ToList()));
				} catch ( Exception ) { continue; }
				var matched = source.Where(e => { try { return spec.Matches(e); } catch ( Exception ) { return false; } }).ToList();
				if ( spec == observers[0] ) HoldAfterBoss(everything);
				if ( matched.Count == 0 ) continue;
				if ( spec.Key == null ) {
					Observe(observed.First(o => o.Spec == spec && o.Key == null), matched);
				} else {
					foreach ( var group in matched.GroupBy(e => { try { return spec.Key(e); } catch ( Exception ) { return null; } }).Where(g => g.Key != null) ) {
						var row = observed.FirstOrDefault(o => o.Spec == spec && o.Key == group.Key) ?? AddRow(spec, group.Key, queued);
						Observe(row, group.ToList());
					}
				}
			}
		}

		// the routine goes on to leave the area at once: after the boss is dead it is held, to look at what stands by the exit
		private void HoldAfterBoss(List<Entity> everything) {
			if ( startedRoutine == null || !startedRoutine.IsActive || string.IsNullOrEmpty(StopRoutineAfterDeathOf) || everything == null ) return;
			foreach ( var e in everything.Where(e => e.Path?.Contains(StopRoutineAfterDeathOf) ?? false) ) {
				var life = e.GetComponent<Life>();
				if ( !IsValid(life) ) continue;
				if ( life.CurHP > 0 ) aliveWatched.Add(e.Id);
				else if ( aliveWatched.Contains(e.Id) ) {
					startedRoutine.Stop($"held: {StopRoutineAfterDeathOf} died");
					status = $"routine held after {StopRoutineAfterDeathOf} died (/offsets/routine?cmd=start resumes it)";
					Log("OffsetTests: " + status);
					return;
				}
			}
		}

		private void Observe(ObservedRow row, List<Entity> entities) {
			var check = row.Check;
			if ( check.Status == CheckStatus.Passed || check.Status == CheckStatus.Failed ) return;
			try {
				var outcome = row.Spec.Run(new TestContext(check, Time.ElapsedMilliseconds - row.Since), entities);
				switch ( outcome.Result ) {
					case Outcome.Kind.Pass: check.Status = CheckStatus.Passed; check.Detail = outcome.Detail; break;
					case Outcome.Kind.Fail: check.Status = CheckStatus.Failed; check.Detail = outcome.Detail; break;
					default: check.Detail = outcome.Detail; break;
				}
			} catch ( Exception e ) {
				check.Status = CheckStatus.Failed;
				check.Detail = $"threw {e.GetType().Name}: {e.Message}";
			}
		}

		public override IState OnTick(long dt) {
			if ( checks.Count == 0 ) return this;
			if ( ShowList && (running || finished) ) DrawList();
			if ( (running || finished) && PoEMemory.IsAttached ) TickObservers();
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
					StartClassRoutine();
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
			foreach ( var c in AllRows ) {
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
			sb.AppendLine($"status: {status}; game focused: {PoEMemory.TargetHasFocus}; routine: {(startedRoutine != null ? $"{startedRoutine.Key} {startedRoutine.Status}" : "none")}");
			sb.AppendLine();
			foreach ( var group in AllRows.GroupBy(c => c.Group) ) {
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
				var rows = AllRows.Select(c => new { c.Id, Status = c.Status.ToString(), c.Detail, Version = Offsets.PoEVersion });
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
				foreach ( var c in AllRows.Where(c => c.Status == CheckStatus.Failed || c.Status == CheckStatus.Blocked) ) queue.Add(c.Id);
			} else if ( what.Length > 0 ) {
				foreach ( var id in what.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ) {
					var matches = AllRows.Where(c => c.Id == id || c.Group == id).ToList();
					if ( matches.Count == 0 ) return $"error: no check or group {id}\n";
					foreach ( var m in matches ) queue.Add(m.Id);
				}
			}
			SaveQueue(queue);
			foreach ( var c in AllRows ) {
				if ( queue.Contains(c.Id) && c.Status != CheckStatus.Passed ) { c.Status = CheckStatus.Queued; c.Detail = "queued for a fix: " + c.Detail; }
				else if ( !queue.Contains(c.Id) && c.Status == CheckStatus.Queued ) c.Status = CheckStatus.Waiting;
			}
			return $"queued for fix ({queue.Count}): {string.Join(", ", queue.OrderBy(i => i))}\n";
		}
	}
}
