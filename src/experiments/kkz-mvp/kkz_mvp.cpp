// kkz_mvp.cpp -- task-47 / R26  (KKZ 最小朗读闭环)
// SenrenBanka (KiriKiri Z, x86, Steam DRM) 上的第三代插件代理:
//   getSample.dll 代理 -> 转发 V2Link -> 用 exporter 进 TJS 层
//   -> 注入 TJS 钩子(__kkz_*) -> 主线程 WM_TIMER 轮询 -> 原生落盘 + (可选)SAPI 朗读
//
// 设计依据(本机已验证或来自本作既有产品代码):
//   [实测 task-46] exporter->QueryFunctionsByNarrowString 能拿到 TVP/TJS 函数; 2 参
//                   TVPExecuteScript/TVPExecuteExpression 可用; tTJSVariant 32B, vt@8.
//   [实测 task-46] /EHa + 每步先落盘, 可定位崩溃步.
//   [文件] SenrenBanka-For-Accessibility/src/a11y6.cpp:1022-1073  本作唯一可靠文本源是
//          global.kag.historyLayer.currentInfo {disp,text,plaintext}; KAG onProcessCh 不触发.
//   [文件] 同文件 :3308-3323  在 V2Link 里建 message-only 窗口 + SetTimer, WM_TIMER 回到
//          引擎主线程 -> 在 TJS 线程上执行脚本 (本作 0.2.0.1 产品即用此法).
//   [文件] 同文件 :590-594  tTJSVariantString 布局: LongString@4 / ShortString@8, 二者皆 0 结尾.
//
// 只写 %TEMP%\kkz_mvp_probe.txt 与 %TEMP%\kkz_mvp_capture.txt; 游戏目录内只多一个被换的 dll.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdarg.h>
#pragma comment(lib, "user32.lib")
// task-47 可选 TTS: 进程内 SAPI (x86). 用 SPF_ASYNC|SPF_PURGEBEFORESPEAK 播报每行新台词。
#include <sapi.h>
#pragma comment(lib, "SAPI.Lib")
#pragma comment(lib, "ole32.lib")

typedef int            tjs_int;
typedef unsigned int   tjs_uint;
typedef wchar_t        tjs_char;
typedef __int64        tjs_int64;

// tjsTypes.h: TJS_INTF_METHOD == __cdecl (MSVC)
struct iTVPFunctionExporter
{
	virtual bool __cdecl QueryFunctions(const tjs_char **name, void **function, tjs_uint count) = 0;
	virtual bool __cdecl QueryFunctionsByNarrowString(const char **name, void **function, tjs_uint count) = 0;
};

typedef void   (__cdecl *fnExecExpr)(const void *ttstrRef, void *variantOut);
typedef void * (__cdecl *fnAllocStr)(const tjs_char *s);
typedef void * (__cdecl *fnGetScriptDispatch)(void);
typedef void   (__cdecl *fnAddLog)(const void *ttstrRef);

static fnExecExpr          g_execExpr    = NULL;  // TVPExecuteExpression(const ttstr&, tTJSVariant*)
static fnExecExpr          g_execScript  = NULL;  // TVPExecuteScript(const ttstr&, tTJSVariant*)
static fnAllocStr          g_allocStr    = NULL;  // TJSAllocVariantString(const tjs_char*)
static fnGetScriptDispatch g_getDispatch = NULL;
static fnAddLog            g_addLog      = NULL;

static HMODULE g_orig = NULL;
static void *  g_origV2Link = NULL;
static void *  g_origV2Unlink = NULL;
static void *  g_origThreadModel = NULL;
static int     g_probed = 0;
static int     g_ready = 0;          // TJS 侧钩子装好、计时器在跑

#define KKZ_TIMER_ID   0x4B5A
#define KKZ_POLL_MS    400

static volatile LONG g_tickBusy = 0;
static int g_seen = 0;               // 已取走的记录数（TJS 侧 cnt 单调递增）
static int g_ticks = 0;
static int g_firstTickLogged = 0;
static int g_pollFailLogged = 0;
static int g_canaryLogged = 0;       // 变体缓冲超写只报一次
static int g_coreInstalled = 0;      // TJS 侧 __kkz_* 核心已装
static int g_envProbed = 0;          // KAG 环境探针已跑（放到 pump 里，等 KAG 脚本加载完）
static int g_wrapTried = 0;          // processCh 包装尝试过
static int g_coreTries = 0;
static int g_coreStep = 0;
static int g_micro = 0;
static int g_microDone = 0;
static int g_stage = 0;              // KAG 读取阶梯当前步
static int g_rows = 0;               // 已落盘台词行数
static int g_pollReadyLogged = 0;
static wchar_t g_lastName[1024] = {0};
static wchar_t g_lastText[4096] = {0};
static wchar_t g_lastPlain[4096] = {0};
static int g_gotPollOk = 0;

