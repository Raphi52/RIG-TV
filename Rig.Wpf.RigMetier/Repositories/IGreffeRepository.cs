using System.Collections.Generic;
using Rig.Wpf.RigMetier.Dtos;

namespace Rig.Wpf.RigMetier.Repositories;

public interface IGreffeRepository
{
    GreffeDto? GetByCode(string codeGreffe);
    IReadOnlyList<GreffeDto> GetTous();
    IReadOnlyList<GreffeDto> GetActifs();
}
