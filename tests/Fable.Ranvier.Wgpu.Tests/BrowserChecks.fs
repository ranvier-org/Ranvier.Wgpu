/// <summary>
/// What the browser package draws and picks for the shared scenes. <c>browser.test.mjs</c> runs these in a
/// headless browser page and compares the results with the .NET package's.
/// </summary>
module Ranvier.Wgpu.Tests.BrowserChecks

open System
open System.Numerics
open Fable.Core
open Fickl.Core
open Ranvier.Wgpu

/// <summary>A complete scene shader written from nothing: only the two mesh entry points, painting magenta.</summary>
[<Literal>]
let private MagentaMeshOnlyWgsl =
    """
struct SceneUniforms {
    viewProj: mat4x4<f32>,
    camPos: vec4<f32>,
    clipPlane: vec4<f32>,
    skyColor: vec4<f32>,
    groundColor: vec4<f32>,
    keyLight: vec4<f32>,
    rimLight: vec4<f32>,
    sectionStyle: vec4<f32>,
};

@group(0) @binding(0)
var<uniform> u: SceneUniforms;

@vertex
fn vs_main(
    @location(0) localPos: vec3<f32>,
    @location(3) m0: vec4<f32>,
    @location(4) m1: vec4<f32>,
    @location(5) m2: vec4<f32>,
    @location(6) m3: vec4<f32>,
) -> @builtin(position) vec4<f32> {
    return u.viewProj * mat4x4<f32>(m0, m1, m2, m3) * vec4<f32>(localPos, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return vec4<f32>(1.0, 0.0, 1.0, 1.0);
}
"""

let private pixel (pixels: byte array) (width: int) (x: int) (y: int) : int array =
    let i = (y * width + x) * 4
    [| int pixels[i]; int pixels[i + 1]; int pixels[i + 2]; int pixels[i + 3] |]

/// <summary>Brightest red channel in column 32 between rows 12 and 20 of a 64-pixel-wide frame, where the hairline lands.</summary>
let private brightestRedNearRow16 (pixels: byte array) : int =
    let mutable best = 0

    for y = 12 to 20 do
        let p = pixel pixels 64 32 y

        if p[1] < 60 && p[0] > best then
            best <- p[0]

    best

let private pickResult (x: int) (y: int) (hit: PickHit voption) =
    match hit with
    | ValueNone ->
        {|
            x = x
            y = y
            pickId = 0u
            position = [||]
        |}
    | ValueSome hit ->
        {|
            x = x
            y = y
            pickId = hit.PickId
            position =
                [|
                    float hit.WorldPosition.X
                    float hit.WorldPosition.Y
                    float hit.WorldPosition.Z
                |]
        |}

/// <summary>
/// Draws the fixed scene, reads it back, picks every listed pixel, then draws and reads once more: a pick
/// rewrites the scene uniforms, and the second frame shows that a later frame is not disturbed by it.
/// </summary>
let scene (canvas: obj) =
    jsAsync {
        let! ctx = WgpuContext.CreateAsync (canvas, SceneCase.Width, SceneCase.Height)
        let pass = SceneCase.build ctx
        let metrics = ctx.Render pass
        let! pixels = ctx.ReadPixelsAsync ()
        let picks = ResizeArray ()

        for struct (x, y) in SceneCase.pickPixels do
            let! hit = ctx.PickAsync (x, y, pass)
            picks.Add (pickResult x y hit)

        // Two picks started together: the second cannot use the staging buffer the first has mapped.
        let struct (x0, y0) = SceneCase.pickPixels[0]
        let struct (x1, y1) = SceneCase.pickPixels[1]
        let first = ctx.PickAsync (x0, y0, pass)
        let second = ctx.PickAsync (x1, y1, pass)
        let! firstHit = first
        let! secondHit = second
        let! outside = ctx.PickAsync (-1, 0, pass)

        ctx.Render pass |> ignore
        let! again = ctx.ReadPixelsAsync ()

        let result =
            {|
                adapter = ctx.AdapterName
                backend = ctx.BackendName
                width = ctx.Width
                height = ctx.Height
                drawCalls = metrics.DrawCalls
                instances = metrics.InstanceCount
                triangles = metrics.TriangleCount
                gpuRenderMs = metrics.GpuRenderMs
                renderCalls = ctx.RenderCalls
                framesDelivered = ctx.FramesDelivered
                pixels = pixels
                again = again
                picks = picks.ToArray ()
                together = [| pickResult x0 y0 firstHit; pickResult x1 y1 secondHit |]
                outsideIsNone = outside.IsNone
            |}

        ctx.Dispose ()
        return result
    }

