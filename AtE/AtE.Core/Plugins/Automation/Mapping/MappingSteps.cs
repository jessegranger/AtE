using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AtE.Plugins;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The hideout steps of the Mapping loop as state chains, each ending in next (stitched together in MappingLoop.cs):
	///   StashEverything  walk to the stash, open it, show the DumpTab, BackpackPlugin.PlanDumpAll
	///   PickNextMap      (stash open) show the MapsTab, ActivateMapTier(MapTier), Ctrl + click a map into the backpack
	///   RollMap          (stash open, map in the backpack) Scouring / Alchemy from the CurrencyTab (stash and inventory open, one map) until it is
	///                    a rare with none of ProhibitedMods
	///   SelectMapArea    with the Map Device open: drag the Atlas to MapArea's area node and click it (shows its Map Loader)
	///   OpenMap          close the stash, walk to the Map Device, open it, SelectMapArea, put the map in, Activate, wait for a
	///                    new portal, then EnterMap (MappingLoop.cs: walk to a fresh portal, click it, WaitForNewArea)
	/// StashEverything, PickNextMap, RollMap and OpenMap skip themselves (go on to next) while SkipToClear (the routine was
	/// switched on in a map, and its loop has not got to ClearArea yet).
	/// Each step decides what to do when it is reached, not when it is built (the backpack and stash change on the way), and
	/// stops the chain (null) with a log line when it can't go on. The steps of a plan are built up front, each with its
	/// Next set to the step it goes on to, so the whole plan can be looked at (eg, in an ObjectBrowser) before it runs;
	/// what a step runs in between (a walk, a click, a wait) is made when it runs.
	///
	/// They walk (Navigate), so they run in the routine's CampaignRunner (eg, Call()ed from a goal), not in a plain Machine.
	/// Waits longer than GoalRoutine.MaxInputMs are CampaignStates (see Step), which the routine does not cut short.
	/// Entering a new area restarts the routine's goal (GoalRoutine.ForgetOnNewArea cancels the Runner's chain): the loop's
	/// chain is kept by MapLoop, which the Runner only ticks, so it goes on through portals (see MappingLoop.cs).
	/// </summary>
	public partial class MappingRoutine {

		/// <summary>The stash tab everything is dumped in.</summary>
		public string DumpTab = "dump";
		/// <summary>The stash tab the maps are taken from (a Map stash tab, see ActivateMapTier).</summary>
		public string MapsTab = "maps";
		/// <summary>The stash tab with the currency (Orb of Scouring, Orb of Alchemy).</summary>
		public string CurrencyTab = "$$";
		/// <summary>The tier of map to take from the MapsTab.</summary>
		public int MapTier = 1;
		/// <summary>Map mods not to run, separated by commas: parts of a mod's name (Mods.dat id, eg "Reflect") or display name.</summary>
		public string ProhibitedMods = "";
		/// <summary>RollMap gives up after using this many currency items on one map.</summary>
		public int MaxRolls = 20;
		/// <summary>The area to run, by its name on the Atlas (eg "Silo"): SelectMapArea clicks its area node.</summary>
		public string MapArea = "Silo";
		/// <summary>How near (cells) to walk to the stash, the Map Device and a portal before clicking it.</summary>
		public float ClickReach = 12;

		// ---- the steps ----

		/// <summary>In the hideout: walk to the stash, click it (its label), show the DumpTab, then dump the backpack (BackpackPlugin.PlanDumpAll: identify, stash all but what it keeps), then next. Leaves the stash open.</summary>
		public IState StashEverything(IState next) {
			var dump = new Step("Dump the backpack", (s, ctx, dt) => {
				if ( GetPlugin<BackpackPlugin>() is not BackpackPlugin backpack ) return Fail("no BackpackPlugin to dump with");
				// (one map of MapTier in the backpack already: kept, to run next, see PickNextMap)
				// (by its address: an item in an inventory has no entity id, it reads 0)
				var keep = HeldMap()?.Entity?.Address ?? IntPtr.Zero;
				if ( keep != IntPtr.Zero ) Log($"{Name}: keeping the tier {MapTier} map in the backpack, to run next");
				return backpack.PlanDumpAll(CheckStashed(backpack, next), e => keep != IntPtr.Zero && e.Address == keep);
			}, next);
			var tab = Tab(DumpTab, dump);
			var open = ClickToOpen("the stash", FindStash, StashIsOpen, tab);
			var walk = WalkTo("the stash", FindStash, open, skipTo: StashIsOpen);
			// (the stash open already, wherever: no walking, no clicking its label)
			return ForceGameFocus(new Step("Stash everything", (s, ctx, dt) =>
				SkipToClear || SkipToPortals ? next
				: StashIsOpen() ? tab
				: !InHideout() ? Fail("stash everything: not in the hideout")
				: walk, walk));
		}

		/// <summary>How long after the dump the items it was to stash may take to leave the backpack, before it counts as failed.</summary>
		private const long StashCheckMs = 2000;

		// after the dump: every item it was to stash (BackpackPlugin.LastStashPlanned) gone from the backpack, then next; any
		// still there after StashCheckMs (eg, the stash tab is full, or the stash closed part way): stop (null)
		private IState CheckStashed(BackpackPlugin backpack, IState next) {
			// (read when it runs: this is made before the dump's plan, which fills LastStashPlanned)
			HashSet<IntPtr> planned = null;
			return new Step("Check the backpack was stashed", (s, ctx, dt) => {
				planned ??= backpack.LastStashPlanned.ToHashSet();
				var left = BackpackItems().Where(i => i?.Entity is Entity e && planned.Contains(e.Address)).ToList();
				if ( left.Count == 0 ) return s.Pass();
				if ( s.Elapsed < StashCheckMs ) { s.Detail = $"{left.Count} of {planned.Count} items still in the backpack"; return s; }
				string names = string.Join(", ", left.Take(5).Select(i => Equipment.ItemName(i.Entity) ?? i.Entity.Path?.Split('/').LastOrDefault() ?? "?"));
				return Fail($"stash everything: {left.Count} of {planned.Count} items were not stashed ({names}{(left.Count > 5 ? ", ..." : "")}): is the \"{DumpTab}\" tab full?");
			}, next);
		}

		/// <summary>With the stash open: show the MapsTab, ActivateMapTier(MapTier), Ctrl + click a map of that tier into the backpack (it becomes NextMap), then next.</summary>
		public IState PickNextMap(IState next) {
			var tab = Tab(MapsTab, ActivateMapTier(MapTier, TakeMap(next)));
			return ForceGameFocus(new Step("Pick the next map", (s, ctx, dt) => {
				if ( SkipToClear || SkipToPortals ) return next;
				// (a map of MapTier in the backpack already, kept by StashEverything: that one; RollMap checks its mods)
				if ( HeldMap()?.Entity is Entity held ) {
					NextMap = (held.Id, ChooseNextMap.MapArea(held));
					Log($"{Name}: the next map is the {Equipment.ItemName(held)} (tier {MapTier}) in the backpack");
					return next;
				}
				return !StashIsOpen() ? Fail("pick the next map: the stash is not open") : tab;
			}, tab));
		}

		// a map of MapTier in the backpack (NextMap if it is one), or null
		private InventoryItem HeldMap() {
			var maps = BackpackItems().Where(i => ChooseNextMap.IsMap(i?.Entity) && TierOf(i.Entity) == MapTier && !IsUniqueMap(i.Entity)).ToList();
			return (NextMap is { } n ? maps.FirstOrDefault(i => i.Entity.Id == n.id) : null) ?? maps.FirstOrDefault();
		}

		/// <summary>How long ActivateMapTier waits for the tier buttons to show (the page is built after its tab is first shown).</summary>
		public long TierButtonsWaitMs = 2000;

		/// <summary>
		/// With the stash open on the MapsTab (a Map stash tab): show the maps of tier, then next. Nothing to do if the maps
		/// showing are that tier's already. Stops if the tab has no tier buttons, or no maps of that tier.
		///
		/// The Map stash tab's page (StashRoot.VisibleTabPage), 3.30.0d, seen live:
		///   .0.0 and .0.1  two rows of 9 tier buttons (73.5 px squares, left to right): button i (in reading order) is tier
		///                  i + 1, and its child [0]'s text is how many maps of that tier the tab holds ("17", "10", "8", "0"...)
		///   .0.2           an empty strip (0 high)
		///   .0.3           a bar of 6 numbered buttons "1".."6" (its last child is the one selected, like the stash's tab bar), and their pages
		///   .0.4.N         the grids of maps (12 x 6 cells of 53.25 px): the visible one holds the selected tier's maps as
		///                  items, eg Metadata/Items/Maps/MapKeyTier3, named "Map (Tier 3)"
		///   .0.5           two small arrow buttons (hidden)
		/// No button shows which tier is selected; the maps showing do (their path's MapKeyTierN). The buttons are found by
		/// their shape (a row of 9 children, each with a number for its text), not by index, in case the page moves.
		/// </summary>
		public IState ActivateMapTier(int tier, IState next) => ForceGameFocus(new Step($"Show tier {tier} maps", (s, ctx, dt) => {
			if ( !StashIsOpen() ) return Fail($"show tier {tier} maps: the stash is not open");
			if ( ShowsTier(tier) ) return next;
			var page = GetUI()?.StashElement?.VisibleTabPage;
			var buttons = TierButtons(page);
			if ( buttons.Count == 0 ) {
				// (the page may not be built yet, the first frames after its tab is shown: wait for the buttons a while)
				if ( s.Elapsed < TierButtonsWaitMs ) { s.Detail = "waiting for the tier buttons"; return s; }
				var top = page?.GetChild(0);
				string saw = page == null ? "no visible page" : $"visible page {page.Address:X} ({page.Children.Count()} children), its first child "
					+ (top == null ? "missing" : $"{top.Address:X} with {top.Children.Count()} children: rows of {string.Join(", ", top.Children.Select(r => r?.Children.Count() ?? 0))}");
				Log($"{Name}: show tier {tier} maps: no tier buttons after {s.Elapsed} ms; saw {saw}");
				return Fail($"show tier {tier} maps: the \"{MapsTab}\" tab has no tier buttons (is it a Map stash tab?)");
			}
			if ( tier < 1 || tier > buttons.Count ) return Fail($"show tier {tier} maps: there are {buttons.Count} tier buttons");
			var button = buttons[tier - 1];
			if ( int.TryParse(button.GetChild(0)?.Text?.Trim(), out int count) && count == 0 ) return Fail($"no tier {tier} maps on the \"{MapsTab}\" tab");
			s.Detail = $"clicking tier {tier} ({count} maps)";
			// (after the click: any visible grid of that tier's maps will do, see GridShowsTier; and if none shows, the log
			// says what grids the code saw, see MapGridsSeen)
			return Inputs.Click(button.GetClientRect(), new Step($"Wait for tier {tier} maps to show", (s2, c2, d2) => {
				if ( GridShowsTier(tier) ) return next;
				if ( s2.Elapsed <= 2000 ) return s2;
				Log($"{Name}: show tier {tier} maps: after clicking its button, the maps seen: {MapGridsSeen()}");
				return Fail($"tier {tier} maps did not show after clicking its button");
			}, next));
		}, next));

		/// <summary>
		/// With the stash open: show the CurrencyTab, right click the currency named name (its item name, eg "Orb of Scouring"),
		/// left click target (where it is when clicked), then next.
		/// A right click puts the game in "currency use mode" (the currency stays in the stash; its icon is drawn at the mouse,
		/// UIElementLibrary.MouseIconSprite). After the right click it confirms that name's icon is at the mouse (looking every
		/// CursorLookMs, for CursorWaitMs at most); if no icon shows, it right clicks again (PickUpTries times at most); if another
		/// currency's icon shows, or an item is held on the cursor, it stops (null). It never right clicks while name is already
		/// in use (a second right click turns use mode off).
		/// </summary>
		public IState UseCurrencyFromStash(string name, InventoryItem target, IState next) {
			var tab = Tab(CurrencyTab, PickUpCurrency(name, FromStash(name), target, 1, next));
			return ForceGameFocus(new Step($"Use {name}", (s, ctx, dt) => !StashIsOpen() ? Fail($"use {name}: the stash is not open") : tab, tab));
		}

		/// <summary>
		/// With the inventory open: right click the currency at path in the backpack (eg, a Scroll of Wisdom), confirm it is
		/// in use, left click target, then next; checked as UseCurrencyFromStash is (the same retries and stops).
		/// </summary>
		public IState UseCurrencyFromBackpack(string name, string path, InventoryItem target, IState next) {
			var use = PickUpCurrency(name, FromBackpack(name, path), target, 1, next);
			return ForceGameFocus(new Step($"Use {name}", (s, ctx, dt) => !BackpackIsOpen() ? Fail($"use {name}: the inventory is not open") : use, use));
		}

		// where PickUpCurrency finds the currency: (the item, or null with why it isn't there)
		private Func<(InventoryItem item, string missing)> FromStash(string name) => () => {
			if ( !StashIsOpen() ) return (null, $"use {name}: the stash is not open");
			var item = StashItems().FirstOrDefault(i => i?.Entity is Entity e && IsNamed(Equipment.ItemName(e), name));
			return item == null ? (null, $"no {name} on the \"{CurrencyTab}\" tab") : (item, null);
		};
		private static Func<(InventoryItem item, string missing)> FromBackpack(string name, string path) => () => {
			var item = BackpackItems().FirstOrDefault(i => i?.Entity?.Path == path);
			return item == null ? (null, $"no {name} in the backpack") : (item, null);
		};

		private const int PickUpTries = 5;
		private const long CursorLookMs = 1000, CursorWaitMs = 3000;

		/// <summary>The name of the item held on the mouse cursor, "" if nothing is held, or null if it can't be read.</summary>
		private static string CursorItemName() {
			var igs = PoEMemory.GameRoot?.InGameState;
			if ( igs == null ) return null;
			int count = igs.ServerInventoryCount(Offsets.InventorySlot.Cursor);
			if ( count < 0 ) return null;
			if ( count == 0 ) return "";
			var ent = igs.CursorItem;
			return IsValid(ent) ? Equipment.ItemName(new Entity() { Address = ent }) ?? "?" : "?";
		}

		/// <summary>The file name (without folder or extension) of the icon at the mouse, eg "CurrencyUpgradeToRare" in Orb of Alchemy
		/// use mode; "" if there is none; null if not readable.</summary>
		private static string MouseIconName() {
			string sprite = GetUI()?.MouseIconSprite;
			if ( sprite == null || sprite == "" ) return sprite;
			string file = sprite.Split('|')[0];
			file = file.Substring(file.LastIndexOf('/') + 1);
			int dot = file.LastIndexOf('.');
			return dot > 0 ? file.Substring(0, dot) : file;
		}

		/// <summary>The last part of an item's path, eg "CurrencyUpgradeToRare": the name of its icon too.</summary>
		private static string PathName(Entity e) {
			string path = e?.Path ?? "";
			return path.Substring(path.LastIndexOf('/') + 1);
		}

		private static bool IsNamed(string itemName, string name) => string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase);

		/// <summary>Right click the currency named name in the stash, confirm it is in use, then left click target.</summary>
		private IState PickUpCurrency(string name, Func<(InventoryItem item, string missing)> find, InventoryItem target, int attempt, IState next) =>
			new Step($"Right click {name}", (s, ctx, dt) => {
				if ( !BackpackIsOpen() ) return Fail($"use {name}: the inventory is not open");
				if ( !IsValid(target) ) return Fail($"use {name}: the item to use it on is gone");
				var (currency, missing) = find();
				if ( currency == null ) return Fail(missing);
				string icon = PathName(currency.Entity);
				string held = CursorItemName(), inUse = MouseIconName();
				if ( held == null || inUse == null ) return Fail($"use {name}: can't read the cursor");
				if ( held != "" ) return Fail($"use {name}: {held} is held on the cursor");
				if ( inUse != "" ) return IsNamed(inUse, icon)
					? ClickTarget(name, target, next) // already in use: a right click would turn it off
					: Fail($"use {name}: another currency is in use ({inUse})");
				var rect = currency.GetClientRect();
				Log($"{Name}: right clicking {name} at {rect.X + rect.Width / 2:F0},{rect.Y + rect.Height / 2:F0} (try {attempt} of {PickUpTries})");
				return Inputs.RightClick(rect, new Delay(Inputs.Latency, ConfirmInUse(name, find, icon, target, attempt, next)));
			}, next);

		/// <summary>Look at the mouse now and every CursorLookMs: name's icon there goes on to the left click; no icon for
		/// CursorWaitMs goes back to the right click; anything else stops.</summary>
		private IState ConfirmInUse(string name, Func<(InventoryItem item, string missing)> find, string icon, InventoryItem target, int attempt, IState next) {
			long lookAt = 0;
			return new Step($"Confirm {name} is in use", (s, ctx, dt) => {
				if ( s.Elapsed < lookAt ) return s;
				lookAt += CursorLookMs;
				string held = CursorItemName(), inUse = MouseIconName();
				if ( held == null || inUse == null ) return Fail($"use {name}: can't read the cursor");
				if ( held != "" ) return Fail($"use {name}: {held} is held on the cursor");
				if ( inUse != "" ) {
					if ( !IsNamed(inUse, icon) ) return Fail($"use {name}: another currency is in use ({inUse})");
					Log($"{Name}: {name} is in use ({s.Elapsed} ms after the right click)");
					return ClickTarget(name, target, next);
				}
				if ( s.Elapsed < CursorWaitMs ) return s;
				if ( attempt >= PickUpTries ) return Fail($"use {name}: still not in use after {PickUpTries} right clicks");
				Log($"{Name}: {name} not in use {CursorWaitMs} ms after right clicking it, trying again");
				return PickUpCurrency(name, find, target, attempt + 1, next);
			}, next);
		}

		private IState ClickTarget(string name, InventoryItem target, IState next) {
			if ( !IsValid(target) ) return Fail($"use {name}: the item to use it on is gone");
			// (InputLatency between every click: after the right click's confirm, before this one, and after it)
			return new Delay(Inputs.Latency, Inputs.Click(target.GetClientRect(), new Delay(Inputs.Latency, new Delay(InventoryPlans.Settle, next))));
		}

		/// <summary>
		/// With the stash and the inventory open, and one map in the backpack (normal, magic or rare): make it a rare with
		/// none of ProhibitedMods, with Orbs of Scouring and Alchemy right clicked in the stash window (the CurrencyTab) and
		/// used on the map: Alchemy on a normal map; Scouring on a magic one or a rare with a prohibited mod (then Alchemy
		/// again). An unidentified magic or rare map is identified first (Scouring does not work on it), with a Scroll of
		/// Wisdom from the backpack (UseCurrencyFromBackpack; not counted against MaxRolls). Then next. Stops (null) at once if the stash or the inventory
		/// isn't open (at any roll), if there isn't exactly one map in the backpack at the start, on a unique map, or after
		/// MaxRolls. A corrupted map (which can't be rolled) is done as it is, whatever its rarity, if it has none of
		/// ProhibitedMods (identified first if need be), and stops it if it has one.
		/// </summary>
		public IState RollMap(IState next) {
			var roll = RollStep(0, next);
			return ForceGameFocus(new Step("Roll the map", (s, ctx, dt) => {
				if ( SkipToClear || SkipToPortals ) return next;
				if ( !StashIsOpen() ) return Fail("roll the map: the stash is not open");
				if ( !BackpackIsOpen() ) return Fail("roll the map: the inventory is not open");
				cursorMapRecoveries = 0;
				// (a map on the cursor counts: it goes back in the backpack at the first roll)
				int maps = BackpackItems().Count(i => ChooseNextMap.IsMap(i?.Entity)) + (CursorHoldsMap() ? 1 : 0);
				if ( maps != 1 ) return Fail($"roll the map: there should be one map in the backpack, there are {maps}");
				return roll;
			}, roll));
		}

		// (each roll's UseCurrencyFromStash is made when it runs: which currency depends on the map)
		private IState RollStep(int used, IState next) => new Step($"Roll {used + 1}", (s, ctx, dt) => {
			if ( !StashIsOpen() ) return Fail("roll the map: the stash is not open");
			if ( !BackpackIsOpen() ) return Fail("roll the map: the inventory is not open");
			// a map left on the cursor (eg, an extra click picked it up): back in the backpack first
			if ( CursorHoldsMap() ) return PutCursorMapDown(RollStep(used, next));
			var map = BackpackMap();
			var e = map?.Entity;
			var mods = e?.GetComponent<Mods>();
			if ( e == null || !IsValid(mods) ) return Fail("roll the map: no map in the backpack");
			string name = Equipment.ItemName(e) ?? "the map";
			if ( mods.Rarity == Offsets.ItemRarity.Unique ) return Fail($"roll the map: {name} is unique");
			// an unidentified magic or rare map: Scouring does not work on it, so identify it first (a Scroll of Wisdom from
			// the backpack, checked as the currency is: in use before the map is clicked), then look again
			if ( mods.Rarity != Offsets.ItemRarity.Normal && !mods.IsIdentified ) {
				// (read again a moment later: just taken from the stash, an identified map once read as unidentified, and the
				// Scroll of Wisdom was refused, "already identified")
				if ( s.Elapsed < UnidentifiedSureMs ) { s.Detail = "looking again: unidentified?"; return s; }
				Log($"{Name}: roll {used + 1}: {name} is unidentified: identifying it");
				s.Detail = "identifying";
				return UseCurrencyFromBackpack("Scroll of Wisdom", Offsets.PATH_SCROLL_WISDOM, map,
					WaitUntil($"{name} to be identified", 2000,
						() => CursorItemName() == "" && BackpackMap()?.Entity?.GetComponent<Mods>() is Mods m && IsValid(m) && m.IsIdentified,
						RollStep(used, next), $"{name} was not identified", () => RollStep(used, next)));
			}
			// a corrupted map can't be rolled: it will do as it is (whatever its rarity) if it has none of ProhibitedMods
			if ( IsCorrupted(e) ) {
				var badMods = mods.ExplicitMods.Where(IsProhibited).Select(m => m.DisplayName ?? m.GroupName).ToList();
				if ( badMods.Count > 0 ) return Fail($"roll the map: {name} is corrupted, with prohibited mods: {string.Join(", ", badMods)}");
				Log($"{Name}: {name} is corrupted ({mods.Rarity}) with no prohibited mods: it will do ({used} currency used)");
				return next;
			}
			string why = null;
			if ( mods.Rarity == Offsets.ItemRarity.Rare ) {
				var bad = mods.ExplicitMods.Where(IsProhibited).Select(m => m.DisplayName ?? m.GroupName).ToList();
				if ( bad.Count == 0 ) {
					Log($"{Name}: {name} is a rare with no prohibited mods ({used} currency used): {string.Join(", ", mods.ExplicitMods.Select(m => m.DisplayName ?? m.GroupName))}");
					return next;
				}
				why = $"prohibited: {string.Join(", ", bad)}";
			}
			if ( used >= MaxRolls ) return Fail($"roll the map: gave up on {name} after {used} currency");
			string currency = mods.Rarity == Offsets.ItemRarity.Normal ? "Orb of Alchemy" : "Orb of Scouring";
			why ??= $"{mods.Rarity}".ToLowerInvariant();
			Log($"{Name}: roll {used + 1}: {name} is {why}: {currency}");
			s.Detail = $"{why}: {currency}";
			// changed: the map back in the backpack, nothing on the cursor, its Mods read (a read that fails for a frame while
			// the item updates is not a change; item entities' Ids are all 0, so no use either), and the rarity the currency
			// gives: not normal after an Alchemy, normal after a Scouring
			string before = MapSignature(e);
			bool alchemy = mods.Rarity == Offsets.ItemRarity.Normal;
			return UseCurrencyFromStash(currency, map,
				WaitUntil($"{name} to change ({currency})", 2000,
					() => CursorItemName() == "" && BackpackMap()?.Entity is Entity now && now.GetComponent<Mods>() is Mods m && IsValid(m)
						&& MapSignature(now) is string after && after != before
						&& (alchemy ? m.Rarity != Offsets.ItemRarity.Normal : m.Rarity == Offsets.ItemRarity.Normal),
					RollStep(used + 1, next), $"{name} did not change after the {currency}", () => RollStep(used + 1, next)));
		}, next);

		/// <summary>
		/// Close the stash (and the inventory with it) if it is open, walk to the Map Device, click it (its label), show the
		/// Map Loader for MapArea (SelectMapArea), put the map (NextMap, else any) in its slot unless one is there already,
		/// click Activate, wait for a new map portal, then EnterMap (walk to a fresh portal, click it, WaitForNewArea), then
		/// next (in the map). Stops (null) if there is no map in the backpack nor in the Map Loader's slot. Enter Map has no
		/// test of its own: it takes a portal opened by this Activate (OpenMap.FreshPortals).
		/// </summary>
		public IState OpenMap(IState next) {
			var enter = EnterMap(next);
			var atDevice = AtTheMapDevice(enter);
			Func<Entity> device = () => CampaignState.Context is CombatContext c ? AtE.OpenMap.FindDevice(c) : null;
			var walk = WalkTo("the Map Device", device, ClickToOpen("the Map Device", device, MapDeviceIsOpen, atDevice), skipTo: MapDeviceIsOpen);
			return ForceGameFocus(new Step("Open the map", (s, ctx, dt) =>
				SkipToClear ? next
				: SkipToPortals ? (StashIsOpen() ? CloseStash(s) : enter)
				: MapDeviceIsOpen() ? atDevice
				: BackpackMap() == null && GetUI()?.MapDevice?.LoadedMap == null ? Fail("open the map: no map in the backpack (nor in the Map Device)")
				: StashIsOpen() ? CloseStash(s)
				: walk, walk));
		}

		/// <summary>Press Escape once (it closes the stash and the inventory together), and again if that opened the escape menu, then next.</summary>
		private static IState CloseStash(IState next) =>
			CloseWindow("the stash", new Step("Check the escape menu", (s, ctx, dt) =>
				GetUI()?.EscapeMenu?.IsVisible ?? false ? CloseWindow("the escape menu", next) : next, next));

		/// <summary>
		/// With the Map Device open: show the MapLoader for MapArea, then next. If the MapLoader is showing another area, close
		/// it (Escape) first. If it isn't showing, drag the Atlas (from an empty spot near the middle) until MapArea's area node is
		/// near the middle of the screen, then click the node. Never drags while the MapLoader is showing. Stops (null) if the
		/// Map Device isn't open, there is no node for MapArea, after MaxAtlasDrags drags, or if the click doesn't show it.
		/// </summary>
		public IState SelectMapArea(IState next) => ForceGameFocus(SelectAreaStep(MapArea, 0, next));

		private const int MaxAtlasDrags = 15;
		// how near (as a part of the screen's height) the node must be to the middle to be clicked
		private const float AtlasCenteredPart = 0.12f;

		private IState SelectAreaStep(string area, int drags, IState next) => new Step($"Select {area} on the Atlas", (s, ctx, dt) => {
			var device = GetUI()?.MapDevice;
			if ( !MapDeviceIsOpen() || device == null ) return Fail($"select {area}: the Map Device is not open");
			if ( device.MapLoaderIsOpen ) {
				string title = device.MapLoaderTitle;
				if ( string.Equals(title, area, StringComparison.OrdinalIgnoreCase) ) { Log($"{Name}: the Map Loader shows {area}"); return next; }
				Log($"{Name}: the Map Loader shows {title}, not {area}: closing it");
				Inputs.LastEscapeMs = Time.ElapsedMilliseconds;
				return Inputs.Press(VKey.Escape, WaitUntil("the Map Loader to close", 2000, () => !(GetUI()?.MapDevice?.MapLoaderIsOpen ?? false),
					SelectAreaStep(area, drags, next), "the Map Loader did not close"));
			}
			var node = device.FindAreaNode(area);
			if ( node == null ) return Fail($"select {area}: no area node named \"{area}\" on the Atlas");
			var screen = device.GetClientRect();
			var center = new Vector2(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
			var r = node.GetClientRect();
			var at = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
			if ( Vector2.Distance(at, center) <= screen.Height * AtlasCenteredPart ) {
				Log($"{Name}: clicking the {area} area node at {at.X:F0},{at.Y:F0}");
				return Inputs.MoveTo(at, new Delay(150, Inputs.Click(at, WaitUntil($"the Map Loader for {area}", 2000,
					() => GetUI()?.MapDevice is MapDevice d && d.MapLoaderIsOpen && string.Equals(d.MapLoaderTitle, area, StringComparison.OrdinalIgnoreCase),
					next, $"clicking the {area} area node did not show its Map Loader"))));
			}
			if ( drags >= MaxAtlasDrags ) return Fail($"select {area}: still not near the middle after {drags} drags (at {at.X:F0},{at.Y:F0})");
			if ( !FindEmptyAtlasSpot(device, center, screen.Height, out var from) ) return Fail($"select {area}: no empty spot on the Atlas to drag it by");
			var delta = center - at;
			float most = screen.Height * 0.35f;
			if ( delta.Length() > most ) delta = Vector2.Normalize(delta) * most;
			Log($"{Name}: dragging the Atlas by {delta.X:F0},{delta.Y:F0} (the {area} node is at {at.X:F0},{at.Y:F0}; drag {drags + 1} of {MaxAtlasDrags})");
			return Drag(from, from + delta, new Delay(250, SelectAreaStep(area, drags + 1, next)));
		}, next);

		// a spot near the middle of the screen with no area node near it, and on no other element of the Atlas or the panel,
		// so pressing there drags the Atlas, not clicks something (seen live, 3.30.0d: the dead middle is the Atlas's own
		// "29/100" counter, child 194, a button that takes the press, with discs (196-198) and a button (195) round it;
		// corner buttons and icons are children 137-160; children 0-3 are the full-size background layers)
		private static bool FindEmptyAtlasSpot(MapDevice device, Vector2 center, float height, out Vector2 spot) {
			var nodes = device.AreaNodes.Select(p => p.node.GetClientRect()).Select(r => new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2))
				.Where(p => Vector2.Distance(p, center) < height * 0.4f).ToList();
			const float margin = 15f;
			var panel = device.GetClientRect();
			// (the visible children of the Atlas and of the panel, but not the Atlas itself, the MapLoader, or the
			// background layers: anything half the screen wide or more)
			var blocks = (device.Atlas?.Children ?? Enumerable.Empty<Element>()).Concat(device.Children.Skip(1))
				.Where(e => IsValid(e) && e.IsVisibleLocal).Select(e => e.GetClientRect())
				.Where(r => r.Width > 0 && r.Height > 0 && r.Width < panel.Width / 2 && r.Height < panel.Height / 2)
				.Select(r => new System.Drawing.RectangleF(r.X - margin, r.Y - margin, r.Width + 2 * margin, r.Height + 2 * margin)).ToList();
			float clearance = 40f;
			for ( float rad = 0; rad < height * 0.3f; rad += clearance / 2 ) {
				for ( int i = 0; i < 16; i++ ) {
					double a = i * Math.PI / 8;
					var s = center + new Vector2((float)Math.Cos(a) * rad, (float)Math.Sin(a) * rad);
					if ( nodes.All(p => Vector2.Distance(p, s) > clearance) && !blocks.Any(r => r.Contains(s.X, s.Y)) ) { spot = s; return true; }
					if ( rad == 0 ) break;
				}
			}
			spot = default;
			return false;
		}

		// drag with the left button: press at from, a few moves, let go at to, then next
		private static IState Drag(Vector2 from, Vector2 to, IState next) =>
			Inputs.MoveTo(from, new Delay(60, new LeftMouseDown(new Delay(60,
				Inputs.MoveTo(Vector2.Lerp(from, to, 0.33f), new Delay(50,
				Inputs.MoveTo(Vector2.Lerp(from, to, 0.66f), new Delay(50,
				Inputs.MoveTo(to, new Delay(80, new LeftMouseUp(new Delay(Inputs.Latency, next))))))))))));

		// ---- the parts ----

		// show the tab named name (see StashRoot.ActivateTab), then next
		private IState Tab(string name, IState next) => new Step($"Show the \"{name}\" tab", (s, ctx, dt) =>
			StashIsOpen() && GetUI()?.StashElement is StashRoot stash ? stash.ActivateTab(name, next)
			: Fail($"can't show the \"{name}\" tab: the stash is not open"), next);

		// walk to within ClickReach of what find finds (when this is reached), wait there for it to hold still on screen,
		// then next; straight to next if skipTo says the walk isn't needed (eg, the window it walks to open is open already)
		private IState WalkTo(string what, Func<Entity> find, IState next, Func<bool> skipTo = null) {
			var arrive = WaitForStill(what, find, next);
			return new Step($"Find {what}", (s, ctx, dt) => {
				if ( skipTo?.Invoke() == true ) return next;
				var pos = find()?.GetComponent<Positioned>();
				if ( !IsValid(pos) ) return Fail($"no {what} in sight");
				var grid = pos.GridPos;
				return new Navigate(what, _ => grid, ClickReach, arrive) { FightRadius = -1 };
			}, arrive);
		}

		/// <summary>The frames in a row the click point must stay put (see WaitForStill), and how long to wait for that at most.</summary>
		private const int StillFrames = 2;
		private const long StillTimeoutMs = 2000;

		// stop walking, and wait until where what would be clicked (its label, else where it stands: Ground.TryGetClickPoint)
		// has not moved on screen for StillFrames frames in a row, then next: on arriving (eg, by a Blink, or the camera
		// catching up after one) the labels move for a few frames, and a click read from them too soon lands past what it is
		// meant for. Goes on anyway after StillTimeoutMs.
		//
		// The player must be done moving too: a Blink moves the player's grid position (Positioned) at once, so Navigate has
		// arrived, but the player is drawn (Render, and the camera, and the labels with it) getting there only at the end of
		// its animation, after its cast, during which nothing on screen moves: so the label alone can sit still for a frame
		// or two before it moves. Nothing in ActionFlags, the Actor's action or AnimationId shows a Frostblink (all 0 during
		// it, 3.30.0d, seen by the user). So also: the player is drawn within StillGridCells of its grid position, and has not
		// moved on screen; and its ActionFlags show no skill in use and no walking (4224 while walking, 0 idle).
		private const Offsets.Component_ActionFlags Busy = Offsets.Component_ActionFlags.UsingAbility
			| Offsets.Component_ActionFlags.AbilityCooldownActive | Offsets.Component_ActionFlags.Moving;
		private const float StillGridCells = 2f;
		private IState WaitForStill(string what, Func<Entity> find, IState next) {
			Vector2? lastAt = null, lastMe = null;
			int still = 0, frames = 0;
			void Reset() { lastAt = lastMe = null; still = frames = 0; }
			return new Step($"Wait for {what} to hold still", (s, ctx, dt) => {
				ctx.Driver.StopWalking($"arriving at {what}");
				// (the left button let go of on arriving, whoever held it: held, the player walks on past what it came to)
				if ( frames == 0 ) { DefaultFollower.ReleaseMouse(); Run(MouseHold.Release(MouseBtn.Left)); }
				frames++;
				var player = GetPlayer();
				var flags = player?.GetComponent<Actor>()?.ActionFlags ?? Offsets.Component_ActionFlags.None;
				// (how far the player is drawn from where the grid says it is, in cells)
				var drawn = Position(player);
				float lag = Vector2.Distance(new Vector2(drawn.X, drawn.Y) * 0.092f, GridPosition(player));
				bool idle = (flags & Busy) == 0 && lag <= StillGridCells;
				bool haveMe = TryWorldToScreen(drawn, out Vector2 me);
				if ( !Ground.TryGetClickPoint(find(), out Vector2 at) || !haveMe ) {
					lastAt = lastMe = null; still = 0;
					if ( s.Elapsed > StillTimeoutMs ) { Reset(); s.Elapsed = 0; return Fail($"{what} is not on screen"); }
					return s;
				}
				bool held = lastAt is Vector2 a && Vector2.Distance(a, at) < 0.5f && lastMe is Vector2 m && Vector2.Distance(m, me) < 0.5f;
				still = held && idle ? still + 1 : 0;
				lastAt = at; lastMe = me;
				s.Detail = $"{what} at {at.X:F0}, {at.Y:F0}, player at {me.X:F0}, {me.Y:F0} ({flags}, drawn {lag:F1} cells from its grid position), still for {still} frames";
				if ( still >= StillFrames || s.Elapsed > StillTimeoutMs ) {
					Log(still >= StillFrames
						? $"{Name}: {what} held still at {at.X:F0}, {at.Y:F0} ({frames} frames, {s.Elapsed} ms after arriving)"
						: $"{Name}: {what} still moving after {StillTimeoutMs} ms (at {at.X:F0}, {at.Y:F0}, player {flags}, drawn {lag:F1} cells from its grid position): clicking anyway");
					Reset(); s.Elapsed = 0;
					return next;
				}
				return s;
			}, next);
		}

		/// <summary>How many times ClickToOpen clicks before it gives up, and how long it waits for the window after each.</summary>
		private const int OpenClicks = 3;
		private const long OpenWaitMs = 660; // (the stash opened within 0.4 s of a click that worked)

		// click what find finds (its label if it shows one), wait for isOpen, then next; nothing to do if open already. A
		// click the game ignores (eg, right after a Frostblink lands, at the right spot) is tried again: up to OpenClicks
		// clicks, OpenWaitMs apart, each aimed where it is then
		private IState ClickToOpen(string what, Func<Entity> find, Func<bool> isOpen, IState next) {
			int clicks = 0;
			return new Step($"Open {what}", (s, ctx, dt) => {
				if ( isOpen() ) { clicks = 0; return next; }
				if ( clicks >= OpenClicks ) { clicks = 0; return Fail($"{what} did not open after {OpenClicks} clicks"); }
				var e = find();
				if ( e == null ) { clicks = 0; return Fail($"no {what} in sight"); }
				ctx.Driver.StopWalking($"opening {what}");
				clicks++;
				Log($"{Name}: clicking {what} at {(Ground.TryGetClickPoint(e, out Vector2 at) ? $"{at.X:F0}, {at.Y:F0}" : "?")} (click {clicks} of {OpenClicks})");
				s.Detail = $"click {clicks} of {OpenClicks}";
				// (back here after the wait, open or not: open goes on to next, else the next click)
				var self = s;
				var click = Ground.ClickEntity(e, new Step($"Wait for {what} to open", (w, c2, d2) =>
					isOpen() || w.Elapsed > OpenWaitMs ? self : w, self));
				if ( click != null ) return click;
				clicks = 0;
				return Fail($"can't click {what} (off screen, or under a panel)");
			}, next);
		}

		// press Escape (noted as ours, see Inputs.LastEscapeMs), wait a moment, then next
		private static IState CloseWindow(string what, IState next) {
			Inputs.LastEscapeMs = Time.ElapsedMilliseconds;
			return Inputs.Press(VKey.Escape, new Delay(InventoryPlans.Settle, next));
		}

		// the stash entity nearest the player
		private static Entity FindStash() => Nearest(e => e.Path == Offsets.PATH_STASH);

		private static Entity Nearest(Func<Entity, bool> match) {
			var player = GetPlayer()?.GetComponent<Positioned>();
			if ( !IsValid(player) ) return null;
			var at = player.GridPos;
			return (GetEntities() ?? Enumerable.Empty<Entity>()).Where(e => IsValid(e) && match(e))
				.Select(e => (e, pos: e.GetComponent<Positioned>())).Where(x => IsValid(x.pos))
				.OrderBy(x => Combat.Distance(at, x.pos.GridPos)).Select(x => x.e).FirstOrDefault();
		}

		// the map to roll / open: NextMap if it is in the backpack, else the first map there
		private InventoryItem BackpackMap() {
			var maps = BackpackItems().Where(i => ChooseNextMap.IsMap(i?.Entity)).ToList();
			return (NextMap is { } n ? maps.FirstOrDefault(i => i.Entity.Id == n.id) : null) ?? maps.FirstOrDefault();
		}

		private static readonly Regex MapKeyTier = new Regex(@"/MapKeyTier(\d+)$");

		/// <summary>A map's tier: from its path (3.30: Metadata/Items/Maps/MapKeyTier3), else its Map component; 0 if neither says.</summary>
		public static int TierOf(Entity map) {
			var m = map?.Path is string path ? MapKeyTier.Match(path) : null;
			if ( m != null && m.Success && int.TryParse(m.Groups[1].Value, out int tier) ) return tier;
			return map?.GetComponent<Map>()?.Tier ?? 0;
		}

		// the maps showing in the stash
		private static List<StashItem> MapsShowing() => StashItems().Where(i => ChooseNextMap.IsMap(i?.Entity)).ToList();

		// the maps of MapTier showing in the stash
		private IEnumerable<StashItem> MapsOfTier() => MapsShowing().Where(i => TierOf(i.Entity) == MapTier && !IsUniqueMap(i.Entity));

		// a unique map: its base is a plain MapKeyTierN (eg, an unidentified one is "Map (Tier 4)"), so only its rarity tells
		private static bool IsUniqueMap(Entity e) => e?.GetComponent<Mods>() is Mods m && IsValid(m) && m.Rarity == Offsets.ItemRarity.Unique;

		// the maps showing are all of tier, and some are not unique: not the unique maps' section, left open by an earlier
		// stashing, whose maps have the same MapKeyTierN paths (seen live, 3.30.0d: an unidentified tier 4 unique showing
		// alone was taken for the tier 4 maps); a unique among the tier's own maps is fine (a stashed unique goes there too)
		private static bool ShowsTier(int tier) {
			var maps = MapsShowing();
			return maps.Count > 0 && maps.All(i => TierOf(i.Entity) == tier) && maps.Any(i => !IsUniqueMap(i.Entity));
		}

		// some visible grid of maps holds only maps of tier, some not unique (after its button is clicked: the grid shown
		// before, eg tier 2's after a deposit, may still read as visible a while beside the new one; each tier's grid is
		// its own element, 3.30.0d, the page's .0.4.N, made the first time that tier is shown)
		private static bool GridShowsTier(int tier) => MapsShowing().GroupBy(i => i.Parent?.Address ?? IntPtr.Zero)
			.Any(g => g.All(i => TierOf(i.Entity) == tier) && g.Any(i => !IsUniqueMap(i.Entity)));

		// for the log: the visible grids of maps, each with its maps' tiers (and uniques)
		private static string MapGridsSeen() {
			var grids = MapsShowing().GroupBy(i => i.Parent?.Address ?? IntPtr.Zero).ToList();
			if ( grids.Count == 0 ) return "none";
			return string.Join("; ", grids.Select(g => $"grid {g.Key:X}: {g.Count()} maps, tiers "
				+ string.Join(",", g.GroupBy(i => TierOf(i.Entity)).OrderBy(t => t.Key).Select(t => $"{t.Key} x{t.Count()}"))
				+ $", {g.Count(i => IsUniqueMap(i.Entity))} unique"));
		}

		// the tier buttons of a Map stash tab's page, in tier order (see ActivateMapTier): the rows of 9 children that each
		// have a number for their child [0]'s text, under the page's first child
		private static List<Element> TierButtons(Element page) {
			var rows = page?.GetChild(0)?.Children.Where(r => IsValid(r) && r.IsVisibleLocal
				&& r.Children.Count() == 9 && r.Children.All(b => int.TryParse(b?.GetChild(0)?.Text?.Trim(), out _)));
			return (rows ?? Enumerable.Empty<Element>()).SelectMany(r => r.Children)
				.OrderBy(b => b.GetClientRect().Y).ThenBy(b => b.GetClientRect().X).ToList();
		}

		// Ctrl + click the first map of MapTier showing into the backpack, note it as NextMap, then next
		private IState TakeMap(IState next) => new Step("Take a map", (s, ctx, dt) => {
			var map = MapsOfTier().FirstOrDefault();
			if ( map == null ) return Fail($"no tier {MapTier} maps showing on the \"{MapsTab}\" tab");
			if ( !BackpackIsOpen() ) return Fail("take a map: the inventory is not open");
			var had = new HashSet<uint>(BackpackItems().Where(i => ChooseNextMap.IsMap(i?.Entity)).Select(i => i.Entity.Id));
			Func<InventoryItem> taken = () => BackpackItems().FirstOrDefault(i => ChooseNextMap.IsMap(i?.Entity) && !had.Contains(i.Entity.Id));
			return Inputs.CtrlClick(map.GetClientRect(), WaitUntil("the map in the backpack", 2000, () => taken() != null,
				new Step("Note the next map", (s2, c2, d2) => {
					var e = taken()?.Entity;
					if ( e == null ) return Fail("the map left the backpack");
					NextMap = (e.Id, ChooseNextMap.MapArea(e));
					Log($"{Name}: took {Equipment.ItemName(e)} (tier {TierOf(e)}) from the \"{MapsTab}\" tab");
					return next;
				}, next), "the map did not show in the backpack"));
		}, next);

		// (map device open) SelectMapArea, then LoadAndActivate
		private IState AtTheMapDevice(IState next) => new Step("At the Map Device", (s, ctx, dt) =>
			MapDeviceIsOpen() ? SelectAreaStep(MapArea, 0, LoadAndActivate(next)) : Fail("at the Map Device: it is not open"), next);

		// (Map Loader showing MapArea) put the map in the slot if it is empty (opening the inventory if need be, then showing
		// MapArea again), note the portals open now (not the new map's), click Activate, wait for a new portal, then next
		private IState LoadAndActivate(IState next) => new Step("Load the map", (s, ctx, dt) => {
			var device = GetUI()?.MapDevice;
			if ( !MapDeviceIsOpen() || device == null ) return Fail("load the map: the Map Device is not open");
			if ( !device.MapLoaderIsOpen ) return Fail("load the map: the Map Loader is not showing");
			var loaded = device.LoadedMap;
			if ( loaded == null ) {
				if ( !BackpackIsOpen() ) // (then show the area again, in case opening the inventory hid the Map Loader)
					return GameKeys.Press(GameKeys.InventoryKey, WaitUntil("the inventory to open", 2000, BackpackIsOpen,
						AtTheMapDevice(next), "the inventory did not open"));
				var map = BackpackMap();
				if ( map == null ) return Fail("load the map: no map in the backpack");
				var e = map.Entity;
				Log($"{Name}: putting {Equipment.ItemName(e)} (tier {TierOf(e)}) in the Map Loader for {MapArea}");
				return Inputs.CtrlClick(map.GetClientRect(), WaitUntil("the map in the Map Loader", 2000,
					() => GetUI()?.MapDevice?.LoadedMap != null, new Delay(InventoryPlans.Settle, s), "the map did not show in the Map Loader"));
			}
			var button = device.MapLoaderActivateButton;
			if ( button == null ) return Fail("load the map: no Activate button");
			StalePortals.Clear();
			StalePortals.UnionWith(MapDevice.MapPortals().Select(p => p.Id));
			MapLaunched = true;
			var r = button.GetClientRect();
			Log($"{Name}: clicking Activate at {r.X + r.Width / 2:F0},{r.Y + r.Height / 2:F0} ({Equipment.ItemName(loaded.Entity)} in {device.MapLoaderTitle})");
			return Inputs.Click(r, WaitUntil("a new map portal", 6000, () => AtE.OpenMap.FreshPortals(this).Any(), next, "no new map portal opened"));
		}, next);

		private static uint AreaHash() => PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;

		private bool IsProhibited(ItemMod mod) {
			var terms = ProhibitedMods.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
			return terms.Any(t => (mod.GroupName?.IndexOf(t, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
				|| (mod.DisplayName?.IndexOf(t, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
		}

		// what a currency item changes on a map: rarity, identified, the explicit mods
		private static string MapSignature(Entity map) {
			var mods = map?.GetComponent<Mods>();
			if ( !IsValid(mods) ) return null;
			return $"{mods.Rarity}|{mods.IsIdentified}|{string.Join(",", mods.ExplicitMods.Select(m => m.GroupName))}";
		}

		private IState WaitUntil(string what, long timeoutMs, Func<bool> done, IState next, string failWhy) =>
			new Step($"Wait for {what}", (s, ctx, dt) => done() ? next : s.Elapsed > timeoutMs ? Fail(failWhy) : s, next);

		// as WaitUntil, but on the timeout with a map on the cursor: put it back in the backpack and go on with retry() (at
		// most MaxCursorMapRecoveries times in a roll), else stop
		private IState WaitUntil(string what, long timeoutMs, Func<bool> done, IState next, string failWhy, Func<IState> retry) =>
			new Step($"Wait for {what}", (s, ctx, dt) => {
				if ( done() ) return next;
				if ( s.Elapsed <= timeoutMs ) return s;
				if ( CursorHoldsMap() && cursorMapRecoveries < MaxCursorMapRecoveries ) {
					cursorMapRecoveries += 1;
					Log($"{Name}: {failWhy}, and the map is on the cursor: putting it back (recovery {cursorMapRecoveries} of {MaxCursorMapRecoveries})");
					return PutCursorMapDown(retry());
				}
				return Fail(failWhy + (CursorHoldsMap() ? " (the map is on the cursor)" : ""));
			}, next);

		/// <summary>How long a map must read as unidentified before a Scroll of Wisdom is used on it.</summary>
		public long UnidentifiedSureMs = 400;
		/// <summary>Maps put back from the cursor in one roll (see WaitUntil) before it stops.</summary>
		private const int MaxCursorMapRecoveries = 2;
		private int cursorMapRecoveries;

		// is a map (an item whose path is a map's) held on the cursor?
		private static bool CursorHoldsMap() {
			var igs = PoEMemory.GameRoot?.InGameState;
			if ( igs == null || igs.ServerInventoryCount(Offsets.InventorySlot.Cursor) <= 0 ) return false;
			var ent = igs.CursorItem;
			return IsValid(ent) && ChooseNextMap.IsMap(new Entity() { Address = ent });
		}

		// put the map on the cursor in the first free cell of the backpack (it is 1x1), then next once the cursor is empty
		private IState PutCursorMapDown(IState next) {
			Log($"{Name}: a map is on the cursor: putting it in the backpack");
			return InventoryPlans.PutHeldInBackpack(1, 1, WaitUntil("the cursor to be empty", 2000, () => CursorItemName() == "", next,
				"the map on the cursor did not go in the backpack"));
		}

		private IState Fail(string why) {
			Log($"{Name}: {why}");
			LastFailure = why;
			return null;
		}

		private void RenderStepSettings() {
			ImGui.InputText("Dump Tab", ref DumpTab, 64);
			ImGui.InputText("Maps Tab", ref MapsTab, 64);
			ImGui.InputText("Currency Tab", ref CurrencyTab, 64);
			ImGui.InputText("Map Area", ref MapArea, 64);
			ImGui.SameLine();
			ImGui_HelpMarker("The stash tabs, by name: everything is dumped in the Dump Tab, maps are taken from the Maps Tab (a Map stash tab), and the Orbs of Scouring and Alchemy that roll them come from the Currency Tab.");
			ImGui.SliderInt("Map Tier", ref MapTier, 1, 18);
			ImGui.InputText("Prohibited Mods", ref ProhibitedMods, 1000);
			ImGui.SameLine();
			ImGui_HelpMarker("Map mods not to run, separated by commas: parts of the mod's name (eg, Reflect) or its display name. A map is rolled until it is a rare with none of them.");
			ImGui.SliderInt("Max Rolls", ref MaxRolls, 1, 100);
		}

		/// <summary>
		/// A step of a chain: func is asked each frame (with the step, for its Elapsed and Detail) what to run: the step
		/// itself to keep at it, another state, or null to stop. Its Next is the step it goes on to when all is well (for
		/// looking at the plan; func returns it itself). A CampaignState, so the routine does not cut a long wait short (as
		/// it does input that takes longer than MaxInputMs). Elapsed counts the ms it has been ticked.
		/// </summary>
		private sealed class Step : CampaignState {
			private readonly string label;
			private readonly Func<Step, CombatContext, long, IState> func;
			/// <summary>Milliseconds ticked so far.</summary>
			public long Elapsed;
			public Step(string label, Func<Step, CombatContext, long, IState> func, IState next) : base(next) {
				this.label = label;
				this.func = func;
			}
			/// <summary>Go on to Next, and let go of it (so a step left behind does not keep the rest of a chain alive).</summary>
			public IState Pass() {
				var next = Next;
				Next = null;
				return next;
			}
			public override string Name => label;
			protected override IState Tick(CombatContext ctx, long dt) {
				Elapsed += dt;
				return func(this, ctx, dt);
			}
		}
	}
}
