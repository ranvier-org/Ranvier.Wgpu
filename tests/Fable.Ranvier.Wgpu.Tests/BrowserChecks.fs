/// <summary>
/// What the browser package draws and picks for the shared scenes. <c>browser.test.mjs</c> runs these in a
/// headless browser page, on WebGPU and on WebGL2, and compares the results with the .NET package's.
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

/// <summary><c>MagentaMeshOnlyWgsl</c> in GLSL ES 3.00.</summary>
[<Literal>]
let private MagentaMeshOnlyGlsl =
    """
layout(std140) uniform SceneUniforms {
    mat4 viewProj;
    vec4 camPos;
    vec4 clipPlane;
    vec4 skyColor;
    vec4 groundColor;
    vec4 keyLight;
    vec4 rimLight;
    vec4 sectionStyle;
} u;

#ifdef VS_MAIN
layout(location = 0) in vec3 localPos;
layout(location = 3) in vec4 m0;
layout(location = 4) in vec4 m1;
layout(location = 5) in vec4 m2;
layout(location = 6) in vec4 m3;

void vs_main() {
    gl_Position = u.viewProj * mat4(m0, m1, m2, m3) * vec4(localPos, 1.0);
}
#endif

#ifdef FS_MAIN
vec4 fs_main() {
    return vec4(1.0, 0.0, 1.0, 1.0);
}
#endif
"""

/// <summary>The backend a check is asked to run on, by the name <c>BackendName</c> reports.</summary>
let private backendOf (name: string) : WgpuBrowserBackend =
    match name with
    | "WebGPU" -> WgpuBrowserBackend.WebGPU
    | "WebGL2" -> WgpuBrowserBackend.WebGL2
    | "Auto" -> WgpuBrowserBackend.Auto
    | other -> failwith $"Unknown backend '{other}'."

/// <summary>The stock GLSL with the hooks the shared scene gives the stock WGSL.</summary>
let private customGlsl () : string =
    GlslShaders.composeScene SceneCase.customHooks

/// <summary>
/// What the canvas itself holds, as the page would show it: the canvas drawn into a 2D canvas and read back
/// as straight RGBA bytes, rows from the top. Call it in the task that rendered, before the browser presents.
/// </summary>
[<Emit("(c => { const d = document.createElement('canvas'); d.width = c.width; d.height = c.height; const g = d.getContext('2d', { willReadFrequently: true }); g.drawImage(c, 0, 0); return new Uint8Array(g.getImageData(0, 0, c.width, c.height).data.buffer); })($0)")>]
let private canvasPixels (canvas: obj) : byte array = jsNative

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
let scene (canvas: obj) (backend: string) =
    jsAsync {
        let! ctx =
            WgpuContext.CreateAsync (canvas, SceneCase.Width, SceneCase.Height, backend = backendOf backend)

        let pass = SceneCase.build ctx
        let metrics = ctx.Render pass
        let shown = canvasPixels canvas
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
                shown = shown
                again = again
                picks = picks.ToArray ()
                together = [| pickResult x0 y0 firstHit; pickResult x1 y1 secondHit |]
                outsideIsNone = outside.IsNone
            |}

        ctx.Dispose ()
        return result
    }

