using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static AtE.Globals;

namespace AtE {
	public static partial class Globals {
		
		public static bool IsValid(Entity ent) => ent != null
			// && ent.ServerId > 0 && ent.ServerId < int.MaxValue
			&& (ent.Path?.StartsWith("Metadata") ?? false);

		public static Entity GetEntityById(ushort id) =>
			PoEMemory.GameRoot?.InGameState.Entities.FirstOrDefault(e => e.Id == id);

	}
	public class Entity : MemoryObject {
		public Cached<Offsets.Entity> Cache;
		public Cached<Offsets.EntityDetails> Details;
		public Entity() {
			Cache = CachedStruct<Offsets.Entity>(this);
			Details = CachedStruct<Offsets.EntityDetails>(() => Cache.Value.ptrDetails);
		}

		/// <summary>
		///  The remote id used by the PoE server to sync the ent.
		///  Client-only effects do have Entity structure, but dont have Id.
		/// </summary>
		public uint Id => Cache.Value.Id;

		public string Path => PoEMemory.TryReadString(Details.Value.ptrPath, Encoding.Unicode, out string ret) ? ret : null;

		public bool HasComponent<T>() where T : MemoryObject, new() {
			if ( Components == null ) Components = GetComponents();
			return Components?.TryGetValue(typeof(T).Name, out var _) ?? false;
		}

		public T GetComponent<T>() where T : MemoryObject, new() {
			if ( Components == null ) Components = GetComponents();
			if ( Components == null ) return null;
			if ( Components.TryGetValue(typeof(T).Name, out IntPtr ret) ) {
				return new T() { Address = ret };
			}
			return null;
		}

		public Life Life => GetComponent<Life>();
		public Actor Actor => GetComponent<Actor>();

		private Dictionary<string, IntPtr> Components;

		public Dictionary<string, IntPtr> GetComponents() {

			// Build a map of Component name to address
			var result = new Dictionary<string, IntPtr>();
			// the entity has a list of ptr to Component
			// managed by an ArrayHandle at ComponentsArray
			var entityComponents = Cache.Value.ComponentsArray.GetItems<IntPtr>().ToArray();
			if( ! PoEMemory.TryRead(Details.Value.ptrComponentLookup, out Offsets.ComponentLookup lookup) ) {
				return result;
			}
			if ( lookup.Capacity < 1 || lookup.Capacity > 24 ) {
				return result;
			}

			// stored separately is a lookup table, stored as
			// a packed array of (ptr string, int index) pairs

			// each entry in the array packs 8 items into one struct, so we read N / 8 structs
			int trueCapacity = (int)((lookup.Capacity + 1) / 8);
			var componentArray = new Offsets.ComponentArrayEntry[trueCapacity];
			if ( 0 == PoEMemory.TryRead(lookup.ComponentArray, componentArray) ) {
				return result;
			}

			string name;
			foreach ( var entry in componentArray ) {
				if ( entry.Flag0 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer0.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer0.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, entityComponents[index]);
					}
				}
				if ( entry.Flag1 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer1.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer1.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, entityComponents[index]);
					}
				}
				if ( entry.Flag2 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer2.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer2.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, entityComponents[index]);
					}
				}
				if ( entry.Flag3 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer3.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer3.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, Address = entityComponents[index] );
					}
				}
				if ( entry.Flag4 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer4.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer4.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, Address = entityComponents[index] );
					}
				}
				if ( entry.Flag5 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer5.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer5.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, entityComponents[index]);
					}
				}
				if ( entry.Flag6 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer6.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer6.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, entityComponents[index]);
					}
				}
				if ( entry.Flag7 != byte.MaxValue
					&& PoEMemory.TryReadString(entry.Pointer7.ptrName, Encoding.ASCII, out name)
					&& !string.IsNullOrWhiteSpace(name)
					&& !result.ContainsKey(name) ) {
					int index = entry.Pointer7.Index;
					if ( index >= 0 && index <= entityComponents.Length ) {
						result.Add(name, entityComponents[index]);
					}

				}
			}

			return result;
		}
	}

	/* see InGameState.GetEntities(), which re-implements this using the structs directly, and adds Entity caching
	public class EntityList : MemoryObject, IEnumerable<Entity> {
		public Cached<Offsets.EntityList> Cache;
		public EntityList() => Cache = CachedStruct<Offsets.EntityList>(this);
		IEnumerator IEnumerable.GetEnumerator() => null;
		public IEnumerator<Entity> GetEnumerator() {
			IntPtr cursor = Cache.Value.Head;
			int count = 0;
			while( PoEMemory.TryRead(cursor, out Offsets.EntityListNode node) ) {
				yield return new Entity() { Address = node.Entity };
				count += 1;
				cursor = node.Third;
				if ( cursor == IntPtr.Zero || cursor == Cache.Value.Head || count > 2 ) yield break;
			}
		}
	}
	public class EntityListNode : MemoryObject {
		public Cached<Offsets.EntityListNode> Cache;
		public EntityListNode() => Cache = CachedStruct<Offsets.EntityListNode>(this);
		public EntityListNode(Offsets.EntityListNode node) =>
			Cache = new Cached<Offsets.EntityListNode>(() => node);
		public Entity Entity => new Entity() { Address = Cache.Value.Entity };
		public EntityListNode First => new EntityListNode() { Address = Cache.Value.First };
		public EntityListNode Second => new EntityListNode() { Address = Cache.Value.Second };
		public EntityListNode Third => new EntityListNode() { Address = Cache.Value.Third };
	}
	*/

}
