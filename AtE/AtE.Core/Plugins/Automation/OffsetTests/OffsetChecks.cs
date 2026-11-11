using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The script of OffsetChecks for a fresh character standing on the beach of The Twilight Strand, in order: the roots,
	/// the area, the player entity, the player's components. (Later steps: the UI, hotkeys, the minimap, and observers that wait
	/// for monsters, items and NPCs.) Each says which Offsets members it covers; a check whose offsets everything else rests on is
	/// a DependsOn of those that follow, so a wrong root shows as one failure and a lot of Blocked, not a hundred failures.
	/// Values that only a fresh character has are goldens (see GoldenStore).
	/// </summary>
	public static partial class OffsetChecks {

		public static List<OffsetCheck> All() {
			var list = new List<OffsetCheck>();
			list.AddRange(Roots());
			list.AddRange(Area());
			list.AddRange(PlayerEntity());
			list.AddRange(PlayerComponents());
			list.AddRange(Projection());
			list.AddRange(Movement());
			list.AddRange(UI());
			list.AddRange(MiniMapAtRest());
			list.AddRange(Hotkeys());
			list.AddRange(Pointer());
			var ids = new HashSet<string>();
			foreach ( var c in list ) {
				if ( !ids.Add(c.Id) ) throw new InvalidOperationException($"OffsetChecks: duplicate id {c.Id}");
				foreach ( var d in c.DependsOn ) if ( !list.Any(o => o.Id == d) ) throw new InvalidOperationException($"OffsetChecks: {c.Id} depends on unknown {d}");
			}
			return list;
		}

		private static OffsetCheck Check(string id, string[] covers, Func<TestContext, Outcome> run, string[] dependsOn = null, bool needsPlayer = true, int timeoutMs = 3000, bool optionalTarget = false) => new OffsetCheck {
			Id = id,
			Covers = covers,
			Run = run,
			DependsOn = dependsOn ?? Array.Empty<string>(),
			NeedsPlayer = needsPlayer,
			TimeoutMs = timeoutMs,
			OptionalTarget = optionalTarget,
		};

		/// <summary>Every component named (of those the entity has) keeps the entity's address at +0x8 (entOwner).</summary>
		internal static void Owners(TestContext c, Entity e, params string[] names) {
			var components = e.GetComponents();
			foreach ( var name in names ) {
				if ( !components.TryGetValue(name, out IntPtr ptr) ) continue;
				if ( !PoEMemory.TryRead(ptr + 8, out IntPtr owner) || owner != e.Address ) c.That(false, $"{name}'s entOwner (+0x8) is not the entity");
			}
		}

		private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

		// ---------------------------------------------------------------- A1: the roots

		private static IEnumerable<OffsetCheck> Roots() {
			yield return Check("A1.GameRoot", new[] { "GameRoot.*", "GameStateArrayEntry.*", "GameRoot_Ref.ptrToGameRootPtr" }, c => {
				var root = PoEMemory.GameRoot;
				if ( root == null || !root.IsValid ) return Outcome.Fail("PoEMemory.GameRoot is not valid (the GameRoot search pattern or Offsets.GameRoot is wrong)");
				var r = root.Cache;
				var entries = new (string name, Offsets.GameStateArrayEntry entry)[] {
					("AreaLoadingState", r.AreaLoadingState), ("WaitingState", r.WaitingState), ("CreditsState", r.CreditsState),
					("EscapeState", r.EscapeState), ("InGameState", r.InGameState), ("ChangePasswordState", r.ChangePasswordState),
					("LoginState", r.LoginState), ("PreGameState", r.PreGameState), ("CreateCharacterState", r.CreateCharacterState),
					("SelectCharacterState", r.SelectCharacterState), ("DeleteCharacterState", r.DeleteCharacterState), ("LoadingState", r.LoadingState),
				};
				foreach ( var e in entries ) c.That(e.entry.IsValid && e.entry.ptrToGameState != IntPtr.Zero, $"{e.name} entry is not valid (its handle should be 16 bytes before the state)");
				c.That(!(r.ProbeState.IsValid && r.ProbeState.ptrToGameState != IntPtr.Zero), "ProbeState has an entry: the game added a GameState");
				c.That(Offsets.IsValid(r.CurrentGameStates), "CurrentGameStates is not a valid array");
				c.That(Offsets.IsValid(r.ActiveGameStates), "ActiveGameStates is not a valid array");
				var active = new ArrayHandle<Offsets.GameStateArrayEntry>(r.ActiveGameStates).ToArray(limit: 20);
				c.That(active.Any(a => a.ptrToGameState == r.InGameState.ptrToGameState), "InGameState is not among the ActiveGameStates");
				c.Note($"{active.Length} active states");
				return c.Result();
			}, needsPlayer: false);

			yield return Check("A1.GameStateKinds", new[] { "InGameState.Kind", "Offsets.GameState_Kind" }, c => {
				var r = PoEMemory.GameRoot.Cache;
				var expect = new (string name, IntPtr ptr, Offsets.GameStateType kind)[] {
					("AreaLoadingState", r.AreaLoadingState.ptrToGameState, Offsets.GameStateType.AreaLoadingState),
					("EscapeState", r.EscapeState.ptrToGameState, Offsets.GameStateType.EscapeState),
					("InGameState", r.InGameState.ptrToGameState, Offsets.GameStateType.InGameState),
				};
				foreach ( var e in expect ) {
					if ( !PoEMemory.TryRead(e.ptr + Offsets.GameState_Kind, out Offsets.GameStateType kind) ) { c.That(false, $"could not read the Kind of {e.name}"); continue; }
					c.Eq($"{e.name}.Kind", kind, e.kind);
				}
				c.Eq("InGameState struct Kind", PoEMemory.GameRoot.InGameState.Cache.Kind, Offsets.GameStateType.InGameState);
				return c.Result();
			}, new[] { "A1.GameRoot" }, needsPlayer: false);

			yield return Check("A1.StateHandles", new[] { "GameStateArrayEntryHandle.vtable", "GameStateArrayEntryHandle.Status" }, c => {
				var entry = PoEMemory.GameRoot.Cache.InGameState;
				if ( !PoEMemory.TryRead(entry.ptrToHandle, out Offsets.GameStateArrayEntryHandle handle) ) return Outcome.Fail("could not read the InGameState handle");
				c.That(Offsets.IsValid(handle.vtable), $"handle vtable {handle.vtable} is not a pointer");
				c.That(handle.Status != Offsets.GameStateHandleStatus.Invalid, "InGameState handle Status is Invalid (0) while playing");
				c.Note($"Status={handle.Status}");
				return c.Result();
			}, new[] { "A1.GameRoot" }, needsPlayer: false);

			yield return Check("A1.AreaLoadingState", new[] { "AreaGameState.IsLoading", "AreaGameState.strAreaName" }, c => {
				var loading = PoEMemory.GameRoot.AreaLoadingState;
				c.That(!loading.IsLoading, "AreaLoadingState says it is loading while in the game");
				string name = loading.AreaName;
				c.That(!string.IsNullOrEmpty(name), "strAreaName did not read");
				c.Note($"area name {name}");
				return c.Result();
			}, new[] { "A1.GameRoot" }, needsPlayer: false);
		}

		// ---------------------------------------------------------------- A2: the area

		private static IEnumerable<OffsetCheck> Area() {
			yield return Check("A2.InGameStatePointers", new[] { "InGameState.ptrData", "InGameState.ptrWorldData", "InGameState.ptrUIElements", "InGameState.elemRoot", "Element.Children", "Element.elemSelf" }, c => {
				var game = c.Game;
				var gs = game.Cache;
				c.That(Offsets.IsValid(gs.ptrData), "ptrData is not a pointer");
				c.That(Offsets.IsValid(gs.ptrWorldData), "ptrWorldData is not a pointer");
				c.That(Offsets.IsValid(gs.ptrUIElements), "ptrUIElements is not a pointer");
				var root = game.UIRoot;
				c.That(IsValid(root), "elemRoot is not an Element");
				if ( IsValid(root) ) {
					c.That(!IsValid(root.Parent) || root.Parent == root, "elemRoot has a parent");
					c.That(root.Children.Count() > 1, "elemRoot has no children");
				}
				return c.Result();
			}, new[] { "A1.GameStateKinds" }, needsPlayer: false);

			// the mouse is put at a few places, each different from the last in both x and y: MousePos has to follow, in both coordinates
			var mouseAt = new[] { new Vector2(0.25f, 0.30f), new Vector2(0.70f, 0.20f), new Vector2(0.40f, 0.60f), new Vector2(0.85f, 0.45f) };
			var mouseCheck = Check("A2.Mouse", new[] { "InGameState.MousePos" }, c => {
				var size = WindowSize.Value;
				int i = c.Scratch.TryGetValue("i", out object stored) ? (int)stored : 0;
				if ( i >= mouseAt.Length ) return c.Result();
				var target = new Vector2((int)(size.X * mouseAt[i].X), (int)(size.Y * mouseAt[i].Y));
				var mouse = c.Game.Cache.MousePos;
				if ( Math.Abs(mouse.X - target.X) <= 2 && Math.Abs(mouse.Y - target.Y) <= 2 ) {
					c.Note($"({mouse.X},{mouse.Y}) for {target}");
					c.Scratch["i"] = i + 1;
					c.Scratch["moved"] = c.Elapsed;
					return i + 1 >= mouseAt.Length ? c.Result() : Outcome.NotYet("next position");
				}
				// the game only learns of the mouse when it moves: keep sending it there
				SkillBar.MouseTo(target);
				if ( c.Elapsed - (c.Scratch.TryGetValue("moved", out object m) ? (long)m : 0) > 2500 ) {
					c.That(false, $"MousePos is ({mouse.X},{mouse.Y}) with the mouse at {target}: X {(Math.Abs(mouse.X - target.X) <= 2 ? "follows" : "does not follow")}, Y {(Math.Abs(mouse.Y - target.Y) <= 2 ? "follows" : "does not follow")}");
					return c.Result();
				}
				return Outcome.NotYet($"moving the mouse to {target}");
			}, new[] { "A2.InGameStatePointers" }, needsPlayer: false, timeoutMs: 12000);
			mouseCheck.Setup = () => { }; // it sends the mouse, so it needs the focus
			yield return mouseCheck;

			yield return Check("A2.WorldArea", new[] {
					"WorldData.ptrToWorldAreaRef", "WorldAreaRef.ptrToWorldAreaDetails", "WorldAreaDetails.strRawName", "WorldAreaDetails.strName",
					"WorldAreaDetails.Act", "WorldAreaDetails.IsTownByte", "WorldAreaDetails.HasWaypointByte", "WorldAreaDetails.ConnectionsCount",
					"WorldAreaDetails.ptrConnections", "WorldAreaDetails.MonsterLevel", "WorldAreaDetails.WorldAreaId" }, c => {
				var world = c.Game.WorldData;
				if ( world == null ) return Outcome.Fail("WorldData did not read");
				var area = GetCurrentArea();
				if ( area == null ) return Outcome.Fail("the current WorldArea did not read (ptrToWorldAreaRef or ptrToWorldAreaDetails)");
				c.Eq("area name", area.Name, "The Twilight Strand");
				c.Eq("WorldData.AreaName agrees", world.AreaName, area.Name);
				c.Eq("AreaLoadingState agrees", PoEMemory.GameRoot.AreaLoadingState.AreaName, area.Name);
				c.Eq("Act", area.Act, 1);
				c.That(!area.IsTown, "IsTown is true on the beach");
				c.That(!area.HasWaypoint, "HasWaypoint is true on the beach");
				c.That(!string.IsNullOrEmpty(area.Id), "strRawName did not read");
				c.Golden("area.id", area.Id);
				c.Golden("area.worldAreaId", world.AreaId);
				c.Golden("area.monsterLevel", world.AreaDetails.Value.MonsterLevel);
				c.Golden("area.connections", string.Join("|", area.Connections.Select(a => a.Name).OrderBy(n => n)));
				c.That(area.Connections.Any(), "ConnectionsCount / ptrConnections gave no neighboring areas");
				return c.Result();
			}, new[] { "A2.InGameStatePointers" });

			yield return Check("A2.AreaHashAndLevel", new[] { "WorldAreaRef.areaHash", "InGameState_Data.CurrentAreaHash", "InGameState_Data.CurrentAreaLevel" }, c => {
				var world = c.Game.WorldData;
				var data = c.Game.Data.Value;
				c.That(world.AreaHash != 0, "areaHash is 0");
				// the same memory since 3.29.3 (WorldAreaRef overlaps InGameState_Data), so this only catches one of them moving
				c.Eq("CurrentAreaHash agrees with areaHash", data.CurrentAreaHash, world.AreaHash);
				c.Eq("CurrentAreaLevel agrees with MonsterLevel", (uint)data.CurrentAreaLevel, world.AreaDetails.Value.MonsterLevel);
				return c.Result();
			}, new[] { "A2.WorldArea" });

			yield return Check("A2.Terrain", new[] {
					"InGameState_Data.Terrain", "TerrainData.vtable", "TerrainData.TilesX", "TerrainData.TilesY", "TerrainData.TileDetails", "TerrainData.WalkableGrid",
					"TerrainData.ProjectileGrid", "TerrainData.BytesPerRow", "ArrayHandle.*", "TileDetails.ptrSubTiles", "TileDetails.ptrTileFile", "TileFile.vtable", "TileFile.Path" }, c => {
				var td = c.Game.Data.Value.Terrain;
				c.That(Offsets.IsValid(td.vtable), "TerrainData vtable is not a pointer (InGameState_Data.Terrain moved)");
				c.InRange("TilesX", td.TilesX, 1, 200);
				c.InRange("TilesY", td.TilesY, 1, 200);
				if ( td.TilesX < 1 || td.TilesX > 200 || td.TilesY < 1 || td.TilesY > 200 ) return c.Result();
				int width = (int)td.TilesX * Offsets.TerrainData.CellsPerTile, height = (int)td.TilesY * Offsets.TerrainData.CellsPerTile;
				c.Eq("BytesPerRow", td.BytesPerRow, (width + 1) / 2);
				long gridBytes = (long)td.BytesPerRow * height;
				c.That(Offsets.IsValid(td.WalkableGrid) && td.WalkableGrid.ItemCount(1) >= gridBytes, $"WalkableGrid is {td.WalkableGrid.ItemCount(1)} bytes, expected at least {gridBytes}");
				c.That(Offsets.IsValid(td.ProjectileGrid) && td.ProjectileGrid.ItemCount(1) >= gridBytes, $"ProjectileGrid is {td.ProjectileGrid.ItemCount(1)} bytes, expected at least {gridBytes}");
				c.Eq("TileDetails count", td.TileDetails.ItemCount(Offsets.TileDetails.Size), (int)(td.TilesX * td.TilesY));
				if ( PoEMemory.TryRead(td.TileDetails.Head, out Offsets.TileDetails tile) ) {
					c.That(Offsets.IsValid(tile.ptrSubTiles), "tile 0 ptrSubTiles is not a pointer");
					if ( PoEMemory.TryRead(tile.ptrTileFile, out Offsets.TileFile file) ) {
						c.That(Offsets.IsValid(file.vtable), "TileFile vtable is not a pointer");
						string path = file.Path.Value;
						c.That(path != null && path.EndsWith(".tdt"), $"TileFile.Path is {path ?? "unreadable"}, expected a .tdt");
					} else c.That(false, "could not read tile 0's TileFile");
				} else c.That(false, "could not read tile 0");
				c.Golden("terrain.tiles", $"{td.TilesX}x{td.TilesY}");

				var terrain = GetTerrain();
				if ( terrain == null || !terrain.IsLoaded ) return Outcome.Fail("the Terrain wrapper did not load from TerrainData: " + string.Join("; ", new[] { c.Result().Detail }));
				c.Eq("Terrain.Width", terrain.Width, width);
				c.Eq("Terrain.Height", terrain.Height, height);
				int walkable = 0, projectileOnly = 0, walkableNotProjectile = 0;
				for ( int y = 0; y < terrain.Height; y++ ) {
					for ( int x = 0; x < terrain.Width; x++ ) {
						bool w = terrain.IsWalkable(x, y), p = terrain.IsProjectilePassable(x, y);
						if ( w ) walkable++;
						if ( w && !p ) walkableNotProjectile++;
						if ( p && !w ) projectileOnly++;
					}
				}
				double fraction = walkable / (double)(width * height);
				c.InRange("walkable fraction", fraction, 0.01, 0.95);
				c.Eq("walkable cells that are not projectile-passable", walkableNotProjectile, 0);
				c.Note($"{projectileOnly} projectile-only cells");
				return c.Result();
			}, new[] { "A2.InGameStatePointers" }, timeoutMs: 6000);

			yield return Check("A2.Clock", new[] {
					"InGameState.GameTimeMs", "InGameState.FrameDeltaSec", "InGameState.GameTimeSec", "InGameState.IsRunning" }, c => {
				var gs = c.Game.Cache;
				var data = c.Game.Data.Value;
				if ( !c.Scratch.ContainsKey("t0") ) {
					c.Scratch["t0"] = (gs.GameTimeMs, c.Elapsed);
					return Outcome.NotYet("sampling the clocks");
				}
				var (ms0, elapsed0) = ((int, long))c.Scratch["t0"];
				long real = c.Elapsed - elapsed0;
				if ( real < 1000 ) return Outcome.NotYet("sampling the clocks");
				c.Near("GameTimeMs advanced per real ms", (gs.GameTimeMs - ms0) / (double)real, 1.0, 0.25);
				c.Near("GameTimeSec", gs.GameTimeSec, gs.GameTimeMs / 1000.0, 0.1);
				c.InRange("FrameDeltaSec", gs.FrameDeltaSec, 0.0005, 0.25);
				c.That(gs.IsRunning, "IsRunning is false");
				return c.Result();
			}, new[] { "A2.InGameStatePointers" }, timeoutMs: 4000);

			yield return Check("A2.FrameCount", new[] { "InGameState_Data.FrameCount" }, c => {
				int now = c.Game.Data.Value.FrameCount;
				if ( !c.Scratch.ContainsKey("t0") ) {
					c.Scratch["t0"] = (now, c.Elapsed);
					return Outcome.NotYet("sampling the frame counter");
				}
				var (count0, elapsed0) = ((int, long))c.Scratch["t0"];
				long real = c.Elapsed - elapsed0;
				if ( real < 1000 ) return Outcome.NotYet("sampling the frame counter");
				c.InRange("FrameCount per second", (now - count0) * 1000.0 / real, 40, 80);
				return c.Result();
			}, new[] { "A2.InGameStatePointers" }, timeoutMs: 4000);
		}

		// ---------------------------------------------------------------- A3: the player entity

		private static readonly string[] playerComponents = { "Render", "Positioned", "Life", "Player", "Actor", "Stats" };

		private static IEnumerable<OffsetCheck> PlayerEntity() {
			yield return Check("A3.PlayerEntity", new[] {
					"InGameState_Data.entPlayer", "Entity.vtable", "Entity.ptrDetails", "Entity.ComponentBasePtrs", "Entity.Id", "EntityDetails.ptrPath", "EntityDetails.ptrComponentLookup",
					"ComponentLookup.*", "Component_Render.entOwner", "Component_Positioned.entOwner", "Component_Life.entOwner", "Component_Player.entOwner", "Component_Actor.entOwner", "Component_Stats.entOwner", "Component_Stats.vtable", "Component_Actor.vtable", "Component_Actor.entOwnerAgain" }, c => {
				var data = c.Game.Data.Value;
				c.That(Offsets.IsValid(data.entPlayer), "entPlayer is not a pointer");
				var player = c.Player;
				if ( !IsValid(player) ) return Outcome.Fail("the player entity is not valid: " + c.Result().Detail);
				c.That(player.Address == data.entPlayer, "Player.Address is not entPlayer");
				c.That(Offsets.IsValid(player.Cache.vtable), "Entity vtable is not a pointer");
				c.That(Offsets.IsValid(player.Cache.ptrDetails), "Entity ptrDetails is not a pointer");
				c.That(Offsets.IsValid<IntPtr>(player.Cache.ComponentBasePtrs, 50) && player.Cache.ComponentBasePtrs.ItemCount(8) >= 5, "ComponentBasePtrs is not a sane array of components");
				c.That(player.Id != 0, "Entity.Id is 0");
				string path = player.Path;
				c.That(path != null && path.StartsWith("Metadata/Characters/"), $"Entity path is {path ?? "unreadable"}, expected Metadata/Characters/...");
				c.Golden("player.path", path);
				var components = player.GetComponents();
				foreach ( var name in playerComponents ) {
					if ( !components.TryGetValue(name, out IntPtr ptr) ) { c.That(false, $"the component lookup has no {name} (ptrComponentLookup / ComponentLookup)"); continue; }
					if ( !PoEMemory.TryRead(ptr + 8, out IntPtr owner) || owner != player.Address ) c.That(false, $"{name}'s entOwner (+0x8) is not the player");
				}
				foreach ( var name in new[] { "Stats", "Actor" } ) {
					if ( components.TryGetValue(name, out IntPtr ptr) ) c.That(PoEMemory.TryRead(ptr, out IntPtr vtable) && Offsets.IsValid(vtable), $"{name}'s vtable (+0x0) is not a pointer");
				}
				if ( components.TryGetValue("Actor", out IntPtr actorPtr) ) c.That(PoEMemory.TryRead(actorPtr + 0x6d8, out IntPtr again) && again == player.Address, "Actor's entOwnerAgain (+0x6d8) is not the player");
				c.Note($"{components.Count} components");
				return c.Result();
			}, new[] { "A2.InGameStatePointers" }, needsPlayer: false, timeoutMs: 5000);

			yield return Check("A3.EntityList", new[] { "InGameState_Data.EntityListHead", "InGameState_Data.EntitiesCount", "EntityListNode.First", "EntityListNode.Second", "EntityListNode.Third", "EntityListNode.Entity" }, c => {
				var data = c.Game.Data.Value;
				c.That(Offsets.IsValid(data.EntityListHead), "EntityListHead is not a pointer");
				c.InRange("EntitiesCount", data.EntitiesCount, 1, 20000);
				var node = c.Game.EntityListHead;
				c.That(Offsets.IsValid(node.First) || Offsets.IsValid(node.Second) || Offsets.IsValid(node.Third), "the EntityListHead node has no links");
				var ents = GetEntities().ToList();
				var player = c.Player;
				c.That(ents.Count >= 1, "no entities were enumerated");
				c.That(ents.Any(e => e.Address == player.Address), "the player is not among the enumerated entities");
				c.That(GetEntityById(player.Id)?.Address == player.Address, "GetEntityById(player.Id) is not the player");
				c.Note($"{ents.Count} enumerated of {data.EntitiesCount}");
				return c.Result();
			}, new[] { "A3.PlayerEntity" });
		}

		// ---------------------------------------------------------------- A4: the player's components

		private static IEnumerable<OffsetCheck> PlayerComponents() {
			yield return Check("A4.Player", new[] { "Component_Player.strName", "Component_Player.XP", "Component_Player.Strength", "Component_Player.Dexterity", "Component_Player.Intelligence", "Component_Player.Level" }, c => {
				var p = c.Player.GetComponent<Player>();
				if ( !IsValid(p) ) return Outcome.Fail("no Player component");
				c.That(!string.IsNullOrEmpty(p.Name), "strName did not read");
				c.Eq("Level", (int)p.Level, 1);
				c.Eq("XP", p.XP, 0u);
				c.Golden("player.str", p.Str);
				c.Golden("player.dex", p.Dex);
				c.Golden("player.int", p.Int);
				c.Note($"name {p.Name}");
				return c.Result();
			}, new[] { "A3.PlayerEntity" });

			yield return Check("A4.Life", new[] {
					"Component_Life.ReservedFlatHP", "Component_Life.ReservedPercentHP", "Component_Life.MaxHP", "Component_Life.CurHP",
					"Component_Life.ReservedFlatMana", "Component_Life.ReservedPercentMana", "Component_Life.MaxMana", "Component_Life.CurMana",
					"Component_Life.MaxES", "Component_Life.CurES" }, c => {
				var life = c.Player.GetComponent<Life>();
				if ( !IsValid(life) ) return Outcome.Fail("no Life component");
				var l = life.Cache;
				c.That(l.MaxHP > 0, $"MaxHP is {l.MaxHP}");
				c.Eq("CurHP is full", l.CurHP, l.MaxHP);
				c.That(l.MaxMana > 0, $"MaxMana is {l.MaxMana}");
				c.Eq("CurMana is full", l.CurMana, l.MaxMana);
				c.Eq("MaxES", l.MaxES, 0);
				c.Eq("CurES", l.CurES, 0);
				c.Eq("ReservedFlatHP", l.ReservedFlatHP, 0);
				c.Eq("ReservedPercentHP", l.ReservedPercentHP, 0);
				c.Eq("ReservedFlatMana", l.ReservedFlatMana, 0);
				c.Eq("ReservedPercentMana", l.ReservedPercentMana, 0);
				c.Golden("life.maxHP", l.MaxHP);
				c.Golden("life.maxMana", l.MaxMana);
				return c.Result();
			}, new[] { "A3.PlayerEntity" });

			yield return Check("A4.Positioned", new[] { "Component_Positioned.Reaction", "Component_Positioned.GridPos", "Component_Positioned.Rotation", "Component_Positioned.Scale", "Component_Positioned.Size", "Component_Positioned.WorldPos", "Vector2i.*" }, c => {
				var pos = c.Player.GetComponent<Positioned>();
				if ( !IsValid(pos) ) return Outcome.Fail("no Positioned component");
				var terrain = GetTerrain();
				c.That(terrain != null && terrain.InBounds(pos.GridPos.X, pos.GridPos.Y), $"GridPos {pos.GridPos} is outside the terrain");
				if ( terrain != null && terrain.InBounds(pos.GridPos.X, pos.GridPos.Y) ) c.That(terrain.IsWalkable(pos.GridPos), $"GridPos {pos.GridPos} is not a walkable cell");
				c.That(!pos.IsHostile, $"the player is hostile (Reaction {pos.Reaction})");
				c.That(float.IsFinite(pos.WorldPos.X) && float.IsFinite(pos.WorldPos.Y), "WorldPos is not finite");
				c.InRange("Rotation", pos.Rotation, -7, 7);
				c.InRange("Scale", pos.Scale, 0.1, 10);
				c.InRange("Size", pos.Size, 0.1, 100);
				var render = c.Player.GetComponent<Render>();
				if ( IsValid(render) ) {
					c.Near("WorldPos.X vs Render.Position.X", pos.WorldPos.X, render.Position.X, 5);
					c.Near("WorldPos.Y vs Render.Position.Y", pos.WorldPos.Y, render.Position.Y, 5);
					var expected = Offsets.GridToWorld(pos.GridPos, render.Position.Z);
					c.Near("GridPos vs Render.Position.X", expected.X, render.Position.X, 15);
					c.Near("GridPos vs Render.Position.Y", expected.Y, render.Position.Y, 15);
				}
				return c.Result();
			}, new[] { "A2.Terrain", "A3.PlayerEntity" });

			yield return Check("A4.Render", new[] { "Component_Render.Pos", "Component_Render.Bounds", "Component_Render.Name", "Component_Render.Rotation", "Component_Render.RotationRadians", "StringHandle.*" }, c => {
				var render = c.Player.GetComponent<Render>();
				if ( !IsValid(render) ) return Outcome.Fail("no Render component");
				c.That(IsFinite(render.Position), $"Position {render.Position} is not finite");
				c.InRange("Position.Z", render.Position.Z, -2000, 2000);
				c.That(IsFinite(render.Bounds) && render.Bounds.X > 0 && render.Bounds.Y > 0 && render.Bounds.Z > 0 && render.Bounds.X < 1000 && render.Bounds.Y < 1000 && render.Bounds.Z < 1000, $"Bounds {render.Bounds} is not a sane size");
				c.That(!string.IsNullOrEmpty(render.Name), "Name did not read");
				c.That(IsFinite(render.Rotation), $"Rotation {render.Rotation} is not finite");
				c.InRange("RotationRadians", render.Cache.RotationRadians, -1000, 1000); // not normalized: it accumulates as she turns
				c.Golden("render.name", render.Name);
				c.Golden("render.bounds", render.Bounds);
				return c.Result();
			}, new[] { "A3.PlayerEntity" });

			yield return Check("A4.Actor", new[] { "Component_Actor.ActorSkillsHandle", "ActorSkillArrayEntry.ActorSkillPtr", "ActorSkill.Id", "ActorSkill.ptrGemEffects", "SkillGem.NamePtr", "Component_Actor.ActionFlags" }, c => {
				var actor = c.Player.GetComponent<Actor>();
				if ( !IsValid(actor) ) return Outcome.Fail("no Actor component");
				var skills = actor.Skills.ToList();
				c.That(skills.Count > 0, "no skills (ActorSkillsHandle)");
				var attack = skills.FirstOrDefault(s => s.Id == 0x4000);
				c.That(attack != null, $"no skill with Id 0x4000 (Default Attack) among {string.Join(", ", skills.Select(s => $"{s.Id:X}"))}");
				if ( attack != null ) c.Eq("skill 0x4000 display name", attack.DisplayName, "Default Attack");
				c.Note($"{skills.Count} skills: {string.Join(", ", skills.Select(s => s.DisplayName))}");
				c.Golden("actor.skills", string.Join("|", skills.Select(s => $"{s.Id:X}:{s.InternalName}:{s.DisplayName}").OrderBy(s => s)));
				c.Note($"ActionFlags={actor.ActionFlags}"); // WashedUp while lying on the beach, so not a golden
				return c.Result();
			}, new[] { "A3.PlayerEntity" });

			yield return Check("A4.Passives", new[] { "InGameState_Data.ServerData", "ServerData.ptrPassives", "ServerData_Passives.AllocatedPassives" }, c => {
				var data = c.Game.Data.Value;
				c.That(Offsets.IsValid(data.ServerData), "ServerData is not a pointer");
				if ( PoEMemory.TryRead(data.ServerData, out Offsets.ServerData server) ) {
					c.That(Offsets.IsValid(server.ptrPassives), "ptrPassives is not a pointer");
					if ( PoEMemory.TryRead(server.ptrPassives, out Offsets.ServerData_Passives passives ) ) c.That(passives.AllocatedPassives.Head == passives.AllocatedPassives.Tail || Offsets.IsValid(passives.AllocatedPassives), "AllocatedPassives is not a valid array (neither empty nor two pointers)");
					else c.That(false, "could not read the ServerData_Passives");
				} else c.That(false, "could not read the ServerData");
				c.Eq("allocated passives of a new character", c.Game.AllocatedPassives.Length, 0);
				return c.Result();
			}, new[] { "A3.PlayerEntity" });

			yield return Check("A4.Stats", new[] { "Component_Stats.GameStats", "GameStatArray.*", "GameStatArrayEntry.*" }, c => {
				var stats = c.Player.GetComponent<Stats>()?.GetStats();
				if ( stats == null ) return Outcome.Fail("no Stats component");
				c.That(stats.Count >= 5, $"only {stats.Count} stats read");
				var life = c.Player.GetComponent<Life>();
				if ( stats.TryGetValue(Offsets.GameStat.MaximumLife, out int maxLife) && IsValid(life) ) c.Eq("stat MaximumLife vs Life.MaxHP", maxLife, life.MaxHP);
				else c.Note("no MaximumLife stat to compare with Life.MaxHP");
				c.Note($"{stats.Count} stats");
				return c.Result();
			}, new[] { "A4.Life" });
		}

		// ---------------------------------------------------------------- A5: walking a few steps

		private class WalkLog {
			public Offsets.Vector2i Start, Target;
			public bool HasTarget;
			public long ClickedAt, LastClick;
			public int Clicks;
			public Vector2 Screen;
			public readonly List<(long t, Offsets.Vector2i grid, Offsets.Component_ActionFlags flags)> Samples = new List<(long, Offsets.Vector2i, Offsets.Component_ActionFlags)>();
		}

		private static IEnumerable<OffsetCheck> Movement() {
			var walk = new WalkLog();

			// click on the ground a few steps away (left click moves, with no monster there)
			var step = Check("A5.Walk", new[] { "Component_Actor.ActionFlags", "Component_Positioned.GridPos", "Component_Render.Pos", "InGameState_Data.IsPaused" }, c => {
				if ( !walk.HasTarget ) return Outcome.Fail("found no walkable cell in the clear, on the screen, to click");
				var player = c.Player;
				var pos = player.GetComponent<Positioned>();
				var actor = player.GetComponent<Actor>();
				walk.Samples.Add((Time.ElapsedMilliseconds, pos.GridPos, actor?.ActionFlags ?? 0));
				int dx = pos.GridPos.X - walk.Start.X, dy = pos.GridPos.Y - walk.Start.Y;
				double moved = Math.Sqrt((dx * dx) + (dy * dy));
				// the game takes a click only after it has seen the mouse arrive; and a lying (washed up) character may need more than one
				long now = Time.ElapsedMilliseconds;
				if ( moved < 4 && walk.Clicks < 40 && now - walk.LastClick >= (walk.Clicks == 0 ? 250 : 1500) ) {
					SkillBar.ClickAt(new RectangleF(walk.Screen.X - 1, walk.Screen.Y - 1, 2, 2));
					walk.Clicks++;
					walk.LastClick = now;
				}
				if ( moved < 4 ) return Outcome.NotYet($"waiting for the player to walk from {walk.Start} toward {walk.Target} ({moved:0.#} cells so far)");
				// keep sampling a little after the first step, so the ticks have time to show
				if ( !c.Scratch.ContainsKey("moved") ) c.Scratch["moved"] = c.Elapsed;
				if ( c.Elapsed - (long)c.Scratch["moved"] < 1500 ) return Outcome.NotYet("walking");
				c.That(!c.Game.IsPaused, "IsPaused is true while the player walks");
				c.That(walk.Samples.Any(s => s.flags.HasFlag(Offsets.Component_ActionFlags.Moving)), "ActionFlags never showed Moving while the player walked");
				c.That(!walk.Samples.Any(s => s.flags.HasFlag(Offsets.Component_ActionFlags.WashedUp)) || walk.Samples.Last().flags == Offsets.Component_ActionFlags.None || walk.Samples.Last().flags.HasFlag(Offsets.Component_ActionFlags.Moving), "the player is still washed up");
				var render = player.GetComponent<Render>();
				c.Near("Render.Position.X vs GridPos", render.Position.X, Offsets.GridToWorld(pos.GridPos, render.Position.Z).X, 15);
				c.Near("Render.Position.Y vs GridPos", render.Position.Y, Offsets.GridToWorld(pos.GridPos, render.Position.Z).Y, 15);
				c.Note($"walked {moved:0.#} cells from {walk.Start} to {pos.GridPos}");
				return c.Result();
			}, new[] { "A4.Positioned", "A4.Actor" }, timeoutMs: 60000);
			step.Setup = () => {
				walk.HasTarget = false;
				walk.Samples.Clear();
				var player = GetPlayer();
				var pos = player?.GetComponent<Positioned>();
				var terrain = GetTerrain();
				var size = WindowSize.Value;
				if ( !IsValid(pos) || terrain == null || size.X <= 0 ) return;
				float z = Position(player).Z;
				walk.Start = pos.GridPos;
				foreach ( int dist in new[] { 12, 9, 15, 6 } ) {
					for ( int k = 0; k < 16 && !walk.HasTarget; k++ ) {
						double angle = k * Math.PI / 8;
						var cell = new Offsets.Vector2i(pos.GridPos.X + (int)Math.Round(Math.Cos(angle) * dist), pos.GridPos.Y + (int)Math.Round(Math.Sin(angle) * dist));
						if ( !terrain.InBounds(cell.X, cell.Y) || !terrain.IsWalkable(cell) || terrain.Clearance(cell) < 2 || !terrain.IsLineClear(pos.GridPos, cell) ) continue;
						// well inside the window and clear of the panels, the skill bar and the orbs at the bottom
						if ( !TryGridToScreen(cell, z, out Vector2 screen) || screen.X < size.X * 0.2f || screen.X > size.X * 0.8f || screen.Y < size.Y * 0.15f || screen.Y > size.Y * 0.65f ) continue;
						walk.Target = cell;
						walk.HasTarget = true;
						walk.ClickedAt = walk.LastClick = Time.ElapsedMilliseconds;
						walk.Clicks = 0;
						walk.Screen = screen;
						SkillBar.MouseTo(screen);
					}
				}
			};
			yield return step;
		}

		// ---------------------------------------------------------------- the camera, once the player's position is known

		private static IEnumerable<OffsetCheck> Projection() {
			yield return Check("A2.Camera", new[] { "WorldData.Camera", "Camera.Matrix", "Camera.Size", "Camera.Width", "Camera.Height", "Camera.Position" }, c => {
				var camera = GetCamera();
				var size = WindowSize.Value;
				c.That(size.X > 0, "the window size is unknown");
				c.Near("Camera.Width vs the window", camera.Width, size.X, size.X * 0.05);
				c.Near("Camera.Height vs the window", camera.Height, size.Y, size.Y * 0.05);
				var render = c.Player.GetComponent<Render>();
				c.That(IsFinite(camera.Position), $"Camera.Position {camera.Position} is not finite");
				c.InRange("camera distance to the player", Vector3.Distance(camera.Position, render.Position), 50, 20000);
				var screen = WorldToScreen(render.Position);
				// the camera follows the player: they are drawn near the middle of the window
				c.Near("player screen X", screen.X, size.X / 2, size.X * 0.25);
				c.Near("player screen Y", screen.Y, size.Y / 2, size.Y * 0.25);
				return c.Result();
			}, new[] { "A4.Render" });
		}
	}
}
