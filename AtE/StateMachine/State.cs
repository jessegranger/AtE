﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;
using static AtE.Win32;

namespace AtE {
	public abstract class State {

		// 'Next' defines the default State we will go to when this State is complete.
		// This value is just a suggestion, the real value is what gets returned by OnTick
		public State Next = null;
		// 'Fail' defines the State to go to if there is any kind of exception
		public State Fail = null;

		public State(State next = null) => Next = next;

		// OnEnter gets called once (by a StateMachine) before the first call to OnTick.
		public virtual State OnEnter() => this;

		// OnTick gets called every frame (by a StateMachine), and should return the next State to continue with (usually itself).
		public virtual State OnTick() => this;

		// OnCancel gets called (by a StateMachine), to ask a State to clean up any incomplete work immediately (before returning).
		public virtual void OnCancel() { }

		public virtual State Then(State next) {
			Next = next;
			return Tail();
		}
		public virtual State Then(params State[] next) {
			State cursor = this;
			foreach ( State s in next ) {
				cursor = cursor.Then(s);
			}
			return cursor.Tail();
		}
		public virtual State Then(Action action) {
			Next = State.From(action);
			return Tail();
		}
		public State Tail() {
			if ( Next == null ) return this;
			else return Next.Tail();
		}

		// A friendly name for the State, the class name by default.
		public virtual string Name => GetType().Name.Split('.').Last();

		// A verbose description of the State, that includes the Name of the next State (if known).
		public override string ToString() => $"{Name}{(Next == null ? "" : " then " + Next.Name)}";
		public virtual string Describe() => $"{Name}{(Next == null ? " end" : " then " + Next.Describe())}";

		// You can create a new State using any Func<State, State>
		public static State From(string label, Func<State, State> func, State next = null) => new Runner(label, func, next);
		public static State From(Func<State, State> func) => new Runner(func);
		public static State From(Action func, State next = null) => new ActionState(func, next);

		public static State WaitFor(uint duration, Func<bool> predicate, State next, State fail) {
			long started = Time.ElapsedMilliseconds;
			return State.From($"WaitFor({duration})", (state) =>
				(Time.ElapsedMilliseconds - started) > duration ? fail :
					predicate() ? next :
					state
			);
		}
		private class ActionState : State {
			public readonly Action Act;
			public ActionState(Action action, State next = null) : base(next) => Act = action;
			public override State OnTick() {
				Act?.Invoke();
				return Next;
			}
		}
	}

	public class Delay : State // Delay is a State that waits for a fixed number of milliseconds
	{
		private long started;
		readonly uint ms;
		public Delay(uint ms, State next = null) : base(next) => this.ms = ms;
		public override State OnEnter() {
			started = Time.ElapsedMilliseconds;
			return this;
		}
		public override State OnTick() => (Time.ElapsedMilliseconds - started) >= ms ? Next : (this);
		public override string Name => $"Delay({ms})";
	}

	class InputState : State {
		protected InputState(State next = null) : base(next) { }
		public override State OnTick() => Next;
	}

	class KeyState : InputState {
		public readonly Keys Key;
		protected KeyState(Keys key, State next = null) : base(next) => Key = key;
	}

	class KeyDown : KeyState {
		public KeyDown(Keys key, State next = null) : base(key, next) { }
		public override State OnTick() {
			// if ( !AllowInputInChatBox && ChatIsOpen() ) return Next;
			Input.Dispatch(Input.KeyDownMessage(Key));
			return Next;
		}
		public override string Name => $"KeyDown({Key})";
	}

	class KeyUp : KeyState {
		public KeyUp(Keys key, State next = null) : base(key, next) { }
		public override State OnTick() {
			// if ( !AllowInputInChatBox && ChatIsOpen() ) return Next;
			Input.Dispatch(Input.KeyUpMessage(Key));
			return Next;
		}
		public override string Name => $"KeyUp({Key})";
	}

	class PressKey : KeyState {
		private static Dictionary<Keys, long> lastPressTime = new Dictionary<Keys, long>();
		private readonly long throttle = long.MaxValue;
		public PressKey(Keys key, uint duration, State next = null) : base(key,
				new KeyDown(key, new Delay(duration, new KeyUp(key, next)))) { }
		public PressKey(Keys key, uint duration, long throttle, State next = null) : base(key,
				new KeyDown(key, new Delay(duration, new KeyUp(key, next)))) {
			this.throttle = throttle;
		}

		public override State OnEnter() {
			lastPressTime.TryGetValue(Key, out long lastPress);
			long now = Time.ElapsedMilliseconds;
			long elapsed = now - lastPress;
			if( throttle != long.MaxValue && elapsed < throttle ) {
				Log($"Key {Key} throttled. {elapsed} < {throttle}");
				return null;
			}
			lastPressTime[Key] = Time.ElapsedMilliseconds;
			return Next;
		}
	}

