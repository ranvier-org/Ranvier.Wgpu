namespace Ranvier.Wgpu

open System
open Fable.Core

/// <summary>Which graphics API a <c>WgpuContext</c> draws with in a browser.</summary>
[<RequireQualifiedAccess; Struct>]
type WgpuBrowserBackend =
    /// <summary>WebGPU when <c>navigator.gpu</c> gives an adapter, otherwise WebGL2.</summary>
    | Auto
    /// <summary>WebGPU only. Creating a context fails where the page has no WebGPU or no adapter.</summary>
    | WebGPU
    /// <summary>WebGL2 only.</summary>
    | WebGL2

    override this.ToString() =
        match this with
        | Auto -> "Auto"
        | WebGPU -> "WebGPU"
        | WebGL2 -> "WebGL2"

// The seam between the scene API and the graphics API that draws it. `WgpuContext.fs` and `Batches.fs` call
// only what is declared here. `WebGpuBackend.fs` and `WebGl2Backend.fs` implement it, and `WgpuContext.CreateAsync`
// chooses between them once, when a context is made.
//
// Every call is coarse: a buffer write, a uniform write, a whole pass. A backend reads the batches of a pass
// through the views below, so drawing costs one call across the seam for a pass, not one for a batch.

/// <summary>A GPU buffer owned by a backend.</summary>
[<AllowNullLiteral>]
type internal IBackendBuffer = interface end

/// <summary>What a backend reads from an uploaded mesh.</summary>
type internal IMeshDraw =
    abstract VertexBuffer: IBackendBuffer
    abstract VertexBytes: int
    abstract IndexBuffer: IBackendBuffer
    abstract IndexBytes: int
    abstract IndexCount: int
    abstract TriangleCount: int

/// <summary>What a backend reads from an instance batch. The buffer is null until the batch is first filled.</summary>
type internal IInstanceDraw =
    abstract Mesh: IMeshDraw
    abstract InstanceBuffer: IBackendBuffer
    abstract ActiveBytes: int
    abstract Count: int
    abstract AlphaBlend: bool
    abstract DoubleSided: bool

/// <summary>What a backend reads from a line batch. The buffer is null until the batch is first filled.</summary>
type internal ILineDraw =
    abstract VertexBuffer: IBackendBuffer
    abstract ActiveBytes: int
    abstract VertexCount: int

/// <summary>What a backend reads from a disc batch. The buffer is null until the batch is first filled.</summary>
type internal IDiscDraw =
    abstract InstanceBuffer: IBackendBuffer
    abstract ActiveBytes: int
    abstract Count: int

/// <summary>Counters of the last pass a backend drew. A context keeps one and the backend overwrites it.</summary>
[<NoEquality; NoComparison>]
type internal PassCounters =
    {
        mutable DrawCalls: int
        mutable Instances: int
        mutable Triangles: int
    }

/// <summary>A graphics API that can draw a scene pass into a canvas.</summary>
type internal ISceneBackend =
    /// <summary>The adapter's name, or an empty string when the browser withholds it.</summary>
    abstract AdapterName: string

    /// <summary>The name of the graphics API, such as <c>WebGPU</c>.</summary>
    abstract BackendName: string

    abstract CreateVertexBuffer: byteLength: int -> IBackendBuffer
    abstract CreateIndexBuffer: byteLength: int -> IBackendBuffer

    /// <summary>Copies the first <c>byteLength</c> bytes of <c>data</c> to the start of <c>buffer</c>.</summary>
    abstract WriteBuffer: buffer: IBackendBuffer * data: JS.ArrayBuffer * byteLength: int -> unit

    /// <summary>Copies all of <c>indices</c> to the start of <c>buffer</c>.</summary>
    abstract WriteIndices: buffer: IBackendBuffer * indices: uint32 array -> unit

    abstract DestroyBuffer: buffer: IBackendBuffer -> unit

    /// <summary>Writes the scene uniform block: <c>Packing.SceneUniformFloats</c> values.</summary>
    abstract WriteSceneUniforms: data: JS.Float32Array -> unit

    /// <summary>Writes <c>slotCount</c> slots of the user uniform block from <c>firstSlot</c>, out of a copy of the whole block.</summary>
    abstract WriteUserUniforms: data: JS.Float32Array * firstSlot: int * slotCount: int -> unit

    /// <summary>
    /// Draws one pass into the canvas with the uniforms last written: opaque batches, lines, blended batches,
    /// then discs.
    /// </summary>
    abstract DrawPass<'Batch, 'Line, 'Disc when 'Batch :> IInstanceDraw and 'Line :> ILineDraw and 'Disc :> IDiscDraw> :
        clear: RgbaColor * batches: ReadOnlyMemory<'Batch> * lines: ReadOnlyMemory<'Line> * discs: ReadOnlyMemory<'Disc> * counters: PassCounters ->
            unit

    /// <summary>
    /// Draws the pick pass with the uniforms last written and reads the pixel at <c>(px, py)</c>, which is
    /// inside the target.
    /// </summary>
    abstract PickAsync<'Batch, 'Disc when 'Batch :> IInstanceDraw and 'Disc :> IDiscDraw> :
        px: int * py: int * batches: ReadOnlyMemory<'Batch> * discs: ReadOnlyMemory<'Disc> -> JS.Promise<PickHit voption>

    /// <summary>Reads the last pass drawn as straight RGBA bytes, rows from the top.</summary>
    abstract ReadPixelsAsync: unit -> JS.Promise<byte array>

    /// <summary>Sets the canvas size and reallocates the targets.</summary>
    abstract Resize: width: int * height: int -> unit

    abstract Dispose: unit -> unit

/// <summary>What both backends do to a frame read back from the GPU.</summary>
module internal Readback =
    /// <summary>
    /// Copies rows of <c>stride</c> bytes of premultiplied pixels into tight straight-alpha RGBA rows: the
    /// readback of the .NET package's <c>Render</c>.
    /// </summary>
    let copyPixels (source: JS.Uint8Array) (stride: int) (width: int) (height: int) (isBgra: bool) : byte array =
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

/// <summary>The clock that frame timings are read from.</summary>
module internal Clock =
    /// <summary>Milliseconds since the page's time origin.</summary>
    [<Emit("performance.now()")>]
    let now () : float = jsNative
