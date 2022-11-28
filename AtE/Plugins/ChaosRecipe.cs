using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE {
	public class ChaosRecipe : PluginBase {

		public override string Name => "Chaos Recipe";

		public bool ShowChaosRecipeItems = false;

		/// <summary>
		/// This is run every frame.
		/// </summary>
		/// <param name="dt">duration of this frame, in ms</param>
		/// <returns>This plugin, or another IState to replace it.</returns>
		public override IState OnTick(long dt) {
			if ( Enabled && !Paused && PoEMemory.IsAttached && (PoEMemory.TargetHasFocus || Overlay.HasFocus)) {
				var ui = GetUI();
				if( IsValid(ui) ) {
					var elem = ui.StashElement;
					if( elem?.IsVisibleLocal ?? false ) {
						string text = "Highlight Chaos Recipe";
						var textSize = ImGui.CalcTextSize(text);
						var mainBody = elem.GetChild(2);
						if ( IsValid(mainBody) ) {
							var mainRect = mainBody.GetChild(0)?.GetClientRect() ?? default;
							ImGui.SetNextWindowBgAlpha(0.0f);
							ImGui.SetNextWindowPos(new Vector2(mainRect.X, mainRect.Y + mainRect.Height + textSize.Y + 24));
							ImGui.SetNextWindowSize(new Vector2(0f, 0f));
							if ( ImGui.Begin("Chaos Recipe", ref Enabled, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNavFocus) ) {
								ImGui.Checkbox(text, ref ShowChaosRecipeItems);
								// ImGui.SameLine();
								// ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
								// if( ImGui.Button("X##DisableChaosRecipe") ) {
								// Enabled = false;
								// }
								if ( ShowChaosRecipeItems ) {
									int count = 0;
									bool recipeComplete = false;
									bestGlove = null;
									gloveCount = 0;
									bestBoot = null;
									bootCount = 0;
									bestChest = null;
									chestCount = 0;
									bestWeapon = null;
									weaponCount = 0;
									bestOffhand = null;
									offhandCount = 0;
									bestHelm = null;
									helmCount = 0;
									bestAmulet = null;
									amuletCount = 0;
									bestLeftRing = null;
									bestRightRing = null;
									ringCount = 0;
									bestBelt = null;
									beltCount = 0;

									foreach ( var item in BackpackItems() ) {
										Add(item);
										count += 1;
									}

									foreach ( var item in StashItems() ) {
										Add(item);
										count += 1;
									}

									if ( hasOneBelow74 ) {
										string needs = "Needed: ";
										if ( weaponCount < 1 ) needs += "Weapon ";
										if ( offhandCount < 1 ) needs += "Offhand ";
										if ( helmCount < 1 ) needs += "Helmet ";
										if ( chestCount < 1 ) needs += "Chest ";
										if ( amuletCount < 1 ) needs += "Amulet ";
										if ( ringCount < 2 ) needs += "Rings ";
										if ( gloveCount < 1 ) needs += "Gloves ";
										if ( bootCount < 1 ) needs += "Boots";
										if ( beltCount < 1 ) needs += "Belts";
										if ( !needs.Equals("Needed: ") ) {
											ImGui.Text(needs);
										} else {
											recipeComplete = true;
										}

										HighlightItem(bestWeapon, "Weapon");
										HighlightItem(bestOffhand, "Offhand");
										HighlightItem(bestHelm, "Helm");
										HighlightItem(bestBelt, "Belt");
										HighlightItem(bestChest, "Chest");
										HighlightItem(bestAmulet, "Amulet");
										HighlightItem(bestRightRing, "Ring");
										HighlightItem(bestLeftRing, "Ring");
										HighlightItem(bestGlove, "Glove");
										HighlightItem(bestBoot, "Boot");

										ImGui.SameLine();
										if( recipeComplete && ImGui.Button($"Pickup##PickupOneSetOfChaosRecipeItems") ) {
											uint inputDelay = (uint)GetPlugin<CoreSettings>().InputLatency;
											Run(new KeyDown(Keys.LControlKey,
												new LeftClickAt(bestWeapon?.GetClientRect() ?? default, inputDelay, 1,
												new LeftClickAt(bestOffhand?.GetClientRect() ?? default, inputDelay, 1,
												new LeftClickAt(bestHelm.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestBelt.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestChest.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestAmulet.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestRightRing.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestLeftRing.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestGlove.GetClientRect(), inputDelay, 1,
												new LeftClickAt(bestBoot.GetClientRect(), inputDelay, 1,
												new KeyUp(Keys.LControlKey)
												))))))))))));
										}
									}
								}

							}
						}

						ImGui.End(); // end the Chaos Recipe window
					}
				}
			}
			return this;
		}
		bool hasOneBelow74 = false;
		InventoryItem bestGlove = null;
		uint gloveCount = 0;
		InventoryItem bestBoot = null;
		uint bootCount = 0;
		InventoryItem bestChest = null;
		uint chestCount = 0;
		InventoryItem bestWeapon = null;
		uint weaponCount = 0;
		InventoryItem bestOffhand = null;
		uint offhandCount = 0;
		InventoryItem bestHelm = null;
		uint helmCount = 0;
		InventoryItem bestAmulet = null;
		uint amuletCount = 0;
		InventoryItem bestLeftRing = null;
		InventoryItem bestRightRing = null;
		uint ringCount = 0;
		InventoryItem bestBelt = null;
		uint beltCount = 0;
		private void Add(InventoryItem item) {
			// at least 1 item 60 to 74
			// rest of the items 60 to 100
			var ent = item.Entity;
			if ( !IsValid(ent) ) {
				return;
			}

			var mods = ent.GetComponent<Mods>();
			if ( !IsValid(mods) ) {
				return;
			}

			if ( mods.IsIdentified ) {
				return;
			}

			if ( mods.Rarity != Offsets.ItemRarity.Rare ) {
				return;
			}

			if ( mods.Level < 60 ) {
				return;
			}

			string[] path = ent.Path.Split('/');
			bool has2HWeap = IsValid(bestWeapon) && !IsOneHanded(bestWeapon.Entity);

			switch ( path[2] ) {
				case "Weapons":
					weaponCount += 1;
					switch ( path[3] ) {
						case "OneHandWeapons":
							if ( has2HWeap ) {
								break;
							}
							if ( !IsValid(bestWeapon) || (mods.Level < GetItemLevel(bestWeapon)) ) {
								bestWeapon = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestWeapon) < 75;
							} else if ( !IsValid(bestOffhand) || mods.Level < GetItemLevel(bestOffhand) ) {
								bestOffhand = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestOffhand) < 75;
							}
							break;
						case "TwoHandWeapons":
							if ( !IsValid(bestWeapon) || IsOneHanded(bestWeapon.Entity) || mods.Level < GetItemLevel(bestWeapon) ) {
								bestWeapon = item;
								bestOffhand = null;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestWeapon) < 75;
							}
							break;
					}
					break;
				case "Armours":
					switch ( path[3] ) {
						case "BodyArmours":
							chestCount += 1;
							if ( mods.Level < GetItemLevel(bestChest?.Entity, 100) ) {
								bestChest = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestChest) < 75;
							}
							break;
						case "Boots":
							bootCount += 1;
							if ( mods.Level < GetItemLevel(bestBoot?.Entity, 100) ) {
								bestBoot = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestBoot) < 75;
							}
							break;
						case "Gloves":
							gloveCount += 1;
							if ( mods.Level < GetItemLevel(bestGlove?.Entity, 100) ) {
								bestGlove = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestGlove) < 75;
							}
							break;
						case "Helmets":
							helmCount += 1;
							if ( mods.Level < GetItemLevel(bestHelm?.Entity, 100) ) {
								bestHelm = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestHelm) < 75;
							}
							break;
						case "Shields":
							offhandCount += 1;
							if ( has2HWeap ) {
								break;
							}
							if ( mods.Level < GetItemLevel(bestOffhand?.Entity, 100) ) {
								bestOffhand = item;
								hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestOffhand) < 75;
							}
							break;
					}
					break;
				case "Rings":
					ringCount += 1;
					if ( mods.Level < GetItemLevel(bestLeftRing?.Entity, 100) ) {
						bestLeftRing = item;
						hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestLeftRing) < 75;
					} else if ( mods.Level < GetItemLevel(bestRightRing?.Entity, 100) ) {
						bestRightRing = item;
						hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestRightRing) < 75;
					}
					break;
				case "Amulets":
					amuletCount += 1;
					if ( mods.Level < GetItemLevel(bestAmulet?.Entity, 100) ) {
						bestAmulet = item;
						hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestAmulet) < 75;
					}
					break;
				case "Belts":
					beltCount += 1;
					if ( mods.Level < GetItemLevel(bestBelt?.Entity, 100) ) {
						bestBelt = item;
						hasOneBelow74 = hasOneBelow74 || GetItemLevel(bestBelt) < 75;
					}
					break;
			}
		}

		private static void HighlightItem(InventoryItem item, string label) {
			if ( IsValid(item) ) {
				var rect = item.GetClientRect();
				DrawFrame(rect, Color.Yellow);
				DrawTextAt(Center(rect), label, Color.Yellow);
			}
		}

	}
}
