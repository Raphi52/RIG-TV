namespace Rig.Wpf.Core.Abstractions;

public interface IUtilisateurService
{
    UtilisateurInfo? GetCurrent();
    UtilisateurInfo? GetByCode(string codeUtilisateur);
}

public sealed record UtilisateurInfo(
    string Code,
    string Nom,
    string Prenom,
    string ServiceAbrege);
