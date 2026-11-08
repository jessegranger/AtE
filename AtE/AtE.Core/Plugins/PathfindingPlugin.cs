using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {
	class PathfindingPlugin : PluginBase {

		public bool ShowCurrentPath = false;
		public bool ShowTerrain = false;
		public int TerrainRadius = 30; // in grid cells
		public int TerrainStep = 2; // draw every Nth cell

		public bool ShowPathToCursor = false;
		public bool ShowPathCells = false;
		public HotKey PinTargetKey = new HotKey(VKey.Home);
		public float WallPenalty = 3f;
		public int SmoothClearance = 3;

		public bool ShowExits = false;
		public string RouteGoal = "";

		// when pinned, the test path goes to PinnedTarget instead of the cursor
		private bool targetPinned = false;
		private Offsets.Vector2i pinnedTarget;
		private NavPath testPath;
		private Offsets.Vector2i testPathFrom, testPathTo;
		private long testPathTime = 0;

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Current Path", ref ShowCurrentPath);
			ImGui.Checkbox("Show Terrain", ref ShowTerrain);
			ImGui.SliderInt("Terrain Radius", ref TerrainRadius, 5, 100);
			ImGui.SliderInt("Terrain Step", ref TerrainStep, 1, 5);
			ImGui.Separator();
			ImGui.Checkbox("Show Path to Cursor", ref ShowPathToCursor);
			ImGui.SameLine();
			ImGui_HelpMarker("Plans a path from the player to the cursor. The pin key fixes the target where the cursor is (press again to un-pin).");
			ImGui.Checkbox("Show Path Cells", ref ShowPathCells);
			ImGui_HotKeyButton("Pin Target", ref PinTargetKey);
			ImGui.SliderFloat("Wall Penalty", ref WallPenalty, 0f, 10f);
			ImGui.SliderInt("Smooth Clearance", ref SmoothClearance, 1, 5);
			if ( testPath != null ) {
				ImGui.Text($"Path: {testPath.Cells.Count} cells, {testPath.Waypoints.Count} waypoints, cost {testPath.Cost:F0}");
				ImGui.Text($"Search: {testPath.Expanded} cells expanded in {testPath.ElapsedMs:F1} ms");
			} else if ( ShowPathToCursor ) {
				ImGui.Text("Path: none");
			}
			ImGui.Separator();

			var terrain = GetTerrain();
			if ( terrain == null ) {
				ImGui.Text("Terrain: not loaded");
			} else {
				ImGui.Text($"Terrain: {terrain.Width} x {terrain.Height} cells");
				var p = GetPlayer();
				var posComponent = p?.GetComponent<Positioned>();
				if ( IsValid(posComponent) ) {
					var grid = posComponent.GridPos;
					ImGui.Text($"Player cell: {grid} clearance: {terrain.Clearance(grid)} projectile: {terrain.IsProjectilePassable(grid)}");
				}
				RenderExitsPanel(terrain);
			}
			if ( PoEMemory.TryRead(PoEMemory.GameRoot?.InGameState?.WorldData?.Cache.ptrToWorldAreaRef ?? IntPtr.Zero, out Offsets.WorldAreaRef ptr) ) {
				ImGui_Address(ptr.ptrToWorldAreaDetails, "Area Details", "WorldAreaDetails");
			}
			var pc = GetPlayer()?.GetComponent<Pathfinding>() ?? null;
			if ( pc != null ) {
				ImGui_Address(pc.Address, "Component", "Component_Pathfinding");
				ImGui_Object("Component_Pathfinding", "Component_Pathfinding", pc, new HashSet<int>());
			}
		}

		private void RenderExitsPanel(Terrain terrain) {
			ImGui.Separator();
			ImGui.Checkbox("Show Exits", ref ShowExits);
			var area = GetCurrentArea();
			ImGui.Text($"Area: {area}");
			if ( area != null ) {
				ImGui.Text($"Connects to: {string.Join(", ", area.Connections.Select(a => a.Name))}");
			}
			ImGui.SetNextItemWidth(200);
			ImGui.InputText("Route to Area", ref RouteGoal, 64);
			ImGui.SameLine();
			ImGui_HelpMarker("Name (eg 'The Ledge') or id (eg '1_1_5') of an area; shows the areas to walk through, and highlights the exit to take.");
			var route = string.IsNullOrWhiteSpace(RouteGoal) ? null : WorldArea.FindRoute(area, RouteGoal.Trim());
			if ( !string.IsNullOrWhiteSpace(RouteGoal) ) {
				ImGui.TextWrapped(route == null ? "Route: not found" : $"Route: {string.Join(" -> ", route.Select(a => a.Name))}");
			}
			int i = 0;
			foreach ( var exit in terrain.Exits ) {
				if ( ImGui.Button($"Path##exit{i++}") ) {
					ShowPathToCursor = true;
					targetPinned = true;
					pinnedTarget = exit.GridPos;
				}
				ImGui.SameLine();
				ImGui.Text($"{exit}");
			}
		}

		/// <summary>
		/// The exit that leads to the next area on the route to RouteGoal, if it is known.
		/// </summary>
		private AreaExit NextRouteExit(Terrain terrain) {
			if ( string.IsNullOrWhiteSpace(RouteGoal) ) return null;
			var route = WorldArea.FindRoute(GetCurrentArea(), RouteGoal.Trim());
			if ( route == null || route.Count < 2 ) return null;
			return terrain.Exits.FirstOrDefault(e => e.Destination == route[1].Name);
		}

		private void RenderExits(Terrain terrain, float z) {
			var next = NextRouteExit(terrain);
			foreach ( var exit in terrain.Exits ) {
				var screen = GridToScreen(exit.GridPos, z);
				Color color = exit == next ? Color.Gold : exit.Seen ? Color.White : Color.Orange;
				DrawCircle(screen, 10, color);
				DrawTextAt(screen + new Vector2(12, -6), exit.Destination ?? "exit?", color);
			}
		}

		private static readonly Color[] ClearanceColors = new Color[] {
			Color.Red, // 0 = blocked
			Color.FromArgb(255, 40, 120, 40),
			Color.FromArgb(255, 60, 160, 60),
			Color.FromArgb(255, 80, 200, 80),
			Color.FromArgb(255, 100, 230, 100),
			Color.FromArgb(255, 140, 255, 140),
		};

		private void RenderTerrain(Terrain terrain, Offsets.Vector2i playerGrid, float z) {
			int step = Math.Max(1, TerrainStep);
			for ( int x = playerGrid.X - TerrainRadius; x <= playerGrid.X + TerrainRadius; x += step ) {
				for ( int y = playerGrid.Y - TerrainRadius; y <= playerGrid.Y + TerrainRadius; y += step ) {
					if ( !terrain.InBounds(x, y) ) continue;
					int clearance = terrain.Clearance(x, y);
					Color color = clearance > 0 ? ClearanceColors[Math.Min(clearance, ClearanceColors.Length - 1)]
						: terrain.IsProjectilePassable(x, y) ? Color.CornflowerBlue // blocked, but projectiles pass
						: Color.Red;
					// TODO: z comes from the player, so cells on slopes will be drawn off the ground
					DrawCircle(GridToScreen(new Offsets.Vector2i(x, y), z), 2, color);
				}
			}
		}

		private void RenderPathToCursor(Offsets.Vector2i playerGrid, float z) {
			var terrain = GetTerrain();
			if ( terrain == null ) return;
			// GridToWorld puts things 80 units above z, so un-project onto that same plane
			if ( !TryScreenToGrid(Platform.Current.Keys.GetCursorPos() - WindowOffset(), z + 80f, out Offsets.Vector2i cursorGrid) ) return;
			if ( PinTargetKey.IsReleased ) {
				targetPinned = !targetPinned;
				pinnedTarget = cursorGrid;
			}
			var target = targetPinned ? pinnedTarget : cursorGrid;

			// re-plan when either end moves, but not more than 10 times a second
			bool moved = target.Id != testPathTo.Id || playerGrid.Id != testPathFrom.Id;
			if ( moved && Time.ElapsedMilliseconds - testPathTime > 100 ) {
				DefaultNavigator.WallPenalty = WallPenalty;
				DefaultNavigator.SmoothClearance = SmoothClearance;
				testPath = DefaultNavigator.FindPath(terrain, playerGrid, target);
				testPathFrom = playerGrid;
				testPathTo = target;
				testPathTime = Time.ElapsedMilliseconds;
			}

			DrawCircle(GridToScreen(target, z), 6, targetPinned ? Color.Magenta : Color.White);
			if ( testPath == null ) return;
			if ( ShowPathCells ) {
				foreach ( var cell in testPath.Cells ) {
					DrawCircle(GridToScreen(cell, z), 1, Color.Gray);
				}
			}
			var points = testPath.Waypoints;
			for ( int i = 1; i < points.Count; i++ ) {
				DrawLine(GridToScreen(points[i - 1], z), GridToScreen(points[i], z), Color.Cyan);
				DrawCircle(GridToScreen(points[i], z), 3, Color.Cyan);
			}
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached ) {
				var p = GetPlayer();
				if ( !IsAlive(p) ) return this;
				var posComponent = p.GetComponent<Positioned>();
				if ( !IsValid(posComponent) ) return this;
				var playerPos = Position(p);
				var playerGrid = posComponent.GridPos;

				if ( ShowTerrain ) {
					var terrain = GetTerrain();
					if ( terrain != null ) {
						RenderTerrain(terrain, playerGrid, playerPos.Z);
					} else {
						DrawBottomLeftText("Pathfinding: terrain not loaded", Color.Orange);
					}
				}

				if ( ShowExits ) {
					var terrain = GetTerrain();
					if ( terrain != null ) RenderExits(terrain, playerPos.Z);
				}

				if ( ShowPathToCursor ) {
					RenderPathToCursor(playerGrid, playerPos.Z);
				} else {
					testPath = null;
				}

				var pc = p.GetComponent<Pathfinding>();
				if ( ShowCurrentPath && IsValid(pc) && pc.IsMoving ) {
					var prevPos = GridToScreen(pc.PrevPos, playerPos.Z);
					var nextPos = GridToScreen(pc.NextPos, playerPos.Z);
					var targetPos = GridToScreen(pc.TargetPos, playerPos.Z);
					DrawLine(WorldToScreen(playerPos), nextPos, Color.Yellow);
					DrawLine(prevPos, nextPos, Color.Orange);
					DrawLine(nextPos, targetPos, Color.Red);
				}
			}
			return this;
		}

	}

}
