#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GalA11y Forge · Ren'Py 覆盖层静态校验（原型）

在**没有 Ren'Py SDK** 的机器上能做的验证：
  1) 把每个 .rpy 里的 python 代码块抽出来，用本机 python 编译（语法错误必暴露）；
  2) 15 条结构性断言（钩子是否真的挂上、红线是否被破、版本差异是否兼容）；
  3) 清单/部署计划/卸载脚本的一致性（SHA256 逐文件核对）。

用法：
    python lint_overlay.py build\\ddlc
    python lint_overlay.py build\\ddlc --log evidence\\lint.log
"""

import argparse
import io
import json
import os
import re
import sys

PLATFORM_DIR = os.path.join("game", "a11y_platform")


# ── python 代码块抽取 & 编译 ────────────────────────────────────────────────

BLOCK_RE = re.compile(r"^\s*(?:init(?:\s+-?\d+)?\s+)?python\s*:\s*$")


def extract_blocks(text):
    """返回 [(start_line, code)]；按缩进裁出 python 块并 dedent。"""
    lines = text.splitlines()
    blocks = []
    i = 0
    while i < len(lines):
        if BLOCK_RE.match(lines[i]):
            base = len(lines[i]) - len(lines[i].lstrip())
            body = []
            j = i + 1
            while j < len(lines):
                ln = lines[j]
                if not ln.strip():
                    body.append("")
                    j += 1
                    continue
                ind = len(ln) - len(ln.lstrip())
                if ind <= base:
                    break
                body.append(ln)
                j += 1
            code_lines = []
            dedent = None
            for ln in body:
                if ln.strip():
                    ind = len(ln) - len(ln.lstrip())
                    if dedent is None or ind < dedent:
                        dedent = ind
            dedent = dedent or (base + 4)
            for ln in body:
                code_lines.append(ln[dedent:] if len(ln) >= dedent else ln.lstrip())
            blocks.append((i + 1, "\n".join(code_lines)))
            i = j
        else:
            i += 1
    return blocks


def compile_blocks(path):
    text = io.open(path, "r", encoding="utf-8", errors="replace").read()
    errors = []
    count = 0
    for start, code in extract_blocks(text):
        count += 1
        if not code.strip():
            continue
        try:
            compile(code, "%s:%d" % (os.path.basename(path), start), "exec")
        except SyntaxError as exc:
            errors.append("%s:%s python 块语法错误 @%d: %s" % (
                os.path.basename(path), exc.lineno, start, exc.msg))
    return count, errors


# ── 断言 ────────────────────────────────────────────────────────────────────

class Checker(object):
    def __init__(self, root):
        self.root = root
        self.results = []
        self.files = {}

    def load(self):
        pdir = os.path.join(self.root, PLATFORM_DIR)
        if not os.path.isdir(pdir):
            raise SystemExit("找不到 %s" % pdir)
        for name in sorted(os.listdir(pdir)):
            if name.endswith(".rpy"):
                p = os.path.join(pdir, name)
                self.files[name] = io.open(p, "r", encoding="utf-8", errors="replace").read()
        self.all_text = "\n".join(self.files[k] for k in sorted(self.files))

    def check(self, name, ok, detail=""):
        self.results.append((name, bool(ok), detail))

    def run(self):
        self.load()
        n = len(self.files)
        self.check("A1 平台文件齐全(6)", n == 6, "实际 %d 个: %s" % (n, ", ".join(sorted(self.files))))

        missing_utf8 = [k for k, v in self.files.items() if "coding: utf-8" not in v.splitlines()[0]]
        self.check("A2 每个 .rpy 首行 utf-8 声明", not missing_utf8, str(missing_utf8))

        # python 块编译
        total, errs = 0, []
        for k in sorted(self.files):
            c, e = compile_blocks(os.path.join(self.root, PLATFORM_DIR, k))
            total += c
            errs.extend(e)
        self.check("A3 python 块可编译", not errs, "%d 个块; %s" % (total, "; ".join(errs)))

        # say 钩子：原样返回
        dlg = self.files.get("02_dialogue.rpy", "")
        self.check("B1 挂载 config.say_menu_text_filter",
                   "renpy.config.say_menu_text_filter = _a11y_say_filter" in dlg)
        filt = re.search(r"def _a11y_say_filter\(s\):(.*?)(?=\n    def )", dlg, re.S)
        fbody = filt.group(1) if filt else ""
        self.check("B2 filter 原样返回（红线）",
                   "return out" in fbody and "return _a11y_clean" not in fbody and "return text" not in fbody,
                   "函数体首行: %r" % fbody.strip().splitlines()[:1])
        self.check("B3 链式保留游戏自己的 filter",
                   "_A11Y_ORIG_SAY_FILTER" in dlg and "orig(s)" in dlg)

        # TTS：接管 + 关自语音（防双朗读）
        sp = self.files.get("01_speech.rpy", "")
        self.check("B4 接管 config.tts_function",
                   "renpy.config.tts_function = _a11y_tts_function" in sp)
        self.check("B5 强制 self_voicing=False",
                   "self_voicing = False" in sp and "self_voicing" in self.files.get("04_focus.rpy", ""))

        # 键位：init 1200 + underlay + 无参处理函数
        keys = self.files.get("05_keys.rpy", "")
        self.check("B6 键位在 init 1200 登记", "init 1200 python:" in keys and "config.underlay.append" in keys)
        self.check("B7 键处理函数无参数",
                   bool(re.search(r"def _a11y_key_\w+\(\):", keys))
                   and not re.search(r"def _a11y_key_\w+\((self|ev)", keys)
                   and "compile_event" in keys)
        self.check("B8 使用引擎 compile_event 验证键名", "compile_event" in keys)

        # 版本差异：_tts_all 签名
        c0 = self.files.get("00_contract.rpy", "")
        self.check("B9 _tts_all 6.99/8.x 双签名回退",
                   "_tts_all(raw=True)" in c0 and "except TypeError" in c0 and "_tts_all()" in c0)

        # 焦点：不缓存 focus_list，走轮询
        menu = self.files.get("03_menu.rpy", "")
        self.check("B10 focus_list 每次现取（不缓存引用）",
                   "renpy.display.focus.focus_list" in menu and "list(fl)" in menu
                   and "focus_list = [" not in menu)

        # 周期回调登记
        foc = self.files.get("04_focus.rpy", "")
        self.check("B11 心跳登记 periodic_callbacks",
                   "config.periodic_callbacks.append(_a11y_periodic)" in foc)

        # 日志与关闭开关
        self.check("B12 日志 + a11y_off.flag 关闭开关",
                   "a11y_speech.log" in c0 and "a11y_off.flag" in c0
                   and "_a11y_flag_disabled" in c0)

        # 不越界：不写游戏资源、不引用绝对路径
        bad_paths = re.findall(r"[A-Za-z]:\\\\[^\s\"']+", self.all_text)
        self.check("C1 无硬编码盘符/绝对路径", not bad_paths, str(bad_paths[:3]))
        self.check("C2 不写 .rpa/不删游戏文件",
                   "rpa" not in self.all_text.lower().replace("rpa_bytes", "")
                   and "os.remove" in sp  # 仅允许删自己的临时文件
                   and "shutil.rmtree" not in self.all_text)

        # 清单一致性
        man_path = os.path.join(self.root, "a11y_manifest.json")
        plan_path = os.path.join(self.root, "deploy_plan.json")
        ok_manifest = False
        detail = "缺清单"
        if os.path.isfile(man_path):
            import hashlib
            man = json.load(io.open(man_path, "r", encoding="utf-8"))
            bad = []
            for f in man.get("files", []):
                p = os.path.join(self.root, f["path"].replace("/", os.sep))
                if not os.path.isfile(p):
                    bad.append(f["path"] + ":缺文件")
                    continue
                h = hashlib.sha256(open(p, "rb").read()).hexdigest()
                if h != f["sha256"]:
                    bad.append(f["path"] + ":哈希不符")
            ok_manifest = not bad and len(man.get("files", [])) >= 9
            detail = "文件数=%d %s" % (len(man.get("files", [])), bad[:3] or "")
        self.check("C3 清单 SHA256 全部对得上", ok_manifest, detail)

        ok_plan = os.path.isfile(plan_path)
        if ok_plan:
            plan = json.load(io.open(plan_path, "r", encoding="utf-8"))
            ok_plan = bool(plan.get("steps")) and bool(plan.get("guard"))
        self.check("C4 deploy_plan 完整（含 guard）", ok_plan)

        # 卸载脚本：按清单删且校验哈希
        ps1 = os.path.join(self.root, "uninstall.ps1")
        ok_un = os.path.isfile(ps1)
        if ok_un:
            t = io.open(ps1, "r", encoding="utf-8-sig", errors="replace").read()
            ok_un = ("a11y_manifest.json" in t or "$Manifest" in t) and "Get-FileHash" in t and "-Force" in t
        self.check("C5 卸载脚本核对哈希后才删", ok_un)

        # 补丁不含大文件（版权/体积红线）
        big = []
        for base, dirs, files in os.walk(self.root):
            for name in files:
                p = os.path.join(base, name)
                if os.path.getsize(p) > 1 << 20:
                    big.append(os.path.relpath(p, self.root))
        self.check("C6 无 >1MB 文件（不含游戏资源）", not big, str(big[:3]))

    def report(self):
        width = max(len(n) for n, _, _ in self.results)
        failed = 0
        lines = []
        for name, ok, detail in self.results:
            tag = "PASS" if ok else "FAIL"
            if not ok:
                failed += 1
            line = "[%s] %-*s %s" % (tag, width, name, detail if detail else "")
            lines.append(line.rstrip())
        lines.append("")
        lines.append("结果: %d/%d 通过%s" % (len(self.results) - failed, len(self.results),
                                            "" if not failed else " —— 有 %d 条失败" % failed))
        return "\n".join(lines), failed


def main(argv=None):
    ap = argparse.ArgumentParser(description="Ren'Py 覆盖层静态校验")
    ap.add_argument("out_dir")
    ap.add_argument("--log", default="")
    opts = ap.parse_args(argv)
    root = os.path.abspath(opts.out_dir)
    chk = Checker(root)
    chk.run()
    text, failed = chk.report()
    print(text)
    if opts.log:
        d = os.path.dirname(os.path.abspath(opts.log))
        if d and not os.path.isdir(d):
            os.makedirs(d)
        io.open(opts.log, "w", encoding="utf-8", newline="\n").write(text + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
