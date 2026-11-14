module RanvierWgpuAtlas.Program

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Numerics
open System.Runtime.InteropServices
open Ranvier
open Ranvier.Dom
open Ranvier.D5
open Ranvier.D5.View
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.WebCore
open Ranvier.View.WebCore.Sdl
open Ranvier.Wgpu
open Ranvier.Wgpu.Sdl

// ---------------------------------------------------------------------------
// 1. Constants, 3-Mode Hyperbolic/3D Camera & Quaternion Trackball Model
// ---------------------------------------------------------------------------

[<Literal>]
let WindowWidth = 1360

[<Literal>]
let WindowHeight = 860

[<Literal>]
let LeftRailWidth = 248

[<Literal>]
let TopBarHeight = 46

[<Literal>]
let StageWidth = 1112

[<Literal>]
let StageHeight = 814

[<Literal>]
let ExplorerWidth = 370.0

[<Literal>]
let CosmosRadius = 5.0f

[<Literal>]
let RefCamDist = 19.8f

[<Literal>]
let BaseHalfFovDeg = 15.8f

[<RequireQualifiedAccess; Struct>]
type AtlasMode =
    | Map
    | Globe
    | Layers

[<Struct>]
type ModeWeights = {
    WMap: float32
    WGlobe: float32
    WLayers: float32
}

module ModeWeights =
    let ofMode = function
        | AtlasMode.Map -> { WMap = 1.0f; WGlobe = 0.0f; WLayers = 0.0f }
        | AtlasMode.Globe -> { WMap = 0.0f; WGlobe = 1.0f; WLayers = 0.0f }
        | AtlasMode.Layers -> { WMap = 0.0f; WGlobe = 0.0f; WLayers = 1.0f }

    let lerp (a: ModeWeights) (b: ModeWeights) (t: float32) = {
        WMap = a.WMap + (b.WMap - a.WMap) * t
        WGlobe = a.WGlobe + (b.WGlobe - a.WGlobe) * t
        WLayers = a.WLayers + (b.WLayers - a.WLayers) * t
    }

[<Struct>]
type AtlasCamera = {
    Mode: AtlasMode
    Weights: ModeWeights
    Q: Quaternion
    Zoom: float
    PanWorldX: float32
    PanWorldY: float32
}

module AtlasCamera =
    let private mapDefaultTilt =
        Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll(
                -4.0f * MathF.PI / 180.0f,
                5.5f * MathF.PI / 180.0f,
                0.0f
            )
        )

    let globeShowcaseRotation =
        Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll(
                28.0f * MathF.PI / 180.0f,
                -16.0f * MathF.PI / 180.0f,
                4.0f * MathF.PI / 180.0f
            )
        )

    let mapOverview = {
        Mode = AtlasMode.Map
        Weights = ModeWeights.ofMode AtlasMode.Map
        Q = mapDefaultTilt
        Zoom = 1.0
        PanWorldX = 0.0f
        PanWorldY = 0.0f
    }

    let globeOverview = {
        Mode = AtlasMode.Globe
        Weights = ModeWeights.ofMode AtlasMode.Globe
        Q = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(10.0f * MathF.PI / 180.0f, -8.0f * MathF.PI / 180.0f, 0.0f))
        Zoom = 1.0
        PanWorldX = 0.0f
        PanWorldY = 0.0f
    }

    let layersOverview = {
        Mode = AtlasMode.Layers
        Weights = ModeWeights.ofMode AtlasMode.Layers
        Q = Quaternion.Identity
        Zoom = 1.0
        PanWorldX = 0.0f
        PanWorldY = 0.0f
    }

    let inline fovYRadForZoom (zoom: float) : float32 =
        let baseTan = MathF.Tan(BaseHalfFovDeg * MathF.PI / 180.0f)
        2.0f * MathF.Atan(baseTan / float32 (max 0.55 zoom))

    let inline worldUnitsPerPixelAtZero (zoom: float) : float32 =
        let halfH = RefCamDist * MathF.Tan(fovYRadForZoom zoom * 0.5f)
        (2.0f * halfH) / float32 StageHeight

    /// Converts AtlasCamera into Ranvier.Wgpu's Camera3D.
    /// When the right-hand Explorer drawer is open (`drawerOpen = true`), shifts camera target slightly right
    /// so the focal point sits cleanly at the center of the unobstructed left viewport (`x = 371 px`).
    let toCamera3D (drawerOpen: bool) (cam: AtlasCamera) : Camera3D =
        let fovRad = fovYRadForZoom cam.Zoom
        let fovDeg = fovRad * 180.0f / MathF.PI
        let drawerShiftWorld =
            if drawerOpen then float32 (ExplorerWidth * 0.5) * worldUnitsPerPixelAtZero cam.Zoom
            else 0.0f
        let cx = cam.PanWorldX + drawerShiftWorld
        let cy = cam.PanWorldY
        {
            Position = Vector3(cx, cy, RefCamDist)
            Target = Vector3(cx, cy, 0.0f)
            Up = Vector3.UnitY
            FovYDeg = fovDeg
            NearPlane = 0.25f
            FarPlane = 64.0f
        }

    /// Applies screen-space trackball rotation (`axis = [-dy, dx, 0]` in world XY where +Y is up and screen +Y is down).
    let applyTrackballDrag (dxPx: float) (dyPx: float) (cam: AtlasCamera) : AtlasCamera =
        let mag = Double.Hypot(dxPx, dyPx)
        if mag < 1e-4 then cam
        else
            // Dragging right (+dx) rotates front +Z toward +X (around +Y); dragging down (+dy) rotates front +Z toward -Y (around +X).
            let axis = Vector3.Normalize(Vector3(float32 dyPx, float32 dxPx, 0.0f))
            let angle = float32 (mag * 0.0048 / Math.Sqrt(max 1.0 cam.Zoom))
            let dq = Quaternion.CreateFromAxisAngle(axis, angle)
            { cam with Q = Quaternion.Normalize(dq * cam.Q) }

    let lerp (a: AtlasCamera) (b: AtlasCamera) (t: float) : AtlasCamera =
        let tf = float32 t
        {
            Mode = if t >= 0.5 then b.Mode else a.Mode
            Weights = ModeWeights.lerp a.Weights b.Weights tf
            Q = Quaternion.Normalize(Quaternion.Slerp(a.Q, b.Q, tf))
            Zoom = a.Zoom + (b.Zoom - a.Zoom) * t
            PanWorldX = a.PanWorldX + (b.PanWorldX - a.PanWorldX) * tf
            PanWorldY = a.PanWorldY + (b.PanWorldY - a.PanWorldY) * tf
        }

// ---------------------------------------------------------------------------
// 2. Authentic &Atlas Dataset: 3,459 C Projects, 12,391 Directed Dependencies,
//    Poincaré Hyperbolic Dome / 3D Dandelion / Horseshoe Arch + C/Lean Specs
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess; Struct>]
type ProofStatus =
    | Verified
    | InProgress
    | Open

type InnerModule = {
    Id: string
    FileName: string
    Center: Point     // Computed by Ranvier.D5.Pack.layout inside [-1, 1]^2
    Radius: float
}

type InnerFunc = {
    Index: int
    Id: string
    Name: string
    ModuleIdx: int
    FileName: string
    LocalPos: Point   // Settled by Ranvier.D5.Force inside its module circle
    DotRadiusPx: float
    BaseStatus: ProofStatus
    VerifyThreshold: float // [0.0..1.0] for Verification Time-Travel Scrubber
    Lines: int
    StartLine: int
    EndLine: int
    SignatureC: string
    BodyC: string
    LeanSpec: string
}

[<Struct>]
type InnerCall = {
    Caller: int
    Callee: int
    BendSign: float
    CrossModule: bool
}

type InnerHypergraph = {
    Modules: InnerModule[]
    Functions: InnerFunc[]
    Calls: InnerCall[]
}

[<Struct>]
type PortalMicroDot = {
    LocalX: float32
    LocalY: float32
    RelRadius: float32
    StatusKind: byte // 0uy = slate/cream, 1uy = emerald verified, 2uy = amber in-progress
}

type AtlasProject = {
    Index: int
    Id: string
    Name: string
    Tier: int
    Description: string
    MapPos: Vector3
    GlobePos: Vector3
    LayersPos: Vector3
    BaseRadiusPx: float32
    FillColor: RgbaColor
    TotalLoc: int
    FnCount: int
    BaseVerifiedFrac: float32
    BaseInProgressFrac: float32
    HasArc: bool
    DependsOn: int[]
    mutable UsedByCount: int
    PortalDots: PortalMicroDot[]
    mutable CachedInner: InnerHypergraph option
}

[<Struct>]
type DepEdge = {
    FromIdx: int // Dependent (outer/higher tier)
    ToIdx: int   // Dependency target (inner/lower tier)
    IsCoreLong: bool
}

[<RequireQualifiedAccess>]
type SelectionTarget =
    | NoSelection
    | Project of projIdx: int
    | Function of projIdx: int * fnIdx: int

type AtlasDataset = {
    Projects: AtlasProject[]
    Edges: DepEdge[]
    ByProjectEdges: int[][]
    RankedVerifiedIds: int[]
    TotalProjects: int
    TotalDependencies: int
    BaselineVerifiedLines: int
    BaselineInProgressLines: int
    GlibcIdx: int
    LinuxIdx: int
    ZlibIdx: int
    OpensslIdx: int
    Libxml2Idx: int
}

