using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

public enum BatchPhase { Idle, Running, Paused, Done }

public sealed partial class BatchRunState : ObservableObject
{
    public ObservableCollection<ScenarioRunState> Scenarios { get; } = new ObservableCollection<ScenarioRunState>();

    [ObservableProperty] private ScenarioRunState? selectedScenario;
    [ObservableProperty] private BatchPhase phase = BatchPhase.Idle;
    [ObservableProperty] private int passCount;
    [ObservableProperty] private int failCount;
    [ObservableProperty] private int queuedCount;
    [ObservableProperty] private int runningCount;

    public int TotalCount => Scenarios.Count;

    /// <summary>
    /// Recompute les counters depuis Scenarios. À appeler après transition de phase d'un ScenarioRunState.
    /// </summary>
    public void RefreshCounts()
    {
        PassCount = Scenarios.Count(s => s.Verdict == Verdict.Pass);
        FailCount = Scenarios.Count(s => s.Verdict == Verdict.Fail);
        QueuedCount = Scenarios.Count(s => s.Phase == ScenarioPhase.Queued);
        RunningCount = Scenarios.Count(s => s.Verdict == Verdict.Pending && s.Phase != ScenarioPhase.Queued);
    }
}
