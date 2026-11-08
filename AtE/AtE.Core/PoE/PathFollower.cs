using System;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	public enum FollowStatus { Idle, Walking, OpeningDoor, Arrived, NoPath, Stuck, WaitingForFocus, Paused }

	/// <summary>
	/// Walks the player along a NavPath to a goal, by clicking on the ground a little way ahead on the path,
	/// and opens closed doors on the way by clicking their labels.
	///
	/// PoE only moves the player by mouse (clicking, or a skill aimed at the cursor), and only reads the
	/// mouse while it has focus (see WindowsInputSink), so this waits while the game is not focused.
	/// Call Tick once per frame while IsActive.
	/// </summary>
	public class PathFollower {

		/// <summary>How far ahead along the path (in cells) to aim each click.</summary>
		public int AimAhead = 18;
		/// <summary>Within this many cells of the goal counts as arrived.</summary>
		public int ArriveRadius = 4;
		/// <summary>How often to click (or press MoveKey) while walking.</summary>
		public long ClickIntervalMs = 120;
		/// <summary>If the player moves less than 2 cells in this long, re-plan; after MaxStuck re-plans in a row, give up.</summary>
		public long StuckMs = 1500;
		public int MaxStuck = 3;
		/// <summary>Start clicking a closed door when this close to it (in cells).</summary>
		public float DoorReach = 15f;
		/// <summary>How often to click a door that has not opened yet.</summary>
		public long DoorClickIntervalMs = 500;
		/// <summary>
		/// If the game's "Move Only" action is bound to a key, set it here: it is pressed (in the background) instead of a
		/// left click, so walking never attacks or picks things up. VKey.None means left click.
		/// </summary>
		public VKey MoveKey = VKey.None;

		public bool IsActive { get; private set; }
		public Offsets.Vector2i Goal { get; private set; }
		public NavPath Path { get; private set; }
		public FollowStatus Status { get; private set; } = FollowStatus.Idle;
		/// <summary>A few words about what it is doing now (eg, which door it is opening).</summary>
		public string Detail { get; private set; } = "";
		/// <summary>The screen position of the last click.</summary>
		public Vector2 LastAim { get; private set; }

		private long lastClick, lastDoorClick;
		private Offsets.Vector2i progressFrom;
		private long progressTime;
		private int stuckCount;
		private int terrainGeneration;

		public void Start(Offsets.Vector2i goal) {
			if ( !IsActive || goal.Id != Goal.Id ) Path = null;
			Goal = goal;
			IsActive = true;
			Status = FollowStatus.Walking;
			Detail = "";
			stuckCount = 0;
			progressTime = Time.ElapsedMilliseconds;
			progressFrom = new Offsets.Vector2i(int.MinValue / 2, 0);
			terrainGeneration = GetTerrain()?.Generation ?? 0;
		}

		/// <summary>Change the goal while walking (eg, to the next exploration target), keeping everything else going.</summary>
		public void SetGoal(Offsets.Vector2i goal) {
			if ( !IsActive ) Start(goal);
			else Goal = goal;
		}

		public void Stop(FollowStatus status = FollowStatus.Idle, string detail = "") {
			IsActive = false;
			Status = status;
			Detail = detail;
		}

		public FollowStatus Tick(Navigator nav, Terrain terrain, Offsets.Vector2i player, float z) {
			if ( !IsActive ) return Status;
			var game = PoEMemory.GameRoot?.InGameState;
			if ( terrain == null || game == null ) return Wait(FollowStatus.Paused, "no terrain");
			if ( terrain.Generation != terrainGeneration ) {
				Stop(FollowStatus.Idle, "the area changed");
				return Status;
			}
			if ( game.IsPaused ) return Wait(FollowStatus.Paused, "the game is paused");
			if ( game.HasInputFocus ) return Wait(FollowStatus.Paused, "typing in the game");
			if ( PoEMemory.GameRoot?.EscapeState?.Menu != null ) return Wait(FollowStatus.Paused, "escape menu");
			if ( !PoEMemory.TargetHasFocus ) return Wait(FollowStatus.WaitingForFocus, "the game needs focus to use the mouse");

			long now = Time.ElapsedMilliseconds;
			Path = nav.UpdatePath(Path, terrain, player, Goal);
			if ( Path == null ) {
				Stop(FollowStatus.NoPath, $"no path to {Goal}");
				return Status;
			}
			if ( Distance(player, Path.Goal) <= ArriveRadius ) {
				Stop(FollowStatus.Arrived, $"at {Path.Goal}");
				return Status;
			}

			// stuck: not making progress for a while
			if ( Distance(player, progressFrom) >= 2 ) {
				progressFrom = player;
				progressTime = now;
				stuckCount = 0;
			} else if ( now - progressTime > StuckMs ) {
				stuckCount += 1;
				if ( stuckCount > MaxStuck ) {
					Stop(FollowStatus.Stuck, $"no progress at {player}");
					return Status;
				}
				Path = nav.FindPath(terrain, player, Goal); // start over, the plan may be wrong
				progressTime = now;
			}

			// a closed door ahead: walk to it, then click it until it opens
			var doorStep = Path.Steps.FirstOrDefault(s => s.Kind == NavStepKind.OpenDoor);
			int doorCell = Path.Cells.Count;
			if ( doorStep != null ) {
				// the cell in front of the door is the Walk step just before the OpenDoor step
				int i = Path.Steps.IndexOf(doorStep);
				var front = i > 0 ? Path.Steps[i - 1].Target : player;
				doorCell = Math.Max(0, Path.Cells.FindIndex(c => c.Id == front.Id));
				if ( Distance(player, doorStep.Target) <= DoorReach ) {
					Status = FollowStatus.OpeningDoor;
					Detail = $"door {doorStep.Door.EntityId}";
					if ( now - lastDoorClick > DoorClickIntervalMs && TryGetDoorClickPoint(doorStep, z, out Vector2 at) ) {
						Click(at);
						lastDoorClick = now;
					}
					return Status;
				}
			}

			// walk: click on the path a little way ahead (but never past a closed door, and only where the screen is safe to click)
			Status = FollowStatus.Walking;
			Detail = $"to {Path.Goal}";
			if ( now - lastClick < ClickIntervalMs ) return Status;
			int aim = Math.Min(Math.Min(AimAhead, doorCell), Path.Cells.Count - 1);
			for ( ; aim > 0; aim-- ) {
				if ( TryGridToScreen(Path.Cells[aim], z, out Vector2 screen) && IsSafeToClick(screen) ) {
					if ( MoveKey != VKey.None ) {
						MoveCursor(screen);
						Platform.Current.Input.KeyDown(MoveKey);
						Platform.Current.Input.KeyUp(MoveKey);
					} else {
						Click(screen);
					}
					LastAim = screen;
					lastClick = now;
					break;
				}
			}
			return Status;
		}

		private FollowStatus Wait(FollowStatus status, string why) {
			Status = status;
			Detail = why;
			progressTime = Time.ElapsedMilliseconds; // waiting is not being stuck
			return Status;
		}

		private bool TryGetDoorClickPoint(NavStep step, float z, out Vector2 at) {
			var label = step.Door.GetLabel();
			if ( IsValid(label) ) {
				var rect = label.GetClientRect();
				at = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
				if ( IsSafeToClick(at) ) return true;
			}
			// no label showing: click the door itself
			return TryGridToScreen(step.Target, z, out at) && IsSafeToClick(at);
		}

		/// <summary>
		/// Inside the game window, away from the edges and the HUD: the skill bar and flasks along the bottom, and the minimap.
		/// </summary>
		public static bool IsSafeToClick(Vector2 pos) {
			var size = WindowSize.Value;
			if ( size.X <= 0 || size.Y <= 0 ) return false;
			float x = pos.X / size.X, y = pos.Y / size.Y;
			if ( x < 0.05f || x > 0.95f || y < 0.08f || y > 0.80f ) return false;
			if ( x > 0.78f && y < 0.30f ) return false; // the minimap (top right)
			return true;
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;

		private static void MoveCursor(Vector2 clientPos) => Platform.Current.Input.MouseMove(clientPos + WindowOffset());

		private static void Click(Vector2 clientPos) {
			MoveCursor(clientPos);
			Platform.Current.Input.MouseButton(MouseBtn.Left, true);
			Platform.Current.Input.MouseButton(MouseBtn.Left, false);
		}

		private static float Distance(Offsets.Vector2i a, Offsets.Vector2i b) {
			float dx = a.X - b.X, dy = a.Y - b.Y;
			return MathF.Sqrt((dx * dx) + (dy * dy));
		}
	}

	public static partial class Globals {
		/// <summary>The shared PathFollower; only one thing can be moving the player at a time.</summary>
		public static readonly PathFollower DefaultFollower = new PathFollower();
	}
}
