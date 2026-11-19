namespace Ranvier.Wgpu.Sdl

open System
open System.Runtime.InteropServices
open System.Threading
open Microsoft.FSharp.NativeInterop
open Ranvier.Dom
open Ranvier.View.WebCore
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Native

#nowarn "9"
#nowarn "51"

/// <summary>Active GPU presentation transport bridging `Ranvier.Wgpu` into a `Ranvier.View.WebCore.Sdl` DOM element.</summary>
[<RequireQualifiedAccess; Struct>]
type WgpuPresentationMode =
    /// <summary>Zero-readback Windows DX12 to D3D11 NT-shared VRAM texture presentation synchronized via GPU shared fence.</summary>
    | Dx12SharedTexture
    /// <summary>Zero-readback macOS Metal to `IOSurfaceRef` VRAM texture presentation composited in hardware by WebCore.</summary>
    | MetalIOSurface
    /// <summary>Hardware WebCore shared-texture composition fed by `WgpuContext` upload when cross-API NT sharing is unavailable.</summary>
    | GpuTextureUpload
    /// <summary>Software DOM canvas fallback when hardware GPU element composition is unavailable.</summary>
    | CanvasFallback

    override this.ToString() =
        match this with
        | Dx12SharedTexture -> "Dx12SharedTexture"
        | MetalIOSurface -> "MetalIOSurface"
        | GpuTextureUpload -> "GpuTextureUpload"
        | CanvasFallback -> "CanvasFallback"

