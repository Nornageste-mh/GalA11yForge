# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# GalA11y Forge · 02_dialogue —— 对话朗读（say 钩子）
#
# 挂载点：`config.say_menu_text_filter`
#   - 6.99.12：renpy/ast.py:603-604（say 前）、:1443-1444（menu 项）
#   - 8.6    ：renpy/ast.py:971-972（say 前）、:1787-1788（menu 项）
#   ✔ 每个 say 都经过；✘ 拿不到说话人（ast.py 在 filter 之后才写 _last_say_who）
#
# 因此本文件只做两件事：
#   1) 把文本**原样返回**（红线：不许改游戏显示）；
#   2) 记下"待播报"，由 04_focus.rpy 的周期回调在下一拍补齐说话人再朗读。
#
# 说话人来源：store._last_say_who 是**源码表达式字符串**（如 "yuri_"），
#   要再 getattr(store, ...) 取 Character 对象，读它的 .name。
# ═══════════════════════════════════════════════════════════════════════════

init -1700 python:

    # 有配音时是否仍然朗读文本。True=始终朗读（读屏用户优先）；
    # False=有配音就不念，避免人声与 TTS 抢耳朵（逐作可改）。
    _A11Y_SPEAK_WHEN_VOICED = True
    _A11Y_VOICE_CHANNEL = "voice"

    def _a11y_voice_playing():
        try:
            return bool(renpy.audio.music.get_playing(_A11Y_VOICE_CHANNEL))
        except Exception:
            return False

    def _a11y_speaker_name():
        """尽力从 _last_say_who 解析显示名；解析不了就返回空（当成旁白）。"""
        try:
            w = getattr(renpy.store, "_last_say_who", None)
        except Exception:
            return u""
        if w is None:
            return u""
        obj = None
        if isinstance(w, _A11Y_TEXT):
            expr = w.strip()
            if not expr or expr in (u"None", u"none"):
                return u""
            if len(expr) >= 2 and expr[0] in (u"'", u'"') and expr[-1] == expr[0]:
                return _a11y_clean(expr[1:-1])
            try:
                obj = getattr(renpy.store, expr, None)
            except Exception:
                obj = None
            if obj is None:
                return u""
        else:
            obj = w
        try:
            nm = getattr(obj, "name", None)
        except Exception:
            return u""
        if nm is None:
            return u""
        if callable(nm):
            try:
                nm = nm()
            except Exception:
                return u""
        nm = _a11y_clean(nm)
        # Character(_(u"Yuri")) 这类翻译包壳
        if nm.startswith(u"_(") and nm.endswith(u")"):
            nm = nm[2:-1].strip()
        if nm.startswith(u"_(") and nm.endswith(u")"):
            nm = nm[2:-1].strip()
        return nm

    def _a11y_prepare_speech_text(raw):
        """把 say 文本整理成可朗读的一句话：先替换变量，再清标签。"""
        s = _a11y_u(raw)
        if not s:
            return u""
        try:
            s = renpy.substitute(s)
        except Exception:
            pass
        return _a11y_clean(s)

    def _a11y_note_text(raw):
        """filter 每被调用一次就记一条"待播报"（不在这里发声）。"""
        st = A11yHost
        try:
            text = _a11y_prepare_speech_text(raw)
            if not text:
                return
            st.pending_text = text
            st.pending_seq += 1
            st.pending_time = time.time()
        except Exception as exc:
            _a11y_error("dialogue.note", exc)

    def _a11y_say_filter(s):
        """必须原样返回；游戏自己的 filter 若存在，先链式调用并保留其结果。"""
        out = s
        try:
            orig = _A11Y_ORIG_SAY_FILTER
        except Exception:
            orig = None
        if orig is not None:
            try:
                got = orig(s)
                if got is not None:
                    out = got
            except Exception as exc:
                _a11y_error("dialogue.orig_filter", exc)
        try:
            _a11y_note_text(out)
        except Exception:
            pass
        return out

    try:
        _A11Y_ORIG_SAY_FILTER = renpy.config.say_menu_text_filter
    except Exception:
        _A11Y_ORIG_SAY_FILTER = None
    renpy.config.say_menu_text_filter = _a11y_say_filter
    _a11y_log("挂载", u"config.say_menu_text_filter 已挂载（原 filter=%s）" % (
        _A11Y_ORIG_SAY_FILTER is not None))

    def _a11y_choice_screen():
        # 6.99 无 renpy.get_screen 导出 —— 统一走 00_contract 的兼容层
        return _a11y_get_screen("choice")

    def _a11y_dialogue_tick():
        """周期回调：把最近一次 say 说出来（含说话人前缀）。

        与菜单的关系：choice 屏在场时，菜单标签也会经过 say filter；此时不念，
        把序号标记为已处理，交给 03_menu.rpy 整体播报，避免"半句台词 + 选项"抢读。
        """
        st = A11yHost
        if st.pending_seq <= st.spoken_seq:
            return
        if not _a11y_speech_ok():
            st.spoken_seq = st.pending_seq
            return
        if _a11y_choice_screen() is not None:
            st.spoken_seq = st.pending_seq
            return
        if time.time() - st.pending_time < 0.04:
            return
        st.spoken_seq = st.pending_seq
        text = st.pending_text
        if not text:
            return
        if (not _A11Y_SPEAK_WHEN_VOICED) and _a11y_voice_playing():
            _a11y_log("跳过", u"有配音且已配置跳过: %s" % text)
            return
        who = _a11y_speaker_name()
        line = (u"%s: %s" % (who, text)) if who else text
        st.last_line = line
        speech = st.speech
        if speech is not None:
            speech.say(line, kind=u"对话", interrupt=True, dedupe=0)
