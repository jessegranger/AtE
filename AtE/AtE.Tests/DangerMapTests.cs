using System;
using System.Linq;
using Xunit;

namespace AtE.Tests {

	/// <summary>The DangerMap's stamping and its search for the safest cell, on made-up terrain (no game needed).</summary>
	public class DangerMapTests {

		// a w x h area, all walkable with clearance 5, except the cells blocked says
		private static Terrain Field(int w, int h, Func<int, int, bool> blocked = null, Func<int, int, int> clearance = null) {
			int bpr = (w + 1) / 2;
			var grid = new byte[bpr * h];
			for ( int y = 0; y < h; y++ ) {
				for ( int x = 0; x < w; x++ ) {
					int v = blocked?.Invoke(x, y) == true ? 0 : clearance?.Invoke(x, y) ?? 5;
					int i = (y * bpr) + (x >> 1);
					grid[i] |= (byte)((x & 1) == 0 ? v : v << 4);
				}
			}
			return Terrain.FromGrids(w, h, bpr, grid, grid);
		}

		private static Offsets.Vector2i C(int x, int y) => new Offsets.Vector2i(x, y);

		private static DangerMap Build(Terrain terrain, Offsets.Vector2i center, params Hazard[] hazards) {
			var map = new DangerMap(30);
			map.Build(terrain, center, hazards);
			return map;
		}

		[Fact]
		public void CircleHasFullWeightInsideAndHalfInItsMargin() {
			var map = Build(Field(100, 100), C(50, 50), new Hazard(C(50, 50), 5) { Weight = 30, Source = "a" });
			Assert.Equal(30, map.NowAt(C(50, 50)));
			Assert.Equal(30, map.NowAt(C(55, 50)));
			Assert.Equal(15, map.NowAt(C(58, 50))); // (within Margin 4 of the edge)
			Assert.Equal(0, map.NowAt(C(60, 50)));
			Assert.Equal(0, map.SoonAt(C(50, 50)));
			Assert.False(map.IsSafe(C(58, 50)));
			Assert.True(map.IsSafe(C(60, 50)));
		}

		[Fact]
		public void SameSourceDoesNotStackButDifferentSourcesDo() {
			var t = Field(100, 100);
			var same = Build(t, C(50, 50), new Hazard(C(50, 50), 5) { Weight = 30, Source = "a" }, new Hazard(C(51, 50), 5) { Weight = 30, Source = "a" });
			Assert.Equal(30, same.NowAt(C(50, 50)));
			var different = Build(t, C(50, 50), new Hazard(C(50, 50), 5) { Weight = 30, Source = "a" }, new Hazard(C(51, 50), 5) { Weight = 30, Source = "b" });
			Assert.Equal(60, different.NowAt(C(50, 50)));
			// (and it saturates below Blocked)
			var many = Build(t, C(50, 50), Enumerable.Range(0, 20).Select(i => new Hazard(C(50, 50), 5) { Weight = 100, Source = $"s{i}" }).ToArray());
			Assert.Equal(DangerMap.Max, many.NowAt(C(50, 50)));
		}

		[Fact]
		public void SoonGoesOnItsOwnLayerAndCounts() {
			var map = Build(Field(100, 100), C(50, 50), new Hazard(C(50, 50), 5) { Layer = DangerLayer.Soon, Weight = 40, Source = "b" });
			Assert.Equal(0, map.NowAt(C(50, 50)));
			Assert.Equal(40, map.SoonAt(C(50, 50)));
			Assert.Equal(40, map.Total(C(50, 50)));
			Assert.False(map.IsSafe(C(50, 50)));
		}

		[Fact]
		public void BeamAndSector() {
			var map = Build(Field(100, 100), C(50, 50),
				new Hazard(C(30, 40), C(70, 40), 2) { Weight = 60, Source = "beam" },
				new Hazard(C(50, 60), 20, new System.Numerics.Vector2(0, 1), -0.3f, 0.3f) { Weight = 50, Source = "breath" });
			Assert.Equal(60, map.NowAt(C(50, 41)));
			Assert.True(map.IsSafe(C(50, 48)));
			Assert.Equal(50, map.NowAt(C(50, 75))); // (in front of the caster, which faces +Y)
			Assert.True(map.IsSafe(C(65, 61))); // (beside it, outside the slice and its margin)
		}

