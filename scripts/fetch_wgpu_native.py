#!/usr/bin/env python3
"""Downloads and SHA-256-verifies pinned wgpu-native release binaries defined in pins.json."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import pathlib
import stat
import sys
import urllib.request
import zipfile


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def download_verified(url: str, expected_sha256: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "Ranvier.Wgpu-fetch/1.0"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        payload = resp.read()
    actual = sha256_bytes(payload)
    if actual.lower() != expected_sha256.lower():
        raise RuntimeError(f"SHA-256 mismatch for {url}: expected {expected_sha256}, got {actual}")
    return payload


def main() -> int:
    repo_root = pathlib.Path(__file__).resolve().parent.parent
    pins_path = repo_root / "pins.json"
    pins = json.loads(pins_path.read_text(encoding="utf-8"))

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--rid",
        action="append",
        dest="rids",
        help="Runtime identifier(s) to fetch (default: all pinned RIDs).",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Re-download even when target binary and stamp already match.",
    )
    args = parser.parse_args()

    upstream = pins["upstream"]
    tag = upstream["tag"]
    repo_url = upstream["repository"].rstrip("/")
    runtimes: dict[str, dict[str, str]] = pins["runtimes"]
    selected = args.rids if args.rids else list(runtimes.keys())

    runtimes_root = repo_root / "src" / "Ranvier.Wgpu" / "runtimes"

    for rid in selected:
        if rid not in runtimes:
            raise KeyError(f"Unknown RID '{rid}'. Available: {', '.join(runtimes.keys())}")
        spec = runtimes[rid]
        out_dir = runtimes_root / rid / "native"
        out_file = out_dir / spec["file"]
        stamp_file = out_dir / ".pin-sha256"
        expected_archive_sha = spec["archiveSha256"].lower()

        if (
            not args.force
            and out_file.is_file()
            and stamp_file.is_file()
            and stamp_file.read_text(encoding="utf-8").strip() == f"{tag}:{expected_archive_sha}"
        ):
            print(f"[wgpu-native] {rid}: up-to-date ({out_file.name})")
            continue

        url = f"{repo_url}/releases/download/{tag}/{spec['asset']}"
        print(f"[wgpu-native] {rid}: downloading {url} ...")
        archive_bytes = download_verified(url, expected_archive_sha)

        with zipfile.ZipFile(io.BytesIO(archive_bytes)) as zf:
            lib_bytes = zf.read(spec["entry"])

        out_dir.mkdir(parents=True, exist_ok=True)
        out_file.write_bytes(lib_bytes)
        if not rid.startswith("win-"):
            out_file.chmod(out_file.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        stamp_file.write_text(f"{tag}:{expected_archive_sha}\n", encoding="utf-8")
        print(f"[wgpu-native] {rid}: verified and staged {out_file.relative_to(repo_root)} ({len(lib_bytes) // 1024} KB)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
