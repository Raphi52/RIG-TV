using System.Collections.Generic;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Shell.Demo;

/// <summary>
/// Étape Saisie d'un module de démo : formulaire 2 colonnes alimenté par
/// une liste de <see cref="DemoField"/>. Toujours valide (démo sans backend).
/// </summary>
public sealed class DemoSaisieEtape : EtapeViewModelBase
{
    public DemoSaisieEtape(IReadOnlyList<DemoField> fields)
        : base("SAISIE", "Saisie")
    {
        Fields = fields;
        SetIsValid(true);
    }

    public IReadOnlyList<DemoField> Fields { get; }
}
