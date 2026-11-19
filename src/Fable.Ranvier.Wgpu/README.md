# Fable.Ranvier.Wgpu

The [`Ranvier.Wgpu`](https://www.nuget.org/packages/Ranvier.Wgpu) 3D scene API for the browser. It is compiled to JavaScript with [Fickl](https://github.com/shayanhabibi/fickl) and draws on WebGPU, or on WebGL2 where a browser has no WebGPU.

Cameras, instances, primitives, the stock shader and shader hooks are the same F# source files as the .NET package's, so a scene written for one runs on the other.

## Install

```sh
dotnet add package Fable.Ranvier.Wgpu --version 0.2.1
```

```sh
dotnet tool install fickl
```

## Quick start

```fsharp
open System.Numerics
open Fickl.Core
open Ranvier.Wgpu

let draw (canvas: obj) =
    jsAsync {
        let! ctx = WgpuContext.CreateAsync (canvas)
        let mesh = ctx.UploadMesh (Primitives.unitIcosphere 3)
        let batch = ctx.CreateBatch mesh

        batch.SetSingle (
            Instance3D.sphere Vector3.Zero 2.0f (RgbaColor.fromHex "#3b82f6") 0.28f 1u InstanceFlags.None
        )

        let camera = Camera3D.orbit Vector3.Zero 0.6f 0.4f 5.0f 45.0f

        let pass =
            ScenePass.create camera
            |> ScenePass.withClear (RgbaColor.fromHex "#090d16")
            |> ScenePass.withBatches [| batch |]

        ctx.Render pass |> ignore
        let! hit = ctx.PickAsync (ctx.Width / 2, ctx.Height / 2, pass)
        return hit
    }
```

Compile the project with `dotnet fickl <project> --outDir <folder>` and load the result as an ES module.

## What differs from the .NET package

The browser's graphics APIs are asynchronous, so three members change:

| .NET | Browser |
|---|---|
| `WgpuContext(...)` constructor | `WgpuContext.CreateAsync(canvas, ?width, ?height, ?customWgsl, ?customGlsl, ?backend)` |
| `Pick` | `PickAsync` |
| `ValidateWgsl` | `ValidateWgslAsync`, and `ValidateGlslAsync` for WebGL2 |

- `Render` draws to the canvas. `ReadPixelsAsync` reads the last frame back.
- There is no `RenderInto`, `RgbaPixels`, native backend choice or SDL surface.
- A `Vector3` or `Matrix4x4` is a JavaScript object, so assigning one shares it. .NET copies it. Code that
  changes a field of a vector through one of two names sees the change through both.

## Backends

`backend` is `WgpuBrowserBackend.Auto` (the default), `WebGPU` or `WebGL2`. `Auto` uses WebGPU when
`navigator.gpu` gives an adapter and WebGL2 otherwise. `ctx.BackendName` reports which one draws.

A custom shader is given per language: `customWgsl` for WebGPU and `customGlsl` (GLSL ES 3.00) for WebGL2.
`Shaders.composeScene` builds WGSL from the six hook points, and `GlslShaders.composeScene` builds GLSL from the
same hook points. A context that lands on a backend whose language it was not given raises an exception; it does
not draw the stock shader. A page that should run on both backends passes both.

## Requirements

- Fickl 0.11.0 or later. Earlier releases cannot compile `System.Numerics` vectors and spans.
- .NET 10 (`net10.0`) and `FSharp.Core` 10.0.100 or later, which ships with the first .NET 10 SDK, so any .NET 10
  SDK builds against this package. Version 0.1.0 required `FSharp.Core` 10.1.401 (.NET SDK 10.0.401).
- A browser with WebGPU or WebGL2.

## Current limits

- The limits of `Ranvier.Wgpu` apply: no textures, lines are a one-pixel line list, and discs and blended
  batches are not depth-sorted.
- Tested in Chrome on Windows. Firefox, Safari, Linux and macOS are not tested.
- On WebGL2 through Direct3D 11, two frames of an unchanged scene can differ by one step of 255 in a few pixels
  where a line lies under a blended mesh.
- A lost WebGPU device or WebGL context is not recovered.
