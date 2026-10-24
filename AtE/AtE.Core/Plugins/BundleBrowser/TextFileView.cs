using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace AtE.Plugins {

	/// <summary>
	/// A scrolling, searchable view of a whole text file. Built on a worker thread (decoding and
	/// splitting a 15 MB description file takes a moment) and drawn on the render thread, where
	/// only the lines on screen are touched, so very large files stay smooth.
	/// </summary>
	internal sealed class TextFileView {

		public readonly string Path;
		private readonly string[] lines;
		private readonly string encodingName;

		private string find = "";
		private string appliedFind = "";
		private bool[] isMatch;
		private int[] matchLines = Array.Empty<int>();
		private int current = -1;
		private int pendingScrollLine = -1;

		private const int MaxMatches = 200000;

		/// <summary>Pure data work only — no ImGui — so this is safe to construct off the render thread.</summary>
		public TextFileView(string path, byte[] bytes) {
			Path = path;
			string text = Decode(bytes, out encodingName);
			lines = text.Split('\n');
			for ( int i = 0; i < lines.Length; i++ ) {
				string l = lines[i];
				if ( l.Length > 0 && l[l.Length - 1] == '\r' ) lines[i] = l.Substring(0, l.Length - 1);
			}
		}

		/// <summary>Honours a byte-order mark; otherwise UTF-8, falling back to UTF-16 when the bytes are full of NULs.</summary>
		public static string Decode(byte[] b, out string name) {
			if ( b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ) { name = "UTF-16 LE"; return Encoding.Unicode.GetString(b, 2, (b.Length - 2) & ~1); }
			if ( b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ) { name = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(b, 2, (b.Length - 2) & ~1); }
			if ( b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ) { name = "UTF-8"; return Encoding.UTF8.GetString(b, 3, b.Length - 3); }
			int probe = Math.Min(b.Length, 4096), zeros = 0;
			for ( int i = 0; i < probe; i++ ) if ( b[i] == 0 ) zeros++;
			if ( zeros > probe / 8 ) { name = "UTF-16 LE"; return Encoding.Unicode.GetString(b, 0, b.Length & ~1); }
			name = "UTF-8";
			return Encoding.UTF8.GetString(b);
		}

		public int LineCount => lines.Length;

		private void RunFind() {
			appliedFind = find;
			current = -1;
			matchLines = Array.Empty<int>();
			isMatch = null;
			string term = find.Trim();
			if ( term.Length == 0 ) return;

			var hits = new List<int>();
			var flags = new bool[lines.Length];
			for ( int i = 0; i < lines.Length && hits.Count < MaxMatches; i++ ) {
				if ( lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 ) continue;
				hits.Add(i);
				flags[i] = true;
			}
			matchLines = hits.ToArray();
			isMatch = flags;
			Step(+1);
		}

		/// <summary>Moves to the next (+1) or previous (-1) match, wrapping around, and scrolls to it.</summary>
		private void Step(int direction) {
			if ( matchLines.Length == 0 ) return;
			current = current < 0
				? (direction > 0 ? 0 : matchLines.Length - 1)
				: (current + direction + matchLines.Length) % matchLines.Length;
			pendingScrollLine = matchLines[current];
		}

		public unsafe void Draw() {
			ImGui.TextUnformatted($"{lines.Length:N0} lines, {encodingName}");

			ImGui.SetNextItemWidth(260f);
			bool enter = ImGui.InputText("##textfind", ref find, 256, ImGuiInputTextFlags.EnterReturnsTrue);
			ImGui.SameLine();
			bool findClicked = ImGui.Button("Find##textfindbtn");
			if ( enter || findClicked ) {
				if ( find != appliedFind ) RunFind();
				else Step(+1);
			}
			ImGui.SameLine();
			if ( ImGui.Button("Prev##textprev") ) Step(-1);
			ImGui.SameLine();
			if ( ImGui.Button("Next##textnext") ) Step(+1);
			ImGui.SameLine();
			if ( matchLines.Length > 0 )
				ImGui.TextDisabled($"{current + 1:N0} / {matchLines.Length:N0}{(matchLines.Length >= MaxMatches ? "+" : "")}");
			else if ( appliedFind.Trim().Length > 0 )
				ImGui.TextDisabled("no matches");

			ImGui.BeginChild("textlines##" + Path, new Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar);

			float lineHeight = ImGui.GetTextLineHeightWithSpacing();
			if ( pendingScrollLine >= 0 ) {
				// Put the match about a third of the way down so there is context above it.
				ImGui.SetScrollY(Math.Max(0f, pendingScrollLine * lineHeight - ImGui.GetWindowHeight() / 3f));
				pendingScrollLine = -1;
			}

			var matchColor = new Vector4(1f, 0.85f, 0.3f, 1f);
			var currentColor = new Vector4(1f, 0.6f, 0.1f, 1f);
			int currentLine = current >= 0 ? matchLines[current] : -1;
			var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
			clipper.Begin(lines.Length, lineHeight);
			while ( clipper.Step() ) {
				for ( int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++ ) {
					// ImGui has no tab stops; expand them so indentation survives.
					string text = $"{i + 1,7}  {lines[i].Replace("\t", "    ")}";
					// Not ImGui.TextColored: it is printf-style, and these files are full of '%'.
					bool isCurrent = i == currentLine;
					bool highlight = isCurrent || (isMatch != null && isMatch[i]);
					if ( highlight ) ImGui.PushStyleColor(ImGuiCol.Text, isCurrent ? currentColor : matchColor);
					ImGui.TextUnformatted(text);
					if ( highlight ) ImGui.PopStyleColor();
				}
			}
			clipper.End();
			ImGuiNative.ImGuiListClipper_destroy(clipper.NativePtr);
			ImGui.EndChild();
		}
	}
}
