// SPDX-License-Identifier: Proprietary
// Registre des scénarios définis via le builder.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rig.Wpf.Kbis.SmokeRunner.Scenarios;

/// <summary>
/// AUTO-DÉCOUVERTE des scénarios : pour en ajouter un, on AJOUTE une définition ICI (un seul endroit) —
/// plus de flag CLI dédié dans Program, plus d'entrée séparée, plus d'adapter boilerplate.
/// MVP : <c>kbis-vk</c> migré depuis <c>RunLegacyKbisVk</c> comme preuve red→green ; les autres scénarios
/// (kbis-xex, alertes-*, dcademat-*) suivront sur le même modèle.
/// </summary>
internal static class LegacyScenarioCatalog
{
    // ⚠ CONTRAT : les libellés de step ci-dessous sont MATCHÉS EN PRÉFIXE par les adapters UI
    //   (Kbis/Alertes/Dcademat LegacyScenarioAdapter, côté TestViewer). Ils doivent rester VERBATIM
    //   identiques à ceux des anciennes méthodes RunLegacyXxx. NE PAS « améliorer le wording » d'un libellé
    //   sans mettre à jour l'adapter correspondant — sinon le matching casse SILENCIEUSEMENT (pas d'erreur compile).
    private const string KbisPdfKey = "kbisPdf";

    private static string NumGestion()
    {
        var n = Environment.GetEnvironmentVariable("RIG_LEGACY_NUM_GESTION");
        return string.IsNullOrWhiteSpace(n) ? "2024B00001" : n;
    }

