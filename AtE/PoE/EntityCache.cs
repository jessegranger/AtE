using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static AtE.Globals;

namespace AtE {
	public static class EntityCache {

		public static bool TryGetEntity(uint id, out Entity ent) => Entities.TryGetValue(id, out ent);

		public static IEnumerable<Entity> GetEntities() => Entities.Values.Where(IsValid);

		public static EventHandler<Entity> EntityAdded;
		public static EventHandler<uint> EntityRemoved;

		private static readonly ConcurrentDictionary<uint, Entity> Entities = new ConcurrentDictionary<uint, Entity>();

		public static long LastFrameTime = 0;
		public static long LastFrameElapsed = 0;
		public static long EntsAddedLastFrame = 0;
		internal static void MainThread() {
			Log($"EntityCache: MainThread starting...");
			HashSet<IntPtr> deduper = new HashSet<IntPtr>();
			HashSet<uint> incomingIds = new HashSet<uint>();
			Stack<Offsets.EntityListNode> frontier = new Stack<Offsets.EntityListNode>();
			while( true ) {

				// exit if the main form exits
				if ( Overlay.IsClosed ) {
					break;
				}

				// make sure we dont advance any faster than the main frame rate
				Overlay.FrameLock.WaitOne();

				// make some quick notes for debugging purposes
				LastFrameTime = Time.ElapsedMilliseconds;

				// do nothing if not attached
				if ( !PoEMemory.IsAttached ) {
					continue;
				}

				// while attached, try to find all the entities and update their IsLoaded status
				deduper.Clear();
				incomingIds.Clear();
				frontier.Clear();

				var head = PoEMemory.GameRoot.InGameState.EntityListHead;
				frontier.Push(head);
				while ( frontier.Count > 0 && deduper.Count < 2000 ) {
					var node = frontier.Pop();
					IntPtr entPtr = node.Entity;
					if ( deduper.Contains(entPtr) ) {
						continue;
					}
					deduper.Add(entPtr);


					// probe the entity id before we construct a full ent
					if ( PoEMemory.TryRead(entPtr + GetOffset<Offsets.Entity>("Id"), out uint id)
						&& id > 0 && id < int.MaxValue ) { // ids greater than int.MaxValue are referred to as "server entities" in ExileApi code?
						// if it looks legit, queue it and try to follow the other links
						var ent = Entities.GetOrAdd(id, _ => {
							// Log($"EntityCache[{id}] Creating new ent {id} at {Format(entPtr)} from GetOrAdd");
							return new Entity() { Address = entPtr };
						});
						if( ent.Address != entPtr ) {
							// Log($"EntityCache[{id}] (re-using) Changing Address from {Format(ent.Address)} to {Format(entPtr)}");
							ent.Address = entPtr;
						}
						if( IsValid(ent) ) {
							incomingIds.Add(id);
						} else {
							// Log($"EntityCache[{id}] Failed to produce a valid ent, rejecting.");
							ent.Address = default;
						}
					}
					if ( PoEMemory.TryRead(node.First, out Offsets.EntityListNode first) ) {
						frontier.Push(first);
					}
					if ( PoEMemory.TryRead(node.Second, out Offsets.EntityListNode second) ) {
						frontier.Push(second);
					}
					if ( PoEMemory.TryRead(node.Third, out Offsets.EntityListNode third) ) {
						frontier.Push(third);
					}
				}
				// now, frontier is empty, and incomingIds is full
				var toRemove = Entities.Keys.Where(k => !incomingIds.Contains(k)).ToArray();
				foreach(var id in toRemove) {
					if( Entities.TryGetValue(id, out Entity ent) ) {
						if( ent.Address != IntPtr.Zero ) {
							ent.Address = IntPtr.Zero;
							EntityRemoved?.Invoke(null, id);
							// Log($"EntityCache[{id}] Unloading from cache...");
						}
					}
				}
				LastFrameElapsed = Time.ElapsedMilliseconds - LastFrameTime;
				EntsAddedLastFrame = incomingIds.Count;

			}
			Log($"EntityCache: MainThread exiting...");
		}

	}
}
