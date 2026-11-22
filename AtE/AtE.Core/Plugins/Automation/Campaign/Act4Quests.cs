using System;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 4's quests: The Eternal Nightmare (the act's story, all the way to Malachai: its tracker steps carry the other
	/// quests' work too) and Return to Oriath (up The Ascent, to the portal that ends the act). Each is a table of rules
	/// (FirstOf): in an area, at a step of the quest tracker, given some conditions (the quest items had, the labels in
	/// sight), what to do; the first rule that holds wins.
	/// TODO: confirm live (not seen yet): most labels and names here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string DriedLake = "The Dried Lake", Mines1 = "The Mines Level 1", Mines2 = "The Mines Level 2",
			CrystalVeins = "The Crystal Veins", DaressosDream = "Daresso's Dream", GrandArena = "The Grand Arena",
			KaomsDream = "Kaom's Dream", KaomsStronghold = "Kaom's Stronghold", Belly1 = "The Belly of the Beast Level 1",
			Belly2 = "The Belly of the Beast Level 2", Harvest = "The Harvest", Ascent = "The Ascent";
		private const string EternalNightmare = "The Eternal Nightmare", ReturnToOriath = "Return to Oriath";

		private void Act4Quests(FinishAllQuests quests) {
			EternalNightmareQuest(quests);
			ReturnToOriathQuest(quests);
			// (their work is The Eternal Nightmare's steps; their rewards and trips are still done)
			foreach ( var quest in new[] { "Breaking the Seal", "An Indomitable Spirit", "The King of Fury", "The King of Desire", "Corpus Malachus" } ) {
				quests.Handlers.Add(QuestHandler.Elsewhere(quest, EternalNightmare));
			}
			quests.Handlers.Add(Stub("Niko's Fuel"));
			quests.Handlers.Add(Stub("Niko's Mine"));
			quests.Handlers.Add(Stub("Niko's Explosives"));
		}

		// ---- helpers for the rules ----

		/// <summary>Does the quest's tracker step start with prefix?</summary>
		private static bool StepStarts(string quest, string prefix) => StepOf(quest)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;

		/// <summary>Does any quest in the tracker have a step starting with prefix (eg, another quest's "Talk to Dialla in town")?</summary>
		private static bool AnyStepStarts(string prefix) => Town.QuestNames().Any(q => Town.QuestStep(q)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false);

		/// <summary>Is a label with this text (exactly, ignoring case) on the ground in sight?</summary>
		private static bool LabelShown(string text) => GetUI()?.LabelsOnGround?.GetAllLabels()?
			.Any(l => string.Equals(Ground.LabelText(l.Label)?.Trim(), text, StringComparison.OrdinalIgnoreCase)) ?? false;

		/// <summary>Is a label with text in it (eg, an item's name) on the ground in sight?</summary>
		private static bool ItemShown(string text) => GetUI()?.LabelsOnGround?.GetAllLabels()?
			.Any(l => (Ground.LabelText(l.Label)?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) ?? false;

		/// <summary>Is an entity match picks (alive or not) one the player can walk to (eg, a boss in the arena the player is in)?</summary>
		private static bool CanReach(Func<Entity, bool> match) {
			var terrain = GetTerrain();
			var p = GetPlayer()?.GetComponent<Positioned>();
			if ( terrain == null || !IsValid(p) ) return false;
			return GetEntities()?.Any(e => e != null && match(e) && e.GetComponent<Positioned>() is Positioned ep && IsValid(ep)
				&& DefaultNavigator.TrySnapReachable(terrain, p.GridPos, ep.GridPos, out _, 15)) ?? false;
		}

		/// <summary>Is a monster match picks in range and dead?</summary>
		private static bool SeenDead(Func<Entity, bool> match) => GetEntities()?.Any(e => e != null && match(e)
			&& e.GetComponent<Life>() is Life life && IsValid(life) && life.CurHP <= 0) ?? false;

		/// <summary>A unique monster whose name has name in it (eg, "King Kaom", not "Kaom's Chosen").</summary>
		private static Func<Entity, bool> UniqueNamed(string name) {
			var named = MonsterNamed(name);
			return e => named(e) && e.GetComponent<ObjectMagicProperties>()?.Rarity == Offsets.MonsterRarity.Unique;
		}

		/// <summary>Within reach cells of the area's waypoint (seen as an entity)?</summary>
		private static bool AtWaypoint(float reach = 15) {
			var wp = GetTerrain()?.Waypoint;
			var p = GetPlayer()?.GetComponent<Positioned>();
			return wp != null && wp.Seen && IsValid(p) && Combat.Distance(p.GridPos, wp.GridPos) <= reach;
		}

		/// <summary>Back to Highgate (see ToTown).</summary>
		private void ToHighgate(FirstOf rules, string label, string area, Func<bool> when) => ToTown(rules, 4, label, area, when);

		/// <summary>
		/// The Belly of the Beast Level 1's way to Level 2: one of its three Features/belly_tunnel_v01_01 tiles (one can't be
		/// walked to, one is the way in): the exit named Level 2 if one is; else, of those not seen yet that can be walked
		/// to, the left-most on screen (each is named once near; the way in is named at once, so it is told by elimination).
		/// </summary>
		private static Offsets.Vector2i? BellyTunnel(CombatContext ctx) {
			var exits = ctx.Terrain.Exits;
			var named = exits.FirstOrDefault(e => string.Equals(e.Destination, Belly2, StringComparison.OrdinalIgnoreCase));
			if ( named != null ) return named.GridPos;
			float ScreenX(Offsets.Vector2i g) => TryGridToScreen(g, ctx.Z, out Vector2 s) ? s.X : g.X - g.Y;
			return exits.Where(e => e.Destination == null && !e.Seen && (e.TileFile?.IndexOf("/belly_tunnel_v01_01", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
					&& DefaultNavigator.TrySnapReachable(ctx.Terrain, ctx.Grid, e.GridPos, out _, 25))
				.OrderBy(e => ScreenX(e.GridPos)).FirstOrDefault()?.GridPos;
		}

		/// <summary>
		/// The Eternal Nightmare, from The Aqueduct to Malachai (the user's table, 3.30; the step texts as the tracker shows
		/// them, matched by their start, ignoring case):
		///  - The Aqueduct: "Talk to Dialla" (Lady Dialla, out there); "Travel to Highgate" (the exit on the
		///    Town/mountiantown_connection tile)
		///  - Highgate: "Talk to Dialla", "Talk to Oyun"; "Find a way into the Mountain": with Deshret's Banner, click Deshret's
		///    Seal; once the seal is broken (The Mines Level 1's label in sight, or been in the mines), into The Mines Level 1;
		///    else (no banner) to The Dried Lake
		///  - The Dried Lake: with the banner, back to Highgate; the banner in sight, pick it up; else find and kill Voll (no
		///    tile known near him: explore), and pick up the banner he drops
		///  - The Mines Level 1: to The Mines Level 2 (Features/mine_areatransition)
		///  - The Mines Level 2: unless An Indomitable Spirit is done ("Talk to Tasuni in town ..." in the tracker, or its
		///    passive point had), find Deshret's Spirit and click it; then to The Crystal Veins (Features/mine_areatransition_v03)
		///  - The Crystal Veins: to the waypoint; "Talk to Dialla"
		///  - "Power the Rapture Device with the gems from Kaom and Daresso": without The Eye of Desire, through Daresso's Dream
		///    to The Grand Arena (Features/arena_areatransition), across its network of arenas (CrossTransitions: each
		///    colosseum_entrance into an arena, each colosseum_wall_door out of it) to the entrance_v01_02 tile, into The Ring of
		///    Blades, kill Daresso (AvoidSpam: his DaressoSwordTrap), take the Eye; back to Highgate, to The Crystal Veins;
		///    then through Kaom's Dream to Kaom's Stronghold (Features/lava_abyss_transition_entrance), through the door on the
		///    Features/lava_kaom3_transition_entrance tile, kill Kaom, take The Eye of Fury; by portal to Highgate, to The
		///    Crystal Veins; with both, talk to Lady Dialla
		///  - "Destroy the Beast Within": while another quest says "Talk to Dialla in town", by waypoint to Highgate (its
		///    reward); else into The Belly of the Beast Level 1, to Level 2 (BellyTunnel), into The Bowels of the Beast (by the
		///    Features/mapmarkeronly_bellyspikes tile), and kill Piety (PietyKill); "Talk to Piety"; "Travel to The Harvest"
		///    (by the nearest Features/belly_tunnel_level2 tile)
		///  - The Harvest: "Find a way into the Black Core": to the waypoint (to discover it); then each organ's arena, by its
		///    door ("Doedre's Arena" for Malachai's Lungs, a plain fight; "Maligaro's Arena" for Malachai's Heart, AvoidSpam;
		///    "Shavronne's Arena" for Malachai's Entrails, Orbit), each a mapmarkeronly_bellyspikes tile told apart by where it
		///    is; with all three, talk to Piety by the waypoint. "Kill Doedre to access the Black Core": Doedre's arena.
		///    "Enter the Black Core": with all three organs, Piety; with none (she has them), the door "The Black Core" by the
		///    waypoint. "Kill Malachai": to the Features/malachai_fight tile, wait for him, and kill him (AvoidSpam).
		/// TODO: Malachai's fight in detail (its phases, the second arena "The Black Heart"); then back in Highgate, Tasuni (a
		/// reward talk, see TakeQuestRewards)
		/// </summary>
		private void EternalNightmareQuest(FinishAllQuests quests) {
			const string en = EternalNightmare;
			Func<string> main = BuildPlan.MainSkill;
			string highgate = TownOf(4);
			bool at(string prefix) => StepStarts(en, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			var rules = new FirstOf(en);
			var dialla = TalkTo.NpcNamed("Dialla");

			// The Aqueduct
			rules.When("talk to Lady Dialla", Aqueduct, () => at("Talk to Dialla"), new TalkTo("Lady Dialla", dialla) { Where = Aqueduct });
			rules.When("to Highgate", Aqueduct, () => at("Travel to Highgate"),
				new TakeTransition("Highgate") { Where = Aqueduct, SearchAt = Search.Exit("Highgate", "/mountiantown_connection") });

			// Highgate
			rules.When("talk to Lady Dialla", highgate, () => at("Talk to Dialla"), new TalkTo("Lady Dialla", dialla) { Where = highgate });
			rules.When("talk to Oyun", highgate, () => at("Talk to Oyun"), new TalkTo("Oyun", TalkTo.NpcNamed("Oyun")) { Where = highgate });
			// the seal broken: the mines' way in shows, the player has been in the mines (this run), or The Crystal Veins'
			// waypoint is unlocked (latched; looked at with each rule of the step, wherever the player is)
			bool sealBroken = false;
			bool SealBroken() => sealBroken = sealBroken || InAny(Mines1, Mines2, CrystalVeins) || (InAny(highgate) && LabelShown(Mines1))
				|| WaypointPanel.IsKnownUnlocked(CrystalVeins);
			bool mountain() { SealBroken(); return at("Find a way into the Mountain"); }
			bool banner() => has("Deshret's Banner");
			rules.When("into the mines", highgate, () => mountain() && SealBroken(), new TakeTransition(Mines1) { Where = highgate });
			rules.When("break Deshret's Seal with the banner", highgate, () => mountain() && banner(),
				new Activate("Deshret's Seal") { Where = highgate, DoneWhen = SealBroken });
			rules.When("to The Dried Lake for Deshret's Banner", highgate, () => mountain() && !banner(), new TakeTransition(DriedLake) { Where = highgate });

			// The Dried Lake
			ToHighgate(rules, "back to Highgate with the banner", DriedLake, () => mountain() && banner());
			rules.When("pick up Deshret's Banner", DriedLake, () => mountain() && ItemShown("Deshret's Banner"), new PickUp("Deshret's Banner") { Where = DriedLake });
			rules.When("kill Voll", DriedLake, mountain, new Orbit("Voll", UniqueNamed("Voll"), main) { Where = DriedLake });
			rules.When("pick up Deshret's Banner", DriedLake, mountain, new PickUpQuestItem("Deshret's Banner") { Where = DriedLake });

			// The Mines
			rules.When("to The Mines Level 2", Mines1, mountain, new TakeTransition(Mines2) { Where = Mines1, SearchAt = Search.Exit(Mines2, "/mine_areatransition") });
			bool spiritDone() => AnyStepStarts("Talk to Tasuni in town") || CampaignProgress.HasPassiveFrom("An Indomitable Spirit");
			rules.When("free Deshret's Spirit", Mines2, () => mountain() && !spiritDone(), new Activate("Deshret's Spirit") { Where = Mines2 });
			rules.When("to The Crystal Veins", Mines2, mountain,
				new TakeTransition(CrystalVeins) { Where = Mines2, SearchAt = Search.Exit(CrystalVeins, "/mine_areatransition_v03") });

			// The Crystal Veins
			rules.When("to the waypoint", CrystalVeins, mountain, new GoToWaypoint() { Where = CrystalVeins });
			rules.When("talk to Lady Dialla", CrystalVeins, () => at("Talk to Dialla"), new TalkTo("Lady Dialla", dialla) { Where = CrystalVeins });

			// "Power the Rapture Device with the gems from Kaom and Daresso"
			bool power() => at("Power the Rapture Device");
			bool desire() => has("The Eye of Desire");
			bool fury() => has("The Eye of Fury");
			rules.When("give Lady Dialla the eyes", CrystalVeins, () => power() && desire() && fury(), new TalkTo("Lady Dialla", dialla) { Where = CrystalVeins });
			rules.When("to Kaom's Dream", CrystalVeins, () => power() && desire(), new TakeTransition(KaomsDream) { Where = CrystalVeins });
			rules.When("to Daresso's Dream", CrystalVeins, power, new TakeTransition(DaressosDream) { Where = CrystalVeins });
			rules.When("to The Crystal Veins", highgate, power, new EnterArea(CrystalVeins));
			// Daresso
			rules.When("to The Grand Arena", DaressosDream, () => power() && !desire(),
				new TakeTransition(GrandArena) { Where = DaressosDream, SearchAt = Search.Exit(GrandArena, "/arena_areatransition") });
			ToHighgate(rules, "back to Highgate with The Eye of Desire", GrandArena, () => power() && desire());
			ToHighgate(rules, "back to Highgate with The Eye of Desire", DaressosDream, () => power() && desire());
			var daresso = UniqueNamed("Daresso");
			bool inRing() => CanReach(daresso) || ItemShown("The Eye of Desire");
			var ring = Search.Tile("/colosseum_entrance_v01_02");
			rules.When("pick up The Eye of Desire", GrandArena, () => power() && ItemShown("The Eye of Desire"), new PickUp("The Eye of Desire") { Where = GrandArena });
			rules.When("kill Daresso", GrandArena, () => power() && inRing(), new AvoidSpam("Daresso", daresso, main) { Where = GrandArena });
			rules.When("pick up The Eye of Desire", GrandArena, () => power() && inRing(), new PickUpQuestItem("The Eye of Desire") { Where = GrandArena });
			rules.When("through the arenas to The Ring of Blades", GrandArena, power,
				new CrossTransitions("The Ring of Blades", ring, "/colosseum_entrance", "/colosseum_wall_door") { Where = GrandArena });
			rules.When("into The Ring of Blades", GrandArena, power, new TakeTransition("The Ring of Blades") { Where = GrandArena, SearchAt = ring, DoneWhen = inRing });
			// Kaom
			rules.When("to Kaom's Stronghold", KaomsDream, () => power() && desire() && !fury(),
				new TakeTransition(KaomsStronghold) { Where = KaomsDream, SearchAt = Search.Exit(KaomsStronghold, "/lava_abyss_transition_entrance") });
			ToHighgate(rules, "back to Highgate with The Eye of Fury", KaomsStronghold, () => power() && fury());
			ToHighgate(rules, "back to Highgate with The Eye of Fury", KaomsDream, () => power() && fury());
			var kaom = UniqueNamed("Kaom");
			bool inKaomsArena() => CanReach(kaom) || ItemShown("The Eye of Fury");
			var kaomsDoor = Search.Tile("/lava_kaom3_transition_entrance");
			rules.When("pick up The Eye of Fury", KaomsStronghold, () => power() && ItemShown("The Eye of Fury"), new PickUp("The Eye of Fury") { Where = KaomsStronghold });
			rules.When("kill Kaom", KaomsStronghold, () => power() && inKaomsArena(), new Orbit("Kaom", kaom, main) { Where = KaomsStronghold });
			rules.When("pick up The Eye of Fury", KaomsStronghold, () => power() && inKaomsArena(), new PickUpQuestItem("The Eye of Fury") { Where = KaomsStronghold });
			rules.When("into Kaom's arena", KaomsStronghold, power,
				new TakeTransition(null) { What = "door to Kaom's arena", Where = KaomsStronghold, SearchAt = kaomsDoor, DoneWhen = inKaomsArena });

			// "Destroy the Beast Within"
			bool beast() => at("Destroy the Beast Within");
			bool diallaInTown() => AnyStepStarts("Talk to Dialla in town");
			rules.When("by waypoint to Highgate (Dialla in town)", CrystalVeins, () => beast() && diallaInTown(), new EnterArea(highgate));
			rules.When("into The Belly of the Beast", CrystalVeins, beast, new TakeTransition(Belly1) { Where = CrystalVeins });
			rules.When("to The Crystal Veins", highgate, () => beast() && !diallaInTown(), new EnterArea(CrystalVeins));
			rules.When("to The Belly of the Beast Level 2", Belly1, beast, new TakeTransition(Belly2) { Where = Belly1, SearchAt = BellyTunnel });
			bool pietyMonster(Entity e) => e?.Path is string path && path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)
				&& string.Equals(e.GetComponent<Render>()?.Name?.Trim(), "Piety", StringComparison.OrdinalIgnoreCase);
			var bowels = Search.Tile("/mapmarkeronly_bellyspikes");
			rules.When("kill Piety", Belly2, () => beast() && CanReach(pietyMonster), new PietyKill("Piety", pietyMonster, main) { Where = Belly2, SearchAt = bowels });
			rules.When("into The Bowels of the Beast", Belly2, beast,
				new TakeTransition("The Bowels of the Beast") { Where = Belly2, SearchAt = bowels, DoneWhen = () => CanReach(pietyMonster) });
			rules.When("talk to Piety", Belly2, () => at("Talk to Piety"), new TalkTo("Piety", TalkTo.NpcNamed("Piety")) { Where = Belly2 });
			rules.When("to The Harvest", Belly2, () => at("Travel to The Harvest"),
				new TakeTransition(Harvest) { Where = Belly2, SearchAt = Search.Exit(Harvest, "/belly_tunnel_level2") });

			// The Harvest
			bool findWay() => at("Find a way into the Black Core");
			bool killDoedre() => at("Kill Doedre");
			bool enterCore() => at("Enter the Black Core");
			bool killMalachai() => IsKillStep(StepOf(en), "Malachai");
			const string lungs = "Malachai's Lungs", heart = "Malachai's Heart", entrails = "Malachai's Entrails";
			int organs() => new[] { lungs, heart, entrails }.Count(has);
			// the waypoint discovered: the panel says so, or the player was at it (this run)
			bool wpVisited = false;
			bool WaypointKnown() => wpVisited = wpVisited || WaypointPanel.IsKnownUnlocked(Harvest) || (InAny(Harvest) && AtWaypoint());
			Offsets.Vector2i? waypoint(CombatContext c) => c.Terrain.Waypoint?.GridPos;
			rules.When("to the waypoint (to discover it)", Harvest, () => findWay() && !WaypointKnown(), new GoToWaypoint() { Where = Harvest, DoneWhen = WaypointKnown });
			rules.When("to Piety with the organs", Harvest, () => (findWay() || enterCore()) && organs() == 3,
				new CrossTransitions("the waypoint", waypoint) { Where = Harvest, Avoid = new[] { "The Black Core" } });
			rules.When("give Piety the organs", Harvest, () => (findWay() || enterCore()) && organs() == 3,
				new TalkTo("Piety", TalkTo.NpcNamed("Piety")) { Where = Harvest, SearchAt = waypoint });
			// an organ's arena: its door (a mapmarkeronly_bellyspikes tile, by where it is), its boss, the organ it drops
			void Arena(string door, int x, int y, string organ, Func<Entity, bool> boss, Func<bool> when, Func<Goal> fight) {
				var tile = Search.TileNear("/mapmarkeronly_bellyspikes", x, y);
				bool inside() => CanReach(boss) || ItemShown(organ);
				Func<bool> wants = () => when() && !has(organ);
				rules.When($"pick up {organ}", Harvest, () => wants() && ItemShown(organ), new PickUp(organ) { Where = Harvest });
				var kill = fight();
				kill.Where = Harvest;
				rules.When($"{door}: the fight", Harvest, () => wants() && inside(), kill);
				rules.When($"pick up {organ}", Harvest, () => wants() && inside(), new PickUpQuestItem(organ) { Where = Harvest });
				rules.When($"to {door}", Harvest, wants, new CrossTransitions(door, tile) { Where = Harvest, Avoid = new[] { "The Black Core" } });
				rules.When($"into {door}", Harvest, wants, new TakeTransition(door) { Where = Harvest, SearchAt = tile, DoneWhen = inside });
			}
			var doedre = UniqueNamed("Doedre");
			Arena("Doedre's Arena", 1598, 701, lungs, doedre, () => (findWay() && WaypointKnown()) || killDoedre(),
				() => new FightUntil("Doedre's Arena", () => SeenDead(doedre) || ItemShown(lungs) || has(lungs), main));
			var maligaro = UniqueNamed("Maligaro");
			Arena("Maligaro's Arena", 517, 563, heart, maligaro, () => findWay() && WaypointKnown(),
				() => new AvoidSpam("Maligaro", maligaro, main));
			var shavronne = UniqueNamed("Shavronne");
			Arena("Shavronne's Arena", 724, 885, entrails, shavronne, () => findWay() && WaypointKnown(),
				() => new Orbit("Shavronne", shavronne, main));
			rules.When("to the waypoint", Harvest, () => enterCore() && organs() == 0,
				new CrossTransitions("the waypoint", waypoint) { Where = Harvest, Avoid = new[] { "The Black Core" } });
			rules.When("into The Black Core", Harvest, () => enterCore() && organs() == 0,
				new TakeTransition("The Black Core") { Where = Harvest, SearchAt = waypoint });
			// Malachai (TODO: his phases; the second arena)
			var malachai = UniqueNamed("Malachai");
			var malachaiFight = Search.Tile("/malachai_fight");
			rules.When("into The Black Heart", Harvest, () => killMalachai() && !CanReach(malachai) && LabelShown("The Black Heart"),
				new TakeTransition("The Black Heart") { Where = Harvest });
			rules.When("to Malachai", Harvest, killMalachai, new CrossTransitions("Malachai's arena", malachaiFight) { Where = Harvest });
			rules.When("kill Malachai", Harvest, killMalachai, new AvoidSpam("Malachai", malachai, main) { Where = Harvest, SearchAt = malachaiFight });
			// (in Highgate, eg after a trip to town, at a step of The Harvest's: back there)
			rules.When("to The Harvest", highgate, () => findWay() || killDoedre() || enterCore() || killMalachai(), new EnterArea(Harvest));

			quests.Handlers.Add(new QuestHandler(en, rules) {
				// (any step a rule holds at: its "Travel to" steps too, eg "Travel to Highgate" from The Aqueduct)
				Active = _ => rules.Holding() != null,
				// (the "Talk to" steps out in the areas: Lady Dialla in The Aqueduct and The Crystal Veins, Oyun, Piety)
				FieldNpcs = new[] { "Dialla", "Lady Dialla", "Oyun", "Piety" },
			});
		}

		/// <summary>
		/// Return to Oriath: in Highgate, "Access the Ascent from the eastern side of Highgate": take the exit to The Ascent;
		/// there, "Climb the Ascent to find the portal back to Oriath": to the Features/ascent_summit_v01_01 tile, click the
		/// "Lever", and take the "Oriath Portal" (to Act 5: the act is done).
		/// TODO: confirm live (not seen yet): the labels "The Ascent", "Lever", "Oriath Portal"
		/// </summary>
		private void ReturnToOriathQuest(FinishAllQuests quests) {
			const string rto = ReturnToOriath;
			string highgate = TownOf(4);
			var rules = new FirstOf(rto);
			var summit = Search.Tile("/ascent_summit");
			bool climb() => StepStarts(rto, "Climb the Ascent");
			rules.When("to The Ascent", highgate, () => StepStarts(rto, "Access the Ascent"), new TakeTransition(Ascent) { Where = highgate });
			rules.When("pull the Lever", Ascent, () => climb() && !LabelShown("Oriath Portal"),
				new Activate("Lever") { Where = Ascent, SearchAt = summit, DoneWhen = () => LabelShown("Oriath Portal") });
			rules.When("through the Oriath Portal", Ascent, climb, new TakeTransition("Oriath Portal") { Where = Ascent, SearchAt = summit });
			quests.Handlers.Add(new QuestHandler(rto, rules) { Active = _ => rules.Holding() != null });
		}
	}
}
