using ImGuiNET;
using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Buttons that run one of the steps in MappingSteps.cs on its own (Stash Everything, Pick Map, Open Map): the routine
	/// is switched on, and while the test runs it ticks only that step's chain (no goals, looting, or anything else of
	/// GoalRoutine's, but the input others queue with Send: Righteous Fire, travel skills), then switches itself off, with
	/// "done" or "stopped" (the reason is in the log). Starting one collapses
	/// the overlay's window and focuses the game.
	/// </summary>
	public partial class MappingRoutine {

		private IState testCurrent;
		private string testName;
		private bool testOk;
		/// <summary>How the last test ended (null while one runs, or before the first).</summary>
		public string TestResult { get; private set; }

		/// <summary>Run step on its own: switch the routine on, and tick nothing but its chain until it ends.</summary>
		public void StartTest(string name, Func<IState, IState> step) {
			Runner.Cancel(); // (the goal chain, if one was running)
			testName = name;
			testOk = false;
			TestResult = null;
			testCurrent = step(State.NewState(() => testOk = true))?.OnEnter();
			Log($"{Name}: test {name}: starting");
			AtE.Console.CollapseNext = true; // (out of the way of the stash and the ground; each step's plan starts with ForceGameFocus)
			if ( !IsActive ) Start();
		}

		/// <summary>Stop the test running (and switch the routine off).</summary>
		public void StopTest(string why) {
			if ( testCurrent == null ) return;
			testCurrent = null;
			EndTest($"stopped: {why}");
		}

		private void EndTest(string result) {
			TestResult = $"{testName}: {result}";
			Log($"{Name}: test {TestResult}");
			StopWalking("test over");
			Stop($"test {TestResult}");
		}

		// while a test runs: tick its chain (as the CampaignRunner would), nothing else
		protected override void Decide(CombatContext ctx) {
			if ( testCurrent == null ) { base.Decide(ctx); return; }
			// input queued with Send (eg, RighteousFireKeeper's key, TravelSkills' Frostblink) goes out first, then the test
			// goes on where it was: as GoalRoutine.Decide does with Runner.Push (else it waits in the queue forever)
			if ( TakeInput() is IState input ) {
				input.Tail().Next = testCurrent;
				testCurrent = input.OnEnter();
			}
			CampaignState.Context = ctx;
			try {
				var next = testCurrent.OnTick(FrameDt);
				if ( next != testCurrent ) next = next?.OnEnter();
				testCurrent = next;
			} finally {
				CampaignState.Context = null;
			}
			if ( testCurrent == null ) { EndTest(testOk ? "done" : "stopped (see the log)"); return; }
			SetStatus($"test {testName}: {testCurrent}");
		}

		// (switched off some other way, eg the On / Off key: the test is over)
		private void WatchTest() {
			if ( testCurrent != null && !IsActive ) {
				testCurrent = null;
				TestResult = $"{testName}: switched off";
				Log($"{Name}: test {TestResult}");
			}
		}

		// a step's test button, its "B" (the plan, built but not run, in an ObjectBrowser: follow each step's Next), and its help
		private void TestButton(string name, Func<IState, IState> step, string help) {
			if ( ImGui.Button(name) ) StartTest(name, step);
			ImGui.SameLine();
			if ( ImGui.Button($"B##plan_{name}") ) Run_ObjectBrowser($"{Name} plan: {name}", step(State.NewState(() => { })));
			ImGui.SameLine();
			ImGui_HelpMarker(help);
		}

		/// <summary>The step tests: Key (for the memory server's /mapping/test?name=), the button's Name, the step, and what it does.</summary>
		public (string Key, string Name, Func<IState, IState> Step, string Help)[] Tests => [
			("stash", "Stash Everything", StashEverything, $"In the hideout: walk to the stash, open it, show the \"{DumpTab}\" tab, dump the backpack."),
			("pickmap", "Pick Map", PickNextMap, $"With the stash open: show the \"{MapsTab}\" tab, show tier {MapTier} (ActivateMapTier), take a map into the backpack."),
			("rollmap", "Roll Map", RollMap, $"With the stash and the inventory open and one map in the backpack: Orbs of Scouring and Alchemy from the \"{CurrencyTab}\" tab until it is a rare with no prohibited mods."),
			("selectmap", "Select Map Area", SelectMapArea, $"With the Map Device open: show the Map Loader for \"{MapArea}\" (close one showing another area; drag the Atlas to its area node and click it)."),
			("openmap", "Open Map", OpenMap, "With a map in the backpack: walk to the Map Device, open it, put the map in, Activate, wait for a new portal."),
		];

		/// <summary>Start the test whose Key or Name is name; false if there is none.</summary>
		public bool StartTest(string name) {
			var test = Tests.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
			if ( test.Step == null ) return false;
			StartTest(test.Name, test.Step);
			return true;
		}

		/// <summary>The test running (and the state it is in), or how the last one ended.</summary>
		public string TestStatus => testCurrent != null ? $"running {testName}: {testCurrent}" : TestResult != null ? $"last test: {TestResult}" : "no test has run";

		private void RenderTests() {
			if ( !ImGui.TreeNode("Test Steps") ) return;
			ImGui.TextWrapped("Each runs one step on its own, then switches the routine off. B: look at the step's whole plan (built, not run).");
			foreach ( var t in Tests ) TestButton(t.Name, t.Step, t.Help);
			if ( testCurrent != null ) {
				ImGui.Text($"Running {testName}: {testCurrent}");
				if ( ImGui.Button("Stop Test") ) StopTest("the Stop Test button");
			} else if ( TestResult != null ) ImGui.Text($"Last test: {TestResult}");
			ImGui.TreePop();
		}
	}
}
