// SPDX-License-Identifier: Proprietary
// FocusContext DCADEMAT — délègue la logique commune à LegacyFocusContextBase.

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Dcademat;

/// <summary>
/// FocusContext DCADEMAT — zone PNG centrale (live snap) + boutons Plein écran / Ouvrir HDESK / Close HDESK.
/// Logique commune (HDESK, no-ops, IsFullscreen) fournie par LegacyFocusContextBase.
/// </summary>
public sealed class DcadematFocusContext : LegacyFocusContextBase
{
    private readonly DcadematScenarioCatalog _catalog;

    public DcadematFocusContext(DcadematScenarioCatalog catalog) : base("DcadematFocusContext")
    {
        _catalog = catalog;
        _catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DcadematScenarioCatalog.SelectedScenario))
                OnSelectedScenarioChanged();
        };
    }

    public override IScenarioItem? SelectedItem => _catalog.SelectedScenario;
}
