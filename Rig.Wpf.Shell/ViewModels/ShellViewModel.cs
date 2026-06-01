using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Mvvm;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Shell.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly IPluginLoader _pluginLoader;
    private readonly INativeProcessusRegistry _nativeRegistry;
    private TabViewModel? _selectedTab;
    private bool _isHomeRequested;

    public ShellViewModel(IPluginLoader pluginLoader, INativeProcessusRegistry nativeRegistry,
        SessionInfo session)
    {
        _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
        _nativeRegistry = nativeRegistry ?? throw new ArgumentNullException(nameof(nativeRegistry));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        OpenTabCommand = new RelayCommand<string>(OpenTab);
        CloseTabCommand = new RelayCommand<string>(CloseTab);
        GoHomeCommand = new RelayCommand(GoHome);

        // Dashboard d'accueil — pointe vers OpenTabCommand pour les cartes/KPI.
        Dashboard = new DashboardViewModel(code => OpenTab(code), Session);

        // Notifier HasTabs/IsHome quand la collection Tabs évolue.
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsHome));
        };
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>Infos utilisateur + base affichées dans la sidebar.</summary>
    public SessionInfo Session { get; }

    /// <summary>VM de la page d'accueil affichée quand aucun tab n'est ouvert.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>True quand l'utilisateur a demandé explicitement le retour Accueil
    /// (click sur le logo RIG). Force l'affichage du Dashboard même si des tabs
    /// sont ouverts ; ils restent en mémoire et sont rappelables depuis la sidebar
    /// ou le bandeau d'onglets.</summary>
    private bool IsHomeRequested
    {
        get => _isHomeRequested;
        set
        {
            if (SetProperty(ref _isHomeRequested, value))
            {
                OnPropertyChanged(nameof(HasTabs));
                OnPropertyChanged(nameof(IsHome));
            }
        }
    }

    /// <summary>True quand des tabs sont visibles : il y a au moins un tab ouvert
    /// ET l'utilisateur n'est pas explicitement revenu à l'accueil.</summary>
    public bool HasTabs => Tabs.Count > 0 && !IsHomeRequested;

    /// <summary>True quand on affiche le Dashboard d'accueil : soit aucun tab
    /// ouvert, soit l'utilisateur a cliqué le logo.</summary>
    public bool IsHome => Tabs.Count == 0 || IsHomeRequested;

    /// <summary>Commande déclenchée par le click sur le logo RIG : retour au
    /// Dashboard sans fermer les tabs ouverts.</summary>
    public RelayCommand GoHomeCommand { get; }

    private void GoHome()
    {
        IsHomeRequested = true;
    }

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public RelayCommand<string> OpenTabCommand { get; }
    public RelayCommand<string> CloseTabCommand { get; }

    public void UpdateTabCaption(string codeProcessus, string libelle)
    {
        var tab = FindTab(codeProcessus);
        if (tab is not null) tab.Libelle = libelle;
    }

    public bool IsTabOpen(string codeProcessus) => FindTab(codeProcessus) is not null;

    private void OpenTab(string? codeProcessus)
    {
        if (string.IsNullOrWhiteSpace(codeProcessus))
            throw new ArgumentException("Code processus requis.", nameof(codeProcessus));

        // Ouverture d'un tab → on quitte l'accueil.
        IsHomeRequested = false;

        var existing = FindTab(codeProcessus!);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        TabViewModel tab = _nativeRegistry.IsNative(codeProcessus!)
            ? new NativeTabViewModel(_nativeRegistry.CreateProcessus(codeProcessus!))
            : CreateLegacyTab(codeProcessus!);

        Tabs.Add(tab);
        SelectedTab = tab;
    }

    private LegacyPluginTabViewModel CreateLegacyTab(string code)
    {
        var descriptor = _pluginLoader.LoadPlugin(code);
        return new LegacyPluginTabViewModel(descriptor);
    }

    private void CloseTab(string? codeProcessus)
    {
        if (string.IsNullOrWhiteSpace(codeProcessus)) return;
        var tab = FindTab(codeProcessus!);
        if (tab is null) return;

        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.Remove(tab);
        if (wasSelected) SelectedTab = Tabs.LastOrDefault();
    }

    private TabViewModel? FindTab(string codeProcessus)
        => Tabs.FirstOrDefault(t =>
            string.Equals(t.CodeProcessus, codeProcessus, StringComparison.OrdinalIgnoreCase));
}
