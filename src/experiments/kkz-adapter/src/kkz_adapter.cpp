// kkz_adapter.cpp -- task-49 / W2-Y : KKZ 实验性适配器 v1（可复用代理模板）
// ============================================================================
// 作用：把 KiriKiri Z 的某个 plugin\*.dll 换成"代理 DLL"，用引擎自己的
//       iTVPFunctionExporter 读 KAG 当前台词并落盘（可选进程内 TTS）。
//
// 设计基线（全部来自本机实测，勿凭想象改）：
//   [实测 task-46] exporter->QueryFunctionsByNarrowString 用"精确签名串"命中
//                  TVPExecuteExpression / TVPExecuteScript / TJSAllocVariantString /
//                  TVPGetScriptDispatch；ttstr 是 POD{void*}; tTJSVariant 32B，vt@8
//                  （2=字符串 4=整数 5=实数）。
//   [实测 task-46] /EHa 必须（否则 TJS 抛出的异常穿到原生侧会直接终止）；每步先落盘。
//   [实测 task-47] message-only 窗口 + SetTimer 回到引擎主线程后，TJS 表达式稳定可求值
//                  （wtid == cur_tid），一次进局抓到 8 行真实台词。
//   [实测 task-47] 唯一可靠文本源：global.kag.historyLayer.currentInfo
//                  { disp / text / plaintext }；本作该 build 里 disp/text 恒空，
//                  plaintext = 画面日文原文（中文译文未取到，见 REPORT）。
//
// ★★★ task-47 的坑位（本文件用代码结构强制规避，改代码前先读一遍）★★★
//   坑1  TJS 侧 `new Object()` / `new Array()` -> 进程 0xC0000409 (___report_gsfailure)。
//        => 本模板【完全不向 TJS 注入任何脚本/对象/函数】。TJS 只被"求值表达式读属性"，
//           状态机 / 缓冲 / 去重 / 稳定化 / 落盘 / 朗读全部在原生侧（见 Tick）。
//   坑2  `typeof(对象)` 会抛异常（typeof(1) 正常）。=> 存在性判定一律用 `x == void`。
//   坑3  `wsprintfW(buf, L"前缀=", 值)` 这种"格式串里没有转换符"会静默丢值。
//        => 本文件不使用 wsprintf 拼日志，全部走 Append*() 手工拼接。
//   坑4  在 V2Link 里立刻求值 TJS 时机太早（KAG 未加载）。=> V2Link 只取函数指针 +
//        建定时器；所有 TJS 求值都在第一个 WM_TIMER 之后（此时 KAG 已就绪）。
//   坑5  逐字显示会产生中间态（空行/重复行/半句）。=> 稳定化（StabilizeMs）+ 最小长度
//        + 连续去重，三件套都在 Emit 前做。
//
// 参数化（编译期默认值 + 运行期 kkz-adapter.ini 覆盖，INI 与 DLL 同目录）：
//   TargetDll / OrigSuffix / PollMs / OutputFile / LogFile / EnableTts /
//   StabilizeMs / MinChars / MaxRows / RequireExe / RequireExeVersion /
//   RequireOrigSha256 / FailMode / ExprName / ExprText / ExprPlain
//   见 README.md 与 tools/kkz-adapter.example.ini。
//
// 安全门（不满足 => 拒绝注入，但仍尽量把原插件转发出去让游戏能跑）：
//   1) 本模块文件名必须等于 TargetDll（防误改名安装）
//   2) 宿主 exe 名 / 版本必须匹配 RequireExe / RequireExeVersion
//   3) 原 DLL 必须存在；RequireOrigSha256 非空时必须哈希一致
//   编译期常量见下方 KKZ_CFG_*。
// ============================================================================
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincrypt.h>   // CryptAcquireContextW / CryptHashData（SHA-256 校验原 DLL 用）
#include <stdint.h>
#include <wchar.h>   // wcscmp / wcsncmp / wcslen

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "kernel32.lib")
#pragma comment(lib, "advapi32.lib")   // CryptoAPI (SHA-256) / GetPrivateProfileStringW
#pragma comment(lib, "version.lib")    // GetFileVersionInfoW

// ---- 可选 TTS：MSVC 默认开；gcc 由构建脚本决定（-DKKZ_HAS_SAPI=1/0）----
#ifndef KKZ_HAS_SAPI
#  if defined(_MSC_VER)
#    define KKZ_HAS_SAPI 1
#  else
#    define KKZ_HAS_SAPI 0
#  endif
#endif
#if KKZ_HAS_SAPI
#  include <sapi.h>
#  pragma comment(lib, "sapi.lib")
#  pragma comment(lib, "ole32.lib")
#endif

// ---------------------------------------------------------------- 编译期默认 ----
// build.ps1 会把配置写成 build\kkz_build_config.h 再以 -I/-I 引入：有些默认值
// （典型：%TEMP%\...）无法安全地放在 cl 命令行 /D 上（cl 报 D8038），故走头文件。
#if defined(__has_include)
#  if __has_include("kkz_build_config.h")
#    include "kkz_build_config.h"
#  endif
#endif
#ifndef KKZ_CFG_VERSION
#  define KKZ_CFG_VERSION            L"0.1.0.0"
#endif
#ifndef KKZ_CFG_TARGET_DLL
#  define KKZ_CFG_TARGET_DLL         L"getSample.dll"
#endif
#ifndef KKZ_CFG_ORIG_SUFFIX
#  define KKZ_CFG_ORIG_SUFFIX        L"_orig"
#endif
#ifndef KKZ_CFG_POLL_MS
#  define KKZ_CFG_POLL_MS            200
#endif
#ifndef KKZ_CFG_STABILIZE_MS
#  define KKZ_CFG_STABILIZE_MS       200
#endif
#ifndef KKZ_CFG_MIN_CHARS
#  define KKZ_CFG_MIN_CHARS          2
#endif
#ifndef KKZ_CFG_MAX_ROWS
#  define KKZ_CFG_MAX_ROWS           0     // 0 = 不限
#endif
#ifndef KKZ_CFG_OUTPUT_FILE
#  define KKZ_CFG_OUTPUT_FILE        L"%TEMP%\\kkz_adapter_capture.txt"
#endif
#ifndef KKZ_CFG_LOG_FILE
#  define KKZ_CFG_LOG_FILE           L"%TEMP%\\kkz_adapter_probe.txt"
#endif
#ifndef KKZ_CFG_ENABLE_TTS
#  define KKZ_CFG_ENABLE_TTS         0
#endif
#ifndef KKZ_CFG_REQUIRE_EXE
#  define KKZ_CFG_REQUIRE_EXE        L""
#endif
#ifndef KKZ_CFG_REQUIRE_EXE_VERSION
#  define KKZ_CFG_REQUIRE_EXE_VERSION L""
#endif
#ifndef KKZ_CFG_REQUIRE_ORIG_SHA256
#  define KKZ_CFG_REQUIRE_ORIG_SHA256 L""
#endif
#ifndef KKZ_CFG_FAIL_MODE
#  define KKZ_CFG_FAIL_MODE          L"degraded"   // degraded|strict
#endif
#ifndef KKZ_CFG_EXPR_NAME
#  define KKZ_CFG_EXPR_NAME          L"global.kag.historyLayer.currentInfo.disp"
#endif
#ifndef KKZ_CFG_EXPR_TEXT
#  define KKZ_CFG_EXPR_TEXT          L"global.kag.historyLayer.currentInfo.text"
#endif
#ifndef KKZ_CFG_EXPR_PLAIN
#  define KKZ_CFG_EXPR_PLAIN         L"global.kag.historyLayer.currentInfo.plaintext"
#endif

