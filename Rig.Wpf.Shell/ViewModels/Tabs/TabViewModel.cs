using Rig.Wpf.Mvvm;

namespace Rig.Wpf.Shell.ViewModels;

public abstract class TabViewModel : ViewModelBase
{
    private string _libelle;

    protected TabViewModel(string codeProcessus, string libelle)
    {
        CodeProcessus = codeProcessus;
        _libelle = libelle;
    }

    public string CodeProcessus { get; }

    public string Libelle
    {
        get => _libelle;
        set => SetProperty(ref _libelle, value);
    }
}
