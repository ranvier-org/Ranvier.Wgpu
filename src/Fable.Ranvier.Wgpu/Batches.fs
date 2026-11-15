namespace Ranvier.Wgpu

open System
open Fable.Core

/// <summary>GPU-resident indexed triangle mesh uploaded to a <see cref="T:Ranvier.Wgpu.WgpuContext"/>.</summary>
[<Sealed>]
type MeshHandle internal (backend: ISceneBackend, staging: PackBuffer, mesh: MeshData) =
    let mutable disposed = false
    let indexCount = mesh.Indices.Length
    let triangleCount = mesh.Indices.Length / 3
    let vertexBytes = max 1 mesh.Vertices.Length * Packing.VertexBytes
    let indexBytes = max 4 (mesh.Indices.Length * 4)

    let vertexBuffer =
        let buffer = backend.CreateVertexBuffer vertexBytes

        if mesh.Vertices.Length > 0 then
            let written = Packing.vertices staging (ReadOnlySpan<Vertex3D>(mesh.Vertices))
            backend.WriteBuffer (buffer, staging.Buffer, written)

        buffer

    let indexBuffer =
        let buffer = backend.CreateIndexBuffer indexBytes

        if mesh.Indices.Length > 0 then
            // A project compiled without typed arrays has a plain array here.
            let indices: uint32 array =
                if JS.Constructors.ArrayBuffer.isView mesh.Indices then
                    mesh.Indices
                else
                    unbox (JS.Constructors.Uint32Array.Create (box mesh.Indices))

            backend.WriteIndices (buffer, indices)

        buffer

    /// <summary>Total index count in the mesh index buffer.</summary>
    member _.IndexCount = uint32 indexCount

    /// <summary>Total triangle count (`IndexCount / 3`).</summary>
    member _.TriangleCount = triangleCount

    /// <summary>Destroys the GPU vertex and index buffers.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true
            backend.DestroyBuffer vertexBuffer
            backend.DestroyBuffer indexBuffer

    interface IMeshDraw with
        member _.VertexBuffer = vertexBuffer
        member _.VertexBytes = vertexBytes
        member _.IndexBuffer = indexBuffer
        member _.IndexBytes = indexBytes
        member _.IndexCount = indexCount
        member _.TriangleCount = triangleCount

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced GPU draw batch bound to a <see cref="T:Ranvier.Wgpu.MeshHandle"/>.
/// Supports updating `Instance3D` transforms dynamically without recreating mesh geometry.
/// </summary>
[<Sealed>]
type InstanceBatch internal (backend: ISceneBackend, staging: PackBuffer, mesh: MeshHandle, blendMode: BlendMode, doubleSided: bool) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable count = 0
    let mutable instanceBuffer: IBackendBuffer = null
    let alphaBlend = blendMode = BlendMode.AlphaBlend

    let ensureCapacity (needed: int) =
        let required = max 1 needed

        if required > capacity || isNull instanceBuffer then
            if not (isNull instanceBuffer) then
                backend.DestroyBuffer instanceBuffer

            let newCapacity = max required (max 16 (capacity * 2))
            instanceBuffer <- backend.CreateVertexBuffer (newCapacity * Packing.InstanceBytes)
            capacity <- newCapacity

    let checkLive () =
        if disposed then
            raise (ObjectDisposedException (nameof InstanceBatch))

    /// <summary>Mesh geometry instanced by this batch.</summary>
    member _.Mesh = mesh

    /// <summary>Opaque or premultiplied alpha-blend pass selection.</summary>
    member _.BlendMode = blendMode

    /// <summary>True when back-face culling is disabled for this batch.</summary>
    member _.DoubleSided = doubleSided

    /// <summary>Active instance count uploaded to the GPU.</summary>
    member _.Count = count

    /// <summary>Uploads a contiguous span of `Instance3D` records to the GPU instance buffer.</summary>
    member _.UpdateInstances(instances: ReadOnlySpan<Instance3D>) =
        checkLive ()
        count <- instances.Length

        if instances.Length > 0 then
            ensureCapacity instances.Length
            backend.WriteBuffer (instanceBuffer, staging.Buffer, Packing.instances staging instances)

    /// <summary>Uploads an array of `Instance3D` records to the GPU instance buffer.</summary>
    member this.UpdateInstances(instances: Instance3D array) =
        this.UpdateInstances (ReadOnlySpan<Instance3D>(instances))

    /// <summary>Uploads a single `Instance3D` record without allocating an array.</summary>
    member _.SetSingle(instance: Instance3D) =
        checkLive ()
        count <- 1
        ensureCapacity 1
        backend.WriteBuffer (instanceBuffer, staging.Buffer, Packing.instance staging instance)

    /// <summary>Destroys the GPU instance buffer.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull instanceBuffer) then
                backend.DestroyBuffer instanceBuffer
                instanceBuffer <- null

    interface IInstanceDraw with
        member _.Mesh = mesh :> IMeshDraw
        member _.InstanceBuffer = instanceBuffer
        member _.ActiveBytes = max 1 count * Packing.InstanceBytes
        member _.Count = count
        member _.AlphaBlend = alphaBlend
        member _.DoubleSided = doubleSided

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware 4x-MSAA premultiplied alpha-blended 3D hairline batch (`LineList`).
/// Consecutive vertex pairs `(2k, 2k+1)` form independent 3D line segments.
/// </summary>
[<Sealed>]
type LineBatch internal (backend: ISceneBackend, staging: PackBuffer) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable vertexCount = 0
    let mutable vertexBuffer: IBackendBuffer = null

    let ensureCapacity (needed: int) =
        let required = max 2 needed

        if required > capacity || isNull vertexBuffer then
            if not (isNull vertexBuffer) then
                backend.DestroyBuffer vertexBuffer

            let newCapacity = max required (max 64 (capacity * 2))
            vertexBuffer <- backend.CreateVertexBuffer (newCapacity * Packing.LineVertexBytes)
            capacity <- newCapacity

    /// <summary>Active endpoint vertex count (always rounded down to an even number).</summary>
    member _.VertexCount = vertexCount

    /// <summary>Active line segment count (`VertexCount / 2`).</summary>
    member _.SegmentCount = vertexCount / 2

    /// <summary>Uploads a contiguous span of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member _.UpdateVertices(vertices: ReadOnlySpan<LineVertex3D>) =
        if disposed then
            raise (ObjectDisposedException (nameof LineBatch))

        let evenLength = vertices.Length &&& ~~~1
        vertexCount <- evenLength

        if evenLength > 0 then
            ensureCapacity evenLength
            backend.WriteBuffer (vertexBuffer, staging.Buffer, Packing.lineVertices staging vertices evenLength)

    /// <summary>Uploads an array of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member this.UpdateVertices(vertices: LineVertex3D array) =
        this.UpdateVertices (ReadOnlySpan<LineVertex3D>(vertices))

    /// <summary>Destroys the GPU line vertex buffer.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull vertexBuffer) then
                backend.DestroyBuffer vertexBuffer
                vertexBuffer <- null

    interface ILineDraw with
        member _.VertexBuffer = vertexBuffer
        member _.ActiveBytes = max 2 vertexCount * Packing.LineVertexBytes
        member _.VertexCount = vertexCount

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced camera-facing SDF disc and verification-arc billboard batch (`DiscInstance3D`).
/// Supports both anti-aliased visual rendering and 1x1 GPU picking.
/// </summary>
[<Sealed>]
type DiscBatch internal (backend: ISceneBackend, staging: PackBuffer) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable count = 0
    let mutable instanceBuffer: IBackendBuffer = null

    let ensureCapacity (needed: int) =
        let required = max 1 needed

        if required > capacity || isNull instanceBuffer then
            if not (isNull instanceBuffer) then
                backend.DestroyBuffer instanceBuffer

            let newCapacity = max required (max 32 (capacity * 2))
            instanceBuffer <- backend.CreateVertexBuffer (newCapacity * Packing.DiscBytes)
            capacity <- newCapacity

    /// <summary>Active billboard disc count uploaded to the GPU.</summary>
    member _.Count = count

    /// <summary>Uploads a contiguous span of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member _.UpdateDiscs(discs: ReadOnlySpan<DiscInstance3D>) =
        if disposed then
            raise (ObjectDisposedException (nameof DiscBatch))

        count <- discs.Length

        if discs.Length > 0 then
            ensureCapacity discs.Length
            backend.WriteBuffer (instanceBuffer, staging.Buffer, Packing.discs staging discs)

    /// <summary>Uploads an array of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member this.UpdateDiscs(discs: DiscInstance3D array) =
        this.UpdateDiscs (ReadOnlySpan<DiscInstance3D>(discs))

    /// <summary>Destroys the GPU disc instance buffer.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull instanceBuffer) then
                backend.DestroyBuffer instanceBuffer
                instanceBuffer <- null

    interface IDiscDraw with
        member _.InstanceBuffer = instanceBuffer
        member _.ActiveBytes = max 1 count * Packing.DiscBytes
        member _.Count = count

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()
