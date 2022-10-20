using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtE {
	public static partial class Globals {

		public delegate void ActionDelegate();

		private static StateMachine Machine = new StateMachine();

		public static void Tick(long dt) {

			// Clear the prior frame
			D3DController.NewFrame();

			// Start a new frame
			ImGuiController.NewFrame(dt);

			// Advance all the states, which may call ImGui.Foo() functions to render things
			Machine.OnTick();

			// Render the ImGui layer to vertexes and Draw them to the GPU
			ImGuiController.Render(dt);

			// TODO: Render a Sprite layer?

			// Finalize the rendering to the screen
			D3DController.Render();

		}

		public static readonly Stopwatch Time = Stopwatch.StartNew();

		public static void Run(State s) => Machine.Add(s);
		public static void Run(Func<State, State> func) => Machine.Add(State.From(func));
		public static void Run(string label, Func<State, State> func) => Machine.Add(State.From(label, func));

		public static void Log(params string[] line) => Debug.WriteLine(string.Join(" ", line));

	}
}
