// ============================================================================
// kkz_proxy.cpp -- GalA11y Forge / task-51 (R28)
// KKZ 旧版（KiriKiri Z 1.2.x，如 SenrenBanka 1.2.0.3）实验性适配器 v1
//
// 干什么：替换 <GameDir>\plugin\getSample.dll，把 V2Link 转发给改名后的原件
//        getSample_orig.dll，然后在本进程主线程上按固定字段名“只读”求值 KAG 文本：
//            kag.historyLayer.currentInfo.text      -> 中文译文
//            kag.historyLayer.currentInfo.disp      -> 中文说话人
//            kag.historyLayer.currentInfo.plaintext -> 日文原文（语种判定/兜底）
//        去重后追加到 %LOCALAPPDATA%\GalA11yForge\kkz-adapter\capture-<pid>.tsv，
//        由进程外的 speak.ps1 独立朗读（不在游戏进程里做 TTS）。
//
// 硬约束（task-47/48 实测教训，改代码前先读完）：
//   · TJS 侧禁止 new Object()/new Array()  -> 0xC0000409 (___report_gsfailure)
//   · 禁止 typeof(<对象>)                  -> 抛 C++ 异常；存在性用 x == void
//   · 禁止 for (var k in <kag 对象>)       -> 进程直接消失（R27 run3）
//   · V2Link 内立刻求值会卡死主线程        -> 必须 warmup >= 25s 再碰 TJS
//   · wsprintfW(buf, L"key=", v) 会静默丢值 -> 每段格式串都必须有转换符
//   · .def 只能 ASCII（含中文注释会让 link 静默丢弃全部导出）
//
// 线程模型（为什么 TJS 只在 WM_TIMER 里跑）：
//   · 后台线程：只等 warmup，到点置 g_warmupDone=1，不做任何 TJS。
//   · WM_TIMER：引擎主线程，warmup 之后才执行 TVPExecuteExpression。
//     TJS 不是线程安全的，R26/R27 只在引擎主线程上成功过；换线程没有任何实测依据。
//   · 重入用 InterlockedExchange 拦截（引擎可能重入消息循环）。
// ============================================================================
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winver.h>

#define KKZ_VER L"0.1.0.0"
#define KKZ_TIMER_ID 0x4B5B

// ---------------------------------------------------------------- SHA-256 ---
typedef unsigned int   u32;
typedef unsigned char  u8;
typedef unsigned __int64 u64;

struct Sha256Ctx { u32 h[8]; u8 buf[64]; u64 total; u32 idx; };

static const u32 K256[64] = {
	0x428a2f98u,0x71374491u,0xb5c0fbcfu,0xe9b5dba5u,0x3956c25bu,0x59f111f1u,0x923f82a4u,0xab1c5ed5u,
	0xd807aa98u,0x12835b01u,0x243185beu,0x550c7dc3u,0x72be5d74u,0x80deb1feu,0x9bdc06a7u,0xc19bf174u,
	0xe49b69c1u,0xefbe4786u,0x0fc19dc6u,0x240ca1ccu,0x2de92c6fu,0x4a7484aau,0x5cb0a9dcu,0x76f988dau,
	0x983e5152u,0xa831c66du,0xb00327c8u,0xbf597fc7u,0xc6e00bf3u,0xd5a79147u,0x06ca6351u,0x14292967u,
	0x27b70a85u,0x2e1b2138u,0x4d2c6dfcu,0x53380d13u,0x650a7354u,0x766a0abbu,0x81c2c92eu,0x92722c85u,
	0xa2bfe8a1u,0xa81a664bu,0xc24b8b70u,0xc76c51a3u,0xd192e819u,0xd6990624u,0xf40e3585u,0x106aa070u,
	0x19a4c116u,0x1e376c08u,0x2748774cu,0x34b0bcb5u,0x391c0cb3u,0x4ed8aa4au,0x5b9cca4fu,0x682e6ff3u,
	0x748f82eeu,0x78a5636fu,0x84c87814u,0x8cc70208u,0x90befffau,0xa4506cebu,0xbef9a3f7u,0xc67178f2u
};

static u32 RotR(u32 x, int n) { return (x >> n) | (x << (32 - n)); }

static void Sha256Init(Sha256Ctx *c)
{
	c->h[0]=0x6a09e667u; c->h[1]=0xbb67ae85u; c->h[2]=0x3c6ef372u; c->h[3]=0xa54ff53au;
	c->h[4]=0x510e527fu; c->h[5]=0x9b05688cu; c->h[6]=0x1f83d9abu; c->h[7]=0x5be0cd19u;
	c->total=0; c->idx=0;
}

static void Sha256Block(Sha256Ctx *c, const u8 *p)
{
	u32 w[64];
	for(int i=0;i<16;++i)
		w[i]=((u32)p[i*4]<<24)|((u32)p[i*4+1]<<16)|((u32)p[i*4+2]<<8)|(u32)p[i*4+3];
	for(int i=16;i<64;++i)
	{
		u32 s0=RotR(w[i-15],7)^RotR(w[i-15],18)^(w[i-15]>>3);
		u32 s1=RotR(w[i-2],17)^RotR(w[i-2],19)^(w[i-2]>>10);
		w[i]=w[i-16]+s0+w[i-7]+s1;
	}
	u32 a=c->h[0],b=c->h[1],d0=c->h[2],d=c->h[3],e=c->h[4],f=c->h[5],g=c->h[6],h=c->h[7];
	for(int i=0;i<64;++i)
	{
		u32 S1=RotR(e,6)^RotR(e,11)^RotR(e,25);
		u32 ch=(e&f)^((~e)&g);
		u32 t1=h+S1+ch+K256[i]+w[i];
		u32 S0=RotR(a,2)^RotR(a,13)^RotR(a,22);
		u32 mj=(a&b)^(a&d0)^(b&d0);
		u32 t2=S0+mj;
		h=g; g=f; f=e; e=d+t1; d=d0; d0=b; b=a; a=t1+t2;
	}
	c->h[0]+=a; c->h[1]+=b; c->h[2]+=d0; c->h[3]+=d; c->h[4]+=e; c->h[5]+=f; c->h[6]+=g; c->h[7]+=h;
}

