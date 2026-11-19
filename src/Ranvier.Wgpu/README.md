# Ranvier.Wgpu

NativeAOT-compatible F# 3D and WebGPU (`webgpu.h` / `wgpu-native`) engine with runtime WGSL shaders, 4× MSAA, hardware instancing (`MeshBatch`, `LineBatch`, `DiscBatch`), live BIM section clipping, premultiplied sRGB blending, and 1×1 `RGBA32Uint` GPU pixel picking.

## Install

```sh
dotnet add package Ranvier.Wgpu --version 0.3.1
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

## Requirements

- .NET 10 (`net10.0`).
- `FSharp.Core` 10.0.100 or later, which ships with the first .NET 10 SDK, so any .NET 10 SDK builds against
  this package. Versions up to 0.2.0 required `FSharp.Core` 10.1.401 (.NET SDK 10.0.401).

## Current limits

These have held since 0.1.0.

- **No textures.** A shader gets two uniform blocks and the vertex and instance attributes. There is no texture,
  sampler or storage binding, so surface detail is procedural or per vertex.
- **Lines are a line list.** A `LineBatch` draws independent one-pixel segments from vertex pairs `(2k, 2k+1)`.
  A polyline of `n` points is `n - 1` pairs, and a line wider than one pixel is drawn by the caller as quads.
- **Discs are not depth-sorted.** Disc batches are drawn last, in the order given, tested against the depth of
  opaque geometry and writing none. Sort overlapping translucent discs far to near. The same holds for
  `AlphaBlend` instance batches.
- **Draw order in a pass** is fixed: opaque instance batches, line batches, alpha-blended instance batches, disc
  batches.

A batch with nothing in it is skipped, so a pass may hold batches that are empty on some frames.

## Custom shading

A context draws with `Shaders.DefaultSceneWgsl` unless it is given WGSL of its own. A context created with WGSL
that does not compile, or that does not fit the pipelines, throws `WgslException` with the compiler's message.

### Extension points of the stock shader

`Shaders.composeScene` inserts WGSL at named places in the stock shader and returns the text for `customWgsl`.
Line and disc batches keep drawing with the stock line and disc shaders.

| Hook | Where the code goes | Names in scope |
| :--- | :--- | :--- |
| `Declarations` | Module scope, before `vs_main` | `u`, `user`, `VsIn`, `VsOut` |
| `Varyings` | Extra members of `VsOut`, from `@location(5)` up | |
| `VertexBegin` | First statements of `vs_main`; may return a `VsOut` | `in` |
| `VertexEnd` | Before `return out;` in `vs_main` | `in`, `model`, `worldPos4`, `worldNormal`, `c0`, `c1`, `c2`, `out` |
| `FragmentBegin` | In `fs_main`, after section clipping; may return a premultiplied colour or `discard` | `in`, `isFront`, `flags` |
| `FragmentLit` | In `fs_main`, after lighting, before the section contour and tone mapping | the above, `alpha`, mutable `lit` |

```fsharp
let wgsl =
    Shaders.composeScene [
        SceneShaderHook.Declarations,
        "fn stripes(p: vec3<f32>) -> f32 { return step(0.5, fract(p.y * 8.0 + user.slots[0].x)); }"
        SceneShaderHook.Varyings, "    @location(5) localPos: vec3<f32>,"
        SceneShaderHook.VertexEnd, "    out.localPos = in.localPos;"
        SceneShaderHook.FragmentLit, "    lit = lit * mix(0.6, 1.0, stripes(in.localPos));"
    ]

use ctx = new WgpuContext(1280, 720, customWgsl = wgsl)
ctx.SetUserUniform(0, Vector4(phase, 0.0f, 0.0f, 0.0f))
```

### User uniforms

`@group(0) @binding(1)` is a block of `Shaders.UserUniformSlots` (16) `vec4<f32>` values for the application's
own data. The stock shader declares it as `user.slots`. `ctx.SetUserUniforms [| ... |]` writes from slot 0,
`ctx.SetUserUniform(slot, value)` writes one slot, and every slot is zero until written. A write applies to
every pass rendered after it.

`LightingRig.KeyLightDir` and `RimLightDir` are normalised on upload, so they cannot carry other data.

### Writing the whole shader

WGSL written from nothing needs `vs_main` and `fs_main`. The other entry points are optional, and the stock
shader supplies the ones left out:

| Entry points | Used for | When left out |
| :--- | :--- | :--- |
| `vs_main`, `fs_main` | Instance batches | Context creation fails and names the missing one |
| `fs_pick` | Picking instance batches | Stock `vs_main` and `fs_pick` |
| `vs_line`, `fs_line` | Line batches | Stock line shader. Define both or neither |
| `vs_disc`, `fs_disc` | Disc batches | Stock disc shader. Define both or neither |
| `fs_disc_pick` | Picking disc batches | Stock `vs_disc` and `fs_disc_pick`. Needs `vs_disc` and `fs_disc` when defined |

Binding 0 of group 0 is `SceneUniforms`, laid out as in the stock shader. Binding 1 is the user block: declare it
with any struct of up to 16 `vec4<f32>` (256 bytes).

### Checking a shader without a window

`dotnet build` does not compile WGSL. `WgpuContext.ValidateWgsl` does, on an offscreen device, and returns the
compiler's message:

```fsharp
match WgpuContext.ValidateWgsl wgsl with
| Ok () -> ()
| Error message -> failwith message
```

Call it from a test so that a shader error fails the test run rather than the first frame of the app.
