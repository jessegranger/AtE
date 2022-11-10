using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {
	public class XPPlugin : PluginBase {

		public override string Name => "Experience";

		public bool ShowXPRate = true;
		public bool ShowPickups = true;

		public XPPlugin():base() {
			OnAreaChange += (sender, areaName) => {
				if( xpGainedInThisArea != 0 ) {
					Notify($"XP: {FormatNumber(xpGainedInThisArea)}", Color.Orange, 8000);
					Notify($"Kills: {uniquesKilledInThisArea} uniq, {raresKilledInThisArea} rare, {magicMonstersKilledInThisArea} magic, {normalMonstersKilledInThisArea} norm", Color.Orange, 8000);
				}
				xpGainedInThisArea = 0;
				normalMonstersKilledInThisArea = 0;
				magicMonstersKilledInThisArea = 0;
				raresKilledInThisArea = 0;
				uniquesKilledInThisArea = 0;
				dyingMonsters.Clear();
			};
		}

		private HashSet<uint> aliveLastFrame = new HashSet<uint>();

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show XP Rate", ref ShowXPRate);
			ImGui.SameLine();
			ImGui_HelpMarker("Xp/hr and time to level");

			ImGui.Checkbox("Show XP Pickups", ref ShowPickups);
			ImGui.SameLine();
			ImGui_HelpMarker("XP gains float toward you as numbers");

		}

		private static uint[] xpToNextLevel = new uint[] {
			0, 525, 1760, 3781, 7184, 12186, 19324, 29377, 43181, 61693, 85990, 117506, 157384, 207736, 269997, 346462, 439268, 551295, 685171, 843709, 1030734, 1249629, 1504995, 1800847, 2142652, 2535122, 2984677, 3496798, 4080655, 4742836, 5490247, 6334393, 7283446, 8384398, 9541110, 10874351, 12361842, 14018289, 15859432, 17905634, 20171471, 22679999, 25456123, 28517857, 31897771, 35621447, 39721017, 44225461, 49176560, 54607467, 60565335, 67094245, 74247659, 82075627, 90631041, 99984974, 110197515, 121340161, 133497202, 146749362, 161191120, 176922628, 194049893, 212684946, 232956711, 255001620, 278952403, 304972236, 333233648, 363906163, 397194041, 433312945, 472476370, 514937180, 560961898, 610815862, 664824416, 723298169, 786612664, 855129128, 929261318, 1009443795, 1096169525, 1189918242, 1291270350, 1400795257, 1519130326, 1646943474, 1784977296, 1934009687, 2094900291, 2268549086, 2455921256, 2658074992, 2876116901, 3111280300, 3364828162, 3638186694, 3932818530, 4250334444
		};

		private uint xpGainedInThisArea = 0;
		private uint normalMonstersKilledInThisArea = 0;
		private uint magicMonstersKilledInThisArea = 0;
		private uint raresKilledInThisArea = 0;
		private uint uniquesKilledInThisArea = 0;
		private uint xpLastFrame = uint.MaxValue;
		private double xpPerMs = 0d;

		private Func<IState, long, IState> DriftingText(string text, Color color, Vector3 startPos, Func<Vector3> targetPos, long duration_ms = 3000) {
			long elapsed = 0;
			Vector3 currentPos = startPos;
			return (self, dt) => {
				DrawTextAt(currentPos, text, color);
				elapsed += dt;
				if ( elapsed > duration_ms ) return null;
				float pctComplete = Math.Max(0f, Math.Min(1f, elapsed / (float)duration_ms));
				Vector3 delta = targetPos() - startPos;
				currentPos = startPos + (delta * pctComplete);
				return self;
			};
		}

		private Func<IState, long, IState> DriftingText(string text, Color color, Vector3 startPos, Func<Vector3> targetPos, float speed = 1f) {
			return (self, dt) => {
				DrawTextAt(startPos, text, color);
				Vector3 delta = targetPos() - startPos;
				float len = delta.Length();
				if ( len < 10f ) {
					return null; // success!
				}
				delta *= speed * dt / len; // normalize delta, and scale it so Length() == distance travelled this frame
				startPos += delta;
				return self;
			};
		}

		private static Vector3 GetPlayerPos() => GetPlayer()?.GetComponent<Render>()?.Position ?? Vector3.Zero;

		private Queue<Vector3> dyingMonsters = new Queue<Vector3>();

		public override IState OnTick(long dt) {
			if ( Enabled && ! Paused ) {
				/*
				ImGui.Begin("Debug Player");
				try {
					ImGui_Object("Player", "Player", GetPlayer()?.GetComponent<Player>(), new HashSet<int>());
				} finally {
					ImGui.End();
				}
				*/
				var player = GetPlayer()?.GetComponent<Player>();
				if ( !IsValid(player) ) {
					return this;
				}

				if ( PoEMemory.GameRoot.AreaLoadingState.IsLoading ) {
					return this;
				}
				var aliveThisFrame = new HashSet<uint>();
				foreach(var ent in GetEnemies().Where(IsValid) ) {
					if( IsAlive(ent) ) {
						aliveThisFrame.Add(ent.Id);
					} else if( aliveLastFrame.Contains(ent.Id) ) {
						switch( ent.GetComponent<ObjectMagicProperties>()?.Rarity ) {
							case Offsets.MonsterRarity.Unique: uniquesKilledInThisArea += 1; break;
							case Offsets.MonsterRarity.Rare: raresKilledInThisArea += 1; break;
							case Offsets.MonsterRarity.Magic: magicMonstersKilledInThisArea += 1; break;
							case Offsets.MonsterRarity.White: normalMonstersKilledInThisArea += 1; break;
						}
						dyingMonsters.Enqueue(ent.GetComponent<Render>()?.Position ?? Vector3.Zero);
					}
				}
				// DrawBottomLeftText($"Tracking {aliveThisFrame.Count} alive {dyingMonsters.Count} dead ents...", Color.Yellow);
				aliveLastFrame = aliveThisFrame;

				if ( xpLastFrame == uint.MaxValue ) {
					xpLastFrame = player.XP;
				} else {
					uint xpGain = player.XP - xpLastFrame;
					xpGainedInThisArea += xpGain;
					if ( xpGain > 0 ) {
						// deque all the dyingMonsters
						// show floating text for each share of xp gained
						string xpShare = $"+{xpGain / Math.Max(1, dyingMonsters.Count):F0}";
						while ( dyingMonsters.Count > 0 ) {
							// this may be slow im not sure, the old Assistant had PersistedText as one state, with it's own list of text items to draw 
							Run("DriftingText", DriftingText(xpShare, Color.Yellow, dyingMonsters.Dequeue(), GetPlayerPos, 900));
							// the State Added/Removed messages alone will be spam city
						}
					}
					xpLastFrame += xpGain;
					xpPerMs = MovingAverage(xpPerMs, xpGain / dt, 1000);
					if ( xpPerMs > 0f && ShowXPRate ) {
						// translate the xpPerMs into %/hr and hrs until level
						uint xpToLevel = xpToNextLevel[player.Level] - player.XP;
						var pctPerHr = (xpPerMs * 100 * 1000 * 60 * 60) / xpToLevel;
						var msToLevel = xpToLevel / xpPerMs;
						var hrToLevel = Math.Min(999, msToLevel / (1000 * 60 * 60));
						string status = $"XP: {pctPerHr:F2}%/hr {hrToLevel:F2} hrs";
						// if the xp bar UI element is known, position next to it
						var ui = GetUI();
						if ( IsValid(ui?.ExperienceBar) ) {
							var rect = ui.ExperienceBar.GetClientRect();
							float padding = ImGui.GetFontSize() + 4;
							DrawTextAt(new Vector2(rect.Left, rect.Top - padding), status, Color.Orange);
						} else {
							DrawBottomLeftText(status, Color.Orange);
						}
					}
				}

			}
			return this;
		}
	}
}
