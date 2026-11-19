namespace Ranvier.Wgpu

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions

/// <summary>
/// Raised when WGSL given to a <c>WgpuContext</c> does not compile, lacks a required entry point, or does not fit
/// the scene pipelines. The message is the WGSL compiler's or the pipeline validator's own text.
/// </summary>
[<Sealed>]
type WgslException(message: string) =
    inherit Exception(message)

/// <summary>
/// Named places in the stock scene shader where <c>Shaders.composeScene</c> inserts a consumer's WGSL.
/// Each case lists the names that are in scope at that place.
/// </summary>
[<RequireQualifiedAccess; Struct>]
type SceneShaderHook =
    /// <summary>
    /// Module scope, before <c>vs_main</c>: functions, structs and constants. In scope: the scene uniforms
    /// <c>u</c>, the user uniforms <c>user</c>, <c>VsIn</c> and <c>VsOut</c>.
    /// </summary>
    | Declarations
    /// <summary>
    /// Extra members of <c>VsOut</c>, one per line and each ending in a comma. The stock members use locations
    /// 0 to 4, so start at <c>@location(5)</c>.
    /// </summary>
    | Varyings
    /// <summary>
    /// First statements of <c>vs_main</c>. In scope: <c>in: VsIn</c>. The code may return a complete
    /// <c>VsOut</c> to replace the stock vertex stage for some instances.
    /// </summary>
    | VertexBegin
    /// <summary>
    /// Last statements of <c>vs_main</c>, before <c>return out;</c>. In scope: <c>in</c>, <c>model</c>,
    /// <c>worldPos4</c>, <c>worldNormal</c>, the model's axes <c>c0</c>, <c>c1</c>, <c>c2</c>, and the mutable
    /// <c>out: VsOut</c>. Assign the members added through <c>Varyings</c> here.
    /// </summary>
    | VertexEnd
    /// <summary>
    /// Statements of <c>fs_main</c> that run after section-plane clipping and before the stock shading. In
    /// scope: <c>in: VsOut</c>, <c>isFront</c>, <c>flags</c>. The code may return a premultiplied colour or
    /// <c>discard</c>.
    /// </summary>
    | FragmentBegin
    /// <summary>
    /// Statements of <c>fs_main</c> that run after the stock lighting and before the section contour and tone
    /// mapping. In scope: everything from <c>FragmentBegin</c>, <c>alpha</c>, and the mutable linear colour
    /// <c>lit</c>.
    /// </summary>
    | FragmentLit

    override this.ToString() =
        match this with
        | Declarations -> "Declarations"
        | Varyings -> "Varyings"
        | VertexBegin -> "VertexBegin"
        | VertexEnd -> "VertexEnd"
        | FragmentBegin -> "FragmentBegin"
        | FragmentLit -> "FragmentLit"

/// <summary>Which pipelines of a context take their entry points from custom WGSL rather than the stock shader.</summary>
[<Struct>]
type internal SceneEntryPlan =
    {
        CustomPick: bool
        CustomLines: bool
        CustomDiscs: bool
        CustomDiscPick: bool
    }

    override _.ToString() =
        "SceneEntryPlan"

/// <summary>Built-in runtime-compiled WGSL scene, hairline, billboard-disc, and GPU-picking shaders.</summary>
module Shaders =
    /// <summary>
    /// Number of <c>vec4&lt;f32&gt;</c> slots in the user uniform block bound at <c>@group(0) @binding(1)</c>
    /// and written with <c>WgpuContext.SetUserUniforms</c>.
    /// </summary>
    [<Literal>]
    let UserUniformSlots = 16

    // The stock shader is kept as the segments between its extension points. `DefaultSceneWgsl` joins them with
    // nothing in between, and `composeScene` joins them with the consumer's code.

    [<Literal>]
    let private SceneHead =
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

// Application data written with WgpuContext.SetUserUniforms. Every slot is zero until it is written.
struct UserUniforms {
    slots: array<vec4<f32>, 16>,
};

@group(0) @binding(1)
var<uniform> user: UserUniforms;