#define KKZ_TIMER_ID     0x4B5A
#define KKZ_MAX_TEXT     4096
#define KKZ_MAX_NAME     512
#define KKZ_MAX_ROWS_HARD 20000

// ---------------------------------------------------------------- 小工具 ----
static void AppendW(wchar_t *buf, int cap, const wchar_t *s)
{
	if (!buf || cap <= 1 || !s) return;
	int n = (int)lstrlenW(buf);
	while (*s && n < cap - 1) buf[n++] = *s++;
	buf[n] = 0;
}

static void AppendWChar(wchar_t *buf, int cap, wchar_t c)
{
	if (!buf || cap <= 1) return;
	int n = (int)lstrlenW(buf);
	if (n < cap - 1) { buf[n++] = c; buf[n] = 0; }
}

static void AppendInt(wchar_t *buf, int cap, long long v)
{
	wchar_t tmp[32];
	int i = 0;
	unsigned long long u;
	if (v < 0) { AppendWChar(buf, cap, L'-'); u = (unsigned long long)(-(v + 1)) + 1ULL; }
	else u = (unsigned long long)v;
	if (u == 0) tmp[i++] = L'0';
	while (u && i < 31) { tmp[i++] = (wchar_t)(L'0' + (wchar_t)(u % 10)); u /= 10; }
	while (i > 0) AppendWChar(buf, cap, tmp[--i]);
}

static void AppendHex(wchar_t *buf, int cap, unsigned long long v, int digits)
{
	wchar_t tmp[24];
	const wchar_t *hex = L"0123456789ABCDEF";
	int i = 0;
	if (digits <= 0) digits = 1;
	for (int k = digits - 1; k >= 0; --k) tmp[i++] = hex[(v >> (k * 4)) & 0xF];
	tmp[i] = 0;
	AppendW(buf, cap, tmp);
}

static void CopyW(wchar_t *dst, int cap, const wchar_t *src)
{
	if (!dst || cap <= 0) return;
	if (!src) { dst[0] = 0; return; }
	lstrcpynW(dst, src, cap);
}

static int EqNoCase(const wchar_t *a, const wchar_t *b)
{
	if (!a || !b) return 0;
	return lstrcmpiW(a, b) == 0;
}

// 取文件名（不分配内存）
static const wchar_t *LeafOf(const wchar_t *path)
{
	if (!path) return L"";
	const wchar_t *leaf = path;
	for (const wchar_t *p = path; *p; ++p)
		if (*p == L'\\' || *p == L'/') leaf = p + 1;
	return leaf;
}

// 去掉扩展名（写入 out）
static void StemOf(const wchar_t *name, wchar_t *out, int cap)
{
	CopyW(out, cap, name);
	int n = (int)lstrlenW(out);
	for (int i = n - 1; i >= 0; --i)
	{
		if (out[i] == L'.') { out[i] = 0; return; }
		if (out[i] == L'\\' || out[i] == L'/') return;
	}
}

static void ExpandEnv(const wchar_t *in, wchar_t *out, int cap)
{
	out[0] = 0;
	if (!in || !in[0]) return;
	DWORD n = ExpandEnvironmentStringsW(in, out, (DWORD)cap);
	if (n == 0 || n > (DWORD)cap) CopyW(out, cap, in);   // 展开失败就原样用
}

// ---------------------------------------------------------------- 配置 ----
struct KkzConfig
{
	wchar_t version[32];
	wchar_t targetDll[128];
	wchar_t origSuffix[32];
	DWORD   pollMs;
	DWORD   stabilizeMs;
	int     minChars;
	int     maxRows;
	wchar_t outputFile[MAX_PATH * 2];
	wchar_t logFile[MAX_PATH * 2];
	BOOL    enableTts;
	wchar_t requireExe[128];
	wchar_t requireExeVersion[64];
	wchar_t requireOrigSha256[80];
	int     failStrict;                 // 0=degraded 1=strict
	wchar_t exprName[512];
	wchar_t exprText[512];
	wchar_t exprPlain[512];
	wchar_t iniPath[MAX_PATH];
	BOOL    iniPresent;
	wchar_t selfPath[MAX_PATH];
	wchar_t selfDir[MAX_PATH];
	BOOL    loaded;
};
static KkzConfig g_cfg;

static void IniStr(const wchar_t *sec, const wchar_t *key, const wchar_t *def,
                   wchar_t *out, int cap)
{
	out[0] = 0;
	if (!g_cfg.iniPresent) { CopyW(out, cap, def); return; }
	// 用一个不可能出现在配置里的哨兵字符判断"键不存在"
	DWORD n = GetPrivateProfileStringW(sec, key, L"\x01", out, (DWORD)cap, g_cfg.iniPath);
	if (n == 0 || out[0] == 0x01) CopyW(out, cap, def);
}

static int IniInt(const wchar_t *sec, const wchar_t *key, int def, int lo, int hi)
{
	wchar_t tmp[64];
	IniStr(sec, key, L"", tmp, 64);
	if (!tmp[0]) return def;
	int v = 0, sign = 1, i = 0;
	if (tmp[0] == L'-') { sign = -1; i = 1; }
	for (; tmp[i] >= L'0' && tmp[i] <= L'9'; ++i) v = v * 10 + (tmp[i] - L'0');
	v *= sign;
	if (v < lo) v = lo;
	if (v > hi) v = hi;
	return v;
}

static void SanitizeLeaf(const wchar_t *in, wchar_t *out, int cap, const wchar_t *def)
{
	// 只允许纯文件名：任何路径分隔符/盘符都视为非法，回落到默认
	for (const wchar_t *p = in; p && *p; ++p)
		if (*p == L'\\' || *p == L'/' || *p == L':') { CopyW(out, cap, def); return; }
	if (!in || !in[0]) { CopyW(out, cap, def); return; }
	CopyW(out, cap, in);
}

