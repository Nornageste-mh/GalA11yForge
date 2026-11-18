// gal_a11y.cpp -- KiriKiri Z 1.4.0.8 TPM 最小实验
// 目的（task-41）：
//   1) 证明 plugin\*.tpm 在 startup.tjs 之前自动加载；
//   2) 在 TPM 的 V2Link 里通过 TVPExecuteExpression 安装 KAG3 TJS hook；
//   3) 证明 TPM 创建的原生方法对象能挂到 TJS 全局并被 TJS 调用。
// 仅实验，不含任何游戏资源/台词。所有 TJS ABI 用 POD 手写，避免链接 tjs2.lib。
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <stdint.h>

#include "tjsCommHead.h"
#include "tjsInterface.h"

// ---------------------------------------------------------------- exporter ---
struct iTVPFunctionExporter
{
	virtual bool TJS_INTF_METHOD QueryFunctions(const tjs_char **name, void **function, tjs_uint count) = 0;
	virtual bool TJS_INTF_METHOD QueryFunctionsByNarrowString(const char **name, void **function, tjs_uint count) = 0;
};

typedef void *(__stdcall *GetScriptDispatchFn)(void);
typedef void (__stdcall *ExecuteExpressionFn)(const void *expr, void *result);
typedef tTJSVariantString *(__stdcall *AllocVariantStringFn)(const tjs_char *str);
typedef void *(__stdcall *CreateNativeMethodFn)(void *callback);

static GetScriptDispatchFn  g_getScriptDispatch = NULL;
static ExecuteExpressionFn  g_executeExpression = NULL;
static AllocVariantStringFn g_allocVariantString = NULL;
static CreateNativeMethodFn g_createNativeMethod = NULL;
static iTJSDispatch2       *g_global = NULL;

// ---------------------------------------------------------------- POD ABI ----
#pragma pack(push, 4)
typedef struct _VarPOD {
	union {
		struct { void *Object; void *ObjThis; } closure;
		__int64 integer;
		double real;
		tTJSVariantString *string;
		void *octet;
	} u;
	unsigned long vt;
} VarPOD;

typedef struct _VSPOD {
	tjs_int RefCount;
	tjs_char *LongString;
	tjs_char ShortString[22];
	tjs_int Length;
	tjs_uint32 HeapFlag;
	tjs_uint32 Hint;
} VSPOD;
#pragma pack(pop)

struct TJSStrPOD { void *Ptr; }; // tTJSString 只有一个 tTJSVariantString* 成员

// tTJSVariantType: 0=void, 1=object, 2=string, 3=octet, 4=integer, 5=real
#define VT_VOID   0
#define VT_OBJECT 1
#define VT_STRING 2

typedef tjs_int (__cdecl *NativeCallback)(void *result, tjs_int numparams, void **param, void *objthis);

// ---------------------------------------------------------------- logging ----
// 只用 Win32 文件 API 记日志：避免 CRT/静态初始化在插件加载早期引发问题。
// 每次调用重新打开文件，代价可接受（实验日志只有几十行）。
static void logLineW(const wchar_t *s)
{
	if(!s) return;
	wchar_t exe[MAX_PATH] = {0};
	GetModuleFileNameW(NULL, exe, MAX_PATH);
	wchar_t *slash = wcsrchr(exe, L'\\');
	if(slash) *slash = 0;
	wchar_t path[MAX_PATH + 32];
	_snwprintf_s(path, _countof(path), _TRUNCATE, L"%s\\gal_a11y.log", exe);

	HANDLE h = CreateFileW(path, FILE_APPEND_DATA,
		FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS,
		FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return;
	char buf[4096];
	int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, buf, sizeof(buf), NULL, NULL);
	DWORD written = 0;
	if(n > 1) WriteFile(h, buf, (DWORD)(n - 1), &written, NULL);
	WriteFile(h, "\r\n", 2, &written, NULL);
	CloseHandle(h);
}

static void logf(const wchar_t *fmt, ...)
{
	wchar_t buf[2048];
	va_list ap;
	va_start(ap, fmt);
	wvsprintfW(buf, fmt, ap);
	va_end(ap);
	logLineW(buf);
}

