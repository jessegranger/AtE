using System;
using System.Collections.Generic;
using System.Linq;

namespace AtE {

	/// <summary>The sockets of one item: their colours, and how they are linked.</summary>
	public sealed class SocketItem {
		/// <summary>Where it is (eg, "Chest"), or a name for an item being considered.</summary>
		public string Slot;
		public SocketColor[] Colors = Array.Empty<SocketColor>();
		/// <summary>The sizes of the groups of linked sockets, in socket order (eg, [2, 1]: sockets 0 and 1 are linked, 2 is alone).</summary>
		public int[] LinkGroups = Array.Empty<int>();

		public SocketItem() { }
		/// <summary>An item from a colour string (R, G, B, W) with "-" for a link, as the game shows them: eg "R-G-B B" or "RGB" for a 3-link.</summary>
		public SocketItem(string slot, string sockets) {
			Slot = slot;
			var colors = new List<SocketColor>();
			var groups = new List<int>();
			foreach ( var part in sockets.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) ) {
				int n = 0;
				foreach ( char c in part ) {
					var color = char.ToUpperInvariant(c) switch { 'R' => SocketColor.Red, 'G' => SocketColor.Green, 'B' => SocketColor.Blue, 'W' => SocketColor.White, _ => SocketColor.None };
					if ( color == SocketColor.None ) continue;
					colors.Add(color);
					n++;
				}
				if ( n > 0 ) groups.Add(n);
			}
			Colors = colors.ToArray();
			LinkGroups = groups.ToArray();
		}

		/// <summary>An item's sockets, from its Sockets component (null if it has none).</summary>
		public static SocketItem From(string slot, Entity item) {
			var sockets = item?.GetComponent<Sockets>();
			if ( !Globals.IsValid(sockets) || sockets.Count == 0 ) return null;
			return new SocketItem {
				Slot = slot,
				Colors = Enumerable.Range(0, sockets.Count).Select(sockets.Color).ToArray(),
				LinkGroups = sockets.LinkGroups.Select(b => (int)b).ToArray(),
			};
		}

		/// <summary>The worn items' sockets.</summary>
		public static List<SocketItem> Worn() => Equipment.WornSlots()
			.SelectMany(s => s.inventory.Items.Where(i => i?.Entity != null).Select(i => From(s.name, i.Entity)))
			.Where(i => i != null).ToList();

		/// <summary>The sockets of each link group (indexes into Colors).</summary>
		public IEnumerable<int[]> Links() {
			int start = 0;
			foreach ( int size in LinkGroups ) {
				int n = Math.Min(size, Colors.Length - start);
				if ( n <= 0 ) yield break;
				yield return Enumerable.Range(start, n).ToArray();
				start += n;
			}
		}

