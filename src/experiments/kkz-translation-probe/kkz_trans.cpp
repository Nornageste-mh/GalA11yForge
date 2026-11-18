// kkz_trans.cpp -- task-48 / R27
// SenrenBanka (KiriKiri Z 1.2.0.3, x86) 上的“中文译文源定位 + 台词原件重抓”探针。
//
// 与 task-47 的 kkz_mvp.cpp 同一条可信通道（已在 task-46/47 实测）：
//   plugin\getSample.dll 代理 -> 转发 V2Link -> exporter 取 5 个 TVP/TJS 函数
//   -> message-only 窗口 + SetTimer 回到引擎主线程 -> TVPExecuteScript/Expression 只读求值。
//
// 本轮相对 v7 的改动（只改取文本这一件事）：
//   1) 落盘路径改成工作区固定目录（不再用 %TEMP%），避免下一次启动把原件覆盖掉；
//      每行 WriteFile 后立即 CloseHandle，进程被杀也保住已抓到的行。
//   2) 阶段化“字段发现”：先 for-in 枚举 currentInfo 的键，再逐字段取 vt，
//      再查 renderMsgwinPlugin.currentText、historyLayer.data、SystemConfig 语言项。
//   3) 每轮询一次同时取 currentInfo 三字段 + data[末条] 两字段 + render 当前文本，
//      任一变化就落一行（native 侧去重），这样不管中文在哪条路径上都能被抓到。
//
// 硬约束（task-47 实测教训，勿违反）：
//   · TJS 侧禁止 new Object()/new Array()  -> 0xC0000409 (___report_gsfailure)
//   · 禁止 typeof(<对象>)                  -> 抛 C++ 异常（typeof(1) 正常）
//   · 所有状态/缓冲/去重/落盘在原生侧；TJS 只做“读属性 + 字符串拼接”
//   · 例外：本轮为了枚举键，用到 for(var k in obj) 与字符串拼接；不分配对象。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

typedef int            tjs_int;
typedef unsigned int   tjs_uint;
typedef wchar_t        tjs_char;
typedef __int64        tjs_int64;

struct iTVPFunctionExporter
{
	virtual bool __cdecl QueryFunctions(const tjs_char **name, void **function, tjs_uint count) = 0;
	virtual bool __cdecl QueryFunctionsByNarrowString(const char **name, void **function, tjs_uint count) = 0;
};

typedef void   (__cdecl *fnExecExpr)(const void *ttstrRef, void *variantOut);
typedef void * (__cdecl *fnAllocStr)(const tjs_char *s);
typedef void * (__cdecl *fnGetScriptDispatch)(void);
typedef void   (__cdecl *fnAddLog)(const void *ttstrRef);

static fnExecExpr          g_execExpr    = NULL;
static fnExecExpr          g_execScript  = NULL;
static fnAllocStr          g_allocStr    = NULL;
static fnGetScriptDispatch g_getDispatch = NULL;
static fnAddLog            g_addLog      = NULL;

static HMODULE g_orig = NULL;
static void *  g_origV2Link = NULL;
static void *  g_origV2Unlink = NULL;
static void *  g_origThreadModel = NULL;
static int     g_probed = 0;
static int     g_ready = 0;

#define KKZ_TIMER_ID   0x4B5B
#define KKZ_POLL_MS    400

// 输出目录：工作区里的任务目录，游戏进程直接写（不经过 %TEMP%）
static const wchar_t *kOutDir = L"D:\\DSHWorkSpace\\_kkz-trans2\\out\\";

static volatile LONG g_tickBusy = 0;
static int g_ticks = 0;
static int g_firstTickLogged = 0;
static int g_canaryLogged = 0;
static int g_stage = 0;
static int g_rows = 0;
static int g_histLen = -1;
static int g_renLen = -1;
static int g_pollFailLogged = 0;
static int g_lastLogTick = 0;

static wchar_t g_lastKey[8192] = {0};

// ---------------------------------------------------------------- paths ------
static void OutPath(wchar_t *out, DWORD cap, const wchar_t *name)
{
	out[0] = 0;
	lstrcpynW(out, kOutDir, (int)cap);
	lstrcatW(out, name);
}

