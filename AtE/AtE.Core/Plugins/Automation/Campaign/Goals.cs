using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>Fight everything near and explore the whole area.</summary>
	public class ClearArea : Goal {
		public string Skill;
		/// <param name="skill">The skill to fight with, eg "Fireball"; null uses the driver's Attack.</param>
		public ClearArea(string skill = null) => Skill = skill;
		public override string Name => "Clear the area";
		public override bool CheckDone(CombatContext ctx) => ctx.Driver.ExploreDone && ctx.Hostiles.Count == 0;
		protected override IState Step(CombatContext ctx) => FightFirst(ctx, 0, Skill) ?? Call(new Explore(null) { FightSkill = Skill });
	}

	/// <summary>
	/// Kill a particular monster (by a part of its metadata path, or any test): explore (fighting whatever is in the
	/// way) until it is seen, then fight it.
	/// </summary>
	public class Kill : Goal {
		public readonly string Label;
		public readonly Func<Entity, bool> Match;
		/// <summary>The skill to use on it, asked when it is used (eg, the campaign's main skill); null uses the driver's Attack.</summary>
		public Func<string> SkillOf;
		public string Skill => SkillOf?.Invoke();
		public float Range;
		public bool? NeedsLineOfSight;
		private bool killed;
		private Offsets.Vector2i lastSeen;
		private bool seen;

		/// <param name="label">How to show it, eg "Hillock".</param>
		/// <param name="pathContains">A part of the monster's metadata path.</param>
		/// <param name="skill">The skill to use on it (eg, "Fireball", "Default Attack"); null uses the driver's Attack.</param>
		/// <param name="range">How close to get, in cells; 0 uses the driver's AttackRange.</param>
		public Kill(string label, string pathContains, string skill = null, float range = 0, bool? needsLineOfSight = null)
			: this(label, e => e.Path?.Contains(pathContains) ?? false, skill == null ? null : () => skill, range, needsLineOfSight) { }

		/// <param name="match">Which entities are the monster (eg, by path and name).</param>
		public Kill(string label, Func<Entity, bool> match, Func<string> skill = null, float range = 0, bool? needsLineOfSight = null) {
			Label = label; Match = match; SkillOf = skill; Range = range; NeedsLineOfSight = needsLineOfSight;
		}
		public override string Name => $"Kill {Label}{(Skill == null ? "" : $" with {Skill}")}";
		public override void Reset() { base.Reset(); killed = seen = false; }

		private IEnumerable<Entity> Matches() => GetEntities()?.Where(e => e != null && Match(e)) ?? Empty<Entity>();

		public override bool CheckDone(CombatContext ctx) {
			if ( killed ) return true;
			foreach ( var ent in Matches() ) {
				var life = ent.GetComponent<Life>();
				if ( IsValid(life) && life.CurHP <= 0 ) return killed = true;
			}
			return false;
		}

		protected override IState Step(CombatContext ctx) {
			var ent = Matches().FirstOrDefault(e => IsAlive(e));
			if ( ent != null ) {
				var target = Combat.ToHostile(ent, ctx.Grid);
				if ( target != null ) {
					seen = true;
					lastSeen = target.GridPos;
					if ( Combat.ClickReason(ent) is string why ) {
						ctx.Driver.ClickMonster(ctx, target);
						Detail = $"clicking it, {why}";
						return this;
					}
					if ( !IsTargetable(ent) || Combat.CannotBeDamaged(ent) ) {
						// eg, a boss that has not woken up yet, or is invulnerable for a phase: get close and wait
						Detail = IsTargetable(ent) ? "waiting for it to be damageable" : "waiting for it to be targetable";
						if ( target.Distance > 20 ) ctx.Driver.WalkTo(Approach(ctx, target.GridPos), $"walking to {Label}");
						else ctx.Driver.StopWalking("waiting");
						return this;
					}
					// a special target near (eg, a "Nearby Allies Cannot Die" totem): kill it first, or this one can't die
					// (not while killing a special target itself: two would hand the fight back and forth)
					if ( !Combat.IsSpecialTarget(ent) && ctx.Hostiles.FirstOrDefault(h => h.Entity?.Id != ent.Id && Combat.IsSpecialTarget(h.Entity)) is Hostile special ) {
						Detail = $"killing {special} first";
						return Call(AtE.Fight.AvoidSpamTarget(special, null));
					}
					Fight(ctx, target);
					return this;
				}
			}
			// not in sight: go back to where it was last seen, else keep looking
			if ( seen ) {
				if ( !(SearchAt != null && SearchPastMonsters) && FightFirst(ctx, 0, Skill) is IState fight ) { Detail = "fighting on the way"; return fight; }
				Detail = "going back to where it was last seen";
				return Call(new Navigate(Label, _ => lastSeen, 5) { FightRadius = -1, LegMs = 1000 });
			}
			return Search(ctx, Label, Skill);
		}

		/// <summary>Fight it, in sight and targetable: attack it from where it is in range.</summary>
		protected virtual void Fight(CombatContext ctx, Hostile target) {
			Detail = $"fighting, {target.Distance:F0} cells away";
			if ( ctx.Driver.Engage(ctx, target, Skill, Range, NeedsLineOfSight) ) return;
			Detail = "can't find a spot to attack from";
			ctx.Driver.WalkTo(Approach(ctx, target.GridPos), $"walking to {Label}");
		}
	}

	/// <summary>
	/// Kill a boss by circling it (eg, Hillock): use the skill at it (standing still until the cast is out), walk to the
	/// next of four points around it (north, west, south, east on screen, counter-clockwise, Radius cells from it), use
	/// the skill again, and so on round. If the walk takes longer than MaxWalkMs, it uses the skill on the way and then
	/// goes on to the same point. A point that can't be walked to, can't see the boss, or is near a wall or obstacle (its
	/// cell's clearance in the Terrain grid below MinClearance) is skipped; with every point skipped, it uses the skill
	/// from where it stands (walking only to get in range and sight of it).
	/// </summary>
	public class Orbit : Kill {
		public float Radius;
		/// <summary>How far from it to circle, in cells, with a melee skill (when Radius is 0).</summary>
		public float MeleeRadius = 8;
		/// <summary>The least clearance (Terrain.Clearance: 1 beside a wall, up to 5 in the open) of a point to circle to.</summary>
		public int MinClearance = 3;
		private bool allSkipped; // every point was skipped last time (logged once, until one is good again)
		public long MaxWalkMs = 1500;
		private int point = -1; // the point being walked to, an index into Cardinal; -1 for none yet
		private bool castDue = true;
		private long walkStart, castUntil, aimSince;
		public long AimMs = 150; // standing still with the cursor on it, before each cast
		public float CastFraction = 2f / 3; // a cast can be cancelled after this much of its time and still go off (Fireball: 500 of 750 ms)

		// screen directions, counter-clockwise from north (screen y is down): the order the points are visited
		private static readonly Vector2[] Cardinal = { new Vector2(0, -1), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(1, 0) };
		private const string CardinalNames = "NWSE";

		/// <param name="radius">How far from it to circle, in cells; 0 for MeleeRadius with a melee skill, else 60% of the driver's AttackRange.</param>
		public Orbit(string label, string pathContains, Func<string> skill, float radius = 0)
			: base(label, e => e.Path?.Contains(pathContains) ?? false, skill) => Radius = radius;

		/// <param name="match">Which entities are the monster (eg, by its name).</param>
		public Orbit(string label, Func<Entity, bool> match, Func<string> skill, float radius = 0)
			: base(label, match, skill) => Radius = radius;

		public override string Name => $"Kill {Label}{(string.IsNullOrEmpty(Skill) ? "" : $" with {Skill}")}, circling it";
		public override void Reset() { base.Reset(); point = -1; castDue = true; castUntil = aimSince = progressAt = 0; }

		protected override void Fight(CombatContext ctx, Hostile target) {
			string skill = Skill;
			float radius = Radius > 0 ? Radius
				: SkillBar.IsMelee(SkillBar.FindSlot(skill)?.Skill) ? MeleeRadius
				: ctx.Driver.AttackRange * .6f;
			if ( Time.ElapsedMilliseconds < castUntil ) {
				ctx.Driver.StopWalking("casting");
				Detail = $"casting {skill}";
				progressAt = 0; // (standing still on purpose: the walk's progress starts over after)
				return;
			}
			var dirs = GridDirections(target.GridPos, ctx.Z);
			if ( castDue ) {
				// in range of it from anywhere on the circle (and a bit outside): use the skill; else get in range first
				bool inRange = Combat.Distance(ctx.Grid, target.GridPos) <= radius * 1.5f && ctx.Terrain.HasLineOfSight(ctx.Grid, target.GridPos);
				if ( !inRange || !TryWorldToScreen(target.WorldPos, out Vector2 screen) || !PathFollower.IsSafeToClick(screen) ) {
					// (Engage walks to within 80% of the range given, which is inside the 1.5 above)
					aimSince = 0;
					Detail = $"getting near it, {target.Distance:F0} cells away";
					if ( !ctx.Driver.Engage(ctx, target, skill, radius * 1.4f, true) ) base.Fight(ctx, target);
					return;
				}
				ctx.Driver.StopWalking("using a skill");
				// the cast goes where the cursor is when it starts: stop, and keep the cursor on it for a moment first
				// (only while aiming: input queued this frame makes the driver refuse the skill, see CombatDriver.InputBusy;
				// using the skill moves the cursor there itself)
				if ( aimSince == 0 ) aimSince = Time.ElapsedMilliseconds;
				if ( Time.ElapsedMilliseconds - aimSince < AimMs ) {
					ctx.Driver.Send(Inputs.MoveTo(screen));
					Detail = "aiming at it";
					return;
				}
				if ( ctx.Driver.TryPlaceTotem(ctx, screen) ) { Detail = "placing a totem"; return; }
				Detail = ctx.Driver.InputBusy ? $"using {skill} at it (waiting for the input before it)" : $"using {skill} at it";
				if ( !ctx.Driver.UseSkillPaced(skill, screen) ) return;
				aimSince = 0;
				castDue = false;
				// a click to walk too soon would cancel the cast: stand still until it is out (CastFraction of the cast time,
				// plus a little for the key press, which goes out on the next frame)
				int castMs = Math.Max(300, SkillBar.CastTimeMs(SkillBar.FindSlot(skill)?.Skill));
				castUntil = Time.ElapsedMilliseconds + (long)(castMs * CastFraction) + 50;
				Log($"Orbit: {skill} at {Label}, {target.Distance:F0} cells away, holding still {castUntil - Time.ElapsedMilliseconds} ms");
				walkStart = castUntil;
				if ( point < 0 ) point = NextPoint(ctx.Grid, target.GridPos, dirs);
				return;
			}
			// walk to the point (or the next one that can be walked to and sees it)
			for ( int tries = 0; tries < Cardinal.Length; tries++ ) {
				if ( TrySpot(ctx, target.GridPos, dirs[point], radius, MinClearance, out var spot) ) {
					allSkipped = false;
					if ( Combat.Distance(ctx.Grid, spot) <= 3 ) {
						// there: use the skill again, then on to the next one
						castDue = true;
						point = (point + 1) % Cardinal.Length;
						progressAt = 0;
					} else if ( IsBlocked(ctx, spot, out bool monsters) && monsters ) {
						// monsters in the way (eg, the boss's minions): clear a way, with the skill toward the point
						CastToward(ctx, skill, spot);
					} else if ( Time.ElapsedMilliseconds - progressAt >= StalledMs && progressAt != 0 ) {
						// no nearer for a while, and no monsters near: held up by something not in the grid (eg, the boss's
						// own body, between the player and the point on its far side): on to the next point
						int from = point;
						point = (point + 1) % Cardinal.Length;
						progressAt = 0;
						castDue = true;
						Log($"Orbit: no nearer to the {CardinalNames[from]} of {Label} in {StalledMs} ms: on to the {CardinalNames[point]}");
					} else if ( Time.ElapsedMilliseconds - walkStart > MaxWalkMs ) {
						castDue = true; // a long walk: use the skill on the way, then keep going to this point
					} else {
						Detail = $"circling it, to the {CardinalNames[point]}";
						ctx.Driver.WalkTo(spot, $"circling {Label}");
					}
					return;
				}
				point = (point + 1) % Cardinal.Length;
				progressAt = 0;
			}
			// nowhere around it to stand clear of walls: use the skill from here (castDue walks only to get in range and sight)
			if ( !allSkipped ) Log($"Orbit: no point around {Label} is clear of walls (clearance {MinClearance}+): using {skill} from here");
			allSkipped = true;
			Detail = "no point around it is clear of walls: attacking from here";
			castDue = true;
		}

		/// <summary>Walking to a point counts as blocked when it has not got a cell nearer in this long, with monsters near.</summary>
		public long BlockedMs = 600;
		/// <summary>Walking to a point that has got no nearer in this long (with no monsters near) gives it up for the next.</summary>
		public long StalledMs = 1000;
		/// <summary>How near (in cells) a monster has to be to count as in the way.</summary>
		public float BlockRadius = 12;
		/// <summary>How far toward the point (in cells) to aim when clearing the way.</summary>
		public float BlockedAimCells = 15;
		private long progressAt; // when the walk to the point last got nearer; 0 to start over
		private float progressDistance;

		// is the walk to spot held up: no nearer for BlockedMs (monsters: with one within BlockRadius); keeps progressAt
		private bool IsBlocked(CombatContext ctx, Offsets.Vector2i spot, out bool monsters) {
			monsters = false;
			float d = Combat.Distance(ctx.Grid, spot);
			long now = Time.ElapsedMilliseconds;
			if ( progressAt == 0 || d < progressDistance - 1 ) {
				progressAt = now;
				progressDistance = d;
				return false;
			}
			if ( now - progressAt < BlockedMs ) return false;
			monsters = ctx.Hostiles.Any(h => h.Distance <= BlockRadius);
			return true;
		}

		// use the skill once toward spot (at most BlockedAimCells away), standing still until the cast is out
		private void CastToward(CombatContext ctx, string skill, Offsets.Vector2i spot) {
			Detail = $"blocked on the way to the {CardinalNames[point]}: {skill} toward it";
			float d = Combat.Distance(ctx.Grid, spot);
			var aim = d <= BlockedAimCells ? spot : new Offsets.Vector2i(
				ctx.Grid.X + (int)MathF.Round((spot.X - ctx.Grid.X) * BlockedAimCells / d),
				ctx.Grid.Y + (int)MathF.Round((spot.Y - ctx.Grid.Y) * BlockedAimCells / d));
			if ( !TryGridToScreen(aim, ctx.Z, out Vector2 screen) || !PathFollower.IsSafeToClick(screen) ) {
				progressAt = 0; // can't aim there: keep walking, and look again
				return;
			}
			ctx.Driver.StopWalking("clearing the way");
			if ( !ctx.Driver.UseSkillPaced(skill, screen) ) return; // (input still going out: again next frame)
			int castMs = Math.Max(300, SkillBar.CastTimeMs(SkillBar.FindSlot(skill)?.Skill));
			castUntil = Time.ElapsedMilliseconds + (long)(castMs * CastFraction) + 50;
			progressAt = 0;
			Log($"Orbit: blocked on the way to the {CardinalNames[point]} of {Label}: {skill} toward it");
		}

		// where to stand on the point in direction dir from center: a cell that can be walked to, near it, that sees center,
		// and is at least minClearance from walls and obstacles (Terrain.Clearance)
		private static bool TrySpot(CombatContext ctx, Offsets.Vector2i center, Vector2 dir, float radius, int minClearance, out Offsets.Vector2i spot) {
			var want = new Offsets.Vector2i((int)MathF.Round(center.X + (dir.X * radius)), (int)MathF.Round(center.Y + (dir.Y * radius)));
			return DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, want, out spot)
				&& ctx.Terrain.Clearance(spot) >= minClearance
				&& Combat.Distance(spot, want) <= radius * .5f
				&& ctx.Terrain.HasLineOfSight(spot, center);
		}

		// the next point (in Cardinal's order) after the player's place around center (at least 30 degrees on, so it is a real move)
		private static int NextPoint(Offsets.Vector2i player, Offsets.Vector2i center, Vector2[] dirs) {
			var at = new Vector2(player.X - center.X, player.Y - center.Y);
			float playerAngle = MathF.Atan2(Cross(dirs[0], at), Vector2.Dot(dirs[0], at)); // from the north point, in grid space
			int best = 0;
			float bestTurn = float.MaxValue;
			for ( int i = 0; i < dirs.Length; i++ ) {
				float turn = MathF.Atan2(Cross(dirs[0], dirs[i]), Vector2.Dot(dirs[0], dirs[i])) - playerAngle;
				turn *= MathF.Sign(Cross(dirs[0], dirs[1])); // the way round from the first point to the second
				while ( turn < MathF.PI / 6 ) turn += 2 * MathF.PI;
				if ( turn < bestTurn ) { bestTurn = turn; best = i; }
			}
			return best;
		}
		private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

		// the grid direction (unit) that looks like each of Cardinal on screen (the camera is isometric); grid axes if off screen
		private static Vector2[] GridDirections(Offsets.Vector2i center, float z) {
			var dirs = (Vector2[])Cardinal.Clone();
			if ( !TryGridToScreen(center, z, out Vector2 origin) ) return dirs;
			var best = new float[Cardinal.Length];
			for ( int i = 0; i < best.Length; i++ ) best[i] = float.MinValue;
			for ( int deg = 0; deg < 360; deg += 5 ) {
				var g = new Vector2(MathF.Cos(deg * MathF.PI / 180), MathF.Sin(deg * MathF.PI / 180));
				var cell = new Offsets.Vector2i((int)MathF.Round(center.X + (g.X * 20)), (int)MathF.Round(center.Y + (g.Y * 20)));
				if ( !TryGridToScreen(cell, z, out Vector2 s) || s == origin ) continue;
				var sd = Vector2.Normalize(s - origin);
				for ( int i = 0; i < Cardinal.Length; i++ ) {
					float dot = Vector2.Dot(sd, Cardinal[i]);
					if ( dot > best[i] ) { best[i] = dot; dirs[i] = g; }
				}
			}
			return dirs;
		}
	}

	/// <summary>
	/// Pick up an item from the ground, or click any other label on the ground (eg, an NPC's): found by the text of its
	/// label (eg, "Large Chest"), or by a test of the item (eg, any weapon). Done once a label it clicked is gone.
	/// </summary>
	public class PickUp : Goal {
		/// <summary>The label text to look for; null when found by ItemMatch.</summary>
		public readonly string LabelText;
		/// <summary>A test of the item (the item a WorldItem holds, else the label's entity), when not found by text.</summary>
		public readonly Func<Entity, bool> ItemMatch;
		public readonly string What;
		public readonly string Verb;
		/// <summary>Click the label when within this many cells of the item.</summary>
		public float Reach = 12;
		private bool pickedUp;
		private long lastClick;
		private bool clicked;

		/// <param name="verb">For the on-screen list: "Pick up", or eg "Click" for a label that is not an item.</param>
		public PickUp(string labelText, string verb = "Pick up") { LabelText = labelText; What = labelText; Verb = verb; }
		/// <param name="what">What it is, for the on-screen list (eg, "a weapon").</param>
		/// <param name="itemMatch">Which items (eg, e => e.Path.Contains("/Weapons/")).</param>
		public PickUp(string what, Func<Entity, bool> itemMatch, string verb = "Pick up") { What = what; ItemMatch = itemMatch; Verb = verb; }
		public override string Name => $"{Verb} {What}";
		public override void Reset() { base.Reset(); pickedUp = clicked = false; }

		/// <summary>Is this label one it is after?</summary>
		public bool Matches(LabelOnGround l) {
			if ( LabelText != null ) return (Ground.LabelText(l.Label)?.IndexOf(LabelText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
			var ground = l.Item;
			var item = ground?.GetComponent<WorldItem>()?.Item ?? ground;
			return item != null && ItemMatch(item);
		}

		private LabelOnGround Find() => GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(Matches);

		// a label we clicked on that is gone now was picked up
		public override bool CheckDone(CombatContext ctx) => pickedUp || (pickedUp = clicked && Find() == null);

		protected override IState Step(CombatContext ctx) {
			var label = Find();
			var pos = label?.Item?.GetComponent<Positioned>();
			if ( label == null || !IsValid(pos) ) return Search(ctx, What);
			if ( !IsNear(ctx, pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"{d:F0} cells away";
				return Call(new Navigate(What, _ => pos.GridPos, Reach) { FightRadius = IgnoreMonsters || (SearchAt != null && SearchPastMonsters) ? -1 : 0, LegMs = 1000 });
			}
			ctx.Driver.StopWalking(Verb);
			Detail = "clicking it";
			if ( Time.ElapsedMilliseconds - lastClick > 600 && Ground.ClickLabel(label, this) is IState click ) {
				lastClick = Time.ElapsedMilliseconds;
				clicked = true;
				return click;
			}
			return this;
		}
	}

	/// <summary>Put a skill on the skill bar (on slot, or the first free slot if slot is -1).</summary>
	public class BindSkill : Goal {
		private readonly Func<string> skill;
		public string Skill => skill();
		public readonly int Slot;
		public BindSkill(string skill, int slot = -1) : this(() => skill, slot) { }
		/// <param name="skill">The skill, asked when it is needed (eg, the build's starting skill).</param>
		public BindSkill(Func<string> skill, int slot = -1) { this.skill = skill; Slot = slot; }
		public override string Name => Slot < 0 ? $"Put {Skill} on the skill bar" : $"Put {Skill} on slot {GemPlan.SlotLabels.ElementAtOrDefault(Slot) ?? Slot.ToString()}";
		public override bool Sticky => false;
		// reached a later one putting something else on the same slot: don't put this back over it
		public override bool SupersededBy(IEnumerable<Goal> later) {
			if ( Slot < 0 ) return false;
			foreach ( var o in later ) {
				if ( o is BindSkill b && b.Slot == Slot ) return true;
				if ( !o.IsComplete ) return false;
			}
			return false;
		}
		public override bool CheckDone(CombatContext ctx) {
			string name = Skill;
			if ( name == null ) return false;
			return Slot < 0 ? SkillBar.FindSlot(name) != null : SkillBar.Holds(SkillBar.GetSlot(Slot), name);
		}
		protected override IState Step(CombatContext ctx) {
			string name = Skill;
			if ( name == null ) { Detail = "which skill? (see the Build)"; return this; }
			int slot = Slot >= 0 ? Slot : SkillBar.FindFreeSlot()?.Index ?? -1;
			if ( slot < 0 ) { Detail = "no free slot"; return this; }
			ctx.Driver.StopWalking("changing the skill bar");
			Detail = $"binding to slot {slot}";
			ctx.Driver.Bind(slot, name);
			return this;
		}
	}

	/// <summary>
	/// Go to another area, by the route of area exits (see WorldArea.FindRoute): find the exit to the next area on it
	/// (exploring until it is seen), walk there, and click it. Done when the current area is the destination.
	/// </summary>
	public class GoToArea : Goal {
		public readonly string Destination;
		/// <summary>Click the exit when within this many cells of it.</summary>
		public float Reach = 15;
		/// <summary>While walking between unnamed exits looking for the right one: fight monsters within this many cells.</summary>
		public float ExitSearchFightRadius = 75;
		/// <summary>An unnamed exit this near (cells) that still shows no name is not one (eg, a transition tile with no door): skip it.</summary>
		public float UnnamedExitReach = 10;
		private long lastClick;
		// unnamed exits walked to that never showed a name, in this area's exit list
		private readonly HashSet<AreaExit> deadExits = new HashSet<AreaExit>();
		private IReadOnlyList<AreaExit> deadExitsOf;

		/// <param name="destination">An area's name (eg, "Lioneye's Watch"), or a name with its act ("The Sarn Encampment (Act 3)").</param>
		public GoToArea(string destination) => Destination = destination;
		public override string Name => $"Go to {Destination}";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); deadExits.Clear(); }

		// the nearest reachable exit found in the tiles whose entity has not been seen yet (so its name is not known)
		private AreaExit NearestUnnamedExit(CombatContext ctx, Func<AreaExit, bool> where = null) {
			var exits = ctx.Terrain.Exits;
			if ( !ReferenceEquals(exits, deadExitsOf) ) { deadExits.Clear(); deadExitsOf = exits; } // (a new area)
			foreach ( var e in exits.Where(e => !e.Seen && e.Destination == null && !deadExits.Contains(e)).ToList() ) {
				if ( Combat.Distance(ctx.Grid, e.GridPos) <= UnnamedExitReach ) {
					deadExits.Add(e);
					Log($"Campaign: the unnamed exit at {e.GridPos} showed no name from {UnnamedExitReach} cells: skipping it");
				}
			}
			return exits.Where(e => !e.Seen && e.Destination == null && !deadExits.Contains(e) && (where == null || where(e))
					&& DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, e.GridPos))
				.OrderBy(e => Combat.Distance(ctx.Grid, e.GridPos))
				.FirstOrDefault();
		}

		/// <summary>
		/// With no exit known to lead to the next area: walk to the unnamed exits in turn, and explore (true), or stop the
		/// routine, listing the exits in the tiles, so the right tile can be named in AreaExits.KnownExitTiles (false).
		/// </summary>
		public static bool ExploreUnknownExits = LaunchOptions.ExploreExits; // (off, unless launched with --explore-exits)

		internal static bool Is(WorldArea area, string name) => area != null
			&& (string.Equals(area.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(area.DisplayName, name, StringComparison.OrdinalIgnoreCase));

		public override bool CheckDone(CombatContext ctx) => Is(GetCurrentArea(), Destination);

		protected override IState Step(CombatContext ctx) {
			var route = WorldArea.FindRoute(GetCurrentArea(), Destination);
			if ( route == null || route.Count < 2 ) { Detail = "no route"; return this; }
			var next = route[1];
			// (seen, or not yet seen but named by its tile, see AreaExits.KnownExitTiles: walk there, the entity names it exactly)
			var exit = ctx.Terrain.Exits.FirstOrDefault(e => e.Seen && e.Destination == next.Name)
				?? ctx.Terrain.Exits.FirstOrDefault(e => !e.Seen && e.Destination == next.Name);
			if ( exit == null ) {
				// an exit whose tile may lead there, among others (see AreaExits.AmbiguousExitTiles): walk to each in turn
				// until its name shows (a wrong one is then Seen, named, and the next is tried)
				var maybe = NearestUnnamedExit(ctx, e => AreaExits.MayLeadTo(e.TileFile, next.Name));
				if ( maybe != null ) {
					Detail = $"looking for the way to {next.Name}: checking the exit at {maybe.GridPos}";
					return Call(new Navigate($"the exit at {maybe.GridPos}", _ => maybe.GridPos, UnnamedExitReach) {
						FightRadius = IgnoreMonsters ? -1 : ExitSearchFightRadius, LegMs = 1000 });
				}
			}
			if ( exit == null && !ExploreUnknownExits ) {
				// no exit known to lead there (see AreaExits.KnownExitTiles): stop, listing the exits the tiles show, so the
				// one that does can be named, instead of walking to each in turn
				var tiles = ctx.Terrain.Exits.Select(e => $"{e.Destination ?? "?"} at {e.GridPos} ({e.TileFile})");
				string why = $"no known exit from {GetCurrentArea()?.Name} toward {next.Name} (on the way to {Destination})";
				Log($"Campaign: {why}; the exits in the tiles: {string.Join("; ", tiles)}");
				Detail = why;
				return Call(new StopForHelp(why));
			}
			if ( exit == null ) {
				// one of the exits not named yet (found in the tiles, its entity not in range yet): walk to the nearest of
				// them, until its name shows; a wrong one is then Seen, and the next nearest is tried
				var unnamed = NearestUnnamedExit(ctx);
				if ( unnamed != null ) {
					Detail = $"looking for the way to {next.Name}: checking the unnamed exit at {unnamed.GridPos}";
					return Call(new Navigate($"the unnamed exit at {unnamed.GridPos}", _ => unnamed.GridPos, UnnamedExitReach) {
						FightRadius = IgnoreMonsters ? -1 : ExitSearchFightRadius, LegMs = 1000 });
				}
				Detail = $"looking for the way to {next.Name}";
				return FightFirst(ctx) ?? Call(new Explore($"the way to {next.Name}") { FightRadius = IgnoreMonsters ? -1 : 0 });
			}
			if ( !IsNear(ctx, exit.GridPos, Reach, out _, out float d) ) {
				Detail = $"to {next.Name} at {exit.GridPos}, {d:F0} cells away";
				return GoTo(next.Name, exit.GridPos, Reach);
			}
			ctx.Driver.StopWalking("taking the exit");
			Detail = $"entering {next.Name}";
			if ( Time.ElapsedMilliseconds - lastClick < 1000 ) return this;
			var label = GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => Ground.LabelText(l.Label) == next.Name);
			var click = label != null ? Ground.ClickLabel(label, this) : Ground.ClickGrid(exit.GridPos, ctx.Z, this);
			if ( click == null ) return this;
			lastClick = Time.ElapsedMilliseconds;
			return click;
		}
	}

	/// <summary>
	/// Walk to the area's waypoint (Terrain.Waypoint: known from the tiles when the area loads, exact once its entity is
	/// in range), close enough for it to be discovered. Done once within Reach of it (sticky).
	/// </summary>
	public class GoToWaypoint : Goal {
		/// <summary>Done when within this many cells of the waypoint.</summary>
		public float Reach = 12;
		public override string Name => "Walk to the waypoint";

		public override bool CheckDone(CombatContext ctx) =>
			ctx.Terrain.Waypoint is AreaFeature wp && wp.Seen && Combat.Distance(ctx.Grid, wp.GridPos) <= Reach;

		protected override IState Step(CombatContext ctx) {
			var wp = ctx.Terrain.Waypoint;
			if ( wp == null ) {
				Detail = "looking for the waypoint";
				return FightFirst(ctx) ?? Call(new Explore("the waypoint") { FightRadius = IgnoreMonsters ? -1 : 0 });
			}
			Detail = $"at {wp}";
			return Call(new Navigate("the waypoint", c => c.Terrain.Waypoint?.GridPos, Reach - 2) { FightRadius = IgnoreMonsters ? -1 : 0 });
		}
	}

	/// <summary>Click a button in the game's UI until it goes away (eg, "Skip All Tutorials").</summary>
	public class ClickButton : Goal {
		private readonly string label;
		private readonly Func<Element> find;
		private long lastClick;
		/// <param name="find">Gets the button (eg, () => GetUI()?.SkipAllTutorials); done when it is gone or hidden.</param>
		public ClickButton(string label, Func<Element> find) { this.label = label; this.find = find; }
		public override string Name => $"Click {label}";
		public override bool Sticky => false;
		public override bool CheckDone(CombatContext ctx) {
			var button = find();
			return !IsValid(button) || !button.IsVisible;
		}
		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("clicking a button");
			var button = find();
			if ( !IsValid(button) || Time.ElapsedMilliseconds - lastClick < 1000 ) return this;
			Detail = "clicking it";
			lastClick = Time.ElapsedMilliseconds;
			return Inputs.Click(button.GetClientRect(), this);
		}
	}

	/// <summary>
	/// Put a gem from the backpack into a socket of the main hand weapon (one linked to another gem's, for a support):
	/// open the inventory, click the gem (it goes on the cursor), click the socket. Done when the weapon's Sockets have
	/// the gem (linked as asked). The campaign closes the inventory after.
	///
	/// Where a socket is drawn: the weapon's item element has the sockets as the children of its first child, in socket
	/// order, each at the top left of its socket.
	/// </summary>
	public class SocketGem : Goal {
		private readonly Func<string> gem, linkedTo;
		/// <summary>The gem's name (eg, "Fireball", "Arcane Surge Support").</summary>
		public string Gem => gem();
		/// <summary>For a support: the gem (in the weapon) to link it with, eg "Fireball"; null for any socket.</summary>
		public string LinkedTo => linkedTo?.Invoke();
		/// <summary>Only if it can be done: also done when the gem is not in the backpack, or there is no empty socket for it.</summary>
		public bool OnlyIfPossible;
		private long lastAction;
		private const long Pace = 700;
		private const long SocketPace = 1500; // after a click on a socket, time for it to show in the Sockets before the next click

		public SocketGem(string gem, string linkedTo = null) : this(() => gem, linkedTo == null ? null : (Func<string>)(() => linkedTo)) { }
		/// <param name="gem">The gem, asked when needed (eg, the build's starting skill).</param>
		/// <param name="linkedTo">For a support: the gem to link it with, asked when needed.</param>
		public SocketGem(Func<string> gem, Func<string> linkedTo = null) { this.gem = gem; this.linkedTo = linkedTo; }

		public override string Name => LinkedTo == null ? $"Socket {Gem} in the main hand" : $"Socket {Gem} linked to {LinkedTo}";
		public override void Reset() { base.Reset(); picked = false; }
		public override bool Sticky => false;
		public override bool UsesInventory => true;

		private InventoryItem GemInBackpack(string name) => BackpackItems().FirstOrDefault(i => Equipment.IsGem(i.Entity, name));
		private static InventoryItem Weapon() => GetUI()?.InventoryPanel?.LWeapon?.Items.FirstOrDefault(i => IsValid(i.Entity?.GetComponent<Sockets>()));

		public override bool CheckDone(CombatContext ctx) {
			string name = Gem, link = LinkedTo;
			if ( name == null ) return false;
			var sockets = Weapon()?.Entity?.GetComponent<Sockets>();
			if ( !IsValid(sockets) ) return false;
			int i = sockets.IndexOfGem(e => Equipment.IsGem(e, name));
			if ( i >= 0 && (link == null || sockets.LinkedWith(i).Contains(sockets.IndexOfGem(e => Equipment.IsGem(e, link)))) ) return true;
			if ( !OnlyIfPossible || picked ) return false; // (picked: it is on the cursor, on its way to a socket)
			var item = GetUI()?.InventoryPanel?.Backpack?.Items.FirstOrDefault(x => Equipment.IsGem(x.Entity, name));
			if ( item == null ) return true;
			string detail = Detail;
			bool none = TargetSocket(sockets, item.Entity.GetComponent<SkillGem>()?.Color ?? SocketColor.None, link) < 0;
			Detail = detail;
			return none;
		}

		// the empty socket to put it in (linked to link's), one of the gem's color if there is one (for +10% quality), or -1
		private int TargetSocket(Sockets sockets, SocketColor gemColor, string link) {
			IEnumerable<int> candidates = Enumerable.Range(0, sockets.Count);
			if ( link != null ) {
				int other = sockets.IndexOfGem(e => Equipment.IsGem(e, link));
				if ( other < 0 ) { Detail = $"{link} is not in the weapon"; return -1; }
				candidates = sockets.LinkedWith(other);
			}
			var empty = candidates.Where(i => sockets.Gem(i) == null).ToList();
			if ( empty.Count == 0 ) {
				Detail = link == null ? "no empty socket" : $"no empty socket linked to {link}";
				return -1;
			}
			return empty.Where(i => sockets.Color(i) == gemColor).DefaultIfEmpty(empty[0]).First();
		}
		private SocketColor gemColor; // as read while the gem was in the backpack (it has no entity of ours on the cursor)
		private bool picked; // the gem was found in the backpack, and clicked

		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("using the inventory");
			string name = Gem;
			if ( name == null ) { Detail = "which gem? (see the Build)"; return this; }
			if ( Time.ElapsedMilliseconds - lastAction < Pace ) return this;
			lastAction = Time.ElapsedMilliseconds;
			if ( !BackpackIsOpen() ) {
				Detail = "opening the inventory";
				return Inputs.Press(VKey.I, this);
			}
			var slot = GetUI().InventoryPanel.LWeapon.GetClientRect();
			var weapon = Weapon();
			if ( weapon == null ) {
				// eg, a click that missed the socket picked the weapon up: put it back (with nothing on the cursor, this does nothing)
				Detail = "putting the weapon back";
				return Inputs.Click(slot, this);
			}
			var gem = GemInBackpack(name);
			if ( gem != null ) gemColor = gem.Entity.GetComponent<SkillGem>()?.Color ?? SocketColor.None;
			else if ( !picked ) { Detail = $"no {name} in the backpack (gems there: {Equipment.BackpackGems()})"; return this; }
			int target = TargetSocket(weapon.Entity.GetComponent<Sockets>(), gemColor, LinkedTo);
			if ( target < 0 ) return this;
			if ( gem != null ) {
				Detail = "picking up the gem";
				picked = true;
				return Inputs.Click(gem.GetClientRect(), this);
			}
			// not in the backpack and not socketed: it is on the cursor
			var socket = weapon.GetChild(0)?.GetChild(target);
			if ( !IsValid(socket) ) { Detail = $"can't find socket {target} on screen"; return this; }
			Detail = $"putting the gem in socket {target}";
			lastAction += SocketPace - Pace;
			return Inputs.Click(SocketRect(socket.GetClientRect(), slot), this);
		}

		/// <summary>The box of a socket, from its element's top left (the element has no size of its own).</summary>
		public static System.Drawing.RectangleF SocketRect(System.Drawing.RectangleF socket, System.Drawing.RectangleF weaponSlot) {
			float size = .75f * weaponSlot.Width / 2; // a weapon slot is 2 cells wide; a socket is drawn about 3/4 of a cell
			return new System.Drawing.RectangleF(socket.X, socket.Y, size, size);
		}

	}

	/// <summary>What the player has on, and carries.</summary>
	public static class Equipment {
		// OffHandWeaponType reads 14 with nothing in the off hand (3.29.3); assumed to mean the same for the main hand
		private const int NoWeapon = 14;

		/// <summary>Is there a weapon in the main hand (from the player's MainHandWeaponType stat, so the inventory can be closed).</summary>
		public static bool HasWeapon() {
			var stats = GetPlayer()?.GetStats();
			return stats != null && stats.TryGetValue(Offsets.GameStat.MainHandWeaponType, out int type) && type != NoWeapon;
		}

		/// <summary>Is the item a weapon (by its metadata path).</summary>
		public static bool IsWeapon(Entity item) => item?.Path?.Contains("/Items/Weapons/") ?? false;
		/// <summary>Is the item a skill gem (not a support).</summary>
		public static bool IsSkillGem(Entity item) => (item?.Path?.Contains("/Gems/") ?? false) && !item.Path.Contains("/SupportGem");
		/// <summary>Is the item a support gem.</summary>
		public static bool IsSupportGem(Entity item) => item?.Path?.Contains("/Gems/SupportGem") ?? false;

		/// <summary>
		/// Is e the gem called name (eg, "Chance to Poison Support"): by its item name (a support's is without " Support"),
		/// or, if that cannot be read, by its metadata path (which is not always the name).
		/// </summary>
		public static bool IsGem(Entity e, string name) {
			if ( name == null || !(e?.Path is string path) || !path.Contains("/Gems/") ) return false;
			var itemName = ItemName(e);
			return itemName != null ? string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase)
					|| string.Equals($"{itemName} Support", name, StringComparison.OrdinalIgnoreCase)
				: path.Contains(name.Replace(" Support", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>An item's base name (eg, "Driftwood Wand", "Fireball"), or null if it cannot be read.</summary>
		public static string ItemName(Entity e) {
			var info = e?.GetComponent<Base>()?.Info;
			var name = info?.Value.Name.Value;
			return string.IsNullOrWhiteSpace(name) ? null : name;
		}

		/// <summary>A gem's name as the campaign uses it: its item name, with " Support" for a support.</summary>
		public static string GemName(Entity e) {
			var name = ItemName(e);
			if ( name == null ) return null;
			return IsSupportGem(e) && !name.EndsWith(" Support", StringComparison.OrdinalIgnoreCase) ? $"{name} Support" : name;
		}

		/// <summary>Is the gem called name (see IsGem) in the backpack (open or not).</summary>
		public static bool GemInBackpack(string name) =>
			GetUI()?.InventoryPanel?.Backpack?.Items.Any(i => IsGem(i.Entity, name)) ?? false;

		/// <summary>Is the gem called name (see IsGem) in the backpack, or socketed in a worn item.</summary>
		public static bool HasGem(string name) => GemInBackpack(name) || SocketedGems().Any(e => IsGem(e, name));

		/// <summary>The gems had: in the backpack (open or not) and socketed in worn items.</summary>
		public static IEnumerable<Entity> OwnedGems() => (GetUI()?.InventoryPanel?.Backpack?.Items ?? Enumerable.Empty<InventoryItem>())
			.Select(i => i.Entity).Where(e => e?.Path?.Contains("/Gems/") ?? false).Concat(SocketedGems());

		/// <summary>The worn items' inventories (not the weapon swap, flasks or backpack).</summary>
		public static IEnumerable<Inventory> WornInventories() => WornSlots().Select(s => s.inventory);

		/// <summary>The worn items' inventories, with their names (eg, "Helm", "LWeapon").</summary>
		public static IEnumerable<(string name, Inventory inventory)> WornSlots() {
			var p = GetUI()?.InventoryPanel;
			if ( p == null ) return Enumerable.Empty<(string, Inventory)>();
			return new[] { ("Helm", p.Helm), ("Amulet", p.Amulet), ("Chest", p.Chest), ("LWeapon", p.LWeapon), ("RWeapon", p.RWeapon),
				("LRing", p.LRing), ("RRing", p.RRing), ("Gloves", p.Gloves), ("Belt", p.Belt), ("Boots", p.Boots) }.Where(s => s.Item2 != null);
		}

		/// <summary>The worn items.</summary>
		public static IEnumerable<InventoryItem> WornItems() => WornInventories().SelectMany(i => i.Items).Where(i => i?.Entity != null);

		/// <summary>The gems socketed in worn items.</summary>
		public static IEnumerable<Entity> SocketedGems() => WornItems()
			.Select(i => i.Entity.GetComponent<Sockets>()).Where(s => IsValid(s))
			.SelectMany(s => Enumerable.Range(0, s.Count).Select(s.Gem)).Where(e => e != null);

		/// <summary>The gems in the backpack.</summary>
		public static IEnumerable<Entity> BackpackGemEntities() => (GetUI()?.InventoryPanel?.Backpack?.Items ?? Enumerable.Empty<InventoryItem>())
			.Select(i => i.Entity).Where(e => e?.Path?.Contains("/Gems/") ?? false);

		/// <summary>The gems in the backpack, as "name (path)", for status lines.</summary>
		public static string BackpackGems() => string.Join(", ", (GetUI()?.InventoryPanel?.Backpack?.Items ?? Enumerable.Empty<InventoryItem>())
			.Where(i => i.Entity?.Path?.Contains("/Gems/") ?? false).Select(i => $"{ItemName(i.Entity) ?? "?"} ({i.Entity.Path})"));

		/// <summary>
		/// Is a quest item called name (eg, "Sewer Keys", "Thaumetic Sulphite": its name has it, or its path has it without
		/// spaces) in the backpack (open or not)? Quest items are kept there (3.30, seen live: Metadata/Items/QuestItems/
		/// RibbonSpool, "Ribbon Spool"; SulphiteFlask, "Thaumetic Sulphite").
		/// </summary>
		public static bool HasQuestItem(string name) => BackpackItems().Any(i => i.Entity?.Path is string path && path.Contains("/QuestItems/")
			&& ((ItemName(i.Entity)?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)
				|| path.Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)));

		public static bool InBackpack(params string[] pathParts) =>
			GetUI()?.InventoryPanel?.Backpack?.Items.Any(i => i.Entity?.Path is string path && pathParts.All(path.Contains)) ?? false;
	}

	/// <summary>Helpers for things on the ground: label text, and clicking labels or places.</summary>
	public static class Ground {
		/// <summary>The text of a label: its own, or the first text among its children (item labels keep it a level or two down).</summary>
		public static string LabelText(Element label, int depth = 3) {
			if ( !IsValid(label) ) return null;
			var text = label.Text;
			if ( !string.IsNullOrWhiteSpace(text) ) return text;
			if ( depth <= 0 ) return null;
			foreach ( var child in label.Children ) {
				text = LabelText(child, depth - 1);
				if ( !string.IsNullOrWhiteSpace(text) ) return text;
			}
			return null;
		}

		/// <summary>A click on a label (as Inputs, then next), or null if it can't be clicked safely (off screen, under a panel).</summary>
		public static IState ClickLabel(LabelOnGround label, IState next = null) {
			var rect = label?.Label?.GetClientRect() ?? default;
			var at = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
			if ( rect.Width <= 0 || !PathFollower.IsSafeToClick(at) ) return null;
			return Inputs.Click(rect, next);
		}

		/// <summary>A click on a grid cell (as Inputs, then next), or null if it can't be clicked safely.</summary>
		public static IState ClickGrid(Offsets.Vector2i grid, float z, IState next = null) {
			if ( !TryGridToScreen(grid, z, out Vector2 at) || !PathFollower.IsSafeToClick(at) ) return null;
			return Inputs.Click(at, next);
		}

		/// <summary>Where ClickEntity would click e (client coordinates): the middle of its label if it shows one, else where it stands on screen; false if neither.</summary>
		public static bool TryGetClickPoint(Entity e, out Vector2 at) {
			at = default;
			if ( e == null ) return false;
			var rect = GetUI()?.LabelsOnGround?.GetAllLabels()?.FirstOrDefault(l => l.Item?.Address == e.Address)?.Label?.GetClientRect() ?? default;
			if ( rect.Width > 0 ) { at = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)); return true; }
			return TryWorldToScreen(Position(e), out at);
		}

		/// <summary>A click on an entity: its label if it shows one, else where it stands; null if neither can be clicked.</summary>
		public static IState ClickEntity(Entity e, IState next = null) {
			var label = GetUI()?.LabelsOnGround?.GetAllLabels()?.FirstOrDefault(l => l.Item?.Address == e.Address);
			if ( label != null && ClickLabel(label, next) is IState click ) return click;
			if ( !TryWorldToScreen(Position(e), out Vector2 at) || !PathFollower.IsSafeToClick(at) ) return null;
			return Inputs.Click(at, next);
		}
	}
}
