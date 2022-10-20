using System;
using System.Collections.Generic;
using System.Threading;
using SharpDX.Direct3D11;
using static AtE.Globals;

namespace AtE {
	internal static class TextureCache {
		private static readonly Dictionary<string, ShaderResourceView> byName = new Dictionary<string, ShaderResourceView>();
		private static readonly Dictionary<IntPtr, ShaderResourceView> byPtr = new Dictionary<IntPtr, ShaderResourceView>();
		private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

		public static void Add(string name, ShaderResourceView texture) {
			semaphore.Wait();
			try {
				if ( byName.TryGetValue(name, out var prior) ) {
					prior.Dispose();
				}
				Log($"TextureCache: adding {name} at {texture.NativePointer:X}");
				byName[name] = texture;
				byPtr[texture.NativePointer] = texture;
			} finally {
				semaphore.Release();
			}
		}

		public static bool TryGetValue(string name, out ShaderResourceView value) {
			semaphore.Wait();
			try {
				return byName.TryGetValue(name, out value);
			} finally {
				semaphore.Release();
			}
		}
		public static bool TryGetValue(IntPtr ptr, out ShaderResourceView value) {
			semaphore.Wait();
			try {
				return byPtr.TryGetValue(ptr, out value);
			} finally {
				semaphore.Release();
			}
		}
	}

}
