using System;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Processus.Mandataire.Etapes;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.Processus.Mandataire;

public sealed class MandataireProcessusViewModel : ProcessusViewModelBase
{
    public const string StatusKindSuccess = "success";
    public const string StatusKindWarning = "warning";
    public const string StatusKindError = "error";

    private readonly IMandataireRepository _repository;
    private readonly MandataireMapper _mapper;
    private readonly MandataireRechercheEtapeViewModel _etapeRecherche;
    private readonly MandataireSaisieEtapeViewModel _etapeSaisie;
    private int _idMandataire;
    private string? _statusMessage;
    private string _statusKind = "";

    public MandataireProcessusViewModel(IMandataireRepository repository, MandataireMapper mapper)
        : this(repository, mapper,
            new MandataireRechercheEtapeViewModel(repository),
            new MandataireSaisieEtapeViewModel())
    { }

    private MandataireProcessusViewModel(
        IMandataireRepository repository,
        MandataireMapper mapper,
        MandataireRechercheEtapeViewModel recherche,
        MandataireSaisieEtapeViewModel saisie)
        : base("MANDATAIRE", "Mandataire judiciaire", new EtapeViewModelBase[] { recherche, saisie })
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _etapeRecherche = recherche;
        _etapeSaisie = saisie;

        PropertyChanged += OnSelfPropertyChanged;

        LoadCommand = new RelayCommand<int>(Load);
        SaveCommand = new RelayCommand(Save, () => _etapeSaisie.IsValid);
        NouveauMandataireCommand = new RelayCommand(StartNew);

        _etapeSaisie.IsValidChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public RelayCommand<int> LoadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand NouveauMandataireCommand { get; }

    public int IdMandataire
    {
        get => _idMandataire;
        private set => SetProperty(ref _idMandataire, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CurrentEtape)) return;
        if (ReferenceEquals(CurrentEtape, _etapeSaisie)
            && _etapeRecherche.MandataireSelectionne is { } selected)
        {
            _mapper.Load(selected, _etapeSaisie);
            IdMandataire = selected.Id;
        }
    }

    private void StartNew()
    {
        _etapeSaisie.Civilite = null;
        _etapeSaisie.Nom = null;
        _etapeSaisie.Abrege = null;
        _etapeSaisie.NumeroCnbf = null;
        _etapeSaisie.EstActif = true;
        IdMandataire = 0;
        _etapeRecherche.MandataireSelectionne = null;
        StatusKind = "";
        StatusMessage = null;
        SetCurrentEtape(_etapeSaisie);
    }

    private void Load(int idMandataire)
    {
        if (idMandataire <= 0) return;
        var existing = _repository.GetById(idMandataire);
        if (existing is null) return;

        _mapper.Load(existing, _etapeSaisie);
        IdMandataire = existing.Id;
        StatusKind = "";
        StatusMessage = null;
        SetCurrentEtape(_etapeSaisie);
    }

    private void Save()
    {
        // CanExecute garde déjà _etapeSaisie.IsValid → pas de branche !IsValid ici.
        try
        {
            int newId;
            if (_idMandataire == 0)
            {
                var dto = _mapper.SaveAsNew(_etapeSaisie);
                newId = _repository.Save(dto);
            }
            else
            {
                var existing = _repository.GetById(_idMandataire)
                               ?? new MandataireDto(_idMandataire, "", "", null, null, true);
                _mapper.Save(_etapeSaisie, ref existing);
                newId = _repository.Save(existing);
            }
            IdMandataire = newId;
            StatusKind = StatusKindSuccess;
            StatusMessage = $"Mandataire #{newId} enregistré.";
        }
        catch (NotSupportedException ex)
        {
            StatusKind = StatusKindWarning;
            StatusMessage = "Écriture mandataire non encore disponible côté legacy. " + ex.Message;
        }
        catch (Exception ex)
        {
            StatusKind = StatusKindError;
            StatusMessage = "Erreur lors de l'enregistrement : " + ex.Message;
        }
    }
}
