using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Rig.Wpf.Mvvm.Processus;

/// <summary>
/// Base de tout Processus WPF natif. Orchestre une séquence d'étapes
/// (<see cref="EtapeViewModelBase"/>) avec navigation simple.
/// </summary>
public abstract class ProcessusViewModelBase : ViewModelBase
{
    private EtapeViewModelBase? _currentEtape;

    protected ProcessusViewModelBase(
        string code,
        string libelle,
        IEnumerable<EtapeViewModelBase> etapes)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code processus requis.", nameof(code));
        if (etapes is null) throw new ArgumentNullException(nameof(etapes));

        Code = code;
        Libelle = libelle;
        Etapes = new ReadOnlyCollection<EtapeViewModelBase>(new List<EtapeViewModelBase>(etapes));

        foreach (var etape in Etapes)
        {
            etape.IsValidChanged += OnEtapeIsValidChanged;
        }

        GoNextCommand = new RelayCommand(GoNext, CanGoNext);
        GoPreviousCommand = new RelayCommand(GoPrevious, CanGoPrevious);

        if (Etapes.Count > 0) _currentEtape = Etapes[0];
    }

    public string Code { get; }
    public string Libelle { get; }
    public IReadOnlyList<EtapeViewModelBase> Etapes { get; }

    public EtapeViewModelBase? CurrentEtape
    {
        get => _currentEtape;
        private set
        {
            if (SetProperty(ref _currentEtape, value))
            {
                GoNextCommand.NotifyCanExecuteChanged();
                GoPreviousCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public RelayCommand GoNextCommand { get; }
    public RelayCommand GoPreviousCommand { get; }

    /// <summary>
    /// Permet aux sous-classes de naviguer programmatiquement vers une étape
    /// (commandes "Nouveau", "Modifier", chargement initial par id, etc. —
    /// transitions hors GoNext/GoPrevious).
    /// </summary>
    protected void SetCurrentEtape(EtapeViewModelBase etape)
    {
        if (etape is null) throw new ArgumentNullException(nameof(etape));
        CurrentEtape = etape;
    }

    private bool CanGoNext()
    {
        if (_currentEtape is null) return false;
        if (!_currentEtape.IsValid) return false;
        return CurrentIndex < Etapes.Count - 1;
    }

    private void GoNext()
    {
        if (!CanGoNext()) return;
        CurrentEtape = Etapes[CurrentIndex + 1];
    }

    private bool CanGoPrevious()
        => _currentEtape is not null && CurrentIndex > 0;

    private void GoPrevious()
    {
        if (!CanGoPrevious()) return;
        CurrentEtape = Etapes[CurrentIndex - 1];
    }

    private int CurrentIndex
    {
        get
        {
            if (_currentEtape is null) return -1;
            for (var i = 0; i < Etapes.Count; i++)
                if (ReferenceEquals(Etapes[i], _currentEtape)) return i;
            return -1;
        }
    }

    private void OnEtapeIsValidChanged(object? sender, EventArgs e)
    {
        GoNextCommand.NotifyCanExecuteChanged();
    }
}
