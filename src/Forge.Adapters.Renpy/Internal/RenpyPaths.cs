using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>工作区内的固定位置。游戏目录永远只读，生成物只写这里。</summary>
internal static class RenpyPaths
{
    public static string ResolveSlug(GameContext context)
    {
        if (PathSafety.IsValidSlug(context.Slug))
        {
            return context.Slug;
        }

        return PathSafety.SlugFromGameDirectory(context.GameDirectory);
    }

    public static string DefaultGeneratedDirectory(GameContext context)
    {
        var slug = ResolveSlug(context);
        return Path.Combine(context.WorkspaceRoot, "games", slug, "generated");
    }
}
