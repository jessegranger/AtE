using ImGuiNET;
using System.Drawing;
using System.Linq;
using System.Numerics;
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

		public bool EnableAlsoCastKey1 = false;
		public HotKey AlsoCastKey1 = new HotKey(Keys.None);
		public int AlsoCastKey1Throttle = 0;
		private long LastPressKey1 = 0;

		public bool EnableAlsoCastKey2 = false;
		public HotKey AlsoCastKey2 = new HotKey(Keys.None);
		public int AlsoCastKey2Throttle = 0;
		private long LastPressKey2 = 0;

		public bool EnableAlsoCastKey3 = false;
		public HotKey AlsoCastKey3 = new HotKey(Keys.None);
		public int AlsoCastKey3Throttle = 0;
		private long LastPressKey3 = 0;

		public bool EnableAlsoCastKey4 = false;
		public HotKey AlsoCastKey4 = new HotKey(Keys.None);
		public int AlsoCastKey4Throttle = 0;
		private long LastPressKey4 = 0;

		public bool ShowMouseCoords = false;
		public HotKey ResetZoneSecretKey = new HotKey(Keys.None);

		public override void Render() {
			base.Render();

			ImGui.Text("Mouse:");
			ImGui.Checkbox("Show Cursor Position", ref ShowMouseCoords);
			ImGui.Checkbox("Repeat Left Clicks", ref RepeatLeftClicks);
			ImGui.SetNextItemWidth(200f);
			ImGui.SliderInt("Start repeating after", ref RepeatLeftClickWait, 300, 1000);
			ImGui.SetNextItemWidth(200f);
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

			ImGui.Checkbox("Also Press##AlsoCast1", ref EnableAlsoCastKey1);
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #1", ref AlsoCastKey1);
			ImGui.SameLine();
			ImGui.Text("Every");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100f);
			ImGui.InputInt("seconds##Key1", ref AlsoCastKey1Throttle, 1);
	
			ImGui.Checkbox("Also Press##AlsoCast2", ref EnableAlsoCastKey2);
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #2", ref AlsoCastKey2);
			ImGui.SameLine();
			ImGui.Text("Every");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100f);
			ImGui.InputInt("seconds##Key2", ref AlsoCastKey2Throttle, 1);
		
			ImGui.Checkbox("Also Press##AlsoCast3", ref EnableAlsoCastKey3);
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #3", ref AlsoCastKey3);
			ImGui.SameLine();
			ImGui.Text("Every");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100f);
			ImGui.InputInt("seconds##Key3", ref AlsoCastKey3Throttle, 1);
		
			ImGui.Checkbox("Also Press##AlsoCast4", ref EnableAlsoCastKey4);
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #4", ref AlsoCastKey4);
			ImGui.SameLine();
			ImGui.Text("Every");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100f);
			ImGui.InputInt("seconds##Key4", ref AlsoCastKey4Throttle, 1);
		}

		private bool downBefore = false;
		private long downSince = 0;
		private long lastRepeat = 0;

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached || !PoEMemory.TargetHasFocus ) return this;

			if( ShowMouseCoords ) {
				var ui = GetUI();
				if ( IsValid(ui) ) {
					var pos = Center(ui.Mouse?.GetClientRect() ?? RectangleF.Empty);
					DrawBottomLeftText($"Mouse X: {pos.X} Y: {pos.Y}", Color.Yellow);
				}
			}

			if( ResetZoneSecretKey.IsReleased ) {
				var label = GetUI().LabelsOnGround.GetAllLabels().FirstOrDefault(l => l.Label.Text?.Equals("Waypoint") ?? false);
				if( IsValid(label?.Label) ) {
					Run(new LeftClickAt(label.Label.GetClientRect(), 30, 1
						, new Delay(300
						, new KeyDown(Keys.LControlKey
						, new LeftClickAt(new Vector2(520, 400), 30, 1
						, new Delay(300
						, new LeftClickAt(new Vector2(420, 370), 30, 1
						, new KeyUp(Keys.LControlKey)
						))))))
					);
				} else {
					Notify("Did not find a Waypoint label");
				}
			}

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
							Win32.SendInput( // repeat the already-held left mouse, by releasing and pressing it again
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

		private void doKeyDown(Keys key, ref long lastPress, int throttle) {
			long elapsed = Time.ElapsedMilliseconds - lastPress;
			if( elapsed < throttle * 1000 ) {
				return;
			}
			lastPress = Time.ElapsedMilliseconds;
			Win32.SendInput(Win32.INPUT_KeyDown(key));
		}

		private void doKeyUp(Keys key) {
			Win32.SendInput(Win32.INPUT_KeyUp(key));
		}

		private bool alsoCastMainKeyDownBefore = false;
		private void CheckAlsoCast() {
			if( EnableAlsoCast && ! PoEMemory.GameRoot.InGameState.HasInputFocus ) {
				bool downNow = Win32.IsKeyDown(AlsoCastMainKey.Key);
				if( downNow && ! alsoCastMainKeyDownBefore ) {
					// OnKeyDown:
					if ( EnableAlsoCastKey1 ) { doKeyDown(AlsoCastKey1.Key, ref LastPressKey1, AlsoCastKey1Throttle); }
					if ( EnableAlsoCastKey2 ) { doKeyDown(AlsoCastKey2.Key, ref LastPressKey2, AlsoCastKey2Throttle); }
					if ( EnableAlsoCastKey3 ) { doKeyDown(AlsoCastKey3.Key, ref LastPressKey3, AlsoCastKey3Throttle); }
					if ( EnableAlsoCastKey4 ) { doKeyDown(AlsoCastKey4.Key, ref LastPressKey4, AlsoCastKey4Throttle); }
				} else if ( alsoCastMainKeyDownBefore && !downNow ) {
					if( EnableAlsoCastKey1 ) { doKeyUp(AlsoCastKey1.Key); }
					if( EnableAlsoCastKey2 ) { doKeyUp(AlsoCastKey2.Key); }
					if( EnableAlsoCastKey3 ) { doKeyUp(AlsoCastKey3.Key); }
					if( EnableAlsoCastKey4 ) { doKeyUp(AlsoCastKey4.Key); }
				}
				alsoCastMainKeyDownBefore = downNow;
			}
		}
	}
}