    /// <summary>Construit le catalogue (frais à chaque appel → lit les env vars au moment du run).</summary>
    public static IReadOnlyList<ScenarioDefinition> All()
    {
        var numGestion = NumGestion();
        var list = new List<ScenarioDefinition>
        {
            // ── kbis-vk — migré depuis RunLegacyKbisVk. Libellés VERBATIM : ils sont le contrat de
            //    complétion matché par KbisLegacyScenarioAdapter (préfixe "VK :"). NE PAS les altérer.
            ScenarioBuilder.New("kbis-vk", "PROC_VK : Visualisation extrait RCS depuis num_gestion")
                .Module("kbis")
                .Step("VK : Ouvrir PROC_KBIS depuis la Console d'accueil",
                      d => d.OpenProcKbis())
                .Step("VK : Le tab K-Bis (VK) apparaît dans la Console d'accueil",
                      d => d.VerifyKbisTabOpened())
                .Step($"VK : Saisir numéro de gestion '{numGestion}' + Tab → dossier chargé",
                      d => d.EnterNumGestionInActiveTab(numGestion, "tab VK"))
                .StepCtx("VK : Click 'Visualiser K-bis' → document/PDF ouvert",
                      ctx => ctx.Set(KbisPdfKey, ctx.Driver.OpenKbisDocument()))
                .StepCtx("VK : Le PDF K-bis contient les bonnes données (texte extrait, sans vision)",
                      ctx => ctx.Driver.VerifyKbisPdfContent(ctx.Get<string>(KbisPdfKey), numGestion))
                .Build(),

            // ── kbis-xex (migré depuis RunLegacyKbisXex) ──────────────────────────────────────────────
            ScenarioBuilder.New("kbis-xex", "PROC_XEX : Édition K-bis Brouillon (no print)")
                .Module("kbis")
                .Step("XEX : Ouvrir PROC_XEX depuis l'Accueil", d => d.OpenProcXex())
                .Step($"XEX : Saisir numéro de gestion '{numGestion}' + Tab",
                      d => d.EnterNumGestionInActiveTab(numGestion, "tab XEX"))
                .Step("XEX : Alt+V Valider → tableau d'édition affiché", d =>
                {
                    d.ClickValiderInActiveForm("tab XEX");
                    d.WaitForTableauEdition(timeoutSeconds: 15);
                })
                .Step("XEX : Décoche imprimante (NE PAS IMPRIMER) + Alt+V → Brouillon généré (popup fermée)", d =>
                {
                    d.UncheckImprimanteAndValidate();
                    d.WaitForTableauEditionClosed(timeoutSeconds: 10);
                })
                .Build(),

            // ── alertes-int-form (migré depuis RunLegacyAlertesIntForm) ────────────────────────────────
            // allowMyEnCours:true UNIQUEMENT pour les reprises 'interrompue' (int-*) : rouvrir une demande
            // déjà « en cours par moi » EST le terminal métier de la reprise. Les rec-* gardent le défaut (false).
            ScenarioBuilder.New("alertes-int-form", "ALERTES : reprise interrompue — formalités (J00)")
                .Module("alertes")
                .Step("INT-FORM : Ouvrir alerte 'Demandes interrompues' (grille visible)",
                      d => d.OpenAlerteRcs("interrompue"))
                .Step("INT-FORM : Double-clic demande formalités J00 → demande/document ouvert (reprise)",
                      d => d.OpenFirstDemandeAndVerify(dcademat: false, allowMyEnCours: true))
                .Build(),

            // ── alertes-int-dca (migré depuis RunLegacyAlertesIntDca) ──────────────────────────────────
            ScenarioBuilder.New("alertes-int-dca", "ALERTES : reprise interrompue — DCADEMAT")
                .Module("alertes")
                .Step("INT-DCA : Ouvrir alerte 'Demandes interrompues' (grille visible)",
                      d => d.OpenAlerteRcs("interrompue"))
                .Step("INT-DCA : Double-clic demande DCADEMAT → demande/document ouvert (reprise)",
                      d => d.OpenFirstDemandeAndVerify(dcademat: true, allowMyEnCours: true))
                .Build(),

            // ── alertes-rec-form (migré depuis RunLegacyAlertesRecForm) ────────────────────────────────
            ScenarioBuilder.New("alertes-rec-form", "ALERTES : reprise réclamation — formalités (J00)")
                .Module("alertes")
                .Step("REC-FORM : Ouvrir alerte 'réclamations > N j' (grille visible)",
                      d => d.OpenAlerteRcs("réclamation"))
                .Step("REC-FORM : Ouvrir 1re demande formalités J00 (reprise)",
                      d => d.OpenFirstDemandeAndVerify(dcademat: false))
                .Step("REC-FORM : Clic-droit → 'Reprendre les impressions' → courrier de réclamation affiché (sans imprimer)",
                      d => d.OpenReclamationViaMenuMultiTry(dcademat: false, menuItemSub: "Reprendre les impressions", label: "Courrier de réclamation (formalités)"))
                .Build(),

            // ── alertes-rec-dca (migré depuis RunLegacyAlertesRecDca) ──────────────────────────────────
            ScenarioBuilder.New("alertes-rec-dca", "ALERTES : reprise réclamation — DCADEMAT")
                .Module("alertes")
                .Step("REC-DCA : Ouvrir alerte 'réclamations > N j' (grille visible)",
                      d => d.OpenAlerteRcs("réclamation"))
                .Step("REC-DCA : Ouvrir 1re demande DCADEMAT (reprise)",
                      d => d.OpenFirstDemandeAndVerify(dcademat: true))
                .Step("REC-DCA : Clic-droit → 'Lancer le pool d'éditions' → Lettre de réclamation affichée (sans imprimer)",
                      d => d.OpenReclamationViaMenuMultiTry(dcademat: true, menuItemSub: "Lancer le pool", label: "Lettre de réclamation (DCADEMAT)"))
                .Build(),
        };
        list.AddRange(DcadematScenarios(numGestion));   // fix-ok: feature I3 (migration DCADEMAT), pas un fix aveugle
        return list.AsReadOnly();
    }

