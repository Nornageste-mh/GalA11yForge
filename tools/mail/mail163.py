#!/usr/bin/env python3
"""163 邮箱收发工具（GalA11y Forge 项目通信通道）。

用法:
  python mail163.py send --to 385254598@qq.com --subject "主题" --body "正文"
  python mail163.py check [--limit 10] [--mark-seen] [--all]
  python mail163.py check --json

授权码从环境变量 MAIL163_PASS 读取；缺省时回退读取 D:\\DSHWorkSpace\\k.txt.txt 的 163 段。
账号固定 18357873105@163.com（可用 --user 覆盖）。
"""
import argparse
import email
import email.header
import email.utils
import imaplib
import json
import os
import re
import smtplib
import sys
from email.message import EmailMessage

DEFAULT_USER = "18357873105@163.com"
KEY_FILE = r"D:\DSHWorkSpace\k.txt.txt"


def read_pass():
    pw = os.environ.get("MAIL163_PASS")
    if pw:
        return pw.strip()
    try:
        with open(KEY_FILE, "r", encoding="utf-8-sig", errors="ignore") as f:
            lines = [x.strip() for x in f.read().splitlines()]
        for i, line in enumerate(lines):
            if line.lower() == "163" and i + 1 < len(lines):
                return lines[i + 1].strip()
    except OSError:
        pass
    return None


def decode_hdr(value):
    if not value:
        return ""
    parts = []
    for chunk, enc in email.header.decode_header(value):
        if isinstance(chunk, bytes):
            try:
                parts.append(chunk.decode(enc or "utf-8", errors="replace"))
            except (LookupError, UnicodeDecodeError):
                parts.append(chunk.decode("utf-8", errors="replace"))
        else:
            parts.append(chunk)
    return "".join(parts).strip()


def body_text(msg):
    if msg.is_multipart():
        for part in msg.walk():
            ctype = part.get_content_type()
            disp = str(part.get("Content-Disposition") or "")
            if ctype == "text/plain" and "attachment" not in disp:
                payload = part.get_payload(decode=True) or b""
                charset = part.get_content_charset() or "utf-8"
                try:
                    return payload.decode(charset, errors="replace")
                except LookupError:
                    return payload.decode("utf-8", errors="replace")
        return ""
    payload = msg.get_payload(decode=True) or b""
    charset = msg.get_content_charset() or "utf-8"
    try:
        return payload.decode(charset, errors="replace")
    except LookupError:
        return payload.decode("utf-8", errors="replace")


def cmd_send(args):
    pw = read_pass()
    if not pw:
        print(json.dumps({"ok": False, "error": "缺少授权码（MAIL163_PASS 或 k.txt.txt 163 段）"}, ensure_ascii=False))
        return 2
    body = args.body
    if getattr(args, "body_file", None):
        with open(args.body_file, "r", encoding="utf-8-sig") as fh:
            body = fh.read()
    if not body:
        print(json.dumps({"ok": False, "error": "正文为空：需要 --body 或 --body-file"}, ensure_ascii=False))
        return 2
    msg = EmailMessage()
    msg["From"] = args.user
    msg["To"] = args.to
    msg["Subject"] = args.subject
    msg["Date"] = email.utils.formatdate(localtime=True)
    msg.set_content(body, charset="utf-8")
    try:
        with smtplib.SMTP_SSL("smtp.163.com", 465, timeout=30) as s:
            s.login(args.user, pw)
            s.send_message(msg)
    except Exception as exc:  # noqa: BLE001
        print(json.dumps({"ok": False, "error": f"{type(exc).__name__}: {exc}"}, ensure_ascii=False))
        return 3
    print(json.dumps({"ok": True, "to": args.to, "subject": args.subject}, ensure_ascii=False))
    return 0


def imap_connect(user, pw):
    # 163 要求登录前先发 IMAP ID，否则报 Unsafe Login；imaplib 默认不认识 ID 命令。
    imaplib.Commands.setdefault("ID", ("NONAUTH", "AUTH", "SELECTED"))
    conn = imaplib.IMAP4_SSL("imap.163.com", 993, timeout=30)
    try:
        conn._simple_command(
            "ID",
            '("name" "GalA11yForge" "version" "0.1" "vendor" "dsh" '
            '"support-email" "18357873105@163.com")',
        )
    except Exception:  # noqa: BLE001
        pass
    conn.login(user, pw)
    return conn


def cmd_check(args):
    pw = read_pass()
    if not pw:
        print(json.dumps({"ok": False, "error": "缺少授权码"}, ensure_ascii=False))
        return 2
    try:
        conn = imap_connect(args.user, pw)
    except Exception as exc:  # noqa: BLE001
        print(json.dumps({"ok": False, "error": f"{type(exc).__name__}: {exc}"}, ensure_ascii=False))
        return 3
    out = {"ok": True, "messages": []}
    try:
        typ, _ = conn.select("INBOX")
        if typ != "OK":
            print(json.dumps({"ok": False, "error": f"SELECT INBOX 失败: {typ}"}, ensure_ascii=False))
            return 3
        criteria = "ALL" if args.all else "UNSEEN"
        typ, data = conn.search(None, criteria)
        ids = data[0].split() if data and data[0] else []
        if args.limit and len(ids) > args.limit:
            ids = ids[-args.limit:]
        for mid in ids:
            typ, raw = conn.fetch(mid, "(BODY.PEEK[])")
            if typ != "OK" or not raw or not isinstance(raw[0], tuple):
                continue
            msg = email.message_from_bytes(raw[0][1])
            item = {
                "seq": mid.decode(),
                "from": decode_hdr(msg.get("From")),
                "to": decode_hdr(msg.get("To")),
                "subject": decode_hdr(msg.get("Subject")),
                "date": decode_hdr(msg.get("Date")),
                "message_id": (msg.get("Message-ID") or "").strip(),
                "body": body_text(msg).strip()[:4000],
            }
            out["messages"].append(item)
            if args.mark_seen:
                conn.store(mid, "+FLAGS", "(\\Seen)")
    finally:
        try:
            conn.logout()
        except Exception:  # noqa: BLE001
            pass
    if args.json:
        print(json.dumps(out, ensure_ascii=False, indent=2))
    else:
        for m in out["messages"]:
            print(f"[{m['date']}] {m['from']}\n  主题: {m['subject']}\n  正文: {m['body'][:300]}\n")
        if not out["messages"]:
            print("（无新邮件）")
    return 0


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--user", default=DEFAULT_USER)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sp = sub.add_parser("send")
    sp.add_argument("--to", required=True)
    sp.add_argument("--subject", required=True)
    sp.add_argument("--body", required=False, default=None)
    sp.add_argument(
        "--body-file",
        default=None,
        help="从 UTF-8 文本文件读取正文（与 --body 二选一；长正文建议用这个，避免命令行转义问题）",
    )
    sp.set_defaults(func=cmd_send)
    cp = sub.add_parser("check")
    cp.add_argument("--limit", type=int, default=10)
    cp.add_argument("--mark-seen", action="store_true")
    cp.add_argument("--all", action="store_true")
    cp.add_argument("--json", action="store_true")
    cp.set_defaults(func=cmd_check)
    args = ap.parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
