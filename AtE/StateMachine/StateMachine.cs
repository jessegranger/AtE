using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {
	// public static implicit operator State(Func<State, State> func) => new Runner(func);
	// public static implicit operator Func<State, State>(State state) => (s) => { try { return state.OnTick(); } catch ( Exception ) { return null; } };

	public static partial class Globals {
		public static void Run(IState s) => StateMachine.DefaultMachine.Add(s);
		public static void Run(string label, Func<IState, long, IState> func) => StateMachine.DefaultMachine.Add(State.From(label, func));
		public static void RunForever(string label, Action action) => Run(label, (self, dt) => { action(); return self; });
		public static void RunForever(string label, Action<long> action) => Run(label, (self, dt) => { action(dt); return self; });
		public static void RunFor(long duration_ms, string label, Action action) {
			Run(label, (self, dt) => {
				if ( (duration_ms -= dt) > 0 ) {
					action();
					return self;
				} else {
					return null;
				}
			});
		}
		// TODO: RunWhile
	}

	internal class StateMachine : State {

		public bool Paused = false;

		public StateMachine(params IState[] states) {
			States = new LinkedList<IState>(states);
			Next = this; // state machines are states that run forever, unless you manually assign Next
		}

		// there is one default static machine that runs all the other machinery
		public static readonly StateMachine DefaultMachine = new StateMachine();

		// each machine runs any number of states at once
		public readonly LinkedList<IState> States; // OnTick below will tick the states in this list, and manage the results


		public override string ToString() => GetType().Name + "[" + string.Join(",", States.Select(s => s.Name)) + "]";

		/// <summary>
		/// Remove an item while iterating a linked list and continue the iteration.
		/// </summary>
		private static LinkedListNode<T> RemoveAndContinue<T>(LinkedList<T> list, LinkedListNode<T> node) {
			LinkedListNode<T> next = node.Next;
			list.Remove(node);
			return next;
		}

		public override IState OnTick(long dt) {
			// Each State in the States list will be ticked once each frame
			if ( Paused ) {
				return this;
			}
			// iterate over the linked list of currently active states:
			LinkedListNode<IState> curNode = States.First;
			while ( curNode != null ) {
				try {
					// each node in the linked list contains one State
					IState curState = curNode.Value;
					// that state is ticked once per frame
					var tickStart = Time.Elapsed;
					IState gotoState = curState.OnTick(dt);
					var elapsed = Time.Elapsed - tickStart;

					// the result, gotoState can either terminate, replace, or continue, the current state

					// terminate
					if ( gotoState == null ) {
						Log($"State Finished: {curState.Name}.");
						curNode = RemoveAndContinue(States, curNode); // unlink from the linked list
						continue;
					}

					// replace
					if ( gotoState != curState ) {
						gotoState = gotoState.OnEnter();
						Log($"State Changed: {curState.Name} to {gotoState.Name}");
						curState.Next = null; // remove the forward reference from the old state
						curNode.Value = gotoState; // gotoState often has captured it, and brings it back, but that's up to the States
						// fall through to continue
					}
					// continue
					
				} catch ( Exception e ) {
					Log(e.Message);
					Log(e.StackTrace);
					throw;
				}
				curNode = curNode.Next; // loop over the whole list
			}
			return States.Count == 0 ? Next : this;
		}
		/// <summary>
		/// Call state.OnCancel() and remove it from this StateMachine.
		/// </summary>
		/// <param name="state"></param>
		public void Cancel(IState state) {
			state.OnCancel();
			Remove(state);
		}
		public void CancelExcept(IState except = null) {
			foreach ( IState s in States.Where(s => s != except) ) {
				s.OnCancel();
			}
			States.Clear();
		}
		public void CancelAll() {
			foreach ( IState s in States ) {
				s.OnCancel();
			}
			States.Clear();
		}
		public void Add(IState state) {
			if ( state == null ) return;
			Log($"State Added: {state.Name}");
			States.AddLast(States.Count == 0 ? state.OnEnter() : state);
			// call OnEnter if it's the first state bc it will start immediately
		}
		/// <summary>
		/// Remove immediately without cancelling.
		/// state.OnCancel() is not called.
		/// </summary>
		/// <param name="state"></param>
		public void Remove(IState state) {
			if ( state != null ) {
				States.Remove(state);
			}
		}
		public void RemoveAll(Type removeType) {
			LinkedListNode<IState> cursor = States.First;
			while ( cursor != null ) {
				cursor = cursor.Value.GetType() == removeType ? RemoveAndContinue(States, cursor) : cursor.Next;
			}
		}

		public bool HasState(Type stateType) => States.Any(s => s.GetType() == stateType);
		public bool HasState(string stateName) => States.Any(s => s.Name.Equals(stateName));
	}
}
