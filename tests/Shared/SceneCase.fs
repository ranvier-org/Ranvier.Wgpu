/// <summary>
/// The fixed scenes that the .NET golden tool draws with the native <c>WgpuContext</c> and the browser test
/// draws with the WebGPU one. Compiled into both, so both draw the same batches from the same source.
/// </summary>
/// <remarks>
/// Only members that exist in both packages are used. Transforms avoid rotations, so no value depends on
/// <c>sin</c> or <c>cos</c>; the projection's <c>tan</c> is the one function that may differ in its last digit.
/// </remarks>
module Ranvier.Wgpu.Tests.SceneCase

open System.Numerics
open Ranvier.Wgpu

[<Literal>]
let Width = 320

[<Literal>]
let Height = 240

let camera: Camera3D =
    {
        Position = Vector3 (7.0f, 5.5f, 9.0f)
        Target = Vector3 (0.0f, 0.8f, 0.0f)
        Up = Vector3.UnitY
        FovYDeg = 42.0f
        NearPlane = 0.08f
        FarPlane = 240.0f
    }

/// <summary>Clips everything with <c>x &gt; 3</c> that does not opt out.</summary>
let section: SectionPlane = SectionPlane.create (Vector3 (-1.0f, 0.0f, 0.0f)) 3.0f

let private place (position: Vector3) (scale: Vector3) =
    Matrix4x4.CreateScale scale
    * Matrix4x4.CreateTranslation position

let private box (position: Vector3) (scale: Vector3) (color: RgbaColor) (roughness: float32) (pickId: uint32) (flags: uint32) (emissive: float32) =
    Instance3D.fromMatrix (place position scale) color roughness pickId flags emissive

/// <summary>
/// Opaque and alpha-blended instanced meshes, hairlines, discs with progress arcs, and a section plane that
/// cuts one box and spares another.
/// </summary>
let build (ctx: WgpuContext) : ScenePass =
    let boxMesh = ctx.UploadMesh (Primitives.unitBox ())
    let sphereMesh = ctx.UploadMesh (Primitives.unitIcosphere 2)

    let boxes = ctx.CreateBatch boxMesh

    boxes.UpdateInstances
        [|
            // Ground slab.
            box (Vector3 (0.0f, -0.05f, 0.0f)) (Vector3 (9.0f, 0.1f, 9.0f)) (RgbaColor (0.42f, 0.44f, 0.48f, 1.0f)) 0.9f 1u InstanceFlags.None 0.0f
            box (Vector3 (-1.6f, 0.75f, 0.4f)) (Vector3 (1.5f, 1.5f, 1.5f)) (RgbaColor (0.85f, 0.32f, 0.22f, 1.0f)) 0.35f 2u InstanceFlags.None 0.0f
            box (Vector3 (0.6f, 0.5f, -1.8f)) (Vector3 (1.0f, 1.0f, 1.0f)) (RgbaColor (0.25f, 0.62f, 0.9f, 1.0f)) 0.6f 3u InstanceFlags.Selected 0.0f
            box (Vector3 (-3.0f, 0.4f, -2.2f)) (Vector3 (0.8f, 0.8f, 0.8f)) (RgbaColor (0.95f, 0.85f, 0.2f, 1.0f)) 0.5f 4u InstanceFlags.Unlit 0.35f
            // Straddles the section plane: its far part is cut and shows the contour colour.
            box (Vector3 (3.0f, 0.6f, 1.2f)) (Vector3 (1.6f, 1.2f, 1.2f)) (RgbaColor (0.3f, 0.8f, 0.45f, 1.0f)) 0.4f 5u InstanceFlags.None 0.0f
            // Beyond the section plane, and exempt from it.
            box
                (Vector3 (3.9f, 0.35f, -1.4f))
                (Vector3 (0.7f, 0.7f, 0.7f))
                (RgbaColor (0.8f, 0.4f, 0.9f, 1.0f))
                0.5f
                6u
                InstanceFlags.IgnoreSectionPlane
                0.0f
        |]

    let spheres = ctx.CreateBatch sphereMesh

    spheres.UpdateInstances
        [|
            Instance3D.sphere (Vector3 (0.9f, 0.7f, 1.6f)) 0.7f (RgbaColor (0.92f, 0.92f, 0.95f, 1.0f)) 0.15f 10u InstanceFlags.None
            Instance3D.sphere (Vector3 (-0.4f, 2.0f, -0.6f)) 0.45f (RgbaColor (1.0f, 0.55f, 0.1f, 1.0f)) 0.8f 11u InstanceFlags.None
        |]

    let glass = ctx.CreateBatch (sphereMesh, BlendMode.AlphaBlend, true)

    glass.UpdateInstances
        [|
            Instance3D.sphere (Vector3 (-1.6f, 1.9f, 0.4f)) 0.9f (RgbaColor (0.3f, 0.9f, 1.0f, 0.45f)) 0.2f 20u InstanceFlags.None
            Instance3D.sphere (Vector3 (1.9f, 1.1f, 2.6f)) 0.6f (RgbaColor (1.0f, 0.3f, 0.6f, 0.6f)) 0.4f 21u InstanceFlags.None
        |]

    let panes = ctx.CreateBatch (boxMesh, BlendMode.AlphaBlend, false)

    panes.SetSingle (
        box (Vector3 (-0.3f, 0.9f, 2.6f)) (Vector3 (2.2f, 1.6f, 0.08f)) (RgbaColor (0.6f, 0.75f, 1.0f, 0.35f)) 0.1f 22u InstanceFlags.None 0.0f
    )

    let lines = ctx.CreateLineBatch ()

    lines.UpdateVertices
        [|
            LineVertex3D (Vector3 (-4.5f, 0.02f, 4.4f), RgbaColor (1.0f, 0.2f, 0.2f, 1.0f))
            LineVertex3D (Vector3 (4.5f, 0.02f, 4.4f), RgbaColor (1.0f, 0.2f, 0.2f, 1.0f))
            LineVertex3D (Vector3 (-4.4f, 0.02f, -4.5f), RgbaColor (0.2f, 1.0f, 0.3f, 0.8f))
            LineVertex3D (Vector3 (-4.4f, 0.02f, 4.5f), RgbaColor (0.2f, 0.4f, 1.0f, 0.8f))
            LineVertex3D (Vector3 (-1.6f, 1.5f, 0.4f), RgbaColor (1.0f, 1.0f, 1.0f, 0.9f))
            LineVertex3D (Vector3 (-0.4f, 2.0f, -0.6f), RgbaColor (1.0f, 1.0f, 0.2f, 0.5f))
            // An odd endpoint: it is dropped.
            LineVertex3D (Vector3 (9.0f, 9.0f, 9.0f), RgbaColor.white)
        |]

    let discs = ctx.CreateDiscBatch ()

    discs.UpdateDiscs
        [|
            DiscInstance3D.create
                (Vector3 (-3.4f, 2.4f, 1.0f))
                13.0f
                (RgbaColor (0.15f, 0.55f, 0.95f, 1.0f))
                0.55f
                0.2f
                3.0f
                0.0f
                30u
                InstanceFlags.None
            DiscInstance3D.create
                (Vector3 (2.4f, 2.6f, -2.0f))
                9.0f
                (RgbaColor (0.95f, 0.75f, 0.2f, 0.85f))
                0.0f
                0.0f
                0.0f
                0.0f
                31u
                InstanceFlags.Selected
            DiscInstance3D.create (Vector3 (0.2f, 3.3f, 1.0f)) 16.0f (RgbaColor (0.7f, 0.3f, 0.9f, 1.0f)) 0.3f 0.5f 2.0f 0.8f 32u InstanceFlags.None
        |]

    ScenePass.create camera
    |> ScenePass.withClear (RgbaColor (0.05f, 0.07f, 0.11f, 1.0f))
    |> ScenePass.withSection section
    |> ScenePass.withBatches [| boxes; spheres; glass; panes |]
    |> ScenePass.withLines [| lines |]
    |> ScenePass.withDiscs [| discs |]

