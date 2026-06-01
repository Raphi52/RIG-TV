using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

/// <summary>
/// State par scénario. ObservableObject => binding WPF direct (DataGrid row, mosaic tile, focus).
/// Rolling logs buffer cappé à 500 lignes pour éviter l'explosion mémoire sur batch long.
///
/// Implémente <see cref="IScenarioItem"/> pour s'intégrer dans le template SmokePage
/// (MosaicView/FocusView/ScenarioListView consomment IScenarioItem, pas la classe concrète).
/// Toutes les props de l'interface sont déjà présentes — l'implémentation est implicite.
/// </summary>
public sealed partial class ScenarioRunState : ObservableObject, IScenarioItem
{
    private const int LogsTailCap = 500;
    private readonly Queue<string> _logsBuffer = new Queue<string>(LogsTailCap);

    // set au lieu de init — net48 manque IsExternalInit dans ce projet.
    public string Id { get; set; } = "";
    public string JsonPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortKey))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private ScenarioPhase phase = ScenarioPhase.Queued;

    /// <summary>Sort key pour ICollectionView. Ordre voulu : actifs en HAUT, queued au MILIEU, terminés à la FIN.
    ///   0 = Launching / Login / OpenProcRetaud / SelectAudience / ClickImporter / Recap / Apply (= actifs live)
    ///   50 = Queued (pas encore démarrés mais à venir, à garder visible)
    ///   99 = Done / Fail (terminés, descendus tout en bas pour ne pas distraire pendant que le batch tourne)
    /// Re-trigger via LiveSorting sur "SortKey" à chaque transition de Phase.</summary>
    public int SortKey => Phase switch
    {
        ScenarioPhase.Done => 99,
        ScenarioPhase.Fail => 99,
        ScenarioPhase.Queued => 50,
        _ => 0
    };

    /// <summary>True si un worker est en cours pour ce scénario (verdict pas encore tombé).
    /// Drives CanStop=true / CanPlay=false → cards mosaïque montrent ⏹ Stop.</summary>
    public bool IsRunning => Verdict == Verdict.Pending && Phase != ScenarioPhase.Queued;

    /// <summary>Play visible/enabled quand le scénario n'est pas en train de tourner
    /// (état initial Queued OU re-run après Done/Fail).</summary>
    public bool CanPlay => !IsRunning;

    /// <summary>Stop visible/enabled uniquement pendant l'exécution active.</summary>
    public bool CanStop => IsRunning;

    /// <summary>Pas de steps internes exposés pour Rapture (les logs sont dans LogsTail).
    /// KbisLegacyScenarioAdapter override pour filtrer les SmokeResultLine par workerId.</summary>
    public System.Collections.Generic.IReadOnlyList<Smoke.StepDisplay> Steps
        => System.Array.Empty<Smoke.StepDisplay>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private Verdict verdict = Verdict.Pending;
    [ObservableProperty] private int? workerPid;
    [ObservableProperty] private string? desktopName;
    [ObservableProperty] private DateTime? startedAt;
    [ObservableProperty] private string logsTail = "";
    [ObservableProperty] private string? lastSnapPath;
    [ObservableProperty] private string? failScreenshotPath;

    public TimeSpan Duration => StartedAt.HasValue ? DateTime.UtcNow - StartedAt.Value : TimeSpan.Zero;

    /// <summary>Durée formatée mm:ss directement consommable par les bindings WPF (banner verdict).
    /// Le StringFormat avec backslash escape `mm\:ss` parse mal dans MultiBinding XAML, d'où le
    /// précompute côté VM. Si scénario pas encore démarré → "—:—".</summary>
    public string DurationFormatted => StartedAt.HasValue
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int)Duration.TotalMinutes, Duration.Seconds)
        : "—:—";

    public void MarkStarted(int workerPid, string desktopName, DateTime startedAt)
    {
        WorkerPid = workerPid;
        DesktopName = desktopName;
        StartedAt = startedAt;
        Phase = ScenarioPhase.Launching;
    }

    public void MarkFinished(Verdict verdict)
    {
        Verdict = verdict;
        Phase = verdict == Verdict.Fail ? ScenarioPhase.Fail : ScenarioPhase.Done;
    }

    public void AppendLog(string line)
    {
        if (_logsBuffer.Count >= LogsTailCap) _logsBuffer.Dequeue();
        _logsBuffer.Enqueue(line);
        LogsTail = string.Join("\n", _logsBuffer);
    }

    /// <summary>Reset au state initial (Queued, pas de worker, pas de logs, pas de snap).
    /// Utilisé : (1) pre-population au startup depuis le catalog, (2) re-run d'un scénario
    /// déjà completé via le Play card mosaïque, (3) restart batch (Clear+Add remplacé par reset).</summary>
    public void Reset()
    {
        _logsBuffer.Clear();
        LogsTail = "";
        Phase = ScenarioPhase.Queued;
        Verdict = Verdict.Pending;
        WorkerPid = null;
        StartedAt = null;
        LastSnapPath = null;
        FailScreenshotPath = null;
    }
}
