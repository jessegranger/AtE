using ImGuiNET;
using System.Collections.Generic;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A new character, from waking up on the beach in The Twilight Strand to Lioneye's Watch: skip the tutorials, pick up
	/// the starting weapon, kill the zombie that comes for the Dying Exile (a Metadata/Monsters/Zombies/ZombieBite
	/// from a spawner, once the Exile is clicked) with Default Attack, pick up and socket the starting skill gem, open the
	/// Large Chest on the way to the exit for the starting support gem, socket it linked to the skill, and kill Hillock
	/// (in front of the exit) with the skill, circling him (closer with a melee skill, see Orbit).
	/// Each class says which weapon and gems it starts with.
	/// </summary>
	public abstract class StartBase : CombatRoutine {

		/// <summary>The weapon on the beach (eg, "Driftwood Wand"); any weapon will do.</summary>
		protected abstract string StartingWeapon { get; }
		/// <summary>The skill gem the zombie drops (eg, "Fireball").</summary>
		protected abstract string StartingSkill { get; }
		/// <summary>The support gem in the Large Chest (eg, "Arcane Surge Support").</summary>
		protected abstract string StartingSupport { get; }
		/// <summary>The reward to take for each quest, in Lioneye's Watch: quest name => reward (eg, "Enemy at the Gate" => "Splitting Steel").</summary>
		protected virtual Dictionary<string, string> QuestRewards => new Dictionary<string, string>();

		protected StartBase() {
			Area("The Twilight Strand",
				new ClickButton("Skip All Tutorials", () => GetUI()?.SkipAllTutorials),
				new PickUp(StartingWeapon) { DoneWhen = Equipment.HasWeapon }, // or any weapon
				new BindSkill("Default Attack", 3), // Q
				new PickUp("Dying Exile", "Click"),
				new Kill("the Dying Exile's zombie", "Monsters/Zombies/ZombieBite", "Default Attack", range: 25),
				new PickUp(StartingSkill) { DoneWhen = () => Equipment.GemInBackpack(StartingSkill) },
				new SocketGem(StartingSkill),
				new BindSkill(StartingSkill, 3), // Q, in place of Default Attack
				// on the way to Hillock, who stands in the only way out: walk past the monsters
				new PickUp("Large Chest", "Open") { SearchAt = Search.TheOnlyExit },
				new PickUp(StartingSupport) { SearchAt = Search.TheOnlyExit, DoneWhen = () => Equipment.GemInBackpack(StartingSupport) },
				new SocketGem(StartingSupport, linkedTo: StartingSkill),
				new Orbit("Hillock", "Hillock", StartingSkill) { SearchAt = Search.TheOnlyExit },
				new GoToArea("Lioneye's Watch"));
			var rewards = QuestRewards;
			Area("Lioneye's Watch", rewards.Count > 0
				? new Objective[] { new TakeQuestRewards(rewards), new GoToArea("The Coast") }
				: new Objective[] { new GoToArea("The Coast") });
			AfterTheStart();
		}

		/// <summary>
		/// The objectives every class shares, from The Coast on (the classes only differ in the first two areas).
		/// </summary>
		private void AfterTheStart() {
			// the waypoint is near the exits, whose names are known once there: walk past the monsters, then out
			Area("The Coast",
				new GoToWaypoint() { IgnoreMonsters = true },
				new GoToArea("The Mud Flats") { IgnoreMonsters = true });
			Area("The Mud Flats",
				// each nest drops a glyph: Haliotis (Glyph1), Ammonite (Glyph2), Roseus (Glyph3)
				new OpenQuestChests("Rhoa Nest", "rhoanest", "QuestChests/Rhoa", 3) { FightRadius = 100, ItemPathPart = "QuestItems/Glyphs/" },
				// with the three glyphs, the wall opens the exit beside it
				new UseQuestObject("Strange Glyph Wall", "WaterCave/GlyphWall", "watercave", "WaterCave/WaterCaveAreaTransition"),
				new GoToArea("The Submerged Passage"));
		}

		public override void Render() {
			base.Render();
			var skill = SkillBar.FindSlot(StartingSkill)?.Skill;
			if ( skill != null ) ImGui.Text($"{StartingSkill}: {(SkillBar.IsMelee(skill) ? "melee" : "not melee")} (tags: {string.Join(", ", SkillBar.SkillTags(skill))})");
		}

		/// <summary>The slot the main skill goes on (Q): the starting skill, or whatever replaced it (eg, Galvanic Arrow).</summary>
		public const int MainSlot = 3;

		/// <summary>The skill to fight with: what is on MainSlot (unless it is Default Attack or nothing), else the starting skill if it is on the bar, else Default Attack.</summary>
		public string MainSkill() {
			var main = SkillBar.GetSlot(MainSlot)?.Skill;
			if ( main != null && !SkillBar.IsMovement(main) ) {
				string name = SkillBar.SkillName(main);
				if ( name != "Default Attack" ) return name;
			}
			return SkillBar.FindSlot(StartingSkill) != null ? StartingSkill : "Default Attack";
		}

		// for fights where the objective names no skill
		protected override void Attack(Hostile target, Vector2 screenPos) => UseSkillPaced(MainSkill(), screenPos);
	}
}
