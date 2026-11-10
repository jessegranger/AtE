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

		/// <summary>True if one of the windows an NPC option opens is open: purchase, sell, or quest reward.</summary>
		public static bool AnyWindowOpen => IsOpen(PurchaseWindow) || IsOpen(SellWindow) || IsOpen(QuestRewardWindow);

		/// <summary>Close the purchase, sell, and quest reward windows: a click at the top left corner of the screen.</summary>
		public static void CloseWindows() {
			Platform.Current.Input.MouseMove(new Vector2(1, 1));
			Platform.Current.Input.MouseButton(MouseBtn.Left, true);
			Platform.Current.Input.MouseButton(MouseBtn.Left, false);
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
			if ( string.Equals(root.GetChild(0)?.GetChild(0)?.Text?.Trim(), name, StringComparison.OrdinalIgnoreCase) ) return root;
			if ( depth <= 0 ) return null;
			foreach ( var child in root.Children ) {
				var found = FindQuest(name, child, depth - 1);
				if ( found != null ) return found;
			}
			return null;
		}

		/// <summary>The current step of the quest called name (eg, "Talk to Tarkleigh for your reward"), or null.</summary>
		public static string QuestStep(string name) => FindQuest(name)?.GetChild(1)?.GetChild(0)?.GetChild(1)?.Text?.Trim();

		private static readonly Regex RewardStep = new Regex("^Talk to (.+) for your reward$", RegexOptions.IgnoreCase);

		/// <summary>The NPC who has the reward for the quest called name, if its step is "Talk to [NPC] for your reward"; else null.</summary>
		public static string RewardNpc(string quest) {
			var m = RewardStep.Match(QuestStep(quest) ?? "");
			return m.Success ? m.Groups[1].Value : null;
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
				SkillBar.ClickAt(option.GetClientRect());
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
			var label = GetUI()?.LabelsOnGround?.GetAllLabels()?.FirstOrDefault(l => l.Item?.Address == npc.Address);
			if ( label != null && Ground.TryClickLabel(label) ) return true;
			var pos = Position(npc);
			if ( !TryWorldToScreen(pos, out Vector2 at) || !PathFollower.IsSafeToClick(at) ) return false;
			SkillBar.ClickAt(new RectangleF(at.X, at.Y, 0, 0));
			return true;
		}
	}

	/// <summary>
	/// Take the rewards of finished quests: for each quest (by name, eg "Enemy at the Gate") whose step in the quest tracker
	/// is "Talk to [NPC] for your reward", talk to that NPC, choose its option ending in "Reward" (eg, Tarkleigh's
	/// "Hillock Reward"), and Ctrl+click the reward named for that quest in the quest reward window. Done when none of
	/// the quests shows that step.
	/// </summary>
	public class TakeQuestRewards : Objective {
		/// <summary>The reward to take for each quest: quest name => reward (eg, a gem's name).</summary>
		public readonly IReadOnlyDictionary<string, string> Rewards;
		private NpcInteraction talk;
		private long lastClick, retryAt;
		private int clicks;
		private string clickedQuest;
		public TakeQuestRewards(IReadOnlyDictionary<string, string> rewards) { Rewards = rewards; }

		public override string Name => $"Take quest rewards ({string.Join(", ", Rewards.Values)})";
		public override bool Sticky => false;
		// the quest reward window opens the inventory beside it: the routine must not close that (it cancels the reward)
		public override bool UsesInventory => true;
		public override void Reset() { base.Reset(); talk = null; clicks = 0; retryAt = 0; clickedQuest = null; }

		// the first quest waiting for its reward, and who has it
		private (string quest, string npc) Pending() {
			foreach ( var quest in Rewards.Keys ) {
				if ( Town.RewardNpc(quest) is string npc ) return (quest, npc);
			}
			return (null, null);
		}

		public override bool CheckDone(CombatContext ctx) => Pending().quest == null;

		public override void Tick(CombatContext ctx) {
			long now = Time.ElapsedMilliseconds;
			var (quest, npc) = Pending();
			if ( quest == null ) return;
			string reward = Rewards[quest];
			if ( quest != clickedQuest ) { clickedQuest = quest; clicks = 0; }
			if ( Town.IsOpen(Town.QuestRewardWindow) ) {
				ctx.Driver.StopWalking("taking a quest reward");
				if ( now - lastClick < 800 ) return;
				var element = Town.FindQuestReward(reward);
				if ( element == null ) { Detail = $"no {reward} among the rewards for {quest} ({Town.RewardNames()})"; return; }
				if ( clicks >= 3 ) { Detail = $"clicked {reward} {clicks} times, but it was not taken"; return; }
				Detail = $"taking {reward} for {quest}";
				// with Ctrl held, the game takes it at once
				Platform.Current.Input.KeyDown(VKey.LControlKey);
				SkillBar.ClickAt(element.GetClientRect());
				Platform.Current.Input.KeyUp(VKey.LControlKey);
				lastClick = now;
				clicks += 1;
				return;
			}
			if ( Town.AnyWindowOpen ) {
				// eg, the purchase window, from the wrong option
				ctx.Driver.StopWalking("closing a window");
				if ( now - lastClick > 700 ) { lastClick = now; Town.CloseWindows(); }
				return;
			}
			if ( now < retryAt ) return;
			if ( talk == null || talk.IsDone || talk.Npc != npc ) {
				talk = new NpcInteraction(npc, $"{quest} reward", goal => ctx.Driver.WalkTo(goal, $"walking to {npc}"), () => ctx.Driver.StopWalking($"talking to {npc}")) {
					IsOption = text => text.EndsWith(" Reward", StringComparison.OrdinalIgnoreCase),
				};
			}
			talk.Tick();
			Detail = talk.Status;
			if ( talk.IsFailed ) {
				talk = null;
				retryAt = now + 2000;
			}
		}
	}
}
