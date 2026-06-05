using System;
using System.Windows;
using Rig.Wpf.Kbis.TestViewer.Services;
using Rig.Wpf.Kbis.TestViewer.ViewModels;

namespace Rig.Wpf.Kbis.TestViewer;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    // Cleanup à la fermeture : tue l'arbre worker (SmokeRunner) + RIG (RigClientAccueil) encore
    // en vie, ce qui libère leurs HDESK isolés. Sans ça, fermer TestViewer EN PLEIN BATCH laissait
    // les workers tourner invisibles (analyse 2026-06-04 : aucun Job Object, aucun handler Closing,
    // aucun CancellationToken). On ne tue QUE les descendants de CE TV (pas TV lui-même, pas un
    // autre agent). Force-kill ferme les handles → Windows détruit les HDESK.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            ProcessTreeControl.KillDescendants(System.Diagnostics.Process.GetCurrentProcess().Id);
            Log.Info("OnClosing: arbre worker/RIG nettoye, HDESK liberes.");
        }
        catch (System.Exception ex) { Log.Info("OnClosing cleanup KO (non bloquant): " + ex.Message); }
        base.OnClosing(e);
    }

    public MainWindow()
    {
        InitializeComponent();
        // WindowStartupLocation=Manual + placement explicite sur l'écran PRIMAIRE.
        // CenterScreen calculait depuis le VirtualScreen incluant les écrans
        // fantômes mémorisés → la fenêtre s'ouvrait hors-écran en X négatif
        // (ex. -3692), invisible ET cause de clics FlaUI parasites dans d'autres
        // apps. SystemParameters.WorkArea = écran primaire uniquement (WPF pur,
        // pas de dépendance WinForms).
        PlaceOnPrimaryScreen();
        Log.Info("MainWindow constructed");

        // ── DI manuel ─────────────────────────────────────────────────────
        var paths = TestingPaths.ResolveDefault();
        Log.Info("Paths resolved: SmokeRunner=" + paths.SmokeRunnerExe);
        Log.Info("Paths resolved: RegressionProject=" + paths.RegressionProjectDir);
        Log.Info("Paths resolved: ScenariosDir=" + paths.RegressionScenariosDir);
        Log.Info("Paths resolved: GoldensDir=" + paths.RegressionGoldensDir);

        var smokeProxy = new SmokeRunnerProxy(paths.SmokeRunnerExe, paths.SmokeRendersDir);
        // 2e proxy : même exe, mais flag --legacy → bascule sur le smoke RigClientAccueil.exe.
        var legacySmokeProxy = new SmokeRunnerProxy(paths.SmokeRunnerExe, paths.SmokeRendersDir)
        {
            FixedArgs = "--legacy",
        };
        // 3e proxy : Smoke Import Rapture (tab Smoke Import, bouton "▶ Start E2E").
        // FixedArgs default = "--rapture-selfdrive" (invisible, self-drive in-process).
        // RunRaptureProcessE2EAsync() bascule sur "--legacy-rapture-process" (visible,
        // ouvre RIG/login/PROC_RETAUD/audience/Import) quand la checkbox "Mode visible"
        // (RaptureSmokeVisibleMode) est cochée.
        var raptureSmokeProxy = new SmokeRunnerProxy(paths.SmokeRunnerExe, paths.SmokeRendersDir)
        {
            FixedArgs = "--rapture-selfdrive",
        };
        // 4e proxy : flag --legacy-rapture-export → Start E2E (tab Smoke Export).
        // Pilote l'UI réelle de RIG legacy : Login → PROC_PREAUD → audience → click
        // 'Export JSON Plumitif' → écrit le JSON sur disque. Tab parallèle au Smoke
        // Import, séparation propre import↔export (1 bouton par tab — cf. décision UX
        // 2026-05-20 : décision import/export se fait via les tabs, pas via boutons).
        var raptureExportProxy = new SmokeRunnerProxy(paths.SmokeRunnerExe, paths.SmokeRendersDir)
        {
            FixedArgs = "--legacy-rapture-export",
        };
        var regression = new RegressionRunner(paths);
        var regressionCatalog = new RegressionCatalog(paths);
        var scenarioFiles = new ScenarioFileService(paths);
        var sourceExtractor = new TestSourceExtractor(paths);

        // Module Rapture : 2ème projet xUnit (net48, importé via merge rapture-import).
        // Mêmes services, instanciés sur le path Source/Wpf/Rig.Rapture.Tests.
        Log.Info("Paths resolved: RaptureRegressionProject=" + paths.RaptureRegressionProjectDir);
        var raptureRegression = new RegressionRunner(paths.RaptureRegressionProjectDir);
        var raptureRegressionCatalog = new RegressionCatalog(
            paths.RaptureRegressionProjectDir,
            System.IO.Path.Combine(paths.RaptureRegressionProjectDir, "Import")); // scan summaries dans Import/

        // Settings globaux persistés (JSON sous %LOCALAPPDATA%\rig-wpf-kbis\global-settings.json).
        // Premier setting livré : Parallelism (1-32 instances RIG simultanées).
        var globalSettings = new GlobalSettingsService();
        Log.Info($"GlobalSettings loaded: parallelism={globalSettings.Current.Parallelism} screenshotsLoop={globalSettings.Current.ScreenshotsLoopEnabled} useCache={globalSettings.Current.UseTestResultCache}");

        // ML LOOP Phase 4 — Cache des résultats de tests E2E. Stocké à côté de
        // global-settings.json sous %LOCALAPPDATA%\rig-wpf-kbis\test-cache.json.
        var testCache = new TestResultCacheService();
        var (cacheTotal, cachePass, cacheFail) = testCache.Stats();
        Log.Info($"TestResultCache loaded: {cacheTotal} entries ({cachePass} PASS / {cacheFail} FAIL)");

        // ML LOOP S4.4 — Smoke test des services au démarrage.
        // Détecte une régression des services Cache/dHash avant que l'utilisateur
        // ne déclenche un batch. Si KO → log ERROR + flag _vm pour bandeau UI.
        RunMlServicesSmokeTest(testCache);

        _vm = new MainWindowViewModel(paths, smokeProxy, legacySmokeProxy, regression, regressionCatalog,
            scenarioFiles, sourceExtractor, raptureRegression, raptureRegressionCatalog, raptureSmokeProxy, raptureExportProxy,
            globalSettings, testCache);
        DataContext = _vm;

        Loaded += async (_, _) =>
        {
            Log.Info("Window Loaded — starting InitializeAsync");
            await _vm.InitializeAsync();
            Log.Info("InitializeAsync done — regression tests: " + _vm.RegressionTests.Count);
        };

        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>
    /// Centre la fenêtre sur l'écran PRIMAIRE uniquement (jamais le VirtualScreen,
    /// qui peut contenir des écrans fantômes → coordonnées négatives hors-écran).
    /// Clampe Left/Top pour que la barre de titre reste toujours atteignable.
    /// </summary>
    private void PlaceOnPrimaryScreen()
    {
        var wa = SystemParameters.WorkArea; // écran primaire, unités WPF
        Left = Math.Max(wa.Left, wa.Left + (wa.Width  - Width)  / 2.0);
        Top  = Math.Max(wa.Top,  wa.Top  + (wa.Height - Height) / 2.0);
    }

    private void OnLegacyStdoutChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.CaretIndex = tb.Text?.Length ?? 0;
            tb.ScrollToEnd();
        }
    }

    private void OnSmokeStdoutChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Auto-scroll to bottom — utile pendant le streaming live du smoke run.
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.CaretIndex = tb.Text?.Length ?? 0;
            tb.ScrollToEnd();
        }
    }

    private void OnRaptureSmokeStdoutChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Auto-scroll to bottom pendant le streaming live de la box smoke Rapture.
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.CaretIndex = tb.Text?.Length ?? 0;
            tb.ScrollToEnd();
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.IO.File.Exists(Log.FilePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Log.FilePath,
                    UseShellExecute = true,
                });
            else
                MessageBox.Show("Log file not yet created:\n" + Log.FilePath,
                    "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to open log:\n" + ex.Message,
                "Logs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// ML Loop Phase 3 — appelle l'orchestrateur PowerShell <c>Tools\ml-loop.ps1</c>
    /// qui lit <c>Audit\last-batch-result.json</c>, snapshotte l'historique,
    /// écrit <c>Audit\ml-loop-prompt.md</c> (prompt structuré pour le master agent),
    /// et met à jour <c>Audit\LOOP_STATE.md</c>. Au retour, propose d'ouvrir le
    /// prompt master dans l'éditeur par défaut.
    ///
    /// Codes de sortie du script :
    ///   0 → 100% PASS (rien à faire)
    ///   1 → fails détectés, prompt écrit
    ///   2 → -AutoSpawn lancé mais -Watch absent
    ///   3 → timeout attente nouveau batch
    ///   4 → MaxIterations atteint sans 100%
    ///
    /// Non bloquant : le powershell tourne en process séparé, on attend de
    /// manière async via Task.Run(WaitForExit) pour ne pas geler l'UI WPF.
    /// </summary>
    private async void RunMlLoop_Click(object sender, RoutedEventArgs e)
    {
        const string script = @"C:\Code RIG\Tools\ml-loop.ps1";
        const string promptPath = @"C:\Code RIG\Audit\ml-loop-prompt.md";
        const string loopStatePath = @"C:\Code RIG\Audit\LOOP_STATE.md";
        const string jsonPath = @"C:\Code RIG\Audit\last-batch-result.json";

        if (!System.IO.File.Exists(script))
        {
            MessageBox.Show(
                "Orchestrateur ML Loop introuvable :\n" + script +
                "\n\nLe script est livré avec ce repo dans C:\\Code RIG\\Tools\\ml-loop.ps1.",
                "ML Loop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!System.IO.File.Exists(jsonPath))
        {
            MessageBox.Show(
                "Aucun batch result trouvé :\n" + jsonPath +
                "\n\nLance d'abord un batch via Start E2E + 'All scenarios'.",
                "ML Loop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Log.Info("ML Loop : invocation " + script);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-ExecutionPolicy Bypass -NoProfile -File \"" + script + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                MessageBox.Show("Échec spawn powershell.exe", "ML Loop",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Drain streams en parallèle (sinon deadlock si stdout buffer plein).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            // .NET Fx 4.8 : pas de WaitForExitAsync — on bloque un thread du
            // pool plutôt que l'UI.
            await System.Threading.Tasks.Task.Run(() => proc.WaitForExit());
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var exitCode = proc.ExitCode;

            Log.Info($"ML Loop : exit={exitCode}, stdout={stdout?.Length ?? 0} chars, stderr={stderr?.Length ?? 0} chars");

            string title;
            MessageBoxImage icon;
            switch (exitCode)
            {
                case 0:
                    title = "🎉 ML Loop : 100% PASS";
                    icon = MessageBoxImage.Information;
                    break;
                case 1:
                    title = "⚠ ML Loop : fails détectés — prompt écrit";
                    icon = MessageBoxImage.Warning;
                    break;
                case 4:
                    title = "❌ ML Loop : MaxIterations atteint";
                    icon = MessageBoxImage.Warning;
                    break;
                default:
                    title = "ML Loop : exit=" + exitCode;
                    icon = MessageBoxImage.Warning;
                    break;
            }

            var summary = (stdout ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(stderr))
                summary += "\n\n--- stderr ---\n" + stderr.Trim();

            // MessageBox utilise une police (MS Shell Dlg / Tahoma) sans glyphes
            // pour les box-drawing (U+2500..257F) ni les emojis (>U+2600), qui
            // s'affichent en ♦. On les nettoie pour la lisibilité — le prompt
            // master complet reste accessible via ml-loop-prompt.md.
            // ⚠ Prérequis : le script ml-loop.ps1 force [Console]::OutputEncoding
            // = UTF8 en début de script, sinon les bytes CP850 arrivent ici comme
            // des U+FFFD une fois décodés en UTF-8 (sanitize ne peut plus rien).
            summary = SanitizeForMessageBox(summary);

            // Limite l'affichage pour la lisibilité dans le MessageBox.
            if (summary.Length > 4000)
                summary = summary.Substring(0, 4000) + "\n\n[... tronqué, voir le log de TestViewer pour le détail ...]";

            string question = exitCode == 0
                ? "\n\nAfficher LOOP_STATE.md ?"
                : "\n\nAfficher le prompt master (ml-loop-prompt.md) ?";

            var result = MessageBox.Show(summary + question, title,
                MessageBoxButton.YesNo, icon);

            if (result == MessageBoxResult.Yes)
            {
                var target = exitCode == 0 ? loopStatePath : promptPath;
                if (System.IO.File.Exists(target))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("ML Loop : exception", ex);
            MessageBox.Show("Échec ML Loop :\n" + ex.Message, "ML Loop",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// ML LOOP S4.4 — Smoke test au démarrage. Vérifie que :
    ///   1) TestResultCacheService.Get sur clé inexistante retourne null (pas de throw)
    ///   2) ScreenshotDiffService.ComputeDHash sur un PNG 16x16 synthétique fonctionne
    ///   3) Hamming distance entre 2 hashes identiques = 0
    ///   4) FormatHash / ParseHash round-trip OK
    /// Si l'un fail → log Error pour aider au diagnostic précoce.
    /// </summary>
    private void RunMlServicesSmokeTest(TestResultCacheService cache)
    {
        try
        {
            // (1) Cache Get / Set / Clear silencieux sur clé temp
            var tmpKey = "boot-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var hit = cache.Get("smoke", tmpKey);
            if (hit != null) { Log.Warn("ML smoke: Get sur clé fraîche retourne hit ?!"); }

            // (2) dHash sur PNG synthétique 16x16 (créé in-memory, écrit en temp)
            var tmpPng = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ml-loop-boot-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            try
            {
                using (var bmp = new System.Drawing.Bitmap(16, 16,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.Clear(System.Drawing.Color.Gray);
                    bmp.Save(tmpPng, System.Drawing.Imaging.ImageFormat.Png);
                }
                var dh1 = ScreenshotDiffService.ComputeDHash(tmpPng);
                var dh2 = ScreenshotDiffService.ComputeDHash(tmpPng);
                var hamming = ScreenshotDiffService.HammingDistance(dh1, dh2);
                var hex = ScreenshotDiffService.FormatHash(dh1);
                var parsed = ScreenshotDiffService.ParseHash(hex);
                if (hamming != 0 || parsed != dh1)
                {
                    Log.Error($"ML smoke FAIL: dHash inconsistent — hamming(h,h)={hamming}, round-trip ok={parsed == dh1}", null);
                    return;
                }
            }
            finally
            {
                try { if (System.IO.File.Exists(tmpPng)) System.IO.File.Delete(tmpPng); } catch { }
            }

            Log.Info("ML smoke OK : cache Get/dHash compute/HammingDistance/Format-Parse round-trip");
        }
        catch (Exception ex)
        {
            Log.Error("ML smoke EXCEPTION : services ML LOOP cassés ? " + ex.GetType().Name + ": " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Remplace les caractères que la police MessageBox ne sait pas afficher :
    /// box-drawing (U+2500..U+257F → '-'), symboles divers et emojis
    /// (U+2600+, surrogates UTF-16 D83C..DFFF → supprimés). Le but est juste
    /// que le summary stdout du script PowerShell s'affiche proprement dans
    /// la dialog MessageBox native qui utilise MS Shell Dlg / Tahoma.
    /// </summary>
    private static string SanitizeForMessageBox(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '─' && c <= '╿') sb.Append('-');               // box-drawing
            else if (c >= '▀' && c <= '▟') sb.Append('#');          // block elements
            else if (c >= '☀' && c <= '➿') continue;                // symbols + dingbats → skip
            else if (c >= '\uD83C' && c <= '\uDBFF')                          // emoji high surrogate
            {
                // Skip the surrogate pair (high + low).
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                continue;
            }
            else if (c == ' ') sb.Append(' ');                           // NBSP
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private async void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.PdfPreviewPath)) return;
        if (string.IsNullOrEmpty(_vm.PdfPreviewPath)) return;
        try
        {
            if (PdfWebView.CoreWebView2 is null) await PdfWebView.EnsureCoreWebView2Async();
            PdfWebView.CoreWebView2!.Navigate(new Uri(_vm.PdfPreviewPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebView2 navigation échouée : " + ex.Message,
                "PDF preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
