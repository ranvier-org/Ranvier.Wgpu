namespace Ranvier.Wgpu

open System
open System.Diagnostics
open System.Numerics
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop
open Ranvier.Wgpu.Native

#nowarn "9"
#nowarn "51"

[<Struct; StructLayout(LayoutKind.Sequential)>]
type internal GpuSceneUniforms =
    val mutable ViewProj: Matrix4x4
    val mutable CamPos: Vector4
    val mutable ClipPlane: Vector4
    val mutable SkyColor: Vector4
    val mutable GroundColor: Vector4
    val mutable KeyLight: Vector4
    val mutable RimLight: Vector4
    val mutable SectionStyle: Vector4

module private WgpuCallbacks =
    let mutable lastAdapterStatus = 0u
    let mutable lastAdapterHandle = 0n
    let mutable lastAdapterMessage = ""

    let mutable lastDeviceStatus = 0u
    let mutable lastDeviceHandle = 0n
    let mutable lastDeviceMessage = ""

    let mutable lastMapStatus = 0u
    let mutable lastMapDone = false

    let mutable lastScopeDone = false
    let mutable lastScopeType = 0u
    let mutable lastScopeMessage = ""

    let logDelegate =
        WGPULogCallback (fun level msg _ ->
            if level <= WGPULogLevel_Warn then
                Console.Error.WriteLine ($"[Ranvier.Wgpu] {msg.AsString ()}"))

    let errorDelegate =
        WGPUUncapturedErrorCallback (fun _ errType msg _ _ -> Console.Error.WriteLine ($"[Ranvier.Wgpu Error {errType}] {msg.AsString ()}"))

    let adapterDelegate =
        WGPURequestAdapterCallback (fun status adapter msg _ _ ->
            lastAdapterStatus <- status
            lastAdapterHandle <- adapter
            lastAdapterMessage <- msg.AsString ())

    let deviceDelegate =
        WGPURequestDeviceCallback (fun status device msg _ _ ->
            lastDeviceStatus <- status
            lastDeviceHandle <- device
            lastDeviceMessage <- msg.AsString ())

    let mapDelegate =
        WGPUBufferMapCallback (fun status _ _ _ ->
            lastMapStatus <- status
            lastMapDone <- true)

    let scopeDelegate =
        WGPUPopErrorScopeCallback (fun _ errType msg _ _ ->
            lastScopeType <- errType
            lastScopeMessage <- msg.AsString ()
            lastScopeDone <- true)

    let logPtr = Marshal.GetFunctionPointerForDelegate<WGPULogCallback>(logDelegate)

    let scopePtr =
        Marshal.GetFunctionPointerForDelegate<WGPUPopErrorScopeCallback>(scopeDelegate)

    let errorPtr =
        Marshal.GetFunctionPointerForDelegate<WGPUUncapturedErrorCallback>(errorDelegate)

    let adapterPtr =
        Marshal.GetFunctionPointerForDelegate<WGPURequestAdapterCallback>(adapterDelegate)

    let devicePtr =
        Marshal.GetFunctionPointerForDelegate<WGPURequestDeviceCallback>(deviceDelegate)

    let mapPtr =
        Marshal.GetFunctionPointerForDelegate<WGPUBufferMapCallback>(mapDelegate)

