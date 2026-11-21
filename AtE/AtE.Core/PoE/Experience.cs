using System;

namespace AtE {

	/// <summary>
	/// The total experience a character needs to reach each level (the game's experience table: level 1 needs 0, level
	/// 100 needs 4,250,334,444). Checked live (3.30.0d): a level 75 character's Player.XP, 597,210,939, is between the
	/// totals for 75 and 76.
	/// </summary>
	public static class Experience {
		private static readonly long[] TotalForLevel = {
			0, // (no level 0)
			0, 525, 1760, 3781, 7184, 12186, 19324, 29377, 43181, 61693, // 1-10
			85990, 117506, 157384, 207736, 269997, 346462, 439268, 551295, 685171, 843709, // 11-20
			1030734, 1249629, 1504995, 1800847, 2142652, 2535122, 2984677, 3496798, 4080655, 4742836, // 21-30
			5490247, 6334393, 7283446, 8348398, 9541110, 10874351, 12361842, 14018289, 15859432, 17905634, // 31-40
			20171471, 22679999, 25456123, 28517857, 31897771, 35621447, 39721017, 44225461, 49176560, 54607467, // 41-50
			60565335, 67094245, 74247659, 82075627, 90631041, 99984974, 110197515, 121340161, 133497202, 146749362, // 51-60
			161191120, 176922628, 194049893, 212684946, 232956711, 255001620, 278952403, 304972236, 333233648, 363906163, // 61-70
			397194041, 433312945, 472476370, 514937180, 560961898, 610815862, 664824416, 723298169, 786612664, 855129128, // 71-80
			929261318, 1009443795, 1096169525, 1189918242, 1291270350, 1400795257, 1519130326, 1646943474, 1784977296, 1934009687, // 81-90
			2094900291, 2268549086, 2455921256, 2658074992, 2876116901, 3111280300, 3364828162, 3638186694, 3932818530, 4250334444, // 91-100
		};

		/// <summary>The total experience needed to reach level (1 to 100); -1 for a level out of range.</summary>
		public static long Total(int level) => level >= 1 && level <= 100 ? TotalForLevel[level] : -1;

		/// <summary>How far into its level (0 to 100) a character of level with xp total experience is; 100 at level 100; NaN if they do not fit.</summary>
		public static double PercentOfLevel(int level, long xp) {
			if ( level == 100 ) return 100;
			long from = Total(level), to = Total(level + 1);
			if ( from < 0 || to <= from || xp < from || xp > to ) return double.NaN;
			return 100.0 * (xp - from) / (to - from);
		}
	}
}
