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
	/// Playing the game by itself: the PoB build to follow (Build), finding the way (Navigation), spending passive points
	/// (Passive Tree), fighting through areas (Combat), and town (Town).
	/// </summary>
	public class AutomationPlugin : PluginBase {
		public override string Name => "Automation";

		public AutomationPlugin() : base() {
			Enabled = false; // opt-in for a new profile; a persisted INI value still overrides this
		}

		public readonly BuildSection Build = new BuildSection();
		public readonly NavigationSection Navigation = new NavigationSection();
		public readonly PassiveTreeSection PassiveTree = new PassiveTreeSection();
		public readonly CombatSection Combat = new CombatSection();
		public readonly TownSection Town = new TownSection();
		public readonly OffsetTestSection OffsetTests = new OffsetTestSection();

		/// <summary>Draw a small expanding circle where each mouse click is sent (see ClickMarks).</summary>
		public bool ShowClicks = true;

		private IEnumerable<AutomationSection> Sections => new AutomationSection[] { Build, Navigation, PassiveTree, Combat, Town, OffsetTests };

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
			HeldButtons.Check(); // (the right button never stays down)
			if ( ShowClicks ) ClickMarks.Draw();
			return this;
		}

		// each section's fields, as "Section.Field=value"; the combat routines' as "CombatSection.Routine.Field=value"
		private IEnumerable<(string prefix, object target)> SettingsTargets =>
			Sections.Select(s => ($"{s.Key}.", (object)s))
				.Concat(Combat.Routines.Select(r => ($"{Combat.Key}.{r.Key}.", (object)r)));

		public override string[] Save() =>
			// (the sections are saved below, by field)
			FieldLines(this).Where(line => !new[] { nameof(Build), nameof(Navigation), nameof(PassiveTree), nameof(Combat), nameof(Town), nameof(OffsetTests) }.Any(f => line.StartsWith($"{f}=")))
				.Concat(SettingsTargets.SelectMany(t => FieldLines(t.target, t.prefix)))
				.ToArray();

		public override bool Load(string key, string value) {
			// each routine had its own on / off key: keep the chosen routine's
			if ( key == $"{Combat.Key}.{Combat.Routine}.ToggleKey" && Combat.ToggleKey.Key == VKey.None ) {
				TrySetField(Combat, "ToggleKey", value);
				return true;
			}
			// the build was chosen in the Passive Tree section
			if ( key == $"{PassiveTree.Key}.BuildFile" ) {
				if ( string.IsNullOrEmpty(Build.BuildFile) ) Build.BuildFile = value;
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
		};
	}

	/// <summary>
	/// Fighting: one of the combat drivers (see CombatDriver, CombatRoutine) runs, the one chosen as Routine.
	/// </summary>
	public class CombatSection : AutomationSection {
		public override string Name => "Combat";

		/// <summary>The class name of the combat driver to run.</summary>
		public string Routine = nameof(CampaignRoutine);
		/// <summary>Starts and stops the chosen routine.</summary>
		public HotKey ToggleKey = new HotKey(VKey.None);

		/// <summary>Click the things worth clicking when passing them, while a routine runs (see Interactables).</summary>
		public bool UseInteractables = true;
		/// <summary>The kinds of Interactables switched off (their names, separated by commas).</summary>
		public string DisabledInteractables = "";

		/// <summary>Is the kind of Interactables named name switched on?</summary>
		public bool IsInteractableEnabled(string name) => UseInteractables && !DisabledInteractableNames().Contains(name);
		private HashSet<string> DisabledInteractableNames() => new HashSet<string>(
			DisabledInteractables.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

		private void RenderInteractables() {
			if ( !ImGui.TreeNode("Interactables") ) return;
			ImGui.Checkbox("Use Interactables", ref UseInteractables);
			ImGui.SameLine();
			ImGui_HelpMarker("While a routine runs, walk to and click the kinds checked below when passing them (within 60 cells, with no monsters in attack range).");
			if ( !UseInteractables ) ImGui.BeginDisabled();
			var disabled = DisabledInteractableNames();
			foreach ( var kind in Interactables.Kinds ) {
				bool on = !disabled.Contains(kind.Name);
				if ( ImGui.Checkbox(kind.Name, ref on) ) {
					if ( on ) disabled.Remove(kind.Name); else disabled.Add(kind.Name);
					DisabledInteractables = string.Join(", ", disabled.OrderBy(n => n));
				}
			}
			if ( !UseInteractables ) ImGui.EndDisabled();
			ImGui.TreePop();
		}

		/// <summary>One of each combat driver there is.</summary>
		[NoPersist]
		public readonly List<CombatDriver> Routines = typeof(CombatDriver).Assembly.GetTypes()
			.Where(t => t.IsSubclassOf(typeof(CombatDriver)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
			.Select(t => (CombatDriver)Activator.CreateInstance(t))
			.OrderBy(r => r.Name)
			.ToList();

		// (a routine that is gone, eg a class's start routine, falls back to the Campaign)
		public CombatDriver Current => Routines.FirstOrDefault(r => r.Key == Routine) ?? Routines.FirstOrDefault(r => r is CampaignRoutine);

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
			RenderInteractables();
			ImGui.Separator();
			current?.Render();
		}

		// --campaign (see LaunchOptions): switch the Campaign on once, after the game has been ready for a moment
		private bool launchStarted;
		private long readySince = -1;
		private void StartOnLaunch() {
			if ( launchStarted || !LaunchOptions.Campaign ) return;
			bool ready = PoEMemory.IsAttached && IsValid(GetPlayer()) && GetCurrentArea() != null
				&& !(PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true);
			if ( !ready ) { readySince = -1; return; }
			if ( readySince < 0 ) readySince = Time.ElapsedMilliseconds;
			if ( Time.ElapsedMilliseconds - readySince < 3000 ) return;
			launchStarted = true;
			var campaign = Routines.FirstOrDefault(r => r is CampaignRoutine);
			if ( campaign == null ) return;
			if ( Current != campaign ) { Current?.Stop("another routine was chosen"); Routine = campaign.Key; }
			Log($"Combat: --campaign: switching the {campaign.Name} on");
			Run(ForceGameFocus(State.NewState(() => { if ( !campaign.IsActive ) campaign.Start(); })));
		}

		public override IState OnTick(long dt) {
			TravelSkills.Start(); // (once: it runs on the default Machine from then on)
			RighteousFireKeeper.Start(this);
			StartOnLaunch();
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
