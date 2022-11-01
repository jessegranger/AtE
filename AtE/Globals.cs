using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

		public static string Truncate(string tooLong, int maxLen) {
			return tooLong.Length > maxLen ? tooLong.Substring(0, maxLen) : tooLong;
		}

		public static string Last(this string s, int n) => s.Substring(Math.Max(0, s.Length - n), Math.Min(s.Length, n));

		public static int GetOffset<T>(string name) where T : struct =>
			typeof(T).GetFields()
				 .First(x => x.Name == name)
				 .GetCustomAttribute<FieldOffsetAttribute>()
				 .Value;

		public static void Log(params string[] line) => Debug.WriteLine(string.Join(" ", line));

		public static EventHandler<string> OnAreaChange;

		public static void OnRelease(Keys key, Action act) {
			bool downBefore = false;
			Run($"KeyBind[{key}]", (self, dt) => {
				if ( dt == 0 ) return self;
				bool downNow = Win32.IsKeyDown(key);
				if ( downBefore && !downNow ) act();
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

	}
}
