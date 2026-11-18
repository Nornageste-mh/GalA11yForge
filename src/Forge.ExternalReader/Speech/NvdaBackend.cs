using System.Runtime.InteropServices;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Speech;

/// <summary>
/// NVDA 直连（nvdaControllerClient64.dll，LGPL-2.1，不随包）。
///
/// 探测顺序：--nvda-dll → 本 exe 目录 → %TEMP%\forgereader\dll → D:\DSHWorkSpace\_tolk_probe
/// → %ProgramFiles%\NVDA → 系统搜索。加载成功不等于 NVDA 在跑：
/// 还要 nvdaController_testIfRunning()==0 才算可用（R6 实测 rc=0 表示运行中）。
/// </summary>
public sealed class NvdaBackend : ISpeechBackend
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TestIfRunningFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int SpeakTextFn(string text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CancelSpeechFn();

    private IntPtr _module;
    private TestIfRunningFn? _testIfRunning;
    private SpeakTextFn? _speakText;
    private CancelSpeechFn? _cancelSpeech;

    public string Name => "nvda";
    public string? DllPath { get; private set; }
    public string? LastError { get; private set; }

    public bool TryInit(out string? error) => TryInit(null, out error);

    public bool TryInit(string? explicitPath, out string? error)
    {
        error = null;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "nvdaControllerClient64.dll"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "forgereader", "dll", "nvdaControllerClient64.dll"));
        candidates.Add(@"D:\DSHWorkSpace\_tolk_probe\nvdaControllerClient64.dll");
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidates.Add(Path.Combine(pf, "NVDA", "nvdaControllerClient64.dll"));
        candidates.Add(Path.Combine(pf86, "NVDA", "nvdaControllerClient64.dll"));
        candidates.Add("nvdaControllerClient64.dll"); // 走系统搜索路径

        foreach (var c in candidates)
        {
            var isBare = !Path.IsPathRooted(c);
            if (!isBare && !File.Exists(c)) continue;
            var h = LoadLibraryW(c);
            if (h == IntPtr.Zero) continue;

            var test = GetProcAddress(h, "nvdaController_testIfRunning");
            var speak = GetProcAddress(h, "nvdaController_speakText");
            var cancel = GetProcAddress(h, "nvdaController_cancelSpeech");
            if (test == IntPtr.Zero || speak == IntPtr.Zero || cancel == IntPtr.Zero)
            {
                FreeLibrary(h);
                continue;
            }

            _module = h;
            _testIfRunning = Marshal.GetDelegateForFunctionPointer<TestIfRunningFn>(test);
            _speakText = Marshal.GetDelegateForFunctionPointer<SpeakTextFn>(speak);
            _cancelSpeech = Marshal.GetDelegateForFunctionPointer<CancelSpeechFn>(cancel);
            DllPath = c;

            var rc = 0;
            try { rc = _testIfRunning(); }
            catch (Exception ex)
            {
                error = "nvdaController_testIfRunning 抛异常: " + ex.Message;
                return false;
            }
            if (rc != 0)
            {
                error = $"NVDA 未运行（testIfRunning=0x{rc:X8}）";
                return false;
            }
            return true;
        }

        error = "找不到可用的 nvdaControllerClient64.dll（NVDA 未安装或未提供 client 包）";
        return false;
    }

    public bool Speak(string text)
    {
        if (_speakText is null || string.IsNullOrEmpty(text)) return false;
        try
        {
            var rc = _speakText(text);
            if (rc != 0)
            {
                LastError = $"nvdaController_speakText 返回 0x{rc:X8}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public void Cancel()
    {
        try { _cancelSpeech?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_module != IntPtr.Zero)
        {
            try { FreeLibrary(_module); } catch { }
            _module = IntPtr.Zero;
        }
        _testIfRunning = null;
        _speakText = null;
        _cancelSpeech = null;
    }
}

/// <summary>只记日志不出声的后端：e2e 自动化时不打扰用户，也能验证“文本→朗读队列”链路。</summary>
public sealed class NullBackend : ISpeechBackend
{
    private readonly Log _log;
    public NullBackend(Log log) => _log = log;
    public string Name => "null";
    public bool TryInit(out string? error) { error = null; return true; }
    public bool Speak(string text) { _log.Evt("speech.null", "未发声（--speech null）", ("len", text.Length)); return true; }
    public void Cancel() { }
    public void Dispose() { }
}
