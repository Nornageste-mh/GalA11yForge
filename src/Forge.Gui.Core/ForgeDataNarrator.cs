using System.Globalization;
using System.Text.Json;

namespace GalA11yForge.Gui.Core;

/// <summary>
/// 把 --json 信封里的 data 念成人话。读屏用户读 JSON 很痛苦，所以：
/// 1) 已知字段换成中文标签；2) 数组逐条成行；3) 有上限，避免一次刷几百行。
/// 只读 JsonElement，不抛异常；未知结构原样降级显示。
/// </summary>
public static class ForgeDataNarrator
{
    /// <summary>最多输出多少行 data 文本；超出时明确写「还有 N 项未显示」。</summary>
    public const int MaxLines = 120;

    private const int MaxArrayItemsInline = 12;
    private const int MaxCellLength = 300;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["game"] = "游戏目录",
        ["workspace"] = "工作区",
        ["slug"] = "作品标识",
        ["engine"] = "引擎",
        ["engineVersion"] = "引擎版本",
        ["engineFamily"] = "引擎家族",
        ["family"] = "引擎族",
        ["researchFamily"] = "研究族",
        ["version"] = "版本",
        ["bitness"] = "位数",
        ["confidence"] = "置信度",
        ["verdict"] = "判定",
        ["known"] = "已知引擎",
        ["suspected"] = "疑似",
        ["code"] = "错误码",
        ["message"] = "消息",
        ["suggestion"] = "建议",
        ["filesScanned"] = "扫描文件数",
        ["directoriesScanned"] = "扫描目录数",
        ["truncated"] = "结果已截断",
        ["elapsedMs"] = "耗时（毫秒）",
        ["evidence"] = "证据",
        ["candidates"] = "候选",
        ["coverage"] = "覆盖",
        ["risks"] = "风险",
        ["unverified"] = "未验证",
        ["checks"] = "检查项",
        ["toolVersion"] = "工具版本",
        ["route"] = "路线",
        ["patchPoints"] = "补丁点",
        ["artifacts"] = "产物",
        ["dryRun"] = "演练模式",
        ["advanced"] = "高级后端",
        ["ok"] = "结果正常",
        ["name"] = "名称",
        ["passed"] = "通过",
        ["detail"] = "详情",
        ["id"] = "编号",
        ["kind"] = "类型",
        ["score"] = "分数",
        ["source"] = "来源",
        ["ruleId"] = "规则",
        ["count"] = "数量",
        ["reportPath"] = "报告路径",
        ["report"] = "报告",
        ["markdownPath"] = "报告文件",
        ["jsonPath"] = "报告数据",
        ["humanTodosPath"] = "人工待办文件",
        ["humanTodoCount"] = "人工待办数",
        ["blockingCount"] = "阻断项数",
        ["blockingTodos"] = "阻断项",
        ["suggestedTodos"] = "建议待办",
        ["outputDirectory"] = "输出目录",
        ["rollback"] = "回滚",
        ["installed"] = "已安装",
        ["deployed"] = "已部署",
        ["backupRoot"] = "备份目录",
        ["pluginPath"] = "插件路径",
        ["manifestPath"] = "安装清单",
        ["gamePath"] = "游戏目录",
        ["reason"] = "原因",
        ["severity"] = "严重度",
        ["recommendation"] = "建议动作",
        ["manualTodo"] = "人工待办",
        ["notCovered"] = "未覆盖",
        ["covered"] = "已覆盖",
        ["total"] = "合计",
        ["speech"] = "语音证据",
        ["speechLines"] = "朗读行总数",
        ["speechValidLines"] = "有效朗读行",
        ["speechUniqueLines"] = "去重后朗读行",
        ["speechPlaceholderLines"] = "占位噪声行",
        ["speechLogBytes"] = "日志字节数",
        ["status"] = "状态",
        ["ghidra"] = "高级分析",
        ["executable"] = "分析程序",
        ["executableSizeBytes"] = "分析程序大小（字节）",
        ["estimatedSeconds"] = "预计分析耗时（秒）",
        ["suggestedTimeoutSeconds"] = "建议超时上限（秒）",
        ["elapsedSeconds"] = "实际分析耗时（秒）",
        ["optionWarnings"] = "选项警告",
    };

    public static string? LabelFor(string key) => Labels.TryGetValue(key, out var label) ? label : null;

    public static IReadOnlyList<string> Narrate(JsonElement data)
    {
        var lines = new List<string>();
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return lines;
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in data.EnumerateObject())
            {
                if (lines.Count >= MaxLines)
                {
                    break;
                }

                AppendProperty(lines, property.Name, property.Value, indent: string.Empty);
            }
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            AppendArray(lines, "数据", data, indent: string.Empty);
        }
        else
        {
            lines.Add(FormatScalar(data.ValueKind, data.GetRawText(), key: null));
        }

        if (lines.Count > MaxLines)
        {
            var extra = lines.Count - MaxLines;
            lines.RemoveRange(MaxLines, extra);
            lines.Add($"……还有 {extra} 行未显示（完整内容见 report 文件或原始 JSON）。");
        }

        return lines;
    }

    private static void AppendProperty(List<string> lines, string key, JsonElement value, string indent)
    {
        var label = LabelFor(key) ?? key;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                lines.Add($"{indent}{label}：");
                foreach (var child in value.EnumerateObject())
                {
                    if (lines.Count >= MaxLines)
                    {
                        return;
                    }

                    AppendProperty(lines, child.Name, child.Value, indent + "  ");
                }

                return;

            case JsonValueKind.Array:
                AppendArray(lines, label, value, indent);
                return;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                lines.Add($"{indent}{label}：未提供");
                return;

            default:
                lines.Add($"{indent}{label}：{FormatScalar(value.ValueKind, value.GetRawText(), key)}");
                return;
        }
    }

    private static void AppendArray(List<string> lines, string label, JsonElement array, string indent)
    {
        var items = array.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            lines.Add($"{indent}{label}：无");
            return;
        }

        var allObjects = items.All(item => item.ValueKind == JsonValueKind.Object);
        if (allObjects && LooksLikeChecks(items))
        {
            lines.Add($"{indent}{label}（{items.Count} 项）：");
            foreach (var item in items)
            {
                if (lines.Count >= MaxLines)
                {
                    return;
                }

                var passed = ReadBool(item, "passed");
                var mark = passed switch
                {
                    true => "通过",
                    false => "未通过",
                    null => "状态未知",
                };
                var name = ReadString(item, "name") ?? "(未命名检查)";
                var detail = ReadString(item, "detail");
                lines.Add(string.IsNullOrWhiteSpace(detail)
                    ? $"{indent}  {mark}　{name}"
                    : $"{indent}  {mark}　{name}：{Clip(detail)}");
            }

            return;
        }

        if (allObjects)
        {
            lines.Add($"{indent}{label}（{items.Count} 项）：");
            foreach (var item in items)
            {
                if (lines.Count >= MaxLines)
                {
                    return;
                }

                lines.Add($"{indent}  · {DescribeObject(item)}");
            }

            return;
        }

        var scalars = items
            .Take(MaxArrayItemsInline)
            .Select(item => FormatScalar(item.ValueKind, item.GetRawText(), key: null))
            .ToList();
        var suffix = items.Count > MaxArrayItemsInline ? $"（共 {items.Count} 项）" : string.Empty;
        lines.Add($"{indent}{label}{suffix}：{string.Join('、', scalars)}");
    }

    /// <summary>doctor 的 data.checks 是这个形状：name + passed + detail。</summary>
    private static bool LooksLikeChecks(IReadOnlyList<JsonElement> items)
        => items.All(item => ReadString(item, "name") is not null && ReadBool(item, "passed") is not null);

    private static string DescribeObject(JsonElement item)
    {
        var parts = new List<string>();
        foreach (var property in item.EnumerateObject())
        {
            if (parts.Count >= 6)
            {
                parts.Add("……");
                break;
            }

            var label = LabelFor(property.Name) ?? property.Name;
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.Object => "{…}",
                JsonValueKind.Array => $"[{property.Value.GetArrayLength()} 项]",
                _ => FormatScalar(property.Value.ValueKind, property.Value.GetRawText(), property.Name),
            };
            parts.Add($"{label}={Clip(value, 80)}");
        }

        return string.Join("，", parts);
    }

    private static string FormatScalar(JsonValueKind kind, string rawText, string? key)
    {
        switch (kind)
        {
            case JsonValueKind.True:
                return "是";
            case JsonValueKind.False:
                return "否";
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "未提供";
            case JsonValueKind.Number:
                if (key is not null
                    && key.Contains("confidence", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                    && confidence is >= 0 and <= 1)
                {
                    return $"{confidence * 100:F0}%";
                }

                return rawText;
            case JsonValueKind.String:
                var text = rawText.Trim('"');
                text = System.Text.Json.JsonSerializer.Deserialize<string>(rawText) ?? text;
                return Clip(text);
            default:
                return Clip(rawText);
        }
    }

    private static bool? ReadBool(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return null;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }

        return null;
    }

    private static string Clip(string value, int max = MaxCellLength)
    {
        var single = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }
}
