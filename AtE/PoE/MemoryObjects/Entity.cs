using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {
	public static partial class Globals {
		
		public static bool IsValid(Entity ent) => ent != null
			// && ent.ServerId > 0 && ent.ServerId < int.MaxValue
			&& (ent.Path?.StartsWith("Metadata") ?? false);

		public static Entity GetEntityById(ushort id) => PoEMemory.GameRoot?.InGameState.GetEntityById(id);

		/// <summary>
		/// Helper to get the Player from the GameRoot.
		/// </summary>
		/// <returns>The current Player Entity.</returns>
		public static PlayerEntity GetPlayer() => PoEMemory.GameRoot?.InGameState?.Player;

		/// <summary>
		/// Helper to get the full Entity list from the InGameState (safely cached) from anywhere.
		/// The result is only considered valid for this frame.
		/// </summary>
		/// <returns>An enumerable over the current entity list.</returns>
		public static IEnumerable<Entity> GetEntities() => PoEMemory.GameRoot?.InGameState?.Entities;

		public static void DrawTextAt(Entity ent, string text, Color color) {
			var camera = PoEMemory.GameRoot?.InGameState?.WorldData?.Camera ?? default;
			var pos = ent.GetComponent<Render>().Position;
			DrawTextAt(ent.Id, camera.WorldToScreen(pos), text, color);
		}

	}
	public class Entity : MemoryObject<Offsets.Entity> {
		public Cached<Offsets.EntityDetails> Details;
		public Entity() : base() =>
			Details = CachedStruct<Offsets.EntityDetails>(() => Cache.ptrDetails);

		/// <summary>
		///  The remote id used by the PoE server to sync the ent.
		///  Client-only effects do have Entity structure, but dont have Id.
		/// </summary>
		public uint Id => Cache.Id;

		public string Path => PoEMemory.TryReadString(Details.Value.ptrPath, Encoding.Unicode, out string ret) ? ret : null;

		public bool HasComponent<T>() where T : MemoryObject, new() {
			if ( Components == null ) Components = GetComponents();
			return Components?.ContainsKey(typeof(T).Name) ?? false;
		}

		public T GetComponent<T>() where T : MemoryObject, new() {
			if ( Components == null ) Components = GetComponents();
			if ( Components == null ) return null;
			if ( Components.TryGetValue(typeof(T).Name, out IntPtr ret) ) {
				return new T() { Address = ret };
			}
			return null;
		}

		private Dictionary<string, IntPtr> Components;

		public Dictionary<string, IntPtr> GetComponents() {

			// Build a map of Component name to address
			var result = new Dictionary<string, IntPtr>();
			// the entity has a list of ptr to Component
			// managed by an ArrayHandle at ComponentsArray
			var entityComponents = new ArrayHandle<IntPtr>(Cache.ComponentsArray).ToArray();
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
		public Vector3 Position => (IsValid(render) ? render : render = GetComponent<Render>())?.Position ?? Vector3.Zero; 
		public Vector3 Bounds => (IsValid(render) ? render : render = GetComponent<Render>())?.Bounds ?? Vector3.Zero; 
	}


}
