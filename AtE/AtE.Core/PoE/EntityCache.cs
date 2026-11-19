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
					return IsValid(ent);
				} else {
					// Log($"EntityCache: ptr {Describe(ptr)} -> {Describe(ent.Address)} failed to produce valid Entity.");
				}
			}
			return false;
		}
		private static Entity newEnt(IntPtr a) => new Entity() { Address = a }; // define this out here so we dont have to create a lambda every time

		/// <summary>
		/// Enumerate the "world" entities (things like monsters, players, objects).
		/// This doesn't include non-world entities like the items in your backpack.
		/// </summary>
		public static IEnumerable<Entity> GetEntities() {
			foreach(var entry in EntitiesById) {
				if( IsValid(entry.Value) ) {
					yield return entry.Value;
				} else {
					entsToBeRemoved.Enqueue(entry.Key);
				}
			}
		}
		private static ConcurrentQueue<uint> entsToBeRemoved = new ConcurrentQueue<uint>();

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
		/// ground effects).
		/// </summary>
		public static IReadOnlyList<Entity> GetLocalEntities() => localEntities;
		private static volatile Entity[] localEntities = Array.Empty<Entity>();
		// the Entity made for each address of those (so one keeps its cached components from walk to walk)
		private static readonly Dictionary<IntPtr, Entity> localByAddress = new Dictionary<IntPtr, Entity>();
		private static bool IsProjectileId(uint id) => (id & 0xC0000000) == 0x80000000;
		private static void removeAllPending() {
			while( entsToBeRemoved.TryDequeue(out uint id) ) {
				if( EntitiesById.TryRemove(id, out Entity ent) ) {
					EntitiesByAddress.TryRemove(ent.Address, out _);
				}
			}
		}

		public static EventHandler<Entity> EntityAdded;
		public static EventHandler<uint> EntityRemoved;

		public static int AddressCount => EntitiesByAddress.Count;
		public static int IdCount => EntitiesById.Count;
		private static readonly int idOffset = GetOffset<Offsets.Entity>("Id");

		internal static void Debug() {
		}

		internal static void MainThread() {
			Log($"EntityCache: MainThread starting...");

			// used to make sure we dont visit the same node in this tree structure more than once
			HashSet<IntPtr> deduper = new HashSet<IntPtr>();
			// incomingIds is the set of entity ids that we found on the most recent full walk of the tree
			HashSet<uint> incomingIds = new HashSet<uint>();
			// the frontier stack is used to explore the tree, it should start and end empty with each walk of the tree
			Queue<Offsets.EntityListNode> frontier = new Queue<Offsets.EntityListNode>();

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
					// while attached, try to find all the entities and update their Address
					deduper.Clear();
					incomingIds.Clear();
					frontier.Clear();
					removeAllPending();

					try {
						if( ! IsValid(PoEMemory.GameRoot?.InGameState ?? null) ) {
							continue;
						}
						Offsets.EntityListNode head = PoEMemory.GameRoot.InGameState.EntityListHead;
						frontier.Enqueue(head);
					} catch ( NullReferenceException ) {
						continue;
					}
					// we are going to skip reading the Ent from the first (head) node
					bool skippedOne = false;
					var foundProjectiles = new List<Entity>();
					var foundLocal = new List<Entity>();
					while ( frontier.Count > 0 && deduper.Count < 2000 ) {
						var node = frontier.Dequeue();
						IntPtr entPtr = node.Entity;
						if ( ! deduper.Add(entPtr) ) {
							continue;
						}

						// probe only the entity id before we construct a full ent
						if( skippedOne == false ) {
							skippedOne = true;
						} else if ( PoEMemory.TryRead(entPtr + idOffset, out uint id)) {
							if ( (int)id < 0 ) {
								if ( !localByAddress.TryGetValue(entPtr, out Entity local) ) localByAddress[entPtr] = local = new Entity() { Address = entPtr };
								if ( IsValid(local) ) {
									foundLocal.Add(local);
									if ( IsProjectileId(id) ) foundProjectiles.Add(local);
								}
							} else if ( id == 0 ) {
								// Log($"EntityCache[id={id}] Failed to produce a valid ent, rejecting.");
							} else {
								// attempt to construct and cache an Entity instance from this ptr
								if ( TryGetEntity(entPtr, out Entity ent) ) {
									if ( IsValid(ent) && ent.Id == id ) {
										incomingIds.Add(id);
										EntitiesById[id] = ent;
									} else {
										Log($"EntityCache[{id}] Failed to produce a valid ent, rejecting.");
										EntitiesByAddress.TryRemove(ent.Address, out _);
										ent.Address = default;
									}
								}
							}
						}

						if ( PoEMemory.TryRead(node.First, out Offsets.EntityListNode first) ) {
							frontier.Enqueue(first);
						}
						if ( PoEMemory.TryRead(node.Second, out Offsets.EntityListNode second) ) {
							frontier.Enqueue(second);
						}
						if ( PoEMemory.TryRead(node.Third, out Offsets.EntityListNode third) ) {
							frontier.Enqueue(third);
						}
					}
					// now, frontier is empty, and incomingIds is full
					projectiles = foundProjectiles.ToArray();
					localEntities = foundLocal.ToArray();
					if ( localByAddress.Count > foundLocal.Count ) {
						var keep = new HashSet<IntPtr>(foundLocal.Select(e => e.Address));
						foreach ( var gone in localByAddress.Keys.Where(a => !keep.Contains(a)).ToArray() ) localByAddress.Remove(gone);
					}
					var toRemove = EntitiesById.Keys.Where(k => !incomingIds.Contains(k)).ToArray();
					foreach ( var id in toRemove ) {
						// note, that we dont remove from EntitiesById, but mark as Invalid
						// so that, if (when) the game re-uses the Entity id, we re-use the instance
						if ( EntitiesById.TryGetValue(id, out Entity ent) && ent != null ) {
							if ( ent.Address != IntPtr.Zero ) {
								// Log($"EntityCache[{id}] Unloading from cache at old Address {Describe(ent.Address)}...");
								EntitiesByAddress.TryRemove(ent.Address, out _);
								// this will leave a record in EntitiesById[id] but the Entity instance contained there will not pass IsValid()
								ent.Address = IntPtr.Zero;
								EntityRemoved?.Invoke(null, id);
							}
						}
					}
				}

			}
			Log($"EntityCache: MainThread exiting...");
		}

	}
}
