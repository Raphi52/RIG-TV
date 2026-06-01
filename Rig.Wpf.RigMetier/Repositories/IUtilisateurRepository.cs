using System.Collections.Generic;
using Rig.Wpf.RigMetier.Dtos;

namespace Rig.Wpf.RigMetier.Repositories;

public interface IUtilisateurRepository
{
    UtilisateurDto? GetByCode(string codeUtilisateur);
    UtilisateurDto? GetCourant();
    IReadOnlyList<UtilisateurDto> GetByService(string serviceAbrege);
}
