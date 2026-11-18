# -*- coding: utf-8 -*-
"""R18 / task-21：把 dump.cs 里定位到的 IL2CPP 候选 hook 点按 research/04-engines 的评分规则打分，
并叠加本机 BepInEx 探针的运行时结果，输出 CSV。

评分维度来自 research/04-engines/evidence/scoring-rubric.json（S1..S8 正分，N1..N4 负分）。
静态限制：S6（CJK 字面量密度）在 dump.cs 里无法按方法归属（Il2CppDumper 只导出全局
stringliteral.json，没有 per-method xref），静态阶段记 0 并标 s6_status=static_unscored；
若探针在运行时真的抓到非空中文台词，则 S6 置 10 并标 runtime_confirmed（此时满分=100）。

行号校验：对每个候选，在 dump.cs 中找所有 "方法名(" 出现处，向上回溯最近的 type 声明，
只有“所属类型短名 == 声明的 type”且该出现处是方法定义（不是调用）时才算 ok。

用法：
  D:\\python\\python.exe score_candidates.py ^
    --dump D:\\_re\\tools\\il2cppdumper\\dump.cs ^
    --probe-log D:\\_re\\sh\\BepInEx\\re-probe.log ^
    --out D:\\DSHWorkSpace\\research\\20-il2cpp-re\\evidence\\candidate-hooks.csv
"""
import argparse
import csv
import io
import os
import re
import sys

