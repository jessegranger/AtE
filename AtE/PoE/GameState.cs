using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using static AtE.Globals;
using System.Text;
using System.Collections.Generic;
using System.Drawing;

namespace AtE {

	public class GameRoot : MemoryObject<Offsets.GameRoot>, IDisposable {

		// Address gets assigned after the constructor finishes
		// (because the usage is always new T() { Address = xxx })
		// So this property override does the final construction
		public new IntPtr Address {
			get => base.Address;
			set {

				if ( IsDisposed || value == base.Address ) return;

				if ( base.Address != IntPtr.Zero ) {
					StateMachine.DefaultMachine.Remove(InGameState);
					StateMachine.DefaultMachine.Remove(AreaLoadingState);
				}

				base.Address = value;

				if ( base.Address != IntPtr.Zero ) {
					Log($"GameStateBase: Loading from ${Format(base.Address)}...");
					InGameState = new InGameState() { Address = Cache.InGameState.ptrToGameState };
					EscapeState = new EscapeState() { Address = Cache.EscapeState.ptrToGameState };
					AreaLoadingState = new AreaLoadingState() { Address = Cache.AreaLoadingState.ptrToGameState };
					// the other states are here in the Cache if we want them, but they are useless

					if ( IsValid(InGameState) ) {
						StateMachine.DefaultMachine.Add(InGameState);
					}

					if ( IsValid(AreaLoadingState) ) {
						StateMachine.DefaultMachine.Add(AreaLoadingState);
					}
				}
			}
		}

		public bool IsValid => base.Address != IntPtr.Zero
			&& Cache.ActiveGameStates.ItemCount(Marshal.SizeOf(typeof(Offsets.GameStateArrayEntry))) > 0;


		public InGameState InGameState;
		public EscapeState EscapeState;
		public AreaLoadingState AreaLoadingState;


		public bool IsActive() => ActiveGameStates.Any(s => s.Address == Address);

		private IEnumerable<GameState> ActiveGameStates =>
			new ArrayHandle<Offsets.GameStateArrayEntry>(Cache.ActiveGameStates)
				.Select(x => new GameState() { Address = x.ptrToGameState });

		public bool IsDisposed { get; private set; } = false;
		private bool isDisposing = false;
		public override void Dispose() {
			if ( IsDisposed || isDisposing ) return;
			isDisposing = true;
			StateMachine.DefaultMachine.Remove(InGameState);
			StateMachine.DefaultMachine.Remove(AreaLoadingState);
			Address = IntPtr.Zero;
			IsDisposed = true;
			base.Dispose();
		}
	}

	public class GameState : GameState<Offsets.Empty> { }

	public class GameState<T> : MemoryObject<T>, IState where T : unmanaged {

		// this is the only property done as a direct read (without the Cached<>)
		// because of the complexity of the templated inheritance
		// it's almost never called except from something like the Object Browser
		public Offsets.GameStateType Kind =>
			PoEMemory.TryRead(Address + Offsets.GameState_Kind, out Offsets.GameStateType kind)
			? kind
			: Offsets.GameStateType.InvalidState;

		public string Name => $"{Kind}";

		// Implement the IState interface, so sub-classes can run in a StateMachine
		public virtual IState OnTick(long dt) => this;
		public virtual IState OnEnter() => this;
		public virtual void OnCancel() { }
		public IState Next { get; set; } = null;
		public IState Tail() => Next == null ? this : Next.Tail();

	}

	public class AreaLoadingState : GameState<Offsets.AreaGameState> {
		public bool IsLoading => Cache.IsLoading == 1;
		public string AreaName => PoEMemory.TryReadString(Cache.strAreaName, Encoding.Unicode, out string val) ? val : null;

