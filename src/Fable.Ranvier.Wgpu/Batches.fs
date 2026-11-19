namespace Ranvier.Wgpu

open System
open Fable.Core

[<Sealed>]
type private DynamicBatchBuffer
    (backend: ISceneBackend, staging: PackBuffer, strideBytes: int, minCapacity: int, minActiveCount: int, ownerName: string) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable count = 0
    let mutable buffer: IBackendBuffer = null
    let mutable shadowBuffer: JS.ArrayBuffer = null
    let mutable shadowU32: JS.Uint32Array = Unchecked.defaultof<JS.Uint32Array>

    let zeroWords (u32: JS.Uint32Array) (startByte: int) (byteCount: int) =
        let startWord = startByte >>> 2
        let wordCount = byteCount >>> 2

        for i = 0 to wordCount - 1 do
            u32[startWord + i] <- 0u

    let copyIntoShadow (dstStartByte: int) (byteCount: int) =
        let dstWord = dstStartByte >>> 2
        let wordCount = byteCount >>> 2
        let src = staging.U32

        for i = 0 to wordCount - 1 do
            shadowU32[dstWord + i] <- src[i]

    let ensureCapacity (needed: int) (preserveCount: int) : bool =
        let req = max minActiveCount needed

        if req > capacity || isNull buffer then
            let newCap = max req (max minCapacity (capacity * 2))
            let newBuf = backend.CreateVertexBuffer (newCap * strideBytes)
            let newShadowBuf = JS.Constructors.ArrayBuffer.Create (newCap * strideBytes)
            let newShadowU32 = JS.Constructors.Uint32Array.Create newShadowBuf

            if preserveCount > 0 && not (isNull shadowBuffer) then
                let preserveBytes = preserveCount * strideBytes
                let preserveWords = preserveBytes >>> 2

                for i = 0 to preserveWords - 1 do
                    newShadowU32[i] <- shadowU32[i]

                backend.WriteBuffer (newBuf, newShadowBuf, preserveBytes)

            if not (isNull buffer) then
                backend.DestroyBuffer buffer

            buffer <- newBuf
            shadowBuffer <- newShadowBuf
            shadowU32 <- newShadowU32
            capacity <- newCap
            true
        else
            false

    member _.Count = count
    member _.Buffer = buffer
    member _.ActiveBytes = max minActiveCount count * strideBytes

    member _.EnsureAlive() =
        if disposed then
            raise (ObjectDisposedException ownerName)

    member this.BeginReplace(activeCount: int) =
        this.EnsureAlive ()

        if activeCount > 0 then
            ensureCapacity activeCount 0 |> ignore

    member _.EndReplace(activeCount: int, writtenBytes: int) =
        let prevCount = count

        if activeCount > 0 then
            copyIntoShadow 0 writtenBytes

            if activeCount < prevCount then
                zeroWords shadowU32 (activeCount * strideBytes) ((prevCount - activeCount) * strideBytes)

            backend.WriteBuffer (buffer, staging.Buffer, writtenBytes)
        elif prevCount > 0 && not (isNull shadowBuffer) then
            zeroWords shadowU32 0 (min prevCount capacity * strideBytes)

        count <- activeCount

    member this.Resize(newCount: int) =
        this.EnsureAlive ()

        if newCount < 0 then
            raise (ArgumentOutOfRangeException (nameof newCount, $"{ownerName} count cannot be negative."))

        let prevCount = count

        if newCount > prevCount then
            let reallocated = ensureCapacity newCount prevCount

            if not reallocated && not (isNull buffer) then
                let offsetBytes = prevCount * strideBytes
                let zeroBytes = (newCount - prevCount) * strideBytes
                zeroWords shadowU32 offsetBytes zeroBytes
                Packing.ensure staging zeroBytes
                zeroWords staging.U32 0 zeroBytes
                backend.WriteBufferAt (buffer, offsetBytes, staging.Buffer, zeroBytes)
        elif newCount < prevCount && not (isNull shadowBuffer) then
            zeroWords shadowU32 (newCount * strideBytes) ((prevCount - newCount) * strideBytes)

        count <- newCount

    member this.BeginUpdateRange(startIndex: int, itemCount: int) : bool =
        this.EnsureAlive ()

        if
            startIndex < 0
            || startIndex > count
            || itemCount > count - startIndex
        then
            raise (
                ArgumentOutOfRangeException (
                    nameof startIndex,
                    $"{ownerName} update range [{startIndex}, {int64 startIndex + int64 itemCount}) exceeds count {count}."
                )
            )

        if itemCount > 0 then
            ensureCapacity count count |> ignore
            true
        else
            false

    member _.EndUpdateRange(startIndex: int, writtenBytes: int) =
        let offsetBytes = startIndex * strideBytes
        copyIntoShadow offsetBytes writtenBytes
        backend.WriteBufferAt (buffer, offsetBytes, staging.Buffer, writtenBytes)

    member _.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull buffer) then
                backend.DestroyBuffer buffer
                buffer <- null

            shadowBuffer <- null
            shadowU32 <- Unchecked.defaultof<JS.Uint32Array>

