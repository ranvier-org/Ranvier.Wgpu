namespace Ranvier.Wgpu

open System
open System.Globalization
open System.Numerics
open System.Runtime.InteropServices

/// <summary>Linear or sRGB RGBA float32 color value.</summary>
[<Struct; StructLayout(LayoutKind.Sequential)>]
type RgbaColor =
    val R: float32
    val G: float32
    val B: float32
    val A: float32
    new(r: float32, g: float32, b: float32, a: float32) = { R = r; G = g; B = b; A = a }

    /// <summary>Converts this color to a four-component <see cref="T:System.Numerics.Vector4"/>.</summary>
    member this.ToVector4() =
        Vector4 (this.R, this.G, this.B, this.A)

    /// <summary>Returns a copy of this color with its alpha channel replaced by <paramref name="alpha"/>.</summary>
    /// <param name="alpha">New opacity value in `[0, 1]`.</param>
    member this.WithAlpha(alpha: float32) =
        RgbaColor (this.R, this.G, this.B, alpha)

    override _.ToString() =
        "RgbaColor"

/// <summary>Constructors and constants for <see cref="T:Ranvier.Wgpu.RgbaColor"/>.</summary>
module RgbaColor =
    /// <summary>Creates an <see cref="T:Ranvier.Wgpu.RgbaColor"/> from normalized `[0, 1]` channels.</summary>
    let inline create r g b a =
        RgbaColor (r, g, b, a)

    /// <summary>Creates an opaque <see cref="T:Ranvier.Wgpu.RgbaColor"/> from 8-bit RGB channels.</summary>
    let inline fromRgb24 (r: byte) (g: byte) (b: byte) =
        RgbaColor (float32 r / 255.0f, float32 g / 255.0f, float32 b / 255.0f, 1.0f)

    /// <summary>Creates an <see cref="T:Ranvier.Wgpu.RgbaColor"/> from 8-bit RGBA channels.</summary>
    let inline fromRgba32 (r: byte) (g: byte) (b: byte) (a: byte) =
        RgbaColor (float32 r / 255.0f, float32 g / 255.0f, float32 b / 255.0f, float32 a / 255.0f)

    /// <summary>Parses a 6-digit (`#RRGGBB`) or 8-digit (`#RRGGBBAA`) hexadecimal color string.</summary>
    /// <param name="hex">Hexadecimal string with or without a leading `#`.</param>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="hex"/> is null.</exception>
    /// <exception cref="T:System.FormatException"><paramref name="hex"/> does not have 6 or 8 hex digits.</exception>
    let fromHex (hex: string) =
        if isNull hex then
            nullArg (nameof hex)

        let s = if hex.StartsWith ('#') then hex.Substring (1) else hex

        if s.Length = 6 then
            let r =
                Byte.Parse (s.AsSpan (0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            let g =
                Byte.Parse (s.AsSpan (2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            let b =
                Byte.Parse (s.AsSpan (4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            fromRgb24 r g b
        elif s.Length = 8 then
            let r =
                Byte.Parse (s.AsSpan (0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            let g =
                Byte.Parse (s.AsSpan (2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            let b =
                Byte.Parse (s.AsSpan (4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            let a =
                Byte.Parse (s.AsSpan (6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

            fromRgba32 r g b a
        else
            raise (FormatException $"Expected 6-digit or 8-digit hex color, got '{hex}'.")

    /// <summary>Opaque pure white `(1, 1, 1, 1)`.</summary>
    let white = RgbaColor (1.0f, 1.0f, 1.0f, 1.0f)

    /// <summary>Default deep-space dark backdrop `(0.016, 0.024, 0.043, 1)`.</summary>
    let darkBackdrop = RgbaColor (0.016f, 0.024f, 0.043f, 1.0f)

/// <summary>40-byte GPU mesh vertex (`Position: float32x3`, `Normal: float32x3`, `Color: float32x4`).</summary>
[<Struct; StructLayout(LayoutKind.Sequential)>]
type Vertex3D =
    val mutable Position: Vector3
    val mutable Normal: Vector3
    val mutable Color: Vector4

    new(pos: Vector3, normal: Vector3, color: Vector4) =
        {
            Position = pos
            Normal = normal
            Color = color
        }

    new(pos: Vector3, normal: Vector3) =
        {
            Position = pos
            Normal = normal
            Color = Vector4.One
        }

    override _.ToString() =
        "Vertex3D"

/// <summary>
/// 32-byte unlit 3D line-segment endpoint (`Position: float32x3`, `Pad: float32`, `Color: float32x4`).
/// Pair consecutive vertices `(2k, 2k+1)` in a `LineBatch` for 4x-MSAA alpha-blended hairlines.
/// </summary>
[<Struct; StructLayout(LayoutKind.Sequential)>]
type LineVertex3D =
    val mutable Position: Vector3
    val mutable Pad: float32
    val mutable Color: Vector4

    new(pos: Vector3, color: RgbaColor) =
        {
            Position = pos
            Pad = 0.0f
            Color = color.ToVector4 ()
        }

    new(pos: Vector3, color: Vector4) =
        {
            Position = pos
            Pad = 0.0f
            Color = color
        }

    override _.ToString() =
        "LineVertex3D"

/// <summary>
/// 64-byte hardware-instanced camera-facing SDF disc and concentric verification-arc billboard.
/// </summary>
[<Struct; StructLayout(LayoutKind.Sequential)>]
type DiscInstance3D =
    val mutable CenterRadiusPx: Vector4
    val mutable FillColor: Vector4
    val mutable ArcParams: Vector4
    val mutable PickId: uint32
    val mutable Flags: uint32
    val mutable ArcGapPx: float32
    val mutable TrackAlpha: float32

    override _.ToString() =
        "DiscInstance3D"

/// <summary>Constructors for <see cref="T:Ranvier.Wgpu.DiscInstance3D"/>.</summary>
module DiscInstance3D =
    /// <summary>Creates a camera-facing SDF disc instance with optional verification-progress ring and hollow portal.</summary>
    let create
        (worldPos: Vector3)
        (radiusPx: float32)
        (color: RgbaColor)
        (verifiedFrac: float32)
        (inProgressFrac: float32)
        (arcThicknessPx: float32)
        (portalHollow: float32)
        (pickId: uint32)
        (flags: uint32)
        : DiscInstance3D =
        let mutable d = DiscInstance3D ()
        d.CenterRadiusPx <- Vector4 (worldPos.X, worldPos.Y, worldPos.Z, max 0.5f radiusPx)
        d.FillColor <- color.ToVector4 ()

        d.ArcParams <-
            Vector4 (
                Math.Clamp (verifiedFrac, 0.0f, 1.0f),
                Math.Clamp (inProgressFrac, 0.0f, 1.0f),
                max 0.0f arcThicknessPx,
                Math.Clamp (portalHollow, 0.0f, 1.0f)
            )

        d.PickId <- pickId
        d.Flags <- flags
        d.ArcGapPx <- 3.2f
        d.TrackAlpha <- 0.42f
        d

/// <summary>Per-instance bitmask flags controlling shading, selection highlight, and section clipping.</summary>
module InstanceFlags =
    /// <summary>Standard lit PBR-style shading subject to active section planes.</summary>
    [<Literal>]
    let None = 0u

    /// <summary>Render as unlit emissive color (gizmos, wireframes, grids, starfields, filaments).</summary>
    [<Literal>]
    let Unlit = 1u

    /// <summary>Highlight with selection rim and emissive boost.</summary>
    [<Literal>]
    let Selected = 2u

    /// <summary>Exempt this instance from active BIM section-plane clipping.</summary>
    [<Literal>]
    let IgnoreSectionPlane = 4u

/// <summary>
/// 96-byte hardware GPU instance payload (4x4 world matrix, albedo/roughness, pick ID/flags, opacity/emissive).
/// </summary>
[<Struct; StructLayout(LayoutKind.Sequential)>]
type Instance3D =
    val mutable ModelCol0: Vector4
    val mutable ModelCol1: Vector4
    val mutable ModelCol2: Vector4
    val mutable ModelCol3: Vector4
    val mutable ColorRoughness: Vector4
    val mutable PickId: uint32
    val mutable Flags: uint32
    val mutable Opacity: float32
    val mutable Emissive: float32

    override _.ToString() =
        "Instance3D"

/// <summary>Constructors for <see cref="T:Ranvier.Wgpu.Instance3D"/>.</summary>
module Instance3D =
    /// <summary>Builds an <see cref="T:Ranvier.Wgpu.Instance3D"/> from a row-vector `System.Numerics.Matrix4x4` world transform.</summary>
    let fromMatrix (world: Matrix4x4) (color: RgbaColor) (roughness: float32) (pickId: uint32) (flags: uint32) (emissive: float32) : Instance3D =
        let mutable inst = Instance3D ()
        inst.ModelCol0 <- Vector4 (world.M11, world.M12, world.M13, world.M14)
        inst.ModelCol1 <- Vector4 (world.M21, world.M22, world.M23, world.M24)
        inst.ModelCol2 <- Vector4 (world.M31, world.M32, world.M33, world.M34)
        inst.ModelCol3 <- Vector4 (world.M41, world.M42, world.M43, world.M44)
        inst.ColorRoughness <- Vector4 (color.R, color.G, color.B, Math.Clamp (roughness, 0.04f, 0.98f))
        inst.PickId <- pickId
        inst.Flags <- flags
        inst.Opacity <- Math.Clamp (color.A, 0.0f, 1.0f)
        inst.Emissive <- emissive
        inst

    /// <summary>Builds an identity-transformed instance.</summary>
    let identity (color: RgbaColor) (roughness: float32) (pickId: uint32) (flags: uint32) =
        fromMatrix Matrix4x4.Identity color roughness pickId flags 0.0f

    /// <summary>Builds a uniformly scaled sphere/point instance centered at `center`.</summary>
    let sphere (center: Vector3) (radius: float32) (color: RgbaColor) (roughness: float32) (pickId: uint32) (flags: uint32) =
        let m =
            Matrix4x4.CreateScale (radius)
            * Matrix4x4.CreateTranslation (center)

        fromMatrix m color roughness pickId flags 0.0f

    /// <summary>Builds an instance from translation, Y-axis yaw in degrees, and non-uniform scale.</summary>
    let trs (translation: Vector3) (yawDeg: float32) (scale: Vector3) (color: RgbaColor) (roughness: float32) (pickId: uint32) (flags: uint32) =
        let yawRad = yawDeg * MathF.PI / 180.0f

        let m =
            Matrix4x4.CreateScale (scale)
            * Matrix4x4.CreateRotationY (yawRad)
            * Matrix4x4.CreateTranslation (translation)

        fromMatrix m color roughness pickId flags 0.0f

/// <summary>CPU-side indexed triangle mesh ready to upload to a `WgpuContext`.</summary>
type MeshData =
    {
        Vertices: Vertex3D array
        Indices: uint32 array
    }

    override _.ToString() =
        "MeshData"

/// <summary>Color-attachment blending mode for an `InstanceBatch`.</summary>
[<RequireQualifiedAccess; Struct>]
type BlendMode =
    /// <summary>Opaque depth-writing pass rendered first.</summary>
    | Opaque
    /// <summary>Premultiplied alpha-blended pass with depth writes disabled.</summary>
    | AlphaBlend

    override this.ToString() =
        match this with
        | Opaque -> "Opaque"
        | AlphaBlend -> "AlphaBlend"

/// <summary>Preferred native WebGPU graphics backend when creating a `WgpuContext`.</summary>
[<RequireQualifiedAccess; Struct>]
type WgpuBackendPreference =
    /// <summary>Selects the platform default high-performance backend automatically.</summary>
    | Auto
    /// <summary>Prefers Direct3D 12 on Windows.</summary>
    | Dx12
    /// <summary>Prefers Vulkan.</summary>
    | Vulkan
    /// <summary>Prefers Metal on macOS.</summary>
    | Metal
    /// <summary>Prefers OpenGL / OpenGL ES.</summary>
    | OpenGL

    override this.ToString() =
        match this with
        | Auto -> "Auto"
        | Dx12 -> "Dx12"
        | Vulkan -> "Vulkan"
        | Metal -> "Metal"
        | OpenGL -> "OpenGL"

/// <summary>World-space half-space clipping plane (`dot(worldPos, Normal) + Offset` negative is discarded).</summary>
[<Struct>]
type SectionPlane =
    {
        Enabled: bool
        Normal: Vector3
        Offset: float32
    }

    /// <summary>Packs this plane into the shader uniform `vec4(normal.xyz, offset)`.</summary>
    member this.AsUniformVector4() =
        if
            not this.Enabled
            || this.Normal.LengthSquared () < 1e-6f
        then
            Vector4 (0.0f, 0.0f, 0.0f, 1.0f)
        else
            let n = Vector3.Normalize (this.Normal)
            Vector4 (n.X, n.Y, n.Z, this.Offset)

    /// <summary>Returns `true` when <paramref name="worldPos"/> lies in the clipped half-space.</summary>
    /// <param name="worldPos">3D world-space coordinate to test.</param>
    member this.IsClipped(worldPos: Vector3) =
        if
            not this.Enabled
            || this.Normal.LengthSquared () < 1e-6f
        then
            false
        else
            let n = Vector3.Normalize (this.Normal)
            Vector3.Dot (worldPos, n) + this.Offset < 0.0f

    override _.ToString() =
        "SectionPlane"

/// <summary>Constructors for <see cref="T:Ranvier.Wgpu.SectionPlane"/>.</summary>
module SectionPlane =
    /// <summary>Disabled clipping plane leaving all geometry intact.</summary>
    let disabled =
        {
            Enabled = false
            Normal = Vector3.UnitY
            Offset = 0.0f
        }

    /// <summary>Creates an active clipping plane with normalized `normal` and signed `offset`.</summary>
    let create (normal: Vector3) (offset: float32) =
        {
            Enabled = true
            Normal = Vector3.Normalize (normal)
            Offset = offset
        }

/// <summary>Hemisphere + key/rim directional lighting rig and section-plane contour highlight parameters.</summary>
/// <remarks>
/// <c>KeyLightDir</c> and <c>RimLightDir</c> are normalised when a pass is rendered, so only their direction
/// reaches the shader. Application data for a custom shader goes through <c>WgpuContext.SetUserUniforms</c>.
/// </remarks>
[<Struct>]
type LightingRig =
    {
        SkyColor: Vector3
        GroundColor: Vector3
        KeyLightDir: Vector3
        KeyLightIntensity: float32
        RimLightDir: Vector3
        RimLightIntensity: float32
        SectionContourColor: Vector3
        SectionContourWidth: float32
    }

    override _.ToString() =
        "LightingRig"

/// <summary>Presets for <see cref="T:Ranvier.Wgpu.LightingRig"/>.</summary>
module LightingRig =
    /// <summary>Balanced architectural studio hemisphere + key/rim rig with warm amber section contour.</summary>
    let studioDefault =
        {
            SkyColor = Vector3 (0.20f, 0.28f, 0.44f)
            GroundColor = Vector3 (0.09f, 0.08f, 0.07f)
            KeyLightDir = Vector3.Normalize (Vector3 (0.55f, 0.78f, 0.48f))
            KeyLightIntensity = 1.08f
            RimLightDir = Vector3.Normalize (Vector3 (-0.62f, 0.30f, -0.58f))
            RimLightIntensity = 0.44f
            SectionContourColor = Vector3 (1.0f, 0.72f, 0.18f)
            SectionContourWidth = 0.055f
        }

/// <summary>Right-handed perspective 3D camera parameters.</summary>
[<Struct>]
type Camera3D =
    {
        Position: Vector3
        Target: Vector3
        Up: Vector3
        FovYDeg: float32
        NearPlane: float32
        FarPlane: float32
    }

    override _.ToString() =
        "Camera3D"

/// <summary>Orbit camera construction, view/projection matrices, and screen-space ray/projection helpers.</summary>
module Camera3D =
    /// <summary>Constructs an orbit camera around `target` from spherical angles in degrees and `distance`.</summary>
    let orbit (target: Vector3) (yawDeg: float32) (pitchDeg: float32) (distance: float32) (fovYDeg: float32) : Camera3D =
        let clampedPitch = Math.Clamp (pitchDeg, -85.0f, 85.0f)
        let yaw = yawDeg * MathF.PI / 180.0f
        let pitch = clampedPitch * MathF.PI / 180.0f
        let cp = MathF.Cos pitch

        let offset =
            Vector3 (distance * cp * MathF.Sin yaw, distance * MathF.Sin pitch, distance * cp * MathF.Cos yaw)

        {
            Position = target + offset
            Target = target
            Up = Vector3.UnitY
            FovYDeg = fovYDeg
            NearPlane = 0.08f
            FarPlane = 240.0f
        }

    /// <summary>Right-handed look-at view matrix using row-vector `System.Numerics` convention.</summary>
    let viewMatrix (cam: Camera3D) : Matrix4x4 =
        let zAxis = Vector3.Normalize (cam.Position - cam.Target)

        let upRef =
            if MathF.Abs (Vector3.Dot (zAxis, Vector3.Normalize (cam.Up))) > 0.995f then
                Vector3.UnitZ
            else
                Vector3.Normalize (cam.Up)

        let xAxis = Vector3.Normalize (Vector3.Cross (upRef, zAxis))
        let yAxis = Vector3.Cross (zAxis, xAxis)

        Matrix4x4 (
            xAxis.X,
            yAxis.X,
            zAxis.X,
            0.0f,
            xAxis.Y,
            yAxis.Y,
            zAxis.Y,
            0.0f,
            xAxis.Z,
            yAxis.Z,
            zAxis.Z,
            0.0f,
            -Vector3.Dot(xAxis, cam.Position),
            -Vector3.Dot(yAxis, cam.Position),
            -Vector3.Dot(zAxis, cam.Position),
            1.0f
        )

    /// <summary>Right-handed perspective projection matrix with WebGPU `[0, 1]` NDC depth range.</summary>
    let projMatrix (aspect: float32) (cam: Camera3D) : Matrix4x4 =
        let fovRad = cam.FovYDeg * MathF.PI / 180.0f
        let f = 1.0f / MathF.Tan (fovRad * 0.5f)
        let safeAspect = max 0.05f aspect
        let zn = cam.NearPlane
        let zf = cam.FarPlane
        let rangeInv = 1.0f / (zn - zf)
        Matrix4x4 (f / safeAspect, 0.0f, 0.0f, 0.0f, 0.0f, f, 0.0f, 0.0f, 0.0f, 0.0f, zf * rangeInv, -1.0f, 0.0f, 0.0f, zn * zf * rangeInv, 0.0f)

    /// <summary>Combined `view * projection` matrix for a viewport of `width` by `height` pixels.</summary>
    let viewProjMatrix (width: int) (height: int) (cam: Camera3D) : Matrix4x4 =
        let aspect = float32 (max 1 width) / float32 (max 1 height)
        viewMatrix cam * projMatrix aspect cam

    /// <summary>Projects `worldPos` onto viewport pixel coordinates, or `ValueNone` when clipped.</summary>
    let projectToScreen (width: int) (height: int) (cam: Camera3D) (worldPos: Vector3) : Vector2 voption =
        let vp = viewProjMatrix width height cam
        let clip = Vector4.Transform (Vector4 (worldPos, 1.0f), vp)

        if clip.W <= 1e-4f then
            ValueNone
        else
            let invW = 1.0f / clip.W
            let ndcX = clip.X * invW
            let ndcY = clip.Y * invW
            let ndcZ = clip.Z * invW

            if ndcZ < 0.0f || ndcZ > 1.0f then
                ValueNone
            else
                let sx = (ndcX * 0.5f + 0.5f) * float32 width
                let sy = (0.5f - ndcY * 0.5f) * float32 height
                ValueSome (Vector2 (sx, sy))

    /// <summary>Unprojects pixel `(px, py)` into a world-space ray `struct (origin, unitDirection)`.</summary>
    let screenRay (width: int) (height: int) (cam: Camera3D) (px: float32) (py: float32) : struct (Vector3 * Vector3) =
        let vp = viewProjMatrix width height cam
        let mutable invVp = Matrix4x4.Identity

        if not (Matrix4x4.Invert (vp, &invVp)) then
            struct (cam.Position, Vector3.Normalize (cam.Target - cam.Position))
        else
            let ndcX = (px / float32 (max 1 width)) * 2.0f - 1.0f
            let ndcY = 1.0f - (py / float32 (max 1 height)) * 2.0f
            let nearClip = Vector4.Transform (Vector4 (ndcX, ndcY, 0.0f, 1.0f), invVp)
            let farClip = Vector4.Transform (Vector4 (ndcX, ndcY, 1.0f, 1.0f), invVp)

            let nearW =
                Vector3 (nearClip.X, nearClip.Y, nearClip.Z)
                / nearClip.W

            let farW =
                Vector3 (farClip.X, farClip.Y, farClip.Z)
                / farClip.W

            struct (nearW, Vector3.Normalize (farW - nearW))

/// <summary>Result of a 1x1 hardware GPU picking query (`PickId` and exact 3D `WorldPosition`).</summary>
[<Struct>]
type PickHit =
    {
        PickId: uint32
        WorldPosition: Vector3
    }

    override _.ToString() = "PickHit"

/// <summary>Per-frame GPU timing and geometry counters returned by scene render calls.</summary>
[<Struct>]
type FrameMetrics =
    {
        GpuRenderMs: float
        ReadbackMs: float
        DrawCalls: int
        InstanceCount: int
        TriangleCount: int
    }

    override _.ToString() =
        "FrameMetrics"
