using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using static AtE.Globals;
using ImGuiNET;
using System.Drawing;

namespace AtE {
	class LevelingPlugin : PluginBase {

		public override string Name => "Leveling Help";

		public LevelingPlugin() : base() {
			EntityCache.EntityAdded += (sender, ent) => {
				Notify($"Added: {ent?.Path}");
			};
		}

		public bool DismissStoryText = false;
		public uint ClickDelayMilliseconds = 550;

		public bool ShowMinionStats = false;
		public bool ShowSpectreSpells = false;
		public bool DebugUnknownMinions = false;

		private struct MinionSkillDesc {
			public string PathPrefix;
			public string DisplayName;
			public string InternalName;
			public Color Color;
		}
		private static void AddMinionSkillDesc(string pathPrefix, string displayName, string internalName, Color color) {
			if ( MinionDescriptors == null ) {
				MinionDescriptors = new Dictionary<string, MinionSkillDesc>();
			}
			MinionDescriptors[pathPrefix] = new MinionSkillDesc() { PathPrefix = pathPrefix, DisplayName = displayName, InternalName = internalName, Color = color };
		}
		static LevelingPlugin() {
			AddMinionSkillDesc("Metadata/Monsters/AnimatedItem/AnimatedWeapon", "Animated Weapon", "animate_weapon", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/AnimatedItem/AnimatedArmour", "Animated Guardian", "animate_armour", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/AnimatedItem/HolyLivingRelic", "Summoned Holy Relic", "summon_relic", Color.PaleGoldenrod);
			AddMinionSkillDesc("Metadata/Monsters/SummonedSkull/SummonedSkull", "Raging Spirit", "summon_raging_spirit", Color.LightGoldenrodYellow);
			AddMinionSkillDesc("Metadata/Monsters/SummonedSkull/SummonedRaven", "Summoned Raven", "summon_raging_spirit", Color.MediumPurple);
			AddMinionSkillDesc("Metadata/Monsters/SummonedPhantasm/SummonedPhantasm", "Summoned Phantasm", "summon_phantasm", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/IcyRagingSpirit/IcyRagingSpirit", "Decree of the Grave", "decree_of_the_grave_on_kill", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/BoneGolem/BoneGolem", "Golem - Carrion", "summon_bone_golem", Color.LimeGreen);
			AddMinionSkillDesc("Metadata/Monsters/LightningGolem/LightningGolemSummoned", "Golem - Lightning", "summon_lightning_golem", Color.SkyBlue);
			AddMinionSkillDesc("Metadata/Monsters/RockGolem/RockGolemSummoned", "Golem - Stone", "summon_rock_golem", Color.BurlyWood);
			AddMinionSkillDesc("Metadata/Monsters/FireElemental/FireElementalSummoned", "Golem - Flame", "summon_fire_elemental", Color.LightSalmon);
			AddMinionSkillDesc("Metadata/Monsters/IceElemental/IceElementalSummoned", "Golem - Ice", "summon_ice_elemental", Color.SkyBlue);
			AddMinionSkillDesc("Metadata/Monsters/ChaosElemental/ChaosElementalSummoned", "Golem - Chaos", "summon_chaos_elemental", Color.MediumPurple);
			AddMinionSkillDesc("Metadata/Monsters/RaisedZombies/RaisedZombieStandard", "Raised Zombie", "raise_zombie", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard", "Summoned Skeleton", "summon_skeletons", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/RaisedSkeletons/RaisedSkeletonRanged1Quality", "Summoned Skeleton", "summon_skeletons", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/RaisedSkeletons/RaisedSkeletonRanged2Quality", "Summoned Skeleton", "summon_skeletons", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/Skitterbot/SkitterbotCold", "Skitterbot - Chill", "skitterbots", Color.Cyan);
			AddMinionSkillDesc("Metadata/Monsters/Skitterbot/SkitterbotLightning", "Skitterbot - Shock", "skitterbots", Color.Yellow);
			AddMinionSkillDesc("Metadata/Monsters/Totems/TauntTotem", "Totem - Decoy", "totem_taunt", Color.Beige);
			AddMinionSkillDesc("Metadata/Monsters/SpiderPlated/HeraldOfAgonySpiderPlated", "Herald - Agony", "herald_of_agony", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Totems/SlamTotem", "Totem - Ancestral Warchief", "ancestor_totem_slam", Color.Orange);
			AddMinionSkillDesc("Metadata/Monsters/Totems/VaalSlamTotem", "Totem - Vaal Ancestral Warchief", "vaal_ancestral_warchief", Color.Orange);
			AddMinionSkillDesc("Metadata/Monsters/Totems/MeleeTotem", "Totem - Ancestral Protector", "totem_melee", Color.Orange);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/WitchMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/RangerMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/TemplarMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/DuelistMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/ShadowMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/MarauderMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Mirage/ScionMirage", "Mirage Archer", "mirage_archer", Color.LightGreen);
			AddMinionSkillDesc("Metadata/Monsters/Totems/IntelligenceTotem", "Totem - Spell Totem", "spell_totem", Color.AliceBlue);
			AddMinionSkillDesc("Metadata/Monsters/Totems/StrengthTotem", "Totem - Spell Totem", "spell_totem", Color.AliceBlue);
			AddMinionSkillDesc("Metadata/Monsters/Totems/DexterityTotem", "Totem - Spell Totem", "spell_totem", Color.AliceBlue);
			AddMinionSkillDesc("Metadata/Monsters/Totems/HolyFireSprayTotem", "Totem - Holy Flame", "spell_totem", Color.LightGoldenrodYellow);
			// traps? mines?
			// skitterbot curse ring
			AddMinionSkillDesc("Raised Spectre", "Raised Spectre", "raise_spectre", Color.White);
		}

		private static Dictionary<string, MinionSkillDesc> MinionDescriptors;

		public override void Render() {
			ImGui.Checkbox("Dismiss Story Text", ref DismissStoryText);
			ImGui.SameLine();
			ImGui_HelpMarker("When NPCs show long text with a 'Continue' button, this will click 'Continue' immediately.");

			ImGui.Checkbox("Show Minion Summary", ref ShowMinionStats);
			if( ShowMinionStats ) {
				ImGui.Indent(); ImGui.Checkbox("Show Spectre Spells", ref ShowSpectreSpells);
			}
		}

		private long lastClick = 0;

		private void DebugElement(Element elem, string prefix = "") {
			long id = elem.Address.ToInt64();
			ImGui.Text($"{prefix}.Text = {elem.Text ?? "??"}");
			ImGui.SameLine();
			if ( ImGui.Button($"B##{id:X}") ) {
				Run_ObjectBrowser($"Element {id:X}", elem);
			} else if ( ImGui.IsItemHovered() ) {
				DrawFrame(elem.GetClientRect(), Color.Yellow, 2);
			}
			if( elem.IsVisible ) {
				ImGui.SameLine();
				ImGui.Text("Visible");
			}
			Element[] children = elem.Children.ToArray();
			for(uint i = 0; i < children.Length; i++ ) {
				DebugElement(children[i], prefix + $".{i}");
			}
		}

		private uint GetColorU32(byte red, byte green, byte blue, byte alpha) => ImGui.GetColorU32(new Vector4(red / 256f, green / 256f, blue / 256f, alpha / 256f));
		private uint GetColorU32(Color color) => GetColorU32(color.R, color.G, color.B, color.A);


		private void ProgressBar(float percentComplete, string label, Vector2 size, Color background, Color foreground, Color text) {
			ImGui.PushStyleColor(ImGuiCol.FrameBg, GetColorU32(background));
			ImGui.PushStyleColor(ImGuiCol.Text, GetColorU32(text));
			ImGui.PushStyleColor(ImGuiCol.PlotHistogram, GetColorU32(foreground));
			ImGui.ProgressBar(percentComplete, size, label);
			ImGui.PopStyleColor(3);
		}

		private void HealthBars(Entity ent, Color healthColor, Color manaColor, Color energyShieldColor) {
			if( !IsValid(ent) ) {
				return;
			}
			var life = ent.GetComponent<Life>();
			if( !IsValid(life) ) {
				return;
			}
			var lineHeight = ImGui.GetFontSize();
			ImGui.Text($"-- {ent.Path} --");
			ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, 0);
			ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, 0);
			if( life.MaxHP > 0 ) {
				float pctLife = life.CurHP / (float)life.MaxHP;
				ProgressBar(pctLife, $"{life.CurHP}", new Vector2(100, lineHeight), Color.Transparent, healthColor, Color.White);
			}
			if ( life.MaxES > 0 ) {
				float pctES = life.CurES / (float)life.MaxES;
				ImGui.SameLine(7f, 0); ProgressBar(pctES, $"", new Vector2(100, lineHeight), Color.Transparent, Color.FromArgb(128, energyShieldColor), Color.Black);
			}
			if( life.MaxMana > 0 ) {
				float pctMana = life.CurMana / (float)life.MaxMana;
				ProgressBar(pctMana, "", new Vector2(100, 3), Color.Transparent, manaColor, Color.White);
			}
		}

