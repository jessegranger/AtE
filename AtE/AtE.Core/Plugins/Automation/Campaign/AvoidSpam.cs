using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Kill a boss that covers the ground near it with lasting damaging areas (eg, Merveil, one kind in each phase): stay
	/// out of the areas, and otherwise stand still and spam the skill at it. Standing in an area (or within Margin cells
	/// of one): walk to the nearest cell outside all of them, preferring one in range of the boss and with line of sight
	/// to it. Out of range: get in range (Engage).
	///
	/// The areas are entities of their own (see HazardPaths): so far Merveil's ice storm; her other phase's is still to
	/// be found.
	/// </summary>
	public class AvoidSpam : Kill {
		/// <summary>A damaging area on the ground: its center, and radius in cells.</summary>
		public readonly struct Hazard {
			public readonly Offsets.Vector2i Center;
			public readonly float Radius;
			public Hazard(Offsets.Vector2i center, float radius) { Center = center; Radius = radius; }
			public bool Covers(Offsets.Vector2i cell, float margin) => Combat.Distance(cell, Center) <= Radius + margin;
			public override string ToString() => $"{Center} r{Radius:F0}";
		}

		/// <summary>
		/// The entities that are damaging areas: a part of the metadata path, a buff the entity has (null: any), and the
		/// area's radius in cells.
		/// </summary>
		public static readonly List<(string pathPart, string buff, float radius)> HazardPaths = new List<(string, string, float)> {
			// Merveil's ice storm (3.29.4, paused in it): a ground effect with ground_ice_chill wherever ice fell, 26 of them
			// within 25 cells of her. Each has 10 at GroundEffect+0x38, taken as its radius in cells (the player stood just
			// outside, 10 cells from the nearest). (The SleepableServerEffect entities on the same spots are the falling ice.)
			("ground_effects/FillGroundEffect", "ground_ice_chill", 10),
			// her second phase's water geysers (3.29.4, paused with 10 placed, the nearest 23-27 cells from the player, who was
			// outside them): entities with only Animated, Positioned, Render, Transitionable. Render.Bounds is 19.6 x 19.6 x 289
			// (about 2 cells across, a tall pillar), so the drawing is narrow: 12 cells is a guess at the damaging radius, on the safe side
			("merveil/water_geyser/WaterGeyser", null, 12),
		};

		/// <summary>Where the damaging areas are now. Default: the entities matching HazardPaths.</summary>
		public Func<CombatContext, IEnumerable<Hazard>> Hazards = DefaultHazards;

		/// <summary>Stay this many cells further out than an area's radius.</summary>
		public float Margin = 4;
		/// <summary>How far from the player (cells) to look for a safe cell.</summary>
		public int SearchRadius = 60;
		/// <summary>Range to use the skill from, in cells; 0 for 80% of the driver's AttackRange.</summary>
		public float SpamRange;

		public AvoidSpam(string label, Func<Entity, bool> match, Func<string> skill) : base(label, match, skill) { }

		public override string Name => $"Kill {Label} with {Skill}, out of its damaging areas";

		private int lastHazardCount = -1;

		public static IEnumerable<Hazard> DefaultHazards(CombatContext ctx) {
			if ( HazardPaths.Count == 0 ) yield break;
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				string path = e?.Path;
				if ( path == null ) continue;
				foreach ( var (part, buff, radius) in HazardPaths ) {
					if ( !path.Contains(part) || (buff != null && !HasBuff(e, buff)) ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( IsValid(pos) ) yield return new Hazard(pos.GridPos, radius);
					break;
				}
			}
		}

		protected override void Fight(CombatContext ctx, Hostile target) {
			string skill = Skill;
			float range = SpamRange > 0 ? SpamRange : ctx.Driver.AttackRange * .8f;
			var hazards = Hazards(ctx).ToList();
			if ( hazards.Count != lastHazardCount ) {
				lastHazardCount = hazards.Count;
				Log($"AvoidSpam: {hazards.Count} damaging areas near {Label}{(hazards.Count == 0 ? "" : ": " + string.Join(", ", hazards))}");
			}

			// in (or at the edge of) an area: get out first
			if ( hazards.Any(h => h.Covers(ctx.Grid, Margin)) ) {
				if ( TryFindSafeCell(ctx, hazards, target, range, out var safe) ) {
					Detail = $"stepping out of a damaging area, to {safe}";
					ctx.Driver.WalkTo(safe, $"avoiding {Label}'s damaging area");
				} else {
					Detail = "in a damaging area, with nowhere safe near";
					base.Fight(ctx, target);
				}
				return;
			}

			// out of range or sight: get in range (TODO: keep the walk out of the areas, not just where it ends)
			bool inRange = Combat.Distance(ctx.Grid, target.GridPos) <= range && ctx.Terrain.HasLineOfSight(ctx.Grid, target.GridPos);
			if ( !inRange || !TryWorldToScreen(target.WorldPos, out Vector2 screen) || !PathFollower.IsSafeToClick(screen) ) {
				Detail = $"getting in range, {target.Distance:F0} cells away";
				if ( !ctx.Driver.Engage(ctx, target, skill, range, true) ) base.Fight(ctx, target);
				return;
			}

			// safe and in range: stand still and spam the skill at it
			ctx.Driver.StopWalking("using a skill");
			if ( ctx.Driver.TryPlaceTotem(ctx, screen) ) { Detail = "placing a totem"; return; }
			Detail = $"using {skill} at it, {target.Distance:F0} cells away";
			ctx.Driver.UseSkillPaced(skill, screen);
		}

		/// <summary>
		/// The nearest walkable cell (that can be walked to) out of all the areas (by Margin), within SearchRadius:
		/// preferring one in range of the target with line of sight to it.
		/// </summary>
		private bool TryFindSafeCell(CombatContext ctx, List<Hazard> hazards, Hostile target, float range, out Offsets.Vector2i best) {
			best = default;
			float bestScore = float.MaxValue;
			for ( int r = 3; r <= SearchRadius; r += 3 ) {
				int steps = Math.Max(8, r);
				for ( int i = 0; i < steps; i++ ) {
					double a = 2 * Math.PI * i / steps;
					var cell = new Offsets.Vector2i(ctx.Grid.X + (int)Math.Round(r * Math.Cos(a)), ctx.Grid.Y + (int)Math.Round(r * Math.Sin(a)));
					if ( !ctx.Terrain.IsWalkable(cell) || hazards.Any(h => h.Covers(cell, Margin)) ) continue;
					if ( !DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, cell) ) continue;
					bool good = Combat.Distance(cell, target.GridPos) <= range && ctx.Terrain.HasLineOfSight(cell, target.GridPos);
					float score = r + (good ? 0 : 1000);
					if ( score < bestScore ) { bestScore = score; best = cell; }
				}
				if ( bestScore < 1000 ) return true; // (the nearest ring with a good cell)
			}
			return bestScore < float.MaxValue;
		}
	}
}
