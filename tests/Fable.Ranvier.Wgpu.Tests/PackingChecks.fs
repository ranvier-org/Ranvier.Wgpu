/// <summary>
/// The bytes the packing writers produce for the shared cases, for <c>packing.test.mjs</c> to compare with the
/// bytes of the .NET structs.
/// </summary>
module Ranvier.Wgpu.Tests.PackingChecks

open System
open Fable.Core
open Ranvier.Wgpu

/// <summary>A copy of the first <c>count</c> bytes of a staging buffer.</summary>
let private written (buffer: PackBuffer) (count: int) : JS.Uint8Array =
    (JS.Constructors.Uint8Array.Create (buffer.Buffer, 0, count)).slice()

// Each buffer starts smaller than what is written, so the writers also grow it.

let vertices () : JS.Uint8Array =
    let buffer = Packing.create 16
    written buffer (Packing.vertices buffer (ReadOnlySpan<Vertex3D>(PackingCases.vertices)))

let instances () : JS.Uint8Array =
    let buffer = Packing.create 16
    written buffer (Packing.instances buffer (ReadOnlySpan<Instance3D>(PackingCases.instances)))

/// <summary>The single-instance writer, for each case in turn.</summary>
let instancesOneByOne () : JS.Uint8Array array =
    let buffer = Packing.create 16

    PackingCases.instances
    |> Array.map (fun instance -> written buffer (Packing.instance buffer instance))

let lineVertices () : JS.Uint8Array =
    let buffer = Packing.create 16
    let even = PackingCases.lineVertices.Length &&& ~~~1
    written buffer (Packing.lineVertices buffer (ReadOnlySpan<LineVertex3D>(PackingCases.lineVertices)) even)

let discs () : JS.Uint8Array =
    let buffer = Packing.create 16
    written buffer (Packing.discs buffer (ReadOnlySpan<DiscInstance3D>(PackingCases.discs)))

let userUniforms () : JS.Uint8Array =
    let values = JS.Constructors.Float32Array.Create Packing.UserUniformFloats
    Packing.userUniforms values (ReadOnlySpan<Numerics.Vector4>(PackingCases.userUniforms))
    JS.Constructors.Uint8Array.Create (values.buffer, 0, PackingCases.userUniforms.Length * 16)

/// <summary>The <c>Vector3.Normalize</c> cases, three <c>float32</c> each.</summary>
let normalized () : JS.Uint8Array =
    let vectors = PackingCases.normalized ()
    let values = JS.Constructors.Float32Array.Create (vectors.Length * 3)

    vectors
    |> Array.iteri (fun i v ->
        values[i * 3] <- v.X
        values[i * 3 + 1] <- v.Y
        values[i * 3 + 2] <- v.Z)

    JS.Constructors.Uint8Array.Create values.buffer

/// <summary>The scene uniform block; its first 64 bytes are <c>Camera3D.viewProjMatrix</c>.</summary>
let sceneUniforms () : JS.Uint8Array =
    let values = JS.Constructors.Float32Array.Create Packing.SceneUniformFloats
    Packing.sceneUniforms values PackingCases.Width PackingCases.Height PackingCases.camera PackingCases.lighting PackingCases.section
    JS.Constructors.Uint8Array.Create values.buffer

/// <summary>The shared file's own listing of the scene uniform block, computed in JavaScript.</summary>
let sceneUniformValues () : JS.Uint8Array =
    let values =
        JS.Constructors.Float32Array.Create (box (PackingCases.sceneUniformValues ()))

    JS.Constructors.Uint8Array.Create values.buffer

/// <summary>Orthographic and <c>orbitPerspectiveness</c> camera matrices, 16 <c>float32</c> values each.</summary>
let cameraMatrices () : JS.Uint8Array =
    let values =
        JS.Constructors.Float32Array.Create (box (PackingCases.cameraMatrices ()))

    JS.Constructors.Uint8Array.Create values.buffer

let private throwsOutOfRange (action: unit -> unit) : bool =
    try
        action ()
        false
    with :? ArgumentOutOfRangeException ->
        true

