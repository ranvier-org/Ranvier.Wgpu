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
        runTestsWithCLIArgs [ Sequenced ] argv wgpuSdlTests

    // WebKit and SDL3 on macOS require window/host work on the process's main thread.
    if OperatingSystem.IsMacOS () then
        serveOnThisThread run
    else
        run ()
