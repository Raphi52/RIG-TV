using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;
using LegacyRigGreffe = RIG.TOOLS.RigGreffe;

namespace Rig.Wpf.RigMetier.Legacy.Repositories;

/// <summary>
/// Wrappe les façades statiques <c>RIG.TOOLS.RigGreffe</c> derrière
/// l'interface injectable <see cref="IGreffeRepository"/>.
/// </summary>
public sealed class LegacyGreffeRepository : IGreffeRepository
{
    private readonly LegacyRigMetierBootstrap _bootstrap;
    private readonly ILogger _logger;

    public LegacyGreffeRepository(
        LegacyRigMetierBootstrap bootstrap,
        ILogger<LegacyGreffeRepository>? logger = null)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _logger = (ILogger?)logger ?? NullLogger<LegacyGreffeRepository>.Instance;
    }

    public GreffeDto? GetByCode(string codeGreffe)
    {
        if (string.IsNullOrWhiteSpace(codeGreffe)) return null;
        try
        {
            var g = LegacyRigGreffe.GetGreffe(codeGreffe);
            return Map(g);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetByCode({Code}) — échec.", codeGreffe);
            return null;
        }
    }

    public IReadOnlyList<GreffeDto> GetTous()
    {
        try
        {
            var list = LegacyRigGreffe.GetListGreffeRig(localOnly: true);
            return list.Select(Map).Where(x => x is not null).Cast<GreffeDto>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetTous() — échec.");
            return Array.Empty<GreffeDto>();
        }
    }

    public IReadOnlyList<GreffeDto> GetActifs()
    {
        try
        {
            var list = LegacyRigGreffe.GetListGreffeRigExploitation(localOnly: true);
            return list.Select(Map).Where(x => x is not null).Cast<GreffeDto>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetActifs() — échec.");
            return Array.Empty<GreffeDto>();
        }
    }

    private static GreffeDto? Map(LegacyRigGreffe? g)
    {
        if (g is null) return null;
        try
        {
            return new GreffeDto(
                Code: g.CodeGreffe,
                Libelle: g.Libelle ?? g.CodeGreffe,
                VilleSiege: null,
                EstActif: true);
        }
        catch
        {
            return null;
        }
    }
}
