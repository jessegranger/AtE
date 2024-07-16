using ImGuiNET;
using System;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using System.Linq;
using static AtE.Globals;
using System.Collections.Generic;

namespace AtE {
	public class MapElement : Element {
		public Cached<Offsets.Element_Map> Data;
		public MapElement() : base() => Data = CachedStruct<Offsets.Element_Map>(() => Address);

		private IntPtr lastKnownLargeMapAddress = IntPtr.Zero;
		private SubMapElement lastKnownLargeMap;
		public SubMapElement LargeMap {
			get {
				if ( IsValid(Address) ) {
					IntPtr largeMapPtr = Data.Value.ptrToSubMap_Full;
					if ( IsValid(largeMapPtr) ) {
						if ( lastKnownLargeMapAddress != largeMapPtr ) {
							lastKnownLargeMap = new SubMapElement() { Address = largeMapPtr };
						}
						return lastKnownLargeMap;
					} else {
						lastKnownLargeMap = null;
						lastKnownLargeMapAddress = IntPtr.Zero;
					}
				}
				return null;
			}
		}

		private IntPtr lastKnownMiniMapAddress = IntPtr.Zero;
		private SubMapElement lastKnownMiniMap;
		public SubMapElement MiniMap {
			get {
				if ( IsValid(Address) ) {
					IntPtr miniMapPtr = Data.Value.ptrToSubMap_Mini;
					if ( IsValid(miniMapPtr) ) {
						if ( lastKnownMiniMapAddress != miniMapPtr ) {
							lastKnownMiniMap = new SubMapElement() { Address = miniMapPtr };
						}
						return lastKnownMiniMap;
					} else {
						lastKnownMiniMap = null;
						lastKnownMiniMapAddress = IntPtr.Zero;
					}
				}
				return null;

			}
		}

		public class SubMapElement : Element {
			public Cached<Offsets.Element_SubMap> Data;
			public SubMapElement() : base() => Data = CachedStruct<Offsets.Element_SubMap>(() => Address);
			public Vector2 Shift => Data.Value.Shift;
			public Vector2 DefaultShift => Data.Value.DefaultShift;
			public float Zoom => Data.Value.Zoom;
		}
		private static readonly float COS_CAMERA_ANGLE = (float)Math.Cos(38f * (float)Math.PI / 180f);
		private static readonly float SIN_CAMERA_ANGLE = (float)Math.Sin(38f * (float)Math.PI / 180f);

		static MapElement() {
			new HotKey(Keys.Tab).OnRelease += (sender, args) => UpdateMiniMapRect();
			/*
			Run("MapElement", (self, dt) => {
				ImGui.Begin("Minimap debug");
				var player = GetPlayer();
				if ( !IsValid(player) ) return self;
				var playerPos = player.GetComponent<Render>()?.Position ?? Vector3.Zero;
				var playerGrid = player.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
				ImGui.Text($"Player Pos: {playerPos} Player Grid: {playerGrid}");
				var actor = player.GetComponent<Actor>();
				if ( !IsValid(actor) ) return self;
				var ent = GetEntities().Where(Globals.IsValid)
								 .Where(e => e.HasComponent<NPC>())
								 .OrderBy(e => (e.GetComponent<Positioned>().GridPosF - playerGrid).LengthSquared())
								 .FirstOrDefault();
				if ( !IsValid(ent) ) return self;
				var entGrid = ent.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
				var entPos = ent.GetComponent<Render>()?.Position ?? Vector3.Zero;
				ImGui.Text($"Ent Pos: {entPos} Player Grid: {entGrid}");
				var ui = GetUI();
				if ( !IsValid(ui) ) return self;
				var orig = ui.Map.WorldToMap(ent);
				DrawSprite(SpriteIcon.SmallGreenCircle, orig, 10f, 10f);
				var pos = ui.Map.WorldToMinimap(entGrid, entPos, playerGrid, playerPos);
				ImGui.Text($"Minimap Pos: {pos}");
				DrawSprite(SpriteIcon.SmallBlueCircle, pos, 8f, 8f);
				var pos2 = ui.Map.WorldToLargeMap(entGrid, entPos, playerGrid, playerPos);
				ImGui.Text($"Largemap Pos: {pos2}");
				DrawSprite(SpriteIcon.SmallRedCircle, pos2, 8f, 8f);
				ImGui.End();
				return self;
			});
			*/

			Overlay.RenderForm.UserResized += (sender, args) => UpdateMiniMapRect();
		}