// ---------------------------------------------------------------- logging ----
static void AppendFileUtf8(const wchar_t *path, const wchar_t *text, DWORD textLen)
{
	if(!path || !path[0] || !text) return;
	char u8[16384];
	int need = WideCharToMultiByte(CP_UTF8, 0, text, (int)textLen, NULL, 0, NULL, NULL);
	if(need <= 0) return;
	if(need > (int)sizeof(u8) - 8) need = (int)sizeof(u8) - 8;
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

static void WriteTo(const wchar_t *fname, const wchar_t *s)
{
	wchar_t p[512];
	OutPath(p, 512, fname);
	AppendFileUtf8(p, s, (DWORD)lstrlenW(s));
}

static void Log(const wchar_t *s) { WriteTo(L"probe.txt", s); }

// 只拼 <=2 段，避免 wsprintfW 多参问题（task-47 踩坑）
static void LogStr(const wchar_t *prefix, const wchar_t *val)
{
	wchar_t buf[8192];
	lstrcpynW(buf, prefix, 4000);
	lstrcatW(buf, val ? val : L"(null)");
	Log(buf);
}

static void LogD(const wchar_t *prefix, int v)
{
	wchar_t buf[1024]; wchar_t num[32];
	wsprintfW(num, L"%d", v);
	lstrcpynW(buf, prefix, 900);
	lstrcatW(buf, num);
	Log(buf);
}

static void LogH(const wchar_t *prefix, unsigned v)
{
	wchar_t buf[1024]; wchar_t num[32];
	wsprintfW(num, L"%08X", v);
	lstrcpynW(buf, prefix, 900);
	lstrcatW(buf, num);
	Log(buf);
}

static void LogUtc(void)
{
	SYSTEMTIME st; GetSystemTime(&st);
	wchar_t buf[128];
	wsprintfW(buf, L"utc=%04d-%02d-%02dT%02d:%02d:%02dZ", st.wYear, st.wMonth, st.wDay,
		st.wHour, st.wMinute, st.wSecond);
	Log(buf);
}

// ---------------------------------------------------------------- variant ----
struct VarBuf
{
	union {
		struct { void *Object; void *ObjThis; } closure;
		tjs_int64 integer;
		double    real;
		void *    string;
		void *    octet;
	} u;
	unsigned long vt;     // @8 : 2=string 4=int 5=real
	unsigned long pad[5];
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

// tTJSVariantString: RefCount@0 / LongString@4 / ShortString[]@8，两缓冲都以 0 结尾
static int CopyVariantString(const VarBuf *v, wchar_t *out, int cap)
{
	out[0] = 0;
	if(!v || v->vt != 2 || !v->u.string || cap <= 1) return 0;
	const unsigned char *base = (const unsigned char *)v->u.string;
	void *longStr = *(void **)(base + 4);
	const wchar_t *src = longStr ? (const wchar_t *)longStr : (const wchar_t *)(base + 8);
	int i = 0;
	for(; i < cap - 1; ++i) { wchar_t c = src[i]; if(c == 0) break; out[i] = c; }
	out[i] = 0;
	return i;
}

// 返回：1=字符串已拷出 0=调用成功但非字符串(看 vt) -1=抛异常 -2=alloc 失败
static int EvalVia(fnExecExpr fn, const wchar_t *src, wchar_t *out, int cap, unsigned *vtOut)
{
	out[0] = 0;
	if(vtOut) *vtOut = 0xFFFFFFFFu;
	if(!g_allocStr || !fn) return -1;
	VarBuf r;
	VarBufInit(&r);
	int ok = 0;
	try
	{
		void *vs = g_allocStr(src);
		if(!vs) return -2;
		TtstrPOD t; t.Ptr = vs;
		fn(&t, &r);
		ok = 1;
	}
	catch(...) { ok = 0; }
	if(vtOut) *vtOut = (unsigned)r.vt;
	int bad = VarBufCanaryBroken(&r);
	if(bad && !g_canaryLogged)
	{
		g_canaryLogged = 1;
		LogD(L"variant_overrun_slot=", bad);
	}
	if(!ok) return -1;
	if(r.vt != 2) return 0;
	CopyVariantString(&r, out, cap);
	return 1;
}

static int EvalStr(const wchar_t *expr, wchar_t *out, int cap, unsigned *vt)
{
	return EvalVia(g_execExpr, expr, out, cap, vt);
}
static int EvalScript(const wchar_t *src, wchar_t *out, int cap, unsigned *vt)
{
	return EvalVia(g_execScript, src, out, cap, vt);
}

// “字段诊断”一行：st/vt/len/val
static void LogField(const wchar_t *label, const wchar_t *expr)
{
	wchar_t val[4096] = {0};
	unsigned vt = 0xFFFFFFFFu;
	int st = EvalStr(expr, val, 4096, &vt);
	wchar_t buf[5120];
	wsprintfW(buf, L"field=%s st=%d vt=%d len=%d val=", label, st, (int)vt, (int)lstrlenW(val));
	lstrcatW(buf, val);
	Log(buf);
}

// ------------------------------------------------------------ TJS 探针脚本 ----
// 全部 ASCII。每个脚本都自带 try/catch 并 return，避免异常逃到引擎。
static const wchar_t *kCIKeysScript =
L"var s=\"\";"
L"try{ var o=global.kag.historyLayer.currentInfo;"
L"  if(o==void||o==null){ s=\"(void)\"; }"
L"  else { for(var k in o){ s += k + \",\"; } if(s==\"\") s=\"(no keys)\"; }"
L"}catch(e){ s=\"ERR:\"+e; }"
L"return s;";

static const wchar_t *kRenProbeScript =
L"var s=\"\";"
L"try{"
L"  var rp=global.kag.renderMsgwinPlugin;"
L"  if(rp==void||rp==null){ s=\"plugin=(void)\"; }"
L"  else {"
L"    var r=rp.getRender(void,1);"
L"    if(r==void||r==null){ s=\"render=(void)\"; }"
L"    else {"
L"      var ct=r.currentText;"
L"      if(ct==void||ct==null){ s=\"currentText=(void)\"; }"
L"      else { s=\"len=\"+ct.length; }"
L"    }"
L"  }"
L"}catch(e){ s=\"ERR:\"+e; }"
L"return s;";

static const wchar_t *kRenItemsScript =
L"var s=\"\";"
L"try{"
L"  var r=global.kag.renderMsgwinPlugin.getRender(void,1);"
L"  var ct=r.currentText;"
L"  var i=0;"
L"  while(i<ct.length && i<4){"
L"    var e=ct[i]; var t=\"\"; var nm=\"\"; var ky=\"\";"
L"    try{ t=\"\"+e.text; }catch(a){ t=\"\\x03\"; }"
L"    try{ nm=\"\"+e.name; }catch(b){ nm=\"\\x03\"; }"
L"    try{ for(var q in e){ ky+=q+\",\"; } }catch(c){ ky=\"\\x03\"; }"
L"    s += \"[\"+i+\"]name=\"+nm+\" keys=\"+ky+\" text=\"+t+\"\\x02\";"
L"    i=i+1;"
L"  }"
L"}catch(e){ s=\"ERR:\"+e; }"
L"return s;";

static const wchar_t *kHistKeysScript =   // 需要拼接索引，运行时构造
L"var s=\"\";"
L"try{"
L"  var d=global.kag.historyLayer.data;"
L"  var n=d.length;"
L"  s=\"len=\"+n+\" \";"
L"  var st=n-3; if(st<0) st=0;"
L"  var i=st;"
L"  while(i<n){"
L"    var e=d[i]; var ky=\"\";"
L"    try{ for(var q in e){ ky+=q+\",\"; } }catch(c){ ky=\"\\x03\"; }"
L"    s += \"[\"+i+\"]keys=\"+ky+\"\\x02\";"
L"    i=i+1;"
L"  }"
L"}catch(e){ s=\"ERR:\"+e; }"
L"return s;";

static const wchar_t *kHistItemsScript =
L"var s=\"\";"
L"try{"
L"  var d=global.kag.historyLayer.data;"
L"  var n=d.length;"
L"  var st=n-3; if(st<0) st=0;"
L"  var i=st;"
L"  while(i<n){"
L"    var e=d[i]; var nm=\"\"; var t=\"\"; var p=\"\";"
L"    try{ nm=\"\"+e.disp; }catch(a){ nm=\"\\x03\"; }"
L"    try{ t=\"\"+e.text; }catch(b){ t=\"\\x03\"; }"
L"    try{ p=\"\"+e.plaintext; }catch(c){ p=\"\\x03\"; }"
L"    s += \"[\"+i+\"]disp=\"+nm+\"\\x01text=\"+t+\"\\x01plain=\"+p+\"\\x02\";"
L"    i=i+1;"
L"  }"
L"}catch(e){ s=\"ERR:\"+e; }"
L"return s;";

static const wchar_t *kLangScript =
L"var s=\"\";"
L"try{"
L"  var c=global.SystemConfig;"
L"  if(c==void||c==null){ s=\"SystemConfig=(void)\"; }"
L"  else {"
L"    var a=\"\"; var b=\"\"; var u=\"\";"
L"    try{ a=\"\"+c.subLanguageType; }catch(x1){ a=\"\\x03\"; }"
L"    try{ b=\"\"+c.textLanguageType; }catch(x2){ b=\"\\x03\"; }"
L"    try{ u=\"\"+c.uiLanguageType; }catch(x3){ u=\"\\x03\"; }"
L"    s=\"sub=\"+a+\" text=\"+b+\" ui=\"+u;"
L"  }"
L"}catch(e){ s=\"ERR:\"+e; }"
L"var g1=\"\";"
L"try{ g1=\"\"+global.CurrentLanguageTag; }catch(y1){ g1=\"\\x03\"; }"
L"var g2=\"\";"
L"try{ g2=\"\"+global.CurrentLanguageUITag; }catch(y2){ g2=\"\\x03\"; }"
L"return s+\" tag=\"+g1+\" uitag=\"+g2;";

static const wchar_t *kLayerScript =
L"var s=\"\";"
L"try{"
L"  var n=\"\"; var lb=\"\";"
L"  try{ n=\"\"+global.kag.current.name; }catch(a){ n=\"\\x03\"; }"
L"  try{ lb=\"\"+global.kag.currentLabel; }catch(b){ lb=\"\\x03\"; }"
L"  var pl=\"\";"
L"  try{ var p=global.kag.getPrimaryLayerAt(960,540); if(p!=void&&p!=null) pl=\"\"+p.name; }catch(c){ pl=\"\\x03\"; }"
L"  s=\"current.name=\"+n+\" label=\"+lb+\" primary=\"+pl;"
L"}catch(e){ s=\"ERR:\"+e; }"
L"return s;";

// 主轮询脚本：一行取全部候选文本（currentInfo 三字段 + data 末条两字段 + render 三条）
static const wchar_t *kPollScript =
L"var r=\"\";"
L"try{"
L"  var h=global.kag.historyLayer;"
L"  if(h==void||h==null){ r=\"\\x05\"; }"
L"  else {"
L"    var ci=h.currentInfo;"
L"    var n=\"\"; var t=\"\"; var p=\"\";"
L"    if(ci!=void&&ci!=null){"
L"      try{ n=\"\"+ci.disp; }catch(e1){ n=\"\\x03\"; }"
L"      try{ t=\"\"+ci.text; }catch(e2){ t=\"\\x03\"; }"
L"      try{ p=\"\"+ci.plaintext; }catch(e3){ p=\"\\x03\"; }"
L"    }"
L"    var dt=\"\"; var dp=\"\";"
L"    try{"
L"      var d=h.data;"
L"      if(d!=void&&d!=null&&d.length>0){"
L"        var e=d[d.length-1];"
L"        try{ dt=\"\"+e.text; }catch(f1){ dt=\"\\x03\"; }"
L"        try{ dp=\"\"+e.plaintext; }catch(f2){ dp=\"\\x03\"; }"
L"      }"
L"    }catch(f0){ dt=\"\\x06\"; dp=\"\\x06\"; }"
L"    var rr=\"\";"
L"    try{"
L"      var ren=global.kag.renderMsgwinPlugin.getRender(void,1);"
L"      if(ren!=void&&ren!=null){"
L"        var ct=ren.currentText;"
L"        if(ct!=void&&ct!=null){"
L"          var i=0;"
L"          while(i<ct.length && i<3){"
L"            var s=\"\";"
L"            try{ s=\"\"+ct[i].text; }catch(g1){ s=\"\\x03\"; }"
L"            rr += s + \"\\x02\";"
L"            i=i+1;"
L"          }"
L"        }"
L"      }"
L"    }catch(g0){ rr=\"\\x04\"; }"
L"    r = n+\"\\x01\"+t+\"\\x01\"+p+\"\\x01\"+dt+\"\\x01\"+dp+\"\\x01\"+rr;"
L"  }"
L"}catch(e){ r=\"\\x07\"; }"
L"return r;";

// ---------------------------------------------------------------- capture ----
static void WriteCaptureHeader(void)
{
	wchar_t buf[1024];
	SYSTEMTIME st; GetLocalTime(&st);
	wsprintfW(buf, L"# kkz_trans43 session pid=%u %04d-%02d-%02d %02d:%02d:%02d",
		(unsigned)GetCurrentProcessId(), st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
	WriteTo(L"capture.txt", buf);
	WriteTo(L"capture.txt", L"# cols: seq<TAB>name<TAB>ci.text<TAB>ci.plaintext<TAB>data.last.text<TAB>data.last.plaintext<TAB>render0<TAB>render1<TAB>render2<TAB>tick");
	WriteTo(L"capture.txt", L"# \\x03=field threw  \\x04=render threw  \\x05=no kag  \\x06=data threw  \\x07=poll threw");
}

static void WriteCaptureRow(int seq, const wchar_t *name, const wchar_t *text, const wchar_t *plain,
                            const wchar_t *dt, const wchar_t *dp,
                            const wchar_t *r0, const wchar_t *r1, const wchar_t *r2)
{
	wchar_t buf[16384];
	int n = wsprintfW(buf, L"%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%d", seq,
		name ? name : L"", text ? text : L"", plain ? plain : L"",
		dt ? dt : L"", dp ? dp : L"", r0 ? r0 : L"", r1 ? r1 : L"", r2 ? r2 : L"", g_ticks);
	WriteTo(L"capture.txt", buf);
	(void)n;
}

// ------------------------------------------------------------ stage helpers ---
// 拆分 \x01 分隔的 poll 结果
static int SplitFields(wchar_t *buf, wchar_t sep, wchar_t *out[], int maxOut)
{
	int n = 0;
	if(!buf) return 0;
	out[n++] = buf;
	for(wchar_t *p = buf; *p && n < maxOut; ++p)
	{
		if(*p == sep) { *p = 0; out[n++] = p + 1; }
	}
	return n;
}

static void PollOnce(void)
{
	wchar_t raw[8192] = {0};
	unsigned vt = 0xFFFFFFFFu;
	int st = EvalScript(kPollScript, raw, 8192, &vt);
	if(st != 1)
	{
		if(!g_pollFailLogged)
		{
			g_pollFailLogged = 1;
			LogD(L"poll_read_fail st=", st);
			LogH(L"poll_read_vt=", vt);
		}
		return;
	}

	wchar_t *f[8] = {0};
	if(SplitFields(raw, L'\x01', f, 8) < 6) return;

	wchar_t key[8192];
	wsprintfW(key, L"%s\x01%s\x01%s\x01%s\x01%s\x01%s", f[0], f[1], f[2], f[3], f[4], f[5]);
	if(lstrcmpW(key, g_lastKey) == 0) return;
	lstrcpynW(g_lastKey, key, 8192);

	// render 段里以 \x02 分条
	wchar_t *r[4] = {0};
	int rn = SplitFields(f[5], L'\x02', r, 4);

	++g_rows;
	WriteCaptureRow(g_rows, f[0], f[1], f[2], f[3], f[4],
		rn > 0 ? r[0] : L"", rn > 1 ? r[1] : L"", rn > 2 ? r[2] : L"");
	{
		wchar_t b[256];
		wsprintfW(b, L"row=%d ci.name_len=%d ci.text_len=%d ci.plain_len=%d d.text_len=%d d.plain_len=%d rend_n=%d",
			g_rows, (int)lstrlenW(f[0]), (int)lstrlenW(f[1]), (int)lstrlenW(f[2]),
			(int)lstrlenW(f[3]), (int)lstrlenW(f[4]), rn);
		Log(b);
	}
}

// 阶段机：每拍只做一步，先把结论落盘再前进
enum {
	ST_BASELINE = 0,
	ST_KAG_VOID,
	ST_HIST_VOID,
	ST_CI_VOID,
	ST_CI_KEYS,
	ST_CI_FIELDS,
	ST_REN,
	ST_REN_ITEMS,
	ST_HIST_KEYS,
	ST_HIST_ITEMS,
	ST_LANG,
	ST_LAYER,
	ST_COUNT
};

// RunStage 内部自己判断“能不能做”：返回 1 = 已完成并前进；0 = 条件不满足，原地重试。
static int HasLine(void);
// （不在 Tick 里用 needLine 门控：run2 实测那种写法导致卡在 stage 0，改成内部门控更直观）
static int RunStage(void)
{
	wchar_t val[8192] = {0};
	unsigned vt = 0xFFFFFFFFu;
	int st = 0;

	switch(g_stage)
	{
	case ST_BASELINE:
		st = EvalStr(L"1 + 1", val, 64, &vt);
		LogD(L"res=baseline st=", st); LogH(L"res=baseline vt=", vt);
		break;
	case ST_KAG_VOID:
		st = EvalStr(L"global.kag == void", val, 64, &vt);
		LogD(L"res=kag_void st=", st); LogH(L"res=kag_void vt=", vt);
		// 顺便把“现在到底停在哪”一次问清楚：主层名/当前标签/选项显示/历史长度。
		st = EvalScript(
			L"var s=\"\";"
			L"try{ var k=global.kag;"
			L"  var p=\"\"; try{ p=\"\"+k.getPrimaryLayerAt(960,540).name; }catch(a){ p=\"\\x03\"; }"
			L"  var cl=\"\"; try{ cl=\"\"+k.currentLabel; }catch(b){ cl=\"\\x03\"; }"
			L"  var cn=\"\"; try{ cn=\"\"+k.current.name; }catch(c){ cn=\"\\x03\"; }"
			L"  var ss=\"\"; try{ ss=\"\"+k.selectShowing; }catch(d){ ss=\"\\x03\"; }"
			L"  var dl=\"\"; try{ dl=\"\"+k.historyLayer.data.length; }catch(e2){ dl=\"\\x03\"; }"
			L"  var sf=\"\"; try{ sf=\"\"+k.isSkip; }catch(e3){ sf=\"\\x03\"; }"
			L"  s=\"primary=\"+p+\" label=\"+cl+\" curlayer=\"+cn+\" selectShowing=\"+ss+\" histLen=\"+dl+\" skip=\"+sf;"
			L"}catch(e){ s=\"ERR:\"+e; }"
			L"return s;", val, 4096, &vt);
		LogD(L"res=where st=", st);
		LogStr(L"where=", val);
		break;
	case ST_HIST_VOID:
		st = EvalStr(L"global.kag.historyLayer == void", val, 64, &vt);
		LogD(L"res=hist_void st=", st); LogH(L"res=hist_void vt=", vt);
		break;
	case ST_CI_VOID:
		st = EvalStr(L"global.kag.historyLayer.currentInfo == void", val, 64, &vt);
		LogD(L"res=ci_void st=", st); LogH(L"res=ci_void vt=", vt);
		break;
	case ST_CI_KEYS:
		// run3 实测：for(var k in currentInfo) 直接把进程搞死（无异常、无日志，进程消失）。
		// 结论：不做键枚举；改用固定字段名读取（字段名来自 a11y6.cpp:619-620 与 :2000-2026）。
		Log(L"res=ci_keys skipped_no_enum_run3_crash");
		val[0] = 0;
		st = 1;
		break;
	case ST_CI_FIELDS:
		// 不再等“有台词”：菜单/无台词时也要把 5 个字段的实际内容记下来（含空串与 vt）。
		LogField(L"ci.disp",      L"global.kag.historyLayer.currentInfo.disp");
		LogField(L"ci.text",      L"global.kag.historyLayer.currentInfo.text");
		LogField(L"ci.plaintext", L"global.kag.historyLayer.currentInfo.plaintext");
		LogField(L"ci.name",      L"global.kag.historyLayer.currentInfo.name");
		LogField(L"ci.voice",     L"global.kag.historyLayer.currentInfo.voice");
		break;
	case ST_REN:
		st = EvalScript(kRenProbeScript, val, 4096, &vt);
		LogD(L"res=render st=", st);
		LogStr(L"render_probe=", val);
		break;
	case ST_REN_ITEMS:
		if(!HasLine()) { Log(L"retry=ren_items no_line"); return 0; }
		// 不做 for-in（run3 crash）；只取 render 0..2 的 text/name 两个固定字段
		st = EvalScript(
			L"var s=\"\";"
			L"try{ var r=global.kag.renderMsgwinPlugin.getRender(void,1); var ct=r.currentText;"
			L"  var i=0; while(i<ct.length && i<3){"
			L"    var t=\"\"; var nm=\"\";"
			L"    try{ t=\"\"+ct[i].text; }catch(a){ t=\"\\x03\"; }"
			L"    try{ nm=\"\"+ct[i].name; }catch(b){ nm=\"\\x03\"; }"
			L"    s += \"[\"+i+\"]name=\"+nm+\" text=\"+t+\"\\x02\"; i=i+1; }"
			L"}catch(e){ s=\"ERR:\"+e; }"
			L"return s;", val, 8192, &vt);
		LogD(L"res=render_items st=", st);
		LogStr(L"render_items=", val);
		break;
	case ST_HIST_KEYS:
		// 只取长度，不枚举键（run3 crash）
		st = EvalScript(
			L"var s=\"\";"
			L"try{ var d=global.kag.historyLayer.data;"
			L"  if(d==void||d==null){ s=\"(void)\"; } else { s=\"len=\"+d.length; } }"
			L"catch(e){ s=\"ERR:\"+e; }"
			L"return s;", val, 4096, &vt);
		LogD(L"res=hist_len st=", st);
		LogStr(L"hist_len=", val);
		break;
	case ST_HIST_ITEMS:
		if(!HasLine()) { Log(L"retry=hist_items no_line"); return 0; }
		st = EvalScript(kHistItemsScript, val, 8192, &vt);
		LogD(L"res=hist_items st=", st);
		LogStr(L"hist_items=", val);
		break;
	case ST_LANG:
		st = EvalScript(kLangScript, val, 4096, &vt);
		LogD(L"res=lang st=", st);
		LogStr(L"lang=", val);
		break;
	case ST_LAYER:
		st = EvalScript(kLayerScript, val, 4096, &vt);
		LogD(L"res=layer st=", st);
		LogStr(L"layer=", val);
		break;
	}
	++g_stage;
	if(g_stage >= ST_COUNT) Log(L"stages_done enter_poll");
	return 1;
}

// V2Link 发生在 startup.tjs 里，早于 KAG 初始化，所以阶段必须门控：
//   · 启动前 25s 只做原生打点，完全不进 TJS（实测：开机加载期引擎自己在泵消息，
//     此时 TVPExecuteScript 重入会卡死整条主线程，run1 就死在第一个 KagReady）
//   · 之后结构型阶段等 global.kag != void；字段型阶段等“屏幕上真有一句台词”
//   · 进程自己有可见对话框（a11y 的「NVDA 朗讀查看器」是模态框）时同样不进 TJS
static DWORD g_warmupStart = 0;
static int   g_warmupDone  = 0;
static const DWORD kKanWarmupMs = 25000;

static int OwnDialogPresent(void)
{
	struct Ctx { DWORD pid; int found; } ctx;
	ctx.pid = GetCurrentProcessId();
	ctx.found = 0;
	EnumWindows([](HWND h, LPARAM lp) -> BOOL {
		Ctx *c = (Ctx *)lp;
		DWORD pid = 0;
		GetWindowThreadProcessId(h, &pid);
		if(pid != c->pid) return TRUE;
		if(!IsWindowVisible(h)) return TRUE;
		wchar_t cls[64]; cls[0] = 0;
		GetClassNameW(h, cls, 64);
		if(lstrcmpiW(cls, L"#32770") == 0)
		{
			wchar_t t[128]; t[0] = 0;
			GetWindowTextW(h, t, 128);
			if(t[0]) { c->found = 1; return FALSE; }
		}
		return TRUE;
	}, (LPARAM)&ctx);
	return ctx.found;
}

static int KagReady(void)
{
	wchar_t v[64] = {0}; unsigned vt = 0;
	int st = EvalStr(L"global.kag == void", v, 64, &vt);
	return st == 0;                       // 布尔结果 -> EvalStr 返回 0
}

static int HasLine(void)
{
	wchar_t v[512] = {0}; unsigned vt = 0;
	int st = EvalScript(L"var s=\"\";"
		L"try{ var c=global.kag.historyLayer.currentInfo;"
		L"  if(c!=void&&c!=null){ var a=\"\"; var b=\"\";"
		L"    try{ a=\"\"+c.text; }catch(e1){} try{ b=\"\"+c.plaintext; }catch(e2){}"
		L"    s=a+b; } }catch(e){}"
		L"return s;", v, 512, &vt);
	return st == 1 && lstrlenW(v) > 0;
}

static void PostClickAt(double fx, double fy);
static void Tick(void)
{
	if(InterlockedExchange(&g_tickBusy, 1) == 1) return;
	if(!g_ready) { g_tickBusy = 0; return; }
	++g_ticks;

	if(!g_firstTickLogged)
	{
		g_firstTickLogged = 1;
		wchar_t b[160];
		wsprintfW(b, L"first_tick tid=%u", (unsigned)GetCurrentThreadId());
		Log(b);
	}

		// 进程内注入点击：本轮实测 SendInput 返回 0（系统拒绝），跨进程合成输入完全失效；
	// 但我们在游戏进程里，PostMessage 到自己的顶层窗口不经过 UIPI，KAG 的窗口过程照常分发。
	// 只在“还没有任何台词行”时点，按固定序列循环，每 5 拍（2 秒）一次。
	if(g_rows < 16 && (g_ticks % 5 == 0))
	{
		static const double kPts[][2] = {
			{0.060, 0.345}, // 新遊戲
			{0.500, 0.750}, {0.500, 0.700}, {0.500, 0.800},
			{0.500, 0.750}
		};
		static int pt = 0;
		int n = (int)(sizeof(kPts) / sizeof(kPts[0]));
		PostClickAt(kPts[pt][0], kPts[pt][1]);
		pt = (pt + 1) % n;
	}
if(g_stage < ST_COUNT)
	{
		if(!g_warmupDone)
		{
			if(GetTickCount() - g_warmupStart < kKanWarmupMs)
			{
				if(g_ticks % 10 == 0) LogD(L"tick_warmup=", g_ticks);
				g_tickBusy = 0;
				return;
			}
			g_warmupDone = 1;
			wchar_t b[160];
			wsprintfW(b, L"warmup_done tick=%d", g_ticks);
			Log(b);
		}
		if(OwnDialogPresent())
		{
			if(g_ticks % 20 == 1) LogD(L"wait_dialog tick=", g_ticks);
			g_tickBusy = 0;
			return;
		}
		if(!KagReady())
		{
			if(g_ticks % 20 == 1) LogD(L"wait_kag tick=", g_ticks);
			g_tickBusy = 0;
			return;
		}
		LogD(L"stage_enter=", g_stage);
		RunStage();
		g_tickBusy = 0;
		return;
	}
	PollOnce();


	if(g_ticks - g_lastLogTick >= 50)
	{
		g_lastLogTick = g_ticks;
		wchar_t b[160];
		wsprintfW(b, L"tick=%d rows=%d", g_ticks, g_rows);
		Log(b);
	}
	g_tickBusy = 0;
}

// 找本进程面积最大的可见顶层窗口（不用 GetForegroundWindow：全屏/窗口模式下都可能不准）
struct OwnCtx { HWND best; long area; unsigned long pid; };
static BOOL CALLBACK OwnMainEnum(HWND h, LPARAM lp);
static void PostClickAt(double fx, double fy);
static HWND OwnMainWindow(void)
{
	OwnCtx ctx;
	ctx.best = 0; ctx.area = 0; ctx.pid = GetCurrentProcessId();
	EnumWindows(OwnMainEnum, (LPARAM)&ctx);
	return ctx.best;
}

static BOOL CALLBACK OwnMainEnum(HWND h, LPARAM lp)
{
	OwnCtx *p = (OwnCtx *)lp;
	unsigned long pid = 0;
	GetWindowThreadProcessId(h, &pid);
	if(pid != p->pid) return TRUE;
	if(!IsWindowVisible(h)) return TRUE;
	RECT rc;
	if(!GetClientRect(h, &rc)) return TRUE;
	long a = (long)(rc.right - rc.left) * (long)(rc.bottom - rc.top);
	if(a > p->area) { p->area = a; p->best = h; }
	return TRUE;
}

static void PostClickAt(double fx, double fy)
{
	HWND h = OwnMainWindow();
	if(!h) { Log(L"postclick_nowin"); return; }
	RECT rc;
	if(!GetClientRect(h, &rc)) { Log(L"postclick_norect"); return; }
	int w = rc.right - rc.left, ht = rc.bottom - rc.top;
	if(w <= 0 || ht <= 0) { Log(L"postclick_badrect"); return; }
	int x = (int)(fx * w + 0.5), y = (int)(fy * ht + 0.5);
	LPARAM lp = MAKELPARAM(x, y);
	PostMessageW(h, WM_MOUSEMOVE, 0, lp);
	PostMessageW(h, WM_LBUTTONDOWN, MK_LBUTTON, lp);
	PostMessageW(h, WM_LBUTTONUP, 0, lp);
	wchar_t b[160];
	wsprintfW(b, L"postclick x=%d y=%d of %dx%d", x, y, w, ht);
	Log(b);
}

static LRESULT CALLBACK TransWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp)
{
	if(msg == WM_TIMER && wp == KKZ_TIMER_ID)
	{
		try { Tick(); } catch(...) { Log(L"tick_cpp_exception"); }
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
	wc.lpfnWndProc = TransWndProc;
	wc.hInstance = hInst;
	wc.lpszClassName = L"KkzTransMsgWindow";
	RegisterClassW(&wc);
	HWND h = CreateWindowExW(0, L"KkzTransMsgWindow", L"", 0, 0, 0, 0, 0,
		HWND_MESSAGE, NULL, hInst, NULL);
	if(!h) { LogD(L"pump_fail err=", (int)GetLastError()); return; }
	SetTimer(h, KKZ_TIMER_ID, KKZ_POLL_MS, NULL);
	wchar_t b[160];
	wsprintfW(b, L"pump=ok timer=%dms hwnd=%08X tid=%u", KKZ_POLL_MS,
		(unsigned)(UINT_PTR)h, (unsigned)GetCurrentThreadId());
	Log(b);
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

	Log(L"---- kkz_trans TjsSetup begin");
	LogUtc();
	LogD(L"pid_tid=", (int)GetCurrentThreadId());
	if(!exporter) { Log(L"tjs=FAIL reason=null_exporter"); return; }

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
		try { ok = exp->QueryFunctionsByNarrowString(&nm, &f, 1); } catch(...) { ok = false; }
		*(items[i].slot) = f;
		if(f) okCount++;
		wchar_t q[256];
		wsprintfW(q, L"Q %s f=%08X", items[i].label, (unsigned)(UINT_PTR)f);
		Log(q);
	}
	LogD(L"Q total=", okCount);

	if(!g_allocStr || !g_execExpr || !g_execScript) { Log(L"tjs=FAIL reason=no_exec"); return; }

	RunViaCheck:;
	{
		unsigned vt = 0; wchar_t got[128] = {0};
		int st = EvalScript(L"return \"SELFTEST_ABC_0123456789\";", got, 128, &vt);
		LogD(L"selftest_st=", st);
		LogStr(L"selftest=", got);
	}

	WriteCaptureHeader();
	Log(L"tjs=OK enter_stages");
	g_warmupStart = GetTickCount();
	g_ready = 1;
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

	Log(L"==== V2Link");
	LogUtc();
	LogStr(L"self=", self);
	LogStr(L"orig=", orig);
	LogD(L"load_ok=", g_orig ? 1 : 0);
	LogD(L"lasterr=", (int)err);
	LogD(L"forwarded=", forwarded);
	LogD(L"forward_ret=", (int)hr);

	if(forwarded)
	{
		try { TjsSetup(exporter); } catch(...) { Log(L"tjs=FAIL reason=setup_cpp_exception"); }
	}
	return hr;
}

extern "C" HRESULT __stdcall V2Unlink(void)
{
	Log(L"==== V2Unlink");
	LogD(L"ticks=", g_ticks);
	LogD(L"rows=", g_rows);
	LogD(L"stage=", g_stage);
	if(g_origV2Unlink)
	{
		typedef HRESULT (__stdcall *fnUnlink)(void);
		return ((fnUnlink)g_origV2Unlink)();
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


