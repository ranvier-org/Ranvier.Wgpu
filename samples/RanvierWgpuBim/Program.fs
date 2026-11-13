module RanvierWgpuBim.Program

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Numerics
open System.Text
open Ranvier
open Ranvier.Dom
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.WebCore
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Sdl

[<Literal>]
let RenderWidth = 820

[<Literal>]
let RenderHeight = 766

type InteractionMode =
    | OrbitCamera
    | DragSelectedXZ

[<RequireQualifiedAccess; Struct>]
type SectionCutMode =
    | Off
    | PlanL1
    | LongitudinalZ
    | DiagonalCut

[<RequireQualifiedAccess; Struct>]
type BimGeometry =
    | SolidBox
    | GlazingPanel
    | GableRoof

type BimElementSpec =
    {
        Id: string
        Name: string
        IfcClass: string
        Level: string
        MaterialName: string
        SwatchHex: string
        BaseColor: RgbaColor
        Roughness: float32
        Geometry: BimGeometry
        Dimensions: Vector3
        InitialPos: Vector3
        InitialYawDeg: float32
        Deletable: bool
    }

type LiveBimElement =
    {
        Spec: BimElementSpec
        mutable Pos: Vector3
        mutable YawDeg: float32
        mutable Scale: Vector3
    }
    member this.EffectiveDimensions = this.Spec.Dimensions * this.Scale

type ElementRowView =
    {
        Id: string
        Name: string
        IfcClass: string
        Level: string
        SwatchHex: string
        IsSelected: bool
        OnSelect: unit -> unit
    }

type ProjectedCallout =
    {
        Visible: bool
        LeftPct: float
        TopPct: float
        Label: string
        SubLabel: string
    }

let private fmtF (v: float32) = v.ToString("F2", CultureInfo.InvariantCulture)
let private rgb (r: byte) (g: byte) (b: byte) = RgbaColor.fromRgb24 r g b
let private rgba (r: byte) (g: byte) (b: byte) (alpha: float32) = (RgbaColor.fromRgb24 r g b).WithAlpha(alpha)

let private mkSpec id name ifc level mat hex color rough geom dim pos deletable =
    {
        Id = id
        Name = name
        IfcClass = ifc
        Level = level
        MaterialName = mat
        SwatchHex = hex
        BaseColor = color
        Roughness = rough
        Geometry = geom
        Dimensions = dim
        InitialPos = pos
        InitialYawDeg = 0.0f
        Deletable = deletable
    }

let private initialSpecs: BimElementSpec list =
    [
        // L0 Foundation Slab
        mkSpec "S-101" "Foundation Plinth Slab" "IfcSlab" "L0 · 0.00m" "Reinforced Concrete C35/45" "#64748b" (rgb 102uy 114uy 130uy) 0.86f BimGeometry.SolidBox (Vector3(12.40f, 0.36f, 9.20f)) (Vector3(0.0f, -0.18f, 0.0f)) false
        // L1 Structural Steel Corner & Atrium Columns
        mkSpec "C-101" "NW Corner Column" "IfcColumn" "L1 · +0.00m" "Structural Steel S355" "#38bdf8" (rgb 56uy 189uy 248uy) 0.34f BimGeometry.SolidBox (Vector3(0.46f, 3.60f, 0.46f)) (Vector3(-4.80f, 1.80f, -3.40f)) true
        mkSpec "C-102" "NE Corner Column" "IfcColumn" "L1 · +0.00m" "Structural Steel S355" "#38bdf8" (rgb 56uy 189uy 248uy) 0.34f BimGeometry.SolidBox (Vector3(0.46f, 3.60f, 0.46f)) (Vector3(4.80f, 1.80f, -3.40f)) true
        mkSpec "C-103" "SW Corner Column" "IfcColumn" "L1 · +0.00m" "Structural Steel S355" "#38bdf8" (rgb 56uy 189uy 248uy) 0.34f BimGeometry.SolidBox (Vector3(0.46f, 3.60f, 0.46f)) (Vector3(-4.80f, 1.80f, 3.40f)) true
        mkSpec "C-104" "SE Corner Column" "IfcColumn" "L1 · +0.00m" "Structural Steel S355" "#38bdf8" (rgb 56uy 189uy 248uy) 0.34f BimGeometry.SolidBox (Vector3(0.46f, 3.60f, 0.46f)) (Vector3(4.80f, 1.80f, 3.40f)) true
        mkSpec "C-105" "Interior Atrium Column" "IfcColumn" "L1 · +0.00m" "Glulam / Steel Composite" "#a78bfa" (rgb 167uy 139uy 250uy) 0.42f BimGeometry.SolidBox (Vector3(0.50f, 3.60f, 0.50f)) (Vector3(1.10f, 1.80f, -1.80f)) true
        // L1 Concrete Shear Walls & Interior Core Partition
        mkSpec "W-101" "North Shear Wall" "IfcWallStandardCase" "L1 · +0.00m" "Precast Concrete Panel" "#cbd5e1" (rgb 176uy 190uy 208uy) 0.78f BimGeometry.SolidBox (Vector3(9.10f, 3.56f, 0.28f)) (Vector3(0.0f, 1.78f, -3.40f)) true
        mkSpec "W-102" "East Shear Wall" "IfcWallStandardCase" "L1 · +0.00m" "Precast Concrete Panel" "#cbd5e1" (rgb 165uy 180uy 198uy) 0.78f BimGeometry.SolidBox (Vector3(0.28f, 3.56f, 6.30f)) (Vector3(4.80f, 1.78f, 0.0f)) true
        mkSpec "W-103" "Interior Core Partition" "IfcWallStandardCase" "L1 · +0.00m" "Board-Formed Concrete Core" "#e2e8f0" (rgb 203uy 213uy 225uy) 0.74f BimGeometry.SolidBox (Vector3(0.28f, 3.56f, 4.20f)) (Vector3(2.60f, 1.78f, -0.40f)) true
        // L1 Translucent Curtain-Wall Glazing (BlendMode.AlphaBlend ~ 0.42)
        mkSpec "WIN-101" "South Curtain-Wall Glazing" "IfcWindow" "L1 · +0.00m" "Triple Low-E Architectural Glass" "#2dd4bf" (rgba 45uy 212uy 191uy 0.42f) 0.10f BimGeometry.GlazingPanel (Vector3(8.90f, 3.36f, 0.10f)) (Vector3(0.0f, 1.72f, 3.30f)) true
        mkSpec "WIN-102" "West Atrium Glazing" "IfcWindow" "L1 · +0.00m" "Triple Low-E Architectural Glass" "#38bdf8" (rgba 68uy 202uy 248uy 0.42f) 0.10f BimGeometry.GlazingPanel (Vector3(0.10f, 3.36f, 5.90f)) (Vector3(-4.68f, 1.72f, 0.0f)) true
        // L2 Primary Steel Girders & Composite Floor Deck
        mkSpec "B-201" "North Roof Girder" "IfcBeam" "L2 · +3.60m" "Weathering Steel IPE400" "#f59e0b" (rgb 245uy 158uy 11uy) 0.40f BimGeometry.SolidBox (Vector3(10.06f, 0.40f, 0.34f)) (Vector3(0.0f, 3.80f, -3.40f)) true
        mkSpec "B-202" "South Roof Girder" "IfcBeam" "L2 · +3.60m" "Weathering Steel IPE400" "#f59e0b" (rgb 245uy 158uy 11uy) 0.40f BimGeometry.SolidBox (Vector3(10.06f, 0.40f, 0.34f)) (Vector3(0.0f, 3.80f, 3.40f)) true
        mkSpec "B-203" "West Tie Girder" "IfcBeam" "L2 · +3.60m" "Weathering Steel IPE360" "#fbbf24" (rgb 234uy 179uy 8uy) 0.40f BimGeometry.SolidBox (Vector3(0.34f, 0.40f, 7.10f)) (Vector3(-4.80f, 3.80f, 0.0f)) true
        mkSpec "B-204" "East Tie Girder" "IfcBeam" "L2 · +3.60m" "Weathering Steel IPE360" "#fbbf24" (rgb 234uy 179uy 8uy) 0.40f BimGeometry.SolidBox (Vector3(0.34f, 0.40f, 7.10f)) (Vector3(4.80f, 3.80f, 0.0f)) true
        mkSpec "S-201" "L2 Composite Floor Deck" "IfcSlab" "L2 · +3.60m" "CLT / Concrete Composite Slab" "#94a3b8" (rgb 132uy 146uy 166uy) 0.72f BimGeometry.SolidBox (Vector3(9.90f, 0.26f, 7.04f)) (Vector3(0.0f, 3.73f, 0.0f)) true
        // Pitched Gable Roof (Primitives.unitGableRoof)
        mkSpec "R-301" "Pitched Standing-Seam Roof" "IfcRoof" "RF · +4.00m" "Terracotta Standing-Seam Zinc" "#e06d53" (rgb 217uy 107uy 76uy) 0.50f BimGeometry.GableRoof (Vector3(10.12f, 1.90f, 7.44f)) (Vector3(0.0f, 4.95f, 0.0f)) true
    ]

