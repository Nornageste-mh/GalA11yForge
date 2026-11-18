# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# GalA11y Forge · 01_speech —— 语音后端链（NVDA → SAPI → 日志）
#
# 为什么不用引擎自带 self-voicing 出声：
#   1) 引擎的 tts_voice_channels 配音守卫（tts.py `displayable()`）会在有配音时
#      直接吞掉文本；本作多数台词有配音。
#   2) 6.99 与 8.x 的 `alt` / 自语音细节不同，靠引擎出声等于把兼容性交给版本。
#   3) 双朗读：只要引擎出声、补丁也出声，同一句就会念两遍。
# 做法：**引擎自语音保持关闭**（init 时强制 False），并接管 `config.tts_function`
#      把引擎万一发出的语音只记日志、不发声 → 结构上不可能双朗读。
# ═══════════════════════════════════════════════════════════════════════════

init -1800 python:
    import codecs
    import sys as _a11y_sys
    import ctypes as _a11y_ctypes
    import subprocess as _a11y_subprocess
    import tempfile as _a11y_tempfile

    _A11Y_TTS_PREF = "auto"          # auto / nvda / sapi / log
    _A11Y_SAPI_RATE = 0          # -10 .. 10

    class A11yLogBackend(object):
        """只写日志、不出声；给无声环境与自动化测试用。"""

        name = "日志"

        def available(self):
            return True

        def speak(self, text):
            _a11y_log("朗读", u"[log] %s" % text)

        def stop(self):
            pass

    class A11yNvdaBackend(object):
        """NVDA Controller Client；32/64 位必须与游戏 python 位数一致。"""

        name = "NVDA"

        def __init__(self):
            self.dll = None
            self.reason = u""
            self._load()

        def _candidates(self):
            bits = 64 if _a11y_sys.maxsize > 2 ** 32 else 32
            leaf = "nvdaControllerClient%d.dll" % bits
            dirs = []
            for getter in ("gamedir", "basedir"):
                try:
                    dirs.append(getattr(renpy.config, getter))
                except Exception:
                    pass
            dirs.append(_a11y_basedir())
            out = []
            for d in dirs:
                if d:
                    out.append(os.path.join(d, leaf))
            out.append(leaf)
            return out

        def _load(self):
            if not _a11y_sys.platform.startswith("win"):
                self.reason = u"非 Windows"
                return
            for path in self._candidates():
                try:
                    dll = _a11y_ctypes.WinDLL(path)
                    dll.nvdaController_testIfRunning.restype = _a11y_ctypes.c_long
                    dll.nvdaController_speakText.argtypes = [_a11y_ctypes.c_wchar_p]
                    dll.nvdaController_speakText.restype = _a11y_ctypes.c_long
                    dll.nvdaController_cancelSpeech.restype = _a11y_ctypes.c_long
                    rc = dll.nvdaController_testIfRunning()
                    self.reason = u"testIfRunning=%s dll=%s" % (rc, _a11y_u(path))
                    if rc == 0:
                        self.dll = dll
                        return
                except Exception as exc:
                    self.reason = u"%s (%s)" % (_a11y_u(path), _a11y_u(exc))
            self.dll = None

        def available(self):
            return self.dll is not None

        def speak(self, text):
            if self.dll is None:
                return
            rc = self.dll.nvdaController_speakText(text)
            if rc != 0:
                _a11y_log("错误", u"NVDA speakText rc=%s" % rc)

        def stop(self):
            if self.dll is not None:
                try:
                    self.dll.nvdaController_cancelSpeech()
                except Exception as exc:
                    _a11y_error("nvda.cancel", exc)

    class A11ySayVbsBackend(object):
        """SAPI：写 UTF-16 临时文件 → wscript a11y_say.vbs 朗读。

        文本走文件而不是命令行，绕开 py2 下 wscript 参数的中文编码坑
        （引擎自己的 default_tts_function 用 fsencode，那是另一条已验证路径）。
        """

        name = "SAPI"

        def __init__(self):
            self.vbs = u""
            self.proc = None
            self.reason = u""
            for getter in ("gamedir", "basedir"):
                try:
                    cand = os.path.join(getattr(renpy.config, getter), "a11y_say.vbs")
                    if os.path.isfile(cand):
                        self.vbs = cand
                        break
                except Exception:
                    pass
            if not self.vbs:
                self.reason = u"未找到 a11y_say.vbs"

        def available(self):
            return bool(self.vbs) and _a11y_sys.platform.startswith("win")

        def _kill_old(self):
            if self.proc is None:
                return
            try:
                if self.proc.poll() is None:
                    self.proc.terminate()
            except Exception:
                pass
            self.proc = None

        def speak(self, text):
            if not self.available():
                return
            self._kill_old()
            path = u""
            try:
                fd, path = _a11y_tempfile.mkstemp(prefix="a11y_say_", suffix=".txt")
                os.close(fd)
                with codecs.open(path, "w", "utf-16") as f:
                    f.write(text)
                args = [u"wscript.exe", self.vbs, path, u"%d" % _A11Y_SAPI_RATE]
                if _A11Y_PY2:
                    args = [_a11y_s(a) for a in args]
                self.proc = _a11y_subprocess.Popen(args)
            except Exception as exc:
                _a11y_error("sapi.speak", exc)
                try:
                    if path and os.path.exists(path):
                        os.remove(path)
                except Exception:
                    pass

        def stop(self):
            self._kill_old()

    class A11ySpeech(object):
        """后端选择 + 去重 + 历史。所有播报都从这里出去。"""

        def __init__(self):
            self.backends = [A11yNvdaBackend(), A11ySayVbsBackend(), A11yLogBackend()]
            self.active = None
            self.last_text = u""
            self.last_time = 0.0
            self.history = []

        def _wanted(self):
            want = (_A11Y_TTS_PREF or u"auto").strip().lower()
            for b in self.backends:
                if want == u"nvda" and b.name == "NVDA":
                    return [b]
                if want in (u"sapi", u"vbs") and b.name == "SAPI":
                    return [b]
                if want == u"log" and b.name == "日志":
                    return [b]
            return self.backends

        def start(self):
            chain = []
            for b in self._wanted():
                try:
                    ok = b.available()
                except Exception:
                    ok = False
                chain.append(u"%s=%s" % (_a11y_u(b.name), ok))
                if ok and self.active is None:
                    self.active = b
            if self.active is None:
                self.active = self.backends[-1]
            A11yHost.backend = self.active
            A11yHost.backend_name = _a11y_u(self.active.name)
            _a11y_log("后端", u"链条 %s → 采用 %s" % (u" ".join(chain), A11yHost.backend_name))

        def cycle(self):
            avail = []
            for b in self._wanted():
                try:
                    if b.available():
                        avail.append(b)
                except Exception:
                    pass
            if not avail:
                return A11yHost.backend_name
            try:
                idx = avail.index(self.active)
            except Exception:
                idx = -1
            self.active = avail[(idx + 1) % len(avail)]
            A11yHost.backend = self.active
            A11yHost.backend_name = _a11y_u(self.active.name)
            _a11y_log("后端", u"切换到 %s" % A11yHost.backend_name)
            return A11yHost.backend_name

        def say(self, text, kind=u"读", interrupt=True, dedupe=1.2):
            st = A11yHost
            if not _a11y_speech_ok():
                return False
            text = _a11y_clean(text)
            if not text:
                return False
            now = time.time()
            if dedupe and text == self.last_text and (now - self.last_time) < dedupe:
                return False
            if interrupt and self.active is not None:
                try:
                    self.active.stop()
                except Exception:
                    pass
            spoken = False
            try:
                if self.active is not None:
                    self.active.speak(text)
                    spoken = True
            except Exception as exc:
                _a11y_error("speech.say", exc)
            self.last_text = text
            self.last_time = now
            self.history.append(text)
            if len(self.history) > 30:
                del self.history[:-30]
            st.last_text = text
            st.last_time = now
            _a11y_log(kind, u"backend=%s %s" % (A11yHost.backend_name, text))
            return spoken

        def repeat(self):
            if not A11yHost.history:
                self.say(A11Y_T["nothing"], kind=u"重读", dedupe=0)
                return
            self.say(A11yHost.history[-1], kind=u"重读", dedupe=0)

        def stop(self):
            if self.active is not None:
                try:
                    self.active.stop()
                except Exception:
                    pass

    # ── 接管引擎自语音：只记日志、不发声，结构上排除双朗读 ────────────────
    def _a11y_tts_function(s):
        try:
            _a11y_log("引擎自语音", _a11y_u(s))
        except Exception:
            pass
        return None

    try:
        _a11y_orig_tts = renpy.config.tts_function
        renpy.config.tts_function = _a11y_tts_function
        _a11y_log("接管", u"config.tts_function 已接管（引擎自语音只记日志）")
    except Exception as exc:
        _a11y_error("tts.takeover", exc)

    try:
        renpy.game.preferences.self_voicing = False
        _a11y_log("自语音", u"已强制 preferences.self_voicing=False")
    except Exception as exc:
        _a11y_error("tts.self_voicing", exc)

    A11yHost.speech = A11ySpeech()
    A11yHost.speech.start()