/// <summary>
/// Offscreen WebGPU (`wgpu-native` / `webgpu.h`) 3D context with 4x MSAA, runtime WGSL shaders,
/// premultiplied sRGB color targets, hardware instancing, BIM section clipping, and 1x1 scissor GPU pixel picking.
/// </summary>
[<Sealed>]
type WgpuContext
    (
        ?initialWidth: int,
        ?initialHeight: int,
        ?customWgsl: string,
        ?backend: WgpuBackendPreference,
        ?useBgraColorTarget: bool,
        ?preferredVendorDevice: struct (uint32 * uint32)
    ) =
    static let callbackLock = obj ()

    static let initOnce =
        lazy
            (NativeResolver.ensureRegistered ()
             wgpuSetLogCallback (WgpuCallbacks.logPtr, 0n)
             wgpuSetLogLevel (WGPULogLevel_Warn))

    do initOnce.Force ()

    let mutable disposed = false
    let mutable curWidth = max 1 (defaultArg initialWidth 960)
    let mutable curHeight = max 1 (defaultArg initialHeight 708)
    let wgslCode = defaultArg customWgsl Shaders.DefaultSceneWgsl

    // Decided from the text before any native handle exists, so a shader that cannot work costs nothing to reject.
    let entryPlan =
        match customWgsl with
        | None ->
            {
                CustomPick = true
                CustomLines = true
                CustomDiscs = true
                CustomDiscPick = true
            }
        | Some wgsl ->
            if isNull wgsl then
                nullArg (nameof customWgsl)

            match Shaders.planSceneEntryPoints wgsl with
            | Ok plan -> plan
            | Error reason -> raise (WgslException reason)

    let requestedBackend = defaultArg backend WgpuBackendPreference.Auto

    let colorTargetFormat =
        if defaultArg useBgraColorTarget false then
            WGPUTextureFormat_BGRA8Unorm
        else
            WGPUTextureFormat_RGBA8Unorm

    let tryCreateBackend (pref: WgpuBackendPreference) =
        let inst =
            match pref with
            | WgpuBackendPreference.Auto -> wgpuCreateInstance (0n)
            | _ ->
                let mutable extras = WGPUInstanceExtras ()
                extras.Chain.SType <- WGPUSType_InstanceExtras
                extras.DxcPath <- WGPUStringView.Empty

                match pref with
                | WgpuBackendPreference.Dx12 ->
                    extras.Backends <- WGPUInstanceBackend_DX12
                    extras.Dx12ShaderCompiler <- WGPUDx12Compiler_Fxc
                | WgpuBackendPreference.Vulkan -> extras.Backends <- WGPUInstanceBackend_Vulkan
                | WgpuBackendPreference.Metal -> extras.Backends <- WGPUInstanceBackend_Metal
                | WgpuBackendPreference.OpenGL -> extras.Backends <- WGPUInstanceBackend_GL
                | WgpuBackendPreference.Auto -> extras.Backends <- WGPUInstanceBackend_All

                let mutable instDesc = WGPUInstanceDescriptor ()
                instDesc.NextInChain <- addrOf &extras
                wgpuCreateInstance (addrOf &instDesc)

        if inst = 0n then
            ValueNone
        else
            let tryMatchPreferredAdapter () =
                match preferredVendorDevice with
                | None -> ValueNone
                | Some (struct (wantVendor, wantDevice)) ->
                    let mutable enumOpts = WGPUInstanceEnumerateAdapterOptions ()

                    enumOpts.Backends <-
                        match pref with
                        | WgpuBackendPreference.Dx12 -> WGPUInstanceBackend_DX12
                        | WgpuBackendPreference.Vulkan -> WGPUInstanceBackend_Vulkan
                        | WgpuBackendPreference.Metal -> WGPUInstanceBackend_Metal
                        | WgpuBackendPreference.OpenGL -> WGPUInstanceBackend_GL
                        | WgpuBackendPreference.Auto -> WGPUInstanceBackend_All

                    let count = int (wgpuInstanceEnumerateAdapters (inst, addrOf &enumOpts, 0n))

                    if count <= 0 then
                        ValueNone
                    else
                        let handles = Array.zeroCreate<nativeint> count
                        use pHandles = fixed handles

                        let got =
                            int (wgpuInstanceEnumerateAdapters (inst, addrOf &enumOpts, NativePtr.toNativeInt pHandles))

                        let mutable chosen = ValueNone

                        for i = 0 to min count got - 1 do
                            let h = handles[i]

                            if h <> 0n then
                                match chosen with
                                | ValueSome _ -> wgpuAdapterRelease h
                                | ValueNone ->
                                    let mutable info = WGPUAdapterInfo ()
                                    info.Vendor <- WGPUStringView.Null
                                    info.Architecture <- WGPUStringView.Null
                                    info.Device <- WGPUStringView.Null
                                    info.Description <- WGPUStringView.Null

                                    if wgpuAdapterGetInfo (h, addrOf &info) = WGPUStatus_Success then
                                        let matches =
                                            info.VendorID = wantVendor
                                            && info.DeviceID = wantDevice

                                        let dName = info.Device.AsString ()
                                        let bType = info.BackendType
                                        let bName = backendDisplayName bType
                                        wgpuAdapterInfoFreeMembers (info)

                                        if matches then
                                            chosen <- ValueSome (struct (h, dName, bType, bName))
                                        else
                                            wgpuAdapterRelease h
                                    else
                                        wgpuAdapterRelease h

                        chosen

            let adapterOpt =
                match tryMatchPreferredAdapter () with
                | ValueSome matched -> ValueSome matched
                | ValueNone ->
                    lock callbackLock (fun () ->
                        WgpuCallbacks.lastAdapterStatus <- 0u
                        WgpuCallbacks.lastAdapterHandle <- 0n
                        let mutable opts = WGPURequestAdapterOptions ()
                        opts.PowerPreference <- WGPUPowerPreference_HighPerformance

                        opts.BackendType <-
                            match pref with
                            | WgpuBackendPreference.Auto -> WGPUBackendType_Undefined
                            | WgpuBackendPreference.Dx12 -> WGPUBackendType_D3D12
                            | WgpuBackendPreference.Vulkan -> WGPUBackendType_Vulkan
                            | WgpuBackendPreference.Metal -> WGPUBackendType_Metal
                            | WgpuBackendPreference.OpenGL -> WGPUBackendType_OpenGL

                        let mutable cbInfo = WGPURequestAdapterCallbackInfo ()
                        cbInfo.Mode <- WGPUCallbackMode_AllowSpontaneous
                        cbInfo.Callback <- WgpuCallbacks.adapterPtr
                        let _ = wgpuInstanceRequestAdapter (inst, addrOf &opts, cbInfo)

                        if WgpuCallbacks.lastAdapterStatus = 0u then
                            wgpuInstanceProcessEvents (inst)

                        if
                            WgpuCallbacks.lastAdapterStatus
                            <> WGPURequestAdapterStatus_Success
                            || WgpuCallbacks.lastAdapterHandle = 0n
                        then
                            WgpuCallbacks.lastAdapterStatus <- 0u
                            WgpuCallbacks.lastAdapterHandle <- 0n
                            opts.PowerPreference <- WGPUPowerPreference_Undefined
                            opts.ForceFallbackAdapter <- 1u
                            let _ = wgpuInstanceRequestAdapter (inst, addrOf &opts, cbInfo)

                            if WgpuCallbacks.lastAdapterStatus = 0u then
                                wgpuInstanceProcessEvents (inst)

                        if
                            WgpuCallbacks.lastAdapterStatus
                            <> WGPURequestAdapterStatus_Success
                            || WgpuCallbacks.lastAdapterHandle = 0n
                        then
                            ValueNone
                        else
                            let adp = WgpuCallbacks.lastAdapterHandle
                            let mutable info = WGPUAdapterInfo ()
                            info.Vendor <- WGPUStringView.Null
                            info.Architecture <- WGPUStringView.Null
                            info.Device <- WGPUStringView.Null
                            info.Description <- WGPUStringView.Null

                            if wgpuAdapterGetInfo (adp, addrOf &info) = WGPUStatus_Success then
                                let dName = info.Device.AsString ()
                                let bType = info.BackendType
                                let bName = backendDisplayName bType
                                wgpuAdapterInfoFreeMembers (info)
                                ValueSome (struct (adp, dName, bType, bName))
                            else
                                ValueSome (struct (adp, "WebGPU Adapter", WGPUBackendType_Undefined, "Unknown")))

            match adapterOpt with
            | ValueNone ->
                wgpuInstanceRelease inst
                ValueNone
            | ValueSome (struct (adp, dName, bType, bName)) ->
                let devOpt =
                    lock callbackLock (fun () ->
                        WgpuCallbacks.lastDeviceStatus <- 0u
                        WgpuCallbacks.lastDeviceHandle <- 0n
                        let mutable desc = WGPUDeviceDescriptor ()
                        desc.Label <- WGPUStringView.Null
                        desc.DefaultQueue.Label <- WGPUStringView.Null
                        desc.UncapturedErrorCallbackInfo.Callback <- WgpuCallbacks.errorPtr
                        let mutable cbInfo = WGPURequestDeviceCallbackInfo ()
                        cbInfo.Mode <- WGPUCallbackMode_AllowSpontaneous
                        cbInfo.Callback <- WgpuCallbacks.devicePtr
                        let _ = wgpuAdapterRequestDevice (adp, addrOf &desc, cbInfo)

                        if WgpuCallbacks.lastDeviceStatus = 0u then
                            wgpuInstanceProcessEvents (inst)

                        if
                            WgpuCallbacks.lastDeviceStatus
                            <> WGPURequestDeviceStatus_Success
                            || WgpuCallbacks.lastDeviceHandle = 0n
                        then
                            ValueNone
                        else
                            ValueSome WgpuCallbacks.lastDeviceHandle)

                match devOpt with
                | ValueNone ->
                    wgpuAdapterRelease adp
                    wgpuInstanceRelease inst
                    ValueNone
                | ValueSome dev -> ValueSome (struct (inst, adp, dev, dName, bType, bName))

    let struct (instance, adapter, device, adapterDeviceName, adapterBackendType, adapterBackendName) =
        match tryCreateBackend requestedBackend with
        | ValueSome result -> result
        | ValueNone when requestedBackend <> WgpuBackendPreference.Auto ->
            match tryCreateBackend WgpuBackendPreference.Auto with
            | ValueSome fallback -> fallback
            | ValueNone ->
                failwith $"WgpuContext failed to initialize WebGPU device ({WgpuCallbacks.lastAdapterMessage} / {WgpuCallbacks.lastDeviceMessage})."
        | ValueNone ->
            failwith $"WgpuContext failed to initialize WebGPU device ({WgpuCallbacks.lastAdapterMessage} / {WgpuCallbacks.lastDeviceMessage})."

    let queue = wgpuDeviceGetQueue (device)

    let releaseDevice () =
        if queue <> 0n then
            wgpuQueueRelease queue

        if device <> 0n then
            wgpuDeviceRelease device

        if adapter <> 0n then
            wgpuAdapterRelease adapter

        if instance <> 0n then
            wgpuInstanceRelease instance

    // Ends the innermost validation error scope and returns the first error raised inside it.
    let popValidationError () : string voption =
        lock callbackLock (fun () ->
            WgpuCallbacks.lastScopeDone <- false
            WgpuCallbacks.lastScopeType <- WGPUErrorType_NoError
            WgpuCallbacks.lastScopeMessage <- ""
            let mutable cbInfo = WGPUPopErrorScopeCallbackInfo ()
            cbInfo.Mode <- WGPUCallbackMode_AllowSpontaneous
            cbInfo.Callback <- WgpuCallbacks.scopePtr
            let _ = wgpuDevicePopErrorScope (device, cbInfo)

            if not WgpuCallbacks.lastScopeDone then
                wgpuInstanceProcessEvents (instance)

            if
                WgpuCallbacks.lastScopeDone
                && WgpuCallbacks.lastScopeType
                   <> WGPUErrorType_NoError
            then
                ValueSome WgpuCallbacks.lastScopeMessage
            else
                ValueNone)

    let compileModule (code: string) : nativeint =
        withUtf8View code (fun codeView ->
            let mutable wgsl = WGPUShaderSourceWGSL ()
            wgsl.Chain.SType <- WGPUSType_ShaderSourceWGSL
            wgsl.Code <- codeView
            let mutable smDesc = WGPUShaderModuleDescriptor ()
            smDesc.NextInChain <- addrOf &wgsl
            smDesc.Label <- WGPUStringView.Null
            wgpuDeviceCreateShaderModule (device, addrOf &smDesc))

    // A WGSL compile error is collected in an error scope and thrown. Left uncaptured it is only printed, and
    // the first queue submit that uses a pipeline of the broken module then aborts the process.
    let shaderModule =
        wgpuDevicePushErrorScope (device, WGPUErrorFilter_Validation)
        let compiled = compileModule wgslCode

        match popValidationError () with
        | ValueNone when compiled <> 0n -> compiled
        | failure ->
            if compiled <> 0n then
                wgpuShaderModuleRelease compiled

            releaseDevice ()

            match failure with
            | ValueSome message -> raise (WgslException $"WGSL failed to compile. {message}")
            | ValueNone -> failwith "wgpuDeviceCreateShaderModule failed compiling WGSL."

    // The stock shader supplies the entry points that custom WGSL leaves out, so line, disc and picking
    // pipelines exist on every context.
    let stockModule =
        if
            entryPlan.CustomPick
            && entryPlan.CustomLines
            && entryPlan.CustomDiscs
            && entryPlan.CustomDiscPick
        then
            0n
        else
            compileModule Shaders.DefaultSceneWgsl

    let pickModule = if entryPlan.CustomPick then shaderModule else stockModule
    let lineModule = if entryPlan.CustomLines then shaderModule else stockModule
    let discModule = if entryPlan.CustomDiscs then shaderModule else stockModule

    let discPickModule =
        if entryPlan.CustomDiscPick then
            shaderModule
        else
            stockModule

    let uniformSize = uint64 ((sizeof<GpuSceneUniforms> + 15) &&& ~~~15)

    let uniformBuffer =
        let mutable desc = WGPUBufferDescriptor ()

        desc.Usage <-
            WGPUBufferUsage_Uniform
            ||| WGPUBufferUsage_CopyDst

        desc.Size <- uniformSize
        let b = wgpuDeviceCreateBuffer (device, addrOf &desc)

        if b = 0n then
            failwith "wgpuDeviceCreateBuffer (uniformBuffer) failed."

        b

    let userUniformSize = uint64 (Shaders.UserUniformSlots * sizeof<Vector4>)

    let userUniformBuffer =
        let mutable desc = WGPUBufferDescriptor ()

        desc.Usage <-
            WGPUBufferUsage_Uniform
            ||| WGPUBufferUsage_CopyDst

        desc.Size <- userUniformSize
        let b = wgpuDeviceCreateBuffer (device, addrOf &desc)

        if b = 0n then
            failwith "wgpuDeviceCreateBuffer (userUniformBuffer) failed."

        b

    let bindGroupLayout =
        let uniformEntry (binding: uint32) (size: uint64) =
            let mutable entry = WGPUBindGroupLayoutEntry ()
            entry.Binding <- binding

            entry.Visibility <-
                WGPUShaderStage_Vertex
                ||| WGPUShaderStage_Fragment

            entry.Buffer.Type <- WGPUBufferBindingType_Uniform
            entry.Buffer.MinBindingSize <- size
            entry

        let entries = [| uniformEntry 0u uniformSize; uniformEntry 1u userUniformSize |]

        use pEntries = fixed entries
        let mutable desc = WGPUBindGroupLayoutDescriptor ()
        desc.Label <- WGPUStringView.Null
        desc.EntryCount <- unativeint entries.Length
        desc.Entries <- NativePtr.toNativeInt pEntries
        let bgl = wgpuDeviceCreateBindGroupLayout (device, addrOf &desc)

        if bgl = 0n then
            failwith "wgpuDeviceCreateBindGroupLayout failed."

        bgl

    let bindGroup =
        let bufferEntry (binding: uint32) (buffer: nativeint) (size: uint64) =
            let mutable entry = WGPUBindGroupEntry ()
            entry.Binding <- binding
            entry.Buffer <- buffer
            entry.Offset <- 0uL
            entry.Size <- size
            entry

        let entries =
            [|
                bufferEntry 0u uniformBuffer uniformSize
                bufferEntry 1u userUniformBuffer userUniformSize
            |]

        use pEntries = fixed entries
        let mutable desc = WGPUBindGroupDescriptor ()
        desc.Label <- WGPUStringView.Null
        desc.Layout <- bindGroupLayout
        desc.EntryCount <- unativeint entries.Length
        desc.Entries <- NativePtr.toNativeInt pEntries
        let bg = wgpuDeviceCreateBindGroup (device, addrOf &desc)

        if bg = 0n then
            failwith "wgpuDeviceCreateBindGroup failed."

        bg

    let pipelineLayout =
        let mutable bgl = bindGroupLayout
        let mutable desc = WGPUPipelineLayoutDescriptor ()
        desc.Label <- WGPUStringView.Null
        desc.BindGroupLayoutCount <- 1un
        desc.BindGroupLayouts <- addrOf &bgl
        let pl = wgpuDeviceCreatePipelineLayout (device, addrOf &desc)

        if pl = 0n then
            failwith "wgpuDeviceCreatePipelineLayout failed."

        pl

    let buildPipeline
        (sceneModule: nativeint)
        (fsEntry: string)
        (colorFormat: uint32)
        (sampleCount: uint32)
        (cullBack: bool)
        (alphaBlend: bool)
        (depthWrite: bool)
        =
        let vAttrs =
            [|
                let mutable a0 = WGPUVertexAttribute () in
                a0.Format <- WGPUVertexFormat_Float32x3
                a0.Offset <- 0uL
                a0.ShaderLocation <- 0u
                a0
                let mutable a1 = WGPUVertexAttribute () in
                a1.Format <- WGPUVertexFormat_Float32x3
                a1.Offset <- 12uL
                a1.ShaderLocation <- 1u
                a1
                let mutable a2 = WGPUVertexAttribute () in
                a2.Format <- WGPUVertexFormat_Float32x4
                a2.Offset <- 24uL
                a2.ShaderLocation <- 2u
                a2
            |]

        let iAttrs =
            [|
                let mutable i0 = WGPUVertexAttribute () in
                i0.Format <- WGPUVertexFormat_Float32x4
                i0.Offset <- 0uL
                i0.ShaderLocation <- 3u
                i0
                let mutable i1 = WGPUVertexAttribute () in
                i1.Format <- WGPUVertexFormat_Float32x4
                i1.Offset <- 16uL
                i1.ShaderLocation <- 4u
                i1
                let mutable i2 = WGPUVertexAttribute () in
                i2.Format <- WGPUVertexFormat_Float32x4
                i2.Offset <- 32uL
                i2.ShaderLocation <- 5u
                i2
                let mutable i3 = WGPUVertexAttribute () in
                i3.Format <- WGPUVertexFormat_Float32x4
                i3.Offset <- 48uL
                i3.ShaderLocation <- 6u
                i3
                let mutable i4 = WGPUVertexAttribute () in
                i4.Format <- WGPUVertexFormat_Float32x4
                i4.Offset <- 64uL
                i4.ShaderLocation <- 7u
                i4
                let mutable i5 = WGPUVertexAttribute () in
                i5.Format <- WGPUVertexFormat_Uint32x2
                i5.Offset <- 80uL
                i5.ShaderLocation <- 8u
                i5
                let mutable i6 = WGPUVertexAttribute () in
                i6.Format <- WGPUVertexFormat_Float32x2
                i6.Offset <- 88uL
                i6.ShaderLocation <- 9u
                i6
            |]

        use pV = fixed vAttrs
        use pI = fixed iAttrs

        let vBufLayouts =
            [|
                let mutable b0 = WGPUVertexBufferLayout ()
                b0.StepMode <- WGPUVertexStepMode_Vertex
                b0.ArrayStride <- uint64 sizeof<Vertex3D>
                b0.AttributeCount <- unativeint vAttrs.Length
                b0.Attributes <- NativePtr.toNativeInt pV
                b0

                let mutable b1 = WGPUVertexBufferLayout ()
                b1.StepMode <- WGPUVertexStepMode_Instance
                b1.ArrayStride <- uint64 sizeof<Instance3D>
                b1.AttributeCount <- unativeint iAttrs.Length
                b1.Attributes <- NativePtr.toNativeInt pI
                b1
            |]

        use pLayouts = fixed vBufLayouts

        withUtf8View "vs_main" (fun vsView ->
            withUtf8View fsEntry (fun fsView ->
                // Premultiplied alpha blending: rgb_out = src_rgb + dst_rgb * (1 - src_a)
                let mutable blend = WGPUBlendState ()
                blend.Color.Operation <- WGPUBlendOperation_Add
                blend.Color.SrcFactor <- WGPUBlendFactor_One
                blend.Color.DstFactor <- WGPUBlendFactor_OneMinusSrcAlpha
                blend.Alpha.Operation <- WGPUBlendOperation_Add
                blend.Alpha.SrcFactor <- WGPUBlendFactor_One
                blend.Alpha.DstFactor <- WGPUBlendFactor_OneMinusSrcAlpha

                let mutable target = WGPUColorTargetState ()
                target.Format <- colorFormat
                target.Blend <- if alphaBlend then addrOf &blend else 0n
                target.WriteMask <- WGPUColorWriteMask_All

                let mutable frag = WGPUFragmentState ()
                frag.Module <- sceneModule
                frag.EntryPoint <- fsView
                frag.TargetCount <- 1un
                frag.Targets <- addrOf &target

                let mutable ds = WGPUDepthStencilState ()
                ds.Format <- WGPUTextureFormat_Depth32Float

                ds.DepthWriteEnabled <-
                    if depthWrite then
                        WGPUOptionalBool_True
                    else
                        WGPUOptionalBool_False

                ds.DepthCompare <- WGPUCompareFunction_Less
                ds.StencilFront.Compare <- WGPUCompareFunction_Always
                ds.StencilFront.FailOp <- WGPUStencilOperation_Keep
                ds.StencilFront.DepthFailOp <- WGPUStencilOperation_Keep
                ds.StencilFront.PassOp <- WGPUStencilOperation_Keep
                ds.StencilBack <- ds.StencilFront

                let mutable rp = WGPURenderPipelineDescriptor ()
                rp.Label <- WGPUStringView.Null
                rp.Layout <- pipelineLayout
                rp.Vertex.Module <- sceneModule
                rp.Vertex.EntryPoint <- vsView
                rp.Vertex.BufferCount <- 2un
                rp.Vertex.Buffers <- NativePtr.toNativeInt pLayouts
                rp.Primitive.Topology <- WGPUPrimitiveTopology_TriangleList
                rp.Primitive.FrontFace <- WGPUFrontFace_CCW
                rp.Primitive.CullMode <- if cullBack then WGPUCullMode_Back else WGPUCullMode_None
                rp.DepthStencil <- addrOf &ds
                rp.Multisample.Count <- sampleCount
                rp.Multisample.Mask <- 0xFFFFFFFFu
                rp.Fragment <- addrOf &frag

                let p = wgpuDeviceCreateRenderPipeline (device, addrOf &rp)

                if p = 0n then
                    failwith $"wgpuDeviceCreateRenderPipeline ({fsEntry}) failed."

                p))

    // Pipeline creation is validated in one error scope: a shader that compiles can still fail to fit a
    // pipeline (an entry point without its stage attribute, a vertex input of the wrong type, a binding
    // larger than the buffer behind it).
    do wgpuDevicePushErrorScope (device, WGPUErrorFilter_Validation)

    let opaqueCullPipeline =
        buildPipeline shaderModule "fs_main" colorTargetFormat 4u true false true

    let opaqueDoublePipeline =
        buildPipeline shaderModule "fs_main" colorTargetFormat 4u false false true

    let blendCullPipeline =
        buildPipeline shaderModule "fs_main" colorTargetFormat 4u true true false

    let blendDoublePipeline =
        buildPipeline shaderModule "fs_main" colorTargetFormat 4u false true false

    let pickCullPipeline =
        buildPipeline pickModule "fs_pick" WGPUTextureFormat_RGBA32Uint 1u true false true

    let pickDoublePipeline =
        buildPipeline pickModule "fs_pick" WGPUTextureFormat_RGBA32Uint 1u false false true

    let buildLinePipeline (sceneModule: nativeint) =
        let lAttrs =
            [|
                let mutable a0 = WGPUVertexAttribute () in
                a0.Format <- WGPUVertexFormat_Float32x4
                a0.Offset <- 0uL
                a0.ShaderLocation <- 0u
                a0
                let mutable a1 = WGPUVertexAttribute () in
                a1.Format <- WGPUVertexFormat_Float32x4
                a1.Offset <- 16uL
                a1.ShaderLocation <- 1u
                a1
            |]

        use pL = fixed lAttrs
        let mutable vLayout = WGPUVertexBufferLayout ()
        vLayout.StepMode <- WGPUVertexStepMode_Vertex
        vLayout.ArrayStride <- uint64 sizeof<LineVertex3D>
        vLayout.AttributeCount <- unativeint lAttrs.Length
        vLayout.Attributes <- NativePtr.toNativeInt pL

        withUtf8View "vs_line" (fun vsView ->
            withUtf8View "fs_line" (fun fsView ->
                let mutable blend = WGPUBlendState ()
                blend.Color.Operation <- WGPUBlendOperation_Add
                blend.Color.SrcFactor <- WGPUBlendFactor_One
                blend.Color.DstFactor <- WGPUBlendFactor_OneMinusSrcAlpha
                blend.Alpha.Operation <- WGPUBlendOperation_Add
                blend.Alpha.SrcFactor <- WGPUBlendFactor_One
                blend.Alpha.DstFactor <- WGPUBlendFactor_OneMinusSrcAlpha

                let mutable target = WGPUColorTargetState ()
                target.Format <- colorTargetFormat
                target.Blend <- addrOf &blend
                target.WriteMask <- WGPUColorWriteMask_All

                let mutable frag = WGPUFragmentState ()
                frag.Module <- sceneModule
                frag.EntryPoint <- fsView
                frag.TargetCount <- 1un
                frag.Targets <- addrOf &target

                let mutable ds = WGPUDepthStencilState ()
                ds.Format <- WGPUTextureFormat_Depth32Float
                ds.DepthWriteEnabled <- WGPUOptionalBool_False
                ds.DepthCompare <- WGPUCompareFunction_LessEqual
                ds.StencilFront.Compare <- WGPUCompareFunction_Always
                ds.StencilFront.FailOp <- WGPUStencilOperation_Keep
                ds.StencilFront.DepthFailOp <- WGPUStencilOperation_Keep
                ds.StencilFront.PassOp <- WGPUStencilOperation_Keep
                ds.StencilBack <- ds.StencilFront

                let mutable rp = WGPURenderPipelineDescriptor ()
                rp.Label <- WGPUStringView.Null
                rp.Layout <- pipelineLayout
                rp.Vertex.Module <- sceneModule
                rp.Vertex.EntryPoint <- vsView
                rp.Vertex.BufferCount <- 1un
                rp.Vertex.Buffers <- addrOf &vLayout
                rp.Primitive.Topology <- WGPUPrimitiveTopology_LineList
                rp.Primitive.FrontFace <- WGPUFrontFace_CCW
                rp.Primitive.CullMode <- WGPUCullMode_None
                rp.DepthStencil <- addrOf &ds
                rp.Multisample.Count <- 4u
                rp.Multisample.Mask <- 0xFFFFFFFFu
                rp.Fragment <- addrOf &frag

                let p = wgpuDeviceCreateRenderPipeline (device, addrOf &rp)

                if p = 0n then
                    failwith "wgpuDeviceCreateRenderPipeline (linePipeline) failed."

                p))

    let buildDiscPipeline
        (sceneModule: nativeint)
        (fsEntry: string)
        (colorFormat: uint32)
        (sampleCount: uint32)
        (alphaBlend: bool)
        (depthWrite: bool)
        =
        let dAttrs =
            [|
                let mutable d0 = WGPUVertexAttribute () in
                d0.Format <- WGPUVertexFormat_Float32x4
                d0.Offset <- 0uL
                d0.ShaderLocation <- 0u
                d0
                let mutable d1 = WGPUVertexAttribute () in
                d1.Format <- WGPUVertexFormat_Float32x4
                d1.Offset <- 16uL
                d1.ShaderLocation <- 1u
                d1
                let mutable d2 = WGPUVertexAttribute () in
                d2.Format <- WGPUVertexFormat_Float32x4
                d2.Offset <- 32uL
                d2.ShaderLocation <- 2u
                d2
                let mutable d3 = WGPUVertexAttribute () in
                d3.Format <- WGPUVertexFormat_Uint32x2
                d3.Offset <- 48uL
                d3.ShaderLocation <- 3u
                d3
                let mutable d4 = WGPUVertexAttribute () in
                d4.Format <- WGPUVertexFormat_Float32x2
                d4.Offset <- 56uL
                d4.ShaderLocation <- 4u
                d4
            |]

        use pD = fixed dAttrs
        let mutable instLayout = WGPUVertexBufferLayout ()
        instLayout.StepMode <- WGPUVertexStepMode_Instance
        instLayout.ArrayStride <- uint64 sizeof<DiscInstance3D>
        instLayout.AttributeCount <- unativeint dAttrs.Length
        instLayout.Attributes <- NativePtr.toNativeInt pD

        withUtf8View "vs_disc" (fun vsView ->
            withUtf8View fsEntry (fun fsView ->
                let mutable blend = WGPUBlendState ()
                blend.Color.Operation <- WGPUBlendOperation_Add
                blend.Color.SrcFactor <- WGPUBlendFactor_One
                blend.Color.DstFactor <- WGPUBlendFactor_OneMinusSrcAlpha
                blend.Alpha.Operation <- WGPUBlendOperation_Add
                blend.Alpha.SrcFactor <- WGPUBlendFactor_One
                blend.Alpha.DstFactor <- WGPUBlendFactor_OneMinusSrcAlpha

                let mutable target = WGPUColorTargetState ()
                target.Format <- colorFormat
                target.Blend <- if alphaBlend then addrOf &blend else 0n
                target.WriteMask <- WGPUColorWriteMask_All

                let mutable frag = WGPUFragmentState ()
                frag.Module <- sceneModule
                frag.EntryPoint <- fsView
                frag.TargetCount <- 1un
                frag.Targets <- addrOf &target

                let mutable ds = WGPUDepthStencilState ()
                ds.Format <- WGPUTextureFormat_Depth32Float

                ds.DepthWriteEnabled <-
                    if depthWrite then
                        WGPUOptionalBool_True
                    else
                        WGPUOptionalBool_False

                ds.DepthCompare <-
                    if depthWrite then
                        WGPUCompareFunction_Less
                    else
                        WGPUCompareFunction_LessEqual

                ds.StencilFront.Compare <- WGPUCompareFunction_Always
                ds.StencilFront.FailOp <- WGPUStencilOperation_Keep
                ds.StencilFront.DepthFailOp <- WGPUStencilOperation_Keep
                ds.StencilFront.PassOp <- WGPUStencilOperation_Keep
                ds.StencilBack <- ds.StencilFront

                let mutable rp = WGPURenderPipelineDescriptor ()
                rp.Label <- WGPUStringView.Null
                rp.Layout <- pipelineLayout
                rp.Vertex.Module <- sceneModule
                rp.Vertex.EntryPoint <- vsView
                rp.Vertex.BufferCount <- 1un
                rp.Vertex.Buffers <- addrOf &instLayout
                rp.Primitive.Topology <- WGPUPrimitiveTopology_TriangleList
                rp.Primitive.FrontFace <- WGPUFrontFace_CCW
                rp.Primitive.CullMode <- WGPUCullMode_None
                rp.DepthStencil <- addrOf &ds
                rp.Multisample.Count <- sampleCount
                rp.Multisample.Mask <- 0xFFFFFFFFu
                rp.Fragment <- addrOf &frag

                let p = wgpuDeviceCreateRenderPipeline (device, addrOf &rp)

                if p = 0n then
                    failwith $"wgpuDeviceCreateRenderPipeline ({fsEntry}) failed."

                p))

    let linePipeline = buildLinePipeline lineModule

    let discPipeline =
        buildDiscPipeline discModule "fs_disc" colorTargetFormat 4u true false

    let discPickPipeline =
        buildDiscPipeline discPickModule "fs_disc_pick" WGPUTextureFormat_RGBA32Uint 1u false true

    let releasePipelineState () =
        for pipeline in
            [|
                discPickPipeline
                discPipeline
                linePipeline
                opaqueCullPipeline
                opaqueDoublePipeline
                blendCullPipeline
                blendDoublePipeline
                pickCullPipeline
                pickDoublePipeline
            |] do
            if pipeline <> 0n then
                wgpuRenderPipelineRelease pipeline

        if pipelineLayout <> 0n then
            wgpuPipelineLayoutRelease pipelineLayout

        if bindGroup <> 0n then
            wgpuBindGroupRelease bindGroup

        if bindGroupLayout <> 0n then
            wgpuBindGroupLayoutRelease bindGroupLayout

        for buffer in [| uniformBuffer; userUniformBuffer |] do
            if buffer <> 0n then
                wgpuBufferDestroy buffer
                wgpuBufferRelease buffer

        for shader in [| shaderModule; stockModule |] do
            if shader <> 0n then
                wgpuShaderModuleRelease shader

    do
        match popValidationError () with
        | ValueSome message ->
            releasePipelineState ()
            releaseDevice ()
            raise (WgslException $"WGSL does not fit the scene pipelines. {message}")
        | ValueNone -> ()

    // Fixed 256-byte staging buffer for 1x1 GPU pixel picking
    let pickReadbackBuffer =
        let mutable desc = WGPUBufferDescriptor ()

        desc.Usage <-
            WGPUBufferUsage_MapRead
            ||| WGPUBufferUsage_CopyDst

        desc.Size <- 256uL
        let b = wgpuDeviceCreateBuffer (device, addrOf &desc)

        if b = 0n then
            failwith "wgpuDeviceCreateBuffer (pickReadbackBuffer) failed."

        b

    // Auto-resized framebuffer targets; CPU readback buffer & byte[] are allocated lazily on first CPU readback.
    let mutable msaaColorTex = 0n
    let mutable msaaColorView = 0n
    let mutable resolveColorTex = 0n
    let mutable resolveColorView = 0n
    let mutable msaaDepthTex = 0n
    let mutable msaaDepthView = 0n
    let mutable pickColorTex = 0n
    let mutable pickColorView = 0n
    let mutable pickDepthTex = 0n
    let mutable pickDepthView = 0n
    let mutable readbackBuffer = 0n
    let mutable paddedBytesPerRow = 0u
    let mutable rgbaPixels = Array.empty<byte>
    let mutable renderCalls = 0
    let mutable framesDelivered = 0

    let destroyTargets () =
        let relView v =
            if v <> 0n then
                wgpuTextureViewRelease v
                0n
            else
                0n

        let relTex t =
            if t <> 0n then
                wgpuTextureDestroy t
                wgpuTextureRelease t
                0n
            else
                0n

        msaaColorView <- relView msaaColorView
        msaaColorTex <- relTex msaaColorTex
        resolveColorView <- relView resolveColorView
        resolveColorTex <- relTex resolveColorTex
        msaaDepthView <- relView msaaDepthView
        msaaDepthTex <- relTex msaaDepthTex
        pickColorView <- relView pickColorView
        pickColorTex <- relTex pickColorTex
        pickDepthView <- relView pickDepthView
        pickDepthTex <- relTex pickDepthTex

        if readbackBuffer <> 0n then
            wgpuBufferDestroy readbackBuffer
            wgpuBufferRelease readbackBuffer
            readbackBuffer <- 0n

        rgbaPixels <- Array.empty<byte>

    let createTex2D (w: int) (h: int) (format: uint32) (samples: uint32) (usage: uint64) =
        let mutable desc = WGPUTextureDescriptor ()
        desc.Label <- WGPUStringView.Null
        desc.Usage <- usage
        desc.Dimension <- WGPUTextureDimension_2D
        desc.Size.Width <- uint32 w
        desc.Size.Height <- uint32 h
        desc.Size.DepthOrArrayLayers <- 1u
        desc.Format <- format
        desc.MipLevelCount <- 1u
        desc.SampleCount <- samples
        let tex = wgpuDeviceCreateTexture (device, addrOf &desc)

        if tex = 0n then
            failwith $"wgpuDeviceCreateTexture ({w}x{h}, fmt={format}) failed."

        let view = wgpuTextureCreateView (tex, 0n)

        if view = 0n then
            failwith "wgpuTextureCreateView failed."

        tex, view

    let allocateTargets (w: int) (h: int) =
        destroyTargets ()
        curWidth <- max 1 w
        curHeight <- max 1 h

        let t1, v1 =
            createTex2D curWidth curHeight colorTargetFormat 4u WGPUTextureUsage_RenderAttachment

        msaaColorTex <- t1
        msaaColorView <- v1

        let t2, v2 =
            createTex2D
                curWidth
                curHeight
                colorTargetFormat
                1u
                (WGPUTextureUsage_RenderAttachment
                 ||| WGPUTextureUsage_CopySrc)

        resolveColorTex <- t2
        resolveColorView <- v2

        let t3, v3 =
            createTex2D curWidth curHeight WGPUTextureFormat_Depth32Float 4u WGPUTextureUsage_RenderAttachment

        msaaDepthTex <- t3
        msaaDepthView <- v3

        let t4, v4 =
            createTex2D
                curWidth
                curHeight
                WGPUTextureFormat_RGBA32Uint
                1u
                (WGPUTextureUsage_RenderAttachment
                 ||| WGPUTextureUsage_CopySrc)

        pickColorTex <- t4
        pickColorView <- v4

        let t5, v5 =
            createTex2D curWidth curHeight WGPUTextureFormat_Depth32Float 1u WGPUTextureUsage_RenderAttachment

        pickDepthTex <- t5
        pickDepthView <- v5
        paddedBytesPerRow <- uint32 (((curWidth * 4) + 255) &&& ~~~255)

    let ensureReadbackBuffer () =
        if readbackBuffer = 0n then
            let bufBytes = uint64 paddedBytesPerRow * uint64 curHeight
            let mutable bDesc = WGPUBufferDescriptor ()

            bDesc.Usage <-
                WGPUBufferUsage_MapRead
                ||| WGPUBufferUsage_CopyDst

            bDesc.Size <- bufBytes
            let created = wgpuDeviceCreateBuffer (device, addrOf &bDesc)

            if created = 0n then
                failwith "wgpuDeviceCreateBuffer (readbackBuffer) failed."

            readbackBuffer <- created

        readbackBuffer

    let ensureRgbaPixels () =
        let needed = curWidth * curHeight * 4

        if rgbaPixels.Length <> needed then
            rgbaPixels <- Array.zeroCreate<byte> needed

        rgbaPixels

    do allocateTargets curWidth curHeight

    let writeSceneUniforms (camera: Camera3D) (lighting: LightingRig) (sectionPlane: SectionPlane) =
        let mutable u = GpuSceneUniforms ()
        u.ViewProj <- Camera3D.viewProjMatrix curWidth curHeight camera
        u.CamPos <- Vector4 (camera.Position, 0.0f)
        u.ClipPlane <- sectionPlane.AsUniformVector4 ()
        u.SkyColor <- Vector4 (lighting.SkyColor, float32 curWidth)
        u.GroundColor <- Vector4 (lighting.GroundColor, float32 curHeight)
        u.KeyLight <- Vector4 (Vector3.Normalize lighting.KeyLightDir, lighting.KeyLightIntensity)
        u.RimLight <- Vector4 (Vector3.Normalize lighting.RimLightDir, lighting.RimLightIntensity)
        u.SectionStyle <- Vector4 (lighting.SectionContourColor, lighting.SectionContourWidth)
        wgpuQueueWriteBuffer (queue, uniformBuffer, 0uL, addrOf &u, unativeint sizeof<GpuSceneUniforms>)

    let submitScenePass (scene: ScenePass) (resolveTargetView: nativeint) : struct (int64 * int64 * int * int * int) =
        renderCalls <- renderCalls + 1

        let clear =
            match scene.ClearColor with
            | ValueSome c -> c
            | ValueNone -> RgbaColor.darkBackdrop

        let rig =
            match scene.Lighting with
            | ValueSome l -> l
            | ValueNone -> LightingRig.studioDefault

        let clip =
            match scene.SectionPlane with
            | ValueSome s -> s
            | ValueNone -> SectionPlane.disabled

        let t0 = Stopwatch.GetTimestamp ()
        writeSceneUniforms scene.Camera rig clip

        let enc = wgpuDeviceCreateCommandEncoder (device, 0n)
        let clearAlpha = float (Math.Clamp (clear.A, 0.0f, 1.0f))
        let mutable colorAtt = WGPURenderPassColorAttachment ()
        colorAtt.View <- msaaColorView
        colorAtt.DepthSlice <- WGPU_DEPTH_SLICE_UNDEFINED
        colorAtt.ResolveTarget <- resolveTargetView
        colorAtt.LoadOp <- WGPULoadOp_Clear
        colorAtt.StoreOp <- WGPUStoreOp_Store
        colorAtt.ClearValue.R <- float clear.R * clearAlpha
        colorAtt.ClearValue.G <- float clear.G * clearAlpha
        colorAtt.ClearValue.B <- float clear.B * clearAlpha
        colorAtt.ClearValue.A <- clearAlpha

        let mutable depthAtt = WGPURenderPassDepthStencilAttachment ()
        depthAtt.View <- msaaDepthView
        depthAtt.DepthLoadOp <- WGPULoadOp_Clear
        depthAtt.DepthStoreOp <- WGPUStoreOp_Discard
        depthAtt.DepthClearValue <- 1.0f

        let mutable rpDesc = WGPURenderPassDescriptor ()
        rpDesc.Label <- WGPUStringView.Null
        rpDesc.ColorAttachmentCount <- 1un
        rpDesc.ColorAttachments <- addrOf &colorAtt
        rpDesc.DepthStencilAttachment <- addrOf &depthAtt

        let pass = wgpuCommandEncoderBeginRenderPass (enc, addrOf &rpDesc)
        wgpuRenderPassEncoderSetBindGroup (pass, 0u, bindGroup, 0un, 0n)

        let mutable drawCalls = 0
        let mutable totalInstances = 0
        let mutable totalTris = 0
        let mutable lastPipeline = 0n

        let batchSpan = scene.Batches.Span
        let lineSpan = scene.LineBatches.Span
        let discSpan = scene.DiscBatches.Span

        for i = 0 to batchSpan.Length - 1 do
            let b = batchSpan[i]

            if
                b.BlendMode = BlendMode.Opaque
                && b.Count > 0
                && b.Mesh.IndexCount > 0u
            then
                let pipe =
                    if b.DoubleSided then
                        opaqueDoublePipeline
                    else
                        opaqueCullPipeline

                if pipe <> lastPipeline then
                    wgpuRenderPassEncoderSetPipeline (pass, pipe)
                    lastPipeline <- pipe

                wgpuRenderPassEncoderSetVertexBuffer (pass, 0u, b.Mesh.VertexBuffer, 0uL, b.Mesh.VertexBytes)
                wgpuRenderPassEncoderSetVertexBuffer (pass, 1u, b.InstanceBuffer, 0uL, b.ActiveBytes)
                wgpuRenderPassEncoderSetIndexBuffer (pass, b.Mesh.IndexBuffer, WGPUIndexFormat_Uint32, 0uL, b.Mesh.IndexBytes)
                wgpuRenderPassEncoderDrawIndexed (pass, b.Mesh.IndexCount, uint32 b.Count, 0u, 0, 0u)
                drawCalls <- drawCalls + 1
                totalInstances <- totalInstances + b.Count
                totalTris <- totalTris + (b.Mesh.TriangleCount * b.Count)

        if linePipeline <> 0n && lineSpan.Length > 0 then
            for i = 0 to lineSpan.Length - 1 do
                let lb = lineSpan[i]

                if lb.VertexCount >= 2 then
                    if lastPipeline <> linePipeline then
                        wgpuRenderPassEncoderSetPipeline (pass, linePipeline)
                        lastPipeline <- linePipeline

                    wgpuRenderPassEncoderSetVertexBuffer (pass, 0u, lb.VertexBuffer, 0uL, lb.ActiveBytes)
                    wgpuRenderPassEncoderDraw (pass, uint32 lb.VertexCount, 1u, 0u, 0u)
                    drawCalls <- drawCalls + 1

        for i = 0 to batchSpan.Length - 1 do
            let b = batchSpan[i]

            if
                b.BlendMode = BlendMode.AlphaBlend
                && b.Count > 0
                && b.Mesh.IndexCount > 0u
            then
                let pipe =
                    if b.DoubleSided then
                        blendDoublePipeline
                    else
                        blendCullPipeline

                if pipe <> lastPipeline then
                    wgpuRenderPassEncoderSetPipeline (pass, pipe)
                    lastPipeline <- pipe

                wgpuRenderPassEncoderSetVertexBuffer (pass, 0u, b.Mesh.VertexBuffer, 0uL, b.Mesh.VertexBytes)
                wgpuRenderPassEncoderSetVertexBuffer (pass, 1u, b.InstanceBuffer, 0uL, b.ActiveBytes)
                wgpuRenderPassEncoderSetIndexBuffer (pass, b.Mesh.IndexBuffer, WGPUIndexFormat_Uint32, 0uL, b.Mesh.IndexBytes)
                wgpuRenderPassEncoderDrawIndexed (pass, b.Mesh.IndexCount, uint32 b.Count, 0u, 0, 0u)
                drawCalls <- drawCalls + 1
                totalInstances <- totalInstances + b.Count
                totalTris <- totalTris + (b.Mesh.TriangleCount * b.Count)

        if discPipeline <> 0n && discSpan.Length > 0 then
            for i = 0 to discSpan.Length - 1 do
                let db = discSpan[i]

                if db.Count > 0 then
                    if lastPipeline <> discPipeline then
                        wgpuRenderPassEncoderSetPipeline (pass, discPipeline)
                        lastPipeline <- discPipeline

                    wgpuRenderPassEncoderSetVertexBuffer (pass, 0u, db.InstanceBuffer, 0uL, db.ActiveBytes)
                    wgpuRenderPassEncoderDraw (pass, 6u, uint32 db.Count, 0u, 0u)
                    drawCalls <- drawCalls + 1
                    totalInstances <- totalInstances + db.Count
                    totalTris <- totalTris + (db.Count * 2)

        wgpuRenderPassEncoderEnd (pass)
        wgpuRenderPassEncoderRelease (pass)

        let mutable cmd = wgpuCommandEncoderFinish (enc, 0n)
        wgpuCommandEncoderRelease (enc)
        let mutable subIdx = wgpuQueueSubmitForIndex (queue, 1un, addrOf &cmd)
        wgpuCommandBufferRelease (cmd)
        let _ = wgpuDevicePoll (device, WGPU_TRUE, addrOf &subIdx)
        let t1 = Stopwatch.GetTimestamp ()
        struct (t0, t1, drawCalls, totalInstances, totalTris)

    let stageAndCopyResolveToSpan (destination: Span<byte>) =
        let requiredBytes = curWidth * curHeight * 4

        if destination.Length < requiredBytes then
            invalidArg (nameof destination) $"Destination span length {destination.Length} is smaller than required {requiredBytes} bytes."

        let rb = ensureReadbackBuffer ()
        let copyEnc = wgpuDeviceCreateCommandEncoder (device, 0n)
        let mutable srcTex = WGPUTexelCopyTextureInfo ()
        srcTex.Texture <- resolveColorTex
        srcTex.MipLevel <- 0u
        srcTex.Aspect <- WGPUTextureAspect_All

        let mutable dstBuf = WGPUTexelCopyBufferInfo ()
        dstBuf.Buffer <- rb
        dstBuf.Layout.Offset <- 0uL
        dstBuf.Layout.BytesPerRow <- paddedBytesPerRow
        dstBuf.Layout.RowsPerImage <- uint32 curHeight

        let mutable extent = WGPUExtent3D ()
        extent.Width <- uint32 curWidth
        extent.Height <- uint32 curHeight
        extent.DepthOrArrayLayers <- 1u

        wgpuCommandEncoderCopyTextureToBuffer (copyEnc, addrOf &srcTex, addrOf &dstBuf, addrOf &extent)
        let mutable copyCmd = wgpuCommandEncoderFinish (copyEnc, 0n)
        wgpuCommandEncoderRelease (copyEnc)
        wgpuQueueSubmit (queue, 1un, addrOf &copyCmd)
        wgpuCommandBufferRelease (copyCmd)

        let totalMappedSize = unativeint (uint64 paddedBytesPerRow * uint64 curHeight)

        lock callbackLock (fun () ->
            WgpuCallbacks.lastMapDone <- false
            WgpuCallbacks.lastMapStatus <- 0u
            let mutable mapCb = WGPUBufferMapCallbackInfo ()
            mapCb.Mode <- WGPUCallbackMode_AllowSpontaneous
            mapCb.Callback <- WgpuCallbacks.mapPtr
            let _ = wgpuBufferMapAsync (rb, WGPUMapMode_Read, 0un, totalMappedSize, mapCb)
            let _ = wgpuDevicePoll (device, WGPU_TRUE, 0n)

            if
                not WgpuCallbacks.lastMapDone
                || WgpuCallbacks.lastMapStatus
                   <> WGPUMapAsyncStatus_Success
            then
                failwith $"wgpuBufferMapAsync (Render) failed with status {WgpuCallbacks.lastMapStatus}.")

        let mappedPtr = wgpuBufferGetConstMappedRange (rb, 0un, totalMappedSize)

        try
            let unpaddedRow = curWidth * 4
            let dst = destination.Slice (0, requiredBytes)

            if int paddedBytesPerRow = unpaddedRow then
                ReadOnlySpan<byte>(mappedPtr.ToPointer (), requiredBytes).CopyTo(dst)
            else
                let stride = int paddedBytesPerRow

                for y = 0 to curHeight - 1 do
                    let srcRow =
                        ReadOnlySpan<byte>((mappedPtr + nativeint (y * stride)).ToPointer(), unpaddedRow)

                    srcRow.CopyTo (dst.Slice (y * unpaddedRow, unpaddedRow))

            let isBgra = colorTargetFormat = WGPUTextureFormat_BGRA8Unorm
            let mutable i = 0

            while i < requiredBytes do
                let mutable r = dst[i]
                let mutable g = dst[i + 1]
                let mutable b = dst[i + 2]
                let a = dst[i + 3]

                if isBgra then
                    let tmp = r
                    r <- b
                    b <- tmp

                if a > 0uy && a < 255uy then
                    let ai = int a
                    let half = ai >>> 1
                    r <- byte (min 255 ((int r * 255 + half) / ai))
                    g <- byte (min 255 ((int g * 255 + half) / ai))
                    b <- byte (min 255 ((int b * 255 + half) / ai))

                if isBgra || (a > 0uy && a < 255uy) then
                    dst[i] <- r
                    dst[i + 1] <- g
                    dst[i + 2] <- b

                i <- i + 4
        finally
            wgpuBufferUnmap (rb)

    let buildLegacyScenePass
        (camera: Camera3D)
        (batches: seq<InstanceBatch>)
        (clearColor: RgbaColor option)
        (lighting: LightingRig option)
        (sectionPlane: SectionPlane option)
        (lineBatches: seq<LineBatch> option)
        (discBatches: seq<DiscBatch> option)
        : ScenePass =
        let batchMem =
            match batches with
            | :? (InstanceBatch array) as arr -> ReadOnlyMemory<InstanceBatch>(arr)
            | _ -> ReadOnlyMemory<InstanceBatch>(Seq.toArray batches)

        let lineMem =
            match lineBatches with
            | None -> ReadOnlyMemory.Empty
            | Some (:? (LineBatch array) as arr) -> ReadOnlyMemory<LineBatch>(arr)
            | Some lbs -> ReadOnlyMemory<LineBatch>(Seq.toArray lbs)

        let discMem =
            match discBatches with
            | None -> ReadOnlyMemory.Empty
            | Some (:? (DiscBatch array) as arr) -> ReadOnlyMemory<DiscBatch>(arr)
            | Some dbs -> ReadOnlyMemory<DiscBatch>(Seq.toArray dbs)

        {
            Camera = camera
            ClearColor =
                match clearColor with
                | Some c -> ValueSome c
                | None -> ValueNone
            Lighting =
                match lighting with
                | Some l -> ValueSome l
                | None -> ValueNone
            SectionPlane =
                match sectionPlane with
                | Some s -> ValueSome s
                | None -> ValueNone
            Batches = batchMem
            LineBatches = lineMem
            DiscBatches = discMem
        }

    /// <summary>Human-readable GPU adapter name reported by WebGPU.</summary>
    member _.AdapterName = adapterDeviceName

    /// <summary>Active native WebGPU backend name (`D3D12`, `Vulkan`, `Metal`, `OpenGL`).</summary>
    member _.BackendName = adapterBackendName

    /// <summary>Current offscreen target width in device pixels.</summary>
    member _.Width = curWidth

    /// <summary>Current offscreen target height in device pixels.</summary>
    member _.Height = curHeight

    /// <summary>CPU straight-RGBA8 buffer populated by `Render` (lazily allocated on first CPU access).</summary>
    member _.RgbaPixels = ensureRgbaPixels ()

    /// <summary>Total scene render passes submitted on this context.</summary>
    member _.RenderCalls = renderCalls

    /// <summary>Total frames completed on this context.</summary>
    member _.FramesDelivered = framesDelivered

    /// <summary>True after `Dispose` has released native WebGPU handles.</summary>
    member _.IsDisposed = disposed

    member internal _.InstanceHandle = instance
    member internal _.AdapterHandle = adapter
    member internal _.DeviceHandle = device
    member internal _.QueueHandle = queue
    member internal _.BackendType = adapterBackendType
    member internal _.ColorTargetFormat = colorTargetFormat
    member internal _.ResolveTextureHandle = resolveColorTex
    member internal _.ResolveViewHandle = resolveColorView

    /// <summary>Resizes the offscreen 4x-MSAA, resolve, and GPU-picking targets when dimensions change.</summary>
    member _.Resize(width: int, height: int) =
        let w = max 1 width
        let h = max 1 height

        if w <> curWidth || h <> curHeight then
            allocateTargets w h

    /// <summary>Uploads CPU `MeshData` into immutable GPU vertex and index buffers.</summary>
    member _.UploadMesh(mesh: MeshData) : MeshHandle =
        new MeshHandle (device, queue, mesh)

    /// <summary>Creates a dynamic hardware-instanced draw batch bound to `mesh`.</summary>
    member _.CreateBatch(mesh: MeshHandle, ?blendMode: BlendMode, ?doubleSided: bool) : InstanceBatch =
        new InstanceBatch (device, queue, mesh, defaultArg blendMode BlendMode.Opaque, defaultArg doubleSided false)

    /// <summary>Creates a dynamic 4x-MSAA 3D hairline batch.</summary>
    member _.CreateLineBatch() : LineBatch =
        new LineBatch (device, queue)

    /// <summary>Creates a dynamic camera-facing SDF disc and verification-arc billboard batch.</summary>
    member _.CreateDiscBatch() : DiscBatch =
        new DiscBatch (device, queue)

    /// <summary>
    /// Writes application data into the user uniform block, starting at slot 0. A shader reads it as
    /// <c>user.slots[i]</c> (<c>@group(0) @binding(1)</c>).
    /// </summary>
    /// <remarks>
    /// The block holds <c>Shaders.UserUniformSlots</c> values. Slots keep what was last written and start at
    /// zero. A write takes effect for every pass rendered after it.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">More values than the block has slots.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.SetUserUniforms(values: ReadOnlySpan<Vector4>) =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuContext))

        if values.Length > Shaders.UserUniformSlots then
            invalidArg (nameof values) $"The user uniform block holds {Shaders.UserUniformSlots} values, got {values.Length}."

        if values.Length > 0 then
            let byteLen = unativeint (values.Length * sizeof<Vector4>)
            use p = fixed &MemoryMarshal.GetReference (values)
            wgpuQueueWriteBuffer (queue, userUniformBuffer, 0uL, NativePtr.toNativeInt p, byteLen)

    /// <summary>Writes an array of application data into the user uniform block, starting at slot 0.</summary>
    /// <exception cref="T:System.ArgumentException">More values than the block has slots.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member this.SetUserUniforms(values: Vector4 array) =
        this.SetUserUniforms (ReadOnlySpan<Vector4>(values))

    /// <summary>Writes one slot of the user uniform block and leaves the others as they are.</summary>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The slot is outside the block.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The context is disposed.</exception>
    member _.SetUserUniform(slot: int, value: Vector4) =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuContext))

        if slot < 0 || slot >= Shaders.UserUniformSlots then
            raise (ArgumentOutOfRangeException (nameof slot, slot, $"The user uniform block has slots 0 to {Shaders.UserUniformSlots - 1}."))

        let mutable v = value
        wgpuQueueWriteBuffer (queue, userUniformBuffer, uint64 (slot * sizeof<Vector4>), addrOf &v, unativeint sizeof<Vector4>)

    /// <summary>
    /// Checks WGSL the way a context would use it, without opening a window: it compiles the text and builds
    /// every scene pipeline from it on an offscreen device.
    /// </summary>
    /// <returns>
    /// <c>Ok</c> when a context can be created with the WGSL; otherwise <c>Error</c> with the compiler's or the
    /// pipeline validator's message.
    /// </returns>
    /// <remarks>
    /// Meant for a test or a build step, so that a shader error fails there rather than when the app starts.
    /// It needs a GPU adapter or a software one, as any context does, and throws if none is available.
    /// </remarks>
    static member ValidateWgsl(wgsl: string, ?backend: WgpuBackendPreference) : Result<unit, string> =
        try
            (new WgpuContext (initialWidth = 1, initialHeight = 1, customWgsl = wgsl, ?backend = backend)).Dispose()
            Ok ()
        with :? WgslException as failure ->
            Error failure.Message

    /// <summary>Executes a zero-allocation `ScenePass` purely in GPU VRAM without staging or mapping a CPU readback buffer.</summary>
    member internal _.RenderGpuPass(pass: ScenePass, ?resolveViewOverride: nativeint) : FrameMetrics =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuContext))

        let targetView = defaultArg resolveViewOverride resolveColorView

        let struct (t0, t1, drawCalls, totalInstances, totalTris) =
            submitScenePass pass targetView

        framesDelivered <- framesDelivered + 1
        let freq = float Stopwatch.Frequency

        {
            GpuRenderMs = float (t1 - t0) * 1000.0 / freq
            ReadbackMs = 0.0
            DrawCalls = drawCalls
            InstanceCount = totalInstances
            TriangleCount = totalTris
        }

    /// <summary>Executes the 4x-MSAA scene pass purely in GPU VRAM without staging or mapping a CPU readback buffer.</summary>
    member internal this.RenderGpuOnly
        (
            camera: Camera3D,
            batches: seq<InstanceBatch>,
            ?clearColor: RgbaColor,
            ?lighting: LightingRig,
            ?sectionPlane: SectionPlane,
            ?lineBatches: seq<LineBatch>,
            ?discBatches: seq<DiscBatch>,
            ?resolveViewOverride: nativeint
        ) : FrameMetrics =
        let pass =
            buildLegacyScenePass camera batches clearColor lighting sectionPlane lineBatches discBatches

        match resolveViewOverride with
        | Some view -> this.RenderGpuPass (pass, resolveViewOverride = view)
        | None -> this.RenderGpuPass (pass)

    /// <summary>Renders `pass` and copies unpremultiplied RGBA8 pixels directly into caller-supplied `destination`.</summary>
    member _.RenderInto(pass: ScenePass, destination: Span<byte>) : FrameMetrics =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuContext))

        let struct (t0, t1, drawCalls, totalInstances, totalTris) =
            submitScenePass pass resolveColorView

        stageAndCopyResolveToSpan destination
        framesDelivered <- framesDelivered + 1
        let t2 = Stopwatch.GetTimestamp ()
        let freq = float Stopwatch.Frequency

        {
            GpuRenderMs = float (t1 - t0) * 1000.0 / freq
            ReadbackMs = float (t2 - t1) * 1000.0 / freq
            DrawCalls = drawCalls
            InstanceCount = totalInstances
            TriangleCount = totalTris
        }

    /// <summary>Renders a zero-allocation `ScenePass` and updates `RgbaPixels` on the CPU.</summary>
    member this.Render(pass: ScenePass) : FrameMetrics =
        let buf = ensureRgbaPixels ()
        this.RenderInto (pass, Span<byte>(buf))

    /// <summary>Renders `camera` and `batches` with optional scene settings and updates `RgbaPixels` on the CPU.</summary>
    member this.Render
        (
            camera: Camera3D,
            batches: seq<InstanceBatch>,
            ?clearColor: RgbaColor,
            ?lighting: LightingRig,
            ?sectionPlane: SectionPlane,
            ?lineBatches: seq<LineBatch>,
            ?discBatches: seq<DiscBatch>
        ) : FrameMetrics =
        let pass =
            buildLegacyScenePass camera batches clearColor lighting sectionPlane lineBatches discBatches

        this.Render (pass)

    /// <summary>
    /// Executes a 1x1-scissor hardware GPU picking pass at pixel `(px, py)` for `scene`,
    /// returning the hit `PickId` and exact 3D `WorldPosition` or `ValueNone`.
    /// </summary>
    member _.Pick(px: int, py: int, scene: ScenePass) : PickHit voption =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuContext))

        if
            px < 0
            || px >= curWidth
            || py < 0
            || py >= curHeight
        then
            ValueNone
        else
            let clip =
                match scene.SectionPlane with
                | ValueSome s -> s
                | ValueNone -> SectionPlane.disabled

            writeSceneUniforms scene.Camera LightingRig.studioDefault clip

            let enc = wgpuDeviceCreateCommandEncoder (device, 0n)
            let mutable colorAtt = WGPURenderPassColorAttachment ()
            colorAtt.View <- pickColorView
            colorAtt.DepthSlice <- WGPU_DEPTH_SLICE_UNDEFINED
            colorAtt.LoadOp <- WGPULoadOp_Clear
            colorAtt.StoreOp <- WGPUStoreOp_Store

            let mutable depthAtt = WGPURenderPassDepthStencilAttachment ()
            depthAtt.View <- pickDepthView
            depthAtt.DepthLoadOp <- WGPULoadOp_Clear
            depthAtt.DepthStoreOp <- WGPUStoreOp_Discard
            depthAtt.DepthClearValue <- 1.0f

            let mutable rpDesc = WGPURenderPassDescriptor ()
            rpDesc.Label <- WGPUStringView.Null
            rpDesc.ColorAttachmentCount <- 1un
            rpDesc.ColorAttachments <- addrOf &colorAtt
            rpDesc.DepthStencilAttachment <- addrOf &depthAtt

            let pass = wgpuCommandEncoderBeginRenderPass (enc, addrOf &rpDesc)
            wgpuRenderPassEncoderSetScissorRect (pass, uint32 px, uint32 py, 1u, 1u)
            wgpuRenderPassEncoderSetBindGroup (pass, 0u, bindGroup, 0un, 0n)

            let mutable lastPipe = 0n
            let batchSpan = scene.Batches.Span

            for i = 0 to batchSpan.Length - 1 do
                let b = batchSpan[i]

                if b.Count > 0 && b.Mesh.IndexCount > 0u then
                    let pipe =
                        if b.DoubleSided then
                            pickDoublePipeline
                        else
                            pickCullPipeline

                    if pipe <> lastPipe then
                        wgpuRenderPassEncoderSetPipeline (pass, pipe)
                        lastPipe <- pipe

                    wgpuRenderPassEncoderSetVertexBuffer (pass, 0u, b.Mesh.VertexBuffer, 0uL, b.Mesh.VertexBytes)
                    wgpuRenderPassEncoderSetVertexBuffer (pass, 1u, b.InstanceBuffer, 0uL, b.ActiveBytes)
                    wgpuRenderPassEncoderSetIndexBuffer (pass, b.Mesh.IndexBuffer, WGPUIndexFormat_Uint32, 0uL, b.Mesh.IndexBytes)
                    wgpuRenderPassEncoderDrawIndexed (pass, b.Mesh.IndexCount, uint32 b.Count, 0u, 0, 0u)

            let discSpan = scene.DiscBatches.Span

            if discPickPipeline <> 0n && discSpan.Length > 0 then
                for i = 0 to discSpan.Length - 1 do
                    let db = discSpan[i]

                    if db.Count > 0 then
                        if lastPipe <> discPickPipeline then
                            wgpuRenderPassEncoderSetPipeline (pass, discPickPipeline)
                            lastPipe <- discPickPipeline

                        wgpuRenderPassEncoderSetVertexBuffer (pass, 0u, db.InstanceBuffer, 0uL, db.ActiveBytes)
                        wgpuRenderPassEncoderDraw (pass, 6u, uint32 db.Count, 0u, 0u)

            wgpuRenderPassEncoderEnd (pass)
            wgpuRenderPassEncoderRelease (pass)

            let mutable srcTex = WGPUTexelCopyTextureInfo ()
            srcTex.Texture <- pickColorTex
            srcTex.MipLevel <- 0u
            srcTex.Origin.X <- uint32 px
            srcTex.Origin.Y <- uint32 py
            srcTex.Origin.Z <- 0u
            srcTex.Aspect <- WGPUTextureAspect_All

            let mutable dstBuf = WGPUTexelCopyBufferInfo ()
            dstBuf.Buffer <- pickReadbackBuffer
            dstBuf.Layout.Offset <- 0uL
            dstBuf.Layout.BytesPerRow <- 256u
            dstBuf.Layout.RowsPerImage <- 1u

            let mutable extent = WGPUExtent3D ()
            extent.Width <- 1u
            extent.Height <- 1u
            extent.DepthOrArrayLayers <- 1u

            wgpuCommandEncoderCopyTextureToBuffer (enc, addrOf &srcTex, addrOf &dstBuf, addrOf &extent)
            let mutable cmd = wgpuCommandEncoderFinish (enc, 0n)
            wgpuCommandEncoderRelease (enc)
            wgpuQueueSubmit (queue, 1un, addrOf &cmd)
            wgpuCommandBufferRelease (cmd)

            lock callbackLock (fun () ->
                WgpuCallbacks.lastMapDone <- false
                WgpuCallbacks.lastMapStatus <- 0u
                let mutable mapCb = WGPUBufferMapCallbackInfo ()
                mapCb.Mode <- WGPUCallbackMode_AllowSpontaneous
                mapCb.Callback <- WgpuCallbacks.mapPtr
                let _ = wgpuBufferMapAsync (pickReadbackBuffer, WGPUMapMode_Read, 0un, 16un, mapCb)
                let _ = wgpuDevicePoll (device, WGPU_TRUE, 0n)

                if
                    not WgpuCallbacks.lastMapDone
                    || WgpuCallbacks.lastMapStatus
                       <> WGPUMapAsyncStatus_Success
                then
                    failwith $"wgpuBufferMapAsync (Pick) failed with status {WgpuCallbacks.lastMapStatus}.")

            let ptr = wgpuBufferGetConstMappedRange (pickReadbackBuffer, 0un, 16un)

            try
                let uPtr = NativePtr.ofNativeInt<uint32> ptr
                let ux = NativePtr.get uPtr 0
                let uy = NativePtr.get uPtr 1
                let uz = NativePtr.get uPtr 2
                let pickId = NativePtr.get uPtr 3

                if pickId = 0u then
                    ValueNone
                else
                    let wx = BitConverter.UInt32BitsToSingle ux
                    let wy = BitConverter.UInt32BitsToSingle uy
                    let wz = BitConverter.UInt32BitsToSingle uz

                    ValueSome
                        {
                            PickId = pickId
                            WorldPosition = Vector3 (wx, wy, wz)
                        }
            finally
                wgpuBufferUnmap (pickReadbackBuffer)

    /// <summary>Executes a 1x1-scissor hardware GPU picking pass at pixel `(px, py)`.</summary>
    member this.PickAtPixel
        (px: int, py: int, camera: Camera3D, batches: seq<InstanceBatch>, ?sectionPlane: SectionPlane, ?discBatches: seq<DiscBatch>)
        : PickHit voption =
        let pass =
            buildLegacyScenePass camera batches None None sectionPlane None discBatches

        this.Pick (px, py, pass)

    /// <summary>Releases all native WebGPU pipelines, buffers, textures, queue, device, adapter, and instance handles.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true
            destroyTargets ()

            if pickReadbackBuffer <> 0n then
                wgpuBufferDestroy pickReadbackBuffer
                wgpuBufferRelease pickReadbackBuffer

            releasePipelineState ()
            releaseDevice ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>Curried functional pipeline helpers for <see cref="T:Ranvier.Wgpu.WgpuContext"/>.</summary>
