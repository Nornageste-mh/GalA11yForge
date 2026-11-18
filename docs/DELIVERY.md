# 意汉词典（yihan.it）离线版 · 交付说明

> 生成时间：2026-10-19 22:20；**最终刷新：2026-10-21 17:05（Asia/Shanghai，含 Web UI 双 BUG 修复与测试体系）**
> 数据来源：<https://www.yihan.it/>（博大意汉词典 Online，作者 HeartNest）
> 本文件是整套交付的入口。数字为 **站点抓取全部完成后的最终快照**（抓取 108,948 / 109,882 词、6,698 个变位页、0 次 429），如需重建执行 `node tools/finalize.mjs`（见 §8）。

---

## 0. 一句话

把 yihan.it 的全部词条（意→汉 77,135 + 汉→意 45,301，共 122,436 条）、6,698 个动词的完整变位表、站点的词性分类与"放疫词汇"7 类分类，落成 **SQLite / 纯 SQL 分发物 + 全量数据校验报告 + 100 个不规则动词的人工校对修正 + 一个纯离线、WCAG 2.1 AA 的无障碍 Web UI**。

| 需求（原始） | 交付 |
| --- | --- |
| 1. 抓取所有词汇数据、按网站分类、不遗漏词条内容、统一格式、机器可读 SQL | §2 数据规模 + §3.2 SQL + §4 分类落地；`yihan-dict/sqlite/yihan.sqlite`、`yihan.sql`、`yihan.sql.gz` |
| 2. 校对 100 个常用不规则动词变位、修正错误、总结成文档 | §5；`verbs-audit/AUDIT-REPORT.md`、`CORRECTIONS-SUMMARY.md`；修正已写入数据库与 UI |
| 3. 无障碍 Web UI：模糊搜索、显示完整词条、输入已变位词形可反查原型 | §6；`web/`，双击 `web/start.cmd` 即用 |

---

## 1. 交付物清单

