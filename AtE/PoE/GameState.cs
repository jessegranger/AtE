using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using static AtE.Globals;
using System.Text;
using System.Collections.Generic;

namespace AtE {

	public class GameStateBase : MemoryObject, IDisposable {
		public Cached<Offsets.GameStateBase> Cache;

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
					Log($"GameStateBase: Loading from ${base.Address}...");
					InGameState = new InGameState() { Address = Cache.Value.InGameState.ptrToGameState };
					EscapeState = new EscapeState() { Address = Cache.Value.EscapeState.ptrToGameState };
					AreaLoadingState = new AreaLoadingState() { Address = Cache.Value.AreaLoadingState.ptrToGameState };
					// the other states are here in the Cache.Value if we want them, but they are useless

					if ( IsValid(InGameState) ) {
						StateMachine.DefaultMachine.Add(InGameState);
					}

					if ( IsValid(AreaLoadingState) ) {
						StateMachine.DefaultMachine.Add(AreaLoadingState);
					}
					Run_ObjectBrowser($"GameStateBase @ 0x{(long)value:X}", this);
				}
			}
		}

		public GameStateBase() : base() => Cache = CachedStruct<Offsets.GameStateBase>(this);

		public InGameState InGameState;
		public EscapeState EscapeState;
		public AreaLoadingState AreaLoadingState;


		public bool IsActive() => ActiveGameStates.Any(s => s.Address == Address);

		private IEnumerable<GameState> ActiveGameStates =>
			Cache.Value.ActiveGameStates.GetItems<Offsets.GameStateArrayEntry>()
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

		public Offsets.GameStateType Kind => PoEMemory.Read<Offsets.GameStateType>(Address + Offsets.GameState_Kind);

		public string Name => $"{Kind}";
		public virtual IState OnTick(long dt) {
			return this;
		}
		public IState OnEnter() => this;
		public void OnCancel() { }
		public IState Next { get; set; } = null;
		public IState Tail() => Next == null ? this : Next.Tail();

	}

	public class AreaLoadingState : GameState<Offsets.AreaGameState> {
		public bool IsLoading => Struct.IsLoading == 1;
		public string AreaName => PoEMemory.TryReadString(Struct.strAreaName, Encoding.Unicode, out string val) ? val : null;

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
			new Element() { Address = Struct.elemRoot };
	}

	public class InGameState : GameState<Offsets.InGameState> {
		public Cached<Offsets.InGameState_Data> Data;

		public InGameState():base() {
			Data = CachedStruct<Offsets.InGameState_Data>(() => Struct.ptrData);
			entities = new Cached<Entity[]>(() =>
				(!PoEMemory.TryRead(Data.Value.EntityList, out Offsets.EntityListNode tree)
				? Empty<Entity>()
				: GetEntities(tree)).ToArray());
		}

		public Element UIRoot => Address == IntPtr.Zero || IsDisposed ? null :
			new Element() { Address = Struct.elemRoot };

		public WorldData WorldData => Address == IntPtr.Zero || IsDisposed ? null :
			new WorldData() { Address = Struct.ptrWorldData };

		public Entity Player => Address == IntPtr.Zero || IsDisposed ? null :
			new Entity() { Address = Data.Value.entPlayer };

		private readonly Cached<Entity[]> entities;
		/// <summary>
		/// Yields all the current entities (with a server Id).
		/// This excludes entities like client-only effect animations (for now).
		/// Cached such that it is fresh once per frame.
		/// </summary>
		public IEnumerable<Entity> Entities => Address == IntPtr.Zero
			|| IsDisposed
			|| ! PoEMemory.TryRead(Data.Value.EntityList, out Offsets.EntityListNode tree)
			? Empty<Entity>()
			: GetEntities(tree);

		private static IEnumerable<Entity> GetEntities(Offsets.EntityListNode tree) {
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

			/*
			var player = GetPlayer();
			ImGui.Begin("Component Debug");
			var ptr = player.Details.Value.ComponentLookupPtr;
			ImGui_Address(player.Address, "Player Base:");
			Debugger.RegisterOffset("Player Base", player.Address);
			Debugger.RegisterStructLabels<Offsets.Entity>("Player", player.Address);
			ImGui.Text("Details struct:");
			ImGui_Object("Details", player.Details.Value, new HashSet<int>());
			Debugger.RegisterOffset("Details", player.Cache.Value.DetailsPtr);
			Debugger.RegisterStructLabels<Offsets.EntityDetails>("Details", player.Cache.Value.DetailsPtr);
			var lookup = PoEMemory.Read<Offsets.ComponentLookup>(ptr);
			ImGui_Address(ptr, "Details.ComponentLookupPtr");
			Debugger.RegisterStructLabels<Offsets.ComponentLookup>("Details.ComponentLookupPtr", ptr);
			ImGui_Object("ComponentLookup", lookup, new HashSet<int>());
			*/

			/*
			if ( lookup.Capacity < 1 || lookup.Capacity > 24 ) {
				return result;
			}
			// each entry in the array packs 8 items into one struct, so we read N / 8 structs
			int trueCapacity = (int)((lookup.Capacity + 1) / 8);
			var componentArray = new Offsets.ComponentArrayEntry[trueCapacity];
			if( 0 == PoEMemory.TryRead(lookup.ComponentArray, componentArray) ) {
			*/

			// ImGui.End();

			/*
			ImGui.SetNextWindowSize(new Vector2(300, 200), ImGuiCond.FirstUseEver);
			ImGui.Begin("Entity List");
			ImGui_Address(Data.Value.EntityList, "Data.EntityList");
			var ents = new EntityList() { Address = Data.Value.EntityList };
			ImGui_Address(ents.Cache.Value.Head, "EntityList.Head");
			var head = new EntityListNode() { Address = Data.Value.EntityList };
			if( ImGui.Button($"Browse##{ents.Address}") ) {
				Run_ObjectBrowser("EntityTree", head);
			}
			HashSet<long> deduper = new HashSet<long>();
			Stack<EntityListNode> frontier = new Stack<EntityListNode>();
			frontier.Push(head);
			while(frontier.Count>0) {
				var node = frontier.Pop();
				if ( deduper.Contains(node.Address) ) continue;
				deduper.Add(node.Address);
				frontier.Push(node.First);
				frontier.Push(node.Second);
				frontier.Push(node.Third);
			}
			*/

			long started = Time.ElapsedMilliseconds;
			if ( PoEMemory.TryRead(Data.Value.EntityList, out Offsets.EntityListNode tree) ) {
				var allEnts = GetEntities(tree).Count();
				ImGui.Text($"Found {allEnts} ents in {Time.ElapsedMilliseconds - started}ms");
			}

			// ImGui.End();

			return base.OnTick(dt);
		}

		public bool IsDisposed = false;
		private bool isDisposing = false;
		public override void Dispose() {
			if ( isDisposing || IsDisposed ) return;
			isDisposing = true;
			Data?.Dispose();
			Data = null;
			entities.Dispose();
			IsDisposed = true;
			base.Dispose();
		}
	}

	public class ChangePasswordState : GameState<Offsets.Empty> { }
	public class LoginState : GameState<Offsets.Empty> { }
	public class PreGameState : GameState<Offsets.PreGameState> {
		public Element UIRoot => Address == IntPtr.Zero ? null :
			new Element() { Address = Struct.UIRoot };
	}
	public class CreateCharacterState : GameState<Offsets.Empty> { }
	public class SelectCharacterState : GameState<Offsets.Empty> { }
	public class DeleteCharacterState : GameState<Offsets.Empty> { }
	public class LoadingState : GameState<Offsets.Empty> { }

	public class WorldData : MemoryObject<Offsets.WorldData> {
		public Offsets.Camera Camera => Struct.Camera;
	}

	public static partial class Globals {

		public static Entity GetPlayer() => PoEMemory.GameRoot?.InGameState?.Player;

		public static IEnumerable<Entity> GetEntities() => PoEMemory.GameRoot?.InGameState?.Entities;

		public static bool IsValid(AreaLoadingState state) => state != null && state.Kind == Offsets.GameStateType.AreaLoadingState;
		public static bool IsValid(WaitingState state) => state != null && state.Kind == Offsets.GameStateType.WaitingState;
		public static bool IsValid(CreditsState state) => state != null && state.Kind == Offsets.GameStateType.CreditsState;
		public static bool IsValid(EscapeState state) => state != null && state.Kind == Offsets.GameStateType.EscapeState;
		public static bool IsValid(InGameState state) => state != null && state.Kind == Offsets.GameStateType.InGameState;
		public static bool IsValid(ChangePasswordState state) => state != null && state.Kind == Offsets.GameStateType.ChangePasswordState;
		public static bool IsValid(LoginState state) => state != null && state.Kind == Offsets.GameStateType.LoginState;
		public static bool IsValid(PreGameState state) => state != null && state.Kind == Offsets.GameStateType.PreGameState;
		public static bool IsValid(CreateCharacterState state) => state != null && state.Kind == Offsets.GameStateType.CreateCharacterState;
		public static bool IsValid(SelectCharacterState state) => state != null && state.Kind == Offsets.GameStateType.SelectCharacterState;
		public static bool IsValid(DeleteCharacterState state) => state != null && state.Kind == Offsets.GameStateType.DeleteCharacterState;
		public static bool IsValid(LoadingState state) => state != null && state.Kind == Offsets.GameStateType.LoadingState;

	}
}
