namespace Ranvier.Wgpu

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop
open Ranvier.Wgpu.Native

#nowarn "9"
#nowarn "51"

module private BufferHelpers =
    let createBuffer (device: nativeint) (usage: uint64) (bytes: uint64) (label: string) : nativeint =
        let mutable desc = WGPUBufferDescriptor ()
        desc.Usage <- usage
        desc.Size <- bytes
        let buf = wgpuDeviceCreateBuffer (device, addrOf &desc)

        if buf = 0n then
            failwith $"wgpuDeviceCreateBuffer ({label}) failed."

        buf

    let releaseBuffer (buf: nativeint) =
        if buf <> 0n then
            wgpuBufferDestroy buf
            wgpuBufferRelease buf

    let inline vertexBytesU64 (count: int) =
        uint64 count * uint64 sizeof<Vertex3D>

    let inline vertexBytesNative (count: int) =
        unativeint count * unativeint sizeof<Vertex3D>

    let inline indexBytesNative (count: int) =
        unativeint count * unativeint sizeof<uint32>

    let inline alignedIndexBytesU64 (count: uint64) =
        let raw = max 4uL (count * uint64 sizeof<uint32>)
        (raw + 3uL) &&& ~~~3uL

module private MeshValidation =
    let validate (mesh: MeshData) =
        if isNull (box mesh) then
            nullArg (nameof mesh)

        if isNull mesh.Vertices then
            nullArg "mesh.Vertices"

        if isNull mesh.Indices then
            nullArg "mesh.Indices"

