using ImGuiNET;

namespace AtE {
	public class ExamplePlugin : PluginBase {

		// public Fields of known types will be automatically persisted using an .ini file
		public bool ShowDemoWindow = false;
		// see: PluginBase for the supported types
		public bool ShowMetricsWindow = false;

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Demo Window", ref ShowDemoWindow);
			ImGui.Checkbox("Show Metrics Window", ref ShowMetricsWindow);
		}

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused ) {
				if ( ShowDemoWindow ) {
					ImGui.ShowDemoWindow();
				}
				if ( ShowMetricsWindow ) {
					ImGui.ShowMetricsWindow();
				}
			}
			return this;
		}
	}
}
