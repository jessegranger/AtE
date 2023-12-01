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
		public uint ClickDelayMilliseconds = 350;

		public bool ShowMinionStats = false;
		public bool ShowSpectreSpells = false;

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
			AddMinionSkillDesc("Metadata/Monsters/SummonedSkull/SummonedSkull", "Raging Spirit", "summon_raging_spirit", Color.LightGoldenrodYellow);
			AddMinionSkillDesc("Metadata/Monsters/SummonedSkull/SummonedRaven", "Summoned Raven", "summon_raging_spirit", Color.MediumPurple);
			AddMinionSkillDesc("Metadata/Monsters/SummonedPhantasm/SummonedPhantasm", "Summoned Phantasm", "summon_phantasm", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/IcyRagingSpirit/IcyRagingSpirit", "Decree of the Grave", "decree_of_the_grave_on_kill", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/BoneGolem/BoneGolem", "Golem - Carrion", "summon_bone_golem", Color.LimeGreen);
			AddMinionSkillDesc("Metadata/Monsters/LightningGolem/LightningGolemSummoned", "Golem - Lightning", "summon_lightning_golem", Color.SkyBlue);
			AddMinionSkillDesc("Metadata/Monsters/RaisedZombies/RaisedZombieStandard", "Raised Zombie", "raise_zombie", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard", "Summoned Skeleton", "summon_skeletons", Color.White);
			AddMinionSkillDesc("Metadata/Monsters/Skitterbot/SkitterbotCold", "Skitterbot - Chill", "skitterbots", Color.Cyan);
			AddMinionSkillDesc("Metadata/Monsters/Skitterbot/SkitterbotLightning", "Skitterbot - Shock", "skitterbots", Color.Yellow);
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
					ImGui.SetNextWindowPos(spot);
					ImGui.Begin("Minion Summary", 
						ImGuiWindowFlags.None
						| ImGuiWindowFlags.NoDecoration 
						| ImGuiWindowFlags.NoBackground
						| ImGuiWindowFlags.NoMove
						| ImGuiWindowFlags.AlwaysAutoResize
						| ImGuiWindowFlags.NoInputs );
					foreach(var minion in deployed) {
						var ent = minion.GetEntity();
						if( IsValid(ent) ) {
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
								if( actor.GetSkill(desc.InternalName)?.TryGetStat(Offsets.GameStat.MinionDuration, out minionDuration) ?? false ) {
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
								label = $"Spectre - {path.Split('/').LastOrDefault()}";
								if ( ShowSpectreSpells ) {
									label += " " + string.Join(", ", ent.GetComponent<Actor>().Skills
										.Where((skill) => !skill.InternalName.Equals("melee"))
										.Select((skill) => skill.InternalName));
								}
								summary.DisplayName = label;
								summary.Color = MinionDescriptors["Raised Spectre"].Color;
							} else {
								ImGui.Text($"Unknown path {path}");
							}
							minionSummary[path] = summary;
							// DrawTextAt(36, spot, label, color);
						}
					}
					
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
							ImGui.ProgressBar(ratio, new Vector2(-1f, 0f), label);
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
					if ( IsValid(continueOption) && continueOption.IsVisibleLocal && continueOption.Text.Equals("Continue") ) {
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
