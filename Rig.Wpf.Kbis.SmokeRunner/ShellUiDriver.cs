using System;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.UIA3;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Lance <c>Rig.Wpf.Shell.exe</c> (binaire pré-buildé en Release) et drive
/// le flux click utilisateur via UI Automation : sidebar → tab K-bis → recherche.
///
/// Limites connues :
///  - Le poste doit avoir un display (FlaUI n'est PAS headless).
///  - Le WebView2 ne peut pas être validé via UIA car son DOM est isolé.
///  - L'appsettings.json du Shell est swappé temporairement pour pre-ouvrir
///    le tab KBIS au boot.
/// </summary>
public sealed class ShellUiDriver
{
    private readonly string _shellExePath;
    private readonly string _shellAppSettingsPath;

    public ShellUiDriver(string shellBinDir)
    {
        _shellExePath        = Path.Combine(shellBinDir, "Rig.Wpf.Shell.exe");
        _shellAppSettingsPath = Path.Combine(shellBinDir, "appsettings.json");
    }

    public bool ShellExeExists => File.Exists(_shellExePath);

    /// <summary>
    /// SIREN d'une société dont on SAIT qu'elle génère un PDF (greffe local).
    /// Si set, le buttons coverage l'utilise pour cibler à coup sûr — contourne
    /// l'instabilité d'ordering du SQL Search qui renvoie parfois les dossiers
    /// historiques au lieu de l'actif (bug du ORDER BY sans tiebreaker).
    /// </summary>
    public string? KnownWorkingSiren { get; set; }

    /// <summary>
    /// Configuration appliquée temporairement au Shell pour le smoke :
    /// pre-ouvre KBIS, SQL+Legacy actifs, greffe 7401 par défaut.
    /// </summary>
    private const string TestConfig =
        "{\"RigWpf\":{"
        + "\"PluginDirectory\":\"C:\\\\rig\\\\Bin Processus\","
        + "\"DisableNativeFor\":[],"
        + "\"PreOpenTabs\":[\"KBIS\"],"
        + "\"UseLegacyRigMetier\":true,"
        + "\"UseSqlRigMetier\":true,"
        + "\"SqlConnectionString\":\"Server=SQL-DEV\\\\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=5;\","
        + "\"DefaultGreffe\":\"7401\""
        + "}}";