[<Sealed>]
type private DynamicBatchBuffer<'T when 'T: unmanaged>(device: nativeint, queue: nativeint, minCapacity: int, minActiveCount: int, ownerName: string)
    =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable count = 0
    let mutable buffer = 0n
    let mutable shadow = Array.empty<'T>
    let stride = sizeof<'T>

    let ensureCapacity (needed: int) (preserveCount: int) : bool =
        let req = max minActiveCount needed

        if req > capacity || buffer = 0n then
            let newCap = max req (max minCapacity (capacity * 2))
            let bytes = uint64 newCap * uint64 stride

            let buf =
                BufferHelpers.createBuffer device (WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst) bytes ownerName

            let newShadow = Array.zeroCreate<'T> newCap

            if
                preserveCount > 0
                && shadow.Length >= preserveCount
            then
                Array.Copy (shadow, newShadow, preserveCount)
                let copyBytes = unativeint preserveCount * unativeint stride
                use p = fixed &newShadow[0]
                wgpuQueueWriteBuffer (queue, buf, 0uL, NativePtr.toNativeInt p, copyBytes)

            BufferHelpers.releaseBuffer buffer
            shadow <- newShadow
            buffer <- buf
            capacity <- newCap
            true
        else
            false

    member _.Count = count
    member _.Buffer = buffer
    member _.ActiveBytes = uint64 (max minActiveCount count) * uint64 stride

    member _.EnsureAlive() =
        if disposed then
            raise (ObjectDisposedException ownerName)

    member this.ReplaceAll(items: ReadOnlySpan<'T>, activeCount: int) =
        this.EnsureAlive ()
        let prevCount = count

        if activeCount > 0 then
            ensureCapacity activeCount 0 |> ignore
            items.Slice(0, activeCount).CopyTo(shadow.AsSpan (0, activeCount))

            if activeCount < prevCount then
                Array.Clear (shadow, activeCount, prevCount - activeCount)

            let byteLen = unativeint activeCount * unativeint stride
            use p = fixed &MemoryMarshal.GetReference (items)
            wgpuQueueWriteBuffer (queue, buffer, 0uL, NativePtr.toNativeInt p, byteLen)
        elif prevCount > 0 && shadow.Length > 0 then
            Array.Clear (shadow, 0, min prevCount shadow.Length)

        count <- activeCount

    member this.Resize(newCount: int) =
        this.EnsureAlive ()

        if newCount < 0 then
            raise (ArgumentOutOfRangeException (nameof newCount, newCount, $"{ownerName} count cannot be negative."))

        let prevCount = count

        if newCount > prevCount then
            let reallocated = ensureCapacity newCount prevCount

            if not reallocated && buffer <> 0n then
                Array.Clear (shadow, prevCount, newCount - prevCount)
                let offsetBytes = uint64 prevCount * uint64 stride

                let zeroBytes =
                    unativeint (newCount - prevCount)
                    * unativeint stride

                use p = fixed &shadow[prevCount]
                wgpuQueueWriteBuffer (queue, buffer, offsetBytes, NativePtr.toNativeInt p, zeroBytes)
        elif newCount < prevCount && shadow.Length > 0 then
            Array.Clear (shadow, newCount, prevCount - newCount)

        count <- newCount

    member this.UpdateRange(startIndex: int, items: ReadOnlySpan<'T>) =
        this.EnsureAlive ()

        if
            startIndex < 0
            || startIndex > count
            || items.Length > count - startIndex
        then
            raise (
                ArgumentOutOfRangeException (
                    nameof startIndex,
                    startIndex,
                    $"{ownerName} update range [{startIndex}, {int64 startIndex + int64 items.Length}) exceeds count {count}."
                )
            )

        if items.Length > 0 then
            ensureCapacity count count |> ignore
            items.CopyTo (shadow.AsSpan (startIndex, items.Length))
            let offsetBytes = uint64 startIndex * uint64 stride
            let byteLen = unativeint items.Length * unativeint stride
            use p = fixed &MemoryMarshal.GetReference (items)
            wgpuQueueWriteBuffer (queue, buffer, offsetBytes, NativePtr.toNativeInt p, byteLen)

    member _.Dispose() =
        if not disposed then
            disposed <- true
            BufferHelpers.releaseBuffer buffer
            buffer <- 0n
            shadow <- Array.empty

/// <summary>GPU-resident indexed triangle mesh uploaded to a <see cref="T:Ranvier.Wgpu.WgpuContext"/>.</summary>
[<Sealed>]
type MeshHandle internal (device: nativeint, queue: nativeint, mesh: MeshData) =
    do MeshValidation.validate mesh

    let mutable disposed = false
    let mutable vertexCount = mesh.Vertices.Length
    let mutable vertexCapacity = max 1 mesh.Vertices.Length
    let mutable indexCount = uint32 mesh.Indices.Length
    let mutable indexCapacity = max 1 mesh.Indices.Length
    let mutable triangleCount = mesh.Indices.Length / 3

    let mutable vertexBuffer =
        let byteLen = BufferHelpers.vertexBytesU64 vertexCapacity

        let buf =
            BufferHelpers.createBuffer device (WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst) byteLen "vertexBuffer"

        if mesh.Vertices.Length > 0 then
            use p = fixed mesh.Vertices
            wgpuQueueWriteBuffer (queue, buf, 0uL, NativePtr.toNativeInt p, BufferHelpers.vertexBytesNative mesh.Vertices.Length)

        buf

    let mutable indexBuffer =
        let alignedBytes = BufferHelpers.alignedIndexBytesU64 (uint64 indexCapacity)

        let buf =
            BufferHelpers.createBuffer device (WGPUBufferUsage_Index ||| WGPUBufferUsage_CopyDst) alignedBytes "indexBuffer"

        if mesh.Indices.Length > 0 then
            use p = fixed mesh.Indices
            wgpuQueueWriteBuffer (queue, buf, 0uL, NativePtr.toNativeInt p, BufferHelpers.indexBytesNative mesh.Indices.Length)

        buf

    let ensureAlive () =
        if disposed then
            raise (ObjectDisposedException (nameof MeshHandle))

    let ensureVertexCapacity (needed: int) =
        let req = max 1 needed

        if req > vertexCapacity || vertexBuffer = 0n then
            let newCap = max req (max 4 (vertexCapacity * 2))
            let bytes = BufferHelpers.vertexBytesU64 newCap

            let buf =
                BufferHelpers.createBuffer device (WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst) bytes "vertexBuffer"

            BufferHelpers.releaseBuffer vertexBuffer
            vertexBuffer <- buf
            vertexCapacity <- newCap

    let ensureIndexCapacity (needed: int) =
        let req = max 1 needed

        if req > indexCapacity || indexBuffer = 0n then
            let newCap = max req (max 4 (indexCapacity * 2))
            let alignedBytes = BufferHelpers.alignedIndexBytesU64 (uint64 newCap)

            let buf =
                BufferHelpers.createBuffer device (WGPUBufferUsage_Index ||| WGPUBufferUsage_CopyDst) alignedBytes "indexBuffer"

            BufferHelpers.releaseBuffer indexBuffer
            indexBuffer <- buf
            indexCapacity <- newCap

    /// <summary>Total vertex count in the mesh vertex buffer.</summary>
    member _.VertexCount = vertexCount

    /// <summary>Total index count in the mesh index buffer.</summary>
    member _.IndexCount = indexCount

    /// <summary>Total triangle count (`IndexCount / 3`).</summary>
    member _.TriangleCount = triangleCount

    member internal _.VertexBuffer = vertexBuffer
    member internal _.IndexBuffer = indexBuffer

    member internal _.VertexBytes = BufferHelpers.vertexBytesU64 (max 1 vertexCount)

    member internal _.IndexBytes = BufferHelpers.alignedIndexBytesU64 (uint64 indexCount)

    /// <summary>Replaces all vertices in the mesh vertex buffer in place, growing the GPU buffer if needed.</summary>
    member _.UpdateVertices(vertices: ReadOnlySpan<Vertex3D>) =
        ensureAlive ()

        if vertices.Length > 0 then
            ensureVertexCapacity vertices.Length
            let byteLen = BufferHelpers.vertexBytesNative vertices.Length
            use p = fixed &MemoryMarshal.GetReference (vertices)
            wgpuQueueWriteBuffer (queue, vertexBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

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
                    startVertex,
                    $"MeshHandle vertex range [{startVertex}, {int64 startVertex + int64 vertices.Length}) exceeds VertexCount {vertexCount}."
                )
            )

        if vertices.Length > 0 then
            let offsetBytes = BufferHelpers.vertexBytesU64 startVertex
            let byteLen = BufferHelpers.vertexBytesNative vertices.Length
            use p = fixed &MemoryMarshal.GetReference (vertices)
            wgpuQueueWriteBuffer (queue, vertexBuffer, offsetBytes, NativePtr.toNativeInt p, byteLen)

    /// <summary>Updates a contiguous sub-range of vertices starting at `startVertex` from an array.</summary>
    member this.UpdateVertexRange(startVertex: int, vertices: Vertex3D array) =
        this.UpdateVertexRange (startVertex, ReadOnlySpan<Vertex3D>(vertices))

    /// <summary>Replaces both vertex and index buffers from `mesh`, growing GPU buffers by doubling when capacity is exceeded.</summary>
    member _.UpdateMesh(mesh: MeshData) =
        ensureAlive ()
        MeshValidation.validate mesh

        if mesh.Vertices.Length > 0 then
            ensureVertexCapacity mesh.Vertices.Length
            let byteLen = BufferHelpers.vertexBytesNative mesh.Vertices.Length
            use p = fixed mesh.Vertices
            wgpuQueueWriteBuffer (queue, vertexBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

        if mesh.Indices.Length > 0 then
            ensureIndexCapacity mesh.Indices.Length
            let byteLen = BufferHelpers.indexBytesNative mesh.Indices.Length
            use p = fixed mesh.Indices
            wgpuQueueWriteBuffer (queue, indexBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

        vertexCount <- mesh.Vertices.Length
        indexCount <- uint32 mesh.Indices.Length
        triangleCount <- mesh.Indices.Length / 3

    /// <summary>Destroys and releases the GPU vertex and index buffers.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true
            BufferHelpers.releaseBuffer vertexBuffer
            vertexBuffer <- 0n
            BufferHelpers.releaseBuffer indexBuffer
            indexBuffer <- 0n

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced GPU draw batch bound to a <see cref="T:Ranvier.Wgpu.MeshHandle"/>.
/// Supports updating `Instance3D` transforms dynamically without recreating mesh geometry.
/// </summary>
[<Sealed>]
type InstanceBatch internal (device: nativeint, queue: nativeint, mesh: MeshHandle, blendMode: BlendMode, doubleSided: bool) =
    let buf = DynamicBatchBuffer<Instance3D>(device, queue, 16, 1, nameof InstanceBatch)

    /// <summary>Mesh geometry instanced by this batch.</summary>
    member _.Mesh = mesh

    /// <summary>Opaque or premultiplied alpha-blend pass selection.</summary>
    member _.BlendMode = blendMode

    /// <summary>True when back-face culling is disabled for this batch.</summary>
    member _.DoubleSided = doubleSided

    /// <summary>Active instance count uploaded to the GPU.</summary>
    member _.Count = buf.Count

    member internal _.InstanceBuffer = buf.Buffer
    member internal _.ActiveBytes = buf.ActiveBytes

    /// <summary>Uploads a contiguous span of `Instance3D` records to the GPU instance buffer.</summary>
    member _.UpdateInstances(instances: ReadOnlySpan<Instance3D>) =
        buf.ReplaceAll (instances, instances.Length)

    /// <summary>Uploads an array of `Instance3D` records to the GPU instance buffer.</summary>
    member this.UpdateInstances(instances: Instance3D array) =
        this.UpdateInstances (ReadOnlySpan<Instance3D>(instances))

    /// <summary>Resizes the active instance count while preserving existing prefix slots across capacity growth.</summary>
    member _.Resize(count: int) =
        buf.Resize count

    /// <summary>Updates a contiguous sub-range of `Instance3D` records starting at `startIndex`.</summary>
    member _.UpdateRange(startIndex: int, instances: ReadOnlySpan<Instance3D>) =
        buf.UpdateRange (startIndex, instances)

    /// <summary>Updates a contiguous sub-range of `Instance3D` records from an array starting at `startIndex`.</summary>
    member this.UpdateRange(startIndex: int, instances: Instance3D array) =
        this.UpdateRange (startIndex, ReadOnlySpan<Instance3D>(instances))

    /// <summary>Uploads a single `Instance3D` record without allocating an array.</summary>
    member this.SetSingle(instance: Instance3D) =
        let mutable tmp = instance
        this.UpdateInstances (MemoryMarshal.CreateReadOnlySpan (&tmp, 1))

    /// <summary>Destroys and releases the GPU instance buffer.</summary>
    member _.Dispose() =
        buf.Dispose ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware 4x-MSAA premultiplied alpha-blended 3D hairline batch (`LineList`).
/// Consecutive vertex pairs `(2k, 2k+1)` form independent 3D line segments.
/// </summary>
[<Sealed>]
type LineBatch internal (device: nativeint, queue: nativeint) =
    let buf = DynamicBatchBuffer<LineVertex3D>(device, queue, 64, 2, nameof LineBatch)

    /// <summary>Active endpoint vertex count (always rounded down to an even number).</summary>
    member _.VertexCount = buf.Count

    /// <summary>Active line segment count (`VertexCount / 2`).</summary>
    member _.SegmentCount = buf.Count / 2

    member internal _.VertexBuffer = buf.Buffer
    member internal _.ActiveBytes = buf.ActiveBytes

    /// <summary>Uploads a contiguous span of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member _.UpdateVertices(vertices: ReadOnlySpan<LineVertex3D>) =
        let evenLen = vertices.Length &&& ~~~1
        buf.ReplaceAll (vertices, evenLen)

    /// <summary>Uploads an array of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member this.UpdateVertices(vertices: LineVertex3D array) =
        this.UpdateVertices (ReadOnlySpan<LineVertex3D>(vertices))

    /// <summary>Resizes the active endpoint vertex count (must be non-negative and even) while preserving existing prefix vertices.</summary>
    member _.Resize(vertexCount: int) =
        buf.EnsureAlive ()

        if vertexCount < 0 then
            raise (ArgumentOutOfRangeException (nameof vertexCount, vertexCount, "LineBatch vertexCount cannot be negative."))

        if (vertexCount &&& 1) <> 0 then
            invalidArg (nameof vertexCount) $"LineBatch vertexCount must be even, got {vertexCount}."

        buf.Resize vertexCount

    /// <summary>Updates a contiguous sub-range of `LineVertex3D` endpoints starting at an even `startVertex`.</summary>
    member _.UpdateRange(startVertex: int, vertices: ReadOnlySpan<LineVertex3D>) =
        buf.EnsureAlive ()

        if startVertex < 0 then
            raise (ArgumentOutOfRangeException (nameof startVertex, startVertex, "LineBatch startVertex cannot be negative."))

        if (startVertex &&& 1) <> 0 then
            invalidArg (nameof startVertex) $"LineBatch startVertex must be even, got {startVertex}."

        if (vertices.Length &&& 1) <> 0 then
            invalidArg (nameof vertices) $"LineBatch UpdateRange requires an even number of vertices, got {vertices.Length}."

        buf.UpdateRange (startVertex, vertices)

    /// <summary>Updates a contiguous sub-range of `LineVertex3D` endpoints from an array starting at an even `startVertex`.</summary>
    member this.UpdateRange(startVertex: int, vertices: LineVertex3D array) =
        this.UpdateRange (startVertex, ReadOnlySpan<LineVertex3D>(vertices))

    /// <summary>Destroys and releases the GPU line vertex buffer.</summary>
    member _.Dispose() =
        buf.Dispose ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced camera-facing SDF disc and verification-arc billboard batch (`DiscInstance3D`).
/// Supports both anti-aliased visual rendering and 1x1 GPU picking.
/// </summary>
[<Sealed>]
type DiscBatch internal (device: nativeint, queue: nativeint) =
    let buf = DynamicBatchBuffer<DiscInstance3D>(device, queue, 32, 1, nameof DiscBatch)

    /// <summary>Active billboard disc count uploaded to the GPU.</summary>
    member _.Count = buf.Count

    member internal _.InstanceBuffer = buf.Buffer
    member internal _.ActiveBytes = buf.ActiveBytes

    /// <summary>Uploads a contiguous span of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member _.UpdateDiscs(discs: ReadOnlySpan<DiscInstance3D>) =
        buf.ReplaceAll (discs, discs.Length)

    /// <summary>Uploads an array of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member this.UpdateDiscs(discs: DiscInstance3D array) =
        this.UpdateDiscs (ReadOnlySpan<DiscInstance3D>(discs))

    /// <summary>Resizes the active disc count while preserving existing prefix slots across capacity growth.</summary>
    member _.Resize(count: int) =
        buf.Resize count

    /// <summary>Updates a contiguous sub-range of `DiscInstance3D` records starting at `startIndex`.</summary>
    member _.UpdateRange(startIndex: int, discs: ReadOnlySpan<DiscInstance3D>) =
        buf.UpdateRange (startIndex, discs)

    /// <summary>Updates a contiguous sub-range of `DiscInstance3D` records from an array starting at `startIndex`.</summary>
    member this.UpdateRange(startIndex: int, discs: DiscInstance3D array) =
        this.UpdateRange (startIndex, ReadOnlySpan<DiscInstance3D>(discs))

    /// <summary>Destroys and releases the GPU disc instance buffer.</summary>
    member _.Dispose() =
        buf.Dispose ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()
