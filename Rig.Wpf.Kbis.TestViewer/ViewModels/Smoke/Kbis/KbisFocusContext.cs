// SPDX-License-Identifier: Proprietary
// SmokePage template — stub Kbis pour IFocusContext (UI shell only, 2026-05-28).
//
// État : SCAFFOLD. Identique en surface au RaptureFocusContext mais sans logique
// (toutes les commandes loguent Warn). Permet à FocusView de rendre proprement
// dans le tab KBIS Smoke E2E avec une zone PNG vide et boutons grisés.

using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Kbis;

/// <summary>
/// FocusContext KBIS — STUB UI shell. Référence le KbisScenarioCatalog pour la sélection
/// (sync avec MosaicView/ScenarioListView via SelectedScenario propagation).
/// Tous les commands sont des no-ops pour l'instant.
/// </summary>
public sealed class KbisFocusContext : IFocusContext
{
    private readonly KbisScenarioCatalog _catalog;
    private bool _isFullscreen;
    private bool _isRenderingPaused;
    private int _snapRefreshIntervalMs = 500;

    public event PropertyChangedEventHandler? PropertyChanged;

    public KbisFocusContext(KbisScenarioCatalog catalog)
    {
        _catalog = catalog;
        _catalog.PropertyChanged += OnCatalogPropertyChanged;
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);

        // ↗ Ouvrir HDESK : SwitchDesktop vers le HDESK isolé du worker sélectionné
        // (RigSmoke_<pid>). Le DesktopName de l'adapter est null si le worker est mort,
        // donc le bouton est auto-grisé hors-run. Pendant le run → switch vers le RIG live.
        // Escape via Ctrl+Alt+Backspace (agent installé par SwitchToHDesk).
        OpenHdeskCommand = new RelayCommand(() =>
        {
            var desk = SelectedItem?.DesktopName;
            if (string.IsNullOrEmpty(desk))
            {
                Log.Warn("KbisFocusContext.OpenHdeskCommand — pas de DesktopName (worker mort ou pas démarré)");
                return;
            }
            Log.Info($"KbisFocusContext.OpenHdeskCommand — SwitchToHDesk('{desk}')");
            Views.HDeskEscapeHelper.SwitchToHDesk(desk!);
        });

        // ✕ Close HDESK : ramène sur Default + taskkill /T /F le worker (libère le HDESK).
        CloseHdeskCommand = new RelayCommand(() =>
        {
            try { Views.HDeskEscapeHelper.SwitchToDefault(); }
            catch (System.Exception ex) { Log.Warn($"CloseHdesk SwitchToDefault: {ex.Message}"); }
            if (SelectedItem?.WorkerPid is int pid)
            {
                try
                {
                    using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/T /F /PID {pid}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    Log.Info($"KbisFocusContext.CloseHdeskCommand — taskkill /T /F /PID {pid}");
                }
                catch (System.Exception ex) { Log.Warn($"CloseHdesk taskkill: {ex.Message}"); }
            }
        });
    }

    public IScenarioItem? SelectedItem => _catalog.SelectedScenario;

    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set { if (_isFullscreen != value) { _isFullscreen = value; Raise(nameof(IsFullscreen)); } }
    }

    public bool IsRenderingPaused
    {
        get => _isRenderingPaused;
        set { if (_isRenderingPaused != value) { _isRenderingPaused = value; Raise(nameof(IsRenderingPaused)); } }
    }

    public int SnapRefreshIntervalMs
    {
        get => _snapRefreshIntervalMs;
        set { if (_snapRefreshIntervalMs != value) { _snapRefreshIntervalMs = value; Raise(nameof(SnapRefreshIntervalMs)); } }
    }

    public string? CurrentRunStamp => null; // pas de run KBIS pour l'instant

    public ICommand ToggleFullscreenCommand { get; }
    public ICommand OpenHdeskCommand { get; }
    public ICommand CloseHdeskCommand { get; }

    public ICommand OpenSnapsRootCommand { get; } = new RelayCommand(() =>
        Log.Warn("KbisFocusContext.OpenSnapsRootCommand — pas implémenté"));
    public ICommand OpenSnapsFolderCommand { get; } = new RelayCommand(() =>
        Log.Warn("KbisFocusContext.OpenSnapsFolderCommand — pas implémenté"));
    public ICommand CopyLogsCommand { get; } = new RelayCommand(() =>
        Log.Warn("KbisFocusContext.CopyLogsCommand — pas implémenté"));

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KbisScenarioCatalog.SelectedScenario))
            Raise(nameof(SelectedItem));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
