using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The UI half of the script: every InGameState_UIElements pointer found, the always-visible elements and the flasks (fresh
	/// character goldens), the keys that open and close panels (read from the game's config), the mouse and what it hovers, and the
	/// map. A key check opens something, waits for it to show, closes it and waits until the UI is as it was, so the next check
	/// starts from the same screen. When the element it should have toggled did not, it says which pointers did change.
	/// </summary>
	public static partial class OffsetChecks {

		private static readonly FieldInfo[] uiFields = typeof(Offsets.InGameState_UIElements).GetFields(BindingFlags.Public | BindingFlags.Instance)
			.Where(f => f.FieldType == typeof(IntPtr)).ToArray();

		private static string[] UICovers() => uiFields.Select(f => $"InGameState_UIElements.{f.Name}").ToArray();

		private static Dictionary<string, IntPtr> UIPointers() {
			var ui = GetUI();
			var result = new Dictionary<string, IntPtr>();
			if ( ui == null ) return result;
			object boxed = ui.Cache;
			foreach ( var f in uiFields ) result[f.Name] = (IntPtr)f.GetValue(boxed);
			return result;
		}

		private static IntPtr UIPointer(string name) => UIPointers().TryGetValue(name, out IntPtr p) ? p : IntPtr.Zero;

		private static Element ElementAt(IntPtr p) => p != IntPtr.Zero && ElementCache.TryGetElement(p, out Element e) && e.IsValid ? e : null;

		private static Element UIElement(string name) => ElementAt(UIPointer(name));

		private static bool IsShown(IntPtr p) => ElementAt(p)?.IsVisible ?? false;

		/// <summary>The names of the UIElements pointers whose element is on the screen.</summary>
		private static HashSet<string> ShownNow() => new HashSet<string>(UIPointers().Where(p => IsShown(p.Value)).Select(p => p.Key));

		private static void Press(VKey key) {
			Platform.Current.Input.KeyDown(key);
			Platform.Current.Input.KeyUp(key);
		}

		private static readonly string configPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile", "production_Config.ini");

		/// <summary>The key the game's config binds to an action (eg, "open_inventory_panel=73" is I); the default if it is unreadable or has a modifier.</summary>
		private static VKey ConfigKey(string action, VKey fallback) {
			try {
				foreach ( var line in File.ReadAllLines(configPath) ) {
					if ( !line.StartsWith(action + "=") ) continue;
					var values = line.Substring(action.Length + 1).Trim().Split(' ');
					if ( int.TryParse(values[0], out int code) && code > 0 && (values.Length < 2 || values[1] == "0") ) return (VKey)code;
				}
			} catch ( Exception e ) { Log($"OffsetChecks: could not read {configPath}: {e.Message}"); }
			return fallback;
		}

		private static string Joined(IEnumerable<string> names) => string.Join(",", names.OrderBy(n => n));

		// ---------------------------------------------------------------- A6: the UI elements

		private static IEnumerable<OffsetCheck> UI() {
			yield return Check("A6.UIElements", UICovers().Concat(new[] { "Element.Width", "Element.Height", "Element.X", "Element.Y" }).ToArray(), c => {
				var game = c.Game;
				var root = game.UIRoot;
				if ( !IsValid(root) ) return Outcome.Fail("no UI root");
				var pointers = UIPointers();
				var nulls = new List<string>();
				var shown = new List<string>();
				foreach ( var pair in pointers ) {
					if ( pair.Value == IntPtr.Zero ) { nulls.Add(pair.Key); continue; }
					var e = ElementAt(pair.Value);
					if ( e == null ) { c.That(false, $"{pair.Key} {pair.Value} is not an Element"); continue; }
					var chain = e;
					int hops = 0;
					while ( chain != null && chain.Address != root.Address && hops++ < 40 ) chain = chain.Parent;
					c.That(chain != null && chain.Address == root.Address, $"{pair.Key} is not under the UI root");
					c.That(float.IsFinite(e.Scale) && e.Scale > 0.05f && e.Scale < 10f, $"{pair.Key} Scale is {e.Scale}");
					c.That(float.IsFinite(e.Size.X) && float.IsFinite(e.Size.Y) && e.Size.X >= 0 && e.Size.Y >= 0 && e.Size.X < 20000 && e.Size.Y < 20000, $"{pair.Key} Size is {e.Size}");
					c.That(float.IsFinite(e.Position.X) && float.IsFinite(e.Position.Y), $"{pair.Key} Position is {e.Position}");
					if ( e.IsVisible ) shown.Add(pair.Key);
				}
				c.Note($"{pointers.Count - nulls.Count} of {pointers.Count} found");
				c.Golden("ui.null", Joined(nulls));
				c.Golden("ui.visible", Joined(shown));
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });

			yield return Check("A6.AlwaysVisible", new[] { "InGameState_UIElements.LifeBubble", "InGameState_UIElements.ManaBubble", "InGameState_UIElements.Flasks", "Element.Position", "Element.Size", "Element.Scale", "Element.IsVisibleByte", "Element.elemParent" }, c => {
				var size = WindowSize.Value;
				var rects = new Dictionary<string, RectangleF>();
				foreach ( var name in new[] { "LifeBubble", "ManaBubble", "Flasks" } ) {
					var e = UIElement(name);
					if ( e == null ) { c.That(false, $"{name} is not an Element"); continue; }
					c.That(e.IsVisible, $"{name} is not visible");
					c.Golden($"ui.{name}.size", e.Size);
					c.Golden($"ui.{name}.scale", e.Scale);
					var rect = e.GetClientRect();
					rects[name] = rect;
					c.That(rect.Left >= -1 && rect.Top >= -1 && rect.Right <= size.X + 1 && rect.Bottom <= size.Y + 1, $"{name} is at {rect}, outside the {size.X}x{size.Y} window");
					c.That(rect.Top > size.Y * 0.6f, $"{name} is not in the bottom of the window ({rect})");
				}
				if ( rects.Count == 3 ) {
					float life = rects["LifeBubble"].X + (rects["LifeBubble"].Width / 2), mana = rects["ManaBubble"].X + (rects["ManaBubble"].Width / 2), flasks = rects["Flasks"].X + (rects["Flasks"].Width / 2);
					c.That(life < flasks && flasks < mana, $"the bubbles and flasks are not left to right: life {life:0}, flasks {flasks:0}, mana {mana:0}");
				}
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });

			// 2 life flasks, 2 empty slots, 1 mana flask on a fresh character
			yield return Check("A6.Flasks", new[] { "InGameState_UIElements.Flasks", "Element_InventoryItem.entItem", "Component_Charges.NumCharges", "Component_Charges.ptrToChargeDetails", "ChargeDetails.Max", "ChargeDetails.PerUse", "Component_Charges.entOwner", "Component_Flask.entOwner" }, c => {
				var panel = GetUI()?.Flasks;
				if ( !IsValid(panel) ) return Outcome.Fail("no flask panel");
				var layout = new List<string>();
				var charges = new List<string>();
				int life = 0, mana = 0, empty = 0;
				for ( int i = 0; i < 5; i++ ) {
					var ent = panel.GetFlask(i)?.Entity;
					if ( !IsValid(ent) ) { empty++; layout.Add("-"); charges.Add("-"); continue; }
					string path = ent.realEnt.Path ?? "?";
					string kind = path.Substring(path.LastIndexOf('/') + 1);
					if ( path.Contains("FlaskLife") ) life++;
					else if ( path.Contains("FlaskMana") ) mana++;
					layout.Add(kind);
					Owners(c, ent.realEnt, "Charges", "Flask");
					var ch = ent.realEnt.GetComponent<Charges>();
					charges.Add(IsValid(ch) ? $"{ch.Current}/{ch.Max}/{ch.PerUse}" : "no Charges");
					c.That(IsValid(ch) && ch.Max > 0 && ch.PerUse > 0 && ch.Current >= 0 && ch.Current <= ch.Max, $"flask {i} charges {charges.Last()} are not sane");
				}
				c.Eq("life flasks", life, 2);
				c.Eq("mana flasks", mana, 1);
				c.Eq("empty slots", empty, 2);
				c.Golden("flasks.layout", string.Join(",", layout));
				c.Golden("flasks.charges", string.Join(",", charges));
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });

			// the equipment slots of the inventory panel: the ones that matter are Elements, and which fields are Elements is a golden (the stale
			// fields past the real slots read as garbage that changes, so what they hold is only noted)
			yield return Check("A6.Inventory", new[] { "InGameState_UIElements.InventoryPanel", "Element_InventoryRoot.InventoryList", "InventoryArray.*" }, c => {
				var panel = GetUI()?.InventoryPanel;
				if ( !IsValid(panel) ) return Outcome.Fail("no inventory panel");
				object boxed = panel.Inventories.Value;
				var elements = new List<string>();
				var nulls = new List<string>();
				var other = new List<string>();
				foreach ( var f in typeof(Offsets.InventoryArray).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(IntPtr)) ) {
					var ptr = (IntPtr)f.GetValue(boxed);
					if ( ptr == IntPtr.Zero ) nulls.Add(f.Name);
					else if ( ElementAt(ptr) != null ) elements.Add(f.Name);
					else other.Add(f.Name);
				}
				foreach ( var slot in new[] { "Backpack", "Helm", "Amulet", "Chest", "LWeapon", "RWeapon", "LRing", "RRing", "Belt", "Boots", "Gloves" } ) {
					c.That(elements.Contains(slot), $"the {slot} slot is not an Element ({(nulls.Contains(slot) ? "empty" : "something else")})");
				}
				c.Note($"{elements.Count} slots are Elements, {nulls.Count} empty, {other.Count} hold something else");
				// only the Elements are a golden: past the real slots the struct reads whatever is in memory, which differs from run to run
				c.Golden("inventory.elements", Joined(elements));
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });

			// the skill bar: 13 slots, each with the key the game's config binds, and Default Attack on the left mouse button
			yield return Check("A6.SkillBar", new[] { "InGameState_UIElements.SkillBar", "Offsets.Element_SkillCell_Skill", "Offsets.Element_SkillCell_SkillId", "Offsets.Element_SkillKeyLabel_VKey" }, c => {
				var slots = SkillBar.GetSlots();
				c.Eq("slots", slots.Count, SkillBar.SlotCount);
				var config = new Dictionary<int, int>();
				try {
					foreach ( var line in File.ReadAllLines(configPath).Where(l => l.StartsWith("use_bound_skill")) ) {
						var parts = line.Substring("use_bound_skill".Length).Split('=');
						if ( parts.Length == 2 && int.TryParse(parts[0], out int n) && int.TryParse(parts[1].Trim().Split(' ')[0], out int code) ) config[n - 1] = code;
					}
				} catch ( Exception ) { }
				foreach ( var slot in slots ) {
					if ( config.TryGetValue(slot.Index, out int code) ) c.That((int)slot.KeyCode == code, $"slot {slot.Index} says key {(int)slot.KeyCode}, the config binds {code}");
				}
				var first = slots.FirstOrDefault();
				c.That(first != null && first.Skill != null && first.Skill.Id == 0x4000, $"slot 0 holds {(first?.Skill == null ? "nothing" : $"skill {first.Skill.Id:X}")}, expected Default Attack (0x4000)");
				if ( first?.Element != null && PoEMemory.TryRead(first.Element.Address + Offsets.Element_SkillCell_SkillId, out ushort id) ) c.Eq("slot 0 SkillId", (int)id, 0x4000);
				c.Golden("skillbar.slots", string.Join(",", slots.Select(s => $"{s.Index}:{(int)s.KeyCode}:{(s.Skill == null ? "-" : SkillBar.SkillName(s.Skill))}")));
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });

			// the hidden skill bar, the list of skills to choose from, only has its cells while it is open: click an empty slot to open it, read
			// the cells (Default Attack, Movement and Clear are always there), then Escape
			{
				Rectangle slotRect = Rectangle.Empty;
				long lastClick = 0;
				int clicks = 0;
				var chooser = Check("A6.SkillChooser", new[] { "InGameState_UIElements.HiddenSkillBar", "Offsets.Element_SkillCell_Skill", "Offsets.Element_SkillCell_Icon", "Offsets.SkillCellIcon_Name" }, c => {
					int phase = c.Scratch.TryGetValue("phase", out object p) ? (int)p : 0;
					if ( phase == 0 ) {
						var available = SkillBar.IsChoosing ? SkillBar.GetAvailable() : new List<AvailableSkill>();
						if ( available.Count == 0 ) {
							long now = Time.ElapsedMilliseconds;
							if ( clicks < 4 && now - lastClick >= (clicks == 0 ? 250 : 1500) && slotRect.Width > 0 ) {
								SkillBar.ClickAt(new RectangleF(slotRect.X, slotRect.Y, slotRect.Width, slotRect.Height));
								clicks++;
								lastClick = now;
							}
							return Outcome.NotYet("waiting for the skill list to open");
						}
						c.That(available.Any(a => a.IsMovement), "the skill list has no Movement cell (Element_SkillCell_Icon / SkillCellIcon_Name)");
						c.That(available.Any(a => a.IsClear), "the skill list has no Clear cell");
						c.That(available.Any(a => a.Skill != null && a.Skill.Id == 0x4000), "the skill list has no Default Attack cell");
						c.Golden("skillbar.available", string.Join(",", available.Select(a => a.Name)));
						c.Scratch["failure"] = c.Result();
						c.Scratch["phase"] = 1;
						c.Scratch["pressedAt"] = c.Elapsed;
						Press(VKey.Escape);
						return Outcome.NotYet("closing the skill list");
					}
					if ( SkillBar.IsChoosing ) {
						if ( c.Elapsed - (long)c.Scratch["pressedAt"] > 1500 ) { c.Scratch["pressedAt"] = c.Elapsed; Press(VKey.Escape); }
						return Outcome.NotYet("waiting for the skill list to close");
					}
					return (Outcome)c.Scratch["failure"];
				}, new[] { "A6.SkillBar" }, timeoutMs: 12000);
				chooser.Setup = () => {
					// an empty slot (Q, the 4th): opening the list for it changes nothing, and Escape closes it
					var slot = SkillBar.GetSlot(3);
					if ( slot == null ) return;
					var r = slot.Rect;
					slotRect = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
					SkillBar.MouseTo(new Vector2(r.X + (r.Width / 2), r.Y + (r.Height / 2)));
					lastClick = Time.ElapsedMilliseconds;
					clicks = 0;
				};
				chooser.Teardown = () => { if ( SkillBar.IsChoosing ) Press(VKey.Escape); };
				yield return chooser;
			}

			// the clock shown in the corner (show_clock in the game's config): the text of an element
			yield return Check("A6.ElementText", new[] { "Offsets.Element_Text", "InGameState_UIElements.CurrentTime" }, c => {
				var clock = UIElement("CurrentTime");
				if ( clock == null ) return Outcome.Fail("CurrentTime is not an Element");
				string text = clock.Children.Select(ch => ch?.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? clock.Text;
				c.That(text != null && System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^\d{1,2}:\d{2}"), $"the clock reads \"{text}\", not a time");
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });
		}

		// the small map is what a new character starts with: in a corner of the screen, with a zoom, and shifted to keep the player in the middle
		private static IEnumerable<OffsetCheck> MiniMapAtRest() {
			yield return Check("A6.MiniMap", new[] { "InGameState_UIElements.Map", "Element_SubMap.Shift", "Element_SubMap.DefaultShift", "Element_SubMap.Zoom" }, c => {
				var map = GetUI()?.Map;
				if ( !IsValid(map) || !IsValid(map.MiniMap) || !IsValid(map.LargeMap) ) return Outcome.Fail("the Map element has no large and small map children");
				c.That(map.MiniMap.IsVisible, "the small map is not visible");
				c.That(!map.LargeMap.IsVisible, "the large map is visible");
				var size = WindowSize.Value;
				var rect = map.MiniMap.GetClientRect();
				c.That(rect.Width > 20 && rect.Height > 20 && rect.Width < size.X * 0.4f && rect.Left > size.X * 0.5f, $"the small map is at {rect}, not a corner of the screen");
				c.InRange("Zoom", map.MiniMap.Zoom, 0.4, 1.6);
				c.That(float.IsFinite(map.MiniMap.Shift.X) && float.IsFinite(map.MiniMap.Shift.Y), $"Shift {map.MiniMap.Shift} is not finite");
				c.Golden("map.miniSize", map.MiniMap.Size);
				c.Golden("map.miniDefaultShift", map.MiniMap.DefaultShift);
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });
		}

		// ---------------------------------------------------------------- A7: keys

		private class Toggle {
			public string Id, Action, TargetName;
			public VKey Fallback;
			public string[] Covers;
			/// <summary>What the target is like now (eg, "shown"); it must differ after the key, and be the same again after the second press.</summary>
			public Func<string> State;
			/// <summary>After the key: null if all is as it should be, else what is wrong.</summary>
			public Func<TestContext, string> Verify;
			public Action<TestContext> OnOpen;
			/// <summary>
			/// Looked at while it is open, before the second key closes it: called each frame until it passes or fails (NotYet: not ready, ask again,
			/// for up to HoldMs). It uses its own context, so it returns that context's Result when it is done.
			/// </summary>
			public Func<TestContext, Outcome> While;
			public int HoldMs = 8000;
			public int TimeoutMs = 6000;
		}

		private static OffsetCheck KeyToggle(Toggle t) {
			string before = null;
			HashSet<string> shownBefore = null;
			VKey key = VKey.None;
			var check = Check(t.Id, t.Covers, c => {
				// phase 0: wait for it to open; phase 1: after the second press, wait for the UI to be as it was
				int phase = c.Scratch.TryGetValue("phase", out object p) ? (int)p : 0;
				var shownNow = ShownNow();
				if ( phase == 0 ) {
					string now = t.State();
					if ( now == before ) {
						if ( c.Elapsed < 1500 ) return Outcome.NotYet($"waiting for {t.Id} to change after {key}");
						var changed = shownNow.Except(shownBefore).Select(n => "+" + n).Concat(shownBefore.Except(shownNow).Select(n => "-" + n));
						c.Scratch["failure"] = $"pressing {key} did not change {t.TargetName} ({now}); the pointers that did change: {(changed.Any() ? Joined(changed) : "none")}";
					} else {
						if ( !c.Scratch.ContainsKey("opened") ) {
							c.Scratch["opened"] = c.Elapsed;
							string wrong = t.Verify?.Invoke(c);
							if ( wrong != null ) c.Scratch["failure"] = wrong;
							var extra = shownNow.Except(shownBefore).Where(n => n != t.TargetName).ToList();
							c.Scratch["note"] = $"{key} toggled {t.TargetName} ({before} -> {now}){(extra.Count > 0 ? $", also showed {Joined(extra)}" : "")}";
							t.OnOpen?.Invoke(c);
						}
						// while it is open: whatever there is to look at in it
						if ( t.While != null && !c.Scratch.ContainsKey("held") ) {
							var held = t.While(c);
							if ( held.Result == Outcome.Kind.NotYet ) {
								if ( c.Elapsed - (long)c.Scratch["opened"] < t.HoldMs ) return Outcome.NotYet(held.Detail);
								c.Scratch["failure"] = $"{(c.Scratch.TryGetValue("failure", out object f0) ? f0 + "; " : "")}timed out inside {t.TargetName}: {held.Detail}";
							} else if ( held.Result == Outcome.Kind.Fail ) {
								c.Scratch["failure"] = $"{(c.Scratch.TryGetValue("failure", out object f1) ? f1 + "; " : "")}{held.Detail}";
							} else {
								c.Scratch["heldNote"] = held.Detail;
							}
							c.Scratch["held"] = true;
						}
					}
					c.Scratch["phase"] = 1;
					c.Scratch["pressedAt"] = c.Elapsed;
					Press(key);
					return Outcome.NotYet("closing");
				}
				long since = c.Elapsed - (long)c.Scratch["pressedAt"];
				if ( t.State() != before || !shownNow.SetEquals(shownBefore) ) {
					if ( since < 2000 ) return Outcome.NotYet("waiting for the UI to be as it was");
					c.That(false, $"after a second {key} the UI is not as it was ({Joined(shownNow.Except(shownBefore).Select(n => "+" + n).Concat(shownBefore.Except(shownNow).Select(n => "-" + n)))})");
				}
				if ( c.Scratch.TryGetValue("note", out object n0) ) c.Note((string)n0);
				if ( c.Scratch.TryGetValue("heldNote", out object n1) ) c.Note((string)n1);
				if ( c.Scratch.TryGetValue("failure", out object f) ) c.That(false, (string)f);
				return c.Result();
			}, new[] { "A2.InGameStatePointers" }, timeoutMs: t.TimeoutMs);
			check.Setup = () => {
				key = ConfigKey(t.Action, t.Fallback);
				before = t.State();
				shownBefore = ShownNow();
				Press(key);
			};
			check.SettleMs = 400;
			// whatever happened, leave the screen as it was
			check.Teardown = () => {
				if ( check.Status == CheckStatus.Running && t.State() != before ) Press(key);
			};
			return check;
		}

		// the passive tree's nodes, while its panel is open: canvas > groups > nodes, each node an element that leads to its passive
		private static void CollectNodes(IntPtr address, int depth, List<(int id, int orbit, int index)> nodes) {
			if ( !PoEMemory.TryRead(address, out Offsets.Element e) || e.elemSelf != address ) return;
			var children = new ArrayHandle<IntPtr>(e.Children);
			if ( children.Length > 0 ) {
				if ( depth < 2 ) foreach ( var child in children ) CollectNodes(child, depth + 1, nodes);
			} else if ( PoEMemory.TryRead(address, out Offsets.Element_PassiveNode node) && Offsets.IsValid(node.ptrNodeData)
				&& PoEMemory.TryRead(node.ptrNodeData, out Offsets.PassiveNodeData data) && Offsets.IsValid(data.ptrPassiveSkill)
				&& PoEMemory.TryRead(data.ptrPassiveSkill, out Offsets.PassiveSkillRow row) && row.Id != 0 ) {
				nodes.Add((row.Id, data.Orbit, data.OrbitIndex));
			}
		}

		private static Outcome PassiveTreeNodes(TestContext c) {
			var canvas = PassiveTreePanel.Canvas;
			if ( !IsValid(canvas) ) return Outcome.NotYet("the tree has no canvas yet");
			var nodes = new List<(int id, int orbit, int index)>();
			foreach ( var group in new ArrayHandle<IntPtr>(canvas.Cache.Children) ) CollectNodes(group, 0, nodes);
			if ( nodes.Count < 500 ) return Outcome.NotYet($"{nodes.Count} passive nodes read so far");
			var distinct = nodes.Select(n => n.id).Distinct().Count();
			c.That(distinct >= nodes.Count * 0.98, $"{nodes.Count} nodes lead to only {distinct} passive ids");
			var odd = nodes.Where(n => n.orbit < 0 || n.orbit > 9 || n.index < 0 || n.index >= 100).ToList();
			// a few elements of the canvas are no passive (decorations whose pointers lead elsewhere): a stale offset would break nearly all of them
			c.That(odd.Count <= nodes.Count * 0.01, $"{odd.Count} of {nodes.Count} nodes have an orbit or orbit index that is not one, eg id {odd.FirstOrDefault().id} orbit {odd.FirstOrDefault().orbit} index {odd.FirstOrDefault().index}");
			// the Witch's first nodes: found on a Witch (ids 8879, 14419, 34750, at orbit 3, indexes 1, 3 and 4)
			foreach ( var id in new[] { 8879, 14419, 34750 } ) {
				var node = nodes.FirstOrDefault(n => n.id == id);
				c.That(node.id == id, $"the Witch's passive {id} is not among the nodes");
				if ( node.id == id ) c.GoldenAny($"passive.{id}", $"orbit {node.orbit} index {node.index}");
			}
			c.Note($"{nodes.Count} passive nodes, {distinct} ids");
			return c.Result();
		}

		private static string ElementState(string name) => IsShown(UIPointer(name)) ? "shown" : "hidden";

		private static IEnumerable<OffsetCheck> Hotkeys() {
			var panels = new[] {
				("InventoryPanel", "open_inventory_panel", VKey.I),
				("OptionsPanel", "open_options", VKey.O),
				("ChallengesPanel", "open_challenges_panel", VKey.H),
				("CharacterPanel", "open_character_panel", VKey.C),
				("SkillTree", "open_passive_skills_panel", VKey.P),
				("SocialPanel", "open_social_panel", VKey.S),
			};
			foreach ( var (name, action, fallback) in panels ) {
				string target = name;
				bool tree = target == "SkillTree";
				yield return KeyToggle(new Toggle {
					Id = $"A7.{target}", Action = action, Fallback = fallback, TargetName = target,
					Covers = tree
						? new[] { $"InGameState_UIElements.{target}", "Element.IsVisibleByte", "Element.elemParent", "Element_PassiveNode.ptrNodeData", "PassiveNodeData.ptrPassiveSkill", "PassiveNodeData.Orbit",
							"PassiveNodeData.OrbitIndex", "PassiveSkillRow.Id" }
						: new[] { $"InGameState_UIElements.{target}", "Element.IsVisibleByte", "Element.elemParent" },
					While = tree ? PassiveTreeNodes : (Func<TestContext, Outcome>)null,
					TimeoutMs = tree ? 16000 : 6000,
					State = () => ElementState(target),
					Verify = c => {
						var e = UIElement(target);
						if ( e == null || !e.IsVisible ) return $"{target} is not visible after the key";
						var rect = e.GetClientRect();
						var size = WindowSize.Value;
						if ( rect.Width < size.X * 0.05f || rect.Height < size.Y * 0.1f || rect.Right < 0 || rect.Left > size.X ) return $"{target} is at {rect}, which is not a panel on the screen";
						return null;
					},
				});
			}

			// Tab swaps the corner map and the large map
			yield return KeyToggle(new Toggle {
				Id = "A7.Map", Action = "open_map", Fallback = VKey.Tab, TargetName = "Map",
				Covers = new[] { "InGameState_UIElements.Map", "Element_SubMap.Shift", "Element_SubMap.DefaultShift", "Element_SubMap.Zoom" },
				State = () => {
					var map = GetUI()?.Map;
					return IsValid(map) ? $"large {map.LargeMap?.IsVisible}, mini {map.MiniMap?.IsVisible}" : "no map";
				},
				Verify = c => {
					var map = GetUI()?.Map;
					if ( !IsValid(map) || !IsValid(map.LargeMap) || !IsValid(map.MiniMap) ) return "the Map element has no large and small map children";
					if ( map.LargeMap.IsVisible == map.MiniMap.IsVisible ) return $"large and small map are both {(map.LargeMap.IsVisible ? "visible" : "hidden")}: one should show";
					var shown = map.LargeMap.IsVisible ? map.LargeMap : map.MiniMap;
					var size = WindowSize.Value;
					// the large map has Size 0: it fills the screen by anchoring, so only the small map has a rect to check
					if ( map.MiniMap.IsVisible ) {
						var rect = map.MiniMap.GetClientRect();
						if ( !(rect.Width > 20 && rect.Height > 20 && rect.Width < size.X * 0.4f && rect.Left > size.X * 0.5f) ) return $"the small map is at {rect}, not a corner of the screen";
						c.Golden("map.miniSize", map.MiniMap.Size);
					}
					if ( !(shown.Zoom >= 0.4f && shown.Zoom <= 1.6f) ) return $"Zoom is {shown.Zoom}, expected 0.5 to 1.5";
					if ( !(float.IsFinite(shown.Shift.X) && float.IsFinite(shown.Shift.Y) && float.IsFinite(shown.DefaultShift.X) && float.IsFinite(shown.DefaultShift.Y)) ) return "Shift is not finite";
					c.Golden("map.defaultShift", shown.DefaultShift);
					return null;
				},
			});

			// the chat box takes the keyboard: elemInputFocus is its input
			yield return KeyToggle(new Toggle {
				Id = "A7.ChatFocus", Action = "chat", Fallback = VKey.Return, TargetName = "elemInputFocus",
				Covers = new[] { "InGameState.elemInputFocus", "InGameState_UIElements.ChatBoxRoot" },
				State = () => PoEMemory.GameRoot?.InGameState?.HasInputFocus == true ? "focused" : "free",
				Verify = c => {
					var focused = PoEMemory.GameRoot?.InGameState?.Focused;
					if ( !IsValid(focused) ) return "elemInputFocus is not an Element";
					var chat = UIElement("ChatBoxRoot");
					if ( chat == null ) return "ChatBoxRoot is not an Element";
					var e = focused;
					for ( int hops = 0; e != null && e.Address != chat.Address && hops < 30; hops++ ) e = e.Parent;
					return e != null && e.Address == chat.Address ? null : "the focused element is not inside the ChatBoxRoot";
				},
			});
		}

		// ---------------------------------------------------------------- A8: the mouse and the escape menu

		/// <summary>Hover the middle of the element (or of its child number childIndex, when the element itself is mostly empty space) and expect elemHover inside it.</summary>
		private static OffsetCheck Hover(string id, string elementName, int childIndex = -1) {
			Vector2 target = Vector2.Zero;
			var check = Check(id, new[] { "InGameState.MousePos", "InGameState.elemHover", $"InGameState_UIElements.{elementName}", "Element.elemParent" }, c => {
				var game = c.Game;
				var element = UIElement(elementName);
				if ( element == null ) return Outcome.Fail($"{elementName} is not an Element");
				var mouse = game.Cache.MousePos;
				if ( c.Elapsed < 1500 && (Math.Abs(mouse.X - target.X) > 4 || Math.Abs(mouse.Y - target.Y) > 4) ) {
					SkillBar.MouseTo(target); // the game only learns of the mouse when it moves: keep sending it there
					return Outcome.NotYet($"waiting for the mouse to reach {target} (MousePos {mouse})");
				}
				c.Near("MousePos.X", mouse.X, target.X, 3);
				c.Near("MousePos.Y", mouse.Y, target.Y, 3);
				var hovered = game.Hovered;
				if ( !IsValid(hovered) ) return c.Elapsed < 1500 ? Outcome.NotYet("nothing hovered yet") : Outcome.Fail($"elemHover is not an Element with the mouse over {elementName}");
				var e = hovered;
				for ( int hops = 0; e != null && e.Address != element.Address && hops < 30; hops++ ) e = e.Parent;
				if ( e == null || e.Address != element.Address ) {
					// say what it is inside: its ancestors, named when one of them is a UIElements pointer
					var known = UIPointers();
					var chain = new List<string>();
					var h = hovered;
					for ( int hops = 0; h != null && hops < 12; hops++ ) {
						string name = known.FirstOrDefault(p => p.Value == h.Address).Key;
						chain.Add($"0x{h.Address.ToInt64():X}{(name != null ? $"={name}" : "")}[{h.Size.X:0}x{h.Size.Y:0}]");
						h = h.Parent;
						if ( h != null && h.Address == hovered.Address ) break;
					}
					c.That(false, $"the hovered element is not inside {elementName} (0x{element.Address.ToInt64():X}); it is {string.Join(" in ", chain)}");
				}
				return c.Result();
			}, new[] { "A2.InGameStatePointers" }, timeoutMs: 3000);
			check.Setup = () => {
				var element = UIElement(elementName);
				if ( element == null ) return;
				var rect = (childIndex >= 0 ? element.GetChild(childIndex) ?? element : element).GetClientRect();
				target = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
				SkillBar.MouseTo(target);
			};
			check.SettleMs = 300;
			return check;
		}

		// open the inventory and put the mouse over an item (the new character's Scrolls of Wisdom); the backpack element's hover fields are read
		// with the offsets there are now (as notes), and its memory is written to offsettests/ with the mouse away and over the item, so the
		// fields can be found by comparing the two
		private static OffsetCheck InventoryHover() {
			VKey key = VKey.None;
			var check = Check("A8.InventoryHover", new[] { "Element_Inventory.HoverItem", "Element_Inventory.XFake", "Element_Inventory.YFake", "Element_Inventory.XReal", "Element_Inventory.YReal", "Element_Inventory.CursorInInventory" }, c => {
				int phase = c.Scratch.TryGetValue("phase", out object p) ? (int)p : 0;
				var panelShown = IsShown(UIPointer("InventoryPanel"));
				var backpack = GetUI()?.InventoryPanel?.Backpack;
				var size = WindowSize.Value;
				if ( phase == 2 ) {
					if ( panelShown && c.Elapsed - (long)c.Scratch["closedAt"] < 2500 ) return Outcome.NotYet("waiting for the inventory to close");
					c.That(!panelShown, "the inventory did not close");
					var hover = (Outcome)c.Scratch["failure"];
					if ( hover.Result == Outcome.Kind.Fail ) c.That(false, hover.Detail);
					if ( c.Scratch.TryGetValue("note", out object n) ) c.Note((string)n);
					return c.Result();
				}
				if ( !panelShown || !IsValid(backpack) ) return c.Elapsed > 4000 ? Outcome.Fail("the inventory panel did not open") : Outcome.NotYet("waiting for the inventory to open");
				var item = backpack.Items.FirstOrDefault(i => i.Entity?.Path?.EndsWith("CurrencyIdentification") ?? false) ?? backpack.Items.FirstOrDefault();
				if ( item == null ) return Outcome.Fail("the backpack holds no item to put the mouse over");
				void Dump(string name) {
					var buffer = new byte[0x600];
					if ( PoEMemory.TryRead(backpack.Address + 0x200, buffer) == buffer.Length ) {
						Directory.CreateDirectory(GoldenStore.Directory);
						File.WriteAllBytes(Path.Combine(GoldenStore.Directory, $"backpack-{name}.bin"), buffer);
						File.WriteAllText(Path.Combine(GoldenStore.Directory, $"backpack-{name}.txt"), $"backpack 0x{backpack.Address.ToInt64():X} dumped from +0x200; the item element is 0x{item.Address.ToInt64():X} at {item.X},{item.Y}");
					}
				}
				var mouse = c.Game.Cache.MousePos;
				if ( phase == 0 ) {
					var away = new Vector2((int)(size.X * 0.1f), (int)(size.Y * 0.5f));
					if ( Math.Abs(mouse.X - away.X) > 2 || Math.Abs(mouse.Y - away.Y) > 2 ) { SkillBar.MouseTo(away); return Outcome.NotYet("moving the mouse away"); }
					if ( !c.Scratch.ContainsKey("awayAt") ) { c.Scratch["awayAt"] = c.Elapsed; return Outcome.NotYet("the mouse is away"); }
					if ( c.Elapsed - (long)c.Scratch["awayAt"] < 400 ) return Outcome.NotYet("the mouse is away");
					Dump("away");
					// with the mouse away from the inventory's items nothing is hovered
					var inAway = PoEMemory.TryRead(backpack.Address, out Offsets.Element_Inventory readAway) ? readAway : default;
					c.Scratch["awayFailure"] = inAway.HoverItem == IntPtr.Zero && inAway.CursorInInventory == 0 ? null : $"with the mouse away HoverItem is {inAway.HoverItem} and CursorInInventory {inAway.CursorInInventory}";
					c.Scratch["phase"] = 1;
					return Outcome.NotYet("over the item next");
				}
				var rect = item.GetClientRect();
				var target = new Vector2((int)(rect.X + (rect.Width / 2)), (int)(rect.Y + (rect.Height / 2)));
				if ( Math.Abs(mouse.X - target.X) > 2 || Math.Abs(mouse.Y - target.Y) > 2 ) { SkillBar.MouseTo(target); return Outcome.NotYet($"moving the mouse to the item at {target}"); }
				if ( !c.Scratch.ContainsKey("overAt") ) { c.Scratch["overAt"] = c.Elapsed; return Outcome.NotYet("the mouse is over the item"); }
				if ( c.Elapsed - (long)c.Scratch["overAt"] < 700 ) return Outcome.NotYet("the mouse is over the item");
				Dump("over");
				var inv = PoEMemory.TryRead(backpack.Address, out Offsets.Element_Inventory read) ? read : default;
				if ( c.Scratch.TryGetValue("awayFailure", out object af) && af != null ) c.That(false, (string)af);
				c.Eq("HoverItem", inv.HoverItem, item.Address);
				c.Eq("XReal", inv.XReal, item.X);
				c.Eq("YReal", inv.YReal, item.Y);
				c.Eq("XFake", inv.XFake, item.X);
				c.Eq("YFake", inv.YFake, item.Y);
				c.Eq("CursorInInventory", (int)inv.CursorInInventory, 1);
				c.Scratch["failure"] = c.Result();
				c.Scratch["note"] = $"over the item at {item.X},{item.Y} ({item.Address}): HoverItem {inv.HoverItem}, MoveItemHoverState {inv.MoveItemHoverState}, Fake {inv.XFake},{inv.YFake}, Real {inv.XReal},{inv.YReal}, CursorInInventory {inv.CursorInInventory}";
				c.Scratch["phase"] = 2;
				c.Scratch["closedAt"] = c.Elapsed;
				Press(key);
				return Outcome.NotYet("closing the inventory");
			}, new[] { "A2.InGameStatePointers" }, timeoutMs: 20000);
			check.Setup = () => {
				key = ConfigKey("open_inventory_panel", VKey.I);
				Press(key);
			};
			check.SettleMs = 400;
			check.Teardown = () => { if ( IsShown(UIPointer("InventoryPanel")) ) Press(key); };
			return check;
		}

		private static IEnumerable<OffsetCheck> Pointer() {
			yield return Hover("A8.HoverFlasks", "Flasks");
			yield return Hover("A8.HoverSkillBar", "SkillBar", 0);
			yield return InventoryHover();

			yield return KeyToggle(new Toggle {
				Id = "A8.EscapeMenu", Action = "", Fallback = VKey.Escape, TargetName = "EscapeState.elemMenu",
				Covers = new[] { "EscapeGameState.elemRoot", "EscapeGameState.elemMenu" },
				State = () => (PoEMemory.GameRoot?.EscapeState?.Menu?.IsVisible ?? false) ? "shown" : "hidden",
				Verify = c => {
					var escape = PoEMemory.GameRoot?.EscapeState;
					if ( !IsValid(escape?.UIRoot) ) return "the EscapeState's root is not an Element";
					if ( !IsValid(escape.Menu) ) return "the EscapeState's menu is not an Element";
					return null;
				},
			});
		}
	}
}