module AtlasWorld =
    [<Literal>]
    let DefaultScrub = 0.62 // Matches live &Atlas snapshot (~105.9K verified · 194.9K in progress)

    let inline private hexCol (hex: string) = RgbaColor.fromHex hex

    /// Exact &Atlas Poincaré radial stretch: rhoHyp = tanh(1.6 * atanh(clamp(rhoLinear, 0.0, 0.992)))
    let inline poincareStretch (rhoLinear: float) : float =
        let u = Math.Clamp(rhoLinear, 0.0, 0.992)
        let atanhU = 0.5 * Math.Log((1.0 + u) / (1.0 - u))
        Math.Tanh(1.6 * atanhU)

    /// Evaluates project 3D world position under current mode weights and trackball quaternion Q.
    let inline worldPos (cam: AtlasCamera) (p: AtlasProject) : Vector3 =
        let w = cam.Weights
        let blended = p.MapPos * w.WMap + p.GlobePos * w.WGlobe + p.LayersPos * w.WLayers
        Vector3.Transform(blended, cam.Q)

    /// Computes live (verifiedFrac, inProgressFrac) for any package at scrubber position `scrub in [0..1]`.
    let inline scrubbedFracs (scrub: float) (p: AtlasProject) : struct (float32 * float32) =
        if not p.HasArc then
            struct (0.0f, 0.0f)
        else
            let scale = float32 (scrub / DefaultScrub)
            let vRaw = p.BaseVerifiedFrac * scale
            let wRaw =
                if scrub <= DefaultScrub then
                    p.BaseInProgressFrac * float32 (Math.Pow(max 0.02 (scrub / DefaultScrub), 0.65))
                else
                    // As scrub advances toward 100%, in-progress work converts into verified!
                    let convertT = float32 ((scrub - DefaultScrub) / (1.0 - DefaultScrub))
                    p.BaseInProgressFrac * (1.0f - 0.58f * convertT)
            let v = Math.Clamp(vRaw + (if scrub > DefaultScrub then p.BaseInProgressFrac * 0.52f * float32 ((scrub - DefaultScrub) / (1.0 - DefaultScrub)) else 0.0f), 0.0f, 0.96f)
            let w = Math.Clamp(wRaw, 0.0f, 0.98f - v)
            struct (v, w)

    /// Computes live ProofStatus of an inner C function at scrubber position `scrub in [0..1]`.
    let inline scrubbedFnStatus (scrub: float) (fn: InnerFunc) : ProofStatus =
        if scrub >= fn.VerifyThreshold then ProofStatus.Verified
        elif scrub >= fn.VerifyThreshold - 0.34 then ProofStatus.InProgress
        else ProofStatus.Open

    let private tier0Defs = [|
        "glibc", "The GNU C Library: core system call wrappers, allocator, dynamic loader, POSIX threads, and standard C runtime."
        "linux", "Linux kernel core subsystems: virtual memory, scheduler, VFS, netfilter, and system call ABI."
    |]

    let private tier1Names = [|
        "libunistring"; "libidn2"; "nghttp2"; "brotli"; "libxml2"; "gmp"
        "acl"; "xz"; "zlib"; "zstd"; "openssl"; "bzip2"
        "ncurses"; "util-linux"; "file"; "expat"; "libevent"; "krb5"
    |]

    let private tier2Names = [|
        "sqlite"; "cpython"; "freetype"; "libbsd"; "libcap"; "c-ares"; "cjson"; "cmark-gfm"
        "apr"; "attr"; "cpuinfo"; "flex"; "jemalloc"; "inih"; "ldns"; "libgd"
        "libmd"; "kissfft"; "libcoap"; "libnftnl"; "libmarias3"; "libsolv"; "miniz"; "nauty-and-traces"
        "littlefs"; "libzip"; "libyaml"; "lmdb"; "libeconf"; "libaio"; "keyutils"; "cyrus-sasl"
        "readline"; "linux-pam"; "p11-kit"; "nettle"; "libpsl"; "libpng"; "libwebp"; "libjpeg-turbo"
        "libxkbcommon"; "libgpg-error"; "libtirpc"; "libnl"
    |]

    let private tier3Names = [|
        "picohttpparser"; "redis"; "rax"; "pkgconf"; "s2n-tls"; "sdparm"; "siphash"; "protobuf-c"
        "snowball"; "tinycbor"; "zlib-ng"; "blake2"; "lz4"; "curl"; "systemd"; "wayland"
        "d-bus"; "pcre2"; "selinux"; "unbound"; "tpm2-tss"; "xcb"; "apparmor"; "cryptsetup"
        "e2fsprogs"; "elfutils"; "fontconfig"; "avahi"; "openldap"; "libarchive"; "libssh2"; "harfbuzz"
        "gnutls"; "libsodium"; "mbedtls"; "yyjson"; "jansson"; "libuv"
    |]

    let private tier4Names = [|
        "git"; "musl"; "busybox"; "openssh"; "wireguard-tools"; "nftables"; "chrony"; "polkit"
        "binutils"; "gdbm"; "lzo"; "argon2"; "wolfssl"; "oniguruma"; "tree-sitter"; "lua"
        "quickjs"; "wasm3"; "libev"; "zeromq"; "nanomsg"; "mosquitto"; "haproxy"; "nginx"
        "lighttpd"; "dnsmasq"; "knot"; "bird"; "frr"; "strongswan"; "openvpn"; "tcpdump"
        "libpcap"; "ethtool"; "iproute2"; "squashfs-tools"; "btrfs-progs"; "xfsprogs"; "lvm2"; "mdadm"
        "smartmontools"; "nvme-cli"; "pciutils"; "usbutils"; "alsa-lib"; "pipewire"; "pixman"; "cairo"
        "gdk-pixbuf"; "libtiff"; "giflib"; "opus"; "flac"; "libogg"; "libvorbis"; "speexdsp"
        "xxhash"; " snappy-c"; "zopfli"; "http-parser"; "llhttp"; "libmicrohttpd"; "hiredis"; "libpq"
    |]

    let private cEcosystemPrefixes = [|
        "lib"; "c-"; "tiny"; "open"; "u"; "mini"; "fast"; "ng"; "sys"; "net"; "io"; "fs"; "sec"; "proto"
    |]

    let private cEcosystemRoots = [|
        "asn1"; "ber"; "cbor"; "dns"; "elf"; "fdt"; "glob"; "hash"; "icmp"; "json"; "kex"; "lz"; "mime"; "netlink"
        "oid"; "pam"; "quic"; "ring"; "sctp"; "tar"; "uring"; "vfs"; "wire"; "xdr"; "yaml"; "zbuf"; "crc"; "hmac"
        "ecc"; "rsa"; "x509"; "pkcs"; "dtls"; "http"; "mqtt"; "amqp"; "resp"; "bson"; "msgpack"; "toml"; "ini"; "csv"
        "utf"; "puny"; "idna"; "regex"; "lex"; "peg"; "ast"; "arena"; "slab"; "buddy"; "btree"; "lsm"; "wal"; "mmap"
    |]

    let private descriptionFor (name: string) (tier: int) =
        match name with
        | "zlib" -> "A massively spiffy yet delicately unobtrusive lossless DEFLATE compression library (RFC 1950 / 1951 / 1952)."
        | "glibc" -> "The GNU C Library providing ISO C11, POSIX.1-2017, dynamic linking, malloc arenas, and Linux syscall bindings."
        | "linux" -> "Linux kernel core C implementation across memory management, VFS, scheduler, locking primitives, and networking."
        | "openssl" -> "Commercial-grade TLS 1.3 / X.509 / EVP cryptographic toolkit and constant-time bignum primitives in C."
        | "libxml2" -> "GNOME XML C parser and DOM/SAX2/XPath toolkit with streaming validation and encoding transcoders."
        | "zstd" -> "Fast lossless compression algorithm targeting real-time entropy coding (Finite State Entropy + Huffman)."
        | "xz" -> "LZMA2 general-purpose data compression library (`liblzma`) with integrity check filters."
        | "bzip2" -> "Burrows–Wheeler block-sorting lossless compression library + Huffman coding stages."
        | "brotli" -> "Generic-purpose lossless LZ77 + 2nd-order context modeling compression library (RFC 7932)."
        | "sqlite" -> "Self-contained transactional SQL database engine with B-tree pager, WAL journal, and VDBE bytecode VM."
        | "libevent" -> "Asynchronous event notification library wrapping epoll, kqueue, timers, and buffered non-blocking I/O."
        | "freetype" -> "Portable TrueType / OpenType / CFF font rasterization and hinting engine in ANSI C."
        | "cmark-gfm" -> "GitHub Flavored Markdown block/inline AST parser and CommonMark HTML renderer in C."
        | "c-ares" -> "Asynchronous C library for non-blocking DNS queries and RFC 1035 packet decoding."
        | _ when tier <= 2 -> $"Foundational C systems library (`{name}`) undergoing incremental Lean 4 memory-safety and functional specification."
        | _ -> $"C ecosystem package (`{name}`, dependency stratum {tier}) linked into the &Atlas dependency graph."

    /// Realistic C modules and functions for `zlib` (matching `step_85_shot_1_zlib_loaded.png` & `step_96.png`),
    /// `libxml2`, `glibc`, and general C packages.
    let private zlibModuleSpecs = [|
        "deflate.c", [|
            "deflate"; "deflateInit2_"; "deflateInit_"; "deflateEnd"; "deflateReset"; "deflateResetKeep"
            "deflateSetDictionary"; "deflateGetDictionary"; "deflateParams"; "deflateTune"; "deflateBound"
            "deflatePending"; "deflatePrime"; "deflateSetHeader"; "deflateCopy"; "deflateStateCheck"
            "putShortMSB"; "flush_pending"; "read_buf"; "lm_init"; "longest_match"; "fill_window"
            "deflate_stored"; "deflate_fast"; "deflate_slow"; "deflate_rle"; "deflate_huff"; "slide_hash"
        |]
        "inflate.c", [|
            "inflate"; "inflateInit2_"; "inflateInit_"; "inflateEnd"; "inflateReset"; "inflateReset2"
            "inflateResetKeep"; "inflatePrime"; "inflateSetDictionary"; "inflateGetDictionary"; "inflateGetHeader"
            "inflateSync"; "inflateSyncPoint"; "inflateCopy"; "inflateMark"; "inflateCodesUsed"
            "inflateStateCheck"; "fixedtables"; "updatewindow"; "syncsearch"; "inflateUndermine"; "inflateValidate"
        |]
        "trees.c", [|
            "_tr_init"; "_tr_stored_block"; "_tr_flush_bits"; "_tr_align"; "_tr_flush_block"; "_tr_tally"
            "init_block"; "pqdownheap"; "gen_bitlen"; "gen_codes"; "build_tree"; "scan_tree"
            "send_tree"; "build_bl_tree"; "send_all_trees"; "compress_block"; "detect_data_type"; "bi_reverse"
            "bi_flush"; "bi_windup"
        |]
        "gzlib.c", [|
            "gzopen"; "gzdopen"; "gzbuffer"; "gzrewind"; "gzseek64"; "gztell64"; "gzoffset64"; "gzeof"
            "gzerror"; "gzclearerr"; "gz_error"; "gz_reset"; "gz_open"; "gzread"; "gzwrite"; "gzflush"; "gzclose"
        |]
        "infback.c", [|
            "inflateBackInit_"; "inflateBack"; "inflateBackEnd"; "fixedtables_back"
            "inf_load_bits"; "inf_flush_out"; "inf_table_dispatch"; "inf_done_check"
            "inf_len_ext"; "inf_dist_ext"; "inf_copy_window"; "inf_state_alloc"
        |]
        "inftrees.c", [|
            "inflate_table"; "inftrees_codes"; "inftrees_lens"; "inftrees_dists"
            "huft_count_syms"; "huft_bounds_check"; "huft_fill_root"; "huft_fill_subtable"
            "huft_step_inc"; "huft_validate_max"
        |]
        "inffast.c", [|
            "inflate_fast"; "inffast_refill"; "inffast_literal"; "inffast_len_base"
            "inffast_dist_back"; "inffast_window_wrap"; "inffast_direct_copy"; "inffast_write_state"
            "inffast_guard_end"
        |]
        "compress.c", [|
            "compress2"; "compress"; "compressBound"; "uncompress2"; "uncompress"
            "zcalloc"; "zcfree"; "zError"; "zlibVersion"; "zlibCompileFlags"
        |]
        "adler32.c", [|
            "adler32_z"; "adler32"; "adler32_combine_"; "adler32_combine"; "adler32_combine64"
            "crc32_z"; "crc32"; "crc32_combine64"; "crc32_combine_gen64"; "get_crc_table"
        |]
    |]

    let private libxml2ModuleSpecs = [|
        "parser.c", [|
            "xmlParseDocument"; "xmlParseElement"; "xmlParseContent"; "xmlParseStartTag2"; "xmlParseEndTag2"
            "xmlParseAttribute2"; "xmlParseCharData"; "xmlParseComment"; "xmlParsePI"; "xmlParseCDSect"
            "xmlParseReference"; "xmlParseAttValueInternal"; "xmlParseName"; "xmlParseQName"; "xmlParseChunk"
            "xmlCtxtResetPush"; "xmlDetectSAX2"; "xmlStopParser"; "xmlFreeParserCtxt"; "xmlNewParserCtxt"
            "xmlReadMemory"; "xmlReadFd"
        |]
        "tree.c", [|
            "xmlNewDoc"; "xmlFreeDoc"; "xmlNewNode"; "xmlNewChild"; "xmlAddChild"; "xmlUnlinkNode"
            "xmlFreeNode"; "xmlCopyNode"; "xmlDocGetRootElement"; "xmlDocSetRootElement"; "xmlNewProp"
            "xmlSetProp"; "xmlGetProp"; "xmlHasProp"; "xmlNodeGetContent"; "xmlNodeSetContent"
            "xmlSearchNs"; "xmlNewNs"; "xmlBufferCreate"; "xmlBufferAdd"
        |]
        "xpath.c", [|
            "xmlXPathEval"; "xmlXPathEvalExpression"; "xmlXPathCompile"; "xmlXPathCompiledEval"
            "xmlXPathCompExpr"; "xmlXPathCompLocationPath"; "xmlXPathCompStep"; "xmlXPathNodeSetCreate"
            "xmlXPathNodeSetAdd"; "xmlXPathNodeSetMerge"; "xmlXPathCastNodeToString"; "xmlXPathCastToNumber"
            "xmlXPathEqualValues"; "xmlXPathNewContext"; "xmlXPathFreeContext"; "xmlXPathRegisterNs"
        |]
        "HTMLparser.c", [|
            "htmlParseDocument"; "htmlParseElement"; "htmlParseStartTag"; "htmlParseEndTag"
            "htmlParseCharData"; "htmlAutoClose"; "htmlTagLookup"; "htmlEntityLookup"
            "htmlCreateMemoryParserCtxt"; "htmlReadMemory"; "htmlCtxtReadMemory"; "htmlNodeStatus"
            "htmlParseScript"; "htmlParseHTMLAttribute"
        |]
        "valid.c", [|
            "xmlValidateDocument"; "xmlValidateElement"; "xmlValidateOneElement"; "xmlValidateOneAttribute"
            "xmlAddElementDecl"; "xmlAddAttributeDecl"; "xmlAddID"; "xmlGetID"; "xmlIsID"
            "xmlValidateRoot"; "xmlValidateDtd"; "xmlFreeElementTable"; "xmlValidBuildContentModel"
        |]
        "encoding.c", [|
            "xmlFindCharEncodingHandler"; "xmlCharEncInFunc"; "xmlCharEncOutFunc"; "xmlCharEncCloseFunc"
            "UTF8Toisolat1"; "isolat1ToUTF8"; "xmlGetCharEncodingName"; "xmlParseCharEncoding"
            "xmlDetectCharEncoding"; "xmlByteConsumed"; "UTF16LEToUTF8"; "UTF8ToUTF16LE"
        |]
        "SAX2.c", [|
            "xmlSAX2StartDocument"; "xmlSAX2EndDocument"; "xmlSAX2StartElementNs"; "xmlSAX2EndElementNs"
            "xmlSAX2Characters"; "xmlSAX2Reference"; "xmlSAX2Comment"; "xmlSAX2CDataBlock"
            "xmlSAX2GetEntity"; "xmlSAX2InternalSubset"; "xmlSAX2ExternalSubset"; "xmlSAXVersion"
        |]
        "xmlwriter.c", [|
            "xmlNewTextWriterMemory"; "xmlFreeTextWriter"; "xmlTextWriterStartDocument"; "xmlTextWriterEndDocument"
            "xmlTextWriterStartElement"; "xmlTextWriterEndElement"; "xmlTextWriterWriteAttribute"
            "xmlTextWriterWriteString"; "xmlTextWriterWriteFormatElement"; "xmlTextWriterFlush"
        |]
    |]

    let private genericCModules = [|
        "core.c",     [| "ctx_init"; "ctx_reset"; "ctx_destroy"; "state_check"; "state_step"; "dispatch_op"; "handle_err"; "version_str"; "flags_query"; "config_apply"; "limits_verify"; "invariant_ok"; "ref_acquire"; "ref_release"; "panic_guard"; "hook_register" |]
        "alloc.c",    [| "arena_create"; "arena_destroy"; "arena_alloc"; "arena_aligned"; "arena_rewind"; "slab_obtain"; "slab_recycle"; "pool_grow"; "pool_trim"; "zeroize_span"; "realloc_safe"; "calloc_checked"; "page_map"; "page_unmap" |]
        "codec.c",    [| "encode_block"; "decode_block"; "flush_bitbuf"; "refill_bitbuf"; "table_build"; "table_lookup"; "window_slide"; "window_copy"; "frame_header"; "frame_trailer"; "checksum_step"; "checksum_final"; "varint_put"; "varint_get"; "dict_bind"; "stream_sync" |]
        "parse.c",    [| "lexer_next"; "lexer_peek"; "parse_unit"; "parse_header"; "parse_field"; "parse_attr"; "scan_ident"; "scan_number"; "scan_string"; "utf8_validate"; "utf8_next_cp"; "unescape_buf"; "expect_tok"; "sync_recover"; "span_slice"; "bounds_guard" |]
        "io_ring.c",  [| "ring_open"; "ring_close"; "ring_submit"; "ring_complete"; "buf_readv"; "buf_writev"; "buf_drain"; "buf_reserve"; "fd_set_nonblock"; "poll_wait"; "timer_arm"; "timer_cancel"; "pipe_splice"; "seek_checked" |]
        "verify.c",   [| "crc32_update"; "adler_step"; "sha256_block"; "hmac_tag"; "ct_memcmp"; "nonce_advance"; "key_schedule"; "tag_verify"; "mac_finalize"; "header_crc_ok"; "length_check"; "magic_check" |]
        "table.c",    [| "htab_init"; "htab_clear"; "htab_find"; "htab_insert"; "htab_remove"; "htab_rehash"; "rb_insert"; "rb_erase"; "rb_lower_bound"; "vec_push"; "vec_reserve"; "vec_pop"; "ringq_push"; "ringq_pop" |]
        "api.c",      [| "open_stream"; "close_stream"; "process_chunk"; "finish_stream"; "get_pending"; "set_options"; "set_dictionary"; "copy_state"; "format_status"; "clear_error"; "bound_estimate"; "validate_args" |]
    |]

    let private buildFunctionCodeAndSpec (pkgName: string) (fileName: string) (fnName: string) (startLine: int) (endLine: int) (status: ProofStatus) =
        if pkgName = "zlib" && fnName = "deflate" then
            let sigC = "int ZEXPORT deflate(z_streamp strm, int flush)"
            let bodyC =
                String.concat "\n" [
                    $"/* src/zlib/{fileName}, lines {startLine}–{endLine} */"
                    "int ZEXPORT deflate(z_streamp strm, int flush) {"
                    "    int old_flush; /* value of flush param for previous deflate call */"
                    "    deflate_state *s;"
                    ""
                    "    if (deflateStateCheck(strm) || flush > Z_BLOCK || flush < 0) {"
                    "        return Z_STREAM_ERROR;"
                    "    }"
                    "    s = strm->state;"
                    "    if (strm->next_out == Z_NULL ||"
                    "        (strm->avail_in != 0 && strm->next_in == Z_NULL) ||"
                    "        (s->status == FINISH_STATE && flush != Z_FINISH)) {"
                    "        ERR_RETURN(strm, Z_STREAM_ERROR);"
                    "    }"
                    "    if (strm->avail_out == 0) ERR_RETURN(strm, Z_BUF_ERROR);"
                    "    old_flush = s->last_flush;"
                    "    s->last_flush = flush;"
                    "    if (s->pending != 0) {"
                    "        flush_pending(strm);"
                    "        if (strm->avail_out == 0) { s->last_flush = -1; return Z_OK; }"
                    "    }"
                    "    bstate = (*configuration_table[s->level].func)(s, flush);"
                    "    return bstate == finish_done ? Z_STREAM_END : Z_OK;"
                    "}"
                ]
            let leanSpec =
                String.concat "\n" [
                    "-- Lean 4 Specification · Zlib.Deflate.deflate_spec"
                    "theorem deflate_preserves_stream_inv"
                    "    (strm : ZStreamPtr) (flush : Int32) (σ : HeapState) :"
                    "  &require ValidStreamPtr strm σ ∧ 0 ≤ flush.val ∧ flush.val ≤ Z_BLOCK"
                    "  &prepost DisjointBuffers strm.next_in strm.next_out σ"
                    "  &post fun ret σ' =>"
                    "    (ret = Z_OK ∨ ret = Z_STREAM_END ∨ ret = Z_BUF_ERROR ∨ ret = Z_STREAM_ERROR) ∧"
                    "    ValidDeflateWindow strm.state σ' ∧"
                    "    DecompressStream (OutputPrefix strm σ') = InputConsumedPrefix strm σ'"
                ]
            sigC, bodyC, leanSpec
        else
            let stComment =
                match status with
                | ProofStatus.Verified -> "verified (0 sorry)"
                | ProofStatus.InProgress -> "proof obligation active"
                | ProofStatus.Open -> "auto-extracted C contract"
            let sigC = $"int {fnName}({pkgName}_ctx_t *ctx, const uint8_t *buf, size_t len)"
            let bodyC =
                String.concat "\n" [
                    $"/* src/{pkgName}/{fileName}, lines {startLine}–{endLine} */"
                    $"int {fnName}({pkgName}_ctx_t *ctx, const uint8_t *buf, size_t len) {{"
                    "    if (ctx == NULL || (len > 0 && buf == NULL)) {"
                    "        return -EINVAL;"
                    "    }"
                    "    if (ctx->avail < len) {"
                    "        int rc = flush_pending_window(ctx);"
                    "        if (rc != 0) return rc;"
                    "    }"
                    "    ctx->checksum = step_integrity_tag(ctx->checksum, buf, len);"
                    "    ctx->cursor += len;"
                    "    return 0;"
                    "}"
                ]
            let leanSpec =
                String.concat "\n" [
                    $"-- Lean 4 Specification ({stComment})"
                    $"theorem {fnName}_memory_and_state_safe"
                    $"    (ctx : Ptr {pkgName}_ctx_t) (buf : Slice UInt8) (σ : HeapState) :"
                    "  &require ValidCtx ctx σ ∧ ReadableSlice buf σ"
                    "  &prepost FramePreserved (CtxRegion ctx ∪ SliceRegion buf) σ"
                    "  &post fun rc σ' =>"
                    "    (rc = 0 → ValidCtx ctx σ' ∧ (Deref ctx σ').cursor = (Deref ctx σ).cursor + buf.len) ∧"
                    "    NoOutOfBoundsWrite σ σ'"
                ]
            sigC, bodyC, leanSpec

    /// Builds deterministic miniature portal constellation dots (~48 dots) for Stage 2 hollow portals (`glibc`, `linux`, etc.).
    let private buildPortalMicroDots (seed: int) (vFrac: float32) (wFrac: float32) : PortalMicroDot[] =
        let rng = Random(seed * 4999 + 17)
        let clusterCount = 7
        let centers =
            Array.init clusterCount (fun c ->
                if c = 0 then 0.0f, 0.0f, 0.25f
                else
                    let a = float (c - 1) / float (clusterCount - 1) * 2.0 * Math.PI + 0.35
                    float32 (0.48 * Math.Cos a), float32 (0.48 * Math.Sin a), 0.22f)
        let dots = ResizeArray<PortalMicroDot>(52)
        for c = 0 to clusterCount - 1 do
            let cx, cy, cr = centers[c]
            let n = if c = 0 then 9 else 7
            for k = 0 to n - 1 do
                let ang = float k / float n * 2.0 * Math.PI + rng.NextDouble() * 0.45
                let rad = cr * float32 (0.28 + 0.65 * Math.Sqrt(rng.NextDouble()))
                let lx = Math.Clamp(cx + rad * float32 (Math.Cos ang), -0.80f, 0.80f)
                let ly = Math.Clamp(cy + rad * float32 (Math.Sin ang), -0.80f, 0.80f)
                let u = float32 (rng.NextDouble())
                let kind =
                    if u < max 0.14f vFrac then 1uy
                    elif u < max 0.46f (vFrac + wFrac) then 2uy
                    else 0uy
                dots.Add {
                    LocalX = lx
                    LocalY = ly
                    RelRadius = 0.038f + float32 (rng.NextDouble()) * 0.024f
                    StatusKind = kind
                }
        dots.ToArray()

    /// Lazily builds the Stage 3/4 inner `.c` module circles (`Ranvier.D5.Pack.layout`) + 115–140 C functions (`Ranvier.D5.Force`)
    /// + 185–230 smooth caller–callee curves!
    let getInner (world: AtlasDataset) (projIdx: int) : InnerHypergraph =
        let pkg = world.Projects[projIdx]
        match pkg.CachedInner with
        | Some h -> h
        | None ->
            let rng = Random(projIdx * 8191 + 2026)
            let rawMods =
                if pkg.Name = "zlib" then zlibModuleSpecs
                elif pkg.Name = "libxml2" then libxml2ModuleSpecs
                else
                    genericCModules
                    |> Array.mapi (fun mIdx (fName, fns) ->
                        let prefix = if mIdx = 0 then $"{pkg.Name.Replace('-', '_')}.c" else fName
                        prefix, fns)

            // 1) Ranvier.D5.Pack.layout computes nested C file circles inside [-1, 1]^2
            let packRoot =
                Hierarchy.node
                    pkg.Id
                    0.0
                    [
                        for (fileName, fns) in rawMods ->
                            let w = float fns.Length * (0.92 + rng.NextDouble() * 0.18)
                            Hierarchy.node $"{pkg.Id}/{fileName}" w []
                    ]

            let packCfg = { PackConfig.create (Bounds.create 0.0 0.0 2.0 2.0) with Padding = 0.085 }
            let packed = Pack.layout packCfg packRoot |> Result.defaultValue []
            let rootC =
                packed
                |> List.tryFind (fun c -> c.Depth = 0)
                |> Option.defaultWith (fun () -> {
                    Id = pkg.Id
                    Parent = None
                    Depth = 0
                    Center = Point.create 1.0 1.0
                    Radius = 1.0
                })

            let normPt (pt: Point) =
                Point.create
                    ((pt.X - rootC.Center.X) / max 1e-6 rootC.Radius)
                    ((pt.Y - rootC.Center.Y) / max 1e-6 rootC.Radius)

            let modules =
                rawMods
                |> Array.mapi (fun mIdx (fileName, _) ->
                    let mid = $"{pkg.Id}/{fileName}"
                    match packed |> List.tryFind (fun c -> c.Depth = 1 && c.Id = mid) with
                    | Some c ->
                        {
                            Id = mid
                            FileName = fileName
                            Center = normPt c.Center
                            Radius = (c.Radius / max 1e-6 rootC.Radius) * 0.94
                        }
                    | None ->
                        let a = float mIdx / float rawMods.Length * 2.0 * Math.PI
                        {
                            Id = mid
                            FileName = fileName
                            Center = Point.create (0.50 * Math.Cos a) (0.50 * Math.Sin a)
                            Radius = 0.25
                        })

            // 2) Settle all functions in each C file using Ranvier.D5.Force so every module contains crisp organic clusters & rings!
            let allFns = ResizeArray<InnerFunc>(148)
            let calls = ResizeArray<InnerCall>(240)
            let moduleFnRanges = Array.zeroCreate<int * int> modules.Length

            let targetVerFrac = if pkg.HasArc then float pkg.BaseVerifiedFrac else 0.12
            let targetWipFrac = if pkg.HasArc then float pkg.BaseInProgressFrac else 0.32

            for mIdx = 0 to modules.Length - 1 do
                let m = modules[mIdx]
                let _, fnNames = rawMods[mIdx]
                let startGlobal = allFns.Count

                let gNodes = [
                    for k in 0 .. fnNames.Length - 1 ->
                        // Golden-angle sunflower seed so functions fill the entire module interior organically
                        let a0 = float k * 2.39996323
                        let r0 = if k = 0 then 0.0 else 6.8 * Math.Sqrt(float k)
                        {
                            GraphNode.create $"f{k}" with
                                Position = Some(Point.create (r0 * Math.Cos a0) (r0 * Math.Sin a0))
                                Radius = if k = 0 then 9.2 else 6.4 + float (k % 3) * 1.0
                        }
                ]
                let gLinks = [
                    for k in 0 .. fnNames.Length - 2 do
                        yield { GraphLink.create $"l1_{k}" $"f{k}" $"f{k + 1}" with Distance = 16.0; Strength = 0.28 }
                    for k = 0 to fnNames.Length - 4 do
                        if k % 2 = 0 then
                            yield { GraphLink.create $"l2_{k}" $"f{k}" $"f{k + 3}" with Distance = 22.0; Strength = 0.20 }
                    if fnNames.Length >= 6 then
                        for k = 2 to min (fnNames.Length - 1) 8 do
                            yield { GraphLink.create $"l0_{k}" "f0" $"f{k}" with Distance = 18.0; Strength = 0.24 }
                ]

                let fCfg = {
                    ForceConfig.create (Point.create 0.0 0.0) with
                        CenterStrength = 0.16
                        InitialRadius = 26.0
                        MaxSteps = 130
                        ManyBody = Some (ManyBodyConfig.create -22.0)
                        Collision = Some { Strength = 0.92; Iterations = 3 }
                }

                let localPts =
                    GraphData.create gNodes gLinks
                    |> Result.bind (fun gd ->
                        let seed = Seed.create (uint32 (projIdx * 977 + mIdx * 131 + 7))
                        Force.initialize fCfg seed gd
                        |> Result.map (fun st0 ->
                            let snap = st0 |> Force.step 115 |> Force.snapshot
                            let maxExt =
                                snap.Nodes
                                |> List.map (fun n -> max 18.0 (Double.Hypot(n.Position.X, n.Position.Y)))
                                |> List.max
                            let scale = (m.Radius * 0.76) / maxExt
                            snap.Nodes
                            |> List.mapi (fun idx n ->
                                if idx = 0 then m.Center
                                else Point.create (m.Center.X + n.Position.X * scale) (m.Center.Y + n.Position.Y * scale))
                            |> List.toArray))
                    |> Result.defaultValue [||]

                let mutable curLine = 68 + mIdx * 40
                for k = 0 to fnNames.Length - 1 do
                    let fnIdx = allFns.Count
                    let fnName = fnNames[k]
                    let pt =
                        if k < localPts.Length then localPts[k]
                        else
                            let a = float k / float fnNames.Length * 2.0 * Math.PI
                            let r = m.Radius * (0.28 + 0.48 * float ((k % 3) + 1) / 3.0)
                            Point.create (m.Center.X + r * Math.Cos a) (m.Center.Y + r * Math.Sin a)

                    let lines, sLine, eLine =
                        if pkg.Name = "zlib" && fnName = "deflate" then 310, 1000, 1309
                        else
                            let ln = 22 + ((fnIdx * 37 + k * 19) % 145)
                            let s = curLine
                            curLine <- curLine + ln + 8
                            ln, s, s + ln - 1

                    // Assign deterministic verification threshold so at DefaultScrub (0.62):
                    // - `deflate` (pkg = zlib, fnIdx = 0) is InProgress (#fb923c), matching `step_96.png`!
                    // - `adler32`, `crc32`, `inflate_table`, `zError` etc. are Verified (#26d07c)!
                    let rankU =
                        if pkg.Name = "zlib" && fnIdx = 0 then
                            targetVerFrac + targetWipFrac * 0.35 // -> InProgress at DefaultScrub = 0.62!
                        elif mIdx = modules.Length - 1 && k < 5 then
                            targetVerFrac * 0.45 // -> Verified!
                        else
                            (float ((fnIdx * 73 + mIdx * 29 + 11) % 100)) / 100.0

                    let verifyThreshold =
                        if rankU < targetVerFrac then
                            0.10 + (rankU / max 0.01 targetVerFrac) * (DefaultScrub - 0.12)
                        elif rankU < targetVerFrac + targetWipFrac then
                            let sub = (rankU - targetVerFrac) / max 0.01 targetWipFrac
                            DefaultScrub + 0.03 + sub * 0.29
                        else
                            0.97 + (rankU - targetVerFrac - targetWipFrac) * 0.25

                    let baseStatus =
                        if DefaultScrub >= verifyThreshold then ProofStatus.Verified
                        elif DefaultScrub >= verifyThreshold - 0.34 then ProofStatus.InProgress
                        else ProofStatus.Open

                    let sigC, bodyC, leanSpec = buildFunctionCodeAndSpec pkg.Name m.FileName fnName sLine eLine baseStatus
                    let dotR =
                        if fnIdx = 0 then 5.8
                        else Math.Clamp(2.3 + Math.Log10(float (lines + 4)) * 1.35, 2.7, 5.4)

                    allFns.Add {
                        Index = fnIdx
                        Id = $"{pkg.Id}::{fnName}"
                        Name = fnName
                        ModuleIdx = mIdx
                        FileName = m.FileName
                        LocalPos = pt
                        DotRadiusPx = dotR
                        BaseStatus = baseStatus
                        VerifyThreshold = verifyThreshold
                        Lines = lines
                        StartLine = sLine
                        EndLine = eLine
                        SignatureC = sigC
                        BodyC = bodyC
                        LeanSpec = leanSpec
                    }

                moduleFnRanges[mIdx] <- (startGlobal, allFns.Count - 1)

                // Intra-module call edges
                let countInMod = fnNames.Length
                for k = 0 to countInMod - 2 do
                    calls.Add {
                        Caller = startGlobal + k
                        Callee = startGlobal + k + 1
                        BendSign = if k % 2 = 0 then 1.0 else -1.0
                        CrossModule = false
                    }
                    if k + 3 < countInMod && k % 2 = 0 then
                        calls.Add {
                            Caller = startGlobal + k
                            Callee = startGlobal + k + 3
                            BendSign = if k % 4 = 0 then -1.15 else 1.15
                            CrossModule = false
                        }

            // Cross-module call edges (especially rich caller/callee fan-in & fan-out on `fnIdx = 0` = `deflate`!)
            if modules.Length >= 3 then
                // Incoming callers into `fn 0` (`deflate` called from `compress.c`, `gzlib.c`, and `deflateInit/Copy/Params`)
                let callerCandidates = [|
                    let sComp, eComp = moduleFnRanges[min (modules.Length - 2) 7]
                    yield sComp
                    if sComp + 1 <= eComp then yield sComp + 1
                    let sGz, eGz = moduleFnRanges[min (modules.Length - 1) 3]
                    if sGz + 4 <= eGz then yield sGz + 4
                    if sGz + 14 <= eGz then yield sGz + 14
                    if sGz + 15 <= eGz then yield sGz + 15
                    yield 8   // deflateParams -> deflate
                    yield 14  // deflateCopy / wrapper -> deflate
                |]
                for cIdx in callerCandidates |> Array.distinct do
                    if cIdx > 0 && cIdx < allFns.Count then
                        calls.Add {
                            Caller = cIdx
                            Callee = 0
                            BendSign = if cIdx % 2 = 0 then 1.0 else -1.0
                            CrossModule = allFns[cIdx].ModuleIdx <> 0
                        }

                // Outgoing callees from `fn 0` (`deflate` calling `deflateStateCheck`, `flush_pending`, `deflate_fast`, `deflate_slow`, `_tr_flush_block`, `_tr_align`, `adler32`, `crc32`)
                let calleeCandidates = [|
                    yield min (allFns.Count - 1) 15 // deflateStateCheck
                    yield min (allFns.Count - 1) 17 // flush_pending
                    yield min (allFns.Count - 1) 23 // deflate_fast
                    yield min (allFns.Count - 1) 24 // deflate_slow
                    let sTrees, eTrees = moduleFnRanges[min (modules.Length - 1) 2]
                    yield sTrees + min 3 (eTrees - sTrees) // _tr_align
                    yield sTrees + min 4 (eTrees - sTrees) // _tr_flush_block
                    let sAdler, eAdler = moduleFnRanges[modules.Length - 1]
                    yield sAdler + min 1 (eAdler - sAdler) // adler32
                    yield sAdler + min 6 (eAdler - sAdler) // crc32
                |]
                for tIdx in calleeCandidates |> Array.distinct do
                    if tIdx > 0 && tIdx < allFns.Count then
                        calls.Add {
                            Caller = 0
                            Callee = tIdx
                            BendSign = if tIdx % 2 = 0 then -1.0 else 1.0
                            CrossModule = allFns[tIdx].ModuleIdx <> 0
                        }

                // Inter-module background call web (~65 cross-module calls creating the authentic &Atlas silvery web)
                for mA = 0 to modules.Length - 1 do
                    let sA, eA = moduleFnRanges[mA]
                    let lenA = max 1 (eA - sA + 1)
                    for step = 0 to 7 do
                        let mB = (mA + 1 + (step % (modules.Length - 1))) % modules.Length
                        let sB, eB = moduleFnRanges[mB]
                        let lenB = max 1 (eB - sB + 1)
                        let u = sA + ((step * 5 + mA * 3) % lenA)
                        let v = sB + ((step * 7 + mB * 2) % lenB)
                        if u <> 0 && v <> 0 && u <> v then
                            calls.Add {
                                Caller = u
                                Callee = v
                                BendSign = if (u + v) % 2 = 0 then 0.9 else -0.9
                                CrossModule = true
                            }

            // Deduplicate directed pairs
            let seenPairs = HashSet<int64>()
            let uniqueCalls =
                calls
                |> Seq.filter (fun c -> c.Caller <> c.Callee && seenPairs.Add((int64 c.Caller <<< 20) ||| int64 c.Callee))
                |> Seq.toArray

            let built = {
                Modules = modules
                Functions = allFns.ToArray()
                Calls = uniqueCalls
            }
            pkg.CachedInner <- Some built
            built

    /// Deterministically constructs all 3,459 C projects across 12 dependency strata (`tier 0..11`)
    /// and exact 12,391 directed dependency edges!
    let create () : AtlasDataset =
        let rng = Random(20261113)
        let totalTarget = 3459
        let edgeTarget = 12391

        // Sub-ring layout:
        // Ring 0: tier 0 (2: glibc, linux)
        // Ring 1: tier 1 (34: zlib, openssl, zstd, ncurses, xz, bzip2, brotli, libxml2, ...)
        // Ring 2: tier 2 (28: sqlite, cpython, freetype, c-ares, cmark-gfm, jemalloc, ...)
        // Ring 3: tier 3 (38: curl, systemd, wayland, pcre2, pkgconf, ...)
        // Ring 4: tier 4 (64: git, musl, busybox, openssh, nginx, ...)
        // Rings 5..17 (13 outer sub-rings across tiers 5..11): 3459 - 166 = 3,293 projects!
        let outerRingCounts = [|
            115; 148; 178; 206; 232; 254; 274; 292; 308; 318; 322; 324; 322
        |]
        // Verify sum: 2 + 34 + 28 + 38 + 64 + 3293 = 3,459!
        let projects = Array.zeroCreate<AtlasProject> totalTarget
        let ringRanges = ResizeArray<int * int * int * float>() // (tier, startIdx, count, rhoLinear)

        let colLinux = hexCol "#9a6b3e"
        let colGlibc = hexCol "#caa070"
        let colCreamBright = hexCol "#f8e8d2"
        let colCreamWarm = hexCol "#ead2b5"
        let colTan = hexCol "#d9b38c"
        let colCopperMid = hexCol "#c69262"
        let colCopperOuter = hexCol "#b47f50"
        let colBronzeRim = hexCol "#9d6d44"

        let usedNames = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let allocUniqueName (preferred: string) (idx: int) =
            let clean = preferred.Trim()
            if usedNames.Add clean then clean
            else
                let alt = $"{clean}-{idx}"
                usedNames.Add alt |> ignore
                alt

        let mutable cursor = 0

        let addRing (tier: int) (rhoLin: float) (rBallNorm: float) (rArchNorm: float) (names: string[]) =
            let startIdx = cursor
            let count = names.Length
            ringRanges.Add(tier, startIdx, count, rhoLin)

            let rhoHyp = poincareStretch rhoLin
            let mapDomeZ = float32 (CosmosRadius * 0.42f * MathF.Sqrt(max 0.0f (1.0f - float32 (rhoHyp * rhoHyp))))

            for k = 0 to count - 1 do
                let idx = cursor
                cursor <- cursor + 1
                let name = allocUniqueName names[k] idx

                // Seamless 360° uniform ring spacing in Map & Globe AND strictly in (0, 1) for Layers horseshoe legs!
                let phaseShift =
                    if tier = 0 then 0.25
                    elif tier <= 4 then ((float ((tier * 37) % 100)) / 100.0) / float (max 4 count)
                    else (float ((ringRanges.Count * 53) % 100) / 100.0) / float count

                let uAngle =
                    if tier = 0 then
                        if k = 0 then 0.41 else 0.94 // glibc upper-left of center, linux right of center (matching `final_step_55.png` & `final_step_100.png`!)
                    else
                        ((float k + 0.5) / float count + phaseShift * 0.25) % 1.0

                let alpha =
                    if tier = 0 then (if k = 0 then 2.58 else -0.35)
                    else uAngle * 2.0 * Math.PI

                // 1) Map Mode (Poincaré concentric rings lifted onto 3D spherical dome)
                let mapX = float32 (rhoHyp * Math.Cos alpha) * CosmosRadius
                let mapY = float32 (rhoHyp * Math.Sin alpha) * CosmosRadius
                let mapPos = Vector3(mapX, mapY, mapDomeZ)

                // 2) Globe Mode (Volumetric 3D celestial dandelion: rBall increases by tier across full 3D sphere S^2!)
                let globePos =
                    if tier = 0 then
                        let coreDir =
                            if k = 0 then Vector3.Normalize(Vector3(-0.58f, 0.34f, 0.74f))
                            else Vector3.Normalize(Vector3(0.62f, -0.22f, 0.75f))
                        coreDir * (CosmosRadius * 0.075f)
                    else
                        // Archimedes hat-box uniform z/r on [-0.96, +0.96] via golden-ratio low-discrepancy sequence
                        let goldenU = ((float idx * 0.6180339887498949) % 1.0)
                        let sinBeta =
                            if tier <= 2 then -0.48 + goldenU * 1.34 |> fun v -> Math.Clamp(v, -0.82, 0.88)
                            else -0.965 + goldenU * 1.93
                        let cosBeta = Math.Sqrt(max 0.02 (1.0 - sinBeta * sinBeta))
                        let gAz = alpha + (if tier >= 5 then (goldenU - 0.5) * 0.32 else 0.0)
                        let dir =
                            Vector3.Normalize(
                                Vector3(
                                    float32 (cosBeta * Math.Cos gAz),
                                    float32 (cosBeta * Math.Sin gAz),
                                    float32 sinBeta
                                )
                            )
                        let jitterR = float32 rBallNorm * (0.97f + float32 (rng.NextDouble()) * 0.06f)
                        dir * (CosmosRadius * jitterR)

                // 3) Layers Mode (Concentric upward-arching horseshoe arch: +222° -> +90° top apex -> -42°, open bottom cutout!)
                let layersPos =
                    let uArch =
                        if tier = 0 then (if k = 0 then 0.36 else 0.64)
                        else
                            // Normalize alpha in [0, 2π) cleanly into [0, 1]
                            let normA = ((alpha % (2.0 * Math.PI)) + 2.0 * Math.PI) % (2.0 * Math.PI)
                            normA / (2.0 * Math.PI)
                    let thetaDeg = 222.0 - uArch * 264.0
                    let thetaRad = thetaDeg * Math.PI / 180.0
                    let rArch = float32 rArchNorm * CosmosRadius
                    let archCenterY = -0.17f * CosmosRadius
                    let lx = rArch * float32 (Math.Cos thetaRad)
                    let ly = archCenterY + rArch * float32 (Math.Sin thetaRad)
                    let lz = CosmosRadius * 0.16f * float32 (1.0 - rArchNorm * 0.82)
                    Vector3(lx, ly, lz)

                // Realistic LOC, screen radius, palette color, and verification progress
                let loc, fnCnt, baseRadPx, fillCol, hasArc, vFrac, wFrac =
                    match name with
                    | "linux" -> 1_840_000, 14_200, 12.8f, colLinux, true, 0.04f, 0.14f
                    | "glibc" -> 1_120_000, 6_450, 12.0f, colGlibc, true, 0.07f, 0.21f
                    | "zlib" -> 5_588, 136, 6.8f, colCreamBright, true, 0.094f, 0.567f // Exact 526 verified / 3,171 in progress / 5,588 lines!
                    | "openssl" -> 485_000, 4_820, 8.3f, colCreamWarm, true, 0.08f, 0.24f
                    | "libxml2" -> 212_000, 1_940, 7.9f, colCreamBright, true, 0.38f, 0.36f
                    | "libevent" -> 76_400, 720, 6.6f, colCreamBright, true, 0.41f, 0.34f
                    | "libwebp" -> 94_200, 810, 6.7f, colCreamWarm, true, 0.36f, 0.33f
                    | "freetype" -> 138_000, 1_180, 7.1f, colCreamBright, true, 0.33f, 0.37f
                    | "cmark-gfm" -> 24_800, 240, 5.7f, colCreamBright, true, 0.46f, 0.35f
                    | "c-ares" -> 34_600, 310, 5.9f, colCreamWarm, true, 0.39f, 0.38f
                    | "brotli" -> 38_400, 340, 6.3f, colCreamBright, true, 0.31f, 0.42f
                    | "cpython" -> 420_000, 3_600, 7.6f, colTan, true, 0.14f, 0.29f
                    | "xz" -> 42_500, 390, 6.4f, colCreamBright, true, 0.29f, 0.44f
                    | "zstd" -> 88_000, 740, 7.0f, colCreamBright, true, 0.22f, 0.41f
                    | "bzip2" -> 8_200, 118, 5.9f, colCreamBright, true, 0.44f, 0.38f
                    | "ncurses" -> 146_000, 1_120, 7.0f, colCreamWarm, true, 0.16f, 0.31f
                    | "nghttp2" -> 58_000, 520, 6.4f, colCreamWarm, true, 0.27f, 0.39f
                    | "sqlite" -> 156_000, 1_450, 7.3f, colCreamBright, true, 0.24f, 0.38f
                    | _ when tier = 1 ->
                        let l = 14_000 + (idx * 7_919) % 95_000
                        let r = 5.4f + float32 (Math.Log10(float l) - 4.0) * 1.15f
                        let c = if k % 2 = 0 then colCreamBright else colCreamWarm
                        let arc = k % 3 <> 2
                        let vf = if arc then 0.10f + float32 ((idx * 17) % 28) * 0.01f else 0.0f
                        let wf = if arc then 0.18f + float32 ((idx * 23) % 30) * 0.01f else 0.0f
                        l, max 95 (l / 110), r, c, arc, vf, wf
                    | _ when tier = 2 ->
                        let l = 8_500 + (idx * 6_151) % 68_000
                        let r = 4.6f + float32 (Math.Log10(float l) - 3.9) * 1.0f
                        let c = if k % 3 = 0 then colCreamWarm else colTan
                        let arc = k % 2 = 0
                        let vf = if arc then 0.08f + float32 ((idx * 13) % 26) * 0.01f else 0.0f
                        let wf = if arc then 0.16f + float32 ((idx * 19) % 28) * 0.01f else 0.0f
                        l, max 80 (l / 115), r, c, arc, vf, wf
                    | _ when tier = 3 ->
                        let l = 5_200 + (idx * 4_993) % 52_000
                        let r = 3.8f + float32 (rng.NextDouble()) * 1.05f
                        let c = if k % 3 = 0 then colTan else colCopperMid
                        let arc = k % 3 = 0
                        let vf = if arc then 0.06f + float32 ((idx * 11) % 22) * 0.01f else 0.0f
                        let wf = if arc then 0.15f + float32 ((idx * 17) % 24) * 0.01f else 0.0f
                        l, max 70 (l / 120), r, c, arc, vf, wf
                    | _ when tier = 4 ->
                        let l = 3_400 + (idx * 3_571) % 38_000
                        let r = 3.0f + float32 (rng.NextDouble()) * 0.95f
                        let c = colCopperMid
                        let arc = k % 6 = 0
                        let vf = if arc then 0.05f + float32 ((idx * 7) % 18) * 0.01f else 0.0f
                        let wf = if arc then 0.14f + float32 ((idx * 13) % 20) * 0.01f else 0.0f
                        l, max 60 (l / 125), r, c, arc, vf, wf
                    | _ ->
                        // Outer tiers 5..11: crisp jewel-like copper/bronze beads (`1.7px .. 2.35px` radius so outer rings never blob!)
                        let l = 900 + (idx * 1_997) % 14_000
                        let ringSub = max 0 (ringRanges.Count - 6)
                        let r = Math.Clamp(2.25f - float32 ringSub * 0.042f + float32 (rng.NextDouble()) * 0.28f, 1.62f, 2.45f)
                        let c =
                            if ringSub < 4 then colCopperMid
                            elif ringSub < 9 then colCopperOuter
                            else colBronzeRim
                        l, 48, r, c, false, 0.0f, 0.0f

                let portalDots =
                    if tier <= 2 then buildPortalMicroDots idx vFrac wFrac
                    else Array.empty

                projects[idx] <- {
                    Index = idx
                    Id = $"pkg-{idx}-{name}"
                    Name = name
                    Tier = tier
                    Description = descriptionFor name tier
                    MapPos = mapPos
                    GlobePos = globePos
                    LayersPos = layersPos
                    BaseRadiusPx = baseRadPx
                    FillColor = fillCol
                    TotalLoc = loc
                    FnCount = fnCnt
                    BaseVerifiedFrac = vFrac
                    BaseInProgressFrac = wFrac
                    HasArc = hasArc
                    DependsOn = Array.empty
                    UsedByCount = 0
                    PortalDots = portalDots
                    CachedInner = None
                }

        // Populate Tier 0..4
        addRing 0 0.052 0.075 0.155 (tier0Defs |> Array.map fst)
        addRing 1 0.168 0.235 0.265 tier1Names
        addRing 2 0.272 0.365 0.365 tier2Names
        addRing 3 0.362 0.485 0.460 tier3Names
        addRing 4 0.448 0.585 0.545 tier4Names

        // Populate Tiers 5..11 across 13 distinct Poincaré outer sub-rings (`rhoLin = 0.515 .. 0.896`)
        for sub = 0 to outerRingCounts.Length - 1 do
            let cnt = outerRingCounts[sub]
            let tier = min 11 (5 + (sub * 7) / outerRingCounts.Length)
            let tSub = float sub / float (outerRingCounts.Length - 1)
            let rhoLin = 0.515 + tSub * 0.381
            let rBallNorm = 0.655 + tSub * 0.345
            let rArchNorm = 0.618 + tSub * 0.415
            let synthNames =
                Array.init cnt (fun k ->
                    let nIdx = cursor + k
                    let pfx = cEcosystemPrefixes[(nIdx * 7 + sub * 3) % cEcosystemPrefixes.Length]
                    let root = cEcosystemRoots[(nIdx * 13 + k * 5) % cEcosystemRoots.Length]
                    $"{pfx}{root}-{tier}{k % 10}")
            addRing tier rhoLin rBallNorm rArchNorm synthNames

        if cursor <> totalTarget then
            failwith $"Internal dataset count mismatch: expected {totalTarget}, built {cursor}."

        let glibcIdx = 0
        let linuxIdx = 1
        let zlibIdx = projects |> Array.findIndex (fun p -> p.Name = "zlib")
        let opensslIdx = projects |> Array.findIndex (fun p -> p.Name = "openssl")
        let libxml2Idx = projects |> Array.findIndex (fun p -> p.Name = "libxml2")

        // Build exact 12,391 angularly-coherent directed dependencies (without wrapping across Layers' bottom V-cutout!)
        let edgeSet = HashSet<int64>()
        let edges = ResizeArray<DepEdge>(edgeTarget + 32)
        let depsPerProj = Array.init totalTarget (fun _ -> ResizeArray<int>(6))
        let usedByCounts = Array.zeroCreate<int> totalTarget

        let tryAddDep (fromIdx: int) (toIdx: int) (isCoreLong: bool) =
            if fromIdx <> toIdx && edges.Count < edgeTarget then
                let key = (int64 fromIdx <<< 16) ||| int64 toIdx
                if edgeSet.Add key then
                    edges.Add { FromIdx = fromIdx; ToIdx = toIdx; IsCoreLong = isCoreLong }
                    depsPerProj[fromIdx].Add toIdx
                    usedByCounts[toIdx] <- usedByCounts[toIdx] + 1
                    true
                else false
            else false

        // Precompute normalized angle u in [0, 1] from MapPos so neighbors stay angularly local in BOTH Map and Layers!
        let projAngleU =
            projects
            |> Array.map (fun p ->
                let a = Math.Atan2(float p.MapPos.Y, float p.MapPos.X)
                ((a % (2.0 * Math.PI)) + 2.0 * Math.PI) % (2.0 * Math.PI) / (2.0 * Math.PI))

        let pickAngularTarget (uFrom: float) (ringStart: int) (ringCount: int) (offsetSlot: int) =
            let mutable bestIdx = ringStart
            let mutable bestDist = 999.0
            for i = ringStart to ringStart + ringCount - 1 do
                let d = abs (projAngleU[i] - uFrom)
                if d < bestDist then
                    bestDist <- d
                    bestIdx <- i
            let shifted = bestIdx + offsetSlot
            Math.Clamp(shifted, ringStart, ringStart + ringCount - 1)

        // 1) Tier 0 & Tier 1 core dependencies (`glibc` -> `linux`; Tier 1 -> `glibc` / `linux`)
        tryAddDep glibcIdx linuxIdx true |> ignore
        let _, r1Start, r1Count, _ = ringRanges[1]
        for i = r1Start to r1Start + r1Count - 1 do
            tryAddDep i glibcIdx true |> ignore
            if i % 2 = 0 || i = zlibIdx then
                tryAddDep i linuxIdx true |> ignore

        // 2) Ensure `zlib` has exact `UsedByCount = 520` dependents across Tiers 1..11 (matching `&Atlas`!)
        let mutable zlibAdded = 0
        let mutable strideCursor = r1Start + 1
        while zlibAdded < 520 && strideCursor < totalTarget do
            if strideCursor <> zlibIdx then
                if tryAddDep strideCursor zlibIdx true then
                    zlibAdded <- zlibAdded + 1
            strideCursor <- strideCursor + (if strideCursor < 350 then 2 else 7)
        let mutable fillZ = r1Start + 2
        while zlibAdded < 520 && fillZ < totalTarget do
            if fillZ <> zlibIdx && tryAddDep fillZ zlibIdx true then
                zlibAdded <- zlibAdded + 1
            fillZ <- fillZ + 1

        // 3) Angularly-coherent ring-to-previous-ring gossamer web across rings 2..17
        for rIdx = 2 to ringRanges.Count - 1 do
            let _, curStart, curCount, _ = ringRanges[rIdx]
            let _, prev1Start, prev1Count, _ = ringRanges[rIdx - 1]
            let _, prev2Start, prev2Count, _ = ringRanges[max 1 (rIdx - 2)]
            for i = curStart to curStart + curCount - 1 do
                let u = projAngleU[i]
                let t1 = pickAngularTarget u prev1Start prev1Count (((i * 3) % 5) - 2)
                let t2 = pickAngularTarget u prev1Start prev1Count (((i * 7 + 2) % 7) - 3)
                let t3 = pickAngularTarget u prev2Start prev2Count (((i * 5) % 5) - 2)
                if t1 <> zlibIdx then tryAddDep i t1 false |> ignore
                if t2 <> zlibIdx then tryAddDep i t2 false |> ignore
                if i % 2 = 0 && t3 <> zlibIdx then
                    tryAddDep i t3 false |> ignore
                // ~22% also link directly to `glibc` / `linux` / Tier 1 foundational pillars (creating the dandelion core rays in Globe mode!)
                if i % 5 = 0 then
                    let coreT =
                        match (i / 5) % 4 with
                        | 0 -> glibcIdx
                        | 1 -> linuxIdx
                        | 2 -> opensslIdx
                        | _ -> pickAngularTarget u r1Start r1Count 0
                    if coreT <> zlibIdx then
                        tryAddDep i coreT true |> ignore

        // 4) Deterministically top up angularly-local cross-stratum dependencies to hit exact `12,391` edges!
        let mutable pass = 0
        while edges.Count < edgeTarget && pass < 16 do
            pass <- pass + 1
            for rIdx = 2 to ringRanges.Count - 1 do
                if edges.Count < edgeTarget then
                    let _, curStart, curCount, _ = ringRanges[rIdx]
                    let targetRing = max 1 (rIdx - 1 - (pass % min 3 rIdx))
                    let _, tgtStart, tgtCount, _ = ringRanges[targetRing]
                    for i = curStart to curStart + curCount - 1 do
                        if edges.Count < edgeTarget && ((i + pass) % 2 = 0) then
                            let slot = ((i * 11 + pass * 3) % 9) - 4
                            let tgt = pickAngularTarget projAngleU[i] tgtStart tgtCount slot
                            if tgt <> zlibIdx then
                                tryAddDep i tgt (targetRing <= 1) |> ignore

        let edgeArr = edges.ToArray()
        let byProj = Array.init totalTarget (fun _ -> ResizeArray<int>(8))
        for eIdx = 0 to edgeArr.Length - 1 do
            let e = edgeArr[eIdx]
            byProj[e.FromIdx].Add eIdx
            byProj[e.ToIdx].Add eIdx

        for i = 0 to totalTarget - 1 do
            projects[i] <- {
                projects[i] with
                    DependsOn = depsPerProj[i].ToArray()
                    UsedByCount = usedByCounts[i]
            }

        // Top verified/active projects for the Left Rail (`PROJECTS · Most verified`)
        let priorityNames = [|
            "libxml2"; "libevent"; "libwebp"; "freetype"; "cmark-gfm"; "zlib"; "c-ares"; "brotli"
            "cpython"; "xz"; "bzip2"; "zstd"; "nghttp2"; "sqlite"; "ncurses"; "openssl"
            "glibc"; "linux"; "acl"; "file"; "gmp"; "jemalloc"
        |]
        let ranked =
            priorityNames
            |> Array.choose (fun nm -> projects |> Array.tryFindIndex (fun p -> p.Name = nm))

        {
            Projects = projects
            Edges = edgeArr
            ByProjectEdges = byProj |> Array.map (fun r -> r.ToArray())
            RankedVerifiedIds = ranked
            TotalProjects = projects.Length
            TotalDependencies = edgeArr.Length
            BaselineVerifiedLines = 105_912
            BaselineInProgressLines = 194_880
            GlibcIdx = glibcIdx
            LinuxIdx = linuxIdx
            ZlibIdx = zlibIdx
            OpensslIdx = opensslIdx
            Libxml2Idx = libxml2Idx
        }

