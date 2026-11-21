using System;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Pausing and resuming the routine and the game together (for the memory server's /mapping/pause and /mapping/resume).
	/// </summary>
	public partial class MappingRoutine {

		/// <summary>The game's escape menu is showing.</summary>
		public static bool EscapeMenuOpen => PoEMemory.GameRoot?.EscapeState?.Menu?.IsVisible ?? false;

		/// <summary>
		/// Pause the routine (Paused: no input, no walking), and the game: Escape until the escape menu shows (an Escape that
		/// closes something else instead, eg the inventory, is followed by another after EscapeRetryMs). Nothing to send if
		/// the escape menu is open already. Done on the default Machine, as the routine does not run while paused.
		/// </summary>
		public void PauseAll() {
			Paused = true;
			if ( DefaultFollower.IsActiveFor(this) ) StopWalking("paused");
			Log($"{Name}: pausing the routine and the game");
			Run(new EscapeMenuState(true, null, Name));
		}

		/// <summary>
		/// Unpause the game (Escape until the escape menu is gone; nothing if it isn't open, eg the game is paused by the
		/// augury card window, which is left to the routine), then the routine.
		/// </summary>
		public void ResumeAll() {
			Log($"{Name}: resuming the game and the routine");
			Run(new EscapeMenuState(false, () => { Paused = false; Log($"{Name}: resumed"); }, Name));
		}

		/// <summary>How long after an Escape to look whether the escape menu is (or is not) up, before sending another.</summary>
		public const long EscapeRetryMs = 500;
		/// <summary>How many Escapes to send at most for a pause or a resume.</summary>
		public const int EscapeTries = 6;

		// send Escape (straight to the game, not as one of the routine's: CombatDriver closes an escape menu one of those
		// opened) until the escape menu's showing is open, at most EscapeTries, EscapeRetryMs apart; then done (also if
		// it gave up)
		private sealed class EscapeMenuState : State {
			private readonly bool open;
			private readonly Action done;
			private readonly string who;
			private long since = EscapeRetryMs; // (the first Escape at once)
			private int tries;
			public EscapeMenuState(bool open, Action done, string who) { this.open = open; this.done = done; this.who = who; }
			public override string Name => open ? "Open the escape menu" : "Close the escape menu";
			public override IState OnTick(long dt) {
				if ( dt == 0 ) return this;
				if ( !PoEMemory.IsAttached ) return null;
				if ( EscapeMenuOpen == open ) {
					if ( tries > 0 ) Log($"{who}: the escape menu is {(open ? "open: the game is paused" : "closed")} ({tries} Escape{(tries == 1 ? "" : "s")})");
					done?.Invoke();
					return null;
				}
				if ( (since += dt) < EscapeRetryMs ) return this;
				if ( tries >= EscapeTries ) {
					Log($"{who}: the escape menu is still {(open ? "not open" : "open")} after {tries} Escapes: giving up");
					done?.Invoke();
					return null;
				}
				if ( !CanSendKey(VKey.Escape) ) return this;
				since = 0;
				tries += 1;
				Platform.Current.Input.KeyDown(VKey.Escape);
				Platform.Current.Input.KeyUp(VKey.Escape);
				return this;
			}
		}
	}
}
