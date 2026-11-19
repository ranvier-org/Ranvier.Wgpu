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