// ---------------------------------------------------------------------------
// 3. Pure Ranvier.Wgpu Cosmos Engine (LineBatch + DiscBatch ONLY — Zero Spheres!)
// ---------------------------------------------------------------------------

[<Struct>]
type private StarDust = {
    Pos: Vector3
    RadiusPx: float32
    Color: RgbaColor
}

type WgpuAtlasEngine(window: Window, world: AtlasDataset) =
    let ctx = WgpuSdlSurface.CreateContext(window, StageWidth, StageHeight)
    let mutable gpuSurface: WgpuSdlSurface option = None

    let lineBatch = ctx.CreateLineBatch()
    let discBatch = ctx.CreateDiscBatch()
    let lineBatchesArr = [| lineBatch |]
    let discBatchesArr = [| discBatch |]

    // 320 faint deep-space cosmos dust specks on `#070b12` (`PickId = 0u`)
    let starDust =
        let rng = Random(90210)
        Array.init 320 (fun i ->
            let sx = float32 (rng.NextDouble() * 26.0 - 13.0)
            let sy = float32 (rng.NextDouble() * 19.0 - 9.5)
            let sz = -6.5f - float32 (rng.NextDouble() * 9.0)
            let a = 0.08f + float32 (rng.NextDouble() * 0.22)
            let col =
                if i % 5 = 0 then RgbaColor(0.92f, 0.82f, 0.68f, a)
                else RgbaColor(0.76f, 0.84f, 0.96f, a * 0.85f)
            {
                Pos = Vector3(sx, sy, sz)
                RadiusPx = 0.75f + float32 (rng.NextDouble() * 0.85)
                Color = col
            })

    // Pre-allocated buffers for zero-GC-spike frame updates:
    // Each of the 12,391 dependency edges uses 2 lightly-bowed segments (4 LineVertex3D = 49,564 vertices)
    let lineVerts = Array.zeroCreate<LineVertex3D> (world.Edges.Length * 4)
    let worldPositions = Array.zeroCreate<Vector3> world.Projects.Length
    let discScratch = ResizeArray<DiscInstance3D>(world.Projects.Length + starDust.Length + 512)
    let sortKeys = ResizeArray<float32>(world.Projects.Length + starDust.Length + 512)

    let mutable curCam3D = AtlasCamera.toCamera3D false AtlasCamera.mapOverview
    let mutable lastMetrics = { GpuRenderMs = 0.0; ReadbackMs = 0.0; DrawCalls = 0; InstanceCount = 0; TriangleCount = 0 }
    let metricsSig = window.Graph.Run(fun () -> Api.createSignal lastMetrics)
    let mutable totalGpuMs = 0.0
    let mutable totalReadbackMs = 0.0
    let mutable lastDiscCount = 0

    let clearBackdrop = RgbaColor.fromHex "#070b12"
    let portalVerCol = RgbaColor.fromHex "#26d07c"
    let portalWipCol = RgbaColor.fromHex "#fb923c"
    let portalOpenCol = RgbaColor(0.86f, 0.78f, 0.68f, 0.86f)

    let smoothstep (edge0: float32) (edge1: float32) (x: float32) =
        let t = Math.Clamp((x - edge0) / max 1e-5f (edge1 - edge0), 0.0f, 1.0f)
        t * t * (3.0f - 2.0f * t)

    let renderLoadedBatches (surf: WgpuSdlSurface) =
        let m =
            ScenePass.create curCam3D
            |> ScenePass.withClear clearBackdrop
            |> ScenePass.withLines lineBatchesArr
            |> ScenePass.withDiscs discBatchesArr
            |> WgpuSdlSurface.render surf
        lastMetrics <- m
        totalGpuMs <- totalGpuMs + m.GpuRenderMs
        totalReadbackMs <- totalReadbackMs + m.ReadbackMs
        metricsSig.Value <- m

    member _.AdapterName = ctx.AdapterName
    member _.BackendName = ctx.BackendName
    member _.FramesDelivered = ctx.FramesDelivered
    member _.RenderCalls = ctx.RenderCalls
    member _.PresentationMode = gpuSurface |> Option.map (fun s -> s.Mode)
    member _.IsZeroCopyGpu = gpuSurface |> Option.exists (fun s -> s.IsZeroCopyGpu)
    member _.LastMetrics = lastMetrics
    member _.MetricsSig = metricsSig
    member _.DiscCount = lastDiscCount
    member _.LineVertexCount = lineVerts.Length
    member _.AvgGpuRenderMs = if ctx.RenderCalls > 0 then totalGpuMs / float ctx.RenderCalls else 0.0
    member _.AvgReadbackMs = if ctx.RenderCalls > 0 then totalReadbackMs / float ctx.RenderCalls else 0.0
    member _.WorldPositions = worldPositions
    member _.CurrentCamera3D = curCam3D

    member this.AttachSurface
        (
            canvasEl: HTMLCanvasElement,
            cam: AtlasCamera,
            drawerOpen: bool,
            activeProj: int option,
            unfoldedProj: (int * float32) option,
            scrub: float
        ) =
        gpuSurface |> Option.iter WgpuSdlSurface.dispose
        canvasEl.width <- uint32 StageWidth
        canvasEl.height <- uint32 StageHeight
        let surf = canvasEl :> Element |> WgpuSdlSurface.bind window ctx
        gpuSurface <- Some surf
        if lastDiscCount > 0 then
            renderLoadedBatches surf
        else
            this.SyncAndRender(cam, drawerOpen, activeProj, unfoldedProj, scrub)

    member _.DetachSurface() =
        gpuSurface |> Option.iter (fun s -> s.Dispose())
        gpuSurface <- None

    member _.SyncAndRender
        (
            cam: AtlasCamera,
            drawerOpen: bool,
            activeProj: int option,
            unfoldedProj: (int * float32) option,
            scrub: float
        ) =
        curCam3D <- AtlasCamera.toCamera3D drawerOpen cam
        let camPos = curCam3D.Position
        let camFwd = Vector3.Normalize(curCam3D.Target - camPos)
        let fovRad = curCam3D.FovYDeg * MathF.PI / 180.0f
        let tanHalfFov = MathF.Tan(fovRad * 0.5f)
        let zoomF = float32 cam.Zoom

        // Global Stage 3/4 dimming when an unfolded package sub-universe is open on the D5 overlay
        let unfoldIdx, unfoldAlpha =
            match unfoldedProj with
            | Some (idx, a) -> idx, a
            | None -> -1, 0.0f

        let bgRayFade = 1.0f - unfoldAlpha * 0.38f

        // 1) Compute current 3D positions for all 3,459 projects
        for i = 0 to world.Projects.Length - 1 do
            worldPositions[i] <- AtlasWorld.worldPos cam world.Projects[i]

        // 2) Update all 12,391 gossamer dependency hairlines (`LineBatch`)
        let wGlobe = cam.Weights.WGlobe
        let bowScale = (1.0f - wGlobe * 0.72f) * 0.095f
        let actIdx = defaultArg activeProj -1

        for eIdx = 0 to world.Edges.Length - 1 do
            let e = world.Edges[eIdx]
            let pA = worldPositions[e.FromIdx]
            let pB = worldPositions[e.ToIdx]
            let chord = pB - pA
            // Subtle perpendicular bow so Map & Layers weave organic curved filaments
            let perp = Vector3(-chord.Y, chord.X, chord.Z * 0.25f)
            let sign = if ((eIdx &&& 1) = 0) then 1.0f else -1.0f
            let mid = (pA + pB) * 0.5f + perp * (bowScale * sign)

            let isIncident = (actIdx >= 0) && (e.FromIdx = actIdx || e.ToIdx = actIdx) && unfoldAlpha < 0.65f
            let cStart, cEnd =
                if isIncident then
                    if e.FromIdx = actIdx then
                        // Outgoing `DEPENDS ON` edge in bright warm gold -> sky tint
                        RgbaColor(0.98f, 0.84f, 0.64f, 0.44f * bgRayFade), RgbaColor(0.48f, 0.82f, 0.98f, 0.42f * bgRayFade)
                    else
                        // Incoming `USED BY` dependent edge: scale alpha smoothly by 1/sqrt(UsedByCount) so 520-dependent packages glow delicately!
                        let inScale = Math.Clamp(1.6f / MathF.Sqrt(float32 (max 4 world.Projects[actIdx].UsedByCount)), 0.075f, 0.34f) * bgRayFade
                        RgbaColor(0.96f, 0.80f, 0.60f, inScale * 0.82f), RgbaColor(0.99f, 0.88f, 0.72f, inScale * 1.15f)
                elif e.IsCoreLong then
                    // Long core-bound rays (`glibc`/`linux`/`zlib`): gossamer alpha so 1,000+ converging rays glow warmly without saturating!
                    let aCore = (0.028f + wGlobe * 0.016f) * bgRayFade
                    RgbaColor(0.75f, 0.56f, 0.39f, aCore * 0.85f), RgbaColor(0.84f, 0.64f, 0.44f, aCore)
                else
                    // Stratum-to-stratum gossamer copper web
                    let aWeb = (0.082f - wGlobe * 0.008f) * bgRayFade
                    RgbaColor(0.73f, 0.55f, 0.39f, aWeb * 0.85f), RgbaColor(0.80f, 0.62f, 0.45f, aWeb)

            let cMid =
                RgbaColor(
                    (cStart.R + cEnd.R) * 0.5f,
                    (cStart.G + cEnd.G) * 0.5f,
                    (cStart.B + cEnd.B) * 0.5f,
                    (cStart.A + cEnd.A) * 0.5f
                )

            let vBase = eIdx * 4
            lineVerts[vBase] <- LineVertex3D(pA, cStart)
            lineVerts[vBase + 1] <- LineVertex3D(mid, cMid)
            lineVerts[vBase + 2] <- LineVertex3D(mid, cMid)
            lineVerts[vBase + 3] <- LineVertex3D(pB, cEnd)

        lineBatch.UpdateVertices(ReadOnlySpan<LineVertex3D>(lineVerts))

        // 3) Build & back-to-front depth-sort all `DiscInstance3D`s (cosmos stars + 3,459 projects + Stage 2 portal micro-dots)
        discScratch.Clear()
        sortKeys.Clear()

        for i = 0 to starDust.Length - 1 do
            let st = starDust[i]
            discScratch.Add(DiscInstance3D.create st.Pos st.RadiusPx st.Color 0.0f 0.0f 0.0f 0.0f 0u InstanceFlags.IgnoreSectionPlane)
            sortKeys.Add(900.0f + float32 i * 0.001f)

        for i = 0 to world.Projects.Length - 1 do
            let p = world.Projects[i]
            let wp = worldPositions[i]
            let depth = Vector3.Dot(wp - camPos, camFwd)
            if depth > 0.35f then
                let persp = Math.Clamp(RefCamDist / depth, 0.72f, 1.48f)
                let rawR = p.BaseRadiusPx * zoomF * persp
                let radiusPx =
                    if p.Tier >= 5 && cam.Zoom < 1.35 then
                        Math.Clamp(rawR, 1.55f, 3.15f)
                    else
                        Math.Clamp(rawR, 1.6f, 220.0f)

                // Smoothly fade all 3D project discs once Stage 3/4 D5 call-graph takes over (matching `step_85_shot_1_zlib_loaded.png`!)
                let selfFade =
                    if unfoldAlpha > 0.01f then 1.0f - smoothstep 0.12f 0.72f unfoldAlpha
                    else 1.0f

                if selfFade > 0.02f then
                    // Depth atmospheric fade in 3D Globe mode so front hemisphere pops over back hemisphere
                    let globeDepthAlpha =
                        if wGlobe > 0.05f then
                            let relZ = (RefCamDist - depth) / CosmosRadius // [-1..+1]
                            Math.Clamp(0.84f + relZ * 0.16f * wGlobe, 0.58f, 1.0f)
                        else 1.0f

                    let col = p.FillColor.WithAlpha(p.FillColor.A * globeDepthAlpha * selfFade)

                    // Mid-zoom Stage 2 `portalHollow`: large discs (`radiusPx >= 21.5px`, e.g. `glibc` & `linux` at ~2.5x zoom)
                    // become dark translucent rimmed portals revealing miniature inner module constellations!
                    let portalHollow =
                        if p.PortalDots.Length > 0 && i <> unfoldIdx then
                            smoothstep 21.5f 29.0f radiusPx
                        else 0.0f

                    let struct (vFrac, wFrac) = AtlasWorld.scrubbedFracs scrub p
                    let arcThick =
                        if p.HasArc && radiusPx >= 4.4f then
                            Math.Clamp(1.65f + radiusPx * 0.055f, 1.85f, 3.1f)
                        else 0.0f

                    let isSel = (actIdx = i) && unfoldAlpha < 0.35f
                    let flags = if isSel then InstanceFlags.Selected else InstanceFlags.None
                    let mutable dInst =
                        DiscInstance3D.create
                            wp
                            radiusPx
                            col
                            vFrac
                            wFrac
                            arcThick
                            portalHollow
                            (uint32 (i + 1))
                            flags

                    dInst.ArcGapPx <- Math.Clamp(radiusPx * 0.24f, 1.8f, 3.4f)
                    dInst.TrackAlpha <- if portalHollow > 0.2f then 0.52f else 0.38f

                    discScratch.Add dInst
                    sortKeys.Add depth

                    // Emit miniature inner module/function micro-dots inside Stage 2 hollow portals!
                    if portalHollow > 0.12f then
                        let worldPerPx = (2.0f * depth * tanHalfFov) / float32 StageHeight
                        let innerSpanWorld = (radiusPx * 0.80f) * worldPerPx
                        let dotAlpha = portalHollow * selfFade * 0.92f
                        for k = 0 to p.PortalDots.Length - 1 do
                            let md = p.PortalDots[k]
                            let mw =
                                Vector3(
                                    wp.X + md.LocalX * innerSpanWorld,
                                    wp.Y - md.LocalY * innerSpanWorld,
                                    wp.Z + 0.015f
                                )
                            let dotR = Math.Clamp(radiusPx * md.RelRadius, 1.15f, 2.85f)
                            let dCol =
                                match md.StatusKind with
                                | 1uy -> portalVerCol.WithAlpha(dotAlpha)
                                | 2uy -> portalWipCol.WithAlpha(dotAlpha)
                                | _ -> portalOpenCol.WithAlpha(dotAlpha * 0.78f)
                            discScratch.Add(DiscInstance3D.create mw dotR dCol 0.0f 0.0f 0.0f 0.0f 0u InstanceFlags.None)
                            // Sort slightly closer than parent portal (`depth - 0.04f`) so micro-dots draw cleanly on top of portal interior!
                            sortKeys.Add(depth - 0.04f - float32 k * 1e-5f)

        let count = discScratch.Count
        lastDiscCount <- count
        let rawDiscs = CollectionsMarshal.AsSpan discScratch
        let rawKeys = CollectionsMarshal.AsSpan sortKeys
        for idx = 0 to count - 1 do
            rawKeys[idx] <- -rawKeys[idx]
        MemoryExtensions.Sort(rawKeys, rawDiscs)

        discBatch.UpdateDiscs(rawDiscs)

        // 4) Render in 2 GPU draw calls (`lineBatch` + `discBatch`) at 4x MSAA directly into the bound GPU surface!
        match gpuSurface with
        | Some surf -> renderLoadedBatches surf
        | None -> ()

    member _.ProjectToStage(worldPos: Vector3) : Point voption =
        match Camera3D.projectToScreen StageWidth StageHeight curCam3D worldPos with
        | ValueSome v -> ValueSome(Point.create (float v.X) (float v.Y))
        | ValueNone -> ValueNone

    /// Hardware 1x1-scissor GPU picking directly on `DiscBatch`!
    member _.PickProjectGpuAtStage(stageX: float, stageY: float) : (int * Vector3) option =
        let rx = int (Math.Round stageX)
        let ry = int (Math.Round stageY)
        if rx < 0 || rx >= StageWidth || ry < 0 || ry >= StageHeight then
            None
        else
            let pass = ScenePass.create curCam3D |> ScenePass.withDiscs discBatchesArr
            match pass |> WgpuContext.pick rx ry ctx with
            | ValueSome hit when hit.PickId >= 1u && int hit.PickId <= world.Projects.Length ->
                Some (int hit.PickId - 1, hit.WorldPosition)
            | _ -> None

    interface IDisposable with
        member _.Dispose() =
            gpuSurface |> Option.iter (fun s -> s.Dispose())
            gpuSurface <- None
            discBatch.Dispose()
            lineBatch.Dispose()
            ctx.Dispose()

