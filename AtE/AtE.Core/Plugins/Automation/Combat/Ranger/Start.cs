namespace AtE {

	/// <summary>A new Witch, to Lioneye's Watch (see StartBase).</summary>
	public class RangerStart : StartBase {
		public override string Name => "Ranger Start";
		protected override string StartingWeapon => "Crude Bow";
		protected override string StartingSkill => "Burning Arrow";
		protected override string StartingSupport => "Momentum Support";
	}
}
