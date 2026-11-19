using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// An arena encounter that shuts the player in: doors that were open close around the player, waves of monsters come
	/// out, and the doors open again once the last wave is dead.
	///
	/// 3.30, seen live in a pit arena (Metadata/Terrain/Mountain/Arena/Pit): two PitGates (TriggerableBlockage) closed as
	/// the encounter started, their box set only then (see AreaDoors). The monsters of every wave are there from the
	/// start, waiting in four pits (PitSpawnerGate) with Transitionable.State 2, and are targetable already; a wave out
	/// has State 3. The next wave appears in the pits as the one before comes out. After the last wave (48 monsters, 4
	/// waves of 12) the gates opened again (IsClosed 0, State back to 1; their box stays set), and a PortalTransition, a
	/// ServerDoodad and a WorldItem appeared.
	///
	/// Told generically by the doors: one seen open that has closed again (AreaDoor.ClosedSince), near the player.
	/// Doors closed when first seen (an ordinary door) never count. Not told if the overlay started while already locked in.
	/// </summary>
	public static class ArenaLock {
		/// <summary>Doors closed again farther than this from the player (in cells) do not lock it in.</summary>
		public static float Radius = 150f;

		/// <summary>The doors that have closed again near the player (empty if it is not locked in).</summary>
		public static List<AreaDoor> LockingDoors(Terrain terrain, Offsets.Vector2i at) =>
			terrain?.Doors.Where(d => d.IsClosed && !d.PassThrough && d.ClosedSince != 0 && Combat.Distance(at, d.GridPos) <= Radius).ToList()
			?? new List<AreaDoor>();

		public static bool IsLockedIn(CombatContext ctx) => LockingDoors(ctx.Terrain, ctx.Grid).Count > 0;
	}

	/// <summary>
	/// While locked in an arena (ArenaLock): fight what comes (the plain Fight); with nothing to fight, go back to where
	/// the player was when the doors closed (inside the arena) and wait for the next wave. Done when the doors open.
	///
	/// Not part of the goal tree: the campaign puts it in place of the goal it would work on while locked in (see
	/// CampaignRoutine.Select), unless that goal fights here itself (a Kill, eg a boss with AvoidSpam, or FightUntil).
	/// </summary>
	public class ArenaLockdown : Goal {
		private readonly System.Func<string> skillOf;
		/// <summary>How near the waiting spot to wait, in cells.</summary>
		public float Reach = 10;
		private Offsets.Vector2i? waitAt;

		public ArenaLockdown(System.Func<string> skill = null) { skillOf = skill; }
		public override string Name => "Arena lockdown";
		public override bool Sticky => false;

		public override bool CheckDone(CombatContext ctx) {
			bool locked = ArenaLock.IsLockedIn(ctx);
			if ( !locked && waitAt.HasValue ) {
				Log($"{Name}: the doors are open again");
				waitAt = null;
			}
			return !locked;
		}

		public override void Reset() {
			base.Reset();
			waitAt = null;
		}

		protected override IState Step(CombatContext ctx) {
			var doors = ArenaLock.LockingDoors(ctx.Terrain, ctx.Grid);
			if ( !waitAt.HasValue ) {
				waitAt = ctx.Grid;
				Log($"{Name}: locked in by {string.Join(", ", doors.Select(d => $"door {d.EntityId} at {d.GridPos}"))}: fighting the waves from {ctx.Grid}");
			}
			if ( FightFirst(ctx, 0, skillOf?.Invoke()) is IState fight ) { Detail = $"fighting ({ctx.Hostiles.Count} near)"; return fight; }
			if ( !IsNear(ctx, waitAt.Value, Reach, out _, out float d) ) {
				Detail = $"no hostiles: back to {waitAt.Value}, {d:F0} cells away";
				return Call(new Navigate(Name, c => Approach(c, waitAt.Value), Reach) { FightRadius = 0, LegMs = 1000 });
			}
			ctx.Driver.StopWalking("waiting for the next wave");
			Detail = $"no hostiles: waiting for the next wave ({doors.Count} doors closed)";
			return this;
		}
	}
}
