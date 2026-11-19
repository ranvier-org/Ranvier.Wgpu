---
title: WebGPU 3D (Wgpu)
description: NativeAOT wgpu-native and browser WebGPU/WebGL2 3D instanced batches, lines, SDF discs, GPU picking, and zero-copy SDL3 surfaces.
---

# WebGPU 3D (`Ranvier.Wgpu`)

`Ranvier.Wgpu` provides NativeAOT-compatible F# bindings and a zero-allocation `ScenePass` 3D engine over `wgpu-native` (`webgpu.h`), paired with an opt-in zero-copy GPU texture bridge for `Ranvier.View.WebCore.Sdl` and a source-compatible browser package (`Fable.Ranvier.Wgpu`) targeting WebGPU and WebGL2 via Fickl.

## Package architecture

| Package | Native Payload | Role |
| :--- | :--- | :--- |
| **`Ranvier.Wgpu`** | Multi-RID `wgpu_native` (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`) | Standalone NativeAOT-clean WebGPU 3D engine: premultiplied sRGB 4× MSAA `Opaque`/`AlphaBlend` `InstanceBatch`, 3D `LineBatch`, camera-facing SDF `DiscBatch`, world-space `SectionPlane` clipping, `[<Struct>] ScenePass`, custom WGSL shader hooks (`Shaders.composeScene`), and 1×1 `RGBA32Uint` GPU picking (`WgpuContext.pick`). |
| **`Ranvier.Wgpu.Sdl`** *(+ `.Traced`)* | ~25 KB managed (pulls `Ranvier.Wgpu`) | Opt-in zero-copy bridge (`WgpuSdlSurface.createContext`, `WgpuSdlSurface.bind`, `WgpuSdlSurface.lend`, `WgpuSdlSurface.render`) rendering 4× MSAA directly into shared DX12 → D3D11 NT-handle VRAM textures on Windows (`Dx12SharedTexture`) and Metal → `IOSurfaceRef` textures on macOS (`MetalIOSurface`), compositing HTML, CSS, and `Ranvier.D5` SVG on top. |
| **`Fable.Ranvier.Wgpu`** | None (compiled to JS by Fickl) | Browser scene API (`WgpuContext.CreateAsync`) sharing `Types.fs`, `Primitives.fs`, `Shaders.fs`, and `ScenePass.fs` with the native package, drawing on WebGPU with an automatic WebGL2 fallback. |

## Quick start (offscreen or headless `.NET 10`)

```sh
dotnet add package Ranvier.Wgpu
```

```fsharp
open System.Numerics
open Ranvier.Wgpu

use ctx = WgpuContext.create 1280 720
use mesh = Primitives.unitIcosphere 3 |> WgpuContext.uploadMesh <| ctx
use batch = ctx |> WgpuContext.createOpaqueBatch mesh

batch.SetSingle(
    Instance3D.sphere Vector3.Zero 2.0f (RgbaColor.fromHex "#3b82f6") 0.28f 1u InstanceFlags.None
)

let camera = Camera3D.orbit Vector3.Zero 0.6f 0.4f 5.0f 45.0f

let pass =
    ScenePass.create camera
    |> ScenePass.withClear (RgbaColor.fromHex "#090d16")
    |> ScenePass.withBatches [| batch |]

let metrics = pass |> WgpuContext.render ctx
let hit = pass |> WgpuContext.pick (ctx.Width / 2) (ctx.Height / 2) ctx
```

## Requirements and engine design limits

- **Runtime & SDK floor**: Targets `net10.0` with `FSharp.Core` `10.0.100` or later, so any `.NET 10` SDK builds against the native and browser packages.
- **Fixed pass draw order**: Each `ScenePass` draws in deterministic five-stage order:
  1. `Opaque` `InstanceBatch` passes (depth-tested, depth-writing)
  2. `AlphaBlend` `InstanceBatch` passes with `DrawBeforeLines = true` (backdrop panels behind hairlines; depth writes disabled)
  3. `LineBatch` passes (`LineList`, 4× MSAA premultiplied alpha, depth-tested against opaque geometry)
  4. `AlphaBlend` `InstanceBatch` passes with `DrawBeforeLines = false` (default; premultiplied alpha, depth writes disabled)
  5. `DiscBatch` passes (camera-facing SDF billboards, depth-tested against opaque geometry, depth writes disabled)
  Empty batches (`Count = 0`) are skipped automatically.
- **Intentional constraints**:
  - **No texture or sampler bindings**: Shaders receive `SceneUniforms` (`@binding(0)`), 16 `vec4<f32>` user uniform slots (`@binding(1)`), and vertex/instance attributes—surface detail is procedural or per-vertex.
  - **Line lists**: `LineBatch` renders independent 1-pixel segments from vertex pairs `(2k, 2k+1)`. Thicker 3D strokes can be built as prism/tube meshes via `MeshBuilder.AddTube` or `MeshBuilder.AddLineSegment`.
  - **Caller-ordered translucency**: `AlphaBlend` instance batches and `DiscBatch` billboards do not write depth and are drawn in caller-supplied order; sort overlapping translucent geometry far-to-near when exact ordering is needed.

## Guides in this section

- **[3D engine, batches, picking & custom WGSL](engine-and-batches.md)**: Procedural meshes (`Primitives`, `MeshBuilder`), dynamic GPU buffers (`MeshHandle`, `InstanceBatch`, `LineBatch`, `DiscBatch`), cameras, lighting, section cuts, 1×1 GPU picking, and `Shaders.composeScene`.
- **[Zero-copy SDL3 surfaces & browser Fable target](sdl-and-fable.md)**: Compositing 3D scenes into `Ranvier.View.WebCore.Sdl` DOM elements with zero CPU readback (`WgpuSdlSurface`), and compiling the shared scene API to WebGPU/WebGL2 in the browser with `Fable.Ranvier.Wgpu` and Fickl.
