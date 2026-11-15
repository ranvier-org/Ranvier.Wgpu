namespace Ranvier.Wgpu

open System
open System.Numerics
open Fable.Core
open Fickl.Core

/// <summary>
/// Browser WebGPU 3D context drawing into a canvas, with 4x MSAA, runtime WGSL shaders, premultiplied sRGB
/// colour, hardware instancing, BIM section clipping, and 1x1 scissor GPU pixel picking.
/// </summary>
/// <remarks>
/// Create one with <c>WgpuContext.CreateAsync</c>: a browser hands out a device asynchronously. Picking and
/// WGSL validation are asynchronous for the same reason. The other members match the .NET package.
/// </remarks>
[<Sealed>]
type WgpuContext private (backend: ISceneBackend, staging: PackBuffer, initialWidth: int, initialHeight: int) =
    let mutable disposed = false
    let mutable curWidth = initialWidth
    let mutable curHeight = initialHeight
    let mutable renderCalls = 0
    let mutable framesDelivered = 0
    let sceneUniforms = JS.Constructors.Float32Array.Create Packing.SceneUniformFloats
    // A copy of the user uniform block, so one slot or the first few can be written from it.
    let userUniforms = JS.Constructors.Float32Array.Create Packing.UserUniformFloats

    let counters =
        {
            DrawCalls = 0
            Instances = 0
            Triangles = 0
        }

    let checkLive () =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuContext))

    // `None` is the stock shader, which has every entry point.
    static let planOf (customWgsl: string option) : SceneEntryPlan option =
        match customWgsl with
        | None -> None
        | Some wgsl ->
            if isNull wgsl then
                nullArg (nameof customWgsl)

            match Shaders.planSceneEntryPoints wgsl with
            | Ok plan -> Some plan
            | Error reason -> raise (WgslException reason)

    static let buildLegacyScenePass
        (camera: Camera3D)
        (batches: seq<InstanceBatch>)
        (clearColor: RgbaColor option)
        (lighting: LightingRig option)
        (sectionPlane: SectionPlane option)
        (lineBatches: seq<LineBatch> option)
        (discBatches: seq<DiscBatch> option)
        : ScenePass =
        {
            Camera = camera
            ClearColor =
                match clearColor with
                | Some c -> ValueSome c
                | None -> ValueNone
            Lighting =
                match lighting with
                | Some l -> ValueSome l
                | None -> ValueNone
            SectionPlane =
                match sectionPlane with
                | Some s -> ValueSome s
                | None -> ValueNone
            Batches =
                match batches with
                | :? (InstanceBatch array) as arr -> ReadOnlyMemory<InstanceBatch>(arr)
                | _ -> ReadOnlyMemory<InstanceBatch>(Seq.toArray batches)
            LineBatches =
                match lineBatches with
                | None -> ReadOnlyMemory.Empty
                | Some (:? (LineBatch array) as arr) -> ReadOnlyMemory<LineBatch>(arr)
                | Some lines -> ReadOnlyMemory<LineBatch>(Seq.toArray lines)
            DiscBatches =
                match discBatches with
                | None -> ReadOnlyMemory.Empty
                | Some (:? (DiscBatch array) as arr) -> ReadOnlyMemory<DiscBatch>(arr)
                | Some discs -> ReadOnlyMemory<DiscBatch>(Seq.toArray discs)
        }

    /// <summary>
    /// Creates a context that draws into <c>canvas</c>, an <c>HTMLCanvasElement</c> or an <c>OffscreenCanvas</c>.
    /// </summary>
    /// <param name="canvas">The canvas to draw into. Its <c>webgpu</c> context is configured by this call.</param>
    /// <param name="width">Target width in device pixels; the canvas is resized to it. Default: the canvas's width.</param>
    /// <param name="height">Target height in device pixels; the canvas is resized to it. Default: the canvas's height.</param>
    /// <param name="customWgsl">WGSL to use in place of the stock scene shader, such as the result of <c>Shaders.composeScene</c>.</param>
    /// <returns>
    /// A promise of the context. It rejects with a <see cref="T:Ranvier.Wgpu.WgslException"/> carrying the browser's
    /// message when the WGSL does not compile or does not fit the scene pipelines, and with an error when the page
    /// has no WebGPU or no adapter.
    /// </returns>
    static member CreateAsync(canvas: obj, ?width: int, ?height: int, ?customWgsl: string) : JS.Promise<WgpuContext> =
        jsAsync {
            // Decided from the text before a device is requested, so a shader that cannot work costs nothing to reject.
            let plan = planOf customWgsl
            let! backend = WebGpuBackend.createAsync canvas width height customWgsl plan
            let canvas: WebGpu.Canvas = unbox canvas
            return new WgpuContext (backend, Packing.create 65536, canvas.width, canvas.height)
        }

    /// <summary>
    /// Checks WGSL the way a context would use it, without a canvas: it compiles the text and builds every scene
    /// pipeline from it on a device of its own.
    /// </summary>
    /// <returns>
    /// A promise of <c>Ok</c> when a context can be created with the WGSL; otherwise of <c>Error</c> with the
    /// browser's compiler or pipeline validator message.
    /// </returns>
    /// <remarks>It rejects when the page has no WebGPU or no adapter.</remarks>
    static member ValidateWgslAsync(wgsl: string) : JS.Promise<Result<unit, string>> =
        jsAsync {
            if isNull wgsl then
                nullArg (nameof wgsl)

            match Shaders.planSceneEntryPoints wgsl with
            | Error reason -> return Error reason
            | Ok plan -> return! WebGpuBackend.validateAsync (Some wgsl) (Some plan)
        }

    /// <summary>The GPU adapter's name, or an empty string when the browser withholds it.</summary>
    member _.AdapterName = backend.AdapterName

    /// <summary>The graphics API in use: <c>WebGPU</c>.</summary>
    member _.BackendName = backend.BackendName

    /// <summary>Current target width in device pixels.</summary>
    member _.Width = curWidth

    /// <summary>Current target height in device pixels.</summary>
    member _.Height = curHeight

    /// <summary>Total scene render passes submitted on this context.</summary>
    member _.RenderCalls = renderCalls

    /// <summary>Total frames completed on this context.</summary>
    member _.FramesDelivered = framesDelivered

    /// <summary>True after `Dispose` has destroyed the device.</summary>
    member _.IsDisposed = disposed

    /// <summary>Resizes the canvas and the 4x-MSAA and GPU-picking targets when dimensions change.</summary>
    member _.Resize(width: int, height: int) =
        let w = max 1 width
        let h = max 1 height

        if w <> curWidth || h <> curHeight then
            curWidth <- w
            curHeight <- h
            backend.Resize (w, h)

    /// <summary>Uploads CPU `MeshData` into immutable GPU vertex and index buffers.</summary>
    member _.UploadMesh(mesh: MeshData) : MeshHandle =
        new MeshHandle (backend, staging, mesh)

    /// <summary>Creates a dynamic hardware-instanced draw batch bound to `mesh`.</summary>
    member _.CreateBatch(mesh: MeshHandle, ?blendMode: BlendMode, ?doubleSided: bool) : InstanceBatch =
        new InstanceBatch (backend, staging, mesh, defaultArg blendMode BlendMode.Opaque, defaultArg doubleSided false)

    /// <summary>Creates a dynamic 4x-MSAA 3D hairline batch.</summary>
    member _.CreateLineBatch() : LineBatch =
        new LineBatch (backend, staging)

    /// <summary>Creates a dynamic camera-facing SDF disc and verification-arc billboard batch.</summary>
    member _.CreateDiscBatch() : DiscBatch =
        new DiscBatch (backend, staging)

    /// <summary>
    /// Writes application data into the user uniform block, starting at slot 0. A shader reads it as
    /// <c>user.slots[i]</c> (<c>@group(0) @binding(1)</c>).
    /// </summary>
    /// <remarks>
    /// The block holds <c>Shaders.UserUniformSlots</c> values. Slots keep what was last written and start at
    /// zero. A write takes effect for every pass rendered after it.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">More values than the block has slots.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.SetUserUniforms(values: ReadOnlySpan<Vector4>) =
        checkLive ()

        if values.Length > Shaders.UserUniformSlots then
            invalidArg (nameof values) $"The user uniform block holds {Shaders.UserUniformSlots} values, got {values.Length}."

        if values.Length > 0 then
            Packing.userUniforms userUniforms values
            backend.WriteUserUniforms (userUniforms, 0, values.Length)

    /// <summary>Writes an array of application data into the user uniform block, starting at slot 0.</summary>
    /// <exception cref="T:System.ArgumentException">More values than the block has slots.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member this.SetUserUniforms(values: Vector4 array) =
        this.SetUserUniforms (ReadOnlySpan<Vector4>(values))

    /// <summary>Writes one slot of the user uniform block and leaves the others as they are.</summary>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The slot is outside the block.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.SetUserUniform(slot: int, value: Vector4) =
        checkLive ()

        if slot < 0 || slot >= Shaders.UserUniformSlots then
            raise (ArgumentOutOfRangeException (nameof slot, $"The user uniform block has slots 0 to {Shaders.UserUniformSlots - 1}."))

        Packing.userUniform userUniforms slot value
        backend.WriteUserUniforms (userUniforms, slot, 1)

    /// <summary>Draws a `ScenePass` into the canvas. The 4x-MSAA target resolves into the canvas texture.</summary>
    /// <returns>
    /// The frame's counters. <c>GpuRenderMs</c> is the time to encode and submit the pass, not the GPU's time to
    /// draw it, and <c>ReadbackMs</c> is zero: nothing is read back.
    /// </returns>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.Render(pass: ScenePass) : FrameMetrics =
        checkLive ()
        renderCalls <- renderCalls + 1

        let clear =
            match pass.ClearColor with
            | ValueSome c -> c
            | ValueNone -> RgbaColor.darkBackdrop

        let rig =
            match pass.Lighting with
            | ValueSome l -> l
            | ValueNone -> LightingRig.studioDefault

        let clip =
            match pass.SectionPlane with
            | ValueSome s -> s
            | ValueNone -> SectionPlane.disabled

        let t0 = Clock.now ()
        Packing.sceneUniforms sceneUniforms curWidth curHeight pass.Camera rig clip
        backend.WriteSceneUniforms sceneUniforms
        backend.DrawPass (clear, pass.Batches, pass.LineBatches, pass.DiscBatches, counters)
        let t1 = Clock.now ()
        framesDelivered <- framesDelivered + 1

        {
            GpuRenderMs = t1 - t0
            ReadbackMs = 0.0
            DrawCalls = counters.DrawCalls
            InstanceCount = counters.Instances
            TriangleCount = counters.Triangles
        }

    /// <summary>Draws `camera` and `batches` with optional scene settings into the canvas.</summary>
    member this.Render
        (
            camera: Camera3D,
            batches: seq<InstanceBatch>,
            ?clearColor: RgbaColor,
            ?lighting: LightingRig,
            ?sectionPlane: SectionPlane,
            ?lineBatches: seq<LineBatch>,
            ?discBatches: seq<DiscBatch>
        ) : FrameMetrics =
        this.Render (buildLegacyScenePass camera batches clearColor lighting sectionPlane lineBatches discBatches)

    /// <summary>
    /// Reads the last frame drawn as straight (not premultiplied) RGBA bytes, four per pixel, rows from the top:
    /// what <c>RgbaPixels</c> holds after <c>Render</c> in the .NET package.
    /// </summary>
    /// <remarks>
    /// This is asynchronous readback: the frame is resolved a second time into a texture, copied to a buffer and
    /// mapped, and the promise settles when the GPU has done so. It is meant for tests and screenshots. Drawing
    /// to the canvas needs no readback.
    /// </remarks>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.ReadPixelsAsync() : JS.Promise<byte array> =
        checkLive ()
        backend.ReadPixelsAsync ()

    /// <summary>
    /// Executes a 1x1-scissor hardware GPU picking pass at pixel `(px, py)` for `scene`. The promise gives the
    /// hit `PickId` and exact 3D `WorldPosition`, or `ValueNone`.
    /// </summary>
    /// <remarks>
    /// The pick pass is encoded before this returns, from the batches as they are then. Reading its one pixel
    /// back is asynchronous.
    /// </remarks>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.PickAsync(px: int, py: int, scene: ScenePass) : JS.Promise<PickHit voption> =
        checkLive ()

        if
            px < 0
            || px >= curWidth
            || py < 0
            || py >= curHeight
        then
            JS.Constructors.Promise.resolve ValueNone
        else
            let clip =
                match scene.SectionPlane with
                | ValueSome s -> s
                | ValueNone -> SectionPlane.disabled

            Packing.sceneUniforms sceneUniforms curWidth curHeight scene.Camera LightingRig.studioDefault clip
            backend.WriteSceneUniforms sceneUniforms
            backend.PickAsync (px, py, scene.Batches, scene.DiscBatches)

    /// <summary>Destroys the pipelines, buffers, textures and the device, and releases the canvas.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true
            backend.Dispose ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>Curried functional pipeline helpers for <see cref="T:Ranvier.Wgpu.WgpuContext"/>.</summary>