		private static Vector2 miniMapCenter;
		private static float miniMapDiag;
		private static Vector2 largeMapCenter;
		private static float largeMapDiag;
		private static float largeMapRotCos;
		private static float largeMapRotSin;
		private static void UpdateMiniMapRect() {
			var ui = GetUI();
			if ( !IsValid(ui) ) {
				return;
			}

			RectangleF mapRect = ui.Map?.MiniMap?.GetClientRect() ?? RectangleF.Empty;
			miniMapCenter = new Vector2(mapRect.X + (mapRect.Width / 2f), mapRect.Y + (mapRect.Height / 2f));
			miniMapDiag = (float)Math.Sqrt((mapRect.Width * mapRect.Width) + (mapRect.Height * mapRect.Height));
			Log($"Minimap setting center and diag: {miniMapCenter} {miniMapDiag}");
			var largeMap = ui.Map?.LargeMap;
			if ( !IsValid(largeMap) ) {
				return;
			}

			var camera = GetCamera();
			largeMapCenter = new Vector2(camera.Width / 2, camera.Height / 2) + largeMap.Shift + largeMap.DefaultShift;
			largeMapDiag = (float)Math.Sqrt((camera.Width * camera.Width) + (camera.Height * camera.Height));
			float k = camera.Width < 1024f ? 1120f : 1024f;
			float scale = k / camera.Height * camera.Width * 3f / 4f / largeMap.Zoom;
			largeMapRotCos = largeMapDiag * COS_CAMERA_ANGLE / scale;
			largeMapRotSin = largeMapDiag * SIN_CAMERA_ANGLE / scale;
			Log($"Large map setting center and diag: {largeMapCenter} {largeMapDiag}");
		}

		// the world grid is rotated 45 degrees from the visual screen grid of pixels
		private static readonly float rotSin = (float)Math.Sin(45f* Math.PI / 180f);
		private static readonly float rotCos = (float)Math.Cos(45f* Math.PI / 180f);
		// but also, the camera is tilted up and that plays in somehow, so we still use the ExileApi projection for now

