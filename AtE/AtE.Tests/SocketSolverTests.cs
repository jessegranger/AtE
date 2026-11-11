using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AtE.Tests {

	/// <summary>The SocketSolver, on made-up items and the example build's groups (no game needed).</summary>
	public class SocketSolverTests {

		private static PobSkillGroup Group(string label, params string[] gems) {
			var g = new PobSkillGroup { Label = label, Enabled = true };
			foreach ( var name in gems ) {
				bool support = name.EndsWith(" Support");
				g.Gems.Add(new PobGem {
					Name = support ? name.Substring(0, name.Length - " Support".Length) : name,
					SkillId = (support ? "Support" : "") + name.Replace(" ", ""),
					Enabled = true,
				});
			}
			return g;
		}

		private static readonly PobSkillGroup Fireball = Group("Q", "Fireball", "Elemental Proliferation Support", "Combustion Support", "Unbound Ailments Support");
		private static readonly PobSkillGroup Frostblink = Group("RMB", "Frostblink");
		private static readonly PobSkillGroup FlameWall = Group("W", "Flame Wall", "Arcane Surge Support");

		// all the made-up gems are blue, except Combustion (red)
		private static SocketColor Colors(PobGem g) => g.Name == "Combustion" ? SocketColor.Red : SocketColor.Blue;

		[Fact]
		public void The_main_group_gets_the_biggest_link() {
			var items = new[] { new SocketItem("Helm", "B-B"), new SocketItem("Chest", "B-B-R-G"), new SocketItem("Boots", "G") };
			var a = SocketSolver.Solve(items, new[] { Fireball, Frostblink, FlameWall }, Colors);
			Assert.All(a.Groups[0], p => Assert.Equal("Chest", p.Item.Slot));
			Assert.Equal(4, a.Groups[0].Count);
			// Combustion (red) is in the red socket
			Assert.Equal(2, a.Groups[0].Single(p => p.Gem.Name == "Combustion").Socket);
			Assert.All(a.Groups[2], p => Assert.Equal("Helm", p.Item.Slot));
			Assert.Equal("Boots", a.Groups[1].Single().Item.Slot);
		}

		[Fact]
		public void Supports_are_dropped_from_the_end_when_the_link_is_short() {
			var a = SocketSolver.Solve(new[] { new SocketItem("Chest", "B-B-B") }, new[] { Fireball }, Colors);
			Assert.Equal(new[] { "Fireball", "Elemental Proliferation", "Combustion" }, a.Groups[0].Select(p => p.Gem.Name));
		}

		[Fact]
		public void A_lone_skill_is_not_linked_to_the_main_skill() {
			// only the main group's link group has room: Frostblink would get Fireball's supports
			var a = SocketSolver.Solve(new[] { new SocketItem("Chest", "B-B-B-B-B") }, new[] { Fireball, Frostblink }, Colors);
			Assert.Equal(4, a.Groups[0].Count);
			Assert.Empty(a.Groups[1]);
		}

		[Fact]
		public void A_white_socket_gives_no_colour_bonus() {
			var a = SocketSolver.Solve(new[] { new SocketItem("Chest", "W-W") }, new[] { Fireball }, Colors);
			Assert.Equal(2, a.Groups[0].Count);
			Assert.All(a.Groups[0], p => Assert.False(p.ColorMatches));
		}

		[Fact]
		public void Gem_colours_come_from_the_attribute_tags() {
			var index = new Dictionary<string, PobGemData.Gem>();
			PobGemData.Load(@"return {
	[""Metadata/Items/Gems/SkillGemSupportCombustion""] = {
		name = ""Combustion"", gameId = ""Metadata/Items/Gems/SupportGemChanceToIgnite"",
		tags = { fire = true, intelligence = true, support = true, }, reqStr = 40, reqDex = 0, reqInt = 60,
	},
	[""Metadata/Items/Gems/SkillGemCleave""] = { name = ""Cleave"", tags = { strength = true, }, },
	[""Metadata/Items/Gems/SkillGemPortal""] = { name = ""Portal"", tags = { }, },
}", index);
			Assert.Equal(SocketColor.Blue, index["metadata/items/gems/supportgemchancetoignite"].Color);
			Assert.Equal(SocketColor.Blue, index["combustion"].Color);
			Assert.Equal(SocketColor.Red, index["cleave"].Color);
			Assert.Equal(SocketColor.White, index["portal"].Color);
		}

		[Fact]
		public void Each_link_and_matching_colour_adds_to_the_score() {
			var groups = new List<PobSkillGroup> { Fireball };
			double none = SocketSolver.Solve(new SocketItem[0], groups, Colors).Score;
			double one = SocketSolver.Solve(new[] { new SocketItem("Chest", "G") }, groups, Colors).Score;
			double two = SocketSolver.Solve(new[] { new SocketItem("Chest", "G-G") }, groups, Colors).Score;
			double twoBlue = SocketSolver.Solve(new[] { new SocketItem("Chest", "B-B") }, groups, Colors).Score;
			Assert.Equal(0, none);
			Assert.Equal(SocketSolver.MainPoints, one, 3);
			Assert.Equal(SocketSolver.MainPoints * SocketSolver.GemFactor, two, 3);
			Assert.Equal(SocketSolver.MainPoints * SocketSolver.ColorFactor / SocketSolver.GemFactor * SocketSolver.ColorFactor, twoBlue, 3);
		}
	}
}
