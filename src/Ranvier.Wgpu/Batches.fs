namespace Ranvier.Wgpu

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop
open Ranvier.Wgpu.Native

#nowarn "9"
#nowarn "51"

/// <summary>GPU-resident indexed triangle mesh uploaded to a <see cref="T:Ranvier.Wgpu.WgpuContext"/>.</summary>
[<Sealed>]
type MeshHandle internal (device: nativeint, queue: nativeint, mesh: MeshData) =
    let mutable disposed = false
    let indexCount = uint32 mesh.Indices.Length
    let triangleCount = mesh.Indices.Length / 3

    let vertexBuffer =
        let byteLen = uint64 (max 1 mesh.Vertices.Length * sizeof<Vertex3D>)
        let mutable desc = WGPUBufferDescriptor ()
        desc.Usage <- WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst
        desc.Size <- byteLen
        let buf = wgpuDeviceCreateBuffer (device, addrOf &desc)

        if buf = 0n then
            failwith "wgpuDeviceCreateBuffer (vertexBuffer) failed."

        if mesh.Vertices.Length > 0 then
            use p = fixed mesh.Vertices
            wgpuQueueWriteBuffer (queue, buf, 0uL, NativePtr.toNativeInt p, unativeint byteLen)

        buf

    let indexBuffer =
        let rawBytes = max 4 (mesh.Indices.Length * sizeof<uint32>)
        let alignedBytes = (rawBytes + 3) &&& ~~~3
        let mutable desc = WGPUBufferDescriptor ()
        desc.Usage <- WGPUBufferUsage_Index ||| WGPUBufferUsage_CopyDst
        desc.Size <- uint64 alignedBytes
        let buf = wgpuDeviceCreateBuffer (device, addrOf &desc)

        if buf = 0n then
            failwith "wgpuDeviceCreateBuffer (indexBuffer) failed."

        if mesh.Indices.Length > 0 then
            use p = fixed mesh.Indices
            wgpuQueueWriteBuffer (queue, buf, 0uL, NativePtr.toNativeInt p, unativeint (mesh.Indices.Length * sizeof<uint32>))

        buf

    /// <summary>Total index count in the mesh index buffer.</summary>
    member _.IndexCount = indexCount

    /// <summary>Total triangle count (`IndexCount / 3`).</summary>
    member _.TriangleCount = triangleCount

    member internal _.VertexBuffer = vertexBuffer
    member internal _.IndexBuffer = indexBuffer
    member internal _.VertexBytes = uint64 (max 1 mesh.Vertices.Length * sizeof<Vertex3D>)

    member internal _.IndexBytes =
        uint64 (
            ((max 4 (mesh.Indices.Length * sizeof<uint32>)) + 3)
            &&& ~~~3
        )

    /// <summary>Destroys and releases the GPU vertex and index buffers.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if vertexBuffer <> 0n then
                wgpuBufferDestroy vertexBuffer
                wgpuBufferRelease vertexBuffer

            if indexBuffer <> 0n then
                wgpuBufferDestroy indexBuffer
                wgpuBufferRelease indexBuffer

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced GPU draw batch bound to a <see cref="T:Ranvier.Wgpu.MeshHandle"/>.
/// Supports updating `Instance3D` transforms dynamically without recreating mesh geometry.
/// </summary>
[<Sealed>]
type InstanceBatch internal (device: nativeint, queue: nativeint, mesh: MeshHandle, blendMode: BlendMode, doubleSided: bool) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable count = 0
    let mutable instanceBuffer = 0n

    let ensureCapacity (needed: int) =
        let req = max 1 needed

        if req > capacity || instanceBuffer = 0n then
            if instanceBuffer <> 0n then
                wgpuBufferDestroy instanceBuffer
                wgpuBufferRelease instanceBuffer

            let newCap = max req (max 16 (capacity * 2))
            let bytes = uint64 newCap * uint64 sizeof<Instance3D>
            let mutable desc = WGPUBufferDescriptor ()
            desc.Usage <- WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst
            desc.Size <- bytes
            let buf = wgpuDeviceCreateBuffer (device, addrOf &desc)

            if buf = 0n then
                failwith "wgpuDeviceCreateBuffer (instanceBuffer) failed."

            instanceBuffer <- buf
            capacity <- newCap

    /// <summary>Mesh geometry instanced by this batch.</summary>
    member _.Mesh = mesh

    /// <summary>Opaque or premultiplied alpha-blend pass selection.</summary>
    member _.BlendMode = blendMode

    /// <summary>True when back-face culling is disabled for this batch.</summary>
    member _.DoubleSided = doubleSided

    /// <summary>Active instance count uploaded to the GPU.</summary>
    member _.Count = count

    member internal _.InstanceBuffer = instanceBuffer
    member internal _.ActiveBytes = uint64 (max 1 count) * uint64 sizeof<Instance3D>

    /// <summary>Uploads a contiguous span of `Instance3D` records to the GPU instance buffer.</summary>
    member _.UpdateInstances(instances: ReadOnlySpan<Instance3D>) =
        if disposed then
            raise (ObjectDisposedException (nameof InstanceBatch))

        count <- instances.Length

        if instances.Length > 0 then
            ensureCapacity instances.Length
            let byteLen = unativeint (instances.Length * sizeof<Instance3D>)
            use p = fixed &MemoryMarshal.GetReference (instances)
            wgpuQueueWriteBuffer (queue, instanceBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

    /// <summary>Uploads an array of `Instance3D` records to the GPU instance buffer.</summary>
    member this.UpdateInstances(instances: Instance3D array) =
        this.UpdateInstances (ReadOnlySpan<Instance3D>(instances))

    /// <summary>Uploads a single `Instance3D` record without allocating an array.</summary>
    member this.SetSingle(instance: Instance3D) =
        let mutable tmp = instance
        this.UpdateInstances (MemoryMarshal.CreateReadOnlySpan (&tmp, 1))

    /// <summary>Destroys and releases the GPU instance buffer.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if instanceBuffer <> 0n then
                wgpuBufferDestroy instanceBuffer
                wgpuBufferRelease instanceBuffer
                instanceBuffer <- 0n

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware 4x-MSAA premultiplied alpha-blended 3D hairline batch (`LineList`).
/// Consecutive vertex pairs `(2k, 2k+1)` form independent 3D line segments.
/// </summary>
[<Sealed>]
type LineBatch internal (device: nativeint, queue: nativeint) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable vertexCount = 0
    let mutable vertexBuffer = 0n

    let ensureCapacity (needed: int) =
        let req = max 2 needed

        if req > capacity || vertexBuffer = 0n then
            if vertexBuffer <> 0n then
                wgpuBufferDestroy vertexBuffer
                wgpuBufferRelease vertexBuffer

            let newCap = max req (max 64 (capacity * 2))
            let bytes = uint64 newCap * uint64 sizeof<LineVertex3D>
            let mutable desc = WGPUBufferDescriptor ()
            desc.Usage <- WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst
            desc.Size <- bytes
            let buf = wgpuDeviceCreateBuffer (device, addrOf &desc)

            if buf = 0n then
                failwith "wgpuDeviceCreateBuffer (LineBatch) failed."

            vertexBuffer <- buf
            capacity <- newCap

    /// <summary>Active endpoint vertex count (always rounded down to an even number).</summary>
    member _.VertexCount = vertexCount

    /// <summary>Active line segment count (`VertexCount / 2`).</summary>
    member _.SegmentCount = vertexCount / 2

    member internal _.VertexBuffer = vertexBuffer

    member internal _.ActiveBytes =
        uint64 (max 2 vertexCount)
        * uint64 sizeof<LineVertex3D>

    /// <summary>Uploads a contiguous span of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member _.UpdateVertices(vertices: ReadOnlySpan<LineVertex3D>) =
        if disposed then
            raise (ObjectDisposedException (nameof LineBatch))

        let evenLen = vertices.Length &&& ~~~1
        vertexCount <- evenLen

        if evenLen > 0 then
            ensureCapacity evenLen
            let byteLen = unativeint (evenLen * sizeof<LineVertex3D>)
            use p = fixed &MemoryMarshal.GetReference (vertices)
            wgpuQueueWriteBuffer (queue, vertexBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

    /// <summary>Uploads an array of `LineVertex3D` endpoints to the GPU vertex buffer.</summary>
    member this.UpdateVertices(vertices: LineVertex3D array) =
        this.UpdateVertices (ReadOnlySpan<LineVertex3D>(vertices))

    /// <summary>Destroys and releases the GPU line vertex buffer.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if vertexBuffer <> 0n then
                wgpuBufferDestroy vertexBuffer
                wgpuBufferRelease vertexBuffer
                vertexBuffer <- 0n

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>
/// Hardware-instanced camera-facing SDF disc and verification-arc billboard batch (`DiscInstance3D`).
/// Supports both anti-aliased visual rendering and 1x1 GPU picking.
/// </summary>
[<Sealed>]
type DiscBatch internal (device: nativeint, queue: nativeint) =
    let mutable disposed = false
    let mutable capacity = 0
    let mutable count = 0
    let mutable instanceBuffer = 0n

    let ensureCapacity (needed: int) =
        let req = max 1 needed

        if req > capacity || instanceBuffer = 0n then
            if instanceBuffer <> 0n then
                wgpuBufferDestroy instanceBuffer
                wgpuBufferRelease instanceBuffer

            let newCap = max req (max 32 (capacity * 2))
            let bytes = uint64 newCap * uint64 sizeof<DiscInstance3D>
            let mutable desc = WGPUBufferDescriptor ()
            desc.Usage <- WGPUBufferUsage_Vertex ||| WGPUBufferUsage_CopyDst
            desc.Size <- bytes
            let buf = wgpuDeviceCreateBuffer (device, addrOf &desc)

            if buf = 0n then
                failwith "wgpuDeviceCreateBuffer (DiscBatch) failed."

            instanceBuffer <- buf
            capacity <- newCap

    /// <summary>Active billboard disc count uploaded to the GPU.</summary>
    member _.Count = count

    member internal _.InstanceBuffer = instanceBuffer

    member internal _.ActiveBytes =
        uint64 (max 1 count)
        * uint64 sizeof<DiscInstance3D>

    /// <summary>Uploads a contiguous span of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member _.UpdateDiscs(discs: ReadOnlySpan<DiscInstance3D>) =
        if disposed then
            raise (ObjectDisposedException (nameof DiscBatch))

        count <- discs.Length

        if discs.Length > 0 then
            ensureCapacity discs.Length
            let byteLen = unativeint (discs.Length * sizeof<DiscInstance3D>)
            use p = fixed &MemoryMarshal.GetReference (discs)
            wgpuQueueWriteBuffer (queue, instanceBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

    /// <summary>Uploads an array of `DiscInstance3D` records to the GPU instance buffer.</summary>
    member this.UpdateDiscs(discs: DiscInstance3D array) =
        this.UpdateDiscs (ReadOnlySpan<DiscInstance3D>(discs))

    /// <summary>Destroys and releases the GPU disc instance buffer.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            if instanceBuffer <> 0n then
                wgpuBufferDestroy instanceBuffer
                wgpuBufferRelease instanceBuffer
                instanceBuffer <- 0n

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()
