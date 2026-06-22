using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Rig.Wpf.Kbis.TestViewer.Views;

public partial class MosaicView : UserControl
{
    public MosaicView() => InitializeComponent();

    // La mosaïque (ListBox + UniformGrid) est imbriquée dans le ScrollViewer de la page :
    // sans ça, la molette remonte au ScrollViewer EXTERNE au lieu de scroller les tiles.
    // La barre de scroll marche déjà ; on re-route juste la molette vers le ScrollViewer
    // INTERNE de la ListBox et on marque l'event Handled pour qu'il ne bulle pas dehors.
    private void OnMosaicWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = FindDescendant<ScrollViewer>((DependencyObject)sender);
        if (sv == null) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
