using System;
using System.IO;
using System.Linq;

namespace Rig.Wpf.Kbis.TestViewer.Services;

public static class SnapDiskUsage
{
    public static (long Bytes, int RunCount) ComputeTotal()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "rig-wpf-testviewer", "self-snaps");
        if (!Directory.Exists(root)) return (0, 0);
        var runs = new DirectoryInfo(root).GetDirectories();
        long total = 0;
        foreach (var r in runs)
        {
            try { total += r.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { /* ignore inaccessible run dirs */ }
        }
        return (total, runs.Length);
    }

    public static string FormatHuman(long bytes, int runCount)
    {
        var mb = bytes / 1024.0 / 1024.0;
        if (mb < 1024) return $"{mb:F1} MB sur {runCount} runs";
        return $"{mb / 1024:F1} GB sur {runCount} runs";
    }
}