module private Win32Dx12Bridge =
    let inline addrOf (value: byref<'T>) : nativeint =
        NativePtr.toNativeInt &&value

    let iidIUnknown = Guid "00000000-0000-0000-C000-000000000046"
    let iidSwapChainPanelNativeUwp = Guid "63AAD0B8-7C24-40FF-85A8-640D944CC325"
    let iidSwapChainPanelNativeWinUi3 = Guid "F92F19D2-3ADE-45A6-A20C-F6F1EA90554B"
    let iidSwapChain3 = Guid "94d99bdb-f1f8-4ab0-b236-7da0170edab1"
    let iidD3D12Resource = Guid "696442be-a72e-4059-bc79-5b5c98040fad"
    let iidD3D12Device = Guid "189819f1-1db6-4b57-be54-1821339b85f7"
    let iidD3D12CommandQueue = Guid "0ec870a6-5d7e-4c22-8cfc-5baae07616ed"
    let iidD3D12CommandAllocator = Guid "6102dee4-af59-4b09-b999-b44d73f09b24"
    let iidD3D12GraphicsCommandList = Guid "5b160d0f-ac1b-4185-8ba8-b3ae42a5a455"
    let iidD3D12Fence = Guid "0a753dcf-c4d8-4b91-adf6-be5a60d95a76"

    [<Literal>]
    let E_NOINTERFACE = 0x80004002

    [<Literal>]
    let D3D12_COMMAND_LIST_TYPE_COPY = 3

    [<Literal>]
    let D3D12_FENCE_FLAG_NONE = 0

    [<Literal>]
    let D3D12_FENCE_FLAG_SHARED = 2

    [<Literal>]
    let GENERIC_ALL = 0x10000000u

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type D3D12CommandQueueDesc =
        val mutable Type: int
        val mutable Priority: int
        val mutable Flags: int
        val mutable NodeMask: uint32

    // Reverse-COM sink delegates for ISwapChainPanelNative
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PanelQueryInterface = delegate of nativeint * nativeint * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PanelAddRefRelease = delegate of nativeint -> uint32

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PanelSetSwapChain = delegate of nativeint * nativeint -> int

    // Forward-COM call delegates
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type SwapChainGetBuffer = delegate of nativeint * uint32 * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type SwapChainGetCurrentBackBufferIndex = delegate of nativeint -> uint32

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type ResourceGetDevice = delegate of nativeint * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DeviceCreateCommandQueue = delegate of nativeint * byref<D3D12CommandQueueDesc> * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DeviceCreateCommandAllocator = delegate of nativeint * int * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DeviceCreateCommandList = delegate of nativeint * uint32 * int * nativeint * nativeint * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DeviceCreateSharedHandle = delegate of nativeint * nativeint * nativeint * uint32 * nativeint * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DeviceOpenSharedHandle = delegate of nativeint * nativeint * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DeviceCreateFence = delegate of nativeint * uint64 * int * byref<Guid> * byref<nativeint> -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type AllocatorOrListCall = delegate of nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CommandListReset = delegate of nativeint * nativeint * nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CommandListCopyResource = delegate of nativeint * nativeint * nativeint -> unit

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type QueueExecuteCommandLists = delegate of nativeint * uint32 * byref<nativeint> -> unit

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type QueueSignal = delegate of nativeint * nativeint * uint64 -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FenceGetCompletedValue = delegate of nativeint -> uint64

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FenceSetEventOnCompletion = delegate of nativeint * uint64 * nativeint -> int

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint CreateEventW(nativeint lpEventAttributes, int bManualReset, int bInitialState, nativeint lpName)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 WaitForSingleObject(nativeint hHandle, uint32 dwMilliseconds)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int CloseHandle(nativeint hObject)

    let inline comMethod<'T when 'T :> Delegate> (thisPtr: nativeint) (slot: int) : 'T =
        Marshal.GetDelegateForFunctionPointer<'T>(Marshal.ReadIntPtr (Marshal.ReadIntPtr thisPtr, slot * IntPtr.Size))

    let inline releaseCom (ptr: byref<nativeint>) =
        if ptr <> 0n then
            Marshal.Release ptr |> ignore
            ptr <- 0n

    // Sink memory layout (3 * IntPtr.Size bytes):
    //   [0]             lpVtbl
    //   [IntPtr.Size]   refCount (int32)
    //   [2*IntPtr.Size] captured IDXGISwapChain* (AddRef'd)
    let private qiDel =
        PanelQueryInterface (fun thisPtr riidPtr ppvObject ->
            if riidPtr = 0n then
                ppvObject <- 0n
                E_NOINTERFACE
            else
                let iid = NativePtr.read (NativePtr.ofNativeInt<Guid> riidPtr)

                if
                    iid = iidIUnknown
                    || iid = iidSwapChainPanelNativeUwp
                    || iid = iidSwapChainPanelNativeWinUi3
                then
                    ppvObject <- thisPtr

                    Interlocked.Increment (NativePtr.toByRef (NativePtr.ofNativeInt<int>(thisPtr + nativeint IntPtr.Size)))
                    |> ignore

                    0
                else
                    ppvObject <- 0n
                    E_NOINTERFACE)

    let private addRefDel =
        PanelAddRefRelease (fun thisPtr ->
            uint32 (Interlocked.Increment (NativePtr.toByRef (NativePtr.ofNativeInt<int>(thisPtr + nativeint IntPtr.Size)))))

    let private releaseDel =
        PanelAddRefRelease (fun thisPtr ->
            let next =
                Interlocked.Decrement (NativePtr.toByRef (NativePtr.ofNativeInt<int>(thisPtr + nativeint IntPtr.Size)))

            uint32 (max 0 next))

    let private setSwapChainDel =
        PanelSetSwapChain (fun thisPtr swapChainPtr ->
            let prev = Marshal.ReadIntPtr (thisPtr, 2 * IntPtr.Size)

            if prev <> 0n then
                Marshal.Release prev |> ignore

            if swapChainPtr <> 0n then
                Marshal.AddRef swapChainPtr |> ignore

            Marshal.WriteIntPtr (thisPtr, 2 * IntPtr.Size, swapChainPtr)
            0)

    let private sinkVtable =
        lazy
            (let vt = Marshal.AllocHGlobal (4 * IntPtr.Size)
             Marshal.WriteIntPtr (vt, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate<PanelQueryInterface>(qiDel))
             Marshal.WriteIntPtr (vt, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate<PanelAddRefRelease>(addRefDel))
             Marshal.WriteIntPtr (vt, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate<PanelAddRefRelease>(releaseDel))
             Marshal.WriteIntPtr (vt, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate<PanelSetSwapChain>(setSwapChainDel))
             vt)

    [<Sealed>]
    type Dx12SharedPresenter
        private
        (
            context: WgpuContext,
            d3dSurface: D3D11GpuSurface,
            sinkPtr: nativeint,
            wgpuSurface: nativeint,
            alphaMode: uint32,
            presentMode: uint32,
            surfUsage: uint64,
            d3d12Device: nativeint,
            copyQueue: nativeint,
            copyAllocs: nativeint array,
            copyList: nativeint,
            copyFence: nativeint,
            d3d11SharedFence: nativeint,
            fenceEvent: nativeint,
            initialSwapChain3: nativeint,
            initialNtHandle: nativeint,
            initialSharedRes: nativeint,
            initialW: int,
            initialH: int
        ) =
        let mutable disposed = false
        let mutable swapChain3 = initialSwapChain3

        let mutable getBackIdxFn =
            comMethod<SwapChainGetCurrentBackBufferIndex> initialSwapChain3 36

        let mutable getBufFn = comMethod<SwapChainGetBuffer> initialSwapChain3 9
        let cachedBackBuffers = Array.zeroCreate<nativeint> 4
        let allocFenceValues = Array.zeroCreate<uint64> copyAllocs.Length
        let mutable allocSlot = 0
        let mutable cachedNtHandle = initialNtHandle
        let mutable sharedD3D12Res = initialSharedRes
        let mutable cfgWidth = initialW
        let mutable cfgHeight = initialH
        let mutable fenceValue = 0uL

        let openSharedFn = comMethod<DeviceOpenSharedHandle> d3d12Device 32

        let resetAllocFns =
            copyAllocs
            |> Array.map (fun ca -> comMethod<AllocatorOrListCall> ca 8)

        let closeListFn = comMethod<AllocatorOrListCall> copyList 9
        let resetListFn = comMethod<CommandListReset> copyList 10
        let copyResFn = comMethod<CommandListCopyResource> copyList 17
        let execListsFn = comMethod<QueueExecuteCommandLists> copyQueue 10
        let signalQueueFn = comMethod<QueueSignal> copyQueue 14
        let completedValFn = comMethod<FenceGetCompletedValue> copyFence 8
        let setEventFn = comMethod<FenceSetEventOnCompletion> copyFence 9

        let clearCachedBackBuffers () =
            for i = 0 to cachedBackBuffers.Length - 1 do
                if cachedBackBuffers[i] <> 0n then
                    Marshal.Release cachedBackBuffers[i] |> ignore
                    cachedBackBuffers[i] <- 0n

        let waitFenceCpu (targetVal: uint64) =
            if
                targetVal > 0uL
                && completedValFn.Invoke copyFence < targetVal
            then
                if setEventFn.Invoke (copyFence, targetVal, fenceEvent) < 0 then
                    invalidOp "ID3D12Fence::SetEventOnCompletion failed"

                WaitForSingleObject (fenceEvent, 5000u) |> ignore

        let refreshSwapChain3 () =
            clearCachedBackBuffers ()
            let rawSc = Marshal.ReadIntPtr (sinkPtr, 2 * IntPtr.Size)

            if rawSc = 0n then
                invalidOp "wgpu DX12 SwapChainPanel surface did not provide an IDXGISwapChain"

            releaseCom &swapChain3
            let mutable sc3Id = iidSwapChain3
            let mutable sc3 = 0n

            if
                Marshal.QueryInterface (rawSc, &sc3Id, &sc3) < 0
                || sc3 = 0n
            then
                invalidOp "QueryInterface(IDXGISwapChain3) failed on wgpu DX12 swapchain"

            swapChain3 <- sc3
            getBackIdxFn <- comMethod<SwapChainGetCurrentBackBufferIndex> sc3 36
            getBufFn <- comMethod<SwapChainGetBuffer> sc3 9

        let acquireBackBuffer (backIdx: uint32) =
            let idx = int backIdx

            if
                idx >= 0
                && idx < cachedBackBuffers.Length
                && cachedBackBuffers[idx] <> 0n
            then
                cachedBackBuffers[idx], false
            else
                let mutable resId = iidD3D12Resource
                let mutable backBufRes = 0n
                let hrBuf = getBufFn.Invoke (swapChain3, backIdx, &resId, &backBufRes)

                if hrBuf < 0 || backBufRes = 0n then
                    invalidOp $"IDXGISwapChain3::GetBuffer({backIdx}) failed: 0x{hrBuf:X8}"

                if idx >= 0 && idx < cachedBackBuffers.Length then
                    cachedBackBuffers[idx] <- backBufRes
                    backBufRes, false
                else
                    backBufRes, true

        let ensureConfigured (w: int) (h: int) =
            let tw = max 1 w
            let th = max 1 h

            if tw <> cfgWidth || th <> cfgHeight then
                waitFenceCpu fenceValue
                clearCachedBackBuffers ()
                let mutable cfg = WGPUSurfaceConfiguration ()
                cfg.Device <- context.DeviceHandle
                cfg.Format <- WGPUTextureFormat_RGBA8Unorm
                cfg.Usage <- surfUsage
                cfg.Width <- uint32 tw
                cfg.Height <- uint32 th
                cfg.AlphaMode <- alphaMode
                cfg.PresentMode <- presentMode
                wgpuSurfaceConfigure (wgpuSurface, addrOf &cfg)
                refreshSwapChain3 ()
                cfgWidth <- tw
                cfgHeight <- th

            let ntHandle = d3dSurface.AcquireSharedStagingHandle (tw, th)

            if ntHandle <> cachedNtHandle || sharedD3D12Res = 0n then
                waitFenceCpu fenceValue
                releaseCom &sharedD3D12Res
                let mutable resId = iidD3D12Resource
                let mutable opened = 0n
                let hr = openSharedFn.Invoke (d3d12Device, ntHandle, &resId, &opened)

                if hr < 0 || opened = 0n then
                    invalidOp $"ID3D12Device::OpenSharedHandle failed during resize: 0x{hr:X8}"

                sharedD3D12Res <- opened
                cachedNtHandle <- ntHandle

        let acquireSurfaceTarget () =
            ensureConfigured context.Width context.Height

            let mutable surfTex = WGPUSurfaceTexture ()
            wgpuSurfaceGetCurrentTexture (wgpuSurface, addrOf &surfTex)

            if
                (surfTex.Status
                 <> WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal
                 && surfTex.Status
                    <> WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal)
                || surfTex.Texture = 0n
            then
                if surfTex.Texture <> 0n then
                    wgpuTextureRelease surfTex.Texture

                waitFenceCpu fenceValue
                let mutable cfg = WGPUSurfaceConfiguration ()
                cfg.Device <- context.DeviceHandle
                cfg.Format <- WGPUTextureFormat_RGBA8Unorm
                cfg.Usage <- surfUsage
                cfg.Width <- uint32 cfgWidth
                cfg.Height <- uint32 cfgHeight
                cfg.AlphaMode <- alphaMode
                cfg.PresentMode <- presentMode
                wgpuSurfaceConfigure (wgpuSurface, addrOf &cfg)
                refreshSwapChain3 ()
                surfTex <- WGPUSurfaceTexture ()
                wgpuSurfaceGetCurrentTexture (wgpuSurface, addrOf &surfTex)

                if surfTex.Texture = 0n then
                    invalidOp $"wgpuSurfaceGetCurrentTexture failed with status {surfTex.Status}"

            let backIdx = getBackIdxFn.Invoke swapChain3
            let surfView = wgpuTextureCreateView (surfTex.Texture, 0n)

            if surfView = 0n then
                wgpuTextureRelease surfTex.Texture
                invalidOp "wgpuTextureCreateView on DX12 surface texture failed"

            struct (surfTex.Texture, surfView, backIdx)

        let copyAndCommitBackBuffer (backIdx: uint32) =
            // Hardware DMA copy from DX12 swapchain backbuffer[backIdx] -> NT-shared D3D11/D3D12 staging texture.
            let backBufRes, mustReleaseBackBuf = acquireBackBuffer backIdx
            let slot = allocSlot
            allocSlot <- (allocSlot + 1) % copyAllocs.Length

            try
                // Only wait if this allocator slot from 2 frames ago is somehow still in flight.
                waitFenceCpu allocFenceValues[slot]

                if resetAllocFns[slot].Invoke copyAllocs[slot] < 0 then
                    invalidOp "ID3D12CommandAllocator::Reset failed"

                if resetListFn.Invoke (copyList, copyAllocs[slot], 0n) < 0 then
                    invalidOp "ID3D12GraphicsCommandList::Reset failed"

                copyResFn.Invoke (copyList, sharedD3D12Res, backBufRes)

                if closeListFn.Invoke copyList < 0 then
                    invalidOp "ID3D12GraphicsCommandList::Close failed"

                let mutable listPtr = copyList
                execListsFn.Invoke (copyQueue, 1u, &listPtr)

                fenceValue <- fenceValue + 1uL
                let targetVal = fenceValue

                if signalQueueFn.Invoke (copyQueue, copyFence, targetVal) < 0 then
                    invalidOp "ID3D12CommandQueue::Signal failed"

                allocFenceValues[slot] <- targetVal

                if d3d11SharedFence <> 0n then
                    // Zero-CPU-stall GPU-timeline handoff: ID3D11DeviceContext4::Wait + CopyResource + Flush.
                    d3dSurface.CommitSharedStagingAfterFence (d3d11SharedFence, targetVal)
                else
                    waitFenceCpu targetVal
                    d3dSurface.CommitSharedStaging ()
            finally
                if mustReleaseBackBuf then
                    let mutable tmp = backBufRes
                    releaseCom &tmp

        static member TryCreate
            (context: WgpuContext, d3dSurface: D3D11GpuSurface, initialWidth: int, initialHeight: int)
            : Dx12SharedPresenter voption =
            if
                not (OperatingSystem.IsWindows ())
                || context.BackendType <> WGPUBackendType_D3D12
                || context.ColorTargetFormat
                   <> WGPUTextureFormat_RGBA8Unorm
            then
                ValueNone
            else
                let w = max 1 initialWidth
                let h = max 1 initialHeight
                let sink = Marshal.AllocHGlobal (3 * IntPtr.Size)
                Marshal.WriteIntPtr (sink, 0 * IntPtr.Size, sinkVtable.Value)
                Marshal.WriteInt32 (sink, IntPtr.Size, 1)
                Marshal.WriteIntPtr (sink, 2 * IntPtr.Size, 0n)

                let mutable surf = 0n
                let mutable sc3 = 0n
                let mutable d3d12Dev = 0n
                let mutable cQueue = 0n
                let mutable cAlloc0 = 0n
                let mutable cAlloc1 = 0n
                let mutable cList = 0n
                let mutable cFence = 0n
                let mutable d3d11Fence = 0n
                let mutable hEvent = 0n
                let mutable sharedRes = 0n
                let mutable configured = false

                let cleanupPartial () =
                    releaseCom &sharedRes

                    if d3d11Fence <> 0n then
                        d3dSurface.ReleaseFence d3d11Fence
                        d3d11Fence <- 0n

                    if hEvent <> 0n then
                        CloseHandle hEvent |> ignore
                        hEvent <- 0n

                    releaseCom &cFence
                    releaseCom &cList
                    releaseCom &cAlloc1
                    releaseCom &cAlloc0
                    releaseCom &cQueue
                    releaseCom &d3d12Dev
                    releaseCom &sc3

                    if surf <> 0n then
                        if configured then
                            wgpuSurfaceUnconfigure surf

                        wgpuSurfaceRelease surf
                        surf <- 0n

                    let capSc = Marshal.ReadIntPtr (sink, 2 * IntPtr.Size)

                    if capSc <> 0n then
                        Marshal.Release capSc |> ignore

                    Marshal.FreeHGlobal sink

                try
                    let mutable panelSrc = WGPUSurfaceSourceSwapChainPanel ()
                    panelSrc.Chain.SType <- WGPUSType_SurfaceSourceSwapChainPanel
                    panelSrc.PanelNative <- sink

                    let mutable surfDesc = WGPUSurfaceDescriptor ()
                    surfDesc.NextInChain <- addrOf &panelSrc
                    surfDesc.Label <- WGPUStringView.Null
                    surf <- wgpuInstanceCreateSurface (context.InstanceHandle, addrOf &surfDesc)

                    if surf = 0n then
                        cleanupPartial ()
                        ValueNone
                    else
                        let mutable caps = WGPUSurfaceCapabilities ()

                        if
                            wgpuSurfaceGetCapabilities (surf, context.AdapterHandle, addrOf &caps)
                            <> WGPUStatus_Success
                        then
                            cleanupPartial ()
                            ValueNone
                        else
                            let mutable hasRgba8 = false
                            let fmtPtr = NativePtr.ofNativeInt<uint32> caps.Formats

                            for i = 0 to int caps.FormatCount - 1 do
                                if NativePtr.get fmtPtr i = WGPUTextureFormat_RGBA8Unorm then
                                    hasRgba8 <- true

                            let mutable chosenPresent = WGPUPresentMode_Fifo
                            let pmPtr = NativePtr.ofNativeInt<uint32> caps.PresentModes

                            for i = 0 to int caps.PresentModeCount - 1 do
                                let pm = NativePtr.get pmPtr i

                                if pm = WGPUPresentMode_Immediate then
                                    chosenPresent <- WGPUPresentMode_Immediate
                                elif
                                    pm = WGPUPresentMode_Mailbox
                                    && chosenPresent <> WGPUPresentMode_Immediate
                                then
                                    chosenPresent <- WGPUPresentMode_Mailbox

                            let mutable chosenAlpha =
                                if caps.AlphaModeCount > 0un then
                                    NativePtr.get (NativePtr.ofNativeInt<uint32> caps.AlphaModes) 0
                                else
                                    WGPUCompositeAlphaMode_Auto

                            let amPtr = NativePtr.ofNativeInt<uint32> caps.AlphaModes

                            for i = 0 to int caps.AlphaModeCount - 1 do
                                let am = NativePtr.get amPtr i

                                if am = WGPUCompositeAlphaMode_Premultiplied then
                                    chosenAlpha <- WGPUCompositeAlphaMode_Premultiplied

                            let usage =
                                WGPUTextureUsage_RenderAttachment
                                ||| (caps.Usages &&& WGPUTextureUsage_CopySrc)

                            wgpuSurfaceCapabilitiesFreeMembers caps

                            if not hasRgba8 then
                                cleanupPartial ()
                                ValueNone
                            else
                                let mutable cfg = WGPUSurfaceConfiguration ()
                                cfg.Device <- context.DeviceHandle
                                cfg.Format <- WGPUTextureFormat_RGBA8Unorm
                                cfg.Usage <- usage
                                cfg.Width <- uint32 w
                                cfg.Height <- uint32 h
                                cfg.AlphaMode <- chosenAlpha
                                cfg.PresentMode <- chosenPresent
                                wgpuSurfaceConfigure (surf, addrOf &cfg)
                                configured <- true

                                let rawSc = Marshal.ReadIntPtr (sink, 2 * IntPtr.Size)
                                let mutable sc3Id = iidSwapChain3

                                if
                                    rawSc = 0n
                                    || Marshal.QueryInterface (rawSc, &sc3Id, &sc3) < 0
                                    || sc3 = 0n
                                then
                                    cleanupPartial ()
                                    ValueNone
                                else
                                    let getBuf = comMethod<SwapChainGetBuffer> sc3 9
                                    let mutable resId = iidD3D12Resource
                                    let mutable buf0 = 0n

                                    if
                                        getBuf.Invoke (sc3, 0u, &resId, &buf0) < 0
                                        || buf0 = 0n
                                    then
                                        cleanupPartial ()
                                        ValueNone
                                    else
                                        let getDev = comMethod<ResourceGetDevice> buf0 7
                                        let mutable devId = iidD3D12Device
                                        let hrDev = getDev.Invoke (buf0, &devId, &d3d12Dev)
                                        releaseCom &buf0

                                        if hrDev < 0 || d3d12Dev = 0n then
                                            cleanupPartial ()
                                            ValueNone
                                        else
                                            let mutable qDesc = D3D12CommandQueueDesc ()
                                            qDesc.Type <- D3D12_COMMAND_LIST_TYPE_COPY
                                            let createQueue = comMethod<DeviceCreateCommandQueue> d3d12Dev 8
                                            let createAlloc = comMethod<DeviceCreateCommandAllocator> d3d12Dev 9
                                            let createList = comMethod<DeviceCreateCommandList> d3d12Dev 12
                                            let createSharedHandle = comMethod<DeviceCreateSharedHandle> d3d12Dev 31
                                            let openShared = comMethod<DeviceOpenSharedHandle> d3d12Dev 32
                                            let createFence = comMethod<DeviceCreateFence> d3d12Dev 36

                                            let mutable qId = iidD3D12CommandQueue
                                            let mutable aId = iidD3D12CommandAllocator
                                            let mutable lId = iidD3D12GraphicsCommandList
                                            let mutable fId = iidD3D12Fence

                                            let okQueue =
                                                createQueue.Invoke (d3d12Dev, &qDesc, &qId, &cQueue)
                                                >= 0
                                                && cQueue <> 0n

                                            let okAlloc0 =
                                                okQueue
                                                && createAlloc.Invoke (d3d12Dev, D3D12_COMMAND_LIST_TYPE_COPY, &aId, &cAlloc0)
                                                   >= 0
                                                && cAlloc0 <> 0n

                                            let okAlloc1 =
                                                okAlloc0
                                                && createAlloc.Invoke (d3d12Dev, D3D12_COMMAND_LIST_TYPE_COPY, &aId, &cAlloc1)
                                                   >= 0
                                                && cAlloc1 <> 0n

                                            let okList =
                                                okAlloc1
                                                && createList.Invoke (d3d12Dev, 0u, D3D12_COMMAND_LIST_TYPE_COPY, cAlloc0, 0n, &lId, &cList)
                                                   >= 0
                                                && cList <> 0n

                                            if okList then
                                                (comMethod<AllocatorOrListCall> cList 9).Invoke cList
                                                |> ignore

                                            // Prefer a cross-API NT-shared GPU fence (`ID3D12Fence` -> `ID3D11Fence`) so DX12 -> D3D11 never stalls the CPU thread.
                                            let mutable okFence = false

                                            if okList && d3dSurface.SupportsSharedFence then
                                                if
                                                    createFence.Invoke (d3d12Dev, 0uL, D3D12_FENCE_FLAG_SHARED, &fId, &cFence)
                                                    >= 0
                                                    && cFence <> 0n
                                                then
                                                    okFence <- true
                                                    let mutable fenceNt = 0n

                                                    if
                                                        createSharedHandle.Invoke (d3d12Dev, cFence, 0n, GENERIC_ALL, 0n, &fenceNt)
                                                        >= 0
                                                        && fenceNt <> 0n
                                                    then
                                                        try
                                                            d3d11Fence <- d3dSurface.OpenSharedFence fenceNt
                                                        with _ ->
                                                            d3d11Fence <- 0n

                                                        CloseHandle fenceNt |> ignore

                                            if not okFence && okList then
                                                okFence <-
                                                    createFence.Invoke (d3d12Dev, 0uL, D3D12_FENCE_FLAG_NONE, &fId, &cFence)
                                                    >= 0
                                                    && cFence <> 0n

                                            hEvent <- if okFence then CreateEventW (0n, 0, 0, 0n) else 0n

                                            let ntHandle =
                                                if hEvent <> 0n then
                                                    d3dSurface.AcquireSharedStagingHandle (w, h)
                                                else
                                                    0n

                                            let okShared =
                                                ntHandle <> 0n
                                                && openShared.Invoke (d3d12Dev, ntHandle, &resId, &sharedRes)
                                                   >= 0
                                                && sharedRes <> 0n

                                            if not okShared then
                                                cleanupPartial ()
                                                ValueNone
                                            else
                                                ValueSome (
                                                    new Dx12SharedPresenter (
                                                        context,
                                                        d3dSurface,
                                                        sink,
                                                        surf,
                                                        chosenAlpha,
                                                        chosenPresent,
                                                        usage,
                                                        d3d12Dev,
                                                        cQueue,
                                                        [| cAlloc0; cAlloc1 |],
                                                        cList,
                                                        cFence,
                                                        d3d11Fence,
                                                        hEvent,
                                                        sc3,
                                                        ntHandle,
                                                        sharedRes,
                                                        w,
                                                        h
                                                    )
                                                )
                with _ ->
                    cleanupPartial ()
                    ValueNone

        member _.Resize(width: int, height: int) =
            if not disposed then
                ensureConfigured width height

        member _.RenderPass(pass: ScenePass) : FrameMetrics =
            let struct (surfTexHandle, surfView, backIdx) = acquireSurfaceTarget ()

            let metrics =
                try
                    let m =
                        try
                            context.RenderGpuPass (pass, resolveViewOverride = surfView)
                        finally
                            wgpuTextureViewRelease surfView

                    // Present BEFORE releasing surfTex.Texture, then synchronize wgpu's DX12 queue barrier to COMMON.
                    let _ = wgpuSurfacePresent wgpuSurface
                    let _ = wgpuDevicePoll (context.DeviceHandle, WGPU_TRUE, 0n)
                    m
                finally
                    wgpuTextureRelease surfTexHandle

            copyAndCommitBackBuffer backIdx
            metrics

        member _.RenderPasses(passes: ReadOnlySpan<ScenePass>, ?clearColor: RgbaColor) : FrameMetrics =
            let struct (surfTexHandle, surfView, backIdx) = acquireSurfaceTarget ()

            let metrics =
                try
                    let m =
                        try
                            context.RenderGpuPasses (passes, ?clearColor = clearColor, resolveViewOverride = surfView)
                        finally
                            wgpuTextureViewRelease surfView

                    let _ = wgpuSurfacePresent wgpuSurface
                    let _ = wgpuDevicePoll (context.DeviceHandle, WGPU_TRUE, 0n)
                    m
                finally
                    wgpuTextureRelease surfTexHandle

            copyAndCommitBackBuffer backIdx
            metrics

        member _.Dispose() =
            if not disposed then
                disposed <- true
                waitFenceCpu fenceValue
                clearCachedBackBuffers ()
                releaseCom &sharedD3D12Res

                if d3d11SharedFence <> 0n then
                    d3dSurface.ReleaseFence d3d11SharedFence

                if fenceEvent <> 0n then
                    CloseHandle fenceEvent |> ignore

                let mutable cf = copyFence in
                releaseCom &cf
                let mutable cl = copyList in
                releaseCom &cl

                for i = 0 to copyAllocs.Length - 1 do
                    let mutable ca = copyAllocs[i]
                    releaseCom &ca

                let mutable cq = copyQueue in
                releaseCom &cq
                let mutable dev = d3d12Device in
                releaseCom &dev
                releaseCom &swapChain3

                if wgpuSurface <> 0n then
                    wgpuSurfaceUnconfigure wgpuSurface
                    wgpuSurfaceRelease wgpuSurface

                let capSc = Marshal.ReadIntPtr (sinkPtr, 2 * IntPtr.Size)

                if capSc <> 0n then
                    Marshal.Release capSc |> ignore

                Marshal.FreeHGlobal sinkPtr

module private MacMetalBridge =
    [<Literal>]
    let private ObjCLib = "/usr/lib/libobjc.A.dylib"

    [<Literal>]
    let private CoreFoundationLib =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"

    [<Literal>]
    let private IOSurfaceLib =
        "/System/Library/Frameworks/IOSurface.framework/IOSurface"

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type MtlOrigin =
        val mutable X: unativeint
        val mutable Y: unativeint
        val mutable Z: unativeint

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type MtlSize =
        val mutable Width: unativeint
        val mutable Height: unativeint
        val mutable Depth: unativeint

    [<DllImport(ObjCLib, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint objc_getClass(string name)

    [<DllImport(ObjCLib, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint sel_registerName(string name)

    [<DllImport(ObjCLib, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint objc_autoreleasePoolPush()

    [<DllImport(ObjCLib, CallingConvention = CallingConvention.Cdecl)>]
    extern void objc_autoreleasePoolPop(nativeint pool)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint msgSend_IntPtr(nativeint receiver, nativeint selector)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern void msgSend_Void(nativeint receiver, nativeint selector)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint msgSend_IntPtr_UIntPtr(nativeint receiver, nativeint selector, unativeint arg1)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint msgSend_IntPtr_Int(nativeint receiver, nativeint selector, int arg1)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern void msgSend_Void_UIntPtr(nativeint receiver, nativeint selector, unativeint arg1)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern void msgSend_Void_IntPtr_IntPtr(nativeint receiver, nativeint selector, nativeint obj, nativeint key)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint msgSend_Tex2DDesc(
        nativeint receiver,
        nativeint selector,
        unativeint pixelFormat,
        unativeint width,
        unativeint height,
        byte mipmapped
    )

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint msgSend_NewTexWithIOSurface(nativeint receiver, nativeint selector, nativeint descriptor, nativeint iosurface, unativeint plane)

    [<DllImport(ObjCLib, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)>]
    extern void msgSend_CopyFromTexture(
        nativeint receiver,
        nativeint selector,
        nativeint srcTex,
        unativeint srcSlice,
        unativeint srcLevel,
        MtlOrigin srcOrigin,
        MtlSize srcSize,
        nativeint dstTex,
        unativeint dstSlice,
        unativeint dstLevel,
        MtlOrigin dstOrigin
    )

    [<DllImport(CoreFoundationLib, CallingConvention = CallingConvention.Cdecl)>]
    extern void CFRelease(nativeint cf)

    [<DllImport(IOSurfaceLib, CallingConvention = CallingConvention.Cdecl)>]
    extern unativeint IOSurfaceAlignProperty(nativeint property, unativeint value)

    [<DllImport(IOSurfaceLib, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint IOSurfaceCreate(nativeint properties)

    [<Sealed>]
    type MetalIOSurfacePresenter
        private (context: WgpuContext, mtlSurface: MetalGpuSurface, mtlDevice: nativeint, mtlQueue: nativeint, ioLib: nativeint) =
        let mutable disposed = false
        let mutable allocW = 0
        let mutable allocH = 0
        // 3-slot IOSurface ring so producer, retained WebCore compositor slot, and WindowServer scanout never contend.
        let ioSurfaces = [| 0n; 0n; 0n |]
        let mtlTextures = [| 0n; 0n; 0n |]
        let mutable ringIndex = 0

        let readCfKey (symbol: string) =
            Marshal.ReadIntPtr (NativeLibrary.GetExport (ioLib, symbol))

        let kWidth = readCfKey "kIOSurfaceWidth"
        let kHeight = readCfKey "kIOSurfaceHeight"
        let kBytesPerElement = readCfKey "kIOSurfaceBytesPerElement"
        let kBytesPerRow = readCfKey "kIOSurfaceBytesPerRow"
        let kPixelFormat = readCfKey "kIOSurfacePixelFormat"

        let clsNSMutableDict = objc_getClass "NSMutableDictionary"
        let clsNSNumber = objc_getClass "NSNumber"
        let clsMTLTextureDesc = objc_getClass "MTLTextureDescriptor"
        let selDictWithCap = sel_registerName "dictionaryWithCapacity:"
        let selSetObjForKey = sel_registerName "setObject:forKey:"
        let selNumWithInt = sel_registerName "numberWithInt:"

        let selTex2DDesc =
            sel_registerName "texture2DDescriptorWithPixelFormat:width:height:mipmapped:"

        let selSetUsage = sel_registerName "setUsage:"
        let selSetStorageMode = sel_registerName "setStorageMode:"

        let selNewTexIOSurface =
            sel_registerName "newTextureWithDescriptor:iosurface:plane:"

        let selRelease = sel_registerName "release"
        let selCommandBuffer = sel_registerName "commandBuffer"
        let selBlitEncoder = sel_registerName "blitCommandEncoder"

        let selCopyTex =
            sel_registerName
                "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"

        let selEndEncoding = sel_registerName "endEncoding"
        let selCommit = sel_registerName "commit"
        let selWaitCompleted = sel_registerName "waitUntilCompleted"

        let drainQueue () =
            if mtlQueue <> 0n then
                let pool = objc_autoreleasePoolPush ()

                try
                    let cmdBuf = msgSend_IntPtr (mtlQueue, selCommandBuffer)

                    if cmdBuf <> 0n then
                        msgSend_Void (cmdBuf, selCommit)
                        msgSend_Void (cmdBuf, selWaitCompleted)
                finally
                    objc_autoreleasePoolPop pool

        let releaseBuffers () =
            if ioSurfaces[0] <> 0n then
                drainQueue ()

            for i = 0 to ioSurfaces.Length - 1 do
                if mtlTextures[i] <> 0n then
                    msgSend_Void (mtlTextures[i], selRelease)
                    mtlTextures[i] <- 0n

                if ioSurfaces[i] <> 0n then
                    CFRelease ioSurfaces[i]
                    ioSurfaces[i] <- 0n

            allocW <- 0
            allocH <- 0

        let ensureBuffers (w: int) (h: int) =
            let tw = max 1 w
            let th = max 1 h

            if tw <> allocW || th <> allocH || ioSurfaces[0] = 0n then
                releaseBuffers ()
                let pool = objc_autoreleasePoolPush ()

                try
                    let alignedRow = int (IOSurfaceAlignProperty (kBytesPerRow, unativeint (tw * 4)))

                    for i = 0 to ioSurfaces.Length - 1 do
                        let dict = msgSend_IntPtr_UIntPtr (clsNSMutableDict, selDictWithCap, 5un)
                        msgSend_Void_IntPtr_IntPtr (dict, selSetObjForKey, msgSend_IntPtr_Int (clsNSNumber, selNumWithInt, tw), kWidth)
                        msgSend_Void_IntPtr_IntPtr (dict, selSetObjForKey, msgSend_IntPtr_Int (clsNSNumber, selNumWithInt, th), kHeight)
                        msgSend_Void_IntPtr_IntPtr (dict, selSetObjForKey, msgSend_IntPtr_Int (clsNSNumber, selNumWithInt, 4), kBytesPerElement)
                        msgSend_Void_IntPtr_IntPtr (dict, selSetObjForKey, msgSend_IntPtr_Int (clsNSNumber, selNumWithInt, alignedRow), kBytesPerRow)

                        msgSend_Void_IntPtr_IntPtr (
                            dict,
                            selSetObjForKey,
                            msgSend_IntPtr_Int (clsNSNumber, selNumWithInt, 0x42475241 (* 'BGRA' *) ),
                            kPixelFormat
                        )

                        let surf = IOSurfaceCreate dict

                        if surf = 0n then
                            invalidOp "IOSurfaceCreate failed"

                        ioSurfaces[i] <- surf

                        let desc =
                            msgSend_Tex2DDesc (clsMTLTextureDesc, selTex2DDesc, 80un, unativeint tw, unativeint th, 0uy)

                        msgSend_Void_UIntPtr (desc, selSetUsage, 5un)
                        msgSend_Void_UIntPtr (desc, selSetStorageMode, 0un)

                        let tex =
                            msgSend_NewTexWithIOSurface (mtlDevice, selNewTexIOSurface, desc, surf, 0un)

                        if tex = 0n then
                            invalidOp "newTextureWithDescriptor:iosurface:plane: failed"

                        mtlTextures[i] <- tex

                    allocW <- tw
                    allocH <- th
                finally
                    objc_autoreleasePoolPop pool

        let publishResolved (w: int) (h: int) =
            let _ = wgpuDevicePoll (context.DeviceHandle, WGPU_TRUE, 0n)

            let srcMtlTex = wgpuTextureGetNativeMetalTexture context.ResolveTextureHandle
            let slot = ringIndex % ioSurfaces.Length
            ringIndex <- ringIndex + 1
            let dstMtlTex = mtlTextures[slot]
            let dstIOSurf = ioSurfaces[slot]

            let pool = objc_autoreleasePoolPush ()

            try
                let cmdBuf = msgSend_IntPtr (mtlQueue, selCommandBuffer)
                let blit = msgSend_IntPtr (cmdBuf, selBlitEncoder)
                let origin = MtlOrigin ()
                let mutable size = MtlSize ()
                size.Width <- unativeint w
                size.Height <- unativeint h
                size.Depth <- 1un
                msgSend_CopyFromTexture (blit, selCopyTex, srcMtlTex, 0un, 0un, origin, size, dstMtlTex, 0un, 0un, origin)
                msgSend_Void (blit, selEndEncoding)
                msgSend_Void (cmdBuf, selCommit)
                msgSend_Void (cmdBuf, selWaitCompleted)
            finally
                objc_autoreleasePoolPop pool

            mtlSurface.PublishIOSurface (dstIOSurf, w, h, flipY = false)

        static member TryCreate
            (context: WgpuContext, mtlSurface: MetalGpuSurface, initialWidth: int, initialHeight: int)
            : MetalIOSurfacePresenter voption =
            if
                not (OperatingSystem.IsMacOS ())
                || context.BackendType <> WGPUBackendType_Metal
                || context.ColorTargetFormat
                   <> WGPUTextureFormat_BGRA8Unorm
            then
                ValueNone
            else
                let mutable ioLib = 0n

                if
                    not (NativeLibrary.TryLoad (IOSurfaceLib, &ioLib))
                    || ioLib = 0n
                then
                    ValueNone
                else
                    try
                        let mtlDev = wgpuDeviceGetNativeMetalDevice context.DeviceHandle

                        let probeTex =
                            if mtlDev <> 0n then
                                wgpuTextureGetNativeMetalTexture context.ResolveTextureHandle
                            else
                                0n

                        if mtlDev = 0n || probeTex = 0n then
                            NativeLibrary.Free ioLib
                            ValueNone
                        else
                            let selNewQueue = sel_registerName "newCommandQueue"
                            let mtlQ = msgSend_IntPtr (mtlDev, selNewQueue)

                            if mtlQ = 0n then
                                NativeLibrary.Free ioLib
                                ValueNone
                            else
                                let presenter =
                                    new MetalIOSurfacePresenter (context, mtlSurface, mtlDev, mtlQ, ioLib)

                                presenter.Resize (initialWidth, initialHeight)
                                ValueSome presenter
                    with _ ->
                        if ioLib <> 0n then
                            NativeLibrary.Free ioLib

                        ValueNone

        member _.Resize(width: int, height: int) =
            if not disposed then
                ensureBuffers width height

        member _.RenderPass(pass: ScenePass) : FrameMetrics =
            let w = context.Width
            let h = context.Height
            ensureBuffers w h

            let metrics = context.RenderGpuPass pass
            publishResolved w h
            metrics

        member _.RenderPasses(passes: ReadOnlySpan<ScenePass>, ?clearColor: RgbaColor) : FrameMetrics =
            let w = context.Width
            let h = context.Height
            ensureBuffers w h

            let metrics = context.RenderGpuPasses (passes, ?clearColor = clearColor)
            publishResolved w h
            metrics

        member _.Dispose() =
            if not disposed then
                disposed <- true
                releaseBuffers ()

                if mtlQueue <> 0n then
                    msgSend_Void (mtlQueue, selRelease)

                if ioLib <> 0n then
                    NativeLibrary.Free ioLib

/// <summary>
/// Opt-in hardware GPU bridge binding a `Ranvier.Wgpu.WgpuContext` directly to a composited DOM element in a
/// `Ranvier.View.WebCore.Sdl.Window`. HTML, CSS, and SVG overlays composite naturally over the 3D surface while
/// accidental drag-selection highlighting is disabled by default.
/// </summary>
[<Sealed>]
type WgpuSdlSurface
    private
    (
        element: Element,
        gpuSurface: GpuSurface,
        context: WgpuContext,
        mode: WgpuPresentationMode,
        dx12Bridge: Win32Dx12Bridge.Dx12SharedPresenter voption,
        metalBridge: MacMetalBridge.MetalIOSurfacePresenter voption
    ) =
    let mutable disposed = false

    let checkActive () =
        if disposed then
            raise (ObjectDisposedException (nameof WgpuSdlSurface))

        if gpuSurface.IsDisposed then
            raise (ObjectDisposedException (nameof GpuSurface))

        if context.IsDisposed then
            raise (ObjectDisposedException (nameof WgpuContext))

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
            Viewport = ValueNone
            Batches = batchMem
            LineBatches = lineMem
            DiscBatches = discMem
        }

    /// <summary>
    /// Creates a `WgpuContext` configured for zero-copy hardware presentation into `window`, matching the window's
    /// GPU adapter and device-pixel dimensions automatically when explicit dimensions are omitted.
    /// </summary>
    static member CreateContext(window: Window, ?width: int, ?height: int, ?customWgsl: string) : WgpuContext =
        if isNull (box window) then
            nullArg (nameof window)

        let struct (pixelW, pixelH) = window.PixelSize
        let docEl = window.Host.Document.documentElement

        let w =
            match width with
            | Some explicitW -> max 1 explicitW
            | None when pixelW > 0 -> pixelW
            | None ->
                let cw = if isNull (box docEl) then 0 else int docEl.clientWidth
                if cw > 0 then cw else 960

        let h =
            match height with
            | Some explicitH -> max 1 explicitH
            | None when pixelH > 0 -> pixelH
            | None ->
                let ch = if isNull (box docEl) then 0 else int docEl.clientHeight
                if ch > 0 then ch else 708

        if
            OperatingSystem.IsWindows ()
            && window.IsGpuComposited
        then
            new WgpuContext (
                initialWidth = w,
                initialHeight = h,
                ?customWgsl = customWgsl,
                backend = WgpuBackendPreference.Dx12,
                ?preferredVendorDevice = ValueOption.toOption window.GpuAdapterVendorDevice
            )
        elif
            OperatingSystem.IsMacOS ()
            && window.IsGpuComposited
        then
            new WgpuContext (
                initialWidth = w,
                initialHeight = h,
                ?customWgsl = customWgsl,
                backend = WgpuBackendPreference.Metal,
                useBgraColorTarget = true
            )
        else
            new WgpuContext (initialWidth = w, initialHeight = h, ?customWgsl = customWgsl)

    /// <summary>
    /// Binds a `WgpuContext` to `element` inside `window`, disabling drag-selection highlighting on the composited
    /// layer by default while allowing interactive child controls to receive clicks and focus.
    /// </summary>
    static member Bind(window: Window, element: Element, context: WgpuContext, ?preventSelection: bool) : WgpuSdlSurface =
        if isNull (box window) then
            nullArg (nameof window)

        if isNull (box element) then
            nullArg (nameof element)

        if isNull (box context) then
            nullArg (nameof context)

        let gpuSurface =
            match preventSelection with
            | Some flag -> window.BindGpuSurface (element, preventSelection = flag)
            | None -> window.BindGpuSurface (element)

        try
            match gpuSurface.Backend with
            | GpuSurfaceBackend.D3D11 d3d ->
                match Win32Dx12Bridge.Dx12SharedPresenter.TryCreate (context, d3d, context.Width, context.Height) with
                | ValueSome dx12 ->
                    new WgpuSdlSurface (element, gpuSurface, context, WgpuPresentationMode.Dx12SharedTexture, ValueSome dx12, ValueNone)
                | ValueNone -> new WgpuSdlSurface (element, gpuSurface, context, WgpuPresentationMode.GpuTextureUpload, ValueNone, ValueNone)
            | GpuSurfaceBackend.Metal mtl ->
                match MacMetalBridge.MetalIOSurfacePresenter.TryCreate (context, mtl, context.Width, context.Height) with
                | ValueSome metal ->
                    new WgpuSdlSurface (element, gpuSurface, context, WgpuPresentationMode.MetalIOSurface, ValueNone, ValueSome metal)
                | ValueNone -> new WgpuSdlSurface (element, gpuSurface, context, WgpuPresentationMode.GpuTextureUpload, ValueNone, ValueNone)
            | GpuSurfaceBackend.Egl _ ->
                new WgpuSdlSurface (element, gpuSurface, context, WgpuPresentationMode.GpuTextureUpload, ValueNone, ValueNone)
            | GpuSurfaceBackend.CpuCanvas ->
                new WgpuSdlSurface (element, gpuSurface, context, WgpuPresentationMode.CanvasFallback, ValueNone, ValueNone)
        with _ ->
            gpuSurface.Dispose ()
            reraise ()

    /// <summary>Bound DOM element whose composited layer displays this 3D surface.</summary>
    member _.Element = element

    /// <summary>Underlying `Ranvier.View.WebCore.Sdl.GpuSurface` binding.</summary>
    member _.GpuSurface = gpuSurface

    /// <summary>Bound `WgpuContext`.</summary>
    member _.Context = context

    /// <summary>Active GPU presentation transport mode.</summary>
    member _.Mode = mode

    /// <summary>True when frames render and present purely in VRAM with zero CPU readback (`Dx12SharedTexture` or `MetalIOSurface`).</summary>
    member _.IsZeroCopyGpu =
        mode = WgpuPresentationMode.Dx12SharedTexture
        || mode = WgpuPresentationMode.MetalIOSurface

    /// <summary>True when WebCore composites this element via hardware GPU shared textures rather than software CPU canvas paints.</summary>
    member _.IsHardwareComposited =
        mode = WgpuPresentationMode.Dx12SharedTexture
        || mode = WgpuPresentationMode.MetalIOSurface
        || mode = WgpuPresentationMode.GpuTextureUpload

    /// <summary>Current 3D render target pixel width.</summary>
    member _.Width = context.Width

    /// <summary>Current 3D render target pixel height.</summary>
    member _.Height = context.Height

    /// <summary>Total frames published through this surface.</summary>
    member _.FramesPresented = gpuSurface.FramesPublished

    /// <summary>True after `Dispose` has detached the surface.</summary>
    member _.IsDisposed = disposed

    /// <summary>Resizes the `WgpuContext` render targets and backing shared GPU textures.</summary>
    member _.Resize(width: int, height: int) =
        checkActive ()
        let w = max 1 width
        let h = max 1 height
        context.Resize (w, h)

        dx12Bridge
        |> ValueOption.iter (fun b -> b.Resize (w, h))

        metalBridge
        |> ValueOption.iter (fun b -> b.Resize (w, h))

    /// <summary>Renders a zero-allocation `ScenePass` and publishes it directly into the bound WebCore composited element.</summary>
    member _.Render(pass: ScenePass) : FrameMetrics =
        checkActive ()

        match dx12Bridge with
        | ValueSome dx12 -> dx12.RenderPass pass
        | ValueNone ->
            match metalBridge with
            | ValueSome metal -> metal.RenderPass pass
            | ValueNone ->
                let metrics = context.Render pass
                gpuSurface.PresentPixels (ReadOnlySpan<byte>(context.RgbaPixels), context.Width, context.Height)
                metrics

    /// <summary>Renders multiple `ScenePass` items sequentially and publishes the combined frame into the bound WebCore composited element.</summary>
    member _.RenderPasses(passes: ReadOnlySpan<ScenePass>, ?clearColor: RgbaColor) : FrameMetrics =
        checkActive ()

        match dx12Bridge with
        | ValueSome dx12 -> dx12.RenderPasses (passes, ?clearColor = clearColor)
        | ValueNone ->
            match metalBridge with
            | ValueSome metal -> metal.RenderPasses (passes, ?clearColor = clearColor)
            | ValueNone ->
                let metrics = context.RenderPasses (passes, ?clearColor = clearColor)
                gpuSurface.PresentPixels (ReadOnlySpan<byte>(context.RgbaPixels), context.Width, context.Height)
                metrics

    /// <summary>Renders an array of `ScenePass` items sequentially and publishes the combined frame into the bound WebCore composited element.</summary>
    member this.RenderPasses(passes: ScenePass array, ?clearColor: RgbaColor) : FrameMetrics =
        if isNull passes then
            nullArg (nameof passes)

        this.RenderPasses (ReadOnlySpan<ScenePass>(passes), ?clearColor = clearColor)

    /// <summary>Renders a 4x-MSAA 3D frame and publishes it directly into the bound WebCore composited element.</summary>
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

        this.Render pass

    /// <summary>Unbinds the GPU surface from the DOM element and releases bridge GPU resources without disposing `Context`.</summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true

            dx12Bridge
            |> ValueOption.iter (fun b -> b.Dispose ())

            metalBridge
            |> ValueOption.iter (fun b -> b.Dispose ())

            gpuSurface.Dispose ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

/// <summary>Curried functional pipeline and `lend` helpers for <see cref="T:Ranvier.Wgpu.Sdl.WgpuSdlSurface"/>.</summary>
module WgpuSdlSurface =
    /// <summary>Creates a `WgpuContext` sized and adapter-matched for `window`.</summary>
    let inline createContext (window: Window) : WgpuContext =
        WgpuSdlSurface.CreateContext window

    /// <summary>Creates a `WgpuContext` of `width` by `height` device pixels adapter-matched for `window`.</summary>
    let inline createContextSized (width: int) (height: int) (window: Window) : WgpuContext =
        WgpuSdlSurface.CreateContext (window, width = width, height = height)

    /// <summary>Binds `context` to `element` on `window` with `preventSelection = true`.</summary>
    let inline bind (window: Window) (context: WgpuContext) (element: Element) : WgpuSdlSurface =
        WgpuSdlSurface.Bind (window, element, context)

    /// <summary>Binds `context` to `element` on `window` with explicit `preventSelection`.</summary>
    let inline bindWith (preventSelection: bool) (window: Window) (context: WgpuContext) (element: Element) : WgpuSdlSurface =
        WgpuSdlSurface.Bind (window, element, context, preventSelection = preventSelection)

    /// <summary>Renders `pass` and publishes it into `surface`.</summary>
    let inline render (surface: WgpuSdlSurface) (pass: ScenePass) : FrameMetrics =
        surface.Render pass

    /// <summary>Renders `passes` sequentially and publishes the combined frame into `surface`.</summary>
    let inline renderPasses (surface: WgpuSdlSurface) (passes: ScenePass array) : FrameMetrics =
        surface.RenderPasses passes

    /// <summary>Resizes `surface` and its `WgpuContext` to `width` by `height` device pixels.</summary>
    let inline resize (width: int) (height: int) (surface: WgpuSdlSurface) : unit =
        surface.Resize (width, height)

    /// <summary>Disposes `surface` without disposing its `WgpuContext`.</summary>
    let inline dispose (surface: WgpuSdlSurface) : unit =
        surface.Dispose ()

    /// <summary>
    /// Attaches a `WgpuSdlSurface` for the mounted lifetime of an element via `div.lend` or `canvas.lend`,
    /// running `setup` and disposing both the user subscription and surface binding cleanly on unmount.
    /// </summary>
    let lend (window: Window) (context: WgpuContext) (setup: WgpuSdlSurface -> IDisposable) (element: #Element) : IDisposable =
        let surface = WgpuSdlSurface.Bind (window, element :> Element, context)

        let userSub =
            try
                setup surface
            with _ ->
                surface.Dispose ()
                reraise ()

        { new IDisposable with
            member _.Dispose() =
                try
                    if not (isNull userSub) then
                        userSub.Dispose ()
                finally
                    surface.Dispose ()
        }

/// <summary>Method extensions on <see cref="T:Ranvier.View.WebCore.Sdl.Window"/> for binding `WgpuContext` surfaces.</summary>
[<AutoOpen>]
module WindowWgpuExtensions =
    type Window with
        /// <summary>
        /// Binds `context` directly to `element` for hardware-composited 3D presentation,
        /// disabling accidental drag-selection highlighting by default (`preventSelection = true`).
        /// </summary>
        member this.BindWgpuSurface(element: Element, context: WgpuContext, ?preventSelection: bool) : WgpuSdlSurface =
            match preventSelection with
            | Some flag -> WgpuSdlSurface.Bind (this, element, context, preventSelection = flag)
            | None -> WgpuSdlSurface.Bind (this, element, context)
