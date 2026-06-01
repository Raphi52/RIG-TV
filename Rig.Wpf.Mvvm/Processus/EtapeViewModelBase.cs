using System;

namespace Rig.Wpf.Mvvm.Processus;

/// <summary>
/// Étape (E) ou Saisie (S) ou Récapitulatif (R) dans un Processus.
/// Concept hérité de la hiérarchie legacy <c>Etape → Operation → Ult</c> mais
/// transposé en MVVM : pas d'<c>IDB</c> direct, validation via INotifyDataErrorInfo
/// ou propriété <see cref="IsValid"/>.
/// </summary>
public abstract class EtapeViewModelBase : ViewModelBase
{
    private bool _isValid;

    protected EtapeViewModelBase(string code, string libelle)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code étape requis.", nameof(code));
        Code = code;
        Libelle = libelle;
    }

    public string Code { get; }
    public string Libelle { get; }

    /// <summary>
    /// True quand l'étape est satisfaite (peut être quittée vers la suivante).
    /// Mutable via <see cref="SetIsValid"/> pour permettre aux sous-classes
    /// d'émettre la notification.
    /// </summary>
    public bool IsValid
    {
        get => _isValid;
        protected set => SetIsValid(value);
    }

    public event EventHandler? IsValidChanged;

    protected bool SetIsValid(bool value)
    {
        if (SetProperty(ref _isValid, value, nameof(IsValid)))
        {
            IsValidChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }
}
