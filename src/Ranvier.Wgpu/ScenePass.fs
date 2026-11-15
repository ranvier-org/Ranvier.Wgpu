namespace Ranvier.Wgpu

open System

/// <summary>
/// Zero-allocation value description of a 3D scene pass (`Camera`, optional `ClearColor` / `Lighting` / `SectionPlane`,
/// and contiguous `ReadOnlyMemory` slices of `InstanceBatch`, `LineBatch`, and `DiscBatch`).
/// </summary>
[<Struct; NoEquality; NoComparison>]
type ScenePass =
    {
        Camera: Camera3D
        ClearColor: RgbaColor voption
        Lighting: LightingRig voption
        SectionPlane: SectionPlane voption
        Batches: ReadOnlyMemory<InstanceBatch>
        LineBatches: ReadOnlyMemory<LineBatch>
        DiscBatches: ReadOnlyMemory<DiscBatch>
    }

    override _.ToString() =
        "ScenePass"

/// <summary>Pipeable zero-allocation builders for <see cref="T:Ranvier.Wgpu.ScenePass"/>.</summary>
module ScenePass =
    /// <summary>Creates an empty `ScenePass` for `camera` using default backdrop, studio lighting, and no section cut.</summary>
    let inline create (camera: Camera3D) : ScenePass =
        {
            Camera = camera
            ClearColor = ValueNone
            Lighting = ValueNone
            SectionPlane = ValueNone
            Batches = ReadOnlyMemory.Empty
            LineBatches = ReadOnlyMemory.Empty
            DiscBatches = ReadOnlyMemory.Empty
        }

    /// <summary>Sets the color-attachment clear color for `pass`.</summary>
    let inline withClear (color: RgbaColor) (pass: ScenePass) : ScenePass =
        { pass with
            ClearColor = ValueSome color
        }

    /// <summary>Sets the hemisphere + directional `LightingRig` for `pass`.</summary>
    let inline withLighting (lighting: LightingRig) (pass: ScenePass) : ScenePass =
        { pass with
            Lighting = ValueSome lighting
        }

    /// <summary>Sets the world-space `SectionPlane` clipping state for `pass`.</summary>
    let inline withSection (plane: SectionPlane) (pass: ScenePass) : ScenePass =
        { pass with
            SectionPlane = ValueSome plane
        }

    /// <summary>Attaches an array of `InstanceBatch` draw batches to `pass` without copying.</summary>
    let inline withBatches (batches: InstanceBatch array) (pass: ScenePass) : ScenePass =
        { pass with
            Batches = ReadOnlyMemory<InstanceBatch>(batches)
        }

    /// <summary>Attaches a `ReadOnlyMemory` slice of `InstanceBatch` draw batches to `pass`.</summary>
    let inline withBatchMemory (batches: ReadOnlyMemory<InstanceBatch>) (pass: ScenePass) : ScenePass =
        { pass with Batches = batches }

    /// <summary>Attaches an array of `LineBatch` hairline batches to `pass` without copying.</summary>
    let inline withLines (lines: LineBatch array) (pass: ScenePass) : ScenePass =
        { pass with
            LineBatches = ReadOnlyMemory<LineBatch>(lines)
        }

    /// <summary>Attaches a `ReadOnlyMemory` slice of `LineBatch` hairline batches to `pass`.</summary>
    let inline withLineMemory (lines: ReadOnlyMemory<LineBatch>) (pass: ScenePass) : ScenePass =
        { pass with LineBatches = lines }

    /// <summary>Attaches an array of `DiscBatch` billboard batches to `pass` without copying.</summary>
    let inline withDiscs (discs: DiscBatch array) (pass: ScenePass) : ScenePass =
        { pass with
            DiscBatches = ReadOnlyMemory<DiscBatch>(discs)
        }

    /// <summary>Attaches a `ReadOnlyMemory` slice of `DiscBatch` billboard batches to `pass`.</summary>
    let inline withDiscMemory (discs: ReadOnlyMemory<DiscBatch>) (pass: ScenePass) : ScenePass =
        { pass with DiscBatches = discs }
