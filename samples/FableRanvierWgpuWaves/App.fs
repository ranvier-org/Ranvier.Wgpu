/// <summary>
/// A live browser scene on <c>Fable.Ranvier.Wgpu</c>: a field of instanced boxes that rise and fall as waves
/// pass through it, glass spheres riding the waves, hairlines, progress discs, a section plane that sweeps
/// across, an orbiting camera, and GPU picking under the pointer.
/// </summary>
module Ranvier.Wgpu.Samples.Waves

open System
open System.Numerics
open Fable.Core
open Fickl.Core
open Ranvier.Wgpu

[<Emit("requestAnimationFrame($0)")>]
let private requestFrame (callback: float -> unit) : unit = jsNative

[<Emit("document.getElementById($0)")>]
let private byId (id: string) : obj = jsNative

[<Emit("$0.textContent = $1")>]
let private setText (element: obj) (text: string) : unit = jsNative

[<Emit("Math.max(1, Math.round($0.clientWidth * devicePixelRatio))")>]
let private pixelWidth (canvas: obj) : int = jsNative

[<Emit("Math.max(1, Math.round($0.clientHeight * devicePixelRatio))")>]
let private pixelHeight (canvas: obj) : int = jsNative

/// <summary>Calls <c>handler</c> with the pointer's position in device pixels of the canvas.</summary>
[<Emit("$0.addEventListener('pointermove', e => { const r = $0.getBoundingClientRect(); $1((e.clientX - r.left) * devicePixelRatio, (e.clientY - r.top) * devicePixelRatio); })")>]
let private onPointerMove (canvas: obj) (handler: float -> float -> unit) : unit = jsNative

[<Emit("$0.addEventListener('pointerleave', () => $1())")>]
let private onPointerLeave (canvas: obj) (handler: unit -> unit) : unit = jsNative

[<Literal>]
let private Side = 24

[<Literal>]
let private Spacing = 0.62f

let private waveHeight (x: float32) (z: float32) (t: float32) : float32 =
    0.75f
    + 0.4f * MathF.Sin (x * 0.9f + t * 1.4f)
    + 0.3f * MathF.Cos (z * 0.7f - t * 1.1f)