module WgpuContext =
    /// <summary>Creates a `WgpuContext` of `width` by `height` device pixels drawing into `canvas`.</summary>
    let inline createAsync (width: int) (height: int) (canvas: obj) : JS.Promise<WgpuContext> =
        WgpuContext.CreateAsync (canvas, width, height)

    /// <summary>Uploads `mesh` to `context`.</summary>
    let inline uploadMesh (mesh: MeshData) (context: WgpuContext) : MeshHandle =
        context.UploadMesh mesh

    /// <summary>Creates an opaque single-sided `InstanceBatch` for `mesh` on `context`.</summary>
    let inline createOpaqueBatch (mesh: MeshHandle) (context: WgpuContext) : InstanceBatch =
        context.CreateBatch (mesh, BlendMode.Opaque, false)

    /// <summary>Creates an `InstanceBatch` with explicit `blendMode` and `doubleSided` flags on `context`.</summary>
    let inline createBatch (blendMode: BlendMode) (doubleSided: bool) (mesh: MeshHandle) (context: WgpuContext) : InstanceBatch =
        context.CreateBatch (mesh, blendMode, doubleSided)

    /// <summary>Creates a 4x-MSAA `LineBatch` on `context`.</summary>
    let inline createLineBatch (context: WgpuContext) : LineBatch =
        context.CreateLineBatch ()

    /// <summary>Creates a camera-facing SDF `DiscBatch` on `context`.</summary>
    let inline createDiscBatch (context: WgpuContext) : DiscBatch =
        context.CreateDiscBatch ()

    /// <summary>Writes `values` into the user uniform block of `context`, starting at slot 0.</summary>
    let inline setUserUniforms (values: Vector4 array) (context: WgpuContext) : unit =
        context.SetUserUniforms values

    /// <summary>Checks `wgsl` without a canvas; `Error` carries the browser's message.</summary>
    let inline validateWgslAsync (wgsl: string) : JS.Promise<Result<unit, string>> =
        WgpuContext.ValidateWgslAsync wgsl

    /// <summary>Draws `pass` on `context` into its canvas.</summary>
    let inline render (context: WgpuContext) (pass: ScenePass) : FrameMetrics =
        context.Render pass

    /// <summary>Executes 1x1 GPU picking at `(px, py)` for `pass` on `context`.</summary>
    let inline pickAsync (px: int) (py: int) (context: WgpuContext) (pass: ScenePass) : JS.Promise<PickHit voption> =
        context.PickAsync (px, py, pass)

    /// <summary>Resizes `context` to `width` by `height` device pixels.</summary>
    let inline resize (width: int) (height: int) (context: WgpuContext) : unit =
        context.Resize (width, height)

    /// <summary>Disposes `context`.</summary>
    let inline dispose (context: WgpuContext) : unit =
        context.Dispose ()
