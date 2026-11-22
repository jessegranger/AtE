using System;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	public enum FollowStatus { Idle, Walking, OpeningDoor, UsingLever, AvoidingTrap, SkippingObstacle, SkippingDanger, Arrived, NoPath, Stuck, WaitingForFocus, Paused }

	/// <summary>
	/// Whoever walks the player (the PathFollower's Owner) can implement this to help get past traps (see
	/// PathFollower.TrapTouchMs): with a travel skill, and by stopping the walk at once.
	/// </summary>
	public interface ITrapDodger {
		/// <summary>Use a travel skill to get past the trap at trap along the path ahead; true if one was used.</summary>
		bool TryDodgeTrap(PathFollower follower, Terrain terrain, Offsets.Vector2i player, Offsets.Vector2i trap, float z);
		/// <summary>Stop the player moving now (eg, tap the main skill for one InputLatency, too short to cast); true if sent.</summary>
		bool StopMoving();
		/// <summary>True if there is a travel skill to use (ready or not), eg to get past obstacles (see TrySkipObstacle).</summary>
		bool HasTravelSkill { get; }
		/// <summary>Use a travel skill past the chest or barrel at obstacle (the path's cell obstacleIndex); true if one was used.</summary>
		bool TrySkipObstacle(PathFollower follower, Terrain terrain, Offsets.Vector2i player, Offsets.Vector2i obstacle, int obstacleIndex, float z);
		/// <summary>Use a travel skill over the danger ahead, to a safe cell of the path at or past landIndex (see NavStepKind.SkipDanger); true if one was used.</summary>
		bool TrySkipDanger(PathFollower follower, Terrain terrain, Offsets.Vector2i player, int landIndex, float z);
	}

	/// <summary>
	/// Walks the player along a NavPath to a goal, by holding the left button down and keeping the cursor on the ground a
	/// little way ahead on the path, and opens closed doors on the way by clicking their labels. A locked door is unlocked
	/// by the lever the Navigator spliced into the path (a UseLever step): pull it, then wait for the door to open.
	/// Touching a trap (Terrain.Traps) for TrapTouchMs, it gets past with a travel skill (if the Owner is an ITrapDodger
	/// with one ready), else stops and waits TrapPauseMs for the trap to move on.
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
		/// <summary>How often to move the cursor along (or press MoveKey) while walking.</summary>
		public long ClickIntervalMs = 120;
		/// <summary>Hold the left button down while walking, steering with the cursor, instead of clicking each ClickIntervalMs.</summary>
		public bool HoldMouse = true;
		/// <summary>If the player moves less than 2 cells in this long, re-plan; after MaxStuck re-plans in a row, give up.</summary>
		public long StuckMs = 1500;
		public int MaxStuck = 3;
		/// <summary>Start clicking a closed door when this close to it (in cells).</summary>
		public float DoorReach = 15f;
		/// <summary>How often to click a door that has not opened yet.</summary>
		public long DoorClickIntervalMs = 500;
		/// <summary>Start clicking a lever when this close to it (in cells).</summary>
		public float LeverReach = 12f;
		/// <summary>After pulling a lever, wait this long for its locked door to open before planning again (eg, for the next lever).</summary>
		public long LeverDoorWaitMs = 4000;
		/// <summary>A trap within this many cells is touching the player.</summary>
		public float TrapTouchRadius = 6f;
		/// <summary>Touching a trap this long: get past it (a travel skill), or stop and wait TrapPauseMs for it to move on.</summary>
		public long TrapTouchMs = 250;
		public long TrapPauseMs = 250;
		/// <summary>
		/// Within this many cells of a SkipObstacle step's chest or barrel, stop and use a travel skill past it (the Owner's
		/// ITrapDodger), waiting while the skill is on cooldown; after ObstacleWaitMs (eg, no landing cell) walk on into it.
		/// </summary>
		public float ObstacleReach = 14f;
		public long ObstacleWaitMs = 8000;
		/// <summary>
		/// Within this many cells of a SkipDanger step (the path crosses danger, see DangerMap), use a travel skill over it
		/// (the Owner's ITrapDodger), waiting outside it up to DangerWaitMs for one to be ready; then walk on through, and
		/// don't wait again for DangerGiveUpMs. Already in the danger: use one if ready, else walk on out.
		/// </summary>
		public float DangerReach = 10f;
		public long DangerWaitMs = 1500;
		public long DangerGiveUpMs = 3000;
		/// <summary>
		/// If the game's "Move Only" action is bound to a key, set it here: it is pressed (in the background) instead of a
		/// left click, so walking never attacks or picks things up. VKey.None means left click.
		/// </summary>
		public VKey MoveKey = VKey.None;

		public bool IsActive { get; private set; }
		/// <summary>Whoever started it (eg, a plugin), so two things don't steer the player at once.</summary>
		public object Owner { get; private set; }
		public Offsets.Vector2i Goal { get; private set; }
		public NavPath Path { get; private set; }
		public FollowStatus Status { get; private set; } = FollowStatus.Idle;
		/// <summary>A few words about what it is doing now (eg, which door it is opening).</summary>
		public string Detail { get; private set; } = "";
		/// <summary>The screen position of the last click.</summary>
		public Vector2 LastAim { get; private set; }

		private long lastClick, lastDoorClick;
		// the lever pulled last, and when it was first seen used (to wait for its door)
		private AreaLever pulledLever;
		private AreaDoor pulledFor;
		private long pulledAt;
		// touching a trap since (0: not touching), and waiting for one to move on until
		private long trapTouchSince, trapPauseUntil;
		// obstacles (entity ids) a travel skill got past (or that were given up on), and waiting for a skill since
		private readonly System.Collections.Generic.HashSet<uint> skippedObstacles = new System.Collections.Generic.HashSet<uint>();
		private long obstacleWaitSince;
		private uint obstacleWaitFor;
		private Offsets.Vector2i progressFrom;
		private long progressTime;
		private int stuckCount;
		private int terrainGeneration;
		// the left button is held down (see HoldMouse), since lastTick
		private bool held;
		private long lastTick;

		/// <summary>Log in detail (eg, traps near, how long they were touched, and what was done about it); set by Start.</summary>
		public bool Verbose;
		/// <summary>Verbose: the log is also written here (beside the exe), started over by each Start.</summary>
		public static string VerboseLogFile => System.IO.Path.Combine(AppContext.BaseDirectory, "follow-verbose.log");

		/// <summary>Log a line for whoever is walking (eg, its ITrapDodger), into the verbose log file too when Verbose.</summary>
		public void Note(string line) => VLog(line);

		private void VLog(string line) {
			Log(line);
			if ( !Verbose ) return;
			try { System.IO.File.AppendAllText(VerboseLogFile, $"{Time.ElapsedMilliseconds} {line}{Environment.NewLine}"); } catch ( Exception ) { }
		}

		public void Start(Offsets.Vector2i goal, object owner = null, bool verbose = false) {
			if ( !IsActive || goal.Id != Goal.Id ) Path = null;
			Verbose = verbose;
			if ( verbose ) {
				try { System.IO.File.WriteAllText(VerboseLogFile, $"{Time.ElapsedMilliseconds} follow to {goal} for {owner?.GetType().Name}, trap radius {TrapTouchRadius}, touch {TrapTouchMs} ms, pause {TrapPauseMs} ms{Environment.NewLine}"); } catch ( Exception ) { }
			}
			lastVerboseTrapCount = -1;
			trapTouchSince = 0;
			skippedObstacles.Clear();
			obstacleWaitSince = 0;
			Owner = owner;
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
		public void SetGoal(Offsets.Vector2i goal, object owner = null) {
			if ( !IsActive || Owner != owner ) Start(goal, owner);
			else Goal = goal;
		}

		/// <summary>True if owner is the one walking the player now.</summary>
		public bool IsActiveFor(object owner) => IsActive && Owner == owner;

		public void Stop(FollowStatus status = FollowStatus.Idle, string detail = "") {
			ReleaseMouse();
			if ( IsActive && Verbose ) VLog($"PathFollower(v): stopped: {status}: {detail}");
			IsActive = false;
			Status = status;
			Detail = detail;
		}

		private long holdUntil;
		/// <summary>
		/// Stop steering for ms (letting go of the button), still walking after: eg, while a travel skill's key goes out, so
		/// the cursor stays where it was aimed (see TravelSkills).
		/// </summary>
		public void Hold(long ms) {
			ReleaseMouse();
			holdUntil = Time.ElapsedMilliseconds + ms;
		}

		public FollowStatus Tick(Navigator nav, Terrain terrain, Offsets.Vector2i player, float z) {
			if ( !IsActive ) return Status;
			lastTick = Time.ElapsedMilliseconds;
			if ( lastTick < holdUntil ) {
				progressTime = lastTick; // (holding is not being stuck)
				Detail = "holding still for a skill";
				return Status;
			}
			var game = PoEMemory.GameRoot?.InGameState;
			if ( terrain == null || game == null ) return Wait(FollowStatus.Paused, "no terrain");
			if ( terrain.Generation != terrainGeneration ) {
				Stop(FollowStatus.Idle, "the area changed");
				return Status;
			}
			if ( game.IsPaused ) return Wait(FollowStatus.Paused, "the game is paused");
			if ( game.HasInputFocus ) return Wait(FollowStatus.Paused, "typing in the game");
			if ( PoEMemory.GameRoot?.EscapeState?.Menu != null ) return Wait(FollowStatus.Paused, "escape menu");
			if ( !GameFocused ) {
				// (steering now would only queue moves that wait for the focus: pause the game until it is back instead)
				PauseUntilFocused();
				return Wait(FollowStatus.WaitingForFocus, "the game needs focus to use the mouse");
			}

			long now = Time.ElapsedMilliseconds;
			// a lever was just pulled: give its door a moment to open before planning again (the plan would go to another lever)
			if ( Path?.Lever != null && Path.Lever.IsUsed && Path.Lever != pulledLever ) {
				pulledLever = Path.Lever;
				pulledFor = Path.LockedDoor;
				pulledAt = now;
			}
			if ( pulledLever != null && now - pulledAt < LeverDoorWaitMs && pulledFor.IsClosed ) {
				_ = terrain.Doors; // (refreshes IsClosed)
				return Wait(FollowStatus.OpeningDoor, $"lever {pulledLever.EntityId} pulled, waiting for door {pulledFor.EntityId} to unlock");
			}
			DangerMap.Get(); // (fresh, for the path's SkipDanger steps)
			Path = nav.UpdatePath(Path, terrain, player, Goal);
			if ( Path == null ) {
				Stop(FollowStatus.NoPath, $"no path to {Goal}{(nav.LastFailure == null ? "" : $": {nav.LastFailure}")}");
				return Status;
			}
			if ( Distance(player, Path.Goal) <= ArriveRadius ) {
				Stop(FollowStatus.Arrived, $"at {Path.Goal}");
				return Status;
			}
			if ( AvoidTraps(terrain, player, z, now) ) return Status;
			if ( SkipObstacles(terrain, player, z, now) ) return Status;
			if ( SkipDanger(terrain, player, z, now) ) return Status;

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
				ReleaseMouse(); // (and press again, in case the game lost the held button)
				progressTime = now;
			}

			// a closed door (or a lever) ahead: walk to it, then click it until it opens (or is pulled)
			var doorStep = Path.Steps.FirstOrDefault(s => s.Kind == NavStepKind.OpenDoor || s.Kind == NavStepKind.UseLever);
			int doorCell = Path.Cells.Count;
			if ( doorStep?.Kind == NavStepKind.UseLever ) {
				doorCell = Math.Max(0, Path.LeverIndex);
				var stand = Path.Cells[doorCell];
				if ( Distance(player, doorStep.Target) <= LeverReach || Distance(player, stand) <= ArriveRadius ) {
					ReleaseMouse();
					Status = FollowStatus.UsingLever;
					Detail = $"lever {doorStep.Lever.EntityId} (for door {doorStep.Door?.EntityId})";
					progressTime = now; // (standing at the lever is not being stuck)
					if ( now - lastDoorClick > DoorClickIntervalMs && TryGetClickPoint(doorStep.Lever.GetLabel(), doorStep.Target, z, out Vector2 at) ) {
						Click(at);
						lastDoorClick = now;
					}
					doorStep.Lever.Refresh(); // (see it used as soon as it is)
					return Status;
				}
			} else if ( doorStep != null ) {
				// the cell in front of the door is the Walk step just before the OpenDoor step
				int i = Path.Steps.IndexOf(doorStep);
				var front = i > 0 ? Path.Steps[i - 1].Target : player;
				doorCell = Math.Max(0, Path.Cells.FindIndex(c => c.Id == front.Id));
				if ( Distance(player, doorStep.Target) <= DoorReach ) {
					ReleaseMouse();
					Status = FollowStatus.OpeningDoor;
					Detail = $"door {doorStep.Door.EntityId}";
					if ( doorStep.Door.IsLocked ) {
						// (clicking a locked door does nothing; the plan goes to a lever once the Navigator knows one)
						Detail = $"door {doorStep.Door.EntityId} is locked";
						return Status;
					}
					if ( now - lastDoorClick > DoorClickIntervalMs && TryGetClickPoint(doorStep.Door.GetLabel(), doorStep.Target, z, out Vector2 at) ) {
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
			// (not on a locked strongbox: a click there says "This chest is locked", and the walk goes nowhere; unless every
			// cell ahead is on one, then as before)
			var noClick = LockedChestSpots(terrain, z);
			bool Clear(Vector2 at) => noClick.Count == 0 || !noClick.Any(b => Math.Abs(at.X - b.X) < LockedChestHalfWidth && at.Y - b.Y > -LockedChestAbove && at.Y - b.Y < LockedChestBelow);
			bool anyClear = false;
			for ( int k = aim; k > 0 && noClick.Count > 0 && !anyClear; k-- ) anyClear = TryGridToScreen(Path.Cells[k], z, out Vector2 sc) && IsSafeToClick(sc) && Clear(sc);
			// (never on a monster NoClickMonsters names, eg a frozen augury boss: a click there opens its cards, whatever else)
			var noClickMonsters = NoClickMonsterSpots();
			bool ClearOfMonsters(Vector2 at) => noClickMonsters.Count == 0 || !noClickMonsters.Any(b => Math.Abs(at.X - b.X) < NoClickMonsterHalfWidth && at.Y - b.Y > -NoClickMonsterAbove && at.Y - b.Y < NoClickMonsterBelow);
			for ( ; aim > 0; aim-- ) {
				if ( TryGridToScreen(Path.Cells[aim], z, out Vector2 screen) && IsSafeToClick(screen) && (!anyClear || Clear(screen)) && ClearOfMonsters(screen) ) {
					if ( MoveKey != VKey.None ) {
						Run(new MoveMouse(screen + WindowOffset(), new KeyDown(MoveKey, new KeyUp(MoveKey))));
					} else if ( HoldMouse ) {
						// (a held press goes out after the move; and not at all if ReleaseMouse let go before it went out)
						Run(new MoveMouse(screen + WindowOffset(), held ? null : MouseHold.Press(MouseBtn.Left)));
						held = true;
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

		/// <summary>
		/// Touching a trap for TrapTouchMs: use a travel skill past it (the Owner's ITrapDodger), else stop now and wait
		/// TrapPauseMs for it to move on. True while waiting (or a skill is going out), so the walk does nothing else.
		/// </summary>
		private bool AvoidTraps(Terrain terrain, Offsets.Vector2i player, float z, long now) {
			if ( now < trapPauseUntil ) {
				Wait(FollowStatus.AvoidingTrap, $"waiting for a trap to move on ({trapPauseUntil - now} ms)");
				return true;
			}
			var traps = terrain.Traps;
			if ( Verbose ) LogTrapsNear(traps, player, now);
			var trap = traps.Count == 0 ? null : AreaTraps.Nearest(traps, player, TrapTouchRadius);
			if ( trap == null ) {
				if ( Verbose && trapTouchSince != 0 ) VLog($"PathFollower(v): no longer touching a trap at {player}, after {now - trapTouchSince} ms (radius {TrapTouchRadius})");
				trapTouchSince = 0;
				return false;
			}
			if ( trapTouchSince == 0 ) {
				trapTouchSince = now;
				if ( Verbose ) VLog($"PathFollower(v): touching {trap} at {player}, {Distance(player, trap.GridPos):F1} cells (radius {TrapTouchRadius})");
			}
			if ( now - trapTouchSince < TrapTouchMs ) return false;
			if ( Verbose ) VLog($"PathFollower(v): touching {trap} at {player} for {now - trapTouchSince} ms: avoiding it");
			trapTouchSince = 0;
			var dodger = Owner as ITrapDodger;
			Status = FollowStatus.Walking; // (a travel skill is only used while walking)
			if ( dodger != null && dodger.TryDodgeTrap(this, terrain, player, trap.GridPos, z) ) {
				VLog($"PathFollower: touching {trap} at {player} for {TrapTouchMs} ms: used a travel skill past it");
				Status = FollowStatus.AvoidingTrap;
				Detail = $"travel skill past {trap}";
				return true;
			}
			if ( Verbose ) VLog($"PathFollower(v): no travel skill: {(dodger == null ? $"the owner ({Owner?.GetType().Name ?? "none"}) can't dodge traps" : TravelSkills.Instance.LastNote ?? "(no reason given)")}");
			ReleaseMouse();
			bool stopped = dodger?.StopMoving() ?? false;
			trapPauseUntil = now + TrapPauseMs;
			VLog($"PathFollower: touching {trap} at {player} for {TrapTouchMs} ms: {(stopped ? "tapped the main skill to stop" : "let go (no stop tap)")}, waiting {TrapPauseMs} ms for it to move on");
			Wait(FollowStatus.AvoidingTrap, $"waiting for {trap} to move on");
			return true;
		}

		/// <summary>
		/// The next SkipObstacle step (a chest or barrel the path goes through): within ObstacleReach, use a travel skill to
		/// land past it; while none is ready, stand and wait (up to ObstacleWaitMs). True while waiting or a skill went out.
		/// </summary>
		private bool SkipObstacles(Terrain terrain, Offsets.Vector2i player, float z, long now) {
			NavStep step = null;
			foreach ( var s in Path.Steps ) {
				if ( s.Kind != NavStepKind.SkipObstacle || skippedObstacles.Contains(s.Obstacle.EntityId) ) continue;
				if ( !s.Obstacle.IsStanding() ) {
					skippedObstacles.Add(s.Obstacle.EntityId);
					if ( Verbose ) VLog($"PathFollower(v): {s.Obstacle} is opened or broken");
					continue;
				}
				// nearest the path's first cell (where the player is): beside or behind the player, passed it already
				float away = Distance(player, s.Target);
				if ( s.CellIndex <= 0 ) {
					skippedObstacles.Add(s.Obstacle.EntityId);
					if ( Verbose ) VLog($"PathFollower(v): passed {s.Obstacle} ({away:F1} cells away, beside or behind) without a skill");
					continue;
				}
				step = s;
				break;
			}
			if ( step == null || Distance(player, step.Target) > ObstacleReach ) {
				obstacleWaitSince = 0;
				return false;
			}
			var obstacle = step.Obstacle;
			var dodger = Owner as ITrapDodger;
			if ( dodger == null || !dodger.HasTravelSkill ) {
				skippedObstacles.Add(obstacle.EntityId);
				VLog($"PathFollower: the path goes through {obstacle}, but {(dodger == null ? $"the owner ({Owner?.GetType().Name ?? "none"}) has no travel skills" : "there is no travel skill on the skill bar")}: walking on");
				return false;
			}
			if ( obstacleWaitFor != obstacle.EntityId ) {
				obstacleWaitFor = obstacle.EntityId;
				obstacleWaitSince = now;
				if ( Verbose ) VLog($"PathFollower(v): at {player}, {Distance(player, step.Target):F1} cells from {obstacle} (path cell {step.CellIndex}): skipping it with a travel skill");
			}
			Status = FollowStatus.Walking; // (a travel skill is only used while walking)
			if ( dodger.TrySkipObstacle(this, terrain, player, obstacle.GridPos, step.CellIndex, z) ) {
				skippedObstacles.Add(obstacle.EntityId);
				VLog($"PathFollower: used a travel skill past {obstacle} from {player} (after {now - obstacleWaitSince} ms)");
				Status = FollowStatus.SkippingObstacle;
				Detail = $"travel skill past {obstacle}";
				return true;
			}
			string why = TravelSkills.Instance.LastNote ?? "(no reason given)";
			if ( now - obstacleWaitSince > ObstacleWaitMs ) {
				skippedObstacles.Add(obstacle.EntityId);
				VLog($"PathFollower: no travel skill past {obstacle} in {ObstacleWaitMs} ms ({why}): walking on");
				return false;
			}
			if ( Verbose && now - lastVerboseLog >= 250 ) {
				lastVerboseLog = now;
				VLog($"PathFollower(v): waiting at {player} to skip {obstacle}: {why}");
			}
			Wait(FollowStatus.SkippingObstacle, $"waiting for a travel skill past {obstacle}: {why}");
			return true;
		}
		private long dangerWaitSince, dangerGiveUpUntil;

		/// <summary>
		/// The next SkipDanger step (the path crosses danger, see Navigator.AddDangerSteps): within DangerReach, use a travel
		/// skill over it; while none is ready, stand outside it (up to DangerWaitMs). True while waiting or a skill went out.
		/// </summary>
		private bool SkipDanger(Terrain terrain, Offsets.Vector2i player, float z, long now) {
			var step = Path.Steps.FirstOrDefault(s => s.Kind == NavStepKind.SkipDanger);
			bool inside = step != null && step.CellIndex == 0;
			if ( step == null || now < dangerGiveUpUntil || (!inside && Distance(player, step.Target) > DangerReach) ) {
				dangerWaitSince = 0;
				return false;
			}
			var dodger = Owner as ITrapDodger;
			if ( dodger == null || !dodger.HasTravelSkill ) return false; // (walk through it)
			if ( dangerWaitSince == 0 ) {
				dangerWaitSince = now;
				if ( Verbose ) VLog($"PathFollower(v): at {player}, the path crosses danger at {step.Target} (path cells {step.CellIndex} to {step.EndIndex}): skipping it with a travel skill");
			}
			Status = FollowStatus.Walking; // (a travel skill is only used while walking)
			if ( dodger.TrySkipDanger(this, terrain, player, step.EndIndex, z) ) {
				VLog($"PathFollower: used a travel skill over danger at {step.Target} from {player} (after {now - dangerWaitSince} ms)");
				dangerWaitSince = 0;
				Status = FollowStatus.SkippingDanger;
				Detail = $"travel skill over danger at {step.Target}";
				return true;
			}
			if ( inside ) return false; // (in it already: walk on out rather than stand in it)
			string why = TravelSkills.Instance.LastNote ?? "(no reason given)";
			if ( now - dangerWaitSince > DangerWaitMs ) {
				dangerGiveUpUntil = now + DangerGiveUpMs;
				dangerWaitSince = 0;
				VLog($"PathFollower: no travel skill over danger at {step.Target} in {DangerWaitMs} ms ({why}): walking through");
				return false;
			}
			Wait(FollowStatus.SkippingDanger, $"waiting for a travel skill over danger at {step.Target}: {why}");
			return true;
		}

		/// <summary>Verbose: within this many cells, traps are logged (at most each VerboseLogMs).</summary>
		public float VerboseTrapRadius = 25f;
		public long VerboseLogMs = 100;
		private long lastVerboseLog;
		private int lastVerboseTrapCount = -1;

		private void LogTrapsNear(System.Collections.Generic.IReadOnlyList<AreaTrap> traps, Offsets.Vector2i player, long now) {
			if ( traps.Count != lastVerboseTrapCount ) {
				lastVerboseTrapCount = traps.Count;
				VLog($"PathFollower(v): {traps.Count} traps known: {string.Join(", ", traps)}");
			}
			if ( now - lastVerboseLog < VerboseLogMs ) return;
			var near = new System.Collections.Generic.List<string>();
			foreach ( var t in traps ) {
				if ( !t.Refresh() ) { near.Add($"{t.EntityId} gone"); continue; }
				float d = Distance(player, t.GridPos);
				if ( d <= VerboseTrapRadius ) near.Add($"{t.EntityId} at {t.GridPos} {d:F1}");
			}
			if ( near.Count == 0 ) return;
			lastVerboseLog = now;
			VLog($"PathFollower(v): player {player}, {Status}; traps near: {string.Join("; ", near)}");
		}

		private FollowStatus Wait(FollowStatus status, string why) {
			ReleaseMouse();
			Status = status;
			Detail = why;
			progressTime = Time.ElapsedMilliseconds; // waiting is not being stuck
			return Status;
		}

		/// <summary>Let go of the left button, if walking is holding it down (it is pressed again on the next step).</summary>
		public void ReleaseMouse() {
			if ( !held ) return;
			held = false;
			Run(MouseHold.Release(MouseBtn.Left));
		}

		/// <summary>Call each frame: lets go of the left button if whoever was walking stopped ticking (eg, was switched off).</summary>
		public void ReleaseIfNotTicked() {
			if ( held && Time.ElapsedMilliseconds - lastTick > 250 ) ReleaseMouse();
		}

		/// <summary>Where to click a door or lever: the middle of its label, or with no label showing, the thing itself.</summary>
		private static bool TryGetClickPoint(Element label, Offsets.Vector2i target, float z, out Vector2 at) {
			if ( IsValid(label) ) {
				var rect = label.GetClientRect();
				at = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
				if ( IsSafeToClick(at) ) return true;
			}
			return TryGridToScreen(target, z, out at) && IsSafeToClick(at);
		}

		/// <summary>The box around a locked strongbox (see AreaObstacles.IsLockedStrongbox) not to click in, in pixels: each side, above (its lid), below its foot.</summary>
		public static float LockedChestHalfWidth = 70, LockedChestAbove = 120, LockedChestBelow = 45;

		// the screen positions of the locked strongboxes (see AreaObstacles.IsLockedStrongbox) near enough to be on screen
		private static System.Collections.Generic.List<Vector2> LockedChestSpots(Terrain terrain, float z) {
			var spots = new System.Collections.Generic.List<Vector2>();
			foreach ( var o in terrain.Obstacles ) {
				if ( !o.Path.StartsWith("Metadata/Chests/StrongBoxes/", StringComparison.Ordinal) || !o.IsLocked() ) continue;
				if ( TryGridToScreen(o.GridPos, z, out Vector2 at) ) spots.Add(at);
			}
			return spots;
		}

		/// <summary>
		/// Monsters a walking click must not land on (eg, the map's frozen augury boss while its fates are not all taken: a
		/// click on it opens its card window, see MappingRoutine); null or empty for none. A cell ahead on one is not
		/// clicked, even with no other cell to click.
		/// </summary>
		public static Func<System.Collections.Generic.IEnumerable<Entity>> NoClickMonsters;
		/// <summary>The box around a NoClickMonsters monster not to click in, in pixels: each side, above its foot, below it.</summary>
		public static float NoClickMonsterHalfWidth = 110, NoClickMonsterAbove = 200, NoClickMonsterBelow = 70;

		// the screen positions of the NoClickMonsters on screen
		private static System.Collections.Generic.List<Vector2> NoClickMonsterSpots() {
			var spots = new System.Collections.Generic.List<Vector2>();
			var monsters = NoClickMonsters?.Invoke();
			if ( monsters == null ) return spots;
			foreach ( var e in monsters ) {
				if ( e != null && TryWorldToScreen(Position(e), out Vector2 at) ) spots.Add(at);
			}
			return spots;
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

		// (the mouse states, run on the default Machine: they wait for the game to have the focus)
		private static void Click(Vector2 clientPos) => Run(new MoveMouse(clientPos + WindowOffset(), new LeftMouseDown(new LeftMouseUp())));

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
