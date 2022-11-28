using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtE {
	public static partial class Globals {

		public static readonly Stopwatch Time = Stopwatch.StartNew();

		public delegate void ActionDelegate();

		public static IEnumerable<T> Empty<T>() { yield break; }

		public static bool IsValid(Vector2 v) => !(
			float.IsNaN(v.X)
			|| float.IsInfinity(v.X)
			|| float.IsNaN(v.Y)
			|| float.IsInfinity(v.Y)
		);

		public static bool IsValid(Vector3 v) => !(
			float.IsNaN(v.X)
			|| float.IsInfinity(v.X)
			|| float.IsNaN(v.Y)
			|| float.IsInfinity(v.Y)
			|| float.IsNaN(v.Z)
			|| float.IsInfinity(v.Z)
		);

		public static IEnumerable<float> Range(float from, float to, float step = 1f) {
			for (;from < to; from += step) {
				yield return from;
			}
		}
		public static IEnumerable<long> Range(long from, long to, long step = 1) {
			for (;from < to; from += step) {
				yield return from;
			}
		}

		public static string FormatNumber(long value) {
			char suffix = '\0';
			if( value > 1024 ) {
				suffix = 'K';
				value /= 1024;
				if( value > 1024 ) {
					suffix = 'M';
					value /= 1024;
				}
			}
			return value.ToString("N2") + suffix;
		}

		public static double MovingAverage(double value, double sample, int period) => ((value * (period - 1)) + sample) / period;

		public static string Truncate(string tooLong, int maxLen) {
			return tooLong.Length > maxLen ? tooLong.Substring(0, maxLen) : tooLong;
		}

		public static string Last(this string s, int n) => s.Substring(Math.Max(0, s.Length - n), Math.Min(s.Length, n));

		public static int GetOffset<T>(string name) where T : struct =>
			typeof(T).GetFields()
				 .First(x => x.Name == name)
				 .GetCustomAttribute<FieldOffsetAttribute>()
				 .Value;

		public static void Log(params string[] line) => Debug.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] " + string.Join(" ", line));

		public static EventHandler<string> OnAreaChange;

		public static string Describe(Keys key) {
			switch ( key & ~Keys.Modifiers ) {
				case Keys.OemPipe:
					return "|";
				case Keys.OemPeriod:
					return ".";
				case Keys.OemOpenBrackets:
					return "[";
				case Keys.Oem6:
					return "]";
				default:
					return key.ToString();
			}
		}

		public static void OnRelease(Keys key, Action act) {
			bool downBefore = false;
			Run($"KeyBind[{key}]", (self, dt) => {
				bool downNow = Win32.IsKeyDown(key);
				if ( downBefore && !downNow ) {
					act();
				}
				downBefore = downNow;
				return self;
			});
		}

		public static void ImGui_HelpMarker(string text, string label = "(?)") {
			ImGui.TextDisabled(label);
			if( ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
				ImGui.BeginTooltip();
				ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
				ImGui.TextUnformatted(text);
				ImGui.PopTextWrapPos();
				ImGui.EndTooltip();
			}
		}

		private static long lastNotifyTime = 0;
		private static float notifyShift = 0f;
		public static void Notify(string text) => Notify(text, Color.Yellow, 3000, 1f);
		public static void Notify(string text, Color color) => Notify(text, color, 3000, 1f);
		public static void Notify(string text, Color color, long duration) => Notify(text, color, duration, 1f);
		public static void Notify(string text, Color color, long duration, float speed) {
			speed = speed / 13f; // just shifting to human scale, so speed = 1f feels natural
			float fontSize = ImGui.GetFontSize();
			var textPos = new Vector2(0, Overlay.Height 
				- 10 // there is a 10px padding around the bottom of the window I just can't touch despite my best efforts
				- fontSize // the first line always needs to sit one lineHeight above the bottom
				- ImGuiController.GetNextOffsetForTextAt(0)); // id 0 is used by DrawBottomLeftText, we start at the top of that
			float travelled = (Time.ElapsedMilliseconds - lastNotifyTime) * speed;
			lastNotifyTime = Time.ElapsedMilliseconds;
			Log($"Notify: {text}");
			if ( travelled < fontSize ) {
				notifyShift += fontSize - travelled;
				textPos.Y += notifyShift;
			} else {
				notifyShift = 0f;
			}
			Run("Notify", (self, dt) => {
				if( duration > 0 ) {
					DrawTextAt(textPos, text, color);
					textPos.Y -= dt * speed;
					duration -= dt;
					return self;
				}
				return null;
			});

		}

		public static float DistanceSq(Vector3 a, Vector3 b) => (a - b).LengthSquared();
		public static float Distance(Vector3 a, Vector3 b) => (a - b).Length();
		public static float DistanceSq(Vector2 a, Vector2 b) => (a - b).LengthSquared();
		public static float Distance(Vector2 a, Vector2 b) => (a - b).Length();

		public static Vector2 Center(RectangleF rect) => new Vector2(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

		public static string Describe(Offsets.Vector2i v) => $"<{v.X}, {v.Y}>";
		public static string Describe(Vector2 v) => $"<{v.X}, {v.Y}>";
		public static string Describe(Vector3 v) => $"<{v.X}, {v.Y}, {v.Z}>";

		public static bool IsOneHanded(Entity ent) => ent != null && ( (ent.Path?.StartsWith("Metadata/Items/Weapons/OneHandWeapons") ?? false) || IsShield(ent) );
		public static bool IsShield(Entity ent) => ent?.Path.StartsWith("Metadata/Items/Armours/Shields") ?? false;
		public static uint GetItemLevel(InventoryItem item) => GetItemLevel(item?.Entity);
		public static uint GetItemLevel(Entity ent, uint _default = 1) => GetItemLevel(ent?.GetComponent<Mods>(), _default);
		public static uint GetItemLevel(Mods mods, uint _default = 1) => mods?.Level ?? _default;

	}
}
