using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

public sealed partial class ScenarioViewModel : ObservableObject
{
    public ScenarioViewModel(SmokeScenario scenario)
    {
        Scenario = scenario;
        Tags = DeriveTags(scenario);
    }

    public SmokeScenario Scenario { get; }
    public string Title       => Scenario.Title;
    public string Category    => Scenario.Category;
    public string Description => Scenario.Description;
    public IReadOnlyList<string> Tags { get; }

    private static IReadOnlyList<string> DeriveTags(SmokeScenario s)
    {
        // Backend déclaré dans le scénario : "wpf" (default) ou "legacy" → tag stack.
        var tags = new List<string>
        {
            string.Equals(s.Backend, "legacy", StringComparison.OrdinalIgnoreCase)
                ? TestTags.Legacy
                : TestTags.Wpf
        };
        // UI flow = e2e ; reste = smoke pur.
        if (string.Equals(s.Category, "UI", StringComparison.OrdinalIgnoreCase))
            tags.Add(TestTags.E2E);
        else
            tags.Add(TestTags.Smoke);
        // Tags explicites supplémentaires (ex. "kbis" pour les scénarios fonctionnels).
        foreach (var extra in s.ExtraTags)
            if (!tags.Contains(extra, StringComparer.Ordinal))
                tags.Add(extra);
        return tags;
    }

    [ObservableProperty] private SmokeResultLine? matchedLine;

    /// <summary>Update from the latest set of lines from SmokeRunnerProxy.</summary>
    public void RefreshFrom(System.Collections.Generic.IEnumerable<SmokeResultLine> lines)
    {
        MatchedLine = SmokeCatalog.Match(Scenario, lines);
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(ResultMessage));
    }

    public string StatusIcon => MatchedLine?.Outcome switch
    {
        SmokeOutcome.Passed  => "✓",
        SmokeOutcome.Failed  => "✗",
        SmokeOutcome.Skipped => "⊘",
        _                    => "—",
    };

    public string StatusBrushKey => MatchedLine?.Outcome switch
    {
        SmokeOutcome.Passed  => "OkColor",
        SmokeOutcome.Failed  => "FailColor",
        SmokeOutcome.Skipped => "SkipColor",
        _                    => "UnknownColor",
    };

    public string ResultMessage => MatchedLine?.Description ?? "—";
}
