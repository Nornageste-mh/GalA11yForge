using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>步骤一：识别 Ren'Py。只读文件系统，不启动游戏。</summary>
internal sealed class RenpyDetector
{
    public Task<ForgeResult<EngineDetection>> DetectAsync(GameContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "游戏目录为空，无法识别 Ren'Py。",
                "用 --game 指定游戏根目录（包含 renpy/ 与 game/ 的那一层）。"));
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不是合法路径：{context.GameDirectory}",
                "检查路径中的非法字符或过长的目录名。"));
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{root}",
                "确认游戏已安装，--game 指向包含 renpy/ 与 game/ 的根目录。"));
        }

        var facts = RenpyInspector.Inspect(root);
        if (!facts.IsRenpy)
        {
            var missing = new List<string>();
            if (!facts.HasRenpyDirectory)
            {
                missing.Add("renpy/");
            }

            if (!facts.HasGameDirectory)
            {
                missing.Add("game/");
            }

            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                $"该目录不像 Ren'Py 游戏：缺少 {string.Join("、", missing)}。",
                "确认 --game 指向游戏根目录，而不是 game/ 子目录；非 Ren'Py 游戏请换用对应适配器。"));
        }

        var evidence = BuildEvidence(facts);
        var confidence = ComputeConfidence(facts);
        var detection = new EngineDetection
        {
            SchemaVersion = 1,
            Family = RenpyAdapter.EngineId,
            Version = facts.EngineVersion,
            Bitness = facts.Bitness,
            Confidence = confidence,
            Evidence = evidence,
        };

        return Task.FromResult(ForgeResult<EngineDetection>.Success(detection));
    }

    private static List<string> BuildEvidence(RenpyGameFacts facts)
    {
        var evidence = new List<string>
        {
            "file:renpy/__init__.py（Ren'Py 入口存在）",
            "file:game/（游戏资源目录存在）",
        };

        if (facts.EngineVersion is not null && facts.VersionSource is not null)
        {
            evidence.Add($"file:{facts.VersionSource}（version={facts.EngineVersion}）");
        }
        else
        {
            evidence.Add("unverified:未能从 renpy/vc_version.py 或 renpy/__init__.py 解析版本号");
        }

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(facts.VersionName))
        {
            meta.Add($"version_name={RenpySupport.OneLine(facts.VersionName)}");
        }

        if (!string.IsNullOrWhiteSpace(facts.Branch))
        {
            meta.Add($"branch={facts.Branch}");
        }

        if (facts.Nightly is not null)
        {
            meta.Add($"nightly={facts.Nightly.Value.ToString().ToLowerInvariant()}");
        }

        if (facts.Official is not null)
        {
            meta.Add($"official={facts.Official.Value.ToString().ToLowerInvariant()}");
        }

        if (meta.Count > 0)
        {
            evidence.Add("file:renpy/vc_version.py（" + string.Join("，", meta) + "）");
        }

        if (facts.PythonLibs.Count > 0)
        {
            var bitness = facts.Bitness == 0 ? "位数未知" : facts.Bitness + " 位";
            var major = facts.PythonMajor == 0 ? "Python 主版本未知" : "Python " + facts.PythonMajor;
            evidence.Add($"measured:{RenpySupport.ShortList(facts.PythonLibs)} → {major} / {bitness}");
        }
        else
        {
            evidence.Add("unverified:lib/ 下没有可识别的 Python 运行库目录");
        }

        if (facts.RpaFiles.Count > 0)
        {
            evidence.Add(
                $"measured:game/*.rpa = {facts.RpaFiles.Count} 个，总计 {RenpySupport.FormatBytes(facts.RpaBytes)}（{RenpySupport.FormatMiB(facts.RpaBytes)}）");
            foreach (var rpa in facts.RpaFiles.Take(6))
            {
                var header = string.IsNullOrWhiteSpace(rpa.Header) ? "头部未读到" : "头 " + rpa.Header;
                evidence.Add($"file:game/{rpa.Name}（{RenpySupport.FormatBytes(rpa.Bytes)}，{header}）");
            }

            if (facts.RpaFiles.Count > 6)
            {
                evidence.Add($"measured:其余 {facts.RpaFiles.Count - 6} 个 .rpa 未逐条列出");
            }
        }
        else
        {
            evidence.Add("measured:game/*.rpa = 0 个（纯松散脚本或已解包）");
        }

        evidence.Add($"measured:game 下松散 .rpy = {facts.LooseRpy.Count} 个；.rpyc = {facts.LooseRpycCount} 个");
        foreach (var overlay in facts.OverlayDirectories)
        {
            evidence.Add($"file:game/{overlay}/（疑似已装 a11y 补丁目录，本次不覆盖）");
        }

        if (facts.HasRootSayVbs)
        {
            evidence.Add("file:a11y_say.vbs（游戏根已有同名补丁文件）");
        }

        if (facts.HasRootManifest)
        {
            evidence.Add("file:a11y_manifest.json（游戏根已有补丁清单）");
        }

        if (facts.AccessibilityScript is not null)
        {
            evidence.Add(
                $"file:{facts.AccessibilityScript}（{RenpySupport.FormatBytes(facts.AccessibilityBytes)}，引擎自带无障碍脚本）");
        }

        foreach (var nvda in facts.NvdaClients)
        {
            var bitness = nvda.Bitness == 0 ? "位数未知" : nvda.Bitness + " 位";
            evidence.Add($"file:{nvda.RelativePath}（{RenpySupport.FormatBytes(nvda.Bytes)}，{bitness}）");
        }

        if (facts.ExecutableName is not null)
        {
            evidence.Add($"file:{(string.IsNullOrWhiteSpace(facts.ExecutableName) ? string.Empty : facts.ExecutableName)}（游戏可执行文件）");
        }

        if (facts.IsSteam)
        {
            evidence.Add("measured:路径位于 steamapps 或存在 steam_appid.txt（按 Steam 只读目录处理）");
        }

        if (facts.Hooks.AstSayFilterLines.Count > 0)
        {
            evidence.Add(
                $"file:renpy/ast.py:{string.Join("/", facts.Hooks.AstSayFilterLines)}（say_menu_text_filter 调用点）");
        }

        if (facts.Hooks.ConfigSayFilterLine is not null)
        {
            evidence.Add($"file:renpy/config.py:{facts.Hooks.ConfigSayFilterLine}（say_menu_text_filter 定义）");
        }

        evidence.Add("unverified:未启动游戏，未解析 .rpa 内容，所有行为均未实测");
        return evidence;
    }

    private static double ComputeConfidence(RenpyGameFacts facts)
    {
        var score = 0d;
        if (facts.HasRenpyInit)
        {
            score += 0.35;
        }

        if (facts.HasGameDirectory)
        {
            score += 0.20;
        }

        if (facts.EngineVersion is not null)
        {
            score += 0.15;
        }

        if (facts.PythonMajor > 0)
        {
            score += 0.10;
        }

        if (facts.RpaFiles.Count > 0)
        {
            score += 0.10;
        }

        if (!string.IsNullOrWhiteSpace(facts.ExecutableName))
        {
            score += 0.05;
        }

        if (facts.HasRenpyDirectory)
        {
            score += 0.05;
        }

        if (facts.EngineVersion is null)
        {
            score = Math.Min(score, 0.75);
        }

        return Math.Round(Math.Clamp(score, 0d, 1d), 2);
    }
}
