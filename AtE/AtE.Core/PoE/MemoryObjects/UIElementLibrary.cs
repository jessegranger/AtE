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
		public FlaskPanel Flasks => IsValid(Cache.Flasks) ? new FlaskPanel() { Address = Cache.Flasks } : null;
		public Element ExperienceBar => IsValid(Address) ? getElement(Cache.ExperienceBar) : null;
		public Element OpenMenuPopoutButton => IsValid(Address) ? getElement(Cache.OpenMenuPopoutButton) : null;
		public Element CurrentTime => IsValid(Address) ? getElement(Cache.CurrentTime) : null;
		public Element Mouse => IsValid(Address) ? getElement(Cache.Mouse) : null;
		/// <summary>
		/// The sprite of the icon drawn at the mouse, eg "Art/2DItems/Currency/CurrencyUpgradeToRare.dds|0,0,0,0|0" while an Orb of
		/// Alchemy is being used (right clicked, not yet applied); "" when Mouse has no icon; null if not readable.
		/// </summary>
		public string MouseIconSprite {
			get {
				var mouse = Mouse;
				if ( mouse == null ) return null;
				var icon = mouse.GetChild(0);
				if ( icon == null ) return mouse.Children.Any() ? null : "";
				return PoEMemory.TryRead(icon.Address + Offsets.Element_MouseIcon_Sprite, out IntPtr sprite)
					&& PoEMemory.TryRead(sprite, out Offsets.SpriteDetails details)
					&& PoEMemory.TryReadString(details.strSpriteFile, Encoding.Unicode, out string file)
					? file : null;
			}
		}
		/// <summary>The "Skip All Tutorials" button, shown to a new character.</summary>
		public Element SkipAllTutorials => IsValid(Address) ? getElement(Cache.SkipAllTutorials) : null;
		public Element SkillBar => IsValid(Address) ? getElement(Cache.SkillBar) : null;
		/// <summary>The level-up button, shown while there are passive points to spend.</summary>
		public Element PassivePointsButton => IsValid(Address) ? getElement(Cache.PassivePointsButton) : null;
		/// <summary>How many passive points there are to spend (the number on PassivePointsButton; 0 while it is hidden).</summary>
		public int UnspentPassivePoints {
			get {
				var button = PassivePointsButton;
				if ( !IsValid(button) || !button.IsVisible ) return 0;
				return int.TryParse(button.GetChild(1)?.Text, out int n) ? n : 0;
			}
		}
		public Element SkillTree => IsValid(Address) ? getElement(Cache.SkillTree) : null;
		/// <summary>The Options panel (O): its "Input" tab lists the game's key bindings (see GameKeys).</summary>
		public Element OptionsPanel => IsValid(Address) ? getElement(Cache.OptionsPanel) : null;
		public Element HiddenSkillBar => IsValid(Address) ? getElement(Cache.HiddenSkillBar) : null;
		public Element ChatBoxRoot => IsValid(Address) ? getElement(Cache.ChatBoxRoot) : null;
		public Element QuestTracker => IsValid(Address) ? getElement(Cache.QuestTracker) : null;
		public Element OpenLeftPanel => IsValid(Address) ? getElement(Cache.OpenLeftPanel) : null;
		public Element OpenRightPanel => IsValid(Address) ? getElement(Cache.OpenRightPanel) : null;
		public InventoryRoot InventoryPanel => IsValid(Cache.InventoryPanel) ? new InventoryRoot() { Address = Cache.InventoryPanel } : null;
		/// <summary>The stash window; see StashRoot for its tabs.</summary>
		public StashRoot StashElement => IsValid(Address) && IsValid(Cache.StashElement) ? new StashRoot() { Address = Cache.StashElement } : null;
		public Inventory<StashItem> StashInventory => IsValid(Address) && IsValid(Cache.StashElement) ? new Inventory<StashItem>() { Address = Cache.StashElement } : null;
		public Element GuildStashElement => IsValid(Address) ? getElement(Cache.GuildStashElement) : null;
		// public Element AtlasPanel => IsValid(Address) ? getElement(Cache.AtlasPanel) : null;
		// public Element AtlasSkillPanel => IsValid(Address) ? getElement(Cache.AtlasSkillPanel) : null;
		// public Element WorldMap => IsValid(Address) ? getElement(Cache.WorldMap) : null;
		public MapElement Map => IsValid(Cache.Map) ? new MapElement() { Address = Cache.Map } : null;
		public LabelsOnGroundRoot LabelsOnGround => IsValid(Cache.ItemsOnGroundLabelElement) ? new LabelsOnGroundRoot() { Address = Cache.ItemsOnGroundLabelElement } : null;
		// public Element BanditDialog => IsValid(Address) ? getElement(Cache.BanditDialog) : null;
		// public Element Viewport => IsValid(Address) ? getElement(Cache.GameViewport) : null;
		// public Element RootBuffPanel => IsValid(Address) ? getElement(Cache.RootBuffPanel) : null;
		// public Element NpcDialog => IsValid(Address) ? getElement(Cache.NpcDialog) : null;
		public Element NpcOptions => IsValid(Address) ? getElement(Cache.NpcOptions) : null;
		// public Element LeagueInteractButtonPanel => IsValid(Address) ? getElement(Cache.LeagueInteractButtonPanel) : null;
		public Element QuestRewardWindow => IsValid(Address) ? getElement(Cache.QuestRewardWindow) : null;
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
		/// <summary>The Map Device window (also Maligaro's Orrery in Act 7); see MapDevice for its children and plans.</summary>
		public MapDevice MapDevice => IsValid(Cache.MapDevice) ? new MapDevice() { Address = Cache.MapDevice } : null;
		// public Element LabyrinthDivineFontPanel => IsValid(Address) ? getElement(Cache.LabyrinthDivineFontPanel) : null;
		// public Element MapDeviceWindow => IsValid(Address) ? getElement(Cache.MapDeviceWindow) : null;
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
		/// <summary>
		/// A panel holding the Act 2 bandit leaders' dialog (3.29.4, seen live with Oak's: its child 6 shows "If you help
		/// Oak ...", and the buttons "help oak" at (6, 2, 0) and "kill oak" at (6, 2, 1); the other children are arena/PvP texts).
		/// </summary>
		public Element HelpOrKillBandit => IsValid(Address) ? getElement(Cache.HelpOrKillBandit) : null;
		/// <summary>The augury card selection window, opened by clicking a unique monster with the augury_boss_frozen buff.</summary>
		public Element AuguryCardSelectionWindow => IsValid(Address) ? getElement(Cache.AuguryCardSelectionWindow) : null;
		/// <summary>
		/// The augury cards button ("augury_show_cards_button"): its (0, 0) child's Text is the count of fate cards chosen in
		/// this map, eg "3/3" (seen live, 3.30.0d, Mud Geyser, after taking 3 fates). See MappingRoutine.AuguryCardsChosen.
		/// </summary>
		public Element AuguryShowCardsButton => IsValid(Address) ? getElement(Cache.autoAuguryShowCardsButton) : null;
		/// <summary>
		/// The Sentinel drone button ("sentinel_drone_display"), shown once a Sentinel (Interactables) has been clicked
		/// (3.30, seen live after clicking an "Obsidian Apex Sentinel"). Its (0, 4) child's Text is the key that activates
		/// the drone (eg, "F2"), visible while it is ready; once activated (seen live, after pressing F2) (0, 4) is hidden and
		/// (0, 1), (0, 2), (0, 3) are shown, (0, 3)'s Text a number ("4": charges or seconds, not known). See
		/// SentinelDroneKey.
		/// </summary>
		public Element SentinelDroneDisplay => IsValid(Address) ? getElement(Cache.autoSentinelDroneDisplay) : null;
		/// <summary>The key that activates the Sentinel drone, while its button shows it is ready (see SentinelDroneDisplay); else null.</summary>
		public string SentinelDroneKey {
			get {
				var display = SentinelDroneDisplay;
				if ( !IsValid(display) || !display.IsVisible ) return null;
				var label = display.GetChild(0, 4);
				return IsValid(label) && label.IsVisible && !string.IsNullOrWhiteSpace(label.Text) ? label.Text.Trim() : null;
			}
		}
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
		// public Element ItemOnGroundTooltip => IsValid(Address) ? getElement(Cache.ItemOnGroundTooltip) : null;
	}


}
