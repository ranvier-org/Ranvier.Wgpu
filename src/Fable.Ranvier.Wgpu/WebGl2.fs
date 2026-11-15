namespace Ranvier.Wgpu

open Fable.Core

/// <summary>The part of the browser WebGL2 API that the WebGL2 backend calls.</summary>
/// <remarks>Not a general binding: a member is here only because <c>WebGl2Backend.fs</c> uses it.</remarks>
module internal WebGl2 =

    /// <summary>Constants of <c>WebGL2RenderingContext</c>, with the values the specification gives them.</summary>
    module Gl =
        [<Literal>]
        let Lines = 0x0001

        [<Literal>]
        let Triangles = 0x0004

        [<Literal>]
        let DepthBufferBit = 0x0100

        [<Literal>]
        let ColorBufferBit = 0x4000

        [<Literal>]
        let Less = 0x0201

        [<Literal>]
        let LessEqual = 0x0203

        [<Literal>]
        let One = 1

        [<Literal>]
        let OneMinusSrcAlpha = 0x0303

        [<Literal>]
        let Back = 0x0405

        [<Literal>]
        let Cw = 0x0900

        [<Literal>]
        let CullFace = 0x0B44

        [<Literal>]
        let DepthTest = 0x0B71

        [<Literal>]
        let Dither = 0x0BD0

        [<Literal>]
        let Blend = 0x0BE2

        [<Literal>]
        let ScissorTest = 0x0C11

        [<Literal>]
        let UnsignedByte = 0x1401

        [<Literal>]
        let Int = 0x1404

        [<Literal>]
        let UnsignedInt = 0x1405

        [<Literal>]
        let Float = 0x1406

        [<Literal>]
        let Color = 0x1800

        [<Literal>]
        let Depth = 0x1801

        [<Literal>]
        let Rgba = 0x1908

        [<Literal>]
        let Nearest = 0x2600

        [<Literal>]
        let FuncAdd = 0x8006

        [<Literal>]
        let Rgba8 = 0x8058

        [<Literal>]
        let StreamRead = 0x88E1

        [<Literal>]
        let StaticDraw = 0x88E4

        [<Literal>]
        let DynamicDraw = 0x88E8

        [<Literal>]
        let PixelPackBuffer = 0x88EB

        [<Literal>]
        let ArrayBuffer = 0x8892

        [<Literal>]
        let ElementArrayBuffer = 0x8893

        [<Literal>]
        let UniformBuffer = 0x8A11

        [<Literal>]
        let ActiveUniformBlocks = 0x8A36

        [<Literal>]
        let UniformBlockIndex = 0x8A3A

        [<Literal>]
        let UniformOffset = 0x8A3B

        [<Literal>]
        let UniformArrayStride = 0x8A3C

        [<Literal>]
        let UniformBlockDataSize = 0x8A40

        [<Literal>]
        let FragmentShader = 0x8B30

        [<Literal>]
        let VertexShader = 0x8B31

        [<Literal>]
        let IntVec2 = 0x8B53

        [<Literal>]
        let IntVec4 = 0x8B55

        [<Literal>]
        let CompileStatus = 0x8B81

        [<Literal>]
        let LinkStatus = 0x8B82

        [<Literal>]
        let ActiveUniforms = 0x8B86

        [<Literal>]
        let ActiveAttributes = 0x8B89

        [<Literal>]
        let ReadFramebuffer = 0x8CA8

        [<Literal>]
        let DrawFramebuffer = 0x8CA9

        [<Literal>]
        let DepthComponent32F = 0x8CAC

        [<Literal>]
        let FramebufferComplete = 0x8CD5

        [<Literal>]
        let ColorAttachment0 = 0x8CE0

        [<Literal>]
        let DepthAttachment = 0x8D00

        [<Literal>]
        let Framebuffer = 0x8D40

        [<Literal>]
        let Renderbuffer = 0x8D41

        [<Literal>]
        let Rgba32Ui = 0x8D70

        [<Literal>]
        let RgbaInteger = 0x8D99

        [<Literal>]
        let UnsignedIntVec2 = 0x8DC6

        [<Literal>]
        let UnsignedIntVec4 = 0x8DC8

        [<Literal>]
        let SyncGpuCommandsComplete = 0x9117

        [<Literal>]
        let TimeoutExpired = 0x911B

        [<Literal>]
        let WaitFailed = 0x911D

    [<AllowNullLiteral>]
    type Buffer = interface end

    [<AllowNullLiteral>]
    type VertexArray = interface end

    [<AllowNullLiteral>]
    type Framebuffer = interface end

    [<AllowNullLiteral>]
    type Renderbuffer = interface end

    [<AllowNullLiteral>]
    type Shader = interface end

    [<AllowNullLiteral>]
    type Program = interface end

    type Sync = interface end

    /// <summary>What <c>getActiveAttrib</c> and <c>getActiveUniform</c> return.</summary>
    type ActiveInfo =
        abstract name: string
        abstract ``type``: int

    /// <summary>A <c>WebGL2RenderingContext</c>.</summary>
    type Context =
        abstract isContextLost: unit -> bool
        abstract getError: unit -> int

        abstract createBuffer: unit -> Buffer
        abstract bindBuffer: target: int * buffer: Buffer -> unit
        abstract bufferData: target: int * size: int * usage: int -> unit

        /// <summary>Writes <c>length</c> elements of a typed array from element <c>srcOffset</c>.</summary>
        abstract bufferSubData: target: int * dstByteOffset: int * srcData: obj * srcOffset: int * length: int -> unit

        abstract getBufferSubData: target: int * srcByteOffset: int * dstData: obj -> unit
        abstract bindBufferBase: target: int * index: int * buffer: Buffer -> unit
        abstract deleteBuffer: buffer: Buffer -> unit

        abstract createVertexArray: unit -> VertexArray
        abstract bindVertexArray: vertexArray: VertexArray -> unit
        abstract deleteVertexArray: vertexArray: VertexArray -> unit
        abstract enableVertexAttribArray: index: int -> unit
        abstract vertexAttribPointer: index: int * size: int * ``type``: int * normalized: bool * stride: int * offset: int -> unit
        abstract vertexAttribIPointer: index: int * size: int * ``type``: int * stride: int * offset: int -> unit
        abstract vertexAttribDivisor: index: int * divisor: int -> unit

        abstract createShader: ``type``: int -> Shader
        abstract shaderSource: shader: Shader * source: string -> unit
        abstract compileShader: shader: Shader -> unit
        abstract getShaderParameter: shader: Shader * name: int -> bool
        abstract getShaderInfoLog: shader: Shader -> string
        abstract deleteShader: shader: Shader -> unit

        abstract createProgram: unit -> Program
        abstract attachShader: program: Program * shader: Shader -> unit
        abstract linkProgram: program: Program -> unit
        abstract getProgramParameter: program: Program * name: int -> obj
        abstract getProgramInfoLog: program: Program -> string
        abstract useProgram: program: Program -> unit
        abstract deleteProgram: program: Program -> unit
        abstract getActiveAttrib: program: Program * index: int -> ActiveInfo
        abstract getAttribLocation: program: Program * name: string -> int
        abstract getActiveUniform: program: Program * index: int -> ActiveInfo
        abstract getActiveUniformBlockName: program: Program * blockIndex: int -> string
        abstract getActiveUniformBlockParameter: program: Program * blockIndex: int * name: int -> int
        abstract uniformBlockBinding: program: Program * blockIndex: int * binding: int -> unit
        abstract getActiveUniforms: program: Program * indices: int array * name: int -> int array

        abstract createFramebuffer: unit -> Framebuffer
        abstract bindFramebuffer: target: int * framebuffer: Framebuffer -> unit
        abstract framebufferRenderbuffer: target: int * attachment: int * renderbufferTarget: int * renderbuffer: Renderbuffer -> unit
        abstract checkFramebufferStatus: target: int -> int
        abstract deleteFramebuffer: framebuffer: Framebuffer -> unit
        abstract createRenderbuffer: unit -> Renderbuffer
        abstract bindRenderbuffer: target: int * renderbuffer: Renderbuffer -> unit
        abstract renderbufferStorage: target: int * format: int * width: int * height: int -> unit
        abstract renderbufferStorageMultisample: target: int * samples: int * format: int * width: int * height: int -> unit
        abstract deleteRenderbuffer: renderbuffer: Renderbuffer -> unit

        abstract blitFramebuffer:
            srcX0: int * srcY0: int * srcX1: int * srcY1: int * dstX0: int * dstY0: int * dstX1: int * dstY1: int * mask: int * filter: int -> unit

        abstract invalidateFramebuffer: target: int * attachments: int array -> unit

        /// <summary>Reads into a typed array, or to a byte offset of the bound pixel pack buffer.</summary>
        abstract readPixels: x: int * y: int * width: int * height: int * format: int * ``type``: int * pixels: obj -> unit

        abstract viewport: x: int * y: int * width: int * height: int -> unit
        abstract scissor: x: int * y: int * width: int * height: int -> unit
        abstract enable: capability: int -> unit
        abstract disable: capability: int -> unit
        abstract depthMask: flag: bool -> unit
        abstract depthFunc: func: int -> unit
        abstract cullFace: mode: int -> unit
        abstract frontFace: mode: int -> unit
        abstract blendFunc: sourceFactor: int * destinationFactor: int -> unit
        abstract blendEquation: mode: int -> unit
        abstract clearColor: red: float * green: float * blue: float * alpha: float -> unit
        abstract clearDepth: depth: float -> unit
        abstract clear: mask: int -> unit
        abstract clearBufferuiv: buffer: int * drawBuffer: int * values: JS.Uint32Array -> unit
        abstract clearBufferfv: buffer: int * drawBuffer: int * values: JS.Float32Array -> unit

        abstract drawArrays: mode: int * first: int * count: int -> unit
        abstract drawArraysInstanced: mode: int * first: int * count: int * instanceCount: int -> unit
        abstract drawElementsInstanced: mode: int * count: int * ``type``: int * offset: int * instanceCount: int -> unit

        abstract fenceSync: condition: int * flags: int -> Sync
        abstract clientWaitSync: sync: Sync * flags: int * timeout: int -> int
        abstract deleteSync: sync: Sync -> unit
        abstract flush: unit -> unit

    /// <summary>An <c>HTMLCanvasElement</c> or an <c>OffscreenCanvas</c>.</summary>
    type Canvas =
        abstract width: int with get, set
        abstract height: int with get, set
        abstract getContext: contextId: string * attributes: obj -> Context option

    /// <summary>The renderer's name, or an empty string when the browser withholds it.</summary>
    [<Emit("(e => String((e ? $0.getParameter(e.UNMASKED_RENDERER_WEBGL) : $0.getParameter($0.RENDERER)) || ''))($0.getExtension('WEBGL_debug_renderer_info'))")>]
    let rendererName (gl: Context) : string = jsNative

    /// <summary>A 1x1 canvas that is not in the page.</summary>
    [<Emit("(typeof OffscreenCanvas === 'function' ? new OffscreenCanvas(1, 1) : document.createElement('canvas'))")>]
    let scratchCanvas () : Canvas = jsNative

    /// <summary>Frees a context's GPU resources at once. The context cannot be used again.</summary>
    [<Emit("$0.getExtension('WEBGL_lose_context')?.loseContext()")>]
    let loseContext (gl: Context) : unit = jsNative

    /// <summary>Calls <c>callback</c> after <c>milliseconds</c>, on a later turn of the event loop.</summary>
    [<Emit("setTimeout($0, $1)")>]
    let later (callback: unit -> unit) (milliseconds: int) : unit = jsNative

    /// <summary>A promise settled by the executor's <c>resolve</c> or <c>reject</c>.</summary>
    [<Emit("new Promise($0)")>]
    let promise (executor: (unit -> unit) -> (exn -> unit) -> unit) : JS.Promise<unit> = jsNative
