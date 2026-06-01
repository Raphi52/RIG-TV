using System;
using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.Core.Session;

/// <summary>
/// Implémentation par défaut de <see cref="ISessionContext"/>, en mémoire,
/// utilisée pour les tests et tant que l'adapter legacy n'est pas branché.
/// </summary>
public sealed class InMemorySessionContext : ISessionContext
{
    public string? CodeGreffe { get; private set; }
    public string? CodeUtilisateur { get; private set; }
    public bool IsAuthenticated => CodeUtilisateur is not null;

    public event EventHandler? SessionChanged;

    public void SignIn(string codeUtilisateur, string codeGreffe)
    {
        if (string.IsNullOrWhiteSpace(codeUtilisateur))
            throw new ArgumentException("Code utilisateur requis.", nameof(codeUtilisateur));
        if (string.IsNullOrWhiteSpace(codeGreffe))
            throw new ArgumentException("Code greffe requis.", nameof(codeGreffe));

        CodeUtilisateur = codeUtilisateur;
        CodeGreffe = codeGreffe;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        if (!IsAuthenticated) return;
        CodeUtilisateur = null;
        CodeGreffe = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SwitchGreffe(string codeGreffe)
    {
        if (string.IsNullOrWhiteSpace(codeGreffe))
            throw new ArgumentException("Code greffe requis.", nameof(codeGreffe));
        if (CodeGreffe == codeGreffe) return;
        CodeGreffe = codeGreffe;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
