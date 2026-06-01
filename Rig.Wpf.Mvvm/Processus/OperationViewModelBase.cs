using System;

namespace Rig.Wpf.Mvvm.Processus;

/// <summary>
/// Operation (sous-élément d'une Etape, contenant N Ults).
/// Pour Sprint 2 / TESTNLH on n'a pas besoin de ce niveau, mais on définit
/// la base pour les Processus suivants.
/// </summary>
public abstract class OperationViewModelBase : ViewModelBase
{
    protected OperationViewModelBase(string code, string libelle)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code opération requis.", nameof(code));
        Code = code;
        Libelle = libelle;
    }

    public string Code { get; }
    public string Libelle { get; }
}