/// <summary>
/// Pixels to pick, in this order: the blended pane in front of a box, the ground, the selected box, the cut
/// box, the box exempt from the section plane, a blended sphere, an opaque sphere, three discs, a place where
/// the section plane removed the ground, and the backdrop.
/// </summary>
let pickPixels: struct (int * int) array =
    [|
        struct (123, 127)
        struct (160, 200)
        struct (197, 117)
        struct (192, 157)
        struct (268, 145)
        struct (176, 150)
        struct (161, 85)
        struct (80, 73)
        struct (160, 60)
        struct (243, 74)
        struct (283, 127)
        struct (5, 5)
    |]

/// <summary>Side of the square target of the custom shader scene.</summary>
[<Literal>]
let CustomSize = 64

/// <summary>The stock shader with one hook: every lit fragment takes its colour from user uniform slot 2.</summary>
let customWgsl () : string =
    Shaders.composeScene [ (SceneShaderHook.FragmentLit, "lit = user.slots[2].rgb;") ]

/// <summary>Three user uniform slots; the shader reads the last.</summary>
let customUniforms: Vector4 array =
    [|
        Vector4 (0.0f, 1.0f, 0.0f, 0.0f)
        Vector4 (0.0f, 0.0f, 1.0f, 0.0f)
        Vector4 (1.0f, 0.0f, 0.0f, 0.0f)
    |]

/// <summary>A box that covers the centre of the target, drawn by a context made with <c>customWgsl</c>.</summary>
let buildCustom (ctx: WgpuContext) : ScenePass =
    let boxMesh = ctx.UploadMesh (Primitives.unitBox ())
    let boxes = ctx.CreateBatch boxMesh
    boxes.SetSingle (Instance3D.identity RgbaColor.white 0.5f 7u InstanceFlags.None)
    ctx.SetUserUniforms customUniforms

    ScenePass.create
        {
            Position = Vector3 (0.0f, 0.0f, 3.0f)
            Target = Vector3.Zero
            Up = Vector3.UnitY
            FovYDeg = 45.0f
            NearPlane = 0.08f
            FarPlane = 240.0f
        }
    |> ScenePass.withClear (RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
    |> ScenePass.withBatches [| boxes |]
