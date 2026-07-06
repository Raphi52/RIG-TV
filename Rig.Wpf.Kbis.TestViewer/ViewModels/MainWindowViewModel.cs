using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Helpers;
using Rig.Wpf.Kbis.TestViewer.Services;
using Rig.Wpf.Kbis.SmokeRunner; // LegacyParsing : politique + textes des logs de retry-on-transient

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

/// <summary>
/// VM principale — orchestre les modules. Aujourd'hui : KBIS uniquement.
/// Demain : MANDATAIRE / IPE / autres. Chaque module a 3 sous-domaines :
///  1. Smoke WPF  (shell-out SmokeRunner.exe, parse stdout)
///  2. Regression (dotnet test xUnit + TRX parse)
///  3. Scénarios   (.verified.json — snapshots Verify, output attendu par scénario xUnit)
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly TestingPaths _paths;
    private readonly SmokeRunnerProxy _smokeProxy;
    private readonly SmokeRunnerProxy _legacySmokeProxy;
    private readonly RegressionRunner _regression;
    private readonly RegressionCatalog _regressionCatalog;
    private readonly ScenarioFileService _scenarioFiles;
    private readonly TestSourceExtractor _sourceExtractor;
    private readonly RegressionRunner _raptureRegression;
    private readonly RegressionCatalog _raptureRegressionCatalog;
    private readonly SmokeRunnerProxy _raptureSmokeProxy;
    private readonly SmokeRunnerProxy _raptureExportProxy;
    private readonly SmokeRunnerProxy _raptureEdiImportProxy;
    private readonly GlobalSettingsService _globalSettings;
    // ML LOOP Phase 4 — cache des résultats de tests (memoization).
    // Lookup par (codeHash drivers × scenarioId) → PASS connu = skip exec.
    private readonly TestResultCacheService _testCache;

    // ML LOOP Phase 5 — taille écran en pixels réels (vs WPF-units).
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private static int GetSystemMetrics_SmCxScreen() => GetSystemMetrics(0);
    private static int GetSystemMetrics_SmCyScreen() => GetSystemMetrics(1);

    // ML LOOP S2.2 — ROI sur la fenêtre TestViewer plutôt que plein écran.
    // Plein écran inclut wallpaper, taskbar, autres apps → noise élevé sur dHash.
    // GetWindowRect cible juste la fenêtre TV pour un signal plus pur.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public MainWindowViewModel(
        TestingPaths paths,
        SmokeRunnerProxy smokeProxy,
        SmokeRunnerProxy legacySmokeProxy,
        RegressionRunner regression,
        RegressionCatalog regressionCatalog,
        ScenarioFileService scenarioFiles,
        TestSourceExtractor sourceExtractor,
        RegressionRunner raptureRegression,
        RegressionCatalog raptureRegressionCatalog,
        SmokeRunnerProxy raptureSmokeProxy,
        SmokeRunnerProxy raptureExportProxy,
        GlobalSettingsService globalSettings,
        TestResultCacheService testCache,
        SmokeRunnerProxy raptureEdiImportProxy)
    {
        _paths = paths;
        _smokeProxy = smokeProxy;
        _legacySmokeProxy = legacySmokeProxy;
        _regression = regression;
        _regressionCatalog = regressionCatalog;
        _scenarioFiles = scenarioFiles;
        _sourceExtractor = sourceExtractor;
        _raptureRegression = raptureRegression;
        _raptureRegressionCatalog = raptureRegressionCatalog;
        _raptureSmokeProxy = raptureSmokeProxy;
        _raptureExportProxy = raptureExportProxy;
        _raptureEdiImportProxy = raptureEdiImportProxy;
        _globalSettings = globalSettings;
        _testCache = testCache;
        // DB des smoke tests : pose RIG_LEGACY_CONNECTION sur ce process (CLI > persisté > défaut)
        // → héritée par tous les workers SmokeRunner spawnés ensuite.
        DatabaseStartup.Apply(_globalSettings.Current);
        ScenarioDetail = new ScenarioDetailViewModel(
            _scenarioFiles, _sourceExtractor, _regression,
            jumpToXUnit: NavigateToXUnitTest);

        // ── Smoke ─────────────────────────────────────────────────────────
        SmokeScenarios = new ObservableCollection<ScenarioViewModel>(
            SmokeCatalog.All.Select(s => new ScenarioViewModel(s)));
        SmokeView = (ListCollectionView)CollectionViewSource.GetDefaultView(SmokeScenarios);
        SmokeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ScenarioViewModel.Category)));
        SmokeView.Filter = item =>
        {
            var vm = (ScenarioViewModel)item;
            return AnyActiveTagMatches(SmokeFilterTags, vm.Tags);
        };
        SmokeFilterTags = BuildChips(GatherTags(SmokeScenarios.Select(s => (IReadOnlyList<string>)s.Tags)),
            () => SmokeView.Refresh());

        Renders = new ObservableCollection<RenderArtifactViewModel>();
        RefreshRenders();

        _smokeProxy.LinesChanged += OnSmokeLinesChanged;
        _smokeProxy.StateChanged += OnSmokeStateChanged;

        // ── Smoke RIG (legacy WinForms) ──────────────────────────────────
        LegacyScenarios = new ObservableCollection<ScenarioViewModel>(
            LegacySmokeCatalog.All.Select(s => new ScenarioViewModel(s)));
        LegacyView = (ListCollectionView)CollectionViewSource.GetDefaultView(LegacyScenarios);
        // Module Global = uniquement les scénarios cross-fonctionnels (pas de tag module
        // métier). Les scénarios kbis/mandataire/ipe sont rangés dans LEUR module.
        LegacyView.Filter = item => !HasModuleTag(((ScenarioViewModel)item).Tags);

        // Sous-vue dérivée : on partage les MÊMES ScenarioViewModel (le statut se met
        // à jour automatiquement), filtrés sur le tag "kbis" pour le module KBIS.
        // Demain : si MANDATAIRE legacy arrive, on ajoute une équivalente par module.
        KbisLegacyScenarios = new ObservableCollection<ScenarioViewModel>(
            LegacyScenarios.Where(s => s.Tags.Contains("kbis", StringComparer.Ordinal)));
        KbisLegacyView = (ListCollectionView)CollectionViewSource.GetDefaultView(KbisLegacyScenarios);

        // Module ALERTES RCS : sous-vue filtrée sur le tag "alertes" (4 scénarios).
        AlertesLegacyScenarios = new ObservableCollection<ScenarioViewModel>(
            LegacyScenarios.Where(s => s.Tags.Contains("alertes", StringComparer.Ordinal)));
        AlertesLegacyView = (ListCollectionView)CollectionViewSource.GetDefaultView(AlertesLegacyScenarios);

        // Module DCADEMAT (2026-05-29) : sous-vue filtrée sur le tag "dcademat".
        DcadematLegacyScenarios = new ObservableCollection<ScenarioViewModel>(
            LegacyScenarios.Where(s => s.Tags.Contains("dcademat", StringComparer.Ordinal)));
        DcadematLegacyView = (ListCollectionView)CollectionViewSource.GetDefaultView(DcadematLegacyScenarios);
        // Plus de filtre stack global : le module KBIS legacy montre tous ses scénarios.

        _legacySmokeProxy.LinesChanged += OnLegacyLinesChanged;
        _legacySmokeProxy.StateChanged += OnLegacyStateChanged;

        // ── Regression xUnit ──────────────────────────────────────────────
        RegressionTests = new ObservableCollection<RegressionItemViewModel>();
        RegressionView  = (ListCollectionView)CollectionViewSource.GetDefaultView(RegressionTests);
        RegressionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RegressionItemViewModel.Category)));
        RegressionView.Filter = item =>
        {
            var vm = (RegressionItemViewModel)item;
            return AnyActiveTagMatches(RegressionFilterTags, vm.Tags);
        };
        RegressionFilterTags = new ObservableCollection<TagChipViewModel>();

        _regression.ResultsUpdated += OnRegressionResultsUpdated;
        _regression.StateChanged   += OnRegressionStateChanged;
        // Live updates pendant le run : à chaque ligne stdout dotnet test, fire un
        // refresh de la console B2 + des checkmarks individuelles (pas d'attente
        // d'EndOfRun comme avant).
        _regression.LiveStdoutChanged += OnRegressionLiveStdoutChanged;
        _regression.TestProgress      += OnRegressionTestProgress;

        // ── Regression xUnit Rapture (2ème projet, parallèle à KBIS) ──────
        RaptureRegressionTests = new ObservableCollection<RegressionItemViewModel>();
        RaptureRegressionView = (ListCollectionView)CollectionViewSource.GetDefaultView(RaptureRegressionTests);
        // Groupement par catégorie comme la vue KBIS — donne le pattern
        // "Auth & Validation · 6" / "Autres · 14" qui structure les tests live.
        RaptureRegressionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RegressionItemViewModel.Category)));
        RaptureRegressionView.Filter = item =>
        {
            var vm = (RegressionItemViewModel)item;
            return AnyActiveTagMatches(RaptureRegressionFilterTags, vm.Tags);
        };
        RaptureRegressionFilterTags = new ObservableCollection<TagChipViewModel>();
        _raptureRegression.ResultsUpdated += OnRaptureRegressionResultsUpdated;
        _raptureRegression.StateChanged   += OnRaptureRegressionStateChanged;
        _raptureRegression.LiveStdoutChanged += OnRaptureRegressionLiveStdoutChanged;
        _raptureRegression.TestProgress      += OnRaptureRegressionTestProgress;

        // ── Rapture smoke import (paramétrable par JSON fixture) ──────────
        // Scan le dossier Desktop\JsonRapture\ + les fixtures du projet xUnit Rapture
        // pour proposer un dropdown de JSONs à importer via --legacy-rapture-import.
        RaptureJsonFixtures = new ObservableCollection<string>();
        RefreshRaptureJsonFixtures();

        // ── Rapture scenarios (catalogue embarqué, manifest.json) ─────────
        // Dossier par défaut : ./RaptureScenarios à côté du .exe (copié à chaque
        // build du csproj). L'utilisateur peut override le path dans l'UI pour
        // pointer ailleurs (ex. dossier custom contenant ses propres scénarios).
        RaptureScenarios = new ObservableCollection<RaptureScenario>();
        RaptureScenariosFolder = RaptureScenarioCatalog.DefaultScenariosDir();
        ReloadRaptureScenarios();
        _raptureSmokeProxy.LinesChanged += OnRaptureSmokeLinesChanged;
        _raptureSmokeProxy.StateChanged += OnRaptureSmokeStateChanged;

        // ── Rapture smoke export (tab Smoke Export, single button Start E2E) ────
        // Proxy séparé du proxy Import : permet aux 2 tabs d'avoir leur propre
        // état (logs, summary, IsRunning). En pratique RIG legacy est mono-instance
        // donc l'utilisateur ne lance qu'un seul des 2 à la fois, mais les consoles
        // restent isolées pour la lisibilité.
        _raptureExportProxy.LinesChanged += OnRaptureExportLinesChanged;
        _raptureExportProxy.StateChanged += OnRaptureExportStateChanged;
        RefreshRaptureExportFileCount(); // initial badge count
        _raptureEdiImportProxy.LinesChanged += OnRaptureEdiImportLinesChanged;
        _raptureEdiImportProxy.StateChanged += OnRaptureEdiImportStateChanged;

        // ── Scénarios (snapshots .verified.json) ─────────────────────────
        ScenarioFiles = new ObservableCollection<ScenarioFileViewModel>();
        ScenarioFilesView = (ListCollectionView)CollectionViewSource.GetDefaultView(ScenarioFiles);
        ScenarioFilesView.Filter = item =>
        {
            var vm = (ScenarioFileViewModel)item;
            return AnyActiveTagMatches(ScenarioFilterTags, vm.Tags);
        };
        ScenarioFilterTags = new ObservableCollection<TagChipViewModel>();
        ReloadScenarioFiles();

        // ── Modules (extension future) ────────────────────────────────────
        // Global = tests cross-fonctionnels (boot RIG legacy, login DB 9995, …)
        //          en première position car c'est l'entrée la plus mature et
        //          la moins spécifique à un module métier.
        // KBIS / MANDATAIRE / IPE = modules business du WPF migration. Chacun
        //          héberge des filter-chips Legacy/WPF pour switcher de stack.
        Modules = new ObservableCollection<ModuleNavItem>
        {
            new("Global",      "🌐", isActive: true,  isAvailable: true),
            new("KBIS",        "📄", isActive: false, isAvailable: true),
            new("RAPTURE",     "⚖", isActive: false, isAvailable: true),
            new("Alertes",     "🔔", isActive: false, isAvailable: true),
            new("DCADEMAT",    "📋", isActive: false, isAvailable: true),
            new("MANDATAIRE",  "👤", isActive: false, isAvailable: false),
            new("IPE",         "🏛", isActive: false, isAvailable: false),
        };
        // Pre-selection du module via env var RIG_TV_MODULE (Global|KBIS|RAPTURE|MANDATAIRE|IPE).
        // Permet de bypass le clic UIA sur le rail (WPF ToggleButton -> UIA Toggle ne fire pas Click).
        var envModule = Environment.GetEnvironmentVariable("RIG_TV_MODULE");
        ModuleNavItem? initial = null;
        if (!string.IsNullOrEmpty(envModule))
        {
            initial = Modules.FirstOrDefault(m =>
                m.IsAvailable && string.Equals(m.Name, envModule, StringComparison.OrdinalIgnoreCase));
            if (initial is null)
                Log.Info($"RIG_TV_MODULE='{envModule}' : module inconnu ou indisponible, fallback Global");
        }
        initial ??= Modules[0];
        foreach (var m in Modules) m.IsActive = (m == initial);
        SelectedModule = initial;

        // Initialize mode + parallelism depuis env vars (backwards compat avec relance-tv-mode-*.ps1)
        var envHeadless = Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS");
        if (envHeadless == "0") SelectedRaptureMode = RaptureMode.A;
        // Pour B / C / D : ambigu, on garde le défaut B.

        var envParall = Environment.GetEnvironmentVariable("RIG_SMOKE_PARALLELISM");
        if (int.TryParse(envParall, out var p) && p >= 1 && p <= 16) RaptureParallelism = p;

        // Vue triée des scénarios (Queued à la fin). Lazy-init via ListCollectionView pour
        // que LiveSorting fonctionne. Doit être créée APRÈS BatchState (auto-property already
        // initialisée à la déclaration → non-null ici).
        var scenariosView = new System.Windows.Data.ListCollectionView(BatchState.Scenarios);
        scenariosView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(ScenarioRunState.SortKey), System.ComponentModel.ListSortDirection.Ascending));
        scenariosView.IsLiveSorting = true;
        scenariosView.LiveSortingProperties.Add(nameof(ScenarioRunState.SortKey));
        ScenariosView = scenariosView;

        UpdateSummary();

        // Self-snap retention : on garde les N runs les plus recents, on supprime le reste.
        // Tourne en Task.Run pour ne JAMAIS bloquer le startup UI (cleanup best-effort).
        // Le run en cours (RIG_RUN_STAMP) est protege : meme s'il est plus vieux que les
        // N derniers (cas impossible en pratique, defensif), on ne le purge pas.
        _ = System.Threading.Tasks.Task.Run(() => CleanupOldSelfSnaps(keepLast: 10));

        // Phase 4 : initial compute du footer compteur disk usage (scan de
        // %LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\). Task.Run pour ne pas bloquer
        // le startup UI le temps du scan disque. Sans ça, le footer reste sur le
        // placeholder "(calcul...)" jusqu'au premier cleanup ou première fin de batch.
        _ = System.Threading.Tasks.Task.Run(() => RefreshSnapDiskUsage());

        // Snap timer SINGLETON démarré au ctor (2026-05-27, bug "tile + Focus PNG
        // noir pendant Play"). Avant : timer créé/détruit dans RunAllRaptureScenariosAsync
        // → avec PlayScenario AllowConcurrentExecutions=true, deux batches concurrents
        // se piétinaient (le 1er finissait + appelait `_snapTimer = null` → tuait le timer
        // du 2ème encore actif). Maintenant timer permanent : tick = no-op si BatchState
        // n'a rien d'actif (foreach skip Queued/Done/Fail). Cost: ~10µs/tick à vide.
        InitializeSnapTimer();
    }

    /// <summary>Singleton snap timer démarré au ctor + jamais arrêté pendant la vie TV.
    /// Tick = scan disque tous les <see cref="SnapRefreshIntervalMs"/> ms pour update
    /// LastSnapPath de chaque scénario actif (Verdict=Pending, Phase != Queued). Si
    /// RIG_RUN_STAMP vide ou snapsRoot inexistant → return early. SnapRenderingPaused
    /// (toggle Pause UI) skip le body sans Stop() le timer (état figé visuellement).</summary>
    private void InitializeSnapTimer()
    {
        _snapTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SnapRefreshIntervalMs) };
        _snapTimer.Tick += (_, __) =>
        {
            if (SnapRenderingPaused) return;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Re-read env var à chaque tick : si l'utilisateur relance TV avec un nouveau
            // RIG_RUN_STAMP (ou l'env change pendant la session), on picke up sans restart.
            var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            if (string.IsNullOrEmpty(runStamp)) return;
            var snapsRoot = Path.Combine(local, "rig-wpf-testviewer", "self-snaps", runStamp);
            if (!Directory.Exists(snapsRoot)) return;
            foreach (var st in BatchState.Scenarios)
            {
                if (st.Verdict != Verdict.Pending) continue;
                if (st.Phase == ScenarioPhase.Queued) continue;
                var dir = Path.Combine(snapsRoot, st.Id);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var latest = new DirectoryInfo(dir).GetFiles("snap-*.png")
                        .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (latest != null && st.LastSnapPath != latest.FullName)
                        st.LastSnapPath = latest.FullName;
                }
                catch { /* best-effort — worker peut ecrire en concurrence */ }
            }

            // KBIS legacy : 2 scenarios indépendants → chaque scenario a SON sous-dossier
            // self-snap (kbis-vk/ ou kbis-xex/). On notifie SYSTÉMATIQUEMENT tous les
            // adapters à chaque tick — leur getter LastSnapPath scan le bon dossier
            // selon le Title du scenario (PROC_VK → kbis-vk, PROC_XEX → kbis-xex).
            // Note: pas de check Directory.Exists ici car chaque adapter le fait dans son
            // getter, et pendant le 1er run le dossier n'existe pas encore au début.
            foreach (var item in KbisCatalog.ScenariosView.SourceCollection)
            {
                if (item is ViewModels.Smoke.Kbis.KbisLegacyScenarioAdapter a)
                    a.NotifySnapChanged();
            }

            // Idem module ALERTES — guard sur _alertesCatalog pour ne pas forcer sa création au boot.
            _alertesCatalog?.NotifyAllSnapChanged();
        };
        _snapTimer.Start();
    }

    /// <summary>
    /// Phase 4 : recalcule l'usage disque cumulé des self-snaps (somme bytes + count runs)
    /// et pousse le résultat formaté dans <see cref="SnapDiskUsageHuman"/> via le Dispatcher
    /// UI. Appelé : (1) en fin de constructeur (Task.Run), (2) après CleanupAllSnaps,
    /// (3) en fin de batch (BatchPhase.Done).
    /// </summary>
    private void RefreshSnapDiskUsage()
    {
        var (bytes, count) = Services.SnapDiskUsage.ComputeTotal();
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            SnapDiskUsageHuman = Services.SnapDiskUsage.FormatHuman(bytes, count);
        });
    }

    /// <summary>
    /// Purge les vieux runs de self-snaps : garde les <paramref name="keepLast"/> les plus
    /// recents par LastWriteTime, supprime le reste. Sans-effet si le dir n'existe pas.
    /// </summary>
    /// <remarks>
    /// Sous %LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\<runStamp>\ . Chaque run accumule
    /// rapidement 50-400 MB (4 workers × 500ms snap). Sans cleanup, 1 GB par matinee.
    /// Le run en cours (env var RIG_RUN_STAMP, sinon argv[0]) est explicitement protege.
    /// </remarks>
    private static void CleanupOldSelfSnaps(int keepLast)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = System.IO.Path.Combine(local, "rig-wpf-testviewer", "self-snaps");
            if (!System.IO.Directory.Exists(root)) return;

            var activeRun = Environment.GetEnvironmentVariable("RIG_RUN_STAMP") ?? string.Empty;

            var allRuns = new System.IO.DirectoryInfo(root)
                .EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTime)
                .ToList();

            if (allRuns.Count <= keepLast)
            {
                Log.Info($"Self-snap cleanup : {allRuns.Count} runs <= keepLast={keepLast}, rien a purger");
                return;
            }

            // Toujours garder les keepLast plus recents + protege explicitement le run actif.
            var toKeep = new HashSet<string>(
                allRuns.Take(keepLast).Select(d => d.Name),
                StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(activeRun)) toKeep.Add(activeRun);

            long bytesDeleted = 0;
            int runsDeleted = 0;
            foreach (var d in allRuns.Skip(keepLast))
            {
                if (toKeep.Contains(d.Name)) continue;
                try
                {
                    bytesDeleted += d.EnumerateFiles("*", System.IO.SearchOption.AllDirectories).Sum(f => f.Length);
                    d.Delete(recursive: true);
                    runsDeleted++;
                }
                catch (Exception ex)
                {
                    Log.Info($"Self-snap cleanup : impossible de purger {d.Name} ({ex.GetType().Name}: {ex.Message})");
                }
            }
            if (runsDeleted > 0)
                Log.Info($"Self-snap cleanup : {runsDeleted} runs purges, {bytesDeleted / 1024 / 1024} MB liberes (garde={keepLast}, actif={activeRun})");
        }
        catch (Exception ex)
        {
            Log.Info($"Self-snap cleanup : erreur ignoree ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ── Module nav ───────────────────────────────────────────────────────
    public ObservableCollection<ModuleNavItem> Modules { get; }
    [ObservableProperty] private ModuleNavItem selectedModule;

    /// <summary>Bind sur le Click des pills nav — change le module actif.</summary>
    [RelayCommand]
    private void SelectModule(ModuleNavItem? target)
    {
        if (target is null || !target.IsAvailable) return;
        foreach (var m in Modules) m.IsActive = (m == target);
        SelectedModule = target;
    }

    public bool IsKbisModule    => SelectedModule?.Name == "KBIS";
    public bool IsGlobalModule  => SelectedModule?.Name == "Global";
    public bool IsRaptureModule => SelectedModule?.Name == "RAPTURE";
    public bool IsAlertesModule => SelectedModule?.Name == "Alertes";
    public bool IsDcadematModule => SelectedModule?.Name == "DCADEMAT";

    partial void OnSelectedModuleChanged(ModuleNavItem value)
    {
        OnPropertyChanged(nameof(IsKbisModule));
        OnPropertyChanged(nameof(IsGlobalModule));
        OnPropertyChanged(nameof(IsRaptureModule));
        OnPropertyChanged(nameof(IsAlertesModule));
        OnPropertyChanged(nameof(IsDcadematModule));
    }

    /// <summary>Tags qui dédient un scénario à un module métier (= masqué dans Global).</summary>
    private static readonly string[] ModuleTags = { "kbis", "mandataire", "ipe", "alertes", "dcademat" };

    /// <summary>True si l'item porte un tag de module spécifique (donc à exclure du Global).</summary>
    private static bool HasModuleTag(IReadOnlyList<string> tags)
    {
        foreach (var t in tags)
            foreach (var mt in ModuleTags)
                if (string.Equals(t, mt, StringComparison.Ordinal)) return true;
        return false;
    }

    // ── Filter chips (un set par tab) ────────────────────────────────────
    public ObservableCollection<TagChipViewModel> SmokeFilterTags { get; }
    public ObservableCollection<TagChipViewModel> RegressionFilterTags { get; }
    public ObservableCollection<TagChipViewModel> RaptureRegressionFilterTags { get; }
    public ObservableCollection<TagChipViewModel> ScenarioFilterTags { get; }

    // ── Smoke RIG (legacy) section ───────────────────────────────────────
    public ObservableCollection<ScenarioViewModel> LegacyScenarios { get; }
    public ListCollectionView LegacyView { get; }
    /// <summary>Sous-ensemble de <see cref="LegacyScenarios"/> taggés "kbis" — surface du module KBIS.</summary>
    public ObservableCollection<ScenarioViewModel> KbisLegacyScenarios { get; }
    /// <summary>Vue de <see cref="KbisLegacyScenarios"/> (module KBIS legacy, sans filtre stack global).</summary>
    public ListCollectionView KbisLegacyView { get; }
    [ObservableProperty] private string? legacyLastFullStdout;
    [ObservableProperty] private string? legacyLastStderr;
    [ObservableProperty] private int legacyDetailTabIndex;
    // Flag pour le mode parallel KBIS (RunLegacyAsync ne passe pas par _legacySmokeProxy.RunAsync
    // qui setterait IsRunning, donc on track manuellement pour disable le bouton pendant le run).
    private bool _isLegacyParallelRunning;

    // ── Play par-scénario legacy CONCURRENT (KBIS/ALERTES/DCADEMAT) ──────────────────────────────
    // Bug 2026-06-15 : cliquer ▶ sur plusieurs tuiles legacy à la suite ne lançait que la 1re — la
    // garde `if (IsXxxRunning) return` traitait tout play comme mono-run (≠ RAPTURE qui est concurrent
    // + état par-scénario). Fix : le play par-scénario gate désormais sur IsXxxBatchBusy (un batch/stress
    // tourne) et NON sur les autres plays → N plays parallèles, chacun son worker/HDESK. Throttle au même
    // cap dur que les suites (LegacyHeavySuiteMaxConcurrency) car chaque play ouvre un RigClientAccueil
    // lourd (>2 simultanés = verdicts non fiables). Le compteur alimente IsXxxRunning → la mutex Run/Stress
    // reste honorée (pas de batch pendant des plays, et inversement).
    private int _legacyPlaysInFlight;
    private readonly SemaphoreSlim _legacyPlayThrottle =
        new SemaphoreSlim(LegacyHeavySuiteMaxConcurrency, LegacyHeavySuiteMaxConcurrency);
    private bool AnyLegacyPlayInFlight => System.Threading.Volatile.Read(ref _legacyPlaysInFlight) > 0;

    /// <summary>Notifie les 3 IsXxxRunning + les commandes Run/Stress des 3 modules legacy. Appelé aux
    /// transitions du compteur de plays (compteur PARTAGÉ → un play DCADEMAT grise aussi Run KBIS/ALERTES).</summary>
    private void RaiseLegacyRunningChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(IsLegacyRunning));
            OnPropertyChanged(nameof(IsAlertesRunning));
            OnPropertyChanged(nameof(IsDcadematRunning));
            RunLegacyCommand.NotifyCanExecuteChanged();
            StressSelectedScenarioCommand.NotifyCanExecuteChanged();
            RunAlertesCommand.NotifyCanExecuteChanged();
            StressAlertesScenarioCommand.NotifyCanExecuteChanged();
            RunDcadematCommand.NotifyCanExecuteChanged();
        }
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(Raise); else Raise();
    }

    // IsLegacyRunning gate "Run smoke RIG" ET "Stress" (mutuellement exclusifs — pas 2 batchs
    // RIG en parallèle non contrôlés). _isKbisStressRunning est défini dans la région Stress.
    // IsLegacyBatchBusy = tout SAUF les plays par-scénario (= ce sur quoi un play se bloque).
    public bool IsLegacyBatchBusy => _legacySmokeProxy.IsRunning || _isLegacyParallelRunning || _isKbisStressRunning;
    public bool IsLegacyRunning => IsLegacyBatchBusy || AnyLegacyPlayInFlight;
    public bool LegacyExeExists => _legacySmokeProxy.ExeExists;

    /// <summary>Accès au proxy legacy pour permettre aux adapters KBIS de filtrer leurs
    /// SmokeResultLine par WorkerId (each scenario = 1 worker tag).</summary>
    public SmokeRunnerProxy LegacySmokeProxy => _legacySmokeProxy;
    public string LegacyExeStatus => LegacyExeExists
        ? $"SmokeRunner --legacy : {_legacySmokeProxy.ExePath}"
        : $"⚠ SmokeRunner.exe introuvable : {_legacySmokeProxy.ExePath}";
    [ObservableProperty] private string legacySummary = "—";

    // ── Smoke section ────────────────────────────────────────────────────
    public ObservableCollection<ScenarioViewModel> SmokeScenarios { get; }
    public ListCollectionView SmokeView { get; }
    public ObservableCollection<RenderArtifactViewModel> Renders { get; }
    [ObservableProperty] private bool enableUiFlow;
    [ObservableProperty] private string? smokeLastFullStdout;
    [ObservableProperty] private string? smokeLastStderr;
    [ObservableProperty] private string? pdfPreviewPath;
    /// <summary>Onglet sélectionné dans le panneau de détail Smoke (0=Renders, 1=PDF, 2=Logs).</summary>
    [ObservableProperty] private int smokeDetailTabIndex;

    public bool IsSmokeRunning => _smokeProxy.IsRunning;
    public bool SmokeExeExists => _smokeProxy.ExeExists;
    public string SmokeExeStatus => SmokeExeExists
        ? $"SmokeRunner : {_smokeProxy.ExePath}"
        : $"⚠ SmokeRunner introuvable : {_smokeProxy.ExePath}";

    // ── Regression section ───────────────────────────────────────────────
    public ObservableCollection<RegressionItemViewModel> RegressionTests { get; }
    public ListCollectionView RegressionView { get; }
    public bool IsRegressionRunning => _regression.IsRunning;
    public bool RegressionProjectExists => _regression.ProjectExists;
    public string RegressionProjectStatus => RegressionProjectExists
        ? $"xUnit : {_regression.ProjectDir}"
        : $"⚠ Projet xUnit introuvable : {_regression.ProjectDir}";
    [ObservableProperty] private string? regressionLastStdout;
    [ObservableProperty] private string? regressionLastStderr;

    // ── Regression Rapture section (parallèle à KBIS) ────────────────────
    public ObservableCollection<RegressionItemViewModel> RaptureRegressionTests { get; }
    public ListCollectionView RaptureRegressionView { get; }
    public bool IsRaptureRegressionRunning => _raptureRegression.IsRunning;
    public bool RaptureRegressionProjectExists => _raptureRegression.ProjectExists;
    public string RaptureRegressionProjectStatus => RaptureRegressionProjectExists
        ? $"xUnit Rapture : {_raptureRegression.ProjectDir}"
        : $"⚠ Projet xUnit Rapture introuvable : {_raptureRegression.ProjectDir}";
    [ObservableProperty] private string? raptureRegressionLastStdout;
    [ObservableProperty] private string? raptureRegressionLastStderr;
    [ObservableProperty] private string raptureRegressionSummary = "—";

    // ── Rapture Smoke Import section (UI live, paramétré par JSON) ───────
    /// <summary>Liste de paths absolus vers les JSONs Rapture à importer (Desktop + repo).</summary>
    public ObservableCollection<string> RaptureJsonFixtures { get; }
    /// <summary>Le JSON sélectionné par l'utilisateur dans le ComboBox.</summary>
    [ObservableProperty] private string? selectedRaptureJsonFixture;
    /// <summary>
    /// Si vrai, le smoke ajoute <c>--apply</c> aux args : clic 'Importer (N champs cochés)'
    /// au lieu d'Annuler → ApplyService écrit réellement en base. Le SmokeRunner snapshot
    /// les colonnes APPEL_AFFAIRE et les IDs NOTE_PROCEDURE RAPTURE_* AVANT le clic, puis
    /// restaure tout APRÈS pour rester net-zero sur RIG_DEV partagé. Requiert un AUDNC_ID
    /// renseigné dans l'override audience (sinon snapshot impossible).
    /// </summary>
    [ObservableProperty] private bool raptureSmokeApplyReal;
    /// <summary>
    /// Si vrai, le bouton "▶ Start E2E" lance --legacy-rapture-process (UI live :
    /// RigClientAccueil.exe visible, login RIG, PROC_RETAUD, navigation vers
    /// l'audience ciblée, clic Import Rapture, recap visible, screenshot, Annuler).
    /// Si faux (default), lance --rapture-selfdrive (worker invisible in-process,
    /// pipeline complet + assertions DB/UIA, plus rapide). Ignoré en mode "All
    /// scenarios" qui reste en self-drive batch (sinon 16 RIG visibles séquentiels).
    /// </summary>
    // Default = true (legacy dispatch) car aligned avec selectedRaptureMode default = B (legacy + HDESK).
    // Si on garde le default false ici alors que selectedRaptureMode = B, le partial method
    // OnSelectedRaptureModeChanged ne fire PAS au boot (pas de change) → batch part en selfdrive
    // alors qu'on est censé être en legacy. Confirmed bug 2026-05-27 lors du smoke Mode B.
    [ObservableProperty] private bool raptureSmokeVisibleMode = true;

    // ── Mode selector (P2) — combo box CmbRaptureMode ──────────────────
    /// <summary>Source ItemsSource du combo <c>CmbRaptureMode</c> (B = défaut en première position).</summary>
    public IReadOnlyList<RaptureModeOption> RaptureModeOptions { get; } = RaptureModeOption.All;

    /// <summary>Mode visibilité du batch sélectionné (default B = HDESK self-snap, 0 vol focus).</summary>
    [ObservableProperty] private RaptureMode selectedRaptureMode = RaptureMode.B;

    /// <summary>Nombre de workers RIG en parallèle (1-16, default 4). Synchronisé avec <c>RIG_SMOKE_PARALLELISM</c>.</summary>
    [ObservableProperty] private int raptureParallelism = 4;

    // ── Mode selector KBIS — combo box CmbKbisMode (miroir de CmbRaptureMode) ──────────
    /// <summary>Source ItemsSource du combo <c>CmbKbisMode</c> (mêmes options A/B/C/D que Rapture).</summary>
    public IReadOnlyList<RaptureModeOption> KbisModeOptions { get; } = RaptureModeOption.All;

    /// <summary>Mode visibilité des tuiles K-bis (default B = HDESK self-snap, 0 vol focus).
    /// A/C = HEADLESS=0 (RIG sur le desktop courant, PREND la souris) — utile pour vérifier le K-bis
    /// à l'œil : sur Desktop 1 (composé par le DWM) la page AcroPDF se rend ET le self-snap la capture.
    /// B/D = HEADLESS=1 (HDESK isolé, 0 vol focus, mais page PDF non capturable, cf. composition DWM).</summary>
    [ObservableProperty] private RaptureMode selectedKbisMode = RaptureMode.B;

    // Headless effectif des workers K-bis, dérivé de selectedKbisMode. Champ DÉDIÉ (pas l'env var
    // globale RIG_DRIVER_HEADLESS qui appartient au combo Rapture) → les 2 combos ne se clobberent pas.
    // Default true = aligné sur selectedKbisMode=B (le partial OnChanged ne fire pas au boot).
    private bool _kbisDriverHeadless = true;

    partial void OnSelectedKbisModeChanged(RaptureMode value)
    {
        // A/C → desktop courant (HEADLESS=0, prend la souris) ; B/D → HDESK isolé (HEADLESS=1).
        _kbisDriverHeadless = (value == RaptureMode.B || value == RaptureMode.D);
        Log.Info($"KbisMode changed → {value} (workers RIG_DRIVER_HEADLESS={(_kbisDriverHeadless ? "1" : "0")})");
    }

    partial void OnSelectedRaptureModeChanged(RaptureMode value)
    {
        // Mapping correct des 4 modes vers les 2 dimensions du dispatch :
        //   dimension 1 (RaptureSmokeVisibleMode) : true = --legacy-rapture-process (UI FlaUI), false = --rapture-selfdrive (in-proc)
        //   dimension 2 (RIG_DRIVER_HEADLESS env var) : "1" = HDESK isolé, "0" = Default desktop
        // Mode A = legacy + Default     → visible=true,  HEADLESS=0
        // Mode B = legacy + HDESK isolé → visible=true,  HEADLESS=1 (DÉFAUT)
        // Mode C = legacy + Default     → visible=true,  HEADLESS=0 (user a switché manuellement vers Desktop 2 Sysinternals)
        // Mode D = selfdrive headless   → visible=false, HEADLESS=1
        // ⚠️ Mode B est legacy, PAS selfdrive (D). Confondre les 2 fait que le batch passe par
        // --rapture-selfdrive in-proc au lieu de spawn N RIG visibles en HDESK isolé.
        bool isLegacy = (value != RaptureMode.D);
        bool isHeadless = (value == RaptureMode.B || value == RaptureMode.D);
        RaptureSmokeVisibleMode = isLegacy;
        Environment.SetEnvironmentVariable("RIG_DRIVER_HEADLESS", isHeadless ? "1" : "0");
        Log.Info($"RaptureMode changed → {value} (RIG_DRIVER_HEADLESS={Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS")})");
    }

    partial void OnRaptureParallelismChanged(int value)
    {
        if (value < 1) { RaptureParallelism = 1; return; }
        if (value > 16) { RaptureParallelism = 16; return; }
        Environment.SetEnvironmentVariable("RIG_SMOKE_PARALLELISM", value.ToString());
    }

    /// <summary>Collection bound to ListBox dans <c>Views/RunHistoryDrawer.xaml</c>. Refilled à chaque ouverture.</summary>
    public ObservableCollection<Services.RunHistoryEntry> RunHistoryEntries { get; } = new();

    /// <summary>Visibilité du drawer Historique (Border overlay dans MainWindow.xaml).</summary>
    [ObservableProperty] private bool runHistoryDrawerOpen;

    /// <summary>
    /// Phase 5 — toggle drawer Historique. Au open, rescan <c>%LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\</c>
    /// via <see cref="Services.RunHistoryScanner"/> et repopule <see cref="RunHistoryEntries"/>.
    /// </summary>
    [RelayCommand]
    private void ToggleRunHistoryDrawer()
    {
        if (!RunHistoryDrawerOpen)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = Path.Combine(local, "rig-wpf-testviewer", "self-snaps");
            RunHistoryEntries.Clear();
            foreach (var e in Services.RunHistoryScanner.Scan(root)) RunHistoryEntries.Add(e);
        }
        RunHistoryDrawerOpen = !RunHistoryDrawerOpen;
    }

    /// <summary>Ouvre l'Explorer sur le dossier d'un run historique (bouton 📁 Snaps du drawer).
    /// Si le dir n'existe plus (retention auto-cleanup au boot OU "Tout effacer" appelé entretemps),
    /// supprime l'entry stale de la liste + message utilisateur. Empêche le dialog Explorer
    /// "Emplacement non disponible" qui apparaît si on launch Explorer sur path inexistant.</summary>
    [RelayCommand]
    private void OpenRunHistorySnaps(Services.RunHistoryEntry? entry)
    {
        if (entry == null) return;
        if (!Directory.Exists(entry.DirPath))
        {
            // Stale entry — dir nettoyé depuis le dernier scan. Purge + warn user.
            RunHistoryEntries.Remove(entry);
            MessageBox.Show(
                $"Ce run a été nettoyé depuis (retention policy ou cleanup manuel) :\n{entry.RunStamp}\n\nLa liste est mise à jour.",
                "Run obsolète", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start("explorer.exe", entry.DirPath); }
        catch (Exception ex) { Log.Warn("OpenRunHistorySnaps failed: " + ex.Message); }
    }

    /// <summary>Refresh la liste RunHistoryEntries depuis disque (rescan self-snaps root).
    /// À appeler après toute action qui modifie les runs sur disque : CleanupAllSnaps,
    /// CleanupOldSelfSnaps, fin de batch (qui pourrait avoir purgé via retention).</summary>
    private void RefreshRunHistoryEntries()
    {
        if (!RunHistoryDrawerOpen) return; // pas la peine si drawer fermé, sera scan à la prochaine ouverture
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = Path.Combine(local, "rig-wpf-testviewer", "self-snaps");
            RunHistoryEntries.Clear();
            foreach (var e in Services.RunHistoryScanner.Scan(root)) RunHistoryEntries.Add(e);
        });
    }

    /// <summary>
    /// Re-déclenche un batch avec les mêmes settings qu'un run historique (bouton ▶ Rejouer).
    /// V1 = MessageBox stub. V2 = parse <c>testviewer-*.log</c> pour reconstituer settings.
    /// </summary>
    [RelayCommand]
    private void ReplayRunHistory(Services.RunHistoryEntry? entry)
    {
        if (entry == null) return;
        MessageBox.Show($"Replay {entry.RunStamp} : à implémenter en v2 (parse settings depuis testviewer-*.log).", "Replay");
    }

    // ── Phase 3b : state batch live (mosaïque + focus + liste F1) ──────────
    /// <summary>State centralisé du batch en cours (mosaïque, focus, liste, counters).</summary>
    public BatchRunState BatchState { get; } = new BatchRunState();

    /// <summary>
    /// Vue triée sur BatchState.Scenarios : Queued à la fin (SortKey=99), tous les autres devant (SortKey=0).
    /// Bound par MosaicView ListBox + ScenarioListView DataGrid (au lieu de BatchState.Scenarios direct)
    /// pour que les scénarios qui démarrent migrent automatiquement vers le haut.
    /// LiveSorting sur "SortKey" : ré-évalue à chaque PropertyChanged de SortKey (déclenché par
    /// [NotifyPropertyChangedFor(nameof(SortKey))] sur Phase dans ScenarioRunState).
    /// </summary>
    public System.ComponentModel.ICollectionView ScenariosView { get; }

    /// <summary>Footer disk usage — placeholder Phase 3b, vraie compute Phase 4.</summary>
    [ObservableProperty] private string snapDiskUsageHuman = "(calcul...)";

    /// <summary>Plein écran focus : si true, MainWindow.xaml cache mosaïque + liste + footer.</summary>
    [ObservableProperty] private bool focusIsFullscreen;

    // ── Live observation toolbar (repatriée depuis LiveViewerWindow détachée) ──
    /// <summary>Intervalle (ms) du DispatcherTimer qui scanne les self-snaps disque.
    /// Bound au slider de la barre live observation (100-2000 ms). Default 250 ms.</summary>
    [ObservableProperty] private int snapRefreshIntervalMs = 250;

    /// <summary>Toggle Pause/Reprendre sur la barre live observation.
    /// Quand true, le Tick du snapTimer skip son body — les RaptureScenarios.LastSnapPath
    /// arrêtent de se mettre à jour, donc les Image bindings figent visuellement.</summary>
    [ObservableProperty] private bool snapRenderingPaused;

    /// <summary>Run stamp courant (env var RIG_RUN_STAMP héritée du parent process).
    /// Affiché en monospace dans la barre live observation. RefreshRunStamp() pour re-lire.</summary>
    [ObservableProperty] private string currentRunStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP") ?? "";

    /// <summary>Clamp [100,2000] + propage l'interval au _snapTimer s'il est instancié.</summary>
    partial void OnSnapRefreshIntervalMsChanged(int value)
    {
        if (value < 100) { SnapRefreshIntervalMs = 100; return; }
        if (value > 2000) { SnapRefreshIntervalMs = 2000; return; }
        if (_snapTimer != null) _snapTimer.Interval = TimeSpan.FromMilliseconds(value);
    }

    /// <summary>Re-lit RIG_RUN_STAMP — à appeler au démarrage du batch si la var
    /// est réécrite après lancement de TV (cas rare mais possible).</summary>
    public void RefreshRunStamp()
    {
        CurrentRunStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP") ?? "";
    }

    /// <summary>Ouvre l'explorateur sur la racine des self-snaps (tous runs confondus).</summary>
    [RelayCommand]
    private void OpenSnapsRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = System.IO.Path.Combine(local, "rig-wpf-testviewer", "self-snaps");
        if (System.IO.Directory.Exists(root)) OpenExplorerSafe(root);
        else Log.Warn($"OpenSnapsRoot: {root} introuvable (rien à ouvrir)");
    }

    /// <summary>Ouvre Explorer sur un dossier en quotant le path (defensive contre
    /// Process.Start qui mis-parse les arguments avec espaces/Unicode). Si le dossier
    /// est introuvable au moment du Start (race avec retention cleanup), retombe sur
    /// le parent existant le plus proche au lieu d'afficher "Emplacement non disponible".</summary>
    private static void OpenExplorerSafe(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        // Walk up jusqu'à trouver un dossier qui existe vraiment (anti-race retention cleanup)
        var target = dir;
        while (!System.IO.Directory.Exists(target))
        {
            var parent = System.IO.Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(parent) || parent == target) { target = null; break; }
            target = parent;
        }
        if (target == null) { Log.Warn($"OpenExplorerSafe: ni '{dir}' ni aucun parent n'existe"); return; }
        if (target != dir) Log.Info($"OpenExplorerSafe: '{dir}' absent → fallback parent '{target}'");
        try
        {
            // Arguments quotées : Explorer parse mal les paths non quotés avec espaces/accents
            // ("Emplacement non disponible" sur paths Unicode/longueur > 260 / race deletion).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + target + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Warn($"OpenExplorerSafe '{target}' failed: {ex.Message}"); }
    }

    /// <summary>Champ partagé entre la commande batch (qui le crée/start/stop) et
    /// OnSnapRefreshIntervalMsChanged (qui ajuste son Interval à chaud).</summary>
    private System.Windows.Threading.DispatcherTimer? _snapTimer;

    /// <summary>Click sur une tile mosaïque → bascule le focus.</summary>
    [RelayCommand]
    private void SelectScenario(ScenarioRunState? scenario)
    {
        if (scenario != null) BatchState.SelectedScenario = scenario;
    }

    /// <summary>Toggle ⛶ Plein écran sur la zone Focus.</summary>
    [RelayCommand]
    private void ToggleFocusFullscreen() => FocusIsFullscreen = !FocusIsFullscreen;

    /// <summary>Ouvre l'explorateur sur le dossier de self-snaps du scénario focus.
    /// Si le dossier exact n'existe pas (race avec retention cleanup, ou scénario jamais
    /// run dans le run actif), OpenExplorerSafe retombe sur le parent existant le plus
    /// proche au lieu d'afficher "Emplacement non disponible".</summary>
    [RelayCommand]
    private void OpenSnapsFolder()
    {
        var s = BatchState.SelectedScenario;
        if (s == null) return;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP") ?? "";
        var dir = Path.Combine(local, "rig-wpf-testviewer", "self-snaps", runStamp, s.Id);
        OpenExplorerSafe(dir);
    }

    /// <summary>Copie les logs streaming du scénario focus dans le clipboard.</summary>
    [RelayCommand]
    private void CopyFocusLogs()
    {
        var s = BatchState.SelectedScenario;
        if (s == null) return;
        try { System.Windows.Clipboard.SetText(s.LogsTail ?? ""); }
        catch (Exception ex) { Log.Warn("CopyFocusLogs failed: " + ex.Message); }
    }

    /// <summary>Switch sur le HDESK isolé du worker (Ctrl+Alt+Backspace pour revenir).</summary>
    /// <summary>Play un scénario unique depuis sa card mosaïque. Route via le batch path
    /// avec filtre 1 scénario (et non l'ancien single-path RunRaptureProcessE2E qui ne syncait
    /// pas BatchState). Garantit MarkStarted/Logs/MarkFinished → CanStop devient true → ⏹ apparaît
    /// → click Stop fonctionne avec le vrai WorkerPid.
    /// AllowConcurrentExecutions=true : sans ça, [RelayCommand] async désactive le command
    /// pendant l'exec → user peut pas Play plusieurs scénarios en parallèle (chaque tile partage
    /// le même Command). Avec true, N Play = N workers parallèles via le batch semaphore interne.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayScenarioAsync(ScenarioRunState? state)
    {
        if (state == null) return;
        var cat = RaptureScenarios.FirstOrDefault(s => s.Id == state.Id);
        if (cat == null) { Log.Warn($"PlayScenario: scenarioId '{state.Id}' introuvable dans catalog"); return; }
        state.Reset(); // re-run = clear logs/state
        // Set focus EXPLICITEMENT sur le scénario joué AVANT le batch (sinon le batch
        // path mettait SelectedScenario = FirstOrDefault de la collection = 1er alphabétique
        // de TOUS les 16, pas le bon).
        Application.Current?.Dispatcher.Invoke(() => BatchState.SelectedScenario = state);
        await RunAllRaptureScenariosAsync(filterScenarioId: state.Id);
    }

    /// <summary>Stop un worker actif depuis la card mosaïque (taskkill /T /F /PID).
    /// /T propage aux process enfants (le RIG legacy spawné par le worker).</summary>
    [RelayCommand]
    private void StopScenario(ScenarioRunState? state)
    {
        if (state?.WorkerPid is not int pid) return;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(3000);
            Application.Current?.Dispatcher.Invoke(() => state.MarkFinished(Verdict.Fail));
            Log.Info($"StopScenario: killed pid {pid} (scenario {state.Id}) + children via taskkill /T /F");
        }
        catch (Exception ex) { Log.Warn($"StopScenario pid={pid} failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void OpenHdeskFromFocus()
    {
        var s = BatchState.SelectedScenario;
        if (s?.DesktopName == null) return;
        Views.HDeskEscapeHelper.SwitchToHDesk(s.DesktopName);
    }

    /// <summary>Bouton ✕ du toolbar 🔴 LIVE. Cas d'usage : un scénario a FAIL avec
    /// "Application failed to exit" → RIG legacy reste hung dans son HDESK isolé qui
    /// n'est jamais libéré. Sans ce bouton il faut taskkill /F manuellement.
    ///
    /// Flow :
    ///   1. SwitchToDefault (au cas où le user est encore sur le HDESK via "↗ Ouvrir HDESK")
    ///      → désactive l'agent Ctrl+Alt+Backspace + revient sur Default
    ///   2. taskkill /T /F /PID workerPid → kill worker + RIG legacy enfant
    ///   3. Worker process exit → refcount HDESK → 0 → Windows GC le desktop
    /// </summary>
    [RelayCommand]
    private void CloseHdeskFromFocus()
    {
        var s = BatchState.SelectedScenario;
        if (s == null) return;
        // Step 1 : si le user est actuellement sur le HDESK isolé, le ramener avant de kill.
        // Si pas sur le HDESK, ce no-op silencieux est OK.
        try { Views.HDeskEscapeHelper.SwitchToDefault(); } catch (Exception ex) { Log.Warn($"CloseHdeskFromFocus: SwitchToDefault err: {ex.Message}"); }
        // Step 2 : kill worker (et ses enfants RIG legacy via /T) si encore actif.
        if (s.WorkerPid is int pid)
        {
            try
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                killer?.WaitForExit(3000);
                Application.Current?.Dispatcher.Invoke(() => s.MarkFinished(Verdict.Fail));
                Log.Info($"CloseHdeskFromFocus: killed pid {pid} (scenario {s.Id}) + children + HDESK '{s.DesktopName}' libéré");
            }
            catch (Exception ex) { Log.Warn($"CloseHdeskFromFocus pid={pid} failed: {ex.Message}"); }
        }
        else
        {
            Log.Info($"CloseHdeskFromFocus: scenario {s.Id} pas de WorkerPid (déjà terminé). SwitchToDefault appliqué.");
        }
    }

    /// <summary>Footer "🗑 Tout effacer" — wipe tous les self-snaps sauf le run actif (RIG_RUN_STAMP).</summary>
    [RelayCommand]
    private void CleanupAllSnaps()
    {
        var confirm = System.Windows.MessageBox.Show(
            "Supprimer tous les self-snaps de tous les runs ? Action irreversible.",
            "Tout effacer",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "rig-wpf-testviewer", "self-snaps");
        if (!Directory.Exists(root)) return;
        var activeRun = Environment.GetEnvironmentVariable("RIG_RUN_STAMP") ?? "";
        foreach (var d in new DirectoryInfo(root).GetDirectories())
        {
            if (d.Name == activeRun) continue;
            try { d.Delete(recursive: true); }
            catch (Exception ex) { Log.Warn($"CleanupAllSnaps: delete {d.Name} failed: {ex.Message}"); }
        }
        // Phase 4 : footer compteur reflète le nouveau total (généralement 0 + 1 run actif).
        RefreshSnapDiskUsage();
        // Phase 5 : invalide la liste du drawer Historique si ouvert (toutes les entries sauf actif sont stale).
        RefreshRunHistoryEntries();
    }

    /// <summary>Stdout live du smoke import — re-affiché à chaque LinesChanged.</summary>
    [ObservableProperty] private string? raptureSmokeLastStdout;
    [ObservableProperty] private string? raptureSmokeLastStderr;
    [ObservableProperty] private string raptureSmokeSummary = "—";
    public bool IsRaptureSmokeRunning => _raptureSmokeProxy.IsRunning;
    public bool RaptureSmokeExeExists => _raptureSmokeProxy.ExeExists;
    public string RaptureSmokeExeStatus => RaptureSmokeExeExists
        ? $"SmokeRunner --rapture-selfdrive : {_raptureSmokeProxy.ExePath}"
        : $"⚠ SmokeRunner.exe introuvable : {_raptureSmokeProxy.ExePath}";

    // ── SmokePage template — adapters Rapture (Phase 3+) ───────────────
    // Vue Focus de la zone PNG centrale. DataContext de Views\FocusView.xaml.
    // Construit lazy au 1er accès car dépend de this (BatchState + nos props).
    private Smoke.Rapture.RaptureFocusContext? _raptureFocus;
    public Smoke.Rapture.RaptureFocusContext RaptureFocus
        => _raptureFocus ??= new Smoke.Rapture.RaptureFocusContext(this);

    // Catalogue runtime des scénarios. DataContext de MosaicView + ScenarioListView (Phase 4-5).
    private Smoke.Rapture.RaptureScenarioCatalog? _raptureCatalog;
    public Smoke.Rapture.RaptureScenarioCatalog RaptureCatalog
        => _raptureCatalog ??= new Smoke.Rapture.RaptureScenarioCatalog(this);

    // ── KBIS Smoke E2E template (2026-05-28) — peuplé depuis KbisLegacyScenarios ──
    // Le KbisScenarioCatalog wrappe les ScenarioViewModel "kbis"-taggés du Smoke RIG legacy
    // via KbisLegacyScenarioAdapter (mapping → IScenarioItem). Permet d'afficher les mêmes
    // scénarios via le template SmokePage (3-col) sans dupliquer la donnée.
    // PlayScenarioCommand forward sur RunLegacyCommand (lance la suite entière, sémantique
    // la plus proche : smoke legacy n'est pas par-scénario).
    private Smoke.Kbis.KbisScenarioCatalog? _kbisCatalog;
    public Smoke.Kbis.KbisScenarioCatalog KbisCatalog
        => _kbisCatalog ??= new Smoke.Kbis.KbisScenarioCatalog(this);

    /// <summary>
    /// Plan SUITE : 1 instance de chaque scénario KBIS distinct (1 VK + 1 XEX = 2 tuiles).
    /// Utilisé par "Run smoke RIG" (RunLegacyAsync). Chaque instance a un instanceId unique
    /// (kbis-vk-1, kbis-xex-1) servant de workerId (tag lignes) ET de sous-dossier self-snap.
    /// Public car appelé aussi par le catalog (construction initiale des tuiles).
    /// </summary>
    public IReadOnlyList<Smoke.Kbis.KbisInstanceSpec> BuildKbisSuitePlan()
    {
        var plan = new List<Smoke.Kbis.KbisInstanceSpec>(2);
        var vkBase  = KbisLegacyScenarios.FirstOrDefault(s => (s.Title ?? "").Contains("PROC_VK"));
        var xexBase = KbisLegacyScenarios.FirstOrDefault(s => (s.Title ?? "").Contains("PROC_XEX"));
        if (vkBase != null)
            plan.Add(new Smoke.Kbis.KbisInstanceSpec(vkBase, "--legacy-kbis-vk", "kbis-vk-1", 1, isVk: true, showSuffix: false));
        if (xexBase != null)
            plan.Add(new Smoke.Kbis.KbisInstanceSpec(xexBase, "--legacy-kbis-xex", "kbis-xex-1", 1, isVk: false, showSuffix: false));
        return plan;
    }

    /// <summary>
    /// Plan STRESS : X copies du MÊME scénario (toutes VK ou toutes XEX) à lancer en parallèle.
    /// instanceIds kbis-&lt;type&gt;-1..X → X tuiles indépendantes (snap/worker/verdict par copie).
    /// Utilisé par StressSelectedScenarioAsync pour chasser les flakes sous contention.
    /// </summary>
    private IReadOnlyList<Smoke.Kbis.KbisInstanceSpec> BuildKbisStressPlan(bool isVk, int x)
    {
        var baseVm = KbisLegacyScenarios.FirstOrDefault(s => (s.Title ?? "").Contains(isVk ? "PROC_VK" : "PROC_XEX"));
        var plan = new List<Smoke.Kbis.KbisInstanceSpec>(x);
        if (baseVm == null) return plan;
        string arg  = isVk ? "--legacy-kbis-vk" : "--legacy-kbis-xex";
        string type = isVk ? "vk" : "xex";
        for (int i = 1; i <= x; i++)
            plan.Add(new Smoke.Kbis.KbisInstanceSpec(baseVm, arg, $"kbis-{type}-{i}", i, isVk: isVk, showSuffix: x > 1));
        return plan;
    }

    private Smoke.Kbis.KbisFocusContext? _kbisFocus;
    public Smoke.Kbis.KbisFocusContext KbisFocus
    {
        get
        {
            if (_kbisFocus == null)
            {
                _kbisFocus = new Smoke.Kbis.KbisFocusContext(KbisCatalog);
                // Propage le toggle fullscreen KBIS vers AnyFocusFullscreen (marge outer edge-to-edge).
                _kbisFocus.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(Smoke.Kbis.KbisFocusContext.IsFullscreen))
                        OnPropertyChanged(nameof(AnyFocusFullscreen));
                };
            }
            return _kbisFocus;
        }
    }

    /// <summary>True si N'IMPORTE quel focus (Rapture OU KBIS) est en plein écran.
    /// Pilote la marge outer du Grid racine → PNG edge-to-edge (toute la largeur du form).</summary>
    public bool AnyFocusFullscreen => FocusIsFullscreen || (_kbisFocus?.IsFullscreen ?? false) || (_alertesFocus?.IsFullscreen ?? false);

    partial void OnFocusIsFullscreenChanged(bool value) => OnPropertyChanged(nameof(AnyFocusFullscreen));

    // ── Rapture Scenarios (manifest-driven, embedded) ──────────────────
    /// <summary>Catalogue chargé depuis le manifest.json du dossier <see cref="RaptureScenariosFolder"/>.</summary>
    public ObservableCollection<RaptureScenario> RaptureScenarios { get; }

    /// <summary>Le scénario sélectionné. Au changement → popule auto JsonFilePath + AudienceIdOverride.</summary>
    [ObservableProperty] private RaptureScenario? selectedRaptureScenario;

    /// <summary>
    /// Chemin du dossier contenant les scénarios (manifest.json + JSONs). Par défaut
    /// pointe sur <c>RaptureScenarios\</c> à côté du .exe (donc inclus dans le zip).
    /// Modifiable par l'utilisateur pour pointer ailleurs (ex. Desktop\JsonRapture\).
    /// </summary>
    [ObservableProperty] private string raptureScenariosFolder = "";

    /// <summary>
    /// Override du JSON par défaut du scénario sélectionné. Permet à l'utilisateur de
    /// pointer un fichier custom sans toucher le manifest. Initialisé auto à partir
    /// du scénario sélectionné.
    /// </summary>
    [ObservableProperty] private string raptureScenarioJsonPathOverride = "";

    /// <summary>
    /// Override de l'AUDNC_ID cible. Si renseigné, l'Orchestrator pointera cette
    /// audience plutôt que celle déduite du header JSON (date+heure). Texte pour
    /// permettre vide (= pas d'override), parsé en int côté run.
    /// </summary>
    [ObservableProperty] private string raptureScenarioAudienceIdOverride = "";

    // ── Rapture Export (tab Smoke Export — single button Start E2E) ─────────
    [ObservableProperty] private string? raptureExportLastStdout;
    [ObservableProperty] private string? raptureExportLastStderr;
    [ObservableProperty] private string raptureExportSummary = "—";
    /// <summary>
    /// Nombre de fichiers <c>smoke-plumitif-*.json</c> déjà produits dans
    /// <c>Desktop\JsonRapture</c> (= nombre de runs Export E2E historiques).
    /// Bind sur le badge du tab Smoke Export — refresh manuel après chaque run.
    /// </summary>
    [ObservableProperty] private int raptureExportFileCount;
    public bool IsRaptureExportRunning => _raptureExportProxy.IsRunning;
    public bool RaptureExportExeExists => _raptureExportProxy.ExeExists;
    public string RaptureExportExeStatus => RaptureExportExeExists
        ? $"SmokeRunner --legacy-rapture-export : {_raptureExportProxy.ExePath}"
        : $"⚠ SmokeRunner.exe introuvable : {_raptureExportProxy.ExePath}";

    // ── Rapture EDI Import (tab Import EDI) : log live du scenario rapture-edi-import.ps1 ──
    [ObservableProperty] private string? raptureEdiImportLastStdout;
    public bool IsRaptureEdiImportRunning => _raptureEdiImportProxy.IsRunning;

    // ── Scénarios section (snapshots .verified.json) ────────────────────
    public ObservableCollection<ScenarioFileViewModel> ScenarioFiles { get; }
    public ListCollectionView ScenarioFilesView { get; }
    [ObservableProperty] private ScenarioFileViewModel? selectedScenarioFile;
    [ObservableProperty] private string? selectedScenarioContent;
    public ScenarioDetailViewModel ScenarioDetail { get; }

    // ── Tabs ─────────────────────────────────────────────────────────────
    /// <summary>0 = Smoke, 1 = xUnit, 2 = Scénarios. Bind sur TabControl.SelectedIndex.</summary>
    [ObservableProperty] private int selectedTabIndex;

    // ── Selection cross-tab ──────────────────────────────────────────────
    [ObservableProperty] private RegressionItemViewModel? selectedRegressionTest;

    /// <summary>Saute à l'onglet xUnit en sélectionnant le test correspondant à la méthode.</summary>
    public void NavigateToXUnitTest(string methodName)
    {
        Log.Info($"NavigateToXUnitTest called: methodName={methodName}, regressionTests.Count={RegressionTests.Count}");
        if (RegressionTests.Count == 0)
        {
            var msg = "⚠ Catalogue xUnit vide. Va dans l'onglet xUnit et clique 'Refresh catalog' (build le projet d'abord si besoin).";
            ScenarioDetail.StatusMessage = msg;
            Log.Warn("NavigateToXUnitTest: " + msg);
            return;
        }
        var match = RegressionTests.FirstOrDefault(r => r.MethodName == methodName);
        if (match is null)
        {
            var known = string.Join(", ", RegressionTests.Take(5).Select(r => r.MethodName));
            var msg = $"⚠ Test xUnit « {methodName} » introuvable dans le catalogue ({RegressionTests.Count} tests connus). Échantillon : {known}…";
            ScenarioDetail.StatusMessage = msg;
            Log.Warn("NavigateToXUnitTest: no match for " + methodName + " — first 5 known: " + known);
            return;
        }
        SelectedRegressionTest = match;
        RegressionView.MoveCurrentTo(match);
        SelectedTabIndex = 1;
        Log.Info($"NavigateToXUnitTest: match found — SelectedTabIndex={SelectedTabIndex}, SelectedRegressionTest={match.DisplayName}");
        ScenarioDetail.StatusMessage = $"✓ Sauté vers xUnit › {match.MethodName}";
    }

    /// <summary>Saute à l'onglet Scénarios en sélectionnant le 1er .verified.json lié à la méthode.</summary>
    public void NavigateToScenarioByMethod(string className, string methodName)
    {
        Log.Info($"NavigateToScenarioByMethod called: {className}.{methodName}, scenarioFiles.Count={ScenarioFiles.Count}");
        var prefix = className + "." + methodName;
        var match = ScenarioFiles.FirstOrDefault(f =>
            f.FileName.StartsWith(prefix + "_", StringComparison.Ordinal) ||
            f.FileName.StartsWith(prefix + ".", StringComparison.Ordinal));
        if (match is null)
        {
            Log.Warn($"NavigateToScenarioByMethod: no scenario file matches prefix '{prefix}'");
            return;
        }
        SelectedScenarioFile = match;
        SelectedTabIndex = 2;
        Log.Info($"NavigateToScenarioByMethod: match {match.FileName} — SelectedTabIndex={SelectedTabIndex}");
    }

    public bool ScenarioFilesDirExists => _scenarioFiles.DirExists;
    public string ScenarioFilesDirStatus => ScenarioFilesDirExists
        ? $"Scénarios : {_scenarioFiles.Dir}"
        : $"⚠ Dossier Scénarios introuvable : {_scenarioFiles.Dir}";

    // ── Global summary ───────────────────────────────────────────────────
    [ObservableProperty] private string globalSummary = "—";
    [ObservableProperty] private string smokeSummary = "—";
    [ObservableProperty] private string regressionSummary = "—";

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunSmoke))]
    private async Task RunSmokeAsync()
    {
        Log.Info($"RunSmoke clicked — EnableUiFlow={EnableUiFlow}");
        // Auto-switch sur l'onglet Logs pour voir le stdout streamer en temps réel.
        SmokeDetailTabIndex = 2;
        try
        {
            await _smokeProxy.RunAsync(EnableUiFlow);
            // Auto-persist stdout/stderr pour diagnostic post-run sans devoir lire la RAM.
            DumpSmokeRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("RunSmoke threw", ex);
            ShowError("Run smoke", ex);
        }
    }
    private bool CanRunSmoke() => !IsSmokeRunning && SmokeExeExists;

    [RelayCommand(CanExecute = nameof(CanRunLegacy))]
    private async Task RunLegacyAsync()
    {
        Log.Info("RunLegacy clicked — KBIS suite (1x VK + 1x XEX)");
        LegacyDetailTabIndex = 0; // tab Logs
        _isLegacyParallelRunning = true;
        OnPropertyChanged(nameof(IsLegacyRunning));
        NotifyKbisCommands();
        try
        {
            // Suite = 1 instance de chaque scénario KBIS distinct (2 tuiles VK + XEX).
            await RunKbisPlanAsync(BuildKbisSuitePlan());
        }
        catch (Exception ex)
        {
            Log.Error("RunLegacy threw", ex);
            ShowError("Run smoke RIG (legacy)", ex);
        }
        finally
        {
            _isLegacyParallelRunning = false;
            OnPropertyChanged(nameof(IsLegacyRunning));
            NotifyKbisCommands();
            _ = System.Threading.Tasks.Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }

    /// <summary>
    /// Exécute UN plan d'instances KBIS (suite OU stress) en parallèle : nouveau RIG_RUN_STAMP,
    /// rebuild des tuiles pour matcher le plan, reset lignes/frozen, spawn de tous les workers
    /// (chacun son HDESK isolé + son sous-dossier self-snap = instanceId), attente, dump stdout.
    /// Le caller gère les flags running + NotifyCanExecuteChanged.
    /// </summary>
    private async Task RunKbisPlanAsync(IReadOnlyList<Smoke.Kbis.KbisInstanceSpec> plan)
    {
        var smokeExe = _legacySmokeProxy.ExePath;
        // Nouveau RIG_RUN_STAMP par vague (sinon les self-snaps s'accumulent dans le dossier du
        // run précédent et le user voit toujours l'ancien PNG figé).
        var runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Environment.SetEnvironmentVariable("RIG_RUN_STAMP", runStamp);

        // Rebuild les tuiles pour qu'elles matchent EXACTEMENT les workers qui vont tourner.
        KbisCatalog.Rebuild(plan);
        Log.Info($"RunKbisPlan — {plan.Count} instances : {string.Join(", ", plan.Select(p => p.InstanceId))}");

        _legacySmokeProxy.ResetLines();
        KbisCatalog.ResetAllFrozenPaths();

        // Spawn tous les workers en parallèle + stream stdout temps réel. Chaque worker a son
        // instanceId comme workerId (tag lignes) ET comme sous-dossier self-snap.
        var tasks = plan.Select(spec =>
            SpawnLegacyKbisWorker(smokeExe, spec.Arg, runStamp, spec.InstanceId)).ToList();

        await Task.WhenAll(tasks);
        Log.Info("KBIS plan done — " +
            string.Join("  ", plan.Select((spec, i) => $"{spec.InstanceId}=exit{tasks[i].Result.exitCode}")));

        var combined = string.Join("\n\n", plan.Select((spec, i) =>
            $"═══ {spec.InstanceId} (PID {tasks[i].Result.pid}, exit={tasks[i].Result.exitCode}) ═══\n{tasks[i].Result.stdout}"));
        LegacyLastFullStdout = combined;
        DumpLegacyRunToDisk();
    }

    // ── KBIS Stress : lancer X fois le MÊME scénario en parallèle (chasse au flake) ──

    private bool _isKbisStressRunning;
    private volatile bool _kbisStressCancel;

    /// <summary>Nb de copies parallèles du scénario sélectionné (clamp 1..8 — garde-fou RAM).</summary>
    [ObservableProperty] private int kbisStressRepeat = 4;
    partial void OnKbisStressRepeatChanged(int value)
    {
        if (value < 1) { KbisStressRepeat = 1; return; }
        if (value > 8) { KbisStressRepeat = 8; return; }
    }

    /// <summary>Si coché : relance des vagues de X jusqu'au 1er échec (puis stop + highlight).</summary>
    [ObservableProperty] private bool kbisStressLoopUntilFail;

    /// <summary>Statut live du stress ("Vague 3 OK, relance…", "❌ ÉCHEC vague 5…").</summary>
    [ObservableProperty] private string kbisStressStatus = "";

    /// <summary>
    /// Stresse le scénario sélectionné : lance X copies en parallèle (1 vague). Si "boucler
    /// jusqu'au 1er échec" est coché, relance des vagues jusqu'à ce qu'une copie rate (ou Stop),
    /// puis sélectionne la tuile rouge (FocusView montre ses steps/logs/PNG). Overrides driver
    /// via env RIG_KBIS_STRESS_SCENARIO/COUNT/LOOP.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunKbisStress))]
    private async Task StressSelectedScenarioAsync()
    {
        LegacyDetailTabIndex = 0;

        // Type : override env (mode driver), sinon la tuile sélectionnée.
        bool isVk;
        var envScen = Environment.GetEnvironmentVariable("RIG_KBIS_STRESS_SCENARIO");
        if (!string.IsNullOrWhiteSpace(envScen))
            isVk = envScen.Trim().Equals("vk", StringComparison.OrdinalIgnoreCase);
        else
        {
            var title = (KbisCatalog.SelectedScenario as Smoke.Kbis.KbisLegacyScenarioAdapter)?.Underlying.Title
                        ?? KbisCatalog.SelectedScenario?.Id ?? "";
            isVk = title.Contains("PROC_VK");
        }

        // X : override env, sinon UI. Clamp 1..8 (garde-fou RAM).
        int x = KbisStressRepeat;
        var envCount = Environment.GetEnvironmentVariable("RIG_KBIS_STRESS_COUNT");
        if (!string.IsNullOrWhiteSpace(envCount) && int.TryParse(envCount, out var ec)) x = ec;
        if (x < 1) x = 1;
        if (x > 8) { Log.Warn($"KBIS stress count {x} capé à 8 (garde-fou RAM)"); x = 8; }

        // Loop : override env, sinon UI.
        bool loop = KbisStressLoopUntilFail;
        var envLoop = Environment.GetEnvironmentVariable("RIG_KBIS_STRESS_LOOP");
        if (!string.IsNullOrWhiteSpace(envLoop))
            loop = envLoop.Trim() == "1" || envLoop.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        string typeLabel = isVk ? "PROC_VK" : "PROC_XEX";
        Log.Info($"KBIS stress START — {x}x {typeLabel} loop={loop}");

        _isKbisStressRunning = true;
        _kbisStressCancel = false;
        OnPropertyChanged(nameof(IsLegacyRunning));
        NotifyKbisCommands();

        try
        {
            int wave = 0;
            while (true)
            {
                wave++;
                KbisStressStatus = $"Vague {wave} : {x}× {typeLabel} en cours…";
                await RunKbisPlanAsync(BuildKbisStressPlan(isVk, x));

                var items = KbisCatalog.Items;
                int pass = items.Count(a => a.Verdict == Verdict.Pass);
                int fail = items.Count(a => a.Verdict == Verdict.Fail);
                Log.Info($"KBIS stress wave done — wave={wave} pass={pass} fail={fail}");

                if (fail > 0)
                {
                    var firstFail = items.FirstOrDefault(a => a.Verdict == Verdict.Fail);
                    if (firstFail != null) KbisCatalog.SelectedScenario = firstFail;
                    KbisStressStatus = $"❌ ÉCHEC vague {wave} : {fail}/{x} {typeLabel} en échec ({firstFail?.Id}). Voir steps/logs/PNG.";
                    Log.Info($"KBIS stress STOPPED on fail at wave {wave} — {firstFail?.Id}");
                    break;
                }

                KbisStressStatus = $"✓ Vague {wave} OK ({pass}/{x} {typeLabel})" + (loop ? " — relance…" : "");
                if (!loop) break;
                if (_kbisStressCancel) { KbisStressStatus = $"⏹ Stoppé après {wave} vague(s) OK ({typeLabel})."; break; }
                if (wave >= 100) { KbisStressStatus = $"⏹ Stop auto à 100 vagues OK (aucun flake, {typeLabel})."; Log.Warn("KBIS stress auto-stop at 100 waves"); break; }
            }
        }
        catch (Exception ex)
        {
            Log.Error("StressSelectedScenario threw", ex);
            ShowError("Stress KBIS", ex);
        }
        finally
        {
            _isKbisStressRunning = false;
            OnPropertyChanged(nameof(IsLegacyRunning));
            NotifyKbisCommands();
            _ = System.Threading.Tasks.Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }

    private bool CanRunKbisStress() => !IsLegacyRunning && LegacyExeExists;

    [RelayCommand(CanExecute = nameof(CanStopKbisStress))]
    private void StopKbisStress()
    {
        _kbisStressCancel = true;
        KbisStressStatus = "⏹ Arrêt demandé (fin de la vague en cours)…";
        Log.Info("KBIS stress stop requested");
    }

    private bool CanStopKbisStress() => _isKbisStressRunning;

    /// <summary>Re-évalue CanExecute des 3 commandes KBIS (mutuellement exclusives).</summary>
    private void NotifyKbisCommands()
    {
        RunLegacyCommand.NotifyCanExecuteChanged();
        StressSelectedScenarioCommand.NotifyCanExecuteChanged();
        StopKbisStressCommand.NotifyCanExecuteChanged();
    }

    // ── Play / Stop PAR-SCÉNARIO (boutons ▶/⏹ du template MosaicView, contrat IScenarioCatalog) ──

    /// <summary>
    /// Play par-scénario (bouton ▶ d'une tuile) : (re)lance CE scénario seul en 1 instance.
    /// Réutilise RunKbisPlanAsync (rebuild → 1 tuile → spawn 1 worker). No-op si un batch tourne.
    /// Symétrique de PlayScenarioAsync (RAPTURE).
    /// </summary>
    public void PlayKbisScenario(Smoke.Kbis.KbisLegacyScenarioAdapter? a)
    {
        // Gate sur IsLegacyBatchBusy (batch/stress/proxy), PAS sur les autres plays → N plays concurrents.
        if (a is null || !LegacyExeExists || IsLegacyBatchBusy) return;
        _ = PlayKbisScenarioImplAsync(a);
    }

    private async Task PlayKbisScenarioImplAsync(Smoke.Kbis.KbisLegacyScenarioAdapter a)
    {
        Log.Info($"PlayKbisScenario — {(a.IsVk ? "PROC_VK" : "PROC_XEX")} ({a.InstanceId}) in-place");
        LegacyDetailTabIndex = 0;
        System.Threading.Interlocked.Increment(ref _legacyPlaysInFlight);
        RaiseLegacyRunningChanged();
        // Throttle : <= LegacyHeavySuiteMaxConcurrency plays simultanés (cap dur RigClientAccueil). Pas de
        // CancellationToken → WaitAsync ne lève pas, Release inconditionnel en finally est sûr.
        await _legacyPlayThrottle.WaitAsync();
        try
        {
            // EN PLACE : on NE rebuild PAS la mosaïque (sinon les autres tuiles disparaissent —
            // bug signalé 2026-05-29). On garde le RIG_RUN_STAMP courant (les autres tuiles
            // conservent leurs snaps) ; on en crée un seulement si aucun run n'a encore eu lieu.
            var smokeExe = _legacySmokeProxy.ExePath;
            var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            if (string.IsNullOrEmpty(runStamp))
            {
                runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Environment.SetEnvironmentVariable("RIG_RUN_STAMP", runStamp);
            }
            var instanceId = a.InstanceId;
            var arg = a.IsVk ? "--legacy-kbis-vk" : "--legacy-kbis-xex";

            // Reset UNIQUEMENT cette instance (ses lignes ✓/✗ + son frozen PNG) → tuile fraîche,
            // les autres tuiles restent intactes.
            _legacySmokeProxy.ResetLinesForWorker(instanceId);
            a.ResetFrozenPath();

            var (exit, stdout, pid) = await SpawnLegacyKbisWorker(smokeExe, arg, runStamp, instanceId);
            Log.Info($"PlayKbisScenario done — {instanceId} exit={exit} pid={pid}");
            LegacyLastFullStdout = $"═══ {instanceId} (PID {pid}, exit={exit}) ═══\n{stdout}";
            DumpLegacyRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("PlayKbisScenario threw", ex);
            ShowError("Play scénario KBIS", ex);
        }
        finally
        {
            _legacyPlayThrottle.Release();
            System.Threading.Interlocked.Decrement(ref _legacyPlaysInFlight);
            RaiseLegacyRunningChanged();
            _ = System.Threading.Tasks.Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }

    /// <summary>
    /// Stop par-scénario (bouton ⏹ d'une tuile) : taskkill le worker de CETTE tuile (/T /F),
    /// comme StopScenario (RAPTURE). Utile pour tuer une instance bloquée pendant un stress.
    /// </summary>
    public void StopKbisScenario(Smoke.Kbis.KbisLegacyScenarioAdapter? a)
    {
        if (a?.WorkerPid is not int pid) return;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(3000);
            Log.Info($"StopKbisScenario — killed pid {pid} ({a.Id}) + children via taskkill /T /F");
        }
        catch (Exception ex) { Log.Warn($"StopKbisScenario pid={pid} failed: {ex.Message}"); }
    }

    // ════════════════ MODULE ALERTES RCS (inline, même organisation que KBIS) ════════════════
    // Reprise interrompue / réclamation : suite 4 tuiles + stress + Play/Stop. Réutilise
    // _legacySmokeProxy + SpawnLegacyKbisWorker. Les accroches (Modules, ModuleTags, IsAlertesModule,
    // AnyFocusFullscreen, ctor filtre, snap-timer, appel UpdateAlertesSummary) sont ailleurs dans ce fichier.

    public ObservableCollection<ScenarioViewModel> AlertesLegacyScenarios { get; }
    public ListCollectionView AlertesLegacyView { get; }

    private Smoke.Alertes.AlertesScenarioCatalog? _alertesCatalog;
    public Smoke.Alertes.AlertesScenarioCatalog AlertesCatalog
        => _alertesCatalog ??= new Smoke.Alertes.AlertesScenarioCatalog(this);

    private Smoke.Alertes.AlertesFocusContext? _alertesFocus;
    public Smoke.Alertes.AlertesFocusContext AlertesFocus
    {
        get
        {
            if (_alertesFocus == null)
            {
                _alertesFocus = new Smoke.Alertes.AlertesFocusContext(AlertesCatalog);
                _alertesFocus.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(Smoke.Alertes.AlertesFocusContext.IsFullscreen))
                        OnPropertyChanged(nameof(AnyFocusFullscreen));
                };
            }
            return _alertesFocus;
        }
    }

    private bool _isAlertesParallelRunning;
    private bool _isAlertesStressRunning;
    private volatile bool _alertesStressCancel;
    public bool IsAlertesBatchBusy => _legacySmokeProxy.IsRunning || _isAlertesParallelRunning || _isAlertesStressRunning;
    public bool IsAlertesRunning => IsAlertesBatchBusy || AnyLegacyPlayInFlight;

    [ObservableProperty] private string? alertesSummary = "✓ 0  ✗ 0  ⏳ 0";
    [ObservableProperty] private int alertesStressRepeat = 4;
    partial void OnAlertesStressRepeatChanged(int value)
    {
        if (value < 1) { AlertesStressRepeat = 1; return; }
        if (value > 8) { AlertesStressRepeat = 8; return; }
    }
    [ObservableProperty] private bool alertesStressLoopUntilFail;
    [ObservableProperty] private string alertesStressStatus = "";

    private static string ArgForAlertesKind(string kind) => $"--legacy-alertes-{kind}";

    private ScenarioViewModel? FindAlertesBase(string kind)
    {
        bool Match(string t) => kind switch
        {
            "int-form" => t.Contains("interrompue") && t.Contains("formalités"),
            "int-dca"  => t.Contains("interrompue") && t.Contains("DCADEMAT"),
            "rec-form" => t.Contains("réclamation") && t.Contains("formalités"),
            _          => t.Contains("réclamation") && t.Contains("DCADEMAT"),
        };
        return AlertesLegacyScenarios.FirstOrDefault(s => Match(s.Title ?? ""));
    }

    /// <summary>Plan SUITE : 1 instance par scénario Alertes (4 tuiles).</summary>
    public IReadOnlyList<Smoke.Alertes.AlertesInstanceSpec> BuildAlertesSuitePlan()
    {
        var plan = new List<Smoke.Alertes.AlertesInstanceSpec>(4);
        foreach (var kind in new[] { "int-form", "int-dca", "rec-form", "rec-dca" })
        {
            var vm = FindAlertesBase(kind);
            if (vm != null)
                plan.Add(new Smoke.Alertes.AlertesInstanceSpec(
                    vm, ArgForAlertesKind(kind), $"alertes-{kind}", 1, kind, showSuffix: false));
        }
        return plan;
    }

    /// <summary>Plan STRESS : X copies du même scénario Alertes.</summary>
    private IReadOnlyList<Smoke.Alertes.AlertesInstanceSpec> BuildAlertesStressPlan(string kind, int x)
    {
        var baseVm = FindAlertesBase(kind);
        var plan = new List<Smoke.Alertes.AlertesInstanceSpec>(x);
        if (baseVm == null) return plan;
        for (int i = 1; i <= x; i++)
            plan.Add(new Smoke.Alertes.AlertesInstanceSpec(
                baseVm, ArgForAlertesKind(kind), $"alertes-{kind}-{i}", i, kind, showSuffix: x > 1));
        return plan;
    }

    private async Task RunAlertesPlanAsync(IReadOnlyList<Smoke.Alertes.AlertesInstanceSpec> plan)
    {
        var smokeExe = _legacySmokeProxy.ExePath;
        var runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Environment.SetEnvironmentVariable("RIG_RUN_STAMP", runStamp);
        AlertesCatalog.Rebuild(plan);
        Log.Info($"RunAlertesPlan — {plan.Count} instances : {string.Join(", ", plan.Select(p => p.InstanceId))}");
        _legacySmokeProxy.ResetLines();
        AlertesCatalog.ResetAllFrozenPaths();
        // Throttle : même schéma que DCADEMAT (chaque worker = un RigClientAccueil lourd) →
        // max 2 simultanés pour des verdicts fiables. Cf. SpawnLegacyWorkersThrottledAsync.
        var results = await SpawnLegacyWorkersThrottledAsync(
            smokeExe, runStamp,
            plan.Select(spec => (spec.Arg, spec.InstanceId)).ToList(), "ALERTES plan");
        Log.Info("ALERTES plan done — " + string.Join("  ", plan.Select((spec, i) => $"{spec.InstanceId}=exit{results[i].exitCode}")));
        var combined = string.Join("\n\n", plan.Select((spec, i) => $"═══ {spec.InstanceId} (PID {results[i].pid}, exit={results[i].exitCode}) ═══\n{results[i].stdout}"));
        LegacyLastFullStdout = combined;
        DumpLegacyRunToDisk();
    }

    [RelayCommand(CanExecute = nameof(CanRunAlertes))]
    private async Task RunAlertesAsync()
    {
        Log.Info("RunAlertes clicked — suite 4 scénarios Alertes RCS");
        LegacyDetailTabIndex = 0;
        _isAlertesParallelRunning = true;
        OnPropertyChanged(nameof(IsAlertesRunning));
        NotifyAlertesCommands();
        try { await RunAlertesPlanAsync(BuildAlertesSuitePlan()); }
        catch (Exception ex) { Log.Error("RunAlertes threw", ex); ShowError("Run smoke Alertes", ex); }
        finally
        {
            _isAlertesParallelRunning = false;
            OnPropertyChanged(nameof(IsAlertesRunning));
            NotifyAlertesCommands();
            _ = Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }
    private bool CanRunAlertes() => !IsAlertesRunning && LegacyExeExists;

    [RelayCommand(CanExecute = nameof(CanRunAlertesStress))]
    private async Task StressAlertesScenarioAsync()
    {
        LegacyDetailTabIndex = 0;
        string kind;
        var envScen = Environment.GetEnvironmentVariable("RIG_ALERTES_STRESS_SCENARIO");
        if (!string.IsNullOrWhiteSpace(envScen)) kind = envScen.Trim().ToLowerInvariant();
        else kind = (AlertesCatalog.SelectedScenario as Smoke.Alertes.AlertesLegacyScenarioAdapter)?.Kind ?? "int-form";

        int x = AlertesStressRepeat;
        var envCount = Environment.GetEnvironmentVariable("RIG_ALERTES_STRESS_COUNT");
        if (!string.IsNullOrWhiteSpace(envCount) && int.TryParse(envCount, out var ec)) x = ec;
        if (x < 1) x = 1;
        if (x > 8) { Log.Warn($"Alertes stress count {x} capé à 8"); x = 8; }

        bool loop = AlertesStressLoopUntilFail;
        var envLoop = Environment.GetEnvironmentVariable("RIG_ALERTES_STRESS_LOOP");
        if (!string.IsNullOrWhiteSpace(envLoop))
            loop = envLoop.Trim() == "1" || envLoop.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        Log.Info($"Alertes stress START — {x}x {kind} loop={loop}");
        _isAlertesStressRunning = true;
        _alertesStressCancel = false;
        OnPropertyChanged(nameof(IsAlertesRunning));
        NotifyAlertesCommands();
        try
        {
            int wave = 0;
            while (true)
            {
                wave++;
                AlertesStressStatus = $"Vague {wave} : {x}× {kind} en cours…";
                await RunAlertesPlanAsync(BuildAlertesStressPlan(kind, x));

                var items = AlertesCatalog.Items;
                int pass = items.Count(a => a.Verdict == Verdict.Pass);
                int fail = items.Count(a => a.Verdict == Verdict.Fail);
                Log.Info($"Alertes stress wave done — wave={wave} pass={pass} fail={fail}");

                if (fail > 0)
                {
                    var firstFail = items.FirstOrDefault(a => a.Verdict == Verdict.Fail);
                    if (firstFail != null) AlertesCatalog.SelectedScenario = firstFail;
                    AlertesStressStatus = $"❌ ÉCHEC vague {wave} : {fail}/{x} {kind} en échec ({firstFail?.Id}). Voir steps/logs/PNG.";
                    Log.Info($"Alertes stress STOPPED on fail at wave {wave} — {firstFail?.Id}");
                    break;
                }
                AlertesStressStatus = $"✓ Vague {wave} OK ({pass}/{x} {kind})" + (loop ? " — relance…" : "");
                if (!loop) break;
                if (_alertesStressCancel) { AlertesStressStatus = $"⏹ Stoppé après {wave} vague(s) OK ({kind})."; break; }
                if (wave >= 100) { AlertesStressStatus = $"⏹ Stop auto à 100 vagues OK ({kind})."; Log.Warn("Alertes stress auto-stop at 100 waves"); break; }
            }
        }
        catch (Exception ex) { Log.Error("StressAlertesScenario threw", ex); ShowError("Stress Alertes", ex); }
        finally
        {
            _isAlertesStressRunning = false;
            OnPropertyChanged(nameof(IsAlertesRunning));
            NotifyAlertesCommands();
            _ = Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }
    private bool CanRunAlertesStress() => !IsAlertesRunning && LegacyExeExists;

    [RelayCommand(CanExecute = nameof(CanStopAlertesStress))]
    private void StopAlertesStress()
    {
        _alertesStressCancel = true;
        AlertesStressStatus = "⏹ Arrêt demandé (fin de la vague en cours)…";
        Log.Info("Alertes stress stop requested");
    }
    private bool CanStopAlertesStress() => _isAlertesStressRunning;

    private void NotifyAlertesCommands()
    {
        RunAlertesCommand.NotifyCanExecuteChanged();
        StressAlertesScenarioCommand.NotifyCanExecuteChanged();
        StopAlertesStressCommand.NotifyCanExecuteChanged();
    }

    public void PlayAlertesScenario(Smoke.Alertes.AlertesLegacyScenarioAdapter? a)
    {
        if (a is null || !LegacyExeExists || IsAlertesBatchBusy) return;
        _ = PlayAlertesScenarioImplAsync(a);
    }

    private async Task PlayAlertesScenarioImplAsync(Smoke.Alertes.AlertesLegacyScenarioAdapter a)
    {
        Log.Info($"PlayAlertesScenario — {a.Kind} ({a.InstanceId}) in-place");
        LegacyDetailTabIndex = 0;
        System.Threading.Interlocked.Increment(ref _legacyPlaysInFlight);
        RaiseLegacyRunningChanged();
        await _legacyPlayThrottle.WaitAsync();   // throttle <= LegacyHeavySuiteMaxConcurrency plays
        try
        {
            var smokeExe = _legacySmokeProxy.ExePath;
            var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            if (string.IsNullOrEmpty(runStamp))
            {
                runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Environment.SetEnvironmentVariable("RIG_RUN_STAMP", runStamp);
            }
            _legacySmokeProxy.ResetLinesForWorker(a.InstanceId);
            a.ResetFrozenPath();
            var (exit, stdout, pid) = await SpawnLegacyKbisWorker(smokeExe, ArgForAlertesKind(a.Kind), runStamp, a.InstanceId);
            Log.Info($"PlayAlertesScenario done — {a.InstanceId} exit={exit} pid={pid}");
            LegacyLastFullStdout = $"═══ {a.InstanceId} (PID {pid}, exit={exit}) ═══\n{stdout}";
            DumpLegacyRunToDisk();
        }
        catch (Exception ex) { Log.Error("PlayAlertesScenario threw", ex); ShowError("Play scénario Alertes", ex); }
        finally
        {
            _legacyPlayThrottle.Release();
            System.Threading.Interlocked.Decrement(ref _legacyPlaysInFlight);
            RaiseLegacyRunningChanged();
            _ = Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }

    public void StopAlertesScenario(Smoke.Alertes.AlertesLegacyScenarioAdapter? a)
    {
        if (a?.WorkerPid is not int pid) return;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(3000);
            Log.Info($"StopAlertesScenario — killed pid {pid} ({a.Id}) via taskkill /T /F");
        }
        catch (Exception ex) { Log.Warn($"StopAlertesScenario pid={pid} failed: {ex.Message}"); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Module DCADEMAT (2026-05-29 soir) — miroir d'Alertes. Réutilise SpawnLegacyKbisWorker +
    // OpenAlerteRcs/OpenFirstDemandeAndVerify côté driver. Tuiles : DCA + Formalité ×
    // validation/réclamation/refus/interrompue. v1 = open alerte + open demande (vérifiable).
    // Pas de stress/RunAll en v1 (Play par tuile). Step métier "Action" = Étape 3 (avec supervision).
    // ════════════════════════════════════════════════════════════════════════

    public ObservableCollection<ScenarioViewModel> DcadematLegacyScenarios { get; }
    public ListCollectionView DcadematLegacyView { get; }

    private Smoke.Dcademat.DcadematScenarioCatalog? _dcadematCatalog;
    public Smoke.Dcademat.DcadematScenarioCatalog DcadematCatalog
        => _dcadematCatalog ??= new Smoke.Dcademat.DcadematScenarioCatalog(this);

    private Smoke.Dcademat.DcadematFocusContext? _dcadematFocus;
    public Smoke.Dcademat.DcadematFocusContext DcadematFocus
        => _dcadematFocus ??= new Smoke.Dcademat.DcadematFocusContext(DcadematCatalog);

    private bool _isDcadematParallelRunning;
    public bool IsDcadematBatchBusy => _legacySmokeProxy.IsRunning || _isDcadematParallelRunning;
    public bool IsDcadematRunning => IsDcadematBatchBusy || AnyLegacyPlayInFlight;

    private static string ArgForDcadematKind(string kind) => $"--legacy-dcademat-{kind}";

    private ScenarioViewModel? FindDcadematBase(string kind)
    {
        bool isForm = kind.StartsWith("form");
        string state = kind.Substring(kind.IndexOf('-') + 1);            // validation/reclamation/refus/interrompue
        string stateKey = state == "reclamation" ? "clamation" : state;  // accent-safe ("réclamation")
        bool Match(string title)
        {
            var t = title.ToLowerInvariant();
            bool famille = isForm ? t.Contains("formalit") : (t.Contains("dca") && !t.Contains("formalit"));
            return famille && t.Contains(stateKey);
        }
        return DcadematLegacyScenarios.FirstOrDefault(s => Match(s.Title ?? ""));
    }

    /// <summary>Plan SUITE : 1 instance par scénario DCADEMAT (DCA + Formalité × 4 états).</summary>
    public IReadOnlyList<Smoke.Dcademat.DcadematInstanceSpec> BuildDcadematSuitePlan()
    {
        var kinds = new[]
        {
            "dca-validation", "dca-reclamation", "dca-refus", "dca-interrompue",
            "form-validation", "form-reclamation", "form-refus", "form-interrompue",
        };
        var plan = new List<Smoke.Dcademat.DcadematInstanceSpec>(kinds.Length);
        foreach (var kind in kinds)
        {
            var vm = FindDcadematBase(kind);
            if (vm != null)
                plan.Add(new Smoke.Dcademat.DcadematInstanceSpec(
                    vm, ArgForDcadematKind(kind), $"dcademat-{kind}", 1, kind, showSuffix: false));
        }
        return plan;
    }

    public void PlayDcadematScenario(Smoke.Dcademat.DcadematLegacyScenarioAdapter? a)
    {
        if (a is null || !LegacyExeExists || IsDcadematBatchBusy) return;
        _ = PlayDcadematScenarioImplAsync(a);
    }

    private async Task PlayDcadematScenarioImplAsync(Smoke.Dcademat.DcadematLegacyScenarioAdapter a)
    {
        Log.Info($"PlayDcadematScenario — {a.Kind} ({a.InstanceId}) in-place");
        LegacyDetailTabIndex = 0;
        System.Threading.Interlocked.Increment(ref _legacyPlaysInFlight);
        RaiseLegacyRunningChanged();
        await _legacyPlayThrottle.WaitAsync();   // throttle <= LegacyHeavySuiteMaxConcurrency plays
        try
        {
            var smokeExe = _legacySmokeProxy.ExePath;
            var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            if (string.IsNullOrEmpty(runStamp))
            {
                runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Environment.SetEnvironmentVariable("RIG_RUN_STAMP", runStamp);
            }
            _legacySmokeProxy.ResetLinesForWorker(a.InstanceId);
            a.ResetFrozenPath();
            var (exit, stdout, pid) = await SpawnLegacyKbisWorker(smokeExe, ArgForDcadematKind(a.Kind), runStamp, a.InstanceId);
            Log.Info($"PlayDcadematScenario done — {a.InstanceId} exit={exit} pid={pid}");
            LegacyLastFullStdout = $"═══ {a.InstanceId} (PID {pid}, exit={exit}) ═══\n{stdout}";
            DumpLegacyRunToDisk();
        }
        catch (Exception ex) { Log.Error("PlayDcadematScenario threw", ex); ShowError("Play scénario DCADEMAT", ex); }
        finally
        {
            _legacyPlayThrottle.Release();
            System.Threading.Interlocked.Decrement(ref _legacyPlaysInFlight);
            RaiseLegacyRunningChanged();
            _ = Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }

    public void StopDcadematScenario(Smoke.Dcademat.DcadematLegacyScenarioAdapter? a)
    {
        if (a?.WorkerPid is not int pid) return;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(3000);
            Log.Info($"StopDcadematScenario — killed pid {pid} ({a.Id})");
        }
        catch (Exception ex) { Log.Warn($"StopDcadematScenario pid={pid} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Cap dur de concurrence pour les suites legacy LOURDES (DCADEMAT, ALERTES) : chaque
    /// worker ouvre un RigClientAccueil complet. Au-delà de ~2-3 instances simultanées, la
    /// contention CPU/RAM/UIA rend les verdicts OK/FAIL non fiables (timeouts, flakes : un
    /// écran complètement chargé ressort en FAIL). On plafonne donc à 2 quoi qu'il arrive,
    /// indépendamment du Parallelism de GlobalSettings (réglé pour le batch RAPTURE selfdrive,
    /// bien plus léger). Le cap effectif = Math.Min(Parallelism, 2) : si l'utilisateur descend
    /// Parallelism à 1, on respecte 1 ; sinon 2.
    /// </summary>
    private const int LegacyHeavySuiteMaxConcurrency = 2;

    /// <summary>
    /// Spawn une liste de workers SmokeRunner legacy (KBIS/ALERTES/DCADEMAT) en respectant un
    /// THROTTLE de concurrence : jamais plus de N workers vivants en même temps, les suivants
    /// démarrant au fur et à mesure que des slots se libèrent. Reprend EXACTEMENT le pattern
    /// SemaphoreSlim de <see cref="RunAllRaptureScenariosAsync"/> (le batch RAPTURE). N est borné
    /// par <see cref="LegacyHeavySuiteMaxConcurrency"/> ET par le Parallelism de GlobalSettings,
    /// puis on prend le min. Les résultats sont retournés DANS L'ORDRE du plan (le caller indexe
    /// par position pour reconstituer le dump combiné).
    /// </summary>
    private async Task<(int exitCode, string stdout, int pid)[]> SpawnLegacyWorkersThrottledAsync(
        string smokeExe, string runStamp,
        IReadOnlyList<(string arg, string workerId)> workers, string suiteLabel)
    {
        // Cap = min(GlobalSettings.Parallelism, cap dur). Borne basse 1 (clamp défensif).
        int configured = _globalSettings?.Current?.Parallelism ?? 4;
        if (configured < 1) configured = 1;
        int cap = Math.Min(configured, LegacyHeavySuiteMaxConcurrency);
        if (cap < 1) cap = 1;
        Log.Info($"{suiteLabel} — throttle concurrence : {workers.Count} workers, max {cap} simultané(s) " +
                 $"(parallelism={configured}, cap dur={LegacyHeavySuiteMaxConcurrency})");

        var results = new (int exitCode, string stdout, int pid)[workers.Count];
        using var semaphore = new SemaphoreSlim(cap, cap);
        var tasks = workers.Select((w, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                results[i] = await SpawnLegacyKbisWorker(smokeExe, w.arg, runStamp, w.workerId);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToList();

        await Task.WhenAll(tasks);

        // ── RETRY-on-transient ────────────────────────────────────────────────────────────────
        // Absorbe la flakiness environnementale (RIG lent sous Mode B 2-concurrent FAIL ~1 scenario
        // aleatoire/run alors que les vrais modes d'echec sont corriges). Politique (cf.
        // LegacyParsing) : tout worker en ECHEC (exit != 0) est relance UNE seule fois, meme flag,
        // en respectant le throttle. Le retry n'est consulte QUE sur exit != 0 -> les scenarios OK
        // du 1er coup ne sont JAMAIS relances (zero surcout, zero regression). UN SEUL retry par
        // scenario (jamais de boucle) : le resultat du 2e essai est final. Le retry reste VISIBLE
        // (logs explicites) et le verdict agrege / "plan done" reflete l'APRES-retry (on remplace
        // results[i] par le resultat du 2e essai).
        var toRetry = Enumerable.Range(0, workers.Count)
                                .Where(i => LegacyParsing.ShouldRetryAfterExit(results[i].exitCode))
                                .ToList();
        if (toRetry.Count > 0)
        {
            Log.Info($"{suiteLabel} — {toRetry.Count} scenario(s) en echec au 1er essai, retry unique : " +
                     string.Join(", ", toRetry.Select(i => $"{workers[i].workerId}(exit{results[i].exitCode})")));

            var retryTasks = toRetry.Select(i => Task.Run(async () =>
            {
                int firstExit = results[i].exitCode;
                var workerId = workers[i].workerId;
                // Purge D'ABORD les lignes parsees de CE worker : la tuile UI recompute son verdict sur
                // le STDOUT DU 2e ESSAI uniquement (sinon le step FAILED du 1er essai reste "premier" et
                // ComputePhase renverrait toujours Fail malgre un retry vert). On purge AVANT d'emettre
                // l'annonce de retry pour que celle-ci (et les lignes du 2e essai) survivent.
                ResetLinesForWorkerOnUi(workerId);
                // Annonce visible APRES la purge (flux temps reel, persiste dans la tuile).
                LogLegacyRetryLine(workerId, LegacyParsing.RetryStartingLogLine(workerId, firstExit));
                await semaphore.WaitAsync();
                (int exitCode, string stdout, int pid) retryResult;
                try
                {
                    retryResult = await SpawnLegacyKbisWorker(smokeExe, workers[i].arg, runStamp, workerId);
                }
                finally
                {
                    semaphore.Release();
                }
                // Verdict final = celui du 2e essai (le 1er etait != 0, sinon pas de retry).
                results[i] = (LegacyParsing.FinalExitAfterRetry(firstExit, retryResult.exitCode),
                              retryResult.stdout, retryResult.pid);
                if (retryResult.exitCode == 0)
                    LogLegacyRetryLine(workerId, LegacyParsing.RetryAbsorbedLogLine(workerId));
                else
                    LogLegacyRetryLine(workerId, LegacyParsing.RetryConfirmedFailLogLine(workerId));
            })).ToList();

            await Task.WhenAll(retryTasks);

            bool allGreen = LegacyParsing.AllPassedAfterRetry(results.Select(r => r.exitCode));
            Log.Info($"{suiteLabel} — apres retry : " +
                     string.Join("  ", workers.Select((w, i) => $"{w.workerId}=exit{results[i].exitCode}")) +
                     $"  => {(allGreen ? "TOUT VERT" : "echec(s) confirme(s)")}");
        }

        return results;
    }

    /// <summary>
    /// Emet une ligne de log de RETRY (annonce / absorbe / confirme) — visible a la fois dans le log
    /// applicatif ET dans le flux de la tuile (taggee <paramref name="workerId"/>), prefixee "✓" pour
    /// que <see cref="SmokeRunnerProxy.AppendLineForParsing"/> la matche et l'affiche dans l'UI. Le
    /// retry ne doit JAMAIS etre masque silencieusement.
    /// </summary>
    private void LogLegacyRetryLine(string workerId, string message)
    {
        Log.Info(message);
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _legacySmokeProxy.AppendLineForParsing("✓ " + message, workerId));
        }
        catch (Exception ex) { Log.Warn($"LogLegacyRetryLine jeté pour {workerId} : {ex.Message}"); }
    }

    /// <summary>Émet la ligne de log du WATCHDOG (worker tué pour hang). Contrairement au retry, c'est un
    /// ÉCHEC : on l'émet avec l'icône « ✗ » ET un retrait initial (deux espaces) pour que
    /// <see cref="SmokeRunnerProxy.AppendLineForParsing"/> (regex « ^\s+[✓✗⊘] … ») la capte comme une
    /// ligne FAILED visible dans la tuile, en plus du log applicatif. Le hang devient ainsi visible dans
    /// l'UI live avant que le retry ne purge et relance. Le retry (qui suit) re-jugera sur le 2e essai.</summary>
    private void LogLegacyWatchdogLine(string workerId, string message)
    {
        Log.Warn(message);
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _legacySmokeProxy.AppendLineForParsing("  ✗ " + message, workerId));
        }
        catch (Exception ex) { Log.Warn($"LogLegacyWatchdogLine jeté pour {workerId} : {ex.Message}"); }
    }

    /// <summary>Purge (sur le thread UI) les lignes parsees du worker <paramref name="workerId"/> avant
    /// son retry, pour que la tuile recompute son verdict sur le seul stdout du 2e essai.</summary>
    private void ResetLinesForWorkerOnUi(string workerId)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _legacySmokeProxy.ResetLinesForWorker(workerId));
        }
        catch (Exception ex) { Log.Warn($"ResetLinesForWorker jeté pour {workerId} : {ex.Message}"); }
    }

    private async Task RunDcadematPlanAsync(IReadOnlyList<Smoke.Dcademat.DcadematInstanceSpec> plan)
    {
        var smokeExe = _legacySmokeProxy.ExePath;
        var runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Environment.SetEnvironmentVariable("RIG_RUN_STAMP", runStamp);
        DcadematCatalog.Rebuild(plan);
        Log.Info($"RunDcadematPlan — {plan.Count} instances : {string.Join(", ", plan.Select(p => p.InstanceId))}");
        _legacySmokeProxy.ResetLines();
        DcadematCatalog.ResetAllFrozenPaths();
        // Throttle : ces workers ouvrent chacun un RigClientAccueil lourd → max 2 simultanés
        // (sinon verdicts non fiables sous contention). Cf. SpawnLegacyWorkersThrottledAsync.
        var results = await SpawnLegacyWorkersThrottledAsync(
            smokeExe, runStamp,
            plan.Select(spec => (spec.Arg, spec.InstanceId)).ToList(), "DCADEMAT plan");
        Log.Info("DCADEMAT plan done — " + string.Join("  ", plan.Select((spec, i) => $"{spec.InstanceId}=exit{results[i].exitCode}")));
        LegacyLastFullStdout = string.Join("\n\n", plan.Select((spec, i) => $"═══ {spec.InstanceId} (PID {results[i].pid}, exit={results[i].exitCode}) ═══\n{results[i].stdout}"));
        DumpLegacyRunToDisk();
    }

    /// <summary>Suite DCADEMAT : lance les 8 tuiles (DCA + Formalité × 4 états) en parallèle.</summary>
    [RelayCommand(CanExecute = nameof(CanRunDcademat))]
    private async Task RunDcadematAsync()
    {
        Log.Info("RunDcademat clicked — suite DCADEMAT (8 tuiles)");
        LegacyDetailTabIndex = 0;
        _isDcadematParallelRunning = true;
        OnPropertyChanged(nameof(IsDcadematRunning));
        RunDcadematCommand.NotifyCanExecuteChanged();
        try { await RunDcadematPlanAsync(BuildDcadematSuitePlan()); }
        catch (Exception ex) { Log.Error("RunDcademat threw", ex); ShowError("Run smoke DCADEMAT", ex); }
        finally
        {
            _isDcadematParallelRunning = false;
            OnPropertyChanged(nameof(IsDcadematRunning));
            RunDcadematCommand.NotifyCanExecuteChanged();
            _ = Task.Run(() => { CleanupOldSelfSnaps(keepLast: 10); RefreshSnapDiskUsage(); });
        }
    }
    private bool CanRunDcademat() => !IsDcadematRunning && LegacyExeExists;

    private void UpdateAlertesSummary()
    {
        int pass = 0, fail = 0, pend = 0;
        if (_alertesCatalog != null)
        {
            foreach (var a in _alertesCatalog.Items)
            {
                switch (a.Verdict)
                {
                    case Verdict.Pass: pass++; break;
                    case Verdict.Fail: fail++; break;
                    default:           pend++; break;
                }
            }
        }
        var duration = _legacySmokeProxy.LastRunDuration is { } d ? $" · {d.TotalSeconds:F1}s" : "";
        AlertesSummary = $"✓ {pass}  ✗ {fail}  ⏳ {pend}{duration}";
    }

    /// <summary>
    /// Spawn un worker SmokeRunner indépendant pour un scenario KBIS. Stream stdout ligne par
    /// ligne et appelle _legacySmokeProxy.AppendLineForParsing pour update les Lines en temps
    /// réel (les cards Mosaïque passent Pass dès que la ligne ✓ correspondante arrive, même
    /// si l'autre worker tourne encore).
    /// </summary>
    private async Task<(int exitCode, string stdout, int pid)> SpawnLegacyKbisWorker(
        string smokeExe, string arg, string runStamp, string workerId)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = smokeExe,
            Arguments = arg,
            WorkingDirectory = Path.GetDirectoryName(smokeExe) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.EnvironmentVariables["RIG_RUN_STAMP"] = runStamp;
        // Headless dérivé du combo CmbKbisMode : Mode A/C → "0" (RIG sur le desktop courant,
        // prend la souris, page PDF rendue+capturée) ; Mode B/D → "1" (HDESK isolé, défaut).
        psi.EnvironmentVariables["RIG_DRIVER_HEADLESS"] = _kbisDriverHeadless ? "1" : "0";
        // Sous-dossier self-snap dédié à CETTE instance (= workerId). Sans ça, N instances du
        // même scenario (kbis-vk-1, kbis-vk-2…) écriraient leurs PNG dans le même dossier.
        psi.EnvironmentVariables["RIG_KBIS_SNAP_ID"] = workerId;
        if (string.IsNullOrEmpty(psi.EnvironmentVariables["RIG_LEGACY_NUM_GESTION"]))
            psi.EnvironmentVariables["RIG_LEGACY_NUM_GESTION"] = "2024B00001";

        var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start null pour {arg}");
        Log.Info($"Worker {arg} spawned : PID={p.Id}");

        var sb = new System.Text.StringBuilder();
        // ── Drain du stdout (streaming ligne par ligne) + attente de sortie, dans UNE tâche de fond ──
        // On lit jusqu'à EOF (qui survient quand le process ferme stdout = quand il se termine, OU quand
        // on le tue) PUIS WaitForExit. Cette tâche complète => le worker est sorti (proprement ou tué).
        // On la COURSE ensuite contre le watchdog : c'est cette course (et pas un await direct) qui
        // empêche un worker en hang (stdout jamais fermé, busy-poll) de bloquer la suite à l'infini.
        var drainTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                sb.AppendLine(line);
                try
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        _legacySmokeProxy.AppendLineForParsing(line, workerId));
                }
                catch (Exception ex) { Log.Warn($"AppendLineForParsing jeté pour {arg} : {ex.Message}"); }
            }
            p.WaitForExit();
        });

        // ── WATCHDOG par worker (filet anti-hang) ────────────────────────────────────────────────
        // Plafond de temps PAR worker (constante + override env, décision PURE testée). Un worker qui
        // dépasse ce délai = HANG (ex. dca-reclamation post-MB1, cpuSec=378 sans sortir) : on tue son
        // ARBRE de process (worker + RigClientAccueil enfant — Process.Kill() ne tue PAS les enfants en
        // .NET Fx 4.8, d'où taskkill /T /F) et on retourne un exit synthétique != 0 (WatchdogKillExitCode)
        // qui ALIMENTE le retry-on-transient EXISTANT (ShouldRetryAfterExit relance tout exit != 0). Ce
        // n'est PAS un mécanisme parallèle : le hang devient un échec normal que la machinerie de retry
        // re-lance (le re-sampling reprend probablement une autre demande -> passe). Un worker qui sort
        // dans les temps (drainTask gagne la course) n'est JAMAIS tué : aucun impact sur les OK-du-1er-coup.
        int watchdogSeconds = LegacyParsing.ResolveWatchdogSeconds(
            Environment.GetEnvironmentVariable("RIG_LEGACY_WATCHDOG_SECONDS"));
        var winner = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(watchdogSeconds))).ConfigureAwait(false);
        if (winner != drainTask)
        {
            // Hang : le délai a gagné la course avant que le worker ne sorte.
            LogLegacyWatchdogLine(workerId, LegacyParsing.WatchdogTimeoutLogLine(workerId, watchdogSeconds));
            KillProcessTree(p.Id, $"watchdog {workerId}");
            // Le kill ferme stdout => drainTask atteint EOF et complète : on l'attend pour vider proprement
            // les dernières lignes (évite une tâche orpheline) avant de retourner l'exit synthétique.
            try { await drainTask.ConfigureAwait(false); } catch (Exception ex) { Log.Warn($"drainTask post-kill jeté pour {arg} : {ex.Message}"); }
            return (LegacyParsing.WatchdogKillExitCode, sb.ToString(), p.Id);
        }

        await drainTask.ConfigureAwait(false);   // sortie normale : propage une éventuelle exception du drain
        return (p.ExitCode, sb.ToString(), p.Id);
    }

    /// <summary>Tue l'ARBRE d'un process (le worker SmokeRunner + le RigClientAccueil enfant qu'il a
    /// lancé) via <c>taskkill /T /F /PID</c>. ⚠ En .NET Framework 4.8, <c>Process.Kill()</c> ne tue PAS
    /// les descendants : on passe donc par taskkill /T (propage à toute la descendance) /F (force).
    /// Best-effort, non bloquant (timeout 3s sur le killer). <paramref name="reason"/> n'est que pour le log.</summary>
    private void KillProcessTree(int pid, string reason)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(3000);
            Log.Info($"KillProcessTree ({reason}) : pid {pid} + enfants tués via taskkill /T /F.");
        }
        catch (Exception ex) { Log.Warn($"KillProcessTree pid={pid} ({reason}) échoué : {ex.Message}"); }
    }

    private bool CanRunLegacy() => !IsLegacyRunning && LegacyExeExists;

    private void DumpLegacyRunToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            // Mode parallel KBIS : on lit LegacyLastFullStdout (set explicitement par RunLegacyAsync
            // avec stdout VK + XEX combinés) car _legacySmokeProxy.LastFullStdout est null
            // (le proxy n'a pas tourné via RunAsync standard). Fallback sur le proxy si single-worker.
            var stdoutContent = !string.IsNullOrEmpty(LegacyLastFullStdout)
                ? LegacyLastFullStdout
                : _legacySmokeProxy.LastFullStdout;
            if (!string.IsNullOrEmpty(stdoutContent))
                File.WriteAllText(Path.Combine(dir, $"legacy-{stamp}.stdout.log"), stdoutContent);
            if (!string.IsNullOrEmpty(_legacySmokeProxy.LastStderr))
                File.WriteAllText(Path.Combine(dir, $"legacy-{stamp}.stderr.log"), _legacySmokeProxy.LastStderr);
            Log.Info($"Legacy smoke finished — stdout={stdoutContent?.Length ?? 0} chars");
        }
        catch (Exception ex) { Log.Error("Failed to dump legacy run", ex); }
    }

    private void OnLegacyLinesChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            var snapshot = _legacySmokeProxy.Lines;
            foreach (var vm in LegacyScenarios) vm.RefreshFrom(snapshot);
            LegacyLastFullStdout = _legacySmokeProxy.LastFullStdout;
            UpdateLegacySummary();
        }));
    }

    private void OnLegacyStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsLegacyRunning));
            RunLegacyCommand.NotifyCanExecuteChanged();
            if (!_legacySmokeProxy.IsRunning)
            {
                LegacyLastFullStdout = _legacySmokeProxy.LastFullStdout;
                LegacyLastStderr = _legacySmokeProxy.LastStderr;
                UpdateLegacySummary();
            }
        }));
    }

    private void UpdateLegacySummary()
    {
        int pass = 0, fail = 0, skip = 0;
        foreach (var vm in LegacyScenarios)
        {
            switch (vm.MatchedLine?.Outcome)
            {
                case SmokeOutcome.Passed:  pass++; break;
                case SmokeOutcome.Failed:  fail++; break;
                case SmokeOutcome.Skipped: skip++; break;
            }
        }
        var duration = _legacySmokeProxy.LastRunDuration is { } d ? $" · {d.TotalSeconds:F1}s" : "";
        LegacySummary = $"✓ {pass}  ✗ {fail}  ⊘ {skip}{duration}";

        // Summary KBIS-spécifique : compte les INSTANCES KBIS (tuiles) par Verdict — supporte
        // le parallélisme (N instances VK/XEX). Bound dans le header module KBIS.
        // Ne force pas la création du catalog si pas encore touché (évite un new au boot).
        int kpass = 0, kfail = 0, kpend = 0;
        if (_kbisCatalog != null)
        {
            foreach (var a in _kbisCatalog.Items)
            {
                switch (a.Verdict)
                {
                    case Verdict.Pass: kpass++; break;
                    case Verdict.Fail: kfail++; break;
                    default:           kpend++; break;
                }
            }
        }
        KbisSummary = $"✓ {kpass}  ✗ {kfail}  ⏳ {kpend}{duration}";
        UpdateAlertesSummary();
    }

    [ObservableProperty] private string? kbisSummary = "✓ 0  ✗ 0  ⊘ 0";

    private void DumpSmokeRunToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var stdoutPath = Path.Combine(dir, $"smoke-{stamp}.stdout.log");
            var stderrPath = Path.Combine(dir, $"smoke-{stamp}.stderr.log");
            if (!string.IsNullOrEmpty(_smokeProxy.LastFullStdout))
                File.WriteAllText(stdoutPath, _smokeProxy.LastFullStdout);
            if (!string.IsNullOrEmpty(_smokeProxy.LastStderr))
                File.WriteAllText(stderrPath, _smokeProxy.LastStderr);
            Log.Info($"Smoke run finished — exit={_smokeProxy.LastExitCode}, duration={_smokeProxy.LastRunDuration?.TotalSeconds:F1}s. " +
                $"stdout dumped to {stdoutPath}");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to dump smoke run", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunRegression))]
    private async Task RunRegressionAsync()
    {
        try { await _regression.RunAsync(); }
        catch (Exception ex) { ShowError("Run regression", ex); }
    }
    private bool CanRunRegression() => !IsRegressionRunning && RegressionProjectExists;

    [RelayCommand]
    private async Task RefreshRegressionAsync()
    {
        var infos = await _regressionCatalog.GetAsync(forceRefresh: true);
        RegressionTests.Clear();
        foreach (var info in infos)
        {
            RegressionTests.Add(new RegressionItemViewModel(info, _regression, NavigateToScenarioByMethod));
        }
        // Re-tag les paires + reconstruit les chips.
        TagTransitionPairs(
            RegressionTests.Select(r => (key: r.ClassName + "." + r.MethodName, backend: r.Backend, ensure: (Action<string>)r.EnsureTag)));
        RebuildChips(RegressionFilterTags, RegressionTests.Select(r => (IReadOnlyList<string>)r.Tags), () => RegressionView.Refresh(), includeStackTags: true);
        OnRegressionResultsUpdated();
    }

    // ── Rapture regression commands ───────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanRunRaptureRegression))]
    private async Task RunRaptureRegressionAsync()
    {
        try { await _raptureRegression.RunAsync(); }
        catch (Exception ex) { ShowError("Run regression Rapture", ex); }
    }
    private bool CanRunRaptureRegression() => !IsRaptureRegressionRunning && RaptureRegressionProjectExists;

    [RelayCommand]
    private async Task RefreshRaptureRegressionAsync()
    {
        var infos = await _raptureRegressionCatalog.GetAsync(forceRefresh: true);
        RaptureRegressionTests.Clear();
        foreach (var info in infos)
        {
            RaptureRegressionTests.Add(new RegressionItemViewModel(info, _raptureRegression, null));
        }
        // Re-tag les paires + reconstruit les chips de filtre (legacy/api/transition, etc.)
        // Donne le filtre tags style KBIS dans le tab xUnit Rapture.
        TagTransitionPairs(
            RaptureRegressionTests.Select(r => (key: r.ClassName + "." + r.MethodName, backend: r.Backend, ensure: (Action<string>)r.EnsureTag)));
        RebuildChips(RaptureRegressionFilterTags, RaptureRegressionTests.Select(r => (IReadOnlyList<string>)r.Tags), () => RaptureRegressionView.Refresh(), includeStackTags: false);
        OnRaptureRegressionResultsUpdated();
    }

    [RelayCommand]
    private void RefreshScenarioFiles() => ReloadScenarioFiles();

    [RelayCommand]
    private async Task RunAllAsync()
    {
        // Lance smoke puis regression — séquentiel pour ne pas saturer la machine.
        if (CanRunSmoke())      await RunSmokeAsync();
        if (CanRunRegression()) await RunRegressionAsync();
    }
    private bool CanRunAll() => CanRunSmoke() || CanRunRegression();

    /// <summary>
    /// Ouvre le dialog modal "Paramètres globaux" (Parallelism, ScreenshotsLoop…).
    /// Persiste les changements dans %LOCALAPPDATA%\rig-wpf-kbis\global-settings.json
    /// au clic 'Enregistrer'. Bouton ⚙ en bas du Left Rail.
    /// </summary>
    [RelayCommand]
    private void OpenGlobalSettings()
    {
        try
        {
            var dlg = new Views.GlobalSettingsDialog(_globalSettings, _testCache)
            {
                Owner = Application.Current?.MainWindow,
            };
            var result = dlg.ShowDialog();
            if (result == true)
            {
                Log.Info($"GlobalSettings updated: parallelism={_globalSettings.Current.Parallelism} screenshotsLoop={_globalSettings.Current.ScreenshotsLoopEnabled} db={_globalSettings.Current.DatabaseServer}/{_globalSettings.Current.DatabaseName}");
                // Re-applique la connexion DB choisie sur l'env var → les prochains workers l'héritent.
                DatabaseStartup.Apply(_globalSettings.Current);
                OnPropertyChanged(nameof(GlobalSettingsSummary));
                // Note : le parallélisme global pilote RAPTURE uniquement. La suite KBIS = 2 tuiles
                // fixes (1 VK + 1 XEX) ; le nb de tuiles stress vient du champ X dédié, pas d'ici.
            }
        }
        catch (Exception ex)
        {
            Log.Error("OpenGlobalSettings threw", ex);
            ShowError("Paramètres globaux", ex);
        }
    }

    /// <summary>
    /// Résumé visible dans la status bar : "⚙ 4 instances" (parallelism courant)
    /// + 👻 si headless mode actif. Donne un coup d'œil rapide sur la config.
    /// </summary>
    public string GlobalSettingsSummary
    {
        get
        {
            var par = _globalSettings?.Current?.Parallelism ?? 4;
            var head = _globalSettings?.Current?.HeadlessMode ?? true;
            return head ? $"⚙ {par} (👻)" : $"⚙ {par} (👁)";
        }
    }

    // ── Init ─────────────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        // Découvre les xUnit (peut échouer si le projet pas buildé — on tolère)
        try
        {
            var infos = await _regressionCatalog.GetAsync();
            foreach (var info in infos)
            {
                RegressionTests.Add(new RegressionItemViewModel(info, _regression, NavigateToScenarioByMethod));
            }
        }
        catch (Exception ex) { Log.Error("InitializeAsync: catalog discovery failed", ex); }

        // Découvre les tests Rapture (2ème projet xUnit, projet sibling)
        try
        {
            var raptureInfos = await _raptureRegressionCatalog.GetAsync();
            foreach (var info in raptureInfos)
            {
                RaptureRegressionTests.Add(new RegressionItemViewModel(info, _raptureRegression, null));
            }
            // Tag les paires legacy↔native du projet Rapture + initialise les chips de filtre.
            TagTransitionPairs(
                RaptureRegressionTests.Select(r => (key: r.ClassName + "." + r.MethodName, backend: r.Backend, ensure: (Action<string>)r.EnsureTag)));
            RebuildChips(RaptureRegressionFilterTags, RaptureRegressionTests.Select(r => (IReadOnlyList<string>)r.Tags), () => RaptureRegressionView.Refresh(), includeStackTags: false);
            OnRaptureRegressionResultsUpdated();
        }
        catch (Exception ex) { Log.Error("InitializeAsync: Rapture catalog discovery failed", ex); }

        // Post-process : détecte les "paires" (même Class.Method en legacy ET native) et tag "transition".
        TagTransitionPairs(
            RegressionTests.Select(r => (key: r.ClassName + "." + r.MethodName, backend: r.Backend, ensure: (Action<string>)r.EnsureTag)));
        TagTransitionPairs(
            ScenarioFiles.Select(s => (key: s.Info.Metadata.ClassName + "." + s.Info.Metadata.MethodName,
                                       backend: s.Info.Metadata.Backend,
                                       ensure: (Action<string>)s.EnsureTag)));

        // Construit les chips xUnit + Scénarios maintenant que toutes les paires sont taguées.
        RebuildChips(RegressionFilterTags, RegressionTests.Select(r => (IReadOnlyList<string>)r.Tags), () => RegressionView.Refresh(), includeStackTags: true);
        RebuildChips(ScenarioFilterTags,   ScenarioFiles.Select(s => (IReadOnlyList<string>)s.Tags),     () => ScenarioFilesView.Refresh());

        OnRegressionResultsUpdated();
        UpdateSummary();
    }

    // ── Filter helpers ───────────────────────────────────────────────────

    /// <summary>Pas de filtre actif → tout passe. Sinon : au moins un tag actif ∈ tags item.</summary>
    private static bool AnyActiveTagMatches(ObservableCollection<TagChipViewModel>? chips,
        IReadOnlyList<string> itemTags)
    {
        if (chips is null) return true;
        var active = chips.Where(c => c.IsActive).Select(c => c.Tag).ToList();
        if (active.Count == 0) return true;
        foreach (var t in itemTags)
            if (active.Contains(t, StringComparer.Ordinal)) return true;
        return false;
    }

    private static IEnumerable<(string tag, int count)> GatherTags(
        IEnumerable<IReadOnlyList<string>> itemTags, bool includeStackTags = false)
    {
        // legacy/wpf : exclus par défaut (chips Smoke/Scénarios) ; inclus pour la
        // section REGRESSION (includeStackTags=true) où ils servent de raffinement
        // contextuel à côté de api/transition. Plus de StackFilter global.
        return itemTags
            .SelectMany(t => t)
            .Where(t => includeStackTags || (t != TestTags.Legacy && t != TestTags.Wpf))
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(p => TagOrder(p.Key));
    }

    private static int TagOrder(string tag) => tag switch
    {
        TestTags.Legacy     => 0,
        TestTags.Wpf        => 1,
        TestTags.Api        => 2,
        TestTags.Transition => 3,
        TestTags.Bridge     => 4,
        TestTags.E2E        => 5,
        TestTags.Smoke      => 6,
        _                   => 99,
    };

    private static ObservableCollection<TagChipViewModel> BuildChips(
        IEnumerable<(string tag, int count)> tags, Action onToggle)
    {
        var col = new ObservableCollection<TagChipViewModel>();
        foreach (var (tag, count) in tags)
        {
            var chip = new TagChipViewModel(tag, count) { ToggleListener = onToggle };
            col.Add(chip);
        }
        return col;
    }

    /// <summary>Reconstruit une collection chips IN-PLACE (préserve la référence pour le binding XAML).</summary>
    private static void RebuildChips(ObservableCollection<TagChipViewModel> target,
        IEnumerable<IReadOnlyList<string>> itemTags, Action onToggle,
        bool includeStackTags = false)
    {
        target.Clear();
        foreach (var (tag, count) in GatherTags(itemTags, includeStackTags))
        {
            target.Add(new TagChipViewModel(tag, count) { ToggleListener = onToggle });
        }
    }

    /// <summary>
    /// Pour chaque (Class.Method), si on a au moins 1 backend legacy ET 1 backend native,
    /// ajoute le tag <c>transition</c> à TOUS les items de la paire (les 2 ou +).
    /// </summary>
    private static void TagTransitionPairs(
        IEnumerable<(string key, string? backend, Action<string> ensure)> items)
    {
        var list = items.ToList();
        var groups = list.GroupBy(i => i.key);
        foreach (var g in groups)
        {
            var backends = g.Select(i => i.backend).Where(b => b is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hasLegacy = backends.Any(b => string.Equals(b, "legacy", StringComparison.OrdinalIgnoreCase));
            var hasNative = backends.Any(b => string.Equals(b, "native", StringComparison.OrdinalIgnoreCase));
            if (hasLegacy && hasNative)
            {
                foreach (var item in g) item.ensure(TestTags.Transition);
            }
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    partial void OnSelectedScenarioFileChanged(ScenarioFileViewModel? value)
    {
        SelectedScenarioContent = value is null ? null : _scenarioFiles.Read(value.FileName);
        if (value is not null)
        {
            // Charge le VM détail (champs structurés + source + summary).
            var info = new ScenarioFileInfo(value.FileName, 0);
            ScenarioDetail.Load(info);
        }
    }

    private void OnSmokeLinesChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            var snapshot = _smokeProxy.Lines;
            foreach (var vm in SmokeScenarios) vm.RefreshFrom(snapshot);
            // Live streaming du stdout pendant le run : push à chaque ligne pour que
            // l'onglet Logs se mette à jour en temps réel (pas seulement à la fin).
            SmokeLastFullStdout = _smokeProxy.LastFullStdout;
            UpdateSummary();
        }));
    }

    private void OnSmokeStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsSmokeRunning));
            RunSmokeCommand.NotifyCanExecuteChanged();
            RunAllCommand.NotifyCanExecuteChanged();
            if (!_smokeProxy.IsRunning)
            {
                SmokeLastFullStdout = _smokeProxy.LastFullStdout;
                SmokeLastStderr = _smokeProxy.LastStderr;
                RefreshRenders();
                PdfPreviewPath = _smokeProxy.LastGeneratedPdfPath();
                UpdateSummary();
            }
        }));
    }

    private void OnRegressionResultsUpdated()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            foreach (var vm in RegressionTests) vm.RefreshFromRunner();
            RegressionLastStdout = _regression.LastFullStdout;
            RegressionLastStderr = _regression.LastStderr;
            UpdateSummary();
        }));
    }

    private void OnRegressionStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsRegressionRunning));
            RunRegressionCommand.NotifyCanExecuteChanged();
            RunAllCommand.NotifyCanExecuteChanged();
        }));
    }

    private void OnRaptureRegressionResultsUpdated()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            foreach (var vm in RaptureRegressionTests) vm.RefreshFromRunner();
            RaptureRegressionLastStdout = _raptureRegression.LastFullStdout;
            RaptureRegressionLastStderr = _raptureRegression.LastStderr;
            int pass = 0, fail = 0;
            foreach (var vm in RaptureRegressionTests)
            {
                if (vm.LastResult?.Passed == true) pass++;
                else if (vm.LastResult?.Failed == true) fail++;
            }
            var duration = _raptureRegression.LastRunDuration is { } d ? $" · {d.TotalSeconds:F1}s" : "";
            RaptureRegressionSummary = $"✓ {pass}  ✗ {fail}  · {RaptureRegressionTests.Count} tests{duration}";
        }));
    }

    private void OnRaptureRegressionStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsRaptureRegressionRunning));
            RunRaptureRegressionCommand.NotifyCanExecuteChanged();
        }));
    }

    /// <summary>
    /// Live : à chaque ligne stdout dotnet test, ré-affecte la propriété bound à la
    /// console B2 (déclenche un rendu WPF). Pas de dispatcher gymnastics côté UI car
    /// la propriété [ObservableProperty] fire son PropertyChanged sur le thread courant
    /// et WPF marshal automatiquement. Mais on passe quand même par Dispatcher.BeginInvoke
    /// pour être 100% safe (la TextBox bind sur ce property).
    /// </summary>
    private void OnRegressionLiveStdoutChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            RegressionLastStdout = _regression.LastFullStdout;
        }));
    }

    private void OnRaptureRegressionLiveStdoutChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            RaptureRegressionLastStdout = _raptureRegression.LastFullStdout;
        }));
    }

    /// <summary>
    /// Live : à chaque verdict de test détecté dans le stdout dotnet test, retrouve
    /// le <see cref="RegressionItemViewModel"/> correspondant et lui demande de se
    /// refresh depuis le runner — sa StatusIcon et son StatusBrushKey passent
    /// instantanément du gris au vert/rouge. Met aussi à jour le summary count live.
    /// </summary>
    private void OnRegressionTestProgress(TestProgress tp)
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            ApplyLiveProgress(RegressionTests, tp);
            UpdateSummary();
        }));
    }

    private void OnRaptureRegressionTestProgress(TestProgress tp)
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            ApplyLiveProgress(RaptureRegressionTests, tp);
            // Recompute live summary (pass/fail count + tests total)
            int pass = 0, fail = 0;
            foreach (var vm in RaptureRegressionTests)
            {
                if (vm.LastResult?.Passed == true) pass++;
                else if (vm.LastResult?.Failed == true) fail++;
            }
            RaptureRegressionSummary = $"✓ {pass}  ✗ {fail}  · {RaptureRegressionTests.Count} tests · LIVE";
        }));
    }

    /// <summary>
    /// Match un <see cref="TestProgress.FullName"/> (xUnit FQN
    /// <c>Namespace.Class.Method(...)</c>) contre les items de <paramref name="items"/>
    /// (qui ont <c>ClassName + MethodName</c> séparés) et appelle <c>RefreshFromRunner</c>
    /// sur le match. Tolère les Theories (paramètres entre parenthèses).
    /// </summary>
    private static void ApplyLiveProgress(
        System.Collections.ObjectModel.ObservableCollection<RegressionItemViewModel> items,
        TestProgress tp)
    {
        // Strip theory params : "Foo.Bar(x: 1)" → "Foo.Bar"
        var fn = tp.FullName;
        var paren = fn.IndexOf('(');
        if (paren > 0) fn = fn.Substring(0, paren);
        // Compare en suffix : le FQN dotnet test peut être préfixé par Namespace,
        // alors que vm.ClassName est sans namespace.
        foreach (var vm in items)
        {
            var key = vm.ClassName + "." + vm.MethodName;
            if (fn.EndsWith(key, StringComparison.Ordinal))
            {
                vm.RefreshFromRunner();
            }
        }
    }

    // ── Rapture smoke import : command + helpers ──────────────────────────

    /// <summary>Scan Desktop\JsonRapture\ + Source\Wpf\Rig.Rapture.Tests\Fixtures pour peupler la liste de JSONs.</summary>
    private void RefreshRaptureJsonFixtures()
    {
        RaptureJsonFixtures.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void TryAdd(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!File.Exists(p)) return;
            if (!seen.Add(Path.GetFullPath(p))) return;
            RaptureJsonFixtures.Add(p);
        }
        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "JsonRapture");
            if (Directory.Exists(desktop))
            {
                foreach (var f in Directory.EnumerateFiles(desktop, "*.json")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc))
                    TryAdd(f);
            }
            var fixturesDir = Path.Combine(_raptureRegression.ProjectDir, "Fixtures");
            if (Directory.Exists(fixturesDir))
            {
                foreach (var f in Directory.EnumerateFiles(fixturesDir, "*.json")
                    .OrderBy(f => Path.GetFileName(f)))
                    TryAdd(f);
            }
        }
        catch (Exception ex) { Log.Error("RefreshRaptureJsonFixtures", ex); }

        // Pre-select : si la fixture V5 PC est disponible, on la choisit par défaut.
        var v5 = RaptureJsonFixtures.FirstOrDefault(p =>
            Path.GetFileName(p).IndexOf("pc-v5", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? RaptureJsonFixtures.FirstOrDefault();
        SelectedRaptureJsonFixture = v5;
    }

    [RelayCommand]
    private void RefreshRaptureJsons()
    {
        RefreshRaptureJsonFixtures();
    }

    [RelayCommand(CanExecute = nameof(CanRunRaptureSmoke))]
    private async Task RunRaptureSmokeAsync()
    {
        // NB : commande historique du bouton "Smoke UI" qui n'existe plus en UI
        // depuis 2026-05. Le bouton "▶ Start E2E" branche désormais sur
        // RunRaptureProcessE2EAsync (self-drive ou legacy visible selon checkbox
        // "Mode visible"). Conservée pour compat des modes drive-testviewer-*.
        var jsonPath = SelectedRaptureJsonFixture;
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
        {
            ShowError("Run smoke Rapture", new FileNotFoundException("Aucun JSON sélectionné ou fichier introuvable", jsonPath));
            return;
        }
        Log.Info($"RunRaptureSmoke clicked — json={jsonPath} (acceptCreate=default true)");
        _raptureSmokeProxy.ExtraArgs = $"--json \"{jsonPath}\"";
        try
        {
            await _raptureSmokeProxy.RunAsync(enableUi: false);
            DumpRaptureSmokeRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("RunRaptureSmoke threw", ex);
            ShowError("Run smoke Rapture", ex);
        }
    }
    private bool CanRunRaptureSmoke() =>
        !IsRaptureSmokeRunning
        && RaptureSmokeExeExists
        && !string.IsNullOrEmpty(SelectedRaptureJsonFixture)
        && File.Exists(SelectedRaptureJsonFixture);

    /// <summary>
    /// Diagnostic d'import complet : Mapper/Validator/Diff/Apply RÉEL sur
    /// l'audience source + rapport (tables remplies + valeurs qui n'ont pas
    /// trouvé leur place) + restore net-zero. Réutilise le proxy Rapture
    /// (donc la même console B2) en basculant FixedArgs sur --rapture-diag.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRaptureSmoke))]
    private async Task RunRaptureDiagAsync()
    {
        var jsonPath = SelectedRaptureJsonFixture;
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
        {
            ShowError("Diagnostic import Rapture", new FileNotFoundException("Aucun JSON sélectionné ou fichier introuvable", jsonPath));
            return;
        }
        Log.Info($"RunRaptureDiag clicked — json={jsonPath}");
        var savedFixed = _raptureSmokeProxy.FixedArgs;
        _raptureSmokeProxy.FixedArgs = "--rapture-diag";
        _raptureSmokeProxy.ExtraArgs = $"--json \"{jsonPath}\"";
        try
        {
            await _raptureSmokeProxy.RunAsync(enableUi: false);
            DumpRaptureSmokeRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("RunRaptureDiag threw", ex);
            ShowError("Diagnostic import Rapture", ex);
        }
        finally
        {
            _raptureSmokeProxy.FixedArgs = savedFixed;
        }
    }

    /// <summary>
    /// Process E2E : bascule entre 2 modes selon la checkbox "Mode visible" :
    /// - DEFAULT (selfdrive) : <c>--rapture-selfdrive</c> = worker RigClientAccueil
    ///   invisible, pipeline d'import in-process. Rapide, assertions DB/UIA complètes.
    /// - VISIBLE (legacy) : <c>--legacy-rapture-process</c> = ouvre RIG sur l'écran,
    ///   login RIG, PROC_RETAUD, audience ciblée, clic Import Rapture, recap visible.
    ///   Plus lent (~30s/scénario) mais permet de SUIVRE le test à l'œil.
    /// Mode "All scenarios" force toujours selfdrive (sinon 16 RIG visibles séquentiels).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRaptureScenarioE2E))]
    private async Task RunRaptureProcessE2EAsync()
    {
        if (IsAllScenariosSelected)
        {
            await RunAllRaptureScenariosAsync();
            return;
        }

        var jsonPath = !string.IsNullOrEmpty(RaptureScenarioJsonPathOverride)
            ? RaptureScenarioJsonPathOverride
            : SelectedRaptureJsonFixture;
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
        {
            ShowError("Start E2E Rapture", new FileNotFoundException("Aucun JSON sélectionné ou fichier introuvable", jsonPath));
            return;
        }
        int? audienceIdOverride = null;
        if (!string.IsNullOrWhiteSpace(RaptureScenarioAudienceIdOverride)
            && int.TryParse(RaptureScenarioAudienceIdOverride.Trim(), out var parsed))
        {
            audienceIdOverride = parsed;
        }

        var scenarioId = SelectedRaptureScenario?.Id ?? "(adhoc)";
        bool visibleMode = RaptureSmokeVisibleMode;
        string mode = visibleMode ? "legacy-visible" : "selfdrive";
        Log.Info($"RunRaptureProcessE2E [{mode}] — scenario={scenarioId} json={jsonPath} audOverride={audienceIdOverride?.ToString() ?? "(none)"} applyReal={RaptureSmokeApplyReal} visible={visibleMode}");
        var savedFixed = _raptureSmokeProxy.FixedArgs;
        _raptureSmokeProxy.FixedArgs = visibleMode ? "--legacy-rapture-process" : "--rapture-selfdrive";
        var extra = $"--json \"{jsonPath}\"";
        if (RaptureSmokeApplyReal) extra += " --apply";
        if (audienceIdOverride.HasValue) extra += $" --audience-id {audienceIdOverride.Value}";
        if (visibleMode && SelectedRaptureScenario != null)
        {
            // --legacy-rapture-process navigue par date+heure dans la grille RETAUD
            extra += $" --audience-date {SelectedRaptureScenario.AudienceDate}";
            extra += $" --audience-heure {SelectedRaptureScenario.AudienceHeure}";
        }
        if (!visibleMode && scenarioId == "cas-b-multi-match") extra += " --cas-b-auto-setup";
        // --scenario-id passe DANS LES DEUX MODES pour que le worker nomme correctement
        // son dir self-snap (sinon fallback "(adhoc)" → nom du JSON → collision quand
        // plusieurs scenarios partagent le meme JSON, ex: cas-b-multi-match reuses cas-a-contentieux-10).
        extra += $" --scenario-id \"{scenarioId}\"";
        // Les assertions structurées ne sont câblées que côté selfdrive
        if (!visibleMode)
        {
            if (SelectedRaptureScenario?.ExpectedWarnings.HasValue == true)
                extra += $" --expected-warnings {SelectedRaptureScenario.ExpectedWarnings.Value}";
            if (SelectedRaptureScenario?.EffectiveExpectedDetected.HasValue == true)
                extra += $" --expected-detected-modifications {SelectedRaptureScenario.EffectiveExpectedDetected.Value}";
            if (SelectedRaptureScenario?.EffectiveExpectedApplied.HasValue == true)
                extra += $" --expected-applied-modifications {SelectedRaptureScenario.EffectiveExpectedApplied.Value}";
            if (SelectedRaptureScenario?.ExpectedValidationErrors.HasValue == true)
                extra += $" --expected-validation-errors {SelectedRaptureScenario.ExpectedValidationErrors.Value}";
            if (!string.IsNullOrEmpty(SelectedRaptureScenario?.ExpectedMessageContains))
                extra += $" --expected-message-contains \"{SelectedRaptureScenario.ExpectedMessageContains}\"";
            // Fail-closed : un scénario A/B/C dont le worker rapporte ok=False (ex. MSDTC sur INSERT Cas C)
            // doit virer ROUGE ; les scénarios ERROR_* sont exemptés côté handler.
            if (!string.IsNullOrEmpty(SelectedRaptureScenario?.ExpectedCase))
                extra += $" --expected-case \"{SelectedRaptureScenario.ExpectedCase}\"";
        }
        _raptureSmokeProxy.ExtraArgs = extra;
        // Propagate RIG_DRIVER_HEADLESS depuis GlobalSettings au worker SmokeRunner.
        // En single visible mode : permettre de voir RIG s'ouvrir sur le desktop utilisateur.
        bool headlessSingle = _globalSettings?.Current?.HeadlessMode ?? true;
        _raptureSmokeProxy.EnvironmentOverrides = new Dictionary<string, string>
        {
            ["RIG_DRIVER_HEADLESS"] = headlessSingle ? "1" : "0"
        };
        try
        {
            await _raptureSmokeProxy.RunAsync(enableUi: false);
            DumpRaptureSmokeRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("RunRaptureProcessE2E threw", ex);
            ShowError("Start E2E Rapture", ex);
        }
        finally
        {
            _raptureSmokeProxy.FixedArgs = savedFixed;
        }
    }

    /// <summary>
    /// Itère tous les scénarios en PARALLÈLE via des workers SmokeRunner.
    /// Mode default = <c>--rapture-selfdrive</c> (worker RigClientAccueil INVISIBLE
    /// in-process, rapide). Si <c>RaptureSmokeVisibleMode</c> = true, bascule sur
    /// <c>--legacy-rapture-process</c> pour spawn N instances RIG VISIBLES en parallèle
    /// (FlaUI pilote chaque instance, chaque process a son propre handle).
    /// Degré de parallélisme contrôlé par RIG_SMOKE_PARALLELISM (défaut 4).
    /// </summary>
    private int _tileCounter = 0;
    /// <summary>
    /// Run le batch. Si <paramref name="filterScenarioId"/> != null, ne lance QU'un seul
    /// scénario via le même pipeline (= MarkStarted/Logs/MarkFinished + BatchState sync).
    /// Utilisé par PlayScenarioCommand depuis les cards mosaïque (1 scénario à la fois).
    /// </summary>
    private async Task RunAllRaptureScenariosAsync(string? filterScenarioId = null)
    {
        _tileCounter = 0; // reset à chaque batch — workers font Interlocked.Increment
        // CRITIQUE : reset le log box visible AVANT le batch pour éviter que le driver
        // SmokeRunner WaitForSmokeCompletion matche le __BATCH_END__ d'une session
        // précédente résiduelle (la TextBox garde son contenu entre batches sinon).
        RaptureSmokeLastStdout = "";
        var real = RaptureScenarios
            .Where(s => s.Id != AllScenariosSentinelId)
            .Where(s => filterScenarioId == null || s.Id == filterScenarioId)
            .ToList();
        if (real.Count == 0)
        {
            Log.Info("RunAllRaptureScenarios — aucun scénario réel à itérer");
            return;
        }

        // ── Phase 3b : init BatchState pour driver mosaïque + focus + liste F1 ──
        // 2026-05-27 : Reset IN-PLACE au lieu de Clear+Add. La mosaïque est pré-populée au boot
        // depuis le catalog (PopulateBatchScenariosFromCatalog) et reste persistante. Le batch
        // reset l'état des entries existantes (Queued, no worker, no logs) pour repartir clean,
        // mais préserve les références (les bindings UI ne sont pas détachés).
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var s in real)
            {
                var existing = BatchState.Scenarios.FirstOrDefault(x => x.Id == s.Id);
                if (existing != null)
                {
                    existing.Reset();
                    // Update JsonPath au cas où le manifest a changé.
                    if (existing.JsonPath != (s.ResolvedJsonPath ?? "")) {
                        // JsonPath est init-only public set, on peut le réécrire.
                        // Aucune notif (pas observable), pas critique pour UI.
                    }
                }
                else
                {
                    BatchState.Scenarios.Add(new ScenarioRunState
                    {
                        Id = s.Id,
                        JsonPath = s.ResolvedJsonPath ?? ""
                    });
                }
            }
            BatchState.Phase = BatchPhase.Running;
            // SelectedScenario : si déjà set sur un scénario inclus dans le filtre, le préserver
            // (PlayScenario set explicitement state avant d'appeler ce batch path). Sinon prendre
            // le premier scénario du filtre (pour le batch "All" = premier global, pour batch
            // 1-scénario = celui qu'on lance).
            var currentSel = BatchState.SelectedScenario;
            var selectionStillRelevant = currentSel != null && real.Any(r => r.Id == currentSel.Id);
            if (!selectionStillRelevant)
            {
                BatchState.SelectedScenario = BatchState.Scenarios.FirstOrDefault(x => real.Any(r => r.Id == x.Id));
            }
            BatchState.RefreshCounts();
        });

        // Refresh run stamp (au cas où le caller a réécrit RIG_RUN_STAMP après le start de TV).
        RefreshRunStamp();

        // ── Verdict de confiance (I2) : garantir un RUN-STAMP pour CE batch = nonce qui corrèle chaque
        // fichier résultat worker à ce run précis. Si le caller n'en a pas posé (clic GUI direct), on en
        // génère un. Ce stamp est (a) propagé à chaque worker via psi, (b) utilisé pour relire les
        // fichiers résultat : un fichier d'un run antérieur a un autre stamp => ROUGE (anti-péremption).
        if (string.IsNullOrEmpty(CurrentRunStamp))
        {
            var generated = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            Environment.SetEnvironmentVariable("RIG_RUN_STAMP", generated);
            RefreshRunStamp();
        }
        var batchRunStamp = CurrentRunStamp;
        Log.Info($"   ⓘ run-stamp (verdict nonce) : {batchRunStamp}");

        // Snap timer scan disque : SINGLETON démarré au ctor (InitializeSnapTimer).
        // Plus de create/start par batch — supprimé 2026-05-27 pour fix bug concurrence
        // "tile + Focus PNG noir pendant Play" : PlayScenarioAsync avec AllowConcurrentExecutions=true
        // exécutait deux batches en parallèle, le 1er à finir nullifiait `_snapTimer` et tuait
        // le timer du 2ème (poll-disque arrêté → LastSnapPath jamais mis à jour → PNGs noirs).

        var sb = new System.Text.StringBuilder();
        void Emit(string line)
        {
            Log.Info(line);
            lock (sb) { sb.AppendLine(line); }
            var snap = sb.ToString();
            Application.Current?.Dispatcher.BeginInvoke((Action)(() => RaptureSmokeLastStdout = snap));
        }

        // Parallelism source order: 1) RIG_SMOKE_PARALLELISM env var (override
        // dev), 2) GlobalSettings.Parallelism persistée (UI Paramètres globaux),
        // 3) hard default = 4.
        int parallelism = _globalSettings?.Current?.Parallelism ?? 4;
        var envPar = Environment.GetEnvironmentVariable("RIG_SMOKE_PARALLELISM");
        if (!string.IsNullOrEmpty(envPar) && int.TryParse(envPar, out var ep) && ep > 0) parallelism = ep;
        bool visibleMode = RaptureSmokeVisibleMode;
        string modeLabel = visibleMode ? "legacy-visible" : "selfdrive";

        // ML LOOP Phase 4 — Cache des résultats.
        // Si UseTestResultCache=true (settings), on regarde dans test-cache.json
        // avant chaque scenario : entrée PASS pour la même paire (binaire + JSON) →
        // skip l'exécution, émet le résultat cached.
        bool useCache = _globalSettings?.Current?.UseTestResultCache ?? false;
        var smokeExe = _raptureSmokeProxy.ExePath;
        int cacheHits = 0, cacheMisses = 0;
        // ML LOOP S2.1 — codeHash granulaire : hash les FICHIERS SOURCE des
        // drivers (LegacyDriver / TestViewerDriver / Interaction / Program.cs)
        // plutôt que la binaire SmokeRunner.exe entière. Évite l'invalidation
        // totale du cache sur un rebuild qui ne touche pas réellement le pipeline
        // (ex. modif xUnit only, doc, commentaire isolé). Si l'un des paths
        // n'existe pas (binaires de prod sans repo source à côté), fallback sur
        // la binaire SmokeRunner.exe (comportement Phase 4 d'origine).
        // ⚠ Cette base SERVIRA AUSSI à computer scnHash dans le lookup per-scenario.
        string[] driverHashBase = new[] { smokeExe }; // fallback default
        string codeHashGlobal = "";
        if (useCache)
        {
            try
            {
                var smokeDir = Path.GetDirectoryName(smokeExe) ?? "";
                var srcRoot = Path.GetFullPath(Path.Combine(smokeDir, "..", "..", ".."));
                var driverSources = new[]
                {
                    Path.Combine(srcRoot, "LegacyDriver.cs"),
                    Path.Combine(srcRoot, "TestViewerDriver.cs"),
                    Path.Combine(srcRoot, "Interaction.cs"),
                    Path.Combine(srcRoot, "Program.cs"),
                };
                var existingSources = driverSources.Where(File.Exists).ToArray();
                if (existingSources.Length >= 3)
                {
                    driverHashBase = existingSources;
                    codeHashGlobal = TestResultCacheService.ComputeCodeHash(driverHashBase);
                    Log.Info($"ML cache codeHash from source files: {codeHashGlobal} ({existingSources.Length} drivers hashed)");
                }
                else
                {
                    driverHashBase = new[] { smokeExe };
                    codeHashGlobal = TestResultCacheService.ComputeCodeHash(driverHashBase);
                    Log.Info($"ML cache codeHash from binary (sources not found at {srcRoot}): {codeHashGlobal}");
                }
            }
            catch (Exception ex) { Log.Warn("ML cache: ComputeCodeHash failed: " + ex.Message); }
        }

        Emit($"╔═══ RUN ALL SCENARIOS PARALLEL ({real.Count}) — parallelism={parallelism} mode={modeLabel} applyReal={RaptureSmokeApplyReal}{(useCache ? " cache=ON" : "")} ═══╗");
        if (useCache)
            Emit($"   🗄 Cache activé : codeHash={codeHashGlobal} ({_testCache.CountEntries} entrées en stock)");
        if (visibleMode)
            Emit($"   ⚠ Mode VISIBLE : {real.Count} instances RigClientAccueil VISIBLES vont s'ouvrir en parallèle (~{real.Count * 500}MB RAM).");
        var batchSw = System.Diagnostics.Stopwatch.StartNew();
        // Verdict de confiance : le tuple porte aussi assertRun/assertPass (preuve positive émise au JSON).
        // assertRun = -1 => "non mesuré ce run" (entrée servie par le cache, pas re-vérifiée).
        var results = new System.Collections.Concurrent.ConcurrentBag<(string id, bool ok, TimeSpan dur, string detail, bool cached, int assertRun, int assertPass)>();

        // Per-audience mutex : en mode --apply, les scénarios partageant la même
        // audience doivent s'exécuter séquentiellement (snapshot/restore non thread-safe).
        var audienceLocks = new System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim>();

        using var semaphore = new SemaphoreSlim(parallelism, parallelism);
        var tasks = real.Select(s => Task.Run(async () =>
        {
            if (string.IsNullOrEmpty(s.ResolvedJsonPath) || !File.Exists(s.ResolvedJsonPath))
            {
                Emit($"   ⚠ [{s.Id}] JSON introuvable ({s.ResolvedJsonPath}) — skip");
                results.Add((s.Id, false, TimeSpan.Zero, "JSON introuvable", false, 0, 0));
                return;
            }

            // ML LOOP Phase 4 — Cache lookup AVANT spawn worker.
            // Key = hash de (smokeExe + scenario JSON content) × (scenarioId + mode).
            // ML LOOP S2.3 — mode dans la clé : selfdrive ≠ legacy-visible (chemins
            // d'exécution différents → PASS d'un mode ne doit pas court-circuiter l'autre).
            // applyReal aussi : --apply touche la DB, change la sémantique.
            // On NE cache PAS les FAIL : un retry peut résoudre des flakes.
            if (useCache && !string.IsNullOrEmpty(codeHashGlobal))
            {
                try
                {
                    // ML LOOP S2.1 unifié : scnHash basé sur les MÊMES driverHashBase
                    // que codeHashGlobal + le JSON scénario. Si on a fallback binaire, on
                    // hash la binaire ; sinon les fichiers source.
                    var scnHash = TestResultCacheService.ComputeCodeHash(driverHashBase.Concat(new[] { s.ResolvedJsonPath }).ToArray());
                    var modeAwareKey = $"{s.Id}:{modeLabel}:apply={(RaptureSmokeApplyReal ? 1 : 0)}";
                    var hit = _testCache.Get(scnHash, modeAwareKey);
                    if (hit != null && hit.Status == "PASS")
                    {
                        System.Threading.Interlocked.Increment(ref cacheHits);
                        Emit($"   ⚡ [{s.Id}] CACHE HIT (PASS, {hit.DurationS:F1}s cached) — skip worker");
                        results.Add((s.Id, true, TimeSpan.FromSeconds(hit.DurationS), "cached:" + hit.Detail, true, -1, -1));
                        return;
                    }
                    System.Threading.Interlocked.Increment(ref cacheMisses);
                }
                catch (Exception ex)
                {
                    Log.Warn($"ML cache lookup failed pour [{s.Id}]: " + ex.Message);
                    // Continue normalement, miss silencieux.
                }
            }

            await semaphore.WaitAsync();
            SemaphoreSlim audLock = null;
            if (RaptureSmokeApplyReal && s.DefaultAudienceId.HasValue)
                audLock = audienceLocks.GetOrAdd(s.DefaultAudienceId.Value, _ => new SemaphoreSlim(1, 1));
            if (audLock != null) await audLock.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string workerArgs;
                if (visibleMode)
                {
                    workerArgs = $"--legacy-rapture-process --json \"{s.ResolvedJsonPath}\"";
                    if (RaptureSmokeApplyReal) workerArgs += " --apply";
                    if (s.DefaultAudienceId.HasValue) workerArgs += $" --audience-id {s.DefaultAudienceId.Value}";
                    workerArgs += $" --audience-date {s.AudienceDate}";
                    workerArgs += $" --audience-heure {s.AudienceHeure}";
                    // --scenario-id INDISPENSABLE meme en visible : le worker l'utilise pour
                    // nommer son dir self-snap (sinon fallback "(adhoc)" → nom du JSON →
                    // collision quand plusieurs scenarios partagent le meme JSON).
                    workerArgs += $" --scenario-id \"{s.Id}\"";
                    // Assertions structurées non câblées sur le legacy (recap UI uniquement)
                }
                else
                {
                    workerArgs = $"--rapture-selfdrive --json \"{s.ResolvedJsonPath}\"";
                    if (RaptureSmokeApplyReal) workerArgs += " --apply";
                    if (s.DefaultAudienceId.HasValue) workerArgs += $" --audience-id {s.DefaultAudienceId.Value}";
                    if (s.Id == "cas-b-multi-match") workerArgs += " --cas-b-auto-setup";
                    workerArgs += $" --scenario-id \"{s.Id}\"";
                    if (s.ExpectedWarnings.HasValue) workerArgs += $" --expected-warnings {s.ExpectedWarnings.Value}";
                    if (s.EffectiveExpectedDetected.HasValue) workerArgs += $" --expected-detected-modifications {s.EffectiveExpectedDetected.Value}";
                    if (s.EffectiveExpectedApplied.HasValue) workerArgs += $" --expected-applied-modifications {s.EffectiveExpectedApplied.Value}";
                    if (s.ExpectedValidationErrors.HasValue) workerArgs += $" --expected-validation-errors {s.ExpectedValidationErrors.Value}";
                    if (!string.IsNullOrEmpty(s.ExpectedMessageContains)) workerArgs += $" --expected-message-contains \"{s.ExpectedMessageContains}\"";
                    if (!string.IsNullOrEmpty(s.ExpectedCase)) workerArgs += $" --expected-case \"{s.ExpectedCase}\"";
                }

                Emit($"   ▶ [{s.Id}] démarrage worker {modeLabel}");

                var psi = new ProcessStartInfo(smokeExe, workerArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(smokeExe) ?? Environment.CurrentDirectory,
                };
                // Propagate RIG_DRIVER_HEADLESS to each worker. Order of precedence :
                //   1. env var RIG_DRIVER_HEADLESS heritee du process TV (override dev)
                //   2. GlobalSettings.HeadlessMode (UI Parametres globaux)
                //   3. default = true (HDESK separe invisible)
                // "1" = HDESK separe invisible, "0" = desktop utilisateur (visible).
                bool headless = _globalSettings?.Current?.HeadlessMode ?? true;
                var envHl = Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS");
                if (!string.IsNullOrEmpty(envHl)) headless = envHl != "0";
                psi.EnvironmentVariables["RIG_DRIVER_HEADLESS"] = headless ? "1" : "0";
                // Verdict de confiance (I2) : propager le run-stamp du batch au worker → il nomme/co-localise
                // son fichier résultat souverain dessous, et l'orchestrateur le relit par ce même stamp.
                psi.EnvironmentVariables["RIG_RUN_STAMP"] = batchRunStamp;
                // En mode visible (headless=false), distribuer les RIG en mosaïque pour qu'on
                // voie les N workers simultanés à l'écran. RIG_TILE_INDEX = position du worker
                // dans la grille (0-based), RIG_TILE_PARALLELISM = total parallèle (côté grille).
                if (!headless && visibleMode)
                {
                    var idx = System.Threading.Interlocked.Increment(ref _tileCounter) - 1;
                    psi.EnvironmentVariables["RIG_TILE_INDEX"] = idx.ToString();
                    psi.EnvironmentVariables["RIG_TILE_PARALLELISM"] = parallelism.ToString();
                }
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    results.Add((s.Id, false, TimeSpan.Zero, "Process.Start null", false, 0, 0));
                    return;
                }

                // ── Phase 3b : retrouve le ScenarioRunState pour update live (mosaïque + focus + liste) ──
                var liveState = BatchState.Scenarios.FirstOrDefault(x => x.Id == s.Id);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    liveState?.MarkStarted(proc.Id, $"RigSmoke_{proc.Id}", DateTime.UtcNow);
                });

                // Event-driven stdout (BeginOutputReadLine) → accumulator + parser phase + screenshot detect.
                // Replace l'ancien ReadToEndAsync : on a besoin de voir les lignes en flight pour
                // updater Phase live, pas seulement à la fin.
                var stdoutSb = new System.Text.StringBuilder();
                var stderrSb = new System.Text.StringBuilder();
                var lastPhase = ScenarioPhase.Queued;   // #6 : dédup des transitions pour phases.jsonl
                var screenshotRegex = new System.Text.RegularExpressions.Regex(@"📸 Screenshot\s*:\s*(.+\.png)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                proc.OutputDataReceived += (_, ev) =>
                {
                    if (ev.Data == null) return;
                    lock (stdoutSb) stdoutSb.AppendLine(ev.Data);
                    if (liveState != null)
                    {
                        liveState.AppendLog(ev.Data);
                        if (ScenarioPhaseParser.TryParse(ev.Data, out var phase))
                        {
                            if (phase != lastPhase) { ObservabilityFiles.AppendPhase(s.Id, ev.Data, phase.ToString()); lastPhase = phase; }  // #6
                            Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
                            {
                                liveState.Phase = phase;
                                BatchState.RefreshCounts();
                            }));
                        }
                        var m = screenshotRegex.Match(ev.Data);
                        if (m.Success)
                        {
                            var path = m.Groups[1].Value.Trim();
                            Application.Current?.Dispatcher.BeginInvoke((Action)(() => liveState.FailScreenshotPath = path));
                        }
                    }
                };
                proc.ErrorDataReceived += (_, ev) =>
                {
                    if (ev.Data == null) return;
                    lock (stderrSb) stderrSb.AppendLine(ev.Data);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Await event-driven sur Process.Exited (sortie immédiate au exit, pas de polling actif).
                // Plafond de sécurité : 180s. Au-delà, le worker est tué (focus deadlock / freeze probable).
                // Visible mode = même plafond : si ça dépasse 3 min, c'est un blocage à corriger.
                var exitTcs = new TaskCompletionSource<bool>();
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => exitTcs.TrySetResult(true);
                if (proc.HasExited) exitTcs.TrySetResult(true);
                var timeoutTask = Task.Delay(180_000);
                var winner = await Task.WhenAny(exitTcs.Task, timeoutTask);
                bool exited = winner == exitTcs.Task;
                if (!exited) { try { proc.Kill(); } catch { } }
                sw.Stop();

                // Drain les dernières lignes async (CancelOutputRead non requis avec WaitForExit non-bloquant).
                try { proc.WaitForExit(500); } catch { }

                int exitCode = exited ? proc.ExitCode : -1;
                // ── Verdict de confiance (I3, FAIL-CLOSED) : on NE croit PAS l'exit code seul. On lit le
                // fichier résultat SOUVERAIN que le worker a écrit pour CE run-stamp. Absence (clic avalé /
                // crash / jamais lancé) = ROUGE ; run-stamp périmé = ROUGE ; 0 assertion = ROUGE.
                var resultJson = ObservabilityFiles.ReadScenarioResult(s.Id, batchRunStamp);
                var verdict = VerdictTrust.Evaluate(resultJson, batchRunStamp, exitCode);
                bool ok = verdict.Verified;
                string detail = verdict.Detail;
                string stdout;
                lock (stdoutSb) stdout = stdoutSb.ToString();
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        Emit($"      [{s.Id}] {line}");
                }
                ObservabilityFiles.WriteWorkerStdout(s.Id, stdout);   // #5 : log par-scénario co-localisé avec les self-snaps
                results.Add((s.Id, ok, sw.Elapsed, detail, false, verdict.AssertionsRun, verdict.AssertionsPass));
                Emit($"   {(ok ? "✓" : "✗")} [{s.Id}] {(ok ? "PASS" : "FAIL")} en {sw.Elapsed.TotalSeconds:F1}s ({detail})");

                // ── Phase 3b : update BatchState verdict live (drive le coloriage liste + tile) ──
                if (liveState != null)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        liveState.MarkFinished(ok ? Verdict.Pass : Verdict.Fail);
                        BatchState.RefreshCounts();
                    });
                }

                // ML LOOP Phase 4 — Persist le résultat dans le cache.
                // ML LOOP S2.3 — clé inclut mode + applyReal (cf. lookup côté Get).
                if (useCache && !string.IsNullOrEmpty(codeHashGlobal))
                {
                    try
                    {
                        // ML LOOP S2.1 unifié : scnHash basé sur les MÊMES driverHashBase
                    // que codeHashGlobal + le JSON scénario. Si on a fallback binaire, on
                    // hash la binaire ; sinon les fichiers source.
                    var scnHash = TestResultCacheService.ComputeCodeHash(driverHashBase.Concat(new[] { s.ResolvedJsonPath }).ToArray());
                        var modeAwareKey = $"{s.Id}:{modeLabel}:apply={(RaptureSmokeApplyReal ? 1 : 0)}";
                        _testCache.Set(scnHash, modeAwareKey, ok ? "PASS" : "FAIL", sw.Elapsed.TotalSeconds, detail);
                    }
                    catch (Exception ex) { Log.Warn($"ML cache set failed pour [{s.Id}]: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add((s.Id, false, sw.Elapsed, ex.GetType().Name + ": " + ex.Message, false, 0, 0));
                Emit($"   ✗ [{s.Id}] EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                audLock?.Release();
                semaphore.Release();
            }
        }));

        await Task.WhenAll(tasks);
        foreach (var lk in audienceLocks.Values) lk.Dispose();
        batchSw.Stop();

        // ── Phase 3b : end of batch → set Phase=Done + refresh counts ──
        // (Snap timer singleton démarré au ctor — pas de Stop ici, voir InitializeSnapTimer.)
        Application.Current?.Dispatcher.Invoke(() =>
        {
            BatchState.Phase = BatchPhase.Done;
            BatchState.RefreshCounts();
        });

        // Phase 4 : footer compteur reflète l'espace consommé par les self-snaps du batch
        // qui vient de finir. Task.Run pour que le scan disque ne tienne pas le thread VM.
        // 2026-05-28 : ajoute aussi auto-cleanup (keepLast=10) à la fin de chaque batch
        // pour éviter l'accumulation indéfinie de PNGs (chaque session TV peut faire 20+ runs).
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            CleanupOldSelfSnaps(keepLast: 10);
            RefreshSnapDiskUsage();
        });

        var ordered = results.OrderBy(r => real.FindIndex(s => s.Id == r.id)).ToList();
        int passCount = ordered.Count(r => r.ok);
        int failCount = ordered.Count(r => !r.ok);
        Emit($"╠═══ RECAP ALL SCENARIOS — {passCount} PASS / {failCount} FAIL en {batchSw.Elapsed.TotalSeconds:F1}s (parallelism={parallelism}{(useCache ? $", cache hits={cacheHits}/{cacheHits + cacheMisses}" : "")}) ═══╣");
        foreach (var r in ordered)
            Emit($"   {(r.ok ? "✓" : "✗")} {r.id,-40} {r.dur.TotalSeconds,6:F1}s  {(r.cached ? "⚡ " : "  ")}{r.detail}");
        Emit($"──────────────────────────────────────────────────────────────────────");
        Emit($"  ✓ passed  {passCount}");
        Emit($"  ⊘ skipped 0");
        Emit($"  ✗ failed  {failCount}");
        Emit($"  ⏱ {batchSw.ElapsedMilliseconds} ms");
        Emit($"──────────────────────────────────────────────────────────────────────");
        Emit($"╚═══════════════════════════════════════════════════════════════════════╝");
        // Sentinel FICHIER (process-safe, race-free, pas dépendant du log box UIA).
        // Le driver poll ce fichier et compare son timestamp à un baseline.
        try
        {
            var auditDir = Rig.Wpf.Kbis.SmokeRunner.AuditPaths.Root;
            if (!Directory.Exists(auditDir)) Directory.CreateDirectory(auditDir);

            // (1) Compat — fichier txt légacy
            var sentinelPath = Path.Combine(auditDir, "last-batch-end.txt");
            // Verdict de confiance (I4) : le sentinel porte le run_stamp du batch → un driver peut exiger
            // que la complétion corresponde à CE run (pas un sentinel laissé par un run antérieur). Le
            // verdict AUTORITAIRE reste le JSON (lu par run_stamp en I3) ; le sentinel n'est qu'un signal
            // de fin-de-batch pour le driver.
            var payload = $"{DateTime.UtcNow:o}|passed={passCount}|failed={failCount}|elapsed_ms={batchSw.ElapsedMilliseconds}|run_stamp={batchRunStamp}";
            File.WriteAllText(sentinelPath, payload);
            // Isolation batches concurrents : copie sentinel run-stampée (le fixe « latest » reste pour legacy).
            if (!string.IsNullOrEmpty(batchRunStamp))
                File.WriteAllText(Path.Combine(auditDir,
                    Rig.Wpf.Kbis.SmokeRunner.Observability.BatchSentinelFileName(batchRunStamp)), payload);

            // (2) ML-Phase 1 : JSON structuré pour orchestrateur master agent.
            // Format consommable par Claude Code / scripts pour boucle ML.
            var jsonPath = Path.Combine(auditDir, "last-batch-result.json");
            var scenarios = ordered.Select(r => new
            {
                id = r.id,
                // Verdict de confiance : statut à 3 valeurs. UNVERIFIED = on n'a pas pu PROUVER le résultat
                // (fichier manquant/périmé, 0 assertion) — distinct d'un FAIL (échec prouvé). Jamais vert.
                status = r.ok ? "PASS"
                    : (r.detail != null && r.detail.StartsWith("UNVERIFIED") ? "UNVERIFIED" : "FAIL"),
                verified = r.ok,                  // preuve positive complète obtenue ?
                assertions_run = r.assertRun,     // -1 = non mesuré ce run (servi par cache)
                assertions_pass = r.assertPass,
                duration_s = Math.Round(r.dur.TotalSeconds, 1),
                detail = r.detail,
                cached = r.cached, // ML LOOP Phase 4 : true si résultat servi par cache
            }).ToList();

            // ML-Phase 2 : diff baseline. Lit l'ancien JSON, compare statuts par scenario.id,
            // produit régressions (PASS→FAIL) et fixed (FAIL→PASS).
            // ML-Phase 5 : aussi lit l'ancien screenshot_dhash pour comparer.
            var regressions = new List<string>();
            var fixedNow = new List<string>();
            ulong previousDHash = 0UL;
            try
            {
                if (File.Exists(jsonPath))
                {
                    var oldRaw = File.ReadAllText(jsonPath);
                    using var oldDoc = System.Text.Json.JsonDocument.Parse(oldRaw);
                    if (oldDoc.RootElement.TryGetProperty("screenshot_dhash", out var oldDhEl))
                        previousDHash = ScreenshotDiffService.ParseHash(oldDhEl.GetString() ?? "");
                    if (oldDoc.RootElement.TryGetProperty("scenarios", out var oldScenariosEl))
                    {
                        var oldStatuses = new Dictionary<string, string>();
                        foreach (var el in oldScenariosEl.EnumerateArray())
                        {
                            if (el.TryGetProperty("id", out var idEl) && el.TryGetProperty("status", out var stEl))
                                oldStatuses[idEl.GetString() ?? ""] = stEl.GetString() ?? "";
                        }
                        foreach (var s in scenarios)
                        {
                            if (oldStatuses.TryGetValue(s.id, out var oldSt))
                            {
                                if (oldSt == "PASS" && s.status == "FAIL") regressions.Add(s.id);
                                else if (oldSt == "FAIL" && s.status == "PASS") fixedNow.Add(s.id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Diff baseline a échoué (non bloquant): " + ex.Message); }

            // ML LOOP Phase 5 — Screenshot dHash perceptuel.
            // Capture primary screen → écrit dans Audit\screenshots-loop\batch-recap-{ts}.png.
            // Compute dHash, compare au previousDHash (lu plus haut depuis l'ancien JSON).
            // Hamming distance > 10 = scène visuellement différente → flag.
            string screenshotPath = "";
            string screenshotDHash = "0000000000000000";
            int screenshotDiffVsPrevious = 0;
            try
            {
                var shotDir = Path.Combine(auditDir, "screenshots-loop");
                if (!Directory.Exists(shotDir)) Directory.CreateDirectory(shotDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                screenshotPath = Path.Combine(shotDir, "batch-recap-" + stamp + ".png");
                // ML LOOP S2.2 — ROI sur la fenêtre TestViewer plutôt que plein écran.
                // Plein écran contient wallpaper / taskbar / autres apps → noise sur dHash.
                // GetWindowRect sur le main hwnd cible juste la zone utile (logique batch
                // + recap visible dans la log box). Fallback plein écran si hwnd KO.
                int captureX = 0, captureY = 0, captureW, captureH;
                IntPtr hwnd = IntPtr.Zero;
                try { hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { }
                bool useTvRoi = false;
                if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
                {
                    captureX = r.Left;
                    captureY = r.Top;
                    captureW = Math.Max(1, r.Right - r.Left);
                    captureH = Math.Max(1, r.Bottom - r.Top);
                    useTvRoi = true;
                }
                else
                {
                    captureW = GetSystemMetrics_SmCxScreen();
                    captureH = GetSystemMetrics_SmCyScreen();
                }
                using (var bmp = new System.Drawing.Bitmap(captureW, captureH))
                {
                    using (var g2 = System.Drawing.Graphics.FromImage(bmp))
                        g2.CopyFromScreen(captureX, captureY, 0, 0, new System.Drawing.Size(captureW, captureH));
                    bmp.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                Log.Info($"ML Phase 5 capture: {(useTvRoi ? "TV-ROI" : "fullscreen")} {captureW}x{captureH} @ ({captureX},{captureY})");
                var dh = ScreenshotDiffService.ComputeDHash(screenshotPath);
                screenshotDHash = ScreenshotDiffService.FormatHash(dh);
                if (previousDHash != 0UL)
                {
                    screenshotDiffVsPrevious = ScreenshotDiffService.HammingDistance(previousDHash, dh);
                    Log.Info($"ML Phase 5 dHash: current={screenshotDHash}, previous={previousDHash:x16}, hamming={screenshotDiffVsPrevious}");
                }
                else
                {
                    Log.Info($"ML Phase 5 dHash: current={screenshotDHash}, no previous baseline");
                }
            }
            catch (Exception ex) { Log.Warn("ML Phase 5 dHash screenshot/compute échoué (non bloquant): " + ex.Message); }

            var jsonObj = new
            {
                schema_version = 4, // bump : verdict de confiance (verified + assertions + unverified_count)
                timestamp = DateTime.UtcNow.ToString("o"),
                parallelism,
                mode = modeLabel,
                apply_real = RaptureSmokeApplyReal,
                visible_mode = visibleMode,
                run_stamp = batchRunStamp, // nonce du run (corrélation verdict, anti-péremption)
                duration_ms = batchSw.ElapsedMilliseconds,
                pass_count = passCount,    // = verified == true (preuve positive complète)
                fail_count = failCount,    // inclut les UNVERIFIED (jamais vert)
                // Verdict de confiance : combien de scénarios n'ont PAS pu être prouvés (≠ échec prouvé).
                unverified_count = ordered.Count(r => !r.ok && r.detail != null && r.detail.StartsWith("UNVERIFIED")),
                // ML LOOP Phase 4 — stats cache
                cache_enabled = useCache,
                cache_hits = cacheHits,
                cache_misses = cacheMisses,
                code_hash = codeHashGlobal,
                // ML LOOP Phase 5 — perceptual hash de l'état visuel final du batch.
                // Permet de détecter une popup imprévue ou un changement UI invisible
                // dans les logs (cf. bug "dropdown autocomplete bloquant" du 2026-05-22).
                screenshot_dhash = screenshotDHash,
                screenshot_path = screenshotPath,
                screenshot_diff_vs_previous = screenshotDiffVsPrevious,
                regressions, // PASS → FAIL depuis dernier batch (alerte rouge)
                fixed_now = fixedNow, // FAIL → PASS depuis dernier batch (progrès)
                scenarios,
            };
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var jsonText = System.Text.Json.JsonSerializer.Serialize(jsonObj, jsonOpts);
            File.WriteAllText(jsonPath, jsonText);
            // Isolation batches concurrents : copie JSON verdict run-stampée, immune au clobber d'un batch
            // concurrent. Un check corrélé (-ExpectedRunStamp) lit CETTE copie ; le fixe reste le « latest »
            // pour ml-loop / diff / RunHistory.
            if (!string.IsNullOrEmpty(batchRunStamp))
                File.WriteAllText(Path.Combine(auditDir,
                    Rig.Wpf.Kbis.SmokeRunner.Observability.BatchResultFileName(batchRunStamp)), jsonText);

            Log.Info($"Batch result JSON écrit : {jsonPath} → {passCount} PASS / {failCount} FAIL, {regressions.Count} régression(s), {fixedNow.Count} fixed");
        }
        catch (Exception ex) { Log.Error("Sentinel batch-end / JSON écrit échoué", ex); }
    }

    /// <summary>
    /// Enabled si on a un JSON (override OU scenario sélectionné fournit un path
    /// résolu valide) et que rien ne tourne déjà. Cas spécial : sentinel "All scenarios"
    /// (au moins 1 scénario réel dans le catalogue).
    /// </summary>
    private bool CanRunRaptureScenarioE2E()
    {
        if (IsRaptureSmokeRunning || !RaptureSmokeExeExists) return false;
        if (IsAllScenariosSelected)
            return RaptureScenarios.Any(s => s.Id != AllScenariosSentinelId && !s.IsBroken);
        var path = !string.IsNullOrEmpty(RaptureScenarioJsonPathOverride)
            ? RaptureScenarioJsonPathOverride
            : SelectedRaptureJsonFixture;
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    /// <summary>Stop : tue l'arbre de process du run Rapture en cours (SmokeRunner + RigClientAccueil + diag).</summary>
    [RelayCommand(CanExecute = nameof(CanControlRaptureSmoke))]
    private void StopRaptureSmoke()
    {
        Log.Info("Stop Rapture smoke demandé");
        _raptureSmokeProxy.Stop();
    }

    /// <summary>
    /// Reset DB : panic restore SQL pour partir d'une clean slate. Restaure les
    /// APPEL_AFFAIRE cols modifiés par smoke (depuis l'audit), DELETE les
    /// NOTE_PROCEDURE RAPTURE_* sur ces instances, DELETE les AUDIT rows ciblées
    /// et les Cas C audiences leftover (date >= 2027).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResetSmokeDb))]
    private async Task ResetSmokeDbAsync()
    {
        Log.Info("Reset Smoke DB demandé (--reset-smoke-db)");
        var savedFixed = _raptureSmokeProxy.FixedArgs;
        _raptureSmokeProxy.FixedArgs = "--reset-smoke-db";
        _raptureSmokeProxy.ExtraArgs = "";
        try
        {
            await _raptureSmokeProxy.RunAsync(enableUi: false);
            DumpRaptureSmokeRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("ResetSmokeDb threw", ex);
            ShowError("Reset Smoke DB", ex);
        }
        finally
        {
            _raptureSmokeProxy.FixedArgs = savedFixed;
        }
    }

    private bool CanResetSmokeDb() => !IsRaptureSmokeRunning && RaptureSmokeExeExists;

    /// <summary>Pause/Reprise : gèle/dégèle instantanément l'arbre de process en cours.</summary>
    [RelayCommand(CanExecute = nameof(CanControlRaptureSmoke))]
    private void TogglePauseRaptureSmoke()
    {
        if (_raptureSmokeProxy.IsPaused) { Log.Info("Reprise Rapture smoke"); _raptureSmokeProxy.Resume(); }
        else { Log.Info("Pause Rapture smoke"); _raptureSmokeProxy.Pause(); }
    }

    private bool CanControlRaptureSmoke() => IsRaptureSmokeRunning;

    /// <summary>Libellé du bouton pause/reprise (flip selon l'état gelé).</summary>
    public string RaptureSmokePauseLabel => _raptureSmokeProxy.IsPaused ? "▶  Reprendre" : "⏸  Pause";

    partial void OnSelectedRaptureJsonFixtureChanged(string? value)
    {
        RunRaptureSmokeCommand.NotifyCanExecuteChanged();
        RunRaptureDiagCommand.NotifyCanExecuteChanged();
        RunRaptureProcessE2ECommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Quand l'utilisateur choisit un scénario dans le combo : popule auto les 2
    /// champs édit "Fichier" (JSON path) et "Audience" (AUDNC_ID). L'utilisateur
    /// peut ensuite override ces champs si nécessaire (test variant local).
    /// </summary>
    partial void OnSelectedRaptureScenarioChanged(RaptureScenario? value)
    {
        if (value is null)
        {
            RaptureScenarioJsonPathOverride = "";
            RaptureScenarioAudienceIdOverride = "";
        }
        else
        {
            RaptureScenarioJsonPathOverride = value.ResolvedJsonPath ?? "";
            RaptureScenarioAudienceIdOverride = value.DefaultAudienceId?.ToString() ?? "";
        }
        RunRaptureProcessE2ECommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Quand l'utilisateur change le dossier scenarios : recharge le manifest.
    /// </summary>
    partial void OnRaptureScenariosFolderChanged(string value)
    {
        ReloadRaptureScenarios();
    }

    partial void OnRaptureScenarioJsonPathOverrideChanged(string value)
    {
        RunRaptureProcessE2ECommand.NotifyCanExecuteChanged();
    }

    partial void OnRaptureScenarioAudienceIdOverrideChanged(string value)
    {
        RunRaptureProcessE2ECommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// ID du scénario synthétique "All scenarios" placé en tête du combo. Quand
    /// sélectionné + Start E2E cliqué, le VM itère séquentiellement TOUS les
    /// scénarios réels du manifest, screenshot+log + assertions par scénario,
    /// recap final.
    /// </summary>
    public const string AllScenariosSentinelId = "__all__";

    /// <summary>
    /// Recharge le catalogue depuis <see cref="RaptureScenariosFolder"/>. Garde la
    /// sélection courante si possible (match par Id), sinon sélectionne l'entrée
    /// synthétique "All scenarios" placée en tête.
    /// </summary>
    [RelayCommand]
    private void ReloadRaptureScenarios()
    {
        try
        {
            var prevId = SelectedRaptureScenario?.Id;
            RaptureScenarios.Clear();
            var real = RaptureScenarioCatalog.Load(RaptureScenariosFolder).ToList();
            // Synthetic sentinel en tête : "All scenarios". On le crée seulement
            // si on a au moins 1 scénario réel à itérer, sinon il n'a pas de sens.
            if (real.Count > 0)
            {
                RaptureScenarios.Add(new Services.RaptureScenario
                {
                    Id = AllScenariosSentinelId,
                    Name = $"▶ All scenarios ({real.Count})",
                    Description = $"Exécute séquentiellement les {real.Count} scénarios du manifest. " +
                                  "Chaque scénario : RIG launch → login → PROC_RETAUD → import → screenshot + assertions DB/UIA → cleanup. " +
                                  "Recap final avec PASS/FAIL par scénario dans les logs.",
                    JsonFile = "(synthetic)",
                    ResolvedJsonPath = "(batch)", // CanRunRaptureScenarioE2E doit accepter ce sentinel
                    IsBroken = false,
                });
            }
            foreach (var s in real)
                RaptureScenarios.Add(s);
            // Restaure la sélection précédente, sinon prend le sentinel "All scenarios"
            // (1er du combo, demande explicite utilisateur).
            SelectedRaptureScenario =
                RaptureScenarios.FirstOrDefault(s => s.Id == prevId)
                ?? RaptureScenarios.FirstOrDefault();
            Log.Info($"RaptureScenarios reloaded from '{RaptureScenariosFolder}' — {RaptureScenarios.Count} entries (1 sentinel + {real.Count} réels)");
            // Pré-popule la mosaïque depuis le catalog (cards visibles au repos avec Play button).
            PopulateBatchScenariosFromCatalog();
        }
        catch (Exception ex)
        {
            Log.Error("ReloadRaptureScenarios", ex);
        }
    }

    /// <summary>Pré-popule <see cref="BatchRunState.Scenarios"/> depuis le catalog RaptureScenarios.
    /// Skip le sentinel "All scenarios". Si une entry existe déjà pour un Id donné (ex: re-load
    /// catalog en cours de batch), la conserve (préserve son state live). Sinon ajoute en Queued.
    /// Permet à la mosaïque d'avoir des tiles dès le boot avec un Play button par scénario,
    /// même quand aucun batch n'a encore été lancé.</summary>
    private void PopulateBatchScenariosFromCatalog()
    {
        var realScenarios = RaptureScenarios.Where(s => s.Id != AllScenariosSentinelId).ToList();
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var s in realScenarios)
            {
                if (!BatchState.Scenarios.Any(x => x.Id == s.Id))
                {
                    BatchState.Scenarios.Add(new ScenarioRunState
                    {
                        Id = s.Id,
                        JsonPath = s.ResolvedJsonPath ?? ""
                    });
                }
            }
            // Removed scenarios cleanup : si le manifest a perdu un scénario, retire-le.
            var toRemove = BatchState.Scenarios
                .Where(x => !realScenarios.Any(r => r.Id == x.Id))
                .ToList();
            foreach (var r in toRemove) BatchState.Scenarios.Remove(r);
            BatchState.RefreshCounts();
        });
    }

    /// <summary>True si le scénario sélectionné est le sentinel "All scenarios".</summary>
    public bool IsAllScenariosSelected =>
        SelectedRaptureScenario?.Id == AllScenariosSentinelId;

    private void DumpRaptureSmokeRunToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (!string.IsNullOrEmpty(_raptureSmokeProxy.LastFullStdout))
                File.WriteAllText(Path.Combine(dir, $"rapture-smoke-{stamp}.stdout.log"), _raptureSmokeProxy.LastFullStdout!);
            if (!string.IsNullOrEmpty(_raptureSmokeProxy.LastStderr))
                File.WriteAllText(Path.Combine(dir, $"rapture-smoke-{stamp}.stderr.log"), _raptureSmokeProxy.LastStderr!);
            Log.Info($"Rapture smoke finished — exit={_raptureSmokeProxy.LastExitCode}, duration={_raptureSmokeProxy.LastRunDuration?.TotalSeconds:F1}s");
        }
        catch (Exception ex) { Log.Error("DumpRaptureSmokeRunToDisk", ex); }
    }

    private void OnRaptureSmokeLinesChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            RaptureSmokeLastStdout = _raptureSmokeProxy.LastFullStdout;
            UpdateRaptureSmokeSummary();
        }));
    }

    private void OnRaptureSmokeStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsRaptureSmokeRunning));
            OnPropertyChanged(nameof(RaptureSmokePauseLabel));
            RunRaptureSmokeCommand.NotifyCanExecuteChanged();
            RunRaptureDiagCommand.NotifyCanExecuteChanged();
            RunRaptureProcessE2ECommand.NotifyCanExecuteChanged();
            StopRaptureSmokeCommand.NotifyCanExecuteChanged();
            TogglePauseRaptureSmokeCommand.NotifyCanExecuteChanged();
            if (!_raptureSmokeProxy.IsRunning)
            {
                RaptureSmokeLastStdout = _raptureSmokeProxy.LastFullStdout;
                RaptureSmokeLastStderr = _raptureSmokeProxy.LastStderr;
                UpdateRaptureSmokeSummary();
            }
        }));
    }

    private void UpdateRaptureSmokeSummary()
    {
        int pass = 0, fail = 0, skip = 0;
        foreach (var l in _raptureSmokeProxy.Lines)
        {
            switch (l.Outcome)
            {
                case SmokeOutcome.Passed:  pass++; break;
                case SmokeOutcome.Failed:  fail++; break;
                case SmokeOutcome.Skipped: skip++; break;
            }
        }
        var duration = _raptureSmokeProxy.LastRunDuration is { } d ? $" · {d.TotalSeconds:F1}s" : "";
        RaptureSmokeSummary = $"✓ {pass}  ✗ {fail}  ⊘ {skip}{duration}";
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Rapture EXPORT (tab Smoke Export — single button Start E2E)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start E2E (tab Smoke Export) : lance le mode <c>--legacy-rapture-export</c>
    /// du SmokeRunner. Pilote RIG legacy via FlaUI : Login → PROC_PREAUD → sélectionne
    /// une audience → click 'Export JSON Plumitif' → écrit le JSON sur disque dans
    /// Desktop\JsonRapture\ et capture l'état final. Symétrique du Start E2E Import.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRaptureExport))]
    private async Task RunRaptureExportE2EAsync()
    {
        Log.Info("RunRaptureExportE2E clicked");
        try
        {
            await _raptureExportProxy.RunAsync(enableUi: false);
            DumpRaptureExportRunToDisk();
        }
        catch (Exception ex)
        {
            Log.Error("RunRaptureExportE2E threw", ex);
            ShowError("Start E2E Export", ex);
        }
    }

    private bool CanRunRaptureExport() => !IsRaptureExportRunning && RaptureExportExeExists;

    /// <summary>Stop : tue l'arbre de process du run Export en cours.</summary>
    [RelayCommand(CanExecute = nameof(CanControlRaptureExport))]
    private void StopRaptureExport()
    {
        Log.Info("Stop Rapture export demandé");
        _raptureExportProxy.Stop();
    }

    /// <summary>Pause/Reprise du run Export.</summary>
    [RelayCommand(CanExecute = nameof(CanControlRaptureExport))]
    private void TogglePauseRaptureExport()
    {
        if (_raptureExportProxy.IsPaused) { Log.Info("Reprise Rapture export"); _raptureExportProxy.Resume(); }
        else { Log.Info("Pause Rapture export"); _raptureExportProxy.Pause(); }
    }

    private bool CanControlRaptureExport() => IsRaptureExportRunning;

    // ── Rapture EDI Import (tab Import EDI) : lance rapture-edi-import.ps1, streame le log ──
    [RelayCommand(CanExecute = nameof(CanRunRaptureEdiImport))]
    private async Task RunRaptureEdiImportAsync()
    {
        Log.Info("RunRaptureEdiImport clicked");
        try { await _raptureEdiImportProxy.RunAsync(enableUi: false); }
        catch (Exception ex) { Log.Error("RunRaptureEdiImport threw", ex); ShowError("Import EDI", ex); }
    }
    private bool CanRunRaptureEdiImport() => !IsRaptureEdiImportRunning;

    [RelayCommand(CanExecute = nameof(CanStopRaptureEdiImport))]
    private void StopRaptureEdiImport()
    {
        Log.Info("Stop Import EDI demande");
        _raptureEdiImportProxy.Stop();
    }
    private bool CanStopRaptureEdiImport() => IsRaptureEdiImportRunning;

    private void OnRaptureEdiImportLinesChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            RaptureEdiImportLastStdout = _raptureEdiImportProxy.LastFullStdout;
        }));
    }

    private void OnRaptureEdiImportStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsRaptureEdiImportRunning));
            RunRaptureEdiImportCommand.NotifyCanExecuteChanged();
            StopRaptureEdiImportCommand.NotifyCanExecuteChanged();
            if (!_raptureEdiImportProxy.IsRunning)
                RaptureEdiImportLastStdout = _raptureEdiImportProxy.LastFullStdout;
        }));
    }

    /// <summary>Libellé pause/reprise pour le tab Export (binding miroir de l'Import).</summary>
    public string RaptureExportPauseLabel => _raptureExportProxy.IsPaused ? "▶  Reprendre" : "⏸  Pause";

    private void DumpRaptureExportRunToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (!string.IsNullOrEmpty(_raptureExportProxy.LastFullStdout))
                File.WriteAllText(Path.Combine(dir, $"rapture-export-{stamp}.stdout.log"), _raptureExportProxy.LastFullStdout!);
            if (!string.IsNullOrEmpty(_raptureExportProxy.LastStderr))
                File.WriteAllText(Path.Combine(dir, $"rapture-export-{stamp}.stderr.log"), _raptureExportProxy.LastStderr!);
            Log.Info($"Rapture export finished — exit={_raptureExportProxy.LastExitCode}, duration={_raptureExportProxy.LastRunDuration?.TotalSeconds:F1}s");
        }
        catch (Exception ex) { Log.Error("DumpRaptureExportRunToDisk", ex); }
    }

    private void OnRaptureExportLinesChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            RaptureExportLastStdout = _raptureExportProxy.LastFullStdout;
            UpdateRaptureExportSummary();
        }));
    }

    private void OnRaptureExportStateChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            OnPropertyChanged(nameof(IsRaptureExportRunning));
            OnPropertyChanged(nameof(RaptureExportPauseLabel));
            RunRaptureExportE2ECommand.NotifyCanExecuteChanged();
            StopRaptureExportCommand.NotifyCanExecuteChanged();
            TogglePauseRaptureExportCommand.NotifyCanExecuteChanged();
            if (!_raptureExportProxy.IsRunning)
            {
                RaptureExportLastStdout = _raptureExportProxy.LastFullStdout;
                RaptureExportLastStderr = _raptureExportProxy.LastStderr;
                UpdateRaptureExportSummary();
            }
        }));
    }

    private void UpdateRaptureExportSummary()
    {
        int pass = 0, fail = 0, skip = 0;
        foreach (var l in _raptureExportProxy.Lines)
        {
            switch (l.Outcome)
            {
                case SmokeOutcome.Passed:  pass++; break;
                case SmokeOutcome.Failed:  fail++; break;
                case SmokeOutcome.Skipped: skip++; break;
            }
        }
        var duration = _raptureExportProxy.LastRunDuration is { } d ? $" · {d.TotalSeconds:F1}s" : "";
        RaptureExportSummary = $"✓ {pass}  ✗ {fail}  ⊘ {skip}{duration}";
        RefreshRaptureExportFileCount();
    }

    /// <summary>
    /// Rafraîchit le compteur <see cref="RaptureExportFileCount"/> à partir des
    /// fichiers <c>smoke-plumitif-*.json</c> présents dans <c>Desktop\JsonRapture</c>.
    /// </summary>
    private void RefreshRaptureExportFileCount()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "JsonRapture");
            if (!Directory.Exists(dir)) { RaptureExportFileCount = 0; return; }
            RaptureExportFileCount = Directory.GetFiles(dir, "smoke-plumitif-*.json").Length;
        }
        catch (Exception ex)
        {
            Log.Error("RefreshRaptureExportFileCount", ex);
            RaptureExportFileCount = 0;
        }
    }

    private void RefreshRenders()
    {
        Renders.Clear();
        foreach (var f in _smokeProxy.ListRenderFiles())
        {
            try { Renders.Add(new RenderArtifactViewModel(f)); }
            catch { /* ignore */ }
        }
    }

    private void ReloadScenarioFiles()
    {
        ScenarioFiles.Clear();
        foreach (var g in _scenarioFiles.List())
        {
            ScenarioFiles.Add(new ScenarioFileViewModel(g));
        }
        // Re-tag les paires + reconstruit les chips.
        TagTransitionPairs(
            ScenarioFiles.Select(s => (key: s.Info.Metadata.ClassName + "." + s.Info.Metadata.MethodName,
                                       backend: s.Info.Metadata.Backend,
                                       ensure: (Action<string>)s.EnsureTag)));
        RebuildChips(ScenarioFilterTags, ScenarioFiles.Select(s => (IReadOnlyList<string>)s.Tags), () => ScenarioFilesView.Refresh());
    }

    private void UpdateSummary()
    {
        int sPass = 0, sFail = 0, sSkip = 0;
        foreach (var vm in SmokeScenarios)
        {
            switch (vm.MatchedLine?.Outcome)
            {
                case SmokeOutcome.Passed:  sPass++; break;
                case SmokeOutcome.Failed:  sFail++; break;
                case SmokeOutcome.Skipped: sSkip++; break;
            }
        }
        int rPass = 0, rFail = 0;
        foreach (var vm in RegressionTests)
        {
            if (vm.LastResult?.Passed == true) rPass++;
            else if (vm.LastResult?.Failed == true) rFail++;
        }

        SmokeSummary      = $"✓ {sPass}  ✗ {sFail}  ⊘ {sSkip}";
        RegressionSummary = $"✓ {rPass}  ✗ {rFail}  · {RegressionTests.Count} tests";
        GlobalSummary     = $"Smoke {SmokeSummary}   ·   xUnit {RegressionSummary}";
    }

    private static void ShowError(string title, Exception ex)
    {
        Application.Current?.Dispatcher.Invoke(() =>
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error));
    }
}

/// <summary>Item de la nav module (KBIS, MANDATAIRE, ...).</summary>
public sealed partial class ModuleNavItem : ObservableObject
{
    public ModuleNavItem(string name, string icon, bool isActive, bool isAvailable)
    {
        Name = name; Icon = icon;
        IsActive = isActive;
        IsAvailable = isAvailable;
    }
    public string Name { get; }
    public string Icon { get; }
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private bool isAvailable;
}
