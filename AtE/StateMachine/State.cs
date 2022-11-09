using System;
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

	/// <summary>
	/// Most classes just want to extend from State,
	/// but if you really need to be State-like and have another base class.
	/// Any class can implement this, and participate in Run(), StateMachines, et al
	/// </summary>
	public interface IState {
		/// <summary>
		/// The display name for the State.
		/// </summary>
		string Name { get; }
		/// <summary>
		/// This is called once by a StateMachine before the first OnTick().
		/// Usually return `this` to continue, but can return a different State here,
		/// as a way to cancel/pre-empt.
		/// </summary>
		/// <returns>A new State to replace this one, or this.</returns>
		IState OnEnter();
		/// <summary>
		/// This is called every frame by any StateMachine to which it is Add()'ed.
		/// </summary>
		/// <param name="dt">The ms elapsed since the previous call to OnTick</param>
		/// <returns>A new State to replace this one, or this.</returns>
		IState OnTick(long dt);
		/// <summary>
		/// Called if a StateMachine needs to cancel/pre-empt this operation.
		/// The State should try to unwind cleanly if possible.
		/// </summary>
		void OnCancel();
		/// <summary>
		/// Once this State's work is done (if it ever is), this public State lets others configure what State to transition to.
		/// This means, for a typical State, OnTick should `return Next;` when all other work is finished.
		/// </summary>
		IState Next { get; set; }
		/// <summary>
		/// The last State at the end of the current sequence of Next.Next.Next, (aka a "plan").
		/// Appending to a plan means assigning to `Tail().Next`.
		/// </summary>
		/// <returns>The first State where Next is null, in the chain of Next States.</returns>
		IState Tail();
	}
	public abstract class State : IState {

		// 'Next' defines the default State we will go to when this State is complete.
		// This value is just a suggestion, to construct "plan" sequences,
		// the real transition happens as a result of what gets returned by OnTick.
		public IState Next { get; set; } = null;

		public State(IState next = null) => Next = next;

		// OnEnter gets called once (by a StateMachine) before the first call to OnTick.
		public virtual IState OnEnter() => this;

		// OnTick gets called every frame (by a StateMachine), and should return the next State to continue with (usually itself).
		public virtual IState OnTick(long dt) => this;

		// OnCancel gets called (by a StateMachine), to ask a State to clean up any incomplete work immediately (before returning).
		public virtual void OnCancel() { }

		public virtual IState Then(IState next) {
			Next = next;
			return Tail();
		}

		public IState Tail() => Next == null ? this : Next.Tail();

		// A friendly name for the State, the class name by default.
		public virtual string Name => GetType().Name.Split('.').Last();

		// A verbose description of the State, that includes the Name of the next State (if known).
		public override string ToString() => $"{Name}{(Next == null ? "" : " then " + Next.Name)}";

		// You can create a new State using any Func<State, State>
		public static IState From(string label, Func<IState, long, IState> func, IState next = null) => new Runner(label, func, next);
		public static IState From(Func<IState, long, IState> func) => new Runner(func);
		public static IState From(Action func, IState next = null) => new ActionState(func, next);

		public static IState WaitFor(uint duration, Func<bool> predicate, IState next, IState fail) {
			long elapsed = 0;
			return State.From($"WaitFor({duration})", (state, dt) =>
				(elapsed += dt) > duration ? fail :
					predicate() ? next :
					state
			);
		}
		private class ActionState : State {
			public readonly Action Act;
			public ActionState(Action action, IState next = null) : base(next) => Act = action;
			public override IState OnTick(long dt) {
				Act?.Invoke();
				return Next;
			}
		}
	}

	public class Delay : State // Delay is a State that waits for a fixed number of milliseconds
	{
		public long Remaining;
		public Delay(uint ms, IState next = null) : base(next) => this.Remaining = ms;
		public override IState OnTick(long dt) {
			Remaining -= dt;
			return Remaining > 0 ? this : Next;
		}
		public override string Name => $"Delay({Remaining})";
	}

	class InputState : State {
		protected InputState(IState next = null) : base(next) { }
		public override IState OnTick(long dt) => Next;
	}

	class KeyState : InputState {
		public readonly Keys Key;
		protected KeyState(Keys key, IState next = null) : base(next) => Key = key;
	}

	class KeyDown : KeyState {
		public KeyDown(Keys key, State next = null) : base(key, next) { }
		public override IState OnTick(long dt) {
			// if ( !AllowInputInChatBox && ChatIsOpen() ) return Next;
			if( dt > 0 && PoEMemory.TargetHasFocus ) {
				SendInput(INPUT_KeyDown(Key));
				return Next;
			}
			return this;
		}
		public override string Name => $"KeyDown({Key})";
	}

	class KeyUp : KeyState {
		public KeyUp(Keys key, State next = null) : base(key, next) { }
		public override IState OnTick(long dt) {
			// if ( !AllowInputInChatBox && ChatIsOpen() ) return Next;
			if( dt > 0 && PoEMemory.TargetHasFocus ) {
				SendInput(INPUT_KeyUp(Key));
				return Next;
			}
			return this;
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

		public override IState OnEnter() {
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
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !PoEMemory.TargetHasFocus ) return Next;
			if ( X == 0 && Y == 0 ) {
				Log($"Warn: MoveMouse to (0,0) attempted, skipped.");
				return Next;
			}
			SendInput(INPUT_MouseMove(new Vector2(X, Y)));
			return Next;
		}
	}
	class LeftMouseDown : InputState {
		public LeftMouseDown(State next = null) : base(next) { }
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !PoEMemory.TargetHasFocus ) return Next;
			SendInput(INPUT_Mouse(MouseFlag.LeftDown));
			return Next;
		}
	}

	class LeftMouseUp : InputState {
		public LeftMouseUp(State next = null) : base(next) { }
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !PoEMemory.TargetHasFocus ) return Next;
			SendInput(INPUT_Mouse(MouseFlag.LeftUp));
			return Next;
		}
	}

	class LeftClick : InputState {
		public LeftClick(uint duration, uint count, State next = null) : base(
			count == 0 ? next
			: new LeftMouseDown(new Delay(duration, new LeftMouseUp(
				count > 1 ? new Delay(100, new LeftClick(duration, count - 1, next))
				: next)))) { }
		public override IState OnEnter() => Next;
	}

	class LeftClickAt : InputState {
		// public LeftClickAt(Element elem, uint duration, uint count, State next = null) : this(elem?.GetClientRect().Center ?? Vector2.Zero, duration, count, next) { }
		public LeftClickAt(Vector2 pos, uint duration, uint count, State next = null) : this(pos.X, pos.Y, duration, count, next) { }
		public LeftClickAt(float x, float y, uint duration, uint count, State next = null) : base(
				new MoveMouse(x, y, new Delay(duration, new LeftClick(duration, count, next)))) { }
	}

	class RightMouseDown : InputState {
		public RightMouseDown(State next = null) : base(next) { }
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !PoEMemory.TargetHasFocus ) return Next;
			SendInput(INPUT_Mouse(MouseFlag.RightDown));
			return Next;
		}
	}

	class RightMouseUp : InputState {
		public RightMouseUp(State next = null) : base(next) { }
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !PoEMemory.TargetHasFocus ) return Next;
			SendInput(INPUT_Mouse(MouseFlag.RightUp));
			return Next;
		}
	}

	class RightClick : InputState {
		public RightClick(uint duration, State next = null) : base(
				new RightMouseDown(new Delay(duration, new RightMouseUp(next)))) { }
		public override IState OnEnter() => Next;
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
