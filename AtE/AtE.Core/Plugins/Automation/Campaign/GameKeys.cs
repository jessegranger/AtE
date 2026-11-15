using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The game's own key bindings (eg, "Use Portal Scroll" = ","), read from the Options panel's "Input" tab and kept
	/// for as long as the overlay runs (they are rarely changed). The tab's rows stay in memory once the panel was
	/// built, open or not: read from there first (TryRead); if they are not there, ReadGameKeys opens the panel once.
	///
	/// 3.29.4, seen live: OptionsPanel.Child(2, 0, 2, 0, 6) is the "Input" tab, and the body, Child(2, 0, 2, 1), holds
	/// the rows: each with the action at Child(0, 0) (eg "Use Portal Scroll") and its key at Child(1, 0, 0, 0, 0, 1)
	/// (eg ",", "O", "1").
	/// </summary>
	public static class GameKeys {
		private static Dictionary<string, string> bindings;

		/// <summary>True once the bindings were read (for the life of the overlay).</summary>
		public static bool Loaded => bindings != null;
		/// <summary>True once ReadGameKeys gave up on them (for the life of the overlay: not tried again).</summary>
		public static bool Unreadable { get; internal set; }

		/// <summary>The Options panel's "Input" tab, and the body with the rows.</summary>
		public static Element InputTab() => GetUI()?.OptionsPanel?.GetChild(2, 0, 2, 0, 6);
		public static Element Body() => GetUI()?.OptionsPanel?.GetChild(2, 0, 2, 1);

		/// <summary>Read the rows now (no input); false if none are there (the panel not built, or the tab not shown yet).</summary>
		public static bool TryRead() {
			var body = Body();
			if ( !IsValid(body) ) return false;
			var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach ( var row in All(body, 14, 20000) ) {
				string action = row.GetChild(0, 0)?.Text?.Trim();
				if ( string.IsNullOrEmpty(action) ) continue;
				string key = row.GetChild(1, 0, 0, 0, 0, 1)?.Text?.Trim();
				if ( string.IsNullOrEmpty(key) ) continue;
				found[action] = key;
			}
			if ( !found.ContainsKey("Use Portal Scroll") ) return false;
			bindings = found;
			Log($"GameKeys: read {found.Count} key bindings (Use Portal Scroll = {Text("Use Portal Scroll")}, Options = {Text("Options")})");
			return true;
		}

		// every element under root (visible or not), depth first
		private static IEnumerable<Element> All(Element root, int maxDepth, int max) {
			var stack = new Stack<(Element e, int depth)>();
			stack.Push((root, 0));
			int n = 0;
			while ( stack.Count > 0 && n++ < max ) {
				var (e, depth) = stack.Pop();
				if ( !IsValid(e) ) continue;
				yield return e;
				if ( depth >= maxDepth ) continue;
				foreach ( var c in e.Children.Reverse() ) if ( c != null ) stack.Push((c, depth + 1));
			}
		}

		/// <summary>The key text an action is bound to (eg, ","), or null.</summary>
		public static string Text(string action) => bindings != null && bindings.TryGetValue(action, out string t) ? t : null;

		/// <summary>The key an action is bound to (with modifier bits, eg Ctrl + Q), or VKey.None (not read, not bound, or not understood).</summary>
		public static VKey Key(string action) => Parse(Text(action));

		/// <summary>A key as the Options panel writes it (eg, ",", "O", "1", "F5", "Ctrl+Q"), or VKey.None.</summary>
		public static VKey Parse(string text) {
			if ( string.IsNullOrWhiteSpace(text) ) return VKey.None;
			VKey mods = VKey.None;
			var parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
			if ( parts.Count == 0 ) return text.Trim() == "+" ? VKey.Oemplus : VKey.None;
			if ( text.Trim().EndsWith("+") ) parts.Add("+"); // (eg, "Ctrl++")
			foreach ( var m in parts.Take(parts.Count - 1) ) {
				switch ( m.ToLowerInvariant() ) {
					case "ctrl": case "control": mods |= VKey.Control; break;
					case "shift": mods |= VKey.Shift; break;
					case "alt": mods |= VKey.Alt; break;
					default: return VKey.None;
				}
			}
			string k = parts.Last();
			VKey code = VKey.None;
			if ( k.Length == 1 ) {
				char c = char.ToUpperInvariant(k[0]);
				if ( (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ) code = (VKey)c;
				else code = c switch {
					',' => VKey.Oemcomma, '.' => VKey.OemPeriod, '/' => VKey.OemQuestion, ';' => VKey.OemSemicolon,
					'\'' => VKey.OemQuotes, '[' => VKey.OemOpenBrackets, ']' => VKey.OemCloseBrackets, '\\' => VKey.OemPipe,
					'-' => VKey.OemMinus, '=' => VKey.Oemplus, '+' => VKey.Oemplus, '`' => VKey.Oemtilde,
					_ => VKey.None,
				};
			} else if ( Enum.TryParse(k, true, out VKey named) ) {
				code = named; // eg, "F5", "Space", "Tab"
			}
			return code == VKey.None ? VKey.None : code | mods;
		}

		/// <summary>Press a key with its modifier bits (each modifier held around it).</summary>
		public static IState Press(VKey key, IState next) {
			VKey code = key & VKey.KeyCode;
			IState chain = Inputs.Press(code, null);
			var tail = chain.Tail();
			IState last = tail;
			void Wrap(VKey flag, VKey modifier) {
				if ( (key & flag) == 0 ) return;
				var up = new KeyUp(modifier, null);
				last.Next = new Delay(Inputs.Latency, up);
				last = up;
				chain = new KeyDown(modifier, new Delay(Inputs.Latency, chain));
			}
			Wrap(VKey.Control, VKey.LControlKey);
			Wrap(VKey.Shift, VKey.LShiftKey);
			Wrap(VKey.Alt, VKey.LMenu);
			last.Next = next;
			return chain;
		}
	}

	/// <summary>
	/// Read the game's key bindings (see GameKeys), once for the life of the overlay: from memory if the Options panel's
	/// rows are there; else open the panel (the routine's OptionsKey), click its "Input" tab, read them, and close it.
	/// Done when read, or after a few tries (then they are left unread: the caller does without).
	/// </summary>
	public class ReadGameKeys : Goal {
		private long lastAction;
		private int tries;
		private bool opened;
		public override string Name => "Read the game's key bindings (Options, Input)";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); tries = 0; opened = false; }

		private static bool PanelOpen => GetUI()?.OptionsPanel?.IsVisible ?? false;

		public override bool CheckDone(CombatContext ctx) => (GameKeys.Loaded || GameKeys.Unreadable) && !(opened && PanelOpen);

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			ctx.Driver.StopWalking("reading the key bindings");
			if ( now - lastAction < 700 ) return this;
			if ( GameKeys.Loaded || GameKeys.Unreadable || GameKeys.TryRead() ) {
				// (close the panel, if it opened it)
				if ( opened && PanelOpen ) { lastAction = now; Detail = "closing the Options"; return Inputs.Press(VKey.Escape, this); }
				return this;
			}
			if ( ++tries > 4 ) {
				GameKeys.Unreadable = true;
				Log("GameKeys: could not read the key bindings from the Options panel: going on without them");
				return this;
			}
			lastAction = now;
			if ( !PanelOpen ) {
				var key = (ctx.Driver as CampaignRoutine)?.OptionsKey?.Key ?? VKey.O;
				Detail = $"opening the Options ({key})";
				opened = true;
				return GameKeys.Press(key, this);
			}
			var tab = GameKeys.InputTab();
			if ( !IsValid(tab) ) { Detail = "no Input tab"; return this; }
			Detail = "clicking the Input tab";
			return Inputs.Click(tab.GetClientRect(), this);
		}
	}
}
