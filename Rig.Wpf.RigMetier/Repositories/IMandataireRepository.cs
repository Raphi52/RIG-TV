using System.Collections.Generic;
using Rig.Wpf.RigMetier.Dtos;

namespace Rig.Wpf.RigMetier.Repositories;

public interface IMandataireRepository
{
    MandataireDto? GetById(int id);
    IReadOnlyList<MandataireDto> GetTous();
    IReadOnlyList<MandataireDto> GetActifs();
    /// <summary>
    /// Sauvegarde un mandataire. Si <c>Id == 0</c>, crée une nouvelle entrée
    /// et retourne le nouvel id. Sinon met à jour et retourne le même id.
    /// </summary>
    int Save(MandataireDto mandataire);
}
