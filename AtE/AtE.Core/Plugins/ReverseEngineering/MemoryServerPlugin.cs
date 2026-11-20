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
		private long exitAt = 0; // when (Time.ElapsedMilliseconds) to close the overlay, after an /exit request; 0 = not asked

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
			// /exit: shut down the way the console's Exit button does, a moment after the request so its response gets sent first
			if ( exitAt > 0 && Time.ElapsedMilliseconds >= exitAt ) {
				exitAt = 0;
				Log("MemoryServer: exiting by request");
				PluginBase.SaveIniFile();
				StopListener();
				Overlay.Close();
				return this;
			}
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
			// the overlay's recent log lines (Log), the last n (default 100); matching filter, if given
			if ( path == "/log" ) {
				var lines = RecentLog.ToArray().Where(l => q["filter"] == null || l.Contains(q["filter"], StringComparison.OrdinalIgnoreCase));
				return string.Join(NL, lines.TakeLast(ParseInt(q["n"], 100))) + NL;
			}
			// ask the overlay to close itself cleanly (save settings, close the window, end the message loop), attached or not
			if ( path == "/exit" ) {
				Interlocked.CompareExchange(ref exitAt, Time.ElapsedMilliseconds + 250, 0);
				return "exiting\n";
			}
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
				case "/tree": {
					string at = q["at"];
					int depth = ParseInt(q["depth"], 3);
					return RunOnMain(() => TryResolve(at, out long addr, out string err) ? ElementTree(new IntPtr(addr), depth, q["rects"] == "1") : $"error: {err}" + NL);
				}
				case "/waypoints":
					return RunOnMain(WaypointsText);
				case "/route":
					return RunOnMain(() => RouteText(q["to"]));
				case "/tiles":
					return RunOnMain(() => TilesText(q["filter"]));
				case "/doors":
					return RunOnMain(DoorsText);
				case "/grid":
					// the walkable grid around at=X,Y (default: the player), r cells each way: clearance digit (0 = blocked '#'),
					// @ player, B chest/barrel, T trap, D door entity
					return RunOnMain(() => {
						var terrain = GetTerrain();
						if ( terrain == null || !terrain.IsLoaded ) return "no terrain" + NL;
						var me = GetPlayer()?.GetComponent<Positioned>();
						var at = IsValid(me) ? me.GridPos : default;
						var ap = (q["at"] ?? "").Split(',');
						if ( ap.Length == 2 && int.TryParse(ap[0], out int ax) && int.TryParse(ap[1], out int ay) ) at = new Offsets.Vector2i(ax, ay);
						int r = int.TryParse(q["r"], out int rr) ? Math.Clamp(rr, 2, 60) : 20;
						var marks = new Dictionary<long, char>();
						foreach ( var ent in GetEntities() ) {
							if ( !IsValid(ent) ) continue;
							var pos = ent.GetComponent<Positioned>();
							if ( !IsValid(pos) ) continue;
							string path = ent.Path ?? "";
							char c = path.Contains("/Chests/") ? 'B' : AreaTraps.IsTrapPath(path) ? 'T' : path.EndsWith("/Door") || path.Contains("Door_") ? 'D' : '\0';
							if ( c != '\0' ) marks[((long)pos.GridPos.Y << 32) | (uint)pos.GridPos.X] = c;
						}
						if ( IsValid(me) ) marks[((long)me.GridPos.Y << 32) | (uint)me.GridPos.X] = '@';
						var sb = new StringBuilder();
						sb.AppendLine($"grid around {at}, x {at.X - r}..{at.X + r} (left to right), y {at.Y - r}..{at.Y + r} (top to bottom)");
						for ( int y = at.Y - r; y <= at.Y + r; y++ ) {
							sb.Append($"{y,5} ");
							for ( int x = at.X - r; x <= at.X + r; x++ ) {
								if ( marks.TryGetValue(((long)y << 32) | (uint)x, out char m) ) { sb.Append(m); continue; }
								int cl = terrain.Clearance(x, y);
								sb.Append(cl <= 0 ? '#' : (char)('0' + Math.Min(9, cl)));
							}
							sb.AppendLine();
						}
						return sb.ToString();
					});
				case "/localents":
					// the entities EntityCache skips: those in the game's entity list with the high bit of their id set (taken
					// to be client-side), with their path, grid cell and animated object; filter by a part of either path
					return RunOnMain(() => {
						var sb = new StringBuilder();
						var frontier = new Queue<Offsets.EntityListNode>();
						var seen = new HashSet<IntPtr>();
						frontier.Enqueue(PoEMemory.GameRoot.InGameState.EntityListHead);
						int idAt = (int)System.Runtime.InteropServices.Marshal.OffsetOf<Offsets.Entity>("Id");
						bool first = true;
						int count = 0;
						string filter = q["filter"];
						while ( frontier.Count > 0 && seen.Count < 4000 ) {
							var node = frontier.Dequeue();
							if ( !seen.Add(node.Entity) ) continue;
							if ( first ) first = false; // (the head node has no entity)
							else if ( PoEMemory.TryRead(node.Entity + idAt, out uint id) && id != 0 && (int)id < 0 ) {
								count += 1;
								var ent = new Entity() { Address = node.Entity };
								string path = ent.Path ?? "?";
								var anim = ent.GetComponent<Animated>();
								string ao = IsValid(anim) ? anim.AnimatedObjectPath : null;
								var pos = ent.GetComponent<Positioned>();
								if ( filter == null || path.Contains(filter, StringComparison.OrdinalIgnoreCase) || (ao?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) )
								{
									var cac = IsValid(anim) ? anim.AnimatedObject?.GetComponent<ClientAnimationController>() : null;
									string animTime = IsValid(cac) ? $" anim={cac.TimeSpentAnimating:F2}s#{cac.AnimationId}" : "";
									sb.AppendLine($"id=0x{id:X8} addr=0x{node.Entity.ToInt64():X} {path}{(IsValid(pos) ? $" at={pos.GridPos}" : "")}{animTime}{(ao != null ? $" ao={ao}" : "")}");
									if ( q["comps"] == "1" ) foreach ( var kv in (ent.GetComponents() ?? new Dictionary<string, IntPtr>()).OrderBy(k => k.Key) ) sb.AppendLine($"    {kv.Key} = 0x{kv.Value.ToInt64():X}");
								}
							}
							if ( PoEMemory.TryRead(node.First, out Offsets.EntityListNode a) ) frontier.Enqueue(a);
							if ( PoEMemory.TryRead(node.Second, out Offsets.EntityListNode b) ) frontier.Enqueue(b);
							if ( PoEMemory.TryRead(node.Third, out Offsets.EntityListNode c) ) frontier.Enqueue(c);
						}
						return $"{count} entities with a negative id ({seen.Count} nodes walked)" + NL + sb;
					});
				case "/danger":
					// the DangerMap (DangerMap.Describe): its hazards, and the cells r each way around at=X,Y (default: the player)
					return RunOnMain(() => {
						var map = DangerMap.Get();
						if ( map == null ) return "no danger map (no terrain or player)" + NL;
						Offsets.Vector2i? at = null;
						var ap = (q["at"] ?? "").Split(',');
						if ( ap.Length == 2 && int.TryParse(ap[0], out int ax) && int.TryParse(ap[1], out int ay) ) at = new Offsets.Vector2i(ax, ay);
						int r = int.TryParse(q["r"], out int rr) ? Math.Clamp(rr, 2, 100) : 30;
						float z = Position(GetPlayer()).Z;
						return map.Describe(at, r, true, c => (TryGridToScreen(c, z, out var screen), screen), q["rotate"] == "1");
					});
				case "/traps":
					// the traps loaded now (Terrain.Traps), nearest the player first, and the follower's state
					return RunOnMain(() => {
						var terrain = GetTerrain();
						var pos = GetPlayer()?.GetComponent<Positioned>();
						if ( terrain == null || !IsValid(pos) ) return "no terrain or player" + NL;
						var me = pos.GridPos;
						var sb = new StringBuilder();
						var traps = terrain.Traps.Where(t => t.Refresh()).OrderBy(t => (t.GridPos.X - me.X) * (t.GridPos.X - me.X) + (t.GridPos.Y - me.Y) * (t.GridPos.Y - me.Y)).ToList();
						sb.AppendLine($"player at {me}; {traps.Count} traps; follower {DefaultFollower.Status}: {DefaultFollower.Detail}");
						foreach ( var t in traps ) {
							float d = MathF.Sqrt((t.GridPos.X - me.X) * (t.GridPos.X - me.X) + (t.GridPos.Y - me.Y) * (t.GridPos.Y - me.Y));
							sb.AppendLine($"  {t}  {d:F1} cells");
						}
						return sb.ToString();
					});
				case "/path":
					// plan a path (DefaultNavigator.FindPath, which only reads) from the player to a grid cell, and list its steps
					return RunOnMain(() => {
						var terrain = GetTerrain();
						var pos = GetPlayer()?.GetComponent<Positioned>();
						var parts = (q["to"] ?? "").Split(',');
						if ( terrain == null || !IsValid(pos) || parts.Length != 2 || !int.TryParse(parts[0], out int tx) || !int.TryParse(parts[1], out int ty) )
							return "give to=X,Y (grid cells), with terrain and a player" + NL;
						var start = pos.GridPos;
						var fromParts = (q["from"] ?? "").Split(',');
						if ( fromParts.Length == 2 && int.TryParse(fromParts[0], out int fx) && int.TryParse(fromParts[1], out int fy) ) start = new Offsets.Vector2i(fx, fy);
						var path = DefaultNavigator.FindPath(terrain, start, new Offsets.Vector2i(tx, ty));
						if ( path == null ) return $"no path from {start} to <{tx}, {ty}>{(DefaultNavigator.LastFailure == null ? "" : $": {DefaultNavigator.LastFailure}")}" + NL;
						var sb = new StringBuilder();
						sb.AppendLine($"from {start} to <{tx}, {ty}>: {path.Cells.Count} cells, {path.Steps.Count} steps, method {path.Method}, {path.ElapsedMs:F1} ms");
						foreach ( var step in path.Steps ) sb.AppendLine($"  {step.Kind} {step.Target}{(step.Lever == null ? "" : $" ({step.Lever})")}{(step.Door == null ? "" : $" ({step.Door})")}");
						return sb.ToString();
					});
				case "/labels":
					return RunOnMain(LabelsText);
				case "/gamekeys":
					// (not read yet: try reading them now, from the Options panel's element tree, without opening it)
					return RunOnMain(() => {
						bool already = GameKeys.Loaded;
						if ( !already && !GameKeys.TryRead() )
							return $"the key bindings could not be read, from memory (the Input page's key list) or from the Options panel's tree (its Input body {(IsValid(GameKeys.Body()) ? "is there, but has no \"Use Portal Scroll\" row" : "is not built")})\n";
						return (already ? "" : "read now\n") + $"source: {GameKeys.Source}\n"
							+ $"inventory key: {SkillBar.KeyName(GameKeys.InventoryKey)}\n" + string.Join("", GameKeys.All().Select(kv => $"{kv.Key} = {kv.Value}\n"));
					});
				case "/skillbar":
					return RunOnMain(() => SkillBarText(q["stats"] == "1"));
				case "/offsets/run":
					return RunOnMain(() => { var t = OffsetTests(); t.Start(q["only"], q["observers"] == "1"); return "started\n" + t.Summary() + "\n"; });
				case "/offsets/stop":
					return RunOnMain(() => { OffsetTests().Stop(); return "stopped\n"; });
				case "/offsets/observe": // the observers only, on the character as it is (no checks, no routine)
					return RunOnMain(() => { var t = OffsetTests(); t.Start("-", true); return "observing\n" + t.Summary() + "\n"; });
				case "/offsets/routine":
					return RunOnMain(() => q["cmd"] == "start" ? OffsetTests().ResumeRoutine() : q["cmd"] == "stop" ? OffsetTests().HoldRoutine("stopped by request") : "error: cmd=start or cmd=stop\n");
				case "/offsets/coverage":
					return RunOnMain(() => q["format"] == "unchecked" ? OffsetCoverage.UncoveredAsList() : OffsetCoverage.Compute().ToString());
				case "/offsets/report":
					return RunOnMain(() => OffsetTests().Report());
				case "/offsets/queue":
					return RunOnMain(() => OffsetTests().Queue(q["ids"] ?? ""));
				case "/offsets/golden":
					return RunOnMain(() => string.Join("", GoldenStore.All.Select(g => $"{(g.Value.Confirmed ? "confirmed  " : "UNCONFIRMED")} {g.Key} = {g.Value.Value}\n")));
				case "/offsets/confirm":
					return RunOnMain(() => $"confirmed {GoldenStore.Confirm(q["key"] ?? "")} golden values\n");
				case "/offsets/forget":
					return RunOnMain(() => string.IsNullOrEmpty(q["key"]) ? "error: key= is required\n" : $"forgot {GoldenStore.Forget(q["key"])} golden values\n");
				case "/mapping/test":
					return RunOnMain(() => MappingTest(q["name"], q["stop"] == "1"));
				case "/routine":
					return RunOnMain(RoutineText);
				case "/progress":
					return ProgressScan(q["key"]);
				case "/stash":
					return RunOnMain(StashText);
				case "/cursor":
					return RunOnMain(CursorText);
				case "/backpack":
					return RunOnMain(Backpack);
				case "/items":
					return RunOnMain(Items);
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
				case "/search":
					return Search(q["hex"], ParseInt(q["max"], 40), ParseInt(q["align"], 1));
				case "/pathto": {
					// resolve the roots on the main thread, then walk the pointers here (only reads)
					var roots = (q.GetValues("from") ?? new[] { "ingame", "ingame+0x218*", "ingame+0x290*", "gameroot" }).ToList();
					var resolved = new List<(string expr, long addr)>();
					string bad = RunOnMain(() => {
						foreach ( var r in roots ) {
							if ( !TryResolve(r, out long a, out string e) ) return $"error: {r}: {e}\n";
							resolved.Add((r, a));
						}
						return "";
					});
					if ( bad.Length > 0 ) return bad;
					if ( !TryResolve(q["target"] ?? "", out long target, out string terr) ) return $"error: target: {terr}\n";
					return PathTo(resolved, target, ParseInt(q["window"], 0x200), ParseInt(q["depth"], 3), ParseInt(q["len"], 0x1000), ParseInt(q["max"], 30));
				}
				default:
					return "error: unknown endpoint, try /help\n";
			}
		}

		// the worn and backpack items' mods, as stat ids and values (what item scoring reads)
		private static string Items() {
			var sb = new StringBuilder();
			// the flask bar (read with the inventory closed too), and the inventory panel's flask slots by where they are drawn
			for ( int i = 0; i < 5; i++ ) sb.AppendLine($"Flask bar {i + 1}: {GetFlask(i)?.realEnt?.Path ?? "(empty)"}");
			if ( GetUI()?.InventoryPanel?.Flasks is Inventory flaskInv ) {
				var panel = flaskInv.GetClientRect();
				foreach ( var f in flaskInv.Items.Where(f => f?.Entity != null) ) {
					var r = f.GetClientRect();
					int slot = panel.Width <= 0 ? -1 : (int)((r.X + (r.Width / 2) - panel.X) / (panel.Width / 5));
					sb.AppendLine($"Flask panel slot {slot + 1}: {f.Entity.Path} rect {r} (panel {panel})");
				}
			}
			var slots = Equipment.WornSlots().Select(s => (s.name, s.inventory)).ToList();
			if ( GetUI()?.InventoryPanel?.Backpack is Inventory backpack ) slots.Add(("Backpack", backpack));
			var lists = slots.Select(s => (s.name, items: s.inventory.Items.Cast<InventoryItem>())).ToList();
			if ( Town.IsOpen(Town.PurchaseWindow) ) lists.Add(("Purchase", BuildSection.ItemElements(Town.PurchaseWindow, 12)));
			foreach ( var (name, items) in lists ) {
				foreach ( var item in items ) {
					var e = item?.Entity;
					if ( e == null ) continue;
					var mods = e.GetComponent<Mods>();
					var baseInfo = e.GetComponent<Base>();
					sb.Append($"{name}: {Equipment.ItemName(e) ?? "?"} ({e.Path}) base info 0x{(IsValid(baseInfo) ? baseInfo.Cache.ptrToBaseInfo.ToInt64() : 0):X} needs level {ItemInfo.From(e)?.ReqLevel.ToString() ?? "-"}{(IsValid(mods) ? $" {mods.Rarity}{(mods.IsIdentified ? "" : " unidentified")} ilvl {mods.Level} req {mods.ReqLevel}" : "")}{NL}");
					if ( e.GetComponent<Armour>() is Armour armour && IsValid(armour) ) {
						var v = armour.ArmourValues.Value;
						sb.Append($"    armour {v.ArmourMin}-{v.ArmourMax}, evasion {v.EvasionMin}-{v.EvasionMax}, energy shield {v.ESMin}-{v.ESMax}, ward {v.WardMin}-{v.WardMax}{NL}");
					}
					if ( e.GetComponent<Weapon>() is Weapon weapon && IsValid(weapon) ) sb.Append($"    weapon {weapon.DamageMin}-{weapon.DamageMax}, attack time {weapon.AttackTime} ms, crit {weapon.CritChance}{NL}");
					if ( !IsValid(mods) ) continue;
					foreach ( var (kind, list) in new[] { ("implicit", mods.ImplicitMods), ("explicit", mods.ExplicitMods), ("enchant", mods.EnchantedMods) } ) {
						foreach ( var mod in list ) {
							sb.Append($"    {kind} {mod.GroupName}: {string.Join(", ", mod.Stats.Select(st => $"{st.stat}={st.value}{(st.local ? " (local)" : "")}"))}{NL}");
						}
					}
				}
			}
			return sb.ToString();
		}

		private static OffsetTestSection OffsetTests() => GetPlugin<AutomationPlugin>().OffsetTests;

		// a Mapping step test, as its button in the Mapping routine's Test Steps runs it (the Mapping routine is chosen
		// first, stopping the routine that was); no name: what is running, or how the last ended, and the tests there are
		private static string MappingTest(string name, bool stop) {
			var combat = GetPlugin<AutomationPlugin>()?.Combat;
			var mapping = combat?.Routines.OfType<MappingRoutine>().FirstOrDefault();
			if ( mapping == null ) return "error: no Mapping routine\n";
			if ( stop ) { mapping.StopTest("the memory server"); return mapping.TestStatus + "\n"; }
			if ( string.IsNullOrEmpty(name) )
				return mapping.TestStatus + "\ntests: " + string.Join(", ", mapping.Tests.Select(t => $"{t.Key} ({t.Name})")) + "\n";
			if ( !mapping.Tests.Any(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) )
				return $"error: no test {name}; tests: {string.Join(", ", mapping.Tests.Select(t => t.Key))}\n";
			if ( combat.Current != mapping ) { combat.Current?.Stop("the memory server started a Mapping test"); combat.Routine = mapping.Key; }
			mapping.StartTest(name);
			return "started\n" + mapping.TestStatus + "\n";
		}

		private const string Help =
