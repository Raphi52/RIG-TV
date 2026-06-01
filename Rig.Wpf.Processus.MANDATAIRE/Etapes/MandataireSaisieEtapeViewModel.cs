using System;
using System.Collections.Generic;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Processus.Mandataire.Etapes;

/// <summary>
/// Étape unique de saisie d'un mandataire. Reprend les champs essentiels
/// de la table <c>MANDATAIRE</c> côté legacy (MNDTR_*).
/// </summary>
public sealed class MandataireSaisieEtapeViewModel : EtapeViewModelBase
{
    private string? _civilite;
    private string? _nom;
    private string? _abrege;
    private string? _numeroCnbf;
    private bool _estActif = true;

    public MandataireSaisieEtapeViewModel()
        : base("MANDATAIRE_SAISIE", "Saisie du mandataire")
    {
        Recompute();
    }

    /// <summary>
    /// Codes civilité standards (cf. <c>RIG.METIER.JUDICIAIRE.Mandataire.MNDTR_CIVILITE</c>).
    /// PM = personne morale, ND = non déterminée.
    /// </summary>
    public IReadOnlyList<string> CivilitesDisponibles { get; } =
        new[] { "M", "MME", "MLLE", "PM", "ND" };

    public string? Civilite
    {
        get => _civilite;
        set { if (SetProperty(ref _civilite, value)) Recompute(); }
    }

    public string? Nom
    {
        get => _nom;
        set { if (SetProperty(ref _nom, value)) Recompute(); }
    }

    public string? Abrege
    {
        get => _abrege;
        set => SetProperty(ref _abrege, value);
    }

    public string? NumeroCnbf
    {
        get => _numeroCnbf;
        set => SetProperty(ref _numeroCnbf, value);
    }

    public bool EstActif
    {
        get => _estActif;
        set => SetProperty(ref _estActif, value);
    }

    private void Recompute()
        => SetIsValid(!string.IsNullOrWhiteSpace(_civilite)
                      && !string.IsNullOrWhiteSpace(_nom));
}
