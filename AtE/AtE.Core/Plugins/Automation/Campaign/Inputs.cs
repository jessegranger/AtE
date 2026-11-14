using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Mouse and keyboard input as chains of IState (MoveMouse, KeyDown, LeftMouseDown, ... with Delays between), so
	/// each press and release goes out on its own frame, InputLatency apart, never several in one frame (which the
	/// server's rate limiting notices). Each takes the state to go on to (eg, the goal that asked, to come back to it).
	///
	/// Positions are in client coordinates (as Element.GetClientRect gives them); the game window's offset on the
	/// screen is added here.
	/// </summary>
	public static class Inputs {
		/// <summary>How long to hold a key or button, and to wait between steps (CoreSettings.InputLatency).</summary>
		public static uint Latency => InventoryPlans.InputLatency;

		/// <summary>When Escape last went out (eg, to close a dialog: if the dialog had closed already, the escape menu opens instead).</summary>
		public static long LastEscapeMs = -1000000;

		private static Vector2 Screen(Vector2 client) =>
			client + (PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero);
		private static Vector2 Center(RectangleF r) => new Vector2(r.X + (r.Width / 2), r.Y + (r.Height / 2));

		/// <summary>Move the mouse to a client position.</summary>
		public static IState MoveTo(Vector2 client, IState next = null) => new MoveMouse(Screen(client), next);

		/// <summary>Left click the middle of a client rectangle (or a point, for an empty one).</summary>
		public static IState Click(RectangleF client, IState next = null) => Click(Center(client), next);
		public static IState Click(Vector2 client, IState next = null) =>
			new MoveMouse(Screen(client), new Delay(Latency, new LeftMouseDown(new Delay(Latency, new LeftMouseUp(new Delay(Latency, next))))));

		/// <summary>Right click the middle of a client rectangle.</summary>
		public static IState RightClick(RectangleF client, IState next = null) =>
			new MoveMouse(Screen(Center(client)), new Delay(Latency, new RightMouseDown(new Delay(Latency, new RightMouseUp(new Delay(Latency, next))))));

		/// <summary>Ctrl + left click the middle of a client rectangle (eg, take a quest reward at once).</summary>
		public static IState CtrlClick(RectangleF client, IState next = null) =>
			new MoveMouse(Screen(Center(client)), new Delay(Latency,
				new KeyDown(VKey.LControlKey, new Delay(Latency,
					new LeftMouseDown(new Delay(Latency,
						new LeftMouseUp(new Delay(Latency,
							new KeyUp(VKey.LControlKey, new Delay(Latency, next))))))))));

		/// <summary>Ctrl + right click the middle of a client rectangle (eg, toggle a reservation skill in the hidden skill bar).</summary>
		public static IState CtrlRightClick(RectangleF client, IState next = null) =>
			new MoveMouse(Screen(Center(client)), new Delay(Latency,
				new KeyDown(VKey.LControlKey, new Delay(Latency,
					new RightMouseDown(new Delay(Latency,
						new RightMouseUp(new Delay(Latency,
							new KeyUp(VKey.LControlKey, new Delay(Latency, next))))))))));

		/// <summary>Press and release a key.</summary>
		public static IState Press(VKey key, IState next = null) => new KeyDown(key, new Delay(Latency, new KeyUp(key, new Delay(Latency, next))));

		/// <summary>
		/// Use a skill slot aimed at a client position: move the mouse there, hold the slot's modifiers, click its mouse
		/// button or press its key (held holdMs), and let the modifiers go.
		/// </summary>
		public static IState UseSlot(SkillSlot slot, Vector2 client, uint holdMs, IState next = null) {
			if ( slot == null || slot.Key == VKey.None ) return next;
			var mods = new List<VKey>();
			if ( (slot.Key & VKey.Control) != 0 ) mods.Add(VKey.ControlKey);
			if ( (slot.Key & VKey.Shift) != 0 ) mods.Add(VKey.ShiftKey);
			if ( (slot.Key & VKey.Alt) != 0 ) mods.Add(VKey.Menu);
			// from the end back: modifiers up, the press, modifiers down, the move
			IState chain = next;
			foreach ( var m in mods ) chain = new KeyUp(m, chain);
			var button = SkillBar.ToMouseButton(slot.KeyCode);
			if ( button == MouseBtn.Left ) chain = new LeftMouseDown(new Delay(holdMs, new LeftMouseUp(chain)));
			else if ( button == MouseBtn.Right ) chain = new RightMouseDown(new Delay(holdMs, new RightMouseUp(chain)));
			else if ( button.HasValue ) chain = new MouseButtonPress(button.Value, holdMs, chain);
			else chain = new KeyDown(slot.KeyCode, new Delay(holdMs, new KeyUp(slot.KeyCode, chain)));
			foreach ( var m in mods ) chain = new KeyDown(m, new Delay(Latency, chain));
			return new MoveMouse(Screen(client), chain);
		}

		// another mouse button (eg, middle), down then up holdMs later
		private class MouseButtonPress : State {
			private readonly MouseBtn button;
			private readonly uint holdMs;
			private bool down;
			private long held;
			public MouseButtonPress(MouseBtn button, uint holdMs, IState next) : base(next) { this.button = button; this.holdMs = holdMs; }
			public override string Name => $"Press({button})";
			public override IState OnTick(long dt) {
				if ( dt == 0 ) return this;
				if ( !down ) { Platform.Current.Input.MouseButton(button, true); down = true; return this; }
				if ( (held += dt) < holdMs ) return this;
				Platform.Current.Input.MouseButton(button, false);
				return Next;
			}
		}
	}
}