static void logOpen(void)
{
	logLineW(L"==== V2Link ENTER ====");
}

static void logw(const wchar_t *s)
{
	logLineW(s);
}

// ------------------------------------------------------- native TJS method ---
static tjs_int __cdecl nativeLog(void *result, tjs_int numparams, void **param, void *objthis)
{
	(void)objthis;
	if(numparams >= 1 && param && param[0])
	{
		VarPOD *v = (VarPOD *)param[0];
		if(v->vt == VT_STRING && v->u.string)
		{
			VSPOD *s = (VSPOD *)v->u.string;
			logw(s->LongString ? s->LongString : s->ShortString);
		}
	}
	if(result) { VarPOD *r = (VarPOD *)result; memset(r, 0, sizeof(*r)); r->vt = VT_VOID; }
	return 0; // TJS_S_OK
}

// ---------------------------------------------------------------- bootstrap --
static const tjs_char *BOOTSTRAP =
L"(function(){"
L"  global.galA11yBuf = '';"
L"  global.galA11yTicks = 0;"
L"  GalA11yLog('BOOT: V2Link TJS ready, typeof(global.kag)=' + typeof(global.kag));"
L"  global.galA11yInstall = function(){"
L"    GalA11yLog('I1_ENTER');"
L"    var kag = global.kag;"
L"    if(typeof(kag) == 'undefined') return false;"
L"    if(!kag) return false;"
L"    GalA11yLog('I2_KAG');"
L"    if(typeof(kag.fore) == 'undefined' || typeof(kag.fore.messages) == 'undefined') return false;"
L"    GalA11yLog('I3_FORE');"
L"    var ms = [];"
L"    var i;"
L"    for(i = 0; i < kag.fore.messages.count; i++) ms.push(kag.fore.messages[i]);"
L"    GalA11yLog('I4_FORE_MS=' + ms.length);"
L"    GalA11yLog('I4B_BACK');"
L"    if(typeof(kag.back) != 'undefined' && typeof(kag.back.messages) != 'undefined')"
L"      for(i = 0; i < kag.back.messages.count; i++) ms.push(kag.back.messages[i]);"
L"    GalA11yLog('I4C_ALL_MS=' + ms.length);"
L"    var hooked = 0;"
L"    for(i = 0; i < ms.length; i++){"
L"      GalA11yLog('I5_LOOP' + i);"
L"      var m = ms[i];"
L"      GalA11yLog('I6_ORIG' + i);"
L"      m.__galA11yOrigCh = m.processCh;"
L"      m.processCh = function(ch){"
L"        var r = this.__galA11yOrigCh(ch);"
L"        try { global.galA11yBuf += ch; } catch(e) {}"
L"        return r;"
L"      };"
L"      m.__galA11yOrigRet = m.processReturn;"
L"      m.processReturn = function(){"
L"        var r = this.__galA11yOrigRet();"
L"        try { if(global.galA11yBuf != '') { GalA11yLog('LINE:' + global.galA11yBuf); global.galA11yBuf=''; } } catch(e) {}"
L"        return r;"
L"      };"
L"      m.__galA11yOrigClear = m.clear;"
L"      m.clear = function(){"
L"        try { if(global.galA11yBuf != '') { GalA11yLog('CLEAR_FLUSH:' + global.galA11yBuf); global.galA11yBuf=''; } } catch(e) {}"
L"        return this.__galA11yOrigClear();"
L"      };"
L"      m.__galA11yHooked = true;"
L"      hooked++;"
L"    }"
L"    if(hooked > 0) GalA11yLog('HOOK_INSTALLED:' + hooked + ' of ' + ms.length);"
L"    return hooked > 0;"
L"  };"
L"  global.galA11yTick = function(){"
L"    try {"
L"      global.galA11yTicks = (global.galA11yTicks ? global.galA11yTicks : 0) + 1;"
L"      if(global.galA11yTicks < 4 || (global.galA11yTicks % 20) == 0)"
L"        GalA11yLog('TICK#' + global.galA11yTicks + ' kag=' + typeof(global.kag) + ' KAGWindow=' + typeof(global.KAGWindow));"
L"      if(global.galA11yInstall()) { if(global.galA11yTimer) global.galA11yTimer.enabled = false; }"
L"    } catch(e) { GalA11yLog('TICK_ERR:' + e.message + ' @' + e.line); }"
L"  };"
L"  global.galA11yTimer = new Timer(function(){ global.galA11yTick(); }, '');"
L"  GalA11yLog('TIMER_OBJ=' + typeof(global.galA11yTimer) + ' TICK=' + typeof(global.galA11yTick));"
L"  global.galA11yTimer.interval = 100;"
L"  global.galA11yTimer.enabled = true;"
L"  GalA11yLog('TIMER_STARTED');"
L"})()";

