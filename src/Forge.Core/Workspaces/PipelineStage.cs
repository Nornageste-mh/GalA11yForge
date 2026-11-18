namespace GalA11yForge.Core.Workspaces;

/// <summary>七步流水线的目录序号，与 docs/02 的 games/&lt;slug&gt;/00-detect ... 06-verify 对应。</summary>
public enum PipelineStage
{
    Detect = 0,
    Analyze = 1,
    Plan = 2,
    Generate = 3,
    Build = 4,
    Deploy = 5,
    Verify = 6,
}

public static class PipelineStageExtensions
{
    public static string DirectoryName(this PipelineStage stage) => stage switch
    {
        PipelineStage.Detect => "00-detect",
        PipelineStage.Analyze => "01-analyze",
        PipelineStage.Plan => "02-decide",
        PipelineStage.Generate => "03-generate",
        PipelineStage.Build => "04-build",
        PipelineStage.Deploy => "05-deploy",
        PipelineStage.Verify => "06-verify",
        _ => "99-other",
    };
}
