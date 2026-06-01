using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Processus.Ipe.Etapes;

public sealed class IpeFactureEtapeViewModel : EtapeViewModelBase
{
    private decimal _montant;

    public IpeFactureEtapeViewModel()
        : base("IPE_FACTURE", "Facturation")
    {
        Recompute();
    }

    public decimal Montant
    {
        get => _montant;
        set { if (SetProperty(ref _montant, value)) Recompute(); }
    }

    private void Recompute()
        => SetIsValid(_montant > 0m);
}