	class MoveMouse : InputState {
		public readonly float X;
		public readonly float Y;
		public MoveMouse(float x, float y, State next = null) : base(next) {
			X = x; Y = y;
		}
		public MoveMouse(Vector2 pos, State next = null) : this(pos.X , pos.Y, next) { }
		// public MoveMouse(Element label, State next = null) : this(label?.GetClientRect().Center ?? Vector2.Zero, next) { }
		public override State OnTick() {
			if ( X == 0 && Y == 0 ) {
				Log($"Warn: MoveMouse to (0,0) attempted, skipped.");
				return Next;
			}
			Input.Dispatch(Input.MouseMoveTo(new Vector2(X, Y)));
			return Next;
		}
	}
	class LeftMouseDown : InputState {
		public LeftMouseDown(State next = null) : base(next) { }
		public override State OnTick() {
			Input.Dispatch(Input.MouseMessage(MouseFlag.LeftDown));
			return Next;
		}
	}

	class LeftMouseUp : InputState {
		public LeftMouseUp(State next = null) : base(next) { }
		public override State OnTick() {
			Input.Dispatch(Input.MouseMessage(MouseFlag.LeftUp));
			return Next;
		}
	}

	class LeftClick : InputState {
		public LeftClick(uint duration, uint count, State next = null) : base(
			count == 0 ? next
			: new LeftMouseDown(new Delay(duration, new LeftMouseUp(
				count > 1 ? new Delay(100, new LeftClick(duration, count - 1, next))
				: next)))) { }
		public override State OnEnter() => Next;
	}

	class LeftClickAt : InputState {
		// public LeftClickAt(Element elem, uint duration, uint count, State next = null) : this(elem?.GetClientRect().Center ?? Vector2.Zero, duration, count, next) { }
		public LeftClickAt(Vector2 pos, uint duration, uint count, State next = null) : this(pos.X, pos.Y, duration, count, next) { }
		public LeftClickAt(float x, float y, uint duration, uint count, State next = null) : base(
				new MoveMouse(x, y, new Delay(duration, new LeftClick(duration, count, next)))) { }
	}

	class RightMouseDown : InputState {
		public RightMouseDown(State next = null) : base(next) { }
		public override State OnTick() {
			Input.Dispatch(Input.MouseMessage(MouseFlag.RightDown));
			return Next;
		}
	}

	class RightMouseUp : InputState {
		public RightMouseUp(State next = null) : base(next) { }
		public override State OnTick() {
			Input.Dispatch(Input.MouseMessage(MouseFlag.RightUp));
			return Next;
		}
	}

	class RightClick : InputState {
		public RightClick(uint duration, State next = null) : base(
				new RightMouseDown(new Delay(duration, new RightMouseUp(next)))) { }
		public override State OnEnter() => Next;
	}

	class RightClickAt : InputState {
		// public RightClickAt(Element item, uint duration, State next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public RightClickAt(Vector2 pos, uint duration, State next = null) : this(pos.X, pos.Y, duration, next) { }
		public RightClickAt(float x, float y, uint duration, State next = null) : base(
				new MoveMouse(x, y, new Delay(duration, new RightMouseDown(new Delay(duration, new RightMouseUp(next)))))) { }
	}
	class CtrlRightClickAt : InputState {
		// public CtrlRightClickAt(Element item, uint duration, State next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public CtrlRightClickAt(Vector2 pos, uint duration, State next = null) : this(pos.X, pos.Y, duration, next) { }
		public CtrlRightClickAt(float x, float y, uint duration, State next = null) : base(
				new MoveMouse(x, y, new Delay(duration, 
					new KeyDown(Keys.LControlKey, new Delay(duration,
						new RightMouseDown(new Delay(duration,
							new RightMouseUp(new Delay(duration,
								new KeyUp(Keys.LControlKey, next)))))))))) { }
	}
	class CtrlLeftClickAt : InputState {
		// public CtrlLeftClickAt(Element item, uint duration, State next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public CtrlLeftClickAt(Vector2 pos, uint duration, State next = null) : this(pos.X, pos.Y, duration, next) { }
		public CtrlLeftClickAt(float x, float y, uint duration, State next = null) : base(
				new MoveMouse(x, y, new Delay(duration, 
					new KeyDown(Keys.LControlKey, new Delay(duration,
						new LeftMouseDown(new Delay(duration,
							new LeftMouseUp(new Delay(duration,
								new KeyUp(Keys.LControlKey, next)))))))))) { }
	}
	class ShiftLeftClickAt : InputState {
		// public ShiftLeftClickAt(Element item, uint duration, State next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public ShiftLeftClickAt(Vector2 pos, uint duration, State next = null) : this(pos.X, pos.Y, duration, next) { }
		public ShiftLeftClickAt(float x, float y, uint duration, State next = null) : base(
				new MoveMouse(x, y, new Delay(duration, 
					new KeyDown(Keys.LShiftKey, new Delay(duration,
						new LeftMouseDown(new Delay(duration,
							new LeftMouseUp(new Delay(duration,
								new KeyUp(Keys.LShiftKey, next)))))))))) { }

	}
}
