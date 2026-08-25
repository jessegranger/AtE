using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using static AtE.Globals;

namespace AtE.Plugins {

	/// <summary>
	/// Browse the game's own files — the GGPK tree and everything inside the bundles — and
	/// preview the art in the overlay.
	///
	/// Threading matters here. Opening the index takes seconds (it decompresses the index
	/// bundle, generates every path and builds the tree), and reading a file means an Oodle
	/// decode, so both run on worker threads. The render thread only ever does ImGui calls and
	/// the GPU upload, because that is the one thread the Linux backend's GL context is current
	/// on. Workers hand their result back as a single immutable <see cref="PreviewResult"/>,
	/// stamped with the selection it belongs to, so a slow stale read cannot overwrite a newer
	/// preview or get paired with the wrong path.
	/// </summary>
	public class BundleBrowserPlugin : PluginBase {

		public override string Name => "Bundle Browser";

		public override string HelpText =>
			"Browses Content.ggpk and the bundles inside it, previews .dds art, and extracts files to disk. " +
			"Needs the Path of Exile folder, which also supplies the Oodle DLL used to decompress bundles.";

		public override int SortIndex => 900;

		/// <summary>The game folder (or a Content.ggpk path). Persisted.</summary>
		public string GamePath = "";

		/// <summary>Where "Extract" writes. Persisted; empty means a folder beside the exe.</summary>
		public string ExtractTo = "";

		/// <summary>
		/// Optional override for the bundled libooz — a path to another libooz or to an
		/// oo2core_*_win64.dll. Persisted. Leave empty to use the copy that ships with AtE.
		/// See <see cref="Oodle"/>.
		/// </summary>
		public string OodlePath = "";

		public bool ShowWindow = false;

		// ---- not settings ------------------------------------------------------------------------

		/// <summary>What a worker hands back: exactly one of Image / Text / Error is set.</summary>
		private sealed class PreviewResult {
			public readonly int Token;
			public readonly string Path;
			public readonly DdsImage Image;
			public readonly string Text;
			public readonly string Error;
			public PreviewResult(int token, string path, DdsImage image, string text, string error) {
				Token = token;
				Path = path;
				Image = image;
				Text = text;
				Error = error;
			}
		}

		// Written by the loader thread, read every frame on the render thread.
		private volatile BundleIndex index;
		private volatile string status = "Not loaded.";
		private volatile bool busy;

		// The hand-off slot from the preview workers to OnTick, guarded so that a slow worker
		// arriving late cannot overwrite (and thereby destroy) a newer selection's result.
		private readonly object previewGate = new object();
		private PreviewResult pending;

		// Only ever touched on the render thread.
		private int selectionToken;
		private string search = "";
		private string[] searchResults = Array.Empty<string>();
		private string lastSearch = null;
		private string selectedPath;
		private string selectedInfo;
		private ITextureHandle preview;
		private string previewPath;
		private string previewError;
		private string previewText;
		// The parsed image behind the current preview, kept so "Save as PNG" can convert it
		// without re-reading and re-decompressing the bundle. Immutable once parsed, so handing
		// the reference to a worker thread is safe.
		private DdsImage previewImage;

		private const int MaxSearchResults = 400;
		private const int MaxListedFiles = 300;
		private const string PreviewTextureKey = "BundleBrowser/preview";

