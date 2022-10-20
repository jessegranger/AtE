using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AtE {
		// A Runner is a special State that uses a Func<State, State> to convert the Func into a class object with a State interface
		internal class Runner : State {
			readonly Func<State, State> F;
			public Runner(Func<State, State> func) => F = func;
			public override State OnTick() => F(this);
			public override string Name => name;
			private readonly string name = "...";
			public Runner(string name, Func<State, State> func) : this(func) => this.name = name;
			// allowing to construct with .Next is only slightly useful, the real .Next is the return value of func()
			// but sometimes it's helpful when building chains to have .Next attached to this Runner state
			public Runner(string name, Func<State, State> func, State next) : this(name, func) => this.Next = next;
		}
}
