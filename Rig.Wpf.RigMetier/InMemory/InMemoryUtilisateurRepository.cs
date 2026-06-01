using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.InMemory;

/// <summary>
/// Implémentation en mémoire de <see cref="IUtilisateurRepository"/>, pour les tests
/// et pour démarrer le shell sans accès DB. Préchargé avec un utilisateur "courant"
/// par défaut.
/// </summary>
public sealed class InMemoryUtilisateurRepository : IUtilisateurRepository
{
    private readonly ConcurrentDictionary<string, UtilisateurDto> _byCode =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _codeCourant;

    public InMemoryUtilisateurRepository()
    {
        var defaut = new UtilisateurDto("ANON", "Anonyme", "Utilisateur", "ACC", true);
        _byCode[defaut.Code] = defaut;
        _codeCourant = defaut.Code;
    }

    public void Add(UtilisateurDto user) => _byCode[user.Code] = user;

    public void SetCourant(string? codeUtilisateur)
    {
        _codeCourant = codeUtilisateur;
    }

    public UtilisateurDto? GetByCode(string codeUtilisateur)
    {
        if (string.IsNullOrWhiteSpace(codeUtilisateur)) return null;
        return _byCode.TryGetValue(codeUtilisateur, out var u) ? u : null;
    }

    public UtilisateurDto? GetCourant()
        => _codeCourant is null ? null : GetByCode(_codeCourant);

    public IReadOnlyList<UtilisateurDto> GetByService(string serviceAbrege)
        => _byCode.Values
            .Where(u => string.Equals(u.ServiceAbrege, serviceAbrege, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
