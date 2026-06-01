using System.Collections.Generic;

namespace Rig.Wpf.Kbis.TestViewer.Helpers;

/// <summary>
/// Mode visibilité du batch Rapture :
/// A = RIG live sur ton desktop courant (HEADLESS=0, focus stealing actif)
/// B = HDESK isolé + self-snap PNG (HEADLESS=1, défaut, 0 vol focus)
/// C = HEADLESS=0 mais sur Desktop 2 Sysinternals (0 vol focus desktop 1)
/// D = Headless invisible total (--rapture-selfdrive sans HDESK)
/// </summary>
public enum RaptureMode { A, B, C, D }

/// <summary>
/// Option affichable du combo <c>CmbRaptureMode</c> — couple (enum, libellé, description).
/// La liste <see cref="All"/> est la source ItemsSource du combo (B en premier = défaut).
/// </summary>
public sealed class RaptureModeOption
{
    // set au lieu de init — net48 manque IsExternalInit dans ce projet.
    public RaptureMode Mode { get; set; }
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";

    public static IReadOnlyList<RaptureModeOption> All { get; } = new[]
    {
        new RaptureModeOption { Mode = RaptureMode.B, Label = "B — HDESK self-snap (défaut)", Description = "RIG isolé HDESK, PNGs sur disque, 0 vol focus" },
        new RaptureModeOption { Mode = RaptureMode.A, Label = "A — Live focus stealing", Description = "RIG sur ton écran, perte focus pendant le run" },
        new RaptureModeOption { Mode = RaptureMode.C, Label = "C — Desktop 2 Sysinternals", Description = "RIG sur 2e desktop, Win+Alt+2 pour observer" },
        new RaptureModeOption { Mode = RaptureMode.D, Label = "D — Headless invisible", Description = "selfdrive in-process, le plus rapide (CI-like)" },
    };

    // Sans override de ToString(), UIA expose le FQN de la classe ("Rig.Wpf.Kbis.TestViewer.Helpers.RaptureModeOption")
    // pour chaque ListItem du combo — drive-rapture-start.ps1 ne peut pas distinguer A/B/C/D.
    // DisplayMemberPath="Label" affecte juste le rendering visuel, pas le Name UIA.
    public override string ToString() => Label;
}
