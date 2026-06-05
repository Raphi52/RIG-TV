using System.Collections.Generic;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Catalogue statique des scénarios du smoke legacy WinForms (<c>RigClientAccueil.exe</c>).
/// Active via <c>Rig.Wpf.Kbis.SmokeRunner.exe --legacy</c>.
///
/// Backend="legacy" → tag automatique <c>legacy</c> dans le ScenarioViewModel.
/// Scénarios fonctionnels (par module métier) : ExtraTags ajoute "kbis", "mandataire"…
/// pour qu'ils apparaissent sous le bon module dans le TestViewer.
/// </summary>
public static class LegacySmokeCatalog
{
    public static IReadOnlyList<SmokeScenario> All { get; } = new List<SmokeScenario>
    {
        // ── Smoke "Global" (cross-fonctionnel, prérequis tous modules) ─────
        new("Setup",  "Sanity : RigClientAccueil.exe présent",
            new[] { "Sanity : RigClientAccueil.exe présent" },
            "Vérifie que le binaire legacy est livré à C:\\rig\\exe\\ (override possible " +
            "via la variable d'env RIG_LEGACY_EXE). Pré-requis trivial mais utile si la " +
            "machine n'a jamais déployé le legacy.",
            backend: "legacy"),

        new("UI",     "Lancement RIG legacy : démarre et main window apparaît",
            new[] { "Lancement RIG legacy" },
            "Launch RigClientAccueil.exe via FlaUI, attend la main window jusqu'à 60s. " +
            "Si elle n'apparaît pas, c'est probablement un boot bloqué : TypeLoadException " +
            "(strong-name mismatch), GAC pas peuplé, dépendance native manquante " +
            "(Vintasoft, IKVM, LibreOffice cli_*.dll), ou license check qui timeout.",
            backend: "legacy"),

        new("UI",     "Click 'Se connecter' : app passe le login et reste vivante",
            new[] { "Click 'Se connecter'" },
            "Cherche le bouton 'Se connecter' (variantes : Connexion / OK) dans FormLogin, " +
            "click, attend 20s post-login, vérifie que le process est toujours vivant ET " +
            "qu'une main window est encore accessible (FormAccueil prend le focus après " +
            "que FormLogin se ferme). La connection cible la base 9995 (config dev par " +
            "défaut du legacy).",
            backend: "legacy"),

        // ── Smoke KBIS legacy (processus VK = "Visualisation - Extrait RCS") ──
        // ⚠ CORRECTION 2026-05-18 : le bon processus de consultation K-bis est
        //   VK ("Visualisation - Extrait RCS"). L'ancien matcher attrapait par
        //   erreur XXKBIS = "Suppression d'un dossier" (destructeur !). Les
        //   scénarios ci-dessous testent désormais VK.
        // ──────────────────────────────────────────────────────────────────────
        // 2 SCENARIOS INDÉPENDANTS — paralellisables (chacun = 1 RIG + 1 HDESK isolé)
        // 2026-05-28 refactor : remplace 9 entries-steps par 2 scenarios self-contained.
        // Mode CLI dédié :
        //   SmokeRunner --legacy-kbis-vk   → flow VK complet (~30s/scenario)
        //   SmokeRunner --legacy-kbis-xex  → flow XEX complet (~50s/scenario)
        // Self-snap PNGs par-scenario : self-snaps/$RUN_STAMP/kbis-vk/  et /kbis-xex/
        // ──────────────────────────────────────────────────────────────────────
        new("UI",     "KBIS legacy PROC_VK — Visualisation extrait RCS depuis num_gestion",
            new[] { "VK : Saisir numéro de gestion" /* ligne finale du worker VK */ },
            "Scenario indépendant lancé via --legacy-kbis-vk. Spawns son propre RIG sur HDESK " +
            "isolé (HEADLESS=1) : Launch → Login RIG → OpenProcKbis (tab VK auto) → saisie " +
            "directe du numéro de gestion (default 2024B00001, override RIG_LEGACY_NUM_GESTION) → " +
            "Tab → RIG résout le dossier sans passer par la modale Recherche. " +
            "Self-snap PNGs (500ms) dans %LOCALAPPDATA%\\rig-wpf-testviewer\\self-snaps\\$RUN_STAMP\\kbis-vk\\. " +
            "Parallélisable avec PROC_XEX selon settings parallelism.",
            backend: "legacy", extraTags: new[] { "kbis" }),

        new("UI",     "KBIS legacy PROC_XEX — Édition K-bis Brouillon (NE PAS IMPRIMER)",
            new[] { "XEX : Décoche imprimante (NE PAS IMPRIMER)" /* ligne finale du worker XEX */ },
            "Scenario indépendant lancé via --legacy-kbis-xex. Spawns son propre RIG sur HDESK " +
            "isolé : Launch → Login → OpenProcXex → saisie num_gestion → Alt+V Valider → " +
            "tableau d'édition apparaît → décocher imprimante (GUARD UIA Value='False' AVANT " +
            "Click Valider pour empêcher l'impression physique) → Imprimante passe 'Proximité' → " +
            "'Brouillon' → Alt+V → Brouillon généré (File différée, popup fermée). " +
            "Self-snap PNGs dans self-snaps/$RUN_STAMP/kbis-xex/.",
            backend: "legacy", extraTags: new[] { "kbis" }),

        // ── Smoke ALERTES RCS legacy (reprise interrompue / réclamation) ──────
        // 4 SCENARIOS INDÉPENDANTS — parallélisables (chacun = 1 RIG + 1 HDESK isolé).
        // Accès via la tuile "Alertes RCS" de la Console d'accueil (lstAlertes), PAS un PROC.
        // Chaque scenario : Launch → Login → ouvrir l'alerte → trouver la 1ère demande du type
        // (formalités J00… ou DCADEMAT) → double-clic → vérifier ouverture sans crash + DocDemat.
        // Réclamation : en plus, reprendre les impressions / pool d'éditions → ouvrir le courrier.
        // Modes CLI : --legacy-alertes-int-form / -int-dca / -rec-form / -rec-dca.
        // Self-snap PNGs par-scenario : self-snaps/$RUN_STAMP/alertes-<type>/.
        // ──────────────────────────────────────────────────────────────────────
        new("UI",     "Alertes RCS — Reprise interrompue (formalités J00)",
            new[] { "INT-FORM : Double-clic demande" /* ligne terminale (Étape 3) */ },
            "Scenario indépendant lancé via --legacy-alertes-int-form. Spawns son propre RIG sur " +
            "HDESK isolé : Launch → Login → tuile 'Alertes RCS' → alerte 'Demandes interrompues' → " +
            "1ère demande de formalités (N° liaison J00…) → double-clic → vérifie ouverture sans " +
            "crash + DocDemat ouvert (diff process/fenêtre/fichier) → ferme la demande. " +
            "Self-snap PNGs dans self-snaps/$RUN_STAMP/alertes-int-form/.",
            backend: "legacy", extraTags: new[] { "alertes" }),

        new("UI",     "Alertes RCS — Reprise interrompue (DCADEMAT)",
            new[] { "INT-DCA : Double-clic demande" },
            "Scenario indépendant lancé via --legacy-alertes-int-dca. Launch → Login → tuile " +
            "'Alertes RCS' → alerte 'Demandes interrompues' → 1ère demande DCADEMAT → double-clic → " +
            "vérifie ouverture sans crash + DocDemat ouvert → ferme. " +
            "Self-snap PNGs dans self-snaps/$RUN_STAMP/alertes-int-dca/.",
            backend: "legacy", extraTags: new[] { "alertes" }),

        new("UI",     "Alertes RCS — Reprise réclamation (formalités J00)",
            new[] { "REC-FORM : Courrier de réclamation" },
            "Scenario indépendant lancé via --legacy-alertes-rec-form. Launch → Login → tuile " +
            "'Alertes RCS' → alerte 'Demandes en réclamations > N jours' → 1ère demande de formalités " +
            "(J00…) → double-clic → DocDemat → clic droit 'Reprendre les impressions' → ouvrir le " +
            "'courrier de réclamation' et vérifier qu'il s'affiche → ferme. " +
            "Self-snap PNGs dans self-snaps/$RUN_STAMP/alertes-rec-form/.",
            backend: "legacy", extraTags: new[] { "alertes" }),

        new("UI",     "Alertes RCS — Reprise réclamation (DCADEMAT)",
            new[] { "REC-DCA : Lettre de réclamation" },
            "Scenario indépendant lancé via --legacy-alertes-rec-dca. Launch → Login → tuile " +
            "'Alertes RCS' → alerte 'Demandes en réclamations > N jours' → 1ère demande DCADEMAT → " +
            "double-clic → DocDemat → clic droit 'Lancer le pool d'éditions' (onglet POOL_EDIT) → " +
            "ouvrir la 'Lettre de réclamation' (Voir le document) et vérifier qu'elle s'affiche → ferme. " +
            "Self-snap PNGs dans self-snaps/$RUN_STAMP/alertes-rec-dca/.",
            backend: "legacy", extraTags: new[] { "alertes" }),

        // ── Module DCADEMAT (2026-05-29) ──────────────────────────────────
        // 8 tuiles : DCA + Formalité Demat × validation/réclamation/refus/interrompue.
        // v1 = Ouvrir l'alerte RCS + Ouvrir la 1ère demande (réutilise OpenAlerteRcs +
        // OpenFirstDemandeAndVerify). Le step métier "Action" (Valider Alt+V / Réclamer Alt+R /
        // Refuser / Interrompre + lecture n° dépôt/facture/demande) = Étape 3, à éprouver avec
        // supervision. ⚠ La réclamation déclenche un aperçu avant impression (garde NE PAS IMPRIMER).
        // Modes CLI : --legacy-dcademat-{dca|form}-{validation|reclamation|refus|interrompue}.
        new("UI", "DCADEMAT — DCA : Validation (Alt+V)",
            new[] { "DCA-VALIDATION : Ouvrir demande" },
            "Scenario --legacy-dcademat-dca-validation : Launch → Login → alerte 'DCA démat en attente' → " +
            "ouvrir une demande DCADEMAT (Configurer le dépôt). v1 s'arrête là. TODO Étape 3 : cocher la " +
            "case 'DCA' + Valider (Alt+V) + vérifier n° dépôt/facture/demande.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — DCA : Réclamation (motif INPMANQ)",
            new[] { "DCA-RECLAMATION : Action" },
            "Scenario --legacy-dcademat-dca-reclamation : alerte 'réclamation' → ouvrir une demande DCADEMAT " +
            "(robustesse anti-verrou) → motif INPMANQ + modifier texte (ajout 'TEST') + Réclamer (Alt+R) + " +
            "vérifier le courrier (PdfPig / signal). ⚠ aperçu avant impression → NE PAS IMPRIMER.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — DCA : Refus",
            new[] { "DCA-REFUS : Action" },
            "Scenario --legacy-dcademat-dca-refus : alerte 'réclamation' (le bouton REFUS coexiste avec " +
            "RECLAMATION sur l'étape Réclamation/Refus — PAS sur 'Configurer le dépôt') → ouvrir une demande " +
            "DCADEMAT → Refuser (RigToolBar REFUS, Alt+F) + vérif (RIG vivant + courrier de refus). " +
            "⚠ aperçu avant impression → NE PAS IMPRIMER. (refus = action, pas une alerte ; overridable RIG_DCADEMAT_ALERTE_DCA_REFUS.)",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — DCA : Interrompue",
            new[] { "DCA-INTERROMPUE : Ouvrir demande" },
            "Scenario --legacy-dcademat-dca-interrompue : alerte 'interrompue' → ouvrir une demande DCADEMAT. " +
            "TODO Étape 3 : mise en interrompue.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — Formalité Demat : Validation",
            new[] { "FORM-VALIDATION : Action" },
            "Scenario --legacy-dcademat-form-validation : alerte 'DEMAT INPI – Formalités' (overridable " +
            "RIG_DCADEMAT_ALERTE_FORM_VALIDATION) → ouvrir une formalité démat (J00) → Valider la formalité " +
            "(bouton RigToolBar, F12) + vérif sans aperçu (RIG vivant + n° demande best-effort). NE PAS IMPRIMER.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — Formalité Demat : Réclamation",
            new[] { "FORM-RECLAMATION : Ouvrir demande" },
            "Scenario --legacy-dcademat-form-reclamation : ouvrir une formalité démat (J00). TODO Étape 3 : " +
            "réclamation. ⚠ aperçu avant impression → NE PAS IMPRIMER.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — Formalité Demat : Refus",
            new[] { "FORM-REFUS : Action" },
            "Scenario --legacy-dcademat-form-refus : alerte 'réclamation' (réutilise le chemin J00 prouvé par " +
            "form-reclamation ; Refuser est sur la toolbar principale de la formalité) → ouvrir une formalité " +
            "démat (J00) → Refuser (RigToolBar REFUS, Alt+F) + vérif (RIG vivant + courrier de refus). " +
            "NE PAS IMPRIMER. Overridable RIG_DCADEMAT_ALERTE_FORM_REFUS.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        new("UI", "DCADEMAT — Formalité Demat : Interrompue",
            new[] { "FORM-INTERROMPUE : Ouvrir demande" },
            "Scenario --legacy-dcademat-form-interrompue : ouvrir une formalité démat (J00). TODO Étape 3 : interrompue.",
            backend: "legacy", extraTags: new[] { "dcademat" }),

        // ── Smoke Rapture legacy ──────────────────────────────────────────
        new("UI",     "Rapture legacy : ouvrir PROC_RETAUD depuis la Console d'accueil",
            new[] { "Rapture legacy : ouvrir PROC_RETAUD" },
            "Scan onglets btn1..btn7 × lstSousmenu × lstProcessus pour trouver un item dont " +
            "le Name matche 'retaud' ou 'retour audience' / 'retour cabinet'. Double-clic = " +
            "launch. Si introuvable, soit le code processus a une convention différente dans " +
            "ce greffe (XXRETAUD attendu), soit le binary PROC_RETAUD.dll deployé est antérieur " +
            "au merge rapture-import.",
            backend: "legacy", extraTags: new[] { "rapture" }),

        new("UI",     "Rapture legacy : sélectionner une audience pour passer en phase Saisie",
            new[] { "Rapture legacy : sélectionner une audience" },
            "PROC_RETAUD démarre en phase Recherche (form de sélection d'audience). Les boutons " +
            "'Importer Rapture' / 'Voir données Rapture' sont en eButtonVisibleOnPhase.Saisie — " +
            "donc invisibles tant qu'on n'a pas validé une audience. Ce step click la 1ère row " +
            "+ 'Valider la sélection' pour avancer en phase Saisie.",
            backend: "legacy", extraTags: new[] { "rapture" }),

        new("UI",     "Rapture legacy : bouton 'Importer Rapture' présent dans la toolbar",
            new[] { "Rapture legacy : bouton 'Importer Rapture'" },
            "Après ouverture de PROC_RETAUD, cherche dans la window un control (Button OU " +
            "Pane=RigButton) dont le Name contient 'Importer Rapture'. C'est la preuve que " +
            "PROC_RETAUD.dll post-merge rapture-import est bien chargé par RigClientAccueil. " +
            "Pas de click ici (le bouton est en phase 'Saisie', nécessite une audience chargée).",
            backend: "legacy", extraTags: new[] { "rapture" }),

        new("UI",     "Rapture legacy : bouton 'Voir données Rapture' présent dans la toolbar",
            new[] { "Rapture legacy : bouton 'Voir données Rapture'" },
            "Idem : 2ème bouton ajouté par rapture-import — ouvre FormRaptureNotesViewer en " +
            "lecture seule sur les NOTE_PROCEDURE 'RAPTURE_*' déjà persistées.",
            backend: "legacy", extraTags: new[] { "rapture" }),

        new("UI",     "Rapture legacy : ouvrir PROC_PREAUD depuis la Console d'accueil",
            new[] { "Rapture legacy : ouvrir PROC_PREAUD" },
            "Idem PROC_RETAUD mais pour l'EXPORT (RIG → Rapture pré-audience). Scan menu pour " +
            "item 'preaud' / 'edition plumitif' / 'préparation audience'. Double-clic = launch.",
            backend: "legacy", extraTags: new[] { "rapture" }),

        new("UI",     "Rapture legacy : bouton 'Export JSON' Plumitif présent",
            new[] { "Rapture legacy : bouton 'Export JSON'" },
            "Le bouton 'Export JSON' (AutomationId BtnExportJsonPlum, type Ult_Button = " +
            "Pane UIA) est wired par rapture-export dans OPE_EDIT_PLUMITIF. Son clic appelle " +
            "PlumitifJsonExporter pour produire le JSON consommé par Rapture côté pré-audience. " +
            "Preuve que PROC_PREAUD.dll post-merge rapture-export est bien chargé.",
            backend: "legacy", extraTags: new[] { "rapture" }),
    };
}
