using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AtE.Tests {

	/// <summary>Reading the skill sets of the example build that comes with AtE (AtE.Core/Builds), and the GemPlan made from them.</summary>
	public class GemPlanTests {

		private static PobBuild Example() => PobBuild.Load(Path.Combine(AppContext.BaseDirectory, "Builds", "Witch_FireballTest.xml"));

		[Fact]
		public void The_skill_sets_are_read_with_their_levels() {
			var build = Example();
			Assert.Equal(new[] { 1, 4, 8, 31 }, build.SkillSets.Select(s => s.Level));
			Assert.Equal(3, build.ActiveSkillSet); // activeSkillSet="4"
			var main = build.SkillSets[3].EnabledGroups.First();
			Assert.Equal("Q", main.Label);
			Assert.Equal(new[] { "Fireball", "Elemental Proliferation", "Combustion", "Burning Damage", "Unbound Ailments" }, main.EnabledGems.Select(g => g.Name));
			Assert.Equal("Fireball", main.ActiveGems.Single().Name);
			Assert.Equal("Combustion Support", main.Supports.ElementAt(1).ItemName);
			Assert.Equal("Metadata/Items/Gems/SupportGemChanceToIgnite", main.Supports.ElementAt(1).GemId);
		}

		[Fact]
		public void The_plan_follows_the_set_for_the_level() {
			var plan = new GemPlan(Example());
			Assert.Equal("Level 1", plan.SetFor(1).Title);
			Assert.Equal("Level 4", plan.SetFor(7).Title);
			Assert.Equal("Level 8", plan.SetFor(30).Title);
			Assert.Equal("Level 31", plan.SetFor(90).Title);
			Assert.Equal(new[] { "Q", "RMB", "W" }, plan.GroupsFor(5).Select(g => g.Label));
			Assert.Equal(new[] { 3, 2, 4 }, plan.GroupsFor(5).Select(g => GemPlan.SlotOf(g.Label)));
			Assert.Contains(plan.AllGems, g => g.Name == "Flame Surge");
			Assert.Equal(plan.AllGems.Count(), plan.AllGems.Select(g => g.GemId).Distinct().Count());
		}
	}
}
