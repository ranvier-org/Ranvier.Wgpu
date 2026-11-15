module Ranvier.Wgpu.Tests.Program

open System
open System.Collections.Concurrent
open System.Numerics
open System.Runtime.ExceptionServices
open System.Threading
open Expecto
open Ranvier.Dom
open Ranvier.View.WebCore
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Sdl

let private hostQueue = new BlockingCollection<unit -> unit> ()
let mutable private servedOnCaller = false

let private hostWorker =
    lazy
        (let t =
            Thread (
                ThreadStart (fun () ->
                    for work in hostQueue.GetConsumingEnumerable () do
                        work ()),
                64 * 1024 * 1024
            )

         t.IsBackground <- true
         t.Name <- "RanvierWgpuHostThread"
         t.Start ()
         t)

let private serveOnThisThread (run: unit -> int) : int =
    servedOnCaller <- true
    let mutable exitCode = 0
    let mutable failure: ExceptionDispatchInfo option = None

    let runner =
        Thread (
            ThreadStart (fun () ->
                try
                    try
                        exitCode <- run ()
                    with ex ->
                        failure <- Some (ExceptionDispatchInfo.Capture ex)
                finally
                    hostQueue.CompleteAdding ()),
            64 * 1024 * 1024
        )

    runner.Name <- "RanvierWgpuTestsRunner"
    runner.Start ()

    for work in hostQueue.GetConsumingEnumerable () do
        work ()

    runner.Join ()

    match failure with
    | Some edi -> edi.Throw ()
    | None -> ()

    exitCode

let private onHostThread (action: unit -> unit) =
    if not servedOnCaller then
        hostWorker.Force () |> ignore

    use doneSignal = new ManualResetEventSlim (false)
    let mutable captured: ExceptionDispatchInfo option = None

    hostQueue.Add (fun () ->
        try
            action ()
        with ex ->
            captured <- Some (ExceptionDispatchInfo.Capture ex)

        doneSignal.Set ())

    doneSignal.Wait ()

    match captured with
    | Some edi -> edi.Throw ()
    | None -> ()

let private rgbaAt (struct (width: int, _height: int, pixels: byte array)) (x: int) (y: int) =
    let idx = (y * width + x) * 4
    struct (pixels[idx], pixels[idx + 1], pixels[idx + 2], pixels[idx + 3])

/// Runs `action` with `Console.Error` redirected and returns what the WebGPU error and log callbacks printed.
let private captureWgpuConsoleErrors (action: unit -> unit) : string =
    let previous = Console.Error
    use writer = new IO.StringWriter ()
    Console.SetError writer

    try
        action ()
    finally
        Console.SetError previous

    writer.ToString ()

/// Fails when `printed` holds an uncaptured WebGPU error. Adapter warnings, which differ between machines, pass.
let private expectNoWgpuError (printed: string) (because: string) =
    if printed.Contains "[Ranvier.Wgpu Error" then
        failtestf "%s. Printed:\n%s" because printed

let private pixelAt (ctx: WgpuContext) (x: int) (y: int) =
    rgbaAt (struct (ctx.Width, ctx.Height, ctx.RgbaPixels)) x y

/// Camera on the +Z axis looking at the origin: world X runs right and world Y runs up on screen.
let private frontCamera = Camera3D.orbit Vector3.Zero 0.0f 0.0f 4.0f 45.0f

/// One opaque red hairline across the upper part of a 64x64 target, at row 16.
let private redLineAcrossRow16 =
    let red = RgbaColor (1.0f, 0.0f, 0.0f, 1.0f)

    [|
        LineVertex3D (Vector3 (-1.4f, 0.8f, 0.0f), red)
        LineVertex3D (Vector3 (1.4f, 0.8f, 0.0f), red)
    |]

/// One opaque green disc of 9 px radius at the centre of the target.
let private greenDiscAtCentre =
    [|
        DiscInstance3D.create Vector3.Zero 9.0f (RgbaColor (0.0f, 1.0f, 0.0f, 1.0f)) 0.0f 0.0f 0.0f 0.0f 5u InstanceFlags.None
    |]

/// Brightest red channel in column 32 between rows 12 and 20, where the hairline of `redLineAcrossRow16` lands.
let private brightestRedNearRow16 (ctx: WgpuContext) =
    let mutable best = 0uy

    for y = 12 to 20 do
        let struct (r, g, _, _) = pixelAt ctx 32 y

        if g < 60uy && r > best then
            best <- r

    best

/// A complete scene shader written from nothing: it defines only the two mesh entry points and paints magenta.
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

