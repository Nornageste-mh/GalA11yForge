#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""match.py —— GalA11y Forge 签名库匹配原型（R19）。

输入：
  --probe     sigprobe.py 产出的 galforge-probe-v1 JSON（文件布局/版本/token）
  --analysis  unity-mono-analyzer 的 analysis.json（类型/方法/textSink/frameworkTrace）
  --facts     可选：动态/人工补充证据（fields、methods、versions），格式见 README
  --game-name 覆盖游戏名
输出（stdout 或 --out）：
  galforge-match-v1 JSON：检测引擎、签名排序、分数、命中证据、缺失证据、解释

批量模式：
  --batch manifest.json --out-dir DIR --csv FILE
  manifest 每项：{id,name,appid,priority,path,analysis,expected[],note}
  → 逐游戏跑 sigprobe + match，写 probes/*.json、matches/*.json、CSV。

设计要点：
  * 分数是「证据点数」的加和（负权重可做反证），上限 100；阈值 A≥70 / B≥50 / C≥30，
    与 research/04-engines §11 的 S1–S8 分级一致。
  * identity 证据（框架身份）至少命中 1 条才可能高分，否则封顶 30（防通用证据误报）。
  * 证据类型不可用（如 v1 分析 JSON 无字段名）时不计入分母，也不会被当成「未命中」。
"""
from __future__ import annotations

import argparse
import csv
import fnmatch
import json
import os
import re
import sys

SCHEMA = "galforge-match-v1"
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DB = os.path.abspath(os.path.join(
    HERE, "..", "..", "..", "research", "21-signature-db", "evidence", "signatures.json"))


# ------------------------------------------------------------------ evidence
def _norm(p: str) -> str:
    return p.replace("\\", "/").lower()


class Evidence:
    def __init__(self, probe: dict | None = None, analysis: dict | None = None,
                 facts: dict | None = None, game_name: str | None = None):
        self.probe = probe or {}
        self.analysis = analysis or {}
        self.facts = facts or {}

        lay = self.probe.get("layout", {})
        self.files = [_norm(p) for p in lay.get("paths", [])]
        self.top_dirs = [e["name"].lower() for e in lay.get("top_level", []) if e.get("kind") == "dir"]
        self.dlls = {d.lower() for d in lay.get("managed_dlls", [])}
        self.dlls |= {d.lower() for d in lay.get("interop_dlls", [])}
        self.tokens: dict[str, int] = {}
        for chunk in (self.probe.get("binary_tokens") or {}).values():
            for k, v in (chunk.get("counts") or {}).items():
                self.tokens[k.lower()] = self.tokens.get(k.lower(), 0) + int(v)

        self.versions: list[dict] = []
        for v in self.probe.get("exe_version", []) or []:
            self.versions.append({k.lower(): str(val) for k, val in v.items()})
        if self.probe.get("renpy_version"):
            self.versions.append({"path": "<renpy>", "renpy_version": self.probe["renpy_version"]})
        if (self.probe.get("unity") or {}).get("versionGuess"):
            self.versions.append({"path": "<unity>", "unity_version":
                                  self.probe["unity"]["versionGuess"]})

        # ---- analysis
        self.types: list[str] = []
        self.methods: list[dict] = []
        self.fields: list[dict] = []
        self.text_sinks: list[dict] = []
        self.framework_traces: list[dict] = []
        self.candidates: list[dict] = []
        self.stats: dict = {}
        self.flavor: str | None = None
        self.engine = ""
        self.unity_version = None

        if self.probe:
            u = self.probe.get("unity") or {}
            if u.get("flavor"):
                self.flavor = "unity-" + u["flavor"]
            self.engine = self.probe.get("engine_hint") or ""
            self.unity_version = u.get("versionGuess")

        if self.analysis:
            a = self.analysis
            self.flavor = self.flavor or ("unity-il2cpp" if (a.get("unity") or {}).get(
                "il2CppMarkersPresent") else "unity-mono")
            self.engine = self.engine or ("Unity (%s)" % ("IL2CPP" if self.flavor == "unity-il2cpp" else "Mono"))
            self.unity_version = self.unity_version or (a.get("unity") or {}).get("version")
            self.stats = a.get("stats") or {}
            for tr in a.get("frameworkTraces") or []:
                self.framework_traces.append(tr)
                self.types += [t for t in (tr.get("sampleTypes") or []) if t]
            for c in a.get("candidates") or []:
                self.candidates.append(c)
                if c.get("typeFullName"):
                    self.types.append(c["typeFullName"])
                if c.get("methodFullName"):
                    self.methods.append({
                        "type": c.get("typeFullName") or "",
                        "name": c.get("methodName") or _method_name(c.get("methodFullName", "")),
                        "full": c.get("methodFullName") or "",
                        "signature": c.get("signature") or "",
                    })
            for s in a.get("textSinks") or []:
                self.text_sinks.append(s)
            for f in a.get("fields") or []:
                self.fields.append(f)

        for t in (self.probe.get("type_names_sample") or []):
            self.types.append(t)
        for t in (self.facts.get("types") or []):
            self.types.append(t)
        for m in (self.facts.get("methods") or []):
            self.methods.append({"type": m.get("type", ""), "name": m.get("name", ""),
                                 "full": m.get("full", ""), "signature": m.get("signature", "")})
        for f in (self.facts.get("fields") or []):
            self.fields.append(f)
        for v in (self.facts.get("versions") or []):
            self.versions.append({k.lower(): str(val) for k, val in v.items()})

        # 去重，保留顺序
        self.types = list(dict.fromkeys([t for t in self.types if t]))
        self.name = game_name or (self.probe.get("game") or {}).get("name") \
            or (self.analysis.get("game") or {}).get("name") or "<unknown>"

    # -------------------------------------------------------------- matching
    def available(self, kind: str) -> bool:
        return {
            "file": bool(self.files),
            "dir": bool(self.top_dirs),
            "dll": bool(self.dlls),
            "type": bool(self.types),
            "method": bool(self.methods),
            "field": bool(self.fields),
            "string": bool(self.tokens),
            "version": bool(self.versions),
            "text_sink": bool(self.text_sinks),
            "framework_trace": bool(self.framework_traces),
            "engine": bool(self.flavor or self.engine),
        }.get(kind, False)

    def match_test(self, test: dict) -> tuple[bool, str | None]:
        kind = test["evidence"]
        mode = test.get("mode", "regex" if kind in ("type", "method", "field") else "substring")
        pat = str(test.get("pattern", ""))
        cs = bool(test.get("case_sensitive", False))

        if kind in ("file", "dir"):
            pool = self.files if kind == "file" else self.top_dirs
            p = pat if cs else pat.lower()
            for item in pool:
                s = item if cs else item
                if mode == "glob" and fnmatch.fnmatchcase(s, p):
                    return True, item
                if mode == "exact" and s == p:
                    return True, item
                if mode == "substring" and p in s:
                    return True, item
                if mode == "regex" and re.search(pat, item, 0 if cs else re.IGNORECASE):
                    return True, item
            return False, None

        if kind == "dll":
            pool = sorted(self.dlls)
            return _text_match(pool, pat, mode, cs)

        if kind == "type":
            return _text_match(self.types, pat, mode if mode != "glob" else "regex", cs)

        if kind in ("method", "field"):
            rows = self.methods if kind == "method" else self.fields
            scope = test.get("scope_type")
            scope_re = re.compile(scope, 0 if cs else re.IGNORECASE) if scope else None
            hits = []
            for row in rows:
                typ = row.get("type", "")
                if scope_re and not scope_re.search(typ):
                    continue
                name = row.get("name", "") or ""
                ok, detail = _text_match([name], pat, mode, cs)
                if ok:
                    hits.append(f"{typ}::{name}")
            return (True, hits[0]) if hits else (False, None)

        if kind == "string":
            key = pat.lower()
            count = self.tokens.get(key, 0)
            need = int(test.get("min_count", 1))
            if count >= need:
                detail = f"token '{pat}' ×{count} (需≥{need})"
                return True, detail
            return False, None

        if kind == "version":
            fld = test.get("field")
            pool = []
            for v in self.versions:
                if fld:
                    if fld.lower() in v:
                        pool.append(v[fld.lower()])
                else:
                    pool += [str(x) for k, x in v.items() if k not in ("path",)]
            return _text_match(pool, pat, mode, cs)

        if kind == "text_sink":
            pool = []
            for s in self.text_sinks:
                api = s.get("api", "")
                if int(test.get("min_calls", 1)) <= int(s.get("calls", 0)):
                    pool.append(api)
            return _text_match(pool, pat, mode, cs)

        if kind == "framework_trace":
            pool = []
            for tr in self.framework_traces:
                pool += [str(tr.get("id", "")), str(tr.get("displayName", ""))]
            return _text_match(pool, pat, mode, cs)

        if kind == "engine":
            pool = [self.flavor or "", self.engine or ""]
            return _text_match(pool, pat, mode, cs)

        return False, None


def _method_name(full: str) -> str:
    m = re.search(r"::([A-Za-z0-9_.<>]+)\s*\(", full or "")
    return m.group(1) if m else (full or "").split("::")[-1]


def _text_match(pool, pattern: str, mode: str, cs: bool) -> tuple[bool, str | None]:
    if not pool:
        return False, None
    p = pattern if cs else pattern.lower()
    for raw in pool:
        s = str(raw)
        t = s if cs else s.lower()
        if mode == "exact" and t == p:
            return True, s
        if mode == "prefix" and t.startswith(p):
            return True, s
        if mode == "substring" and p in t:
            return True, s
        if mode == "regex":
            try:
                if re.search(pattern, s, 0 if cs else re.IGNORECASE):
                    return True, s
            except re.error:
                return False, None
    return False, None


# ------------------------------------------------------------------ scoring
TIERS = [(70, "A"), (50, "B"), (30, "C")]


def tier_for(score: int) -> str:
    for limit, name in TIERS:
        if score >= limit:
            return name
    return "D"


def evaluate(sig: dict, ev: Evidence, policy: dict) -> dict | None:
    flavor = ev.flavor or "unknown"
    engines = sig.get("engines") or []
    if engines and flavor != "unknown" and flavor not in engines:
        return None

    matched, missing, unavailable = [], [], []
    total = 0
    identity_hits = 0
    kinds = set()
    for test in sig.get("tests", []):
        applies = test.get("applies_to")
        if applies and flavor != "unknown" and flavor not in applies:
            continue
        if not ev.available(test["evidence"]):
            unavailable.append(test.get("id") or test["evidence"])
            continue
        hit, detail = ev.match_test(test)
        w = int(test.get("weight", 0))
        if hit:
            total += w
            kinds.add(test["evidence"])
            if test.get("identity"):
                identity_hits += 1
            matched.append({"test": test.get("id") or test["evidence"],
                            "evidence": test["evidence"], "weight": w,
                            "detail": detail, "note": test.get("note", "")})
        else:
            missing.append({"test": test.get("id") or test["evidence"],
                            "evidence": test["evidence"], "weight": w,
                            "note": test.get("note", "")})

    gate_failed = False
    require_identity = sig.get("require_identity", policy.get("require_identity", True))
    if require_identity and identity_hits == 0:
        gate_failed = True
    score = max(0, min(100, total))
    if gate_failed:
        score = min(score, 30)

    if gate_failed:
        confidence = 0.25 if score >= 30 else 0.15
    elif score >= 70:
        confidence = min(0.99, 0.85 + 0.04 * max(0, min(3, identity_hits - 1)))
    elif score >= 50:
        confidence = 0.6
    elif score >= 30:
        confidence = 0.4
    else:
        confidence = 0.15

    matched.sort(key=lambda x: -abs(x["weight"]))
    missing.sort(key=lambda x: -x["weight"])
    return {
        "id": sig.get("id"),
        "name": sig.get("name"),
        "family": sig.get("family", ""),
        "version": sig.get("version", ""),
        "score": score,
        "raw_score": total,
        "tier": tier_for(score),
        "confidence": round(confidence, 2),
        "identity_hits": identity_hits,
        "gate_failed": gate_failed,
        "evidence_kinds": sorted(kinds),
        "matched": matched,
        "missing": missing[:4],
        "unavailable_tests": unavailable,
        "notes": sig.get("notes", ""),
        "sources": sig.get("sources", []),
    }


def run_match(db: dict, ev: Evidence) -> dict:
    policy = db.get("match_policy") or {}
    results = []
    for sig in db.get("signatures", []):
        r = evaluate(sig, ev, policy)
        if r is None:
            continue
        # 没有任何一条证据命中（全部不可用或全部未命中）的签名不进排名，
        # 避免负对照游戏出现「top=某签名 0 分」的噪声。
        if not r["matched"]:
            continue
        results.append(r)
    results.sort(key=lambda r: (-r["score"], -r["identity_hits"], r["id"]))
    for i, r in enumerate(results, 1):
        r["rank"] = i

    top = results[0] if results else None
    if top and top["score"] >= 70 and top["identity_hits"] > 0:
        verdict = "identified"
    elif top and top["score"] >= 45:
        verdict = "probable"
    elif top and top["score"] >= 25:
        verdict = "weak"
    else:
        verdict = "unknown"
    return {
        "schema": SCHEMA,
        "game": {"name": ev.name, "probe_root": (ev.probe.get("game") or {}).get("root"),
                 "analysis_present": bool(ev.analysis)},
        "detected": {"flavor": ev.flavor or "unknown", "engine": ev.engine or "unknown",
                     "unity_version": ev.unity_version,
                     "axis": {"files": len(ev.files), "dlls": len(ev.dlls), "types": len(ev.types),
                              "methods": len(ev.methods), "fields": len(ev.fields),
                              "tokens": len(ev.tokens), "text_sinks": len(ev.text_sinks)}},
        "verdict": {"top_signature": top["id"] if top else None,
                    "top_name": top["name"] if top else None,
                    "top_score": top["score"] if top else 0,
                    "tier": top["tier"] if top else "D",
                    "confidence": top["confidence"] if top else 0,
                    "state": verdict},
        "signature_ranking": results,
    }


# ------------------------------------------------------------------ batch
def _game_meta(manifest: dict, gid: str) -> dict:
    for g in manifest.get("games", []):
        if g.get("id") == gid:
            return g
    return {}


def batch(db: dict, manifest: dict, out_dir: str, csv_path: str | None) -> list[dict]:
    sys.path.insert(0, HERE)
    import sigprobe  # 同目录

    tokens = [t for t in (db.get("probe_tokens") or []) if isinstance(t, str)]
    probes_dir = os.path.join(out_dir, "probes")
    matches_dir = os.path.join(out_dir, "matches")
    os.makedirs(probes_dir, exist_ok=True)
    os.makedirs(matches_dir, exist_ok=True)
    rows, summaries = [], []
    for g in manifest.get("games", []):
        gid, path = g["id"], g["path"]
        if not os.path.isdir(path):
            summaries.append({"id": gid, "name": g.get("name"), "error": "path missing",
                              "path": path})
            continue
        pr = sigprobe.probe(path, g.get("name"), tokens)
        with open(os.path.join(probes_dir, f"{gid}.json"), "w", encoding="utf-8") as fh:
            json.dump(pr, fh, ensure_ascii=False, indent=2)
        analysis = None
        ap = g.get("analysis")
        if ap:
            ap_abs = ap if os.path.isabs(ap) else os.path.join(os.path.dirname(__file__), ap)
            if os.path.isfile(ap_abs):
                with open(ap_abs, "r", encoding="utf-8") as fh:
                    analysis = json.load(fh)
        facts = None
        if g.get("facts"):
            fp = g["facts"] if os.path.isabs(g["facts"]) else os.path.join(HERE, g["facts"])
            if os.path.isfile(fp):
                with open(fp, "r", encoding="utf-8") as fh:
                    facts = json.load(fh)
        ev = Evidence(pr, analysis, facts, g.get("name"))
        res = run_match(db, ev)
        res["game"].update({"id": gid, "appid": g.get("appid"), "priority": g.get("priority"),
                            "root": path})
        res["expected"] = {"signatures": g.get("expected", []), "note": g.get("note", "")}
        top_id = res["verdict"]["top_signature"]
        if g.get("expect_none"):
            # 负对照：只有判不出任何签名（unknown）才算通过
            res["pass"] = res["verdict"]["state"] == "unknown"
        else:
            res["pass"] = top_id in (g.get("expected") or [])
        with open(os.path.join(matches_dir, f"{gid}.json"), "w", encoding="utf-8") as fh:
            json.dump(res, fh, ensure_ascii=False, indent=2)
        summaries.append({"id": gid, "name": g.get("name"), "priority": g.get("priority"),
                          "flavor": res["detected"]["flavor"],
                          "top": top_id, "score": res["verdict"]["top_score"],
                          "tier": res["verdict"]["tier"], "pass": res["pass"],
                          "expected": ",".join(g.get("expected") or [])})
        if not res["signature_ranking"]:
            # 负对照/未知游戏也留一行，保证 CSV 与 manifest 款数一致
            rows.append({
                "game": g.get("name"), "appid": g.get("appid"), "priority": g.get("priority"),
                "engine": res["detected"]["engine"], "flavor": res["detected"]["flavor"],
                "rank": 0, "signature_id": "", "signature_name": "（无命中）",
                "score": 0, "tier": res["verdict"]["tier"], "identity_hits": 0,
                "matched_tests": 0, "evidence_kinds": "",
                "expected": ",".join(g.get("expected") or []),
                "top1_pass": res["pass"],
                "note": "无任何签名证据命中（negative/unknown）",
            })
        for r in res["signature_ranking"][:3]:
            rows.append({
                "game": g.get("name"), "appid": g.get("appid"), "priority": g.get("priority"),
                "engine": res["detected"]["engine"], "flavor": res["detected"]["flavor"],
                "rank": r["rank"], "signature_id": r["id"], "signature_name": r["name"],
                "score": r["score"], "tier": r["tier"], "identity_hits": r["identity_hits"],
                "matched_tests": len(r["matched"]), "evidence_kinds": "+".join(r["evidence_kinds"]),
                "expected": ",".join(g.get("expected") or []),
                "top1_pass": res["pass"],
                "note": (r["matched"][0]["detail"] if r["matched"] else ""),
            })

    if csv_path:
        os.makedirs(os.path.dirname(os.path.abspath(csv_path)), exist_ok=True)
        with open(csv_path, "w", encoding="utf-8-sig", newline="") as fh:
            w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()) if rows else
                               ["game", "signature_id"])
            w.writeheader()
            w.writerows(rows)
    return summaries


# ------------------------------------------------------------------ main
def main(argv=None):
    ap = argparse.ArgumentParser(description="GalA11y Forge 签名匹配原型 (galforge-match-v1)")
    ap.add_argument("--db", default=DEFAULT_DB, help="signatures.json")
    ap.add_argument("--probe")
    ap.add_argument("--analysis")
    ap.add_argument("--facts")
    ap.add_argument("--game-name")
    ap.add_argument("--out")
    ap.add_argument("--batch", help="验证清单 JSON")
    ap.add_argument("--out-dir", default=os.path.join(HERE, "out"))
    ap.add_argument("--csv")
    args = ap.parse_args(argv)

    if not os.path.isfile(args.db):
        print(f"签名库不存在: {args.db}", file=sys.stderr)
        return 2
    with open(args.db, "r", encoding="utf-8") as fh:
        db = json.load(fh)

    if args.batch:
        with open(args.batch, "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        summaries = batch(db, manifest, args.out_dir, args.csv)
        for s in summaries:
            if s.get("error"):
                print(f"✗ {s['id']}: {s['error']} ({s.get('path')})")
            else:
                flag = "PASS" if s["pass"] else "FAIL"
                print(f"{flag:4} {s['priority'] or '-':3} {s['name'][:28]:28} "
                      f"{s['flavor']:14} {str(s['top']):28} {s['score']:3} {s['tier']}"
                      f"  (expect {s['expected']})")
        return 0

    if not args.probe and not args.analysis and not args.facts:
        print("至少需要 --probe 或 --analysis 之一", file=sys.stderr)
        return 2
    probe = _read_json(args.probe)
    analysis = _read_json(args.analysis)
    facts = _read_json(args.facts)
    ev = Evidence(probe, analysis, facts, args.game_name)
    res = run_match(db, ev)
    text = json.dumps(res, ensure_ascii=False, indent=2)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(text)
        v = res["verdict"]
        print(f"wrote {args.out}: top={v['top_signature']} score={v['top_score']} "
              f"tier={v['tier']} state={v['state']}")
    else:
        print(text)
    return 0


def _read_json(path):
    if not path:
        return None
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


if __name__ == "__main__":
    sys.exit(main())
