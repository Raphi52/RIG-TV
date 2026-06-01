using System.Collections.Generic;
using Rig.Wpf.RigMetier.Dtos;

namespace Rig.Wpf.RigMetier.Repositories;

public interface IDemandeRepository
{
    DemandeDto? GetById(int idDemande);
    IReadOnlyList<DemandeDto> GetByGreffe(string codeGreffe);
    IReadOnlyList<DemandeDto> GetEnCours(string codeGreffe);
    int Save(DemandeDto demande);
}