// ---------------------------------------------------------------- paths ------
static void GetTempFile(wchar_t *out, DWORD cap, const wchar_t *name)
{
	wchar_t dir[MAX_PATH] = {0};
	DWORD n = GetTempPathW(MAX_PATH, dir);
	if(n == 0 || n >= MAX_PATH) { out[0] = 0; return; }
	lstrcpynW(out, dir, cap);
	lstrcatW(out, name);
}

static void ProbePath(wchar_t *out, DWORD cap)   { GetTempFile(out, cap, L"kkz_mvp_probe.txt"); }
static void CapturePath(wchar_t *out, DWORD cap) { GetTempFile(out, cap, L"kkz_mvp_capture.txt"); }

// ---------------------------------------------------------------- logging ----
// 用 WriteFile + 自定义格式化（任务卡已记录 wvsprintfW 多参尾部会出错，这里只用 <=2 个可变参）
static void AppendFileUtf8(const wchar_t *path, const wchar_t *text, DWORD textLen)
{
	if(!path || !path[0] || !text) return;
	char u8[8192];
	int need = WideCharToMultiByte(CP_UTF8, 0, text, (int)textLen, NULL, 0, NULL, NULL);
	if(need <= 0) return;
	if(need > (int)sizeof(u8) - 4) need = (int)sizeof(u8) - 4;
	int n = WideCharToMultiByte(CP_UTF8, 0, text, (int)textLen, u8, need, NULL, NULL);
	if(n <= 0) return;
	u8[n] = '\r'; u8[n + 1] = '\n';

	HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
		NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return;
	DWORD wrote = 0;
	WriteFile(h, u8, (DWORD)n + 2, &wrote, NULL);
	CloseHandle(h);
}

static void ProbeCat(const wchar_t *s)
{
	wchar_t p[MAX_PATH + 32];
	ProbePath(p, MAX_PATH + 32);
	AppendFileUtf8(p, s, (DWORD)lstrlenW(s));
}

static void ProbeLine(const wchar_t *s)
{
	ProbeCat(s);
}

// 注意（首跑教训）：这里的 fmt 是不带转换符的前缀，值由我们手工拼接，
// 不再依赖 wsprintfW 的 %s/%d（首跑 marker 里值全空就是踩了这个坑）。
static void ProbeFmt1(const wchar_t *fmt, const wchar_t *arg)
{
	wchar_t buf[4096];
	lstrcpynW(buf, fmt, 2000);
	lstrcatW(buf, arg ? arg : L"(null)");
	ProbeLine(buf);
}

static void ProbeFmtD(const wchar_t *fmt, int v)
{
	wchar_t buf[4096];
	wchar_t num[32];
	wsprintfW(num, L"%d", v);
	lstrcpynW(buf, fmt, 2000);
	lstrcatW(buf, num);
	ProbeLine(buf);
}

static void ProbeFmtH(const wchar_t *fmt, unsigned v)
{
	wchar_t buf[4096];
	wchar_t num[32];
	wsprintfW(num, L"%08X", v);
	lstrcpynW(buf, fmt, 2000);
	lstrcatW(buf, num);
	ProbeLine(buf);
}

static void ProbeUtc(void)
{
	SYSTEMTIME st;
	GetSystemTime(&st);
	wchar_t buf[128];
	wsprintfW(buf, L"utc=%04d-%02d-%02dT%02d:%02d:%02dZ", st.wYear, st.wMonth, st.wDay,
		st.wHour, st.wMinute, st.wSecond);
	ProbeLine(buf);
}

// ---------------------------------------------------------------- variant ----
// tTJSVariant 32 位: 8B union + 4B type(@8)（task-46 实测）。缓冲给 32B。
struct VarBuf
{
	union {
		struct { void *Object; void *ObjThis; } closure;
		tjs_int64 integer;
		double    real;
		void *    string;
		void *    octet;
	} u;
	unsigned long vt;     // @8
	unsigned long pad[5];
	// 崩溃调查（0xC0000409 / ___report_gsfailure，故障模块本 dll）：
	// 先把变体缓冲放大到 224B，并放尾部哨兵，用来排除/证实 "TJS 超写变体" 假说。
	unsigned long fill[40];
	unsigned long canary[8];
};
static const unsigned long kCanaryVal = 0xA5A5A5A5u;

static void VarBufInit(VarBuf *v)
{
	ZeroMemory(v, sizeof(*v));
	for(int i = 0; i < 40; ++i) v->fill[i] = 0x5A5A5A5Au;
	for(int i = 0; i < 8; ++i) v->canary[i] = kCanaryVal;
}

static int VarBufCanaryBroken(const VarBuf *v)
{
	for(int i = 0; i < 8; ++i) if(v->canary[i] != kCanaryVal) return i + 1;
	return 0;
}

struct TtstrPOD { void *Ptr; };

struct RunResult { int status; unsigned long vt; tjs_int64 ival; int canary; };

