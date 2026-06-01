using System;
using System.IO;
using System.Text;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Couche Mémoire : range les artefacts d'un run dans
/// %LOCALAPPDATA%\rig-wpf-testviewer\loop\runs\&lt;runId&gt;\ et tient history.jsonl.
/// </summary>
internal static class LoopArtifacts
{
    public static string LoopRoot()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "rig-wpf-testviewer", "loop");
        Directory.CreateDirectory(root);
        return root;
    }

    public static string RunDir(string runId)
    {
        string d = Path.Combine(LoopRoot(), "runs", runId);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Écrit result.json dans runs/&lt;id&gt;/ et append 1 ligne dans history.jsonl.</summary>
    public static void Persist(LoopRunResult result)
    {
        string dir = RunDir(result.RunId);
        string resultPath = Path.Combine(dir, "result.json");
        result.WriteTo(resultPath);

        // history.jsonl : 1 ligne JSON compacte = le summary + runId.
        string histLine = string.Format(
            "{{\"runId\":\"{0}\",\"startedAt\":\"{1}\",\"passed\":{2},\"failed\":{3},\"flaky\":{4},\"skipped\":{5},\"durationMs\":{6}}}",
            result.RunId, result.StartedAt,
            result.Summary.Passed, result.Summary.Failed, result.Summary.Flaky,
            result.Summary.Skipped, result.Summary.DurationMs);
        File.AppendAllText(Path.Combine(LoopRoot(), "history.jsonl"),
            histLine + Environment.NewLine, new UTF8Encoding(false));
    }
}
