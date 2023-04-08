using ImGuiNET;
using System.Collections.Generic;
using static AtE.Globals;

namespace AtE {
	public class ExamplePlugin : PluginBase {

		// public Fields of known types will be automatically persisted using an .ini file
		public bool ShowDemoWindow = false;
		// see: PluginBase for the supported types
		public bool ShowMetricsWindow = false;

		public override int SortIndex => 999;

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Demo Window", ref ShowDemoWindow);
			ImGui.Checkbox("Show Metrics Window", ref ShowMetricsWindow);
		}

		private string strDebugId = "";

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached ) {
				if ( ShowDemoWindow ) {
					ImGui.ShowDemoWindow();
				}
				if ( ShowMetricsWindow ) {
					ImGui.ShowMetricsWindow();
				}
			}


			/* How to Debug:
			ImGui.Begin("Debug Entity");
			ImGui.InputText("Entity:", ref strDebugId, 5);
			uint entId = 0;
			try {
				entId = uint.Parse(strDebugId);
				ImGui.Text($"Entity: {entId}");
			} catch ( System.Exception ) {
			}
			if( entId != 0 ) {
				if( EntityCache.TryGetEntity(entId, out Entity ent) ) {
					ImGui_Object($"Entity-{entId}", "Entity", ent, new HashSet<int>());
					// ImGui_Object($"ObjectMagicProperties-{entId}", "ObjectMagicProperties", ent.GetComponent<ObjectMagicProperties>(), new HashSet<int>());
					// ImGui_Object($"Positioned-{entId}", "Positioned", ent.GetComponent<Positioned>(), new HashSet<int>());
					// ImGui_Object($"Render-{entId}", "Render", ent.GetComponent<Render>(), new HashSet<int>());
					// ImGui.Text("Stats:");
					// ImGui_Object($"Stats-{entId}", "Stats", ent.GetComponent<Stats>(), new HashSet<int>());
					ImGui.Text("ObjectMagicProperties:");
					ImGui_Object($"ObjectMagicProperties-{entId}", "ObjectMagicProperties", ent.GetComponent<ObjectMagicProperties>(), new HashSet<int>());
				}
			} else {
				ImGui.Text("No entity selected.");
			}
			ImGui.End();
			*/

			return this;
		}
	}
}
