using System;
using System.Numerics;

namespace AtE {

	/// <summary>
	/// Linux implementation of <see cref="IInputSource"/> (the polling side of input) — stub.
	///
	/// Target design (PORTING.md cheat-sheet):
	///  - <see cref="IsKeyDown"/>: X11 <c>XQueryKeymap</c> (translate <see cref="VKey"/> to a
	///    KeyCode), or read evdev key state directly.
	///  - <see cref="GetCursorPos"/>: X11 <c>XQueryPointer</c> (root-relative screen coords).
	///  - <see cref="IsTargetForeground"/>: compare the active window from the
	///    <c>_NET_ACTIVE_WINDOW</c> root-window hint against the game / overlay window.
	/// </summary>
	public sealed class LinuxInputSource : IInputSource {

		private const string NYI = "LinuxInputSource is not yet implemented (PORTING.md step 6).";

		public bool IsKeyDown(VKey key) =>
			throw new NotImplementedException(NYI + " TODO: map VKey -> KeyCode and test XQueryKeymap (or evdev) state.");

		public Vector2 GetCursorPos() =>
			throw new NotImplementedException(NYI + " TODO: read the pointer position via XQueryPointer.");

		public bool IsTargetForeground() =>
			throw new NotImplementedException(NYI + " TODO: compare _NET_ACTIVE_WINDOW against the game/overlay window.");
	}
}
