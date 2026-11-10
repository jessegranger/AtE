using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Spends passive points on a plan, by clicking: open the tree (with the level-up button), bring the next planned
	/// passive into view (by dragging the tree), click it, and see it allocated; then the next, until there are no
	/// points left; then close the tree. Tick it each frame while it is on; it only clicks while the game has focus.
	/// </summary>
	public class PassiveAllocator {

		public bool IsActive { get; private set; }
		public string Status { get; private set; } = "off";

		/// <summary>The planned point being taken now, or null.</summary>
		public PassiveStep Step { get; private set; }

		// how long to wait for the game to answer a click before trying again, and how many tries
		public int AnswerMs = 1200;
		public int Tries = 3;

		private long lastAction;
		private int clickedId, clickTries;
		private Drag drag;

		public void Start() {
			IsActive = true;
			Status = "starting";
			clickedId = 0;
			clickTries = 0;
			drag = null;
		}

		public void Stop(string why) {
			IsActive = false;
			Status = why;
			Step = null;
			drag?.Release();
			drag = null;
		}

		public void Tick(PassivePlan plan) {
			if ( !IsActive ) return;
			if ( plan == null ) { Stop("no plan"); return; }
			if ( !PoEMemory.TargetHasFocus ) { Status = "waiting: the game needs focus to use the mouse"; return; }
			long now = Time.ElapsedMilliseconds;
			if ( drag != null ) {
				Status = $"dragging the tree to {Step?.Node.Name}";
				if ( drag.Tick(now) ) { drag = null; lastAction = now; }
				return;
			}

			var allocated = new HashSet<int>((PoEMemory.GameRoot?.InGameState?.AllocatedPassives ?? Array.Empty<ushort>()).Select(id => (int)id));
			int points = GetUI()?.UnspentPassivePoints ?? 0;
			Step = points > 0 ? plan.Next(allocated) : null;
			bool open = PassiveTreePanel.IsOpen;

			if ( Step == null ) {
				// done: close the tree
				if ( !open ) { Stop(points > 0 ? "the plan is all taken" : "no points to spend"); return; }
				Status = "closing the tree";
				if ( now - lastAction > 700 ) {
					lastAction = now;
					Platform.Current.Input.KeyDown(VKey.Escape);
					Platform.Current.Input.KeyUp(VKey.Escape);
				}
				return;
			}
			if ( Step.Node.Kind == PassiveNodeKind.Mastery ) { Stop($"next is a mastery ({Step}): choosing its effect is not done yet"); return; }

			if ( !open ) {
				var button = GetUI()?.PassivePointsButton;
				Status = "opening the tree";
				if ( IsValid(button) && button.IsVisible && now - lastAction > 1000 ) {
					lastAction = now;
					SkillBar.ClickAt(button.GetClientRect());
				}
				return;
			}

			// was the last click answered?
			if ( clickedId != 0 ) {
				if ( allocated.Contains(clickedId) ) {
					Log($"PassiveAllocator: allocated {clickedId} {PassiveNodeName(plan, clickedId)}");
					clickedId = 0;
					clickTries = 0;
				} else if ( now - lastAction < AnswerMs ) {
					Status = $"waiting for {Step.Node.Name} to be allocated";
					return;
				} else if ( clickTries >= Tries ) {
					Stop($"clicked {Step.Node.Name} {clickTries} times, but it was not allocated");
					return;
				}
			}
			if ( now - lastAction < 150 ) return; // (let the tree settle after a drag or a click)

			if ( !PassiveTreePanel.TryGetScreenPosition(Step.Node.Id, out var pos) ) {
				Status = $"{Step.Node.Name} is not on the tree";
				return;
			}
			var size = WindowSize.Value;
			if ( !IsClear(pos, size) ) {
				// drag the tree, from an empty spot, to bring the node to the middle of the screen
				var center = new Vector2(size.X * 0.5f, size.Y * 0.5f);
				if ( !FindEmptySpot(center, size, out var from) ) { Stop("no empty spot on the tree to drag it by"); return; }
				var to = Vector2.Clamp(from + (center - pos), size * 0.05f, size * 0.95f);
				drag = new Drag(from, to, now);
				return;
			}
			Status = $"clicking {Step.Node.Name}";
			lastAction = now;
			clickedId = Step.Node.Id;
			clickTries += 1;
			// with Ctrl held, the game allocates it at once (a plain click asks to confirm, in a pop up)
			Platform.Current.Input.KeyDown(VKey.LControlKey);
			SkillBar.ClickAt(new System.Drawing.RectangleF(pos.X, pos.Y, 0, 0));
			Platform.Current.Input.KeyUp(VKey.LControlKey);
		}

		private static string PassiveNodeName(PassivePlan plan, int id) => plan.Tree.Node(id)?.Name ?? "?";

		// away from the edges, and below the tree's header (the search box and points) along the top
		private static bool IsClear(Vector2 pos, Vector2 size) =>
			pos.X > size.X * 0.10f && pos.X < size.X * 0.90f && pos.Y > size.Y * 0.22f && pos.Y < size.Y * 0.88f;

		// a spot near the middle of the screen with no passive close to it, so pressing there does not click one
		private static bool FindEmptySpot(Vector2 center, Vector2 size, out Vector2 spot) {
			float scale = PassiveTreePanel.ScreenScale;
			float clearance = Math.Max(20f, 90f * scale); // (a big node is about 90 tree units across)
			var canvas = PassiveTreePanel.Canvas;
			var nodes = PassiveTreePanel.Centers.Values.Select(c => canvas.LocalToScreen(c))
				.Where(p => Vector2.Distance(p, center) < size.Y * 0.4f).ToList();
			for ( float r = 0; r < size.Y * 0.3f; r += clearance / 2 ) {
				for ( int i = 0; i < 16; i++ ) {
					double a = i * Math.PI / 8;
					spot = center + new Vector2((float)Math.Cos(a) * r, (float)Math.Sin(a) * r);
					if ( !IsClear(spot, size) ) continue;
					var s = spot;
					if ( nodes.All(p => Vector2.Distance(p, s) > clearance) ) return true;
					if ( r == 0 ) break;
				}
			}
			spot = default;
			return false;
		}

		// a press, a few moves, a release, over a few frames
		private class Drag {
			private readonly Vector2 from, to;
			private readonly long start;
			private int step;
			public Drag(Vector2 from, Vector2 to, long now) { this.from = from; this.to = to; start = now; }
			// true when done
			public bool Tick(long now) {
				long t = now - start;
				switch ( step ) {
					case 0: SkillBar.MouseTo(from); step++; break;
					case 1: if ( t < 60 ) break; Platform.Current.Input.MouseButton(MouseBtn.Left, true); step++; break;
					case 2: if ( t < 120 ) break; SkillBar.MouseTo(Vector2.Lerp(from, to, 0.33f)); step++; break;
					case 3: if ( t < 170 ) break; SkillBar.MouseTo(Vector2.Lerp(from, to, 0.66f)); step++; break;
					case 4: if ( t < 220 ) break; SkillBar.MouseTo(to); step++; break;
					case 5: if ( t < 300 ) break; Release(); return true;
				}
				return false;
			}
			public void Release() {
				if ( step >= 2 && step <= 5 ) Platform.Current.Input.MouseButton(MouseBtn.Left, false);
				step = 6;
			}
		}
	}
}
