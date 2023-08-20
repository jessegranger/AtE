using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {
	public class ExamplePlugin : PluginBase {

		// public Fields of known types will be automatically persisted using an .ini file
		public bool ShowDemoWindow = false;
		// see: PluginBase for the supported types
		public bool ShowMetricsWindow = false;

		public override int SortIndex => 999;

		/// <summary>
		/// Uses ImGui to render controls for configurable fields.
		/// </summary>
		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show Demo Window", ref ShowDemoWindow);
			ImGui.Checkbox("Show Metrics Window", ref ShowMetricsWindow);
		}

		/*
		private IntPtr fileRootMatch = IntPtr.Zero;
		private long lastFileRootSearch = 0;

		private Offsets.File_RootHeader[] fileRoots = new Offsets.File_RootHeader[16];
		private long fileRoot_ReadCount = 0;
		private long fileRootParseProgress_Root = 0;
		private long fileRootParseProgress_Bucket = 0;
		private long totalFileCount = 0;

		private Dictionary<string, IntPtr> fileBasePtrs = new Dictionary<string, IntPtr>();
*/
		private string fileRootFilter = "/Mods.dat";

		private unsafe Vector2 WorldToScreen(Matrix4x4 matrix, float width, float height, Vector3 worldPos) {
				Vector2 result; // put a struct on the stack
				Vector4 coord = *(Vector4*)&worldPos;
				coord.W = 1;
				// ImGui_Address(PoEMemory.GameRoot.InGameState.WorldData.Address
					// + GetOffset<WorldData>("Camera"),
					// "Camera Address", "Camera");
				// ImGui.Text($"WorldToScreen before transform: X:{coord.X} Y:{coord.Y} Z:{coord.Z} W:{coord.W}");
				// ImGui.Text("Matrix:");
				// ImGui.Text($"{Matrix.M11}, {Matrix.M12}, {Matrix.M13}, {Matrix.M14}");
				// ImGui.Text($"{Matrix.M21}, {Matrix.M22}, {Matrix.M23}, {Matrix.M24}");
				// ImGui.Text($"{Matrix.M31}, {Matrix.M32}, {Matrix.M33}, {Matrix.M34}");
				// ImGui.Text($"{Matrix.M41}, {Matrix.M42}, {Matrix.M43}, {Matrix.M44}");
				coord = Vector4.Transform(coord, matrix);
				// ImGui.Text($"WorldToScreen after transform: X:{coord.X} Y:{coord.Y} Z:{coord.Z} W:{coord.W}");
				coord = Vector4.Divide(coord, coord.W);
				// ImGui.Text($"WorldToScreen after divide: X:{coord.X} Y:{coord.Y} Z:{coord.Z} W:{coord.W}");
				result.X = (coord.X + 1.0f) * (width / 2f);
				result.Y = (1.0f - coord.Y) * (height / 2f);
				return result;
		}

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached ) {
				if ( ShowDemoWindow ) {
					ImGui.ShowDemoWindow();
				}
				if ( ShowMetricsWindow ) {
					ImGui.ShowMetricsWindow();
				}
			}
			return this;
		}

	}
}
