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
		// What the last parse of the components read (see ParseComponents), published whole as one object: the EntityCache
		// thread can parse while the main thread reads, and the map, the base pointers it was made from and the lookup must
		// agree with each other.
		private sealed class Parsed {
			public Dictionary<string, IntPtr> Map; // the name of each Component to the Address of its instance for this Entity
			public IntPtr[] BasePtrs; // this entity's component base pointers the map was made from
			public IntPtr Lookup; // and its type's ptrComponentLookup
		}
		private Parsed parsed;
		// maps the Type (of a Component) to the Address of that Component instance for this Entity
		private Dictionary<string, IntPtr> ComponentPtrs => parsed?.Map;
		private Dictionary<string, MemoryObject> ComponentCache; // these are filled in as requested, then re-used if requested a second time
		private long LastParseTime;
		private bool lastParseFailed;

		internal static long[] cachedVtablePtr = new long[12];
		static Entity() {
			OnAreaChange += (obj, areaName) => ClearAreaCaches();
			// (Detach drops every OnAreaChange handler, so hook it again on each attach; a new process also means new
			// addresses for the component names, see componentNames)
			PoEMemory.OnAttach += (_, __) => {
				componentNames.Clear();
				ClearAreaCaches();
				OnAreaChange += (obj, areaName) => ClearAreaCaches();
			};
		}
		private static void ClearAreaCaches() {
			// the Entity vtable pointer cache entries
			Array.Clear(cachedVtablePtr, 0, cachedVtablePtr.Length);
			// and the component lookups: their memory may be freed and reused for other types in the next area
			componentLookups.Clear();
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
				identityDetails = IntPtr.Zero;
				identityPath = IntPtr.Zero;
				parsed = null;
				ComponentCache = null;
				LastParseTime = 0;
				lastParseFailed = false;

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

		// The path is read once and kept for as long as Address is the same: an entity at one address never changes its
		// path. (Checked live, 3.30.0d, by an audit that re-read it: the only changes were objects the game put at a reused
		// address, which EntityCache now gives a fresh Entity, see IsRecycled; the walk makes a fresh one for a new id.)
		private string path;
		public string Path {
			get {
				if ( !IsValid(Address) ) return null;
				if ( path != null ) return path;
				IntPtr ptrPath = Details.Value.ptrPath;
				if ( IsValid(ptrPath) && PoEMemory.TryReadString(ptrPath, Encoding.Unicode, out string read) && !string.IsNullOrEmpty(read) ) {
					path = read;
				}
				return path;
			}
		}

		public bool HasComponent<T>() where T : MemoryObject, new() => IsValid(Address) && GetComponent<T>() != null;
		public bool HasComponent(string name) {
			EnsureParsed();
			return ComponentPtrs?.ContainsKey(name) ?? false;
		}
		public bool HasComponent() {
			EnsureParsed();
			return (ComponentPtrs?.Count ?? 0) > 0;
		}

		public T GetComponent<T>() where T : MemoryObject, new() {
			if ( !IsValid(Address) ) {
				return null;
			}
			if ( Environment.CurrentManagedThreadId != 2 ) {
				Log($"Warning: GetComponent<{typeof(T).Name}> called from background thread!");
			}
			EnsureParsed();
			var ptrs = ComponentPtrs;
			if ( ptrs == null ) {
				// the parse failed, so there are no components
				ComponentCache = null;
				return null;
			}

			string key = ComponentName<T>.Name;
			// (locals: the EntityCache thread can replace these meanwhile, see DropStaleComponents)
			var cache = ComponentCache;
			if ( cache != null && cache.TryGetValue(key, out MemoryObject cachedResult) ) {
				return (T)cachedResult;
			}

			if ( ptrs.TryGetValue(key, out IntPtr ptr) ) {
				var ret = new T() { Address = ptr };
				if ( cache == null ) {
					ComponentCache = cache = new Dictionary<string, MemoryObject>();
				}
				cache[key] = ret;
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
			EnsureParsed();
			return ComponentPtrs;
		}

		public IEnumerable<Buff> GetBuffs() => GetComponent<Buffs>()?.GetBuffs();

		public Animated Animated => GetComponent<Animated>();

		/// <summary>
		/// Creation time (seconds, animation clock) of this entity's animated object, read from its AnimatedRender.
		/// NaN if this entity has no animated object. Same information as ClientAnimationController.TimeSpentAnimating.
		/// </summary>
		public float CreationTime => Animated?.AnimatedObject?.GetComponent<AnimatedRender>()?.CreationTime ?? float.NaN;

		/// <summary>Forget the components, so the next use parses them again from memory.</summary>
		public void ClearComponents() {
			// (assigned, not cleared: the other thread may be reading the old ones)
			parsed = null;
			ComponentCache = null;
			lastParseFailed = false;
		}
		public void DebugComponents() {
			var componentPtrs = new ArrayHandle<IntPtr>(Cache.ComponentBasePtrs);
			ImGui.Text($"ComponentsArray claims to have {componentPtrs.Length} items.");
			ImGui_Address(Details.Value.ptrComponentLookup, "ptrComponentLookup", "ComponentLookup");
			IntPtr mapAt = Details.Value.ptrComponentLookup + GetOffset<Offsets.ComponentLookup>("ComponentMap");
			if ( !PoEMemory.TryRead(mapAt, out Offsets.ArrayHandle handle) ) {
				ImGui.Text("Failed to read the ComponentMap handle");
				return;
			}
			var namesArray = new ArrayHandle<Offsets.NameAndIndexStruct>(handle).ToArray(limit: COMPONENT_MAX);
			ImGui.Text($"ComponentMap has {namesArray.Length} entries.");

			foreach(var entry in namesArray ) {
				ImGui.Text($" - Index: {entry.Index} '{(PoEMemory.TryReadString(entry.ptrName, Encoding.ASCII, out string name0) ? name0 : "")}'");
			}

		}

		// the component name for a type, computed once per type instead of on every GetComponent<T>
		private static class ComponentName<T> { public static readonly string Name = typeof(T).Name; }

		private const int COMPONENT_MAX = 64; // max number of components on one entity

		private const int COMPONENT_MAX_NAME = 128; // max length of a component name

		/// <summary>How long a parsed component map is used before it is checked against memory again.</summary>
		private const long REPARSE_MS = 1337;
		/// <summary>After a failed parse, how long before trying again (an entity being freed would otherwise parse, and
		/// log, on every GetComponent).</summary>
		private const long RETRY_FAILED_MS = 250;

		// parse the components if they are not parsed yet, or not lately
		private void EnsureParsed() {
			long since = Time.ElapsedMilliseconds - LastParseTime;
			if ( parsed != null ? since <= REPARSE_MS : (lastParseFailed && since < RETRY_FAILED_MS) ) return;
			ParseComponents();
		}

		private void ParseComponents() {
			using ( Perf.Section("ParseComponents") ) {
				LastParseTime = Time.ElapsedMilliseconds;
				var was = parsed;
				var now = ReadComponents(was, out string error);
				lastParseFailed = now == null;
				if ( now == null ) {
					Log($"ParseComponents[{Id}]: {error}");
					// (a read that failed is not trusted at all, not even in part: the entity is most likely being freed;
					// assigned, not cleared, as the main thread may be reading the old ones)
					parsed = null;
					ComponentCache = null;
					return;
				}
				if ( now == was ) return; // unchanged
				// Built in a local and published whole: the EntityCache thread can parse (IsValid, through HasComponent)
				// while the main thread reads ComponentPtrs, and a Dictionary being filled is not safe to read.
				parsed = now;
				// (the main thread may be adding to the cache meanwhile: if so, the next parse drops what is stale)
				try { DropStaleComponents(now.Map); } catch ( InvalidOperationException ) { }
			}
		}

		// A cached component (ComponentCache) whose pointer is not the one just parsed (changed, or gone) is dropped, so
		// GetComponent makes it again. Without this the cache outlived every re-parse: an Entity is cached by its address
		// (EntityCache), and when the game reused an item entity's memory for another item, its old components stayed
		// (seen live, 3.30.0d: a stash's Orb of Scouring stack read its Base from an old Karui Chopper, so its name was
		// "Karui Chopper", its size 2x4, and the routine found no Orb of Scouring with 24 of them in the tab).
		private void DropStaleComponents(Dictionary<string, IntPtr> ptrs) {
			var cache = ComponentCache;
			if ( cache == null || cache.Count == 0 ) return;
			bool anyStale = false;
			foreach ( var kv in cache ) {
				if ( !ptrs.TryGetValue(kv.Key, out IntPtr p) || p != kv.Value.Address ) { anyStale = true; break; }
			}
			if ( !anyStale ) return;
			// (a new dictionary, published whole: this can run on the EntityCache thread while the main thread reads the old one)
			var kept = new Dictionary<string, MemoryObject>();
			foreach ( var kv in cache ) {
				if ( ptrs.TryGetValue(kv.Key, out IntPtr p) && p == kv.Value.Address ) kept[kv.Key] = kv.Value;
			}
			ComponentCache = kept;
		}
		// Read this entity's components from memory: was itself if nothing changed since it was read, a new Parsed if
		// something did, null (and why in error) if the read can not be trusted.
		//
		// Two things are read: this entity's own array of pointers to its component instances (Entity.ComponentBasePtrs:
		// its Life, Positioned, ...), and its type's ComponentLookup (EntityDetails.ptrComponentLookup), the packed array
		// of a hash map from component name to slot in that array ("Life" -> 3). The lookup is shared by every entity of
		// one type (checked live, 3.30.0d: 154 entities, 28 types, 28 lookups, none shared between types), so it is
		// cached by its address (componentLookups).
		private Parsed ReadComponents(Parsed was, out string error) {
			error = null;
			var basePtrsHandle = new ArrayHandle<IntPtr>(Cache.ComponentBasePtrs);
			int count = basePtrsHandle.Length;
			if ( count == 0 || count > COMPONENT_MAX ) { error = $"invalid length of basePtrs array: {Describe(basePtrsHandle)}"; return null; }
			// (all the pointers in one read)
			var basePtrs = basePtrsHandle.ToArray(limit: COMPONENT_MAX);
			if ( basePtrs.Length != count ) { error = $"read {basePtrs.Length} of {count} basePtrs"; return null; }
			for ( int i = 0; i < count; i++ ) {
				if ( !IsValid(basePtrs[i]) ) { error = $"invalid basePtr {Describe(basePtrs[i])} at index {i}"; return null; }
			}
			IntPtr lookup = Details.Value.ptrComponentLookup;
			if ( !IsValid(lookup) ) { error = $"invalid ptrComponentLookup {Describe(lookup)}"; return null; }

			// the same component instances, and the same type: the same map
			if ( was != null && was.Lookup == lookup && basePtrs.AsSpan().SequenceEqual(was.BasePtrs) ) return was;

			var names = LookupNames(lookup, count, out error);
			if ( names == null ) return null;
			var map = new Dictionary<string, IntPtr>(count);
			foreach ( var (name, index) in names ) map[name] = basePtrs[index];
			return new Parsed() { Map = map, BasePtrs = basePtrs, Lookup = lookup };
		}

		// A type's ComponentLookup, read: each component's name and its slot in an entity's ComponentBasePtrs; and the
		// entries' array (head and count) it was read from, to tell a lookup changed at the same address.
		private sealed class ComponentLookupNames {
			public IntPtr Head;
			public int Count;
			public (string name, int index)[] Names;
		}
		// by ptrComponentLookup; cleared on each area change and attach (see ClearAreaCaches)
		private static readonly ConcurrentDictionary<IntPtr, ComponentLookupNames> componentLookups = new ConcurrentDictionary<IntPtr, ComponentLookupNames>();
		// The component names, by the address of the name: the game's own static strings, in its exe image (seen live
		// 3.30.0d: mod+0x36129D8 and the like), the same for every entity of every type. Cleared on attach.
		private static readonly ConcurrentDictionary<IntPtr, string> componentNames = new ConcurrentDictionary<IntPtr, string>();

		// the names and slots in the lookup at ptrLookup, for an entity with count components; null (and why) if it can
		// not be read, or does not agree with the entity
		private static (string name, int index)[] LookupNames(IntPtr ptrLookup, int count, out string error) {
			error = null;
			IntPtr mapAt = ptrLookup + GetOffset<Offsets.ComponentLookup>("ComponentMap");
			if ( !PoEMemory.TryRead(mapAt, out Offsets.ArrayHandle handle) ) { error = $"failed to read ComponentMap handle from {Describe(mapAt)}"; return null; }
			var entries = new ArrayHandle<Offsets.NameAndIndexStruct>(handle);
			int n = entries.Length;
			// A torn read (the entity freed, or its memory reused, while it was read) is caught by these: one name for each
			// component, and the slots exactly 0..count-1, each once (all held, live 3.30.0d, for all 154 entities checked).
			if ( n != count ) { error = $"the ComponentMap has {n} names for {count} components"; return null; }
			if ( componentLookups.TryGetValue(ptrLookup, out var known) && known.Head == handle.Head && known.Count == n ) return known.Names;

			// (all the entries in one read)
			var raw = entries.ToArray(limit: COMPONENT_MAX);
			if ( raw.Length != n ) { error = $"read {raw.Length} of {n} ComponentMap entries"; return null; }
			var names = new (string name, int index)[n];
			var seen = new bool[n];
			for ( int i = 0; i < n; i++ ) {
				uint index = raw[i].Index;
				if ( index >= (uint)n ) { error = $"invalid entry index: {index} should be in [0..{n})"; return null; }
				if ( seen[index] ) { error = $"entry index {index} is in the ComponentMap twice"; return null; }
				seen[index] = true;
				string name = ComponentNameAt(raw[i].ptrName, out error);
				if ( name == null ) return null;
				names[i] = (name, (int)index);
			}
			componentLookups[ptrLookup] = new ComponentLookupNames() { Head = handle.Head, Count = n, Names = names };
			return names;
		}

		// the component name at ptrName (cached, see componentNames); null (and why) if it can not be read
		private static string ComponentNameAt(IntPtr ptrName, out string error) {
			error = null;
			if ( !IsValid(ptrName) ) { error = $"invalid entry name ptr {Describe(ptrName)}"; return null; }
			if ( componentNames.TryGetValue(ptrName, out string name) ) return name;
			if ( !PoEMemory.TryReadString(ptrName, Encoding.ASCII, out name) ) { error = $"failed to read component name from ptr {Describe(ptrName)}"; return null; }
			if ( name.Length < 3 || name.Length > COMPONENT_MAX_NAME ) { error = $"invalid component name \"{name}\""; return null; }
			componentNames[ptrName] = name;
			return name;
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
