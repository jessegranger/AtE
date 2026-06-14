using System.Numerics;

namespace AtE {

	/// <summary>
	/// Synthetic input <em>into</em> the game: the seam that wraps SendInput and friends.
	/// Everything that injects mouse/keyboard events (the input <c>State</c>s, the
	/// flask/also-cast plugins, <c>HotKey.PressImmediate</c>) goes through this instead of
	/// touching the OS directly.
	///
	/// Windows impl: <c>WindowsInputSink</c> (SendInput / INPUT / MapVirtualKey).
	/// Linux impl (TODO): a <c>uinput</c> virtual device, or XTEST under X11.
	/// </summary>
	public interface IInputSink {

		/// <summary>Move the cursor to a position in the target window's client space.</summary>
		void MouseMove(Vector2 screenPos);

		/// <summary>Press (down=true) or release (down=false) a mouse button.</summary>
		void MouseButton(MouseBtn btn, bool down);

		/// <summary>Press a key.</summary>
		void KeyDown(VKey key);

		/// <summary>Release a key.</summary>
		void KeyUp(VKey key);
	}
}