// ---------------------------------------------------------------- V2Link -----
extern "C" __declspec(dllexport) HRESULT __stdcall V2Link(void *exporterptr)
{
	logOpen();
	if(!exporterptr) { logf(L"no exporter"); return E_FAIL; }
	iTVPFunctionExporter *exp = (iTVPFunctionExporter *)exporterptr;

	struct { const char *name; void **slot; } q[] = {
		{"iTJSDispatch2 * ::TVPGetScriptDispatch()", (void **)&g_getScriptDispatch},
		{"void ::TVPExecuteExpression(const ttstr &,tTJSVariant *)", (void **)&g_executeExpression},
		{"tTJSVariantString * ::TJSAllocVariantString(const tjs_char *)", (void **)&g_allocVariantString},
		{"tTJSNativeClassMethod * ::TJSCreateNativeClassMethod(tTJSNativeClassMethodCallback)", (void **)&g_createNativeMethod},
	};
	for(int i = 0; i < 4; i++)
	{
		const char *n = q[i].name; void *f = NULL;
		bool ok = exp->QueryFunctionsByNarrowString(&n, &f, 1);
		*(q[i].slot) = f;
		logf(L"Q[%d] -> %08X ok=%d", i, (unsigned)(uintptr_t)f, (int)ok);
	}
	if(!g_getScriptDispatch || !g_executeExpression || !g_allocVariantString || !g_createNativeMethod)
	{
		logf(L"FATAL: missing core export");
		return E_FAIL;
	}

	g_global = (iTJSDispatch2 *)g_getScriptDispatch();
	logf(L"global=%08X", (unsigned)(uintptr_t)g_global);
	if(!g_global) return E_FAIL;

	// 原生方法对象 -> 全局 GalA11yLog
	void *fn = g_createNativeMethod((void *)nativeLog);
	logf(L"native method=%08X", (unsigned)(uintptr_t)fn);
	if(fn)
	{
		VarPOD val; memset(&val, 0, sizeof(val));
		val.u.closure.Object = fn;
		val.u.closure.ObjThis = NULL;
		val.vt = VT_OBJECT;
		tjs_error hr = g_global->PropSet(TJS_MEMBERENSURE | TJS_IGNOREPROP,
			TJS_W("GalA11yLog"), NULL, (const tTJSVariant *)&val, g_global);
		logf(L"PropSet GalA11yLog hr=%d", (int)hr);
	}
	else logf(L"createNativeMethod returned NULL");

	// 执行 bootstrap；脚本包在 TJS try/catch 里。
	TJSStrPOD script;
	script.Ptr = g_allocVariantString(BOOTSTRAP);
	logf(L"bootstrap variant=%08X", (unsigned)(uintptr_t)script.Ptr);
	if(script.Ptr)
	{
		VarPOD result; memset(&result, 0, sizeof(result)); result.vt = VT_VOID;
		g_executeExpression(&script, &result);
		logf(L"bootstrap returned vt=%lu", result.vt);
	}
	else logf(L"alloc bootstrap failed");

	return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall V2Unlink(void)
{
	logf(L"==== V2Unlink ====");
	return S_OK;
}

extern "C" __declspec(dllexport) int __stdcall GetModuleThreadModel(void)
{
	return 0; // mtMightUse
}
