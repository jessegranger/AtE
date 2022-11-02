using System;
using System.Drawing;
using System.Numerics;
using static AtE.Globals;

namespace AtE {
	public class MapElement : Element {
		public Cached<Offsets.Element_Map> Data;
		public MapElement() : base() => Data = CachedStruct<Offsets.Element_Map>(this);

		public SubMapElement LargeMap => Address == IntPtr.Zero || Data.Value.ptrToSubMap_Full == IntPtr.Zero ? null
			: new SubMapElement() { Address = Data.Value.ptrToSubMap_Full };

		public SubMapElement MiniMap => Address == IntPtr.Zero || Data.Value.ptrToSubMap_Mini == IntPtr.Zero ? null
			: new SubMapElement() { Address = Data.Value.ptrToSubMap_Mini };

		public class SubMapElement : Element {
			public Cached<Offsets.Element_SubMap> Data;
			public SubMapElement() : base() => Data = CachedStruct<Offsets.Element_SubMap>(this);
			public Vector2 Shift => Data.Value.Shift;
			public Vector2 DefaultShift => Data.Value.DefaultShift;
			public float Zoom => Data.Value.Zoom;
		}
		private Vector2 DeltaInWorldToMinimapDelta(Vector2 delta, double diag, float scale, float deltaZ = 0) {
			const float CAMERA_ANGLE = 38f * (float)Math.PI / 180f;
			// Values according to 40 degree rotation of cartesian coordiantes, still doesn't seem right but closer
			var cos = (float)(diag * Math.Cos(CAMERA_ANGLE) / scale);
			var sin = (float)(diag * Math.Sin(CAMERA_ANGLE) / scale); // possible to use cos so angle = nearly 45 degrees

			// 2D rotation formulas not correct, but it's what appears to work?
			var result = new Vector2((delta.X - delta.Y) * cos, deltaZ - (delta.X + delta.Y) * sin);
			// ImGui.Text($"DeltaInWorldToMinimapDelta: {delta} -> {result}");
			return result;
		}

		public Vector2 WorldToMap(Entity ent) {
			Vector2 result = Vector2.Zero;
			// This is a direct transcription of how ExileApi did it,
			// but this can't be the best way... The minimaps should
			// also have a Camera that's like WorldData.Camera + MiniMap.Shift
			// then that Camera should use it's WorldToScreen() (to use it's Matrix)
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
				// ImGui.Text($"Using minimap: {mapRect} {diag}");
			} else if ( largeMap.IsVisibleLocal ) {
				drawOnLargeMap = true;
				mapRect = largeMap.GetClientRect();
				diag = (float)Math.Sqrt(camera.Width * camera.Width + camera.Height * camera.Height);
				// ImGui.Text($"Using large map: {mapRect} {diag}");
			} else {
				// Log($"Invalid map elements, neither is visible.");
				return result;
			}
			Vector2 mapCenter = new Vector2(mapRect.X + mapRect.Width / 2, mapRect.Y + mapRect.Height / 2);
			// ImGui.Text($"mapCenter: {mapCenter}");

			var playerGrid = player.GetComponent<Positioned>().GridPosF;
			var playerZ = player.GetComponent<Render>().Position.Z;
			var gridPos = ent.GetComponent<Positioned>().GridPosF;
			var renderPos = ent.GetComponent<Render>()?.Position ?? Vector3.Zero;
			var gridDelta =  gridPos - playerGrid;
			var zDelta = renderPos.Z - playerZ;

			// ImGui.Text($"playerGrid: {playerGrid}");
			// ImGui.Text($"playerZ: {playerZ}");
			// ImGui.Text($"gridDelta: {gridDelta}");
			// ImGui.Text($"zDelta: {zDelta}");

			if ( drawOnLargeMap ) {
				float k = camera.Width < 1024f ? 1120f : 1024f;
				float scale = k / camera.Height * camera.Width * 3f / 4f / largeMap.Zoom;
				// ImGui.Text($"scale: {scale}");
				// ImGui.Text($"Large map shift: {largeMap.Shift}");
				// ImGui.Text($"Large map default shift: {largeMap.DefaultShift}");
				result = mapCenter + largeMap.Shift + largeMap.DefaultShift + DeltaInWorldToMinimapDelta( gridDelta, diag,
					scale, zDelta / (9f / largeMap.Zoom));
			} else {
				result = mapCenter + DeltaInWorldToMinimapDelta( gridDelta, diag,
					240f, zDelta / 20f);
			}
			// ImGui.Text($"Result: {result}");
			return result;

		}
	}


}