static RunResult RunVia(fnExecExpr fn, const tjs_char *src)
{
	RunResult rr;
	rr.status = 0; rr.vt = 0xFFFFFFFFu; rr.ival = 0; rr.canary = 0;
	if(!g_allocStr || !fn) return rr;

	void *vs = g_allocStr(src);
	if(!vs) { rr.status = -1; return rr; }

	TtstrPOD t;
	t.Ptr = vs;
	VarBuf r;
	VarBufInit(&r);
	rr.status = 1;
	try { fn(&t, &r); }
	catch(...) { rr.status = -2; }   // /EHa: C++ 与 SEH 都落到这里
	rr.canary = VarBufCanaryBroken(&r);
	if(rr.canary && !g_canaryLogged)
	{
		g_canaryLogged = 1;
		ProbeFmtD(L"variant_overrun_slot=", rr.canary);
		ProbeFmtH(L"variant_vt_seen=", (unsigned)r.vt);
	}
	rr.vt = r.vt;
	if(r.vt == 4)      rr.ival = r.u.integer;         // tvtInteger
	else if(r.vt == 5) rr.ival = (tjs_int64)r.u.real;  // tvtReal
	return rr;
}

static RunResult RunExpr(const tjs_char *src)   { return RunVia(g_execExpr, src); }
static RunResult RunScript(const tjs_char *src) { return RunVia(g_execScript, src); }

// tTJSVariantString: tjs_int RefCount@0 / tjs_char* LongString@4 / tjs_char ShortString[]@8
// 两个缓冲都以 0 结尾（tjsVariantString.h:99-122），所以只读指针 + 边界扫描即可，
// 不碰 Length 字段（其偏移依赖 TJS_VS_SHORT_LEN，未在本作确认）。
static int CopyVariantString(const VarBuf *v, wchar_t *out, int cap)
{
	out[0] = 0;
	if(!v || v->vt != 2 || !v->u.string || cap <= 1) return 0;
	const unsigned char *base = (const unsigned char *)v->u.string;
	void *longStr = *(void **)(base + 4);
	const wchar_t *src = longStr ? (const wchar_t *)longStr : (const wchar_t *)(base + 8);
	int i = 0;
	for(; i < cap - 1; ++i)
	{
		wchar_t c = src[i];
		if(c == 0) break;
		out[i] = c;
	}
	out[i] = 0;
	return i;
}

// 读取表达式并把结果当字符串取回
static int EvalStr(const wchar_t *expr, wchar_t *out, int cap)
{
	out[0] = 0;
	if(!g_allocStr || !g_execExpr) return 0;

	void *vs = g_allocStr(expr);
	if(!vs) return 0;
	TtstrPOD t;
	t.Ptr = vs;
	VarBuf r;
	VarBufInit(&r);
	int ok = 0;
	try { g_execExpr(&t, &r); ok = 1; }
	catch(...) { ok = 0; }
	int bad = VarBufCanaryBroken(&r);
	if(bad && !g_canaryLogged)
	{
		g_canaryLogged = 1;
		ProbeFmtD(L"variant_overrun_slot=", bad);
		ProbeFmtH(L"variant_vt_seen=", (unsigned)r.vt);
	}
	if(!ok || r.vt != 2) return 0;
	return CopyVariantString(&r, out, cap);
}

// 求值并回传 vt：1 = 拿到字符串（已拷入 out）；0 = 调用成功但结果不是字符串（看 vt/iv）；
// -1 = 调用抛异常；-2 = 分配 ttstr 失败。整个调用都在 try 里（TJS 的异常会真的抛到原生侧）。
static int EvalStrVt(const wchar_t *expr, wchar_t *out, int cap, unsigned *vtOut, tjs_int64 *ivOut)
{
	out[0] = 0;
	if(vtOut) *vtOut = 0xFFFFFFFFu;
	if(ivOut) *ivOut = 0;
	if(!g_allocStr || !g_execExpr) return -1;
	int ok = 0;
	VarBuf r;
	VarBufInit(&r);
	try
	{
		void *vs = g_allocStr(expr);
		if(!vs) return -2;
		TtstrPOD t;
		t.Ptr = vs;
		g_execExpr(&t, &r);
		ok = 1;
	}
	catch(...) { ok = 0; }
	if(vtOut) *vtOut = (unsigned)r.vt;
	if(ivOut && r.vt == 4) *ivOut = r.u.integer;
	int bad = VarBufCanaryBroken(&r);
	if(bad && !g_canaryLogged)
	{
		g_canaryLogged = 1;
		ProbeFmtD(L"variant_overrun_slot=", bad);
	}
	if(!ok) return -1;
	if(r.vt != 2) return 0;
	CopyVariantString(&r, out, cap);
	return 1;
}