[<Tests>]
let wgpuContextTests =
    testSequenced
    <| testList
        "Ranvier.Wgpu"
        [
            testCase "a context with custom WGSL that keeps the stock entry points draws a line batch and a disc batch"
            <| fun () ->
                let custom =
                    Shaders.DefaultSceneWgsl
                    + "\n// a consumer's copy of the stock shader\n"

                use ctx =
                    new WgpuContext (initialWidth = 64, initialHeight = 64, customWgsl = custom)

                use lines = ctx.CreateLineBatch ()
                use discs = ctx.CreateDiscBatch ()
                lines.UpdateVertices redLineAcrossRow16
                discs.UpdateDiscs greenDiscAtCentre

                let metrics =
                    ScenePass.create frontCamera
                    |> ScenePass.withClear (RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
                    |> ScenePass.withLines [| lines |]
                    |> ScenePass.withDiscs [| discs |]
                    |> WgpuContext.render ctx

                Expect.equal metrics.DrawCalls 2 "one draw for the line batch and one for the disc batch"
                Expect.isGreaterThan (brightestRedNearRow16 ctx) 90uy "the red hairline is in the frame"
                let struct (dr, dg, _, _) = pixelAt ctx 32 32
                Expect.isGreaterThan dg 200uy "the green disc covers the centre"
                Expect.isLessThan dr 60uy "the centre is not red"

                let hit =
                    ScenePass.create frontCamera
                    |> ScenePass.withDiscs [| discs |]
                    |> WgpuContext.pick 32 32 ctx

                Expect.equal (hit |> ValueOption.map _.PickId) (ValueSome 5u) "the disc is pickable under custom WGSL"

            testCase "a context with custom WGSL that defines only vs_main and fs_main draws lines and discs with the built-in shaders"
            <| fun () ->
                let mutable metrics = Unchecked.defaultof<FrameMetrics>
                let mutable line = 0uy
                let mutable disc = struct (0uy, 0uy, 0uy, 0uy)
                let mutable mesh = struct (0uy, 0uy, 0uy, 0uy)
                let mutable hit = ValueNone

                let printed =
                    captureWgpuConsoleErrors (fun () ->
                        use ctx =
                            new WgpuContext (initialWidth = 64, initialHeight = 64, customWgsl = MagentaMeshOnlyWgsl)

                        use boxMesh = ctx.UploadMesh (Primitives.unitBox ())
                        use boxes = ctx.CreateBatch boxMesh
                        use lines = ctx.CreateLineBatch ()
                        use discs = ctx.CreateDiscBatch ()

                        // A small box in the lower part of the frame, clear of the line and of the disc.
                        boxes.SetSingle (
                            Instance3D.trs (Vector3 (0.0f, -1.0f, 0.0f)) 0.0f (Vector3 (0.6f, 0.6f, 0.6f)) RgbaColor.white 0.5f 9u InstanceFlags.None
                        )

                        lines.UpdateVertices redLineAcrossRow16
                        discs.UpdateDiscs greenDiscAtCentre

                        let pass =
                            ScenePass.create frontCamera
                            |> ScenePass.withClear (RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
                            |> ScenePass.withBatches [| boxes |]
                            |> ScenePass.withLines [| lines |]
                            |> ScenePass.withDiscs [| discs |]

                        metrics <- pass |> WgpuContext.render ctx
                        line <- brightestRedNearRow16 ctx
                        disc <- pixelAt ctx 32 32
                        mesh <- pixelAt ctx 32 51
                        hit <- pass |> WgpuContext.pick 32 51 ctx)

                expectNoWgpuError printed "nothing is reported for a shader that leaves the optional entry points out"
                Expect.equal metrics.DrawCalls 3 "mesh, line and disc batches are all drawn"
                let struct (mr, mg, mb, _) = mesh
                Expect.isGreaterThan mr 200uy "the custom fs_main painted the box (red channel)"
                Expect.isLessThan mg 60uy "the custom fs_main painted the box (green channel)"
                Expect.isGreaterThan mb 200uy "the custom fs_main painted the box (blue channel)"
                Expect.isGreaterThan line 90uy "the built-in line shader drew the hairline"
                let struct (dr, dg, _, _) = disc
                Expect.isGreaterThan dg 200uy "the built-in disc shader drew the disc"
                Expect.isLessThan dr 60uy "the disc is green"
                Expect.equal (hit |> ValueOption.map _.PickId) (ValueSome 9u) "picking falls back to the built-in pick shader"

            testCase "a context whose custom WGSL lacks a required entry point fails with a message naming it"
            <| fun () ->
                let noFragment = MagentaMeshOnlyWgsl.Replace ("fn fs_main()", "fn fs_other()")

                let message =
                    try
                        use ctx =
                            new WgpuContext (initialWidth = 16, initialHeight = 16, customWgsl = noFragment)

                        ""
                    with ex ->
                        ex.Message

                Expect.stringContains message "fs_main" "the failure names the missing entry point"

            testCase "a context given WGSL that uses the reserved word `patch` as an identifier fails with the compiler's message"
            <| fun () ->
                let invalid =
                    Shaders.DefaultSceneWgsl
                    + "\nfn broken() -> f32 {\n    let patch = 1.0;\n    return patch;\n}\n"

                let message =
                    try
                        use ctx =
                            new WgpuContext (initialWidth = 16, initialHeight = 16, customWgsl = invalid)

                        ""
                    with ex ->
                        ex.Message

                Expect.stringContains message "patch" "the failure quotes the offending identifier"

            testCase "ValidateWgsl returns the WGSL compiler's error without opening a window"
            <| fun () ->
                let printed =
                    captureWgpuConsoleErrors (fun () ->
                        let invalid =
                            Shaders.DefaultSceneWgsl
                            + "\nfn broken() -> f32 {\n    let patch = 1.0;\n    return patch;\n}\n"

                        match WgpuContext.ValidateWgsl invalid with
                        | Ok () -> failtest "WGSL using the reserved word `patch` as an identifier was accepted"
                        | Error message ->
                            Expect.stringContains message "patch" "the error quotes the offending identifier"
                            Expect.stringContains message "reserved" "the error says why the identifier is rejected"

                        Expect.equal (WgpuContext.ValidateWgsl Shaders.DefaultSceneWgsl) (Ok ()) "the stock shader is valid"
                        Expect.equal (WgpuContext.validateWgsl MagentaMeshOnlyWgsl) (Ok ()) "a shader with only the mesh entry points is valid"

                        let expectError (wgsl: string) (fragment: string) (because: string) =
                            match WgpuContext.ValidateWgsl wgsl with
                            | Ok () -> failtestf "accepted WGSL that should fail: %s" because
                            | Error message -> Expect.stringContains message fragment because

                        expectError (MagentaMeshOnlyWgsl.Replace ("fn vs_main(", "fn vs_other(")) "vs_main" "a missing required entry point is named"

                        expectError
                            (MagentaMeshOnlyWgsl
                             + "\n@fragment\nfn fs_line() -> @location(0) vec4<f32> {\n    return vec4<f32>(1.0);\n}\n")
                            "vs_line"
                            "half of the line entry points names the other half"

                        expectError
                            (MagentaMeshOnlyWgsl
                             + "\n@fragment\nfn fs_disc_pick() -> @location(0) vec4<u32> {\n    return vec4<u32>(1u);\n}\n")
                            "vs_disc"
                            "a disc picking shader without the disc shader is rejected"

                        // Compiles, but its user block is larger than the 16 slots the context binds.
                        expectError
                            (MagentaMeshOnlyWgsl.Replace (
                                "@fragment\nfn fs_main() -> @location(0) vec4<f32> {\n    return vec4<f32>(1.0, 0.0, 1.0, 1.0);",
                                "struct TooBig {\n    slots: array<vec4<f32>, 17>,\n};\n\n@group(0) @binding(1)\nvar<uniform> big: TooBig;\n\n@fragment\nfn fs_main() -> @location(0) vec4<f32> {\n    return big.slots[16];"
                            ))
                            "does not fit the scene pipelines"
                            "a shader that compiles but does not fit a pipeline is rejected")

                expectNoWgpuError printed "validation reports through its result and not through the console"

            testCase "user uniforms set on the context reach a custom shader"
            <| fun () ->
                // The block is declared here with a shape of the consumer's own; only its size is fixed.
                let wgsl =
                    MagentaMeshOnlyWgsl.Replace (
                        "@fragment\nfn fs_main() -> @location(0) vec4<f32> {\n    return vec4<f32>(1.0, 0.0, 1.0, 1.0);",
                        "struct AppData {\n    tint: vec4<f32>,\n    glow: vec4<f32>,\n};\n\n@group(0) @binding(1)\nvar<uniform> app: AppData;\n\n@fragment\nfn fs_main() -> @location(0) vec4<f32> {\n    return vec4<f32>(app.tint.rgb + app.glow.rgb, 1.0);"
                    )

                Expect.stringContains wgsl "app.tint" "the test shader reads the user block"
                use ctx = new WgpuContext (initialWidth = 64, initialHeight = 64, customWgsl = wgsl)
                use boxMesh = ctx.UploadMesh (Primitives.unitBox ())
                use boxes = ctx.CreateBatch boxMesh
                boxes.SetSingle (Instance3D.identity RgbaColor.white 0.5f 1u InstanceFlags.None)

                let pass =
                    ScenePass.create frontCamera
                    |> ScenePass.withClear (RgbaColor (1.0f, 1.0f, 1.0f, 1.0f))
                    |> ScenePass.withBatches [| boxes |]

                pass |> WgpuContext.render ctx |> ignore
                Expect.equal (pixelAt ctx 32 32) (struct (0uy, 0uy, 0uy, 255uy)) "every slot is zero until it is written"

                ctx.SetUserUniforms [| Vector4 (0.0f, 1.0f, 0.0f, 0.0f) |]
                pass |> WgpuContext.render ctx |> ignore
                Expect.equal (pixelAt ctx 32 32) (struct (0uy, 255uy, 0uy, 255uy)) "slot 0 reached the shader"

                ctx.SetUserUniform (1, Vector4 (0.0f, 0.0f, 1.0f, 0.0f))
                pass |> WgpuContext.render ctx |> ignore
                Expect.equal (pixelAt ctx 32 32) (struct (0uy, 255uy, 255uy, 255uy)) "slot 1 was written and slot 0 was kept"

                ctx
                |> WgpuContext.setUserUniforms [| Vector4 (1.0f, 0.0f, 0.0f, 0.0f); Vector4.Zero |]

                pass |> WgpuContext.render ctx |> ignore
                Expect.equal (pixelAt ctx 32 32) (struct (255uy, 0uy, 0uy, 255uy)) "both slots were replaced"

                Expect.throwsT<ArgumentException>
                    (fun () -> ctx.SetUserUniforms (Array.zeroCreate<Vector4>(Shaders.UserUniformSlots + 1)))
                    "more values than slots are rejected"

                Expect.throwsT<ArgumentOutOfRangeException>
                    (fun () -> ctx.SetUserUniform (Shaders.UserUniformSlots, Vector4.One))
                    "a slot outside the block is rejected"

            testCase "the stock shader's named extension points take a consumer's code without editing its text"
            <| fun () ->
                Expect.equal (Shaders.composeScene []) Shaders.DefaultSceneWgsl "with no hooks the composed shader is the stock shader"

                Expect.throwsT<ArgumentNullException>
                    (fun () ->
                        Shaders.composeScene [ SceneShaderHook.Declarations, null ]
                        |> ignore)
                    "null hook code is rejected"

                let twice =
                    Shaders.composeScene
                        [
                            SceneShaderHook.Declarations, "const FIRST: f32 = 1.0;"
                            SceneShaderHook.Declarations, "const SECOND: f32 = FIRST + 1.0;"
                        ]

                Expect.isLessThan (twice.IndexOf "const FIRST") (twice.IndexOf "const SECOND") "code for one hook keeps the order it was given in"

                let wgsl =
                    Shaders.composeScene
                        [
                            SceneShaderHook.Declarations, "fn tinted(base: vec3<f32>) -> vec3<f32> {\n    return base * user.slots[0].rgb;\n}"
                            SceneShaderHook.Varyings, "    @location(5) localPos: vec3<f32>,"
                            // Pick id 77 marks an instance the vertex stage moves out of the frame.
                            SceneShaderHook.VertexBegin,
                            "    if (in.pickFlags.x == 77u) {\n        var hidden: VsOut;\n        hidden.clipPos = vec4<f32>(2.0, 2.0, 2.0, 1.0);\n        return hidden;\n    }"
                            SceneShaderHook.VertexEnd, "    out.localPos = in.localPos;"
                            // Flag bit 8 marks an instance painted by the consumer's own function.
                            SceneShaderHook.FragmentBegin,
                            "    if ((flags & 256u) != 0u) {\n        return vec4<f32>(tinted(vec3<f32>(1.0)), 1.0);\n    }"
                            SceneShaderHook.FragmentLit, "    lit = vec3<f32>(0.0, 0.0, abs(in.localPos.z) * 100.0);"
                        ]

                Expect.equal (WgpuContext.ValidateWgsl wgsl) (Ok ()) "the composed shader is valid"
                use ctx = new WgpuContext (initialWidth = 64, initialHeight = 64, customWgsl = wgsl)
                use boxMesh = ctx.UploadMesh (Primitives.unitBox ())
                use boxes = ctx.CreateBatch boxMesh
                use lines = ctx.CreateLineBatch ()
                let size = Vector3 (0.5f, 0.5f, 0.5f)

                boxes.UpdateInstances
                    [|
                        Instance3D.trs (Vector3 (-0.9f, 0.0f, 0.0f)) 0.0f size RgbaColor.white 0.5f 11u (InstanceFlags.Unlit ||| 256u)
                        Instance3D.trs Vector3.Zero 0.0f size RgbaColor.white 0.5f 12u InstanceFlags.Unlit
                        Instance3D.trs (Vector3 (0.9f, 0.0f, 0.0f)) 0.0f size RgbaColor.white 0.5f 77u InstanceFlags.Unlit
                    |]

                lines.UpdateVertices redLineAcrossRow16
                ctx.SetUserUniform (0, Vector4 (0.0f, 1.0f, 0.0f, 0.0f))

                let pass =
                    ScenePass.create frontCamera
                    |> ScenePass.withClear (RgbaColor (0.0f, 0.0f, 0.0f, 1.0f))
                    |> ScenePass.withBatches [| boxes |]
                    |> ScenePass.withLines [| lines |]

                pass |> WgpuContext.render ctx |> ignore

                Expect.equal
                    (pixelAt ctx 12 32)
                    (struct (0uy, 255uy, 0uy, 255uy))
                    "FragmentBegin returned the colour of a Declarations function fed by a user uniform"

                let struct (cr, cg, cb, _) = pixelAt ctx 32 32
                Expect.isLessThan cr 30uy "FragmentLit replaced the lit colour (red channel)"
                Expect.isLessThan cg 30uy "FragmentLit replaced the lit colour (green channel)"
                Expect.isGreaterThan cb 200uy "the Varyings member assigned in VertexEnd reached FragmentLit"
                Expect.equal (pixelAt ctx 52 32) (struct (0uy, 0uy, 0uy, 255uy)) "VertexBegin moved the third box out of the frame"
                Expect.isGreaterThan (brightestRedNearRow16 ctx) 90uy "line batches still draw beside the hooks"

                Expect.equal
                    (pass
                     |> WgpuContext.pick 32 32 ctx
                     |> ValueOption.map _.PickId)
                    (ValueSome 12u)
                    "picking works with the composed shader"

                Expect.equal (pass |> WgpuContext.pick 52 32 ctx) ValueNone "the hidden box is not pickable either"

            testCase "batches with nothing in them are accepted by render and pick without a WebGPU error"
            <| fun () ->
                let printed =
                    captureWgpuConsoleErrors (fun () ->
                        use ctx = new WgpuContext (initialWidth = 32, initialHeight = 32)
                        use boxMesh = ctx.UploadMesh (Primitives.unitBox ())

                        use emptyMesh =
                            ctx.UploadMesh
                                {
                                    Vertices = Array.empty
                                    Indices = Array.empty
                                }

                        use neverUpdated = ctx.CreateBatch boxMesh
                        use updatedWithNothing = ctx.CreateBatch boxMesh
                        use emptied = ctx.CreateBatch (boxMesh, BlendMode.AlphaBlend, true)
                        use onEmptyMesh = ctx.CreateBatch emptyMesh
                        use linesNeverUpdated = ctx.CreateLineBatch ()
                        use linesEmptied = ctx.CreateLineBatch ()
                        use linesOneVertex = ctx.CreateLineBatch ()
                        use discsNeverUpdated = ctx.CreateDiscBatch ()
                        use discsEmptied = ctx.CreateDiscBatch ()

                        let box = Instance3D.identity RgbaColor.white 0.5f 1u InstanceFlags.None

                        updatedWithNothing.UpdateInstances (Array.empty<Instance3D>)
                        emptied.UpdateInstances [| box; box |]
                        emptied.UpdateInstances (ReadOnlySpan<Instance3D>.Empty)
                        onEmptyMesh.SetSingle box
                        linesEmptied.UpdateVertices redLineAcrossRow16
                        linesEmptied.UpdateVertices (Array.empty<LineVertex3D>)
                        linesOneVertex.UpdateVertices [| redLineAcrossRow16[0] |]
                        discsEmptied.UpdateDiscs greenDiscAtCentre
                        discsEmptied.UpdateDiscs (Array.empty<DiscInstance3D>)

                        let everyEmptyBatch =
                            ScenePass.create frontCamera
                            |> ScenePass.withBatches [| neverUpdated; updatedWithNothing; emptied; onEmptyMesh |]
                            |> ScenePass.withLines [| linesNeverUpdated; linesEmptied; linesOneVertex |]
                            |> ScenePass.withDiscs [| discsNeverUpdated; discsEmptied |]

                        let noBatches =
                            ScenePass.create frontCamera
                            |> ScenePass.withBatches Array.empty
                            |> ScenePass.withLines Array.empty
                            |> ScenePass.withDiscs Array.empty

                        for pass in [ everyEmptyBatch; noBatches; ScenePass.create frontCamera ] do
                            let metrics = pass |> WgpuContext.render ctx
                            Expect.equal metrics.DrawCalls 0 "nothing is drawn for empty batches"
                            Expect.equal (pass |> WgpuContext.pick 16 16 ctx) ValueNone "nothing is picked from empty batches"

                        // The legacy overloads build their pass from sequences.
                        ctx.Render (frontCamera, Seq.empty, lineBatches = Seq.empty, discBatches = Seq.empty)
                        |> ignore

                        ctx.Render (frontCamera, [ neverUpdated ], lineBatches = [ linesEmptied ], discBatches = [ discsEmptied ])
                        |> ignore)

                expectNoWgpuError printed "no WebGPU error is printed for empty batches"
        ]

[<Tests>]
let wgpuSdlTests =
    testSequenced
    <| testList
        "Ranvier.Wgpu.Sdl"
        [
            testCase "RgbaColor.fromHex parses 6 and 8 digit hex strings and rejects invalid lengths"
            <| fun () ->
                let c6 = RgbaColor.fromHex "#102040"
                Expect.equal (int (MathF.Round (c6.R * 255.0f))) 0x10 "6-digit R"
                Expect.equal (int (MathF.Round (c6.G * 255.0f))) 0x20 "6-digit G"
                Expect.equal (int (MathF.Round (c6.B * 255.0f))) 0x40 "6-digit B"
                Expect.equal c6.A 1.0f "6-digit A"

                let c8 = RgbaColor.fromHex "26d07c80"
                Expect.equal (int (MathF.Round (c8.A * 255.0f))) 0x80 "8-digit A"

                Expect.throwsT<FormatException> (fun () -> RgbaColor.fromHex "#fff" |> ignore) "invalid length throws FormatException"
                Expect.throwsT<ArgumentNullException> (fun () -> RgbaColor.fromHex null |> ignore) "null throws ArgumentNullException"

            testCase
                "functional ScenePass + WgpuSdlSurface pipeline composites HTML/SVG overlays, allows child button clicks, and prevents drag highlighting"
            <| fun () ->
                onHostThread (fun () ->
                    use window =
                        Window.Create ("WgpuSdlSurface Functional Pipeline Test", 320, 240, hidden = true)

                    window.Host.SetDeviceScale 1.0f
                    window.Host.Resize (320, 240)

                    window.Host.Document.body.innerHTML <-
                        """<style>
                            html, body { margin: 0; padding: 0; width: 320px; height: 240px; background: #000; overflow: hidden; }
                            #viewport { width: 320px; height: 240px; }
                            #hud-badge { position: absolute; left: 12px; top: 12px; width: 48px; height: 36px; background: #ff0000; z-index: 2; }
                            #hud-btn { position: absolute; left: 12px; top: 60px; width: 64px; height: 28px; z-index: 4; }
                            #svg-overlay { position: absolute; left: 0; top: 0; width: 320px; height: 240px; pointer-events: none; z-index: 3; }
                        </style>
                        <div id="viewport" tabindex="0">
                            <div id="hud-badge"></div>
                            <button id="hud-btn" type="button">Action</button>
                            <svg id="svg-overlay" viewBox="0 0 320 240">
                                <rect x="240" y="12" width="56" height="36" fill="#00ff00" />
                            </svg>
                        </div>"""

                    let viewport = window.Host.Document.getElementById "viewport"
                    let hudBtn = window.Host.Document.getElementById "hud-btn"
                    let mutable clicked = 0
                    hudBtn.addEventListener ("click", fun (_: Event) -> clicked <- clicked + 1)

                    use wgpu =
                        window
                        |> WgpuSdlSurface.createContextSized 320 240

                    use surface = viewport |> WgpuSdlSurface.bind window wgpu

                    // Default drag-selection protection on composited element
                    let styleAttr =
                        (viewport.getAttribute "style"
                         |> Option.ofObj
                         |> Option.defaultValue "")
                            .ToLowerInvariant()

                    Expect.stringContains styleAttr "user-select:none" "viewport disables user-select by default"
                    Expect.stringContains styleAttr "isolation:isolate" "viewport isolates stacking context"
                    Expect.equal (viewport.getAttribute "draggable") "false" "viewport disables native drag by default"

                    // Child button inside viewport still receives native pointer click!
                    window.Host.SetFocused true
                    window.Present ()
                    window.Host.Click (30, 74) |> ignore
                    window.Step 10 |> ignore
                    Expect.equal clicked 1 "interactive child button inside WgpuSdlSurface viewport receives click"

                    // Render an unlit blue sphere over a dark backdrop via ScenePass
                    use sphereMesh =
                        Primitives.unitIcosphere 2
                        |> WgpuContext.uploadMesh
                        <| wgpu

                    use batch = wgpu |> WgpuContext.createOpaqueBatch sphereMesh
                    let batches = [| batch |]
                    batch.SetSingle (Instance3D.sphere Vector3.Zero 2.2f (RgbaColor.fromRgb24 0uy 110uy 255uy) 0.3f 42u InstanceFlags.Unlit)

                    let cam = Camera3D.orbit Vector3.Zero 0.0f 0.0f 4.2f 45.0f

                    let pass =
                        ScenePass.create cam
                        |> ScenePass.withClear (RgbaColor.fromRgb24 8uy 14uy 28uy)
                        |> ScenePass.withBatches batches

                    let metrics = pass |> WgpuSdlSurface.render surface
                    Expect.equal metrics.InstanceCount 1 "rendered 1 sphere instance"
                    Expect.equal surface.FramesPresented 1L "published 1 GPU frame"

                    // GPU picking via ScenePass
                    let pick = pass |> WgpuContext.pick 160 120 wgpu
                    Expect.isTrue pick.IsSome "center pixel picks sphere"
                    Expect.equal pick.Value.PickId 42u "picked sphere PickId = 42u"

                    window.Present ()
                    let readBuf = Array.zeroCreate<byte>(320 * 240 * 4)
                    let struct (fw, fh) = window.ReadPixelsInto (Span<byte>(readBuf))
                    let frame = struct (fw, fh, readBuf)

                    // 1) HTML red badge at (24, 24) composites ON TOP of 3D surface
                    let struct (hr, hg, hb, ha) = rgbaAt frame 24 24
                    Expect.isGreaterThan hr 220uy "HTML badge red channel"
                    Expect.isLessThan hg 35uy "HTML badge green channel"
                    Expect.isLessThan hb 35uy "HTML badge blue channel"
                    Expect.equal ha 255uy "HTML badge alpha"

                    // 2) SVG green rect at (260, 24) composites ON TOP of 3D surface
                    let struct (sr, sg, sb, sa) = rgbaAt frame 260 24
                    Expect.isLessThan sr 35uy "SVG overlay red channel"
                    Expect.isGreaterThan sg 220uy "SVG overlay green channel"
                    Expect.isLessThan sb 35uy "SVG overlay blue channel"
                    Expect.equal sa 255uy "SVG overlay alpha"

                    // 3) Center (160, 120) shows the rendered 3D blue sphere
                    let struct (cr, cg, cb, ca) = rgbaAt frame 160 120
                    Expect.isLessThan cr 40uy "3D sphere red channel"
                    Expect.isGreaterThan cb 200uy "3D sphere blue channel"
                    Expect.isGreaterThan cg 60uy "3D sphere green channel"
                    Expect.equal ca 255uy "3D sphere alpha"

                    printfn
                        "[Ranvier.Wgpu.Tests] Backend=%s Adapter=%s Mode=%A ZeroCopy=%b Hardware=%b ReadbackMs=%.2f"
                        wgpu.BackendName
                        wgpu.AdapterName
                        surface.Mode
                        surface.IsZeroCopyGpu
                        surface.IsHardwareComposited
                        metrics.ReadbackMs

                    // 4) Hardware zero-copy assertions when D3D11 swap chain is active on Windows
                    if
                        OperatingSystem.IsWindows ()
                        && window.IsGpuComposited
                    then
                        Expect.isTrue surface.IsHardwareComposited "uses hardware D3D11 shared texture"
                        Expect.equal window.CpuFramesPresented 0L "zero CPU software frames presented"

                        let isSoftwareWarp =
                            match window.GpuAdapterVendorDevice with
                            | ValueSome (struct (0x1414u, _)) -> true
                            | _ -> false

                        if wgpu.BackendName = "D3D12" && not isSoftwareWarp then
                            Expect.equal surface.Mode WgpuPresentationMode.Dx12SharedTexture "uses zero-readback DX12 -> D3D11 VRAM path"
                            Expect.isTrue surface.IsZeroCopyGpu "IsZeroCopyGpu is true on DX12"
                            Expect.equal metrics.ReadbackMs 0.0 "zero CPU readback time on DX12 fast path")

            testCase "a WgpuSdlSurface presents passes whose batches are empty without a WebGPU error"
            <| fun () ->
                let mutable presented = 0L

                let printed =
                    captureWgpuConsoleErrors (fun () ->
                        onHostThread (fun () ->
                            use window =
                                Window.Create ("WgpuSdlSurface Empty Batch Test", 160, 120, hidden = true)

                            window.Host.SetDeviceScale 1.0f
                            window.Host.Resize (160, 120)
                            window.Host.Document.body.innerHTML <- """<div id="vp" style="width:160px;height:120px"></div>"""
                            let vp = window.Host.Document.getElementById "vp"

                            use wgpu =
                                window
                                |> WgpuSdlSurface.createContextSized 160 120

                            use surface = vp |> WgpuSdlSurface.bind window wgpu
                            use boxMesh = wgpu.UploadMesh (Primitives.unitBox ())
                            use neverUpdated = wgpu.CreateBatch boxMesh
                            use emptied = wgpu.CreateBatch (boxMesh, BlendMode.AlphaBlend, true)
                            use lines = wgpu.CreateLineBatch ()
                            use discs = wgpu.CreateDiscBatch ()

                            emptied.SetSingle (Instance3D.identity RgbaColor.white 0.5f 1u InstanceFlags.None)
                            emptied.UpdateInstances (Array.empty<Instance3D>)
                            lines.UpdateVertices (Array.empty<LineVertex3D>)
                            discs.UpdateDiscs (Array.empty<DiscInstance3D>)

                            let emptyBatches =
                                ScenePass.create frontCamera
                                |> ScenePass.withBatches [| neverUpdated; emptied |]
                                |> ScenePass.withLines [| lines |]
                                |> ScenePass.withDiscs [| discs |]

                            for pass in [ emptyBatches; ScenePass.create frontCamera ] do
                                let metrics = pass |> WgpuSdlSurface.render surface
                                Expect.equal metrics.DrawCalls 0 "nothing is drawn for empty batches"
                                window.Present ()

                            surface.Render (frontCamera, Seq.empty, lineBatches = [ lines ], discBatches = [ discs ])
                            |> ignore

                            window.Present ()
                            presented <- surface.FramesPresented))

                Expect.equal presented 3L "all three frames were presented"
                expectNoWgpuError printed "no WebGPU error is printed for empty batches"

            testCase "WgpuSdlSurface.lend restores element state on unmount, retains 3D GPU texture across HTML-only updates, and resizes cleanly"
            <| fun () ->
                onHostThread (fun () ->
                    use window =
                        Window.Create ("WgpuSdlSurface Lifecycle Test", 320, 240, hidden = true)

                    window.Host.SetDeviceScale 1.0f
                    window.Host.Resize (320, 240)

                    window.Host.Document.body.innerHTML <-
                        """<style>
                            html, body { margin: 0; padding: 0; width: 320px; height: 240px; background: #101010; overflow: hidden; }
                            .vp-small { width: 160px; height: 120px; }
                            .vp-large { width: 300px; height: 220px; }
                            #pin { position: absolute; left: 10px; top: 10px; width: 30px; height: 30px; background: #ff00ff; }
                        </style>
                        <div id="vp" class="vp-small" style="opacity:1"><div id="pin"></div></div>"""

                    let vp = window.Host.Document.getElementById "vp"
                    let pin = window.Host.Document.getElementById "pin"

                    use wgpu =
                        window
                        |> WgpuSdlSurface.createContextSized 160 120

                    use boxMesh =
                        Primitives.unitBox () |> WgpuContext.uploadMesh
                        <| wgpu

                    use batch = wgpu |> WgpuContext.createOpaqueBatch boxMesh
                    let batches = [| batch |]

                    batch.SetSingle (
                        Instance3D.trs
                            Vector3.Zero
                            0.0f
                            (Vector3 (4.0f, 4.0f, 1.0f))
                            (RgbaColor.fromRgb24 0uy 210uy 190uy)
                            0.4f
                            7u
                            InstanceFlags.Unlit
                    )

                    let cam = Camera3D.orbit Vector3.Zero 0.0f 0.0f 4.0f 45.0f

                    let pass =
                        ScenePass.create cam
                        |> ScenePass.withClear (RgbaColor.fromRgb24 5uy 5uy 10uy)
                        |> ScenePass.withBatches batches

                    let mutable capturedSurface: WgpuSdlSurface option = None
                    let mutable userCleanedUp = false

                    let lentSub =
                        vp
                        |> WgpuSdlSurface.lend window wgpu (fun surf ->
                            capturedSurface <- Some surf
                            pass |> WgpuSdlSurface.render surf |> ignore

                            { new IDisposable with
                                member _.Dispose() =
                                    userCleanedUp <- true
                            })

                    let surface = capturedSurface.Value
                    window.Present ()

                    // Mutate HTML overlay WITHOUT re-rendering 3D: 3D teal quad must stay intact inside 160x120!
                    pin.setAttribute ("style", "position:absolute;left:10px;top:10px;width:30px;height:30px;background:#ffff00")
                    window.Present ()
                    let afterDomOnly = window.ReadPixels ()
                    Expect.equal surface.FramesPresented 1L "still only 1 3D frame published"

                    let struct (pr, pg, pb, _) = rgbaAt afterDomOnly 20 20
                    Expect.isGreaterThan pr 220uy "updated HTML pin R (yellow)"
                    Expect.isGreaterThan pg 220uy "updated HTML pin G (yellow)"
                    Expect.isLessThan pb 35uy "updated HTML pin B (yellow)"

                    let struct (tr, tg, tb, _) = rgbaAt afterDomOnly 80 60
                    Expect.isLessThan tr 60uy "retained 3D center R"
                    Expect.isGreaterThan tg 160uy "retained 3D center G"
                    Expect.isGreaterThan tb 150uy "retained 3D center B"

                    // Pixel (240, 180) is outside the 160x120 viewport before resize (#101010 dark background)
                    let struct (bgR, bgG, bgB, _) = rgbaAt afterDomOnly 240 180
                    Expect.isLessThan bgR 40uy "outside viewport R before resize"
                    Expect.isLessThan bgG 40uy "outside viewport G before resize"
                    Expect.isLessThan bgB 40uy "outside viewport B before resize"

                    // Resize DOM element + WgpuSdlSurface to 300x220 and render at new dimensions
                    vp.setAttribute ("class", "vp-large")
                    surface |> WgpuSdlSurface.resize 300 220
                    Expect.equal surface.Width 300 "resized surface width"
                    Expect.equal surface.Height 220 "resized surface height"
                    pass |> WgpuSdlSurface.render surface |> ignore
                    window.Present ()
                    let afterResize = window.ReadPixels ()

                    // Pixel (240, 180) is now covered by the expanded 300x220 3D teal surface!
                    let struct (expR, expG, expB, _) = rgbaAt afterResize 240 180
                    Expect.isLessThan expR 60uy "expanded 3D surface R at (240, 180)"
                    Expect.isGreaterThan expG 160uy "expanded 3D surface G at (240, 180)"
                    Expect.isGreaterThan expB 150uy "expanded 3D surface B at (240, 180)"

                    // Disposing the lent subscription runs user cleanup, unbinds cleanly, and restores original style!
                    lentSub.Dispose ()
                    Expect.isTrue userCleanedUp "lent user disposable ran"
                    Expect.isTrue surface.IsDisposed "surface marked disposed"
                    Expect.equal (vp.getAttribute "style") "opacity:1" "original style restored on unmount"
                    Expect.isNull (vp.getAttribute "draggable") "injected draggable attribute removed on unmount"

                    Expect.throwsT<ObjectDisposedException>
                        (fun () -> pass |> WgpuSdlSurface.render surface |> ignore)
                        "rendering after Dispose throws ObjectDisposedException")
        ]

[<EntryPoint>]
let main argv =
    let run () =
        runTestsWithCLIArgs [ Sequenced ] argv (testList "all" [ wgpuContextTests; wgpuSdlTests ])

    // WebKit and SDL3 on macOS require window/host work on the process's main thread.
    if OperatingSystem.IsMacOS () then
        serveOnThisThread run
    else
        run ()
