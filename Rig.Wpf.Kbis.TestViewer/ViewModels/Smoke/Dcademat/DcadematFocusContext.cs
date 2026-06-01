// SPDX-License-Identifier: Proprietary
// FocusContext DCADEMAT — miroir d'AlertesFocusContext (2026-05-29 soir).
// Zone PNG centrale (live snap) + boutons Plein écran / Ouvrir HDESK / Close HDESK.

using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Dcademat;

public sealed class DcadematFocusContext : IFocusContext
{
    private readonly DcadematScenarioCatalog _catalog;
    private bool _isFullscreen;
    private bool _isRenderingPaused;
    private int _snapRefreshIntervalMs = 500;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DcadematFocusContext(DcadematScenarioCatalog catalog)
    {
        _catalog = catalog;
        _catalog.PropertyChanged += OnCatalogPropertyChanged;
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);

        OpenHdeskCommand = new RelayCommand(() =>
        {
            var desk = SelectedItem?.DesktopName;
            if (string.IsNullOrEmpty(desk))
            {
                Log.Warn("DcadematFocusContext.OpenHdeskCommand — pas de DesktopName (worker mort ou pas démarré)");
                return;
            }
            Log.Info($"DcadematFocusContext.OpenHdeskCommand — SwitchToHDesk('{desk}')");
            Views.HDeskEscapeHelper.SwitchToHDesk(desk!);
        });

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
                    Log.Info($"DcadematFocusContext.CloseHdeskCommand — taskkill /T /F /PID {pid}");
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

    public string? CurrentRunStamp => null;

    public ICommand ToggleFullscreenCommand { get; }
    public ICommand OpenHdeskCommand { get; }
    public ICommand CloseHdeskCommand { get; }

    public ICommand OpenSnapsRootCommand { get; } = new RelayCommand(() =>
        Log.Warn("DcadematFocusContext.OpenSnapsRootCommand — pas implémenté"));
    public ICommand OpenSnapsFolderCommand { get; } = new RelayCommand(() =>
        Log.Warn("DcadematFocusContext.OpenSnapsFolderCommand — pas implémenté"));
    public ICommand CopyLogsCommand { get; } = new RelayCommand(() =>
        Log.Warn("DcadematFocusContext.CopyLogsCommand — pas implémenté"));

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DcadematScenarioCatalog.SelectedScenario))
            Raise(nameof(SelectedItem));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
