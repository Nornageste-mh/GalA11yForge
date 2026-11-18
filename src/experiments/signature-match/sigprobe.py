#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""sigprobe.py —— 只读的「游戏目录 → 证据 JSON」探针（GalA11y Forge R19 原型）。

用途：
  给签名匹配器（match.py）提供与引擎无关的目录证据：
    - 文件布局（相对路径、顶层条目、关键文件）
    - 托管 DLL 名单（*_Data/Managed、BepInEx/interop）
    - exe / UnityPlayer 的 VersionInfo（ProductName、FileVersion 等）
    - Unity 版本猜测、Unity Mono / IL2CPP 判定
    - IL2CPP：global-metadata.dat / GameAssembly.dll 中「签名库声明的 token」出现次数
    - Ren'Py 版本、KiriKiri 目录特征、RPG Maker MV/MZ 特征

设计约束：
  * 只读：不写游戏目录，不导出资源；对 IL2CPP 只统计 ASCII token 次数与类型名样本，
    不落盘任何台词/文本。
  * 输出 schema = galforge-probe-v1（见 README.md）。
  * 不依赖第三方库；Python 3.9+，Windows（VersionInfo 用 ctypes，失败则留空）。

用法：
  python sigprobe.py --game "D:\\Steam\\steamapps\\common\\Build" \
      --signatures ..\\..\\..\\research\\21-signature-db\\evidence\\signatures.json \
      --out probe.json --name 五天一夜
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import sys
from ctypes import wintypes
from datetime import datetime, timezone, timedelta

SCHEMA = "galforge-probe-v1"
TOOL = {"name": "galforge-sigprobe", "version": "0.1.0"}

TEXT_EXT = {".dll", ".exe", ".xp3", ".rpa", ".dat", ".assets", ".bundle", ".json", ".py", ".ini"}
SKIP_DIRS = {".git", "savedata", "logs", "log", "cache", "Cache", "__pycache__"}


# ---------------------------------------------------------------- VersionInfo
def _query_value(buf, sub):
    p = ctypes.c_void_p()
    n = ctypes.c_uint()
    ok = ctypes.windll.version.VerQueryValueW(buf, sub, ctypes.byref(p), ctypes.byref(n))
    if not ok or not p.value:
        return None
    return ctypes.wstring_at(p.value, max(0, n.value - 1))


def file_version_info(path: str) -> dict:
    """读取 PE VersionInfo 的 StringFileInfo（只读）。失败返回 {}。"""
    try:
        size = ctypes.windll.version.GetFileVersionInfoSizeW(path, None)
        if not size:
            return {}
        buf = ctypes.create_string_buffer(size)
        if not ctypes.windll.version.GetFileVersionInfoW(path, 0, size, buf):
            return {}
        p = ctypes.c_void_p()
        n = ctypes.c_uint()
        if not ctypes.windll.version.VerQueryValueW(buf, "\\VarFileInfo\\Translation",
                                                    ctypes.byref(p), ctypes.byref(n)):
            return {}
        lang, cp = ctypes.cast(p, ctypes.POINTER(ctypes.c_uint16))[0], \
            ctypes.cast(p, ctypes.POINTER(ctypes.c_uint16))[1]
        prefix = f"\\StringFileInfo\\{lang:04x}{cp:04x}\\"
        out = {}
        for key in ("ProductName", "FileDescription", "FileVersion", "CompanyName",
                    "OriginalFilename", "InternalName"):
            v = _query_value(buf, prefix + key)
            if v:
                out[key] = v.strip()
        return out
    except Exception as exc:  # pragma: no cover - 只在异常环境触发
        return {"_error": f"{type(exc).__name__}: {exc}"}


# ---------------------------------------------------------------- helpers
def rel_posix(root: str, path: str) -> str:
    return os.path.relpath(path, root).replace("\\", "/")


def read_head(path: str, limit: int = 4096) -> bytes:
    try:
        with open(path, "rb") as fh:
            return fh.read(limit)
    except OSError:
        return b""


