using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Most classes just want to extend from IState,
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
		public static IState NewState(string label, Func<IState, long, IState> func, IState next = null) => new Runner(label, func, next);
		public static IState NewState(Func<IState, long, IState> func) => new Runner(func);
		public static IState NewState(Action func, IState next = null) => new ActionState(func, next);

		public static IState WaitFor(uint duration, Func<bool> predicate, IState next, IState fail) {
			long elapsed = 0;
			return State.NewState($"WaitFor({duration})", (state, dt) =>
				(elapsed += dt) > duration ? fail :
					predicate() ? next :
					state
			);
		}

		// an Empty State is a placeholder, useful for being the head of dynamic sequences of states, for example
		public class Empty : State {
			public Empty(IState next = null) : base(next) { }
			public override IState OnTick(long dt) => Next;
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

	public static partial class Globals {
		/// <summary>
		/// Make the game the foreground window, then next. Asks again every RetryMs (Windows can refuse, eg while the
		/// overlay is still taking the mouse) until the game has the focus, then waits SettleMs so the game's first input
		/// isn't spent on taking the focus. Stops the plan (null) if the game still isn't focused after TimeoutMs.
		/// </summary>
		public static IState ForceGameFocus(IState next) => new ForceGameFocusState(next);

		private sealed class ForceGameFocusState : State {
			private const long RetryMs = 250, TimeoutMs = 2000;
			private const uint SettleMs = 100;
			private long elapsed, nextTry;
			public ForceGameFocusState(IState next) : base(next) { }
			public override string Name => "ForceGameFocus";
			private bool asked; // (only wait SettleMs if the focus had to be taken)
			public override IState OnEnter() {
				elapsed = 0;
				nextTry = 0;
				asked = false;
				return this;
			}
			public override IState OnTick(long dt) {
				if ( PoEMemory.TargetHasFocus ) return asked ? new Delay(SettleMs, Next) : Next;
				elapsed += dt;
				if ( elapsed > TimeoutMs ) {
					Log($"ForceGameFocus: the game did not take the focus in {TimeoutMs} ms: stopped.");
					Notify("Could not focus the game: stopped.");
					return null;
				}
				if ( elapsed >= nextTry ) {
					nextTry = elapsed + RetryMs;
					asked = true;
					Platform.Current.Memory.TryFocusTarget();
				}
				return this;
			}
		}

		/// <summary>
		/// True while the mouse can be used on the game: the game has the focus, or the overlay has it (it hands the focus
		/// straight back to the game, see Overlay.UpdateKeyboardFocus; and a plan started from one of its buttons begins
		/// while it has it).
		/// </summary>
		public static bool GameFocused => PoEMemory.TargetHasFocus || Overlay.HasFocus;

		/// <summary>True while the game is paused by an Escape of ours, because it lost the focus (see WaitForFocus).</summary>
		public static bool GamePausedForFocus { get; private set; }

		/// <summary>
		/// Wait for the game to have the focus (see GameFocused), then next. While it doesn't, the game is paused: if it
		/// isn't paused already, Escape is sent (keys can be posted to it in the background), and once the focus is back,
		/// Escape again to unpause (if the escape menu is still open; and only if it was ours). Every mouse state waits with
		/// this (MoveMouse, LeftMouseDown, ...: see MouseButtonDown).
		///
		/// The pausing is done by one state on the default Machine (see PauseUntilFocused), not by this one: the chain this
		/// is in may not be ticked while the game is paused (eg, CombatDriver waits while the escape menu is open), and
		/// there may be several of these waiting at once.
		/// </summary>
		public static IState WaitForFocus(IState next) => new WaitForFocusState(next);

		/// <summary>Pause the game until it has the focus again (see WaitForFocus), if it doesn't have it now. Safe to call every frame.</summary>
		public static void PauseUntilFocused() {
			if ( GameFocused || pauserRunning ) return;
			pauserRunning = true;
			Run(new PauseUntilFocusedState());
		}
		private static bool pauserRunning;

		private sealed class WaitForFocusState : State {
			private bool waited;
			public WaitForFocusState(IState next) : base(next) { }
			public override string Name => "WaitForFocus";
			public override IState OnTick(long dt) {
				// (focused, and the pauser has sent its Escape to unpause: a moment for the escape menu to close, then go on)
				if ( GameFocused && !pauserRunning ) return waited ? new Delay(100, Next) : Next;
				waited = true;
				PauseUntilFocused();
				return this;
			}
		}

		private sealed class PauseUntilFocusedState : State {
			private const long RepeatMs = 200;
			private long sinceEscape;
			private bool pressing; // (sending Escape every RepeatMs until the escape menu shows)
			public override string Name => "PauseUntilFocused";
			private static bool EscapeMenuOpen => PoEMemory.GameRoot?.EscapeState?.Menu?.IsVisible ?? false;
			private static void SendEscape() {
				// (straight to the game, not Inputs.LastEscapeMs: CombatDriver closes an escape menu that one of those opened)
				Platform.Current.Input.KeyDown(VKey.Escape);
				Platform.Current.Input.KeyUp(VKey.Escape);
			}
			public override IState OnTick(long dt) {
				if ( dt == 0 ) return this;
				if ( !PoEMemory.IsAttached ) { GamePausedForFocus = false; pauserRunning = false; return null; }
				if ( GameFocused ) {
					// Escape to unpause only if the escape menu is (still) open: not if the focus came back by a click on its
					// Resume Game (which closed it), or it never opened
					if ( GamePausedForFocus && EscapeMenuOpen ) { Log("PauseUntilFocused: the game has the focus again: Escape to unpause"); SendEscape(); }
					GamePausedForFocus = false;
					pauserRunning = false;
					return null;
				}
				if ( !pressing && !GamePausedForFocus ) {
					// (not if the game is paused by something else, eg the augury window: Escape would close that)
					bool paused = PoEMemory.GameRoot?.InGameState?.IsPaused ?? true;
					if ( paused || EscapeMenuOpen ) return this;
					Log("PauseUntilFocused: the game lost the focus: Escape until the escape menu shows");
					pressing = true;
					GamePausedForFocus = true;
					sinceEscape = RepeatMs; // (the first at once)
				}
				if ( pressing ) {
					// each Escape closes an open panel (the stash, the inventory, ...) first: again, until the menu is up
					if ( EscapeMenuOpen ) { pressing = false; Log("PauseUntilFocused: the escape menu is open: paused"); return this; }
					if ( (sinceEscape += dt) >= RepeatMs && CanSendKey(VKey.Escape) ) { sinceEscape = 0; SendEscape(); }
				}
				return this;
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
		public readonly VKey Key;
		protected KeyState(VKey key, IState next = null) : base(next) => Key = key;
	}

	class KeyDown : KeyState {
		public KeyDown(VKey key, IState next = null) : base(key, next) { }
		public override IState OnTick(long dt) {
			if( dt > 0 && CanSendKey(Key) ) {
				if ( Key == VKey.Escape ) Inputs.LastEscapeMs = Time.ElapsedMilliseconds;
				Platform.Current.Input.KeyDown(Key);
				return Next;
			}
			return this;
		}
		public override string Name => $"KeyDown({Key})";
	}

	class KeyUp : KeyState {
		public KeyUp(VKey key, IState next = null) : base(key, next) { }
		public override IState OnTick(long dt) {
			// if ( !AllowInputInChatBox && ChatIsOpen() ) return Next;
			if( dt > 0 && CanSendKey(Key) ) {
				Platform.Current.Input.KeyUp(Key);
				return Next;
			}
			return this;
		}
		public override string Name => $"KeyUp({Key})";
	}

	class PressKey : KeyState {
		private static Dictionary<VKey, long> lastPressTime = [];
		private readonly long throttle = long.MaxValue;
		public PressKey(VKey key, uint duration, IState next = null) : base(key,
				new KeyDown(key, new Delay(duration, new KeyUp(key, next)))) { }
		public PressKey(VKey key, uint duration, long throttle, IState next = null) : base(key,
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
		public MoveMouse(float x, float y, IState next = null) : base(next) {
			X = x; Y = y;
		}
		public MoveMouse(Vector2 pos, IState next = null) : this(pos.X , pos.Y, next) { }
		public override string Name => $"MoveMouse({X:F0}, {Y:F0})";
		/// <summary>Where the last MoveMouse moved to, and whether the overlay had the mouse then (so the game did not see the move).</summary>
		public static Vector2 LastPos;
		public static bool LastMoveTakenByOverlay;
		// public MoveMouse(Element label, IState next = null) : this(label?.GetClientRect().Center ?? Vector2.Zero, next) { }
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !GameFocused ) return WaitForFocus(this);
			if ( X == 0 && Y == 0 ) {
				Log($"Warn: MoveMouse to (0,0) attempted, skipped.");
				return Next;
			}
			Platform.Current.Input.MouseMove(new Vector2(X, Y));
			LastPos = new Vector2(X, Y);
			// (the overlay had the mouse: the game was not told of this move, see MouseButtonDown)
			LastMoveTakenByOverlay = !Overlay.IsClickThrough;
			return Next;
		}
	}
	/// <summary>
	/// A mouse button pressed down. It waits for the game to have the focus (WaitForFocus, which pauses the game until
	/// then), and then (up to MaxWaitMs) until clicks go through the overlay to the game: the overlay takes the mouse while
	/// it is over one of its windows, and gives it back a frame or two after the mouse leaves (ImGui's WantCaptureMouse is
	/// a frame behind), so a click right after the mouse moves away from an overlay button (eg, the first click of a plan
	/// started from one) would land on the overlay.
	///
	/// A held button (CancelIfReleased, see MouseHold): if a MouseButtonUp of the same button went out while this was
	/// still waiting, it is not pressed at all (else the release would come first, and the button stay down).
	/// </summary>
	class MouseButtonDown : InputState {
		public const long MaxWaitMs = 500;
		public readonly MouseBtn Button;
		public bool CancelIfReleased;
		private long waited;
		private int removes; // moves sent again, after the overlay let the mouse through
		// (counted when made, not on its first tick: it is often queued behind a MoveMouse, and a release going out in
		// between, eg the PathFollower stopping, was taken for one before it, so the press went out with nothing left to let
		// go of it: the player kept walking, and after a stop the button stayed down)
		private readonly long releasesAtStart;
		public MouseButtonDown(MouseBtn button, IState next = null) : base(next) {
			Button = button;
			releasesAtStart = MouseButtonUp.Releases(button);
		}
		public override string Name => $"MouseDown({Button})";
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			if ( !GameFocused ) return WaitForFocus(this);
			if ( WaitForClickThrough(dt) ) return this;
			if ( CancelIfReleased && MouseButtonUp.Releases(Button) != releasesAtStart ) {
				Log($"{Name}: let go before it went out: not pressed");
				return Next;
			}
			Platform.Current.Input.MouseButton(Button, true);
			return Next;
		}
		// true while it should wait: for the overlay to let the click through, and then (if the overlay had the mouse when
		// it was moved, so the game did not see the move) for the move to be sent again: next to the spot, then on it
		private bool WaitForClickThrough(long dt) {
			if ( !Overlay.IsClickThrough ) {
				if ( (waited += dt) <= MaxWaitMs ) return true;
				Log($"Warn: the overlay still takes the mouse after {MaxWaitMs} ms ({Overlay.CaptureReason ?? "reason unknown"}): clicking anyway.");
				return false;
			}
			if ( MoveMouse.LastMoveTakenByOverlay && removes < 2 ) {
				var at = MoveMouse.LastPos + (removes == 0 ? new Vector2(1, 1) : Vector2.Zero);
				Platform.Current.Input.MouseMove(at);
				Log($"{Name}: moving the mouse again to {at.X:F0}, {at.Y:F0} (the game did not see the move)");
				if ( ++removes == 2 ) MoveMouse.LastMoveTakenByOverlay = false;
				return true;
			}
			return false;
		}
	}

	/// <summary>
	/// A mouse button let go. This one does not wait for the focus: a button left down while another window is in front
	/// would drag in that window.
	/// </summary>
	class MouseButtonUp : InputState {
		public readonly MouseBtn Button;
		private static readonly Dictionary<MouseBtn, long> releases = [];
		/// <summary>How many times button has been let go (see MouseButtonDown.CancelIfReleased).</summary>
		public static long Releases(MouseBtn button) => releases.TryGetValue(button, out long n) ? n : 0;
		public MouseButtonUp(MouseBtn button, IState next = null) : base(next) => Button = button;
		public override string Name => $"MouseUp({Button})";
		public override IState OnTick(long dt) {
			if ( dt == 0 ) return this;
			Platform.Current.Input.MouseButton(Button, false);
			releases[Button] = Releases(Button) + 1;
			return Next;
		}
	}

	/// <summary>
	/// Pressing and letting go of a mouse button as separate plans (eg, walking holds the left button down while it steers
	/// with MoveMouse): Press's button is not pressed if Release goes out before it does (it may wait for the focus).
	/// </summary>
	public static class MouseHold {
		public static IState Press(MouseBtn button, IState next = null) => new MouseButtonDown(button, next) { CancelIfReleased = true };
		public static IState Release(MouseBtn button, IState next = null) => new MouseButtonUp(button, next);
	}

	class LeftMouseDown : MouseButtonDown {
		public LeftMouseDown(IState next = null) : base(MouseBtn.Left, next) { }
	}

	class LeftMouseUp : MouseButtonUp {
		public LeftMouseUp(IState next = null) : base(MouseBtn.Left, next) { }
	}

	class LeftClick : InputState {
		public LeftClick(uint duration, uint count, IState next = null) : base(
			count == 0 ? next
			: new LeftMouseDown(new Delay(duration, new LeftMouseUp(
				count > 1 ? new Delay(100, new LeftClick(duration, count - 1, next))
				: next)))) { }
		public override IState OnEnter() => Next;
	}

	class LeftClickAt : InputState {
		public override string Name => $"LeftClickAt({(Next as MoveMouse)?.X ?? 0}, {(Next as MoveMouse)?.Y ?? 0})";
		public LeftClickAt(RectangleF rect, uint duration, uint count, IState next = null) : this(Center(rect), duration, count, next) { }
		public LeftClickAt(Vector2 pos, uint duration, uint count, IState next = null) : this(pos.X, pos.Y, duration, count, next) { }
		public LeftClickAt(float x, float y, uint duration, uint count, IState next = null) : base(
			(x == 0f && y == 0f) ? next
			: new MoveMouse(x, y, new Delay(duration, new LeftClick(duration, count, next)))) { }
	}

	class RightMouseDown : MouseButtonDown {
		public RightMouseDown(IState next = null) : base(MouseBtn.Right, next) { }
	}

	class RightMouseUp : MouseButtonUp {
		public RightMouseUp(IState next = null) : base(MouseBtn.Right, next) { }
	}

	class RightClick : InputState {
		public RightClick(uint duration, IState next = null) : base(
				new RightMouseDown(new Delay(duration, new RightMouseUp(next)))) { }
		public override IState OnEnter() => Next;
	}

	class RightClickAt : InputState {
		public override string Name => $"RightClickAt({(Next as MoveMouse)?.X ?? 0}, {(Next as MoveMouse)?.Y ?? 0})";
		public RightClickAt(RectangleF rect, uint duration, IState next = null) : this(Center(rect), duration, next) { }
		public RightClickAt(Vector2 pos, uint duration, IState next = null) : this(pos.X, pos.Y, duration, next) { }
		public RightClickAt(float x, float y, uint duration, IState next = null) : base(
				(x == 0 && y == 0) ? next :
				new MoveMouse(x, y, new Delay(duration, new RightMouseDown(new Delay(duration, new RightMouseUp(next)))))) { }
	}
	class CtrlRightClickAt : InputState {
		public override string Name => $"CtrlRightClickAt({(Next as MoveMouse)?.X ?? 0}, {(Next as MoveMouse)?.Y ?? 0})";
		// public CtrlRightClickAt(Element item, uint duration, IState next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public CtrlRightClickAt(Vector2 pos, uint duration, IState next = null) : this(pos.X, pos.Y, duration, next) { }
		public CtrlRightClickAt(float x, float y, uint duration, IState next = null) : base(
				(x == 0 && y == 0) ? next :
				new MoveMouse(x, y, new Delay(duration, 
					new KeyDown(VKey.LControlKey, new Delay(duration,
						new RightMouseDown(new Delay(duration,
							new RightMouseUp(new Delay(duration,
								new KeyUp(VKey.LControlKey, next)))))))))) { }
	}
	class CtrlLeftClickAt : InputState {
		public override string Name => $"CtrlLeftClickAt({(Next as MoveMouse)?.X ?? 0}, {(Next as MoveMouse)?.Y ?? 0})";
		// public CtrlLeftClickAt(Element item, uint duration, IState next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public CtrlLeftClickAt(Vector2 pos, uint duration, IState next = null) : this(pos.X, pos.Y, duration, next) { }
		public CtrlLeftClickAt(float x, float y, uint duration, IState next = null) : base(
				(x == 0 && y == 0) ? next :
				new MoveMouse(x, y, new Delay(duration, 
					new KeyDown(VKey.LControlKey, new Delay(duration,
						new LeftMouseDown(new Delay(duration,
							new LeftMouseUp(new Delay(duration,
								new KeyUp(VKey.LControlKey, next)))))))))) { }
	}
	class ShiftLeftClickAt : InputState {
		// public ShiftLeftClickAt(Element item, uint duration, IState next = null) : this(item?.GetClientRect().Center ?? Vector2.Zero, duration, next) { }
		public ShiftLeftClickAt(Vector2 pos, uint duration, IState next = null) : this(pos.X, pos.Y, duration, next) { }
		public ShiftLeftClickAt(float x, float y, uint duration, IState next = null) : base(
				(x == 0 && y == 0) ? next :
				new MoveMouse(x, y, new Delay(duration, 
					new KeyDown(VKey.LShiftKey, new Delay(duration,
						new LeftMouseDown(new Delay(duration,
							new LeftMouseUp(new Delay(duration,
								new KeyUp(VKey.LShiftKey, next)))))))))) { }

	}

}
