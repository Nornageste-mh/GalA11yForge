# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） · 05_keys —— 按键登记
#
# 两条硬事实（实测，踩过坑）：
#   1) `config.underlay` 在 renpy/common/00keymap.rpy 里被 **整体赋值**
#      `config.underlay = [ _default_keymap ]`（init -1100），早于它的登记会被冲掉
#      → 本文件必须用 **init 1200**，且 append 而不是替换。
#   2) `renpy.Keymap` 的值是**无参调用**（behavior.py Keymap.event → run(action)），
#      处理函数不能声明 (self, ev)，也不能读 ev。
#   3) 键名先过引擎 compile_event(spec, True) 试解析；解析不了的静默永不匹配，
#      必须记日志丢弃而不是假装登记成功。
#
# 键位（避开引擎 F1/F2/F3/F4/F7/F8 与游戏常用键）：
#   F5  重读上一句
#   F8  朗读开/关（不影响游戏本身）
#   F9  整屏朗读
#   F10 切换朗读后端
#   F11 诊断快照（写日志，不念长文本）
# ═══════════════════════════════════════════════════════════════════════════

init 1200 python:

    def _a11y_key_reread():
        try:
            if A11yHost.speech is not None:
                A11yHost.speech.repeat()
        except Exception as exc:
            _a11y_error("key.reread", exc)

    def _a11y_key_toggle():
        st = A11yHost
        try:
            if st.enabled:
                st.speech.say(A11Y_T["off"], kind=u"开关", dedupe=0)
                st.enabled = False
            else:
                st.enabled = True
                st.speech.say(A11Y_T["on"], kind=u"开关", dedupe=0)
            _a11y_log("开关", u"朗读 enabled=%s" % st.enabled)
        except Exception as exc:
            _a11y_error("key.toggle", exc)

    def _a11y_key_read_screen():
        try:
            _a11y_read_screen()
        except Exception as exc:
            _a11y_error("key.read_screen", exc)

    def _a11y_key_cycle_backend():
        try:
            name = A11yHost.speech.cycle()
            A11yHost.speech.say(u"%s：%s" % (A11Y_T["backend"], name), kind=u"开关", dedupe=0)
        except Exception as exc:
            _a11y_error("key.backend", exc)

    def _a11y_key_snapshot():
        try:
            st = A11yHost
            msgs = [u"snapshot patch=%s build=%s game=%s" % (
                _a11y_u(_A11Y_PATCH_VERSION), _a11y_u(_A11Y_BUILD), _a11y_u(_A11Y_GAME_NAME)),
                u"enabled=%s backend=%s flag=%s errors=%s" % (
                    st.enabled, st.backend_name, st.disabled_by_flag, st.errors)]
            try:
                msgs.append(u"focus=" + repr(st.focus_text))
            except Exception:
                pass
            try:
                msgs.append(u"last=" + repr(st.last_text))
            except Exception:
                pass
            try:
                msgs.append(u"screen=%s choice=%s" % (
                    _a11y_get_screen("say") is not None,
                    _a11y_choice_screen() is not None))
            except Exception:
                pass
            _a11y_log("快照", u" | ".join(msgs))
            if st.speech is not None:
                st.speech.say(_a11y_u("已写入诊断快照"), kind=u"开关", dedupe=0)
        except Exception as exc:
            _a11y_error("key.snapshot", exc)

    try:
        from renpy.display.behavior import compile_event as _a11y_compile_event
    except Exception:
        _a11y_compile_event = None

    # 防御：游戏自己的 init 0 脚本可能重设这两个钩子，init 1200 再确认一次
    try:
        if renpy.config.say_menu_text_filter is not _a11y_say_filter:
            _a11y_log("挂载", u"say filter 被游戏覆盖过，重新挂回")
            renpy.config.say_menu_text_filter = _a11y_say_filter
    except Exception as exc:
        _a11y_error("keys.refilter", exc)
    try:
        if renpy.config.tts_function is not _a11y_tts_function:
            _a11y_log("挂载", u"tts_function 被游戏覆盖过，重新挂回")
            renpy.config.tts_function = _a11y_tts_function
    except Exception as exc:
        _a11y_error("keys.retts", exc)

    _A11Y_KEY_SPECS = [
        ("A11yReread", "K_F5", u"重读上一句", _a11y_key_reread),
        ("A11yToggle", "K_F8", u"朗读开关", _a11y_key_toggle),
        ("A11yReadScreen", "K_F9", u"整屏朗读", _a11y_key_read_screen),
        ("A11yBackend", "K_F10", u"切换后端", _a11y_key_cycle_backend),
        ("A11ySnapshot", "K_F11", u"诊断快照", _a11y_key_snapshot),
    ]

    _a11y_keymap = {}
    for _name, _spec, _desc, _fn in _A11Y_KEY_SPECS:
        try:
            if _a11y_compile_event is not None:
                _a11y_compile_event(_spec, True)
            renpy.config.keymap[_name] = [_spec]
            _a11y_keymap[_name] = _fn
            _a11y_log("键位", u"登记 %s = %s（%s）" % (_a11y_u(_name), _a11y_u(_spec), _desc))
        except Exception as exc:
            _a11y_log("键位", u"丢弃 %s = %s：引擎不认这个键名（%s）" % (
                _a11y_u(_name), _a11y_u(_spec), _a11y_u(exc)))

    try:
        if not isinstance(renpy.config.underlay, list):
            renpy.config.underlay = []
        renpy.config.underlay.append(renpy.Keymap(**_a11y_keymap))
        _a11y_log("键位", u"config.underlay 已追加 Keymap（init 1200，%d 个动作）" % len(_a11y_keymap))
    except Exception as exc:
        _a11y_error("keys.underlay", exc)