// KAG 文本读取阶梯：先确认 global.kag / historyLayer / currentInfo 是否 void，再往下读文本。
// 注意：本作 TJS 里 typeof 不可用（v5 实测：typeof 表达式直接抛异常），所以用 == void 判定。
struct KagExpr { const wchar_t *src; const wchar_t *label; };
static const KagExpr kKagExpr[] = {
	{ L"1 + 1",                                                   L"baseline" },
	{ L"typeof(1)",                                               L"typeof_probe" },
	{ L"global.kag == void",                                      L"kag_void" },
	{ L"global.kag.historyLayer == void",                         L"hist_void" },
	{ L"global.kag.historyLayer.currentInfo == void",             L"ci_void" },
};
static const int kKagStageCount = (int)(sizeof(kKagExpr) / sizeof(kKagExpr[0]));

// ------------------------------------------------------------ TJS 钩子源码 ----
// 全部 ASCII，避免源码编码问题；文本本身走 wide char 回来。
// 分 3 步执行：首跑 0xC0000409 (BEX, 故障模块本 dll) 崩溃，需要确定死在哪一类构造上，
// 同时也让每一步都能独立重试。
static const wchar_t *kCoreS1 =
L"global.__kkz = new Object();"
L"global.__kkz.cnt = 0;"
L"global.__kkz.err = \"\";"
L"global.__kkz.hook = \"\";"
L"global.__kkz.lastKey = \"\";"
L"global.__kkz.rn = new Array();"
L"global.__kkz.rt = new Array();"
L"global.__kkz.rp = new Array();"
L"global.__kkz.rs = new Array();";

static const wchar_t *kCoreS2 =
L"global.__kkz_put = function(tag,n,t,p){"
L"  if(t == void || t == null) t = \"\";"
L"  t = \"\" + t;"
L"  if(t == \"\") return 0;"
L"  if(n == void || n == null) n = \"\"; else n = \"\" + n;"
L"  if(p == void || p == null) p = \"\"; else p = \"\" + p;"
L"  var k = tag + \"\\x02\" + n + \"\\x02\" + t;"
L"  if(k == global.__kkz.lastKey) return 0;"
L"  global.__kkz.lastKey = k;"
L"  var c = global.__kkz.cnt;"
L"  global.__kkz.rn[c] = n; global.__kkz.rt[c] = t; global.__kkz.rp[c] = p; global.__kkz.rs[c] = tag;"
L"  global.__kkz.cnt = c + 1;"
L"  return c + 1;"
L"};";

static const wchar_t *kCoreS3 =
L"global.__kkz_get = function(f,i){"
L"  var v = \"\";"
L"  try {"
L"    if(f == 0) v = global.__kkz.rn[i];"
L"    else if(f == 1) v = global.__kkz.rt[i];"
L"    else if(f == 2) v = global.__kkz.rp[i];"
L"    else v = global.__kkz.rs[i];"
L"    if(v == void || v == null) v = \"\"; else v = \"\" + v;"
L"  } catch(e) { v = \"\"; }"
L"  return v;"
L"};"
L"global.__kkz_poll = function(){"
L"  var r = 0;"
L"  try {"
L"    var h = global.kag.historyLayer;"
L"    if(h != void && h != null){"
L"      var ci = h.currentInfo;"
L"      if(ci != void && ci != null){"
L"        var n = \"\"; var t = \"\"; var p = \"\";"
L"        try { n = \"\" + ci.disp; } catch(e1) { n = \"\"; }"
L"        try { t = \"\" + ci.text; } catch(e2) { t = \"\"; }"
L"        try { p = \"\" + ci.plaintext; } catch(e3) { p = \"\"; }"
L"        r = global.__kkz_put(\"poll\", n, t, p);"
L"      }"
L"    }"
L"  } catch(e) { global.__kkz.err = \"\" + e; }"
L"  return r;"
L"};"
L"global.__kkz_ok = 1;";

static const wchar_t *kCoreSteps[3] = { kCoreS1, kCoreS2, kCoreS3 };

// 微探针：先用最小语句确认 "在 WM_TIMER 里调 TJS" 这条路本身通不通，
// 再谈装钩子。1 = 走 TVPExecuteExpression，0 = 走 TVPExecuteScript。
static const wchar_t *kMicro[] = {
	L"1 + 1",
	L"\"a\" + \"b\"",
	L"global.__kkz_t1 = 1;",
	L"global.__kkz_t2 = new Object();",
	L"global.__kkz_t3 = new Array();",
	L"global.__kkz_t4 = function(x){ return x + 1; };"
};
static const int kMicroIsExpr[] = { 1, 1, 0, 0, 0, 0 };
static const int kMicroCount = (int)(sizeof(kMicro) / sizeof(kMicro[0]));