struct VsIn {
    @location(0) localPos: vec3<f32>,
    @location(1) localNormal: vec3<f32>,
    @location(2) vertexColor: vec4<f32>,
    @location(3) modelCol0: vec4<f32>,
    @location(4) modelCol1: vec4<f32>,
    @location(5) modelCol2: vec4<f32>,
    @location(6) modelCol3: vec4<f32>,
    @location(7) colorRoughness: vec4<f32>,
    @location(8) pickFlags: vec2<u32>,
    @location(9) opacityEmissive: vec2<f32>,
};

struct VsOut {
    @builtin(position) clipPos: vec4<f32>,
    @location(0) worldPos: vec3<f32>,
    @location(1) worldNormal: vec3<f32>,
    @location(2) colorRoughness: vec4<f32>,
    @location(3) opacityEmissive: vec2<f32>,
    @location(4) @interpolate(flat) pickFlags: vec2<u32>,
"""

    [<Literal>]
    let private SceneAfterVaryings =
        """};

"""

    [<Literal>]
    let private SceneVertexOpen =
        """@vertex
fn vs_main(in: VsIn) -> VsOut {
"""

    [<Literal>]
    let private SceneVertexBody =
        """    let model = mat4x4<f32>(in.modelCol0, in.modelCol1, in.modelCol2, in.modelCol3);
    let worldPos4 = model * vec4<f32>(in.localPos, 1.0);

    // Exact inverse-transpose normal via 3x3 cofactor matrix (handles non-uniform BIM scales + rotations)
    let c0 = in.modelCol0.xyz;
    let c1 = in.modelCol1.xyz;
    let c2 = in.modelCol2.xyz;
    let adj0 = cross(c1, c2);
    let adj1 = cross(c2, c0);
    let adj2 = cross(c0, c1);
    let detSign = select(1.0, -1.0, dot(c0, adj0) < 0.0);
    let rawN = (adj0 * in.localNormal.x + adj1 * in.localNormal.y + adj2 * in.localNormal.z) * detSign;
    let nLenSq = dot(rawN, rawN);
    let worldNormal = select(vec3<f32>(0.0, 1.0, 0.0), rawN * inverseSqrt(max(nLenSq, 1e-12)), nLenSq > 1e-12);

    var out: VsOut;
    out.clipPos = u.viewProj * worldPos4;
    out.worldPos = worldPos4.xyz;
    out.worldNormal = worldNormal;
    out.colorRoughness = vec4<f32>(in.vertexColor.rgb * in.colorRoughness.rgb, in.colorRoughness.a);
    out.opacityEmissive = vec2<f32>(in.vertexColor.a * in.opacityEmissive.x, in.opacityEmissive.y);
    out.pickFlags = in.pickFlags;
"""

    [<Literal>]
    let private SceneFragmentOpen =
        """    return out;
}

