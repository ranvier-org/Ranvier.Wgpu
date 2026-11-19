---
title: SDL3 zero-copy & browser Fable
description: Hardware VRAM surface compositing in Ranvier.View.WebCore.Sdl and WebGPU/WebGL2 browser rendering via Fable.Ranvier.Wgpu.
---

# SDL3 zero-copy & browser Fable

The same `ScenePass`, camera, primitive, instance, and shader-hook vocabulary runs natively inside `Ranvier.View.WebCore.Sdl` windows (`Ranvier.Wgpu.Sdl`) and in web browsers (`Fable.Ranvier.Wgpu`).

## Zero-copy native DOM compositing (`Ranvier.Wgpu.Sdl`)

`Ranvier.View.WebCore.Sdl` stays completely `wgpu`-free until an application opts into `Ranvier.Wgpu.Sdl` (or `Ranvier.Wgpu.Sdl.Traced` when building with `-p:RanvierTrace=true`).

```sh
dotnet add package Ranvier.Wgpu.Sdl
```

`WgpuSdlSurface` binds a `WgpuContext` directly onto any DOM element (`<div>` or `<canvas>`) inside a `Ranvier.View.WebCore.Sdl.Window`, allowing ordinary HTML controls, CSS backdrops, and `Ranvier.D5` SVG overlays to composite cleanly on top of 4× MSAA 3D geometry while preserving click and focus routing to child elements.

### Declarative element lifetime (`WgpuSdlSurface.lend`)

```fsharp
open System
open System.Numerics
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Sdl

use window = Window.Create("Ranvier + Wgpu", 1280, 800)
let viewport = window.Host.Document.getElementById "viewport"

use wgpu = window |> WgpuSdlSurface.createContext
use mesh = Primitives.unitIcosphere 2 |> WgpuContext.uploadMesh <| wgpu
use batch = wgpu |> WgpuContext.createOpaqueBatch mesh
batch.SetSingle(
    Instance3D.sphere Vector3.Zero 1.8f (RgbaColor.fromHex "#0ea5e9") 0.3f 1u InstanceFlags.None
)

let camera = Camera3D.orbit Vector3.Zero -30.0f 22.0f 5.0f 45.0f

use subscription =
    viewport
    |> WgpuSdlSurface.lend window wgpu (fun surface ->
        let pass =
            ScenePass.create camera
            |> ScenePass.withClear RgbaColor.darkBackdrop
            |> ScenePass.withBatches [| batch |]
        let metrics = pass |> WgpuSdlSurface.render surface
        { new IDisposable with member _.Dispose() = () })
```

You can also bind imperatively via `WgpuSdlSurface.bind window wgpu element` (or `window.BindWgpuSurface(element, wgpu)`), resize render targets and shared textures via `WgpuSdlSurface.resize width height surface`, and pass custom WGSL via `WgpuSdlSurface.CreateContext(window, customWgsl = wgsl)`.

### Presentation transport modes (`WgpuPresentationMode`)

`surface.Mode` reports the negotiated presentation path (`surface.IsZeroCopyGpu` and `surface.IsHardwareComposited`):

| `WgpuPresentationMode` | Platform & Mechanism | CPU Readback |
| :--- | :--- | :--- |
| `Dx12SharedTexture` | Windows Direct3D 12 → D3D11 shared NT `HANDLE` texture synchronized via GPU shared fence | `0.00 ms` (pure VRAM) |
| `MetalIOSurface` | macOS Metal → 3-slot `IOSurfaceRef` composited in hardware by WebCore | `0.00 ms` (pure VRAM) |
| `GpuTextureUpload` | Hardware WebCore shared-texture composition fed by `WgpuContext` upload (e.g., Linux EGL or cross-adapter fallback) | Upload path |
| `CanvasFallback` | Software DOM `<canvas>` fallback when hardware GPU element composition is disabled | Software fallback |

Disposing `WgpuSdlSurface` detaches the element surface and releases bridge handles without disposing the caller-owned `WgpuContext`.

## Browser WebGPU & WebGL2 (`Fable.Ranvier.Wgpu`)

`Fable.Ranvier.Wgpu` compiles the shared `Ranvier.Wgpu` F# scene files (`Types.fs`, `Primitives.fs`, `Shaders.fs`, `ScenePass.fs`) to JavaScript ES modules via [Fickl](https://github.com/shayanhabibi/fickl) (`0.11.0+`), rendering on WebGPU where available and automatically falling back to WebGL2.

```sh
dotnet add package Fable.Ranvier.Wgpu
dotnet tool install fickl
```

### Browser quick start

```fsharp
open System.Numerics
open Fickl.Core
open Ranvier.Wgpu

let draw (canvas: obj) =
    jsAsync {
        let! ctx = WgpuContext.CreateAsync(canvas)
        let mesh = ctx.UploadMesh(Primitives.unitIcosphere 3)
        let batch = ctx.CreateBatch mesh

        batch.SetSingle(
            Instance3D.sphere Vector3.Zero 2.0f (RgbaColor.fromHex "#3b82f6") 0.28f 1u InstanceFlags.None
        )

        let camera = Camera3D.orbit Vector3.Zero 0.6f 0.4f 5.0f 45.0f

        let pass =
            ScenePass.create camera
            |> ScenePass.withClear (RgbaColor.fromHex "#090d16")
            |> ScenePass.withBatches [| batch |]

        ctx.Render pass |> ignore
        let! hit = ctx.PickAsync(ctx.Width / 2, ctx.Height / 2, pass)
        return hit
    }
```

Compile to an ES module directory with:

```sh
dotnet fickl MyBrowserApp.fsproj --outDir dist
```

### API differences between `.NET` and Browser

Because browser GPU initialization, buffer mapping, and shader compilation diagnostics are asynchronous, three entry points become `jsAsync` operations in `Fable.Ranvier.Wgpu`:

| Native (`Ranvier.Wgpu`) | Browser (`Fable.Ranvier.Wgpu`) |
| :--- | :--- |
| `new WgpuContext(...)` / `WgpuContext.create` | `WgpuContext.CreateAsync(canvas, ?width, ?height, ?customWgsl, ?customGlsl, ?backend)` |
| `ctx.Pick(px, py, pass)` / `WgpuContext.pick` | `ctx.PickAsync(px, py, pass)` |
| `WgpuContext.ValidateWgsl(wgsl)` | `WgpuContext.ValidateWgslAsync(wgsl)` and `WgpuContext.ValidateGlslAsync(glsl)` |

Additional browser notes:

- **Backend selection (`WgpuBrowserBackend`)**: `Auto` (default) chooses `WebGPU` when `navigator.gpu` yields an adapter and `WebGL2` otherwise; inspect `ctx.BackendName` at runtime.
- **Dual-backend custom shaders**: `Shaders.composeScene` builds custom WGSL from the six `SceneShaderHook` points for WebGPU, and `GlslShaders.composeScene` builds matching GLSL ES 3.00 from the same six `SceneShaderHook` points for WebGL2. Pass both `customWgsl` and `customGlsl` when targeting `WgpuBrowserBackend.Auto` so both backends have a custom shader.
- **Frame readback**: `ctx.Render pass` presents directly to the HTML `<canvas>`; call `ctx.ReadPixelsAsync()` when pixel readback is required in tests.
- **Value vs. reference semantics in JS**: Under JavaScript compilation, `Vector3` and `Matrix4x4` are JS objects rather than stack-copied `.NET` structs—construct fresh vectors/matrices rather than mutating shared instances through aliases.
