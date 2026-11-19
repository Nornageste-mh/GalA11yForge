using System.Globalization;
using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 生成工程的文本模板。所有模板都**只含代码/配置**，不含任何游戏资源或台词文本。
/// 占位符统一用 <c>__NAME__</c> 形式，避免与 C#/MSBuild 的花括号冲突。
/// </summary>
internal static class Il2CppTemplates
{

    internal static string ProjectFile(
        string assemblyName,
        string rootNamespace,
        string gameDirectoryHint,
        IReadOnlyList<string> gameAssemblyFiles,
        bool needsUnityUi,
        bool needsTextMeshPro,
        bool hasInterop)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net6.0</TargetFramework>");
        sb.AppendLine("    <AssemblyName>" + assemblyName + "</AssemblyName>");
        sb.AppendLine("    <RootNamespace>" + rootNamespace + "</RootNamespace>");
        sb.AppendLine("    <LangVersion>latest</LangVersion>");
        sb.AppendLine("    <Nullable>disable</Nullable>");
        sb.AppendLine("    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>");
        sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        sb.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
        sb.AppendLine("    <!-- 同时引用 net6.0 基础库与 Il2Cppmscorlib 会产生重复类型警告，与 R18 探针工程一致地静音。 -->");
        sb.AppendLine("    <NoWarn>CS0436;CS0437;CS1701;CS1702;CS0162</NoWarn>");
        sb.AppendLine("    <GameDir Condition=\"'$(GameDir)' == ''\">" + gameDirectoryHint + "</GameDir>");
        sb.AppendLine("    <CoreDir Condition=\"'$(CoreDir)' == ''\">$(GameDir)\\BepInEx\\core</CoreDir>");
        sb.AppendLine("    <InteropDir Condition=\"'$(InteropDir)' == ''\">$(GameDir)\\BepInEx\\interop</InteropDir>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <!-- BepInEx 6 (be.788) 与 HarmonyX：与 R18 探针工程同一套引用。 -->");
        sb.AppendLine("  <ItemGroup>");
        AppendCoreReference(sb, "BepInEx.Core");
        AppendCoreReference(sb, "BepInEx.Unity.IL2CPP");
        AppendCoreReference(sb, "BepInEx.Unity.Common");
        AppendCoreReference(sb, "0Harmony");
        AppendCoreReference(sb, "Il2CppInterop.Runtime");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        if (hasInterop)
        {
            sb.AppendLine("  <!-- 游戏 interop 程序集（BepInEx 6 首启生成）。换游戏版本后这里的方法名可能失效。 -->");
            sb.AppendLine("  <ItemGroup>");
            AppendInteropReference(sb, "Il2Cppmscorlib");
            foreach (var file in gameAssemblyFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AppendInteropReference(sb, Path.GetFileNameWithoutExtension(file), file);
            }
            if (needsUnityUi)
            {
                AppendInteropReference(sb, "UnityEngine.UI");
                AppendInteropReference(sb, "UnityEngine.UIModule");
            }

            if (needsTextMeshPro)
            {
                AppendInteropReference(sb, "Unity.TextMeshPro");
            }

            AppendInteropReference(sb, "UnityEngine.CoreModule");
            sb.AppendLine("  </ItemGroup>");
        }
        else
        {
            sb.AppendLine("  <!-- 当前没有 interop 程序集：只能编译骨架。先让 BepInEx 6 启动一次再重新生成。 -->");
        }

