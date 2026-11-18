using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 分析编排：探测布局 → 选静态输入（interop 优先，dump.cs 兜底）→ 扫描 → 打分。
///
/// <para>
/// 两种输入的能力不一样，必须分开声明：interop 是 BepInEx 6 首启生成的托管桩程序集，
/// 有类型/方法/参数名/属性/事件 adder，但**没有方法体语义**；dump.cs 有 RVA/槽位/签名，
/// 但同样没有字符串常量与调用关系。所以两边都只能做“结构信号 + 命名信号”打分。
/// </para>
/// </summary>
internal sealed class Il2CppAnalysisService
{
    private readonly Il2CppInteropScanner _interopScanner = new();
    private readonly Il2CppDumpParser _dumpParser = new();
    private readonly Il2CppAnalyzer _analyzer = new();

    public AnalysisReport Analyze(GameContext context, EngineDetection detection, CancellationToken cancellationToken)
    {
        var layout = Probe(context);
        return AnalyzeWithLayout(context, detection, layout, cancellationToken);
    }

    /// <summary>
    /// 用给定布局继续扫描+打分。自动 dump 成功后走这里，避免重新探测时丢掉刚生成的 dump.cs 路径。
    /// </summary>
    public AnalysisReport AnalyzeWithLayout(
        GameContext context,
        EngineDetection detection,
        Il2CppLayout layout,
        CancellationToken cancellationToken)
    {
        var options = Il2CppOptions.From(context);
        var scan = Scan(layout, cancellationToken);
        var evidence = Il2CppRuntimeEvidence.TryLoad(options.RuntimeEvidencePath);
        return _analyzer.Analyze(context, detection, options, scan, evidence, cancellationToken);
    }

    /// <summary>供检测阶段复用：只探测，不扫描（扫描几十个程序集要数秒）。</summary>
    public Il2CppLayout Probe(GameContext context)
        => Il2CppLayoutProbe.Probe(context.GameDirectory, Il2CppOptions.From(context).AsDictionary());

    /// <summary>自动 dump 成功后，把产物路径并入选项重新探测，得到 HasDump=true 的布局。</summary>
    public Il2CppLayout ProbeWithDump(GameContext context, string dumpPath)
    {
        var options = Il2CppOptions.From(context);
        var merged = new Dictionary<string, string>(options.AsDictionary(), StringComparer.OrdinalIgnoreCase)
        {
            ["dumpPath"] = dumpPath,
        };
        return Il2CppLayoutProbe.Probe(context.GameDirectory, merged);
    }

    public Il2CppScanOutcome Scan(Il2CppLayout layout, CancellationToken cancellationToken)
    {
        if (layout.HasInterop && layout.InteropAssemblyCount > 0)
        {
            try
            {
                return _interopScanner.Scan(layout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // interop 少了/坏了不该让整条流水线死掉：退回 dump.cs，并把原因写进警告。
                if (layout.HasDump)
                {
                    var fallback = _dumpParser.Parse(layout, cancellationToken);
                    fallback.Warnings.Insert(0, "interop 扫描失败（" + ex.GetType().Name + "：" + ex.Message + "），已退回 dump.cs。");
                    return fallback;
                }

                var broken = new Il2CppScanOutcome { Source = Il2CppSourceKind.None, Layout = layout };
                broken.Warnings.Add("interop 扫描失败（" + ex.GetType().Name + "：" + ex.Message + "），且没有可用的 dump.cs。");
                return broken;
            }
        }

        if (layout.HasDump)
        {
            try
            {
                return _dumpParser.Parse(layout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var broken = new Il2CppScanOutcome { Source = Il2CppSourceKind.None, Layout = layout };
                broken.Warnings.Add("dump.cs 读取失败（" + ex.GetType().Name + "：" + ex.Message + "）。");
                return broken;
            }
        }

        return new Il2CppScanOutcome { Source = Il2CppSourceKind.None, Layout = layout };
    }

    /// <summary>把“没有任何静态输入”这件事变成带下一步建议的失败，而不是空报告假装成功。</summary>
    public static ForgeError NoStaticInput(GameContext context)
    {
        var layout = Il2CppLayoutProbe.Probe(context.GameDirectory, Il2CppOptions.From(context).AsDictionary());
        var detail = layout.Notes.Count == 0 ? string.Empty : " 布局探测：" + string.Join("；", layout.Notes);
        return ForgeError.Create(
            ForgeErrorCodes.DependencyMissing,
            "没有可用的静态输入：既读不到 BepInEx 6 的 interop 程序集，也没有 dump.cs。" + detail,
            "两种办法任选其一：① 在游戏目录安装 BepInEx 6.0.0-be.788 并启动一次（首启会自动生成 BepInEx/interop，实测约 229 秒）；" +
            "② 用 Il2CppDumper 生成 dump.cs，然后用 --option il2cpp.dumpPath=<dump.cs 路径> 指定。");
    }
}
