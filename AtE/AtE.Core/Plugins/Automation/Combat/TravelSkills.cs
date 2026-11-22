using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The skill bar's travel skills (tagged Travel, eg Frostblink, Leap Slam), looked for once every RefreshMs, used while
	/// walking on a straight stretch of the path ahead. A Blink skill (also tagged Blink: a teleport) goes along any line a
	/// projectile would fly (Terrain.HasLineOfSight), so it may cut a corner of the path; the others only along a stretch
	/// where the path itself runs straight and walkable. A Blink skill is preferred; one on cooldown (or that can't be
	/// used) is passed over for any other that can.
	///
	/// A State that runs forever (OnTick returns this), on the default Machine: Start puts the one Instance there, once.
	/// Each frame it works for whichever combat driver the path follower is walking for (if that driver is on, and its
	/// UseTravelSkills is set), sending the skill through the driver's input (CombatDriver.Send).
	/// </summary>
	public class TravelSkills : State {
		/// <summary>The one instance (see Start).</summary>
		public static readonly TravelSkills Instance = new TravelSkills();
		private static bool started;
		/// <summary>Run the Instance on the default Machine, if not already.</summary>
		public static void Start() {
			if ( started ) return;
			started = true;
			Run(Instance);
		}

		public override IState OnTick(long dt) {
			if ( !PoEMemory.IsAttached ) return this;
			var driver = DefaultFollower.Owner as CombatDriver;
			if ( driver == null || !driver.IsActive || !driver.UseTravelSkills ) return this;
			var terrain = GetTerrain();
			var player = GetPlayer();
			var pos = player?.GetComponent<Positioned>();
			if ( terrain == null || !IsValid(pos) ) return this;
			TryUse(driver, terrain, pos.GridPos, Position(player).Z);
			return this;
		}

		/// <summary>How often to look at the skill bar again.</summary>
		public long RefreshMs = 60000;
		/// <summary>Use one only toward a point at least this many cells away (a shorter stretch is walked).</summary>
		public float MinCells = 20;
		/// <summary>Aim at most this many cells ahead (past a skill's own range it goes as far as it can, along the same line).</summary>
		public float MaxCells = 45;
		/// <summary>How far a path cell may be from the straight line and still count as on it.</summary>
		public float StraightTolerance = 1.5f;
		/// <summary>The least time between uses.</summary>
		public long PaceMs = 600;

		public struct Travel {
			public string Name;
			public bool Blink;
			public override string ToString() => Blink ? $"{Name} (Blink)" : Name;
		}

		/// <summary>The travel skills found the last time the skill bar was looked at (null: not looked yet).</summary>
		public List<Travel> Skills { get; private set; }
		private long refreshedAt = -1000000, lastUse;

		/// <summary>Look at the skill bar again (each RefreshMs, or now if force).</summary>
		public void Refresh(bool force = false) {
			long now = Time.ElapsedMilliseconds;
			if ( !force && Skills != null && now - refreshedAt < RefreshMs ) return;
			refreshedAt = now;
			var found = new List<Travel>();
			foreach ( var slot in SkillBar.GetSlots() ) {
				var skill = slot.Skill;
				if ( skill == null || SkillBar.IsMovement(skill) ) continue;
				var tags = SkillBar.SkillTags(skill);
				if ( !tags.Contains("Travel", StringComparer.OrdinalIgnoreCase) ) continue;
				string name = SkillBar.SkillName(skill);
				if ( found.Any(t => t.Name == name) ) continue;
				found.Add(new Travel { Name = name, Blink = tags.Contains("Blink", StringComparer.OrdinalIgnoreCase) });
			}
			string before = Skills == null ? null : string.Join(", ", Skills);
			Skills = found;
			string after = string.Join(", ", found);
			if ( before != after ) Log($"TravelSkills: {(found.Count == 0 ? "none on the skill bar" : after)}");
		}

		/// <summary>
		/// Walking (the follower steering for driver): use a travel skill along the straight stretch ahead, if there is one
		/// long enough and a skill ready. True if one was used.
		/// </summary>
		public bool TryUse(CombatDriver driver, Terrain terrain, Offsets.Vector2i me, float z) =>
			UseAlong(driver, () => driver.InputBusy, driver.Send, terrain, me, z, MinCells, null, "along the path");

		/// <summary>How near (in cells) a dodge may land to the trap it gets past.</summary>
		public float DodgeClearance = 10;
		/// <summary>The shortest dodge (in cells).</summary>
		public float MinDodgeCells = 8;

		/// <summary>
		/// Touching the trap at trap while walking (see PathFollower.TrapTouchMs): use a travel skill to a cell of the path
		/// ahead at least DodgeClearance from it (and MinDodgeCells away). True if one was used.
		/// </summary>
		public bool TryDodge(CombatDriver driver, Terrain terrain, Offsets.Vector2i me, Offsets.Vector2i trap, float z) =>
			TryDodge(driver, () => driver.InputBusy, driver.Send, terrain, me, trap, z);

		/// <summary>True if there is a travel skill on the skill bar (ready or not).</summary>
		public bool HasAny {
			get {
				Refresh();
				return Skills != null && Skills.Count > 0;
			}
		}

		/// <summary>How far past an obstacle (in cells) a skip must land.</summary>
		public float ObstacleClearance = 5;

		/// <summary>
		/// The path goes through a chest or barrel (a SkipObstacle step, at path cell obstacleIndex): use a travel skill to a
		/// cell of the path beyond it, at least ObstacleClearance from it. True if one was used.
		/// </summary>
		public bool TrySkip(object owner, Func<bool> busy, Func<IState, bool> send, Terrain terrain, Offsets.Vector2i me, Offsets.Vector2i obstacle, int obstacleIndex, float z) =>
			UseAlong(owner, busy, send, terrain, me, z, MinDodgeCells,
				(k, c) => k > obstacleIndex && Combat.Distance(c, obstacle) >= ObstacleClearance, $"past an obstacle at {obstacle}");

		/// <summary>
		/// The path crosses danger (a SkipDanger step, see Navigator.AddDangerSteps): use a travel skill to a safe cell
		/// (DangerMap.IsSafe) of the path at or past landIndex. True if one was used.
		/// </summary>
		public bool TrySkipDanger(object owner, Func<bool> busy, Func<IState, bool> send, Terrain terrain, Offsets.Vector2i me, int landIndex, float z) {
			var map = DangerMap.Current;
			return UseAlong(owner, busy, send, terrain, me, z, MinDodgeCells,
				(k, c) => k >= landIndex && (map == null || map.IsSafe(c)), $"over danger, to path cell {landIndex}");
		}

		/// <summary>TryDodge for any owner of the follower: busy says input is still going out, send queues more (false if it can't).</summary>
		public bool TryDodge(object owner, Func<bool> busy, Func<IState, bool> send, Terrain terrain, Offsets.Vector2i me, Offsets.Vector2i trap, float z) =>
			UseAlong(owner, busy, send, terrain, me, z, MinDodgeCells, (k, c) => Combat.Distance(c, trap) >= DodgeClearance, $"past a trap at {trap}");

		/// <summary>Why the last TryUse / TryDodge used nothing (null if it used a skill).</summary>
		public string LastNote { get; private set; }
		private bool Fail(string why) {
			LastNote = why;
			return false;
		}

		// a travel skill to the furthest cell of the path ahead that a straight line reaches, at least minCells away, and
		// (if landOk is given) where landOk says it may land
		private bool UseAlong(object owner, Func<bool> busy, Func<IState, bool> send, Terrain terrain, Offsets.Vector2i me, float z, float minCells,
			Func<int, Offsets.Vector2i, bool> landOk, string why) {
			LastNote = null;
			var notes = new List<string>();
			Refresh();
			if ( Skills == null || Skills.Count == 0 ) return Fail("no travel skills on the skill bar");
			if ( !DefaultFollower.IsActiveFor(owner) || DefaultFollower.Status != FollowStatus.Walking ) return Fail($"the follower is not walking for this owner ({DefaultFollower.Status})");
			long now = Time.ElapsedMilliseconds;
			if ( busy() || now - lastUse < PaceMs ) return Fail(busy() ? "input still going out" : $"used one {now - lastUse} ms ago (PaceMs {PaceMs})");
			if ( GetCurrentArea()?.IsTown ?? false ) return Fail("in town"); // (skills are paused in town)
			var cells = DefaultFollower.Path?.Cells;
			if ( cells == null || cells.Count < 2 ) return Fail("no path");

			// where the player is on the path: the nearest of its first cells
			int start = 0;
			float best = float.MaxValue;
			for ( int i = 0; i < Math.Min(cells.Count, 40); i++ ) {
				float d = Combat.Distance(me, cells[i]);
				if ( d < best ) { best = d; start = i; }
			}
			if ( best > 4 ) return Fail($"off the path ({best:F1} cells from it)"); // (off the path: it is being planned again)

			// the furthest cell ahead (within MaxCells) a straight line from the player reaches: walking (the path runs
			// straight, and the line is walkable), and as a projectile flies (for a Blink)
			int walkEnd = -1, blinkEnd = -1;
			bool walking = true, blinking = true;
			for ( int k = start + 1; k < cells.Count && (walking || blinking); k++ ) {
				if ( Combat.Distance(me, cells[k]) > MaxCells ) break;
				if ( blinking ) {
					if ( terrain.HasLineOfSight(me, cells[k]) ) blinkEnd = k;
					else blinking = false;
				}
				if ( walking ) {
					if ( terrain.IsLineClear(me, cells[k]) && OnLine(me, cells[k], cells, start, k) ) walkEnd = k;
					else walking = false;
				}
			}

			foreach ( var travel in Skills.OrderByDescending(t => t.Blink) ) {
				int end = travel.Blink ? Math.Max(walkEnd, blinkEnd) : walkEnd;
				if ( landOk != null ) {
					while ( end > start && !landOk(end, cells[end]) ) end -= 1;
				}
				if ( end <= start || Combat.Distance(me, cells[end]) < minCells ) { notes.Add($"{travel}: no landing cell (end {end}, start {start})"); continue; }
				var slot = SkillBar.FindSlot(travel.Name);
				var skill = slot?.Skill;
				if ( skill == null || !skill.CanBeUsed || skill.IsOnCooldown ) { notes.Add($"{travel}: {(skill == null ? "not on the bar" : skill.IsOnCooldown ? "on cooldown" : "can't be used")}"); continue; }
				if ( !TryGridToScreen(cells[end], z, out Vector2 screen) || !PathFollower.IsSafeToClick(screen) ) { notes.Add($"{travel}: landing cell {cells[end]} not safe to click"); continue; }
				// (not with the cursor on a frozen augury boss: that opens its card window)
				if ( !PathFollower.ClearOfNoClick(screen) ) { notes.Add($"{travel}: landing cell {cells[end]} is on a monster not to click"); continue; }
				// let go of the walk, and hold it until the key is out (the cursor stays where the skill was aimed)
				DefaultFollower.Hold(Math.Max(250, SkillBar.CastTimeMs(skill)) + 100);
				if ( !send(Inputs.UseSlot(slot, screen, 40)) ) return Fail("input refused");
				lastUse = now;
				Log($"TravelSkills: {travel} toward {cells[end]}, {Combat.Distance(me, cells[end]):F0} cells {why}");
				return true;
			}
			return Fail(string.Join("; ", notes));
		}

		// are the path's cells from..to all within StraightTolerance of the line a-b
		private bool OnLine(Offsets.Vector2i a, Offsets.Vector2i b, List<Offsets.Vector2i> cells, int from, int to) {
			float dx = b.X - a.X, dy = b.Y - a.Y;
			float len = MathF.Sqrt((dx * dx) + (dy * dy));
			if ( len < 1 ) return true;
			for ( int i = from; i <= to; i++ ) {
				float off = MathF.Abs(((cells[i].X - a.X) * dy) - ((cells[i].Y - a.Y) * dx)) / len;
				if ( off > StraightTolerance ) return false;
			}
			return true;
		}
	}
}
