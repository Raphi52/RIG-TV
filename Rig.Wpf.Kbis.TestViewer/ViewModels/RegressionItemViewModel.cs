using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

public sealed partial class RegressionItemViewModel : ObservableObject
{
    private readonly RegressionRunner _runner;
    private readonly Action<string, string>? _jumpToScenario;

    public RegressionItemViewModel(RegressionTestInfo info, RegressionRunner runner,
        Action<string, string>? jumpToScenario = null)
    {
        Info = info;
        _runner = runner;
        _jumpToScenario = jumpToScenario;
        Tags = new System.Collections.Generic.List<string>(DeriveInitialTags(info));
    }

    /// <summary>
    /// Tags du test. Mutable (List) parce que <c>transition</c> est ajouté
    /// après coup par MainVM quand il détecte qu'une paire legacy+api existe.
    /// </summary>
    public System.Collections.Generic.List<string> Tags { get; }

    private static System.Collections.Generic.IEnumerable<string> DeriveInitialTags(RegressionTestInfo info)
    {
        // Backend "legacy" → tag legacy. "native" → tag api (la "native" est la nouvelle API HTTP).
        if (string.Equals(info.Backend, "legacy", StringComparison.OrdinalIgnoreCase))
            yield return TestTags.Legacy;
        else if (string.Equals(info.Backend, "native", StringComparison.OrdinalIgnoreCase))
            yield return TestTags.Api;
    }

    public void EnsureTag(string tag)
    {
        if (!Tags.Contains(tag, StringComparer.Ordinal)) Tags.Add(tag);
    }

    public RegressionTestInfo Info { get; }
    public string DisplayName => Info.DisplayName;
    public string MethodName  => Info.MethodName;
    public string ClassName   => Info.ClassName;
    public string Category    => Info.Category;
    public string Description => Info.Description;
    public string? Backend    => Info.Backend;

    [ObservableProperty] private RegressionResult? lastResult;

    public string StatusIcon => LastResult switch
    {
        { Passed: true } => "✓",
        { Failed: true } => "✗",
        _                => "—",
    };

    public string StatusBrushKey => LastResult switch
    {
        { Passed: true } => "OkColor",
        { Failed: true } => "FailColor",
        _                => "UnknownColor",
    };

    public string DurationLabel => LastResult?.Duration is { } d
        ? $"{d.TotalMilliseconds:F0} ms"
        : "";

    public string BackendBadgeKey => Backend switch
    {
        "legacy" => "BadgeLegacy",
        "native" => "BadgeNative",
        _        => "BadgeNeutral",
    };

    public void RefreshFromRunner()
    {
        LastResult = _runner.Results.TryGetValue(Info.FullyQualifiedName, out var r) ? r : null;
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(DurationLabel));
    }

    [RelayCommand]
    private async Task RunSingleAsync()
    {
        try { await _runner.RunAsync("DisplayName~" + Info.DisplayName); }
        catch (Exception) { /* erreurs remontées via stderr / lastResult null */ }
    }

    /// <summary>Saute à l'onglet Scénarios sur le 1er .verified.json correspondant.</summary>
    [RelayCommand(CanExecute = nameof(CanJumpToScenario))]
    private void JumpToScenario()
    {
        _jumpToScenario?.Invoke(ClassName, MethodName);
    }
    private bool CanJumpToScenario() => _jumpToScenario is not null;
}
