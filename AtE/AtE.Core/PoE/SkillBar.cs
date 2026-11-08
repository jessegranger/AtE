using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// One of the 13 slots on the skill bar: which key uses it, and which skill is in it.
	/// </summary>
	public class SkillSlot {
		/// <summary>0..12, in the game's order (use_bound_skill1..13 in the game's config): LMB, MMB, RMB, Q, W, E, R, T, then the second bar.</summary>
		public int Index;
		/// <summary>The key (or mouse button: LButton, RButton, MButton, XButton1/2) with any modifier bits (VKey.Control, ...); VKey.None if unbound.</summary>
		public VKey Key;
		/// <summary>The skill in the slot, or null if it is empty.</summary>
		public ActorSkill Skill;
		/// <summary>Where the slot is on the screen (only while the skill bar is visible; the second bar's slots are hidden until it is shown).</summary>
		public RectangleF Rect;
		public bool IsVisible;
		public Element Element;

		public bool IsEmpty => Skill == null;
		public VKey KeyCode => Key & VKey.KeyCode;
		public VKey Modifiers => Key & VKey.Modifiers;
		public bool IsMouseButton => SkillBar.ToMouseButton(KeyCode).HasValue;
		public override string ToString() => $"[{Index}] {SkillBar.KeyName(Key)}: {(IsEmpty ? "(empty)" : SkillBar.SkillName(Skill))}";
	}

	/// <summary>
	/// A skill the player could put on a slot: one of the cells of the hidden skill bar (the one that opens when you click a slot).
	/// </summary>
	public class AvailableSkill {
		/// <summary>The cell's index among the hidden skill bar's children (0 is not a skill).</summary>
		public int Index;
		/// <summary>The skill, or null for the two built-in cells: Movement and Clear (empties the slot).</summary>
		public ActorSkill Skill;
		public bool IsMovement;
		public bool IsClear;
		/// <summary>Where the cell is on the screen; only valid while the hidden skill bar is open.</summary>
		public RectangleF Rect;
		public Element Element;
		public string Name => IsMovement ? SkillBar.MovementName : IsClear ? SkillBar.ClearName : SkillBar.SkillName(Skill);
		public override string ToString() => Name;
	}

	/// <summary>
	/// Reads the skill bar: what is on each slot and what key uses it, and the skills that could go on a slot.
	/// </summary>
	public static class SkillBar {
		public const int SlotCount = 13;
		/// <summary>The id the game gives the built-in movement skill (it has no gem, so no name to read).</summary>
		public const ushort MovementSkillId = 10505;
		public const string MovementName = "Movement";
		/// <summary>The name of the hidden skill bar's cell that empties a slot.</summary>
		public const string ClearName = "Clear";
		/// <summary>The left mouse button's slot; it should hold Movement, so a left click only moves or interacts.</summary>
		public const int LeftClickSlot = 0;

		public static bool IsMovement(ActorSkill skill) => skill != null && skill.Id == MovementSkillId;

		/// <summary>The 13 slots, or an empty list when not in game.</summary>
		public static List<SkillSlot> GetSlots() {
			var result = new List<SkillSlot>();
			var bar = GetUI()?.SkillBar;
			if ( !IsValid(bar) ) return result;
			var actor = PlayerActor();
			var modifiers = ConfigModifiers();
			for ( int i = 0; i < SlotCount; i++ ) {
				var cell = bar.GetChild(i);
				if ( !IsValid(cell) ) break;
				var label = cell.GetChild(0)?.GetChild(0);
				VKey key = VKey.None;
				if ( IsValid(label) && PoEMemory.TryRead(label.Address + Offsets.Element_SkillKeyLabel_VKey, out int vk) && vk > 0 && vk < 0x100 ) {
					key = (VKey)vk | (modifiers.TryGetValue(i, out VKey mod) ? mod : VKey.None);
				}
				result.Add(new SkillSlot() {
					Index = i,
					Key = key,
					Skill = SkillAt(cell, actor),
					Rect = cell.GetClientRect(),
					IsVisible = cell.IsVisible,
					Element = cell,
				});
			}
			return result;
		}

		/// <summary>True while the hidden skill bar (the list of skills to choose from) is open.</summary>
		public static bool IsChoosing => GetUI()?.HiddenSkillBar?.IsVisible ?? false;

		/// <summary>The skills that could be put on a slot (the cells of the hidden skill bar).</summary>
		public static List<AvailableSkill> GetAvailable() {
			var result = new List<AvailableSkill>();
			var hidden = GetUI()?.HiddenSkillBar;
			if ( !IsValid(hidden) ) return result;
			var actor = PlayerActor();
			int i = 0;
			foreach ( var cell in hidden.Children ) {
				if ( i > 0 && IsValid(cell) ) {
					// Movement and Clear have no skill in their cell, only their icon tells them apart
					var skill = SkillAt(cell, actor);
					string icon = skill == null ? IconName(cell) : null;
					bool isMovement = icon?.EndsWith("SkillMoveOnly") ?? false;
					bool isClear = icon?.EndsWith("SkillClear") ?? false;
					if ( skill != null || isMovement || isClear ) {
						result.Add(new AvailableSkill() {
							Index = i, Skill = skill, IsMovement = isMovement, IsClear = isClear,
							Rect = cell.GetClientRect(), Element = cell
						});
					}
				}
				i += 1;
			}
			return result;
		}

		/// <summary>The cell of the hidden skill bar for name ("Movement", "Clear", or a skill's name), or null.</summary>
		public static AvailableSkill FindAvailable(string name) => GetAvailable().FirstOrDefault(a =>
			a.Skill == null ? string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) : IsNamed(a.Skill, name));

		/// <summary>The first slot with a skill whose internal name (eg, "fireball") or display name (eg, "Fireball") is name; null if none.</summary>
		public static SkillSlot FindSlot(string name) => GetSlots().FirstOrDefault(s => IsNamed(s.Skill, name));

		public static SkillSlot GetSlot(int index) => GetSlots().FirstOrDefault(s => s.Index == index);

		/// <summary>True if slot holds name ("Movement", a skill's name, or "Clear" for an empty slot).</summary>
		public static bool Holds(SkillSlot slot, string name) => slot != null
			&& (string.Equals(name, ClearName, StringComparison.OrdinalIgnoreCase) ? slot.IsEmpty : IsNamed(slot.Skill, name));

		/// <summary>
		/// A slot that a skill could be bound to without taking anything away: the first empty, visible slot whose key
		/// is a plain key (Q, W, E, R, T, ...; not a mouse button, no modifier). Null if there is none.
		/// </summary>
		public static SkillSlot FindFreeSlot() => GetSlots().FirstOrDefault(s =>
			s.IsEmpty && s.IsVisible && s.Key != VKey.None && !s.IsMouseButton && s.Modifiers == VKey.None);

		/// <summary>The result of BindSkill: did it work, and if not, why.</summary>
		public delegate void BindResult(bool ok, string why);

		/// <summary>
		/// Put name ("Movement", "Clear", or a skill's name) on a slot, the way a player does: click the slot,
		/// wait for the hidden skill bar to open, click the skill there, then wait for the slot to hold it.
		/// It clicks, so the game must have focus. Calls done (if given) when it finishes either way.
		/// </summary>
		public static IState BindSkill(int slotIndex, string name, BindResult done = null, uint timeoutMs = 1500) {
			int step = 0;
			long elapsed = 0;
			IState Finish(bool ok, string why) {
				if ( !ok ) {
					Log($"BindSkill({slotIndex}, {name}): {why}");
					if ( IsChoosing ) PressEscape(); // don't leave it open
				}
				done?.Invoke(ok, why);
				return null;
			}
			return State.NewState($"BindSkill({slotIndex}, {name})", (self, dt) => {
				elapsed += dt;
				if ( !PoEMemory.TargetHasFocus ) return Finish(false, "the game is not focused");
				var slot = GetSlot(slotIndex);
				if ( slot == null ) return Finish(false, "no such slot");
				switch ( step ) {
					case 0: // open the hidden skill bar from the slot
						if ( Holds(slot, name) ) return Finish(true, "already there");
						if ( !slot.IsVisible ) return Finish(false, "the slot is not visible");
						if ( !IsChoosing ) ClickAt(slot.Rect);
						step = 1; elapsed = 0;
						return self;
					case 1: // wait for it to open
						if ( IsChoosing ) { step = 2; elapsed = 0; }
						else if ( elapsed > timeoutMs ) return Finish(false, "the hidden skill bar did not open");
						return self;
					case 2: // click the skill
						var pick = FindAvailable(name);
						if ( pick == null ) return Finish(false, $"{name} is not in the hidden skill bar ({string.Join(", ", GetAvailable())})");
						ClickAt(pick.Rect);
						step = 3; elapsed = 0;
						return self;
					default: // wait for the slot to hold it
						if ( Holds(slot, name) ) return Finish(true, "bound");
						if ( elapsed > timeoutMs ) return Finish(false, "the slot did not change");
						return self;
				}
			});
		}

		/// <summary>Click the middle of rect (in client coordinates, like Element.GetClientRect).</summary>
		public static void ClickAt(RectangleF rect) {
			MouseTo(new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)));
			Platform.Current.Input.MouseButton(MouseBtn.Left, true);
			Platform.Current.Input.MouseButton(MouseBtn.Left, false);
		}

		/// <summary>Move the cursor to pos (in client coordinates).</summary>
		public static void MouseTo(Vector2 pos) => Platform.Current.Input.MouseMove(pos + WindowOffset());

		private static void PressEscape() {
			Platform.Current.Input.KeyDown(VKey.Escape);
			Platform.Current.Input.KeyUp(VKey.Escape);
		}

		/// <summary>
		/// Use a slot: aim the cursor at target (client coordinates), then click the slot's mouse button, or hold its key
		/// for holdMs. Modifiers are held around it (they go through SendInput, which needs focus; plain keys can be posted).
		/// </summary>
		public static void UseSlot(SkillSlot slot, Vector2 target, uint holdMs = 40) {
			if ( slot == null || slot.Key == VKey.None ) return;
			MouseTo(target);
			var input = Platform.Current.Input;
			var mods = new List<VKey>();
			if ( (slot.Key & VKey.Control) != 0 ) mods.Add(VKey.ControlKey);
			if ( (slot.Key & VKey.Shift) != 0 ) mods.Add(VKey.ShiftKey);
			if ( (slot.Key & VKey.Alt) != 0 ) mods.Add(VKey.Menu);
			foreach ( var m in mods ) input.KeyDown(m);
			var button = ToMouseButton(slot.KeyCode);
			if ( button.HasValue ) {
				input.MouseButton(button.Value, true);
				input.MouseButton(button.Value, false);
				foreach ( var m in mods ) input.KeyUp(m);
			} else {
				Run(new PressKey(slot.KeyCode, holdMs, State.NewState(() => { foreach ( var m in mods ) input.KeyUp(m); })));
			}
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;

		public static bool IsNamed(ActorSkill skill, string name) => skill != null && name != null
			&& (string.Equals(SkillName(skill), name, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(skill.InternalName, name, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(skill.DisplayName, name, StringComparison.OrdinalIgnoreCase));

		/// <summary>A skill's name for display; other skills without a gem show their id.</summary>
		public static string SkillName(ActorSkill skill) => skill == null ? "(none)"
			: IsMovement(skill) ? MovementName
			: skill.DisplayName ?? $"skill #{skill.Id}";

		/// <summary>True if left click only moves (and interacts), rather than using some other skill.</summary>
		public static bool LeftClickIsMovement() => IsMovement(GetSlots().FirstOrDefault(s => s.Index == LeftClickSlot)?.Skill);

		/// <summary>How long a skill takes to use, in ms (the VirtualBaseSpellCastTimeMs stat); 0 if unknown.</summary>
		public static int CastTimeMs(ActorSkill skill) =>
			skill != null && skill.TryGetStat(Offsets.GameStat.VirtualBaseSpellCastTimeMs, out int ms) ? ms : 0;

		public static MouseBtn? ToMouseButton(VKey key) {
			switch ( key & VKey.KeyCode ) {
				case VKey.LButton: return MouseBtn.Left;
				case VKey.RButton: return MouseBtn.Right;
				case VKey.MButton: return MouseBtn.Middle;
				case VKey.XButton1: return MouseBtn.X1;
				case VKey.XButton2: return MouseBtn.X2;
				default: return null;
			}
		}

		public static string KeyName(VKey key) {
			if ( key == VKey.None ) return "unbound";
			string name;
			switch ( key & VKey.KeyCode ) {
				case VKey.LButton: name = "LMB"; break;
				case VKey.RButton: name = "RMB"; break;
				case VKey.MButton: name = "MMB"; break;
				case VKey.XButton1: name = "Mouse4"; break;
				case VKey.XButton2: name = "Mouse5"; break;
				default: name = (key & VKey.KeyCode).ToString(); break;
			}
			if ( (key & VKey.Alt) != 0 ) name = "Alt+" + name;
			if ( (key & VKey.Shift) != 0 ) name = "Shift+" + name;
			if ( (key & VKey.Control) != 0 ) name = "Ctrl+" + name;
			return name;
		}

		// (not Actor.Skills, which leaves out the skills without a gem, like Movement)
		private static Actor PlayerActor() => GetPlayer()?.GetComponent<Actor>();

		// eg, "Art/2DArt/UIImages/InGame/SkillMoveOnly"
		private static string IconName(Element cell) =>
			PoEMemory.TryRead(cell.Address + Offsets.Element_SkillCell_Icon, out IntPtr icon) && IsValid(icon)
			&& PoEMemory.TryRead(icon + Offsets.SkillCellIcon_Name, out IntPtr name)
			&& PoEMemory.TryReadString(name, System.Text.Encoding.Unicode, out string str) ? str : null;

		private static ActorSkill SkillAt(Element cell, Actor actor) =>
			actor != null && IsValid(actor.Address) && PoEMemory.TryRead(cell.Address + Offsets.Element_SkillCell_Skill, out IntPtr ptr) && IsValid(ptr)
				? new ActorSkill(actor) { Address = ptr } : null;

		// The modifier of each slot's key is not on the skill bar, but it is in the game's config:
		// use_bound_skill9=81 2 is Q with modifier 2 (Ctrl; Shift 1 and Alt 4 are assumed).
		private static readonly string configPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile", "production_Config.ini");
		private static DateTime configTime;
		private static Dictionary<int, VKey> configModifiers = new Dictionary<int, VKey>();
		private static long configChecked;

		private static Dictionary<int, VKey> ConfigModifiers() {
			if ( Time.ElapsedMilliseconds - configChecked < 2000 ) return configModifiers;
			configChecked = Time.ElapsedMilliseconds;
			try {
				var time = File.GetLastWriteTimeUtc(configPath);
				if ( time == configTime ) return configModifiers;
				configTime = time;
				var result = new Dictionary<int, VKey>();
				foreach ( var line in File.ReadAllLines(configPath) ) {
					if ( !line.StartsWith("use_bound_skill") ) continue;
					var parts = line.Substring("use_bound_skill".Length).Split('=');
					if ( parts.Length != 2 || !int.TryParse(parts[0], out int n) ) continue;
					var values = parts[1].Trim().Split(' ');
					if ( values.Length < 2 || !int.TryParse(values[1], out int mod) ) continue;
					VKey key = VKey.None;
					if ( (mod & 1) != 0 ) key |= VKey.Shift;
					if ( (mod & 2) != 0 ) key |= VKey.Control;
					if ( (mod & 4) != 0 ) key |= VKey.Alt;
					result[n - 1] = key;
				}
				configModifiers = result;
			} catch ( Exception e ) {
				Log($"SkillBar: could not read {configPath}: {e.Message}");
			}
			return configModifiers;
		}
	}
}