@fragment
fn fs_main(in: VsOut, @builtin(front_facing) isFront: bool) -> @location(0) vec4<f32> {
    let flags = in.pickFlags.y;
    let ignoreClip = (flags & 4u) != 0u;
    let sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    let clipDist = dot(in.worldPos, u.clipPlane.xyz) + u.clipPlane.w;
    if (sectionActive && clipDist < 0.0) {
        discard;
    }

"""

    [<Literal>]
    let private SceneFragmentShading =
        """    let alpha = clamp(in.opacityEmissive.x, 0.0, 1.0);
    if (alpha <= 0.002) {
        discard;
    }

    let isUnlit = (flags & 1u) != 0u;
    let isSelected = (flags & 2u) != 0u;

    var lit: vec3<f32>;
    if (isUnlit) {
        lit = in.colorRoughness.rgb * (1.0 + in.opacityEmissive.y);
        if (isSelected) {
            lit = mix(lit, vec3<f32>(0.45, 0.96, 1.0), 0.45) + vec3<f32>(0.15, 0.35, 0.45);
        }
    } else {
        let V = normalize(u.camPos.xyz - in.worldPos);
        let rawN = normalize(in.worldNormal);
        let N = select(-rawN, rawN, isFront);

        let baseAlbedo = select(in.colorRoughness.rgb, mix(in.colorRoughness.rgb, vec3<f32>(0.38, 0.94, 1.0), 0.52), isSelected);
        let albedo = select(baseAlbedo * vec3<f32>(0.78, 0.72, 0.68), baseAlbedo, isFront);
        let roughness = clamp(in.colorRoughness.a, 0.06, 0.96);

        let hemi = mix(u.groundColor.xyz, u.skyColor.xyz, N.y * 0.5 + 0.5);

        let L1 = normalize(u.keyLight.xyz);
        let ndotl1 = max(dot(N, L1), 0.0);
        let H1 = normalize(L1 + V);
        let shininess = mix(112.0, 10.0, roughness);
        let specNorm = (shininess + 8.0) / 25.1327;
        let spec1 = pow(max(dot(N, H1), 0.0), shininess) * specNorm * ndotl1 * (1.0 - roughness * 0.62);

        let L2 = normalize(u.rimLight.xyz);
        let ndotl2 = max(dot(N, L2), 0.0);
        let fresnel = pow(1.0 - max(dot(N, V), 0.0), 3.2);

        lit =
            albedo * (hemi + vec3<f32>(1.0, 0.97, 0.91) * (ndotl1 * u.keyLight.w) + vec3<f32>(0.42, 0.70, 1.0) * (ndotl2 * u.rimLight.w))
            + vec3<f32>(1.0, 0.98, 0.93) * (spec1 * 0.38)
            + vec3<f32>(0.26, 0.56, 0.92) * (fresnel * 0.18)
            + baseAlbedo * in.opacityEmissive.y;

        if (isSelected) {
            lit = lit + vec3<f32>(0.14, 0.38, 0.52) * (0.45 + fresnel * 0.85);
        }
    }

"""

    [<Literal>]
    let private SceneTail =
        """    if (sectionActive && u.sectionStyle.w > 0.0 && clipDist < u.sectionStyle.w) {
        let edgeT = 1.0 - (clipDist / u.sectionStyle.w);
        lit = mix(lit, u.sectionStyle.xyz, edgeT * 0.88);
    }

    let exactColor = (flags & 8u) != 0u;
    if (exactColor) {
        return vec4<f32>(clamp(lit, vec3<f32>(0.0), vec3<f32>(1.0)) * alpha, alpha);
    }

    let mapped = lit / (lit + vec3<f32>(0.86));
    let gamma = pow(clamp(mapped * 1.36, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(1.0 / 2.15));
    return vec4<f32>(gamma * alpha, alpha);
}

@fragment
fn fs_pick(in: VsOut) -> @location(0) vec4<u32> {
    let pickId = in.pickFlags.x;
    if (pickId == 0u) {
        discard;
    }
    if (clamp(in.opacityEmissive.x, 0.0, 1.0) <= 0.002) {
        discard;
    }
    let flags = in.pickFlags.y;
    let ignoreClip = (flags & 4u) != 0u;
    let sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    let clipDist = dot(in.worldPos, u.clipPlane.xyz) + u.clipPlane.w;
    if (sectionActive && clipDist < 0.0) {
        discard;
    }
    return vec4<u32>(
        bitcast<u32>(in.worldPos.x),
        bitcast<u32>(in.worldPos.y),
        bitcast<u32>(in.worldPos.z),
        pickId
    );
}

// ============================================================================
// 3D Unlit Alpha-Blended Hairline Pipeline (`LineVertex3D`, `LineList`)
// ============================================================================

struct LineVsIn {
    @location(0) posPad: vec4<f32>,
    @location(1) color: vec4<f32>,
};

struct LineVsOut {
    @builtin(position) clipPos: vec4<f32>,
    @location(0) worldPosPad: vec4<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_line(in: LineVsIn) -> LineVsOut {
    var out: LineVsOut;
    let wp = in.posPad.xyz;
    out.clipPos = u.viewProj * vec4<f32>(wp, 1.0);
    out.worldPosPad = in.posPad;
    out.color = in.color;
    return out;
}

@fragment
fn fs_line(in: LineVsOut) -> @location(0) vec4<f32> {
    let ignoreClip = in.worldPosPad.w > 0.5;
    let sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    if (sectionActive && (dot(in.worldPosPad.xyz, u.clipPlane.xyz) + u.clipPlane.w) < 0.0) {
        discard;
    }
    if (in.color.a <= 0.002) {
        discard;
    }
    let lineAlpha = clamp(in.color.a, 0.0, 1.0);
    return vec4<f32>(in.color.rgb * lineAlpha, lineAlpha);
}

// ============================================================================
// Camera-Facing SDF Disc + Segmented Verification Arc Pipeline (`DiscInstance3D`)
// ============================================================================

struct DiscVsIn {
    @builtin(vertex_index) vid: u32,
    @location(0) centerRadiusPx: vec4<f32>,
    @location(1) fillColor: vec4<f32>,
    @location(2) arcParams: vec4<f32>,
    @location(3) pickFlags: vec2<u32>,
    @location(4) gapTrack: vec2<f32>,
};

struct DiscVsOut {
    @builtin(position) clipPos: vec4<f32>,
    @location(0) worldPos: vec3<f32>,
    @location(1) localPx: vec2<f32>,
    @location(2) fillColor: vec4<f32>,
    @location(3) arcParams: vec4<f32>,
    @location(4) @interpolate(flat) pickFlags: vec2<u32>,
    @location(5) radiusGapTrack: vec3<f32>,
};

@vertex
fn vs_disc(in: DiscVsIn) -> DiscVsOut {
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 1.0, -1.0),
        vec2<f32>( 1.0,  1.0),
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 1.0,  1.0),
        vec2<f32>(-1.0,  1.0)
    );
    let corner = corners[in.vid % 6u];
    let worldPos = in.centerRadiusPx.xyz;
    let radiusPx = max(in.centerRadiusPx.w, 0.5);
    let arcThick = max(in.arcParams.z, 0.0);
    let isSelected = (in.pickFlags.y & 2u) != 0u;
    let extraMargin =
        select(0.0, in.gapTrack.x + arcThick + 1.5, arcThick > 0.01)
        + select(0.0, 6.0, isSelected)
        + 1.75;
    let quadHalfPx = radiusPx + extraMargin;

    let vpW = max(u.skyColor.w, 1.0);
    let vpH = max(u.groundColor.w, 1.0);
    let clipCenter = u.viewProj * vec4<f32>(worldPos, 1.0);

    var out: DiscVsOut;
    if (clipCenter.w <= 0.0001) {
        out.clipPos = vec4<f32>(2.0, 2.0, 2.0, 1.0);
    } else {
        let ndcOffset = vec2<f32>(
            corner.x * (2.0 * quadHalfPx / vpW),
            corner.y * (2.0 * quadHalfPx / vpH)
        );
        out.clipPos = vec4<f32>(
            clipCenter.xy + ndcOffset * clipCenter.w,
            clipCenter.z,
            clipCenter.w
        );
    }
    out.worldPos = worldPos;
    out.localPx = vec2<f32>(corner.x * quadHalfPx, -corner.y * quadHalfPx);
    out.fillColor = in.fillColor;
    out.arcParams = in.arcParams;
    out.pickFlags = in.pickFlags;
    out.radiusGapTrack = vec3<f32>(radiusPx, in.gapTrack.x, in.gapTrack.y);
    return out;
}

