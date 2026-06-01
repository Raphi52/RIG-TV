using System.Windows.Controls;

namespace Rig.Wpf.Kbis.TestViewer.Views;

/// <summary>
/// BatchControlsView — Card header de SmokePageView. Extraction Phase 6.
/// DataContext attendu = MainWindowViewModel (bindings directs Rapture* pour l'instant ;
/// futur refactor IBatchControls cf. roadmap SmokePage template).
/// </summary>
public partial class BatchControlsView : UserControl
{
    public BatchControlsView()
    {
        InitializeComponent();
    }
}
