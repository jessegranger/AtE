using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Something the tests wait for rather than make happen: a monster, an item on the ground, an NPC, a chest... They come as the
	/// character plays through the beach (by hand, or with the combat routine). While testing, each frame's entities are matched against
	/// each spec; Run is given the matching ones and passes or fails the row once (NotYet: it can't tell yet, ask again). A spec with a
	/// Key makes a row for each key it sees (eg, one for each monster type).
	/// </summary>
	public class ObserverSpec {
		/// <summary>The row's id (a keyed spec adds ":" and the key).</summary>
		public string Id;
		public string[] Covers = Array.Empty<string>();
		/// <summary>What a row says while nothing it watches for has been seen.</summary>
		public string WaitingFor = "waiting for one to appear";
		/// <summary>Where to look; null for every entity.</summary>
		public Func<IEnumerable<Entity>> Source;
		public Func<Entity, bool> Matches;
		/// <summary>One row per distinct key (eg, the entity's path); null for a single row.</summary>
		public Func<Entity, string> Key;
		public Func<TestContext, List<Entity>, Outcome> Run;
	}

	public static class OffsetObservers {

		public static List<ObserverSpec> All() {
			var list = new List<ObserverSpec>();
			list.AddRange(Creatures());
			list.AddRange(Things());
			list.AddRange(Items());
			var ids = new HashSet<string>();
			foreach ( var spec in list ) if ( !ids.Add(spec.Id) ) throw new InvalidOperationException($"OffsetObservers: duplicate id {spec.Id}");
			return list;
		}

		private static string After(string path, string marker) {
			int i = path?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
			return i < 0 ? path : path.Substring(i + marker.Length);
		}

		private static bool Has(Entity e, string component) => IsValid(e) && e.HasComponent(component);

		private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

		// ---------------------------------------------------------------- monsters, NPCs, the player

		private static IEnumerable<ObserverSpec> Creatures() {
			// one row for each kind of monster seen: its life as a golden (by type, rarity and area level, as the game gives it)
			yield return new ObserverSpec {
				Id = "B.Monster",
				Covers = new[] {
					"Component_Life.MaxHP", "Component_Life.CurHP", "Component_Life.MaxMana", "Component_Life.CurMana", "Component_Life.MaxES", "Component_Life.CurES",
					"Component_Life.ReservedFlatHP", "Component_Life.ReservedPercentHP", "Component_Monster.ptrMonsterData", "Component_ObjectMagicProperties.Rarity",
					"Component_ObjectMagicProperties.Mods", "Component_Targetable.IsTargetable", "Component_Targetable.IsHighlightable", "Component_Positioned.Reaction" },
				Matches = e => (e.Path?.StartsWith("Metadata/Monsters/") ?? false) && Has(e, "Life") && Has(e, "Positioned") && Has(e, "Render"),
				Key = e => After(e.Path, "Metadata/Monsters/"),
				Run = (c, ents) => {
					var e = ents.First();
					var life = e.GetComponent<Life>();
					var pos = e.GetComponent<Positioned>();
					var render = e.GetComponent<Render>();
					if ( !IsValid(life) || !IsValid(pos) || !IsValid(render) ) return Outcome.NotYet("its components have not read yet");
					var l = life.Cache;
					var omp = e.GetComponent<ObjectMagicProperties>();
					var rarity = IsValid(omp) ? omp.Rarity : Offsets.MonsterRarity.White;
					c.That(Enum.IsDefined(typeof(Offsets.MonsterRarity), rarity) && rarity != Offsets.MonsterRarity.Error, $"Rarity is {rarity}");
					c.That(l.MaxHP > 0, $"MaxHP is {l.MaxHP}");
					c.That(l.CurHP >= 0 && l.CurHP <= l.MaxHP, $"CurHP {l.CurHP} is not within 0..MaxHP {l.MaxHP}");
					c.That(l.MaxMana >= 0 && l.CurMana >= 0 && l.CurMana <= l.MaxMana, $"mana {l.CurMana}/{l.MaxMana} is not sane");
					c.That(l.MaxES >= 0 && l.CurES >= 0 && l.CurES <= l.MaxES, $"energy shield {l.CurES}/{l.MaxES} is not sane");
					c.That(l.ReservedFlatHP == 0 && l.ReservedPercentHP == 0, $"a monster has reserved life ({l.ReservedFlatHP}, {l.ReservedPercentHP})");
					c.That(pos.IsHostile, $"it is not hostile (Reaction {pos.Reaction})");
					var terrain = GetTerrain();
					c.That(terrain != null && terrain.InBounds(pos.GridPos.X, pos.GridPos.Y), $"GridPos {pos.GridPos} is outside the terrain");
					c.That(IsFinite(render.Position) && IsFinite(render.Bounds) && render.Bounds.X > 0 && render.Bounds.Y > 0 && render.Bounds.Z >= 0, $"Render {render.Position} / {render.Bounds} is not sane");
					c.That(!string.IsNullOrEmpty(render.Name), "Render.Name did not read");
					c.That(Offsets.IsValid(e.GetComponent<Monster>()?.Cache.ptrMonsterData ?? IntPtr.Zero), "Monster.ptrMonsterData is not a pointer");
					var targetable = e.GetComponent<Targetable>();
					if ( IsValid(targetable) ) c.Note($"targetable {targetable.IsTargetable}");
					var stats = e.GetComponent<Stats>()?.GetStats();
					if ( stats != null && stats.TryGetValue(Offsets.GameStat.MaximumLife, out int maxLife) ) c.Eq("stat MaximumLife vs Life.MaxHP", maxLife, l.MaxHP);
					string type = After(e.Path, "Metadata/Monsters/");
					c.GoldenAny($"monster.{type}.{rarity}.level{c.Game.Data.Value.CurrentAreaLevel}.life", $"{l.MaxHP}/{l.MaxMana}/{l.MaxES}");
					c.GoldenAny($"monster.{type}.name", render.Name);
					return c.Result();
				},
			};

			// a monster that was alive is seen dead: its life reads 0 and its action says Dead
			yield return new ObserverSpec {
				Id = "B.MonsterDeath",
				Covers = new[] { "Component_Life.CurHP", "Component_Actor.ActionFlags" },
				WaitingFor = "waiting for a monster to be seen dying",
				Matches = e => (e.Path?.StartsWith("Metadata/Monsters/") ?? false) && Has(e, "Life"),
				Run = (c, ents) => {
					var alive = c.Scratch.TryGetValue("alive", out object stored) ? (HashSet<uint>)stored : (HashSet<uint>)(c.Scratch["alive"] = new HashSet<uint>());
					foreach ( var e in ents ) {
						var life = e.GetComponent<Life>();
						if ( !IsValid(life) ) continue;
						if ( life.CurHP > 0 ) { alive.Add(e.Id); continue; }
						if ( !alive.Contains(e.Id) ) continue;
						c.Eq("CurHP of the dead", life.CurHP, 0);
						c.That(!IsAlive(e), "IsAlive is still true");
						var actor = e.GetComponent<Actor>();
						if ( IsValid(actor) ) c.That(actor.ActionFlags.HasFlag(Offsets.Component_ActionFlags.Dead), $"ActionFlags is {actor.ActionFlags}, not Dead");
						c.Note($"{After(e.Path, "Metadata/Monsters/")} died");
						return c.Result();
					}
					return Outcome.NotYet("waiting for a monster to be seen dying");
				},
			};

			// the player's regeneration reads 0 while life and mana are full (A4.LifeRegen): so once some is missing
			yield return new ObserverSpec {
				Id = "B.PlayerRegen",
				Covers = new[] { "Component_Life.CurHPRegen", "Component_Life.ManaRegen", "Component_Life.CurHP", "Component_Life.CurMana" },
				WaitingFor = "waiting for the player to be missing some mana",
				Matches = e => IsValid(GetPlayer()) && e.Address == GetPlayer().Address,
				Run = (c, ents) => {
					var l = ents[0].GetComponent<Life>()?.Cache ?? default;
					if ( l.MaxHP <= 0 || l.CurMana >= l.MaxMana ) return Outcome.NotYet("waiting for the player to be missing some mana");
					// (a new character has no life regeneration: it reads 0 with life missing, so it can only be required to be sane)
					c.That(l.CurHPRegen >= 0 && l.CurHPRegen < 1000, $"CurHPRegen is {l.CurHPRegen}");
					if ( l.CurMana < l.MaxMana ) c.That(l.ManaRegen > 0 && l.ManaRegen < 1000, $"ManaRegen is {l.ManaRegen} with mana missing ({l.CurMana}/{l.MaxMana})");
					c.Note($"life {l.CurHP}/{l.MaxHP} regen {l.CurHPRegen}, mana {l.CurMana}/{l.MaxMana} regen {l.ManaRegen}");
					return c.Result();
				},
			};

			yield return new ObserverSpec {
				Id = "B.NPC",
				Covers = new[] { "Component_NPC.Hidden", "Component_NPC.VisibleOnMinimap", "Component_NPC.Icon", "Component_Targetable.IsTargetable", "Component_Positioned.Reaction" },
				Matches = e => (e.Path?.StartsWith("Metadata/NPC/") ?? false) && Has(e, "NPC"),
				Key = e => After(e.Path, "Metadata/NPC/"),
				Run = (c, ents) => {
					var e = ents.First();
					var npc = e.GetComponent<NPC>();
					var render = e.GetComponent<Render>();
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(npc) || !IsValid(render) || !IsValid(pos) ) return Outcome.NotYet("its components have not read yet");
					c.That(npc.Cache.Hidden <= 1 && npc.Cache.VisibleOnMinimap <= 1, $"Hidden {npc.Cache.Hidden} / VisibleOnMinimap {npc.Cache.VisibleOnMinimap} are not bytes of 0 or 1");
					c.That(!string.IsNullOrEmpty(render.Name), "Render.Name did not read");
					var targetable = e.GetComponent<Targetable>();
					c.That(IsValid(targetable), "no Targetable component");
					c.Note($"hidden {npc.Hidden}, minimap {npc.VisibleOnMiniMap}, icon {npc.HasIconOverhead}, targetable {targetable?.IsTargetable}, Reaction {pos.Reaction}");
					c.GoldenAny($"npc.{After(e.Path, "Metadata/NPC/")}.name", render.Name);
					return c.Result();
				},
			};
		}

		// ---------------------------------------------------------------- things in the world

		private static IEnumerable<ObserverSpec> Things() {
			yield return new ObserverSpec {
				Id = "B.GroundItem",
				Covers = new[] { "Component_WorldItem.entItem", "InGameState_UIElements.ItemsOnGroundLabelElement", "Element_ItemsOnGroundLabelRoot.labelsOnGroundHead",
					"ItemsOnGroundLabelEntry.nextEntry", "ItemsOnGroundLabelEntry.elemLabel", "ItemsOnGroundLabelEntry.entItem" },
				Matches = e => Has(e, "WorldItem"),
				Key = e => After(e.GetComponent<WorldItem>()?.Item?.Path, "Metadata/Items/"),
				Run = (c, ents) => {
					foreach ( var e in ents ) {
						var item = e.GetComponent<WorldItem>()?.Item;
						if ( !IsValid(item) ) continue;
						c.That(item.Path?.StartsWith("Metadata/Items/") ?? false, $"the item's path is {item.Path}");
						var labels = GetUI()?.LabelsOnGround;
						if ( !IsValid(labels) ) return Outcome.Fail("the ItemsOnGroundLabelElement is not an Element");
						var all = labels.GetAllLabels().ToList();
						var label = all.FirstOrDefault(l => l.Item != null && (l.Item.Address == item.Address || l.Item.Address == e.Address));
						if ( label == null || !IsValid(label.Label) ) {
							// an item near the player is on the screen: it has a label (else the list, or its entries, are wrong)
							var pos = e.GetComponent<Positioned>();
							var me = GetPlayer()?.GetComponent<Positioned>();
							double distance = IsValid(pos) && IsValid(me) ? Math.Sqrt(Math.Pow(pos.GridPos.X - me.GridPos.X, 2) + Math.Pow(pos.GridPos.Y - me.GridPos.Y, 2)) : 1000;
							string seen = $"{all.Count} labels listed (head {labels.Details.Value.labelsOnGroundHead}, element has {labels.Children.Count()} children; entItems {string.Join(",", all.Take(4).Select(l => l.Item?.Path?.Substring(Math.Max(0, (l.Item.Path.Length) - 24)) ?? "?"))})";
							if ( distance < 25 && c.Elapsed > 20000 ) return Outcome.Fail($"no label for {item.Path} {distance:0} cells away after {c.Elapsed / 1000} s: {seen}");
							return Outcome.NotYet($"no label for {item.Path} ({distance:0} cells away, {seen})");
						}
						var size = WindowSize.Value;
						var rect = label.Label.GetClientRect();
						c.That(rect.Width > 5 && rect.Height > 5 && rect.Right > 0 && rect.Left < size.X && rect.Bottom > 0 && rect.Top < size.Y, $"its label is at {rect}, not on the screen");
						string text = Ground.LabelText(label.Label);
						c.That(!string.IsNullOrWhiteSpace(text), "the label has no text");
						c.Note($"label \"{text}\"");
						var render = e.GetComponent<Render>();
						c.That(IsValid(render) && IsFinite(render.Position), "the WorldItem has no sane Render position");
						return c.Result();
					}
					return Outcome.NotYet("the WorldItem's item has not read yet");
				},
			};

			yield return new ObserverSpec {
				Id = "B.Chest",
				Covers = new[] { "Component_Chest.IsOpened", "Component_Chest.IsLocked", "Component_Chest.Quality", "Component_Chest.IsStrongbox", "Component_Chest.ptrToStrongboxDetails", "StrongboxDetails.IsLarge" },
				Matches = e => Has(e, "Chest"),
				Key = e => After(e.Path, "Metadata/"),
				Run = (c, ents) => {
					var e = ents.First();
					var chest = e.GetComponent<Chest>();
					if ( !IsValid(chest) ) return Outcome.NotYet("the Chest has not read yet");
					var cache = chest.Cache;
					c.That(chest.Quality <= 30, $"Quality is {chest.Quality}");
					c.That(!(chest.IsOpened && chest.IsLocked), "it is both opened and locked");
					c.Note($"opened {chest.IsOpened}, locked {chest.IsLocked}, large {chest.IsLarge}, strongbox {chest.IsStrongbox}, targetable {e.GetComponent<Targetable>()?.IsTargetable}");
					c.GoldenAny($"chest.{After(e.Path, "Metadata/")}.kind", $"strongbox {chest.IsStrongbox} large {chest.IsLarge} destroy {chest.WillDestroyAfterOpen}");
					return c.Result();
				},
			};

			// the exit's label (Render.Name) is where it leads: one of the areas the current area connects to
			yield return new ObserverSpec {
				Id = "B.AreaTransition",
				Covers = new[] { "Component_AreaTransition.entOwner", "WorldAreaDetails.ptrConnections", "WorldAreaDetails.ConnectionsCount" },
				Matches = e => Has(e, "AreaTransition") && Has(e, "Render"),
				Key = e => After(e.Path, "Metadata/"),
				Run = (c, ents) => {
					var e = ents.First();
					string destination = e.GetComponent<Render>()?.Name;
					if ( string.IsNullOrEmpty(destination) ) return Outcome.NotYet("Render.Name has not read yet");
					var here = GetCurrentArea();
					c.That(here != null && here.Connections.Any(a => a.Name == destination), $"\"{destination}\" is not among the areas {here?.Name} connects to ({string.Join(", ", here?.Connections.Select(a => a.Name) ?? new string[0])})");
					c.That(IsValid(e.GetComponent<Positioned>()), "the exit has no Positioned component");
					c.GoldenAny($"transition.{After(e.Path, "Metadata/")}.destination", destination);
					return c.Result();
				},
			};

			yield return new ObserverSpec {
				Id = "B.Door",
				Covers = new[] { "Component_TriggerableBlockage.IsClosed", "Component_TriggerableBlockage.GridMin", "Component_TriggerableBlockage.GridMax" },
				Matches = e => Has(e, "TriggerableBlockage") && Has(e, "Positioned"),
				Key = e => After(e.Path, "Metadata/"),
				Run = (c, ents) => {
					var e = ents.First();
					var door = e.GetComponent<TriggerableBlockage>();
					var pos = e.GetComponent<Positioned>();
					if ( !IsValid(door) || !IsValid(pos) ) return Outcome.NotYet("its components have not read yet");
					c.That(door.Cache.IsClosed <= 1, $"IsClosed is {door.Cache.IsClosed}");
					bool noBox = door.GridMin.X == 0 && door.GridMin.Y == 0 && door.GridMax.X == 0 && door.GridMax.Y == 0;
					if ( noBox ) {
						c.Note($"closed {door.IsClosed}, no box (not set up, or this offset moved): GridPos {pos.GridPos}");
					} else {
						c.That(door.GridMin.X <= door.GridMax.X && door.GridMin.Y <= door.GridMax.Y, $"the box {door.GridMin}..{door.GridMax} is empty");
						c.That(pos.GridPos.X >= door.GridMin.X - 1 && pos.GridPos.X <= door.GridMax.X + 1 && pos.GridPos.Y >= door.GridMin.Y - 1 && pos.GridPos.Y <= door.GridMax.Y + 1,
							$"GridPos {pos.GridPos} is not in the box {door.GridMin}..{door.GridMax}");
						c.Note($"closed {door.IsClosed}");
					}
					return c.Result();
				},
			};
		}

		// ---------------------------------------------------------------- items the character holds

		// the items in the backpack and the equipped slots (the inventory panel does not need to be open)
		private static IEnumerable<Entity> HeldItems() {
			var panel = GetUI()?.InventoryPanel;
			if ( !IsValid(panel) ) return Enumerable.Empty<Entity>();
			var inventories = new[] { panel.Backpack, panel.LWeapon, panel.RWeapon, panel.Helm, panel.Chest, panel.Amulet };
			return inventories.Where(i => i != null).SelectMany(i => i.Items).Select(i => i.Entity).Where(e => IsValid(e)).ToList();
		}

		private static IEnumerable<ObserverSpec> Items() {
			yield return new ObserverSpec {
				Id = "B.Item",
				Covers = new[] { "Component_Base.ptrToBaseInfo", "Component_Base_Info.ItemCellSizeX", "Component_Base_Info.ItemCellSizeY", "Component_Base_Info.Name", "Component_Mods.ItemRarity",
					"Component_Mods.ItemLevel", "Component_Mods.RequiredLevel", "Component_Weapon.Details", "WeaponDetails.DamageMin", "WeaponDetails.DamageMax", "WeaponDetails.AttackTime",
					"WeaponDetails.CritChance", "Component_Sockets.Links", "Component_Stack.CurSize", "Component_Stack_Details.MaxSize", "Component_Charges.NumCharges" },
				Source = HeldItems,
				Matches = e => Has(e, "Base"),
				Key = e => After(e.Path, "Metadata/Items/"),
				Run = (c, ents) => {
					var e = ents.First();
					var b = e.GetComponent<Base>();
					if ( !IsValid(b) ) return Outcome.NotYet("the Base component has not read yet");
					c.That(b.CellSizeX >= 1 && b.CellSizeX <= 2 && b.CellSizeY >= 1 && b.CellSizeY <= 4, $"the item is {b.CellSizeX}x{b.CellSizeY} cells");
					string name = Equipment.ItemName(e);
					c.That(name != null, "the item's name did not read");
					c.GoldenAny($"item.{After(e.Path, "Metadata/Items/")}.name", name);
					c.GoldenAny($"item.{After(e.Path, "Metadata/Items/")}.cells", $"{b.CellSizeX}x{b.CellSizeY}");
					var mods = e.GetComponent<Mods>();
					if ( IsValid(mods) ) {
						c.That(Enum.IsDefined(typeof(Offsets.ItemRarity), mods.Rarity), $"ItemRarity is {mods.Rarity}");
						c.That(mods.Level <= 100 && mods.ReqLevel <= 100, $"ItemLevel {mods.Level} / RequiredLevel {mods.ReqLevel} are not levels");
						c.Note($"{mods.Rarity} ilvl {mods.Level}");
					}
					var weapon = e.GetComponent<Weapon>();
					if ( IsValid(weapon) ) {
						c.That(weapon.DamageMin > 0 && weapon.DamageMax >= weapon.DamageMin && weapon.DamageMax < 100000, $"damage {weapon.DamageMin}-{weapon.DamageMax}");
						c.That(weapon.AttackTime > 100 && weapon.AttackTime < 5000, $"AttackTime {weapon.AttackTime}");
						c.That(weapon.CritChance > 0 && weapon.CritChance < 2000, $"CritChance {weapon.CritChance}");
						c.GoldenAny($"item.{After(e.Path, "Metadata/Items/")}.weapon", $"{weapon.DamageMin}-{weapon.DamageMax} {weapon.AttackTime}ms crit {weapon.CritChance}");
					}
					var sockets = e.GetComponent<Sockets>();
					if ( IsValid(sockets) ) {
						var links = new ArrayHandle<byte>(sockets.Cache.Links).ToArray(limit: 6);
						c.That(links.Length >= 1 && links.Sum(l => l) <= 6, $"socket links {string.Join("-", links)}");
						c.GoldenAny($"item.{After(e.Path, "Metadata/Items/")}.links", string.Join("-", links));
					}
					var stack = e.GetComponent<Stack>();
					if ( IsValid(stack) ) c.That(stack.CurSize >= 1 && stack.CurSize <= stack.MaxSize, $"stack {stack.CurSize}/{stack.MaxSize}");
					return c.Result();
				},
			};

			// the mods of an item: the entries of Mods.dat the item's mod pointers lead to (read from the game's memory, not by FileRoot)
			yield return new ObserverSpec {
				Id = "B.ItemMods",
				Covers = new[] { "Component_Mods.ImplicitModsArray", "Component_Mods.ExplicitModsArray", "ItemModEntry.*", "File_ModsDat_Entry.*" },
				WaitingFor = "waiting for an item with mods",
				Source = HeldItems,
				Matches = e => e.GetComponent<Mods>() is Mods m && IsValid(m) && (m.ExplicitMods.Any() || m.ImplicitMods.Any()),
				Run = (c, ents) => {
					var e = ents.First();
					var mods = e.GetComponent<Mods>();
					var all = mods.ImplicitMods.Concat(mods.ExplicitMods).ToList();
					foreach ( var mod in all ) {
						c.That(!string.IsNullOrEmpty(mod.GroupName), "a mod has no readable group name");
						c.That(Enum.IsDefined(typeof(Offsets.AffixType), mod.AffixType), $"AffixType is {mod.AffixType}");
						var values = mod.Values(4).ToList();
						c.That(values.All(v => v > -100000 && v < 100000), $"a mod's values are not sane ({string.Join(",", values)})");
					}
					c.Note($"{After(e.Path, "Metadata/Items/")}: {string.Join(", ", all.Select(m => m.GroupName))}");
					return c.Result();
				},
			};
		}
	}
}
