using System.Diagnostics;
using Microsoft.Win32;
using GalA11yForge.Verify.Abstractions;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify;

/// <summary>
/// Windows 只读环境探测：进程、DLL 与注册表。
/// 不加载 Tolk、不调用 NVDA/争渡/SAPI、不播报任何声音；Available 只代表组件存在。
/// </summary>
public sealed class WindowsEnvironmentProbe : IEnvironmentProbe
{
    public Task<EnvironmentSnapshot> ProbeAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Probe(gameDirectory, cancellationToken));
    }

    private static EnvironmentSnapshot Probe(string gameDirectory, CancellationToken cancellationToken)
    {
        var roots = CandidateRoots(gameDirectory);
        var snapshot = new EnvironmentSnapshot
        {
            CapturedAt = DateTimeOffset.Now.ToString("O"),
        };

        snapshot.Backends.Add(SafeProbe("tolk", "Tolk", () => ProbeTolk(roots)));
        snapshot.Backends.Add(SafeProbe("nvda", "NVDA", () => ProbeNvda(roots)));
        snapshot.Backends.Add(SafeProbe("zdsr", "争渡", () => ProbeZdsr(roots)));
        snapshot.Backends.Add(SafeProbe("sapi", "SAPI5", ProbeSapi));

        snapshot.Metrics["voiceBackends"] = snapshot.Backends.Count;
        snapshot.Metrics["availableVoiceBackends"] = snapshot.AvailableCount;
        snapshot.Notes.Add("本探测只读：检查进程、DLL 与注册表，不加载 Tolk、不调用任何朗读 API。");
        snapshot.Notes.Add("可用只代表组件存在，不代表游戏内实际出声；出声需要人工实测。");
        snapshot.Notes.Add("Tolk 是否被补丁实际加载、争渡 Speak 状态机是否正常，不在本探测范围内。");

        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    private static VoiceBackendFact SafeProbe(string id, string displayName, Func<VoiceBackendFact> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            return new VoiceBackendFact
            {
                Id = id,
                DisplayName = displayName,
                Available = null,
                Fact = $"只读探测失败：{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    private static VoiceBackendFact ProbeTolk(IReadOnlyList<string> roots)
    {
        var dll = FindFile(roots, "Tolk.dll", "tolk.dll");
        if (dll is null)
        {
            return new VoiceBackendFact
            {
                Id = "tolk",
                DisplayName = "Tolk",
                Available = false,
                Fact = "未找到 Tolk.dll；Tolk 后端不可用。",
            };
        }

        return new VoiceBackendFact
        {
            Id = "tolk",
            DisplayName = "Tolk",
            Available = true,
            Fact = $"找到 Tolk.dll：{dll}",
            Evidence = { dll },
        };
    }

    private static VoiceBackendFact ProbeNvda(IReadOnlyList<string> roots)
    {
        var processRunning = IsProcessRunning("nvda");
        var dll = FindFile(
            roots.Concat(new[]
            {
                @"C:\Program Files\NVDA",
                @"C:\Program Files (x86)\NVDA",
            }),
            "nvdaControllerClient64.dll",
            "nvdaControllerClient.dll");

        var fact = new VoiceBackendFact
        {
            Id = "nvda",
            DisplayName = "NVDA",
            Available = dll is not null && processRunning,
        };

        if (dll is not null)
        {
            fact.Evidence.Add(dll);
        }

        if (processRunning)
        {
            fact.Evidence.Add("nvda.exe 进程运行中");
        }

        fact.Fact = (dll, processRunning) switch
        {
            (not null, true) => "NVDA 进程运行中，且找到控制器 DLL。",
            (not null, false) => "找到 NVDA 控制器 DLL，但 NVDA 进程未运行。",
            (null, true) => "NVDA 进程运行中，但未找到 nvdaControllerClient*.dll。",
            _ => "未检测到 NVDA 进程或控制器 DLL。",
        };

        return fact;
    }

    private static VoiceBackendFact ProbeZdsr(IReadOnlyList<string> roots)
    {
        var processRunning = IsProcessRunning(
            "ZDSRMain",
            "ZDSRMain_x64",
            "ZDSRDaemon",
            "ZDSRService",
            "ZDSRMultiEngine");

        var dll = FindFile(
            roots.Concat(new[]
            {
                @"C:\Program Files (x86)\zdsr\zdsr",
                @"C:\Program Files\zdsr\zdsr",
            }),
            "ZDSRAPI_x64.dll",
            "ZDSRAPI.dll");

        var fact = new VoiceBackendFact
        {
            Id = "zdsr",
            DisplayName = "争渡",
            Available = dll is not null && processRunning,
        };

        if (dll is not null)
        {
            fact.Evidence.Add(dll);
        }

        if (processRunning)
        {
            fact.Evidence.Add("争渡进程运行中");
        }

        fact.Fact = (dll, processRunning) switch
        {
            (not null, true) => "争渡进程运行中，且找到 ZDSRAPI DLL。",
            (not null, false) => "找到争渡 API DLL，但争渡进程未运行。",
            (null, true) => "争渡进程运行中，但未找到 ZDSRAPI*.dll。",
            _ => "未检测到争渡进程或 API DLL。",
        };

        return fact;
    }

    private static VoiceBackendFact ProbeSapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VoiceBackendFact
            {
                Id = "sapi",
                DisplayName = "SAPI5",
                Available = null,
                Fact = "非 Windows 平台，未检查 SAPI voice token。",
            };
        }

        var count = 0;
        var evidence = new List<string>();
        var locations = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Speech\Voices\Tokens"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Speech\Voices\Tokens"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens"),
        };

        foreach (var (hive, subKey) in locations)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key is null)
                {
                    continue;
                }

                var names = key.GetSubKeyNames();
                count += names.Length;
                evidence.Add($"{hive.Name}\\{subKey}：{names.Length} 个 token");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                evidence.Add($"{hive.Name}\\{subKey}：读取失败（{ex.GetType().Name}）");
            }
        }

        return new VoiceBackendFact
        {
            Id = "sapi",
            DisplayName = "SAPI5",
            Available = count > 0,
            Fact = count > 0
                ? $"注册表中共找到 {count} 个 SAPI voice token（未校验语言与音质）。"
                : "注册表未找到 SAPI voice token。",
            Evidence = evidence,
        };
    }

    private static IReadOnlyList<string> CandidateRoots(string gameDirectory)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 单个候选目录不合法不影响整体探测。
            }
        }

        Add(gameDirectory);
        Add(AppContext.BaseDirectory);
        Add(Environment.CurrentDirectory);
        Add(Path.Combine(Environment.CurrentDirectory, "_tolk_probe"));
        Add(Path.Combine(AppContext.BaseDirectory, "_tolk_probe"));
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalA11yForge",
            "adapters"));
        Add(Environment.GetEnvironmentVariable("GALA11Y_TOLK_DIR"));

        return roots;
    }

    private static string? FindFile(IEnumerable<string> roots, params string[] fileNames)
    {
        foreach (var root in roots)
        {
            foreach (var fileName in fileNames)
            {
                try
                {
                    var candidate = Path.Combine(root, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // 跳过非法候选路径。
                }
            }
        }

        return null;
    }

    private static bool IsProcessRunning(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
