import pathlib

p = pathlib.Path('docs/16-v0.2-朗读接线证据.md')
s = p.read_text(encoding='utf-8')
addition = '''

## §13 0.2.5 开发版：字段兜底真机成功 + task-50 产品问题修复

- 真机（GalMaster 4513880）：6 个钩子仍全部挂载；详细日志出现完整台词（含说话人）：
  `[Speech] 晓夜，Xiao Ye，老哥，我大概还有半个小时就到你住的地方咯？`、
  `[Speech] 黎铭，Li Ming，我还在商业街，干脆你顺路过来这边，等下一起回去吧？`、
  `[Speech] 正式开始独居的第二周，晓夜提议要来我住的地方看看。`
  同时有 `[Hook:hook-001] 命中 DialogueHandle:ShowDialogue；按实例文本字段兜底朗读。`
- 结论：无字符串参数的入口，靠候选证据里的实例文本字段也能朗读；同一生成器在第二款 Unity Mono 游戏上闭环。
- 同批修复：install 自动找 exe 旁 `bepinex\\`；plan 人工待办/风险按本计划动态生成；回滚后 verify 输出 `overall=not_installed`（exit 0，不假通过）；verify 报告额外归档 `verify-<时间戳>.md`。
- 测试：全解决方案 618 通过 0 失败（Core 82）。
- 证据：`docs/37-字段兜底真机验证-v0.md`、`_galmaster-field-e2e/evidence/`。
'''
if '§13 0.2.5 开发版' not in s:
    p.write_text(s + addition, encoding='utf-8')
    print('docs/16 appended §13')
else:
    print('docs/16 already has §13')

p = pathlib.Path('docs/33-0.2.4-计划.md')
s = p.read_text(encoding='utf-8')
note = '''

## 追加：0.2.5 开发进展（2026-11-15 04:00）

- 0.2.4.0 已发布（612 测试基线）。
- 0.2.5 进行中：字段兜底朗读（GalMaster 真机成功）、plan 人工待办动态化、install 自动找 bepinex、回滚后 verify 的 not_installed 语义、verify 报告存档；测试 618 全绿。
- 待收尾：KKZ 适配器 v1 真机闭环（task-51 子代理）；收口后升 0.2.5.0 并发布。
'''
if '0.2.5 开发进展' not in s:
    p.write_text(s + note, encoding='utf-8')
    print('docs/33 updated')
else:
    print('docs/33 already updated')