/// Native Ranvier.Wgpu 3D BIM Engine (4x MSAA, dynamic instancing, WGSL section cuts, 1x1 GPU pixel picking, zero-copy WebCore GPU surface).
type WgpuBimEngine(window: Window) =
    let wgpu = WgpuSdlSurface.CreateContext(window, RenderWidth, RenderHeight)

    let gridMesh =
        let mb = MeshBuilder()
        mb.AddGridXZ(14.0f, 1.0f, 0.002f, RgbaColor.fromRgb24 34uy 47uy 66uy, RgbaColor.fromRgb24 72uy 108uy 148uy)
        wgpu.UploadMesh(mb.ToMeshData())

    let boxMesh = wgpu.UploadMesh(Primitives.unitBox ())
    let roofMesh = wgpu.UploadMesh(Primitives.unitGableRoof ())
    let cylMesh = wgpu.UploadMesh(Primitives.unitCylinder 20)
    let coneMesh = wgpu.UploadMesh(Primitives.unitCone 20)
    let torusMesh = wgpu.UploadMesh(Primitives.unitTorus 44 14 0.055f)

    let gridBatch = wgpu.CreateBatch(gridMesh, BlendMode.Opaque, doubleSided = false)
    let opaqueBoxBatch = wgpu.CreateBatch(boxMesh, BlendMode.Opaque, doubleSided = true)
    let roofBatch = wgpu.CreateBatch(roofMesh, BlendMode.Opaque, doubleSided = true)
    let glassBatch = wgpu.CreateBatch(boxMesh, BlendMode.AlphaBlend, doubleSided = true)
    let gizmoCylBatch = wgpu.CreateBatch(cylMesh, BlendMode.Opaque, doubleSided = false)
    let gizmoConeBatch = wgpu.CreateBatch(coneMesh, BlendMode.Opaque, doubleSided = false)
    let gizmoTorusBatch = wgpu.CreateBatch(torusMesh, BlendMode.Opaque, doubleSided = false)

    let allBatches =
        [| gridBatch; opaqueBoxBatch; roofBatch; gizmoCylBatch; gizmoConeBatch; gizmoTorusBatch; glassBatch |]

    let lightingRig =
        {
            LightingRig.studioDefault with
                SkyColor = Vector3(0.24f, 0.34f, 0.52f)
                GroundColor = Vector3(0.10f, 0.09f, 0.08f)
                KeyLightDir = Vector3.Normalize(Vector3(-0.35f, 0.80f, 0.62f))
                KeyLightIntensity = 1.15f
                RimLightDir = Vector3.Normalize(Vector3(0.62f, 0.36f, -0.58f))
                RimLightIntensity = 0.48f
                SectionContourColor = Vector3(1.0f, 0.54f, 0.02f)
                SectionContourWidth = 0.115f
        }

    let clearBackdrop = RgbaColor(0.055f, 0.078f, 0.118f, 1.0f)
    let mutable gpuSurface: WgpuSdlSurface option = None
    let mutable lastMetrics = Unchecked.defaultof<FrameMetrics>

    let elements = ResizeArray<LiveBimElement>()
    let mutable addedCounter = 1

    let mutable camTarget = Vector3(0.0f, 2.00f, 0.0f)
    let mutable camYawDeg = -34.0f
    let mutable camPitchDeg = 24.0f
    let mutable camDistance = 17.2f
    let mutable sectionMode = SectionCutMode.Off
    let mutable sectionShift = 0.0f

    let spawnInitial () =
        elements.Clear()
        addedCounter <- 1
        for spec in initialSpecs do
            elements.Add({ Spec = spec; Pos = spec.InitialPos; YawDeg = spec.InitialYawDeg; Scale = Vector3.One })

    do
        gridBatch.SetSingle(Instance3D.identity RgbaColor.white 0.85f 0u (InstanceFlags.Unlit ||| InstanceFlags.IgnoreSectionPlane))
        spawnInitial ()

    let currentCamera () = Camera3D.orbit camTarget camYawDeg camPitchDeg camDistance 45.0f

    let currentSectionPlane () : SectionPlane =
        match sectionMode with
        | SectionCutMode.Off -> SectionPlane.disabled
        | SectionCutMode.PlanL1 -> SectionPlane.create (Vector3(0.0f, -1.0f, 0.0f)) (2.20f + sectionShift)
        | SectionCutMode.LongitudinalZ -> SectionPlane.create (Vector3(0.0f, 0.0f, -1.0f)) (0.60f + sectionShift)
        | SectionCutMode.DiagonalCut -> SectionPlane.create (Vector3(0.52f, -0.58f, -0.63f)) (1.35f + sectionShift)

    let syncBatches (selectedId: string option) =
        let boxes = ResizeArray<Instance3D>(elements.Count)
        let roofs = ResizeArray<Instance3D>(2)
        let glasses = ResizeArray<Instance3D>(4)

        for idx = 0 to elements.Count - 1 do
            let el = elements[idx]
            let pickId = uint32 (idx + 1)
            let flags = if selectedId = Some el.Spec.Id then InstanceFlags.Selected else InstanceFlags.None
            let inst = Instance3D.trs el.Pos el.YawDeg el.EffectiveDimensions el.Spec.BaseColor el.Spec.Roughness pickId flags

            match el.Spec.Geometry with
            | BimGeometry.SolidBox -> boxes.Add inst
            | BimGeometry.GableRoof -> roofs.Add inst
            | BimGeometry.GlazingPanel -> glasses.Add inst

        opaqueBoxBatch.UpdateInstances(boxes.ToArray())
        roofBatch.UpdateInstances(roofs.ToArray())
        glassBatch.UpdateInstances(glasses.ToArray())

        // 3D selection gizmos (PickId = 0u, Unlit | IgnoreSectionPlane -> hardware picking passes straight through)
        match selectedId |> Option.bind (fun id -> elements |> Seq.tryFind (fun e -> e.Spec.Id = id)) with
        | None ->
            gizmoCylBatch.UpdateInstances(Array.empty<Instance3D>)
            gizmoConeBatch.UpdateInstances(Array.empty<Instance3D>)
            gizmoTorusBatch.UpdateInstances(Array.empty<Instance3D>)
        | Some el ->
            let gFlags = InstanceFlags.Unlit ||| InstanceFlags.IgnoreSectionPlane
            let eff = el.EffectiveDimensions
            let clip = currentSectionPlane ()
            let rawOrigin = Vector3(el.Pos.X, max 0.15f el.Pos.Y, el.Pos.Z)
            let origin =
                if sectionMode = SectionCutMode.PlanL1 && clip.IsClipped rawOrigin then
                    Vector3(el.Pos.X, max 0.15f (2.10f + sectionShift), el.Pos.Z)
                else
                    rawOrigin

            let arrowLen, shaftD, coneD, coneL = 1.28f, 0.075f, 0.22f, 0.34f
            let ringDiam = Math.Clamp(max eff.X eff.Z + 1.25f, 1.85f, 4.20f)
            let xCol = RgbaColor(0.98f, 0.15f, 0.18f, 1.0f)
            let yCol = RgbaColor(0.10f, 0.86f, 0.32f, 1.0f)
            let zCol = RgbaColor(0.14f, 0.48f, 1.00f, 1.0f)
            let yawCol = RgbaColor(1.00f, 0.66f, 0.04f, 1.0f)
            let hubCol = RgbaColor(0.10f, 0.88f, 1.00f, 1.0f)
            let rotToX = Matrix4x4.CreateRotationZ(-MathF.PI * 0.5f)
            let rotToZ = Matrix4x4.CreateRotationX(MathF.PI * 0.5f)
            let inline gizmo m col em = Instance3D.fromMatrix m col 0.3f 0u gFlags em

            gizmoCylBatch.UpdateInstances(
                [|
                    gizmo (Matrix4x4.CreateScale(0.24f) * Matrix4x4.CreateTranslation(origin)) hubCol 0.08f
                    gizmo (Matrix4x4.CreateScale(shaftD, arrowLen, shaftD) * rotToX * Matrix4x4.CreateTranslation(origin + Vector3(arrowLen * 0.5f, 0.0f, 0.0f))) xCol 0.05f
                    gizmo (Matrix4x4.CreateScale(shaftD, arrowLen, shaftD) * rotToZ * Matrix4x4.CreateTranslation(origin + Vector3(0.0f, 0.0f, arrowLen * 0.5f))) zCol 0.05f
                    gizmo (Matrix4x4.CreateScale(shaftD, 1.08f, shaftD) * Matrix4x4.CreateTranslation(origin + Vector3(0.0f, 0.54f, 0.0f))) yCol 0.05f
                |]
            )
            gizmoConeBatch.UpdateInstances(
                [|
                    gizmo (Matrix4x4.CreateScale(coneD, coneL, coneD) * rotToX * Matrix4x4.CreateTranslation(origin + Vector3(arrowLen + coneL * 0.5f, 0.0f, 0.0f))) xCol 0.08f
                    gizmo (Matrix4x4.CreateScale(coneD, coneL, coneD) * rotToZ * Matrix4x4.CreateTranslation(origin + Vector3(0.0f, 0.0f, arrowLen + coneL * 0.5f))) zCol 0.08f
                    gizmo (Matrix4x4.CreateScale(coneD, coneL, coneD) * Matrix4x4.CreateTranslation(origin + Vector3(0.0f, 1.08f + coneL * 0.5f, 0.0f))) yCol 0.08f
                |]
            )
            gizmoTorusBatch.SetSingle(gizmo (Matrix4x4.CreateScale(ringDiam) * Matrix4x4.CreateTranslation(origin)) yawCol 0.08f)

    member _.AdapterName = wgpu.AdapterName
    member _.BackendName = wgpu.BackendName
    member _.Elements = elements
    member _.FramesDelivered = wgpu.FramesDelivered
    member _.RenderCalls = wgpu.RenderCalls
    member _.PresentationMode = gpuSurface |> Option.map (fun s -> s.Mode)
    member _.IsZeroCopyGpu = gpuSurface |> Option.exists (fun s -> s.IsZeroCopyGpu)
    member _.LastMetrics = lastMetrics
    member _.SectionMode = sectionMode
    member _.CameraSummary = $"Yaw {camYawDeg:F0}° · Pitch {camPitchDeg:F0}° · Dist {camDistance:F1}m"

    member _.SectionSummary =
        match sectionMode with
        | SectionCutMode.Off -> "Section: Off (Full Building)"
        | SectionCutMode.PlanL1 -> $"Plan Cut L1 · Y ≤ {fmtF (2.20f + sectionShift)}m"
        | SectionCutMode.LongitudinalZ -> $"Longitudinal Z · Z ≤ {fmtF (0.60f + sectionShift)}m"
        | SectionCutMode.DiagonalCut -> $"Diagonal Cut · d = {fmtF (1.35f + sectionShift)}m"

    member this.AttachSurface(canvasEl: HTMLCanvasElement, selectedId: string option) =
        gpuSurface |> Option.iter WgpuSdlSurface.dispose
        canvasEl.width <- uint32 RenderWidth
        canvasEl.height <- uint32 RenderHeight
        let surf = canvasEl :> Element |> WgpuSdlSurface.bind window wgpu
        gpuSurface <- Some surf
        this.RenderDemandDriven(selectedId)

    member _.DetachSurface() =
        gpuSurface |> Option.iter WgpuSdlSurface.dispose
        gpuSurface <- None

    member _.RenderDemandDriven(selectedId: string option) =
        syncBatches selectedId
        let pass =
            ScenePass.create (currentCamera ())
            |> ScenePass.withClear clearBackdrop
            |> ScenePass.withLighting lightingRig
            |> ScenePass.withSection (currentSectionPlane ())
            |> ScenePass.withBatches allBatches

        lastMetrics <-
            match gpuSurface with
            | Some surf -> pass |> WgpuSdlSurface.render surf
            | None -> pass |> WgpuContext.render wgpu

    member _.ProjectWorldToPixel(worldPos: Vector3) : struct (int * int) voption =
        match Camera3D.projectToScreen RenderWidth RenderHeight (currentCamera ()) worldPos with
        | ValueNone -> ValueNone
        | ValueSome pt ->
            let px, py = int (MathF.Round pt.X), int (MathF.Round pt.Y)
            if px >= 0 && px < RenderWidth && py >= 0 && py < RenderHeight then ValueSome(struct (px, py)) else ValueNone

    member _.ProjectSelectedCallout(selectedId: string option) : ProjectedCallout =
        match selectedId |> Option.bind (fun id -> elements |> Seq.tryFind (fun e -> e.Spec.Id = id)) with
        | None -> { Visible = false; LeftPct = 50.0; TopPct = 50.0; Label = ""; SubLabel = "" }
        | Some el ->
            let rawTopY = el.Pos.Y + el.EffectiveDimensions.Y * 0.5f + 0.32f
            let anchorY =
                match sectionMode with
                | SectionCutMode.PlanL1 -> min rawTopY (2.20f + sectionShift + 0.18f)
                | _ -> rawTopY
            match Camera3D.projectToScreen RenderWidth RenderHeight (currentCamera ()) (Vector3(el.Pos.X, anchorY, el.Pos.Z)) with
            | ValueSome pt when pt.X >= 14.0f && pt.X <= float32 (RenderWidth - 14) && pt.Y >= 14.0f && pt.Y <= float32 (RenderHeight - 14) ->
                {
                    Visible = true
                    LeftPct = Math.Clamp(float pt.X / float RenderWidth * 100.0, 16.0, 84.0)
                    TopPct = Math.Clamp(float pt.Y / float RenderHeight * 100.0, 11.0, 90.0)
                    Label = $"{el.Spec.Id} · {el.Spec.Name}"
                    SubLabel = $"{el.Spec.IfcClass} ({fmtF el.Pos.X}, {fmtF el.Pos.Y}, {fmtF el.Pos.Z})m"
                }
            | _ -> { Visible = false; LeftPct = 50.0; TopPct = 50.0; Label = ""; SubLabel = "" }

    member _.PickAtPixel(px: int, py: int) : (LiveBimElement * Vector3) option =
        let cx, cy = Math.Clamp(px, 0, RenderWidth - 1), Math.Clamp(py, 0, RenderHeight - 1)
        let pass =
            ScenePass.create (currentCamera ())
            |> ScenePass.withSection (currentSectionPlane ())
            |> ScenePass.withBatches allBatches
        match pass |> WgpuContext.pick cx cy wgpu with
        | ValueSome hit when int hit.PickId >= 1 && int hit.PickId <= elements.Count ->
            Some(elements[int hit.PickId - 1], hit.WorldPosition)
        | _ -> None

    member _.DragSelectedOnHorizontalPlane(selectedId: string, px: int, py: int) : bool =
        match elements |> Seq.tryFind (fun e -> e.Spec.Id = selectedId && e.Spec.Deletable) with
        | None -> false
        | Some el ->
            let cx = float32 (Math.Clamp(px, 0, RenderWidth - 1))
            let cy = float32 (Math.Clamp(py, 0, RenderHeight - 1))
            let struct (rayOrigin, rayDir) = Camera3D.screenRay RenderWidth RenderHeight (currentCamera ()) cx cy
            if MathF.Abs rayDir.Y > 1e-4f then
                let t = (el.Pos.Y - rayOrigin.Y) / rayDir.Y
                if t > 0.0f then
                    let hit = rayOrigin + rayDir * t
                    let snap v = MathF.Round(v * 10.0f) / 10.0f
                    let nx, nz = Math.Clamp(snap hit.X, -9.5f, 9.5f), Math.Clamp(snap hit.Z, -9.5f, 9.5f)
                    if nx <> el.Pos.X || nz <> el.Pos.Z then
                        el.Pos <- Vector3(nx, el.Pos.Y, nz)
                        true
                    else
                        false
                else
                    false
            else
                false

    member _.Orbit(deltaX: float, deltaY: float) =
        camYawDeg <- camYawDeg - float32 deltaX * 0.42f
        camPitchDeg <- Math.Clamp(camPitchDeg + float32 deltaY * 0.36f, -8.0f, 84.0f)

    member _.Pan(deltaX: float, deltaY: float) =
        let cam = currentCamera ()
        let fwd = Vector3.Normalize(camTarget - cam.Position)
        let right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY))
        let up = Vector3.Normalize(Vector3.Cross(right, fwd))
        camTarget <- camTarget + (-right * float32 deltaX + up * float32 deltaY) * (camDistance * 0.0016f)

    member _.Zoom(wheelDeltaY: float) =
        let factor = if wheelDeltaY > 0.0 then 1.10f else 0.90f
        camDistance <- Math.Clamp(camDistance * factor, 3.5f, 46.0f)

    member _.SetSectionMode(mode: SectionCutMode) =
        sectionMode <- mode
        sectionShift <- 0.0f

    member _.NudgeSectionOffset(deltaMeters: float32) =
        if sectionMode = SectionCutMode.Off then
            sectionMode <- SectionCutMode.PlanL1
            sectionShift <- 0.0f
        else
            sectionShift <- Math.Clamp(sectionShift + deltaMeters, -3.5f, 3.5f)

    member _.ResetCamera() =
        camTarget <- Vector3(0.0f, 2.00f, 0.0f)
        camYawDeg <- -34.0f
        camPitchDeg <- 24.0f
        camDistance <- 17.2f

    member _.AddStructuralColumn() : string =
        let idx = addedCounter
        addedCounter <- addedCounter + 1
        let id = $"C-20{idx}"
        let angle = float (idx - 1) * 1.18
        let x = MathF.Round(float32 (Math.Cos angle * 2.2) * 10.0f) / 10.0f
        let z = MathF.Round(float32 (Math.Sin angle * 1.6) * 10.0f) / 10.0f
        let spec =
            mkSpec id $"Interior Column #{idx}" "IfcColumn" "L1 · +0.00m" "Timber Glulam GL28h" "#c084fc"
                (rgb 192uy 132uy 252uy) 0.44f BimGeometry.SolidBox (Vector3(0.42f, 3.60f, 0.42f)) (Vector3(x, 1.80f, z)) true
        elements.Add({ Spec = spec; Pos = spec.InitialPos; YawDeg = 0.0f; Scale = Vector3.One })
        id

    member _.DeleteElement(id: string) : bool =
        match elements |> Seq.tryFindIndex (fun e -> e.Spec.Id = id && e.Spec.Deletable) with
        | None -> false
        | Some idx -> elements.RemoveAt idx; true

    member _.ResetScene() =
        sectionMode <- SectionCutMode.Off
        sectionShift <- 0.0f
        spawnInitial ()

    interface IDisposable with
        member _.Dispose() =
            gpuSurface |> Option.iter (fun s -> s.Dispose())
            gpuSurface <- None
            for b in allBatches do b.Dispose()
            gridMesh.Dispose(); boxMesh.Dispose(); roofMesh.Dispose()
            cylMesh.Dispose(); coneMesh.Dispose(); torusMesh.Dispose()
            wgpu.Dispose()

