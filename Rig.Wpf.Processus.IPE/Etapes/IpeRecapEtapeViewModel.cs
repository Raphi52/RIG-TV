using System;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Processus.Ipe.Etapes;

/// <summary>
/// Étape récapitulative read-only. Reçoit les VM des deux étapes précédentes
/// et expose des projections lisibles. Toujours valide (étape de validation finale).
/// </summary>
public sealed class IpeRecapEtapeViewModel : EtapeViewModelBase
{
    private readonly IpeDemandeEtapeViewModel _demande;
    private readonly IpeFactureEtapeViewModel _facture;
    private readonly IpeOrchestrator _orchestrator;

    public IpeRecapEtapeViewModel(
        IpeDemandeEtapeViewModel demande,
        IpeFactureEtapeViewModel facture,
        IpeOrchestrator orchestrator)
        : base("IPE_RECAP", "Récapitulatif")
    {
        _demande = demande ?? throw new ArgumentNullException(nameof(demande));
        _facture = facture ?? throw new ArgumentNullException(nameof(facture));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        _demande.PropertyChanged += (_, _) => RaiseProjections();
        _facture.PropertyChanged += (_, _) => RaiseProjections();

        SetIsValid(true);
    }

    public string TypeIpLibelle => _demande.TypeIp switch
    {
        TypeIp.Standard => "Standard",
        TypeIp.TribunalDigital => "Tribunal digital",
        _ => "(non renseigné)"
    };

    public string DateSaisineLibelle
        => _demande.DateSaisineGreffe?.ToString("dd/MM/yyyy") ?? "(non renseignée)";

    public string MontantLibelle
        => _facture.Montant.ToString("0.00 €", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    public string PrefixFacturation
        => _orchestrator.GetPrefixFacturation(_demande.DateSaisineGreffe);

    private void RaiseProjections()
    {
        OnPropertyChanged(nameof(TypeIpLibelle));
        OnPropertyChanged(nameof(DateSaisineLibelle));
        OnPropertyChanged(nameof(MontantLibelle));
        OnPropertyChanged(nameof(PrefixFacturation));
    }
}
