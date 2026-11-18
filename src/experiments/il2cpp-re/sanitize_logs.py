# -*- coding: utf-8 -*-
"""R18 / task-21：证据日志脱敏（幂等）。

纪律要求：日志最多含 3 条 ≤50 字的短样本，且不得导出剧本/资源。
探针调试期曾把 uGUI 文本原样打进日志（设置面板标签/覆盖层题字，非剧本），交付前统一脱敏：
  1) CJK 连续串替换为 <cjk-redacted:N>（N=字符数），保留长度信息；
  2) 超过 3 条的 sample 行替换为省略说明；
  3) 文件头插入一行 ASCII 脱敏说明（重复运行不会重复插入）。
用法：D:\\python\\python.exe sanitize_logs.py <evidenceDir>
"""
import io
import os
import re
import sys

CJK_RUN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]+")
SAMPLE = re.compile(r"^(sample(\d+): )(.*)$")

HEADER = ("# [redacted] CJK samples replaced with <cjk-redacted:N>; max 3 sample lines per log; "
          "originals kept at D:\\_re\\sh\\BepInEx (not distributed)\n")
HEADER_PREFIXES = ("# [redacted]", "# [脱敏]", "# [sanitized]")


def clean_line(line):
    m = SAMPLE.match(line)
    if m:
        if int(m.group(2)) > 3:
            return m.group(1) + "<omitted: over 3-sample quota>\n"
        head, body = m.group(1), m.group(3)
        return head + CJK_RUN.sub(lambda x: "<cjk-redacted:%d>" % len(x.group(0)), body)
    return CJK_RUN.sub(lambda x: "<cjk-redacted:%d>" % len(x.group(0)), line)


def main():
    d = sys.argv[1]
    touched = []
    for name in sorted(os.listdir(d)):
        if not name.lower().endswith((".log", ".txt")):
            continue
        p = os.path.join(d, name)
        with io.open(p, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
        # 去掉历史遗留的（可能已被二次转义的）说明行，最后统一插一条 ASCII 头
        body = [l for l in lines if not l.startswith(HEADER_PREFIXES)]
        out = [HEADER] + [clean_line(l) for l in body]
        if [l.rstrip("\r\n") for l in out] == [l.rstrip("\r\n") for l in lines]:
            continue
        with io.open(p, "w", encoding="utf-8", newline="\n") as f:
            f.writelines(out)
        touched.append(name)
    print("sanitized %d file(s): %s" % (len(touched), ", ".join(touched)))
    for name in sorted(os.listdir(d)):
        if not name.lower().endswith((".log", ".txt")):
            continue
        with io.open(os.path.join(d, name), encoding="utf-8", errors="replace") as f:
            rest = "".join(l for l in f.readlines() if not l.startswith(HEADER_PREFIXES))
        left = CJK_RUN.findall(rest)
        if left:
            print("WARNING: %s still has CJK: %r" % (name, left[:5]))


if __name__ == "__main__":
    main()