// ---------------------------------------------------------------------------
// 4. 4-Stage Ranvier.D5 SceneBuilder (Quadtree Labels + Unfolded Call-Graph)
// ---------------------------------------------------------------------------

[<Struct>]
type ProjectedLabelCandidate = {
    ProjIdx: int
    Screen: Point
    RadiusPx: float
    OuterRadiusPx: float
    Visible: bool
}

type UnfoldedPackageView = {
    ProjIdx: int
    Center: Point
    RadiusPx: float
    Alpha: float
    Inner: InnerHypergraph
}

type FrameState = {
    Scene: Ranvier.D5.Scene
    Candidates: ProjectedLabelCandidate[]
    Unfolded: UnfoldedPackageView option
    DrawerOpen: bool
    ActiveProjIdx: int option
    ActiveModeText: string
    MarkCount: int
}

module SceneBuilder =
    open Ranvier.D5

    let private svgW = float StageWidth
    let private svgH = float StageHeight
    let private unitCircle = Shape.arc 0.0 1.0 0.0 (2.0 * Math.PI)

    // Exact &Atlas colors
    let private verifiedGreen = Paint.rgb 38 208 124    // #26d07c
    let private inProgressAmber = Paint.rgb 251 146 60  // #fb923c
    let private openSlate = Paint.rgb 100 116 139       // #64748b
    let private ringTrackSlate = Paint.rgb 51 65 85     // #334155
    let private pkgRimCream = Paint.rgb 223 199 170     // #dfc7aa
    let private pkgDarkFill = Paint.rgb 7 11 18         // #070b12
    let private modFillDark = Paint.rgb 13 20 36        // rgba(13, 20, 36, 0.35)
    let private modStrokeSlate = Paint.rgb 148 163 184  // rgba(148, 163, 184, 0.24)
    let private callWebSilvery = Paint.rgb 226 232 240  // Silvery call-graph web
    let private callerBlue = Paint.rgb 56 189 248       // #38bdf8 incoming callers
    let private calleeWhite = Paint.rgb 255 255 255     // #ffffff outgoing callees
    let private labelBright = Paint.rgb 248 250 252     // #f8fafc
    let private labelMuted = Paint.rgb 203 213 225      // #cbd5e1
    let private labelMod = Paint.rgb 148 163 184        // #94a3b8

    let private sansCentered: TextStyle = {
        Family = "Inter, 'Segoe UI', system-ui, sans-serif"
        Weight = 500
        Anchor = "middle"
        Baseline = "alphabetic"
    }

    let private sansBoldCentered: TextStyle = {
        Family = "Inter, 'Segoe UI', system-ui, sans-serif"
        Weight = 700
        Anchor = "middle"
        Baseline = "alphabetic"
    }

    let private monoModStyle: TextStyle = {
        Family = "Consolas, 'JetBrains Mono', ui-monospace, monospace"
        Weight = 500
        Anchor = "middle"
        Baseline = "alphabetic"
    }

    let inline private smoothstepD (e0: float) (e1: float) (x: float) =
        let t = Math.Clamp((x - e0) / max 1e-6 (e1 - e0), 0.0, 1.0)
        t * t * (3.0 - 2.0 * t)

    /// Builds a smooth C1-continuous 2-segment quadratic S-curve (cross-module) or bowed quadratic curve (intra-module).
    let private appendCallBezier (cmds: ResizeArray<PathCommand>) (a: Point) (b: Point) (bendSign: float) (crossModule: bool) =
        let dx = b.X - a.X
        let dy = b.Y - a.Y
        let len = max 1.0 (Double.Hypot(dx, dy))
        let nx = -dy / len
        let ny = dx / len
        if crossModule && len > 28.0 then
            let sway = Math.Clamp(len * 0.16, 8.0, 38.0) * bendSign
            let c1 = Point.create (a.X + dx * 0.28 + nx * sway) (a.Y + dy * 0.28 + ny * sway)
            let c2 = Point.create (a.X + dx * 0.72 - nx * sway * 0.55) (a.Y + dy * 0.72 - ny * sway * 0.55)
            // Exact midpoint of (c1, c2) guarantees C1 tangent continuity across the two QuadraticTo segments!
            let mid = Point.create ((c1.X + c2.X) * 0.5) ((c1.Y + c2.Y) * 0.5)
            cmds.Add(MoveTo a)
            cmds.Add(QuadraticTo(c1, mid))
            cmds.Add(QuadraticTo(c2, b))
        else
            let bow = Math.Clamp(len * 0.18, 4.0, 22.0) * bendSign
            let ctrl = Point.create ((a.X + b.X) * 0.5 + nx * bow) ((a.Y + b.Y) * 0.5 + ny * bow)
            cmds.Add(MoveTo a)
            cmds.Add(QuadraticTo(ctrl, b))

    let build
        (engine: WgpuAtlasEngine)
        (world: AtlasDataset)
        (cam: AtlasCamera)
        (selected: SelectionTarget)
        (hovered: SelectionTarget)
        (scrub: float)
        : FrameState =

        let drawerOpen =
            match selected with
            | SelectionTarget.Project _
            | SelectionTarget.Function _ -> true
            | SelectionTarget.NoSelection -> false

        let visibleRightX = if drawerOpen then svgW - ExplorerWidth else svgW
        let visCenterX = visibleRightX * 0.5
        let visCenterY = svgH * 0.48

        let activeProjHighlight =
            match hovered, selected with
            | SelectionTarget.Project idx, _ -> Some idx
            | SelectionTarget.Function(idx, _), _ -> Some idx
            | _, SelectionTarget.Project idx -> Some idx
            | _, SelectionTarget.Function(idx, _) -> Some idx
            | _ -> None

        // Pre-check Stage 3/4 focused package when `cam.Zoom >= 3.85`
        let prelimCam3D = AtlasCamera.toCamera3D drawerOpen cam
        let prelimFwd = Vector3.Normalize(prelimCam3D.Target - prelimCam3D.Position)

        let unfoldedCandidate =
            if cam.Zoom < 3.85 then
                None
            else
                let uAlpha = smoothstepD 3.85 4.42 cam.Zoom
                // Prefer selected package if it's near viewport center, else closest named Tier 0..4 package
                let checkCount = min world.Projects.Length 166
                let mutable bestIdx = -1
                let mutable bestScore = 1e9
                let selIdx =
                    match selected with
                    | SelectionTarget.Project p
                    | SelectionTarget.Function(p, _) -> p
                    | SelectionTarget.NoSelection -> -1

                for i = 0 to checkCount - 1 do
                    let wp = AtlasWorld.worldPos cam world.Projects[i]
                    match Camera3D.projectToScreen StageWidth StageHeight prelimCam3D wp with
                    | ValueSome sc ->
                        let d = Double.Hypot(float sc.X - visCenterX, float sc.Y - visCenterY)
                        let score = if i = selIdx then d * 0.35 else d
                        if d < 280.0 && score < bestScore then
                            bestScore <- score
                            bestIdx <- i
                    | ValueNone -> ()

                if bestIdx >= 0 then
                    Some (bestIdx, float32 uAlpha)
                else
                    None

        // 1) Synchronize & render the 4x-MSAA Ranvier.Wgpu LineBatch + DiscBatch cosmos!
        engine.SyncAndRender(cam, drawerOpen, activeProjHighlight, unfoldedCandidate, scrub)

        // 2) Project named Tier 0..4 packages (`0..165`) for Ranvier.D5 Quadtree collision-avoided labels
        let cam3D = engine.CurrentCamera3D
        let camPos = cam3D.Position
        let camFwd = Vector3.Normalize(cam3D.Target - camPos)
        let namedCount = min world.Projects.Length 166
        let candidates = Array.zeroCreate<ProjectedLabelCandidate> namedCount

        for i = 0 to namedCount - 1 do
            let p = world.Projects[i]
            let wp = engine.WorldPositions[i]
            let depth = Vector3.Dot(wp - camPos, camFwd)
            match engine.ProjectToStage wp with
            | ValueSome pt when depth > 0.4f ->
                let persp = Math.Clamp(RefCamDist / depth, 0.72f, 1.48f)
                let rPx = float (Math.Clamp(p.BaseRadiusPx * float32 cam.Zoom * persp, 1.6f, 220.0f))
                let gapPx = Math.Clamp(rPx * 0.24, 1.8, 3.4)
                let arcT = if p.HasArc && rPx >= 4.4 then Math.Clamp(1.65 + rPx * 0.055, 1.85, 3.1) else 0.0
                let outR = rPx + (if arcT > 0.0 then gapPx + arcT else 0.0)
                let vis = pt.X >= -60.0 && pt.X <= visibleRightX + 40.0 && pt.Y >= -40.0 && pt.Y <= svgH + 40.0
                candidates[i] <- {
                    ProjIdx = i
                    Screen = pt
                    RadiusPx = rPx
                    OuterRadiusPx = outR
                    Visible = vis
                }
            | _ ->
                candidates[i] <- {
                    ProjIdx = i
                    Screen = Point.create -9999.0 -9999.0
                    RadiusPx = 0.0
                    OuterRadiusPx = 0.0
                    Visible = false
                }

        // Build Ranvier.D5 Quadtree over visible disc centers so labels also avoid colliding with neighboring discs!
        let visibleDiscItems =
            candidates
            |> Array.filter (fun c -> c.Visible)
            |> Array.map (fun c -> c.Screen, c.ProjIdx, c.OuterRadiusPx)
        let discQuadtree = Quadtree.build (fun (pt, _, _) -> pt) (fun (_, _, r) -> r) visibleDiscItems

        let marks = ResizeArray<SceneNode>(320)
        let inline addLeaf m = marks.Add(Leaf m)
        let defs = [ RectClip("vp-clip", Bounds.create 0.0 0.0 svgW svgH) ]

        let maxLabels = if cam.Zoom < 1.6 then 42 elif cam.Zoom < 4.0 then 68 else 48
        let placedBoxes = ResizeArray<Bounds>(maxLabels)

        let tryClaimLabel (ownerIdx: int) (cx: float) (topY: float) (text: string) (fontSize: float) (avoidDiscs: bool) =
            if placedBoxes.Count >= maxLabels then
                false
            else
                let w = float text.Length * fontSize * 0.56 + 7.0
                let h = fontSize + 3.0
                let minX = 10.0
                let maxX = visibleRightX - 10.0
                let maxY = svgH - 46.0 // Leave room above bottom scrubber!
                if cx - w * 0.5 < minX || cx + w * 0.5 > maxX || topY < 8.0 || topY + h > maxY then
                    false
                else
                    let cand = Bounds.create (cx - w * 0.5) topY w h
                    let overlapsLabel =
                        placedBoxes |> Seq.exists (fun b ->
                            Bounds.x cand < Bounds.x b + Bounds.width b
                            && Bounds.x cand + Bounds.width cand > Bounds.x b
                            && Bounds.y cand < Bounds.y b + Bounds.height b
                            && Bounds.y cand + Bounds.height cand > Bounds.y b)
                    if overlapsLabel then
                        false
                    else
                        let hitsNearbyDisc =
                            if not avoidDiscs then false
                            else
                                let midY = topY + h * 0.5
                                let probePts = [|
                                    Point.create (cx - w * 0.32) midY
                                    Point.create cx midY
                                    Point.create (cx + w * 0.32) midY
                                |]
                                probePts
                                |> Array.exists (fun pProbe ->
                                    match Quadtree.nearest pProbe 26.0 discQuadtree with
                                    | Some (dPt, pIdx, outR) ->
                                        pIdx <> ownerIdx
                                        && outR >= 5.2
                                        && dPt.X + outR > cx - w * 0.48
                                        && dPt.X - outR < cx + w * 0.48
                                        && dPt.Y + outR > topY + 1.0
                                        && dPt.Y - outR < topY + h - 1.0
                                    | None -> false)
                        if hitsNearbyDisc then false
                        else
                            placedBoxes.Add cand
                            true

        // 3) Build Stage 3 / Stage 4 Unfolded Package View (if zoomed in)
        let unfoldedView =
            match unfoldedCandidate with
            | Some (uIdx, uAlphaF) ->
                let c = candidates[uIdx]
                if c.Visible then
                    let pkgR = Math.Clamp(54.5 * cam.Zoom, 185.0, 460.0)
                    Some {
                        ProjIdx = uIdx
                        Center = c.Screen
                        RadiusPx = pkgR
                        Alpha = float uAlphaF
                        Inner = AtlasWorld.getInner world uIdx
                    }
                else None
            | None -> None

        let unfoldDimBg =
            match unfoldedView with
            | Some uv -> 1.0 - uv.Alpha * 0.85
            | None -> 1.0

        // 4) Stage 1 & Stage 2 Package Labels (centered directly underneath discs, matching `final_step_55.png` & `final_step_100.png`!)
        if unfoldDimBg > 0.16 then
            let sortedForLabels =
                candidates
                |> Array.filter (fun c -> c.Visible)
                |> Array.sortByDescending (fun c ->
                    let p = world.Projects[c.ProjIdx]
                    let selBoost = if activeProjHighlight = Some c.ProjIdx then 1000.0 else 0.0
                    let tierBoost = float (max 0 (4 - p.Tier)) * 18.0
                    let arcBoost = if p.HasArc then 9.0 else 0.0
                    selBoost + tierBoost + arcBoost + c.RadiusPx * 2.5)

            for c in sortedForLabels do
                let isUnfoldedSelf =
                    match unfoldedView with
                    | Some uv -> uv.ProjIdx = c.ProjIdx
                    | None -> false
                if not isUnfoldedSelf then
                    let p = world.Projects[c.ProjIdx]
                    let isHi = activeProjHighlight = Some c.ProjIdx
                    let minR = if cam.Zoom < 1.5 then 5.3 elif cam.Zoom < 2.8 then 4.6 else 3.8
                    if isHi || p.Tier = 0 || c.RadiusPx >= minR then
                        let fSize =
                            if p.Tier = 0 then Math.Clamp(11.0 + cam.Zoom * 0.7, 11.2, 13.5)
                            elif isHi then 11.8
                            else Math.Clamp(10.2 + c.RadiusPx * 0.08, 10.4, 12.2)
                        let baselineY = c.Screen.Y + c.OuterRadiusPx + fSize + 3.5
                        if tryClaimLabel c.ProjIdx c.Screen.X (baselineY - fSize) p.Name fSize (not isHi && p.Tier > 0) then
                            let col = if isHi || p.Tier <= 1 then labelBright else labelMuted
                            let style = if isHi || p.Tier = 0 then sansBoldCentered else sansCentered
                            addLeaf (
                                Mark.text $"pkg-lbl/{c.ProjIdx}" (Point.create c.Screen.X baselineY) p.Name
                                |> Mark.withFontSize fSize
                                |> Mark.withFill col
                                |> Mark.withTextStyle style
                                |> Mark.withOpacity (Math.Clamp(unfoldDimBg * 0.94, 0.15, 0.98))
                            )

        // 5) Stage 3 & Stage 4: Unfolded Focused Package Sub-Universe (`zlib`, `libxml2`, `glibc`, etc.)
        match unfoldedView with
        | None -> ()
        | Some uv ->
            let pkg = world.Projects[uv.ProjIdx]
            let inner = uv.Inner
            let center = uv.Center
            let pkgR = uv.RadiusPx
            let alpha = uv.Alpha

            // 5a) Dark `#070b12` package disc + crisp `#dfc7aa` champagne inner boundary circle
            addLeaf (
                unitCircle
                |> Mark.path $"u-bg/{uv.ProjIdx}"
                |> Mark.withTransform (Transform.create pkgR 0.0 0.0 pkgR center.X center.Y)
                |> Mark.withFill pkgDarkFill
                |> Mark.withStroke pkgRimCream (1.25 / max 1.0 pkgR)
                |> Mark.withOpacity (alpha * 0.97)
                |> Mark.withLabel (Some pkg.Name)
            )

            // 5b) Outer clockwise verification progress ring (`#26d07c` green -> `#fb923c` amber -> `#334155` slate track)
            let ringInner = pkgR + 4.8
            let ringOuter = ringInner + Math.Clamp(pkgR * 0.017, 4.0, 5.6)
            let struct (vFrac, wFrac) = AtlasWorld.scrubbedFracs scrub pkg
            let verSweep = float vFrac * 2.0 * Math.PI
            let wipSweep = float wFrac * 2.0 * Math.PI

            addLeaf (
                Shape.arc ringInner ringOuter 0.0 (2.0 * Math.PI)
                |> Mark.path $"u-track/{uv.ProjIdx}"
                |> Mark.withTransform (Transform.translate center.X center.Y)
                |> Mark.withFill ringTrackSlate
                |> Mark.withOpacity (alpha * 0.56)
            )
            if verSweep > 0.015 then
                addLeaf (
                    Shape.arc ringInner ringOuter 0.0 verSweep
                    |> Mark.path $"u-ver/{uv.ProjIdx}"
                    |> Mark.withTransform (Transform.translate center.X center.Y)
                    |> Mark.withFill verifiedGreen
                    |> Mark.withOpacity (alpha * 0.96)
                )
            if wipSweep > 0.015 then
                addLeaf (
                    Shape.arc ringInner ringOuter verSweep (min (2.0 * Math.PI) (verSweep + wipSweep))
                    |> Mark.path $"u-wip/{uv.ProjIdx}"
                    |> Mark.withTransform (Transform.translate center.X center.Y)
                    |> Mark.withFill inProgressAmber
                    |> Mark.withOpacity (alpha * 0.95)
                )

            // Package title centered cleanly below bottom rim (matching `step_85_shot_1_zlib_loaded.png`!)
            let titleY = center.Y + ringOuter + 21.0
            if titleY < svgH - 36.0 then
                addLeaf (
                    Mark.text $"u-title/{uv.ProjIdx}" (Point.create center.X titleY) pkg.Name
                    |> Mark.withFontSize 15.5
                    |> Mark.withFill labelBright
                    |> Mark.withTextStyle sansBoldCentered
                    |> Mark.withOpacity alpha
                )

            let mapLocal (pt: Point) =
                Point.create (center.X + pt.X * pkgR) (center.Y + pt.Y * pkgR)

            let activeFn =
                match hovered, selected with
                | SelectionTarget.Function(p, f), _ when p = uv.ProjIdx -> Some f
                | _, SelectionTarget.Function(p, f) when p = uv.ProjIdx -> Some f
                | _ -> None

            let activeModIdx =
                activeFn
                |> Option.bind (fun f ->
                    if f >= 0 && f < inner.Functions.Length then Some inner.Functions[f].ModuleIdx
                    else None)

            // 5c) Nested C source file circles (`Ranvier.D5.Pack.layout`) — delicate thin slate hairline rings ONLY!
            for mIdx = 0 to inner.Modules.Length - 1 do
                let m = inner.Modules[mIdx]
                let mc = mapLocal m.Center
                let mr = max 8.0 (m.Radius * pkgR)
                let isModActive = activeModIdx = Some mIdx

                // Subtle dark interior tint (`rgba(13, 20, 36, 0.35)`)
                addLeaf (
                    unitCircle
                    |> Mark.path $"mod-bg/{uv.ProjIdx}/{mIdx}"
                    |> Mark.withTransform (Transform.create mr 0.0 0.0 mr mc.X mc.Y)
                    |> Mark.withFill modFillDark
                    |> Mark.withStroke Paint.none 0.0
                    |> Mark.withOpacity (alpha * 0.36)
                )
                // Crisp thin hairline ring (`rgba(148, 163, 184, 0.24)`)
                addLeaf (
                    unitCircle
                    |> Mark.path $"mod-ring/{uv.ProjIdx}/{mIdx}"
                    |> Mark.withTransform (Transform.create mr 0.0 0.0 mr mc.X mc.Y)
                    |> Mark.withFill Paint.none
                    |> Mark.withStroke modStrokeSlate ((if isModActive then 1.35 else 1.0) / mr)
                    |> Mark.withOpacity (alpha * (if isModActive then 0.48 else 0.25))
                )

                if mr >= 28.0 then
                    let mLabelY = mc.Y - mr + 13.0
                    addLeaf (
                        Mark.text $"mod-lbl/{uv.ProjIdx}/{mIdx}" (Point.create mc.X mLabelY) m.FileName
                        |> Mark.withFontSize (if isModActive then 10.5 else 9.8)
                        |> Mark.withFill (if isModActive then labelMuted else labelMod)
                        |> Mark.withTextStyle monoModStyle
                        |> Mark.withOpacity (alpha * (if isModActive then 0.92 else 0.72))
                    )

            // 5d) Smooth Bézier caller–callee web (`#38bdf8` blue incoming callers + `#ffffff` white outgoing callees when a function is selected!)
            let webCmds = ResizeArray<PathCommand>(inner.Calls.Length * 3)
            let callerCmds = ResizeArray<PathCommand>(32)
            let calleeCmds = ResizeArray<PathCommand>(32)
            let callerSet = HashSet<int>()
            let calleeSet = HashSet<int>()

            for c in inner.Calls do
                let pA = mapLocal inner.Functions[c.Caller].LocalPos
                let pB = mapLocal inner.Functions[c.Callee].LocalPos
                match activeFn with
                | Some selF when c.Callee = selF ->
                    callerSet.Add c.Caller |> ignore
                    appendCallBezier callerCmds pA pB c.BendSign c.CrossModule
                | Some selF when c.Caller = selF ->
                    calleeSet.Add c.Callee |> ignore
                    appendCallBezier calleeCmds pA pB c.BendSign c.CrossModule
                | _ ->
                    appendCallBezier webCmds pA pB c.BendSign c.CrossModule

            if webCmds.Count > 0 then
                let webAlpha = match activeFn with Some _ -> alpha * 0.065 | None -> alpha * 0.165
                addLeaf (
                    Path.ofCommands (List.ofSeq webCmds)
                    |> Mark.path $"calls-web/{uv.ProjIdx}"
                    |> Mark.withFill Paint.none
                    |> Mark.withStroke callWebSilvery 0.95
                    |> Mark.withOpacity webAlpha
                )

            if callerCmds.Count > 0 then
                addLeaf (
                    Path.ofCommands (List.ofSeq callerCmds)
                    |> Mark.path $"calls-in/{uv.ProjIdx}"
                    |> Mark.withFill Paint.none
                    |> Mark.withStroke callerBlue 1.85
                    |> Mark.withOpacity (alpha * 0.95)
                )

            if calleeCmds.Count > 0 then
                addLeaf (
                    Path.ofCommands (List.ofSeq calleeCmds)
                    |> Mark.path $"calls-out/{uv.ProjIdx}"
                    |> Mark.withFill Paint.none
                    |> Mark.withStroke calleeWhite 1.85
                    |> Mark.withOpacity (alpha * 0.96)
                )

            // 5e) C Function Dots (`#26d07c` verified, `#fb923c` in-progress, `#64748b` open) + Stage 4 Halo & Labels
            let zoomDotScale = Math.Clamp(pkgR / 285.0, 0.78, 1.45)
            let fnLabelQueue = ResizeArray<int * Point * float * string * int>() // priority: 2 = selected, 1 = caller/callee, 0 = general

            for fIdx = 0 to inner.Functions.Length - 1 do
                let fn = inner.Functions[fIdx]
                let fp = mapLocal fn.LocalPos
                let fr = Math.Clamp(fn.DotRadiusPx * zoomDotScale, 2.5, 8.2)
                let isSelFn = activeFn = Some fIdx
                let isCaller = callerSet.Contains fIdx
                let isCallee = calleeSet.Contains fIdx
                let isIncident = isSelFn || isCaller || isCallee

                let st = AtlasWorld.scrubbedFnStatus scrub fn
                let fill =
                    match st with
                    | ProofStatus.Verified -> verifiedGreen
                    | ProofStatus.InProgress -> inProgressAmber
                    | ProofStatus.Open -> openSlate

                let dotOp =
                    match activeFn with
                    | Some _ when not isIncident -> alpha * 0.54
                    | _ -> alpha * 0.98

                let strokePaint, strokeW =
                    if isSelFn then calleeWhite, 1.8 / fr
                    elif isCaller then callerBlue, 1.35 / fr
                    elif isCallee then calleeWhite, 1.35 / fr
                    else pkgDarkFill, 0.75 / fr

                addLeaf (
                    unitCircle
                    |> Mark.path $"fn/{uv.ProjIdx}/{fIdx}"
                    |> Mark.withTransform (Transform.create fr 0.0 0.0 fr fp.X fp.Y)
                    |> Mark.withFill fill
                    |> Mark.withStroke strokePaint strokeW
                    |> Mark.withOpacity dotOp
                    |> Mark.withLabel (Some $"{pkg.Name}::{fn.Name}")
                )

                if isSelFn then
                    let halo1 = fr + 3.4
                    let halo2 = fr + 6.6
                    addLeaf (
                        unitCircle
                        |> Mark.path $"fn-halo1/{uv.ProjIdx}/{fIdx}"
                        |> Mark.withTransform (Transform.create halo1 0.0 0.0 halo1 fp.X fp.Y)
                        |> Mark.withFill Paint.none
                        |> Mark.withStroke calleeWhite (1.55 / halo1)
                        |> Mark.withOpacity alpha
                    )
                    addLeaf (
                        unitCircle
                        |> Mark.path $"fn-halo2/{uv.ProjIdx}/{fIdx}"
                        |> Mark.withTransform (Transform.create halo2 0.0 0.0 halo2 fp.X fp.Y)
                        |> Mark.withFill Paint.none
                        |> Mark.withStroke callerBlue (1.15 / halo2)
                        |> Mark.withOpacity (alpha * 0.82)
                    )

                if isSelFn then
                    fnLabelQueue.Add(fIdx, fp, fr + 6.0, fn.Name, 2)
                elif isCaller || isCallee then
                    fnLabelQueue.Add(fIdx, fp, fr + 2.0, fn.Name, 1)
                elif cam.Zoom >= 7.2 && fIdx % 3 = 0 then
                    fnLabelQueue.Add(fIdx, fp, fr + 1.5, fn.Name, 0)

            fnLabelQueue.Sort(fun (_, _, _, _, pA) (_, _, _, _, pB) -> compare pB pA)
            for (fIdx, fp, padR, fnName, prio) in fnLabelQueue do
                let fSize = if prio = 2 then 11.8 else 9.8
                let by = fp.Y + padR + fSize + 1.5
                if tryClaimLabel -1 fp.X (by - fSize) fnName fSize false then
                    let lCol =
                        if prio = 2 then calleeWhite
                        elif callerSet.Contains fIdx then callerBlue
                        else labelBright
                    addLeaf (
                        Mark.text $"fn-lbl/{uv.ProjIdx}/{fIdx}" (Point.create fp.X by) fnName
                        |> Mark.withFontSize fSize
                        |> Mark.withFill lCol
                        |> Mark.withTextStyle (if prio = 2 then sansBoldCentered else sansCentered)
                        |> Mark.withOpacity alpha
                    )

        let rootGroup = Group({ GroupSpec.create "viewport" with Clip = Some "vp-clip" }, List.ofSeq marks)
        let scene = Scene.compose { Width = svgW; Height = svgH } "&Atlas Ranvier.Wgpu + Ranvier.D5" defs [ rootGroup ]

        let modeName =
            match cam.Mode with
            | AtlasMode.Map -> "Map"
            | AtlasMode.Globe -> "Globe"
            | AtlasMode.Layers -> "Layers"

        let activeText, activeIdx =
            match unfoldedView with
            | Some uv -> $"{modeName} · {world.Projects[uv.ProjIdx].Name} ({cam.Zoom:F1}×)", Some uv.ProjIdx
            | None -> $"{modeName} · {cam.Zoom:F1}×", activeProjHighlight

        {
            Scene = scene
            Candidates = candidates
            Unfolded = unfoldedView
            DrawerOpen = drawerOpen
            ActiveProjIdx = activeIdx
            ActiveModeText = activeText
            MarkCount = marks.Count
        }

    /// Unified Stage 1–4 Hit-Testing:
    /// - In Stage 3/4, uses Ranvier.D5 `Quadtree` to pick inner C functions (`SelectionTarget.Function`) or the unfolded package circle.
    /// - In Stage 1/2, delegates directly to `Ranvier.Wgpu`'s hardware 1x1-scissor `DiscBatch` GPU pick (`PickProjectGpuAtStage`)!
    let pickAt (engine: WgpuAtlasEngine) (frame: FrameState) (pt: Point) : SelectionTarget =
        match frame.Unfolded with
        | Some uv when Double.Hypot(pt.X - uv.Center.X, pt.Y - uv.Center.Y) <= uv.RadiusPx + 10.0 ->
            let zoomDotScale = Math.Clamp(uv.RadiusPx / 285.0, 0.78, 1.45)
            let fnItems =
                uv.Inner.Functions
                |> Array.mapi (fun fIdx fn ->
                    let fp = Point.create (uv.Center.X + fn.LocalPos.X * uv.RadiusPx) (uv.Center.Y + fn.LocalPos.Y * uv.RadiusPx)
                    let hitR = Math.Clamp(fn.DotRadiusPx * zoomDotScale, 3.0, 9.0) + 4.5
                    fp, fIdx, hitR)
            let fnTree = Quadtree.build (fun (fp, _, _) -> fp) (fun (_, _, r) -> r) fnItems
            match Quadtree.nearest pt 18.0 fnTree with
            | Some (fp, fIdx, hitR) when Double.Hypot(pt.X - fp.X, pt.Y - fp.Y) <= hitR ->
                SelectionTarget.Function(uv.ProjIdx, fIdx)
            | _ ->
                SelectionTarget.Project uv.ProjIdx
        | _ ->
            match engine.PickProjectGpuAtStage(pt.X, pt.Y) with
            | Some (idx, _) -> SelectionTarget.Project idx
            | None -> SelectionTarget.NoSelection