/// <summary>Checks <c>Camera3D.orthographic</c> and <c>Camera3D.orbitPerspectiveness</c> validation and clamping.</summary>
let cameraValidation () =
    let cam0 = Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.5f 0.0f

    let camNeg =
        Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.5f -0.5f

    let cam1 = Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.5f 1.0f

    let camOver =
        Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.5f 1.8f

    {|
        orthoFov = float PackingCases.orthoAxisCamera.FovYDeg
        clampedLow = camNeg = cam0
        clampedHigh = camOver = cam1
        zeroHalfHeight =
            throwsOutOfRange (fun () ->
                Camera3D.orthographic Numerics.Vector3.Zero 0.0f 0.0f 0.0f 6.0f
                |> ignore)
        zeroDistance =
            throwsOutOfRange (fun () ->
                Camera3D.orthographic Numerics.Vector3.Zero 0.0f 0.0f 2.0f 0.0f
                |> ignore)
        nanHalfHeight =
            throwsOutOfRange (fun () ->
                Camera3D.orthographic Numerics.Vector3.Zero 0.0f 0.0f Single.NaN 6.0f
                |> ignore)
        infDistance =
            throwsOutOfRange (fun () ->
                Camera3D.orthographic Numerics.Vector3.Zero 0.0f 0.0f 2.0f Single.PositiveInfinity
                |> ignore)
        zeroFitRadius =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 0.0f 0.5f
                |> ignore)
        negFitRadius =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f -1.0f 0.5f
                |> ignore)
        nanFitRadius =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f Single.NaN 0.5f
                |> ignore)
        infFitRadius =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f Single.PositiveInfinity 0.5f
                |> ignore)
        nanPerspectiveness =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.0f Single.NaN
                |> ignore)
        posInfPerspectiveness =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.0f Single.PositiveInfinity
                |> ignore)
        negInfPerspectiveness =
            throwsOutOfRange (fun () ->
                Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 0.0f 0.0f 2.0f Single.NegativeInfinity
                |> ignore)
    |}

/// <summary>Checks 3-arg <c>LineVertex3D</c> <c>Pad</c> values and float offset `+3` in staging buffer.</summary>
let lineVertexPacking () =
    let buffer = Packing.create 16
    let p0 = Numerics.Vector3 (1.25f, -2.5f, 3.75f)
    let p1 = Numerics.Vector3 (-4.0f, 5.0f, 6.5f)
    let c0 = RgbaColor (0.25f, 0.5f, 0.75f, 1.0f)
    let c1 = Numerics.Vector4 (0.8f, 0.6f, 0.4f, 0.9f)
    let vDefault = LineVertex3D (p0, c0)
    let vExemptRgba = LineVertex3D (p0, c0, true)
    let vExemptVec4 = LineVertex3D (p1, c1, true)
    let vClipped = LineVertex3D (p1, c1, false)
    let verts = [| vExemptRgba; vExemptVec4; vDefault; vClipped |]

    let byteCount =
        Packing.lineVertices buffer (ReadOnlySpan<LineVertex3D>(verts)) verts.Length

    let floats = Array.init (byteCount / 4) (fun i -> float buffer.F32[i])

    {|
        defaultPad = float vDefault.Pad
        exemptRgbaPad = float vExemptRgba.Pad
        exemptVec4Pad = float vExemptVec4.Pad
        clippedPad = float vClipped.Pad
        byteCount = byteCount
        floats = floats
    |}

let private smallScaleCheck (cam: Camera3D) =
    let distance = Numerics.Vector3.Distance (cam.Position, cam.Target)

    let depthValid =
        0.0f < cam.NearPlane
        && cam.NearPlane < distance
        && distance < cam.FarPlane

    let projected =
        match Camera3D.projectToScreen 200 200 cam Numerics.Vector3.Zero with
        | ValueSome p -> [| float p.X; float p.Y |]
        | ValueNone -> [||]

    {|
        depthValid = depthValid
        nearPlane = float cam.NearPlane
        distance = float distance
        farPlane = float cam.FarPlane
        projected = projected
    |}

/// <summary>Checks small-scale <c>Camera3D.orthographic</c> and <c>Camera3D.orbitPerspectiveness</c> depth range and center projection.</summary>
let smallScaleCamera () =
    {|
        ortho = smallScaleCheck (Camera3D.orthographic Numerics.Vector3.Zero 0.0f 0.0f 0.0005f 0.005f)
        persp = smallScaleCheck (Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 30.0f 20.0f 0.005f 1.0f)
        orthoZeroPersp = smallScaleCheck (Camera3D.orbitPerspectiveness Numerics.Vector3.Zero 30.0f 20.0f 0.002f 0.0f)
    |}

let private throwsNullArg (action: unit -> unit) : bool =
    try
        action ()
        false
    with :? ArgumentNullException ->
        true

