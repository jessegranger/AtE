using System.Drawing;
using System.Numerics;
using ImGuiNET;

namespace AtE {

	/// <summary>
	/// The portable drawing API the plugins call (via <c>using static AtE.Globals;</c>). These
	/// helpers are platform-neutral: the overloads, bottom-left stacking and <see cref="ToRGBA"/>
	/// are pure, and the primitive draws delegate to the active renderer through the
	/// <see cref="IRenderer"/> seam (<see cref="Platform.Current"/>). The backend-specific work
	/// (ImGui draw lists, the sprite atlas, GPU upload) lives behind that seam, so this file has
	/// no Windows dependency. It previously lived as <c>Globals</c> fragments inside the Windows
	/// <c>ImGuiController</c>/<c>SpriteController</c>; moving it here keeps <c>Globals</c> a single
	/// type in <c>AtE.Core</c> after the project split.
	/// </summary>
	public static partial class Globals {

		public static void DrawBottomLeftText(string text, Color color) => DrawBottomLeftText(text, color, ImGui.GetFontSize());
		public static void DrawBottomLeftText(string text, Color color, float lineHeight) =>
			// use a line id, and a negative line height, so each successive call moves up the screen
			DrawTextAt(0, new Vector2(4, Overlay.Height - 10 - lineHeight), text, color, -lineHeight);

		public static void DrawTextAt(uint id, Vector2 pos, string text, Color color, float lineHeight = 14f) =>
			Platform.Current.Renderer.DrawTextAt(id, pos, text, color, lineHeight);
		public static void DrawTextAt(Vector2 pos, string text, Color color) =>
			Platform.Current.Renderer.DrawText(text, pos, color);

		public static void DrawCircle(Vector2 pos, float radius, Color color) =>
			Platform.Current.Renderer.DrawCircle(pos, radius, color, 1);

		public static void DrawFrame(Vector2 topLeft, Vector2 bottomRight, Color color, int thickness = 1) =>
			Platform.Current.Renderer.DrawFrame(topLeft, bottomRight, color, thickness);
		public static void DrawFrame(RectangleF rect, Color color, int thickness = 1) => DrawFrame(
			new Vector2(rect.X, rect.Y),
			new Vector2(rect.X + rect.Width, rect.Y + rect.Height),
			color, thickness);

		public static void DrawLine(Vector2 start, Vector2 end, Color color) =>
			Platform.Current.Renderer.DrawLine(start, end, color, 1);

		public static int ToRGBA(Color color) => (color.A << 24) | (color.B << 16) | (color.G << 8) | color.R;

		public static void DrawSprite(SpriteIcon icon, Vector2 pos, float w, float h) =>
			Platform.Current.Renderer.DrawSprite(icon, new RectangleF(pos.X - (w / 2), pos.Y - (h / 2), w, h));
	}
}
