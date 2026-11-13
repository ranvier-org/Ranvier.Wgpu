namespace Ranvier.Wgpu

open System
open System.Collections.Generic
open System.Numerics

/// <summary>
/// Mutable builder merging transformed meshes, 3D tubes, line segments, and reference grids into one `MeshData`.
/// </summary>
type MeshBuilder() =
    let vertices = ResizeArray<Vertex3D>(1024)
    let indices = ResizeArray<uint32>(3072)

    /// <summary>Current accumulated vertex count.</summary>
    member _.VertexCount = vertices.Count
    /// <summary>Current accumulated triangle index count.</summary>
    member _.IndexCount = indices.Count

    /// <summary>Appends <paramref name="mesh"/> transformed by <paramref name="transform"/> and tinted by <paramref name="color"/>.</summary>
    /// <param name="mesh">Source mesh geometry.</param>
    /// <param name="transform">World/local transform matrix (automatically flips winding when determinant is negative).</param>
    /// <param name="color">Per-part multiplicative vertex color tint.</param>
    member _.AddMesh(mesh: MeshData, transform: Matrix4x4, color: RgbaColor) =
        let baseIdx = uint32 vertices.Count
        let c0 = Vector3 (transform.M11, transform.M12, transform.M13)
        let c1 = Vector3 (transform.M21, transform.M22, transform.M23)
        let c2 = Vector3 (transform.M31, transform.M32, transform.M33)
        let adj0 = Vector3.Cross (c1, c2)
        let adj1 = Vector3.Cross (c2, c0)
        let adj2 = Vector3.Cross (c0, c1)
        let detSign = if Vector3.Dot (c0, adj0) < 0.0f then -1.0f else 1.0f
        let tint = color.ToVector4 ()

        for v in mesh.Vertices do
            let wp = Vector3.Transform (v.Position, transform)

            let rawN =
                (adj0 * v.Normal.X
                 + adj1 * v.Normal.Y
                 + adj2 * v.Normal.Z)
                * detSign

            let wn =
                if rawN.LengthSquared () > 1e-12f then
                    Vector3.Normalize rawN
                else
                    Vector3.UnitY

            vertices.Add (Vertex3D (wp, wn, v.Color * tint))

        if detSign > 0.0f then
            for idx in mesh.Indices do
                indices.Add (baseIdx + idx)
        else
            let mutable i = 0

            while i + 2 < mesh.Indices.Length do
                indices.Add (baseIdx + mesh.Indices[i])
                indices.Add (baseIdx + mesh.Indices[i + 2])
                indices.Add (baseIdx + mesh.Indices[i + 1])
                i <- i + 3

    /// <summary>Appends a smooth 3D cylindrical tube along `points` (`sides` at least 3, typically 4 or 6).</summary>
    member _.AddTube(points: ReadOnlySpan<Vector3>, radius: float32, sides: int, startColor: RgbaColor, endColor: RgbaColor) =
        if points.Length >= 2 && sides >= 3 then
            let nPts = points.Length
            let baseVertex = uint32 vertices.Count
            let mutable prevUp = Vector3.UnitY

            for i = 0 to nPts - 1 do
                let p = points[i]

                let tangent =
                    if i = 0 then
                        Vector3.Normalize (points[1] - points[0])
                    elif i = nPts - 1 then
                        Vector3.Normalize (points[nPts - 1] - points[nPts - 2])
                    else
                        let d = points[i + 1] - points[i - 1]

                        if d.LengthSquared () > 1e-8f then
                            Vector3.Normalize d
                        else
                            Vector3.UnitY

                let fallback =
                    if MathF.Abs (Vector3.Dot (tangent, prevUp)) > 0.94f then
                        if MathF.Abs tangent.Y < 0.9f then
                            Vector3.UnitY
                        else
                            Vector3.UnitX
                    else
                        prevUp

                let side = Vector3.Normalize (Vector3.Cross (fallback, tangent))
                let up = Vector3.Normalize (Vector3.Cross (tangent, side))
                prevUp <- up

                let t = float32 i / float32 (max 1 (nPts - 1))
                let col = Vector4.Lerp (startColor.ToVector4 (), endColor.ToVector4 (), t)

                for s = 0 to sides - 1 do
                    let ang = float32 s / float32 sides * MathF.PI * 2.0f
                    let nrm = side * MathF.Cos ang + up * MathF.Sin ang
                    vertices.Add (Vertex3D (p + nrm * radius, nrm, col))

            for i = 0 to nPts - 2 do
                let ring0 = baseVertex + uint32 (i * sides)
                let ring1 = baseVertex + uint32 ((i + 1) * sides)

                for s = 0 to sides - 1 do
                    let sNext = (s + 1) % sides
                    let a = ring0 + uint32 s
                    let b = ring0 + uint32 sNext
                    let c = ring1 + uint32 sNext
                    let d = ring1 + uint32 s
                    indices.Add a
                    indices.Add c
                    indices.Add b
                    indices.Add a
                    indices.Add d
                    indices.Add c

    /// <summary>Appends a single straight line segment as a thin 4-sided 3D prism.</summary>
    member this.AddLineSegment(a: Vector3, b: Vector3, thickness: float32, color: RgbaColor) =
        if (b - a).LengthSquared() > 1e-8f then
            let pts = [| a; b |]
            this.AddTube (ReadOnlySpan pts, thickness * 0.5f, 4, color, color)

    /// <summary>Appends a world-space XZ reference grid centered at `originY`.</summary>
    member this.AddGridXZ(halfExtent: float32, step: float32, originY: float32, minorColor: RgbaColor, axisColor: RgbaColor) =
        let count = int (MathF.Round (halfExtent / step))

        for i = -count to count do
            let coord = float32 i * step
            let isAxis = i = 0
            let col = if isAxis then axisColor else minorColor
            let thick = if isAxis then 0.032f else 0.015f
            this.AddLineSegment (Vector3 (-halfExtent, originY, coord), Vector3 (halfExtent, originY, coord), thick, col)
            this.AddLineSegment (Vector3 (coord, originY, -halfExtent), Vector3 (coord, originY, halfExtent), thick, col)

    /// <summary>Materializes the accumulated geometry as an immutable `MeshData` record.</summary>
    member _.ToMeshData() : MeshData =
        {
            Vertices = vertices.ToArray ()
            Indices = indices.ToArray ()
        }