static void CfgLoad(void)
{
	if (g_cfg.loaded) return;
	g_cfg.loaded = TRUE;

	ZeroMemory(&g_cfg, sizeof(g_cfg));
	CopyW(g_cfg.version, 32, KKZ_CFG_VERSION);

	// 自身路径 => 目录 => ini 路径（环境变量 KKZ_ADAPTER_INI 可覆盖）
	HMODULE hm = NULL;
	if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(const void *)&CfgLoad, &hm))
		GetModuleFileNameW(hm, g_cfg.selfPath, MAX_PATH);

	CopyW(g_cfg.selfDir, MAX_PATH, g_cfg.selfPath);
	for (int i = (int)lstrlenW(g_cfg.selfDir) - 1; i >= 0; --i)
	{
		if (g_cfg.selfDir[i] == L'\\' || g_cfg.selfDir[i] == L'/') { g_cfg.selfDir[i] = 0; break; }
	}

	wchar_t iniOverride[MAX_PATH] = {0};
	DWORD env = GetEnvironmentVariableW(L"KKZ_ADAPTER_INI", iniOverride, MAX_PATH);
	if (env > 0 && env < MAX_PATH && iniOverride[0]) CopyW(g_cfg.iniPath, MAX_PATH, iniOverride);
	else
	{
		CopyW(g_cfg.iniPath, MAX_PATH, g_cfg.selfDir);
		AppendW(g_cfg.iniPath, MAX_PATH, L"\\kkz-adapter.ini");
	}
	g_cfg.iniPresent = (GetFileAttributesW(g_cfg.iniPath) != INVALID_FILE_ATTRIBUTES);

	IniStr(L"adapter", L"TargetDll", KKZ_CFG_TARGET_DLL, g_cfg.targetDll, 128);
	{
		wchar_t tmp[128];
		CopyW(tmp, 128, g_cfg.targetDll);                        // 先拷出来：就地清洗不可靠
		SanitizeLeaf(tmp, g_cfg.targetDll, 128, KKZ_CFG_TARGET_DLL);
	}

	IniStr(L"adapter", L"OrigSuffix", KKZ_CFG_ORIG_SUFFIX, g_cfg.origSuffix, 32);
	{
		// 后缀不允许路径字符与点（点由 MakeOrigPath 自己补）
		BOOL bad = FALSE;
		for (const wchar_t *p = g_cfg.origSuffix; *p; ++p)
			if (*p == L'\\' || *p == L'/' || *p == L':' || *p == L'.') bad = TRUE;
		if (bad || !g_cfg.origSuffix[0]) CopyW(g_cfg.origSuffix, 32, KKZ_CFG_ORIG_SUFFIX);
	}

	g_cfg.pollMs      = (DWORD)IniInt(L"adapter", L"PollMs", KKZ_CFG_POLL_MS, 50, 5000);
	g_cfg.stabilizeMs = (DWORD)IniInt(L"adapter", L"StabilizeMs", KKZ_CFG_STABILIZE_MS, 0, 5000);
	g_cfg.minChars    = IniInt(L"adapter", L"MinChars", KKZ_CFG_MIN_CHARS, 0, 64);
	g_cfg.maxRows     = IniInt(L"adapter", L"MaxRows", KKZ_CFG_MAX_ROWS, 0, KKZ_MAX_ROWS_HARD);
	g_cfg.enableTts   = IniInt(L"adapter", L"EnableTts", KKZ_CFG_ENABLE_TTS, 0, 1) ? TRUE : FALSE;

	{
		wchar_t raw[MAX_PATH * 2];
		IniStr(L"adapter", L"OutputFile", KKZ_CFG_OUTPUT_FILE, raw, MAX_PATH * 2);
		ExpandEnv(raw, g_cfg.outputFile, MAX_PATH * 2);
		IniStr(L"adapter", L"LogFile", KKZ_CFG_LOG_FILE, raw, MAX_PATH * 2);
		ExpandEnv(raw, g_cfg.logFile, MAX_PATH * 2);
	}

	IniStr(L"adapter", L"RequireExe", KKZ_CFG_REQUIRE_EXE, g_cfg.requireExe, 128);
	{
		wchar_t tmp[128];
		CopyW(tmp, 128, g_cfg.requireExe);
		SanitizeLeaf(tmp, g_cfg.requireExe, 128, L"");
	}
	IniStr(L"adapter", L"RequireExeVersion", KKZ_CFG_REQUIRE_EXE_VERSION, g_cfg.requireExeVersion, 64);
	IniStr(L"adapter", L"RequireOrigSha256", KKZ_CFG_REQUIRE_ORIG_SHA256, g_cfg.requireOrigSha256, 80);
	{
		wchar_t fm[32];
		IniStr(L"adapter", L"FailMode", KKZ_CFG_FAIL_MODE, fm, 32);
		g_cfg.failStrict = EqNoCase(fm, L"strict") ? 1 : 0;
	}

	IniStr(L"text", L"ExprName",  KKZ_CFG_EXPR_NAME,  g_cfg.exprName,  512);
	IniStr(L"text", L"ExprText",  KKZ_CFG_EXPR_TEXT,  g_cfg.exprText,  512);
	IniStr(L"text", L"ExprPlain", KKZ_CFG_EXPR_PLAIN, g_cfg.exprPlain, 512);
}

// ---------------------------------------------------------------- 落盘 ----
static void AppendFileUtf8(const wchar_t *path, const wchar_t *text, DWORD textLen)
{
	if (!path || !path[0] || !text || !textLen) return;
	char u8[16384];
	int need = WideCharToMultiByte(CP_UTF8, 0, text, (int)textLen, NULL, 0, NULL, NULL);
	if (need <= 0) return;
	if (need > (int)sizeof(u8) - 4) need = (int)sizeof(u8) - 4;
	int n = WideCharToMultiByte(CP_UTF8, 0, text, (int)textLen, u8, need, NULL, NULL);
	if (n <= 0) return;
	u8[n] = '\r'; u8[n + 1] = '\n';
	HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
	                       NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if (h == INVALID_HANDLE_VALUE) return;
	DWORD wrote = 0;
	WriteFile(h, u8, (DWORD)n + 2, &wrote, NULL);
	CloseHandle(h);
}

// 日志：key=value（不用 wsprintf，见坑3）
static void LogLine(const wchar_t *line)
{
	wchar_t buf[16384];
	CopyW(buf, 16384, line);
	AppendFileUtf8(g_cfg.logFile, buf, (DWORD)lstrlenW(buf));
}

static void LogKV(const wchar_t *k, const wchar_t *v)
{
	wchar_t buf[16384];
	buf[0] = 0;
	AppendW(buf, 16384, k);
	AppendWChar(buf, 16384, L'=');
	AppendW(buf, 16384, v ? v : L"(null)");
	LogLine(buf);
}

static void LogKD(const wchar_t *k, long long v)
{
	wchar_t buf[16384];
	buf[0] = 0;
	AppendW(buf, 16384, k);
	AppendWChar(buf, 16384, L'=');
	AppendInt(buf, 16384, v);
	LogLine(buf);
}

static void LogKH(const wchar_t *k, unsigned long long v, int digits)
{
	wchar_t buf[16384];
	buf[0] = 0;
	AppendW(buf, 16384, k);
	AppendWChar(buf, 16384, L'=');
	AppendHex(buf, 16384, v, digits);
	LogLine(buf);
}