		public override string ToString() => $"{Slot}: {string.Join(" ", Links().Select(l => string.Concat(l.Select(i => Colors[i].ToString()[0]))))}";
	}

	/// <summary>Where one gem goes.</summary>
	public sealed class GemPlacement {
		public PobGem Gem;
		/// <summary>Which socket group of the plan it belongs to (0 is the main group).</summary>
		public int Group;
		public SocketItem Item;
		public int Socket;
		/// <summary>Does the socket's colour match the gem's (a quality bonus)?</summary>
		public bool ColorMatches;
		public override string ToString() => $"{Gem.Name} in {Item.Slot} socket {Socket}{(ColorMatches ? " (colour matches)" : "")}";
	}

	/// <summary>The SocketSolver's answer: where each gem goes, and how good that is.</summary>
	public sealed class SocketArrangement {
		public readonly List<GemPlacement> Placements = new List<GemPlacement>();
		/// <summary>For each group of the plan, its gems that found a socket (in priority order); empty if its skill did not.</summary>
		public readonly List<List<GemPlacement>> Groups = new List<List<GemPlacement>>();
		/// <summary>Points, as in item scores: see SocketSolver.</summary>
		public double Score;
		/// <summary>Each group's points (the main group's is scaled by damage in item scores).</summary>
		public readonly List<double> GroupScores = new List<double>();

		/// <summary>The gem for a socket of an item, or null.</summary>
		public PobGem GemAt(SocketItem item, int socket) => Placements.FirstOrDefault(p => p.Item == item && p.Socket == socket)?.Gem;
	}

	/// <summary>
	/// Arrange the plan's socket groups (main group first, then the utility ones, in priority order; see GemPlan) in the
	/// sockets of a set of items, as well as they fit.
	///
	/// Any gem fits any socket; a socket of the gem's colour gives it a quality bonus. Each group gets a link group of its
	/// own, in priority order: the one that holds the most of its gems (its skills first, then its supports in PoB's order),
	/// then with the most matching colours, then the smallest (leaving bigger ones for the groups after it). A group
	/// without supports that finds no link group of its own goes in any free socket, but not one linked to the main skill.
	///
	/// Score: each group's skill is worth Base (MainPoints for the main group, UtilityPoints for others), times GemFactor
	/// for each support linked to it (ColorFactor instead when the colour matches; the skill's own colour too); 0 if
	/// the skill has no socket. So, with the defaults, a support linked to the main skill adds about 30% of its value.
	/// </summary>
	public static class SocketSolver {
		public static double MainPoints = 100;
		public static double UtilityPoints = 10;
		public static double GemFactor = 1.30;
		public static double ColorFactor = 1.35;

		/// <param name="colorOf">A gem's colour (None when not known: then no socket matches it).</param>
		public static SocketArrangement Solve(IEnumerable<SocketItem> items, IList<PobSkillGroup> groups, Func<PobGem, SocketColor> colorOf) {
			colorOf ??= (_ => SocketColor.None);
			var links = items.Where(i => i != null).SelectMany(item => item.Links().Select(sockets => (item, sockets))).ToList();
			var used = new HashSet<int>(); // indexes into links
			var free = new List<(SocketItem item, int socket, int link)>();
			var result = new SocketArrangement();
			var waiting = new List<int>(); // groups without supports, still to place
			for ( int g = 0; g < groups.Count; g++ ) {
				var gems = Ordered(groups[g]);
				result.Groups.Add(new List<GemPlacement>());
				if ( gems.Count == 0 ) continue;
				int best = -1;
				(int fit, int matches, int size) bestKey = (0, 0, 0);
				for ( int l = 0; l < links.Count; l++ ) {
					if ( used.Contains(l) ) continue;
					var (item, sockets) = links[l];
					int fit = Math.Min(gems.Count, sockets.Length);
					int matches = Assign(gems.Take(fit).ToList(), item, sockets, colorOf).Count(p => p.ColorMatches);
					(int fit, int matches, int size) key = (fit, matches, sockets.Length);
					if ( best < 0 || key.fit > bestKey.fit || (key.fit == bestKey.fit && (key.matches > bestKey.matches || (key.matches == bestKey.matches && key.size < bestKey.size))) ) {
						best = l; bestKey = key;
					}
				}
				// a skill alone takes a link group only if nothing else wants one (it can go in a free socket instead)
				if ( gems.Count == 1 && g > 0 ) { waiting.Add(g); continue; }
				if ( best < 0 ) continue;
				used.Add(best);
				var (bItem, bSockets) = links[best];
				var placed = Assign(gems.Take(bestKey.fit).ToList(), bItem, bSockets, colorOf);
				foreach ( var p in placed ) p.Group = g;
				result.Groups[g].AddRange(placed);
				result.Placements.AddRange(placed);
			}
			// the skills alone: an unused link group's socket first (the smallest group, matching colour), then a free socket
			// in a link group of a utility group; never linked to the main skill (its supports would change it)
			int mainLink = -1;
			if ( result.Groups.Count > 0 && result.Groups[0].Count > 0 ) {
				var first = result.Groups[0][0];
				mainLink = links.FindIndex(l => l.item == first.Item && l.sockets.Contains(first.Socket));
			}
			foreach ( int g in waiting ) {
				var gem = Ordered(groups[g])[0];
				var color = colorOf(gem);
				var options = new List<(SocketItem item, int socket, int rank)>();
				for ( int l = 0; l < links.Count; l++ ) {
					if ( l == mainLink ) continue;
					var (item, sockets) = links[l];
					foreach ( int s in sockets ) {
						if ( result.Placements.Any(p => p.Item == item && p.Socket == s) ) continue;
						bool match = Matches(color, item.Colors[s]);
						// unused groups before used ones, smaller before bigger, matching colour first
						int rank = (used.Contains(l) ? 100 : 0) + sockets.Length * 2 + (match ? 0 : 1);
						options.Add((item, s, rank));
					}
				}
				if ( options.Count == 0 ) continue;
				var pick = options.OrderBy(o => o.rank).First();
				var link = links.FindIndex(l => l.item == pick.item && l.sockets.Contains(pick.socket));
				used.Add(link);
				var placement = new GemPlacement { Gem = gem, Group = g, Item = pick.item, Socket = pick.socket, ColorMatches = Matches(color, pick.item.Colors[pick.socket]) };
				result.Groups[g].Add(placement);
				result.Placements.Add(placement);
			}
			result.Score = Score(result, groups);
			return result;
		}

		/// <summary>The points of an arrangement (see SocketSolver).</summary>
		public static double Score(SocketArrangement a, IList<PobSkillGroup> groups) {
			double total = 0;
			a.GroupScores.Clear();
			for ( int g = 0; g < a.Groups.Count; g++ ) {
				var placed = a.Groups[g];
				a.GroupScores.Add(0);
				if ( placed.Count == 0 || placed[0].Gem.IsSupport ) continue;
				double value = g == 0 ? MainPoints : UtilityPoints;
				// the skill's own factor is relative: a matching colour adds ColorFactor / GemFactor
				if ( placed[0].ColorMatches ) value *= ColorFactor / GemFactor;
				foreach ( var p in placed.Skip(1) ) value *= p.ColorMatches ? ColorFactor : GemFactor;
				a.GroupScores[g] = value;
				total += value;
			}
			return total;
		}

		// a group's gems in placing order: its skills first, then its supports, each in PoB's order
		private static List<PobGem> Ordered(PobSkillGroup group) => group.ActiveGems.Concat(group.Supports).ToList();

		// a White socket holds any gem, but gives no colour bonus
		private static bool Matches(SocketColor gem, SocketColor socket) => gem != SocketColor.None && socket == gem;

		// put gems (no more than the sockets) in the sockets of one link group, with as many matching colours as can be (a matching of gems to sockets)
		private static List<GemPlacement> Assign(List<PobGem> gems, SocketItem item, int[] sockets, Func<PobGem, SocketColor> colorOf) {
			var colors = gems.Select(colorOf).ToArray();
			var socketOf = new int[gems.Count]; // gem -> index into sockets, or -1
			var gemIn = new int[sockets.Length]; // index into sockets -> gem, or -1
			Array.Fill(socketOf, -1);
			Array.Fill(gemIn, -1);
			bool augment(int gem, bool[] seen) {
				for ( int s = 0; s < sockets.Length; s++ ) {
					if ( seen[s] || !Matches(colors[gem], item.Colors[sockets[s]]) ) continue;
					seen[s] = true;
					if ( gemIn[s] < 0 || augment(gemIn[s], seen) ) {
						gemIn[s] = gem; socketOf[gem] = s;
						return true;
					}
				}
				return false;
			}
			for ( int g = 0; g < gems.Count; g++ ) augment(g, new bool[sockets.Length]);
			// the rest go in the sockets left, in order
			for ( int g = 0; g < gems.Count; g++ ) {
				if ( socketOf[g] >= 0 ) continue;
				int s = Array.IndexOf(gemIn, -1);
				if ( s < 0 ) break;
				gemIn[s] = g; socketOf[g] = s;
			}
			return Enumerable.Range(0, gems.Count).Where(g => socketOf[g] >= 0).Select(g => new GemPlacement {
				Gem = gems[g], Item = item, Socket = sockets[socketOf[g]],
				ColorMatches = Matches(colors[g], item.Colors[sockets[socketOf[g]]]),
			}).ToList();
		}
	}
}