        sb.AppendLine();
        sb.AppendLine("  <Target Name=\"A11yProbe\" BeforeTargets=\"Build\" Condition=\"'$(GameDir)' != ''\">");
        sb.AppendLine("    <Message Importance=\"high\" Text=\"[a11y] GameDir=$(GameDir)；CoreDir=$(CoreDir)；InteropDir=$(InteropDir)\" />");
        sb.AppendLine("  </Target>");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    internal static string Plugin(string guid, string displayName, string rootNamespace)
    {
        return """
using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace __ROOT__;

/// <summary>
/// BepInEx 6 (IL2CPP) 入口。只做三件事：初始化朗读运行时、安装 Harmony 补丁、把解析结果写日志。
/// 任何异常都吞在 Load 内：补丁加载失败不能把游戏带崩。
/// </summary>
[BepInPlugin(Guid, "__DISPLAY__", "__VERSION__")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "__GUID__";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = base.Log;
        A11y.A11yRuntime.Initialize(Logger, typeof(Plugin).Assembly.Location);
        Logger.LogInfo("[a11y] IL2CPP 无障碍补丁加载中；游戏版本漂移会导致部分钩子解析失败，详见后续日志。");

        try
        {
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("[a11y] Harmony 补丁安装完成。");
        }
        catch (Exception ex)
        {
            Logger.LogError("[a11y] Harmony 补丁安装异常（游戏可继续运行，但不朗读）：" + ex);
        }
    }

    public override bool Unload()
    {
        A11y.A11yRuntime.Shutdown();
        return true;
    }
}
""".Replace("__ROOT__", rootNamespace)
            .Replace("__DISPLAY__", displayName)
            .Replace("__VERSION__", GalA11yForge.Core.ForgeInfo.Version)
            .Replace("__GUID__", guid);
    }

    internal static string Tolk(string rootNamespace)
    {
        return """
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace __ROOT__.A11y;

/// <summary>
/// Tolk 屏幕阅读器桥（NVDA / ZDSR / SAPI 自动选择）。
///
/// 声明来自 Framework/platform/Speech.cs（同一套 P/Invoke），运行期才加载 Tolk.dll：
/// 找不到 DLL 时整体降级为日志输出，绝不抛异常进游戏逻辑。
/// </summary>
internal static class TolkBridge
{
    private const string DllName = "Tolk";

    private static bool _available;
    private static bool _tried;

    public static bool Available => _available;

    public static void Initialize(string pluginDirectory)
    {
        if (_tried)
        {
            return;
        }

        _tried = true;
        try
        {
            var dll = Path.Combine(pluginDirectory, DllName + ".dll");
            if (File.Exists(dll))
            {
                // 先按绝对路径加载，之后 DllImport("Tolk") 会复用已加载模块。
                NativeLibrary.Load(dll);
            }

            Tolk_TrySAPI(true);
            Tolk_Load();
            _available = Tolk_IsLoaded();
            if (_available)
            {
                var reader = Marshal.PtrToStringUni(Tolk_DetectScreenReader()) ?? "(未检测到阅读器)";
                Plugin.Logger.LogInfo("[a11y] Tolk 已加载，阅读器=" + reader);
            }
            else
            {
                Plugin.Logger.LogWarning("[a11y] Tolk 加载失败：将以日志方式输出台词。");
            }
        }
        catch (Exception ex)
        {
            _available = false;
            Plugin.Logger.LogWarning("[a11y] Tolk 不可用（" + ex.GetType().Name + "）：将以日志方式输出台词。");
        }
    }

    public static bool Speak(string text, bool interrupt)
    {
        if (!_available || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return Tolk_Output(text, interrupt);
        }
        catch (Exception)
        {
            _available = false;
            return false;
        }
    }

    public static void Shutdown()
    {
        if (!_available)
        {
            return;
        }

        try
        {
            Tolk_Unload();
        }
        catch (Exception)
        {
            // 卸载失败不影响游戏退出。
        }

        _available = false;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_Load();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Tolk_IsLoaded();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_Unload();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool trySAPI);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr Tolk_DetectScreenReader();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern bool Tolk_Output(string str, [MarshalAs(UnmanagedType.I1)] bool interrupt);
}
""".Replace("__ROOT__", rootNamespace);
    }

