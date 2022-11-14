using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {
	public static class Perf {
		public static void Clear() {
			ticks.Clear();
			clearTime = Time.ElapsedTicks;
		}

		public static IDisposable Section(string name) => new Using { Section = name, Update = ticks };

		private static Dictionary<string, long> ticks = new Dictionary<string, long>();
		private static long clearTime = 0;

		private class Using : IDisposable {
			public string Section;
			public Dictionary<string, long> Update;
			Stopwatch sw = Stopwatch.StartNew();
			private bool isDisposing = false;
			private bool isDisposed = false;
			public void Dispose() {
				if ( isDisposed || isDisposing ) return;
				isDisposing = true;
				Update.TryGetValue(Section, out long current);
				Update[Section] = current + sw.ElapsedTicks;
				isDisposed = true;
			}
		}

		private static Vector2 progressBarSize = new Vector2(-1f, 0f);
		public static void Render(ref bool show) {
			if ( show && ImGui.Begin("Performance", ref show) ) {
				ImGui.BeginTable("Table.Perf", 2);
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed);
				ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

				float budget = 16.66f * TimeSpan.TicksPerMillisecond;
				long totalTicksInFrame = Time.ElapsedTicks - clearTime;
				long sum = 0;
				foreach ( var item in ticks.OrderBy(kv => kv.Key) ) {
					float share = item.Value / (float)totalTicksInFrame;
					if ( !item.Key.Equals("StateMachine") ) {
						ImGui.TableNextRow();
						ImGui.TableNextColumn();
						ImGui.AlignTextToFramePadding();
						ImGui.Text(item.Key);
						ImGui.TableNextColumn();
						ImGui.ProgressBar(share, progressBarSize);
						sum += item.Value;
					}
					// ImGui.SameLine();
					// ImGui.Text($"{item.Key} = {item.Value} ticks");
				}
				long other = totalTicksInFrame - sum;
				float otherShare = other / (float)totalTicksInFrame;
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.Text("Other");
				ImGui.TableNextColumn();
				ImGui.ProgressBar(otherShare, progressBarSize);
				ImGui.EndTable();
				// ImGui.Text($"Total Ticks in Frame: {totalTicksInFrame} Budget: {budget}");
				// ImGui.Text($"Total Counted: {sum}");
				// ImGui.Text($"Total Uncounted: {totalTicksInFrame}");
				ImGui.End();
			}
			Clear();
		}
	}
}
