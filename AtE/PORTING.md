# AtE — Platform Abstraction for a Linux-Native Build

A design sketch for cutting the Windows-specific layers behind interfaces so a Linux
backend can be dropped in. **Seams only** — interfaces and plug points are defined;
Linux implementations are left as TODO.

## Goal & strategy

Today every platform dependency is reached statically: `static class Win32`,
`static class PoEMemory`, the `D3DController`/`Overlay`/`ImGuiController` statics, and
direct `System.Windows.Forms` / `System.Drawing` use. The plan is to introduce a small
set of **platform interfaces**, route all current callers through them, keep the existing
Windows code as the first implementation, and select the implementation once at startup.

Two prerequisites sit underneath the abstraction work:

- **Move off .NET Framework 4.8 to modern .NET (8/9).** 4.8 is Windows-only. This is the
  single biggest change and gates everything else. SharpDX and WinForms references must
  go behind the platform seam so the shared core compiles on Linux.
- **Split the project.** One shared, platform-neutral assembly (`AtE.Core`) plus thin
  per-platform assemblies (`AtE.Windows`, `AtE.Linux`). The core references only the
  interfaces below, never `Win32`/SharpDX/WinForms directly.

## The seams

Five interfaces cover essentially all of `Win32.cs`, `PoEMemory.cs`, and `Overlay/`.
A sixth (`IPlatform`) is just the composition root that hands them out.

### 1. `IProcessMemory` — attach + read target memory

Wraps the parts of `PoEMemory` that touch the OS: process discovery, attach, and the
`TryRead` primitives. The entity/component model, `ArrayHandle<T>`, offset scanning, and
caching stay in the core unchanged — they only call `TryRead`.

```csharp
public interface IProcessMemory {
    bool TryAttach(out int pid);          // find + open the PoE process
    bool IsAttached { get; }
    void Detach();

    IntPtr BaseAddress { get; }           // main module base, for pattern scanning
    bool TryReadImage(out byte[] image);  // PE/exe image bytes for the GameRoot scan

    bool TryRead<T>(IntPtr addr, out T value) where T : unmanaged;
    int  TryRead<T>(IntPtr addr, T[] buffer) where T : unmanaged;   // array read
    bool TryReadString(IntPtr addr, Encoding enc, out string s, int maxLen = 256);

    bool TryQueryRegion(IntPtr addr, out MemoryRegion region);      // VirtualQueryEx equiv
}
```

- **Windows impl:** current `ProcessMemoryUtilities` calls — `OpenProcess`,
  `ReadProcessMemory`, `VirtualQueryEx`, `GetSystemInfo`.
- **Linux impl (TODO):** `process_vm_readv(2)` for the bulk reads; parse `/proc/<pid>/maps`
  for region/validity checks (replaces `VirtualQueryEx`) and `/proc/<pid>/mem` or the maps
  for the module base. Process discovery via scanning `/proc/*/comm`. Note: may require
  `CAP_SYS_PTRACE` or matching uid / `ptrace_scope` settings.

**Callers to reroute:** all of `PoEMemory.TryRead*`, `ArrayHandle<T>`, `Offsets`
validity helpers, the attach loop, and the `GameRoot` pattern scan.

### 2. `IInputSink` — synthetic input to the game

Wraps `SendInput` and friends from `Win32.cs`.

```csharp
public interface IInputSink {
    void MouseMove(Vector2 screenPos);
    void MouseButton(MouseBtn btn, bool down);
    void KeyDown(VKey key);
    void KeyUp(VKey key);
}
```

- **Windows impl:** existing `SendInput` / `INPUT` / `MapVirtualKey` wrappers.
- **Linux impl (TODO):** `uinput` virtual device, or XTEST under X11. Define an internal
  `VKey` enum in the core to replace `System.Windows.Forms.Keys` so the core stops
  depending on WinForms; each backend maps `VKey` to its own keycodes.

**Callers:** `MouseKeyboardPlugin`, `FlasksPlugin`, anything calling `Win32.SendInput*`.

### 3. `IInputSource` — hotkeys + cursor + foreground state

Wraps the polling side: `GetAsyncKeyState`, `GetCursorPos`, `GetForegroundWindow`.

```csharp
public interface IInputSource {
    bool IsKeyDown(VKey key);
    Vector2 GetCursorPos();
    bool IsTargetForeground();   // is PoE (or our overlay) the active window
}
```