		public override void Render() {
			base.Render();
			ImGui.TextWrapped("Reads the game's own data files. Point this at your Path of Exile folder.");
			ImGui.InputText("Game folder or .ggpk", ref GamePath, 512);
			ImGui.SameLine();
			if ( ImGui.Button("Find##BundleBrowserFind") ) {
				string found = GGPKReader.FindContentGgpk(GamePath);
				if ( found == null ) Notify("Could not find Content.ggpk in the usual places.");
				else GamePath = Path.GetDirectoryName(found);
			}
			ImGui.InputText("Extract to", ref ExtractTo, 512);
			ImGui.InputText("Oodle DLL (libooz / oo2core)", ref OodlePath, 512);
			if ( ImGui.IsItemHovered() ) {
				ImGui.BeginTooltip();
				ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
				ImGui.TextUnformatted(Oodle.NotFoundAdvice);
				ImGui.PopTextWrapPos();
				ImGui.EndTooltip();
			}
			ImGui.Checkbox("Show browser window", ref ShowWindow);
			ImGui.Separator();
			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(status);
			ImGui.PopTextWrapPos();
			if ( !busy && index == null && ImGui.Button("Load index##BundleBrowserLoad") ) BeginLoad();
			if ( index != null ) {
				ImGui.SameLine();
				if ( ImGui.Button("Unload##BundleBrowserUnload") ) Unload();
			}
			// When the load failed for want of a decoder, show exactly where we looked.
			if ( index == null && !busy && !Oodle.IsAvailable ) {
				if ( ImGui.CollapsingHeader("Oodle search detail##BundleBrowserOodle") ) {
					ImGui.PushTextWrapPos(0f);
					ImGui.TextUnformatted(Oodle.SearchReport);
					ImGui.PopTextWrapPos();
				}
			}
		}

		public override IState OnTick(long dt) {
			if ( !Enabled || Paused ) return this;

			// Take whatever a worker left, and ignore results for a selection we have moved past.
			PreviewResult ready;
			lock ( previewGate ) {
				ready = pending;
				pending = null;
			}
			if ( ready != null && ready.Token == selectionToken ) Apply(ready);

			if ( ShowWindow ) DrawWindow();
			return this;
		}

		#region Loading

		private void BeginLoad() {
			if ( busy ) return;
			string path = string.IsNullOrWhiteSpace(GamePath) ? GGPKReader.FindContentGgpk() : GamePath;
			if ( string.IsNullOrWhiteSpace(path) ) {
				status = "Set the game folder first (no Content.ggpk found automatically).";
				return;
			}
			busy = true;
			status = $"Loading {path} ...";
			string oodlePath = OodlePath;
			var worker = new Thread(() => {
				try {
					// Normally the libooz that ships with AtE is used and this is empty. A path
					// here overrides it, and rebinds now so the error lands on this setting
					// rather than looking like an index problem.
					Oodle.OverridePath = string.IsNullOrWhiteSpace(oodlePath) ? null : oodlePath;
					if ( Oodle.OverridePath != null ) {
						if ( !Oodle.TryLoadFrom(Oodle.OverridePath) ) {
							status = $"Could not load the Oodle DLL at \"{oodlePath}\" — see the log.";
							return;
						}
					} else if ( Oodle.IsAvailable && Oodle.LibraryPath != null
							&& Oodle.LibraryPath.IndexOf(Oodle.BundledLibraryName, StringComparison.OrdinalIgnoreCase) < 0 ) {
						// An override was in force and has now been cleared; drop it so the next
						// TryInitialise goes back to the bundled library instead of silently
						// keeping whatever is already bound.
						Oodle.Unload();
					}
					var loaded = BundleIndex.Open(path);
					// Build the path tree here rather than on the first frame that opens the
					// window: it walks every path, and doing that on the render thread stalls it.
					int roots = loaded.Tree.DirectoryCount;
					index = loaded;
					status = $"{loaded.Bundles.Count} bundles, {loaded.FileCount} files, {roots} top-level folders, " +
						$"{loaded.Scheme} hashing" +
						(Oodle.IsAvailable ? $", Oodle: {Path.GetFileName(Oodle.LibraryPath)}" : ", Oodle: NOT FOUND");
				} catch ( Exception e ) {
					status = $"Failed: {e.Message}";
					Log($"BundleBrowser: load failed: {e}");
				} finally {
					busy = false;
				}
			}) {
				IsBackground = true,
				Name = "AtE BundleBrowser loader",
			};
			worker.Start();
		}

		private void Unload() {
			var old = index;
			index = null;
			ClearPreview();
			selectedPath = null;
			selectedInfo = null;
			searchResults = Array.Empty<string>();
			lastSearch = null;
			// Bumping the token makes any in-flight worker's result be discarded on arrival.
			selectionToken++;
			// A read already inside the index will fail with ObjectDisposedException, which the
			// worker catches and reports; nothing is left holding a bundle afterwards.
			old?.Dispose();
			status = "Not loaded.";
		}

