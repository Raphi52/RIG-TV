using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.Core.Session;

/// <summary>
/// Implémentation neutre de <see cref="IUtilisateurService"/>, utilisée par défaut
/// dans le DI tant que l'adapter legacy (qui appelle <c>Utilisateur.GetUtilisateurCourant</c>)
/// n'est pas branché. Retourne toujours <c>null</c>.
/// </summary>
public sealed class NullUtilisateurService : IUtilisateurService
{
    public UtilisateurInfo? GetCurrent() => null;
    public UtilisateurInfo? GetByCode(string codeUtilisateur) => null;
}