@fragment
fn fs_disc(in: DiscVsOut) -> @location(0) vec4<f32> {
    let flags = in.pickFlags.y;
    let ignoreClip = (flags & 4u) != 0u;
    let sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    if (sectionActive && (dot(in.worldPos, u.clipPlane.xyz) + u.clipPlane.w) < 0.0) {
        discard;
    }

    let r = length(in.localPx);
    let discR = in.radiusGapTrack.x;
    let gapPx = in.radiusGapTrack.y;
    let trackAlpha = in.radiusGapTrack.z;
    let verifiedFrac = clamp(in.arcParams.x, 0.0, 1.0);
    let inProgFrac = clamp(in.arcParams.y, 0.0, 1.0 - verifiedFrac);
    let arcThick = in.arcParams.z;
    let portalHollow = clamp(in.arcParams.w, 0.0, 1.0);
    let isSelected = (flags & 2u) != 0u;

    let aa = 0.72;
    let discMask = 1.0 - smoothstep(discR - aa, discR + aa, r);

    // Optional mid-zoom hollow portal (`glibc`, `linux` revealing inner module constellations)
    var discRgb = in.fillColor.rgb;
    var discAlpha = in.fillColor.a * discMask;
    if (portalHollow > 0.005 && discMask > 0.001) {
        let rimWidth = clamp(discR * 0.09, 1.35, 2.4);
        let innerMask = 1.0 - smoothstep(discR - rimWidth - aa, discR - rimWidth + aa, r);
        let portalBg = vec3<f32>(0.035, 0.055, 0.095);
        discRgb = mix(discRgb, mix(discRgb, portalBg, innerMask * 0.92), portalHollow);
        let portalCoreAlpha = mix(in.fillColor.a, in.fillColor.a * 0.52, portalHollow);
        let rimBoost = 1.0 - innerMask;
        discAlpha = discMask * mix(portalCoreAlpha, max(in.fillColor.a, 0.88), rimBoost * portalHollow);
    }

    var outRgb = discRgb;
    var outAlpha = discAlpha;

    // Concentric clockwise verification progress arc starting at 12 o'clock
    if (arcThick > 0.05) {
        let arcR = discR + gapPx;
        let halfT = arcThick * 0.5;
        let ringMask = 1.0 - smoothstep(halfT - 0.62, halfT + 0.62, abs(r - arcR));
        if (ringMask > 0.004) {
            let rawAng = atan2(in.localPx.x, -in.localPx.y);
            let cwAng = select(rawAng, rawAng + 6.283185307, rawAng < 0.0);
            let frac = cwAng * 0.159154943;

            let greenCol = vec3<f32>(0.149, 0.816, 0.486); // #26d07c
            let amberCol = vec3<f32>(0.984, 0.573, 0.235); // #fb923c
            let slateCol = vec3<f32>(0.200, 0.255, 0.333); // #334155

            var segCol = slateCol;
            var segAlpha = trackAlpha * in.fillColor.a;
            if (verifiedFrac > 0.001 && frac <= verifiedFrac) {
                segCol = greenCol;
                segAlpha = min(1.0, in.fillColor.a * 1.15);
            } else if (inProgFrac > 0.001 && frac <= (verifiedFrac + inProgFrac)) {
                segCol = amberCol;
                segAlpha = min(1.0, in.fillColor.a * 1.12);
            }
            let a = ringMask * segAlpha;
            let combA = a + outAlpha * (1.0 - a);
            if (combA > 0.0001) {
                outRgb = (segCol * a + outRgb * outAlpha * (1.0 - a)) / combA;
                outAlpha = combA;
            }
        }
    }

    // Selection halo ring
    if (isSelected) {
        let selR = discR + select(2.8, gapPx + arcThick + 2.6, arcThick > 0.05);
        let selMask = 1.0 - smoothstep(0.6, 1.9, abs(r - selR));
        if (selMask > 0.004) {
            let selCol = vec3<f32>(0.220, 0.741, 0.973); // #38bdf8
            let a = selMask * 0.92;
            let combA = a + outAlpha * (1.0 - a);
            outRgb = (selCol * a + outRgb * outAlpha * (1.0 - a)) / combA;
            outAlpha = combA;
        }
    }

    if (outAlpha <= 0.005) {
        discard;
    }
    let clampedAlpha = clamp(outAlpha, 0.0, 1.0);
    return vec4<f32>(outRgb * clampedAlpha, clampedAlpha);
}