// ---------------------------------------------------------------------------
// 5. &Atlas UI Chrome: Top Milestone Bar, Left Rail, Right C/Lean Explorer,
//    Floating Map Controls & Verification Replay Scrubber
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess; Struct>]
type CodeTab =
    | Split
    | CSource
    | Lean4

type AtlasSession = {
    World: AtlasDataset
    Engine: WgpuAtlasEngine
    CameraSig: Signal<AtlasCamera>
    SelectedSig: Signal<SelectionTarget>
    HoveredSig: Signal<SelectionTarget>
    ScrubSig: Signal<float>
    PlayingSig: Signal<bool>
    CodeTabSig: Signal<CodeTab>
    FrameMemo: Memo<FrameState>
    StartFlight: AtlasCamera -> unit
    FocusProject: int -> float -> bool -> unit
    SwitchMode: AtlasMode -> unit
    StepClock: unit -> unit
    ZoomStep: Point -> float -> unit
}

let private appCss = """
*, *::before, *::after {
    box-sizing: border-box;
    -webkit-user-select: none;
    user-select: none;
    -webkit-user-drag: none;
}
*::selection { background: transparent; color: inherit; }
*::-webkit-selection { background: transparent; color: inherit; }
html, body {
    margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden;
    background: #070b12; color: #e2e8f0;
    font-family: Inter, 'Segoe UI', system-ui, -apple-system, sans-serif;
    -webkit-user-select: none;
    user-select: none;
}
.shell { display: flex; flex-direction: column; width: 1360px; height: 860px; background: #070b12; }
.topbar {
    height: 46px; display: flex; align-items: center; justify-content: space-between;
    padding: 0 14px; background: #080d16; border-bottom: 1px solid #162032; gap: 14px;
}
.brand-row { display: flex; align-items: center; gap: 10px; }
.wordmark { font-size: 17px; font-weight: 700; color: #f8fafc; letter-spacing: -0.2px; }
.wordmark-amp { color: #d9b38c; font-weight: 500; margin-right: 1px; }
.bug-pill {
    display: flex; align-items: center; gap: 5px; padding: 2px 8px; border-radius: 999px;
    background: #111a2b; border: 1px solid #24324a; font-size: 11px; font-weight: 600; color: #cbd5e1;
}
.bug-dot { width: 6px; height: 6px; border-radius: 50%; background: #fb923c; }
.scale-meta { font-size: 11.5px; color: #94a3b8; }

.milestone-box { display: flex; flex-direction: column; gap: 4px; width: 430px; }
.milestone-top { display: flex; justify-content: space-between; font-size: 11.5px; color: #94a3b8; }
.c-ver { color: #26d07c; font-weight: 600; }
.c-wip { color: #fb923c; font-weight: 600; }
.ms-track { height: 5px; background: #162032; border-radius: 999px; overflow: hidden; display: flex; }
.ms-ver { background: #26d07c; height: 100%; }
.ms-wip { background: #fb923c; height: 100%; }

.gpu-pill {
    font-size: 11px; font-weight: 600; padding: 4px 10px; border-radius: 999px;
    background: #0c1424; border: 1px solid #1e2d45; color: #94a3b8;
}

.workspace { display: flex; flex: 1; height: 814px; overflow: hidden; }
.left-rail {
    width: 248px; background: #080c14; border-right: 1px solid #162032;
    display: flex; flex-direction: column; padding: 10px; gap: 10px;
}
.mode-tabs {
    display: flex; background: #0c1322; border: 1px solid #1a273d; border-radius: 7px; padding: 2px; gap: 2px;
}
.mode-tab {
    flex: 1; background: transparent; border: none; color: #94a3b8; font-size: 12px; font-weight: 600;
    padding: 5px 0; border-radius: 5px; cursor: pointer;
}
.mode-tab:hover { color: #e2e8f0; }
.mode-tab.active { background: #19263c; color: #f8fafc; }

.preset-row { display: flex; flex-wrap: wrap; gap: 4px; }
.preset-chip {
    background: #0d1526; border: 1px solid #1d2b44; color: #cbd5e1;
    font-size: 10.5px; font-weight: 600; padding: 3px 7px; border-radius: 5px; cursor: pointer;
}
.preset-chip:hover { background: #16233b; border-color: #38bdf8; color: #f8fafc; }

.rail-hdr {
    display: flex; justify-content: space-between; align-items: center;
    font-size: 10.5px; font-weight: 700; letter-spacing: 0.6px; color: #64748b; text-transform: uppercase;
    padding: 2px 2px 0 2px;
}
.rail-sort { color: #94a3b8; font-weight: 500; text-transform: none; letter-spacing: 0; }
.proj-list {
    flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 3px; padding-right: 2px;
}
.proj-row {
    padding: 5px 7px; border-radius: 6px; background: #0b111d; border: 1px solid transparent; cursor: pointer;
    display: flex; flex-direction: column; gap: 4px;
}
.proj-row:hover { background: #111b2e; border-color: #1e2f4d; }
.proj-row.selected { background: #132038; border-color: #38bdf8; }
.proj-row-top { display: flex; justify-content: space-between; align-items: baseline; font-size: 12px; }
.proj-name { font-weight: 600; color: #e2e8f0; }
.proj-loc { font-size: 10.5px; color: #64748b; }
.mini-track { height: 3px; background: #162032; border-radius: 2px; overflow: hidden; display: flex; }

.stage {
    position: relative; width: 1112px; height: 814px; overflow: hidden; background: #070b12;
    touch-action: none;
}
.atlas-canvas {
    position: absolute; left: 0; top: 0; width: 1112px; height: 814px; pointer-events: none;
    -webkit-user-select: none; user-select: none; -webkit-user-drag: none;
}
.d5-overlay {
    position: absolute; left: 0; top: 0; width: 1112px; height: 814px;
    touch-action: none;
}
.d5-overlay svg { display: block; width: 1112px; height: 814px; cursor: grab; touch-action: none; }
.d5-overlay svg:active { cursor: grabbing; }
.d5-overlay svg text { paint-order: stroke fill; stroke: rgba(7, 11, 18, 0.88); stroke-width: 3px; stroke-linejoin: round; }

.map-ctrls {
    position: absolute; bottom: 50px; right: 16px; display: flex; flex-direction: column; gap: 4px;
}
.map-ctrls.shifted { right: 386px; }
.ctrl-btn {
    width: 30px; height: 30px; border-radius: 6px; background: rgba(12, 19, 34, 0.90);
    border: 1px solid #24344f; color: #e2e8f0; font-size: 14px; font-weight: 600; cursor: pointer;
    display: flex; align-items: center; justify-content: center;
}
.ctrl-btn:hover { background: #1a2942; border-color: #38bdf8; }

.scrubber-bar {
    position: absolute; bottom: 10px; left: 16px; height: 30px; padding: 0 10px;
    background: rgba(10, 16, 29, 0.90); border: 1px solid #1c2b45; border-radius: 999px;
    display: flex; align-items: center; gap: 8px; font-size: 11px; color: #cbd5e1;
}
.scrub-btn {
    background: #131f36; border: 1px solid #293d5e; color: #f8fafc; border-radius: 999px;
    padding: 2px 8px; font-size: 10.5px; font-weight: 600; cursor: pointer;
}
.scrub-btn.active { background: #1d3557; border-color: #38bdf8; color: #38bdf8; }
.scrub-track { width: 110px; height: 5px; background: #172338; border-radius: 999px; overflow: hidden; }
.scrub-fill { height: 100%; background: linear-gradient(90deg, #fb923c, #26d07c); }

.explorer {
    position: absolute; top: 0; right: 0; width: 370px; height: 814px;
    background: rgba(9, 14, 24, 0.96); border-left: 1px solid #1b2840;
    display: flex; flex-direction: column; padding: 14px; gap: 11px; overflow-y: auto;
}
.explorer.hidden { display: none; }
.exp-top { display: flex; justify-content: space-between; align-items: center; }
.exp-title { font-size: 18px; font-weight: 700; color: #f8fafc; }
.exp-close {
    background: transparent; border: 1px solid #24344f; color: #94a3b8;
    width: 24px; height: 24px; border-radius: 5px; cursor: pointer; font-size: 13px;
}
.exp-sec-lbl {
    font-size: 10px; font-weight: 700; letter-spacing: 0.7px; color: #64748b; text-transform: uppercase;
}
.exp-desc { font-size: 12px; color: #cbd5e1; line-height: 1.42; }

.exp-prog-bar { height: 7px; background: #1e293b; border-radius: 4px; overflow: hidden; display: flex; }
.exp-stats-grid { display: flex; justify-content: space-between; gap: 6px; }
.stat-col {
    flex: 1; background: #0d1526; border: 1px solid #18253d; border-radius: 6px; padding: 6px 7px;
    display: flex; flex-direction: column; gap: 2px;
}
.stat-head { font-size: 10px; font-weight: 600; display: flex; align-items: center; gap: 4px; }
.stat-val { font-size: 12.5px; font-weight: 700; color: #f8fafc; }
.stat-sub { font-size: 10px; color: #64748b; }

.spark-box {
    background: #0b1220; border: 1px solid #162238; border-radius: 6px; padding: 6px 8px;
    display: flex; flex-direction: column; gap: 4px;
}
.spark-bars { display: flex; align-items: flex-end; height: 34px; gap: 3px; }
.spark-col { flex: 1; display: flex; flex-direction: column; justify-content: flex-end; height: 100%; background: #111a2c; border-radius: 2px; overflow: hidden; }

.meta-row { display: flex; justify-content: space-between; align-items: center; font-size: 11.5px; color: #94a3b8; gap: 6px; }
.dep-pills { display: flex; flex-wrap: wrap; gap: 4px; }
.dep-pill {
    background: #111c30; border: 1px solid #223454; color: #cbd5e1; font-size: 10.5px;
    padding: 2px 7px; border-radius: 4px; cursor: pointer;
}
.dep-pill:hover { border-color: #38bdf8; color: #f8fafc; }

.crumb-bar {
    display: flex; justify-content: space-between; align-items: center;
    background: #0d1526; border: 1px solid #1c2b45; border-radius: 6px; padding: 5px 8px; font-size: 11.5px;
}
.crumb-path { color: #e2e8f0; font-weight: 600; font-family: Consolas, ui-monospace, monospace; font-size: 11px; }
.tab-grp { display: flex; gap: 3px; }
.code-tab {
    background: transparent; border: 1px solid transparent; color: #94a3b8;
    font-size: 10.5px; font-weight: 600; padding: 2px 6px; border-radius: 4px; cursor: pointer;
}
.code-tab.active { background: #1a2a44; border-color: #2e476e; color: #f8fafc; }

.fn-banner {
    display: flex; justify-content: space-between; align-items: center; font-size: 11px; color: #94a3b8;
}
.edge-legend { display: flex; gap: 10px; font-size: 10.5px; color: #cbd5e1; }
.dot-caller { color: #38bdf8; font-weight: 700; }
.dot-callee { color: #ffffff; font-weight: 700; }

.code-pane {
    font-family: Consolas, 'JetBrains Mono', ui-monospace, monospace; font-size: 10.6px; line-height: 1.38;
    background: #060a12; border: 1px solid #19263d; border-radius: 6px; padding: 8px 9px;
    color: #dbeafe; white-space: pre-wrap; overflow-x: hidden;
}
.lean-pane {
    font-family: Consolas, 'JetBrains Mono', ui-monospace, monospace; font-size: 10.6px; line-height: 1.38;
    background: #071019; border: 1px solid #1b3548; border-radius: 6px; padding: 8px 9px;
    color: #a7f3d0; white-space: pre-wrap; overflow-x: hidden;
}
"""

