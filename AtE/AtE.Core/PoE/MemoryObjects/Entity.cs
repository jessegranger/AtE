using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using static AtE.Globals;

namespace AtE {
	public static partial class Globals {

		public static bool IsValid(Entity ent) {
			if( ent == null || !IsValid(ent.Address) ) {
				return false;
			}
			// Optimization: use the vtable ptr to quickly classify an Entity as valid
			// this avoids reading the whole Path as a unicode string instead
			long vtablePtr = ent.vtablePtr.ToInt64();
			
			// If we've already memo-ized the current pointers, check them:
			for(int i = 0; i < Entity.cachedVtablePtr.Length; i++ ) {
				long p = Entity.cachedVtablePtr[i];
				if( (p != 0) && (vtablePtr == p) ) {
					return true; // Entities are valid when they have a recognized vtablePtr
				}
			}
			// otherwise, recognize vtablePtrs that we haven't seen before, by reading their Path value
			if ( (ent.Path?.StartsWith("Meta", StringComparison.Ordinal) ?? false) && ent.HasComponent() ) {
				// Memo-ize the valid Entity pointers the first time we see them
				bool full = true;
				for(int i = 0; i < Entity.cachedVtablePtr.Length; i++ ) {
					if( Entity.cachedVtablePtr[i] == 0 ) {
						Entity.cachedVtablePtr[i] = vtablePtr;
						Debugger.RegisterVtable($"Entity{i}", ent.vtablePtr);
						full = false;
						break;
					}
				}
				if( full ) {
					Log($"Entity: Valid Entity recognized at {Describe(ent.vtablePtr)}, but vtable ptr cache is full, increase it's size.");
				}
				return true;
			}
			return false;
		}
			// && ent.vtablePtr.ToInt64() == 0x7FF6EE0717F0; // only valid in 3.23.2c
		  // to find the new value, use the (more expensive) StartsWith code below to find valid entities

		public static Entity GetEntityById(uint id) => EntityCache.TryGetEntity(id, out Entity ret) ? ret : null;

		/// <summary>
		/// Helper to get the Player from the GameRoot.
		/// Call GetPlayer() every frame, as the underlying object can be moved by PoE at any time.
		/// </summary>
		/// <returns>The current Player Entity.</returns>
		public static PlayerEntity GetPlayer() => PoEMemory.GameRoot?.InGameState?.Player;

		/// <summary>
		/// Helper to get the full Entity list from the InGameState (safely cached) from anywhere.
		/// The resulting iterator is only considered valid for this frame.
		/// </summary>
		/// <returns>An enumerable over the current entity list.</returns>
		public static IEnumerable<Entity> GetEntities() => EntityCache.GetEntities();

		public static IEnumerable<Entity> GetEnemies() => GetEntities()?
			.Take(1000)
			.Where(e => 
				e != null
				&& e.Id > 0 && e.Id < int.MaxValue
				&& (e.Path?.StartsWith("Metadata/Monster", StringComparison.Ordinal) ?? false)
				&& (e.GetComponent<Positioned>()?.IsHostile ?? false)) ?? Empty<Entity>();

		public static IEnumerable<Entity> NearbyEnemies(float radius) {
			Vector2 playerPos = GridPosition(GetPlayer());
			return playerPos == Vector2.Zero ? Empty<Entity>() : GetEnemies().Where(e => Distance(playerPos, GridPosition(e)) <= radius);
		}

		public static IEnumerable<Entity> NearbyEnemies(float radius, Offsets.MonsterRarity rarity) =>
			NearbyEnemies(radius).Where(e => (e.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White) >= rarity );


		public static void DrawTextAt(Entity ent, string text, Color color) {
			if ( !IsValid(ent) ) return;
			var pos = ent.GetComponent<Render>()?.Position ?? Vector3.Zero; 
			DrawTextAt(ent.Id, WorldToScreen(pos), text, color);
		}

	}