type AppState =
    {
        Engine: WgpuBimEngine
        SelectedId: Signal<string option>
        LastPickedWorld: Signal<Vector3 option>
        Interaction: Signal<InteractionMode>
        Revision: Signal<int>
        StatusText: Signal<string>
        mutable CanvasEl: HTMLCanvasElement option
        mutable Dragging: bool
        mutable DragButton: int16
        mutable DragShift: bool
        mutable DragAlt: bool
        mutable DownX: float
        mutable DownY: float
        mutable LastX: float
        mutable LastY: float
    }

let private refreshView (state: AppState) (statusMsg: string option) =
    state.Engine.RenderDemandDriven(state.SelectedId.Peek)
    Api.batch (fun () ->
        state.Revision.Value <- state.Revision.Peek + 1
        statusMsg |> Option.iter (fun msg -> state.StatusText.Value <- msg))

let private selectedElement (state: AppState) : LiveBimElement option =
    let _ = state.Revision.Value
    state.SelectedId.Value |> Option.bind (fun id -> state.Engine.Elements |> Seq.tryFind (fun e -> e.Spec.Id = id))

let private mutateSelected (state: AppState) (describe: LiveBimElement -> string) (mutate: LiveBimElement -> unit) =
    match state.SelectedId.Peek |> Option.bind (fun id -> state.Engine.Elements |> Seq.tryFind (fun e -> e.Spec.Id = id)) with
    | None -> ()
    | Some el ->
        mutate el
        state.LastPickedWorld.Value <- None
        refreshView state (Some(describe el))

