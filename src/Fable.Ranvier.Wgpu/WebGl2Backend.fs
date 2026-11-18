namespace Ranvier.Wgpu

open System
open System.Collections.Generic
open System.Numerics
open Fable.Core
open Fickl.Core
open Ranvier.Wgpu.WebGl2

/// <summary>The browser WebGL2 implementation of the backend seam, for a browser without WebGPU.</summary>
/// <remarks>
/// <para>
/// It draws what <c>WebGpuBackend</c> draws: the same nine pipeline states, vertex layouts, uniform bytes, draw
/// order and pick pass, into 4x multisampled renderbuffers whose resolved frame is copied into the canvas.
/// The shaders are the GLSL ES 3.00 of <c>GlslShaders</c>.
/// </para>
/// <para>Where WebGL differs from WebGPU, and what is done about it:</para>
/// <list type="bullet">
/// <item><description>
/// Clip-space depth runs from <c>-w</c> to <c>w</c>, not from 0 to <c>w</c>. The <c>main</c> that wraps a
/// vertex entry point rewrites <c>gl_Position.z</c>, so the camera matrices and the shaders stay WebGPU's.
/// </description></item>
/// <item><description>
/// A framebuffer's first row is the bottom of the picture, where a WebGPU texture's is the top. The same
/// <c>main</c> negates <c>gl_Position.y</c>, so the targets hold the frame with the top row first, as
/// WebGPU's do. A pick then reads pixel <c>(px, py)</c> as it is given and a readback needs no reordering.
/// Two things follow. A front face winds clockwise in WebGL's terms. And the canvas is filled by a second
/// blit that turns the resolved frame over, because the blit that resolves the samples may not.
/// </description></item>
/// <item><description>
/// That second blit buys equal pixels. Chrome on Windows draws WebGL through Direct3D 11, whose first row is
/// also the top one, so with the targets turned over the rasteriser is given the triangles WebGPU gives it:
/// the same four sample positions in a pixel and the same interpolation. <c>browser.test.mjs</c> measures
/// the result against the .NET frame. Drawn the WebGL way up, edges and hairlines differed by one sample of
/// four, and picked positions in their last bits.
/// </description></item>
/// <item><description>
/// A vertex buffer has no layout of its own. A vertex array is made for a batch's buffer on its first draw
/// and kept with the buffer, so a draw binds one object.
/// </description></item>
/// <item><description>
/// Reading a pixel back blocks until the GPU has drawn it. A pick reads into a pixel pack buffer and waits
/// for a fence instead, so <c>PickAsync</c> does not stall the page.
/// </description></item>
/// </list>
/// </remarks>
module internal WebGl2Backend =

    [<Literal>]
    let private SceneUniformBytes = 176

    [<Literal>]
    let private UserUniformBytes = 256

    [<Literal>]
    let private SampleCount = 4

    // The one use of the stock shader's text in this file: a literal is copied to each place that names it.
    let private stockGlsl = GlslShaders.DefaultSceneGlsl

    /// <summary>A program with the fixed-function state it is drawn with: one of WebGPU's render pipelines.</summary>
    [<NoEquality; NoComparison>]
    type private Pipeline =
        {
            Program: Program
            Cull: bool
            AlphaBlend: bool
            DepthWrite: bool
            DepthCompare: int
        }

    [<NoEquality; NoComparison>]
    type private Resources =
        {
            OpaqueCull: Pipeline
            OpaqueDouble: Pipeline
            BlendCull: Pipeline
            BlendDouble: Pipeline
            PickCull: Pipeline
            PickDouble: Pipeline
            Line: Pipeline
            Disc: Pipeline
            DiscPick: Pipeline
            Programs: Program array
        }

    /// <summary>A buffer behind the seam, with the vertex array that draws from it.</summary>
    [<NoEquality; NoComparison>]
    type private GlBuffer =
        {
            Buffer: Buffer
            Target: int
            /// The vertex array of the batch this buffer belongs to, made on its first draw.
            mutable VertexArray: VertexArray
            /// For an instance buffer, the mesh vertex buffer its vertex array was made with.
            mutable VertexArrayMesh: IBackendBuffer
            /// For an instance buffer, the mesh index buffer its vertex array was made with.
            mutable VertexArrayIndices: IBackendBuffer
            /// Index in the backend's list of live buffers, or -1 once deleted.
            mutable Slot: int
        }

    /// <summary>
    /// The text a driver compiles for one entry point: the version, the default precisions, the entry point's
    /// macro and <c>VARYING</c>, the shader, then a <c>main</c> that calls the entry point.
    /// </summary>
    let private source (glsl: string) (entry: string) : string =
        let isVertex = entry.StartsWith "vs_"

        let main =
            if isVertex then
                // WebGPU keeps 0 <= z <= w and stores z / w as depth. WebGL keeps -w <= z <= w and stores
                // (z / w + 1) / 2. With z' = 2z - w both the kept volume and the stored depth are WebGPU's.
                // The y is negated so that row 0 of a target is the top of the frame, as in WebGPU.
                $"\nvoid main() {{\n    {entry}();\n    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;\n    gl_Position.y = -gl_Position.y;\n}}\n"
            elif entry.EndsWith "_pick" then
                $"\nlayout(location = 0) out highp uvec4 ranvierFragment;\nvoid main() {{\n    ranvierFragment = {entry}();\n}}\n"
            else
                $"\nlayout(location = 0) out highp vec4 ranvierFragment;\nvoid main() {{\n    ranvierFragment = {entry}();\n}}\n"

        let varying = if isVertex then "out" else "in"

        // `#line 1` makes the driver's log count lines from the start of the shader text, not of this prefix.
        $"#version 300 es\nprecision highp float;\nprecision highp int;\n#define {entry.ToUpperInvariant ()} 1\n#define VARYING {varying}\n#line 1\n{glsl}{main}"

    let private compile (gl: Context) (glsl: string) (entry: string) : Shader =
        let shader =
            gl.createShader (
                if entry.StartsWith "vs_" then
                    Gl.VertexShader
                else
                    Gl.FragmentShader
            )

        gl.shaderSource (shader, source glsl entry)
        gl.compileShader shader

        if gl.getShaderParameter (shader, Gl.CompileStatus) then
            shader
        else
            let log = gl.getShaderInfoLog shader
            gl.deleteShader shader
            raise (GlslException $"GLSL failed to compile. {entry}: {log}")

    let private baseType (glType: int) : string =
        if
            glType = Gl.UnsignedInt
            || (glType >= Gl.UnsignedIntVec2
                && glType <= Gl.UnsignedIntVec4)
        then
            "uint"
        elif
            glType = Gl.Int
            || (glType >= Gl.IntVec2 && glType <= Gl.IntVec4)
        then
            "int"
        else
            "float"

    /// <summary>
    /// Why a linked program cannot be drawn from the vertex buffers and uniform buffers of a context, or
    /// <c>None</c>. It also points the program's uniform blocks at those buffers.
    /// </summary>
    /// <remarks>
    /// WebGPU refuses such a shader when the pipeline is made. WebGL would draw it with an error for each
    /// draw, or with zeros, so the same things are looked for here.
    /// </remarks>
    let private misfit (gl: Context) (program: Program) (layouts: WebGpu.VertexBufferLayout array) : string option =
        let mutable reason = None
        let attributes: int = unbox (gl.getProgramParameter (program, Gl.ActiveAttributes))

        for i = 0 to attributes - 1 do
            let info = gl.getActiveAttrib (program, i)
            let location = gl.getAttribLocation (program, info.name)

            // A built-in such as gl_VertexID has no location.
            if location >= 0 && reason.IsNone then
                let mutable supplied = ""

                for layout in layouts do
                    for attribute in layout.attributes do
                        if attribute.shaderLocation = location then
                            supplied <-
                                if attribute.format = WebGpu.VertexFormat.Uint32x2 then
                                    "uint"
                                else
                                    "float"

                let declared = baseType info.``type``

                if supplied = "" then
                    reason <- Some $"The vertex input {info.name} is at location {location}, where the vertex buffers have no attribute."
                elif supplied <> declared then
                    reason <-
                        Some
                            $"The vertex input {info.name} at location {location} has the base type {declared}, and the vertex buffer holds {supplied} there."

        let blocks: int = unbox (gl.getProgramParameter (program, Gl.ActiveUniformBlocks))

        for i = 0 to blocks - 1 do
            let name = gl.getActiveUniformBlockName (program, i)

            let bytes = gl.getActiveUniformBlockParameter (program, i, Gl.UniformBlockDataSize)

            let binding, available =
                match name with
                | "SceneUniforms" -> 0, SceneUniformBytes
                | "UserUniforms" -> 1, UserUniformBytes
                | _ -> -1, 0

            if binding < 0 then
                if reason.IsNone then
                    reason <- Some $"The uniform block {name} has no buffer. A context provides the blocks SceneUniforms and UserUniforms."
            elif bytes > available then
                if reason.IsNone then
                    reason <- Some $"The uniform block {name} is {bytes} bytes, and the buffer behind it is {available} bytes."
            else
                gl.uniformBlockBinding (program, i, binding)

        let uniforms: int = unbox (gl.getProgramParameter (program, Gl.ActiveUniforms))

        if uniforms > 0 && reason.IsNone then
            let blockOf =
                gl.getActiveUniforms (program, Array.init uniforms id, Gl.UniformBlockIndex)

            for i = 0 to uniforms - 1 do
                if blockOf[i] < 0 && reason.IsNone then
                    let name = gl.getActiveUniform(program, i).name

                    reason <-
                        Some
                            $"The uniform {name} is outside a uniform block, so a context cannot set it. A context provides the blocks SceneUniforms and UserUniforms."

        reason

    let private link
        (gl: Context)
        (vertexEntry: string)
        (vertex: Shader)
        (fragmentEntry: string)
        (fragment: Shader)
        (layouts: WebGpu.VertexBufferLayout array)
        : Program =
        let program = gl.createProgram ()
        gl.attachShader (program, vertex)
        gl.attachShader (program, fragment)
        gl.linkProgram program

        let failure =
            if unbox<bool>(gl.getProgramParameter (program, Gl.LinkStatus)) then
                misfit gl program layouts
            else
                Some (gl.getProgramInfoLog program)

        match failure with
        | None -> program
        | Some reason ->
            gl.deleteProgram program
            raise (GlslException $"GLSL does not fit the scene pipelines. {vertexEntry} with {fragmentEntry}: {reason}")

    /// <summary>
    /// Compiles the GLSL and links every scene program from it. A failure raises a <c>GlslException</c> that
    /// carries the driver's log, and leaves no program behind.
    /// </summary>
    let private build (gl: Context) (customGlsl: string option) (plan: SceneEntryPlan option) : Resources =
        let glsl = defaultArg customGlsl stockGlsl
        let shaders = Dictionary<string, Shader>()
        let programs = ResizeArray<Program>()

        // The stock shader supplies the entry points that custom GLSL leaves out, so line, disc and picking
        // programs exist on every context.
        let customPick, customLines, customDiscs, customDiscPick =
            match plan with
            | Some plan -> plan.CustomPick, plan.CustomLines, plan.CustomDiscs, plan.CustomDiscPick
            | None -> true, true, true, true

        // An entry point shared by two programs, such as vs_main, is compiled once.
        let shader (custom: bool) (entry: string) : Shader =
            let key = (if custom then "custom " else "stock ") + entry

            match shaders.TryGetValue key with
            | true, shader -> shader
            | _ ->
                let shader = compile gl (if custom then glsl else stockGlsl) entry
                shaders[key] <- shader
                shader

        let program (custom: bool) (vertexEntry: string) (fragmentEntry: string) (layouts: WebGpu.VertexBufferLayout array) : Program =
            let program =
                link gl vertexEntry (shader custom vertexEntry) fragmentEntry (shader custom fragmentEntry) layouts

            programs.Add program
            program

        let pipeline (program: Program) (cull: bool) (alphaBlend: bool) (depthWrite: bool) (depthCompare: int) : Pipeline =
            {
                Program = program
                Cull = cull
                AlphaBlend = alphaBlend
                DepthWrite = depthWrite
                DepthCompare = depthCompare
            }

        try
            try
                let mesh = program true "vs_main" "fs_main" WebGpuBackend.meshLayouts
                let pick = program customPick "vs_main" "fs_pick" WebGpuBackend.meshLayouts
                let line = program customLines "vs_line" "fs_line" WebGpuBackend.lineLayouts
                let disc = program customDiscs "vs_disc" "fs_disc" WebGpuBackend.discLayouts

                let discPick =
                    program customDiscPick "vs_disc" "fs_disc_pick" WebGpuBackend.discLayouts

                // The states of the nine pipelines of `WebGpuBackend.buildAsync`, in its order.
                {
                    OpaqueCull = pipeline mesh true false true Gl.Less
                    OpaqueDouble = pipeline mesh false false true Gl.Less
                    BlendCull = pipeline mesh true true false Gl.Less
                    BlendDouble = pipeline mesh false true false Gl.Less
                    PickCull = pipeline pick true false true Gl.Less
                    PickDouble = pipeline pick false false true Gl.Less
                    Line = pipeline line false true false Gl.LessEqual
                    Disc = pipeline disc false true false Gl.LessEqual
                    DiscPick = pipeline discPick false false true Gl.Less
                    Programs = programs.ToArray ()
                }
            with failure ->
                for program in programs do
                    gl.deleteProgram program

                raise failure
        finally
            // A linked program keeps what it needs of its shaders.
            for shader in shaders.Values do
                gl.deleteShader shader

    let private apply (gl: Context) (pipeline: Pipeline) =
        gl.useProgram pipeline.Program

        if pipeline.Cull then
            gl.enable Gl.CullFace
        else
            gl.disable Gl.CullFace

        if pipeline.AlphaBlend then
            gl.enable Gl.Blend
        else
            gl.disable Gl.Blend

        gl.depthMask pipeline.DepthWrite
        gl.depthFunc pipeline.DepthCompare

    /// <summary>
    /// Describes the attributes of the bound vertex array from WebGPU's vertex layouts: <c>first</c> is the
    /// buffer of the first layout and <c>second</c> of the second.
    /// </summary>
    let private describe (gl: Context) (layouts: WebGpu.VertexBufferLayout array) (first: Buffer) (second: Buffer) =
        for i = 0 to layouts.Length - 1 do
            let layout = layouts[i]
            let stride = layout.arrayStride

            let divisor = if layout.stepMode = WebGpu.StepMode.Instance then 1 else 0

            gl.bindBuffer (Gl.ArrayBuffer, (if i = 0 then first else second))

            for attribute in layout.attributes do
                let location = attribute.shaderLocation
                let offset = attribute.offset
                gl.enableVertexAttribArray location

                match attribute.format with
                | WebGpu.VertexFormat.Float32x2 -> gl.vertexAttribPointer (location, 2, Gl.Float, false, stride, offset)
                | WebGpu.VertexFormat.Float32x3 -> gl.vertexAttribPointer (location, 3, Gl.Float, false, stride, offset)
                | WebGpu.VertexFormat.Float32x4 -> gl.vertexAttribPointer (location, 4, Gl.Float, false, stride, offset)
                // An integer attribute keeps its bits only through the integer pointer call.
                | WebGpu.VertexFormat.Uint32x2 -> gl.vertexAttribIPointer (location, 2, Gl.UnsignedInt, stride, offset)

                gl.vertexAttribDivisor (location, divisor)

    /// <summary>Binds the vertex array that draws <c>mesh</c> with the instances in <c>instances</c>.</summary>
    let private bindMesh (gl: Context) (mesh: IMeshDraw) (instances: GlBuffer) =
        let vertices = mesh.VertexBuffer
        let indices = mesh.IndexBuffer

        if
            isNull instances.VertexArray
            || not (obj.ReferenceEquals (instances.VertexArrayMesh, vertices))
            || not (obj.ReferenceEquals (instances.VertexArrayIndices, indices))
        then
            if not (isNull instances.VertexArray) then
                gl.deleteVertexArray instances.VertexArray

            let vertexArray = gl.createVertexArray ()
            gl.bindVertexArray vertexArray
            describe gl WebGpuBackend.meshLayouts (unbox<GlBuffer> vertices).Buffer instances.Buffer
            gl.bindBuffer (Gl.ElementArrayBuffer, (unbox<GlBuffer> indices).Buffer)
            instances.VertexArray <- vertexArray
            instances.VertexArrayMesh <- vertices
            instances.VertexArrayIndices <- indices
        else
            gl.bindVertexArray instances.VertexArray

    /// <summary>Binds the vertex array of a line or disc batch, whose one buffer is <c>buffer</c>.</summary>
    let private bindSingle (gl: Context) (layouts: WebGpu.VertexBufferLayout array) (buffer: GlBuffer) =
        if isNull buffer.VertexArray then
            let vertexArray = gl.createVertexArray ()
            gl.bindVertexArray vertexArray
            describe gl layouts buffer.Buffer null
            buffer.VertexArray <- vertexArray
        else
            gl.bindVertexArray buffer.VertexArray

    /// <summary>
    /// Draws the batches of one blend mode. Returns the pipeline left applied, so the caller applies a
    /// pipeline only when it changes.
    /// </summary>
    let private drawBatches<'Batch when 'Batch :> IInstanceDraw>
        (gl: Context)
        (batches: ReadOnlySpan<'Batch>)
        (alphaBlend: bool)
        (cullPipeline: Pipeline)
        (doublePipeline: Pipeline)
        (current: Pipeline)
        (counters: PassCounters)
        : Pipeline =
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
                    apply gl pipeline
                    last <- pipeline

                bindMesh gl mesh (unbox batch.InstanceBuffer)
                gl.drawElementsInstanced (Gl.Triangles, indexCount, Gl.UnsignedInt, 0, count)
                counters.DrawCalls <- counters.DrawCalls + 1
                counters.Instances <- counters.Instances + count
                counters.Triangles <- counters.Triangles + mesh.TriangleCount * count

        last

    /// <summary>
    /// A promise that resolves when the GPU has passed <c>sync</c>. It looks once in each turn of the event
    /// loop, where a browser updates a fence, and rejects when the backend is disposed first.
    /// </summary>
    let private signaled (gl: Context) (sync: Sync) (isDisposed: unit -> bool) : JS.Promise<unit> =
        promise (fun resolve reject ->
            let rec look () =
                if isDisposed () then
                    reject (Exception "The context was disposed before the pick was read.")
                else
                    let status = gl.clientWaitSync (sync, 0, 0)

                    if status = Gl.TimeoutExpired then
                        later look 1
                    elif status = Gl.WaitFailed then
                        reject (Exception "WebGL2 could not wait for the pick pass.")
                    else
                        resolve ()

            look ())

    [<Sealed>]
    type private Backend(gl: Context, canvas: Canvas, resources: Resources, initialWidth: int, initialHeight: int) =
        let adapterName = rendererName gl
        let mutable width = initialWidth
        let mutable height = initialHeight
        let mutable disposed = false
        // Every buffer made through the seam and not yet destroyed: `Dispose` deletes what the caller left.
        let live = ResizeArray<GlBuffer>()
        // A byte view of the staging buffer last written from, kept while the caller keeps that buffer.
        let mutable stagingData = Unchecked.defaultof<JS.ArrayBuffer>
        let mutable stagingBytes = Unchecked.defaultof<JS.Uint8Array>

        let uniformBuffer (bytes: int) (binding: int) : Buffer =
            let buffer = gl.createBuffer ()
            gl.bindBuffer (Gl.UniformBuffer, buffer)
            gl.bufferData (Gl.UniformBuffer, bytes, Gl.DynamicDraw)
            gl.bindBufferBase (Gl.UniformBuffer, binding, buffer)
            buffer

        let sceneUniforms = uniformBuffer SceneUniformBytes 0
        let userUniforms = uniformBuffer UserUniformBytes 1

        let msaaFramebuffer = gl.createFramebuffer ()
        let msaaColor = gl.createRenderbuffer ()
        let msaaDepth = gl.createRenderbuffer ()
        let pickFramebuffer = gl.createFramebuffer ()
        let pickColor = gl.createRenderbuffer ()
        let pickDepth = gl.createRenderbuffer ()
        // The 1x target the multisampled colour is resolved into. It keeps the last frame, top row first.
        let resolveFramebuffer = gl.createFramebuffer ()
        let resolveColor = gl.createRenderbuffer ()

        let pickClear = JS.Constructors.Uint32Array.Create 4
        let depthClear = JS.Constructors.Float32Array.Create 1
        let multisampled = [| Gl.ColorAttachment0; Gl.DepthAttachment |]

        let store (renderbuffer: Renderbuffer) (samples: int) (format: int) =
            gl.bindRenderbuffer (Gl.Renderbuffer, renderbuffer)

            if samples > 1 then
                gl.renderbufferStorageMultisample (Gl.Renderbuffer, samples, format, width, height)
            else
                gl.renderbufferStorage (Gl.Renderbuffer, format, width, height)

        let attach (framebuffer: Framebuffer) (color: Renderbuffer) (depth: Renderbuffer) (what: string) =
            gl.bindFramebuffer (Gl.Framebuffer, framebuffer)
            gl.framebufferRenderbuffer (Gl.Framebuffer, Gl.ColorAttachment0, Gl.Renderbuffer, color)

            if not (isNull depth) then
                gl.framebufferRenderbuffer (Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, depth)

            let status = gl.checkFramebufferStatus Gl.Framebuffer

            if status <> Gl.FramebufferComplete then
                failwith
                    $"WgpuContext failed to initialize WebGL2: the {what} target of {width} by {height} pixels is not complete (status {status})."

        let allocateTargets () =
            store msaaColor SampleCount Gl.Rgba8
            store msaaDepth SampleCount Gl.DepthComponent32F
            store pickColor 1 Gl.Rgba32Ui
            store pickDepth 1 Gl.DepthComponent32F
            attach msaaFramebuffer msaaColor msaaDepth "4x multisampled"
            attach pickFramebuffer pickColor pickDepth "picking"
            store resolveColor 1 Gl.Rgba8
            attach resolveFramebuffer resolveColor null "resolve"

        /// Copies the resolved frame into the canvas, turned over: its first row is the top of the frame, and
        /// the first row of a WebGL drawing buffer is the bottom of the canvas.
        let present () =
            gl.bindFramebuffer (Gl.ReadFramebuffer, resolveFramebuffer)
            gl.bindFramebuffer (Gl.DrawFramebuffer, null)
            gl.blitFramebuffer (0, 0, width, height, 0, height, width, 0, Gl.ColorBufferBit, Gl.Nearest)

        let createBuffer (target: int) (byteLength: int) (usage: int) : IBackendBuffer =
            if target = Gl.ElementArrayBuffer then
                gl.bindVertexArray null

            let buffer = gl.createBuffer ()
            gl.bindBuffer (target, buffer)
            gl.bufferData (target, byteLength, usage)

            let entry =
                {
                    Buffer = buffer
                    Target = target
                    VertexArray = null
                    VertexArrayMesh = null
                    VertexArrayIndices = null
                    Slot = live.Count
                }

            live.Add entry
            unbox entry

        let delete (buffer: GlBuffer) =
            gl.deleteBuffer buffer.Buffer

            if not (isNull buffer.VertexArray) then
                gl.deleteVertexArray buffer.VertexArray
                buffer.VertexArray <- null

            buffer.VertexArrayMesh <- null
            buffer.VertexArrayIndices <- null

        let writeBufferAt (buffer: IBackendBuffer) (dstByteOffset: int) (data: JS.ArrayBuffer) (byteLength: int) =
            if byteLength > 0 then
                let glBuf = unbox<GlBuffer> buffer

                if not (obj.ReferenceEquals (data, stagingData)) then
                    stagingData <- data
                    stagingBytes <- JS.Constructors.Uint8Array.Create data

                if glBuf.Target = Gl.ElementArrayBuffer then
                    gl.bindVertexArray null

                gl.bindBuffer (glBuf.Target, glBuf.Buffer)
                gl.bufferSubData (glBuf.Target, dstByteOffset, stagingBytes, 0, byteLength)

        /// <summary>
        /// Sets the state that no pass changes, allocates the targets, and checks that the canvas takes a
        /// frame. Raises when it does not.
        /// </summary>
        member _.Start() =
            gl.disable Gl.Dither
            gl.disable Gl.ScissorTest
            gl.enable Gl.DepthTest
            gl.clearDepth 1.0
            gl.cullFace Gl.Back
            // Counter-clockwise in WebGPU's clip space, which the vertex stage turns over.
            gl.frontFace Gl.Cw
            gl.blendEquation Gl.FuncAdd
            // Premultiplied alpha: rgb_out = src_rgb + dst_rgb * (1 - src_a).
            gl.blendFunc (Gl.One, Gl.OneMinusSrcAlpha)
            depthClear[0] <- 1.0f
            allocateTargets ()

            // Errors left by whoever used the context before would be read as this blit's.
            while gl.getError () <> 0 do
                ()

            present ()
            let error = gl.getError ()

            if error <> 0 then
                failwith
                    $"WgpuContext failed to initialize WebGL2: the canvas's drawing buffer does not take the frame (error {error}). Its webgl2 context was made elsewhere with antialias, and nothing can be copied into a multisampled drawing buffer. Give WgpuContext a canvas that has no context yet."

        interface ISceneBackend with
            member _.AdapterName = adapterName
            member _.BackendName = "WebGL2"

            member _.CreateVertexBuffer(byteLength) =
                createBuffer Gl.ArrayBuffer byteLength Gl.DynamicDraw

            // No vertex array is bound between passes, so this binding changes no batch's vertex array.
            member _.CreateIndexBuffer(byteLength) =
                createBuffer Gl.ElementArrayBuffer byteLength Gl.StaticDraw

            member _.WriteBuffer(buffer, data, byteLength) =
                writeBufferAt buffer 0 data byteLength

            member _.WriteBufferAt(buffer, dstByteOffset, data, byteLength) =
                writeBufferAt buffer dstByteOffset data byteLength

            member _.WriteIndices(buffer, indices) =
                if indices.Length > 0 then
                    let glBuf = unbox<GlBuffer> buffer
                    gl.bindVertexArray null
                    gl.bindBuffer (glBuf.Target, glBuf.Buffer)
                    gl.bufferSubData (glBuf.Target, 0, indices, 0, indices.Length)

            member _.DestroyBuffer(buffer) =
                let glBuf = unbox<GlBuffer> buffer
                let slot = glBuf.Slot

                if slot >= 0 then
                    let last = live[live.Count - 1]
                    live[slot] <- last
                    last.Slot <- slot
                    live.RemoveAt (live.Count - 1)
                    glBuf.Slot <- -1

                    for i = 0 to live.Count - 1 do
                        let entry = live[i]

                        if
                            not (isNull entry.VertexArray)
                            && (obj.ReferenceEquals (entry.VertexArrayMesh, buffer)
                                || obj.ReferenceEquals (entry.VertexArrayIndices, buffer))
                        then
                            gl.deleteVertexArray entry.VertexArray
                            entry.VertexArray <- null
                            entry.VertexArrayMesh <- null
                            entry.VertexArrayIndices <- null

                    delete glBuf

            member _.WriteSceneUniforms(data) =
                gl.bindBuffer (Gl.UniformBuffer, sceneUniforms)
                gl.bufferSubData (Gl.UniformBuffer, 0, data, 0, Packing.SceneUniformFloats)

            // A slot is one std140 vec4: 16 bytes of the buffer and 4 elements of the copy.
            member _.WriteUserUniforms(data, firstSlot, slotCount) =
                gl.bindBuffer (Gl.UniformBuffer, userUniforms)
                gl.bufferSubData (Gl.UniformBuffer, firstSlot * 16, data, firstSlot * 4, slotCount * 4)

            member _.DrawPass(clear, batches, lines, discs, counters) =
                let alpha = float (Math.Clamp (clear.A, 0.0f, 1.0f))
                gl.bindFramebuffer (Gl.Framebuffer, msaaFramebuffer)
                gl.viewport (0, 0, width, height)
                gl.clearColor (float clear.R * alpha, float clear.G * alpha, float clear.B * alpha, alpha)
                // A clear writes depth only while depth writes are on.
                gl.depthMask true
                gl.clear (Gl.ColorBufferBit ||| Gl.DepthBufferBit)
                counters.DrawCalls <- 0
                counters.Instances <- 0
                counters.Triangles <- 0

                let batchSpan = batches.Span
                let lineSpan = lines.Span
                let discSpan = discs.Span
                let linePipeline = resources.Line
                let discPipeline = resources.Disc

                let mutable last =
                    drawBatches gl batchSpan false resources.OpaqueCull resources.OpaqueDouble Unchecked.defaultof<Pipeline> counters

                for i = 0 to lineSpan.Length - 1 do
                    let line = lineSpan[i] :> ILineDraw
                    let vertexCount = line.VertexCount

                    if vertexCount >= 2 then
                        if not (obj.ReferenceEquals (last, linePipeline)) then
                            apply gl linePipeline
                            last <- linePipeline

                        bindSingle gl WebGpuBackend.lineLayouts (unbox line.VertexBuffer)
                        gl.drawArrays (Gl.Lines, 0, vertexCount)
                        counters.DrawCalls <- counters.DrawCalls + 1

                last <- drawBatches gl batchSpan true resources.BlendCull resources.BlendDouble last counters

                for i = 0 to discSpan.Length - 1 do
                    let disc = discSpan[i] :> IDiscDraw
                    let count = disc.Count

                    if count > 0 then
                        if not (obj.ReferenceEquals (last, discPipeline)) then
                            apply gl discPipeline
                            last <- discPipeline

                        bindSingle gl WebGpuBackend.discLayouts (unbox disc.InstanceBuffer)
                        gl.drawArraysInstanced (Gl.Triangles, 0, 6, count)
                        counters.DrawCalls <- counters.DrawCalls + 1
                        counters.Instances <- counters.Instances + count
                        counters.Triangles <- counters.Triangles + count * 2

                gl.bindVertexArray null
                // Averages the four samples of each pixel. A multisampled blit cannot turn the frame over, so
                // the canvas is filled from the resolved frame by a second one.
                gl.bindFramebuffer (Gl.ReadFramebuffer, msaaFramebuffer)
                gl.bindFramebuffer (Gl.DrawFramebuffer, resolveFramebuffer)
                gl.blitFramebuffer (0, 0, width, height, 0, 0, width, height, Gl.ColorBufferBit, Gl.Nearest)
                gl.invalidateFramebuffer (Gl.ReadFramebuffer, multisampled)
                present ()

            member _.PickAsync(px, py, batches, discs) =
                gl.bindFramebuffer (Gl.Framebuffer, pickFramebuffer)
                gl.viewport (0, 0, width, height)
                gl.enable Gl.ScissorTest
                gl.scissor (px, py, 1, 1)
                gl.depthMask true
                gl.clearBufferuiv (Gl.Color, 0, pickClear)
                gl.clearBufferfv (Gl.Depth, 0, depthClear)

                let mutable last = Unchecked.defaultof<Pipeline>
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
                            apply gl pipeline
                            last <- pipeline

                        bindMesh gl mesh (unbox batch.InstanceBuffer)
                        gl.drawElementsInstanced (Gl.Triangles, indexCount, Gl.UnsignedInt, 0, count)

                let discSpan = discs.Span

                for i = 0 to discSpan.Length - 1 do
                    let disc = discSpan[i] :> IDiscDraw
                    let count = disc.Count

                    if count > 0 then
                        if not (obj.ReferenceEquals (last, resources.DiscPick)) then
                            apply gl resources.DiscPick
                            last <- resources.DiscPick

                        bindSingle gl WebGpuBackend.discLayouts (unbox disc.InstanceBuffer)
                        gl.drawArraysInstanced (Gl.Triangles, 0, 6, count)

                gl.bindVertexArray null
                gl.disable Gl.ScissorTest

                // Each pick reads into a buffer of its own, so picks started together do not share one, and
                // Chrome keeps no stale copy of a buffer that is written a second time.
                let target = gl.createBuffer ()
                gl.bindBuffer (Gl.PixelPackBuffer, target)
                gl.bufferData (Gl.PixelPackBuffer, 16, Gl.StreamRead)
                gl.readPixels (px, py, 1, 1, Gl.RgbaInteger, Gl.UnsignedInt, 0)
                gl.bindBuffer (Gl.PixelPackBuffer, null)
                let sync = gl.fenceSync (Gl.SyncGpuCommandsComplete, 0)
                gl.flush ()

                jsAsync {
                    try
                        do! signaled gl sync (fun () -> disposed)
                        let texel = JS.Constructors.Uint32Array.Create 4
                        gl.bindBuffer (Gl.PixelPackBuffer, target)
                        gl.getBufferSubData (Gl.PixelPackBuffer, 0, texel)
                        gl.bindBuffer (Gl.PixelPackBuffer, null)
                        let pickId = texel[3]

                        if pickId = 0u then
                            return ValueNone
                        else
                            let position = JS.Constructors.Float32Array.Create texel.buffer

                            return
                                ValueSome
                                    {
                                        PickId = pickId
                                        WorldPosition = Vector3 (position[0], position[1], position[2])
                                    }
                    finally
                        gl.deleteSync sync
                        gl.deleteBuffer target
                }

            member _.ReadPixelsAsync() =
                gl.bindFramebuffer (Gl.ReadFramebuffer, resolveFramebuffer)
                let rows = JS.Constructors.Uint8Array.Create (width * height * 4)
                gl.readPixels (0, 0, width, height, Gl.Rgba, Gl.UnsignedByte, rows)
                let pixels = Readback.copyPixels rows (width * 4) width height false
                jsAsync { return pixels }

            member _.Resize(newWidth, newHeight) =
                width <- newWidth
                height <- newHeight
                canvas.width <- newWidth
                canvas.height <- newHeight
                allocateTargets ()

            // The context itself is left alive: a canvas has one webgl2 context for its whole life, and losing
            // it would stop the canvas from taking another WgpuContext.
            member _.Dispose() =
                if not disposed then
                    disposed <- true
                    gl.bindVertexArray null
                    gl.bindFramebuffer (Gl.Framebuffer, null)
                    gl.useProgram null

                    for buffer in live do
                        buffer.Slot <- -1
                        delete buffer

                    live.Clear ()
                    gl.deleteFramebuffer msaaFramebuffer
                    gl.deleteFramebuffer pickFramebuffer
                    gl.deleteRenderbuffer msaaColor
                    gl.deleteRenderbuffer msaaDepth
                    gl.deleteRenderbuffer pickColor
                    gl.deleteRenderbuffer pickDepth
                    gl.deleteFramebuffer resolveFramebuffer
                    gl.deleteRenderbuffer resolveColor
                    gl.deleteBuffer sceneUniforms
                    gl.deleteBuffer userUniforms

                    for program in resources.Programs do
                        gl.deleteProgram program

    let private contextOf (canvas: Canvas) : Context =
        let attributes =
            {|
                // The frame is multisampled in renderbuffers of the backend's own and copied into the canvas.
                antialias = false
                alpha = true
                premultipliedAlpha = true
                depth = false
                stencil = false
                powerPreference = "high-performance"
            |}

        match canvas.getContext ("webgl2", attributes) with
        | None ->
            failwith
                "WgpuContext failed to initialize WebGL2: the canvas gave no webgl2 context. The browser has no WebGL2, or the canvas already has a context of another kind."
        | Some gl when gl.isContextLost () -> failwith "WgpuContext failed to initialize WebGL2: the canvas's webgl2 context is lost."
        | Some gl -> gl

    /// <summary>
    /// Links the programs and binds the canvas. With no size given, the canvas keeps the size it has.
    /// </summary>
    let create (canvas: obj) (width: int option) (height: int option) (customGlsl: string option) (plan: SceneEntryPlan option) : ISceneBackend =
        let canvas: Canvas = unbox canvas
        let gl = contextOf canvas
        let resources = build gl customGlsl plan
        let targetWidth = max 1 (defaultArg width canvas.width)
        let targetHeight = max 1 (defaultArg height canvas.height)
        canvas.width <- targetWidth
        canvas.height <- targetHeight
        let backend = new Backend (gl, canvas, resources, targetWidth, targetHeight)

        try
            backend.Start ()
            backend :> ISceneBackend
        with failure ->
            (backend :> ISceneBackend).Dispose()
            raise failure

    /// <summary>Compiles the GLSL and links every scene program from it on a context of its own, with no canvas of the page.</summary>
    let validate (customGlsl: string option) (plan: SceneEntryPlan option) : Result<unit, string> =
        let gl = contextOf (scratchCanvas ())

        try
            try
                build gl customGlsl plan |> ignore
                Ok ()
            with :? GlslException as failure ->
                Error failure.Message
        finally
            loseContext gl

    /// <summary>
    /// The std140 layout a driver gives the uniform blocks of the mesh program linked from <c>glsl</c>: each
    /// block's name and size, and each member's name, byte offset and array stride. Test support.
    /// </summary>
    let uniformLayout (glsl: string) =
        let gl = contextOf (scratchCanvas ())

        try
            let program = (build gl (Some glsl) None).OpaqueCull.Program
            let blocks: int = unbox (gl.getProgramParameter (program, Gl.ActiveUniformBlocks))
            let uniforms: int = unbox (gl.getProgramParameter (program, Gl.ActiveUniforms))
            let indices = Array.init uniforms id
            let blockOf = gl.getActiveUniforms (program, indices, Gl.UniformBlockIndex)
            let offsets = gl.getActiveUniforms (program, indices, Gl.UniformOffset)
            let strides = gl.getActiveUniforms (program, indices, Gl.UniformArrayStride)

            {|
                blocks =
                    Array.init blocks (fun i ->
                        {|
                            name = gl.getActiveUniformBlockName (program, i)
                            bytes = gl.getActiveUniformBlockParameter (program, i, Gl.UniformBlockDataSize)
                        |})
                members =
                    Array.init uniforms (fun i ->
                        {|
                            name = gl.getActiveUniform(program, i).name
                            block = gl.getActiveUniformBlockName (program, blockOf[i])
                            offset = offsets[i]
                            arrayStride = strides[i]
                        |})
            |}
        finally
            loseContext gl
