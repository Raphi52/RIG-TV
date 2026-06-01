using System.Windows.Controls;

namespace Rig.Wpf.Kbis.TestViewer.Views;

/// <summary>
/// Phase 5 — drawer Historique : liste les 10 derniers runs de self-snaps avec
/// boutons Snaps (ouvre Explorer) et Rejouer (v2). UserControl 320px de large,
/// hosté dans MainWindow.xaml en overlay slide-in depuis la droite du tab Smoke Import.
/// </summary>
public partial class RunHistoryDrawer : UserControl
{
    public RunHistoryDrawer() => InitializeComponent();
}
