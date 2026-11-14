using ImGuiNET;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The base for a combat driver: a plugin that clears an area by itself.
	///
	/// Each frame, while switched on (Start, by CombatSection's key): if life is low, retreat to a safe spot; else pick a monster
	/// (ChooseTarget), and if it is in range (with line of sight, if NeedsLineOfSight) Attack it, or else walk to a
	/// spot where it would be; with no monsters around, walk to the next unexplored place; with nothing left, stop.
	///
	/// A driver for a particular build overrides Attack (and usually AttackRange / NeedsLineOfSight / ChooseTarget).
	/// Moving the player and attacking both use the mouse, so this waits while the game is not focused.
	/// </summary>
	public abstract class CombatDriver : AutomationSection {

		/// <summary>Monsters within this many cells are fought before exploring further.</summary>
		public int EngageRadius = 60;
		/// <summary>Retreat (to a spot RetreatDistance from all monsters) when life + energy shield is below this percent; 0 never retreats.</summary>
		public int RetreatBelowLifePercent = 0;
		public int RetreatDistance = 40;

		/// <summary>How close (in cells) the player needs to be to Attack.</summary>
		public virtual float AttackRange => 30f;
		/// <summary>True if attacks need a clear line for projectiles (from the player to the target).</summary>
		public virtual bool NeedsLineOfSight => true;

		/// <summary>The monster to fight next, from the hostiles nearest first; null for none.</summary>
		protected virtual Hostile ChooseTarget(System.Collections.Generic.List<Hostile> hostiles) => hostiles.FirstOrDefault();

		/// <summary>
		/// Attack target, which is in range (and in sight); screenPos is where it is on the screen (safe to click).
		/// Called every frame while it is the target, so throttle as needed.
		/// </summary>
		protected abstract void Attack(Hostile target, Vector2 screenPos);

		public bool IsActive { get; private set; }
		public string Status { get; private set; } = "off";
		public Hostile Target { get; private set; }

		private bool binding;
		private ExploreTarget exploreTarget;
		private long exploreTargetTime;

		public override void Render() {
			base.Render();
			ImGui.SliderInt("Engage Radius (cells)", ref EngageRadius, 10, 120);
			ImGui.SliderInt("Retreat Below Life %", ref RetreatBelowLifePercent, 0, 90);
			ImGui.SliderInt("Retreat Distance (cells)", ref RetreatDistance, 10, 80);
			if ( ImGui.TreeNode("Skill Bar") ) {
				foreach ( var slot in SkillBar.GetSlots() ) {
					int ms = SkillBar.CastTimeMs(slot.Skill);
					ImGui.Text($"{slot}{(ms > 0 ? $" ({ms} ms)" : "")}");
				}
				ImGui.Text($"Available{(SkillBar.IsChoosing ? " (open)" : "")}: {string.Join(", ", SkillBar.GetAvailable())}");
				if ( !SkillBar.LeftClickIsMovement() ) ImGui.TextColored(new Vector4(1, .6f, .2f, 1), "Left click is not Movement: walking may attack");
				ImGui.TreePop();
			}
		}

		public void Start() {
			IsActive = true;
			Status = "starting";
		}

		public void Stop(string why) {
			IsActive = false;
			Status = why;
			Target = null;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
		}

		/// <summary>The ms since the last frame (for Decide).</summary>
		protected long FrameDt { get; private set; }

		/// <summary>
		/// Input to send (a chain of input states, see Inputs), queued by Send. A driver that runs states itself (the
		/// Campaign) takes it into its own chain (TakeInput); otherwise it runs on the default Machine.
		/// </summary>
		private IState pendingInput;
		private bool inputRunning;
		/// <summary>True while input is queued or still going out: Send refuses more until it is done.</summary>
		public bool InputBusy => pendingInput != null || inputRunning;
		/// <summary>True if this driver takes the queued input into its own chain (see TakeInput) instead of the default Machine running it.</summary>
		protected virtual bool TakesInput => false;

		/// <summary>Queue input (eg, Inputs.UseSlot) to go out over the next frames; false (and not queued) if input is still going out.</summary>
		public bool Send(IState input) {
			if ( input == null || InputBusy ) return false;
			inputRunning = true;
			input.Tail().Next = State.NewState(() => inputRunning = false);
			pendingInput = input;
			return true;
		}

		/// <summary>The queued input (its chain's last state is open: set its Next), or null; it is not queued any more.</summary>
		protected IState TakeInput() {
			var input = pendingInput;
			pendingInput = null;
			return input;
		}

		/// <summary>Forget input queued or going out (eg, the chain it was in was cancelled).</summary>
		protected void ForgetInput() { pendingInput = null; inputRunning = false; }

		public override IState OnTick(long dt) {
			FrameDt = dt;
			if ( !TakesInput && pendingInput != null ) Run(TakeInput());
			if ( !PoEMemory.IsAttached ) return this;
			if ( !IsActive ) return this;

			var game = PoEMemory.GameRoot?.InGameState;
			var terrain = GetTerrain();
			var player = GetPlayer();
			var pos = player?.GetComponent<Positioned>();
			if ( game == null || terrain == null || !IsValid(pos) ) return Wait("no terrain or player");
			if ( !IsAlive(player) ) {
				if ( !OnPlayerDead() ) Stop("the player died");
				DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
				return this;
			}
			// (the augury card selection window pauses the game: it is worked on all the same, see TryAugury)
			if ( game.IsPaused && !AuguryIsOpen() ) return Wait("the game is paused");
			if ( game.HasInputFocus ) return Wait("typing in the game");
			if ( PoEMemory.GameRoot?.EscapeState?.Menu != null ) return Wait("escape menu");
			if ( !PoEMemory.TargetHasFocus ) return Wait("the game needs focus to use the mouse");
			if ( binding ) return Wait("changing the skill bar");
			// left click should only move (and interact), never attack
			if ( !SkillBar.LeftClickIsMovement() ) {
				Bind(SkillBar.LeftClickSlot, SkillBar.MovementName);
				return Wait("putting Movement on left click");
			}
			var ctx = new CombatContext(this, game, terrain, player, pos.GridPos, Position(player).Z);
			if ( TrySpendPoints(ctx) ) {
				DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
				return this;
			}
			try {
				Decide(ctx);
			} finally {
				// walk, if walking was decided on
				if ( DefaultFollower.IsActiveFor(this) ) {
					DefaultFollower.Tick(DefaultNavigator, terrain, ctx.Grid, ctx.Z);
				}
			}
			DrawBottomLeftText($"{Name}: {Status}", Color.Orange);
			return this;
		}

		// the points and passives there were when spending last stopped short (eg, at a mastery): not tried again until they change
		private string spendStoppedAt;

		/// <summary>
		/// With passive points to spend and no monsters within AttackRange (as for picking up items), spend them on the
		/// Passive Tree plan (see PassiveAllocator). True while doing so (instead of anything else). (The game is paused
		/// while the tree is open, so monsters can't come near once it has started.)
		/// </summary>
		public bool TrySpendPoints(CombatContext ctx) {
			var section = GetPlugin<AutomationPlugin>()?.PassiveTree;
			var allocator = section?.Allocator;
			if ( allocator == null || section.Plan == null ) return false;
			int points = GetUI()?.UnspentPassivePoints ?? 0;
			string at = $"{points} points, {ctx.Game.AllocatedPassives.Length} passives";
			if ( !allocator.IsActive ) {
				// it stopped: all spent, or stopped short
				if ( spending ) { spending = false; spendStoppedAt = at; }
				if ( points == 0 || at == spendStoppedAt ) return false;
				if ( ctx.Hostiles.Any(h => h.Distance <= AttackRange) ) return false; // (not with monsters near: once they are dealt with)
				allocator.Start();
				spending = true;
			}
			// (the Passive Tree section ticks the allocator)
			StopWalking("spending passive points");
			Status = $"spending passive points: {allocator.Status}";
			return true;
		}
		private bool spending;

		/// <summary>
		/// Called each frame while the driver is on and the player is dead: true to stay on (eg, resurrecting), false to stop.
		/// </summary>
		protected virtual bool OnPlayerDead() => false;

		/// <summary>
		/// Called each frame while the driver is on (and the game is ready for input): decide what to do, by calling
		/// TryRetreat, TryFight, Engage, TryExplore, WalkTo, UseSkill... (they set Status). Walking happens after this returns.
		/// The default: retreat if low on life, else fight, else explore, else stop.
		/// </summary>
		protected virtual void Decide(CombatContext ctx) {
			if ( TryAugury() || TryRetreat(ctx) || TryFight(ctx) ) return;
			if ( !TryExplore(ctx) ) Stop("the area is explored and clear");
		}

		/// <summary>If life + energy shield is below RetreatBelowLifePercent with monsters near, walk away from them; true if retreating.</summary>
		public bool TryRetreat(CombatContext ctx) {
			var life = ctx.Player.GetComponent<Life>();
			if ( RetreatBelowLifePercent <= 0 || ctx.Hostiles.Count == 0 || !IsValid(life) ) return false;
			float max = life.MaxHP + life.MaxES;
			float percent = max <= 0 ? 100 : 100f * (life.CurHP + life.CurES) / max;
			if ( percent >= RetreatBelowLifePercent ) return false;
			if ( !Combat.NearestSafeSpot(ctx.Terrain, ctx.Grid, ctx.Hostiles.Select(h => h.GridPos), RetreatDistance, out var safe) ) return false;
			WalkTo(safe, $"retreating to {safe} ({percent:F0}% life)");
			return true;
		}

		/// <summary>
		/// Fight the monster ChooseTarget picks from those near (with skill, if given, else Attack); true if fighting.
		/// A monster near that has to be clicked first (inside a monolith, a frozen augury boss) is clicked (see ClickMonster).
		/// </summary>
		public bool TryFight(CombatContext ctx, string skill = null) {
			if ( ctx.MonstersToClick.FirstOrDefault() is Hostile toClick ) return ClickMonster(ctx, toClick);
			Target = ChooseTarget(ctx.Hostiles);
			return Target != null && Engage(ctx, Target, skill);
		}

		/// <summary>Fight the monsters within radius cells (that can be walked to), instead of EngageRadius; true if fighting.</summary>
		public bool TryFight(CombatContext ctx, float radius, string skill = null) {
			if ( ctx.MonstersToClick.FirstOrDefault(h => h.Distance <= radius) is Hostile toClick ) return ClickMonster(ctx, toClick);
			var near = Combat.GetHostiles(ctx.Grid, radius).Where(h => DefaultNavigator.IsReachable(ctx.Terrain, ctx.Grid, h.GridPos)).ToList();
			Target = ChooseTarget(near);
			return Target != null && Engage(ctx, Target, skill);
		}

		/// <summary>
		/// Fight target: if it is within range (and in sight, if needed), attack it, with skill if given (see UseSkill),
		/// else with Attack; otherwise walk to a spot where it would be. False if there is no such spot.
		/// </summary>
		public bool Engage(CombatContext ctx, Hostile target, string skill = null, float range = 0, bool? needsLineOfSight = null) {
			if ( range <= 0 ) range = AttackRange;
			bool los = needsLineOfSight ?? NeedsLineOfSight;
			Target = target;
			float d = Combat.Distance(ctx.Grid, target.GridPos);
			bool inRange = d <= range && (!los || ctx.Terrain.HasLineOfSight(ctx.Grid, target.GridPos));
			if ( inRange && TryWorldToScreen(target.WorldPos, out Vector2 screen) && PathFollower.IsSafeToClick(screen) ) {
				if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "attacking");
				if ( TryPlaceTotem(ctx, screen) ) { Status = $"placing a Decoy Totem near {target}"; return true; }
				Status = $"attacking {target}{(skill == null ? "" : $" with {skill}")}";
				if ( skill == null ) Attack(target, screen);
				else UseSkillPaced(skill, screen);
				return true;
			}
			if ( Combat.FindAttackSpot(ctx.Terrain, ctx.Grid, target.GridPos, range * 0.8f, los, out var spot) ) {
				WalkTo(spot, $"moving to attack {target} from {spot}");
				return true;
			}
			return false;
		}

		/// <summary>How often to click in the augury card selection window.</summary>
		public long AuguryClickMs = 300;
		/// <summary>How long to wait for the window to close after clicking Confirm, before clicking it again.</summary>
		public long AuguryConfirmRetryMs = 1500;
		private readonly HashSet<long> auguryCardsClicked = new HashSet<long>();
		private long lastAuguryClick, lastAuguryConfirm;

		/// <summary>Is the augury card selection window open (see TryAugury)?</summary>
		public static bool AuguryIsOpen() {
			var window = GetUI()?.AuguryCardSelectionWindow;
			return IsValid(window) && window.IsVisible;
		}

		// the visible direct children of e (eg, the cards in the augury card selection window)
		private static List<Element> VisibleChildren(Element e) =>
			e?.Children?.Where(c => IsValid(c) && c.IsVisible).ToList() ?? new List<Element>();

		/// <summary>
		/// While the augury card selection window is open, either:
		///  - opened by clicking a frozen augury boss (see ClickMonster): click each visible card (the direct children of
		///    its GetChild(2, 6, 0)) once, to apply them to the boss, then click Confirm (GetChild(2, 9));
		///  - opened by "Select a Fate" on an AuguryCardObject (see TrySelectFate; or, if not opened that way, when
		///    GetChild(2, 6, 0) shows no cards but GetChild(2, 5, 0) does): click one of the visible cards (the direct
		///    children of GetChild(2, 5, 0); see ChooseFateCard), then click Confirm (GetChild(2, 9, 1)).
		/// Confirm is clicked again every AuguryConfirmRetryMs until the window closes. True while it is open.
		/// Called first thing in Decide (so a driver that TakesInput gets the clicks into its chain), even while the game
		/// is paused, which it is while the window is open (see OnTick).
		/// </summary>
		public bool TryAugury() {
			var window = GetUI()?.AuguryCardSelectionWindow;
			if ( !IsValid(window) || !window.IsVisible ) {
				auguryCardsClicked.Clear();
				lastAuguryConfirm = 0;
				if ( Time.ElapsedMilliseconds - lastFateClick > FateOpenMs ) selectingFate = false;
				return false;
			}
			StopWalking("the augury card selection window is open");
			long now = Time.ElapsedMilliseconds;
			var bossCards = VisibleChildren(window.GetChild(2, 6, 0));
			if ( selectingFate || (bossCards.Count == 0 && VisibleChildren(window.GetChild(2, 5, 0)).Count > 0) ) return TrySelectFateCard(window, now);
			var cards = bossCards;
			var card = cards.FirstOrDefault(c => !auguryCardsClicked.Contains(c.Address.ToInt64()));
			if ( card != null ) {
				Status = $"augury: applying card {auguryCardsClicked.Count + 1} of {cards.Count}";
				if ( InputBusy || now - lastAuguryClick < AuguryClickMs ) return true;
				var rect = card.GetClientRect();
				if ( rect.Width <= 0 || rect.Height <= 0 ) { auguryCardsClicked.Add(card.Address.ToInt64()); return true; } // (nothing to click)
				DefaultFollower.ReleaseMouse();
				if ( Send(Inputs.Click(rect)) ) {
					auguryCardsClicked.Add(card.Address.ToInt64());
					lastAuguryClick = now;
					Log($"{Name}: augury card {auguryCardsClicked.Count} of {cards.Count} clicked");
				}
				return true;
			}
			var confirm = window.GetChild(2, 9);
			Status = $"augury: {auguryCardsClicked.Count} cards applied, confirming";
			if ( !IsValid(confirm) || !confirm.IsVisible ) { Status = "augury: no Confirm button at GetChild(2, 9)"; return true; }
			if ( InputBusy || now - lastAuguryClick < AuguryClickMs || now - lastAuguryConfirm < AuguryConfirmRetryMs ) return true;
			var at = confirm.GetClientRect();
			if ( at.Width <= 0 || at.Height <= 0 ) return true;
			DefaultFollower.ReleaseMouse();
			if ( Send(Inputs.Click(at)) ) {
				lastAuguryClick = lastAuguryConfirm = now;
				Log($"{Name}: augury Confirm clicked ({auguryCardsClicked.Count} cards)");
			}
			return true;
		}

		/// <summary>
		/// The reward texts of a fate card (a visible child of the window's GetChild(2, 5, 0)): the tooltip of each of its
		/// reward slots, at GetChild(0, 0, 8, 0, i, 3) (hidden until hovered; eg, "Empowered Monster will drop additional
		/// Rare Ranged Weapons or Quivers"). The slot's icon (a rectangle of Augury/2.dds, at the slot's +0x230*+0x8*) says
		/// the same, less exactly. Only the visible slots (one per reward; three in all).
		/// </summary>
		public static IEnumerable<string> FateRewards(Element card) =>
			VisibleChildren(card?.GetChild(0, 0, 8, 0))
				.Select(slot => slot.GetChild(3)?.Text?.Trim())
				.Where(t => !string.IsNullOrEmpty(t));

		/// <summary>The fate card to choose: the first whose rewards mention "Currency", else the first.</summary>
		public static Element ChooseFateCard(List<Element> cards) =>
			cards.FirstOrDefault(c => FateRewards(c).Any(t => t.IndexOf("Currency", StringComparison.OrdinalIgnoreCase) >= 0))
			?? cards.FirstOrDefault();

		// "Select a Fate": a card under GetChild(2, 5, 0) (see ChooseFateCard), once, then Confirm at GetChild(2, 9, 1)
		private bool TrySelectFateCard(Element window, long now) {
			if ( auguryCardsClicked.Count == 0 ) {
				var card = ChooseFateCard(VisibleChildren(window.GetChild(2, 5, 0)));
				Status = "augury: selecting a fate";
				if ( card == null ) { Status = "augury: no fate cards at GetChild(2, 5, 0)"; return true; }
				if ( InputBusy || now - lastAuguryClick < AuguryClickMs ) return true;
				var rect = card.GetClientRect();
				if ( rect.Width <= 0 || rect.Height <= 0 ) { Status = "augury: the first fate card has no size"; return true; }
				DefaultFollower.ReleaseMouse();
				if ( Send(Inputs.Click(rect)) ) {
					auguryCardsClicked.Add(card.Address.ToInt64());
					lastAuguryClick = now;
					Log($"{Name}: augury fate chosen: {card.GetChild(0, 0, 4)?.Text?.Trim()} ({string.Join("; ", FateRewards(card))})");
				}
				return true;
			}
			// (GetChild(2, 9) is the button, IsButton; its GetChild(2, 9, 1), the same size, is hidden (a highlight?), and
			// GetChild(2, 9, 0) its text "confirm")
			var confirm = window.GetChild(2, 9, 1);
			if ( !IsValid(confirm) || !confirm.IsVisible ) confirm = window.GetChild(2, 9);
			Status = "augury: fate selected, confirming";
			if ( !IsValid(confirm) || !confirm.IsVisible ) { Status = "augury: no Confirm button at GetChild(2, 9)"; return true; }
			if ( InputBusy || now - lastAuguryClick < AuguryClickMs || now - lastAuguryConfirm < AuguryConfirmRetryMs ) return true;
			var at = confirm.GetClientRect();
			if ( at.Width <= 0 || at.Height <= 0 ) return true;
			DefaultFollower.ReleaseMouse();
			if ( Send(Inputs.Click(at)) ) {
				lastAuguryClick = lastAuguryConfirm = now;
				Log($"{Name}: augury fate Confirm clicked");
			}
			return true;
		}

		public const string PATH_AUGURY_CARD_OBJECT = "Metadata/Terrain/Leagues/Augury/Objects/AuguryCardObject";
		/// <summary>How near (in cells) to be to an AuguryCardObject to click its "Select a Fate" (see TrySelectFate).</summary>
		public float FateReach = 15;
		/// <summary>How often to click an AuguryCardObject until its window opens.</summary>
		public long FateClickMs = 1000;
		/// <summary>How long after clicking an AuguryCardObject its window counts as opened by that click.</summary>
		public long FateOpenMs = 3000;
		/// <summary>Clicks on one AuguryCardObject without its window opening before it is given up on.</summary>
		public int FateMaxClicks = 5;
		private bool selectingFate;
		private long lastFateClick;
		private uint fateArea;
		private readonly Dictionary<uint, int> fateClicks = new Dictionary<uint, int>(); // entity id: clicks, in this area instance

		/// <summary>Is e an AuguryCardObject whose fate has not been selected yet (its StateMachine's card_chosen is 0)?</summary>
		public static bool IsUnusedAuguryCard(Entity e) => IsValid(e) && e.Path == PATH_AUGURY_CARD_OBJECT
			&& (e.GetComponent<StateMachine>()?.TryGetState("card_chosen", out long chosen) ?? false) && chosen == 0
			&& (e.GetComponent<Targetable>()?.IsTargetable ?? true);

		/// <summary>
		/// "Select a Fate" from an unused AuguryCardObject in range (see IsUnusedAuguryCard): walk within FateReach of it,
		/// then click it (its "Select a Fate" label) every FateClickMs, until its augury card selection window opens (then
		/// TryAugury selects the fate). Not with monsters within AttackRange (fight them first), nor while the window is
		/// open. One clicked FateMaxClicks times without the window opening is given up on. True while doing so.
		/// </summary>
		public bool TrySelectFate(CombatContext ctx) {
			if ( AuguryIsOpen() || BackpackIsOpen() ) return false;
			uint area = PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;
			if ( area != fateArea ) { fateArea = area; fateClicks.Clear(); }
			if ( ctx.Hostiles.Any(h => h.Distance <= AttackRange) ) return false;
			var card = GetEntities()?
				.Where(e => IsUnusedAuguryCard(e) && (!fateClicks.TryGetValue(e.Id, out int n) || n < FateMaxClicks))
				.Select(e => (e, pos: e.GetComponent<Positioned>()))
				.Where(x => IsValid(x.pos))
				.OrderBy(x => Combat.Distance(ctx.Grid, x.pos.GridPos))
				.FirstOrDefault() ?? default;
			if ( card.e == null ) return false;
			var grid = card.pos.GridPos;
			float d = Combat.Distance(ctx.Grid, grid);
			if ( d > FateReach ) {
				var spot = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, grid, out var s) ? s : grid;
				if ( Combat.Distance(ctx.Grid, spot) > 3 ) {
					WalkTo(spot, $"walking to an augury card object to select a fate, {d:F0} cells away");
					return true;
				}
			}
			StopWalking("selecting a fate");
			Status = "clicking \"Select a Fate\"";
			if ( InputBusy || binding || Time.ElapsedMilliseconds - lastFateClick < FateClickMs ) return true;
			var click = Ground.ClickEntity(card.e);
			if ( click == null ) {
				WalkTo(grid, "walking to an augury card object (can't click it from here)");
				return true;
			}
			DefaultFollower.ReleaseMouse();
			if ( Send(click) ) {
				lastFateClick = Time.ElapsedMilliseconds;
				selectingFate = true;
				fateClicks[card.e.Id] = fateClicks.TryGetValue(card.e.Id, out int clicks) ? clicks + 1 : 1;
				Log($"{Name}: clicked \"Select a Fate\" on the augury card object {card.e.Id} (click {fateClicks[card.e.Id]})");
			}
			return true;
		}

		/// <summary>How near (in cells) to be to a monster to click it (see ClickMonster).</summary>
		public float MonsterClickReach = 20;
		/// <summary>How often to click a monster that has to be clicked, until it no longer has to be.</summary>
		public long MonsterClickMs = 300;
		private long lastMonsterClick;

		/// <summary>
		/// Click target, a monster that has to be clicked before it can be fought (see Combat.ClickReason): a monster
		/// inside a monolith (MonsterInsideMonolith = 1, and CannotBeDamaged until it is out), or a unique frozen for an
		/// augury (which opens the augury card selection window). Walks near it, then clicks it every MonsterClickMs
		/// (the caller stops asking once it no longer has to be clicked). True while doing so.
		/// </summary>
		public bool ClickMonster(CombatContext ctx, Hostile target) {
			Target = target;
			string why = Combat.ClickReason(target.Entity) ?? "to click";
			if ( target.Distance > MonsterClickReach ) {
				var spot = DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, target.GridPos, out var s) ? s : target.GridPos;
				if ( Combat.Distance(ctx.Grid, spot) > 3 ) {
					WalkTo(spot, $"walking to {target}, {why}");
					return true;
				}
			}
			StopWalking("clicking a monster");
			Status = $"clicking {target}, {why}";
			if ( InputBusy || binding || Time.ElapsedMilliseconds - lastMonsterClick < MonsterClickMs ) return true;
			var click = Ground.ClickEntity(target.Entity);
			if ( click == null ) {
				// off screen or under a panel: get nearer
				WalkTo(target.GridPos, $"walking to {target}, {why} (can't click it from here)");
				return true;
			}
			DefaultFollower.ReleaseMouse();
			if ( Send(click) ) lastMonsterClick = Time.ElapsedMilliseconds;
			return true;
		}

		/// <summary>The totem skills to keep placed while fighting (see PlaceTotem).</summary>
		public static readonly string[] TotemSkills = { "Decoy Totem" };
		/// <summary>After a totem is placed, its DeployedObjects entry shows up a moment later: no other totem cast until then.</summary>
		public long TotemSettleMs = 1000;
		private long lastTotem;

		/// <summary>While fighting: PlaceTotem for each of TotemSkills, until one is cast. True if one was.</summary>
		public bool TryPlaceTotem(CombatContext ctx, Vector2 targetScreen) {
			foreach ( var name in TotemSkills ) {
				if ( PlaceTotem(ctx, name, targetScreen) ) return true;
			}
			return false;
		}

		/// <summary>How many totems the skill may have out at once (its SkillDisplayNumberOfTotemsAllowed stat; 1 if it has none).</summary>
		public static int TotemsAllowed(ActorSkill skill) =>
			skill != null && skill.TryGetStat(Offsets.GameStat.SkillDisplayNumberOfTotemsAllowed, out int n) && n > 0 ? n : 1;

		/// <summary>
		/// How many totems of the skill are out: the player's DeployedObjects entries with its skill id. If no entry has
		/// the id of any of the player's skills (the entry's SkillId offset is not confirmed), the totem entities among
		/// them (path with "/Totems/"; eg, Decoy Totem's is not named for it) are counted instead.
		/// </summary>
		public static int TotemsPlaced(Actor actor, ActorSkill skill) {
			if ( !IsValid(actor) || skill == null ) return 0;
			var deployed = actor.DeployedObjects.ToList();
			int byId = deployed.Count(d => d.SkillId == skill.Id);
			if ( byId > 0 ) return byId;
			var ids = new HashSet<uint>(actor.Skills.Select(s => (uint)s.Id));
			if ( deployed.Any(d => ids.Contains(d.SkillId)) ) return 0; // (the ids read right: none of this skill's are out)
			return deployed.Count(d => d.GetEntity()?.Path?.Contains("/Totems/", StringComparison.OrdinalIgnoreCase) ?? false);
		}

		/// <summary>
		/// Keep the totem skill called name at its number of totems allowed (see TotemsAllowed): if it is on the skill
		/// bar, usable, off cooldown, and fewer of its totems are out (see TotemsPlaced), cast it halfway between the
		/// player and targetScreen (or at targetScreen, if halfway can't be clicked). True if it was cast.
		/// </summary>
		public bool PlaceTotem(CombatContext ctx, string name, Vector2 targetScreen) {
			if ( InputBusy || binding || Time.ElapsedMilliseconds - lastTotem < TotemSettleMs ) return false;
			var slot = SkillBar.FindSlot(name);
			var skill = slot?.Skill;
			if ( skill == null || !skill.CanBeUsed || skill.IsOnCooldown ) return false;
			var actor = ctx.Player?.GetComponent<Actor>();
			int placed = TotemsPlaced(actor, skill), allowed = TotemsAllowed(skill);
			if ( placed >= allowed ) return false;
			if ( !TryWorldToScreen(Position(ctx.Player), out Vector2 me) ) return false;
			var at = (me + targetScreen) / 2;
			if ( !PathFollower.IsSafeToClick(at) ) at = targetScreen;
			DefaultFollower.ReleaseMouse();
			if ( !Send(Inputs.UseSlot(slot, at, 40)) ) return false;
			lastTotem = Time.ElapsedMilliseconds;
			Log($"{Name}: placing {name} ({placed} of {allowed} out)");
			return true;
		}

		private long lastPacedUse;
		/// <summary>UseSkill, but no more often than the skill's cast time (and at least 150 ms apart).</summary>
		public bool UseSkillPaced(string skill, Vector2 screenPos) {
			int interval = Math.Max(150, SkillBar.CastTimeMs(SkillBar.FindSlot(skill)?.Skill));
			if ( Time.ElapsedMilliseconds - lastPacedUse < interval ) return false;
			if ( !UseSkill(skill, screenPos) ) return false;
			lastPacedUse = Time.ElapsedMilliseconds;
			return true;
		}

		/// <summary>True once TryExplore has found nothing left to explore in this area.</summary>
		public bool ExploreDone => exploreTarget != null && exploreTarget.Kind == ExploreTargetKind.None;

		/// <summary>Walk to the next unexplored place; false if there is none left.</summary>
		public bool TryExplore(CombatContext ctx) {
			// look for the next place twice a second, it moves as the area is explored
			if ( exploreTarget == null || Time.ElapsedMilliseconds - exploreTargetTime > 500 || DefaultFollower.Status == FollowStatus.Arrived ) {
				exploreTarget = DefaultExplorer.FindNextTarget(DefaultNavigator, ctx.Terrain, ctx.Grid, monsters: false);
				exploreTargetTime = Time.ElapsedMilliseconds;
			}
			if ( exploreTarget.Kind == ExploreTargetKind.None ) return false;
			WalkTo(exploreTarget.GridPos, $"exploring {exploreTarget.GridPos} ({DefaultExplorer.ExploredFraction:P0} explored)");
			return true;
		}

		/// <summary>Walk the player to goal (the walking happens after Decide returns).</summary>
		public void WalkTo(Offsets.Vector2i goal, string status) {
			Status = status;
			Combat.PathTo(goal, this);
		}

		/// <summary>Stop walking (eg, to click something).</summary>
		public void StopWalking(string why) {
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, why);
		}

		/// <summary>Set the status line (shown at the bottom left).</summary>
		public void SetStatus(string status) => Status = status;

		/// <summary>
		/// Use the skill called name (eg, "Fireball") aimed at screenPos: press the key of the slot it is on, or, if it is
		/// not on the skill bar, put it on a free slot first (then this returns false until that is done).
		/// </summary>
		public bool UseSkill(string name, Vector2 screenPos, uint holdMs = 40) {
			if ( binding ) return false;
			var slot = SkillBar.FindSlot(name);
			if ( slot == null ) {
				var free = SkillBar.FindFreeSlot();
				if ( free == null ) {
					Status = $"no free slot for {name}";
					return false;
				}
				Bind(free.Index, name);
				return false;
			}
			if ( InputBusy ) return false;
			DefaultFollower.ReleaseMouse(); // (walking holds the left button: let go of it to fight)
			return Send(Inputs.UseSlot(slot, screenPos, holdMs));
		}

		/// <summary>Put name on a slot (see SkillBar.BindSkill); the driver waits until it is done, and stops if it fails.</summary>
		public void Bind(int slotIndex, string name) {
			if ( binding ) return;
			binding = true;
			if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "changing the skill bar");
			Run(SkillBar.BindSkill(slotIndex, name, (ok, why) => {
				binding = false;
				if ( !ok ) Stop($"could not put {name} on slot {slotIndex}: {why}");
			}));
		}

		private IState Wait(string why) {
			Status = $"waiting: {why}";
			DrawBottomLeftText($"{Name}: {Status}", Color.Gray);
			return this;
		}
	}

	/// <summary>
	/// What a combat driver knows about the current frame, for deciding what to do.
	/// </summary>
	public class CombatContext {
		public readonly CombatDriver Driver;
		public readonly InGameState Game;
		public readonly Terrain Terrain;
		public readonly PlayerEntity Player;
		public readonly Offsets.Vector2i Grid;
		public readonly float Z;
		private List<Hostile> hostiles;
		public CombatContext(CombatDriver driver, InGameState game, Terrain terrain, PlayerEntity player, Offsets.Vector2i grid, float z) {
			Driver = driver; Game = game; Terrain = terrain; Player = player; Grid = grid; Z = z;
		}
		/// <summary>The hostile monsters within the driver's EngageRadius that can be walked to, nearest first.</summary>
		public List<Hostile> Hostiles => hostiles ??= Combat.GetHostiles(Grid, Driver.EngageRadius)
			.Where(h => DefaultNavigator.IsReachable(Terrain, Grid, h.GridPos)).ToList();
		private List<Hostile> monstersToClick;
		/// <summary>
		/// The monsters within the driver's EngageRadius to click before fighting (inside a monolith, or a frozen augury
		/// boss; see Combat.ClickReason), nearest first.
		/// </summary>
		public List<Hostile> MonstersToClick => monstersToClick ??= Combat.GetMonstersToClick(Grid, Driver.EngageRadius);
	}

	/// <summary>
	/// The simplest combat driver: aim the cursor at the nearest monster and press one skill key.
	/// Fits most single-skill builds (a spell, a bow attack, a melee strike with a short range).
	/// </summary>
	public class SkillAttackDriver : CombatDriver {
		public override string Name => "Skill Attack";

		/// <summary>The skill to attack with, by name (eg, Fireball); it is put on a free slot if needed. Empty: press AttackKey instead.</summary>
		public string AttackSkill = "";
		public HotKey AttackKey = new HotKey(VKey.Q);
		public int Range = 30; // cells
		public bool LineOfSight = true;
		public int AttackIntervalMs = 250;
		public int HoldMs = 40;

		public override float AttackRange => Range;
		public override bool NeedsLineOfSight => LineOfSight;

		private long lastAttack;

		public override void Render() {
			base.Render();
			ImGui.Separator();
			ImGui.InputText("Attack Skill", ref AttackSkill, 64);
			ImGui.SameLine();
			ImGui_HelpMarker("A skill's name, eg Fireball. It is put on a free slot if it is not on the skill bar, and used as fast as its cast time allows. Leave empty to press the Attack Skill Key instead.");
			if ( string.IsNullOrWhiteSpace(AttackSkill) ) ImGui_HotKeyButton("Attack Skill Key", ref AttackKey);
			ImGui.SliderInt("Attack Range (cells)", ref Range, 3, 80);
			ImGui.Checkbox("Needs Line of Sight", ref LineOfSight);
			ImGui.SliderInt("Attack Interval (ms)", ref AttackIntervalMs, 50, 1500);
			ImGui.SliderInt("Hold Key (ms)", ref HoldMs, 10, 500);
		}

		protected override void Attack(Hostile target, Vector2 screenPos) {
			if ( !string.IsNullOrWhiteSpace(AttackSkill) ) {
				var slot = SkillBar.FindSlot(AttackSkill.Trim());
				int interval = Math.Max(AttackIntervalMs, SkillBar.CastTimeMs(slot?.Skill));
				if ( Time.ElapsedMilliseconds - lastAttack < interval ) return;
				if ( UseSkill(AttackSkill.Trim(), screenPos, (uint)HoldMs) ) lastAttack = Time.ElapsedMilliseconds;
				return;
			}
			if ( AttackKey.Key == VKey.None || Time.ElapsedMilliseconds - lastAttack < AttackIntervalMs ) return;
			lastAttack = Time.ElapsedMilliseconds;
			// aim with the real cursor (the game only reads it while focused), press the skill key (which can be posted)
			Platform.Current.Input.MouseMove(screenPos + WindowOffset());
			Run(new PressKey(AttackKey.Key, (uint)HoldMs));
		}

		private static Vector2 WindowOffset() =>
			PoEMemory.Memory.TryGetTargetWindowRect(out int x, out int y, out _, out _) ? new Vector2(x, y) : Vector2.Zero;
	}
}
