using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Kill Piety in The Bowels of the Beast (The Belly of the Beast Level 2, Act 4): AvoidSpam (stay out of the circles she
	/// summons, see CircleParts), and, while her blood beam is out, nothing but keeping out of its way: it swings around
	/// her, so it is tracked from frame to frame (its angle, and which way and how fast it turns) and the player walks to the
	/// nearest cell it will not sweep over soon (preferring one behind it), using no skill until it is gone.
	///
	/// The beam is an entity with a Beam component (Start, End in world units) within BeamRange of her, or one whose path
	/// (or animated object's .ao path) has one of BeamParts (then it is taken as a ray from her through it, BeamLength long).
	/// TODO: confirm live (not seen yet): the circles' and the beam's entities. Each fight logs the effect entities seen near
	/// her (once per path, see LogEffects), so they can be named in CircleParts and BeamParts.
	/// </summary>
	public class PietyKill : AvoidSpam {
		/// <summary>
		/// Her circles: (a part of the path or .ao path, radius in cells). TODO: none seen live yet: name them from the
		/// fight's log (LogEffects). Left empty rather than guessed: a guess that matched an effect on her own spot would keep
		/// the player away from her for good.
		/// </summary>
		public static readonly List<(string pathPart, float radius)> CircleParts = new List<(string, float)>();
		/// <summary>Parts of the path (or .ao path) of her beam's entity, when it has no Beam component. Guesses until seen live.</summary>
		public static readonly List<string> BeamParts = new List<string> { "Beam", "Laser" };

		/// <summary>How far from her (cells) a Beam entity may start to be hers.</summary>
		public float BeamRange = 30;
		/// <summary>The beam's length (cells), when it is told by a path part (no Beam component to read it from).</summary>
		public float BeamLength = 70;
		/// <summary>The beam's half width, in cells (plus Margin).</summary>
		public float BeamWidth = 4;
		/// <summary>How far ahead (ms) of where the beam is to keep out of, as it turns.</summary>
		public long LookaheadMs = 1200;
		/// <summary>Not knowing which way it turns yet: keep out of this many degrees on each side of it.</summary>
		public float UnknownSweepDegrees = 60;
		/// <summary>The beam not seen for this long: it is over.</summary>
		public long BeamGoneMs = 400;

		private float lastAngle;
		private long lastAngleAt, beamSeenAt;
		private float turnRate; // radians per second, signed (smoothed); 0: not known
		private bool hadBeam;

		public PietyKill(string label, Func<Entity, bool> match, Func<string> skill) : base(label, match, skill) {
			ExtraHazards = Circles;
		}

		public override string Name => $"Kill {Label}{(string.IsNullOrEmpty(Skill) ? "" : $" with {Skill}")}, out of her circles and her beam";
		public override void Reset() { base.Reset(); hadBeam = false; turnRate = 0; lastAngleAt = beamSeenAt = 0; }

		private static IEnumerable<Hazard> Circles() {
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				string path = e?.Path;
				if ( path == null || path.StartsWith("Metadata/Monsters/") ) continue; // (not her, nor her minions)
				string ao = path.StartsWith("Metadata/Effects/") ? e.GetComponent<Animated>()?.AnimatedObjectPath : null;
				foreach ( var (part, radius) in CircleParts ) {
					if ( !(path.Contains(part, StringComparison.OrdinalIgnoreCase) || (ao?.Contains(part, StringComparison.OrdinalIgnoreCase) ?? false)) ) continue;
					var pos = e.GetComponent<Positioned>();
					if ( IsValid(pos) ) yield return new Hazard(pos.GridPos, radius);
					break;
				}
			}
		}

		/// <summary>Her beam now, as a segment in grid cells (from, to), if it is out.</summary>
		private bool TryBeam(Offsets.Vector2i piety, out Vector2 from, out Vector2 to) {
			from = to = default;
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				if ( e?.Path == null ) continue;
				var beam = e.GetComponent<Beam>();
				if ( IsValid(beam) && beam.Start != beam.End ) {
					var a = WorldToGrid(beam.Start);
					var b = WorldToGrid(beam.End);
					if ( Combat.Distance(a, piety) > BeamRange && Combat.Distance(b, piety) > BeamRange ) continue;
					// (from her end)
					if ( Combat.Distance(b, piety) < Combat.Distance(a, piety) ) (a, b) = (b, a);
					from = new Vector2(a.X, a.Y);
					to = new Vector2(b.X, b.Y);
					if ( Vector2.Distance(from, to) < 3 ) continue;
					return true;
				}
				string path = e.Path;
				string ao = path.StartsWith("Metadata/Effects/") ? e.GetComponent<Animated>()?.AnimatedObjectPath : null;
				if ( !BeamParts.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase) || (ao?.Contains(part, StringComparison.OrdinalIgnoreCase) ?? false)) ) continue;
				var pos = e.GetComponent<Positioned>();
				if ( !IsValid(pos) || Combat.Distance(pos.GridPos, piety) > BeamLength ) continue;
				from = new Vector2(piety.X, piety.Y);
				var dir = new Vector2(pos.GridPos.X - piety.X, pos.GridPos.Y - piety.Y);
				if ( dir.LengthSquared() < 1 ) continue;
				to = from + (Vector2.Normalize(dir) * BeamLength);
				return true;
			}
			return false;
		}

		protected override void Fight(CombatContext ctx, Hostile target) {
			LogEffects(ctx, target.GridPos);
			long now = Time.ElapsedMilliseconds;
			if ( TryBeam(target.GridPos, out var from, out var to) ) {
				float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X);
				if ( hadBeam && lastAngleAt > 0 && now > lastAngleAt ) {
					float rate = Wrap(angle - lastAngle) * 1000f / (now - lastAngleAt);
					turnRate = turnRate == 0 ? rate : (turnRate * .7f) + (rate * .3f);
				}
				if ( !hadBeam ) Log($"PietyKill: her beam is out, from {from} to {to}");
				hadBeam = true;
				lastAngle = angle;
				lastAngleAt = beamSeenAt = now;
				DodgeBeam(ctx, from, to, angle);
				return;
			}
			if ( hadBeam && now - beamSeenAt < BeamGoneMs ) {
				// (a frame without it: keep away from where it was)
				ctx.Driver.StopWalking("waiting for her beam to show again");
				return;
			}
			if ( hadBeam ) Log("PietyKill: her beam is over");
			hadBeam = false;
			turnRate = 0;
			base.Fight(ctx, target);
		}

		private static float Wrap(float a) {
			while ( a > MathF.PI ) a -= 2 * MathF.PI;
			while ( a < -MathF.PI ) a += 2 * MathF.PI;
			return a;
		}

		/// <summary>Will the beam (from, angle, length) sweep over cell within LookaheadMs (or is it on it now)?</summary>
		private bool InBeamsWay(Vector2 from, float angle, float length, Vector2 cell) {
			var d = cell - from;
			float r = d.Length();
			if ( r > length + Margin ) return false;
			if ( r <= BeamWidth + Margin ) return true; // (at her feet, where it starts)
			float delta = Wrap(MathF.Atan2(d.Y, d.X) - angle);
			float widthAngle = MathF.Atan((BeamWidth + Margin) / r);
			if ( turnRate == 0 ) return MathF.Abs(delta) <= (UnknownSweepDegrees * MathF.PI / 180f) + widthAngle;
			float sweep = turnRate * LookaheadMs / 1000f; // signed: where it gets to
			float lo = Math.Min(0, sweep) - widthAngle, hi = Math.Max(0, sweep) + widthAngle;
			return delta >= lo && delta <= hi;
		}

		private void DodgeBeam(CombatContext ctx, Vector2 from, Vector2 to, float angle) {
			float length = Vector2.Distance(from, to);
			var me = new Vector2(ctx.Grid.X, ctx.Grid.Y);
			var circles = Circles().ToList();
			if ( !InBeamsWay(from, angle, length, me) && !circles.Any(h => h.Covers(ctx.Grid, Margin)) ) {
				ctx.Driver.StopWalking("out of her beam's way");
				Detail = $"her beam is out (turning {turnRate * 180 / MathF.PI:F0} deg/s): out of its way, waiting";
				return;
			}
			// the nearest cell out of its way (and out of the circles), preferring one behind it (where it has been)
			Offsets.Vector2i best = default;
			float bestScore = float.MaxValue;
			for ( int r = 3; r <= SearchRadius; r += 3 ) {
				int steps = Math.Max(8, r);
				for ( int i = 0; i < steps; i++ ) {
					double a = 2 * Math.PI * i / steps;
					var cell = new Offsets.Vector2i(ctx.Grid.X + (int)Math.Round(r * Math.Cos(a)), ctx.Grid.Y + (int)Math.Round(r * Math.Sin(a)));
					if ( !ctx.Terrain.IsWalkable(cell) || circles.Any(h => h.Covers(cell, Margin)) ) continue;
					var c = new Vector2(cell.X, cell.Y);
					if ( InBeamsWay(from, angle, length, c) ) continue;
					if ( !DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, cell) ) continue;
					float delta = Wrap(MathF.Atan2(c.Y - from.Y, c.X - from.X) - angle);
					bool behind = turnRate != 0 && Math.Sign(delta) == -Math.Sign(turnRate);
					float score = r + (behind ? 0 : 6);
					if ( score < bestScore ) { bestScore = score; best = cell; }
				}
				if ( bestScore < r ) break; // (one behind it, as near as any can be)
			}
			if ( bestScore == float.MaxValue ) {
				Detail = "in her beam's way, with nowhere safe near: away from her";
				var away = me - from;
				if ( away.LengthSquared() < 1 ) away = new Vector2(1, 0);
				var spot = me + (Vector2.Normalize(away) * 15);
				ctx.Driver.WalkTo(Approach(ctx, new Offsets.Vector2i((int)spot.X, (int)spot.Y)), "away from her beam");
				return;
			}
			Detail = $"her beam is out (turning {turnRate * 180 / MathF.PI:F0} deg/s): to {best}";
			ctx.Driver.WalkTo(best, "out of her beam's way");
		}

		// ---- finding out what her effects are ----

		/// <summary>The paths (and .ao paths) of the effect entities seen near her in this fight, each logged once.</summary>
		private readonly HashSet<string> logged = new HashSet<string>();
		private long lastEffectScan;

		/// <summary>
		/// Log each new kind of entity (not a monster, an item or an NPC) within 80 cells of her, with its components of
		/// note (Beam, its .ao path): to name her circles and beam in CircleParts and BeamParts. At most twice a second.
		/// </summary>
		private void LogEffects(CombatContext ctx, Offsets.Vector2i piety) {
			long now = Time.ElapsedMilliseconds;
			if ( now - lastEffectScan < 500 ) return;
			lastEffectScan = now;
			foreach ( var e in GetEntities() ?? Enumerable.Empty<Entity>() ) {
				string path = e?.Path;
				if ( path == null || path.StartsWith("Metadata/Monsters/") || path.StartsWith("Metadata/NPC/") || path.StartsWith("Metadata/Items/") ) continue;
				var pos = e.GetComponent<Positioned>();
				if ( !IsValid(pos) || Combat.Distance(pos.GridPos, piety) > 80 ) continue;
				string ao = e.GetComponent<Animated>()?.AnimatedObjectPath;
				string key = $"{path} {ao}";
				if ( !logged.Add(key) ) continue;
				var beam = e.GetComponent<Beam>();
				string beamText = IsValid(beam) && beam.Start != beam.End ? $", Beam {WorldToGrid(beam.Start)} -> {WorldToGrid(beam.End)}" : "";
				Log($"PietyKill: near her, at {pos.GridPos} ({Combat.Distance(pos.GridPos, ctx.Grid):F0} from the player): {path}{(ao == null ? "" : $" (ao {ao})")}{beamText}");
			}
		}
	}
}
