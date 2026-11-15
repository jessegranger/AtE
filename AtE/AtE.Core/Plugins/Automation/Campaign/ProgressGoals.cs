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

		/// <summary>
		/// Read it without opening it: the AreaInstanceUI keeps its element tree, with each tab's visibility, while it
		/// is closed (3.29.4). False (nothing read) if no "Part" tab shows there (eg, the tree is not built yet). It is
		/// not kept up to date while closed: what it shows is as of the last time the panel was open (so the run's
		/// ReadCampaignProgress opens it instead; only the memory server's /progress reads it this way).
		/// </summary>
		public static bool TryReadClosed() {
			var panel = GetUI()?.AreaInstanceUi;
			if ( !IsValid(panel) ) return false;
			if ( !UiSearch.Texts(panel.GetChild(2, 0, 0)).Any(t => t.StartsWith("Part", StringComparison.OrdinalIgnoreCase)) ) return false;
			Read(panel);
			return true;
		}

		/// <summary>Give up reading it (eg, the panel did not open): go by the current area's act.</summary>
		public static void Failed(string why) {
			Scanned = true;
			Summary = $"could not read it ({why}): going by the current act";
			Log($"Campaign: progress: {Summary}");
		}

		/// <summary>Forget it (a new run reads it again).</summary>
		/// <summary>
		/// True once the inventory panel was opened (and shut again) in this run, so what the game shows of the worn items
		/// is up to date (see ReadCampaignProgress; the looter goes by them, see Looter.RareOrEmptySlot).
		/// </summary>
		public static bool EquipmentRefreshed { get; internal set; }

		/// <summary>True once /passives was read in this run (or given up on).</summary>
		public static bool PassivesRead { get; internal set; }
		/// <summary>
		/// The quests whose passive point the character has (a Skill Book's), from /passives: eg "(1 from The Dweller of the
		/// Deep)". Those quests are done.
		/// </summary>
		public static HashSet<string> PassiveQuests { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Has the character the passive point of quest (see PassiveQuests)?</summary>
		public static bool HasPassiveFrom(string quest) => quest != null && PassiveQuests.Contains(quest);

		/// <summary>
		/// Read /passives' reply from the chat's last messages: from the last one with "total Passive Skill Points", each
		/// line "(N from [quest])" names a quest done. False if no reply is there.
		/// </summary>
		public static bool ReadPassives(List<string> messages) {
			int at = messages.FindLastIndex(m => m.Contains("total Passive Skill Points", StringComparison.OrdinalIgnoreCase));
			if ( at < 0 ) return false;
			var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach ( var line in messages.Skip(at).SelectMany(msg => msg.Split('\n')).Select(l => l.Trim()) ) {
				var match = PassiveFromQuest.Match(line);
				if ( match.Success ) found.Add(match.Groups[1].Value.Trim());
			}
			PassiveQuests = found;
			PassivesRead = true;
			Log($"Campaign: /passives: the passive points of {(found.Count == 0 ? "no quests" : string.Join(", ", found))}");
			return true;
		}
		private static readonly System.Text.RegularExpressions.Regex PassiveFromQuest = new System.Text.RegularExpressions.Regex(@"^\(\d+ from (.+)\)$");

		public static void Forget() {
			PassivesRead = false;
			PassiveQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Scanned = false;
			EquipmentRefreshed = false;
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
				if ( presses == 0 && TryReadClosed() ) return finish; // (no keys needed)
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
	/// At the start of a run: open the AreaInstanceUI (U) and the inventory (I) together, read the campaign progress
	/// (see CampaignProgress), then close the inventory (I) and the AreaInstanceUI (U). The panel is not read while
	/// closed: its closed tree is only as of when it was last open (eg, an act behind if the player went on without
	/// opening it since). The inventory is opened so the game's view of the worn items is up to date (the looter picks
	/// up normal items for a slot it thinks is empty, see Looter.RareOrEmptySlot).
	/// </summary>
	public class ReadCampaignProgress : Goal {
		public VKey Key = VKey.U;
		public VKey InventoryKey = VKey.I;
		/// <summary>How long to keep both open before reading and closing them.</summary>
		public long OpenMs = 500;
		/// <summary>The least time between key presses (for the panel to show or hide).</summary>
		public long PressMs = 700;
		private long lastPress, bothOpenAt;
		private int tries, inventoryTries;
		public override string Name => "Read the campaign progress";
		public override bool Sticky => false;
		public override bool UsesInventory => true; // (it opens and closes the inventory itself)
		public override void Reset() { base.Reset(); tries = inventoryTries = passivesTries = 0; bothOpenAt = passivesSentAt = 0; }
		// did it open the panel / the inventory itself (so it is to close them): the panel is also the waypoint's, and one
		// a waypoint goal opened must be left open (closing it here made a waypoint goal and this one take turns)
		private bool openedIt, openedInventory;

		private static bool Read => CampaignProgress.Scanned && CampaignProgress.EquipmentRefreshed;

		public override bool CheckDone(CombatContext ctx) {
			if ( !Read ) return false;
			if ( openedIt && WaypointPanel.Panel() != null ) return false;
			if ( openedInventory && BackpackIsOpen() ) return false;
			if ( !CampaignProgress.PassivesRead ) return false;
			openedIt = openedInventory = false;
			return true;
		}

		protected override IState Step(CombatContext ctx) {
			ctx.Driver.StopWalking("reading the campaign progress");
			var panel = WaypointPanel.Panel();
			bool inventory = BackpackIsOpen();
			long now = Time.ElapsedMilliseconds;
			bool paced = now - lastPress >= PressMs;
			if ( !Read ) {
				// 1. open the AreaInstanceUI (U)
				if ( panel == null ) {
					Detail = "opening the AreaInstanceUI";
					if ( !paced ) return this;
					if ( ++tries > 3 ) {
						CampaignProgress.Failed($"{Key} did not open the AreaInstanceUI");
						CampaignProgress.EquipmentRefreshed = true;
						return this;
					}
					return Press(Key, now, () => openedIt = true);
				}
				// 2. open the inventory (I), with the panel open
				if ( !inventory ) {
					Detail = "opening the inventory";
					if ( !paced ) return this;
					if ( ++inventoryTries > 3 ) {
						Log($"Campaign: {InventoryKey} did not open the inventory: going on with the worn items as they read");
						CampaignProgress.Read(panel);
						CampaignProgress.EquipmentRefreshed = true;
						return this;
					}
					bothOpenAt = 0;
					return Press(InventoryKey, now, () => openedInventory = true);
				}
				// 3. both open: a moment, then read
				if ( bothOpenAt == 0 ) bothOpenAt = now;
				Detail = "reading the campaign progress and the equipment";
				if ( now - bothOpenAt < OpenMs ) return this;
				CampaignProgress.Read(panel);
				CampaignProgress.EquipmentRefreshed = true;
				Log($"Campaign: worn (with the inventory open): {string.Join(", ", Equipment.WornSlots().Where(s => s.inventory.Items.Any(i => i?.Entity != null)).Select(s => s.name))}");
				lastPress = now - PressMs; // (close at once)
				return this;
			}
			Detail = CampaignProgress.Summary;
			if ( !paced ) return this;
			// 4. close the inventory (I), then 5. the AreaInstanceUI (U), each only if it opened it
			if ( openedInventory && inventory ) return Press(InventoryKey, now, null);
			if ( openedIt && panel != null ) return Press(Key, now, null);
			// 6. /passives: which quests' passive points the character has (they are done; see CampaignProgress.PassiveQuests)
			if ( !CampaignProgress.PassivesRead ) {
				if ( passivesSentAt == 0 ) {
					if ( ++passivesTries > 2 ) {
						Log("Campaign: /passives gave no reply: going on without it");
						CampaignProgress.PassivesRead = true;
						return this;
					}
					Detail = "sending /passives";
					passivesSentAt = now;
					return ChatCommand.Send("/passives", this);
				}
				Detail = "reading /passives' reply";
				if ( now - passivesSentAt < PassivesWaitMs ) return this;
				if ( CampaignProgress.ReadPassives(ChatCommand.LastMessages(30)) ) return this;
				if ( now - passivesSentAt > 3 * PassivesWaitMs ) passivesSentAt = 0; // (no reply: send it again)
			}
			return this;
		}

		/// <summary>How long after sending /passives to read its reply.</summary>
		public long PassivesWaitMs = 1000;
		private long passivesSentAt;
		private int passivesTries;

		private IState Press(VKey key, long now, Action pressed) {
			lastPress = now;
			pressed?.Invoke();
			return Inputs.Press(key, this);
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
	/// Stop the routine once, with a reason (eg, at a Trial of Ascendancy, until its goals are written): done after it
	/// stopped, for as long as the overlay runs (switching the routine on again carries on past it).
	/// </summary>
	public class StopForHelp : Goal {
		private readonly string why;
		private bool stopped;
		public StopForHelp(string why) => this.why = why;
		public override string Name => $"Stop: {why}";
		public override bool CheckDone(CombatContext ctx) => stopped;
		protected override IState Step(CombatContext ctx) {
			stopped = true;
			Log($"Campaign: stopping: {why}");
			ctx.Driver.Stop(why);
			return this;
		}
	}

	/// <summary>
	/// Go to an area: by the named waypoint (eg, "The Coast"), if this area has a
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
			if ( Waypoint != null && !noWaypoint && WaypointPanel.IsKnownLocked(Waypoint) ) {
				noWaypoint = true;
				Log($"Campaign: the waypoint {Waypoint} is not unlocked (the waypoint panel's tree): walking to {Area}");
			}
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
	/// Talk to an NPC out in an area (eg, Captain Fairgraves, in The Ship Graveyard): look for it (Search), then talk to
	/// it (TalkToNpc, closing its options). Done after MaxTalks talks, or by DoneWhen (eg, the quest moving on).
	/// </summary>
	public class TalkTo : Goal {
		public readonly string Label;
		private readonly Func<Entity, bool> match;
		public int MaxTalks = 3;
		private int talks;
		/// <param name="label">Its name, for the status line (eg, "Captain Fairgraves").</param>
		/// <param name="match">Which entity it is (see NpcNamed).</param>
		public TalkTo(string label, Func<Entity, bool> match) { Label = label; this.match = match; }
		public override string Name => $"Talk to {Label}";
		public override void Reset() { base.Reset(); talks = 0; }
		public override bool CheckDone(CombatContext ctx) => talks >= MaxTalks;

		/// <summary>
		/// An NPC by name: the end of its metadata path (eg, "Nessa"; spaces ignored), or its name (Render.Name) has the
		/// name in it (eg, "Captain Fairgraves").
		/// </summary>
		public static Func<Entity, bool> NpcNamed(string name) => e => e?.Path is string path && path.StartsWith("Metadata/NPC/")
			&& (string.Equals(path.Substring(path.LastIndexOf('/') + 1), name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
				|| (e.GetComponent<Render>()?.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false));

		protected override IState Step(CombatContext ctx) {
			var npc = GetEntities()?.FirstOrDefault(e => e != null && match(e));
			if ( npc == null ) return Search(ctx, Label);
			talks += 1;
			Detail = $"talking to {Label} ({talks} of {MaxTalks})";
			return Call(new TalkToNpc(npc));
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
		private long lastClick, lastContinue;

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
				// a reward the NPC still has (eg, one skipped: the quest is gone from the tracker, and nothing else tells of it):
				// its option ("[quest] Reward") is taken first, whatever the talk was for (the reward window is TakeQuestRewards')
				if ( (isOption == null || Town.FindByText(options, isOption) == null)
					&& Town.FindByText(options, t => t.EndsWith(" Reward", StringComparison.OrdinalIgnoreCase)) is Element owed ) {
					Detail = $"clicking {owed.Text?.Trim()}";
					Log($"Campaign: {npcName} has a reward waiting: clicking {owed.Text?.Trim()}");
					return Inputs.Click(owed.GetClientRect(), new Delay(300, Next));
				}
				if ( isOption == null ) { Detail = "closing the dialog"; return Inputs.Press(VKey.Escape, Next); }
				var option = Town.FindByText(options, isOption);
				if ( option == null && Town.NpcContinue() is Element more ) {
					// story text first: Continue to the options (or the reward window)
					if ( Time.ElapsedMilliseconds - lastContinue < 800 ) return this;
					lastContinue = Time.ElapsedMilliseconds;
					Detail = "clicking Continue";
					return Inputs.Click(more.GetClientRect(), this);
				}
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

		/// <summary>NPCs (the end of their metadata path) whose marker is not a quest: Einhar's (the bestiary league's) is his own.</summary>
		public static readonly List<string> NotQuestGivers = new List<string> { "/Einhar" };

		/// <summary>The NPCs here with a quest marker over their heads (not tried twice yet).</summary>
		public IEnumerable<Entity> Waiting() => GetEntities()?.Where(e => e?.Path is string path && path.StartsWith("Metadata/NPC/")
			&& !NotQuestGivers.Any(n => path.EndsWith(n, StringComparison.OrdinalIgnoreCase))
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
		public Func<string, bool> Active = DefaultActive;
		/// <summary>Does a handler do a quest step, by default: any step but a trip ("Travel to X", "Enter town") or a reward.</summary>
		public static bool DefaultActive(string step) => step != null
			&& !step.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase)
			&& !FinishAllQuests.IsToTown(step)
			&& !step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase);
		public QuestHandler(string quest, Goal goal) { Quest = quest; Goal = goal; }

		/// <summary>
		/// The quest is not in the quest tracker but is still to do (eg, Breaking Some Eggs before it starts, which is at
		/// the glyph wall: The Submerged Passage's waypoint is not unlocked). Null: a quest not in the tracker is not done.
		/// </summary>
		public Func<bool> Untracked;

		/// <summary>Does the handler work now: on the quest's step (tracker's quest, or null if it is not there).</summary>
		/// <summary>
		/// The town whose NPC gives the reward, when it is not the act's own (eg, The Way Forward, worked on in Act 2:
		/// Bestel, in Lioneye's Watch). Null: the act's town.
		/// </summary>
		public string RewardTown;

		/// <summary>The reward to take first, if the reward window offers it (eg, "Quicksilver Flask"); null: RewardChooser's choice.</summary>
		public string PreferredReward;

		/// <summary>
		/// NPCs the handler talks to out in an area (eg, Captain Fairgraves): a step "Talk to" one of them is the handler's,
		/// not a talk in town.
		/// </summary>
		public string[] FieldNpcs = Array.Empty<string>();

		/// <summary>Does the handler work now: not if the character has the quest's passive point (it is done, see CampaignProgress.PassiveQuests).</summary>
		public bool IsActive(string trackerQuest) => !CampaignProgress.HasPassiveFrom(Quest) && IsActiveByStep(trackerQuest);
		private bool IsActiveByStep(string trackerQuest) => trackerQuest != null ? Active(Town.QuestStep(trackerQuest)) : (Untracked?.Invoke() ?? false);

		/// <summary>A quest whose handler is not written yet: it does nothing (the quest counts as having no goals).</summary>
		public static QuestHandler Stub(string quest) => new QuestHandler(quest, new NotSpecified(quest)) { Active = _ => false };
		public bool IsStub => Goal is NotSpecified && !IsSkipped && DoneBy == null;

		/// <summary>
		/// A quest done by goals outside the quests (eg, The Siren's Cadence: the act's route kills Merveil and enters the
		/// forest). It does nothing itself; its "Travel to" steps and rewards are still done.
		/// </summary>
		public static QuestHandler Elsewhere(string quest, string doneBy) => new QuestHandler(quest, new NotSpecified(quest)) { Active = _ => false, DoneBy = doneBy };
		public string DoneBy { get; private set; }

		/// <summary>A quest not to do (eg, an optional one not worth the trip): nothing is done for it, not even its "Travel to" steps.</summary>
		public static QuestHandler Skip(string quest) => new QuestHandler(quest, new NotSpecified(quest)) { Active = _ => false, IsSkipped = true };
		public bool IsSkipped { get; private set; }

		/// <summary>Is the quest tracker's quest (eg, "The Dweller of the Deep (Optional)") this handler's (eg, "The Dweller of the Deep")?</summary>
		public bool IsFor(string trackerName) {
			if ( trackerName == null ) return false;
			const string optional = " (Optional)";
			if ( trackerName.EndsWith(optional, StringComparison.OrdinalIgnoreCase) ) trackerName = trackerName.Substring(0, trackerName.Length - optional.Length);
			return string.Equals(trackerName.Trim(), Quest, StringComparison.OrdinalIgnoreCase);
		}
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
		private readonly Func<IReadOnlyDictionary<string, string>> rewardOverrides;
		// the rewards given in another town (see QuestHandler.RewardTown), by town
		private readonly Dictionary<string, TakeQuestRewards> rewardsElsewhere = new Dictionary<string, TakeQuestRewards>(StringComparer.OrdinalIgnoreCase);

		/// <summary>The NPC in town a quest's step sends the player to (see Town.RewardNpc), unless its handler talks to it out in an area (FieldNpcs).</summary>
		public string TownNpc(string quest) {
			var npc = Town.RewardNpc(quest);
			if ( npc == null ) return null;
			// (not those whose marker is not a quest, eg Einhar's Hunt's "Talk to Einhar")
			if ( TalkToQuestGivers.NotQuestGivers.Any(n => string.Equals(n.TrimStart('/'), npc, StringComparison.OrdinalIgnoreCase)) ) return null;
			// (a plain "Talk to X" only: "Meet X in town", a delivery or a reward is in town)
			string step = Town.QuestStep(quest) ?? "";
			bool plainTalk = step.StartsWith("Talk to ", StringComparison.OrdinalIgnoreCase) && !step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase);
			return plainTalk && Handlers.Any(h => h.IsFor(quest) && h.FieldNpcs.Any(f => string.Equals(f, npc, StringComparison.OrdinalIgnoreCase))) ? null : npc;
		}

		/// <summary>The reward a quest's handler prefers (see QuestHandler.PreferredReward), or null.</summary>
		public string PreferredRewardOf(string quest) => Handlers.FirstOrDefault(h => h.PreferredReward != null && h.IsFor(quest))?.PreferredReward;

		/// <summary>The other town where the quest's reward is given (see QuestHandler.RewardTown), or null for this act's town.</summary>
		public string RewardTownOf(string quest) => Handlers.FirstOrDefault(h => h.RewardTown != null && !IsTown(h.RewardTown) && h.IsFor(quest))?.RewardTown;

		private TakeQuestRewards RewardsIn(string town) {
			if ( !rewardsElsewhere.TryGetValue(town, out var g) ) {
				rewardsElsewhere[town] = g = new TakeQuestRewards(rewardOverrides) { Where = town, Quests = q => string.Equals(RewardTownOf(q), town, StringComparison.OrdinalIgnoreCase), Prefer = PreferredRewardOf };
			}
			return g;
		}
		private readonly EnterArea toTown;
		private readonly UsePortalScroll portalToTown;
		private readonly Dictionary<string, EnterArea> travels = new Dictionary<string, EnterArea>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Where a quest's "Travel to X" step really means, when X is not the area's name: (quest, X) to the area.
		/// The Siren's Cadence: "Travel to Merveil's Caverns" (the waypoint's name, in The Cavern of Wrath) means Merveil's
		/// lair, The Cavern of Anger: TravelTo takes the waypoint to The Cavern of Wrath if it can, and walks on from there.
		/// </summary>
		public static readonly Dictionary<(string quest, string place), string> TravelAliases =
			new Dictionary<(string quest, string place), string>() {
				{ ("The Siren's Cadence", "Merveil's Caverns"), "The Cavern of Anger" },
			};

		/// <summary>The area a step "Travel to X" of quest (the tracker's name, may be null) names, or null.</summary>
		public static string TravelTarget(string step, string quest = null) {
			if ( step == null || !step.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) ) return null;
			string place = step.Substring("Travel to ".Length).Trim().TrimEnd('.');
			if ( quest != null ) {
				string bare = quest.EndsWith(" (Optional)", StringComparison.OrdinalIgnoreCase) ? quest.Substring(0, quest.Length - " (Optional)".Length) : quest;
				foreach ( var kv in TravelAliases ) {
					if ( string.Equals(kv.Key.quest, bare, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(kv.Key.place, place, StringComparison.OrdinalIgnoreCase) ) return kv.Value;
				}
			}
			// a place of several levels (eg, Intruders in Black's "Travel to The Chamber of Sins"): its first level. Also when
			// the name alone is an area of another act (eg, Through Sacred Ground's "Travel to the Crypt", 3.29.4: Act 7 has
			// "The Crypt", reached through The Wetlands and the acts after it, while Act 2's is "The Crypt Level 1")
			var here = GetCurrentArea();
			if ( here != null && !GoToArea.Is(here, place) ) {
				bool InThisAct(List<WorldArea> route) => route != null && route[route.Count - 1].Act == here.Act;
				if ( !InThisAct(WorldArea.FindRoute(here, place)) && InThisAct(WorldArea.FindRoute(here, $"{place} Level 1")) ) return $"{place} Level 1";
			}
			return place;
		}

		/// <summary>A quest step that means going to town without naming it (eg, Enemy at the Gate's "Enter town for your reward").</summary>
		public static bool IsToTown(string step) => step != null
			&& (step.StartsWith("Enter town", StringComparison.OrdinalIgnoreCase) || step.StartsWith("Return to town", StringComparison.OrdinalIgnoreCase));

		/// <summary>Where a quest's step sends the player: the area of a "Travel to X", this act's town for "Enter town ...", or null.</summary>
		public string Destination(string step, string quest = null) => IsToTown(step) ? TownName : TravelTarget(step, quest);

		/// <summary>
		/// Is the area another level of the same place as the one called name (eg, The Chamber of Sins Level 1 and Level 2):
		/// a quest's work there is "here" (Intruders in Black, in Level 1, goes on to Level 2 before another quest's trip).
		/// </summary>
		public static bool SamePlace(WorldArea here, string name) {
			static string Place(string n) => n == null ? null : System.Text.RegularExpressions.Regex.Replace(n.Trim(), @"\s+Level\s+\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			string a = Place(here?.Name), b = Place(name);
			return a != null && b != null && a != here.Name && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}

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
			rewards = new TakeQuestRewards(rewardOverrides) { Where = townName, Quests = q => RewardTownOf(q) == null && TownNpc(q) != null, Prefer = PreferredRewardOf };
			this.rewardOverrides = rewardOverrides;
			toTown = new EnterArea(townName);
			portalToTown = new UsePortalScroll(townName);
		}
		public override string Name => $"Finish all quests (Act {Act})";
		public override bool Sticky => false;
		public override IEnumerable<Goal> Children => new Goal[] { givers, rewards, toTown, portalToTown }.Concat(Upkeep).Concat(travels.Values).Concat(rewardsElsewhere.Values).Concat(Handlers.Select(h => h.Goal));
		public override void Reset() { base.Reset(); foreach ( var g in Children ) g.Reset(); }

		/// <summary>The quests in the tracker no handler does anything for (at their current step).</summary>
		public IEnumerable<string> Unhandled() => Town.QuestNames().Where(q => !IsSkipped(q)).Where(q => {
			var step = Town.QuestStep(q);
			if ( TownNpc(q) != null || Destination(step, q) != null ) return false;
			return !Handlers.Any(h => h.IsFor(q) && !h.IsStub);
		});

		/// <summary>Is the quest tracker's quest one to skip (see QuestHandler.Skip)?</summary>
		public bool IsSkipped(string quest) => Handlers.Any(h => h.IsSkipped && h.IsFor(quest));

		/// <summary>What it would do next for a quest in the tracker, as text (eg, "talk to Nessa for the reward").</summary>
		public string NextFor(string quest) {
			if ( IsSkipped(quest) ) return "nothing: skipped";
			var step = Town.QuestStep(quest);
			if ( TownNpc(quest) is string npc ) return step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase)
				? $"talk to {npc} in {TownName} for the reward" : $"talk to {npc} in {TownName}";
			if ( Destination(step, quest) is string area ) return IsTown(area) ? $"go to {TownName}, and talk to the NPCs with something to say" : $"go to {area}";
			var h = Handlers.FirstOrDefault(x => x.IsFor(quest));
			if ( h == null ) return "nothing: no goals known for it yet";
			if ( h.IsStub ) return "nothing: its handler is a stub (not written yet)";
			if ( h.DoneBy != null && !h.Active(step) ) return $"nothing here: {h.DoneBy} does it";
			if ( !h.Active(step) ) return $"nothing at this step ({h.Goal.Name} does other steps)";
			var leaf = h.Goal is Sequence seq ? seq.Goals.FirstOrDefault(g => !g.IsComplete) : h.Goal;
			return $"{h.Goal.Name}{(leaf == null || leaf == h.Goal ? "" : $": {leaf.Name}{(leaf.Where == null ? "" : $" (in {leaf.Where})")}")}";
		}

		/// <summary>The goal to work on now, or null if nothing is left.</summary>
		public Goal Choose(CombatContext ctx) {
			var quests = Town.QuestNames();
			bool inTown = GetCurrentArea()?.IsTown ?? false;
			if ( inTown ) {
				// (a reward window open, eg from a reward option found while talking: take it first; in another act's town, with
				// that town's goal: this act's has Where this act's town, eg Bestel's for The Way Forward in Lioneye's Watch)
				if ( Town.IsOpen(Town.QuestRewardWindow) ) {
					string hereName = GetCurrentArea()?.Name;
					var open = IsTown(hereName) || hereName == null ? rewards : RewardsIn(hereName);
					if ( !open.IsDone(ctx) ) return open;
				}
				if ( !givers.IsDone(ctx) ) return givers;
				if ( !rewards.IsDone(ctx) ) return rewards;
				foreach ( var g in Upkeep ) if ( !g.IsDone(ctx) ) return g;
			}
			// a quest's own goal to do here, in the area the player is in (eg, The Way Forward in Prisoner's Gate), before
			// going to town for a reward or traveling elsewhere for another quest's "Travel to X" (eg, Breaking Some Eggs:
			// The Submerged Passage's waypoint, before going to town for the reward)
			var here = GetCurrentArea();
			foreach ( var h in Handlers ) {
				if ( !h.IsActive(quests.FirstOrDefault(h.IsFor)) ) continue;
				var next = h.Goal is Sequence s ? s.Select(ctx) : (h.Goal.IsDone(ctx) ? null : h.Goal);
				if ( next?.Where != null && (GoToArea.Is(here, next.Where) || SamePlace(here, next.Where)) ) return h.Goal;
			}
			if ( quests.Any(q => TownNpc(q) != null && RewardTownOf(q) == null) && !rewards.IsDone(ctx) ) return rewards; // (a reward given up on is passed by)
			// a reward given in another town (eg, The Way Forward's, by Bestel in Lioneye's Watch): go there for it
			foreach ( var town in quests.Where(q => TownNpc(q) != null).Select(RewardTownOf).Where(t => t != null).Distinct(StringComparer.OrdinalIgnoreCase) ) {
				var there = RewardsIn(town);
				if ( !there.IsDone(ctx) ) return there;
			}
			// (a handler that does a quest's "Travel to" step itself, eg Deal with the Bandits' "Travel to The Broken Bridge, The
			// Western Forest or The Wetlands", has it: no trip for it)
			foreach ( var area in quests.Where(q => !IsSkipped(q) && !Handlers.Any(h => h.IsFor(q) && h.Active(Town.QuestStep(q))))
				.Select(q => Destination(Town.QuestStep(q), q)).Where(a => a != null) ) {
				// (to town: by a Portal Scroll, with the routine's key, unless the chat said lately there are none; the backpack
				// is not looked in: see UsePortalScroll.CanTry)
				if ( IsTown(area) ) {
					if ( inTown ) continue;
					return UsePortalScroll.CanTry(ctx.Driver) ? portalToTown : toTown;
				}
				else if ( !GoToArea.Is(GetCurrentArea(), area) ) return TravelGoal(area);
			}
			foreach ( var h in Handlers ) {
				if ( !h.IsActive(quests.FirstOrDefault(h.IsFor)) ) continue;
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
