using System.Runtime.InteropServices;

namespace GalA11yForge.ExternalReader.Speech;

/// <summary>
/// SAPI5（ISpVoice）纯 vtable P/Invoke —— 不用 COM 互操作、不用 System.Speech 包。
///
/// 这套槽位来自已发布补丁的 Framework/platform/Sapi.cs（本机实测过 rate/volume/Speak），
/// 之所以照抄而不是用 <c>new SpeechSynthesizer()</c>：.NET 8 下 System.Speech 需要额外
/// NuGet 包且要 SAPI 的托管包装；vtable 方式零依赖、行为可预测。
///
/// 必须异步：Framework 的实测里，阻塞式念 68 字要 17.6 秒（R6）。
/// </summary>
public sealed class SapiBackend : ISpeechBackend
{
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint CLSCTX_ALL = 0x17;

    private static readonly Guid CLSID_SpVoice = new("96749377-3391-11d2-9ee3-00c04f797396");
    private static readonly Guid IID_ISpVoice = new("6c44df74-72b9-4992-a1ec-ef996e0422d4");

    private const int VT_RELEASE = 2;
    private const int VT_SPEAK = 20;
    private const int VT_SETRATE = 28;
    private const int VT_GETRATE = 29;
    private const int VT_GETVOLUME = 31;

    private const uint SPF_ASYNC = 1;
    private const uint SPF_PURGEBEFORESPEAK = 2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SpeakFn(IntPtr self, IntPtr pwcs, uint flags, IntPtr streamNumber);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRateFn(IntPtr self, int rate);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRateFn(IntPtr self, out int rate);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetVolumeFn(IntPtr self, out ushort volume);

    private IntPtr _voice;
    private SpeakFn? _speak;
    private ReleaseFn? _release;
    private bool _comInited;

    public string Name => "sapi";
    public string? LastError { get; private set; }
    public int Rate { get; private set; }

    public bool TryInit(out string? error)
    {
        error = null;
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            _comInited = hr >= 0;

            var clsid = CLSID_SpVoice;
            var iid = IID_ISpVoice;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var p);
            if (hr != 0 || p == IntPtr.Zero)
            {
                error = $"CoCreateInstance(SpVoice) 返回 0x{hr:X8}";
                return false;
            }

            var vtbl = Marshal.ReadIntPtr(p);
            _speak = Marshal.GetDelegateForFunctionPointer<SpeakFn>(Marshal.ReadIntPtr(vtbl, VT_SPEAK * IntPtr.Size));
            _release = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Marshal.ReadIntPtr(vtbl, VT_RELEASE * IntPtr.Size));
            var getRate = Marshal.GetDelegateForFunctionPointer<GetRateFn>(Marshal.ReadIntPtr(vtbl, VT_GETRATE * IntPtr.Size));
            var getVolume = Marshal.GetDelegateForFunctionPointer<GetVolumeFn>(Marshal.ReadIntPtr(vtbl, VT_GETVOLUME * IntPtr.Size));

            var hrRate = getRate(p, out var rate);
            var hrVol = getVolume(p, out var vol);
            if (hrRate != 0 || hrVol != 0)
            {
                error = $"SpVoice 属性读取失败（rate=0x{hrRate:X8} volume=0x{hrVol:X8}），vtable 槽位可能不对";
                return false;
            }

            Rate = rate;
            _voice = p;
            error = null;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public bool SetRate(int rate)
    {
        if (_voice == IntPtr.Zero) return false;
        try
        {
            var vtbl = Marshal.ReadIntPtr(_voice);
            var setRate = Marshal.GetDelegateForFunctionPointer<SetRateFn>(Marshal.ReadIntPtr(vtbl, VT_SETRATE * IntPtr.Size));
            var hr = setRate(_voice, Math.Clamp(rate, -10, 10));
            if (hr == 0) Rate = rate;
            return hr == 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool Speak(string text)
    {
        if (_voice == IntPtr.Zero || _speak is null || string.IsNullOrEmpty(text)) return false;
        IntPtr p = IntPtr.Zero;
        try
        {
            p = Marshal.StringToHGlobalUni(text);
            var hr = _speak(_voice, p, SPF_ASYNC, IntPtr.Zero);
            if (hr != 0)
            {
                LastError = $"ISpVoice::Speak 返回 0x{hr:X8}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>清空队列（pwcs=NULL + SPF_PURGEBEFORESPEAK）。R6 实测打断生效。</summary>
    public void Cancel()
    {
        if (_voice == IntPtr.Zero || _speak is null) return;
        try { _speak(_voice, IntPtr.Zero, SPF_ASYNC | SPF_PURGEBEFORESPEAK, IntPtr.Zero); }
        catch { }
    }

    public void Dispose()
    {
        try { Cancel(); } catch { }
        if (_voice != IntPtr.Zero && _release is not null)
        {
            try { _release(_voice); } catch { }
            _voice = IntPtr.Zero;
        }
        if (_comInited)
        {
            try { CoUninitialize(); } catch { }
            _comInited = false;
        }
    }
}
