using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// The quest handlers, act by act, one for each quest of the campaign (in the order the wiki lists them): what to do
	/// for a quest at the steps the quest tracker shows (see QuestHandler, FinishAllQuests). A handler not written yet is
	/// a stub (QuestHandler.Stub): it does nothing, and its quest counts as having no goals. Each act's handlers are in its
	/// own file (Act1Quests.cs to Act10Quests.cs); this file has the dispatch and the helpers they share.
	/// </summary>
	public partial class CampaignRoutine {

		private void AddQuestHandlers(FinishAllQuests quests) {
			switch ( quests.Act ) {
				case 1: Act1Quests(quests); break;
				case 2: Act2Quests(quests); break;
				case 3: Act3Quests(quests); break;
				case 4: Act4Quests(quests); break;
				case 5: Act5Quests(quests); break;
				case 6: Act6Quests(quests); break;
				case 7: Act7Quests(quests); break;
				case 8: Act8Quests(quests); break;
				case 9: Act9Quests(quests); break;
				case 10: Act10Quests(quests); break;
			}
		}

		private static QuestHandler Stub(string quest) => QuestHandler.Stub(quest);
		private static QuestHandler Skip(string quest) => QuestHandler.Skip(quest);

		/// <summary>
		/// A quest step "Kill [boss]" (eg, "Kill Hillock", "Kill the Dweller of the Deep"): find the boss in its area and
		/// kill it, circling it (Orbit). The boss is told by match, or by its name (see MonsterNamed). Done when the quest
		/// tracker moves on (the step no longer says to kill it).
		/// </summary>
		/// <param name="door">
		/// The label of a door to the boss's arena within the area (eg, Brutus's "Warden's Chambers" in The Upper Prison),
		/// taken first: done once the boss is in sight (eg, after a restart in the arena).
		/// </param>
		private static QuestHandler KillBoss(string quest, string boss, string area, Func<Entity, bool> match = null, Func<string> skill = null,
			Func<CombatContext, Offsets.Vector2i?> searchAt = null, string door = null) {
			var isBoss = match ?? MonsterNamed(boss);
			bool inSight() => GetEntities()?.Any(e => e != null && isBoss(e)) ?? false;
			return new QuestHandler(quest, new Sequence($"{quest}: kill {boss}",
				door == null ? null : new TakeTransition(door) { Where = area, DoneWhen = inSight },
				new Orbit(boss, isBoss, skill ?? BuildPlan.MainSkill) { Where = area, SearchAt = searchAt })) {
				Active = step => IsKillStep(step, boss),
			};
		}

		/// <summary>
		/// A step "Kill [boss]" (or "Hunt", "Slay"), however the boss is written (eg, The Dweller of the Deep's "Kill the The Dweller of the
		/// Deep"): "Kill" and the boss's name without a leading "the".
		/// </summary>
		private static bool IsKillStep(string step, string boss) => step != null
			&& (step.StartsWith("Kill ", StringComparison.OrdinalIgnoreCase) || step.StartsWith("Hunt ", StringComparison.OrdinalIgnoreCase)
				|| step.StartsWith("Slay ", StringComparison.OrdinalIgnoreCase))
			&& step.IndexOf(Bare(boss), StringComparison.OrdinalIgnoreCase) >= 0;
		private static string Bare(string name) => name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name.Substring(4) : name;

		/// <summary>
		/// A quest step "Find [item]" (eg, Mercy Mission's "Find the Medicine Chest on The Tidal Island") where a boss
		/// carries the quest item: find the boss in its area, kill it, pick up what it drops, and go back to town by a
		/// portal (picking up a Portal Scroll first if there is none). Handing it over ("Take the Medicine Chest to Nessa")
		/// is a talk in town (see Town.RewardNpc).
		/// </summary>
		/// <param name="door">
		/// The label of a door to the boss's arena within the area (eg, the Weaver's "The Weaver's Nest"), taken first (found
		/// by doorAt, else by looking): done once the boss is in sight (eg, after a restart in the arena).
		/// </param>
		private static QuestHandler KillForQuestItem(string quest, string item, string boss, string area, string town, Func<Entity, bool> match = null,
			Func<string> skill = null, string door = null, Func<CombatContext, Offsets.Vector2i?> doorAt = null) {
			var isBoss = match ?? MonsterNamed(boss);
			bool inSight() => GetEntities()?.Any(e => e != null && isBoss(e)) ?? false;
			return new QuestHandler(quest, new Sequence($"{quest}: {item}", new Goal[] {
				door == null ? null : new TakeTransition(door) { Where = area, DoneWhen = inSight, SearchAt = doorAt },
				new Orbit(boss, isBoss, skill ?? BuildPlan.MainSkill) { Where = area },
				new PickUpQuestItem(item) { Where = area },
			}.Where(g => g != null).Concat(PortalToTown(town)).ToArray())) {
				Active = step => step != null && (step.StartsWith("Find ", StringComparison.OrdinalIgnoreCase)
					|| IsKillStep(step, boss)),
			};
		}

		/// <summary>
		/// Walk into an area (eg, one a quest just opened) and to its waypoint, so the waypoint is discovered (eg, Breaking
		/// Some Eggs: The Submerged Passage, then the waypoint to town for the reward).
		/// </summary>
		private static Goal DiscoverWaypoint(string area) => new GoToWaypoint() { Where = area, DoneWhen = () => WaypointPanel.IsKnownUnlocked(area) };

		/// <summary>The quest tracker's step for the quest (its name with or without " (Optional)"), or null if it is not there.</summary>
		private static string StepOf(string quest) => Town.QuestStep(quest) ?? Town.QuestStep($"{quest} (Optional)");

		/// <summary>The quest is at its reward (its step "Talk to X for your reward"): what came before it is done.</summary>
		private static bool AtReward(string quest) => StepOf(quest)?.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase) ?? false;

		/// <summary>The player is in one of the areas (eg, a step's own area, and the one it opens).</summary>
		private static bool InAny(params string[] areas) => GetCurrentArea() is WorldArea here && areas.Any(a => GoToArea.Is(here, a));

		/// <summary>Back to town by a portal: pick up a Portal Scroll if there is none in the backpack, and read it.</summary>
		private static Goal[] PortalToTown(string town) => new Goal[] {
			new PickUp("Portal Scroll") { DoneWhen = () => Equipment.InBackpack(Offsets.PATH_SCROLL_PORTAL) },
			new UsePortalScroll(town),
		};

		/// <summary>A monster whose name (Render.Name) has name in it, without a leading "the" (eg, "the Dweller of the Deep").</summary>
		public static Func<Entity, bool> MonsterNamed(string name) {
			string bare = Bare(name);
			return e => e?.Path is string path && path.StartsWith("Metadata/Monsters/")
				&& (e.GetComponent<Render>()?.Name?.Contains(bare, StringComparison.OrdinalIgnoreCase) ?? false);
		}

		private const string CraftingRecipe = "Crafting Recipe";

		/// <summary>True once no label with text in it has been on the ground for ms (counted from when it is first asked).</summary>
		private static Func<bool> NoLabelFor(string text, long ms) {
			long since = -1;
			return () => {
				bool shown = GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => (Ground.LabelText(l.Label)?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) ?? false;
				if ( shown ) { since = -1; return false; }
				if ( since < 0 ) since = Time.ElapsedMilliseconds;
				return Time.ElapsedMilliseconds - since > ms;
			};
		}

		private static bool IsEmperorsRoadStep(string step) => step != null && step.IndexOf("Follow the Emperor's Road", StringComparison.OrdinalIgnoreCase) >= 0;
	}
}
