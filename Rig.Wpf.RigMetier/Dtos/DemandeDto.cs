using System;

namespace Rig.Wpf.RigMetier.Dtos;

/// <summary>
/// Projection plate d'une <c>RIG.METIER.Demande</c>. Les états reflètent l'enum
/// historique (Entree, Saisie, Recapitulatif, Validation, Terminee, Rejetee).
/// </summary>
public sealed record DemandeDto(
    int Id,
    string CodeProcessus,
    string CodeGreffe,
    DemandeEtat Etat,
    DateTime DateCreation,
    string? CodeUtilisateurOuvreur);

public enum DemandeEtat
{
    Entree,
    Saisie,
    Recapitulatif,
    Validation,
    Terminee,
    Rejetee
}