let private fmtK (n: int) =
    if n >= 1_000_000 then sprintf "%.2fM" (float n / 1_000_000.0)
    elif n >= 1_000 then sprintf "%.1fK" (float n / 1_000.0)
    else string n

let private mountAtlas (window: Window) : AtlasSession * IDisposable =
    let host = window.Host
    let graph = window.Graph
    let doc = host.Document
    let styleEl = doc.createElement "style"
    styleEl.textContent <- appCss
    doc.head.appendChild styleEl |> ignore

    let container = doc.createElement "div"
    doc.body.appendChild container |> ignore

    let world = AtlasWorld.create ()
    let engine = new WgpuAtlasEngine(window, world)

    let easeOutCubic = Ranvier.Anime.Ease.named "outCubic"
    let mutable flightOpt: Transition<AtlasCamera> option = None
    let sw = Stopwatch.StartNew()
    let mutable lastPlayMs = 0.0

    let camSig, selSig, hovSig, scrubSig, playSig, codeTabSig, frameMemo =
        graph.Run (fun () ->
            let camS = Api.createSignal AtlasCamera.mapOverview
            let selS = Api.createSignal SelectionTarget.NoSelection
            let hovS = Api.createSignal SelectionTarget.NoSelection
            let scrS = Api.createSignal AtlasWorld.DefaultScrub
            let plyS = Api.createSignal false
            let tabS = Api.createSignal CodeTab.Split
            let fMemo =
                Reactive.derive
                    (fun () -> camS.Value, selS.Value, hovS.Value, scrS.Value)
                    (fun (cam, sel, hov, scr) -> SceneBuilder.build engine world cam sel hov scr)
            camS, selS, hovS, scrS, plyS, tabS, fMemo)

    let startFlight (target: AtlasCamera) =
        let nowMs = sw.Elapsed.TotalMilliseconds
        let cur = camSig.Peek
        flightOpt <-
            Some (
                match flightOpt with
                | Some seg -> Transition.retarget nowMs target seg
                | None -> Transition.start nowMs 540.0 easeOutCubic AtlasCamera.lerp cur target
            )

    /// Computes exact camera target that centers project `projIdx` in the visible stage (accounting for right Explorer drawer!).
    let makeFocusedCamera (cur: AtlasCamera) (projIdx: int) (targetZoom: float) (drawerOpen: bool) : AtlasCamera =
        let baseCam =
            match cur.Mode with
            | AtlasMode.Map -> { cur with Q = AtlasCamera.mapOverview.Q; Weights = ModeWeights.ofMode AtlasMode.Map }
            | _ -> cur
        let wp = AtlasWorld.worldPos baseCam world.Projects[projIdx]
        let depth = max 2.0f (RefCamDist - wp.Z)
        let drawerShiftAtZero =
            if drawerOpen then float32 (ExplorerWidth * 0.5) * AtlasCamera.worldUnitsPerPixelAtZero targetZoom
            else 0.0f
        // Perspective depth correction so dome Z (`wp.Z`) lands at exact center of unobstructed left viewport!
        let depthFactor = depth / RefCamDist
        let panX = wp.X + drawerShiftAtZero * (depthFactor - 1.0f)
        let panY = wp.Y
        {
            baseCam with
                Zoom = targetZoom
                PanWorldX = panX
                PanWorldY = panY
        }

    let focusProject (projIdx: int) (targetZoom: float) (selectDefaultFn: bool) =
        graph.Run (fun () ->
            if selectDefaultFn then
                let _ = AtlasWorld.getInner world projIdx
                selSig.Value <- SelectionTarget.Function(projIdx, 0)
            else
                selSig.Value <- SelectionTarget.Project projIdx
            let nextCam = makeFocusedCamera camSig.Peek projIdx targetZoom true
            startFlight nextCam)

    let switchMode (mode: AtlasMode) =
        graph.Run (fun () ->
            selSig.Value <- SelectionTarget.NoSelection
            let target =
                match mode with
                | AtlasMode.Map -> AtlasCamera.mapOverview
                | AtlasMode.Globe -> { AtlasCamera.globeOverview with Q = AtlasCamera.globeShowcaseRotation }
                | AtlasMode.Layers -> AtlasCamera.layersOverview
            startFlight target)

    let stepClock () =
        let nowMs = sw.Elapsed.TotalMilliseconds
        match flightOpt with
        | Some tr ->
            let samp = Transition.sample nowMs tr
            graph.Run (fun () -> camSig.Value <- samp)
            let fin = Transition.sample (nowMs + 100_000.0) tr
            if abs (samp.Zoom - fin.Zoom) < 1e-3
               && abs (samp.PanWorldX - fin.PanWorldX) < 1e-4f
               && abs (samp.PanWorldY - fin.PanWorldY) < 1e-4f
               && abs (samp.Weights.WMap - fin.Weights.WMap) < 1e-3f
               && abs (samp.Weights.WGlobe - fin.Weights.WGlobe) < 1e-3f
               && abs (Quaternion.Dot(samp.Q, fin.Q)) > 0.9999f then
                flightOpt <- None
        | None -> ()

        if playSig.Peek then
            if nowMs - lastPlayMs >= 32.0 then
                lastPlayMs <- nowMs
                graph.Run (fun () ->
                    let next = scrubSig.Peek + 0.008
                    if next >= 1.0 then
                        scrubSig.Value <- 1.0
                        playSig.Value <- false
                    else
                        scrubSig.Value <- next)

    let zoomStep (anchorPt: Point) (factor: float) =
        flightOpt <- None
        graph.Run (fun () ->
            let cur = camSig.Peek
            let drawerOpen = frameMemo.Peek.DrawerOpen
            let nextZoom = Math.Clamp(cur.Zoom * factor, 0.80, 15.0)
            let wppBefore = AtlasCamera.worldUnitsPerPixelAtZero cur.Zoom
            let wppAfter = AtlasCamera.worldUnitsPerPixelAtZero nextZoom
            let visCenterX = (if drawerOpen then float StageWidth - ExplorerWidth else float StageWidth) * 0.5
            let visCenterY = float StageHeight * 0.5
            let dxPx = float32 (anchorPt.X - visCenterX)
            let dyPx = float32 (visCenterY - anchorPt.Y)
            let nextPanX = Math.Clamp(cur.PanWorldX + dxPx * (wppBefore - wppAfter), -6.5f, 6.5f)
            let nextPanY = Math.Clamp(cur.PanWorldY + dyPx * (wppBefore - wppAfter), -5.5f, 5.5f)
            camSig.Value <- {
                cur with
                    Zoom = nextZoom
                    PanWorldX = nextPanX
                    PanWorldY = nextPanY
            })

    let session = {
        World = world
        Engine = engine
        CameraSig = camSig
        SelectedSig = selSig
        HoveredSig = hovSig
        ScrubSig = scrubSig
        PlayingSig = playSig
        CodeTabSig = codeTabSig
        FrameMemo = frameMemo
        StartFlight = startFlight
        FocusProject = focusProject
        SwitchMode = switchMode
        StepClock = stepClock
        ZoomStep = zoomStep
    }

    let d5View = Renderer.passive

    let projRowComponent =
        Component.define
            (fun _ (read: unit -> AtlasProject) -> read)
            (fun t ->
                t.div
                    [|
                        t.Attrs.attribute (
                            "class",
                            fun read ->
                                let p = read ()
                                let isSel =
                                    match selSig.Value with
                                    | SelectionTarget.Project idx
                                    | SelectionTarget.Function(idx, _) -> idx = p.Index
                                    | SelectionTarget.NoSelection -> false
                                Some (if isSel then "proj-row selected" else "proj-row")
                        )
                        t.Events.onClick (fun read _ ->
                            let p = read ()
                            let selectFn = p.Name = "zlib"
                            session.FocusProject p.Index 5.35 selectFn)
                    |]
                    [|
                        t.div
                            [| t.Attrs.attribute ("class", "proj-row-top") |]
                            [|
                                t.span [| t.Attrs.attribute ("class", "proj-name") |] [| t.text (fun read -> (read ()).Name) |]
                                t.span
                                    [| t.Attrs.attribute ("class", "proj-loc") |]
                                    [|
                                        t.text (fun read ->
                                            let p = read ()
                                            let struct (vf, _) = AtlasWorld.scrubbedFracs scrubSig.Value p
                                            let pct = int (Math.Round(float vf * 100.0))
                                            $"{pct}%% · {fmtK p.TotalLoc}")
                                    |]
                            |]
                        t.div
                            [| t.Attrs.attribute ("class", "mini-track") |]
                            [|
                                t.div
                                    [|
                                        t.Attrs.attribute ("class", "ms-ver")
                                        t.Attrs.attribute (
                                            "style",
                                            fun read ->
                                                let p = read ()
                                                let struct (vf, _) = AtlasWorld.scrubbedFracs scrubSig.Value p
                                                let w = (float vf * 100.0).ToString("F1", CultureInfo.InvariantCulture)
                                                Some $"width:{w}%%"
                                        )
                                    |]
                                    [||]
                                t.div
                                    [|
                                        t.Attrs.attribute ("class", "ms-wip")
                                        t.Attrs.attribute (
                                            "style",
                                            fun read ->
                                                let p = read ()
                                                let struct (_, wf) = AtlasWorld.scrubbedFracs scrubSig.Value p
                                                let w = (float wf * 100.0).ToString("F1", CultureInfo.InvariantCulture)
                                                Some $"width:{w}%%"
                                        )
                                    |]
                                    [||]
                            |]
                    |])

    let depPillComponent =
        Component.define
            (fun _ (read: unit -> int) -> read)
            (fun t ->
                t.span
                    [|
                        t.Attrs.attribute ("class", "dep-pill")
                        t.Events.onClick (fun read _ -> session.FocusProject (read ()) 5.35 false)
                    |]
                    [| t.text (fun read -> world.Projects[read ()].Name) |])

    // 14-column historical verification progress sparkline inside Explorer
    let sparkHistorySteps = [| 0.14; 0.20; 0.27; 0.34; 0.41; 0.48; 0.55; 0.62; 0.69; 0.76; 0.83; 0.90; 0.95; 1.0 |]
    let sparkColComponent =
        Component.define
            (fun _ (read: unit -> float) -> read)
            (fun t ->
                t.div
                    [|
                        t.Attrs.attribute ("class", "spark-col")
                        t.Attrs.attribute (
                            "style",
                            fun read ->
                                let step = read ()
                                let dim = if step <= scrubSig.Value + 0.03 then "1.0" else "0.28"
                                Some $"opacity:{dim}"
                        )
                    |]
                    [|
                        t.div
                            [|
                                t.Attrs.attribute (
                                    "style",
                                    fun read ->
                                        let step = read ()
                                        let pIdx =
                                            match selSig.Value with
                                            | SelectionTarget.Project idx
                                            | SelectionTarget.Function(idx, _) -> idx
                                            | SelectionTarget.NoSelection -> world.ZlibIdx
                                        let struct (_, wf) = AtlasWorld.scrubbedFracs step world.Projects[pIdx]
                                        let h = Math.Clamp(float wf * 95.0, 4.0, 58.0).ToString("F1", CultureInfo.InvariantCulture)
                                        Some $"height:{h}%%;background:#fb923c"
                                )
                            |]
                            [||]
                        t.div
                            [|
                                t.Attrs.attribute (
                                    "style",
                                    fun read ->
                                        let step = read ()
                                        let pIdx =
                                            match selSig.Value with
                                            | SelectionTarget.Project idx
                                            | SelectionTarget.Function(idx, _) -> idx
                                            | SelectionTarget.NoSelection -> world.ZlibIdx
                                        let struct (vf, _) = AtlasWorld.scrubbedFracs step world.Projects[pIdx]
                                        let h = Math.Clamp(float vf * 95.0, 4.0, 72.0).ToString("F1", CultureInfo.InvariantCulture)
                                        Some $"height:{h}%%;background:#26d07c"
                                )
                            |]
                            [||]
                    |])

    let uiComponent =
        Component.define
            (fun _ () -> session)
            (fun t ->
            let inline activeProject s =
                match s.SelectedSig.Value with
                | SelectionTarget.Project idx -> Some (s.World.Projects[idx], None)
                | SelectionTarget.Function(idx, fIdx) ->
                    let p = s.World.Projects[idx]
                    let inner = AtlasWorld.getInner s.World idx
                    let fn = if fIdx >= 0 && fIdx < inner.Functions.Length then Some inner.Functions[fIdx] else None
                    Some (p, fn)
                | SelectionTarget.NoSelection -> None

            t.div
                [| t.Attrs.attribute ("class", "shell") |]
                [|
                    // 1) Top `&Atlas` Milestone Bar
                    t.div
                        [| t.Attrs.attribute ("class", "topbar") |]
                        [|
                            t.div
                                [| t.Attrs.attribute ("class", "brand-row") |]
                                [|
                                    t.span
                                        [| t.Attrs.attribute ("class", "wordmark") |]
                                        [|
                                            t.span [| t.Attrs.attribute ("class", "wordmark-amp") |] [| t.text "&" |]
                                            t.text "Atlas"
                                        |]
                                    t.span
                                        [| t.Attrs.attribute ("class", "bug-pill") |]
                                        [|
                                            t.span [| t.Attrs.attribute ("class", "bug-dot") |] [||]
                                            t.text "900"
                                        |]
                                    t.span
                                        [| t.Attrs.attribute ("class", "scale-meta") |]
                                        [| t.text (fun s -> $"{s.World.TotalProjects:N0} projects · {s.World.TotalDependencies:N0} dependencies") |]
                                |]

                            t.div
                                [| t.Attrs.attribute ("class", "milestone-box") |]
                                [|
                                    t.div
                                        [| t.Attrs.attribute ("class", "milestone-top") |]
                                        [|
                                            t.span
                                                [||]
                                                [|
                                                    t.span
                                                        [| t.Attrs.attribute ("class", "c-ver") |]
                                                        [|
                                                            t.text (fun s ->
                                                                let sc = s.ScrubSig.Value / AtlasWorld.DefaultScrub
                                                                fmtK (int (float s.World.BaselineVerifiedLines * sc)))
                                                        |]
                                                    t.text " verified · "
                                                    t.span
                                                        [| t.Attrs.attribute ("class", "c-wip") |]
                                                        [|
                                                            t.text (fun s ->
                                                                let sc = Math.Pow(max 0.05 (s.ScrubSig.Value / AtlasWorld.DefaultScrub), 0.55)
                                                                fmtK (int (float s.World.BaselineInProgressLines * sc)))
                                                        |]
                                                    t.text " in progress"
                                                |]
                                            t.span [||] [| t.text "Milestone 1: 1M lines" |]
                                        |]
                                    t.div
                                        [| t.Attrs.attribute ("class", "ms-track") |]
                                        [|
                                            t.div
                                                [|
                                                    t.Attrs.attribute ("class", "ms-ver")
                                                    t.Attrs.attribute (
                                                        "style",
                                                        fun s ->
                                                            let pct = Math.Clamp(10.59 * (s.ScrubSig.Value / AtlasWorld.DefaultScrub), 0.5, 45.0)
                                                            Some (sprintf "width:%.1f%%" pct)
                                                    )
                                                |]
                                                [||]
                                            t.div
                                                [|
                                                    t.Attrs.attribute ("class", "ms-wip")
                                                    t.Attrs.attribute (
                                                        "style",
                                                        fun s ->
                                                            let pct = Math.Clamp(19.49 * Math.Pow(max 0.05 (s.ScrubSig.Value / AtlasWorld.DefaultScrub), 0.5), 1.0, 45.0)
                                                            Some (sprintf "width:%.1f%%" pct)
                                                    )
                                                |]
                                                [||]
                                        |]
                                |]

                            t.span
                                [| t.Attrs.attribute ("class", "gpu-pill") |]
                                [|
                                    t.text (fun s ->
                                        let f = s.FrameMemo.Value
                                        let m = s.Engine.MetricsSig.Value
                                        let zc = if s.Engine.IsZeroCopyGpu then "Zero-Copy · " else ""
                                        $"Ranvier.Wgpu {s.Engine.BackendName} · {zc}{m.GpuRenderMs:F2}ms GPU · {m.DrawCalls} draws · {f.ActiveModeText}")
                                |]
                        |]

                    // 2) Main Workspace: Left Rail + Hybrid Wgpu/D5 Stage
                    t.div
                        [| t.Attrs.attribute ("class", "workspace") |]
                        [|
                            t.div
                                [| t.Attrs.attribute ("class", "left-rail") |]
                                [|
                                    // View Mode Switcher (`Map` | `Globe` | `Layers`)
                                    t.div
                                        [| t.Attrs.attribute ("class", "mode-tabs") |]
                                        [|
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", fun s -> Some (if s.CameraSig.Value.Mode = AtlasMode.Map then "mode-tab active" else "mode-tab"))
                                                    t.Events.onClick (fun s _ -> s.SwitchMode AtlasMode.Map)
                                                |]
                                                [| t.text "Map" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", fun s -> Some (if s.CameraSig.Value.Mode = AtlasMode.Globe then "mode-tab active" else "mode-tab"))
                                                    t.Events.onClick (fun s _ -> s.SwitchMode AtlasMode.Globe)
                                                |]
                                                [| t.text "Globe" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", fun s -> Some (if s.CameraSig.Value.Mode = AtlasMode.Layers then "mode-tab active" else "mode-tab"))
                                                    t.Events.onClick (fun s _ -> s.SwitchMode AtlasMode.Layers)
                                                |]
                                                [| t.text "Layers" |]
                                        |]

                                    // Quick Multi-Stage Focus Presets
                                    t.div
                                        [| t.Attrs.attribute ("class", "preset-row") |]
                                        [|
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "preset-chip")
                                                    t.Events.onClick (fun s _ ->
                                                        s.SelectedSig.Value <- SelectionTarget.NoSelection
                                                        s.StartFlight AtlasCamera.mapOverview)
                                                |]
                                                [| t.text "Overview" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "preset-chip")
                                                    t.Events.onClick (fun s _ ->
                                                        s.SelectedSig.Value <- SelectionTarget.Project s.World.ZlibIdx
                                                        let gPos = AtlasWorld.worldPos AtlasCamera.mapOverview s.World.Projects[s.World.GlibcIdx]
                                                        let zPos = AtlasWorld.worldPos AtlasCamera.mapOverview s.World.Projects[s.World.ZlibIdx]
                                                        let mid = (gPos + zPos) * 0.45f
                                                        s.StartFlight {
                                                            AtlasCamera.mapOverview with
                                                                Zoom = 2.55
                                                                PanWorldX = mid.X
                                                                PanWorldY = mid.Y - 0.08f
                                                        })
                                                |]
                                                [| t.text "Mid-Zoom Portals" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "preset-chip")
                                                    t.Events.onClick (fun s _ -> s.FocusProject s.World.ZlibIdx 5.35 true)
                                                |]
                                                [| t.text "zlib · deflate" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "preset-chip")
                                                    t.Events.onClick (fun s _ -> s.FocusProject s.World.Libxml2Idx 5.35 false)
                                                |]
                                                [| t.text "libxml2" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "preset-chip")
                                                    t.Events.onClick (fun s _ -> s.FocusProject s.World.GlibcIdx 5.35 false)
                                                |]
                                                [| t.text "glibc" |]
                                        |]

                                    t.div
                                        [| t.Attrs.attribute ("class", "rail-hdr") |]
                                        [|
                                            t.span [||] [| t.text "Projects" |]
                                            t.span [| t.Attrs.attribute ("class", "rail-sort") |] [| t.text "Most verified ▾" |]
                                        |]

                                    t.div
                                        [| t.Attrs.attribute ("class", "proj-list") |]
                                        [|
                                            t.each
                                                (fun (p: AtlasProject) -> p.Id)
                                                (fun s -> s.World.RankedVerifiedIds |> Array.map (fun idx -> s.World.Projects[idx]))
                                                projRowComponent
                                        |]
                                |]

                            // Interactive Stage: Ranvier.Wgpu 4x-MSAA <canvas> + Ranvier.D5 <svg> + Controls + Explorer Drawer
                            t.div
                                [| t.Attrs.attribute ("class", "stage") |]
                                [|
                                    t.canvas
                                        [|
                                            t.Attrs.attribute ("class", "atlas-canvas")
                                            t.Hooks.mounted (fun s (canvasEl: HTMLCanvasElement) ->
                                                let f = s.FrameMemo.Peek
                                                let uArg = f.Unfolded |> Option.map (fun u -> u.ProjIdx, float32 u.Alpha)
                                                s.Engine.AttachSurface(canvasEl, s.CameraSig.Peek, f.DrawerOpen, f.ActiveProjIdx, uArg, s.ScrubSig.Peek)
                                                fun () -> s.Engine.DetachSurface())
                                        |]
                                        [||]

                                    t.div
                                        [| t.Attrs.attribute ("class", "d5-overlay") |]
                                        [| t.render d5View (fun s -> fun () -> s.FrameMemo.Value.Scene) |]

                                    // Floating bottom-right map controls (`+`, `−`, `⤢ Fit`)
                                    t.div
                                        [|
                                            t.Attrs.attribute (
                                                "class",
                                                fun s -> Some (if s.FrameMemo.Value.DrawerOpen then "map-ctrls shifted" else "map-ctrls")
                                            )
                                        |]
                                        [|
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "ctrl-btn")
                                                    t.Events.onClick (fun s _ ->
                                                        let cx = if s.FrameMemo.Peek.DrawerOpen then 371.0 else 556.0
                                                        s.ZoomStep (Point.create cx 407.0) 1.38)
                                                |]
                                                [| t.text "+" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "ctrl-btn")
                                                    t.Events.onClick (fun s _ ->
                                                        let cx = if s.FrameMemo.Peek.DrawerOpen then 371.0 else 556.0
                                                        s.ZoomStep (Point.create cx 407.0) (1.0 / 1.38))
                                                |]
                                                [| t.text "−" |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "ctrl-btn")
                                                    t.Events.onClick (fun s _ ->
                                                        s.SelectedSig.Value <- SelectionTarget.NoSelection
                                                        match s.CameraSig.Peek.Mode with
                                                        | AtlasMode.Map -> s.StartFlight AtlasCamera.mapOverview
                                                        | AtlasMode.Globe -> s.StartFlight { AtlasCamera.globeOverview with Q = AtlasCamera.globeShowcaseRotation }
                                                        | AtlasMode.Layers -> s.StartFlight AtlasCamera.layersOverview)
                                                |]
                                                [| t.text "⤢" |]
                                        |]

                                    // Bottom Verification Time-Travel Scrubber
                                    t.div
                                        [| t.Attrs.attribute ("class", "scrubber-bar") |]
                                        [|
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", fun s -> Some (if s.PlayingSig.Value then "scrub-btn active" else "scrub-btn"))
                                                    t.Events.onClick (fun s _ ->
                                                        if s.ScrubSig.Peek >= 0.99 then s.ScrubSig.Value <- 0.15
                                                        s.PlayingSig.Value <- not s.PlayingSig.Peek)
                                                |]
                                                [| t.text (fun s -> if s.PlayingSig.Value then "⏸ Pause" else "▶ Replay") |]
                                            t.button
                                                [|
                                                    t.Attrs.attribute ("class", "scrub-btn")
                                                    t.Events.onClick (fun s _ ->
                                                        s.PlayingSig.Value <- false
                                                        let next = if s.ScrubSig.Peek >= 0.98 then 0.20 else min 1.0 (s.ScrubSig.Peek + 0.12)
                                                        s.ScrubSig.Value <- next)
                                                |]
                                                [| t.text "Step +12%" |]
                                            t.div
                                                [| t.Attrs.attribute ("class", "scrub-track") |]
                                                [|
                                                    t.div
                                                        [|
                                                            t.Attrs.attribute ("class", "scrub-fill")
                                                            t.Attrs.attribute (
                                                                "style",
                                                                fun s ->
                                                                    let w = (s.ScrubSig.Value * 100.0).ToString("F0", CultureInfo.InvariantCulture)
                                                                    Some $"width:{w}%%"
                                                            )
                                                        |]
                                                        [||]
                                                |]
                                            t.span
                                                [||]
                                                [| t.text (fun s -> sprintf "Timeline %.0f%%" (s.ScrubSig.Value * 100.0)) |]
                                        |]

                                    // Right-Hand Project & Function Explorer Drawer (C Source + Lean 4 Spec!)
                                    t.div
                                        [|
                                            t.Attrs.attribute (
                                                "class",
                                                fun s -> Some (if s.FrameMemo.Value.DrawerOpen then "explorer" else "explorer hidden")
                                            )
                                        |]
                                        [|
                                            t.div
                                                [| t.Attrs.attribute ("class", "exp-top") |]
                                                [|
                                                    t.span
                                                        [| t.Attrs.attribute ("class", "exp-title") |]
                                                        [|
                                                            t.text (fun s ->
                                                                match activeProject s with
                                                                | Some (p, _) -> p.Name
                                                                | None -> "")
                                                        |]
                                                    t.button
                                                        [|
                                                            t.Attrs.attribute ("class", "exp-close")
                                                            t.Events.onClick (fun s _ -> s.SelectedSig.Value <- SelectionTarget.NoSelection)
                                                        |]
                                                        [| t.text "×" |]
                                                |]

                                            t.div [| t.Attrs.attribute ("class", "exp-sec-lbl") |] [| t.text "Description" |]
                                            t.div
                                                [| t.Attrs.attribute ("class", "exp-desc") |]
                                                [|
                                                    t.text (fun s ->
                                                        match activeProject s with
                                                        | Some (p, _) -> p.Description
                                                        | None -> "")
                                                |]

                                            t.div [| t.Attrs.attribute ("class", "exp-sec-lbl") |] [| t.text "Progress" |]
                                            t.div
                                                [| t.Attrs.attribute ("class", "exp-prog-bar") |]
                                                [|
                                                    t.div
                                                        [|
                                                            t.Attrs.attribute ("class", "ms-ver")
                                                            t.Attrs.attribute (
                                                                "style",
                                                                fun s ->
                                                                    match activeProject s with
                                                                    | Some (p, _) ->
                                                                        let struct (vf, _) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                        Some (sprintf "width:%.1f%%" (float vf * 100.0))
                                                                    | None -> Some "width:0%"
                                                            )
                                                        |]
                                                        [||]
                                                    t.div
                                                        [|
                                                            t.Attrs.attribute ("class", "ms-wip")
                                                            t.Attrs.attribute (
                                                                "style",
                                                                fun s ->
                                                                    match activeProject s with
                                                                    | Some (p, _) ->
                                                                        let struct (_, wf) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                        Some (sprintf "width:%.1f%%" (float wf * 100.0))
                                                                    | None -> Some "width:0%"
                                                            )
                                                        |]
                                                        [||]
                                                |]

                                            t.div
                                                [| t.Attrs.attribute ("class", "exp-stats-grid") |]
                                                [|
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "stat-col") |]
                                                        [|
                                                            t.span [| t.Attrs.attribute ("class", "stat-head c-ver") |] [| t.text "● verified" |]
                                                            t.span
                                                                [| t.Attrs.attribute ("class", "stat-val") |]
                                                                [|
                                                                    t.text (fun s ->
                                                                        match activeProject s with
                                                                        | Some (p, _) ->
                                                                            let struct (vf, _) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                            $"{int (Math.Round(float p.TotalLoc * float vf)):N0} lines"
                                                                        | None -> "0")
                                                                |]
                                                            t.span
                                                                [| t.Attrs.attribute ("class", "stat-sub") |]
                                                                [|
                                                                    t.text (fun s ->
                                                                        match activeProject s with
                                                                        | Some (p, _) ->
                                                                            let struct (vf, _) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                            $"{int (Math.Round(float p.FnCount * float vf))} functions"
                                                                        | None -> "")
                                                                |]
                                                        |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "stat-col") |]
                                                        [|
                                                            t.span [| t.Attrs.attribute ("class", "stat-head c-wip") |] [| t.text "● in progress" |]
                                                            t.span
                                                                [| t.Attrs.attribute ("class", "stat-val") |]
                                                                [|
                                                                    t.text (fun s ->
                                                                        match activeProject s with
                                                                        | Some (p, _) ->
                                                                            let struct (_, wf) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                            $"{int (Math.Round(float p.TotalLoc * float wf)):N0} lines"
                                                                        | None -> "0")
                                                                |]
                                                            t.span
                                                                [| t.Attrs.attribute ("class", "stat-sub") |]
                                                                [|
                                                                    t.text (fun s ->
                                                                        match activeProject s with
                                                                        | Some (p, _) ->
                                                                            let struct (_, wf) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                            $"{int (Math.Round(float p.FnCount * float wf))} functions"
                                                                        | None -> "")
                                                                |]
                                                        |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "stat-col") |]
                                                        [|
                                                            t.span [| t.Attrs.attribute ("class", "stat-head"); t.Attrs.attribute ("style", "color:#94a3b8") |] [| t.text "○ open" |]
                                                            t.span
                                                                [| t.Attrs.attribute ("class", "stat-val") |]
                                                                [|
                                                                    t.text (fun s ->
                                                                        match activeProject s with
                                                                        | Some (p, _) ->
                                                                            let struct (vf, wf) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                            let rem = max 0.0 (1.0 - float (vf + wf))
                                                                            $"{int (Math.Round(float p.TotalLoc * rem)):N0} lines"
                                                                        | None -> "0")
                                                                |]
                                                            t.span
                                                                [| t.Attrs.attribute ("class", "stat-sub") |]
                                                                [|
                                                                    t.text (fun s ->
                                                                        match activeProject s with
                                                                        | Some (p, _) ->
                                                                            let struct (vf, wf) = AtlasWorld.scrubbedFracs s.ScrubSig.Value p
                                                                            let rem = max 0.0 (1.0 - float (vf + wf))
                                                                            $"{int (Math.Round(float p.FnCount * rem))} functions"
                                                                        | None -> "")
                                                                |]
                                                        |]
                                                |]

                                            // Stacked verification history sparkline
                                            t.div
                                                [| t.Attrs.attribute ("class", "spark-box") |]
                                                [|
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "spark-bars") |]
                                                        [|
                                                            t.each
                                                                (fun (st: float) -> st.ToString("F2", CultureInfo.InvariantCulture))
                                                                (fun _ -> sparkHistorySteps)
                                                                sparkColComponent
                                                        |]
                                                |]

                                            t.div [| t.Attrs.attribute ("class", "exp-sec-lbl") |] [| t.text "Metadata" |]
                                            t.div
                                                [| t.Attrs.attribute ("class", "meta-row") |]
                                                [|
                                                    t.span [||] [| t.text "DEPENDS ON" |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "dep-pills") |]
                                                        [|
                                                            t.each
                                                                (fun (idx: int) -> string idx)
                                                                (fun s ->
                                                                    match activeProject s with
                                                                    | Some (p, _) -> p.DependsOn |> Array.truncate 5
                                                                    | None -> Array.empty)
                                                                depPillComponent
                                                        |]
                                                |]
                                            t.div
                                                [| t.Attrs.attribute ("class", "meta-row") |]
                                                [|
                                                    t.span [||] [| t.text "USED BY" |]
                                                    t.span
                                                        [| t.Attrs.attribute ("style", "color:#f8fafc;font-weight:600") |]
                                                        [|
                                                            t.text (fun s ->
                                                                match activeProject s with
                                                                | Some (p, _) -> $"{p.UsedByCount:N0} projects"
                                                                | None -> "0 projects")
                                                        |]
                                                |]

                                            t.div [| t.Attrs.attribute ("class", "exp-sec-lbl") |] [| t.text "Explorer" |]
                                            t.div
                                                [| t.Attrs.attribute ("class", "crumb-bar") |]
                                                [|
                                                    t.span
                                                        [| t.Attrs.attribute ("class", "crumb-path") |]
                                                        [|
                                                            t.text (fun s ->
                                                                match activeProject s with
                                                                | Some (_, Some fn) -> $"{fn.FileName} › {fn.Name}"
                                                                | Some (p, None) -> $"{p.Name} › click any function dot"
                                                                | None -> "")
                                                        |]
                                                    t.div
                                                        [| t.Attrs.attribute ("class", "tab-grp") |]
                                                        [|
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", fun s -> Some (if s.CodeTabSig.Value = CodeTab.Split then "code-tab active" else "code-tab"))
                                                                    t.Events.onClick (fun s _ -> s.CodeTabSig.Value <- CodeTab.Split)
                                                                |]
                                                                [| t.text "C + Lean" |]
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", fun s -> Some (if s.CodeTabSig.Value = CodeTab.CSource then "code-tab active" else "code-tab"))
                                                                    t.Events.onClick (fun s _ -> s.CodeTabSig.Value <- CodeTab.CSource)
                                                                |]
                                                                [| t.text "C" |]
                                                            t.button
                                                                [|
                                                                    t.Attrs.attribute ("class", fun s -> Some (if s.CodeTabSig.Value = CodeTab.Lean4 then "code-tab active" else "code-tab"))
                                                                    t.Events.onClick (fun s _ -> s.CodeTabSig.Value <- CodeTab.Lean4)
                                                                |]
                                                                [| t.text "Lean 4" |]
                                                        |]
                                                |]

                                            t.div
                                                [| t.Attrs.attribute ("class", "fn-banner") |]
                                                [|
                                                    t.span
                                                        [||]
                                                        [|
                                                            t.text (fun s ->
                                                                match activeProject s with
                                                                | Some (p, Some fn) ->
                                                                    let st =
                                                                        match AtlasWorld.scrubbedFnStatus s.ScrubSig.Value fn with
                                                                        | ProofStatus.Verified -> "● verified"
                                                                        | ProofStatus.InProgress -> "● in progress"
                                                                        | ProofStatus.Open -> "○ not started"
                                                                    $"{st} · src/{p.Name}/{fn.FileName}, lines {fn.StartLine}–{fn.EndLine}"
                                                                | Some (p, None) ->
                                                                    let inner = AtlasWorld.getInner s.World p.Index
                                                                    $"{inner.Modules.Length} C files · {inner.Functions.Length} functions · {inner.Calls.Length} call edges"
                                                                | None -> "")
                                                        |]
                                                |]

                                            t.div
                                                [| t.Attrs.attribute ("class", "edge-legend") |]
                                                [|
                                                    t.span [||] [| t.span [| t.Attrs.attribute ("class", "dot-caller") |] [| t.text "━━ " |]; t.text "Incoming callers" |]
                                                    t.span [||] [| t.span [| t.Attrs.attribute ("class", "dot-callee") |] [| t.text "━━ " |]; t.text "Outgoing callees" |]
                                                |]

                                            t.div
                                                [|
                                                    t.Attrs.attribute ("class", "code-pane")
                                                    t.Attrs.attribute (
                                                        "style",
                                                        fun s -> Some (if s.CodeTabSig.Value = CodeTab.Lean4 then "display:none" else "")
                                                    )
                                                |]
                                                [|
                                                    t.text (fun s ->
                                                        match activeProject s with
                                                        | Some (_, Some fn) -> fn.BodyC
                                                        | Some (p, None) ->
                                                            let inner = AtlasWorld.getInner s.World p.Index
                                                            inner.Functions[0].BodyC
                                                        | None -> "")
                                                |]

                                            t.div
                                                [|
                                                    t.Attrs.attribute ("class", "lean-pane")
                                                    t.Attrs.attribute (
                                                        "style",
                                                        fun s -> Some (if s.CodeTabSig.Value = CodeTab.CSource then "display:none" else "")
                                                    )
                                                |]
                                                [|
                                                    t.text (fun s ->
                                                        match activeProject s with
                                                        | Some (_, Some fn) -> fn.LeanSpec
                                                        | Some (p, None) ->
                                                            let inner = AtlasWorld.getInner s.World p.Index
                                                            inner.Functions[0].LeanSpec
                                                        | None -> "")
                                                |]
                                        |]
                                |]
                        |]
                |])

    let mounted =
        Mounting.mount graph None (fun errs -> raise (AggregateException errs)) container None (fun ctx ->
            Program.run (Component.render uiComponent ()) ctx.Backend)

    let svgEl =
        match container.querySelector "svg" with
        | null -> failwith "Expected Ranvier.D5 SVG overlay inside stage."
        | el -> unbox<Ranvier.Dom.SVGGraphicsElement> el

    let mutable dragging = false
    let mutable dragButton = 0s
    let mutable dragShift = false
    let mutable dragMoved = false
    let mutable lastClient = Point.create 0.0 0.0

    let onBlockDefault (ev: Ranvier.Dom.Event) = ev.preventDefault ()

    let onDown (ev: Ranvier.Dom.Event) =
        ev.preventDefault ()
        match ev with
        | :? Ranvier.Dom.PointerEvent as pe ->
            dragging <- true
            dragButton <- pe.button
            dragShift <- pe.shiftKey
            dragMoved <- false
            lastClient <- Point.create pe.clientX pe.clientY
            flightOpt <- None
            svgEl.setPointerCapture pe.pointerId
        | _ -> ()

    let onMove (ev: Ranvier.Dom.Event) =
        match ev with
        | :? Ranvier.Dom.PointerEvent as pe ->
            if dragging then ev.preventDefault ()
            InputAdapter.clientToPlot svgEl (Point.create pe.clientX pe.clientY)
            |> Option.iter (fun pt ->
                if dragging then
                    let dx = pe.clientX - lastClient.X
                    let dy = pe.clientY - lastClient.Y
                    if abs dx > 1.5 || abs dy > 1.5 then dragMoved <- true
                    lastClient <- Point.create pe.clientX pe.clientY

                    graph.Run (fun () ->
                        let cur = Api.untrack (fun () -> session.CameraSig.Value)
                        let panMode = (cur.Zoom >= 2.2 && dragButton = 0s && not dragShift) || dragButton = 1s
                        if panMode then
                            let wpp = AtlasCamera.worldUnitsPerPixelAtZero cur.Zoom
                            session.CameraSig.Value <- {
                                cur with
                                    PanWorldX = Math.Clamp(cur.PanWorldX - float32 dx * wpp, -6.5f, 6.5f)
                                    PanWorldY = Math.Clamp(cur.PanWorldY + float32 dy * wpp, -5.5f, 5.5f)
                            }
                        else
                            session.CameraSig.Value <- AtlasCamera.applyTrackballDrag dx dy cur)
                else
                    graph.Run (fun () ->
                        let frame = Api.untrack (fun () -> session.FrameMemo.Value)
                        let hit = SceneBuilder.pickAt engine frame pt
                        let prev = Api.untrack (fun () -> session.HoveredSig.Value)
                        if hit <> prev then session.HoveredSig.Value <- hit))
        | _ -> ()

    let onUp (ev: Ranvier.Dom.Event) =
        match ev with
        | :? Ranvier.Dom.PointerEvent as pe when dragging ->
            dragging <- false
            if svgEl.hasPointerCapture pe.pointerId then
                svgEl.releasePointerCapture pe.pointerId
            if not dragMoved && dragButton = 0s then
                InputAdapter.clientToPlot svgEl (Point.create pe.clientX pe.clientY)
                |> Option.iter (fun pt ->
                    graph.Run (fun () ->
                        let frame = Api.untrack (fun () -> session.FrameMemo.Value)
                        match SceneBuilder.pickAt engine frame pt with
                        | SelectionTarget.Function(pIdx, fIdx) ->
                            session.SelectedSig.Value <- SelectionTarget.Function(pIdx, fIdx)
                        | SelectionTarget.Project pIdx ->
                            let curZoom = Api.untrack (fun () -> session.CameraSig.Value.Zoom)
                            if curZoom < 4.0 then
                                session.FocusProject pIdx 5.35 false
                            else
                                session.SelectedSig.Value <- SelectionTarget.Project pIdx
                        | SelectionTarget.NoSelection ->
                            session.SelectedSig.Value <- SelectionTarget.NoSelection))
        | _ -> ()

    let onWheel (ev: Ranvier.Dom.Event) =
        match ev with
        | :? Ranvier.Dom.WheelEvent as we ->
            InputAdapter.clientToPlot svgEl (Point.create we.clientX we.clientY)
            |> Option.iter (fun pt ->
                let unitScale = if we.deltaMode = 1u then 16.0 elif we.deltaMode = 2u then 400.0 else 1.0
                ev.preventDefault ()
                let delta = Math.Clamp(-we.deltaY * unitScale * 0.0022, -0.55, 0.55)
                session.ZoomStep pt (exp delta))
        | _ -> ()

    let onKey (ev: Ranvier.Dom.Event) =
        match ev with
        | :? Ranvier.Dom.KeyboardEvent as ke when ke.key = "Escape" ->
            ev.preventDefault ()
            graph.Run (fun () ->
                session.SelectedSig.Value <- SelectionTarget.NoSelection
                session.StartFlight AtlasCamera.mapOverview)
        | _ -> ()

    let svgListeners = [
        "contextmenu", onBlockDefault
        "mousedown", onBlockDefault
        "selectstart", onBlockDefault
        "dragstart", onBlockDefault
        "pointerdown", onDown
        "pointermove", onMove
        "pointerup", onUp
        "pointercancel", onUp
        "wheel", onWheel
        "keydown", onKey
    ]
    let bodyListeners = [
        "selectstart", onBlockDefault
        "dragstart", onBlockDefault
    ]
    for name, fn in svgListeners do svgEl.addEventListener(name, fn)
    for name, fn in bodyListeners do doc.body.addEventListener(name, fn)

    let cleanup =
        { new IDisposable with
            member _.Dispose() =
                for name, fn in bodyListeners do doc.body.removeEventListener(name, fn)
                for name, fn in svgListeners do svgEl.removeEventListener(name, fn)
                graph.Run (fun () -> mounted.Dispose ())
                (engine :> IDisposable).Dispose ()
                container.remove ()
                styleEl.remove ()
        }

    session, cleanup

