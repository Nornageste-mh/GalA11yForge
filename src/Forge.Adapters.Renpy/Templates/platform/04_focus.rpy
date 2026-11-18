# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# GalA11y Forge · 04_focus —— 界面焦点播报 + 整屏朗读 + 周期调度
#
# 关键事实（实测）：
#   · renpy.display.focus.focus_list 每轮渲染重建 → 只能轮询，不能存引用；
#   · renpy.display.focus.get_focused() 6.99/8.x 都在；
#   · config.periodic_callbacks 约 50ms 一次，回调无参数。
# 所以本文件是补丁的"心跳"：对话、菜单、焦点三条 tick 都挂在它下面，各自节流。
# ═══════════════════════════════════════════════════════════════════════════

init -1500 python:

    _A11Y_FOCUS_INTERVAL = 0.12     # 焦点轮询节流（秒）
    _A11Y_MENU_INTERVAL = 0.25
    _A11Y_SPECIAL_INTERVAL = 0.12   # 输入框/确认框轮询节流（秒）
    _A11Y_VOICE_GUARD_INTERVAL = 3.0

    def _a11y_focus_tick():
        st = A11yHost
        # 有台词还没念 → 先让对话播报，别用"焦点"插队
        if st.pending_seq > st.spoken_seq:
            return
        try:
            cur = renpy.display.focus.get_focused()
        except Exception:
            return
        if cur is None:
            st.focus_key = None
            return
        widget = getattr(cur, "widget", cur)
        text = _a11y_displayable_text(widget)
        if not text:
            # 0.2.27：imagebutton 之类没有文字的按钮，也要让用户知道焦点在按钮上
            role = _a11y_duck_role(widget)
            if role == u"按钮":
                text = u"（没有可读文字，按 Tab 换一个，回车执行）"
            else:
                return
        key = (id(widget), text)
        if key == st.focus_key:
            return
        st.focus_key = key
        st.focus_text = text
        st.focus_time = time.time()
        if not _a11y_speech_ok():
            return
        if text == st.last_text:
            return
        role = _a11y_duck_role(widget)
        line = (u"%s %s" % (role, text)) if role else text
        if st.speech is not None:
            st.speech.say(line, kind=u"焦点", interrupt=True, dedupe=1.0)

    def _a11y_read_screen():
        """整屏朗读：把当前所有可聚焦控件的文本按顺序念一遍。"""
        if not _a11y_speech_ok():
            return
        texts = []
        for sname, txt, d in _a11y_focus_entries():
            if txt not in texts:
                texts.append(txt)
        if not texts:
            try:
                dl = renpy.get_screen("say")
                if dl is not None:
                    txt = _a11y_displayable_text(dl)
                    if txt:
                        texts = [txt]
            except Exception:
                pass
        if not texts:
            line = A11Y_T["nothing"]
        else:
            line = A11Y_T["screen"] + u"：" + u"；".join(texts[:20])
        if A11yHost.speech is not None:
            A11yHost.speech.say(line, kind=u"整屏", interrupt=True, dedupe=0)

    def _a11y_periodic():
        """约 50ms 一次的心跳；各子任务自己按时间戳节流。"""
        st = A11yHost
        now = time.time()
        if not st.startup_done:
            st.startup_done = True
            try:
                _a11y_probe()
            except Exception as exc:
                _a11y_error("probe", exc)
        # 引擎自语音守卫：偏好可能在 init 之后才从 persistent 载入
        if now - getattr(st, "vo_check", 0.0) > _A11Y_VOICE_GUARD_INTERVAL:
            st.vo_check = now
            try:
                if renpy.game.preferences.self_voicing:
                    renpy.game.preferences.self_voicing = False
                    _a11y_log("自语音", u"再次关闭引擎自语音（持久化偏好把它打开了）")
            except Exception:
                pass
        try:
            _a11y_dialogue_tick()
        except Exception as exc:
            _a11y_error("tick.dialogue", exc)
        if now - getattr(st, "menu_time", 0.0) > _A11Y_MENU_INTERVAL:
            st.menu_time = now
            try:
                _a11y_menu_tick()
            except Exception as exc:
                _a11y_error("tick.menu", exc)
        if now - getattr(st, "focus_tick", 0.0) > _A11Y_FOCUS_INTERVAL:
            st.focus_tick = now
            try:
                _a11y_focus_tick()
            except Exception as exc:
                _a11y_error("tick.focus", exc)
        if now - getattr(st, "special_time", 0.0) > _A11Y_SPECIAL_INTERVAL:
            st.special_time = now
            try:
                _a11y_special_tick()
            except Exception as exc:
                _a11y_error("tick.special", exc)

    try:
        renpy.config.periodic_callbacks.append(_a11y_periodic)
        _a11y_log("挂载", u"config.periodic_callbacks 已登记（心跳 50ms）")
    except Exception as exc:
        _a11y_error("periodic.register", exc)