		private bool loadingBefore = true; // starting as true causes OnAreaChange to fire once after Attach()
		public override IState OnTick(long dt) {
			bool loadingNow = IsLoading;
			if( loadingBefore && !loadingNow ) {
				OnAreaChange?.Invoke(this, AreaName);
			}
			loadingBefore = loadingNow;
			return this;
		}

	}
	public class WaitingState : GameState<Offsets.Empty> { }
	public class CreditsState : GameState<Offsets.Empty> { }
	public class EscapeState : GameState<Offsets.EscapeGameState> {
		public Element UIRoot => Address == IntPtr.Zero ? null :
			new Element() { Address = Cache.elemRoot };
	}

	public class InGameState : GameState<Offsets.InGameState> {
		private readonly Cached<Offsets.InGameState_Data> Data;
		private readonly Cached<Entity[]> entities;
		private readonly Dictionary<uint, int> entityIndex = new Dictionary<uint, int>();

		public InGameState():base() {
			Data = CachedStruct<Offsets.InGameState_Data>(() => Cache.ptrData);
			entities = new Cached<Entity[]>(() =>
				(PoEMemory.TryRead(Data.Value.EntityListHead, out Offsets.EntityListNode tree)
				? GetEntities(tree) : Empty<Entity>()).ToArray());
		}

		public Element UIRoot => Cache.elemRoot == IntPtr.Zero ? null :
			new Element() { Address = Cache.elemRoot };

		public WorldData WorldData => Cache.ptrWorldData == IntPtr.Zero ? null :
			new WorldData() { Address = Cache.ptrWorldData };

		public PlayerEntity Player => Data.Value.entPlayer == IntPtr.Zero ? null :
			new PlayerEntity() { Address = Data.Value.entPlayer };

		public Element Hovered => Cache.elemHover == IntPtr.Zero ? null :
			new Element() { Address = Cache.elemHover };

		/// <summary>
		/// Is there some UI Element currently capturing keyboard input.
		/// Eg, one of the filter inputs on a stash tab.
		/// </summary>
		public bool HasInputFocus => Cache.elemInputFocus != IntPtr.Zero;
		/// <summary>
		/// Returns the UI Element that has currently captured keyboard input.
		/// </summary>
		public Element Focused => Cache.elemInputFocus == IntPtr.Zero ? null :
			new Element() { Address = Cache.elemInputFocus};

		/// <summary>
		/// A library of shortcuts into the UI tree, for the more useful elements,
		/// and returns extra helper types.
		/// </summary>
		public UIElementLibrary UIElements => new UIElementLibrary() { Address = Cache.ptrUIElements };

		/// <summary>
		/// Yields all the current entities (with a server Id).
		/// This excludes entities like client-only effect animations (for now).
		/// Cached such that it is fresh once per frame.
		/// </summary>
		public IEnumerable<Entity> Entities => entities.Value;

		public Entity GetEntityById(uint id) => entityIndex.TryGetValue(id, out int index) ? entities.Value[index] : null;

		private IEnumerable<Entity> GetEntities(Offsets.EntityListNode tree) {
			entityIndex.Clear();
			int index = 0;
			HashSet<long> deduper = new HashSet<long>();
			Stack<Offsets.EntityListNode> frontier = new Stack<Offsets.EntityListNode>();
			frontier.Push(tree);
			while ( frontier.Count > 0 ) {
				var node = frontier.Pop();
				long key = node.Entity.ToInt64();
				if ( !deduper.Contains(key) ) {
					deduper.Add(key);

					var ent = new Entity() { Address = node.Entity };
					var id = ent.Id;
					if ( id > 0 && id < int.MaxValue ) {
						entityIndex[id] = index++;
						yield return ent;
					}

					if ( PoEMemory.TryRead(node.First, out Offsets.EntityListNode first) ) {
						frontier.Push(first);
					}
					if ( PoEMemory.TryRead(node.Second, out Offsets.EntityListNode second) ) {
						frontier.Push(second);
					}
					if ( PoEMemory.TryRead(node.Third, out Offsets.EntityListNode third) ) {
						frontier.Push(third);
					}
				}
			}
		}

