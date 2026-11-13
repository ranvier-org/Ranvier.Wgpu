# Ranvier.Wgpu

Cross-platform, NativeAOT-compatible F# bindings, zero-allocation `ScenePass` 3D engine, and **opt-in zero-copy `Ranvier.View.WebCore.Sdl` GPU surface bridge** over **`wgpu-native` (`v29.0.1.1` / `webgpu.h`)**.

## Opt-In Package Architecture

| Package | Native Payload | Role |
| :--- | :--- | :--- |
| **`Ranvier.View.WebCore.Sdl`** *(in `Ranvier.View`)* | **0 MB extra** (`wgpu`-free) | Vendor-neutral `GpuSurface` (`GpuSurface.bind`, `GpuSurface.lend`, capability-typed `GpuSurfaceBackend` over D3D11 shared `ID3D11Texture2D` + NT `HANDLE` + `ID3D11Fence`, Metal `IOSurfaceRef`, EGL `EGLImage`, or `<canvas>` fallback), disables drag-selection highlighting by default while preserving interactive child controls, and exposes zero-alloc `window.ReadPixelsInto(Span<byte>)`. |
| **`Ranvier.Wgpu`** | **Multi-RID `wgpu_native`** (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`) | Standalone NativeAOT-clean WebGPU 3D engine: premultiplied sRGB 4× MSAA `Opaque`/`AlphaBlend` `InstanceBatch`, 3D `LineBatch`, SDF `DiscBatch` with clockwise verification arcs & hollow portals, WGSL `SectionPlane`, zero-alloc `[<Struct>] ScenePass`, and 1×1 `RGBA32Uint` `WgpuContext.pick`. |
| **`Ranvier.Wgpu.Sdl`** *(+ `.Traced`)* | **~25 KB managed** (pulls `Ranvier.Wgpu` when referenced) | Opt-in zero-copy bridge (`WgpuSdlSurface.bind`, `WgpuSdlSurface.lend`, `WgpuSdlSurface.render`): renders 4× MSAA directly into DX12 → D3D11 shared NT-handle textures synchronized via GPU shared fence on Windows (`Dx12SharedTexture`, `ReadbackMs = 0.00 ms`, `CpuFramesPresented = 0`) and 3-slot Metal → `IOSurfaceRef` on macOS (`MetalIOSurface`), compositing HTML, CSS, and `Ranvier.D5` SVG naturally on top. |

## Functional Pipeline Example

```fsharp
open System.Numerics
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Sdl

use wgpu = window |> WgpuSdlSurface.createContext
use mesh = Primitives.unitIcosphere 2 |> WgpuContext.uploadMesh <| wgpu
use batch = wgpu |> WgpuContext.createOpaqueBatch mesh
let batches = [| batch |]
batch.SetSingle(Instance3D.sphere Vector3.Zero 1.8f (RgbaColor.fromHex "#0ea5e9") 0.3f 1u InstanceFlags.None)

let pass =
    ScenePass.create (Camera3D.orbit Vector3.Zero -30.0f 22.0f 5.0f 45.0f)
    |> ScenePass.withClear RgbaColor.darkBackdrop
    |> ScenePass.withBatches batches

// Direct DOM element pipeline or declarative `div.lend` / `canvas.lend`:
use surface = viewportEl |> WgpuSdlSurface.bind window wgpu
let metrics = pass |> WgpuSdlSurface.render surface
```

## Repository Layout

```text
Ranvier.Wgpu/
├── pins.json                   # Pinned wgpu-native v29.0.1.1 release + SHA-256 digests across 6 RIDs
├── scripts/
│   └── fetch_wgpu_native.py    # Verifies SHA-256 digests and stages runtimes/<rid>/native/*
├── headers/webgpu/             # Official wgpu-native v29.0.1.1 C headers (webgpu.h, wgpu.h)
├── tools/
│   ├── type_map.json           # Declarative C-to-F# primitive, typedef, keyword & union mapping
│   └── gen_bindings.py         # Deterministic header generator -> Generated/WgpuBindings.fs
├── src/
│   ├── Ranvier.Wgpu/           # Core WebGPU 3D engine (IsAotCompatible=true, multi-RID wgpu_native)
│   └── Ranvier.Wgpu.Sdl/       # Opt-in zero-copy SDL/WebCore shared GPU surface bridge (+ .Traced)
├── tests/Ranvier.Wgpu.Tests/   # Hardware GPU surface compositing, child button clicks, lend lifecycle & resize tests
└── samples/
    ├── RanvierWgpuBim/         # IFC/BIM authoring, translucent glazing, gizmos & live WGSL section cuts (0.00ms readback)
    └── RanvierWgpuAtlas/       # Faithful &Atlas 3,459-project Map/Globe/Layers + Ranvier.D5 call-graph explorer (0.00ms readback)
```

## Building, Testing, & Running Samples

```powershell
# Fetch pinned wgpu-native runtime binaries (verified against pins.json SHA-256 digests)
python scripts/fetch_wgpu_native.py

# Build entire solution in Release (0 warnings, 0 errors, WarnOn=3390)
dotnet build Ranvier.Wgpu.sln -c Release --no-incremental

# Run hardware GPU surface compositing & lifecycle test suite
dotnet run --project tests/Ranvier.Wgpu.Tests -c Release

# Run interactive BIM sample (or add `-- --smoke` for headless verification)
dotnet run --project samples/RanvierWgpuBim -c Release

# Run interactive &Atlas Map/Globe/Layers + D5 Call-Graph sample (or add `-- --smoke` for headless verification)
dotnet run --project samples/RanvierWgpuAtlas -c Release

# Pack NuGet packages (untraced + traced)
dotnet pack src/Ranvier.Wgpu/Ranvier.Wgpu.fsproj -c Release -o artifacts/nupkg
dotnet pack src/Ranvier.Wgpu.Sdl/Ranvier.Wgpu.Sdl.fsproj -c Release -o artifacts/nupkg
dotnet pack src/Ranvier.Wgpu.Sdl/Ranvier.Wgpu.Sdl.fsproj -c Release -p:RanvierTrace=true -o artifacts/nupkg
```

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
