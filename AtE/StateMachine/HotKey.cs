using ImGuiNET;
using System;
using System.Numerics;
using System.Windows.Forms;
using static AtE.Globals;
using static AtE.Win32;

namespace AtE {

	public class HotKey : State, IDisposable {
		public Keys Key;
		public HotKey(Keys key) {
			Key = key;
			StateMachine.DefaultMachine.Add(this);
		}
		public bool IsUp => !IsDown;
		public bool IsDown = false;
		public bool IsReleased = false;
		public override IState OnTick(long dt) {
			if ( isDisposed ) return null;
			if ( Key == Keys.None ) return this;
			bool downNow = IsKeyDown(Key);
			IsReleased = IsDown && !downNow;
			if ( IsReleased ) OnRelease?.Invoke(this, null);
			IsDown = downNow;
			return this;
		}

		public EventHandler OnRelease;

		private bool isDisposed = false;
		private bool isDisposing = false;
		public void Dispose() {
			if ( !(isDisposed || isDisposing) ) {
				isDisposing = true;
				StateMachine.DefaultMachine.Remove(this);
				isDisposed = true;
			}
		}
		public bool PressImmediate() {
			if ( PoEMemory.IsAttached && PoEMemory.TargetHasFocus && !PoEMemory.GameRoot.InGameState.HasInputFocus ) {
				Log($"HotKey: PressImmediate {Key}");
				SendInput(INPUT_KeyDown(Key), INPUT_KeyUp(Key));
				return true;
			}
			return false;
		}
		public override string ToString() => Key.ToString();
		public static HotKey Parse(string str) => new HotKey(Enum.TryParse(str, out Keys key) ? key : Keys.None);
	}


	public static partial class Globals {
		public static void ImGui_HotKeyButton(string label, ref HotKey hotKey) {
			if ( ImGui.Button($"{label}:{hotKey.Key}") ) {
				Run(new KeyPicker() { Target = hotKey });
			}
		}
	}


	public class KeyPicker : State {
		public HotKey Target;
		public override IState OnTick(long dt) {
			ImGui.SetNextWindowSize(new Vector2(Overlay.Width, Overlay.Height));
			ImGui.SetNextWindowPos(new Vector2(Overlay.Left, Overlay.Top));
			if ( ImGui.Begin("HotKeyModal", ImGuiWindowFlags.Modal
				| ImGuiWindowFlags.NoNavFocus
				| ImGuiWindowFlags.NoDecoration
				| ImGuiWindowFlags.NoMove)
				) {
				var center = ImGui.GetMainViewport().GetCenter();
				ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
				if( ImGui.Begin("HotKeyCenter", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMouseInputs) ) {
					ImGui.Text("Press the new key for the binding...");
					var io = ImGui.GetIO();
					for ( int i = 0; i < io.KeyMap.Count; i++ ) {
						var key = io.KeyMap[i];
						if ( key != 0 && Win32.IsKeyDown((Keys)key) ) {
							Target.Key = (Keys)key;
							return Next;
						}
						ImGui.End();
					}
				}
				ImGui.End();
			}
			return base.OnTick(dt);
		}
	}


}