let private elementRowComponent: Component<unit -> ElementRowView> =
    Component.define
        (fun _ getRow -> getRow)
        (fun t ->
            t.button
                [|
                    t.Attrs.attribute ("class", fun getRow -> Some(if (getRow ()).IsSelected then "tree-row selected" else "tree-row"))
                    t.Events.onClick (fun getRow _ -> (getRow ()).OnSelect())
                |]
                [|
                    t.span [| t.Attrs.attribute ("style", fun getRow -> Some $"background:{(getRow ()).SwatchHex}"); t.Attrs.attribute ("class", "swatch") |] [||]
                    t.div
                        [| t.Attrs.attribute ("class", "tree-meta") |]
                        [|
                            t.div [| t.Attrs.attribute ("class", "tree-title") |] [| t.text (fun getRow -> $"{(getRow ()).Id} · {(getRow ()).Name}") |]
                            t.div [| t.Attrs.attribute ("class", "tree-sub") |] [| t.text (fun getRow -> $"{(getRow ()).IfcClass} · {(getRow ()).Level}") |]
                        |]
                |])

let private canvasPixelsFromState (state: AppState) (clientX: float) (clientY: float) =
    match state.CanvasEl with
    | None -> Math.Clamp(int clientX, 0, RenderWidth - 1), Math.Clamp(int clientY, 0, RenderHeight - 1)
    | Some el ->
        let rect = el.getBoundingClientRect()
        let w = if rect.width > 1.0 then rect.width else float RenderWidth
        let h = if rect.height > 1.0 then rect.height else float RenderHeight
        Math.Clamp(int (Math.Round((clientX - rect.left) / w * float RenderWidth)), 0, RenderWidth - 1),
        Math.Clamp(int (Math.Round((clientY - rect.top) / h * float RenderHeight)), 0, RenderHeight - 1)

