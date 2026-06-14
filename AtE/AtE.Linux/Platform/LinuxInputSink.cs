using System;
using System.Numerics;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IInputSink"/> (synthetic input into the game) — stub.
	///
	/// Target design (PORTING.md cheat-sheet): create a <c>uinput</c> virtual mouse+keyboard
	/// device and emit evdev events for moves/clicks/keystrokes; or use XTEST under X11. The
	/// core <see cref="VKey"/> / <see cref="MouseBtn"/> values must be translated to evdev
	/// keycodes (KEY_*, BTN_*) here.
	///
	/// Note: the ToS/anti-cheat implications of synthetic input are platform-independent
	/// (see PORTING.md "Biggest risks").
	/// </summary>
	public sealed class LinuxInputSink : IInputSink {

		private const string NYI = "LinuxInputSink is not yet implemented (PORTING.md step 6).";

		public void MouseMove(Vector2 screenPos) =>
			throw new NotImplementedException(NYI + " TODO: emit a uinput absolute/relative move (or XTEST XWarpPointer).");

		public void MouseButton(MouseBtn btn, bool down) =>
			throw new NotImplementedException(NYI + " TODO: emit a uinput BTN_* event for the mapped button.");

		public void KeyDown(VKey key) =>
			throw new NotImplementedException(NYI + " TODO: map VKey -> evdev KEY_* and emit a uinput press.");

		public void KeyUp(VKey key) =>
			throw new NotImplementedException(NYI + " TODO: map VKey -> evdev KEY_* and emit a uinput release.");
	}
}
