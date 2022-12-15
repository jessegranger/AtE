using ImGuiNET;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class MouseKeyboardPlugin : PluginBase {

		public override string Name => "Mouse & Keyboard";

		public bool RepeatLeftClicks = true;
		public int RepeatLeftClickWait = 500;
		public int RepeatLeftClickInterval = 150;

		public bool OnlyWhileHoldingShift = true;
		public bool OnlyWhileHoldingControl = true;

		public bool EnableAlsoCast = false;
		public HotKey AlsoCastMainKey = new HotKey(Keys.None);
		public bool EnableAlsoCastAlways = false;
		public HotKey AlsoCastAlwaysKey = new HotKey(Keys.None);
		public bool EnableAlsoCast4Second = false;
		public HotKey AlsoCast4SecondKey = new HotKey(Keys.None);
		public bool EnableAlsoCast8Second = false;
		public HotKey AlsoCast8SecondKey = new HotKey(Keys.None);
		public bool EnableAlsoCast16Second = false;
		public HotKey AlsoCast16SecondKey = new HotKey(Keys.None);

		public override void Render() {
			base.Render();

			ImGui.Text("Mouse:");
			ImGui.Checkbox("Repeat Left Clicks", ref RepeatLeftClicks);
			ImGui.SliderInt("Start repeating after", ref RepeatLeftClickWait, 300, 1000);
			ImGui.SliderInt("Then, repeat interval", ref RepeatLeftClickInterval, 100, 300);
			ImGui.Checkbox("While Holding Shift", ref OnlyWhileHoldingShift);
			ImGui.Checkbox("While Holding Control", ref OnlyWhileHoldingControl);
			ImGui.Separator();

			ImGui.Text("Keyboard:");
			ImGui.Checkbox("Enabled##UseAlsoCast", ref EnableAlsoCast);
			ImGui.SameLine();
			ImGui_HotKeyButton("Main Key", ref AlsoCastMainKey);
			ImGui.SameLine();
			ImGui_HelpMarker("While you hold the main key, the other keys will cast automatically");

			ImGui.Checkbox("Always##UseAlsoCast", ref EnableAlsoCastAlways);
			ImGui.SameLine();
			ImGui_HotKeyButton("Always", ref AlsoCastAlwaysKey);

			/* Not Implemented yet:

			ImGui.Checkbox("Every 4 Seconds##UseAlsoCast", ref EnableAlsoCast4Second);
			ImGui.SameLine();
			ImGui_HotKeyButton("Every 4 Seconds", ref AlsoCastAlwaysKey);

			ImGui.Checkbox("Every 8 Seconds##UseAlsoCast", ref EnableAlsoCast8Second);
			ImGui.SameLine();
			ImGui_HotKeyButton("Every 8 Seconds", ref AlsoCastAlwaysKey);

			ImGui.Checkbox("Every 16 Seconds##UseAlsoCast", ref EnableAlsoCast16Second);
			ImGui.SameLine();
			ImGui_HotKeyButton("Every 16 Seconds", ref AlsoCastAlwaysKey);
			*/

		}

		private bool downBefore = false;
		private long downSince = 0;
		private long lastRepeat = 0;

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached || !PoEMemory.TargetHasFocus ) return this;

			bool downNow = Win32.IsKeyDown(Keys.LButton);
			bool shiftDownNow = Win32.IsKeyDown(Keys.LShiftKey);
			bool controlDownNow = Win32.IsKeyDown(Keys.LControlKey);
			bool shouldRun = (OnlyWhileHoldingShift && shiftDownNow) || (OnlyWhileHoldingControl && controlDownNow);
			long now = Time.ElapsedMilliseconds;
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
			CheckAlsoCast();
			return this;
		}

		private bool alsoCastMainKeyDownBefore = false;
		private void CheckAlsoCast() {
			if( EnableAlsoCast && ! PoEMemory.GameRoot.InGameState.HasInputFocus ) {
				bool downNow = Win32.IsKeyDown(AlsoCastMainKey.Key);
				if( downNow && ! alsoCastMainKeyDownBefore ) {
					// OnKeyDown:
					if( EnableAlsoCastAlways ) {
						Win32.SendInput(Win32.INPUT_KeyDown(AlsoCastAlwaysKey.Key));
					}
				} else if ( alsoCastMainKeyDownBefore && !downNow ) {
					if( EnableAlsoCastAlways ) {
						Win32.SendInput(Win32.INPUT_KeyUp(AlsoCastAlwaysKey.Key));
					}
				}
				alsoCastMainKeyDownBefore = downNow;
			}
		}
	}
}