| 路径 | 内容 | 说明 |
| --- | --- | --- |
| `web/` | 离线 Web UI（原生 ES module，无第三方运行时依赖） | **主交付**。`web/start.cmd` 一键启动 |
| `web/data/` | Web UI 的静态数据（79.8 MB / 76.1 MiB） | index 482,286 行（34.4 MB，含 6 个补录合成原型）/ 分片 58 个（31.5 MB）/ 中文 45,301 / 动词 6,700（含 6 原型变位）/ 校对表 100 词 |
| `yihan-dict/sqlite/yihan.sqlite` | 成品数据库（221.3 MB，schema v4，13 表 + 5 视图 + 28 索引） | 直接 `sqlite3 yihan.sqlite` 查询 |
| `yihan-dict/sqlite/yihan.sql` | **纯 SQL 分发物**（137.0 MB） | schema + 全部 INSERT + 索引重建，可直接 `sqlite3 new.db < yihan.sql` |
| `yihan-dict/sqlite/yihan.sql.gz` | 上者 gzip（25.2 MB） | 分发用 |
| `yihan-dict/sql/schema.sql` | 建表 DDL（含枚举种子、视图、索引） | 二次开发/评审用 |
| `yihan-dict/tools/` | 生产管线：`finalize.mjs`（一键 6 步）、`build_db.mjs`、`validate.mjs`、`coverage.mjs`、`export_web.mjs`、`export_sql.mjs`、`lib/parse*.mjs` | 见 `tools/README.md`（含 schema v4 与导出统计） |
| `yihan-dict/build/` | 中间层 JSONL（entries/senses/examples/forms/conj/covid/ld2） | 可审计、可重放 |
| `yihan-dict/raw/` | 抓取原始层（entries.jsonl、conj/*.html+json、class/covid.jsonl） | 原始证据 |
| `verbs-audit/AUDIT-REPORT.md` | **100 动词变位校对报告**（33 KB，12 节，含逐条错误清单与依据 URL） | 需求 2 主文档 |
| `verbs-audit/CORRECTIONS-SUMMARY.md` | 1–2 页中文摘要（结论/数字/错误分类/修正方法/局限） | 先看这个 |
| `verbs-audit/corrections.json` | 183 条修正（175 格级 + 8 整表级），已 100% 应用到 DB | 机器可读 |
| `verbs-audit/verbs-corrected.json` | 100 词 × 9 时态 × 6 人称的校对后全表（5,400 格 0 空） | UI 展示 + DB 整表替换 |
| `verbs-audit/variants.json`、`MANUAL-OVERRIDES.md`、`SPOTCHECK.md` | 285 条可接受变体、8 个整表污染词逐词依据、60 格外部抽样 | 复核用 |
| `yihan-dict/logs/validate-report.md` | 全量数据校验报告（14 节，✅ 44 通过 / 2 告警 / 0 失败） | 质量证据 |
| `yihan-dict/logs/coverage-report.md` | 抓取覆盖/完整度报告（对 Lingoes 官方词表逐词对标） | 完整性证据 |
| `docs/SCRAPING-METHOD.md` | 抓取方法与限流实测、双源策略、复现步骤、合规说明 | 需求 1 方法学 |
| `web/README.md` | Web UI 使用/架构/契约/无障碍对照表/自测结果 | 需求 3 文档 |
| `web/sample/e2e-real.mjs`、`e2e-report-real.md` | 真实数据端到端 + axe-core 无障碍测试（jsdom） | 自动验证 |

---

## 2. 数据规模（抓取完成后的最终快照）

| 表 | 行数 | 说明 |
| --- | ---: | --- |
| `entries` | **122,436** | 意→汉 77,135 / 汉→意 45,301（其中站点共现 109,691、站点独有 12,536、Lingoes 独有 209；变位形词条 17,493） |
| `entry_pos` | 54,080 | 词性标签（38 种枚举，见 §4） |
| `senses` | 313,191 | 义项（按词性/编号拆分） |
| `examples` | 125,107 | 意汉对照例句 |
| `zh_index` | 77,645 | 中文关键词索引（中文→意语反查） |
| `verbs` | 6,761 | 动词原形 |
| `conj_tenses` | 80,328 | 6,698 动词 × 12 时态（站点 9 简单时态 + 复合时态派生） |
| `conj_forms` | 381,163 | 变位形（含 886 格人工校对修正） |
| `form_index` | 344,637 | 变位形→原型反查索引（conj 336,908 / Lingoes orphan 4,733 / 站点异体形 2,956 / 旧形 40） |
| `conj_corrections` | 183 | 校对修正记录（applied=183） |
| `covid_category` / `covid_entry` | 7 / 180 | 站点"放疫词汇"分类与词条 |

**完整性对标**：站点词库的原始来源是 Lingoes 平台上的《Italian-Chinese & Chinese-Italian Dictionary》（作者夏江，v2.7，MD5 `752A32DD0D3DABE7B6FF9F3B854C565C`，官方明示 65,063 意汉 + 44,840 汉意）。我们已下载该原始词库（8,183,248 字节，MD5 已校验）作为**完整性基准与正文补充源**，并逐词比对：

| 维度 | 覆盖率 | 说明 |
| --- | ---: | --- |
| 意大利语词条 | **99.17%**（64,415 / 64,954）；**仅站点抓取即 98.98%** | 站点抓取（原生 id）+ Lingoes 补 209 条 |
| 中文词条 | **100%**（44,928 / 44,928）；**仅站点 99.74%** | 站点 2012 年后无新增中文词条 |
| 动词变位页 | **6,698 / 6,698 动词**（9 时态齐全） | 每表 6 人称；抓取页 100% 成功 |
| 变位词形 | 93.21%（246,088 / 264,025） | 差额为站内检索不到的多词短语，非真实缺失（`logs/coverage-missing-forms.txt`） |
| 抓取命中率 | 99.29%（108,948 / 109,882 词） | 未命中 182 条为 `?`、`2`、`4WD` 等纯数字/符号/夹用词，站点确无此词条（`logs/notfound.jsonl`） |
| 新冠词汇 | 180 / 180（7 类） | 站点唯一 2012 年后的增量内容 |

> 为什么不全靠爬站点？站点 `autocomplete` 接口硬上限 12 条、无索引页、无 sitemap，且 Aruba 有 DDoS 限流（实测安全速率约 3 req/s，超过即 429 封 IP 约 20 分钟）。完整方法学见 `docs/SCRAPING-METHOD.md`。

---

## 3. 怎么用

### 3.1 Web UI（推荐，一条命令）

```bat
D:\DSHWorkSpace\web\start.cmd
```

或手动：

```bash
cd D:/DSHWorkSpace/web
node server.mjs            # 打开 http://127.0.0.1:8000/
# 也可 --port 8080 / --no-open；无 Node 时用 python -m http.server 8000 --bind 127.0.0.1
```

> 必须走 HTTP：UI 用 `fetch` 读 JSON，`file://` 会被浏览器安全策略拦截。

能做什么：
- 输入 `vedere` / `ved`（前缀）/ `vedre`（容错）/ `学生`（中文）→ 结果列表完整显示全部命中，分页「第 X / P 页 · 共 N 条」，aria-live 播报；
- 输入**已变位词形**（如 `andato`、`farà`、`sappiate`）→ 直接反查到原型、时态、人称，原型可点击跳转；
- 词条页展示完整释义（HTML 清洗后）、词性、例句、中文索引词，动词展示 9 时态 × 6 人称变位表；100 个校对动词额外显示「已按校对表修正 N 处，补齐 M 格」与来源，站点异写以「（站点写法：veggo）」小字保留。

### 3.2 SQL

```bash
# 方式 A：直接用成品库
sqlite3 D:/DSHWorkSpace/yihan-dict/sqlite/yihan.sqlite
# 方式 B：从纯 SQL 重建（交付给第三方时只需这一个文件）
sqlite3 mydict.db < D:/DSHWorkSpace/yihan-dict/sqlite/yihan.sql
```

常用查询：

```sql
-- 词条全文（视图：词条 + 词性 + 首义项 + 释义 + 义项数/例句数）
SELECT * FROM v_entry_full WHERE lemma = 'vedere';

-- 变位形反查原型（视图：形 → 原型/时态/人称/来源）
SELECT * FROM v_form_lookup WHERE form = 'farà';

-- 按站点词性统计（视图：词性分类分布，含站点/合并来源拆分）
SELECT pos, pos_zh, pos_kind, site, site_ld2, ld2, total FROM v_pos_stats ORDER BY total DESC;

-- 新冠词汇按 7 类列出
SELECT no, category_name, zh, it FROM v_covid ORDER BY no;
```

---

## 4. 网站分类如何落地

站点的"分类"体现在三个维度，全部进库：

1. **语言方向**：`entries.kind` = `it`（意→汉，77,135，含 17,493 条变位形词条）/`zh`（汉→意，45,301）。
2. **词性**（站点侧栏与使用说明里的 15 种标签 + 扩展）：`pos_enum` 38 行枚举（含 `kind` 与中文名），`entry_pos` 逐词条挂接；覆盖：意大利语单词词条 82.7% 有词性标签，多词短语按站点惯例无标签（99.3%）。
3. **主题分类**：站点「词典 / 放疫词汇」180 条按 7 类（疫病名称 10 / 传染防控 68 / 政策举措 29 / 机构职业群体和场所名称 16 / 病理症状 21 / 器具名称 13 / 其他医学名词 23）→ `covid_category` + `covid_entry`（外键、序号 1–180 连续）。

"不遗漏词条内容"的落地：每个词条的原始释义 HTML 全文保留（`entries.def_html`），并结构化为 `senses`（义项）、`examples`（意汉对照例句）、`entry_pos`（词性）、`zh_index`（中文关键词），另保留 `raw` 证据层可回溯。

---

## 5. 100 个不规则动词变位校对（需求 2）

**结论：站点变位表整体质量不错（硬错误率 0.27%），但存在系统性问题，已全部修正。**

| 指标 | 数值 |
| --- | ---: |
| 校对动词 | 100 个（全部为真不规则动词，含 essere/avere/fare/dire/andare/potere/volere/dovere/sapere/stare/venire/porre/trarre/tradurre…） |
| 逐格比对格数 | 5,244（92 词 × 57 格；8 词整表不可用） |
| 与权威表一致 | 4,784（91.23%） |
| **硬错误** | **14 格（0.27%）** |
| 缺形式（被变体形挤出表外） | 161 格（3.07%） |
| 差异合计 | 3.34% |
| 产出修正 | **183 条**（175 格级 + 8 整表级），已 100% 写入 DB（886 格 `corrected=1`） |
| 可接受变体（不计错） | 285 条（37 词） |

典型错误（报告 §8 有逐条依据 URL）：
- `sapere` 命令式 voi：站点 `sapete` → 正确 `sappiate`
- `cuocere` 过去分词：站点 `cociuto` → 正确 `cotto`
- `produrre/tradurre/condurre` 副动词/现在分词：站点 `produnendo` → 正确 `producendo`
- `sedersi` 近过去时：站点 `mi ho seduto` → 正确 `mi sono seduto`
- **8 个整表污染**（accendere/chiudere/decidere/dovere/prendere/scegliere/togliere 被渲染成反身变位 `mi prendo…`；accorgersi 整表丢反身代词）→ 整表替换，依据见 `MANUAL-OVERRIDES.md`

依据与复核：Treccani、Wiktionary it、coniugazione.it、italian-verbs.com；8 个整表词与独立源逐格交叉验证 53/54、48/54 一致，20 词 × 3 格外部抽样 0 冲突（`SPOTCHECK.md`）。

---

## 6. 无障碍（需求 3）

- 目标级别 **WCAG 2.1 AA**；`axe-core` 自动检查（搜索页 28 条规则 / 详情页 27 条规则，**0 违规**）。
- 语义结构：`header/nav/main` + 跳过链接、单一 `h1` 层级、`role="search"`、`aria-live` 状态播报（结果数/翻页/加载进度）、结果列表 `role="listbox"` 语义与键盘路径（↑↓ Home End Enter Esc Tab）。
- 对比度：亮色最低 6.79:1、暗色 8.98:1（AA 要求 4.5:1）；支持 `prefers-reduced-motion`、`forced-colors`、200% 缩放、纯键盘与读屏标签。
- 加载 48 万行索引时分块执行（12,000 行/段，最大主线程阻塞 106 ms）+ 进度条 role="progressbar"，不冻结读屏。
- **真实全量数据端到端验证**（`web/sample/e2e-real.mjs`，14 用例）：索引 482,280 行加载、前缀/容错/变位形反查/动词 9 表/中文词条/深链刷新/纯键盘全路径/axe 搜索页 28 规则与详情页 27 规则 0 违规；报告 `web/sample/e2e-report-real.md`。
- 已知限制：自动化验证用 jsdom + axe-core 完成（四层 66 用例全绿，见 `web/tests/TEST-REPORT.md`），**未做真机 NVDA/VoiceOver 验证**。本机沙箱对旧式 Chromium 启动（命名管道）确有 `EPERM`，但 CDP 方式（`--remote-debugging-port` + WebSocket）已实测可启动 Chrome 155 截图，像素级走查可在此基础上扩展。

---

## 7. 抓取方法与合规

- 限速 3.8 req/s 抓完整个站点：106,306 次请求、26.4 MB、**0 次 429**（实测 429 阈值：>6 req/s 约 2.5 分钟封 IP 约 20 分钟；≤3.8 req/s 长期安全）。
- 遵守 robots 语义（站点无 robots.txt，未抓取登录/后台/反馈接口），仅读取公开词条页与两个公开 ajax 接口；UA 标识为普通浏览器；无并发轰炸。
- 双源策略：站点抓取（原始证据）+ Lingoes 官方同源词库（完整性基准，MD5 校验）。详见 `docs/SCRAPING-METHOD.md`。

---

## 8. 完整复现

```bash
cd D:/DSHWorkSpace/yihan-dict
node tools/finalize.mjs          # 一键 6 步：快照 → build_db → validate → coverage → export_web → export_sql
# 产物：sqlite/yihan.sqlite、yihan.sql(.gz)、web/data/*、logs/*.md
```

抓取（可选，需联网）：

```bash
cd D:/DSHWorkSpace/scrape
node crawl.mjs --rate 3.8 --conc 6 --noenum --workers 5 \
  --list D:/DSHWorkSpace/data/list_all.txt \
  --conjlist D:/DSHWorkSpace/data/ld2_verbs_forms.jsonl
# 断点续跑；日志 yihan-dict/logs/crawl.log；抓完再跑一次 finalize.mjs
```

---

## 9. 已知限制

1. **站点抓取已完成**（108,948 / 109,882 词，用时 7.8 小时，0 次 429）。未命中的 182 条为纯数字/符号/夹用词（`?`、`2`、`4WD`、`a'jour` 等），站点确无对应词条。**所有内容均已 100% 入本地库**。
2. 站点不提供复合时态（先将来时、条件式过去时、虚拟式过去时/愈过去时、远愈过去时）与不定式表；DB 中这 6 类由派生/空缺表示，未臆造。
3. 非校对动词（6,568 个）仍有站点原始缺格 3,069 个（变体形溢出导致），如实记录在 `meta.conj_missing_cells`，未强行补全。
4. `load_sqlite.py` 是 v3 时代 Python 回退路径，v4 未完全对齐；**生产路径仅支持 Node >= 22**（工具 README 顶部有警告）。
5. Web UI 索引 48 万行、34.4 MB，首次加载 1–4 秒（进度条 role="progressbar" + aria-live 提示；jsdom 实测 3.8 s）；>60–80 万条时建议按已设计的分片方案改造。

---

## 10. 报告索引

| 想了解 | 看这个 |
| --- | --- |
| 数据质量（44 项检查） | `yihan-dict/logs/validate-report.md` |
| 抓了多少、缺什么 | `yihan-dict/logs/coverage-report.md` + `coverage.json` |
| 100 动词错了哪些、怎么改的 | `verbs-audit/AUDIT-REPORT.md`（先看 `CORRECTIONS-SUMMARY.md`） |
| 抓取方法、限流实测、双源论证 | `docs/SCRAPING-METHOD.md` |
| 每条抓取明细/未命中清单 | `yihan-dict/logs/crawl.log`、`logs/notfound.jsonl` |
| Web UI 架构/数据契约/无障碍对照 | `web/README.md` |
| 管线命令与 schema v4 | `yihan-dict/tools/README.md` |
| 发布步骤与耗时 | `yihan-dict/logs/release-stats.json` |
