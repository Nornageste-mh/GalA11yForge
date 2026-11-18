// kkr_proxy2.cpp -- task-46 / R25
// SenrenBanka (KiriKiri Z 1.2.0.3, x86, Steam DRM) 上的第二代插件代理。
//
// 与 task-45 的 kkr_dllproxy.cpp 的差别：
//   1) 除了转发 V2Link/V2Unlink，还在 V2Link 里通过同一个 exporter 拿到 TJS 层函数指针：
//        iTJSDispatch2 * ::TVPGetScriptDispatch()
//        void ::TVPExecuteExpression(const ttstr &,tTJSVariant *)
//        void ::TVPExecuteScript(const ttstr &,tTJSVariant *)
//        tTJSVariantString * ::TJSAllocVariantString(const tjs_char *)
//        void ::TVPAddLog(const ttstr &)
//        tTJSNativeClassMethod * ::TJSCreateNativeClassMethod(...)
//      名字来自本作自带 plugin\getSample.dll / menu.dll 里的字符串（1.2.0.3 的插件 SDK
//      在编译期就把这些签名串写进 DLL），并与 krkrz 源码 FuncStubs.cpp 的导出名表核对过。
//   2) 在 V2Link 里执行 TJS 并读回结果写 marker：证明代理能进入脚本层。
//
// 约束：
//   - 不 import 任何 TVP 符号（imports 只有 KERNEL32/USER32），避免链接期 ABI 依赖。
//   - tTJSVariant 布局按 tjs2 32 位实际布局手写：8 字节 union + 4 字节类型（类型在 offset 8），
//     缓冲区给到 32 字节，避免引擎写入越界。
//   - 只用 Win32 API 记日志（CRT 在 V2Link 早期不安全的教训见 research/34）。
//   - 任何脚本异常 catch(...)，不让异常穿回引擎。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdarg.h>
#pragma comment(lib, "user32.lib")

typedef int            tjs_int;
typedef unsigned int   tjs_uint;
typedef wchar_t        tjs_char;
typedef __int64        tjs_int64;

// tjsTypes.h: #define TJS_INTF_METHOD __cdecl  (MSVC, since TJS2 2.4.14)
struct iTVPFunctionExporter
{
	virtual bool __cdecl QueryFunctions(const tjs_char **name, void **function, tjs_uint count) = 0;
	virtual bool __cdecl QueryFunctionsByNarrowString(const char **name, void **function, tjs_uint count) = 0;
};

typedef void   (__cdecl *fnExecExpr)(const void *ttstrRef, void *variantOut);
typedef void * (__cdecl *fnAllocStr)(const tjs_char *s);
typedef void * (__cdecl *fnGetScriptDispatch)(void);
typedef void   (__cdecl *fnAddLog)(const void *ttstrRef);
typedef void * (__cdecl *fnCreateNativeMethod)(void *cb);

static fnExecExpr           g_execExpr = NULL;   // TVPExecuteExpression(const ttstr&, tTJSVariant*)
static fnExecExpr           g_execScript = NULL; // TVPExecuteScript(const ttstr&, tTJSVariant*)
static fnAllocStr           g_allocStr = NULL;   // TJSAllocVariantString(const tjs_char*)
static fnGetScriptDispatch  g_getDispatch = NULL;
static fnAddLog             g_addLog = NULL;
static fnCreateNativeMethod g_createNativeMethod = NULL;

static HMODULE g_orig = NULL;
static void *  g_origV2Link = NULL;
static void *  g_origV2Unlink = NULL;
static void *  g_origThreadModel = NULL;
static int     g_probed = 0;

// ---------------------------------------------------------------- logging ----
static void MarkerPath(wchar_t *out, DWORD cap)
{
	wchar_t exe[MAX_PATH] = {0};
	DWORD n = GetModuleFileNameW(NULL, exe, MAX_PATH);
	DWORD cut = n;
	while(cut > 0 && exe[cut - 1] != L'\\') cut--;
	if(cut == 0 || cut + 32 >= cap) { out[0] = 0; return; }
	DWORD i = 0;
	for(; i < cut && i + 1 < cap; ++i) out[i] = exe[i];
	out[i] = 0;
	lstrcatW(out, L"kkr_proxy2_probe.txt");
}