    internal static string Runtime(string rootNamespace, bool enableSpeech)
    {
        var speechLiteral = enableSpeech ? "true" : "false";
        return """
using System;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace __ROOT__.A11y;

/// <summary>
/// 朗读运行时：所有钩子最终都调到这里。
///
/// 三个必须做的事（都来自 R18 真机实测）：
/// 1) 过滤空串：文本 setter 会大量以空串触发（set_Text 6 次里 5 次为空，set_PrintedText 14 次里 13 次为空）；
/// 2) 跨通道去重：同一行台词会从 6 个通道重复触发且内容 SHA1 相同，同一行只能读一次；
/// 3) 清洗富文本：拿到的是带 &lt;tag&gt; 的原始串。
/// 另外默认只用 hash/长度写日志，不把台词原文落到日志文件。
/// </summary>
internal static class A11yRuntime
{
    private const bool EnableSpeech = __SPEECH__;
    private const int DedupeWindowMs = 250;
    private const int RecentCapacity = 48;

    private static readonly ulong[] RecentHash = new ulong[RecentCapacity];
    private static readonly long[] RecentTime = new long[RecentCapacity];
    private static readonly object Gate = new();

    private static ManualLogSource _logger;
    private static string _speaker = string.Empty;
    private static int _recentCursor;
    private static long _emptyCalls;
    private static long _placeholderCalls;
    private static long _duplicateCalls;
    private static long _spoken;

    internal static long EmptyCalls => Interlocked.Read(ref _emptyCalls);
    internal static long PlaceholderCalls => Interlocked.Read(ref _placeholderCalls);
    internal static long DuplicateCalls => Interlocked.Read(ref _duplicateCalls);
    internal static long SpokenLines => Interlocked.Read(ref _spoken);

    public static void Initialize(ManualLogSource logger, string pluginAssemblyPath)
    {
        _logger = logger;
        var directory = System.IO.Path.GetDirectoryName(pluginAssemblyPath) ?? ".";
        TolkBridge.Initialize(directory);
        _logger.LogInfo("[a11y] 朗读运行时就绪；speech=" + EnableSpeech + "，tolk=" + TolkBridge.Available);
    }

    /// <summary>说话人名通道：只在该名字变化时朗读一次。</summary>
    public static void OnSpeaker(string channel, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var clean = Clean(name);
        if (clean.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            if (string.Equals(clean, _speaker, StringComparison.Ordinal))
            {
                return;
            }

            _speaker = clean;
        }

        Emit(channel, "说话人：" + clean, false);
    }

    /// <summary>台词通道。空白串与 250ms 内的重复内容会被丢弃。</summary>
    public static void OnDialogue(string channel, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Interlocked.Increment(ref _emptyCalls);
            return;
        }

        var clean = Clean(text);
        if (clean.Length == 0)
        {
            Interlocked.Increment(ref _emptyCalls);
            return;
        }

        // 真机 manosaba：SetTitleText 反复收到 "[8] Empty" 这类占位符，不能当台词念。
        if (IsPlaceholderText(clean))
        {
            Interlocked.Increment(ref _placeholderCalls);
            return;
        }

        if (IsDuplicate(Hash64(clean)))
        {
            Interlocked.Increment(ref _duplicateCalls);
            return;
        }

        Interlocked.Increment(ref _spoken);
        string speaker;
        lock (Gate)
        {
            speaker = _speaker;
        }

        var line = string.IsNullOrEmpty(speaker) ? clean : speaker + "：" + clean;
        Emit(channel, line, false);
    }

    /// <summary>选项通道（v1 未自动生成钩子，留给人工接入用）。</summary>
    public static void OnChoice(string channel, int index, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clean = Clean(text);
        if (clean.Length == 0)
        {
            return;
        }

        if (IsPlaceholderText(clean))
        {
            Interlocked.Increment(ref _placeholderCalls);
            return;
        }

        Emit(channel, "选项 " + index + "：" + clean, true);
    }

    public static void Shutdown()
    {
        _logger?.LogInfo("[a11y] 本次运行统计：朗读 " + SpokenLines + " 行，空串 " + EmptyCalls +
                        " 次，占位 " + PlaceholderCalls + " 次，去重 " + DuplicateCalls + " 次。");
        TolkBridge.Shutdown();
    }

    private static void Emit(string channel, string text, bool interrupt)
    {
        if (EnableSpeech && TolkBridge.Speak(text, interrupt))
        {
            if (_logger != null)
            {
                _logger.LogInfo("[a11y] " + channel + " len=" + text.Length + " sha=" + ShortHash(text));
            }

            return;
        }

        // Tolk 不可用时的兜底：把台词写进日志（本插件唯一的“出口”）。
        _logger?.LogInfo("[a11y-speech] " + channel + "：" + text);
    }

    /// <summary>去重窗口：同内容在 <see cref="DedupeWindowMs"/> 内只放行一次。</summary>
    private static bool IsDuplicate(ulong hash)
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            for (var i = 0; i < RecentCapacity; i++)
            {
                if (RecentHash[i] == hash && RecentHash[i] != 0 && now - RecentTime[i] <= DedupeWindowMs)
                {
                    RecentTime[i] = now;
                    return true;
                }
            }

            RecentHash[_recentCursor] = hash;
            RecentTime[_recentCursor] = now;
            _recentCursor = (_recentCursor + 1) % RecentCapacity;
            return false;
        }
    }

    /// <summary>去掉富文本标签、换行与重复空白，其余字符原样保留。</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inTag = false;
        foreach (var ch in text)
        {
            if (ch == '<')
            {
                inTag = true;
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                continue;
            }

            if (inTag)
            {
                continue;
            }

            builder.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
        }

        var trimmed = builder.ToString().Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder(trimmed.Length);
        var lastSpace = false;
        foreach (var ch in trimmed)
        {
            if (ch == ' ')
            {
                if (lastSpace)
                {
                    continue;
                }

                lastSpace = true;
            }
            else
            {
                lastSpace = false;
            }

            result.Append(ch);
        }

        return result.ToString();
    }

    /// <summary>占位文本判定：Empty/None/null/N/A/未设置/无内容，允许带 "[12] " 这类索引前缀。</summary>
    private static bool IsPlaceholderText(string text)
    {
        var value = text.Trim();
        if (value.Length > 0 && value[0] == '[')
        {
            var end = value.IndexOf(']');
            if (end > 0 && end + 1 < value.Length)
            {
                value = value.Substring(end + 1).Trim();
            }
        }

        return value.Equals("Empty", StringComparison.OrdinalIgnoreCase)
               || value.Equals("None", StringComparison.OrdinalIgnoreCase)
               || value.Equals("null", StringComparison.OrdinalIgnoreCase)
               || value.Equals("n/a", StringComparison.OrdinalIgnoreCase)
               || value.Equals("未设置", StringComparison.Ordinal)
               || value.Equals("无内容", StringComparison.Ordinal);
    }

    private static ulong Hash64(string text)
    {
        // FNV-1a 64：只为去重与日志指纹，不用于任何安全用途。
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static string ShortHash(string text)
        => Hash64(text).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)[..8];
}
""".Replace("__ROOT__", rootNamespace).Replace("__SPEECH__", speechLiteral);
    }

