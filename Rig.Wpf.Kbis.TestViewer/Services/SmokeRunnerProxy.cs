using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Shell-out vers <c>Rig.Wpf.Kbis.SmokeRunner.exe</c> et parse stdout (lignes
/// "  ✓ desc" / "  ✗ desc" / "  ⊘ desc") au fil de l'eau. Émet des événements
/// quand de nouvelles lignes-résultats sont captées, pour MAJ UI live.
///
/// Choix shell-out plutôt que in-process : isolation (un STA WPF par process,
/// le runner crée le sien, on évite les conflits), même protocole testé que
/// le Blazor TestViewer initial.
/// </summary>
public sealed class SmokeRunnerProxy
{
    private readonly string _exePath;
    private readonly string _rendersDir;
    private readonly object _lock = new();
    private readonly List<SmokeResultLine> _lines = new();

    public SmokeRunnerProxy(string exePath, string rendersDir)
    {
        _exePath = exePath;
        _rendersDir = rendersDir;
    }

    public event Action? LinesChanged;
    public event Action? StateChanged;

    public string ExePath => _exePath;
    public string RendersDir => _rendersDir;
    public bool ExeExists => File.Exists(_exePath);

    public bool IsRunning { get; private set; }

    private int _childPid;
    private bool _isPaused;
    /// <summary>True si le run en cours est gelé (Pause). Toggle via <see cref="Pause"/>/<see cref="Resume"/>.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Stop : tue l'arbre de process (SmokeRunner + RigClientAccueil + diag). Le run se termine proprement (stdout fermé).</summary>
    public void Stop()
    {
        var pid = _childPid;
        if (!IsRunning || pid <= 0) return;
        ProcessTreeControl.KillTree(pid);
    }

    /// <summary>Pause : gèle instantanément tout l'arbre (auto FlaUI + fenêtre RIG figées en l'état).</summary>
    public void Pause()
    {
        var pid = _childPid;
        if (!IsRunning || pid <= 0 || _isPaused) return;
        ProcessTreeControl.SuspendTree(pid);
        _isPaused = true;
        StateChanged?.Invoke();
    }

    /// <summary>Reprise : dégèle l'arbre, le run repart pile où il en était.</summary>
    public void Resume()
    {
        var pid = _childPid;
        if (!IsRunning || pid <= 0 || !_isPaused) return;
        ProcessTreeControl.ResumeTree(pid);
        _isPaused = false;
        StateChanged?.Invoke();
    }

    public DateTime? LastRunStartedAt { get; private set; }
    public TimeSpan? LastRunDuration { get; private set; }
    public int? LastExitCode { get; private set; }
    public string? LastFullStdout { get; private set; }
    public string? LastStderr { get; private set; }

    public IReadOnlyList<SmokeResultLine> Lines
    {
        get { lock (_lock) { return _lines.ToList(); } }
    }

    private static readonly Regex ResultLineRegex = new(
        @"^\s+(?<icon>[✓✗⊘])\s+(?<desc>.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Pour les batchs en parallèle (spawn de plusieurs workers en dehors du RunAsync standard) :
    /// inject un stdout combiné à parser pour update Lines + raise LinesChanged.
    /// Utilisé par RunLegacyAsync pour stitcher VK + XEX workers parallèles.
    /// </summary>
    public void ManualInjectStdoutForParsing(string combinedStdout)
    {
        lock (_lock) { _lines.Clear(); }
        foreach (var rawLine in combinedStdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = ResultLineRegex.Match(line);
            if (!m.Success) continue;
            var icon = m.Groups["icon"].Value;
            var desc = m.Groups["desc"].Value;
            var outcome = icon switch
            {
                "✓" => SmokeOutcome.Passed,
                "✗" => SmokeOutcome.Failed,
                "⊘" => SmokeOutcome.Skipped,
                _ => SmokeOutcome.Unknown,
            };
            lock (_lock)
            {
                _lines.Add(new SmokeResultLine(_lines.Count, outcome, desc, DateTime.Now));
            }
        }
        LastFullStdout = combinedStdout;
        LinesChanged?.Invoke();
    }

    /// <summary>
    /// Append UNE ligne au flux Lines (streaming) + raise LinesChanged. Append-mode utilisé
    /// par les workers parallèles qui poussent leurs ✓/✗ stdout en temps réel (vs
    /// ManualInjectStdoutForParsing qui remplace tout en batch).
    /// Le <paramref name="workerId"/> permet de tagger la ligne pour filtrer par scenario
    /// (ex. KbisLegacyScenarioAdapter filtre par "kbis-vk" ou "kbis-xex").
    /// </summary>
    public void AppendLineForParsing(string line, string? workerId = null)
    {
        var trimmed = line.TrimEnd('\r');
        var m = ResultLineRegex.Match(trimmed);
        if (!m.Success) return;
        var icon = m.Groups["icon"].Value;
        var desc = m.Groups["desc"].Value;
        var outcome = icon switch
        {
            "✓" => SmokeOutcome.Passed,
            "✗" => SmokeOutcome.Failed,
            "⊘" => SmokeOutcome.Skipped,
            _ => SmokeOutcome.Unknown,
        };
        lock (_lock)
        {
            _lines.Add(new SmokeResultLine(_lines.Count, outcome, desc, DateTime.Now, workerId));
        }
        LinesChanged?.Invoke();
    }

    /// <summary>Reset Lines (avant un batch parallèle pour repartir clean).</summary>
    public void ResetLines()
    {
        lock (_lock) { _lines.Clear(); }
        LinesChanged?.Invoke();
    }

    /// <summary>Reset uniquement les lignes taggées <paramref name="workerId"/> — pour relancer UN
    /// seul scénario en place (bouton ▶ d'une tuile) sans effacer l'état des autres tuiles.</summary>
    public void ResetLinesForWorker(string workerId)
    {
        lock (_lock) { _lines.RemoveAll(l => l.WorkerId == workerId); }
        LinesChanged?.Invoke();
    }

    /// <summary>
    /// Arguments fixes passés à l'exe à chaque run (ex : <c>--legacy</c> pour le mode legacy).
    /// Null par défaut = pas d'arg fixe. Le flag <c>enableUi</c> est ajouté dynamiquement.
    /// (set au lieu de init — net48 manque IsExternalInit.)
    /// </summary>
    public string? FixedArgs { get; set; }

    /// <summary>
    /// Arguments dynamiques ajoutés pour le PROCHAIN run uniquement, puis remis à null.
    /// Sert aux scénarios paramétrés type <c>--legacy-rapture-import --json &lt;path&gt;</c>
    /// où le path varie selon ce que l'utilisateur a sélectionné dans la UI.
    /// </summary>
    public string? ExtraArgs { get; set; }

    /// <summary>
    /// Env vars à propager au process enfant pour le PROCHAIN run uniquement.
    /// Consommé puis remis à null comme <see cref="ExtraArgs"/>. Override les env
    /// vars héritées du process parent. Ex. <c>{"RIG_DRIVER_HEADLESS","0"}</c>
    /// pour rendre les fenêtres RIG visibles sur le desktop utilisateur.
    /// </summary>
    public Dictionary<string, string>? EnvironmentOverrides { get; set; }

    public async Task RunAsync(bool enableUi, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("SmokeRunner déjà en cours");
        if (!ExeExists) throw new FileNotFoundException("Exe SmokeRunner introuvable", _exePath);

        IsRunning = true;
        LastRunStartedAt = DateTime.Now;
        LastExitCode = null;
        LastStderr = null;
        lock (_lock) { _lines.Clear(); }
        StateChanged?.Invoke();
        LinesChanged?.Invoke();

        var sw = Stopwatch.StartNew();
        try
        {
            await RunCoreAsync(enableUi, ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            LastRunDuration = sw.Elapsed;
            IsRunning = false;
            _childPid = 0;
            _isPaused = false;
            StateChanged?.Invoke();
        }
    }

    private async Task RunCoreAsync(bool enableUi, CancellationToken ct)
    {
        // Compose args : fixe (--legacy par ex) + extra (--json <path>) + dynamique (--ui).
        // ExtraArgs est consommé puis remis à null pour ne pas leak d'un run à l'autre.
        var argParts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(FixedArgs)) argParts.Add(FixedArgs!);
        if (!string.IsNullOrEmpty(ExtraArgs)) argParts.Add(ExtraArgs!);
        ExtraArgs = null;
        if (enableUi) argParts.Add("--ui");

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory,
            // ArgumentList n'existe pas en net48 — on bourre Arguments avec une chaîne simple.
            // Pas d'injection possible : pas d'input utilisateur ici, juste des flags connus.
            Arguments = string.Join(" ", argParts),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = !enableUi,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Apply env var overrides (consumed once, then cleared)
        if (EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in EnvironmentOverrides)
                psi.EnvironmentVariables[kv.Key] = kv.Value;
            EnvironmentOverrides = null;
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start a renvoyé null");
        _childPid = p.Id;

        var sb = new StringBuilder();
        var stderrTask = p.StandardError.ReadToEndAsync();

        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            sb.AppendLine(line);
            // Update stdout en temps réel pour que l'onglet Logs se rafraîchisse
            // à chaque ligne, pas seulement à la fin du run.
            LastFullStdout = sb.ToString();

            var m = ResultLineRegex.Match(line);
            if (m.Success)
            {
                var icon = m.Groups["icon"].Value;
                var desc = m.Groups["desc"].Value;
                var outcome = icon switch
                {
                    "✓" => SmokeOutcome.Passed,
                    "✗" => SmokeOutcome.Failed,
                    "⊘" => SmokeOutcome.Skipped,
                    _ => SmokeOutcome.Unknown,
                };
                lock (_lock)
                {
                    _lines.Add(new SmokeResultLine(_lines.Count, outcome, desc, DateTime.Now));
                }
            }
            // Notify TOUJOURS, pas seulement sur match. Permet à l'UI de raffraîchir
            // le stdout live même quand on a juste des logs FOP / Console.WriteLine.
            LinesChanged?.Invoke();
            if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } break; }
        }
        try { p.WaitForExit(); } catch { /* déjà mort */ }
        LastExitCode = p.HasExited ? p.ExitCode : null;
        LastFullStdout = sb.ToString();   // valeur finale (cohérence)
        LastStderr = await stderrTask.ConfigureAwait(false);
    }

    public IReadOnlyList<string> ListRenderFiles()
    {
        if (!Directory.Exists(_rendersDir)) return Array.Empty<string>();
        return new DirectoryInfo(_rendersDir)
            .EnumerateFiles("*.png")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .ToList();
    }

    /// <summary>Path du PDF généré au dernier run (extrait du stdout). Null si pas trouvé.</summary>
    public string? LastGeneratedPdfPath()
    {
        if (LastFullStdout is null) return null;
        // Match "PDF généré sur disque (C:\path\to\file.pdf)"
        var m = Regex.Match(LastFullStdout, @"PDF généré sur disque \((?<p>[^)]+)\)");
        if (!m.Success) return null;
        var path = m.Groups["p"].Value;
        return File.Exists(path) ? path : null;
    }
}

public enum SmokeOutcome { Unknown, Passed, Failed, Skipped }

public sealed class SmokeResultLine
{
    public int Order { get; }
    public SmokeOutcome Outcome { get; }
    public string Description { get; }
    public DateTime CapturedAt { get; }
    /// <summary>Tag worker (ex. "kbis-vk", "kbis-xex") pour filtrer les steps par scenario
    /// quand plusieurs workers émettent simultanément vers le même proxy. Null si single-worker.</summary>
    public string? WorkerId { get; }
    public SmokeResultLine(int order, SmokeOutcome outcome, string description, DateTime capturedAt, string? workerId = null)
    {
        Order = order; Outcome = outcome; Description = description; CapturedAt = capturedAt;
        WorkerId = workerId;
    }
}
