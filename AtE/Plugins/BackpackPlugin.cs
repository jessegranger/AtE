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

			ImGui_HotKeyButton("Deposit All Loot", ref DumpKey);

		}

		private uint InputLatency => (uint)GetPlugin<CoreSettings>().InputLatency;

		private IState RightClickItem(InventoryItem item, IState next = null) => IsValid(item) ? new RightClickAt(Center(item.GetClientRect()), InputLatency, next) : null;
		private IState LeftClickItem(InventoryItem item, uint count = 1, IState next = null) => IsValid(item) ? new LeftClickAt(Center(item.GetClientRect()), InputLatency, count, next) : null;

		private IState PlanUseItemFromBackpack(string usePath, IState next = null, IState fail = null) {
			var useItem = BackpackItems().Where(i => i.Entity?.Path?.Equals(usePath) ?? false).FirstOrDefault();
			if( !IsValid(useItem) ) {
				Notify($"Could not find any {usePath} to use.");
				return fail;
			}
			return RightClickItem(useItem, next);
		}

		private IState OnlyIfStashIsOpen(IState ifOpen, IState ifClosed = null) => NewState("CheckStashIsOpen", (self, dt) => BackpackIsOpen() && StashIsOpen() ? ifOpen : ifClosed, next: ifOpen);
		private IState OnlyIfBackpackIsOpen(IState ifOpen, IState ifClosed = null) => NewState("CheckStashIsOpen", (self, dt) => BackpackIsOpen() ? ifOpen : ifClosed, next: ifOpen);

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
				IState head = PlanUseItemFromBackpack(Offsets.PATH_SCROLL_WISDOM,
					new Delay(InputLatency,
					new KeyDown(Keys.LShiftKey,
					new Delay(InputLatency,
					null))));
				if( head == null ) {
					return null;
				}
				IState tail = head.Tail();
				IState keyUp = new KeyUp(Keys.LShiftKey, next);
				foreach(var item in itemsToIdentify) {
					tail.Next = OnlyIfBackpackIsOpen(
						ifOpen: new LeftClickAt(item.GetClientRect(), InputLatency, 1,
							new Delay(InputLatency,
							null)),
						ifClosed: keyUp);
					tail = tail.Tail();
				}
				tail.Next = keyUp;
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
			// make a copy to modify
			var needs = new Dictionary<string, int>(restockNeeds);
			IState head = new KeyDown(Keys.LControlKey, null);
			IState tail = head.Tail();
			foreach(var item in BackpackItems() ) {
				var ent = item.Entity;
				if( ! IsValid(ent) ) {
					continue;
				}
				if( needs.TryGetValue(ent.Path, out int needed) && needed > 0) {
					needs[ent.Path] -= ent.GetComponent<Stack>()?.CurSize ?? 1;
					continue;
				}
				tail.Next = OnlyIfStashIsOpen(
					ifOpen: LeftClickItem(item, 1, null),
					ifClosed: new KeyUp(Keys.LControlKey, null)
				);
				tail = tail.Tail();
			}
			tail.Next = new KeyUp(Keys.LControlKey, next);
			head = OnlyIfStashIsOpen(head, ifClosed: NewState(() => Notify("Not open")));
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
					Notify("Depositing all your loot.");
					Run(PlanIdentifyAll(PlanStashAll(null)));
					// TODO: incubate and open stashed decks
					return this;
				}

			}
			return this;
		}


	}
}
