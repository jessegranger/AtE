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
	/// </summary>
	public abstract class Objective {
		/// <summary>What it is, for the on-screen list (eg, "Kill Hillock").</summary>
		public abstract string Name { get; }
		/// <summary>What it is doing right now (eg, "looking for Hillock"); shown next to the Name.</summary>
		public string Detail = "";
		public bool IsComplete { get; internal set; }
		/// <summary>True if, once done, it stays done (its done-ness can't be read back later); false to check it every time.</summary>
		public virtual bool Sticky => true;

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
	}

	/// <summary>Fight everything near and explore the whole area.</summary>
	public class ClearArea : Objective {
		public string Skill;
		/// <param name="skill">The skill to fight with, eg "Fireball"; null uses the driver's Attack.</param>
		public ClearArea(string skill = null) => Skill = skill;
		public override string Name => "Clear the area";
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
		public readonly string PathContains;
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
		public Kill(string label, string pathContains, string skill = null, float range = 0, bool? needsLineOfSight = null) {
			Label = label; PathContains = pathContains; Skill = skill; Range = range; NeedsLineOfSight = needsLineOfSight;
		}
		public override string Name => $"Kill {Label}{(Skill == null ? "" : $" with {Skill}")}";
		public override void Reset() { base.Reset(); killed = seen = false; }

		private IEnumerable<Entity> Matches() => GetEntities()?.Where(e => e?.Path?.Contains(PathContains) ?? false) ?? Empty<Entity>();

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
					Detail = $"fighting, {target.Distance:F0} cells away";
					if ( ctx.Driver.Engage(ctx, target, Skill, Range, NeedsLineOfSight) ) return;
					Detail = "can't find a spot to attack from";
					ctx.Driver.WalkTo(Approach(ctx, target.GridPos), $"walking to {Label}");
					return;
				}
			}
			// not in sight: clear the way and keep looking
			if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx, Skill) ) { Detail = "fighting on the way"; return; }
			if ( seen ) {
				Detail = $"going back to where it was last seen";
				ctx.Driver.WalkTo(Approach(ctx, lastSeen), $"looking for {Label}");
				return;
			}
			Detail = "looking for it";
			if ( !ctx.Driver.TryExplore(ctx) ) Detail = "explored everything without finding it";
		}
	}

	/// <summary>Pick up an item from the ground, found by the text of its label (eg, "Driftwood Wand").</summary>
	public class PickUp : Objective {
		public readonly string LabelText;
		/// <summary>Click the label when within this many cells of the item.</summary>
		public float Reach = 12;
		private bool pickedUp;
		private long lastClick;
		private bool clicked;

		public PickUp(string labelText) => LabelText = labelText;
		public override string Name => $"Pick up {LabelText}";
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
				if ( ctx.Driver.TryRetreat(ctx) || ctx.Driver.TryFight(ctx) ) { Detail = "fighting on the way"; return; }
				Detail = "looking for it";
				if ( !ctx.Driver.TryExplore(ctx) ) Detail = "explored everything without finding it";
				return;
			}
			float d = Combat.Distance(ctx.Grid, pos.GridPos);
			if ( d > Reach ) {
				Detail = $"{d:F0} cells away";
				ctx.Driver.WalkTo(Approach(ctx, pos.GridPos), $"walking to {LabelText}");
				return;
			}
			ctx.Driver.StopWalking("picking up");
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
