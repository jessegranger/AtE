using ImGuiNET;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class MousePlugin : PluginBase {

		public override string Name => "Mouse";

		public bool RepeatLeftClicks = true;
		public int RepeatLeftClickWait = 500;
		public int RepeatLeftClickInterval = 150;

		public bool OnlyWhileHoldingShift = true;
		public bool OnlyWhileHoldingControl = true;

		public override void Render() {
			base.Render();

			ImGui.Checkbox("Repeat Left Clicks", ref RepeatLeftClicks);
			ImGui.SliderInt("Start repeating after", ref RepeatLeftClickWait, 300, 1000);
			ImGui.SliderInt("Then, repeat interval", ref RepeatLeftClickInterval, 100, 300);
			ImGui.Separator();
			ImGui.Checkbox("While Holding Shift", ref OnlyWhileHoldingShift);
			ImGui.Checkbox("While Holding Control", ref OnlyWhileHoldingControl);
		}

		private bool downBefore = false;
		private long downSince = 0;
		private long lastRepeat = 0;

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached || !PoEMemory.TargetHasFocus ) return this;
			// ImGui.Begin("Debug Mouse");

			bool downNow = Win32.IsKeyDown(Keys.LButton);
			bool shiftDownNow = Win32.IsKeyDown(Keys.LShiftKey);
			bool controlDownNow = Win32.IsKeyDown(Keys.LControlKey);
			bool shouldRun = (OnlyWhileHoldingShift && shiftDownNow) || (OnlyWhileHoldingControl && controlDownNow);
			long now = Time.ElapsedMilliseconds;
			// ImGui.Text($"Down now: {downNow} {now - downSince}ms Last Repeat {now - lastRepeat}ms");
			if( downNow && shouldRun ) {
				if( ! downBefore ) { // this is the first frame the button went down
					downSince = lastRepeat = now;
				} else {
					if( (now - downSince) > RepeatLeftClickWait ) {
						if( (now - lastRepeat) > RepeatLeftClickInterval ) {
							Win32.SendInput(
								Win32.INPUT_Mouse(Win32.MouseFlag.LeftUp),
								Win32.INPUT_Mouse(Win32.MouseFlag.LeftDown));
							lastRepeat = now;
						}
					}
				}
			}
			downBefore = downNow;
			// ImGui.End();
			return this;
		}
	}
}
