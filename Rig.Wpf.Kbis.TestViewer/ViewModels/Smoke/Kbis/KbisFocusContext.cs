// SPDX-License-Identifier: Proprietary
// FocusContext KBIS — délègue la logique commune à LegacyFocusContextBase.
// Seul le catalog concret et le nom de log varient par rapport à Alertes/Dcademat.

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Kbis;

/// <summary>
/// FocusContext KBIS — zone PNG centrale (live snap) + boutons Plein écran / Ouvrir HDESK / Close HDESK.
/// Logique commune (HDESK, no-ops, IsFullscreen) fournie par LegacyFocusContextBase.
/// </summary>
public sealed class KbisFocusContext : LegacyFocusContextBase
{
    private readonly KbisScenarioCatalog _catalog;

    public KbisFocusContext(KbisScenarioCatalog catalog) : base("KbisFocusContext")
    {
        _catalog = catalog;
        _catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KbisScenarioCatalog.SelectedScenario))
                OnSelectedScenarioChanged();
        };
    }

    public override IScenarioItem? SelectedItem => _catalog.SelectedScenario;
}
