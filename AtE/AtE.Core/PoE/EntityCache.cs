using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static AtE.Globals;

namespace AtE {
	public static class EntityCache {

		private static readonly ConcurrentDictionary<IntPtr, Entity> EntitiesByAddress = new ConcurrentDictionary<IntPtr, Entity>();
		private static readonly ConcurrentDictionary<uint, Entity> EntitiesById = new ConcurrentDictionary<uint, Entity>();
		public static bool TryGetEntity(uint id, out Entity ent) => EntitiesById.TryGetValue(id, out ent);

		public static bool TryGetEntity(IntPtr ptr, out Entity ent) {
			ent = null;
			// if the ptr is to a valid memory range
			if( IsValid(ptr) ) {
				// ensure ptr is in the cache, as an Entity, constructing one if the ptr is being added
				ent = EntitiesByAddress.GetOrAdd(ptr, newEnt);
				if ( IsValid(ent) ) {
					if( ent.Address != ptr ) {
						ent.Address = ptr;
					}
					// the game reuses the memory of animated objects and items: if the object at ptr is not the one this
					// Entity was made for, replace it with a fresh Entity, so no path or components carry over
					if ( ent.IsRecycled() ) {
						var fresh = new Entity() { Address = ptr };
						if ( !IsValid(fresh) ) {
							ent = null;
							return false;
						}
						fresh.IsRecycled(); // record its identity
						EntitiesByAddress.TryUpdate(ptr, fresh, ent);
						Interlocked.Increment(ref recycledCount);
						ent = fresh;
					}
					ent.LastLookup = Time.ElapsedMilliseconds;
					return true;
				} else {
					// Log($"EntityCache: ptr {Describe(ptr)} -> {Describe(ent.Address)} failed to produce valid Entity.");
				}
			}
			return false;
		}
		private static Entity newEnt(IntPtr a) => new Entity() { Address = a }; // define this out here so we dont have to create a lambda every time

		/// <summary>How many times TryGetEntity(IntPtr) found the game had put another object at a cached address.</summary>
		public static int RecycledCount => recycledCount;
		private static int recycledCount;

		// Entities made by TryGetEntity(IntPtr) (animated objects, items, targets) are dropped from the address map when
		// not looked up for this long; nothing else removes them (the walk only unloads its own)
		private const long EVICT_AFTER_MS = 5000;
		private const long EVICT_EVERY_MS = 2000;
		private static long nextEvict;
		private static void EvictStale() {
			long now = Time.ElapsedMilliseconds;
			if ( now < nextEvict ) return;
			nextEvict = now + EVICT_EVERY_MS;
			foreach ( var kv in EntitiesByAddress ) {
				var e = kv.Value;
				if ( e != null && !e.FromWalk && now - e.LastLookup > EVICT_AFTER_MS ) {
					// (not cleared: a holder of it keeps working; the next lookup just makes a new one)
					((ICollection<KeyValuePair<IntPtr, Entity>>)EntitiesByAddress).Remove(kv);
				}
			}
		}

		/// <summary>
		/// The "world" entities (things like monsters, players, objects), as of the last complete walk of the entity list.
		/// This doesn't include non-world entities like the items in your backpack, nor the ids with the high bit set
		/// (see GetLocalEntities). A new array is published by each walk, so holding on to one is safe; an entity the
		/// game removes after the walk has its Address cleared, so it stops passing IsValid.
		/// </summary>
		public static IReadOnlyList<Entity> GetEntities() => entities;
		private static volatile Entity[] entities = Array.Empty<Entity>();

