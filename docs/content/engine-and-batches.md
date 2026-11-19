---
title: Engine, batches & custom WGSL
description: Procedural meshes, instanced and billboard GPU batches, cameras, section planes, 1x1 GPU picking, and shader composition.
---

# Engine, batches & custom WGSL

`Ranvier.Wgpu` separates immutable value descriptions (`MeshData`, `Camera3D`, `LightingRig`, `SectionPlane`, `ScenePass`) from disposable GPU-resident buffers (`WgpuContext`, `MeshHandle`, `InstanceBatch`, `LineBatch`, `DiscBatch`). Per-frame rendering allocates zero managed heap objects when passing pre-allocated arrays or `ReadOnlyMemory<'T>` slices.

## Meshes and procedural primitives

A `MeshData` record holds `Vertices: Vertex3D array` (`Position`, `Normal`, `Color`) and `Indices: uint32 array`.

- **`Primitives`** builds unit-scale geometry centered at the origin:
  - `Primitives.unitBox ()` — `[-0.5, +0.5]^3` cube with sharp face normals
  - `Primitives.unitIcosphere subdivisions` — unit-radius sphere (`subdivisions` clamped to `0..4`)
  - `Primitives.unitCylinder segments` — capped cylinder along `Y` (`radius = 0.5`, `y` in `[-0.5, +0.5]`)
  - `Primitives.unitCone segments` — capped cone along `Y` (`base radius = 0.5` at `y = -0.5`, apex at `y = +0.5`)
  - `Primitives.unitTorus majorSegments minorSegments minorRatio` — XZ-plane torus
  - `Primitives.unitGableRoof ()` — pitched roof prism inside `[-0.5, +0.5]^3`
- **`MeshBuilder`** combines transformed meshes (`AddMesh`, automatically flipping triangle winding when a transform has negative determinant), smooth cylindrical polylines (`AddTube`), 4-sided 3D prism segments (`AddLineSegment`), and world XZ reference grids (`AddGridXZ`) into a single `MeshData` via `ToMeshData()`.

Upload a `MeshData` once with `WgpuContext.uploadMesh` (`ctx.UploadMesh`), and update vertices or topology in place when needed via `meshHandle.UpdateVertices`, `meshHandle.UpdateVertexRange`, or `meshHandle.UpdateMesh` (growing the underlying GPU buffers by doubling capacity automatically).

## Dynamic GPU batches

All three batch types wrap grow-on-demand GPU vertex/instance buffers supporting full replacement, prefix-preserving `Resize`, and sub-range `UpdateRange` over `ReadOnlySpan<'T>` or arrays:

### 1. `InstanceBatch` (`Instance3D`)

Created via `WgpuContext.createOpaqueBatch mesh ctx` or `WgpuContext.createBatch blendMode doubleSided mesh ctx` (`BlendMode.Opaque` or `BlendMode.AlphaBlend`). Each 96-byte `Instance3D` carries a 4×4 row-vector world matrix, RGB color + roughness, `PickId: uint32`, `Flags: uint32`, `Opacity: float32`, and `Emissive: float32`:

- `Instance3D.fromMatrix world color roughness pickId flags emissive`
- `Instance3D.identity color roughness pickId flags`
- `Instance3D.sphere center radius color roughness pickId flags`
- `Instance3D.trs translation yawDeg scale color roughness pickId flags`

Per-instance bitflags in `InstanceFlags` combine via bitwise `|||`:
- `InstanceFlags.None` (`0u`) — standard lit shading, clipped by active `SectionPlane`
- `InstanceFlags.Unlit` (`1u`) — unlit emissive color (gizmos, grids, filaments)
- `InstanceFlags.Selected` (`2u`) — selection rim and emissive highlight
- `InstanceFlags.IgnoreSectionPlane` (`4u`) — exempts the instance from section-plane clipping

Update instances with `batch.SetSingle(inst)`, `batch.UpdateInstances(spanOrArray)`, `batch.Resize(count)`, or `batch.UpdateRange(startIndex, spanOrArray)`.

### 2. `LineBatch` (`LineVertex3D`)

Created via `WgpuContext.createLineBatch ctx`. Pairs of consecutive 32-byte `LineVertex3D` endpoints `(2k, 2k+1)` render 4× MSAA alpha-blended hairlines. Passing `ignoreSectionPlane = true` to `LineVertex3D(pos, color, ignoreSectionPlane = true)` exempts the segment from `SectionPlane` clipping. Update with `lineBatch.UpdateVertices`, `lineBatch.Resize` (even vertex count), or `lineBatch.UpdateRange`.

### 3. `DiscBatch` (`DiscInstance3D`)

Created via `WgpuContext.createDiscBatch ctx`. Renders camera-facing anti-aliased SDF discs with optional clockwise verification-progress arcs and hollow portal rings (`DiscInstance3D.create worldPos radiusPx color verifiedFrac inProgressFrac arcThicknessPx portalHollow pickId flags`), participating in both visual rendering and 1×1 GPU picking.

