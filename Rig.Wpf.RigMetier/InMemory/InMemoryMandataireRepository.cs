using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.InMemory;

public sealed class InMemoryMandataireRepository : IMandataireRepository
{
    private readonly ConcurrentDictionary<int, MandataireDto> _byId = new();
    private int _nextId;

    public MandataireDto? GetById(int id)
        => _byId.TryGetValue(id, out var m) ? m : null;

    public IReadOnlyList<MandataireDto> GetTous() => _byId.Values.ToList();

    public IReadOnlyList<MandataireDto> GetActifs()
        => _byId.Values.Where(m => m.EstActif).ToList();

    public int Save(MandataireDto mandataire)
    {
        if (mandataire is null) throw new ArgumentNullException(nameof(mandataire));
        var id = mandataire.Id == 0 ? Interlocked.Increment(ref _nextId) : mandataire.Id;
        _byId[id] = mandataire with { Id = id };
        return id;
    }
}