module private MeshValidation =
    let validate (mesh: MeshData) =
        if isNull (box mesh) then
            nullArg (nameof mesh)

        if isNull mesh.Vertices then
            nullArg "mesh.Vertices"

        if isNull mesh.Indices then
            nullArg "mesh.Indices"

/// <summary>GPU-resident indexed triangle mesh uploaded to a <see cref="T:Ranvier.Wgpu.WgpuContext"/>.</summary>
[<Sealed>]
type MeshHandle internal (backend: ISceneBackend, staging: PackBuffer, mesh: MeshData) =
    do MeshValidation.validate mesh

    let asUint32View (indices: uint32 array) : uint32 array =
        // A project compiled without typed arrays has a plain array here.
        if JS.Constructors.ArrayBuffer.isView indices then
            indices
        else
            unbox (JS.Constructors.Uint32Array.Create (box indices))

    let mutable disposed = false
    let mutable vertexCount = mesh.Vertices.Length
    let mutable vertexCapacity = max 1 mesh.Vertices.Length
    let mutable indexCount = mesh.Indices.Length
    let mutable indexCapacity = max 1 mesh.Indices.Length
    let mutable triangleCount = mesh.Indices.Length / 3

    let mutable vertexBuffer =
        let buffer = backend.CreateVertexBuffer (vertexCapacity * Packing.VertexBytes)

        if mesh.Vertices.Length > 0 then
            let written = Packing.vertices staging (ReadOnlySpan<Vertex3D>(mesh.Vertices))
            backend.WriteBuffer (buffer, staging.Buffer, written)

        buffer

    let mutable indexBuffer =
        let alignedBytes = max 4 (indexCapacity * 4)
        let buffer = backend.CreateIndexBuffer alignedBytes

        if mesh.Indices.Length > 0 then
            backend.WriteIndices (buffer, asUint32View mesh.Indices)

        buffer

    let ensureAlive () =
        if disposed then
            raise (ObjectDisposedException (nameof MeshHandle))

    let ensureVertexCapacity (needed: int) =
        let req = max 1 needed

        if req > vertexCapacity || isNull vertexBuffer then
            let newCap = max req (max 4 (vertexCapacity * 2))
            let buf = backend.CreateVertexBuffer (newCap * Packing.VertexBytes)

            if not (isNull vertexBuffer) then
                backend.DestroyBuffer vertexBuffer

            vertexBuffer <- buf
            vertexCapacity <- newCap

    let ensureIndexCapacity (needed: int) =
        let req = max 1 needed

        if req > indexCapacity || isNull indexBuffer then
            let newCap = max req (max 4 (indexCapacity * 2))
            let buf = backend.CreateIndexBuffer (max 4 (newCap * 4))

            if not (isNull indexBuffer) then
                backend.DestroyBuffer indexBuffer

            indexBuffer <- buf
            indexCapacity <- newCap

    /// <summary>Total vertex count in the mesh vertex buffer.</summary>
    member _.VertexCount = vertexCount

    /// <summary>Total index count in the mesh index buffer.</summary>
    member _.IndexCount = uint32 indexCount

    /// <summary>Total triangle count (`IndexCount / 3`).</summary>
    member _.TriangleCount = triangleCount

    /// <summary>Replaces all vertices in the mesh vertex buffer in place, growing the GPU buffer if needed.</summary>
    member _.UpdateVertices(vertices: ReadOnlySpan<Vertex3D>) =
        ensureAlive ()

        if vertices.Length > 0 then
            ensureVertexCapacity vertices.Length
            let written = Packing.vertices staging vertices
            backend.WriteBuffer (vertexBuffer, staging.Buffer, written)

        vertexCount <- vertices.Length

    /// <summary>Replaces all vertices in the mesh vertex buffer in place from an array, growing the GPU buffer if needed.</summary>
    member this.UpdateVertices(vertices: Vertex3D array) =
        this.UpdateVertices (ReadOnlySpan<Vertex3D>(vertices))

    /// <summary>Updates a contiguous sub-range of vertices starting at `startVertex` in the GPU vertex buffer.</summary>
    member _.UpdateVertexRange(startVertex: int, vertices: ReadOnlySpan<Vertex3D>) =
        ensureAlive ()

        if
            startVertex < 0
            || startVertex > vertexCount
            || vertices.Length > vertexCount - startVertex
        then
            raise (
                ArgumentOutOfRangeException (
                    nameof startVertex,
                    $"MeshHandle vertex range [{startVertex}, {int64 startVertex + int64 vertices.Length}) exceeds VertexCount {vertexCount}."
                )
            )

        if vertices.Length > 0 then
            let written = Packing.vertices staging vertices
            backend.WriteBufferAt (vertexBuffer, startVertex * Packing.VertexBytes, staging.Buffer, written)

    /// <summary>Updates a contiguous sub-range of vertices starting at `startVertex` from an array.</summary>
    member this.UpdateVertexRange(startVertex: int, vertices: Vertex3D array) =
        this.UpdateVertexRange (startVertex, ReadOnlySpan<Vertex3D>(vertices))

    /// <summary>Replaces both vertex and index buffers from `mesh`, growing GPU buffers by doubling when capacity is exceeded.</summary>
    member _.UpdateMesh(mesh: MeshData) =
        ensureAlive ()
        MeshValidation.validate mesh

        if mesh.Vertices.Length > 0 then
            ensureVertexCapacity mesh.Vertices.Length
            let written = Packing.vertices staging (ReadOnlySpan<Vertex3D>(mesh.Vertices))
            backend.WriteBuffer (vertexBuffer, staging.Buffer, written)

        if mesh.Indices.Length > 0 then
            ensureIndexCapacity mesh.Indices.Length
            backend.WriteIndices (indexBuffer, asUint32View mesh.Indices)

        vertexCount <- mesh.Vertices.Length
        indexCount <- mesh.Indices.Length
        triangleCount <- mesh.Indices.Length / 3

    /// <summary>Destroys the GPU vertex and index buffers.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull vertexBuffer) then
                backend.DestroyBuffer vertexBuffer
                vertexBuffer <- null

            if not (isNull indexBuffer) then
                backend.DestroyBuffer indexBuffer
                indexBuffer <- null

    interface IMeshDraw with
        member _.VertexBuffer = vertexBuffer
        member _.VertexBytes = max 1 vertexCount * Packing.VertexBytes
        member _.IndexBuffer = indexBuffer
        member _.IndexBytes = max 4 (indexCount * 4)
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
    let buf =
        DynamicBatchBuffer (backend, staging, Packing.InstanceBytes, 16, 1, nameof InstanceBatch)

    let single = [| Instance3D () |]
    let alphaBlend = blendMode = BlendMode.AlphaBlend

    /// <summary>Mesh geometry instanced by this batch.</summary>
    member _.Mesh = mesh

    /// <summary>Opaque or premultiplied alpha-blend pass selection.</summary>
    member _.BlendMode = blendMode

    /// <summary>True when back-face culling is disabled for this batch.</summary>
    member _.DoubleSided = doubleSided

    /// <summary>Active instance count uploaded to the GPU.</summary>
    member _.Count = buf.Count

    /// <summary>Uploads a contiguous span of `Instance3D` records to the GPU instance buffer.</summary>
    member _.UpdateInstances(instances: ReadOnlySpan<Instance3D>) =
        buf.BeginReplace instances.Length

        let written =
            if instances.Length > 0 then
                Packing.instances staging instances
            else
                0

        buf.EndReplace (instances.Length, written)

    /// <summary>Uploads an array of `Instance3D` records to the GPU instance buffer.</summary>
    member this.UpdateInstances(instances: Instance3D array) =
        this.UpdateInstances (ReadOnlySpan<Instance3D>(instances))

    /// <summary>Resizes the active instance count while preserving existing prefix slots across capacity growth.</summary>
    member _.Resize(count: int) =
        buf.Resize count

    /// <summary>Updates a contiguous sub-range of `Instance3D` records starting at `startIndex`.</summary>
    member _.UpdateRange(startIndex: int, instances: ReadOnlySpan<Instance3D>) =
        if buf.BeginUpdateRange (startIndex, instances.Length) then
            let written = Packing.instances staging instances
            buf.EndUpdateRange (startIndex, written)

    /// <summary>Updates a contiguous sub-range of `Instance3D` records from an array starting at `startIndex`.</summary>
    member this.UpdateRange(startIndex: int, instances: Instance3D array) =
        this.UpdateRange (startIndex, ReadOnlySpan<Instance3D>(instances))

    /// <summary>Uploads a single `Instance3D` record without allocating an array.</summary>
    member this.SetSingle(instance: Instance3D) =
        single[0] <- instance
        this.UpdateInstances (ReadOnlySpan<Instance3D>(single))

    /// <summary>Destroys the GPU instance buffer.</summary>
    member _.Dispose() =
        buf.Dispose ()

    interface IInstanceDraw with
        member _.Mesh = mesh :> IMeshDraw
        member _.InstanceBuffer = buf.Buffer
        member _.ActiveBytes = buf.ActiveBytes
        member _.Count = buf.Count
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
    let buf =
        DynamicBatchBuffer (backend, staging, Packing.LineVertexBytes, 64, 2, nameof LineBatch)

    /// <summary>Active endpoint vertex count (always rounded down to an even number).</summary>
    member _.VertexCount = buf.Count

    /// <summary>Active line segment count (`VertexCount / 2`).</summary>
    member _.SegmentCount = buf.Count / 2

    /// <summary>Uploads a contiguous span of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member _.UpdateVertices(vertices: ReadOnlySpan<LineVertex3D>) =
        let evenLength = vertices.Length &&& ~~~1
        buf.BeginReplace evenLength

        let written =
            if evenLength > 0 then
                Packing.lineVertices staging vertices evenLength
            else
                0

        buf.EndReplace (evenLength, written)

    /// <summary>Uploads an array of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member this.UpdateVertices(vertices: LineVertex3D array) =
        this.UpdateVertices (ReadOnlySpan<LineVertex3D>(vertices))

    /// <summary>Resizes the active endpoint vertex count (must be non-negative and even) while preserving existing prefix vertices.</summary>
    member _.Resize(vertexCount: int) =
        buf.EnsureAlive ()

        if vertexCount < 0 then
            raise (ArgumentOutOfRangeException (nameof vertexCount, "LineBatch vertexCount cannot be negative."))

        if (vertexCount &&& 1) <> 0 then
            invalidArg (nameof vertexCount) $"LineBatch vertexCount must be even, got {vertexCount}."

        buf.Resize vertexCount

    /// <summary>Updates a contiguous sub-range of `LineVertex3D` endpoints starting at an even `startVertex`.</summary>
    member _.UpdateRange(startVertex: int, vertices: ReadOnlySpan<LineVertex3D>) =
        buf.EnsureAlive ()

        if startVertex < 0 then
            raise (ArgumentOutOfRangeException (nameof startVertex, "LineBatch startVertex cannot be negative."))

        if (startVertex &&& 1) <> 0 then
            invalidArg (nameof startVertex) $"LineBatch startVertex must be even, got {startVertex}."

        if (vertices.Length &&& 1) <> 0 then
            invalidArg (nameof vertices) $"LineBatch UpdateRange requires an even number of vertices, got {vertices.Length}."

        if buf.BeginUpdateRange (startVertex, vertices.Length) then
            let written = Packing.lineVertices staging vertices vertices.Length
            buf.EndUpdateRange (startVertex, written)

    /// <summary>Updates a contiguous sub-range of `LineVertex3D` endpoints from an array starting at an even `startVertex`.</summary>
    member this.UpdateRange(startVertex: int, vertices: LineVertex3D array) =
        this.UpdateRange (startVertex, ReadOnlySpan<LineVertex3D>(vertices))

    /// <summary>Destroys the GPU line vertex buffer.</summary>
    member _.Dispose() =
        buf.Dispose ()

    interface ILineDraw with
        member _.VertexBuffer = buf.Buffer
        member _.ActiveBytes = buf.ActiveBytes
        member _.VertexCount = buf.Count

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced camera-facing SDF disc and verification-arc billboard batch (`DiscInstance3D`).
/// Supports both anti-aliased visual rendering and 1x1 GPU picking.
/// </summary>
[<Sealed>]
type DiscBatch internal (backend: ISceneBackend, staging: PackBuffer) =
    let buf =
        DynamicBatchBuffer (backend, staging, Packing.DiscBytes, 32, 1, nameof DiscBatch)

    /// <summary>Active billboard disc count uploaded to the GPU.</summary>
    member _.Count = buf.Count

    /// <summary>Uploads a contiguous span of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member _.UpdateDiscs(discs: ReadOnlySpan<DiscInstance3D>) =
        buf.BeginReplace discs.Length

        let written = if discs.Length > 0 then Packing.discs staging discs else 0

        buf.EndReplace (discs.Length, written)

    /// <summary>Uploads an array of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member this.UpdateDiscs(discs: DiscInstance3D array) =
        this.UpdateDiscs (ReadOnlySpan<DiscInstance3D>(discs))

    /// <summary>Resizes the active disc count while preserving existing prefix slots across capacity growth.</summary>
    member _.Resize(count: int) =
        buf.Resize count

    /// <summary>Updates a contiguous sub-range of `DiscInstance3D` records starting at `startIndex`.</summary>
    member _.UpdateRange(startIndex: int, discs: ReadOnlySpan<DiscInstance3D>) =
        if buf.BeginUpdateRange (startIndex, discs.Length) then
            let written = Packing.discs staging discs
            buf.EndUpdateRange (startIndex, written)

    /// <summary>Updates a contiguous sub-range of `DiscInstance3D` records from an array starting at `startIndex`.</summary>
    member this.UpdateRange(startIndex: int, discs: DiscInstance3D array) =
        this.UpdateRange (startIndex, ReadOnlySpan<DiscInstance3D>(discs))

    /// <summary>Destroys the GPU disc instance buffer.</summary>
    member _.Dispose() =
        buf.Dispose ()

    interface IDiscDraw with
        member _.InstanceBuffer = buf.Buffer
        member _.ActiveBytes = buf.ActiveBytes
        member _.Count = buf.Count

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()
