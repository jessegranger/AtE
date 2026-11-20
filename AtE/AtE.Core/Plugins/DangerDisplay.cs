using ImGuiNET;
using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE.Plugins {

	/// <summary>
	/// Draws the DangerMap on the ground around the player, for debugging AvoidSpam: each block of cells with any danger
	/// gets a translucent quad, red for the Now layer, yellow for Soon (stronger for more), and optionally the walls'
	/// small weights in grey. Also the cell AvoidSpam last chose to walk to, the outlines of the hazards, and a line of
	/// numbers (the map's age, how many hazards, how long it took to build).
	///
	/// While on, it builds the map (DangerMap.Get) each frame it draws, even out of a fight. The MemoryServer's /danger
	/// shows the same map as text.
	/// </summary>
	public class DangerDisplay : PluginBase {

		public override string Name => "Display Danger Map";
		public override string HelpText => "Draws the DangerMap that AvoidSpam uses (red: hurts now, yellow: about to)";

		/// <summary>Draw this many cells each way around the player.</summary>
		public int DrawRadius = 60;
		/// <summary>Draw blocks of this many cells across (the most danger in the block), to draw fewer quads.</summary>
		public int CellStep = 2;
		/// <summary>The most opacity of a block (0-255), reached at danger 100.</summary>
		public int MaxAlpha = 140;
		public bool ShowWalls = false;
		public bool ShowOutlines = true;
		public bool ShowGoal = true;
		public bool ShowStats = true;
		/// <summary>
		/// Show the safest cell (DangerMap.TryFindSafest) within attack range, and in sight, of the boss: the rarest
		/// enemy within the driver's EngageRadius, the one with the most life among equals.
		/// </summary>
		public bool ShowSafest = true;

		public override void Render() {
			base.Render();
			ImGui.SliderInt("Draw radius (cells)", ref DrawRadius, 10, 150);
			ImGui.SliderInt("Block size (cells)", ref CellStep, 1, 6);
			ImGui.SliderInt("Opacity", ref MaxAlpha, 20, 255);
			ImGui.Checkbox("Show walls (their small weights)", ref ShowWalls);
			ImGui.Checkbox("Show hazard outlines", ref ShowOutlines);
			ImGui.Checkbox("Show AvoidSpam's goal", ref ShowGoal);
			ImGui.Checkbox("Show stats", ref ShowStats);
			ImGui.Checkbox("Show the safest cell in range of the boss", ref ShowSafest);
			var map = DangerMap.Current;
			if ( map != null ) {
				ImGui.Text($"map #{map.Number}: {map.Hazards.Count} hazards, read {map.CollectMs:F2} ms, stamped {map.StampMs:F2} ms");
				foreach ( var h in map.Hazards ) ImGui.TextWrapped(h.ToString());
			}
		}

		private Vector2[] corners = Array.Empty<Vector2>();
		private bool[] cornerOk = Array.Empty<bool>();

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached ) return this;
			if ( !(PoEMemory.TargetHasFocus || Overlay.HasFocus) ) return this;
			if ( PoEMemory.GameRoot?.AreaLoadingState?.IsLoading ?? true ) return this;
			var player = GetPlayer();
			var map = DangerMap.Get();
			if ( map == null || !IsValid(player) ) return this;
			float z = Position(player).Z;
			var me = map.Center;
			DrawCells(map, me, z);
			if ( ShowOutlines ) DrawOutlines(map, z);
			if ( ShowGoal && Time.ElapsedMilliseconds - AvoidSpam.LastGoalAt < 1000 && TryGridToScreen(AvoidSpam.LastGoal, z, out Vector2 goal) ) {
				DrawCircle(goal, 10, Color.LimeGreen);
				DrawCircle(goal, 11, Color.LimeGreen);
			}
			if ( ShowSafest ) DrawSafest(map, me, z);
			if ( ShowStats && TryGridToScreen(me, z, out Vector2 at) ) {
				string here = $"now {map.NowAt(me)} soon {map.SoonAt(me)}";
				DrawTextAt(at + new Vector2(16, 8), $"danger {here}; #{map.Number} {map.Hazards.Count} hazards, {map.CollectMs:F1}+{map.StampMs:F1} ms", map.IsSafe(me) || map.IsBlocked(me) ? Color.White : Color.OrangeRed);
			}
			return this;
		}

		// the last answer, kept until the map or the boss's cell changes (the search is a flood over the map)
		private long safestMap = -1;
		private uint safestBossId;
		private Offsets.Vector2i safestBossCell;
		private Safest safest;

		/// <summary>An answer of FindSafest.</summary>
		public class Safest {
			public Entity Boss;
			public Offsets.Vector2i BossCell, Cell;
			public float Range, Engage, Score, Walk;
			/// <summary>Cell is safe (DangerMap.IsSafe); InRange: within Range of the boss, and in sight.</summary>
			public bool Safe, InRange;
			public override string ToString() => $"safest in range of {Boss?.Path?.Split('/').LastOrDefault()} at {BossCell} (range {Range:F0}, engage {Engage:F0}): "
				+ $"{Cell}, walk {Walk:F1}, score {Score:F1}, safe {Safe}, in range {InRange}";
		}

		/// <summary>
		/// The boss: the rarest enemy within radius of from that is alive, targetable and can be damaged; among equals,
		/// the one with the most life (to pick the boss out of a crowd of its own rares). Not Combat.GetHostiles, which
		/// feeds Stalemates: a display must not make the fighting ignore monsters.
		/// </summary>
		public static Entity FindBoss(Offsets.Vector2i from, float radius, out Offsets.Vector2i cell) {
			Entity best = null;
			cell = default;
			var bestRarity = Offsets.MonsterRarity.White;
			uint bestLife = 0;
			foreach ( var ent in GetEnemies() ?? Enumerable.Empty<Entity>() ) {
				if ( !IsAlive(ent) || !IsTargetable(ent) || Combat.CannotBeDamaged(ent) ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				var grid = pos.GridPos;
				if ( Combat.Distance(from, grid) > radius ) continue;
				var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White;
				if ( rarity == Offsets.MonsterRarity.Error ) rarity = Offsets.MonsterRarity.White;
				uint life = MaxLife(ent);
				if ( best == null || rarity > bestRarity || (rarity == bestRarity && life > bestLife) ) {
					best = ent;
					bestRarity = rarity;
					bestLife = life;
					cell = grid;
				}
			}
			return best;
		}

		/// <summary>
		/// The safest cell (DangerMap.TryFindSafest from me) within 80% of the driver's AttackRange of the boss (see
		/// FindBoss, within the driver's EngageRadius), and in its sight; null with no boss.
		/// </summary>
		public static Safest FindSafest(DangerMap map, Offsets.Vector2i me) {
			var driver = GetPlugin<AutomationPlugin>()?.Combat.Current;
			var s = new Safest {
				Engage = driver?.EngageRadius ?? 60,
				Range = (driver?.AttackRange ?? 30f) * .8f, // (as AvoidSpam)
			};
			s.Boss = FindBoss(me, s.Engage, out s.BossCell);
			if ( s.Boss == null ) return null;
			var terrain = GetTerrain();
			var bossCell = s.BossCell;
			float range = s.Range;
			bool Good(Offsets.Vector2i c) => Combat.Distance(c, bossCell) <= range && (terrain?.HasLineOfSight(c, bossCell) ?? true);
			s.Safe = map.TryFindSafest(me, Good, out s.Cell, out s.Score);
			s.InRange = Good(s.Cell);
			map.CostTo(s.Cell, out s.Walk);
			return s;
		}

		private void DrawSafest(DangerMap map, Offsets.Vector2i me, float z) {
			var boss = FindBoss(me, GetPlugin<AutomationPlugin>()?.Combat.Current?.EngageRadius ?? 60, out var bossCell);
			if ( boss == null ) return;
			if ( safest == null || map.Number != safestMap || boss.Id != safestBossId || bossCell.Id != safestBossCell.Id ) {
				safest = FindSafest(map, me);
				safestMap = map.Number;
				safestBossId = boss.Id;
				safestBossCell = bossCell;
			}
			if ( safest == null || !TryGridToScreen(safest.Cell, z, out Vector2 spot) ) return;
			bool good = safest.Safe && safest.InRange;
			var color = good ? Color.Green : Color.DarkOliveGreen;
			DrawCircle(spot, 14, color);
			DrawCircle(spot, 15, color);
			DrawCircle(spot, 16, color);
			string note = good ? "safest" : !safest.InRange ? "safest (none in range)" : "least bad";
			DrawTextAt(spot + new Vector2(18, -6), $"{note}, {map.Total(safest.Cell)}; {boss.Path?.Split('/').LastOrDefault()}", color);
		}

		// the world position of a cell's top-left corner (GridToWorld gives the cell's middle)
		private static Vector3 Corner(int x, int y, float z) => new Vector3(x / 0.092f, y / 0.092f, z + 80f);

		private void DrawCells(DangerMap map, Offsets.Vector2i me, float z) {
			int step = Math.Max(1, CellStep);
			int r = Math.Max(1, DrawRadius) / step * step;
			int n = (2 * r / step) + 1; // blocks per side
			int x0 = me.X - r, y0 = me.Y - r;
			if ( corners.Length < (n + 1) * (n + 1) ) {
				corners = new Vector2[(n + 1) * (n + 1)];
				cornerOk = new bool[(n + 1) * (n + 1)];
			}
			bool projected = false; // (the corners are projected only when some block has something to draw)
			for ( int by = 0; by < n; by++ ) {
				for ( int bx = 0; bx < n; bx++ ) {
					int now = 0, soon = 0;
					bool blocked = true;
					for ( int dy = 0; dy < step; dy++ ) {
						for ( int dx = 0; dx < step; dx++ ) {
							var c = new Offsets.Vector2i(x0 + (bx * step) + dx, y0 + (by * step) + dy);
							int v = map.NowAt(c);
							if ( v == DangerMap.Blocked ) continue;
							blocked = false;
							now = Math.Max(now, v);
							soon = Math.Max(soon, map.SoonAt(c));
						}
					}
					if ( blocked ) continue;
					bool wallsOnly = now < DangerMap.SafeLevel && soon == 0;
					if ( now == 0 && soon == 0 ) continue;
					if ( wallsOnly && !ShowWalls ) continue;
					if ( !projected ) {
						for ( int j = 0; j <= n; j++ ) {
							for ( int i = 0; i <= n; i++ ) {
								int k = (j * (n + 1)) + i;
								cornerOk[k] = TryWorldToScreen(Corner(x0 + (i * step), y0 + (j * step), z), out corners[k]);
							}
						}
						projected = true;
					}
					int a = (by * (n + 1)) + bx, b = a + 1, d = a + n + 1, c2 = d + 1;
					if ( !(cornerOk[a] && cornerOk[b] && cornerOk[c2] && cornerOk[d]) ) continue;
					if ( wallsOnly ) {
						DrawQuadFilled(corners[a], corners[b], corners[c2], corners[d], Color.FromArgb(Alpha(now * 4), 160, 160, 160));
						continue;
					}
					if ( now >= DangerMap.SafeLevel ) DrawQuadFilled(corners[a], corners[b], corners[c2], corners[d], Color.FromArgb(Alpha(now), 230, 30, 30));
					if ( soon > 0 ) DrawQuadFilled(corners[a], corners[b], corners[c2], corners[d], Color.FromArgb(Alpha(soon), 240, 200, 20));
				}
			}
		}

		// more opaque for more danger: a third of MaxAlpha at the least, all of it at 100
		private int Alpha(int danger) => Math.Clamp((MaxAlpha / 3) + ((MaxAlpha - (MaxAlpha / 3)) * Math.Min(danger, 100) / 100), 0, 255);

		private static void DrawOutlines(DangerMap map, float z) {
			foreach ( var h in map.Hazards ) {
				var color = h.Layer == DangerLayer.Soon ? Color.Yellow : Color.Red;
				if ( h.IsBeam ) {
					if ( TryGridToScreen(h.Center, z, out Vector2 a) && TryGridToScreen(h.End, z, out Vector2 b) ) DrawLine(a, b, color);
					continue;
				}
				// a circle (or a sector's arc and sides), as a polygon of points on the ground
				int segments = 32;
				double from = 0, to = 2 * Math.PI;
				if ( h.IsSector ) { from = h.From; to = h.To; }
				Vector2 prev = default;
				bool havePrev = false;
				if ( h.IsSector ) havePrev = TryGridToScreen(h.Center, z, out prev);
				for ( int i = 0; i <= segments; i++ ) {
					double t = from + ((to - from) * i / segments);
					var dir = h.IsSector ? (h.Dir * (float)Math.Cos(t)) + (h.Left * (float)Math.Sin(t)) : new Vector2((float)Math.Cos(t), (float)Math.Sin(t));
					var cell = new Offsets.Vector2i(h.Center.X + (int)Math.Round(dir.X * h.Radius), h.Center.Y + (int)Math.Round(dir.Y * h.Radius));
					bool ok = TryGridToScreen(cell, z, out Vector2 p);
					if ( ok && havePrev ) DrawLine(prev, p, color);
					prev = p;
					havePrev = ok;
				}
				if ( h.IsSector && havePrev && TryGridToScreen(h.Center, z, out Vector2 apex) ) DrawLine(prev, apex, color);
				if ( h.IsRing && h.Inner > 0 ) {
					havePrev = false;
					for ( int i = 0; i <= segments; i++ ) {
						double t = 2 * Math.PI * i / segments;
						var cell = new Offsets.Vector2i(h.Center.X + (int)Math.Round(Math.Cos(t) * h.Inner), h.Center.Y + (int)Math.Round(Math.Sin(t) * h.Inner));
						bool ok = TryGridToScreen(cell, z, out Vector2 p);
						if ( ok && havePrev ) DrawLine(prev, p, color);
						prev = p;
						havePrev = ok;
					}
				}
			}
		}
	}
}