		public Vector2 WorldToMinimap(Vector2 entGrid, Vector3 entPos, Vector2 playerGrid, Vector3 playerPos) {
			var miniMap = MiniMap;
			if( IsValid(miniMap) && miniMap.IsVisibleLocal ) {
				if ( miniMapCenter == Vector2.Zero ) UpdateMiniMapRect();
				Vector2 mapCenter = miniMapCenter;
				float diag = miniMapDiag;
				float scale = 480f;
				Vector2 gridDelta = entGrid - playerGrid;
				float zDelta = entPos.Z - playerPos.Z; // the minimap shifts things vertically on the screen (along Y axis) to show height
				float zoom = miniMap.Zoom * 2f; // .Zoom ranges from 0.5 to 1.5, and the right scaling on the screen is from 1 to 3
				// this is the working formula from ExileApi, which is not standard 2D rotation
				// what I think is happening is a 2D rotation and also a rotation around the tilt of the game camera
				var result = mapCenter + (zoom * new Vector2(
					(gridDelta.X - gridDelta.Y) * (diag * COS_CAMERA_ANGLE / scale),
					// the Y coordinate is:
					// (shifted up/down by zDelta) (minus, because Y screen coordinates are flipped) (rotated and scaled gridDelta Y)
					(zDelta/20f) - ((gridDelta.X + gridDelta.Y) * (diag * SIN_CAMERA_ANGLE / scale))));
				// DrawLine(mapCenter, result, Color.Yellow);
				return result;
			}
			return Vector2.Zero;
		}
		public Vector2 WorldToLargeMap(Vector2 entGrid, Vector3 entPos, Vector2 playerGrid, Vector3 playerPos) {
			var largeMap = LargeMap;
			if( IsValid(largeMap) && largeMap.IsVisibleLocal ) {
				if ( largeMapCenter == Vector2.Zero ) UpdateMiniMapRect();

				var mapCenter = largeMapCenter;
				var gridDelta = entGrid - playerGrid;
				float zDelta = entPos.Z - playerPos.Z;
				float zoom = largeMap.Zoom;
				var result = new Vector2(
					(gridDelta.X - gridDelta.Y) * largeMapRotCos,
					(zDelta / (9f / zoom)) - ((gridDelta.X + gridDelta.Y) * largeMapRotSin));
				return mapCenter + result;
			}
			return Vector2.Zero;
		}
		private Cached<Vector3> playerPos = new Cached<Vector3>(() => GetPlayer()?.GetComponent<Render>()?.Position ?? Vector3.Zero);
		private Cached<Vector2> playerGrid = new Cached<Vector2>(() => GetPlayer()?.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero);
		public Vector2 WorldToMap(Vector2 entGrid, Vector3 entPos) {
				return WorldToLargeMap(entGrid, entPos, playerGrid.Value, playerPos.Value) // one of these two will always be Vector2.Zero
					+ WorldToMinimap(entGrid, entPos, playerGrid.Value, playerPos.Value); // so we just add them together instead of branching
		}
		public Vector2 WorldToMap(Entity ent) {
			if ( IsValid(ent) ) {
				Vector2 entGrid = ent.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
				Vector3 entPos = ent.GetComponent<Render>()?.Position ?? Vector3.Zero;
				return WorldToMap(entGrid, entPos);
			}
			return Vector2.Zero;
			/*
			Vector2 result = Vector2.Zero;
			var miniMap = MiniMap;
			var largeMap = LargeMap;
			var camera = GetCamera();
			var player = GetPlayer(); // the two minimaps are both positioned relative to the player
			if( !(
				IsValid(MiniMap)
				&& IsValid(LargeMap)
				&& IsValid(player)
				)) {
				return result;
			}

			bool drawOnLargeMap = false;
			RectangleF mapRect;
			float diag;
			if ( miniMap.IsVisibleLocal ) {
				mapRect = miniMap.GetClientRect();
				diag = (float)(Math.Sqrt(mapRect.Width * mapRect.Width + mapRect.Height * mapRect.Height) / 2f);
			} else if ( largeMap.IsVisibleLocal ) {
				drawOnLargeMap = true;
				mapRect = largeMap.GetClientRect();
				diag = (float)Math.Sqrt(camera.Width * camera.Width + camera.Height * camera.Height);
			} else {
				return result;
			}
			Vector2 mapCenter = new Vector2(mapRect.X + mapRect.Width / 2, mapRect.Y + mapRect.Height / 2);

			var playerGrid = player.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
			var playerZ = player.GetComponent<Render>()?.Position.Z ?? 0f;
			var gridPos = ent.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
			var renderPos = ent.GetComponent<Render>()?.Position ?? Vector3.Zero;
			if( gridPos == Vector2.Zero || renderPos == Vector3.Zero || playerGrid == Vector2.Zero) {
				return result;
			}
			var gridDelta =  gridPos - playerGrid;
			var zDelta = renderPos.Z - playerZ;

			if ( drawOnLargeMap ) {
				float k = camera.Width < 1024f ? 1120f : 1024f;
				float scale = k / camera.Height * camera.Width * 3f / 4f / largeMap.Zoom;
				result = mapCenter + largeMap.Shift + largeMap.DefaultShift + DeltaInWorldToMinimapDelta( gridDelta, diag,
					scale, zDelta / (9f / largeMap.Zoom));
			} else {
				result = mapCenter + DeltaInWorldToMinimapDelta( gridDelta, diag,
					240f, zDelta / 20f);
			}
			return result;
			*/

		}

		/*
		const float CAMERA_ANGLE = 38f * (float)Math.PI / 180f;
		private Vector2 _WorldToMap(
			RectangleF mapRect, // in screen coords, this rect should frame the map (either mini or large)
			Offsets.Camera camera, // the current ingame camera
			Vector2 playerGrid, // the player's grid position
			Vector3 playerPos, // the player's render position
			Vector2 targetGrid, // the target's grid position
			Vector3 targetPos, // the target's render position
			float scale // 240f for minimap
		) {
			float zDelta = targetPos.Z - playerPos.Z;
			Vector2 mapCenter = new Vector2(mapRect.X + mapRect.Width / 2, mapRect.Y + mapRect.Height / 2);
			float diag = (float)(mapRect.Width * mapRect.Width + mapRect.Height * mapRect.Height);
			var cos = (float)(diag * Math.Cos(CAMERA_ANGLE) / scale);
			var sin = (float)(diag * Math.Sin(CAMERA_ANGLE) / scale); // possible to use cos so angle = nearly 45 degrees

			// 2D rotation formulas not correct, but it's what appears to work?
			var result = new Vector2((delta.X - delta.Y) * cos, zDelta - (delta.X + delta.Y) * sin);
			// ImGui.Text($"DeltaInWorldToMinimapDelta: {delta} -> {result}");
			return result;
		}
		*/
	}


}
