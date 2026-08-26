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

    /// <summary>Match du processus RAPTUVAL dans lstProcessus (code exact OU libellé « Validation … Rapture »).</summary>
    private static bool RaptuvalMatch(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        if (n.Split(' ')[0].Equals("RAPTUVAL", StringComparison.OrdinalIgnoreCase)) return true;
        return n.IndexOf("validation", StringComparison.OrdinalIgnoreCase) >= 0
            && n.IndexOf("rapture", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Construit le catalogue (frais à chaque appel → lit les env vars au moment du run).</summary>
    public static IReadOnlyList<ScenarioDefinition> All()
    {
        var numGestion = NumGestion();
        var list = new List<ScenarioDefinition>
        {
            ScenarioBuilder.New("dca-grid-focus", "PROC_DCA : oracle avant/après focus Date de clôture")
                .Module("alertes")
                .Step("DCA-GRID : Confirmer Recette 9999", d => d.RequireConnectedEnvironment("9999", "RIG_RECETTE"))
                .Step("DCA-GRID : Ouvrir PROC_DCA", d => d.OpenProcessus("PROC_DCA", name =>
                {
                    var n = (name ?? "").Trim();
                    if (n.Length == 0) return false;
                    var first = n.Split(' ')[0];
                    return first.Equals("DCA", StringComparison.OrdinalIgnoreCase)
                        || (n.IndexOf("dépôt", StringComparison.OrdinalIgnoreCase) >= 0
                            && n.IndexOf("compt", StringComparison.OrdinalIgnoreCase) >= 0);
                }))
                .Step($"DCA-GRID : Saisir dossier '{numGestion}' + Tab", d => d.EnterNumGestionInActiveTab(numGestion, "tab DCA"))
                .Step("DCA-GRID : Capturer avant/après focus", d => d.CaptureDcaDateCellFocusOracle(
                    Environment.GetEnvironmentVariable("RIG_DCA_EXPECTED_DATE") ?? "31/12/2024"))
                .Build(),

            // ── raptuval-inspect — outil de DIAG : à utiliser avec RIG_LEGACY_EXE=PROC_RAPTUVAL_EXE.exe.
            //    Après le login (fait par le harnais), laisse vivre le cockpit jusqu'à 90 s pour passer
            //    l'overlay de chargement, puis le screenshot final fullScreen (RunLegacyKbisScenario)
            //    capture l'état réel de la grille. Poll borné (pas de condition UIA fiable sur l'overlay
            //    custom RIG : le hwnd overlay/MainWindow varie — cf. checkpoint 5 du run rapture).
            ScenarioBuilder.New("raptuval-inspect", "DIAG cockpit RAPTUVAL : attente rendu grille + screenshot final")
                .Module("kbis")
                .Step("Attendre le rendu de la grille (poll 500ms, cap 90s)", d =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 90)
                        System.Threading.Thread.Sleep(500); // sleep-ok: poll settle-UI borné — overlay custom RIG sans condition UIA pollable, le verdict est le screenshot final
                })
                .Build(),

            // ── raptuval-cockpit — DIAG : lance RAPTUVAL par le VRAI chemin utilisateur (Console
            //    d'accueil → scan menu → activation), là où le standalone PROC_RAPTUVAL_EXE reste
            //    sur son overlay (cf. run rapture checkpoint 6 : le cycle demande→étape n'est
            //    orchestré que par la Console). Verdict = tab ouvert (WaitForProcessusTabLoaded)
            //    + screenshot final fullScreen après settle.
            ScenarioBuilder.New("raptuval-cockpit", "DIAG cockpit RAPTUVAL via Console d'accueil : nav menu + rendu grille")
                .Module("kbis")
                .Step("Ouvrir RAPTUVAL depuis la Console d'accueil (scan menu)", d =>
                    d.OpenProcessus("RAPTUVAL", name =>
                    {
                        var n = (name ?? "").Trim();
                        if (n.Length == 0) return false;
                        var firstToken = n.Split(' ')[0];
                        if (firstToken.Equals("RAPTUVAL", StringComparison.OrdinalIgnoreCase)) return true;
                        return n.IndexOf("validation", StringComparison.OrdinalIgnoreCase) >= 0
                            && n.IndexOf("rapture", StringComparison.OrdinalIgnoreCase) >= 0;
                    }))
                .Step("Basculer sur le tab du processus (≠ Accueil)", d => d.SelectLastNonAccueilTab())
                .Step("Attendre le rendu de la grille (poll 500ms, cap 45s)", d =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 45)
                        System.Threading.Thread.Sleep(500); // sleep-ok: settle-UI borné post-ouverture tab — verdict = screenshot final
                })
                .Build(),

            // ── raptuval-ecarter / raptuval-reactiver — e2e des boutons du cockpit (judge Faithful :
            //    « boutons jamais exécutés e2e »). Ouvre le cockpit via Console, sélectionne la 1ʳᵉ ligne,
            //    clique l'action, confirme. VÉRIFICATION = transition RAPTU_ETAT en DB (côté PowerShell
            //    appelant : Écarter → état 3 ; Réactiver → état 1). Paramétrés par le libellé du bouton.
            //
            //    ⚠⚠ FAUX-VERT CONNU EN HEADLESS (mur harnais, vécu 2026-07-02) : en HDESK Mode B le clic
            //    ne ROUTE PAS (SelectionItem UIA ne peuple pas SelectedRows FullRowSelect ; le clic physique
            //    n'atteint pas le bouton) → le scénario peut rendre N/0 passed SANS AUCUNE transition DB.
            //    NE JAMAIS conclure sur l'exit-code seul : exiger la transition RAPTU_ETAT en DB.
            //    Alternative PROUVÉE (2026-07-06) : pilotage bureau RÉEL par clics natifs →
            //    voir `raptuval-live-drive.ps1` (racine RIG-TV) — Écarter → DB 1→3 confirmé.
            ScenarioBuilder.New("raptuval-ecarter", "e2e bouton Écarter du cockpit RAPTUVAL (⚠ faux-vert possible en headless — exiger la transition DB)")
                .Module("kbis")
                .Step("Ouvrir RAPTUVAL (Console) puis Écarter la 1ʳᵉ ligne", d =>
                {
                    d.OpenProcessus("RAPTUVAL", n => RaptuvalMatch(n));
                    d.SelectLastNonAccueilTab();
                    System.Threading.Thread.Sleep(3000); // sleep-ok: settle grille avant sélection ligne
                    if (!d.DriveCockpitAction("écarter"))
                        throw new Exception("Écarter : action non exécutée (ligne ou bouton introuvable)");
                })
                .Build(),

            ScenarioBuilder.New("raptuval-reactiver", "e2e bouton Réactiver du cockpit RAPTUVAL")
                .Module("kbis")
                .Step("Ouvrir RAPTUVAL (Console) puis Réactiver la 1ʳᵉ ligne", d =>
                {
                    d.OpenProcessus("RAPTUVAL", n => RaptuvalMatch(n));
                    d.SelectLastNonAccueilTab();
                    System.Threading.Thread.Sleep(3000); // sleep-ok: settle grille avant sélection ligne
                    if (!d.DriveCockpitAction("réactiver"))
                        throw new Exception("Réactiver : action non exécutée (ligne ou bouton introuvable)");
                })
                .Build(),

            // ── mjud — PILOTE « proc à données » du chantier tester-tous-les-procs (2026-07-09).
            //    PROC_MJUD = Modification d'Instance Judiciaire (⚠ PAS Mandataire ; ≠ MJUDS voisin du même
            //    sous-menu). Plugin DLL complexe ouvrant sur EtapeRechercheInstance. INC-A.1 = ouverture
            //    READ-ONLY : ouvrir MJUD via la Console (scan menu, matcher excluant MJUDS), basculer sur son
            //    tab, settle → VERDICT = capture finale (l'écran de recherche d'instance doit s'afficher sans
            //    crash). ⚠ READ-ONLY STRICT : aucune validation/enregistrement (MJUD modifie des instances).
            //    La recherche+sélection d'une instance = INC-A.2 (après inspection UIA de l'écran de recherche).
            ScenarioBuilder.New("mjud", "PROC_MJUD : ouverture (Modification instance judiciaire) — READ-ONLY, écran de recherche")
                .Module("judiciaire")
                .Step("MJUD : Ouvrir PROC_MJUD depuis la Console d'accueil (scan menu, exclut MJUDS)", d =>
                    d.OpenProcessus("MJUD", name =>
                    {
                        var n = (name ?? "").Trim();
                        if (n.Length == 0) return false;
                        var first = n.Split(' ')[0];
                        if (first.Equals("MJUDS", StringComparison.OrdinalIgnoreCase)) return false; // proc voisin (délibéré)
                        if (first.Equals("MJUD", StringComparison.OrdinalIgnoreCase)) return true;
                        var low = n.ToLowerInvariant();
                        return low.Contains("modification") && low.Contains("instance")
                            && !low.Contains("délibéré") && !low.Contains("delibere");
                    }))
                .Step("MJUD : Basculer sur le tab du processus (≠ Accueil)", d => d.SelectLastNonAccueilTab())
                .Step("MJUD : Laisser l'écran de recherche d'instance se rendre (poll 500ms, cap 10s)", d =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 10)
                        System.Threading.Thread.Sleep(500); // sleep-ok: settle-UI borné écran de recherche
                })
                // INC-A.2 — recherche+sélection READ-ONLY (charge l'écran de modif, ne valide RIEN).
                // Num instance DEV existant (SQL TOP 1, 2026-07-09) ; ⚠ pose un verrou (Lock=écriture DB DEV)
                // levé à la fermeture + nettoyé par ClearMyEnCoursLocks au scénario suivant.
                // N° de RÔLE métier (INSTN_NUM_ROLE), PAS la PK INSTN_ID_INSTN (498164 = faux-vert vécu e2e-3 :
                // masque champ → « 2026OP498164 » → recherche invalide). Instance DEV 498164 → rôle 2026F00686 (SQL 2026-07-09).
                .Step("MJUD : Rechercher l'instance 2026F00686 (READ-ONLY — verrou nettoyé au run suivant)",
                      d => d.SearchInstanceMjud("2026F00686"))
                .Step("MJUD : Laisser l'écran de modification se rendre (poll 500ms, cap 20s)", d =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 20)
                        System.Threading.Thread.Sleep(500); // sleep-ok: settle-UI borné écran de modification post-recherche
                })
                // .Expect DISCRIMINANT (anti faux-vert) : la recherche doit avoir ABOUTI (écran de recherche
                // quitté), sinon FAIL honnête au lieu d'un PASS mensonger.
                .Expect("MJUD : l'instance est chargée en modification (écran de recherche quitté)",
                        ctx => ctx.Driver.MjudInstanceLoaded())
                .Build(),

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
        list.AddRange(ChargeOnlyCatalog.All());          // INC-B : smoke charge-only des procs de l'allowlist opt-in
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
