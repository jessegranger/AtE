using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>Searching the UI tree: the visible elements under a root, depth first.</summary>
	public static class UiSearch {
		/// <summary>The visible elements under root (root too), depth first, at most max of them, to maxDepth levels down.</summary>
		public static IEnumerable<Element> Visible(Element root, int maxDepth = 12, int max = 5000) {
			if ( !IsValid(root) || !root.IsVisibleLocal ) yield break;
			var stack = new Stack<(Element e, int depth)>();
			stack.Push((root, 0));
			int seen = 0;
			while ( stack.Count > 0 && seen++ < max ) {
				var (e, depth) = stack.Pop();
				yield return e;
				if ( depth >= maxDepth ) continue;
				foreach ( var child in e.Children ) {
					if ( IsValid(child) && child.IsVisibleLocal ) stack.Push((child, depth + 1));
				}
			}
		}

		/// <summary>The texts of the visible elements under root (trimmed, not empty).</summary>
		public static IEnumerable<string> Texts(Element root, int maxDepth = 12) =>
			Visible(root, maxDepth).Select(e => e.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t));

		/// <summary>Is there a visible element under root whose text is text (ignoring case)?</summary>
		public static bool HasText(Element root, string text, int maxDepth = 12) =>
			Texts(root, maxDepth).Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// How far the character is through the campaign, read from the AreaInstanceUI (U) when a run starts (see
	/// ReadCampaignProgress): its Child(2, 0, 0) lists the Parts available ("Part 1", "Part 2", "Epilogue"), and
	/// Child(2, 0) the acts ("Act 5" there: acts 1 to 4 are complete). Also the act of the area the player is in.
	/// </summary>
	public static class CampaignProgress {
		/// <summary>True once read in this run.</summary>
		public static bool Scanned { get; internal set; }
		/// <summary>The highest "Act N" the AreaInstanceUI shows (0 if not read).</summary>
		public static int HighestAct { get; private set; }
		public static bool HasPart2 { get; private set; }
		public static bool HasEpilogue { get; private set; }
		/// <summary>What was read, for the debug view.</summary>
		public static string Summary { get; private set; } = "not read yet";

		/// <summary>The act of the area the player is in (0 if not known).</summary>
		public static int CurrentAct => GetCurrentArea()?.Act ?? 0;

		/// <summary>Is act n complete: a later act is available, or the player is in one.</summary>
		public static bool ActDone(int n) => HighestAct > n || CurrentAct > n;
		/// <summary>Is Part 1 (acts 1 to 5) complete.</summary>
		public static bool Part1Done => HasPart2 || HasEpilogue || ActDone(5);
		/// <summary>Is the campaign complete: the Epilogue is available.</summary>
		public static bool CampaignDone => HasEpilogue || CurrentAct > 10;

		/// <summary>The texts the last Read found: under Child(2, 0, 0) (the Parts), and under Child(2, 0).</summary>
		public static List<string> PartTexts { get; private set; } = new List<string>();
		public static List<string> ActTexts { get; private set; } = new List<string>();

		/// <summary>Read the open AreaInstanceUI.</summary>
		public static void Read(Element panel) {
			var parts = panel?.GetChild(2, 0, 0);
			var acts = panel?.GetChild(2, 0);
			PartTexts = UiSearch.Texts(parts).ToList();
			ActTexts = UiSearch.Texts(acts).Where(t => t.StartsWith("Act", StringComparison.OrdinalIgnoreCase)).ToList();
			HasEpilogue = UiSearch.HasText(parts, "Epilogue");
			HasPart2 = UiSearch.HasText(parts, "Part 2");
			HighestAct = UiSearch.Texts(acts).Select(t => t.StartsWith("Act ", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.Substring(4), out int n) ? n : 0)
				.DefaultIfEmpty(0).Max();
			Scanned = true;
			Summary = $"highest act {HighestAct}, part 2: {HasPart2}, epilogue: {HasEpilogue}";
			Log($"Campaign: progress: {Summary}");
		}

		/// <summary>Give up reading it (eg, the panel did not open): go by the current area's act.</summary>
		public static void Failed(string why) {
			Scanned = true;
			Summary = $"could not read it ({why}): going by the current act";
			Log($"Campaign: progress: {Summary}");
		}

		/// <summary>Forget it (a new run reads it again).</summary>
		public static void Forget() {
			Scanned = false;
			HighestAct = 0;
			HasPart2 = HasEpilogue = false;
			PartTexts = new List<string>();
			ActTexts = new List<string>();
			Summary = "not read yet";
		}

		/// <summary>
		/// A state (for the default Machine) that reads the progress again: opens the AreaInstanceUI (key, tried up to 3
		/// times), reads it, closes it (if it opened it), then calls done. For the memory server's /progress.
		/// </summary>
		public static IState Scan(Action done, VKey key = VKey.U) {
			Forget();
			bool wasOpen = WaypointPanel.Panel() != null;
			int presses = 0;
			long waited = 0;
			IState finish = State.NewState(done);
			return State.NewState("ScanCampaignProgress", (self, dt) => {
				// (KeyDown / KeyUp post the key to the game's window when it can be sent in the background: no focus needed then)
				if ( !CanSendKey(key) ) { Failed($"{key} can't be sent to the game (not focused, and not sendable in the background)"); return finish; }
				var panel = WaypointPanel.Panel();
				if ( panel != null ) {
					Read(panel);
					return wasOpen ? finish : Inputs.Press(key, finish);
				}
				if ( presses > 0 && (waited += dt) < 1500 ) return self;
				if ( presses >= 3 ) { Failed($"{key} did not open the AreaInstanceUI"); return finish; }
				presses += 1;
				waited = 0;
				return Inputs.Press(key, self);
			});
		}
	}

	/// <summary>
	/// At the start of a run: open the AreaInstanceUI (U), read the campaign progress from it (see CampaignProgress),
	/// and close it again.
	/// </summary>
	public class ReadCampaignProgress : Goal {
		public VKey Key = VKey.U;
		private long lastPress;
		private int tries;
		public override string Name => "Read the campaign progress";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); tries = 0; }
		public override bool CheckDone(CombatContext ctx) => CampaignProgress.Scanned && WaypointPanel.Panel() == null;

		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("reading the campaign progress");
			var panel = WaypointPanel.Panel();
			long now = Time.ElapsedMilliseconds;
			if ( panel != null && !CampaignProgress.Scanned ) CampaignProgress.Read(panel);
			Detail = CampaignProgress.Summary;
			if ( now - lastPress < 1500 ) return this;
			if ( panel != null ) { lastPress = now; return Inputs.Press(Key, this); } // (close it)
			if ( CampaignProgress.Scanned ) return this;
			if ( ++tries > 3 ) { CampaignProgress.Failed($"{Key} did not open the AreaInstanceUI"); return this; }
			lastPress = now;
			return Inputs.Press(Key, this);
		}
	}

	/// <summary>A goal whose steps are not written yet: stops the campaign there (its parent's check skips it once that is done).</summary>
	public class NotSpecified : Goal {
		private readonly string name;
		public NotSpecified(string name) => this.name = name;
		public override string Name => $"{name} (not specified yet)";
		public override bool CheckDone(CombatContext ctx) => false;
		protected override IState Step(CombatContext ctx) {
			ctx.Driver.Stop($"the goals for \"{name}\" are not specified yet");
			return this;
		}
	}

	/// <summary>
	/// Go to an area: by the named waypoint (eg, "Merveil's Caverns" for The Cavern of Anger), if this area has a
	/// waypoint and that one is discovered; else by TravelTo. Done once in the area.
	/// </summary>
	public class RouteTo : Goal {
		public readonly string Area, Waypoint;
		private UseWaypoint waypoint;
		private bool noWaypoint;
		public RouteTo(string area, string waypoint = null) { Area = area; Waypoint = waypoint; }
		public override string Name => $"Route to {Area}{(Waypoint == null ? "" : $" (waypoint: {Waypoint})")}";
		public override bool CheckDone(CombatContext ctx) => GoToArea.Is(GetCurrentArea(), Area);
		protected override IState Step(CombatContext ctx) {
			if ( waypoint?.Missing ?? false ) { noWaypoint = true; waypoint = null; }
			var here = GetCurrentArea();
			if ( Waypoint != null && !noWaypoint && here != null && (here.HasWaypoint || here.IsTown) ) {
				Detail = $"by the waypoint {Waypoint}";
				waypoint = new UseWaypoint(Waypoint) { Arrives = Area, GiveUpIfMissing = true, IgnoreMonsters = IgnoreMonsters };
				return Call(waypoint);
			}
			Detail = "traveling";
			return Call(new TravelTo(Area) { IgnoreMonsters = IgnoreMonsters });
		}
	}

	/// <summary>
	/// Take a transition by its label (eg, "Stairs", "Pyramid Apex"): one that moves the player within the area, or to
	/// another. Labels near where a transition put the player (eg, the stairs back down) are left alone. Done once the
	/// player jumped (30+ cells) or the area changed after a click.
	/// </summary>
	public class TakeTransition : Goal {
		public readonly string LabelText;
		public float Reach = 12;
		private bool clicked;
		private Offsets.Vector2i clickedAt;
		private uint clickedHash;
		private bool done;
		private long lastClick;
		// where transitions put the player, in this area instance
		private static uint arrivalsHash;
		private static readonly List<Offsets.Vector2i> arrivals = new List<Offsets.Vector2i>();

		public TakeTransition(string labelText) => LabelText = labelText;
		public override string Name => $"Take the {LabelText}";
		public override void Reset() { base.Reset(); clicked = false; }

		private static uint AreaHash => PoEMemory.GameRoot?.InGameState?.WorldData?.AreaHash ?? 0;

		public override bool CheckDone(CombatContext ctx) {
			if ( done ) return true;
			if ( !clicked ) return false;
			if ( AreaHash != clickedHash || Combat.Distance(ctx.Grid, clickedAt) > 30 ) {
				done = true;
				if ( arrivalsHash != AreaHash ) { arrivalsHash = AreaHash; arrivals.Clear(); }
				arrivals.Add(ctx.Grid);
				Log($"Campaign: took the {LabelText}, now at {ctx.Grid}");
			}
			return done;
		}

		private (LabelOnGround label, Offsets.Vector2i pos) Find(CombatContext ctx) {
			if ( arrivalsHash != AreaHash ) { arrivalsHash = AreaHash; arrivals.Clear(); }
			return (GetUI()?.LabelsOnGround?.GetAllLabels() ?? Enumerable.Empty<LabelOnGround>())
				.Where(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), LabelText, StringComparison.OrdinalIgnoreCase))
				.Select(l => (label: l, pos: l.Item?.GetComponent<Positioned>()))
				.Where(x => IsValid(x.pos) && !arrivals.Any(a => Combat.Distance(a, x.pos.GridPos) < 25))
				.Select(x => (x.label, x.pos.GridPos))
				.OrderBy(x => Combat.Distance(ctx.Grid, x.GridPos))
				.FirstOrDefault();
		}

		protected override IState Step(CombatContext ctx) {
			var (label, pos) = Find(ctx);
			if ( label == null ) return Search(ctx, $"the {LabelText}");
			if ( !IsNear(ctx, pos, Reach, out _, out float d) ) {
				Detail = $"{d:F0} cells away";
				return GoTo(LabelText, pos, Reach);
			}
			ctx.Driver.StopWalking($"taking the {LabelText}");
			if ( Time.ElapsedMilliseconds - lastClick < 2000 || !(Ground.ClickLabel(label, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			clicked = true;
			clickedAt = ctx.Grid;
			clickedHash = AreaHash;
			Detail = "clicking it";
			return click;
		}
	}

	/// <summary>
	/// Activate something by its label (eg, the "Dark Altar"): walk to it and click it. Done once clicked and its label is
	/// gone (or it is not targetable), or after MaxClicks.
	/// </summary>
	public class Activate : Goal {
		public readonly string LabelText;
		public float Reach = 12;
		public int MaxClicks = 3;
		private int clicks;
		private long lastClick;
		public Activate(string labelText) => LabelText = labelText;
		public override string Name => $"Activate the {LabelText}";
		public override bool ProvesEarlier => true;
		public override void Reset() { base.Reset(); }

		private LabelOnGround Find() => GetUI()?.LabelsOnGround?.GetAllLabels()
			.FirstOrDefault(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), LabelText, StringComparison.OrdinalIgnoreCase));

		public override bool CheckDone(CombatContext ctx) {
			if ( clicks == 0 ) return false;
			var label = Find();
			if ( label == null || !(label.Item?.GetComponent<Targetable>()?.IsTargetable ?? true) ) return true;
			return clicks >= MaxClicks && Time.ElapsedMilliseconds - lastClick > 2000;
		}

		protected override IState Step(CombatContext ctx) {
			var label = Find();
			var pos = label?.Item?.GetComponent<Positioned>();
			if ( label == null || !IsValid(pos) ) return Search(ctx, $"the {LabelText}");
			if ( !IsNear(ctx, pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"{d:F0} cells away";
				return GoTo(LabelText, pos.GridPos, Reach);
			}
			if ( FightFirst(ctx) is IState fight ) return fight;
			ctx.Driver.StopWalking($"activating the {LabelText}");
			if ( Time.ElapsedMilliseconds - lastClick < 2000 || !(Ground.ClickLabel(label, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			clicks += 1;
			Detail = $"clicked it {clicks} times";
			return click;
		}
	}

	/// <summary>
	/// Talk to an NPC: walk near it, click it (its label, or where it stands), and once its options show, click the
	/// option IsOption picks (eg, "Mercy Mission Reward"); with no IsOption, close them (Escape) once they show, or once
	/// the NPC's overhead icon (the quest marker) is gone. Hands back when done, or after MaxClicks clicks without options.
	/// </summary>
	public class TalkToNpc : CampaignState {
		private readonly string npcName;
		private readonly IntPtr npcAddress;
		private readonly Func<string, bool> isOption;
		private readonly string optionLabel;
		public float Reach = 15;
		public int MaxClicks = 5;
		private int clicks;
		private long lastClick;

		/// <param name="npc">Its name (the end of its metadata path, eg "Nessa").</param>
		/// <param name="isOption">Which option to click; null to just talk (eg, to hear about a new quest).</param>
		public TalkToNpc(string npc, Func<string, bool> isOption = null, string optionLabel = null) { npcName = npc; this.isOption = isOption; this.optionLabel = optionLabel; }
		/// <param name="npc">The NPC entity.</param>
		public TalkToNpc(Entity npc, Func<string, bool> isOption = null, string optionLabel = null) {
			npcAddress = npc.Address;
			npcName = npc.Path?.Substring(npc.Path.LastIndexOf('/') + 1);
			this.isOption = isOption; this.optionLabel = optionLabel;
		}
		public override string Name => $"Talk to {npcName}{(optionLabel == null ? "" : $": {optionLabel}")}";
		public override void OnCancel() => Context?.Driver.StopWalking("cancelled");

		private Entity Npc() => npcAddress != IntPtr.Zero
			? GetEntities()?.FirstOrDefault(e => e?.Address == npcAddress)
			: Town.FindNPC(npcName);

		protected override IState Tick(CombatContext ctx, long dt) {
			var options = Town.NpcOptions;
			if ( Town.IsOpen(options) ) {
				ctx.Driver.StopWalking($"talking to {npcName}");
				if ( isOption == null ) { Detail = "closing the dialog"; return Inputs.Press(VKey.Escape, Next); }
				var option = Town.FindByText(options, isOption);
				if ( option == null ) {
					Detail = $"no option {optionLabel} (shows: {string.Join(", ", Town.Texts(options))})";
					Log($"Campaign: {npcName} has no option {optionLabel} (shows: {string.Join(", ", Town.Texts(options))})");
					return Inputs.Press(VKey.Escape, Next);
				}
				Detail = $"clicking {option.Text?.Trim()}";
				return Inputs.Click(option.GetClientRect(), new Delay(300, Next));
			}
			var npc = Npc();
			if ( npc == null ) { Detail = $"{npcName} is not here"; return Next; }
			if ( isOption == null && clicks > 0 && !(npc.GetComponent<NPC>()?.HasIconOverhead ?? false) ) return Next; // (heard it)
			var pos = npc.GetComponent<Positioned>();
			if ( !IsValid(pos) ) return this;
			if ( !Navigate.IsNear(ctx, pos.GridPos, Reach, out _, out float d) ) {
				Detail = $"{d:F0} cells away";
				return Call(new Navigate(npcName, _ => pos.GridPos, Reach) { FightRadius = -1 });
			}
			ctx.Driver.StopWalking($"talking to {npcName}");
			if ( Time.ElapsedMilliseconds - lastClick < 1000 ) return this;
			if ( clicks >= MaxClicks ) { Detail = $"clicked {npcName} {clicks} times, no options showed"; Log($"Campaign: {Detail}"); return Next; }
			if ( !(Ground.ClickEntity(npc, this) is IState click) ) return this;
			lastClick = Time.ElapsedMilliseconds;
			clicks += 1;
			Detail = $"clicking {npcName}";
			return click;
		}
	}

	/// <summary>
	/// In town: talk to each NPC with something to say (a quest marker over its head), so the quest tracker has every
	/// quest in progress. Done when none is left (each is tried at most twice per area instance), or not in a town.
	/// </summary>
	public class TalkToQuestGivers : Goal {
		private readonly Dictionary<IntPtr, int> tries = new Dictionary<IntPtr, int>();
		public override string Name => "Talk to the NPCs with something to say";
		public override bool Sticky => false;
		public override void Reset() { base.Reset(); tries.Clear(); }

		/// <summary>The NPCs here with a quest marker over their heads (not tried twice yet).</summary>
		public IEnumerable<Entity> Waiting() => GetEntities()?.Where(e => e?.Path is string path && path.StartsWith("Metadata/NPC/")
			&& (e.GetComponent<NPC>()?.HasIconOverhead ?? false) && !(tries.TryGetValue(e.Address, out int n) && n >= 2)) ?? Enumerable.Empty<Entity>();

		public override bool CheckDone(CombatContext ctx) => !(GetCurrentArea()?.IsTown ?? false) || !Waiting().Any();

		protected override IState Step(CombatContext ctx) {
			var npc = Waiting().OrderBy(e => Combat.Distance(ctx.Grid, e.GetComponent<Positioned>()?.GridPos ?? ctx.Grid)).FirstOrDefault();
			if ( npc == null ) return this;
			tries.TryGetValue(npc.Address, out int n);
			tries[npc.Address] = n + 1;
			Detail = $"{Waiting().Count()} to talk to";
			return Call(new TalkToNpc(npc));
		}
	}

	/// <summary>
	/// What to do for one quest, while it shows in the quest tracker (see FinishAllQuests): Goal, while Active says so
	/// of the quest's current step (by default: any step but "Talk to X for your reward" and "Travel to X", which
	/// FinishAllQuests handles for every quest).
	/// </summary>
	public sealed class QuestHandler {
		public readonly string Quest;
		public readonly Goal Goal;
		public Func<string, bool> Active = step => step != null
			&& !step.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase)
			&& !step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase);
		public QuestHandler(string quest, Goal goal) { Quest = quest; Goal = goal; }
	}

	/// <summary>
	/// Finish the quests of an act, as the quest tracker shows them (a quest only has goals while it is there). In
	/// town: talk to the NPCs with something to say (TalkToQuestGivers), take the rewards waiting (TakeQuestRewards),
	/// and keep the gear up (flask, gems, skills: see the Upkeep goals). A quest whose step is "Travel to [town]" (eg, to
	/// be given it): go to town; "Travel to [another area]" (eg, The Way Forward: "Travel to Prisoner's Gate"): go there.
	/// be given it): go to town. A quest with a handler (see QuestHandler), at a step the handler does: its goal.
	/// Done when none of these is left; quests with no handler are listed, not done.
	/// </summary>
	public class FinishAllQuests : Goal {
		public readonly int Act;
		public readonly string TownName;
		public readonly List<QuestHandler> Handlers = new List<QuestHandler>();
		public readonly List<Goal> Upkeep = new List<Goal>();
		private readonly TalkToQuestGivers givers = new TalkToQuestGivers();
		private readonly TakeQuestRewards rewards;
		private readonly EnterArea toTown;
		private readonly Dictionary<string, EnterArea> travels = new Dictionary<string, EnterArea>(StringComparer.OrdinalIgnoreCase);

		/// <summary>The area a step "Travel to X" names, or null.</summary>
		public static string TravelTarget(string step) =>
			step != null && step.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) ? step.Substring("Travel to ".Length).Trim().TrimEnd('.') : null;

		/// <summary>Is the area called name this act's town (its name, without the act)?</summary>
		public bool IsTown(string name) => name != null && TownName != null
			&& (string.Equals(name, TownName, StringComparison.OrdinalIgnoreCase) || TownName.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase));

		// the goal to go to an area a quest step names (one per area)
		private EnterArea TravelGoal(string area) {
			if ( !travels.TryGetValue(area, out var g) ) travels[area] = g = new EnterArea(area);
			return g;
		}
		/// <summary>The goal working on now (after the last choice), or null.</summary>
		public Goal Current { get; private set; }

		public FinishAllQuests(int act, string townName, Func<IReadOnlyDictionary<string, string>> rewardOverrides) {
			Act = act; TownName = townName;
			rewards = new TakeQuestRewards(rewardOverrides) { Where = townName };
			toTown = new EnterArea(townName);
		}
		public override string Name => $"Finish all quests (Act {Act})";
		public override bool Sticky => false;
		public override IEnumerable<Goal> Children => new Goal[] { givers, rewards }.Concat(Upkeep).Concat(travels.Values).Concat(Handlers.Select(h => h.Goal));
		public override void Reset() { base.Reset(); foreach ( var g in Children ) g.Reset(); }

		/// <summary>The quests in the tracker no handler does anything for (at their current step).</summary>
		public IEnumerable<string> Unhandled() => Town.QuestNames().Where(q => {
			var step = Town.QuestStep(q);
			if ( Town.RewardNpc(q) != null || (step?.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) ?? false) ) return false;
			return !Handlers.Any(h => string.Equals(h.Quest, q, StringComparison.OrdinalIgnoreCase));
		});

		/// <summary>What it would do next for a quest in the tracker, as text (eg, "talk to Nessa for the reward").</summary>
		public string NextFor(string quest) {
			var step = Town.QuestStep(quest);
			if ( Town.RewardNpc(quest) is string npc ) return $"talk to {npc} in {TownName} for the reward";
			if ( TravelTarget(step) is string area ) return IsTown(area) ? $"go to {TownName}, and talk to the NPCs with something to say" : $"go to {area}";
			var h = Handlers.FirstOrDefault(x => string.Equals(x.Quest, quest, StringComparison.OrdinalIgnoreCase));
			if ( h == null ) return "nothing: no goals known for it yet";
			if ( !h.Active(step) ) return $"nothing at this step ({h.Goal.Name} does other steps)";
			var leaf = h.Goal is Sequence seq ? seq.Goals.FirstOrDefault(g => !g.IsComplete) : h.Goal;
			return $"{h.Goal.Name}{(leaf == null || leaf == h.Goal ? "" : $": {leaf.Name}{(leaf.Where == null ? "" : $" (in {leaf.Where})")}")}";
		}

		/// <summary>The goal to work on now, or null if nothing is left.</summary>
		public Goal Choose(CombatContext ctx) {
			var quests = Town.QuestNames();
			bool inTown = GetCurrentArea()?.IsTown ?? false;
			if ( inTown ) {
				if ( !givers.IsDone(ctx) ) return givers;
				if ( !rewards.IsDone(ctx) ) return rewards;
				foreach ( var g in Upkeep ) if ( !g.IsDone(ctx) ) return g;
			}
			if ( quests.Any(q => Town.RewardNpc(q) != null) ) return rewards;
			foreach ( var area in quests.Select(q => TravelTarget(Town.QuestStep(q))).Where(a => a != null) ) {
				if ( IsTown(area) ) { if ( !inTown ) return toTown; }
				else if ( !GoToArea.Is(GetCurrentArea(), area) ) return TravelGoal(area);
			}
			foreach ( var h in Handlers ) {
				if ( !quests.Any(q => string.Equals(q, h.Quest, StringComparison.OrdinalIgnoreCase)) || !h.Active(Town.QuestStep(h.Quest)) ) continue;
				if ( h.Goal is Sequence seq ? seq.Select(ctx) != null : !h.Goal.IsDone(ctx) ) return h.Goal;
			}
			return null;
		}

		public override bool CheckDone(CombatContext ctx) {
			Current = Choose(ctx);
			if ( Current == null ) Detail = Unhandled().Any() ? $"no goals known yet for: {string.Join(", ", Unhandled())}" : "";
			return Current == null;
		}

		protected override IState Step(CombatContext ctx) {
			Current = Choose(ctx);
			if ( Current == null ) return this;
			Detail = Current.Name;
			return Call(Current);
		}
	}
}
