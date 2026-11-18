// kkr_dllproxy.cpp — task-45 / R24
// 目的：验证「DLL 代理注入」在 SenrenBanka (KiriKiri Z 1.2.0.3, x86, Steam DRM) 上是否成立。
// 机制：本 DLL 顶替一个已被脚本 Plugins.link 点名的插件 dll（本次 getSample.dll）；
//       在 V2Link(exporter) 里 LoadLibrary 真正的原件（同目录 <name>_orig.dll），
//       把同一个 exporter 指针转发给原件的 V2Link，再把结果返回给引擎。
// 约束：不调用任何 TVP API（只用 kernel32），避免引擎版本/ABI 依赖。
// 证据：在宿主 exe 目录写 kkr_dllproxy_probe.txt（UTF-16LE + BOM）。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

typedef HRESULT (__stdcall *pfnV2Link)(void *exporter);
typedef HRESULT (__stdcall *pfnV2Unlink)(void);
typedef int     (__stdcall *pfnGetModuleThreadModel)(void);

static HMODULE g_orig = NULL;
static pfnV2Link g_origV2Link = NULL;
static pfnV2Unlink g_origV2Unlink = NULL;
static pfnGetModuleThreadModel g_origThreadModel = NULL;

// ---- 无 CRT 的字符串拼接（避免引入运行库依赖）----
static wchar_t *AppendW(wchar_t *d, const wchar_t *s)
{
	while(*s) *d++ = *s++;
	return d;
}

static wchar_t *AppendUIntW(wchar_t *d, unsigned int v)
{
	wchar_t t[12];
	int n = 0;
	if(v == 0) t[n++] = L'0';
	while(v) { t[n++] = (wchar_t)(L'0' + (v % 10)); v /= 10; }
	while(n) *d++ = t[--n];
	return d;
}

static wchar_t *Append2W(wchar_t *d, unsigned int v)
{
	*d++ = (wchar_t)(L'0' + (v / 10) % 10);
	*d++ = (wchar_t)(L'0' + (v % 10));
	return d;
}

static wchar_t *AppendHexW(wchar_t *d, unsigned int v)
{
	const wchar_t *hx = L"0123456789ABCDEF";
	for(int i = 28; i >= 0; i -= 4) *d++ = hx[(v >> i) & 0xF];
	return d;
}

// 取本 DLL 的完整路径
static DWORD GetSelfPath(wchar_t *buf, DWORD cap)
{
	HMODULE hm = NULL;
	if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(const void *)&GetSelfPath, &hm))
		return 0;
	return GetModuleFileNameW(hm, buf, cap);
}

// 由自身路径推导原件路径：<dir>\<stem>.dll -> <dir>\<stem>_orig.dll
static bool MakeOrigPath(const wchar_t *self, wchar_t *out, DWORD cap)
{
	DWORD n = (DWORD)lstrlenW(self);
	if(n == 0 || n + 16 >= cap) return false;
	DWORD dot = n;
	while(dot > 0 && self[dot - 1] != L'.' && self[dot - 1] != L'\\') dot--;
	if(dot == 0 || self[dot - 1] != L'.') return false; // 没有 .dll 后缀

	DWORD p = 0;
	for(DWORD i = 0; i < dot - 1 && p + 1 < cap; ++i) out[p++] = self[i];
	out[p] = 0;
	wchar_t *q = out + p;
	q = AppendW(q, L"_orig");
	for(DWORD i = dot - 1; i < n; ++i) *q++ = self[i];
	*q = 0;
	return true;
}

