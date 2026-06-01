using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Shell.Demo;

/// <summary>
/// VM générique d'un module de démo. Configuré via <see cref="DemoModuleConfig"/>,
/// présente une étape Liste (DataGrid filtrable) + une étape Saisie (formulaire).
/// Aucun branchement base : tout est mock-data. Sert à matérialiser
/// l'agencement final de chaque module RIG.
/// </summary>
public abstract class DemoProcessusViewModel : ProcessusViewModelBase
{
    private string? _statusMessage;
    private string _statusKind = "";

    protected DemoProcessusViewModel(DemoModuleConfig config)
        : base(config.Code, config.Title,
            new EtapeViewModelBase[]
            {
                new DemoListEtape(config.ListColumns, config.SampleRows),
                new DemoSaisieEtape(config.SaisieFields),
            })
    {
        Config = config;
        SaveCommand = new RelayCommand(Save);
        AddCommand = new RelayCommand(Add);
        BackToListCommand = new RelayCommand(BackToList);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CurrentEtape))
                OnPropertyChanged(nameof(IsOnSaisie));
        };
    }

    public DemoModuleConfig Config { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand BackToListCommand { get; }

    /// <summary>True quand l'étape courante est l'étape Saisie — pilote
    /// l'affichage du footer "Enregistrer".</summary>
    public bool IsOnSaisie => CurrentEtape is DemoSaisieEtape;

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

    private void Save()
    {
        StatusKind = "success";
        StatusMessage = "Démo : enregistrement simulé (aucune écriture en base).";
    }

    private void Add()
    {
        // Navigue vers l'étape Saisie en sautant la validation Liste.
        if (Etapes.Count >= 2) SetCurrentEtape(Etapes[1]);
        StatusKind = "";
        StatusMessage = null;
    }

    private void BackToList()
    {
        if (Etapes.Count >= 1) SetCurrentEtape(Etapes[0]);
        StatusKind = "";
        StatusMessage = null;
    }
}
