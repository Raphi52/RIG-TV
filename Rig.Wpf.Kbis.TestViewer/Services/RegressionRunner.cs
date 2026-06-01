using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Lance <c>dotnet test --no-build --logger trx</c> sur le projet xUnit
/// <c>Rig.Kbis.RegressionTests</c> et parse le TRX pour remonter outcome+durée
/// par test. Notifie l'UI via <see cref="ResultsUpdated"/>.
///
/// Port direct de <c>TestRunnerService</c> du Blazor TestViewer.
/// </summary>
public sealed class RegressionRunner
{
    private readonly string _projectDir;
    private readonly object _lock = new();
    private readonly Dictionary<string, RegressionResult> _results = new(StringComparer.Ordinal);

    /// <summary>
    /// Dossier d'archivage des TRX, aligné sur le pattern de <see cref="Log"/> :
    /// <c>%LocalAppData%\rig-wpf-testviewer\trx-history\</c>. Chaque run y dépose
    /// un TRX horodaté (<c>&lt;projet&gt;-yyyyMMdd-HHmmss.trx</c>) au lieu de le
    /// laisser mourir en fichier GUID orphelin sous <c>TestResults\</c>. Ces TRX
    /// sont au format natif Azure DevOps (PublishTestResults les lit tels quels).
    /// </summary>
    private static readonly string TrxHistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rig-wpf-testviewer", "trx-history");

    /// <summary>Nombre de TRX conservés par projet (rotation FIFO sur l'âge).</summary>
    private const int TrxHistoryMax = 50;

    /// <summary>Constructeur historique : projet xUnit pointé par <see cref="TestingPaths.RegressionProjectDir"/>.</summary>
    public RegressionRunner(TestingPaths paths) : this(paths.RegressionProjectDir) { }

    /// <summary>Constructeur multi-projet : on peut instancier N runners pour N projets xUnit (KBIS, Rapture, …).</summary>
    public RegressionRunner(string projectDir) { _projectDir = projectDir; }

    public event Action? ResultsUpdated;
    public event Action? StateChanged;

    /// <summary>
    /// Émis dès qu'une nouvelle ligne stdout arrive du process dotnet test —
    /// permet à la console B2 de streamer en live (au lieu d'attendre la fin du
    /// run pour ré-afficher tout d'un coup).
    /// </summary>
    public event Action? LiveStdoutChanged;

    /// <summary>
    /// Émis par <see cref="RunCoreAsync"/> quand une ligne dotnet test décrit
    /// l'outcome d'un test individuel (pattern <c>Passed/Failed/Skipped FullName [Xms]</c>).
    /// Permet à l'UI de flipper la checkmark du <c>RegressionItemViewModel</c>
    /// correspondant en temps réel, sans attendre le parsing du TRX final.
    /// </summary>
    public event Action<TestProgress>? TestProgress;

