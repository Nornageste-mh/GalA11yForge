#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GalA11y Forge · 假引擎冒烟台（无 Ren'Py SDK 时的"能跑起来"证据）

本机没有 Ren'Py SDK，不能 `renpy lint` / 真启动。于是用**最小 renpy 桩**模拟
引擎运行期：按 init 优先级执行生成的 .rpy 代码 → 触发 say / 焦点 / 选项 / 按键，
检查补丁是否按设计说话、是否记日志、是否抛异常。

这不是"跑通游戏"，只证明：补丁的 init 代码在 Python 层能执行、钩子能生效、
播报链路能出字。真运行仍需 D:\\Steam 之外的副本 + 用户授权。

用法：
    python smoke_harness.py build\\ddlc --log evidence\\smoke_ddlc.log
"""

import argparse
import io
import json
import os
import re
import shutil
import sys
import tempfile
import time
import traceback
import types

BLOCK_RE = re.compile(r"^(?P<indent>\s*)init(?:\s+(?P<prio>-?\d+))?\s+python\s*:\s*$", re.M)


# ── 引擎桩 ─────────────────────────────────────────────────────────────────

class FakeStyle(object):
    def __init__(self, name="default", alt=None):
        self.name = name
        self.alt = alt


class FakeWidget(object):
    def __init__(self, text="", cls="Text", style=None):
        self._text = text
        self.__class__ = type(cls, (FakeWidget,), {}) if cls != "FakeWidget" else FakeWidget
        self.style = style or FakeStyle()

    def _tts_all(self, raw=False):
        return self._text


class FakeFocus(object):
    def __init__(self, widget, screen=""):
        self.widget = widget
        self.screen = screen


class FakeConfig(object):
    def __init__(self, basedir, gamedir):
        self.basedir = basedir
        self.gamedir = gamedir
        self.say_menu_text_filter = None
        self.tts_function = lambda s: None
        self.tts_voice_channels = ["voice"]
        self.tts_substitutions = []
        self.periodic_callbacks = []
        self.interact_callbacks = []
        self.overlay_screens = []
        self.keymap = {}
        self.default_keymap = {}
        self.underlay = []
        self.version = "fake"


class FakePrefs(object):
    def __init__(self):
        self.self_voicing = True       # 故意打开，检验补丁是否会关掉


class FakeKeymap(object):
    def __init__(self, **kw):
        self.keymap = kw


def build_fake_renpy(root):
    basedir = root
    gamedir = os.path.join(root, "game")
    renpy = types.ModuleType("renpy")
    renpy.config = FakeConfig(basedir, gamedir)
    renpy.version = "fake-8.6.0"
    renpy.Keymap = FakeKeymap
    renpy.substitute = lambda s, scope=None, translate=True: s

    game = types.SimpleNamespace(preferences=FakePrefs())
    renpy.game = game
    renpy.store = types.SimpleNamespace()

    focus = types.ModuleType("renpy.display.focus")
    focus.focus_list = []
    focus.get_focused = lambda: (focus.focus_list[-1].widget if focus.focus_list else None)

    screen_mod = types.ModuleType("renpy.display.screen")
    screens = {}
    screen_mod.get_screen = lambda name, layer=None: screens.get(name)
    screen_mod.screens = screens

    behavior = types.ModuleType("renpy.display.behavior")

    def compile_event(spec, keydown):
        if not isinstance(spec, str) or not spec:
            raise ValueError("bad spec %r" % (spec,))
        if not spec.startswith("K_"):
            raise ValueError("unknown key %r" % (spec,))
        return "(True)"

    behavior.compile_event = compile_event
    behavior.map_event = lambda ev, name: False
    behavior.Keymap = FakeKeymap

    display = types.ModuleType("renpy.display")
    display.focus = focus
    display.screen = screen_mod
    display.behavior = behavior

    audio = types.ModuleType("renpy.audio")
    music = types.ModuleType("renpy.audio.music")
    music.get_playing = lambda ch: None
    audio.music = music

    renpy.display = display
    renpy.audio = audio

    sys.modules["renpy"] = renpy
    sys.modules["renpy.display"] = display
    sys.modules["renpy.display.focus"] = focus
    sys.modules["renpy.display.screen"] = screen_mod
    sys.modules["renpy.display.behavior"] = behavior
    sys.modules["renpy.audio"] = audio
    sys.modules["renpy.audio.music"] = music
    return renpy, screens


# ── init 代码执行 ──────────────────────────────────────────────────────────

def extract_init_blocks(path):
    text = io.open(path, "r", encoding="utf-8", errors="replace").read()
    lines = text.splitlines()
    blocks = []
    for i, line in enumerate(lines):
        m = BLOCK_RE.match(line)
        if not m:
            continue
        prio = int(m.group("prio") or 0)
        base = len(line) - len(line.lstrip())
        body = []
        j = i + 1
        while j < len(lines):
            ln = lines[j]
            if not ln.strip():
                body.append("")
                j += 1
                continue
            if len(ln) - len(ln.lstrip()) <= base:
                break
            body.append(ln)
            j += 1
        dedent = min([len(ln) - len(ln.lstrip()) for ln in body if ln.strip()] or [base + 4])
        code = "\n".join(ln[dedent:] if len(ln) >= dedent else ln.lstrip() for ln in body)
        blocks.append((prio, i + 1, code))
    return blocks


def main(argv=None):
    ap = argparse.ArgumentParser(description="Ren'Py 覆盖层假引擎冒烟台")
    ap.add_argument("build_dir")
    ap.add_argument("--log", default="")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    opts = ap.parse_args(argv)

    build_dir = os.path.abspath(opts.build_dir)
    pdir = os.path.join(build_dir, "game", "a11y_platform")
    if not os.path.isdir(pdir):
        raise SystemExit("找不到 %s" % pdir)

    tmp = tempfile.mkdtemp(prefix="a11y_smoke_")
    results = []

    def check(name, ok, detail=""):
        results.append((name, bool(ok), detail))

    try:
        # 伪造游戏根：game/ 放补丁，根放 a11y_say.vbs（SAPI 后端会探测它）
        os.makedirs(os.path.join(tmp, "game", "a11y_platform"))
        for name in sorted(os.listdir(pdir)):
            shutil.copy2(os.path.join(pdir, name),
                         os.path.join(tmp, "game", "a11y_platform", name))
        for name in ("a11y_say.vbs",):
            src = os.path.join(build_dir, name)
            if os.path.isfile(src):
                shutil.copy2(src, os.path.join(tmp, name))

        renpy, screens = build_fake_renpy(tmp)
        store = {}
        store["renpy"] = renpy
        store["__builtins__"] = __builtins__
        renpy.store = types.SimpleNamespace()
        # 让函数体内的 store 名字解析到同一个 dict
        exec_globals = store

        blocks = []
        for name in ("00_contract.rpy", "01_speech.rpy", "02_dialogue.rpy",
                     "03_menu.rpy", "04_focus.rpy", "05_keys.rpy"):
            p = os.path.join(pdir, name)
            for prio, line, code in extract_init_blocks(p):
                blocks.append((prio, name, line, code))
        blocks.sort(key=lambda b: b[0])
        failed_exec = []
        for prio, name, line, code in blocks:
            try:
                exec(compile(code, "%s:%d" % (name, line), "exec"), exec_globals)
            except Exception:
                failed_exec.append("%s:%d\n%s" % (name, line, traceback.format_exc()))
        check("S1 全部 init 代码可执行", not failed_exec, (failed_exec[0].splitlines()[-1] if failed_exec else "%d 块" % len(blocks)))

        log_path = os.path.join(tmp, "a11y_speech.log")

        def log_text():
            if not os.path.isfile(log_path):
                return ""
            return io.open(log_path, "r", encoding="utf-8", errors="replace").read()

        # 第一次心跳：启动自检（probe）写日志
        renpy.config.periodic_callbacks[0]()
        check("S2 启动日志已写出", "补丁" in log_text() and "say_filter=True" in log_text())
        check("S3 say filter 已挂上", renpy.config.say_menu_text_filter is not None)
        check("S4 self_voicing 被关掉", renpy.game.preferences.self_voicing is False)
        check("S5 tts_function 已接管", renpy.config.tts_function is not renpy.config.__dict__.get("_orig_tts", None) and renpy.config.tts_function is not None)
        check("S6 心跳已登记", len(renpy.config.periodic_callbacks) == 1)
        check("S7 键位已 append 到 underlay", len(renpy.config.underlay) == 1
              and isinstance(renpy.config.underlay[0], FakeKeymap))
        keys = renpy.config.underlay[0].keymap if renpy.config.underlay else {}
        check("S8 五个按键动作齐全", set(["A11yReread", "A11yToggle", "A11yReadScreen",
                                          "A11yBackend", "A11ySnapshot"]) <= set(keys))

        # ── 场景 1：一句带说话人的台词 ───────────────────────────────────
        class Yuri(object):
            name = "Yuri"
        renpy.store.yuri_ = Yuri()
        renpy.store._last_say_who = "yuri_"
        renpy.config.say_menu_text_filter("这是一句测试台词。")
        time.sleep(0.06)
        renpy.config.periodic_callbacks[0]()
        time.sleep(0.06)
        renpy.config.periodic_callbacks[0]()
        log = log_text()
        check("S9 台词被朗读（含说话人）", "Yuri: 这是一句测试台词" in log,
              [l for l in log.splitlines() if "测试台词" in l][-1:] or "")

        # ── 场景 2：焦点移动播报 ─────────────────────────────────────────
        btn = FakeWidget("开始游戏", "TextButton", FakeStyle("textbutton"))
        renpy.display.focus.focus_list = [FakeFocus(btn, "main_menu")]
        time.sleep(0.15)
        renpy.config.periodic_callbacks[0]()
        log = log_text()
        check("S10 焦点按钮被播报", "开始游戏" in log and "[A11y焦点]" in log)

        # ── 场景 3：选项菜单整体播报 ─────────────────────────────────────
        i1 = FakeWidget("1. 跟她说话", "TextButton", FakeStyle("textbutton"))
        i2 = FakeWidget("2. 保持沉默", "TextButton", FakeStyle("textbutton"))

        class FakeScreen(object):
            children = [i1, i2]

        screens["choice"] = FakeScreen()
        renpy.display.focus.focus_list = [FakeFocus(i1, "choice"), FakeFocus(i2, "choice")]
        time.sleep(0.3)
        renpy.config.periodic_callbacks[0]()
        log = log_text()
        check("S11 选项整体播报", "选项 2 个" in log and "跟她说话" in log)

        # ── 场景 4：按键动作不抛异常 ─────────────────────────────────────
        key_errors = []
        for name, fn in keys.items():
            try:
                fn()
            except Exception:
                key_errors.append("%s: %s" % (name, traceback.format_exc().splitlines()[-1]))
        check("S12 按键处理不抛异常", not key_errors, str(key_errors[:2]))

        # F8 关掉后不再播报，再按一次打开（按键循环里已按过一次，故先归位）
        exec_globals["A11yHost"].enabled = True
        state0 = exec_globals["A11yHost"].enabled
        keys["A11yToggle"]()
        state1 = exec_globals["A11yHost"].enabled
        keys["A11yToggle"]()
        state2 = exec_globals["A11yHost"].enabled
        check("S13 F8 开/关可逆", state0 is True and state1 is False and state2 is True,
              "%s→%s→%s" % (state0, state1, state2))

        # ── 场景 5：无异常日志 ───────────────────────────────────────────
        log = log_text()
        err_lines = [l for l in log.splitlines() if "[A11y错误]" in l]
        check("S14 运行期没有错误日志", not err_lines, str(err_lines[:2]))

        # ── 场景 6：静音开关文件 ─────────────────────────────────────────
        flag = os.path.join(tmp, "a11y_off.flag")
        io.open(flag, "w").write("x")
        exec_globals["A11yHost"].last_flag_check = 0
        before = len(log_text())
        renpy.config.say_menu_text_filter("静音测试台词。")
        time.sleep(0.06)
        renpy.config.periodic_callbacks[0]()
        time.sleep(0.06)
        renpy.config.periodic_callbacks[0]()
        check("S15 a11y_off.flag 生效", "静音测试台词" not in log_text()[before:])
        os.remove(flag)

        # ── 场景 7：_tts_all 双签名兼容（6.99 无 raw） ────────────────────
        class OldWidget(object):
            style = FakeStyle()

            def _tts_all(self):
                return "旧版控件"

        exec_globals["A11yHost"].last_flag_check = 0
        txt = exec_globals["_a11y_displayable_text"](OldWidget())
        check("S16 _tts_all() 旧签名兼容", txt == "旧版控件", txt)

    finally:
        if not opts.keep:
            shutil.rmtree(tmp, ignore_errors=True)
        else:
            print("临时目录: %s" % tmp)

    width = max(len(n) for n, _, _ in results)
    failed = 0
    lines = []
    for name, ok, detail in results:
        if not ok:
            failed += 1
        lines.append("[%s] %-*s %s" % ("PASS" if ok else "FAIL", width, name,
                                       detail if detail else ""))
    lines.append("")
    lines.append("结果: %d/%d 通过%s" % (len(results) - failed, len(results),
                                      "" if not failed else " —— 有 %d 条失败" % failed))
    text = "\n".join(lines)
    print(text)
    if opts.log:
        d = os.path.dirname(os.path.abspath(opts.log))
        if d and not os.path.isdir(d):
            os.makedirs(d)
        io.open(opts.log, "w", encoding="utf-8", newline="\n").write(text + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
