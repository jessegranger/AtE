using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The routine's status as data, for the memory server's HTML status page (StatusReport is the same as text).
	/// </summary>
	public partial class MappingRoutine {

		/// <summary>Why the loop last stopped (a step's Fail), until the routine is switched on again; null if it has not.</summary>
		public string LastFailure { get; private set; }

		public enum RunState { Off, Running, Paused, Stopped, Dead }
		public enum AreaKind { Hideout, Map, Town, Elsewhere, Unknown }

		public class StatusView {
			public bool Attached, Chosen, GameReadable, GamePaused, GameFocused, PausedForFocus, AuguryOpen;
			public RunState State;
			public string Status, Failure;
			public bool HasCharacter;
			public string CharName;
			public int Level, Life, MaxLife, ES, MaxES, Mana, MaxMana, ReservedMana;
			public double XpPercent = double.NaN;
			public string AreaName;
			public AreaKind Area;
			public int? MapTier;
			public int? MonstersRemaining;
			public int MonstersLeft, UniquesKilled;
			public bool MapComplete;
			public int MapsCompleted, MapsRun;
			public string NextMap;
			public List<string> Steps = new List<string>();
			public List<string> Skipping = new List<string>();
		}

		/// <summary>The status now (main thread). chosen: the routine is the one Combat runs.</summary>
		public StatusView GetStatusView(bool chosen) {
			var v = new StatusView() { Chosen = chosen, Attached = PoEMemory.IsAttached };
			var game = PoEMemory.GameRoot?.InGameState;
			v.GameReadable = game != null;
			v.GamePaused = game?.IsPaused ?? false;
			v.GameFocused = PoEMemory.TargetHasFocus;
			v.PausedForFocus = GamePausedForFocus;
			v.AuguryOpen = v.GameReadable && AuguryIsOpen();
			v.Status = Status;
			v.Failure = LastFailure;
			var player = GetPlayer();
			var p = player?.GetComponent<Player>();
			var life = player?.GetComponent<Life>();
			if ( IsValid(p) ) {
				v.HasCharacter = true;
				v.CharName = p.Name;
				v.Level = p.Level;
				v.XpPercent = Experience.PercentOfLevel(p.Level, p.XP);
			}
			if ( IsValid(life) ) {
				v.Life = life.CurHP; v.MaxLife = life.MaxHP;
				v.ES = life.CurES; v.MaxES = life.MaxES;
				v.Mana = life.CurMana; v.MaxMana = life.MaxMana; v.ReservedMana = life.TotalReservedMana;
			}
			bool dead = IsValid(player) && !IsAlive(player);
			v.State = dead ? RunState.Dead : IsActive ? (Paused ? RunState.Paused : RunState.Running) : LastFailure != null ? RunState.Stopped : RunState.Off;
			var area = GetCurrentArea();
			v.AreaName = area?.Name;
			v.Area = area == null ? AreaKind.Unknown : InHideout() ? AreaKind.Hideout : InMap() ? AreaKind.Map : area.IsTown ? AreaKind.Town : AreaKind.Elsewhere;
			if ( v.Area == AreaKind.Map ) {
				if ( area.Name == MapArea ) v.MapTier = MapTier;
				v.MonstersRemaining = MonstersRemaining;
				v.MapComplete = MapComplete;
				v.UniquesKilled = UniquesKilled.Count;
			}
			v.MonstersLeft = MonstersLeft;
			v.MapsCompleted = MapsCompleted;
			v.MapsRun = MapsRun;
			v.NextMap = NextMap is { } n ? $"{n.area} (in the backpack)" : $"{MapArea} T{MapTier}";
			var s = mapLoop?.Current;
			for ( int i = 0; s != null && i < 12; i++, s = s.Next ) v.Steps.Add(s.ToString());
			if ( s != null ) v.Steps.Add("...");
			if ( s == null && mapLoop?.Current == null && CurrentGoal != null && IsActive ) v.Steps.Add((Working(CurrentGoal) ?? CurrentGoal).ToString());
			if ( SkipToClear ) v.Skipping.Add("switched on in a map: skipping to Clear Area");
			if ( SkipToPortals ) v.Skipping.Add("map portals up: skipping to Enter the map");
			return v;
		}
	}
}
