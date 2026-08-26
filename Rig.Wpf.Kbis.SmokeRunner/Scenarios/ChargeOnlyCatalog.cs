// SPDX-License-Identifier: Proprietary
using System;
using System.Collections.Generic;

namespace Rig.Wpf.Kbis.SmokeRunner.Scenarios;

/// <summary>
/// INC-B (Option D) — moteur de smoke CHARGE-ONLY pour les PROC RIG : pour chaque code d'une ALLOWLIST curée
/// (opt-in), génère un scénario générique « ouvrir le proc via la Console d'accueil + vérifier qu'un tab proc
/// s'ouvre » (READ-ONLY : aucune saisie/sélection/validation). Démarre PETIT (allowlist) pour éviter le
/// faux-FAIL de masse ; on élargit un code à la fois APRÈS l'avoir prouvé charge-only-safe e2e (allowlist →
/// denylist quand la couverture mûrit). Les procs À DONNÉES (MJUD…) ont un scénario MÉTIER dédié dans
/// <see cref="LegacyScenarioCatalog"/> et ne figurent PAS ici.
/// <para>Le flag CLI <c>--legacy-chargeonly-&lt;code&gt;</c> est auto-généré par le dispatch foreach de Program.</para>
/// </summary>
internal static class ChargeOnlyCatalog
{
    private const string TabsBeforeKey = "chargeonly.tabsBefore";

    /// <summary>Codes PROC charge-only-safe (opt-in). Élargir UN par UN après preuve e2e (capture lue).
    /// Candidats issus de rig-menu-tree.txt (procs DLL de paramétrage/consultation sans sélection amont).
    /// Périmètre total dispo = 190 procs DLL executable+visible (SQL PROCESSUS_ET_FONCTIONALITE, cf. --list-procs).</summary>
    public static readonly string[] Allowlist =
    {
        // (vide pour l'instant — opt-in : on n'ajoute un code qu'APRÈS l'avoir prouvé charge-only-safe e2e.)
        // Le signal charge-only = « un NOUVEAU tab s'ouvre ». Candidats VALIDES = procs qui ouvrent en ONGLET
        // (comme MJUD sur son écran de recherche, sans prérequis pour ouvrir).
        // DENYLIST connue (n'ouvrent PAS de tab → mauvais candidats charge-only, e2e 2026-07-09) :
        //   - PARAMPROC : activation OK mais AUCUN nouveau TabItem (prev=2/current=2) → fenêtre de config modale,
        //                 pas un onglet. Les procs PARAM_* sont probablement dans ce cas.
    };

    public static IEnumerable<ScenarioDefinition> All()
    {
        foreach (var code in Allowlist)
        {
            var codeLocal = code;
            yield return ScenarioBuilder.New($"chargeonly-{codeLocal.ToLowerInvariant()}",
                    $"CHARGE-ONLY {codeLocal} : ouverture proc (READ-ONLY, vérif NOUVEAU tab ouvert)")
                .Module("chargeonly")
                // Snapshot du nombre de tabs AVANT ouverture (baseline : Accueil + Demandes permanents).
                .StepCtx($"{codeLocal} : snapshot tabs avant ouverture", ctx => ctx.Set(TabsBeforeKey, ctx.Driver.CountTabs()))
                .Step($"{codeLocal} : Ouvrir depuis la Console d'accueil (scan menu)", d =>
                    d.OpenProcessus(codeLocal, name =>
                    {
                        var n = (name ?? "").Trim();
                        if (n.Length == 0) return false;
                        return n.Split(' ')[0].Equals(codeLocal, StringComparison.OrdinalIgnoreCase);
                    }))
                .Step($"{codeLocal} : Laisser le proc se rendre (poll 500ms, cap 15s)", d =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 15)
                        System.Threading.Thread.Sleep(500); // sleep-ok: settle-UI borné post-ouverture proc
                })
                // Expect DISCRIMINANT anti-faux-vert : un NOUVEAU tab doit être apparu (count > baseline),
                // sinon FAIL honnête. « un tab non-Accueil existe » ne suffit PAS (tab Demandes permanent).
                .Expect($"{codeLocal} : un NOUVEAU tab proc s'est ouvert (count > baseline = charge sans crash)",
                        ctx => ctx.Driver.CountTabs() > ctx.Get<int>(TabsBeforeKey))
                .Build();
        }
    }
}
