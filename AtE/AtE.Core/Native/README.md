# Native binaries

Third-party native libraries AtE loads at runtime, one folder per
[RID](https://learn.microsoft.com/dotnet/core/rid-catalog):

```
Native/
  win-x64/libooz.dll     <- put the Windows build here
  linux-x64/libooz.so    <- put a Linux build here, if you make one
```

Each host project (`AtE.Windows`, `AtE.Linux`) links its platform's file into the **root** of its
output directory with `CopyToOutputDirectory`, so the built app ends up as:

```
AssistantToTheExile.exe
libooz.dll
Static/...
```

The root placement is deliberate: `Oodle.TryInitialise` asks `NativeLibrary.TryLoad` for the name
`libooz` with `DllImportSearchPath.AssemblyDirectory`, which probes exactly that directory and
applies the platform's own naming rules (`libooz.dll` / `libooz.so` / `libooz.dylib`). One name, no
per-OS code, no directory scanning.

Both csproj items are wrapped in `Condition="Exists(...)"`, so the solution still builds when a
binary is missing. What you get instead is a clear runtime message from the Bundle Browser
("no Oodle decoder loaded") rather than a build break.

## Why this is needed at all

Everything inside `Bundles2/` — including `_.index.bin` — is compressed with Oodle
(Kraken/Mermaid/Leviathan). Path of Exile links Oodle **statically** into `PathOfExile.exe` and
ships no redistributable DLL, so there is nothing in the game folder to load. Oodle itself is
commercial and cannot be vendored.

`ooz` (<https://github.com/zao/ooz>) is a clean-room open-source decoder for those same streams. It
exports `Ooz_Decompress(src, srcLen, dst, dstLen)`, which is what `Oodle.cs` binds. A real
`oo2core_*_win64.dll` also works if you have one — it exports `OodleLZ_Decompress` and `Oodle.cs`
accepts either — but it is not distributable either, so it belongs in the "Oodle DLL" override
setting rather than in this folder.

## Provenance / licensing — read before committing a binary

`zao/ooz` is a fork of `powzix/ooz` and **declares no licence**. Committing a build of it into this
repo is therefore a decision to make deliberately, not a detail. Note also that the code is a
reverse-engineered reimplementation of a commercial codec.

If you would rather not commit it, the alternative costs one manual step: leave these folders empty
and have each user drop their own `libooz.dll` next to the exe (or set the override path). The
`Condition="Exists(...)"` guards mean nothing else has to change.

Record what you actually put here:

| File | Source | Version / commit | Date added |
| ---- | ------ | ---------------- | ---------- |
| `win-x64/libooz.dll` | | | |

## Building `libooz.so` for Linux

`ooz` ships a `CMakeLists.txt` and vendors [simde](https://github.com/simd-everywhere/simde) as a
submodule specifically so it builds off MSVC/x86, so a Linux shared library is realistic:

```sh
git clone --recursive https://github.com/zao/ooz
cd ooz
cmake -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_POSITION_INDEPENDENT_CODE=ON
cmake --build build -j
```

Two things to check, because upstream is built primarily as a CLI:

1. You need a **shared** library, not just the `ooz` executable and a static archive. If CMake only
   produces a static lib, either add `BUILD_SHARED_LIBS=ON` or add a small shared target of your own
   over the same sources.
2. Confirm the symbol is actually exported with C linkage:
   `nm -D --defined-only libooz.so | grep Ooz_Decompress`. If it is missing, the load will succeed
   and the bind will fail — which `Oodle.SearchReport` will tell you verbatim.

Then copy the result to `Native/linux-x64/libooz.so`. No code changes; `AtE.Linux.csproj` picks it
up on the next build.

Untested caveat, stated plainly: the Linux path has never been run. The managed side is
platform-neutral and `NativeLibrary` handles the name mapping, so it *should* work as soon as a
valid `.so` is present, but nobody has proven that yet.
