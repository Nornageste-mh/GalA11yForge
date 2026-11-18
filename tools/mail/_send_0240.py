import pathlib
import sys
import types

sys.path.insert(0, r"D:\DSHWorkSpace\tools\mail")
import mail163  # noqa: E402

body = pathlib.Path(r"D:\DSHWorkSpace\tools\mail\_body_0240.txt").read_text(encoding="utf-8")
args = types.SimpleNamespace(
    user=mail163.DEFAULT_USER,
    to="385254598@qq.com",
    subject="GalA11y 0.2.4.0 已打包：BepInEx 随包即用，612 测试全绿",
    body=body,
)
raise SystemExit(mail163.cmd_send(args))