// ---------------------------------------------------------------------------
// 6. Pure .NET ZLibStream PNG Encoder & 5-Artifact `--smoke` Verification
// ---------------------------------------------------------------------------

module private PngEncoder =
    let private crcTable =
        Array.init 256 (fun n ->
            let mutable c = uint32 n
            for _ = 0 to 7 do
                if (c &&& 1u) <> 0u then c <- 0xEDB88320u ^^^ (c >>> 1)
                else c <- c >>> 1
            c)

    let private writeChunk (out: Stream) (chunkType: string) (data: byte[]) =
        let hdr = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0, 4), uint32 data.Length)
        hdr[4] <- byte chunkType[0]
        hdr[5] <- byte chunkType[1]
        hdr[6] <- byte chunkType[2]
        hdr[7] <- byte chunkType[3]
        out.Write(hdr, 0, 8)
        if data.Length > 0 then
            out.Write(data, 0, data.Length)

        let mutable crc = 0xFFFF_FFFFu
        for i = 4 to 7 do
            crc <- crcTable[int ((crc ^^^ uint32 hdr[i]) &&& 0xFFu)] ^^^ (crc >>> 8)
        for i = 0 to data.Length - 1 do
            crc <- crcTable[int ((crc ^^^ uint32 data[i]) &&& 0xFFu)] ^^^ (crc >>> 8)

        let crcBytes = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes.AsSpan(0, 4), crc ^^^ 0xFFFF_FFFFu)
        out.Write(crcBytes, 0, 4)

    let saveRgba8 (path: string) (width: int) (height: int) (rgba: byte[]) =
        let dir = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

        use fs = File.Create path
        fs.Write([| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |], 0, 8)

        let ihdr = Array.zeroCreate<byte> 13
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), uint32 width)
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), uint32 height)
        ihdr[8] <- 8uy
        ihdr[9] <- 6uy
        ihdr[10] <- 0uy
        ihdr[11] <- 0uy
        ihdr[12] <- 0uy
        writeChunk fs "IHDR" ihdr

        use idatMs = new MemoryStream()
        do
            use zlib = new ZLibStream(idatMs, CompressionLevel.Optimal, leaveOpen = true)
            let rowStride = width * 4
            let scanline = Array.zeroCreate<byte> (1 + rowStride)
            scanline[0] <- 0uy
            for y = 0 to height - 1 do
                Buffer.BlockCopy(rgba, y * rowStride, scanline, 1, rowStride)
                for x = 0 to width - 1 do
                    scanline[4 + x * 4] <- 255uy
                zlib.Write(scanline, 0, scanline.Length)

        writeChunk fs "IDAT" (idatMs.ToArray())
        writeChunk fs "IEND" Array.empty<byte>