    /// <summary>单个补丁点。<paramref name="parameters"/> 是补丁方法要声明的形参（只含简单类型）。</summary>
    internal static string Hook(
        int index,
        string rootNamespace,
        Il2CppTarget target,
        IReadOnlyList<Il2CppParameterFact> parameters,
        string textParameterName,
        string channelKind)
    {
        var className = "Hook_" + index.ToString("000", CultureInfo.InvariantCulture);
        var call = channelKind switch
        {
            "speaker" => "A11y.A11yRuntime.OnSpeaker(Channel, " + textParameterName + ");",
            _ => "A11y.A11yRuntime.OnDialogue(Channel, " + textParameterName + ");",
        };

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using HarmonyLib;");
        sb.AppendLine();
        sb.AppendLine("namespace " + rootNamespace + ".Hooks;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// 补丁点 " + className + "：" + target.Display);
        sb.AppendLine("/// 声明类型必须精确：IL2CPP + HarmonyX 下把子类当目标会走 DeclaredMethod 并抛 Patching exception in method null。");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("[HarmonyPatch]");
        sb.AppendLine("internal static class " + className);
        sb.AppendLine("{");
        sb.AppendLine("    internal const string Channel = \"" + target.CSharpTypeName + "::" + target.MethodName + "\";");
        sb.AppendLine();
        sb.AppendLine("    private static MethodBase _target;");
        sb.AppendLine("    private static bool _logged;");
        sb.AppendLine();
        sb.AppendLine("    internal static MethodBase Resolve()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_target != null)");
        sb.AppendLine("        {");
        sb.AppendLine("            return _target;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            // 只按「名字 + 形参个数」解析：泛型/结构体形参（AsyncToken 等）无法写成 typeof(...)，");
        sb.AppendLine("            // 而 Harmony 允许补丁方法只声明原方法的参数子集，所以不需要它们的类型。");
        sb.AppendLine("            var type = typeof(global::" + target.CSharpTypeName + ");");
        sb.AppendLine("            foreach (var method in AccessTools.GetDeclaredMethods(type))");
        sb.AppendLine("            {");
        sb.AppendLine("                if (method.Name != \"" + target.MethodName + "\" || method.GetParameters().Length != " +
                     target.ParameterTypes.Count.ToString(CultureInfo.InvariantCulture) + ")");
        sb.AppendLine("                {");
        sb.AppendLine("                    continue;");
        sb.AppendLine("                }");
        sb.AppendLine();
        sb.AppendLine("                if (_target != null)");
        sb.AppendLine("                {");
        sb.AppendLine("                    LogMissing(\"同名同参数个数的方法有多个重载，放弃以避免打错\");");
        sb.AppendLine("                    _target = null;");
        sb.AppendLine("                    return null;");
        sb.AppendLine("                }");
        sb.AppendLine();
        sb.AppendLine("                _target = method;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (_target == null)");
        sb.AppendLine("            {");
        sb.AppendLine("                LogMissing(\"方法未找到（游戏版本可能已变）\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            LogMissing(ex.GetType().Name + \": \" + ex.Message);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return _target;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [HarmonyPrepare]");
        sb.AppendLine("    internal static bool Prepare() => Resolve() != null;");
        sb.AppendLine();
        sb.AppendLine("    [HarmonyTargetMethod]");
        sb.AppendLine("    internal static MethodBase TargetMethod() => Resolve();");
        sb.AppendLine();
        sb.AppendLine("    [HarmonyPostfix]");
        sb.Append("    internal static void Postfix(");
        sb.Append(string.Join(", ", parameters.Select(p => CSharpType(p.TypeName) + " " + p.Name)));
        sb.AppendLine(")");
        sb.AppendLine("    {");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            " + call);
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            Plugin.Logger.LogWarning(\"[a11y] \" + Channel + \" 回调异常：\" + ex.GetType().Name);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void LogMissing(string reason)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_logged)");
        sb.AppendLine("        {");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        _logged = true;");
        sb.AppendLine("        Plugin.Logger.LogWarning(\"[a11y] 补丁点未解析：\" + Channel + \"（\" + reason + \"）\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// 字段兜底补丁：没有 string 形参时，按实例文本字段朗读。
    /// 读取全部走 <see cref="A11yFieldReader"/> 的原生字段 API，不在生成代码里写死 offset。
    /// </summary>
    internal static string FieldHook(
        int index,
        string rootNamespace,
        Il2CppTarget target,
        IReadOnlyList<Il2CppTextField> fields)
    {
        var className = "Hook_" + index.ToString("000", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using HarmonyLib;");
        sb.AppendLine();
        sb.AppendLine("namespace " + rootNamespace + ".Hooks;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// 补丁点 " + className + "：" + target.Display);
        sb.AppendLine("/// 没有可用的 string 形参，按实例文本字段兜底朗读（字段读取见 A11y/A11yFieldReader.cs）。");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("[HarmonyPatch]");
        sb.AppendLine("internal static class " + className);
        sb.AppendLine("{");
        sb.AppendLine("    internal const string Channel = \"" + target.CSharpTypeName + "::" + target.MethodName + "\";");
        sb.AppendLine();
        sb.AppendLine("    private static System.Reflection.MethodBase _target;");
        sb.AppendLine("    private static bool _logged;");
        sb.AppendLine();
        sb.AppendLine("    internal static System.Reflection.MethodBase Resolve()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_target != null)");
        sb.AppendLine("        {");
        sb.AppendLine("            return _target;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            var type = typeof(global::" + target.CSharpTypeName + ");");
        sb.AppendLine("            foreach (var method in AccessTools.GetDeclaredMethods(type))");
        sb.AppendLine("            {");
        sb.AppendLine("                if (method.Name != \"" + target.MethodName + "\" || method.GetParameters().Length != " +
                     target.ParameterTypes.Count.ToString(CultureInfo.InvariantCulture) + ")");
        sb.AppendLine("                {");
        sb.AppendLine("                    continue;");
        sb.AppendLine("                }");
        sb.AppendLine();
        sb.AppendLine("                if (_target != null)");
        sb.AppendLine("                {");
        sb.AppendLine("                    LogMissing(\"同名同参数个数的方法有多个重载，放弃以避免打错\");");
        sb.AppendLine("                    _target = null;");
        sb.AppendLine("                    return null;");
        sb.AppendLine("                }");
        sb.AppendLine();
        sb.AppendLine("                _target = method;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (_target == null)");
        sb.AppendLine("            {");
        sb.AppendLine("                LogMissing(\"方法未找到（游戏版本可能已变）\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            LogMissing(ex.GetType().Name + \": \" + ex.Message);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return _target;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [HarmonyPrepare]");
        sb.AppendLine("    internal static bool Prepare() => Resolve() != null;");
        sb.AppendLine();
        sb.AppendLine("    [HarmonyTargetMethod]");
        sb.AppendLine("    internal static System.Reflection.MethodBase TargetMethod() => Resolve();");
        sb.AppendLine();
        sb.AppendLine("    [HarmonyPostfix]");
        sb.AppendLine("    internal static void Postfix(global::" + target.CSharpTypeName + " __instance)");
        sb.AppendLine("    {");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            A11y.A11yFieldReader.ReadAndSpeak(Channel, __instance, new A11y.FieldSpec[]");
        sb.AppendLine("            {");
        foreach (var field in fields)
        {
            if (field.Kind == Il2CppFieldKind.Unsupported)
            {
                continue;
            }

            var component = field.Kind == Il2CppFieldKind.Component
                ? "typeof(global::" + field.CSharpType + ")"
                : "null";
            sb.AppendLine("                new A11y.FieldSpec(\"" + field.Name + "\", " + component + "),");
        }

        sb.AppendLine("            });");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            Plugin.Logger.LogWarning(\"[a11y] \" + Channel + \" 字段兜底回调异常：\" + ex.GetType().Name);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void LogMissing(string reason)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_logged)");
        sb.AppendLine("        {");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        _logged = true;");
        sb.AppendLine("        Plugin.Logger.LogWarning(\"[a11y] 补丁点未解析：\" + Channel + \"（\" + reason + \"）\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// IL2CPP 原生字段读取器。运行时用 <c>il2cpp_object_get_class</c> + 继承链查找 +
    /// <c>il2cpp_field_get_value_object</c> 读取，不依赖 dump.cs 里的 offset（offset 在部分类型上是 -1）。
    /// </summary>
    internal static string FieldReader(string rootNamespace)
    {
        return """
using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace __ROOT__.A11y;

/// <summary>一个字段候选：Name 是元数据原名，Component 不为 null 时按组件 text 属性读取。</summary>
public readonly struct FieldSpec
{
    public readonly string Name;

    public readonly Type Component;

    public FieldSpec(string name, Type component = null)
    {
        Name = name;
        Component = component;
    }
}

/// <summary>
/// 没有 string 形参时的兜底：从实例的原生字段读文本。
/// 只读、绝不修改游戏对象；任何异常都吞掉并返回 null，不把游戏带崩。
/// </summary>
public static class A11yFieldReader
{
    private static readonly Dictionary<string, IntPtr> _fieldCache = new Dictionary<string, IntPtr>();

    /// <summary>依次尝试候选字段，读到第一条非空文本就朗读并返回。</summary>
    public static void ReadAndSpeak(string channel, Il2CppObjectBase instance, FieldSpec[] candidates)
    {
        if (instance == null || candidates == null)
        {
            return;
        }

        try
        {
            IntPtr instancePtr = instance.Pointer;
            if (instancePtr == IntPtr.Zero)
            {
                return;
            }

            IntPtr klass = IL2CPP.il2cpp_object_get_class(instancePtr);
            foreach (var spec in candidates)
            {
                if (string.IsNullOrEmpty(spec.Name))
                {
                    continue;
                }

                string text = TryRead(klass, instancePtr, spec);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                Plugin.Logger.LogInfo("[Speech:fields] hook=" + channel + " field=" + spec.Name + " len=" + text.Length);
                A11yRuntime.OnDialogue(channel, text);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("[a11y] 字段兜底读取异常：" + ex.GetType().Name);
        }
    }

    private static string TryRead(IntPtr klass, IntPtr instance, FieldSpec spec)
    {
        try
        {
            string key = klass.ToString("X8") + "|" + spec.Name;
            if (!_fieldCache.TryGetValue(key, out IntPtr field))
            {
                field = FindField(klass, spec.Name);
                _fieldCache[key] = field;
            }

            if (field == IntPtr.Zero)
            {
                return null;
            }

            IntPtr obj = IL2CPP.il2cpp_field_get_value_object(field, instance);
            if (obj == IntPtr.Zero)
            {
                return null;
            }

            if (spec.Component == null)
            {
                return IL2CPP.Il2CppStringToManaged(obj);
            }

            object component = Activator.CreateInstance(spec.Component, obj);
            PropertyInfo textProperty = spec.Component.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            return textProperty == null ? null : textProperty.GetValue(component) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IntPtr FindField(IntPtr klass, string name)
    {
        for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(k, name);
            if (field != IntPtr.Zero)
            {
                return field;
            }

            // 属性名（例如 text）在 IL2CPP 里对应 <text>k__BackingField；字段名本身查不到时补一次。
            field = IL2CPP.il2cpp_class_get_field_from_name(k, "<" + name + ">k__BackingField");
            if (field != IntPtr.Zero)
            {
                return field;
            }
        }

        return IntPtr.Zero;
    }
}
""".Replace("__ROOT__", rootNamespace);
    }

    internal static string Readme(
        string slug,
        string guid,
        string gameDirectory,
        string assemblyHash,
        string sourceKind,
        IReadOnlyList<PatchPoint> points,
        IReadOnlyList<string> unverified)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + slug + " IL2CPP 无障碍插件");
        sb.AppendLine();
        sb.AppendLine("> 由 Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） 自动生成。**本工程不含任何游戏资源或台词文本**，只含代码与配置。");
        sb.AppendLine();
        sb.AppendLine("## 这是什么");
        sb.AppendLine();
        sb.AppendLine("BepInEx 6（IL2CPP）插件：挂钩游戏的文本通道，把台词交给 Tolk 交给屏幕阅读器。");
        sb.AppendLine("插件 GUID：`" + guid + "`");
        sb.AppendLine();
        sb.AppendLine("## 构建");
        sb.AppendLine();
        sb.AppendLine("```powershell");
        sb.AppendLine("./build.ps1 -GameDir \"D:\\Steam\\steamapps\\common\\<游戏目录>\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("`GameDir` 必须是**已经装好 BepInEx 6 且成功启动过至少一次**的游戏目录，");
        sb.AppendLine("否则 `BepInEx/interop` 不存在，编译会缺游戏类型引用。");
        sb.AppendLine();
        sb.AppendLine("## 安装");
        sb.AppendLine();
        sb.AppendLine("1. 把 `bin/Release/" + slug + ".il2cpp.a11y.dll` 复制到 `BepInEx/plugins/`；");
        sb.AppendLine("2. 把 `Tolk.dll`（64 位）放到同一个目录；");
        sb.AppendLine("3. 启动游戏，看 `BepInEx/LogOutput.log` 里有多少条 `[a11y]` 钩子安装成功。");
        sb.AppendLine();
        sb.AppendLine("## 已知限制（必须知道）");
        sb.AppendLine();
        sb.AppendLine("- 本工程基于静态元数据生成：**运行期是否真的收到台词未验证**，必须真机跑一遍；");
        sb.AppendLine("- 缺 Tolk.dll 时插件不朗读，只把台词写进日志（`[a11y-speech]` 前缀）；");
        sb.AppendLine("- 文本 setter 大量以空串触发、同一行会从多个通道重复触发，运行时已做过滤与 250ms 去重；");
        sb.AppendLine("- 选项朗读、多行连续对话的节奏未验证。");
        sb.AppendLine();
        sb.AppendLine("## 生成时的原料");
        sb.AppendLine();
        sb.AppendLine("- 游戏目录：" + gameDirectory);
        sb.AppendLine("- 静态原料来源：" + sourceKind);
        sb.AppendLine("- interop 缓存键（assembly-hash.txt）：" + (string.IsNullOrEmpty(assemblyHash) ? "未记录" : assemblyHash));
        sb.AppendLine();
        sb.AppendLine("## 补丁点");
        sb.AppendLine();
        if (points.Count == 0)
        {
            sb.AppendLine("（无自动补丁点，见生成报告的 ManualTodo）");
        }
        else
        {
            foreach (var point in points)
            {
                sb.AppendLine("- `" + point.Target + "` — " + point.Description);
            }
        }

        if (unverified.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 未验证项");
            sb.AppendLine();
            foreach (var item in unverified.Take(12))
            {
                sb.AppendLine("- " + item);
            }
        }

        return sb.ToString();
    }

    internal static string BuildScript()
    {
        return """
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$project = Get-ChildItem -Path $PSScriptRoot -Filter *.csproj | Select-Object -First 1
if (-not $project) {
    throw "找不到 csproj"
}

Write-Host "[a11y] 构建 $($project.Name)，GameDir=$GameDir"
dotnet build $project.FullName -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（exit $LASTEXITCODE）"
}

Write-Host "[a11y] 完成：$PSScriptRoot\bin\$Configuration"
""";
    }

    internal static string CSharpType(string typeName) => typeName switch
    {
        "System.String" => "string",
        "System.Boolean" => "bool",
        "System.Char" => "char",
        "System.Object" => "object",
        "System.Int32" => "int",
        "System.Int64" => "long",
        "System.UInt32" => "uint",
        "System.UInt64" => "ulong",
        "System.Int16" => "short",
        "System.UInt16" => "ushort",
        "System.Byte" => "byte",
        "System.SByte" => "sbyte",
        "System.Single" => "float",
        "System.Double" => "double",
        "System.Decimal" => "decimal",
        _ => typeName.Replace('/', '.'),
    };

    private static void AppendCoreReference(StringBuilder sb, string name)
    {
        sb.AppendLine("    <Reference Include=\"" + name + "\">");
        sb.AppendLine("      <HintPath>$(CoreDir)\\" + name + ".dll</HintPath>");
        sb.AppendLine("      <Private>false</Private>");
        sb.AppendLine("    </Reference>");
    }

    private static void AppendInteropReference(StringBuilder sb, string name, string? fileName = null)
    {
        var file = fileName ?? name + ".dll";
        sb.AppendLine("    <Reference Include=\"" + name + "\" Condition=\"Exists('$(InteropDir)\\" + file + "')\">");
        sb.AppendLine("      <HintPath>$(InteropDir)\\" + file + "</HintPath>");
        sb.AppendLine("      <Private>false</Private>");
        sb.AppendLine("    </Reference>");
    }
}