let private bimAppComponent (state: AppState) =
    Component.define
        (fun _ () -> state)
        (fun t ->
            let propRow (keyLabel: string) (valClass: string) (readVal: AppState -> string) =
                t.div
                    [| t.Attrs.attribute ("class", "prop-row") |]
                    [|
                        t.span [| t.Attrs.attribute ("class", "prop-key") |] [| t.text keyLabel |]
                        t.span [| t.Attrs.attribute ("class", $"prop-val {valClass}") |] [| t.text readVal |]
                    |]

            let nudgeRow (tagCls: string) (tagTxt: string) (readout: AppState -> string) (decLabel: string) (onDec: AppState -> unit) (incLabel: string) (onInc: AppState -> unit) =
                t.div
                    [| t.Attrs.attribute ("class", "nudge-row") |]
                    [|
                        t.span [| t.Attrs.attribute ("class", $"axis-tag {tagCls}") |] [| t.text tagTxt |]
                        t.span [| t.Attrs.attribute ("class", "coord-readout") |] [| t.text readout |]
                        t.button [| t.Attrs.attribute ("class", "sm-btn"); t.Events.onClick (fun s _ -> onDec s) |] [| t.text decLabel |]
                        t.button [| t.Attrs.attribute ("class", "sm-btn"); t.Events.onClick (fun s _ -> onInc s) |] [| t.text incLabel |]
                    |]

            let cutModeBtn (mode: SectionCutMode) (activeCls: string) (label: string) =
                t.button
                    [|
                        t.Attrs.attribute (
                            "class",
                            fun s ->
                                let _ = s.Revision.Value
                                Some(if s.Engine.SectionMode = mode then $"sm-btn {activeCls}" else "sm-btn")
                        )
                        t.Events.onClick (fun s _ ->
                            s.Engine.SetSectionMode mode
                            refreshView s (Some $"WGSL {s.Engine.SectionSummary}"))
                    |]
                    [| t.text label |]

            t.div
                [| t.Attrs.attribute ("class", "app-shell") |]
                [|
                    // Top Toolbar
                    t.div
                        [| t.Attrs.attribute ("class", "topbar") |]
                        [|
                            t.div
                                [| t.Attrs.attribute ("class", "brand") |]
                                [|
                                    t.span [| t.Attrs.attribute ("class", "brand-dot") |] [||]
                                    t.text "Ranvier.View + Ranvier.Wgpu"
                                    t.span
                                        [| t.Attrs.attribute ("class", "gpu-pill") |]
                                        [|
                                            t.text (fun s ->
                                                let _ = s.Revision.Value
                                                let modeTag = if s.Engine.IsZeroCopyGpu then "Zero-Copy · 4x MSAA" else "4x MSAA"
                                                $"{s.Engine.BackendName} · {modeTag}")
                                        |]
                                |]
                            t.div
                                [| t.Attrs.attribute ("class", "toolbar-group") |]
                                [|
                                    t.button
                                        [|
                                            t.Attrs.attribute ("class", fun s -> Some(if s.Interaction.Value = OrbitCamera then "tb-btn active" else "tb-btn"))
                                            t.Events.onClick (fun s _ ->
                                                s.Interaction.Value <- OrbitCamera
                                                s.StatusText.Value <- "Mode: Orbit Camera (left-drag orbits, click GPU-picks 3D IFC element)")
                                        |]
                                        [| t.text "Orbit Mode" |]
                                    t.button
                                        [|
                                            t.Attrs.attribute ("class", fun s -> Some(if s.Interaction.Value = DragSelectedXZ then "tb-btn active" else "tb-btn"))
                                            t.Events.onClick (fun s _ ->
                                                s.Interaction.Value <- DragSelectedXZ
                                                s.StatusText.Value <- "Mode: Drag Element XZ (left-drag translates selected element on horizontal plane)")
                                        |]
                                        [| t.text "Drag XZ" |]
                                    t.button
                                        [|
                                            t.Attrs.attribute (
                                                "class",
                                                fun s ->
                                                    let _ = s.Revision.Value
                                                    Some(if s.Engine.SectionMode = SectionCutMode.PlanL1 then "tb-btn cut-active" else "tb-btn")
                                            )
                                            t.Events.onClick (fun s _ ->
                                                let next = if s.Engine.SectionMode = SectionCutMode.PlanL1 then SectionCutMode.Off else SectionCutMode.PlanL1
                                                s.Engine.SetSectionMode next
                                                refreshView s (Some $"WGSL {s.Engine.SectionSummary}"))
                                        |]
                                        [| t.text "Plan Cut L1" |]
                                    t.button
                                        [|
                                            t.Attrs.attribute ("class", "tb-btn accent")
                                            t.Events.onClick (fun s _ ->
                                                let newId = s.Engine.AddStructuralColumn()
                                                s.SelectedId.Value <- Some newId
                                                s.LastPickedWorld.Value <- None
                                                refreshView s (Some $"Added new IfcColumn {newId} to 3D BIM model"))
                                        |]
                                        [| t.text "+ Add Column" |]
                                    t.button
                                        [|
                                            t.Attrs.attribute ("class", "tb-btn")
                                            t.Events.onClick (fun s _ -> s.Engine.ResetCamera(); refreshView s (Some "Camera reset to SW architectural orbit"))
                                        |]
                                        [| t.text "Reset Cam" |]
                                    t.button
                                        [|
                                            t.Attrs.attribute ("class", "tb-btn")
                                            t.Events.onClick (fun s _ ->
                                                s.Engine.ResetScene()
                                                s.SelectedId.Value <- Some "C-101"
                                                s.LastPickedWorld.Value <- None
                                                refreshView s (Some "BIM scene & section plane reset to initial IFC layout"))
                                        |]
                                        [| t.text "Reset Scene" |]
                                |]
                            t.div
                                [| t.Attrs.attribute ("class", "render-stats") |]
                                [|
                                    t.text (fun s ->
                                        let _ = s.Revision.Value
                                        $"Frames: {s.Engine.FramesDelivered} · GPU {s.Engine.LastMetrics.GpuRenderMs:F2}ms · {s.Engine.CameraSummary}")
                                |]
                        |]

                    // Main 3-Column Workspace
                    t.div
                        [| t.Attrs.attribute ("class", "workspace") |]
                        [|
                            // Left Sidebar: IFC Spatial Tree
                            t.div
                                [| t.Attrs.attribute ("class", "sidebar left-pane") |]
                                [|
                                    t.div
                                        [| t.Attrs.attribute ("class", "pane-header") |]
                                        [| t.text (fun s -> let _ = s.Revision.Value in $"IFC Spatial Hierarchy ({s.Engine.Elements.Count})") |]
                                    t.div
                                        [| t.Attrs.attribute ("class", "tree-list") |]
                                        [|
                                            t.each
                                                (fun (r: ElementRowView) -> r.Id)
                                                (fun s ->
                                                    let _ = s.Revision.Value
                                                    let sel = s.SelectedId.Value
                                                    s.Engine.Elements
                                                    |> Seq.map (fun el ->
                                                        let id = el.Spec.Id
                                                        {
                                                            Id = id
                                                            Name = el.Spec.Name
                                                            IfcClass = el.Spec.IfcClass
                                                            Level = el.Spec.Level
                                                            SwatchHex = el.Spec.SwatchHex
                                                            IsSelected = (sel = Some id)
                                                            OnSelect =
                                                                fun () ->
                                                                    s.SelectedId.Value <- Some id
                                                                    s.LastPickedWorld.Value <- None
                                                                    refreshView s (Some $"Selected {id} ({el.Spec.IfcClass}) from hierarchy")
                                                        })
                                                    |> Seq.toArray)
                                                elementRowComponent
                                        |]
                                |]

                            // Center Viewport: Ranvier.Wgpu -> <canvas> + Projected HTML Callout
                            t.div
                                [| t.Attrs.attribute ("class", "viewport-wrap") |]
                                [|
                                    t.canvas
                                        [|
                                            t.Attrs.attribute ("class", "bim-canvas")
                                            t.Hooks.mounted (fun s (canvasEl: HTMLCanvasElement) ->
                                                s.CanvasEl <- Some canvasEl
                                                s.Engine.AttachSurface(canvasEl, s.SelectedId.Peek)
                                                s.Revision.Value <- s.Revision.Peek + 1
                                                fun () -> s.CanvasEl <- None; s.Engine.DetachSurface())
                                            t.Events.onContextmenu (fun _ ev -> ev.preventDefault())
                                            t.Events.onMousedown (fun s ev ->
                                                ev.preventDefault()
                                                s.Dragging <- true
                                                s.DragButton <- ev.button
                                                s.DragShift <- ev.shiftKey
                                                s.DragAlt <- ev.altKey
                                                s.DownX <- ev.clientX; s.DownY <- ev.clientY
                                                s.LastX <- ev.clientX; s.LastY <- ev.clientY)
                                            t.Events.onMousemove (fun s ev ->
                                                if s.Dragging then
                                                    ev.preventDefault()
                                                    let dx, dy = ev.clientX - s.LastX, ev.clientY - s.LastY
                                                    s.LastX <- ev.clientX; s.LastY <- ev.clientY
                                                    if Math.Abs dx > 0.01 || Math.Abs dy > 0.01 then
                                                        let px, py = canvasPixelsFromState s ev.clientX ev.clientY
                                                        if s.DragButton = 2s || s.DragButton = 1s || s.DragShift then
                                                            s.Engine.Pan(dx, dy)
                                                            refreshView s None
                                                        elif (s.Interaction.Peek = DragSelectedXZ || s.DragAlt) && s.SelectedId.Peek.IsSome then
                                                            let id = s.SelectedId.Peek.Value
                                                            if s.Engine.DragSelectedOnHorizontalPlane(id, px, py) then
                                                                s.LastPickedWorld.Value <- None
                                                                refreshView s (Some $"Dragged {id} on horizontal XZ plane")
                                                        else
                                                            s.Engine.Orbit(dx, dy)
                                                            refreshView s None)
                                            t.Events.onMouseup (fun s ev ->
                                                if s.Dragging then
                                                    s.Dragging <- false
                                                    if s.DragButton = 0s && Math.Abs(ev.clientX - s.DownX) + Math.Abs(ev.clientY - s.DownY) < 5.0 then
                                                        let px, py = canvasPixelsFromState s ev.clientX ev.clientY
                                                        match s.Engine.PickAtPixel(px, py) with
                                                        | Some(picked, hitWorld) ->
                                                            s.SelectedId.Value <- Some picked.Spec.Id
                                                            s.LastPickedWorld.Value <- Some hitWorld
                                                            refreshView s (Some $"GPU Picked {picked.Spec.Id} ({picked.Spec.Name}) at ({fmtF hitWorld.X}, {fmtF hitWorld.Y}, {fmtF hitWorld.Z})m")
                                                        | None ->
                                                            s.SelectedId.Value <- None
                                                            s.LastPickedWorld.Value <- None
                                                            refreshView s (Some "Cleared selection (clicked empty backdrop)"))
                                            t.Events.onMouseleave (fun s _ -> s.Dragging <- false)
                                            t.Events.onWheel (fun s ev -> ev.preventDefault(); s.Engine.Zoom(ev.deltaY); refreshView s None)
                                        |]
                                        [||]

                                    t.div
                                        [|
                                            t.Attrs.attribute ("class", "projected-tag")
                                            t.Attrs.attribute (
                                                "style",
                                                fun s ->
                                                    let _ = s.Revision.Value
                                                    let c = s.Engine.ProjectSelectedCallout(s.SelectedId.Value)
                                                    if c.Visible then
                                                        Some(sprintf "display:flex;left:%s%%;top:%s%%" (c.LeftPct.ToString("F2", CultureInfo.InvariantCulture)) (c.TopPct.ToString("F2", CultureInfo.InvariantCulture)))
                                                    else
                                                        Some "display:none"
                                            )
                                        |]
                                        [|
                                            t.span [| t.Attrs.attribute ("class", "projected-title") |] [| t.text (fun s -> let _ = s.Revision.Value in s.Engine.ProjectSelectedCallout(s.SelectedId.Value).Label) |]
                                            t.span [| t.Attrs.attribute ("class", "projected-sub") |] [| t.text (fun s -> let _ = s.Revision.Value in s.Engine.ProjectSelectedCallout(s.SelectedId.Value).SubLabel) |]
                                        |]

                                    t.div
                                        [|
                                            t.Attrs.attribute (
                                                "class",
                                                fun s -> let _ = s.Revision.Value in Some(if s.Engine.SectionMode = SectionCutMode.Off then "cut-banner hidden" else "cut-banner")
                                            )
                                        |]
                                        [|
                                            t.span [| t.Attrs.attribute ("class", "cut-dot") |] [||]
                                            t.text (fun s -> let _ = s.Revision.Value in $"WGSL LIVE SECTION · {s.Engine.SectionSummary}")
                                        |]

                                    t.div
                                        [| t.Attrs.attribute ("class", "viewport-hud") |]
                                        [| t.text (fun s -> $"GPU: {s.Engine.AdapterName} ({s.Engine.BackendName}) · Click: 1x1 RGBA32Uint Pick · Drag: Orbit / Alt: Drag XZ · Wheel: Zoom") |]
                                |]

                            // Right Sidebar: Live WGSL Section Cut + BIM Element Inspector
                            t.div
                                [| t.Attrs.attribute ("class", "sidebar right-pane") |]
                                [|
                                    t.div [| t.Attrs.attribute ("class", "pane-header") |] [| t.text "BIM Section & Inspector" |]
                                    t.div
                                        [| t.Attrs.attribute ("class", "inspector-body") |]
                                        [|
                                            t.div
                                                [| t.Attrs.attribute ("class", "prop-card cut-card") |]
                                                [|
                                                    t.div [| t.Attrs.attribute ("class", "section-label amber") |] [| t.text "Live WGSL BIM Section Cut" |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "cut-grid") |]
                                                        [|
                                                            cutModeBtn SectionCutMode.Off "active" "Off"
                                                            cutModeBtn SectionCutMode.PlanL1 "cut-active" "Plan L1 (2.2m)"
                                                            cutModeBtn SectionCutMode.LongitudinalZ "cut-active" "Longitudinal Z"
                                                            cutModeBtn SectionCutMode.DiagonalCut "cut-active" "Diagonal Cut"
                                                        |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "coord-readout amber-txt") |]
                                                        [| t.text (fun s -> let _ = s.Revision.Value in s.Engine.SectionSummary) |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "action-row") |]
                                                        [|
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", "sm-btn full")
                                                                    t.Events.onClick (fun s _ -> s.Engine.NudgeSectionOffset -0.50f; refreshView s (Some $"Pushed section plane -> {s.Engine.SectionSummary}"))
                                                                |]
                                                                [| t.text "Push -0.5m" |]
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", "sm-btn full")
                                                                    t.Events.onClick (fun s _ -> s.Engine.NudgeSectionOffset 0.50f; refreshView s (Some $"Pulled section plane -> {s.Engine.SectionSummary}"))
                                                                |]
                                                                [| t.text "Pull +0.5m" |]
                                                        |]
                                                |]

                                            t.div
                                                [| t.Attrs.attribute ("class", "prop-card") |]
                                                [|
                                                    propRow "Element" "strong" (fun s ->
                                                        match selectedElement s with
                                                        | Some e -> $"{e.Spec.Id} — {e.Spec.Name}"
                                                        | None -> "None selected")
                                                    propRow "IFC Class" "badge" (fun s -> selectedElement s |> Option.map (fun e -> e.Spec.IfcClass) |> Option.defaultValue "—")
                                                    propRow "Storey" "" (fun s -> selectedElement s |> Option.map (fun e -> e.Spec.Level) |> Option.defaultValue "—")
                                                    propRow "Material" "" (fun s -> selectedElement s |> Option.map (fun e -> e.Spec.MaterialName) |> Option.defaultValue "—")
                                                    propRow "Dimensions" "mono" (fun s ->
                                                        match selectedElement s with
                                                        | Some e -> let d = e.EffectiveDimensions in $"{fmtF d.X} × {fmtF d.Y} × {fmtF d.Z} m"
                                                        | None -> "—")
                                                    propRow "GPU Pick 3D" "mono pick-val" (fun s ->
                                                        match s.LastPickedWorld.Value with
                                                        | Some w -> $"({fmtF w.X}, {fmtF w.Y}, {fmtF w.Z}) m"
                                                        | None ->
                                                            match selectedElement s with
                                                            | Some e -> $"centroid ({fmtF e.Pos.X}, {fmtF e.Pos.Y}, {fmtF e.Pos.Z}) m"
                                                            | None -> "—")
                                                |]

                                            t.div
                                                [| t.Attrs.attribute ("class", "prop-card") |]
                                                [|
                                                    t.div [| t.Attrs.attribute ("class", "section-label") |] [| t.text "Transform & Parametric Edit" |]
                                                    nudgeRow "x" "X" (fun s -> selectedElement s |> Option.map (fun e -> $"{fmtF e.Pos.X} m") |> Option.defaultValue "—")
                                                        "-0.25" (fun s -> mutateSelected s (fun e -> $"Moved {e.Spec.Id} X -> {fmtF e.Pos.X}m") (fun e -> e.Pos <- Vector3(e.Pos.X - 0.25f, e.Pos.Y, e.Pos.Z)))
                                                        "+0.25" (fun s -> mutateSelected s (fun e -> $"Moved {e.Spec.Id} X -> {fmtF e.Pos.X}m") (fun e -> e.Pos <- Vector3(e.Pos.X + 0.25f, e.Pos.Y, e.Pos.Z)))
                                                    nudgeRow "y" "Y" (fun s -> selectedElement s |> Option.map (fun e -> $"{fmtF e.Pos.Y} m") |> Option.defaultValue "—")
                                                        "-0.25" (fun s -> mutateSelected s (fun e -> $"Elevated {e.Spec.Id} Y -> {fmtF e.Pos.Y}m") (fun e -> e.Pos <- Vector3(e.Pos.X, e.Pos.Y - 0.25f, e.Pos.Z)))
                                                        "+0.25" (fun s -> mutateSelected s (fun e -> $"Elevated {e.Spec.Id} Y -> {fmtF e.Pos.Y}m") (fun e -> e.Pos <- Vector3(e.Pos.X, e.Pos.Y + 0.25f, e.Pos.Z)))
                                                    nudgeRow "z" "Z" (fun s -> selectedElement s |> Option.map (fun e -> $"{fmtF e.Pos.Z} m") |> Option.defaultValue "—")
                                                        "-0.25" (fun s -> mutateSelected s (fun e -> $"Moved {e.Spec.Id} Z -> {fmtF e.Pos.Z}m") (fun e -> e.Pos <- Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z - 0.25f)))
                                                        "+0.25" (fun s -> mutateSelected s (fun e -> $"Moved {e.Spec.Id} Z -> {fmtF e.Pos.Z}m") (fun e -> e.Pos <- Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z + 0.25f)))
                                                    nudgeRow "yaw" "Yaw" (fun s -> selectedElement s |> Option.map (fun e -> $"{e.YawDeg:F0}°") |> Option.defaultValue "—")
                                                        "Yaw -15°" (fun s -> mutateSelected s (fun e -> $"Rotated {e.Spec.Id} -> {e.YawDeg:F0}°") (fun e -> e.YawDeg <- (e.YawDeg - 15.0f) % 360.0f))
                                                        "Yaw +15°" (fun s -> mutateSelected s (fun e -> $"Rotated {e.Spec.Id} -> {e.YawDeg:F0}°") (fun e -> e.YawDeg <- (e.YawDeg + 15.0f) % 360.0f))
                                                    nudgeRow "y" "Hgt" (fun s -> selectedElement s |> Option.map (fun e -> $"{fmtF e.EffectiveDimensions.Y} m") |> Option.defaultValue "—")
                                                        "-10%" (fun s -> mutateSelected s (fun e -> $"Scaled {e.Spec.Id} height ({fmtF e.Scale.Y}x)") (fun e -> e.Scale <- Vector3(e.Scale.X, Math.Clamp(e.Scale.Y - 0.1f, 0.3f, 3.0f), e.Scale.Z)))
                                                        "+10%" (fun s -> mutateSelected s (fun e -> $"Scaled {e.Spec.Id} height ({fmtF e.Scale.Y}x)") (fun e -> e.Scale <- Vector3(e.Scale.X, Math.Clamp(e.Scale.Y + 0.1f, 0.3f, 3.0f), e.Scale.Z)))
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "action-row") |]
                                                        [|
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", "tb-btn full")
                                                                    t.Events.onClick (fun s _ ->
                                                                        mutateSelected s (fun e -> $"Reset {e.Spec.Id} transform") (fun e ->
                                                                            e.Pos <- e.Spec.InitialPos; e.YawDeg <- e.Spec.InitialYawDeg; e.Scale <- Vector3.One))
                                                                |]
                                                                [| t.text "Reset Element" |]
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", "tb-btn danger full")
                                                                    t.Events.onClick (fun s _ ->
                                                                        match s.SelectedId.Peek with
                                                                        | Some id when s.Engine.DeleteElement id ->
                                                                            s.SelectedId.Value <- None
                                                                            s.LastPickedWorld.Value <- None
                                                                            refreshView s (Some $"Deleted {id} from BIM model")
                                                                        | Some id -> s.StatusText.Value <- $"{id} is the primary foundation slab and is locked"
                                                                        | None -> ())
                                                                |]
                                                                [| t.text "Delete Selected" |]
                                                        |]
                                                |]
                                        |]
                                |]
                        |]

                    t.div [| t.Attrs.attribute ("class", "statusbar") |] [| t.text (fun s -> s.StatusText.Value) |]
                |])

