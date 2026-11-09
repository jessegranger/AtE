using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A new Witch, from waking up on the beach in The Twilight Strand to Lioneye's Watch: skip the tutorials, pick up
	/// the Driftwood Wand, kill the zombie that comes for the Dying Exile (a Metadata/Monsters/Zombies/ZombieBite
	/// from a spawner, once the Exile is clicked) with Default Attack, pick up and socket Fireball, open the
	/// Large Chest on the way to the exit for Arcane Surge Support, kill Hillock (in front of the exit) with Fireball.
	/// </summary>
	public class WitchStart : CombatRoutine {
		public override string Name => "Combat: Witch Start";

		public WitchStart() {
			Area("The Twilight Strand",
				new ClickButton("Skip All Tutorials", () => GetUI()?.SkipAllTutorials),
				new PickUp("Driftwood Wand") { DoneWhen = Equipment.HasWeapon }, // or any weapon
				new BindSkill("Default Attack", 3), // Q
				new PickUp("Dying Exile", "Click"),
				new Kill("the Dying Exile's zombie", "Monsters/Zombies/ZombieBite", "Default Attack", range: 25),
				new PickUp("Fireball") { DoneWhen = () => Equipment.InBackpack("/Gems/", "Fireball") },
				new SocketGem("Fireball"),
				new BindSkill("Fireball", 3), // Q, in place of Default Attack
				// on the way to Hillock, who stands in the only way out: walk past the monsters
				new PickUp("Large Chest", "Open") { SearchAt = Search.TheOnlyExit },
				new PickUp("Arcane Surge Support") { SearchAt = Search.TheOnlyExit, DoneWhen = () => Equipment.InBackpack("/Gems/", "ArcaneSurge") },
				new SocketGem("Arcane Surge Support", linkedTo: "Fireball"),
				new Orbit("Hillock", "Hillock", "Fireball") { SearchAt = Search.TheOnlyExit },
				new GoToArea("Lioneye's Watch"));
		}

		// for fights outside of the objectives: Fireball once it is on the bar, else Default Attack
		protected override void Attack(Hostile target, Vector2 screenPos) =>
			UseSkillPaced(SkillBar.FindSlot("Fireball") != null ? "Fireball" : "Default Attack", screenPos);
	}
}