/// <summary>Checks <c>RgbaColor.black</c>, <c>transparent</c>, <c>lerp</c>, and <c>toHex</c>.</summary>
let colorHelpers () =
    let a = RgbaColor (0.2f, 0.4f, 0.6f, 0.8f)
    let b = RgbaColor (0.8f, 0.2f, 1.0f, 0.4f)
    let mid = RgbaColor.lerp a b 0.5f

    let roundTripMatches =
        [
            (0uy, 0uy, 0uy, 255uy)
            (18uy, 52uy, 86uy, 255uy)
            (38uy, 208uy, 124uy, 128uy)
            (255uy, 255uy, 255uy, 0uy)
        ]
        |> List.forall (fun (r, g, b8, a8) ->
            let orig = RgbaColor.fromRgba32 r g b8 a8
            let rt = RgbaColor.fromHex (RgbaColor.toHex orig)

            int (MathF.Round (rt.R * 255.0f)) = int r
            && int (MathF.Round (rt.G * 255.0f)) = int g
            && int (MathF.Round (rt.B * 255.0f)) = int b8
            && int (MathF.Round (rt.A * 255.0f)) = int a8)

    {|
        black =
            [|
                float RgbaColor.black.R
                float RgbaColor.black.G
                float RgbaColor.black.B
                float RgbaColor.black.A
            |]
        transparent =
            [|
                float RgbaColor.transparent.R
                float RgbaColor.transparent.G
                float RgbaColor.transparent.B
                float RgbaColor.transparent.A
            |]
        lerpZero = RgbaColor.lerp a b 0.0f = a
        lerpOne = RgbaColor.lerp a b 1.0f = b
        lerpNeg = RgbaColor.lerp a b -0.5f = a
        lerpOver = RgbaColor.lerp a b 1.5f = b
        lerpNaN = RgbaColor.lerp a b Single.NaN = a
        lerpMid = [| float mid.R; float mid.G; float mid.B; float mid.A |]
        hexRed = RgbaColor.toHex (RgbaColor (1.0f, 0.0f, 0.0f, 1.0f))
        hexClamped = RgbaColor.toHex (RgbaColor (-0.2f, 1.5f, 0.0f, 1.2f))
        hexTranslucent = RgbaColor.toHex (RgbaColor (0.0f, 1.0f, 0.0f, 128.0f / 255.0f))
        roundTripMatches = roundTripMatches
    |}

/// <summary>Checks <c>MeshData.empty</c>, <c>MeshData.merge</c>, and <c>MeshBuilder.AddRawMesh</c>.</summary>
let meshMerge () =
    let mergedNull = MeshData.merge null
    let meshA = Primitives.unitBox ()
    let meshB = Primitives.unitGableRoof ()
    let merged = MeshData.merge [| meshA; MeshData.empty; meshB |]

    let mb = MeshBuilder ()
    mb.AddRawMesh meshA
    mb.AddRawMesh meshB
    let rawBuilt = mb.ToMeshData ()

    let offset = uint32 meshA.Vertices.Length

    let verticesMatch =
        merged.Vertices.Length = meshA.Vertices.Length + meshB.Vertices.Length
        && ([ 0 .. meshA.Vertices.Length - 1 ]
            |> List.forall (fun i -> merged.Vertices[i] = meshA.Vertices[i]))
        && ([ 0 .. meshB.Vertices.Length - 1 ]
            |> List.forall (fun i -> merged.Vertices[meshA.Vertices.Length + i] = meshB.Vertices[i]))

    let indicesMatch =
        merged.Indices.Length = meshA.Indices.Length + meshB.Indices.Length
        && ([ 0 .. meshA.Indices.Length - 1 ]
            |> List.forall (fun i -> merged.Indices[i] = meshA.Indices[i]))
        && ([ 0 .. meshB.Indices.Length - 1 ]
            |> List.forall (fun i -> merged.Indices[meshA.Indices.Length + i] = meshB.Indices[i] + offset))

    let rawMatches =
        rawBuilt.Vertices = merged.Vertices
        && rawBuilt.Indices = merged.Indices

    let nullRawThrows =
        throwsNullArg (fun () -> mb.AddRawMesh (Unchecked.defaultof<MeshData>))

    {|
        emptyCounts = [| MeshData.empty.Vertices.Length; MeshData.empty.Indices.Length |]
        nullCounts = [| mergedNull.Vertices.Length; mergedNull.Indices.Length |]
        mergedCounts = [| merged.Vertices.Length; merged.Indices.Length |]
        verticesMatch = verticesMatch
        indicesMatch = indicesMatch
        rawMatches = rawMatches
        nullRawThrows = nullRawThrows
    |}

