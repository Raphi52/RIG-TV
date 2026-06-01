using System.Collections.Generic;
using Rig.Wpf.RigMetier.Dtos;

namespace Rig.Wpf.RigMetier.Repositories;

public interface ISocieteRepository
{
    /// <summary>
    /// Recherche par préfixe sur dénomination, SIREN et n° gestion (insensible
    /// à la casse). Retourne max <paramref name="maxResults"/> résultats triés
    /// par dénomination.
    /// </summary>
    IReadOnlyList<SocieteDto> Search(string filter, int maxResults = 50);

    /// <summary>Charge la société par son <c>DSSRC_ID_DSRCS</c>. Retourne <c>null</c> si introuvable.</summary>
    SocieteDto? GetById(int idDossier);
}
