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
6. ✅ **Done (stubs) — Add `AtE.Linux`** with stub implementations of all five interfaces, then
   fill them in in the same order (memory → input → render → window).
   - New `AtE.Linux` project (`net9.0`, neutral — like `AtE.Core`, no `-windows`/WinForms/SharpDX),
     a `Library` referencing only `AtE.Core`, added to `AtE.sln`. It holds `LinuxProcessMemory`,
     `LinuxInputSink`, `LinuxInputSource`, `LinuxRenderer` (+ `LinuxTextureHandle`) and
     `LinuxOverlayWindow` under `Platform/`, mirroring `AtE.Windows/Platform`. Every interface
     member is present; the bodies throw `NotImplementedException` (cleanup paths
     `Detach`/`Close` and the `IsAttached`/`IsOpen` liveness flags return safe defaults), each
     carrying a `// TODO` pointing at the Linux target from the cheat-sheet below
     (`process_vm_readv` + `/proc/<pid>/maps`; `uinput`/XTEST; `XQueryKeymap`/`XQueryPointer`/
     `_NET_ACTIVE_WINDOW`; Vulkan/OpenGL via Veldrid/Silk.NET; X11 override-redirect ARGB +
     XShape input region).
   - **`IPlatform` composition root + entry points scaffolded.** `IPlatform` (Memory/Input/Keys/
     Window/Renderer, seam #6) and a `Platform` holder (`Platform.Current`, set once via
     `Platform.Initialise`) live in `AtE.Core/Platform`. `WindowsPlatform` (in `AtE.Windows`)
     constructs and owns one instance of each Windows backend; `Program.Main` calls
     `Platform.Initialise(new WindowsPlatform())` at the top. `LinuxPlatform` (in `AtE.Linux`)
     constructs and owns the five Linux stubs directly. `AtE.Linux` is now an `Exe` with a
     scaffolded `LinuxProgram.Main` that selects the platform and lays out the intended startup
     sequence (mirroring `AtE.Windows/Program.Main`) in comments, then stops with a clear
     `NotImplementedException`.
   - **✅ Transitional statics removed (reroute done).** The `Input`/`Rendering`/`Windowing` static
     holders are deleted and `PoEMemory.Memory` is now a thin accessor onto `Platform.Current.Memory`.
     All ~57 call sites go through `Platform.Current.{Memory,Input,Keys,Window,Renderer}`. (Owner of
     the backends is now `WindowsPlatform`/`LinuxPlatform`, not the statics.)
   - **✅ Seam-prep for the core extraction done (no files moved yet).** The residual Windows
     couplings that sat inside otherwise-portable code were cut behind seams so the portable set can
     compile without `Win32`/WinForms/`ImGuiController`/`Process`:
       - `IProcessMemory` gained `Pid`, `ModuleSize`, and `TryGetTargetWindowRect(...)` (Windows: via
         `Target` + `GetWindowRect`/`MainModule`; Linux: stubbed). The game-window rect callers
         (`Globals.WindowSize`, `PoEMemory` overlay-resize, `MapElement` minimap, `CoreSettings`
         debug) and the pattern-scan base/size now use these instead of `Win32`/`Process`.
       - `IRenderer` gained `GetNextTextOffset(uint)` (Windows: `ImGuiController.GetNextOffsetForTextAt`;
         Linux: returns 0). `Globals.Notify` and `DefenseDisplay` use it instead of reaching the
         Windows `ImGuiController`.
       - `IInputSource.IsTargetForeground` is now defined as **game-window-only** foreground (it was
         unused before); `PoEMemory.TargetHasFocus` routes through it, preserving the original
         game-only semantics (callers add `|| Overlay.HasFocus` themselves where wanted).
       - `PoEMemory.Target`/`Handle` (which cast to `WindowsProcessMemory`) are removed; the Windows
         `IsTargetForeground` sources the game window straight from `WindowsProcessMemory.Target`.
       - Dropped a dead `using System.Windows.Forms;` from `Console.cs`.
     After this, no portable-set file references `Win32`, `ImGuiController`, `Process.Target`, or
     WinForms (verified by grep; two harmless commented-out references remain).
   - ✅ **Done — portable files moved into `AtE.Core`.** Following a confirmed clean Windows build,
     the portable set was physically moved out of `AtE.Windows` into `AtE.Core`, preserving the
     folder layout: `StateMachine/` (HotKey, Machine, Runner, State), the PoE model
     (`PoE/Components/Component`, `ElementCache`, `EntityCache`, `GameStat`, `GameState`, `Offsets`,
     `PoEMemory`, all of `PoE/MemoryObjects/`), `Memory/` (Debugger, Memory, ObjectBrowser), all of
     `Plugins/` (incl. `PluginBase` and `CoreSettings`), `Globals`, `Console`, the `Overlay`
     orchestrator (`Overlay/Overlay.cs`) and `Overlay/Perf.cs`. `AtE.Windows` now keeps only
     `Win32`, the Overlay GPU backends (`D3DController`, `ImGuiController`, `OverlayForm`,
     `SpriteController`, `TextureCache`), the `Platform/Windows*` seam impls, `Program.cs` and
     `AssemblyInfo.cs`. Both projects use SDK-style default globbing, so no `<Compile>` edits were
     needed. `AtE.Core.csproj` gained the `ImGui.NET` PackageReference (1.87.3, matching Windows;
     the portable plugins/draw helpers call `ImGui.*`). The `System.Drawing` usage in the moved set
     is confined to the in-box `System.Drawing.Primitives` types (`Point`/`Rectangle`/`Color`/`Size`)
     — verified no `Bitmap`/`Graphics`/`Icon`/`Image` (the `Icon` identifiers are a project struct
     and field names, not `System.Drawing.Icon`) — so no `System.Drawing.Common` reference is needed.
   - ⚠️ **`InternalsVisibleTo` added.** A few portable types stay `internal` (`Machine`, `Runner`,
     and `PluginBase.Machine`) and are reached from the host entry points (`Program.Main`'s
     `Run(PluginBase.Machine)`, and the equivalent in the scaffolded `LinuxProgram.Main`). That was
     legal while everything lived in one assembly; after the move it crosses the assembly boundary,
     so `AtE.Core.csproj` declares `[InternalsVisibleTo]` for `AssistantToTheExile` (the `AtE.Windows`
     assembly name) and `AtE.Linux`. Verified by grep that the dependency is one-directional — no
     moved (core) file references a Windows-only concrete type (`Win32`/SharpDX/WinForms/
     `ImGuiController`/`D3DController`/`OverlayForm`/`SpriteController`/`TextureCache`/`Process`); the
     only such mention is one commented-out line in `DefenseDisplay.cs` — and no remaining Windows
     class inherits a portable base, so internal-visibility is the complete cross-boundary fix.
   - ⚠️ **`Globals` draw helpers pulled into the renderer seam (fallout of the move).** `Globals` is a
     `partial class` and two fragments of it still lived in Windows files (`SpriteController.cs`'s
     `DrawSprite` and `ImGuiController.cs`'s `DrawText`/`DrawTextAt`/`DrawCircle`/`DrawFrame`/`DrawLine`/
     `DrawBottomLeftText`/`ToRGBA`). Partial classes don't span assemblies, so after the move there were
     two distinct `Globals` types (one per assembly); the compiler bound `using static AtE.Globals` in
     the Windows files to the local one, hiding the core members (`Log`, etc.). These draw helpers are
     also called by the portable plugins and `Component.cs` needs `SpriteIcon`, so the fix was to make
     `Globals` single-homed in the core: `IRenderer` gained `DrawText`/`DrawTextAt`/`DrawCircle`/
     `DrawFrame`/`DrawLine`/`DrawSprite` (the existing `ImGuiController`/`SpriteController` impls are
     delegated to verbatim by `WindowsRenderer`; `LinuxRenderer` stubs them); the portable wrappers
     (overloads, bottom-left stacking, `ToRGBA`) now live in `AtE.Core/Overlay/Draw.cs` routing through
     `Platform.Current.Renderer`; and the `SpriteIcon` enum moved to `AtE.Core/Overlay/SpriteIcon.cs`.
     After this, no `partial class Globals` fragment remains in `AtE.Windows` (verified by grep). This
     supersedes the step-4 note that the `Draw*` helpers sit *above* the renderer seam — the primitive
     draws sit *on* it because they need the backend's per-frame draw-list/offset state.
   - ✅ **Done — all five Linux seam backends implemented** (memory → input → render → window),
     each self-contained on raw P/Invoke with no new managed dependencies, matching the cheat-sheet:
     `LinuxProcessMemory` (libc `process_vm_readv` + `/proc/<pid>/{maps,mem,comm,cmdline}`),
     `LinuxInputSink` (libc `uinput` virtual device), `LinuxInputSource` (libX11 `XQueryKeymap` +
     `XQueryPointer` for cursor/button mask + `_NET_ACTIVE_WINDOW`/`_NET_WM_PID`),
     `LinuxOverlayWindow` (libX11/libXext: ARGB visual + compositor, `_NET_WM_STATE_ABOVE`,
     XShape empty input region for click-through, an `XPending`/`XNextEvent` pump) and
     `LinuxRenderer` (GLX + libGL). The renderer departs from the cheat-sheet's
     "Veldrid/Silk.NET" suggestion in favour of a dependency-free, fixed-function GLX/OpenGL backend
     modelled on Dear ImGui's legacy `imgui_impl_opengl2` (client vertex arrays + ortho projection,
     using only GL 1.x/1.4 entry points libGL exports directly — no GL function-pointer loading);
     it binds a `GLXFBConfig` matched to the overlay window's ARGB visual, uploads the ImGui font
     atlas + sprite atlas as GL textures, submits ImGui draw data and atlas quads, and presents with
     `glXSwapBuffers`. Because there is no `System.Drawing.Common`/WIC on Linux, it includes a small
     built-in PNG reader (8-bit non-interlaced RGB/RGBA; zlib via `DeflateStream` + scanline
     unfilter) used by `LoadTexture` and the sprite atlas. The remaining work is host wiring +
     packaging + input/cleanup polish — see "## Remaining follow-ups" below.
   - ⚠️ **Build caveats to check.** (1) The file move was made without a compiler in the working
     environment — re-run `dotnet build AtE.sln` on Windows to confirm. As a bonus, `AtE.Core` is now
     a neutral `net9.0` library and should compile on Linux on its own (`dotnet build AtE.Core`),
     which is the cheapest way to catch any stray Windows coupling left in the moved set.
     (2) Five source files contain trailing NUL bytes that predate this work — four are now in
     `AtE.Core` (`Globals.cs`, `Overlay/Overlay.cs`, `PoE/PoEMemory.cs`, `PoE/MemoryObjects/MapElement.cs`)
     and one stays in `AtE.Windows` (`Overlay/ImGuiController.cs`); if the build flags them, stripping
     the trailing NULs should resolve it.