// 真正的 TJS 包装：给 MessageLayer 实例挂 processCh（本作剧情不走它，装上作为对照证据）
static const wchar_t *kHookWrapProcessCh =
L"try {"
L"  if(global.__kkz != void && global.__kkz != null && global.__kkz.wrapDone != 1){"
L"  global.__kkz.wrapDone = 1;"
L"  if(global.kag != void && global.kag != null){"
L"    var fw = global.kag.fore;"
L"    if(fw != void && fw != null && fw.messages != void && fw.messages != null){"
L"      var ms = fw.messages; var i = 0;"
L"      while(i < ms.length){"
L"        var m = ms[i];"
L"        if(m != void && m != null && m.processCh != void && m.__kkz_orig == void){"
L"          m.__kkz_orig = m.processCh;"
L"          m.processCh = function(ch){ try { global.__kkz_put(\"processCh\", \"\", \"\" + ch, \"\"); } catch(x) {} return this.__kkz_orig(ch); };"
L"          global.__kkz.hook = global.__kkz.hook + \"+fore\";"
L"        }"
L"        i = i + 1;"
L"      }"
L"    }"
L"  }"
L"  }"
L"} catch(e) { global.__kkz.err = global.__kkz.err + \"|wrap:\" + e; }";

// ---------------------------------------------------------------- capture ----
static void WriteCaptureHeader(const wchar_t *hooks)
{
	wchar_t p[MAX_PATH + 32];
	CapturePath(p, MAX_PATH + 32);
	wchar_t buf[1024];
	SYSTEMTIME st;
	GetLocalTime(&st);
	wsprintfW(buf, L"# kkz_mvp session pid=%u %04d-%02d-%02d %02d:%02d:%02d hooks=%s",
		(unsigned)GetCurrentProcessId(), st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
		hooks ? hooks : L"");
	AppendFileUtf8(p, buf, (DWORD)lstrlenW(buf));
}

static void WriteCaptureLine(int seq, const wchar_t *tag, const wchar_t *name,
                             const wchar_t *text, const wchar_t *plain)
{
	wchar_t p[MAX_PATH + 32];
	CapturePath(p, MAX_PATH + 32);

	wchar_t buf[8192];
	int n = wsprintfW(buf, L"%d\t%s\t%s\t%s\t%s", seq, tag ? tag : L"", name ? name : L"",
		text ? text : L"", plain ? plain : L"");
	AppendFileUtf8(p, buf, (DWORD)n);
}

// ------------------------------------------------------------------ tick -----
// 环境探针（诊断用，放在拿到文本之后跑，避免诊断代码挡住主线）
static void ProbeEnvOnce(void)
{
	ProbeLine(L"---- env probe");
	wchar_t got[512];
	EvalStr(L"\"\" + typeof(global.kag)", got, 512);
	ProbeFmt1(L"env_kag_type=", got);
	EvalStr(L"\"\" + typeof(global.kag.historyLayer)", got, 512);
	ProbeFmt1(L"env_hist_type=", got);
	EvalStr(L"\"\" + typeof(global.kag.historyLayer.currentInfo)", got, 512);
	ProbeFmt1(L"env_ci_type=", got);
	EvalStr(L"\"\" + typeof(global.kag.fore.messages)", got, 512);
	ProbeFmt1(L"env_msgs_type=", got);
	RunResult m = RunExpr(L"global.kag.fore.messages.length");
	ProbeFmtD(L"env_msgs_len=", (int)m.ival);
}

static DWORD g_pumpTid = 0;
static int g_tidWarned = 0;

// ---- 可选 TTS: 进程内 SAPI (x86) ----
// 可行性依据: 本机 SDK 有 x86\SAPI.Lib 与 um\sapi.h; 进程是 x86, 语音引擎随系统 x86 组件安装.
static ISpVoice *g_voice = NULL;
static int  g_ttsState = 0;      // 0=未尝试 1=可用 -1=不可用
static int  g_ttsOk = 0, g_ttsErr = 0;
static wchar_t g_ttsVoice[256] = {0};

static void TtsInitOnce(void)
{
	if(g_ttsState != 0) return;
	g_ttsState = -1;             // 先置失败, 成功再置 1
	try
	{
		HRESULT hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
		if(FAILED(hr) && hr != RPC_E_CHANGED_MODE)
		{
			ProbeFmtH(L"tts_coinit_fail_hr=", (unsigned)hr);
			return;
		}
		ISpVoice *v = NULL;
		hr = CoCreateInstance(CLSID_SpVoice, NULL, CLSCTX_ALL, IID_ISpVoice, (void **)&v);
		if(FAILED(hr) || !v)
		{
			ProbeFmtH(L"tts_cocreate_fail_hr=", (unsigned)hr);
			return;
		}
		ISpObjectToken *tok = NULL;
		if(SUCCEEDED(v->GetVoice(&tok)) && tok)
		{
			LPWSTR desc = NULL;
			if(SUCCEEDED(tok->GetStringValue(NULL, &desc)) && desc)
			{
				lstrcpynW(g_ttsVoice, desc, 256);
				CoTaskMemFree(desc);
			}
			tok->Release();
		}
		g_voice = v;
		g_ttsState = 1;
		ProbeFmt1(L"tts_voice=", g_ttsVoice);
		ProbeLine(L"tts_init=ok");
	}
	catch(...)
	{
		ProbeLine(L"tts_init_cpp_exception");
		g_ttsState = -1;
	}
}

