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
		/// <summary>
		/// A damaging area on the ground: its center, and radius in cells; or, for a sector (eg, a breath), its apex
		/// (Center), length (Radius), the direction the caster faces (Dir, normalized), and the slice it covers: from From to
		/// To radians off Dir, positive toward the caster's left; or, for a beam, a band Radius cells either side of the
		/// line from Center to End.
		/// </summary>
		public readonly struct Hazard {
			public readonly Offsets.Vector2i Center;
			public readonly float Radius;
			public readonly Vector2 Dir;
			public readonly float From, To;
			public readonly bool IsSector;
			public readonly Offsets.Vector2i End;
			public readonly bool IsBeam;
			public Hazard(Offsets.Vector2i center, float radius) { Center = center; Radius = radius; Dir = default; From = To = 0; IsSector = false; End = center; IsBeam = false; }
			public Hazard(Offsets.Vector2i apex, float length, Vector2 facing, float from, float to) {
				Center = apex; Radius = length; Dir = facing.LengthSquared() > 0 ? Vector2.Normalize(facing) : new Vector2(0, -1);
				From = from; To = to; IsSector = true; End = apex; IsBeam = false;
			}
			/// <summary>A beam: the cells within halfWidth of the line from start to end.</summary>
			public Hazard(Offsets.Vector2i start, Offsets.Vector2i end, float halfWidth) {
				Center = start; End = end; Radius = halfWidth; Dir = default; From = To = 0; IsSector = false; IsBeam = true;
			}
			/// <summary>The caster's left, when it faces Dir (Dir (0,-1): (1,0); see HazardSkills).</summary>
			public Vector2 Left => new Vector2(-Dir.Y, Dir.X);
			/// <summary>Cells from cell to the area's edge (negative inside); for a sector, to its circle's edge.</summary>
			public float EdgeDistance(Offsets.Vector2i cell) => (IsBeam ? BeamDistance(cell) : Combat.Distance(cell, Center)) - Radius;
			private float BeamDistance(Offsets.Vector2i cell) {
				var a = new Vector2(Center.X, Center.Y);
				var ab = new Vector2(End.X, End.Y) - a;
				var p = new Vector2(cell.X, cell.Y);
				float len2 = ab.LengthSquared();
				float t = len2 == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
				return Vector2.Distance(p, a + t * ab);
			}
			public bool Covers(Offsets.Vector2i cell, float margin) {
				if ( IsBeam ) return BeamDistance(cell) <= Radius + margin;
				float d = Combat.Distance(cell, Center);
				if ( d > Radius + margin ) return false;
				if ( !IsSector || d <= margin ) return true;
				var v = new Vector2(cell.X - Center.X, cell.Y - Center.Y);
				double angle = Math.Atan2(Vector2.Dot(v, Left), Vector2.Dot(v, Dir));
				// (the margin widens the slice by the angle it makes at this distance)
				double widen = Math.Atan2(margin, d);
				return angle >= From - widen && angle <= To + widen;
			}
			public override string ToString() => IsBeam ? $"beam {Center} to {End}, {Radius:F0} either side"
				: IsSector ? $"{Center} sector {Radius:F0} facing ({Dir.X:F2}, {Dir.Y:F2}), {From * 180 / Math.PI:F0} to {To * 180 / Math.PI:F0} deg"
				: $"{Center} r{Radius:F0}";
		}

		/// <summary>
		/// Skills that are damaging areas while a monster is using them (no entity of their own until they land): a part of
		/// the skill's name (see Actor.CurrentAction, ActorAction.SkillOf; the first entry that matches is used, so longer
		/// names go first), and the slice of ground in front of the caster it covers: its length in cells, and from/to in
		/// degrees off the way the caster faces, positive toward the caster's left (so its "Right" is negative).
		///
		/// The caster faces the way its Positioned.Rotation says, taking rotation 0 as facing -Y on the grid. Seen once:
		/// Kitava has rotation 0 and faces his arena, which lies toward -Y of him, with his right toward -X (the left of the
		/// screen). Not checked for other rotations.
		/// </summary>
		public static readonly List<(string skillPart, float length, float fromDeg, float toDeg)> HazardSkills = new List<(string, float, float, float)> {
			// Kitava's fire breaths in his final fight (3.30.0c). No entity: only his action (Actor+0x1B0, see
			// Component_Actor_Action), which names the skill. He stands at the top of a fan-shaped arena that lies within
			// about 90 degrees either side of the way he faces, out to about 100 cells. From the user, seen live: DeathBreathRight
			// covered the half of the arena on his right (the left of the screen; the player was hit at Kitava + (-21, -20));
			// Left the other half; Middle the middle 50%. UltimateDeathBreath covers 90% of the arena, all but a strip along
			// the edge on the side opposite its name: the player stood safe from UltimateDeathBreathRight at Kitava + (39, -8),
			// against the right edge of the screen, 78 degrees to his left. The slices run to +-100 degrees for slack, and
			// the 90% ones leave the outer 18 degrees (10% of 180)
			("UltimateDeathBreathRight", 150, -100, 72),
			("UltimateDeathBreathLeft", 150, -72, 100),
			("DeathBreathRight", 150, -100, 0),
			("DeathBreathLeft", 150, 0, 100),
			("DeathBreathMiddle", 150, -45, 45),
		};

		/// <summary>
		/// Skills that light a network of beams between reflectors (no entity of their own): a part of the skill's name, a
		/// part of the reflectors' metadata path, the beams' half-width in cells, and how long (ms) after a monster was last
		/// seen using the skill to keep avoiding them. While it lasts: the beam from the caster to the reflector its action
		/// targets (ActorAction.Target), and, from every reflector within ReflectorRange of the caster, the beam along the
		/// way it faces (Positioned.Rotation, 0 facing -Y), to the reflector it faces (within ReflectorAimDeg), or else
		/// ReflectorRayCells long.
		/// </summary>
		public static readonly List<(string skillPart, string reflectorPath, float halfWidth, long lingerMs)> BeamSkills = new List<(string, string, float, long)> {
			// Renkarr, the Kiln Keeper (the Silo map boss, Monsters/Vilenta/VilentaMap_; 3.30.0c, paused twice):
			// VilentaReflectionMap, red fire beams that give vilenta_reflection_beam_degen_fire. His action targets one of the
			// 12 pylons around the arena walls (Monsters/AtlasBosses/TheElder/ArenaObjects/Tower/BeamTarget1-4,
			// vilenta_pylon.ao; first pylon 8 at the north wall, later pylon 10 in the north-east corner). The pylons never
			// turn (the same Positioned.Rotation in both pauses, a few seconds of fighting apart), and 10 of the 12 face
			// another pylon within 4 degrees: those lines, projected onto the user's screenshot of the second pause, are the
			// star of beams on screen (eg, 11 to 8, 9 to 14, 15 to 8, 4 to 10). Which pylons are lit was not found (no
			// per-pylon state told them apart), so all of the lines are avoided. The beams look about 3 cells wide; how long
			// they last after the channel is a guess
			("VilentaReflection", "ArenaObjects/Tower/BeamTarget", 3, 6000),
		};
		/// <summary>Reflectors (see BeamSkills) within this many cells of the caster are part of its beam network.</summary>
		public static float ReflectorRange = 200;
		/// <summary>A reflector faces another reflector when it lies within this many degrees of the way it faces.</summary>
		public static float ReflectorAimDeg = 10;
		/// <summary>The length of the beam from a reflector that faces no other reflector.</summary>
		public static float ReflectorRayCells = 30;
		// per BeamSkills entry: when, and where, a monster was last seen using it
		private static readonly Dictionary<string, (long when, Offsets.Vector2i caster)> beamSeen = new Dictionary<string, (long, Offsets.Vector2i)>();

		/// <summary>The beams of the BeamSkills in use now, or lately (see BeamSkills).</summary>
		public static IEnumerable<Hazard> BeamHazards(List<Entity> ents) {
			if ( BeamSkills.Count == 0 ) yield break;
			long now = Time.ElapsedMilliseconds;
			// the beam from each caster to its target
			foreach ( var e in ents ) {
				if ( e?.Path == null || !e.Path.StartsWith("Metadata/Monsters/") || !IsAlive(e) ) continue;
				var actor = e.GetComponent<Actor>();
				if ( !IsValid(actor) || (actor.ActionFlags & Offsets.Component_ActionFlags.UsingAbility) == 0 ) continue;
				var action = actor.CurrentAction;
				string name = action?.SkillOf(actor)?.Name;
				if ( name == null ) continue;
				foreach ( var (part, _, halfWidth, _) in BeamSkills ) {
					if ( name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(pos) ) break;
					beamSeen[part] = (now, pos.GridPos);
					var target = action.Target?.GetComponent<Positioned>();
					if ( IsValid(target) && Combat.Distance(target.GridPos, pos.GridPos) > 2 ) yield return new Hazard(pos.GridPos, target.GridPos, halfWidth);
					break;
				}
			}
			// the network of beams between the reflectors
			foreach ( var (part, reflectorPath, halfWidth, lingerMs) in BeamSkills ) {
				if ( !beamSeen.TryGetValue(part, out var seen) || now - seen.when > lingerMs ) continue;
				var reflectors = new List<(Offsets.Vector2i at, Vector2 facing)>();
				foreach ( var e in ents ) {
					if ( e?.Path == null || !e.Path.Contains(reflectorPath) ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(pos) || Combat.Distance(pos.GridPos, seen.caster) > ReflectorRange ) continue;
					float r = pos.Rotation; // 0: facing -Y
					reflectors.Add((pos.GridPos, new Vector2((float)Math.Sin(r), -(float)Math.Cos(r))));
				}
				foreach ( var (at, facing) in reflectors ) {
					Offsets.Vector2i end = default;
					double bestAngle = ReflectorAimDeg * Math.PI / 180;
					bool found = false;
					foreach ( var (other, _) in reflectors ) {
						var v = new Vector2(other.X - at.X, other.Y - at.Y);
						if ( v.LengthSquared() < 4 ) continue;
						double angle = Math.Abs(Math.Atan2(facing.X * v.Y - facing.Y * v.X, Vector2.Dot(facing, v)));
						if ( angle <= bestAngle ) { bestAngle = angle; end = other; found = true; }
					}
					if ( !found ) end = new Offsets.Vector2i(at.X + (int)Math.Round(facing.X * ReflectorRayCells), at.Y + (int)Math.Round(facing.Y * ReflectorRayCells));
					yield return new Hazard(at, end, halfWidth);
				}
			}
		}

		/// <summary>The slices of the HazardSkills being used now, by any monster near, and the beams of the BeamSkills (see BeamHazards).</summary>
		public static IEnumerable<Hazard> SkillHazards() {
			var ents = (GetEntities() ?? Enumerable.Empty<Entity>()).ToList();
			foreach ( var h in BeamHazards(ents) ) yield return h;
			if ( HazardSkills.Count == 0 ) yield break;
			foreach ( var e in ents ) {
				if ( e?.Path == null || !e.Path.StartsWith("Metadata/Monsters/") || !IsAlive(e) ) continue;
				var actor = e.GetComponent<Actor>();
				if ( !IsValid(actor) || (actor.ActionFlags & Offsets.Component_ActionFlags.UsingAbility) == 0 ) continue;
				string name = actor.CurrentAction?.SkillOf(actor)?.Name;
				if ( name == null ) continue;
				foreach ( var (part, length, fromDeg, toDeg) in HazardSkills ) {
					if ( name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(pos) ) break;
					float r = pos.Rotation; // 0: facing -Y (see HazardSkills)
					var facing = new Vector2((float)Math.Sin(r), -(float)Math.Cos(r));
					const float rad = (float)(Math.PI / 180);
					yield return new Hazard(pos.GridPos, length, facing, fromDeg * rad, toDeg * rad);
					break;
				}
			}
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
			// burning ground (ground_fire_burn), eg Renkarr's in the Silo map (3.30.0c, his skill CGEVilentaMapBurningGround):
			// the same FillGroundEffect, 10 at GroundEffect+0x38 again, but the player was burning 12 cells from its center,
			// so the radius is taken as 14
			("ground_effects/FillGroundEffect", "ground_fire_burn", 14),
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
			// Augury empowerment "Blackfire Wall" (3.30, seen on Kitava: a wave of fire that leaves the player burning with
			// Blackfire). Monsters/Augury/Daemon/BlackfireWallDaemon (sits on the boss, StateMachine have_wall, tier) lays a
			// line of Metadata/Monsters/Daemon/Augury/BlackfireWallObject segments (Animated, BaseEvents, Buffs, Positioned,
			// Render, StateMachine aura=1, tier), animated object .../Boss_Empowerments/BlackfireWalls/blackfire_wall.ao,
			// each with the buff blackfire_wall_daemon_burn, which the player standing in it gets too: the segments are the
			// source of Blackfire. Paused mid-deploy: 3 segments about 10 cells apart, Render.Bounds 30 x 30 (about 3
			// cells), the last one on the player. Radius 6 so the circles join into the wall
			("Daemon/Augury/BlackfireWallObject", null, 6),
			// Augury empowerment "Crow Bombs" (3.30, seen on Kitava alongside the wall, not seen exploding yet):
			// Monsters/Augury/Daemon/CrowBombDaemon places Metadata/Monsters/Daemon/Augury/CrowBombObject (Animated,
			// BaseEvents, Positioned, Render, StateMachine spawn=1), animated object .../Boss_Empowerments/CrowBombs/crow_bomb.ao,
			// Render.Bounds 30 x 30. 10 cells is a guess at the blast
			("Daemon/Augury/CrowBombObject", null, 10),
			// Daresso's sword traps in The Ring of Blades (Act 4; the name from the user, not seen live yet: 8 cells is a guess)
			("DaressoSwordTrap", null, 8),
		};

		/// <summary>Where the damaging areas are now. Default: the skills in HazardSkills being used, and the entities matching HazardPaths.</summary>
		public Func<CombatContext, IEnumerable<Hazard>> Hazards = DefaultHazards;

		/// <summary>Stay this many cells further out than an area's radius.</summary>
		public float Margin = 4;
		/// <summary>How far from the player (cells) to look for a safe cell.</summary>
		public int SearchRadius = 60;
		/// <summary>How far to look when no cell within SearchRadius is safe.</summary>
		public int FarSearchRadius = 150;
		/// <summary>Range to use the skill from, in cells; 0 for 80% of the driver's AttackRange.</summary>
		public float SpamRange;

		public AvoidSpam(string label, Func<Entity, bool> match, Func<string> skill) : base(label, match, skill) { }

		public override string Name => $"Kill {Label} with {Skill}, out of its damaging areas";

		/// <summary>Log the damaging areas (how many, and the nearest) at most this often, when their number has changed.</summary>
		public long HazardLogMs = 5000;
		private int lastHazardCount = -1;
		private long lastHazardLog;

		public static IEnumerable<Hazard> DefaultHazards(CombatContext ctx) {
			foreach ( var h in SkillHazards() ) yield return h;
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
				string nearest = hazards.Count == 0 ? "" : $", the nearest {hazards.Min(h => h.EdgeDistance(ctx.Grid)):F0} cells from its edge";
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

			// safe and in range: stand still and spam the skill (or, with none, the driver's Attack) at it; a channelling
			// skill is held down instead, until the player walks again (see CombatDriver.TryHoldChannel)
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
			// past SearchRadius only while nothing safe is found (eg, Kitava's ultimate breath: the only safe strip is along
			// the far edge of the arena)
			for ( int r = 3; r <= SearchRadius || (bestScore == float.MaxValue && r <= FarSearchRadius); r += 3 ) {
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