		[Fact]
		public void LevelAgreesWithCovers() {
			var shapes = new[] {
				new Hazard(C(50, 50), 7),
				new Hazard(C(40, 45), C(62, 58), 3),
				new Hazard(C(50, 50), 25, new System.Numerics.Vector2(0.6f, -0.8f), -1.2f, 0.4f),
				new Hazard(C(50, 50), 12, 20, true),
				new Hazard(C(50, 50), 2, 9, true),
			};
			foreach ( var h in shapes ) {
				for ( int y = 15; y <= 85; y++ ) {
					for ( int x = 15; x <= 85; x++ ) {
						var c = C(x, y);
						int expected = h.Covers(c, 0) ? 2 : h.Covers(c, 4) ? 1 : 0;
						Assert.True(expected == h.Level(x, y, 4), $"{h.Shape} at {c}: Covers says {expected}, Level {h.Level(x, y, 4)}");
					}
				}
			}
		}

		[Fact]
		public void RingLeavesItsMiddle() {
			var map = Build(Field(100, 100), C(50, 50), new Hazard(C(50, 50), 12, 20, true) { Weight = 60, Source = "ring" });
			Assert.Equal(0, map.NowAt(C(50, 50)));
			Assert.Equal(30, map.NowAt(C(59, 50))); // (inside, within Margin of the inner edge)
			Assert.Equal(60, map.NowAt(C(65, 50)));
			Assert.Equal(30, map.NowAt(C(73, 50)));
			Assert.Equal(0, map.NowAt(C(75, 50)));
			Assert.True(map.TryFindSafest(C(65, 50), null, out var best, out _));
			Assert.True(map.IsSafe(best));
		}

		[Fact]
		public void WallsAreBlockedAndNearWallsWeighALittle() {
			var t = Field(100, 100, blocked: (x, y) => x == 60, clearance: (x, y) => x == 59 ? 2 : 5);
			var map = Build(t, C(50, 50));
			Assert.Equal(DangerMap.Blocked, map.NowAt(C(60, 50)));
			Assert.Equal(3 * DangerMap.WallWeight, map.NowAt(C(59, 50)));
			Assert.True(map.IsSafe(C(59, 50)));
			// outside the area is blocked too
			var edge = Build(t, C(5, 5));
			Assert.Equal(DangerMap.Blocked, edge.NowAt(C(-1, 5)));
			Assert.Equal(DangerMap.Blocked, edge.NowAt(C(40, 5))); // (outside the window)
		}

		[Fact]
		public void SafestIsJustOutsideTheArea() {
			var map = Build(Field(100, 100), C(50, 50), new Hazard(C(50, 50), 5) { Weight = 30, Source = "a" });
			Assert.True(map.TryFindSafest(C(50, 50), null, out var best, out _));
			Assert.True(map.IsSafe(best));
			float d = Combat.Distance(best, C(50, 50));
			Assert.InRange(d, 9, 11);
		}

		[Fact]
		public void SafestPrefersAGoodCell() {
			var map = Build(Field(100, 100), C(50, 50), new Hazard(C(50, 50), 5) { Weight = 30, Source = "a" });
			Assert.True(map.TryFindSafest(C(50, 50), c => c.X >= 65, out var best, out float score));
			Assert.True(best.X >= 65);
			Assert.True(map.IsSafe(best));
			Assert.True(score < DangerMap.NotGoodPenalty);
		}

		[Fact]
		public void SafestGoesAroundAWall() {
			// a wall at x = 55 from y 0 to 70: the safe side (x > 60) is reached around its end
			var t = Field(100, 100, blocked: (x, y) => x == 55 && y <= 70);
			var map = Build(t, C(50, 50), new Hazard(C(45, 50), 30) { Weight = 30, Source = "a" });
			Assert.True(map.TryFindSafest(C(50, 50), c => c.X > 56, out var best, out _));
			Assert.True(best.X > 56);
			Assert.True(map.CostTo(best, out float walk));
			Assert.True(walk > 40); // (around the wall's end, not through it)
		}

		[Fact]
		public void NothingSafeGivesTheLeastBadCell() {
			var map = Build(Field(100, 100), C(50, 50),
				new Hazard(C(50, 50), 100) { Weight = 30, Source = "low" },
				new Hazard(C(50, 50), 6) { Weight = 90, Source = "high" });
			Assert.False(map.TryFindSafest(C(50, 50), null, out var best, out _));
			Assert.True(map.Total(best) < map.Total(C(50, 50)));
		}
	}
}
