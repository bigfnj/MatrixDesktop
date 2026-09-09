using System;
using System.IO;

namespace MatrixDesktopConfigurator;

// Locates the bundled matrix web/ folder so the configurator can map it to a virtual host
// for the embedded live-preview iframe.
//
// This is all that survived PreviewWindow. That class drove a second top-level WebView2
// window, and commit 808262a replaced it with an in-page iframe driven by buildWebQuery
// without removing the old code, so ~200 lines including a whole Form subclass sat
// unreachable: nothing in configurator/js/app.js ever sent openPreview, closePreview or
// previewCommand, which were its only entry points.
internal static class WebAssets
{
    // First the publish layout, where web/ sits beside the executable. Otherwise walk up
    // looking for the MatrixDesktop project's own web/ folder. The previous version used
    // fixed "..\..\..\.." hops, which encoded an exact output depth and therefore silently
    // returned null for any RID-specific build, where the output gains a win-x64 level.
    public static string? FindWebRoot()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "web");
        if (IsWebRoot(beside))
        {
            return Path.GetFullPath(beside);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(current.FullName, "MatrixDesktop", "web"),
                         Path.Combine(current.FullName, "web"),
                     })
            {
                if (IsWebRoot(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static bool IsWebRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var full = Path.GetFullPath(candidate);
            return Directory.Exists(full) && File.Exists(Path.Combine(full, "index.html"));
        }
        catch
        {
            return false;
        }
    }
}
