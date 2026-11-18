#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GalA11y Forge · Ren'Py 覆盖层启动证据检查（八条）

静态 lint 只能证明"代码像对的"；这个脚本读**游戏真跑过一次**留下的
`a11y_speech.log`，逐条核对补丁是否真的挂上、真的说话、真的没报错。

必须在用户授权的副本上跑（AGENTS.md：D:\\Steam 只读，不启动未授权游戏）。
用法：
    python check_boot.py "D:\\tmp\\ddlc-a11y"
    python check_boot.py <root> --min-dialogues 3 --json evidence\\boot.json
"""

import argparse
import io
import json
import os
import sys
import traceback

CHECKS = []


def check(key, title, ok, detail=""):
    CHECKS.append({"key": key, "title": title, "ok": bool(ok), "detail": detail})


def read_text(path, limit=4000000):
    if not os.path.isfile(path):
        return ""
    try:
        return io.open(path, "r", encoding="utf-8", errors="replace").read()[:limit]
    except Exception:
        return ""


def main(argv=None):
    ap = argparse.ArgumentParser(description="Ren'Py 覆盖层启动证据检查")
    ap.add_argument("root", help="游戏根目录（含 game/ 与 a11y_speech.log）")
    ap.add_argument("--min-dialogues", type=int, default=1)
    ap.add_argument("--expect-version", default="0.2.0.1")
    ap.add_argument("--json", default="")
    opts = ap.parse_args(argv)

    root = os.path.abspath(opts.root)
    log = read_text(os.path.join(root, "a11y_speech.log"))
    eng = read_text(os.path.join(root, "log.txt"))
    trace = read_text(os.path.join(root, "traceback.txt"))

    check("B1", "补丁已加载（日志含版本号）",
          ("补丁 %s" % opts.expect_version) in log,
          "a11y_speech.log %d 字节" % len(log))
    check("B2", "say filter 已挂上", "say_filter=True" in log)
    check("B3", "引擎自语音被关掉（防双朗读）",
          "自语音" in log or "self_voicing" in log)
    check("B4", "朗读后端已选定",
          ("[A11y后端]" in log) or ("朗读后端" in log) or ("后端=" in log),
          next((l for l in log.splitlines() if "后端" in l), ""))
    dialogue = [l for l in log.splitlines() if "[A11y对话]" in l]
    check("B5", "至少 %d 条对话被朗读" % opts.min_dialogues,
          len(dialogue) >= opts.min_dialogues, "实际 %d 条" % len(dialogue))
    ui = [l for l in log.splitlines() if ("[A11y焦点]" in l or "[A11y菜单]" in l
                                          or "[A11y屏幕]" in l)]
    check("B6", "界面/选项至少播报过一次", bool(ui), ui[-1][:120] if ui else "")
    check("B7", "init 1200 键位已登记",
          "underlay 已追加 Keymap" in log and "init 1200" in log,
          next((l for l in log.splitlines() if "underlay" in l), ""))
    errs = [l for l in log.splitlines() if "[A11y错误]" in l]
    bad_trace = "a11y" in trace.lower()
    check("B8", "运行期无补丁异常", not errs and not bad_trace,
          (errs[:2] or ("traceback.txt 提到 a11y" if bad_trace else "")))

    failed = [c for c in CHECKS if not c["ok"]]
    width = max(len(c["title"]) for c in CHECKS)
    for c in CHECKS:
        print("[%s] %s %-*s %s" % ("PASS" if c["ok"] else "FAIL", c["key"],
                                   width, c["title"], c["detail"] or ""))
    print("")
    print("结果: %d/%d 通过%s" % (len(CHECKS) - len(failed), len(CHECKS),
                                 "" if not failed else " —— 未通过：%s" % ",".join(
                                     c["key"] for c in failed)))
    if opts.json:
        d = os.path.dirname(os.path.abspath(opts.json))
        if d and not os.path.isdir(d):
            os.makedirs(d)
        payload = {"root": root, "engine_log_bytes": len(eng),
                   "checks": CHECKS, "passed": len(CHECKS) - len(failed),
                   "total": len(CHECKS)}
        io.open(opts.json, "w", encoding="utf-8", newline="\n").write(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        sys.exit(2)