		private void ClearPreview() {
			preview = null;
			previewPath = null;
			previewText = null;
			previewError = null;
			previewImage = null;
			lock ( previewGate ) {
				pending = null;
			}
		}

		#endregion

		#region Window

		private void DrawWindow() {
			bool open = ShowWindow;
			ImGui.SetNextWindowSize(new Vector2(980, 620), ImGuiCond.FirstUseEver);
			if ( ImGui.Begin("Bundle Browser", ref open) ) {
				var idx = index;
				if ( idx == null ) {
					// TextUnformatted, not Text/TextWrapped: these strings carry paths and
					// exception text, and the Text* family treats its argument as a printf format.
					ImGui.PushTextWrapPos(0f);
					ImGui.TextUnformatted(status);
					ImGui.PopTextWrapPos();
					if ( !busy && ImGui.Button("Load index") ) BeginLoad();
				} else {
					ImGui.TextUnformatted(status);
					ImGui.Separator();
					ImGui.InputText("Search", ref search, 256);
					ImGui.SameLine();
					if ( ImGui.Button("Clear") ) search = "";

					ImGui.BeginChild("BundleBrowserLeft", new Vector2(520, 0), true);
					if ( string.IsNullOrWhiteSpace(search) ) DrawTree(idx.Tree);
					else DrawSearchResults(idx);
					ImGui.EndChild();

					ImGui.SameLine();
					ImGui.BeginChild("BundleBrowserRight", new Vector2(0, 0), true);
					DrawDetails();
					ImGui.EndChild();
				}
			}
			ImGui.End();
			ShowWindow = open;
		}

		private void DrawSearchResults(BundleIndex idx) {
			if ( search != lastSearch ) {
				lastSearch = search;
				string term = search.Trim();
				searchResults = idx.Paths
					.Where(p => p.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
					.Take(MaxSearchResults)
					.ToArray();
			}
			ImGui.TextUnformatted($"{searchResults.Length}{(searchResults.Length == MaxSearchResults ? "+" : "")} match(es)");
			ImGui.Separator();
			for ( int i = 0; i < searchResults.Length; i++ ) {
				string path = searchResults[i];
				if ( ImGui.Selectable($"{path}##result_{i}", path == selectedPath) ) Select(path);
			}
		}

		private void DrawTree(BundleDirectory dir) {
			foreach ( var child in dir.Directories ) {
				if ( ImGui.TreeNode($"{child.Name}##dir_{child.FullPath}") ) {
					DrawTree(child);
					ImGui.TreePop();
				}
			}
			// Some of PoE's folders hold thousands of files; drawing them all every frame is
			// wasted work, and search is the better tool at that point.
			var files = dir.Files;
			int limit = Math.Min(files.Count, MaxListedFiles);
			for ( int i = 0; i < limit; i++ ) {
				var file = files[i];
				if ( ImGui.Selectable($"{file.Name}##file_{file.FullPath}", file.FullPath == selectedPath) )
					Select(file.FullPath);
			}
			if ( files.Count > limit ) {
				ImGui.TextDisabled($"... and {files.Count - limit} more, use Search");
			}
		}

		private void DrawDetails() {
			if ( selectedPath == null ) {
				ImGui.TextWrapped("Pick a file on the left. .dds art previews here; text files show their first lines.");
				return;
			}
			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(selectedPath);
			ImGui.PopTextWrapPos();
			if ( selectedInfo != null ) ImGui.TextUnformatted(selectedInfo);
			if ( ImGui.Button("Copy path") ) ImGui.SetClipboardText(selectedPath);
			ImGui.SameLine();

			// With a convertible image on screen, offer the thing that is actually useful: a file
			// any viewer opens. Otherwise fall back to writing the raw bytes.
			DdsImage image = previewPath == selectedPath ? previewImage : null;
			bool asPng = image != null && image.CanDecodeToRgba;
			// Stable "##" id despite the changing label: ImGui keys the active widget by id, and a
			// label that changed between press and release would drop the click.
			if ( ImGui.Button(asPng ? "Save as PNG##BundleBrowserSaveFile" : "Extract file##BundleBrowserSaveFile") ) {
				if ( asPng ) SaveAsPng(selectedPath, image);
				else ExtractSelected(folder: false);
			}
			if ( image != null && !asPng && ImGui.IsItemHovered() ) {
				ImGui.BeginTooltip();
				ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
				ImGui.TextUnformatted($"{image.Format} has no software decoder (the GPU handles it for the " +
					"preview), so this writes the raw .dds instead.");
				ImGui.PopTextWrapPos();
				ImGui.EndTooltip();
			}
			ImGui.SameLine();
			if ( ImGui.Button("Extract folder") ) ExtractSelected(folder: true);
			ImGui.Separator();

			if ( previewPath != selectedPath ) {
				ImGui.TextUnformatted("Loading preview ...");
			} else if ( previewError != null ) {
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted($"No preview: {previewError}");
				ImGui.PopTextWrapPos();
			} else if ( previewText != null ) {
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted(previewText);
				ImGui.PopTextWrapPos();
			} else if ( preview != null ) {
				ImGui.TextUnformatted($"{preview.Width} x {preview.Height}");
				// Fit the image into the pane, but never upscale past 1:1.
				float avail = Math.Max(64f, ImGui.GetContentRegionAvail().X);
				float scale = Math.Min(1f, avail / Math.Max(1, preview.Width));
				ImGui.Image(preview.Id, new Vector2(preview.Width * scale, preview.Height * scale));
			}
		}

		#endregion

		#region Selection and preview

		private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"txt", "csv", "json", "xml", "hlsl", "fx", "it", "ot", "otc", "ao", "aoc", "arm", "act", "env", "mat",
			"mtd", "ddt", "sm", "tsi", "tgt", "trl", "atlas", "cht", "clt", "dlp", "ecf", "epk", "et", "ffx",
			"fmt", "gft", "gt", "idl", "idt", "mtp", "pet", "red", "rs", "rts", "slt", "ui", "ult", "properties",
		};