## Remaining follow-ups

The five seams and the portable core are in place; what's left is wiring the Linux host together,
shipping the runtime assets, and polishing the renderer/window seams. Roughly in the order you'd
want to tackle them:

### A. Build verification

- ✅ **Build verified.** `AtE.sln` (Windows) and `AtE.Core`/`AtE.Linux` (Linux) compile with all five
  seams implemented; the trailing-NUL files did not block the build. What's *not* yet exercised is
  runtime behaviour — the seams compile but have not been run end-to-end against a live game (blocked
  on the host wiring in B). Treat C–F as runtime-correctness work, not compile fixes.

### B. Linux host entry point (`LinuxProgram.Main`)

- ✅ **`LinuxProgram.Main` un-stubbed.** It no longer throws; it now runs the full startup sequence
  verbatim from `AtE.Windows/Program.Main` against the portable `AtE.Core` orchestration:
  `Platform.Initialise(new LinuxPlatform())` → `Overlay.Initialise()` →
  `RunForever("PoEMemory", PoEMemory.OnTick)` → `Run(PluginBase.Machine)` → `Run(new Console())` →
  start the `EntityCache.MainThread` worker thread → `Overlay.RenderLoop()` (blocking) → `Join` the
  worker on exit. The cross-check against the Windows `Main` found no Windows-only setup to port: that
  `Main` has no single-instance mutex, no `AppDomain`/working-directory assumptions, and no exception
  dialog — the two `Main`s now differ only in which `IPlatform` they construct. `Run`/`RunForever`/`Log`
  come from `using static AtE.Globals`; `PluginBase.Machine` and `EntityCache.MainThread` are `internal`
  in the core and reached via the `[InternalsVisibleTo("AtE.Linux")]` declared in `AtE.Core.csproj`.
