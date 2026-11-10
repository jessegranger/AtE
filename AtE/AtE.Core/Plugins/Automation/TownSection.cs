using ImGuiNET;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Trying out the town helpers (see Town): talk to an NPC and choose an option, see which windows are open, close
	/// them, and take a quest reward.
	/// </summary>
	public class TownSection : AutomationSection {
		public override string Name => "Town";

		public string Npc = "Nessa";
		public string Option = "Purchase Items";
		public string Reward = "";
		public string Quest = "Enemy at the Gate";

		private NpcInteraction talk;
		private string rewardStatus = "";
		// the button takes the focus from the game: a plain click on the reward focuses it, then the Ctrl+click, a moment later
		private Element focusedReward;
		private long focusClickTime;

		public override void Render() {
			ImGui.InputText("NPC", ref Npc, 64);
			ImGui.InputText("Option", ref Option, 64);
			if ( talk == null || talk.IsDone || talk.IsFailed ) {
				if ( ImGui.Button("Talk") ) talk = new NpcInteraction(Npc.Trim(), Option.Trim());
			} else if ( ImGui.Button("Stop") ) {
				talk.Stop();
				talk = null;
			}
			if ( talk != null ) { ImGui.SameLine(); ImGui.Text(talk.Status); }
			if ( ImGui.Button("Close windows") ) Town.CloseWindows();
			ImGui.Text($"Here: {string.Join(", ", new[] { "Nessa", "Bestel", "Tarkleigh" }.Where(n => Town.FindNPC(n) != null))}");
			ImGui.Text($"Open: options {Town.IsOpen(Town.NpcOptions)}, purchase {Town.IsOpen(Town.PurchaseWindow)}, sell {Town.IsOpen(Town.SellWindow)}, quest reward {Town.IsOpen(Town.QuestRewardWindow)}");
			if ( Town.IsOpen(Town.NpcOptions) ) ImGui.Text($"Options: {string.Join(", ", Town.Texts(Town.NpcOptions))}");
			if ( Town.IsOpen(Town.QuestRewardWindow) ) ImGui.Text($"Rewards: {Town.RewardNames()}");
			ImGui.InputText("Quest", ref Quest, 64);
			ImGui.Text($"Step: {Town.QuestStep(Quest.Trim()) ?? "(not in the quest tracker)"}; reward from: {Town.RewardNpc(Quest.Trim()) ?? "(nobody)"}");
			ImGui.InputText("Reward", ref Reward, 64);
			if ( ImGui.Button("Take reward") ) {
				var reward = Town.FindQuestReward(Reward.Trim());
				if ( reward == null ) rewardStatus = $"no {Reward} among the rewards ({Town.RewardNames()})";
				else {
					SkillBar.ClickAt(reward.GetClientRect());
					focusedReward = reward;
					focusClickTime = Time.ElapsedMilliseconds;
					rewardStatus = $"focusing the game on {Reward}";
				}
			}
			ImGui.SameLine();
			ImGui.Text(rewardStatus);
		}

		public override IState OnTick(long dt) {
			talk?.Tick();
			if ( focusedReward != null && Time.ElapsedMilliseconds - focusClickTime > 300 ) {
				Platform.Current.Input.KeyDown(VKey.LControlKey);
				SkillBar.ClickAt(focusedReward.GetClientRect());
				Platform.Current.Input.KeyUp(VKey.LControlKey);
				rewardStatus = $"Ctrl+clicked {Reward}";
				focusedReward = null;
			}
			return this;
		}
	}
}
