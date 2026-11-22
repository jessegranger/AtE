using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using static AtE.MappingRoutine;

namespace AtE {

	/// <summary>
	/// The memory server's /mapping/status page for a browser (a phone, mostly): dark, one screen. A strip coloured by the
	/// routine's state (running, paused, off, stopped, dead) with its status; chips for the overlay attached, the game
	/// running or paused, and focused; bars for life, energy shield, mana and XP; the area, coloured by its kind (hideout,
	/// map, town); the loop's steps as a timeline (now, then next); only the actions that make sense now; the log, folded.
	/// It refreshes itself every two seconds (in place, keeping what is unfolded).
	/// </summary>
	public static class StatusPageHtml {

		private static string Esc(string t) => WebUtility.HtmlEncode(t ?? "");

		private static string StateColor(RunState s) => s switch {
			RunState.Running => "var(--ok)",
			RunState.Paused => "var(--warn)",
			RunState.Stopped => "var(--bad)",
			RunState.Dead => "var(--bad)",
			_ => "var(--off)",
		};

		private static string AreaColor(AreaKind k) => k switch {
			AreaKind.Hideout => "var(--hideout)",
			AreaKind.Map => "var(--map)",
			AreaKind.Town => "var(--town)",
			_ => "var(--else)",
		};

		// a span of time, short: "45 s", "12 min", "3 h 05 min", "2 d 4 h"
		private static string Ago(TimeSpan t) =>
			t.TotalSeconds < 0 ? "0 s"
			: t.TotalMinutes < 1 ? $"{t.Seconds} s"
			: t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:D2} s"
			: t.TotalDays < 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min"
			: $"{(int)t.TotalDays} d {t.Hours} h";

		private static string Bar(string cls, int cur, int max, string left, string right) {
			double pct = max <= 0 ? 0 : Math.Clamp(100.0 * cur / max, 0, 100);
			return $"<div class=\"bar {cls}\"><i style=\"width:{pct:F1}%\"></i><b><span>{Esc(left)}</span><span>{Esc(right)}</span></b></div>";
		}

		private static string Chip(string color, string text) => $"<span class=chip><i style=\"background:{color}\"></i>{Esc(text)}</span>";

