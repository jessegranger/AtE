using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	public partial class CampaignRoutine {

		/// <summary>A part of the path of the Act 6 beacon payloads (two per area, each pushed along its own rail).</summary>
		public const string BeaconPayloadPath = "QuestObjects/Act6/BeaconPayload";

		/// <summary>
		/// Is it an Act 6 beacon payload at the end of its rail? Transitionable.State goes 1 -> 2 on the frame it arrives
		/// (3.30, confirmed live), and stays 1 while it is pushed.
		/// </summary>
		public static bool IsBeaconPayloadDone(Entity e) => (e?.GetComponent<Transitionable>()?.State ?? 0) >= 2;

		/// <summary>The nearest Act 6 beacon payload in range (to the player) that is not at the end of its rail yet; null if none.</summary>
		public static Entity FindBeaconPayload(CombatContext ctx) {
			if ( ctx == null ) return null;
			return GetEntities()?
				.Where(e => IsValid(e) && (e.Path?.IndexOf(BeaconPayloadPath, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
				.Select(e => (ent: e, pos: e.GetComponent<Positioned>(), t: e.GetComponent<Transitionable>()))
				.Where(x => IsValid(x.pos) && IsValid(x.t) && x.t.State == 1)
				.OrderBy(x => Combat.Distance(ctx.Grid, x.pos.GridPos))
				.Select(x => x.ent)
				.FirstOrDefault();
		}
	}

	/// <summary>
	/// Push an Act 6 beacon payload (Metadata/QuestObjects/Act6/BeaconPayload) to the end of its rail: it moves while the
	/// player stands in the golden circle around it, about one cell per 100 ms (3.30, seen live: north, then east, then
	/// south, 46 s for one). Takes the nearest one not done yet (CampaignRoutine.FindBeaconPayload) and keeps to it.
	///
	/// Each frame, moving comes first: more than Behind cells from the payload, walk to the cell it moves to next
	/// (Movement.NextStep; its own cell while it has not started); within Behind, fight the hostiles near (one frame of
	/// the driver's fight at a time, so a fight that leads more than Behind away is left for the payload); with none,
	/// walk to that next cell, to stay in the circle.
	///
	/// Done when that payload's Transitionable.State is 2 (at the end of its rail). One goal is one payload: for both, two
	/// goals. With no payload not done in range, it looks for one (SearchAt, else exploring): give it a DoneWhen (eg, from
	/// the quest step) for when both are done already.
	/// </summary>
	public class PushBeaconPayload : Goal {
		/// <summary>How far (cells) the player may be from the payload before walking after it comes first.</summary>
		public float Behind = 20;
		/// <summary>Within this many cells of where it walks to, stand still (no hostiles to fight).</summary>
		public float Reach = 3;
		/// <summary>The skill to fight with, asked when it is used; null uses the driver's Attack.</summary>
		public Func<string> SkillOf;

		private uint payloadId;
		private bool pushed;

		public PushBeaconPayload(Func<string> skill = null) => SkillOf = skill;

		public override string Name => "Push the beacon payload";

		// (a new area instance: new entities)
		public override void Reset() { base.Reset(); payloadId = 0; pushed = false; }

		private Entity Payload(CombatContext ctx) {
			if ( payloadId != 0 ) {
				var known = GetEntityById(payloadId);
				if ( IsValid(known) && (known.Path?.IndexOf(CampaignRoutine.BeaconPayloadPath, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ) return known;
				// (out of range, or gone: take the nearest again)
				payloadId = 0;
			}
			var found = CampaignRoutine.FindBeaconPayload(ctx);
			if ( found != null ) {
				payloadId = found.Id;
				Log($"Campaign: pushing the beacon payload {payloadId} at {found.GetComponent<Positioned>()?.GridPos}");
			}
			return found;
		}

		public override bool CheckDone(CombatContext ctx) {
			if ( pushed ) return true;
			if ( payloadId == 0 || ctx == null ) return false;
			var e = GetEntityById(payloadId);
			if ( !IsValid(e) || !CampaignRoutine.IsBeaconPayloadDone(e) ) return false;
			Log($"Campaign: the beacon payload {payloadId} is at the end of its rail ({e.GetComponent<Positioned>()?.GridPos})");
			return pushed = true;
		}

		protected override IState Step(CombatContext ctx) {
			var payload = Payload(ctx);
			if ( payload == null ) return Search(ctx, "a beacon payload", SkillOf?.Invoke());
			var pos = payload.GetComponent<Positioned>()?.GridPos;
			if ( !pos.HasValue ) { Detail = "the payload has no position"; return this; }
			// where it is going next: its NextStep while it moves; else ((0, 0), or its own cell) where it is
			var next = payload.GetComponent<Movement>()?.NextStep ?? default;
			bool moving = !(next.X == 0 && next.Y == 0) && !(next.X == pos.Value.X && next.Y == pos.Value.Y);
			var to = Approach(ctx, moving ? next : pos.Value);
			float behind = Combat.Distance(ctx.Grid, pos.Value);
			if ( behind > Behind ) {
				Detail = $"{behind:F0} cells behind the payload: to {to}";
				ctx.Driver.WalkTo(to, "catching up with the beacon payload");
				return this;
			}
			if ( !IgnoreMonsters ) {
				if ( ctx.Driver.TryRetreat(ctx) ) { Detail = "retreating"; return this; }
				if ( ctx.Driver.TryFight(ctx, SkillOf?.Invoke()) ) {
					Detail = $"fighting {behind:F0} cells from the payload: {ctx.Driver.Target}";
					return this;
				}
			}
			if ( Combat.Distance(ctx.Grid, to) <= Reach ) {
				ctx.Driver.StopWalking("at the beacon payload");
				Detail = moving ? "with the payload" : "at the payload, waiting for it to move";
				return this;
			}
			Detail = $"{(moving ? "following" : "to")} the payload, {behind:F0} cells away";
			ctx.Driver.WalkTo(to, "following the beacon payload");
			return this;
		}
	}
}
