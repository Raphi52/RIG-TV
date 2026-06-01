using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.InMemory;

public sealed class InMemoryDemandeRepository : IDemandeRepository
{
    private readonly ConcurrentDictionary<int, DemandeDto> _byId = new();
    private int _nextId;

    public DemandeDto? GetById(int idDemande)
        => _byId.TryGetValue(idDemande, out var d) ? d : null;

    public IReadOnlyList<DemandeDto> GetByGreffe(string codeGreffe)
        => _byId.Values
            .Where(d => string.Equals(d.CodeGreffe, codeGreffe, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<DemandeDto> GetEnCours(string codeGreffe)
        => _byId.Values
            .Where(d => string.Equals(d.CodeGreffe, codeGreffe, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Etat is DemandeEtat.Entree or DemandeEtat.Saisie or DemandeEtat.Recapitulatif)
            .ToList();

    public int Save(DemandeDto demande)
    {
        if (demande is null) throw new ArgumentNullException(nameof(demande));
        var id = demande.Id == 0 ? Interlocked.Increment(ref _nextId) : demande.Id;
        _byId[id] = demande with { Id = id };
        return id;
    }
}
