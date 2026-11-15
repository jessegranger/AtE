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
	/// Its clicks, keys and drags are input chains (see Inputs) run on the default Machine, one at a time: nothing else
	/// is done while one is going out.
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
		private bool inputBusy;

		// send an input chain (on the default Machine); false while one is still going out. lastAction is when it ends.
		private bool Send(IState chain) {
			if ( inputBusy || chain == null ) return false;
			inputBusy = true;
			lastAction = Time.ElapsedMilliseconds;
			chain.Tail().Next = State.NewState(() => { inputBusy = false; lastAction = Time.ElapsedMilliseconds; });
			Run(chain);
			return true;
		}

		public void Start() {
			IsActive = true;
			Status = "starting";
			clickedId = 0;
			clickTries = 0;
			masteryOpenTries = 0;
			applyPending = false;
		}

		public void Stop(string why) {
			IsActive = false;
			Status = why;
			Step = null;
			// (an input chain going out finishes by itself: a drag lets go of the mouse at its end)
		}

		public void Tick(PassivePlan plan) {
			if ( !IsActive ) return;
			if ( plan == null ) { Stop("no plan"); return; }
			if ( !PoEMemory.TargetHasFocus ) { Status = "waiting: the game needs focus to use the mouse"; return; }
			long now = Time.ElapsedMilliseconds;
			if ( inputBusy ) return; // (an input chain is going out)

			var allocated = new HashSet<int>((PoEMemory.GameRoot?.InGameState?.AllocatedPassives ?? Array.Empty<ushort>()).Select(id => (int)id));
			allocated.UnionWith(masteriesTaken);
			int points = GetUI()?.UnspentPassivePoints ?? 0;
			// (a mastery's effect chosen counts its point at once, but it is only pending until Apply Points: keep at it)
			Step = points > 0 || (clickedId != 0 && (applyPending || applied)) ? plan.Next(allocated) : null;
			bool open = PassiveTreePanel.IsOpen;

			if ( Step == null ) {
				// done: close the tree
				if ( !open ) { Stop(points > 0 ? "the plan is all taken" : "no points to spend"); return; }
				Status = "closing the tree";
				if ( now - lastAction > 700 ) Send(Inputs.Press(VKey.Escape));
				return;
			}
			if ( !open ) {
				var button = GetUI()?.PassivePointsButton;
				Status = "opening the tree";
				if ( IsValid(button) && button.IsVisible && now - lastAction > 1000 ) Send(Inputs.Click(button.GetClientRect()));
				return;
			}

			if ( Step.Node.Kind == PassiveNodeKind.Mastery && clickedId == 0 && TryChooseMastery(now) ) return;

			// was the last click answered?
			if ( clickedId != 0 ) {
				// (a mastery, if it is not in AllocatedPassives: taken when the points went down after Apply Points; they go
				// down as soon as the effect is chosen, but that is only pending, and undone if the tree is closed)
				if ( !allocated.Contains(clickedId) && Step.Node.Kind == PassiveNodeKind.Mastery && applied && points < pointsAtClick ) {
					masteriesTaken.Add(clickedId);
					allocated.Add(clickedId);
				}
				if ( !allocated.Contains(clickedId) && applyPending ) {
					// the effect is chosen: apply it, once "Apply Points" shows
					var apply = ApplyPointsButton();
					if ( apply != null && now - lastAction > 300 ) {
						applyPending = false;
						applied = true;
						Status = "applying the points";
						Log($"PassiveAllocator: {Step.Node.Name}'s effect chosen: clicking Apply Points");
						Send(Inputs.Click(apply.GetClientRect()));
						return;
					}
					if ( now - lastAction < AnswerMs ) { Status = "waiting for Apply Points"; return; }
					applyPending = false; // (it did not show: try again, below)
				}
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
				} else if ( Step.Node.Kind == PassiveNodeKind.Mastery ) {
					// (try the effect again, in its pop up, opening that again if it closed; not the node, which toggles it)
					clickedId = 0;
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
				Status = $"dragging the tree to {Step.Node.Name}";
				Send(Drag(from, to));
				return;
			}
			if ( Step.Node.Kind == PassiveNodeKind.Mastery ) {
				// a plain click opens its pop up, to choose the effect in (see TryChooseMastery)
				Status = $"opening {Step.Node.Name}'s effects";
				// (the pop up takes a moment to show: clicking again before then closes it)
				if ( masteryOpenTries > 0 && now - lastAction < AnswerMs ) return;
				if ( ++masteryOpenTries > Tries ) { Stop($"clicked {Step.Node.Name} {Tries} times, but its pop up did not open"); return; }
				Send(Inputs.Click(pos));
				return;
			}
			Status = $"clicking {Step.Node.Name}";
			// with Ctrl held, the game allocates it at once (a plain click asks to confirm, in a pop up)
			if ( !Send(Inputs.CtrlClick(new System.Drawing.RectangleF(pos.X, pos.Y, 0, 0))) ) return;
			clickedId = Step.Node.Id;
			clickTries += 1;
		}

		private static string PassiveNodeName(PassivePlan plan, int id) => plan.Tree.Node(id)?.Name ?? "?";

		private int masteryOpenTries;
		private int pointsAtClick;
		private bool applyPending, applied;

		// the tree's "Apply Points" button (3.29.4, seen live: the panel's GetChild(3, 1, 0, 0), its text "apply points"
		// in its GetChild(0)), if it is showing
		private static Element ApplyPointsButton() {
			var button = PassiveTreePanel.Panel?.GetChild(3, 1, 0, 0);
			return IsValid(button) && button.IsVisible
				&& string.Equals(button.GetChild(0)?.Text?.Trim(), "apply points", StringComparison.OrdinalIgnoreCase) ? button : null;
		}
		// masteries seen taken (by the points going down) this session, in case AllocatedPassives does not list them
		private static readonly HashSet<int> masteriesTaken = new HashSet<int>();


		/// <summary>
		/// For a Mastery step, with its pop up open: click the planned effect's row (then it is waited for like any click).
		/// True if it did (or is waiting on the pop up); false if the pop up is not open (so the node is clicked to open it).
		///
		/// The pop up (3.29.4, seen live: Fire Mastery) is a child of the tree's canvas: its GetChild(0, 0) is the title
		/// ("&lt;bigger&gt;{Fire Mastery}"), its GetChild(1) a list: a hint line first, then one row per effect, each row's
		/// GetChild(0, 0) a radio button and GetChild(0, 1) the effect's text. Where it is drawn: see PopupRect.
		/// </summary>
		private bool TryChooseMastery(long now) {
			var popup = MasteryPopup(Step.Node.Name);
			if ( popup == null ) return false;
			masteryOpenTries = 0;
			if ( now - lastAction < 300 ) return true; // (let it settle after opening)
			string first = Step.MasteryText?.Split(new[] { " / " }, StringSplitOptions.None)[0]?.Trim();
			var row = popup.GetChild(1)?.Children.Skip(1)
				.FirstOrDefault(r => first != null && (r.GetChild(0, 1)?.Text?.IndexOf(first, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
			if ( row == null ) { Stop($"{Step.Node.Name}'s pop up has no effect \"{first}\""); return true; }
			// (the whole row: its radio button alone did not take the click, 3.29.4)
			var rect = PopupRect(row, popup);
			var size = WindowSize.Value;
			if ( rect.Width <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > size.X || rect.Bottom > size.Y ) {
				Stop($"{Step.Node.Name}'s effect \"{first}\" is off the screen at {rect}");
				return true;
			}
			// point at the row a moment first (so the pop up sees the cursor on it), then click it; then "Apply Points"
			// (Ctrl + click, which allocates a passive at once, does not work for a mastery's effect, 3.29.4)
			var center = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
			Status = $"choosing {Step}";
			if ( !Send(Inputs.MoveTo(center, new Delay(250, Inputs.Click(center)))) ) return true;
			applyPending = true;
			applied = false;
			Log($"PassiveAllocator: choosing {Step} at {rect}");
			pointsAtClick = GetUI()?.UnspentPassivePoints ?? 0;
			clickedId = Step.Node.Id;
			clickTries += 1;
			return true;
		}

		/// <summary>
		/// Where an element of a mastery pop up is on the screen. The pop up is a child of the tree's canvas, but not zoomed
		/// with it: it is placed (its Position) from the canvas's origin (where its local 0, 0 is on the screen, ie the
		/// middle of the tree, see Element.LocalToScreen), in plain UI units, as are its children within it (3.29.4, checked
		/// against a screenshot: the Fire Mastery pop up, 1250x534 units, drawn 937x400 pixels at 1920x1200, left edge at
		/// the canvas origin's X + 0.75 * its Position.X). (GetClientRect is wrong for it: it zooms it with the canvas, and
		/// leaves out the canvas's ScrollOffset.)
		/// </summary>
		private static System.Drawing.RectangleF PopupRect(Element e, Element popup) {
			var canvas = PassiveTreePanel.Canvas;
			if ( !IsValid(canvas) || !IsValid(e) ) return System.Drawing.RectangleF.Empty;
			var pos = Vector2.Zero;
			var cursor = e;
			for ( int hops = 0; hops < 20 && IsValid(cursor); hops++ ) {
				pos += cursor.Position;
				if ( cursor.Address == popup.Address ) break;
				cursor = cursor.Parent;
			}
			var scale = Element.UIScreenScale;
			var at = canvas.LocalToScreen(Vector2.Zero) + (pos * scale);
			var size = e.Size * scale;
			return new System.Drawing.RectangleF(at.X, at.Y, size.X, size.Y);
		}

		// the visible pop up of the mastery named name (eg, "Fire Mastery") on the tree's canvas, or null
		private static Element MasteryPopup(string name) {
			var canvas = PassiveTreePanel.Canvas;
			if ( !IsValid(canvas) || name == null ) return null;
			return canvas.Children.LastOrDefault(c => c.IsVisibleLocal
				&& (c.GetChild(0, 0)?.Text?.IndexOf(name, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
				&& c.GetChild(1) != null);
		}

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

		// drag with the left button: press at from, a few moves, let go at to
		private static IState Drag(Vector2 from, Vector2 to) =>
			Inputs.MoveTo(from, new Delay(60, new LeftMouseDown(new Delay(60,
				Inputs.MoveTo(Vector2.Lerp(from, to, 0.33f), new Delay(50,
				Inputs.MoveTo(Vector2.Lerp(from, to, 0.66f), new Delay(50,
				Inputs.MoveTo(to, new Delay(80, new LeftMouseUp(new Delay(Inputs.Latency))))))))))));
	}
}
