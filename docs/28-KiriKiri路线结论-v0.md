# docs/28 KiriKiri 路线结论（R23 之后）v0

- 日期：2026-11-15
- 依据：`research/36-kirkiri-verify/REPORT.md`（task-44，186 行）、`research/34-kirkiri-design/REPORT.md`（task-41）、`src/experiments/kirkiri-tpm-probe/`
- 状态：R23 完成；task-45（DLL 代理实验）进行中

## 一、确定结论（[实测]）

1. **官方 krkrz 1.4.0.8 + KAG3 3.32（x86）**：`plugin\*.tpm` 会自动加载，V2Link 早于 startup.tjs，TPM 原生方法可被 TJS 调用。同一探针 1.7 秒写出 marker。
2. **SenrenBanka 1.2.0.3（x86，Steam DRM）不加载 tpm**：4 次有效启动（plugin 91s / 32 位模块表 80s / exe 根 51s / 直启约 60s）marker 全无；32 位模块表 111 项无该模块；阳性对照已排除探针问题。
3. exe 里确有 `*.tpm`、`(info) Loading ` 字符串，但运行时不生效（推断：启动流程未调用 `TVPLoadPlugins` 的 tpm 分支）。
4. 本作对插件 DLL **无 .sig 强制**（`a11y.dll` 无 `.sig` 仍加载）；运行时模块表里有 20+ 个 `plugin\*.dll`。
5. 回滚记录：plugin 59 文件/9,100,777B 逐文件 SHA256 与实验前一致；根 33 文件一致；探针/marker 已删；0 残留进程。

## 二、对产品的含义

- **KiriKiri Z 旧商业作（1.2.x）不能靠“丢 tpm”免改 data.xp3**。v1 产品对这类作品只能：
  - 识别引擎 + 明确告诉用户“此作需要替换 data.xp3 或代理 DLL，自动安装未验证”；
  - 不作为“一键自动”承诺。
- 用户当前 SenrenBanka 0.2.0.1 补丁是 data.xp3 注入（可运行但分发包含版权资源风险）——不是产品分发方案，只是用户自用。
- 新作 krkrz ≥2.5.0 有官方 a11y API（R4b 结论），那是另一条路（需作品脚本 opt-in），不在本版范围。

## 三、替代注入点（按优先级）

