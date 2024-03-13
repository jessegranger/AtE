using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE.Plugins {
	class ItemRollingPlugin : PluginBase {

		public override string Name => "Item Rolling";

#if DEBUG
		public override bool Hidden => false;
#else
		public override bool Hidden => true;
#endif

		public HotKey RollOnceKey = new HotKey(Keys.None);

		private HashSet<string> ModNames = new HashSet<string>();
		public string ModTarget = "";

		private string[] RollingMethodNames = new string[] {
			"Alteration Only",
			"Alteration + Augment",
			"Chaos"
		};
		public int CurrentRollingMethod = 0;

		public ItemRollingPlugin() : base() {
			OnAreaChange += (obj, areaName) => {
				RefreshModNames();
			};
		}

		private void RefreshModNames() {
			Log("Parsing Mods.dat into ModNames...");
			ModNames.Clear();
			foreach ( var item in PoEMemory.GetFileContents<Offsets.File_ModsDat_Entry>("Data/Mods.dat") ) {
				if ( IsValid(item.strName) && PoEMemory.TryReadString(item.strName, Encoding.Unicode, out string name) ) {
					if ( IsValid(item.displayName) && PoEMemory.TryReadString(item.displayName, Encoding.Unicode, out string displayName) ) {
						ModNames.Add($"{name} \"{displayName}\" ({item.AffixType})");
					}
				}
			}
		}

		private bool HasExplicitMod(ItemMod mods, Regex pattern) {
			if( !IsValid(mods.Address) ) {
				return false;
			}
			return pattern.IsMatch(mods.DisplayName) || pattern.IsMatch(mods.GroupName);
		}
		private bool HasExplicitMod(Mods mods, Regex pattern) {
			if( !IsValid(mods) ) {
				return false;
			}
			return mods.ExplicitMods.Any((mod) => HasExplicitMod(mod, pattern));
		}
		private bool HasExplicitMod(InventoryItem item, Regex pattern) {
			if( !IsValid(item) ) {
				return false;
			}
			return HasExplicitMod(item?.Entity?.GetComponent<Mods>(), pattern);
		}

		private IState RollForever() {
			return RollOnce(new Delay(300, NewState((self, dt) => RollForever())));
		}

		private IState RollOnce(IState next = null) {
			if( ! BackpackIsOpen() ) {
				Notify("Backpack must be open.", Color.Red);
				return null;
			}
			var topLeft = BackpackItems().Where((item) => item.X == 0 && item.Y == 0).FirstOrDefault();
			if( !IsValid(topLeft) ) {
				Notify("No top-left item found.", Color.Red);
				return null;
			}
			if( (ModTarget?.Length ?? 0) < 1 ) {
				Notify("Cannot roll without a mod pattern to match.", Color.Red);
				return null;
			}
			Notify("Rolling it once...", Color.Yellow);
			switch(CurrentRollingMethod) {
				default:
				case 0: return RollOnce_AlterationOnly(topLeft, next);
				case 1: return RollOnce_AlterationAugment(topLeft, next);
				case 2: return RollOnce_Chaos(topLeft, next);
			}
		}
		
		private IState RollOnce_AlterationOnly(InventoryItem targetItem, IState next = null) {
			if( !IsValid(targetItem) ) {
				Notify("RollOnce_AlterationOnly: targetItem is invalid!", Color.Red);
				return null;
			}
			var regex = new Regex(ModTarget, RegexOptions.IgnoreCase);
			if( HasExplicitMod(targetItem, regex) ) {
				Notify("Success!", Color.Green);
				return null;
			}
			var backpackPlugin = GetPlugin<BackpackPlugin>();
			var coreSettings = GetPlugin<CoreSettings>();
			return backpackPlugin.PlanUseItemFromBackpack(Offsets.PATH_ALTERATION,
				next: new LeftClickAt(targetItem.GetClientRect(), (uint)coreSettings.InputLatency, 1, next)
			);
		}

		private IState RollOnce_AlterationAugment(InventoryItem targetItem, IState next = null) {

			if ( !IsValid(targetItem) ) {
				Notify("RollOnce_AlterationOnly: targetItem is invalid!", Color.Red);
				return null;
			}
			var regex = new Regex(ModTarget, RegexOptions.IgnoreCase);
			if ( HasExplicitMod(targetItem, regex) ) {
				Notify("Success!", Color.Green);
				return null;
			}
			var backpackPlugin = GetPlugin<BackpackPlugin>();
			var coreSettings = GetPlugin<CoreSettings>();
			var mods = targetItem?.Entity?.GetComponent<Mods>();
			if ( IsValid(mods) ) {
				if( mods.Rarity != Offsets.ItemRarity.Magic ) {
					Notify("Can only roll magic items with this method!", Color.Red);
					return null;
				}
				if ( mods.ExplicitMods.Count() == 1 ) {
					return backpackPlugin.PlanUseItemFromBackpack(Offsets.PATH_AUGMENT,
						next: new LeftClickAt(targetItem.GetClientRect(), (uint)coreSettings.InputLatency, 1, next)
					);
				} else {
					return backpackPlugin.PlanUseItemFromBackpack(Offsets.PATH_ALTERATION,
						next: new LeftClickAt(targetItem.GetClientRect(), (uint)coreSettings.InputLatency, 1, next)
					);
				}
			}
			return null;
		}

		private IState RollOnce_Chaos(InventoryItem targetItem, IState next = null) {
			Notify("TODO: Chaos spam support", Color.Red);
			return next;
		}

		public override void Render() {
			// base.Render();
			// ImGui_HotKeyButton("Roll Once", ref RollOnceKey);
			// ImGui.Checkbox("Show Demo Window", ref ShowDemoWindow);

			ImGui.AlignTextToFramePadding();
			ImGui.Text($"Loaded {ModNames.Count} mods from Data/Mods.dat");
			ImGui.SameLine();
			if( ImGui.Button("R##rolling_refresh_button") || ModNames.Count == 0 ) {
				RefreshModNames();
			}
			var topLeft = BackpackItems().Where((item) => item.X == 0 && item.Y == 0).FirstOrDefault();
			ImGui.BeginTable("rolling_table", 2, ImGuiTableFlags.Borders);
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Method:");
			ImGui.TableNextColumn();
			ImGui.SetNextItemWidth(ImGui.CalcTextSize("Alteration + Augment").X + 26);
			ImGui.Combo("##rolling_method_combo", ref CurrentRollingMethod, RollingMethodNames, RollingMethodNames.Length);

			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Step 1.");
			ImGui.TableNextColumn();
			switch( CurrentRollingMethod ) {
				case 2:
					ImGui.Text("Put a rare item in the top left corner of your inventory.");
					break;
				case 1:
				case 0:
					ImGui.Text("Put a magic item in the top left corner of your inventory.");
					break;
			}
			if ( IsValid(topLeft) ) {
				ImGui.Indent();
				ImGui.AlignTextToFramePadding();
				ImGui.Text($"Item: {(IsValid(topLeft) ? topLeft.Entity?.Path : "Invalid")}");
				ImGui.SameLine();
				if( ImGui.Button("R##refresh_components_on_target_item") || true ) {
					topLeft?.Entity?.ClearComponents();
				}
				ImGui.SameLine();
				if( ImGui.Button("B##browse_target_item") ) {
					Run_ObjectBrowser("Target Item", topLeft);
				}
				var mods = topLeft?.Entity?.GetComponent<Mods>();
				if ( IsValid(mods) ) {
					ImGui.Text($"Rarity: {mods.Rarity}");
					ImGui.Text("Current Mods:");
					foreach ( var mod in mods.ExplicitMods ?? Empty<ItemMod>() ) {
						ImGui.Text($"  {mod.GroupName} \"{mod.DisplayName}\" ({mod.AffixType})");
					}
				}
				ImGui.Unindent();
			}

			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Step 2.");
			ImGui.TableNextColumn();
			int alterationCount = 0;
			int augmentCount = 0;
			int chaosCount = 0;
			foreach(var item in BackpackItems() ) {
				var ent = item?.Entity;
				if ( !IsValid(ent) ) {
					continue;
				}
				string path = ent.Path;
				if( !IsValid(path) ) {
					continue;
				}
				if( path.Equals(Offsets.PATH_ALTERATION) ) {
					alterationCount += ent.GetComponent<Stack>()?.CurSize ?? 1;
				} else if( path.Equals(Offsets.PATH_AUGMENT ) ) {
					augmentCount += ent.GetComponent<Stack>()?.CurSize ?? 1;
				} else if( path.Equals(Offsets.PATH_CHAOS) ) {
					chaosCount += ent.GetComponent<Stack>()?.CurSize ?? 1;
				}
			}
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Put crafting currency in your backpack.");
			ImGui.Indent();
			switch ( CurrentRollingMethod ) {
				case 2: // Chaos Spam
					ImGui.Text($"Chaos: {chaosCount}");
					break;
				case 1: // Alteration + Augment
					ImGui.Text($"Augments: {augmentCount}");
					ImGui.Text($"Alterations: {alterationCount}");
					break;
				case 0: // Alteration only
					ImGui.Text($"Alterations: {alterationCount}");
					break;
			}
			ImGui.Unindent();

			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Mod Target:");
			ImGui_HelpMarker("Will roll until a mod matching this Mod Filter is found");
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Search Mods:");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(160);
			ImGui.InputText("##rolling_input", ref ModTarget, 128);
			ImGui.SameLine();
			ImGui_HelpMarker("Regex Help:\n   .* match anything\n   ^ match only at the start\n   $ match only at the end.\nExample: \"^Item.*Rarity\"");
			if( (ModTarget?.Length ?? 0) > 0 ) {
				ImGui.Text("Example matches for filter: \"" + ModTarget + "\"");
				ImGui.Indent();
				int limit = 200;
				try {
					var regex = new Regex(ModTarget, RegexOptions.IgnoreCase);
					string[] modNames = ModNames.Where((name) => regex.IsMatch(name)).Take(limit).ToArray();
					for ( int i = 0; i < modNames.Length; i++ ) {
						ImGui.Text($"{modNames[i]}");
					}
				} catch( ArgumentException e ) {
					ImGui.Text("Invalid Regex: " + e.Message);
				}
				ImGui.Unindent();
			}
			ImGui_HelpMarker("You must verify yourself that some of the above matches are relevant to your target item.");
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.Text("Status:");
			ImGui.TableNextColumn();
			ImGui.AlignTextToFramePadding();
			if ( (ModTarget?.Length ?? 0) > 0 ) {
				try {
					var regex = new Regex(ModTarget, RegexOptions.IgnoreCase);
					ImGui.Text($"Target Mod: {ModTarget} == {HasExplicitMod(topLeft, regex)}");
				} catch( ArgumentException e ) {
					ImGui.Text($"Target Mod: false");
				}
			}
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.TableNextColumn();
			ImGui_HotKeyButton("Start Rolling", ref RollOnceKey);
			if( RollOnceKey.IsReleased ) {
				Run(RollForever());
			}
			ImGui.EndTable();
		}

		private string filter = "";

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled ) return this;
			var ui = GetUI();
			if ( false ) {
				ImGui.Begin("Mods.dat");
				ImGui.InputText("filter", ref filter, 32);
				int limit = 20;
				ImGui.Text("filter: " + filter);
				foreach(var name in ModNames) {
					if( filter == null || filter.Length == 0 || name.Contains(filter) ) {
						ImGui.Text(name);
						if ( --limit <= 0 ) break;
					}
				}
				ImGui.End();
			}
			if ( false ) {
				ImGui.Begin("StashElement.VisibleItems");
				foreach ( InventoryItem item in ui.StashInventory.VisibleItems ) {
					var ent = item?.Entity;
					if ( IsValid(ent) ) {
						ImGui.Text(ent.Path);
						ImGui.SameLine();
						if ( ImGui.Button($"B##{ent.Id}") ) {
							Run_ObjectBrowser($"StashElement {ent.Id}", item);
						}
						var r = item.GetClientRect();
						// only if its not toggled open, hover should highlight the element's rect
						if ( IsValid(item) && ImGui.IsItemHovered() && (r.Width * r.Height) > 0 ) {
							DrawFrame(r, Color.Yellow, 3);
						}
					}
				}
			}
			ImGui.End();

			return base.OnTick(dt);
		}

	}
}