## Cameras, lighting, section cuts, and `ScenePass`

- **`Camera3D`**: Right-handed perspective (`FovYDeg > 0`) or orthographic (`FovYDeg < 0`, encoding `-halfHeight`) camera:
  - `Camera3D.orbit target yawDeg pitchDeg distance fovYDeg`
  - `Camera3D.orthographic target yawDeg pitchDeg halfHeight distance`
  - `Camera3D.orbitPerspectiveness target yawDeg pitchDeg fitRadius perspectiveness` — dolly-zoom transition between orthographic (`0.0f`) and perspective (`1.0f`)
  - `Camera3D.projectToScreen width height cam worldPos` (`Vector2 voption`) and `Camera3D.screenRay width height cam px py` (`struct (origin, unitDirection)`)
- **`LightingRig`**: Hemisphere sky/ground + normalized key/rim directional lights and section-cut contour highlight (`LightingRig.studioDefault`).
- **`SectionPlane`**: World-space half-space clip (`SectionPlane.disabled` or `SectionPlane.create normal offset`, discarding fragments where `dot(worldPos, Normal) + Offset < 0`).
- **`ScenePass`**: `[<Struct>]` pass value assembled with zero heap allocation:

```fsharp
let pass =
    ScenePass.create camera
    |> ScenePass.withClear RgbaColor.darkBackdrop
    |> ScenePass.withLighting LightingRig.studioDefault
    |> ScenePass.withSection (SectionPlane.create -Vector3.UnitY 1.2f)
    |> ScenePass.withBatches instanceBatches
    |> ScenePass.withLines lineBatches
    |> ScenePass.withDiscs discBatches

let metrics: FrameMetrics = pass |> WgpuContext.render ctx
```

`FrameMetrics` reports `GpuRenderMs`, `ReadbackMs`, `DrawCalls`, `InstanceCount`, and `TriangleCount`.

## 1×1 hardware GPU picking

`WgpuContext.pick px py ctx pass` (`ctx.Pick(px, py, pass)`) renders a 1×1 scissored `RGBA32Uint` pass across active `InstanceBatch` and `DiscBatch` draw calls (respecting active `SectionPlane` cuts) and reads back 16 bytes:

- Returns `ValueNone` when the pixel hits background or `PickId = 0u`.
- Returns `ValueSome { PickId: uint32; WorldPosition: Vector3 }` with the exact 3D world-space hit point on the picked surface or billboard.

## Custom WGSL shaders and headless validation

By default, `WgpuContext` compiles `Shaders.DefaultSceneWgsl`. Use `Shaders.composeScene` to splice custom WGSL into six typed `SceneShaderHook` extension points while keeping the stock line, disc, and picking pipelines intact:

| `SceneShaderHook` | Injection Point | Bindings & Variables in Scope |
| :--- | :--- | :--- |
| `Declarations` | Module scope before `vs_main` | `u` (`SceneUniforms`), `user` (`user.slots[0..15]`), `VsIn`, `VsOut` |
| `Varyings` | Extra fields on `VsOut` (`@location(5)` and up) | — |
| `VertexBegin` | Start of `vs_main` (may early-return `VsOut`) | `in` |
| `VertexEnd` | Before `return out;` in `vs_main` | `in`, `model`, `worldPos4`, `worldNormal`, `c0`, `c1`, `c2`, `out` |
| `FragmentBegin` | In `fs_main` after section clip (may `discard` or return premultiplied RGBA) | `in`, `isFront`, `flags` |
| `FragmentLit` | In `fs_main` after lighting, before section contour & tone mapping | `in`, `isFront`, `flags`, `alpha`, mutable `lit` |

```fsharp
open System.Numerics
open Ranvier.Wgpu

let wgsl =
    Shaders.composeScene [
        SceneShaderHook.Declarations,
        "fn stripes(p: vec3<f32>) -> f32 { return step(0.5, fract(p.y * 8.0 + user.slots[0].x)); }"
        SceneShaderHook.Varyings, "    @location(5) localPos: vec3<f32>,"
        SceneShaderHook.VertexEnd, "    out.localPos = in.localPos;"
        SceneShaderHook.FragmentLit, "    lit = lit * mix(0.6, 1.0, stripes(in.localPos));"
    ]

// Validate WGSL in unit tests without opening a window:
match WgpuContext.validateWgsl wgsl with
| Ok () -> ()
| Error compilerMessage -> failwith compilerMessage

use ctx = new WgpuContext(1280, 720, customWgsl = wgsl)
ctx.SetUserUniform(0, Vector4(phase, 0.0f, 0.0f, 0.0f))
```

Up to 16 `Vector4` (`Shaders.UserUniformSlots`, 256 bytes) application uniforms live at `@group(0) @binding(1)` via `ctx.SetUserUniform(slot, value)` or `WgpuContext.setUserUniforms values ctx`. Invalid WGSL or pipeline layout mismatches throw `WgslException` with the WebGPU compiler message.
