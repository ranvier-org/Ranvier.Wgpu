/// <summary>
/// Writes what the .NET package produces for the shared cases, for the JavaScript tests to compare with:
/// struct bytes, the scene uniform block, the pixels of the fixed scene, and its pick results.
/// </summary>
/// <remarks>
/// Usage: <c>dotnet run --project tests/Fable.Ranvier.Wgpu.Golden -- [backend] [output folder]</c>. The backend
/// is <c>auto</c>, <c>dx12</c>, <c>vulkan</c>, <c>metal</c> or <c>opengl</c>; the default is <c>dx12</c> on Windows, where
/// a browser draws WebGPU through Direct3D, and <c>auto</c> elsewhere. Graphics APIs rasterise hairlines by
/// different rules, so a frame is compared with one drawn through the same API. The default folder is
/// <c>tests/Fable.Ranvier.Wgpu.Tests/golden</c>. The context is offscreen; no window opens.
///
/// <c>packing.json</c> holds bytes that are the same on every machine, and is written first, before a GPU is
/// asked for. <c>scene.json</c> and <c>scene.rgba</c> depend on the GPU and driver that drew them, so they are a
/// reference for this machine only.
/// </remarks>
module Ranvier.Wgpu.Tests.Golden

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open Ranvier.Wgpu
open Ranvier.Wgpu.Tests

let private hex (bytes: ReadOnlySpan<byte>) =
    Convert.ToHexString(bytes).ToLowerInvariant()

let private structBytes<'T when 'T: struct and 'T: (new: unit -> 'T) and 'T :> ValueType> (values: 'T array) =
    hex (MemoryMarshal.AsBytes (ReadOnlySpan<'T>(values)))

[<EntryPoint>]
let main argv =
    let backendName =
        if argv.Length > 0 then argv[0].ToLowerInvariant()
        elif OperatingSystem.IsWindows () then "dx12"
        else "auto"

    let backend =
        match backendName with
        | "dx12" -> WgpuBackendPreference.Dx12
        | "vulkan" -> WgpuBackendPreference.Vulkan
        | "metal" -> WgpuBackendPreference.Metal
        | "opengl" -> WgpuBackendPreference.OpenGL
        | "auto" -> WgpuBackendPreference.Auto
        | other -> failwith $"Unknown backend '{other}'. Use auto, dx12, vulkan, metal or opengl."

    let folder =
        if argv.Length > 1 then
            argv[1]
        else
            Path.Combine (__SOURCE_DIRECTORY__, "..", "Fable.Ranvier.Wgpu.Tests", "golden")

    Directory.CreateDirectory folder |> ignore
    let json = StringBuilder ()

    let line (text: string) =
        json.Append(text).Append('\n') |> ignore

    line "{"
    line $"  \"vertices\": \"{structBytes PackingCases.vertices}\","
    line $"  \"instances\": \"{structBytes PackingCases.instances}\","
    // A line batch uploads an even number of endpoints.
    line $"  \"lineVertices\": \"{structBytes (Array.truncate 2 PackingCases.lineVertices)}\","
    line $"  \"discs\": \"{structBytes PackingCases.discs}\","
    line $"  \"userUniforms\": \"{structBytes PackingCases.userUniforms}\","
    line $"  \"normalized\": \"{structBytes (PackingCases.normalized ())}\","
    line $"  \"sceneUniforms\": \"{structBytes (PackingCases.sceneUniformValues ())}\","
    line $"  \"cameraMatrices\": \"{structBytes (PackingCases.cameraMatrices ())}\""
    line "}"
    File.WriteAllText (Path.Combine (folder, "packing.json"), json.ToString ())
    json.Clear () |> ignore
    line "{"

    use ctx =
        new WgpuContext (initialWidth = SceneCase.Width, initialHeight = SceneCase.Height, backend = backend)

    let pass = SceneCase.build ctx
    let metrics = ctx.Render pass
    File.WriteAllBytes (Path.Combine (folder, "scene.rgba"), ctx.RgbaPixels)

    line $"  \"adapter\": \"{ctx.AdapterName}\","
    line $"  \"backend\": \"{ctx.BackendName}\","

    line
        $"  \"scene\": {{ \"width\": {ctx.Width}, \"height\": {ctx.Height}, \"drawCalls\": {metrics.DrawCalls}, \"instances\": {metrics.InstanceCount}, \"triangles\": {metrics.TriangleCount} }},"

    line "  \"picks\": ["

    SceneCase.pickPixels
    |> Array.iteri (fun i (struct (x, y)) ->
        let comma = if i < SceneCase.pickPixels.Length - 1 then "," else ""

        match ctx.Pick (x, y, pass) with
        | ValueNone -> line $"    {{ \"x\": {x}, \"y\": {y}, \"pickId\": 0 }}{comma}"
        | ValueSome hit ->
            let p = hit.WorldPosition

            let bits (value: float32) =
                BitConverter.SingleToUInt32Bits value
            // The position as the bits of its three float32 values, so the comparison is exact.
            line $"    {{ \"x\": {x}, \"y\": {y}, \"pickId\": {hit.PickId}, \"positionBits\": [{bits p.X}, {bits p.Y}, {bits p.Z}] }}{comma}")

    line "  ],"

    use custom =
        new WgpuContext (
            initialWidth = SceneCase.CustomSize,
            initialHeight = SceneCase.CustomSize,
            customWgsl = SceneCase.customWgsl (),
            backend = backend
        )

    custom.Render (SceneCase.buildCustom custom)
    |> ignore

    let centre =
        (SceneCase.CustomSize / 2 * SceneCase.CustomSize
         + SceneCase.CustomSize / 2)
        * 4

    let pixels = custom.RgbaPixels
    line $"  \"customCentre\": [{pixels[centre]}, {pixels[centre + 1]}, {pixels[centre + 2]}, {pixels[centre + 3]}]"
    line "}"

    File.WriteAllText (Path.Combine (folder, "scene.json"), json.ToString ())
    printfn "Wrote packing.json, scene.json and scene.rgba to %s (%s, %s)" (Path.GetFullPath folder) ctx.AdapterName ctx.BackendName
    0
