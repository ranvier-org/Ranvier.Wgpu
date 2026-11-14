# Ranvier.Wgpu.Sdl

Opt-in zero-copy GPU texture bridge binding [`Ranvier.Wgpu`](https://www.nuget.org/packages/Ranvier.Wgpu) 3D scenes directly onto [`Ranvier.View.WebCore.Sdl`](https://www.nuget.org/packages/Ranvier.View.WebCore.Sdl) DOM elements with natural HTML, CSS, and SVG overlay compositing.

## Install

```sh
dotnet add package Ranvier.Wgpu.Sdl --version 0.1.0
```

Use `Ranvier.Wgpu.Sdl.Traced` when building against `Ranvier.View.WebCore.Sdl.Traced`.

## Quick Start

```fsharp
open System
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Sdl

use window = Window.Create("Ranvier + Wgpu", 1280, 800)
let viewport = window.Host.Document.getElementById "viewport"

use wgpu = window |> WgpuSdlSurface.createContext
use subscription =
    viewport
    |> WgpuSdlSurface.lend window wgpu (fun surface ->
        let pass = ScenePass.create camera |> ScenePass.withBatches [| batch |]
        pass |> WgpuSdlSurface.render surface |> ignore
        { new IDisposable with member _.Dispose() = () })
```
