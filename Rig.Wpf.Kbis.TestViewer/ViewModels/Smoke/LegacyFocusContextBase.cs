// SPDX-License-Identifier: Proprietary
// Base commune pour KbisFocusContext / AlertesFocusContext / DcadematFocusContext.
// Les 3 contextes sont quasi-identiques : même logique HDESK, mêmes no-ops,
// seul le catalog (type concret) et les messages de log varient.
// Ce base-class élimine ~95 % du code dupliqué.
//
// Pattern :
//   public sealed class KbisFocusContext : LegacyFocusContextBase
//   {
//       public KbisFocusContext(KbisScenarioCatalog c) : base(c, "KbisFocusContext") {}
//       // Pas de surcharge : tout vient du base.
//   }

using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// Implémentation partagée de IFocusContext pour les modules smoke legacy
/// (KBIS / ALERTES / DCADEMAT). Contient toute la logique HDESK et les no-ops.
/// Les sous-classes ne fournissent que le catalog concret et un nom pour les logs.
/// </summary>
public abstract class LegacyFocusContextBase : IFocusContext
{
    // ── Référence abstraite au catalog (pour SelectedItem + PropertyChanged) ─

    private readonly string _logContext;
    private bool _isFullscreen;
    private bool _isRenderingPaused;
    private int _snapRefreshIntervalMs = 500;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected LegacyFocusContextBase(string logContext)
    {
        _logContext = logContext;
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);

        OpenHdeskCommand = new RelayCommand(() =>
        {
            var desk = SelectedItem?.DesktopName;
            if (string.IsNullOrEmpty(desk))
            {
                Log.Warn($"{_logContext}.OpenHdeskCommand — pas de DesktopName (worker mort ou pas démarré)");
                return;
            }
            Log.Info($"{_logContext}.OpenHdeskCommand — SwitchToHDesk('{desk}')");
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
                    Log.Info($"{_logContext}.CloseHdeskCommand — taskkill /T /F /PID {pid}");
                }
                catch (System.Exception ex) { Log.Warn($"CloseHdesk taskkill: {ex.Message}"); }
            }
        });

        OpenSnapsRootCommand = new RelayCommand(() =>
            Log.Warn($"{_logContext}.OpenSnapsRootCommand — pas implémenté"));
        OpenSnapsFolderCommand = new RelayCommand(() =>
            Log.Warn($"{_logContext}.OpenSnapsFolderCommand — pas implémenté"));
        CopyLogsCommand = new RelayCommand(() =>
            Log.Warn($"{_logContext}.CopyLogsCommand — pas implémenté"));
    }

    // ── IFocusContext ────────────────────────────────────────────────────────

    public abstract IScenarioItem? SelectedItem { get; }

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

    /// <summary>Pas de run stamp pour les modules legacy (pas de batch JSON/sentinel).</summary>
    public string? CurrentRunStamp => null;

    public ICommand ToggleFullscreenCommand { get; }
    public ICommand OpenHdeskCommand { get; }
    public ICommand CloseHdeskCommand { get; }
    public ICommand OpenSnapsRootCommand { get; }
    public ICommand OpenSnapsFolderCommand { get; }
    public ICommand CopyLogsCommand { get; }

    // ── Notifications ────────────────────────────────────────────────────────

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Appelé par la sous-classe quand le catalog lève PropertyChanged sur SelectedScenario.</summary>
    protected void OnSelectedScenarioChanged() => Raise(nameof(SelectedItem));
}