static void Sha256Update(Sha256Ctx *c, const u8 *data, u32 n)
{
	c->total += n;
	while(n > 0)
	{
		u32 take = 64 - c->idx;
		if(take > n) take = n;
		for(u32 i=0;i<take;++i) c->buf[c->idx+i]=data[i];
		c->idx += take; data += take; n -= take;
		if(c->idx == 64) { Sha256Block(c, c->buf); c->idx = 0; }
	}
}

static void Sha256Final(Sha256Ctx *c, u8 out[32])
{
	u64 bits = c->total * 8;
	u8 pad[72]; u32 padLen;
	pad[0] = 0x80;
	u32 rem = (u32)((c->total) % 64);
	padLen = (rem < 56) ? (56 - rem) : (120 - rem);
	for(u32 i=1;i<padLen;++i) pad[i]=0;
	Sha256Update(c, pad, padLen);
	u8 lenb[8];
	for(int i=0;i<8;++i) lenb[i]=(u8)(bits >> (56 - i*8));
	Sha256Update(c, lenb, 8);
	for(int i=0;i<8;++i)
	{
		out[i*4+0]=(u8)(c->h[i]>>24); out[i*4+1]=(u8)(c->h[i]>>16);
		out[i*4+2]=(u8)(c->h[i]>>8);  out[i*4+3]=(u8)(c->h[i]);
	}
}

static const wchar_t *kHex = L"0123456789ABCDEF";

static int Sha256File(const wchar_t *path, wchar_t *hexOut, int cap)
{
	hexOut[0]=0;
	HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
		NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return 0;
	Sha256Ctx c; Sha256Init(&c);
	u8 buf[8192];
	for(;;)
	{
		DWORD got = 0;
		if(!ReadFile(h, buf, sizeof(buf), &got, NULL)) { CloseHandle(h); return 0; }
		if(got == 0) break;
		Sha256Update(&c, buf, got);
	}
	CloseHandle(h);
	u8 dig[32]; Sha256Final(&c, dig);
	if(cap < 65) return 0;
	for(int i=0;i<32;++i) { hexOut[i*2]=kHex[dig[i]>>4]; hexOut[i*2+1]=kHex[dig[i]&15]; }
	hexOut[64]=0;
	return 1;
}

// ---------------------------------------------------------------- paths ------
static void SelfDir(wchar_t *out, int cap)
{
	HMODULE hm = NULL;
	out[0]=0;
	if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(const void *)&SelfDir, &hm)) return;
	wchar_t p[MAX_PATH]; p[0]=0;
	if(!GetModuleFileNameW(hm, p, MAX_PATH)) return;
	lstrcpynW(out, p, cap);
	for(int i=lstrlenW(out)-1;i>=0;--i) { if(out[i]==L'\\') { out[i]=0; break; } }
}

static const wchar_t *BaseName(const wchar_t *p)
{
	const wchar_t *b = p;
	for(const wchar_t *q=p; *q; ++q) if(*q==L'\\' || *q==L'/') b=q+1;
	return b;
}

static void EnsureDir(const wchar_t *path)
{
	wchar_t tmp[MAX_PATH*2];
	lstrcpynW(tmp, path, MAX_PATH*2);
	int n = lstrlenW(tmp);
	for(int i=3;i<n;++i)
	{
		if(tmp[i]==L'\\') { tmp[i]=0; CreateDirectoryW(tmp, NULL); tmp[i]=L'\\'; }
	}
	CreateDirectoryW(tmp, NULL);
}

static void ExpandEnv(const wchar_t *in, wchar_t *out, int cap)
{
	out[0]=0;
	if(!in || !in[0]) return;
	DWORD n = ExpandEnvironmentStringsW(in, out, (DWORD)cap);
	if(n == 0 || n > (DWORD)cap) lstrcpynW(out, in, cap);
}

// ---------------------------------------------------------------- config -----
struct Cfg
{
	wchar_t targetDll[64];
	wchar_t origSuffix[24];
	int     pollMs;
	int     warmupMs;
	int     stabilizeMs;
	int     minChars;
	int     autoClick;
	int     clickIntervalMs;
	int     maxClicks;
	int     clickRowsGoal;
	int     quietAfterClicksMs;
	wchar_t captureDir[MAX_PATH];
	wchar_t logFile[MAX_PATH];
	wchar_t requireExe[64];
	wchar_t requireExeVersion[64];
	wchar_t requireOrigSha256[80];
	wchar_t exeVersion[64];
	wchar_t iniPath[MAX_PATH];
};

static Cfg g_cfg;
static int  g_cfgLoaded = 0;

static void CfgDefaults(void)
{
	ZeroMemory(&g_cfg, sizeof(g_cfg));
	lstrcpynW(g_cfg.targetDll, L"getSample.dll", 64);
	lstrcpynW(g_cfg.origSuffix, L"_orig", 24);
	g_cfg.pollMs = 300;
	g_cfg.warmupMs = 25000;
	g_cfg.stabilizeMs = 300;
	g_cfg.minChars = 2;
	g_cfg.autoClick = 1;
	g_cfg.clickIntervalMs = 2000;
	g_cfg.maxClicks = 60;
	g_cfg.clickRowsGoal = 8;
	g_cfg.quietAfterClicksMs = 60000;
	lstrcpynW(g_cfg.captureDir, L"%LOCALAPPDATA%\\GalA11yForge\\kkz-adapter", MAX_PATH);
}

static void CfgReadStr(const wchar_t *sec, const wchar_t *key, const wchar_t *def, wchar_t *out, int cap)
{
	wchar_t tmp[1024]; tmp[0]=0;
	if(g_cfg.iniPath[0]) GetPrivateProfileStringW(sec, key, def, tmp, 1024, g_cfg.iniPath);
	else lstrcpynW(tmp, def, 1024);
	if(!tmp[0]) lstrcpynW(tmp, def, 1024);
	lstrcpynW(out, tmp, cap);
}