let private stylesheetCss =
    """
    *, *::before, *::after {
        box-sizing: border-box;
        -webkit-user-select: none;
        user-select: none;
        -webkit-user-drag: none;
    }
    *::selection { background: transparent; color: inherit; }
    *::-webkit-selection { background: transparent; color: inherit; }
    html, body {
        margin: 0; padding: 0; width: 100%; height: 100%;
        background: #0b1017; color: #e2e8f0;
        font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
        font-size: 12.5px; overflow: hidden;
        -webkit-user-select: none;
        user-select: none;
    }
    .mount-root, .app-shell { display: flex; flex-direction: column; width: 100vw; height: 100vh; overflow: hidden; }
    .topbar {
        display: flex; align-items: center; justify-content: space-between;
        height: 46px; padding: 0 14px; gap: 10px;
        background: #111827; border-bottom: 1px solid #1f2937; flex-shrink: 0;
    }
    .brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 13.5px; color: #f8fafc; white-space: nowrap; }
    .brand-dot { width: 9px; height: 9px; border-radius: 999px; background: #38bdf8; }
    .gpu-pill {
        background: rgba(56, 189, 248, 0.14); border: 1px solid rgba(56, 189, 248, 0.40);
        color: #7dd3fc; font-size: 11px; font-weight: 600; padding: 2px 7px; border-radius: 999px;
    }
    .toolbar-group { display: flex; align-items: center; gap: 6px; flex-wrap: nowrap; }
    .render-stats { font-family: Consolas, 'Cascadia Mono', monospace; font-size: 11.5px; color: #94a3b8; white-space: nowrap; }
    .tb-btn, .sm-btn {
        background: #1e293b; color: #cbd5e1; border: 1px solid #334155;
        border-radius: 5px; padding: 5px 9px; font-size: 11.5px; cursor: pointer;
    }
    .tb-btn:hover, .sm-btn:hover { background: #283548; color: #ffffff; }
    .tb-btn.active, .sm-btn.active { background: #0284c7; border-color: #38bdf8; color: #ffffff; font-weight: 600; }
    .tb-btn.cut-active, .sm-btn.cut-active { background: #b45309; border-color: #fbbf24; color: #fffbeb; font-weight: 700; }
    .tb-btn.accent { background: #0f766e; border-color: #14b8a6; color: #f0fdfa; font-weight: 600; }
    .tb-btn.danger { background: #451a1a; border-color: #7f1d1d; color: #fecaca; }
    .tb-btn.full, .sm-btn.full { flex: 1; text-align: center; }
    .workspace { display: flex; flex: 1; min-height: 0; overflow: hidden; }
    .sidebar { display: flex; flex-direction: column; background: #0f172a; flex-shrink: 0; min-height: 0; }
    .left-pane { width: 240px; border-right: 1px solid #1e293b; }
    .right-pane { width: 300px; border-left: 1px solid #1e293b; }
    .pane-header {
        padding: 9px 12px; font-size: 11px; font-weight: 700; text-transform: uppercase;
        letter-spacing: 0.06em; color: #94a3b8; background: #111827; border-bottom: 1px solid #1e293b;
    }
    .tree-list { flex: 1; overflow-y: auto; padding: 6px; display: flex; flex-direction: column; gap: 3px; }
    .tree-row {
        display: flex; align-items: center; gap: 8px; width: 100%; text-align: left;
        padding: 6px 8px; background: transparent; border: 1px solid transparent;
        border-radius: 6px; color: #cbd5e1; cursor: pointer;
    }
    .tree-row:hover { background: #1e293b; }
    .tree-row.selected { background: #172554; border-color: #3b82f6; color: #ffffff; }
    .swatch { width: 11px; height: 11px; border-radius: 3px; flex-shrink: 0; border: 1px solid rgba(255,255,255,0.25); }
    .tree-meta { display: flex; flex-direction: column; gap: 1px; overflow: hidden; }
    .tree-title { font-size: 12px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .tree-sub { font-size: 10.5px; color: #94a3b8; }
    .viewport-wrap { position: relative; flex: 1; min-width: 0; min-height: 0; background: #0e1420; overflow: hidden; touch-action: none; }
    .bim-canvas { width: 100%; height: 100%; display: block; cursor: crosshair; touch-action: none; -webkit-user-select: none; user-select: none; -webkit-user-drag: none; }
    .projected-tag {
        position: absolute; transform: translate(-50%, -100%); flex-direction: column; gap: 2px;
        padding: 5px 9px; border-radius: 6px; background: rgba(15, 23, 42, 0.92);
        border: 1px solid #38bdf8; color: #f8fafc; pointer-events: none; white-space: nowrap;
    }
    .projected-title { font-size: 12px; font-weight: 700; color: #7dd3fc; }
    .projected-sub { font-size: 10.5px; font-family: Consolas, monospace; color: #cbd5e1; }
    .cut-banner {
        position: absolute; top: 12px; left: 12px; display: flex; align-items: center; gap: 7px;
        padding: 5px 10px; border-radius: 6px; background: rgba(69, 26, 3, 0.88);
        border: 1px solid #f59e0b; color: #fef3c7; font-size: 11.5px; font-weight: 700; pointer-events: none;
    }
    .cut-banner.hidden { display: none; }
    .cut-dot { width: 8px; height: 8px; border-radius: 999px; background: #fbbf24; }
    .viewport-hud {
        position: absolute; left: 12px; bottom: 10px; padding: 4px 9px; border-radius: 4px;
        background: rgba(15, 23, 42, 0.82); border: 1px solid #1e293b;
        font-size: 11px; color: #94a3b8; pointer-events: none;
    }
    .inspector-body { flex: 1; overflow-y: auto; padding: 9px; display: flex; flex-direction: column; gap: 8px; }
    .prop-card {
        background: #111827; border: 1px solid #1e293b; border-radius: 7px;
        padding: 9px; display: flex; flex-direction: column; gap: 6px;
    }
    .prop-card.cut-card { border-color: rgba(245, 158, 11, 0.42); }
    .cut-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
    .prop-row { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
    .prop-key { color: #94a3b8; font-size: 11.5px; }
    .prop-val { color: #e2e8f0; font-size: 11.5px; text-align: right; }
    .prop-val.strong { font-weight: 600; color: #f8fafc; }
    .prop-val.badge {
        background: #1e293b; border: 1px solid #334155; padding: 1px 6px;
        border-radius: 4px; font-family: Consolas, monospace; font-size: 11px;
    }
    .prop-val.mono { font-family: Consolas, monospace; font-size: 11px; }
    .prop-val.pick-val { color: #67e8f9; font-weight: 600; }
    .section-label { font-size: 10.5px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; color: #64748b; }
    .section-label.amber { color: #fbbf24; }
    .amber-txt { color: #fde68a; font-size: 11px; }
    .nudge-row { display: flex; align-items: center; gap: 5px; }
    .axis-tag {
        width: 32px; text-align: center; padding: 2px 0; border-radius: 4px;
        font-size: 10.5px; font-weight: 700; font-family: Consolas, monospace;
    }
    .axis-tag.x { background: rgba(248,113,113,0.18); color: #fca5a5; }
    .axis-tag.y { background: rgba(74,222,128,0.18); color: #86efac; }
    .axis-tag.z { background: rgba(96,165,250,0.18); color: #93c5fd; }
    .axis-tag.yaw { background: rgba(251,191,36,0.18); color: #fde68a; }
    .coord-readout {
        flex: 1; font-family: Consolas, monospace; font-size: 11.5px; color: #f1f5f9;
        overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .sm-btn { padding: 4px 6px; font-family: Consolas, monospace; font-size: 11px; }
    .action-row { display: flex; gap: 6px; margin-top: 2px; }
    .statusbar {
        height: 28px; padding: 0 14px; display: flex; align-items: center;
        background: #090d14; border-top: 1px solid #1e293b; font-size: 11.5px; color: #94a3b8; flex-shrink: 0;
    }
    """