		private void ClickElement(Element elem) {
			long now = Time.ElapsedMilliseconds;
			if( (now - lastClick) > ClickDelayMilliseconds ) {
				lastClick = now;
				Run(new LeftClickAt(elem.GetClientRect(), 30, 1));
			}
		}

		private struct MinionSummary {
			public string DisplayName;
			public uint Count;
			public float MinTimeLeft;
			public float MaxTimeLeft;
			public Color Color;
		}

		public override IState OnTick(long dt) {
			if ( Paused || !Enabled || !PoEMemory.IsAttached ) {
				return this;
			}

			var ui = GetUI();
			if( !IsValid(ui) ) {
				return this;
			}


			if( ShowMinionStats ) {
				var chatBox = ui.ChatBoxRoot?.GetChild(0);
				if( chatBox != null && ! chatBox.IsVisible ) {
					var loc = chatBox.GetClientRect().Location;
					var actor = GetPlayer()?.GetComponent<Actor>();
					var deployed = (actor?.DeployedObjects ?? Empty<DeployedObject>()).ToArray();
					var minionSummary = new Dictionary<string, MinionSummary>();
					var spot = new Vector2(loc.X, loc.Y);
					float maxTextWidth = 0f;
					foreach(var minion in deployed) {
						var ent = minion.GetEntity();
						if( IsValid(ent) ) {
							// HealthBars(ent, Color.Green, Color.DarkBlue, Color.SkyBlue);
							var color = Color.White;
							string path = ent.Path?.Split('@').FirstOrDefault() ?? "null";
							if( ! minionSummary.ContainsKey(path) ) {
								minionSummary[path] = new MinionSummary() { MinTimeLeft = float.PositiveInfinity };
							}
							var summary = minionSummary[path];
							summary.Count += 1;
							string label = path;
							if( MinionDescriptors.TryGetValue(path, out var desc) ) {
								summary.DisplayName = desc.DisplayName;
								summary.Color = desc.Color;
								int minionDuration = 0;
								if ( minion.GetSkill()?.TryGetStat(Offsets.GameStat.MinionDuration, out minionDuration) ?? false ) {
									float expectedTimeAlive = (minionDuration / 1000f);
									float timeAlive = ent.GetComponent<Animated>()?.AnimatedObject?.GetComponent<ClientAnimationController>()?.TimeSpentAnimating ?? 0f;
									float timeRemaining = expectedTimeAlive - timeAlive;
									if( timeRemaining < summary.MinTimeLeft ) {
										summary.MinTimeLeft = timeRemaining;
									}
									if( expectedTimeAlive > summary.MaxTimeLeft ) {
										summary.MaxTimeLeft = expectedTimeAlive;
									}
								}
							} else if( HasBuff(ent, "spectre_buff") ) {
								color = Color.CornflowerBlue;
								label = $"Spectre - {ent.GetComponent<Render>()?.Name ?? path.Split('/').LastOrDefault()}";
								if ( ShowSpectreSpells ) {
									label += " " + string.Join(", ", ent.GetComponent<Actor>().Skills
										.Where((skill) => !skill.InternalName.Equals("melee"))
										.Select((skill) => skill.Name ?? skill.InternalName));
								}
								summary.DisplayName = label;
								summary.Color = MinionDescriptors["Raised Spectre"].Color;
							} else if ( DebugUnknownMinions ) {
								ImGui.Text($"Unknown path {path}");
							}
							minionSummary[path] = summary;
							float width = ImGui.CalcTextSize(summary.DisplayName).X + ((float)(1f + Math.Ceiling(Math.Log10(summary.Count))) * 14f);
							if( summary.MinTimeLeft != float.PositiveInfinity ) {
								width += 60f;
							}
							if( width > maxTextWidth ) {
								maxTextWidth = width;
							}
							// DrawTextAt(36, spot, label, color);
						}
					}
					
					ImGui.SetNextWindowPos(spot);
					ImGui.SetNextWindowSize(new Vector2(maxTextWidth + 20f, 0f));
					ImGui.Begin("Minion Summary", 
						ImGuiWindowFlags.None
						| ImGuiWindowFlags.NoDecoration 
						| ImGuiWindowFlags.NoBackground
						| ImGuiWindowFlags.NoMove
						| ImGuiWindowFlags.AlwaysAutoResize
						| ImGuiWindowFlags.NoInputs );
					foreach(var entry in minionSummary ) {
						Color color = entry.Value.Color;
						string label = entry.Value.DisplayName;
						if ( label == null ) label = "null";
						if( entry.Value.Count > 1 ) {
							label += $" (x{entry.Value.Count})";
						}
						float min = entry.Value.MinTimeLeft;
						float max = entry.Value.MaxTimeLeft;
						float ratio = (min / max);
						if ( min > 0 && min < float.PositiveInfinity ) {
							label += $" {min:f}s";
							if ( ratio < 0.1 ) {
								color = Color.Red;
							} else if ( ratio < 0.3 ) {
								color = Color.Orange;
							} else if ( ratio < 0.5 ) {
								color = Color.Yellow;
							} else if ( ratio > 0.9 ) {
								color = Color.ForestGreen;
							}
							ImGui.PushStyleColor(ImGuiCol.FrameBg, ImGui.GetColorU32(new Vector4(color.R/256f, color.G/256f, color.B/256f, .166f)));
							ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));
							ImGui.PushStyleColor(ImGuiCol.PlotHistogram, ImGui.GetColorU32(new Vector4(color.R/256f, color.G/256f, color.B/256f, .5f)));
							ImGui.ProgressBar(ratio, new Vector2(maxTextWidth + 10f, 0f), label);
							ImGui.PopStyleColor(3);
						} else {
							ImGui.TextColored(new Vector4(color.R / 256f, color.G / 256f, color.B / 256f, color.A / 256f), label);
						}
						// ImGui.Text(label);
						// DrawTextAt(36, spot, label, color);
					}
					ImGui.End();
				}
			}


			if ( DismissStoryText ) {
				var dialog = ui.NpcOptions;
				/*
				ImGui.Begin("Debug LeagueNpcDialog");
				if( IsValid(dialog) ) {
					DebugElement(ui.NpcOptions, "ui.NpcOptions");
				} else {
					ImGui.Text("Not valid");
				}
				ImGui.End();
				*/
				if ( IsValid(dialog) && dialog.IsVisibleLocal ) {
					var continueOption = dialog
						.GetChild(1)?
						.GetChild(2)?
						.GetChild(0)?
						.GetChild(2)?
						.GetChild(2)?
						.GetChild(0) ?? null;
					string text;
					if ( IsValid(continueOption)
						&& continueOption.IsVisibleLocal
						&& ((text = continueOption.Text)?.Equals("Continue") ?? false) ) {
						ClickElement(continueOption);
						return new Delay(ClickDelayMilliseconds, this);
					}
				}
				dialog = ui.NpcDialog;
				if ( IsValid(dialog) && (dialog?.IsVisibleLocal ?? false) ) {
					var options = dialog.GetChild(1)?.GetChild(2) ?? null;
					if ( options != null ) {
						foreach ( var child in options.Children ) {
							var textChild = child?.GetChild(0);
							if ( textChild?.Text?.Equals("Continue") ?? false ) {
								ClickElement(textChild);
								return new Delay(ClickDelayMilliseconds, this);
							}
						}
					}
				}
			}
			
			return base.OnTick(dt);
		}
	}
}