static void LogLine(const wchar_t *fmt, ...)
{
	wchar_t path[MAX_PATH + 32];
	MarkerPath(path, MAX_PATH + 32);
	if(path[0] == 0) return;

	wchar_t buf[2048];
	va_list ap;
	va_start(ap, fmt);
	wvsprintfW(buf, fmt, ap);
	va_end(ap);

	char u8[4096];
	int n = WideCharToMultiByte(CP_UTF8, 0, buf, -1, u8, sizeof(u8) - 2, NULL, NULL);
	if(n <= 1) return;
	u8[n - 1] = '\r';
	u8[n] = '\n';

	HANDLE h = CreateFileW(path, FILE_APPEND_DATA,
		FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS,
		FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return;
	DWORD wrote = 0;
	WriteFile(h, u8, (DWORD)n + 1, &wrote, NULL);
	CloseHandle(h);
}

static void LogUtc(void)
{
	SYSTEMTIME st;
	GetSystemTime(&st);
	LogLine(L"utc=%04d-%02d-%02dT%02d:%02d:%02dZ", st.wYear, st.wMonth, st.wDay,
		st.wHour, st.wMinute, st.wSecond);
}

// ---- 变体读取 ----
struct VarBuf
{
	union {
		struct { void *Object; void *ObjThis; } closure;
		tjs_int64 integer;
		double    real;
		void *    string;
		void *    octet;
	} u;
	unsigned long vt;     // offset 8
	unsigned long pad[5];
};

struct TtstrPOD { void *Ptr; };   // tTJSString 只有一个成员 tTJSVariantString *Ptr

struct RunResult { int status; unsigned long vt; tjs_int64 ival; };

static RunResult RunScript(const tjs_char *src)
{
	RunResult rr;
	rr.status = 0; rr.vt = 0xFFFFFFFFu; rr.ival = 0;
	if(!g_allocStr || !g_execExpr) return rr;

	void *vs = g_allocStr(src);
	if(!vs) { rr.status = -1; return rr; }

	TtstrPOD t;
	t.Ptr = vs;
	VarBuf r;
	ZeroMemory(&r, sizeof(r));
	rr.status = 1;
	try
	{
		g_execExpr(&t, &r);
	}
	catch(...)
	{
		rr.status = -2;
	}
	rr.vt = r.vt;
	if(r.vt == 4)      rr.ival = r.u.integer;          // tvtInteger
	else if(r.vt == 5) rr.ival = (tjs_int64)r.u.real;   // tvtReal
	return rr;
}

// ------------------------------------------------------------ TJS 探针 ------
static int TjsProbe(void *exporter, const wchar_t *self)
{
	if(g_probed) return 1;
	g_probed = 1;

	LogLine(L"---- TjsProbe begin");
	LogLine(L"self=%s", self);
	if(!exporter) { LogLine(L"tjs=FAIL reason=null_exporter"); return 0; }

	iTVPFunctionExporter *exp = (iTVPFunctionExporter *)exporter;
	unsigned vtable = *(unsigned *)exporter;
	unsigned e0 = vtable ? ((unsigned *)vtable)[0] : 0;
	unsigned e1 = vtable ? ((unsigned *)vtable)[1] : 0;
	LogLine(L"exporter=%08X vtable=%08X entry0=%08X entry1=%08X",
		(unsigned)(UINT_PTR)exporter, vtable, e0, e1);

	struct QItem { const char *name; void **slot; const wchar_t *label; };
	QItem items[] = {
		{ "iTJSDispatch2 * ::TVPGetScriptDispatch()",                       (void **)&g_getDispatch,        L"TVPGetScriptDispatch" },
		{ "void ::TVPExecuteExpression(const ttstr &,tTJSVariant *)",      (void **)&g_execExpr,           L"TVPExecuteExpression" },
		{ "void ::TVPExecuteScript(const ttstr &,tTJSVariant *)",          (void **)&g_execScript,         L"TVPExecuteScript" },
		{ "tTJSVariantString * ::TJSAllocVariantString(const tjs_char *)", (void **)&g_allocStr,           L"TJSAllocVariantString" },
		{ "void ::TVPAddLog(const ttstr &)",                               (void **)&g_addLog,             L"TVPAddLog" },
		{ "tTJSNativeClassMethod * ::TJSCreateNativeClassMethod(tTJSNativeClassMethodCallback)",
		                                                                   (void **)&g_createNativeMethod, L"TJSCreateNativeClassMethod" },
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
		LogLine(L"Q %s ok=%d f=%08X", items[i].label, (int)ok, (unsigned)(UINT_PTR)f);
	}
	LogLine(L"Q total_ok=%d of %d", okCount, N);

	if(!g_getDispatch) { LogLine(L"tjs=FAIL reason=no_TVPGetScriptDispatch"); return 0; }

	void *global = NULL;
	try { global = g_getDispatch(); }
	catch(...) { global = NULL; }
	LogLine(L"global=%08X", (unsigned)(UINT_PTR)global);
	if(!global) { LogLine(L"tjs=FAIL reason=global_null_engine_not_ready"); return 0; }

	if(!g_allocStr || !g_execExpr)
	{
		LogLine(L"tjs=FAIL reason=no_exec_or_alloc");
		return 0;
	}

	RunResult r1 = RunScript(L"global.__kkr_probe = 123;");
	LogLine(L"S1_set status=%d", r1.status);

	RunResult r2 = RunScript(L"global.__kkr_probe");
	LogLine(L"S2_read status=%d vt=%lu i=%d", r2.status, r2.vt, (int)r2.ival);

	RunResult r3 = RunScript(L"global.__kkr_probe = global.__kkr_probe + 1; global.__kkr_probe");
	LogLine(L"S3_incr status=%d vt=%lu i=%d", r3.status, r3.vt, (int)r3.ival);

	RunResult r4 = RunScript(
		L"class KkrProbe { function hello() { return 7; } };"
		L"global.__kkr_cls = (new KkrProbe()).hello();");
	RunResult r4v = RunScript(L"global.__kkr_cls");
	LogLine(L"S4_class status=%d cls_vt=%lu cls_i=%d", r4.status, r4v.vt, (int)r4v.ival);

	RunResult r5 = RunScript(
		L"global.__kkr_catch = 0;"
		L"try { throw new Error('kkrprobe'); } catch(e) { global.__kkr_catch = 1; }");
	RunResult r5v = RunScript(L"global.__kkr_catch");
	LogLine(L"S5_exc status=%d catch_vt=%lu catch_i=%d", r5.status, r5v.vt, (int)r5v.ival);

	RunResult r6 = RunScript(L"typeof(global) == 'object' ? 1 : 0");
	LogLine(L"S6_globalobj status=%d vt=%lu i=%d", r6.status, r6.vt, (int)r6.ival);

	int tjsOk = (r2.status == 1 && r2.vt == 4 && r2.ival == 123 &&
	             r3.status == 1 && r3.vt == 4 && r3.ival == 124 &&
	             r4v.status == 1 && r4v.vt == 4 && r4v.ival == 7) ? 1 : 0;
	LogLine(L"tjs=%s", tjsOk ? L"OK" : L"FAIL");
	return tjsOk;
}

// ---------------------------------------------------------------- 路径 ------
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

// self: <dir>\<stem>.dll -> orig: <dir>\<stem>_orig.dll
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

	LogLine(L"==== V2Link");
	LogUtc();
	LogLine(L"pid=%u tid=%u", (unsigned)GetCurrentProcessId(), (unsigned)GetCurrentThreadId());
	LogLine(L"self=%s", self);
	LogLine(L"orig=%s", orig);
	LogLine(L"load=%d lasterr=%u origv2link=%d ret=0x%08X",
		g_orig ? 1 : 0, err, forwarded, (unsigned)hr);

	// 转发之后再碰 TJS：让原件先完成自己的注册
	if(forwarded)
	{
		try { TjsProbe(exporter, self); }
		catch(...) { LogLine(L"tjs=FAIL reason=probe_cpp_exception"); }
	}
	return hr;
}

extern "C" HRESULT __stdcall V2Unlink(void)
{
	wchar_t self[MAX_PATH] = {0};
	GetSelfPath(self, MAX_PATH);
	LogLine(L"==== V2Unlink self=%s", self);
	if(g_origV2Unlink)
	{
		typedef HRESULT (__stdcall *fnUnlink)(void);
		HRESULT hr = ((fnUnlink)g_origV2Unlink)();
		LogLine(L"unlink ret=0x%08X", (unsigned)hr);
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
