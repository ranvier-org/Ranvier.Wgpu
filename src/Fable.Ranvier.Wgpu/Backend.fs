namespace Ranvier.Wgpu

open System
open Fable.Core

// The seam between the scene API and the graphics API that draws it. `WgpuContext.fs` and `Batches.fs` call
// only what is declared here, so a second backend (WebGL2) is a new file beside `WebGpuBackend.fs`.
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

/// <summary>The clock that frame timings are read from.</summary>
module internal Clock =
    /// <summary>Milliseconds since the page's time origin.</summary>
    [<Emit("performance.now()")>]
    let now () : float = jsNative