static void SpeakRow(const wchar_t *s)
{
	if(!s || !s[0]) return;
	if(g_ttsState == 0) TtsInitOnce();
	if(g_ttsState != 1 || !g_voice) return;
	try
	{
		HRESULT hr = g_voice->Speak(s, SPF_ASYNC | SPF_PURGEBEFORESPEAK | SPF_IS_NOT_XML, NULL);
		if(SUCCEEDED(hr))
		{
			if(++g_ttsOk <= 3) ProbeFmtD(L"tts_speak_ok_no=", g_ttsOk);
		}
		else
		{
			if(++g_ttsErr <= 3) ProbeFmtH(L"tts_speak_fail_hr=", (unsigned)hr);
		}
	}
	catch(...)
	{
		ProbeLine(L"tts_speak_cpp_exception");
		g_ttsState = -1;
	}
}
// ---- TTS 结束 ----

static void Tick(void)
{
	if(InterlockedExchange(&g_tickBusy, 1) == 1) return;
	if(!g_ready) { g_tickBusy = 0; return; }
	++g_ticks;

	if(g_pumpTid && GetCurrentThreadId() != g_pumpTid && !g_tidWarned)
	{
		g_tidWarned = 1;
		wchar_t b[160];
		wsprintfW(b, L"tick_tid_mismatch pump=%u cur=%u", (unsigned)g_pumpTid,
			(unsigned)GetCurrentThreadId());
		ProbeLine(b);
	}

	if(!g_firstTickLogged)
	{
		g_firstTickLogged = 1;
		wchar_t buf[256];
		wsprintfW(buf, L"first_tick tid=%u ticks=%d", (unsigned)GetCurrentThreadId(), g_ticks);
		ProbeLine(buf);
	}

	// 0) KAG 读取阶梯：只用表达式，不建任何 TJS 对象/函数
	//    证据：v4 探针里 micro 0/1/2 (1+1 / 字符串拼接 / 全局赋值) 全过，
	//    micro 3 = "new Object()" 直接把进程打成 0xC0000409(___report_gsfailure)。
	//    => 本机结论：从 WM_TIMER 里可以求值表达式，但不要在 TJS 侧分配对象。
	//    所以拼行/去重/落盘全部放在原生侧，TJS 只做属性读取。
	if(g_stage < kKagStageCount)
	{
		ProbeFmtD(L"kag_stage_in=", g_stage);
		wchar_t got[2048] = {0};
		unsigned vt = 0xFFFFFFFFu;
		tjs_int64 iv = 0;
		int st = EvalStrVt(kKagExpr[g_stage].src, got, 2048, &vt, &iv);
		wchar_t line[2400];
		wsprintfW(line, L"kag_out st=%d vt=%d iv=", st, (int)vt);
		{
			wchar_t num[32];
			wsprintfW(num, L"%d", (int)iv);
			lstrcatW(line, num);
		}
		lstrcatW(line, L" val=");
		lstrcatW(line, got);
		ProbeLine(line);
		if(st >= 0) { ++g_stage; ProbeFmtD(L"kag_stage_next=", g_stage); }
		g_tickBusy = 0;
		return;
	}

	if(!g_pollReadyLogged)
	{
		g_pollReadyLogged = 1;
		ProbeLine(L"poll=ladder_done start_capture");
	}

	// 1) 每拍读 currentInfo 的三段文本（原生侧拼行 + 去重）
	{
		wchar_t name[1024]  = {0};
		wchar_t text[4096]  = {0};
		wchar_t plain[4096] = {0};
		unsigned v1 = 0, v2 = 0, v3 = 0;
		int s1 = EvalStrVt(L"global.kag.historyLayer.currentInfo.disp", name, 1024, &v1, NULL);
		int s2 = EvalStrVt(L"global.kag.historyLayer.currentInfo.text", text, 4096, &v2, NULL);
		int s3 = EvalStrVt(L"global.kag.historyLayer.currentInfo.plaintext", plain, 4096, &v3, NULL);

		if(s1 < 0 || s2 < 0 || s3 < 0)
		{
			if(!g_pollFailLogged)
			{
				g_pollFailLogged = 1;
				wchar_t b[200];
				wsprintfW(b, L"read_fail s1=%d s2=%d s3=%d v1=%d v2=%d v3=%d", s1, s2, s3, (int)v1, (int)v2, (int)v3);
				ProbeLine(b);
			}
		}
		else if(lstrcmpW(text, g_lastText) != 0 || lstrcmpW(name, g_lastName) != 0 ||
		        lstrcmpW(plain, g_lastPlain) != 0)
		{
			lstrcpynW(g_lastName, name, 1024);
			lstrcpynW(g_lastText, text, 4096);
			lstrcpynW(g_lastPlain, plain, 4096);
			++g_rows;
			WriteCaptureLine(g_rows, L"poll", name, text, plain);
			SpeakRow(plain[0] ? plain : text);
			wchar_t b[300];
			wsprintfW(b, L"row=%d name_len=%d text_len=%d plain_len=%d",
				g_rows, lstrlenW(name), lstrlenW(text), lstrlenW(plain));
			ProbeLine(b);
		}
	}

	if((g_ticks % 50) == 0)
	{
		wchar_t buf[256];
		wsprintfW(buf, L"tick=%d rows=%d", g_ticks, g_rows);
		ProbeLine(buf);
	}
	g_tickBusy = 0;
}

