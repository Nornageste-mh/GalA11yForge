#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GalA11y Forge · Ren'Py 零归档覆盖层生成器（原型 v0.2.0.1）

把一个 Ren'Py 游戏目录变成"能读屏"的补丁包：只生成放在 game/ 下的松散 .rpy
+ 一个 SAPI 桥 a11y_say.vbs + 清单/卸载脚本；不碰 .rpa、不改游戏原文件。

用法：
    # 1) 只看检测结果（不写盘）
    python renpy_a11y_gen.py --game "D:\\...\\Doki Doki Literature Club" --inspect out.json

    # 2) 生成到任意输出目录（默认，不触碰游戏）
    python renpy_a11y_gen.py --game <游戏根> --out build\\ddlc

    # 3) 部署到游戏目录（Steam 目录一律拒绝；已有同名补丁目录也拒绝）
    python renpy_a11y_gen.py --game <非 Steam 游戏根> --out build\\x --apply

设计依据见 research/14-renpy/REPORT.md；模板在 templates/ 下，改模板后重新生成。
"""

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
TPL = os.path.join(HERE, "templates")
GEN_VERSION = "0.2.0.1"
MANIFEST_NAME = "a11y_manifest.json"

PLATFORM_DIR = "game/a11y_platform"
PLATFORM_FILES = [
    "00_contract.rpy",
    "01_speech.rpy",
    "02_dialogue.rpy",
    "03_menu.rpy",
    "04_focus.rpy",
    "05_keys.rpy",
]
ROOT_FILES = [
    ("root/a11y_say.vbs", "a11y_say.vbs"),
    ("root/README-A11Y.txt", "README-A11Y.txt"),
    ("root/uninstall.ps1", "uninstall.ps1"),
]


# ── 小工具 ──────────────────────────────────────────────────────────────────

def read_text(path):
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def write_text(path, text, encoding="utf-8"):
    d = os.path.dirname(path)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    with io.open(path, "w", encoding=encoding, newline="\n") as f:
        f.write(text)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def human(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return "%.1f%s" % (n, unit)
        n /= 1024.0


def slugify(name):
    s = re.sub(r"[^A-Za-z0-9._-]+", "-", name).strip("-._")
    if not s:
        s = "game"
    return s[:48].lower()


# ── 检测 ────────────────────────────────────────────────────────────────────

def _parse_engine_meta(root):
    """三种发行格式都要认：
    - 6.99：renpy/__init__.py 里 version_tuple=(6,99,12,4,vc_version)，
            renpy/vc_version.py 只有 vc_version = 2187（实测）
    - 8.x ：renpy/vc_version.py 里 version='8.5.2.26010301' + branch/nightly
    """
    meta = {"version": "", "version_tuple": [], "version_name": "",
            "branch": "", "nightly": None, "vc_file": ""}
    chunks = []
    for rel in (os.path.join("renpy", "__init__.py"),
                os.path.join("renpy", "vc_version.py")):
        p = os.path.join(root, rel)
        if os.path.isfile(p):
            chunks.append(read_text(p))
            meta["vc_file"] = rel.replace("\\", "/") if not meta["vc_file"] else meta["vc_file"]
    if not chunks:
        return meta
    src = "\n".join(chunks)
    m = re.search(r"version_tuple\s*=\s*\(([^)]*)\)", src)
    if m:
        nums = []
        for part in m.group(1).split(","):
            part = part.strip()
            if re.match(r"^\d+$", part):
                nums.append(int(part))
            else:
                break
        meta["version_tuple"] = nums
        meta["version"] = ".".join(str(x) for x in nums)
    if not meta["version"]:
        m = re.search(r"\bversion\s*=\s*['\"]([0-9][0-9.]*)['\"]", src)
        if m:
            meta["version"] = m.group(1).strip(".")
            meta["version_tuple"] = [int(x) for x in meta["version"].split(".") if x.isdigit()]
    m = re.search(r'version_name\s*=\s*["\']([^"\']*)', src)
    if m:
        meta["version_name"] = m.group(1)
    m = re.search(r'branch\s*=\s*["\']([^"\']*)', src)
    if m:
        meta["branch"] = m.group(1)
    if re.search(r"^\s*nightly\s*=\s*True", src, re.M):
        meta["nightly"] = True
    elif re.search(r"^\s*nightly\s*=\s*False", src, re.M):
        meta["nightly"] = False
    return meta


def _detect_python(root):
    lib = os.path.join(root, "lib")
    found = []
    if os.path.isdir(lib):
        for name in sorted(os.listdir(lib)):
            p = os.path.join(lib, name)
            if not os.path.isdir(p):
                continue
            if name.startswith("py3"):
                found.append((3, name))
            elif name.startswith("windows") or name.startswith("linux"):
                found.append((2, name))
    if not found:
        return {"major": 0, "libs": []}
    return {"major": found[0][0], "libs": [n for _, n in found]}


def _detect_rpa(root):
    gdir = os.path.join(root, "game")
    out = []
    if not os.path.isdir(gdir):
        return out
    for name in sorted(os.listdir(gdir)):
        if not name.lower().endswith(".rpa"):
            continue
        p = os.path.join(gdir, name)
        if not os.path.isfile(p):
            continue
        head = b""
        try:
            with open(p, "rb") as f:
                head = f.read(16)
        except OSError:
            pass
        try:
            head_s = head.split(b"\0")[0].decode("ascii", "replace").strip()
        except Exception:
            head_s = ""
        out.append({"name": name, "bytes": os.path.getsize(p), "head": head_s})
    return out


def _detect_loose_scripts(root):
    gdir = os.path.join(root, "game")
    rpy = []
    rpyc = []
    overlays = []
    if not os.path.isdir(gdir):
        return {"rpy": rpy, "rpyc": rpyc, "overlays": overlays}
    for base, dirs, files in os.walk(gdir):
        rel_dir = os.path.relpath(base, gdir)
        depth = 0 if rel_dir == "." else rel_dir.count(os.sep) + 1
        if depth <= 1 and os.path.basename(base).lower().startswith("a11y"):
            overlays.append(rel_dir.replace("\\", "/"))
        if depth > 3:
            dirs[:] = []
            continue
        for name in files:
            low = name.lower()
            rel = os.path.join(rel_dir, name) if rel_dir != "." else name
            rel = rel.replace("\\", "/")
            if low.endswith(".rpy"):
                rpy.append(rel)
            elif low.endswith(".rpyc"):
                rpyc.append(rel)
        dirs[:] = [d for d in dirs if d != "cache"]
    return {"rpy": sorted(rpy), "rpyc": sorted(rpyc), "overlays": sorted(set(overlays))}


def _detect_nvda(root):
    gdir = os.path.join(root, "game")
    found = []
    for base in (gdir, root):
        for bits in (32, 64):
            p = os.path.join(base, "nvdaControllerClient%d.dll" % bits)
            if os.path.isfile(p):
                found.append({"path": os.path.relpath(p, root).replace("\\", "/"),
                              "bits": bits, "bytes": os.path.getsize(p)})
    return found


def _detect_say_vbs(root):
    lib = os.path.join(root, "lib")
    out = []
    if os.path.isdir(lib):
        for name in sorted(os.listdir(lib)):
            p = os.path.join(lib, name, "say.vbs")
            if os.path.isfile(p):
                out.append({"path": os.path.relpath(p, root).replace("\\", "/"),
                            "bytes": os.path.getsize(p)})
    return out


def _detect_common(root):
    p = os.path.join(root, "renpy", "common", "00accessibility.rpy")
    if os.path.isfile(p):
        return {"has_00accessibility": True, "bytes": os.path.getsize(p)}
    return {"has_00accessibility": False, "bytes": 0}


def _detect_logs(root):
    out = {}
    for name in ("log.txt", "a11y_speech.log", "traceback.txt", "errors.txt"):
        p = os.path.join(root, name)
        if os.path.isfile(p):
            out[name] = {"bytes": os.path.getsize(p),
                         "mtime": time.strftime("%Y-%m-%d %H:%M:%S",
                                                time.localtime(os.path.getmtime(p)))}
    return out


def detect_game(root):
    root = os.path.abspath(root)
    info = {"root": root, "exists": os.path.isdir(root)}
    if not info["exists"]:
        return info
    engine = _parse_engine_meta(root)
    py = _detect_python(root)
    loose = _detect_loose_scripts(root)
    info.update({
        "engine": engine,
        "python_major": py["major"],
        "python_libs": py["libs"],
        "rpa": _detect_rpa(root),
        "loose_scripts": loose,
        "rpa_bytes": sum(x["bytes"] for x in _detect_rpa(root)),
        "nvda_client": _detect_nvda(root),
        "say_vbs": _detect_say_vbs(root),
        "common": _detect_common(root),
        "logs": _detect_logs(root),
        "is_steam": "steamapps" in [p.lower() for p in root.replace("/", "\\").split("\\")],
        "conflict_overlay": bool(loose["overlays"]),
        "exe": [],
    })
    for name in sorted(os.listdir(root)):
        if name.lower().endswith(".exe"):
            p = os.path.join(root, name)
            if os.path.isfile(p):
                info["exe"].append({"name": name, "bytes": os.path.getsize(p)})
    return info


# ── 渲染 ────────────────────────────────────────────────────────────────────

def build_tokens(game, det, patch_version, tts_pref, sapi_rate):
    return {
        "@@GAME_NAME@@": os.path.basename(game.rstrip("\\/")),
        "@@RENPY_VERSION@@": det["engine"]["version"] or "未知",
        "@@PY_MAJOR@@": str(det["python_major"] or "?"),
        "@@PATCH_VERSION@@": patch_version,
        "@@BUILD@@": "renpy-a11y-gen %s (%s)" % (GEN_VERSION, time.strftime("%Y-%m-%d")),
        "@@TTS_PREF@@": tts_pref,
        "@@SAPI_RATE@@": str(int(sapi_rate)),
        "@@DATE@@": time.strftime("%Y-%m-%d %H:%M:%S"),
    }


def render(tokens, text):
    out = text
    for key, val in tokens.items():
        out = out.replace(key, val)
    left = re.findall(r"@@[A-Z_]+@@", out)
    if left:
        raise SystemExit("模板里还有未替换的占位符: %s" % sorted(set(left)))
    return out


def build_outputs(game, out_dir, det, opts):
    tokens = build_tokens(game, det, opts.patch_version, opts.tts, opts.rate)
    produced = []

    for name in PLATFORM_FILES:
        src = os.path.join(TPL, "platform", name)
        text = render(tokens, read_text(src))
        rel = "%s/%s" % (PLATFORM_DIR, name)
        dst = os.path.join(out_dir, rel.replace("/", os.sep))
        write_text(dst, text)
        produced.append((rel, dst, "platform"))

    for src_rel, target_rel in ROOT_FILES:
        src = os.path.join(TPL, src_rel)
        text = render(tokens, read_text(src))
        dst = os.path.join(out_dir, target_rel)
        encoding = "utf-8-sig" if target_rel.endswith(".ps1") else "utf-8"
        write_text(dst, text, encoding=encoding)
        produced.append((target_rel, dst, "root"))

    files = []
    for rel, dst, role in produced:
        data = open(dst, "rb").read()
        files.append({"path": rel, "role": role, "bytes": len(data),
                      "sha256": sha256_bytes(data)})

    manifest = {
        "schema": "gala11y.renpy.overlay/1",
        "generator": "renpy-a11y-gen",
        "generator_version": GEN_VERSION,
        "patch_version": opts.patch_version,
        "created_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "game": {"root": game, "name": os.path.basename(game.rstrip("\\/")),
                 "engine": det["engine"]["version"],
                 "python_major": det["python_major"],
                 "is_steam": det["is_steam"]},
        "files": files,
    }
    write_text(os.path.join(out_dir, MANIFEST_NAME),
               json.dumps(manifest, ensure_ascii=False, indent=2))

    plan = {
        "schema": "gala11y.renpy.deploy_plan/1",
        "game_root": game,
        "guard": "steamapps 目录只读：生成器拒绝 --apply；请复制到临时目录后部署",
        "conflict_overlay": det["conflict_overlay"],
        "steps": [{"op": "mkdir", "path": PLATFORM_DIR}]
        + [{"op": "copy", "source": f["path"], "target": f["path"],
            "sha256": f["sha256"]} for f in files],
        "verify": ["静态 lint: python lint_overlay.py <out>",
                   "启动证据: python check_boot.py --game <游戏根>"],
    }
    write_text(os.path.join(out_dir, "deploy_plan.json"),
               json.dumps(plan, ensure_ascii=False, indent=2))
    return manifest, plan


def apply_outputs(out_dir, manifest, det, force):
    game = det["root"]
    if det["is_steam"]:
        raise SystemExit("拒绝写入 Steam 目录（AGENTS.md 只读）：%s\n"
                         "请把游戏复制到临时目录，或只用 --out 生成补丁包。" % game)
    if det["conflict_overlay"]:
        raise SystemExit("游戏 game/ 下已有 a11y* 补丁目录（%s），拒绝覆盖。"
                         % ", ".join(det["loose_scripts"]["overlays"]))
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup_dir = os.path.join(game, "a11y_backup", stamp)
    applied = []
    for entry in manifest["files"]:
        rel = entry["path"].replace("/", os.sep)
        src = os.path.join(out_dir, rel)
        dst = os.path.join(game, rel)
        if os.path.exists(dst):
            if sha256_file(dst) == entry["sha256"]:
                applied.append((entry["path"], "unchanged"))
                continue
            if not force:
                raise SystemExit("目标已存在且内容不同，拒绝覆盖: %s（用 --force 备份后覆盖）" % dst)
            bdst = os.path.join(backup_dir, rel)
            os.makedirs(os.path.dirname(bdst), exist_ok=True)
            shutil.copy2(dst, bdst)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        applied.append((entry["path"], "copied"))
    write_text(os.path.join(game, MANIFEST_NAME),
               json.dumps(manifest, ensure_ascii=False, indent=2))
    return applied, (backup_dir if os.path.isdir(backup_dir) else "")


# ── CLI ─────────────────────────────────────────────────────────────────────

def make_slug(game):
    base = os.path.basename(game.rstrip("\\/"))
    s = slugify(base)
    if re.search(r"[^A-Za-z0-9._-]", base):
        s = "%s-%s" % (s, hashlib.sha1(base.encode("utf-8")).hexdigest()[:6])
    return s


def main(argv=None):
    ap = argparse.ArgumentParser(description="Ren'Py 零归档无障碍覆盖层生成器（原型）")
    ap.add_argument("--game", required=True, help="游戏根目录（含 renpy/ 与 game/）")
    ap.add_argument("--out", default="", help="输出目录；默认 build/<slug>")
    ap.add_argument("--slug", default="", help="输出目录名（默认由游戏目录名生成）")
    ap.add_argument("--patch-version", default="0.2.0.1")
    ap.add_argument("--tts", default="auto", choices=["auto", "nvda", "sapi", "log"])
    ap.add_argument("--rate", type=int, default=0, help="SAPI 语速 -10..10")
    ap.add_argument("--inspect", default="", help="只检测，把 JSON 写到这个路径后退出")
    ap.add_argument("--json", action="store_true", help="把检测 JSON 打到 stdout")
    ap.add_argument("--apply", action="store_true", help="把生成结果复制进游戏目录")
    ap.add_argument("--force", action="store_true", help="覆盖已存在且内容不同的目标")
    opts = ap.parse_args(argv)

    game = os.path.abspath(opts.game)
    det = detect_game(game)
    if not det["exists"]:
        raise SystemExit("游戏目录不存在: %s" % game)
    if not os.path.isdir(os.path.join(game, "renpy")):
        raise SystemExit("不像 Ren'Py 游戏（缺 renpy/）: %s" % game)
    if not os.path.isdir(os.path.join(game, "game")):
        raise SystemExit("不像 Ren'Py 游戏（缺 game/）: %s" % game)

    if opts.inspect:
        write_text(os.path.abspath(opts.inspect),
                   json.dumps(det, ensure_ascii=False, indent=2))
        print("检测结果已写入 %s" % opts.inspect)
        return 0
    if opts.json:
        print(json.dumps(det, ensure_ascii=False, indent=2))
        return 0

    out_dir = opts.out or os.path.join(HERE, "build", opts.slug or make_slug(game))
    out_dir = os.path.abspath(out_dir)
    manifest, plan = build_outputs(game, out_dir, det, opts)

    print("游戏      : %s" % game)
    print("引擎      : Ren'Py %s / Python %s / %s" % (
        det["engine"]["version"] or "?", det["python_major"] or "?",
        "Steam(只读)" if det["is_steam"] else "非 Steam"))
    print("归档      : %d 个 .rpa，共 %s" % (len(det["rpa"]), human(det["rpa_bytes"])))
    print("松散脚本  : %d 个 .rpy / %d 个 .rpyc；已有补丁目录=%s" % (
        len(det["loose_scripts"]["rpy"]), len(det["loose_scripts"]["rpyc"]),
        det["loose_scripts"]["overlays"] or "无"))
    print("自检      : 00accessibility=%s; say.vbs=%d; NVDA client=%d" % (
        det["common"]["has_00accessibility"], len(det["say_vbs"]), len(det["nvda_client"])))
    print("输出      : %s（%d 个文件）" % (out_dir, len(manifest["files"])))
    for f in manifest["files"]:
        print("  - %-40s %8d B  %s" % (f["path"], f["bytes"], f["sha256"][:12]))

    if opts.apply:
        applied, backup = apply_outputs(out_dir, manifest, det, opts.force)
        print("已部署    : %d 个文件（备份目录 %s）" % (len(applied), backup or "无（目标原本没有同名文件）"))
    else:
        print("未部署    : 需要 --apply 才会写入游戏目录（默认只产出补丁包）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
