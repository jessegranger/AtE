using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static AtE.Globals;

namespace AtE {
	// public static implicit operator State(Func<State, State> func) => new Runner(func);
	// public static implicit operator Func<State, State>(State state) => (s) => { try { return state.OnTick(); } catch ( Exception ) { return null; } };

	internal class StateMachine : State {

		// each machine runs any number of states at once (in 'parallel' frame-wise)
		// when a machine is empty, it gets collected by the reaper
		public LinkedList<State> States;

		public State CurrentState => States.FirstOrDefault();

		public StateMachine(params State[] states) => States = new LinkedList<State>(states);

		public override string ToString() => string.Join(" while ", States.Select(s => $"({s})")) + (Next == null ? "" : $" then {Next.Name}");

		private static LinkedListNode<T> RemoveAndContinue<T>(LinkedList<T> list, LinkedListNode<T> node) {
			LinkedListNode<T> next = node.Next;
			list.Remove(node);
			return next;
		}

		/// <summary>
		/// Clear is a special state that clears all running states from a MultiState.
		/// </summary>
		/// <example>new StateMachine(
		///   new WalkTo(X),
		///   new ShootAt(Y, // will walk and shoot at the same time, when shoot is finished, clear the whole machine (cancel the walk)
		///     new StateMachine.Clear(this)) );
		///  </example>
		public class Clear : State {
			public Clear(State next) : base(next) { }
			public override State OnTick(long dt) => Next;
		}

		private Action<string> logDelegate;
		public void EnableLogging(Action<string> logger) => logDelegate = logger;
		public void DisableLogging() => logDelegate = null;
		private void Log(string s) => logDelegate?.Invoke($"{Time.Elapsed} {s}");

		public void Pause() => Paused = true;
		public void Resume() => Paused = false;
		public void TogglePause() => Paused = !Paused;
		private bool Paused = false;

		public override State OnTick(long dt) {
			if ( Paused ) {
				return this;
			}
			// Each State in the States list will be ticked each frame
			try {
				// iterate over the linked list of currently active states:
				LinkedListNode<State> curNode = States.First;
				while ( curNode != null ) {
					try {
						// each node in the linked list contains one State
						State curState = curNode.Value;
						// that state is ticked once per frame
						long tickStart = Time.ElapsedMilliseconds;
						State gotoState = curState.OnTick(dt);
						long elapsed = Time.ElapsedMilliseconds - tickStart;
						if ( false && elapsed > 100 ) {
							Log($"OnTick: {curState.Name} took {elapsed} ms, cancelling...");
							gotoState = null;
						}

						// the result can either terminate, replace, or continue, the State in this node
						if ( gotoState == null ) { // terminate the State in this node
							Log($"State Finished: {curState.Name}.");
							curNode = RemoveAndContinue(States, curNode); // unlink from the linked list
							continue;
						}
						if ( gotoState != curState ) { // replace the State in this node with gotoState
							gotoState = gotoState.OnEnter();
							Log($"State Changed: {curState.Name} to {gotoState.Name}");
							if ( gotoState.GetType() == typeof(Clear) ) {
								Cancel(except: curState); // call all OnAbort in State, except curState.OnAbort, because it just ended cleanly (as far as it knows)
								return gotoState.Next ?? Next;
							}
							curState.Next = null; // just in case
							curNode.Value = gotoState;
						}
					} catch ( Exception e ) {
						Log(e.StackTrace);
					}
					curNode = curNode.Next; // loop over the whole list
				}
			} finally {
			}
			return States.Count == 0 ? Next : this;
		}
		public void Cancel(State except = null) {
			foreach ( State s in States.Where(s => s != except) ) {
				s.OnCancel();
			}
			States.Clear();
		}
		public void Add(State state) {
			if ( state == null ) return;
			Log($"State Added: {state.Name}");
			States.AddLast(States.Count == 0 ? state.OnEnter() : state);
		}
		public void Remove(State state) => States.Remove(state);
		public void Remove(Type stateType) {
			LinkedListNode<State> cur = States.First;
			while ( cur != null ) {
				cur = cur.Value.GetType() == stateType ? RemoveAndContinue(States, cur) : cur.Next;
			}
		}

		public bool HasState(Type stateType) => States.Any(s => s.GetType() == stateType);
		public bool HasState(string stateName) => States.Any(s => s.Name.Equals(stateName));
	}
}
