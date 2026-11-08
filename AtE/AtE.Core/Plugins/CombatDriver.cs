using ImGuiNET;
using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The base for a combat driver: a plugin that clears an area by itself.
	///
	/// Each frame, while switched on (ToggleKey): if life is low, retreat to a safe spot; else pick a monster
	/// (ChooseTarget), and if it is in range (with line of sight, if NeedsLineOfSight) Attack it, or else walk to a
	/// spot where it would be; with no monsters around, walk to the next unexplored place; with nothing left, stop.
	///
	/// A driver for a particular build overrides Attack (and usually AttackRange / NeedsLineOfSight / ChooseTarget).
	/// Moving the player and attacking both use the mouse, so this waits while the game is not focused.
	/// </summary>
	public abstract class CombatDriver : PluginBase {

		public HotKey ToggleKey = new HotKey(VKey.None);
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

		private ExploreTarget exploreTarget;
		private long exploreTargetTime;

		public override void Render() {
			base.Render();
			ImGui_HotKeyButton("On / Off", ref ToggleKey);
			ImGui.SameLine();
			ImGui.Text(IsActive ? $"On: {Status}" : $"Off ({Status})");
			ImGui.SliderInt("Engage Radius (cells)", ref EngageRadius, 10, 120);
			ImGui.SliderInt("Retreat Below Life %", ref RetreatBelowLifePercent, 0, 90);
			ImGui.SliderInt("Retreat Distance (cells)", ref RetreatDistance, 10, 80);
		}

		public void Stop(string why) {
			IsActive = false;
			Status = why;
			Target = null;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
		}

		public override IState OnTick(long dt) {
			if ( !Enabled || Paused || !PoEMemory.IsAttached ) return this;
			if ( ToggleKey.IsReleased ) {
				if ( IsActive ) Stop("switched off");
				else { IsActive = true; Status = "starting"; }
			}
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
			var grid = pos.GridPos;
			float z = Position(player).Z;

			try {
				Decide(game, terrain, player, grid, z);
			} finally {
				// walk, if walking was decided on
				if ( DefaultFollower.IsActiveFor(this) ) {
					DefaultFollower.Tick(DefaultNavigator, terrain, grid, z);
				}
			}
			DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
			return this;
		}

		private void Decide(InGameState game, Terrain terrain, PlayerEntity player, Offsets.Vector2i grid, float z) {
			var hostiles = Combat.GetHostiles(grid, EngageRadius)
				.Where(h => DefaultNavigator.IsReachable(terrain, grid, h.GridPos)).ToList();

			// low on life: get away from them
			var life = player.GetComponent<Life>();
			if ( RetreatBelowLifePercent > 0 && hostiles.Count > 0 && IsValid(life) ) {
				float max = life.MaxHP + life.MaxES;
				float percent = max <= 0 ? 100 : 100f * (life.CurHP + life.CurES) / max;
				if ( percent < RetreatBelowLifePercent ) {
					if ( Combat.NearestSafeSpot(terrain, grid, hostiles.Select(h => h.GridPos), RetreatDistance, out var safe) ) {
						Status = $"retreating to {safe} ({percent:F0}% life)";
						Combat.PathTo(safe, this);
						return;
					}
				}
			}

			// fight
			Target = ChooseTarget(hostiles);
			if ( Target != null ) {
				bool inRange = Target.Distance <= AttackRange && (!NeedsLineOfSight || terrain.HasLineOfSight(grid, Target.GridPos));
				if ( inRange && TryWorldToScreen(Target.WorldPos, out Vector2 screen) && PathFollower.IsSafeToClick(screen) ) {
					if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "attacking");
					Status = $"attacking {Target}";
					Attack(Target, screen);
					return;
				}
				if ( Combat.FindAttackSpot(terrain, grid, Target.GridPos, AttackRange * 0.8f, NeedsLineOfSight, out var spot) ) {
					Status = $"moving to attack {Target} from {spot}";
					Combat.PathTo(spot, this);
					return;
				}
			}

			// explore (look for the next place twice a second, it moves as the area is explored)
			if ( exploreTarget == null || Time.ElapsedMilliseconds - exploreTargetTime > 500 || DefaultFollower.Status == FollowStatus.Arrived ) {
				exploreTarget = DefaultExplorer.FindNextTarget(DefaultNavigator, terrain, grid, monsters: false);
				exploreTargetTime = Time.ElapsedMilliseconds;
			}
			if ( exploreTarget.Kind == ExploreTargetKind.None ) {
				Stop("the area is explored and clear");
				return;
			}
			Status = $"exploring {exploreTarget.GridPos} ({DefaultExplorer.ExploredFraction:P0} explored)";
			Combat.PathTo(exploreTarget.GridPos, this);
		}

		private IState Wait(string why) {
			Status = $"waiting: {why}";
			DrawBottomLeftText($"{Name}: {Status}", Color.Gray);
			return this;
		}
	}

	/// <summary>
	/// The simplest combat driver: aim the cursor at the nearest monster and press one skill key.
	/// Fits most single-skill builds (a spell, a bow attack, a melee strike with a short range).
	/// </summary>
	public class SkillAttackDriver : CombatDriver {
		public override string Name => "Combat: Skill Attack";

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
			ImGui_HotKeyButton("Attack Skill Key", ref AttackKey);
			ImGui.SliderInt("Attack Range (cells)", ref Range, 3, 80);
			ImGui.Checkbox("Needs Line of Sight", ref LineOfSight);
			ImGui.SliderInt("Attack Interval (ms)", ref AttackIntervalMs, 50, 1500);
			ImGui.SliderInt("Hold Key (ms)", ref HoldMs, 10, 500);
		}

		protected override void Attack(Hostile target, Vector2 screenPos) {
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
