using ImGuiNET;
using System;
using System.Drawing;
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

		public override void Render() {
			base.Render();
			ImGui.SliderInt("Draw radius (cells)", ref DrawRadius, 10, 150);
			ImGui.SliderInt("Block size (cells)", ref CellStep, 1, 6);
			ImGui.SliderInt("Opacity", ref MaxAlpha, 20, 255);
			ImGui.Checkbox("Show walls (their small weights)", ref ShowWalls);
			ImGui.Checkbox("Show hazard outlines", ref ShowOutlines);
			ImGui.Checkbox("Show AvoidSpam's goal", ref ShowGoal);
			ImGui.Checkbox("Show stats", ref ShowStats);
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
			if ( ShowStats && TryGridToScreen(me, z, out Vector2 at) ) {
				string here = $"now {map.NowAt(me)} soon {map.SoonAt(me)}";
				DrawTextAt(at + new Vector2(16, 8), $"danger {here}; #{map.Number} {map.Hazards.Count} hazards, {map.CollectMs:F1}+{map.StampMs:F1} ms", map.IsSafe(me) || map.IsBlocked(me) ? Color.White : Color.OrangeRed);
			}
			return this;
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
