using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Processus.Testnlh.Etapes;

/// <summary>
/// Étape unique du Processus TESTNLH : saisie d'un message libre,
/// validé dès qu'il n'est pas vide. Sert de modèle minimal pour le pattern
/// d'Etape WPF natif (sans <c>Ult.Link(IDB)</c>, juste un binding propre).
/// </summary>
public sealed class TestnlhEtape1ViewModel : EtapeViewModelBase
{
    private string? _message;

    public TestnlhEtape1ViewModel()
        : base("TESTNLH_E1", "Saisie de test") { }

    public string? Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value))
                SetIsValid(!string.IsNullOrWhiteSpace(value));
        }
    }
}
