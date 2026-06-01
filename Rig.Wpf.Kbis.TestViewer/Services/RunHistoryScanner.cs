using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Une entrée d'historique = un dossier <c>self-snaps/&lt;runStamp&gt;/</c> avec ses
/// sous-dossiers (un par scénario) et la somme des octets contenus.
/// </summary>
public sealed class RunHistoryEntry
{
    public string RunStamp { get; set; } = "";
    public string DirPath { get; set; } = "";
    public int ScenarioCount { get; set; }
    public long Bytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Phase 5 — scan <c>%LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\</c> (ou root passé)
/// et renvoie les 10 runs les plus récents, du plus récent au plus ancien.
/// </summary>
/// <remarks>
/// Robuste à un root inexistant (renvoie liste vide). Le calcul des octets est best-effort :
/// si un dossier est en concurrence d'écriture par un worker, on log et on renvoie 0
/// pour cette entrée plutôt que de cracher tout le scan.
/// </remarks>
public static class RunHistoryScanner
{
    public static IReadOnlyList<RunHistoryEntry> Scan(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<RunHistoryEntry>();
        return new DirectoryInfo(root).GetDirectories()
            .Select(d => new RunHistoryEntry
            {
                RunStamp = d.Name,
                DirPath = d.FullName,
                ScenarioCount = SafeCountDirs(d),
                Bytes = SafeSum(d),
                CreatedAt = d.CreationTime
            })
            .OrderByDescending(e => e.CreatedAt)
            .Take(10)
            .ToList();
    }

    private static int SafeCountDirs(DirectoryInfo d)
    {
        try { return d.GetDirectories().Length; }
        catch { return 0; }
    }

    private static long SafeSum(DirectoryInfo d)
    {
        try { return d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }
}