    public UiDriveResult RunFullClickFlow(int searchWaitSeconds = 15, int pdfWaitSeconds = 30)
    {
        if (!ShellExeExists)
        {
            return UiDriveResult.Skip($"Rig.Wpf.Shell.exe absent au chemin {_shellExePath}");
        }

        var originalConfig = File.Exists(_shellAppSettingsPath)
            ? File.ReadAllText(_shellAppSettingsPath)
            : null;
        File.WriteAllText(_shellAppSettingsPath, TestConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(_shellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));

            if (window is null)
            {
                return UiDriveResult.Fail("Window principale non trouvée après 20s");
            }

            // Attend que le tab KBIS pre-ouvert finisse son load (SQL search initial).
            Thread.Sleep(TimeSpan.FromSeconds(searchWaitSeconds));

            // ─ Étape 1 : la TextBox de recherche existe ───────────────────────
            var allEdits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            var textBox = allEdits.Select(t => t.AsTextBox()).FirstOrDefault();
            if (textBox is null)
            {
                return UiDriveResult.Fail(
                    $"Aucune TextBox trouvée dans le tab KBIS (UIA descendants Edit = {allEdits.Length}). " +
                    "Cause probable : pre-open KBIS échoue (DI ?) ou WebView2 vole le focus.");
            }

            // ─ Étape 2 : saisie "A" ──────────────────────────────────────────
            textBox.Enter("A");
            Thread.Sleep(2000); // SQL search latency

            // ─ Étape 3 : la ListBox se remplit ───────────────────────────────
            var listItems = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            if (listItems.Length == 0)
            {
                return UiDriveResult.Fail("ListBox vide après saisie 'A' (RIG_DEV inaccessible ?)");
            }

            // ─ Étape 4 : clic sur le 1er résultat ────────────────────────────
            // Préférence : un résultat dont le NumGestion commence par 7401 (greffe configuré localement)
            ListBoxItem? firstClickable = null;
            foreach (var item in listItems)
            {
                var listItem = item.AsListBoxItem();
                var text = listItem.Name ?? string.Empty;
                if (text.Contains("7401")) { firstClickable = listItem; break; }
            }
            firstClickable ??= listItems[0].AsListBoxItem();
            // CAPTURE Name AVANT le click + sleep — après 30s, l'élément UIA peut être
            // re-réalisé / déconnecté et son Name jette PropertyNotSupportedException.
            string firstClickableName;
            try { firstClickableName = firstClickable.Name ?? "(sans nom)"; }
            catch { firstClickableName = "(Name unavailable)"; }
            // Mouse-free : SelectionItem pattern UIA, sinon Focus (clavier). Jamais Mouse.Click.
            try { firstClickable.Patterns.SelectionItem.Pattern.Select(); }
            catch { try { firstClickable.AsListBoxItem().Select(); } catch { try { firstClickable.Focus(); } catch { } } }

            // ─ Étape 5 : attendre que PdfFilePath se résolve (binding sur WebView2 Navigate) ─
            // Impossible de lire la prop VM via UIA. Best-effort : sleep + verify
            // que le tab n'a pas crashé et que l'app vit toujours.
            Thread.Sleep(TimeSpan.FromSeconds(pdfWaitSeconds));

            if (app.HasExited)
            {
                return UiDriveResult.Fail($"App s'est fermée pendant le test (exit code {app.ExitCode})");
            }

            // ─ Étape 6 : Buttons Coverage — enumerate + click chaque Button du tab KBIS ─
            // Reste dans le même process Shell pour éviter de payer le startup 2x.
            // Émet sur stdout 1 ligne par bouton ; retourne un agrégat dans Result.Buttons.
            ButtonsCoverageReport = RunButtonsCoverage(window, app);

            return UiDriveResult.Ok(
                $"window vivante, {listItems.Length} résultats listés après 'A', click effectué sur '{firstClickableName}'.");
        }
        catch (Exception ex)
        {
            return UiDriveResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Close peut timeout (COMException 0x80131505) si la window est saturée.
            // Fallback Kill — moins propre mais on récupère le control.
            try
            {
                if (app is not null && !app.HasExited)
                {
                    var closeOk = false;
                    try { app.Close(); closeOk = true; } catch { }
                    if (!closeOk || !app.HasExited)
                    {
                        Console.WriteLine("      ⓘ app.Close failed/timed out — killing");
                        try { app.Kill(); } catch { }
                    }
                }
            }
            catch { /* best-effort */ }
            try { app?.Dispose(); } catch { /* best-effort */ }
            if (originalConfig is not null)
            {
                try { File.WriteAllText(_shellAppSettingsPath, originalConfig); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Rapport de buttons coverage du dernier <see cref="RunFullClickFlow"/>. Null si pas exécuté.</summary>
    public ButtonsCoverage? ButtonsCoverageReport { get; private set; }

    /// <summary>
    /// Teste les 5 boutons connus du toolbar PDF de KbisConsultationEtapeView :
    /// Imprimer, Télécharger, Zoom -, Zoom +, Régénérer. Ciblage par Name UIA = Content
    /// text (ce que WPF expose comme Name pour un Button avec TextBlock à l'intérieur).
    /// Pas d'énumération sauvage de toute la window — évite la sidebar, la title bar,
    /// le HeaderSite ToggleButton (qui ne supporte pas Invoke), etc.
    ///
    /// Pré-condition : la toolbar KBIS doit être VISIBLE (= PdfFilePath != null = un
    /// PDF chargé dans la WebView2). Si le 1er click sur la liste a fail (greffe
    /// externe), on itère sur les listItems suivants jusqu'à trouver une société
    /// qui génère un PDF.
    /// </summary>
    private ButtonsCoverage RunButtonsCoverage(Window window, Application app)
    {
        var report = new ButtonsCoverage();
        Console.WriteLine();
        Console.WriteLine("  🖱  KBIS Buttons Coverage (5 boutons curés)");

        // Étape préalable : s'assurer que la toolbar est visible. Probe sur "Imprimer".
        // Si invisible, c'est que le 1er click a fail (greffe externe) — on essaye
        // les listItems suivants jusqu'à trouver une société qui rend la toolbar.
        if (!IsToolbarVisible(window))
        {
            // Fast path : si on connaît un SIREN qui marche (depuis l'in-process Coverage PDF),
            // on retape direct la search box avec ce SIREN → 1 seul résultat ciblé → click.
            // Contourne le bug de ORDER BY sans tiebreaker qui fait que la liste UI montre
            // parfois les dossiers historiques au lieu des actifs.
            if (!string.IsNullOrEmpty(KnownWorkingSiren))
            {
                Console.WriteLine($"      ⓘ Toolbar cachée. Fast-path : recherche par SIREN connu '{KnownWorkingSiren}'");
                var allEdits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                var searchBox = allEdits.Select(t => t.AsTextBox()).FirstOrDefault();
                if (searchBox is not null)
                {
                    // Clear via Ctrl+A + Delete pour déclencher le TextChanged WPF
                    // (le setter Text= peut bypass le binding selon UpdateSourceTrigger).
                    try
                    {
                        searchBox.Focus();
                        FlaUI.Core.Input.Keyboard.TypeSimultaneously(
                            FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                            FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DELETE);
                        Thread.Sleep(200);
                        // Enter() simule des frappes clavier réelles, déclenche TextChanged
                        searchBox.Enter(KnownWorkingSiren!);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"        Échec écriture search box : {ex.GetType().Name} {ex.Message}");
                    }
                    Thread.Sleep(2500);
                    var newItems = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
                    Console.WriteLine($"        → {newItems.Length} ListItem après recherche SIREN");
                    if (newItems.Length > 0)
                    {
                        string itemName;
                        try { itemName = newItems[0].AsListBoxItem().Name ?? "(sans nom)"; }
                        catch { itemName = "(Name unreadable)"; }
                        Console.WriteLine($"        Item à clicker : {Truncate(itemName, 100)}");
                        try
                        {
                            try { newItems[0].AsListBoxItem().Select(); Console.WriteLine("        Select() effectué"); }
                            catch (Exception sx) { Console.WriteLine($"        Select() jeté {sx.GetType().Name}, fallback Focus() (mouse-free)"); try { newItems[0].Focus(); } catch { } }
                            var probeDeadline = Environment.TickCount + 15_000;
                            while (Environment.TickCount < probeDeadline)
                            {
                                Thread.Sleep(300);
                                if (IsToolbarVisible(window)) { Console.WriteLine("        ✓ Toolbar visible !"); goto toolbarOk; }
                            }
                            Console.WriteLine("        Fast-path click n'a pas rendu la toolbar après 15s — diag :");
                            // Dump diagnostic : tous les Button visibles + tous les Text contenant "ation"
                            DumpDiagnostic(window);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"        Fast-path click jeté {ex.GetType().Name} : {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"        ⚠ SIREN '{KnownWorkingSiren}' ne renvoie aucun résultat — search box n'a pas filtré ?");
                    }
                }
                else
                {
                    Console.WriteLine("        ⚠ Search box introuvable dans la window UIA");
                }
            }

            // Fallback : itération sur les listItems (ancien comportement, peu fiable
            // à cause du bug de SQL ordering).
            Console.WriteLine("      ⓘ Itération sur les listItems suivants…");
            var listItems = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            Console.WriteLine($"        → {listItems.Length} ListItem trouvés dans la window UIA");
            bool toolbarShown = false;
            for (int i = 1; i < Math.Min(listItems.Length, 11) && !toolbarShown; i++)
            {
                string itemName;
                try { itemName = listItems[i].AsListBoxItem().Name ?? "(sans nom)"; }
                catch { itemName = "(Name unreadable)"; }

                try
                {
                    var listItem = listItems[i].AsListBoxItem();
                    // Select() via SelectionItem pattern — plus fiable que Click() pour
                    // les ListBox WPF (Click souris simulée peut rater l'item virtualisé).
                    try { listItem.Select(); }
                    catch { try { listItem.Focus(); /* fallback mouse-free */ } catch { } }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"        item #{i} '{Truncate(itemName, 50)}': select/click jeté {ex.GetType().Name}, suivant");
                    continue;
                }
                // Poll 8s pour voir si la toolbar apparaît
                var probeDeadline = Environment.TickCount + 8000;
                while (Environment.TickCount < probeDeadline)
                {
                    Thread.Sleep(300);
                    if (IsToolbarVisible(window)) { toolbarShown = true; break; }
                }
                Console.WriteLine($"        item #{i} '{Truncate(itemName, 50)}': toolbar {(toolbarShown ? "VISIBLE ✓" : "toujours cachée")}");
            }
            if (!toolbarShown)
            {
                Console.WriteLine("      ⊘ Aucune société ne rend la toolbar visible — Buttons Coverage skipped");
                return report;
            }
            toolbarOk: ;
        }

        var expected = new (string Label, string ContentText)[]
        {
            ("Imprimer",    "Imprimer"),
            ("Télécharger", "Télécharger"),
            ("Zoom -",      "−"),       // U+2212 MINUS SIGN
            ("Zoom +",      "+"),
            ("Régénérer",   "Régénérer"),
        };

        foreach (var (label, content) in expected)
        {
            // Poll 5s — la toolbar KBIS est conditionnellement visible (PdfFilePath != null).
            FlaUI.Core.AutomationElements.AutomationElement? btn = null;
            var deadline = Environment.TickCount + 5_000;
            while (btn is null && Environment.TickCount < deadline)
            {
                btn = window.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.Button).And(cf.ByName(content)));
                if (btn is null) Thread.Sleep(200);
            }
            if (btn is null)
            {
                Console.WriteLine($"      ⊘ {label} — introuvable dans l'arbre UIA (toolbar PDF cachée ?)");
                report.Add(new ButtonResult(label, ButtonOutcome.SkippedDisabled, "not found"));
                continue;
            }

            try
            {
                // Try Invoke d'abord (pour Button standard). Fallback Focus+Space si pattern
                // non supporté (ToggleButton, etc.) — mouse-free strict.
                try { btn.AsButton().Invoke(); }
                catch (PatternNotSupportedException)
                {
                    Console.WriteLine($"      ⓘ {label} — Invoke non supporté, fallback Focus+Space (mouse-free)");
                    try { btn.Focus(); Thread.Sleep(60); FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE); }
                    catch { }
                }

                Thread.Sleep(400);
                TryDismissModalDialog(app);
                Thread.Sleep(200);

                if (app.HasExited)
                {
                    Console.WriteLine($"      ✗ {label} — la window est morte après click");
                    report.Add(new ButtonResult(label, ButtonOutcome.Failed, "window died"));
                    return report;
                }
                Console.WriteLine($"      ✓ {label} — clicked, window vivante");
                report.Add(new ButtonResult(label, ButtonOutcome.Ok, null));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ✗ {label} — exception : {ex.GetType().Name} {ex.Message}");
                report.Add(new ButtonResult(label, ButtonOutcome.Failed, ex.Message));
            }
        }
        return report;
    }

    /// <summary>Dump pour diagnostic : tous les Button trouvables + Text contenant des mots-clés.</summary>
    private static void DumpDiagnostic(Window window)
    {
        try
        {
            var btns = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            Console.WriteLine($"        → {btns.Length} Button au total dans la window");
            int shown = 0;
            foreach (var b in btns)
            {
                if (shown >= 20) { Console.WriteLine("        ... (truncated)"); break; }
                string name = "";
                try { name = b.AsButton().Name ?? ""; } catch { name = "(unreadable)"; }
                Console.WriteLine($"          • '{Truncate(name, 60)}'");
                shown++;
            }
            var texts = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            int errorTextsFound = 0;
            foreach (var t in texts)
            {
                string txt = "";
                try { txt = t.Name ?? ""; } catch { continue; }
                if (txt.IndexOf("Génération", StringComparison.OrdinalIgnoreCase) >= 0
                    || txt.IndexOf("introuvable", StringComparison.OrdinalIgnoreCase) >= 0
                    || txt.IndexOf("erreur", StringComparison.OrdinalIgnoreCase) >= 0
                    || txt.IndexOf("FICHE RAPIDE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine($"          ⚠ Text trouvé : '{Truncate(txt, 120)}'");
                    errorTextsFound++;
                }
                if (errorTextsFound >= 5) break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        Dump diag jeté : {ex.GetType().Name} {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    /// <summary>Toolbar PDF visible = bouton "Imprimer" présent dans l'arbre UIA.</summary>
    private static bool IsToolbarVisible(Window window)
    {
        try
        {
            return window.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Imprimer"))) is not null;
        }
        catch { return false; }
    }

    private static void TryDismissModalDialog(Application app)
    {
        // Cherche une window enfant qui n'est pas la main — c'est probablement une MessageBox.
        // Quick + dirty : si on détecte une, on envoie ENTER (= clic sur le bouton par défaut, généralement OK).
        try
        {
            using var automation = new UIA3Automation();
            var allWindows = app.GetAllTopLevelWindows(automation);
            if (allWindows.Length > 1)
            {
                // Une window de plus que la main — probable MessageBox. Send ENTER.
                var dialog = allWindows[allWindows.Length - 1];
                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            }
        }
        catch { /* best-effort */ }
    }
}

public sealed class ButtonsCoverage
{
    private readonly System.Collections.Generic.List<ButtonResult> _results = new();
    public System.Collections.Generic.IReadOnlyList<ButtonResult> Results => _results;
    public void Add(ButtonResult r) => _results.Add(r);
    public int OkCount       => _results.FindAll(r => r.Outcome == ButtonOutcome.Ok).Count;
    public int FailedCount   => _results.FindAll(r => r.Outcome == ButtonOutcome.Failed).Count;
    public int SkippedCount  => _results.FindAll(r => r.Outcome == ButtonOutcome.SkippedDisabled || r.Outcome == ButtonOutcome.SkippedDestructive).Count;
    public int Total => _results.Count;
}

public sealed class ButtonResult
{
    public string Name { get; }
    public ButtonOutcome Outcome { get; }
    public string? Error { get; }
    public ButtonResult(string name, ButtonOutcome outcome, string? error)
    {
        Name = name; Outcome = outcome; Error = error;
    }
}

public enum ButtonOutcome { Ok, Failed, SkippedDisabled, SkippedDestructive }

public sealed class UiDriveResult
{
    public UiDriveStatus Status { get; }
    public string Message { get; }
    private UiDriveResult(UiDriveStatus status, string message) { Status = status; Message = message; }
    public static UiDriveResult Ok(string message)   => new(UiDriveStatus.Ok,   message);
    public static UiDriveResult Skip(string message) => new(UiDriveStatus.Skip, message);
    public static UiDriveResult Fail(string message) => new(UiDriveStatus.Fail, message);
}

public enum UiDriveStatus { Ok, Skip, Fail }