/// <summary>
/// Draws a box with the shader built by <c>Shaders.composeScene</c>, or by <c>GlslShaders.composeScene</c> on
/// WebGL2, and reads the centre pixel.
/// </summary>
let composed (canvas: obj) (backend: string) =
    jsAsync {
        let size = SceneCase.CustomSize

        let! ctx =
            WgpuContext.CreateAsync (canvas, size, size, SceneCase.customWgsl (), customGlsl (), backendOf backend)

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
                backend = ctx.BackendName
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
/// Draws a box, a hairline and a disc with a shader that defines only <c>vs_main</c> and <c>fs_main</c>, so
/// the line, disc and pick pipelines come from the stock shader.
/// </summary>
let meshOnly (canvas: obj) (backend: string) =
    jsAsync {
        let! ctx =
            WgpuContext.CreateAsync (canvas, 64, 64, MagentaMeshOnlyWgsl, MagentaMeshOnlyGlsl, backendOf backend)

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
                backend = ctx.BackendName
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
                    isGlslException = false
                    message = ""
                |}
        with failure ->
            return
                {|
                    rejected = true
                    isWgslException = (failure :? WgslException)
                    isGlslException = (failure :? GlslException)
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

let private glslValidation (glsl: string) =
    jsAsync {
        let! result = WgpuContext.ValidateGlslAsync glsl

        return
            match result with
            | Ok () -> {| ok = true; message = "" |}
            | Error message -> {| ok = false; message = message |}
    }

/// <summary>
/// GLSL that cannot work on a context forced to WebGL2, through <c>CreateAsync</c> and through
/// <c>ValidateGlslAsync</c>: the counterpart of <c>badWgsl</c>, with a link failure added.
/// </summary>
let badGlsl (canvas: obj) =
    jsAsync {
        let create (glsl: string) =
            rejection (fun () -> WgpuContext.CreateAsync (canvas, 16, 16, customGlsl = glsl, backend = WgpuBrowserBackend.WebGL2))

        // Compiles nowhere: a name that is not declared.
        let unresolved =
            GlslShaders.DefaultSceneGlsl
            + "\nfloat broken() {\n    return notDeclaredAnywhere;\n}\n"

        // The line of the undeclared name, counted from the start of the text as an editor counts it.
        let unresolvedLine =
            unresolved.Substring(0, unresolved.IndexOf "notDeclaredAnywhere").Split('\n').Length

        // Compiles and links, and fits no pipeline: the vertex stage reads location 0 as an integer vector.
        let integerInput =
            MagentaMeshOnlyGlsl.Replace ("layout(location = 0) in vec3 localPos;", "layout(location = 0) in uvec3 localPosBits;")

        let wrongInput =
            integerInput.Replace ("vec4(localPos, 1.0)", "vec4(vec3(localPosBits), 1.0)")

        // Each stage compiles, and the two do not link: the fragment stage reads a varying nothing writes.
        let unwritten =
            MagentaMeshOnlyGlsl.Replace (
                "vec4 fs_main() {\n    return vec4(1.0, 0.0, 1.0, 1.0);",
                "in vec3 v_neverWritten;\nvec4 fs_main() {\n    return vec4(v_neverWritten, 1.0);"
            )

        // Links, and reads a uniform a context has no way to set.
        let looseUniform =
            MagentaMeshOnlyGlsl.Replace (
                "vec4 fs_main() {\n    return vec4(1.0, 0.0, 1.0, 1.0);",
                "uniform float brightness;\nvec4 fs_main() {\n    return vec4(brightness, 0.0, 1.0, 1.0);"
            )

        let noFragment = MagentaMeshOnlyGlsl.Replace ("vec4 fs_main()", "vec4 fs_other()")

        let! compile = create unresolved
        let! pipeline = create wrongInput
        let! link = create unwritten
        let! loose = create looseUniform
        let! missing = create noFragment

        let! validCompile = glslValidation unresolved
        let! validPipeline = glslValidation wrongInput
        let! validLink = glslValidation unwritten
        let! validMissing = glslValidation noFragment
        let! validStock = glslValidation GlslShaders.DefaultSceneGlsl
        let! validComposed = glslValidation (customGlsl ())
        let! validMeshOnly = glslValidation MagentaMeshOnlyGlsl

        // After the failures the canvas still takes a context.
        let! ctx =
            WgpuContext.CreateAsync (canvas, 16, 16, backend = WgpuBrowserBackend.WebGL2)

        let backend = ctx.BackendName
        ctx.Dispose ()

        return
            {|
                backendAfter = backend
                unresolvedLine = unresolvedLine
                compile = compile
                pipeline = pipeline
                link = link
                loose = loose
                missing = missing
                validCompile = validCompile
                validPipeline = validPipeline
                validLink = validLink
                validMissing = validMissing
                validStock = validStock
                validComposed = validComposed
                validMeshOnly = validMeshOnly
            |}
    }

/// <summary>
/// A custom shader given only in the language of the other graphics API: WGSL alone to a context on WebGL2,
/// GLSL alone to a context on WebGPU. Neither may draw the stock shader in its place.
/// </summary>
let otherLanguage (canvas: obj) (backend: string) =
    jsAsync {
        let! only =
            rejection (fun () ->
                if backend = "WebGL2" then
                    WgpuContext.CreateAsync (canvas, 16, 16, SceneCase.customWgsl (), backend = WgpuBrowserBackend.WebGL2)
                else
                    WgpuContext.CreateAsync (canvas, 16, 16, customGlsl = customGlsl (), backend = WgpuBrowserBackend.WebGPU))

        // A text whose entry points cannot make a context is refused whichever API draws.
        let! brokenOther =
            rejection (fun () ->
                if backend = "WebGL2" then
                    WgpuContext.CreateAsync (canvas, 16, 16, "fn vs_main() {}", customGlsl (), WgpuBrowserBackend.WebGL2)
                else
                    WgpuContext.CreateAsync (canvas, 16, 16, SceneCase.customWgsl (), "void vs_main() {}", WgpuBrowserBackend.WebGPU))

        // After the refusals the canvas still takes a context on that API.
        let! ctx = WgpuContext.CreateAsync (canvas, 16, 16, backend = backendOf backend)
        let after = ctx.BackendName
        ctx.Dispose ()

        return
            {|
                only = only
                brokenOther = brokenOther
                backendAfter = after
            |}
    }

/// <summary>Creates a context with the backend asked for and reports the one it got, or why it got none.</summary>
let chosen (canvas: obj) (backend: string) =
    jsAsync {
        try
            let! ctx = WgpuContext.CreateAsync (canvas, 8, 8, backend = backendOf backend)

            let result =
                {|
                    backend = ctx.BackendName
                    adapter = ctx.AdapterName
                    message = ""
                |}

            ctx.Dispose ()
            return result
        with failure ->
            return
                {|
                    backend = ""
                    adapter = ""
                    message = failure.Message
                |}
    }

/// <summary>
/// Clip-space depth. A box stands between the camera and the near plane, in front of a second box. WebGPU
/// keeps clip-space z from 0 to w, so the near box is cut away whole. A WebGL2 backend that passed the
/// camera's z through unchanged would keep z down to -w, which puts the near plane at half its distance,
/// and would draw the near box.
/// </summary>
let nearPlane (canvas: obj) (backend: string) =
    jsAsync {
        let! ctx = WgpuContext.CreateAsync (canvas, 64, 64, backend = backendOf backend)
        let boxMesh = ctx.UploadMesh (Primitives.unitBox ())
        let boxes = ctx.CreateBatch boxMesh
        let half = Matrix4x4.CreateScale (Vector3 (0.5f, 0.5f, 0.5f))

        boxes.UpdateInstances
            [|
                // 1.25 to 1.75 from the camera: nearer than the near plane at 2, farther than half of it.
                Instance3D.fromMatrix
                    (half
                     * Matrix4x4.CreateTranslation (Vector3 (0.0f, 0.0f, 2.5f)))
                    (RgbaColor (1.0f, 0.0f, 0.0f, 1.0f))
                    0.5f
                    1u
                    InstanceFlags.Unlit
                    0.0f
                // 3.5 to 4.5 from the camera.
                Instance3D.fromMatrix Matrix4x4.Identity (RgbaColor (0.0f, 1.0f, 0.0f, 1.0f)) 0.5f 2u InstanceFlags.Unlit 0.0f
            |]

        let pass =
            ScenePass.create
                {
                    Position = Vector3 (0.0f, 0.0f, 4.0f)
                    Target = Vector3.Zero
                    Up = Vector3.UnitY
                    FovYDeg = 45.0f
                    NearPlane = 2.0f
                    FarPlane = 240.0f
                }
            |> ScenePass.withClear (RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
            |> ScenePass.withBatches [| boxes |]

        ctx.Render pass |> ignore
        let! pixels = ctx.ReadPixelsAsync ()
        let! hit = ctx.PickAsync (32, 32, pass)

        let result =
            {|
                backend = ctx.BackendName
                centre = pixel pixels 64 32 32
                pickId = (pickResult 0 0 hit).pickId
                position = (pickResult 0 0 hit).position
            |}

        ctx.Dispose ()
        return result
    }

/// <summary>The six hook points, each with code that the others need, in WGSL.</summary>
let private everyHookWgsl: (SceneShaderHook * string) list =
    [
        (SceneShaderHook.Declarations, "fn tint(c: vec3<f32>) -> vec3<f32> {\n    return c * user.slots[0].rgb;\n}")
        (SceneShaderHook.Varyings, "    @location(5) height: f32,")
        (SceneShaderHook.VertexBegin, "    let lift = user.slots[1].x;")
        (SceneShaderHook.VertexEnd, "    out.height = worldPos4.y + lift;")
        (SceneShaderHook.FragmentBegin, "    if (isFront && (flags & 1u) != 0u) {\n        return vec4<f32>(0.0, 0.0, in.height, 1.0);\n    }")
        (SceneShaderHook.FragmentLit, "    lit = tint(vec3<f32>(1.0, 1.0, in.height));")
    ]

/// <summary><c>everyHookWgsl</c> in GLSL: the same statements under the naming of <c>GlslShaders</c>.</summary>
let private everyHookGlsl: (SceneShaderHook * string) list =
    [
        (SceneShaderHook.Declarations, "vec3 tint(vec3 c) {\n    return c * user.slots[0].rgb;\n}")
        (SceneShaderHook.Varyings, "VARYING float v_height;")
        (SceneShaderHook.VertexBegin, "    float lift = user.slots[1].x;")
        (SceneShaderHook.VertexEnd, "    v_height = worldPos4.y + lift;")
        (SceneShaderHook.FragmentBegin, "    if (isFront && (flags & 1u) != 0u) {\n        return vec4(0.0, 0.0, v_height, 1.0);\n    }")
        (SceneShaderHook.FragmentLit, "    lit = tint(vec3(1.0, 1.0, v_height));")
    ]

/// <summary>
/// A shader with code at every hook point. A lit box is coloured by a declared function from a varying the
/// vertex stage writes, and an unlit box beside it takes the early return of <c>FragmentBegin</c>.
/// </summary>
let everyHook (canvas: obj) (backend: string) =
    jsAsync {
        let wgsl = Shaders.composeScene everyHookWgsl
        let glsl = GlslShaders.composeScene everyHookGlsl
        let! ctx = WgpuContext.CreateAsync (canvas, 64, 64, wgsl, glsl, backendOf backend)
        let boxMesh = ctx.UploadMesh (Primitives.unitBox ())
        let boxes = ctx.CreateBatch boxMesh

        let at (x: float32) =
            Matrix4x4.CreateTranslation (Vector3 (x, 0.0f, 0.0f))

        boxes.UpdateInstances
            [|
                Instance3D.fromMatrix (at -0.7f) RgbaColor.white 0.5f 1u InstanceFlags.None 0.0f
                Instance3D.fromMatrix (at 0.7f) RgbaColor.white 0.5f 2u InstanceFlags.Unlit 0.0f
            |]

        ctx.SetUserUniforms [| Vector4 (1.0f, 0.5f, 0.25f, 0.0f); Vector4 (0.5f, 0.0f, 0.0f, 0.0f) |]

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

        ctx.Render pass |> ignore
        let! pixels = ctx.ReadPixelsAsync ()
        let! lit = ctx.PickAsync (20, 32, pass)
        let! unlit = ctx.PickAsync (44, 32, pass)

        let! validWgsl = WgpuContext.ValidateWgslAsync wgsl
        let! validGlsl = WgpuContext.ValidateGlslAsync glsl

        let result =
            {|
                backend = ctx.BackendName
                lit = pixel pixels 64 20 32
                unlit = pixel pixels 64 44 32
                litPickId = (pickResult 0 0 lit).pickId
                unlitPickId = (pickResult 0 0 unlit).pickId
                validWgsl = validWgsl.IsOk
                validGlsl = validGlsl.IsOk
            |}

        ctx.Dispose ()
        return result
    }

/// <summary>
/// Partial writes of the user uniform block. The shader adds slots 1 and 15, so a write that landed on the
/// wrong slot, or that cleared a slot it was not given, changes the colour.
/// </summary>
let userSlots (canvas: obj) (backend: string) =
    jsAsync {
        let hooks =
            [
                (SceneShaderHook.FragmentLit, "lit = user.slots[1].rgb + user.slots[15].rgb;")
            ]

        let size = SceneCase.CustomSize

        let! ctx =
            WgpuContext.CreateAsync (canvas, size, size, Shaders.composeScene hooks, GlslShaders.composeScene hooks, backendOf backend)

        let pass = SceneCase.buildCustom ctx

        let centre () =
            jsAsync {
                ctx.Render pass |> ignore
                let! pixels = ctx.ReadPixelsAsync ()
                return pixel pixels size (size / 2) (size / 2)
            }

        // Slots 0 and 1 written together: slot 1 is green and slot 15 is still zero.
        ctx.SetUserUniforms [| Vector4 (0.0f, 0.0f, 1.0f, 0.0f); Vector4 (0.0f, 1.0f, 0.0f, 0.0f) |]
        let! firstTwo = centre ()
        // The last slot alone: red is added to the green.
        ctx.SetUserUniform (15, Vector4 (1.0f, 0.0f, 0.0f, 0.0f))
        let! lastSlot = centre ()
        // Slots 0 and 1 again: slot 1 turns blue and slot 15 keeps its red.
        ctx.SetUserUniforms [| Vector4 (0.0f, 1.0f, 0.0f, 0.0f); Vector4 (0.0f, 0.0f, 1.0f, 0.0f) |]
        let! firstTwoAgain = centre ()

        let result =
            {|
                backend = ctx.BackendName
                firstTwo = firstTwo
                lastSlot = lastSlot
                firstTwoAgain = firstTwoAgain
            |}

        ctx.Dispose ()
        return result
    }

/// <summary>A pick that is still waiting for its pixel when the context is disposed.</summary>
let pickThenDispose (canvas: obj) (backend: string) =
    jsAsync {
        let! ctx = WgpuContext.CreateAsync (canvas, 64, 64, backend = backendOf backend)
        let pass = SceneCase.buildCustom ctx
        ctx.Render pass |> ignore
        let pending = ctx.PickAsync (32, 32, pass)
        ctx.Dispose ()

        try
            let! hit = pending

            return
                {|
                    settled = "resolved"
                    pickId = (pickResult 0 0 hit).pickId
                    message = ""
                |}
        with failure ->
            return
                {|
                    settled = "rejected"
                    pickId = 0u
                    message = failure.Message
                |}
    }

/// <summary>
/// The std140 layout the driver gives the two uniform blocks of the GLSL mesh program, to compare with the
/// offsets <c>Packing.fs</c> writes at.
/// </summary>
let uniformLayout () =
    WebGl2Backend.uniformLayout (customGlsl ())