	public class Entity : MemoryObject<Offsets.Entity> {
		public Cached<Offsets.EntityDetails> Details;
		// maps the Type (of a Component) to the Address of that Component instance for this Entity
		private Dictionary<string, IntPtr> ComponentPtrs; // we have to parse this all at once
		private Dictionary<string, MemoryObject> ComponentCache; // these are filled in as requested, then re-used if requested a second time
		private long LastParseTime;

		internal static long[] cachedVtablePtr = new long[12];
		static Entity() {
			OnAreaChange += (obj, areaName) => {
				// clear the Entity vtable pointer cache entries on zone change
				Array.Clear(cachedVtablePtr, 0, cachedVtablePtr.Length);
			};
		}

		public Entity() : base() {
			Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);
		}

		public override IntPtr Address {
			get => base.Address;
			set {
				if ( value == base.Address ) {
					return;
				}
				if ( !IsValid(value) ) {
					base.Address = IntPtr.Zero;
					return;
				}
				// when this gets assigned, `Cache` gets a new value as well
				base.Address = value;
				Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);

				// invalidate the private caches whenever Address changes
				path = null;
				auditPath = null;
				identityDetails = IntPtr.Zero;
				identityPath = IntPtr.Zero;
				ComponentPtrs = null;
				ComponentCache = null;
				LastParseTime = 0;

			}
		}

		/// <summary>
		///  The remote id used by the PoE server to sync the ent.
		///  Client-only effects do have Entity structure, but dont have Id.
		/// </summary>
		public uint Id => IsValid(Address) ? Cache.Id : 0;

		public IntPtr vtablePtr => IsValid(Address) ? Cache.vtable : IntPtr.Zero;

		// The identity of the object this Entity was made for: its details and path pointers when first checked. The game
		// reuses the memory of animated objects and items for new ones, and EntityCache.TryGetEntity(IntPtr) caches by
		// address alone, so it checks this to hand out a fresh Entity (fresh caches) when the object at Address changed.
		// (seen live 3.30.0d with the Path audit: hundreds of animated objects, eg chilled_grd.ao -> necrobolt.ao, and an
		// item, GoldCoin -> FlaskLife11, at an unchanged address; every time ptrPath changed.)
		private IntPtr identityDetails, identityPath;
		/// <summary>When EntityCache last handed this Entity out by address (Time.ElapsedMilliseconds); for eviction.</summary>
		internal long LastLookup;
		/// <summary>Made by EntityCache's walk of the entity list (the walk unloads it), not by a lookup by address.</summary>
		internal bool FromWalk;
		/// <summary>True if the object at Address is no longer the one this Entity was made for (its details or path
		/// pointer changed since first checked). The first call records the identity and returns false.</summary>
		internal bool IsRecycled() {
			IntPtr d = Cache.ptrDetails;
			if ( !IsValid(d) ) return false;
			IntPtr p = Details.Value.ptrPath;
			if ( identityDetails == IntPtr.Zero ) {
				identityDetails = d;
				identityPath = p;
				return false;
			}
			return d != identityDetails || p != identityPath;
		}

		private string path;
		private IntPtr pathOrigin = IntPtr.Zero;
		public string Path {
			get {
				if ( !IsValid(Address) ) return null;
				if ( IsValid(Details.Value.ptrPath) ) {
					// if we have a cached path, read from the same pointer
					IntPtr curPath = Details.Value.ptrPath;
					if ( path != null && pathOrigin == curPath ) {
						// audit: now and then, re-read the string in place, to see if it changes behind the same pointer
						long now = Time.ElapsedMilliseconds;
						if ( now >= nextPathAudit ) {
							nextPathAudit = now + PATH_AUDIT_MS;
							if ( PoEMemory.TryReadString(curPath, Encoding.Unicode, out string reread) ) {
								AuditPath(reread, curPath, "the string changed behind the same ptrPath");
							}
						}
						return path;
					} else {
						// otherwise, read the path and cache it
						if( PoEMemory.TryReadString(curPath, Encoding.Unicode, out path) ) {
							AuditPath(path, curPath, pathOrigin == IntPtr.Zero ? null : "ptrPath changed");
							pathOrigin = curPath;
							return path;
							// TODO: if could be that this is a good place to invalidate ComponentPtr as well
						}
					}
				}
				return null;
			}
		}

		// Path cache audit (temporary diagnostics). The proposed speed-up is to cache Path for as long as Address is the
		// same, without reading EntityDetails.ptrPath every frame. That is only safe if an Entity at one address never
		// changes its path. auditPath is the first path read since Address was set (what that cache would hold); every
		// later read that differs from it is logged as "PathCache violation", and counted in PathCacheViolations
		// (MemoryServer /status). If this never logs over a long run, caching Path per Address is safe.
		private string auditPath;
		private uint auditId;
		private long nextPathAudit;
		private const long PATH_AUDIT_MS = 2000;
		private static int pathCacheViolations;
		public static int PathCacheViolations => pathCacheViolations;
		private void AuditPath(string now, IntPtr ptrPath, string how) {
			if ( string.IsNullOrEmpty(now) ) return;
			uint id = Id;
			if ( auditPath == null ) {
				auditPath = now;
				auditId = id;
				return;
			}
			if ( now != auditPath ) {
				int n = Interlocked.Increment(ref pathCacheViolations);
				Log($"PathCache violation #{n} ({how ?? "re-read"}): entity at {Describe(Address)} id {auditId} -> {id}, ptrPath {Describe(ptrPath)}: \"{auditPath}\" -> \"{now}\"");
				auditPath = now; // log each change once
				auditId = id;
			}
		}

		public bool HasComponent<T>() where T : MemoryObject, new() => IsValid(Address) && GetComponent<T>() != null;
		public bool HasComponent(string name) {
			if ( ComponentPtrs == null || (Time.ElapsedMilliseconds - LastParseTime) > 1337 ) {
				ParseComponents();
			}
			return ComponentPtrs?.ContainsKey(name) ?? false;
		}
		public bool HasComponent() {
			if ( ComponentPtrs == null || (Time.ElapsedMilliseconds - LastParseTime) > 1337 ) {
				ParseComponents();
			}
			return (ComponentPtrs?.Count ?? 0) > 0;
		}

		public T GetComponent<T>() where T : MemoryObject, new() {
			if ( !IsValid(Address) ) {
				return null;
			}
			if ( Environment.CurrentManagedThreadId != 2 ) {
				Log($"Warning: GetComponent<{typeof(T).Name}> called from background thread!");
			}
			if ( ComponentPtrs == null || (Time.ElapsedMilliseconds - LastParseTime) > 1337 ) {
				ParseComponents();
			}
			if ( ComponentPtrs == null ) {
				// all the above failed to parse any ptrs, so there are no components
				ClearComponents();
				return null;
			}

			string key = ComponentName<T>.Name;
			if ( ComponentCache != null && ComponentCache.TryGetValue(key, out MemoryObject cachedResult) ) {
				return (T)cachedResult;
			}

			IntPtr ptr = default;
			if ( ComponentPtrs?.TryGetValue(key, out ptr) ?? false ) {
				var ret = new T() { Address = ptr };
				if ( ComponentCache == null ) {
					ComponentCache = new Dictionary<string, MemoryObject>();
				}
				ComponentCache[key] = ret;
				return ret;
			}
			return null;
		}

		public Dictionary<Offsets.GameStat, int> GetStats() => GetComponent<Stats>()?.GetStats();

		public IEnumerable<ActorSkill> GetSkills() => GetComponent<Actor>()?.Skills ?? Empty<ActorSkill>();

		public Mods GetMods() => GetComponent<Mods>();

		public RectangleF GetClientRect() {
			var render = GetComponent<Render>();
			if( !IsValid(render) ) {
				return RectangleF.Empty;
			}
			var pos = WorldToScreen(render.Position);
			var bounds = render.Bounds;
			return new RectangleF(pos.X - bounds.X, pos.Y - bounds.Y, bounds.X, bounds.Z);
		}

		public Dictionary<string,IntPtr> GetComponents() {
			if( ComponentPtrs == null ) {
				ParseComponents();
			}
			return ComponentPtrs;
		}

		public IEnumerable<Buff> GetBuffs() => GetComponent<Buffs>()?.GetBuffs();

		public Animated Animated => GetComponent<Animated>();

		/// <summary>
		/// Creation time (seconds, animation clock) of this entity's animated object, read from its AnimatedRender.
		/// NaN if this entity has no animated object. Same information as ClientAnimationController.TimeSpentAnimating.
		/// </summary>
		public float CreationTime => Animated?.AnimatedObject?.GetComponent<AnimatedRender>()?.CreationTime ?? float.NaN;

		public void ClearComponents() {
			ComponentPtrs = null;
			ComponentCache?.Clear();
			ComponentCache = null;
		}
		public void DebugComponents() {
			var componentPtrs = new ArrayHandle<IntPtr>(Cache.ComponentBasePtrs);
			ImGui.Text($"ComponentsArray claims to have {componentPtrs.Length} items.");
			ImGui_Address(Details.Value.ptrComponentLookup, "ptrComponentLookup", "ComponentLookup");
			if ( !PoEMemory.TryRead(Details.Value.ptrComponentLookup, out Offsets.ComponentLookup lookup) ) {
				ImGui.Text("Failed to read ptrComponentLookup");
			} else {
				ImGui.Text($"ComponentLookup Capacity: {lookup.Counter} of {lookup.Capacity}");
			}
			var namesArray = new Offsets.NameAndIndexStruct[lookup.Counter];
			if ( 0 == PoEMemory.TryRead(lookup.ComponentMap, namesArray) ) {
				ImGui.Text($"ComponentMap: Read 0 bytes, aborting.");
				return;
			}

			foreach(var entry in namesArray ) {
				ImGui.Text($" - Index: {entry.Index} '{(PoEMemory.TryReadString(entry.ptrName, Encoding.ASCII, out string name0) ? name0 : "")}'");
			}

		}

		// the component name for a type, computed once per type instead of on every GetComponent<T>
		private static class ComponentName<T> { public static readonly string Name = typeof(T).Name; }

		private const int COMPONENT_MAX = 64; // max number of components on one entity

		private const int COMPONENT_MAX_NAME = 128; // max length of a component name

		private void ParseComponents() {

			// Build the map in a local and publish it when done: the EntityCache thread can parse (IsValid, through
			// HasComponent) while the main thread reads ComponentPtrs, and a Dictionary being filled is not safe to read.
			var ptrs = new Dictionary<string, IntPtr>();
			bool publish = true;
			try {
				ParseComponents(ptrs, ref publish);
			} finally {
				if ( publish ) ComponentPtrs = ptrs;
			}
		}
		private void ParseComponents(Dictionary<string, IntPtr> ptrs, ref bool publish) {
			using ( Perf.Section("ParseComponents") ) {
				LastParseTime = Time.ElapsedMilliseconds;
				// the Entity has a list of ptr to Component, managed by an ArrayHandle
				// (these are the .Values of the underlying map)
				var basePtrsHandle = new ArrayHandle<IntPtr>(Cache.ComponentBasePtrs);
				if ( basePtrsHandle.Length == 0 || basePtrsHandle.Length > COMPONENT_MAX ) {
					Log($"ParseComponents[{Id}]: invalid length of basePtrs array: {Describe(basePtrsHandle)}");
					publish = false;
					ClearComponents();
					return;
				}

				// read out all the pointers at once, so that later accesses dont read each ptr individually
				var basePtrs = basePtrsHandle.ToArray(limit: COMPONENT_MAX);
				
				// the old map structure (a boost map) is replaced with an ArrayHandle (an stl vector)
				IntPtr ComponentMap_Offset = Details.Value.ptrComponentLookup + GetOffset<Offsets.ComponentLookup>("ComponentMap");
				if ( !PoEMemory.TryRead(ComponentMap_Offset, out Offsets.ArrayHandle handle) ) {
					Log($"ParseComponents[{Id}]: failed to read ComponentMap handle from {Describe(ComponentMap_Offset)}");
					return;
				}
				var namesArray = new ArrayHandle<Offsets.NameAndIndexStruct>(handle);
				foreach ( var entry in namesArray ) {
					if ( entry.Index < 0 || entry.Index >= basePtrs.Length ) {
						Log($"ParseComponents[{Id}]: invalid entry index: {entry.Index} should be in [0..{basePtrs.Length}]");
						return;
					}
					if ( !IsValid(entry.ptrName) ) {
						Log($"ParseComponents[{Id}]: invalid entry name ptr {Describe(entry.ptrName)}");
						return;
					}
					if ( !IsValid(basePtrs[entry.Index]) ) {
						Log($"ParseComponents[{Id}]: invalid basePtr {Describe(basePtrs[entry.Index])} at index {entry.Index}");
						return;
					}
					if ( !PoEMemory.TryReadString(entry.ptrName, Encoding.ASCII, out string name) ) {
						Log($"ParseComponents[{Id}]: failed to read component name from ptr {Describe(entry.ptrName)}");
						return;
					}
					if ( name.Length < 3 || name.Length > COMPONENT_MAX_NAME ) {
						Log($"ParseComponents[{Id}]: invalid component name \"{name}\"");
						return;
					}
					// if ( Id == 400 ) { Log($"ParseComponents[{Id}]: valid component \"{name}\" => index {entry.Index}"); }
					ptrs[name] = basePtrs[entry.Index];
				}

				// Log($"Entity[{Id}] Parsed {ComponentPtrs.Count} components");
				return;
			}
		}

		public struct Icon {
			// Size == 1f with Sprite == None means the ent has been determined to have no icon
			// Size == 0f with Sprite == None means the ent has not checked yet to see what icon it should use
			public SpriteIcon Sprite;
			public float Size; // later, Size == 0f will mean a sprite has not yet been determined for the ent
			public long Expires;
			public Icon(SpriteIcon icon, float iconSize, long expires = long.MaxValue) {
				Sprite = icon;
				Size = iconSize;
				Expires = expires;
			}
		}
		public Icon MinimapIcon = default;

	}

	/// <summary>
	/// This is a helper class that adds a few features to an Entity that is also the LocalPlayer.
	/// Players are just normal Entities (with a Player component).
	/// </summary>
	public class PlayerEntity : Entity {
		/// <summary>
		/// Where to find data about HP, Mana, ES, etc.
		/// </summary>
		public Life Life => GetComponent<Life>();

		public Stats Stats => GetComponent<Stats>();
		public new Animated Animated => GetComponent<Animated>();

		/// <summary>
		/// Where to find data about your Skills, and the DeployedObjects they create.
		/// </summary>
		public Actor Actor => GetComponent<Actor>();

		public Pathfinding Pathfinding => GetComponent<Pathfinding>();
		public Positioned Positioned => GetComponent<Positioned>();
		public Player Player => GetComponent<Player>();

		public Buffs Buffs => GetComponent<Buffs>();

		private Render render;
		public Render Render => IsValid(render) ? render : render = GetComponent<Render>();
		public Vector3 Position => Render?.Position ?? Vector3.Zero;
		public Vector3 Bounds => Render?.Bounds ?? Vector3.Zero;

	}


}