static LRESULT CALLBACK MvpWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp)
{
	if(msg == WM_TIMER && wp == KKZ_TIMER_ID)
	{
		try { Tick(); } catch(...) { ProbeLine(L"tick_cpp_exception"); }
		return 0;
	}
	if(msg == WM_DESTROY)
	{
		KillTimer(hWnd, KKZ_TIMER_ID);
		return 0;
	}
	return DefWindowProcW(hWnd, msg, wp, lp);
}

static void EnsurePump(HINSTANCE hInst)
{
	WNDCLASSW wc;
	ZeroMemory(&wc, sizeof(wc));
	wc.lpfnWndProc = MvpWndProc;
	wc.hInstance = hInst;
	wc.lpszClassName = L"KkzMvpMsgWindow";
	RegisterClassW(&wc);

	// message-only 窗口: 不可见/不在任务栏/不抢焦点, 只作为定时器落点(同本作 a11y 补丁做法)
	HWND h = CreateWindowExW(0, L"KkzMvpMsgWindow", L"", 0, 0, 0, 0, 0,
		HWND_MESSAGE, NULL, hInst, NULL);
	if(!h)
	{
		ProbeFmtD(L"pump_fail err=", (int)GetLastError());
		return;
	}
	g_pumpTid = GetCurrentThreadId();
	SetTimer(h, KKZ_TIMER_ID, KKZ_POLL_MS, NULL);
	{
		DWORD wpid = 0;
		DWORD wtid = GetWindowThreadProcessId(h, &wpid);
		wchar_t b[160];
		wsprintfW(b, L"pump=ok timer=400ms hwnd=%08X wtid=%u cur_tid=%u",
			(unsigned)(UINT_PTR)h, (unsigned)wtid, (unsigned)GetCurrentThreadId());
		ProbeLine(b);
	}
}

// ---------------------------------------------------------------- V2Link -----
static DWORD GetSelfPath(wchar_t *buf, DWORD cap)
{
	HMODULE hm = NULL;
	if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(const void *)&GetSelfPath, &hm))
		return 0;
	return GetModuleFileNameW(hm, buf, cap);
}

static void *SafeGetProc(HMODULE m, const char *name)
{
	if(!m) return NULL;
	return (void *)GetProcAddress(m, name);
}

static int MakeOrigPath(const wchar_t *self, wchar_t *out)
{
	DWORD n = (DWORD)lstrlenW(self);
	if(n == 0 || n + 16 >= MAX_PATH) return 0;
	DWORD dot = n;
	while(dot > 0 && self[dot - 1] != L'.' && self[dot - 1] != L'\\') dot--;
	if(dot <= 1 || self[dot - 1] != L'.') return 0;

	DWORD p = 0;
	for(DWORD i = 0; i < dot - 1; ++i) out[p++] = self[i];
	const wchar_t *suffix = L"_orig";
	for(const wchar_t *q = suffix; *q; ++q) out[p++] = *q;
	for(DWORD i = dot - 1; i < n; ++i) out[p++] = self[i];
	out[p] = 0;
	return 1;
}

