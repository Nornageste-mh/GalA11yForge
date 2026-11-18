#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""汇总 W2-G 的机器可读证据到 research/14-renpy/evidence/。

产物：
    inventory.json        三款游戏的只读盘点（引擎/归档/松散脚本/冲突面）
    generated-files.json  三个 build 的全部生成文件（sha256/bytes）
    sample-ddlc/          DDLC（6.99 / Python 2）生成样例，便于直接审阅
"""

import hashlib
import io
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
EVID = os.path.join(ROOT, "research", "14-renpy", "evidence")
BUILD = os.path.join(HERE, "build")

GAMES = [
    ("ddlc", r"D:\Steam\steamapps\common\Doki Doki Literature Club"),
    ("mei", r"D:\Steam\steamapps\common\mei"),
    ("obe", r"D:\Steam\steamapps\common\永恒与星辰与日常"),
]

sys.path.insert(0, HERE)
import renpy_a11y_gen as gen  # noqa: E402


def sha256(path):
    h = hashlib.sha256()
    with io.open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not os.path.isdir(EVID):
        os.makedirs(EVID)
    inv = {"schema": "gala11y.renpy.inventory/1",
           "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
           "note": "只读盘点；D:\\Steam 下未写入任何文件",
           "games": []}
    for slug, path in GAMES:
        d = gen.detect_game(path)
        d["slug"] = slug
        inv["games"].append(d)

    files = {"schema": "gala11y.renpy.generated/1",
             "generated_at": inv["generated_at"], "builds": {}}
    for slug, _ in GAMES:
        bdir = os.path.join(BUILD, slug)
        entries = []
        for dirpath, _dirnames, names in os.walk(bdir):
            for n in sorted(names):
                p = os.path.join(dirpath, n)
                entries.append({"path": os.path.relpath(p, bdir).replace("\\", "/"),
                                "bytes": os.path.getsize(p),
                                "sha256": sha256(p)})
        files["builds"][slug] = entries

    kept = ["00_contract.rpy", "01_speech.rpy", "02_dialogue.rpy", "03_menu.rpy",
            "04_focus.rpy", "05_keys.rpy", "a11y_say.vbs", "README-A11Y.txt",
            "uninstall.ps1", "a11y_manifest.json", "deploy_plan.json"]
    sample = os.path.join(EVID, "sample-ddlc")
    if os.path.isdir(sample):
        shutil.rmtree(sample)
    os.makedirs(sample)
    for name in kept:
        src = os.path.join(BUILD, "ddlc", name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(sample, name))
    plat = os.path.join(BUILD, "ddlc", "game", "a11y_platform")
    dst_plat = os.path.join(sample, "game", "a11y_platform")
    os.makedirs(dst_plat)
    for name in sorted(os.listdir(plat)):
        shutil.copy2(os.path.join(plat, name), os.path.join(dst_plat, name))

    for name, payload in (("inventory.json", inv), ("generated-files.json", files)):
        io.open(os.path.join(EVID, name), "w", encoding="utf-8", newline="\n").write(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n")
    total = sum(len(v) for v in files["builds"].values())
    print("inventory.json: %d 作" % len(inv["games"]))
    print("generated-files.json: %d 个文件" % total)
    print("sample-ddlc: %d 个文件" % sum(len(fs) for _, _, fs in os.walk(sample)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