def guess_unity_version(data_dir: str, vinfo: dict) -> str | None:
    gg = os.path.join(data_dir, "globalgamemanagers")
    head = read_head(gg, 65536)
    m = re.search(rb"(20\d\d\.\d+\.\d+[abfp]\d+)", head)
    if m:
        return m.group(1).decode("ascii", "ignore")
    for key in ("FileVersion", "ProductVersion"):
        v = vinfo.get(key) or ""
        m = re.search(r"(20\d\d\.\d+\.\d+[abfp]\d+)", v)
        if m:
            return m.group(1)
    return None


def renpy_version(root: str) -> str | None:
    for rel in ("renpy/vc_version.py", "renpy/__init__.py"):
        p = os.path.join(root, rel)
        if os.path.isfile(p):
            txt = read_head(p, 4096).decode("utf-8", "ignore")
            m = re.search(r"version\s*=\s*\(([^)]{3,60})\)", txt)
            if m:
                return m.group(1).replace(" ", "")
            m = re.search(r"version_tuple\s*=\s*\(([^)]{3,60})\)", txt)
            if m:
                return m.group(1).replace(" ", "")
            # Ren'Py 8.6 起 vc_version.py 用字符串而非元组（实测 OBE: version = '8.6.0.26032101'）
            m = re.search(r"version\s*=\s*['\"]([0-9][^'\"]{2,40})['\"]", txt)
            if m:
                return m.group(1)
    return None


def scan_tokens(path: str, tokens: list[str]) -> dict:
    """在二进制文件中统计 ASCII token 次数（大小写不敏感）。只返回计数。"""
    counts = {t: 0 for t in tokens}
    if not tokens or not os.path.isfile(path):
        return counts
    lowered = [(t, t.encode("ascii", "ignore").lower()) for t in tokens if t.isascii()]
    overlap = max((len(b) for _, b in lowered), default=1)
    prev = b""
    try:
        with open(path, "rb") as fh:
            while True:
                chunk = fh.read(8 << 20)
                if not chunk:
                    break
                buf = (prev + chunk).lower()
                for t, b in lowered:
                    if b:
                        counts[t] += buf.count(b)
                prev = buf[-overlap:]
    except OSError:
        pass
    return counts


def type_name_sample(metadata_path: str, cap: int = 6000) -> list[str]:
    """从 IL2CPP global-metadata.dat 中抽取「像类型名」的 ASCII 串（只读、限量）。

    注意：这是字符串堆启发式，不是完整 metadata 解析；只用于给正则签名提供线索。
    """
    if not os.path.isfile(metadata_path):
        return []
    try:
        with open(metadata_path, "rb") as fh:
            data = fh.read(64 << 20)
    except OSError:
        return []
    seen: dict[str, None] = {}
    for m in re.finditer(rb"[A-Za-z_][A-Za-z0-9_.<>`]{2,79}", data):
        s = m.group(0).decode("ascii", "ignore")
        if "." in s and re.search(r"[A-Z]", s) and not s.endswith("."):
            seen[s] = None
            if len(seen) >= cap:
                break
    return sorted(seen)


def detect_flavor(root: str, top: list[str], data_dirs: list[str]) -> str:
    if any(n.endswith("_Data") for n in top):
        d = os.path.join(root, [n for n in top if n.endswith("_Data")][0])
        if os.path.isfile(os.path.join(root, "GameAssembly.dll")) and \
                os.path.isdir(os.path.join(d, "il2cpp_data")):
            return "unity-il2cpp"
        if os.path.isfile(os.path.join(d, "Managed", "Assembly-CSharp.dll")):
            return "unity-mono"
        if os.path.isfile(os.path.join(root, "GameAssembly.dll")):
            return "unity-il2cpp"
        return "unity-mono"
    if os.path.isdir(os.path.join(root, "renpy")) and os.path.isdir(os.path.join(root, "game")):
        return "renpy"
    if any(n.lower().endswith(".xp3") for n in top) and os.path.isfile(os.path.join(root, "plugin")):
        return "kirikiri"
    if os.path.isfile(os.path.join(root, "package.json")) and (
            os.path.isdir(os.path.join(root, "www", "js")) or os.path.isdir(os.path.join(root, "js"))):
        return "rpgmaker"
    if os.path.isdir(os.path.join(root, "resources")) or os.path.isfile(os.path.join(root, "nw.dll")):
        return "electron"
    return "unknown"


