using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.InMemory;

public sealed class InMemoryGreffeRepository : IGreffeRepository
{
    private readonly ConcurrentDictionary<string, GreffeDto> _byCode =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(GreffeDto greffe) => _byCode[greffe.Code] = greffe;

    public GreffeDto? GetByCode(string codeGreffe)
    {
        if (string.IsNullOrWhiteSpace(codeGreffe)) return null;
        return _byCode.TryGetValue(codeGreffe, out var g) ? g : null;
    }

    public IReadOnlyList<GreffeDto> GetTous() => _byCode.Values.ToList();

    public IReadOnlyList<GreffeDto> GetActifs()
        => _byCode.Values.Where(g => g.EstActif).ToList();
}
