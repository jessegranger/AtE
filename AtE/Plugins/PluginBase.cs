using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
			if ( iniSettings.TryGetValue(T.Name, out var iniSection) ) {
				foreach ( var pair in iniSection ) {
					// if the plugin can Load the key directly, let them parse it themselves
					if ( obj.Load(pair.Key, pair.Value) ) {
						continue;
					}
					// otherwise, let's try a default deserialization
					var field = T.GetField(pair.Key); // find the public field named by the Key
					if ( field == null ) {
						continue;
					}
					// skip loading any fields marked as NoPersist
					if ( field.GetCustomAttribute<NoPersist>() != null ) {
						continue;
					}

					if ( field.FieldType == typeof(HotKey) ) {
						field.SetValue(obj, HotKey.Parse(pair.Value));
					} else if ( field.FieldType == typeof(Keys) ) {
						if( Enum.TryParse(pair.Value, out Keys key) ) {
							field.SetValue(obj, key);
						}
					} else if ( field.FieldType == typeof(float) ) {
						field.SetValue(obj, float.Parse(pair.Value));
					} else if ( field.FieldType == typeof(double) ) {
						field.SetValue(obj, double.Parse(pair.Value));
					} else if ( field.FieldType == typeof(long) ) {
						field.SetValue(obj, long.Parse(pair.Value));
					} else if ( field.FieldType == typeof(int) ) {
						field.SetValue(obj, int.Parse(pair.Value));
					} else if ( field.FieldType == typeof(bool) ) {
						field.SetValue(obj, bool.Parse(pair.Value));
					} else if ( field.FieldType == typeof(string) ) {
						field.SetValue(obj, pair.Value);
					} else {
						throw new ArgumentException($"Unknown Field Type, cannot load from string: {field.FieldType}");
					}
				}
			}
		
			Instances.Add(T.Name, obj);
			Machine.Add(obj);
			Log($"Plugin Added: {T.Name}");
			return true;
		}

		private static Dictionary<string, PluginBase> Instances;
		internal static IEnumerable<PluginBase> Plugins => Instances.Values;
		internal static T GetPlugin<T>() where T : PluginBase => Instances.TryGetValue(typeof(T).Name, out PluginBase value) ? (T)value : null;

		internal static StateMachine Machine;
		static PluginBase() {
			Instances = new Dictionary<string, PluginBase>();
			Machine = new StateMachine();
			iniSettings = new Dictionary<string, Dictionary<string, string>>();
			LoadIniFile();

			foreach(var pluginType in typeof(PluginBase).Assembly.GetTypes() ) {
				TryRegisterPluginType(pluginType);
			}
		
		}

		private static readonly string SettingsFileName = "Settings.ini";
		private static Dictionary<string, Dictionary<string, string>> iniSettings;
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
				string[] linesFromPlugin = plugin.Save();
				if ( linesFromPlugin == null ) {
					// if the plugin.Save() returned null, use this default serialization of public fields:
					foreach ( var field in plugin.GetType().GetFields().Where(field => field.GetCustomAttribute<NoPersist>() == null) ) {
						sb.AppendLine($"{field.Name}={field.GetValue(plugin)}");
					}
				} else {
					// otherwise, use all the lines from Save()
					foreach(var line in linesFromPlugin) {
						sb.AppendLine(line); // they should all be Name=Value pairs suitable for .ini file
					}
				}
				sb.AppendLine();
			}
			string text = sb.ToString();
			Log($"Saving INI File: {text}");
			File.WriteAllText(SettingsFileName, text);
		}

		internal static void PauseAll() {
			foreach(var plugin in Instances.Values) {
				plugin.Paused = true;
			}
		}
		internal static void ResumeAll() {
			foreach(var plugin in Instances.Values) {
				plugin.Paused = false;
			}
		}


		/// <summary>
		/// A disabled Plugin should take no actions.
		/// But, Render() should still produce settings controls.
		/// </summary>
		public bool Enabled = true; // Enabled is persistent, saved to profile settings

		/// <summary>
		/// Paused it meant to be toggled via HotKey, and temporarily pauses some Plugin functions.
		/// The Plugin remains Enabled, and it's OnTick() will be called, but should determine it's own
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
			ImGui.Separator();
		}

		/// <summary>
		/// Sub-classes should return null to use the default serialization
		/// </summary>
		public virtual string[] Save() {
			return null;
		}

		/// <summary>
		/// Called as data is read from the INI file.
		/// </summary>
		/// <returns>true if the key was handled, false to process the key with default deserializer</returns>
		public virtual bool Load(string key, string value) {
			return false;
		}
	}
}
