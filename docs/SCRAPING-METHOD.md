# 数据获取方法学（SCRAPING METHOD）

本文件记录 `博大意汉词典 Online`（https://www.yihan.it/）词库的获取过程、数据来源、可重复步骤与限制，供审计与复现。

## 1. 目标与范围

抓取范围（与站点实际可抓内容一致）：

| 类别 | 站点对应 | 条数（权威基准） |
|---|---|---|
| 意汉词条（意大利语 → 中文） | 首页搜索返回的词条 | 64,970 |
| 汉意词条（中文 → 意大利语） | 同一部词典的反向词条 | 44,928 |
| 动词变位表 | `/动词变位/<动词>` 页面 | 6,658 个动词 × 9 简单时态 × 6 人称 + 3 个分词形式 |
| 站内分类：词性 | 使用说明页说明的 s.m./s.f./v.tr./… 共 18 类 | 由词条释义首行解析 |
| 站内分类：新冠词汇 | `词典/放疫词汇` 页 | 180 条 / 7 类 |

站点**没有**的：复合时态（先将来时、远愈过去时、条件式过去时、虚拟式过去时/愈过去时、不定式）——其变位页只提供 9 个简单时态 + 副动词/现在分词/过去分词，因此不校对、不虚构。

## 2. 双源策略（关键）

站点的词库并非原创：站点 2013 年上线时导入的正是 **Lingoes 平台上的《Italian-Chinese & Chinese-Italian Dictionary》v2.7**（作者 夏江 / xiapaolo@126.com，2007-2012，官网统计 65,063 意汉 + 44,840 汉意 = 109,903 条）。因此采用双源：

- **源 A：站点实时抓取**（权威性最高，含站点 id、动词 conj id、站点渲染后的释义 HTML）
  - `POST /ajax/findword`（body: `userid=&word=<urlencoded>`）→ JSON 数组 `[{id,w,s,c,feed,conj?}]`
  - `GET /动词变位/<动词>` → 变位表 HTML
  - `GET /词典/放疫词汇` → 新冠 180 条
- **源 B：Lingoes 官方词库文件**（完整性基准，覆盖站点全部词条）
  - 来源页：http://lingoes.net/en/dictionary/dict_down.php?id=B04AFA0E12932743B26CC62B80F6EF23
  - 直链：http://dl.lingoes.net/download/dict/ld2/Italian-Chinese%20%26%20Chinese-Italian%20Dictionary.ld2
  - 文件：8,183,248 字节，**MD5 = 752A32DD0D3DABE7B6FF9F3B854C565C**（与官网公布一致，已校验）
  - 解析：自实现 `ld2read.mjs`（zlib 分块解压 + 词条索引，参考 GitHub `nviet/lingoes-converter` 的 LingoesConverter.php 逻辑）
  - 内容：373,928 条记录 = 意汉 64,970（释义非空）+ 汉意 44,928 + 变位形指针 264,027（无独立释义，是"某动词某时态变位"的指针，已跳过）+ 少量符号条目

两源合并规则见 `yihan-dict/tools/build_db.mjs`：以 `(lower(trim(word)), kind)` 为键，站点行为主，ld2 补齐站点未抓到的词条（`entries.source` 列区分 `site`/`ld2`/`site+ld2`）。**结论：无论抓取进度如何，词条集合都是完整的**（以 ld2 为完整性基准），站点抓取负责提供站点 id 与站点渲染内容。

### 完整性证据

1. 站点无公开词条总数；但 ld2 的 373,928 条记录与站点 id 空间（意汉词条 id ~1,726–130,000、汉意 ~38 万、变位形 ~16 万–31 万）吻合。
2. 抽查现代词（健康码/核酸检测/微博/二维码/人工智能/博客/视频/smartphone/email）在站点上**均不作为独立词条存在**（返回近似词），说明站点自 2012 年后几乎未新增普通词条；唯一新增是 2020 年防疫词汇 180 条，已单独抓取。
3. 站点抓取覆盖率以 `tools/coverage.mjs` 报告为准：站点已抓词数 / ld2 词表数。

## 3. 抓取速率与反爬（重要）

- 站点由 Aruba 托管，前置 DDoS 防护：短时高频请求会返回 **HTTP 429 "TOO MANY REQUESTS"**，并进入 **IP 级临时封禁**（实测 18–25 分钟自动解除；无 Retry-After 头）。
- 实测阈值：≈6.4 req/s 持续 2–3 分钟即被封；≈2.9–3.0 req/s 可持续运行（本项目最终采用 **3.0 req/s**，4 个并发 worker，全局限速器 + 429 指数退避 30s/60s/120s + 自动降速）。
- IPv6 不可用，改 UA 无效（封禁按 IP）。
- 抓取器 `scrape/lib.mjs` 的限速器为令牌桶 + 并发上限；`scrape/crawl.mjs` 支持断点续跑（`raw/crawl_state.json` + 按 id/词去重），中断后重启不会重复抓取。

## 4. 查询接口（实测结论）

| 接口 | 用途 | 限制 |
|---|---|---|
| `POST /ajax/findword` | 精确 + 近似查询，返回完整释义 | 无批量接口；一次一个词 |
| `GET /ajax/autocomplete?arg=<prefix>` | 前缀补全 | **硬上限 12 条**，`limit/num/count/...` 参数全部无效 → 无法用它整表导出 |
| `GET /<词条>`、`GET /动词变位/<动词>` | 词条页 / 变位页 | 无 sitemap、无索引路由、无 id 反查 |

由于 autocomplete 只能给 12 条且无批量导出，词表枚举（trie/逐字符扩展）成本极高且易触发封禁——这正是采用 ld2 词表作为抓取清单的原因：**109,882 个待抓词**（100 个校对动词 + 64,954 意汉 + 44,928 汉意，去重）。

## 5. 复现步骤

```bash
# 1) 下载并校验 ld2 词库（分块 Range 下载 + 拼接，见 data/dl_ld2*.mjs）
node data/concat_ld2.mjs           # → data/icdict.ld2（校验 MD5）
# 2) 解析 ld2 → 词表
node data/ld2read.mjs && node data/extract_ld2.mjs   # → data/ld2_words.jsonl
node data/build_lists2.mjs         # → data/list_it.txt / list_zh.txt / list_forms.txt / list_all.txt
# 3) 抓取站点（限速 3 req/s，可断点续跑）
cd scrape && node crawl.mjs --rate 3.0 --conc 5 --minrps 2.2 --workers 4 --noenum \
  --list D:/DSHWorkSpace/data/list_all.txt --conjlist D:/DSHWorkSpace/data/ld2_verbs_forms.jsonl
# 4) 建库 + 校验 + 导出
cd yihan-dict && node tools/build_db.mjs --fresh --lite --corrections ../verbs-audit/corrections.json \
  --corrected ../verbs-audit/verbs-corrected.json --ld2 ../data/ld2_words.jsonl
node tools/validate.mjs && node tools/coverage.mjs && node tools/export_web.mjs && node tools/export_sql.mjs
```

## 6. 合规与限速说明

本站为个人学术性词典站（作者为博洛尼亚大学研究者），数据源本身是公开发布的 Lingoes 词库（可自由下载）。抓取遵守：低速率（3 req/s）、遇到 429 立即退避、不抓取登录/反馈等交互接口、不重复下载已知内容。数据仅用于学习与研究用途，版权归原作者所有。
