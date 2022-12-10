using ImGuiNET;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class CoreSettings : PluginBase {

		[NoPersist] // only CoreSettings should NoPersist on Enabled
		public new const bool Enabled = true; // always true for CoreSettings, so we add the const

		public override int SortIndex => 0;

		public int InputLatency = 30; // number of ms per input, in situtations where many inputs must be sent

		public bool ShowFPS = false;
		public bool ShowPerformanceWindow = false;

		public bool OnlyRenderWhenFocused = true;

		public string SelectedProfile = "default";

		public HotKey ConsoleKey = new HotKey(Keys.F12);
		public HotKey PauseKey = new HotKey(Keys.Pause);

		private Stopwatch timeInZone = Stopwatch.StartNew();
		public CoreSettings():base() {
			OnAreaChange += (sender, areaName) => timeInZone.Restart();
		}

		public override string Name => "Core Settings";

		public override void Render() {
			ImGui_HotKeyButton("Console Key", ref ConsoleKey);
			ImGui_HotKeyButton("Pause/Resume Key", ref PauseKey);
			ImGui.Checkbox("Only Render when PoE is focused", ref OnlyRenderWhenFocused);
			ImGui.AlignTextToFramePadding();
			ImGui.Text($"Overlay FPS: {Overlay.FPS:F0}fps");
			ImGui.SameLine();
			ImGui.Checkbox("Display", ref ShowFPS);
			ImGui.Checkbox("Show Performance Window", ref ShowPerformanceWindow);
			ImGui.SliderInt("Input Latency", ref InputLatency, 10, 100);
			// ImGui.Text($"Overlay IsTransparent: {Overlay.RenderForm.IsTransparent}");
			ImGui.Text($"Offsets: {Offsets.VersionMajor}.{Offsets.VersionMinor} for PoE {Offsets.PoEVersion}");
			var target = PoEMemory.Target;
			ImGui.AlignTextToFramePadding();
			ImGui.Text($"Attached: {PoEMemory.IsAttached}");
			if ( IsValid(target) ) {
				ImGui.SameLine();
				ImGui.AlignTextToFramePadding();
				ImGui.Text($"PID: {target.Id}");
				ImGui.SameLine();
				ImGui.AlignTextToFramePadding();
				ImGui.Text($"Handle: {PoEMemory.Handle}");
				var root = PoEMemory.GameRoot;
				ImGui_Address(root.Address, "Game Root:");
				ImGui.SameLine();
				if ( ImGui.Button("B##GameRoot") ) {
					Run_ObjectBrowser("GameRoot", root);
				}
				if ( Win32.GetWindowRect(target.MainWindowHandle, out var rect) ) {
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"Window: {rect.Width}x{rect.Height} at {rect.Top},{rect.Left} ");
				}

			}
		}

		private double averageFPS = 60d;

		public override IState OnTick(long dt) {
			if( PauseKey.IsReleased ) {
				if ( Paused ) {
					Notify("Resumed.");
					ResumeAll();
				} else {
					Notify("Paused.");
					PauseAll();
				}
			}
			averageFPS = MovingAverage(averageFPS, Overlay.FPS, 10);
			DrawBottomLeftText(
				(Paused ? "[=]" : "[>]")
				+ $" {timeInZone.Elapsed:mm\\:ss}"
				+ (ShowFPS ? $" fps {averageFPS:F0}" : "")
			, Color.Orange);
			return base.OnTick(dt);
		}


		public static IEnumerable<string> GetProfiles() => Directory.EnumerateFiles(".", "Settings-*.ini", SearchOption.TopDirectoryOnly)
			.Select(s => s.Split('-')[1].Split('.')[0]);

		public static bool CreateProfile(string name) {
			if ( name == null ) return false;
			if ( name.Length < 1 ) return false;
			if ( name.Equals("default") ) return false;
			string fileName = $"Settings-{Slug(name)}.ini";
			if( !File.Exists(fileName) ) {
				File.Copy(SettingsFileName, fileName);
				return true;
			}
			return false;
		}
	}
}