static void LogUtc(void)
{
	SYSTEMTIME st;
	GetSystemTime(&st);
	wchar_t buf[128];
	buf[0] = 0;
	AppendW(buf, 128, L"utc=");
	AppendInt(buf, 128, st.wYear); AppendWChar(buf, 128, L'-');
	AppendInt(buf, 128, st.wMonth); AppendWChar(buf, 128, L'-');
	AppendInt(buf, 128, st.wDay);   AppendWChar(buf, 128, L'T');
	AppendInt(buf, 128, st.wHour);  AppendWChar(buf, 128, L':');
	AppendInt(buf, 128, st.wMinute); AppendWChar(buf, 128, L':');
	AppendInt(buf, 128, st.wSecond); AppendWChar(buf, 128, L'Z');
	LogLine(buf);
}

// ---------------------------------------------------------------- 哈希 ----
// 用 CryptoAPI 算 SHA-256（不引第三方库）；失败返回 FALSE，调用方按"校验不通过"处理。
static BOOL FileSha256Hex(const wchar_t *path, wchar_t *outHex, int cap)
{
	outHex[0] = 0;
	HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
	                       NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if (f == INVALID_HANDLE_VALUE) return FALSE;

	HCRYPTPROV prov = 0;
	HCRYPTHASH hash = 0;
	BOOL ok = FALSE;
	if (!CryptAcquireContextW(&prov, NULL, NULL, PROV_RSA_AES, CRYPT_VERIFYCONTEXT))
	{ CloseHandle(f); return FALSE; }
	if (!CryptCreateHash(prov, CALG_SHA_256, 0, 0, &hash))
	{ CryptReleaseContext(prov, 0); CloseHandle(f); return FALSE; }

	BYTE chunk[65536];
	DWORD got = 0;
	BOOL readOk = TRUE;
	for (;;)
	{
		if (!ReadFile(f, chunk, sizeof(chunk), &got, NULL)) { readOk = FALSE; break; }
		if (got == 0) break;
		if (!CryptHashData(hash, chunk, got, 0)) { readOk = FALSE; break; }
	}

	if (readOk)
	{
		BYTE digest[32];
		DWORD len = sizeof(digest);
		if (CryptGetHashParam(hash, HP_HASHVAL, digest, &len, 0) && len == 32)
		{
			int i = 0;
			for (int b = 0; b < 32 && i < cap - 3; ++b)
			{
				const wchar_t *hex = L"0123456789abcdef";
				outHex[i++] = hex[(digest[b] >> 4) & 0xF];
				outHex[i++] = hex[digest[b] & 0xF];
			}
			outHex[i] = 0;
			ok = TRUE;
		}
	}

	if (hash) CryptDestroyHash(hash);
	CryptReleaseContext(prov, 0);
	CloseHandle(f);
	return ok;
}

// ---------------------------------------------------------------- 宿主 exe ----
struct HostInfo
{
	wchar_t path[MAX_PATH];
	wchar_t leaf[MAX_PATH];
	wchar_t version[64];     // a.b.c.d
	BOOL    haveVersion;
};
static HostInfo g_host;

static void GetHostInfo(void)
{
	if (g_host.path[0]) return;
	ZeroMemory(&g_host, sizeof(g_host));
	GetModuleFileNameW(NULL, g_host.path, MAX_PATH);
	CopyW(g_host.leaf, MAX_PATH, LeafOf(g_host.path));

	DWORD dummy = 0;
	DWORD sz = GetFileVersionInfoSizeW(g_host.path, &dummy);
	if (sz == 0) return;
	BYTE *data = (BYTE *)HeapAlloc(GetProcessHeap(), 0, sz);
	if (!data) return;
	if (GetFileVersionInfoW(g_host.path, 0, sz, data))
	{
		VS_FIXEDFILEINFO *fi = NULL;
		UINT len = 0;
		if (VerQueryValueW(data, L"\\", (LPVOID *)&fi, &len) && fi)
		{
			g_host.version[0] = 0;
			AppendInt(g_host.version, 64, HIWORD(fi->dwFileVersionMS));
			AppendWChar(g_host.version, 64, L'.');
			AppendInt(g_host.version, 64, LOWORD(fi->dwFileVersionMS));
			AppendWChar(g_host.version, 64, L'.');
			AppendInt(g_host.version, 64, HIWORD(fi->dwFileVersionLS));
			AppendWChar(g_host.version, 64, L'.');
			AppendInt(g_host.version, 64, LOWORD(fi->dwFileVersionLS));
			g_host.haveVersion = TRUE;
		}
	}
	HeapFree(GetProcessHeap(), 0, data);
}

// 版本门：require 为空 => 通过；以 '*' 结尾 => 前缀匹配；否则按点分数字逐段比较
static BOOL VersionGateOk(const wchar_t *require)
{
	if (!require || !require[0]) return TRUE;
	if (!g_host.haveVersion) return FALSE;

	int n = (int)lstrlenW(require);
	if (n > 0 && require[n - 1] == L'*')
	{
		wchar_t prefix[64];
		CopyW(prefix, 64, require);
		int pn = (int)lstrlenW(prefix);
		if (pn > 0) prefix[pn - 1] = 0;
		return wcsncmp(g_host.version, prefix, wcslen(prefix)) == 0;
	}
	// 逐段数值比较（容忍 "1.2" 这类短写法）
	const wchar_t *a = g_host.version;
	const wchar_t *b = require;
	for (int seg = 0; seg < 8; ++seg)
	{
		long va = 0, vb = 0;
		while (*a >= L'0' && *a <= L'9') { va = va * 10 + (*a - L'0'); ++a; }
		while (*b >= L'0' && *b <= L'9') { vb = vb * 10 + (*b - L'0'); ++b; }
		if (va != vb) return FALSE;
		BOOL aEnd = (*a != L'.'), bEnd = (*b != L'.');
		if (aEnd && bEnd) return TRUE;
		if (aEnd || bEnd) return FALSE;
		++a; ++b;
	}
	return TRUE;
}

// ---------------------------------------------------------------- TJS ABI ----
typedef int          tjs_int;
typedef unsigned int tjs_uint;
typedef wchar_t      tjs_char;
typedef __int64      tjs_int64;

struct iTVPFunctionExporter
{
	virtual bool __cdecl QueryFunctions(const tjs_char **name, void **function, tjs_uint count) = 0;
	virtual bool __cdecl QueryFunctionsByNarrowString(const char **name, void **function, tjs_uint count) = 0;
};

typedef void   (__cdecl *fnExecExpr)(const void *ttstrRef, void *variantOut);
typedef void * (__cdecl *fnAllocStr)(const tjs_char *s);

