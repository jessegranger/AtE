using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Plans (sequences of IState, joined by Next) for working in the inventory panel: clicking items and sockets, and
	/// changing gear. Each step that clicks is guarded by OnlyIfBackpackIsOpen, so closing the inventory (Escape) stops
	/// the rest of a plan. Steps that depend on where the game put things (eg, a gem taken out of a socket goes in the
	/// first free cell) are planned when they are reached, from what the inventory shows then.
	/// </summary>
	public static class InventoryPlans {

		public static uint InputLatency => (uint)(GetPlugin<CoreSettings>()?.InputLatency ?? 30);
		/// <summary>Time after a click for the game to show what it did.</summary>
		public static uint Settle = 300;

		// (clicks go through Inputs, which add the game window's offset on the screen: a client rect clicked as is misses
		// when the window is not at the top left of the screen)
		public static IState RightClickItem(InventoryItem item, IState next = null) => IsValid(item) ? Inputs.RightClick(item.GetClientRect(), next) : null;
		public static IState LeftClickItem(InventoryItem item, uint count = 1, IState next = null) {
			if ( !IsValid(item) ) return null;
			var rect = item.GetClientRect();
			for ( uint i = 0; i < Math.Max(1u, count); i++ ) next = Inputs.Click(rect, next);
			return next;
		}

		public static IState OnlyIfBackpackIsOpen(IState ifOpen, IState ifClosed = null) => State.NewState("CheckBackpackIsOpen", (self, dt) => BackpackIsOpen() ? ifOpen : ifClosed, next: ifOpen);
		public static IState OnlyIfStashIsOpen(IState ifOpen, IState ifClosed = null) => State.NewState("CheckStashIsOpen", (self, dt) => BackpackIsOpen() && StashIsOpen() ? ifOpen : ifClosed, next: ifOpen);

		/// <summary>
		/// Make the game the focused window (if it is not), then next: eg, a plan started from a button in the overlay,
		/// whose first click would otherwise only focus the game. Stops the plan (null) if the game does not take the focus
		/// within a second.
		/// </summary>
		public static IState TryFocusGame(IState next) => State.NewState("TryFocusGame", (self, dt) => {
			if ( PoEMemory.TargetHasFocus ) return next;
			if ( !Platform.Current.Memory.TryFocusTarget() ) Log("TryFocusGame: could not focus the game");
			return State.WaitFor(1000, () => PoEMemory.TargetHasFocus, new Delay(100, next),
				State.NewState(() => Notify("Could not focus the game: stopped."), null));
		}, next);

		/// <summary>Open the inventory panel (if it is not open), then next.</summary>
		public static IState OpenBackpack(IState next) => State.NewState("OpenBackpack", (self, dt) =>
			BackpackIsOpen() ? next : new PressKey(VKey.I, InputLatency, new Delay(Settle, next)), next);

		/// <summary>
		/// The box of a socket of an item on screen (a worn item, which shows its sockets): the item element has the
		/// sockets as the children of its first child, in socket order, each at the top left of its socket; a socket is
		/// drawn about 3/4 of a backpack cell. Empty if it can't be found.
		/// </summary>
		public static RectangleF SocketRect(InventoryItem item, int socket) {
			var at = item?.GetChild(0)?.GetChild(socket);
			if ( !IsValid(at) ) return RectangleF.Empty;
			float size = .75f * BackpackGrid.CellSize;
			var s = at.GetClientRect();
			return new RectangleF(s.X, s.Y, size, size);
		}

		/// <summary>Right click the gem in a socket of item (it comes onto the cursor).</summary>
		public static IState RightClickSocketedGem(InventoryItem item, int socket, IState next = null) {
			var rect = SocketRect(item, socket);
			return rect.IsEmpty ? null : Named($"RightClickSocketedGem({socket})", Inputs.RightClick(rect, new Delay(Settle, next)));
		}

		/// <summary>Take the gem out of a socket of item (onto the cursor), and put it in the first free cell of the backpack.</summary>
		public static IState UnsocketToBackpack(InventoryItem item, int socket, IState next = null) =>
			RightClickSocketedGem(item, socket, OnlyIfBackpackIsOpen(PutHeldInBackpack(1, 1, next)));

		/// <summary>Left click a socket of item (with a gem on the cursor: it goes in).</summary>
		public static IState LeftClickSocket(InventoryItem item, int socket, IState next = null) {
			var rect = SocketRect(item, socket);
			return rect.IsEmpty ? null : Named($"LeftClickSocket({socket})", Inputs.Click(rect, new Delay(Settle, next)));
		}

		/// <summary>Put a w x h item on the cursor in the first free space of the backpack (found when this step is reached).</summary>
		public static IState PutHeldInBackpack(int w, int h, IState next = null) => State.NewState($"PutHeldInBackpack({w}x{h})", (self, dt) => {
			if ( !BackpackGrid.Read().TryFindFree(w, h, out int x, out int y) ) {
				Notify($"No room in the backpack for a {w}x{h} item (it is on the cursor).");
				return null;
			}
			return Inputs.Click(BackpackGrid.SpaceRect(x, y, w, h), new Delay(Settle, next));
		}, next);

		/// <summary>
		/// Wear newItem (in the backpack) in place of oldItem (worn): take the gems out of oldItem (each onto the cursor, then into the backpack), pick
		/// newItem up, click oldItem (they swap: oldItem comes onto the cursor), and put oldItem in the backpack. Null if
		/// there will not be room in the backpack for oldItem and its gems.
		/// </summary>
		public static IState PlanSwapGear(InventoryItem oldItem, InventoryItem newItem, IState next = null) {
			if ( !IsValid(oldItem) || !IsValid(newItem) ) return null;
			var oldEntity = oldItem.Entity;
			var size = oldEntity?.GetComponent<Base>();
			int w = Math.Max(1, (int)(size?.CellSizeX ?? 1)), h = Math.Max(1, (int)(size?.CellSizeY ?? 1));
			var sockets = oldEntity?.GetComponent<Sockets>();
			var gems = IsValid(sockets) ? Enumerable.Range(0, sockets.Count).Where(i => sockets.Gem(i) != null).ToList() : new List<int>();
			if ( !HasRoom(newItem, gems.Count, w, h) ) {
				Notify($"No room in the backpack for {Equipment.ItemName(oldEntity)} and its {gems.Count} gems.");
				return null;
			}
			// from the end backwards: put the old item away, swap, pick up the new one, and before that, each gem out
			IState plan = OnlyIfBackpackIsOpen(PutHeldInBackpack(w, h, next));
			plan = OnlyIfBackpackIsOpen(Named("ClickWornItem", Inputs.Click(oldItem.GetClientRect(), new Delay(Settle, plan))));
			plan = OnlyIfBackpackIsOpen(Named("PickUpNewItem", LeftClickItem(newItem, 1, new Delay(Settle, plan))));
			foreach ( int socket in gems.AsEnumerable().Reverse() ) {
				var step = UnsocketToBackpack(oldItem, socket, plan);
				if ( step == null ) { Notify($"Can't find socket {socket} of {Equipment.ItemName(oldEntity)} on screen."); return null; }
				plan = OnlyIfBackpackIsOpen(step);
			}
			return TryFocusGame(OpenBackpack(plan));
		}

		/// <summary>Wear newItem (in the backpack) in slot (eg, "LWeapon"): PlanSwapGear with what is worn there, or put it in the empty slot.</summary>
		public static IState PlanEquip(string slot, InventoryItem newItem, IState next = null) {
			var worn = GemArrangement.WornItem(slot);
			if ( worn != null ) return PlanSwapGear(worn, newItem, next);
			var rect = GemArrangement.SlotRect(slot);
			if ( rect.IsEmpty || !IsValid(newItem) ) return null;
			return TryFocusGame(OpenBackpack(OnlyIfBackpackIsOpen(Named("PickUpNewItem", LeftClickItem(newItem, 1, new Delay(Settle,
				OnlyIfBackpackIsOpen(Named($"ClickSlot({slot})", Inputs.Click(rect, new Delay(Settle, next))))))))));
		}

		// will there be room for the gems and the w x h item, once newItem is picked up (the gems go in the first free cells)?
		private static bool HasRoom(InventoryItem newItem, int gems, int w, int h) {
			var grid = BackpackGrid.Read();
			for ( int g = 0; g < gems; g++ ) {
				if ( !grid.TryFindFree(1, 1, out int x, out int y) ) return false;
				grid.Reserve(x, y, 1, 1);
			}
			grid.Free(newItem);
			return grid.TryFindFree(w, h, out _, out _);
		}

		/// <summary>
		/// Put the gems in the worn items as the SocketSolver arranges the gem plan: one move at a time, each planned when
		/// it is reached (from where the gems are then), until there is nothing left to move (or maxMoves).
		/// </summary>
		public static IState PlanSocketGems(GemPlan plan, int maxMoves = 20, IState next = null) {
			int moves = 0;
			return TryFocusGame(State.NewState("PlanSocketGems", (self, dt) => {
				if ( !BackpackIsOpen() ) return null;
				if ( moves++ >= maxMoves ) { Notify($"Socketing gems: still not done after {maxMoves} moves."); return next; }
				var move = PlanOneGemMove(plan, GemPlan.CharacterLevel(), self);
				return move ?? next;
			}, next));
		}

		/// <summary>The next gem move (then next), or null if every gem had is where the arrangement wants it.</summary>
		public static IState PlanOneGemMove(GemPlan plan, int level, IState next) {
			foreach ( var (slot, socket, gem) in GemArrangement.Targets(plan, level) ) {
				var item = GemArrangement.WornItem(slot);
				var sockets = item?.Entity?.GetComponent<Sockets>();
				if ( !IsValid(sockets) ) continue;
				// something else is in the socket: take it out
				if ( sockets.Gem(socket) != null ) return OnlyIfBackpackIsOpen(UnsocketToBackpack(item, socket, next));
				var inBackpack = BackpackItems().FirstOrDefault(i => GemPlan.Matches(gem, i.Entity));
				if ( inBackpack != null ) {
					return OnlyIfBackpackIsOpen(Named($"PickUpGem({gem.Name})", LeftClickItem(inBackpack, 1, new Delay(Settle,
						OnlyIfBackpackIsOpen(LeftClickSocket(item, socket, next))))));
				}
				// in another socket, where it is not wanted: take it out (onto the cursor) and put it in its socket
				var elsewhere = GemArrangement.Misplaced(plan, level).FirstOrDefault(s => GemPlan.Matches(gem, s.gem));
				if ( elsewhere.slot != null ) {
					return OnlyIfBackpackIsOpen(RightClickSocketedGem(GemArrangement.WornItem(elsewhere.slot), elsewhere.socket,
						OnlyIfBackpackIsOpen(LeftClickSocket(item, socket, next))));
				}
			}
			return null;
		}

		/// <summary>The steps of a plan, in order (following Next; nested steps are not opened), for showing it.</summary>
		public static IEnumerable<IState> Steps(IState plan, int max = 200) {
			for ( int i = 0; plan != null && i < max; i++, plan = plan.Next ) yield return plan;
		}

		// a step with a name of its own (so a plan reads well in the Object Browser), that goes on to step
		private static IState Named(string name, IState step) => step == null ? null : new NamedStep(name, step);
		private sealed class NamedStep : State {
			private readonly string name;
			public NamedStep(string name, IState next) : base(next) { this.name = name; }
			public override string Name => name;
			public override IState OnTick(long dt) => Next;
		}
	}
}
