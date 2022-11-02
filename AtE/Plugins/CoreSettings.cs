﻿using ImGuiNET;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class CoreSettings : PluginBase {

		[NoPersist] // only CoreSettings should NoPersist on Enabled
		public new const bool Enabled = true; // always true for CoreSettings, so we add the const

		public override int SortIndex => 0;

		public float FPS_Maximum = 40f;
		public bool Enable_FPS_Maximum = true;
		[NoPersist]
		public bool ShowLogWindow = false;

		public HotKey ConsoleKey = new HotKey(Keys.F12);

		public override string Name => "Core Settings";

		public override void Render() {
			base.Render();

			ImGui.Checkbox("Show Log Window", ref ShowLogWindow);
			ImGui.Checkbox("FPS Cap:", ref Enable_FPS_Maximum);
			ImGui.SameLine();
			ImGui.SliderFloat("", ref FPS_Maximum, 20, 60);
			ImGui.AlignTextToFramePadding();
			ImGui.Text($"Overlay FPS: {Overlay.FPS:F0}fps");
			ImGui.Text($"Overlay IsTransparent: {Overlay.RenderForm.IsTransparent}");
			var target = PoEMemory.Target;
			ImGui.AlignTextToFramePadding();
			ImGui.Text($"Attached: {PoEMemory.Attached}");
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
					ImGui.Text($"Window: {rect.Width}x{rect.Height} at {rect.Top},{rect.Left} ");
				}
				var io = ImGui.GetIO();
				ImGui.Text($"Want Capture Mouse: {io.WantCaptureMouse} Keyboard: {io.WantCaptureKeyboard}");
			}
		}

		public override IState OnTick(long dt) {
			if ( ShowLogWindow && ImGui.Begin("Log Window", ref ShowLogWindow) ) {
				ImGui.Text("This is the log window");
				ImGui.End();
			}
			return base.OnTick(dt);
		}

	}
}