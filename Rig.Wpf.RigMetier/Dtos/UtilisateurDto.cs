namespace Rig.Wpf.RigMetier.Dtos;

/// <summary>
/// Projection plate de l'entité <c>RIG.METIER.Utilisateur</c> côté WPF.
/// Pas de référence directe à RigMetier ici : un mapper côté Rig.Wpf.RigMetier.Legacy
/// (à venir) convertira l'entité legacy en ce DTO.
/// </summary>
public sealed record UtilisateurDto(
    string Code,
    string Nom,
    string Prenom,
    string ServiceAbrege,
    bool EstActif);
