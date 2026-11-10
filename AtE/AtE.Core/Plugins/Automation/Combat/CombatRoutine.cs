using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A combat driver that follows a script: for each area, a list of objectives (see Objective) to do in order.
	/// While the driver is on, it works on the first objective that is not complete. In an area with no objectives, or
	/// once they are all complete, it does what CombatDriver does (fight, explore, stop when clear).
	///
	/// The current area's objectives are drawn on screen, marked done / current / to do.
	/// A routine for a class calls Area(...) in its constructor, eg:
	///   Area("The Twilight Strand", new PickUp("Driftwood Wand"), new Kill("Hillock", "Hillock"), new GoToArea("Lioneye's Watch"));
	/// </summary>
	public abstract class CombatRoutine : CombatDriver {

		private readonly List<(string area, List<Objective> objectives)> areas = new List<(string, List<Objective>)>();
		private uint areaHash;

		public bool ShowObjectives = true;
		/// <summary>Pick up the items that show a label on the ground (see Looter), when no monster is within AttackRange.</summary>
		public bool PickUpItems = true;
		protected readonly Looter Looter = new Looter();

		protected CombatRoutine() =>
			// an item an objective is to pick up is left to it, so it sees the item go and knows it is done
			Looter.Leave = text => CurrentObjectives?.OfType<PickUp>().Any(p => !p.IsComplete
				&& text.IndexOf(p.LabelText, StringComparison.OrdinalIgnoreCase) >= 0) ?? false;

		/// <summary>The objectives for an area, by its name ("The Twilight Strand"), its name with act ("The Coast (Act 1)") or its id ("1_1_1").</summary>
		protected void Area(string area, params Objective[] objectives) => areas.Add((area, objectives.ToList()));

		/// <summary>The objectives for the current area, or null if this routine has none for it.</summary>
		public List<Objective> CurrentObjectives {
			get {
				var here = GetCurrentArea();
				if ( here == null ) return null;
				return areas.FirstOrDefault(a =>
					string.Equals(a.area, here.Name, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(a.area, here.DisplayName, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(a.area, here.Id, StringComparison.OrdinalIgnoreCase)).objectives;
			}
		}

		/// <summary>The objective being worked on now, or null.</summary>
		public Objective Current { get; private set; }

		public override void Render() {
			base.Render();
			ImGuiNET.ImGui.Checkbox("Show Objectives", ref ShowObjectives);
			ImGuiNET.ImGui.Checkbox("Pick Up Items", ref PickUpItems);
		}

		public override IState OnTick(long dt) {
			if ( PoEMemory.IsAttached ) {
				ForgetOnNewArea();
				if ( !IsActive ) UpdateCompletion(); // (while on, Decide does this)
				if ( ShowObjectives ) DrawObjectives();
			}
			return base.OnTick(dt);
		}

		// a new area instance (even the same area again) starts the objectives over
		private void ForgetOnNewArea() {
			uint hash = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			if ( hash == areaHash ) return;
			areaHash = hash;
			Current = null;
			lastProven = -1;
			lastProvenCheck = 0;
			Looter.Reset();
			foreach ( var area in areas ) foreach ( var o in area.objectives ) o.Reset();
		}

		// mark the objectives that are done, up to the first one that is not (which becomes Current)
		private void UpdateCompletion(CombatContext ctx) {
			var objectives = CurrentObjectives;
			Current = null;
			if ( objectives == null ) return;
			int proven = LastProven(objectives, ctx);
			for ( int i = 0; i < objectives.Count; i++ ) {
				var o = objectives[i];
				if ( i <= proven && o.ProvenBy(objectives.Skip(i + 1).Take(proven - i)) ) {
					// it, or one after it, is done now: so it was done (eg, Default Attack is off slot 3 because Fireball is on it)
					if ( !o.IsComplete ) {
						o.IsComplete = true;
						o.Detail = "";
						Log($"{Name}: done: {o.Name}");
					}
					continue;
				}
				if ( o.IsComplete ) {
					if ( o.Sticky || o.IsDone(ctx) ) continue;
					o.IsComplete = false; // undone (eg, the skill was taken off the bar): do it again
					Log($"{Name}: not done any more: {o.Name}");
				}
				if ( o.IsDone(ctx) ) {
					o.IsComplete = true;
					o.Detail = "";
					Log($"{Name}: done: {o.Name}");
					continue;
				}
				Current = o;
				break;
			}
		}

		// the last objective that is done now (see Objective.ProvesEarlier), or -1; asked every 250 ms
		private int lastProven = -1;
		private long lastProvenCheck;
		private int LastProven(List<Objective> objectives, CombatContext ctx) {
			if ( Time.ElapsedMilliseconds - lastProvenCheck < 250 ) return Math.Min(lastProven, objectives.Count - 1);
			lastProvenCheck = Time.ElapsedMilliseconds;
			lastProven = -1;
			for ( int i = objectives.Count - 1; i >= 0; i-- ) {
				if ( objectives[i].ProvesEarlier && objectives[i].IsDone(ctx) ) {
					lastProven = i;
					break;
				}
			}
			return lastProven;
		}

		private long lastCompletionCheck;
		private void UpdateCompletion() {
			if ( Time.ElapsedMilliseconds - lastCompletionCheck < 250 ) return;
			lastCompletionCheck = Time.ElapsedMilliseconds;
			var game = PoEMemory.GameRoot?.InGameState;
			var terrain = GetTerrain();
			var player = GetPlayer();
			var pos = player?.GetComponent<Positioned>();
			if ( game == null || terrain == null || !IsValid(pos) ) return;
			UpdateCompletion(new CombatContext(this, game, terrain, player, pos.GridPos, Position(player).Z));
		}

		protected override void Decide(CombatContext ctx) {
			UpdateCompletion(ctx);
			if ( Current == null ) {
				if ( !TryLoot(ctx) ) base.Decide(ctx);
				return;
			}
			SetStatus(Current.Name);
			if ( !Current.UsesInventory && BackpackIsOpen() ) {
				// eg, left open after socketing a gem: it covers the screen
				ctx.Driver.StopWalking("closing the inventory");
				if ( Time.ElapsedMilliseconds - lastInventoryClose > 700 ) {
					lastInventoryClose = Time.ElapsedMilliseconds;
					SocketGem.PressKey(VKey.I);
				}
				return;
			}
			if ( !Current.UsesInventory && TryLoot(ctx) ) return;
			Current.Tick(ctx);
			if ( !string.IsNullOrEmpty(Current.Detail) ) SetStatus($"{Current.Name}: {Current.Detail}");
		}

		private long lastInventoryClose;

		// pick up an item, if there is one; true if doing so, or fighting the monsters near it (or near the player) first.
		// (so an item comes before walking on to the next objective: walking away from the monsters, then coming back
		// for the item, back and forth, never gets it)
		private bool TryLoot(CombatContext ctx) {
			if ( !PickUpItems || BackpackIsOpen() || !Looter.TryGetTarget(ctx, out var item) ) return false;
			var near = ctx.Hostiles.Where(h => h.Distance <= AttackRange || Combat.Distance(h.GridPos, item) <= AttackRange).ToList();
			if ( near.Count > 0 ) {
				if ( Current is Kill ) return false; // (it fights them its own way, eg, Orbit)
				var target = ChooseTarget(near);
				if ( target != null && Engage(ctx, target) ) {
					SetStatus($"fighting {target} before picking up an item");
					return true;
				}
				return false;
			}
			if ( !Looter.TryLoot(ctx) ) return false;
			SetStatus(Looter.Status);
			return true;
		}

		private void DrawObjectives() {
			var objectives = CurrentObjectives;
			if ( objectives == null ) return;
			var size = WindowSize.Value;
			var pos = new Vector2(size.X * 0.30f, size.Y * 0.08f); // top middle-left: clear of the console, minimap and quest tracker
			const float line = 16f;
			DrawTextAt(pos, $"{Name}: {GetCurrentArea()?.DisplayName}{(IsActive ? "" : " (off)")}", Color.Orange);
			pos.Y += line;
			bool foundCurrent = false;
			foreach ( var o in objectives ) {
				string mark;
				Color color;
				if ( o.IsComplete ) { mark = "[x]"; color = Color.LightGreen; }
				else if ( !foundCurrent ) { mark = "[>]"; color = Color.Yellow; foundCurrent = true; }
				else { mark = "[ ]"; color = Color.Gray; }
				string detail = !o.IsComplete && o == Current && !string.IsNullOrEmpty(o.Detail) ? $" - {o.Detail}" : "";
				DrawTextAt(pos, $"{mark} {o.Name}{detail}", color);
				pos.Y += line;
			}
		}
	}

	/// <summary>
	/// A routine to try the objectives with: for The Twilight Strand, clear the area, then go to Lioneye's Watch.
	/// </summary>
	public class TestRoutine : CombatRoutine {
		public override string Name => "Test Routine";
		public string AttackSkill = "Fireball";
		public TestRoutine() {
			Area("The Twilight Strand",
				new BindSkill("Fireball"),
				new Kill("Hillock", "Hillock", "Fireball"),
				new GoToArea("Lioneye's Watch"));
		}
		protected override void Attack(Hostile target, Vector2 screenPos) => UseSkillPaced(AttackSkill, screenPos);
	}
}