/// <summary>The scene's batches and the state that changes between frames.</summary>
type private Scene(ctx: WgpuContext, canvas: obj, hud: obj) =
    let boxMesh = ctx.UploadMesh (Primitives.unitBox ())
    let sphereMesh = ctx.UploadMesh (Primitives.unitIcosphere 2)
    let boxes = ctx.CreateBatch boxMesh
    let glass = ctx.CreateBatch (sphereMesh, BlendMode.AlphaBlend, true)
    let lines = ctx.CreateLineBatch ()
    let discs = ctx.CreateDiscBatch ()

    // The arrays handed to the batches and to the pass are made once and refilled each frame.
    let boxInstances = Array.zeroCreate<Instance3D>(Side * Side)
    let glassInstances = Array.zeroCreate<Instance3D> 3
    let discInstances = Array.zeroCreate<DiscInstance3D> 3
    let meshBatches = [| boxes; glass |]
    let lineBatches = [| lines |]
    let discBatches = [| discs |]
    let half = float32 (Side - 1) * Spacing * 0.5f

    let mutable pointerX = -1
    let mutable pointerY = -1
    let mutable hovered = 0u
    let mutable picking = false
    let mutable frames = 0
    let mutable encodeMs = 0.0

    do
        let edge = half + 0.6f
        let color = RgbaColor (0.45f, 0.7f, 1.0f, 0.7f)

        lines.UpdateVertices
            [|
                LineVertex3D (Vector3 (-edge, 0.0f, -edge), color)
                LineVertex3D (Vector3 (edge, 0.0f, -edge), color)
                LineVertex3D (Vector3 (edge, 0.0f, -edge), color)
                LineVertex3D (Vector3 (edge, 0.0f, edge), color)
                LineVertex3D (Vector3 (edge, 0.0f, edge), color)
                LineVertex3D (Vector3 (-edge, 0.0f, edge), color)
                LineVertex3D (Vector3 (-edge, 0.0f, edge), color)
                LineVertex3D (Vector3 (-edge, 0.0f, -edge), color)
            |]

        onPointerMove canvas (fun x y ->
            pointerX <- int x
            pointerY <- int y)

        onPointerLeave canvas (fun () ->
            pointerX <- -1
            pointerY <- -1
            hovered <- 0u)

    // One pick at a time: the next starts when the last has resolved.
    member private _.Pick(pass: ScenePass) =
        picking <- true

        jsAsync {
            let! hit = ctx.PickAsync (pointerX, pointerY, pass)

            hovered <-
                match hit with
                | ValueSome hit -> hit.PickId
                | ValueNone -> 0u

            picking <- false
        }
        |> ignore

    /// <summary>Draws the scene as it is <c>milliseconds</c> after the page loaded.</summary>
    member this.Frame(milliseconds: float) : unit =
        let t = float32 (milliseconds / 1000.0)
        ctx.Resize (pixelWidth canvas, pixelHeight canvas)

        for row = 0 to Side - 1 do
            for column = 0 to Side - 1 do
                let index = row * Side + column
                let x = float32 column * Spacing - half
                let z = float32 row * Spacing - half
                let height = waveHeight x z t
                let pickId = uint32 (index + 1)
                let warmth = Math.Clamp ((height - 0.1f) / 1.3f, 0.0f, 1.0f)

                let world =
                    Matrix4x4.CreateScale (Vector3 (0.5f, height, 0.5f))
                    * Matrix4x4.CreateTranslation (Vector3 (x, height * 0.5f, z))

                let color =
                    RgbaColor (0.15f + 0.8f * warmth, 0.35f + 0.3f * warmth, 0.95f - 0.7f * warmth, 1.0f)

                let flags =
                    if pickId = hovered then
                        InstanceFlags.Selected
                    else
                        InstanceFlags.None

                boxInstances[index] <- Instance3D.fromMatrix world color 0.45f pickId flags 0.0f

        boxes.UpdateInstances boxInstances

        for i = 0 to glassInstances.Length - 1 do
            let angle = t * 0.5f + float32 i * 2.0943952f
            let x = 3.2f * MathF.Cos angle
            let z = 3.2f * MathF.Sin angle
            let y = waveHeight x z t + 0.9f
            let discId = uint32 (2000 + i)

            glassInstances[i] <-
                Instance3D.sphere (Vector3 (x, y, z)) 0.55f (RgbaColor (0.6f, 0.95f, 1.0f, 0.45f)) 0.15f (uint32 (1000 + i)) InstanceFlags.None

            discInstances[i] <-
                DiscInstance3D.create
                    (Vector3 (x, y + 1.1f, z))
                    11.0f
                    (RgbaColor (0.98f, 0.75f, 0.2f, 1.0f))
                    (0.5f + 0.5f * MathF.Sin (t * 0.6f + float32 i))
                    0.15f
                    2.5f
                    0.0f
                    discId
                    (if hovered = discId then
                         InstanceFlags.Selected
                     else
                         InstanceFlags.None)

        glass.UpdateInstances glassInstances
        discs.UpdateDiscs discInstances

        // The cut sweeps back and forth across the field and shows the contour colour where it passes.
        let section =
            SectionPlane.create
                (Vector3 (-1.0f, 0.0f, 0.0f))
                (half * (0.55f + 0.5f * MathF.Sin (t * 0.35f))
                 + 1.0f)

        let pass =
            ScenePass.create (Camera3D.orbit (Vector3 (0.0f, 0.6f, 0.0f)) (t * 9.0f) 28.0f 19.0f 42.0f)
            |> ScenePass.withSection section
            |> ScenePass.withBatches meshBatches
            |> ScenePass.withLines lineBatches
            |> ScenePass.withDiscs discBatches

        let metrics = ctx.Render pass
        encodeMs <- encodeMs + metrics.GpuRenderMs
        frames <- frames + 1

        if not picking && pointerX >= 0 then
            this.Pick pass

        if frames % 30 = 0 then
            let hover = if hovered = 0u then "nothing" else $"pick id {hovered}"
            let mean = (encodeMs / 30.0).ToString "F2"

            setText
                hud
                $"{ctx.BackendName} on {ctx.AdapterName} | {ctx.Width}x{ctx.Height} | {metrics.DrawCalls} draws, {metrics.InstanceCount} instances, {metrics.TriangleCount} triangles | encode and submit {mean} ms | frame {frames} | under the pointer: {hover}"

            encodeMs <- 0.0

/// <summary>Starts the scene on the canvas with id <c>scene</c>, or shows why it cannot start.</summary>
let start () : JS.Promise<unit> =
    jsAsync {
        let canvas = byId "scene"
        let hud = byId "hud"

        try
            let! ctx = WgpuContext.CreateAsync (canvas, pixelWidth canvas, pixelHeight canvas)
            let scene = Scene (ctx, canvas, hud)

            let rec loop (milliseconds: float) =
                scene.Frame milliseconds
                requestFrame loop

            requestFrame loop
        with failure ->
            setText hud failure.Message
    }

start () |> ignore
