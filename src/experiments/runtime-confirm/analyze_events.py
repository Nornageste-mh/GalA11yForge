#!/usr/bin/env python3
"""R17 runtime-confirm 日志统计。只输出哈希/长度/计数，不落台词正文。"""
import csv
import json
import os
import re
import statistics
from collections import Counter, defaultdict
from datetime import datetime

ROOT = r"D:\Steam\steamapps\common\Build"
EVENTS = os.path.join(ROOT, "BepInEx", "runtime-confirm", "events.tsv")
STATS = os.path.join(ROOT, "BepInEx", "runtime-confirm", "stats.tsv")
OUT = r"D:\DSHWorkSpace\research\19-re-runtime-confirm\evidence\stats"


def parse_ts(s):
    try:
        return datetime.strptime(s, "%H:%M:%S.%f")
    except ValueError:
        return None


def parse_detail(d):
    out = {}
    for part in d.split("|"):
        if "=" in part:
            k, v = part.split("=", 1)
            out[k.strip()] = v
    return out


def main():
    os.makedirs(OUT, exist_ok=True)
    events = []
    with open(EVENTS, encoding="utf-8") as f:
        for line in f:
            cols = line.rstrip("\n").split("\t")
            if len(cols) < 3:
                continue
            ts = parse_ts(cols[0])
            if ts is None:
                continue
            events.append({"ts": ts, "raw_ts": cols[0], "type": cols[1], "detail": cols[2],
                           "kv": parse_detail(cols[2])})

    by_type = Counter(e["type"] for e in events)
    dotexts = [e for e in events if e["type"] == "DOTEXT"]
    animates = [e for e in events if e["type"] == "ANIMATE"]
    messages = [e for e in events if e["type"] == "MESSAGE"]
    choices = [e for e in events if e["type"] == "CHOICE"]

    pairs = []
    match_stats = Counter()
    for a in animates:
        kv = a["kv"]
        num = kv.get("num", "")
        h = kv.get("hash", "")
        cands = [d for d in dotexts if d["ts"] <= a["ts"] and (a["ts"] - d["ts"]).total_seconds() <= 5.0]
        best = cands[-1] if cands else None
        row = {"animate_ts": a["raw_ts"], "num": num, "hash": h, "len": kv.get("len", ""),
               "target": kv.get("target", ""), "dur": kv.get("dur", ""), "name_len": kv.get("nameLen", ""),
               "voice": kv.get("voice", ""), "dotext_ts": "", "display": "", "jaLen": "", "scLen": "",
               "enLen": "", "tcLen": "", "match": "", "gap_ms": ""}
        if best:
            dk = best["kv"]
            row.update({"dotext_ts": best["raw_ts"], "display": dk.get("display", ""),
                        "jaLen": dk.get("jaLen", ""), "scLen": dk.get("scLen", ""),
                        "enLen": dk.get("enLen", ""), "tcLen": dk.get("tcLen", ""),
                        "gap_ms": int((a["ts"] - best["ts"]).total_seconds() * 1000)})
            for f in ("ja", "sc", "en", "tc"):
                if dk.get(f) == h:
                    row["match"] = f
                    break
            if not row["match"]:
                row["match"] = "none"
        else:
            row["match"] = "no-dotext"
        match_stats[row["match"]] += 1
        pairs.append(row)

    msg_pairs = Counter()
    for m in messages:
        cands = [d for d in dotexts if abs((m["ts"] - d["ts"]).total_seconds()) <= 0.2]
        if not cands:
            msg_pairs["no-dotext"] += 1
            continue
        d = cands[-1]
        got = "ja" if d["kv"].get("ja") == m["kv"].get("hash") else (
            "sc" if d["kv"].get("sc") == m["kv"].get("hash") else "other")
        msg_pairs[got] += 1

    # 重复：按 (num, hash) 分组
    groups = defaultdict(list)
    for a in animates:
        groups[(a["kv"].get("num", ""), a["kv"].get("hash", ""))].append(a)
    dups = []
    for (num, h), lst in groups.items():
        if len(lst) > 1:
            dups.append({"num": num, "hash": h, "count": len(lst), "first": lst[0]["raw_ts"],
                         "last": lst[-1]["raw_ts"], "lens": ",".join(x["kv"].get("len", "") for x in lst)})

    dgroups = defaultdict(list)
    for d in dotexts:
        dgroups[d["kv"].get("ja", "")].append(d)
    ddups = []
    for h, lst in dgroups.items():
        if h and len(lst) > 1:
            ddups.append({"ja": h, "count": len(lst), "first": lst[0]["raw_ts"],
                          "last": lst[-1]["raw_ts"],
                          "displays": ",".join(x["kv"].get("display", "") for x in lst),
                          "lens": ",".join(x["kv"].get("jaLen", "") for x in lst)})

    # 选项回显：Choice 的 Sc 哈希是否在随后 15s 的 AnimateText 里出现（说明选中项也走台词管线）
    cpat = re.compile(r"c(\d)Ja=(\d+):([0-9a-f]{8}),Sc=(\d+):([0-9a-f]{8})")
    echo_rows = []
    choice_option_hashes = set()
    for idx, c in enumerate(choices):
        opts = {}
        for m in cpat.finditer(c["detail"]):
            n = int(m.group(1)); opts[m.group(5)] = n
        choice_option_hashes |= set(opts)
        if not opts:
            continue
        end = choices[idx + 1]["ts"] if idx + 1 < len(choices) else None
        later = [a for a in animates if a["ts"] > c["ts"] and (end is None or a["ts"] < end)]
        for a in later:
            if a["kv"].get("hash") in opts:
                echo_rows.append({"choice_ts": c["raw_ts"], "asset": c["kv"].get("asset", ""),
                                  "option": opts[a["kv"].get("hash")], "animate_ts": a["raw_ts"],
                                  "num": a["kv"].get("num", ""), "hash": a["kv"].get("hash", ""),
                                  "delay_s": round((a["ts"] - c["ts"]).total_seconds(), 2)})
                break
    echo_all = [a for a in animates if a["kv"].get("hash") in choice_option_hashes]


    # 每分钟事件
    minute = Counter()
    for e in events:
        if e["type"] in ("ANIMATE", "DOTEXT", "CHOICE", "MESSAGE"):
            minute[(e["ts"].strftime("%H:%M"), e["type"])] += 1
    minutes = sorted({k[0] for k in minute})
    minute_rows = [{"minute": m, **{t: minute.get((m, t), 0) for t in ("ANIMATE", "DOTEXT", "CHOICE", "MESSAGE")}}
                   for m in minutes]

    # 目标/通道/时长
    targets = Counter(a["kv"].get("target", "") for a in animates)
    nums = Counter(a["kv"].get("num", "") for a in animates)
    dotext_display = Counter(d["kv"].get("display", "") for d in dotexts)
    voice_nonempty = sum(1 for a in animates if a["kv"].get("voice"))
    durs = []
    for a in animates:
        try:
            durs.append(float(a["kv"].get("dur", "0")))
        except ValueError:
            pass
    lengths = []
    for a in animates:
        try:
            lengths.append(int(a["kv"].get("len", "0")))
        except ValueError:
            pass
    gaps = []
    for x, y in zip(animates, animates[1:]):
        gaps.append((y["ts"] - x["ts"]).total_seconds())

    stats = {}
    if os.path.exists(STATS):
        with open(STATS, encoding="utf-8-sig") as f:
            for line in f:
                cols = line.rstrip("\n").split("\t")
                if len(cols) == 2 and cols[0] != "key":
                    stats[cols[0]] = cols[1]

    summary = {
        "schema": "gala11y.runtime-confirm.analysis/v1",
        "events_file": EVENTS,
        "event_lines": len(events),
        "by_type": dict(by_type),
        "animate": {
            "total": len(animates),
            "unique_num_hash": len(groups),
            "empty": sum(1 for a in animates if a["kv"].get("len") == "0"),
            "with_name": sum(1 for a in animates if a["kv"].get("nameLen", "0") not in ("", "0")),
            "voice_nonempty": voice_nonempty,
            "num_counts": dict(nums),
            "target_counts": dict(targets),
            "len_total": sum(lengths),
            "len_median": statistics.median(lengths) if lengths else None,
            "len_min": min(lengths) if lengths else None,
            "len_max": max(lengths) if lengths else None,
            "dur_median": round(statistics.median(durs), 2) if durs else None,
            "gap_median_s": round(statistics.median(gaps), 2) if gaps else None,
            "gap_max_s": round(max(gaps), 2) if gaps else None,
        },
        "dotext": {
            "total": len(dotexts),
            "unique_ja": len({d["kv"].get("ja") for d in dotexts}),
            "display_counts": dict(dotext_display),
            "empty_speak": sum(1 for d in dotexts if d["kv"].get("jaLen") == "0"),
        },
        "message": {"total": len(messages), "dottext_hash_match": dict(msg_pairs)},
        "choice": {"total": len(choices),
                   "assets": sorted({c["kv"].get("asset", "") for c in choices}),
                   "option_count": sum(int(c["kv"].get("count", 0) or 0) for c in choices),
                   "echo_rows": echo_rows,
                   "animate_hash_in_choice_options": len(echo_all)},
        "settext": {
            "total": int(stats.get("settext_total", 0) or 0),
            "empty": int(stats.get("settext_empty", 0) or 0),
            "full_line_match": int(stats.get("settext_full_line_match", 0) or 0),
            "full_match_per_animate": round(int(stats.get("settext_full_line_match", 0) or 0) / len(animates), 2) if animates else None,
            "empty_per_animate": round(int(stats.get("settext_empty", 0) or 0) / len(animates), 2) if animates else None,
        },
        "pairing": {"animate_to_dotext": dict(match_stats),
                    "rate_sc": round(match_stats.get("sc", 0) / len(animates), 4) if animates else None},
        "duplicates": {"pair_count": len(dups), "extra_events": sum(d["count"] - 1 for d in dups),
                       "rows": dups[:50],
                       "dotext_pair_count": len(ddups),
                       "dotext_rows": ddups[:50]},

        "minutes": minute_rows,
        "stats_final": stats,
    }

    with open(os.path.join(OUT, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    with open(os.path.join(OUT, "pairs.csv"), "w", newline="", encoding="utf-8-sig") as f:
        wr = csv.DictWriter(f, fieldnames=list(pairs[0].keys()) if pairs else
                            ["animate_ts", "num", "hash", "len", "target", "dur", "name_len", "voice",
                             "dotext_ts", "display", "jaLen", "scLen", "enLen", "tcLen", "match", "gap_ms"])
        wr.writeheader()
        wr.writerows(pairs)
    with open(os.path.join(OUT, "duplicates.csv"), "w", newline="", encoding="utf-8-sig") as f:
        wr = csv.DictWriter(f, fieldnames=["num", "hash", "count", "first", "last", "lens"])
        wr.writeheader()
        wr.writerows(dups)
    with open(os.path.join(OUT, "dotext-duplicates.csv"), "w", newline="", encoding="utf-8-sig") as f:
        wr = csv.DictWriter(f, fieldnames=["ja", "count", "first", "last", "displays", "lens"])
        wr.writeheader()
        wr.writerows(ddups)
    with open(os.path.join(OUT, "choice-echo.csv"), "w", newline="", encoding="utf-8-sig") as f:
        wr = csv.DictWriter(f, fieldnames=["choice_ts", "asset", "option", "animate_ts", "num", "hash", "delay_s"])
        wr.writeheader()
        wr.writerows(echo_rows)

    with open(os.path.join(OUT, "minutes.csv"), "w", newline="", encoding="utf-8-sig") as f:
        wr = csv.DictWriter(f, fieldnames=["minute", "ANIMATE", "DOTEXT", "CHOICE", "MESSAGE"])
        wr.writeheader()
        wr.writerows(minute_rows)
    print(json.dumps({k: summary[k] for k in ("event_lines", "by_type", "animate", "pairing", "duplicates")},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
