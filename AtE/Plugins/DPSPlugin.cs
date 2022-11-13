using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using static AtE.Globals;

namespace AtE.Plugins {

	public class DPSPlugin : PluginBase {

		public override string Name => "DPS Display";

		/// <summary>
		/// Show small text in the world with DPS numbers when Rare or Uniques are killed.
		/// </summary>
		public bool ShowDPSText = true;

		/// <summary>
		/// Show "Notify" text (in the bottom left) with DPS numbers when Rare or Uniques are killed.
		/// </summary>
		public bool ShowDPSTextAsNotify = true;

		public override void Render() {
			base.Render();
			ImGui.Checkbox("Show DPS on Rares and Uniques", ref ShowDPSText);
			ImGui.Checkbox("Show DPS as Notification", ref ShowDPSTextAsNotify);
		}

		private class Sighting {
			public uint MonsterId;
			public long FirstSeen;
			public uint FirstLife;
		}

		private Dictionary<uint, Sighting> trackingMonsters = new Dictionary<uint, Sighting>();

		public DPSPlugin():base() {
			OnAreaChange += (sender, areaName) => {
				// TODO: end of area report
				trackingMonsters.Clear();
			};
		}

		private void DriftUpText(Vector3 startPos, string text, Color color, float speed, long duration) {
			Run("DriftUpText", (self, dt) => {
				if ( duration <= 0 ) return null;
				DrawTextAt(startPos, text, color);
				startPos.Z -= speed * dt;
				duration -= dt;
				return self;
			});


		}

		public override IState OnTick(long dt) {
			if ( Enabled && !Paused ) {
				foreach(var ent in GetEnemies().Where(IsValid) ) {
					var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity ?? Offsets.MonsterRarity.White;
					if( rarity >= Offsets.MonsterRarity.Rare ) {

						// if alive, just tracked
						var life = ent.GetComponent<Life>();
						if ( !IsValid(life) ) continue;

						if( IsAlive(life) ) {
							if ( IsFullLife(life) ) {
								continue; // dont start tracking DPS until they take first damage
							}
							if ( !trackingMonsters.ContainsKey(ent.Id) ) {
								trackingMonsters.Add(ent.Id, new Sighting { MonsterId = ent.Id, FirstLife = MaxEHP(ent), FirstSeen = Time.ElapsedMilliseconds });
							}

						} else if( trackingMonsters.TryGetValue(ent.Id, out Sighting value) ) {
							// else, tracked and dead monsters we record
							trackingMonsters.Remove(ent.Id);
							long elapsed = Time.ElapsedMilliseconds - value.FirstSeen;
							long dps = 1000 * value.FirstLife / elapsed;
							if( ShowDPSText ) {
								DriftUpText(ent.GetComponent<Render>()?.Position ?? Vector3.Zero,
									$"{FormatNumber(dps)}", Color.Cyan, .09f, 8000);
							}
							if( ShowDPSTextAsNotify ) {
								Notify($"{ent.Path} with {FormatNumber(value.FirstLife)} hp killed in {elapsed / 1000:F1}s : {FormatNumber(dps)}dps", Color.Cyan, 8000);
							}
						}
					}
				}
			}
			return this;
		}
	}
}
