using System;

namespace Rig.Wpf.RigMetier.Dtos;

/// <summary>
/// Projection plate d'une société (dossier RCS + entreprise jointe).
/// Couvre les champs nécessaires à la recherche et l'identification visuelle.
/// </summary>
public sealed record SocieteDto(
    int IdDossier,           // DOSSIER_RCS.DSSRC_ID_DSRCS
    string NumGestion,       // DOSSIER_RCS.DSSRC_NUM_GESTION   ex "2010B00123"
    string? Siren,           // DOSSIER_RCS.DSSRC_SIREN
    string Denomination,     // ENTREPRISE.ENTRP_DESIGNATION
    string? FormeJuridique,  // ENTREPRISE.ENTRP_FORME_JURIDIQUE_UTI
    string? Sigle,           // ENTREPRISE.ENTRP_SIGLE
    DateTime DateImmat,      // DOSSIER_RCS.DSSRC_DATE_IMMAT
    DateTime? DateRadiation, // DOSSIER_RCS.DSSRC_DATE_RADIATION (null si active)
    string EtatDossier);     // DOSSIER_RCS.DSSRC_ETAT_DOSSIER 1 char
