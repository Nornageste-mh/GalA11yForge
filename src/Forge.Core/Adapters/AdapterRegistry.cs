using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Adapters;

/// <summary>
/// 适配器注册表（adapters/adapter.json）。支持两种顶层写法：
/// { "schemaVersion": 1, "adapters": [...] } 或直接 [...]。
/// 注册项按 priority 升序、name 升序排序；name 必须唯一。
/// </summary>
public sealed class AdapterRegistry
{
    private AdapterRegistry(IEnumerable<AdapterManifest> adapters)
    {
        Adapters = adapters
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<AdapterManifest> Adapters { get; }

    public static string DefaultRelativePath => Path.Combine("adapters", "adapter.json");

    public static ForgeResult<AdapterRegistry> LoadFromWorkspace(ForgeWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var candidates = new[]
        {
            Path.Combine(workspace.RootPath, "adapters", "adapter.json"),
            Path.Combine(workspace.RootPath, "payload", "adapters", "adapter.json"),
        };

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null)
        {
            return ForgeResult<AdapterRegistry>.Failure(ForgeError.Create(
                ForgeErrorCodes.AdapterNotFound,
                $"未找到适配器注册表：{candidates[0]}。",
                "先放置 adapters/adapter.json，或运行 doctor 检查安装是否完整。"));
        }

        return Load(existing);
    }

    public static ForgeResult<AdapterRegistry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ForgeResult<AdapterRegistry>.Failure(ForgeError.Create(
                    ForgeErrorCodes.AdapterNotFound,
                    $"适配器注册表不存在：{path}。",
                    "检查路径是否正确；预期文件名是 adapter.json。"));
            }

            return FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<AdapterRegistry>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取适配器注册表失败：{path}：{ex.Message}",
                "检查文件权限后重试。"));
        }
    }

    public static ForgeResult<AdapterRegistry> FromJson(string json)
    {
        AdapterRegistryDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind == JsonValueKind.Array)
            {
                var arrayAdapters = JsonSerializer.Deserialize<List<AdapterManifest>>(json, ForgeJson.Options)
                    ?? new List<AdapterManifest>();
                document = new AdapterRegistryDocument { Adapters = arrayAdapters };
            }
            else
            {
                document = JsonSerializer.Deserialize<AdapterRegistryDocument>(json, ForgeJson.Options)
                    ?? new AdapterRegistryDocument();
            }
        }
        catch (JsonException ex)
        {
            return ForgeResult<AdapterRegistry>.Failure(ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器注册表不是合法 JSON：{ex.Message}",
                "对照 docs/06-接口契约-v1.md 的示例修正 adapter.json。"));
        }

        if (document.SchemaVersion != 1)
        {
            return ForgeResult<AdapterRegistry>.Failure(ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"不支持的 adapter schemaVersion：{document.SchemaVersion}。",
                "当前只支持 schemaVersion=1。"));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adapters = document.Adapters ?? new List<AdapterManifest>();
        foreach (var adapter in adapters)
        {
            var error = Validate(adapter, names);
            if (error is not null)
            {
                return ForgeResult<AdapterRegistry>.Failure(error);
            }
        }

        return ForgeResult<AdapterRegistry>.Success(new AdapterRegistry(adapters));
    }

    public IEnumerable<AdapterManifest> FindByEngine(string engine)
        => Adapters.Where(a => string.Equals(a.Engine, engine, StringComparison.OrdinalIgnoreCase));

    public AdapterManifest? FindPreferred(string engine)
        => FindByEngine(engine).FirstOrDefault();

    public IEnumerable<AdapterManifest> FindByCapability(string capability)
        => Adapters.Where(a => a.Capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)));

    private static ForgeError? Validate(AdapterManifest adapter, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(adapter.Name))
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                "适配器缺少 name。",
                "name 使用 slug 风格，例如 unity-mono。");
        }

        if (!names.Add(adapter.Name))
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器 name 重复：{adapter.Name}。",
                "每个适配器的 name 必须唯一（不区分大小写）。");
        }

        if (string.IsNullOrWhiteSpace(adapter.Engine))
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器 {adapter.Name} 缺少 engine。",
                "engine 使用引擎 id，例如 unity_mono。");
        }

        if (adapter.Priority < 0)
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器 {adapter.Name} 的 priority 不能为负数。",
                "默认 100；数字越小越优先。");
        }

        if (string.IsNullOrWhiteSpace(adapter.Assembly) || string.IsNullOrWhiteSpace(adapter.Type))
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器 {adapter.Name} 缺少 assembly 或 type。",
                "填写程序集文件名与实现类型全名。");
        }

        try
        {
            PathSafety.ValidateRelative(adapter.Assembly);
        }
        catch (ForgePathException ex)
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器 {adapter.Name} 的 assembly 路径不安全：{ex.Error.Message}",
                "assembly 只能是适配器目录下的相对文件名。");
        }

        if (adapter.Capabilities.Count == 0)
        {
            return ForgeError.Create(
                ForgeErrorCodes.AdapterInvalid,
                $"适配器 {adapter.Name} 未声明 capabilities。",
                "至少声明 detect；可加 analyze、plan、generate、build、deploy、verify。");
        }

        return null;
    }
}
