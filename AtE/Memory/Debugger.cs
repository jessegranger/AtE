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
		public string Highlight = "";
		public IntPtr ViewAddress;
		public IntPtr BaseAddress;
		public Debugger(IntPtr addr, State next = null) : base(next) {
			ViewAddress = addr;
			BaseAddress = addr;
			InputAddress = $"{(long)addr:X16}";
			Id = $"Debugger@{Describe(addr)}";
			Log($"Debugger: created to view {Describe(ViewAddress)}");
		}

		private static Dictionary<IntPtr, string> globalKnownOffsets = new Dictionary<IntPtr, string>();
		public static void RegisterOffset(string label, IntPtr addr) => globalKnownOffsets[new IntPtr((((long)addr)/8)*8)] = label;

		private byte[] sample;
		private long lastSampleTime;

		private static Dictionary<string, IntPtr> knownVtablePtrs = new Dictionary<string, IntPtr>();
		private static Dictionary<IntPtr, string> knownVtableNames = new Dictionary<IntPtr, string>();
		public static void RegisterVtable(string name, IntPtr ptr) {
			if( ! knownVtablePtrs.TryGetValue(name, out IntPtr ignore) ) {
				Log($"Debugger: recognizing vtable at {Describe(ptr)} for {name}");
				knownVtablePtrs[name] = ptr;
				knownVtableNames[ptr] = name;
			}
		}

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

		public Debugger usingStructLabelsFrom(string name) {
			for ( int i = 0; i < knownOffsetNames.Length; i++ ) {
				if( knownOffsetNames[i].Equals(name) ) {
					selectedOffsets = i;
					break;
				}
			}
			return this;
		}

		private int showColumnTypeAsFloat = 0; // 0 = show as int, 1 = show as floats

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
						int fieldOffset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0;
						int alignedOffset = fieldOffset - (fieldOffset % 8); // for label purposes, labelAddr should end up aligned by 8 bytes
						IntPtr labelAddr = BaseAddress
							+ alignedOffset;
						string labelText = knownOffsetNames[selectedOffsets] + "." + field.Name
							+ (alignedOffset != fieldOffset ? $" (+{fieldOffset % 8})" : "");
						if( temporaryLabels.TryGetValue(labelAddr, out string prior) ) {
							temporaryLabels[labelAddr] = prior + " " + labelText;
						} else {
							temporaryLabels[labelAddr] = labelText;
						}
					}
				}
				ImGui.AlignTextToFramePadding();
				if ( ImGui.IsWindowFocused() ) {
					if ( ImGui.IsKeyPressed(ImGuiKey.UpArrow) || ImGui.GetIO().MouseWheel > 0 ) {
						ViewAddress = new IntPtr(Convert.ToInt64(InputAddress, 16));
						ViewAddress -= 16;
						InputAddress = $"{ViewAddress.ToInt64():X}";
						Resample();
					}
					else if ( ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.GetIO().MouseWheel < 0 ) {
						ViewAddress = new IntPtr(Convert.ToInt64(InputAddress, 16));
						ViewAddress += 16;
						InputAddress = $"{ViewAddress.ToInt64():X}";
						Resample();
					}
				}
				bool resampleDue = (Time.ElapsedMilliseconds - lastSampleTime) > 200;
				ImGui.Text("Address 0x");
				ImGui.SameLine();
				if ( ImGui.InputText("##Address", ref InputAddress, 32, ImGuiInputTextFlags.EnterReturnsTrue) 
					|| sample == null
					|| resampleDue ) {
					try {
						Resample();
					} catch ( FormatException ) {
						ImGui.SameLine();
						ImGui.Text("Invalid Address");
					}
				}
				ImGui.SameLine();
				ImGui.Text("- preview as:");
				ImGui.SameLine();
				ImGui.RadioButton("int", ref showColumnTypeAsFloat, 0);
				ImGui.SameLine();
				ImGui.RadioButton("float", ref showColumnTypeAsFloat, 1);

				ImGui.AlignTextToFramePadding();
				ImGui.Text("Highlight");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(100);
				ImGui.InputText("##Highlight", ref Highlight, 12);
				ImGui.SameLine();
				ImGui.Text(" - or - ");
				ImGui.SameLine();
				if( ImGui.Button("Search") ) {
					Run(new MemorySearch());
				}

				// begin the main display table:
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
							byte b = sample[rowOffset + j];
							string s = $"{b:X2}";
							bool highlight = Highlight.Contains(s);
							if( highlight ) {
								ImGui.PushStyleColor(ImGuiCol.Text, (uint)ToRGBA(Color.Yellow));
							}
							ImGui.Text($"{b:X2}");
							if( highlight ) {
								ImGui.PopStyleColor();
							}
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
						long longValue = 0;
						if ( showColumnTypeAsFloat == 0 ) {
							ImGui.TableNextColumn();
							if ( PoEMemory.TryRead(offset, out int intValue1) ) {
								ImGui.Text($"{intValue1}i");
							}
							ImGui.TableNextColumn();
							if ( PoEMemory.TryRead(offset + 4, out int intValue2) ) {
								ImGui.Text($"{intValue2}i");
							}
							ImGui.TableNextColumn();
							if ( PoEMemory.TryRead(offset, out longValue) ) {
								ImGui.Text($"{longValue}l");
							}
						} else if( showColumnTypeAsFloat == 1 ) {
							ImGui.TableNextColumn();
							if( PoEMemory.TryRead(offset, out double doubleValue) ) {
								ImGui.Text($"{doubleValue}d");
							}
							ImGui.TableNextColumn();
							if( PoEMemory.TryRead(offset, out float floatValue) ) {
								ImGui.Text($"{floatValue}f");
							}
							ImGui.TableNextColumn();
							if( PoEMemory.TryRead(offset + 4, out float floatValue2) ) {
								ImGui.Text($"{floatValue2}f");
							}
						}
						ImGui.TableNextColumn();
						if( PoEMemory.TryRead(offset, out longValue) ) {
							ImGui.Text($"0x{longValue:X}");
							ImGui.TableNextColumn();
							IntPtr ptr = new IntPtr(longValue);
							if ( IsValid(ptr) ) {
								// Use knownVtablePtrs to see if this address is a known vtable value
								if ( knownVtableNames.TryGetValue(ptr, out string ptrName) ) {
									ImGui.AlignTextToFramePadding();
									ImGui.Text(ptrName); ImGui.SameLine();
								}
								// Use knownVtablePtrs to find references to known classes
								else if ( PoEMemory.TryRead(ptr, out IntPtr refValue) 
									&& knownVtableNames.TryGetValue(refValue, out string refName) ) {
									ImGui.AlignTextToFramePadding();
									ImGui.Text($"ptr {refName}"); ImGui.SameLine();
									if ( ImGui.Button($"M##{ptr}") ) {
										Run(new Debugger(ptr).usingStructLabelsFrom(refName));
									}
								// Use the ElementCache to find ptrs to Elements
								} else if ( ElementCache.TryGetElement(ptr, out Element elem) ) {
									ImGui.AlignTextToFramePadding();
									ImGui.Text("ptr Element"); ImGui.SameLine();
									if ( ImGui.Button($"B##{longValue:X}") ) {
										Run_ObjectBrowser($"Unknown Element {longValue:X}", elem);
									} else if ( ImGui.IsItemHovered() ) {
										DrawFrame(elem.GetClientRect(), Color.Yellow, 2);
									}
								// Use the EntityCache to match ptrs to known Entities
								} else if ( EntityCache.TryGetEntity(ptr, out Entity ent) ) {
									ImGui.AlignTextToFramePadding();
									ImGui.Text("ptr Entity"); ImGui.SameLine();
									if ( ImGui.Button($"B##{longValue:X}") ) {
										Run_ObjectBrowser($"Unknown Entity {longValue:X}", ent);
									}
								} else { // and last, see if it's possible to read a string from the ptr
									if ( PoEMemory.TryReadString(ptr, Encoding.ASCII, out string asc, 16) ) {
										ImGui.Text($"s\"{Truncate(asc.Replace('\n', '?'), 16)}\"");
									}
									if ( PoEMemory.TryReadString(ptr, Encoding.Unicode, out string utf, 16) ) {
										ImGui.SameLine();
										ImGui.Text($"u\"{Truncate(utf.Replace('\n', '?'), 16)}\"");
									}
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

	class MemorySearch : State {
		bool Open = true;
		string SearchPattern = "00000000";
		string SearchMask = "xxxxxxxx";
		int SearchStride = 8;
		int MatchLimit = 2;
		private List<IntPtr> Matches = new List<IntPtr>();
		public override IState OnTick(long dt) {
			if( dt <= 0 ) {
				return this;
			}
			if( !Open ) {
				return Next;
			}
			ImGui.Begin("Memory Search", ref Open);
			ImGui.Text("Welcome to Memory Search");

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Stride:"); ImGui.SameLine();
			ImGui.SetNextItemWidth(80);
			ImGui.InputInt("##Stride", ref SearchStride);

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Pattern:"); ImGui.SameLine();
			ImGui.SetNextItemWidth(200);
			ImGui.InputText("##Pattern", ref SearchPattern, 16);

			IntPtr searchPtr = new IntPtr(0x299CF700020);
			IntPtr searchPtrTwo = new IntPtr(0x299CF700420);
			ImGui.Text($"(forcing ptr): {Describe(searchPtr)}");

			// ImGui.AlignTextToFramePadding();
			// ImGui.Text("Mask:"); ImGui.SameLine();
			// ImGui.SetNextItemWidth(200);
			// ImGui.InputText("##Mask", ref SearchMask, 16);

			if ( SearchPattern.Length != SearchMask.Length ) {
				ImGui.Text("Error: mask must be the same length as pattern");
			} else if (ImGui.Button("Search") ) {
				Matches.Clear();
				ImGui.Text("Searching...");
				foreach ( var page in PoEMemory.EnumerateAllocatedRanges() ) {
					IntPtr startAddress = page.BaseAddress;
					IntPtr endAddress = new IntPtr(startAddress.ToInt64() + page.RegionSize);
					while ( startAddress.ToInt64() < endAddress.ToInt64() ) {
						if( PoEMemory.TryRead(startAddress, out IntPtr result)
							&& result.Equals(searchPtr) ) {
							Log($"Partial Match at: {Describe(startAddress)}");
							if( PoEMemory.TryRead(startAddress + 8, out IntPtr resultTwo)
								&& resultTwo.Equals(searchPtrTwo) ) {
								Log($"Full Match at: {Describe(startAddress)}");
								Matches.Add(startAddress);
							}
						}
						if( Matches.Count >= MatchLimit ) {
							break;
						}
						startAddress += SearchStride;
					}
				}

			} else if ( Matches.Count > 0 ) {
				foreach( IntPtr match in Matches ) {
					ImGui_Address(match, "Unknown Match");
				}
			}

			ImGui.End();
			return base.OnTick(dt);
		}

	}
}