		public override IState OnTick(long dt) {
			if ( IsDisposed ) return null;

			// DrawBottomLeftText($"{PoEMemory.GameRoot.InGameState.Hovered?}");

			/* How to debug UI Elements offsets:
			ImGui.Begin("UI Elements");
			try {
				Offsets.InGameData_UIElements ui = PoEMemory.GameRoot?.InGameState?.UIElements;
				var seen = new HashSet<int>();
				foreach ( var field in ui.GetType().GetFields().OrderBy(f => f.Name) ) {
					var elem = new Element() { Address = (IntPtr)field.GetValue(ui) };
					if( IsValid(elem) ) {
						ImGui.PushStyleColor(ImGuiCol.Text, (uint)ToRBGA(Color.White));
					} else {
						ImGui.PushStyleColor(ImGuiCol.Text, (uint)ToRBGA(Color.Red));
					}
					if( ImGui_ObjectLabel(field.Name, elem, seen) ) {
						ImGui_Object(field.Name, elem, seen);
						ImGui_ObjectLabelPop();
					}
					ImGui.PopStyleColor();
				}
			} finally {
				ImGui.End();
			}
			*/

			/* How to debug a Component:
			foreach(var ent in Entities.Where(e => e.HasComponent<Player>()) ) {
				// DrawTextAt(ent, "Chest", Color.Orange);
				var screen = WorldToScreen(ent.GetComponent<Render>()?.Position ?? default);
				ImGui.SetNextWindowPos(screen);
				if( ImGui.Begin($"Item @ {ent.Address}") ) {
					var seen = new HashSet<int>();
					ImGui.Text(ent.Path);
					ImGui.Text("Player");
					ImGui_Object("Player", ent.GetComponent<Player>(), seen);
					ImGui.End();
				}
			}
			*/
			return base.OnTick(dt);
		}

		private bool IsDisposed = false;
		private bool isDisposing = false;
		public override void Dispose() {
			if ( isDisposing || IsDisposed ) return;
			isDisposing = true;
			Data?.Dispose();
			entities.Dispose();
			IsDisposed = true;
			base.Dispose();
		}
	}

	public class PreGameState : GameState<Offsets.PreGameState> {
		public Element UIRoot => Address == IntPtr.Zero ? null :
			new Element() { Address = Cache.UIRoot };
	}

	public class WorldData : MemoryObject<Offsets.WorldData> {
		public Offsets.Camera Camera => Cache.Camera;
		public Cached<Offsets.WorldAreaDetails> AreaDetails;
		public WorldData() : base() => AreaDetails = CachedStruct<Offsets.WorldAreaDetails>(
			() => PoEMemory.TryRead(Cache.ptrToWorldAreaRef, out Offsets.WorldAreaRef ptr)
				? ptr.ptrToWorldAreaDetails : IntPtr.Zero
			);

		public bool IsTown => AreaDetails.Value.IsTown;

		private string areaName = null;
		public string AreaName => areaName ?? (PoEMemory.TryReadString(AreaDetails.Value.strName, Encoding.Unicode, out areaName) ? areaName : null);

		// TODO: won't work in non-English
		public bool IsHideout => AreaName.Contains("Hideout");
	}

	public static partial class Globals {

		public static UIElementLibrary GetUI() => PoEMemory.GameRoot?.InGameState?.UIElements ?? default;

		public static Offsets.Camera GetCamera() => PoEMemory.GameRoot?.InGameState?.WorldData?.Camera ?? default;
		public static Vector2 WorldToScreen(Vector3 pos) => GetCamera().WorldToScreen(pos);
		public static void DrawTextAt(Vector3 pos, string text, Color color) => DrawTextAt(WorldToScreen(pos), text, color);

		public static bool IsValid(GameRoot state) => state != null && state.IsValid;
		public static bool IsValid(AreaLoadingState state) => state != null && state.Kind == Offsets.GameStateType.AreaLoadingState;
		public static bool IsValid(EscapeState state) => state != null && state.Kind == Offsets.GameStateType.EscapeState;
		public static bool IsValid(InGameState state) => state != null && state.Kind == Offsets.GameStateType.InGameState;

	}
}