let private countSampledColors (rgba: byte[]) (stride: int) =
    let seen = HashSet<uint32>()
    let mutable i = 0
    while i <= rgba.Length - 4 do
        let c = (uint32 rgba[i] <<< 24) ||| (uint32 rgba[i + 1] <<< 16) ||| (uint32 rgba[i + 2] <<< 8) ||| uint32 rgba[i + 3]
        seen.Add c |> ignore
        i <- i + stride
    seen.Count

let private runSmokeCheck () =
    let artifactsDir =
        match Environment.GetEnvironmentVariable "RANVIER_CAPTURE_DIR" with
        | s when not (String.IsNullOrWhiteSpace s) -> Path.GetFullPath s
        | _ -> Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "artifacts"))

    Directory.CreateDirectory artifactsDir |> ignore

    // Hardware GPU WebCore Window + zero-copy Ranvier.Wgpu.Sdl shared GPU texture!
    use window =
        Window.Create("Ranvier.D5 + Ranvier.Wgpu — &Atlas", WindowWidth, WindowHeight, hidden = true)

    let session, cleanup = mountAtlas window
    try
        let stepFrames n =
            for _ in 1 .. n do
                if not (window.Step(timeout = 16)) then
                    failwith "Window closed unexpectedly during smoke test."

        let captureWindowPixels () =
            window.Present()
            window.ReadPixels()

        let captureWindowPng fileName =
            let struct (winW, winH, buf) = captureWindowPixels ()
            let fullPath = Path.Combine(artifactsDir, fileName)
            PngEncoder.saveRgba8 fullPath winW winH buf
            fullPath, buf

        stepFrames 12

        if session.World.TotalProjects <> 3459 || session.World.TotalDependencies <> 12391 then
            failwith $"Expected 3,459 projects & 12,391 dependencies, got {session.World.TotalProjects} & {session.World.TotalDependencies}."

        let zlibProj = session.World.Projects[session.World.ZlibIdx]
        if zlibProj.UsedByCount <> 520 then
            failwith $"Expected zlib UsedByCount = 520, got {zlibProj.UsedByCount}."

        // -------------------------------------------------------------------
        // 1) Default `Map` View (`atlas-map.png`) + Hardware DiscBatch GPU Pick
        // -------------------------------------------------------------------
        let pngMap, mapWinPixels = captureWindowPng "atlas-map.png"
        let mapFrame = window.Graph.Run (fun () -> session.FrameMemo.Value)
        let mapMetrics = session.Engine.LastMetrics
        let mapWinColors = countSampledColors mapWinPixels 32

        if session.Engine.FramesDelivered < 1 || mapWinColors < 60 || mapMetrics.DrawCalls <> 2 then
            failwith $"Expected rich 2-draw-call LineBatch+DiscBatch Map render, got frames={session.Engine.FramesDelivered}, draws={mapMetrics.DrawCalls}, winColors={mapWinColors}."

        if OperatingSystem.IsWindows() && window.IsGpuComposited then
            if not session.Engine.IsZeroCopyGpu || window.CpuFramesPresented <> 0L || mapMetrics.ReadbackMs <> 0.0 then
                failwith $"Expected zero-copy Dx12SharedTexture presentation (IsZeroCopyGpu={session.Engine.IsZeroCopyGpu}, CpuFrames={window.CpuFramesPresented}, ReadbackMs={mapMetrics.ReadbackMs})."

        let glibcScreen = mapFrame.Candidates[session.World.GlibcIdx].Screen
        match session.Engine.PickProjectGpuAtStage(glibcScreen.X, glibcScreen.Y) with
        | Some (idx, _) when idx = session.World.GlibcIdx -> ()
        | other -> failwith $"Expected hardware GPU DiscBatch pick at glibc ({glibcScreen.X:F1}, {glibcScreen.Y:F1}) to hit glibc ({session.World.GlibcIdx}), got {other}."

        let zlibScreen = mapFrame.Candidates[session.World.ZlibIdx].Screen
        match session.Engine.PickProjectGpuAtStage(zlibScreen.X, zlibScreen.Y) with
        | Some (idx, _) when idx = session.World.ZlibIdx -> ()
        | other -> failwith $"Expected hardware GPU DiscBatch pick at zlib ({zlibScreen.X:F1}, {zlibScreen.Y:F1}) to hit zlib ({session.World.ZlibIdx}), got {other}."

        // -------------------------------------------------------------------
        // 2) Rotated 3D `Globe` View (`atlas-globe.png`) + Trackball Drag Delta
        // -------------------------------------------------------------------
        window.Graph.Run (fun () ->
            session.SelectedSig.Value <- SelectionTarget.NoSelection
            session.CameraSig.Value <- AtlasCamera.globeOverview)
        stepFrames 6
        let struct (_, _, globeBeforeDrag) = captureWindowPixels ()

        window.Graph.Run (fun () ->
            session.CameraSig.Value <-
                AtlasCamera.globeOverview
                |> AtlasCamera.applyTrackballDrag 115.0 -68.0)
        stepFrames 6

        let pngGlobe, globeAfterDrag = captureWindowPng "atlas-globe.png"
        let mutable globeDragDeltaBytes = 0
        for i = 0 to globeBeforeDrag.Length - 1 do
            if globeBeforeDrag[i] <> globeAfterDrag[i] then
                globeDragDeltaBytes <- globeDragDeltaBytes + 1
        if globeDragDeltaBytes <= 80_000 then
            failwith $"Expected > 80,000 composited window bytes changed on 3D Globe quaternion drag, got {globeDragDeltaBytes}."

        // -------------------------------------------------------------------
        // 3) `Layers` Concentric Dependency Arch View (`atlas-layers.png`)
        // -------------------------------------------------------------------
        window.Graph.Run (fun () ->
            session.SelectedSig.Value <- SelectionTarget.NoSelection
            session.CameraSig.Value <- AtlasCamera.layersOverview)
        stepFrames 6
        let pngLayers, _ = captureWindowPng "atlas-layers.png"

        // -------------------------------------------------------------------
        // 4) Stage 2 Mid-Zoom ~2.55x (`atlas-midzoom.png`) with `portalHollow`
        //    dark-rimmed inner module constellations inside `glibc` & `linux`!
        // -------------------------------------------------------------------
        window.Graph.Run (fun () ->
            session.SelectedSig.Value <- SelectionTarget.Project session.World.ZlibIdx
            let gPos = AtlasWorld.worldPos AtlasCamera.mapOverview session.World.Projects[session.World.GlibcIdx]
            let zPos = AtlasWorld.worldPos AtlasCamera.mapOverview session.World.Projects[session.World.ZlibIdx]
            let mid = (gPos + zPos) * 0.45f
            session.CameraSig.Value <- {
                AtlasCamera.mapOverview with
                    Zoom = 2.55
                    PanWorldX = mid.X
                    PanWorldY = mid.Y - 0.08f
            })
        stepFrames 6
        let midDiscCount = session.Engine.DiscCount
        if midDiscCount < 3459 + 320 + 80 then
            failwith $"Expected Stage 2 hollow portals (`glibc` & `linux`) to emit miniature inner module micro-dots, got DiscCount={midDiscCount}."

        let pngMidZoom, _ = captureWindowPng "atlas-midzoom.png"

        // -------------------------------------------------------------------
        // 5) Stage 3 & 4 `zlib` Drill-Down + `deflate` Selection (`atlas-zlib-callgraph.png`)
        // -------------------------------------------------------------------
        window.Graph.Run (fun () ->
            let zPos = AtlasWorld.worldPos AtlasCamera.mapOverview zlibProj
            let targetZoom = 5.35
            let depth = max 2.0f (RefCamDist - zPos.Z)
            let drawerShift = float32 (ExplorerWidth * 0.5) * AtlasCamera.worldUnitsPerPixelAtZero targetZoom
            let panX = zPos.X + drawerShift * ((depth / RefCamDist) - 1.0f)
            session.SelectedSig.Value <- SelectionTarget.Project session.World.ZlibIdx
            session.CameraSig.Value <- {
                AtlasCamera.mapOverview with
                    Zoom = targetZoom
                    PanWorldX = panX
                    PanWorldY = zPos.Y
            })
        stepFrames 6

        let zlibFrame = window.Graph.Run (fun () -> session.FrameMemo.Value)
        let uv =
            match zlibFrame.Unfolded with
            | Some u when u.ProjIdx = session.World.ZlibIdx -> u
            | _ -> failwith "Expected zlib to unfold into Stage 3/4 Ranvier.D5 Pack+Force call-graph."

        if uv.Inner.Functions.Length < 120 || uv.Inner.Calls.Length < 160 then
            failwith $"Expected >= 120 zlib functions and >= 160 call edges, got fns={uv.Inner.Functions.Length}, calls={uv.Inner.Calls.Length}."

        let deflateFn = uv.Inner.Functions[0]
        let deflatePt =
            Point.create
                (uv.Center.X + deflateFn.LocalPos.X * uv.RadiusPx)
                (uv.Center.Y + deflateFn.LocalPos.Y * uv.RadiusPx)
        match SceneBuilder.pickAt session.Engine zlibFrame deflatePt with
        | SelectionTarget.Function(pIdx, 0) when pIdx = session.World.ZlibIdx -> ()
        | other -> failwith $"Expected Quadtree function pick at deflate ({deflatePt.X:F1}, {deflatePt.Y:F1}) to hit Function(zlib, 0=deflate), got {other}."

        // Select `deflate` so `#38bdf8` incoming caller & `#ffffff` outgoing callee Bézier curves + C/Lean Explorer render!
        window.Graph.Run (fun () ->
            session.CodeTabSig.Value <- CodeTab.Split
            session.SelectedSig.Value <- SelectionTarget.Function(session.World.ZlibIdx, 0))
        stepFrames 8

        let pngZlib, _ = captureWindowPng "atlas-zlib-callgraph.png"

        // -------------------------------------------------------------------
        // 6) Reset to Overview & Verify Zero Idle Renders
        // -------------------------------------------------------------------
        window.Graph.Run (fun () ->
            session.SelectedSig.Value <- SelectionTarget.NoSelection
            session.CameraSig.Value <- AtlasCamera.mapOverview)
        stepFrames 6

        let rendersBeforeIdle = session.Engine.RenderCalls
        for _ in 1 .. 8 do
            window.Step(timeout = 12) |> ignore
            session.StepClock ()
        let idleRenders = session.Engine.RenderCalls - rendersBeforeIdle
        if idleRenders <> 0 then
            failwith $"Expected 0 idle WgpuContext.Render calls, got {idleRenders}."

        for p in [ pngMap; pngGlobe; pngLayers; pngMidZoom; pngZlib ] do
            let fi = FileInfo p
            if not fi.Exists || fi.Length < 25_000L then
                failwith $"Expected valid PNG screenshot (> 25 KB) at {p}, got exists={fi.Exists}."

        if OperatingSystem.IsWindows() && window.IsGpuComposited && window.CpuFramesPresented <> 0L then
            failwith $"Expected 0 software CPU frames during entire Atlas session, got {window.CpuFramesPresented}."

        let finalZlibFrame = window.Graph.Run (fun () -> session.FrameMemo.Value)
        let modeStr = session.Engine.PresentationMode |> Option.map string |> Option.defaultValue "Unattached"
        Console.WriteLine(
            $"Smoke check PASSED on {session.Engine.BackendName} ({session.Engine.AdapterName}, mode={modeStr}, gpuFrames={window.GpuFramesPresented}, cpuFrames={window.CpuFramesPresented}):\n"
            + $"  Dataset: {session.World.TotalProjects:N0} C projects (12 strata) · {session.World.TotalDependencies:N0} directed dependencies (`LineBatch`={session.Engine.LineVertexCount:N0} verts, `DiscBatch`={session.Engine.DiscCount:N0} discs)\n"
            + $"  4x-MSAA Render (2 GPU draw calls): gpu={mapMetrics.GpuRenderMs:F2}ms, readback={mapMetrics.ReadbackMs:F2}ms (avg gpu={session.Engine.AvgGpuRenderMs:F2}ms, readback={session.Engine.AvgReadbackMs:F2}ms)\n"
            + $"  Verification: gpuDiscPick=Hit(glibc & zlib), globeDragDeltaBytes={globeDragDeltaBytes:N0}, portalMidZoomDiscs={midDiscCount:N0}, zlibCallGraph=({uv.Inner.Modules.Length} C files, {uv.Inner.Functions.Length} Force fns, {uv.Inner.Calls.Length} Beziers, picked deflate), idleRenders={idleRenders}, resetMarks={finalZlibFrame.MarkCount}\n"
            + $"  Artifacts:\n    - {pngMap}\n    - {pngGlobe}\n    - {pngLayers}\n    - {pngMidZoom}\n    - {pngZlib}"
        )
        window.Close ()
    finally
        cleanup.Dispose ()

[<EntryPoint>]
let main argv =
    let isSmoke =
        Environment.GetEnvironmentVariable "RANVIER_SMOKE" = "1"
        || (argv |> Array.exists (fun a -> a = "--smoke"))

    if isSmoke then
        runSmokeCheck ()
    else
        use window =
            Window.Create("Ranvier.D5 + Ranvier.Wgpu — &Atlas", WindowWidth, WindowHeight, hidden = true)

        let session, cleanup = mountAtlas window
        try
            window.Show ()
            window.Present ()
            let modeStr = session.Engine.PresentationMode |> Option.map string |> Option.defaultValue "Unattached"
            Console.WriteLine(
                $"Ranvier.D5 + Ranvier.Wgpu &Atlas opened ({WindowWidth}x{WindowHeight}) on {session.Engine.BackendName} ({session.Engine.AdapterName}, mode={modeStr})."
            )
            Console.Out.Flush ()
            while window.Step () do
                session.StepClock ()
        finally
            cleanup.Dispose ()

    0
