/// <summary>
/// The bytes the packing writers produce for the shared cases, for <c>packing.test.mjs</c> to compare with the
/// bytes of the .NET structs.
/// </summary>
module Ranvier.Wgpu.Tests.PackingChecks

open System
open Fable.Core
open Ranvier.Wgpu

/// <summary>A copy of the first <c>count</c> bytes of a staging buffer.</summary>
let private written (buffer: PackBuffer) (count: int) : JS.Uint8Array =
    (JS.Constructors.Uint8Array.Create (buffer.Buffer, 0, count)).slice()

// Each buffer starts smaller than what is written, so the writers also grow it.

let vertices () : JS.Uint8Array =
    let buffer = Packing.create 16
    written buffer (Packing.vertices buffer (ReadOnlySpan<Vertex3D>(PackingCases.vertices)))

let instances () : JS.Uint8Array =
    let buffer = Packing.create 16
    written buffer (Packing.instances buffer (ReadOnlySpan<Instance3D>(PackingCases.instances)))

/// <summary>The single-instance writer, for each case in turn.</summary>
let instancesOneByOne () : JS.Uint8Array array =
    let buffer = Packing.create 16

    PackingCases.instances
    |> Array.map (fun instance -> written buffer (Packing.instance buffer instance))

let lineVertices () : JS.Uint8Array =
    let buffer = Packing.create 16
    let even = PackingCases.lineVertices.Length &&& ~~~1
    written buffer (Packing.lineVertices buffer (ReadOnlySpan<LineVertex3D>(PackingCases.lineVertices)) even)

let discs () : JS.Uint8Array =
    let buffer = Packing.create 16
    written buffer (Packing.discs buffer (ReadOnlySpan<DiscInstance3D>(PackingCases.discs)))

let userUniforms () : JS.Uint8Array =
    let values = JS.Constructors.Float32Array.Create Packing.UserUniformFloats
    Packing.userUniforms values (ReadOnlySpan<Numerics.Vector4>(PackingCases.userUniforms))
    JS.Constructors.Uint8Array.Create (values.buffer, 0, PackingCases.userUniforms.Length * 16)

/// <summary>The <c>Vector3.Normalize</c> cases, three <c>float32</c> each.</summary>
let normalized () : JS.Uint8Array =
    let vectors = PackingCases.normalized ()
    let values = JS.Constructors.Float32Array.Create (vectors.Length * 3)

    vectors
    |> Array.iteri (fun i v ->
        values[i * 3] <- v.X
        values[i * 3 + 1] <- v.Y
        values[i * 3 + 2] <- v.Z)

    JS.Constructors.Uint8Array.Create values.buffer

/// <summary>The scene uniform block; its first 64 bytes are <c>Camera3D.viewProjMatrix</c>.</summary>
let sceneUniforms () : JS.Uint8Array =
    let values = JS.Constructors.Float32Array.Create Packing.SceneUniformFloats
    Packing.sceneUniforms values PackingCases.Width PackingCases.Height PackingCases.camera PackingCases.lighting PackingCases.section
    JS.Constructors.Uint8Array.Create values.buffer

/// <summary>The shared file's own listing of the scene uniform block, computed in JavaScript.</summary>
let sceneUniformValues () : JS.Uint8Array =
    let values =
        JS.Constructors.Float32Array.Create (box (PackingCases.sceneUniformValues ()))

    JS.Constructors.Uint8Array.Create values.buffer
