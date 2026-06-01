using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;
using Rig.Wpf.RigMetier.Services;

namespace Rig.Wpf.Processus.Kbis.Etapes;

/// <summary>
/// Étape Consultation K-bis (phase C) : recherche société + sélection + génération
/// PDF asynchrone. Expose <see cref="PdfFilePath"/> que la View binde sur WebView2.
/// </summary>
public sealed class KbisConsultationEtapeViewModel : EtapeViewModelBase
{
    private readonly ISocieteRepository _societes;
    private readonly IKbisGenerator _generator;
    private readonly string _codeGreffe;

    private string _filtreTexte = "";
    private SocieteDto? _selection;
    private string? _pdfFilePath;
    private bool _isGenerating;
    private string? _erreurGeneration;
    private CancellationTokenSource? _currentCts;
    private CancellationTokenSource? _reloadCts;
    private string? _erreurRecherche;

    public KbisConsultationEtapeViewModel(
        ISocieteRepository societes,
        IKbisGenerator generator,
        string codeGreffe)
        : base("KBIS_CONSULTATION", "Consultation K-bis")
    {
        _societes = societes ?? throw new ArgumentNullException(nameof(societes));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _codeGreffe = codeGreffe ?? throw new ArgumentNullException(nameof(codeGreffe));
        Resultats = new ObservableCollection<SocieteDto>();
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync());
        RegenererCommand = new RelayCommand(async () => await RegenererAsync(), () => Selection is not null);
        // Fire-and-forget : surtout pas Reload() synchrone en ctor — bloque le
        // dispatcher pendant que SQL répond et empêche l'animation d'ouverture
        // de l'onglet. Le ReloadAsync() peuple la collection après l'await.
        _ = ReloadAsync();
    }

    public ObservableCollection<SocieteDto> Resultats { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RegenererCommand { get; }

    public string FiltreTexte
    {
        get => _filtreTexte;
        set { if (SetProperty(ref _filtreTexte, value ?? "")) _ = ReloadAsync(); }
    }

    /// <summary>Dernière erreur de recherche SQL (timeout, connectivité). Null si OK.</summary>
    public string? ErreurRecherche
    {
        get => _erreurRecherche;
        private set => SetProperty(ref _erreurRecherche, value);
    }

    public SocieteDto? Selection
    {
        get => _selection;
        set
        {
            if (SetProperty(ref _selection, value))
            {
                SetIsValid(value is not null);
                RegenererCommand.NotifyCanExecuteChanged();
                _ = GenerateAsync(value);
            }
        }
    }

    public string? PdfFilePath
    {
        get => _pdfFilePath;
        private set => SetProperty(ref _pdfFilePath, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        private set => SetProperty(ref _isGenerating, value);
    }

    public string? ErreurGeneration
    {
        get => _erreurGeneration;
        private set => SetProperty(ref _erreurGeneration, value);
    }

    private async Task ReloadAsync()
    {
        // Cancel any in-flight search — l'utilisateur tape vite, on jette les
        // résultats périmés plutôt que de les afficher après le résultat plus récent.
        _reloadCts?.Cancel();
        _reloadCts = new CancellationTokenSource();
        var ct = _reloadCts.Token;
        var query = _filtreTexte;

        try
        {
            // ISocieteRepository.Search est synchrone (ADO.NET) → Task.Run pour
            // libérer le dispatcher pendant la latence SQL. L'await sans
            // ConfigureAwait(false) reprend sur le SyncContext WPF, donc l'update
            // de Resultats reste thread-safe.
            var data = await Task.Run(() => _societes.Search(query, maxResults: 50), ct);
            if (ct.IsCancellationRequested) return;
            ErreurRecherche = null;
            Resultats.Clear();
            foreach (var s in data) Resultats.Add(s);
        }
        catch (OperationCanceledException) { /* superseded by a fresher query */ }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            ErreurRecherche = "Recherche échouée : " + ex.Message;
            Resultats.Clear();
        }
    }

    private async Task GenerateAsync(SocieteDto? dto, bool force = false)
    {
        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        if (dto is null)
        {
            PdfFilePath = null;
            ErreurGeneration = null;
            return;
        }
        IsGenerating = true;
        ErreurGeneration = null;
        try
        {
            var path = await _generator.GeneratePdfAsync(dto.IdDossier, _codeGreffe, force, ct);
            if (!ct.IsCancellationRequested) PdfFilePath = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErreurGeneration = "Génération K-bis échouée : " + ex.Message;
            PdfFilePath = null;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private Task RegenererAsync() => GenerateAsync(_selection, force: true);
}