		private const string Css = @"
:root{color-scheme:dark;--bg:#0e0f12;--card:#17191e;--card2:#1f2229;--line:#2a2e37;--text:#e6e6e6;--dim:#8b909a;
--ok:#3fb950;--warn:#e3a62f;--bad:#f0524f;--off:#6b7280;--hideout:#2bb3a3;--map:#b58cff;--town:#5aa0ff;--else:#8b909a}
*{box-sizing:border-box}
html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.35 system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;-webkit-text-size-adjust:100%}
#app{max-width:520px;margin:0 auto;padding-bottom:84px}
.strip{padding:14px 16px 12px;color:#111}
.strip .k{display:flex;justify-content:space-between;align-items:baseline;gap:8px;font-weight:800;font-size:17px;letter-spacing:.03em}
.strip .k span{min-width:0;overflow-wrap:anywhere;font-size:12.5px;font-weight:600;opacity:.75;letter-spacing:0;text-align:right}
.strip .v{font-size:15px;font-weight:600;margin-top:4px;overflow-wrap:anywhere}
.strip .d{font-size:13px;opacity:.8;margin-top:2px;overflow-wrap:anywhere}
.msg{margin:10px 16px 0;padding:8px 12px;border-radius:10px;background:var(--card2);font-size:13px;color:var(--dim);overflow-wrap:anywhere}
.fail{margin:10px 16px 0;padding:10px 12px;border-radius:10px;background:color-mix(in srgb,var(--bad) 16%,var(--card));border-left:3px solid var(--bad);font-size:13.5px;overflow-wrap:anywhere}
.chips{display:flex;flex-wrap:wrap;gap:6px;padding:12px 16px 4px}
.chip{display:inline-flex;align-items:center;gap:6px;padding:4px 10px;border-radius:999px;background:var(--card2);font-size:12.5px;font-weight:600}
.chip i{width:8px;height:8px;border-radius:50%}
.who{display:flex;justify-content:space-between;align-items:baseline;padding:10px 16px 0}
.who b{font-size:16px}.who span{color:var(--dim);font-size:13px}
.bars{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:6px;padding:8px 16px 4px}
.bars>*{min-width:0}
.bars .full{grid-column:1/-1}
.bar{position:relative;height:22px;background:#25282f;border-radius:5px;overflow:hidden}
.bar>i{position:absolute;inset:0 auto 0 0}
.bar>b{position:absolute;inset:0;display:flex;align-items:center;justify-content:space-between;gap:6px;padding:0 8px;font-size:12px;font-weight:700;text-shadow:0 1px 2px #000;white-space:nowrap}
.bar>b span{overflow:hidden;text-overflow:ellipsis}
.hp>i{background:linear-gradient(#e14b4b,#a82828)}
.mana>i{background:linear-gradient(#4b82f0,#2a52b5)}
.es>i{background:linear-gradient(#a6e3ff,#58b6e6)}
.xp>i{background:linear-gradient(#f0c040,#b8861a)}
.explore{margin-top:8px}
.explore>i{background:linear-gradient(#4cc96a,#25913f)}
.card{margin:10px 16px 0;padding:12px 14px;border-radius:14px;background:var(--card)}
.area{border-left:4px solid}
.area .t{font-size:11.5px;font-weight:800;letter-spacing:.07em;text-transform:uppercase}
.area .n{font-size:16px;font-weight:600;margin-top:2px}
.facts{display:flex;flex-wrap:wrap;gap:4px 14px;color:var(--dim);font-size:13px;margin-top:4px}
.facts .good{color:var(--ok)}
.tl{display:flex;gap:10px;padding:4px 0;font-size:13.5px;color:var(--dim);overflow-wrap:anywhere}
.tl .d{width:10px;height:10px;border-radius:50%;margin-top:4px;flex:none;border:2px solid var(--line)}
.tl.now{color:var(--text)}
.tl.now .d{border-color:currentColor}
.h{font-size:11.5px;font-weight:800;letter-spacing:.07em;text-transform:uppercase;color:var(--dim);margin-bottom:4px}
details{margin:10px 16px 0;border-radius:14px;background:var(--card)}
summary{padding:12px 14px;cursor:pointer;color:var(--dim);font-size:13.5px}
pre{margin:0;padding:0 14px 12px;font:12px/1.45 ui-monospace,Consolas,monospace;color:#b9bdc6;white-space:pre-wrap;word-break:break-word}
.acts{position:fixed;left:0;right:0;bottom:0;background:rgba(14,15,18,.94);border-top:1px solid var(--line);padding:10px 16px calc(10px + env(safe-area-inset-bottom))}
.acts div{max-width:488px;margin:0 auto;display:flex;gap:8px}
.btn{flex:1;text-align:center;padding:13px 0;border-radius:11px;background:var(--card2);border:1px solid var(--line);color:var(--text);text-decoration:none;font-weight:700;font-size:15px}
.btn.go{background:#1f6f3a;border-color:#2a8a4a}
.btn.stop{background:#5a1f22;border-color:#7a2a2e}
.btn.pause{background:#6b4d12;border-color:#87621a}
";

		// refresh #app in place every 2 s (not while the page is hidden), keeping which <details> are open
		private const string Js = @"
(function(){
  var busy=false;
  function tick(){
    if(busy||document.hidden)return; busy=true;
    fetch('/mapping/status',{headers:{Accept:'text/html'},cache:'no-store'}).then(function(r){return r.text()}).then(function(t){
      var d=new DOMParser().parseFromString(t,'text/html'), n=d.getElementById('app'), app=document.getElementById('app');
      if(!n||!app)return;
      var open={}; app.querySelectorAll('details[data-k]').forEach(function(e){open[e.dataset.k]=e.open});
      n.querySelectorAll('details[data-k]').forEach(function(e){if(e.dataset.k in open)e.open=open[e.dataset.k]});
      app.innerHTML=n.innerHTML;
      if(location.search)history.replaceState(null,'','/mapping/status');
    }).catch(function(){}).finally(function(){busy=false});
  }
  setInterval(tick,2000);
})();";

		public static string Render(StatusView v, IEnumerable<string> log, string msg) {
			var sb = new StringBuilder();
			string stateWord = v.State switch {
				RunState.Running => "RUNNING", RunState.Paused => "PAUSED", RunState.Stopped => "STOPPED", RunState.Dead => "DEAD", _ => "OFF",
			};
			// the status "goal: detail" as a headline and a detail line
			string status = v.Status ?? "";
			int colon = status.IndexOf(": ", StringComparison.Ordinal);
			string head = colon > 0 ? status.Substring(0, colon) : status;
			string detail = colon > 0 ? status.Substring(colon + 2) : "";
			if ( v.State == RunState.Off && (head == "off" || head == "") ) { head = "Mapping is off"; detail = ""; }

			sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1,viewport-fit=cover\">")
				.Append("<meta name=theme-color content=\"#0e0f12\"><title>Mapping Status</title><style>").Append(Css).Append("</style></head><body><div id=app>");

			// the strip: state, routine and area; the status
			sb.Append($"<div class=strip style=\"background:{StateColor(v.State)}\"><div class=k>{stateWord}<span>Mapping{(v.Chosen ? "" : " (not chosen)")}{(v.AreaName != null ? $" · {Esc(v.AreaName)}" : "")}</span></div>")
				.Append($"<div class=v>{Esc(head)}</div>")
				.Append(detail.Length > 0 ? $"<div class=d>{Esc(detail)}</div>" : "")
				.Append("</div>");
			if ( !string.IsNullOrEmpty(msg) ) sb.Append($"<div class=msg>{Esc(msg)}</div>");
			if ( v.State == RunState.Stopped && v.Failure != null ) sb.Append($"<div class=fail><b>Stopped:</b> {Esc(v.Failure)}</div>");

			// the chips: attached, the game, focus
			sb.Append("<div class=chips>");
			sb.Append(v.Attached ? Chip("var(--ok)", "attached") : Chip("var(--bad)", "not attached"));
			if ( v.Attached ) {
				sb.Append(!v.GameReadable ? Chip("var(--bad)", "game not readable")
					: v.GamePaused ? Chip("var(--warn)", v.PausedForFocus ? "game paused (until focused)" : "game paused")
					: Chip("var(--ok)", "game running"));
				if ( !v.GameFocused ) sb.Append(Chip("var(--warn)", "not focused"));
				if ( v.AuguryOpen ) sb.Append(Chip("var(--map)", "augury window open"));
			}
			if ( v.SinceBuild is TimeSpan built ) sb.Append(Chip("var(--dim)", $"built {Ago(built)} ago"));
			sb.Append("</div>");

			// the character: life, ES, mana, XP
			if ( v.HasCharacter ) {
				sb.Append($"<div class=who><b>{Esc(v.CharName)}</b><span>level {v.Level}</span></div><div class=bars>");
				if ( v.MaxLife > 0 ) sb.Append(Bar("hp full", v.Life, v.MaxLife, $"Life {v.Life:N0} / {v.MaxLife:N0}", $"{100.0 * v.Life / v.MaxLife:F0}%"));
				if ( v.MaxES > 0 ) sb.Append(Bar("es", v.ES, v.MaxES, $"ES {v.ES:N0}", $"{100.0 * v.ES / v.MaxES:F0}%"));
				// (against the mana not reserved: with most of it reserved, the bar is full at the little left)
				int freeMana = Math.Max(0, v.MaxMana - v.ReservedMana);
				if ( v.MaxMana > 0 ) sb.Append(Bar(v.MaxES > 0 ? "mana" : "mana full", v.Mana, freeMana, $"Mana {v.Mana:N0} / {freeMana:N0}", v.ReservedMana > 0 ? $"{v.ReservedMana:N0} res" : $"{(freeMana > 0 ? 100.0 * v.Mana / freeMana : 0):F0}%"));
				if ( !double.IsNaN(v.XpPercent) ) sb.Append($"<div class=\"bar xp full\"><i style=\"width:{Math.Clamp(v.XpPercent, 0, 100):F1}%\"></i><b><span>XP · level {v.Level}</span><span>{v.XpPercent:F1}%</span></b></div>");
				sb.Append("</div>");
			} else sb.Append("<div class=who><span>character not readable</span></div>");

			// the area, coloured by its kind
			string kind = v.Area switch {
				AreaKind.Hideout => "Hideout", AreaKind.Map => v.MapTier is int t ? $"Map · tier {t}" : "Map", AreaKind.Town => "Town", AreaKind.Elsewhere => "Area", _ => "Area",
			};
			sb.Append($"<div class=\"card area\" style=\"border-color:{AreaColor(v.Area)}\"><div class=t style=\"color:{AreaColor(v.Area)}\">{kind}</div><div class=n>{Esc(v.AreaName ?? "(not readable)")}</div>");
			if ( !double.IsNaN(v.Explored) ) {
				double pct = Math.Clamp(v.Explored * 100, 0, 100);
				sb.Append($"<div class=\"bar explore\"><i style=\"width:{pct:F1}%\"></i><b><span>Explored</span><span>{pct:F0}%</span></b></div>");
			}
			sb.Append("<div class=facts>");
			if ( v.InArea is TimeSpan inArea ) sb.Append($"<span>{(v.AreaTimeIsMinimum ? "over " : "")}{Ago(inArea)} here</span>");
			if ( v.Area == AreaKind.Map ) {
				sb.Append($"<span>{(v.MonstersRemaining is int m ? $"{m}" : "50+")} monsters left (want &lt;{v.MonstersLeft})</span>");
				sb.Append(v.MapComplete ? "<span class=good>Map Complete</span>" : "<span>not complete</span>");
				if ( v.UniquesKilled > 0 ) sb.Append($"<span>{v.UniquesKilled} unique{(v.UniquesKilled == 1 ? "" : "s")} killed</span>");
			}
			sb.Append($"<span>{v.MapsCompleted} done · {v.MapsRun} entered</span><span>next: {Esc(v.NextMap)}</span>");
			sb.Append("</div></div>");

			// the loop's steps: now, then next
			if ( v.Steps.Count > 0 || v.Skipping.Count > 0 ) {
				sb.Append("<div class=card><div class=h>Steps</div>");
				for ( int i = 0; i < v.Steps.Count; i++ ) {
					sb.Append(i == 0
						? $"<div class=\"tl now\" style=\"color:{StateColor(v.State)}\"><i class=d></i><span style=\"color:var(--text)\">{Esc(v.Steps[i])}</span></div>"
						: $"<div class=tl><i class=d></i><span>{Esc(v.Steps[i])}</span></div>");
				}
				foreach ( var s in v.Skipping ) sb.Append($"<div class=facts>{Esc(s)}</div>");
				sb.Append("</div>");
			}

			// the log, folded
			var lines = log?.ToList() ?? new List<string>();
			sb.Append($"<details data-k=log><summary>Log ({lines.Count} lines)</summary><pre>{Esc(string.Join("\n", lines))}</pre></details>");

			// the actions that make sense now
			var acts = new List<string>();
			string A(string action, string cls, string text) => $"<a class=\"btn {cls}\" href=\"/mapping/{action}\">{text}</a>";
			switch ( v.State ) {
				case RunState.Running: acts.Add(A("pause", "pause", "Pause")); acts.Add(A("stop", "stop", "Stop")); break;
				case RunState.Paused: acts.Add(A("resume", "go", "Resume")); acts.Add(A("stop", "stop", "Stop")); break;
				case RunState.Dead: acts.Add(A("stop", "stop", "Stop")); break;
				default: acts.Add(A("start", "go", "Start")); break;
			}
			sb.Append($"<div class=acts><div>{string.Join("", acts)}</div></div>");

			sb.Append("</div><script>").Append(Js).Append("</script></body></html>");
			return sb.ToString();
		}
	}
}
