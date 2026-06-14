using System;
using System.IO;
using System.Text;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Écriture des artefacts d'observabilité par-scénario, co-localisés avec les self-snaps du worker
/// sous <c>%LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\&lt;RIG_RUN_STAMP&gt;\&lt;scenarioId&gt;\</c> :
/// #5 <c>worker.stdout.log</c> (stdout complet du worker) et #6 <c>phases.jsonl</c> (transitions de phase
/// {ts, phase, snap_idx}). Extrait du VM pour être testable sans WPF/DEV. Best-effort : aucun échec
/// d'écriture ne casse le batch ; si RIG_RUN_STAMP est absent, no-op (pas de dossier cible).
/// </summary>
public static class ObservabilityFiles
{
    /// <summary>Dossier de snaps du scénario, ou null si RIG_RUN_STAMP absent (→ no-op appelant).</summary>
    private static string ScenarioDir(string scenarioId)
    {
        var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
        if (string.IsNullOrEmpty(runStamp)) return null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "rig-wpf-testviewer", "self-snaps", runStamp, scenarioId);
    }

    /// <summary>#5 — écrit le stdout complet d'un worker dans son dossier de self-snaps co-localisé,
    /// pour que log et PNG se lisent au même préfixe de chemin (plus de bracket-grep du log combiné).</summary>
    public static void WriteWorkerStdout(string scenarioId, string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return;
        var dir = ScenarioDir(scenarioId);
        if (dir == null) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "worker.stdout.log"), stdout, new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warn($"#5 worker.stdout.log [{scenarioId}] : {ex.Message}"); }
    }

    /// <summary>#6 — append une transition de phase {ts, phase, snap_idx} en JSONL. snap_idx/ts sont
    /// extraits des tags [snap=]/[hh:mm:ss.fff] de la ligne stdout (#2) ; sinon snap_idx=-1 et ts=maintenant.</summary>
    public static void AppendPhase(string scenarioId, string line, string phase)
    {
        var dir = ScenarioDir(scenarioId);
        if (dir == null) return;
        try
        {
            int snap = ObservabilityTags.ParseSnapTag(line);
            var ts = ObservabilityTags.ParseTimeTag(line, DateTime.Now.ToString("HH:mm:ss.fff"));
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "phases.jsonl"),
                ObservabilityTags.PhaseLine(ts, phase, snap) + Environment.NewLine);
        }
        catch (Exception ex) { Log.Warn($"#6 phases.jsonl [{scenarioId}] : {ex.Message}"); }
    }
}
