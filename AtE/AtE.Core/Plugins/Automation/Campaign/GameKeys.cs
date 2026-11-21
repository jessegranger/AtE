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
	///
	/// TryReadFromMemory reads them without the panel (see Offsets.UIInput.Bindings: a list of { key, modifier, action }
	/// kept from the start of the game). TryRead tries that first, so the panel is only opened if it does not work.
	/// </summary>
	public static class GameKeys {
		private static Dictionary<string, string> bindings;

		/// <summary>True once the bindings were read (for the life of the overlay).</summary>
		public static bool Loaded => bindings != null;
		/// <summary>True once ReadGameKeys gave up on them (for the life of the overlay: not tried again).</summary>
		public static bool Unreadable { get; internal set; }

		/// <summary>
		/// The Options panel's "Input" tab, found by its label (the tab bar's child whose Child(0, 1) reads "Input"), and the
		/// body with the rows. 3.30.0d+: the bar Child(2, 0, 2, 0) has the tabs as children 6 ("Graphics", the first, on the
		/// left), 0 "Game", 1 "UI", 2 "Sound", 3 "Input", 4 "Notifications" (and 5, an empty strip): not in screen order.
		/// </summary>
		public static Element InputTab() => GetUI()?.OptionsPanel?.GetChild(2, 0, 2, 0)?.Children
			.FirstOrDefault(t => IsValid(t) && string.Equals(t.GetChild(0, 1)?.Text?.Trim(), "Input", StringComparison.OrdinalIgnoreCase));
		public static Element Body() => GetUI()?.OptionsPanel?.GetChild(2, 0, 2, 1);

		/// <summary>Where the bindings were read from, for the memory server's /gamekeys (null: not read).</summary>
		public static string Source { get; private set; }

		/// <summary>
		/// The actions by index (Offsets.UIInput.Bindings' action): the Input tab's rows in order, as read live (3.29.4)
		/// with the default and a few changed keys. Null for the ones that had no key, whose row's name is not known yet.
		/// </summary>
		public static readonly string[] BindingActions = {
			"Use flask in slot 1", "Use flask in slot 2", "Use flask in slot 3", "Use flask in slot 4", "Use flask in slot 5", // 0
			"Use Temp Bound Skill 1", "Use Temp Bound Skill 2", // 5
			"Use bound skill 1", "Use bound skill 2", "Use bound skill 3", "Use bound skill 4", "Use bound skill 5", // 7
			"Use bound skill 6", "Use bound skill 7", "Use bound skill 8", "Use bound skill 9", "Use bound skill 10", // 12
			"Use bound skill 11", "Use bound skill 12", "Use bound skill 13", // 17
			"Attack in place", "Weapon swap", null, "Highlight items and objects", "Toggle highlighting", // 20
			"Enable corpse targeting", "Chat", "Overlay Map", "Options", "Character Screen", // 25
			"Social Screen", "Ladder Screen", "Inventory", "Challenges/Achievements", "Pantheon Panel", // 30
			"Cosmetics", "Microtransaction List", null, "World Screen", "Passive Skill Tree", // 35
			"Atlas Screen", "Atlas Skill Tree", null, "Market Panel", "League Interface", // 40
			"League Panel", "Help Panel", "Close All User Interface", "Take a screenshot", "Zoom the camera in", // 45
			"Zoom the camera out", "Toggle Performance Metrics", null, "Toggle Skill Set", "Show Advanced Item Descriptions", // 50
			"Compare Item Descriptions", "End Delirium Encounter", "Use Alternate Currency", null, null, // 55
			null, null, null, "Use Sentinel", null, // 60
			"Return to the Mirage Portal", "Use Portal Scroll", null, "Pin Popups", // 65
		};

		/// <summary>
		/// Read the player's bindings from memory (Offsets.UIInput.Bindings: no input, and the Options panel need not have
		/// been shown); false if they can't be read, or do not look right (the vector's size and count disagree, an action
		/// index past BindingActions, or a key that is not one).
		/// </summary>
		public static bool TryReadFromMemory() {
			var root = PoEMemory.GameRoot?.InGameState?.UIRoot;
			if ( !IsValid(root) ) return false;
			if ( !PoEMemory.TryRead(root.Address + Offsets.UIRoot_Input, out IntPtr ptr) || !IsValid(ptr) ) return false;
			if ( !PoEMemory.TryRead(ptr, out Offsets.UIInput input) || !Offsets.IsValid(input.Bindings) ) return false;
			var list = new ArrayHandle<Offsets.KeyBindingEntry>(input.Bindings);
			int count = input.BindingCount;
			if ( count <= 0 || count > 500 || list.Length != count ) {
				LogOnce($"GameKeys: the key list does not look right (count {count}, {list.Length} entries): not read from memory (the game changed?)");
				return false;
			}
			var entries = list.ToArray(limit: 500);
			if ( entries.Length != count ) return false;
			var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			for ( int i = 0; i < count; i++ ) {
				int code = entries[i].Key, mod = entries[i].Modifier, action = entries[i].Action;
				if ( code <= 0 || code > 0xFF || action < 0 || action >= BindingActions.Length || (mod != 0 && (mod < 0x10 || mod > 0x12)) ) {
					LogOnce($"GameKeys: the key list's entry {i} does not look right (key {code}, modifier {mod}, action {action}): not read from memory (the game changed?)");
					return false;
				}
				if ( BindingActions[action] != null ) found[BindingActions[action]] = KeyText(code, mod);
			}
			bindings = found;
			Source = "memory (the UI root's key list)";
			Log($"GameKeys: read {found.Count} key bindings from memory (Use Portal Scroll = {Text("Use Portal Scroll") ?? "none"}, Inventory = {Text("Inventory") ?? "none"})");
			return true;
		}

		// (asked every frame until read: say why it can't be read once, not each time)
		private static string lastFailure;
		private static void LogOnce(string line) {
			if ( line == lastFailure ) return;
			lastFailure = line;
			Log(line);
		}

		// a key as text that Parse reads back: "Ctrl+Q", "I", "Oemcomma", "LButton"
		private static string KeyText(int code, int mod) {
			string m = mod switch { 0x10 => "Shift+", 0x11 => "Ctrl+", 0x12 => "Alt+", _ => "" };
			char c = (char)code;
			string k = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ? c.ToString() : ((VKey)code).ToString();
			return m + k;
		}

		/// <summary>
		/// Read the bindings now, with no input: from memory (TryReadFromMemory), else from the Options panel's rows; false
		/// if neither is there (the rows: the panel not built, or the tab not shown yet).
		/// </summary>
		public static bool TryRead() {
			if ( TryReadFromMemory() ) return true;
			return TryReadRows();
		}

		/// <summary>Read the Options panel's rows now (no input); false if none are there (the panel not built, or the tab not shown yet).</summary>
		public static bool TryReadRows() {
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
			Source = "the Options panel's rows";
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

		/// <summary>Every binding read (action, key text), for the memory server's /gamekeys; empty if not read.</summary>
		public static IEnumerable<KeyValuePair<string, string>> All() => bindings ?? Enumerable.Empty<KeyValuePair<string, string>>();

		/// <summary>
		/// The key that opens and closes the inventory, from the Options panel (the action named "Inventory Panel" or
		/// "Inventory", else the first one with "Inventory" in its name, and no "Stash" or "Guild"); VKey.I if the bindings
		/// are not read yet, or no such action is bound.
		/// </summary>
		public static VKey InventoryKey {
			get {
				string action = new[] { "Inventory Panel", "Inventory" }.FirstOrDefault(a => Text(a) != null)
					?? All().Select(kv => kv.Key).FirstOrDefault(a => a.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0
						&& a.IndexOf("Stash", StringComparison.OrdinalIgnoreCase) < 0 && a.IndexOf("Guild", StringComparison.OrdinalIgnoreCase) < 0);
				var key = action == null ? VKey.None : Key(action);
				return key == VKey.None ? VKey.I : key;
			}
		}

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
				var key = (ctx.Driver as GoalRoutine)?.OptionsKey?.Key ?? VKey.O;
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
