using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Helpers for town: finding NPCs, the windows they open (purchase, sell, quest rewards), and closing them.
	/// </summary>
	public static class Town {

		/// <summary>The NPC called name (eg, "Nessa": its metadata path ends in /Nessa), or null if it is not here.</summary>
		public static Entity FindNPC(string name) => GetEntities()?.FirstOrDefault(e =>
			e?.Path is string path && path.StartsWith("Metadata/NPC/")
			&& string.Equals(path.Substring(path.LastIndexOf('/') + 1), name, StringComparison.OrdinalIgnoreCase));

		public static bool IsOpen(Element window) => IsValid(window) && window.IsVisible;

		/// <summary>The options an NPC shows when clicked on (eg, "Purchase Items", "Sell Items", "Hillock Reward").</summary>
		public static Element NpcOptions => GetUI()?.NpcOptions;
		public static Element PurchaseWindow => GetUI()?.PurchaseWindow;
		public static Element SellWindow => GetUI()?.SellWindow;
		public static Element QuestRewardWindow => GetUI()?.QuestRewardWindow;

		/// <summary>The "Continue" under the open NpcOptions (it shows story text before the options, or before a quest reward window); null if none.</summary>
		public static Element NpcContinue() => IsOpen(NpcOptions) ? FindByText(NpcOptions, "Continue") : null;

		/// <summary>True if one of the windows an NPC option opens is open: purchase, sell, or quest reward.</summary>
		public static bool AnyWindowOpen => IsOpen(PurchaseWindow) || IsOpen(SellWindow) || IsOpen(QuestRewardWindow);

		/// <summary>Close the purchase, sell, and quest reward windows: a click at the top left corner of the screen.</summary>
		public static void CloseWindows() {
			Run(Inputs.Click(new Vector2(1, 1)));
		}

		/// <summary>The first visible element under root (or root) whose Text is text, depth first; null if none.</summary>
		public static Element FindByText(Element root, string text, int depth = 12) =>
			FindByText(root, t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase), depth);

		/// <summary>The first visible element under root (or root) whose Text matches, depth first; null if none.</summary>
		public static Element FindByText(Element root, Func<string, bool> match, int depth = 12) {
			if ( !IsValid(root) || !root.IsVisibleLocal ) return null;
			if ( root.Text?.Trim() is string text && text.Length > 0 && match(text) ) return root;
			if ( depth <= 0 ) return null;
			foreach ( var child in root.Children ) {
				var found = FindByText(child, match, depth - 1);
				if ( found != null ) return found;
			}
			return null;
		}

		/// <summary>The texts of the visible elements under root, for status lines.</summary>
		public static IEnumerable<string> Texts(Element root, int depth = 12) {
			if ( !IsValid(root) || !root.IsVisibleLocal ) yield break;
			var text = root.Text?.Trim();
			if ( !string.IsNullOrEmpty(text) ) yield return text;
			if ( depth <= 0 ) yield break;
			foreach ( var child in root.Children ) foreach ( var t in Texts(child, depth - 1) ) yield return t;
		}

		/// <summary>The rewards in the open quest reward window, each a clickable element (an item); empty if it is closed.</summary>
		public static List<Element> QuestRewards() {
			var window = QuestRewardWindow;
			if ( !IsOpen(window) ) return new List<Element>();
			return window.GetChild(5)?.GetChild(0)?.GetChild(0)?.Children.Where(IsValid).ToList() ?? new List<Element>();
		}

		/// <summary>The item element of a quest reward (an InventoryItem a little way under it), or null. (Not the one to click: see FindQuestReward.)</summary>
		public static InventoryItem RewardItemElement(Element reward, int depth = 3) {
			if ( !IsValid(reward) ) return null;
			var item = new InventoryItem(reward.Address);
			if ( item.Entity != null ) return item;
			if ( depth <= 0 ) return null;
			foreach ( var child in reward.Children ) {
				if ( RewardItemElement(child, depth - 1) is InventoryItem found ) return found;
			}
			return null;
		}

		/// <summary>The item a quest reward element offers, or null.</summary>
		public static Entity RewardItem(Element reward) => RewardItemElement(reward)?.Entity;

		/// <summary>
		/// The reward in the open quest reward window that is the gem (or item) called name, or null: the element to click
		/// (its item element, the InventoryItem's Parent.Parent, is positioned well below where it is drawn).
		/// </summary>
		public static Element FindQuestReward(string name) => QuestRewards().FirstOrDefault(r =>
			RewardItem(r) is Entity item && (Equipment.IsGem(item, name) || string.Equals(Equipment.ItemName(item), name, StringComparison.OrdinalIgnoreCase)));

		public static Element QuestTracker => GetUI()?.QuestTracker;

		/// <summary>
		/// The quest called name in the quest tracker (eg, "Enemy at the Gate"), or null if it is not shown: an element with
		/// the name at .Children[0].Children[0] and the current step at .Children[1].Children[0].Children[1].
		/// </summary>
		public static Element FindQuest(string name, Element root = null, int depth = 6) {
			root = root ?? QuestTracker;
			if ( !IsValid(root) || !root.IsVisibleLocal ) return null;
			if ( string.Equals(root.GetChild(0)?.GetChild(0)?.Text?.Trim(), name, StringComparison.OrdinalIgnoreCase) )
				return IsIgnoredStep(root.GetChild(1)?.GetChild(0)?.GetChild(1)?.Text?.Trim()) ? null : root;
			if ( depth <= 0 ) return null;
			foreach ( var child in root.Children ) {
				var found = FindQuest(name, child, depth - 1);
				if ( found != null ) return found;
			}
			return null;
		}

		/// <summary>The names of the quests shown in the quest tracker (each an element with its name at .Children[0].Children[0] and its step at .Children[1].Children[0].Children[1]).</summary>
		public static List<string> QuestNames() {
			var names = new List<string>();
			void Walk(Element e, int depth) {
				if ( !IsValid(e) || !e.IsVisibleLocal ) return;
				var name = e.GetChild(0)?.GetChild(0)?.Text?.Trim();
				var step = e.GetChild(1)?.GetChild(0)?.GetChild(1)?.Text?.Trim();
				if ( !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(step) ) { if ( !IsIgnoredStep(step) ) names.Add(name); return; }
				if ( depth <= 0 ) return;
				foreach ( var child in e.Children ) Walk(child, depth - 1);
			}
			Walk(QuestTracker, 6);
			return names;
		}

		// (eg, "Talk to Einhar in your Hideout": not something the campaign does)
		private static readonly Regex HideoutStep = new Regex("^Talk to .+ in your Hideout$", RegexOptions.IgnoreCase);

		/// <summary>
		/// A tracker step the routine ignores, as if its quest were not in the tracker (FindQuest, QuestStep and
		/// QuestNames leave it out): "Talk to [NPC] in your Hideout".
		/// </summary>
		public static bool IsIgnoredStep(string step) => step != null && HideoutStep.IsMatch(step);

		/// <summary>The current step of the quest called name (eg, "Talk to Tarkleigh for your reward"), or null.</summary>
		public static string QuestStep(string name) => FindQuest(name)?.GetChild(1)?.GetChild(0)?.GetChild(1)?.Text?.Trim();

		private static readonly Regex RewardStep = new Regex("^Talk to (.+?)(?: in town)? for your reward$", RegexOptions.IgnoreCase); // (eg, "Talk to Tasuni in town for your reward")
		// (a reward talked about but not taken: the step becomes just "Talk to Nessa", and her "Merveil Reward" option
		// opens the reward window again)
		private static readonly Regex TalkStep = new Regex("^Talk to ([^,.]+?)(?: in town)?$", RegexOptions.IgnoreCase); // (eg, "Talk to Dialla in town")
		// a quest item to hand over (eg, Mercy Mission's "Take the Medicine Chest to Nessa"): talking to the NPC does it
		private static readonly Regex DeliverStep = new Regex("^(?:Take|Bring|Return|Give) .+ to ([^,.]+)$", RegexOptions.IgnoreCase);
		// (eg, Intruders in Black's "Meet Helena in Town")
		private static readonly Regex MeetStep = new Regex("^Meet ([^,.]+?) in town$", RegexOptions.IgnoreCase);

		/// <summary>
		/// The NPC who has the reward for the quest called name, if its step is "Talk to [NPC] for your reward", or just
		/// "Talk to [NPC]" (eg, after the reward window was closed without taking one); else null.
		/// </summary>
		public static string RewardNpc(string quest) {
			string step = QuestStep(quest) ?? "";
			var m = RewardStep.Match(step);
			if ( !m.Success ) m = TalkStep.Match(step);
			if ( !m.Success ) m = DeliverStep.Match(step);
			if ( !m.Success ) m = MeetStep.Match(step);
			return m.Success ? m.Groups[1].Value.Trim() : null;
		}

		/// <summary>The names of the rewards in the open quest reward window, for status lines.</summary>
		public static string RewardNames() => string.Join(", ", QuestRewards().Select(r => RewardItem(r) is Entity item ? Equipment.ItemName(item) ?? item.Path : "?"));
	}

	/// <summary>
	/// Talk to an NPC and choose one of its options (eg, Nessa, "Purchase Items"): walk near it, click it, then click the
	/// option in the NpcOptions it shows. Tick it each frame until it is done (or failed).
	///
	/// Walking: by default it walks the player itself (with the DefaultFollower); a combat routine passes its own WalkTo
	/// and StopWalking instead, and walks after its Decide.
	/// </summary>
	public class NpcInteraction {
		public readonly string Npc, Option;
		/// <summary>Which option text to click: Option, by default (ignoring case).</summary>
		public Func<string, bool> IsOption;
		/// <summary>Click the NPC from within this many cells of it.</summary>
		public float Reach = 15;
		public long ClickPace = 800;
		public int MaxClicks = 5;

		public bool IsDone { get; private set; }
		public bool IsFailed { get; private set; }
		public string Status { get; private set; } = "";

		private readonly Action<Offsets.Vector2i> walkTo;
		private readonly Action stopWalking;
		private int npcClicks;
		private long lastClick, optionClickTime;

		public NpcInteraction(string npc, string option, Action<Offsets.Vector2i> walkTo = null, Action stopWalking = null) {
			Npc = npc; Option = option;
			IsOption = text => string.Equals(text, option, StringComparison.OrdinalIgnoreCase);
			this.walkTo = walkTo ?? (goal => DefaultFollower.SetGoal(goal, this));
			this.stopWalking = stopWalking ?? (() => { if ( DefaultFollower.IsActiveFor(this) ) DefaultFollower.Stop(FollowStatus.Idle, "at the NPC"); });
		}

		private void Fail(string why) { IsFailed = true; Status = why; stopWalking(); }

		/// <summary>Do the next thing; false once done or failed.</summary>
		public bool Tick() {
			if ( IsDone || IsFailed ) return false;
			if ( !PoEMemory.TargetHasFocus ) { Status = "waiting: the game needs focus to use the mouse"; return true; }
			long now = Time.ElapsedMilliseconds;
			if ( optionClickTime > 0 ) {
				// give the game a moment to open what the option opens
				if ( now - optionClickTime < 300 ) return true;
				IsDone = true;
				Status = $"chose {Option}";
				return false;
			}
			var npc = Town.FindNPC(Npc);
			if ( npc == null ) { Fail($"{Npc} is not here"); return false; }

			var options = Town.NpcOptions;
			if ( Town.IsOpen(options) ) {
				stopWalking();
				var option = Town.FindByText(options, IsOption);
				if ( option == null ) {
					Fail($"{Npc} has no option {Option} (shows: {string.Join(", ", Town.Texts(options))})");
					return false;
				}
				Status = $"clicking {Option}";
				Run(Inputs.Click(option.GetClientRect()));
				optionClickTime = now;
				return true;
			}

			var player = GetPlayer();
			var playerPos = player?.GetComponent<Positioned>();
			var npcPos = npc.GetComponent<Positioned>();
			if ( !IsValid(playerPos) || !IsValid(npcPos) ) { Status = "waiting for positions"; return true; }
			float d = Combat.Distance(playerPos.GridPos, npcPos.GridPos);
			if ( d > Reach ) {
				Status = $"walking to {Npc}, {d:F0} cells away";
				var terrain = GetTerrain();
				var goal = terrain != null && DefaultNavigator.TrySnapReachable(terrain, playerPos.GridPos, npcPos.GridPos, out var spot) ? spot : npcPos.GridPos;
				walkTo(goal);
				// (walking it ourselves: the follower needs ticking)
				if ( DefaultFollower.IsActiveFor(this) && terrain != null ) DefaultFollower.Tick(DefaultNavigator, terrain, playerPos.GridPos, Position(player).Z);
				return true;
			}
			stopWalking();
			if ( now - lastClick < ClickPace ) return true;
			if ( npcClicks >= MaxClicks ) { Fail($"clicked {Npc} {npcClicks} times, but it showed no options"); return false; }
			Status = $"clicking {Npc}";
			if ( ClickNpc(npc) ) {
				lastClick = now;
				npcClicks += 1;
			}
			return true;
		}

		/// <summary>Stop walking (eg, when it is given up on).</summary>
		public void Stop() => stopWalking();

		// its name label, if it shows one, else the NPC itself
		private static bool ClickNpc(Entity npc) {
			var click = Ground.ClickEntity(npc);
			if ( click == null ) return false;
			Run(click);
			return true;
		}

	}

}
