using System;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 7's quests, as tables of rules (FirstOf, as in Act4Quests): Essence of the Artist (Maligaro's Map, from The
	/// Crypt, to his Sanctum), Web of Secrets (the Black Venom to Silk, for the Obsidian Key), The Master of a Million Faces
	/// (through The Den and The Ashen Fields to Greust) and The Mother of Spiders (to Arakaali, by way of Kishara's Star,
	/// Gruthkul and the Fireflies: Lighting the Way and Queen of Despair are its steps).
	/// TODO: confirm live (not seen yet): the labels, names and tiles here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string BrokenBridge7 = "The Broken Bridge", Crossroads7 = "The Crossroads", FellshrineRuins = "The Fellshrine Ruins",
			Crypt7 = "The Crypt", ChamberOfSins1_7 = "The Chamber of Sins Level 1", ChamberOfSins2_7 = "The Chamber of Sins Level 2",
			MaligarosSanctum = "Maligaro's Sanctum", Den7 = "The Den", AshenFields = "The Ashen Fields", NorthernForest7 = "The Northern Forest",
			Causeway = "The Causeway", VaalCity = "The Vaal City", DreadThicket = "The Dread Thicket",
			TempleOfDecay1 = "The Temple of Decay Level 1", TempleOfDecay2 = "The Temple of Decay Level 2", SarnRamparts = "The Sarn Ramparts";
		private const string EssenceOfTheArtist = "Essence of the Artist", WebOfSecrets = "Web of Secrets",
			MasterOfAMillionFaces = "The Master of a Million Faces", MotherOfSpiders = "The Mother of Spiders",
			LightingTheWay = "Lighting the Way", QueenOfDespair = "Queen of Despair", KisharasStar = "Kishara's Star",
			InMemoryOfGreust = "In Memory of Greust";
		/// <summary>The waypoint panel's name for Act 7's town (UseWaypoint takes the panel's names).</summary>
		private const string BridgeEncampment = "The Bridge Encampment";

		private void Act7Quests(FinishAllQuests quests) {
			quests.Handlers.Add(Stub("The Silver Locket"));
			EssenceOfTheArtistQuest(quests);
			WebOfSecretsQuest(quests);
			MasterOfAMillionFacesQuest(quests);
			// (Greust's Necklace goes to the Azmeri Shrine on The Mother of Spiders' way; its reward is still taken)
			quests.Handlers.Add(QuestHandler.Elsewhere(InMemoryOfGreust, MotherOfSpiders));
			// (Gruthkul, Kishara's Star and the Fireflies are on The Mother of Spiders' way; their rewards are still taken)
			quests.Handlers.Add(QuestHandler.Elsewhere(QueenOfDespair, MotherOfSpiders));
			quests.Handlers.Add(QuestHandler.Elsewhere(KisharasStar, MotherOfSpiders));
			quests.Handlers.Add(QuestHandler.Elsewhere(LightingTheWay, MotherOfSpiders));
			MotherOfSpidersQuest(quests);
			quests.Handlers.Add(Stub("The Lord's Labyrinth"));
			quests.Handlers.Add(Stub("No Time like the Present"));
		}

		/// <summary>
		/// Essence of the Artist (the user's table):
		///  - "Travel to the Fellshrine Crypt": from The Bridge Encampment to The Broken Bridge; there to The Crossroads (the
		///    Transitions/bridgeconnection_v01_01 tile); there, to the waypoint (to discover it), then to The Fellshrine Ruins
		///    (road_wall_gate_v01_01_reverse); there, to The Crypt (Transitions/church_dungeon_entrance_v01_01)
		///  - The Crypt, "Find Maligaro's Map": click the Sarcophagus (church_sarcophagus_entrance_01) and take the "Stairs"
		///    it opens; then click the "Container of Sins" (scriptorium_crevice_corner_concave_v01_02) and pick up the map
		///  - "Travel to the Chamber of Sins", with the map: back to town (by a Portal Scroll if one can be tried), by
		///    waypoint to The Crossroads, and into The Chamber of Sins Level 1 (temple_entrance_v01_01)
		///  - The Chamber of Sins Level 1, "Use Maligaro's Map in his Map Device": by the waypoint, the "Map Device" (UseMapDevice)
		///  - Maligaro's Sanctum, "Find Maligaro and kill him": to the swamppool_v01_01 tile, into "Maligaro's Workshop", kill
		///    Maligaro (AvoidSpam), and pick up the Black Venom he drops; "Talk to Helena for your Reward": a Portal Scroll
		///    (back to the map device)
		/// TODO: Maligaro's effects (AvoidSpam's hazards) and his immunity phases; the Sarcophagus's label (the user's notes say
		/// "Sarcophogus")
		/// </summary>
		private void EssenceOfTheArtistQuest(FinishAllQuests quests) {
			const string ea = EssenceOfTheArtist;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(7);
			bool at(string prefix) => StepStarts(ea, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(ea);

			// "Travel to the Fellshrine Crypt"
			bool toCrypt() => at("Travel to the Fellshrine Crypt");
			rules.When("to The Broken Bridge", town, toCrypt, new TakeTransition(BrokenBridge7) { Where = town });
			rules.When("to The Crossroads", BrokenBridge7, toCrypt,
				new TakeTransition(Crossroads7) { Where = BrokenBridge7, SearchAt = Search.Exit(Crossroads7, "Transitions/bridgeconnection_v01_01") });
			var crossroadsWp = WaypointKnownLatch(Crossroads7);
			rules.When("to the waypoint (to discover it)", Crossroads7, () => toCrypt() && !crossroadsWp(),
				new GoToWaypoint() { Where = Crossroads7, DoneWhen = crossroadsWp });
			rules.When("to The Fellshrine Ruins", Crossroads7, toCrypt,
				new TakeTransition(FellshrineRuins) { Where = Crossroads7, SearchAt = Search.Exit(FellshrineRuins, "road_wall_gate_v01_01_reverse") });
			rules.When("to The Crypt", FellshrineRuins, toCrypt,
				new TakeTransition(Crypt7) { Where = FellshrineRuins, SearchAt = Search.Exit(Crypt7, "Transitions/church_dungeon_entrance_v01_01") });

			// The Crypt: "Find Maligaro's Map"
			const string map = "Maligaro's Map";
			bool findMap() => at("Find Maligaro's Map") && !has(map);
			const string containerTile = "scriptorium_crevice_corner_concave_v01_02";
			bool downstairs() => TileReachable(containerTile);
			var sarcophagus = Search.Tile("church_sarcophagus_entrance_01");
			var container = Search.Tile(containerTile);
			rules.When("pick up Maligaro's Map", Crypt7, () => findMap() && ItemShown(map), new PickUp(map) { Where = Crypt7 });
			rules.When("open the Container of Sins", Crypt7, () => findMap() && downstairs(),
				new Activate("Container of Sins") { Where = Crypt7, SearchAt = container, DoneWhen = () => ItemShown(map) || has(map) });
			rules.When("pick up Maligaro's Map", Crypt7, () => findMap() && downstairs(),
				new PickUpQuestItem(map) { Where = Crypt7 });
			rules.When("down the Stairs", Crypt7, () => findMap() && TargetableLabelShown("Stairs"),
				new TakeTransition("Stairs") { Where = Crypt7, SearchAt = sarcophagus, DoneWhen = downstairs });
			rules.When("open the Sarcophagus", Crypt7, findMap,
				new Activate("Sarcophagus") { Where = Crypt7, SearchAt = sarcophagus, DoneWhen = () => TargetableLabelShown("Stairs") || downstairs() });

			// "Travel to the Chamber of Sins", with the map
			bool toChamber() => at("Travel to the Chamber of Sins") && has(map);
			ToTown(rules, 7, "back to town with Maligaro's Map", Crypt7, toChamber);
			rules.When("by waypoint to The Crossroads", town, toChamber, new UseWaypoint(Crossroads7) { Where = town });
			rules.When("to The Chamber of Sins Level 1", Crossroads7, toChamber,
				new TakeTransition(ChamberOfSins1_7) { Where = Crossroads7, SearchAt = Search.Exit(ChamberOfSins1_7, "temple_entrance_v01_01") });

			// The Chamber of Sins Level 1: the Map Device, by the waypoint
			rules.When("use Maligaro's Map in his Map Device", ChamberOfSins1_7, () => at("Use Maligaro's Map"),
				new UseMapDevice("Map Device", map, MaligarosSanctum) { Where = ChamberOfSins1_7, SearchAt = c => c.Terrain.Waypoint?.GridPos });

			// Maligaro's Sanctum (TODO: his effects, for AvoidSpam; his immunity phases)
			const string venom = "Black Venom";
			bool kill() => at("Find Maligaro");
			var maligaro = UniqueNamed("Maligaro");
			var workshop = new TakeTransition("Maligaro's Workshop") { Where = MaligarosSanctum, SearchAt = Search.Tile("swamppool_v01_01"),
				DoneWhen = () => CanReach(maligaro) };
			rules.When("pick up the Black Venom", MaligarosSanctum, () => !has(venom) && ItemShown(venom), new PickUp(venom) { Where = MaligarosSanctum });
			rules.When("kill Maligaro", MaligarosSanctum, () => kill() && CanReach(maligaro),
				new AvoidSpam("Maligaro, The Artist", maligaro, main) { Where = MaligarosSanctum });
			rules.When("the rest of Maligaro's fight", MaligarosSanctum, () => kill() && workshop.Taken,
				new FightUntil("Maligaro's Workshop", () => !kill() || CanReach(maligaro), main) { Where = MaligarosSanctum });
			rules.When("into Maligaro's Workshop", MaligarosSanctum, kill, workshop);
			// (the step moves on as he dies, maybe before the venom shows)
			rules.When("pick up the Black Venom", MaligarosSanctum, () => AtReward(ea) && !has(venom),
				new PickUpQuestItem($"the {venom}") { Where = MaligarosSanctum });
			rules.When("out by a Portal Scroll", MaligarosSanctum, () => AtReward(ea), new UsePortalScroll(ChamberOfSins1_7));

			quests.Handlers.Add(new QuestHandler(ea, rules) { Active = _ => rules.Holding() != null });
		}

		/// <summary>
		/// Web of Secrets: "Bring the Black Venom to Silk": out of Maligaro's Sanctum by a Portal Scroll, and talk to Silk in
		/// The Chamber of Sins Level 1, for the Obsidian Key.
		/// TODO: confirm live (not seen yet): that Silk's talk gives the key (not a reward window)
		/// </summary>
		private void WebOfSecretsQuest(FinishAllQuests quests) {
			const string ws = WebOfSecrets;
			bool bring() => StepStarts(ws, "Bring the Black Venom") && Equipment.HasQuestItem("Black Venom");
			var rules = new FirstOf(ws);
			rules.When("out by a Portal Scroll", MaligarosSanctum, bring, new UsePortalScroll(ChamberOfSins1_7));
			rules.When("talk to Silk", ChamberOfSins1_7, bring,
				new TalkTo("Silk", TalkTo.NpcNamed("Silk")) { Where = ChamberOfSins1_7, DoneWhen = () => Equipment.HasQuestItem("Obsidian Key") });
			quests.Handlers.Add(new QuestHandler(ws, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Silk" },
			});
		}

		/// <summary>
		/// The Master of a Million Faces (the user's table):
		///  - The Chamber of Sins Level 1, "You can now access the Chamber of Sins Level 2" (with the Obsidian Key): to Level 2
		///    (templeruinforest_exit_down_v01_01)
		///  - Level 2, "Use the Obsidian Key to access the Den": click the "Secret Passage" (templeruinforest_maligaro_passage)
		///    and take the exit to The Den
		///  - "Find the old Forest Encampment": The Den, to the waypoint, then to The Ashen Fields (forestcave_exit_up_v01_01);
		///    The Ashen Fields, to the waypoint, then toward <618, 510>, into "The Fortress Encampment", and fight there
		///  - "Kill Greust": the same; once in the arena (or if <618, 510> can't be walked to: locked in), fight until the step
		///    moves on
		///  - "Talk to Eramir for your Reward": from The Ashen Fields to The Northern Forest, and by waypoint to town
		/// TODO: confirm live (not seen yet): which quest's steps the first two are (any quest's are taken), Greust's step
		/// </summary>
		private void MasterOfAMillionFacesQuest(FinishAllQuests quests) {
			const string mm = MasterOfAMillionFaces;
			Func<string> main = BuildPlan.MainSkill;
			bool at(string prefix) => StepStarts(mm, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(mm);

			// the Chamber of Sins, with the Obsidian Key
			bool access() => StepStarts(mm, "You can now access") || AnyStepStarts("You can now access the Chamber of Sins Level 2");
			rules.When("to The Chamber of Sins Level 2", ChamberOfSins1_7, () => access() && has("Obsidian Key"),
				new TakeTransition(ChamberOfSins2_7) { Where = ChamberOfSins1_7, SearchAt = Search.Exit(ChamberOfSins2_7, "templeruinforest_exit_down_v01_01") });
			bool useKey() => StepStarts(mm, "Use the Obsidian Key") || AnyStepStarts("Use the Obsidian Key");
			var passage = Search.Tile("templeruinforest_maligaro_passage");
			rules.When("to The Den", ChamberOfSins2_7, () => useKey() && TargetableLabelShown(Den7),
				new TakeTransition(Den7) { Where = ChamberOfSins2_7, SearchAt = passage });
			rules.When("open the Secret Passage", ChamberOfSins2_7, () => useKey() && has("Obsidian Key"),
				new Activate("Secret Passage") { Where = ChamberOfSins2_7, SearchAt = passage, DoneWhen = () => TargetableLabelShown(Den7) });
			rules.When("to The Den", ChamberOfSins2_7, useKey, new TakeTransition(Den7) { Where = ChamberOfSins2_7, SearchAt = passage });

			// "Find the old Forest Encampment", "Kill Greust"
			bool find() => at("Find the old Forest Encampment");
			bool kill() => at("Kill ");
			var denWp = WaypointKnownLatch(Den7);
			rules.When("to the waypoint (to discover it)", Den7, () => find() && !denWp(), new GoToWaypoint() { Where = Den7, DoneWhen = denWp });
			rules.When("to The Ashen Fields", Den7, find,
				new TakeTransition(AshenFields) { Where = Den7, SearchAt = Search.Exit(AshenFields, "forestcave_exit_up_v01_01") });
			var ashenWp = WaypointKnownLatch(AshenFields);
			rules.When("to the waypoint (to discover it)", AshenFields, () => (find() || kill()) && !ashenWp(),
				new GoToWaypoint() { Where = AshenFields, DoneWhen = ashenWp });
			var fortress = Search.At(618, 510);
			var greust = UniqueNamed("Greust");
			var fortressDoor = new TakeTransition("The Fortress Encampment") { Where = AshenFields, SearchAt = fortress };
			// (locked in the arena, eg after a restart there: <618, 510> can't be walked to)
			bool lockedIn() => CampaignState.Context is CombatContext ctx && ctx.Terrain != null
				&& !DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, new Offsets.Vector2i(618, 510), out _, 25);
			bool inFortress() => fortressDoor.Taken || CanReach(greust) || lockedIn();
			fortressDoor.DoneWhen = inFortress;
			rules.When("fight in The Fortress Encampment", AshenFields, () => (find() || kill()) && inFortress(),
				new FightUntil("The Fortress Encampment", () => !(find() || kill()), main) { Where = AshenFields, SearchAt = fortress });
			rules.When("into The Fortress Encampment", AshenFields, () => find() || kill(), fortressDoor);

			// "Talk to Eramir for your Reward"
			rules.When("to The Northern Forest", AshenFields, () => AtReward(mm), new TakeTransition(NorthernForest7) { Where = AshenFields });
			rules.When("by waypoint to town", NorthernForest7, () => AtReward(mm), new UseWaypoint(BridgeEncampment) { Where = NorthernForest7 });

			quests.Handlers.Add(new QuestHandler(mm, rules) { Active = _ => rules.Holding() != null });
		}

		/// <summary>
		/// The Mother of Spiders (the user's table):
		///  - "Find Arakaali's temple and save Silk": from town by waypoint to The Northern Forest; there, with Greust's
		///    Necklace, click the "Azmeri Shrine" (forest_azmeri_shrine); else to The Causeway (forestriver_plinthtransition_v01_01);
		///    there, to the waypoint (to discover it), then open "Kishara's Lockbox" (by vaal_stairs_bottom_v01_01), pick up
		///    Kishara's Star, and take the exit to The Vaal City (the same tile)
		///  - "Find a way into the Arakaali's Temple": in The Vaal City, talk to Yeena by the waypoint. Then Lighting the Way's
		///    "Search the Dread Thicket for Fireflies": by waypoint to The Northern Forest, into The Dread Thicket
		///    (grovewall_entrance_v01_01); there, while Queen of Despair is not at its reward, into Gruthkul's arena
		///    (forestcave_entrance_hole_v01_01), kill her, and out by "The Dread Thicket"; then the 7 Fireflies (the area's
		///    QuestObject minimap icons, see OpenQuestObjects). "Deliver the Fireflies to Yeena": back to town, by waypoint to
		///    The Vaal City, and talk to Yeena
		///  - "Enter Arakaali's temple and save Silk": The Vaal City, to The Temple of Decay Level 1
		///  - "Find Silk and Arakaali": Level 1, down the "Stairs" (dungeon_stairs_down_v01_01) to the one to Level 2; Level 2,
		///    down the "Stairs" until the dungeon_web_inca_exit_v03_01 tile can be walked to, into "Arakaali's Web", and kill
		///    Arakaali (AvoidSpam)
		///  - "The exit to Act 8 is now open": Level 2, the exit to The Sarn Ramparts
		/// TODO: Gruthkul's and Arakaali's fights in detail (AvoidSpam's hazards); confirm live (not seen yet): the label of
		/// Gruthkul's arena, the Fireflies' icons and item, Kishara's Lockbox
		/// </summary>
		private void MotherOfSpidersQuest(FinishAllQuests quests) {
			const string ms = MotherOfSpiders;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(7);
			bool at(string prefix) => StepStarts(ms, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(ms);

			// "Find Arakaali's temple and save Silk"
			bool findTemple() => at("Find Arakaali's temple");
			rules.When("by waypoint to The Northern Forest", town, findTemple, new UseWaypoint(NorthernForest7) { Where = town });
			const string necklace = "Greust's Necklace";
			rules.When("Greust's Necklace to the Azmeri Shrine", NorthernForest7, () => findTemple() && has(necklace),
				new Activate("Azmeri Shrine") { Where = NorthernForest7, SearchAt = Search.Tile("forest_azmeri_shrine"), DoneWhen = () => !has(necklace) });
			rules.When("to The Causeway", NorthernForest7, findTemple,
				new TakeTransition(Causeway) { Where = NorthernForest7, SearchAt = Search.Exit(Causeway, "forestriver_plinthtransition_v01_01") });
			var causewayWp = WaypointKnownLatch(Causeway);
			rules.When("to the waypoint (to discover it)", Causeway, () => findTemple() && !causewayWp(),
				new GoToWaypoint() { Where = Causeway, DoneWhen = causewayWp });
			const string star = "Kishara's Star";
			// (the star had: in the backpack, or its quest started or done)
			bool starHad() => has(star) || StepOf(KisharasStar) != null || QuestFinished(KisharasStar);
			var stairsBottom = Search.Tile("vaal_stairs_bottom_v01_01");
			rules.When("pick up Kishara's Star", Causeway, () => findTemple() && !has(star) && ItemShown(star), new PickUp(star) { Where = Causeway });
			rules.When("open Kishara's Lockbox", Causeway, () => findTemple() && !starHad(),
				new OpenChest("Kishara's Lockbox", "Kishara's Lockbox") { Where = Causeway, SearchAt = stairsBottom, DoneWhen = () => ItemShown(star) || starHad() });
			rules.When("to The Vaal City", Causeway, findTemple,
				new TakeTransition(VaalCity) { Where = Causeway, SearchAt = Search.Exit(VaalCity, "vaal_stairs_bottom_v01_01") });

			// "Find a way into the Arakaali's Temple", with Lighting the Way's steps
			bool findWay() => at("Find a way into");
			bool fireflies() => findWay() && StepStarts(LightingTheWay, "Search the Dread Thicket");
			bool deliver() => findWay() && StepStarts(LightingTheWay, "Deliver the Fireflies");
			var yeena = TalkTo.NpcNamed("Yeena");
			Offsets.Vector2i? waypoint(CombatContext c) => c.Terrain.Waypoint?.GridPos;
			rules.When("give Yeena the Fireflies", VaalCity, deliver,
				new TalkTo("Yeena", yeena) { Where = VaalCity, SearchAt = waypoint, DoneWhen = () => !deliver() });
			rules.When("by waypoint to The Northern Forest", VaalCity, fireflies, new UseWaypoint(NorthernForest7) { Where = VaalCity });
			rules.When("talk to Yeena", VaalCity, findWay,
				new TalkTo("Yeena", yeena) { Where = VaalCity, SearchAt = waypoint, DoneWhen = () => StepOf(LightingTheWay) != null });
			rules.When("to The Dread Thicket", NorthernForest7, fireflies,
				new TakeTransition(DreadThicket) { Where = NorthernForest7, SearchAt = Search.Exit(DreadThicket, "grovewall_entrance_v01_01") });
			// The Dread Thicket: Gruthkul, then the Fireflies
			var gruthkul = UniqueNamed("Gruthkul");
			bool gruthkulDead = false;
			bool queenDone() => gruthkulDead = gruthkulDead || SeenDead(gruthkul) || AtReward(QueenOfDespair) || QuestFinished(QueenOfDespair);
			var arenaDoor = new TakeTransition(null) { What = "way into Gruthkul's arena", Where = DreadThicket,
				SearchAt = Search.Tile("forestcave_entrance_hole_v01_01"), DoneWhen = () => CanReach(gruthkul) };
			var outDoor = new TakeTransition(DreadThicket) { Where = DreadThicket };
			// in the arena: its door taken, or Gruthkul (alive or not) can be walked to; until the way out is taken
			bool inArena = false;
			bool InArena() => inArena = (inArena || arenaDoor.Taken || CanReach(gruthkul)) && !outDoor.Taken;
			rules.When("out of Gruthkul's arena", DreadThicket, () => fireflies() && queenDone() && InArena(), outDoor);
			rules.When("kill Gruthkul", DreadThicket, () => fireflies() && !queenDone() && CanReach(gruthkul),
				new Orbit("Gruthkul, Mother of Despair", gruthkul, main) { Where = DreadThicket });
			rules.When("the rest of Gruthkul's fight", DreadThicket, () => fireflies() && !queenDone() && arenaDoor.Taken,
				new FightUntil("Gruthkul's arena", () => queenDone() || CanReach(gruthkul), main) { Where = DreadThicket });
			rules.When("into Gruthkul's arena", DreadThicket, () => fireflies() && !queenDone(), arenaDoor);
			rules.When("the Fireflies", DreadThicket, fireflies, new OpenQuestObjects("Firefly", 7) { Where = DreadThicket });
			ToTown(rules, 7, "back to town with the Fireflies", DreadThicket, deliver);
			rules.When("by waypoint to The Vaal City", town, deliver, new UseWaypoint(VaalCity) { Where = town });

			// "Enter Arakaali's temple and save Silk", "Find Silk and Arakaali"
			rules.When("to The Temple of Decay Level 1", VaalCity, () => at("Enter Arakaali's temple"),
				new TakeTransition(TempleOfDecay1) { Where = VaalCity });
			bool findSilk() => at("Find Silk and Arakaali");
			rules.When("down to The Temple of Decay Level 2", TempleOfDecay1, findSilk,
				new ClimbStairs(TempleOfDecay1, "/dungeon_stairs_down_v01_01", null, "Stairs", TempleOfDecay2) { Where = TempleOfDecay1,
					DoneWhen = () => InAny(TempleOfDecay2) });
			// Level 2 (TODO: Arakaali's effects, for AvoidSpam)
			bool exitOpen() => AnyStepStarts("The exit to Act 8");
			rules.When("to The Sarn Ramparts", TempleOfDecay2, exitOpen, new TakeTransition(SarnRamparts) { Where = TempleOfDecay2 });
			const string webTile = "/dungeon_web_inca_exit_v03_01";
			var arakaali = UniqueNamed("Arakaali");
			var webDoor = new TakeTransition("Arakaali's Web") { Where = TempleOfDecay2, SearchAt = Search.Tile(webTile), DoneWhen = () => CanReach(arakaali) };
			rules.When("kill Arakaali", TempleOfDecay2, () => findSilk() && CanReach(arakaali),
				new AvoidSpam("Arakaali", arakaali, main) { Where = TempleOfDecay2 });
			rules.When("the rest of Arakaali's fight", TempleOfDecay2, () => findSilk() && webDoor.Taken,
				new FightUntil("Arakaali's Web", () => !findSilk() || CanReach(arakaali), main) { Where = TempleOfDecay2 });
			rules.When("into Arakaali's Web", TempleOfDecay2, () => findSilk() && TileReachable(webTile), webDoor);
			rules.When("down the stairs to Arakaali's Web", TempleOfDecay2, findSilk,
				new ClimbStairs(TempleOfDecay2, "/dungeon_stairs_down_v01_01", webTile, "Stairs") { Where = TempleOfDecay2 });

			quests.Handlers.Add(new QuestHandler(ms, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Yeena" },
			});
		}
	}
}
