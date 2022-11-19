using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AtE.Globals;

namespace AtE.Plugins {
	public class BackpackPlugin : PluginBase {

		public override string Name => "Backpack";

		public HotKey DumpKey = new HotKey(Keys.OemOpenBrackets);

		public override void Render() {
			base.Render();

			ImGui_HotKeyButton("Stash All Loot", ref DumpKey);

		}

		private uint InputLatency => (uint)GetPlugin<CoreSettings>().InputLatency;
		private uint BackpackLatency => 200;

		private IState RightClickItem(InventoryItem item, IState next = null) => IsValid(item) ? new RightClickAt(Center(item.GetClientRect()), InputLatency, next) : null;
		private IState LeftClickItem(InventoryItem item, uint count = 1, IState next = null) => IsValid(item) ? new LeftClickAt(Center(item.GetClientRect()), InputLatency, count, next) : null;

		private IState PlanUseItem(string usePath, IState next = null, IState fail = null) {
			var useItem = BackpackItems().Where(i => i.Entity?.Path?.Equals(usePath) ?? false).FirstOrDefault();
			if( !IsValid(useItem) ) {
				Notify($"Could not find any {usePath} to use.");
				return fail;
			}
			return RightClickItem(useItem, next);
		}

		private IState OnlyIfStashIsOpen(IState pass, IState fail = null) => NewState("CheckStashIsOpen", (self, dt) => BackpackIsOpen() && StashIsOpen() ? pass : fail, next: pass);

		private IEnumerable<InventoryItem> ItemsToIdentify() => BackpackItems().Where(e => !IsIdentified(e) && !IsCorrupted(e));
		private IState PlanIdentifyAll(IState next = null) {
			var ui = GetUI();
			if ( IsValid(ui) ) {
				var itemsToIdentify = ItemsToIdentify().ToArray();
				if( itemsToIdentify.Length == 0 ) {
					// no work to do
					return next;
				}
				// the real plan is:
				// right click a wisdom scroll
				// keydown shift
				// left click on each item to identify
				// keyup shift
				IState head = PlanUseItem(Offsets.PATH_SCROLL_WISDOM,
					new Delay(InputLatency,
					new KeyDown(Keys.LShiftKey,
					new Delay(InputLatency,
					null))));
				IState tail = head.Tail();
				foreach(var item in itemsToIdentify) {
					tail.Next = new LeftClickAt(item.GetClientRect(), InputLatency, 1,
						new Delay(InputLatency,
						null));
					tail = tail.Tail();
				}
				tail.Next = new KeyUp(Keys.LShiftKey, next);
				return head;
			}
			return next;
		}

		private Dictionary<string, int> restockNeeds = new Dictionary<string, int>() {
			{ Offsets.PATH_SCROLL_WISDOM, 40 },
			{ Offsets.PATH_SCROLL_PORTAL, 40 },
			{ Offsets.PATH_REMNANT_OF_CORRUPTION, 9 }
		};

		private IState PlanStashAll(IState next = null) {
			var ui = GetUI();
			if( !IsValid(ui) ) {
				return null;
			}
			var needs = new Dictionary<string, int>(restockNeeds);
			IState head = new KeyDown(Keys.LControlKey, null);
			IState tail = head.Tail();
			foreach(var item in BackpackItems() ) {
				var ent = item.Entity;
				if( needs.TryGetValue(ent.Path, out int needed) && needed > 0) {
					needs[ent.Path] -= ent.GetComponent<Stack>()?.CurSize ?? 1;
					continue;
				}
				tail.Next = OnlyIfStashIsOpen(
					pass: LeftClickItem(item, 1, null),
					fail: new KeyUp(Keys.LControlKey, null)
				);
				tail = tail.Tail();
			}
			tail.Next = new KeyUp(Keys.LControlKey, next);
			head = OnlyIfStashIsOpen(head, fail: NewState(() => Notify("Not open")));
			/*
			IState cursor = head;
			while( cursor != null ) {
				Log($"Final plan: {cursor.Name}");
				cursor = cursor.Next;
			}
			*/
			return head;
		}

		public override IState OnTick(long dt) {
			if( Enabled && !Paused && PoEMemory.IsAttached ) {

				if( PoEMemory.TargetHasFocus && DumpKey.IsReleased ) {
					Notify("Should dump everything");
					Run(PlanIdentifyAll(PlanStashAll(null)));
					// TODO: incubate and open stashed decks
					return this;
				}

			}
			return this;
		}


	}
}
