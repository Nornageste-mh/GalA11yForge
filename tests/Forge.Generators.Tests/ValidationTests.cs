using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Generators.Tests;

/// <summary>输入校验：每类「缺必填 / 不合法」都要有稳定错误码，且绝不写盘。</summary>
public sealed class ValidationTests : GeneratorTestBase
{
    [Fact]
    public async Task Null_plan_returns_argument_invalid()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), null!, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "plan");
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Null_context_returns_argument_invalid()
    {
        var result = await CreateGenerator().GenerateAsync(null!, Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid);
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Plan_without_patch_points_returns_argument_invalid()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(), OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "补丁点");
    }

    [Fact]
    public async Task Missing_slug_returns_argument_invalid()
    {
        var plan = Plan(Hook("a", "GameNS.A:B"));
        plan.GameSlug = string.Empty;

        var result = await CreateGenerator().GenerateAsync(Context(slug: string.Empty), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "slug");
    }

    [Fact]
    public async Task Invalid_slug_returns_argument_invalid()
    {
        var plan = Plan(Hook("a", "GameNS.A:B"));
        plan.GameSlug = "Bad Slug";

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "slug");
    }

    [Fact]
    public async Task Unsupported_route_returns_engine_unsupported()
    {
        var plan = Plan(Hook("a", "GameNS.A:B"));
        plan.Route = PlanRoute.Unsupported;

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.EngineUnsupported);
    }

    [Fact]
    public async Task Unsupported_engine_returns_engine_unsupported()
    {
        var plan = Plan(Hook("a", "GameNS.A:B"));
        plan.Target.Engine = "unity_il2cpp";

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.EngineUnsupported, "unity_mono");
    }

    [Fact]
    public async Task Empty_output_directory_returns_argument_invalid()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), "   ");

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "输出目录");
    }

    [Fact]
    public async Task Hook_target_with_dialogue_returns_argument_invalid()
    {
        var plan = Plan(Hook("a", "GameNS.A:B 这是一句台词，别写进代码也不要猜类名"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid);
        Assert.True(result.Error!.Message.Length < 500, "错误消息不应回显整段台词");
    }

    [Fact]
    public async Task Empty_hook_target_returns_argument_invalid()
    {
        var plan = Plan(Hook("a", string.Empty));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "目标为空");
    }

    [Fact]
    public async Task Constructor_target_returns_argument_invalid()
    {
        var plan = Plan(Hook("a", "GameNS.A:.ctor(string)"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid);
    }

    [Fact]
    public async Task Duplicate_patch_point_ids_returns_argument_invalid()
    {
        var plan = Plan(Hook("dup", "GameNS.A:B"), Hook("dup", "GameNS.A:C"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "重复");
    }

    [Fact]
    public async Task Non_hook_only_plan_returns_argument_invalid()
    {
        var plan = Plan(NonHook("config-only"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "kind=hook");
    }

    [Fact]
    public async Task Invalid_version_option_returns_argument_invalid()
    {
        var result = await CreateGenerator(options: new GeneratorOptions { Version = "1.0-beta" })
            .GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "版本号");
    }

    [Fact]
    public async Task Invalid_namespace_option_returns_argument_invalid()
    {
        var result = await CreateGenerator(options: new GeneratorOptions { Namespace = "1Bad.Ns" })
            .GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "命名空间");
    }

    [Fact]
    public async Task Invalid_guid_option_returns_argument_invalid()
    {
        var result = await CreateGenerator(options: new GeneratorOptions { PluginGuid = "not-a-guid" })
            .GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "GUID");
    }

    [Fact]
    public async Task Bridge_without_contract_layer_returns_dependency_missing()
    {
        var source = new StubVoiceBridgeSource(
            new VoiceBridgeFile("Platform/Speech.cs", StubFiles.Speech, false, "stub/Speech.cs", StubFiles.Sha(StubFiles.Speech)));

        var result = await CreateGenerator(source).GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.DependencyMissing, "A11yHost");
    }

    [Fact]
    public async Task Bridge_without_framework_namespace_returns_artifact_invalid()
    {
        var broken = "internal static class Speech { }\n";
        var source = new StubVoiceBridgeSource(
            new VoiceBridgeFile("Platform/Speech.cs", broken, false, "stub/Speech.cs", StubFiles.Sha(broken)),
            new VoiceBridgeFile("Platform/A11yHost.cs", StubFiles.Host, false, "stub/A11yHost.cs", StubFiles.Sha(StubFiles.Host)));

        var result = await CreateGenerator(source).GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArtifactInvalid, "namespace A11yFramework");
    }

    [Fact]
    public async Task Bridge_with_leftover_placeholder_returns_artifact_invalid()
    {
        var bad = "namespace A11yFramework\n{\n    internal static class Bad { internal const string Value = \"{{NS}}\"; }\n}\n";
        var source = new StubVoiceBridgeSource(
            new VoiceBridgeFile("Platform/Speech.cs", StubFiles.Speech, false, "stub/Speech.cs", StubFiles.Sha(StubFiles.Speech)),
            new VoiceBridgeFile("Platform/A11yHost.cs", StubFiles.Host, false, "stub/A11yHost.cs", StubFiles.Sha(StubFiles.Host)),
            new VoiceBridgeFile("Platform/Bad.cs", bad, false, "stub/Bad.cs", StubFiles.Sha(bad)));

        var result = await CreateGenerator(source).GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArtifactInvalid, "{{NS}}");
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Bridge_file_outside_platform_returns_artifact_invalid()
    {
        var source = new StubVoiceBridgeSource(
            new VoiceBridgeFile("Lib/Speech.cs", StubFiles.Speech, false, "stub/Speech.cs", StubFiles.Sha(StubFiles.Speech)),
            new VoiceBridgeFile("Platform/A11yHost.cs", StubFiles.Host, false, "stub/A11yHost.cs", StubFiles.Sha(StubFiles.Host)));

        var result = await CreateGenerator(source).GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArtifactInvalid, "Platform/");
    }

    [Fact]
    public async Task Missing_framework_directory_returns_dependency_missing()
    {
        var generator = new UnityMonoGenerator(null, new GeneratorOptions
        {
            FrameworkDirectory = Path.Combine(Root, "no-such-framework"),
        });

        var result = await generator.GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.DependencyMissing, "Framework");
    }
}
