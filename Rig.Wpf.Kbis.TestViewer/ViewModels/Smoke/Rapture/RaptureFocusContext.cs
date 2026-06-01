// SPDX-License-Identifier: Proprietary
// SmokePage template — adapter Rapture pour IFocusContext (Phase 3).

using System.ComponentModel;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Rapture;

/// <summary>
/// Implémente <see cref="IFocusContext"/> en wrappant les props/commands existants
/// de <see cref="MainWindowViewModel"/>. Aucune logique métier déplacée — juste un
/// mapping renaming :
///   MainWindowViewModel.FocusIsFullscreen        → IFocusContext.IsFullscreen
///   MainWindowViewModel.SnapRenderingPaused      → IFocusContext.IsRenderingPaused
///   MainWindowViewModel.ToggleFocusFullscreen    → IFocusContext.ToggleFullscreenCommand
///   etc.
///   BatchState.SelectedScenario (ScenarioRunState) → IFocusContext.SelectedItem (IScenarioItem)
///
/// Subscribe à 2 PropertyChanged sources :
///   1. MainWindowViewModel pour FocusIsFullscreen, SnapRefreshIntervalMs, SnapRenderingPaused, CurrentRunStamp
///   2. BatchRunState pour SelectedScenario
/// Et re-raise sur l'équivalent IFocusContext pour que les bindings WPF réagissent.
/// </summary>
public sealed class RaptureFocusContext : IFocusContext
{
    private readonly MainWindowViewModel _vm;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RaptureFocusContext(MainWindowViewModel vm)
    {
        _vm = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.BatchState.PropertyChanged += OnBatchStatePropertyChanged;
    }

    // ── Forwarding propriétés ───────────────────────────────────────────────

    public IScenarioItem? SelectedItem => _vm.BatchState.SelectedScenario;

    public bool IsFullscreen => _vm.FocusIsFullscreen;

    public bool IsRenderingPaused
    {
        get => _vm.SnapRenderingPaused;
        set => _vm.SnapRenderingPaused = value;
    }

    public int SnapRefreshIntervalMs
    {
        get => _vm.SnapRefreshIntervalMs;
        set => _vm.SnapRefreshIntervalMs = value;
    }

    public string? CurrentRunStamp => _vm.CurrentRunStamp;

    // ── Forwarding commandes ────────────────────────────────────────────────

    public ICommand ToggleFullscreenCommand => _vm.ToggleFocusFullscreenCommand;
    public ICommand OpenHdeskCommand        => _vm.OpenHdeskFromFocusCommand;
    public ICommand CloseHdeskCommand       => _vm.CloseHdeskFromFocusCommand;
    public ICommand OpenSnapsRootCommand    => _vm.OpenSnapsRootCommand;
    public ICommand OpenSnapsFolderCommand  => _vm.OpenSnapsFolderCommand;
    public ICommand CopyLogsCommand         => _vm.CopyFocusLogsCommand;

    // ── Pont PropertyChanged ────────────────────────────────────────────────

    /// <summary>
    /// Re-raise sur l'IFocusContext quand MainWindowViewModel raise sur ses props
    /// internes. Map nom → nom. Si non mappé, ignore.
    /// </summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? mapped = e.PropertyName switch
        {
            nameof(MainWindowViewModel.FocusIsFullscreen)    => nameof(IsFullscreen),
            nameof(MainWindowViewModel.SnapRefreshIntervalMs) => nameof(SnapRefreshIntervalMs),
            nameof(MainWindowViewModel.SnapRenderingPaused)   => nameof(IsRenderingPaused),
            nameof(MainWindowViewModel.CurrentRunStamp)       => nameof(CurrentRunStamp),
            _ => null,
        };
        if (mapped != null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(mapped));
    }

    /// <summary>
    /// Re-raise IFocusContext.SelectedItem quand BatchRunState.SelectedScenario change
    /// (click sur tile mosaïque ou ligne grid).
    /// </summary>
    private void OnBatchStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchRunState.SelectedScenario))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));
    }
}
