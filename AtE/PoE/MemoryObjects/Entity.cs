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

		public IEnumerable<string> GetComponentNames() {
			if( ComponentPtrs == null ) {
				ParseComponents();
			}
			return ComponentPtrs?.Keys ?? Empty<string>();
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
			var componentsArray = new ArrayHandle<IntPtr>(Cache.ComponentsArray);
			ImGui.Text($"ComponentsArray claims to have {componentsArray.Length} items.");
			if ( !PoEMemory.TryRead(Details.Value.ptrComponentLookup, out Offsets.ComponentLookup lookup) ) {
				ImGui.Text("Failed to read ptrComponentLookup");
			} else {
				ImGui.Text($"ComponentLookup Capacity: {lookup.Counter} of {lookup.Capacity}");
			}
			// each entry in the array packs 8 items into one struct, so we read N / 8 structs
			int trueCapacity = (int)((lookup.Capacity + 1) / 8);
			ImGui.Text($"True Capacity: {trueCapacity}");
			var componentArray = new Offsets.ComponentArrayEntry[trueCapacity];
			if ( 0 == PoEMemory.TryRead(lookup.ComponentMap, componentArray) ) {
				ImGui.Text($"Read 0 bytes, aborting.");
				return;
			}

			foreach(var entry in componentArray ) {
				ImGui.Text($"Entry: ---");
				ImGui.Text($"  Flag0: 0x{entry.Flag0:X} {entry.Pointer0.Index} {(PoEMemory.TryReadString(entry.Pointer0.ptrName, Encoding.ASCII, out string name0) ? name0 : "")}");
				ImGui.Text($"  Flag1: 0x{entry.Flag1:X} {entry.Pointer1.Index} {(PoEMemory.TryReadString(entry.Pointer1.ptrName, Encoding.ASCII, out string name1) ? name1 : "")}");
				ImGui.Text($"  Flag2: 0x{entry.Flag2:X} {entry.Pointer2.Index} {(PoEMemory.TryReadString(entry.Pointer2.ptrName, Encoding.ASCII, out string name2) ? name2 : "")}");
				ImGui.Text($"  Flag3: 0x{entry.Flag3:X} {entry.Pointer3.Index} {(PoEMemory.TryReadString(entry.Pointer3.ptrName, Encoding.ASCII, out string name3) ? name3 : "")}");
				ImGui.Text($"  Flag4: 0x{entry.Flag4:X} {entry.Pointer4.Index} {(PoEMemory.TryReadString(entry.Pointer4.ptrName, Encoding.ASCII, out string name4) ? name4 : "")}");
				ImGui.Text($"  Flag5: 0x{entry.Flag5:X} {entry.Pointer5.Index} {(PoEMemory.TryReadString(entry.Pointer5.ptrName, Encoding.ASCII, out string name5) ? name5 : "")}");
				ImGui.Text($"  Flag6: 0x{entry.Flag6:X} {entry.Pointer6.Index} {(PoEMemory.TryReadString(entry.Pointer6.ptrName, Encoding.ASCII, out string name6) ? name6 : "")}");
				ImGui.Text($"  Flag7: 0x{entry.Flag7:X} {entry.Pointer7.Index} {(PoEMemory.TryReadString(entry.Pointer7.ptrName, Encoding.ASCII, out string name7) ? name7 : "")}");
			}

		}
		private void ParseComponents() {

			using ( Perf.Section("ParseComponents") ) {
				ComponentPtrs = new Dictionary<string, IntPtr>();
				// the entity has a list of ptr to Component managed by an ArrayHandle at ComponentsArray
				// this is basically the .Values element of the ComponentMap
				var entityComponents = new ArrayHandle<IntPtr>(Cache.ComponentsArray)
					.ToArray(limit: 50); // if it claims to have more than 50 components, its corrupt data
				if ( entityComponents.Length == 0 ) {
					ClearComponents();
					return;
				}

				// stored separately, is the control structure of the ComponentMap, called a ComponentLookup
				if ( !PoEMemory.TryRead(Details.Value.ptrComponentLookup, out Offsets.ComponentLookup lookup) ) {
					return;
				}
				// sanity checks on the lookup structure and what it claims to hold
				if ( lookup.Capacity < 1 || lookup.Capacity > 1024 ) {
					ClearComponents();
					return;
				}

				// stored separately is a lookup table, stored as
				// a packed array of (ptr string, int index) pairs

				// each entry in the array packs 8 items into one struct, so we read N / 8 structs
				int trueCapacity = (int)((lookup.Capacity + 1) / 8);
				var componentArray = new Offsets.ComponentArrayEntry[trueCapacity];
				if ( 0 == PoEMemory.TryRead(lookup.ComponentMap, componentArray) ) {
					ClearComponents();
					return;
				}

				string name;
				foreach ( var entry in componentArray ) {

					if ( entry.Flag0 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer0.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer0.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag1 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer1.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer1.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag2 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer2.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer2.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag3 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer3.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer3.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag4 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer4.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer4.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag5 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer5.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer5.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag6 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer6.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer6.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
						}
					}

					if ( entry.Flag7 != byte.MaxValue
						&& PoEMemory.TryReadString(entry.Pointer7.ptrName, Encoding.ASCII, out name)
						&& !string.IsNullOrWhiteSpace(name)
						) {
						int index = entry.Pointer7.Index;
						if ( index >= 0 && index < entityComponents.Length ) {
							UpdateParsedIndex(name, entityComponents[index]);
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
