namespace Rig.Wpf.RigMetier.Dtos;

/// <summary>
/// Projection plate de l'entité <c>RIG.METIER.JUDICIAIRE.Mandataire</c>.
/// Couvre les champs essentiels pour la création/modification ; les champs
/// secondaires (adresse, partie, civilité étendue) seront ajoutés à la demande.
/// </summary>
public sealed record MandataireDto(
    int Id,
    string Civilite,
    string Nom,
    string? Abrege,
    string? NumeroCnbf,
    bool EstActif);
