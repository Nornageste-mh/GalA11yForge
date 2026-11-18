# -*- coding: utf-8 -*-
import io, os, sys
p = r'D:\DSHWorkSpace\_renpy-buttons\game\game\a11y_platform\03_menu.rpy'
s = io.open(p, encoding='utf-8').read()
anchor = u'                texts = [_a11y_translate_text(t) for t in texts]\n'
diag = u'''                if name == u"confirm":
                    try:
                        _ns = _a11y_screen_nodes(name)
                        _a11y_log("诊断", u"confirm节点数=%d" % len(_ns))
                        for _i, _nd in enumerate(_ns[:14]):
                            try:
                                _cn = _a11y_u(_nd.__class__.__name__)
                            except Exception:
                                _cn = u"?"
                            try:
                                _tx = _a11y_displayable_text(_nd) or u""
                            except Exception as _e:
                                _tx = u"(err:%s)" % _a11y_u(_e.__class__.__name__)
                            _a11y_log("诊断", u"节点%d cls=%s txt=%s" % (_i, _cn, _tx[:50]))
                    except Exception as _e:
                        _a11y_log("诊断", u"节点dump失败 %s" % _a11y_u(_e))
                    try:
                        _fe = _a11y_focus_entries()
                        _a11y_log("诊断", u"focus数=%d" % len(_fe))
                        for _j, (_sn, _tx, _w) in enumerate(_fe[:10]):
                            try:
                                _cn2 = _a11y_u(_w.__class__.__name__)
                            except Exception:
                                _cn2 = u"?"
                            _a11y_log("诊断", u"focus%d screen=%s cls=%s txt=%s" % (_j, _sn, _cn2, _tx[:50]))
                    except Exception as _e2:
                        _a11y_log("诊断", u"focus dump失败 %s" % _a11y_u(_e2))
'''
assert anchor in s, 'anchor missing'
s = s.replace(anchor, anchor + diag, 1)
io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
c = os.path.join(os.path.dirname(p), '03_menu.rpyc')
if os.path.exists(c):
    os.remove(c)
print('diag patched')
