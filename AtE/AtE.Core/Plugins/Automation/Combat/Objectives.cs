using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// One step of a CombatRoutine in an area (eg, "Kill Hillock"). The routine works on the first one that is not
	/// complete: each frame it asks CheckDone, and if not done, calls Tick to work on it.
	///
	/// Done-ness is read from the game where possible (the skill is on the bar, the area is the one we wanted), so a
	/// routine picks up where it left off. Some things can't be read back (a monster that was killed and is gone): those
	/// objectives are Sticky, once CheckDone says true they stay complete until the routine leaves the area instance.
	/// The others are checked again every time, so they are redone if undone (eg, the skill was taken off the bar).
	///
	/// An objective that is done shows that the ones before it were done too (Fireball on the bar: it was picked up
	/// and socketed), so CombatRoutine marks them complete without asking them.
	/// </summary>
	public abstract class Objective {
		/// <summary>What it is, for the on-screen list (eg, "Kill Hillock").</summary>
		public abstract string Name { get; }
		/// <summary>What it is doing right now (eg, "looking for Hillock"); shown next to the Name.</summary>
		public string Detail = "";
		public bool IsComplete { get; internal set; }
		/// <summary>True if, once done, it stays done (its done-ness can't be read back later); false to check it every time.</summary>
		public virtual bool Sticky => true;
		/// <summary>True if this being done shows the objectives before it are done (see CombatRoutine).</summary>
		public virtual bool ProvesEarlier => true;
		/// <summary>
		/// True if a later objective being done shows this one was done (see ProvesEarlier); false if this must be checked
		/// itself, eg, a skill bound to a slot: socketing a support later says nothing about where the skill is on the bar.
		/// </summary>
		public virtual bool ProvenBy(IEnumerable<Objective> later) => true;
		/// <summary>
		/// True if it no longer matters, the routine having got past it to a later objective (all those after it, given in
		/// order, are complete up to one that undoes it): eg, Default Attack on a slot, once Fireball goes on that slot.
		/// </summary>
		public virtual bool SupersededBy(IEnumerable<Objective> later) => false;
		/// <summary>True if it works in the inventory panel; while working on any other objective the routine closes it.</summary>
		public virtual bool UsesInventory => false;
		/// <summary>Also done when this is true (eg, any weapon is equipped, for picking up the starting weapon).</summary>
		public Func<bool> DoneWhen;

		/// <summary>
		/// Where to look for what it is after (eg, Search.TheOnlyExit, for a boss in the way out): walk there, past the
		/// monsters, without fighting. Null (or a null answer): explore the area, fighting on the way.
		/// </summary>
		public Func<CombatContext, Offsets.Vector2i?> SearchAt;

		/// <summary>CheckDone, or DoneWhen.</summary>
		public bool IsDone(CombatContext ctx) => CheckDone(ctx) || (DoneWhen?.Invoke() ?? false);

		/// <summary>Is it done? Called each frame until it says true.</summary>
		public abstract bool CheckDone(CombatContext ctx);
		/// <summary>Work on it for one frame (walk, attack, click...), using ctx.Driver.</summary>
		public abstract void Tick(CombatContext ctx);
		/// <summary>Forget any progress (when entering a new area instance).</summary>
		public virtual void Reset() {
			IsComplete = false;
			Detail = "";
		}
		public override string ToString() => Name;

		/// <summary>
		/// Where to walk to reach pos: the nearest cell to it that can be walked to from the player (pos itself may be
		/// blocked, eg a door or transition set into a wall, and the nearest walkable cell may be on the far side).
		/// </summary>
		protected static Offsets.Vector2i Approach(CombatContext ctx, Offsets.Vector2i pos) =>
			DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, pos, out var spot) ? spot : pos;

		/// <summary>Look for what, not in sight yet: walk toward SearchAt, or explore (see SearchAt).</summary>
		protected void Search(CombatContext ctx, string what, string skill = null) {
			var at = SearchAt?.Invoke(ctx);
			if ( at.HasValue ) {
				Detail = $"looking for {what} on the way to {at.Value}";
				ctx.Driver.WalkTo(Approach(ctx, at.Value), $"looking for {what}");
				return;
			}
			if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, skill) ) { Detail = "fighting on the way"; return; }
			Detail = $"looking for {what}";
			if ( !ctx.Driver.TryExplore(ctx) ) Detail = $"explored everything without finding {what}";
		}
	}

	/// <summary>Places to look, for Objective.SearchAt.</summary>
	public static class Search {
		/// <summary>The way out, when the area has only one (eg, The Twilight Strand, with Hillock in front of it); else null.</summary>
		public static Offsets.Vector2i? TheOnlyExit(CombatContext ctx) {
			var exits = ctx.Terrain.Exits;
			return exits.Count == 1 ? exits[0].GridPos : (Offsets.Vector2i?)null;
		}
	}

	/// <summary>Fight everything near and explore the whole area.</summary>
	public class ClearArea : Objective {
		public string Skill;
		/// <param name="skill">The skill to fight with, eg "Fireball"; null uses the driver's Attack.</param>
		public ClearArea(string skill = null) => Skill = skill;
		public override string Name => "Clear the area";
		public override bool ProvesEarlier => false; // a cleared area says nothing about what was picked up or bound
		public override bool CheckDone(CombatContext ctx) => ctx.Driver.ExploreDone && ctx.Hostiles.Count == 0;
		public override void Tick(CombatContext ctx) {
			if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, Skill) ) {
				Detail = "fighting";
			} else if ( ctx.Driver.TryExplore(ctx) ) {
				Detail = $"exploring ({DefaultExplorer.ExploredFraction:P0})";
			} else {
				Detail = "";
			}
		}
	}

	/// <summary>
	/// Kill a particular monster, found by a part of its metadata path (eg, "Metadata/Monsters/...Hillock"):
	/// explore (fighting whatever is in the way) until it is seen, then fight it.
	/// </summary>
	public class Kill : Objective {
		public readonly string Label;
		public readonly Func<Entity, bool> Match;
		public string Skill;
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
			: this(label, e => e.Path?.Contains(pathContains) ?? false, skill, range, needsLineOfSight) { }

		/// <param name="match">Which entities are the monster (eg, by path and whether it is hostile).</param>
		public Kill(string label, Func<Entity, bool> match, string skill = null, float range = 0, bool? needsLineOfSight = null) {
			Label = label; Match = match; Skill = skill; Range = range; NeedsLineOfSight = needsLineOfSight;
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

		public override void Tick(CombatContext ctx) {
			var ent = Matches().FirstOrDefault(e => IsAlive(e));
			if ( ent != null ) {
				var target = Combat.ToHostile(ent, ctx.Grid);
				if ( target != null ) {
					seen = true;
					lastSeen = target.GridPos;
					if ( !IsTargetable(ent) ) {
						// eg, a boss that has not woken up yet: get close and wait
						Detail = "waiting for it to be targetable";
						if ( target.Distance > 20 ) ctx.Driver.WalkTo(Approach(ctx, target.GridPos), $"walking to {Label}");
						else ctx.Driver.StopWalking("waiting");
						return;
					}
					Fight(ctx, target);
					return;
				}
			}
			// not in sight: go back to where it was last seen, else keep looking
			if ( seen ) {
				if ( SearchAt == null && (ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, Skill)) ) { Detail = "fighting on the way"; return; }
				Detail = $"going back to where it was last seen";
				ctx.Driver.WalkTo(Approach(ctx, lastSeen), $"looking for {Label}");
				return;
			}
			Search(ctx, Label, Skill);
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
	/// the skill again, and so on round. If the walk
	/// takes longer than MaxWalkMs, it uses the skill on the way and then goes on to the same point. A point that can't be
	/// walked to, or can't see the boss, is skipped.
	/// </summary>
	public class Orbit : Kill {
		public float Radius;
		/// <summary>How far from it to circle, in cells, with a melee skill (when Radius is 0).</summary>
		public float MeleeRadius = 8;
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
		public Orbit(string label, string pathContains, string skill, float radius = 0)
			: base(label, pathContains, skill) => Radius = radius;

		public override string Name => $"Kill {Label} with {Skill}, circling it";
		public override void Reset() { base.Reset(); point = -1; castDue = true; castUntil = aimSince = 0; }

		protected override void Fight(CombatContext ctx, Hostile target) {
			float radius = Radius > 0 ? Radius
				: SkillBar.IsMelee(SkillBar.FindSlot(Skill)?.Skill) ? MeleeRadius
				: ctx.Driver.AttackRange * .6f;
			if ( Time.ElapsedMilliseconds < castUntil ) {
				ctx.Driver.StopWalking("casting");
				Detail = $"casting {Skill}";
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
					if ( !ctx.Driver.Engage(ctx, target, Skill, radius * 1.4f, true) ) base.Fight(ctx, target);
					return;
				}
				ctx.Driver.StopWalking("using a skill");
				// the cast goes where the cursor is when it starts: stop, and keep the cursor on it for a moment first
				SkillBar.MouseTo(screen);
				if ( aimSince == 0 ) aimSince = Time.ElapsedMilliseconds;
				if ( Time.ElapsedMilliseconds - aimSince < AimMs ) { Detail = "aiming at it"; return; }
				Detail = $"using {Skill} at it";
				if ( !ctx.Driver.UseSkillPaced(Skill, screen) ) return;
				aimSince = 0;
				castDue = false;
				// a click to walk too soon would cancel the cast: stand still until it is out (CastFraction of the cast time,
				// plus a little for the key press, which goes out on the next frame)
				int castMs = Math.Max(300, SkillBar.CastTimeMs(SkillBar.FindSlot(Skill)?.Skill));
				castUntil = Time.ElapsedMilliseconds + (long)(castMs * CastFraction) + 50;
				Log($"Orbit: {Skill} at {Label}, {target.Distance:F0} cells away, holding still {castUntil - Time.ElapsedMilliseconds} ms");
				walkStart = castUntil;
				if ( point < 0 ) point = NextPoint(ctx.Grid, target.GridPos, dirs);
				return;
			}
			// walk to the point (or the next one that can be walked to and sees it)
			for ( int tries = 0; tries < Cardinal.Length; tries++ ) {
				if ( TrySpot(ctx, target.GridPos, dirs[point], radius, out var spot) ) {
					if ( Combat.Distance(ctx.Grid, spot) <= 3 ) {
						// there: use the skill again, then on to the next one
						castDue = true;
						point = (point + 1) % Cardinal.Length;
					} else if ( Time.ElapsedMilliseconds - walkStart > MaxWalkMs ) {
						castDue = true; // a long walk: use the skill on the way, then keep going to this point
					} else {
						Detail = $"circling it, to the {CardinalNames[point]}";
						ctx.Driver.WalkTo(spot, $"circling {Label}");
					}
					return;
				}
				point = (point + 1) % Cardinal.Length;
			}
			// nowhere around it to stand: fight it from wherever
			base.Fight(ctx, target);
		}

		// where to stand on the point in direction dir from center: a cell that can be walked to, near it, that sees center
		private static bool TrySpot(CombatContext ctx, Offsets.Vector2i center, Vector2 dir, float radius, out Offsets.Vector2i spot) {
			var want = new Offsets.Vector2i((int)MathF.Round(center.X + (dir.X * radius)), (int)MathF.Round(center.Y + (dir.Y * radius)));
			return DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, want, out spot)
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
	/// Pick up an item from the ground, found by the text of its label (eg, "Driftwood Wand"); or click any other label
	/// on the ground (eg, an NPC's). Done once a label it clicked is gone.
	/// </summary>
	public class PickUp : Objective {
		public readonly string LabelText;
		public readonly string Verb;
		/// <summary>Click the label when within this many cells of the item.</summary>
		public float Reach = 12;
		private bool pickedUp;
		private long lastClick;
		private bool clicked;

		/// <param name="verb">For the on-screen list: "Pick up", or eg "Click" for a label that is not an item.</param>
		public PickUp(string labelText, string verb = "Pick up") { LabelText = labelText; Verb = verb; }
		public override string Name => $"{Verb} {LabelText}";
		public override void Reset() { base.Reset(); pickedUp = clicked = false; }

		private LabelOnGround Find() => GetUI()?.LabelsOnGround?.GetAllLabels()
			.FirstOrDefault(l => (Ground.LabelText(l.Label)?.IndexOf(LabelText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

		// a label we clicked on that is gone now was picked up
		public override bool CheckDone(CombatContext ctx) => pickedUp || (pickedUp = clicked && Find() == null);

		public override void Tick(CombatContext ctx) {
			var label = Find();
			var item = label?.Item;
			var pos = item?.GetComponent<Positioned>();
			if ( label == null || !IsValid(pos) ) {
				Search(ctx, LabelText);
				return;
			}
			float d = Combat.Distance(ctx.Grid, pos.GridPos);
			if ( d > Reach ) {
				Detail = $"{d:F0} cells away";
				ctx.Driver.WalkTo(Approach(ctx, pos.GridPos), $"walking to {LabelText}");
				return;
			}
			ctx.Driver.StopWalking(Verb);
			Detail = "clicking it";
			if ( Time.ElapsedMilliseconds - lastClick > 600 && Ground.TryClickLabel(label) ) {
				lastClick = Time.ElapsedMilliseconds;
				clicked = true;
			}
		}
	}

	/// <summary>Put a skill on the skill bar (on slot, or the first free slot if slot is -1).</summary>
	public class BindSkill : Objective {
		public readonly string Skill;
		public readonly int Slot;
		public BindSkill(string skill, int slot = -1) { Skill = skill; Slot = slot; }
		public override string Name => Slot < 0 ? $"Put {Skill} on the skill bar" : $"Put {Skill} on slot {Slot}";
		public override bool Sticky => false;
		// only by a later one putting something else on the same slot (eg, Fireball in place of Default Attack)
		public override bool ProvenBy(IEnumerable<Objective> later) => Slot >= 0 && later.OfType<BindSkill>().Any(b => b.Slot == Slot);
		// reached a later one putting something else on the same slot: don't put this back over it
		public override bool SupersededBy(IEnumerable<Objective> later) {
			if ( Slot < 0 ) return false;
			foreach ( var o in later ) {
				if ( o is BindSkill b && b.Slot == Slot ) return true;
				if ( !o.IsComplete ) return false;
			}
			return false;
		}
		public override bool CheckDone(CombatContext ctx) => Slot < 0
			? SkillBar.FindSlot(Skill) != null
			: SkillBar.Holds(SkillBar.GetSlot(Slot), Skill);
		public override void Tick(CombatContext ctx) {
			int slot = Slot >= 0 ? Slot : SkillBar.FindFreeSlot()?.Index ?? -1;
			if ( slot < 0 ) { Detail = "no free slot"; return; }
			ctx.Driver.StopWalking("changing the skill bar");
			Detail = $"binding to slot {slot}";
			ctx.Driver.Bind(slot, Skill);
		}
	}

	/// <summary>
	/// Go to another area, by the route of area exits (see WorldArea.FindRoute): find the exit to the next area on it
	/// (exploring until it is seen), walk there, and click it. Done when the current area is the destination.
	/// </summary>
	public class GoToArea : Objective {
		public readonly string Destination;
		/// <summary>Click the exit when within this many cells of it.</summary>
		public float Reach = 15;
		private long lastClick;

		/// <param name="destination">An area's name (eg, "Lioneye's Watch"), or a name with its act ("The Sarn Encampment (Act 3)").</param>
		public GoToArea(string destination) => Destination = destination;
		public override string Name => $"Go to {Destination}";
		public override bool Sticky => false;

		private static bool Is(WorldArea area, string name) => area != null
			&& (string.Equals(area.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(area.DisplayName, name, StringComparison.OrdinalIgnoreCase));

		public override bool CheckDone(CombatContext ctx) => Is(GetCurrentArea(), Destination);

		public override void Tick(CombatContext ctx) {
			var route = WorldArea.FindRoute(GetCurrentArea(), Destination);
			if ( route == null || route.Count < 2 ) { Detail = "no route"; return; }
			var next = route[1];
			var exit = ctx.Terrain.Exits.FirstOrDefault(e => e.Seen && e.Destination == next.Name);
			if ( exit == null ) {
				if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx) ) { Detail = "fighting on the way"; return; }
				Detail = $"looking for the way to {next.Name}";
				if ( !ctx.Driver.TryExplore(ctx) ) Detail = $"explored everything without finding the way to {next.Name}";
				return;
			}
			// walk to the nearest reachable cell to the exit (the exit itself is often in a wall)
			var approach = Approach(ctx, exit.GridPos);
			float d = Combat.Distance(ctx.Grid, exit.GridPos);
			if ( d > Reach && Combat.Distance(ctx.Grid, approach) > 3 ) {
				Detail = $"to {next.Name} at {exit.GridPos}, {d:F0} cells away (walking to {approach})";
				ctx.Driver.WalkTo(approach, $"walking to {next.Name}");
				return;
			}
			ctx.Driver.StopWalking("taking the exit");
			Detail = $"entering {next.Name}";
			if ( Time.ElapsedMilliseconds - lastClick < 1000 ) return;
			var label = GetUI()?.LabelsOnGround?.GetAllLabels().FirstOrDefault(l => Ground.LabelText(l.Label) == next.Name);
			if ( label != null ? Ground.TryClickLabel(label) : Ground.TryClickGrid(exit.GridPos, ctx.Z) ) {
				lastClick = Time.ElapsedMilliseconds;
			}
		}
	}

	/// <summary>Click a button in the game's UI until it goes away (eg, "Skip All Tutorials").</summary>
	public class ClickButton : Objective {
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
		public override void Tick(CombatContext ctx) {
			ctx.Driver.StopWalking("clicking a button");
			var button = find();
			if ( !IsValid(button) || Time.ElapsedMilliseconds - lastClick < 1000 ) return;
			Detail = "clicking it";
			SkillBar.ClickAt(button.GetClientRect());
			lastClick = Time.ElapsedMilliseconds;
		}
	}

	/// <summary>
	/// Put a gem from the backpack into a socket of the main hand weapon (one linked to another gem's, for a support):
	/// open the inventory, click the gem (it goes on the cursor), click the socket. Done when the weapon's Sockets have
	/// the gem (linked as asked). The routine closes the inventory after.
	///
	/// Where a socket is drawn: the weapon's item element has the sockets as the children of its first child, in socket
	/// order, each at the top left of its socket.
	/// </summary>
	public class SocketGem : Objective {
		public readonly string Gem;
		public readonly string LinkedTo;
		private long lastAction;
		private const long Pace = 700;
		private const long SocketPace = 1500; // after a click on a socket, time for it to show in the Sockets before the next click

		/// <param name="gem">The gem's name (eg, "Fireball", "Arcane Surge Support").</param>
		/// <param name="linkedTo">For a support: the gem (in the weapon) to link it with (eg, "Fireball").</param>
		public SocketGem(string gem, string linkedTo = null) {
			Gem = gem;
			LinkedTo = linkedTo;
		}

		public override string Name => LinkedTo == null ? $"Socket {Gem} in the main hand" : $"Socket {Gem} linked to {LinkedTo}";
		public override void Reset() { base.Reset(); picked = false; }
		public override bool Sticky => false;
		public override bool UsesInventory => true;

		private InventoryItem GemInBackpack() => BackpackItems().FirstOrDefault(i => Equipment.IsGem(i.Entity, Gem));
		private static InventoryItem Weapon() => GetUI()?.InventoryPanel?.LWeapon?.Items.FirstOrDefault(i => IsValid(i.Entity?.GetComponent<Sockets>()));

		public override bool CheckDone(CombatContext ctx) {
			var sockets = Weapon()?.Entity?.GetComponent<Sockets>();
			if ( !IsValid(sockets) ) return false;
			int i = sockets.IndexOfGem(e => Equipment.IsGem(e, Gem));
			return i >= 0 && (LinkedTo == null || sockets.LinkedWith(i).Contains(sockets.IndexOfGem(e => Equipment.IsGem(e, LinkedTo))));
		}

		// the empty socket to put it in (linked to LinkedTo's), one of the gem's color if there is one (for +10% quality), or -1
		private int TargetSocket(Sockets sockets, SocketColor gemColor) {
			IEnumerable<int> candidates = Enumerable.Range(0, sockets.Count);
			if ( LinkedTo != null ) {
				int other = sockets.IndexOfGem(e => Equipment.IsGem(e, LinkedTo));
				if ( other < 0 ) { Detail = $"{LinkedTo} is not in the weapon"; return -1; }
				candidates = sockets.LinkedWith(other);
			}
			var empty = candidates.Where(i => sockets.Gem(i) == null).ToList();
			if ( empty.Count == 0 ) {
				Detail = LinkedTo == null ? "no empty socket" : $"no empty socket linked to {LinkedTo}";
				return -1;
			}
			return empty.Where(i => sockets.Color(i) == gemColor).DefaultIfEmpty(empty[0]).First();
		}
		private SocketColor gemColor; // as read while the gem was in the backpack (it has no entity of ours on the cursor)
		private bool picked; // the gem was found in the backpack, and clicked

		public override void Tick(CombatContext ctx) {
			ctx.Driver.StopWalking("using the inventory");
			if ( Time.ElapsedMilliseconds - lastAction < Pace ) return;
			lastAction = Time.ElapsedMilliseconds;
			if ( !BackpackIsOpen() ) {
				Detail = "opening the inventory";
				PressKey(VKey.I);
				return;
			}
			var slot = GetUI().InventoryPanel.LWeapon.GetClientRect();
			var weapon = Weapon();
			if ( weapon == null ) {
				// eg, a click that missed the socket picked the weapon up: put it back (with nothing on the cursor, this does nothing)
				Detail = "putting the weapon back";
				SkillBar.ClickAt(slot);
				return;
			}
			var gem = GemInBackpack();
			if ( gem != null ) gemColor = gem.Entity.GetComponent<SkillGem>()?.Color ?? SocketColor.None;
			else if ( !picked ) { Detail = $"no {Gem} in the backpack (gems there: {Equipment.BackpackGems()})"; return; }
			int target = TargetSocket(weapon.Entity.GetComponent<Sockets>(), gemColor);
			if ( target < 0 ) return;
			if ( gem != null ) {
				Detail = "picking up the gem";
				SkillBar.ClickAt(gem.GetClientRect());
				picked = true;
				return;
			}
			// not in the backpack and not socketed: it is on the cursor
			var socket = weapon.GetChild(0)?.GetChild(target);
			if ( !IsValid(socket) ) { Detail = $"can't find socket {target} on screen"; return; }
			Detail = $"putting the gem in socket {target}";
			SkillBar.ClickAt(SocketRect(socket.GetClientRect(), slot));
			lastAction += SocketPace - Pace;
		}

		/// <summary>The box of a socket, from its element's top left (the element has no size of its own).</summary>
		public static System.Drawing.RectangleF SocketRect(System.Drawing.RectangleF socket, System.Drawing.RectangleF weaponSlot) {
			float size = .75f * weaponSlot.Width / 2; // a weapon slot is 2 cells wide; a socket is drawn about 3/4 of a cell
			return new System.Drawing.RectangleF(socket.X, socket.Y, size, size);
		}

		internal static void PressKey(VKey key) => Run(new AtE.PressKey(key, 30));
	}

	/// <summary>An objective made of two functions, for one-off steps.</summary>
	public class Step : Objective {
		private readonly string name;
		private readonly Func<CombatContext, bool> isDone;
		private readonly Action<Step, CombatContext> tick;
		public Step(string name, Func<CombatContext, bool> isDone, Action<Step, CombatContext> tick) {
			this.name = name; this.isDone = isDone; this.tick = tick;
		}
		public override string Name => name;
		public override bool Sticky => false;
		public override bool CheckDone(CombatContext ctx) => isDone(ctx);
		public override void Tick(CombatContext ctx) => tick(this, ctx);
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

		/// <summary>Is an item whose path contains all the parts (eg, "/Gems/", "Fireball") in the backpack (open or not).</summary>
		/// <summary>
		/// Is e the gem called name (eg, "Chance to Poison Support"): by its item name (a support's is without " Support"),
		/// or, if that cannot be read, by its metadata path (which is not always the name: Chance to Poison Support is
		/// Metadata/Items/Gems/SupportGemLesserPoison).
		/// </summary>
		public static bool IsGem(Entity e, string name) {
			if ( !(e?.Path is string path) || !path.Contains("/Gems/") ) return false;
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

		/// <summary>Is the gem called name (see IsGem) in the backpack (open or not).</summary>
		public static bool GemInBackpack(string name) =>
			GetUI()?.InventoryPanel?.Backpack?.Items.Any(i => IsGem(i.Entity, name)) ?? false;

		/// <summary>Is the gem called name (see IsGem) in the backpack, or socketed in a worn item.</summary>
		public static bool HasGem(string name) => GemInBackpack(name) || SocketedGems().Any(e => IsGem(e, name));

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

		public static bool TryClickLabel(LabelOnGround label) {
			var rect = label?.Label?.GetClientRect() ?? default;
			var at = new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
			if ( rect.Width <= 0 || !PathFollower.IsSafeToClick(at) ) return false;
			SkillBar.ClickAt(rect);
			return true;
		}

		public static bool TryClickGrid(Offsets.Vector2i grid, float z) {
			if ( !TryGridToScreen(grid, z, out Vector2 at) || !PathFollower.IsSafeToClick(at) ) return false;
			SkillBar.ClickAt(new System.Drawing.RectangleF(at.X, at.Y, 0, 0));
			return true;
		}
	}
}
