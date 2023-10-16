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
		public bool ShowGridMarkers = false;


		private static Dictionary<Edge, Offsets.Vector2i> DirectionDeltas = new Dictionary<Edge, Offsets.Vector2i>() {
			{  Edge.N,   new Offsets.Vector2i(0,1) },
			{  Edge.NNE, new Offsets.Vector2i(1,2) },
			{  Edge.NE,  new Offsets.Vector2i(1,1) },
			{  Edge.ENE, new Offsets.Vector2i(2,1) },
			{  Edge.E,   new Offsets.Vector2i(1, 0) },
			{  Edge.ESE, new Offsets.Vector2i(2, -1) },
			{  Edge.SE,  new Offsets.Vector2i(1, -1) },
			{  Edge.SSE, new Offsets.Vector2i(1, -2) },
			{  Edge.S,   new Offsets.Vector2i(0, -1) },
			{  Edge.SSW, new Offsets.Vector2i(-1, -2) },
			{  Edge.SW,  new Offsets.Vector2i(-1, -1) },
			{  Edge.WSW, new Offsets.Vector2i(-2, -1) },
			{  Edge.W,   new Offsets.Vector2i(-1, 0) },
			{  Edge.WNW, new Offsets.Vector2i(-2, 1) },
			{  Edge.NW,  new Offsets.Vector2i(-1, 1) },
			{  Edge.NNW, new Offsets.Vector2i(-1, 2) }
		};
		private static Edge GetEdgeFromDelta(int dx, int dy) {
			switch (dx) {
				case -2:
					switch(dy) {
						case -2: return Edge.None;
						case -1: return Edge.WSW;
						case 0: return Edge.None;
						case 1: return Edge.WNW;
						case 2: return Edge.None;
						default: return Edge.None;
					}
				case -1:
					switch(dy) {
						case -2: return Edge.SSW;
						case -1: return Edge.SW;
						case 0: return Edge.W;
						case 1: return Edge.NW;
						case 2: return Edge.NNW;
						default: return Edge.None;
					}
				case 0:
					switch(dy) {
						case -2: return Edge.None;
						case -1: return Edge.S;
						case 0: return Edge.None;
						case 1: return Edge.N;
						case 2: return Edge.None;
						default: return Edge.None;
					}
				case 1:
					switch(dy) {
						case -2: return Edge.SSE;
						case -1: return Edge.SE;
						case 0: return Edge.E;
						case 1: return Edge.NE;
						case 2: return Edge.NNE;
						default: return Edge.None;
					}
				case 2:
					switch(dy) {
						case -2: return Edge.None;
						case -1: return Edge.ESE;
						case 0: return Edge.None;
						case 1: return Edge.ENE;
						case 2: return Edge.None;
						default: return Edge.None;
					}
				default:
					return Edge.None;
			}
		}

		private Dictionary<GridHandle, Edge> Graph = new Dictionary<GridHandle, Edge>();

		public Edge GetEdges(GridHandle g) => Graph.TryGetValue(g, out Edge ret) ? ret : Edge.None;
		public Edge SetEdge(GridHandle g, Edge e, bool enabled = true) {
			var edges = GetEdges(g);
			if( enabled ) {
				edges |= e;
			} else {
				edges ^= (edges & e);
			}
			return Graph[g] = edges;
		}
		public GridHandle GetNeighbor(GridHandle g, Edge e) {
			if( DirectionDeltas.TryGetValue(e, out var delta) ) {
				var grid = HandleToGrid(g);
				return (GridHandle)new Offsets.Vector2i(grid.X + delta.X, grid.Y + delta.Y).Id;
			}
			return GridHandle.None;
		}
		public static IEnumerable<GridHandle> GetNeighbors(GridHandle g, Edge edges) {
			var grid = HandleToGrid(g);
			foreach(var edge in DirectionDeltas.Keys) {
				if( edges.HasFlag(edge) ) {
					var delta = DirectionDeltas[edge];
					yield return (GridHandle)new Offsets.Vector2i(grid.X + delta.X, grid.Y + delta.Y).Id;
				}
			}

		}
		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			var pc = GetPlayer()?.GetComponent<Pathfinding>() ?? null;
			if ( pc != null ) {
				if( PoEMemory.TryRead(PoEMemory.GameRoot.InGameState.WorldData.Cache.ptrToWorldAreaRef, out Offsets.WorldAreaRef ptr) ) {
					ImGui_Address(ptr.ptrToWorldAreaDetails, "Area Details", "WorldAreaDetails");
				}
				ImGui_Address(pc.Address, "Component", "Component_Pathfinding");
				ImGui_Object("Component_Pathfinding", "Component_Pathfinding", pc, new HashSet<int>());
			}
			ImGui.Checkbox("Show Current Path", ref ShowCurrentPath);
			ImGui.Checkbox("Show Grid Markers", ref ShowGridMarkers);

			if( ImGui.Button("Debug This Grid Cell") ) {
				SetEdge(PlayerGrid, (Edge)65535 /* clear all edges */, false);
				SetEdge(PlayerGrid, Edge.S | Edge.SE | Edge.SSE);
			}
		}

		private void RenderGraphLines(GridHandle g, int depth, Vector3 playerPos, Offsets.Vector2i playerGrid) {
			if ( depth <= 0 ) return;
			var edges = GetEdges(g);
			var gridScreen = HandleToScreen(g, playerPos.Z);
			foreach ( var node in GetNeighbors(g, edges) ) {
				DrawLine(gridScreen, HandleToScreen(node, playerPos.Z), Color.Yellow);
				if( depth > 1 ) {
					RenderGraphLines(node, depth - 1, playerPos, playerGrid);
				}
			}
		}

		private GridHandle PlayerGrid;
		private uint PlayerArea;

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached ) {
				var p = GetPlayer();
				if ( !IsAlive(p) ) return this;
				var pc = p.GetComponent<Pathfinding>();
				if ( !IsValid(pc) ) return this;
				// TODO: detect area changes and respond
				var playerArea = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaId ?? 0;
				if( PlayerArea != playerArea ) {
					Notify("Area changed: Syncing pathfinder data...");
					Graph.Clear();
					PlayerArea = playerArea;
				}
				var playerPos = Position(p);
				var playerGrid = p.GetComponent<Positioned>().GridPos;
				var newPlayerGrid = (GridHandle)playerGrid.Id;
				if( PlayerGrid != newPlayerGrid ) {
					// we just moved from one grid to the next
					if( pc.IsMoving ) { // if we walked there
						var oldGrid = HandleToGrid(PlayerGrid);
						var dx = playerGrid.X - oldGrid.X;
						var dy = playerGrid.Y - oldGrid.Y;
						var edge = GetEdgeFromDelta(dx, dy);
						if( edge != Edge.None ) {
							SetEdge(PlayerGrid, edge, true);
							// Notify($"Moving direction: {edge} and edges are now: {GetEdges(PlayerGrid)}", Color.YellowGreen);
						}
					}
					PlayerGrid = newPlayerGrid;
				}
				if ( ShowCurrentPath && pc.IsMoving ) {
					var prevPos = GridToScreen(pc.PrevPos, playerPos.Z);
					var nextPos = GridToScreen(pc.NextPos, playerPos.Z);
					var targetPos = GridToScreen(pc.TargetPos, playerPos.Z);
					DrawLine(WorldToScreen(playerPos), nextPos, Color.Yellow);
					DrawLine(prevPos, nextPos, Color.Orange);
					DrawLine(nextPos, targetPos, Color.Red);
				}
				// DrawTextAt(playerGridScreen, "Player Grid Here", Color.White);
				if ( ShowGridMarkers ) {
					int radius = 20;
					DrawBottomLeftText($"Area Id: {PoEMemory.GameRoot?.InGameState?.WorldData?.AreaId}", Color.White);
					RenderGraphLines(PlayerGrid, 2, playerPos, playerGrid);
					for ( int x = playerGrid.X - radius; x < playerGrid.X + radius; x += 1) {
						for(int y = playerGrid.Y - radius; y < playerGrid.Y + radius; y += 1 ) {
							var gridPos = new Offsets.Vector2i(x, y);
							var color = Color.Gray;
							if ( x == playerGrid.X && y == playerGrid.Y ) {
								color = Color.Yellow;
							}
							DrawCircle(GridToScreen(gridPos, playerPos.Z), 2, color);
						}
					}
				}

			}
			return this;
		}

	}

	/* A GridHandle is a single long Id that holds the grid coordinates for one grid cell in navigation space.
	 * Same value as WorldData.AreaDetails.Id
	 */
	public enum GridHandle : long {
		None = 0,
		Invalid = long.MaxValue
	}

	/* An Edge uint describes a set of connections
	 * between a grid node and it's neighbors.
	 * Edge values are stored in a map of <GridHandle,Edge>
	 * Once pathfinding has learned these values,
	 * a given edge value might be like { Edge.N | Edge.NNE | Edge.S }
	 * this would describe a grid node with connections to the North, North-Northeast, and South directions
	 */
	[Flags] public enum Edge : uint {
		None = 0,
		N =   1<<0,
		NNE = 1<<1,
		NE =  1<<2,
		ENE = 1<<3,
		E =   1<<4,
		ESE = 1<<5,
		SE =  1<<6,
		SSE = 1<<7,
		S =   1<<8,
		SSW = 1<<9,
		SW =  1<<10,
		WSW = 1<<11,
		W =   1<<12,
		WNW = 1<<13,
		NW =  1<<14,
		NNW = 1<<15,
	}

	public static partial class Globals {
		public static bool IsValid(GridHandle g) => g != GridHandle.None && g != GridHandle.Invalid;
		public static GridHandle GridToHandle(Offsets.Vector2i pos) => (GridHandle)pos.Id;
		public static Offsets.Vector2i HandleToGrid(GridHandle g) => new Offsets.Vector2i((long)g);
		public static Vector2 HandleToScreen(GridHandle g, float z = 0) =>
			GridToScreen(HandleToGrid(g), z);
		public static Vector3 HandleToWorld(GridHandle g, float z = 0) =>
			Offsets.GridToWorld(HandleToGrid(g), z);

	}

}