static int CfgReadInt(const wchar_t *sec, const wchar_t *key, int def)
{
	if(!g_cfg.iniPath[0]) return def;
	return (int)GetPrivateProfileIntW(sec, key, def, g_cfg.iniPath);
}

static void CfgLoad(void)
{
	CfgDefaults();
	wchar_t dir[MAX_PATH]; SelfDir(dir, MAX_PATH);
	wchar_t p[MAX_PATH]; p[0]=0;
	DWORD n = GetEnvironmentVariableW(L"KKZ_ADAPTER_INI", p, MAX_PATH);
	if(n == 0 || n >= MAX_PATH)
	{
		p[0]=0;
		lstrcpynW(p, dir, MAX_PATH);
		lstrcatW(p, L"\\kkz-adapter.ini");
	}
	g_cfg.iniPath[0]=0;
	if(GetFileAttributesW(p) != INVALID_FILE_ATTRIBUTES) lstrcpynW(g_cfg.iniPath, p, MAX_PATH);

	CfgReadStr(L"adapter", L"TargetDll",       g_cfg.targetDll, g_cfg.targetDll, 64);
	CfgReadStr(L"adapter", L"OrigSuffix",      g_cfg.origSuffix, g_cfg.origSuffix, 24);
	g_cfg.pollMs        = CfgReadInt(L"adapter", L"PollMs",        g_cfg.pollMs);
	g_cfg.warmupMs      = CfgReadInt(L"adapter", L"WarmupMs",      g_cfg.warmupMs);
	g_cfg.stabilizeMs   = CfgReadInt(L"adapter", L"StabilizeMs",   g_cfg.stabilizeMs);
	g_cfg.minChars      = CfgReadInt(L"adapter", L"MinChars",      g_cfg.minChars);
	g_cfg.autoClick     = CfgReadInt(L"adapter", L"AutoClick",     g_cfg.autoClick);
	g_cfg.clickIntervalMs = CfgReadInt(L"adapter", L"ClickIntervalMs", g_cfg.clickIntervalMs);
	g_cfg.maxClicks     = CfgReadInt(L"adapter", L"MaxClicks",     g_cfg.maxClicks);
	g_cfg.clickRowsGoal = CfgReadInt(L"adapter", L"ClickRowsGoal", g_cfg.clickRowsGoal);
	g_cfg.quietAfterClicksMs = CfgReadInt(L"adapter", L"QuietAfterClicksMs", g_cfg.quietAfterClicksMs);
	CfgReadStr(L"adapter", L"CaptureDir", g_cfg.captureDir, g_cfg.captureDir, MAX_PATH);
	CfgReadStr(L"adapter", L"LogFile",    L"", g_cfg.logFile, MAX_PATH);

	CfgReadStr(L"gate", L"RequireExe",         L"", g_cfg.requireExe, 64);
	CfgReadStr(L"gate", L"RequireExeVersion",  L"", g_cfg.requireExeVersion, 64);
	CfgReadStr(L"gate", L"RequireOrigSha256",  L"", g_cfg.requireOrigSha256, 80);

	// 安全下限：warmup 不许低于 25s（R26/R27 实测低于它会卡死主线程）
	if(g_cfg.warmupMs < 25000) g_cfg.warmupMs = 25000;
	if(g_cfg.pollMs < 100) g_cfg.pollMs = 100;
	if(g_cfg.pollMs > 5000) g_cfg.pollMs = 5000;
	if(g_cfg.stabilizeMs < 0) g_cfg.stabilizeMs = 0;
	if(g_cfg.minChars < 0) g_cfg.minChars = 0;
	g_cfgLoaded = 1;
}

// ---------------------------------------------------------------- logging ----
static wchar_t g_logPath[MAX_PATH];
static wchar_t g_capPath[MAX_PATH];
static int     g_logReady = 0;
static int     g_capReady = 0;

static void AppendFileUtf8(const wchar_t *path, const wchar_t *text)
{
	if(!path || !path[0] || !text) return;
	int len = lstrlenW(text);
	char u8[32768];
	int need = WideCharToMultiByte(CP_UTF8, 0, text, len, NULL, 0, NULL, NULL);
	if(need <= 0) return;
	if(need > (int)sizeof(u8) - 8) need = (int)sizeof(u8) - 8;
	int n = WideCharToMultiByte(CP_UTF8, 0, text, len, u8, need, NULL, NULL);
	if(n <= 0) return;
	u8[n] = '\r'; u8[n+1] = '\n';
	HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ|FILE_SHARE_WRITE,
		NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return;
	DWORD wrote = 0;
	WriteFile(h, u8, (DWORD)n + 2, &wrote, NULL);
	CloseHandle(h);
}

static void LogLine(const wchar_t *s)
{
	if(!g_logReady) return;
	wchar_t buf[32768];
	wchar_t pid[32];
	wsprintfW(pid, L"[%u] ", (unsigned)GetCurrentProcessId());
	lstrcpynW(buf, pid, 32);
	lstrcatW(buf, s);
	AppendFileUtf8(g_logPath, buf);
}

static void LogKV(const wchar_t *key, const wchar_t *val)
{
	wchar_t buf[32768];
	lstrcpynW(buf, key, 16000);
	lstrcatW(buf, val ? val : L"(null)");
	LogLine(buf);
}

static void LogI(const wchar_t *key, int v)
{
	wchar_t num[32]; wchar_t buf[32768];
	wsprintfW(num, L"%d", v);
	lstrcpynW(buf, key, 16000);
	lstrcatW(buf, num);
	LogLine(buf);
}