@"MemoryServer (read-only, loopback only)
  /status
  /offsets/run[?only=A6,A4.Life[&observers=1]] | /offsets/observe (observers only, no checks) | /offsets/stop | /offsets/report     the Offsets tests (Automation > Offset Tests): start, stop, the results with details
  /mapping/test[?name=stash|pickmap|rollmap|openmap][&stop=1]   run one Mapping step test on its own (as its button does; chooses the Mapping routine); no name: what runs, or how the last ended; stop=1 stops it
  /offsets/routine?cmd=start|stop                   start or stop just the class routine testing mode runs after its checks (it is held when Hillock dies)
  /offsets/coverage[?format=unchecked]               which Offsets the checks cover (the ratchet against unchecked.txt); format=unchecked lists the gaps as an unchecked.txt
  /offsets/queue?ids=failed|clear|A2.Terrain,A4      queue checks for fixing (shown on screen as FIX)
  /offsets/golden | /offsets/confirm?key=<contains> | /offsets/forget?key=<contains>   the values a fresh character is known to have
  /ents?filter=<path substring>&comp=<ComponentName>&id=<entity id>&limit=30
  /tree?at=EXPR&depth=3[&rects=1]   an element and its children: path of child indexes, address, visible, (rect), text, and the item if it is an InventoryItem
  /route?to=<area name>         the route of areas (WorldArea.FindRoute) from here to an area, and each area's connections, waypoint, town, hub
  /waypoints                    the destinations on the waypoint panel, open or closed (read from its element tree): name, unlocked (visited), element, screen rect
  /tiles[?filter=]              the current area's tile files (.tdt): groups of neighboring tiles per file and where, and the exits known
  /doors                        the doors Terrain knows (closed/open, passage cells), each with a map of its box in the walkable grid
  /danger[?at=X,Y&r=30&rotate=1]   the DangerMap (AvoidSpam's hazards; built now if old): its hazards, the danger at a cell (default the player) and what covers it, and a picture r cells each way (framed with the screen's directions; rotate=1: turned to match the screen)
  /labels                       the labels on the ground: entity id and path, text, screen rect, and the entity's screen position
  /skillbar[?stats=1]           the skill bar's slots: key, skill name, internal name, id, use time, tags (stats=1: each skill's stats)
  /gamekeys                     the game's key bindings from its Options panel (GameKeys; read from its tree now, if not yet), and the inventory key chosen
  /routine                      the chosen combat routine's objectives: complete or not, and what IsDone says now
  /progress[?key=U]             reads the campaign progress (parts, acts) from the closed AreaInstanceUI; only if that fails, PRESSES KEYS to open it (U) and close it; reports
  /stash                        the items on the visible stash page: the rect a click aims at (StashItem.GetClientRect), name, path
  /cursor                       the item held on the mouse cursor (server-side cursor inventory) and the backpack item count
  /backpack                     the items in the backpack (open or not): element and entity addresses, path, components
  /items                        the worn, backpack and purchase window items: name, rarity, and each mod's stats (Stats.dat ids) with their values
  /log?n=100&filter=            the overlay's last log lines (the last n, matching filter)
  /exit                         close the overlay cleanly (saves settings, like the console's Exit button); use this instead of killing the process
  /info?id=<entity id>&skill=<owner skill name>   ground truth: buffs (timer/max), animation time, duration-ish stats, owner skill MinionDuration
  /scan?id=<entity id>&value=1.5,3000&tol=0.01&kind=f32|i32|both&depth=1&len=0x400   find values in all components (and the animated object's) + pointees
  /gsnap?id=<entity id>&name=a&depth=1&len=0x400    snapshot all components + pointees (+ animated object) under a label
  /gdiff?name=a[&name2=b][&top=1][&match=Comp]   re-read (or compare to snapshot b) and list every 4-byte slot that changed, as i32 and f32
  /read?at=EXPR&len=0x80&depth=0        annotated hexdump; depth>0 also dumps what valid pointers point at
  /search?hex=4F000000??00&max=40&align=1   find bytes (?? any) in the game's read-write memory (slow: scans it all)
  /pathto?target=0xADDR&from=ingame&depth=3&len=0x1000&window=0x200   pointer chains from roots (default ingame, its data, UI elements, gameroot) to a struct
  /track?at=EXPR&len=0x40&ms=100&dur=5000    sample, then report only the slots that change (i32/i64/f32/f64)
  /record?at=EXPR[&at=EXPR2]&len=0x40[&len=0x100]&ms=100&dur=5000&name=label    writes memdumps/<name>-<time>.jsonl
EXPR = root then steps. roots: 0xHEX base gameroot ingame player ent(ID) comp(ID,Name) ui(1,142,3) (UIRoot children by index). steps: +N -N *
  e.g. ingame+0x920   comp(1234,DiesAfterTime)+0x20*   (use %2B for + in URLs, or curl -G --data-urlencode)\n";

		// ---------------------------------------------------------------- status / entities

		private string ElementTree(IntPtr address, int depth, bool rects = false) {
			var sb = new StringBuilder();
			void Walk(Element e, string path, int d) {
				if ( !IsValid(e) ) { sb.AppendLine($"{path} (invalid)"); return; }
				var item = new InventoryItem(e.Address).Entity;
				var children = e.Children.ToList();
				sb.AppendLine($"{path} 0x{e.Address.ToInt64():X} visible={e.IsVisibleLocal} children={children.Count}{(rects ? $" rect={e.GetClientRect()}" : "")} text={e.Text?.Trim()}{(item == null ? "" : $" item={item.Path} entity=0x{item.Address.ToInt64():X} base=0x{(item.GetComponent<Base>()?.Address ?? IntPtr.Zero).ToInt64():X} name={Equipment.ItemName(item)}")}");
				if ( d <= 0 ) return;
				for ( int i = 0; i < children.Count; i++ ) Walk(children[i], $"{path}.{i}", d - 1);
			}
			Walk(new Element() { Address = address }, "root", depth);
			return sb.ToString();
		}

		private const string NL = "\n";

		// the current area's tile files: per .tdt, how many groups of neighboring tiles and where (the walkable cell nearest
		// each group's center), plus the exits known now; filter keeps the paths containing it
		/// <summary>
		/// /doors: Terrain.Doors, each with its TriggerableBlockage box drawn from the walkable grid as read when the area
		/// loaded: '#' blocked, '.' walkable, 'P' a passage cell (blocked in the grid, walkable to the Navigator), 'D' the door.
		/// </summary>
		private string DoorsText() {
			var terrain = GetTerrain();
			if ( terrain == null || !terrain.IsLoaded ) return "no terrain" + NL;
			var doors = terrain.Doors;
			var sb = new StringBuilder();
			sb.AppendLine($"{doors.Count} doors (terrain generation {terrain.Generation})");
			// what AreaDoors.UpdateFromEntities sees: every entity with a TriggerableBlockage
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) || !ent.HasComponent("TriggerableBlockage") ) continue;
				var blockage = ent.GetComponent<TriggerableBlockage>();
				var pos = ent.GetComponent<Positioned>();
				sb.AppendLine($"  entity {ent.Id} {ent.Path}: blockage valid={IsValid(blockage)} closed={(IsValid(blockage) ? blockage.IsClosed.ToString() : "?")} box={(IsValid(blockage) ? $"{blockage.GridMin}..{blockage.GridMax}" : "?")} positioned valid={IsValid(pos)} at={(IsValid(pos) ? pos.GridPos.ToString() : "?")}");
			}
			foreach ( var d in doors ) {
				sb.AppendLine($"{d} box {d.GridMin}..{d.GridMax} exact={d.PassageIsExact}");
				var passage = new HashSet<long>(d.Passage.Select(c => c.Id));
				for ( int y = d.GridMin.Y - 1; y <= d.GridMax.Y + 1; y++ ) {
					var line = new StringBuilder("  ");
					for ( int x = d.GridMin.X - 1; x <= d.GridMax.X + 1; x++ ) {
						var c = new Offsets.Vector2i(x, y);
						line.Append(x == d.GridPos.X && y == d.GridPos.Y ? 'D' : passage.Contains(c.Id) ? 'P' : terrain.IsWalkable(c) ? '.' : '#');
					}
					sb.AppendLine(line.ToString());
				}
			}
			return sb.ToString();
		}

		private string TilesText(string filter) {
			var terrain = GetTerrain();
			if ( terrain == null || !terrain.IsLoaded ) return "(no terrain loaded)" + NL;
			var playerPos = GetPlayer()?.GetComponent<Positioned>();
			Offsets.Vector2i? here = IsValid(playerPos) ? playerPos.GridPos : (Offsets.Vector2i?)null;
			// (the straight-line distance from the player, in grid cells)
			float Dist(Offsets.Vector2i p) => here.HasValue ? Combat.Distance(here.Value, p) : float.NaN;
			var labRoom = Labyrinth.CurrentRoom();
			if ( labRoom != null ) Labyrinth.LearnFromExits(labRoom, terrain.Exits); // also names the room exits by the map's order
			var sb = new StringBuilder(labRoom == null ? "" : $"labyrinth room {labRoom}: {Labyrinth.NamingNote}" + NL);
			sb.Append($"area {GetCurrentArea()?.Name}, player at {(here.HasValue ? here.Value.ToString() : "?")}" + NL);
			foreach ( var e in terrain.Exits.OrderBy(e => Dist(e.GridPos)) ) sb.Append($"exit: {e}  d={Dist(e.GridPos):F0}  tile={e.TileFile}" + NL);
			var groups = terrain.AllTileGroups()
				.Where(g => string.IsNullOrEmpty(filter) || (g.TileFile?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
				.GroupBy(g => g.TileFile)
				.OrderBy(g => g.Count());
			foreach ( var file in groups ) {
				// (each group's nearest positions first)
				var nearest = file.Select(g => g.GridPos).OrderBy(Dist).ToList();
				// (count, then the distance to the nearest one)
				string dist = here.HasValue ? $"{Dist(nearest[0]),6:F0}" : "     ?";
				sb.Append($"{file.Count(),4}  {dist}  {file.Key}  at {string.Join(" ", nearest.Take(6))}{(file.Count() > 6 ? " ..." : "")}" + NL);
			}
			return sb.ToString();
		}

		private string RouteText(string to) {
			var sb = new StringBuilder();
			var here = GetCurrentArea();
			string Describe(WorldArea a) => $"{a.DisplayName} [{a.Id}]{(a.HasWaypoint ? " wp" : "")}{(a.IsTown ? " town" : "")}{(a.IsHub ? " hub" : "")}"
				+ $"{(WaypointPanel.IsKnownLocked(a.Name) ? " (locked)" : WaypointPanel.IsKnownUnlocked(a.Name) ? " (unlocked)" : "")}";
			sb.AppendLine($"here: {(here == null ? "?" : Describe(here))}");
			if ( here != null ) sb.AppendLine($"  connections: {string.Join("; ", here.Connections.Select(Describe))}");
			var labRoom = Labyrinth.CurrentRoom();
			if ( labRoom != null ) {
				var terrain = GetTerrain();
				if ( terrain != null ) Labyrinth.LearnFromExits(labRoom, terrain.Exits);
				sb.AppendLine("labyrinth rooms (* = here):");
				foreach ( var r in Labyrinth.Rooms() ) {
					sb.AppendLine($"  {(r == labRoom ? "*" : " ")} {r}  -> {string.Join("; ", r.Neighbours.Select(n => n.DisplayName))}");
				}
				if ( string.IsNullOrEmpty(to) ) return sb.ToString();
				var labRoute = Labyrinth.FindRoute(to);
				if ( labRoute == null ) { sb.AppendLine($"no labyrinth route to {to}"); return sb.ToString(); }
				sb.AppendLine($"route: {string.Join(" -> ", labRoute.Select(r => r.DisplayName))}");
				if ( terrain != null && labRoute.Count > 1 ) sb.AppendLine($"exit to take: {Labyrinth.ExitTo(labRoom, labRoute[1], terrain.Exits)?.ToString() ?? "not known yet"}");
				return sb.ToString();
			}
			if ( string.IsNullOrEmpty(to) ) return sb.ToString();
			var route = WorldArea.FindRoute(here, to);
			if ( route == null ) { sb.AppendLine($"no route to {to}"); return sb.ToString(); }
			foreach ( var a in route ) sb.AppendLine($"{Describe(a)}\n  connections: {string.Join("; ", a.Connections.Select(Describe))}");
			return sb.ToString();
		}

		private string WaypointsText() {
			bool open = WaypointPanel.Panel() != null;
			var sb = new StringBuilder(open ? "" : "(the waypoint panel is closed: read from its element tree; rects are not on screen)" + NL);
			int n = 0;
			foreach ( var (e, name) in open ? WaypointPanel.Destinations() : WaypointPanel.DestinationsEvenClosed() ) {
				sb.Append($"{name}  unlocked={WaypointPanel.IsUnlocked(e)}{(WaypointPanel.IsCurrentArea(e) ? " (you are here)" : "")} known-locked={WaypointPanel.IsKnownLocked(name)} element={e.Address} rect={e.GetClientRect()} visible={e.IsVisible}" + NL);
				n += 1;
			}
			return n == 0 ? sb.Append("(no destinations found)" + NL).ToString() : sb.ToString();
		}

		private string LabelsText() {
			var sb = new StringBuilder();
			var player = GetPlayer();
			var z = IsValid(player) ? Position(player).Z : 0;
			foreach ( var l in GetUI()?.LabelsOnGround?.GetAllLabels() ?? Enumerable.Empty<LabelOnGround>() ) {
				var e = l.Item;
				var pos = e?.GetComponent<Positioned>();
				string at = IsValid(pos) && TryGridToScreen(pos.GridPos, z, out var s) ? $"grid {pos.GridPos} screen ({s.X:F0}, {s.Y:F0})" : "";
				sb.AppendLine($"{e?.Id} {e?.Path} text=\"{Ground.LabelText(l.Label)}\" rect={l.Label.GetClientRect()} label={l.Label.Address} {at}");
			}
			return sb.Length == 0 ? "(no labels)" + NL : sb.ToString();
		}

		private string SkillBarText(bool stats) {
			var sb = new StringBuilder();
			foreach ( var slot in SkillBar.GetSlots() ) {
				sb.AppendLine($"{slot}  internal={slot.Skill?.InternalName} display={slot.Skill?.DisplayName} id={slot.Skill?.Id} visible={slot.IsVisible} useTime={SkillBar.CastTimeMs(slot.Skill)}ms tags={string.Join(",", SkillBar.SkillTags(slot.Skill))}");
				if ( stats && slot.Skill != null ) {
					foreach ( var s in slot.Skill.GetAllStats().OrderBy(s => s.Key.ToString()) ) sb.AppendLine($"    {s.Key} = {s.Value}");
				}
			}
			return sb.Length == 0 ? "(no skill bar)\n" : sb.ToString();
		}

		// the campaign progress scan (see CampaignProgress.Scan): it presses keys in the game (posted to its window when it is not focused) (opens and closes the
		// AreaInstanceUI), the one thing here that does; asked for by the user. Waits up to 10 s for it.
		private string ProgressScan(string keyName) {
			var key = Enum.TryParse(keyName ?? "U", true, out VKey k) ? k : VKey.U;
			var done = new ManualResetEventSlim(false);
			string started = RunOnMain(() => { Run(CampaignProgress.Scan(() => done.Set(), key)); return "ok"; });
			if ( started != "ok" ) return started;
			bool finished = done.Wait(10000);
			return RunOnMain(() => {
				var sb = new StringBuilder();
				if ( !finished ) sb.AppendLine("timed out (10 s) before the scan finished");
				sb.AppendLine($"progress: {CampaignProgress.Summary}");
				sb.AppendLine($"in: {GetCurrentArea()?.DisplayName} (act {CampaignProgress.CurrentAct})");
				sb.AppendLine($"act 1 done: {CampaignProgress.ActDone(1)}, act 2 done: {CampaignProgress.ActDone(2)}, part 1 done: {CampaignProgress.Part1Done}, campaign done: {CampaignProgress.CampaignDone}");
				sb.AppendLine($"parts (Child(2,0,0)): {string.Join(" | ", CampaignProgress.PartTexts)}");
				sb.AppendLine($"acts (under Child(2,0), starting with Act): {string.Join(" | ", CampaignProgress.ActTexts)}");
				// the quests, and what the campaign's Finish all quests (for the act the player is in, else the first) would do for each
				var finishers = (GetPlugin<AutomationPlugin>()?.Combat.Routines.OfType<CampaignRoutine>().FirstOrDefault()?.Root.All().OfType<FinishAllQuests>() ?? Enumerable.Empty<FinishAllQuests>()).ToList();
				var finisher = finishers.FirstOrDefault(f => f.Act == CampaignProgress.CurrentAct) ?? finishers.FirstOrDefault();
				var quests = Town.QuestNames();
				sb.AppendLine($"quests ({quests.Count}){(finisher == null ? "" : $", next steps as Finish all quests (Act {finisher.Act}) sees them")}:");
				foreach ( var quest in quests ) {
					sb.AppendLine($"  {quest}: {Town.QuestStep(quest)}");
					if ( finisher != null ) sb.AppendLine($"      next: {finisher.NextFor(quest)}");
				}
				return sb.ToString();
			});
		}

		private string RoutineText() {
			var driver = GetPlugin<AutomationPlugin>()?.Combat.Current;
			if ( !(driver is CampaignRoutine routine) ) return $"the routine is {driver?.Name ?? "(none)"}, which has no goals\n";
			var sb = new StringBuilder($"{routine.Name}: {(routine.IsActive ? "on" : "off")}, {routine.Status}\n");
			if ( !IsAlive(GetPlayer()) ) {
				var button = CampaignRoutine.FindResurrectButton();
				sb.AppendLine(button == null ? "dead: no resurrect button found" : $"dead: resurrect button at {button.GetClientRect()} ({button.Address})");
			}
			sb.AppendLine($"build: {BuildPlan.Problem() ?? $"starting skill {BuildPlan.StartingSkill()}, support {BuildPlan.StartingSupport()}, main skill {BuildPlan.MainSkill()}"}");
			sb.AppendLine($"quests: {string.Join("; ", Town.QuestNames().Select(q => $"{q}: {Town.QuestStep(q)}"))}");
			sb.AppendLine($"area: {GetCurrentArea()?.DisplayName}; progress: {CampaignProgress.Summary}; in act {CampaignProgress.CurrentAct}");
			var givers = GetEntities()?.Where(e => e?.Path?.StartsWith("Metadata/NPC/") ?? false).Select(e => $"{e.Path.Substring(e.Path.LastIndexOf('/') + 1)}{((e.GetComponent<NPC>()?.HasIconOverhead ?? false) ? " (!)" : "")}");
			sb.AppendLine($"npcs: {string.Join(", ", givers ?? Enumerable.Empty<string>())}");
			var rw = Town.QuestRewardWindow;
			sb.AppendLine($"windows: reward {(Town.IsOpen(rw) ? $"open at 0x{rw.Address.ToInt64():X} ({Town.QuestRewards().Count} rewards: {Town.RewardNames()})" : "closed")}, npc options {(Town.IsOpen(Town.NpcOptions) ? "open" : "closed")}, inventory {(BackpackIsOpen() ? "open" : "closed")}");
			var gemPlan = BuildPlan.Gems();
			if ( gemPlan != null ) {
				int level = GemPlan.CharacterLevel();
				sb.AppendLine($"gems (level {level}): socketed {string.Join(", ", GemArrangement.SocketedGems().Select(s => $"{s.slot}[{s.socket}]={Equipment.ItemName(s.gem)}"))}; backpack {string.Join(", ", BackpackItems().Where(i => i.Entity?.Path?.Contains("/Gems/") ?? false).Select(i => Equipment.ItemName(i.Entity)))}");
				sb.AppendLine($"  planned groups: {string.Join(" / ", gemPlan.GroupsFor(level).Select(g => g.ToString()))}");
				sb.AppendLine($"  solver: {string.Join(", ", gemPlan.SolveWorn(level).Placements.Select(p => p.ToString()))}");
				sb.AppendLine($"  moves to do: {string.Join(", ", GemArrangement.Targets(gemPlan, level).Select(t => $"{t.gem.Name} -> {t.slot}[{t.socket}]"))}");
				sb.AppendLine($"  misplaced: {string.Join(", ", GemArrangement.Misplaced(gemPlan, level).Select(m => $"{m.slot}[{m.socket}]={Equipment.ItemName(m.gem)}"))}");
				sb.AppendLine($"  next move: {InventoryPlans.PlanOneGemMove(gemPlan, level, null)?.Name ?? "(none)"}");
			}
			var treeSection = GetPlugin<AutomationPlugin>()?.PassiveTree;
			if ( treeSection?.Plan != null ) {
				var have = new HashSet<int>((PoEMemory.GameRoot?.InGameState?.AllocatedPassives ?? Array.Empty<ushort>()).Select(id => (int)id));
				var nextStep = treeSection.Plan.Next(have);
				sb.AppendLine($"passives: {GetUI()?.UnspentPassivePoints ?? 0} unspent; next: {(nextStep == null ? "(none)" : $"{nextStep.Node.Id} {nextStep} ({nextStep.Node.Kind}{(nextStep.MasteryEffect >= 0 ? $", effect {nextStep.MasteryEffect}" : "")})")}; allocator: {treeSection.Allocator.Status}");
			}
			sb.AppendLine($"goal: {routine.CurrentGoal?.Id ?? "(none)"}");
			foreach ( var s in routine.StackLines() ) sb.AppendLine($"  running: {s}");
			sb.AppendLine($"  explore: {routine.ExploreTarget?.ToString() ?? "(none)"}, {DefaultExplorer.ExploredFraction:P0} explored; walking: {DefaultFollower.Status} {DefaultFollower.Detail}");
			Sequence shown = null;
			foreach ( var (parent, goal, depth) in routine.Leaves() ) {
				if ( parent != shown ) { shown = parent; sb.AppendLine($"{new string(' ', depth * 2)}{parent.Name}{(parent.IsComplete ? " [x]" : "")}"); }
				string mark = goal.IsComplete ? "[x]" : goal == routine.CurrentGoal ? "[>]" : "[ ]";
				sb.AppendLine($"  {new string(' ', depth * 2)}{mark} {goal.Name}{(goal.Where == null ? "" : $" @ {goal.Where}")}  proves={goal.Proves ?? goal.ProvesEarlier} {goal.Detail}");
			}
			return sb.ToString();
		}

		/// <summary>The items on the visible stash page, with the screen rect a click on each aims at (StashItem.GetClientRect).</summary>
		private string StashText() {
			if ( !StashIsOpen() ) return "stash: closed" + NL;
			var rows = new List<string[]> { new[] { "name", "count", "x", "y", "w", "h", "path", "element" } };
			foreach ( var item in StashItems() ) {
				var e = item.Entity;
				var r = item.GetClientRect();
				var stack = e?.GetComponent<Stack>();
				rows.Add(new[] {
					e == null ? "?" : Equipment.ItemName(e) ?? "?",
					IsValid(stack) ? stack.CurSize.ToString() : "1",
					r.X.ToString("0.##"), r.Y.ToString("0.##"), r.Width.ToString("0.##"), r.Height.ToString("0.##"),
					e?.Path ?? "", $"0x{item.Address.ToInt64():X}",
				});
			}
			if ( rows.Count == 1 ) return "stash: open, no items showing" + NL;
			var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(row => row[c].Length)).ToArray();
			bool[] numeric = { false, true, true, true, true, true, false, false };
			var sb = new StringBuilder();
			foreach ( var row in rows )
				sb.AppendLine(string.Join("  ", row.Select((cell, c) => numeric[c] ? cell.PadLeft(widths[c]) : cell.PadRight(widths[c]))).TrimEnd());
			return sb.ToString();
		}

		/// <summary>The item held on the mouse cursor (ServerData.PlayerInventories, slot 13) and the backpack's server-side item count.</summary>
		private string CursorText() {
			var igs = PoEMemory.GameRoot?.InGameState;
			if ( igs == null ) return "error: not in game" + NL;
			var ent = igs.CursorItem;
			string path = ent == IntPtr.Zero ? "" : new Entity() { Address = ent }.Path;
			return $"cursor: {igs.ServerInventoryCount(Offsets.InventorySlot.Cursor)} item(s) entity=0x{ent.ToInt64():X} {path}" + NL
				+ $"backpack: {igs.ServerInventoryCount(Offsets.InventorySlot.MainInventory)} item(s)" + NL;
		}

		private string Backpack() {
			var sb = new StringBuilder();
			foreach ( var item in BackpackItems() ) {
				var ent = item.Entity;
				sb.AppendLine($"element=0x{item.Address.ToInt64():X} entity=0x{(ent?.Address ?? IntPtr.Zero).ToInt64():X} {ent?.Path}");
				foreach ( var kv in (ent?.GetComponents() ?? new Dictionary<string, IntPtr>()).OrderBy(k => k.Key) ) {
					sb.AppendLine($"    {kv.Key} = 0x{kv.Value.ToInt64():X}");
				}
			}
			return sb.Length == 0 ? "(none)\n" : sb.ToString();
		}

		private string Status() {
			var sb = new StringBuilder();
			var gr = PoEMemory.GameRoot;
			var igs = gr?.InGameState;
			sb.AppendLine($"attached pid={PoEMemory.Memory.Pid} base=0x{PoEMemory.Memory.BaseAddress.ToInt64():X} size=0x{PoEMemory.Memory.ModuleSize:X}");
			sb.AppendLine($"gameroot=0x{gr?.Address.ToInt64() ?? 0:X}");
			sb.AppendLine($"ingame=0x{igs?.Address.ToInt64() ?? 0:X} valid={IsValid(igs)}");
			sb.AppendLine($"player=0x{(IsValid(igs) ? igs.Player?.Address.ToInt64() ?? 0 : 0):X}");
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
				if ( p.StartsWith("Metadata/Monsters/") ) {
					var render = ent.GetComponent<Render>();
					var mods = ent.GetComponent<ObjectMagicProperties>();
					sb.Append($"  name=\"{(IsValid(render) ? render.Name : null)}\" rarity={(IsValid(mods) ? mods.Rarity.ToString() : "?")} at={ent.GetComponent<Positioned>()?.GridPos}");
				}
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
			var machine = ent.GetComponent<StateMachine>();
			if ( machine != null ) {
				foreach ( var (name, value) in machine.States ) sb.AppendLine($"state {name} = {value}");
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
				case "ui": { // ui(1,142,3): UIRoot.Children[1].Children[142].Children[3]; ui() is UIRoot
					Element e = igs?.UIRoot;
					foreach ( var a in args.Where(a => a.Length > 0) ) {
						e = IsValid(e) ? e.GetChild(ParseInt(a, -1)) : null;
					}
					addr = IsValid(e) ? e.Address.ToInt64() : 0;
					break;
				}
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

		/// <summary>
		/// /pathto: chains of pointers from the roots to within window bytes after target (eg, a struct whose address was
		/// found by /search), breadth first: each node's first len bytes are read, every 8-byte value that is a valid pointer
		/// is followed, to depth levels. Prints each chain as an address expression (eg, ingame+0x218*+0x9C8*+0x110), at
		/// most max. Read-only.
		/// </summary>
		private string PathTo(List<(string expr, long addr)> roots, long target, int window, int depth, int len, int max) {
			depth = Math.Max(1, Math.Min(depth, 5));
			len = Math.Max(8, Math.Min(len, 0x10000));
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var sb = new StringBuilder();
			var seen = new HashSet<long>();
			var level = roots.Select(r => (r.expr, r.addr)).ToList();
			foreach ( var r in roots ) seen.Add(r.addr);
			int found = 0, nodes = 0;
			const int MaxNodes = 300000;
			var buf = new byte[len];
			for ( int d = 0; d < depth && level.Count > 0 && found < max; d++ ) {
				var next = new List<(string expr, long addr)>();
				foreach ( var (expr, addr) in level ) {
					if ( ++nodes > MaxNodes || found >= max ) break;
					int got = PoEMemory.TryRead(new IntPtr(addr), buf);
					for ( int off = 0; off + 8 <= got; off += 8 ) {
						long v = BitConverter.ToInt64(buf, off);
						if ( !IsValid(new IntPtr(v)) ) continue;
						string here = $"{expr}+0x{off:X}*";
						if ( v >= target - window && v <= target ) {
							sb.AppendLine($"{here}{(v == target ? "" : $"+0x{target - v:X}")}   (depth {d + 1})");
							if ( ++found >= max ) break;
							continue;
						}
						if ( d + 1 < depth && seen.Add(v) ) next.Add((here, v));
					}
				}
				level = next;
			}
			return $"# {found} found, {nodes} nodes read in {watch.ElapsedMilliseconds} ms (depth {depth}, len 0x{len:X}, window 0x{window:X})\n" + sb;
		}

		/// <summary>
		/// /search: every place in the game's committed read-write memory where the bytes hex (eg, "4F000000430000",
		/// "??" for any byte) are, at addresses that are multiples of align; at most max. Read-only (it only reads).
		/// </summary>
		private string Search(string hex, int max, int align) {
			if ( string.IsNullOrWhiteSpace(hex) ) return "error: give hex=<bytes>, eg hex=4F00000043000000 (?? for any byte)\n";
			hex = hex.Replace(" ", "");
			if ( hex.Length % 2 != 0 ) return "error: hex needs an even number of digits\n";
			int n = hex.Length / 2;
			var pattern = new byte[n];
			var any = new bool[n];
			for ( int i = 0; i < n; i++ ) {
				string b = hex.Substring(i * 2, 2);
				if ( b == "??" ) { any[i] = true; continue; }
				if ( !byte.TryParse(b, System.Globalization.NumberStyles.HexNumber, null, out pattern[i]) ) return $"error: bad hex byte {b}\n";
			}
			align = Math.Max(1, align);
			max = Math.Max(1, Math.Min(max, 1000));
			var sb = new StringBuilder();
			int found = 0;
			long scanned = 0;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			const int Chunk = 1 << 20;
			foreach ( var region in PoEMemory.EnumerateAllocatedRanges() ) {
				long start = region.BaseAddress.ToInt64(), end = start + region.RegionSize;
				for ( long at = start; at < end && found < max; at += Chunk ) {
					int want = (int)Math.Min(Chunk + n - 1, end - at);
					if ( want < n ) break;
					var span = new byte[want];
					if ( PoEMemory.TryRead(new IntPtr(at), span) != want ) continue;
					scanned += want;
					int last = Math.Min(Chunk, want - n + 1);
					for ( int i = (int)((align - (at % align)) % align); i < last; i += align ) {
						bool match = true;
						for ( int j = 0; j < n; j++ ) {
							if ( !any[j] && span[i + j] != pattern[j] ) { match = false; break; }
						}
						if ( !match ) continue;
						sb.AppendLine($"0x{at + i:X}  (region 0x{start:X} +0x{at + i - start:X}, size 0x{region.RegionSize:X})");
						if ( ++found >= max ) break;
					}
				}
				if ( found >= max ) break;
			}
			return $"# {found} found{(found >= max ? " (max reached)" : "")}, {scanned / (1 << 20)} MB scanned in {watch.ElapsedMilliseconds} ms\n" + sb;
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