// tTJSVariant（32 位）：8B union + 4B vt(@8) + 20B padding = 32B；再放大 + canary 防超写。
// 依据 task-46 实测；canary 只在首次破坏时记一条日志（task-47 全程未触发）。
struct VarBuf
{
	union
	{
		struct { void *Object; void *ObjThis; } closure;
		tjs_int64 integer;
		double    real;
		void *    string;
		void *    octet;
	} u;
	unsigned long vt;       // @8: 2=string 4=integer 5=real
	unsigned long pad[5];
	unsigned long fill[40];
	unsigned long canary[8];
};
static const unsigned long kCanary = 0xA5A5A5A5u;

static void VarInit(VarBuf *v)
{
	ZeroMemory(v, sizeof(*v));
	for (int i = 0; i < 40; ++i) v->fill[i] = 0x5A5A5A5Au;
	for (int i = 0; i < 8; ++i) v->canary[i] = kCanary;
}
static int VarCanaryBroken(const VarBuf *v)
{
	for (int i = 0; i < 8; ++i) if (v->canary[i] != kCanary) return i + 1;
	return 0;
}

static fnExecExpr  g_execExpr = NULL;
static fnAllocStr  g_allocStr = NULL;
static void *      g_scriptDispatch = NULL;
static int         g_tjsQueried = 0;
static int         g_tjsReady = 0;

// tTJSVariantString：RefCount@0 / LongString@4 / ShortString@8，两缓冲皆 0 结尾
static int CopyVariantString(const VarBuf *v, wchar_t *out, int cap)
{
	out[0] = 0;
	if (!v || v->vt != 2 || !v->u.string || cap <= 1) return 0;
	const unsigned char *base = (const unsigned char *)v->u.string;
	void *longStr = *(void **)(base + 4);
	const wchar_t *src = longStr ? (const wchar_t *)longStr : (const wchar_t *)(base + 8);
	int i = 0;
	for (; i < cap - 1; ++i)
	{
		wchar_t c = src[i];
		if (c == 0) break;
		out[i] = c;
	}
	out[i] = 0;
	return i;
}

// 求值一个 TJS 表达式。返回 1=拿到字符串(已拷 out)，0=调用成功但非字符串，-1=调用抛异常，
// -2=分配 ttstr 失败，-3=函数指针缺失。整个调用包在 try 里（TJS 异常会真的抛到原生侧）。
static int EvalStr(const wchar_t *expr, wchar_t *out, int cap, unsigned long *vtOut)
{
	out[0] = 0;
	if (vtOut) *vtOut = 0xFFFFFFFFu;
	if (!g_tjsReady || !g_allocStr || !g_execExpr) return -3;

	int ok = 0;
	VarBuf r;
	VarInit(&r);
	try
	{
		void *vs = g_allocStr(expr);
		if (!vs) return -2;
		struct TtstrPOD { void *Ptr; } t;
		t.Ptr = vs;
		g_execExpr(&t, &r);
		ok = 1;
	}
	catch (...) { ok = 0; }

	if (vtOut) *vtOut = (unsigned long)r.vt;
	{
		static int canaryLogged = 0;
		int bad = VarCanaryBroken(&r);
		if (bad && !canaryLogged)
		{
			canaryLogged = 1;
			LogKD(L"variant_overrun_slot", bad);
			LogKH(L"variant_vt_seen", (unsigned long long)r.vt, 8);
		}
	}
	if (!ok) return -1;
	if (r.vt != 2) return 0;
	CopyVariantString(&r, out, cap);
	return 1;
}

static int EvalInt(const wchar_t *expr, long long *outI64)
{
	if (outI64) *outI64 = 0;
	if (!g_tjsReady || !g_allocStr || !g_execExpr) return -3;
	int ok = 0;
	VarBuf r;
	VarInit(&r);
	try
	{
		void *vs = g_allocStr(expr);
		if (!vs) return -2;
		struct TtstrPOD { void *Ptr; } t;
		t.Ptr = vs;
		g_execExpr(&t, &r);
		ok = 1;
	}
	catch (...) { ok = 0; }
	if (!ok) return -1;
	if (r.vt == 4 && outI64) *outI64 = r.u.integer;
	return 1;
}

// 从 exporter 取函数（精确签名串；取自 task-46 实测命中的 5 条）
static void QueryTjs(void *exporter)
{
	if (g_tjsQueried) return;
	g_tjsQueried = 1;
	if (!exporter) { LogLine(L"tjs=unavailable reason=null_exporter"); return; }

	iTVPFunctionExporter *exp = (iTVPFunctionExporter *)exporter;
	struct QItem { const char *name; void **slot; const wchar_t *label; };
	QItem items[] = {
		{ "void ::TVPExecuteExpression(const ttstr &,tTJSVariant *)",      (void **)&g_execExpr,     L"TVPExecuteExpression" },
		{ "void ::TVPExecuteScript(const ttstr &,tTJSVariant *)",          NULL,                     L"TVPExecuteScript" },
		{ "tTJSVariantString * ::TJSAllocVariantString(const tjs_char *)", (void **)&g_allocStr,     L"TJSAllocVariantString" },
		{ "iTJSDispatch2 * ::TVPGetScriptDispatch()",                      (void **)&g_scriptDispatch, L"TVPGetScriptDispatch" },
	};
	const int N = (int)(sizeof(items) / sizeof(items[0]));
	int okCount = 0;
	for (int i = 0; i < N; ++i)
	{
		const char *nm = items[i].name;
		void *f = NULL;
		bool ok = false;
		try { ok = exp->QueryFunctionsByNarrowString(&nm, &f, 1); }
		catch (...) { ok = false; }
		if (items[i].slot) *(items[i].slot) = f;
		if (f) okCount++;
		wchar_t line[512];
		line[0] = 0;
		AppendW(line, 512, L"Q ");
		AppendW(line, 512, items[i].label);
		AppendW(line, 512, L" ok=");
		AppendInt(line, 512, ok ? 1 : 0);
		AppendW(line, 512, L" fn=");
		AppendHex(line, 512, (unsigned long long)(UINT_PTR)f, 8);
		LogLine(line);
	}
	LogKD(L"Q_total", okCount);

	g_tjsReady = (g_execExpr && g_allocStr) ? 1 : 0;
	LogKD(L"tjs_ready", g_tjsReady);
}

// ---------------------------------------------------------------- TTS ----
#if KKZ_HAS_SAPI
static ISpVoice *g_voice = NULL;
static int  g_ttsState = 0;        // 0=未尝试 1=可用 -1=不可用(永久禁用)
static int  g_ttsOk = 0, g_ttsErr = 0;
static wchar_t g_ttsVoice[256];

