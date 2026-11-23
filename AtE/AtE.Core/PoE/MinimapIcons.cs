using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// One icon of the area's minimap icon list (see Offsets.MinimapIconList): known for the whole area, also for entities far
	/// outside the entity list's range, eg the yellow quest markers of the quest chests on the large map.
	/// </summary>
	public class MinimapIconMarker {
		public IntPtr Address;
		public string Icon; // the MinimapIcons.dat Id, eg "QuestObject", "QuestItem", "Entrance", "NPC"
		public int IconIndex;
		public Vector2 GridPos;
		public uint EntityId;
		/// <summary>Is the icon drawn on the map (eg, not once its quest chest is opened)? See Offsets.MinimapIconRecord.IsVisible.</summary>
		public bool IsVisible;

		/// <summary>The entity, while it is in range (in the entity list), else null.</summary>
		public Entity Entity => EntityCache.TryGetEntity(EntityId, out Entity ent) ? ent : null;

		public bool IsQuestObject => Icon == "QuestObject";

		public override string ToString() => $"{Icon} <{GridPos.X},{GridPos.Y}> id {EntityId}{(IsVisible ? "" : " (hidden)")}";
	}

	public static class MinimapIcons {

		private static readonly Dictionary<IntPtr, string> iconNames = new Dictionary<IntPtr, string>();

		/// <summary>The MinimapIcons.dat Id of a row (eg "QuestObject"), or null.</summary>
		private static string IconName(IntPtr row) {
			if ( iconNames.TryGetValue(row, out string name) ) return name;
			name = PoEMemory.TryRead(row, out IntPtr strId) && PoEMemory.TryReadString(strId, Encoding.Unicode, out string id, 128) ? id : null;
			if ( name != null ) iconNames[row] = name;
			return name;
		}

		/// <summary>Every minimap icon of the current area, or empty when the list does not read.</summary>
		public static List<MinimapIconMarker> All() {
			var result = new List<MinimapIconMarker>();
			var ptr = PoEMemory.GameRoot?.InGameState?.Data.Value.ptrMinimapIcons ?? IntPtr.Zero;
			if ( !Offsets.IsValid(ptr) || !PoEMemory.TryRead(ptr, out Offsets.MinimapIconList list) ) return result;
			var handle = list.Icons;
			const int size = Offsets.MinimapIconRecord.Size;
			if ( !Offsets.IsValid(handle.Head) || (handle.Tail.ToInt64() - handle.Head.ToInt64()) % size != 0 ) return result;
			int count = handle.ItemCount(size);
			if ( count < 1 || count > 2000 ) return result;
			var records = new Offsets.MinimapIconRecord[count];
			if ( PoEMemory.TryRead(handle.Head, records) != count * size ) return result;
			for ( int i = 0; i < count; i++ ) {
				var r = records[i];
				result.Add(new MinimapIconMarker() {
					Address = handle.GetRecordPtr(i, size),
					Icon = IconName(r.ptrIconRow),
					IconIndex = r.IconIndex,
					GridPos = new Vector2(r.GridPos.X, r.GridPos.Y),
					EntityId = r.EntityId,
					IsVisible = r.IsVisible,
				});
			}
			return result;
		}

		/// <summary>The quest markers (QuestObject icons, eg quest chests) of the current area, near or far, that are drawn on the map (not an opened chest's).</summary>
		public static IEnumerable<MinimapIconMarker> QuestObjects() => All().Where(m => m.IsQuestObject && m.IsVisible);
	}
}