// 在宿主 exe 目录写 marker
static void WriteMarker(const wchar_t *self, const wchar_t *orig,
                        int loaded, DWORD lasterr, int hasLink,
                        HRESULT ret)
{
	wchar_t exe[MAX_PATH];
	DWORD n = GetModuleFileNameW(NULL, exe, MAX_PATH);
	if(n == 0 || n >= MAX_PATH) return;
	DWORD cut = n;
	while(cut > 0 && exe[cut - 1] != L'\\') cut--;
	exe[cut] = 0;

	wchar_t path[MAX_PATH + 40];
	wchar_t *p = path;
	p = AppendW(p, exe);
	p = AppendW(p, L"kkr_dllproxy_probe.txt");
	*p = 0;

	wchar_t buf[4096];
	wchar_t *b = buf;
	*b++ = 0xFEFF;
	b = AppendW(b, L"KKR_DLLPROXY_PROBE v1\r\nevent=V2Link");

	SYSTEMTIME st;
	GetSystemTime(&st);
	b = AppendW(b, L"\r\nutc=");
	b = AppendUIntW(b, st.wYear); *b++ = L'-';
	b = Append2W(b, st.wMonth);   *b++ = L'-';
	b = Append2W(b, st.wDay);     *b++ = L'T';
	b = Append2W(b, st.wHour);    *b++ = L':';
	b = Append2W(b, st.wMinute);  *b++ = L':';
	b = Append2W(b, st.wSecond);  *b++ = L'Z';

	b = AppendW(b, L"\r\npid=");  b = AppendUIntW(b, GetCurrentProcessId());
	b = AppendW(b, L"\r\ntid=");  b = AppendUIntW(b, GetCurrentThreadId());

	b = AppendW(b, L"\r\nhostdir="); b = AppendW(b, exe);
	b = AppendW(b, L"\r\nself=");    b = AppendW(b, self);
	b = AppendW(b, L"\r\norig=");    b = AppendW(b, orig);
	b = AppendW(b, L"\r\nload=");    b = AppendUIntW(b, (unsigned)loaded);
	b = AppendW(b, L"\r\nlasterr="); b = AppendUIntW(b, lasterr);
	b = AppendW(b, L"\r\norigv2link="); b = AppendUIntW(b, (unsigned)hasLink);
	b = AppendW(b, L"\r\nret=0x");  b = AppendHexW(b, (unsigned)ret);
	b = AppendW(b, L"\r\n");

	HANDLE h = CreateFileW(path, GENERIC_WRITE,
		FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, CREATE_ALWAYS,
		FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return;
	DWORD wrote = 0;
	WriteFile(h, buf, (DWORD)((b - buf) * sizeof(wchar_t)), &wrote, NULL);
	CloseHandle(h);
}

extern "C" HRESULT __stdcall V2Link(void *exporter)
{
	wchar_t self[MAX_PATH];
	wchar_t orig[MAX_PATH];
	DWORD selfLen = GetSelfPath(self, MAX_PATH);
	if(selfLen == 0 || selfLen >= MAX_PATH) { self[0] = 0; }
	bool pathOk = (self[0] != 0) && MakeOrigPath(self, orig, MAX_PATH);
	if(!pathOk) orig[0] = 0;

	DWORD err = 0;
	HRESULT hr = E_FAIL;
	int hasLink = 0;

	if(pathOk)
	{
		SetLastError(0);
		if(g_orig == NULL)
		{
			g_orig = LoadLibraryW(orig);
			err = GetLastError();
			if(g_orig)
			{
				g_origV2Link = (pfnV2Link)GetProcAddress(g_orig, "V2Link");
				g_origV2Unlink = (pfnV2Unlink)GetProcAddress(g_orig, "V2Unlink");
				g_origThreadModel = (pfnGetModuleThreadModel)
					GetProcAddress(g_orig, "GetModuleThreadModel");
			}
		}
		else
		{
			err = 0; // 已加载（V2Link 被重复调用）
		}
		if(g_orig && g_origV2Link)
		{
			hasLink = 1;
			hr = g_origV2Link(exporter);   // 转发同一个 exporter
		}
	}
	else
	{
		err = ERROR_PATH_NOT_FOUND; // 183/3
	}

	WriteMarker(self, orig, g_orig ? 1 : 0, err, hasLink, hr);
	return hr;
}

extern "C" HRESULT __stdcall V2Unlink(void)
{
	if(g_origV2Unlink) return g_origV2Unlink();
	if(g_origV2Link == NULL && g_orig == NULL)
	{
		// 极小概率：引擎先 Unlink 后 Link，尝试按需加载
		wchar_t self[MAX_PATH];
		wchar_t orig[MAX_PATH];
		DWORD n = GetSelfPath(self, MAX_PATH);
		if(n > 0 && n < MAX_PATH && MakeOrigPath(self, orig, MAX_PATH))
		{
			g_orig = LoadLibraryW(orig);
			if(g_orig) g_origV2Unlink = (pfnV2Unlink)GetProcAddress(g_orig, "V2Unlink");
		}
		if(g_origV2Unlink) return g_origV2Unlink();
	}
	return S_OK;
}

extern "C" int __stdcall GetModuleThreadModel(void)
{
	if(g_origThreadModel) return g_origThreadModel();
	return 0; // 原件未导出该函数时：主线程模型
}

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID)
{
	return TRUE;
}
