using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The default way to fight any monster, normal to unique (Fight calls one per target, see Fight.AvoidSpamTarget), and
	/// bosses that cover the ground near them with lasting damaging areas (eg, Merveil, one kind in each phase): stay
	/// out of the areas, and otherwise stand still and spam the skill at it. The areas go on the DangerMap (this class
	/// holds the catalog of them, see CatalogHazards). Standing in danger (an area, or its margin): walk to the best cell
	/// (DangerMap.TryFindSafest: near, safe, in range of the boss and with line of sight to it). Out of range: with areas
	/// about, walk to the best safe cell in range; else (or with none near) get in range (Engage). The walk itself is
	/// planned without regard to danger: the path follower uses a travel skill over a dangerous stretch (SkipDanger).
	///
	/// The areas are skills being used (HazardSkills, BeamSkills) and entities of their own (HazardPaths), eg Merveil's
	/// ice storm and water geysers, and General Gravicius's falling fireballs.
	/// </summary>
	public class AvoidSpam : Kill {
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
					if ( IsValid(target) && Combat.Distance(target.GridPos, pos.GridPos) > 2 ) yield return new Hazard(pos.GridPos, target.GridPos, halfWidth) { Layer = DangerLayer.Now, Weight = BeamWeight, Source = part };
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
					yield return new Hazard(at, end, halfWidth) { Layer = DangerLayer.Now, Weight = BeamWeight, Source = part };
				}
			}
		}

		/// <summary>The weight (see DangerMap) of a HazardSkills slice, on the Soon layer (the skill is being cast: it lands at the end).</summary>
		public static byte SkillWeight = 100;
		/// <summary>The weight of a BeamSkills beam, on the Now layer.</summary>
		public static byte BeamWeight = 60;

		/// <summary>
		/// Skills aimed at a cell (the action's Destination, Actor+0x1B0 +0x130) that land there, or along the way to it,
		/// when the cast ends (no entity of their own until then): a part of the skill's name, and the ground it covers:
		/// a line (from the caster toward the cell, size cells long, halfWidth either side) or a circle (size cells around
		/// the cell); and how it goes on the DangerMap (on the Soon layer: the cast is not over yet). With a casterPart, only
		/// a caster whose metadata path has it (for a skill with a common name, eg "Melee").
		/// </summary>
		public static readonly List<(string skillPart, string casterPart, bool line, float size, float halfWidth, byte weight)> AimedSkills = new List<(string, string, bool, float, float, byte)> {
			// Augury empowerment "Bloodbone" (3.30.0c, seen on Renkarr in the Silo map, paused as the user saw a wave of bone
			// spikes about to go from him (center-left) across the arena): Monsters/Augury/Daemon/BloodboneProjectileDaemon
			// (BloodBoneDaemon.ao) sits on the boss, using GPSBloodboneProjectileMed aimed at the player (Target the player
			// entity, Destination the player's cell). The spikes run past the player: 80 cells and 4 either side are guesses
			("GPSBloodboneProjectile", null, true, 80, 4, 60),
			// Renkarr's fire bombs (same pause): Monsters/Daemon/VilentaCarpetBombDaemonMap, using VilentaBombTossFire aimed at
			// a cell near the player (12 cells from them). Not seen landing: a circle of 12 cells is a guess
			("VilentaBombToss", null, false, 12, 0, 50),
			// Nightmare Manifest's orb (PietyBeastMapBoss; 3.30.0c, paused as the user saw an orb about to pop beside him):
			// PietyCorruptedBallLightningMaps, aimed at a cell 33 cells from him (no Target entity), which the user placed
			// "screen-left of him, screen-down from the player". Not seen popping: 15 cells is a guess
			("PietyCorruptedBallLightning", null, false, 15, 0, 80),
			// Tore, Towering Ancient (Metadata/Monsters/Viking/VikingMapBoss, a map boss; 3.30.0c, paused as the user saw him
			// about to do a big slam): his action was Melee, aimed at the player's cell (6 cells from him). His other skills:
			// VikingCycloneMapBoss, VikingUpheavalMapBoss. Not seen landing: 15 cells around the cell was drawn slightly
			// smaller than the slam's mark on the ground (the user, with a screenshot): 18
			("Melee", "Viking/VikingMapBoss", false, 18, 0, 80),
		};

		/// <summary>
		/// Buffs that put a damaging area around the monster that has them (no entity of their own): the buff's name, the
		/// area's inner and outer radius in cells (a ring; inner 0: a circle), and how it goes on the DangerMap.
		/// </summary>
		public static readonly List<(string buff, float inner, float outer, DangerLayer layer, byte weight)> BuffHazards = new List<(string, float, float, DangerLayer, byte)> {
			// Augury empowerment "Blood Donut" (3.30.0c, seen on Tore, Towering Ancient, paused): a ring of blood around the
			// boss, harmless in the middle (from the user). Monsters/Augury/Daemon/BloodDonutDaemon (StateMachine donut=1, tier)
			// sits on him and gives him the buff augury_blood_donut (no radius among its stats). 20 to 30 cells, a guess,
			// matched the outer edge on the user's screenshots, but the band was drawn too wide inward (the safe middle too
			// small): inner 24
			("augury_blood_donut", 24, 30, DangerLayer.Now, 60),
			// Archnemesis "Mana Siphoner" (3.30.0c, seen on a rare Fenumal Widow, paused): a blue ring around the monster
			// that drains mana and does lightning damage (from the user). Monsters/Daemon/Archnemesis/ManaDonutDaemon sits on
			// it, with the buff archnemesis_mana_donut itself (StateMachine donut=1; no radius among its stats). The blood
			// ring's 24 to 30 was the right width but drawn too far in on the user's screenshot: 28 to 34
			("archnemesis_mana_donut", 28, 34, DangerLayer.Now, 50),
		};

		/// <summary>The BuffHazards around the monsters in ents.</summary>
		public static IEnumerable<Hazard> BuffAreaHazards(List<Entity> ents) {
			if ( BuffHazards.Count == 0 ) yield break;
			foreach ( var e in ents ) {
				if ( e?.Path == null || !e.Path.StartsWith("Metadata/Monsters/") || !IsAlive(e) ) continue;
				foreach ( var (buff, inner, outer, layer, weight) in BuffHazards ) {
					if ( !HasBuff(e, buff) ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(pos) ) break;
					var shape = inner > 0 ? new Hazard(pos.GridPos, inner, outer, true) : new Hazard(pos.GridPos, outer);
					yield return shape with { Layer = layer, Weight = weight, Source = buff };
				}
			}
		}

		/// <summary>
		/// Beams a monster channels along the way it faces (Positioned.Rotation, 0 facing -Y), turning as it goes: a part of
		/// the skill's name, the beam's length and half-width in cells, its weight (on the Now layer), and how far ahead
		/// (ms) of it to keep out of as it turns (on the Soon layer, at half its weight). The turn rate is measured from
		/// frame to frame (see turning); until it is known (or while it doesn't turn), UnknownSweepDeg either side.
		/// </summary>
		public static readonly List<(string skillPart, float length, float halfWidth, byte weight, long lookaheadMs)> RotatingBeamSkills = new List<(string, float, float, byte, long)> {
			// Nightmare Manifest (Metadata/Monsters/Axis/PietyBeastMapBoss, a map boss; 3.30.0c, paused mid-beam):
			// PietyCorruptedLightningBeamMaps, a lightning beam that turns around him, very damaging. No entity of its own
			// (no Beam component, no effect near it): only his action. The action's Destination was 6 cells +Y of him (toward
			// the player, 25 cells +Y), while his rotation (0.405) said he faced (0.39, -0.92): taken as the beam having
			// turned since it was aimed. In a later pause (rotation 1.079) the user saw the beam drawn along his facing (by
			// DangerDisplay) lie on the real one. Which way it turns is not confirmed. Length and width are guesses (Piety's
			// own beam is taken as 70 x 4)
			("PietyCorruptedLightningBeam", 90, 5, 120, 1200),
		};
		/// <summary>Not knowing which way a RotatingBeamSkills beam turns: keep out of this many degrees either side of it (on the Soon layer).</summary>
		public static float UnknownSweepDeg = 45;
		// per caster (entity id): its rotation when last seen, when, and its turn rate (radians/s, smoothed; NaN unknown)
		private static readonly Dictionary<uint, (float rotation, long at, float rate)> turning = new Dictionary<uint, (float, long, float)>();

		private static IEnumerable<Hazard> RotatingBeam(Entity e, Positioned pos, string part, float length, float halfWidth, byte weight, long lookaheadMs) {
			long now = Time.ElapsedMilliseconds;
			float r = pos.Rotation; // 0: facing -Y
			float rate = float.NaN;
			if ( turning.TryGetValue(e.Id, out var seen) && now - seen.at < 1000 ) {
				rate = seen.rate;
				if ( now > seen.at ) {
					float d = r - seen.rotation;
					while ( d > Math.PI ) d -= (float)(2 * Math.PI);
					while ( d < -Math.PI ) d += (float)(2 * Math.PI);
					if ( d != 0 ) {
						float measured = d * 1000f / (now - seen.at);
						rate = float.IsNaN(rate) ? measured : (rate * .7f) + (measured * .3f);
					}
				}
			}
			turning[e.Id] = (r, now, rate);
			var facing = new Vector2((float)Math.Sin(r), -(float)Math.Cos(r));
			var end = new Offsets.Vector2i(pos.GridPos.X + (int)Math.Round(facing.X * length), pos.GridPos.Y + (int)Math.Round(facing.Y * length));
			yield return new Hazard(pos.GridPos, end, halfWidth) { Layer = DangerLayer.Now, Weight = weight, Source = part };
			// where it turns next. A sector's angles are positive toward the caster's left, Left = (-Dir.Y, Dir.X); a
			// growing rotation turns facing (sin r, -cos r) toward (cos r, sin r) = Left: so the same sign
			const float rad = (float)(Math.PI / 180);
			float from = -UnknownSweepDeg * rad, to = UnknownSweepDeg * rad;
			if ( !float.IsNaN(rate) && rate != 0 ) {
				float ahead = Math.Clamp(rate * lookaheadMs / 1000f, -(float)Math.PI, (float)Math.PI);
				from = Math.Min(0, ahead);
				to = Math.Max(0, ahead);
			}
			yield return new Hazard(pos.GridPos, length, facing, from, to) { Layer = DangerLayer.Soon, Weight = (byte)(weight / 2), Source = part + " (turning)" };
		}

		/// <summary>The slices of the HazardSkills, the AimedSkills and RotatingBeamSkills, being used now by any monster in ents.</summary>
		public static IEnumerable<Hazard> SkillHazards(List<Entity> ents) {
			if ( HazardSkills.Count == 0 && AimedSkills.Count == 0 && RotatingBeamSkills.Count == 0 ) yield break;
			foreach ( var e in ents ) {
				if ( e?.Path == null || !e.Path.StartsWith("Metadata/Monsters/") || !IsAlive(e) ) continue;
				var actor = e.GetComponent<Actor>();
				if ( !IsValid(actor) || (actor.ActionFlags & Offsets.Component_ActionFlags.UsingAbility) == 0 ) continue;
				var action = actor.CurrentAction;
				string name = action?.SkillOf(actor)?.Name;
				if ( name == null ) continue;
				bool aimed = false;
				foreach ( var (part, length, halfWidth, weight, lookaheadMs) in RotatingBeamSkills ) {
					if ( name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
					aimed = true;
					var pos = e.GetComponent<Positioned>();
					if ( IsValid(pos) ) foreach ( var h in RotatingBeam(e, pos, part, length, halfWidth, weight, lookaheadMs) ) yield return h;
					break;
				}
				if ( aimed ) continue;
				foreach ( var (part, casterPart, line, size, halfWidth, weight) in AimedSkills ) {
					if ( name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0 || (casterPart != null && !e.Path.Contains(casterPart)) ) continue;
					aimed = true;
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(pos) ) break;
					var at = action.Destination;
					if ( Combat.Distance(at, pos.GridPos) < 1 ) { // (no cell: at the target entity)
						var target = action.Target?.GetComponent<Positioned>();
						if ( IsValid(target) ) at = target.GridPos;
					}
					if ( !line ) {
						yield return new Hazard(at, size) { Layer = DangerLayer.Soon, Weight = weight, Source = part };
						break;
					}
					var dir = new Vector2(at.X - pos.GridPos.X, at.Y - pos.GridPos.Y);
					if ( dir.LengthSquared() < 1 ) break;
					dir = Vector2.Normalize(dir) * size;
					var end = new Offsets.Vector2i(pos.GridPos.X + (int)Math.Round(dir.X), pos.GridPos.Y + (int)Math.Round(dir.Y));
					yield return new Hazard(pos.GridPos, end, halfWidth) { Layer = DangerLayer.Soon, Weight = weight, Source = part };
					break;
				}
				if ( aimed ) continue;
				foreach ( var (part, length, fromDeg, toDeg) in HazardSkills ) {
					if ( name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(pos) ) break;
					float r = pos.Rotation; // 0: facing -Y (see HazardSkills)
					var facing = new Vector2((float)Math.Sin(r), -(float)Math.Cos(r));
					const float rad = (float)(Math.PI / 180);
					yield return new Hazard(pos.GridPos, length, facing, fromDeg * rad, toDeg * rad) { Layer = DangerLayer.Soon, Weight = SkillWeight, Source = part };
					break;
				}
			}
		}

		/// <summary>
		/// The entities that are damaging areas: a part of the metadata path (or, for an effect entity, of its animated
		/// object's .ao path), a buff the entity has (null: any), the area's radius in cells, and how it goes on the DangerMap:
		/// its layer (Now: it hurts now; Soon: it is about to land or go off) and weight (at least 2 * DangerMap.SafeLevel,
		/// so the Margin ring, at half weight, is not safe either; about 100 for a lethal one, 30 for burning ground).
		/// </summary>
		public static readonly List<(string pathPart, string buff, float radius, DangerLayer layer, byte weight)> HazardPaths = new List<(string, string, float, DangerLayer, byte)> {
			// Merveil's ice storm (3.29.4, paused in it): a ground effect with ground_ice_chill wherever ice fell, 26 of them
			// within 25 cells of her. Each has 10 at GroundEffect+0x38, taken as its radius in cells (the player stood just
			// outside, 10 cells from the nearest). (The SleepableServerEffect entities on the same spots are the falling ice.)
			("ground_effects/FillGroundEffect", "ground_ice_chill", 10, DangerLayer.Now, 30),
			// burning ground (ground_fire_burn), eg Renkarr's in the Silo map (3.30.0c, his skill CGEVilentaMapBurningGround):
			// the same FillGroundEffect, 10 at GroundEffect+0x38 again, but the player was burning 12 cells from its center,
			// so the radius is taken as 14
			("ground_effects/FillGroundEffect", "ground_fire_burn", 14, DangerLayer.Now, 30),
			// her second phase's water geysers (3.29.4, paused with 10 placed, the nearest 23-27 cells from the player, who was
			// outside them): entities with only Animated, Positioned, Render, Transitionable. Render.Bounds is 19.6 x 19.6 x 289
			// (about 2 cells across, a tall pillar), so the drawing is narrow: 12 cells is a guess at the damaging radius, on the safe side
			("merveil/water_geyser/WaterGeyser", null, 12, DangerLayer.Now, 40),
			// General Gravicius's fireballs that fall from the sky (3.29.3, paused with 19 placed, 9-60 cells around him): entities
			// Metadata/Effects/SleepableServerEffect (Animated, BaseEvents, LimitedLifespan, Positioned, Render) whose animated
			// object is "HellfireBarrageEffect", matched by its .ao path (see Animated.AnimatedObjectPath). Render.Bounds is
			// 19.6 x 19.6 x 112 (a narrow falling pillar), so 8 cells is a guess at the blast and its burning ground
			("Gravicius/ballistastorm.ao", null, 8, DangerLayer.Soon, 40),
			// Kitava's bloody pools (physical damage over time) in his final fight (3.30, paused with 19 placed, 28-100 cells from
			// the player): Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect (Animated, BaseEvents, Buffs,
			// GroundEffect, Positioned, Render) with the buff ground_devouring_darkness, animated object
			// ground_effects/devouring_darkness/ground_rig.ao. GroundEffect+0x38 is 10, as for Merveil's ice: radius 10
			("ground_effects/VisibleServerGroundEffect", "ground_devouring_darkness", 10, DangerLayer.Now, 30),
			// Augury league boss empowerment "Orbital Viscera Cannon" (3.30, seen on Kitava): a Daemon monster
			// (Monsters/Augury/Daemon/OrbitalVisceraCannonDaemon_, sitting on the boss, StateMachine beam_begin/tier) fires a
			// Metadata/Effects/ServerEffect (LimitedLifespan) whose animated object is
			// .../League_Augury/Boss_Empowerments/OrbitalVisceraBeam/viscera_beam.ao. Render.Bounds 19.6 x 19.6 like the
			// geysers; 12 cells is a guess at the blast
			("OrbitalVisceraBeam/viscera_beam.ao", null, 12, DangerLayer.Soon, 60),
			// its marker, before the beam lands (3.30.0c, on the Nightmare Manifest: a Metadata/Effects/ServerEffect whose
			// animated object is .../OrbitalVisceraBeam/viscera_beam_marker.ao, 1.7 s old): where the beam will hit, the same 12
			("OrbitalVisceraBeam/viscera_beam_marker.ao", null, 12, DangerLayer.Soon, 60),
			// Nightmare Manifest's orbs (PietyCorruptedBallLightningMaps, see AimedSkills; 3.30.0c, paused with 4 out): projectiles
			// Metadata/Projectiles/CorruptedBallLightning (.../act4/piety/sticky_orb/travel/orb_travel.ao), one at the cast's
			// aimed cell, two where the user saw two big lightning orbs on screen. Not among the entities: see
			// EntityCache.GetProjectiles. On the screenshot an orb is about 20 cells across: radius 14 with slack
			("Projectiles/CorruptedBallLightning", null, 14, DangerLayer.Now, 80),
			// Augury empowerment "Blackfire Wall" (3.30, seen on Kitava: a wave of fire that leaves the player burning with
			// Blackfire). Monsters/Augury/Daemon/BlackfireWallDaemon (sits on the boss, StateMachine have_wall, tier) lays a
			// line of Metadata/Monsters/Daemon/Augury/BlackfireWallObject segments (Animated, BaseEvents, Buffs, Positioned,
			// Render, StateMachine aura=1, tier), animated object .../Boss_Empowerments/BlackfireWalls/blackfire_wall.ao,
			// each with the buff blackfire_wall_daemon_burn, which the player standing in it gets too: the segments are the
			// source of Blackfire. Paused mid-deploy: 3 segments about 10 cells apart, Render.Bounds 30 x 30 (about 3
			// cells), the last one on the player. Radius 6 so the circles join into the wall
			("Daemon/Augury/BlackfireWallObject", null, 6, DangerLayer.Now, 50),
			// Augury empowerment "Crow Bombs" (3.30, seen on Kitava alongside the wall, not seen exploding yet):
			// Monsters/Augury/Daemon/CrowBombDaemon places Metadata/Monsters/Daemon/Augury/CrowBombObject (Animated,
			// BaseEvents, Positioned, Render, StateMachine spawn=1), animated object .../Boss_Empowerments/CrowBombs/crow_bomb.ao,
			// Render.Bounds 30 x 30. 10 cells is a guess at the blast
			("Daemon/Augury/CrowBombObject", null, 10, DangerLayer.Soon, 40),
			// Augury Darktongue Matron's pustules (Monsters/Augury/CauldronCrone, a rare one; 3.30.0c, paused as the user saw
			// "a line of ground orbs starting at the player's feet"): Metadata/Monsters/LeagueAugury/CauldronCrone/
			// CauldronCronePustule (Animated, BaseEvents, Brackets, Positioned, Render, StateMachine pop=1), animated object
			// .../League_Augury/CauldronCrone/cauldron_pustule_02.ao. Four in a line from the player outward, about 16
			// cells apart, placed 0.13 s after each other; Render.Bounds 30 x 30 (3 cells). Not seen popping: radius 8 is
			// a guess (about half the spacing, so they join into a strip). Soon until they pop (see HazardNowStates)
			("LeagueAugury/CauldronCrone/CauldronCronePustule", null, 8, DangerLayer.Soon, 60),
			// Augury empowerment "Blood Geysers" (3.30.0c, seen on Renkarr in the Silo map, paused, not seen erupting yet):
			// Monsters/Augury/Daemon/TriggeredBloodGeyserDaemon (sits on the boss) places
			// Metadata/Monsters/Daemon/Augury/TriggeredBloodGeysersObject (Animated, BaseEvents, Positioned, Render,
			// StateMachine hit_count=0, tier), animated object .../Boss_Empowerments/BloodGeysers/blood_geyser_grd.ao, under
			// the player (1 cell away, 0.66 s old). Taken as about to erupt: 10 cells is a guess
			("Daemon/Augury/TriggeredBloodGeysersObject", null, 10, DangerLayer.Soon, 50),
			// Daresso's sword traps in The Ring of Blades (Act 4; the name from the user, not seen live yet: 8 cells is a guess)
			("DaressoSwordTrap", null, 8, DangerLayer.Now, 40),
		};

		/// <summary>
		/// This fight's own damaging areas, beyond the catalog (eg, PietyKill's circles): put on the DangerMap (see
		/// DangerMap.SetExtra) while it fights. Null: none.
		/// </summary>
		public Func<IEnumerable<Hazard>> ExtraHazards;

		/// <summary>Stay this many cells further out than an area's radius (PietyKill's own beam; the DangerMap has its own Margin).</summary>
		public float Margin = 4;
		/// <summary>How far from the player (cells) to look for a safe cell (PietyKill's own search).</summary>
		public int SearchRadius = 60;
		/// <summary>How far (cells) the DangerMap search for a safe cell may go.</summary>
		public int FarSearchRadius = 150;
		/// <summary>Keep walking to the cell chosen before while its score is within this many (cells walked) of the best now.</summary>
		public float GoalHysteresis = 8;
		/// <summary>Forget the cell chosen before after this long.</summary>
		public long GoalKeepMs = 2000;
		/// <summary>Range to use the skill from, in cells; 0 for 80% of the driver's AttackRange.</summary>
		public float SpamRange;

		public AvoidSpam(string label, Func<Entity, bool> match, Func<string> skill) : base(label, match, skill) { }

		public override string Name => $"Kill {Label} with {Skill}, out of its damaging areas";

		/// <summary>Log the damaging areas (how many, and the nearest) at most this often, when their number has changed.</summary>
		public long HazardLogMs = 5000;
		private int lastHazardCount = -1;
		private long lastHazardLog;
		// the cell walked to last (to keep it, see GoalHysteresis), and when it was chosen
		private Offsets.Vector2i goal;
		private long goalAt = -1000000;
		// the last search (see TryFindGoal): on which map, from where, at what target, and whether it found a safe cell
		private int searchedMap = -1;
		private Offsets.Vector2i searchedFrom, searchedTarget;
		private bool searchedSafe;

		/// <summary>The cell some AvoidSpam last chose to walk to (out of danger, or safe in range), and when (for DangerDisplay).</summary>
		public static Offsets.Vector2i LastGoal { get; private set; }
		public static long LastGoalAt { get; private set; } = -1000000;

		/// <summary>
		/// Every catalog hazard now (from one walk over the entities): the BeamSkills beams, the HazardSkills being used, and
		/// the entities matching HazardPaths. DangerMap builds from these.
		/// </summary>
		public static IEnumerable<Hazard> CatalogHazards() {
			var ents = (GetEntities() ?? Enumerable.Empty<Entity>()).ToList();
			foreach ( var h in BeamHazards(ents) ) yield return h;
			foreach ( var h in SkillHazards(ents) ) yield return h;
			foreach ( var h in PathHazards(ents) ) yield return h;
			foreach ( var h in BuffAreaHazards(ents) ) yield return h;
			// (projectiles are not among the entities, see EntityCache.GetProjectiles)
			foreach ( var h in PathHazards(EntityCache.GetProjectiles().ToList()) ) yield return h;
		}

		/// <summary>The entities in ents matching HazardPaths.</summary>
		/// <summary>
		/// HazardPaths entries (by their path part) whose entities stay in the entity list after they are gone: the most
		/// seconds one lasts, by its animated object's clock (ClientAnimationController.TimeSpentAnimating, which keeps
		/// running after it is gone); an older one is not a hazard.
		/// </summary>
		public static readonly Dictionary<string, float> HazardMaxAgeSec = new Dictionary<string, float> {
			// the Nightmare Manifest's orbs (3.30.0c): paused after 2 s of fighting, the user saw only the two newest, one
			// flying (0.2 s old) and one landed (1.9 s); the next was 5.2 s old and gone. Nothing else told them apart (their
			// Projectile, Animated, Positioned and Render matched once landed), and the gone ones stayed listed for 45 s and more
			["Projectiles/CorruptedBallLightning"] = 5,
		};

		/// <summary>
		/// HazardPaths entries (by their path part) that go on the Now layer, whatever their entry says, while their
		/// StateMachine has a state at a value: eg, a blast that is Soon while it winds up and Now once it goes off.
		/// </summary>
		public static readonly Dictionary<string, (string state, long value)> HazardNowStates = new Dictionary<string, (string, long)> {
			// the Darktongue Matron's pustules: pop is 0 while they wait, 1 when they go off (from the user)
			["LeagueAugury/CauldronCrone/CauldronCronePustule"] = ("pop", 1),
		};

		// the animated object's clock (s), or NaN
		private static float AnimationAge(Entity e) {
			var anim = e.GetComponent<Animated>();
			var cac = IsValid(anim) ? anim.AnimatedObject?.GetComponent<ClientAnimationController>() : null;
			return IsValid(cac) ? cac.TimeSpentAnimating : float.NaN;
		}

		public static IEnumerable<Hazard> PathHazards(List<Entity> ents) {
			if ( HazardPaths.Count == 0 ) yield break;
			foreach ( var e in ents ) {
				string path = e?.Path;
				if ( path == null ) continue;
				// effects with a generic metadata path (eg, SleepableServerEffect) are told apart by their animated object
				string aoPath = path.StartsWith("Metadata/Effects/") ? e.GetComponent<Animated>()?.AnimatedObjectPath : null;
				foreach ( var (part, buff, radius, layer, weight) in HazardPaths ) {
					if ( !(path.Contains(part) || (aoPath != null && aoPath.Contains(part))) || (buff != null && !HasBuff(e, buff)) ) continue;
					if ( HazardMaxAgeSec.TryGetValue(part, out float maxAge) && !(AnimationAge(e) <= maxAge) ) break; // (gone; or no clock, taken as gone)
					var pos = e.GetComponent<Positioned>();
					var at = layer;
					if ( HazardNowStates.TryGetValue(part, out var now) && (e.GetComponent<StateMachine>()?.TryGetState(now.state, out long v) ?? false) && v == now.value ) at = DangerLayer.Now;
					if ( IsValid(pos) ) yield return new Hazard(pos.GridPos, radius) { Layer = at, Weight = weight, Source = buff ?? part };
					break;
				}
			}
		}

		protected override void Fight(CombatContext ctx, Hostile target) {
			// life low: retreat first (as Fight does between targets)
			if ( ctx.Driver.TryRetreat(ctx) ) { Detail = "retreating"; return; }
			string skill = Skill;
			float range = SpamRange > 0 ? SpamRange : ctx.Driver.AttackRange * .8f;
			DangerMap.SetExtra(this, ExtraHazards);
			var map = DangerMap.Get();
			if ( map == null ) { Detail = "no danger map (no terrain?)"; base.Fight(ctx, target); return; }
			var hazards = map.Hazards;
			// (no line for "0 areas" until there were some: it runs for every monster fought)
			if ( hazards.Count != Math.Max(0, lastHazardCount) && Time.ElapsedMilliseconds - lastHazardLog >= HazardLogMs ) {
				lastHazardCount = hazards.Count;
				lastHazardLog = Time.ElapsedMilliseconds;
				string nearest = hazards.Count == 0 ? "" : $", the nearest {hazards.Min(h => h.EdgeDistance(ctx.Grid)):F0} cells from its edge";
				Log($"AvoidSpam: {hazards.Count} damaging areas near {Label}{nearest}");
			}
			bool Good(Offsets.Vector2i c) => Combat.Distance(c, target.GridPos) <= range && ctx.Terrain.HasLineOfSight(c, target.GridPos);

			// in danger (an area, or its margin): get out first, to the best cell (see DangerMap.TryFindSafest)
			int here = map.IsBlocked(ctx.Grid) ? map.SoonAt(ctx.Grid) : map.Total(ctx.Grid); // (the grid may block the player's own cell, near a wall)
			if ( here >= DangerMap.SafeLevel ) {
				bool safe = TryFindGoal(ctx, map, Good, target.GridPos, out var cell);
				if ( cell.Id != ctx.Grid.Id && (safe || map.Total(cell) < here) ) {
					Detail = safe ? $"stepping out of a damaging area, to {cell}" : $"in a damaging area, nowhere safe near: to the least bad cell, {cell}";
					ctx.Driver.WalkTo(cell, $"avoiding {Label}'s damaging area");
				} else {
					Detail = "in a damaging area, with nowhere safe near";
					base.Fight(ctx, target);
				}
				return;
			}

			// out of range or sight: get in range; with hazards about, to the best safe cell in range (if there is one near)
			bool inRange = Good(ctx.Grid);
			if ( !inRange || !TryWorldToScreen(target.WorldPos, out Vector2 screen) || !PathFollower.IsSafeToClick(screen) ) {
				Detail = $"getting in range, {target.Distance:F0} cells away";
				if ( !inRange && hazards.Count > 0 && TryFindGoal(ctx, map, Good, target.GridPos, out var cell) && Good(cell) ) {
					Detail = $"getting in range, {target.Distance:F0} cells away, to a safe cell {cell}";
					ctx.Driver.WalkTo(cell, $"getting in range of {Label}, out of its damaging areas");
					return;
				}
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
		/// The best cell to stand on (DangerMap.TryFindSafest, good: in range of the target with line of sight), keeping the
		/// one chosen before while it scores within GoalHysteresis of the best. True if it is safe.
		/// </summary>
		private bool TryFindGoal(CombatContext ctx, DangerMap map, Func<Offsets.Vector2i, bool> good, Offsets.Vector2i target, out Offsets.Vector2i cell) {
			long now = Time.ElapsedMilliseconds;
			// (the same map, from the same cell, at the same target: the same answer; the search runs at most once per map)
			if ( map.Number == searchedMap && ctx.Grid.Id == searchedFrom.Id && target.Id == searchedTarget.Id ) {
				cell = goal;
				goalAt = now;
				return searchedSafe;
			}
			bool safe = map.TryFindSafest(ctx.Grid, good, out cell, out float score, FarSearchRadius);
			searchedMap = map.Number;
			searchedFrom = ctx.Grid;
			searchedTarget = target;
			searchedSafe = safe;
			if ( now - goalAt < GoalKeepMs && goal.Id != cell.Id && map.IsSafe(goal) == safe
				&& map.Score(goal, good(goal)) <= score + GoalHysteresis ) {
				cell = goal;
			} else if ( goal.Id != cell.Id ) {
				goal = cell;
			}
			goalAt = now;
			LastGoal = cell;
			LastGoalAt = now;
			return safe;
		}
	}
}
