using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {

	class CoreSettings {
		// WIP POC: maybe we do it this way
		// but maybe it should be more like:
		// [CheckboxSetting(label)]
		// bool EnforceFpsCap = true;
		// [FloatRangeSetting(label, 10, 60)]
		// float fpsCap = 30f;
		// If we can make that work...
		// Pausing this train of thought now, to work on how should Plugins work, back later
		public static ToggleSetting EnforceFpsCap = new ToggleSetting("Enforce FPS Limit", true);
		public static FloatRangeSetting FpsCap = new FloatRangeSetting("FPS Limit", 10, 60, 40);

		public static ToggleSetting ShowDebugGrid = new ToggleSetting("Show Debug Grid", false);
		public static ToggleSetting ShowDemoWindow = new ToggleSetting("Show Demo Window", false);

		static CoreSettings() {
			RunForever("Debug Grid", () => {
				if ( ShowDebugGrid ) {
					DrawDebugGrid();
				}
			});

			RunForever("Demo Window", () => {
				if ( ShowDemoWindow ) {
					ImGui.ShowDemoWindow();
				}
			});
		}
			

	}
	
	class Settings {
		public static void Show() => showWindow = true;
		public static void Hide() => showWindow = false;
		public static void Toggle() => showWindow = !showWindow;

		private static bool showWindow = true;
		public static void Render() {
			if ( !showWindow ) return;
			ImGui.Begin("Settings", ref showWindow);
			ImGui.Text($"Focus: {!Overlay.RenderForm.IsTransparent}");
			ImGui.Text($"Current FPS: {Overlay.FPS:F1}");
			Type type = typeof(CoreSettings);
			foreach(var prop in type.GetProperties()) {
				ImGui.Text($"Property: {prop.Name}");
			}
			foreach(var field in type.GetFields()) {
				object setting = field.GetValue(type);
				setting.GetType().GetMethod("Render").Invoke(setting, new object[] { });
			}
			if( ImGui.Button("Exit") ) {
				Overlay.Close();
			}
			ImGui.End();
		}
	}

	class ToggleSetting : Setting<bool> {

		public ToggleSetting(string label, bool initial_value = false) : base(label, initial_value) { }

		public override void Render() => ImGui.Checkbox(Label, ref val);
	}

	class FloatRangeSetting : Setting<float> {

		public float Min;
		public float Max;

		public FloatRangeSetting(string label, float min, float max, float initial) : base(label, initial) {
			Min = min;
			Max = max;
		}

		public override void Render() => ImGui.SliderFloat(Label, ref val, Min, Max);
	}

	class FilePathSetting : Setting<string> {
		public string WorkingDirectory = ".";
		public FilePathSetting(string label, string initial_value = "") : base(label, initial_value) { } 
		public override void Render() {
			ImGui.InputText(Label, ref val, uint.MaxValue, ImGuiInputTextFlags.ReadOnly);
			ImGui.SameLine();
			if( ImGui.Button("...") ) {
				ImGui.BeginPopup("Browse");
				ImGui.Text($"Working Directory: {WorkingDirectory}");
				foreach(string dir in Directory.GetDirectories(WorkingDirectory)) {
					if( ImGui.Button(dir) ) {
						WorkingDirectory = dir;
					}
				}
				foreach(string file in Directory.GetFiles(WorkingDirectory) ) {
					if( ImGui.Button(file) ) {
						Value = file;
					}
				}
				ImGui.End();
			}

		}
	}

	abstract class Setting<T> : ImGuiElement where T : IComparable {

		protected Setting(string label) => Label = label;
		protected Setting(string label, T value) {
			Label = label;
			Value = value;
		}

		public string Label;

		protected T val;
		public T Value {
			get => val;
			set {
				if( value?.CompareTo(val) == 0 ) {
					return;
				}
				val = value;
				OnValueChanged?.Invoke(this, val);
			}
		}

		public event EventHandler<T> OnValueChanged;
		public static implicit operator T(Setting<T> s) => s.Value;

	}

	abstract class ImGuiElement {
		protected ImGuiElement() { }
		public abstract void Render();
	}
}
