using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
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

#if DEBUG
		private void DebugOneComponent<T>(Entity ent) where T : MemoryObject, new() {
			string name = typeof(T).Name;
			var obj = ent.GetComponent<T>();
			if ( obj != null ) {
				ImGui_Address(obj.Address, name, "Component_" + name);
				ImGui.SameLine();
				if ( ImGui.Button($"B##DebugOneComponent_{name}") ) {
					Run_ObjectBrowser($"Component<{name}>", obj);
				}
			} else {
				ImGui.Text($"{name} - invalid");
			}
		}

		private Dictionary<string, LabelOnGround> mostRecentLabelSweep = new Dictionary<string, LabelOnGround>();
		private long mostRecentSweepTime = 0;
#endif

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

#if DEBUG
				if ( true ) {
					ImGui.Begin("Sonar Sweep");
					if( (Time.ElapsedMilliseconds - mostRecentSweepTime) > 2000  ) {
						mostRecentLabelSweep.Clear();
						mostRecentSweepTime = Time.ElapsedMilliseconds;
						ImGui.Text("Sweeping...");
						var labels = GetUI()?.LabelsOnGround?.GetAllLabels() ?? Empty<LabelOnGround>();
						foreach ( var label in labels ) {
							var ent = label.Item;
							if ( label?.Label?.IsVisible ?? false ) {
								// ImGui.AlignTextToFramePadding();
								// ImGui.Text($"{(label.Label?.Text ?? "null")}");
								var realEnt = ent?.GetComponent<WorldItem>()?.Item;
								if ( realEnt != null ) {
									// ImGui.SameLine();
									// ImGui.Text(realEnt.Path);
									// ImGui.SameLine();
									// if ( ImGui.Button($"B##{realEnt.Id}") ) {
										// Run_ObjectBrowser($"Entity {realEnt.Id}", realEnt);
									// }
									var renderItem = realEnt.GetComponent<RenderItem>();
									// ImGui.SameLine();
									string spriteFile = renderItem.ResourceName;
									// ImGui.Text(spriteFile);
									if( spriteFile != null && spriteFile.Length > 0 ) {
										if ( !Regex.IsMatch(spriteFile, "Gluttony|BiscosLeash|Umbilicus|MotherDyadus|Faminebind|ImmortalFlesh|85482|BeltOfTheDeciever|PyroshockClasp|Belt\\dUnique|KaomBelt|LeashOfOblation|MothersEmbrace|Prismweave") ) {
											mostRecentLabelSweep[spriteFile] = label;
										}
									}
								}
								// ImGui.SameLine();
								// if ( ImGui.Button($"B##{ent.Id}") ) {
									// Run_ObjectBrowser($"Entity {ent.Id}", ent);
								// }
							}
						}
					}
					foreach( var item in mostRecentLabelSweep ) {
						string name = item.Key;
						if ( name.Length > 3 && name.Contains("Belt") ) {
							ImGui.Text(item.Key);
							ImGui.SameLine();
							var ent = item.Value.Item;
							if ( IsValid(ent) ) {
								if ( ImGui.Button($"B##{ent.Id}") ) {
									Run_ObjectBrowser($"Entity {ent.Id}", ent);
								}
							}
						}
					}
					ImGui.End();
				}

				if( false ) {
					var pos = GetPlayer()?.GetComponent<Positioned>()?.GridPos ?? new Offsets.Vector2i();
					DrawBottomLeftText($"Grid Pos: {pos.X}, {pos.Y}", Color.Red);
				}

				if( false ) {
					ImGui.Begin("Minions");
					var p = GetPlayer();
					if( IsValid(p) ) {
						var actor = p.GetComponent<Actor>();
						ImGui_Address(actor.Address, "Actor @ ", "Component_Actor");
						ImGui.SameLine();
						if( ImGui.Button($"B##Component_Actor_Self_Button") ) {
							Run_ObjectBrowser("Component<Actor> of Player", actor);
						}
						var deployed = actor.DeployedObjects.ToArray();
						ImGui.Text($"Deployed Objects ({deployed.Length}):");
						foreach(var obj in deployed ) {
							var ent = obj.GetEntity();
							if( ent == null ) {
								continue;
							}
							string label = $"Entity {ent?.Id ?? 0} ";
							// ImGui_Address(ent.Address, $"{label}_Address", "Entity");
							ImGui.AlignTextToFramePadding();
							ImGui_Address(ent.Address, label, "Entity");
							ImGui.SameLine();
							if( ImGui.Button($"B##{label}_Button") ) {
								Run_ObjectBrowser(label, ent);
							}
							ImGui.SameLine();
							if( ImGui.Button($"C##{label}_ClearComponents") ) {
								ent.ClearComponents();
							}
							bool valid = IsValid(ent);
							ImGui.SameLine(); ImGui.Text($"{(valid ? "Valid" : "Invalid")}");
							ImGui.SameLine(); ImGui.Text($"{(IsAlive(ent) ? "Alive" : "Unalive")}");
							if ( valid ) {
								var life = ent.GetComponent<Life>();
								if ( IsValid(life) ) {
									ImGui.SameLine(); ImGui.Text($"{life.CurHP} hp");
								} else {
									ImGui.SameLine(); ImGui.Text($"null hp");
								}
								ImGui.Text($"Components: {string.Join(", ", ent.GetComponents()?.Keys ?? Empty<string>())}");
								// DebugOneComponent<Life>(ent);
								// DebugOneComponent<Monster>(ent);
								DebugOneComponent<Stats>(ent);
								// DebugOneComponent<ObjectMagicProperties>(ent);
								DebugOneComponent<Actor>(ent);
								// DebugOneComponent<DiesAfterTime>(ent);
								DebugOneComponent<Buffs>(ent);
								// DebugOneComponent<Brackets>(ent);
								// DebugOneComponent<Animated>(ent);
								// DebugOneComponent<ClientAnimationController>(ent.GetComponent<Animated>()?.AnimatedObject);
								float timeAlive = ent.GetComponent<Animated>()?.AnimatedObject?.GetComponent<ClientAnimationController>()?.TimeSpentAnimating ?? 0f;
								ImGui.Text($"{ent.Path} {timeAlive:f} sec");
								if( ent.Path.StartsWith("Metadata/Monsters/AnimatedItem/AnimatedWeapon") ) {
									int minionDuration = 0;
									if( actor.GetSkill("animate_weapon")?.TryGetStat(Offsets.GameStat.MinionDuration, out minionDuration) ?? false ) {
										float expectedTimeAlive = (minionDuration / 1000f);
										ImGui.SameLine();
										ImGui.Text($"of expected {expectedTimeAlive:f} sec");
									}
								}

								// ent?.DebugComponents();
							}
						}
					} else {
						ImGui.Text("Invalid Player");
					}
					ImGui.End();
				}
			
				if ( false ) {
					var pos = Position(GetPlayer());
					ImGui.Begin("Nearest Corpse");
					var nearest = NearbyEnemies(300).Where((e) => !IsAlive(e)).OrderBy((e) => DistanceSq(pos, Position(e))).FirstOrDefault();
					if ( nearest != null ) {
						ImGui.Text($"Path: {nearest.Path}");
						ImGui.SameLine();
						if( ImGui.Button($"B##Entity_{nearest.Id}") ) {
							Run_ObjectBrowser("Entity " + nearest.Id, nearest.GetComponent<ObjectMagicProperties>());
						}
					}  else {
						ImGui.Text("No corpse found.");
					}
					ImGui.End();
				}
#endif
			}
			return this;
		}

	}
}