		private void Select(string path) {
			var idx = index;
			if ( idx == null ) return;
			selectedPath = path;
			selectedInfo = idx.TryGetFile(path, out BundleIndex.FileRecord record)
				? $"{record.Size:N0} bytes, bundle {record.BundleIndex} (+{record.Offset:N0})"
				: "not in the index";
			ClearPreview();
			int token = ++selectionToken;

			var t = new Thread(() => LoadPreview(idx, path, token)) {
				IsBackground = true,
				Name = "AtE BundleBrowser preview",
			};
			t.Start();
		}

		private void LoadPreview(BundleIndex idx, string path, int token) {
			try {
				byte[] bytes = idx.ReadAllBytes(path);
				string extension = Path.GetExtension(path).TrimStart('.');

				if ( DdsImage.TryGetRedirect(bytes, out string redirect) ) {
					// PoE stores some art entries as "*<other path>". Follow it once — and note
					// that the target's own extension is what decides how to read it.
					string next = redirect.Replace('\\', '/').TrimStart('/');
					if ( !idx.TryReadAllBytes(next, out byte[] indirect) ) {
						Publish(token, path, error: $"redirects to \"{redirect}\", which is not in the index");
						return;
					}
					bytes = indirect;
					extension = Path.GetExtension(next).TrimStart('.');
				}

				if ( DdsImage.TryParse(bytes, out DdsImage image, out string error) ) {
					Publish(token, path, image: image);
					return;
				}
				if ( TextExtensions.Contains(extension) ) {
					int take = Math.Min(bytes.Length, 4000);
					string text = System.Text.Encoding.UTF8.GetString(bytes, 0, take);
					if ( text.IndexOf('\0') >= 0 )   // UTF-16, which most of PoE's text files are
						text = System.Text.Encoding.Unicode.GetString(bytes, 0, take & ~1);
					Publish(token, path, text: text);
					return;
				}
				Publish(token, path, error: $"{bytes.Length:N0} bytes, not a previewable image ({error})");
			} catch ( Exception e ) {
				Publish(token, path, error: e.Message);
			}
		}

		private void Publish(int token, string path, DdsImage image = null, string text = null, string error = null) {
			lock ( previewGate ) {
				// Never regress the slot: an older worker finishing after a newer one must not
				// replace its result, or OnTick would drop the newer one on the token check and
				// the pane would sit on "Loading preview ..." with nothing left to deliver it.
				if ( pending == null || pending.Token <= token )
					pending = new PreviewResult(token, path, image, text, error);
			}
		}