static void LogStamp(void)
{
	SYSTEMTIME st; GetLocalTime(&st);
	wchar_t b[128];
	wsprintfW(b, L"time=%04d-%02d-%02dT%02d:%02d:%02d.%03d", st.wYear, st.wMonth, st.wDay,
		st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
	LogLine(b);
}

static void CapStamp(wchar_t *out, int cap)
{
	SYSTEMTIME st; GetLocalTime(&st);
	wsprintfW(out, L"%04d-%02d-%02dT%02d:%02d:%02d.%03d", st.wYear, st.wMonth, st.wDay,
		st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
	(void)cap;
}

// ---------------------------------------------------------------- variant ----
struct VarBuf
{
	union {
		struct { void *Object; void *ObjThis; } closure;
		__int64 integer;
		double  real;
		void *  string;
		void *  octet;
	} u;
	unsigned long vt;
	unsigned long pad[5];
	unsigned long fill[40];
	unsigned long canary[8];
};
static const unsigned long kCanaryVal = 0xA5A5A5A5u;

static void VarBufInit(VarBuf *v)
{
	ZeroMemory(v, sizeof(*v));
	for(int i=0;i<40;++i) v->fill[i]=0x5A5A5A5Au;
	for(int i=0;i<8;++i) v->canary[i]=kCanaryVal;
}
static int VarBufCanaryBroken(const VarBuf *v)
{
	for(int i=0;i<8;++i) if(v->canary[i] != kCanaryVal) return i+1;
	return 0;
}
struct TtstrPOD { void *Ptr; };

static int CopyVariantString(const VarBuf *v, wchar_t *out, int cap)
{
	out[0]=0;
	if(!v || v->vt != 2 || !v->u.string || cap <= 1) return 0;
	const unsigned char *base = (const unsigned char *)v->u.string;
	void *longStr = *(void **)(base + 4);
	const wchar_t *src = longStr ? (const wchar_t *)longStr : (const wchar_t *)(base + 8);
	int i=0;
	for(; i<cap-1; ++i) { wchar_t c=src[i]; if(c==0) break; out[i]=c; }
	out[i]=0;
	return i;
}

// ---------------------------------------------------------------- TJS ABI ----
typedef void   (__cdecl *fnExec)(const void *ttstrRef, void *variantOut);
typedef void * (__cdecl *fnAllocStr)(const wchar_t *s);

struct iTVPFunctionExporter
{
	virtual bool __cdecl QueryFunctions(const wchar_t **name, void **function, unsigned count) = 0;
	virtual bool __cdecl QueryFunctionsByNarrowString(const char **name, void **function, unsigned count) = 0;
};

static fnExec    g_execExpr = NULL;
static fnExec    g_execScript = NULL;
static fnAllocStr g_allocStr = NULL;
static int       g_canaryLogged = 0;

static int EvalStr(const wchar_t *src, wchar_t *out, int cap, unsigned *vtOut)
{
	out[0]=0;
	if(vtOut) *vtOut=0xFFFFFFFFu;
	if(!g_execExpr || !g_allocStr) return -1;
	VarBuf r; VarBufInit(&r);
	int ok=0;
	try
	{
		void *vs = g_allocStr(src);
		if(!vs) return -2;
		TtstrPOD t; t.Ptr=vs;
		g_execExpr(&t, &r);
		ok=1;
	}
	catch(...) { ok=0; }
	if(vtOut) *vtOut=(unsigned)r.vt;
	int bad = VarBufCanaryBroken(&r);
	if(bad && !g_canaryLogged) { g_canaryLogged=1; LogI(L"variant_overrun_slot=", bad); }
	if(!ok) return -1;
	if(r.vt != 2) return 0;
	CopyVariantString(&r, out, cap);
	return 1;
}

// ---------------------------------------------------------------- text ------
static int HasKana(const wchar_t *s)
{
	for(const wchar_t *p=s; p && *p; ++p)
	{
		unsigned c=(unsigned)*p;
		if((c>=0x3041 && c<=0x309F) || (c>=0x30A0 && c<=0x30FF)) return 1;
	}
	return 0;
}

static void TrimEnd(wchar_t *s)
{
	int n = lstrlenW(s);
	while(n > 0)
	{
		wchar_t c = s[n-1];
		if(c==L'\r'||c==L'\n'||c==L' '||c==L'\t'||c==0x3000) { s[--n]=0; }
		else break;
	}
}

static void EscapeText(const wchar_t *in, wchar_t *out, int cap)
{
	int i=0;
	for(const wchar_t *p=in; p && *p && i < cap-4; ++p)
	{
		wchar_t c=*p;
		if(c==L'\r') continue;
		if(c==L'\n') { out[i++]=L'\\'; out[i++]=L'n'; continue; }
		if(c==L'\t') { out[i++]=L' '; continue; }
		out[i++]=c;
	}
	out[i]=0;
}

// ---------------------------------------------------------------- state ------
static HMODULE g_orig = NULL;
static void   *g_origV2Link = NULL;
static void   *g_origV2Unlink = NULL;
static void   *g_origThreadModel = NULL;
static int     g_gatesOk = 0;
static int     g_inject = 0;
static int     g_ready = 0;
static volatile LONG g_tickBusy = 0;
static int     g_ticks = 0;
static int     g_polls = 0;
static int     g_rows = 0;
static int     g_firstPollLogged = 0;
static volatile LONG g_warmupDone = 0;
static DWORD   g_startTick = 0;
static wchar_t g_pendingKey[8192];
static int     g_pendingAge = 0;
static wchar_t g_lastKey[8192];
static int     g_clicks = 0;
static DWORD   g_lastClickTick = 0;
static int     g_clickGiveupLogged = 0;
static int     g_clickSeq = 0;
static DWORD   g_zeroRowsSince = 0;
static int     g_abort = 0;
static int     g_abortLogged = 0;

// ---------------------------------------------------------------- gates -----
static int GateFileName(void)
{
	HMODULE hm=NULL;
	if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(const void *)&GateFileName, &hm)) return 0;
	wchar_t full[MAX_PATH]; full[0]=0;
	if(!GetModuleFileNameW(hm, full, MAX_PATH)) return 0;
	return lstrcmpiW(BaseName(full), g_cfg.targetDll) == 0;
}

