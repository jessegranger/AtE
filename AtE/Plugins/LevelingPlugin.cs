using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;
using ImGuiNET;
using System.Drawing;

namespace AtE {
	class LevelingPlugin : PluginBase {

		public override string Name => "Leveling Help";

		public LevelingPlugin() : base() {
			EntityCache.EntityAdded += (sender, ent) => {
				Notify($"Added: {ent?.Path}");
			};
		}

		public bool DismissStoryText = false;

		public override void Render() {
			ImGui.Checkbox("Dismiss Story Text", ref DismissStoryText);
			ImGui.SameLine();
			ImGui_HelpMarker("When NPCs show long text with a 'Continue' button, this will click 'Continue' immediately.");
		}

		private long lastClick = 0;

		private void DebugElement(Element elem, string prefix = "") {
			long id = elem.Address.ToInt64();
			ImGui.Text($"{prefix}.Text = {elem.Text ?? "??"}");
			ImGui.SameLine();
			if ( ImGui.Button($"B##{id:X}") ) {
				Run_ObjectBrowser($"Element {id:X}", elem);
			} else if ( ImGui.IsItemHovered() ) {
				DrawFrame(elem.GetClientRect(), Color.Yellow, 2);
			}
			if( elem.IsVisible ) {
				ImGui.SameLine();
				ImGui.Text("Visible");
			}
			Element[] children = elem.Children.ToArray();
			for(uint i = 0; i < children.Length; i++ ) {
				DebugElement(children[i], prefix + $".{i}");
			}
		}

		private void ClickElement(Element elem) {
			long now = Time.ElapsedMilliseconds;
			if( (now - lastClick) > 100 ) {
				lastClick = now;
				Run(new LeftClickAt(elem.GetClientRect(), 30, 1));
			}
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached ) {
				return this;
			}

			var ui = GetUI();
			if( !IsValid(ui) ) {
				return this;
			}

			var dialog = ui.NpcOptions;
			/*
			ImGui.Begin("Debug LeagueNpcDialog");
			if( IsValid(dialog) ) {
				DebugElement(ui.NpcOptions, "ui.NpcOptions");
			} else {
				ImGui.Text("Not valid");
			}
			ImGui.End();
			*/
			if( IsValid(dialog) && dialog.IsVisibleLocal) {
				var continueOption = dialog
					.GetChild(1)?
					.GetChild(2)?
					.GetChild(0)?
					.GetChild(2)?
					.GetChild(2)?
					.GetChild(0) ?? null;
				if( IsValid(continueOption) && continueOption.IsVisibleLocal && continueOption.Text.Equals("Continue") ) {
					ClickElement(continueOption);
					return new Delay(100, this);
				}
			}
			dialog = ui.NpcDialog;
			if( IsValid(dialog) && (dialog?.IsVisibleLocal ?? false) ) {
				var options = dialog.GetChild(1)?.GetChild(2) ?? null;
				if( options != null ) {
					foreach(var child in options.Children) {
						var textChild = child?.GetChild(0);
						if( textChild?.Text?.Equals("Continue") ?? false ) {
							ClickElement(textChild);
							return new Delay(100, this);
						}
					}
				}
			}
			return base.OnTick(dt);
		}
	}
}