/// <summary>Checks <c>Primitives.convexPolyhedron</c> and <c>MeshBuilder.AddConvexPolyhedron</c> winding and outward normals.</summary>
let convexPolyhedronCheck () =
    let nullVertsThrows =
        throwsNullArg (fun () ->
            Primitives.convexPolyhedron null [||] RgbaColor.white
            |> ignore)

    let nullFacesThrows =
        throwsNullArg (fun () ->
            Primitives.convexPolyhedron [||] null RgbaColor.white
            |> ignore)

    let cubeVerts =
        [|
            Numerics.Vector3 (-0.5f, -0.5f, -0.5f)
            Numerics.Vector3 (0.5f, -0.5f, -0.5f)
            Numerics.Vector3 (0.5f, 0.5f, -0.5f)
            Numerics.Vector3 (-0.5f, 0.5f, -0.5f)
            Numerics.Vector3 (-0.5f, -0.5f, 0.5f)
            Numerics.Vector3 (0.5f, -0.5f, 0.5f)
            Numerics.Vector3 (0.5f, 0.5f, 0.5f)
            Numerics.Vector3 (-0.5f, 0.5f, 0.5f)
        |]

    // 3 CCW outward faces + 3 intentionally CW inward faces
    let mixedFaces: int array array =
        [|
            [| 4; 5; 6; 7 |]
            [| 1; 2; 3; 0 |]
            [| 5; 1; 2; 6 |]
            [| 0; 3; 7; 4 |]
            [| 3; 2; 6; 7 |]
            [| 0; 1; 5; 4 |]
        |]

    let tint = RgbaColor (0.25f, 0.75f, 0.5f, 1.0f)
    let poly = Primitives.convexPolyhedron cubeVerts mixedFaces tint
    let expectedColor = tint.ToVector4 ()

    let allColorsMatch =
        poly.Vertices
        |> Array.forall (fun v -> v.Color = expectedColor)

    let mutable allTrianglesValid = poly.Indices.Length = 36
    let mutable triIdx = 0

    while triIdx + 2 < poly.Indices.Length do
        let v0 = poly.Vertices[int poly.Indices[triIdx]]
        let v1 = poly.Vertices[int poly.Indices[triIdx + 1]]
        let v2 = poly.Vertices[int poly.Indices[triIdx + 2]]

        let triNormal =
            Numerics.Vector3.Cross (v1.Position - v0.Position, v2.Position - v0.Position)

        let triCenter = (v0.Position + v1.Position + v2.Position) / 3.0f

        if
            triNormal.LengthSquared () <= 1e-6f
            || Numerics.Vector3.Dot (triNormal, v0.Normal)
               <= 0.0f
            || Numerics.Vector3.Dot (v0.Normal, triCenter)
               <= 0.0f
        then
            allTrianglesValid <- false

        triIdx <- triIdx + 3

    let pool = Array.append [| Numerics.Vector3 (1000.0f, 1000.0f, 1000.0f) |] cubeVerts

    let shiftedFaces =
        mixedFaces
        |> Array.map (Array.map (fun idx -> idx + 1))

    let mb = MeshBuilder ()
    mb.AddConvexPolyhedron (ReadOnlySpan<Numerics.Vector3>(pool), shiftedFaces, tint)
    let fromPool = mb.ToMeshData ()

    {|
        nullVertsThrows = nullVertsThrows
        nullFacesThrows = nullFacesThrows
        vertexCount = poly.Vertices.Length
        indexCount = poly.Indices.Length
        allColorsMatch = allColorsMatch
        allTrianglesValid = allTrianglesValid
        poolMatches =
            fromPool.Vertices = poly.Vertices
            && fromPool.Indices = poly.Indices
    |}

/// <summary>Checks <c>ViewportRect.create</c> validation and <c>Contains</c> bounds.</summary>
let viewportRectCheck () =
    let negXThrows =
        throwsOutOfRange (fun () -> ViewportRect.create -1 0 10 10 |> ignore)

    let negYThrows =
        throwsOutOfRange (fun () -> ViewportRect.create 0 -1 10 10 |> ignore)

    let zeroWThrows =
        throwsOutOfRange (fun () -> ViewportRect.create 0 0 0 10 |> ignore)

    let zeroHThrows =
        throwsOutOfRange (fun () -> ViewportRect.create 0 0 10 0 |> ignore)

    let leftVp = ViewportRect.create 0 0 160 200
    let rightVp = ViewportRect.create 160 0 160 200

    {|
        negXThrows = negXThrows
        negYThrows = negYThrows
        zeroWThrows = zeroWThrows
        zeroHThrows = zeroHThrows
        leftOrigin = leftVp.Contains (0, 0)
        leftMaxInside = leftVp.Contains (159, 199)
        leftRightEdge = leftVp.Contains (160, 100)
        rightLeftEdge = rightVp.Contains (160, 0)
        rightOuterEdge = rightVp.Contains (320, 100)
    |}
