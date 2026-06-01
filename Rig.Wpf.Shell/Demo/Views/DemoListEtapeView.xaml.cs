using System.Windows.Controls;
using System.Windows.Data;

namespace Rig.Wpf.Shell.Demo.Views;

public partial class DemoListEtapeView : UserControl
{
    public DemoListEtapeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildColumns();
        Loaded += (_, _) => BuildColumns();
    }

    private void BuildColumns()
    {
        if (DataContext is not DemoListEtape vm) return;

        // Garde la première colonne (pastille de statut) puis rajoute dynamiquement
        // les colonnes de la config.
        while (Grid.Columns.Count > 1) Grid.Columns.RemoveAt(1);

        foreach (var col in vm.Columns)
        {
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Header,
                Width = new DataGridLength(col.Width),
                Binding = new Binding($"Cells[{col.PropertyKey}]"),
            });
        }
    }
}
