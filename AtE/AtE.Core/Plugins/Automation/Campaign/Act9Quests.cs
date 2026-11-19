using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Act 9's quests, as one table of rules (FirstOf, as in Act4Quests): Recurring Nightmare, whose long steps ("Find the
	/// Basilisk Acid", "Find the Trarthan Powder") cover The Storm Blade (the Storm-Weathered Chest), Queen of the Sands
	/// (Shakari) and The Ruler of Highgate (Garukhan's Sekhema Feather); then Sin, the three spirits and the Depraved Trinity
	/// in The Rotting Core.
	/// TODO: confirm live (not seen yet): the labels, names and tiles here are from the user's notes, not seen as entities
	/// </summary>
	public partial class CampaignRoutine {
		private const string Descent9 = "The Descent", VastiriDesert = "The Vastiri Desert", Oasis9 = "The Oasis", Foothills9 = "The Foothills",
			BoilingLake9 = "The Boiling Lake", Tunnel9 = "The Tunnel", Quarry9 = "The Quarry", Refinery9 = "The Refinery",
			BellyOfTheBeast9 = "The Belly of the Beast", RottingCore9 = "The Rotting Core";
		private const string RecurringNightmare = "Recurring Nightmare", StormBlade = "The Storm Blade", QueenOfTheSands = "Queen of the Sands",
			RulerOfHighgate = "The Ruler of Highgate";

		private void Act9Quests(FinishAllQuests quests) {
			// (the Storm Blade, Shakari and Garukhan are on Recurring Nightmare's way; their rewards are still taken)
			quests.Handlers.Add(QuestHandler.Elsewhere(StormBlade, RecurringNightmare));
			quests.Handlers.Add(Stub("Fastis Fortuna"));
			quests.Handlers.Add(QuestHandler.Elsewhere(RulerOfHighgate, RecurringNightmare));
			quests.Handlers.Add(QuestHandler.Elsewhere(QueenOfTheSands, RecurringNightmare));
			RecurringNightmareQuest(quests);
			quests.Handlers.Add(Stub("The Immortal Syndicate"));
		}

		// ---- helpers for the rules ----

		/// <summary>Any hostile within radius cells of the player?</summary>
		private static bool HostilesNear(float radius) => CampaignState.Context is CombatContext c && Fight.AnyNear(c, radius);

		/// <summary>
		/// The nearest of the tiles with tilePathPart the player has not been within reach cells of yet (this run), eg the
		/// two rockwall_transition tiles in The Oasis, only one of them the way into The Sand Pit; null once all are tried.
		/// </summary>
		private static Func<CombatContext, Offsets.Vector2i?> UntriedTile(string tilePathPart, float reach) {
			var tried = new HashSet<Offsets.Vector2i>();
			return ctx => {
				var left = ctx.Terrain.TileFeatures(tilePathPart).Select(f => f.GridPos).Where(p => !tried.Contains(p)).ToList();
				foreach ( var p in left.Where(p => Navigate.IsNear(ctx, p, reach, out _, out _)).ToList() ) {
					tried.Add(p);
					left.Remove(p);
					Log($"Campaign: tried the {tilePathPart} tile at {p}");
				}
				return left.Count == 0 ? (Offsets.Vector2i?)null : left.OrderBy(p => Combat.Distance(ctx.Grid, p)).First();
			};
		}

		/// <summary>
		/// One of the spirits in The Rotting Core (eg, Doedre: "Defeat the Spirit of Doedre"), rules in that area: from The
		/// Black Core through its door (eg, "Doedre's Despair"); there, toward the one mapmarkeronly_bellyspikes_v01_01 tile
		/// that can be walked to, into its arena (eg, "Doedre's Arena"), kill the spirit (AvoidSpam), and back by "The Black
		/// Core" (once the spirit is seen dead, or the step moved on).
		/// </summary>
		private static void Spirit(FirstOf rules, string name, Func<bool> step, Func<bool> any, string despair, string arena) {
			Func<string> main = BuildPlan.MainSkill;
			const string spikes = "mapmarkeronly_bellyspikes_v01_01";
			var boss = UniqueNamed(name);
			bool alive(Entity e) => boss(e) && IsAlive(e);
			var dead = Latch(() => SeenDead(boss));
			var despairDoor = new TakeTransition(despair) { Where = RottingCore9, DoneWhen = () => TileReachable(spikes) };
			var arenaDoor = new TakeTransition(arena) { Where = RottingCore9, SearchAt = ReachableTile(spikes), DoneWhen = () => CanReach(boss) };
			var outDoor = new TakeTransition("The Black Core") { Where = RottingCore9 };
			bool inArena = false;
			bool InArena() => inArena = (inArena || arenaDoor.Taken || CanReach(boss)) && !outDoor.Taken;
			rules.When($"out of {arena}", RottingCore9, () => any() && InArena() && (dead() || !step()), outDoor);
			rules.When($"kill {name}", RottingCore9, () => step() && CanReach(alive), new AvoidSpam(name, boss, main) { Where = RottingCore9 });
			rules.When($"the rest of {name}'s fight", RottingCore9, () => step() && InArena() && !dead(),
				new FightUntil(arena, () => !step() || CanReach(alive) || dead(), main) { Where = RottingCore9 });
			rules.When($"into {arena}", RottingCore9, () => step() && TileReachable(spikes), arenaDoor);
			rules.When($"through {despair}", RottingCore9, step, despairDoor);
		}

		/// <summary>
		/// Recurring Nightmare (the user's table):
		///  - "Meet Sin in Highgate": The Blood Aqueduct, to Highgate (mountiantown_connection_blood)
		///  - "Find the Basilisk Acid":
		///     - Highgate: to The Descent (by waypoint to The Vastiri Desert once it is known; with the Bottled Storm, or Queen
		///       of the Sands done, too: to The Foothills, if known)
		///     - The Descent: down the "Supply Hoist"s (descent_top_winch_v01_01, then descent_ravine_convex_winch_v01_01),
		///       and out by "The Vastiri Desert" (decent_ravinestraight_tocliff_winch_v01_01)
		///     - The Vastiri Desert: to the waypoint; open the "Storm-Weathered Chest" (DriedLakeDunes_v03_05), fight what it
		///       starts, and pick up the Storm Blade; with it, toward wagons_transition_v01_01_reverse until Queen of the
		///       Sands is given, then to Highgate (for the Bottled Storm); with the Bottled Storm, into The Oasis (the same
		///       tile); once Queen of the Sands is done, to The Foothills (desert_to_foothills_v01_01)
		///     - The Oasis: try the two rockwall_transition_v01_01 tiles for "The Sand Pit"; there, fight Shakari (AvoidSpam),
		///       follow her into "The Swallowing Sands", and kill her; at Queen of the Sands' reward, to Highgate
		///     - The Foothills: to the waypoint, and to The Boiling Lake (by it); there, toward DriedLakeDunes_v02_01, kill
		///       The Basilisk (AvoidSpam), and pick up the Basilisk Acid
		///  - "Find the Trarthan Powder":
		///     - The Boiling Lake, to Highgate; Highgate, by waypoint to The Foothills (or The Tunnel, or The Quarry, the
		///       furthest known); The Foothills, to The Tunnel (foothills_tunnel_exit_v01_01); The Tunnel, to the waypoint,
		///       and to The Quarry (tunnels_to_quarry_transition)
		///     - The Quarry: to the waypoint; into the "Shrine of the Winds" (slum_thinwall_arena_entrance), kill Garukhan
		///       (AvoidSpam), and pick up the Sekhema Feather; with it (or at The Ruler of Highgate's reward), to Highgate;
		///       once The Ruler of Highgate is done, to The Refinery (warehouse_entrance_v01_01)
		///     - The Refinery: toward refinery_thinwall_boss_door_v01_01, kill General Adus, click the "Theurgic Precipitate
		///       Machine", and pick up the Trarthan Powder
		///  - "Deliver the Trarthan Powder to Sin": to Highgate, by waypoint to The Quarry, and talk to Sin there
		///  - "Meet Sin at the Black Core": The Quarry, to The Belly of the Beast; to The Rotting Core (belly_to_rottingcore_v01_01);
		///    there, into "The Black Core" (belly_groundhole), and talk to Sin
		///  - "Defeat the Spirit of Doedre / Maligaro / Shavronne": see Spirit
		///  - "Talk to Sin"; "Kill the Depraved Trinity": into "The Black Heart" and kill it (AvoidSpam); "Talk to Lilly"
		/// TODO: the bosses' effects (AvoidSpam's hazards: Shakari, The Basilisk, Garukhan, the spirits, the Trinity) and
		/// Shakari's immune phase; confirm live: the quests' steps (Queen of the Sands', The Ruler of Highgate's, The Storm
		/// Blade's), that the Storm Blade's fight drops it, that the Bottled Storm is given in Highgate (by the town's talks)
		/// </summary>
		private void RecurringNightmareQuest(FinishAllQuests quests) {
			const string rn = RecurringNightmare;
			Func<string> main = BuildPlan.MainSkill;
			string town = TownOf(9);
			bool at(string prefix) => StepStarts(rn, prefix);
			bool has(string item) => Equipment.HasQuestItem(item);
			bool known(string area) => WaypointPanel.IsKnownUnlocked(area);
			Offsets.Vector2i? waypoint(CombatContext c) => c.Terrain.Waypoint?.GridPos;
			var rules = new FirstOf(rn);

			// "Meet Sin in Highgate"
			rules.When("to Highgate", BloodAqueduct, () => at("Meet Sin in Highgate"),
				new TakeTransition("Highgate") { Where = BloodAqueduct, SearchAt = Search.Exit("Highgate", "mountiantown_connection_blood") });

			// ---- "Find the Basilisk Acid" ----
			bool find() => at("Find the Basilisk Acid");
			const string blade = "Storm Blade", bottled = "Bottled Storm", acid = "Basilisk Acid";
			bool queenStarted() => StepOf(QueenOfTheSands) != null;
			bool queenReward() => AtReward(QueenOfTheSands) || StepStarts(QueenOfTheSands, "Talk to Irasha");
			bool queenDone() => QuestFinished(QueenOfTheSands);
			// (the Storm Blade given to Petarus and Vanja: the Bottled Storm had, or The Storm Blade done)
			bool bladeDone() => has(bottled) || QuestFinished(StormBlade);
			bool wantBlade() => find() && !has(blade) && !bladeDone() && !queenDone();

			// Highgate (with the Storm Blade, or at Queen of the Sands' reward: the town's talks)
			bool townBusy() => has(blade) || queenReward();
			rules.When("by waypoint to The Foothills", town, () => find() && queenDone() && known(Foothills9), new UseWaypoint(Foothills9) { Where = town });
			rules.When("by waypoint to The Vastiri Desert", town, () => find() && !townBusy() && (has(bottled) || queenDone() || known(VastiriDesert)),
				new UseWaypoint(VastiriDesert) { Where = town });
			rules.When("to The Descent", town, () => find() && !townBusy(), new TakeTransition(Descent9) { Where = town });

			// The Descent: the Supply Hoists
			const string topWinch = "descent_top_winch_v01_01", ravineWinch = "descent_ravine_convex_winch_v01_01",
				cliffWinch = "decent_ravinestraight_tocliff_winch_v01_01";
			rules.When("to The Vastiri Desert", Descent9, () => find() && TileReachable(cliffWinch),
				new TakeTransition(VastiriDesert) { Where = Descent9, SearchAt = Search.Tile(cliffWinch) });
			rules.When("down the second Supply Hoist", Descent9, () => find() && TileReachable(ravineWinch),
				new ClimbStairs("the second Supply Hoist", ravineWinch, cliffWinch, "Supply Hoist") { Where = Descent9 });
			rules.When("down the first Supply Hoist", Descent9, find,
				new ClimbStairs("the first Supply Hoist", topWinch, ravineWinch, "Supply Hoist") { Where = Descent9 });

			// The Vastiri Desert
			const string wagons = "wagons_transition_v01_01_reverse";
			var desertWp = WaypointKnownLatch(VastiriDesert);
			rules.When("to The Foothills", VastiriDesert, () => find() && queenDone(),
				new TakeTransition(Foothills9) { Where = VastiriDesert, SearchAt = Search.Exit(Foothills9, "desert_to_foothills_v01_01") });
			ToTown(rules, 9, "to Highgate for Queen of the Sands' reward", VastiriDesert, () => find() && queenReward());
			ToTown(rules, 9, "to Highgate with the Storm Blade", VastiriDesert, () => find() && has(blade) && queenStarted());
			rules.When("to The Oasis", VastiriDesert, () => find() && (has(bottled) || (queenStarted() && bladeDone())),
				new TakeTransition(Oasis9) { Where = VastiriDesert, SearchAt = Search.Exit(Oasis9, wagons) });
			rules.When("toward The Oasis (for Queen of the Sands)", VastiriDesert, () => find() && has(blade),
				new ApproachTiles("The Oasis", wagons) { Where = VastiriDesert, DoneWhen = queenStarted });
			rules.When("to the waypoint (to discover it)", VastiriDesert, () => find() && !desertWp(), new GoToWaypoint() { Where = VastiriDesert, DoneWhen = desertWp });
			// the Storm-Weathered Chest: it starts a fight, and the Storm Blade drops
			var chestTile = Search.Tile("DriedLakeDunes_v03_05");
			var chest = new OpenChest("the Storm-Weathered Chest", "Storm-Weathered Chest") { Where = VastiriDesert, SearchAt = chestTile,
				DoneWhen = () => ItemShown(blade) || has(blade) };
			rules.When("pick up the Storm Blade", VastiriDesert, () => wantBlade() && ItemShown(blade), new PickUp(blade) { Where = VastiriDesert });
			rules.When("fight for the Storm Blade", VastiriDesert, () => wantBlade() && chest.Opened && HostilesNear(60),
				new FightUntil("the Storm-Weathered Chest's fight", () => ItemShown(blade) || has(blade) || !HostilesNear(60), main) { Where = VastiriDesert });
			rules.When("pick up the Storm Blade", VastiriDesert, () => wantBlade() && chest.Opened, new PickUpQuestItem($"the {blade}") { Where = VastiriDesert });
			rules.When("open the Storm-Weathered Chest", VastiriDesert, wantBlade, chest);

			// The Oasis: Shakari (TODO: her effects, for AvoidSpam; her immune phase)
			ToTown(rules, 9, "to Highgate for Queen of the Sands' reward", Oasis9, () => find() && queenReward());
			bool shakariFight() => find() && queenStarted() && !queenReward() && !queenDone();
			var shakari = UniqueNamed("Shakari");
			bool shakariAlive(Entity e) => shakari(e) && IsAlive(e);
			Offsets.Vector2i? lastShakari = null;
			Offsets.Vector2i? shakariAt(CombatContext c) {
				var e = GetEntities()?.FirstOrDefault(x => x != null && shakariAlive(x));
				if ( e?.GetComponent<Positioned>() is Positioned p && IsValid(p) ) lastShakari = p.GridPos;
				return lastShakari;
			}
			var pitDoor = new TakeTransition("The Sand Pit") { Where = Oasis9, DoneWhen = () => CanReach(shakari) };
			var sandsDoor = new TakeTransition("The Swallowing Sands") { Where = Oasis9, DoneWhen = () => CanReach(shakariAlive) };
			var inPit = Latch(() => pitDoor.Taken || CanReach(shakari));
			rules.When("kill Shakari", Oasis9, () => shakariFight() && CanReach(shakariAlive),
				new AvoidSpam("Shakari, Queen of the Sands", shakari, main) { Where = Oasis9 });
			rules.When("into The Swallowing Sands", Oasis9, () => shakariFight() && TargetableLabelShown("The Swallowing Sands"), sandsDoor);
			rules.When("the rest of Shakari's fight", Oasis9, () => shakariFight() && inPit(),
				new FightUntil("The Sand Pit", () => !shakariFight() || CanReach(shakariAlive) || TargetableLabelShown("The Swallowing Sands"), main) {
					Where = Oasis9, SearchAt = shakariAt });
			rules.When("into The Sand Pit", Oasis9, () => shakariFight() && TargetableLabelShown("The Sand Pit"), pitDoor);
			// (two rockwall_transition_v01_01 tiles, only one of them by The Sand Pit: each tried in turn)
			rules.When("toward The Sand Pit", Oasis9, shakariFight,
				new FightUntil("the way to The Sand Pit", () => TargetableLabelShown("The Sand Pit"), main) {
					Where = Oasis9, Reach = 20, SearchAt = UntriedTile("rockwall_transition_v01_01", 20) });

			// The Foothills, The Boiling Lake: The Basilisk (TODO: its effects, for AvoidSpam)
			var foothillsWp = WaypointKnownLatch(Foothills9);
			rules.When("to the waypoint (to discover it)", Foothills9, () => find() && !foothillsWp(), new GoToWaypoint() { Where = Foothills9, DoneWhen = foothillsWp });
			rules.When("to The Boiling Lake", Foothills9, find, new TakeTransition(BoilingLake9) { Where = Foothills9, SearchAt = waypoint });
			var basilisk = UniqueNamed("Basilisk");
			bool basiliskAlive(Entity e) => basilisk(e) && IsAlive(e);
			bool wantAcid() => find() && !has(acid);
			var lakeTile = Search.Tile("DriedLakeDunes_v02_01");
			rules.When("pick up the Basilisk Acid", BoilingLake9, () => wantAcid() && ItemShown(acid), new PickUp(acid) { Where = BoilingLake9 });
			rules.When("kill The Basilisk", BoilingLake9, () => wantAcid() && CanReach(basiliskAlive), new AvoidSpam("The Basilisk", basilisk, main) { Where = BoilingLake9 });
			rules.When("pick up the Basilisk Acid", BoilingLake9, () => wantAcid() && SeenDead(basilisk), new PickUpQuestItem($"the {acid}") { Where = BoilingLake9 });
			rules.When("find The Basilisk", BoilingLake9, wantAcid, new Orbit("The Basilisk", basilisk, main) { Where = BoilingLake9, SearchAt = lakeTile });

			// ---- "Find the Trarthan Powder" ----
			bool powder() => at("Find the Trarthan Powder");
			const string feather = "Sekhema Feather", powderItem = "Trarthan Powder";
			bool rulerDone() => QuestFinished(RulerOfHighgate);
			// (the Sekhema Feather to give to Irasha: the town's talks)
			bool featherBusy() => has(feather) || AtReward(RulerOfHighgate);
			ToTown(rules, 9, "to Highgate with the Basilisk Acid", BoilingLake9, powder);
			// Highgate: by waypoint to the furthest known of The Quarry, The Tunnel and The Foothills
			rules.When("by waypoint to The Quarry", town, () => powder() && !featherBusy() && (rulerDone() || known(Quarry9)), new UseWaypoint(Quarry9) { Where = town });
			rules.When("by waypoint to The Tunnel", town, () => powder() && !featherBusy() && known(Tunnel9), new UseWaypoint(Tunnel9) { Where = town });
			rules.When("by waypoint to The Foothills", town, () => powder() && !featherBusy(), new UseWaypoint(Foothills9) { Where = town });
			rules.When("to The Tunnel", Foothills9, powder,
				new TakeTransition(Tunnel9) { Where = Foothills9, SearchAt = Search.Exit(Tunnel9, "foothills_tunnel_exit_v01_01") });
			var tunnelWp = WaypointKnownLatch(Tunnel9);
			rules.When("to the waypoint (to discover it)", Tunnel9, () => powder() && !tunnelWp(), new GoToWaypoint() { Where = Tunnel9, DoneWhen = tunnelWp });
			rules.When("to The Quarry", Tunnel9, powder,
				new TakeTransition(Quarry9) { Where = Tunnel9, SearchAt = Search.Exit(Quarry9, "tunnels_to_quarry_transition") });

			// The Quarry: Garukhan (TODO: her effects, for AvoidSpam), then The Refinery
			rules.When("to The Refinery", Quarry9, () => powder() && rulerDone(),
				new TakeTransition(Refinery9) { Where = Quarry9, SearchAt = Search.Exit(Refinery9, "warehouse_entrance_v01_01") });
			ToTown(rules, 9, "to Highgate with the Sekhema Feather", Quarry9, () => powder() && featherBusy());
			var quarryWp = WaypointKnownLatch(Quarry9);
			rules.When("to the waypoint (to discover it)", Quarry9, () => powder() && !quarryWp(), new GoToWaypoint() { Where = Quarry9, DoneWhen = quarryWp });
			var garukhan = UniqueNamed("Garukhan");
			bool wantFeather() => powder() && !rulerDone();
			rules.When("pick up the Sekhema Feather", Quarry9, () => wantFeather() && ItemShown(feather), new PickUp(feather) { Where = Quarry9 });
			rules.When("pick up the Sekhema Feather", Quarry9, () => wantFeather() && SeenDead(garukhan), new PickUpQuestItem($"the {feather}") { Where = Quarry9 });
			BossArena(rules, Quarry9, wantFeather, "Garukhan, Queen of the Winds", garukhan,
				new TakeTransition("Shrine of the Winds") { SearchAt = Search.Tile("slum_thinwall_arena_entrance") });

			// The Refinery: General Adus, and the Theurgic Precipitate Machine
			const string machine = "Theurgic Precipitate Machine";
			var adus = UniqueNamed("Adus");
			bool adusAlive(Entity e) => adus(e) && IsAlive(e);
			var adusDead = Latch(() => SeenDead(adus));
			var bossDoor = Search.Tile("refinery_thinwall_boss_door_v01_01");
			bool wantPowder() => powder() && !has(powderItem);
			rules.When("pick up the Trarthan Powder", Refinery9, () => wantPowder() && ItemShown(powderItem), new PickUp(powderItem) { Where = Refinery9 });
			rules.When("kill General Adus", Refinery9, () => wantPowder() && CanReach(adusAlive), new Orbit("General Adus", adus, main) { Where = Refinery9 });
			rules.When("use the Theurgic Precipitate Machine", Refinery9, () => wantPowder() && adusDead(),
				new Activate(machine) { Where = Refinery9, SearchAt = bossDoor, DoneWhen = () => ItemShown(powderItem) || has(powderItem) });
			rules.When("pick up the Trarthan Powder", Refinery9, () => wantPowder() && adusDead(), new PickUpQuestItem($"the {powderItem}") { Where = Refinery9 });
			rules.When("find General Adus", Refinery9, wantPowder, new Orbit("General Adus", adus, main) { Where = Refinery9, SearchAt = bossDoor });

			// ---- "Deliver the Trarthan Powder to Sin": in The Quarry ----
			bool deliver() => at("Deliver the Trarthan Powder");
			var sin = TalkTo.NpcNamed("Sin");
			ToTown(rules, 9, "to Highgate with the Trarthan Powder", Refinery9, deliver);
			rules.When("by waypoint to The Quarry", town, deliver, new UseWaypoint(Quarry9) { Where = town });
			rules.When("give Sin the Trarthan Powder", Quarry9, deliver, new TalkTo("Sin", sin) { Where = Quarry9, DoneWhen = () => !deliver() });

			// ---- "Meet Sin at the Black Core" ----
			bool meet() => at("Meet Sin at the Black Core");
			rules.When("to The Belly of the Beast", Quarry9, meet, new TakeTransition(BellyOfTheBeast9) { Where = Quarry9 });
			rules.When("to The Rotting Core", BellyOfTheBeast9, meet,
				new TakeTransition(RottingCore9) { Where = BellyOfTheBeast9, SearchAt = Search.Exit(RottingCore9, "belly_to_rottingcore_v01_01") });
			rules.When("talk to Sin at the Black Core", RottingCore9, () => meet() && CanReach(sin), new TalkTo("Sin", sin) { Where = RottingCore9, DoneWhen = () => !meet() });
			rules.When("into The Black Core", RottingCore9, meet,
				new TakeTransition("The Black Core") { Where = RottingCore9, SearchAt = Search.Tile("belly_groundhole"), DoneWhen = () => CanReach(sin) });

			// ---- The Rotting Core: the spirits, Sin, the Depraved Trinity, Lilly (TODO: their effects, for AvoidSpam) ----
			bool inCore() => StepOf(rn) != null;
			Spirit(rules, "Doedre", () => at("Defeat the Spirit of Doedre"), inCore, "Doedre's Despair", "Doedre's Arena");
			Spirit(rules, "Maligaro", () => at("Defeat the Spirit of Maligaro"), inCore, "Maligaro's Misery", "Maligaro's Arena");
			Spirit(rules, "Shavronne", () => at("Defeat the Spirit of Shavronne"), inCore, "Shavronne's Sorrow", "Shavronne's Arena");
			rules.When("talk to Sin", RottingCore9, () => at("Talk to Sin"), new TalkTo("Sin", sin) { Where = RottingCore9, DoneWhen = () => !at("Talk to Sin") });
			BossArena(rules, RottingCore9, () => at("Kill the Depraved Trinity"), "The Depraved Trinity", UniqueNamed("Depraved Trinity"),
				new TakeTransition("The Black Heart"));
			rules.When("talk to Lilly Roth", RottingCore9, () => at("Talk to Lilly"),
				new TalkTo("Lilly Roth", TalkTo.NpcNamed("Lilly")) { Where = RottingCore9, DoneWhen = () => !at("Talk to Lilly") });

			quests.Handlers.Add(new QuestHandler(rn, rules) {
				Active = _ => rules.Holding() != null,
				FieldNpcs = new[] { "Sin", "Lilly", "Lilly Roth" },
			});
		}
	}
}
