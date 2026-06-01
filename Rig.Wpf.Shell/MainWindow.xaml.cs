using System.Linq;
using System.Windows;
using System.Windows.Forms;

namespace Rig.Wpf.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // WindowStartupLocation=Manual + Left/Top explicites : on ne fait PAS
        // confiance à CenterScreen qui calcule depuis le VirtualScreen incluant
        // les écrans fantômes mémorisés (régression typique quand un 2e écran
        // a été débranché entre deux sessions).
        PlaceOnPrimaryScreen();
    }

    private void PlaceOnPrimaryScreen()
    {
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        if (primary is null) return;
        var wa = primary.WorkingArea;
        Left = wa.Left + (wa.Width - Width) / 2.0;
        Top  = wa.Top  + (wa.Height - Height) / 2.0;
    }
}
