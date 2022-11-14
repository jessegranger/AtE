using ImGuiNET;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class CoreSettings : PluginBase {

		[NoPersist] // only CoreSettings should NoPersist on Enabled
		public new const bool Enabled = true; // always true for CoreSettings, so we add the const

		public override int SortIndex => 0;

		public float FPS_Maximum = 40f;
		public bool Enable_FPS_Maximum = true;
		public bool ShowFPS = false;

		public bool OnlyRenderWhenFocused = true;

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
			ImGui.Checkbox("FPS Cap:", ref Enable_FPS_Maximum);
			ImGui.SameLine();
			ImGui.SliderFloat("", ref FPS_Maximum, 20, 60);
			ImGui.Checkbox("Only Render when PoE is focused", ref OnlyRenderWhenFocused);
			ImGui.AlignTextToFramePadding();
			ImGui.Text($"Overlay FPS: {Overlay.FPS:F0}fps");
			ImGui.SameLine();
			ImGui.Checkbox("Display", ref ShowFPS);
			// ImGui.Text($"Overlay IsTransparent: {Overlay.RenderForm.IsTransparent}");
			ImGui.Text($"Offsets: {Offsets.VersionMajor}.{Offsets.VersionMinor} / PoE {Offsets.PoEVersion}");
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
			DrawBottomLeftText(
				(Paused ? "[=]" : "[>]")
				+ $" {timeInZone.Elapsed.ToString(@"mm\:ss")}"
				+ (ShowFPS ? $" {Overlay.FPS:F0} fps" : "")
			, Color.Orange);
			return base.OnTick(dt);
		}

	}
}
