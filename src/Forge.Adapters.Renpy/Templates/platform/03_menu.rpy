# -*- coding: utf-8 -*-
# ═══════════════════════════════════════════════════════════════════════════
# Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） · 03_menu —— 选项菜单与主菜单播报
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

    # 屏幕文字清理：去掉零宽字符/不换行空格，统一空白；标题偶尔带尾冒号，
    # 去重时把「Please enter your name:」和「Please enter your name」当同一句。
    _A11Y_INVISIBLE_CHARS = (u"\u200b", u"\ufeff", u"\u00a0")

    def _a11y_clean_text(value):
        text = _a11y_u(value or u"")
        for ch in _A11Y_INVISIBLE_CHARS:
            text = text.replace(ch, u" ")
        return text.strip()

    def _a11y_text_key(value):
        return _a11y_clean_text(value).rstrip(u":").strip()

    def _a11y_add_unique(parts, value, limit=24):
        text = _a11y_clean_text(value)
        if not text:
            return
        key = _a11y_text_key(text)
        for existing in parts:
            if _a11y_text_key(existing) == key:
                return
        if len(parts) < limit:
            parts.append(text)

    # 0.2.26：屏幕里读到的是 source 文本，界面渲染时才翻译
    # （真机证据：中文确认框，播报却是英文原文）。
    # 播报前按当前文本语言翻译一次；拿不到翻译或出错就退回原文。
    def _a11y_translate_text(value):
        text = _a11y_clean_text(value)
        if not text:
            return text
        try:
            translated = renpy.translate_string(text)
        except Exception:
            try:
                translated = renpy.translation.translate_string(text)
            except Exception:
                return text
        translated = _a11y_clean_text(translated)
        return translated or text

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
            _a11y_add_unique(texts, txt, limit)
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
            _a11y_add_unique(texts, txt, limit)
            try:
                kids = getattr(node, "children", None)
                if kids:
                    stack.extend(list(kids))
            except Exception:
                pass
        return texts[:limit]

    # ─── 特殊屏幕：输入框 / 确认框 / 退出确认 ───────────────────────────
    # 这几类屏幕的提示文字通常不是可聚焦控件，focus_list 里看不到；只读
    # focus_list 会漏掉「请输入名字」「确定要退出吗」这类关键信息。
    # 做法：按屏幕名探测在场 → 播报一次屏幕内文字；input 屏再逐字回显。
    _A11Y_SPECIAL_SCREENS = [
        (u"input", u"输入框"),
        (u"name_input", u"输入框"),  # DDLC 等作品用自定义屏名做姓名输入
        (u"confirm", u"确认"),
        (u"quit", u"退出确认"),
    ]

    def _a11y_screen_nodes(screen_name, limit=400):
        """按显示树收集屏幕内节点（有界、去重），用于找提示文字与输入控件。"""
        scr = _a11y_get_screen(screen_name)
        if scr is None:
            return []
        out = []
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
            return out
        seen = set()
        guard = 0
        while stack and len(out) < limit and guard < 1200:
            guard += 1
            node = stack.pop(0)
            if node is None or id(node) in seen:
                continue
            seen.add(id(node))
            out.append(node)
            try:
                kids = getattr(node, "children", None)
                if kids:
                    stack.extend(list(kids))
            except Exception:
                pass
        return out

    def _a11y_screen_buttons(screen_name):
        """数一数屏幕里像按钮的控件，并尽量取出文字标签。

        0.2.27：确认框这类屏幕常用 imagebutton（按钮上没有文字），
        此时仍要告诉用户“有几个按钮、怎么操作”，而不是只念提示。
        """
        count = 0
        labels = []
        for node in _a11y_screen_nodes(screen_name):
            try:
                if _a11y_duck_role(node) != u"按钮":
                    continue
            except Exception:
                continue
            count += 1
            try:
                txt = _a11y_translate_text(_a11y_displayable_text(node))
            except Exception:
                txt = u""
            _a11y_add_unique(labels, txt, 6)
        return count, labels

    def _a11y_input_value(nodes):
        """从 input 屏的显示树里取当前输入值；拿不到返回 None。"""
        for node in nodes:
            try:
                val = getattr(node, "value", None)
                if val is not None and hasattr(val, "get_text") and callable(val.get_text):
                    txt = val.get_text()
                    if txt is not None:
                        return _a11y_clean_text(txt)
            except Exception:
                pass
            try:
                content = getattr(node, "content", None)
                if isinstance(content, _A11Y_TEXT):
                    return _a11y_clean_text(content)
            except Exception:
                pass
        return None

    def _a11y_special_tick():
        st = A11yHost
        if not _a11y_speech_ok():
            return
        seen = getattr(st, "special_seen", None)
        if not isinstance(seen, dict):
            seen = {}
            st.special_seen = seen
        for name, label in _A11Y_SPECIAL_SCREENS:
            is_input = (label == u"输入框")
            try:
                present = _a11y_get_screen(name) is not None
            except Exception:
                present = False
            if not present:
                if seen.get(name):
                    seen[name] = False
                    if is_input:
                        st.input_value = u""
                continue
            nodes = []
            if is_input:
                try:
                    nodes = _a11y_screen_nodes(name)
                except Exception:
                    nodes = []
            if not seen.get(name):
                seen[name] = True
                try:
                    texts = _a11y_screen_texts(name)[:8]
                except Exception:
                    texts = []
                texts = [_a11y_translate_text(t) for t in texts]
                cur = _a11y_input_value(nodes) if nodes else None
                parts = []
                for t in texts:
                    if cur and _a11y_text_key(t) == _a11y_text_key(cur):
                        continue
                    _a11y_add_unique(parts, t, 8)
                if is_input:
                    parts.append(u"输入后按回车确认")
                else:
                    try:
                        btn_count, btn_labels = _a11y_screen_buttons(name)
                    except Exception:
                        btn_count, btn_labels = 0, []
                    if btn_labels:
                        parts.append(u"按钮：" + u"、".join(btn_labels))
                    elif btn_count:
                        parts.append(u"有 %d 个按钮。按 Tab 切换，回车确认" % btn_count)
                line = label + ((u"：" + u"；".join(parts)) if parts else u"")
                _a11y_log("屏幕", line)
                if st.speech is not None:
                    st.speech.say(line, kind=u"屏幕", interrupt=True, dedupe=0)
                if is_input:
                    st.input_value = cur or u""
                continue
            if not is_input:
                continue
            # 输入框：只有值变化时才回显，避免每 120ms 重复念
            if not nodes:
                continue
            try:
                cur = _a11y_input_value(nodes)
            except Exception:
                cur = None
            if cur is None:
                continue
            prev = _a11y_u(getattr(st, "input_value", u"") or u"")
            if cur == prev:
                continue
            st.input_value = cur
            if prev and cur.startswith(prev):
                delta = cur[len(prev):]
            elif prev and prev.startswith(cur):
                delta = u"已删除"
            else:
                delta = cur[-12:]
            _a11y_log("输入", u"当前值=%s" % cur)
            if st.speech is not None and delta:
                st.speech.say(delta, kind=u"输入", interrupt=False, dedupe=0)

    # 这个节点是不是「没有动作的假按钮」：choice 屏的题目由
    # `textbutton i.caption action i.action` 渲染，题目的 action 是 None，
    # 仍然会进 focus_list，被当成第 1 个选项（0.2.44 真机实测）。
    def _a11y_button_has_no_action(node):
        try:
            return getattr(node, "action", None) is None
        except Exception:
            return False

    def _a11y_choice_options():
        """choice 屏的选项文本：只算按钮。

        0.2.44 真机发现：choice 屏的题目文字也是 Text，先前列选项时把题目
        当成第 1 项（「选项 3 个：1 问题是…；2 去公园；3 回家」），编号整体错位。
        做法：优先取 focus_list（可聚焦的必是按钮），拿不到再遍历显示树但只认按钮。
        """
        out = []
        for sname, txt, d in _a11y_focus_entries(prefix=u"choice"):
            if _a11y_button_has_no_action(d):
                continue
            _a11y_add_unique(out, txt, 12)
        if out:
            return out[:12]
        try:
            nodes = _a11y_screen_nodes("choice")
        except Exception:
            return out
        for node in nodes:
            try:
                if _a11y_duck_role(node) != u"按钮":
                    continue
                if _a11y_button_has_no_action(node):
                    continue
            except Exception:
                continue
            txt = _a11y_displayable_text(node)
            if txt:
                _a11y_add_unique(out, txt, 12)
        return out[:12]

    def _a11y_choice_prompt():
        """choice 屏上方的提示文字（题目/说明）。这些文字不是按钮，focus_list 读不到。"""
        try:
            nodes = _a11y_screen_nodes("choice")
        except Exception:
            return u""
        option_keys = set()
        for t in _a11y_choice_options():
            option_keys.add(_a11y_text_key(t))
        parts = []
        for node in nodes:
            try:
                if _a11y_duck_role(node) == u"按钮":
                    continue
            except Exception:
                pass
            txt = _a11y_displayable_text(node)
            if not txt:
                continue
            key = _a11y_text_key(txt)
            if not key or key in option_keys:
                continue
            _a11y_add_unique(parts, _a11y_translate_text(txt), 2)
            if len(parts) >= 2:
                break
        return u"；".join(parts)

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
        texts = [_a11y_translate_text(t) for t in _a11y_choice_options()]
        if not texts:
            return
        # 选项上方通常有一句题目/说明（不是按钮，focus_list 读不到），
        # 只播选项不播题目，盲人用户会不知道“在选什么”。0.2.16。
        prompt = _a11y_choice_prompt()
        sig = prompt + u"||" + u"|".join(texts)
        if sig == st.choice_sig:
            return
        st.choice_sig = sig
        st.choice_announced = time.time()
        parts = []
        for i, t in enumerate(texts[:12]):
            parts.append(_a11y_u(u"%d %s") % (i + 1, t))
        if prompt:
            line = _a11y_u(u"提示：%s。选项 %d 个：%s") % (prompt, len(texts), u"；".join(parts))
        else:
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
