using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Profiles;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Core.Tests;

public sealed class ProfileMergerTests
{
    [Fact]
    public void Overrides_Win_ForScalars_AndNullInherits()
    {
        var profile = TestData.Profile();
        var overrides = new ProfileDocument
        {
            Engine = "renpy",
            MinConfidence = 0.9,
            Voice = null,
            Advanced = true,
            LastResult = null,
        };

        var merged = ProfileMerger.Merge(profile, overrides);

        Assert.Equal("renpy", merged.Engine);
        Assert.Equal(0.9, merged.MinConfidence);
        Assert.Equal("sapi", merged.Voice);
        Assert.True(merged.Advanced);
        Assert.Equal("detected", merged.LastResult);
        Assert.Equal("sample-game", merged.Slug);
    }

    [Fact]
    public void Keymap_IsMergedPerKey()
    {
        var profile = TestData.Profile();
        var overrides = new ProfileDocument
        {
            Keymap = new Dictionary<string, string>
            {
                ["advance"] = "Enter",
                ["auto"] = "A",
            },
        };

        var merged = ProfileMerger.Merge(profile, overrides);

        Assert.Equal("Enter", merged.Keymap["advance"]);
        Assert.Equal("Tab", merged.Keymap["menu"]);
        Assert.Equal("A", merged.Keymap["auto"]);
    }

    [Fact]
    public void Options_AreMergedRecursively_AndArraysReplace()
    {
        var profile = new ProfileDocument
        {
            Options = new Dictionary<string, JsonElement>
            {
                ["plugin"] = JsonSerializer.SerializeToElement(new
                {
                    mode = "safe",
                    retries = 3,
                    tags = new[] { "a", "b" },
                }),
            },
        };
        var overrides = new ProfileDocument
        {
            Options = new Dictionary<string, JsonElement>
            {
                ["plugin"] = JsonSerializer.SerializeToElement(new
                {
                    retries = 5,
                    tags = new[] { "c" },
                }),
            },
        };

        var merged = ProfileMerger.Merge(profile, overrides);
        var plugin = merged.Options["plugin"];

        Assert.Equal("safe", plugin.GetProperty("mode").GetString());
        Assert.Equal(5, plugin.GetProperty("retries").GetInt32());
        Assert.Equal(1, plugin.GetProperty("tags").GetArrayLength());
        Assert.Equal("c", plugin.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public void Merge_DoesNotMutateInputs()
    {
        var profile = TestData.Profile();
        var originalEngine = profile.Engine;
        var originalAdvance = profile.Keymap["advance"];
        var overrides = new ProfileDocument
        {
            Engine = "renpy",
            Keymap = new Dictionary<string, string> { ["advance"] = "Enter" },
        };

        _ = ProfileMerger.Merge(profile, overrides);

        Assert.Equal(originalEngine, profile.Engine);
        Assert.Equal(originalAdvance, profile.Keymap["advance"]);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeConfidence()
    {
        var result = ProfileMerger.Validate(new ProfileDocument { MinConfidence = 1.5 });

        Assert.False(result.Ok);
        Assert.Equal("E_PROFILE_INVALID", result.Error!.Code);
    }

    [Fact]
    public void LoadPair_MissingFiles_ReturnsDefaultProfile()
    {
        using var temp = new TempFolder();
        var result = ProfileStore.LoadPair(
            Path.Combine(temp.Root, "profile.json"),
            Path.Combine(temp.Root, "overrides.json"));

        Assert.True(result.Ok);
        Assert.Equal(1, result.Value!.SchemaVersion);
    }

    [Fact]
    public void LoadPair_InvalidJson_ReturnsProfileInvalid()
    {
        using var temp = new TempFolder();
        var result = ProfileStore.LoadPair(temp.File("profile.json", "{ not json"), temp.File("overrides.json", "{}"));

        Assert.False(result.Ok);
        Assert.Equal("E_PROFILE_INVALID", result.Error!.Code);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsMergedProfile()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "games", "sample", "profile.json");
        var merged = ProfileMerger.Merge(TestData.Profile(), new ProfileDocument { Voice = "nvda" });

        var saved = ProfileStore.Save(path, merged);
        var loaded = ProfileStore.Load(path);

        Assert.True(saved.Ok);
        Assert.True(loaded.Ok);
        Assert.Equal("nvda", loaded.Value!.Voice);
        Assert.Equal("Space", loaded.Value.Keymap["advance"]);
    }
}
