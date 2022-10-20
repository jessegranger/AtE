using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using static AtE.Win32;

namespace AtE {
	public class Input {
		// this is a bare-bones wrapper around all things SendInput()
		// built in a hurry because I needed to not have the real InputSimulator as a dependency
		// sends one or more input events, returns the number successful
		public static uint Dispatch(params INPUT[] inputs) => SendInput((UInt32)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

		public static INPUT MouseMessage(MouseFlag button, uint mouseData = 0) {
			var msg = new INPUT { Type = InputType.Mouse };
			msg.Data.Mouse.Flags = button;
			msg.Data.Mouse.MouseData = mouseData;
			return msg;
		}
		public static INPUT KeyDownMessage(Keys key) {
			int vkey = (int)(key & Keys.KeyCode);
			var msg = new INPUT { Type = InputType.Keyboard };
			msg.Data.Keyboard.KeyCode = (UInt16)vkey;
			msg.Data.Keyboard.ScanCode = (UInt16)(MapVirtualKey((UInt32)vkey, 0) & 0xFFU);
			msg.Data.Keyboard.Flags = 0;
			msg.Data.Keyboard.Time = 0;
			msg.Data.Keyboard.ExtraInfo = IntPtr.Zero;
			return msg;
		}
		public static INPUT KeyUpMessage(Keys key) {
			int vkey = (int)(key & Keys.KeyCode);
			var msg = new INPUT { Type = InputType.Keyboard };
			msg.Data.Keyboard.KeyCode = (UInt16)vkey;
			msg.Data.Keyboard.ScanCode = (UInt16)(MapVirtualKey((UInt32)vkey, 0) & 0xFFU);
			msg.Data.Keyboard.Flags = KeyboardFlag.KeyUp;
			msg.Data.Keyboard.Time = 0;
			msg.Data.Keyboard.ExtraInfo = IntPtr.Zero;
			return msg;
		}
		private static Vector2 WindowsApiNormalize(Rectangle window, Vector2 pos) {
			// TODO: measure this for performance, not sure if we need to cache PrimaryScreen.Bounds or not
			float X = pos.X;
			float Y = pos.Y;
			if ( X > window.Width || X < 0 || Y > window.Height || Y < 0 ) {
				return Vector2.Zero;
			}
			return new Vector2(
					(window.Left + X) * 65535 / window.Width,
					(window.Top + Y) * 65535 / window.Height);
		}
		public static INPUT MouseMoveTo(Vector2 pos) {
			// x,y and in window coordinates, we need to shift them and normalize to the windows pattern
			// for this part of the windows api, mouse events happen on a 65535 x 65535 grid
			// a bit hard to sort out how it all works on a complex virtual desktop
			Rectangle bounds = Screen.PrimaryScreen.Bounds;
			pos = WindowsApiNormalize(bounds, pos);
			var msg = new INPUT { Type = InputType.Mouse };
			msg.Data.Mouse.Flags = MouseFlag.Move | MouseFlag.Absolute;
			msg.Data.Mouse.X = (int)Math.Round(pos.X);
			msg.Data.Mouse.Y = (int)Math.Round(pos.Y);
			return msg;
		}

		public static INPUT[] TranslateKeyBind(string keyBind, bool keyUp) {
			// takes as input something like "Ctrl+E", or "M5", and returns appropriate INPUT events
			// if keyUp == true, the INPUT events will be of the key up (or mouse up) variety
			// otherwise, they will be keydown (or mouse down) variety
			string[] keys = keyBind.Split('+');
			INPUT[] inputs = new INPUT[keys.Length];
			for(int i = 0; i < keys.Length; i++) {
				string k = keys[i];
				if ( k.Equals("Ctrl") ) {
					inputs[i] = keyUp ? KeyUpMessage(Keys.LControlKey) : KeyDownMessage(Keys.LControlKey);
				} else if ( k.Equals("Shift") ) {
					inputs[i] = keyUp ? KeyUpMessage(Keys.LShiftKey) : KeyDownMessage(Keys.LShiftKey);
				} else if ( k.Equals("Alt") ) {
					inputs[i] = keyUp ? KeyUpMessage(Keys.LMenu) : KeyDownMessage(Keys.LMenu);
				} else if ( k.Length == 2 && k[0] == 'M' && k[1] >= '0' && k[1] <= '9' ) {
					char button = k[1];
					if ( button == '1' ) inputs[i] = MouseMessage(keyUp ? MouseFlag.LeftUp : MouseFlag.LeftDown);
					else if ( button == '2' ) inputs[i] = MouseMessage(keyUp ? MouseFlag.RightUp : MouseFlag.RightDown);
					else if ( button == '3' ) inputs[i] = MouseMessage(keyUp ? MouseFlag.MiddleUp : MouseFlag.MiddleDown);
					else if ( button == '4' ) inputs[i] = MouseMessage(keyUp ? MouseFlag.XUp : MouseFlag.XDown, 0x0001);
					else if ( button == '5' ) inputs[i] = MouseMessage(keyUp ? MouseFlag.XUp : MouseFlag.XDown, 0x0002);
				} else if ( k.Length == 1 && k[0] >= 'A' && k[0] <= 'Z' ) {
					inputs[i] = keyUp ? KeyUpMessage((Keys)(byte)k[0]) : KeyDownMessage((Keys)(byte)k[0]);
				}
			}
			return inputs;
		}

	}
}
