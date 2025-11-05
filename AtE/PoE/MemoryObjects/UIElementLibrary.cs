using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AtE.Globals;

namespace AtE {
	public class UIElementLibrary : MemoryObject<Offsets.InGameState_UIElements> {

		private Element getElement(IntPtr ptr) => IsValid(Address) && ElementCache.TryGetElement(ptr, out Element elem) ? elem : null;

		public IEnumerable<InventoryItem> BackpackItems => BackpackItems();

		// even though this doesn't from from InGameState, put it here for convenience
		public Element EscapeMenu => PoEMemory.GameRoot?.EscapeState?.Menu;

		// public Element GetQuests => IsValid(Address) ? getElement(Cache.GetQuests) : null;
		public Element GameUI => IsValid(Address) ? getElement(Cache.GameUI) : null;
		public Element LifeBubble => IsValid(Address) ? getElement(Cache.LifeBubble) : null;
		public Element ManaBubble => IsValid(Address) ? getElement(Cache.ManaBubble) : null;
		public FlaskPanel Flasks => new FlaskPanel() { Address = Cache.Flasks };
		public Element ExperienceBar => IsValid(Address) ? getElement(Cache.ExperienceBar) : null;
		public Element OpenMenuPopoutButton => IsValid(Address) ? getElement(Cache.OpenMenuPopoutButton) : null;
		public Element CurrentTime => IsValid(Address) ? getElement(Cache.CurrentTime) : null;
		public Element Mouse => IsValid(Address) ? getElement(Cache.Mouse) : null;
		public Element SkillBar => IsValid(Address) ? getElement(Cache.SkillBar) : null;
		public Element SkillTree => IsValid(Address) ? getElement(Cache.SkillTree) : null;
		public Element HiddenSkillBar => IsValid(Address) ? getElement(Cache.HiddenSkillBar) : null;
		public Element ChatBoxRoot => IsValid(Address) ? getElement(Cache.ChatBoxRoot) : null;
		public Element QuestTracker => IsValid(Address) ? getElement(Cache.QuestTracker) : null;
		public Element OpenLeftPanel => IsValid(Address) ? getElement(Cache.OpenLeftPanel) : null;
		public Element OpenRightPanel => IsValid(Address) ? getElement(Cache.OpenRightPanel) : null;
		public InventoryRoot InventoryPanel => IsValid(Cache.InventoryPanel) ? new InventoryRoot() { Address = Cache.InventoryPanel } : null;
		public Element StashElement => IsValid(Address) ? getElement(Cache.StashElement) : null;
		public Inventory<StashItem> StashInventory => IsValid(Address) ? new Inventory<StashItem>() { Address = Cache.StashElement } : null;
		public Element GuildStashElement => IsValid(Address) ? getElement(Cache.GuildStashElement) : null;
		// public Element AtlasPanel => IsValid(Address) ? getElement(Cache.AtlasPanel) : null;
		// public Element AtlasSkillPanel => IsValid(Address) ? getElement(Cache.AtlasSkillPanel) : null;
		// public Element WorldMap => IsValid(Address) ? getElement(Cache.WorldMap) : null;
		public MapElement Map => new MapElement() { Address = Cache.Map };
		public LabelsOnGroundRoot LabelsOnGround => new LabelsOnGroundRoot() { Address = Cache.ItemsOnGroundLabelElement };
		// public Element BanditDialog => IsValid(Address) ? getElement(Cache.BanditDialog) : null;
		// public Element Viewport => IsValid(Address) ? getElement(Cache.GameViewport) : null;
		public Element RootBuffPanel => IsValid(Address) ? getElement(Cache.RootBuffPanel) : null;
		public Element NpcDialog => IsValid(Address) ? getElement(Cache.NpcDialog) : null;
		public Element NpcOptions => IsValid(Address) ? getElement(Cache.NpcOptions) : null;
		// public Element LeagueInteractButtonPanel => IsValid(Address) ? getElement(Cache.LeagueInteractButtonPanel) : null;
		// public Element QuestRewardWindow => IsValid(Address) ? getElement(Cache.QuestRewardWindow) : null;
		public Element PurchaseWindow => IsValid(Address) ? getElement(Cache.PurchaseWindow) : null;
		public Element SellWindow => IsValid(Address) ? getElement(Cache.SellWindow) : null;
		/*
		public Element Unknown730 => IsValid(Address) ? getElement(Cache.Unknown730) : null; // LeaguePurchasePanel
		public Element Unknown740 => IsValid(Address) ? getElement(Cache.Unknown740) : null;
		public Element Unknown758 => IsValid(Address) ? getElement(Cache.Unknown758) : null;
		public Element Unknown768 => IsValid(Address) ? getElement(Cache.Unknown768) : null;
		public Element Unknown778 => IsValid(Address) ? getElement(Cache.Unknown778) : null;
		public Element Unknown780 => IsValid(Address) ? getElement(Cache.Unknown780) : null;
		public Element Unknown788 => IsValid(Address) ? getElement(Cache.Unknown788) : null;
		public Element Unknown790 => IsValid(Address) ? getElement(Cache.Unknown790) : null;
		public Element Unknown798 => IsValid(Address) ? getElement(Cache.Unknown798) : null;
		public Element Unknown7a0 => IsValid(Address) ? getElement(Cache.Unknown7a0) : null;
		public Element Unknown7a8 => IsValid(Address) ? getElement(Cache.Unknown7a8) : null;
		public Element Unknown7b0 => IsValid(Address) ? getElement(Cache.Unknown7b0) : null;
		public Element Unknown7b8 => IsValid(Address) ? getElement(Cache.Unknown7b8) : null;
		public Element Unknown7F0 => IsValid(Address) ? getElement(Cache.Unknown7F0) : null;
		public Element Unknown800 => IsValid(Address) ? getElement(Cache.Unknown800) : null;
		public Element Unknown810 => IsValid(Address) ? getElement(Cache.Unknown810) : null;
		public Element Unknown828 => IsValid(Address) ? getElement(Cache.Unknown828) : null;
		public Element Unknown830 => IsValid(Address) ? getElement(Cache.Unknown830) : null;
		public Element Unknown838 => IsValid(Address) ? getElement(Cache.Unknown838) : null;
		public Element Unknown8C8 => IsValid(Address) ? getElement(Cache.Unknown8C8) : null;
		public Element Unknown8D0 => IsValid(Address) ? getElement(Cache.Unknown8D0) : null;
		public Element Unknown8D8 => IsValid(Address) ? getElement(Cache.Unknown8D8) : null;
		public Element Unknown8E0 => IsValid(Address) ? getElement(Cache.Unknown8E0) : null;
		public Element Unknown8F8 => IsValid(Address) ? getElement(Cache.Unknown8F8) : null;
		public Element Unknown900 => IsValid(Address) ? getElement(Cache.Unknown900) : null;
		public Element Unknown908 => IsValid(Address) ? getElement(Cache.Unknown908) : null;
		public Element Unknown910 => IsValid(Address) ? getElement(Cache.Unknown910) : null;
		public Element Unknown918 => IsValid(Address) ? getElement(Cache.Unknown918) : null;
		public Element Unknown920 => IsValid(Address) ? getElement(Cache.Unknown920) : null;
		public Element Unknown930 => IsValid(Address) ? getElement(Cache.Unknown930) : null;
		public Element Unknown938 => IsValid(Address) ? getElement(Cache.Unknown938) : null;
		public Element Unknown940 => IsValid(Address) ? getElement(Cache.Unknown940) : null;
		public Element Unknown948 => IsValid(Address) ? getElement(Cache.Unknown948) : null;
		public Element Unknown950 => IsValid(Address) ? getElement(Cache.Unknown950) : null;
		*/
		public Element TradeWindow => IsValid(Address) ? getElement(Cache.TradeWindow) : null;
		// public Element LabyrinthDivineFontPanel => IsValid(Address) ? getElement(Cache.LabyrinthDivineFontPanel) : null;
		public Element MapDeviceWindow => IsValid(Address) ? getElement(Cache.MapDeviceWindow) : null;
		public Element CardTradePanel => IsValid(Address) ? getElement(Cache.CardTradePanel) : null;
		// public Element IncursionAltarOfSacrificePanel => IsValid(Address) ? getElement(Cache.IncursionAltarOfSacrificePanel) : null;
		// public Element IncursionLapidaryLensPanel => IsValid(Address) ? getElement(Cache.IncursionLapidaryLensPanel) : null;
		// public Element DelveWindow => IsValid(Address) ? getElement(Cache.DelveWindow) : null;
		// public Element ZanaMissionChoice => IsValid(Address) ? getElement(Cache.ZanaMissionChoice) : null; // KiracMissionPanel
		// public Element BetrayalWindow => IsValid(Address) ? getElement(Cache.BetrayalWindow) : null;
		public Element CraftBench => IsValid(Address) ? getElement(Cache.CraftBench) : null;
		public Element UnveilWindow => IsValid(Address) ? getElement(Cache.UnveilWindow) : null;
		// public Element BlightAnointItemPanel => IsValid(Address) ? getElement(Cache.BlightAnointItemPanel) : null;
		// public Element MetamorphWindow => IsValid(Address) ? getElement(Cache.MetamorphWindow) : null;
		// public Element TanesMetamorphPanel => IsValid(Address) ? getElement(Cache.TanesMetamorphPanel) : null;
		// public Element HorticraftingHideoutPanel => IsValid(Address) ? getElement(Cache.HorticraftingHideoutPanel) : null;
		// public Element HeistContractWindow => IsValid(Address) ? getElement(Cache.HeistContractWindow) : null;
		// public Element HeistRevealWindow => IsValid(Address) ? getElement(Cache.HeistRevealWindow) : null;
		// public Element HeistAllyEquipmentWindow => IsValid(Address) ? getElement(Cache.HeistAllyEquipmentWindow) : null;
		// public Element HeistBlueprintWindow => IsValid(Address) ? getElement(Cache.HeistBlueprintWindow) : null;
		// public Element HeistLockerWindow => IsValid(Address) ? getElement(Cache.HeistLockerWindow) : null;
		// public Element RitualWindow => IsValid(Address) ? getElement(Cache.RitualWindow) : null;
		// public Element RitualFavourWindow => IsValid(Address) ? getElement(Cache.RitualFavourWindow) : null;
		// public Element UltimatumProgressWindow => IsValid(Address) ? getElement(Cache.UltimatumProgressWindow) : null;
		// public Element ExpeditionSelectPanel => IsValid(Address) ? getElement(Cache.ExpeditionSelectPanel) : null;
		// public Element LogbookReceptaclePanel => IsValid(Address) ? getElement(Cache.LogbookReceptaclePanel) : null;
		// public Element ExpeditionLockerPanel => IsValid(Address) ? getElement(Cache.ExpeditionLockerPanel) : null;
		public Element BuffsPanel => IsValid(Address) ? getElement(Cache.BuffsPanel) : null;
		public Element DelveDarkness => IsValid(Address) ? getElement(Cache.DelveDarkness) : null; // Debuffs Panel
		public Element AreaInstanceUi => IsValid(Address) ? getElement(Cache.AreaInstanceUi) : null;
		// public Element InteractButtonWrapper => IsValid(Address) ? getElement(Cache.InteractButtonWrapper) : null;
		// public Element SkipAheadButton => IsValid(Address) ? getElement(Cache.SkipAheadButton) : null;
		// public Element SyndicateHelpButton => IsValid(Address) ? getElement(Cache.SyndicateHelpButton) : null;
		// public Element SyndicateReleasePanel => IsValid(Address) ? getElement(Cache.SyndicateReleasePanel) : null;
		// public Element LeagueInteractPanel => IsValid(Address) ? getElement(Cache.LeagueInteractPanel) : null;
		// public Element MetamorphInteractPanel => IsValid(Address) ? getElement(Cache.MetamorphInteractPanel) : null;
		// public Element RitualInteractPanel => IsValid(Address) ? getElement(Cache.RitualInteractPanel) : null;
		// public Element ExpeditionInteractPanel => IsValid(Address) ? getElement(Cache.ExpeditionInteractPanel) : null;
		// public Element InvitesPanel => IsValid(Address) ? getElement(Cache.InvitesPanel) : null;
		public Element GemLvlUpPanel => IsValid(Address) ? getElement(Cache.GemLvlUpPanel) : null;
		// public Element SkillBarNotifyPanel1 => IsValid(Address) ? getElement(Cache.SkillBarNotifyPanel1) : null;
		public Element ItemOnGroundTooltip => IsValid(Address) ? getElement(Cache.ItemOnGroundTooltip) : null;
	}


}
