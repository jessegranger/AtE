using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;

namespace AtE {
	public class Debugger : State {
		public bool Open = true;
		public string Id = "Debugger";

		public int LineCount = 30;

		public string InputAddress = "0";
		public IntPtr ViewAddress;
		public IntPtr BaseAddress;
		public Debugger(IntPtr addr, State next = null) : base(next) {
			ViewAddress = addr;
			BaseAddress = addr;
			InputAddress = $"{(long)addr:X16}";
			Id = $"Debugger@{Globals.Format(addr)}";
			Log($"Debugger: created to view {ViewAddress}");
		}

		private static Dictionary<IntPtr, string> globalKnownOffsets = new Dictionary<IntPtr, string>();
		public static void RegisterOffset(string label, IntPtr addr) => globalKnownOffsets[new IntPtr((((long)addr)/8)*8)] = label;

		private byte[] sample;
		private long lastSampleTime;

		private void Resample() {
			ViewAddress = new IntPtr(Convert.ToInt64(InputAddress, 16));
			sample = new byte[8 * LineCount];
			if ( PoEMemory.TryRead(ViewAddress, sample) == 0 ) {
				sample = null;
			}
			lastSampleTime = Time.ElapsedMilliseconds;
		}

		private Dictionary<IntPtr, string> knownAddressLabels = new Dictionary<IntPtr, string>();
		public Debugger WithKnownAddress(string label, IntPtr addr) {
			knownAddressLabels[addr] = label;
			return this;
		}

		private int selectedOffsets = -1;
		private Type[] knownOffsets = typeof(Offsets).GetNestedTypes()
			.Where(t => t.IsExplicitLayout)
			.OrderBy(t => t.Name)
			.ToArray();
		private string[] knownOffsetNames = typeof(Offsets).GetNestedTypes()
			.Where(t => t.IsExplicitLayout)
			.Select(t => t.Name)
			.OrderBy(t => t)
			.ToArray();
		private bool useStructLabels = true;

