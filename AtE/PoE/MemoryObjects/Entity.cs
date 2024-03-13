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
		
		public static bool IsValid(Entity ent) => ent != null
			&& IsValid(ent.Address)
			// && ent.ServerId > 0 && ent.ServerId < int.MaxValue
			&& (ent.Path?.StartsWith("Meta") ?? false);

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
				&& (e.Path?.StartsWith("Metadata/Monster") ?? false)
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

		public Entity() : base() {
			Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);
		}

		public override IntPtr Address {
			get => base.Address;
			set {
				if ( value == base.Address ) {
					return;
				}
				// when this gets assigned, `Cache` gets a new value as well
				base.Address = value;
				Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);

				if ( !IsValid(value) ) {
					return;
				}
				// invalidate the private caches whenever Address changes
				path = null;
				ComponentPtrs = null;
				ComponentCache = null;

			}
		}

		/// <summary>
		///  The remote id used by the PoE server to sync the ent.
		///  Client-only effects do have Entity structure, but dont have Id.
		/// </summary>
		public uint Id => Address == IntPtr.Zero ? 0 : Cache.Id;

		private string path;
		private IntPtr pathOrigin = IntPtr.Zero;
		public string Path {
			get {
				if ( !IsValid(Address) ) return null;
				if ( IsValid(Details.Value.ptrPath) ) {
					// if we have a cached path, read from the same pointer
					IntPtr curPath = Details.Value.ptrPath;
					if ( path != null && pathOrigin == curPath ) {
						return path;
					} else {
						// otherwise, read the path and cache it
						if( PoEMemory.TryReadString(curPath, Encoding.Unicode, out path) ) {
							pathOrigin = curPath;
							return path;
							// TODO: if could be that this is a good place to invalidate ComponentPtr as well
						}
					}
				}
				return null;
			}
		}

		public bool HasComponent<T>() where T : MemoryObject, new() => IsValid(Address) && GetComponent<T>() != null;
		public bool HasComponent(string name) => ComponentPtrs?.ContainsKey(name) ?? false;

		public T GetComponent<T>() where T : MemoryObject, new() {
			if ( !IsValid(Address) ) {
				return null;
			}
			if ( Thread.CurrentThread.ManagedThreadId != 1 ) {
				Log($"Warning: GetComponent<{typeof(T).Name}> called from background thread!");
			}
			if ( ComponentPtrs == null ) {
				ParseComponents();
			}
			if ( ComponentPtrs == null ) {
				// all the above failed to parse any ptrs, so there are no components
				ClearComponents();
				return null;
			}

			string key = typeof(T).Name;
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
			return new RectangleF(pos.X - bounds.X, pos.Y - bounds.Y, bounds.X, bounds.Y);
		}

		public Dictionary<string,IntPtr> GetComponents() {
			if( ComponentPtrs == null ) {
				ParseComponents();
			}
			return ComponentPtrs;
		}

		private void UpdateParsedIndex(string name, IntPtr addr) {
			if ( IsValid(addr) ) {
				ComponentPtrs.Add(name, addr);
			}
		}

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
			var namesArray = new Offsets.ComponentNameAndIndexStruct[lookup.Counter];
			if ( 0 == PoEMemory.TryRead(lookup.ComponentMap, namesArray) ) {
				ImGui.Text($"ComponentMap: Read 0 bytes, aborting.");
				return;
			}

			foreach(var entry in namesArray ) {
				ImGui.Text($" - Index: {entry.Index} '{(PoEMemory.TryReadString(entry.ptrName, Encoding.ASCII, out string name0) ? name0 : "")}'");
			}

		}
		private void ParseComponents() {

			using ( Perf.Section("ParseComponents") ) {
				ComponentPtrs = new Dictionary<string, IntPtr>();
				// the Entity has a list of ptr to Component, managed by an ArrayHandle
				// (these are the .Values of the underlying map)
				var basePtrs = new ArrayHandle<IntPtr>(Cache.ComponentBasePtrs).ToArray(limit: 32);
				if ( basePtrs.Length == 0 || basePtrs.Length > 32 ) {
					ClearComponents();
					return;
				}

				// stored separately, is the control structure of the ComponentMap, called a ComponentLookup
				if ( !PoEMemory.TryRead(Details.Value.ptrComponentLookup, out Offsets.ComponentLookup lookup) ) {
					ClearComponents();
					return;
				}

				// sanity checks on the lookup structure and what it claims to hold
				if ( lookup.Capacity < 1 || lookup.Capacity > 1024 ) {
					ClearComponents();
					return;
				}

				if ( lookup.Counter < 1 || lookup.Counter > 32 ) {
					ClearComponents();
					return;
				}

				// in the control structure is the list of names, used as keys to index the basePtrs
				var namesArray = new Offsets.ComponentNameAndIndexStruct[lookup.Counter];
				if ( 0 == PoEMemory.TryRead(lookup.ComponentMap, namesArray) ) {
					ClearComponents();
					return;
				}
				foreach ( var entry in namesArray ) {
					if( entry.Index >= 0 && entry.Index < basePtrs.Length ) {
						if( PoEMemory.TryReadString(entry.ptrName, Encoding.ASCII, out string name) && name.Length > 2 && name.Length < 128 ) {
							ComponentPtrs[name] = basePtrs[entry.Index];
						}
					}
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
		public Animated Animated => GetComponent<Animated>();

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
