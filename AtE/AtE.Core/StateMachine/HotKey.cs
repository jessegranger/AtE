using ImGuiNET;
using System;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	public class HotKey : State, IDisposable {
		public VKey Key;
		public HotKey(VKey key) {
			Key = key;
			Machine.DefaultMachine.Add(this);
		}
		public bool IsUp => !IsDown;
		public bool IsDown = false;
		public bool IsReleased = false;
		public bool WasJustPressed = false;
		public override IState OnTick(long dt) {
			if ( isDisposed ) return null;
			if ( Key == VKey.None ) return this;
			bool downNow = Platform.Current.Keys.IsKeyDown(Key);
			WasJustPressed = downNow && !IsDown;
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
				Machine.DefaultMachine.Remove(this);
				isDisposed = true;
			}
		}
		public bool PressImmediate() {
			if ( PoEMemory.IsAttached && PoEMemory.TargetHasFocus && !PoEMemory.GameRoot.InGameState.HasInputFocus ) {
				Log($"HotKey: PressImmediate {Key}");
				Platform.Current.Input.KeyDown(Key);
				Platform.Current.Input.KeyUp(Key);
				return true;
			}
			return false;
		}
		public override string ToString() => Key.ToString();
		public static HotKey Parse(string str) => new HotKey(Enum.TryParse(str, out VKey key) ? key : VKey.None);
	}


	public static partial class Globals {
		public static void ImGui_HotKeyButton(string label, ref HotKey hotKey) {
			if ( ImGui.Button($"{label} : {Describe(hotKey.Key)}") ) {
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
				if( ImGui.Begin("HotKeyCenter", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.Modal ) ) {
					ImGui.Text("Press the new key for the binding...");
					var io = ImGui.GetIO();
					for ( int i = 0; i < io.KeyMap.Count; i++ ) {
						var key = io.KeyMap[i];
						if ( key != 0 && Platform.Current.Keys.IsKeyDown((VKey)key) ) {
							Target.Key = (VKey)key;
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