@fragment
fn fs_disc_pick(in: DiscVsOut) -> @location(0) vec4<u32> {
    let pickId = in.pickFlags.x;
    if (pickId == 0u) {
        discard;
    }
    let flags = in.pickFlags.y;
    if (in.fillColor.a <= 0.005 && (flags & 2u) == 0u) {
        discard;
    }
    let ignoreClip = (flags & 4u) != 0u;
    let sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    if (sectionActive && (dot(in.worldPos, u.clipPlane.xyz) + u.clipPlane.w) < 0.0) {
        discard;
    }
    let r = length(in.localPx);
    let hitR = in.radiusGapTrack.x + select(1.5, in.radiusGapTrack.y + in.arcParams.z, in.arcParams.z > 0.05);
    if (r > hitR) {
        discard;
    }
    return vec4<u32>(
        bitcast<u32>(in.worldPos.x),
        bitcast<u32>(in.worldPos.y),
        bitcast<u32>(in.worldPos.z),
        pickId
    );
}
"""

    /// <summary>
    /// Default WGSL shader module emitting premultiplied sRGB color attachments (`fs_main`, `fs_line`, `fs_disc`)
    /// plus `RGBA32Uint` 1x1 hardware picking (`fs_pick`, `fs_disc_pick`).
    /// </summary>
    /// <remarks>
    /// To change the shading, pass code for the named extension points to <c>composeScene</c> rather than
    /// editing this text: the text may change between versions, and the extension points keep their names.
    /// </remarks>
    [<Literal>]
    let DefaultSceneWgsl =
        SceneHead
        + SceneAfterVaryings
        + SceneVertexOpen
        + SceneVertexBody
        + SceneFragmentOpen
        + SceneFragmentShading
        + SceneTail

    /// <summary>
    /// Builds the stock scene shader with a consumer's WGSL inserted at named extension points, ready to pass
    /// as the custom WGSL of a <c>WgpuContext</c>.
    /// </summary>
    /// <remarks>
    /// Code given twice for one hook is inserted in the order given. With no hooks the result is
    /// <c>DefaultSceneWgsl</c>. The line and disc shaders are not touched, so line and disc batches draw as they
    /// do on a stock context. <c>WgpuContext.ValidateWgsl</c> checks the result without opening a window.
    /// </remarks>
    /// <exception cref="T:System.ArgumentNullException">A hook's code is null.</exception>
    let composeScene (hooks: seq<SceneShaderHook * string>) : string =
        let given = Seq.toArray hooks
        let text = StringBuilder (DefaultSceneWgsl.Length + 1024)

        let insert (hook: SceneShaderHook) =
            for (target, code) in given do
                if target = hook then
                    if isNull code then
                        nullArg (nameof hooks)

                    text.Append (code) |> ignore

                    if code.Length > 0 && not (code.EndsWith '\n') then
                        text.Append ('\n') |> ignore

        text.Append (SceneHead) |> ignore
        insert SceneShaderHook.Varyings
        text.Append (SceneAfterVaryings) |> ignore
        insert SceneShaderHook.Declarations
        text.Append (SceneVertexOpen) |> ignore
        insert SceneShaderHook.VertexBegin
        text.Append (SceneVertexBody) |> ignore
        insert SceneShaderHook.VertexEnd
        text.Append (SceneFragmentOpen) |> ignore
        insert SceneShaderHook.FragmentBegin
        text.Append (SceneFragmentShading) |> ignore
        insert SceneShaderHook.FragmentLit
        text.Append (SceneTail) |> ignore
        text.ToString ()

    let private commentPattern =
        Regex (@"//[^\n]*|/\*[\s\S]*?\*/", RegexOptions.CultureInvariant)

    let private functionPattern =
        Regex (@"\bfn\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)

    /// <summary>
    /// Decides, from the functions custom WGSL defines, which pipelines use it and which fall back to the stock
    /// shader. Returns the reason when the set of entry points cannot make a working context.
    /// </summary>
    let internal planSceneEntryPoints (wgsl: string) : Result<SceneEntryPlan, string> =
        let names = HashSet<string>(StringComparer.Ordinal)

        for m in functionPattern.Matches (commentPattern.Replace (wgsl, " ")) do
            names.Add m.Groups[1].Value |> ignore

        let has (name: string) =
            names.Contains name

        let missingRequired =
            [ "vs_main"; "fs_main" ]
            |> List.filter (has >> not)

        let halfPair (vertex: string) (fragment: string) (batches: string) =
            if has vertex = has fragment then
                None
            else
                let present, absent = if has vertex then vertex, fragment else fragment, vertex

                Some
                    $"Custom WGSL defines {present} but not {absent}. Define both to draw {batches} batches with your own shader, or neither to keep the built-in {batches} shader."

        if not missingRequired.IsEmpty then
            let missing = String.Join (" and ", missingRequired)
            Error $"Custom WGSL must define the entry points vs_main and fs_main. Missing: {missing}."
        else
            match halfPair "vs_line" "fs_line" "line", halfPair "vs_disc" "fs_disc" "disc" with
            | Some reason, _
            | None, Some reason -> Error reason
            | None, None when has "fs_disc_pick" && not (has "vs_disc") ->
                Error
                    "Custom WGSL defines fs_disc_pick but not vs_disc and fs_disc. Define all three, or leave fs_disc_pick out to keep the built-in disc picking shader."
            | None, None ->
                Ok
                    {
                        CustomPick = has "fs_pick"
                        CustomLines = has "vs_line"
                        CustomDiscs = has "vs_disc"
                        CustomDiscPick = has "vs_disc" && has "fs_disc_pick"
                    }