static void GateExeVersion(void)
{
	wchar_t exe[MAX_PATH]; exe[0]=0;
	GetModuleFileNameW(NULL, exe, MAX_PATH);
	g_cfg.exeVersion[0]=0;
	DWORD h=0;
	DWORD sz = GetFileVersionInfoSizeW(exe, &h);
	if(sz)
	{
		HANDLE heap = GetProcessHeap();
		void *buf = HeapAlloc(heap, HEAP_ZERO_MEMORY, sz);
		if(buf)
		{
			if(GetFileVersionInfoW(exe, 0, sz, buf))
			{
				VS_FIXEDFILEINFO *fi=NULL; UINT len=0;
				if(VerQueryValueW(buf, L"\\", (void **)&fi, &len) && fi)
				{
					wsprintfW(g_cfg.exeVersion, L"%d.%d.%d.%d",
						(int)HIWORD(fi->dwFileVersionMS), (int)LOWORD(fi->dwFileVersionMS),
						(int)HIWORD(fi->dwFileVersionLS), (int)LOWORD(fi->dwFileVersionLS));
				}
			}
			HeapFree(heap, 0, buf);
		}
	}
}

// 版本前缀匹配，支持 "1.2.*"（只比较给出的段）
static int VersionMatches(const wchar_t *actual, const wchar_t *required)
{
	if(!required[0]) return 1;
	if(!actual[0]) return 0;
	const wchar_t *a=actual, *r=required;
	for(;;)
	{
		if(*r==0) return 1;
		if(*r==L'*') return 1;
		// 取实际段
		int av=0, an=0;
		while(*a>=L'0' && *a<=L'9') { av=av*10+(*a-L'0'); ++a; ++an; }
		if(an==0) return 0;
		// 取要求段
		int rv=0, rn=0;
		while(*r>=L'0' && *r<=L'9') { rv=rv*10+(*r-L'0'); ++r; ++rn; }
		if(rn==0) return 0;
		if(av!=rv) return 0;
		if(*r==L'.') { ++r; if(*a==L'.') ++a; else if(*r!=0 && *r!=L'*') return 0; }
		else if(*r==0) return 1;
		else return 0;
	}
}

static int GateExe(void)
{
	int ok=1;
	if(g_cfg.requireExe[0])
	{
		wchar_t exe[MAX_PATH]; exe[0]=0;
		GetModuleFileNameW(NULL, exe, MAX_PATH);
		if(lstrcmpiW(BaseName(exe), g_cfg.requireExe) != 0) ok=0;
	}
	if(ok && !VersionMatches(g_cfg.exeVersion, g_cfg.requireExeVersion)) ok=0;
	return ok;
}

static void MakeOrigPath(const wchar_t *self, wchar_t *out, int cap)
{
	// <dir>\<stem><suffix>.dll
	wchar_t stem[MAX_PATH]; stem[0]=0;
	lstrcpynW(stem, self, MAX_PATH);
	wchar_t *base = (wchar_t *)BaseName(stem);
	wchar_t *dot = NULL;
	for(wchar_t *p=base; *p; ++p) if(*p==L'.') dot=p;
	if(dot) *dot=0;
	wchar_t buf[MAX_PATH*2];
	wsprintfW(buf, L"%s%s.dll", stem, g_cfg.origSuffix);
	lstrcpynW(out, buf, cap);
}

static int GateOrigHash(const wchar_t *origPath, wchar_t *hex, int cap)
{
	if(GetFileAttributesW(origPath) == INVALID_FILE_ATTRIBUTES) return 0;
	if(!Sha256File(origPath, hex, cap)) return 0;
	if(!g_cfg.requireOrigSha256[0]) return 1;
	return lstrcmpiW(hex, g_cfg.requireOrigSha256) == 0;
}

// ---------------------------------------------------------------- capture ---
static void CapWriteHeader(void)
{
	wchar_t b[1024];
	wsprintfW(b, L"# kkz-adapter v%s capture pid=%u exe_ver=%s warmup_ms=%d poll_ms=%d",
		KKZ_VER, (unsigned)GetCurrentProcessId(), g_cfg.exeVersion, g_cfg.warmupMs, g_cfg.pollMs);
	AppendFileUtf8(g_capPath, b);
	AppendFileUtf8(g_capPath, L"# fields: time<TAB>name<TAB>text   (CR/LF escaped as \\n, TAB as space)");
	AppendFileUtf8(g_capPath, L"# text source: kag.historyLayer.currentInfo.text(zh) / .disp(zh name) / .plaintext(ja fallback)");
}

static void CapWriteRow(const wchar_t *name, const wchar_t *text)
{
	static wchar_t ts[64];
	static wchar_t en[4096];
	static wchar_t et[16384];
	static wchar_t line[24576];
	CapStamp(ts, 64);
	EscapeText(name, en, 4096);
	EscapeText(text, et, 16384);
	wsprintfW(line, L"%s\t%s\t%s", ts, en, et);
	AppendFileUtf8(g_capPath, line);
}

// ---------------------------------------------------------------- clicking --
struct OwnCtx { HWND best; long area; DWORD pid; };

static BOOL CALLBACK MainEnumProc(HWND h, LPARAM lp)
{
	OwnCtx *p=(OwnCtx *)lp;
	DWORD pid=0;
	GetWindowThreadProcessId(h, &pid);
	if(pid != p->pid) return TRUE;
	if(!IsWindowVisible(h)) return TRUE;
	RECT rc;
	if(!GetClientRect(h, &rc)) return TRUE;
	long a=(long)(rc.right-rc.left)*(long)(rc.bottom-rc.top);
	if(a > p->area) { p->area=a; p->best=h; }
	return TRUE;
}

static HWND OwnMainWindow(void)
{
	OwnCtx c; c.best=0; c.area=0; c.pid=GetCurrentProcessId();
	EnumWindows(MainEnumProc, (LPARAM)&c);
	return c.best;
}

