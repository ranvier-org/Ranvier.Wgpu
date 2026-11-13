#!/usr/bin/env python3
"""
Deterministic C-header -> F# P/Invoke binding generator for webgpu.h + wgpu.h.
Driven by tools/type_map.json; emits NativeAOT-clean blittable structs, enums,
64-bit flag constants, callback delegates, and DllImport extern declarations.
"""

import json
import os
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HEADERS_DIR = ROOT / "headers" / "webgpu"
SPIKE_HEADERS = Path("C:/Users/shaya/.gemini/antigravity/scratch/RanvierSpikeWgpu/native/include/webgpu")
TYPE_MAP_PATH = ROOT / "tools" / "type_map.json"
OUT_PATH = ROOT / "src" / "Ranvier.Wgpu" / "Generated" / "WgpuBindings.fs"


def ensure_headers():
    HEADERS_DIR.mkdir(parents=True, exist_ok=True)
    for name in ("webgpu.h", "wgpu.h"):
        dst = HEADERS_DIR / name
        if not dst.exists() and (SPIKE_HEADERS / name).exists():
            shutil.copy2(SPIKE_HEADERS / name, dst)


def strip_comments_and_macros(src: str) -> str:
    # Remove /* ... */ and // ... comments
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", " ", src)
    # Remove multi-line #define / preprocessor lines (after capturing important constants separately)
    out_lines = []
    in_macro = False
    for line in src.splitlines():
        s = line.strip()
        if in_macro:
            if not s.endswith("\\"):
                in_macro = False
            continue
        if s.startswith("#"):
            if s.endswith("\\"):
                in_macro = True
            continue
        out_lines.append(line)
    return "\n".join(out_lines)


def pascal_field(name: str, kw_map: dict) -> str:
    if name in kw_map:
        return kw_map[name]
    if not name:
        return "Field"
    p = name[0].upper() + name[1:]
    return kw_map.get(p.lower(), p) if p in kw_map else p


def safe_param_name(name: str, kw_map: dict, idx: int) -> str:
    if not name:
        return f"arg{idx}"
    if name in kw_map:
        return name + "_"
    # Additional F# reserved words
    fsharp_reserved = {
        "type", "module", "function", "begin", "end", "default", "global",
        "namespace", "open", "let", "in", "to", "val", "with", "member",
        "override", "base", "null", "void", "params", "fixed", "inline",
        "match", "return", "use", "rec", "mutable", "struct", "sig", "upcast",
        "downcast", "lazy", "assert", "asr", "land", "lor", "lsl", "lsr", "lxor", "mod"
    }
    if name in fsharp_reserved:
        return name + "_"
    return name


