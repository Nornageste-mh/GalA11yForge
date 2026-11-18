# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# GalA11y Forge · Ren'Py 无障碍覆盖层 —— 00_contract（契约 / 日志 / 状态）
#
# 生成文件，请勿手改；改 templates/ 后重新生成。
#   目标游戏 : @@GAME_NAME@@
#   Ren'Py   : @@RENPY_VERSION@@（Python @@PY_MAJOR@@）
#   生成器   : @@BUILD@@
#
# 红线：本补丁只"读"游戏状态，不改变任何显示文本与流程。
#       say filter 必须原样返回（见 02_dialogue.rpy）。
# ═══════════════════════════════════════════════════════════════════════════

init -1850 python:
    # 不能直接 `import os` 后就依赖运行期的 os 名字：Ren'Py 的
    # renpy/common/00keymap.rpy 在 init -1600 里会 `import os ... del os`，
    # 把 store 里的 os 删掉，任何运行期代码再引用 os 就是 NameError
    # （0.2.44 用自造最小样本实测：每句都报 sapi.speak NameError，语音根本没出去）。
    # 所以统一用别名 _a11y_os，引擎不会删它。
    import os as _a11y_os
    import sys
    import time
    import codecs

    _A11Y_PATCH_VERSION = "@@PATCH_VERSION@@"
    _A11Y_BUILD = "@@BUILD@@"
    _A11Y_GAME_NAME = "@@GAME_NAME@@"
    _A11Y_ENGINE_VERSION = "@@RENPY_VERSION@@"
    _A11Y_PY2 = sys.version_info[0] == 2

    try:
        _A11Y_TEXT = unicode          # Python 2
    except NameError:
        _A11Y_TEXT = str              # Python 3

    def _a11y_u(s):
        """bytes -> unicode；unicode/str 原样返回。py2 下中文常量必须过这里。"""
        if isinstance(s, _A11Y_TEXT):
            return s
        try:
            return s.decode("utf-8")
        except Exception:
            try:
                return _A11Y_TEXT(s)
            except Exception:
                return u""

    def _a11y_s(v):
        """unicode -> str（子进程参数用）；py2 的 str 原样返回，py3 原样返回。"""
        if not _A11Y_PY2:
            return v
        if isinstance(v, str):
            return v
        try:
            return v.encode("utf-8")
        except Exception:
            return v

    def _a11y_basedir():
        """游戏根目录（含 game/ 的那一层），日志与开关文件都放这里。"""
        try:
            return renpy.config.basedir
        except Exception:
            return ""

    def _a11y_get_screen(name):
        """取当前显示的 screen。

        6.99.12 没有把 renpy.get_screen 导出到 renpy.*（实测：只有
        renpy/display/screen.py:912 get_screen），8.x 两处都有；先走 display.screen，
        再退 exports，两条都不在就返回 None（调用方一律按"拿不到"处理）。
        """
        try:
            return renpy.display.screen.get_screen(name)
        except Exception:
            pass
        try:
            return renpy.get_screen(name)
        except Exception:
            return None

    # ── 对外文案集中在这里，便于逐作改写 ───────────────────────────────────
    A11Y_T = {
        "on": _a11y_u("朗读已开启"),
        "off": _a11y_u("朗读已关闭"),
        "menu": _a11y_u("选项"),
        "focus": _a11y_u("焦点"),
        "screen": _a11y_u("屏幕"),
        "backend": _a11y_u("朗读后端"),
        "nothing": _a11y_u("没有可朗读的内容"),
        "flag_off": _a11y_u("检测到 a11y_off.flag，本次不朗读"),
        "started": _a11y_u("覆盖层已加载"),
    }

    class _A11yLegacyBridge(object):
        """兼容旧版 a11y_game（A11yFramework 契约）的桥对象。

        有些游戏自带旧版无障碍补丁（game/a11y_game），它按旧契约调用
        A11yHost.rpy.start(...) / A11yHost.keys.install() / A11yHost.log(...)
        等。我们不接管它的朗读链（否则两个播报源抢麦），只把调用吞掉并记一行
        日志，保证游戏不因 AttributeError 崩在启动画面。
        """

        def __init__(self, name):
            self._a11y_name = name

        def __call__(self, *args, **kwargs):
            try:
                _a11y_log(u"兼容", u"旧 a11y_game 调用了 A11yHost.%s(...)，已忽略（避免双读）"
                          % _a11y_u(self._a11y_name))
            except Exception:
                pass
            return None

        def __getattr__(self, item):
            return _A11yLegacyBridge(self._a11y_name + u"." + _a11y_u(item))

    class _A11yState(object):
        """全部可变状态挂这里，避免污染游戏自己的 store 命名空间。"""

        def __init__(self):
            # 朗读
            self.enabled = True
            self.backend = None            # 当前后端对象
            self.backend_name = u""
            self.speech = None             # 由 01_speech.rpy 赋值
            self.last_text = u""
            self.last_time = 0.0
            # 对话
            self.pending_text = u""
            self.pending_who = u""
            self.pending_seq = 0
            self.spoken_seq = 0
            self.pending_time = 0.0
            self.last_line = u""           # 最近一条完整播报（重读用）
            self.history = []
            # 焦点
            self.focus_key = None
            self.focus_text = u""
            self.focus_time = 0.0
            self.focus_misses = 0
            # 菜单 / 主菜单
            self.choice_sig = u""
            self.choice_announced = 0.0
            self.main_menu_announced = False
            # 特殊屏幕（输入框 / 确认框 / 退出确认）：按屏幕名记录是否已播报
            self.special_seen = {}
            self.input_value = u""
            # 日志 / 开关
            self.log_path = None
            self.log_start = 0.0
            self.flag_path = _a11y_os.path.join(_a11y_basedir(), "a11y_off.flag")
            self.disabled_by_flag = False
            self.last_flag_check = 0.0
            self.startup_done = False
            self.errors = 0
            # ── 兼容旧版 a11y_game（A11yFramework 契约）────────────────────
            # 某些游戏自带旧版无障碍补丁，它按旧契约读这些字段、调 validate()。
            # 只提供默认值与空实现，保证启动不崩；产品默认会在安装前拦截旧补丁，
            # 真的两套共存时这里只记 [A11y兼容] 日志，不做双读。
            self.CfgAutoEnable = True
            self.CfgDiagLog = True
            self.CfgRereadKey = u"K_F5"
            self.CfgSpeechBackend = u"auto"
            self.GameName = _A11Y_GAME_NAME
            self.GameVersion = _A11Y_ENGINE_VERSION
            self.PatchVersion = _A11Y_PATCH_VERSION
            self.rpy = _A11yLegacyBridge(u"rpy")
            self.keys = _A11yLegacyBridge(u"keys")
            self.log = _A11yLegacyBridge(u"log")
            self.repeat = _A11yLegacyBridge(u"repeat")
            self.said = _A11yLegacyBridge(u"said")
            self.choice = _A11yLegacyBridge(u"choice")

        def validate(self):
            """旧契约自检入口：空实现，只留证据。"""
            _a11y_log(u"兼容", u"旧 a11y_game 调用了 A11yHost.validate()，已按空实现放行")
            return 0

        def log_exc(self, where=u"", exc=None):
            """旧契约异常日志入口：不重复吞异常，只记录调用点。"""
            try:
                _a11y_log(u"兼容", u"旧 a11y_game log_exc：%s" % _a11y_u(where))
            except Exception:
                pass
            return None

    A11yHost = _A11yState()

    def _a11y_log(kind, msg):
        """追加一行到 <游戏根>/a11y_speech.log；任何异常都不许冒泡。"""
        st = A11yHost
        try:
            now = time.time()
            if st.log_path is None:
                st.log_path = _a11y_os.path.join(_a11y_basedir(), "a11y_speech.log")
                st.log_start = now
            if not st.log_start:
                st.log_start = now
            line = u"[A11y%s] +%.3fs %s\n" % (kind, now - st.log_start, _a11y_u(msg))
            with codecs.open(st.log_path, "a", "utf-8") as f:
                f.write(line)
        except Exception:
            pass

    def _a11y_error(where, exc):
        """记录异常：失败要留证据，不许静默。"""
        try:
            A11yHost.errors += 1
            _a11y_log("错误", u"%s: %s: %s" % (
                _a11y_u(where), _a11y_u(exc.__class__.__name__), _a11y_u(exc)))
        except Exception:
            pass

    def _a11y_flag_disabled():
        """外部开关：<根目录>/a11y_off.flag 存在即静音（每秒最多查一次）。"""
        st = A11yHost
        now = time.time()
        if now - st.last_flag_check < 1.0:
            return st.disabled_by_flag
        st.last_flag_check = now
        try:
            st.disabled_by_flag = _a11y_os.path.exists(st.flag_path)
        except Exception:
            st.disabled_by_flag = False
        return st.disabled_by_flag

    def _a11y_speech_ok():
        return bool(A11yHost.enabled) and not _a11y_flag_disabled()

    def _a11y_clean(text):
        """把 Ren'Py 文本标签/换行/多余空白清成适合朗读的一句话。"""
        s = _a11y_u(text)
        if not s:
            return u""
        try:
            out = []
            depth = 0
            for ch in s:
                if ch == u"{":
                    depth += 1
                    continue
                if ch == u"}":
                    if depth > 0:
                        depth -= 1
                    continue
                if depth == 0:
                    out.append(ch)
            s = u"".join(out)
        except Exception:
            pass
        try:
            s = s.replace(u"\\n", u" ").replace(u"\n", u" ").replace(u"\r", u" ")
            s = s.replace(u"\u3000", u" ")
            while u"  " in s:
                s = s.replace(u"  ", u" ")
            s = s.strip()
        except Exception:
            pass
        if len(s) > 1200:
            s = s[:1200] + u"…"
        return s

    def _a11y_displayable_text(d):
        """取一个 displayable 的整段朗读文本。

        版本差异（实测）：Ren'Py 6.99 是 `_tts_all(self)`，8.x 是
        `_tts_all(self, raw)`；用 TypeError 回退，两条路都走不通再退回
        Text.text / style.alt。

        0.2.26：必须用 raw=False。raw=True 取的是 text_parameter（翻译前的
        source text），中文界面会念出英文原文（真机：永恒与星辰与日常的确认框）；
        raw=False 取 self.text，是翻译后的文本，与屏幕显示一致。
        """
        if d is None:
            return u""
        try:
            try:
                s = d._tts_all(raw=False)
            except TypeError:
                try:
                    s = d._tts_all()
                except TypeError:
                    s = d._tts_all(raw=True)
            if s:
                return _a11y_clean(s)
        except Exception:
            pass
        try:
            t = getattr(d, "text", None)
            if isinstance(t, _A11Y_TEXT) and t:
                return _a11y_clean(t)
        except Exception:
            pass
        try:
            alt = d.style.alt
            if alt:
                return _a11y_clean(alt)
        except Exception:
            pass
        return u""

    def _a11y_duck_role(d):
        """按类名/样式名猜一个读屏可懂的控件角色（按钮/选项/滑杆）。"""
        name = u""
        try:
            name = _a11y_u(d.__class__.__name__).lower()
        except Exception:
            pass
        sname = u""
        try:
            sname = _a11y_u(getattr(d.style, "name", u"")).lower()
        except Exception:
            pass
        probe = name + u" " + sname
        for token, role in ((u"textbutton", u"按钮"), (u"button", u"按钮"),
                            (u"bar", u"滑杆"), (u"slider", u"滑杆"),
                            (u"imagebutton", u"按钮"), (u"hotspot", u"按钮")):
            if token in probe:
                return role
        return u""

    def _a11y_probe():
        """启动自检：把关键能力写进日志，便于事后核对而不是猜。"""
        info = []
        try:
            info.append(u"renpy=%s" % _a11y_u(renpy.version))
        except Exception:
            info.append(u"renpy=?")
        try:
            info.append(u"python=%s" % _a11y_u(sys.version.split()[0]))
        except Exception:
            info.append(u"python=?")
        try:
            info.append(u"basedir=%s" % _a11y_u(_a11y_basedir()))
        except Exception:
            info.append(u"basedir=?")
        try:
            info.append(u"say_filter=%s" % (renpy.config.say_menu_text_filter is not None))
        except Exception:
            pass
        try:
            info.append(u"tts_voice_channels=%s" % (_a11y_u(repr(renpy.config.tts_voice_channels))))
        except Exception:
            pass
        try:
            import renpy.display.tts as _t
            info.append(u"tts_module=%s" % bool(_t))
        except Exception:
            info.append(u"tts_module=False")
        _a11y_log("启动", u"补丁 %s / %s / %s" % (
            _a11y_u(_A11Y_PATCH_VERSION), _a11y_u(_A11Y_BUILD), _a11y_u(_A11Y_GAME_NAME)))
        _a11y_log("启动", u" ".join(info))
