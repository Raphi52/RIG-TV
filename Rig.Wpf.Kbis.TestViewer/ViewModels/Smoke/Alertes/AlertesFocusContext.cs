// SPDX-License-Identifier: Proprietary
// FocusContext ALERTES RCS — délègue la logique commune à LegacyFocusContextBase.

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Alertes;

/// <summary>
/// FocusContext Alertes RCS — zone PNG centrale (live snap) + boutons Plein écran / Ouvrir HDESK / Close HDESK.
/// Logique commune (HDESK, no-ops, IsFullscreen) fournie par LegacyFocusContextBase.
/// </summary>
public sealed class AlertesFocusContext : LegacyFocusContextBase
{
    private readonly AlertesScenarioCatalog _catalog;

    public AlertesFocusContext(AlertesScenarioCatalog catalog) : base("AlertesFocusContext")
    {
        _catalog = catalog;
        _catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AlertesScenarioCatalog.SelectedScenario))
                OnSelectedScenarioChanged();
        };
    }

    public override IScenarioItem? SelectedItem => _catalog.SelectedScenario;
}
