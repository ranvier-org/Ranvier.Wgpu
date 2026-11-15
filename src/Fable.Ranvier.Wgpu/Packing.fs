namespace Ranvier.Wgpu

open System
open System.Numerics
open Fable.Core

/// <summary>
/// One <c>ArrayBuffer</c> with a <c>Float32Array</c> and a <c>Uint32Array</c> view over it, kept between frames.
/// Vertex and instance records are written into it in the byte layout of the .NET structs and uploaded from it.
/// </summary>
/// <remarks>
/// A context has one, shared by its meshes and batches: an upload copies the bytes out before it returns, so
/// the next write can reuse them. It grows by doubling and never shrinks.
/// </remarks>
[<NoEquality; NoComparison>]
type internal PackBuffer =
    {
        mutable Buffer: JS.ArrayBuffer
        mutable F32: JS.Float32Array
        mutable U32: JS.Uint32Array
    }

/// <summary>
/// Writers that lay the vertex, instance and uniform records into typed arrays with the byte layout of the
/// .NET structs: the offsets and formats of the vertex attribute tables in the WebGPU backend.
/// </summary>
/// <remarks>
/// A JavaScript object has no byte layout, so what .NET uploads with one <c>memcpy</c> is written here one
/// field at a time. The views are typed <c>JS.Float32Array</c> and <c>JS.Uint32Array</c>, not F# arrays: a
/// write to an F# array compiles to a bounds-checked library call, and a write to a typed array view compiles
/// to a plain store. Each loop is written out in full for the same reason, so nothing is called for an
/// element. Offsets in the loops are in 4-byte elements.
/// </remarks>
module internal Packing =
    /// <summary>Size of a <c>Vertex3D</c>: position at 0, normal at 12, colour at 24.</summary>
    [<Literal>]
    let VertexBytes = 40

    /// <summary>
    /// Size of an <c>Instance3D</c>: four model columns at 0, 16, 32 and 48, colour and roughness at 64, pick id
    /// and flags as <c>uint32</c> at 80 and 84, opacity and emissive at 88 and 92.
    /// </summary>
    [<Literal>]
    let InstanceBytes = 96

    /// <summary>Size of a <c>LineVertex3D</c>: position and padding at 0, colour at 16.</summary>
    [<Literal>]
    let LineVertexBytes = 32

    /// <summary>
    /// Size of a <c>DiscInstance3D</c>: centre and radius at 0, fill colour at 16, arc parameters at 32, pick id
    /// and flags as <c>uint32</c> at 48 and 52, arc gap and track alpha at 56 and 60.
    /// </summary>
    [<Literal>]
    let DiscBytes = 64

    /// <summary>Number of <c>float32</c> values in the scene uniform block: a matrix and seven vectors.</summary>
    [<Literal>]
    let SceneUniformFloats = 44

    /// <summary>Number of <c>float32</c> values in the user uniform block.</summary>
    [<Literal>]
    let UserUniformFloats = 64

    /// <summary>Creates a staging buffer of <c>bytes</c> bytes, a multiple of 4.</summary>
    let create (bytes: int) : PackBuffer =
        let data = JS.Constructors.ArrayBuffer.Create bytes

        {
            Buffer = data
            F32 = JS.Constructors.Float32Array.Create data
            U32 = JS.Constructors.Uint32Array.Create data
        }

    /// <summary>Makes room for <c>bytes</c> bytes. What the buffer held is not kept.</summary>
    let ensure (buffer: PackBuffer) (bytes: int) =
        let capacity = buffer.Buffer.byteLength

        if bytes > capacity then
            let data = JS.Constructors.ArrayBuffer.Create (max bytes (capacity * 2))
            buffer.Buffer <- data
            buffer.F32 <- JS.Constructors.Float32Array.Create data
            buffer.U32 <- JS.Constructors.Uint32Array.Create data

    /// <summary>Writes mesh vertices from the start of the buffer.</summary>
    let vertices (buffer: PackBuffer) (source: ReadOnlySpan<Vertex3D>) : int =
        let count = source.Length
        ensure buffer (count * VertexBytes)
        let f = buffer.F32

        for i = 0 to count - 1 do
            let s = source[i]
            let o = i * 10
            let p = s.Position
            let n = s.Normal
            let c = s.Color
            f[o] <- p.X
            f[o + 1] <- p.Y
            f[o + 2] <- p.Z
            f[o + 3] <- n.X
            f[o + 4] <- n.Y
            f[o + 5] <- n.Z
            f[o + 6] <- c.X
            f[o + 7] <- c.Y
            f[o + 8] <- c.Z
            f[o + 9] <- c.W

        count * VertexBytes

    /// <summary>Writes instances from the start of the buffer.</summary>
    let instances (buffer: PackBuffer) (source: ReadOnlySpan<Instance3D>) : int =
        let count = source.Length
        ensure buffer (count * InstanceBytes)
        let f = buffer.F32
        let u = buffer.U32

        for i = 0 to count - 1 do
            let s = source[i]
            let o = i * 24
            let c0 = s.ModelCol0
            let c1 = s.ModelCol1
            let c2 = s.ModelCol2
            let c3 = s.ModelCol3
            let cr = s.ColorRoughness
            f[o] <- c0.X
            f[o + 1] <- c0.Y
            f[o + 2] <- c0.Z
            f[o + 3] <- c0.W
            f[o + 4] <- c1.X
            f[o + 5] <- c1.Y
            f[o + 6] <- c1.Z
            f[o + 7] <- c1.W
            f[o + 8] <- c2.X
            f[o + 9] <- c2.Y
            f[o + 10] <- c2.Z
            f[o + 11] <- c2.W
            f[o + 12] <- c3.X
            f[o + 13] <- c3.Y
            f[o + 14] <- c3.Z
            f[o + 15] <- c3.W
            f[o + 16] <- cr.X
            f[o + 17] <- cr.Y
            f[o + 18] <- cr.Z
            f[o + 19] <- cr.W
            u[o + 20] <- s.PickId
            u[o + 21] <- s.Flags
            f[o + 22] <- s.Opacity
            f[o + 23] <- s.Emissive

        count * InstanceBytes

    // `SetSingle` writes through this one-element array, so there is one instance writer.
    let private single: Instance3D array = [| Instance3D () |]

    /// <summary>Writes one instance at the start of the buffer.</summary>
    let instance (buffer: PackBuffer) (source: Instance3D) : int =
        single[0] <- source
        instances buffer (ReadOnlySpan<Instance3D>(single))

    /// <summary>Writes the first <c>count</c> line endpoints from the start of the buffer.</summary>
    let lineVertices (buffer: PackBuffer) (source: ReadOnlySpan<LineVertex3D>) (count: int) : int =
        ensure buffer (count * LineVertexBytes)
        let f = buffer.F32

        for i = 0 to count - 1 do
            let s = source[i]
            let o = i * 8
            let p = s.Position
            let c = s.Color
            f[o] <- p.X
            f[o + 1] <- p.Y
            f[o + 2] <- p.Z
            f[o + 3] <- s.Pad
            f[o + 4] <- c.X
            f[o + 5] <- c.Y
            f[o + 6] <- c.Z
            f[o + 7] <- c.W

        count * LineVertexBytes

    /// <summary>Writes disc instances from the start of the buffer.</summary>
    let discs (buffer: PackBuffer) (source: ReadOnlySpan<DiscInstance3D>) : int =
        let count = source.Length
        ensure buffer (count * DiscBytes)
        let f = buffer.F32
        let u = buffer.U32

        for i = 0 to count - 1 do
            let s = source[i]
            let o = i * 16
            let center = s.CenterRadiusPx
            let fill = s.FillColor
            let arc = s.ArcParams
            f[o] <- center.X
            f[o + 1] <- center.Y
            f[o + 2] <- center.Z
            f[o + 3] <- center.W
            f[o + 4] <- fill.X
            f[o + 5] <- fill.Y
            f[o + 6] <- fill.Z
            f[o + 7] <- fill.W
            f[o + 8] <- arc.X
            f[o + 9] <- arc.Y
            f[o + 10] <- arc.Z
            f[o + 11] <- arc.W
            u[o + 12] <- s.PickId
            u[o + 13] <- s.Flags
            f[o + 14] <- s.ArcGapPx
            f[o + 15] <- s.TrackAlpha

        count * DiscBytes

    /// <summary>
    /// Writes the scene uniform block for a target of <c>width</c> by <c>height</c> pixels: the view-projection
    /// matrix, then camera position, clip plane, sky colour with the width, ground colour with the height, key
    /// light, rim light and section style.
    /// </summary>
    let sceneUniforms (f: JS.Float32Array) (width: int) (height: int) (camera: Camera3D) (lighting: LightingRig) (section: SectionPlane) =
        let m = Camera3D.viewProjMatrix width height camera
        f[0] <- m.M11
        f[1] <- m.M12
        f[2] <- m.M13
        f[3] <- m.M14
        f[4] <- m.M21
        f[5] <- m.M22
        f[6] <- m.M23
        f[7] <- m.M24
        f[8] <- m.M31
        f[9] <- m.M32
        f[10] <- m.M33
        f[11] <- m.M34
        f[12] <- m.M41
        f[13] <- m.M42
        f[14] <- m.M43
        f[15] <- m.M44
        let position = camera.Position
        f[16] <- position.X
        f[17] <- position.Y
        f[18] <- position.Z
        f[19] <- 0.0f
        let plane = section.AsUniformVector4 ()
        f[20] <- plane.X
        f[21] <- plane.Y
        f[22] <- plane.Z
        f[23] <- plane.W
        let sky = lighting.SkyColor
        f[24] <- sky.X
        f[25] <- sky.Y
        f[26] <- sky.Z
        f[27] <- float32 width
        let ground = lighting.GroundColor
        f[28] <- ground.X
        f[29] <- ground.Y
        f[30] <- ground.Z
        f[31] <- float32 height
        let key = Vector3.Normalize lighting.KeyLightDir
        f[32] <- key.X
        f[33] <- key.Y
        f[34] <- key.Z
        f[35] <- lighting.KeyLightIntensity
        let rim = Vector3.Normalize lighting.RimLightDir
        f[36] <- rim.X
        f[37] <- rim.Y
        f[38] <- rim.Z
        f[39] <- lighting.RimLightIntensity
        let contour = lighting.SectionContourColor
        f[40] <- contour.X
        f[41] <- contour.Y
        f[42] <- contour.Z
        f[43] <- lighting.SectionContourWidth

    /// <summary>Writes user uniform values from slot 0.</summary>
    let userUniforms (f: JS.Float32Array) (values: ReadOnlySpan<Vector4>) =
        for i = 0 to values.Length - 1 do
            let v = values[i]
            let o = i * 4
            f[o] <- v.X
            f[o + 1] <- v.Y
            f[o + 2] <- v.Z
            f[o + 3] <- v.W

    /// <summary>Writes one user uniform slot.</summary>
    let userUniform (f: JS.Float32Array) (slot: int) (value: Vector4) =
        let o = slot * 4
        f[o] <- value.X
        f[o + 1] <- value.Y
        f[o + 2] <- value.Z
        f[o + 3] <- value.W
