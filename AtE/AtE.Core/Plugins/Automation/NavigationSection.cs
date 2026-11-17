using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {
	public class NavigationSection : AutomationSection, ITrapDodger {
		public override string Name => "Navigation";

		public bool ShowCurrentPath = false;
		public bool ShowTerrain = false;
		public int TerrainRadius = 30; // in grid cells
		public int TerrainStep = 2; // draw every Nth cell

		public bool ShowPathToCursor = false;
		public bool ShowPathCells = false;
		public bool ShowPathOnMap = false;
		public float WallPenalty = 3f;
		public int SmoothClearance = 3;

		public bool ShowExits = false;
		public bool ShowDoors = false;
		public string RouteGoal = "";
		public string TileSearch = "";
		public string EntitySearch = "";
		private string entitySearchCommitted = "";
		private string tileSearchCommitted = "";

		public bool ShowExploration = false;
		public bool PathToExploreTarget = false;
		public bool ExploreMonsters = true;
		private ExploreTarget exploreTarget;
		private long exploreTargetTime = 0;

		// when pinned, the test path goes to PinnedTarget instead of the cursor
		private bool targetPinned = false;
		private Offsets.Vector2i pinnedTarget;
		private NavPath testPath;
		private Offsets.Vector2i testPathFrom, testPathTo;
		private long testPathTime = 0;
		private double worstPathMs = 0;

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
			ImGui_HelpMarker("Plans a path from the player to the cursor (or to an exit or the waypoint, from its Path button below).");
			if ( targetPinned ) {
				ImGui.SameLine();
				if ( ImGui.Button("Unpin") ) targetPinned = false;
			}
			ImGui.Checkbox("Show Path Cells", ref ShowPathCells);
			ImGui.SameLine();
			ImGui.Checkbox("Show Path on Map", ref ShowPathOnMap);
			ImGui.SameLine();
			ImGui_HelpMarker("Draws the current path (to the cursor, or the one being followed) on the minimap and the large map, with or without Show Path to Cursor.");
			ImGui.SliderFloat("Wall Penalty", ref WallPenalty, 0f, 10f);
			ImGui.SliderInt("Smooth Clearance", ref SmoothClearance, 1, 5);
			if ( testPath != null ) {
				ImGui.Text($"Path: {testPath.Cells.Count} cells, {testPath.Waypoints.Count} waypoints, cost {testPath.Cost:F0}");
				ImGui.Text($"Search ({testPath.Method}): {testPath.Expanded} cells expanded in {testPath.ElapsedMs:F2} ms (worst for this target: {worstPathMs:F2} ms)");
				ImGui.TextWrapped($"From {testPath.Start} to {testPath.Goal}: {string.Join(" | ", testPath.Steps.Take(12))}{(testPath.Steps.Count > 12 ? " ..." : "")}");
				if ( ImGui.Button("Save path for debugging") ) {
					SavePathDebug();
				}
				ImGui.SameLine();
				if ( DefaultFollower.IsActiveFor(this) ) {
					if ( ImGui.Button("Stop following") ) DefaultFollower.Stop(FollowStatus.Idle, "stopped from Navigation");
					ImGui.SameLine();
					ImGui.Text($"{DefaultFollower.Status}: {DefaultFollower.Detail}");
				} else if ( DefaultFollower.IsActive ) {
					ImGui.Text($"(the path follower is busy for {DefaultFollower.Owner})");
				} else if ( ImGui.Button("Follow path") ) {
					StartFollowing(testPath.Goal);
				}
				ImGui.SameLine();
				ImGui_HelpMarker("Minimizes the console, focuses the game, and walks this path to its end with the path follower (doors, levers and traps included).");
			} else if ( DefaultFollower.IsActiveFor(this) ) {
				if ( ImGui.Button("Stop following") ) DefaultFollower.Stop(FollowStatus.Idle, "stopped from Navigation");
			} else if ( ShowPathToCursor || ShowPathOnMap ) {
				ImGui.Text("Path: none");
			}
			ImGui.Separator();
			ImGui.Text($"Explored: {DefaultExplorer.ExploredFraction:P0} of this area");
			ImGui.SameLine();
			if ( ImGui.Button("Reset##explored") ) DefaultExplorer.Reset();
			ImGui.Checkbox("Show Exploration", ref ShowExploration);
			ImGui.SameLine();
			ImGui_HelpMarker("Colors the terrain dots by whether they have been explored (gray) or not (orange), and marks the next target.");
			ImGui.Checkbox("Path to Next Target", ref PathToExploreTarget);
			ImGui.SameLine();
			ImGui_HelpMarker("Instead of the cursor, the path goes to where clearing the area should go next: a nearby monster, or the nearest unexplored place.");
			ImGui.Checkbox("Target Monsters", ref ExploreMonsters);
			ImGui.Text($"Reveal radius: {Explorer.RevealRadius} cells");
			if ( exploreTarget != null ) ImGui.Text($"Next target: {exploreTarget} (found in {exploreTarget.ElapsedMs:F2} ms)");
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
		}

		private void RenderExitsPanel(Terrain terrain) {
			ImGui.Separator();
			ImGui.Checkbox("Show Exits", ref ShowExits);
			ImGui.SameLine();
			ImGui.Checkbox("Show Doors", ref ShowDoors);
			if ( ShowDoors ) foreach ( var door in terrain.Doors ) ImGui.Text($"{door}");
			var area = GetCurrentArea();
			ImGui.Text($"Area: {area}");
			if ( area != null ) {
				ImGui.Text($"Connects to: {string.Join(", ", area.Connections.Select(a => a.DisplayName))}");
			}
			ImGui.SetNextItemWidth(200);
			ImGui.InputText("Route to Area", ref RouteGoal, 64);
			ImGui.SameLine();
			ImGui_HelpMarker("Name (eg 'The Ledge') or id (eg '1_1_5') of an area; shows the areas to walk through, and highlights the exit to take."
				+ " In the Labyrinth: a room's area (eg 'Domain Crossing'), its type (eg 'OutdoorQuad'), 'Aspirant's Trial' (the nearest trial) or 'Izaro' (the last one).");
			var labRoom = Labyrinth.CurrentRoom();
			if ( labRoom != null ) {
				Labyrinth.LearnFromExits(labRoom, terrain.Exits);
				ImGui.Text($"Labyrinth room: {labRoom}");
				ImGui.TextWrapped($"Rooms next to it: {string.Join(", ", labRoom.Neighbours.Select(r => r.DisplayName))}");
				if ( !string.IsNullOrWhiteSpace(RouteGoal) ) {
					var labRoute = Labyrinth.FindRoute(RouteGoal);
					ImGui.TextWrapped(labRoute == null ? "Route: not found" : $"Route: {string.Join(" -> ", labRoute.Select(r => r.DisplayName))}");
				}
			} else if ( !string.IsNullOrWhiteSpace(RouteGoal) ) {
				var route = WorldArea.FindRoute(area, RouteGoal.Trim());
				ImGui.TextWrapped(route == null ? "Route: not found" : $"Route: {string.Join(" -> ", route.Select(a => a.DisplayName))}");
			}
			if ( terrain.Waypoint is AreaFeature waypoint ) {
				if ( ImGui.Button("Path##waypoint") ) {
					ShowPathToCursor = true;
					targetPinned = true;
					pinnedTarget = waypoint.GridPos;
				}
				ImGui.SameLine();
				ImGui.Text($"Waypoint at {waypoint}");
			} else ImGui.Text("Waypoint: none found");
			RenderTileSearch(terrain);
			RenderEntitySearch();
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

		private void RenderEntitySearch() {
			ImGui.SetNextItemWidth(300);
			bool entered = ImGui.InputText("##entitysearch", ref EntitySearch, 128, ImGuiInputTextFlags.EnterReturnsTrue);
			ImGui.SameLine();
			if ( ImGui.Button("Find Entity") || entered ) entitySearchCommitted = EntitySearch.Trim();
			ImGui.SameLine();
			ImGui_HelpMarker("Part of an entity's metadata path (eg 'Puzzle_Parts/Switch', 'Traps/LabyrinthSawblade'), case-insensitive; lists the loaded entities that match, nearest first, each with a Path button.");
			if ( string.IsNullOrEmpty(entitySearchCommitted) ) return;
			var p = GetPlayer()?.GetComponent<Positioned>();
			var from = IsValid(p) ? p.GridPos : default;
			var found = new List<(Entity Ent, Offsets.Vector2i Grid)>();
			foreach ( var ent in GetEntities() ) {
				if ( !IsValid(ent) ) continue;
				string path = ent.Path;
				if ( path == null || path.IndexOf(entitySearchCommitted, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
				var pos = ent.GetComponent<Positioned>();
				if ( !IsValid(pos) ) continue;
				found.Add((ent, pos.GridPos));
			}
			if ( found.Count == 0 ) {
				ImGui.Text($"No loaded entities with \"{entitySearchCommitted}\" in their path");
				return;
			}
			const int maxShown = 30;
			int i = 0;
			foreach ( var (ent, grid) in found.OrderBy(f => Combat.Distance(from, f.Grid)).Take(maxShown) ) {
				if ( ImGui.Button($"Path##entity{i++}") ) {
					ShowPathToCursor = true;
					targetPinned = true;
					pinnedTarget = grid;
				}
				ImGui.SameLine();
				ImGui.Text($"{ent.Id} {ent.Path} at {grid} ({Combat.Distance(from, grid):F0} away)");
			}
			if ( found.Count > maxShown ) ImGui.Text($"... and {found.Count - maxShown} more");
		}

		private void RenderTileSearch(Terrain terrain) {
			ImGui.SetNextItemWidth(300);
			bool entered = ImGui.InputText("##tilesearch", ref TileSearch, 128, ImGuiInputTextFlags.EnterReturnsTrue);
			ImGui.SameLine();
			if ( ImGui.Button("Find Tile") || entered ) tileSearchCommitted = TileSearch.Trim();
			ImGui.SameLine();
			ImGui_HelpMarker("Part of a tile's .tdt path (eg 'Features/lava_kaom3_transition_entrance'); lists the groups of matching tiles, nearest first, each with a Path button.");
			if ( string.IsNullOrEmpty(tileSearchCommitted) ) return;
			var found = terrain.TileFeatures(tileSearchCommitted);
			if ( found.Count == 0 ) {
				ImGui.Text($"No tiles with \"{tileSearchCommitted}\" in this area");
				return;
			}
			var p = GetPlayer()?.GetComponent<Positioned>();
			var from = IsValid(p) ? p.GridPos : default;
			int i = 0;
			foreach ( var tile in found.OrderBy(f => Combat.Distance(from, f.GridPos)) ) {
				if ( ImGui.Button($"Path##tile{i++}") ) {
					ShowPathToCursor = true;
					targetPinned = true;
					pinnedTarget = tile.GridPos;
				}
				ImGui.SameLine();
				ImGui.Text($"{tile} {System.IO.Path.GetFileNameWithoutExtension(tile.TileFile ?? "")} ({Combat.Distance(from, tile.GridPos):F0} away)");
			}
		}

		/// <summary>
		/// The exit that leads to the next area on the route to RouteGoal, if it is known.
		/// </summary>
		private AreaExit NextRouteExit(Terrain terrain) {
			if ( string.IsNullOrWhiteSpace(RouteGoal) ) return null;
			var labRoom = Labyrinth.CurrentRoom();
			if ( labRoom != null ) {
				var labRoute = Labyrinth.FindRoute(RouteGoal);
				return labRoute == null || labRoute.Count < 2 ? null : Labyrinth.ExitTo(labRoom, labRoute[1], terrain.Exits);
			}
			var route = WorldArea.FindRoute(GetCurrentArea(), RouteGoal.Trim());
			if ( route == null || route.Count < 2 ) return null;
			return terrain.Exits.FirstOrDefault(e => e.Destination == route[1].Name);
		}

		private void RenderExits(Terrain terrain, float z) {
			var next = NextRouteExit(terrain);
			foreach ( var exit in terrain.Exits ) {
				if ( !TryGridToScreen(exit.GridPos, z, out Vector2 screen) ) continue; // behind the camera
				Color color = exit == next ? Color.Gold : exit.Seen ? Color.White : Color.Orange;
				DrawCircle(screen, 10, color);
				DrawTextAt(screen + new Vector2(12, -6), exit.Destination ?? "exit?", color);
			}
			if ( terrain.Waypoint is AreaFeature waypoint && TryGridToScreen(waypoint.GridPos, z, out Vector2 at) ) {
				Color color = waypoint.Seen ? Color.SkyBlue : Color.Orange;
				DrawCircle(at, 10, color);
				DrawTextAt(at + new Vector2(12, -6), "Waypoint", color);
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
					if ( ShowExploration && clearance > 0 ) color = DefaultExplorer.IsExplored(x, y) ? Color.DimGray : Color.Orange;
					// TODO: z comes from the player, so cells on slopes will be drawn off the ground
					if ( TryGridToScreen(new Offsets.Vector2i(x, y), z, out Vector2 dot) ) DrawCircle(dot, 2, color);
				}
			}
		}

		// a short history of the paths made, to see how a bad one came about
		private readonly Queue<string> pathHistory = new Queue<string>();
		private void RecordPath(string why, NavPath path, Offsets.Vector2i player, Offsets.Vector2i target) {
			pathHistory.Enqueue($"{Time.ElapsedMilliseconds} {why} player={player} target={target} -> "
				+ (path == null ? "null" : $"{path.Method} gen={path.Generation} start={path.Start} goal={path.Goal} cells={path.Cells.Count} waypoints={string.Join(" ", path.Waypoints)}"));
			while ( pathHistory.Count > 30 ) pathHistory.Dequeue();
		}

		private void SavePathDebug() {
			var sb = new StringBuilder();
			sb.AppendLine($"area: {GetCurrentArea()} terrain generation: {GetTerrain()?.Generation}");
			sb.AppendLine($"pinned: {targetPinned} {pinnedTarget}");
			sb.AppendLine("history:");
			foreach ( var line in pathHistory ) sb.AppendLine(line);
			if ( testPath != null ) {
				sb.AppendLine($"waypoint indices: {string.Join(" ", testPath.WaypointIndices)}");
				sb.AppendLine($"cells: {string.Join(" ", testPath.Cells)}");
			}
			string file = System.IO.Path.Combine(AppContext.BaseDirectory, "pathfinding-debug.txt");
			System.IO.File.WriteAllText(file, sb.ToString());
			Notify($"Saved {file}");
		}

		/// <summary>Walk to goal with the path follower (ticked in OnTick), with the console out of the way and the game focused.</summary>
		private void StartFollowing(Offsets.Vector2i goal) {
			AtE.Console.CollapseNext = true;
			if ( !PoEMemory.TargetHasFocus && !Platform.Current.Memory.TryFocusTarget() ) Log("Navigation: could not focus the game");
			targetPinned = true; // (keep showing the path to this goal after the walk)
			pinnedTarget = goal;
			DefaultFollower.Start(goal, this, verbose: true);
			Log($"Navigation: following the path to {goal}");
		}

		// input sent for the follower (a travel skill past a trap, or the tap that stops the walk), still going out
		private bool inputRunning;
		private bool SendInput(IState input) {
			if ( input == null || inputRunning ) return false;
			inputRunning = true;
			input.Tail().Next = State.NewState(() => inputRunning = false);
			Run(input);
			return true;
		}

		public bool TryDodgeTrap(PathFollower follower, Terrain terrain, Offsets.Vector2i player, Offsets.Vector2i trap, float z) =>
			TravelSkills.Instance.TryDodge(this, () => inputRunning, SendInput, terrain, player, trap, z);

		public bool HasTravelSkill => TravelSkills.Instance.HasAny;

		public bool TrySkipObstacle(PathFollower follower, Terrain terrain, Offsets.Vector2i player, Offsets.Vector2i obstacle, int obstacleIndex, float z) =>
			TravelSkills.Instance.TrySkip(this, () => inputRunning, SendInput, terrain, player, obstacle, obstacleIndex, z);

		/// <summary>Tap the main skill (BuildPlan.MainSkill, else the slot on Q) for one InputLatency: too short to cast, but it stops the walk.</summary>
		public bool StopMoving() {
			string main = BuildPlan.MainSkill();
			var slot = SkillBar.FindSlot(main) ?? SkillBar.GetSlot(BuildPlan.MainSlot);
			if ( slot?.Skill == null || SkillBar.IsMovement(slot.Skill) || slot.Key == VKey.None ) {
				DefaultFollower.Note($"Navigation: can't stop with the main skill ({main}): slot {slot?.Index}, skill {(slot?.Skill == null ? "none" : SkillBar.SkillName(slot.Skill))}, key {slot?.Key}");
				return false;
			}
			DefaultFollower.ReleaseMouse();
			bool sent = SendInput(Inputs.UseSlotHere(slot, Inputs.Latency));
			DefaultFollower.Note($"Navigation: {(sent ? "tapped" : "could not tap (input still going out)")} {SkillBar.SkillName(slot.Skill)} on {slot.Key} for {Inputs.Latency} ms to stop");
			return sent;
		}

		private void RenderPathToCursor(Offsets.Vector2i playerGrid, float z, bool drawOnScreen = true) {
			var terrain = GetTerrain();
			if ( terrain == null ) return;
			var follower = DefaultFollower;
			Offsets.Vector2i target;
			if ( follower.IsActive ) {
				target = follower.Goal; // (the cursor is busy walking)
			} else if ( !targetPinned && testPath != null && ImGui.GetIO().WantCaptureMouse ) {
				target = testPathTo; // (the cursor is on the console, eg going to Follow path: keep the target it had in the game)
			} else {
				// GridToWorld puts things 80 units above z, so un-project onto that same plane
				if ( !TryScreenToGrid(Platform.Current.Keys.GetCursorPos() - WindowOffset(), z + 80f, out Offsets.Vector2i cursorGrid) ) return;
				target = targetPinned ? pinnedTarget : cursorGrid;
			}
			if ( PathToExploreTarget ) {
				if ( exploreTarget == null || exploreTarget.Kind == ExploreTargetKind.None ) {
					testPath = null;
					return;
				}
				target = exploreTarget.GridPos;
			}
			if ( follower.IsActive ) testPath = follower.Path; // someone (eg, a combat driver) is walking: show their path

			// a new target needs a new plan (at most 10 times a second, for a moving cursor);
			// when only the player moves, UpdatePath re-uses the plan we have, which is cheap enough for every frame
			DefaultNavigator.WallPenalty = WallPenalty;
			DefaultNavigator.SmoothClearance = SmoothClearance;
			if ( follower.IsActive ) {
				// (the follower keeps its own path up to date)
			} else if ( target.Id != testPathTo.Id ) {
				if ( Time.ElapsedMilliseconds - testPathTime > 100 ) {
					testPath = DefaultNavigator.FindPath(terrain, playerGrid, target);
					RecordPath("new target", testPath, playerGrid, target);
					testPathFrom = playerGrid;
					testPathTo = target;
					testPathTime = Time.ElapsedMilliseconds;
					worstPathMs = 0;
				}
			} else if ( playerGrid.Id != testPathFrom.Id ) {
				testPath = DefaultNavigator.UpdatePath(testPath, terrain, playerGrid, target);
				RecordPath("player moved", testPath, playerGrid, target);
				testPathFrom = playerGrid;
			}
			if ( testPath != null ) worstPathMs = Math.Max(worstPathMs, testPath.ElapsedMs);
			if ( !drawOnScreen ) return; // (only planned, for Show Path on Map)

			if ( TryGridToScreen(target, z, out Vector2 targetScreen) ) DrawCircle(targetScreen, 6, targetPinned ? Color.Magenta : Color.White);
			if ( testPath == null ) {
				DrawArrowToward(playerGrid, target, z, Color.OrangeRed);
				return;
			}
			if ( ShowPathCells ) {
				foreach ( var cell in testPath.Cells ) {
					if ( TryGridToScreen(cell, z, out Vector2 cellScreen) ) DrawCircle(cellScreen, 1, Color.Gray);
				}
			}
			var points = testPath.Waypoints;
			for ( int i = 1; i < points.Count; i++ ) {
				DrawGridLine(points[i - 1], points[i], z, Color.Cyan);
				if ( TryGridToScreen(points[i], z, out Vector2 point) ) DrawCircle(point, 3, Color.Cyan);
			}
			// the doors to open on the way: highlight the label to click, or the door if its label is not showing
			foreach ( var step in testPath.Steps ) {
				if ( step.Kind == NavStepKind.SkipObstacle && TryGridToScreen(step.Target, z, out Vector2 obstacle) ) {
					DrawCircle(obstacle, 8, Color.Orange);
					DrawTextAt(obstacle + new Vector2(10, -6), "travel skill", Color.Orange);
				}
				if ( step.Kind != NavStepKind.OpenDoor ) continue;
				var label = step.Door.GetLabel();
				if ( IsValid(label) ) {
					var rect = label.GetClientRect();
					DrawFrame(rect, Color.Gold, 2);
					DrawTextAt(new Vector2(rect.X, rect.Y + rect.Height + 2), "click to open", Color.Gold);
				} else if ( TryGridToScreen(step.Target, z, out Vector2 door) ) {
					DrawCircle(door, 8, Color.Gold);
					DrawTextAt(door + new Vector2(10, -6), "open door", Color.Gold);
				}
			}
		}

		/// <summary>
		/// When no path was found: an arrow from the player pointing at target (which may be off screen), with its distance.
		/// </summary>
		private static void DrawArrowToward(Offsets.Vector2i from, Offsets.Vector2i target, float z, Color color) {
			float dx = target.X - from.X, dy = target.Y - from.Y;
			float len = (float)Math.Sqrt(dx * dx + dy * dy);
			if ( len < 1f ) return;
			// project a point a little way toward the target, to get the on-screen direction
			const float probe = 10f; // in grid cells
			var toward = new Offsets.Vector2i(from.X + (int)Math.Round(dx / len * probe), from.Y + (int)Math.Round(dy / len * probe));
			if ( !TryGridToScreen(from, z, out Vector2 start) || !TryGridToScreen(toward, z, out Vector2 end) ) return;
			var dir = end - start;
			if ( dir.LengthSquared() < 1f ) return;
			dir = Vector2.Normalize(dir);
			var tail = start + dir * 40f;
			var tip = start + dir * 140f;
			var side = new Vector2(-dir.Y, dir.X);
			for ( int w = -1; w <= 1; w++ ) DrawLine(tail + side * w, tip + side * w, color); // a bit thicker
			DrawLine(tip, tip - dir * 20f + side * 12f, color);
			DrawLine(tip, tip - dir * 20f - side * 12f, color);
			DrawTextAt(tip + dir * 8f + new Vector2(0, -6), $"no path ({len:F0} cells)", color);
		}

		private void RenderDoors(Terrain terrain, float z) {
			foreach ( var door in terrain.Doors ) {
				Color color = door.IsClosed ? Color.Red : Color.LimeGreen;
				var min = door.GridMin;
				var max = door.GridMax;
				var a = new Offsets.Vector2i(min.X, max.Y);
				var b = new Offsets.Vector2i(max.X, min.Y);
				DrawGridLine(min, a, z, color);
				DrawGridLine(a, max, z, color);
				DrawGridLine(max, b, z, color);
				DrawGridLine(b, min, z, color);
				foreach ( var cell in door.Passage ) {
					if ( TryGridToScreen(cell, z, out Vector2 dot) ) DrawCircle(dot, 1, color);
				}
			}
		}

		/// <summary>
		/// Draw path on whichever map is showing: the minimap (clipped to its frame) or the large map.
		/// </summary>
		private static void RenderPathOnMap(NavPath path, float z) {
			var map = GetUI()?.Map;
			if ( !IsValid(map) ) return;
			RectangleF clip;
			var miniMap = map.MiniMap;
			var largeMap = map.LargeMap;
			if ( IsValid(largeMap) && largeMap.IsVisibleLocal ) {
				clip = RectangleF.Empty; // (the large map covers the screen)
			} else if ( IsValid(miniMap) && miniMap.IsVisibleLocal ) {
				clip = miniMap.GetClientRect();
				if ( clip.Width < 1 || clip.Height < 1 ) return;
			} else return;

			Vector2 ToMap(Offsets.Vector2i cell) => map.WorldToMap(new Vector2(cell.X, cell.Y), new Vector3(0, 0, z));
			var points = path.Waypoints;
			if ( points.Count == 0 ) return;
			Vector2 prev = ToMap(points[0]);
			for ( int i = 1; i < points.Count; i++ ) {
				Vector2 next = ToMap(points[i]);
				Vector2 a = prev, b = next;
				if ( clip.IsEmpty || ClipSegment(clip, ref a, ref b) ) Platform.Current.Renderer.DrawLine(a, b, Color.Cyan, 2);
				prev = next;
			}
			// the end, and any doors / levers / obstacles on the way
			Vector2 end = ToMap(points[points.Count - 1]);
			if ( clip.IsEmpty || clip.Contains(end.X, end.Y) ) DrawCircle(end, 4, Color.White);
			foreach ( var step in path.Steps ) {
				if ( step.Kind == NavStepKind.Walk ) continue;
				Vector2 at = ToMap(step.Target);
				if ( !clip.IsEmpty && !clip.Contains(at.X, at.Y) ) continue;
				DrawCircle(at, 4, step.Kind == NavStepKind.SkipObstacle ? Color.Orange : Color.Gold);
			}
		}

		/// <summary>Clip the segment a-b to rect (Liang-Barsky); false if none of it is inside.</summary>
		private static bool ClipSegment(RectangleF rect, ref Vector2 a, ref Vector2 b) {
			float t0 = 0f, t1 = 1f;
			float dx = b.X - a.X, dy = b.Y - a.Y;
			bool Edge(float p, float q) {
				if ( p == 0 ) return q >= 0;
				float r = q / p;
				if ( p < 0 ) { if ( r > t1 ) return false; if ( r > t0 ) t0 = r; }
				else { if ( r < t0 ) return false; if ( r < t1 ) t1 = r; }
				return true;
			}
			if ( !Edge(-dx, a.X - rect.Left) || !Edge(dx, rect.Right - a.X)
				|| !Edge(-dy, a.Y - rect.Top) || !Edge(dy, rect.Bottom - a.Y) ) return false;
			var start = a;
			a = start + new Vector2(dx, dy) * t0;
			b = start + new Vector2(dx, dy) * t1;
			return true;
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( PoEMemory.IsAttached ) {
				var p = GetPlayer();
				if ( !IsAlive(p) ) return this;
				var posComponent = p.GetComponent<Positioned>();
				if ( !IsValid(posComponent) ) return this;
				var playerPos = Position(p);
				var playerGrid = posComponent.GridPos;

				// walking a path started by Follow path
				if ( DefaultFollower.IsActiveFor(this) ) {
					var status = DefaultFollower.Tick(DefaultNavigator, GetTerrain(), playerGrid, playerPos.Z);
					if ( !DefaultFollower.IsActive ) Log($"Navigation: following ended: {status}: {DefaultFollower.Detail}");
				}

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
				if ( ShowDoors ) {
					var terrain = GetTerrain();
					if ( terrain != null ) RenderDoors(terrain, playerPos.Z);
				}

				if ( ShowExploration || PathToExploreTarget ) {
					var terrain = GetTerrain();
					// the next target moves as the area is explored, so only look for it twice a second
					if ( terrain != null && (exploreTarget == null || Time.ElapsedMilliseconds - exploreTargetTime > 500) ) {
						exploreTarget = DefaultExplorer.FindNextTarget(DefaultNavigator, terrain, playerGrid, ExploreMonsters);
						exploreTargetTime = Time.ElapsedMilliseconds;
					}
					if ( ShowExploration && exploreTarget != null && exploreTarget.Kind != ExploreTargetKind.None
						&& TryGridToScreen(exploreTarget.GridPos, playerPos.Z, out Vector2 next) ) {
						var color = exploreTarget.Kind == ExploreTargetKind.Monster ? Color.OrangeRed : Color.Violet;
						DrawCircle(next, 12, color);
						DrawTextAt(next + new Vector2(14, -6), exploreTarget.Kind == ExploreTargetKind.Monster ? "fight" : "explore", color);
					}
				}

				if ( ShowPathToCursor || PathToExploreTarget ) {
					RenderPathToCursor(playerGrid, playerPos.Z);
				} else if ( ShowPathOnMap ) {
					RenderPathToCursor(playerGrid, playerPos.Z, drawOnScreen: false); // (plan the path to the cursor, but draw it only on the map)
				} else {
					testPath = null;
				}

				if ( ShowPathOnMap ) {
					var mapPath = DefaultFollower.IsActive ? DefaultFollower.Path : testPath;
					if ( mapPath != null ) RenderPathOnMap(mapPath, playerPos.Z);
				}

				var pc = p.GetComponent<Pathfinding>();
				if ( ShowCurrentPath && IsValid(pc) && pc.IsMoving ) {
					DrawWorldLine(playerPos, Offsets.GridToWorld(pc.NextPos, playerPos.Z), Color.Yellow);
					DrawGridLine(pc.PrevPos, pc.NextPos, playerPos.Z, Color.Orange);
					DrawGridLine(pc.NextPos, pc.TargetPos, playerPos.Z, Color.Red);
				}
			}
			return this;
		}

	}

}