| 路线 | 做法 | 状态 | 风险 |
|---|---|---|---|
| A. DLL 代理/替换 | 备份后替换一个 `Plugins.link` 已点名的 `plugin\*.dll`（如 `getSample.dll`/`menu.dll`），代理里转发原 `V2Link` 再挂钩 | **task-45 正在做受控实验** | 需改 1 个游戏文件；Steam 更新覆盖；杀软敏感；目标 dll 选错可能影响游戏功能 |
| B. 新增小 xp3 覆盖 startup.tjs | 放一个只含脚本的小 `*.xp3`，看是否自动挂载并覆盖同名文件 | 未实测（约 15 分钟可验） | 需确认 1.2.0.3 是否自动扫 `*.xp3`；覆盖顺序未知 |
| C. 改/替换 data.xp3 | 用户当前方案 | 技术上可行 | 分发含 1GB 游戏资源，违反 AGENTS.md §5，不作为产品方案 |
| D. 其他注入点 | `system\` 目录、随机新 dll 自动加载 | 无证据/不值得试 | — |

## 三·五、R24 补充：DLL 代理注入成立（2026-11-15，[实测]）

- task-45 受控实验：把 `plugin\getSample.dll` 换成 87KB x86 代理、原件改名 `getSample_orig.dll`；Steam 启动后 **2.4 秒** 代理 `V2Link` 被调用，转发同一 `exporter` 给原件，marker 记 `load=1/lasterr=0/origv2link=1/ret=S_OK`；32 位模块表里 `getSample.dll` 与 `getSample_orig.dll` 同时存在；pid 31356 标题「千戀＊萬花」、Responding、完整主菜单截图；功能无损。
- **`.sig` 不构成阻碍**：`plugin\` 下 26 个 dll 各带 212B `-- SIGNATURE - SHA256/PSS/RSA --` 块；整个替换后签名必然失配，引擎仍照常加载 ⇒ 免改 `data.xp3` 的代码入口已打通，无需伪造签名。
- 关键设计事实：转发的是同一个 exporter 指针，原件注册 TJS 类语义不变；引擎不检查 `V2Link` 返回值（`PluginImpl.cpp:313-316`），失败静默 —— 判定必须靠 marker + 模块表双证。
- 风险（均未实测）：单次启动、只到主菜单；只验证无功能依赖的样例插件；`menu.dll`/`textrender.dll`（承载功能+有.sig）未测；Steam 更新会覆盖、杀软敏感度未知。
- 证据：`research/38-kirkiri-proxy/REPORT.md`（259 行）+ evidence 24 文件；源码 `src/experiments/kirkiri-dll-proxy/`。
- 下一格（task-46）：① 代理内用同一 exporter 真注册一个 TJS 类（证明拿到脚本层，产品路线关键一步）；② 隔离实验 `menu.dll`；③ 写“替换+备份+回滚+自检”安装器原型。

## 三·六、R25 补充：TJS 脚本层已打通、menu.dll 代理可用、安装器原型通过（2026-11-15，[实测]）

- TJS 已打通：代理 V2Link 内经 exporter 拿到 6 个 TVP/TJS 函数（`TVPQueryFunctionsByNarrowString` 全 ok，`global` 指针 0x069CC158），写/读/自增/class 定义/实例化+方法/try-catch 共 6 组全过（pass=6/6），进程稳定到主菜单。首版 class 崩溃 0xC0000409 由 `/EHsc`→`/EHa` + `TVPExecuteScript` 修复。
- menu.dll 代理可用：V2Link 转发成功、代理与原件双载、主菜单完整渲染可交互、Responding，且与 `a11y.dll` 共存；但未进游戏一局（2 次启动已用满）。
- 回滚干净：plugin 59 文件 + 根目录 33 文件逐文件 SHA256 0 差异，无残留进程。
- 安装器原型：`tools/kkz-proxy/install.ps1`、`uninstall.ps1` 沙箱 T1–T10 全过（PS5.1、篡改拒绝、运行中守卫、dry-run），真实游戏目录未跑。
- 产品建议（v1）：采用"早期插件代理 + 脚本级 class"；即代理 DLL 在 V2Link 注册一个 TJS 类/全局函数，TJS 侧可挂钩 `MessageLayer.processCh` 或把台词交给代理的原生出口；安装器先 DryRun 再真实执行。
- 证据：`research/39-kirkiri-proxy2/REPORT.md`（346 行）+ evidence（marker-run4.txt、installer-test.log、menu2-2-afterclick2.png、plugin-before/after.csv、modules-*.log）。
- 风险：未进游戏局；原生类注册 `TVPRegisterNativeClass` 未实测；Steam 更新后回滚会按哈希拒绝、需按 manifest 人工还原；杀软未专项验证。

## 四、下一步

1. 等 task-45 结论：
   - 若 A 可行 → 评估“代理 menu.dll/textrender.dll”的可行性与风险，再决定 KKZ 适配器是否支持“DLL 代理安装”，并写入打回预案（用户要求“万能”但 KKZ 需诚实标注）。
   - 若 A 不可行 → KKZ 产品线只做“识别 + 人工待办 + 研究结论”，不承诺自动补丁；把精力放到 Mono/RenPy/IL2CPP。
2. B 路线若 A 失败再做 15 分钟实验。
3. 更新 docs/22 打回预案「未知程序/新引擎」与用户话术：KiriKiri 旧作不承诺一键。

## 五、证据索引

- `research/36-kirkiri-verify/REPORT.md` + evidence（阳性对照、4 次启动、模块表、回滚 diff）
- `research/34-kirkiri-design/REPORT.md`（1.4.0.8 最小闭环）
- `src/experiments/kirkiri-tpm-probe/`（探针源码与构建脚本）
- task-45：`research/38-kirkiri-proxy/`（进行中）

---

## 2026-11-15 02:25 R26 最小朗读闭环结论（task-47，《千恋＊万花》1.2.0.3）

- **能进局**：getSample.dll 代理在场时，Steam 启动 → 主菜单 → 点「新遊戲」→ CHAPTER 1-1 正常；代理用 message-only 窗口 + SetTimer(400ms) 在引擎主线程持续工作。
- **能拿到真实台词**：读取 global.kag.historyLayer.currentInfo.plaintext，一次进局拿到 8 行日文原文（「お客さんってば」、「あっ、はい。なんですか？」、飛び起きた俺は、前に座っている運転手に返事をする。 等）；第 8 行与截图画面逐字一致。
- **但只有日文原文**：.disp（说话人）与 .text（预期译文）整轮为空；画面上的中文译文目前取不到 → 这是 KKZ 适配器 v1 的头号缺口。
- **逐字中间态**：轮询会看到重复行/空行（逐字显示造成），v1 必须做稳定化（同句 ~150–250ms 不再变化再朗读）+ 去重 + 最小长度。
- **TTS 可行（外部）**：用捕获的真实台词做外部 SAPI 合成得到 153,672B WAV（约 6 秒）。进程内 SAPI 已编译并稳定加载，但未实际发声 [未验证]。
- **回滚干净**：plugin 59 文件 + 根 33 文件逐文件 SHA256 与实验前全等；savedata 未变；临时文件 0 残留。
- **硬约束**：TJS 侧 
- **硬约束**：TJS 侧 new Object() 触发 0xC0000409（/GS fail-fast）；typeof(对象) 抛异常；状态机/缓冲/落盘必须放原生侧。全屏→窗口切换后合成输入（mouse_event/PostMessage/keybd_event）全部失效，只影响自动回归，不影响产品。

**对产品的含义**：KKZ v1 只能定位为「实验性/开发者预览」，不能承诺一键；取文本设计=plaintext + 200ms 稳定化 + 原生侧状态机 + 进程外朗读（代理只落盘/IPC）；安装器必须带哈希校验与回滚。下一步两条并行：R27 定位中文译文源并补 8 行原件证据（task-48），W2-Y 代理模板工程化与安装/回滚设计（task-49）。
## 2026-11-15 02:52 R28 适配器设计结论（task-49）

- 交付 docs/35-KKZ适配器设计-v0.md + src/experiments/kkz-adapter/（参数化代理模板、build.ps1、install/uninstall 脚本、README）；沙箱假目录全流程通过（dry-run/哈希门/拒绝重复安装/回滚哈希复原），**真实游戏目录未安装**。
- 核心设计：只替换白名单插件（默认 getSample.dll），原 DLL 改名 _orig 保留；三闸（文件名/宿主版本/原 DLL 哈希）任一失败就"降级不注入"，游戏表现与没装一样；清单写 %LOCALAPPDATA%\kkz-adapter\，回滚前先验证"要删的确实是我们装的"，避免 Steam 校验覆盖后误删原文件。
- 结论：**不进 0.2.4 主线、不发可执行成品**；只随文档作为"开发者预览"。阻塞项=真实机器闭环（安装→取台词→回滚→哈希复原）未做，需 Lead/用户授权后才能进行。
- 坑清单（模板已规避）：TJS 注入对象 → 0xC0000409；typeof(对象) 抛异常；wsprintfW 无格式符静默丢值；V2Link 内过早求值取空；.def 含中文注释导致 LNK4017 静默丢导出。

## R28 定稿补充（task-49，2026-11-15 03:01）

- 代理模板已工程化并可复现构建：x86、三导出（V2Link/V2Unlink/GetModuleThreadModel）、V2Link 转发原 DLL + 只读轮询 TJS；状态机/稳定化/落盘/TTS 均在原生侧。
- 参数化：`build.ps1` 编译期默认 + `kkz-adapter.ini` 运行期覆盖（目标 DLL/后缀/轮询间隔/输出/TTS/版本门/哈希门/FailMode）。
- 可复现构建：link `/Brepro`，同源码两次构建哈希一致；dist SHA256 `31C2661BB0112E35EEBBEE090CBD0213E1502942FE4904FF99CC828E91208225`（TTS 版 `B65A433A…`）。
- 新踩两坑：`.def` 含中文注释 → link 报 LNK4017 并静默丢弃全部导出（产物“构建成功”却无效）；`cl` 的 `/D` 值含 `%` → D8038（改生成配置头）。
- 沙箱 install/uninstall T1–T10 全过（默认 dry-run、哈希不符拒绝、重复安装拒绝、回滚后哈希复原）。
- **决定：0.2.4 不纳入主线、不随包发可执行资产**（真实游戏目录零验证）；源码作为“开发者预览”留在 `src/experiments/kkz-adapter`，待用户授权真机闭环（装→取词→回滚→哈希复原）后再评估是否需要 extras。
