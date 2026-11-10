using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Where the overlay has clicked the mouse lately, to draw as a small expanding circle at each click.
	/// Every click sent through Platform.Current.Input is recorded (see ClickMarkingInput).
	/// </summary>
	public static class ClickMarks {
		public static int DurationMs = 300;
		public static float StartRadius = 3f;
		public static float EndRadius = 14f;

		private static readonly List<(Vector2 pos, long time)> marks = new List<(Vector2, long)>();

		/// <summary>Record a click at pos, in screen coordinates.</summary>
		public static void Add(Vector2 screenPos) {
			var offset = PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;
			lock ( marks ) marks.Add((screenPos - offset, Time.ElapsedMilliseconds));
		}

		/// <summary>Draw the clicks of the last DurationMs, each a yellow circle that grows and fades.</summary>
		public static void Draw() {
			long now = Time.ElapsedMilliseconds;
			lock ( marks ) {
				marks.RemoveAll(m => now - m.time > DurationMs);
				foreach ( var (pos, time) in marks ) {
					float t = Math.Clamp((now - time) / (float)DurationMs, 0f, 1f);
					DrawCircle(pos, StartRadius + ((EndRadius - StartRadius) * t), Color.FromArgb((int)(255 * (1f - (0.7f * t))), Color.Yellow));
				}
			}
		}
	}

	/// <summary>
	/// Passes input on to the platform's sink, recording where each mouse button press happens: where the cursor was last
	/// moved to (if lately, as the move may not have reached the cursor yet), or else where the cursor is.
	/// </summary>
	internal class ClickMarkingInput : IInputSink {
		private readonly IInputSink inner;
		private readonly IInputSource keys;
		private Vector2 lastMove;
		private long lastMoveTime = long.MinValue;
		public ClickMarkingInput(IInputSink inner, IInputSource keys) { this.inner = inner; this.keys = keys; }

		public void MouseMove(Vector2 screenPos) {
			lastMove = screenPos;
			lastMoveTime = Time.ElapsedMilliseconds;
			inner.MouseMove(screenPos);
		}

		public void MouseButton(MouseBtn btn, bool down) {
			if ( down ) ClickMarks.Add(Time.ElapsedMilliseconds - lastMoveTime < 100 ? lastMove : keys.GetCursorPos());
			inner.MouseButton(btn, down);
		}

		public void KeyDown(VKey key) => inner.KeyDown(key);
		public void KeyUp(VKey key) => inner.KeyUp(key);
		public bool CanSendInBackground(VKey key) => inner.CanSendInBackground(key);
	}

	/// <summary>A platform with its input sink wrapped in a ClickMarkingInput.</summary>
	internal class ClickMarkingPlatform : IPlatform {
		private readonly IPlatform inner;
		public ClickMarkingPlatform(IPlatform inner) {
			this.inner = inner;
			Input = new ClickMarkingInput(inner.Input, inner.Keys);
		}
		public IProcessMemory Memory => inner.Memory;
		public IInputSink Input { get; }
		public IInputSource Keys => inner.Keys;
		public IOverlayWindow Window => inner.Window;
		public IRenderer Renderer => inner.Renderer;
	}
}
