using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The default way to fight any monster, normal to unique (Fight calls one per target, see Fight.AvoidSpamTarget), and
	/// bosses that cover the ground near them with lasting damaging areas (eg, Merveil, one kind in each phase): stay
	/// out of the areas, and otherwise stand still and spam the skill at it. Standing in an area (or within Margin cells
	/// of one): walk to the nearest cell outside all of them, preferring one in range of the boss and with line of sight
	/// to it. Out of range: get in range (Engage).
	///
	/// The areas are entities of their own (see HazardPaths): so far Merveil's ice storm and water geysers, and General
	/// Gravicius's falling fireballs.
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
		/// The entities that are damaging areas: a part of the metadata path (or, for an effect entity, of its animated
		/// object's .ao path), a buff the entity has (null: any), and the area's radius in cells.
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
			// General Gravicius's fireballs that fall from the sky (3.29.3, paused with 19 placed, 9-60 cells around him): entities
			// Metadata/Effects/SleepableServerEffect (Animated, BaseEvents, LimitedLifespan, Positioned, Render) whose animated
			// object is "HellfireBarrageEffect", matched by its .ao path (see Animated.AnimatedObjectPath). Render.Bounds is
			// 19.6 x 19.6 x 112 (a narrow falling pillar), so 8 cells is a guess at the blast and its burning ground
			("Gravicius/ballistastorm.ao", null, 8),
			// Kitava's bloody pools (physical damage over time) in his final fight (3.30, paused with 19 placed, 28-100 cells from
			// the player): Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect (Animated, BaseEvents, Buffs,
			// GroundEffect, Positioned, Render) with the buff ground_devouring_darkness, animated object
			// ground_effects/devouring_darkness/ground_rig.ao. GroundEffect+0x38 is 10, as for Merveil's ice: radius 10
			("ground_effects/VisibleServerGroundEffect", "ground_devouring_darkness", 10),
			// Augury league boss empowerment "Orbital Viscera Cannon" (3.30, seen on Kitava): a Daemon monster
			// (Monsters/Augury/Daemon/OrbitalVisceraCannonDaemon_, sitting on the boss, StateMachine beam_begin/tier) fires a
			// Metadata/Effects/ServerEffect (LimitedLifespan) whose animated object is
			// .../League_Augury/Boss_Empowerments/OrbitalVisceraBeam/viscera_beam.ao. Render.Bounds 19.6 x 19.6 like the
			// geysers; 12 cells is a guess at the blast
			("OrbitalVisceraBeam/viscera_beam.ao", null, 12),
			// Daresso's sword traps in The Ring of Blades (Act 4; the name from the user, not seen live yet: 8 cells is a guess)
			("DaressoSwordTrap", null, 8),
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

		/// <summary>Log the damaging areas (how many, and the nearest) at most this often, when their number has changed.</summary>
		public long HazardLogMs = 5000;
		private int lastHazardCount = -1;
		private long lastHazardLog;

		public static IEnumerable<Hazard> DefaultHazards(CombatContext ctx) {
			if ( HazardPaths.Count == 0 ) yield break;
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				string path = e?.Path;
				if ( path == null ) continue;
				// effects with a generic metadata path (eg, SleepableServerEffect) are told apart by their animated object
				string aoPath = path.StartsWith("Metadata/Effects/") ? e.GetComponent<Animated>()?.AnimatedObjectPath : null;
				foreach ( var (part, buff, radius) in HazardPaths ) {
					if ( !(path.Contains(part) || (aoPath != null && aoPath.Contains(part))) || (buff != null && !HasBuff(e, buff)) ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( IsValid(pos) ) yield return new Hazard(pos.GridPos, radius);
					break;
				}
			}
		}

		protected override void Fight(CombatContext ctx, Hostile target) {
			// life low: retreat first (as Fight does between targets)
			if ( ctx.Driver.TryRetreat(ctx) ) { Detail = "retreating"; return; }
			string skill = Skill;
			float range = SpamRange > 0 ? SpamRange : ctx.Driver.AttackRange * .8f;
			var hazards = Hazards(ctx).ToList();
			// (no line for "0 areas" until there were some: it runs for every monster fought)
			if ( hazards.Count != Math.Max(0, lastHazardCount) && Time.ElapsedMilliseconds - lastHazardLog >= HazardLogMs ) {
				lastHazardCount = hazards.Count;
				lastHazardLog = Time.ElapsedMilliseconds;
				string nearest = hazards.Count == 0 ? "" : $", the nearest {hazards.Min(h => Combat.Distance(ctx.Grid, h.Center) - h.Radius):F0} cells from its edge";
				Log($"AvoidSpam: {hazards.Count} damaging areas near {Label}{nearest}");
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

			// safe and in range: stand still and spam the skill (or, with none, the driver's Attack) at it
			ctx.Driver.StopWalking("using a skill");
			Detail = $"using {skill ?? "the driver's attack"} at it, {target.Distance:F0} cells away";
			ctx.Driver.Engage(ctx, target, skill, range, true); // (in range: places a totem, or attacks; it doesn't walk)
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