/// <summary>Draws a box with the shader built by <c>Shaders.composeScene</c> and reads the centre pixel.</summary>
let composed (canvas: obj) =
    jsAsync {
        let size = SceneCase.CustomSize
        let! ctx = WgpuContext.CreateAsync (canvas, size, size, SceneCase.customWgsl ())
        let pass = SceneCase.buildCustom ctx
        let metrics = ctx.Render pass
        let! pixels = ctx.ReadPixelsAsync ()
        let! hit = ctx.PickAsync (size / 2, size / 2, pass)

        // One slot rewritten: the box turns blue and the other slots stay.
        ctx.SetUserUniform (2, Vector4 (0.0f, 0.0f, 1.0f, 0.0f))
        ctx.Render pass |> ignore
        let! recoloured = ctx.ReadPixelsAsync ()

        // A smaller, taller target, drawn through the overload that takes the camera and the batches.
        ctx.Resize (40, 56)

        let canvasSize: int array =
            Fable.Core.JsInterop.emitJsExpr canvas "[$0.width, $0.height]"

        ctx.Render (pass.Camera, pass.Batches.ToArray (), clearColor = RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
        |> ignore

        let! resized = ctx.ReadPixelsAsync ()
        let! resizedHit = ctx.PickAsync (20, 28, pass)

        let result =
            {|
                drawCalls = metrics.DrawCalls
                centre = pixel pixels size (size / 2) (size / 2)
                corner = pixel pixels size 1 1
                pickId = (pickResult 0 0 hit).pickId
                recolouredCentre = pixel recoloured size (size / 2) (size / 2)
                resizedSize = [| ctx.Width; ctx.Height; canvasSize[0]; canvasSize[1]; resized.Length |]
                resizedCentre = pixel resized 40 20 28
                resizedCorner = pixel resized 40 1 1
                resizedPickId = (pickResult 0 0 resizedHit).pickId
            |}

        ctx.Dispose ()
        return result
    }

/// <summary>
/// Draws a box, a hairline and a disc with WGSL that defines only <c>vs_main</c> and <c>fs_main</c>, so the
/// line, disc and pick pipelines come from the stock shader.
/// </summary>
let meshOnly (canvas: obj) =
    jsAsync {
        let! ctx = WgpuContext.CreateAsync (canvas, 64, 64, MagentaMeshOnlyWgsl)
        let boxMesh = ctx.UploadMesh (Primitives.unitBox ())
        let boxes = ctx.CreateBatch boxMesh
        let lines = ctx.CreateLineBatch ()
        let discs = ctx.CreateDiscBatch ()
        let scale = Matrix4x4.CreateScale (Vector3 (0.6f, 0.6f, 0.6f))
        let down = Matrix4x4.CreateTranslation (Vector3 (0.0f, -1.0f, 0.0f))
        boxes.SetSingle (Instance3D.fromMatrix (scale * down) RgbaColor.white 0.5f 9u InstanceFlags.None 0.0f)
        let red = RgbaColor (1.0f, 0.0f, 0.0f, 1.0f)

        lines.UpdateVertices
            [|
                LineVertex3D (Vector3 (-1.4f, 0.8f, 0.0f), red)
                LineVertex3D (Vector3 (1.4f, 0.8f, 0.0f), red)
            |]

        discs.UpdateDiscs
            [|
                DiscInstance3D.create Vector3.Zero 9.0f (RgbaColor (0.0f, 1.0f, 0.0f, 1.0f)) 0.0f 0.0f 0.0f 0.0f 5u InstanceFlags.None
            |]

        let pass =
            ScenePass.create
                {
                    Position = Vector3 (0.0f, 0.0f, 4.0f)
                    Target = Vector3.Zero
                    Up = Vector3.UnitY
                    FovYDeg = 45.0f
                    NearPlane = 0.08f
                    FarPlane = 240.0f
                }
            |> ScenePass.withClear (RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
            |> ScenePass.withBatches [| boxes |]
            |> ScenePass.withLines [| lines |]
            |> ScenePass.withDiscs [| discs |]

        let metrics = ctx.Render pass
        let! pixels = ctx.ReadPixelsAsync ()
        let! boxHit = ctx.PickAsync (32, 51, pass)
        let! discHit = ctx.PickAsync (32, 32, pass)

        let result =
            {|
                drawCalls = metrics.DrawCalls
                box = pixel pixels 64 32 51
                disc = pixel pixels 64 32 32
                lineRed = brightestRedNearRow16 pixels
                boxPickId = (pickResult 0 0 boxHit).pickId
                discPickId = (pickResult 0 0 discHit).pickId
            |}

        ctx.Dispose ()
        return result
    }

let private rejection (create: unit -> JS.Promise<WgpuContext>) =
    jsAsync {
        try
            let! ctx = create ()
            ctx.Dispose ()

            return
                {|
                    rejected = false
                    isWgslException = false
                    message = ""
                |}
        with failure ->
            return
                {|
                    rejected = true
                    isWgslException = (failure :? WgslException)
                    message = failure.Message
                |}
    }

let private validation (wgsl: string) =
    jsAsync {
        let! result = WgpuContext.ValidateWgslAsync wgsl

        return
            match result with
            | Ok () -> {| ok = true; message = "" |}
            | Error message -> {| ok = false; message = message |}
    }

/// <summary>WGSL that cannot work, through <c>CreateAsync</c> and through <c>ValidateWgslAsync</c>.</summary>
let badWgsl (canvas: obj) =
    jsAsync {
        // Compiles nowhere: a name that is not declared.
        let unresolved =
            Shaders.DefaultSceneWgsl
            + "\nfn broken() -> f32 {\n    return notDeclaredAnywhere;\n}\n"

        // Compiles, and fits no pipeline: the vertex stage reads location 0 as an integer vector.
        let integerInput =
            MagentaMeshOnlyWgsl.Replace ("@location(0) localPos: vec3<f32>", "@location(0) localPosBits: vec3<u32>")

        let wrongInput =
            integerInput.Replace ("vec4<f32>(localPos, 1.0)", "vec4<f32>(vec3<f32>(localPosBits), 1.0)")

        let noFragment = MagentaMeshOnlyWgsl.Replace ("fn fs_main()", "fn fs_other()")

        let! compile =
            rejection (fun () -> WgpuContext.CreateAsync (canvas, 16, 16, unresolved))

        let! pipeline =
            rejection (fun () -> WgpuContext.CreateAsync (canvas, 16, 16, wrongInput))

        let! missing =
            rejection (fun () -> WgpuContext.CreateAsync (canvas, 16, 16, noFragment))

        let! validCompile = validation unresolved
        let! validPipeline = validation wrongInput
        let! validMissing = validation noFragment
        let! validStock = validation Shaders.DefaultSceneWgsl
        let! validComposed = validation (SceneCase.customWgsl ())

        // After the failures the canvas still takes a context.
        let! ctx = WgpuContext.CreateAsync (canvas, 16, 16)
        ctx.Dispose ()

        return
            {|
                compile = compile
                pipeline = pipeline
                missing = missing
                validCompile = validCompile
                validPipeline = validPipeline
                validMissing = validMissing
                validStock = validStock
                validComposed = validComposed
            |}
    }
