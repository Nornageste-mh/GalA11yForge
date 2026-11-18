# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# GalA11y Forge · 03_menu —— 选项菜单与主菜单播报
#
# 做法：不猜菜单文本从哪来，直接读"当前屏幕上可聚焦控件"的文本
#   renpy.display.focus.focus_list（每轮渲染重建，只能轮询，不能缓存引用）。
#   - choice 屏出现 → 整体播报一次「选项 N 个：1 … 2 …」
#   - 之后方向键移动 → 04_focus.rpy 逐项播报
#   - 主菜单出现 → 播报一次「主菜单」
# ═══════════════════════════════════════════════════════════════════════════

init -1600 python:

    # _a11y_get_screen 在 00_contract.rpy（init -1850）里，6.99 没有 renpy.get_screen
    # 导出，必须走 renpy.display.screen.get_screen，这里不再重复定义。

    def _a11y_focus_entries(prefix=u""):
        """[(screen_name, text, widget)]，按屏幕名可选过滤。"""
        out = []
        try:
            fl = renpy.display.focus.focus_list
        except Exception:
            return out
        for f in list(fl):
            try:
                d = getattr(f, "widget", f)
                txt = _a11y_displayable_text(d)
                if not txt:
                    continue
                scr = getattr(f, "screen", None)
                sname = u""
                if isinstance(scr, _A11Y_TEXT):
                    sname = scr
                else:
                    try:
                        sname = _a11y_u(getattr(scr, "screen_name", u"") or u"")
                    except Exception:
                        sname = u""
                if prefix and not sname.lower().startswith(prefix):
                    continue
                out.append((sname, txt, d))
            except Exception:
                continue
        return out

    def _a11y_screen_texts(screen_name, limit=24):
        """优先用 focus_list；拿不到就退化为遍历 screen 的显示树（有界）。"""
        key = screen_name.lower()
        texts = []
        for sname, txt, d in _a11y_focus_entries(prefix=key):
            if txt not in texts:
                texts.append(txt)
        if texts:
            return texts[:limit]
        scr = _a11y_get_screen(screen_name)
        if scr is None:
            return []
        stack = []
        try:
            kids = getattr(scr, "children", None)
            if kids:
                stack.extend(list(kids))
            else:
                child = getattr(scr, "child", None)
                if child is not None:
                    stack.append(child)
        except Exception:
            return []
        seen = set()
        depth_guard = 0
        while stack and len(texts) < limit and depth_guard < 400:
            depth_guard += 1
            node = stack.pop(0)
            if node is None or id(node) in seen:
                continue
            seen.add(id(node))
            txt = _a11y_displayable_text(node)
            if txt and txt not in texts:
                texts.append(txt)
            try:
                kids = getattr(node, "children", None)
                if kids:
                    stack.extend(list(kids))
            except Exception:
                pass
        return texts[:limit]

    def _a11y_menu_tick():
        st = A11yHost
        if not _a11y_speech_ok():
            return
        # 主菜单：只报一次名字，具体按钮交给焦点播报
        try:
            if _a11y_get_screen("main_menu") is not None and not st.main_menu_announced:
                st.main_menu_announced = True
                _a11y_log("菜单", u"主菜单出现")
                if st.speech is not None:
                    st.speech.say(_a11y_u("主菜单"), kind=u"菜单", dedupe=1.0)
        except Exception as exc:
            _a11y_error("menu.main", exc)

        choice = _a11y_choice_screen()
        if choice is None:
            st.choice_sig = u""
            return
        texts = _a11y_screen_texts("choice")
        if not texts:
            return
        sig = u"|".join(texts)
        if sig == st.choice_sig:
            return
        st.choice_sig = sig
        st.choice_announced = time.time()
        parts = []
        for i, t in enumerate(texts[:12]):
            parts.append(_a11y_u(u"%d %s") % (i + 1, t))
        line = _a11y_u(u"选项 %d 个：%s") % (len(texts), u"；".join(parts))
        _a11y_log("菜单", line)
        if st.speech is not None:
            st.speech.say(line, kind=u"菜单", interrupt=True, dedupe=0)
        # 抑制焦点播报对第一项的重复朗读
        try:
            cur = renpy.display.focus.get_focused()
            if cur is not None:
                st.focus_key = (id(cur), texts[0])
                st.focus_text = texts[0]
        except Exception:
            pass
