using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Observers for the skills the character has (the starting skill gem, once socketed, becomes an ActorSkill), the gems themselves
	/// (the skill gem and the support gem, in the backpack or in the sockets of the weapon), and the elements of the items in the inventory.
	/// </summary>
	public static partial class OffsetObservers {

		// text, as opposed to what a wrong pointer reads as: the characters of descriptions (ASCII, the punctuation of 0x2000..0x20ff, and white space)
		private static bool Printable(string s) => s != null && s.Length > 0 && s.All(ch => (ch >= ' ' && ch < 0x7f) || (ch >= 0x2000 && ch < 0x2100) || ch == '\n' || ch == '\r' || ch == '\t' || ch == 0xa0 || ch == 0xe9);

		private static string Str(IntPtr p) => p != IntPtr.Zero && PoEMemory.TryReadString(p, Encoding.Unicode, out string s) && !string.IsNullOrEmpty(s) ? s : null;
		// a pointer to a pointer to a string
		// the pointer a pointer holds (for a string that is two pointers deep: Str2(Str2Ptr(p)))
		private static IntPtr Str2Ptr(IntPtr pp) => pp != IntPtr.Zero && PoEMemory.TryRead(pp, out IntPtr p) ? p : IntPtr.Zero;
		private static string Str2(IntPtr pp) => pp != IntPtr.Zero && PoEMemory.TryRead(pp, out IntPtr p) ? Str(p) : null;

		// ---------------------------------------------------------------- the player's skills

		private static IEnumerable<ObserverSpec> Skills() {
			yield return new ObserverSpec {
				Id = "B.Skill",
				Covers = new[] {
					"ActorSkill.UseStage", "ActorSkill.Id", "ActorSkill.ptrGemEffects", "ActorSkill.CanBeUsedWithWeapon", "ActorSkill.CanBeUsed", "ActorSkill.TotalUses",
					"ActorSkill.CooldownMS", "ActorSkill.ptrGameStats", "GemEffects.ptrSkillGem", "GemEffects.Level", "GemEffects.EffectivenessOfAddedDamage",
					"GemEffects.Cooldown", "SkillGem.NamePtr", "SkillGem.ptrActiveSkill", "ActiveSkill.Names", "SkillNames.*", "ActiveSkill.SkillTagCount", "ActiveSkill.SkillTags",
					"SkillTagEntry.ptrSkillTagDesc", "SkillTagDesc.DisplayName", "SkillTagDesc.InternalName", "ActiveSkill.LongDescription", "ActiveSkill.CastTypeCount", "ActiveSkill.CastTypes",
					"GemEffects.CostValuesCount", "GemEffects.ptrCostValues", "GemEffects.CostTypesCount", "GemEffects.ptrCostTypes", "CostTypeEntry.ptrName" },
				WaitingFor = "waiting for the player to have a skill",
				Matches = e => IsValid(GetPlayer()) && e.Address == GetPlayer().Address,
				Keys = e => e.GetComponent<Actor>()?.Skills.Select(s => s.InternalName).Where(n => n != null).Distinct().ToList() ?? new List<string>(),
				Run = (c, ents) => {
					var actor = ents[0].GetComponent<Actor>();
					var skill = actor?.Skills.FirstOrDefault(s => s.InternalName == c.Key);
					if ( skill == null ) return Outcome.NotYet("the skill is not among the player's now");
					var a = skill.Cache;
					c.That(a.Id != 0, "ActorSkill.Id is 0");
					c.InRange("UseStage", a.UseStage, 0, 20);
					c.That(a.CanBeUsedWithWeapon <= 1 && a.CanBeUsed <= 1, $"CanBeUsed {a.CanBeUsed} / CanBeUsedWithWeapon {a.CanBeUsedWithWeapon} are not 0 or 1");
					c.That(a.TotalUses >= 0 && a.TotalUses < 1000000000, $"TotalUses is {a.TotalUses}");
					c.That(a.CooldownMS >= 0 && a.CooldownMS < 3600000, $"CooldownMS is {a.CooldownMS}");
					c.That(Offsets.IsValid(a.ptrGemEffects), "ptrGameStats / ptrGemEffects is not a pointer");
					c.That(skill.GetAllStats().Count > 0, "the skill has no stats (ptrGameStats)");

					var g = skill.GemEffects.Value;
					c.That(Offsets.IsValid(g.ptrSkillGem), "GemEffects.ptrSkillGem is not a pointer");
					c.InRange("GemEffects.Level", g.Level, 1, 40);
					c.InRange("EffectivenessOfAddedDamage", g.EffectivenessOfAddedDamage, 0, 5000);
					c.InRange("GemEffects.Cooldown", g.Cooldown, 0, 600000);
					string cost = "none";
					if ( g.CostTypesCount > 0 && g.CostTypesCount < 8 && g.CostValuesCount == g.CostTypesCount ) {
						var values = new int[g.CostValuesCount];
						var types = new IntPtr[g.CostTypesCount];
						if ( PoEMemory.TryRead(g.ptrCostValues, values) != values.Length * sizeof(int) ) c.That(false, "could not read the cost values");
						if ( PoEMemory.TryRead(g.ptrCostTypes, types) != types.Length * IntPtr.Size ) c.That(false, "could not read the cost types");
						var parts = new List<string>();
						for ( int i = 0; i < types.Length; i++ ) {
							string type = PoEMemory.TryRead(types[i], out Offsets.CostTypeEntry entry) ? Str(entry.ptrName) : null;
							c.That(type != null, $"cost type {i} has no readable name");
							parts.Add($"{type}:{values[i]}");
						}
						cost = string.Join(",", parts);
					} else if ( g.CostTypesCount != 0 || g.CostValuesCount != 0 ) {
						c.That(false, $"cost counts are {g.CostValuesCount} values, {g.CostTypesCount} types");
					}

					var sg = skill.SkillGem.Value;
					c.Eq("gem name", Str(sg.NamePtr), skill.Name);
					c.That(Offsets.IsValid(sg.ptrActiveSkill), "SkillGem.ptrActiveSkill is not a pointer");

					var act = skill.ActiveSkill.Value;
					c.Eq("InternalName", Str(act.Names.InternalName), c.Key);
					c.That(Printable(Str(act.Names.DisplayName)), "Names.DisplayName did not read as text");
					c.That(Printable(Str(act.Names.Description)), "Names.Description did not read as text");
					c.That(Str(act.Names.IconName)?.StartsWith("Art/") ?? false, $"Names.IconName is {Str(act.Names.IconName)}, expected an Art/ path");
					// Default Attack has none
					if ( act.LongDescription != IntPtr.Zero ) {
						string longText = Str(act.LongDescription); // an empty one reads as null (Default Attack has an empty one)
						if ( longText != null ) c.That(Printable(longText), $"ActiveSkill.LongDescription did not read as text: {string.Join(" ", longText.Take(24).Select(ch => ((int)ch).ToString("X")))}");
					}
					else c.Note("no long description");
					c.InRange("CastTypeCount", act.CastTypeCount, 0, 20);
					if ( act.CastTypeCount > 0 ) c.That(Offsets.IsValid(act.CastTypes), "CastTypes is not a pointer");
					c.InRange("SkillTagCount", act.SkillTagCount, 0, 40);
					var tags = new List<string>();
					for ( int i = 0; i < act.SkillTagCount && i < 40; i++ ) {
						if ( !PoEMemory.TryRead(act.SkillTags + (i * Marshal.SizeOf<Offsets.SkillTagEntry>()), out Offsets.SkillTagEntry entry) || !PoEMemory.TryRead(entry.ptrSkillTagDesc, out Offsets.SkillTagDesc desc) ) { c.That(false, $"tag {i} did not read"); continue; }
						string display = Str(desc.DisplayName), internalName = Str2(desc.InternalName);
						c.That(Printable(display), $"tag {i}: DisplayName {display}");
						tags.Add(display);
					}

					c.GoldenAny($"skill.{c.Key}.cost", cost);
					c.GoldenAny($"skill.{c.Key}.tags", string.Join(",", tags));
					c.GoldenAny($"skill.{c.Key}.names", $"{Str(act.Names.DisplayName)}|{Str(act.Names.IconName)}");
					c.Note($"level {g.Level}, {tags.Count} tags");
					return c.Result();
				},
			};
		}

		// ---------------------------------------------------------------- gems

		// the gems in the backpack and equipped slots, and those socketed into the items there
		private static IEnumerable<Entity> AllGems() {
			var held = HeldItems().ToList();
			var result = held.Where(e => Has(e, "SkillGem")).ToList();
			foreach ( var item in held ) {
				var sockets = item.GetComponent<Sockets>();
				if ( !IsValid(sockets) ) continue;
				for ( int i = 0; i < Sockets.Max; i++ ) {
					var gem = sockets.Gem(i);
					if ( IsValid(gem) && Has(gem, "SkillGem") && result.All(r => r.Address != gem.Address) ) result.Add(gem);
				}
			}
			return result;
		}

		private static IEnumerable<ObserverSpec> Gems() {
			yield return new ObserverSpec {
				Id = "B.Gem",
				Covers = new[] { "Component_SkillGem.entOwner", "Component_SkillGem.Details", "Component_SkillGem.TotalExpGained", "Component_SkillGem.Level", "Component_SkillGem.ExperiencePrevLevel",
					"Component_SkillGem.ExperienceMaxLevel", "Component_SkillGem.QualityType", "SkillGemDetails.SocketColor", "SkillGemDetails.MaxLevel" },
				WaitingFor = "waiting for a gem",
				Source = AllGems,
				Matches = e => Has(e, "SkillGem"),
				Key = e => After(e.Path, "Metadata/Items/"),
				Run = (c, ents) => {
					var e = ents.First();
					var gem = e.GetComponent<SkillGem>();
					if ( !IsValid(gem) ) return Outcome.NotYet("the SkillGem component has not read yet");
					OffsetChecks.Owners(c, e, "SkillGem");
					var g = gem.Cache;
					var d = gem.Details.Value;
					c.InRange("Level", g.Level, 1, 40);
					c.That(g.ExperiencePrevLevel <= g.ExperienceMaxLevel, $"experience {g.ExperiencePrevLevel}..{g.ExperienceMaxLevel} is not a range");
					c.That(g.TotalExpGained >= g.ExperiencePrevLevel || g.Level == 1, $"TotalExpGained {g.TotalExpGained} is below the level's start {g.ExperiencePrevLevel}");
					c.That(Enum.IsDefined(typeof(Offsets.SkillGemQualityType), g.QualityType), $"QualityType is {g.QualityType}");
					c.InRange("MaxLevel", d.MaxLevel, 1, 40);
					c.InRange("SocketColor", d.SocketColor, 1, 4);
					string path = After(e.Path, "Metadata/Items/");
					c.GoldenAny($"gem.{path}.name", Equipment.ItemName(e));
					c.GoldenAny($"gem.{path}.color", d.SocketColor);
					c.GoldenAny($"gem.{path}.maxlevel", d.MaxLevel);
					c.Note($"level {g.Level}, exp {g.TotalExpGained} of {g.ExperienceMaxLevel}");
					return c.Result();
				},
			};

			// the weapon with the starting gems socketed (Fireball, Arcane Surge Support): sockets, links, the gems in them
			yield return new ObserverSpec {
				Id = "B.Sockets",
				Covers = new[] { "Component_Sockets.entOwner", "Component_Sockets.Socket0", "Component_Sockets.Socket1", "Component_Sockets.Socket2", "Component_Sockets.entGem0", "Component_Sockets.entGem1" },
				WaitingFor = "waiting for an item with gems socketed",
				Source = HeldItems,
				Matches = e => e.GetComponent<Sockets>() is Sockets s && IsValid(s) && Enumerable.Range(0, Sockets.Max).Any(i => IsValid(s.Gem(i))),
				Key = e => After(e.Path, "Metadata/Items/"),
				Run = (c, ents) => {
					var e = ents.First();
					var sockets = e.GetComponent<Sockets>();
					int filled = Enumerable.Range(0, Sockets.Max).Count(i => IsValid(sockets.Gem(i)));
					// the weapon's two starting gems are socketed one after the other
					if ( (e.Path?.Contains("Wands/Wand1") ?? false) && filled < 2 ) return Outcome.NotYet($"{filled} gem(s) socketed so far");
					OffsetChecks.Owners(c, e, "Sockets");
					int count = sockets.Count;
					c.InRange("sockets", count, 1, 6);
					for ( int i = 0; i < Sockets.Max; i++ ) {
						var gem = sockets.Gem(i);
						if ( i >= count ) { c.That(!IsValid(gem), $"socket {i} holds a gem but there are only {count} sockets"); continue; }
						c.That(Enum.IsDefined(typeof(SocketColor), sockets.Color(i)) && sockets.Color(i) != SocketColor.None, $"socket {i} color is {sockets.Color(i)}");
						if ( IsValid(gem) ) c.That(Has(gem, "SkillGem"), $"the thing in socket {i} ({gem.Path}) is not a gem");
					}
					var links = new ArrayHandle<byte>(sockets.Cache.Links).ToArray(limit: 6);
					c.That(links.Length >= 1 && links.Sum(l => l) == count, $"links {string.Join("-", links)} for {count} sockets");
					c.Note($"{count} sockets, links {string.Join("-", links)}, {filled} gems: {string.Join(", ", Enumerable.Range(0, Sockets.Max).Select(i => sockets.Gem(i)).Where(g => IsValid(g)).Select(g => Equipment.ItemName(g)))}");
					return c.Result();
				},
			};
		}

		// ---------------------------------------------------------------- inventory item elements

		private static IEnumerable<(string slot, InventoryItem item)> HeldElements() {
			var panel = GetUI()?.InventoryPanel;
			if ( !IsValid(panel) ) yield break;
			var slots = new (string, Inventory)[] { ("Backpack", panel.Backpack), ("LWeapon", panel.LWeapon), ("RWeapon", panel.RWeapon), ("Helm", panel.Helm), ("Chest", panel.Chest), ("Amulet", panel.Amulet) };
			foreach ( var (name, inventory) in slots ) {
				if ( inventory == null ) continue;
				foreach ( var item in inventory.Items ) if ( IsValid(item.Entity) ) yield return (name, item);
			}
		}

		private static IEnumerable<ObserverSpec> InventoryItems() {
			yield return new ObserverSpec {
				Id = "B.InventoryItem",
				Covers = new[] { "Element_InventoryItem.entItem", "Element_InventoryItem.Width", "Element_InventoryItem.Height", "Element_InventoryItem.spriteDetails",
					"SpriteDetails.strSpriteFile", "SpriteDetails.strSpriteFileLong", "SpriteDetails.Width", "SpriteDetails.Height" },
				WaitingFor = "waiting for an item in the inventory",
				Source = HeldItems,
				Matches = e => Has(e, "Base"),
				Key = e => After(e.Path, "Metadata/Items/"),
				Run = (c, ents) => {
					var e = ents.First();
					var found = HeldElements().FirstOrDefault(x => x.item.Entity != null && x.item.Entity.Address == e.Address);
					if ( found.item == null ) return Outcome.NotYet("its element was not found in the inventories");
					var item = found.item;
					var d = item.Details.Value;
					var b = e.GetComponent<Base>();
					c.Eq("entItem", d.entItem, e.Address);
					if ( found.slot == "Backpack" ) c.That(item.X >= 0 && item.X < 12 && item.Y >= 0 && item.Y < 5, $"the item is at {item.X},{item.Y}, outside the 12x5 backpack");
					if ( IsValid(b) ) {
						c.Eq("Width in cells", d.Width, (int)b.CellSizeX);
						c.Eq("Height in cells", d.Height, (int)b.CellSizeY);
					}
					if ( Offsets.IsValid(d.spriteDetails) && PoEMemory.TryRead(d.spriteDetails, out Offsets.SpriteDetails sprite) ) {
						c.That(Str(sprite.strSpriteFile)?.StartsWith("Art/") ?? false, $"SpriteDetails.strSpriteFile is {Str(sprite.strSpriteFile)}");
						c.That(Str(sprite.strSpriteFileLong)?.StartsWith("path:Art/") ?? false, $"SpriteDetails.strSpriteFileLong is {Str(sprite.strSpriteFileLong)}");
						c.That(sprite.Width > 0 && sprite.Height > 0, $"the sprite is {sprite.Width}x{sprite.Height}");
						c.GoldenAny($"item.{After(e.Path, "Metadata/Items/")}.sprite", Str(sprite.strSpriteFile));
					} else c.That(false, "spriteDetails is not a pointer to a SpriteDetails");
					// a new character's three Scrolls of Wisdom are always in the last slot of the backpack
					if ( found.slot == "Backpack" && (e.Path?.EndsWith("CurrencyIdentification") ?? false) ) {
						c.GoldenAny("item.CurrencyIdentification.slot", $"{found.slot} {item.X},{item.Y}");
						var stack = e.GetComponent<Stack>();
						if ( IsValid(stack) ) c.GoldenAny("item.CurrencyIdentification.stack", $"{stack.CurSize}/{stack.MaxSize}");
					}
					c.Note($"{found.slot} {item.X},{item.Y} {d.Width}x{d.Height}");
					return c.Result();
				},
			};

			// the backpack's own element: its grid size and how many items it holds
			yield return new ObserverSpec {
				Id = "B.Backpack",
				Covers = new[] { "Element_Inventory.Size" },
				WaitingFor = "waiting for the backpack to hold an item",
				Matches = e => IsValid(GetPlayer()) && e.Address == GetPlayer().Address,
				Run = (c, ents) => {
					var backpack = GetUI()?.InventoryPanel?.Backpack;
					if ( !IsValid(backpack) ) return Outcome.NotYet("no backpack element");
					var items = backpack.Items.Where(i => IsValid(i.Entity)).ToList();
					if ( items.Count == 0 ) return Outcome.NotYet("the backpack is empty");
					if ( !PoEMemory.TryRead(backpack.Address, out Offsets.Element_Inventory inv) ) return Outcome.Fail("could not read the Element_Inventory");
					c.Eq("Size", inv.Size.ToString(), "<12, 5>");
					return c.Result();
				},
			};
		}
	}
}