static void TtsInitOnce(void)
{
	if (g_ttsState != 0) return;
	g_ttsState = -1;
	try
	{
		HRESULT hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
		if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
		{
			LogKH(L"tts_coinit_fail_hr", (unsigned long long)(unsigned)hr, 8);
			return;
		}
		ISpVoice *v = NULL;
		hr = CoCreateInstance(CLSID_SpVoice, NULL, CLSCTX_ALL, IID_ISpVoice, (void **)&v);
		if (FAILED(hr) || !v)
		{
			LogKH(L"tts_cocreate_fail_hr", (unsigned long long)(unsigned)hr, 8);
			return;
		}
		ISpObjectToken *tok = NULL;
		if (SUCCEEDED(v->GetVoice(&tok)) && tok)
		{
			LPWSTR desc = NULL;
			if (SUCCEEDED(tok->GetStringValue(NULL, &desc)) && desc)
			{
				CopyW(g_ttsVoice, 256, desc);
				CoTaskMemFree(desc);
			}
			tok->Release();
		}
		g_voice = v;
		g_ttsState = 1;
		LogKV(L"tts_voice", g_ttsVoice);
		LogLine(L"tts_init=ok");
	}
	catch (...) { LogLine(L"tts_init=cpp_exception"); g_ttsState = -1; }
}

static void TtsSpeak(const wchar_t *s)
{
	if (!s || !s[0]) return;
	if (g_ttsState == 0) TtsInitOnce();
	if (g_ttsState != 1 || !g_voice) return;
	try
	{
		// SPF_ASYNC: 不阻塞主线程；PURGEBEFORESPEAK: 新句打断旧句
		HRESULT hr = g_voice->Speak(s, SPF_ASYNC | SPF_PURGEBEFORESPEAK | SPF_IS_NOT_XML, NULL);
		if (SUCCEEDED(hr)) { if (++g_ttsOk <= 3) LogKD(L"tts_speak_ok_no", g_ttsOk); }
		else { if (++g_ttsErr <= 3) LogKH(L"tts_speak_fail_hr", (unsigned long long)(unsigned)hr, 8); }
	}
	catch (...) { LogLine(L"tts_speak=cpp_exception"); g_ttsState = -1; }
}
#else
static void TtsInitOnce(void) {}
static void TtsSpeak(const wchar_t *) {}
#endif

// ---------------------------------------------------------------- 采集状态 ----
static volatile LONG g_tickBusy = 0;
static int   g_ticks = 0;
static int   g_rows = 0;
static int   g_emitSeq = 0;
static int   g_firstTickLogged = 0;
static int   g_tidWarned = 0;
static int   g_ladderDone = 0;
static DWORD g_pumpTid = 0;

static wchar_t g_pendName[KKZ_MAX_NAME];
static wchar_t g_pendText[KKZ_MAX_TEXT];
static wchar_t g_pendPlain[KKZ_MAX_TEXT];
static DWORD   g_pendSince = 0;       // 当前 pending 值的首次观察时刻（GetTickCount）
static BOOL    g_pendValid = FALSE;
static wchar_t g_lastEmitted[KKZ_MAX_TEXT];

// 环境阶梯：只做 `== void`（坑2：不要 typeof(对象)）
struct LadderItem { const wchar_t *expr; const wchar_t *label; };
static const LadderItem kLadder[] = {
	{ L"1 + 1",                                   L"baseline" },
	{ L"global.kag == void",                      L"kag_void" },
	{ L"global.kag.historyLayer == void",         L"hist_void" },
	{ L"global.kag.historyLayer.currentInfo == void", L"ci_void" },
};
static const int kLadderCount = (int)(sizeof(kLadder) / sizeof(kLadder[0]));

static void RunLadder(void)
{
	for (int i = 0; i < kLadderCount; ++i)
	{
		long long iv = 0;
		int st = EvalInt(kLadder[i].expr, &iv);
		wchar_t line[256];
		line[0] = 0;
		AppendW(line, 256, L"ladder ");
		AppendW(line, 256, kLadder[i].label);
		AppendW(line, 256, L" st=");
		AppendInt(line, 256, st);
		AppendW(line, 256, L" val=");
		AppendInt(line, 256, iv);
		LogLine(line);
	}
	g_ladderDone = 1;
}

static void WriteCaptureHeader(void)
{
	wchar_t line[1024];
	line[0] = 0;
	AppendW(line, 1024, L"# kkz-adapter v");
	AppendW(line, 1024, g_cfg.version);
	AppendW(line, 1024, L" pid=");
	AppendInt(line, 1024, (long long)GetCurrentProcessId());
	AppendW(line, 1024, L" exe=");
	AppendW(line, 1024, g_host.leaf);
	AppendW(line, 1024, L" exe_ver=");
	AppendW(line, 1024, g_host.haveVersion ? g_host.version : L"(none)");
	AppendW(line, 1024, L" target=");
	AppendW(line, 1024, g_cfg.targetDll);
	AppendW(line, 1024, L" poll_ms=");
	AppendInt(line, 1024, (long long)g_cfg.pollMs);
	AppendW(line, 1024, L" stabilize_ms=");
	AppendInt(line, 1024, (long long)g_cfg.stabilizeMs);
	AppendW(line, 1024, L" tts=");
	AppendInt(line, 1024, g_cfg.enableTts ? 1 : 0);
	AppendFileUtf8(g_cfg.outputFile, line, (DWORD)lstrlenW(line));
}

// 稳定的整句才落盘/朗读（坑5）
static void EmitRow(const wchar_t *name, const wchar_t *text, const wchar_t *plain)
{
	if (!plain || !plain[0]) return;
	if (lstrcmpW(plain, g_lastEmitted) == 0) return;      // 连续去重
	if (g_cfg.maxRows > 0 && g_rows >= g_cfg.maxRows) return;

	CopyW(g_lastEmitted, KKZ_MAX_TEXT, plain);
	++g_rows;
	++g_emitSeq;

	wchar_t line[16384];
	line[0] = 0;
	AppendInt(line, 16384, g_emitSeq);
	AppendWChar(line, 16384, L'\t');
	AppendW(line, 16384, L"stable");
	AppendWChar(line, 16384, L'\t');
	AppendW(line, 16384, name ? name : L"");
	AppendWChar(line, 16384, L'\t');
	AppendW(line, 16384, text ? text : L"");
	AppendWChar(line, 16384, L'\t');
	AppendW(line, 16384, plain);
	AppendFileUtf8(g_cfg.outputFile, line, (DWORD)lstrlenW(line));

	wchar_t log[512];
	log[0] = 0;
	AppendW(log, 512, L"row=");
	AppendInt(log, 512, g_rows);
	AppendW(log, 512, L" name_len=");
	AppendInt(log, 512, (long long)lstrlenW(name ? name : L""));
	AppendW(log, 512, L" text_len=");
	AppendInt(log, 512, (long long)lstrlenW(text ? text : L""));
	AppendW(log, 512, L" plain_len=");
	AppendInt(log, 512, (long long)lstrlenW(plain));
	LogLine(log);

	if (g_cfg.enableTts) TtsSpeak(plain);
}