struct DlgCtx { DWORD pid; int found; };
static BOOL CALLBACK DlgEnumProc(HWND h, LPARAM lp)
{
	DlgCtx *c=(DlgCtx *)lp;
	DWORD pid=0;
	GetWindowThreadProcessId(h, &pid);
	if(pid != c->pid) return TRUE;
	if(!IsWindowVisible(h)) return TRUE;
	wchar_t cls[64]; cls[0]=0;
	GetClassNameW(h, cls, 64);
	if(lstrcmpiW(cls, L"#32770") == 0)
	{
		wchar_t t[128]; t[0]=0;
		GetWindowTextW(h, t, 128);
		if(t[0]) { c->found=1; return FALSE; }
	}
	return TRUE;
}

static int OwnDialogPresent(void)
{
	DlgCtx c; c.pid=GetCurrentProcessId(); c.found=0;
	EnumWindows(DlgEnumProc, (LPARAM)&c);
	return c.found;
}

static void PostClickAt(double fx, double fy)
{
	HWND h = OwnMainWindow();
	if(!h) { LogLine(L"postclick_nowin"); return; }
	RECT rc;
	if(!GetClientRect(h, &rc)) { LogLine(L"postclick_norect"); return; }
	int w=rc.right-rc.left, ht=rc.bottom-rc.top;
	if(w<=0 || ht<=0) { LogLine(L"postclick_badrect"); return; }
	int x=(int)(fx*w+0.5), y=(int)(fy*ht+0.5);
	LPARAM lp=MAKELPARAM(x, y);
	PostMessageW(h, WM_MOUSEMOVE, 0, lp);
	PostMessageW(h, WM_LBUTTONDOWN, MK_LBUTTON, lp);
	PostMessageW(h, WM_LBUTTONUP, 0, lp);
	wchar_t b[256];
	wsprintfW(b, L"postclick #%d x=%d y=%d of %dx%d", g_clicks, x, y, w, ht);
	LogLine(b);
}

static void MaybeClick(void)
{
	if(!g_cfg.autoClick || g_abort) return;
	if(g_rows >= g_cfg.clickRowsGoal) return;
	if(g_clicks >= g_cfg.maxClicks)
	{
		if(!g_clickGiveupLogged)
		{
			g_clickGiveupLogged=1;
			LogI(L"click_giveup clicks=", g_clicks);
			LogI(L"click_giveup rows=", g_rows);
			g_zeroRowsSince = GetTickCount();
		}
		return;
	}
	if(OwnDialogPresent()) { if((g_ticks % 20)==1) LogLine(L"wait_dialog"); return; }
	DWORD now=GetTickCount();
	if(g_lastClickTick && (now - g_lastClickTick) < (DWORD)g_cfg.clickIntervalMs) return;
	g_lastClickTick = now;

	static const double kNewGame[2] = {0.060, 0.345}; // 标题菜单「新遊戲」左上第一项（R27 实测）
	static const double kAdv[][2] = {
		{0.500, 0.750}, {0.500, 0.700}, {0.500, 0.800}, {0.520, 0.760}
	};
	int na=(int)(sizeof(kAdv)/sizeof(kAdv[0]));
	// 第一次点击只点「新遊戲」；之后就只在文本框区域点，避免反复点进标题菜单
	if(g_clickSeq == 0) PostClickAt(kNewGame[0], kNewGame[1]);
	else                PostClickAt(kAdv[(g_clickSeq-1) % na][0], kAdv[(g_clickSeq-1) % na][1]);
	++g_clickSeq;
	++g_clicks;
}

// ---------------------------------------------------------------- poll ------
static void ChooseText(const wchar_t *text, const wchar_t *plain, wchar_t *out, int cap)
{
	// HasKana 兜底：text 为空 -> 用 plaintext；
	// text 看起来是日文（含假名）而 plaintext 不含假名 -> 用 plaintext（选项场景两字段语义互换）
	lstrcpynW(out, text, cap);
	if(!out[0]) { lstrcpynW(out, plain, cap); return; }
	if(HasKana(out) && plain[0] && !HasKana(plain)) lstrcpynW(out, plain, cap);
}

static void PollOnce(void)
{
	static wchar_t name[1024];
	static wchar_t text[8192];
	static wchar_t plain[8192];
	static wchar_t chosen[16384];
	static wchar_t key[20480];
	name[0]=0; text[0]=0; plain[0]=0; chosen[0]=0;
	unsigned vtN=0, vtT=0, vtP=0;
	int stN = EvalStr(L"global.kag.historyLayer.currentInfo.disp", name, 1024, &vtN);
	int stT = EvalStr(L"global.kag.historyLayer.currentInfo.text", text, 8192, &vtT);
	int stP = EvalStr(L"global.kag.historyLayer.currentInfo.plaintext", plain, 8192, &vtP);
	++g_polls;

	if(!g_firstPollLogged)
	{
		g_firstPollLogged=1;
		wchar_t b[512];
		wsprintfW(b, L"poll_first stN=%d vtN=%d stT=%d vtT=%d stP=%d vtP=%d",
			stN, (int)vtN, stT, (int)vtT, stP, (int)vtP);
		LogLine(b);
	}
	if(stT != 1) text[0]=0;
	if(stP != 1) plain[0]=0;
	if(stN != 1) name[0]=0;

	TrimEnd(name); TrimEnd(text); TrimEnd(plain);

	ChooseText(text, plain, chosen, 16384);
	if(!chosen[0] || (int)lstrlenW(chosen) < g_cfg.minChars)
	{
		g_pendingKey[0]=0; g_pendingAge=0;
		return;
	}

	lstrcpynW(key, name, 1024);
	lstrcatW(key, L"\x01");
	lstrcatW(key, chosen);

	if(lstrcmpW(key, g_pendingKey) != 0)
	{
		lstrcpynW(g_pendingKey, key, 20480);
		g_pendingAge=0;
		return;
	}
	g_pendingAge += g_cfg.pollMs;
	if(g_pendingAge < g_cfg.stabilizeMs) return;

	if(lstrcmpW(key, g_lastKey) == 0) return;
	lstrcpynW(g_lastKey, key, 20480);

	++g_rows;
	CapWriteRow(name, chosen);
	{
		wchar_t b[512];
		wsprintfW(b, L"row=%d name_len=%d text_len=%d kana=%d", g_rows,
			(int)lstrlenW(name), (int)lstrlenW(chosen), HasKana(chosen));
		LogLine(b);
	}
}

