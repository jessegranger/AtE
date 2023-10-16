using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;
using ImGuiNET;

namespace AtE {
	class LevelingPlugin : PluginBase {

		public override string Name => "Leveling Help";


		public bool DismissStoryText = false;

		public override void Render() {
			ImGui.Checkbox("Dismiss Story Text", ref DismissStoryText);
			ImGui.SameLine();
			ImGui_HelpMarker("When NPCs show long text with a 'Continue' button, this will click 'Continue' immediately.");
		}

		private long lastClick = 0;

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached || !PoEMemory.TargetHasFocus ) {
				return this;
			}

			var dialog = GetUI().NpcDialog;
			if( dialog?.IsVisibleLocal ?? false ) {
				var options = dialog.GetChild(1)?.GetChild(2) ?? null;
				if( options != null ) {
					foreach(var child in options.Children) {
						var textChild = child?.GetChild(0);
						if( textChild == default ) {
							continue;
						}
						string text = textChild.Text;
						if( text != null ) {
							if( text.Equals("Continue") ) {
								long now = Time.ElapsedMilliseconds;
								if( (now - lastClick) > 100 ) {
									lastClick = now;
									Run(new LeftClickAt(textChild.GetClientRect(), 30, 1));
								}
							}
						}
					}
				}
			}
			return base.OnTick(dt);
		}
	}
}
