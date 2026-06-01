using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;
using LegacyMandataire = RIG.METIER.JUDICIAIRE.Mandataire;

namespace Rig.Wpf.RigMetier.Legacy.Repositories;

/// <summary>
/// Wrappe les façades statiques <c>RIG.METIER.JUDICIAIRE.Mandataire</c>.
/// </summary>
public sealed class LegacyMandataireRepository : IMandataireRepository
{
    private readonly LegacyRigMetierBootstrap _bootstrap;
    private readonly ILogger _logger;

    public LegacyMandataireRepository(
        LegacyRigMetierBootstrap bootstrap,
        ILogger<LegacyMandataireRepository>? logger = null)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _logger = (ILogger?)logger ?? NullLogger<LegacyMandataireRepository>.Instance;
    }

    private string CodeGreffeOrThrow()
    {
        if (string.IsNullOrEmpty(_bootstrap.CodeGreffe))
            throw new InvalidOperationException(
                "RigMetier legacy n'est pas initialisé : appelez LegacyRigMetierBootstrap.Initialize(codeGreffe).");
        return _bootstrap.CodeGreffe!;
    }

    public MandataireDto? GetById(int id)
    {
        if (id <= 0) return null;
        try
        {
            var m = LegacyMandataire.GetMandataire(id, CodeGreffeOrThrow());
            return Map(m);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetById({Id}) — échec.", id);
            return null;
        }
    }

    public IReadOnlyList<MandataireDto> GetTous()
    {
        try
        {
            var all = LegacyMandataire.GetAllMandataire(CodeGreffeOrThrow());
            return all.Select(Map).Where(x => x is not null).Cast<MandataireDto>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetTous() — échec.");
            return Array.Empty<MandataireDto>();
        }
    }

    public IReadOnlyList<MandataireDto> GetActifs()
    {
        try
        {
            var all = LegacyMandataire.GetAllMandataire(CodeGreffeOrThrow());
            return all
                .Where(m => !m.MNDTR_ACTIF.isNull && m.MNDTR_ACTIF.Valeur)
                .Select(Map).Where(x => x is not null).Cast<MandataireDto>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetActifs() — échec.");
            return Array.Empty<MandataireDto>();
        }
    }

    public int Save(MandataireDto mandataire)
    {
        if (mandataire is null) throw new ArgumentNullException(nameof(mandataire));
        // L'écriture en BD passe par la façade Sauve() de l'entité legacy.
        // Pour l'instant on rend l'opération volontairement non implémentée :
        // le Save complet nécessite une session legacy active (transactions,
        // permissions, etc.) qu'on ne veut pas activer dans le bootstrap minimal
        // de cohabitation. Sera comblé quand un Processus natif aura besoin d'écrire.
        _logger.LogWarning(
            "LegacyMandataireRepository.Save() non implémenté — écriture non effectuée pour Id={Id}, Nom={Nom}.",
            mandataire.Id, mandataire.Nom);
        throw new NotSupportedException(
            "Save d'un mandataire via le legacy n'est pas encore branché. " +
            "Voir LegacyMandataireRepository.Save pour le pas-à-pas (Mandataire.Sauve, transaction).");
    }

    private static MandataireDto? Map(LegacyMandataire? m)
    {
        if (m is null) return null;
        try
        {
            return new MandataireDto(
                Id: m.MNDTR_ID_MNDTR_MANDATAIRE.isNull ? 0 : m.MNDTR_ID_MNDTR_MANDATAIRE.Valeur,
                Civilite: m.MNDTR_CIVILITE.Valeur ?? "",
                Nom: m.MNDTR_NOM.Valeur ?? "",
                Abrege: m.MNDTR_ABREGE.isNull ? null : m.MNDTR_ABREGE.Valeur,
                NumeroCnbf: m.MNDTR_NUMERO_CNBF.isNull ? null : m.MNDTR_NUMERO_CNBF.Valeur,
                EstActif: !m.MNDTR_ACTIF.isNull && m.MNDTR_ACTIF.Valeur);
        }
        catch
        {
            return null;
        }
    }
}
