using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE.Plugins {

	public class MinimapIconPlugin : PluginBase {

		public override string Name => "Minimap Icons";

		public override int SortIndex => 1;

		public bool ShowMinions = true;
		public bool ShowEnemies = true;
		public bool ShowEnemyRares = true;
		public bool ShowEnemyMagic = true;
		public bool ShowEnemyNormal = true;
		public bool ShowChests = true;
		public bool ShowDelvePath = true;
		public bool ShowLeagueNPCs = true;
		public bool ShowLeagueResources = true;
		public bool DebugUnknownResources = false;
		public bool DebugMonsters = false;
		public bool DebugChests = false;

		public bool ShowRareOverhead = true;

		public bool ShowOnMinimap = true;
		public bool ShowOnLargemap = true;

		public int IconSize = 10;

		public override void Render() {
			base.Render();
			var ui = GetUI();
			var map = ui.Map;
			bool largeMapVisible = map.LargeMap?.IsVisibleLocal ?? false;
			bool miniMapVisible = map.MiniMap?.IsVisibleLocal ?? false;
			ImGui.Checkbox("Show Icons on Mini Map", ref ShowOnMinimap);
			if( miniMapVisible ) {
				ImGui.SameLine(); ImGui.TextDisabled("(visible)");
			}
			ImGui.Checkbox("Show Icons on Large Map", ref ShowOnLargemap);
			if( largeMapVisible ) {
				ImGui.SameLine(); ImGui.TextDisabled("(visible)");
			}
			ImGui.SliderInt("Icon Size", ref IconSize, 5, 20);
			ImGui.Separator();
			ImGui.Checkbox("Show Enemies", ref ShowEnemies);
			ImGui.Indent();
				ImGui.Checkbox("Show Rares", ref ShowEnemyRares);
				ImGui.Checkbox("Show Magic", ref ShowEnemyMagic);
				ImGui.Checkbox("Show Normal", ref ShowEnemyNormal);
				ImGui.Checkbox("Also Over Head", ref ShowRareOverhead);
			ImGui.Unindent();
			ImGui.Checkbox("Show Minions", ref ShowMinions);
			ImGui.Checkbox("Show Strongboxes", ref ShowChests);
			ImGui.Checkbox("Show Delve Path", ref ShowDelvePath);
			ImGui.Checkbox("Show League NPCs", ref ShowLeagueNPCs);
			ImGui.Indent();
				ImGui.Checkbox("Show League Resources", ref ShowLeagueResources);
				ImGui.Checkbox("Debug Unknown Resources", ref DebugUnknownResources);
				ImGui.Checkbox("Debug Monsters", ref DebugMonsters);
				ImGui.Checkbox("Debug Chests", ref DebugChests);
			ImGui.Unindent();
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached ) {
				return this;
			}

			var ui = GetUI();
			if ( !IsValid(ui) ) {
				DrawBottomLeftText("MinimapIcon: invalid UI", Color.Yellow);
				return this;
			}

			var map = ui.Map;
			if ( !IsValid(map) ) {
				DrawBottomLeftText("MinimapIcon: invalid Map", Color.Yellow);
				return this;
			}
			bool largeMapVisible = map.LargeMap?.IsVisibleLocal ?? false;

			if ( largeMapVisible && !ShowOnLargemap ) {
				DrawBottomLeftText("MinimapIcon: large map not visible", Color.Yellow);
				return this;
			}

			bool smallMapVisible = map.MiniMap?.IsVisibleLocal ?? false;

			if ( smallMapVisible && !ShowOnMinimap ) {
				DrawBottomLeftText("MinimapIcon: small map not visible", Color.Yellow);
				return this;
			}

			if( !( ShowChests || ShowEnemies || ShowMinions || ShowDelvePath )) { // nothing to show
				return this;
			}

			var player = GetPlayer();
			if( !IsValid(player) ) {
				DrawBottomLeftText("MinimapIcon: invalid player", Color.Yellow);
				return this;
			}

			uint[] deployed = null;
			if ( ShowMinions ) { // get this list ahead of time so we can only iterate GetEntities once
				deployed = (GetPlayer()?.GetComponent<Actor>()?.DeployedObjects.Select(d => d.EntityId) ?? Empty<uint>()).ToArray();
			}
			// debug: var distinctPaths = new Dictionary<string, Entity>();

			foreach ( var ent in GetEntities().Take(2000).Where(IsValid) ) {

				// each entity picks an icon to display
				SpriteIcon icon = SpriteIcon.None;
				float iconSize = 1f;

				var path = ent.Path;
				if ( path == null ) {
					continue;
				}
				/* debug: unknown objects in the area
				if( ! distinctPaths.ContainsKey(path) ) {
					distinctPaths.Add(ent.Path, ent);
				}
				*/

				if ( DebugMonsters && path.StartsWith("Metadata/Monster", StringComparison.Ordinal)
					&& IsHostile(ent)
					&& IsTargetable(ent)
					&& IsAlive(ent)
					// && ent.HasComponent<MinimapIcon>()
					) {
					ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
					ImGui.Begin($"ent:{ent.Id} {ent.Path}");
					ImGui_Object($"{ent.Id}", $"{ent.Id}", ent, new HashSet<int>());
					// ImGui.Text($"IsAlive: {IsAlive(ent)}");
					// ImGui.Text($"IsHostile: {IsHostile(ent)}");
					// ImGui.Text($"IsTargetable: {IsTargetable(ent)}");
					// var minimapIcon = ent.GetComponent<MinimapIcon>();
					// ImGui.Text($"Icon IsVisible: {minimapIcon.IsVisible}");
					// ImGui.Text($"Icon IsHidden: {minimapIcon.IsHide}");
					// ImGui_Object($"MinimapIcon-{ent.Id}", $"MinimapIcon", minimapIcon, new HashSet<int>());
					// ImGui.Text("Positioned");
					// ImGui_Object($"Positioned-{ent.Id}", "Positioned", ent.GetComponent<Positioned>(), new HashSet<int>());
					// ImGui.Text("Render");
					// ImGui_Object($"Render-{ent.Id}", "Render", ent.GetComponent<Render>(), new HashSet<int>());
					// ImGui.Text("ObjectMagicProperties");
					// ImGui_Object($"ObjectMagicProperties-{ent.Id}", "ObjectMagicProperties", ent.GetComponent<ObjectMagicProperties>(), new HashSet<int>());
					ImGui.End();
				}
				if ( DebugChests && path.StartsWith("Metadata/Chest", StringComparison.Ordinal) ) {
					ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
					ImGui.Begin($"Unknown Box##{ent.Id}");
					ImGui_Object($"Box##{ent.Id}", "Box", ent, new HashSet<int>());
					ImGui.Text("Chest:");
					ImGui_Object("$Chest-##{ent.Id}", "Chest", ent.GetComponent<Chest>(), new HashSet<int>());
					ImGui.End();
				}
				if( ent.MinimapIcon.Expires < Time.ElapsedMilliseconds ) {
					ent.MinimapIcon = new Entity.Icon() { Size = 0f, Sprite = SpriteIcon.None };
				}

				if ( path.EndsWith("FuelResupply", StringComparison.Ordinal) ) {
					DrawLine(WorldToScreen(Position(GetPlayer())), WorldToScreen(Position(ent)), Color.LightGreen);
				}

				if ( ent.MinimapIcon.Size > 0f ) {
					icon = ent.MinimapIcon.Sprite;
					iconSize = ent.MinimapIcon.Size;
				} else if ( ShowMinions && deployed.Contains((ushort)ent.Id) ) {
					TryGetMinionIcon(ent, out icon, out iconSize);
				} else if ( ShowLeagueNPCs && path.StartsWith("Metadata/NPC/League/", StringComparison.Ordinal) ) {
					if( path.Contains("Azmeri") || path.Contains("Affliction") ) {
						icon = SpriteIcon.YellowExclamation;
						iconSize = 1.5f;
					}
				} else if ( ShowLeagueResources && path.StartsWith("Metadata/MiscellaneousObjects/Azmeri", StringComparison.Ordinal) ) {
					TryGetLeagueResourceIcon(ent, out icon, out iconSize);
				} else if ( ShowLeagueResources && path.StartsWith("Metadata/Terrain/Leagues/", StringComparison.Ordinal) ) {
					TryGetLeagueResourceIcon(ent, out icon, out iconSize);
					// icon = SpriteIcon.GreenExclamation;
					// iconSize = 1.25f;
					// DrawBottomLeftText($"Unknown terrain: {ent.Path} components: {string.Join(" ", ent.GetComponents().Keys)}", Color.Yellow);
				} else if ( ShowEnemies && path.StartsWith("Metadata/Monster", StringComparison.Ordinal) && IsAlive(ent) && IsHostile(ent) && IsTargetable(ent) ) {
					TryGetEnemyIcon(ent, out icon, out iconSize);
				} else if ( ShowChests && dt < 33 && path.StartsWith("Metadata/Chest", StringComparison.Ordinal) ) {
					TryGetChestIcon(ent, out icon, out iconSize);
				} else if ( ShowDelvePath && path.EndsWith("DelveLight", StringComparison.Ordinal) ) {
					icon = SpriteIcon.BlightPath;
					iconSize = 1f;
				}

				if ( icon != SpriteIcon.None ) {
					var iconPos = map.WorldToMap(ent);
					DrawSprite(icon, iconPos, IconSize * iconSize, IconSize * iconSize);
					// DrawBottomLeftText($"Drawing Sprite {icon} at {iconPos}", Color.Yellow);
				}
			}

			/* debug: how to find unknown objects in the area
			ImGui.Begin("Paths");
			foreach(var pair in distinctPaths) {
				ImGui.Text(pair.Key);
			}
			ImGui.End();
			*/

			return this;
		}
		
		private bool TryGetLeagueResourceIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.None;
			iconSize = 1f;
			string path = ent.Path;
			if ( ! IsValid(path, 1) ) {
				return false;
			}
			if ( path.EndsWith("FuelResupply", StringComparison.Ordinal) ) {
				icon = SpriteIcon.LargeGreenCircle;
				iconSize = 1.1f;
			} else if ( path.Contains("Azmeri/SacrificeAltar") ) {
				icon = SpriteIcon.RedFlag;
			} else if ( path.Contains("Azmeri/AzmeriFlaskRefill") ) {
				icon = SpriteIcon.GreenFlag;
			} else if ( path.EndsWith("Azmeri/AzmeriResourcePrimalist", StringComparison.Ordinal) ) {
				icon = SpriteIcon.BlightPath;
				iconSize = 2.9f;
			} else if ( path.EndsWith("Azmeri/AzmeriResourceWarden", StringComparison.Ordinal) ) {
				icon = SpriteIcon.BlightPath;
				iconSize = 2.9f;
			} else if ( path.EndsWith("Azmeri/AzmeriResourceVoodoo", StringComparison.Ordinal) ) {
				icon = SpriteIcon.BlightPath;
				iconSize = 2.9f;
			} else if ( path.EndsWith("Azmeri/AzmeriResourceWardenLow", StringComparison.Ordinal) ) {
				icon = SpriteIcon.BlightPath;
				iconSize = 1.9f;
			} else if ( path.EndsWith("Azmeri/AzmeriResourcePrimalistLow", StringComparison.Ordinal) ) {
				icon = SpriteIcon.BlightPath;
				iconSize = 1.9f;
			} else if ( path.Contains("Azmeri/AzmeriResourceVoodooLow") ) {
				icon = SpriteIcon.BlightPath;
				iconSize = 1.9f;
			} else if ( path.EndsWith("AzmeriResourceBase", StringComparison.Ordinal) ) {
				icon = SpriteIcon.BlightPath;
			} else if ( path.EndsWith("Spawners/WolfSpawner", StringComparison.Ordinal) || path.EndsWith("SpiderEmergeSpawner", StringComparison.Ordinal) ) {
				icon = SpriteIcon.RedX;
				iconSize = 1.4f;
			} else if ( path.Contains("Azmeri/Objects/Spawners/") && (path.Contains("/Harvest") || path.Contains("/Beyond_")) ) {
				icon = SpriteIcon.GreenExclamation;
				iconSize = 1.4f;
			} else if ( path.Contains("Azmeri/WoodsEntrance") || path.Contains("AzmeriLightBomb") ) {
				icon = SpriteIcon.None;
			} else if ( path.EndsWith("IntroTunnelPath", StringComparison.Ordinal) ) {
				icon = SpriteIcon.GreenFlag;
			} else if ( path.EndsWith("GoldenLantern", StringComparison.Ordinal) ) {
				icon = SpriteIcon.YellowExclamation;
				iconSize = 1.75f;
			} else if ( path.EndsWith("Encounter", StringComparison.Ordinal) ) {
				icon = SpriteIcon.GreenExclamation;
				iconSize = 1.75f;
			} else if ( path.Contains("ResourceChest") || path.EndsWith("PlacementController", StringComparison.Ordinal) || path.EndsWith("Pointer", StringComparison.Ordinal) || path.EndsWith("Lantern", StringComparison.Ordinal) || path.EndsWith("AnchorHigh", StringComparison.Ordinal) ) {
				icon = SpriteIcon.None;
			} else if ( DebugUnknownResources ) {
				DrawBottomLeftText($"Unknown Terrain: {ent.Path}", Color.Yellow);
				DrawTextAt(ent, ent.Path, Color.White);
				icon = SpriteIcon.GreenExclamation;
				iconSize = 1.25f;
			}
			return icon != SpriteIcon.None;
		}

		// Chest paths that never get an icon; compiled once, on first use, and reused for the life of the process.
		private static readonly Regex IgnoredChestPattern = new Regex(
			"^Metadata/Chests/(?:Urn|Basket|Barrel|Chest|SilverChest|PrimevalChest|StatueMakers|.*Rack|Amphora|.*Pot|.*Boulder|Vase|MapChest|.*Cairn|Crate|Cannibal|TemplarChest|InfestationEgg|.*Barrel|.*Wounded|FungalBloom|GoldenChest|KaomChest|Cacoon|.*Bundle|PordWounded|Tutorial|TribalChest|.*BonePile|Sarcophagi|GoldPot|CopperChest|Labyrinth/Izaro|VaalBoneChest|Betrayal|Laboratory/|LegionChests|Blight)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private bool TryGetChestIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.None;
			iconSize = 1f;
			string iconText = null;
			if ( ent.HasComponent<MinimapIcon>() ) {
				var minimapIcon = ent.GetComponent<MinimapIcon>();
				if( IsValid(minimapIcon) && IsValid(minimapIcon.Name, 4) ) {
					return false;
				}
			}
			var chest = ent.GetComponent<Chest>();
			if ( chest?.IsOpened ?? true ) {
				return false;
			}
			if( ent.MinimapIcon.Size != 0f ) {
				icon = ent.MinimapIcon.Sprite;
				iconSize = ent.MinimapIcon.Size;
				return icon != SpriteIcon.None;
			}
			var path = ent.Path;
			if( !IsValid(path, 10) ) {
				return false;
			}
			if( IgnoredChestPattern.IsMatch(path) ) {
				// set them to None, and never check them again
				ent.MinimapIcon = new Entity.Icon() { Size = 1f, Sprite = SpriteIcon.None };
				return false;
			}
			// DrawTextAt(WorldToScreen(Position(ent)), $"{ent.Path}", Color.White);
			icon = SpriteIcon.None; // with size = 1f and Icon = None, we only fall through this path scanning once, then assign to ent.MinimapIcon
			iconSize = 1f; // once that assigns 1f, the next frame will hit the branch above and return ent.MinimapIcon values
			if ( path.StartsWith("Metadata/Chests", StringComparison.Ordinal) ) {
				// DrawTextAt(WorldToScreen(Position(ent)), $"Unknown /Chest: {ent.Path}", Color.White);
				if ( path.StartsWith("Metadata/Chests/QuestChests", StringComparison.Ordinal) ) {
					return false;
				} else if ( path.StartsWith("Metadata/Chests/LeaguesExpedition/", StringComparison.Ordinal) ) {
					icon = SpriteIcon.BlueFlag;
					iconSize = 1.1f;
				} else if ( path.StartsWith("Metadata/Chests/LeagueAzmeri/", StringComparison.Ordinal) ) {
					icon = SpriteIcon.BlueFlag;
					iconSize = 1.1f;
				} else if ( path.StartsWith("Metadata/Chests/LeagueAbyss/", StringComparison.Ordinal) ) {
					iconSize = 2f;
					if ( path.EndsWith("Armour", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardArmour;
					} else if ( path.EndsWith("Currency", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardCurrency;
					} else {
						icon = SpriteIcon.None;
						iconText = path;
					}
				
				} else if ( path.StartsWith("Metadata/Chests/LeagueDeepwater/", StringComparison.Ordinal)) {
					iconSize = 2f;
					if ( path.EndsWith("UniqueArmour", StringComparison.Ordinal) || path.EndsWith("UniqueWeapons", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardUniques;
					} else if ( path.EndsWith("Shield", StringComparison.Ordinal) || path.EndsWith("Gloves", StringComparison.Ordinal) || path.EndsWith("Boots", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardArmour;
					} else if ( path.EndsWith("Weapon", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardWeapons;
					} else if ( path.EndsWith("Jewellery", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardJewellery;
					} else if ( path.EndsWith("Maps", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardMaps;
					} else if ( path.EndsWith("Decks", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardDivinationCards;
					} else if ( path.EndsWith("Scarabs", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardScarabs;
					} else if ( path.EndsWith("BottledItemChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.LargeYellowStar;
					} else if ( path.EndsWith("CurrencyTreasureChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardCurrency;
					} else if ( path.EndsWith("ClamTreasureChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.LargeYellowCross;
						iconSize = 1.75f;
					} else if ( path.EndsWith("GoldTreasureChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.Gold;
						iconSize = 1.95f;
					} else if ( path.EndsWith("CoralChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.None;
					} else if ( path.EndsWith("CursedTreasureChestEncounter", StringComparison.Ordinal) ) {
						icon = SpriteIcon.None;
					} else if ( path.EndsWith("BrinerotStoresChestEncounter", StringComparison.Ordinal) ) {
						icon = SpriteIcon.None;
					} else if ( path.Contains("GemcuttersChest") ) {
						icon = SpriteIcon.RewardGems;
					} else {
						icon = SpriteIcon.RewardGenericItems;
						iconText = path;
					}
				
				} else if ( path.StartsWith("Metadata/Chests/CocoonRot", StringComparison.Ordinal) ) {
					icon = SpriteIcon.MediumGreenStar;
					iconSize = 1.1f;
				} else if ( path.Contains("Breach/BreachBoxChest") ) {
					icon = SpriteIcon.Breach;
					iconSize = 1.5f;
				} else if ( path.Contains("SideArea/SideAreaChest") ) {
					icon = SpriteIcon.VaalSideArea;
					iconSize = 1.5f;
				} else if ( path.Contains("Faridun/Minor") ) {
					icon = SpriteIcon.RewardGenericItems;
					iconSize = 1.5f;
				} else if ( path.Contains("Faridun/Wish") ) {
					icon = SpriteIcon.RewardProphecy;
					iconSize = 1.75f;
				} else if ( path.Contains("Abyss/AbyssFinal") ) {
					icon = SpriteIcon.RewardAbyss;
					iconSize = 1.75f;
				} else if ( path.Contains("Chests/AbyssChest") ) {
					icon = SpriteIcon.RewardAbyss;
					iconSize = 1.25f;
				} else if ( path.EndsWith("/BootyChest", StringComparison.Ordinal) ) {
					icon = SpriteIcon.YellowExclamation;
					iconSize = 1.75f;
				} else if ( path.StartsWith("Metadata/Chests/DelveChests/", StringComparison.Ordinal) ) {
					if ( path.EndsWith("/PathWeapon", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardWeapons;
						iconSize = 1.75f;
					} else if ( path.Contains("/DelveAzuriteVeinEncounter") ) {
						icon = SpriteIcon.None;
						iconSize = 1f;
					} else if ( path.Contains("AzuriteVein") ) {
						icon = SpriteIcon.SmallBlueTriangle;
						iconSize = 1.25f;
					} else if ( path.Contains("Currency") ) {
						icon = SpriteIcon.RewardCurrency;
						iconSize = 1.75f;
					} else if ( path.Contains("Trinkets") ) {
						icon = SpriteIcon.RewardJewellery;
						iconSize = 1.75f;
					} else if ( path.Contains("Weapon") ) {
						icon = SpriteIcon.RewardWeapons;
						iconSize = 1.75f;
					} else if ( path.Contains("Resonator") ) {
						icon = SpriteIcon.PCMapArrow;
						iconSize = 1.75f;
					} else if ( path.Contains("Fossil") ) {
						icon = SpriteIcon.RewardFossils;
						iconSize = 1.85f;
					} else if ( path.EndsWith("Essence", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardEssences;
						iconSize = 1.95f;
					} else if ( path.Contains("Offering") ) {
						icon = SpriteIcon.RewardLabyrinth;
						iconSize = 1.75f;
					} else if ( path.EndsWith("ShaperItem", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 1.75f;
					} else if ( path.Contains("Flares") ) {
						icon = SpriteIcon.SmallYellowTriangle;
						iconSize = 1.75f;
					} else if ( path.Contains("SuppliesDynamite") ) {
						icon = SpriteIcon.SmallRedTriangle;
						iconSize = 1.75f;
					}
				} else if ( path.StartsWith("Metadata/Chests/IncursionChest", StringComparison.Ordinal) ) {
					icon = SpriteIcon.RewardGenericItems;
					iconSize = 1.75f;
					if ( path.Contains("Currency") ) {
						icon = SpriteIcon.RewardCurrency;
					}
				} else if ( path.StartsWith("Metadata/Chests/LeagueSanctum", StringComparison.Ordinal) ) {
					icon = SpriteIcon.RewardGenericItems;
					iconSize = 1.75f;
				} else if ( path.StartsWith("Metadata/Chests/LeagueHeist", StringComparison.Ordinal) ) {
					if ( path.Contains("PrimaryTarget") ) {
						icon = SpriteIcon.None;
						iconSize = 0f;
					}
				} else if ( path.StartsWith("Metadata/Chests/Labyrinth", StringComparison.Ordinal) ) {
					if ( path.EndsWith("TrinketChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.Labyrinth;
						iconSize = 1.7f;
					}
				} else if ( path.StartsWith("Metadata/Chests/SynthesisChests", StringComparison.Ordinal) ) {
					icon = SpriteIcon.LargeWhiteStar;
					iconSize = 1.5f;
				} else if ( path.StartsWith("Metadata/Chests/StrongBox", StringComparison.Ordinal) ) {
					if ( path.EndsWith("/Arcanist", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardCurrency;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/Jeweller", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardJewellery;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/Strongbox", StringComparison.Ordinal) || path.EndsWith("/Large", StringComparison.Ordinal) || path.EndsWith("/Ornate", StringComparison.Ordinal) || path.EndsWith("/Artisan", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/Armory", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardArmour;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/StrongboxScarab", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardScarabs;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/StrongboxDivination", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardDivinationCards;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/Arsenal", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardWeapons;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/Gemcutter", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardGems;
						iconSize = 2.95f;
					} else if ( path.Contains("/Cartographer") ) {
						icon = SpriteIcon.RewardMaps;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/VaultsOfAtziriUniqueChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardFragments;
						iconSize = 2.95f;
					} else if ( path.EndsWith("/VaalTempleChest", StringComparison.Ordinal) ) {
						icon = SpriteIcon.RewardGenericItems;
						iconSize = 2.95f;
					} else {
						DrawTextAt(WorldToScreen(Position(ent)), $"Unknown Strongbox: {ent.Path}", Color.White);
						return false;
					}
				} else {
					DrawTextAt(WorldToScreen(Position(ent)), $"Unknown /Chest: {ent.Path}", Color.White);
					return false;
				}
			} else {
				DrawTextAt(WorldToScreen(Position(ent)), $"Unknown Chest: {ent.Path}", Color.White);
				return false;
			}
			// scale by the map zoom
			iconSize *= GetMapZoom() * 2;
			// save for 1000ms, then check for opened status etc
			ent.MinimapIcon = new Entity.Icon(icon, iconSize, Time.ElapsedMilliseconds + 1000);
			if( iconText != null ) {
				DrawTextAt(WorldToScreen(Position(ent)), iconText, Color.White);
			}
			return true;
		}
		private bool TryGetEnemyIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.None;
			iconSize = 1.2f * GetMapZoom() * 2;
			var minimapIcon = ent.GetComponent<MinimapIcon>();
			if ( IsValid(minimapIcon) && minimapIcon.IsVisible ) {
				return false;
			}
			string path = ent.Path;
			if( path == null ) {
				return false;
			}
			if( ent.MinimapIcon.Size != 0f ) {
				icon = ent.MinimapIcon.Sprite;
				iconSize = ent.MinimapIcon.Size;
				return icon != SpriteIcon.None;
			}
			if ( path.Contains("AfflictionVomitile") || path.Contains("AfflictionVolatile") ) {
				ent.MinimapIcon = new Entity.Icon(SpriteIcon.None, 1f);
				return false;
			}
			bool isHidden = HasBuff(ent, "hidden_monster");
			var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity;
			switch( rarity ) {
				case Offsets.MonsterRarity.Rare: if( ! ShowEnemyRares ) { return false; } break;
				case Offsets.MonsterRarity.Magic: if( ! ShowEnemyMagic ) { return false; } break;
				case Offsets.MonsterRarity.White: if( ! ShowEnemyNormal ) { return false; } break;
			}
			switch ( rarity ) {
				case Offsets.MonsterRarity.Unique: icon = isHidden ? SpriteIcon.SmallBrownHexagon : SpriteIcon.LargeBrownCircle; break;
				case Offsets.MonsterRarity.Rare: icon = isHidden ? SpriteIcon.SmallYellowHexagon : SpriteIcon.MediumYellowCircle; break;
				case Offsets.MonsterRarity.Magic: icon = isHidden ? SpriteIcon.SmallBlueHexagon : SpriteIcon.MediumBlueCircle; break;
				case Offsets.MonsterRarity.White: icon = isHidden ? SpriteIcon.SmallRedHexagon : SpriteIcon.MediumRedCircle; break;
			}
			// save the current icon for 300ms, then it will expire and check again for hidden status, etc
			// ent.MinimapIcon = new Entity.Icon(icon, iconSize, Time.ElapsedMilliseconds + 300);
			bool isInvulnTotem = path.Contains("TotemAlliesCannotDie");
			if ( ShowRareOverhead && (isInvulnTotem || rarity >= Offsets.MonsterRarity.Rare) ) {
				var render = ent.GetComponent<Render>();
				if ( IsValid(render) ) {
					var overhead = WorldToScreen(render.Position + new Vector3(0, 0, -1.5f * render.Bounds.Z));
					if( isInvulnTotem ) {
						icon = SpriteIcon.RedFlag;
					}
					var overheadIconSize = IconSize * GetMapZoom() * 8;
					DrawSprite(icon, overhead, overheadIconSize, overheadIconSize);
					// DrawTextAt(ent, ent.Path, Color.White);
					/*
					ImGui.Begin("debug_overhead");
					ImGui.Text("Overhead vector result:");
					ImGui_Object("overhead", "overhead", overhead, new HashSet<int>());
					ImGui.Text("Render component:");
					ImGui_Object("render", "render", render, new HashSet<int>());
					ImGui.Text("Camera:");
					ImGui_Object("camera", "camera", PoEMemory.GameRoot.InGameState.WorldData.Camera, new HashSet<int>());
					ImGui_Address(PoEMemory.GameRoot.InGameState.WorldData.Address
						+ GetOffset<Offsets.WorldData>("Camera"),
						"Camera Address", "Camera");
					ImGui.End();
					*/
				}
			}
			return true;
		}

		private bool TryGetMinionIcon(Entity ent, out SpriteIcon icon, out float iconSize) {
			icon = SpriteIcon.None;
			iconSize = 1f;

			if ( ent?.Path?.StartsWith("Metadata/Monsters/AnimatedItem", StringComparison.Ordinal) ?? false ) {
				icon = SpriteIcon.SmallGreenCircle;
			} else if ( IsAlive(ent) ) {
				icon = SpriteIcon.GreenX;
			}

			if ( icon != SpriteIcon.None ) {
				// cache the icon for 1 second
				ent.MinimapIcon = new Entity.Icon(icon, iconSize, Time.ElapsedMilliseconds + 1000);
				return true;
			}
			return false;
		}
	}
}
