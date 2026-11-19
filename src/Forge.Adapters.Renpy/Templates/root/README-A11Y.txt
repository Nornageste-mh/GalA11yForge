Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） 无障碍覆盖层 · @@GAME_NAME@@
==================================================

补丁版本 : @@PATCH_VERSION@@
生成器   : @@BUILD@@
目标引擎 : Ren'Py @@RENPY_VERSION@@ (Python @@PY_MAJOR@@)
生成时间 : @@DATE@@

这是什么
--------
一组放在 game/ 下的松散 .rpy 文件，启动时被 Ren'Py 自动加载（磁盘优先于 .rpa），
把对话、旁白、选项菜单、界面按钮用读屏/语音念出来。**没有修改游戏任何原文件**，
不包含任何游戏文本或素材，只有一个文本处理层。

安装
----
把本目录里的内容按相同相对路径复制到游戏根目录即可：
    a11y_say.vbs              -> <游戏根>/
    game/a11y_platform/*.rpy  -> <游戏根>/game/a11y_platform/
生成器也可以代劳（只对非 Steam 目录开放）：
    python renpy_a11y_gen.py --game <游戏根> --out <输出目录> --apply

卸载
----
运行 uninstall.ps1：先按 a11y_manifest.json 逐个核对 SHA256，只删登记且未被改动的文件；
再清理 game/a11y_platform 下 Ren'Py 生成的 *.rpyc（.rpy 的编译缓存），目录空了才删目录；
全部清干净时一并删除运行日志 a11y_speech.log、清单 a11y_manifest.json 与 uninstall.ps1 自身。
重复运行是安全的（清单已不存在也返回成功）。
被改动过的文件会保留并提示，此时脚本、清单与目录也保留，方便再跑一次；确认不再需要可加 -Force 强制删除。
脚本只处理 game/a11y_platform 这一层与根目录的补丁文件，不会碰 game/ 下其他 .rpy / .rpyc。
也可以手动删除 game/a11y_platform 整个目录，以及根目录的 a11y_say.vbs、a11y_speech.log、a11y_manifest.json。

按键
----
F5   重读上一句
F8   朗读开/关（不影响游戏本身）
F9   整屏朗读
F10  切换朗读后端（NVDA / SAPI / 日志）
F11  写诊断快照到日志

日志与开关
----------
<游戏根>/a11y_speech.log   运行日志：启动、后端链、每一句、每一次失败原因
<游戏根>/a11y_off.flag     手动放一个同名空文件即可静音（删掉恢复）

可调项（改 game/a11y_platform/*.rpy 后重启生效）
-----------------------------------------------
00_contract.rpy   A11Y_T 里的中文文案
01_speech.rpy     _A11Y_TTS_PREF（auto/nvda/sapi/log）、_A11Y_SAPI_RATE（-10..10）
02_dialogue.rpy   _A11Y_SPEAK_WHEN_VOICED（有配音时是否仍朗读，默认 True）

已知限制
--------
- 极短按键连打时，SAPI 路径每句会起一个 wscript 进程，可能有几十毫秒延迟
  （NVDA 路径是常驻 DLL，没有这个开销）。
- 编号直选（数字键 1-9）本版未实现，只能用方向键 + 回车。
- 游戏若自己改写 config.say_menu_text_filter，本补丁会链式保留其返回值。
