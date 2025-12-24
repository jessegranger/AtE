using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class MouseKeyboardPlugin : PluginBase {

		public override string Name => "Mouse & Keyboard";

		public enum KeyBindMode : int {
			Hold= 0,
			Press = 1,
		}

		public bool RepeatLeftClicks = true;
		public int RepeatLeftClickWait = 500;
		public int RepeatLeftClickInterval = 150;

		public bool OnlyWhileHoldingShift = true;
		public bool OnlyWhileHoldingControl = true;

		public bool EnableAlsoCast = false;
		public HotKey AlsoCastMainKey = new HotKey(Keys.None);

		public bool EnableAlsoCastKey1 = false;
		public HotKey AlsoCastKey1 = new HotKey(Keys.None);
		public float AlsoCastKey1Throttle = 0;
		public int AlsoCastKey1Mode = (int)KeyBindMode.Hold;
		private long AlsoCastKey1LastPress = 0;
		

		public bool EnableAlsoCastKey2 = false;
		public HotKey AlsoCastKey2 = new HotKey(Keys.None);
		public float AlsoCastKey2Throttle = 0;
		public int AlsoCastKey2Mode = (int)KeyBindMode.Hold;
		private long AlsoCastKey2LastPress = 0;

		public bool EnableAlsoCastKey3 = false;
		public HotKey AlsoCastKey3 = new HotKey(Keys.None);
		public float AlsoCastKey3Throttle = 0;
		public int AlsoCastKey3Mode = (int)KeyBindMode.Hold;
		private long AlsoCastKey3LastPress = 0;

		public bool EnableAlsoCastKey4 = false;
		public HotKey AlsoCastKey4 = new HotKey(Keys.None);
		public float AlsoCastKey4Throttle = 0;
		public int AlsoCastKey4Mode = (int)KeyBindMode.Hold;
		private long AlsoCastKey4LastPress = 0;

		public bool ShowMouseCoords = false;
		public HotKey ResetZoneSecretKey = new HotKey(Keys.None);

		public bool LootFocusEnabled = false;
		public HotKey LootFocusKey = new HotKey(Keys.ShiftKey);
		private PointF LootFocusMouseSaved = new PointF(0, 0);

		public override void Render() {
			base.Render();

			const float numberInputWidth = 85f;
			const float keyInputWidth = 60f;

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
			ImGui.Checkbox("Loot Assist Enabled", ref LootFocusEnabled);
			ImGui.SameLine();

			ImGui_HotKeyButton("Loot Focus Key", ref LootFocusKey);
			ImGui.Checkbox("Enabled##UseAlsoCast", ref EnableAlsoCast);
			ImGui.SameLine();
			ImGui_HotKeyButton("Main Key", ref AlsoCastMainKey);
			ImGui.SameLine();
			ImGui_HelpMarker("While you hold the main key, the other keys will cast automatically");

			ImGui.Checkbox("Also##AlsoCast1", ref EnableAlsoCastKey1);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(keyInputWidth);
			ImGui.Combo("##AlsoCast1Mode", ref AlsoCastKey1Mode, "Hold\0Press");
			ImGui.SameLine();
			if ( AlsoCastKey1Mode == (int)KeyBindMode.Press ) {
				ImGui_HelpMarker("Key #1 will be pressed (down and up) while Main Key is held down.");
			} else {
				ImGui_HelpMarker("Key #1 will be held down for as long as Main Key is held down.");
			}
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #1", ref AlsoCastKey1);
			if( AlsoCastKey1Mode == (int)KeyBindMode.Press ) {
				ImGui.SameLine();
				ImGui.Text("Every");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(numberInputWidth);
				ImGui.InputFloat("seconds##Key1", ref AlsoCastKey1Throttle, 1);
			} else {

			}
	
			ImGui.Checkbox("Also##AlsoCast2", ref EnableAlsoCastKey2);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(keyInputWidth);
			ImGui.Combo("##AlsoCast2Mode", ref AlsoCastKey2Mode, "Hold\0Press");
			ImGui.SameLine();
			if ( AlsoCastKey2Mode == (int)KeyBindMode.Press ) {
				ImGui_HelpMarker("Key #2 will be pressed (down and up) while Main Key is held down.");
			} else {
				ImGui_HelpMarker("Key #2 will be held down for as long as Main Key is held down.");
			}
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #2", ref AlsoCastKey2);
			if( AlsoCastKey2Mode == (int)KeyBindMode.Press ) {
				ImGui.SameLine();
				ImGui.Text("Every");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(numberInputWidth);
				ImGui.InputFloat("seconds##Key2", ref AlsoCastKey2Throttle, 1);
			} else {

			}
	
			ImGui.Checkbox("Also##AlsoCast3", ref EnableAlsoCastKey3);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(keyInputWidth);
			ImGui.Combo("##AlsoCast3Mode", ref AlsoCastKey3Mode, "Hold\0Press");
			ImGui.SameLine();
			if ( AlsoCastKey3Mode == (int)KeyBindMode.Press ) {
				ImGui_HelpMarker("Key #3 will be pressed (down and up) while Main Key is held down.");
			} else {
				ImGui_HelpMarker("Key #3 will be held down for as long as Main Key is held down.");
			}
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #3", ref AlsoCastKey3);
			if( AlsoCastKey3Mode == (int)KeyBindMode.Press ) {
				ImGui.SameLine();
				ImGui.Text("Every");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(numberInputWidth);
				ImGui.InputFloat("seconds##Key3", ref AlsoCastKey3Throttle, 1);
			} else {

			}

			ImGui.Checkbox("Also##AlsoCast4", ref EnableAlsoCastKey4);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(keyInputWidth);
			ImGui.Combo("##AlsoCast4Mode", ref AlsoCastKey4Mode, "Hold\0Press");
			ImGui.SameLine();
			if ( AlsoCastKey4Mode == (int)KeyBindMode.Press ) {
				ImGui_HelpMarker("Key #4 will be pressed (down and up) while Main Key is held down.");
			} else {
				ImGui_HelpMarker("Key #4 will be held down for as long as Main Key is held down.");
			}
			ImGui.SameLine();
			ImGui_HotKeyButton("Key #4", ref AlsoCastKey4);
			if( AlsoCastKey4Mode == (int)KeyBindMode.Press ) {
				ImGui.SameLine();
				ImGui.Text("Every");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(numberInputWidth);
				ImGui.InputFloat("seconds##Key4", ref AlsoCastKey4Throttle, 1);
			} else {

			}

		}


		private bool downBefore = false;
		private long downSince = 0;
		private long lastRepeat = 0;

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached || !PoEMemory.TargetHasFocus ) return this;

			var ui = GetUI();
			if( ! IsValid(ui) ) {
				return this;
			}

			string areaName = PoEMemory.GameRoot?.AreaLoadingState?.AreaName ?? null;
			if( Offsets.IsHideout(areaName) ) {
				return this;
			}

			if( ShowMouseCoords ) {
				var pos = Center(ui.Mouse?.GetClientRect() ?? RectangleF.Empty);
				DrawBottomLeftText($"Mouse X: {pos.X} Y: {pos.Y}", Color.Yellow);
			}

			if( LootFocusEnabled && LootFocusKey.IsReleased ) {
				if( LootFocusMouseSaved.X > 0 && LootFocusMouseSaved.Y > 0 ) {
					new MoveMouse(LootFocusMouseSaved.X, LootFocusMouseSaved.Y).OnTick(dt);
				}
			}

			if( LootFocusEnabled && LootFocusKey.IsDown ) {
				if( LootFocusKey.WasJustPressed ) {
					LootFocusMouseSaved = ui.Mouse.GetClientRect().Location;
				}
				var labels = ui.LabelsOnGround.GetAllLabels();
				var globalRect = ui.LabelsOnGround.GetClientRect();
				var mouseLoc= ui.Mouse.GetClientRect().Location;
				var mousePos = new Vector2(mouseLoc.X, mouseLoc.Y);
				double nearest = double.MaxValue;
				Vector2 nearestPos = Vector2.Zero;
				foreach(var label in labels) {
					var elem = label.Label;
					if( ! elem.IsVisibleLocal ) {
						continue;
					}
					var ent = label.Item;
					if( !IsValid(ent) ) {
						continue;
					}
					var path = ent.Path;
					 if( ! path.Equals("Metadata/MiscellaneousObjects/WorldItem") ) {
						continue;
					}
					var center = Center(elem.GetClientRect());
					var distance = Vector2.DistanceSquared(mousePos, center);
					if( distance < nearest ) {
						nearest = distance;
						nearestPos = center;
					}
					DrawFrame(elem.GetClientRect(), Color.Red);
				}
				if( nearestPos.X > 0 && nearestPos.Y > 0 && nearestPos.X < globalRect.Width && nearestPos.Y < globalRect.Height ) {
					// now run a MoveMouse in this same frame
					new MoveMouse(nearestPos).OnTick(dt);
				}
				return this;
			}

			if( ResetZoneSecretKey.IsReleased ) {
				var label = ui.LabelsOnGround.GetAllLabels().FirstOrDefault(l => l.Label.Text?.Equals("Waypoint") ?? false);
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

		private void doKeyDown(Keys key, ref long lastPress, float throttle) {
			long elapsed = Time.ElapsedMilliseconds - lastPress;
			if( elapsed < throttle * 1000 ) {
				return;
			}
			lastPress = Time.ElapsedMilliseconds;
			Win32.SendInput(Win32.INPUT_KeyDown(key));
		}

		private long lastPressOfAnyAlsoCast = 0;
		private bool doKeyPress(Keys key, ref long lastPress, float throttle) {
			long now = Time.ElapsedMilliseconds;
			long throttleMS = Math.Max(100, (long)(throttle * 1000));
			long sinceLastPress = now - lastPress;
			long sinceAnyAlsoCast = now - lastPressOfAnyAlsoCast;
			if ( sinceLastPress > throttleMS ) {
				if( sinceAnyAlsoCast < 100 ) {
					Log($"Defering key press {key}...");
					return false; // skip injecting fresh inputs for a short while
				}
				Log($"Pressing key {key}...");
				Run(new PressKey(key, 300));
				lastPressOfAnyAlsoCast = now;
				lastPress = now;
				return true;
			}
			return false;
		}

		private void doKeyUp(Keys key) {
			Win32.SendInput(Win32.INPUT_KeyUp(key));
		}

		private bool alsoCastMainKeyDownBefore = false;
		private long alsoCastMainKeyLastPress = 0;

		private void CheckAlsoCast() {
			if( EnableAlsoCast && ! PoEMemory.GameRoot.InGameState.HasInputFocus ) {
				bool downNow = Win32.IsKeyDown(AlsoCastMainKey.Key);
				if ( downNow && !alsoCastMainKeyDownBefore ) {
					// OnKeyDown: (of the main key)
					alsoCastMainKeyLastPress = Time.ElapsedMilliseconds;
					if ( EnableAlsoCastKey1 && AlsoCastKey1Mode == (int)KeyBindMode.Hold ) { doKeyDown(AlsoCastKey1.Key, ref AlsoCastKey1LastPress, AlsoCastKey1Throttle); }
					if ( EnableAlsoCastKey2 && AlsoCastKey2Mode == (int)KeyBindMode.Hold ) { doKeyDown(AlsoCastKey2.Key, ref AlsoCastKey2LastPress, AlsoCastKey2Throttle); }
					if ( EnableAlsoCastKey3 && AlsoCastKey3Mode == (int)KeyBindMode.Hold ) { doKeyDown(AlsoCastKey3.Key, ref AlsoCastKey3LastPress, AlsoCastKey3Throttle); }
					if ( EnableAlsoCastKey4 && AlsoCastKey4Mode == (int)KeyBindMode.Hold ) { doKeyDown(AlsoCastKey4.Key, ref AlsoCastKey4LastPress, AlsoCastKey4Throttle); }
				} else if ( downNow && alsoCastMainKeyDownBefore ) {
					// the main key is being held down over multiple frames
					bool acted = (EnableAlsoCastKey1 && AlsoCastKey1Mode == (int)KeyBindMode.Press && doKeyPress(AlsoCastKey1.Key, ref AlsoCastKey1LastPress, AlsoCastKey1Throttle))
					|| (EnableAlsoCastKey2 && AlsoCastKey2Mode == (int)KeyBindMode.Press && doKeyPress(AlsoCastKey2.Key, ref AlsoCastKey2LastPress, AlsoCastKey2Throttle))
					|| (EnableAlsoCastKey3 && AlsoCastKey3Mode == (int)KeyBindMode.Press && doKeyPress(AlsoCastKey3.Key, ref AlsoCastKey3LastPress, AlsoCastKey3Throttle))
					|| (EnableAlsoCastKey4 && AlsoCastKey4Mode == (int)KeyBindMode.Press && doKeyPress(AlsoCastKey4.Key, ref AlsoCastKey4LastPress, AlsoCastKey4Throttle));
				} else if ( alsoCastMainKeyDownBefore && !downNow ) {
					// OnKeyUp: (of the main key)
					if( EnableAlsoCastKey1  && AlsoCastKey1Mode == (int)KeyBindMode.Hold ) { doKeyUp(AlsoCastKey1.Key); }
					if( EnableAlsoCastKey2  && AlsoCastKey2Mode == (int)KeyBindMode.Hold ) { doKeyUp(AlsoCastKey2.Key); }
					if( EnableAlsoCastKey3  && AlsoCastKey3Mode == (int)KeyBindMode.Hold ) { doKeyUp(AlsoCastKey3.Key); }
					if( EnableAlsoCastKey4  && AlsoCastKey4Mode == (int)KeyBindMode.Hold ) { doKeyUp(AlsoCastKey4.Key); }
				}
				alsoCastMainKeyDownBefore = downNow;
			}
		}
	}
}
