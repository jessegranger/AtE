using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Apply this attribute to plugin fields that should not persist in the Settings INI file.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field)] public class NoPersist : Attribute { }

	public static partial class Globals {
		public static T GetPlugin<T>() where T : PluginBase => PluginBase.GetPlugin<T>();
	}

	/// <summary>
	/// All plugins must inherit from this base.
	/// The static portion of PluginBase manages the set of plugins, and save/load settings.ini
	/// The non-static portion defines the base members for all plugins.
	/// </summary>
	public class PluginBase : State {

		private static bool TryRegisterPluginType(Type T) {
			if ( T == null || !T.IsSubclassOf(typeof(PluginBase)) ) {
				return false;
			}
			Log($"Loading plugin type: {T.Name}");
			// make a new T();
			var constructor = T.GetConstructor(new Type[] { });
			var obj = (PluginBase)constructor.Invoke(new object[] { });
			if ( iniSettings.TryGetValue(T.Name, out var data) ) {
				foreach ( var field in T.GetFields()
					.Where(f => f.GetCustomAttribute<NoPersist>() == null) ) {
					if ( data.TryGetValue(field.Name, out string strValue) ) {
						if ( field.FieldType == typeof(HotKey) ) {
							field.SetValue(obj, HotKey.Parse(strValue));
						} else if ( field.FieldType == typeof(Keys) ) {
							field.SetValue(obj, Enum.TryParse(strValue, out Keys key) ? key : Keys.None);
						} else if ( field.FieldType == typeof(float) ) {
							field.SetValue(obj, float.Parse(strValue));
						} else if ( field.FieldType == typeof(double) ) {
							field.SetValue(obj, double.Parse(strValue));
						} else if ( field.FieldType == typeof(long) ) {
							field.SetValue(obj, long.Parse(strValue));
						} else if ( field.FieldType == typeof(int) ) {
							field.SetValue(obj, int.Parse(strValue));
						} else if ( field.FieldType == typeof(bool) ) {
							field.SetValue(obj, bool.Parse(strValue));
						} else if ( field.FieldType == typeof(string) ) {
							field.SetValue(obj, strValue);
						} else {
							throw new ArgumentException($"Unknown Field Type, cannot load from string: {field.FieldType}");
						}
					}
				}
			}
		
			Instances.Add(T.Name, obj);
			Machine.Add(obj);
			Log($"Plugin Added: {T.Name}");
			return true;
		}

		private static Dictionary<string, PluginBase> Instances = new Dictionary<string, PluginBase>();
		internal static IEnumerable<PluginBase> Plugins => Instances.Values;
		internal static T GetPlugin<T>() where T : PluginBase => Instances.TryGetValue(typeof(T).Name, out PluginBase value) ? (T)value : null;

		internal static StateMachine Machine = new StateMachine();
		static PluginBase() {
			LoadIniFile();

			foreach(var pluginType in typeof(PluginBase).Assembly.GetTypes() ) {
				TryRegisterPluginType(pluginType);
			}
		
		}

		private static readonly string SettingsFileName = "Settings.ini";
		private static Dictionary<string, Dictionary<string, string>> iniSettings = new Dictionary<string, Dictionary<string, string>>();
		private static void LoadIniFile() {
			if ( !File.Exists(SettingsFileName) ) {
				File.Create(SettingsFileName);
			} else {
				Log($"Parsing ini file...");
				string sectionName = "";
				foreach ( var line in File.ReadAllLines(SettingsFileName) ) {
					if ( line.StartsWith("[") ) {
						sectionName = line.Substring(1, line.Length - 2);
						if ( !iniSettings.ContainsKey(sectionName) ) {
							iniSettings[sectionName] = new Dictionary<string, string>();
						}
					} else {
						var key_value = line.Split('=');
						if ( key_value.Length > 1 ) {
							Log($"Parsed {key_value[0]}");
							iniSettings[sectionName][key_value[0]] = string.Join("=", key_value.Skip(1));
						}
					}
				}
			}
		}

		internal static void SaveIniFile() {
			StringBuilder sb = new StringBuilder();
			foreach(var plugin in Instances.Values.OrderBy(p => p.SortIndex) ) {
				sb.AppendLine($"[{plugin.GetType().Name}]");
				foreach ( var field in plugin.GetType().GetFields() ) {
					if ( field.GetCustomAttribute<NoPersist>() != null ) continue;
					sb.AppendLine($"{field.Name}={field.GetValue(plugin)}");
				}
				sb.AppendLine();
			}
			string text = sb.ToString();
			Log($"Saving INI File: {text}");
			File.WriteAllText(SettingsFileName, text);
		}


		/// <summary>
		/// A disabled Plugin does have have it's OnTick() called, and can take no actions.
		/// It's settings page will still call Render().
		/// </summary>
		public bool Enabled = true; // Enabled is persistent, saved to profile settings

		/// <summary>
		/// Paused it meant to be toggled via HotKey, and temporarily pauses some Plugin functions.
		/// The Plugin remains enabled, and it's OnTick() will be called, but should determine it's own
		/// appropriate response to Paused being true.
		/// </summary>
		[NoPersist]
		public bool Paused = false; // Paused is temporary, not saved to profile settings

		/// <summary>
		/// Implementation of PluginBase can override this SortIndex to put themselves higher up in the list.
		/// You probably don't need this, just the first few Core-ish plugins want to be at the top.
		/// </summary>
		public virtual int SortIndex => int.MaxValue;

		/// <summary>
		/// Called when the Console needs to render the settings panel for this plugin.
		/// </summary>
		public virtual void Render() {
			ImGui.Checkbox("Enabled", ref Enabled);
		}

	}
}
