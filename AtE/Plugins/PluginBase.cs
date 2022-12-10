using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
			Log($"Loading plugin: {T.Name}");
			// make a new T();
			var constructor = T.GetConstructor(new Type[] { });
			var obj = (PluginBase)constructor.Invoke(new object[] { });
			ApplyIniToPlugin(obj);
			Instances.Add(T.Name, obj);
			Machine.Add(obj);
			return true;
		}

		private static void ApplyIniToPlugin(PluginBase plugin) {
			Type T = plugin.GetType();
			if ( iniSettings.TryGetValue(T.Name, out var iniSection) ) {
				foreach ( var pair in iniSection ) {
					Log($"INI [{T.Name}] {pair.Key} = {pair.Value}");
					// if the plugin can Load the key directly, let them parse it themselves
					if ( plugin.Load(pair.Key, pair.Value) ) {
						continue;
					}
					// otherwise, let's try a default deserialization
					var field = T.GetField(pair.Key); // find the public field named by the Key
					if ( field == null ) {
						Log($"INI Line did not reference a Plugin field: {pair.Key}");
						continue;
					}
					// skip loading any fields marked as NoPersist
					if ( field.GetCustomAttribute<NoPersist>() != null ) {
						Log($"INI Line skipping NoPersist field: {pair.Key}");
						continue;
					}

					if ( field.FieldType == typeof(HotKey) ) {
						field.SetValue(plugin, HotKey.Parse(pair.Value));
					} else if ( field.FieldType == typeof(Keys) ) {
						if ( Enum.TryParse(pair.Value, out Keys key) ) {
							field.SetValue(plugin, key);
						}
					} else if ( field.FieldType == typeof(float) ) {
						field.SetValue(plugin, float.Parse(pair.Value));
					} else if ( field.FieldType == typeof(double) ) {
						field.SetValue(plugin, double.Parse(pair.Value));
					} else if ( field.FieldType == typeof(long) ) {
						field.SetValue(plugin, long.Parse(pair.Value));
					} else if ( field.FieldType == typeof(int) ) {
						field.SetValue(plugin, int.Parse(pair.Value));
					} else if ( field.FieldType == typeof(bool) ) {
						field.SetValue(plugin, bool.Parse(pair.Value));
					} else if ( field.FieldType == typeof(string) ) {
						field.SetValue(plugin, pair.Value);
					} else {
						throw new ArgumentException($"Unknown Field Type, cannot load from string: {field.FieldType}");
					}
				}
			}
		}
		private static void ReapplyIniToAllPlugins() {
			foreach(var plugin in Plugins) {
				ApplyIniToPlugin(plugin);
			}
		}

		private static Dictionary<string, PluginBase> Instances;
		internal static IEnumerable<PluginBase> Plugins => Instances.Values;
		internal static T GetPlugin<T>() where T : PluginBase => Instances.TryGetValue(typeof(T).Name, out PluginBase value) ? (T)value : null;

		internal static StateMachine Machine;
		static PluginBase() {
			Instances = new Dictionary<string, PluginBase>();
			Machine = new StateMachine();
			iniSettings = new Dictionary<string, Dictionary<string, string>>();
			LoadIniFile(SettingsFileName, autoCreate: true);
			// check if CoreSettings had a SelectedProfile and load that .ini file second
			if( iniSettings.TryGetValue("CoreSettings", out Dictionary<string, string> settings)
				&& settings.TryGetValue("SelectedProfile", out string profile) ) {
				string fileName = $"Settings-{Slug(profile)}.ini";
				LoadIniFile(fileName, autoCreate: false);
			}

			foreach(var pluginType in typeof(PluginBase).Assembly.GetTypes() ) {
				TryRegisterPluginType(pluginType);
			}
		
		}

		public static readonly string SettingsFileName = "Settings.ini";
		private static Dictionary<string, Dictionary<string, string>> iniSettings;
		internal static void LoadIniFile(string fileName, bool autoCreate = false) {
			if ( File.Exists(fileName) ) {
				Log($"Parsing ini file: {fileName}...");
				string sectionName = "";
				foreach ( var line in File.ReadAllLines(fileName) ) {
					if ( line.StartsWith("[") ) {
						sectionName = line.Substring(1, line.Length - 2);
						if ( !iniSettings.ContainsKey(sectionName) ) {
							iniSettings[sectionName] = new Dictionary<string, string>();
						}
					} else {
						var key_value = line.Split('=');
						if ( key_value.Length > 1 ) {
							iniSettings[sectionName][key_value[0]] = string.Join("=", key_value.Skip(1));
						}
					}
				}
			} else if ( autoCreate ) {
				File.Create(fileName);
			}
		}
		internal static void LoadIniFiles() {
			LoadIniFile(SettingsFileName, autoCreate: true);
			CoreSettings settings = GetPlugin<CoreSettings>();
			string profile = settings.SelectedProfile;
			if ( profile == null ) return;
			if ( profile.Equals("default") ) {
				LoadIniFile(SettingsFileName);
				ReapplyIniToAllPlugins();
			} else {
				string fileName = $"Settings-{Slug(profile)}.ini";
				if( File.Exists(fileName) ) {
					LoadIniFile(fileName);
					ReapplyIniToAllPlugins();
					settings.SelectedProfile = profile; // we just applied an old value, but are in the middle of changing to a new one
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
				} else { // otherwise, use all the lines from Save()
					foreach(var line in linesFromPlugin) {
						sb.AppendLine(line); // they should all be Name=Value pairs suitable for .ini file
					}
				}
				sb.AppendLine();
			}
			string text = sb.ToString();
			Log($"Saving INI File: {text}");
			File.WriteAllText(SettingsFileName, text);
			string profile = GetPlugin<CoreSettings>().SelectedProfile;
			if ( !profile.Equals("default") ) {
				string fileName = $"Settings-{Slug(profile)}.ini";
				Log($"Saving profile: {fileName}");
				File.WriteAllText(fileName, text);
			}
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
		public virtual int SortIndex => 100;

		/// <summary>
		/// Some optional help text to display next to the Enabled flag
		/// </summary>
		public virtual string HelpText => null;

		/// <summary>
		/// Called when the Console needs to render the settings panel for this plugin.
		/// </summary>
		public virtual void Render() {
			ImGui.Checkbox("Enabled", ref Enabled);
			if( HelpText != null ) {
				ImGui.SameLine();
				ImGui_HelpMarker(HelpText);
			}
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
