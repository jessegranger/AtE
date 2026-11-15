using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// Send a chat command (eg, "/passives") and read the chat's messages. Sending pastes it: the command goes on the
	/// clipboard, then Enter (opens the chat input), Ctrl + V once the input has focus, Enter. If the input does not get
	/// focus, nothing is pasted (so no letters go to the game as skill keys).
	///
	/// While it types, the game has input focus: CombatDriver lets the chain run on then (see IsTyping), as it would
	/// otherwise wait on "typing in the game" with the chat open.
	/// </summary>
	public static class ChatCommand {
		/// <summary>Give up on a command still typing after this long (eg, its chain was cancelled).</summary>
		public const long MaxTypingMs = 5000;
		private static bool typing;
		private static long typingSince;

		/// <summary>True while a command is being typed (for at most MaxTypingMs).</summary>
		public static bool IsTyping => typing && Time.ElapsedMilliseconds - typingSince < MaxTypingMs;

		/// <summary>The input chain to send command, then go on to next.</summary>
		public static IState Send(string command, IState next) {
			var done = State.NewState(() => typing = false, next);
			var paste = new KeyDown(VKey.LControlKey, new Delay(Inputs.Latency, Inputs.Press(VKey.V,
				new KeyUp(VKey.LControlKey, new Delay(Inputs.Latency, Inputs.Press(VKey.Enter, new Delay(Inputs.Latency, done)))))));
			var failed = State.NewState(() => { typing = false; Log($"Chat: the chat input did not open to send {command}"); }, next);
			return State.NewState(() => {
				ImGui.SetClipboardText(command);
				typing = true;
				typingSince = Time.ElapsedMilliseconds;
			}, Inputs.Press(VKey.Enter, State.WaitFor(1500, () => PoEMemory.GameRoot?.InGameState?.HasInputFocus ?? false, paste, failed)));
		}

		/// <summary>
		/// The chat's message list (3.29.4, seen live: ChatBoxRoot.Child(1, 2, 1), one child per message, oldest first,
		/// its text the message; a message can have several lines).
		/// </summary>
		public static Element MessageList() => GetUI()?.ChatBoxRoot?.GetChild(1, 2, 1);

		/// <summary>The last message's element (to tell the messages after it, see MessagesAfter), or IntPtr.Zero.</summary>
		public static IntPtr LastMessage() => MessageList()?.Children.LastOrDefault(e => e != null)?.Address ?? IntPtr.Zero;

		/// <summary>The texts of the messages after the one whose element is mark (see LastMessage); all of the last 20 if mark is not there.</summary>
		public static List<string> MessagesAfter(IntPtr mark) {
			var list = MessageList();
			if ( !IsValid(list) ) return new List<string>();
			var all = list.Children.Where(e => e != null).TakeLast(20).ToList();
			int at = all.FindLastIndex(e => e.Address == mark);
			return all.Skip(at + 1).Select(e => e.Text ?? "").ToList();
		}

		/// <summary>The last n messages' texts, oldest first.</summary>
		public static List<string> LastMessages(int n) {
			var list = MessageList();
			if ( !IsValid(list) ) return new List<string>();
			return list.Children.Where(e => e != null).Select(e => e.Text ?? "").TakeLast(n).ToList();
		}
	}
}