def detect_engine(flavor: str, top: list[str]) -> str:
    return {
        "unity-mono": "Unity (Mono)",
        "unity-il2cpp": "Unity (IL2CPP)",
        "renpy": "Ren'Py",
        "kirikiri": "KiriKiri Z",
        "rpgmaker": "RPG Maker MV/MZ",
        "electron": "Electron/nw.js",
    }.get(flavor, "unknown")


# ---------------------------------------------------------------- probe
def probe(root: str, name: str | None, tokens: list[str], max_paths: int = 30000) -> dict:
    root = os.path.abspath(root)
    paths: list[str] = []
    top_entries = []
    exe_version = []
    managed_dlls: list[str] = []
    interop_dlls: list[str] = []
    data_dirs: list[str] = []

    for entry in sorted(os.scandir(root), key=lambda e: e.name.lower()):
        try:
            is_dir = entry.is_dir()
            size = entry.stat().st_size if not is_dir else None
        except OSError:
            continue
        top_entries.append({"name": entry.name, "kind": "dir" if is_dir else "file", "bytes": size})
        if is_dir and entry.name.endswith("_Data"):
            data_dirs.append(entry.name)
        if (not is_dir) and entry.name.lower().endswith(".exe"):
            v = file_version_info(entry.path)
            if v:
                exe_version.append({"path": entry.name, **v})

    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if len(paths) >= max_paths:
                break
            full = os.path.join(dirpath, fn)
            paths.append(rel_posix(root, full))
        if len(paths) >= max_paths:
            break

    for dd in data_dirs:
        mdir = os.path.join(root, dd, "Managed")
        if os.path.isdir(mdir):
            managed_dlls += sorted(f for f in os.listdir(mdir) if f.lower().endswith(".dll"))
    idir = os.path.join(root, "BepInEx", "interop")
    if os.path.isdir(idir):
        interop_dlls = sorted(f for f in os.listdir(idir) if f.lower().endswith(".dll"))

    flavor = detect_flavor(root, [e["name"] for e in top_entries], data_dirs)
    unity = {"flavor": flavor.split("-", 1)[1] if flavor.startswith("unity-") else None,
             "unityPlayerDllPresent": os.path.isfile(os.path.join(root, "UnityPlayer.dll")),
             "il2CppMarkersPresent": os.path.isfile(os.path.join(root, "GameAssembly.dll"))}
    if data_dirs:
        dd = os.path.join(root, data_dirs[0])
        unity["dataDir"] = data_dirs[0]
        unity["versionGuess"] = guess_unity_version(dd, next(
            (v for v in exe_version if v.get("path", "").lower().startswith("unityplayer")), {}))
        gmm = os.path.join(dd, "il2cpp_data", "Metadata", "global-metadata.dat")
        if os.path.isfile(gmm):
            unity["globalMetadataBytes"] = os.path.getsize(gmm)
        ga = os.path.join(root, "GameAssembly.dll")
        if os.path.isfile(ga):
            unity["gameAssemblyBytes"] = os.path.getsize(ga)

    result = {
        "schema": SCHEMA,
        "tool": TOOL,
        "generated_at": datetime.now(timezone(timedelta(hours=8))).isoformat(timespec="seconds"),
        "game": {"name": name or os.path.basename(root), "root": root,
                 "exe_names": [e["name"] for e in top_entries if e["name"].lower().endswith(".exe")]},
        "layout": {"top_level": top_entries, "paths": paths, "path_count": len(paths),
                   "data_dirs": data_dirs, "managed_dlls": managed_dlls,
                   "interop_dlls": interop_dlls},
        "exe_version": exe_version,
        "unity": unity,
        "engine_hint": detect_engine(flavor, [e["name"] for e in top_entries]),
        "renpy_version": renpy_version(root) if flavor == "renpy" else None,
        "binary_tokens": {},
        "type_names_sample": [],
        "notes": [],
    }

    gmm = os.path.join(root, data_dirs[0], "il2cpp_data", "Metadata", "global-metadata.dat") \
        if data_dirs else ""

    def scan_group(label: str, paths_: list[str], limit: int = 48 << 20):
        """对一组文件做 token 计数（按文件累加），用于 IL2CPP metadata / interop / Mono 托管 DLL。"""
        counts: dict[str, int] = {t: 0 for t in tokens}
        used, total_bytes = [], 0
        for p in paths_:
            if not os.path.isfile(p):
                continue
            size = os.path.getsize(p)
            if total_bytes + size > limit:
                continue
            used.append(rel_posix(root, p))
            total_bytes += size
            for t, c in scan_tokens(p, tokens).items():
                counts[t] = counts.get(t, 0) + c
        if used:
            result["binary_tokens"][label] = {"files": used, "counts": counts}
        return counts

    if flavor == "unity-il2cpp":
        if gmm and os.path.isfile(gmm):
            scan_group("metadata", [gmm])
            result["type_names_sample"] = type_name_sample(gmm)
        scan_group("gameassembly", [os.path.join(root, "GameAssembly.dll")])
        if interop_dlls:
            idir = os.path.join(root, "BepInEx", "interop")
            pri = sorted(interop_dlls, key=lambda n: (not n.lower().startswith("assembly-csharp"),
                                                      "naninovel" not in n.lower(), n.lower()))
            scan_group("interop", [os.path.join(idir, n) for n in pri[:20]], limit=64 << 20)
        result["notes"].append("IL2CPP token 计数为原始字节串出现次数（大小写不敏感），非类型解析结果；"
                               "interop 计数仅在已装 BepInEx 时存在")
    elif flavor == "unity-mono":
        mdirs = [os.path.join(root, d, "Managed") for d in data_dirs]
        dll_paths = []
        for mdir in mdirs:
            if os.path.isdir(mdir):
                pri = sorted((f for f in os.listdir(mdir) if f.lower().endswith(".dll")),
                             key=lambda n: (not n.lower().startswith("assembly-csharp"), n.lower()))
                dll_paths += [os.path.join(mdir, n) for n in pri]
        if dll_paths:
            scan_group("managed", dll_paths[:6], limit=32 << 20)
        result["notes"].append("Mono 游戏优先用 unity-mono-analyzer 的 analysis.json；managed token 计数为回退证据")
    elif flavor == "kirikiri":
        result["notes"].append("KiriKiri 判定依据：顶层 *.xp3 + plugin/ 目录；ProductName 见 exe_version")
    elif flavor == "renpy":
        result["notes"].append("Ren'Py 判定依据：renpy/ + game/ 目录")
    return result