/// <summary>Unit-scale procedural 3D mesh primitives centered at the origin.</summary>
module Primitives =
    /// <summary>Unit box `[-0.5, +0.5]^3` with sharp outward face normals (24 vertices, 36 indices).</summary>
    let unitBox () : MeshData =
        let faces =
            [|
                // +Z front
                Vector3.UnitZ,
                [|
                    Vector3 (-0.5f, -0.5f, 0.5f)
                    Vector3 (0.5f, -0.5f, 0.5f)
                    Vector3 (0.5f, 0.5f, 0.5f)
                    Vector3 (-0.5f, 0.5f, 0.5f)
                |]
                // -Z back
                -Vector3.UnitZ,
                [|
                    Vector3 (0.5f, -0.5f, -0.5f)
                    Vector3 (-0.5f, -0.5f, -0.5f)
                    Vector3 (-0.5f, 0.5f, -0.5f)
                    Vector3 (0.5f, 0.5f, -0.5f)
                |]
                // +X right
                Vector3.UnitX,
                [|
                    Vector3 (0.5f, -0.5f, 0.5f)
                    Vector3 (0.5f, -0.5f, -0.5f)
                    Vector3 (0.5f, 0.5f, -0.5f)
                    Vector3 (0.5f, 0.5f, 0.5f)
                |]
                // -X left
                -Vector3.UnitX,
                [|
                    Vector3 (-0.5f, -0.5f, -0.5f)
                    Vector3 (-0.5f, -0.5f, 0.5f)
                    Vector3 (-0.5f, 0.5f, 0.5f)
                    Vector3 (-0.5f, 0.5f, -0.5f)
                |]
                // +Y top
                Vector3.UnitY,
                [|
                    Vector3 (-0.5f, 0.5f, 0.5f)
                    Vector3 (0.5f, 0.5f, 0.5f)
                    Vector3 (0.5f, 0.5f, -0.5f)
                    Vector3 (-0.5f, 0.5f, -0.5f)
                |]
                // -Y bottom
                -Vector3.UnitY,
                [|
                    Vector3 (-0.5f, -0.5f, -0.5f)
                    Vector3 (0.5f, -0.5f, -0.5f)
                    Vector3 (0.5f, -0.5f, 0.5f)
                    Vector3 (-0.5f, -0.5f, 0.5f)
                |]
            |]

        let verts = ResizeArray<Vertex3D>(24)
        let inds = ResizeArray<uint32>(36)

        for normal, corners in faces do
            let b = uint32 verts.Count

            for c in corners do
                verts.Add (Vertex3D (c, normal))

            inds.Add b
            inds.Add (b + 1u)
            inds.Add (b + 2u)
            inds.Add b
            inds.Add (b + 2u)
            inds.Add (b + 3u)

        {
            Vertices = verts.ToArray ()
            Indices = inds.ToArray ()
        }

    /// <summary>Unit pitched gable roof prism inside `[-0.5, +0.5]^3` (ridge along X, slopes along Z).</summary>
    let unitGableRoof () : MeshData =
        let verts = ResizeArray<Vertex3D>(18)
        let inds = ResizeArray<uint32>(24)

        let addTri (a: Vector3) (b: Vector3) (c: Vector3) =
            let n = Vector3.Normalize (Vector3.Cross (b - a, c - a))
            let baseIdx = uint32 verts.Count
            verts.Add (Vertex3D (a, n))
            verts.Add (Vertex3D (b, n))
            verts.Add (Vertex3D (c, n))
            inds.Add baseIdx
            inds.Add (baseIdx + 1u)
            inds.Add (baseIdx + 2u)

        let addQuad (a: Vector3) (b: Vector3) (c: Vector3) (d: Vector3) =
            let n = Vector3.Normalize (Vector3.Cross (b - a, c - a))
            let baseIdx = uint32 verts.Count
            verts.Add (Vertex3D (a, n))
            verts.Add (Vertex3D (b, n))
            verts.Add (Vertex3D (c, n))
            verts.Add (Vertex3D (d, n))
            inds.Add baseIdx
            inds.Add (baseIdx + 1u)
            inds.Add (baseIdx + 2u)
            inds.Add baseIdx
            inds.Add (baseIdx + 2u)
            inds.Add (baseIdx + 3u)

        let blF = Vector3 (-0.5f, -0.5f, 0.5f)
        let brF = Vector3 (0.5f, -0.5f, 0.5f)
        let blB = Vector3 (-0.5f, -0.5f, -0.5f)
        let brB = Vector3 (0.5f, -0.5f, -0.5f)
        let topL = Vector3 (-0.5f, 0.5f, 0.0f)
        let topR = Vector3 (0.5f, 0.5f, 0.0f)

        // +Z front slope, -Z back slope, bottom face, -X/+X gable ends
        addQuad blF brF topR topL
        addQuad brB blB topL topR
        addQuad blB brB brF blF
        addTri blB blF topL
        addTri brF brB topR

        {
            Vertices = verts.ToArray ()
            Indices = inds.ToArray ()
        }

    /// <summary>Unit-radius smooth icosphere (`subdivisions` in `0..4`; `1` yields 80 tris, `2` yields 320 tris).</summary>
    let unitIcosphere (subdivisions: int) : MeshData =
        let t = (1.0f + MathF.Sqrt 5.0f) * 0.5f
        let posList = ResizeArray<Vector3>()

        let addNorm x y z =
            posList.Add (Vector3.Normalize (Vector3 (x, y, z)))

        addNorm -1.0f t 0.0f
        addNorm 1.0f t 0.0f
        addNorm -1.0f -t 0.0f
        addNorm 1.0f -t 0.0f
        addNorm 0.0f -1.0f t
        addNorm 0.0f 1.0f t
        addNorm 0.0f -1.0f -t
        addNorm 0.0f 1.0f -t
        addNorm t 0.0f -1.0f
        addNorm t 0.0f 1.0f
        addNorm -t 0.0f -1.0f
        addNorm -t 0.0f 1.0f

        let mutable tris =
            [|
                0u, 11u, 5u
                0u, 5u, 1u
                0u, 1u, 7u
                0u, 7u, 10u
                0u, 10u, 11u
                1u, 5u, 9u
                5u, 11u, 4u
                11u, 10u, 2u
                10u, 7u, 6u
                7u, 1u, 8u
                3u, 9u, 4u
                3u, 4u, 2u
                3u, 2u, 6u
                3u, 6u, 8u
                3u, 8u, 9u
                4u, 9u, 5u
                2u, 4u, 11u
                6u, 2u, 10u
                8u, 6u, 7u
                9u, 8u, 1u
            |]

        let midCache = Dictionary<uint64, uint32>()

        let midpoint (i0: uint32) (i1: uint32) =
            let lo = min i0 i1
            let hi = max i0 i1
            let key = (uint64 lo <<< 32) ||| uint64 hi

            match midCache.TryGetValue key with
            | true, idx -> idx
            | false, _ ->
                let m = Vector3.Normalize ((posList[int i0] + posList[int i1]) * 0.5f)
                let idx = uint32 posList.Count
                posList.Add m
                midCache[key] <- idx
                idx

        for _ = 1 to Math.Clamp (subdivisions, 0, 4) do
            let next = ResizeArray<uint32 * uint32 * uint32>(tris.Length * 4)

            for (a, b, c) in tris do
                let ab = midpoint a b
                let bc = midpoint b c
                let ca = midpoint c a
                next.Add (a, ab, ca)
                next.Add (b, bc, ab)
                next.Add (c, ca, bc)
                next.Add (ab, bc, ca)

            tris <- next.ToArray ()

        let verts =
            Array.init posList.Count (fun i -> let p = posList[i] in Vertex3D (p, p))

        let inds = Array.zeroCreate<uint32>(tris.Length * 3)

        for i = 0 to tris.Length - 1 do
            let (a, b, c) = tris[i]
            inds[i * 3] <- a
            inds[i * 3 + 1] <- b
            inds[i * 3 + 2] <- c

        { Vertices = verts; Indices = inds }

    /// <summary>Unit cylinder along Y (`radius = 0.5`, `y` in `[-0.5, +0.5]`) with end caps.</summary>
    let unitCylinder (segments: int) : MeshData =
        let segs = max 6 segments
        let verts = ResizeArray<Vertex3D>(segs * 4 + 2)
        let inds = ResizeArray<uint32>(segs * 12)

        for i = 0 to segs - 1 do
            let a = float32 i / float32 segs * MathF.PI * 2.0f
            let nx = MathF.Cos a
            let nz = MathF.Sin a
            let n = Vector3 (nx, 0.0f, nz)
            verts.Add (Vertex3D (Vector3 (nx * 0.5f, -0.5f, nz * 0.5f), n))
            verts.Add (Vertex3D (Vector3 (nx * 0.5f, 0.5f, nz * 0.5f), n))

        for i = 0 to segs - 1 do
            let next = (i + 1) % segs
            let b0 = uint32 (i * 2)
            let t0 = b0 + 1u
            let b1 = uint32 (next * 2)
            let t1 = b1 + 1u
            inds.Add b0
            inds.Add t1
            inds.Add b1
            inds.Add b0
            inds.Add t0
            inds.Add t1

        // Top & bottom caps
        let topCenter = uint32 verts.Count
        verts.Add (Vertex3D (Vector3 (0.0f, 0.5f, 0.0f), Vector3.UnitY))

        for i = 0 to segs - 1 do
            let a = float32 i / float32 segs * MathF.PI * 2.0f
            verts.Add (Vertex3D (Vector3 (MathF.Cos a * 0.5f, 0.5f, MathF.Sin a * 0.5f), Vector3.UnitY))

        for i = 0 to segs - 1 do
            let c0 = topCenter + 1u + uint32 i
            let c1 = topCenter + 1u + uint32 ((i + 1) % segs)
            inds.Add topCenter
            inds.Add c1
            inds.Add c0

        let botCenter = uint32 verts.Count
        verts.Add (Vertex3D (Vector3 (0.0f, -0.5f, 0.0f), -Vector3.UnitY))

        for i = 0 to segs - 1 do
            let a = float32 i / float32 segs * MathF.PI * 2.0f
            verts.Add (Vertex3D (Vector3 (MathF.Cos a * 0.5f, -0.5f, MathF.Sin a * 0.5f), -Vector3.UnitY))

        for i = 0 to segs - 1 do
            let c0 = botCenter + 1u + uint32 i
            let c1 = botCenter + 1u + uint32 ((i + 1) % segs)
            inds.Add botCenter
            inds.Add c0
            inds.Add c1

        {
            Vertices = verts.ToArray ()
            Indices = inds.ToArray ()
        }

    /// <summary>Unit cone along Y (`base radius = 0.5` at `y = -0.5`, apex at `y = +0.5`).</summary>
    let unitCone (segments: int) : MeshData =
        let segs = max 6 segments
        let verts = ResizeArray<Vertex3D>(segs * 3 + 1)
        let inds = ResizeArray<uint32>(segs * 6)
        let slopeY = 0.5f / MathF.Sqrt (1.25f)
        let slopeR = 1.0f / MathF.Sqrt (1.25f)

        for i = 0 to segs - 1 do
            let a0 = float32 i / float32 segs * MathF.PI * 2.0f

            let a1 =
                float32 ((i + 1) % segs) / float32 segs
                * MathF.PI
                * 2.0f

            let am = (a0 + a1) * 0.5f
            let p0 = Vector3 (MathF.Cos a0 * 0.5f, -0.5f, MathF.Sin a0 * 0.5f)
            let p1 = Vector3 (MathF.Cos a1 * 0.5f, -0.5f, MathF.Sin a1 * 0.5f)
            let apex = Vector3 (0.0f, 0.5f, 0.0f)
            let n0 = Vector3 (MathF.Cos a0 * slopeR, slopeY, MathF.Sin a0 * slopeR)
            let n1 = Vector3 (MathF.Cos a1 * slopeR, slopeY, MathF.Sin a1 * slopeR)
            let nm = Vector3 (MathF.Cos am * slopeR, slopeY, MathF.Sin am * slopeR)
            let b = uint32 verts.Count
            verts.Add (Vertex3D (p0, n0))
            verts.Add (Vertex3D (apex, nm))
            verts.Add (Vertex3D (p1, n1))
            inds.Add b
            inds.Add (b + 1u)
            inds.Add (b + 2u)

        let botCenter = uint32 verts.Count
        verts.Add (Vertex3D (Vector3 (0.0f, -0.5f, 0.0f), -Vector3.UnitY))

        for i = 0 to segs - 1 do
            let a = float32 i / float32 segs * MathF.PI * 2.0f
            verts.Add (Vertex3D (Vector3 (MathF.Cos a * 0.5f, -0.5f, MathF.Sin a * 0.5f), -Vector3.UnitY))

        for i = 0 to segs - 1 do
            let c0 = botCenter + 1u + uint32 i
            let c1 = botCenter + 1u + uint32 ((i + 1) % segs)
            inds.Add botCenter
            inds.Add c0
            inds.Add c1

        {
            Vertices = verts.ToArray ()
            Indices = inds.ToArray ()
        }

    /// <summary>Unit torus in the XZ plane (`majorRadius = 0.5`, `minorRadius = minorRatio * 0.5`).</summary>
    let unitTorus (majorSegments: int) (minorSegments: int) (minorRatio: float32) : MeshData =
        let maj = max 8 majorSegments
        let minS = max 6 minorSegments
        let R = 0.5f
        let r = 0.5f * Math.Clamp (minorRatio, 0.01f, 0.45f)
        let verts = ResizeArray<Vertex3D>(maj * minS)
        let inds = ResizeArray<uint32>(maj * minS * 6)

        for i = 0 to maj - 1 do
            let u = float32 i / float32 maj * MathF.PI * 2.0f
            let cu = MathF.Cos u
            let su = MathF.Sin u
            let ringCenter = Vector3 (cu * R, 0.0f, su * R)
            let radial = Vector3 (cu, 0.0f, su)

            for j = 0 to minS - 1 do
                let v = float32 j / float32 minS * MathF.PI * 2.0f
                let cv = MathF.Cos v
                let sv = MathF.Sin v
                let n = radial * cv + Vector3.UnitY * sv
                verts.Add (Vertex3D (ringCenter + n * r, n))

        for i = 0 to maj - 1 do
            let iNext = (i + 1) % maj

            for j = 0 to minS - 1 do
                let jNext = (j + 1) % minS
                let a = uint32 (i * minS + j)
                let b = uint32 (iNext * minS + j)
                let c = uint32 (iNext * minS + jNext)
                let d = uint32 (i * minS + jNext)
                inds.Add a
                inds.Add c
                inds.Add b
                inds.Add a
                inds.Add d
                inds.Add c

        {
            Vertices = verts.ToArray ()
            Indices = inds.ToArray ()
        }
