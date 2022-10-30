using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class PluginBase : State {

		protected static void Register<T>() where T : PluginBase, new() {
			var t = new T();
			Instances.Add(typeof(T).Name, t);
			Machine.Add(t);
			Log($"Plugin Added: {typeof(T).Name}");
		}

		private static Dictionary<string, PluginBase> Instances = new Dictionary<string, PluginBase>();
		internal static IEnumerable<PluginBase> Plugins => Instances.Values;

		internal static StateMachine Machine = new StateMachine();
		static PluginBase() {
			Register<PluginLoader>();
			Register<CoreSettings>();
			Register<ExamplePlugin>(); // later, PluginLoader should call all the right Register<T>()
		}

		public bool Enabled = true;
		public virtual int SortIndex => int.MaxValue;

		public virtual void Render() {
			ImGui.Checkbox("Enabled", ref Enabled);
		}

		public static T GetPlugin<T>() where T : PluginBase => Instances.TryGetValue(typeof(T).Name, out PluginBase value) ? (T)value : null;

	}

	public class PluginLoader : PluginBase {
		// a plugin that can download complile and load other plugins

		public override int SortIndex => 1;
		public override string Name => "Plugin Loader";
		public override IState OnTick(long dt) {
			if ( !Enabled ) {
				return this; // keep it in the PluginBase.Machine, but dont run
			}
			// TODO:
			return this;
		}
	}

	public class CoreSettings : PluginBase {

		public new const bool Enabled = true; // always true for CoreSettings
		public override int SortIndex => 0;
		public float FPS_Maximum = 40f;
		public bool Enable_FPS_Maximum = true;
		public bool ShowLogWindow = false;

		public override string Name => "Core Settings";

		public override void Render() {

			ImGui.Checkbox("Show Log Window", ref ShowLogWindow);
			ImGui.Checkbox("FPS Cap:", ref Enable_FPS_Maximum);
			ImGui.SameLine();
			ImGui.SliderFloat("", ref FPS_Maximum, 10, 100);
			if( ImGui.BeginChildFrame(1234, new Vector2(0f, -1f), ImGuiWindowFlags.AlwaysAutoResize) ) {
					ImGui.AlignTextToFramePadding();
				ImGui.Text($"Current FPS: {Overlay.FPS:F0}fps");
				var target = PoEMemory.Target;
				ImGui.AlignTextToFramePadding();
				ImGui.Text($"Target Process: {IsValid(target)}");
				if ( IsValid(target) ) {
					ImGui.SameLine();
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"PID: {target.Id}");
					ImGui.SameLine();
					ImGui.AlignTextToFramePadding();
					ImGui.Text($"Handle: {PoEMemory.Handle}");
					var root = PoEMemory.GameRoot;
					ImGui_Address(root.Address, "Game Root:");
					ImGui.SameLine();
					if( ImGui.Button("B##GameRoot") ) {
						Run_ObjectBrowser("GameRoot", root);
					}
					if ( Win32.GetWindowRect(target.MainWindowHandle, out var rect) ) {
						ImGui.Text($"Window: {rect.Width}x{rect.Height} at {rect.Top},{rect.Left} ");
					}
				}
				ImGui.EndChildFrame();
			}
		}

		public override IState OnTick(long dt) {
			if( ShowLogWindow && ImGui.Begin("Log Window", ref ShowLogWindow) ) {
				ImGui.Text("This is the log window");
				ImGui.End();
			}
			return base.OnTick(dt);
		}

	}


	public class ExamplePlugin : PluginBase {


		public bool UseLifeFlask = true;
		public float UseLifeFlaskAtPctLife = 50f;

		public bool ShowFlaskStatus = false;

		public override string Name => "Example";

		public override void Render() {
			base.Render();

			ImGui.Checkbox("Use Life Flask at %:", ref UseLifeFlask);
			ImGui.SameLine();
			ImGui.SliderFloat("", ref UseLifeFlaskAtPctLife, 10f, 90f);
			ImGui.Checkbox("Show Flask Status", ref ShowFlaskStatus);
			ImGui.SameLine();
			if( ImGui.Button("B##browse-flasks") ) {
				Run_ObjectBrowser("Flasks", GetUI()?.Flasks);
			}
		}
		
		public override IState OnTick(long dt) {
			if ( !Enabled ) {
				return this; // keep it in the PluginBase.Machine, but dont run
			}

			if( ShowFlaskStatus ) {
				uint id = (uint)GetHashCode();
				foreach(var elem in GetUI().Flasks.Flasks) {
					var ent = elem.Item;
					var rect = elem.GetClientRect();
					var color = ent.CurCharges < ent.ChargesPerUse ? Color.Red :
						ent.IsBuffActive ? Color.Gray :
						Color.Green;
					DrawFrame(rect, color, 2);
				}
			}

			var player = GetPlayer();
			if( IsValid(player) ) {
				var life = player.GetComponent<Life>();
				if( IsValid(life) ) {
					if ( UseLifeFlask ) {
						int maxHp = life.MaxHP - life.TotalReservedHP;
						// 0 - 100, to match the scale of the SliderFloat above
						float pctHp = 100 * life.CurHP / maxHp;
						if ( pctHp < UseLifeFlaskAtPctLife ) {
							FlaskEntity useFlask = null;
							foreach ( FlaskEntity flask in GetFlasks() ) {
								if( !IsValid(flask) ) {
									// DrawTextAt(player, $"Flask {flask.FlaskIndex}: Not a valid entity.", Color.Red);
									continue;
								}
								if( flask.LifeHealAmount <= 0 ) {
									// DrawTextAt(player, $"Flask {flask.FlaskIndex}: Not a life flask.", Color.White);
									continue;
								}
								if( flask.CurCharges < flask.ChargesPerUse ) {
									// DrawTextAt(player, $"Flask {flask.FlaskIndex}: Not enough charge {flask.CurCharges} < {flask.ChargesPerUse} (of {flask.MaxCharges}).", Color.White);
									continue;
								}
								if( flask.IsBuffActive ) {
									// DrawTextAt(player, $"Flask {flask.FlaskIndex}: Already active.", Color.White);
									continue;
								}
								if( flask.IsInstant || (flask.IsInstantOnLowLife && pctHp < 50) ) {
									useFlask = flask;
									break;
								} else {
									useFlask = useFlask ?? flask;
								}
							}

							if( useFlask != null ) {
								// DrawTextAt(GetPlayer(), $"Would use a flask {useFlask.Key}", Color.White);
								return new PressKey(useFlask.Key, 40, new Delay(100, this));
							}
						}
					}
				}
			}
			return base.OnTick(dt);
		}



	}
}
