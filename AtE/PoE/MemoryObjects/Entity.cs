using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {
	public static partial class Globals {
		
		public static bool IsValid(Entity ent) => ent != null
			&& ent.Address != IntPtr.Zero
			// && ent.ServerId > 0 && ent.ServerId < int.MaxValue
			&& (ent.Path?.StartsWith("Meta") ?? false);

		public static Entity GetEntityById(ushort id) => EntityCache.TryGetEntity(id, out Entity ret) ? ret : null;

		/// <summary>
		/// Helper to get the Player from the GameRoot.
		/// Call GetPlayer() every frame, as the underlying object can be moved by PoE at any time.
		/// </summary>
		/// <returns>The current Player Entity.</returns>
		public static PlayerEntity GetPlayer() => PoEMemory.GameRoot?.InGameState?.Player;

		/// <summary>
		/// Helper to get the full Entity list from the InGameState (safely cached) from anywhere.
		/// The result is only considered valid for this frame.
		/// </summary>
		/// <returns>An enumerable over the current entity list.</returns>
		public static IEnumerable<Entity> GetEntities() => EntityCache.GetEntities();

		public static IEnumerable<Entity> GetEnemies() => GetEntities()?
			.Take(1000)
			.Where(e => (e.Path?.StartsWith("Metadata/Monster") ?? false)
				&& (e.GetComponent<Positioned>()?.IsHostile ?? false)) ?? Empty<Entity>();

		public static IEnumerable<Entity> NearbyEnemies(float radius) {
			Vector3 playerPos = Position(GetPlayer());
			return playerPos == Vector3.Zero ? Empty<Entity>() : GetEnemies().Where(e => Distance(playerPos, Position(e)) <= radius);
		}

		public static IEnumerable<Entity> NearbyEnemies(float radius, Offsets.MonsterRarity rarity) =>
			NearbyEnemies(radius).Where(e => (e.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White) >= rarity);


		public static void DrawTextAt(Entity ent, string text, Color color) {
			if ( !IsValid(ent) ) return;
			var pos = ent.GetComponent<Render>()?.Position ?? Vector3.Zero; 
			DrawTextAt(ent.Id, WorldToScreen(pos), text, color);
		}

	}
	public class Entity : MemoryObject<Offsets.Entity> {
		public Cached<Offsets.EntityDetails> Details;
		public Entity() : base() {
			Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);
		}

		public override IntPtr Address {
			get => base.Address;
			set {
				if ( value == base.Address ) {
					return;
				}
				// when this gets assigned, Cache gets a new value as well
				base.Address = value;
				Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);

				if ( !IsValid(value) ) {
					return;
				}
				uint id = Cache.Id; // this will read Offset.Entity struct from memory (same cost as before)
				if ( id != 0 ) {
					// if the same Entity id is at the same address as it was last time, we can re-use ComponentPtrs (and skip parsing)
					if ( lastKnownAddress.TryGetValue(id, out IntPtr prev) && prev == value ) {
						lastKnownComponents.TryGetValue(id, out ComponentPtrs);
						// ImGui.Text($"[{id}] stable at {Format(prev)} -> {ComponentPtrs?.Count ?? 0} components");
					} else {
						// otherwise, this is an actual change-of-address for this object
						// so update last known address, and invalidate various caches
						// Log($"New entity {id} at address {Format(value)}");
						// Log($"Entity[{id}] at new address: {Format(value)} {Path}");
						lastKnownAddress[id] = value;
						lastKnownComponents.TryRemove(id, out _);
						ComponentPtrs = null;
						ComponentCache?.Clear();
						ComponentCache = null;
						path = null;
					}
				}
			}
		}

		/// <summary>
		///  The remote id used by the PoE server to sync the ent.
		///  Client-only effects do have Entity structure, but dont have Id.
		/// </summary>
		public uint Id => Address == IntPtr.Zero ? 0 : Cache.Id;

		private string path;
		public string Path => Address == IntPtr.Zero ? null :
			path != null ? path :
			PoEMemory.TryReadString(Details.Value.ptrPath, Encoding.Unicode, out path) ? path
			: null;

		public bool HasComponent<T>() where T : MemoryObject, new() => Address != IntPtr.Zero && GetComponent<T>() != null;

		public T GetComponent<T>() where T : MemoryObject, new() {
			if ( Address == IntPtr.Zero ) {
				return null;
			}
			using ( Perf.Section("GetComponent") ) {
				if ( ComponentPtrs == null ) {
					ParseComponents();
					// save the output of the parsing for next time
					lastKnownComponents[Id] = ComponentPtrs;
				}
				if ( ComponentPtrs == null ) {
					// all the above failed to parse any ptrs, so there are no components
					ComponentCache?.Clear();
					ComponentCache = null;
					return null;
				}

				string key = typeof(T).Name;
				if( ComponentCache != null && ComponentCache.TryGetValue(key, out MemoryObject cachedResult) ) {
					return (T)cachedResult;
				}

				if ( ComponentPtrs.TryGetValue(key, out IntPtr ptr) ) {
					var ret = new T() { Address = ptr };
					if( ComponentCache == null ) {
						ComponentCache = new Dictionary<string, MemoryObject>();
					}
					ComponentCache[key] = ret;
					return ret;
				}
				return null;
			}
		}

		// keep track of when an entity id moves to a new address in memory
		// this is a map of <entity id, address>
		private static ConcurrentDictionary<uint, IntPtr> lastKnownAddress = new ConcurrentDictionary<uint, IntPtr>();
		// as long as an entity stays at the same address, we skip the parsing and re-use the output, stored here:
		// this stores a map<entity id, map< component name, component address >>
		private static ConcurrentDictionary<uint, Dictionary<string, IntPtr>> lastKnownComponents = new ConcurrentDictionary<uint, Dictionary<string, IntPtr>>();

		// maps the Type (of a Component) to the Address of that Component instance for this Entity
		private Dictionary<string, IntPtr> ComponentPtrs; // we have to parse this all at once
		private Dictionary<string, MemoryObject> ComponentCache; // these are filled in as requested, then re-used if requested a second time
		private void UpdateParsedIndex(string name, IntPtr addr) {
			ComponentPtrs.Add(name, addr);
		}

		private void ParseComponents() {

			using ( Perf.Section("ParseComponents") ) {
				ComponentPtrs = new Dictionary<string, IntPtr>();
				// the entity has a list of ptr to Component
				// managed by an ArrayHandle at ComponentsArray
				var entityComponents = new ArrayHandle<IntPtr>(Cache.ComponentsArray)
					.ToArray(limit: 30); // if it claims to have more than 50 components, its corrupt data
				if ( entityComponents.Length == 0 ) {
					return;
				}
				if ( !PoEMemory.TryRead(Details.Value.ptrComponentLookup, out Offsets.ComponentLookup lookup) ) {
					return;
				}
				if ( lookup.Capacity < 1 || lookup.Capacity > 24 ) {
					return;
				}

				// stored separately is a lookup table, stored as
				// a packed array of (ptr string, int index) pairs

				// each entry in the array packs 8 items into one struct, so we read N / 8 structs
				int trueCapacity = (int)((lookup.Capacity + 1) / 8);
				var componentArray = new Offsets.ComponentArrayEntry[trueCapacity];
				if ( 0 == PoEMemory.TryRead(lookup.ComponentMap, componentArray) ) {
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

		/// <summary>
		/// Where to find data about your Skills, and the DeployedObjects they create.
		/// </summary>
		public Actor Actor => GetComponent<Actor>();

		public Stats Stats => GetComponent<Stats>();

		public Buffs Buffs => GetComponent<Buffs>();

		private Render render;
		public Render Render => IsValid(render) ? render : render = GetComponent<Render>();
		public Vector3 Position => Render?.Position ?? Vector3.Zero; 
		public Vector3 Bounds => Render?.Bounds ?? Vector3.Zero; 

		public RectangleF GetClientRect() {
			if ( !IsValid(Render) ) return RectangleF.Empty;
			var pos = WorldToScreen(Position);
			var far = WorldToScreen(Position + Bounds);
			return new RectangleF(pos.X, pos.Y, far.X - pos.X, far.Y - pos.Y);
		}
	}


}