    // ── DCADEMAT : fabrique paramétrique (miroir de RunLegacyDcademat, Program.cs:558-690) ────────────
    // 8 kinds {dca|form}-{validation|reclamation|refus|interrompue}. Chaque kind → alerte source (arbre +
    // override env RIG_DCADEMAT_ALERTE_<KIND>) + squelette {ouvrir alerte → GATE ouvrir demande → Action
    // conditionnelle gatée par kind}. Libellés VERBATIM = contrat stdout de DcadematLegacyScenarioAdapter.
    private static readonly string[] DcadematKinds =
    {
        "dca-validation", "dca-reclamation", "dca-refus", "dca-interrompue",
        "form-validation", "form-reclamation", "form-refus", "form-interrompue",
    };

    private static string DcadematAlerte(string kind, bool isDca)
    {
        var ovr = Environment.GetEnvironmentVariable("RIG_DCADEMAT_ALERTE_" + kind.Replace('-', '_').ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(ovr)) return ovr;
        if (kind.Contains("reclamation") || kind.Contains("refus")) return "réclamation";
        if (kind.Contains("interrompue")) return "interrompue";
        return isDca ? "DCA démat en attente" : "DEMAT INPI";
    }

    private static IEnumerable<ScenarioDefinition> DcadematScenarios(string numGestion)
    {
        const string Opened = "demandeOuverte";
        foreach (var kind in DcadematKinds)
        {
            var tag = kind.ToUpperInvariant();
            var isDca = !kind.StartsWith("form");
            var famille = isDca ? "DCADEMAT" : "formalité J00";
            var alerte = DcadematAlerte(kind, isDca);
            var reprise = kind.Contains("interrompue");

            var b = ScenarioBuilder.New($"dcademat-{kind}",
                        $"DCADEMAT : {kind} (réutilise OpenAlerteRcs / OpenFirstDemandeAndVerify)")
                .Module("dcademat")
                .Step($"{tag} : Ouvrir alerte RCS ('{alerte}') + grille des demandes",
                      d => d.OpenAlerteRcs(alerte))
                .GateStep($"{tag} : Ouvrir demande ({famille}) → Configurer le dépôt", Opened,
                      d => d.OpenFirstDemandeAndVerify(dcademat: isDca, allowMyEnCours: reprise));

            // Action conditionnelle gatée par 'demandeOuverte', branchée par kind (libellés + skip verbatim).
            switch (kind)
            {
                case "dca-validation":
                    b.StepIf(Opened, $"{tag} : Action (validation) - case DCA + Valider + n° depot/facture/demande",
                        d => d.ConfigurerDepotDcaEtValider(numGestion),
                        $"{tag} : Action (validation) SKIP — aucune demande ouverte (étape précédente échouée).");
                    break;
                case "dca-reclamation":
                    b.StepIf(Opened, $"{tag} : Action (réclamation) - motif INPMANQ + texte TEST + Réclamer",
                        d => d.ReclamerDcaAvecMotif(),
                        $"{tag} : Action (réclamation) SKIP — aucune demande ouverte (étape précédente échouée).");
                    break;
                case "form-validation":
                    b.ActionableStepIf(Opened, $"{tag} : Action (validation) - Valider la formalité + vérif (RIG vivant / n° demande)",
                        ctx => ctx.Driver.ValiderFormaliteDemat(),
                        $"{tag} : Action (validation) SKIP — aucune demande ouverte (étape précédente échouée).");
                    break;
                case "dca-refus":
                case "form-refus":
                    b.StepIf(Opened, $"{tag} : Action (refus) - Refuser la demande + vérif (RIG vivant / courrier de refus)",
                        d => d.RefuserDemande(),
                        $"{tag} : Action (refus) SKIP — aucune demande ouverte (étape précédente échouée).");
                    break;
                // dca-interrompue / form-interrompue / form-reclamation : pas d'action (l'ouverture = terminal métier),
                // miroir exact de RunLegacyDcademat (aucune branche pour ces kinds).
            }
            yield return b.Build();
        }
    }

    /// <summary>Retourne la définition d'id donné, ou null si absente.</summary>
    public static ScenarioDefinition TryGet(string id)
        => All().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