// ---------------------------------------------------------------- tick ------
static void Tick(void)
{
	if(InterlockedExchange(&g_tickBusy, 1) == 1) return;
	if(!g_ready || g_abort) { g_tickBusy=0; return; }
	++g_ticks;

	if(!g_warmupDone)
	{
		if((g_ticks % 20)==1) LogI(L"warmup_wait tick=", g_ticks);
		g_tickBusy=0;
		return;
	}
	try
	{
		PollOnce();
		MaybeClick();
	}
	catch(...) { LogLine(L"tick_cpp_exception"); }

	if(g_clickGiveupLogged && !g_abortLogged && g_rows == 0)
	{
		if((GetTickCount() - g_zeroRowsSince) >= (DWORD)g_cfg.quietAfterClicksMs)
		{
			g_abortLogged=1;
			g_abort=1;
			LogI(L"capture_abort rows=0 clicks=", g_clicks);
			LogLine(L"capture_abort reason=no_line_after_clicks_and_quiet_window");
		}
	}
	if((g_ticks % 60)==0)
	{
		wchar_t b[256];
		wsprintfW(b, L"tick=%d polls=%d rows=%d clicks=%d elapsed_ms=%d", g_ticks, g_polls,
			g_rows, g_clicks, (int)(GetTickCount() - g_startTick));
		LogLine(b);
	}
	g_tickBusy=0;
}

static LRESULT CALLBACK KkzWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp)
{
	if(msg == WM_TIMER && wp == KKZ_TIMER_ID)
	{
		try { Tick(); } catch(...) { /* 兜底：任何异常都不许冒泡到引擎 */ }
		return 0;
	}
	if(msg == WM_DESTROY) { KillTimer(hWnd, KKZ_TIMER_ID); return 0; }
	return DefWindowProcW(hWnd, msg, wp, lp);
}

static void EnsurePump(void)
{
	WNDCLASSW wc;
	ZeroMemory(&wc, sizeof(wc));
	wc.lpfnWndProc = KkzWndProc;
	wc.hInstance = (HINSTANCE)GetModuleHandleW(NULL);
	wc.lpszClassName = L"KkzAdapterMsgWindow";
	RegisterClassW(&wc);
	HWND h = CreateWindowExW(0, L"KkzAdapterMsgWindow", L"", 0, 0, 0, 0, 0,
		HWND_MESSAGE, NULL, wc.hInstance, NULL);
	if(!h) { LogI(L"pump_fail err=", (int)GetLastError()); return; }
	SetTimer(h, KKZ_TIMER_ID, (UINT)g_cfg.pollMs, NULL);
	wchar_t b[256];
	wsprintfW(b, L"pump=ok timer=%dms tid=%u", g_cfg.pollMs, (unsigned)GetCurrentThreadId());
	LogLine(b);
}

static DWORD WINAPI WarmupThread(LPVOID)
{
	DWORD t0 = GetTickCount();
	for(;;)
	{
		Sleep(200);
		if(!g_ready) { LogLine(L"warmup_thread_abort not_ready"); return 0; }
		if((GetTickCount() - t0) >= (DWORD)g_cfg.warmupMs) break;
	}
	InterlockedExchange(&g_warmupDone, 1);
	wchar_t b[256];
	wsprintfW(b, L"warmup_done wait_ms=%d", (int)(GetTickCount() - t0));
	LogLine(b);
	return 0;
}

// ---------------------------------------------------------------- TjsSetup --
static void TjsSetup(void *exporter)
{
	if(g_inject) return;
	g_inject=1;
	g_startTick = GetTickCount();

	LogLine(L"---- TjsSetup begin");
	LogStamp();
	if(!exporter) { LogLine(L"tjs=FAIL reason=null_exporter"); return; }

	iTVPFunctionExporter *exp=(iTVPFunctionExporter *)exporter;
	struct QItem { const char *name; void **slot; const wchar_t *label; };
	QItem items[] = {
		{ "iTJSDispatch2 * ::TVPGetScriptDispatch()",                        NULL, L"TVPGetScriptDispatch" },
		{ "void ::TVPExecuteExpression(const ttstr &,tTJSVariant *)",        NULL, L"TVPExecuteExpression" },
		{ "void ::TVPExecuteScript(const ttstr &,tTJSVariant *)",            NULL, L"TVPExecuteScript" },
		{ "tTJSVariantString * ::TJSAllocVariantString(const tjs_char *)",   NULL, L"TJSAllocVariantString" },
	};
	void *dispatch=NULL;
	items[0].slot=&dispatch; items[1].slot=(void **)&g_execExpr;
	items[2].slot=(void **)&g_execScript; items[3].slot=(void **)&g_allocStr;
	int okCount=0;
	for(int i=0;i<4;++i)
	{
		const char *nm=items[i].name;
		void *f=NULL; bool ok=false;
		try { ok = exp->QueryFunctionsByNarrowString(&nm, &f, 1); } catch(...) { ok=false; }
		*(items[i].slot)=f;
		if(f) ++okCount;
		wchar_t q[256];
		wsprintfW(q, L"Q %s ok=%d f=%08X", items[i].label, ok?1:0, (unsigned)(UINT_PTR)f);
		LogLine(q);
	}
	LogI(L"Q_total=", okCount);
	if(!g_allocStr || !g_execExpr) { LogLine(L"tjs=FAIL reason=no_exec"); return; }

	// 自检：一个纯 ASCII 表达式，确认通道可用（不碰 KAG）
	{
		unsigned vt=0; wchar_t got[128]={0};
		int st = EvalStr(L"\"KKZ_ADAPTER_OK\"", got, 128, &vt);
		LogI(L"selftest_st=", st);
		LogKV(L"selftest=", got);
	}

	EnsurePump();
	g_ready=1;
	HANDLE th = CreateThread(NULL, 0, WarmupThread, NULL, 0, NULL);
	if(th) CloseHandle(th); else LogI(L"warmup_thread_fail err=", (int)GetLastError());
	LogLine(L"tjs=OK armed=1");
}

