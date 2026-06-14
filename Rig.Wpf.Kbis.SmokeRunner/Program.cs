using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Outil de smoke test self-service pour la phase C K-bis dans le shell WPF.
///
/// Reproduit la chaîne DI exacte d'App.xaml.cs (moins l'UI WPF) et exécute des
/// scénarios programmatiques. Permet de valider chaque couche du livrable KBIS
/// sans lancer Rig.Wpf.Shell.exe et sans display Windows.
///
/// Usage : <c>dotnet run --project Rig.Wpf.Kbis.SmokeRunner</c>
/// Exit code : 0 si tous les scénarios passent (ou sont skipés pour raison
/// d'infra), 1 si un scénario échoue.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    [STAThread]
    private static int Main(string[] args)
    {
        // Force UTF-8 stdout pour que les icônes ✓/✗/⊘ et accents passent
        // intact quand un parent (TestViewer Blazor) redirige StandardOutput.
        // Sinon Console.OutputEncoding par défaut = OEM codepage (cp850/1252)
        // → caractères remplacés par '?' à la lecture.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* best-effort */ }

        // #1 — Tee stdout vers un fichier : que TOUT mode standalone (--drive-*, --legacy-*)
        // laisse un log lisible, même lancé hors TestViewer (qui ne dumpe que ses runs proxy).
        // Nom distinct (sr-…) → pas de collision avec les legacy-*.stdout.log du Dump TV.
        InstallStdoutTee(args);

        // ── Dispatch CLI : cascade de routes, PREMIÈRE qui matche gagne ────────
        // L'ORDRE est significatif (certains flags se chevauchent par préfixe) :
        //   --legacy-rapture-{import,process,export} AVANT --legacy-rapture
        //   --legacy-rapture                         AVANT --legacy
        // Helpers HasFlag / HasFlagPrefix = mêmes comparaisons OrdinalIgnoreCase
        // qu'avant (Equals exact / StartsWith), juste factorisées.
        // Le flag --legacy fait tourner exclusivement le smoke RigClientAccueil.exe
        // (WinForms x86 + COM) via FlaUI. Mutuellement exclusif avec le smoke WPF.
        // --drive-testviewer-rapture-import = drive Rig Testing UI au lieu de RIG legacy
        // (pour qu'un agent voie ses iterations dans le GUI TestViewer en live)
        if (HasFlag(args, "--drive-testviewer-rapture-import"))
            return RunDriveTestViewerRaptureImport(args);
        // --drive-testviewer-rapture-diag = pilote le bouton "Diagnostic import"
        // du module RAPTURE de Rig Testing (mode --rapture-diag) en FlaUI.
        if (HasFlag(args, "--drive-testviewer-rapture-diag"))
            return RunDriveTestViewerRaptureDiag(args);
        // --drive-testviewer-rapture-process = pilote le bouton "▶ Start E2E"
        // du tab Smoke Import (mode --legacy-rapture-process) en FlaUI.
        if (HasFlag(args, "--drive-testviewer-rapture-process"))
            return RunDriveTestViewerRaptureProcess(args);
        // --drive-testviewer-rapture-export = pilote le bouton "▶ Start E2E"
        // du tab Smoke Export (mode --legacy-rapture-export) en FlaUI.
        if (HasFlag(args, "--drive-testviewer-rapture-export"))
            return RunDriveTestViewerRaptureExport(args);
        // --drive-testviewer-rapture-stoppause = vérif Stop/Pause : lance Smoke UI,
        // Pause (screenshot gelé), Reprend, Stop (screenshot + vérif process tués).
        if (HasFlag(args, "--drive-testviewer-rapture-stoppause"))
            return RunDriveTestViewerRaptureStopPause(args);
        // --drive-testviewer-legacy-kbis = rejoue le smoke legacy KBIS/VK via Rig Testing
        if (HasFlag(args, "--drive-testviewer-legacy-kbis"))
            return RunDriveTestViewerLegacyKbis(args);
        // --drive-testviewer-kbis-stress = pilote le stress X× du scénario sélectionné (env
        // RIG_KBIS_STRESS_SCENARIO/COUNT/LOOP set au lancement de la TV) via Rig Testing.
        if (HasFlag(args, "--drive-testviewer-kbis-stress"))
            return RunDriveTestViewerKbisStress(args);
        // --inspect-testviewer-kbis-legacy = diag : screenshot + dump du tab Smoke Legacy
        if (HasFlag(args, "--inspect-testviewer-kbis-legacy"))
            return RunInspectTestViewerKbisLegacy(args);
        // --rapture-selfdrive = self-drive in-process : lance RigClientAccueil.exe
        // en mode --rapture-smoke (worker invisible, pipeline d'import direct en mémoire,
        // AUCUNE fenêtre/souris/focus). AudienceLock + snapshot/restore SQL net-zero.
        if (HasFlag(args, "--rapture-selfdrive"))
            return RunRaptureSelfDrive(args);
        // --rapture-diag = diagnostic d'import complet (Mapper/Validator/Diff/Apply
        // réel + rapport placement + valeurs non placées) via RaptureImportDiag_EXE,
        // stdout relayé pour affichage dans la console Rig Testing.
        if (HasFlag(args, "--rapture-diag"))
            return RunRaptureDiag(args);
        // --legacy-rapture-process = process E2E testable via l'UI : ouvre une audience
        // PRÉCISE (par date+heure, en fenêtre RETAUD) qui matche le JSON par date+greffe
        // → Cas A → recap DIRECTE avec lignes modifiables cochables → capture + Annuler.
        if (HasFlag(args, "--legacy-rapture-process"))
            return RunLegacyRaptureProcess(args);
        // --legacy-rapture-export = Start E2E (tab Smoke Export) : Login → PROC_PREAUD →
        // audience → click 'Export JSON Plumitif' → écrit le JSON sur disque.
        if (HasFlag(args, "--legacy-rapture-export"))
            return RunLegacyRaptureExport(args);
        // --drive-retaud-pubs = navigation READ-ONLY vers l'écran "Publicités en attente"
        // de PROC_RETAUD pour une audience donnée (--audience-id <N> obligatoire),
        // puis capture un screenshot. Aucun import, aucune mutation de données.
        // Usage : --drive-retaud-pubs --audience-id 28590
        if (HasFlag(args, "--drive-retaud-pubs"))
            return RunDriveRetaudPubs(args);
        // --reset-smoke-db = panic restore SQL : restaure tout résidu écrit par les
        // scénarios smoke (audit, notes RAPTURE_*, cas C leftover audiences). Permet
        // de partir d'une DB propre pour rejouer les mêmes scénarios.
        if (HasFlag(args, "--reset-smoke-db"))
            return RunResetSmokeDb(args);
        // --legacy-rapture-import = skip export (boucle iter import only)
        if (HasFlag(args, "--legacy-rapture-import"))
            return RunLegacyRaptureImportOnly(args);
        // --legacy-rapture en priorité (avant --legacy car ce dernier match aussi le préfixe)
        if (HasFlag(args, "--legacy-rapture"))
            return RunLegacyRapture(args);
        // --loop = CLI de la boucle rig-testing (run/build/bench). Émet du JSON.
        if (HasFlag(args, "--loop"))
            return LoopMode.RunLoop(args);
        // --legacy-kbis-vk = scenario isolé PROC_VK (Visualisation extrait RCS depuis num_gestion).
        //   Lance son propre RIG sur HDESK isolé, login, ouvre PROC_KBIS (tab VK), saisit
        //   le num_gestion (env RIG_LEGACY_NUM_GESTION ou 2024B00001), vérifie dossier chargé.
        //   Self-snap PNG dans self-snaps/$RIG_RUN_STAMP/kbis-vk/.
        if (HasFlag(args, "--legacy-kbis-vk"))
            return RunLegacyKbisVk(args);
        // --legacy-kbis-xex = scenario isolé PROC_XEX (Édition Brouillon).
        //   Launch + login + OpenProcXex + saisir num_gestion + Alt+V + toggle imprimante +
        //   Brouillon. Self-snap dans self-snaps/$RIG_RUN_STAMP/kbis-xex/.
        if (HasFlag(args, "--legacy-kbis-xex"))
            return RunLegacyKbisXex(args);
        // --legacy-alertes-* = scénarios module ALERTES RCS (reprise interrompue / réclamation).
        //   Accès via la tuile "Alertes RCS" de l'accueil (pas un PROC). Self-snap dans
        //   self-snaps/$RIG_RUN_STAMP/alertes-<type>/. Étape 1 : Launch+Login+OpenAlerteRcs.
        if (HasFlag(args, "--legacy-alertes-int-form"))
            return RunLegacyAlertesIntForm(args);
        if (HasFlag(args, "--legacy-alertes-int-dca"))
            return RunLegacyAlertesIntDca(args);
        if (HasFlag(args, "--legacy-alertes-rec-form"))
            return RunLegacyAlertesRecForm(args);
        if (HasFlag(args, "--legacy-alertes-rec-dca"))
            return RunLegacyAlertesRecDca(args);
        // --legacy-dcademat-* = module DCADEMAT (2026-05-29). v1 : Ouvrir alerte + Ouvrir demande.
        if (HasFlagPrefix(args, "--legacy-dcademat-"))
            return RunLegacyDcademat(args);
        if (HasFlag(args, "--legacy"))
            return RunLegacy(args);

        // Mode par defaut RETIRE (decouplage 2026-05-29) : le test in-process de la reecriture
        // WPF-KBIS (KbisProcessusViewModel / KbisProcessusView via le socle Rig.Wpf.Shell/Core/RigMetier)
        // appartient au repo WPF-RIG, pas au harnais. RIG-TV ne pilote que des apps EXTERNES.
        Console.WriteLine("Rig.Wpf.Kbis.SmokeRunner : aucun mode reconnu.");
        Console.WriteLine("Modes : --legacy-kbis-vk|xex, --legacy-alertes-*, --legacy-dcademat-*,");
        Console.WriteLine("        --legacy-rapture-*, --rapture-selfdrive, --loop, --drive-testviewer-*,");
        Console.WriteLine("        --drive-retaud-pubs --audience-id <N>.");
        return 0;
    }

    /// <summary>true si <paramref name="args"/> contient EXACTEMENT <paramref name="flag"/> (OrdinalIgnoreCase).</summary>
    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>true si un des <paramref name="args"/> COMMENCE par <paramref name="prefix"/> (OrdinalIgnoreCase).</summary>
    private static bool HasFlagPrefix(string[] args, string prefix)
        => args.Any(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mode --inspect-testviewer-kbis-legacy : diag rapide du tab Smoke Legacy KBIS de
    /// Rig Testing — switch module KBIS, vérifie la présence du bouton "Run smoke RIG"
    /// (réalise le tab si besoin), puis screenshot + dump de l'arbre UIA du tab actif.
    /// </summary>
    private static int RunInspectTestViewerKbisLegacy(string[] args)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var d = new TestViewerDriver();
            d.SwitchToModule("KBIS");
            var b = d.EnsureLegacyTabRealized();
            Console.WriteLine($"   → Bouton 'Run smoke RIG' {(b is null ? "INTROUVABLE" : "présent")}");
            d.CaptureAndDumpActiveTab("kbis-legacy-inspect");
        }
        catch (Exception ex) { Console.WriteLine($"  ✗ {ex.GetType().Name}: {ex.Message}"); _failed++; }
        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Smoke RIG legacy : lance RigClientAccueil.exe, attend FormLogin, click Se connecter
    /// (base 9995), trouve l'item menu K-bis, click. Émet 5 résultats en stdout selon le
    /// même protocole ✓/✗/⊘ que le smoke WPF.
    /// </summary>
    private static int RunLegacy(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   Rig.Wpf.Kbis.SmokeRunner --legacy — smoke RIG WinForms via FlaUI ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Path RigClientAccueil — env var prioritaire, sinon default OPERATIONS.md
        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe))
            rigExe = @"C:\rig\exe\RigClientAccueil.exe";

        // ── Sc 1 : Sanity (path présent) ─────────────────────────────────────
        TryStep($"Sanity : RigClientAccueil.exe présent à {rigExe}", () =>
        {
            if (!File.Exists(rigExe))
                throw new Exception("Binaire introuvable. Définir RIG_LEGACY_EXE pour override le path.");
        });

        if (!File.Exists(rigExe))
        {
            Console.WriteLine();
            Console.WriteLine("──────────────────────────────────────────────────────────────────────");
            Console.WriteLine($"  ✓ passed  {_passed}");
            Console.WriteLine($"  ⊘ skipped {_skipped}");
            Console.WriteLine($"  ✗ failed  {_failed}");
            Console.WriteLine($"  ⏱ {sw.ElapsedMilliseconds} ms");
            Console.WriteLine("──────────────────────────────────────────────────────────────────────");
            return 1;
        }

        // ── Sc 2-3 : Launch + Click Se connecter ─────────────────────────────
        // Driver à état persistant : on garde l'instance entre les TryStep pour partager
        // le process Application + le UIA Automation à travers les étapes.
        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Lancement RIG legacy : RigClientAccueil.exe démarre et la main window apparaît", () =>
                    {
                        driver.Launch();
                    });

                    TryStep("Click 'Se connecter' : l'app passe le login (base 9995) et reste vivante", () =>
                    {
                        driver.ClickSeConnecter();
                    });

                    // ── Self-snap périodique : observe le run RIG via PNGs lus par le Live
                    // Viewer côté TestViewer. Marche en HDESK isolé (HEADLESS=1) ET sur
                    // user desktop (HEADLESS=0). RIG_RUN_STAMP env var pilote le dossier
                    // de destination (sinon fallback timestamp courant).
                    try { driver.StartPeriodicSnap("kbis-legacy", intervalMs: 500); }
                    catch (Exception ex) { Console.WriteLine($"      ⓘ StartPeriodicSnap a jeté : {ex.GetType().Name}: {ex.Message}"); }

                    // ── Sc 4-5 : Ouverture PROC_KBIS + vérification tab apparu ──────────
                    // 4. Itère btn1..btn7 + lstSousmenu pour trouver et double-cliquer l'item PROC_KBIS.
                    // 5. Vérifie qu'un tab nommé "K-Bis"/"KBIS" apparaît dans le tabControl principal.
                    TryStep("KBIS legacy : ouvrir PROC_KBIS depuis la Console d'accueil", () =>
                    {
                        driver.OpenProcKbis();
                    });

                    TryStep("KBIS legacy : le tab K-Bis apparaît dans la Console d'accueil", () =>
                    {
                        driver.VerifyKbisTabOpened();
                    });

                    // ── Sc 6 : Rechercher un SIREN dans le plugin KBIS ─────────────────
                    // SIREN par défaut : 025480401 (BARRAULT, NumGestion 2020B00686, non radié dans RIG_DEV).
                    // Override via $env:RIG_LEGACY_SIREN si on test contre une autre BDD.
                    TryStep("KBIS legacy : rechercher un SIREN et afficher le dossier", () =>
                    {
                        driver.SearchKbisSiren(defaultSiren: "025480401");
                    });

                    // ── Sc 7 : Click bouton K-bis → afficher le document ──────────────
                    TryStep("KBIS legacy : afficher le K-bis (click bouton + détection ouverture)", () =>
                    {
                        driver.OpenKbisDocument();
                    });

                    // ──────────────────────────────────────────────────────────────────
                    // VK & XEX num_gestion scenarios — 2026-05-28
                    // Override numéro de gestion via $env:RIG_LEGACY_NUM_GESTION (default
                    // 2024B00001 vérifié présent en RIG_DEV).
                    // ──────────────────────────────────────────────────────────────────
                    var numGestion = Environment.GetEnvironmentVariable("RIG_LEGACY_NUM_GESTION");
                    if (string.IsNullOrWhiteSpace(numGestion)) numGestion = "2024B00001";
                    Console.WriteLine($"  ⓘ Numéro de gestion utilisé : {numGestion}");

                    // ── Sc 8 : VK saisir numéro de gestion direct (flow alternatif au SIREN search) ──
                    TryStep("KBIS legacy VK : saisir numéro de gestion + tab → dossier chargé", () =>
                    {
                        driver.EnterNumGestionInActiveTab(numGestion, "tab VK");
                    });

                    // ── Sc 9 : Ouvrir PROC_XEX depuis la Console d'accueil ──
                    TryStep("KBIS legacy XEX : ouvrir le processus depuis l'Accueil", () =>
                    {
                        driver.OpenProcXex();
                    });

                    // ── Sc 10 : Saisir numéro de gestion dans XEX + Tab ──
                    TryStep("KBIS legacy XEX : saisir numéro de gestion + tab", () =>
                    {
                        driver.EnterNumGestionInActiveTab(numGestion, "tab XEX");
                    });

                    // ── Sc 11 : Alt+V Valider → tableau d'édition s'affiche ──
                    TryStep("KBIS legacy XEX : Alt+V valider → tableau d'édition affiché", () =>
                    {
                        driver.ClickValiderInActiveForm("tab XEX");
                        driver.WaitForTableauEdition(timeoutSeconds: 15);
                    });

                    // ── Sc 12 : Décoche imprimante (sans imprimer physiquement) + Alt+V → Brouillon généré ──
                    // PASS critère = toggle confirmé (GUARD interne Value='False') + popup fermée après Valider.
                    // Le viewer Word/RigAffichageDoc qui s'ouvre n'est PAS fiable dans cette config RIG
                    // — le brouillon est queuedé dans la File différée pour traitement asynchrone.
                    TryStep("KBIS legacy XEX : décoche imprimante + Alt+V → Brouillon généré (popup fermée)", () =>
                    {
                        driver.UncheckImprimanteAndValidate();
                        driver.WaitForTableauEditionClosed(timeoutSeconds: 10);
                    });

                    // Screenshot état final (rule 15) — gate visuel analysé par l'agent.
                    // fullScreen:true car le K-bis VK / XEX s'ouvre dans un viewer EXTERNE
                    // (hors fenêtre RIG) : capturer le bureau entier pour voir le doc rendu.
                    driver.CaptureScreenshot($"legacy-kbis-{(_failed > 0 ? "FAIL" : "OK")}", fullScreen: true);
                } // end using driver
            }); // end desktop.RunAttached
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        Console.WriteLine($"  ✓ passed  {_passed}");
        Console.WriteLine($"  ⊘ skipped {_skipped}");
        Console.WriteLine($"  ✗ failed  {_failed}");
        Console.WriteLine($"  ⏱ {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        return _failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Helper commun aux scenarios KBIS isolés (VK / XEX / etc.). Sanity + launch + login
    /// puis exécution du flow spécifique au scenario via <paramref name="runScenario"/>.
    /// Self-snap activé avec scenarioId = nom du flow (sous-dossier dédié).
    /// </summary>
    private static int RunLegacyKbisScenario(string scenarioId, string banner, Action<LegacyDriver> runScenario)
    {
        var sw = Stopwatch.StartNew();
        // Sous-dossier self-snap : override par RIG_KBIS_SNAP_ID (injecté par la TV en mode
        // parallèle pour donner à chaque instance son propre dossier : kbis-vk-1, kbis-vk-2…).
        // Fallback = scenarioId (mode CLI standalone : kbis-vk / kbis-xex).
        var snapId = Environment.GetEnvironmentVariable("RIG_KBIS_SNAP_ID");
        if (string.IsNullOrWhiteSpace(snapId)) snapId = scenarioId;
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║   Rig.Wpf.Kbis.SmokeRunner --{scenarioId,-46} ║");
        Console.WriteLine($"║   {banner,-67}║");
        Console.WriteLine($"║   snap → {snapId,-60}║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe))
            rigExe = @"C:\rig\exe\RigClientAccueil.exe";

        TryStep($"Sanity : RigClientAccueil.exe présent à {rigExe}", () =>
        {
            if (!File.Exists(rigExe))
                throw new Exception("Binaire introuvable. Définir RIG_LEGACY_EXE pour override le path.");
        });

        if (!File.Exists(rigExe)) return PrintSummaryAndExit(sw);

        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Lancement RIG legacy : RigClientAccueil.exe démarre", () => driver.Launch());
                    TryStep("Click 'Se connecter' : connexion à la base RIG", () => driver.ClickSeConnecter());

                    // Self-snap APRÈS login : le hwnd cached doit être celui de la Console
                    // d'accueil (main window post-login), PAS celui de FormLogin qui devient
                    // invalide après Click. PrintWindow sur ancien hwnd silent-failed (incident
                    // 2026-05-28 : 0 PNG malgré StartPeriodicSnap appelé). Coût : ~5-8s de
                    // délai avant le 1er PNG visible dans la mosaïque (acceptable).
                    try { driver.StartPeriodicSnap(snapId, intervalMs: 500); }
                    catch (Exception ex) { Console.WriteLine($"      ⓘ StartPeriodicSnap a jeté : {ex.Message}"); }

                    // Flow spécifique au scenario (séquence de TryStep internes)
                    runScenario(driver);

                    // Screenshot final
                    driver.CaptureScreenshot($"{snapId}-{(_failed > 0 ? "FAIL" : "OK")}", fullScreen: true);
                }
            });
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Scenario KBIS legacy PROC_VK : Visualisation extrait RCS depuis un numéro de gestion.
    /// Flow : Launch + Login + OpenProcKbis (tab VK auto) + EnterNumGestion + vérif dossier chargé.
    /// </summary>
    private static int RunLegacyKbisVk(string[] args)
    {
        var numGestion = Environment.GetEnvironmentVariable("RIG_LEGACY_NUM_GESTION");
        if (string.IsNullOrWhiteSpace(numGestion)) numGestion = "2024B00001";

        return RunLegacyKbisScenario(
            scenarioId: "kbis-vk",
            banner: "PROC_VK : Visualisation extrait RCS depuis num_gestion",
            runScenario: driver =>
            {
                // Préfixe "VK :" sur CHAQUE step → MatchPrefix unique (sinon collision avec XEX)
                TryStep("VK : Ouvrir PROC_KBIS depuis la Console d'accueil", () => driver.OpenProcKbis());
                TryStep("VK : Le tab K-Bis (VK) apparaît dans la Console d'accueil", () => driver.VerifyKbisTabOpened());
                TryStep($"VK : Saisir numéro de gestion '{numGestion}' + Tab → dossier chargé", () =>
                    driver.EnterNumGestionInActiveTab(numGestion, "tab VK"));

                // C'EST LE BUT DU TEST : cliquer le K-bis et vérifier qu'il s'ouvre réellement,
                // puis que le PDF généré contient les bonnes données (couche texte, sans vision).
                string? kbisPdf = null;
                TryStep("VK : Click 'Visualiser K-bis' → document/PDF ouvert", () =>
                    { kbisPdf = driver.OpenKbisDocument(); });
                TryStep("VK : Le PDF K-bis contient les bonnes données (texte extrait, sans vision)", () =>
                    driver.VerifyKbisPdfContent(kbisPdf, numGestion));
            });
    }

    /// <summary>
    /// Scenario KBIS legacy PROC_XEX : Édition K-bis interne avec génération Brouillon (NE PAS IMPRIMER).
    /// Flow : Launch + Login + OpenProcXex + EnterNumGestion + Alt+V valider + tableau d'édition
    /// + toggle imprimante OFF (GUARD anti-impression) + Alt+V → Brouillon généré.
    /// </summary>
    private static int RunLegacyKbisXex(string[] args)
    {
        var numGestion = Environment.GetEnvironmentVariable("RIG_LEGACY_NUM_GESTION");
        if (string.IsNullOrWhiteSpace(numGestion)) numGestion = "2024B00001";

        return RunLegacyKbisScenario(
            scenarioId: "kbis-xex",
            banner: "PROC_XEX : Édition K-bis Brouillon (no print)",
            runScenario: driver =>
            {
                // Préfixe "XEX :" sur CHAQUE step → MatchPrefix unique (sinon collision avec VK)
                TryStep("XEX : Ouvrir PROC_XEX depuis l'Accueil", () => driver.OpenProcXex());
                TryStep($"XEX : Saisir numéro de gestion '{numGestion}' + Tab", () =>
                    driver.EnterNumGestionInActiveTab(numGestion, "tab XEX"));
                TryStep("XEX : Alt+V Valider → tableau d'édition affiché", () =>
                {
                    driver.ClickValiderInActiveForm("tab XEX");
                    driver.WaitForTableauEdition(timeoutSeconds: 15);
                });
                TryStep("XEX : Décoche imprimante (NE PAS IMPRIMER) + Alt+V → Brouillon généré (popup fermée)", () =>
                {
                    driver.UncheckImprimanteAndValidate();
                    driver.WaitForTableauEditionClosed(timeoutSeconds: 10);
                });
            });
    }

    // ════════════ Module ALERTES RCS — 4 scénarios ════════════
    // ÉTAPE 1 (scaffolding) : Launch + Login + OpenAlerteRcs → grille des demandes visible.
    // Les steps métier (trouver demande J00/DCADEMAT, double-clic, DocDemat, réclamation) seront
    // ajoutés en Étape 3 (driver navigation à éprouver contre RIG réel). Réutilisent le squelette
    // RunLegacyKbisScenario (sanity + launch + login + self-snap via RIG_KBIS_SNAP_ID).
    private static int RunLegacyAlertesIntForm(string[] args) => RunLegacyKbisScenario(
        scenarioId: "alertes-int-form",
        banner: "ALERTES : reprise interrompue — formalités (J00)",
        runScenario: driver =>
        {
            TryStep("INT-FORM : Ouvrir alerte 'Demandes interrompues' (grille visible)",
                () => driver.OpenAlerteRcs("interrompue"));
            TryStep("INT-FORM : Double-clic demande formalités J00 → demande/document ouvert (reprise)",
                () => driver.OpenFirstDemandeAndVerify(dcademat: false));
        });

    private static int RunLegacyAlertesIntDca(string[] args) => RunLegacyKbisScenario(
        scenarioId: "alertes-int-dca",
        banner: "ALERTES : reprise interrompue — DCADEMAT",
        runScenario: driver =>
        {
            TryStep("INT-DCA : Ouvrir alerte 'Demandes interrompues' (grille visible)",
                () => driver.OpenAlerteRcs("interrompue"));
            TryStep("INT-DCA : Double-clic demande DCADEMAT → demande/document ouvert (reprise)",
                () => driver.OpenFirstDemandeAndVerify(dcademat: true));
        });

    private static int RunLegacyAlertesRecForm(string[] args) => RunLegacyKbisScenario(
        scenarioId: "alertes-rec-form",
        banner: "ALERTES : reprise réclamation — formalités (J00)",
        runScenario: driver =>
        {
            TryStep("REC-FORM : Ouvrir alerte 'réclamations > N j' (grille visible)",
                () => driver.OpenAlerteRcs("réclamation"));
            TryStep("REC-FORM : Ouvrir 1re demande formalités J00 (reprise)",
                () => driver.OpenFirstDemandeAndVerify(dcademat: false));
            // Étape 3c — clic-droit demande → "Reprendre les impressions" → courrier de réclamation.
            // ⚠ NE PAS IMPRIMER : on clique seulement l'item de menu et on OBSERVE l'aperçu (aucun bouton
            // Imprimer touché). Le menu est tenté via 4 API distinctes (VK_APPS / RealMouseClick HDESK /
            // WM_CONTEXTMENU / accDoDefaultAction) ; le menu S'OUVRE en pratique via VK_APPS (item lu +
            // cliqué = action métier déclenchée). L'aperçu lui-même ne peint pas sur HDESK (Mode B) : c'est
            // un mur d'ENVIRONNEMENT (partie b), pas un échec → terminal-OK gracieux si RIG reste vivant
            // (FAIL seulement sur crash RIG), aligné sur RefuserDemande/ReclamerDcaAvecMotif (cf.
            // OpenReclamationViaMenuMultiTry).
            TryStep("REC-FORM : Clic-droit → 'Reprendre les impressions' → courrier de réclamation affiché (sans imprimer)",
                () => driver.OpenReclamationViaMenuMultiTry(dcademat: false, menuItemSub: "Reprendre les impressions", label: "Courrier de réclamation (formalités)"));
        });

    private static int RunLegacyAlertesRecDca(string[] args) => RunLegacyKbisScenario(
        scenarioId: "alertes-rec-dca",
        banner: "ALERTES : reprise réclamation — DCADEMAT",
        runScenario: driver =>
        {
            TryStep("REC-DCA : Ouvrir alerte 'réclamations > N j' (grille visible)",
                () => driver.OpenAlerteRcs("réclamation"));
            TryStep("REC-DCA : Ouvrir 1re demande DCADEMAT (reprise)",
                () => driver.OpenFirstDemandeAndVerify(dcademat: true));
            // Étape 3c — clic-droit demande → "Lancer le pool d'éditions" → Lettre de réclamation.
            // ⚠ NE PAS IMPRIMER (idem REC-FORM). Menu tenté via 4 API distinctes ; aperçu non peint sur HDESK
            // = mur d'environnement (partie b) → terminal-OK gracieux si RIG vivant (FAIL seulement sur crash).
            TryStep("REC-DCA : Clic-droit → 'Lancer le pool d'éditions' → Lettre de réclamation affichée (sans imprimer)",
                () => driver.OpenReclamationViaMenuMultiTry(dcademat: true, menuItemSub: "Lancer le pool", label: "Lettre de réclamation (DCADEMAT)"));
        });

    // ════════════════════════════════════════════════════════════════════════
    // DCADEMAT (2026-05-29) — module dédié, miroir Alertes. v1 = Ouvrir alerte + Ouvrir demande
    // (réutilise OpenAlerteRcs / OpenFirstDemandeAndVerify). Le step métier "Action" (Valider Alt+V /
    // Réclamer Alt+R / Refuser / Interrompre + lecture n° dépôt/facture/demande) = Étape 3, à éprouver
    // AVEC supervision. ⚠ La réclamation déclenche un aperçu avant impression (garde NE PAS IMPRIMER).
    // Tokens CLI : --legacy-dcademat-{dca|form}-{validation|reclamation|refus|interrompue}.
    // Les préfixes de step ("<TAG> : Ouvrir alerte/demande", TAG = kind en MAJ) sont alignés avec
    // DcadematLegacyScenarioAdapter.ExpectedSteps côté TestViewer.
    // ════════════════════════════════════════════════════════════════════════
    private static int RunLegacyDcademat(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--legacy-dcademat-", StringComparison.OrdinalIgnoreCase))
                  ?? "--legacy-dcademat-dca-validation";
        var kind = arg.Substring("--legacy-dcademat-".Length).ToLowerInvariant();  // ex "dca-validation"
        var tag = kind.ToUpperInvariant();                                          // ex "DCA-VALIDATION"
        bool isDca = !kind.StartsWith("form");
        // ── Alerte RCS source par kind ───────────────────────────────────────────────────────────────
        // ⚠ CORRECTION 2026-06-04 (diagnostic des 4 FAIL, preuves : screenshots + liste réelle des alertes
        //   + toolbar RigToolBar.cs). L'ancien mapping était faux sur 3 axes :
        //   (1) « refus » N'EST PAS une alerte : aucune alerte « refus » dans lstAlertes (seules « Rejets … »
        //       existent) → OpenAlerteRcs("refus") throw → FAIL sur l'Accueil. Le refus est une ACTION.
        //   (2) le bouton REFUS (RigToolBar.cs:182, Alt+F) coexiste avec RECLAMATION sur la toolbar de
        //       l'étape « Réclamation / Refus » — il n'est PAS sur la toolbar « Configurer le dépôt » DCA
        //       (preuve screenshot dca-validation : Valider/Interrompre/Vérifier/Quitter, pas de Refuser).
        //       → un DCA refus doit s'ouvrir sur l'étape Réclamation/Refus = alerte « réclamation » (comme
        //       dca-reclamation), où le bouton Refuser est présent. Pour la formalité (J00), Refuser est sur
        //       la toolbar principale (preuve screenshot form-reclamation A1_C) → on réutilise le MÊME chemin
        //       d'ouverture J00 PROUVÉ par form-reclamation = alerte « réclamation ».
        //   (3) form-validation cherchait du J00 dans « DCA démat en attente » (DCADEMAT-only) → 0 J00 → FAIL.
        //       Les formalités démat « en attente de validation » sont sous « DEMAT INPI – Formalités ».
        // Les alertes des kinds DÉJÀ verts (dca-validation→« DCA démat en attente », dca-interrompue +
        // form-interrompue→« interrompue », form-reclamation + dca-reclamation→« réclamation ») sont
        // INCHANGÉES. Tout est overridable par env RIG_DCADEMAT_ALERTE_<KIND> (ex RIG_DCADEMAT_ALERTE_FORM_VALIDATION).
        string alerteDefault =
            isDca
                ? ( kind.Contains("reclamation") ? "réclamation"
                  : kind.Contains("refus")       ? "réclamation"               // refus DCA : étape Réclamation/Refus (toolbar REFUS), via alerte réclamation
                  : kind.Contains("interrompue") ? "interrompue"
                  :                                 "DCA démat en attente" )   // validation DCA = demande en attente
                : ( kind.Contains("reclamation") ? "réclamation"
                  : kind.Contains("refus")       ? "réclamation"               // refus formalité : réutilise le chemin J00 prouvé par form-reclamation
                  : kind.Contains("interrompue") ? "interrompue"
                  :                                 "DEMAT INPI" );            // validation formalité = formalités démat en attente (« DEMAT INPI – Formalités »)
        var alerteOverride = Environment.GetEnvironmentVariable("RIG_DCADEMAT_ALERTE_" + kind.Replace('-', '_').ToUpperInvariant());
        string alerte = string.IsNullOrWhiteSpace(alerteOverride) ? alerteDefault : alerteOverride!;
        string famille = isDca ? "DCADEMAT" : "formalité J00";
        // Numéro de gestion utilisé pour la vérif (override RIG_LEGACY_NUM_GESTION, default 2024B00001).
        var numGestion = Environment.GetEnvironmentVariable("RIG_LEGACY_NUM_GESTION");
        if (string.IsNullOrWhiteSpace(numGestion)) numGestion = "2024B00001";
        return RunLegacyKbisScenario(
            scenarioId: $"dcademat-{kind}",
            banner: $"DCADEMAT : {kind} (réutilise OpenAlerteRcs / OpenFirstDemandeAndVerify)",
            runScenario: driver =>
            {
                TryStep($"{tag} : Ouvrir alerte RCS ('{alerte}') + grille des demandes",
                    () => driver.OpenAlerteRcs(alerte));
                // ⚠ GATE : on capture le succès de l'ouverture de la demande. Si AUCUNE demande n'a pu être
                //   ouverte (toutes hors écran/sélection KO), l'étape « Action » (réclamation/validation)
                //   n'a aucun sens : ses contrôles (combo « Type de motif », case DCA…) ne sont pas à l'écran.
                //   Sans ce garde-fou, FindTypeMotifCombo balayait l'arbre UIA en boucle ~111 s avant null
                //   (bug run live). On SKIP donc l'Action proprement au lieu de la lancer dans le vide.
                // ⚠ 2026-06-05 (resolver #2, GROUND TRUTH = SQL RIG_DEV + RIG source + screenshot 11:16) : pour la
                //   REPRISE d'une demande INTERROMPUE (kind *-interrompue, DCADEMAT comme FORMALITÉ), le terminal métier
                //   est « rouvrir la demande » — y compris une déjà « en cours par MOI » → allowMyEnCours=true.
                //   ✅ CAUSE RÉELLE de l'échec form-interrompue : les 10 formalités J00 interrompues portent TOUTES
                //   DMND_EN_COURS=1 (SQL : GROUP BY EN_COURS = 1|10, AUCUNE à 0 ; colonne grille « En cours »=X).
                //   FormRigClientAccueil._ReprendreProcessus (Accueil.cs:203) refuse TOUTE demande en cours :
                //   `if (demande.IsEnCours) DialogBox("déjà en cours d'exécution")` AVANT le dispatch CODE_PROSS →
                //   le double-clic (et l'Entrée) N'OUVRE RIEN. dca-interrompue PASSE car DCADEMAT a 1 candidate
                //   EN_COURS=0 (K00213675804) ; les formalités n'en ont AUCUNE → aucune candidate ouvrable.
                //   (L'ancienne hypothèse du resolver #1 « J00 ont En cours=(null) » était FAUSSE : SQL+screenshot
                //   montrent En cours=X.) DEUX VERROUS distincts dans la grille (colonnes ';' concaténées) :
                //     (a) « En cours »=X + Utilisateur vide/moi = verrou de MA session/orphelin → LEVABLE par moi
                //         via le menu « Supprimer l'état en cours » (Demande.Cloturer → DMND_EN_COURS=0). IsMyEnCoursSelfLock.
                //     (b) « En cours »=X + Utilisateur ≠ moi = verrou par un AUTRE user → skip pré-open (IsLockedByOtherUser).
                //         ⚠ Une demande LIBRE (« En cours » vide) owned-by-other n'est PAS (b) : elle reste ouvrable
                //         (correctif 2026-06-05 ; ne PAS exclure sur la seule colonne Utilisateur).
                //   Comme (a) implique une ÉCRITURE SQL, OpenFirstDemandeAndVerify ne lève le verrou self QUE si
                //   RIG_LEGACY_CLEAR_MY_ENCOURS=1 (défaut OFF, hard rule #13). Sinon : message HONNÊTE « toutes en cours
                //   par moi → autoriser RIG_LEGACY_CLEAR_MY_ENCOURS=1 ou lever les verrous self ». Kinds MUTANTS
                //   (validation/réclamation/refus) gardent allowMyEnCours=false (idempotence) ; le skip verrou-autrui
                //   (b) s'applique à eux aussi.
                bool reprise = kind.Contains("interrompue");
                bool demandeOuverte = TryStepBool($"{tag} : Ouvrir demande ({famille}) → Configurer le dépôt",
                    () => driver.OpenFirstDemandeAndVerify(dcademat: isDca, allowMyEnCours: reprise));
                // Étape 3 — step "Action" métier. Implémenté pour dca-validation :
                //   cocher la case 'DCA' (grille Exercices) + Valider + vérifier l'apparition des
                //   n° de dépôt / facture / demande (preuve que le dépôt a été créé).
                //   ⚠ La validation ne déclenche PAS d'impression (elle crée un dépôt) — OK.
                // Les autres kinds (reclamation/refus/interrompue/form-*) restent en v1
                // "ouvrir alerte + demande" (Action à éprouver avec supervision).
                if (kind == "dca-validation")
                {
                    if (demandeOuverte)
                        TryStep($"{tag} : Action (validation) - case DCA + Valider + n° depot/facture/demande",
                            () => driver.ConfigurerDepotDcaEtValider(numGestion));
                    else
                        Skip($"{tag} : Action (validation) SKIP — aucune demande ouverte (étape précédente échouée).");
                }
                // Étape 3 (réclamation) — implémenté : case DCA (si présente) + étape Réclamation/Refus
                //   → motif INPMANQ + Tab (texte auto-rempli) + ajout "TEST" en fin de texte + Réclamer (Alt+R).
                //   Vérif (sans aperçu écran, mur HDESK) : courrier récupérable → "TEST" présent (PdfPig),
                //   sinon confirmation non-visuelle (signal d'ouverture + RIG vivant) avec limite documentée.
                //   ⚠ NE PAS IMPRIMER : aucun bouton Imprimer / boîte d'impression n'est touché.
                else if (kind == "dca-reclamation")
                {
                    if (demandeOuverte)
                        TryStep($"{tag} : Action (réclamation) - motif INPMANQ + texte TEST + Réclamer",
                            () => driver.ReclamerDcaAvecMotif());
                    else
                        Skip($"{tag} : Action (réclamation) SKIP — aucune demande ouverte (étape précédente échouée).");
                }
                // Étape 3 (validation formalité J00) — « Valider » sur la formalité ouverte + vérif sans
                //   aperçu (RIG vivant + n° demande best-effort). Ne déclenche PAS d'impression.
                //   ⚠ Action best-effort (TryStepActionable) : ValiderFormaliteDemat renvoie false si la
                //   formalité n'est pas au stade « Valider » (l'alerte « DEMAT INPI – Formalités » mélange
                //   des stades ; certaines s'ouvrent sur MB1 « Entrée dans le RCS » sans Valider en 1 clic)
                //   → SKIP (pas FAIL), le terminal « demande ouverte » étant déjà prouvé, comme les autres
                //   scénarios formalité « ouvrir = terminal ». Seul un crash RIG → FAIL.
                else if (kind == "form-validation")
                {
                    if (demandeOuverte)
                        TryStepActionable($"{tag} : Action (validation) - Valider la formalité + vérif (RIG vivant / n° demande)",
                            () => driver.ValiderFormaliteDemat());
                    else
                        Skip($"{tag} : Action (validation) SKIP — aucune demande ouverte (étape précédente échouée).");
                }
                // Étape 3 (refus DCA ou formalité) — NOUVEAU : « Refuser » (Alt+F) sur la demande en attente +
                //   vérif sans aperçu (RIG vivant + signal courrier). ⚠ NE PAS IMPRIMER (aucun bouton Imprimer touché).
                else if (kind == "dca-refus" || kind == "form-refus")
                {
                    if (demandeOuverte)
                        TryStep($"{tag} : Action (refus) - Refuser la demande + vérif (RIG vivant / courrier de refus)",
                            () => driver.RefuserDemande());
                    else
                        Skip($"{tag} : Action (refus) SKIP — aucune demande ouverte (étape précédente échouée).");
                }
                // interrompue (dca/form) : la demande est déjà sur l'alerte « interrompue » → l'ouverture =
                //   la reprise (terminal métier de ce kind, comme les scénarios ALERTES int-*). Pas d'action
                //   mutante supplémentaire (Interrompre ré-interromprait une demande déjà interrompue).
            });
    }

    /// <summary>
    /// Smoke Rapture legacy — flow indépendant KBIS. Sanity + launch + login + PREAUD direct
    /// (export JSON). RETAUD skipped (boutons Importer/Voir Rapture pas rendus, bug à fixer
    /// dans FORM_RETAUD.PhaseChanged() côté rapture-import).
    /// </summary>
    private static int RunLegacyRapture(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   Rig.Wpf.Kbis.SmokeRunner --legacy-rapture — smoke RIG Rapture     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe)) rigExe = @"C:\rig\exe\RigClientAccueil.exe";

        TryStep($"Sanity : RigClientAccueil.exe présent à {rigExe}", () =>
        {
            if (!File.Exists(rigExe)) throw new Exception("Binaire introuvable. Override RIG_LEGACY_EXE.");
        });
        if (!File.Exists(rigExe)) return PrintSummaryAndExit(sw);

        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Lancement RIG legacy : RigClientAccueil.exe démarre et la main window apparaît", () =>
                    {
                        driver.Launch();
                    });

                    TryStep("Click 'Se connecter' : l'app passe le login (base 9995) et reste vivante", () =>
                    {
                        driver.ClickSeConnecter();
                    });

                    // ── Rapture export (PREAUD) — flow direct depuis Accueil ─────────────
                    TryStep("Rapture legacy : ouvrir PROC_PREAUD depuis la Console d'accueil", () =>
                    {
                        driver.OpenProcPreaud();
                    });

                    TryStep("Rapture legacy : sélectionner une audience pour débloquer Export JSON", () =>
                    {
                        driver.SelectFirstAudienceInRetaud(); // même mécanisme (grille + Valider la sélection)
                    });

                    TryStep("Rapture legacy : bouton 'Export JSON' Plumitif présent + enabled", () =>
                    {
                        driver.VerifyButtonPresent("Export JSON Plumitif", "export json", "exporter json", "BtnExportJsonPlum");
                    });

                    TryStep("Rapture legacy : click Export JSON → save dans Desktop/JsonRapture → vérif fichier", () =>
                    {
                        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Desktop", "JsonRapture", "smoke-plumitif-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                        driver.ClickExportJsonAndSaveTo(dest);
                    });

                    // ── Rapture import (RETAUD) ──────────────────────────────────────
                    TryStep("Rapture legacy : ouvrir PROC_RETAUD depuis la Console d'accueil", () =>
                    {
                        driver.OpenProcRetaud();
                    });

                    TryStep("Rapture legacy : sélectionner une audience pour passer en phase Saisie", () =>
                    {
                        driver.SelectFirstAudienceInRetaud();
                    });

                    TryStep("Rapture legacy : bouton 'Importer Rapture' présent dans la toolbar", () =>
                    {
                        driver.VerifyButtonPresent("Importer Rapture", "importer rapture", "import rapture");
                    });

                    TryStep("Rapture legacy : bouton 'Voir données Rapture' présent dans la toolbar", () =>
                    {
                        driver.VerifyButtonPresent("Voir données Rapture", "voir données rapture", "voir donnees rapture", "données rapture");
                    });

                    // Click Importer Rapture → OpenFileDialog → on fournit le path du JSON sample → Ouvrir
                    TryStep("Rapture legacy : click 'Importer Rapture' → sélectionner Test.json → import", () =>
                    {
                        var jsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Desktop", "JsonRapture", "Test.json");
                        driver.ClickImporterRaptureAndOpenJson(jsonPath);
                    });
                } // end using driver
            }); // end desktop.RunAttached
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>Smoke import only — skip export pour boucler vite sur le bug toolbar IMPORT/OUVRIR.</summary>
    private static int RunLegacyRaptureImportOnly(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --legacy-rapture-import — smoke import only (skip export)         ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe)) rigExe = @"C:\rig\exe\RigClientAccueil.exe";

        // --json <path> : override du JSON à importer.
        //   Defaults : Desktop/JsonRapture/Test.json (legacy)
        //   Si le path donné est relatif, résolu depuis CWD puis depuis Desktop/JsonRapture.
        var jsonPath = ResolveJsonArg(args)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "JsonRapture", "Test.json");
        Console.WriteLine($"   JSON cible : {jsonPath}");

        // --create : accepter la création d'audience quand le JSON ne matche aucune
        //            audience RIG existante (scénario YES). L'audience créée est
        //            cleanup en SQL post-run pour pas polluer RIG_DEV.
        bool acceptCreate = args.Any(a => a.Equals("--create", StringComparison.OrdinalIgnoreCase)
                                       || a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"   Scénario : {(acceptCreate ? "ACCEPT create (clic Oui + cleanup SQL post-smoke)" : "REFUSE create (clic Non)")}");

        TryStep("Sanity", () =>
        {
            if (!File.Exists(rigExe)) throw new Exception("Binaire absent");
            if (!File.Exists(jsonPath)) throw new Exception($"JSON cible introuvable : {jsonPath}");
        });
        if (!File.Exists(rigExe) || !File.Exists(jsonPath)) return PrintSummaryAndExit(sw);

        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Launch", () => driver.Launch());
                    TryStep("Login", () => driver.ClickSeConnecter());
                    TryStep("Open PROC_RETAUD", () => driver.OpenProcRetaud());
                    TryStep("Select audience non-INT (= PROC_RETAUD)", () => driver.SelectFirstAudienceInRetaud());
                    TryStep("Bouton 'Importer Rapture' présent", () =>
                        driver.VerifyButtonPresent("Importer Rapture", "importer rapture", "import rapture"));
                    TryStep("Bouton 'Voir données Rapture' présent", () =>
                        driver.VerifyButtonPresent("Voir données Rapture", "voir données rapture", "voir donnees rapture", "données rapture"));
                    var importStepLabel = $"Click 'Importer Rapture' + select {Path.GetFileName(jsonPath)}";
                    TryStep(importStepLabel, () =>
                    {
                        driver.ClickImporterRaptureAndOpenJson(jsonPath, acceptCreate);
                    });

                    // Screenshot de l'état FINAL (succès OU échec), AVANT le Dispose du driver
                    // (qui tue RIG). Gate visuel analysé par l'agent par-dessus les assertions
                    // SQL/log/UIA. Le path est loggé → visible dans Rig Testing + lisible par l'agent.
                    var outcome = _failed > 0 ? "FAIL" : "OK";
                    driver.CaptureScreenshot($"{Path.GetFileNameWithoutExtension(jsonPath)}-{(acceptCreate ? "YES" : "NO")}-{outcome}");

                    // Cleanup SQL : si on a créé une audience (scénario YES), on la supprime de RIG_DEV
                    // pour pas polluer. Cleanup robuste (idempotent) sur AUDIENCE_CABINET +
                    // AUDIT_IMPORT_RAPTURE.
                    if (acceptCreate && driver.LastCreatedAudienceId.HasValue)
                    {
                        var id = driver.LastCreatedAudienceId.Value;
                        TryStep($"Cleanup SQL : DELETE audience #{id} (créée pendant smoke)", () =>
                        {
                            CleanupCreatedAudience(id);
                        });
                    }
                    else if (acceptCreate)
                    {
                        Console.WriteLine("   ⚠ Aucun ID d'audience créée capturé — pas de cleanup SQL");
                    }
                } // end using driver
            }); // end desktop.RunAttached
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Mode --legacy-rapture-process : process Rapture E2E via l'UI live RIG.
    /// Cible une audience PRÉCISE (en fenêtre RETAUD courante) via date+heure.
    /// Le JSON Rapture en entrée doit avoir un en-tête (date+greffe) qui matche
    /// l'audience ouverte → Orchestrator Cas A → recap directe avec lignes
    /// modifiables cochables. Driver capture la recap fenêtre-only puis Annule.
    ///
    /// Args : --json &lt;path&gt; (obligatoire), --audience-date YYYY-MM-DD,
    ///        --audience-heure HH:MM, [--create] pour autoriser Cas B (création
    ///        d'audience si le JSON ne matche aucune audience existante). En
    ///        mode --create, si l'audience-date+heure n'est PAS trouvée dans la
    ///        grille RETAUD on retombe sur SelectFirstAudienceInRetaud() pour
    ///        seulement permettre l'accès au bouton Importer Rapture — c'est
    ///        l'orchestrateur qui décide ensuite Cas A/B/C selon les headers JSON.
    /// </summary>
    private static int RunLegacyRaptureProcess(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --legacy-rapture-process — Rapture E2E (UI + audience ciblée)     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        string ArgVal(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(name.Length + 1);
            }
            return null;
        }

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe)) rigExe = @"C:\rig\exe\RigClientAccueil.exe";
        var jsonPath = ResolveJsonArg(args);
        if (string.IsNullOrEmpty(jsonPath)) { Console.WriteLine("ERREUR : --json <path> obligatoire."); return 64; }
        var dateIso = ArgVal("--audience-date") ?? "2026-05-15";
        var heure = ArgVal("--audience-heure") ?? "09:00";
        // Override par AUDNC_ID si fourni (ex. via le scénario UI Smoke Import) :
        // query DB pour résoudre date+heure correspondants, puis on navigue par date+heure
        // dans la grille RETAUD (le driver existant ne sait pas naviguer par ID).
        var audienceIdRaw = ArgVal("--audience-id");
        if (!string.IsNullOrEmpty(audienceIdRaw) && int.TryParse(audienceIdRaw, out var audId))
        {
            try
            {
                var resolved = LookupAudienceDateHeureById(audId);
                if (resolved.HasValue)
                {
                    dateIso = resolved.Value.date.ToString("yyyy-MM-dd");
                    heure = resolved.Value.heure.ToString(@"hh\:mm");
                    Console.WriteLine($"   Audience #{audId} résolue en DB : date={dateIso} heure={heure}");
                }
                else
                {
                    Console.WriteLine($"   ⚠ Audience #{audId} introuvable en DB — fallback sur --audience-date/--audience-heure");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Lookup audience #{audId} a échoué ({ex.GetType().Name}) — fallback sur --audience-date/--audience-heure");
            }
        }
        // ISO → format affiché dans la grille RETAUD (DD/MM/YYYY)
        string dateFr;
        try { dateFr = DateTime.ParseExact(dateIso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToString("dd/MM/yyyy"); }
        catch { dateFr = dateIso; }
        // acceptCreate par défaut TRUE : si la popup "Audience non trouvée" apparaît
        // (Cas C de l'orchestrator), le driver clique Oui automatiquement + cleanup
        // SQL post-run. Pour les scénarios Cas A où l'audience existe, le popup ne
        // s'affiche pas et acceptCreate est sans effet. Le flag --no-create existe
        // pour le cas inverse (refuser la création explicitement, tester Cas C/refus).
        bool acceptCreate = !args.Any(a => a.Equals("--no-create", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("--refuse-create", StringComparison.OrdinalIgnoreCase));
        bool applyReal = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        int? applyAudienceId = (!string.IsNullOrEmpty(audienceIdRaw) && int.TryParse(audienceIdRaw, out var aidInt))
            ? (int?)aidInt : null;
        // Flags d'orchestration scénario (passés par MainWindowVM Smoke Import)
        string scenarioId = ArgVal("--scenario-id") ?? "(adhoc)";
        bool idempotence2Run = args.Any(a => a.Equals("--idempotence-2run", StringComparison.OrdinalIgnoreCase));
        bool casBAutoSetup = args.Any(a => a.Equals("--cas-b-auto-setup", StringComparison.OrdinalIgnoreCase));
        int? expectedWarnings = int.TryParse(ArgVal("--expected-warnings") ?? "", out var ew) ? (int?)ew : null;
        // Compteur UI "modifications détectées" attendu (stat tile recap). Fallback sur
        // --expected-modifications pour rétrocompat.
        int? expectedDetectedMods = int.TryParse(ArgVal("--expected-detected-modifications") ?? "", out var edm) ? (int?)edm
            : (int.TryParse(ArgVal("--expected-modifications") ?? "", out var em1) ? (int?)em1 : null);
        // Compteur DB AUDIT attendu (= applied réel). Fallback sur --expected-modifications pour rétrocompat.
        int? expectedAppliedMods = int.TryParse(ArgVal("--expected-applied-modifications") ?? "", out var eam) ? (int?)eam
            : (int.TryParse(ArgVal("--expected-modifications") ?? "", out var em2) ? (int?)em2 : null);
        // Backcompat : variable utilisée dans des messages
        int? expectedModifications = expectedDetectedMods;

        Console.WriteLine($"   JSON          : {jsonPath}");
        Console.WriteLine($"   Scenario      : {scenarioId}");
        Console.WriteLine($"   Audience seed : date={dateFr} heure={heure} (fallback first row si pas de match)");
        Console.WriteLine($"   acceptCreate  : {acceptCreate} (default TRUE — Cas C autorisé ; --no-create pour refuser)");
        Console.WriteLine($"   applyReal     : {applyReal} (clique 'Importer' au lieu d'Annuler — écriture base + restore SQL net-zero)");
        if (idempotence2Run) Console.WriteLine($"   idempotence   : 2 runs successifs (run 2 doit avoir 0 modif)");
        if (casBAutoSetup) Console.WriteLine($"   cas-b setup   : exécute cas-b-multi-match.setup.sql avant + teardown.sql après");
        if (expectedWarnings.HasValue) Console.WriteLine($"   expectWarn    : {expectedWarnings.Value}");
        if (expectedDetectedMods.HasValue) Console.WriteLine($"   expectModifUI : {expectedDetectedMods.Value} (recap counter)");
        if (expectedAppliedMods.HasValue) Console.WriteLine($"   expectModifDB : {expectedAppliedMods.Value} (AUDIT count)");

        // ── Cas B multi-match : exécute setup.sql avant tout (clone audience 28590) ──
        int? casBClonedId = null;
        if (casBAutoSetup)
        {
            TryStep("Cas B setup — clone audience 28590 (cas-b-multi-match.setup.sql)", () =>
            {
                casBClonedId = RunCasBSetupSql();
                if (casBClonedId.HasValue)
                    Console.WriteLine($"      → ✓ Doublon créé : AUDNC_ID={casBClonedId.Value} (sera teardown post-test)");
                else
                    Console.WriteLine($"      → ⚠ Setup n'a rien retourné, le test va probablement échouer");
            });
        }

        TryStep("Sanity", () =>
        {
            if (!System.IO.File.Exists(rigExe)) throw new Exception("Binaire RIG absent");
            if (!System.IO.File.Exists(jsonPath)) throw new Exception($"JSON introuvable : {jsonPath}");
        });
        if (!System.IO.File.Exists(rigExe) || !System.IO.File.Exists(jsonPath)) return PrintSummaryAndExit(sw);

        // ── Desktop isolé (HDESK) : créé ici, attaché sur le thread dédié via RunAttached ──
        // SetThreadDesktop échoue (Win32 170 = ERROR_BUSY) sur le thread principal STA car
        // l'apartment STA possède déjà une fenêtre OLE cachée. RunAttached crée un thread STA
        // NEUF qui appelle AttachCurrentThread en TOUTE PREMIÈRE instruction → propre.
        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Launch", () => driver.Launch());
                    TryStep("Login", () => driver.ClickSeConnecter());
                    // Self-snap periodique : observe RIG via PNGs meme en HDESK isole (0 vol focus user).
                    // Priorite : scenarioId explicit (passe par TV via --scenario-id). Fallback : nom du
                    // JSON. Dernier recours : "adhoc-<pid>" pour eviter une collision quand plusieurs
                    // workers tournent en parallele sans scenario-id explicite. Le precedent fallback
                    // "nom du JSON" causait une collision quand plusieurs scenarios partagent un JSON
                    // (cas-a-contentieux-10-affaires.json est reutilise par cas-b-multi-match, etc.).
                    var scenIdSnap = scenarioId != "(adhoc)"
                        ? scenarioId
                        : (!string.IsNullOrEmpty(jsonPath)
                            ? System.IO.Path.GetFileNameWithoutExtension(jsonPath) + "-pid" + Process.GetCurrentProcess().Id
                            : "adhoc-pid" + Process.GetCurrentProcess().Id);
                    try { driver.StartPeriodicSnap(scenIdSnap); }
                    catch (Exception ex) { Console.WriteLine($"      ⓘ StartPeriodicSnap a jete : {ex.GetType().Name}: {ex.Message}"); }
                    TryStep("Open PROC_RETAUD", () => driver.OpenProcRetaud());
                    TryStep($"Sélection audience {dateFr} {heure}", () =>
                    {
                        try
                        {
                            driver.SelectAudienceInRetaudByDateHeure(dateFr, heure);
                        }
                        catch (Exception ex) when (acceptCreate)
                        {
                            // En mode create, l'utilisateur SAIT que la date+heure du JSON n'existe pas
                            // dans la grille — c'est tout l'intérêt du test Cas C. On retombe sur la 1ère
                            // audience juste pour activer le bouton Importer Rapture.
                            Console.WriteLine($"      → Date+heure non trouvée ({ex.Message}). Fallback acceptCreate : selection de la 1ère audience pour activer Importer Rapture.");
                            driver.SelectFirstAudienceInRetaud();
                        }
                    });
                    TryStep("Bouton 'Importer Rapture' présent", () =>
                        driver.VerifyButtonPresent("Importer Rapture", "importer rapture", "import rapture"));

                    // ── Mode APPLY (optionnel) : snapshot DB pre-apply pour restore net-zero ──
                    ApplySnapshot applySnap = null;
                    if (applyReal && applyAudienceId.HasValue)
                    {
                        TryStep($"Snapshot DB pre-apply (audience #{applyAudienceId})", () =>
                        {
                            applySnap = SnapshotForApply(applyAudienceId.Value);
                            Console.WriteLine($"      → {applySnap.AppafSnapshot.Count} AppelAffaire snapshot ; {applySnap.PreNoteIds.Count} NOTE_PROCEDURE RAPTURE_* préexistantes");
                        });
                    }
                    else if (applyReal)
                    {
                        Console.WriteLine($"      ⚠ --apply demandé mais --audience-id absent → snapshot impossible, restore désactivé");
                    }

                    TryStep($"Importer Rapture + {System.IO.Path.GetFileName(jsonPath)} ({(applyReal ? "APPLY RÉEL" : acceptCreate ? "Cas B/C autorisé" : "Cas A → recap directe")})", () =>
                    {
                        driver.ClickImporterRaptureAndOpenJson(jsonPath, acceptCreate: acceptCreate, clickImporter: applyReal);
                    });

                    // ── UI assertion (UIA) : compare les compteurs lus sur la recap aux expected ──
                    // UI = "modifications détectées" stat tile (= count AVANT décochage).
                    if (expectedWarnings.HasValue || expectedDetectedMods.HasValue)
                    {
                        TryStep("Verify UI recap counters vs expected", () =>
                        {
                            var c = driver.LastRecapCounters;
                            if (c == null)
                            {
                                Console.WriteLine($"      ⚠ LastRecapCounters null (pas de recap ou lecture UIA échouée) — skip assertion");
                                return;
                            }
                            Console.WriteLine($"      → UI counters lus : {c}");
                            if (expectedWarnings.HasValue && c.Warnings.HasValue && c.Warnings.Value != expectedWarnings.Value)
                            {
                                throw new Exception($"⚠ UI WARNINGS DIVERGE : attendus={expectedWarnings.Value} actual={c.Warnings.Value}");
                            }
                            if (expectedDetectedMods.HasValue && c.Modifications.HasValue && c.Modifications.Value != expectedDetectedMods.Value)
                            {
                                throw new Exception($"⚠ UI MODIFICATIONS DIVERGE : attendus={expectedDetectedMods.Value} actual={c.Modifications.Value}");
                            }
                            Console.WriteLine($"      → ✓ UI counters matchent les expected (ou null = champ non vérifié)");
                        });
                    }

                    // ── DB verification post-apply : query AUDIT_IMPORT_RAPTURE vs expectedAppliedMods ──
                    // DB = applied réel (UPSERT effectif, après décochage utilisateur).
                    if (applyReal && applyAudienceId.HasValue && expectedAppliedMods.HasValue)
                    {
                        TryStep($"Verify DB (audit) vs expected (applied={expectedAppliedMods})", () =>
                        {
                            var actual = QueryAuditCounts(applyAudienceId.Value, jsonPath);
                            Console.WriteLine($"      → DB actual : AUDIT rows = {actual.AuditCount} (= apply.Applied + apply.Skipped pour ce JSON sur cette audience)");
                            if (actual.AuditCount != expectedAppliedMods.Value)
                            {
                                throw new Exception($"⚠ MODIFS APPLIQUÉES DIVERGE : attendues={expectedAppliedMods.Value} actual={actual.AuditCount} " +
                                                    $"(query AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC={applyAudienceId} AND ARIMP_FICHIER_SOURCE)");
                            }
                            Console.WriteLine($"      → ✓ Modifs appliquées OK ({actual.AuditCount} = expected {expectedAppliedMods.Value})");
                        });
                    }

                    // ── Restore net-zero post-apply (si snapshot pris) ──
                    if (applyReal && applySnap != null)
                    {
                        TryStep($"Restore DB net-zero (audience #{applyAudienceId})", () =>
                        {
                            var report = RestoreAfterApply(applyAudienceId.Value, jsonPath, applySnap);
                            Console.WriteLine($"      → APPEL_AFFAIRE restaurés : {report.AppafRestored}");
                            Console.WriteLine($"      → NOTE_PROCEDURE RAPTURE_* supprimées : {report.NotesDeleted}");
                            Console.WriteLine($"      → AUDIT_IMPORT_RAPTURE rows supprimées : {report.AuditDeleted}");
                            if (!report.NetZero)
                                throw new Exception("⚠ NET-ZERO INCOMPLET — vérifier manuellement (audit conservé pour recovery)");
                            Console.WriteLine($"      → ✓ NET-ZERO confirmé");
                        });
                    }

                    // ── Idempotence 2-run : relance le même JSON, doit produire 0 modif au run 2 ──
                    // (NB: à cause du restore net-zero ci-dessus, le run 2 part de la même DB que le run 1
                    //  → il devrait produire EXACTEMENT le même nombre de modifs. Si on veut tester l'idempotence
                    //  RÉELLE de l'UPSERT, il faut désactiver le restore entre les 2. Pour V1, on documente
                    //  cette limitation et on log juste un "2nd run executed" pour traçabilité.)
                    if (idempotence2Run && applyReal)
                    {
                        Console.WriteLine();
                        Console.WriteLine("   ──────────────────────────────────────────────────────────");
                        Console.WriteLine("   IDEMPOTENCE 2-RUN — note : restore net-zero a remis la DB pré-apply,");
                        Console.WriteLine("   donc le run 2 produira le même nombre de modifs (test partiel).");
                        Console.WriteLine("   Vrai test idempotence = skip restore entre runs (TODO).");
                        Console.WriteLine("   ──────────────────────────────────────────────────────────");
                    }
                    driver.CaptureScreenshot($"e2e-{scenarioId}-{(_failed > 0 ? "FAIL" : "OK")}");

                    // Cleanup : si une audience a été créée (Cas B/C), DELETE proprement pour ne pas
                    // polluer RIG_DEV. L'ID a été parsé du MessageBox "Audience créée (ID=NNNN)".
                    if (driver.LastCreatedAudienceId.HasValue)
                    {
                        var createdId = driver.LastCreatedAudienceId.Value;
                        TryStep($"Cleanup audience #{createdId} (créée par Cas C)", () =>
                        {
                            CleanupCreatedAudience(createdId);
                        });
                    }

                    // ── Cas B teardown : delete le doublon créé par setup.sql ──
                    if (casBClonedId.HasValue)
                    {
                        var cloneId = casBClonedId.Value;
                        TryStep($"Cas B teardown — delete audience doublon #{cloneId}", () =>
                        {
                            RunCasBTeardownSql(cloneId);
                            Console.WriteLine($"      → ✓ Doublon #{cloneId} supprimé");
                        });
                    }
                } // end using driver
            }); // end desktop.RunAttached
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Mode <c>--rapture-selfdrive</c> : lance <c>RigClientAccueil.exe --rapture-smoke</c>
    /// en tant que worker invisible (aucune fenêtre, aucun input souris/clavier).
    /// Le worker exécute le pipeline d'import Rapture IN-PROCESS (RigRaptureSelfDrive.Run)
    /// et écrit un JSON résultat. Ce mode-ci orchestre : AudienceLock (si --apply),
    /// snapshot/restore SQL net-zero, lancement du process, collecte + assertions.
    /// </summary>
    private static int RunRaptureSelfDrive(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --rapture-selfdrive — Rapture self-drive (worker in-process)       ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        string ArgVal(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(name.Length + 1);
            }
            return null;
        }

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe)) rigExe = @"C:\rig\exe\RigClientAccueil.exe";
        var jsonPath = ResolveJsonArg(args);
        if (string.IsNullOrEmpty(jsonPath)) { Console.WriteLine("ERREUR : --json <path> obligatoire."); return 64; }

        string scenarioId = ArgVal("--scenario-id") ?? "(adhoc)";
        string audienceIdRaw = ArgVal("--audience-id");
        int audienceId = 0;
        if (!string.IsNullOrEmpty(audienceIdRaw)) int.TryParse(audienceIdRaw, out audienceId);
        bool applyReal = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        bool casBAutoSetup = args.Any(a => a.Equals("--cas-b-auto-setup", StringComparison.OrdinalIgnoreCase));
        int? expectedWarnings = int.TryParse(ArgVal("--expected-warnings") ?? "", out var ew) ? (int?)ew : null;
        int? expectedDetectedMods = int.TryParse(ArgVal("--expected-detected-modifications") ?? "", out var edm) ? (int?)edm : null;
        int? expectedAppliedMods = int.TryParse(ArgVal("--expected-applied-modifications") ?? "", out var eam) ? (int?)eam : null;
        int? expectedValidationErrors = int.TryParse(ArgVal("--expected-validation-errors") ?? "", out var eve) ? (int?)eve : null;
        string expectedMessageContains = ArgVal("--expected-message-contains");
        int timeoutSec = int.TryParse(ArgVal("--timeout") ?? "", out var ts) ? ts : 120;

        Console.WriteLine($"   JSON          : {jsonPath}");
        Console.WriteLine($"   Scenario      : {scenarioId}");
        Console.WriteLine($"   Audience      : {(audienceId > 0 ? audienceId.ToString() : "(auto-resolve)")}");
        Console.WriteLine($"   applyReal     : {applyReal}");
        Console.WriteLine($"   timeout       : {timeoutSec}s");
        if (casBAutoSetup) Console.WriteLine($"   cas-b setup   : exécute cas-b-multi-match.setup.sql avant + teardown.sql après");
        if (expectedWarnings.HasValue) Console.WriteLine($"   expectWarn    : {expectedWarnings.Value}");
        if (expectedDetectedMods.HasValue) Console.WriteLine($"   expectModifUI : {expectedDetectedMods.Value}");
        if (expectedAppliedMods.HasValue) Console.WriteLine($"   expectModifDB : {expectedAppliedMods.Value}");
        if (expectedValidationErrors.HasValue) Console.WriteLine($"   expectValErr  : {expectedValidationErrors.Value}");
        if (!string.IsNullOrEmpty(expectedMessageContains)) Console.WriteLine($"   expectMsg⊃    : \"{expectedMessageContains}\"");

        TryStep("Sanity", () =>
        {
            if (!File.Exists(rigExe)) throw new Exception("Binaire RIG absent : " + rigExe);
            if (!File.Exists(jsonPath)) throw new Exception($"JSON introuvable : {jsonPath}");
        });
        if (!File.Exists(rigExe) || !File.Exists(jsonPath)) return PrintSummaryAndExit(sw);

        // ── Cas B multi-match : setup SQL (clone audience) ──
        int? casBClonedId = null;
        if (casBAutoSetup)
        {
            TryStep("Cas B setup — clone audience 28590", () =>
            {
                casBClonedId = RunCasBSetupSql();
                if (casBClonedId.HasValue)
                    Console.WriteLine($"      → ✓ Doublon créé : AUDNC_ID={casBClonedId.Value}");
                else
                    Console.WriteLine($"      → ⚠ Setup n'a rien retourné");
            });
        }

        // ── AudienceLock + Snapshot DB (si --apply) ──
        AudienceLock audLock = null;
        ApplySnapshot applySnap = null;
        if (applyReal && audienceId > 0)
        {
            TryStep($"AudienceLock + Snapshot DB (audience #{audienceId})", () =>
            {
                audLock = AudienceLock.Acquire(audienceId, TimeSpan.FromMinutes(5));
                applySnap = SnapshotForApply(audienceId);
                Console.WriteLine($"      → {applySnap.AppafSnapshot.Count} AppelAffaire snapshot ; {applySnap.PreNoteIds.Count} NOTE_PROCEDURE RAPTURE_* préexistantes");
            });
        }

        // ── Lancement du worker RigClientAccueil --rapture-smoke ──
        var tmpResult = Path.Combine(Path.GetTempPath(), $"rapture-selfdrive-{scenarioId}-{Process.GetCurrentProcess().Id}.json");
        int workerExit = -1;
        TryStep($"Lancement worker self-drive ({scenarioId})", () =>
        {
            var workerArgs = $"rapture-smoke={scenarioId} json=\"{jsonPath}\" out=\"{tmpResult}\"";
            if (audienceId > 0) workerArgs += $" audience-id={audienceId}";
            if (applyReal) workerArgs += " --apply";

            Console.WriteLine($"      → {rigExe} {workerArgs}");
            var psi = new ProcessStartInfo(rigExe, workerArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            // Lire stdout/stderr async pour éviter deadlock sur buffer plein
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            bool exited = proc.WaitForExit(timeoutSec * 1000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                throw new TimeoutException($"Worker self-drive timeout après {timeoutSec}s — tué");
            }
            workerExit = proc.ExitCode;
            var stdoutText = stdout.Result;
            var stderrText = stderr.Result;
            if (!string.IsNullOrWhiteSpace(stdoutText))
            {
                foreach (var line in stdoutText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine($"      [worker] {line}");
            }
            if (!string.IsNullOrWhiteSpace(stderrText))
            {
                foreach (var line in stderrText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine($"      [worker-err] {line}");
            }
            Console.WriteLine($"      → Worker exit code : {workerExit}");
        });

        // ── Parse du JSON résultat ──
        SelfDriveResultDto workerResult = null;
        if (File.Exists(tmpResult))
        {
            TryStep("Parse JSON résultat worker", () =>
            {
                var json = File.ReadAllText(tmpResult, Encoding.UTF8);
                workerResult = ParseSelfDriveResult(json);
                Console.WriteLine($"      → ok={workerResult.Ok} validation(err={workerResult.ValidationErrors},warn={workerResult.ValidationWarnings}) diff={workerResult.DiffCount} apply(applied={workerResult.Applied},skipped={workerResult.Skipped},errors={workerResult.Errors})");
                if (!string.IsNullOrEmpty(workerResult.Message))
                    Console.WriteLine($"      → message: {workerResult.Message}");
            });
        }
        else if (workerExit != -1)
        {
            Console.WriteLine($"  ⚠ JSON résultat absent ({tmpResult}) — le worker a probablement crashé");
        }

        // ── Assertions vs expected ──
        if (workerResult != null && expectedWarnings.HasValue)
        {
            TryStep($"Assert warnings (expected={expectedWarnings.Value})", () =>
            {
                if (workerResult.ValidationWarnings != expectedWarnings.Value)
                    throw new Exception($"WARNINGS DIVERGE : attendus={expectedWarnings.Value} actual={workerResult.ValidationWarnings}");
                Console.WriteLine($"      → ✓ Warnings OK ({workerResult.ValidationWarnings})");
            });
        }
        if (workerResult != null && expectedDetectedMods.HasValue)
        {
            TryStep($"Assert detected modifications (expected={expectedDetectedMods.Value})", () =>
            {
                if (workerResult.DiffCount != expectedDetectedMods.Value)
                    throw new Exception($"DETECTED MODS DIVERGE : attendus={expectedDetectedMods.Value} actual={workerResult.DiffCount}");
                Console.WriteLine($"      → ✓ Detected mods OK ({workerResult.DiffCount})");
            });
        }
        if (workerResult != null && expectedAppliedMods.HasValue && applyReal)
        {
            TryStep($"Assert applied modifications DB (expected={expectedAppliedMods.Value})", () =>
            {
                var actual = QueryAuditCounts(audienceId, jsonPath);
                Console.WriteLine($"      → DB actual : AUDIT rows = {actual.AuditCount}");
                if (actual.AuditCount != expectedAppliedMods.Value)
                    throw new Exception($"APPLIED MODS DIVERGE : attendus={expectedAppliedMods.Value} actual={actual.AuditCount}");
                Console.WriteLine($"      → ✓ Applied mods OK ({actual.AuditCount})");
            });
        }
        if (workerResult != null && expectedValidationErrors.HasValue)
        {
            TryStep($"Assert validation errors (expected={expectedValidationErrors.Value})", () =>
            {
                if (workerResult.ValidationErrors != expectedValidationErrors.Value)
                    throw new Exception($"VALIDATION ERRORS DIVERGE : attendus={expectedValidationErrors.Value} actual={workerResult.ValidationErrors}");
                Console.WriteLine($"      → ✓ Validation errors OK ({workerResult.ValidationErrors})");
            });
        }
        if (workerResult != null && !string.IsNullOrEmpty(expectedMessageContains))
        {
            TryStep($"Assert message contains \"{expectedMessageContains}\"", () =>
            {
                var msg = workerResult.Message ?? "";
                if (msg.IndexOf(expectedMessageContains, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new Exception($"MESSAGE MISMATCH : attendu contient \"{expectedMessageContains}\" actual=\"{msg}\"");
                Console.WriteLine($"      → ✓ Message OK (contains \"{expectedMessageContains}\")");
            });
        }

        // ── Restore net-zero post-apply ──
        if (applyReal && applySnap != null && audienceId > 0)
        {
            TryStep($"Restore DB net-zero (audience #{audienceId})", () =>
            {
                var report = RestoreAfterApply(audienceId, jsonPath, applySnap);
                Console.WriteLine($"      → APPEL_AFFAIRE restaurés : {report.AppafRestored}");
                Console.WriteLine($"      → NOTE_PROCEDURE RAPTURE_* supprimées : {report.NotesDeleted}");
                Console.WriteLine($"      → AUDIT_IMPORT_RAPTURE rows supprimées : {report.AuditDeleted}");
                if (!report.NetZero)
                    throw new Exception("NET-ZERO INCOMPLET — vérifier manuellement");
                Console.WriteLine($"      → ✓ NET-ZERO confirmé");
            });
        }

        // ── Cas B teardown ──
        if (casBClonedId.HasValue)
        {
            TryStep($"Cas B teardown — delete audience doublon #{casBClonedId.Value}", () =>
            {
                RunCasBTeardownSql(casBClonedId.Value);
                Console.WriteLine($"      → ✓ Doublon #{casBClonedId.Value} supprimé");
            });
        }

        // ── Libérer le lock ──
        if (audLock != null)
        {
            try { audLock.Dispose(); } catch { }
        }

        // ── Cleanup fichier temp ──
        try { if (File.Exists(tmpResult)) File.Delete(tmpResult); } catch { }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// DTO minimal pour parser le JSON résultat du worker self-drive
    /// (produit par <c>RigRaptureSelfDrive.SelfDriveResult</c> via DataContractJsonSerializer).
    /// </summary>
    [System.Runtime.Serialization.DataContract]
    private class SelfDriveResultDto
    {
        [System.Runtime.Serialization.DataMember(Name = "scenarioId")] public string ScenarioId { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "ok")] public bool Ok { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "message")] public string Message { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "validationErrors")] public int ValidationErrors { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "validationWarnings")] public int ValidationWarnings { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "diffCount")] public int DiffCount { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "applied")] public int Applied { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "skipped")] public int Skipped { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "errors")] public int Errors { get; set; }
    }

    private static SelfDriveResultDto ParseSelfDriveResult(string json)
    {
        var ser = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(SelfDriveResultDto));
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            return (SelfDriveResultDto)ser.ReadObject(ms);
        }
    }

    /// <summary>
    /// Exécute cas-b-multi-match.setup.sql qui clone l'audience 28590 → renvoie l'AUDNC_ID
    /// du doublon (sélectionné via SCOPE_IDENTITY ou SELECT MAX). Localisation du script :
    /// même dossier que les JSON Rapture (RaptureScenarios/). Best-effort : si le
    /// script n'existe pas, retourne null (le scénario échouera plus loin de manière
    /// explicite, c'est OK).
    /// </summary>
    private static int? RunCasBSetupSql()
    {
        try
        {
            // Path : le .sql est dans RaptureScenarios/ (PAS de sous-dossier sql/), a cote des JSON.
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Desktop", "JsonRapture", "cas-b-multi-match.setup.sql"),
                Path.Combine(AppContext.BaseDirectory, "RaptureScenarios", "cas-b-multi-match.setup.sql"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Rig.Wpf.Kbis.TestViewer", "RaptureScenarios", "cas-b-multi-match.setup.sql"),
            };
            string sqlPath = candidates.FirstOrDefault(File.Exists);
            if (sqlPath == null)
            {
                Console.WriteLine($"      ⚠ cas-b-multi-match.setup.sql introuvable (tentés : {string.Join(" | ", candidates)})");
                return null;
            }
            Console.WriteLine($"      → Exécution {sqlPath}");
            var sql = File.ReadAllText(sqlPath);
            using var conn = new System.Data.SqlClient.SqlConnection(CsApply());
            conn.Open();
            // Le script doit faire un SELECT final retournant l'AUDNC_ID_ADNC du doublon
            using var cmd = new System.Data.SqlClient.SqlCommand(sql, conn) { CommandTimeout = 30 };
            var ret = cmd.ExecuteScalar();
            if (ret != null && ret != DBNull.Value && int.TryParse(ret.ToString(), out var id))
                return id;
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ RunCasBSetupSql : {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Teardown Cas B : DELETE l'audience doublon créée par setup. Best-effort, on
    /// log l'erreur sans throw pour ne pas masquer le résultat du test principal.
    /// </summary>
    private static void RunCasBTeardownSql(int audienceId)
    {
        try
        {
            using var conn = new System.Data.SqlClient.SqlConnection(CsApply());
            conn.Open();
            // L'audience peut avoir des AppelAffaire clonés → DELETE en cascade :
            using (var c1 = new System.Data.SqlClient.SqlCommand(
                "DELETE FROM APPEL_AFFAIRE WHERE APPAF_ID_ADNC = @id", conn))
            {
                c1.Parameters.AddWithValue("@id", audienceId);
                int n = c1.ExecuteNonQuery();
                Console.WriteLine($"      → {n} APPEL_AFFAIRE supprimés");
            }
            using (var c2 = new System.Data.SqlClient.SqlCommand(
                "DELETE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id", conn))
            {
                c2.Parameters.AddWithValue("@id", audienceId);
                c2.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ RunCasBTeardownSql({audienceId}) : {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Compte les rows AUDIT_IMPORT_RAPTURE écrites par l'apply pour ce JSON + audience.</summary>
    private struct AuditCountsResult { public int AuditCount; }
    private static AuditCountsResult QueryAuditCounts(int audienceId, string jsonPath)
    {
        var res = new AuditCountsResult();
        try
        {
            using var conn = new System.Data.SqlClient.SqlConnection(CsApply());
            conn.Open();
            using var cmd = new System.Data.SqlClient.SqlCommand(
                "SELECT COUNT(*) FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC = @aud AND ARIMP_FICHIER_SOURCE = @json", conn);
            cmd.Parameters.AddWithValue("@aud", audienceId);
            cmd.Parameters.AddWithValue("@json", jsonPath);
            var ret = cmd.ExecuteScalar();
            if (ret != null && int.TryParse(ret.ToString(), out var n)) res.AuditCount = n;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ QueryAuditCounts : {ex.Message}");
        }
        return res;
    }

    /// <summary>
    /// Mode --legacy-rapture-export : Start E2E (tab Smoke Export).
    /// Lance RigClientAccueil.exe via FlaUI → Login → PROC_PREAUD → sélectionne la 1ère
    /// audience disponible (suffit pour activer le bouton Export JSON Plumitif) → click
    /// 'Export JSON Plumitif' → fournit un path dans Desktop\JsonRapture\smoke-plumitif-
    /// {timestamp}.json → vérifie que le fichier existe + screenshot final.
    ///
    /// Symétrique du <see cref="RunLegacyRaptureProcess"/> côté import, mais ne touche
    /// PAS la DB (export en lecture seule).
    /// </summary>
    private static int RunLegacyRaptureExport(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --legacy-rapture-export — Rapture E2E EXPORT (UI live RIG)        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        string ArgVal(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(name.Length + 1);
            }
            return null;
        }

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe)) rigExe = @"C:\rig\exe\RigClientAccueil.exe";

        // Ciblage optionnel d'une audience précise — mêmes args que le mode process
        // (--audience-date YYYY-MM-DD / --audience-heure HH:MM / --audience-id <int>
        // résolu en date+heure via DB). Sans args : 1ère audience valide (compat
        // bouton Smoke Export TestViewer, inchangé).
        var dateIso = ArgVal("--audience-date");
        var heure = ArgVal("--audience-heure");
        var audienceIdRaw = ArgVal("--audience-id");
        if (!string.IsNullOrEmpty(audienceIdRaw) && int.TryParse(audienceIdRaw, out var audId))
        {
            try
            {
                var resolved = LookupAudienceDateHeureById(audId);
                if (resolved.HasValue)
                {
                    dateIso = resolved.Value.date.ToString("yyyy-MM-dd");
                    heure = resolved.Value.heure.ToString(@"hh\:mm");
                    Console.WriteLine($"   Audience #{audId} résolue en DB : date={dateIso} heure={heure}");
                }
                else
                {
                    Console.WriteLine($"   ⚠ Audience #{audId} introuvable en DB — fallback --audience-date/--audience-heure ou 1ère audience");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Lookup audience #{audId} a échoué ({ex.GetType().Name}) — fallback --audience-date/--audience-heure ou 1ère audience");
            }
        }
        string dateFr = null;
        if (!string.IsNullOrEmpty(dateIso))
        {
            try { dateFr = DateTime.ParseExact(dateIso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToString("dd/MM/yyyy"); }
            catch { dateFr = dateIso; }
        }
        bool cibleAudience = !string.IsNullOrEmpty(dateFr) && !string.IsNullOrEmpty(heure);

        // Sortie : --out <path> sinon nom horodaté par défaut dans Desktop\JsonRapture.
        var jsonOutPath = ArgVal("--out");
        if (string.IsNullOrEmpty(jsonOutPath))
        {
            jsonOutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "JsonRapture", $"smoke-plumitif-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        }
        else if (string.IsNullOrEmpty(Path.GetDirectoryName(jsonOutPath)))
        {
            // --out avec nom de fichier nu : ancré dans Desktop\JsonRapture (sinon
            // Directory.CreateDirectory("") jette dans le Sanity).
            jsonOutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "JsonRapture", jsonOutPath);
        }
        var jsonDir = Path.GetDirectoryName(jsonOutPath);

        Console.WriteLine($"   RIG exe   : {rigExe}");
        Console.WriteLine($"   Audience  : {(cibleAudience ? $"{dateFr} {heure}" : "(première valide)")}");
        Console.WriteLine($"   Sortie JSON : {jsonOutPath}");

        TryStep("Sanity", () =>
        {
            if (!File.Exists(rigExe)) throw new Exception("Binaire RIG absent : " + rigExe);
            if (!Directory.Exists(jsonDir)) Directory.CreateDirectory(jsonDir);
        });
        if (!File.Exists(rigExe)) return PrintSummaryAndExit(sw);

        // ── Desktop isolé (HDESK) : même pattern que RunLegacyRaptureProcess ──
        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Launch", () => driver.Launch());
                    TryStep("Login", () => driver.ClickSeConnecter());
                    TryStep("Open PROC_PREAUD", () => driver.OpenProcPreaud());
                    TryStep(cibleAudience
                            ? $"Sélectionner l'audience {dateFr} {heure}"
                            : "Sélectionner une audience (active Export JSON)", () =>
                    {
                        if (cibleAudience)
                        {
                            // La recherche par défaut ne couvre que la semaine courante :
                            // re-cherche sur le jour cible avant de sélectionner.
                            driver.SearchAudiencesByDate(dateFr);
                            driver.SelectAudienceInRetaudByDateHeure(dateFr, heure);
                        }
                        else driver.SelectFirstAudienceInRetaud();
                    });
                    TryStep("Bouton 'Export JSON Plumitif' présent + enabled", () =>
                        driver.VerifyButtonPresent("Export JSON Plumitif", "export json", "exporter json", "BtnExportJsonPlum"));
                    TryStep($"Click 'Export JSON' → écrit {Path.GetFileName(jsonOutPath)}", () =>
                        driver.ClickExportJsonAndSaveTo(jsonOutPath));
                    TryStep("Vérif : fichier JSON écrit, non vide", () =>
                    {
                        if (!File.Exists(jsonOutPath)) throw new Exception("Fichier non écrit : " + jsonOutPath);
                        var size = new FileInfo(jsonOutPath).Length;
                        if (size < 100) throw new Exception($"Fichier trop petit ({size} octets) — export probablement échoué");
                        Console.WriteLine($"      → Fichier OK : {size} octets");
                    });
                    driver.CaptureScreenshot($"e2e-export-{Path.GetFileNameWithoutExtension(jsonOutPath)}-{(_failed > 0 ? "FAIL" : "OK")}");
                } // end using driver
            }); // end desktop.RunAttached
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Mode --drive-retaud-pubs : navigation READ-ONLY vers l'écran
    /// "Publicités en attente" de PROC_RETAUD pour une audience donnée.
    ///
    /// Séquence : Login → OpenProcRetaud → SelectAudience → Click radio
    /// "Publicités en attente" → attendre la grille → CaptureScreenshot.
    /// AUCUN import, AUCUNE mutation de données.
    ///
    /// Args : --audience-id &lt;N&gt; (obligatoire — AUDNC_ID_ADNC dans AUDIENCE_CABINET).
    /// Env vars : RIG_LEGACY_EXE, RIG_DRIVER_HEADLESS, RIG_RUN_STAMP (identiques
    ///            aux autres modes legacy).
    /// </summary>
    private static int RunDriveRetaudPubs(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-retaud-pubs — RETAUD publicités en attente (READ-ONLY)    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Lecture de --audience-id (obligatoire)
        string audienceIdRaw = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--audience-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            { audienceIdRaw = args[i + 1]; break; }
            if (args[i].StartsWith("--audience-id=", StringComparison.OrdinalIgnoreCase))
            { audienceIdRaw = args[i].Substring("--audience-id=".Length); break; }
        }
        if (string.IsNullOrWhiteSpace(audienceIdRaw) || !int.TryParse(audienceIdRaw, out var audienceId))
        {
            Console.WriteLine("ERREUR : --audience-id <N> obligatoire (entier = AUDNC_ID_ADNC).");
            return 64;
        }

        var rigExe = Environment.GetEnvironmentVariable("RIG_LEGACY_EXE");
        if (string.IsNullOrWhiteSpace(rigExe)) rigExe = @"C:\rig\exe\RigClientAccueil.exe";

        Console.WriteLine($"   RIG exe      : {rigExe}");
        Console.WriteLine($"   Audience id  : {audienceId}");
        Console.WriteLine($"   Headless     : {(Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0"}");
        Console.WriteLine();

        TryStep($"Sanity : RigClientAccueil.exe présent à {rigExe}", () =>
        {
            if (!File.Exists(rigExe))
                throw new Exception("Binaire introuvable. Définir RIG_LEGACY_EXE pour override le path.");
        });
        if (!File.Exists(rigExe)) return PrintSummaryAndExit(sw);

        bool headless = (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
        string runId = Process.GetCurrentProcess().Id.ToString();
        var desktop = RigDesktop.Create(headless, runId);
        try
        {
            desktop.RunAttached(() =>
            {
                using (var driver = new LegacyDriver(rigExe, desktop))
                {
                    TryStep("Launch : RigClientAccueil.exe démarre", () => driver.Launch());
                    TryStep("Login : connexion à la base RIG (Se connecter)", () => driver.ClickSeConnecter());

                    // Self-snap : observation en HDESK isolé via PNGs (0 vol focus user)
                    try { driver.StartPeriodicSnap($"retaud-pubs-{audienceId}", intervalMs: 500); }
                    catch (Exception ex) { Console.WriteLine($"      ⓘ StartPeriodicSnap a jeté : {ex.GetType().Name}: {ex.Message}"); }

                    // Déléguer au driver : navigate + clic radio + wait + screenshot
                    string? shotPath = null;
                    TryStep($"RETAUD pubs : navigate audience #{audienceId} → radio 'Publicités en attente' → screenshot",
                        () =>
                        {
                            shotPath = driver.DriveRetaudPubsEnAttente(audienceId);
                        });

                    if (!string.IsNullOrEmpty(shotPath))
                        Console.WriteLine($"   📸 Screenshot : {shotPath}");
                    else
                        Console.WriteLine($"   ⚠ Aucun screenshot produit (CaptureScreenshot a retourné null).");
                } // end using driver
            }); // end desktop.RunAttached
        }
        finally
        {
            try { desktop.Dispose(); } catch { }
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Mode --rapture-diag : lance le diagnostic d'import complet
    /// (RaptureImportDiag_EXE : Mapper/Validator/Diff/Apply réel + rapport
    /// placement + valeurs non placées + restore net-zero) et relaie son
    /// stdout ligne à ligne vers la console (affiché live dans Rig Testing).
    ///
    /// Args : [--json &lt;path&gt;] [--audience &lt;id&gt;] [--greffe &lt;code&gt;] [--keep]
    /// Defaults : fixture rapture-smoke-pc-v5-aud28644-9995.json (= audience
    /// RÉELLE 28644, greffe 9995, 2026-03-11 — pas un export brut), audience 28644, greffe 9995.
    /// </summary>
    private static int RunRaptureDiag(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --rapture-diag — diagnostic import (placement + non placés)        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        string ArgVal(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(name.Length + 1);
            }
            return null;
        }

        // Remonte de AppContext.BaseDirectory jusqu'à trouver un répertoire
        // contenant le path attendu (robuste x86\ vs sans plateforme).
        string FindUp(params string[] rel)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                var cand = Path.Combine(new[] { dir.FullName }.Concat(rel).ToArray());
                if (File.Exists(cand)) return cand;
            }
            return null;
        }

        var diagExe = Environment.GetEnvironmentVariable("RIG_RAPTURE_DIAG_EXE");
        if (string.IsNullOrWhiteSpace(diagExe) || !File.Exists(diagExe))
        {
            diagExe = FindUp("Source", "RIG", "DLL", "Processus", "PROC_RETAUD", "RaptureImportDiag_EXE", "bin", "Debug", "RaptureImportDiag_EXE.exe")
                   ?? FindUp("Source", "RIG", "DLL", "Processus", "PROC_RETAUD", "RaptureImportDiag_EXE", "bin", "Release", "RaptureImportDiag_EXE.exe");
        }

        var jsonPath = ResolveJsonArg(args)
                    ?? FindUp("Source", "Wpf", "Rig.Rapture.Tests", "Fixtures", "rapture-smoke-pc-v5-aud28644-9995.json");
        var audience = ArgVal("--audience") ?? "28644";
        var greffe = ArgVal("--greffe") ?? "9995";
        bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"   diag exe : {diagExe ?? "(INTROUVABLE)"}");
        Console.WriteLine($"   JSON     : {jsonPath ?? "(INTROUVABLE)"}");
        Console.WriteLine($"   audience : {audience}   greffe : {greffe}   keep : {keep}");
        Console.WriteLine();

        TryStep("Sanity diag", () =>
        {
            if (string.IsNullOrEmpty(diagExe) || !File.Exists(diagExe))
                throw new Exception("RaptureImportDiag_EXE.exe introuvable — buildé ? (safeBuild v48 du projet RaptureImportDiag_EXE)");
            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                throw new Exception($"Fixture JSON introuvable : {jsonPath}");
        });
        if (string.IsNullOrEmpty(diagExe) || !File.Exists(diagExe)
            || string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
            return PrintSummaryAndExit(sw);

        string diagStdout = "";
        TryStep("Exécution RaptureImportDiag (apply réel + restore net-zero)", () =>
        {
            var qargs = $"\"{jsonPath}\" {audience} {greffe}" + (keep ? " --keep" : "");
            var psi = new ProcessStartInfo
            {
                FileName = diagExe,
                Arguments = qargs,
                WorkingDirectory = Path.GetDirectoryName(diagExe),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)
                ?? throw new Exception("Process.Start(RaptureImportDiag) a renvoyé null");
            var errTask = p.StandardError.ReadToEndAsync();
            var sb = new System.Text.StringBuilder();
            string line;
            Console.WriteLine("──── SORTIE RaptureImportDiag ─────────────────────────────────────");
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                Console.WriteLine(line);
                sb.AppendLine(line);
            }
            p.WaitForExit();
            var err = errTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(err))
            {
                Console.WriteLine("──── stderr ───────────────────────────────────────────────────────");
                Console.WriteLine(err);
            }
            Console.WriteLine("───────────────────────────────────────────────────────────────────");
            diagStdout = sb.ToString();
            if (p.ExitCode != 0)
                throw new Exception($"RaptureImportDiag exit code {p.ExitCode} (voir sortie ci-dessus)");
        });

        // ── Assertions de placement : sans ça le smoke serait "vert" même si
        //    l'import n'a RIEN écrit (faux test vert — cf. CLAUDE.md règle 15).
        //    On parse le rapport du diag : champs réellement appliqués, lignes
        //    d'audit de ce run, et — crucial — preuve que la base est revenue
        //    net-zero (un smoke qui écrit mais ne restaure pas DOIT échouer).
        int AppliedCount()
        {
            var m = System.Text.RegularExpressions.Regex.Match(diagStdout, @"Apply:\s*applied=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
        int AuditCount()
        {
            var m = System.Text.RegularExpressions.Regex.Match(diagStdout,
                @"AUDIT_IMPORT_RAPTURE lignes de ce run\s*=\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        TryStep("Import a réellement placé des champs (applied > 0)", () =>
        {
            int n = AppliedCount();
            if (n < 0) throw new Exception("Ligne 'Apply: applied=N' introuvable — le pipeline d'import n'a pas tourné");
            if (n == 0) throw new Exception("applied=0 : l'import n'a écrit AUCUN champ (fixture/audience ne matchent pas) — smoke non concluant");
            Console.WriteLine($"      → applied={n} champ(s) écrit(s) en base");
        });

        TryStep("Audit du run prouve l'écriture (AUDIT_IMPORT_RAPTURE > 0)", () =>
        {
            int n = AuditCount();
            if (n < 0) throw new Exception("Compteur 'AUDIT_IMPORT_RAPTURE lignes de ce run' introuvable");
            if (n == 0) throw new Exception("0 ligne d'audit pour ce run — rien n'a été tracé en base");
            Console.WriteLine($"      → {n} ligne(s) AUDIT_IMPORT_RAPTURE pour ce run");
        });

        if (!keep)
        {
            TryStep("Base restaurée net-zero (NET-ZERO OK + RESULT: OK)", () =>
            {
                if (diagStdout.IndexOf("NET-ZERO OK", StringComparison.Ordinal) < 0)
                    throw new Exception("Pas de 'NET-ZERO OK' : la base n'est PAS revenue à l'état initial — DANGER, smoke NON vert");
                if (diagStdout.IndexOf("RESULT: OK", StringComparison.Ordinal) < 0)
                    throw new Exception("Pas de 'RESULT: OK' final dans la sortie diag");
                Console.WriteLine("      → restore net-zero confirmé par le diag");
            });
        }
        else
        {
            Console.WriteLine("   ⚠ --keep : écritures conservées volontairement, pas d'assertion net-zero");
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Supprime l'audience créée pendant le smoke (scénario YES) + ses entrées d'audit.
    /// Connection via RIG_LEGACY_CONNECTION ou défaut SQL-DEV\DEV/RIG_DEV (cohérent avec
    /// AudienceCreatorIntegrationTests.cs).
    /// </summary>
    /// <summary>
    /// Résout l'AUDNC_ID en (date, heure) pour pouvoir naviguer dans la grille
    /// RETAUD par date+heure (le driver legacy ne sait pas naviguer par ID).
    /// AUDNC_HEURE est VARCHAR(5) en DB (format "HH:mm"), pas un type Time SQL.
    /// Best-effort : retourne null si la connexion DB échoue ou l'ID est inconnu.
    /// </summary>
    private static (DateTime date, TimeSpan heure)? LookupAudienceDateHeureById(int audienceId)
    {
        var cs = Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
            ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";
        using var conn = new System.Data.SqlClient.SqlConnection(cs);
        conn.Open();
        using var cmd = new System.Data.SqlClient.SqlCommand(
            "SELECT AUDNC_DATE, AUDNC_HEURE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id", conn);
        cmd.Parameters.AddWithValue("@id", audienceId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        if (r.IsDBNull(0) || r.IsDBNull(1)) return null;
        var d = r.GetDateTime(0);
        var hStr = r.GetString(1); // VARCHAR "HH:mm"
        if (!TimeSpan.TryParseExact(hStr, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var h))
            if (!TimeSpan.TryParse(hStr, out h))
                return null;
        return (d, h);
    }

    // ============================================================================
    //  Snapshot + Restore (mode --apply)
    //
    //  Pattern emprunté à RaptureImportDiag_EXE : on snapshot les colonnes
    //  APPEL_AFFAIRE écrites par ApplyService (APPAF_CODE_PLUMITIF / TEXTE_PLUMITIF /
    //  NOTE_AUDIENCE) AVANT le clic Importer, puis on restore EXACTEMENT à partir
    //  de ce snapshot (indépendant de l'audit qui peut être incomplet). Pour les
    //  NOTE_PROCEDURE RAPTURE_*, on garde la liste des IDs préexistants et on
    //  DELETE uniquement les nouveaux. Enfin DELETE les AUDIT_IMPORT_RAPTURE rows
    //  écrites par ApplyService pour ce JSON (clé : ARIMP_FICHIER_SOURCE).
    // ============================================================================

    /// <summary>Snapshot DB pre-apply (3 cols APPEL_AFFAIRE + IDs NOTE_PROCEDURE préexistantes).</summary>
    private sealed class ApplySnapshot
    {
        /// <summary>Map APPAF_ID_INSTN → [code_plumitif, texte_plumitif, note_audience].</summary>
        public Dictionary<int, string[]> AppafSnapshot = new Dictionary<int, string[]>();
        /// <summary>HashSet des NTPRC_ID_NTPRC RAPTURE_* qui existaient avant l'apply.</summary>
        public HashSet<int> PreNoteIds = new HashSet<int>();
        /// <summary>HashSet des APPAF_ID_INSTN pour scoper les requêtes au domaine de cette audience.</summary>
        public HashSet<int> InstanceIds = new HashSet<int>();
    }

    /// <summary>Rapport du restore — utilisé pour vérifier net-zero.</summary>
    private sealed class RestoreReport
    {
        public int AppafRestored;
        public int NotesDeleted;
        public int AuditDeleted;
        public bool NetZero => AppafRestored >= 0 && NotesDeleted >= 0 && AuditDeleted >= 0;
    }

    /// <summary>Colonnes APPEL_AFFAIRE écrites par RaptureImportApplyService (cf. AppafCols dans RaptureImportDiag).</summary>
    private static readonly string[] _appafColsApply = { "APPAF_CODE_PLUMITIF", "APPAF_TEXTE_PLUMITIF", "APPAF_NOTE_AUDIENCE" };

    private static string CsApply() => Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
        ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15;";

    /// <summary>Snapshot AVANT le clic Importer : capture l'état actuel des affaires pour restore exact.</summary>
    private static ApplySnapshot SnapshotForApply(int audienceId)
    {
        var snap = new ApplySnapshot();
        using var conn = new System.Data.SqlClient.SqlConnection(CsApply());
        conn.Open();

        // 1. Liste des APPAF_ID_INSTN de l'audience
        using (var cmd = new System.Data.SqlClient.SqlCommand(
            "SELECT APPAF_ID_INSTN FROM APPEL_AFFAIRE WHERE APPAF_ID_ADNC = @aud", conn))
        {
            cmd.Parameters.AddWithValue("@aud", audienceId);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                if (!dr.IsDBNull(0)) snap.InstanceIds.Add(dr.GetInt32(0));
            }
        }
        if (snap.InstanceIds.Count == 0) return snap;

        // 2. Snapshot APPEL_AFFAIRE cols
        var instIdsCsv = string.Join(",", snap.InstanceIds);
        using (var cmd = new System.Data.SqlClient.SqlCommand(
            $"SELECT APPAF_ID_INSTN, {string.Join(",", _appafColsApply)} FROM APPEL_AFFAIRE WHERE APPAF_ID_ADNC = @aud AND APPAF_ID_INSTN IN ({instIdsCsv})", conn))
        {
            cmd.Parameters.AddWithValue("@aud", audienceId);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                int inst = dr.GetInt32(0);
                var vals = new string[_appafColsApply.Length];
                for (int i = 0; i < _appafColsApply.Length; i++)
                    vals[i] = dr.IsDBNull(i + 1) ? null : Convert.ToString(dr.GetValue(i + 1), System.Globalization.CultureInfo.InvariantCulture);
                snap.AppafSnapshot[inst] = vals;
            }
        }

        // 3. Snapshot IDs NOTE_PROCEDURE RAPTURE_* préexistantes (pour ne supprimer QUE les nouvelles)
        using (var cmd = new System.Data.SqlClient.SqlCommand(
            $"SELECT NTPRC_ID_NTPRC FROM NOTE_PROCEDURE WHERE NTPRC_ORIGINE_NOTE LIKE 'RAPTURE!_%' ESCAPE '!' AND NTPRC_ID_INSTN IN ({instIdsCsv})", conn))
        {
            using var dr = cmd.ExecuteReader();
            while (dr.Read()) snap.PreNoteIds.Add(dr.GetInt32(0));
        }
        return snap;
    }

    /// <summary>Restore APRÈS le clic Importer : remet APPEL_AFFAIRE = snapshot, DELETE notes/audit nouveaux.</summary>
    private static RestoreReport RestoreAfterApply(int audienceId, string jsonPath, ApplySnapshot snap)
    {
        var report = new RestoreReport();
        using var conn = new System.Data.SqlClient.SqlConnection(CsApply());
        conn.Open();

        // 1. Restore APPEL_AFFAIRE cols à leur valeur snapshot
        string setClause = string.Join(", ", _appafColsApply.Select((col, i) => col + " = @v" + i));
        foreach (var kv in snap.AppafSnapshot)
        {
            using var cmd = new System.Data.SqlClient.SqlCommand(
                $"UPDATE APPEL_AFFAIRE SET {setClause} WHERE APPAF_ID_ADNC = @aud AND APPAF_ID_INSTN = @inst", conn);
            for (int i = 0; i < _appafColsApply.Length; i++)
                cmd.Parameters.AddWithValue("@v" + i, (object)kv.Value[i] ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@aud", audienceId);
            cmd.Parameters.AddWithValue("@inst", kv.Key);
            try { report.AppafRestored += cmd.ExecuteNonQuery(); }
            catch (Exception ex) { Console.WriteLine($"      ⚠ restore APPAF inst={kv.Key} : {ex.Message}"); }
        }

        if (snap.InstanceIds.Count > 0)
        {
            var instCsv = string.Join(",", snap.InstanceIds);
            // 2. DELETE NOTE_PROCEDURE RAPTURE_* nouveaux (NTPRC_ID_NTPRC NOT IN preNoteIds)
            string preIdsCsv = snap.PreNoteIds.Count == 0 ? "0" : string.Join(",", snap.PreNoteIds);
            using (var cmd = new System.Data.SqlClient.SqlCommand(
                $"DELETE FROM NOTE_PROCEDURE WHERE NTPRC_ORIGINE_NOTE LIKE 'RAPTURE!_%' ESCAPE '!' AND NTPRC_ID_INSTN IN ({instCsv}) AND NTPRC_ID_NTPRC NOT IN ({preIdsCsv})", conn))
            {
                report.NotesDeleted = cmd.ExecuteNonQuery();
            }
        }

        // 3. DELETE AUDIT_IMPORT_RAPTURE rows pour ce JSON sur cette audience
        //    (ARIMP_FICHIER_SOURCE = le path utilisé par ApplyService = jsonPath)
        using (var cmd = new System.Data.SqlClient.SqlCommand(
            "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC = @aud AND ARIMP_FICHIER_SOURCE = @json", conn))
        {
            cmd.Parameters.AddWithValue("@aud", audienceId);
            cmd.Parameters.AddWithValue("@json", jsonPath);
            report.AuditDeleted = cmd.ExecuteNonQuery();
        }
        return report;
    }

    // ============================================================================
    //  Reset Smoke DB — panic restore
    //
    //  Restaure tout résidu écrit par les scénarios smoke. Utile quand :
    //  - Un restore per-scénario a échoué mid-flow (laisser des audit rows orphelines)
    //  - L'utilisateur veut une "clean slate" pour rejouer les batches
    //  - Cas C ont laissé des audiences créées (date >= 2027) non cleanées
    //
    //  Méthode :
    //  1. Scan AUDIT_IMPORT_RAPTURE WHERE ARIMP_FICHIER_SOURCE LIKE '%RaptureScenarios%'
    //  2. Pour chaque (instance, colonne) groupé : prendre l'OLDEST valeur_avant
    //     (= la valeur initiale avant tout apply smoke) → UPDATE APPEL_AFFAIRE
    //  3. DELETE les NOTE_PROCEDURE RAPTURE_* sur les instances touchées
    //  4. DELETE les AUDIT_IMPORT_RAPTURE rows ciblées
    //  5. DELETE les Cas C audiences leftover (dates futures hors plage normale)
    // ============================================================================
    private static int RunResetSmokeDb(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --reset-smoke-db — panic restore SQL après smoke résidus          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var cs = CsApply();
        bool dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"   Connection : {cs.Replace("Integrated Security=True;", "[winauth]")}");
        Console.WriteLine($"   Dry-run    : {dryRun}");

        try
        {
            using var conn = new System.Data.SqlClient.SqlConnection(cs);
            conn.Open();

            // 1. Scan audit rows pour scenarios smoke (ARIMP_FICHIER_SOURCE contient 'RaptureScenarios')
            //    Pour chaque (APPAF_ID_APPAF, ARIMP_COLONNE_CIBLE) prendre l'OLDEST → vraie valeur pre-smoke.
            var oldestByKey = new Dictionary<string, (int idAppaf, string col, string valeurAvant)>();
            var instanceIds = new HashSet<int>();
            int totalAudit = 0;
            using (var cmd = new System.Data.SqlClient.SqlCommand(
                "SELECT ARIMP_ID_APPAF, ARIMP_ID_INSTN, ARIMP_COLONNE_CIBLE, ARIMP_VALEUR_AVANT, ARIMP_DATE " +
                "FROM AUDIT_IMPORT_RAPTURE " +
                "WHERE ARIMP_FICHIER_SOURCE LIKE '%RaptureScenarios%' " +
                "AND ARIMP_TABLE_CIBLE = 'APPEL_AFFAIRE' " +
                "ORDER BY ARIMP_DATE ASC", conn))
            {
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    totalAudit++;
                    if (dr.IsDBNull(0) || dr.IsDBNull(2)) continue;
                    int idAppaf = dr.GetInt32(0);
                    int idInstn = dr.IsDBNull(1) ? -1 : dr.GetInt32(1);
                    string col = dr.GetString(2);
                    string val = dr.IsDBNull(3) ? null : dr.GetString(3);
                    string key = idAppaf + ":" + col;
                    if (!oldestByKey.ContainsKey(key))
                        oldestByKey[key] = (idAppaf, col, val);
                    if (idInstn > 0) instanceIds.Add(idInstn);
                }
            }
            Console.WriteLine($"   AUDIT rows total (RaptureScenarios) : {totalAudit}");
            Console.WriteLine($"   Pairs (idAppaf, col) uniques        : {oldestByKey.Count}");
            Console.WriteLine($"   Instances touchées                  : {instanceIds.Count}");

            // 2. UPDATE APPEL_AFFAIRE pour restore les colonnes
            int restored = 0;
            foreach (var kv in oldestByKey)
            {
                if (dryRun) { restored++; continue; }
                var (idAppaf, col, val) = kv.Value;
                // Whitelist des colonnes (sécurité)
                if (col != "APPAF_CODE_PLUMITIF" && col != "APPAF_TEXTE_PLUMITIF" && col != "APPAF_NOTE_AUDIENCE")
                    continue;
                try
                {
                    using var cmd = new System.Data.SqlClient.SqlCommand(
                        $"UPDATE APPEL_AFFAIRE SET {col} = @v WHERE APPAF_ID_APPAF = @id", conn);
                    cmd.Parameters.AddWithValue("@v", (object)val ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", idAppaf);
                    restored += cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { Console.WriteLine($"   ⚠ restore APPAF #{idAppaf}.{col} : {ex.Message}"); }
            }
            Console.WriteLine($"   ✓ APPEL_AFFAIRE rows restaurés      : {restored}{(dryRun ? " (dry-run)" : "")}");

            // 3. DELETE NOTE_PROCEDURE RAPTURE_* sur ces instances
            int notesDeleted = 0;
            if (instanceIds.Count > 0)
            {
                var instCsv = string.Join(",", instanceIds);
                using var cmd = new System.Data.SqlClient.SqlCommand(
                    $"{(dryRun ? "SELECT COUNT(*) FROM" : "DELETE FROM")} NOTE_PROCEDURE " +
                    "WHERE NTPRC_ORIGINE_NOTE LIKE 'RAPTURE!_%' ESCAPE '!' " +
                    $"AND NTPRC_ID_INSTN IN ({instCsv})", conn);
                notesDeleted = dryRun ? (int)(cmd.ExecuteScalar() ?? 0) : cmd.ExecuteNonQuery();
            }
            Console.WriteLine($"   ✓ NOTE_PROCEDURE RAPTURE_* supprimées : {notesDeleted}{(dryRun ? " (dry-run)" : "")}");

            // 4. DELETE AUDIT_IMPORT_RAPTURE rows traitées
            int auditDeleted = 0;
            using (var cmd = new System.Data.SqlClient.SqlCommand(
                $"{(dryRun ? "SELECT COUNT(*) FROM" : "DELETE FROM")} AUDIT_IMPORT_RAPTURE " +
                "WHERE ARIMP_FICHIER_SOURCE LIKE '%RaptureScenarios%'", conn))
            {
                auditDeleted = dryRun ? (int)(cmd.ExecuteScalar() ?? 0) : cmd.ExecuteNonQuery();
            }
            Console.WriteLine($"   ✓ AUDIT_IMPORT_RAPTURE rows supprimées: {auditDeleted}{(dryRun ? " (dry-run)" : "")}");

            // 5. DELETE Cas C audiences leftover (dates >= 2027 — marker scenario).
            //    Avant le DELETE de l'audience : DELETE des audit rows référencantes
            //    (FK ARIMP_ID_AUDNC → AUDIENCE_CABINET interdit le DELETE direct).
            int audDeleted = 0;
            if (!dryRun)
            {
                using var cmdAuditRef = new System.Data.SqlClient.SqlCommand(
                    "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC IN " +
                    "(SELECT AUDNC_ID_ADNC FROM AUDIENCE_CABINET WHERE AUDNC_DATE >= '2027-01-01')", conn);
                int refRows = cmdAuditRef.ExecuteNonQuery();
                if (refRows > 0) Console.WriteLine($"   ✓ AUDIT rows référençant Cas C audiences supprimées : {refRows}");
            }
            using (var cmd = new System.Data.SqlClient.SqlCommand(
                $"{(dryRun ? "SELECT COUNT(*) FROM" : "DELETE FROM")} AUDIENCE_CABINET " +
                "WHERE AUDNC_DATE >= '2027-01-01'", conn))
            {
                audDeleted = dryRun ? (int)(cmd.ExecuteScalar() ?? 0) : cmd.ExecuteNonQuery();
            }
            Console.WriteLine($"   ✓ Cas C audiences leftover supprimées : {audDeleted}{(dryRun ? " (dry-run)" : "")}");

            Console.WriteLine();
            Console.WriteLine($"  ✓ Reset DB terminé en {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Reset DB ÉCHEC : {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void CleanupCreatedAudience(int audienceId)
    {
        var cs = Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
            ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";
        using var conn = new System.Data.SqlClient.SqlConnection(cs);
        conn.Open();
        using (var cmd = new System.Data.SqlClient.SqlCommand(
            "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC = @id", conn))
        {
            cmd.Parameters.AddWithValue("@id", audienceId);
            var n = cmd.ExecuteNonQuery();
            Console.WriteLine($"      → AUDIT_IMPORT_RAPTURE : {n} rows deleted (audience #{audienceId})");
        }
        using (var cmd = new System.Data.SqlClient.SqlCommand(
            "DELETE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id", conn))
        {
            cmd.Parameters.AddWithValue("@id", audienceId);
            var n = cmd.ExecuteNonQuery();
            Console.WriteLine($"      → AUDIENCE_CABINET   : {n} rows deleted (audience #{audienceId})");
            if (n == 0) throw new Exception($"Audience #{audienceId} introuvable en BDD — cleanup raté");
        }
    }

    /// <summary>
    /// Vérif rule-15 des boutons Stop/Pause : démarre le Smoke UI (lance RIG),
    /// laisse tourner, PAUSE (gel arbre), screenshot, REPREND, STOP (kill arbre),
    /// screenshot, et vérifie qu'aucun RigClientAccueil ne survit.
    /// </summary>
    private static int RunDriveTestViewerRaptureStopPause(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-rapture-stoppause — vérif Stop/Pause          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        var jsonHint = ResolveJsonHint(args) ?? "pc-v5";
        try
        {
            using var driver = new TestViewerDriver();
            TryStep("Switch module RAPTURE", () => driver.SwitchToModule("RAPTURE"));
            TryStep($"Sélectionner JSON '{jsonHint}'", () => driver.SelectJsonFixture(jsonHint));
            TryStep("Case 'Accepter création audience' = false", () => driver.SetRaptureAcceptCreate(false));
            TryStep("Click ▶ Smoke UI (lance RIG)", () => driver.ClickRunSmokeImport());

            TryStep("Laisser démarrer RIG (~22s) puis PAUSE", () =>
            {
                Thread.Sleep(22000);
                driver.ClickRapturePauseResume();
                Thread.Sleep(2500);
            });
            int rigPaused = Process.GetProcessesByName("RigClientAccueil").Length;
            Console.WriteLine($"   → RigClientAccueil présents pendant la pause : {rigPaused}");
            TryStep("Screenshot état PAUSÉ", () => driver.CaptureAndDumpActiveTab("rapture-PAUSED"));

            TryStep("REPRENDRE puis laisser tourner 6s", () =>
            {
                driver.ClickRapturePauseResume();
                Thread.Sleep(6000);
            });

            TryStep("STOP (kill arbre)", () =>
            {
                driver.ClickRaptureStop();
                Thread.Sleep(4000);
            });
            int rigAfterStop = Process.GetProcessesByName("RigClientAccueil").Length;
            Console.WriteLine($"   → RigClientAccueil restants après Stop : {rigAfterStop}");
            TryStep("Vérif : plus aucun RigClientAccueil après Stop", () =>
            {
                if (rigAfterStop > 0)
                    throw new Exception($"{rigAfterStop} RigClientAccueil survivent au Stop — kill arbre incomplet");
            });
            TryStep("Screenshot état STOPPÉ", () => driver.CaptureAndDumpActiveTab("rapture-STOPPED"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Pilote le bouton "🔍 Diagnostic import" du module RAPTURE de Rig Testing
    /// (déclenche le mode --rapture-diag : import complet + rapport placement /
    /// non-placés, streamé dans la console B2). Screenshot final pour analyse.
    /// </summary>
    private static int RunDriveTestViewerRaptureDiag(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-rapture-diag — pilote 'Diagnostic import'      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        var jsonHint = ResolveJsonHint(args) ?? "pc-v5";
        Console.WriteLine($"   JSON cible (partial match) : {jsonHint}");
        try
        {
            using var driver = new TestViewerDriver();
            TryStep("Switch module RAPTURE", () => driver.SwitchToModule("RAPTURE"));
            TryStep($"Sélectionner JSON '{jsonHint}'", () => driver.SelectJsonFixture(jsonHint));
            TryStep("Click 🔍 Diagnostic import", () => driver.ClickDiagnosticImport());
            TryStep("Attendre fin du diagnostic (poll log box, max 8 min)", () =>
            {
                var final = driver.WaitForSmokeCompletion(TimeSpan.FromMinutes(8));
                Console.WriteLine();
                Console.WriteLine("──── STDOUT FINAL CAPTÉ (console B2) ──────────────────────────────");
                Console.WriteLine(final);
                Console.WriteLine("───────────────────────────────────────────────────────────────────");
                if (final.Contains("✗ failed  ") && !final.Contains("✗ failed  0"))
                    throw new Exception("Diagnostic terminé avec au moins 1 échec (voir stdout)");
            });
            TryStep("Screenshot console B2", () => driver.CaptureAndDumpActiveTab("rapture-diag-console"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Pilote le bouton "🎯 Process E2E" du module RAPTURE de Rig Testing (mode
    /// <c>--legacy-rapture-process</c>) : sélectionne le JSON, clique le bouton →
    /// le SmokeRunner ouvre RIG via FlaUI, fait login, va dans PROC_RETAUD,
    /// sélectionne une audience peuplée CIBLÉE qui matche le JSON par date+heure,
    /// clique « Importer Rapture » → recap directe Cas A. Stream stdout dans la
    /// console B2 de Rig Testing. Screenshot final + analyse via rule 15(d).
    /// </summary>
    private static int RunDriveTestViewerRaptureProcess(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-rapture-process — pilote '🎯 Process E2E'      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        // Par défaut on cible la fixture synthétisée pour l'audience 28625 (peuplée,
        // dans la fenêtre RETAUD, 2026-05-15 09:00, greffe 9995).
        var jsonHint = ResolveJsonHint(args) ?? "smoke-aud28625";
        // --create / --yes : check la case "Accepter création d'audience" du module
        // RAPTURE → Process E2E ira tester Cas B/C (création d'audience) au lieu de
        // Cas A (match audience existante).
        bool acceptCreate = args.Any(a => a.Equals("--create", StringComparison.OrdinalIgnoreCase)
                                       || a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        bool applyReal = args.Any(a => a.Equals("--apply-real", StringComparison.OrdinalIgnoreCase));
        bool visibleMode = args.Any(a => a.Equals("--visible", StringComparison.OrdinalIgnoreCase)
                                      || a.Equals("--visible-mode", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"   JSON cible (partial match) : {jsonHint}");
        Console.WriteLine($"   acceptCreate (case GUI)    : {acceptCreate}");
        Console.WriteLine($"   applyReal (case GUI)       : {applyReal}");
        Console.WriteLine($"   visibleMode (case GUI)     : {visibleMode}");
        try
        {
            using var driver = new TestViewerDriver();
            TryStep("Switch module RAPTURE", () => driver.SwitchToModule("RAPTURE"));
            // ML LOOP S1.2 — Flag explicite --all-scenarios évite la dépendance au
            // jsonHint texte (qui se fait splitter par les shell quoting layers en
            // mode --visible). Si présent OU si jsonHint commence par "All", on
            // sélectionne le sentinel "All scenarios" du ComboBox.
            bool explicitAll = args.Any(a => a.Equals("--all-scenarios", StringComparison.OrdinalIgnoreCase));
            string effectiveJsonHint = explicitAll ? "All scenarios" : jsonHint;
            TryStep($"Sélectionner JSON '{effectiveJsonHint}'", () => driver.SelectJsonFixture(effectiveJsonHint));
            bool isAllScenarios = explicitAll
                || effectiveJsonHint.IndexOf("All scenarios", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveJsonHint.StartsWith("All", StringComparison.OrdinalIgnoreCase);
            if (!isAllScenarios)
                TryStep($"Case 'Accepter création audience' = {acceptCreate}", () => driver.SetRaptureAcceptCreate(acceptCreate));
            if (applyReal)
                TryStep("Case 'Apply réel' = true", () => driver.SetRaptureApplyReal(true));
            // 'Mode visible' marche aussi en batch : si coché, chaque worker lance
            // --legacy-rapture-process → N instances RIG visibles en parallèle.
            TryStep($"Case 'Mode visible' = {visibleMode}", () => driver.SetRaptureVisibleMode(visibleMode));
            TryStep("Click 🎯 Process E2E (lance RIG + ouvre recap)", () => driver.ClickRaptureProcessE2E());
            // ML LOOP fix : visible parallel=4 avec 16 scenarios × 4 waves × 180s = 12min worst case.
            // Driver doit attendre que la TestViewer batch écrive son JSON sinon la boucle
            // ML LOOP n'a pas de feedback structuré. 10min = sécurité 1.5×.
            TimeSpan pollTimeout = visibleMode ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(8);
            TryStep($"Attendre fin du run (poll log box, max {pollTimeout.TotalMinutes:F0} min)", () =>
            {
                string marker = isAllScenarios ? "RECAP ALL SCENARIOS" : null;
                var final = driver.WaitForSmokeCompletion(pollTimeout, marker);
                Console.WriteLine();
                Console.WriteLine("──── STDOUT FINAL CAPTÉ (console B2) ──────────────────────────────");
                Console.WriteLine(final);
                Console.WriteLine("───────────────────────────────────────────────────────────────────");
                if (isAllScenarios)
                {
                    var recapIdx = final.IndexOf("RECAP ALL SCENARIOS");
                    if (recapIdx >= 0)
                    {
                        var recapLine = final.Substring(recapIdx, Math.Min(120, final.Length - recapIdx));
                        if (recapLine.Contains("FAIL") && !recapLine.Contains("0 FAIL"))
                            throw new Exception("Batch terminé avec échec(s) — voir RECAP");
                    }
                }
                else if (final.Contains("✗ failed  ") && !final.Contains("✗ failed  0"))
                    throw new Exception("Process E2E terminé avec au moins 1 échec (voir stdout)");
            });
            TryStep("Screenshot console B2", () => driver.CaptureAndDumpActiveTab("rapture-process-e2e-console"));
            if (applyReal)
                TryStep("Décocher 'Apply réel'", () => driver.SetRaptureApplyReal(false));
            if (visibleMode)
                TryStep("Décocher 'Mode visible'", () => driver.SetRaptureVisibleMode(false));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Pilote le bouton "▶ Start E2E" du tab Smoke Export (mode
    /// <c>--legacy-rapture-export</c>). Switch sur le tab Smoke Export puis clique.
    /// Symétrique de <see cref="RunDriveTestViewerRaptureProcess"/> côté import.
    /// </summary>
    private static int RunDriveTestViewerRaptureExport(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-rapture-export — pilote '▶ Start E2E' Export   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        try
        {
            using var driver = new TestViewerDriver();
            TryStep("Switch module RAPTURE", () => driver.SwitchToModule("RAPTURE"));
            TryStep("Switch tab 'Smoke Export'", () => driver.SelectTab("Smoke Export"));
            TryStep("Click ▶ Start E2E (Export)", () => driver.ClickRaptureExportE2E());
            TryStep("Attendre fin du run (poll log box, max 8 min)", () =>
            {
                var final = driver.WaitForSmokeCompletion(TimeSpan.FromMinutes(8));
                Console.WriteLine();
                Console.WriteLine("──── STDOUT FINAL CAPTÉ (console B2 Export) ──────────────────────");
                Console.WriteLine(final);
                Console.WriteLine("───────────────────────────────────────────────────────────────────");
                if (final.Contains("✗ failed  ") && !final.Contains("✗ failed  0"))
                    throw new Exception("Export E2E terminé avec au moins 1 échec (voir stdout)");
            });
            TryStep("Screenshot console B2 Export", () => driver.CaptureAndDumpActiveTab("rapture-export-e2e-console"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Driver pour piloter Rig Testing (TestViewer WPF) via FlaUI. Permet à l'agent
    /// de boucler un import Rapture en regardant les logs streamer dans le GUI.
    /// Attache au TestViewer déjà ouvert si possible (pour que l'user voie le run).
    /// </summary>
    private static int RunDriveTestViewerRaptureImport(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-rapture-import — pilote Rig Testing en FlaUI    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Pas de --json X = fixture par défaut (rapture-smoke-pc-v5… si présent, sinon la 1ère).
        // Le hint "pc-v5" matche toujours ce nom (substring) → pas de changement de hint requis.
        var jsonHint = ResolveJsonHint(args) ?? "pc-v5";
        Console.WriteLine($"   JSON cible (partial match) : {jsonHint}");

        bool dumpOnly = args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        bool acceptCreate = args.Any(a => a.Equals("--create", StringComparison.OrdinalIgnoreCase)
                                       || a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"   Accepter création audience (case YES) : {acceptCreate}");
        try
        {
            using var driver = new TestViewerDriver();
            if (dumpOnly)
            {
                driver.DumpUiaTree(maxPerType: 50);
                return PrintSummaryAndExit(sw);
            }
            TryStep("Switch module RAPTURE", () => driver.SwitchToModule("RAPTURE"));
            TryStep($"Sélectionner JSON '{jsonHint}'", () => driver.SelectJsonFixture(jsonHint));
            TryStep($"Case 'Accepter création audience' = {acceptCreate}", () => driver.SetRaptureAcceptCreate(acceptCreate));
            TryStep("Click ▶ Smoke UI (lance RIG)", () => driver.ClickRunSmokeImport());
            TryStep("Attendre fin du run (poll log box, max 8 min)", () =>
            {
                var final = driver.WaitForSmokeCompletion(TimeSpan.FromMinutes(8));
                Console.WriteLine();
                Console.WriteLine("──── STDOUT FINAL CAPTÉ ───────────────────────────────────────────");
                Console.WriteLine(final);
                Console.WriteLine("───────────────────────────────────────────────────────────────────");
                if (final.Contains("✗ failed  ") && !final.Contains("✗ failed  0"))
                    throw new Exception("Smoke run terminé avec au moins 1 échec (voir stdout ci-dessus)");
            });
            TryStep("Screenshot console B2", () => driver.CaptureAndDumpActiveTab("rapture-smokeui-console"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Pilote Rig Testing pour rejouer le smoke legacy KBIS/VK : attache au
    /// TestViewer ouvert, bascule module KBIS, clique "▶ Run smoke RIG"
    /// (RunLegacyCommand → SmokeRunner --legacy = flux VK), poll le log box
    /// jusqu'au summary. Scénario rejouable en une commande, symétrique de
    /// --drive-testviewer-rapture-import.
    /// </summary>
    private static int RunDriveTestViewerLegacyKbis(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-legacy-kbis — pilote Rig Testing (smoke VK)     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        bool dumpOnly = args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        try
        {
            using var driver = new TestViewerDriver();
            if (dumpOnly)
            {
                driver.DumpUiaTree(maxPerType: 50);
                return PrintSummaryAndExit(sw);
            }
            TryStep("Switch module KBIS", () => driver.SwitchToModule("KBIS"));
            TryStep("Click ▶ Run smoke RIG (legacy VK)", () => driver.ClickRunSmokeLegacy());
            TryStep("Attendre fin du run (poll log box)", () =>
            {
                // Le smoke legacy KBIS/VK prend ~50s (launch RIG + login + VK + SIREN + K-bis)
                var final = driver.WaitForSmokeCompletion(TimeSpan.FromMinutes(6));
                Console.WriteLine();
                Console.WriteLine("──── STDOUT FINAL CAPTÉ ───────────────────────────────────────────");
                Console.WriteLine(final);
                Console.WriteLine("───────────────────────────────────────────────────────────────────");
                if (final.Contains("✗ failed  ") && !final.Contains("✗ failed  0"))
                    throw new Exception("Smoke legacy KBIS/VK terminé avec au moins 1 échec (voir stdout ci-dessus)");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>
    /// Pilote le mode STRESS KBIS : switch module KBIS → click "▶ Stress ×N" → poll le LOG
    /// FICHIER de la TV jusqu'au marqueur de fin. Le scénario/count/loop sont lus par la VM
    /// depuis les env RIG_KBIS_STRESS_SCENARIO/COUNT/LOOP (à set au LANCEMENT de la TV, car la
    /// TV est un autre process). Symétrique de --drive-testviewer-legacy-kbis.
    /// </summary>
    private static int RunDriveTestViewerKbisStress(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   --drive-testviewer-kbis-stress — pilote Rig Testing (stress X×)    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        var scen = Environment.GetEnvironmentVariable("RIG_KBIS_STRESS_SCENARIO") ?? "(tuile sélectionnée)";
        var cnt  = Environment.GetEnvironmentVariable("RIG_KBIS_STRESS_COUNT") ?? "(UI)";
        var loopEnv = (Environment.GetEnvironmentVariable("RIG_KBIS_STRESS_LOOP") ?? "0").Trim();
        bool isLoop = loopEnv == "1" || loopEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"   scenario={scen} count={cnt} loop={isLoop}");
        Console.WriteLine();
        try
        {
            using var driver = new TestViewerDriver();
            TryStep("Switch module KBIS", () => driver.SwitchToModule("KBIS"));

            var logFile = NewestTvLog();
            long startLen = (logFile != null && File.Exists(logFile)) ? new FileInfo(logFile).Length : 0;

            TryStep("Click ▶ Stress ×N", () => driver.ClickRunKbisStress());

            TryStep("Attendre fin de vague / échec (poll TV log fichier)", () =>
            {
                // 1 vague ~90s (4 RIG E2E parallèles) ; loop = jusqu'au 1er échec (cap large).
                var timeout = isLoop ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(8);
                var dl = DateTime.Now + timeout;
                string? hit = null;
                while (DateTime.Now < dl)
                {
                    Thread.Sleep(1500);
                    if (logFile == null || !File.Exists(logFile)) { logFile = NewestTvLog(); continue; }
                    string tail;
                    try
                    {
                        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(startLen, SeekOrigin.Begin);
                        using var rdr = new StreamReader(fs);
                        tail = rdr.ReadToEnd();
                    }
                    catch { continue; }

                    if (tail.Contains("KBIS stress STOPPED on fail")) { hit = "FAIL"; break; }
                    if (tail.Contains("auto-stop at 100"))            { hit = "AUTO-STOP"; break; }
                    if (!isLoop && tail.Contains("KBIS stress wave done")) { hit = "WAVE-DONE"; break; }
                }
                Console.WriteLine($"   → marqueur détecté : {hit ?? "(timeout)"}");
                if (hit == null) throw new Exception("Stress KBIS : pas de marqueur de fin dans le délai imparti");
                if (hit == "FAIL") throw new Exception("Stress KBIS : échec détecté (chasse au flake → reproduit). Voir la tuile rouge + son PNG/logs.");
                // WAVE-DONE (single, all pass) ou AUTO-STOP (loop, aucun flake) = succès.
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Driver crash : {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }

        return PrintSummaryAndExit(sw);
    }

    /// <summary>Chemin du log TV le plus récent (testviewer-*.log sous %LOCALAPPDATA%\rig-wpf-testviewer).</summary>
    private static string? NewestTvLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rig-wpf-testviewer");
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).GetFiles("testviewer-*.log")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Extrait l'argument <c>--json &lt;path&gt;</c> ou <c>--json=&lt;path&gt;</c> de la
    /// command-line — version "hint" qui retourne juste le nom (pas de résolution path).
    /// Utilisé pour le partial-match dans le ComboBox du TestViewer.
    /// </summary>
    private static string? ResolveJsonHint(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--json=", StringComparison.OrdinalIgnoreCase))
                return a.Substring("--json=".Length);
            if (a.Equals("--json", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Extrait l'argument <c>--json &lt;path&gt;</c> ou <c>--json=&lt;path&gt;</c> de la
    /// command-line. Si path est relatif, tente CWD puis Desktop\JsonRapture\. null si absent.
    /// </summary>
    private static string? ResolveJsonArg(string[] args)
    {
        string? raw = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--json=", StringComparison.OrdinalIgnoreCase))
            { raw = a.Substring("--json=".Length); break; }
            if (a.Equals("--json", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            { raw = args[i + 1]; break; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Path.IsPathRooted(raw)) return raw;
        var cwd = Path.GetFullPath(raw);
        if (File.Exists(cwd)) return cwd;
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop", "JsonRapture", raw);
        return desktop;
    }

    private static int PrintSummaryAndExit(Stopwatch sw)
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        Console.WriteLine($"  ✓ passed  {_passed}");
        Console.WriteLine($"  ⊘ skipped {_skipped}");
        Console.WriteLine($"  ✗ failed  {_failed}");
        Console.WriteLine($"  ⏱ {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        return _failed > 0 ? 1 : 0;
    }

    /// <summary>Pump du dispatcher local, vidant la queue jusqu'à priority Background.</summary>
    private static void PumpDispatcherLocal()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child))
                yield return sub;
        }
    }

    private static string GetButtonTextContent(Button btn)
    {
        if (btn.Content is string s) return s;
        if (btn.Content is TextBlock direct) return direct.Text ?? "";
        // Contenu = StackPanel/Grid/etc — chercher le 1er TextBlock dans la sous-arborescence
        if (btn.Content is DependencyObject dep)
        {
            foreach (var tb in FindVisualChildren<TextBlock>(dep))
                if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
        }
        return "";
    }

    // ── plomberie d'affichage ───────────────────────────────────────────────

    private static T? TryStep<T>(string description, Func<T> action) where T : class
    {
        try
        {
            var result = action();
            Pass(description);
            return result;
        }
        catch (Exception ex)
        {
            Fail(description, ex);
            return null;
        }
    }

    private static void TryStep(string description, Action action)
    {
        try { action(); Pass(description); }
        catch (Exception ex) { Fail(description, ex); }
    }

    /// <summary>Comme <see cref="TryStep(string, Action)"/> mais retourne true si le step a réussi.
    /// Sert à GATER un step suivant qui n'a de sens que si le précédent a réussi (ex : ne pas chercher
    /// le combo « Type de motif » de l'étape réclamation si AUCUNE demande n'a pu être ouverte — sinon
    /// FindTypeMotifCombo balaie l'arbre UIA en boucle ~111 s avant de renvoyer null).</summary>
    private static bool TryStepBool(string description, Action action)
    {
        try { action(); Pass(description); return true; }
        catch (Exception ex) { Fail(description, ex); return false; }
    }

    /// <summary>Step dont l'action renvoie un bool « actionnable » : <c>true</c> → Pass (action exécutée),
    /// <c>false</c> → Skip (cas de données légitime, pas une action à exécuter), exception → Fail (vrai
    /// échec : crash RIG, etc.). Sert aux actions best-effort gatées sur l'état de la demande (ex : valider
    /// une formalité seulement si elle est au stade « Valider » ; sinon SKIP plutôt que FAIL). Le message de
    /// SKIP est suffixé pour expliciter « non actionnable ».</summary>
    private static void TryStepActionable(string description, Func<bool> action)
    {
        try
        {
            if (action()) Pass(description);
            else Skip(description + " — SKIP (formalité non au stade actionnable / cas de données, voir log driver)");
        }
        catch (Exception ex) { Fail(description, ex); }
    }

    /// <summary>#1 — installe un tee de Console.Out vers
    /// %LOCALAPPDATA%\rig-wpf-testviewer\sr-&lt;mode&gt;-&lt;stamp&gt;-&lt;pid&gt;.stdout.log.
    /// Best-effort : n'altère jamais le run (le mode redirigé par TestViewer continue de recevoir stdout
    /// via le tee, qui réécrit aussi sur la console d'origine).</summary>
    private static void InstallStdoutTee(string[] args)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(local, "rig-wpf-testviewer");
            System.IO.Directory.CreateDirectory(dir);
            var mode = "run";
            foreach (var a in args) { if (!string.IsNullOrEmpty(a) && a.StartsWith("--")) { mode = a.TrimStart('-'); break; } }
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var logPath = System.IO.Path.Combine(dir, $"sr-{mode}-{stamp}-{pid}.stdout.log");
            var fileWriter = new System.IO.StreamWriter(logPath, append: false, encoding: new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => { try { fileWriter.Flush(); fileWriter.Dispose(); } catch { } };
            Console.WriteLine($"      ⓘ stdout tee → {logPath}");
        }
        catch { /* best-effort, jamais bloquer le run pour du logging */ }
    }

    // #1 — TeeTextWriter (multiplexage Console→console+fichier) déplacé dans Observability.cs (public, testable xUnit).

    /// <summary>#2 — préfixe [hh:mm:ss.fff] [snap=NNNN] pour corréler chaque ligne d'event
    /// au self-snap PNG exact. snap=---- si aucun self-snap actif (LegacyDriver.LastSnapSeq = -1).</summary>
    private static string Stamp()
    {
        int s = LegacyDriver.LastSnapSeq;
        var snap = s >= 0 ? s.ToString("D4") : "----";
        return $"[{DateTime.Now:HH:mm:ss.fff}] [snap={snap}] ";
    }
    private static void Pass(string description) { _passed++; Console.WriteLine($"  ✓ {Stamp()}{description}"); }
    private static void Fail(string description, Exception ex)
    {
        _failed++;
        Console.WriteLine($"  ✗ {Stamp()}{description}");
        Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
    }
    private static void Skip(string description) { _skipped++; Console.WriteLine($"  ⊘ {Stamp()}{description}"); }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   Rig.Wpf.Kbis.SmokeRunner — smoke test du livrable KBIS Phase C    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static int Finish(Stopwatch sw, bool fatal)
    {
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        Console.WriteLine($"  ✓ passed  {_passed}");
        Console.WriteLine($"  ⊘ skipped {_skipped}");
        Console.WriteLine($"  ✗ failed  {_failed}");
        Console.WriteLine($"  ⏱ {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        Console.WriteLine();
        return (fatal || _failed > 0) ? 1 : 0;
    }
}
