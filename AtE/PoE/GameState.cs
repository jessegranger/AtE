﻿using ImGuiNET;
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
		public override IntPtr Address {
			get => base.Address;
			set {

				if ( IsDisposed || value == base.Address ) return;

				if ( base.Address != IntPtr.Zero ) {
					Machine.DefaultMachine.Remove(InGameState);
					Machine.DefaultMachine.Remove(AreaLoadingState);
				}

				base.Address = value;

				if ( base.Address != IntPtr.Zero ) {
					Log($"GameStateBase: Loading from base ${Describe(base.Address)}...");
					Log($"GameStateBase: InGameState @ {Describe(Cache.InGameState.ptrToGameState)}...");
					InGameState = new InGameState() { Address = Cache.InGameState.ptrToGameState };
					Log($"GameStateBase: EscapeState @ {Describe(Cache.EscapeState.ptrToGameState)}...");
					EscapeState = new EscapeState() { Address = Cache.EscapeState.ptrToGameState };
					Log($"GameStateBase: AreaLoadingState @ {Describe(Cache.AreaLoadingState.ptrToGameState)}...");
					AreaLoadingState = new AreaLoadingState() { Address = Cache.AreaLoadingState.ptrToGameState };
					Log($"GameStateBase: LoginState @ {Describe(Cache.LoginState.ptrToGameState)}...");
					LoginState = new LoginState() { Address = Cache.LoginState.ptrToGameState };
					// the other states are here in the Cache if we want them, but they are useless

					if ( IsValid(InGameState) ) {
						Machine.DefaultMachine.Add(InGameState);
					}

					if ( IsValid(AreaLoadingState) ) {
						Machine.DefaultMachine.Add(AreaLoadingState);
					}
				}
			}
		}

		private static int sizeOfGameStateArrayEntry = Marshal.SizeOf(typeof(Offsets.GameStateArrayEntry));

		public bool IsValid => base.Address != IntPtr.Zero
			&& Cache.ActiveGameStates.ItemCount(sizeOfGameStateArrayEntry) > 0;


		public InGameState InGameState;
		public EscapeState EscapeState;
		public AreaLoadingState AreaLoadingState;
		public LoginState LoginState;


		public bool IsActive() => ActiveGameStates.Any(s => s.Address == Address);

		private IEnumerable<GameState> ActiveGameStates =>
			new ArrayHandle<Offsets.GameStateArrayEntry>(Cache.ActiveGameStates)
				.Select(x => new GameState() { Address = x.ptrToGameState });

		public bool IsDisposed { get; private set; } = false;
		private bool isDisposing = false;
		public override void Dispose() {
			if ( IsDisposed || isDisposing ) return;
			isDisposing = true;
			Machine.DefaultMachine.Remove(InGameState);
			Machine.DefaultMachine.Remove(AreaLoadingState);
			Address = IntPtr.Zero;
			IsDisposed = true;
			base.Dispose();
		}
	}

	// GameState is a part of the PoE engine, which has at it's top level an array of such.
	public class GameState : GameState<Offsets.Empty> { }

	// GameState<T> allows to specify T, a struct, that defines the layout of memory of the GameState in PoE's memory
	public class GameState<T> : MemoryObject<T>, IState where T : unmanaged {

		// this is the only property done as a direct read (without the MemoryObject creating Cached<> value)
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

		private IntPtr lastKnownAreaNamePtr;
		private string lastKnownAreaName;
		public string AreaName => Cache.strAreaName == lastKnownAreaNamePtr ? lastKnownAreaName :
			PoEMemory.TryReadString(lastKnownAreaNamePtr = Cache.strAreaName, Encoding.Unicode, out lastKnownAreaName) ? lastKnownAreaName : null;

		private bool loadingBefore = true; // starting as true causes OnAreaChange to fire once after Attach()
		public override IState OnTick(long dt) {
			bool loadingNow = IsLoading;
			if( loadingBefore && !loadingNow ) {
				lastKnownAreaNamePtr = IntPtr.Zero;
				OnAreaChange?.Invoke(this, AreaName);
			}
			loadingBefore = loadingNow;
			return this;
		}

	}

	public class LoginState : GameState<Offsets.LoginGameState> {
		public Element UIRoot => IsValid(Address) && ElementCache.TryGetElement(Cache.elemRoot, out Element root) ? root : null;

	}
	public class WaitingState : GameState<Offsets.Empty> { }
	public class CreditsState : GameState<Offsets.Empty> { }
	public class EscapeState : GameState<Offsets.EscapeGameState> {
		public Element UIRoot => IsValid(Address) && ElementCache.TryGetElement(Cache.elemRoot, out Element root) ? root : null;
	}

	public class InGameState : GameState<Offsets.InGameState> {
		public readonly Cached<Offsets.InGameState_Data> Data;

		public InGameState() : base() {
			Data = CachedStruct<Offsets.InGameState_Data>(() => Cache.ptrData);
		}

		internal Offsets.EntityListNode EntityListHead => IsValid(Address)
			&& IsValid(Data.Value.EntityListHead)
			&& PoEMemory.TryRead(Data.Value.EntityListHead, out Offsets.EntityListNode tree) ? tree : default;

		public Element UIRoot => IsValid(Address) && ElementCache.TryGetElement(Cache.elemRoot, out Element root) ? root : null;

		public IEnumerable<Entity> GetEntities() => EntityCache.GetEntities(); // mostly to make it easy to browse

		public WorldData WorldData => Cache.ptrWorldData == IntPtr.Zero ? null :
			new WorldData() { Address = Cache.ptrWorldData };

		private IntPtr lastKnownPlayerAddress = IntPtr.Zero;
		private PlayerEntity lastKnownPlayer = null;
		public PlayerEntity Player {
			get {
				if( Data.Value.entPlayer != lastKnownPlayerAddress ) {
					lastKnownPlayerAddress = Data.Value.entPlayer;
					lastKnownPlayer = new PlayerEntity() { Address = lastKnownPlayerAddress };
				}
				return lastKnownPlayer;
			}
		}

		public bool IsPaused => (Data.Value.PauseByte & Offsets.InGameState_Data.PauseMask) != 0x00;

		public Element Hovered => IsValid(Address) && ElementCache.TryGetElement(Cache.elemHover, out Element hover) ? hover : null;

		/// <summary>
		/// Is there some UI Element currently capturing keyboard input.
		/// Eg, one of the filter inputs on a stash tab.
		/// </summary>
		public bool HasInputFocus => Cache.elemInputFocus != IntPtr.Zero;

		/// <summary>
		/// Returns the UI Element that has currently captured keyboard input.
		/// </summary>
		public Element Focused => IsValid(Address) && ElementCache.TryGetElement(Cache.elemInputFocus, out Element elem) ? elem : null;

		/// <summary>
		/// A library of shortcuts into the UI tree, for the more useful elements,
		/// and returns extra helper types.
		/// </summary>
		public UIElementLibrary UIElements => IsValid(Cache.ptrUIElements) ? new UIElementLibrary() { Address = Cache.ptrUIElements } : null;

		public override IState OnTick(long dt) {
			if ( IsDisposed ) return null;

			/*
			// Experiment: once each frame, forcefully invalidate the player's buff cache
			PlayerEntity p = GetPlayer();
			if( IsValid(p) ) {
				p.GetComponent<Buffs>()?.Invalidate();
			}
			*/

			/*
			ImGui.Begin("Debug Buff");
			var p = GetPlayer();
			if( IsValid(p) ) {
				var ignite = p.Buffs.GetBuffs().FirstOrDefault((b) => b?.Name?.Equals("ignited") ?? false);
				if( IsValid(ignite) ) {
					ImGui.Text("Ignited.");
					ImGui_Object("ignite_buff", "ignite_buff", ignite, new HashSet<int>());
					// Log($"ignite status: {ignite.Cache}");
				} else {
					ImGui.Text("Not ignited.");
				}
			}
			ImGui.End();
			*/

			/*
			ImGui.Begin("Debug Camera");
			var c = GetCamera();
			ImGui.Text($"Camera:");
			ImGui.Indent();
			ImGui.Text($"Position: {c.Position}");
			ImGui.Text($"Size: {c.Size}");
			ImGui.Text($"Matrix: {c.Matrix.M11} {c.Matrix.M12} {c.Matrix.M13} {c.Matrix.M14}");
			ImGui.Text($"Matrix: {c.Matrix.M21} {c.Matrix.M22} {c.Matrix.M23} {c.Matrix.M24}");
			ImGui.Text($"Matrix: {c.Matrix.M31} {c.Matrix.M32} {c.Matrix.M33} {c.Matrix.M34}");
			ImGui.Text($"Matrix: {c.Matrix.M41} {c.Matrix.M42} {c.Matrix.M43} {c.Matrix.M44}");
			ImGui.Unindent();
			PlayerEntity p = GetPlayer();
			if( IsValid(p) ) {
				Vector3 pos = Position(p);
				ImGui.Text($"Player Position: {pos}");
				ImGui.Text($"Player WorldToScreen: {WorldToScreen(pos)}");
				var ui = GetUI();
				if( IsValid(ui) ) {
					Vector2 entGrid = p.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
					Vector3 entPos = p.GetComponent<Render>()?.Position ?? Vector3.Zero;
					var mapPos = ui.Map.WorldToLargeMap(entGrid, entPos, entGrid, entPos);
					ImGui.Text($"Player WorldToLargeMap: {mapPos}");

					DeployedObject minion = p.GetComponent<Actor>()?.DeployedObjects?.FirstOrDefault() ?? null;
					if( minion != null ) {
						var minionEnt = minion.GetEntity();
						if( IsValid(minionEnt) ) {
							var minionGrid = minionEnt.GetComponent<Positioned>()?.GridPosF ?? Vector2.Zero;
							var minionPos = minionEnt.GetComponent<Render>()?.Position ?? Vector3.Zero;
							mapPos = ui.Map.WorldToLargeMap(minionGrid, minionPos, entGrid, entPos);
							ImGui.Text($"Minion WorldToLargeMap: {mapPos}");
							mapPos = ui.Map.WorldToMinimap(minionGrid, minionPos, entGrid, entPos);
							ImGui.Text($"Minion WorldToMiniMap: {mapPos}");
						}
					}
				}
			}
			*/
			/*
			Vector2 cursorPos = new Vector2(0, 0);
			int gridInterval = 100;
			for ( int x = 0; x < c.Width; x += gridInterval ) {
				for ( int y = 0; y < c.Height; y += gridInterval ) {
					Vector2 xy = new Vector2(x, y);
					DrawSprite(SpriteIcon.SmallCyanCircle, xy, 10, 10);
					DrawTextAt(xy, $"{x},{y}", Color.White);
				}
			}
			ImGui.End();
			*/

			/*
			if ( false ) {
				ImGui.Begin("Entities");
				try {
					var pos = GridPosition(GetPlayer());
					foreach ( var ent in GetEntities().Where((ent) => ent.HasComponent<Chest>()).OrderBy((ent) => DistanceSq(GridPosition(ent), pos)).Take(10) ) {
						ImGui.Text(ent.Path);
						ImGui.Text(string.Join(" ", ent.GetComponentNames()));
						//if ( ent.Path.Contains("LeaguesExpedition") ) {
						var chest = ent.GetComponent<Chest>();
						if ( IsValid(chest) ) {
							if ( !chest.IsOpened ) {
								ImGui.SetNextWindowPos(WorldToScreen(Position(ent)));
								ImGui.Begin($"Chest Window##{ent.Id}");
								ImGui.Text(ent.Path);
								ImGui_Object($"Icon##{ent.Id}", "Icon", ent.MinimapIcon, new HashSet<int>());
								ImGui.SameLine();
								if( ImGui.Button("Reset Icon") ) {
									ent.MinimapIcon.Size = 0f;
								}
								ImGui_Object($"MinimapIcon##{ent.Id}", "MinimapIcon", ent.GetComponent<MinimapIcon>(), new HashSet<int>());
								ImGui.End();
								// DrawLine(WorldToScreen(Position(GetPlayer())), WorldToScreen(Position(ent)), Color.Yellow);
							}
						}
						//}
						ImGui.Separator();
					}
				} finally {
					ImGui.End();
				}
			}
			*/

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
			// entities.Dispose();
			IsDisposed = true;
			base.Dispose();
		}
	}

	public class PreGameState : GameState<Offsets.PreGameState> {
		public Element UIRoot => Address == IntPtr.Zero ? null :
			new Element() { Address = Cache.elemRoot };
	}

	public class WorldData : MemoryObject<Offsets.WorldData> {
		public Offsets.Camera Camera => Cache.Camera;
		public Cached<Offsets.WorldAreaDetails> AreaDetails;
		public Cached<Offsets.WorldAreaRef> AreaRef;
		public WorldData() : base() {
			AreaRef = CachedStruct<Offsets.WorldAreaRef>(() => Cache.ptrToWorldAreaRef);
			AreaDetails = CachedStruct<Offsets.WorldAreaDetails>(() => AreaRef.Value.ptrToWorldAreaDetails);

				// PoEMemory.TryRead(Cache.ptrToWorldAreaRef, out Offsets.WorldAreaRef ptr)
					// ? ptr.ptrToWorldAreaDetails : IntPtr.Zero
			// );
		}

		public bool IsTown => AreaDetails.Value.IsTown;

		private string areaName = null;
		public string AreaName => areaName ?? (PoEMemory.TryReadString(AreaDetails.Value.strName, Encoding.Unicode, out areaName) ? areaName : null);

		public uint AreaId => AreaDetails.Value.WorldAreaId;
		public uint AreaHash => AreaRef.Value.areaHash;

		// TODO: won't work in non-English
		public bool IsHideout => AreaName.Contains("Hideout");
	}

	public static partial class Globals {

		public static UIElementLibrary GetUI() => PoEMemory.GameRoot?.InGameState?.UIElements ?? default;

		public static Offsets.Camera GetCamera() => PoEMemory.GameRoot?.InGameState?.WorldData?.Camera ?? default;
		public static Vector2 WorldToScreen(Vector3 pos) {
			/* Debug: 
			ImGui_Address(PoEMemory.GameRoot.InGameState.WorldData.Address
				+ GetOffset<Offsets.WorldData>("Camera"),
				"Camera Address", "Camera");

			ImGui.Text($"Position: {camera.Position} ZFar: {camera.ZFar}");
			ImGui.Text("Matrix:");
			ImGui.Text($"{camera.Matrix.M11}, {camera.Matrix.M12}, {camera.Matrix.M13}, {camera.Matrix.M14}");
			ImGui.Text($"{camera.Matrix.M21}, {camera.Matrix.M22}, {camera.Matrix.M23}, {camera.Matrix.M24}");
			ImGui.Text($"{camera.Matrix.M31}, {camera.Matrix.M32}, {camera.Matrix.M33}, {camera.Matrix.M34}");
			ImGui.Text($"{camera.Matrix.M41}, {camera.Matrix.M42}, {camera.Matrix.M43}, {camera.Matrix.M44}");
			ImGui.Text($"old WorldToScreen: {result}");
			*/
			var rect = WindowSize.Value;
			return GetCamera().WorldToScreen(pos, rect.X, rect.Y);
		}
		public static Vector2 GridToScreen(Offsets.Vector2i pos, float z = 0) => WorldToScreen(Offsets.GridToWorld(pos, z));
		public static void DrawTextAt(Vector3 pos, string text, Color color) => DrawTextAt(WorldToScreen(pos), text, color);

		public static bool IsValid(GameRoot state) => state != null && state.IsValid;
		public static bool IsValid(AreaLoadingState state) => state != null && state.Kind == Offsets.GameStateType.AreaLoadingState;
		public static bool IsValid(EscapeState state) => state != null && state.Kind == Offsets.GameStateType.EscapeState;
		public static bool IsValid(InGameState state) => state != null && state.Kind == Offsets.GameStateType.InGameState;

	}
}
