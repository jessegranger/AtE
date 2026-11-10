using ImGuiNET;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The base for a combat driver: a plugin that clears an area by itself.
	///
	/// Each frame, while switched on (Start, by CombatSection's key): if life is low, retreat to a safe spot; else pick a monster
	/// (ChooseTarget), and if it is in range (with line of sight, if NeedsLineOfSight) Attack it, or else walk to a
	/// spot where it would be; with no monsters around, walk to the next unexplored place; with nothing left, stop.
	///
	/// A driver for a particular build overrides Attack (and usually AttackRange / NeedsLineOfSight / ChooseTarget).
	/// Moving the player and attacking both use the mouse, so this waits while the game is not focused.
	/// </summary>
	public abstract class CombatDriver : AutomationSection {

		/// <summary>Monsters within this many cells are fought before exploring further.</summary>
		public int EngageRadius = 60;
		/// <summary>Retreat (to a spot RetreatDistance from all monsters) when life + energy shield is below this percent; 0 never retreats.</summary>
		public int RetreatBelowLifePercent = 0;
		public int RetreatDistance = 40;

		/// <summary>How close (in cells) the player needs to be to Attack.</summary>
		public virtual float AttackRange => 30f;
		/// <summary>True if attacks need a clear line for projectiles (from the player to the target).</summary>
		public virtual bool NeedsLineOfSight => true;

		/// <summary>The monster to fight next, from the hostiles nearest first; null for none.</summary>
		protected virtual Hostile ChooseTarget(System.Collections.Generic.List<Hostile> hostiles) => hostiles.FirstOrDefault();

		/// <summary>
		/// Attack target, which is in range (and in sight); screenPos is where it is on the screen (safe to click).
		/// Called every frame while it is the target, so throttle as needed.
		/// </summary>
		protected abstract void Attack(Hostile target, Vector2 screenPos);

		public bool IsActive { get; private set; }
		public string Status { get; private set; } = "off";
		public Hostile Target { get; private set; }

		private bool binding;
		private ExploreTarget exploreTarget;
		private long exploreTargetTime;

		public override void Render() {
			base.Render();
			ImGui.SliderInt("Engage Radius (cells)", ref EngageRadius, 10, 120);
			ImGui.SliderInt("Retreat Below Life %", ref RetreatBelowLifePercent, 0, 90);
			ImGui.SliderInt("Retreat Distance (cells)", ref RetreatDistance, 10, 80);
			if ( ImGui.TreeNode("Skill Bar") ) {
				foreach ( var slot in SkillBar.GetSlots() ) {
					int ms = SkillBar.CastTimeMs(slot.Skill);
					ImGui.Text($"{slot}{(ms > 0 ? $" ({ms} ms)" : "")}");
				}
				ImGui.Text($"Available{(SkillBar.IsChoosing ? " (open)" : "")}: {string.Join(", ", SkillBar.GetAvailable())}");
				if ( !SkillBar.LeftClickIsMovement() ) ImGui.TextColored(new Vector4(1, .6f, .2f, 1), "Left click is not Movement: walking may attack");
				ImGui.TreePop();
			}
		}

		public void Start() {
			IsActive = true;
			Status = "starting";
		}

		public void Stop(string why) {
			IsActive = false;
			Status = why;
			Target = null;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
		}

		public override IState OnTick(long dt) {
			if ( !PoEMemory.IsAttached ) return this;
			if ( !IsActive ) return this;

			var game = PoEMemory.GameRoot?.InGameState;
			var terrain = GetTerrain();
			var player = GetPlayer();
			var pos = player?.GetComponent<Positioned>();
			if ( game == null || terrain == null || !IsValid(pos) ) return Wait("no terrain or player");
			if ( !IsAlive(player) ) { Stop("the player died"); return this; }
			if ( game.IsPaused ) return Wait("the game is paused");
			if ( game.HasInputFocus ) return Wait("typing in the game");
			if ( PoEMemory.GameRoot?.EscapeState?.Menu != null ) return Wait("escape menu");
			if ( !PoEMemory.TargetHasFocus ) return Wait("the game needs focus to use the mouse");
			if ( binding ) return Wait("changing the skill bar");
			// left click should only move (and interact), never attack
			if ( !SkillBar.LeftClickIsMovement() ) {
				Bind(SkillBar.LeftClickSlot, SkillBar.MovementName);
				return Wait("putting Movement on left click");
			}
			var ctx = new CombatContext(this, game, terrain, player, pos.GridPos, Position(player).Z);
			if ( TrySpendPoints(ctx) ) {
				DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
				return this;
			}
			try {
				Decide(ctx);
			} finally {
				// walk, if walking was decided on
				if ( DefaultFollower.IsActiveFor(this) ) {
					DefaultFollower.Tick(DefaultNavigator, terrain, ctx.Grid, ctx.Z);
				}
			}
			DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
			return this;
		}

		// the points and passives there were when spending last stopped short (eg, at a mastery): not tried again until they change
		private string spendStoppedAt;

		/// <summary>
		/// With passive points to spend and no monsters within AttackRange (as for picking up items), spend them on the
		/// Passive Tree plan (see PassiveAllocator); if monsters come that close while the tree is open, close it. True while doing either (instead of anything else).
		/// </summary>
		public bool TrySpendPoints(CombatContext ctx) {
			var section = GetPlugin<AutomationPlugin>()?.PassiveTree;
			var allocator = section?.Allocator;
			if ( allocator == null || section.Plan == null ) return false;
			int points = GetUI()?.UnspentPassivePoints ?? 0;
			string at = $"{points} points, {ctx.Game.AllocatedPassives.Length} passives";
			if ( ctx.Hostiles.Any(h => h.Distance <= AttackRange) ) {
				if ( allocator.IsActive ) allocator.Stop("monsters came near");
				spending = false;
				if ( !PassiveTreePanel.IsOpen ) return false;
				StopWalking("closing the passive tree");
				Status = "monsters near: closing the passive tree";
				if ( Time.ElapsedMilliseconds - lastTreeClose > 700 ) {
					lastTreeClose = Time.ElapsedMilliseconds;
					Platform.Current.Input.KeyDown(VKey.Escape);
					Platform.Current.Input.KeyUp(VKey.Escape);
				}
				return true;
			}
			if ( !allocator.IsActive ) {
				// it stopped: all spent, or stopped short
				if ( spending ) { spending = false; spendStoppedAt = at; }
				if ( points == 0 || at == spendStoppedAt ) return false;
				allocator.Start();
				spending = true;
			}
			// (the Passive Tree section ticks the allocator)
			StopWalking("spending passive points");
			Status = $"spending passive points: {allocator.Status}";
			return true;
		}
		private bool spending;
		private long lastTreeClose;

		/// <summary>
		/// Called each frame while the driver is on (and the game is ready for input): decide what to do, by calling
		/// TryRetreat, TryFight, Engage, TryExplore, WalkTo, UseSkill... (they set Status). Walking happens after this returns.
		/// The default: retreat if low on life, else fight, else explore, else stop.
		/// </summary>
		protected virtual void Decide(CombatContext ctx) {
			if ( TryRetreat(ctx) || TryFight(ctx) ) return;
			if ( !TryExplore(ctx) ) Stop("the area is explored and clear");
		}

		/// <summary>If life + energy shield is below RetreatBelowLifePercent with monsters near, walk away from them; true if retreating.</summary>
		public bool TryRetreat(CombatContext ctx) {
			var life = ctx.Player.GetComponent<Life>();
			if ( RetreatBelowLifePercent <= 0 || ctx.Hostiles.Count == 0 || !IsValid(life) ) return false;
			float max = life.MaxHP + life.MaxES;
			float percent = max <= 0 ? 100 : 100f * (life.CurHP + life.CurES) / max;
			if ( percent >= RetreatBelowLifePercent ) return false;
			if ( !Combat.NearestSafeSpot(ctx.Terrain, ctx.Grid, ctx.Hostiles.Select(h => h.GridPos), RetreatDistance, out var safe) ) return false;
			WalkTo(safe, $"retreating to {safe} ({percent:F0}% life)");
			return true;
		}

		/// <summary>Fight the monster ChooseTarget picks from those near (with skill, if given, else Attack); true if fighting.</summary>
		public bool TryFight(CombatContext ctx, string skill = null) {
			Target = ChooseTarget(ctx.Hostiles);
			return Target != null && Engage(ctx, Target, skill);
		}

		/// <summary>
		/// Fight target: if it is within range (and in sight, if needed), attack it, with skill if given (see UseSkill),
		/// else with Attack; otherwise walk to a spot where it would be. False if there is no such spot.
		/// </summary>
		public bool Engage(CombatContext ctx, Hostile target, string skill = null, float range = 0, bool? needsLineOfSight = null) {
			if ( range <= 0 ) range = AttackRange;
			bool los = needsLineOfSight ?? NeedsLineOfSight;
			Target = target;
			float d = Combat.Distance(ctx.Grid, target.GridPos);
			bool inRange = d <= range && (!los || ctx.Terrain.HasLineOfSight(ctx.Grid, target.GridPos));
			if ( inRange && TryWorldToScreen(target.WorldPos, out Vector2 screen) && PathFollower.IsSafeToClick(screen) ) {
				if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "attacking");
				Status = $"attacking {target}{(skill == null ? "" : $" with {skill}")}";
				if ( skill == null ) Attack(target, screen);
				else UseSkillPaced(skill, screen);
				return true;
			}
			if ( Combat.FindAttackSpot(ctx.Terrain, ctx.Grid, target.GridPos, range * 0.8f, los, out var spot) ) {
				WalkTo(spot, $"moving to attack {target} from {spot}");
				return true;
			}
			return false;
		}

		private long lastPacedUse;
		/// <summary>UseSkill, but no more often than the skill's cast time (and at least 150 ms apart).</summary>
		public bool UseSkillPaced(string skill, Vector2 screenPos) {
			int interval = Math.Max(150, SkillBar.CastTimeMs(SkillBar.FindSlot(skill)?.Skill));
			if ( Time.ElapsedMilliseconds - lastPacedUse < interval ) return false;
			if ( !UseSkill(skill, screenPos) ) return false;
			lastPacedUse = Time.ElapsedMilliseconds;
			return true;
		}

		/// <summary>True once TryExplore has found nothing left to explore in this area.</summary>
		public bool ExploreDone => exploreTarget != null && exploreTarget.Kind == ExploreTargetKind.None;

		/// <summary>Walk to the next unexplored place; false if there is none left.</summary>
		public bool TryExplore(CombatContext ctx) {
			// look for the next place twice a second, it moves as the area is explored
			if ( exploreTarget == null || Time.ElapsedMilliseconds - exploreTargetTime > 500 || DefaultFollower.Status == FollowStatus.Arrived ) {
				exploreTarget = DefaultExplorer.FindNextTarget(DefaultNavigator, ctx.Terrain, ctx.Grid, monsters: false);
				exploreTargetTime = Time.ElapsedMilliseconds;
			}
			if ( exploreTarget.Kind == ExploreTargetKind.None ) return false;
			WalkTo(exploreTarget.GridPos, $"exploring {exploreTarget.GridPos} ({DefaultExplorer.ExploredFraction:P0} explored)");
			return true;
		}

		/// <summary>Walk the player to goal (the walking happens after Decide returns).</summary>
		public void WalkTo(Offsets.Vector2i goal, string status) {
			Status = status;
			Combat.PathTo(goal, this);
		}

		/// <summary>Stop walking (eg, to click something).</summary>
		public void StopWalking(string why) {
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
		}

		/// <summary>Set the status line (shown at the bottom left).</summary>
		public void SetStatus(string status) => Status = status;

		/// <summary>
		/// Use the skill called name (eg, "Fireball") aimed at screenPos: press the key of the slot it is on, or, if it is
		/// not on the skill bar, put it on a free slot first (then this returns false until that is done).
		/// </summary>
		public bool UseSkill(string name, Vector2 screenPos, uint holdMs = 40) {
			if ( binding ) return false;
			var slot = SkillBar.FindSlot(name);
			if ( slot == null ) {
				var free = SkillBar.FindFreeSlot();
				if ( free == null ) {
					Status = $"no free slot for {name}";
					return false;
				}
				Bind(free.Index, name);
				return false;
			}
			SkillBar.UseSlot(slot, screenPos, holdMs);
			return true;
		}

		/// <summary>Put name on a slot (see SkillBar.BindSkill); the driver waits until it is done, and stops if it fails.</summary>
		public void Bind(int slotIndex, string name) {
			if ( binding ) return;
			binding = true;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "changing the skill bar");
			Run(SkillBar.BindSkill(slotIndex, name, (ok, why) => {
				binding = false;
				if ( !ok ) Stop($"could not put {name} on slot {slotIndex}: {why}");
			}));
		}

		private IState Wait(string why) {
			Status = $"waiting: {why}";
			DrawBottomLeftText($"{Name}: {Status}", Color.Gray);
			return this;
		}
	}

	/// <summary>
	/// What a combat driver knows about the current frame, for deciding what to do.
	/// </summary>
	public class CombatContext {
		public readonly CombatDriver Driver;
		public readonly InGameState Game;
		public readonly Terrain Terrain;
		public readonly PlayerEntity Player;
		public readonly Offsets.Vector2i Grid;
		public readonly float Z;
		private List<Hostile> hostiles;
		public CombatContext(CombatDriver driver, InGameState game, Terrain terrain, PlayerEntity player, Offsets.Vector2i grid, float z) {
			Driver = driver; Game = game; Terrain = terrain; Player = player; Grid = grid; Z = z;
		}
		/// <summary>The hostile monsters within the driver's EngageRadius that can be walked to, nearest first.</summary>
		public List<Hostile> Hostiles => hostiles ??= Combat.GetHostiles(Grid, Driver.EngageRadius)
			.Where(h => DefaultNavigator.IsReachable(Terrain, Grid, h.GridPos)).ToList();
	}

	/// <summary>
	/// The simplest combat driver: aim the cursor at the nearest monster and press one skill key.
	/// Fits most single-skill builds (a spell, a bow attack, a melee strike with a short range).
	/// </summary>
	public class SkillAttackDriver : CombatDriver {
		public override string Name => "Skill Attack";

		/// <summary>The skill to attack with, by name (eg, Fireball); it is put on a free slot if needed. Empty: press AttackKey instead.</summary>
		public string AttackSkill = "";
		public HotKey AttackKey = new HotKey(VKey.Q);
		public int Range = 30; // cells
		public bool LineOfSight = true;
		public int AttackIntervalMs = 250;
		public int HoldMs = 40;

		public override float AttackRange => Range;
		public override bool NeedsLineOfSight => LineOfSight;

		private long lastAttack;

		public override void Render() {
			base.Render();
			ImGui.Separator();
			ImGui.InputText("Attack Skill", ref AttackSkill, 64);
			ImGui.SameLine();
			ImGui_HelpMarker("A skill's name, eg Fireball. It is put on a free slot if it is not on the skill bar, and used as fast as its cast time allows. Leave empty to press the Attack Skill Key instead.");
			if ( string.IsNullOrWhiteSpace(AttackSkill) ) ImGui_HotKeyButton("Attack Skill Key", ref AttackKey);
			ImGui.SliderInt("Attack Range (cells)", ref Range, 3, 80);
			ImGui.Checkbox("Needs Line of Sight", ref LineOfSight);
			ImGui.SliderInt("Attack Interval (ms)", ref AttackIntervalMs, 50, 1500);
			ImGui.SliderInt("Hold Key (ms)", ref HoldMs, 10, 500);
		}

		protected override void Attack(Hostile target, Vector2 screenPos) {
			if ( !string.IsNullOrWhiteSpace(AttackSkill) ) {
				var slot = SkillBar.FindSlot(AttackSkill.Trim());
				int interval = Math.Max(AttackIntervalMs, SkillBar.CastTimeMs(slot?.Skill));
				if ( Time.ElapsedMilliseconds - lastAttack < interval ) return;
				if ( UseSkill(AttackSkill.Trim(), screenPos, (uint)HoldMs) ) lastAttack = Time.ElapsedMilliseconds;
				return;
			}
			if ( AttackKey.Key == VKey.None || Time.ElapsedMilliseconds - lastAttack < AttackIntervalMs ) return;
			lastAttack = Time.ElapsedMilliseconds;
			// aim with the real cursor (the game only reads it while focused), press the skill key (which can be posted)
			Platform.Current.Input.MouseMove(screenPos + WindowOffset());
			Run(new PressKey(AttackKey.Key, (uint)HoldMs));
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;
	}
}
