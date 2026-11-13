using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Travel by waypoint to another area (one whose waypoint has been discovered): walk to this area's waypoint (fighting
	/// on the way, unless IgnoreMonsters), click its label to open the waypoint panel, and click the destination on it
	/// (see WaypointPanel.ClickWaypoint). Done when the current area is the destination.
	/// </summary>
	public class UseWaypoint : Goal {
		public readonly string Destination;
		/// <summary>Click the waypoint when within this many cells of it.</summary>
		public float Reach = 12;
		private long lastClick;

		/// <param name="destination">The area's name as the waypoint panel shows it (eg, "The Coast").</param>
		public UseWaypoint(string destination) => Destination = destination;
		/// <summary>Hand back (with Missing set) if the destination is not on the waypoint panel, instead of waiting.</summary>
		public bool GiveUpIfMissing;
		/// <summary>True once the panel was seen without the destination on it (it is not discovered).</summary>
		public bool Missing { get; private set; }
		public override string Name => $"Take the waypoint to {Destination}";
		public override bool Sticky => false;
		/// <summary>The area it goes to, if not called as the panel names it (eg, "Merveil's Caverns" goes to The Cavern of Anger).</summary>
		public string Arrives;
		public override bool CheckDone(CombatContext ctx) => GoToArea.Is(GetCurrentArea(), Arrives ?? Destination);

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( WaypointPanel.Panel() != null ) {
				ctx.Driver.StopWalking("using the waypoint");
				if ( now - lastClick < 2000 ) return this;
				var element = WaypointPanel.FindWaypointElement(Destination);
				if ( element != null ) {
					lastClick = now;
					Detail = $"clicking {Destination} on the waypoint panel";
					var r = element.GetClientRect();
					return Inputs.Click(new System.Numerics.Vector2(r.X + WaypointPanel.ClickOffset.X, r.Y + WaypointPanel.ClickOffset.Y), this);
				} else {
					Detail = $"{Destination} is not on the waypoint panel (it shows: {string.Join(", ", WaypointPanel.Destinations().Select(d => d.name))})";
					if ( GiveUpIfMissing && WaypointPanel.Destinations().Any() ) {
						Missing = true;
						// close the panel
						return Inputs.Press(VKey.Escape, Next);
					}
				}
				return this;
			}
			var ent = GetEntities()?.FirstOrDefault(e => IsValid(e) && e.Path == AreaExits.PATH_WAYPOINT);
			var entPos = ent?.GetComponent<Positioned>();
			Offsets.Vector2i? pos = IsValid(entPos) ? entPos.GridPos : ctx.Terrain.Waypoint?.GridPos;
			if ( !pos.HasValue ) {
				Detail = "looking for the waypoint";
				return FightFirst(ctx) ?? Call(new Explore("the waypoint") { FightRadius = IgnoreMonsters ? -1 : 0 });
			}
			if ( !IsNear(ctx, pos.Value, Reach, out _, out float d) ) {
				Detail = $"the waypoint is at {pos.Value}, {d:F0} cells away";
				return GoTo("the waypoint", pos.Value, Reach);
			}
			ctx.Driver.StopWalking("opening the waypoint");
			if ( now - lastClick < 1500 ) return this;
			Detail = "clicking the waypoint";
			var labels = GetUI()?.LabelsOnGround?.GetAllLabels();
			var label = ent == null ? null : labels?.FirstOrDefault(l => l.Item?.Address == ent.Address);
			label ??= labels?.FirstOrDefault(l => string.Equals(Ground.LabelText(l.Label), "Waypoint", StringComparison.OrdinalIgnoreCase));
			var click = label != null ? Ground.ClickLabel(label, this) : Ground.ClickGrid(pos.Value, ctx.Z, this);
			if ( click == null ) return this;
			lastClick = now;
			return click;
		}
	}

	/// <summary>
	/// Go to town with a Portal Scroll: use one (press the routine's PortalScrollKey, or else right click one in the
	/// backpack), close the inventory, then click the portal that opens. Fights the monsters near first. Done when the
	/// current area is the destination.
	/// </summary>
	public class UsePortalScroll : Goal {
		public readonly string Destination;
		/// <summary>Click the portal when within this many cells of it.</summary>
		public float Reach = 12;
		private long scrollUsed, lastClick, lastAction;

		/// <param name="destination">The town the portal goes to (eg, "Lioneye's Watch").</param>
		public UsePortalScroll(string destination) => Destination = destination;
		public override string Name => $"Use a Portal Scroll to go to {Destination}";
		public override bool Sticky => false;
		public override bool UsesInventory => true; // (it opens and closes the inventory itself)
		public override void Reset() { base.Reset(); scrollUsed = lastClick = lastAction = 0; }
		public override bool CheckDone(CombatContext ctx) => GoToArea.Is(GetCurrentArea(), Destination);

		/// <summary>A town portal near the player (Metadata/MiscellaneousObjects/...Portal...), or null.</summary>
		private static Entity FindPortal(CombatContext ctx) => GetEntities()?
			.Where(e => IsValid(e) && e.Path is string path && path.StartsWith("Metadata/MiscellaneousObjects/") && path.Contains("Portal"))
			.Select(e => (e, pos: e.GetComponent<Positioned>()))
			.Where(x => IsValid(x.pos) && Combat.Distance(ctx.Grid, x.pos.GridPos) < 40)
			.OrderBy(x => Combat.Distance(ctx.Grid, x.pos.GridPos))
			.Select(x => x.e).FirstOrDefault();

		protected override IState Step(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			var portal = FindPortal(ctx);
			if ( portal != null || now - scrollUsed < 3000 ) {
				// the inventory covers half the screen: close it first
				if ( BackpackIsOpen() ) {
					ctx.Driver.StopWalking("closing the inventory");
					if ( now - lastAction < 700 ) return this;
					lastAction = now;
					return Inputs.Press(VKey.I, this);
				}
				if ( portal == null ) { ctx.Driver.StopWalking("waiting for the portal"); Detail = "waiting for the portal"; return this; }
				var pos = portal.GetComponent<Positioned>().GridPos;
				if ( !IsNear(ctx, pos, Reach, out _, out float d) ) {
					Detail = $"the portal is {d:F0} cells away";
					return Call(new Navigate("the portal", _ => pos, Reach) { FightRadius = -1 });
				}
				ctx.Driver.StopWalking("taking the portal");
				if ( now - lastClick < 1500 ) return this;
				Detail = "clicking the portal";
				var click = Ground.ClickEntity(portal, this);
				if ( click == null ) return this;
				lastClick = now;
				return click;
			}
			if ( !BackpackIsOpen() && FightFirst(ctx) is IState fight ) { Detail = "fighting before using a Portal Scroll"; return fight; }
			ctx.Driver.StopWalking("using a Portal Scroll");
			if ( now - lastAction < 700 ) return this;
			lastAction = now;
			var key = (ctx.Driver as CampaignRoutine)?.PortalScrollKey?.Key ?? VKey.None;
			if ( key != VKey.None && !BackpackIsOpen() ) {
				Detail = $"pressing {key}";
				scrollUsed = now;
				return Inputs.Press(key, this);
			}
			if ( !BackpackIsOpen() ) { Detail = "opening the inventory"; return Inputs.Press(VKey.I, this); }
			var scroll = BackpackItems().FirstOrDefault(i => i.Entity?.Path == Offsets.PATH_SCROLL_PORTAL);
			if ( scroll == null ) { Detail = "no Portal Scroll in the backpack"; return this; }
			Detail = "right clicking a Portal Scroll";
			scrollUsed = now;
			return Inputs.RightClick(scroll.GetClientRect(), this);
			return this;
		}
	}

	/// <summary>
	/// Put a flask (by its base name, eg "Quicksilver Flask") in a flask slot (1 to 5, left to right): pick it up from
	/// the backpack (or another flask slot), click the slot, and put whatever was there where the flask came from (or in
	/// the backpack). Done when it is there, or when there is no such flask to put there.
	/// </summary>
	public class EquipFlask : Goal {
		public readonly string Flask;
		public readonly int Slot;
		private long lastAction;
		private bool holding; // the flask was clicked (it is on the cursor)
		private bool placing; // the slot's old flask was swapped out (it is on the cursor)
		private RectangleF from; // where the flask came from, if a flask slot (else empty)

		public EquipFlask(string flask, int slot) { Flask = flask; Slot = slot; }
		public override string Name => $"Put the {Flask} in flask slot {Slot}";
		public override bool Sticky => false;
		public override bool UsesInventory => true;
		public override bool ProvesEarlier => false; // (done also when there is no such flask)
		public override void Reset() { base.Reset(); holding = placing = false; }

		private bool IsFlask(Entity e) => string.Equals(Equipment.ItemName(e), Flask, StringComparison.OrdinalIgnoreCase);
		private static Inventory Flasks => GetUI()?.InventoryPanel?.Flasks;

		/// <summary>The box of flask slot i (0 to 4) on screen: a fifth of the flask inventory's box.</summary>
		private static RectangleF SlotRect(RectangleF panel, int i) => new RectangleF(panel.X + (i * panel.Width / 5), panel.Y, panel.Width / 5, panel.Height);
		private static int SlotOf(RectangleF panel, InventoryItem item) {
			var r = item.GetClientRect();
			return panel.Width <= 0 ? -1 : (int)((r.X + (r.Width / 2) - panel.X) / (panel.Width / 5));
		}

		private InventoryItem InSlot(int i) {
			var inv = Flasks;
			if ( inv == null ) return null;
			var panel = inv.GetClientRect();
			return inv.Items.FirstOrDefault(f => f.Entity != null && SlotOf(panel, f) == i);
		}

		public override bool CheckDone(CombatContext ctx) {
			if ( holding || placing ) return false;
			if ( IsFlask(InSlot(Slot - 1)?.Entity) ) return true;
			// none to put there
			bool inBackpack = GetUI()?.InventoryPanel?.Backpack?.Items.Any(i => IsFlask(i.Entity)) ?? false;
			bool inOtherSlot = Flasks?.Items.Any(i => IsFlask(i.Entity)) ?? false;
			return !inBackpack && !inOtherSlot;
		}

		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("using the inventory");
			long now = Time.ElapsedMilliseconds;
			if ( now - lastAction < 700 ) return this;
			lastAction = now;
			if ( !BackpackIsOpen() ) { Detail = "opening the inventory"; return Inputs.Press(VKey.I, this); }
			var inv = Flasks;
			if ( inv == null ) { Detail = "can't find the flask slots"; return this; }
			var panel = inv.GetClientRect();
			if ( placing ) {
				// the old flask is on the cursor: where the new one came from, else the backpack
				placing = false;
				if ( !from.IsEmpty ) { Detail = "putting the old flask where the new one was"; return Inputs.Click(from, this); }
				if ( BackpackGrid.Read().TryFindFree(1, 2, out int x, out int y) ) { Detail = "putting the old flask in the backpack"; return Inputs.Click(BackpackGrid.SpaceRect(x, y, 1, 2), this); }
				Detail = "no room in the backpack for the old flask (it is on the cursor)";
				placing = true;
				return this;
			}
			if ( holding ) {
				holding = false;
				placing = InSlot(Slot - 1) != null; // it swaps with what is there
				Detail = $"putting it in slot {Slot}";
				return Inputs.Click(SlotRect(panel, Slot - 1), this);
			}
			var other = inv.Items.FirstOrDefault(i => IsFlask(i.Entity));
			var item = other ?? BackpackItems().FirstOrDefault(i => IsFlask(i.Entity));
			if ( item == null ) { Detail = $"no {Flask} found"; return this; }
			from = other != null ? SlotRect(panel, SlotOf(panel, other)) : RectangleF.Empty;
			Detail = $"picking up the {Flask}";
			holding = true;
			return Inputs.Click(item.GetClientRect(), this);
		}
	}

	/// <summary>
	/// Put the gems had (in the backpack or sockets) where the build's gem plan wants them (Build section, see
	/// GemArrangement and InventoryPlans.PlanOneGemMove): one move at a time with the inventory open. Done when there is
	/// nothing left to move (or no build is loaded, or after MaxMoves).
	/// </summary>
	public class ArrangeGems : Goal {
		public int MaxMoves = 20;
		private long busyUntil, lastCheck;
		private int moves;
		private bool lastDone;

		public override string Name => "Socket the gems as the build plans" + (moves > 0 ? $" ({moves} moves)" : "");
		public override bool Sticky => false;
		public override bool UsesInventory => true;
		public override bool ProvesEarlier => false;
		public override void Reset() { base.Reset(); moves = 0; busyUntil = lastCheck = 0; lookedOpen = lastDone = false; lastTodo = null; unchanged = 0; }
		private string lastTodo;
		private int unchanged;

		private static GemPlan Plan() => BuildPlan.Gems();

		public override bool CheckDone(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			if ( now < busyUntil ) return false;
			if ( moves >= MaxMoves ) return true;
			// (solving the sockets is not free: ask at most twice a second)
			if ( now - lastCheck < 500 ) return lastDone;
			lastCheck = now;
			var plan = Plan();
			if ( plan == null ) return lastDone = true;
			// the gems in the backpack are only seen with the inventory open: look once that way before saying done
			if ( !BackpackIsOpen() && !lookedOpen ) return lastDone = false;
			lastDone = GemArrangement.Targets(plan, GemPlan.CharacterLevel()).Count == 0;
			if ( lastDone && BackpackIsOpen() ) lookedOpen = true;
			return lastDone;
		}
		private bool lookedOpen;

		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("using the inventory");
			long now = Time.ElapsedMilliseconds;
			if ( now < busyUntil ) { Detail = "moving a gem"; return this; }
			if ( !BackpackIsOpen() ) { Detail = "opening the inventory"; return Inputs.Press(VKey.I, this); }
			var plan = Plan();
			var move = plan == null ? null : InventoryPlans.PlanOneGemMove(plan, GemPlan.CharacterLevel(), this);
			if ( move == null ) { Detail = "nothing to move"; lookedOpen = true; lastCheck = 0; return this; }
			// the same moves still to do after a few moves: they are not working (eg, the clicks miss): stop
			string todo = string.Join(", ", GemArrangement.Targets(plan, GemPlan.CharacterLevel()).Select(t => $"{t.gem.Name}>{t.slot}{t.socket}"));
			if ( todo == lastTodo ) {
				if ( ++unchanged >= 3 ) {
					Log($"Campaign: {Name}: {unchanged} moves changed nothing (still to do: {todo}): giving up");
					moves = MaxMoves;
					return this;
				}
			} else { lastTodo = todo; unchanged = 0; }
			Detail = "moving a gem";
			moves += 1;
			lastCheck = 0;
			// (the move is a plan of clicks, which comes back here)
			return move;
		}
	}

	/// <summary>
	/// Use the Books of Skill in the backpack (a quest reward that gives a passive point): open the inventory, right click
	/// each. Done once the backpack has been seen open (quest items only show then) with none in it.
	/// </summary>
	public class UseBooks : Goal {
		private long lastAction;
		private bool looked; // the backpack was seen open since this goal was last reset (quest items only show once it has been)

		public override string Name => "Use the Books of Skill";
		public override bool Sticky => false;
		public override bool UsesInventory => true;
		public override void Reset() { base.Reset(); looked = false; }

		/// <summary>Is the item a Book of Skill (a quest reward that gives a passive point when used)?</summary>
		public static bool IsBook(Entity e) => e != null
			&& (string.Equals(Equipment.ItemName(e), "Book of Skill", StringComparison.OrdinalIgnoreCase) || (e.Path?.Contains("SkillBook") ?? false));

		private static InventoryItem Book() => GetUI()?.InventoryPanel?.Backpack?.Items.FirstOrDefault(i => IsBook(i.Entity));

		public override bool CheckDone(CombatContext ctx) {
			if ( BackpackIsOpen() ) looked = true;
			return looked && Book() == null;
		}

		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("using the inventory");
			long now = Time.ElapsedMilliseconds;
			if ( now - lastAction < 700 ) return this;
			lastAction = now;
			if ( !BackpackIsOpen() ) { Detail = "opening the inventory"; return Inputs.Press(VKey.I, this); }
			var book = BackpackItems().FirstOrDefault(i => IsBook(i.Entity));
			if ( book == null ) return this;
			Detail = "right clicking a Book of Skill";
			Log("Campaign: using a Book of Skill");
			return Inputs.RightClick(book.GetClientRect(), this);
		}
	}

	/// <summary>
	/// Put the skills on the skill bar as the Build's gem plan says, at the character's level: each group's first skill
	/// gem on the slot its label names (eg, "Q"), if the gem is socketed. Done when they all are (those that can be).
	/// </summary>
	public class BindPlannedSkills : Goal {
		public override string Name => "Put the skills on the bar as the build plans";
		public override bool Sticky => false;
		public override bool ProvesEarlier => false;

		// (slot, skill) the plan wants and can have now (the gem is socketed), not there yet
		private static IEnumerable<(int slot, string skill)> Todo() {
			var plan = BuildPlan.Gems();
			if ( plan == null ) yield break;
			var socketed = Equipment.SocketedGems().ToList();
			foreach ( var group in plan.GroupsFor(GemPlan.CharacterLevel()) ) {
				int slot = GemPlan.SlotOf(group.Label);
				var gem = group.ActiveGems.FirstOrDefault();
				if ( slot < 0 || gem == null || !socketed.Any(e => GemPlan.Matches(gem, e)) ) continue;
				if ( !SkillBar.Holds(SkillBar.GetSlot(slot), gem.Name) ) yield return (slot, gem.Name);
			}
		}

		public override bool CheckDone(CombatContext ctx) => !Todo().Any();

		protected override IState Step(CombatContext ctx) {
			var (slot, skill) = Todo().FirstOrDefault();
			if ( skill == null ) return this;
			ctx.Driver.StopWalking("changing the skill bar");
			Detail = $"putting {skill} on {GemPlan.SlotLabels[slot]}";
			ctx.Driver.Bind(slot, skill);
			return this;
		}
	}

	/// <summary>
	/// What the campaign reads from the Build (the Build section's PoB build), to stay Build-neutral: its gem plan, and
	/// the starting skill and support (the first group of its "Level 1" skill set: its first skill gem, and first support).
	///
	/// The gems that drop at the start are the game's (per class), not the build's: if the one the build names is not
	/// had, but another is, that one is used (and the mismatch is logged once).
	/// </summary>
	public static class BuildPlan {
		/// <summary>The Build's gem plan, or null if no build is loaded.</summary>
		public static GemPlan Gems() {
			var build = GetPlugin<AutomationPlugin>()?.Build;
			build?.EnsureLoaded();
			return build?.Gems;
		}

		/// <summary>The first group of the skill set for level 1, or null.</summary>
		public static PobSkillGroup StartingGroup() => Gems()?.GroupsFor(1).FirstOrDefault();

		/// <summary>Why the starting gems can't be read from the Build, or null if they can.</summary>
		public static string Problem() {
			var plan = Gems();
			if ( plan == null ) return "no Build is loaded (choose one in the Build section)";
			var group = StartingGroup();
			if ( group == null ) return $"the Build {plan.Build.Name} has no skill set for level 1 (title one \"Level 1\")";
			if ( !group.ActiveGems.Any() ) return $"the first group of the Build's level 1 skill set has no skill gem";
			return null;
		}

		/// <summary>The starting skill gem: the Build's (level 1, first group, first skill gem), else the skill gem had; null if neither.</summary>
		public static string StartingSkill() => Resolve(StartingGroup()?.ActiveGems.FirstOrDefault()?.ItemName, Equipment.IsSkillGem, ref warnedSkill);
		/// <summary>The starting support gem: the Build's (level 1, first group, first support), else the support had; null if neither.</summary>
		public static string StartingSupport() => Resolve(StartingGroup()?.Supports.FirstOrDefault()?.ItemName, Equipment.IsSupportGem, ref warnedSupport);
		private static string warnedSkill, warnedSupport;

		private static string Resolve(string planned, Func<Entity, bool> kind, ref string warned) {
			if ( planned != null && Equipment.HasGem(planned) ) return planned;
			var had = Equipment.OwnedGems().Where(kind).Select(Equipment.GemName).FirstOrDefault(n => n != null);
			if ( had == null ) return planned;
			if ( planned != null && warned != had ) {
				warned = had;
				Log($"Campaign: the Build's level 1 skill set names {planned}, but the gem had is {had}: using {had}");
			}
			return had;
		}

		/// <summary>
		/// The skill to fight with: the main group's skill (at the character's level, the group on Q, else the first) if it is on the bar,
		/// else what is on Q (unless it is Default Attack or Movement), else the starting skill if on the bar, else Default Attack.
		/// </summary>
		public static string MainSkill() {
			// the group on Q (MainSlot), else the first
			var groups = Gems()?.GroupsFor(GemPlan.CharacterLevel()).ToList();
			var group = groups?.FirstOrDefault(g => GemPlan.SlotOf(g.Label) == MainSlot) ?? groups?.FirstOrDefault();
			var planned = group?.ActiveGems.FirstOrDefault()?.Name;
			if ( planned != null && SkillBar.FindSlot(planned) != null ) return planned;
			var q = SkillBar.GetSlot(MainSlot)?.Skill;
			if ( q != null && !SkillBar.IsMovement(q) ) {
				string name = SkillBar.SkillName(q);
				if ( name != "Default Attack" ) return name;
			}
			var start = StartingSkill();
			return start != null && SkillBar.FindSlot(start) != null ? start : "Default Attack";
		}

		/// <summary>The slot the main skill goes on (Q).</summary>
		public const int MainSlot = 3;
	}
}