module WgpuContext =
    /// <summary>Creates a new offscreen `WgpuContext` of `width` by `height` device pixels.</summary>
    let inline create (width: int) (height: int) : WgpuContext =
        new WgpuContext (initialWidth = width, initialHeight = height)

    /// <summary>Uploads `mesh` to `context`.</summary>
    let inline uploadMesh (mesh: MeshData) (context: WgpuContext) : MeshHandle =
        context.UploadMesh mesh

    /// <summary>Creates an opaque single-sided `InstanceBatch` for `mesh` on `context`.</summary>
    let inline createOpaqueBatch (mesh: MeshHandle) (context: WgpuContext) : InstanceBatch =
        context.CreateBatch (mesh, BlendMode.Opaque, false)

    /// <summary>Creates an `InstanceBatch` with explicit `blendMode` and `doubleSided` flags on `context`.</summary>
    let inline createBatch (blendMode: BlendMode) (doubleSided: bool) (mesh: MeshHandle) (context: WgpuContext) : InstanceBatch =
        context.CreateBatch (mesh, blendMode, doubleSided)

    /// <summary>Creates a 4x-MSAA `LineBatch` on `context`.</summary>
    let inline createLineBatch (context: WgpuContext) : LineBatch =
        context.CreateLineBatch ()

    /// <summary>Creates a camera-facing SDF `DiscBatch` on `context`.</summary>
    let inline createDiscBatch (context: WgpuContext) : DiscBatch =
        context.CreateDiscBatch ()

    /// <summary>Writes `values` into the user uniform block of `context`, starting at slot 0.</summary>
    let inline setUserUniforms (values: Vector4 array) (context: WgpuContext) : unit =
        context.SetUserUniforms values

    /// <summary>Checks `wgsl` without opening a window; `Error` carries the compiler's message.</summary>
    let inline validateWgsl (wgsl: string) : Result<unit, string> =
        WgpuContext.ValidateWgsl wgsl

    /// <summary>Renders `pass` on `context` and updates `context.RgbaPixels`.</summary>
    let inline render (context: WgpuContext) (pass: ScenePass) : FrameMetrics =
        context.Render pass

    /// <summary>Executes 1x1 GPU picking at `(px, py)` for `pass` on `context`.</summary>
    let inline pick (px: int) (py: int) (context: WgpuContext) (pass: ScenePass) : PickHit voption =
        context.Pick (px, py, pass)

    /// <summary>Resizes `context` to `width` by `height` device pixels.</summary>
    let inline resize (width: int) (height: int) (context: WgpuContext) : unit =
        context.Resize (width, height)

    /// <summary>Disposes `context`.</summary>
    let inline dispose (context: WgpuContext) : unit =
        context.Dispose ()