		private void Apply(PreviewResult result) {
			previewPath = result.Path;
			previewError = result.Error;
			previewText = result.Text;
			preview = null;
			previewImage = result.Image;
			if ( result.Image == null ) return;

			var image = result.Image;
			try {
				var renderer = Platform.Current.Renderer;
				TextureFormat format = image.Format;
				byte[] data = image.Data;
				int pitch = image.RowPitch;

				// Two reasons the GPU may not take these bytes as they are: the backend cannot
				// sample BC blocks at all, or it can but mip 0 is not a whole number of 4x4
				// blocks (D3D rejects that). Either way, decode to RGBA8 first if we can.
				bool blockCompressed = DdsImage.IsBlockFormat(format);
				bool gpuCanTakeIt = renderer.SupportsBlockCompressedTextures
					&& !(blockCompressed && ((image.Width & 3) != 0 || (image.Height & 3) != 0));
				if ( !gpuCanTakeIt && format != TextureFormat.R8G8B8A8_UNorm ) {
					if ( !image.TryDecodeToRgba(out byte[] rgba) ) {
						previewError = $"{image.Format} at {image.Width}x{image.Height} cannot be uploaded as-is, " +
							"and there is no software decoder for that format (BC6H and the float formats are HDR).";
						return;
					}
					data = rgba;
					format = TextureFormat.R8G8B8A8_UNorm;
					pitch = image.Width * 4;
				}

				// One fixed cache key: each upload replaces the last, so browsing a few hundred
				// files does not leave a few hundred textures on the GPU.
				preview = renderer.CreateTexture(PreviewTextureKey, format, image.Width, image.Height, data, pitch);
			} catch ( Exception e ) {
				preview = null;
				previewError = e.Message;
				Log($"BundleBrowser: preview upload failed for {result.Path}: {e}");
			}
		}

		#endregion

		#region Extraction

		private string ExtractRoot => string.IsNullOrWhiteSpace(ExtractTo)
			? Path.Combine(AppContext.BaseDirectory, "extracted")
			: ExtractTo;

		/// <summary>
		/// Decode the previewed image to pixels and write it out as a .png beside where the raw
		/// file would have gone. Off the render thread — the block decode is CPU work.
		/// </summary>
		private void SaveAsPng(string path, DdsImage image) {
			string root = ExtractRoot;
			var t = new Thread(() => {
				try {
					if ( !image.TryDecodeToRgba(out byte[] rgba) ) {
						status = $"{image.Format} cannot be decoded to pixels, so it cannot be saved as PNG.";
						return;
					}
					string outPath = Path.ChangeExtension(
						Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), ".png");
					PngEncoder.WriteFile(outPath, image.Width, image.Height, rgba);
					status = $"Saved {outPath}";
				} catch ( Exception e ) {
					status = $"Save as PNG failed: {e.Message}";
					Log($"BundleBrowser: PNG conversion failed for {path}: {e}");
				}
			}) {
				IsBackground = true,
				Name = "AtE BundleBrowser png",
			};
			t.Start();
		}

		private void ExtractSelected(bool folder) {
			var idx = index;
			if ( idx == null || selectedPath == null ) return;
			string root = ExtractRoot;
			string path = selectedPath;
			var t = new Thread(() => {
				try {
					if ( folder ) {
						int slash = path.LastIndexOf('/');
						string prefix = slash < 0 ? "" : path.Substring(0, slash);
						int written = idx.ExtractDirectory(prefix, Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar)));
						// Notify() touches ImGui state, so report through status instead — this is a worker thread.
						status = $"Extracted {written} file(s) to {root}";
					} else {
						string outPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
						idx.Extract(path, outPath);
						status = $"Extracted {outPath}";
					}
				} catch ( Exception e ) {
					status = $"Extract failed: {e.Message}";
					Log($"BundleBrowser: extract failed: {e}");
				}
			}) {
				IsBackground = true,
				Name = "AtE BundleBrowser extract",
			};
			t.Start();
		}

		#endregion
	}
}
