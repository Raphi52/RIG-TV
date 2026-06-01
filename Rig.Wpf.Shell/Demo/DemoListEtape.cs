using System.Collections.Generic;
using System.Collections.ObjectModel;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Shell.Demo;

/// <summary>
/// Étape Recherche d'un module de démo : DataGrid + filtre simple.
/// IsValid = true dès qu'une ligne est sélectionnée (permet le Go Next).
/// </summary>
public sealed class DemoListEtape : EtapeViewModelBase
{
    private string _filtre = "";
    private DemoRow? _selection;

    public DemoListEtape(IReadOnlyList<DemoColumn> columns, IReadOnlyList<DemoRow> rows)
        : base("LIST", "Recherche")
    {
        Columns = columns;
        Rows = new ObservableCollection<DemoRow>(rows);
    }

    public IReadOnlyList<DemoColumn> Columns { get; }
    public ObservableCollection<DemoRow> Rows { get; }

    public string Filtre
    {
        get => _filtre;
        set => SetProperty(ref _filtre, value);
    }

    public DemoRow? Selection
    {
        get => _selection;
        set
        {
            if (SetProperty(ref _selection, value))
                SetIsValid(value is not null);
        }
    }
}