		public override IState OnTick(long dt) {
			if ( dt <= 0 ) {
				return this;
			}

			if ( !Open ) {
				return Next;
			}

			ImGui.Begin(Id, ref Open);
			try {
				ImGui.Checkbox("", ref useStructLabels); ImGui.SameLine();
				ImGui.Text("Label as:"); ImGui.SameLine();
				ImGui.Combo($"##{Id}", ref selectedOffsets, knownOffsetNames, knownOffsetNames.Length);
				Dictionary<IntPtr, string> temporaryLabels = new Dictionary<IntPtr, string>();
				if( useStructLabels && selectedOffsets > -1 ) {
					Type labelType = knownOffsets[selectedOffsets];
					foreach ( FieldInfo field in labelType.GetFields() ) {
						IntPtr labelAddr = BaseAddress
							+ (field.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0);
						temporaryLabels[labelAddr] = knownOffsetNames[selectedOffsets] + "." + field.Name;
					}
				}
				ImGui.AlignTextToFramePadding();
				ImGui.Text("0x"); ImGui.SameLine();
				if ( ImGui.IsWindowFocused() ) {
					if ( ImGui.IsKeyPressed(ImGuiKey.UpArrow) || ImGui.GetIO().MouseWheel > 0 ) {
						ViewAddress = new IntPtr(Convert.ToInt64(InputAddress, 16));
						ViewAddress -= 8;
						InputAddress = $"{ViewAddress.ToInt64():X}";
						Resample();
					}
					else if ( ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.GetIO().MouseWheel < 0 ) {
						ViewAddress = new IntPtr(Convert.ToInt64(InputAddress, 16));
						ViewAddress += 8;
						InputAddress = $"{ViewAddress.ToInt64():X}";
						Resample();
					}
				}
				if ( ImGui.InputText("Address", ref InputAddress, 32, ImGuiInputTextFlags.EnterReturnsTrue) || sample == null) {
					try {
						Resample();
					} catch ( FormatException ) {
						ImGui.SameLine();
						ImGui.Text("Invalid Address");
					}
				}
				if( (Time.ElapsedMilliseconds - lastSampleTime) > 200 ) {
					Resample();
				}
				if ( ImGui.BeginTable($"Table-{Id}", 24, ImGuiTableFlags.SizingFixedFit ) ) {


					int size = sample?.Length ?? 0;
					// var frontLine = new StringBuilder();
					// var backLine = new StringBuilder();
					for ( int rowOffset = 0; rowOffset < size - 8; rowOffset += 8 ) {
						IntPtr offset = ViewAddress + rowOffset;
						ImGui.TableNextRow();
						ImGui.TableNextColumn();
						ImGui.AlignTextToFramePadding();
						ImGui.Text($"+{rowOffset:X4}"); ImGui.SameLine();
						if( ImGui.Button($"0x{offset.ToInt64():X12}") ) {
							if( ImGui.GetIO().KeyCtrl ) {
								if( PoEMemory.TryRead(offset, out IntPtr ptrTo) ) {
									Run(new Debugger(ptrTo));
								}
							} else {
								Run(new Debugger(offset));
							}
						}
						if( ImGui.IsItemHovered( ) ) {
							ImGui.PushStyleColor(ImGuiCol.Text, (uint)ToRGBA(Color.Orange));
						} else {
							ImGui.PushStyleColor(ImGuiCol.Text, (uint)ToRGBA(Color.White));
						}

						// the 8 columns of hex bytes:
						for ( int j = 0; j < 8; j++ ) {
							ImGui.TableNextColumn();
							ImGui.Text($"{sample[rowOffset + j]:X2}");
						}

						// then 8 columns of the bytes rendered as chars (as is tradition)
						for ( int j = 0; j < 8; j++ ) {
							char c = Convert.ToChar(sample[rowOffset + j]);
							if ( char.IsControl(c) ) {
								c = '?';
							}
							ImGui.TableNextColumn();
							ImGui.Text($"{c}");
						}

						// then try to read this value as different number forms
						ImGui.TableNextColumn();
						if( PoEMemory.TryRead(offset, out int intValue1) ) {
							ImGui.Text($"{intValue1}i");
						}
						ImGui.TableNextColumn();
						if( PoEMemory.TryRead(offset + 4, out int intValue2) ) {
							ImGui.Text($"{intValue2}i");
						}
						ImGui.TableNextColumn();
						if( PoEMemory.TryRead(offset, out long longValue) ) {
							ImGui.Text($"0x{longValue:X}");
							ImGui.TableNextColumn();
							IntPtr ptr = new IntPtr(longValue);
							Element elem = new Element() { Address = ptr };
							if ( IsValid(elem) ) {
								ImGui.AlignTextToFramePadding();
								ImGui.Text("ptr Element"); ImGui.SameLine();
								if ( ImGui.Button($"B##{longValue:X}") ) {
									Run_ObjectBrowser($"Unknown Element {longValue:X}",
										new Element() { Address = ptr });
								} else if( ImGui.IsItemHovered() ) {
									DrawFrame(elem.GetClientRect(), Color.Yellow, 2);
								}
							} else if ( IsValid(new Entity() { Address = ptr }) ) {
								ImGui.AlignTextToFramePadding();
								ImGui.Text("ptr Entity"); ImGui.SameLine();
								if ( ImGui.Button($"B##{longValue:X}") ) {
									Run_ObjectBrowser($"Unknown Entity {longValue:X}",
										new Entity() { Address = ptr });
								}
							} else {
								// if( PoEMemory.TryReadString(new Address(0, longValue), Encoding.Unicode, out string uni) ) {
								// ImGui.Text($"u\"{Truncate(uni,10)}\"");
								// }
								if ( PoEMemory.TryReadString(ptr, Encoding.ASCII, out string asc, 16) ) {
									ImGui.Text($"s\"{Truncate(asc.Replace('\n','?'), 16)}\"");
								}
								ImGui.TableNextColumn();
								if ( PoEMemory.TryReadString(ptr, Encoding.Unicode, out string utf, 16) ) {
									ImGui.Text($"u\"{Truncate(utf.Replace('\n','?'), 16)}\"");
								}

							}
						} else {
							ImGui.Text($"<??>");
							ImGui.TableNextColumn();
						}

						ImGui.TableNextColumn();
						if ( globalKnownOffsets.ContainsKey(offset) ) {
							ImGui.Text($"<- {globalKnownOffsets[offset]}");
						} else if ( knownAddressLabels.ContainsKey(offset) ) {
							ImGui.Text($"<- {knownAddressLabels[offset]}");
						} else if ( temporaryLabels.ContainsKey(offset) ) {
							ImGui.Text($"<- {temporaryLabels[offset]}");
						}
						ImGui.PopStyleColor();
					}
					ImGui.EndTable();
				}
			} catch ( Exception e ) {
				Log(e.Message);
				Log(e.StackTrace);
				return Next;
			} finally {
				ImGui.End();
			}
			return this;
		}

		internal static void RegisterStructLabels<T>(string label, IntPtr addr) where T : struct {
			foreach ( FieldInfo field in typeof(T).GetFields() ) {
				RegisterOffset($"{label}.{field.Name}",
					addr + field.GetCustomAttribute<FieldOffsetAttribute>().Value);
			}
		}
	}
}