- ✅ **`Console`/`EntityCache` confirmed present in `AtE.Core`** (`AtE.Core/Console.cs`,
  `AtE.Core/PoE/EntityCache.cs`), along with `Overlay.Initialise`/`RenderLoop` (`AtE.Core/Overlay/Overlay.cs`)
  — all referenced by `LinuxProgram.Main`.
- ⚠️ **Not yet run end-to-end.** The host compiles and composes, but actual runtime behaviour against a
  live game is still gated on the renderer/window/input polish in C–F.

### C. Runtime asset packaging

- ✅ **Runtime assets consolidated into `AtE.Core\Static` and linked from both hosts.** `Sprites.png`
  and `GreenFlask.ico` were physically moved out of `AtE.Windows` into the shared, platform-neutral
  `AtE.Core\Static\` (the duplicate root-level `GreenFlask.ico` was removed; both copies were
  identical). Each host links them as `Content` with `Link="Static\…"` +
  `CopyToOutputDirectory=PreserveNewest`, so both builds get `<output>/Static/{Sprites.png,
  GreenFlask.ico}` from a single source of truth. `LinuxRenderer` decodes `Static/Sprites.png` from
  there; the Windows `ApplicationIcon` now points at `..\AtE.Core\Static\GreenFlask.ico`. The icon is
  carried into the Linux output too for a planned later use in the Linux backend (e.g. setting the
  overlay's window icon via `_NET_WM_ICON`).

### D. Renderer polish (`LinuxRenderer`)

The concrete input/lifecycle items (keyboard/text, wheel, event-driven buttons, GL teardown, the
threading guard) are now done. The remaining three are the explicitly conditional/optional ones
(PNG-decoder scope, HiDPI, the cross-platform GPU consolidation), left deliberately with their
rationale below.

- ✅ **Keyboard & text input to ImGui.** `LinuxOverlayWindow` now selects
  `KeyPress`/`KeyRelease`/`ButtonPress`/`ButtonRelease` (added to the create-time event mask alongside
  `StructureNotify`/`Exposure`), decodes each X event into an X11-free `LinuxOverlayInput` record
  (resolving the KeySym via a new `LinuxInputSource.KeysymToVKey` — the inverse of the existing
  `VKeyToKeysym`, kept next to it so keysym knowledge stays in one file — and typed characters via
  `XLookupString`), and queues them. The renderer drains the queue each `BeginFrame` (same render-loop
  thread, so the lock is only defensive) and feeds ImGui `io.KeysDown` (VK-indexed), `io.AddInputCharacter`,
  and `io.KeyShift`/`KeyCtrl`/`KeyAlt`; it also sets up `io.KeyMap` identically to `ImGuiController`. This
  is the Linux analogue of the WinForms `KeyDown`/`KeyUp`/`KeyPress` wiring. The click-through
  chicken-and-egg is unchanged (the empty XShape input region gates *delivery*, so these events only
  arrive once `WantCapture*` from the previous frame turned click-through off). **Runtime caveat:** X
  delivers `KeyPress` only to the window holding keyboard focus; a WM-managed utility window may not get
  focus from a click, so keyboard entry into ImGui fields may still need a focus nudge
  (`XSetInputFocus`) or compositor validation — tracked with the other runtime items in E.
- ✅ **Mouse wheel.** X buttons 4/5 (vertical wheel) are translated to `io.MouseWheel` (+1/−1 per notch),
  accumulated per frame. Horizontal-wheel buttons 6/7 and XInput2 smooth-scroll remain unwired (not
  needed by current plugins).
- ✅ **Event-driven mouse buttons.** `io.MouseDown` is now driven by real `ButtonPress`/`ButtonRelease`
  events delivered to the overlay (X1/X2 included), not the global pointer mask, so ImGui no longer sees
  a click meant for the game as its own. Held state is cleared whenever click-through is on (ImGui not
  capturing) so a release that fell through to the game can't leave a button stuck down. Cursor
  *position* is still polled globally (the shaped-out input region means X delivers no motion events to
  the overlay) — position isn't a click, so this is correct.
- ✅ **GL resource cleanup / context teardown.** `LinuxRenderer` now tracks every GL texture it creates
  (font + sprite atlases, any `LoadTexture` result) and has an idempotent `Shutdown()` that
  `glDeleteTextures` them, `glXMakeCurrent(None)` + `glXDestroyContext`, and `ImGui.DestroyContext`. It
  implements `IDisposable` (`Dispose() => Shutdown()`). Teardown is invoked from
  `LinuxOverlayWindow.RunMessageLoop`'s `finally` — on the render-loop thread, while the `Display*` and
  drawable are still valid, before the window/display are destroyed — because GL/GLX calls are only
  valid on the thread the context is current on. `Close()` was correspondingly reduced to *signalling*
  the loop to stop (the loop then tears down on its own thread); window/display destruction moved into a
  private `DestroyWindow()` run from the same `finally`, with a `loopRunning` guard so a `Close()` from
  another thread can't cause a double `XDestroyWindow`. Deliberate deviation from the `LinuxInputSink`
  pattern: **no finalizer** — a finalizer runs on the GC thread where the GLX context isn't current, so
  calling GL there would be invalid; teardown must come through the render-loop thread.
- ✅ **Threading assumption — confirmed.** Audited: `Renderer.LoadTexture` has **no callers anywhere** in
  the codebase today (the only texture uploads are the font + sprite atlases, done in `Initialise` on
  the render-loop thread), and every per-frame call into the renderer happens inside
  `Machine.DefaultMachine.OnTick`, which runs in `Overlay.RenderLoop`'s `onFrame` on the render-loop
  thread (so even `PoEMemory.OnTick`'s `Clear`/gating calls are on-thread). As a guard for the future,
  `LoadTexture` records the render thread id at context creation and logs a loud warning if ever called
  off it (recommending marshalling onto the render thread or a shared context).
- ⚠️ **PNG decoder scope (unchanged — conditional).** Still handles only 8-bit, non-interlaced,
  colour-type 2 (RGB) / 6 (RGBA); palette/grayscale/16-bit/Adam7 are rejected with a clear log. This is
  sufficient for the current `Sprites.png` and any `LoadTexture` asset, and the item is explicitly
  future-conditional, so it was left as-is. If future assets need more, extend the decoder or adopt
  `SkiaSharp`/`ImageSharp` (which would remove the built-in decoder entirely).
- ⚠️ **HiDPI (unchanged — conditional).** `io.DisplayFramebufferScale` stays `1.0` and the framebuffer
  size is taken equal to the window pixel size. Real scale detection is environment-specific (Xrandr /
  `Xft.dpi` / `ConfigureNotify` framebuffer size) and only wanted under fractional scaling, so it is left
  for when HiDPI support is actually needed rather than implemented half-way.
- ⚠️ **Optional renderer consolidation (unchanged — larger refactor).** Adopting one cross-platform GPU
  layer (Veldrid/Silk.NET) for *both* platforms to collapse the `IRenderer` seam and drop SharpDX still
  stands as a larger, separate refactor. The current two-backend split (SharpDX D3D11 on Windows,
  fixed-function GLX here) is the lower-risk path now in place.

### E. Window seam (`LinuxOverlayWindow`) follow-ups

- **Wayland.** Out of scope today; the overlay assumes an X11 session (XWayland works) and a
  borderless-windowed game rather than exclusive fullscreen. Native Wayland needs the layer-shell
  protocol and is compositor-dependent (and the GLX renderer would need an EGL path).
- **Override-redirect fallback.** The window is WM-managed (`_NET_WM_WINDOW_TYPE_UTILITY` +
  `_NET_WM_STATE_ABOVE`) so focus/topmost stay meaningful; some compositors may need the
  override-redirect path instead (the `CWOverrideRedirect` attribute is present to flip). Validate
  transparency + click-through + always-on-top across the common compositors (KWin, Mutter, Picom).
- **`TryGetTargetWindowRect` is stubbed in `LinuxProcessMemory`.** Game-window geometry (for the
  overlay-follows-game resize path, `Globals.WindowSize`, the minimap, the `CoreSettings` debug
  readout) returns "unavailable" on Linux. Implement it against X11 (find the PoE window by
  `_NET_WM_PID` → `XGetWindowAttributes`/`XTranslateCoordinates`, accounting for `_NET_FRAME_EXTENTS`).
  This belongs with the window/X11 layer, not the memory backend, so consider moving the capability
  onto `IOverlayWindow` or a shared X11 helper that both `LinuxInputSource` and `LinuxOverlayWindow`
  already partially duplicate (each opens its own `Display`, interns `_NET_*` atoms, reads root
  properties — ripe for a small shared `LinuxX11` utility).

### F. Runtime permissions & environment (document for users, not code)

- **Memory reads** need `CAP_SYS_PTRACE`, a matching uid, or a permissive
  `/proc/sys/kernel/yama/ptrace_scope` (the cross-process `process_vm_readv` path).
- **Synthetic input** needs `/dev/uinput` access — root or the `input` group plus a udev rule.
- **Transparency** needs a running compositor and a 32-bit ARGB visual.
- **ToS / anti-cheat.** Synthetic input automation carries the same risk on Linux as on Windows
  (see "Biggest risks" below); independent of the port.

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