type MountedBimSession =
    {
        State: AppState
        Engine: WgpuBimEngine
        Disposable: IDisposable
    }

let private mountSession (window: Window) : MountedBimSession =
    let host = window.Host
    let graph = window.Graph
    let styleEl = host.Document.createElement "style"
    styleEl.textContent <- stylesheetCss
    host.Document.head.appendChild styleEl |> ignore

    let container = host.Document.createElement "div"
    container.setAttribute ("class", "mount-root")
    host.Document.body.appendChild container |> ignore

    let engine = new WgpuBimEngine(window)
    let state =
        graph.Run(fun () ->
            {
                Engine = engine
                SelectedId = Api.createSignal (Some "C-101")
                LastPickedWorld = Api.createSignal None
                Interaction = Api.createSignal OrbitCamera
                Revision = Api.createSignal 0
                StatusText =
                    Api.createSignal
                        $"Ready on {engine.AdapterName} ({engine.BackendName}) · Click 3D IFC element to GPU-pick · Drag to orbit · Plan Cut L1 slices building live"
                CanvasEl = None
                Dragging = false; DragButton = 0s; DragShift = false; DragAlt = false
                DownX = 0.0; DownY = 0.0; LastX = 0.0; LastY = 0.0
            })

    let app = bimAppComponent state
    let mounted =
        Mounting.mount graph None (fun errors -> raise (AggregateException errors)) container None (fun ctx ->
            Program.run (Component.render app ()) ctx.Backend)

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                graph.Run(fun () -> mounted.Dispose())
                (engine :> IDisposable).Dispose()
                container.remove ()
                styleEl.remove ()
        }

    { State = state; Engine = engine; Disposable = cleanup }

module private PngEncoder =
    let private crcTable =
        Array.init 256 (fun n ->
            let mutable c = uint32 n
            for _ = 0 to 7 do
                c <- if (c &&& 1u) <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
            c)

    let private computeCrc32 (typeBytes: byte array) (data: byte array) =
        let mutable c = 0xFFFFFFFFu
        for b in typeBytes do c <- crcTable[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
        for b in data do c <- crcTable[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
        ~~~c

    let private writeChunk (stream: Stream) (chunkType: string) (data: byte array) =
        let lenBuf = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf.AsSpan(), uint32 data.Length)
        let typeBytes = Encoding.ASCII.GetBytes chunkType
        let crcBuf = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf.AsSpan(), computeCrc32 typeBytes data)
        stream.Write(lenBuf, 0, 4)
        stream.Write(typeBytes, 0, 4)
        if data.Length > 0 then stream.Write(data, 0, data.Length)
        stream.Write(crcBuf, 0, 4)

    /// Writes top-to-bottom RGBA8 pixels as a valid 32-bit PNG image using pure .NET `ZLibStream`.
    let saveRgbaPng (path: string) (width: int) (height: int) (rgba: byte array) =
        let dir = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory dir |> ignore
        use fs = File.Create path
        fs.Write([| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |], 0, 8)

        let ihdr = Array.zeroCreate<byte> 13
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), uint32 width)
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), uint32 height)
        ihdr[8] <- 8uy; ihdr[9] <- 6uy
        writeChunk fs "IHDR" ihdr

        use compressed = new MemoryStream()
        do
            use zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen = true)
            let rowStride = width * 4
            let scanline = Array.zeroCreate<byte> (1 + rowStride)
            for y = 0 to height - 1 do
                Buffer.BlockCopy(rgba, y * rowStride, scanline, 1, rowStride)
                let mutable aIdx = 4
                while aIdx <= rowStride do
                    scanline[aIdx] <- 255uy
                    aIdx <- aIdx + 4
                zlib.Write(scanline, 0, scanline.Length)

        writeChunk fs "IDAT" (compressed.ToArray())
        writeChunk fs "IEND" Array.empty<byte>

let private countDistinctColors (rgba: byte array) (strideBytes: int) =
    let seen = HashSet<uint32>()
    let mutable i = 0
    while i <= rgba.Length - 4 do
        seen.Add((uint32 rgba[i] <<< 24) ||| (uint32 rgba[i + 1] <<< 16) ||| (uint32 rgba[i + 2] <<< 8) ||| uint32 rgba[i + 3]) |> ignore
        i <- i + strideBytes
    seen.Count

let private defaultArtifactsDir () =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "artifacts"))