		/// <summary>
		/// The projectiles in flight, as of the last walk of the entity list. Their ids have the high bit set (0x80000000
		/// to 0xBFFFFFFF; seen 3.30.0c: Metadata/Projectiles/CorruptedBallLightning, WandProjectile), so they are not among
		/// GetEntities, which skips every id with the high bit set (the client-only entities, 0xC0000000 and up: doodads,
		/// critters, client-side ground effects).
		/// </summary>
		public static IReadOnlyList<Entity> GetProjectiles() => projectiles;
		private static volatile Entity[] projectiles = Array.Empty<Entity>();
		/// <summary>
		/// Every entity of the last walk of the entity list that GetEntities skips, its id having the high bit set: the
		/// projectiles (see GetProjectiles) and the client-only entities (0xC0000000 and up: doodads, critters, client-side
		/// ground effects). These are not validated by the walk (most are never looked at); check IsValid before use.
		/// </summary>
		public static IReadOnlyList<Entity> GetLocalEntities() => localEntities;
		private static volatile Entity[] localEntities = Array.Empty<Entity>();
		// the Entity made for each address of those, and the id it was made for (so one keeps its cached components from
		// walk to walk, and a new entity at a reused address gets a new Entity); double-buffered, swapped after each walk
		private struct LocalEntry { public uint Id; public Entity Ent; }
		private static Dictionary<IntPtr, LocalEntry> localByAddress = new Dictionary<IntPtr, LocalEntry>();
		private static Dictionary<IntPtr, LocalEntry> nextLocalByAddress = new Dictionary<IntPtr, LocalEntry>();
		private static bool IsProjectileId(uint id) => (id & 0xC0000000) == 0x80000000;

		public static EventHandler<Entity> EntityAdded;
		public static EventHandler<uint> EntityRemoved;

		public static int AddressCount => EntitiesByAddress.Count;
		public static int IdCount => EntitiesById.Count;

		internal static void Debug() {
		}

		// drop a world entity the game no longer has: out of both maps, and its Address cleared so held references go invalid
		private static void Unload(uint id) {
			if ( EntitiesById.TryRemove(id, out Entity ent) && ent != null ) {
				if ( ent.Address != IntPtr.Zero ) {
					// only if the address still maps to this Entity (TryGetEntity(ptr) may have made another there since)
					((ICollection<KeyValuePair<IntPtr, Entity>>)EntitiesByAddress).Remove(new KeyValuePair<IntPtr, Entity>(ent.Address, ent));
					ent.Address = IntPtr.Zero;
				}
				EntityRemoved?.Invoke(null, id);
			}
		}

