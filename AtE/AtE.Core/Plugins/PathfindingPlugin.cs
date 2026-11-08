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

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Current Path", ref ShowCurrentPath);
			ImGui.Checkbox("Show Terrain", ref ShowTerrain);
			ImGui.SliderInt("Terrain Radius", ref TerrainRadius, 5, 100);
			ImGui.SliderInt("Terrain Step", ref TerrainStep, 1, 5);

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
