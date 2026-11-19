using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 3's quests, one handler each (in the order the wiki lists them; see QuestHandlers for the shared helpers).
	/// </summary>
	public partial class CampaignRoutine {
		/// <summary>How long Piety must be gone (or not targetable) after the fight in The Crematorium to count as teleported away.</summary>
		private const long PietyGoneMs = 3000;

		private void Act3Quests(FinishAllQuests quests) {
			Func<string> main = BuildPlan.MainSkill;
			// The City of Sarn's way to town: the exit on the RuinedCity/AreaTransitions/act3_docks_to_town_lower tile (3.29.4)
			// (done in town, or in any of later, the areas past it)
			Goal ToSarnEncampment(params string[] later) => new TakeTransition(SarnEncampment) { Where = CityOfSarn, SearchAt = Search.Exit(SarnEncampment, "/act3_docks_to_town_lower"),
				DoneWhen = () => InAny(TownOf(3)) || InAny(later) };
			// Seeing Clarissa in The City of Sarn gives it (before Shadow of the Vaal's step there: it comes first). "Defeat the
			// guard and rescue the girl": kill the Guard Captain (Metadata/Monsters/Axis/GuardLieutenantClarissa, a unique,
			// with Guards around him, by Clarissa: seen live, 3.29.4, at <564, 1298>, Clarissa, Metadata/NPC/Act3/ClarissaSlums,
			// at <560, 1278>), then talk to her once she can be targeted.
			// "Find Tolman in the Crematorium": on to town, from town to The Slums, from The Slums through the exit to The
			// Crematorium (on the AreaTransitions/act3_prison_entrance tile). There, toward the Terrain/quest_marker tile until
			// Piety (Axis/PietyEscape) shows; wait for her to be targetable, and fight her (Orbit) until she teleports away
			// (gone, or not targetable any more, for a few seconds). Then click "Tolman", pick up Tolman's Bracelet, and collect
			// a Crafting Recipe if one shows near.
			// "Travel to the Sarn Encampment" (once the Crematorium's goals are done): a trip (by a Portal Scroll, see
			// FinishAllQuests). "Talk to Clarissa" (she gives the Sewer Keys) and "Talk to Maramoa for your reward" (a gem):
			// talks in town (see TakeQuestRewards).
			// TODO: confirm live (not seen yet): the exits' labels, Piety's teleport, Tolman's label, the Sewer Keys as a reward
			const string love = "Lost in Love";
			bool rescueStep() => StepOf(love)?.StartsWith("Defeat the guard", StringComparison.OrdinalIgnoreCase) ?? false;
			bool tolmanStep() => StepOf(love)?.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase) ?? false;
			bool isPiety(Entity e) => e?.Path?.IndexOf("Axis/PietyEscape", StringComparison.OrdinalIgnoreCase) >= 0;
			// Piety gone: seen targetable (fought), then not there or not targetable for PietyGoneMs (latched)
			bool pietyFought = false, pietyGone = false;
			long pietyLostAt = 0;
			bool PietyGone() {
				if ( pietyGone ) return true;
				var piety = GetEntities()?.FirstOrDefault(e => e != null && isPiety(e));
				bool targetable = piety != null && IsAlive(piety) && (piety.GetComponent<Targetable>()?.IsTargetable ?? false);
				if ( targetable ) { pietyFought = true; pietyLostAt = 0; return false; }
				// (after a restart past the fight: Tolman's label shows, and no Piety in range)
				bool tolman = GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), "Tolman", StringComparison.OrdinalIgnoreCase)) ?? false;
				if ( !pietyFought && !(tolman && piety == null) ) return false;
				if ( pietyLostAt == 0 ) pietyLostAt = Time.ElapsedMilliseconds;
				if ( Time.ElapsedMilliseconds - pietyLostAt < PietyGoneMs ) return false;
				Log($"Campaign: {love}: Piety is gone ({(pietyFought ? "after the fight" : "not in range, and Tolman's label shows")})");
				return pietyGone = true;
			}
			var questMarker = Search.Tile("/quest_marker");
			var recipe = new PickUp(CraftingRecipe) { Where = Crematorium, SearchAt = questMarker, DoneWhen = NoLabelFor(CraftingRecipe, 5000) };
			var clarissaNpc = TalkTo.NpcNamed("Clarissa");
			bool clarissa(Entity e) => clarissaNpc(e) && (e.GetComponent<Targetable>()?.IsTargetable ?? false);
			bool clarissaFree() => GetEntities()?.Any(e => e != null && clarissa(e)) ?? false;
			bool isCaptain(Entity e) => e?.Path?.StartsWith("Metadata/Monsters/Axis/GuardLieutenantClarissa", StringComparison.OrdinalIgnoreCase) ?? false;
			// (look for him by her, if she is in range)
			Offsets.Vector2i? byClarissa(CombatContext ctx) => GetEntities()?.FirstOrDefault(e => e != null && clarissaNpc(e))?.GetComponent<Positioned>()?.GridPos;
			quests.Handlers.Add(new QuestHandler(love, new Sequence(love,
				new Orbit("the Guard Captain", isCaptain, main) { Where = CityOfSarn, SearchAt = byClarissa, DoneWhen = () => !rescueStep() || clarissaFree() },
				new TalkTo("Clarissa", clarissa) { Where = CityOfSarn, SearchAt = byClarissa, DoneWhen = () => !rescueStep() },
				ToSarnEncampment(Slums, Crematorium),
				new EnterArea(Slums) { Where = TownOf(3), DoneWhen = () => InAny(Slums, Crematorium) },
				new TakeTransition(Crematorium) { Where = Slums, SearchAt = Search.Exit(Crematorium, "/act3_prison_entrance"), DoneWhen = () => InAny(Crematorium) },
				new Orbit("Piety", isPiety, main) { Where = Crematorium, SearchAt = questMarker, DoneWhen = () => PietyGone() || !tolmanStep() },
				new Activate("Tolman") { Where = Crematorium, SearchAt = questMarker, DoneWhen = () => !tolmanStep() },
				new PickUpQuestItem("Tolman's Bracelet") { Where = Crematorium },
				recipe)) {
				Active = step => step != null && (
					(InAny(CityOfSarn) && (step.StartsWith("Defeat the guard", StringComparison.OrdinalIgnoreCase) || step.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase)))
					|| (InAny(TownOf(3), Slums) && step.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase))
					// (the step may move on to the trip to town before the bracelet and the recipe are had)
					|| (InAny(Crematorium) && (step.StartsWith("Find Tolman", StringComparison.OrdinalIgnoreCase)
						|| (step.StartsWith("Travel to ", StringComparison.OrdinalIgnoreCase) && !recipe.IsComplete)))),
			});
			// Act 2's Shadow of the Vaal, on in The City of Sarn: "Find the Sarn encampment". Collect a Crafting Recipe if one
			// is in sight on the way (looking for a moment, toward the exit), then on to town.
			const string vaal = "Shadow of the Vaal";
			quests.Handlers.Add(new QuestHandler(vaal, new Sequence($"{vaal}: the Sarn encampment",
				new PickUp(CraftingRecipe) { Where = CityOfSarn, SearchAt = Search.Exit(SarnEncampment, "/act3_docks_to_town_lower"), DoneWhen = NoLabelFor(CraftingRecipe, 5000) },
				ToSarnEncampment())) {
				Active = step => step != null && InAny(CityOfSarn) && step.StartsWith("Find the Sarn encampment", StringComparison.OrdinalIgnoreCase),
			});
			SeverTheRightHand(quests);
			quests.Handlers.Add(Stub("The Gemling Queen"));
			quests.Handlers.Add(Stub("Victario's Secrets"));
			quests.Handlers.Add(Stub("The Ribbon Spool"));
			quests.Handlers.Add(Stub("Fiery Dust"));
			quests.Handlers.Add(Stub("A Swig of Hope"));
			PietysPets(quests);
			quests.Handlers.Add(Stub("A Fixture of Fate"));
			SceptreOfGodQuest(quests);
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("Strange Growths"));
		}

		/// <summary>
		/// Sever the Right Hand. Its step "Travel to The Ebony Barracks" means the way there (through The Sewers) is blocked,
		/// and opening it is a chain of quest items, each got and then used, in order: Tolman's Bracelet (The Crematorium,
		/// see Lost in Love), given to Clarissa for the Sewer Keys; the Sewer Keys, used on the Sewer Grating in The Slums
		/// (the Transitions/slum_sewer_entrance tile); the Thaumetic Sulphite (a chest on The Docks' templeruinforest_questcart
		/// tile), given to Lady Dialla in The Solaris Temple Level 2 (by the Features/gemling_queen_throne tile) for the
		/// Infernal Talc; the Infernal Talc, used on the Undying Blockage in The Sewers (by its exit to The Ebony Barracks, the
		/// Features/sewerexit tile). Which item is in the backpack (Equipment.HasQuestItem) says which step is next. With none
		/// of them: the sulphite, if The Ebony Barracks' waypoint is locked and The Battlefront's unlocked; else (the talc
		/// used) the step is a trip to The Ebony Barracks (FinishAllQuests). There, "Kill General Gravicius" (by the
		/// Floor/temple_carpet_oneside tile), with AvoidSpam; then the town's talks.
		/// TODO: confirm live (not seen yet): the items' names, Dialla's name and that a talk gives the talc, the blockage's
		/// label, Gravicius's name
		/// </summary>
		private void SeverTheRightHand(FinishAllQuests quests) {
			const string sever = "Sever the Right Hand";
			bool travelStep(string step) => step != null && step.StartsWith("Travel to the Ebony Barracks", StringComparison.OrdinalIgnoreCase);
			bool has(string item) => Equipment.HasQuestItem(item);
			// with none of the items: the sulphite is next while The Ebony Barracks' waypoint is locked and The Battlefront's
			// is unlocked (the way on to The Docks is open: the grating is behind); else nothing here (eg, the talc used:
			// the trip to The Ebony Barracks)
			bool sulphiteNext() => WaypointPanel.IsKnownLocked(EbonyBarracks) && WaypointPanel.IsKnownUnlocked("The Battlefront");
			// to an area, done only while there (so it is gone to again, eg after a trip to town)
			Goal To(string area) => new EnterArea(area) { NotDoneWhile = () => !InAny(area) };
			// (active only while it has something left to do: else the step is a trip, as any other)
			void Add(string what, Func<bool> when, params Goal[] goals) {
				var seq = new Sequence($"{sever}: {what}", goals);
				quests.Handlers.Add(new QuestHandler(sever, seq) {
					Active = step => travelStep(step) && when() && CampaignState.Context is CombatContext ctx && seq.Select(ctx) != null,
				});
			}
			Add("the Infernal Talc on the Undying Blockage", () => has("Infernal Talc"),
				To(Sewers),
				new Activate("Undying Blockage") { Where = Sewers, SearchAt = Search.Tile("/sewerexit"), DoneWhen = () => !has("Infernal Talc") });
			Add("the Thaumetic Sulphite to Lady Dialla", () => !has("Infernal Talc") && has("Thaumetic Sulphite"),
				To(SolarisTemple2),
				new TalkTo("Lady Dialla", TalkTo.NpcNamed("Dialla")) { Where = SolarisTemple2, SearchAt = Search.Tile("/gemling_queen_throne"), DoneWhen = () => has("Infernal Talc") });
			Add("the Sewer Keys on the Sewer Grating", () => !has("Infernal Talc") && !has("Thaumetic Sulphite") && has("Sewer Keys"),
				To(Slums),
				new TakeTransition("Sewer Grating") { Where = Slums, SearchAt = Search.Tile("/slum_sewer_entrance"), DoneWhen = () => !has("Sewer Keys") });
			Add("the Thaumetic Sulphite", () => !has("Infernal Talc") && !has("Thaumetic Sulphite") && !has("Sewer Keys") && !has("Tolman's Bracelet") && sulphiteNext(),
				To(Docks),
				new OpenChest("the chest with the Thaumetic Sulphite") { Where = Docks, SearchAt = Search.Tile("/templeruinforest_questcart"), DoneWhen = () => has("Thaumetic Sulphite") },
				new PickUpQuestItem("the Thaumetic Sulphite") { Where = Docks, DoneWhen = () => has("Thaumetic Sulphite") });
			// "Kill General Gravicius", in The Ebony Barracks
			var gravicius = MonsterNamed("Gravicius");
			quests.Handlers.Add(new QuestHandler(sever, new Sequence($"{sever}: kill General Gravicius",
				new AvoidSpam("General Gravicius", gravicius, BuildPlan.MainSkill) { Where = EbonyBarracks, SearchAt = Search.Tile("/temple_carpet_oneside") })) {
				Active = step => IsKillStep(step, "General Gravicius"),
			});
		}

		/// <summary>
		/// Piety's Pets. Done once /passives names it (its reward is a Book of Skill: see QuestHandler.IsActive). Its step is
		/// "Find Piety" until her arena is entered; after that the step does not matter: in The Lunaris Temple Level 2, with
		/// Piety there, she is to kill. Through the "Portal" to her arena (by the Features/templeclean_prepiety_roundtop_center
		/// tile), wait for her to turn hostile (Kill waits while she is not targetable), and kill her (Kill: a plain fight,
		/// not Orbit or AvoidSpam). She drops the Tower Key: pick it up, and collect a Crafting Recipe if one shows near.
		/// Then, with the Tower Key (it is kept: The Sceptre of God's door takes it) and no passive point for the quest,
		/// back to The Sarn Encampment to talk to Grigor for the Book of Skill (UseBooks reads it).
		/// TODO: confirm live (not seen yet): Piety's monster (path, name), the Tower Key's name, that Grigor's talk gives the book
		/// </summary>
		private void PietysPets(FinishAllQuests quests) {
			const string pets = "Piety's Pets";
			Func<string> main = BuildPlan.MainSkill;
			bool hasKey() => Equipment.HasQuestItem("Tower Key");
			bool keyOnGround() => GetUI()?.LabelsOnGround?.GetAllLabels()?.Any(l => (Ground.LabelText(l.Label)?.IndexOf("Tower Key", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) ?? false;
			bool named(Entity e) => string.Equals(e?.GetComponent<Render>()?.Name?.Trim(), "Piety", StringComparison.OrdinalIgnoreCase);
			bool pietyMonster(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/") && named(e);
			bool inSight() => GetEntities()?.Any(e => e != null && pietyMonster(e)) ?? false;
			bool killed() => hasKey() || keyOnGround();
			var arena = Search.Tile("/templeclean_prepiety_roundtop_center");
			var recipe = new PickUp(CraftingRecipe) { Where = LunarisTemple2, SearchAt = arena, DoneWhen = NoLabelFor(CraftingRecipe, 5000) };
			quests.Handlers.Add(new QuestHandler(pets, new Sequence($"{pets}: kill Piety",
				new TakeTransition("Portal") { Where = LunarisTemple2, SearchAt = arena, DoneWhen = () => inSight() || killed() },
				new Kill("Piety", pietyMonster, main) { Where = LunarisTemple2, SearchAt = arena, DoneWhen = killed },
				new PickUpQuestItem("the Tower Key") { Where = LunarisTemple2, DoneWhen = hasKey },
				recipe)) {
				// ("Find Piety", without the key; and in The Lunaris Temple Level 2, until the recipe is looked for, whatever the
				// step: it changes once her arena is entered)
				Active = step => (step != null && !hasKey() && step.StartsWith("Find Piety", StringComparison.OrdinalIgnoreCase))
					|| (InAny(LunarisTemple2) && !recipe.IsComplete),
				Untracked = () => InAny(LunarisTemple2) && !recipe.IsComplete,
			});
			// the Tower Key, and no passive point for the quest (else this handler is not active): Grigor has the Book of Skill
			var grigor = new TalkTo("Grigor", TalkTo.NpcNamed("Grigor")) { Where = TownOf(3), MaxTalks = 2,
				DoneWhen = () => BackpackItems().Any(i => UseBooks.IsBook(i.Entity)) };
			quests.Handlers.Add(new QuestHandler(pets, new Sequence($"{pets}: the Book of Skill from Grigor",
				new UsePortalScroll(TownOf(3)) { DoneWhen = () => InAny(TownOf(3)) || !UsePortalScroll.CanTry(CampaignState.Context?.Driver) },
				grigor) { DoneWhen = () => grigor.IsComplete }) {
				Active = _ => hasKey() && !grigor.IsComplete,
				Untracked = () => hasKey() && !grigor.IsComplete,
			});
		}

		/// <summary>
		/// Sceptre of God, the end of Act 3: done once Act 4 is reached (CampaignProgress.ActDone(3): The Aqueduct is
		/// Act 4's, and the waypoint panel only tells of the current act). It begins with the Tower Key (Piety's Pets).
		/// "Travel to the Imperial Gardens": the area is the first goal's (TravelTo: by the waypoint in The Ebony Barracks,
		/// and walk). "Unlock the Sceptre of God": by the Transitions/Act3_EpicDoor tile, click the "Locked Door" (it takes
		/// the Tower Key). "Travel to The Sceptre of God": the exit on that tile. "Ascend the Sceptre of God": up the
		/// Features/tower_transition_up stairs (labelled "Stairs"; the last, "The Upper Sceptre of God", an exit to that
		/// area), each floor cut off from the others, so the next stairs are the ones that can be walked to (ClimbStairs).
		/// In The Upper Sceptre of God, two more "Stairs", until the Features/tower_totowertop tile can be walked to: take
		/// its "Tower Rooftop" to Dominus's arena (the Tower/Act3_tower tile can be walked to). There, fight the hostiles
		/// (with none, wait on the Sceptre/Blank_invisible tile for them to wake up) until the exit to The Aqueduct is
		/// targetable, and take it.
		/// TODO: confirm live (not seen yet): the quest's name in the tracker, the exit labels "The Sceptre of God" and
		/// "The Aqueduct"
		/// </summary>
		private void SceptreOfGodQuest(FinishAllQuests quests) {
			const string sceptre = "Sceptre of God";
			Func<string> main = BuildPlan.MainSkill;
			bool done() => CampaignProgress.ActDone(3);
			bool hasKey() => Equipment.HasQuestItem("Tower Key");
			// (a tile of the part that can be walked to from the player, in this frame's terrain)
			bool reachable(string tilePathPart) => CampaignState.Context is CombatContext ctx && ctx.Terrain != null
				&& ctx.Terrain.TileFeatures(tilePathPart).Any(f => DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, f.GridPos, out _, 25));
			bool inArena() => InAny(UpperSceptreOfGod) && reachable("Tower/Act3_tower");
			// the exit to The Aqueduct targetable: Dominus is dead
			bool aqueductOpen() => GetEntities()?.Any(e => IsValid(e) && IsValid(e.GetComponent<AreaTransition>())
				&& string.Equals(e.GetComponent<Render>()?.Name, Aqueduct, StringComparison.OrdinalIgnoreCase)
				&& (e.GetComponent<Targetable>()?.IsTargetable ?? false)) ?? false;
			bool stepIs(string prefix) => StepOf(sceptre)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;
			bool unlocked() => InAny(SceptreOfGod, UpperSceptreOfGod) || stepIs("Travel to The Sceptre of God") || stepIs("Ascend");
			var epicDoor = Search.Tile("/Act3_EpicDoor");
			quests.Handlers.Add(new QuestHandler(sceptre, new Sequence(sceptre,
				new Activate("Locked Door") { Where = ImperialGardens, SearchAt = epicDoor, DoneWhen = unlocked },
				new TakeTransition(SceptreOfGod) { Where = ImperialGardens, SearchAt = epicDoor, DoneWhen = () => InAny(SceptreOfGod, UpperSceptreOfGod) },
				new ClimbStairs(SceptreOfGod, "/tower_transition_up", "/tower_totowertop", "Stairs", UpperSceptreOfGod) { Where = SceptreOfGod,
					DoneWhen = () => InAny(UpperSceptreOfGod) },
				new ClimbStairs(UpperSceptreOfGod, "/tower_transition_up", "/tower_totowertop", "Stairs") { Where = UpperSceptreOfGod, DoneWhen = inArena },
				new TakeTransition("Tower Rooftop") { Where = UpperSceptreOfGod, SearchAt = Search.Tile("/tower_totowertop"), DoneWhen = inArena },
				new FightUntil("Dominus's arena", aqueductOpen, main) { Where = UpperSceptreOfGod, SearchAt = Search.Tile("Sceptre/Blank_invisible") },
				new TakeTransition(Aqueduct) { Where = UpperSceptreOfGod, DoneWhen = done }) { DoneWhen = done }) {
				// (any step but a reward, until Act 4; "Travel to the Imperial Gardens" too: the first goal's Where goes there)
				Active = step => step != null && !done() && !step.EndsWith("for your reward", StringComparison.OrdinalIgnoreCase),
				// (not in the tracker, eg under another name: with the Tower Key, or in the Sceptre's areas)
				Untracked = () => !done() && (hasKey() || InAny(SceptreOfGod, UpperSceptreOfGod)),
			});
		}
	}
}