def load_tokens(signatures_path: str) -> list[str]:
    with open(signatures_path, "r", encoding="utf-8") as fh:
        db = json.load(fh)
    tokens = db.get("probe_tokens") or []
    return [t for t in tokens if isinstance(t, str) and t]


def main(argv=None):
    ap = argparse.ArgumentParser(description="GalA11y Forge 只读签名探针 (galforge-probe-v1)")
    ap.add_argument("--game", required=True, help="游戏安装目录")
    ap.add_argument("--signatures", help="signatures.json（用于取 probe_tokens；可选）")
    ap.add_argument("--out", help="输出 JSON 路径；缺省打印到 stdout")
    ap.add_argument("--name", help="覆盖游戏名")
    ap.add_argument("--max-paths", type=int, default=30000)
    args = ap.parse_args(argv)

    if not os.path.isdir(args.game):
        print(f"游戏目录不存在: {args.game}", file=sys.stderr)
        return 2
    tokens = load_tokens(args.signatures) if args.signatures else []
    data = probe(args.game, args.name, tokens, args.max_paths)
    text = json.dumps(data, ensure_ascii=False, indent=2)
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(text)
        print(f"wrote {args.out}  paths={data['layout']['path_count']}  "
              f"flavor={data['unity']['flavor']}  tokens={len(tokens)}")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
