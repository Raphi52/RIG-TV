using System;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Processus.Ipe.Etapes;

namespace Rig.Wpf.Processus.Ipe;

public sealed class IpeProcessusViewModel : ProcessusViewModelBase
{
    private readonly IpeDemandeEtapeViewModel _demande;
    private readonly IpeFactureEtapeViewModel _facture;
    private readonly IpeRecapEtapeViewModel _recap;
    private bool _hasBeenSaved;

    public IpeProcessusViewModel(IpeOrchestrator orchestrator)
        : this(new IpeDemandeEtapeViewModel(), new IpeFactureEtapeViewModel(), orchestrator) { }

    public IpeProcessusViewModel(
        IpeDemandeEtapeViewModel demande,
        IpeFactureEtapeViewModel facture,
        IpeOrchestrator orchestrator)
        : this(demande, facture, new IpeRecapEtapeViewModel(demande, facture,
            orchestrator ?? throw new ArgumentNullException(nameof(orchestrator)))) { }

    private IpeProcessusViewModel(
        IpeDemandeEtapeViewModel demande,
        IpeFactureEtapeViewModel facture,
        IpeRecapEtapeViewModel recap)
        : base("IPE", "Injonction de Payer Européenne", new EtapeViewModelBase[]
        {
            demande, facture, recap
        })
    {
        _demande = demande;
        _facture = facture;
        _recap = recap;

        SaveCommand = new RelayCommand(Save, CanSave);
        _demande.IsValidChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
        _facture.IsValidChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>
    /// Vrai dès qu'un Save a été exécuté avec succès.
    /// Permet aux tests/UI de vérifier la complétion sans persistance réelle.
    /// </summary>
    public bool HasBeenSaved
    {
        get => _hasBeenSaved;
        private set => SetProperty(ref _hasBeenSaved, value);
    }

    private bool CanSave() => _demande.IsValid && _facture.IsValid;

    private void Save()
    {
        if (!CanSave()) return;
        // Pour ce pilote multi-étapes, on s'arrête au passage en état "saved".
        // L'écriture réelle (Demande + Facture + génération du préfixe) sera
        // branchée quand IDemandeRepository.Save() sera implémenté côté legacy.
        HasBeenSaved = true;
    }
}
