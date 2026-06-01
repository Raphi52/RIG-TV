using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;
using LegacyUtilisateur = RIG.METIER.Utilisateur;

namespace Rig.Wpf.RigMetier.Legacy.Repositories;

/// <summary>
/// Wrappe les façades statiques <c>RIG.METIER.Utilisateur</c> derrière
/// l'interface injectable <see cref="IUtilisateurRepository"/>. Toutes les
/// requêtes utilisent le code greffe configuré sur le bootstrap.
/// </summary>
public sealed class LegacyUtilisateurRepository : IUtilisateurRepository
{
    private readonly LegacyRigMetierBootstrap _bootstrap;
    private readonly ILogger _logger;

    public LegacyUtilisateurRepository(
        LegacyRigMetierBootstrap bootstrap,
        ILogger<LegacyUtilisateurRepository>? logger = null)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _logger = (ILogger?)logger ?? NullLogger<LegacyUtilisateurRepository>.Instance;
    }

    private string CodeGreffeOrThrow()
    {
        if (string.IsNullOrEmpty(_bootstrap.CodeGreffe))
            throw new InvalidOperationException(
                "RigMetier legacy n'est pas initialisé : appelez LegacyRigMetierBootstrap.Initialize(codeGreffe) au démarrage.");
        return _bootstrap.CodeGreffe!;
    }

    public UtilisateurDto? GetByCode(string codeUtilisateur)
    {
        if (string.IsNullOrWhiteSpace(codeUtilisateur)) return null;
        try
        {
            var u = LegacyUtilisateur.GetUtilisateurByName(codeUtilisateur, CodeGreffeOrThrow());
            return Map(u);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetByCode({Code}) — échec.", codeUtilisateur);
            return null;
        }
    }

    public UtilisateurDto? GetCourant()
    {
        try
        {
            var u = LegacyUtilisateur.GetUtilisateurCourant(CodeGreffeOrThrow());
            return Map(u);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCourant() — échec.");
            return null;
        }
    }

    public IReadOnlyList<UtilisateurDto> GetByService(string serviceAbrege)
    {
        if (string.IsNullOrWhiteSpace(serviceAbrege)) return Array.Empty<UtilisateurDto>();
        try
        {
            var all = LegacyUtilisateur.GetAllUtilisateurActif(CodeGreffeOrThrow());
            var filtered = new List<UtilisateurDto>();
            foreach (var u in all)
            {
                var dto = Map(u);
                if (dto is null) continue;
                if (string.Equals(dto.ServiceAbrege, serviceAbrege, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(dto);
            }
            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetByService({Service}) — échec.", serviceAbrege);
            return Array.Empty<UtilisateurDto>();
        }
    }

    private static UtilisateurDto? Map(LegacyUtilisateur? u)
    {
        if (u is null) return null;
        try
        {
            // Le service abrégé est dérivé du Service associé (table SERVICE).
            // Si le service n'est pas résolvable, on retourne "" pour rester non-bloquant.
            var serviceAbrege = "";
            try
            {
                var service = u.ServiceAssocie;
                if (service is not null)
                {
                    var prop = service.GetType().GetProperty("SRVC_ABREGE");
                    if (prop is not null)
                    {
                        var raw = prop.GetValue(service);
                        var valeur = raw?.GetType().GetProperty("Valeur")?.GetValue(raw);
                        serviceAbrege = valeur?.ToString() ?? "";
                    }
                }
            }
            catch { /* fallback "" */ }

            return new UtilisateurDto(
                Code: u.UTLST_LOGIN.Valeur ?? "",
                Nom: u.UTLST_NOM.Valeur ?? "",
                Prenom: u.UTLST_PRENOM.Valeur ?? "",
                ServiceAbrege: serviceAbrege,
                EstActif: !u.UTLST_DESACTIVE.Valeur);
        }
        catch
        {
            return null;
        }
    }
}
