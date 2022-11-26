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

		public static int AddressCount => EntityByAddress.Count;
		private static readonly ConcurrentDictionary<IntPtr, Entity> EntityByAddress = new ConcurrentDictionary<IntPtr, Entity>();
		/// <summary>
		/// Checks if a memory address contains an Entity.
		/// </summary>
		/// <returns>true if the address is (likely) an Entity</returns>
		public static bool Probe(IntPtr addr) => IsValid(addr) &&
			PoEMemory.TryRead(addr, out Offsets.Entity ent) && Offsets.IsValid(ent);
		public static Entity Get(IntPtr addr) => Probe(addr) ? // TODO: this will cause a double read currently (so, cached but) we could re-use Probe's read to construct Entity
			EntityByAddress.GetOrAdd(addr, (a) => new Entity() { Address = a }) :
			EntityByAddress.TryRemove(addr, out _) ? null : (Entity)null;

		public static int IdCount => Entities.Count;
		private static readonly ConcurrentDictionary<uint, Entity> Entities = new ConcurrentDictionary<uint, Entity>();
		private static int idOffset = GetOffset<Offsets.Entity>("Id");

		internal static void MainThread() {
			Log($"EntityCache: MainThread starting...");
			HashSet<IntPtr> deduper = new HashSet<IntPtr>();
			HashSet<uint> incomingIds = new HashSet<uint>();
			Stack<Offsets.EntityListNode> frontier = new Stack<Offsets.EntityListNode>();
			while( true ) {

				// exit if the main form exits
				if ( Overlay.IsClosed || (Overlay.RenderForm?.IsDisposed ?? true) ) {
					break;
				}

				using ( Perf.Section("EntityThread Idle") ) {
					// do nothing if not attached
					if ( !PoEMemory.IsAttached ) {
						Thread.Sleep(3000); // wait 3 seconds
						continue; // check again
					} else {
						// use FrameLock to make sure we dont advance any faster than the main frame rate
						Overlay.FrameLock.WaitOne(1000);
						// Thread.Sleep(1); // using this instead of FrameLock will cap EntityThread at about 60fps,
						// but, it really sleeps in multiples of 15ms, so you either get 60fps, 30fps, 15fps, etc
						// and, if the main thread slows way down, this thread will not
					}
				}

				using ( Perf.Section("EntityThread") ) {
					// while attached, try to find all the entities and update their IsLoaded status
					deduper.Clear();
					incomingIds.Clear();
					frontier.Clear();

					var head = PoEMemory.GameRoot.InGameState.EntityListHead;
					frontier.Push(head);
					bool skippedOne = false;
					while ( frontier.Count > 0 && deduper.Count < 2000 ) {
						var node = frontier.Pop();
						IntPtr entPtr = node.Entity;
						if ( deduper.Contains(entPtr) ) {
							continue;
						}
						deduper.Add(entPtr);

						// probe only the entity id before we construct a full ent
						if ( skippedOne && PoEMemory.TryRead(entPtr + idOffset, out uint id)
							&& id > 0 && id < int.MaxValue ) { // ids greater than int.MaxValue are referred to as "server entities" in ExileApi code?
																								 // if it looks legit, queue it and try to follow the other links
							var ent = Entities.GetOrAdd(id, _ => {
								// Log($"EntityCache[{id}] Creating new ent {id} at {Format(entPtr)} from GetOrAdd");
								return Get(entPtr);
							});
							if ( ent != null ) {
								if ( ent.Address != entPtr ) {
									// Log($"EntityCache[{id}] (re-using) Changing Address from {Format(ent.Address)} to {Format(entPtr)}");
									EntityByAddress.TryRemove(ent.Address, out _);
									ent.Address = entPtr;
									EntityByAddress[ent.Address] = ent;
								}
								if ( IsValid(ent) ) {
									incomingIds.Add(id);
								} else {
									Log($"EntityCache[{id}] Failed to produce a valid ent, rejecting.");
									EntityByAddress.TryRemove(ent.Address, out _);
									ent.Address = default;
								}
							}
						} else {
							skippedOne = true;
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
					foreach ( var id in toRemove ) {
						// note, that we dont remove from Entities, but mark as Invalid
						// so that, if (when) the game re-uses the Entity id, we re-use the instance
						if ( Entities.TryGetValue(id, out Entity ent) ) {
							if ( ent.Address != IntPtr.Zero ) {
								EntityByAddress.TryRemove(ent.Address, out _);
								ent.Address = IntPtr.Zero;
								EntityRemoved?.Invoke(null, id);
								// Log($"EntityCache[{id}] Unloading from cache...");
							}
						}
					}
				}

			}
			Log($"EntityCache: MainThread exiting...");
		}

	}
}
