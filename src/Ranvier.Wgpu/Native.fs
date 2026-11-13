namespace Ranvier.Wgpu.Native

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop

#nowarn "9"
#nowarn "51"

module internal NativeResolver =
    let private syncObj = obj ()
    let mutable private registered = false

    let private hostRid () =
        let arch =
            match RuntimeInformation.ProcessArchitecture with
            | Architecture.Arm64 -> "arm64"
            | _ -> "x64"

        if OperatingSystem.IsWindows () then
            $"win-{arch}", "wgpu_native.dll"
        elif OperatingSystem.IsLinux () then
            $"linux-{arch}", "libwgpu_native.so"
        elif OperatingSystem.IsMacOS () then
            $"osx-{arch}", "libwgpu_native.dylib"
        else
            "", ""

    let private candidatePaths (baseDir: string) =
        let rid, fileName = hostRid ()

        if String.IsNullOrEmpty rid then
            [||]
        else
            [|
                Path.Combine (baseDir, fileName)
                Path.Combine (baseDir, "runtimes", rid, "native", fileName)
            |]

    let private resolve (libraryName: string) (assembly: Assembly) (searchPath: Nullable<DllImportSearchPath>) : nativeint =
        if libraryName <> WgpuDllName then
            0n
        else
            let mutable handle = 0n

            if NativeLibrary.TryLoad (libraryName, assembly, searchPath, &handle) then
                handle
            else
                let baseDir = AppContext.BaseDirectory
                let candidates = candidatePaths baseDir
                let mutable idx = 0

                while handle = 0n && idx < candidates.Length do
                    let path = candidates[idx]

                    if File.Exists path then
                        NativeLibrary.TryLoad (path, &handle) |> ignore

                    idx <- idx + 1

                handle

    let ensureRegistered () =
        if not registered then
            lock syncObj (fun () ->
                if not registered then
                    NativeLibrary.SetDllImportResolver (Assembly.GetExecutingAssembly (), DllImportResolver resolve)
                    registered <- true)

[<AutoOpen>]
module internal NativeHelpers =
    let inline addrOf (value: byref<'T>) : nativeint =
        NativePtr.toNativeInt &&value

    let withUtf8View (text: string) (action: WGPUStringView -> 'T) : 'T =
        let bytes = Encoding.UTF8.GetBytes (text)
        let handle = GCHandle.Alloc (bytes, GCHandleType.Pinned)

        try
            let mutable view = WGPUStringView ()
            view.Data <- handle.AddrOfPinnedObject ()
            view.Length <- unativeint bytes.Length
            action view
        finally
            handle.Free ()

    let backendDisplayName (backendType: uint32) =
        if backendType = WGPUBackendType_Vulkan then "Vulkan"
        elif backendType = WGPUBackendType_D3D12 then "D3D12"
        elif backendType = WGPUBackendType_D3D11 then "D3D11"
        elif backendType = WGPUBackendType_Metal then "Metal"
        elif backendType = WGPUBackendType_OpenGL then "OpenGL"
        elif backendType = WGPUBackendType_OpenGLES then "OpenGLES"
        else $"Backend({backendType})"

[<AutoOpen>]
module internal WgpuNativeInterop =
    [<DllImport(WgpuDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern nativeint wgpuDeviceGetNativeMetalDevice(nativeint device)

    [<DllImport(WgpuDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern nativeint wgpuQueueGetNativeMetalCommandQueue(nativeint queue)

    [<DllImport(WgpuDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern nativeint wgpuTextureGetNativeMetalTexture(nativeint texture)
