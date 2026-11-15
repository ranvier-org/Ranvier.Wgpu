namespace Ranvier.Wgpu

open System
open System.Text
open System.Text.RegularExpressions

/// <summary>
/// Raised when GLSL given to a <c>WgpuContext</c> that draws with WebGL2 does not compile, lacks a required
/// entry point, or does not fit the scene pipelines, and when such a context is given custom WGSL without
/// GLSL. The message is the driver's compiler or linker log, or says what is missing.
/// </summary>
[<Sealed>]
type GlslException(message: string) =
    inherit Exception(message)

/// <summary>
/// The stock scene, hairline, billboard-disc and GPU-picking shaders in GLSL ES 3.00, for a context that draws
/// with WebGL2: a port by hand of <c>Shaders.DefaultSceneWgsl</c>.
/// </summary>
/// <remarks>
/// <para>
/// The port keeps the WGSL's names, order and maths, so a change to one can be made in the other. The two
/// languages differ in these ways:
/// </para>
/// <list type="bullet">
/// <item><description>
/// One text holds every entry point. A context compiles it once for each, and puts in front of it
/// <c>#version 300 es</c>, <c>highp</c> as the default precision of <c>float</c> and <c>int</c>, and two
/// macros: the entry point's name in capitals (<c>VS_MAIN</c>, <c>FS_MAIN</c>, <c>FS_PICK</c>,
/// <c>VS_LINE</c>, <c>FS_LINE</c>, <c>VS_DISC</c>, <c>FS_DISC</c>, <c>FS_DISC_PICK</c>) and <c>VARYING</c>,
/// which is <c>out</c> in a vertex stage and <c>in</c> in a fragment stage. Each entry point and what only it
/// uses sits inside <c>#ifdef</c> of its macro. The text has no <c>#version</c> line and no <c>main</c>, and
/// a compiler's message names a line of the text as an editor counts it.
/// </description></item>
/// <item><description>
/// An entry point is a function with the WGSL name. A vertex entry point returns nothing and writes
/// <c>gl_Position</c> and the varyings. A fragment entry point returns the colour: <c>vec4 fs_main()</c>,
/// <c>uvec4 fs_pick()</c>.
/// </description></item>
/// <item><description>
/// A member of a vertex input struct, <c>in.name</c> in WGSL, is the attribute <c>a_name</c>. A member of a
/// vertex output struct, <c>out.name</c> in a vertex stage and <c>in.name</c> in a fragment stage, is the
/// varying <c>v_name</c>. <c>out.clipPos</c> is <c>gl_Position</c>. Attributes carry
/// <c>layout(location = n)</c> with the WGSL's locations.
/// </description></item>
/// <item><description>
/// The uniform blocks are named <c>SceneUniforms</c> and <c>UserUniforms</c>, with the instance names
/// <c>u</c> and <c>user</c>, so <c>u.viewProj</c> and <c>user.slots[i]</c> read as they do in WGSL. A context
/// finds them by block name.
/// </description></item>
/// <item><description>
/// <c>gl_Position</c> is written for WebGPU's clip space, where depth runs from 0 to <c>w</c>: the camera
/// matrices are built for it. The context converts it to WebGL's after the entry point returns.
/// </description></item>
/// </list>
/// </remarks>
module GlslShaders =

    // The segments below are those of `Shaders.fs`, cut at the same places.

    [<Literal>]
    let private SceneHead =
        """
layout(std140) uniform SceneUniforms {
    mat4 viewProj;
    vec4 camPos;
    vec4 clipPlane;
    vec4 skyColor;
    vec4 groundColor;
    vec4 keyLight;
    vec4 rimLight;
    vec4 sectionStyle;
} u;

// Application data written with WgpuContext.SetUserUniforms. Every slot is zero until it is written.
layout(std140) uniform UserUniforms {
    vec4 slots[16];
} user;

#if defined(VS_MAIN) || defined(FS_MAIN) || defined(FS_PICK)

// struct VsIn
#ifdef VS_MAIN
layout(location = 0) in vec3 a_localPos;
layout(location = 1) in vec3 a_localNormal;
layout(location = 2) in vec4 a_vertexColor;
layout(location = 3) in vec4 a_modelCol0;
layout(location = 4) in vec4 a_modelCol1;
layout(location = 5) in vec4 a_modelCol2;
layout(location = 6) in vec4 a_modelCol3;
layout(location = 7) in vec4 a_colorRoughness;
layout(location = 8) in uvec2 a_pickFlags;
layout(location = 9) in vec2 a_opacityEmissive;
#endif

// struct VsOut; clipPos is gl_Position
VARYING vec3 v_worldPos;
VARYING vec3 v_worldNormal;
VARYING vec4 v_colorRoughness;
VARYING vec2 v_opacityEmissive;
flat VARYING uvec2 v_pickFlags;
"""

    [<Literal>]
    let private SceneAfterVaryings =
        """
"""

    [<Literal>]
    let private SceneVertexOpen =
        """#ifdef VS_MAIN
void vs_main() {
"""

    [<Literal>]
    let private SceneVertexBody =
        """    mat4 model = mat4(a_modelCol0, a_modelCol1, a_modelCol2, a_modelCol3);
    vec4 worldPos4 = model * vec4(a_localPos, 1.0);

    // Exact inverse-transpose normal via 3x3 cofactor matrix (handles non-uniform BIM scales + rotations)
    vec3 c0 = a_modelCol0.xyz;
    vec3 c1 = a_modelCol1.xyz;
    vec3 c2 = a_modelCol2.xyz;
    vec3 adj0 = cross(c1, c2);
    vec3 adj1 = cross(c2, c0);
    vec3 adj2 = cross(c0, c1);
    float detSign = dot(c0, adj0) < 0.0 ? -1.0 : 1.0;
    vec3 rawN = (adj0 * a_localNormal.x + adj1 * a_localNormal.y + adj2 * a_localNormal.z) * detSign;
    float nLenSq = dot(rawN, rawN);
    vec3 worldNormal = nLenSq > 1e-12 ? rawN * inversesqrt(max(nLenSq, 1e-12)) : vec3(0.0, 1.0, 0.0);

    gl_Position = u.viewProj * worldPos4;
    v_worldPos = worldPos4.xyz;
    v_worldNormal = worldNormal;
    v_colorRoughness = vec4(a_vertexColor.rgb * a_colorRoughness.rgb, a_colorRoughness.a);
    v_opacityEmissive = vec2(a_vertexColor.a * a_opacityEmissive.x, a_opacityEmissive.y);
    v_pickFlags = a_pickFlags;
"""

    [<Literal>]
    let private SceneFragmentOpen =
        """}
#endif

#ifdef FS_MAIN
vec4 fs_main() {
    bool isFront = gl_FrontFacing;
    uint flags = v_pickFlags.y;
    bool ignoreClip = (flags & 4u) != 0u;
    bool sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    float clipDist = dot(v_worldPos, u.clipPlane.xyz) + u.clipPlane.w;
    if (sectionActive && clipDist < 0.0) {
        discard;
    }

"""

    [<Literal>]
    let private SceneFragmentShading =
        """    float alpha = clamp(v_opacityEmissive.x, 0.0, 1.0);
    if (alpha <= 0.002) {
        discard;
    }

    bool isUnlit = (flags & 1u) != 0u;
    bool isSelected = (flags & 2u) != 0u;

    vec3 lit;
    if (isUnlit) {
        lit = v_colorRoughness.rgb * (1.0 + v_opacityEmissive.y);
        if (isSelected) {
            lit = mix(lit, vec3(0.45, 0.96, 1.0), 0.45) + vec3(0.15, 0.35, 0.45);
        }
    } else {
        vec3 V = normalize(u.camPos.xyz - v_worldPos);
        vec3 rawN = normalize(v_worldNormal);
        vec3 N = isFront ? rawN : -rawN;

        vec3 baseAlbedo = isSelected ? mix(v_colorRoughness.rgb, vec3(0.38, 0.94, 1.0), 0.52) : v_colorRoughness.rgb;
        vec3 albedo = isFront ? baseAlbedo : baseAlbedo * vec3(0.78, 0.72, 0.68);
        float roughness = clamp(v_colorRoughness.a, 0.06, 0.96);

        vec3 hemi = mix(u.groundColor.xyz, u.skyColor.xyz, N.y * 0.5 + 0.5);

        vec3 L1 = normalize(u.keyLight.xyz);
        float ndotl1 = max(dot(N, L1), 0.0);
        vec3 H1 = normalize(L1 + V);
        float shininess = mix(112.0, 10.0, roughness);
        float specNorm = (shininess + 8.0) / 25.1327;
        float spec1 = pow(max(dot(N, H1), 0.0), shininess) * specNorm * ndotl1 * (1.0 - roughness * 0.62);

        vec3 L2 = normalize(u.rimLight.xyz);
        float ndotl2 = max(dot(N, L2), 0.0);
        float fresnel = pow(1.0 - max(dot(N, V), 0.0), 3.2);

        lit =
            albedo * (hemi + vec3(1.0, 0.97, 0.91) * (ndotl1 * u.keyLight.w) + vec3(0.42, 0.70, 1.0) * (ndotl2 * u.rimLight.w))
            + vec3(1.0, 0.98, 0.93) * (spec1 * 0.38)
            + vec3(0.26, 0.56, 0.92) * (fresnel * 0.18)
            + baseAlbedo * v_opacityEmissive.y;

        if (isSelected) {
            lit = lit + vec3(0.14, 0.38, 0.52) * (0.45 + fresnel * 0.85);
        }
    }

"""

    [<Literal>]
    let private SceneTail =
        """    if (sectionActive && u.sectionStyle.w > 0.0 && clipDist < u.sectionStyle.w) {
        float edgeT = 1.0 - (clipDist / u.sectionStyle.w);
        lit = mix(lit, u.sectionStyle.xyz, edgeT * 0.88);
    }

    vec3 mapped = lit / (lit + vec3(0.86));
    vec3 gamma = pow(clamp(mapped * 1.36, vec3(0.0), vec3(1.0)), vec3(1.0 / 2.15));
    return vec4(gamma * alpha, alpha);
}
#endif

#ifdef FS_PICK
uvec4 fs_pick() {
    uint pickId = v_pickFlags.x;
    if (pickId == 0u) {
        discard;
    }
    uint flags = v_pickFlags.y;
    bool ignoreClip = (flags & 4u) != 0u;
    bool sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    float clipDist = dot(v_worldPos, u.clipPlane.xyz) + u.clipPlane.w;
    if (sectionActive && clipDist < 0.0) {
        discard;
    }
    return uvec4(
        floatBitsToUint(v_worldPos.x),
        floatBitsToUint(v_worldPos.y),
        floatBitsToUint(v_worldPos.z),
        pickId
    );
}
#endif

#endif

// ============================================================================
// 3D Unlit Alpha-Blended Hairline Pipeline (`LineVertex3D`, `LineList`)
// ============================================================================

#if defined(VS_LINE) || defined(FS_LINE)

// struct LineVsIn
#ifdef VS_LINE
layout(location = 0) in vec4 a_posPad;
layout(location = 1) in vec4 a_color;
#endif

// struct LineVsOut; clipPos is gl_Position
VARYING vec3 v_worldPos;
VARYING vec4 v_color;

#ifdef VS_LINE
void vs_line() {
    vec3 wp = a_posPad.xyz;
    gl_Position = u.viewProj * vec4(wp, 1.0);
    v_worldPos = wp;
    v_color = a_color;
}
#endif

#ifdef FS_LINE
vec4 fs_line() {
    bool sectionActive = length(u.clipPlane.xyz) > 0.5;
    if (sectionActive && (dot(v_worldPos, u.clipPlane.xyz) + u.clipPlane.w) < 0.0) {
        discard;
    }
    if (v_color.a <= 0.002) {
        discard;
    }
    float lineAlpha = clamp(v_color.a, 0.0, 1.0);
    return vec4(v_color.rgb * lineAlpha, lineAlpha);
}
#endif

#endif

// ============================================================================
// Camera-Facing SDF Disc + Segmented Verification Arc Pipeline (`DiscInstance3D`)
// ============================================================================

#if defined(VS_DISC) || defined(FS_DISC) || defined(FS_DISC_PICK)

// struct DiscVsIn; vid is gl_VertexID
#ifdef VS_DISC
layout(location = 0) in vec4 a_centerRadiusPx;
layout(location = 1) in vec4 a_fillColor;
layout(location = 2) in vec4 a_arcParams;
layout(location = 3) in uvec2 a_pickFlags;
layout(location = 4) in vec2 a_gapTrack;
#endif

// struct DiscVsOut; clipPos is gl_Position
VARYING vec3 v_worldPos;
VARYING vec2 v_localPx;
VARYING vec4 v_fillColor;
VARYING vec4 v_arcParams;
flat VARYING uvec2 v_pickFlags;
VARYING vec3 v_radiusGapTrack;

#ifdef VS_DISC
void vs_disc() {
    vec2 corners[6] = vec2[6](
        vec2(-1.0, -1.0),
        vec2( 1.0, -1.0),
        vec2( 1.0,  1.0),
        vec2(-1.0, -1.0),
        vec2( 1.0,  1.0),
        vec2(-1.0,  1.0)
    );
    vec2 corner = corners[uint(gl_VertexID) % 6u];
    vec3 worldPos = a_centerRadiusPx.xyz;
    float radiusPx = max(a_centerRadiusPx.w, 0.5);
    float arcThick = max(a_arcParams.z, 0.0);
    bool isSelected = (a_pickFlags.y & 2u) != 0u;
    float extraMargin =
        (arcThick > 0.01 ? a_gapTrack.x + arcThick + 1.5 : 0.0)
        + (isSelected ? 6.0 : 0.0)
        + 1.75;
    float quadHalfPx = radiusPx + extraMargin;

    float vpW = max(u.skyColor.w, 1.0);
    float vpH = max(u.groundColor.w, 1.0);
    vec4 clipCenter = u.viewProj * vec4(worldPos, 1.0);

    if (clipCenter.w <= 0.0001) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    } else {
        vec2 ndcOffset = vec2(
            corner.x * (2.0 * quadHalfPx / vpW),
            corner.y * (2.0 * quadHalfPx / vpH)
        );
        gl_Position = vec4(
            clipCenter.xy + ndcOffset * clipCenter.w,
            clipCenter.z,
            clipCenter.w
        );
    }
    v_worldPos = worldPos;
    v_localPx = vec2(corner.x * quadHalfPx, -corner.y * quadHalfPx);
    v_fillColor = a_fillColor;
    v_arcParams = a_arcParams;
    v_pickFlags = a_pickFlags;
    v_radiusGapTrack = vec3(radiusPx, a_gapTrack.x, a_gapTrack.y);
}
#endif

#ifdef FS_DISC
vec4 fs_disc() {
    uint flags = v_pickFlags.y;
    bool ignoreClip = (flags & 4u) != 0u;
    bool sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    if (sectionActive && (dot(v_worldPos, u.clipPlane.xyz) + u.clipPlane.w) < 0.0) {
        discard;
    }

    float r = length(v_localPx);
    float discR = v_radiusGapTrack.x;
    float gapPx = v_radiusGapTrack.y;
    float trackAlpha = v_radiusGapTrack.z;
    float verifiedFrac = clamp(v_arcParams.x, 0.0, 1.0);
    float inProgFrac = clamp(v_arcParams.y, 0.0, 1.0 - verifiedFrac);
    float arcThick = v_arcParams.z;
    float portalHollow = clamp(v_arcParams.w, 0.0, 1.0);
    bool isSelected = (flags & 2u) != 0u;

    float aa = 0.72;
    float discMask = 1.0 - smoothstep(discR - aa, discR + aa, r);

    // Optional mid-zoom hollow portal (`glibc`, `linux` revealing inner module constellations)
    vec3 discRgb = v_fillColor.rgb;
    float discAlpha = v_fillColor.a * discMask;
    if (portalHollow > 0.005 && discMask > 0.001) {
        float rimWidth = clamp(discR * 0.09, 1.35, 2.4);
        float innerMask = 1.0 - smoothstep(discR - rimWidth - aa, discR - rimWidth + aa, r);
        vec3 portalBg = vec3(0.035, 0.055, 0.095);
        discRgb = mix(discRgb, mix(discRgb, portalBg, innerMask * 0.92), portalHollow);
        float portalCoreAlpha = mix(v_fillColor.a, v_fillColor.a * 0.52, portalHollow);
        float rimBoost = 1.0 - innerMask;
        discAlpha = discMask * mix(portalCoreAlpha, max(v_fillColor.a, 0.88), rimBoost * portalHollow);
    }

    vec3 outRgb = discRgb;
    float outAlpha = discAlpha;

    // Concentric clockwise verification progress arc starting at 12 o'clock
    if (arcThick > 0.05) {
        float arcR = discR + gapPx;
        float halfT = arcThick * 0.5;
        float ringMask = 1.0 - smoothstep(halfT - 0.62, halfT + 0.62, abs(r - arcR));
        if (ringMask > 0.004) {
            float rawAng = atan(v_localPx.x, -v_localPx.y);
            float cwAng = rawAng < 0.0 ? rawAng + 6.283185307 : rawAng;
            float frac = cwAng * 0.159154943;

            vec3 greenCol = vec3(0.149, 0.816, 0.486); // #26d07c
            vec3 amberCol = vec3(0.984, 0.573, 0.235); // #fb923c
            vec3 slateCol = vec3(0.200, 0.255, 0.333); // #334155

            vec3 segCol = slateCol;
            float segAlpha = trackAlpha * v_fillColor.a;
            if (verifiedFrac > 0.001 && frac <= verifiedFrac) {
                segCol = greenCol;
                segAlpha = min(1.0, v_fillColor.a * 1.15);
            } else if (inProgFrac > 0.001 && frac <= (verifiedFrac + inProgFrac)) {
                segCol = amberCol;
                segAlpha = min(1.0, v_fillColor.a * 1.12);
            }
            float a = ringMask * segAlpha;
            float combA = a + outAlpha * (1.0 - a);
            if (combA > 0.0001) {
                outRgb = (segCol * a + outRgb * outAlpha * (1.0 - a)) / combA;
                outAlpha = combA;
            }
        }
    }

    // Selection halo ring
    if (isSelected) {
        float selR = discR + (arcThick > 0.05 ? gapPx + arcThick + 2.6 : 2.8);
        float selMask = 1.0 - smoothstep(0.6, 1.9, abs(r - selR));
        if (selMask > 0.004) {
            vec3 selCol = vec3(0.220, 0.741, 0.973); // #38bdf8
            float a = selMask * 0.92;
            float combA = a + outAlpha * (1.0 - a);
            outRgb = (selCol * a + outRgb * outAlpha * (1.0 - a)) / combA;
            outAlpha = combA;
        }
    }

    if (outAlpha <= 0.005) {
        discard;
    }
    float clampedAlpha = clamp(outAlpha, 0.0, 1.0);
    return vec4(outRgb * clampedAlpha, clampedAlpha);
}
#endif

#ifdef FS_DISC_PICK
uvec4 fs_disc_pick() {
    uint pickId = v_pickFlags.x;
    if (pickId == 0u) {
        discard;
    }
    uint flags = v_pickFlags.y;
    bool ignoreClip = (flags & 4u) != 0u;
    bool sectionActive = (!ignoreClip) && (length(u.clipPlane.xyz) > 0.5);
    if (sectionActive && (dot(v_worldPos, u.clipPlane.xyz) + u.clipPlane.w) < 0.0) {
        discard;
    }
    float r = length(v_localPx);
    float hitR = v_radiusGapTrack.x + (v_arcParams.z > 0.05 ? v_radiusGapTrack.y + v_arcParams.z : 1.5);
    if (r > hitR) {
        discard;
    }
    return uvec4(
        floatBitsToUint(v_worldPos.x),
        floatBitsToUint(v_worldPos.y),
        floatBitsToUint(v_worldPos.z),
        pickId
    );
}
#endif

#endif
"""

    /// <summary>
    /// Default GLSL ES 3.00 shader text with the entry points of <c>Shaders.DefaultSceneWgsl</c>:
    /// <c>vs_main</c>, <c>fs_main</c>, <c>fs_pick</c>, <c>vs_line</c>, <c>fs_line</c>, <c>vs_disc</c>,
    /// <c>fs_disc</c> and <c>fs_disc_pick</c>.
    /// </summary>
    /// <remarks>
    /// To change the shading, pass code for the named extension points to <c>composeScene</c> rather than
    /// editing this text: the text may change between versions, and the extension points keep their names.
    /// </remarks>
    [<Literal>]
    let DefaultSceneGlsl =
        SceneHead
        + SceneAfterVaryings
        + SceneVertexOpen
        + SceneVertexBody
        + SceneFragmentOpen
        + SceneFragmentShading
        + SceneTail

    /// <summary>
    /// Builds the stock scene shader with a consumer's GLSL inserted at the extension points of
    /// <c>Shaders.composeScene</c>, ready to pass as the custom GLSL of a <c>WgpuContext</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Give the same hooks to both functions, each in its language, and a context draws the same on WebGPU and
    /// on WebGL2. A hook that only names uniforms and locals, such as <c>lit = user.slots[2].rgb;</c>, is the
    /// same text in both. What is in scope at each hook, in GLSL:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Varyings</c>: one declaration per line, written <c>VARYING vec3 v_name;</c>, or
    /// <c>flat VARYING uvec2 v_name;</c> for integers.
    /// </description></item>
    /// <item><description>
    /// <c>Declarations</c>: global scope of the vertex stage and of both mesh fragment stages. The uniforms
    /// <c>u</c> and <c>user</c> and the varyings <c>v_*</c> are in scope; the attributes <c>a_*</c> only
    /// inside <c>#ifdef VS_MAIN</c>.
    /// </description></item>
    /// <item><description>
    /// <c>VertexBegin</c>: the attributes <c>a_*</c>. To replace the stock vertex stage, write
    /// <c>gl_Position</c> and every varying, then <c>return;</c>.
    /// </description></item>
    /// <item><description>
    /// <c>VertexEnd</c>: the attributes, <c>model</c>, <c>worldPos4</c>, <c>worldNormal</c>, <c>c0</c>,
    /// <c>c1</c>, <c>c2</c>, <c>gl_Position</c> and the varyings. Assign the varyings added through
    /// <c>Varyings</c> here.
    /// </description></item>
    /// <item><description>
    /// <c>FragmentBegin</c>: the varyings <c>v_*</c>, <c>isFront</c> and <c>flags</c>. The code may
    /// <c>return</c> a premultiplied <c>vec4</c> or <c>discard</c>.
    /// </description></item>
    /// <item><description>
    /// <c>FragmentLit</c>: everything from <c>FragmentBegin</c>, <c>alpha</c>, and the linear colour
    /// <c>lit</c>, which the code may change.
    /// </description></item>
    /// </list>
    /// <para>
    /// Code given twice for one hook is inserted in the order given. With no hooks the result is
    /// <c>DefaultSceneGlsl</c>. The line and disc shaders are not touched.
    /// <c>WgpuContext.ValidateGlslAsync</c> checks the result without a canvas.
    /// </para>
    /// </remarks>
    /// <exception cref="T:System.ArgumentNullException">A hook's code is null.</exception>
    let composeScene (hooks: seq<SceneShaderHook * string>) : string =
        let given = Seq.toArray hooks
        let text = StringBuilder (DefaultSceneGlsl.Length + 1024)

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
        Regex (@"\b(?:void|vec4|uvec4)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant)

    /// <summary>
    /// Decides, from the functions custom GLSL defines, which pipelines use it and which fall back to the stock
    /// shader, by the rules of <c>Shaders.planSceneEntryPoints</c>. Returns the reason when the set of entry
    /// points cannot make a working context.
    /// </summary>
    let internal planSceneEntryPoints (glsl: string) : Result<SceneEntryPlan, string> =
        // The rules read WGSL function headers, so each GLSL function is handed over as one.
        let headers = StringBuilder ()

        for m in functionPattern.Matches (commentPattern.Replace (glsl, " ")) do
            headers.Append("fn ").Append(m.Groups[1].Value).Append('\n')
            |> ignore

        match Shaders.planSceneEntryPoints (headers.ToString ()) with
        | Ok plan -> Ok plan
        | Error reason -> Error (reason.Replace ("WGSL", "GLSL"))