def main():
    ensure_headers()
    cfg = json.loads(TYPE_MAP_PATH.read_text(encoding="utf-8"))
    primitives = dict(cfg["primitives"])
    kw_map = cfg["fsharpKeywords"]
    struct_overrides = cfg["structOverrides"]
    dll_name = cfg["dllName"]

    raw_webgpu = (HEADERS_DIR / "webgpu.h").read_text(encoding="utf-8")
    raw_wgpu = (HEADERS_DIR / "wgpu.h").read_text(encoding="utf-8")
    combined_raw = raw_webgpu + "\n" + raw_wgpu
    clean = strip_comments_and_macros(combined_raw)
    # Remove attribute macros
    for tok in ("WGPU_EXPORT", "WGPU_ENUM_ATTRIBUTE", "WGPU_STRUCTURE_ATTRIBUTE", "WGPU_FUNCTION_ATTRIBUTE", "WGPU_OBJECT_ATTRIBUTE", "WGPU_NULLABLE"):
        clean = re.sub(rf"\b{tok}\b", " ", clean)

    # 1. Opaque handle typedefs: typedef struct WGPUFooImpl* WGPUFoo;
    opaque_handles = set()
    for m in re.finditer(r"typedef\s+struct\s+\w+\s*\*\s*(\w+)\s*;", clean):
        opaque_handles.add(m.group(1))

    # 2. Scalar/flag typedefs: typedef WGPUFlags WGPUBufferUsage; or typedef uint64_t WGPUSubmissionIndex;
    typedef_aliases = {}
    for m in re.finditer(r"typedef\s+([A-Za-z0-9_]+)\s+([A-Za-z0-9_]+)\s*;", clean):
        src_t, dst_t = m.group(1), m.group(2)
        if src_t not in ("enum", "struct", "union"):
            typedef_aliases[dst_t] = src_t

    # 3. Enums: typedef enum Name { ... } Name;
    enums = {}  # enum_name -> list of (member_name, value_str)
    for m in re.finditer(r"typedef\s+enum\s+(\w+)?\s*\{([^}]*)\}\s*(\w+)\s*;", clean, flags=re.S):
        ename = m.group(3)
        body = m.group(2)
        items = []
        auto_val = 0
        for entry in body.split(","):
            entry = entry.strip()
            if not entry:
                continue
            if "=" in entry:
                k, v = [x.strip() for x in entry.split("=", 1)]
                v_clean = v.rstrip("uUlL")
                try:
                    auto_val = int(v_clean, 0) + 1
                except ValueError:
                    pass
                items.append((k, v_clean))
            else:
                items.append((entry, hex(auto_val)))
                auto_val += 1
        enums[ename] = items

    # 4. 64-bit WGPUFlags constants: static const WGPUBufferUsage WGPUBufferUsage_MapRead = 0x...;
    flag_consts = []
    seen_consts = set()
    for m in re.finditer(r"static\s+const\s+(\w+)\s+(\w+)\s*=\s*([^;]+);", clean):
        tname, cname, val = m.group(1), m.group(2), m.group(3).strip()
        if cname not in seen_consts:
            seen_consts.add(cname)
            val_clean = val.rstrip("uUlL")
            flag_consts.append((tname, cname, val_clean))

    # 5. Callback typedefs: typedef ret (*WGPU...Callback)(args);
    callbacks = {}  # cb_name -> (ret_c, [(arg_type_c, arg_name)])
    for m in re.finditer(r"typedef\s+([^;(]+?)\s*\(\s*\*\s*(\w+)\s*\)\s*\(([^)]*)\)\s*;", clean):
        ret_c = m.group(1).strip()
        cb_name = m.group(2).strip()
        args_raw = m.group(3).strip()
        args = []
        if args_raw and args_raw != "void":
            for idx, part in enumerate(args_raw.split(",")):
                part = part.strip()
                pm = re.match(r"^(.*?)([A-Za-z_]\w*)$", part)
                if pm and pm.group(1).strip():
                    args.append((pm.group(1).strip(), pm.group(2).strip()))
                else:
                    args.append((part, f"arg{idx}"))
        callbacks[cb_name] = (ret_c, args)

    # 6. Structs: typedef struct Name { ... } Name;
    raw_structs = {}  # sname -> list of (field_name, field_type_c)
    # Match structs without nested braces OR overridden structs
    pos = 0
    td_struct_re = re.compile(r"typedef\s+struct\s+(\w+)?\s*\{")
    while True:
        m = td_struct_re.search(clean, pos)
        if not m:
            break
        brace_start = m.end() - 1
        depth = 0
        i = brace_start
        while i < len(clean):
            if clean[i] == "{":
                depth += 1
            elif clean[i] == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        body = clean[brace_start + 1 : i]
        tail_m = re.match(r"\s*(\w+)\s*;", clean[i + 1 :])
        pos = i + 1 + (tail_m.end() if tail_m else 1)
        if not tail_m:
            continue
        sname = tail_m.group(1)
        if sname in struct_overrides:
            raw_structs[sname] = None  # handled by override
            continue
        fields = []
        for stmt in body.split(";"):
            stmt = stmt.strip()
            if not stmt:
                continue
            fm = re.match(r"^(.*?)([A-Za-z_]\w*)$", stmt, flags=re.S)
            if fm:
                ftype = " ".join(fm.group(1).split())
                fname = fm.group(2).strip()
                fields.append((fname, ftype))
        raw_structs[sname] = fields

    def resolve_c_type(ctype: str, for_struct_field: bool = False) -> str:
        t = " ".join(ctype.replace("const", " ").replace("struct", " ").split())
        if "*" in t:
            return "nativeint"
        if t in primitives:
            return primitives[t]
        if t in opaque_handles:
            return "nativeint"
        if t in callbacks:
            return "nativeint"  # Raw function pointer in ABI structs & externs for AOT safety
        if t in enums:
            return "uint32"
        if t in typedef_aliases:
            return resolve_c_type(typedef_aliases[t], for_struct_field)
        if t in raw_structs or t in struct_overrides:
            return t
        raise KeyError(f"Unmapped C type: '{ctype}' (normalized '{t}')")

    # Topological sort of structs
    sorted_structs = []
    visited = set()

    def visit_struct(sname: str):
        if sname in visited:
            return
        visited.add(sname)
        if sname not in struct_overrides and raw_structs.get(sname):
            for _, ftype in raw_structs[sname]:
                ft = resolve_c_type(ftype, for_struct_field=True)
                if ft in raw_structs or ft in struct_overrides:
                    visit_struct(ft)
        sorted_structs.append(sname)

    for sname in raw_structs:
        visit_struct(sname)

    # 7. Exported C functions (webgpu.h WGPU_EXPORT + wgpu.h extern "C" wgpu*)
    functions = {}  # fname -> (ret_fs, [(param_fs_type, param_name)])
    func_re = re.compile(r"(?:WGPU_EXPORT\s+)?([A-Za-z0-9_*\s]+?)\s+(wgpu[A-Z]\w*)\s*\(([^)]*)\)\s*;")
    for m in func_re.finditer(clean):
        ret_c = " ".join(m.group(1).split())
        if ret_c.startswith("typedef"):
            continue
        fname = m.group(2).strip()
        if fname in functions:
            continue  # Keep canonical first declaration
        args_raw = m.group(3).strip()
        params = []
        if args_raw and args_raw != "void":
            for idx, part in enumerate(args_raw.split(",")):
                part = " ".join(part.strip().split())
                pm = re.match(r"^(.*?)([A-Za-z_]\w*)$", part)
                if pm and pm.group(1).strip():
                    p_c = pm.group(1).strip()
                    p_n = safe_param_name(pm.group(2).strip(), kw_map, idx)
                else:
                    p_c = part
                    p_n = f"arg{idx}"
                params.append((resolve_c_type(p_c), p_n))
        ret_fs = "void" if ret_c == "void" else resolve_c_type(ret_c)
        functions[fname] = (ret_fs, params)

    # Emit F# file
    lines = []
    lines.append("// <auto-generated>")
    lines.append("// Generated by tools/gen_bindings.py from headers/webgpu/{webgpu.h,wgpu.h} and tools/type_map.json.")
    lines.append("// Do not hand-edit; re-run `python tools/gen_bindings.py` when upgrading wgpu-native.")
    lines.append("// </auto-generated>")
    lines.append("namespace Ranvier.Wgpu.Native")
    lines.append("")
    lines.append("open System")
    lines.append("open System.Runtime.InteropServices")
    lines.append("open System.Text")
    lines.append("")
    lines.append("[<AutoOpen>]")
    lines.append("module WgpuGenerated =")
    lines.append(f'    [<Literal>]')
    lines.append(f'    let WgpuDllName = "{dll_name}"')
    lines.append("")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_FALSE = 0u")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_TRUE = 1u")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_ARRAY_LAYER_COUNT_UNDEFINED = 0xFFFFFFFFu")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_COPY_STRIDE_UNDEFINED = 0xFFFFFFFFu")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_DEPTH_SLICE_UNDEFINED = 0xFFFFFFFFu")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_LIMIT_U32_UNDEFINED = 0xFFFFFFFFu")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_LIMIT_U64_UNDEFINED = 0xFFFFFFFFFFFFFFFFuL")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_MIP_LEVEL_COUNT_UNDEFINED = 0xFFFFFFFFu")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_QUERY_SET_INDEX_UNDEFINED = 0xFFFFFFFFu")
    lines.append("    [<Literal>]")
    lines.append("    let WGPU_WHOLE_SIZE = 0xFFFFFFFFFFFFFFFFuL")
    lines.append("    let WGPU_STRLEN = unativeint UInt64.MaxValue")
    lines.append("    let WGPU_WHOLE_MAP_SIZE = unativeint UInt64.MaxValue")
    lines.append("")

    lines.append("    // -----------------------------------------------------------------------")
    lines.append(f"    // Enum Constants ({len(enums)} enums)")
    lines.append("    // -----------------------------------------------------------------------")
    const_env = {}
    emitted_enum_consts = set()
    for ename, items in enums.items():
        for k, v in items:
            if k in emitted_enum_consts:
                continue
            emitted_enum_consts.add(k)
            expr = re.sub(r"(0x[0-9A-Fa-f]+|\b\d+)[uUlL]+\b", r"\1", v)
            expr = re.sub(r"\([A-Za-z_]\w*\)", "", expr)
            val_int = int(eval(expr, {"__builtins__": {}}, const_env)) & 0xFFFFFFFF
            const_env[k] = val_int
            lines.append(f"    [<Literal>]")
            lines.append(f"    let {k} = 0x{val_int:08X}u")
    lines.append("")

    lines.append("    // -----------------------------------------------------------------------")
    lines.append(f"    // 64-Bit Flag Constants ({len(flag_consts)} flags)")
    lines.append("    // -----------------------------------------------------------------------")
    for _, cname, v in flag_consts:
        expr = re.sub(r"(0x[0-9A-Fa-f]+|\b\d+)[uUlL]+\b", r"\1", v)
        expr = re.sub(r"\([A-Za-z_]\w*\)", "", expr)
        val_u64 = int(eval(expr, {"__builtins__": {}}, const_env)) & 0xFFFFFFFFFFFFFFFF
        const_env[cname] = val_u64
        lines.append(f"    [<Literal>]")
        lines.append(f"    let {cname} = 0x{val_u64:016X}uL")
    lines.append("")

    lines.append("    // -----------------------------------------------------------------------")
    lines.append(f"    // Blittable Sequential Structs ({len(sorted_structs)} structs, NativeAOT-safe)")
    lines.append("    // -----------------------------------------------------------------------")
    for sname in sorted_structs:
        lines.append("    [<Struct; StructLayout(LayoutKind.Sequential)>]")
        lines.append(f"    type {sname} =")
        if sname in struct_overrides:
            flist = struct_overrides[sname]
        else:
            flist = [(pascal_field(fn, kw_map), resolve_c_type(ft, for_struct_field=True)) for fn, ft in raw_structs[sname]]
        for fn_fs, ft_fs in flist:
            lines.append(f"        val mutable {fn_fs}: {ft_fs}")
        if sname == "WGPUStringView":
            lines.append("        static member Null =")
            lines.append("            let mutable v = WGPUStringView()")
            lines.append("            v.Data <- 0n")
            lines.append("            v.Length <- WGPU_STRLEN")
            lines.append("            v")
            lines.append("        static member Empty = WGPUStringView()")
            lines.append("        member this.AsString() =")
            lines.append("            if this.Data = 0n || this.Length = 0un then \"\"")
            lines.append("            elif this.Length = WGPU_STRLEN then Marshal.PtrToStringUTF8(this.Data) |> Option.ofObj |> Option.defaultValue \"\"")
            lines.append("            else Marshal.PtrToStringUTF8(this.Data, int this.Length)")
        lines.append("")

    lines.append("    // -----------------------------------------------------------------------")
    lines.append(f"    // Unmanaged Cdecl Callback Delegates ({len(callbacks)} callbacks)")
    lines.append("    // -----------------------------------------------------------------------")
    for cb_name, (ret_c, args) in callbacks.items():
        ret_fs = "unit" if ret_c == "void" else resolve_c_type(ret_c)
        if not args:
            sig = f"unit -> {ret_fs}"
        else:
            parts = [f"{safe_param_name(an, kw_map, idx)}: {resolve_c_type(at)}" for idx, (at, an) in enumerate(args)]
            sig = " * ".join(parts) + f" -> {ret_fs}"
        lines.append("    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]")
        lines.append(f"    type {cb_name} = delegate of {sig}")
    lines.append("")

    lines.append("    // -----------------------------------------------------------------------")
    lines.append(f"    // Native C Exports ({len(functions)} functions)")
    lines.append("    // -----------------------------------------------------------------------")
    for fname, (ret_fs, params) in functions.items():
        param_str = ", ".join(f"{pt} {pn}" for pt, pn in params)
        lines.append(f'    [<DllImport(WgpuDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]')
        lines.append(f"    extern {ret_fs} {fname}({param_str})")
    lines.append("")

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text("\n".join(lines), encoding="utf-8")
    print(
        f"Generated {OUT_PATH.relative_to(ROOT)}: "
        f"{len(enums)} enums ({len(emitted_enum_consts)} values), "
        f"{len(flag_consts)} 64-bit flags, "
        f"{len(sorted_structs)} structs, "
        f"{len(callbacks)} callbacks, "
        f"{len(functions)} C functions ({len(lines)} F# lines)."
    )


if __name__ == "__main__":
    main()
