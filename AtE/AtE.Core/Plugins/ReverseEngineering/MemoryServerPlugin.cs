using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A read-only, loopback-only HTTP server for reverse engineering the target's memory layout.
	/// It exists so a tool (eg Claude Code, via curl) can inspect live memory:
	///
	///   /status                       attach state, module base, key addresses
	///   /ents?filter=&amp;comp=&amp;id=       list entities / their component addresses
	///   /read?at=EXPR&amp;len=N&amp;depth=N    annotated hexdump (pointers, vtables, strings, floats)
	///   /track?at=EXPR&amp;len=N&amp;ms=&amp;dur= sample a region over time, classify slots that change
	///   /record?at=EXPR&amp;len=N&amp;ms=&amp;dur= sample over time into a .jsonl file for offline analysis
	///
	/// Address expressions (EXPR) are a root followed by steps: ingame+0x928, ent(1234), comp(1234,DiesAfterTime)+0x20*
	/// Roots: 0xHEX, base, gameroot, ingame, player, ent(ID), comp(ID,Name).  Steps: +N, -N, * (dereference a pointer).
	/// In a URL, write + as %2B (a bare + decodes as a space, which is also accepted).
	///
	/// Entity/component lookups touch managed caches that belong to the main thread, so those run from OnTick.
	/// Raw reads and sampling happen on the request thread.
	/// </summary>
	public class MemoryServerPlugin : PluginBase {

		public override string HelpText => "Loopback-only HTTP server (read-only) for inspecting game memory while reverse engineering offsets. See the plugin source for endpoints.";
		public override int SortIndex => 998;

		public int Port = 7788;

		private HttpListener listener;
		private int listeningPort;
		private readonly ConcurrentQueue<Job> jobs = new ConcurrentQueue<Job>();
		private long requestCount = 0;
		private string lastError;

		public MemoryServerPlugin() : base() {
			Enabled = false; // opt-in; a persisted INI value still overrides this
		}

		public override void Render() {
			base.Render();
			ImGui.InputInt("Port", ref Port);
			ImGui.Text(listener != null ? $"Listening on http://127.0.0.1:{listeningPort}/  ({requestCount} requests)" : "Not listening.");
			if ( lastError != null ) {
				ImGui.Text($"Last error: {lastError}");
			}
		}

		public override IState OnTick(long dt) {
			if ( Enabled && listener == null ) {
				StartListener();
			} else if ( (!Enabled || listeningPort != Port) && listener != null ) {
				StopListener();
			}
			// run everything queued by the request threads on this (main) thread
			while ( jobs.TryDequeue(out Job job) ) {
				try {
					job.Result = job.Work();
				} catch ( Exception e ) {
					job.Result = $"error: {e.Message}\n";
				}
				job.Done.Set();
			}
			return this;
		}

		private class Job {
			public Func<string> Work;
			public string Result;
			public ManualResetEventSlim Done = new ManualResetEventSlim(false);
		}

		private string RunOnMain(Func<string> work) {
			var job = new Job() { Work = work };
			jobs.Enqueue(job);
			return job.Done.Wait(5000) ? job.Result : "error: timed out waiting for the main thread\n";
		}

		private void StartListener() {
			try {
				listener = new HttpListener();
				listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
				listener.Start();
				listeningPort = Port;
				lastError = null;
				Log($"MemoryServer: listening on 127.0.0.1:{Port}");
				var l = listener;
				Task.Run(() => AcceptLoop(l));
			} catch ( Exception e ) {
				lastError = e.Message;
				Log($"MemoryServer: failed to start: {e.Message}");
				listener = null;
				Enabled = false;
			}
		}

		private void StopListener() {
			try {
				listener?.Close();
			} catch { }
			listener = null;
			Log("MemoryServer: stopped");
		}

		private async Task AcceptLoop(HttpListener l) {
			while ( l.IsListening ) {
				HttpListenerContext ctx;
				try {
					ctx = await l.GetContextAsync();
				} catch {
					break;
				}
				_ = Task.Run(() => Handle(ctx));
			}
		}

		private void Handle(HttpListenerContext ctx) {
			string body;
			int status = 200;
			try {
				Interlocked.Increment(ref requestCount);
				// a browser page must not be able to drive this (DNS rebinding / cross-origin fetch)
				string host = ctx.Request.Headers["Host"] ?? "";
				if ( ctx.Request.Headers["Origin"] != null || !(host.StartsWith("127.0.0.1") || host.StartsWith("localhost")) ) {
					status = 403;
					body = "forbidden\n";
				} else {
					body = Dispatch(ctx.Request.Url.AbsolutePath, ctx.Request.QueryString);
				}
			} catch ( Exception e ) {
				status = 500;
				body = $"error: {e.Message}\n";
			}
			try {
				byte[] bytes = Encoding.UTF8.GetBytes(body);
				ctx.Response.StatusCode = status;
				ctx.Response.ContentType = "text/plain; charset=utf-8";
				ctx.Response.ContentLength64 = bytes.Length;
				ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
			} catch { } finally {
				try { ctx.Response.Close(); } catch { }
			}
		}

		private string Dispatch(string path, System.Collections.Specialized.NameValueCollection q) {
			if ( !PoEMemory.IsAttached && path != "/help" ) {
				return "error: not attached to the game\n";
			}
			switch ( path ) {
				case "/":
				case "/help":
					return Help;
				case "/status":
					return RunOnMain(Status);
				case "/ents":
					return RunOnMain(() => Ents(q["filter"], q["comp"], q["id"], ParseInt(q["limit"], 30)));
				case "/info":
					return RunOnMain(() => Info(ParseInt(q["id"], 0), q["skill"]));
				case "/dump":
					return RunOnMain(() => Dump(q["filter"] ?? "AnimatedWeapon", ParseInt(q["depth"], 1), ParseInt(q["len"], 0x400), ParseInt(q["limit"], 40)));
				case "/hunt":
					return RunOnMain(() => Hunt(q["filter"] ?? "AnimatedWeapon", double.Parse(q["dur"] ?? "85.6", CultureInfo.InvariantCulture),
						double.Parse(q["rate"] ?? "30", CultureInfo.InvariantCulture), double.Parse(q["tol"] ?? "45", CultureInfo.InvariantCulture),
						ParseInt(q["depth"], 1), ParseInt(q["len"], 0x400), q["player"] == "1"));
				case "/gsnap":
					return RunOnMain(() => GraphSnap(ParseInt(q["id"], 0), q["name"] ?? "a", ParseInt(q["depth"], 1), ParseInt(q["len"], 0x400)));
				case "/gdiff":
					return RunOnMain(() => GraphDiff(q["name"] ?? "a", q["name2"], q["top"] == "1", q["match"]));
				case "/scan":
					return RunOnMain(() => Scan(ParseInt(q["id"], 0), q["value"], q["tol"], q["kind"], ParseInt(q["depth"], 1), ParseInt(q["len"], 0x400)));
				case "/read": {
					string at = q["at"];
					string err = null;
					long addr = 0;
					string pre = RunOnMain(() => TryResolve(at, out addr, out err) ? "" : $"error: {err}\n");
					if ( pre.Length > 0 ) return pre;
					return Dump(addr, ParseInt(q["len"], 0x80), ParseInt(q["depth"], 0), $"{at} = 0x{addr:X}");
				}
				case "/track":
				case "/record":
					return SampleCommand(path == "/record", q);
				default:
					return "error: unknown endpoint, try /help\n";
			}
		}

		private const string Help =
@"MemoryServer (read-only, loopback only)
  /status
  /ents?filter=<path substring>&comp=<ComponentName>&id=<entity id>&limit=30
  /info?id=<entity id>&skill=<owner skill name>   ground truth: buffs (timer/max), animation time, duration-ish stats, owner skill MinionDuration
  /scan?id=<entity id>&value=1.5,3000&tol=0.01&kind=f32|i32|both&depth=1&len=0x400   find values in all components (and the animated object's) + pointees
  /gsnap?id=<entity id>&name=a&depth=1&len=0x400    snapshot all components + pointees (+ animated object) under a label
  /gdiff?name=a[&name2=b][&top=1][&match=Comp]   re-read (or compare to snapshot b) and list every 4-byte slot that changed, as i32 and f32
  /read?at=EXPR&len=0x80&depth=0        annotated hexdump; depth>0 also dumps what valid pointers point at
  /track?at=EXPR&len=0x40&ms=100&dur=5000    sample, then report only the slots that change (i32/i64/f32/f64)
  /record?at=EXPR[&at=EXPR2]&len=0x40[&len=0x100]&ms=100&dur=5000&name=label    writes memdumps/<name>-<time>.jsonl
EXPR = root then steps. roots: 0xHEX base gameroot ingame player ent(ID) comp(ID,Name). steps: +N -N *
  e.g. ingame+0x920   comp(1234,DiesAfterTime)+0x20*   (use %2B for + in URLs, or curl -G --data-urlencode)\n";

		// ---------------------------------------------------------------- status / entities

		private string Status() {
			var sb = new StringBuilder();
			var gr = PoEMemory.GameRoot;
			var igs = gr?.InGameState;
			sb.AppendLine($"attached pid={PoEMemory.Memory.Pid} base=0x{PoEMemory.Memory.BaseAddress.ToInt64():X} size=0x{PoEMemory.Memory.ModuleSize:X}");
			sb.AppendLine($"gameroot=0x{gr?.Address.ToInt64() ?? 0:X}");
			sb.AppendLine($"ingame=0x{igs?.Address.ToInt64() ?? 0:X} valid={IsValid(igs)}");
			sb.AppendLine($"player=0x{(IsValid(igs) ? igs.Player.Address.ToInt64() : 0):X}");
			sb.AppendLine($"entities={EntityCache.IdCount}");
			sb.AppendLine($"time_ms={Time.ElapsedMilliseconds}");
			return sb.ToString();
		}

		private string Ents(string filter, string comp, string idText, int limit) {
			var sb = new StringBuilder();
			IEnumerable<Entity> ents = EntityCache.GetEntities();
			if ( idText != null ) {
				ents = EntityCache.TryGetEntity((uint)ParseInt(idText, 0), out Entity one) && IsValid(one) ? new[] { one } : new Entity[0];
			}
			int n = 0;
			foreach ( var ent in ents ) {
				string p = ent.Path ?? "";
				if ( filter != null && p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
				if ( comp != null && !ent.HasComponent(comp) ) continue;
				if ( n++ >= limit ) { sb.AppendLine("..."); break; }
				sb.Append($"id={ent.Id} addr=0x{ent.Address.ToInt64():X} {p}");
				var comps = ent.GetComponents();
				if ( comps != null ) {
					if ( comp != null && comps.TryGetValue(comp, out IntPtr cp) ) {
						sb.Append($"  {comp}=0x{cp.ToInt64():X}");
					} else if ( idText != null ) {
						sb.AppendLine();
						foreach ( var kv in comps.OrderBy(k => k.Key) ) {
							sb.AppendLine($"    {kv.Key} = 0x{kv.Value.ToInt64():X}");
						}
						continue;
					}
				}
				sb.AppendLine();
			}
			if ( n == 0 ) sb.AppendLine("(none)");
			return sb.ToString();
		}

		private string Info(int id, string skillName) {
			if ( !EntityCache.TryGetEntity((uint)id, out Entity ent) || !IsValid(ent) ) return "error: no such entity\n";
			var sb = new StringBuilder();
			sb.AppendLine($"id={ent.Id} addr=0x{ent.Address.ToInt64():X} {ent.Path} game_time_ms_now={ReadGameTimeMs()}");
			var buffs = ent.GetComponent<Buffs>();
			if ( buffs != null ) {
				foreach ( var b in buffs.GetBuffs() ?? Empty<Buff>() ) {
					sb.AppendLine($"buff {b.Name} addr=0x{b.Address.ToInt64():X} MaxTime={b.MaxTime} Timer={b.Timer}");
				}
			}
			var animObj = ent.GetComponent<Animated>()?.AnimatedObject;
			var cac = animObj?.GetComponent<ClientAnimationController>();
			if ( cac != null ) {
				sb.AppendLine($"animated_object id={animObj.Id} addr=0x{animObj.Address.ToInt64():X} ClientAnimationController=0x{cac.Address.ToInt64():X} TimeSpentAnimating={cac.TimeSpentAnimating} AnimationId={cac.AnimationId}");
			}
			var stats = ent.GetStats();
			if ( stats != null ) {
				foreach ( var kv in stats.OrderBy(k => k.Key.ToString()) ) {
					string n = kv.Key.ToString();
					if ( Regex.IsMatch(n, "duration|expire|lifetime|time_alive|die|death|timer", RegexOptions.IgnoreCase) ) {
						sb.AppendLine($"stat {n} = {kv.Value}");
					}
				}
			}
			if ( skillName != null ) {
				var player = PoEMemory.GameRoot?.InGameState?.Player;
				var skill = player?.GetComponent<Actor>()?.GetSkill(skillName);
				if ( skill != null && skill.TryGetStat(Offsets.GameStat.MinionDuration, out int dur) ) {
					sb.AppendLine($"owner skill {skillName} MinionDuration={dur} ms");
				} else {
					sb.AppendLine($"owner skill {skillName}: not found or no MinionDuration");
				}
			}
			return sb.ToString();
		}

		private class GraphSnapshot {
			public Dictionary<string, (long addr, byte[] data)> Regions = new Dictionary<string, (long, byte[])>();
			public long TimeMs;
			public int GameTimeMs;
		}
		private readonly Dictionary<string, GraphSnapshot> graphSnaps = new Dictionary<string, GraphSnapshot>();

		private void WalkEntityGraph(Entity ent, int depth, int len, Action<string, long, byte[]> visit) {
			var seen = new HashSet<long>();
			void Region(string label, long addr, int n, int d) {
				if ( !seen.Add(addr) ) return;
				var buf = new byte[n];
				int got = PoEMemory.TryRead(new IntPtr(addr), buf);
				if ( got < 8 ) return;
				if ( got < n ) Array.Resize(ref buf, got);
				visit(label, addr, buf);
				if ( d > 0 ) {
					for ( int o = 0; o + 8 <= got; o += 8 ) {
						long q = BitConverter.ToInt64(buf, o);
						if ( IsValid(new IntPtr(q)) && !InModule(q) ) Region($"{label}+0x{o:X}*", q, Math.Min(n, 0x100), d - 1);
					}
				}
			}
			void One(string prefix, Entity e) {
				Region(prefix + "Entity", e.Address.ToInt64(), 0x200, 0);
				var comps = e.GetComponents();
				if ( comps == null ) return;
				foreach ( var kv in comps.OrderBy(k => k.Key) ) Region(prefix + kv.Key, kv.Value.ToInt64(), len, depth);
			}
			One("", ent);
			var animObj = ent.GetComponent<Animated>()?.AnimatedObject;
			if ( animObj != null && IsValid(animObj) ) One("anim:", animObj);
		}

		private string GraphSnap(int id, string name, int depth, int len) {
			if ( !EntityCache.TryGetEntity((uint)id, out Entity ent) || !IsValid(ent) ) return "error: no such entity\n";
			var snap = new GraphSnapshot() { TimeMs = Time.ElapsedMilliseconds, GameTimeMs = ReadGameTimeMs() };
			WalkEntityGraph(ent, depth, Math.Max(0x40, Math.Min(len, 0x2000)), (label, addr, data) => snap.Regions[label] = (addr, data));
			graphSnaps[name] = snap;
			return $"snapshot '{name}': {snap.Regions.Count} regions, {snap.Regions.Values.Sum(r => r.data.Length)} bytes, game_time_ms={snap.GameTimeMs}\n";
		}

		private string GraphDiff(string name, string name2, bool topOnly, string match) {
			if ( !graphSnaps.TryGetValue(name, out GraphSnapshot a) ) return "error: no such snapshot\n";
			GraphSnapshot b = null;
			if ( name2 != null && !graphSnaps.TryGetValue(name2, out b) ) return "error: no such snapshot name2\n";
			var sb = new StringBuilder();
			int nowMs = ReadGameTimeMs();
			sb.AppendLine($"# diff '{name}' (game_time_ms={a.GameTimeMs}) vs {(b != null ? $"'{name2}' (game_time_ms={b.GameTimeMs})" : $"now (game_time_ms={nowMs})")}; real elapsed={((b?.GameTimeMs ?? nowMs) - a.GameTimeMs) / 1000.0:F2}s");
			int lines = 0, changedSlots = 0;
			foreach ( var kv in a.Regions.OrderBy(k => k.Key) ) {
				if ( topOnly && kv.Key.Contains("*") ) continue;
				if ( match != null && kv.Key.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
				byte[] other;
				if ( b != null ) {
					if ( !b.Regions.TryGetValue(kv.Key, out var r2 ) ) continue;
					other = r2.data;
				} else {
					other = new byte[kv.Value.data.Length];
					if ( PoEMemory.TryRead(new IntPtr(kv.Value.addr), other) < other.Length ) continue;
				}
				byte[] old = kv.Value.data;
				for ( int o = 0; o + 4 <= Math.Min(old.Length, other.Length); o += 4 ) {
					int x = BitConverter.ToInt32(old, o), y = BitConverter.ToInt32(other, o);
					if ( x == y ) continue;
					// pointer-looking slots that changed (heap realloc / different target) are mostly noise
					changedSlots++;
					if ( lines >= 1500 ) continue;
					float fx = BitConverter.ToSingle(old, o), fy = BitConverter.ToSingle(other, o);
					string f = (PlausibleFloat(fx) != "-" || PlausibleFloat(fy) != "-") ? $"  f32 {fx:G7} -> {fy:G7} (d={fy - fx:G5})" : "";
					sb.AppendLine($"{kv.Key}+0x{o:X} @0x{kv.Value.addr + o:X}  i32 {x} -> {y} (d={y - x}){f}");
					lines++;
				}
			}
			sb.AppendLine($"# {changedSlots} changed 4-byte slots" + (changedSlots > lines ? " (output capped at 1500)" : ""));
			return sb.ToString();
		}

		/// <summary>
		/// Simulation ticks (the server tick counter, ~30/s). Lives in InGameState_Data, at ingame+0x218*+0xB18.
		/// </summary>
		private static bool TryReadSimTicks(out long ticks) {
			ticks = 0;
			var igs = PoEMemory.GameRoot?.InGameState;
			if ( !IsValid(igs) || !PoEMemory.TryRead(igs.Address + 0x218, out IntPtr data) || !IsValid(data) ) return false;
			if ( !PoEMemory.TryRead(data + 0xB18, out int t) ) return false;
			ticks = t;
			return true;
		}

		/// <summary>
		/// Look for a deadline/start stamp/remaining-time for every live entity whose path matches `filter`.
		/// Given the simulation tick now (T), the entity's elapsed animation time and its known total duration,
		/// predict the values a deadline, spawn stamp, remaining or elapsed field would hold (in ticks, ms and seconds)
		/// and report every 4/8 byte slot in the entity's components (+ pointees, + animated object, + the owner's
		/// DeployedObjects entry) that is within tolerance. One TSV line per hit; aggregate across calls offline:
		///   ent  T  elapsed_s  label  type  value  candidate  delta
		/// </summary>
		private string Hunt(string filter, double durSec, double rate, double tolTicks, int depth, int len, bool includePlayer) {
			if ( !TryReadSimTicks(out long T) ) return "error: cannot read SimulationTicks\n";
			var sb = new StringBuilder();
			var player = PoEMemory.GameRoot?.InGameState?.Player;
			sb.AppendLine($"# hunt filter={filter} dur={durSec}s rate={rate}/s T={T} game_time_ms={ReadGameTimeMs()}");
			int ents = 0;
			foreach ( var ent in EntityCache.GetEntities().ToList() ) {
				if ( ent.Path == null || ent.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
				var cac = ent.GetComponent<Animated>()?.AnimatedObject?.GetComponent<ClientAnimationController>();
				if ( cac == null ) continue;
				double elapsed = cac.TimeSpentAnimating;
				ents++;
				double E = elapsed * rate, DT = durSec * rate, R = DT - E;
				double msPerTick = 1000.0 / rate;

				// (name, predicted value, tolerance) for integer slots
				var intCands = new List<(string name, double value, double tol)> {
					("deadline_ticks", T + R, tolTicks), ("spawn_ticks", T - E, tolTicks),
					("remaining_ticks", R, tolTicks), ("elapsed_ticks", E, tolTicks),
					("duration_ticks", DT, 0.5),
					("deadline_ms", (T + R) * msPerTick, tolTicks * msPerTick), ("spawn_ms", (T - E) * msPerTick, tolTicks * msPerTick),
					("remaining_ms", R * msPerTick, tolTicks * msPerTick), ("elapsed_ms", elapsed * 1000, tolTicks * msPerTick),
					("duration_ms", durSec * 1000, 1),
				};
				var floatCands = new List<(string name, double value, double tol)> {
					("deadline_s", (T + R) / rate, tolTicks / rate), ("spawn_s", (T - E) / rate, tolTicks / rate),
					("remaining_s", durSec - elapsed, 0.05), ("elapsed_s", elapsed, 0.05), ("duration_s", durSec, 0.0005),
					("tick_s", T / rate, tolTicks / rate),
				};

				int hits = 0;
				void Check(string label, long addr, byte[] buf) {
					for ( int o = 0; o + 4 <= buf.Length && hits < 300; o += 4 ) {
						int i32 = BitConverter.ToInt32(buf, o);
						if ( i32 != 0 ) {
							foreach ( var c in intCands ) {
								if ( Math.Abs(i32 - c.value) <= c.tol ) { sb.AppendLine($"{ent.Id}\t{T}\t{elapsed:F3}\t{label}+0x{o:X}\ti32\t{i32}\t{c.name}\t{i32 - c.value:F1}"); hits++; }
							}
						}
						float f = BitConverter.ToSingle(buf, o);
						if ( !float.IsNaN(f) && f != 0 ) {
							foreach ( var c in floatCands ) {
								if ( Math.Abs(f - c.value) <= c.tol ) { sb.AppendLine($"{ent.Id}\t{T}\t{elapsed:F3}\t{label}+0x{o:X}\tf32\t{f:G7}\t{c.name}\t{f - c.value:F3}"); hits++; }
							}
						}
						if ( o % 8 == 0 && o + 8 <= buf.Length ) {
							long i64 = BitConverter.ToInt64(buf, o);
							if ( i64 > int.MaxValue || i64 < int.MinValue ) {
								foreach ( var c in intCands ) {
									if ( Math.Abs(i64 - c.value) <= c.tol ) { sb.AppendLine($"{ent.Id}\t{T}\t{elapsed:F3}\t{label}+0x{o:X}\ti64\t{i64}\t{c.name}\t{i64 - c.value:F1}"); hits++; }
								}
							}
							double d = BitConverter.ToDouble(buf, o);
							if ( !double.IsNaN(d) && d != 0 ) {
								foreach ( var c in floatCands ) {
									if ( Math.Abs(d - c.value) <= c.tol ) { sb.AppendLine($"{ent.Id}\t{T}\t{elapsed:F3}\t{label}+0x{o:X}\tf64\t{d:G9}\t{c.name}\t{d - c.value:F3}"); hits++; }
								}
							}
						}
					}
				}

				WalkEntityGraph(ent, depth, len, (label, addr, data) => Check(label, addr, data));

				// the owner's DeployedObjects entry for this entity (12 bytes: EntityId, SkillId, unknown)
				var actor = player?.GetComponent<Actor>();
				if ( actor != null && PoEMemory.TryRead(actor.Address + 0x740, out IntPtr head) && PoEMemory.TryRead(actor.Address + 0x748, out IntPtr tail) ) {
					long n = (tail.ToInt64() - head.ToInt64()) / 12;
					for ( long i = 0; i < n && i < 200; i++ ) {
						var entry = new byte[12];
						if ( PoEMemory.TryRead(new IntPtr(head.ToInt64() + i * 12), entry) == 12 && BitConverter.ToUInt32(entry, 0) == ent.Id ) {
							sb.AppendLine($"{ent.Id}\t{T}\t{elapsed:F3}\tdeployedEntry\traw\t{BitConverter.ToString(entry)}\tindex={i}\t");
							Check("deployedEntry", head.ToInt64() + i * 12, entry);
						}
					}
				}
				if ( includePlayer && player != null && IsValid(player) ) {
					WalkEntityGraph(player, depth, len, (label, addr, data) => Check("player:" + label, addr, data));
				}
				sb.AppendLine($"# entity {ent.Id} elapsed={elapsed:F3}s hits={hits}");
			}
			sb.AppendLine($"# {ents} matching entities");
			return sb.ToString();
		}

		/// <summary>
		/// Raw dump of every matching entity's component graph, for offline correlation (eg: which slot is linear in spawn time).
		/// Per entity a header line "#ENT id T game_time_ms elapsed_s" followed by "label&lt;TAB&gt;hexbytes" lines.
		/// </summary>
		private string Dump(string filter, int depth, int len, int limit) {
			TryReadSimTicks(out long T);
			var sb = new StringBuilder();
			int n = 0;
			foreach ( var ent in EntityCache.GetEntities().ToList() ) {
				if ( ent.Path == null || ent.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
				var cac = ent.GetComponent<Animated>()?.AnimatedObject?.GetComponent<ClientAnimationController>();
				if ( n++ >= limit ) break;
				sb.AppendLine($"#ENT\t{ent.Id}\t{T}\t{ReadGameTimeMs()}\t{(cac != null ? cac.TimeSpentAnimating : -1f):F4}");
				WalkEntityGraph(ent, depth, len, (label, addr, data) => sb.AppendLine($"{label}\t{BitConverter.ToString(data).Replace("-", "")}"));
			}
			return sb.ToString();
		}

		private static int ReadGameTimeMs() {
			var igs = PoEMemory.GameRoot?.InGameState;
			return IsValid(igs) && PoEMemory.TryRead(igs.Address + 0x92c, out int ms) ? ms : -1;
		}

		private string Scan(int id, string valueText, string tolText, string kind, int depth, int len) {
			if ( !EntityCache.TryGetEntity((uint)id, out Entity ent) || !IsValid(ent) ) return "error: no such entity\n";
			if ( string.IsNullOrWhiteSpace(valueText) ) return "error: missing 'value'\n";
			var values = valueText.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
			double tol = string.IsNullOrWhiteSpace(tolText) ? 0.01 : double.Parse(tolText, CultureInfo.InvariantCulture);
			kind = kind ?? "both";
			len = Math.Max(0x40, Math.Min(len, 0x2000));
			var sb = new StringBuilder();
			int hits = 0;
			var seen = new HashSet<long>();

			void ScanRegion(string label, long addr, int n, int d) {
				if ( hits >= 100 || !seen.Add(addr) ) return;
				var buf = new byte[n];
				int got = PoEMemory.TryRead(new IntPtr(addr), buf);
				for ( int o = 0; o + 4 <= got && hits < 100; o += 4 ) {
					if ( kind != "i32" ) {
						float f = BitConverter.ToSingle(buf, o);
						foreach ( var v in values ) {
							if ( !float.IsNaN(f) && Math.Abs(f - v) <= tol && !(f == 0 && v != 0) ) {
								sb.AppendLine($"{label}+0x{o:X} f32 {f} (~{v})  @0x{addr + o:X}");
								hits++;
							}
						}
					}
					if ( kind != "f32" ) {
						int iv = BitConverter.ToInt32(buf, o);
						foreach ( var v in values ) {
							if ( Math.Abs(iv - v) <= Math.Max(0, tol) && !(iv == 0 && v != 0) && Math.Abs(v) >= 2 ) {
								sb.AppendLine($"{label}+0x{o:X} i32 {iv} (~{v})  @0x{addr + o:X}");
								hits++;
							}
						}
					}
				}
				if ( d > 0 ) {
					for ( int o = 0; o + 8 <= got; o += 8 ) {
						long q = BitConverter.ToInt64(buf, o);
						if ( IsValid(new IntPtr(q)) && !InModule(q) ) {
							ScanRegion($"{label}+0x{o:X}*", q, Math.Min(n, 0x100), d - 1);
						}
					}
				}
			}

			void ScanEntity(string prefix, Entity e) {
				ScanRegion(prefix + "Entity", e.Address.ToInt64(), 0x200, 0);
				var comps = e.GetComponents();
				if ( comps == null ) return;
				foreach ( var kv in comps.OrderBy(k => k.Key) ) {
					ScanRegion(prefix + kv.Key, kv.Value.ToInt64(), len, depth);
				}
			}

			sb.AppendLine($"# scan entity {id} for [{string.Join(",", values)}] tol={tol} kind={kind} depth={depth} len=0x{len:X} game_time_ms={ReadGameTimeMs()}");
			ScanEntity("", ent);
			var animObj = ent.GetComponent<Animated>()?.AnimatedObject;
			if ( animObj != null && IsValid(animObj) ) ScanEntity("anim:", animObj);
			if ( hits >= 100 ) sb.AppendLine("... (capped at 100 hits; narrow the tolerance)");
			if ( hits == 0 ) sb.AppendLine("(no matches)");
			return sb.ToString();
		}

		// ---------------------------------------------------------------- address expressions (main thread)

		private static readonly Regex RootRx = new Regex(@"^\s*(?<name>0x[0-9a-fA-F]+|[A-Za-z]+)(\((?<args>[^)]*)\))?");
		private static readonly Regex StepRx = new Regex(@"\G\s*(?<op>[+\-]\s*(0x[0-9a-fA-F]+|\d+)|\*)");

		private bool TryResolve(string expr, out long addr, out string err) {
			addr = 0;
			err = null;
			if ( string.IsNullOrWhiteSpace(expr) ) { err = "missing 'at'"; return false; }
			expr = expr.Replace(' ', '+'); // a raw + in a query string arrives as a space
			var m = RootRx.Match(expr);
			if ( !m.Success ) { err = $"cannot parse root of '{expr}'"; return false; }
			string name = m.Groups["name"].Value;
			string[] args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(s => s.Trim()).ToArray() : new string[0];
			var igs = PoEMemory.GameRoot?.InGameState;
			switch ( name.ToLowerInvariant() ) {
				case "base": addr = PoEMemory.Memory.BaseAddress.ToInt64(); break;
				case "gameroot": addr = PoEMemory.GameRoot?.Address.ToInt64() ?? 0; break;
				case "ingame": addr = igs?.Address.ToInt64() ?? 0; break;
				case "player": addr = IsValid(igs) ? igs.Player.Address.ToInt64() : 0; break;
				case "ent":
				case "comp": {
					if ( args.Length < 1 || !EntityCache.TryGetEntity((uint)ParseInt(args[0], 0), out Entity ent) || !IsValid(ent) ) {
						err = $"no such entity in '{name}({string.Join(",", args)})'";
						return false;
					}
					if ( name.Equals("ent", StringComparison.OrdinalIgnoreCase) ) {
						addr = ent.Address.ToInt64();
					} else {
						if ( args.Length < 2 || !(ent.GetComponents()?.TryGetValue(args[1], out IntPtr cp) ?? false) ) {
							err = $"entity has no component '{(args.Length > 1 ? args[1] : "")}'";
							return false;
						}
						addr = ent.GetComponents()[args[1]].ToInt64();
					}
					break;
				}
				default:
					if ( name.StartsWith("0x") ) {
						addr = Convert.ToInt64(name.Substring(2), 16);
					} else {
						err = $"unknown root '{name}'";
						return false;
					}
					break;
			}
			if ( addr == 0 ) { err = $"root '{name}' resolved to null"; return false; }

			int pos = m.Length;
			while ( pos < expr.Length ) {
				var s = StepRx.Match(expr, pos);
				if ( !s.Success ) { err = $"cannot parse step at '{expr.Substring(pos)}'"; return false; }
				string op = s.Groups["op"].Value.Replace(" ", "");
				if ( op == "*" ) {
					if ( !PoEMemory.TryRead(new IntPtr(addr), out long target) ) { err = $"failed to dereference 0x{addr:X}"; return false; }
					addr = target;
				} else {
					long n = ParseLong(op.Substring(1), 0);
					addr += op[0] == '-' ? -n : n;
				}
				pos += s.Length;
			}
			return true;
		}

		// ---------------------------------------------------------------- hexdump

		private static int ParseInt(string s, int def) => (int)ParseLong(s, def);
		private static long ParseLong(string s, long def) {
			if ( string.IsNullOrWhiteSpace(s) ) return def;
			s = s.Trim();
			if ( s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ) {
				return long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h) ? h : def;
			}
			return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ? d : def;
		}

		private static bool InModule(long v) {
			long b = PoEMemory.Memory.BaseAddress.ToInt64();
			return v >= b && v < b + PoEMemory.Memory.ModuleSize;
		}

		private static string PlausibleFloat(float f) {
			float a = Math.Abs(f);
			if ( float.IsNaN(f) || float.IsInfinity(f) || a < 1e-4f || a > 1e9f ) return "-";
			return f.ToString("G7", CultureInfo.InvariantCulture);
		}

		/// <summary>Describe what a qword in the dump might be: a module address, a heap pointer, a string, etc.</summary>
		private static string TagQword(long v) {
			if ( v == 0 ) return "";
			if ( InModule(v) ) return $"mod+0x{v - PoEMemory.Memory.BaseAddress.ToInt64():X}";
			if ( !IsValid(new IntPtr(v)) ) return "";
			var probe = new byte[32];
			if ( PoEMemory.TryRead(new IntPtr(v), probe) < 32 ) return "ptr(unreadable)";
			long first = BitConverter.ToInt64(probe, 0);
			if ( InModule(first) ) return $"ptr->obj vtable=mod+0x{first - PoEMemory.Memory.BaseAddress.ToInt64():X}";
			// ascii run
			int n = 0;
			while ( n < probe.Length && probe[n] >= 0x20 && probe[n] < 0x7f ) n++;
			if ( n >= 4 ) return $"ptr->\"{Encoding.ASCII.GetString(probe, 0, n)}\"";
			// utf16 run
			int w = 0;
			while ( w + 1 < probe.Length && probe[w] >= 0x20 && probe[w] < 0x7f && probe[w + 1] == 0 ) w += 2;
			if ( w >= 8 ) return $"ptr->u\"{Encoding.Unicode.GetString(probe, 0, w)}\"";
			return "ptr";
		}

		private string Dump(long addr, int len, int depth, string title) {
			len = Math.Max(8, Math.Min(len, 0x4000));
			var sb = new StringBuilder();
			sb.AppendLine($"# {title} len=0x{len:X} t={Time.ElapsedMilliseconds}ms");
			var visited = new HashSet<long>();
			DumpRegion(sb, addr, len, depth, visited, "");
			return sb.ToString();
		}

		private void DumpRegion(StringBuilder sb, long addr, int len, int depth, HashSet<long> visited, string indent) {
			var buf = new byte[len];
			int got = PoEMemory.TryRead(new IntPtr(addr), buf);
			if ( got <= 0 ) {
				sb.AppendLine($"{indent}0x{addr:X}: unreadable");
				return;
			}
			var children = new List<(int off, long ptr)>();
			for ( int o = 0; o + 8 <= got; o += 8 ) {
				long q = BitConverter.ToInt64(buf, o);
				int i0 = BitConverter.ToInt32(buf, o), i1 = BitConverter.ToInt32(buf, o + 4);
				string hex = BitConverter.ToString(buf, o, 8).Replace("-", " ");
				string tag = TagQword(q);
				var line = new StringBuilder();
				line.Append($"{indent}+0x{o:X3} {hex} | i32 {i0,11} {i1,11} | f32 {PlausibleFloat(BitConverter.ToSingle(buf, o)),-10} {PlausibleFloat(BitConverter.ToSingle(buf, o + 4)),-10}");
				if ( tag != "" ) {
					line.Append(" | " + tag);
					if ( tag.StartsWith("ptr") && !InModule(q) ) children.Add((o, q));
				} else if ( q > int.MaxValue || q < int.MinValue ) {
					line.Append($" | i64 {q}");
				}
				sb.AppendLine(line.ToString().TrimEnd());
			}
			if ( depth > 0 ) {
				foreach ( var (off, ptr) in children ) {
					if ( !visited.Add(ptr) ) continue;
					sb.AppendLine($"{indent}  -- +0x{off:X} -> 0x{ptr:X}");
					DumpRegion(sb, ptr, Math.Min(len, 0x80), depth - 1, visited, indent + "    ");
				}
			}
		}

		// ---------------------------------------------------------------- sampling: /track and /record

		private class Sample {
			public double T; // ms since the first sample
			public byte[][] Data; // one buffer per region
		}

		private string SampleCommand(bool record, System.Collections.Specialized.NameValueCollection q) {
			string[] ats = q.GetValues("at");
			string[] lens = q.GetValues("len") ?? new string[0];
			if ( ats == null ) return "error: missing 'at'\n";
			if ( !record && ats.Length != 1 ) return "error: /track takes exactly one 'at' (use /record for several)\n";
			int intervalMs = Math.Max(1, ParseInt(q["ms"], 100));
			int durMs = Math.Max(intervalMs, Math.Min(ParseInt(q["dur"], 5000), 10 * 60 * 1000));

			var addrs = new long[ats.Length];
			var sizes = new int[ats.Length];
			string err = null;
			string resolveResult = RunOnMain(() => {
				for ( int i = 0; i < ats.Length; i++ ) {
					if ( !TryResolve(ats[i], out addrs[i], out err) ) return $"error: {err}\n";
					sizes[i] = Math.Max(8, Math.Min(ParseInt(i < lens.Length ? lens[i] : (lens.Length > 0 ? lens[lens.Length - 1] : null), 0x40), 0x4000));
				}
				return "";
			});
			if ( resolveResult.Length > 0 ) return resolveResult;

			var samples = new List<Sample>();
			var clock = System.Diagnostics.Stopwatch.StartNew();
			long next = 0;
			int failures = 0;
			while ( clock.ElapsedMilliseconds <= durMs ) {
				double t = clock.Elapsed.TotalMilliseconds;
				var s = new Sample() { T = t, Data = new byte[addrs.Length][] };
				bool ok = true;
				for ( int i = 0; i < addrs.Length && ok; i++ ) {
					s.Data[i] = new byte[sizes[i]];
					ok = PoEMemory.TryRead(new IntPtr(addrs[i]), s.Data[i]) == sizes[i];
				}
				if ( ok ) samples.Add(s); else failures++;
				next += intervalMs;
				long wait = next - clock.ElapsedMilliseconds;
				if ( wait > 0 ) Thread.Sleep((int)wait);
			}
			if ( samples.Count < 2 ) return $"error: only {samples.Count} readable samples ({failures} failed reads); target gone?\n";

			var sb = new StringBuilder();
			if ( record ) {
				string dir = Path.Combine(AppContext.BaseDirectory, "memdumps");
				Directory.CreateDirectory(dir);
				string label = Regex.Replace(q["name"] ?? "dump", @"[^A-Za-z0-9_\-]", "_");
				string file = Path.Combine(dir, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
				using ( var w = new StreamWriter(file, false, new UTF8Encoding(false)) ) {
					var regions = string.Join(",", Enumerable.Range(0, addrs.Length).Select(i =>
						$"{{\"at\":\"{ats[i].Replace("\\", "\\\\").Replace("\"", "\\\"")}\",\"addr\":\"0x{addrs[i]:X}\",\"len\":{sizes[i]}}}"));
					w.WriteLine($"{{\"meta\":{{\"intervalMs\":{intervalMs},\"durMs\":{durMs},\"pid\":{PoEMemory.Memory.Pid},\"base\":\"0x{PoEMemory.Memory.BaseAddress.ToInt64():X}\",\"regions\":[{regions}]}}}}");
					foreach ( var s in samples ) {
						w.WriteLine($"{{\"t\":{s.T.ToString("F2", CultureInfo.InvariantCulture)},\"hex\":[{string.Join(",", s.Data.Select(d => "\"" + BitConverter.ToString(d).Replace("-", "") + "\""))}]}}");
					}
				}
				sb.AppendLine($"recorded {samples.Count} samples ({failures} failed) over {samples[samples.Count - 1].T:F0}ms to {file}");
			} else {
				sb.AppendLine($"# track {ats[0]} = 0x{addrs[0]:X} len=0x{sizes[0]:X} samples={samples.Count} failed={failures} span={samples[samples.Count - 1].T:F0}ms");
				Analyze(samples, 0, sb);
			}
			return sb.ToString();
		}

		private class SlotReport {
			public int Offset;
			public string Kind;
			public string Shape; // inc / dec / step / noisy
			public double Rank;
			public string Text;
		}

		/// <summary>
		/// Look at every aligned 4 and 8 byte slot in region <paramref name="region"/> across all samples and describe
		/// the ones that change. Integers are unwrapped across overflow so a counter that wraps still looks linear.
		/// Both widths are reported, so a value that is really an int64 shows up as both an i64 slot and its low i32 slot.
		/// </summary>
		private static void Analyze(List<Sample> samples, int region, StringBuilder sb) {
			int len = samples[0].Data[region].Length;
			double[] t = samples.Select(s => s.T / 1000.0).ToArray();
			var reports = new List<SlotReport>();
			int constant4 = 0;

			foreach ( int width in new[] { 4, 8 } ) {
				for ( int o = 0; o + width <= len; o += 4 ) {
					if ( width == 8 && o % 8 != 0 ) continue;
					var raw = samples.Select(s => width == 4 ? (long)BitConverter.ToUInt32(s.Data[region], o) : BitConverter.ToInt64(s.Data[region], o)).ToArray();
					if ( raw.All(v => v == raw[0]) ) {
						if ( width == 4 ) constant4++;
						continue;
					}
					// integer view, unwrapped
					var ints = new double[raw.Length];
					int zeroDeltas = 0, ups = 0, downs = 0;
					for ( int i = 1; i < raw.Length; i++ ) {
						long d = width == 4 ? (int)(uint)(raw[i] - raw[i - 1]) : unchecked(raw[i] - raw[i - 1]);
						ints[i] = ints[i - 1] + d;
						if ( d == 0 ) zeroDeltas++; else if ( d > 0 ) ups++; else downs++;
					}
					string iname = width == 4 ? "i32" : "i64";
					AddReport(reports, o, iname, ints, t, zeroDeltas, ups, downs, raw.Length, v => v.ToString("0.###", CultureInfo.InvariantCulture),
						width == 4 ? (Func<int, string>)(i => ((int)raw[i]).ToString()) : (i => raw[i].ToString()));

					// float view
					var floats = new double[raw.Length];
					bool plausible = true;
					for ( int i = 0; i < raw.Length && plausible; i++ ) {
						double f = width == 4 ? BitConverter.ToSingle(samples[i].Data[region], o) : BitConverter.ToDouble(samples[i].Data[region], o);
						if ( double.IsNaN(f) || double.IsInfinity(f) || Math.Abs(f) > 1e12 || (f != 0 && Math.Abs(f) < 1e-6) ) plausible = false;
						floats[i] = f;
					}
					if ( plausible ) {
						int fz = 0, fu = 0, fd = 0;
						for ( int i = 1; i < floats.Length; i++ ) {
							double d = floats[i] - floats[i - 1];
							if ( d == 0 ) fz++; else if ( d > 0 ) fu++; else fd++;
						}
						AddReport(reports, o, width == 4 ? "f32" : "f64", floats, t, fz, fu, fd, floats.Length, v => v.ToString("0.####", CultureInfo.InvariantCulture),
							i => floats[i].ToString("0.####", CultureInfo.InvariantCulture));
					}
				}
			}

			sb.AppendLine($"# {constant4} of {len / 4} 4-byte slots never changed; {reports.Count} interpretations of changing slots follow (monotonic/linear first)");
			int shown = 0;
			foreach ( var r in reports.OrderByDescending(r => r.Rank).ThenBy(r => r.Offset) ) {
				if ( shown++ >= 80 ) { sb.AppendLine("... (more; narrow len or use /record)"); break; }
				sb.AppendLine(r.Text);
			}
		}

		private static readonly (double rate, string unit)[] KnownRates = {
			(1, "per-sec"), (30, "30Hz"), (60, "60Hz"), (1000, "~ms"), (1e6, "~us"), (1e7, "~100ns"), (1e9, "~ns"),
		};

		private static void AddReport(List<SlotReport> reports, int offset, string kind, double[] y, double[] t, int zeroDeltas, int ups, int downs, int n,
			Func<double, string> fmt, Func<int, string> rawAt) {

			int intervals = n - 1;
			double flat = 100.0 * zeroDeltas / intervals;
			// least squares line y = a + b t
			double mt = t.Average(), my = y.Average();
			double sxx = 0, sxy = 0, syy = 0;
			for ( int i = 0; i < n; i++ ) {
				sxx += (t[i] - mt) * (t[i] - mt);
				sxy += (t[i] - mt) * (y[i] - my);
				syy += (y[i] - my) * (y[i] - my);
			}
			double slope = sxx > 0 ? sxy / sxx : 0;
			double r2 = (sxx > 0 && syy > 0) ? (sxy * sxy) / (sxx * syy) : 0;
			bool mono = ups == 0 || downs == 0;
			var distinct = new List<string>();
			for ( int i = 0; i < n && distinct.Count <= 8; i++ ) {
				string v = rawAt(i);
				if ( !distinct.Contains(v) ) distinct.Add(v);
			}

			string shape;
			double rank;
			if ( mono && r2 > 0.95 && distinct.Count > 2 ) {
				shape = slope > 0 ? "inc" : "dec";
				rank = 3 + r2;
			} else if ( distinct.Count <= 6 ) {
				shape = "step";
				rank = 2;
			} else if ( mono ) {
				shape = slope > 0 ? "inc-irregular" : "dec-irregular";
				rank = 1.5;
			} else {
				shape = "noisy";
				rank = 0;
			}
			if ( shape == "noisy" && kind.StartsWith("f") ) return; // noisy float views of changing ints are just bit noise
			if ( shape == "noisy" && reports.Count(r => r.Shape == "noisy") >= 12 ) return;

			var text = new StringBuilder();
			text.Append($"+0x{offset:X3} {kind,-3} {shape,-13}");
			if ( shape == "step" ) {
				text.Append($" values: {string.Join(" -> ", distinct)}");
			} else if ( shape != "noisy" ) {
				text.Append($" slope={fmt(slope)}/s r2={r2:F4} flat={flat:F0}% first={rawAt(0)} last={rawAt(n - 1)}");
				double a = Math.Abs(slope);
				foreach ( var kr in KnownRates ) {
					if ( Math.Abs(a - kr.rate) < 0.05 * kr.rate ) { text.Append($"  [{kr.unit}]"); break; }
				}
			} else {
				text.Append($" first={rawAt(0)} last={rawAt(n - 1)} flat={flat:F0}%");
			}
			reports.Add(new SlotReport() { Offset = offset, Kind = kind, Shape = shape, Rank = rank, Text = text.ToString() });
		}
	}
}
