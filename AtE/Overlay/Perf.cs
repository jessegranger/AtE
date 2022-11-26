using ImGuiNET;
using System;
using System.Collections.Concurrent;
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
			foreach(var key in ticks.Keys.ToArray()) {
				ticks[key] = 0;
			}
			clearTime = Time.ElapsedTicks;
		}

		public static IDisposable Section(string name) => new Disposable { Section = name };

		private static ConcurrentDictionary<string, long> ticks = new ConcurrentDictionary<string, long>();
		private static long clearTime = 0;

		private class Disposable : IDisposable {
			public string Section;
			private long startedAt = Time.ElapsedTicks;
			public void Dispose() {
				long elapsed = Time.ElapsedTicks - startedAt;
				ticks.AddOrUpdate(Section, elapsed, (k, v) => v + elapsed);
			}
		}

		private static readonly Vector2 progressBarSize = new Vector2(-1f, 0f);
		public static void Render(ref bool show) {
			if ( show && ImGui.Begin("Performance", ref show) ) {
				ImGui.BeginTable("Table.Perf", 2);
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed);
				ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

				// float budget = 16.66f * TimeSpan.TicksPerMillisecond;
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
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.Text("Entities");
				ImGui.TableNextColumn();
				ImGui.Text($"{EntityCache.IdCount} - {EntityCache.AddressCount}");
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
