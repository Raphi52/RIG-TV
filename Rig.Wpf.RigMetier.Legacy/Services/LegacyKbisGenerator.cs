using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rig.Wpf.RigMetier.Services;
using LegacyRigKbis = RIGKBISXML.RigKbisXml;

namespace Rig.Wpf.RigMetier.Legacy.Services;

/// <summary>
/// Implémentation de <see cref="IKbisGenerator"/> qui réutilise en bloc le
/// pipeline legacy <see cref="RIGKBISXML.RigKbisXml"/>. Pipeline complet :
/// XML → XSL Harmonise → preformat → XSL PDF → FO → Apache FOP (IKVM).
/// La connexion <see cref="RIG.SQL.SqlRigConnection"/> est obtenue depuis
/// <see cref="LegacyRigMetierBootstrap.GetReadConnection"/>.
///
/// <para><b>Cache local</b> : un dictionnaire en mémoire <c>idDossier → pdfPath</c>
/// évite de relancer Apache FOP (2-3s par K-bis) si l'utilisateur revient sur
/// une société déjà consultée dans la session. Le cache est partagé entre tous
/// les VMs car le service est singleton. <c>forceRefresh=true</c> contourne le
/// cache et regenère.</para>
/// </summary>
public sealed class LegacyKbisGenerator : IKbisGenerator
{
    private readonly LegacyRigMetierBootstrap _bootstrap;
    private readonly ILogger _logger;
    private readonly Dictionary<int, string> _cachePaths = new();
    private readonly object _cacheLock = new();

    public LegacyKbisGenerator(LegacyRigMetierBootstrap bootstrap,
        ILogger<LegacyKbisGenerator>? logger = null)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _logger = (ILogger?)logger ?? NullLogger<LegacyKbisGenerator>.Instance;
    }

    public Task<string> GeneratePdfAsync(int idDossier, string codeGreffe,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (idDossier <= 0) throw new ArgumentOutOfRangeException(nameof(idDossier));
        if (string.IsNullOrWhiteSpace(codeGreffe))
            throw new ArgumentException("codeGreffe requis.", nameof(codeGreffe));

        // Cache hit : on a déjà généré ce PDF dans cette session, et le fichier
        // existe encore sur disque. On le retourne instantanément, sans
        // recharger IKVM ni invoquer FOP.
        if (!forceRefresh)
        {
            lock (_cacheLock)
            {
                if (_cachePaths.TryGetValue(idDossier, out var cached) && File.Exists(cached))
                {
                    _logger.LogDebug("Cache hit K-bis idDossier={Id} → {Path}", idDossier, cached);
                    return Task.FromResult(cached);
                }
            }
        }

        // Cache miss : pipeline complet sur threadpool (FOP/IKVM est synchrone
        // bloquant ~2-3s warm, ~10s premier appel JVM load).
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var cnx = _bootstrap.GetReadConnection(codeGreffe);
            var param = new RigKbisParamAdapter
            {
                CodeGreffe = codeGreffe,
                idDossier = idDossier,
                Signature = "true",
                CnxRead = cnx,
            };
            if (forceRefresh) param.idKbis = 0;

            var pipeline = new LegacyRigKbis(param);
            var pdfPath = pipeline.CreatePDF();
            if (string.IsNullOrEmpty(pdfPath))
            {
                // Cas le plus fréquent : dossier sans KbisXml cachable
                // (ex : société récemment immatriculée pour laquelle aucun
                // KbisXml n'a encore été calculé, ou MiseAjourKBisXml échoue).
                _logger.LogWarning(
                    "RigKbisXml.CreatePDF a retourné chemin vide pour idDossier={Id} (greffe {Greffe}). " +
                    "Probable : pas de cache KbisXml et MiseAjourKBisXml impossible (dossier neuf, radié, ou data incomplete).",
                    idDossier, codeGreffe);
                throw new InvalidOperationException(
                    $"Aucun K-bis n'a pu être généré pour ce dossier (id={idDossier}). " +
                    "Probable cause : société récemment immatriculée sans cache K-bis, " +
                    "dossier radié sans entrée KbisXml, ou données entreprise incomplètes. " +
                    "Essayez 'Régénérer' pour forcer le pipeline.");
            }

            lock (_cacheLock)
            {
                _cachePaths[idDossier] = pdfPath;
            }
            _logger.LogInformation("PDF K-bis généré : {Path}", pdfPath);
            return pdfPath;
        }, ct);
    }
}