- **Windows impl:** `GetAsyncKeyState`, `GetCursorPos`, `GetForegroundWindow`/`GetActiveWindow`.
- **Linux impl (TODO):** X11 `XQueryKeymap`/`XQueryPointer`, or read evdev. "Foreground"
  via the active-window hint (`_NET_ACTIVE_WINDOW`).

**Callers:** `StateMachine/HotKey.cs`, `Console`, any plugin polling keys.

### 4. `IOverlayWindow` — the transparent, click-through, topmost window

Wraps `OverlayForm` + the DWM/layered-window tricks. This is the seam with the most
behavioral risk on Linux (see notes), so keep the surface small and explicit.

```csharp
public interface IOverlayWindow {
    void Create(string title);
    void SetClickThrough(bool enabled);          // WS_EX_TRANSPARENT equivalent
    void SetTopmost(bool enabled);
    void SetTransparent(bool enabled);           // per-pixel alpha / sheet-of-glass
    Rectangle TargetClientRect { get; }          // game window bounds, screen coords
    event Action<int,int> Resized;
    IntPtr Handle { get; }                       // native handle for the renderer
    void PumpEvents();                           // replaces the WinForms message loop
}
```

- **Windows impl:** `OverlayForm`, `DwmExtendFrameIntoClientArea`, `SetWindowLong` with
  `WS_EX_LAYERED|TRANSPARENT|TOPMOST`, `GetWindowRect`/`ClientToScreen`.
- **Linux impl (TODO):** X11 override-redirect or `_NET_WM_STATE_ABOVE` window with an
  ARGB visual + compositor for transparency; click-through via the XShape input region
  (empty input shape). Wayland is harder (layer-shell protocol, compositor-dependent).
  This is the seam most likely to need iteration — recommend an explicit
  borderless-windowed assumption for the game rather than exclusive fullscreen.

**Callers:** `Overlay.Initialise`, `OverlayForm`, the resize binding.

### 5. `IRenderer` — GPU device + ImGui backend

Wraps `D3DController`, `ImGuiController`, `SpriteController`, `TextureCache`. The plugin
draw calls are already ImGui (`ImGui.*`), which is portable — only the backend that
uploads vertex buffers and textures to the GPU is Windows-bound.

```csharp
public interface IRenderer {
    void Initialise(IOverlayWindow window);
    void BeginFrame();
    void EndFrame();                          // render ImGui draw data + present
    void Resize(int w, int h);
    ITextureHandle LoadTexture(string path);  // backs TextureCache / SpriteController
}
```

- **Windows impl:** SharpDX Direct3D 11 + DXGI present; existing ImGui.NET D3D11 backend.
- **Linux impl (TODO):** Vulkan or OpenGL device (e.g. Silk.NET or Veldrid as a
  cross-platform GPU layer) with the matching ImGui backend. Switching to Veldrid/Silk.NET
  for *both* platforms is worth considering — it would collapse this seam into one
  cross-platform renderer and remove the SharpDX dependency entirely.

**Callers:** `Overlay`, `D3DController`, `ImGuiController`, `SpriteController`, `TextureCache`.

### 6. `IPlatform` — composition root

```csharp
public interface IPlatform {
    IProcessMemory Memory { get; }
    IInputSink     Input  { get; }
    IInputSource   Keys   { get; }
    IOverlayWindow Window { get; }
    IRenderer      Renderer { get; }
}
```

`Program.Main` picks one at startup (`new WindowsPlatform()` / `new LinuxPlatform()`),
stores it (e.g. on `Globals`), and the rest of the code reaches platform services only
through it. Nothing in the core constructs a concrete backend.

## What stays untouched (the portable core)

These have no platform dependency once the seams above are in place and the `Keys`/`VKey`
swap is done:

- `StateMachine/` — `Machine`, `State`, `Runner`, `HotKey` (after rerouting to `IInputSource`).
- `PoE/` model — `Entity`, `Component<T>`, `EntityCache`, `ElementCache`, `Element`,
  `ArrayHandle<T>`, `MemoryObject<T>`, and **`Offsets.cs`** (still Windows-PoE layout, but
  that's a game-version concern, not an OS one).
- `Plugins/` — all of them; they call ImGui + the model + `IInput*`, never the OS directly.
- `PluginBase` settings/INI loading.

## Things in the core that quietly depend on Windows types

Track these down during the cut — they're easy to miss because they're not P/Invoke:

- `System.Windows.Forms.Keys` (used widely for hotkeys) → replace with core `VKey` enum.
- `System.Drawing` (`Point`, `Rectangle`, `Color`, the `.ico`) → swap for `System.Numerics`
  / a small core geometry struct, or keep `System.Drawing.Common` (works on Linux but is
  itself deprecated cross-platform — prefer replacing).
- `App.config` / `packages.config` → modern SDK-style `.csproj` with PackageReferences.
- `Stopwatch`/timing is fine (portable).

## Suggested migration order

1. ✅ **Done — Retarget to .NET 8/9** and split into `AtE.Core` + `AtE.Windows`; get the existing
   Windows build green on the new framework with all current code still in `AtE.Windows`.
   - Solution `AtE.sln` now holds two projects. `AtE.Core` targets neutral `net9.0` (no
     `-windows`, no WinForms/SharpDX) and currently contains only the seam interface
     `Platform/IProcessMemory.cs` (+ `MemoryRegion`). `AtE.Windows` targets `net9.0-windows`
     (`WinExe`, WinForms, SharpDX, `AssemblyName=AssistantToTheExile`), references `AtE.Core`,
     and holds all remaining source. Verify the build on Windows (`dotnet build AtE.sln` or VS).
2. ✅ **Done — Introduce `IProcessMemory`** and move `PoEMemory`'s OS calls behind it; everything
   else keeps working. This is the lowest-risk, highest-value seam and proves the model
   reads correctly on the new framework.
   - `IProcessMemory` (+ `MemoryRegion`) lives in `AtE.Core/Platform`. `WindowsProcessMemory`
     in `AtE.Windows/Platform` holds every OS call (`OpenProcess`/`ReadProcessMemory`/
     `VirtualQueryEx` via `ProcessMemoryUtilities`, plus `FindWindow` discovery and the exe
     image read). `PoEMemory` now keeps a transitional `static IProcessMemory Memory =
     new WindowsProcessMemory()` and routes all `TryRead*`/attach/region calls through it;
     the entity/component model, `ArrayHandle<T>`, and offset scanning were left untouched.
3. ✅ **Done — Introduce `IInputSource`/`IInputSink`** and the `VKey` enum; reroute `HotKey` and
   the input-sending plugins. Purge `System.Windows.Forms.Keys` from the core.
   - `IInputSink`, `IInputSource`, the core `VKey` enum (Windows VK values, `[Flags]`, so
     INI hotkey names still round-trip) and `MouseBtn` live in `AtE.Core/Platform`.
     `WindowsInputSink` (SendInput/INPUT/MapVirtualKey) and `WindowsInputSource`
     (GetAsyncKeyState/GetCursorPos/GetForegroundWindow) live in `AtE.Windows/Platform`,
     selected via a transitional `Input.Sink`/`Input.Source` composition point. `HotKey`,
     `Console`, and the input-sending plugins now go through `Input.*`; `Win32`'s key APIs
     take `VKey`. (`WindowsInputSource` still reaches `Overlay`/`PoEMemory.Target` directly
     for the foreground check until the window seam lands.)
4. ✅ **Done — Introduce `IRenderer`** — ideally by adopting a cross-platform GPU layer
   (Veldrid/Silk.NET) for the Windows build first, so the Linux build is "just another
   backend" rather than new code.
   - `IRenderer` (+ `ITextureHandle`) lives in `AtE.Core/Platform` and stays free of SharpDX
     and WinForms (only `IntPtr`/`int`/`string`). `WindowsRenderer` in `AtE.Windows/Platform`
     wraps the existing SharpDX D3D11 + ImGui.NET backend — `D3DController`, `ImGuiController`,
     `SpriteController`, `TextureCache` are kept verbatim as the first implementation and
     simply delegated to. A transitional `Rendering.Renderer = new WindowsRenderer()` mirrors
     `Input` (named `Rendering`, not `Render`, to avoid colliding with the `Render` entity
     component). `Overlay` now drives the device through `Initialise`/`BeginFrame`/`EndFrame`/
     `Resize`, and `PoEMemory`'s focus gating toggles `ContentEnabled`/`PresentEnabled`/
     `Clear()` on the seam instead of the controller statics.
   - Transitional deviations from the sketch above (resolved by later steps): `Initialise`
     takes a native window handle + size rather than `IOverlayWindow` (step 5 not built yet —
     `WindowsRenderer` reaches `Overlay.RenderForm` directly for the WinForms input wiring,
     the same way `WindowsInputSource` reaches `Overlay`); `Resize` carries `left/top` because
     the D3D swap-chain bounds need them; and `ContentEnabled`/`PresentEnabled`/`Clear` were
     added to the surface so the focus-based render gating never names a concrete backend.
     The portable ImGui *draw* helpers (`DrawText`/`DrawSprite`/…) are above this seam and
     unchanged — only the GPU upload/present backend moved behind it. Adopting Veldrid/Silk.NET
     to collapse this seam to one cross-platform backend remains the recommended follow-up.
5. ✅ **Done — Introduce `IOverlayWindow`** last — it's the riskiest on Linux and benefits from
   everything else already working so you can iterate on transparency/click-through in
   isolation.
   - `IOverlayWindow` lives in `AtE.Core/Platform` and stays free of WinForms/SharpDX/
     `System.Drawing` (only `System` — `Action`/`IntPtr`/ints). `WindowsOverlayWindow` in
     `AtE.Windows/Platform` owns the existing `OverlayForm` and reproduces the windowing
     behaviour that was in `Overlay.Initialise`: the load-time DWM "sheet of glass"
     (`ExtendFrameIntoClientArea(-1,-1,-1,-1)`), the `WS_EX_TRANSPARENT` click-through toggle
     (`OverlayForm.IsTransparent`), and the `SharpDX.Windows.RenderLoop` message pump. A
     transitional `Windowing.Window = new WindowsOverlayWindow()` mirrors `Input`/`Rendering`.
     `Overlay` is now a Windows-side render-loop orchestrator/facade — it keeps `FrameCount`/
     `FrameLock`/`FPS` and delegates create/bounds/focus/handle/loop/close to the seam; its
     `static RenderForm` field and `SharpDX.Windows` dependency are gone. Callers rerouted:
     `WindowsRenderer` sources the concrete form from `WindowsOverlayWindow.Form`,
     `ImGuiController`'s click-through toggle and `WindowsInputSource.HasFocus` go through the
     seam, `EntityCache` uses `Overlay.IsClosed` (now folding in window liveness), and
     `MapElement` subscribes to `Windowing.Window.Resized`.
   - Transitional deviations from the sketch above (mirroring step 4's): `TargetClientRect` was
     dropped — nothing tracks the *game* window through this seam (`PoEMemory` repositions the
     overlay via `SetBounds` using a rect it reads through `IProcessMemory`), and callers only
     need the overlay's own geometry, exposed as ints to keep `System.Drawing.Rectangle` out of
     the core. `PumpEvents` became `RunMessageLoop(Action onFrame)` because the existing pump is
     blocking and invokes a per-frame callback. `HasFocus`/`IsOpen`/`Close` are surfaced on the
     seam as pure window concerns. The renderer/ImGui WinForms-input wiring still binds to the
     concrete `OverlayForm` (sourced from `WindowsOverlayWindow.Form`) — a within-assembly
     shortcut between two Windows backends; a Linux renderer would bind to `Handle` only.
6. **Add `AtE.Linux`** with stub implementations of all five interfaces, then fill them in
   in the same order (memory → input → render → window).

## Linux backend cheat-sheet (per seam, for later)

| Seam | Windows now | Linux target |
|---|---|---|
| `IProcessMemory` | OpenProcess / ReadProcessMemory / VirtualQueryEx | `process_vm_readv`, `/proc/<pid>/maps`, `/proc/<pid>/comm` |
| `IInputSink` | SendInput / MapVirtualKey | `uinput` or XTEST |
| `IInputSource` | GetAsyncKeyState / GetCursorPos / GetForegroundWindow | XQueryKeymap / XQueryPointer / `_NET_ACTIVE_WINDOW` |
| `IOverlayWindow` | OverlayForm + DWM + WS_EX_LAYERED/TRANSPARENT | X11 override-redirect ARGB window + XShape input region (Wayland: layer-shell) |
| `IRenderer` | SharpDX D3D11 + ImGui.NET | Vulkan/OpenGL via Veldrid/Silk.NET + ImGui backend |

**Biggest risks:** (1) ptrace permissions for cross-process reads; (2) transparent
click-through overlay over a fullscreen game on Wayland; (3) anti-cheat / ToS implications
of `IInputSink` automation, independent of platform.