    public IReadOnlyDictionary<string, RegressionResult> Results
    {
        get { lock (_lock) { return _results.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); } }
    }

    public bool IsRunning { get; private set; }
    public string? LastFullStdout { get; private set; }
    public string? LastStderr { get; private set; }
    public int? LastExitCode { get; private set; }
    public TimeSpan? LastRunDuration { get; private set; }

    /// <summary>Chemin du dernier TRX archivé sous <see cref="TrxHistoryDir"/> (null si l'archivage a échoué).</summary>
    public string? LastTrxArchivePath { get; private set; }

    public bool ProjectExists => Directory.Exists(_projectDir);
    public string ProjectDir  => _projectDir;

    /// <summary>
    /// Lance dotnet test. <paramref name="filter"/> peut être null (run all) ou
    /// "DisplayName~<method>" pour une méthode unique.
    /// </summary>
    public async Task RunAsync(string? filter = null, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Regression run déjà en cours");
        if (!ProjectExists)
            throw new DirectoryNotFoundException("Projet xUnit introuvable : " + _projectDir);

        IsRunning = true;
        StateChanged?.Invoke();
        var sw = Stopwatch.StartNew();
        try
        {
            await RunCoreAsync(filter, ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            LastRunDuration = sw.Elapsed;
            IsRunning = false;
            StateChanged?.Invoke();
        }
    }

    private async Task RunCoreAsync(string? filter, CancellationToken ct)
    {
        var trxName = $"viewer-{Guid.NewGuid():N}.trx";
        var trxPath = Path.Combine(_projectDir, "TestResults", trxName);

        // --verbosity normal (au lieu de quiet) : le test runner émet une ligne par
        // test avec son outcome ("Passed   ClassName.MethodName [X ms]" / "Failed …"),
        // ce qui permet le parsing live dans OnLine + le streaming de la console B2.
        var arguments = $"test --no-build --nologo --logger \"trx;LogFileName={trxName}\" --verbosity normal";
        if (!string.IsNullOrEmpty(filter)) arguments += $" --filter \"{filter}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = _projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // dotnet test sur .NET 8 émet en UTF-8 ; sans cette ligne le StreamReader
            // par défaut décode en OEM codepage → mojibake "SÃ©rie" au lieu de "Série".
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start a renvoyé null pour dotnet test");

        // Stream stdout/stderr line-by-line au lieu de ReadToEndAsync :
        //   1. La console B2 voit le run progresser (LiveStdoutChanged à chaque ligne).
        //   2. Le parser per-line détecte "Passed/Failed/Skipped <FullName>" et émet
        //      un TestProgress event → l'UI flippe les checkmarks live test par test.
        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdoutBuf) stdoutBuf.AppendLine(e.Data);
            LastFullStdout = stdoutBuf.ToString();
            LiveStdoutChanged?.Invoke();
            TryParseAndFireTestProgress(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderrBuf) stderrBuf.AppendLine(e.Data);
            LastStderr = stderrBuf.ToString();
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await Task.Run(p.WaitForExit, ct).ConfigureAwait(false);

        // WaitForExit ne garantit pas que les buffers Output/ErrorDataReceived soient
        // drainés — explicite WaitForExit() sans timeout vidange l'EventQueue.
        p.WaitForExit();

        LastExitCode = p.ExitCode;
        LastFullStdout = stdoutBuf.ToString();
        LastStderr = stderrBuf.ToString();

        if (File.Exists(trxPath))
        {
            ParseTrx(trxPath);
            ArchiveTrx(trxPath);
        }
        ResultsUpdated?.Invoke();
    }

    /// <summary>
    /// Pattern dotnet test --verbosity normal : matche EN <c>Passed/Failed/Skipped</c>
    /// ET FR <c>Réussi/Échec/Ignoré</c> (localisé selon culture .NET du runner). Le
    /// FullName est le xUnit "fully qualified name" — typiquement
    /// <c>Namespace.ClassName.MethodName(theory_params...)</c>.
    /// </summary>
    private static readonly Regex TestOutcomeLine = new(
        @"^\s*(?<o>Passed|Failed|Skipped|Réussi|Échec|Echec|Ignoré|Ignore)\s+(?<n>.+?)\s*(?:\[(?<d>[^\]]+)\])?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Normalise un outcome localisé vers l'anglais canonique
    /// (Passed/Failed/Skipped). Indispensable car <see cref="RegressionResult.Passed"/>
    /// compare en Ordinal contre <c>"Passed"</c>.
    /// </summary>
    private static string NormalizeOutcome(string raw)
    {
        if (string.Equals(raw, "Réussi", StringComparison.OrdinalIgnoreCase)) return "Passed";
        if (string.Equals(raw, "Échec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "Echec", StringComparison.OrdinalIgnoreCase)) return "Failed";
        if (string.Equals(raw, "Ignoré", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "Ignore", StringComparison.OrdinalIgnoreCase)) return "Skipped";
        return raw; // déjà en anglais
    }

    /// <summary>
    /// Parse <paramref name="line"/> et émet <see cref="TestProgress"/> si elle décrit
    /// l'outcome d'un test individuel. Best-effort — toute ligne non-matchée est ignorée.
    /// </summary>
    private void TryParseAndFireTestProgress(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        // Bypass rapide : skip les lignes qui ne ressemblent pas à un verdict (EN + FR).
        if (line.IndexOf("Passed", StringComparison.Ordinal) < 0
            && line.IndexOf("Failed", StringComparison.Ordinal) < 0
            && line.IndexOf("Skipped", StringComparison.Ordinal) < 0
            && line.IndexOf("Réussi", StringComparison.Ordinal) < 0
            && line.IndexOf("Échec", StringComparison.Ordinal) < 0
            && line.IndexOf("Echec", StringComparison.Ordinal) < 0
            && line.IndexOf("Ignor", StringComparison.Ordinal) < 0) return;
        var m = TestOutcomeLine.Match(line);
        if (!m.Success) return;
        var outcome = NormalizeOutcome(m.Groups["o"].Value);
        var fullName = m.Groups["n"].Value;
        TimeSpan? duration = null;
        var dur = m.Groups["d"].Value;
        if (!string.IsNullOrEmpty(dur))
        {
            // Format usuel : "12 ms", "1 s", "< 1 ms"
            var trim = dur.TrimStart('<', ' ').Trim();
            if (trim.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(trim.Substring(0, trim.Length - 2).Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
                    duration = TimeSpan.FromMilliseconds(ms);
            }
            else if (trim.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(trim.Substring(0, trim.Length - 1).Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
                    duration = TimeSpan.FromSeconds(s);
            }
        }
        // Met à jour le _results dict en live aussi — pour que les VM qui réinterrogent
        // Results pendant le run voient la progression incrémentale.
        lock (_lock)
        {
            _results[fullName] = new RegressionResult(
                Outcome: outcome,
                Duration: duration,
                EndTime: DateTime.Now,
                ErrorMessage: null);
        }
        TestProgress?.Invoke(new TestProgress(fullName, outcome, duration));
    }

    /// <summary>
    /// Copie le TRX dans <see cref="TrxHistoryDir"/> sous un nom horodaté lisible,
    /// supprime le fichier GUID temporaire, puis applique la rotation. Best-effort :
    /// un échec d'archivage ne doit jamais faire échouer le run de tests.
    /// </summary>
    private void ArchiveTrx(string trxPath)
    {
        try
        {
            Directory.CreateDirectory(TrxHistoryDir);
            var label = SafeLabel(new DirectoryInfo(_projectDir).Name);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dest  = Path.Combine(TrxHistoryDir, $"{label}-{stamp}.trx");
            for (int n = 1; File.Exists(dest); n++)
                dest = Path.Combine(TrxHistoryDir, $"{label}-{stamp}-{n}.trx");

            File.Copy(trxPath, dest, overwrite: false);
            LastTrxArchivePath = dest;
            Log.Info($"TRX archivé : {dest}");

            try { File.Delete(trxPath); }
            catch { /* fichier GUID orphelin sous TestResults\, suppression best-effort */ }

            RotateTrxHistory(label);
        }
        catch (Exception ex)
        {
            LastTrxArchivePath = null;
            Log.Warn("Échec archivage TRX : " + ex.Message);
        }
    }

    /// <summary>Garde au plus <see cref="TrxHistoryMax"/> TRX par projet (les plus récents).</summary>
    private static void RotateTrxHistory(string label)
    {
        try
        {
            var stale = new DirectoryInfo(TrxHistoryDir)
                .GetFiles(label + "-*.trx")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(TrxHistoryMax)
                .ToList();
            foreach (var f in stale)
            {
                try { f.Delete(); } catch { /* rotation best-effort */ }
            }
        }
        catch { /* rotation best-effort */ }
    }

    /// <summary>Nettoie le nom de projet pour en faire un préfixe de fichier sûr.</summary>
    private static string SafeLabel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "regression";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(raw.Where(c => Array.IndexOf(invalid, c) < 0).ToArray()).Trim();
        return clean.Length == 0 ? "regression" : clean;
    }

    private void ParseTrx(string trxPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(trxPath); }
        catch { return; }

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        lock (_lock)
        {
            _results.Clear();
            foreach (var r in doc.Descendants(ns + "UnitTestResult"))
            {
                var name     = r.Attribute("testName")?.Value ?? "";
                var outcome  = r.Attribute("outcome")?.Value ?? "Unknown";
                var duration = r.Attribute("duration")?.Value ?? "";
                var endTime  = r.Attribute("endTime")?.Value ?? "";
                var message  = r.Descendants(ns + "Message").FirstOrDefault()?.Value;
                _results[name] = new RegressionResult(
                    Outcome: outcome,
                    Duration: TimeSpan.TryParse(duration, out var ts) ? ts : (TimeSpan?)null,
                    EndTime: DateTime.TryParse(endTime, out var dt) ? dt : (DateTime?)null,
                    ErrorMessage: message);
            }
        }
    }
}

/// <summary>
/// Outcome live d'un test individuel, émis par <see cref="RegressionRunner.TestProgress"/>
/// au fur et à mesure que le runner xUnit avance. <see cref="FullName"/> est le nom
/// canonique du test (Namespace.Class.Method[(theory-params)]).
/// </summary>
public sealed class TestProgress
{
    public string FullName { get; }
    public string Outcome { get; }
    public TimeSpan? Duration { get; }
    public TestProgress(string fullName, string outcome, TimeSpan? duration)
    {
        FullName = fullName;
        Outcome = outcome;
        Duration = duration;
    }
    public bool Passed => string.Equals(Outcome, "Passed", StringComparison.OrdinalIgnoreCase);
    public bool Failed => string.Equals(Outcome, "Failed", StringComparison.OrdinalIgnoreCase);
}

public sealed class RegressionResult
{
    public string Outcome { get; }
    public TimeSpan? Duration { get; }
    public DateTime? EndTime { get; }
    public string? ErrorMessage { get; }
    public RegressionResult(string Outcome, TimeSpan? Duration, DateTime? EndTime, string? ErrorMessage)
    {
        this.Outcome = Outcome;
        this.Duration = Duration;
        this.EndTime = EndTime;
        this.ErrorMessage = ErrorMessage;
    }
    public bool Passed => string.Equals(Outcome, "Passed", StringComparison.OrdinalIgnoreCase);
    public bool Failed => string.Equals(Outcome, "Failed", StringComparison.OrdinalIgnoreCase);
}