// ---------------------------------------------------------------- exports ---
extern "C" HRESULT __stdcall V2Link(void *exporter)
{
	CfgLoad();
	wchar_t self[MAX_PATH]; self[0]=0;
	{
		HMODULE hm=NULL;
		if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|
		                      GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                      (LPCWSTR)(const void *)&V2Link, &hm))
			GetModuleFileNameW(hm, self, MAX_PATH);
	}
	wchar_t orig[MAX_PATH]; orig[0]=0;
	if(self[0]) MakeOrigPath(self, orig, MAX_PATH);

	GateExeVersion();

	// 日志/采集路径（先算出来，后面所有内容都写这里）
	wchar_t capDir[MAX_PATH]; ExpandEnv(g_cfg.captureDir, capDir, MAX_PATH);
	EnsureDir(capDir);
	if(g_cfg.logFile[0]) { ExpandEnv(g_cfg.logFile, g_logPath, MAX_PATH); }
	else wsprintfW(g_logPath, L"%s\\kkz-adapter.log", capDir);
	wsprintfW(g_capPath, L"%s\\capture-%u.tsv", capDir, (unsigned)GetCurrentProcessId());
	EnsureDir(capDir);
	g_logReady=1; g_capReady=1;

	LogLine(L"================ kkz-adapter v" KKZ_VER L" V2Link");
	LogStamp();
	LogKV(L"self=", self);
	LogKV(L"orig=", orig);
	LogKV(L"ini=", g_cfg.iniPath[0] ? g_cfg.iniPath : L"(none, defaults)");
	LogKV(L"capture=", g_capPath);
	LogKV(L"exe_version=", g_cfg.exeVersion);
	LogI(L"cfg poll_ms=", g_cfg.pollMs);
	LogI(L"cfg warmup_ms=", g_cfg.warmupMs);
	LogI(L"cfg stabilize_ms=", g_cfg.stabilizeMs);
	LogI(L"cfg min_chars=", g_cfg.minChars);
	LogI(L"cfg auto_click=", g_cfg.autoClick);

	// ---- 三闸（文件名 / 版本 / 原 DLL 哈希）----
	int gateName = GateFileName();
	int gateExe = GateExe();
	wchar_t origHex[80]; origHex[0]=0;
	int gateHash = GateOrigHash(orig, origHex, 80);
	LogI(L"gate_filename_ok=", gateName);
	LogI(L"gate_exe_ok=", gateExe);
	LogI(L"gate_orig_exists=", GetFileAttributesW(orig)!=INVALID_FILE_ATTRIBUTES ? 1 : 0);
	LogKV(L"orig_sha256=", origHex);
	LogI(L"gate_orig_hash_ok=", gateHash);
	g_gatesOk = gateName && gateExe && gateHash;
	LogI(L"gate_all_ok=", g_gatesOk);

	// ---- 转发原插件（无论闸门是否通过都要转发，保证游戏行为尽量不变）----
	DWORD err=0;
	HRESULT hr=E_FAIL;
	int forwarded=0;
	if(GetFileAttributesW(orig) != INVALID_FILE_ATTRIBUTES)
	{
		if(!g_orig)
		{
			SetLastError(0);
			g_orig = LoadLibraryW(orig);
			err = GetLastError();
			if(g_orig)
			{
				g_origV2Link = (void *)GetProcAddress(g_orig, "V2Link");
				g_origV2Unlink = (void *)GetProcAddress(g_orig, "V2Unlink");
				g_origThreadModel = (void *)GetProcAddress(g_orig, "GetModuleThreadModel");
			}
		}
		if(g_orig && g_origV2Link)
		{
			forwarded=1;
			typedef HRESULT (__stdcall *fnLink)(void *);
			hr = ((fnLink)g_origV2Link)(exporter);
		}
	}
	LogI(L"load_ok=", g_orig ? 1 : 0);
	LogI(L"lasterr=", (int)err);
	LogI(L"forwarded=", forwarded);
	LogI(L"forward_ret=", (int)hr);

	if(g_gatesOk && forwarded)
	{
		CapWriteHeader();
		try { TjsSetup(exporter); } catch(...) { LogLine(L"tjs=FAIL reason=setup_cpp_exception"); }
	}
	else
	{
		CapWriteHeader();
		LogLine(L"tjs=SKIP fail_mode=degraded（游戏照常跑，不注入）");
	}
	return hr;
}

extern "C" HRESULT __stdcall V2Unlink(void)
{
	LogLine(L"---- V2Unlink");
	LogI(L"ticks=", g_ticks);
	LogI(L"rows=", g_rows);
	LogI(L"clicks=", g_clicks);
	g_ready=0;
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

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }

// ---- offline self-test build: cl /DKKZ_TEST_MAIN ... (console exe, no game) ----
#ifdef KKZ_TEST_MAIN
#include <stdio.h>
int wmain(int argc, wchar_t **argv)
{
	if(argc < 2) { wprintf(L"usage: sha256 <file>\n"); return 2; }
	wchar_t hex[80]; hex[0]=0;
	if(!Sha256File(argv[1], hex, 80)) { wprintf(L"SHA256_FAIL\n"); return 3; }
	wprintf(L"%s\n", hex);
	// second check: hash the same file twice, must agree (determinism)
	wchar_t hex2[80]; hex2[0]=0;
	Sha256File(argv[1], hex2, 80);
	wprintf(L"deterministic=%d\n", lstrcmpiW(hex, hex2)==0 ? 1 : 0);
	return 0;
}
#endif
