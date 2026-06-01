using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Processus.Testnlh.Etapes;

namespace Rig.Wpf.Processus.Testnlh;

public sealed class TestnlhProcessusViewModel : ProcessusViewModelBase
{
    private string? _lastSavedMessage;
    private readonly TestnlhEtape1ViewModel _etape1;

    public TestnlhProcessusViewModel()
        : this(new TestnlhEtape1ViewModel()) { }

    private TestnlhProcessusViewModel(TestnlhEtape1ViewModel etape1)
        : base("TESTNLH", "Test NLH (pilote WPF)", new[] { etape1 })
    {
        _etape1 = etape1;
        SaveCommand = new RelayCommand(Save, CanSave);
        _etape1.IsValidChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>
    /// Dernier message persisté. En production, un mapper RigMetier prendrait
    /// le relais ; ici on illustre le pattern avec une simple propriété.
    /// </summary>
    public string? LastSavedMessage
    {
        get => _lastSavedMessage;
        private set => SetProperty(ref _lastSavedMessage, value);
    }

    private bool CanSave() => _etape1.IsValid;

    private void Save()
    {
        if (!CanSave()) return;
        LastSavedMessage = _etape1.Message;
    }
}
