// kkr_probe.cpp — task-44 / R1 探针
// 目的：只验证 SenrenBanka（KiriKiri Z 1.2.0.3, x86）是否自动扫描并加载 plugin\*.tpm。
// 约束：不调用任何 TVP API（避免版本/ABI 依赖）；只用 kernel32 Win32 API；
//       在宿主 exe 同目录写 kkr_tpm_probe.txt（UTF-16LE + BOM），然后返回 S_OK。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#ifndef KKR_PROBE_TJS
#define KKR_PROBE_TJS 0
#endif

static int PutStr(wchar_t *d, int p, const wchar_t *s)
{
	while(*s) d[p++] = *s++;
	return p;
}

static int PutUInt(wchar_t *d, int p, unsigned int v)
{
	wchar_t t[12];
	int n = 0;
	if(v == 0) t[n++] = L'0';
	while(v) { t[n++] = (wchar_t)(L'0' + (v % 10)); v /= 10; }
	while(n) d[p++] = t[--n];
	return p;
}

static int Put2(wchar_t *d, int p, unsigned int v)
{
	d[p++] = (wchar_t)(L'0' + (v / 10) % 10);
	d[p++] = (wchar_t)(L'0' + (v % 10));
	return p;
}

static int PutHex(wchar_t *d, int p, unsigned int v)
{
	const wchar_t *hx = L"0123456789ABCDEF";
	for(int i = 28; i >= 0; i -= 4) d[p++] = hx[(v >> i) & 0xF];
	return p;
}

// 在宿主 exe 所在目录写 marker；宿主 exe 路径由 GetModuleFileNameW(NULL) 得到
// （此时 DLL 已被 LoadLibrary，进程主模块仍是游戏 exe）。
static void WriteProbeFile(const wchar_t *event, void *exporter)
{
	wchar_t exe[MAX_PATH];
	DWORD n = GetModuleFileNameW(NULL, exe, MAX_PATH);
	if(n == 0 || n >= MAX_PATH) return;

	int cut = (int)n;
	while(cut > 0 && exe[cut - 1] != L'\\') cut--;

	wchar_t path[MAX_PATH + 40];
	int p = 0;
	for(int i = 0; i < cut; ++i) path[p++] = exe[i];
	p = PutStr(path, p, L"kkr_tpm_probe.txt");
	path[p] = 0;

	wchar_t buf[2048];
	int b = 0;
	buf[b++] = 0xFEFF; // BOM，便于 PowerShell Get-Content 直接读
	b = PutStr(buf, b, L"KRR_TPM_PROBE v1\r\nevent=");
	b = PutStr(buf, b, event);

	SYSTEMTIME st;
	GetSystemTime(&st);
	b = PutStr(buf, b, L"\r\nutc=");
	b = PutUInt(buf, b, st.wYear); buf[b++] = L'-';
	b = Put2(buf, b, st.wMonth);  buf[b++] = L'-';
	b = Put2(buf, b, st.wDay);    buf[b++] = L'T';
	b = Put2(buf, b, st.wHour);   buf[b++] = L':';
	b = Put2(buf, b, st.wMinute); buf[b++] = L':';
	b = Put2(buf, b, st.wSecond); buf[b++] = L'Z';

	b = PutStr(buf, b, L"\r\npid=");
	b = PutUInt(buf, b, GetCurrentProcessId());
	b = PutStr(buf, b, L"\r\ntid=");
	b = PutUInt(buf, b, GetCurrentThreadId());

	b = PutStr(buf, b, L"\r\nexe=");
	for(int i = 0; i < cut; ++i) buf[b++] = exe[i];

	b = PutStr(buf, b, L"\r\nexporter=");
	b = PutHex(buf, b, (unsigned int)(ULONG_PTR)exporter);

#if KKR_PROBE_TJS
	b = PutStr(buf, b, L"\r\ntjs=1");
#else
	b = PutStr(buf, b, L"\r\ntjs=0");
#endif
	b = PutStr(buf, b, L"\r\n");

	HANDLE h = CreateFileW(path, GENERIC_WRITE,
		FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, CREATE_ALWAYS,
		FILE_ATTRIBUTE_NORMAL, NULL);
	if(h == INVALID_HANDLE_VALUE) return;
	DWORD wrote = 0;
	WriteFile(h, buf, (DWORD)(b * sizeof(wchar_t)), &wrote, NULL);
	CloseHandle(h);
}

extern "C" __declspec(dllexport) HRESULT __stdcall V2Link(void *exporter)
{
	WriteProbeFile(L"V2Link", exporter);
	return 0; // S_OK
}

extern "C" __declspec(dllexport) HRESULT __stdcall V2Unlink(void)
{
	return 0;
}

extern "C" __declspec(dllexport) int __stdcall GetModuleThreadModel(void)
{
	return 0; // 与 task-41 实验保持一致
}
