# Ranvier.Wgpu

NativeAOT-compatible F# 3D and WebGPU (`webgpu.h` / `wgpu-native`) engine with runtime WGSL shaders, 4× MSAA, hardware instancing (`MeshBatch`, `LineBatch`, `DiscBatch`), live BIM section clipping, premultiplied sRGB blending, and 1×1 `RGBA32Uint` GPU pixel picking.

## Install

```sh
dotnet add package Ranvier.Wgpu --version 0.1.0-alpha.2
```

Pair with [`Ranvier.Wgpu.Sdl`](https://www.nuget.org/packages/Ranvier.Wgpu.Sdl) to bind `WgpuContext` scenes directly onto `Ranvier.View.WebCore.Sdl` DOM elements with zero-copy VRAM texture compositing.

## Quick Start

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
```
