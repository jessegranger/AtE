using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A part of the AutomationPlugin: its settings are its public fields (saved like a plugin's, with its Key in front),
	/// Render draws its controls, and OnTick is called each frame while the plugin is enabled (its result is not used).
	/// </summary>
	public abstract class AutomationSection : State {
		/// <summary>What its settings are saved under; the class name by default.</summary>
		public virtual string Key => GetType().Name;
		public virtual void Render() { }
	}

	/// <summary>
	/// Playing the game by itself: finding the way (Navigation), spending passive points (Passive Tree), and fighting
	/// through areas (Combat).
	/// </summary>
	public class AutomationPlugin : PluginBase {
		public override string Name => "Automation";

		public readonly NavigationSection Navigation = new NavigationSection();
		public readonly PassiveTreeSection PassiveTree = new PassiveTreeSection();
		public readonly CombatSection Combat = new CombatSection();
		public readonly TownSection Town = new TownSection();

		/// <summary>Draw a small expanding circle where each mouse click is sent (see ClickMarks).</summary>
		public bool ShowClicks = true;

		private IEnumerable<AutomationSection> Sections => new AutomationSection[] { Navigation, PassiveTree, Combat, Town };

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Clicks", ref ShowClicks);
			ImGui.SameLine();
			ImGui_HelpMarker("Draws a small yellow circle, growing for a moment, wherever a mouse click is sent (by a routine, path following, or any other plugin).");
			foreach ( var section in Sections ) {
				if ( ImGui.CollapsingHeader(section.Name) ) {
					ImGui.PushID(section.Key);
					ImGui.Indent();
					section.Render();
					ImGui.Unindent();
					ImGui.PopID();
				}
			}
		}

		public override IState OnTick(long dt) {
			if ( Enabled && !Paused ) {
				foreach ( var section in Sections ) section.OnTick(dt);
			}
			DefaultFollower.ReleaseIfNotTicked();
			if ( ShowClicks ) ClickMarks.Draw();
			return this;
		}

		// each section's fields, as "Section.Field=value"; the combat routines' as "CombatSection.Routine.Field=value"
		private IEnumerable<(string prefix, object target)> SettingsTargets =>
			Sections.Select(s => ($"{s.Key}.", (object)s))
				.Concat(Combat.Routines.Select(r => ($"{Combat.Key}.{r.Key}.", (object)r)));

		public override string[] Save() =>
			// (the sections are saved below, by field)
			FieldLines(this).Where(line => !new[] { nameof(Navigation), nameof(PassiveTree), nameof(Combat), nameof(Town) }.Any(f => line.StartsWith($"{f}=")))
				.Concat(SettingsTargets.SelectMany(t => FieldLines(t.target, t.prefix)))
				.ToArray();

		public override bool Load(string key, string value) {
			// each routine had its own on / off key: keep the chosen routine's
			if ( key == $"{Combat.Key}.{Combat.Routine}.ToggleKey" && Combat.ToggleKey.Key == VKey.None ) {
				TrySetField(Combat, "ToggleKey", value);
				return true;
			}
			// the longest prefix first: CombatSection.WitchStart.EngageRadius is a routine's, not the section's
			foreach ( var (prefix, target) in SettingsTargets.OrderByDescending(t => t.prefix.Length) ) {
				if ( key.StartsWith(prefix) ) {
					TrySetField(target, key.Substring(prefix.Length), value);
					return true;
				}
			}
			return false;
		}

		protected override IEnumerable<(string section, string prefix)> FormerSections => new[] {
			("PathfindingPlugin", $"{Navigation.Key}."),
			("PassiveTreePlugin", $"{PassiveTree.Key}."),
			("SkillAttackDriver", $"{Combat.Key}.SkillAttackDriver."),
			("TestRoutine", $"{Combat.Key}.TestRoutine."),
			("WitchStart", $"{Combat.Key}.WitchStart."),
		};
	}

	/// <summary>
	/// Fighting: one of the combat drivers (see CombatDriver, CombatRoutine) runs, the one chosen as Routine.
	/// </summary>
	public class CombatSection : AutomationSection {
		public override string Name => "Combat";

		/// <summary>The class name of the combat driver to run.</summary>
		public string Routine = nameof(WitchStart);
		/// <summary>Starts and stops the chosen routine.</summary>
		public HotKey ToggleKey = new HotKey(VKey.None);

		/// <summary>One of each combat driver there is.</summary>
		[NoPersist]
		public readonly List<CombatDriver> Routines = typeof(CombatDriver).Assembly.GetTypes()
			.Where(t => t.IsSubclassOf(typeof(CombatDriver)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
			.Select(t => (CombatDriver)Activator.CreateInstance(t))
			.OrderBy(r => r.Name)
			.ToList();

		public CombatDriver Current => Routines.FirstOrDefault(r => r.Key == Routine);

		public override void Render() {
			var names = Routines.Select(r => r.Name).ToArray();
			int index = Routines.IndexOf(Current);
			if ( ImGui.Combo("Routine", ref index, names, names.Length) ) {
				Current?.Stop("another routine was chosen");
				Routine = Routines[index].Key;
			}
			ImGui_HotKeyButton("On / Off", ref ToggleKey);
			var current = Current;
			if ( current != null ) {
				ImGui.SameLine();
				ImGui.Text(current.IsActive ? $"On: {current.Status}" : $"Off ({current.Status})");
			}
			ImGui.Separator();
			current?.Render();
		}

		public override IState OnTick(long dt) {
			var current = Current;
			if ( current != null && ToggleKey.IsReleased && PoEMemory.IsAttached ) {
				if ( current.IsActive ) current.Stop("switched off");
				else current.Start();
			}
			current?.OnTick(dt);
			return this;
		}
	}
}
