namespace GalA11yForge.Analyzers.Ghidra.Tests;

/// <summary>CandidateHookDump.java 的真实输出形状（schema gala11y.ghidra.candidates/1）的最小样本。</summary>
internal static class Fixtures
{
    public const string CandidateJson = """
        {
          "rank": 1,
          "function": "FUN_00401000",
          "address": "0x401000",
          "confidence": 47,
          "level": "medium",
          "apis": ["TextOutW", "GetTextExtentPoint32W"],
          "evidence": {
            "caller_count": 2,
            "callers": ["0x402000", "0x403000"],
            "cjk_string_refs": 1,
            "name_hint": true,
            "debug_hint": false,
            "strings": [
              {"address": "0x40a000", "text": "こんにちは、世界。これは十分に長いテスト用の文字列です。", "cjk_chars": 20},
              {"address": "0x40a100", "text": "二つ目", "cjk_chars": 3}
            ]
          },
          "hook": {"slot": "textout", "strategy": "hook TextOutW and read the string pointer"}
        }
        """;

    public static string CandidatesJson(
        bool packed = false,
        string? candidateJson = null,
        int functions = 420,
        bool dotnet = false,
        bool dynamicApi = false)
    {
        var packedText = packed ? "true" : "false";
        var dotnetText = dotnet ? "true" : "false";
        var dynamicText = dynamicApi ? "true" : "false";
        var candidate = candidateJson ?? CandidateJson;

        return $$"""
            {
              "schema": "gala11y.ghidra.candidates/1",
              "generated_at": "2026-11-14T14:00:00Z",
              "tool": {"name": "Ghidra", "version": "12.1.4", "script": "CandidateHookDump.java"},
              "binary": {
                "path": "C:\\games\\game.exe",
                "name": "game.exe",
                "sha256": "601b10faac9fbf2a4a7d54bc53a4c6790eea99254eff1acb103ec964d9fa1945",
                "size_bytes": 4583168,
                "executable_format": "PE",
                "language": "x86:LE:32:default",
                "compiler": "windows",
                "bits": 32,
                "image_base": "0x400000",
                "entry_point": "0x00456b00",
                "is_dotnet": {{dotnetText}},
                "dynamic_api_resolution_suspected": {{dynamicText}},
                "packed_suspected": {{packedText}},
                "sections": [{"name": ".text", "start": "0x401000", "size": 4096, "execute": true, "write": false, "entropy": 7.91}],
                "dlls": ["user32.dll", "gdi32.dll"]
              },
              "text_api_surface": {
                "present": ["TextOutW", "GetTextExtentPoint32W", "SetWindowTextW"],
                "missing": ["DrawTextW"],
                "import_slots": [{"api": "TextOutW", "dll": "user32.dll", "category": "text", "slot": "0x0045a000", "ref_sites": 2}]
              },
              "candidates": [{{candidate}}],
              "string_table": {
                "source": "defined",
                "defined_strings_total": 100,
                "defined_cjk_strings": 10,
                "utf16_scan_cjk_strings": 12,
                "with_code_xref": 4,
                "top_anchors": [{"address": "0x40a000", "text": "こんにちは", "cjk_chars": 5, "refs": 1, "functions": ["FUN_00401000"]}]
              },
              "engine_hints": [{"id": "kirikiri", "text": "KiriKiri 系列特征"}],
              "stats": {
                "functions": {{functions}},
                "instructions_in_candidate_bodies": 1000,
                "defined_strings": 100,
                "defined_cjk_strings": 10,
                "utf16_scan_cjk_strings": 12,
                "cjk_strings": 13,
                "global_scan_candidates": 4,
                "candidate_count": 1,
                "script_ms": 1500
              }
            }
            """;
    }
}
