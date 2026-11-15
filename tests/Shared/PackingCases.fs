/// <summary>
/// Values whose bytes are produced twice: by the .NET golden tool straight from the structs, and by the
/// JavaScript package's packing writers. Compiled into both.
/// </summary>
/// <remarks>
/// The struct cases hold literal values, so they test the layout alone. The values that go through vector
/// maths (<c>normalized</c>, the scene uniform block) are separate cases. Nothing goes through <c>sin</c> or
/// <c>cos</c>; the projection matrix goes through <c>tan</c>.
/// </remarks>
module Ranvier.Wgpu.Tests.PackingCases

open System.Numerics
open Ranvier.Wgpu

let vertices: Vertex3D array =
    [|
        Vertex3D (Vector3 (1.5f, -2.25f, 3.125f), Vector3 (0.0f, 1.0f, 0.0f), Vector4 (0.1f, 0.2f, 0.3f, 0.4f))
        Vertex3D (Vector3 (-0.001f, 1e6f, 7.0f), Vector3 (0.25f, -0.5f, 0.75f))
        Vertex3D (Vector3.Zero, Vector3.UnitZ, Vector4 (1.0f, 0.5f, 0.25f, 0.125f))
    |]

let instances: Instance3D array =
    [|
        Instance3D.fromMatrix
            (Matrix4x4 (1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f, 9.0f, 10.0f, 11.0f, 12.0f, 13.0f, 14.0f, 15.0f, 16.0f))
            (RgbaColor (0.25f, 0.5f, 0.75f, 0.6f))
            0.33f
            0xDEADBEEFu
            (InstanceFlags.Selected
             ||| InstanceFlags.IgnoreSectionPlane)
            1.25f
        Instance3D.identity (RgbaColor.fromRgb24 12uy 200uy 255uy) 2.0f 0xFFFFFFFFu InstanceFlags.Unlit
        Instance3D.sphere (Vector3 (0.1f, -0.2f, 0.3f)) 0.7f (RgbaColor.fromHex "#3366cc80") 0.0f 7u InstanceFlags.None
    |]

/// <summary>An odd count: a line batch uploads the first two only.</summary>
let lineVertices: LineVertex3D array =
    [|
        LineVertex3D (Vector3 (0.5f, 1.5f, -2.5f), RgbaColor (1.0f, 0.5f, 0.25f, 0.75f))
        LineVertex3D (Vector3 (-3.0f, 0.0f, 9.0f), Vector4 (0.2f, 0.4f, 0.6f, 0.8f))
        LineVertex3D (Vector3 (4.0f, 4.0f, 4.0f), RgbaColor.white)
    |]

let discs: DiscInstance3D array =
    [|
        DiscInstance3D.create
            (Vector3 (1.0f, 2.0f, 3.0f))
            14.5f
            (RgbaColor (0.9f, 0.8f, 0.7f, 0.6f))
            0.4f
            0.3f
            2.5f
            0.0f
            0x80000001u
            InstanceFlags.Selected
        DiscInstance3D.create (Vector3 (-1.0f, 0.0f, 0.5f)) 0.1f RgbaColor.white 1.5f -1.0f -2.0f 0.5f 3u InstanceFlags.None
    |]

let userUniforms: Vector4 array =
    [|
        Vector4 (1.0f, 2.0f, 3.0f, 4.0f)
        Vector4 (0.1f, 0.2f, 0.3f, 0.4f)
        Vector4 (-5.5f, 6.25f, 1e-7f, 3.4e38f)
    |]

/// <summary><c>Vector3.Normalize</c> of a plain vector, of a unit vector, and of a vector already normalised.</summary>
let normalized () : Vector3 array =
    [|
        Vector3.Normalize (Vector3 (1.0f, 2.0f, 3.0f))
        Vector3.Normalize Vector3.UnitY
        Vector3.Normalize (Vector3.Normalize (Vector3 (0.55f, 0.78f, 0.48f)))
        Vector3.Normalize (Vector3 (-0.62f, 0.30f, -0.58f))
    |]

[<Literal>]
let Width = 320

[<Literal>]
let Height = 240

let camera: Camera3D =
    {
        Position = Vector3 (6.5f, 5.0f, 9.0f)
        Target = Vector3 (0.25f, 0.75f, -0.5f)
        Up = Vector3.UnitY
        FovYDeg = 42.0f
        NearPlane = 0.08f
        FarPlane = 240.0f
    }

let lighting: LightingRig = LightingRig.studioDefault

let section: SectionPlane = SectionPlane.create (Vector3 (1.0f, 0.5f, -0.25f)) 0.75f

/// <summary>
/// The scene uniform block as <c>WgpuContext</c> writes it, built from the public API: the view-projection
/// matrix, then camera position, clip plane, sky colour with the width, ground colour with the height, key
/// light, rim light and section style.
/// </summary>
let sceneUniformValues () : float32 array =
    let m = Camera3D.viewProjMatrix Width Height camera
    let plane = section.AsUniformVector4 ()
    let key = Vector3.Normalize lighting.KeyLightDir
    let rim = Vector3.Normalize lighting.RimLightDir

    [|
        m.M11
        m.M12
        m.M13
        m.M14
        m.M21
        m.M22
        m.M23
        m.M24
        m.M31
        m.M32
        m.M33
        m.M34
        m.M41
        m.M42
        m.M43
        m.M44
        camera.Position.X
        camera.Position.Y
        camera.Position.Z
        0.0f
        plane.X
        plane.Y
        plane.Z
        plane.W
        lighting.SkyColor.X
        lighting.SkyColor.Y
        lighting.SkyColor.Z
        float32 Width
        lighting.GroundColor.X
        lighting.GroundColor.Y
        lighting.GroundColor.Z
        float32 Height
        key.X
        key.Y
        key.Z
        lighting.KeyLightIntensity
        rim.X
        rim.Y
        rim.Z
        lighting.RimLightIntensity
        lighting.SectionContourColor.X
        lighting.SectionContourColor.Y
        lighting.SectionContourColor.Z
        lighting.SectionContourWidth
    |]
