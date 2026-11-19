namespace Ranvier.Wgpu

open Fable.Core

/// <summary>The part of the browser WebGPU API that the WebGPU backend calls.</summary>
/// <remarks>
/// Not a general binding: a member is here only because <c>WebGpuBackend.fs</c> uses it. Descriptors are
/// anonymous records, which compile to object literals, and the few descriptors that are kept and changed
/// between frames are classes compiled to plain objects.
/// </remarks>
module internal WebGpu =

    /// <summary>Bits of <c>GPUBufferUsage</c>.</summary>
    module BufferUsage =
        [<Literal>]
        let MapRead = 0x01

        [<Literal>]
        let CopyDst = 0x08

        [<Literal>]
        let Index = 0x10

        [<Literal>]
        let Vertex = 0x20

        [<Literal>]
        let Uniform = 0x40

    /// <summary>Bits of <c>GPUTextureUsage</c>.</summary>
    module TextureUsage =
        [<Literal>]
        let CopySrc = 0x01

        [<Literal>]
        let RenderAttachment = 0x10

    /// <summary>Bits of <c>GPUShaderStage</c>.</summary>
    module ShaderStage =
        [<Literal>]
        let VertexAndFragment = 0x3

    /// <summary><c>GPUMapMode.READ</c>.</summary>
    [<Literal>]
    let MapModeRead = 0x1

    [<StringEnum; RequireQualifiedAccess>]
    type TextureFormat =
        | [<CompiledName "rgba8unorm">] Rgba8Unorm
        | [<CompiledName "bgra8unorm">] Bgra8Unorm
        | [<CompiledName "depth32float">] Depth32Float
        | [<CompiledName "rgba32uint">] Rgba32Uint

    [<StringEnum; RequireQualifiedAccess>]
    type VertexFormat =
        | [<CompiledName "float32x2">] Float32x2
        | [<CompiledName "float32x3">] Float32x3
        | [<CompiledName "float32x4">] Float32x4
        | [<CompiledName "uint32x2">] Uint32x2

    [<StringEnum; RequireQualifiedAccess>]
    type StepMode =
        | [<CompiledName "vertex">] Vertex
        | [<CompiledName "instance">] Instance

    [<StringEnum; RequireQualifiedAccess>]
    type Topology =
        | [<CompiledName "triangle-list">] TriangleList
        | [<CompiledName "line-list">] LineList

    [<StringEnum; RequireQualifiedAccess>]
    type CullMode =
        | [<CompiledName "none">] None
        | [<CompiledName "back">] Back

    [<StringEnum; RequireQualifiedAccess>]
    type CompareFunction =
        | [<CompiledName "less">] Less
        | [<CompiledName "less-equal">] LessEqual
        | [<CompiledName "always">] Always

    [<StringEnum; RequireQualifiedAccess>]
    type LoadOp =
        | [<CompiledName "clear">] Clear
        | [<CompiledName "load">] Load

    [<StringEnum; RequireQualifiedAccess>]
    type StoreOp =
        | [<CompiledName "store">] Store
        | [<CompiledName "discard">] Discard

    [<AllowNullLiteral>]
    type Buffer =
        abstract mapAsync: mode: int * offset: int * size: int -> JS.Promise<unit>
        abstract getMappedRange: offset: int * size: int -> JS.ArrayBuffer
        abstract unmap: unit -> unit
        abstract destroy: unit -> unit

    [<AllowNullLiteral>]
    type TextureView = interface end

    [<AllowNullLiteral>]
    type Texture =
        abstract createView: unit -> TextureView
        abstract destroy: unit -> unit

    type ShaderModule = interface end
    type BindGroupLayout = interface end
    type BindGroup = interface end
    type PipelineLayout = interface end

    [<AllowNullLiteral>]
    type RenderPipeline = interface end

    [<AllowNullLiteral>]
    type CommandBuffer = interface end

    type VertexAttribute =
        {|
            format: VertexFormat
            offset: int
            shaderLocation: int
        |}

    type VertexBufferLayout =
        {|
            arrayStride: int
            stepMode: StepMode
            attributes: VertexAttribute array
        |}

    type BlendComponent =
        {|
            operation: string
            srcFactor: string
            dstFactor: string
        |}

    type ColorTargetState =
        {|
            format: TextureFormat
            blend:
                {|
                    color: BlendComponent
                    alpha: BlendComponent
                |} option
        |}

    /// <summary>A render pipeline. Stencil state, the sample mask and the colour write mask keep their defaults.</summary>
    type RenderPipelineDescriptor =
        {|
            layout: PipelineLayout
            vertex:
                {|
                    ``module``: ShaderModule
                    entryPoint: string
                    buffers: VertexBufferLayout array
                |}
            primitive:
                {|
                    topology: Topology
                    frontFace: string
                    cullMode: CullMode
                |}
            depthStencil:
                {|
                    format: TextureFormat
                    depthWriteEnabled: bool
                    depthCompare: CompareFunction
                |}
            multisample: {| count: int |}
            fragment:
                {|
                    ``module``: ShaderModule
                    entryPoint: string
                    targets: ColorTargetState array
                |}
        |}

    /// <summary>A clear colour, kept by the backend and written before each pass.</summary>
    [<JS.Pojo>]
    type Color(r: float, g: float, b: float, a: float) =
        member val r = r with get, set
        member val g = g with get, set
        member val b = b with get, set
        member val a = a with get, set

    /// <summary>A colour attachment, kept by the backend; the resolve target changes each frame.</summary>
    [<JS.Pojo>]
    type ColorAttachment(view: TextureView, loadOp: LoadOp, storeOp: StoreOp, ?resolveTarget: TextureView, ?clearValue: Color) =
        member val view = view with get, set
        member val resolveTarget = resolveTarget with get, set
        member val clearValue = clearValue with get, set
        member val loadOp = loadOp with get, set
        member val storeOp = storeOp with get, set

    /// <summary>A depth attachment, kept by the backend.</summary>
    [<JS.Pojo>]
    type DepthAttachment(view: TextureView, depthClearValue: float, depthLoadOp: LoadOp, depthStoreOp: StoreOp) =
        member val view = view with get, set
        member val depthClearValue = depthClearValue with get, set
        member val depthLoadOp = depthLoadOp with get, set
        member val depthStoreOp = depthStoreOp with get, set

    /// <summary>A render pass descriptor, kept by the backend.</summary>
    [<JS.Pojo>]
    type RenderPassDescriptor(colorAttachments: ColorAttachment array, ?depthStencilAttachment: DepthAttachment) =
        member val colorAttachments = colorAttachments with get, set
        member val depthStencilAttachment = depthStencilAttachment with get, set

    type RenderPass =
        abstract setPipeline: pipeline: RenderPipeline -> unit
        abstract setBindGroup: index: int * bindGroup: BindGroup -> unit
        abstract setVertexBuffer: slot: int * buffer: Buffer * offset: int * size: int -> unit
        abstract setIndexBuffer: buffer: Buffer * indexFormat: string * offset: int * size: int -> unit
        abstract drawIndexed: indexCount: int * instanceCount: int -> unit
        abstract draw: vertexCount: int * instanceCount: int -> unit
        abstract setViewport: x: float * y: float * width: float * height: float * minDepth: float * maxDepth: float -> unit
        abstract setScissorRect: x: int * y: int * width: int * height: int -> unit
        abstract ``end``: unit -> unit

    type CommandEncoder =
        abstract beginRenderPass: descriptor: RenderPassDescriptor -> RenderPass

        abstract copyTextureToBuffer:
            source:
                {|
                    texture: Texture
                    origin: {| x: int; y: int |}
                |} *
            destination:
                {|
                    buffer: Buffer
                    bytesPerRow: int
                    rowsPerImage: int
                |} *
            copySize: {| width: int; height: int |} ->
                unit

        abstract finish: unit -> CommandBuffer

    type Queue =
        /// <summary>Writes <c>size</c> bytes of an <c>ArrayBuffer</c>, or <c>size</c> elements of a typed array.</summary>
        abstract writeBuffer: buffer: Buffer * bufferOffset: int * data: obj * dataOffset: int * size: int -> unit
        abstract submit: commandBuffers: CommandBuffer array -> unit

    type Error =
        abstract message: string

    type Device =
        abstract queue: Queue
        abstract createBuffer: descriptor: {| size: int; usage: int |} -> Buffer

        abstract createTexture:
            descriptor:
                {|
                    size: {| width: int; height: int |}
                    format: TextureFormat
                    sampleCount: int
                    usage: int
                |} ->
                Texture

        abstract createShaderModule: descriptor: {| code: string |} -> ShaderModule

        abstract createBindGroupLayout:
            descriptor:
                {|
                    entries:
                        {|
                            binding: int
                            visibility: int
                            buffer:
                                {|
                                    ``type``: string
                                    minBindingSize: int
                                |}
                        |} array
                |} ->
                BindGroupLayout

        abstract createBindGroup:
            descriptor:
                {|
                    layout: BindGroupLayout
                    entries:
                        {|
                            binding: int
                            resource:
                                {|
                                    buffer: Buffer
                                    offset: int
                                    size: int
                                |}
                        |} array
                |} ->
                BindGroup

        abstract createPipelineLayout:
            descriptor:
                {|
                    bindGroupLayouts: BindGroupLayout array
                |} ->
                PipelineLayout

        abstract createRenderPipeline: descriptor: RenderPipelineDescriptor -> RenderPipeline
        abstract createCommandEncoder: unit -> CommandEncoder
        abstract pushErrorScope: filter: string -> unit
        abstract popErrorScope: unit -> JS.Promise<Error option>
        abstract destroy: unit -> unit

    type Adapter =
        abstract requestDevice: unit -> JS.Promise<Device>

    /// <summary>The adapter's description, device or vendor, whichever the browser discloses first, or an empty string.</summary>
    [<Emit("($0.info?.description || $0.info?.device || $0.info?.vendor || '')")>]
    let adapterName (adapter: Adapter) : string = jsNative

    type Gpu =
        abstract requestAdapter: options: obj -> JS.Promise<Adapter option>
        abstract getPreferredCanvasFormat: unit -> TextureFormat

    type CanvasContext =
        abstract configure:
            configuration:
                {|
                    device: Device
                    format: TextureFormat
                    alphaMode: string
                |} ->
                unit

        abstract getCurrentTexture: unit -> Texture
        abstract unconfigure: unit -> unit

    /// <summary>An <c>HTMLCanvasElement</c> or an <c>OffscreenCanvas</c>.</summary>
    type Canvas =
        abstract width: int with get, set
        abstract height: int with get, set
        abstract getContext: contextId: string -> CanvasContext option

    /// <summary><c>navigator.gpu</c>, or <c>None</c> where the page has no WebGPU.</summary>
    [<Emit("globalThis.navigator?.gpu")>]
    let gpu () : Gpu option = jsNative