static void TjsSetup(void *exporter)
{
	if(g_probed) return;
	g_probed = 1;

	ProbeLine(L"---- kkz_mvp TjsSetup begin");
	ProbeUtc();
	ProbeFmtD(L"pid_tid=", (int)GetCurrentThreadId());
	if(!exporter) { ProbeLine(L"tjs=FAIL reason=null_exporter"); return; }

	iTVPFunctionExporter *exp = (iTVPFunctionExporter *)exporter;
	struct QItem { const char *name; void **slot; const wchar_t *label; };
	QItem items[] = {
		{ "iTJSDispatch2 * ::TVPGetScriptDispatch()",                       (void **)&g_getDispatch, L"TVPGetScriptDispatch" },
		{ "void ::TVPExecuteExpression(const ttstr &,tTJSVariant *)",      (void **)&g_execExpr,    L"TVPExecuteExpression" },
		{ "void ::TVPExecuteScript(const ttstr &,tTJSVariant *)",          (void **)&g_execScript,  L"TVPExecuteScript" },
		{ "tTJSVariantString * ::TJSAllocVariantString(const tjs_char *)", (void **)&g_allocStr,    L"TJSAllocVariantString" },
		{ "void ::TVPAddLog(const ttstr &)",                               (void **)&g_addLog,      L"TVPAddLog" },
	};
	const int N = (int)(sizeof(items) / sizeof(items[0]));
	int okCount = 0;
	for(int i = 0; i < N; ++i)
	{
		const char *nm = items[i].name;
		void *f = NULL;
		bool ok = false;
		try { ok = exp->QueryFunctionsByNarrowString(&nm, &f, 1); }
		catch(...) { ok = false; }
		*(items[i].slot) = f;
		if(f) okCount++;
		{
			wchar_t q[256];
			wsprintfW(q, L"Q %s f=%08X", items[i].label, (unsigned)(UINT_PTR)f);
			ProbeLine(q);
		}
	}
	ProbeFmtD(L"Q total=", okCount);

	if(!g_getDispatch) { ProbeLine(L"tjs=FAIL reason=no_TVPGetScriptDispatch"); return; }
	void *global = NULL;
	try { global = g_getDispatch(); } catch(...) { global = NULL; }
	if(!global) { ProbeLine(L"tjs=FAIL reason=global_null"); return; }
	if(!g_allocStr || !g_execExpr || !g_execScript) { ProbeLine(L"tjs=FAIL reason=no_exec"); return; }

	// --- 字符串读取自检（证明确实把 TJS 字符串拿回原生侧）---
	ProbeLine(L"selftest begin");
	RunScript(L"global.__kkz_selftest = \"KKZ_SELFTEST_ABC_0123456789\";");
	{
		wchar_t got[256];
		EvalStr(L"global.__kkz_selftest", got, 256);
		ProbeFmt1(L"selftest_ascii=", got);
		ProbeFmtD(L"selftest_ascii_ok=",
			(lstrcmpW(got, L"KKZ_SELFTEST_ABC_0123456789") == 0) ? 1 : 0);
	}
	RunScript(L"global.__kkz_selftest2 = \"\x5343\x6200\xFF0A\x842C\x82B1\x306E\x30C6\x30B9\x30C8\";");
	{
		wchar_t got[256];
		EvalStr(L"global.__kkz_selftest2", got, 256);
		ProbeFmt1(L"selftest_wide=", got);
	}

	// --- 环境探针 / TJS 钩子：都挪到 pump 的 WM_TIMER 里（此时 KAG 已加载）---
	// 首跑在这里直接 typeof(global.kag) 探针导致 0xC0000409 (BEX) 退出，
	// 事后判断是 V2Link 时机太早 + 变体缓冲可能被超写，故只保留已验证的最小子集。
	WriteCaptureHeader(L"boot");
	g_ready = 1;
	ProbeLine(L"tjs=OK funcs_ready");
	EnsurePump((HINSTANCE)GetModuleHandleW(NULL));
}

extern "C" HRESULT __stdcall V2Link(void *exporter)
{
	wchar_t self[MAX_PATH] = {0};
	wchar_t orig[MAX_PATH] = {0};
	DWORD selfLen = GetSelfPath(self, MAX_PATH);
	if(selfLen == 0 || selfLen >= MAX_PATH) self[0] = 0;

	int pathOk = self[0] ? MakeOrigPath(self, orig) : 0;
	DWORD err = 0;
	HRESULT hr = E_FAIL;
	int forwarded = 0;

	if(pathOk)
	{
		if(!g_orig)
		{
			SetLastError(0);
			g_orig = LoadLibraryW(orig);
			err = GetLastError();
			if(g_orig)
			{
				g_origV2Link = SafeGetProc(g_orig, "V2Link");
				g_origV2Unlink = SafeGetProc(g_orig, "V2Unlink");
				g_origThreadModel = SafeGetProc(g_orig, "GetModuleThreadModel");
			}
		}
		if(g_orig && g_origV2Link)
		{
			forwarded = 1;
			typedef HRESULT (__stdcall *fnLink)(void *);
			hr = ((fnLink)g_origV2Link)(exporter);
		}
	}
	else err = ERROR_PATH_NOT_FOUND;

	ProbeLine(L"==== V2Link");
	ProbeUtc();
	ProbeFmt1(L"self=", self);
	ProbeFmt1(L"orig=", orig);
	ProbeFmtD(L"load_ok=", g_orig ? 1 : 0);
	ProbeFmtD(L"lasterr=", (int)err);
	ProbeFmtD(L"forwarded=", forwarded);
	ProbeFmtD(L"forward_ret=", (int)hr);

	if(forwarded)
	{
		try { TjsSetup(exporter); }
		catch(...) { ProbeLine(L"tjs=FAIL reason=setup_cpp_exception"); }
	}
	return hr;
}

extern "C" HRESULT __stdcall V2Unlink(void)
{
	ProbeLine(L"==== V2Unlink");
	ProbeFmtD(L"ticks=", g_ticks);
	ProbeFmtD(L"seen=", g_seen);
	if(g_origV2Unlink)
	{
		typedef HRESULT (__stdcall *fnUnlink)(void);
		HRESULT hr = ((fnUnlink)g_origV2Unlink)();
		return hr;
	}
	return S_OK;
}

extern "C" int __stdcall GetModuleThreadModel(void)
{
	if(g_origThreadModel)
	{
		typedef int (__stdcall *fnTm)(void);
		return ((fnTm)g_origThreadModel)();
	}
	return 0;
}

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID)
{
	return TRUE;
}
