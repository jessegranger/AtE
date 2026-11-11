using System.Linq;
using Xunit;

namespace AtE.Tests {

	/// <summary>
	/// The ratchet on the Offsets tests: every offset is covered by a live check or observer, or is listed (with a reason) in
	/// AtE.Core/Plugins/Automation/OffsetTests/unchecked.txt. These read declarations, not memory, so they need no game.
	/// </summary>
	public class OffsetCoverageTests {

		[Fact]
		public void The_offsets_are_found() {
			Assert.True(OffsetCoverage.AllOffsets().Count > 500, "reflection over Offsets found too few offsets");
		}

		[Fact]
		public void The_checks_and_observers_are_well_formed() {
			// All() throws on duplicate ids and on a DependsOn that names no check
			Assert.NotEmpty(OffsetChecks.All());
			Assert.NotEmpty(OffsetObservers.All());
		}

		[Fact]
		public void Every_offset_is_covered_or_listed_as_unchecked() {
			Assert.NotNull(OffsetCoverage.UncheckedFile);
			var result = OffsetCoverage.Compute();
			Assert.True(result.Ok, "\n" + result);
		}
	}
}
