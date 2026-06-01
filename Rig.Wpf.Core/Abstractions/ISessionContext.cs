using System;

namespace Rig.Wpf.Core.Abstractions;

/// <summary>
/// Session courante (greffe + utilisateur) abstraite des singletons legacy.
/// L'implémentation par défaut est en mémoire ; côté shell WPF l'implémentation
/// adapte le singleton statique RigConsoleAccueil.Connexion.
/// </summary>
public interface ISessionContext
{
    string? CodeGreffe { get; }
    string? CodeUtilisateur { get; }
    bool IsAuthenticated { get; }

    event EventHandler? SessionChanged;

    void SignIn(string codeUtilisateur, string codeGreffe);
    void SignOut();
    void SwitchGreffe(string codeGreffe);
}