static void Tick(void)
{
	if (InterlockedExchange(&g_tickBusy, 1) == 1) return;
	if (!g_tjsReady) { g_tickBusy = 0; return; }
	++g_ticks;

	if (!g_firstTickLogged)
	{
		g_firstTickLogged = 1;
		wchar_t b[256];
		b[0] = 0;
		AppendW(b, 256, L"first_tick tid=");
		AppendInt(b, 256, (long long)GetCurrentThreadId());
		AppendW(b, 256, L" pump_tid=");
		AppendInt(b, 256, (long long)g_pumpTid);
		LogLine(b);
		if (g_pumpTid && GetCurrentThreadId() != g_pumpTid && !g_tidWarned)
		{
			g_tidWarned = 1;
			LogLine(L"tick_tid_mismatch=1");   // 不在引擎主线程 => 不要调 TJS
		}
	}
	if (g_pumpTid && GetCurrentThreadId() != g_pumpTid) { g_tickBusy = 0; return; }

	// 第一拍先把环境阶梯跑完（此时 KAG 已加载；坑4）
	if (!g_ladderDone)
	{
		RunLadder();
		WriteCaptureHeader();
		LogLine(L"poll=start_capture");
		g_tickBusy = 0;
		return;
	}

	wchar_t name[KKZ_MAX_NAME]  = {0};
	wchar_t text[KKZ_MAX_TEXT]  = {0};
	wchar_t plain[KKZ_MAX_TEXT] = {0};
	int s1 = EvalStr(g_cfg.exprName,  name,  KKZ_MAX_NAME, NULL);
	int s2 = EvalStr(g_cfg.exprText,  text,  KKZ_MAX_TEXT, NULL);
	int s3 = EvalStr(g_cfg.exprPlain, plain, KKZ_MAX_TEXT, NULL);

	if (s3 < 0)
	{
		static int failLogged = 0;
		if (!failLogged)
		{
			failLogged = 1;
			wchar_t b[256];
			b[0] = 0;
			AppendW(b, 256, L"read_fail s1=");
			AppendInt(b, 256, s1);
			AppendW(b, 256, L" s2=");
			AppendInt(b, 256, s2);
			AppendW(b, 256, L" s3=");
			AppendInt(b, 256, s3);
			LogLine(b);
		}
		g_tickBusy = 0;
		return;
	}
	(void)s1; (void)s2;   // disp/text 在本作恒空，仍保留探针位（其它作品可能有用）

	// 稳定化：值变了就重置计时；连续 StabilizeMs 不变才认为整句
	DWORD now = GetTickCount();
	BOOL same = g_pendValid
		&& lstrcmpW(g_pendName, name) == 0
		&& lstrcmpW(g_pendText, text) == 0
		&& lstrcmpW(g_pendPlain, plain) == 0;
	if (!same)
	{
		CopyW(g_pendName, KKZ_MAX_NAME, name);
		CopyW(g_pendText, KKZ_MAX_TEXT, text);
		CopyW(g_pendPlain, KKZ_MAX_TEXT, plain);
		g_pendSince = now;
		g_pendValid = TRUE;
	}
	else if (plain[0] && (int)lstrlenW(plain) >= g_cfg.minChars
	         && (DWORD)(now - g_pendSince) >= g_cfg.stabilizeMs)
	{
		EmitRow(g_pendName, g_pendText, g_pendPlain);
	}

	if ((g_ticks % 150) == 0)
	{
		wchar_t b[256];
		b[0] = 0;
		AppendW(b, 256, L"tick=");
		AppendInt(b, 256, g_ticks);
		AppendW(b, 256, L" rows=");
		AppendInt(b, 256, g_rows);
		LogLine(b);
	}
	g_tickBusy = 0;
}

// ---------------------------------------------------------------- 泵 ----
static HWND g_hwnd = NULL;
static BOOL g_pumpInstalled = FALSE;

static LRESULT CALLBACK KkzWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp)
{
	if (msg == WM_TIMER && wp == KKZ_TIMER_ID)
	{
		try { Tick(); }
		catch (...) { LogLine(L"tick=cpp_exception"); }
		return 0;
	}
	if (msg == WM_DESTROY)
	{
		KillTimer(hWnd, KKZ_TIMER_ID);
		return 0;
	}
	return DefWindowProcW(hWnd, msg, wp, lp);
}

static void EnsurePump(void)
{
	if (g_pumpInstalled) return;
	g_pumpInstalled = TRUE;

	WNDCLASSW wc;
	ZeroMemory(&wc, sizeof(wc));
	wc.lpfnWndProc = KkzWndProc;
	wc.hInstance = (HINSTANCE)GetModuleHandleW(NULL);
	wc.lpszClassName = L"KkzAdapterMsgWindow";
	RegisterClassW(&wc);   // 已注册会失败，忽略

	// message-only：不可见/不在任务栏/不抢焦点，只作为定时器落点（task-47 已实测）
	g_hwnd = CreateWindowExW(0, L"KkzAdapterMsgWindow", L"", 0, 0, 0, 0, 0,
	                         HWND_MESSAGE, NULL, (HINSTANCE)GetModuleHandleW(NULL), NULL);
	if (!g_hwnd)
	{
		LogKD(L"pump=fail err", (long long)GetLastError());
		return;
	}
	g_pumpTid = GetCurrentThreadId();
	SetTimer(g_hwnd, KKZ_TIMER_ID, g_cfg.pollMs, NULL);

	wchar_t b[256];
	b[0] = 0;
	AppendW(b, 256, L"pump=ok timer_ms=");
	AppendInt(b, 256, (long long)g_cfg.pollMs);
	AppendW(b, 256, L" tid=");
	AppendInt(b, 256, (long long)g_pumpTid);
	LogLine(b);
}

// ---------------------------------------------------------------- 转发 ----
static HMODULE g_orig = NULL;
static void *  g_origV2Link = NULL;
static void *  g_origV2Unlink = NULL;
static void *  g_origThreadModel = NULL;
static int     g_linkCount = 0;

// self = ...\plugin\getSample.dll => ...\plugin\getSample_orig.dll
static BOOL MakeOrigPath(const wchar_t *self, const wchar_t *suffix, wchar_t *out, int cap)
{
	int n = (int)lstrlenW(self);
	if (n == 0 || n + 32 >= cap) return FALSE;
	int dot = n;
	while (dot > 0 && self[dot - 1] != L'.' && self[dot - 1] != L'\\') --dot;
	if (dot <= 1 || self[dot - 1] != L'.') return FALSE;

	int p = 0;
	for (int i = 0; i < dot - 1; ++i) out[p++] = self[i];
	for (const wchar_t *q = suffix; *q && p < cap - 2; ++q) out[p++] = *q;
	for (int i = dot - 1; i < n && p < cap - 1; ++i) out[p++] = self[i];
	out[p] = 0;
	return TRUE;
}

static void *SafeGetProc(HMODULE m, const char *name)
{
	if (!m) return NULL;
	return (void *)GetProcAddress(m, name);
}

struct GateResult
{
	BOOL moduleNameOk;
	BOOL exeNameOk;
	BOOL exeVersionOk;
	BOOL origExists;
	BOOL origHashOk;
	BOOL allOk;
};
static GateResult g_gate;