# 候选点：所有 RVA/行号均取自 D:\_re\tools\il2cppdumper\dump.cs（Il2CppDumper 6.7.46 输出）
#   probe_key 对应插件 v2 summary 里的计数前缀（A/B/C/D/E/F/G/H/I）
CANDIDATES = [
    dict(
        probe_key="G",
        method="TextPrinterManager.PrintTextAsync(string printerId, string text, string authorId, float speed, AsyncToken)",
        type_full="Naninovel.TextPrinterManager", assembly="Elringus.Naninovel.Runtime",
        dump_line=184392, rva="0x8BA340", slot=68, ret="UniTask", kind="method",
        scores=dict(S1=25, S2=20, S3=20, S4=10, S5=0, S6=0, S7=10, S8=5, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L184392 管理器方法；签名直接带 text+authorId+printerId，一次调用拿全一行（含说话人）；"
              "被 UITextPrinter.RevealTextAsync 链路调用",
        risk="返回 UniTask（struct，IL2CPP 下不能 await）；Harmony Prefix 只读参数、不碰返回值 → 补丁能装上",
    ),
    dict(
        probe_key="H",
        method="add_OnPrintTextStarted(Action<PrintTextArgs>)",
        type_full="Naninovel.TextPrinterManager", assembly="Elringus.Naninovel.Runtime",
        dump_line=184392, rva="0x8BA860", slot=49, ret="Void", kind="event",
        scores=dict(S1=25, S2=20, S3=15, S4=5, S5=0, S6=0, S7=10, S8=5, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L184392 官方事件；PrintTextArgs 字段 Text 0x18 / AuthorId 0x20 / Printer 0x10 / Speed 0x28；"
              "订阅后无需 patch 任何原生代码",
        risk="IL2CPP 委托必须 DelegateSupport.ConvertDelegate<Il2CppSystem.Action<PrintTextArgs>>；"
              "订阅时机必须在 Engine 初始化后",
    ),
    dict(
        probe_key="A",
        method="UITextPrinter.set_Text(string)",
        type_full="Naninovel.UITextPrinter", assembly="Elringus.Naninovel.Runtime",
        dump_line=184733, rva="0x221540", slot=76, ret="Void", kind="property_setter",
        scores=dict(S1=25, S2=20, S3=20, S4=10, S5=0, S6=0, S7=10, S8=5, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L184733 抽象 Text 属性实现；后备字段 text 0xA8；ITextPrinterActor 的文本写入口",
        risk="拿到的是含富文本标签的原始串（需清洗）；实测多为空串（先清空再逐字上屏）",
    ),
    dict(
        probe_key="B",
        method="RevealableTextPrinterPanel.set_PrintedText(string)",
        type_full="Naninovel.UI.RevealableTextPrinterPanel", assembly="Elringus.Naninovel.Runtime",
        dump_line=224227, rva="0x9B5C80", slot=83, ret="Void", kind="property_setter",
        scores=dict(S1=25, S2=15, S3=20, S4=5, S5=5, S6=0, S7=10, S8=5, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L224227 面板最终显示文本（经过面板处理后的串）；同面板 set_AuthorNameText 独立给说话人",
        risk="依赖该面板类型；若游戏换 UITextPrinterPanel 子类则失效",
    ),
    dict(
        probe_key="D",
        method="RevealableTMProText.set_Text(string)",
        type_full="Naninovel.UI.RevealableTMProText", assembly="Elringus.Naninovel.Runtime",
        dump_line=224492, rva="0x9B3480", slot=175, ret="Void", kind="property_setter",
        scores=dict(S1=25, S2=15, S3=20, S4=10, S5=0, S6=0, S7=10, S8=0, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L224492 真正落到 TMP 的文本组件（本游戏无 Naninovel.UI.RevealableText，"
              "先例用的类名在此版本不存在）；后备字段 assignedText 0x820",
        risk="只有 TMP 分支有；uGUI 分支是 RevealableUIText（本作实测走 uGUI 分支，见 I）",
    ),
    dict(
        probe_key="I",
        method="RevealableUIText.set_Text(string)",
        type_full="Naninovel.UI.RevealableUIText", assembly="Elringus.Naninovel.Runtime",
        dump_line=224631, rva="0x9B6DA0", slot=98, ret="Void", kind="property_setter",
        scores=dict(S1=25, S2=15, S3=20, S4=10, S5=0, S6=0, S7=10, S8=0, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L224631 class RevealableUIText : Text, IRevealableText —— uGUI 分支的 IRevealableText.Text 实现"
              "（大写 Text，Slot 98，RVA 0x9B6DA0）；配套 RevealProgress/RevealNextChars 逐字显影",
        risk="注意不是基类的 set_text（小写）：Harmony 的 attribute 走 DeclaredMethod，按 set_text 打会"
             "报 Patching exception in method null；必须打 set_Text",
    ),
    dict(
        probe_key="C",
        method="RevealableTextPrinterPanel.set_AuthorNameText(string)",
        type_full="Naninovel.UI.RevealableTextPrinterPanel", assembly="Elringus.Naninovel.Runtime",
        dump_line=224227, rva="0x9B5C40", slot=85, ret="Void", kind="property_setter",
        scores=dict(S1=25, S2=10, S3=20, S4=5, S5=5, S6=0, S7=10, S8=0, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L224227 只给说话人名，不含台词；适合与 set_PrintedText 配对补齐归属",
        risk="单独用抓不到台词",
    ),
    dict(
        probe_key="",
        method="ChoiceHandlerPanel.AddChoiceButton(ChoiceState)",
        type_full="Naninovel.UI.ChoiceHandlerPanel", assembly="Elringus.Naninovel.Runtime",
        dump_line=220322, rva="0x992370", slot=85, ret="Void", kind="method",
        scores=dict(S1=25, S2=10, S3=20, S4=0, S5=5, S6=0, S7=10, S8=5, N1=0, N2=0, N3=0, N4=0),
        basis="dump.cs:L220322 选项按钮逐个加入；配合 add_OnChoice 可得选项顺序（无障碍需要读选项）",
        risk="不是台词通道；需要 ChoiceState 字段布局（未展开）",
    ),
    dict(
        probe_key="E",
        method="TMP_Text.set_text(string)",
        type_full="TMPro.TMP_Text", assembly="Unity.TextMeshPro",
        dump_line=459951, rva="", slot="", ret="Void", kind="property_setter",
        scores=dict(S1=0, S2=15, S3=20, S4=0, S5=0, S6=0, S7=0, S8=0, N1=-20, N2=-15, N3=0, N4=0),
        basis="dump.cs:L459951 附近 TMPro 段；所有 TMP 文本（含 UI 按钮/标题）都会触发 → 噪声极大",
        risk="必须给 TMP_Text 打补丁：interop 里 set_text 声明在 TMP_Text，不在 TextMeshProUGUI"
             "（实测 AccessTools.DeclaredMethod: Could not find method for type TMPro.TextMeshProUGUI）",
    ),
    dict(
        probe_key="F",
        method="UnityEngine.UI.Text.set_text(string)",
        type_full="UnityEngine.UI.Text", assembly="UnityEngine.UI",
        dump_line=473700, rva="0xDE0140", slot=0, ret="Void", kind="property_setter",
        scores=dict(S1=0, S2=15, S3=20, S4=0, S5=0, S6=0, S7=0, S8=0, N1=-20, N2=-15, N3=0, N4=0),
        basis="dump.cs:L473700 uGUI 文本；本作大量 UI（存档格/设置项/按钮）都用它 → 只能当诊断",
        risk="会把「全屏/窗口化/SAVE/LOAD」等 UI 文案一起抓进来（实测已见，822 次）",
    ),
]

TYPE_DECL = re.compile(
    r"^(?:public |internal |private |protected |abstract |sealed |static |partial |readonly |unsafe )*"
    r"(class|struct|interface|enum)\s+([A-Za-z_0-9`]+)")


def enclosing_type(lines, idx):
    """idx 是 0-based 行下标；返回 (类型短名, 1-based 声明行号)。"""
    for j in range(idx, -1, -1):
        m = TYPE_DECL.match(lines[j])
        if m:
            return m.group(2), j + 1
    return None, None


def check(lines, member, type_short):
    """找 member 的定义行（所属类型匹配）。返回 (ok, 1-based 行号, 说明)。"""
    pat = re.compile(r"(?<![\w.])" + re.escape(member) + r"\s*\(")
    seen = []
    for i, line in enumerate(lines):
        if not pat.search(line):
            continue
        # 方法定义行以访问修饰符/override/abstract 等开头，且含方法名( ... )
        if not re.match(r"^\s*(?:\[|public |private |protected |internal |static |virtual |override |"
                        r"abstract |sealed |new |extern |unsafe )", line):
            continue
        t, decl = enclosing_type(lines, i)
        seen.append((t, i + 1))
        if t == type_short:
            return True, i + 1, "declared in %s (L%d)" % (t, decl)
    if seen:
        return False, None, "found under %s instead" % ", ".join(sorted({str(s[0]) for s in seen}))
    return False, None, "not found"


SUMMARY_RE = re.compile(r"^([A-I]) [^=]*=(\d+)(?: \(empty/null=(\d+), nonEmpty=(\d+)\))?")
SUMMARY_RE2 = re.compile(r"^([A-I]) [^=]*=(\d+)(?: \(empty/null=(\d+)\))?")


def parse_probe(path):
    """解析插件 summary 段：返回 {A: (calls, nonempty)}。nonempty 未知时记 None。"""
    out = {}
    if not path or not os.path.exists(path):
        return out
    with io.open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            m = SUMMARY_RE.match(line) or SUMMARY_RE2.match(line)
            if not m:
                continue
            key, calls, empty, nonempty = m.group(1), int(m.group(2)), m.group(3), m.group(4)
            if nonempty is not None:
                ne = int(nonempty)
            elif empty is not None:
                ne = calls - int(empty)
            else:
                ne = None
            out[key] = (calls, ne)
    return out


LINES_RE = re.compile(r"^([A-Z])-([A-Za-z0-9_]+)\t(\d+)\t(\d+)\t")


def parse_lines(path):
    """解析 hook-*.log 同目录的 -lines.txt：按来源字母统计非空(含 CJK)条数。

    行格式：# 注释 或 `<KEY>-<name>\\t<len>\\t<cjk>\\t<has_richtag>\\t<sha1_8>`。
    返回 {KEY: nonempty_count}，用于补 summary 未拆分空/非空的通道（G/H/J）。
    """
    out = {}
    if not path or not os.path.exists(path):
        return out
    with io.open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith("#"):
                continue
            m = LINES_RE.match(line)
            if not m:
                continue
            key, cjk = m.group(1), int(m.group(3))
            if cjk > 0:
                out[key] = out.get(key, 0) + 1
    return out


def verdict(calls, nonempty):
    if calls is None:
        return "未测", "—"
    if calls == 0:
        return "运行时不触发（静默失败）", "低"
    if nonempty is None:
        return "触发 %d 次（未区分空串）" % calls, "中"
    if nonempty == 0:
        return "触发 %d 次，全为空串/null" % calls, "低"
    return "触发 %d 次，非空 %d 条" % (calls, nonempty), "高"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--probe-log", default="", help="插件 re-probe.log，含 [summary] 段")
    ap.add_argument("--lines", default="", help="同轮 hook-*-lines.txt，用于补非空条数")
    a = ap.parse_args()

    if not os.path.exists(a.dump):
        sys.exit("dump.cs not found: " + a.dump)

    with io.open(a.dump, encoding="utf-8", errors="replace") as f:
        lines = f.readlines()

    probe = parse_probe(a.probe_log)
    line_ne = parse_lines(a.lines)
    for key, ne in line_ne.items():
        calls = probe.get(key, (None, None))[0]
        probe[key] = (calls if calls is not None else ne, ne)
    rows = []
    problems = []
    for c in CANDIDATES:
        member = c["method"].split("(")[0].split(".")[-1]
        type_short = c["type_full"].split(".")[-1]
        ok, mline, detail = check(lines, member, type_short)
        if not ok:
            problems.append("%s.%s: %s" % (type_short, member, detail))

        s = dict(c["scores"])
        calls, nonempty = probe.get(c["probe_key"], (None, None)) if c["probe_key"] else (None, None)
        s6_status = "static_unscored"
        base_total = sum(s.values())
        # 只给“真文本源”补 S6；诊断类（基类 setter，基准分已被负向项压到 <30）不加分
        if nonempty and nonempty > 0 and base_total >= 30:
            s["S6"] = 10
            s6_status = "runtime_confirmed"
        total = sum(s.values())
        vtxt, conf = verdict(calls, nonempty)

        rows.append(dict(
            target=c["method"],
            type=c["type_full"],
            assembly=c["assembly"],
            type_decl_line=c["dump_line"],
            method_line=mline if ok else "",
            rva=c["rva"],
            slot=c["slot"],
            returns=c["ret"],
            kind=c["kind"],
            S1=s["S1"], S2=s["S2"], S3=s["S3"], S4=s["S4"], S5=s["S5"], S6=s["S6"],
            S7=s["S7"], S8=s["S8"],
            N1=s["N1"], N2=s["N2"], N3=s["N3"], N4=s["N4"],
            total=total,
            grade="A" if total >= 70 and s["S1"] > 0 else ("B" if total >= 50 else ("C" if total >= 30 else "排除")),
            line_check="ok" if ok else "MISMATCH",
            line_detail=detail,
            probe_key=c["probe_key"],
            runtime_calls="" if calls is None else calls,
            runtime_nonempty="" if nonempty is None else nonempty,
            runtime_verdict=vtxt,
            confidence=conf,
            s6_status=s6_status,
            basis=c["basis"],
            risk=c["risk"],
        ))

    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with io.open(a.out, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        for r in rows:
            w.writerow(r)

    print("wrote %s (%d rows); probe summarised keys: %s" % (a.out, len(rows), ",".join(sorted(probe))))
    print("note: S6 static=0 -> 静态满分 90；运行时抓到非空台词后 S6=10 -> 满分 100")
    for r in rows:
        print("%-3s %3d  %-52s %-9s %-6s %s" % (
            r["grade"], r["total"], r["target"][:52], r["line_check"], r["confidence"], r["runtime_verdict"]))
    if problems:
        print("PROBLEMS:")
        for p in problems:
            print("  " + p)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