let private runSmokeCheck () =
    let swTotal = Stopwatch.StartNew()
    let artifactsDir =
        match Environment.GetEnvironmentVariable "RANVIER_CAPTURE_DIR" with
        | s when not (String.IsNullOrWhiteSpace s) -> Path.GetFullPath s
        | _ -> defaultArtifactsDir ()

    // 1) Hardware GPU WebCore + Ranvier.Wgpu zero-copy shared texture coexistence (NO CPU readback!)
    use window = Window.Create("Ranvier.View + Ranvier.Wgpu — Native BIM Viewer", 1360, 840, hidden = true)
    let session = mountSession window

    try
        let captureWindow () =
            window.Present()
            window.ReadPixels()

        for _ in 1 .. 14 do
            if not (window.Step(timeout = 16)) then failwith "Window closed unexpectedly during smoke test."

        let engine = session.Engine
        if engine.FramesDelivered < 1 then
            failwith $"Expected Ranvier.Wgpu to deliver >= 1 frame, got {engine.FramesDelivered}."

        if OperatingSystem.IsWindows() && window.IsGpuComposited then
            if not engine.IsZeroCopyGpu || window.CpuFramesPresented <> 0L || engine.LastMetrics.ReadbackMs <> 0.0 then
                failwith $"Expected zero-copy Dx12SharedTexture presentation (IsZeroCopyGpu={engine.IsZeroCopyGpu}, CpuFrames={window.CpuFramesPresented}, ReadbackMs={engine.LastMetrics.ReadbackMs})."

        // 2) Verify PickAtPixel at projected C-103 hits C-103
        let c103 = engine.Elements |> Seq.find (fun e -> e.Spec.Id = "C-103")
        let struct (px103, py103) =
            match engine.ProjectWorldToPixel c103.Pos with
            | ValueSome p -> p
            | ValueNone -> failwith "Expected SW column C-103 centroid to project inside viewport."

        let c103HitPos =
            match engine.PickAtPixel(px103, py103) with
            | Some(picked, hitPos) when picked.Spec.Id = "C-103" ->
                Console.WriteLine($"GPU pick C-103 verified: pixel ({px103}, {py103}) -> {picked.Spec.Id} ({picked.Spec.IfcClass}) at ({fmtF hitPos.X}, {fmtF hitPos.Y}, {fmtF hitPos.Z})m")
                hitPos
            | Some(other, _) -> failwith $"Expected GPU pick at ({px103}, {py103}) to hit C-103, got {other.Spec.Id}."
            | None -> failwith $"Expected GPU pick at ({px103}, {py103}) to hit C-103, got None."

        // 3) Verify picking at C-101 passes straight through active selection gizmos (PickId = 0u) to hit C-101!
        let c101 = engine.Elements |> Seq.find (fun e -> e.Spec.Id = "C-101")
        let struct (px101, py101) =
            match engine.ProjectWorldToPixel c101.Pos with
            | ValueSome p -> p
            | ValueNone -> failwith "Expected NW column C-101 centroid to project inside viewport."

        match engine.PickAtPixel(px101, py101) with
        | Some(picked, hitPos) when picked.Spec.Id = "C-101" ->
            Console.WriteLine($"Gizmo pass-through pick verified: pixel ({px101}, {py101}) -> {picked.Spec.Id} through active PickId=0u gizmos at ({fmtF hitPos.X}, {fmtF hitPos.Y}, {fmtF hitPos.Z})m")
        | Some(other, _) -> failwith $"Expected gizmo pass-through pick at ({px101}, {py101}) to hit C-101, got {other.Spec.Id}."
        | None -> failwith $"Expected gizmo pass-through pick at ({px101}, {py101}) to hit C-101, got None."

        // Populate foreground C-103 selection + exact GPU-picked 3D WorldPosition in inspector & capture bim-overview.png
        window.Graph.Run(fun () ->
            session.State.SelectedId.Value <- Some "C-103"
            session.State.LastPickedWorld.Value <- Some c103HitPos
            refreshView session.State (Some $"GPU Picked C-103 ({c103.Spec.Name}) at ({fmtF c103HitPos.X}, {fmtF c103HitPos.Y}, {fmtF c103HitPos.Z})m"))

        for _ in 1 .. 6 do window.Step(timeout = 16) |> ignore

        let struct (winW, winH, overviewWinPixels) = captureWindow ()
        let distinctWinColors = countDistinctColors overviewWinPixels 8

        if winW <= 0 || winH <= 0 || distinctWinColors <= 500 then
            failwith $"Expected > 500 distinct colors in window.ReadPixels ({winW}x{winH}), got {distinctWinColors}."

        let overviewPngPath = Path.Combine(artifactsDir, "bim-overview.png")
        PngEncoder.saveRgbaPng overviewPngPath winW winH overviewWinPixels

        // 4) Mutate C-101 transform and verify demand-driven re-render updates composited GPU pixels
        window.Graph.Run(fun () -> session.State.SelectedId.Value <- Some "C-101"; refreshView session.State None)
        for _ in 1 .. 2 do window.Step(timeout = 16) |> ignore
        let struct (_, _, beforeMoveBytes) = captureWindow ()
        let framesBeforeMove = engine.FramesDelivered
        c101.Pos <- Vector3(c101.Pos.X + 1.15f, c101.Pos.Y, c101.Pos.Z + 0.65f)
        c101.YawDeg <- 30.0f
        engine.RenderDemandDriven(Some "C-101")
        let struct (_, _, afterMoveBytes) = captureWindow ()

        let mutable transformChangedBytes = 0
        for i = 0 to beforeMoveBytes.Length - 1 do
            if beforeMoveBytes[i] <> afterMoveBytes[i] then transformChangedBytes <- transformChangedBytes + 1

        if engine.FramesDelivered <= framesBeforeMove || transformChangedBytes < 2_000 then
            failwith $"Expected transform mutation to re-render scene (changedBytes={transformChangedBytes})."

        c101.Pos <- c101.Spec.InitialPos
        c101.YawDeg <- c101.Spec.InitialYawDeg
        engine.RenderDemandDriven(Some "C-101")

        // 5) Verify activating WGSL BIM Section Cut (`Plan Cut L1` slicing off pitched roof & L2 floor deck!)
        let c105 = engine.Elements |> Seq.find (fun e -> e.Spec.Id = "C-105")
        let struct (pxInt, pyInt) =
            match engine.ProjectWorldToPixel(Vector3(c105.Pos.X, 1.15f, c105.Pos.Z)) with
            | ValueSome p -> p
            | ValueNone -> failwith "Expected interior column C-105 lower shaft to project inside viewport."

        let preCutPick = engine.PickAtPixel(pxInt, pyInt) |> Option.map (fun (el, _) -> el.Spec.Id) |> Option.defaultValue "None"
        if preCutPick = "C-105" then
            failwith "Expected interior column C-105 to be occluded by roof / L2 slab before activating Plan Cut L1."

        let struct (_, _, beforeCutBytes) = captureWindow ()
        window.Graph.Run(fun () ->
            engine.SetSectionMode SectionCutMode.PlanL1
            refreshView session.State (Some $"Activated WGSL {engine.SectionSummary} — roof & L2 deck sliced open"))
        for _ in 1 .. 4 do window.Step(timeout = 16) |> ignore
        let struct (_, _, afterCutBytes) = captureWindow ()

        let mutable cutChangedBytes = 0
        for i = 0 to beforeCutBytes.Length - 1 do
            if beforeCutBytes[i] <> afterCutBytes[i] then cutChangedBytes <- cutChangedBytes + 1

        if cutChangedBytes <= 50_000 then
            failwith $"Expected > 50,000 composited window bytes changed on Plan Cut L1, got {cutChangedBytes}."

        // Pick interior element C-105 exposed by the live WGSL section cut!
        let interiorHitPos =
            match engine.PickAtPixel(pxInt, pyInt) with
            | Some(picked, hitPos) when picked.Spec.Id = "C-105" ->
                Console.WriteLine($"Section-cut interior GPU pick verified: pixel ({pxInt}, {pyInt}) (was {preCutPick}) -> {picked.Spec.Id} ({picked.Spec.Name}) at ({fmtF hitPos.X}, {fmtF hitPos.Y}, {fmtF hitPos.Z})m")
                hitPos
            | Some(other, _) -> failwith $"Expected section-cut GPU pick at ({pxInt}, {pyInt}) to hit exposed interior C-105, got {other.Spec.Id}."
            | None -> failwith $"Expected section-cut GPU pick at ({pxInt}, {pyInt}) to hit exposed interior C-105, got None."

        window.Graph.Run(fun () ->
            session.State.SelectedId.Value <- Some "C-105"
            session.State.LastPickedWorld.Value <- Some interiorHitPos
            refreshView session.State (Some $"WGSL Plan Cut L1 (Y ≤ 2.20m) · GPU Picked interior C-105 at ({fmtF interiorHitPos.X}, {fmtF interiorHitPos.Y}, {fmtF interiorHitPos.Z})m"))

        for _ in 1 .. 6 do window.Step(timeout = 16) |> ignore

        let struct (_, _, sectionWinPixels) = captureWindow ()
        let sectionPngPath = Path.Combine(artifactsDir, "bim-section-cut.png")
        PngEncoder.saveRgbaPng sectionPngPath winW winH sectionWinPixels

        // 6) Verify 0 idle renders across 10 idle window.Step ticks
        let rendersBeforeIdle = engine.RenderCalls
        for _ in 1 .. 10 do window.Step(timeout = 8) |> ignore
        let idleRenders = engine.RenderCalls - rendersBeforeIdle
        if idleRenders <> 0 then
            failwith $"Expected 0 idle WgpuContext.Render calls across 10 idle ticks, got {idleRenders}."

        if OperatingSystem.IsWindows() && window.IsGpuComposited && window.CpuFramesPresented <> 0L then
            failwith $"Expected 0 software CPU frames during entire BIM session, got {window.CpuFramesPresented}."

        swTotal.Stop()
        let m = engine.LastMetrics
        let modeStr = engine.PresentationMode |> Option.map string |> Option.defaultValue "Unattached"
        Console.WriteLine(
            $"Smoke check PASSED in {swTotal.ElapsedMilliseconds}ms · GPU={engine.AdapterName} ({engine.BackendName}, mode={modeStr}) · "
            + $"window={winW}x{winH} (winColors={distinctWinColors}, gpuFrames={window.GpuFramesPresented}, cpuFrames={window.CpuFramesPresented}) · "
            + $"gpuRender={m.GpuRenderMs:F2}ms, readback={m.ReadbackMs:F2}ms · "
            + $"transformDeltaBytes={transformChangedBytes}, sectionCutDeltaBytes={cutChangedBytes}, idleRenders={idleRenders}"
        )
        Console.WriteLine($"Saved PNG artifacts:\n  - {overviewPngPath}\n  - {sectionPngPath}")
        window.Close()
    finally
        session.Disposable.Dispose()

[<EntryPoint>]
let main argv =
    let isSmoke =
        Environment.GetEnvironmentVariable "RANVIER_SMOKE" = "1"
        || (argv |> Array.exists (fun a -> a = "--smoke"))

    if isSmoke then
        runSmokeCheck ()
    else
        use window = Window.Create("Ranvier.View + Ranvier.Wgpu — Native BIM Viewer", 1360, 840, hidden = true)
        let session = mountSession window
        try
            window.Show()
            window.Present()
            let modeStr = session.Engine.PresentationMode |> Option.map string |> Option.defaultValue "Unattached"
            Console.WriteLine($"Ranvier.View + Ranvier.Wgpu BIM Viewer opened (1360x840) on {session.Engine.AdapterName} ({session.Engine.BackendName}, mode={modeStr}).")
            Console.Out.Flush()
            window.Run()
        finally
            session.Disposable.Dispose()

    0