static void LogGate(void)
{
	LogKD(L"gate_module_name_ok", g_gate.moduleNameOk ? 1 : 0);
	LogKD(L"gate_exe_name_ok", g_gate.exeNameOk ? 1 : 0);
	LogKD(L"gate_exe_version_ok", g_gate.exeVersionOk ? 1 : 0);
	LogKD(L"gate_orig_exists", g_gate.origExists ? 1 : 0);
	LogKD(L"gate_orig_hash_ok", g_gate.origHashOk ? 1 : 0);
	LogKD(L"gate_all_ok", g_gate.allOk ? 1 : 0);
}

extern "C" HRESULT __stdcall V2Link(void *exporter)
{
	++g_linkCount;
	CfgLoad();
	GetHostInfo();

	LogLine(L"==== V2Link");
	LogUtc();
	LogKV(L"version", g_cfg.version);
	LogKV(L"self", g_cfg.selfPath);
	LogKV(L"ini", g_cfg.iniPresent ? g_cfg.iniPath : L"(none, compile-time defaults)");
	LogKV(L"target_dll", g_cfg.targetDll);
	LogKV(L"orig_suffix", g_cfg.origSuffix);
	LogKV(L"output", g_cfg.outputFile);
	LogKD(L"pid", (long long)GetCurrentProcessId());
	LogKD(L"tid", (long long)GetCurrentThreadId());
	LogKV(L"host_exe", g_host.path);
	LogKV(L"host_exe_version", g_host.haveVersion ? g_host.version : L"(none)");
	LogKV(L"link_count", g_linkCount > 1 ? L"(repeat)" : L"1");

	// ---------- 安全门 ----------
	ZeroMemory(&g_gate, sizeof(g_gate));
	wchar_t selfLeaf[MAX_PATH];
	CopyW(selfLeaf, MAX_PATH, LeafOf(g_cfg.selfPath));
	g_gate.moduleNameOk = EqNoCase(selfLeaf, g_cfg.targetDll) ? TRUE : FALSE;
	g_gate.exeNameOk = (g_cfg.requireExe[0] && !EqNoCase(g_host.leaf, g_cfg.requireExe)) ? FALSE : TRUE;
	g_gate.exeVersionOk = VersionGateOk(g_cfg.requireExeVersion);

	wchar_t orig[MAX_PATH] = {0};
	BOOL pathOk = MakeOrigPath(g_cfg.selfPath, g_cfg.origSuffix, orig, MAX_PATH);
	g_gate.origExists = (pathOk && GetFileAttributesW(orig) != INVALID_FILE_ATTRIBUTES) ? TRUE : FALSE;

	if (g_gate.origExists && g_cfg.requireOrigSha256[0])
	{
		wchar_t hex[80] = {0};
		if (FileSha256Hex(orig, hex, 80))
		{
			LogKV(L"orig_sha256", hex);
			g_gate.origHashOk = EqNoCase(hex, g_cfg.requireOrigSha256) ? TRUE : FALSE;
		}
		else
		{
			LogLine(L"orig_sha256=(compute_failed)");
			g_gate.origHashOk = FALSE;   // 算不出来 => 按不通过处理（失败安全）
		}
	}
	else g_gate.origHashOk = TRUE;   // 未 pin

	g_gate.allOk = (g_gate.moduleNameOk && g_gate.exeNameOk && g_gate.exeVersionOk
	                && g_gate.origExists && g_gate.origHashOk) ? TRUE : FALSE;
	LogGate();
	if (!g_gate.allOk)
	{
		LogLine(L"refuse=inject");
		if (!g_gate.moduleNameOk)  LogLine(L"refuse_reason=module_name");
		if (!g_gate.exeNameOk)     LogLine(L"refuse_reason=exe_name");
		if (!g_gate.exeVersionOk)  LogLine(L"refuse_reason=exe_version");
		if (!g_gate.origExists)    LogLine(L"refuse_reason=orig_missing");
		if (!g_gate.origHashOk)    LogLine(L"refuse_reason=orig_hash");
	}

	// ---------- 转发原插件（无条件：保底让游戏能跑） ----------
	HRESULT hr = S_OK;
	int forwarded = 0;
	if (pathOk && g_gate.origExists)
	{
		if (!g_orig)
		{
			SetLastError(0);
			g_orig = LoadLibraryW(orig);
			LogKD(L"load_lasterr", (long long)GetLastError());
			if (g_orig)
			{
				g_origV2Link = SafeGetProc(g_orig, "V2Link");
				g_origV2Unlink = SafeGetProc(g_orig, "V2Unlink");
				g_origThreadModel = SafeGetProc(g_orig, "GetModuleThreadModel");
			}
		}
		if (g_orig && g_origV2Link)
		{
			typedef HRESULT(__stdcall *fnLink)(void *);
			forwarded = 1;
			hr = ((fnLink)g_origV2Link)(exporter);
		}
		else
		{
			LogLine(L"forward=skipped reason=no_orig_v2link");
		}
	}
	LogKD(L"orig_loaded", g_orig ? 1 : 0);
	LogKD(L"forwarded", forwarded);
	LogKH(L"forward_ret", (unsigned long long)(unsigned)hr, 8);

	// ---------- 只取函数指针，不在 V2Link 里求值 TJS（坑4） ----------
	int inject = (g_gate.allOk && forwarded) ? 1 : 0;
	LogKD(L"inject", inject);
	if (inject)
	{
		QueryTjs(exporter);
		if (g_tjsReady) EnsurePump();
		else LogLine(L"inject=aborted reason=tjs_unavailable");
	}

	if (!inject)
	{
		// 失败安全：默认 degraded 返回 S_OK（游戏继续跑、不注入），strict 返回 E_FAIL。
		if (g_cfg.failStrict)
		{
			LogLine(L"fail_mode=strict return=E_FAIL");
			return E_FAIL;
		}
		LogLine(L"fail_mode=degraded return=S_OK");
		return S_OK;
	}
	return hr;
}

extern "C" HRESULT __stdcall V2Unlink(void)
{
	LogLine(L"==== V2Unlink");
	LogKD(L"ticks", g_ticks);
	LogKD(L"rows", g_rows);
	if (g_hwnd)
	{
		KillTimer(g_hwnd, KKZ_TIMER_ID);
		DestroyWindow(g_hwnd);
		g_hwnd = NULL;
	}
#if KKZ_HAS_SAPI
	if (g_voice) { g_voice->Release(); g_voice = NULL; }
#endif
	if (g_origV2Unlink)
	{
		typedef HRESULT(__stdcall *fnUnlink)(void);
		return ((fnUnlink)g_origV2Unlink)();
	}
	return S_OK;
}

extern "C" int __stdcall GetModuleThreadModel(void)
{
	if (g_origThreadModel)
	{
		typedef int(__stdcall *fnTm)(void);
		return ((fnTm)g_origThreadModel)();
	}
	return 0;   // 0 = mtGuard（与原插件缺省一致）
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_DETACH)
	{
		if (g_hwnd) KillTimer(g_hwnd, KKZ_TIMER_ID);
	}
	return TRUE;
}
