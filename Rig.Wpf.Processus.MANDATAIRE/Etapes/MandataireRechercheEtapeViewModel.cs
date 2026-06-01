using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.Processus.Mandataire.Etapes;

/// <summary>
/// Étape Recherche du processus Mandataire : DataGrid des mandataires
/// actifs (par défaut) ou tous, filtrables par texte sur Nom/Abrégé.
/// IsValid = true dès qu'une ligne est sélectionnée.
/// </summary>
public sealed class MandataireRechercheEtapeViewModel : EtapeViewModelBase
{
    private readonly IMandataireRepository _repository;
    private MandataireDto? _mandataireSelectionne;
    private string _filtreTexte = "";
    private bool _inclureInactifs;

    public ObservableCollection<MandataireDto> Resultats { get; }

    public MandataireDto? MandataireSelectionne
    {
        get => _mandataireSelectionne;
        set
        {
            if (SetProperty(ref _mandataireSelectionne, value))
            {
                SetIsValid(value is not null);
            }
        }
    }

    public string FiltreTexte
    {
        get => _filtreTexte;
        set
        {
            if (SetProperty(ref _filtreTexte, value ?? ""))
            {
                Reload();
            }
        }
    }

    public bool InclureInactifs
    {
        get => _inclureInactifs;
        set
        {
            if (SetProperty(ref _inclureInactifs, value))
            {
                Reload();
            }
        }
    }

    public RelayCommand RefreshCommand { get; }

    public MandataireRechercheEtapeViewModel(IMandataireRepository repository)
        : base("MANDATAIRE_RECHERCHE", "Recherche d'un mandataire")
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        Resultats = new ObservableCollection<MandataireDto>();
        RefreshCommand = new RelayCommand(Reload);
        Reload();
    }

    private void Reload()
    {
        IEnumerable<MandataireDto> source = _inclureInactifs
            ? _repository.GetTous()
            : _repository.GetActifs();

        if (!string.IsNullOrWhiteSpace(_filtreTexte))
        {
            var f = _filtreTexte.Trim();
            source = source.Where(m =>
                (m.Nom != null && m.Nom.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                || (m.Abrege != null && m.Abrege.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        var prevId = _mandataireSelectionne?.Id;
        Resultats.Clear();
        foreach (var dto in source.OrderBy(m => m.Nom))
        {
            Resultats.Add(dto);
        }
        if (prevId.HasValue)
        {
            var restored = Resultats.FirstOrDefault(m => m.Id == prevId.Value);
            if (restored is not null)
            {
                _mandataireSelectionne = restored;
                return;
            }
        }
        if (_mandataireSelectionne is not null)
        {
            MandataireSelectionne = null;
        }
    }
}
