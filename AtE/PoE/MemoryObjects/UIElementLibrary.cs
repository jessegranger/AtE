using ImGuiNET;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AtE {
	public class UIElementLibrary : MemoryObject<Offsets.InGameState_UIElements> {

		public Element GetQuests => new Element() { Address = Cache.GetQuests };
		public Element GameUI => new Element() { Address = Cache.GameUI };
		public Element LifeBubble => new Element() { Address = Cache.LifeBubble };
		public Element ManaBubble => new Element() { Address = Cache.ManaBubble };
		public FlaskPanel Flasks => new FlaskPanel() { Address = Cache.Flasks };
		public Element ExperienceBar => new Element() { Address = Cache.ExperienceBar };
		public Element OpenMenuPopoutButton => new Element() { Address = Cache.OpenMenuPopoutButton };
		public Element CurrentTime => new Element() { Address = Cache.CurrentTime };
		public Element Mouse => new Element() { Address = Cache.Mouse };
		public Element SkillBar => new Element() { Address = Cache.SkillBar };
		public Element HiddenSkillBar => new Element() { Address = Cache.HiddenSkillBar };
		public Element ChatBoxRoot => new Element() { Address = Cache.ChatBoxRoot };
		public Element QuestTracker => new Element() { Address = Cache.QuestTracker };
		public Element OpenLeftPanel => new Element() { Address = Cache.OpenLeftPanel };
		public Element OpenRightPanel => new Element() { Address = Cache.OpenRightPanel };
		public InventoryRoot InventoryPanel => new InventoryRoot() { Address = Cache.InventoryPanel };
		public Element StashElement => new Element() { Address = Cache.StashElement };
		public Element GuildStashElement => new Element() { Address = Cache.GuildStashElement };
		public Element AtlasPanel => new Element() { Address = Cache.AtlasPanel };
		public Element AtlasSkillPanel => new Element() { Address = Cache.AtlasSkillPanel };
		public Element WorldMap => new Element() { Address = Cache.WorldMap };
		public MapElement Map => new MapElement() { Address = Cache.Map };
		public LabelsOnGroundRoot LabelsOnGround => new LabelsOnGroundRoot() { Address = Cache.ItemsOnGroundLabelElement };
		public Element BanditDialog => new Element() { Address = Cache.BanditDialog };
		public Element RootBuffPanel => new Element() { Address = Cache.RootBuffPanel };
		public Element NpcDialog => new Element() { Address = Cache.NpcDialog };
		public Element LeagueNpcDialog => new Element() { Address = Cache.LeagueNpcDialog };
		public Element LeagueInteractButtonPanel => new Element() { Address = Cache.LeagueInteractButtonPanel };
		public Element QuestRewardWindow => new Element() { Address = Cache.QuestRewardWindow };
		public Element PurchaseWindow => new Element() { Address = Cache.PurchaseWindow };
		public Element Unknown730 => new Element() { Address = Cache.Unknown730 }; // LeaguePurchasePanel
		public Element SellWindow => new Element() { Address = Cache.SellWindow };
		/*
		public Element Unknown740 => new Element() { Address = Cache.Unknown740 };
		public Element Unknown758 => new Element() { Address = Cache.Unknown758 };
		public Element Unknown768 => new Element() { Address = Cache.Unknown768 };
		public Element Unknown778 => new Element() { Address = Cache.Unknown778 };
		public Element Unknown780 => new Element() { Address = Cache.Unknown780 };
		public Element Unknown788 => new Element() { Address = Cache.Unknown788 };
		public Element Unknown790 => new Element() { Address = Cache.Unknown790 };
		public Element Unknown798 => new Element() { Address = Cache.Unknown798 };
		public Element Unknown7a0 => new Element() { Address = Cache.Unknown7a0 };
		public Element Unknown7a8 => new Element() { Address = Cache.Unknown7a8 };
		public Element Unknown7b0 => new Element() { Address = Cache.Unknown7b0 };
		public Element Unknown7b8 => new Element() { Address = Cache.Unknown7b8 };
		public Element Unknown7F0 => new Element() { Address = Cache.Unknown7F0 };
		public Element Unknown800 => new Element() { Address = Cache.Unknown800 };
		public Element Unknown810 => new Element() { Address = Cache.Unknown810 };
		public Element Unknown828 => new Element() { Address = Cache.Unknown828 };
		public Element Unknown830 => new Element() { Address = Cache.Unknown830 };
		public Element Unknown838 => new Element() { Address = Cache.Unknown838 };
		public Element Unknown8C8 => new Element() { Address = Cache.Unknown8C8 };
		public Element Unknown8D0 => new Element() { Address = Cache.Unknown8D0 };
		public Element Unknown8D8 => new Element() { Address = Cache.Unknown8D8 };
		public Element Unknown8E0 => new Element() { Address = Cache.Unknown8E0 };
		public Element Unknown8F8 => new Element() { Address = Cache.Unknown8F8 };
		public Element Unknown900 => new Element() { Address = Cache.Unknown900 };
		public Element Unknown908 => new Element() { Address = Cache.Unknown908 };
		public Element Unknown910 => new Element() { Address = Cache.Unknown910 };
		public Element Unknown918 => new Element() { Address = Cache.Unknown918 };
		public Element Unknown920 => new Element() { Address = Cache.Unknown920 };
		public Element Unknown930 => new Element() { Address = Cache.Unknown930 };
		public Element Unknown938 => new Element() { Address = Cache.Unknown938 };
		public Element Unknown940 => new Element() { Address = Cache.Unknown940 };
		public Element Unknown948 => new Element() { Address = Cache.Unknown948 };
		public Element Unknown950 => new Element() { Address = Cache.Unknown950 };
		*/
		public Element TradeWindow => new Element() { Address = Cache.TradeWindow };
		public Element LabyrinthDivineFontPanel => new Element() { Address = Cache.LabyrinthDivineFontPanel };
		public Element MapDeviceWindow => new Element() { Address = Cache.MapDeviceWindow };
		public Element CardTradePanel => new Element() { Address = Cache.CardTradePanel };
		public Element IncursionAltarOfSacrificePanel => new Element() { Address = Cache.IncursionAltarOfSacrificePanel };
		public Element IncursionLapidaryLensPanel => new Element() { Address = Cache.IncursionLapidaryLensPanel };
		public Element DelveWindow => new Element() { Address = Cache.DelveWindow };
		public Element ZanaMissionChoice => new Element() { Address = Cache.ZanaMissionChoice }; // KiracMissionPanel
		public Element BetrayalWindow => new Element() { Address = Cache.BetrayalWindow };
		public Element CraftBench => new Element() { Address = Cache.CraftBench };
		public Element UnveilWindow => new Element() { Address = Cache.UnveilWindow };
		public Element BlightAnointItemPanel => new Element() { Address = Cache.BlightAnointItemPanel };
		public Element MetamorphWindow => new Element() { Address = Cache.MetamorphWindow };
		public Element TanesMetamorphPanel => new Element() { Address = Cache.TanesMetamorphPanel };
		public Element HorticraftingHideoutPanel => new Element() { Address = Cache.HorticraftingHideoutPanel };
		public Element HeistContractWindow => new Element() { Address = Cache.HeistContractWindow };
		public Element HeistRevealWindow => new Element() { Address = Cache.HeistRevealWindow };
		public Element HeistAllyEquipmentWindow => new Element() { Address = Cache.HeistAllyEquipmentWindow };
		public Element HeistBlueprintWindow => new Element() { Address = Cache.HeistBlueprintWindow };
		public Element HeistLockerWindow => new Element() { Address = Cache.HeistLockerWindow };
		public Element RitualWindow => new Element() { Address = Cache.RitualWindow };
		public Element RitualFavourWindow => new Element() { Address = Cache.RitualFavourWindow };
		public Element UltimatumProgressWindow => new Element() { Address = Cache.UltimatumProgressWindow };
		public Element ExpeditionSelectPanel => new Element() { Address = Cache.ExpeditionSelectPanel };
		public Element LogbookReceptaclePanel => new Element() { Address = Cache.LogbookReceptaclePanel };
		public Element ExpeditionLockerPanel => new Element() { Address = Cache.ExpeditionLockerPanel };
		public Element KalandraMirroredTabletPanel => new Element() { Address = Cache.KalandraMirroredTabletPanel };
		public Element KalandraReflectionPanel => new Element() { Address = Cache.KalandraReflectionPanel };
		public Element BuffsPanel => new Element() { Address = Cache.BuffsPanel };
		public Element DelveDarkness => new Element() { Address = Cache.DelveDarkness }; // Debuffs Panel
		public Element AreaInstanceUi => new Element() { Address = Cache.AreaInstanceUi };
		public Element InteractButtonWrapper => new Element() { Address = Cache.InteractButtonWrapper };
		public Element SkipAheadButton => new Element() { Address = Cache.SkipAheadButton };
		public Element SyndicateHelpButton => new Element() { Address = Cache.SyndicateHelpButton };
		public Element SyndicateReleasePanel => new Element() { Address = Cache.SyndicateReleasePanel };
		public Element LeagueInteractPanel => new Element() { Address = Cache.LeagueInteractPanel };
		public Element MetamorphInteractPanel => new Element() { Address = Cache.MetamorphInteractPanel };
		public Element RitualInteractPanel => new Element() { Address = Cache.RitualInteractPanel };
		public Element ExpeditionInteractPanel => new Element() { Address = Cache.ExpeditionInteractPanel };
		public Element InvitesPanel => new Element() { Address = Cache.InvitesPanel };
		public Element GemLvlUpPanel => new Element() { Address = Cache.GemLvlUpPanel };
		public Element SkillBarNotifyPanel1 => new Element() { Address = Cache.SkillBarNotifyPanel1 };
		public Element ItemOnGroundTooltip => new Element() { Address = Cache.ItemOnGroundTooltip };
	}


}
