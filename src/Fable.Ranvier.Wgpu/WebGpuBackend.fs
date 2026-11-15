namespace Ranvier.Wgpu

open System
open System.Numerics
open Fable.Core
open Fickl.Core
open Ranvier.Wgpu.WebGpu

/// <summary>The browser WebGPU implementation of the backend seam.</summary>
/// <remarks>
/// Pipeline states, vertex layouts, draw order and the pick pass are those of the .NET package's
/// <c>WgpuContext</c>. The 4x MSAA colour target resolves into the canvas texture, so a frame needs no copy.
/// </remarks>
module internal WebGpuBackend =

    [<NoEquality; NoComparison>]
    type private Resources =
        {
            OpaqueCull: RenderPipeline
            OpaqueDouble: RenderPipeline
            BlendCull: RenderPipeline
            BlendDouble: RenderPipeline
            PickCull: RenderPipeline
            PickDouble: RenderPipeline
            Line: RenderPipeline
            Disc: RenderPipeline
            DiscPick: RenderPipeline
            BindGroup: BindGroup
            SceneUniforms: Buffer
            UserUniforms: Buffer
        }

    [<Literal>]
    let private SceneUniformBytes = 176

    [<Literal>]
    let private UserUniformBytes = 256

    [<Literal>]
    let private VertexUsage = BufferUsage.Vertex ||| BufferUsage.CopyDst

    [<Literal>]
    let private IndexUsage = BufferUsage.Index ||| BufferUsage.CopyDst

    [<Literal>]
    let private UniformUsage = BufferUsage.Uniform ||| BufferUsage.CopyDst

    [<Literal>]
    let private ReadbackUsage = BufferUsage.MapRead ||| BufferUsage.CopyDst

    [<Literal>]
    let private ReadableTargetUsage =
        TextureUsage.RenderAttachment
        ||| TextureUsage.CopySrc

    // The one use of the stock shader's text in this file: a literal is copied to each place that names it.
    let private stockWgsl = Shaders.DefaultSceneWgsl

    let private attribute (format: VertexFormat) (offset: int) (location: int) : VertexAttribute =
        {|
            format = format
            offset = offset
            shaderLocation = location
        |}

    let private meshLayouts: VertexBufferLayout array =
        [|
            {|
                arrayStride = Packing.VertexBytes
                stepMode = StepMode.Vertex
                attributes =
                    [|
                        attribute VertexFormat.Float32x3 0 0
                        attribute VertexFormat.Float32x3 12 1
                        attribute VertexFormat.Float32x4 24 2
                    |]
            |}
            {|
                arrayStride = Packing.InstanceBytes
                stepMode = StepMode.Instance
                attributes =
                    [|
                        attribute VertexFormat.Float32x4 0 3
                        attribute VertexFormat.Float32x4 16 4
                        attribute VertexFormat.Float32x4 32 5
                        attribute VertexFormat.Float32x4 48 6
                        attribute VertexFormat.Float32x4 64 7
                        attribute VertexFormat.Uint32x2 80 8
                        attribute VertexFormat.Float32x2 88 9
                    |]
            |}
        |]

    let private lineLayouts: VertexBufferLayout array =
        [|
            {|
                arrayStride = Packing.LineVertexBytes
                stepMode = StepMode.Vertex
                attributes =
                    [|
                        attribute VertexFormat.Float32x4 0 0
                        attribute VertexFormat.Float32x4 16 1
                    |]
            |}
        |]

    let private discLayouts: VertexBufferLayout array =
        [|
            {|
                arrayStride = Packing.DiscBytes
                stepMode = StepMode.Instance
                attributes =
                    [|
                        attribute VertexFormat.Float32x4 0 0
                        attribute VertexFormat.Float32x4 16 1
                        attribute VertexFormat.Float32x4 32 2
                        attribute VertexFormat.Uint32x2 48 3
                        attribute VertexFormat.Float32x2 56 4
                    |]
            |}
        |]

    // Premultiplied alpha: rgb_out = src_rgb + dst_rgb * (1 - src_a).
    let private premultiplied: BlendComponent =
        {|
            operation = "add"
            srcFactor = "one"
            dstFactor = "one-minus-src-alpha"
        |}

    let private pipeline
        (device: Device)
        (layout: PipelineLayout)
        (shader: ShaderModule)
        (vertexEntry: string)
        (fragmentEntry: string)
        (buffers: VertexBufferLayout array)
        (topology: Topology)
        (cullMode: CullMode)
        (format: TextureFormat)
        (sampleCount: int)
        (alphaBlend: bool)
        (depthWrite: bool)
        (depthCompare: CompareFunction)
        : RenderPipeline =
        device.createRenderPipeline
            {|
                depthStencil =
                    {|
                        depthCompare = depthCompare
                        depthWriteEnabled = depthWrite
                        format = TextureFormat.Depth32Float
                    |}
                fragment =
                    {|
                        entryPoint = fragmentEntry
                        ``module`` = shader
                        targets =
                            [|
                                {|
                                    blend =
                                        if alphaBlend then
                                            Some
                                                {|
                                                    alpha = premultiplied
                                                    color = premultiplied
                                                |}
                                        else
                                            None
                                    format = format
                                |}
                            |]
                    |}
                layout = layout
                multisample = {| count = sampleCount |}
                primitive =
                    {|
                        cullMode = cullMode
                        frontFace = "ccw"
                        topology = topology
                    |}
                vertex =
                    {|
                        buffers = buffers
                        entryPoint = vertexEntry
                        ``module`` = shader
                    |}
            |}

    /// <summary>
    /// Compiles the WGSL and builds every scene pipeline from it, each step in a validation error scope.
    /// A failure rejects with a <c>WgslException</c> that carries the browser's message.
    /// </summary>
    let private buildAsync
        (device: Device)
        (format: TextureFormat)
        (customWgsl: string option)
        (plan: SceneEntryPlan option)
        : JS.Promise<Resources> =
        jsAsync {
            device.pushErrorScope "validation"

            let shader =
                device.createShaderModule
                    {|
                        code = defaultArg customWgsl stockWgsl
                    |}

            let! compileError = device.popErrorScope ()

            match compileError with
            | Some error -> raise (WgslException $"WGSL failed to compile. {error.message}")
            | None -> ()

            // The stock shader supplies the entry points that custom WGSL leaves out, so line, disc and picking
            // pipelines exist on every context.
            let customPick, customLines, customDiscs, customDiscPick =
                match plan with
                | Some plan -> plan.CustomPick, plan.CustomLines, plan.CustomDiscs, plan.CustomDiscPick
                | None -> true, true, true, true

            let stock =
                if
                    customPick
                    && customLines
                    && customDiscs
                    && customDiscPick
                then
                    shader
                else
                    device.createShaderModule {| code = stockWgsl |}

            let pickShader = if customPick then shader else stock
            let lineShader = if customLines then shader else stock
            let discShader = if customDiscs then shader else stock
            let discPickShader = if customDiscPick then shader else stock

            let sceneUniforms =
                device.createBuffer
                    {|
                        size = SceneUniformBytes
                        usage = UniformUsage
                    |}

            let userUniforms =
                device.createBuffer
                    {|
                        size = UserUniformBytes
                        usage = UniformUsage
                    |}

            let bindGroupLayout =
                device.createBindGroupLayout
                    {|
                        entries =
                            [|
                                {|
                                    binding = 0
                                    visibility = ShaderStage.VertexAndFragment
                                    buffer =
                                        {|
                                            ``type`` = "uniform"
                                            minBindingSize = SceneUniformBytes
                                        |}
                                |}
                                {|
                                    binding = 1
                                    visibility = ShaderStage.VertexAndFragment
                                    buffer =
                                        {|
                                            ``type`` = "uniform"
                                            minBindingSize = UserUniformBytes
                                        |}
                                |}
                            |]
                    |}

            let bindGroup =
                device.createBindGroup
                    {|
                        layout = bindGroupLayout
                        entries =
                            [|
                                {|
                                    binding = 0
                                    resource =
                                        {|
                                            buffer = sceneUniforms
                                            offset = 0
                                            size = SceneUniformBytes
                                        |}
                                |}
                                {|
                                    binding = 1
                                    resource =
                                        {|
                                            buffer = userUniforms
                                            offset = 0
                                            size = UserUniformBytes
                                        |}
                                |}
                            |]
                    |}

            let layout =
                device.createPipelineLayout
                    {|
                        bindGroupLayouts = [| bindGroupLayout |]
                    |}

            let scene
                (shader: ShaderModule)
                (fragmentEntry: string)
                (format: TextureFormat)
                (samples: int)
                (cull: bool)
                (blend: bool)
                (depthWrite: bool)
                =
                pipeline
                    device
                    layout
                    shader
                    "vs_main"
                    fragmentEntry
                    meshLayouts
                    Topology.TriangleList
                    (if cull then CullMode.Back else CullMode.None)
                    format
                    samples
                    blend
                    depthWrite
                    CompareFunction.Less

            let disc (shader: ShaderModule) (fragmentEntry: string) (format: TextureFormat) (samples: int) (blend: bool) (depthWrite: bool) =
                pipeline
                    device
                    layout
                    shader
                    "vs_disc"
                    fragmentEntry
                    discLayouts
                    Topology.TriangleList
                    CullMode.None
                    format
                    samples
                    blend
                    depthWrite
                    (if depthWrite then
                         CompareFunction.Less
                     else
                         CompareFunction.LessEqual)

            // A shader that compiles can still fail to fit a pipeline: an entry point without its stage
            // attribute, a vertex input of the wrong type, a binding larger than the buffer behind it.
            device.pushErrorScope "validation"

            let resources =
                {
                    OpaqueCull = scene shader "fs_main" format 4 true false true
                    OpaqueDouble = scene shader "fs_main" format 4 false false true
                    BlendCull = scene shader "fs_main" format 4 true true false
                    BlendDouble = scene shader "fs_main" format 4 false true false
                    PickCull = scene pickShader "fs_pick" TextureFormat.Rgba32Uint 1 true false true
                    PickDouble = scene pickShader "fs_pick" TextureFormat.Rgba32Uint 1 false false true
                    Line =
                        pipeline
                            device
                            layout
                            lineShader
                            "vs_line"
                            "fs_line"
                            lineLayouts
                            Topology.LineList
                            CullMode.None
                            format
                            4
                            true
                            false
                            CompareFunction.LessEqual
                    Disc = disc discShader "fs_disc" format 4 true false
                    DiscPick = disc discPickShader "fs_disc_pick" TextureFormat.Rgba32Uint 1 false true
                    BindGroup = bindGroup
                    SceneUniforms = sceneUniforms
                    UserUniforms = userUniforms
                }

            let! pipelineError = device.popErrorScope ()

            match pipelineError with
            | Some error -> raise (WgslException $"WGSL does not fit the scene pipelines. {error.message}")
            | None -> ()

            return resources
        }

    let private requestAdapterAsync (gpu: Gpu) : JS.Promise<Adapter option> =
        jsAsync {
            let! preferred =
                gpu.requestAdapter
                    {|
                        powerPreference = "high-performance"
                    |}

            match preferred with
            | Some _ -> return preferred
            | None -> return! gpu.requestAdapter {| forceFallbackAdapter = true |}
        }

    let private requestDeviceAsync () : JS.Promise<Gpu * Adapter * Device> =
        jsAsync {
            match gpu () with
            | None -> return failwith "WgpuContext failed to initialize WebGPU: navigator.gpu is not available on this page."
            | Some gpu ->
                let! adapter = requestAdapterAsync gpu

                match adapter with
                | None -> return failwith "WgpuContext failed to initialize WebGPU: the browser returned no adapter."
                | Some adapter ->
                    let! device = adapter.requestDevice ()
                    return gpu, adapter, device
        }

    /// <summary>
    /// Draws the batches of one blend mode. Returns the pipeline left set on the pass, so the caller sets a
    /// pipeline only when it changes.
    /// </summary>
    let private drawBatches<'Batch when 'Batch :> IInstanceDraw>
        (pass: RenderPass)
        (batches: ReadOnlySpan<'Batch>)
        (alphaBlend: bool)
        (cullPipeline: RenderPipeline)
        (doublePipeline: RenderPipeline)
        (current: RenderPipeline)
        (counters: PassCounters)
        : RenderPipeline =
        let mutable last = current

        for i = 0 to batches.Length - 1 do
            let batch = batches[i] :> IInstanceDraw
            let count = batch.Count
            let mesh = batch.Mesh
            let indexCount = mesh.IndexCount

            if
                batch.AlphaBlend = alphaBlend
                && count > 0
                && indexCount > 0
            then
                let pipeline = if batch.DoubleSided then doublePipeline else cullPipeline

                if not (obj.ReferenceEquals (pipeline, last)) then
                    pass.setPipeline pipeline
                    last <- pipeline

                pass.setVertexBuffer (0, unbox mesh.VertexBuffer, 0, mesh.VertexBytes)
                pass.setVertexBuffer (1, unbox batch.InstanceBuffer, 0, batch.ActiveBytes)
                pass.setIndexBuffer (unbox mesh.IndexBuffer, "uint32", 0, mesh.IndexBytes)
                pass.drawIndexed (indexCount, count)
                counters.DrawCalls <- counters.DrawCalls + 1
                counters.Instances <- counters.Instances + count
                counters.Triangles <- counters.Triangles + mesh.TriangleCount * count

        last

    /// <summary>
    /// Copies mapped rows of <c>stride</c> bytes into tight straight-alpha RGBA rows: the readback of the .NET
    /// package's <c>Render</c>.
    /// </summary>
    let private copyPixels (source: JS.Uint8Array) (stride: int) (width: int) (height: int) (isBgra: bool) : byte array =
        let pixels = JS.Constructors.Uint8Array.Create (width * height * 4)

        for y = 0 to height - 1 do
            let from = y * stride
            let into = y * width * 4

            for x = 0 to width - 1 do
                let s = from + x * 4
                let d = into + x * 4
                let mutable r = int source[s]
                let mutable g = int source[s + 1]
                let mutable b = int source[s + 2]
                let a = int source[s + 3]

                if isBgra then
                    let swap = r
                    r <- b
                    b <- swap

                if a > 0 && a < 255 then
                    let half = a >>> 1
                    r <- min 255 ((r * 255 + half) / a)
                    g <- min 255 ((g * 255 + half) / a)
                    b <- min 255 ((b * 255 + half) / a)

                pixels[d] <- byte r
                pixels[d + 1] <- byte g
                pixels[d + 2] <- byte b
                pixels[d + 3] <- byte a

        unbox pixels

    [<Sealed>]
    type private Backend
        (
            device: Device,
            adapterName: string,
            canvas: Canvas,
            surface: CanvasContext,
            format: TextureFormat,
            resources: Resources,
            initialWidth: int,
            initialHeight: int
        ) =
        let queue = device.queue
        let mutable width = initialWidth
        let mutable height = initialHeight
        let mutable msaaColor: Texture = null
        let mutable msaaDepth: Texture = null
        let mutable pickColor: Texture = null
        let mutable pickDepth: Texture = null
        // The 1x target that `ReadPixelsAsync` resolves the kept MSAA colour into; made on first use.
        let mutable readColor: Texture = null
        let mutable pickBusy = false

        // Descriptors of the calls made every frame are created once and changed in place.
        let clearValue = Color (0.0, 0.0, 0.0, 1.0)

        let colorAttachment =
            ColorAttachment (null, LoadOp.Clear, StoreOp.Store, clearValue = clearValue)

        let depthAttachment = DepthAttachment (null, 1.0, LoadOp.Clear, StoreOp.Discard)

        let passDescriptor = RenderPassDescriptor ([| colorAttachment |], depthAttachment)

        let pickColorAttachment = ColorAttachment (null, LoadOp.Clear, StoreOp.Store)
        let pickDepthAttachment = DepthAttachment (null, 1.0, LoadOp.Clear, StoreOp.Discard)

        let pickDescriptor =
            RenderPassDescriptor ([| pickColorAttachment |], pickDepthAttachment)

        let submitList: CommandBuffer array = [| null |]

        // Fixed 256-byte staging buffer for the 1x1 pick readback.
        let pickBuffer = device.createBuffer {| size = 256; usage = ReadbackUsage |}

        let createTarget (format: TextureFormat) (sampleCount: int) (usage: int) =
            device.createTexture
                {|
                    size = {| width = width; height = height |}
                    format = format
                    sampleCount = sampleCount
                    usage = usage
                |}

        let destroy (texture: Texture) =
            if not (isNull texture) then
                texture.destroy ()

        let destroyTargets () =
            destroy msaaColor
            destroy msaaDepth
            destroy pickColor
            destroy pickDepth
            destroy readColor
            readColor <- null

        let allocateTargets () =
            destroyTargets ()
            msaaColor <- createTarget format 4 TextureUsage.RenderAttachment
            msaaDepth <- createTarget TextureFormat.Depth32Float 4 TextureUsage.RenderAttachment

            pickColor <- createTarget TextureFormat.Rgba32Uint 1 ReadableTargetUsage

            pickDepth <- createTarget TextureFormat.Depth32Float 1 TextureUsage.RenderAttachment
            colorAttachment.view <- msaaColor.createView ()
            depthAttachment.view <- msaaDepth.createView ()
            pickColorAttachment.view <- pickColor.createView ()
            pickDepthAttachment.view <- pickDepth.createView ()

        let submit (encoder: CommandEncoder) =
            submitList[0] <- encoder.finish ()
            queue.submit submitList
            submitList[0] <- null

        do allocateTargets ()

        interface ISceneBackend with
            member _.AdapterName = adapterName
            member _.BackendName = "WebGPU"

            member _.CreateVertexBuffer(byteLength) =
                unbox (
                    device.createBuffer
                        {|
                            size = byteLength
                            usage = VertexUsage
                        |}
                )

            member _.CreateIndexBuffer(byteLength) =
                unbox (
                    device.createBuffer
                        {|
                            size = byteLength
                            usage = IndexUsage
                        |}
                )

            member _.WriteBuffer(buffer, data, byteLength) =
                queue.writeBuffer (unbox buffer, 0, data, 0, byteLength)

            member _.WriteIndices(buffer, indices) =
                queue.writeBuffer (unbox buffer, 0, indices, 0, indices.Length)

            member _.DestroyBuffer(buffer) =
                (unbox<Buffer> buffer).destroy()

            member _.WriteSceneUniforms(data) =
                queue.writeBuffer (resources.SceneUniforms, 0, data, 0, Packing.SceneUniformFloats)

            member _.WriteUserUniforms(data, firstSlot, slotCount) =
                queue.writeBuffer (resources.UserUniforms, firstSlot * 16, data, firstSlot * 4, slotCount * 4)

            member _.DrawPass(clear, batches, lines, discs, counters) =
                let alpha = float (Math.Clamp (clear.A, 0.0f, 1.0f))
                clearValue.r <- float clear.R * alpha
                clearValue.g <- float clear.G * alpha
                clearValue.b <- float clear.B * alpha
                clearValue.a <- alpha
                colorAttachment.resolveTarget <- Some (surface.getCurrentTexture().createView())

                let encoder = device.createCommandEncoder ()
                let pass = encoder.beginRenderPass passDescriptor
                pass.setBindGroup (0, resources.BindGroup)
                counters.DrawCalls <- 0
                counters.Instances <- 0
                counters.Triangles <- 0

                let batchSpan = batches.Span
                let lineSpan = lines.Span
                let discSpan = discs.Span
                let linePipeline = resources.Line
                let discPipeline = resources.Disc

                let mutable last =
                    drawBatches pass batchSpan false resources.OpaqueCull resources.OpaqueDouble null counters

                for i = 0 to lineSpan.Length - 1 do
                    let line = lineSpan[i] :> ILineDraw
                    let vertexCount = line.VertexCount

                    if vertexCount >= 2 then
                        if not (obj.ReferenceEquals (last, linePipeline)) then
                            pass.setPipeline linePipeline
                            last <- linePipeline

                        pass.setVertexBuffer (0, unbox line.VertexBuffer, 0, line.ActiveBytes)
                        pass.draw (vertexCount, 1)
                        counters.DrawCalls <- counters.DrawCalls + 1

                last <- drawBatches pass batchSpan true resources.BlendCull resources.BlendDouble last counters

                for i = 0 to discSpan.Length - 1 do
                    let disc = discSpan[i] :> IDiscDraw
                    let count = disc.Count

                    if count > 0 then
                        if not (obj.ReferenceEquals (last, discPipeline)) then
                            pass.setPipeline discPipeline
                            last <- discPipeline

                        pass.setVertexBuffer (0, unbox disc.InstanceBuffer, 0, disc.ActiveBytes)
                        pass.draw (6, count)
                        counters.DrawCalls <- counters.DrawCalls + 1
                        counters.Instances <- counters.Instances + count
                        counters.Triangles <- counters.Triangles + count * 2

                pass.``end`` ()
                submit encoder

            member _.PickAsync(px, py, batches, discs) =
                let encoder = device.createCommandEncoder ()
                let pass = encoder.beginRenderPass pickDescriptor
                pass.setScissorRect (px, py, 1, 1)
                pass.setBindGroup (0, resources.BindGroup)

                let mutable last: RenderPipeline = null
                let batchSpan = batches.Span

                for i = 0 to batchSpan.Length - 1 do
                    let batch = batchSpan[i] :> IInstanceDraw
                    let count = batch.Count
                    let mesh = batch.Mesh
                    let indexCount = mesh.IndexCount

                    if count > 0 && indexCount > 0 then
                        let pipeline =
                            if batch.DoubleSided then
                                resources.PickDouble
                            else
                                resources.PickCull

                        if not (obj.ReferenceEquals (pipeline, last)) then
                            pass.setPipeline pipeline
                            last <- pipeline

                        pass.setVertexBuffer (0, unbox mesh.VertexBuffer, 0, mesh.VertexBytes)
                        pass.setVertexBuffer (1, unbox batch.InstanceBuffer, 0, batch.ActiveBytes)
                        pass.setIndexBuffer (unbox mesh.IndexBuffer, "uint32", 0, mesh.IndexBytes)
                        pass.drawIndexed (indexCount, count)

                let discSpan = discs.Span

                for i = 0 to discSpan.Length - 1 do
                    let disc = discSpan[i] :> IDiscDraw
                    let count = disc.Count

                    if count > 0 then
                        if not (obj.ReferenceEquals (last, resources.DiscPick)) then
                            pass.setPipeline resources.DiscPick
                            last <- resources.DiscPick

                        pass.setVertexBuffer (0, unbox disc.InstanceBuffer, 0, disc.ActiveBytes)
                        pass.draw (6, count)

                pass.``end`` ()

                // A pick that starts while another still has the staging buffer mapped reads into its own buffer.
                let shared = not pickBusy

                let target =
                    if shared then
                        pickBusy <- true
                        pickBuffer
                    else
                        device.createBuffer {| size = 256; usage = ReadbackUsage |}

                encoder.copyTextureToBuffer (
                    {|
                        texture = pickColor
                        origin = {| x = px; y = py |}
                    |},
                    {|
                        buffer = target
                        bytesPerRow = 256
                        rowsPerImage = 1
                    |},
                    {| width = 1; height = 1 |}
                )

                submit encoder

                jsAsync {
                    try
                        do! target.mapAsync (MapModeRead, 0, 16)
                        let texel = target.getMappedRange (0, 16)
                        let pickId = (JS.Constructors.Uint32Array.Create texel)[3]

                        let hit =
                            if pickId = 0u then
                                ValueNone
                            else
                                let position = JS.Constructors.Float32Array.Create texel

                                ValueSome
                                    {
                                        PickId = pickId
                                        WorldPosition = Vector3 (position[0], position[1], position[2])
                                    }

                        target.unmap ()
                        return hit
                    finally
                        if shared then pickBusy <- false else target.destroy ()
                }

            member _.ReadPixelsAsync() =
                if isNull readColor then
                    readColor <- createTarget format 1 ReadableTargetUsage

                // The MSAA colour target keeps the last pass, so resolving it again gives that frame's pixels.
                let resolve =
                    RenderPassDescriptor (
                        [|
                            ColorAttachment (colorAttachment.view, LoadOp.Load, StoreOp.Store, resolveTarget = readColor.createView ())
                        |]
                    )

                let columns = width
                let rows = height
                let stride = (columns * 4 + 255) &&& ~~~255

                let staging =
                    device.createBuffer
                        {|
                            size = stride * rows
                            usage = ReadbackUsage
                        |}

                let encoder = device.createCommandEncoder ()
                (encoder.beginRenderPass resolve).``end``()

                encoder.copyTextureToBuffer (
                    {|
                        texture = readColor
                        origin = {| x = 0; y = 0 |}
                    |},
                    {|
                        buffer = staging
                        bytesPerRow = stride
                        rowsPerImage = rows
                    |},
                    {| width = columns; height = rows |}
                )

                submit encoder

                jsAsync {
                    try
                        do! staging.mapAsync (MapModeRead, 0, stride * rows)

                        let mapped =
                            JS.Constructors.Uint8Array.Create (staging.getMappedRange (0, stride * rows))

                        return copyPixels mapped stride columns rows (format = TextureFormat.Bgra8Unorm)
                    finally
                        staging.destroy ()
                }

            member _.Resize(newWidth, newHeight) =
                width <- newWidth
                height <- newHeight
                canvas.width <- newWidth
                canvas.height <- newHeight
                allocateTargets ()

            member _.Dispose() =
                destroyTargets ()
                pickBuffer.destroy ()
                resources.SceneUniforms.destroy ()
                resources.UserUniforms.destroy ()
                surface.unconfigure ()
                device.destroy ()

    /// <summary>
    /// Requests a device, builds the pipelines and binds the canvas. With no size given, the canvas keeps the
    /// size it has.
    /// </summary>
    let createAsync
        (canvas: obj)
        (width: int option)
        (height: int option)
        (customWgsl: string option)
        (plan: SceneEntryPlan option)
        : JS.Promise<ISceneBackend> =
        jsAsync {
            let canvas: Canvas = unbox canvas
            let! gpu, adapter, device = requestDeviceAsync ()

            try
                let format = gpu.getPreferredCanvasFormat ()
                let! resources = buildAsync device format customWgsl plan

                match canvas.getContext "webgpu" with
                | None -> return failwith "WgpuContext failed to initialize WebGPU: the canvas has no webgpu context."
                | Some surface ->
                    let targetWidth = max 1 (defaultArg width canvas.width)
                    let targetHeight = max 1 (defaultArg height canvas.height)
                    canvas.width <- targetWidth
                    canvas.height <- targetHeight

                    surface.configure
                        {|
                            device = device
                            format = format
                            alphaMode = "premultiplied"
                        |}

                    return new Backend (device, adapterName adapter, canvas, surface, format, resources, targetWidth, targetHeight) :> ISceneBackend
            with failure ->
                device.destroy ()
                return raise failure
        }

    /// <summary>Compiles the WGSL and builds every scene pipeline from it on a device of its own, with no canvas.</summary>
    let validateAsync (customWgsl: string option) (plan: SceneEntryPlan option) : JS.Promise<Result<unit, string>> =
        jsAsync {
            let! _, _, device = requestDeviceAsync ()

            try
                try
                    let! _ = buildAsync device TextureFormat.Rgba8Unorm customWgsl plan
                    return Ok ()
                with :? WgslException as failure ->
                    return Error failure.Message
            finally
                device.destroy ()
        }