		internal static void MainThread() {
			Log($"EntityCache: MainThread starting...");

			// the world entity ids found on the last complete walk, and on this one (swapped after each complete walk)
			HashSet<uint> prevIds = new HashSet<uint>();
			HashSet<uint> seenIds = new HashSet<uint>();
			// the nodes still to visit; it starts and ends empty with each walk of the tree
			Stack<IntPtr> pending = new Stack<IntPtr>();
			List<Entity> foundEntities = new List<Entity>();
			List<Entity> foundLocal = new List<Entity>();
			List<Entity> foundProjectiles = new List<Entity>();

			while( true ) {

				// exit if the main form exits (Overlay.IsClosed now folds in the window's liveness)
				if ( Overlay.IsClosed ) {
					break;
				}

				using ( Perf.Section("EntityThread Idle") ) {
					// do nothing if not attached
					if ( !PoEMemory.IsAttached ) {
						Thread.Sleep(9000);
						continue; // check again
					} else {
						// use FrameLock to make sure we dont advance any faster than the main frame rate
						Overlay.FrameLock.WaitOne(1000);
						// Thread.Sleep(1); // using this instead of FrameLock will cap EntityThread at about 60fps,
						// but, it really sleeps in multiples of 15ms, so you either get 60fps, 30fps, 15fps, etc
						// and, if the main thread slows way down, this thread would not
					}
				}

				using ( Perf.Section("EntityThread") ) {
					IntPtr headPtr;
					long count;
					try {
						var igs = PoEMemory.GameRoot?.InGameState;
						if( ! IsValid(igs) ) {
							continue;
						}
						headPtr = igs.EntityListHeadPtr;
						count = igs.EntitiesCount;
					} catch ( NullReferenceException ) {
						continue;
					}
					if ( !PoEMemory.TryRead(headPtr, out Offsets.EntityListNode head) || head.IsNil == 0 ) {
						continue;
					}

					pending.Clear();
					seenIds.Clear();
					foundEntities.Clear();
					foundLocal.Clear();
					foundProjectiles.Clear();
					nextLocalByAddress.Clear();

					// The map is a red-black tree: walk it from the root through the left and right children only (each
					// node is read once), stopping at links to the head (the nil leaves). The game can change the tree
					// while we read it, so a torn read could make a cycle: visit at most the map's size (plus slack).
					int budget = count > 0 && count < 100_000 ? (int)count + 256 : 20_000;
					bool complete = true;
					if ( head.Second != headPtr ) { // an empty map has the head as its root
						pending.Push(head.Second);
					}
					while ( pending.Count > 0 ) {
						if ( --budget < 0 ) {
							complete = false;
							break;
						}
						IntPtr nodePtr = pending.Pop();
						if ( !PoEMemory.TryRead(nodePtr, out Offsets.EntityListNode node) || node.IsNil != 0 ) {
							complete = false;
							continue;
						}
						if ( node.First != headPtr ) pending.Push(node.First);
						if ( node.Third != headPtr ) pending.Push(node.Third);

						uint id = node.Id; // the map key is the entity id, so no read of the entity is needed to know it
						IntPtr entPtr = node.Entity;
						if ( id == 0 || !IsValid(entPtr) ) {
							continue;
						}
						if ( (int)id < 0 ) {
							// projectiles and client-only entities: not validated here, most are never looked at
							if ( !localByAddress.TryGetValue(entPtr, out LocalEntry local) || local.Id != id ) {
								local = new LocalEntry() { Id = id, Ent = new Entity() { Address = entPtr } };
							}
							nextLocalByAddress[entPtr] = local;
							foundLocal.Add(local.Ent);
							if ( IsProjectileId(id) ) foundProjectiles.Add(local.Ent);
							continue;
						}
						if ( !seenIds.Add(id) ) {
							continue;
						}
						// a known entity at the same address needs no reads: the node says it is still there
						if ( !EntitiesById.TryGetValue(id, out Entity ent) || ent == null || ent.Address != entPtr ) {
							// a new id (or the game moved it): validate a fresh Entity (with its own component caches)
							var fresh = new Entity() { Address = entPtr, FromWalk = true };
							if ( !IsValid(fresh) ) {
								seenIds.Remove(id); // try again next walk (it may still be under construction)
								continue;
							}
							if ( fresh.Id != id ) {
								Log($"EntityCache[{id}] entity at {Describe(entPtr)} has id {fresh.Id}, rejecting.");
								seenIds.Remove(id);
								continue;
							}
							if ( ent != null && ent.Address != IntPtr.Zero ) {
								((ICollection<KeyValuePair<IntPtr, Entity>>)EntitiesByAddress).Remove(new KeyValuePair<IntPtr, Entity>(ent.Address, ent));
								ent.Address = IntPtr.Zero;
							}
							EntitiesByAddress[entPtr] = fresh;
							EntitiesById[id] = fresh;
							ent = fresh;
						}
						foundEntities.Add(ent);
					}

					projectiles = foundProjectiles.ToArray();
					localEntities = foundLocal.ToArray();
					(localByAddress, nextLocalByAddress) = (nextLocalByAddress, localByAddress);

					if ( complete ) {
						foreach ( uint id in prevIds ) {
							if ( !seenIds.Contains(id) ) {
								Unload(id);
							}
						}
						(prevIds, seenIds) = (seenIds, prevIds);
						entities = foundEntities.ToArray();
					} else {
						// a partial walk can't tell what was removed: keep the last snapshot, and remember the ids added
						// this walk so a later complete walk can unload them
						prevIds.UnionWith(seenIds);
					}
					EvictStale();
				}

			}
			Log($"EntityCache: MainThread exiting...");
		}

	}
}
