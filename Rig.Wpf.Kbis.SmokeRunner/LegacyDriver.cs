using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Signal INTERNE (pas un échec) : la demande de réclamation ouverte est DÉJÀ réclamée — le combo
/// « Type de motif » est verrouillé/vide alors qu'un motif est déjà posé, donc on ne peut pas (re)choisir
/// un type de motif, mais le terminal métier « demande en réclamation » EST atteint. Levé par
/// <c>SelectMotifInCombo</c> (cas C1) et converti en succès par <c>ReclamerDcaAvecMotif</c>. Exclusif au
/// scénario dca-reclamation. Voir <see cref="LegacyParsing.IsAlreadyReclamee"/>.
/// </summary>
internal sealed class AlreadyReclameeException : Exception
{
    public string CurrentMotif { get; }
    public AlreadyReclameeException(string currentMotif)
        : base($"Demande déjà réclamée (motif courant '{currentMotif}', combo Type de motif verrouillé/vide).")
        => CurrentMotif = currentMotif;
}

/// <summary>
/// Smoke FlaUI sur le legacy <c>RigClientAccueil.exe</c> (WinForms x86, COM interop).
///
/// Driver à état persistant : on garde <see cref="_app"/> / <see cref="_window"/> entre
/// les étapes pour que Program.cs puisse découper en N <c>TryStep</c> distincts
/// (1 ligne smoke par étape), tout en partageant le process et le UIA automation.
///
/// 3 étapes v1 :
///  1. <see cref="Launch"/> : démarre le binaire + détecte la main window
///  2. <see cref="ClickSeConnecter"/> : clique le bouton login (variantes), attend
///     post-login, vérifie que l'app survit
///  3. (à venir) ouverture d'un PROC_*
///
/// <see cref="Dispose"/> ferme le process proprement (fallback Kill si timeout).
/// </summary>
public sealed class LegacyDriver : IDisposable
{
    public string ExePath { get; }
    public bool ExeExists => File.Exists(ExePath);

    private Application? _app;
    private UIA3Automation? _automation;
    private Window? _window;
    private RigDesktop? _desktop;
    private static bool Headless =>
        (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";

    // Self-snap periodique : voit ce qui se passe dans RIG meme en HDESK isole.
    // Cache le hwnd au demarrage (cote thread attache HDESK), le Timer callback
    // PrintWindow direct (pas d'UIA cross-thread vers AutomationElement HDESK).
    private System.Threading.Timer? _snapTimer;
    private IntPtr _snapHwnd = IntPtr.Zero;
    private string? _snapDir;
    private int _snapSeq;
    private volatile bool _snapStopped;
    private volatile bool _snapReMaximizedOnce;   // self-heal taille : logge le 1er re-maximize par cycle de snap

    /// <summary>#2 — dernier index de self-snap publié (le NNNN du PNG snap-…-NNNN.png).
    /// Lu par Program.Pass/Fail/Skip pour tagger chaque ligne d'event d'un [snap=NNNN]
    /// corrélable au screenshot exact. -1 = aucun self-snap actif. Statique = portée process
    /// (un worker = un scénario), cohérent avec les compteurs statiques _passed/_failed.</summary>
    internal static volatile int LastSnapSeq = -1;

    /// <summary>
    /// Dossiers où RIG dépose un document généré (courrier de réclamation, pièce dématérialisée,
    /// K-bis PDF…). Baseline de détection « un document est apparu » (cf. <see cref="SafeDocList"/>).
    /// Version la PLUS complète (inclut DocDemat) — unifiée pour éviter les copies divergentes.
    /// </summary>
    private static readonly string[] DocOutputDirs =
        { Path.GetTempPath(), @"C:\rig\Temp", @"C:\rig\Cache", @"C:\rig\PDF", @"C:\rig\DocDemat" };

    /// <summary>
    /// ML LOOP S1.2 — Helper retry+ProcessId filter pour les FindDescendants en mode //.
    /// Le mode visible-parallel ouvre N RigClientAccueil simultanés ; UIA peut
    /// jeter ou retourner un élément d'une AUTRE instance (cross-process shortcut).
    /// Pattern : poll up to <paramref name="timeoutSec"/>s, filtre sur _app.ProcessId,
    /// retourne null si rien trouvé.
    /// </summary>
    private AutomationElement? FindButtonWithRetry(string buttonNameSubstring,
                                                    double timeoutSec = 20.0,
                                                    int pollMs = 250)
    {
        if (_window is null) return null;
        int? appPid = null;
        try { appPid = _app?.ProcessId; } catch { }

        var sw = Stopwatch.StartNew();
        int attempts = 0;
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            attempts++;
            try
            {
                var btn = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b =>
                    {
                        try
                        {
                            if (SafeText(() => b.Name).IndexOf(buttonNameSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                                return false;
                            if (appPid.HasValue)
                            {
                                var pid = b.Properties.ProcessId.ValueOrDefault;
                                return pid == 0 || pid == appPid.Value;
                            }
                            return true;
                        }
                        catch { return false; }
                    });
                if (btn != null)
                {
                    if (attempts > 1)
                        Console.WriteLine($"      ✓ Bouton '{buttonNameSubstring}' trouvé au {attempts}e essai ({sw.Elapsed.TotalSeconds:F1}s, pid={appPid})");
                    return btn;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ⚠ FindButtonWithRetry('{buttonNameSubstring}') attempt#{attempts} threw {ex.GetType().Name}: {ex.Message} — retry");
            }
            Thread.Sleep(pollMs);
        }
        Console.WriteLine($"      ✗ Bouton '{buttonNameSubstring}' INTROUVABLE après {attempts} essais en {sw.Elapsed.TotalSeconds:F1}s (pid filter={appPid})");
        return null;
    }

    public LegacyDriver(string exePath, RigDesktop desktop) { ExePath = exePath; _desktop = desktop; }

    /// <summary>Lance le binaire et attend la main window (default 60s).</summary>
    public void Launch(int mainWindowWaitSeconds = 60)
    {
        Console.WriteLine($"      → Launch {ExePath}");

        // WorkingDirectory = dossier de l'exe — sinon RigClientAccueil échoue à charger
        // les plugins via paths relatifs (C:\rig\Bin Processus\…) et les imports COM.
        var workingDir = Path.GetDirectoryName(ExePath) ?? Environment.CurrentDirectory;
        Console.WriteLine($"      → WorkingDirectory={workingDir}");

        // Le desktop est fourni par le constructeur (Program.cs via RigDesktop.RunAttached).
        // Le thread courant est déjà attaché au HDESK (RunAttached a appelé AttachCurrentThread).
        Console.WriteLine($"      → RigDesktop : desktop='{_desktop?.DesktopName ?? "(courant)"}'");

        Process process;
        try
        {
            process = _desktop.LaunchProcess(ExePath, workingDir);
        }
        catch (Exception ex)
        {
            throw new Exception($"RigDesktop.LaunchProcess a jeté {ex.GetType().Name} : {ex.Message}. " +
                                $"Possibles causes : UAC requis, anti-virus qui bloque, exe corrompu, " +
                                $"dépendance native manquante (Vintasoft/IKVM/LibreOffice DLL).", ex);
        }
        _app = Application.Attach(process);
        Console.WriteLine($"      → Process started PID={_app.ProcessId} Name={_app.Name}");
        _automation = new UIA3Automation(); // APRÈS AttachCurrentThread() — obligatoire

        // ⚠ FlaUI.Application.GetMainWindow() appelle Process.WaitForInputIdle() qui
        // timeout à ~1.5s avec un Win32Exception (HRESULT 0x800705B4) sur les apps
        // WinForms lourdes : RigClientAccueil charge plugins COM + IKVM + GAC au boot,
        // il n'atteint "input-idle" qu'après plusieurs secondes. On bypass donc
        // GetMainWindow et on poll directement Process.MainWindowHandle, puis on
        // attache la fenêtre via UIA3.
        Console.WriteLine($"      → Wait main window up to {mainWindowWaitSeconds}s (polling MainWindowHandle + UIA.FromHandle)…");
        var sw = Stopwatch.StartNew();
        IntPtr hwnd = IntPtr.Zero;
        string? lastTitle = null;
        Window? attached = null;
        Exception? lastUiaError = null;
        int hwndFoundLogged = 0;

        // Boucle unique qui poll en parallèle :
        //   1. Process.MainWindowHandle (hwnd dispo)
        //   2. UIA3Automation.FromHandle(hwnd) — peut throw Win32Exception 0x800705B4 si
        //      les peers UIA WinForms ne sont pas encore prêts (cas observé : appelé depuis
        //      le TestViewer WPF, la 1ère tentative timeout, la 2e passe).
        while (sw.Elapsed.TotalSeconds < mainWindowWaitSeconds && attached is null)
        {
            if (_app.HasExited)
                throw new Exception($"Process RigClientAccueil a quitté pendant le boot (exit code {_app.ExitCode}) — crash silencieux (TypeLoadException ? dépendance native manquante ?)");
            try
            {
                var p = Process.GetProcessById(_app.ProcessId);
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    hwnd = p.MainWindowHandle;
                    lastTitle = p.MainWindowTitle;
                    if (hwndFoundLogged == 0)
                    {
                        Console.WriteLine($"      → MainWindowHandle trouvé en {sw.Elapsed.TotalSeconds:F1}s : hwnd=0x{hwnd.ToInt64():X} title='{lastTitle}'");
                        hwndFoundLogged = 1;
                    }
                    try
                    {
                        var el = _automation.FromHandle(hwnd);
                        if (el is not null)
                        {
                            attached = el.AsWindow();
                        }
                    }
                    catch (Exception uiaEx) // ex Win32Exception 0x800705B4 : UIA pas prête
                    {
                        lastUiaError = uiaEx;
                    }
                }
            }
            catch (ArgumentException) { break; /* process disparu */ }
            if (attached is null) Thread.Sleep(250);
        }

        if (attached is null)
        {
            var allRigProcs = Process.GetProcessesByName("RigClientAccueil");
            Console.WriteLine($"      → Échec attache UIA après {sw.Elapsed.TotalSeconds:F1}s (hwnd={(hwnd != IntPtr.Zero ? "0x" + hwnd.ToInt64().ToString("X") : "absent")})");
            if (lastUiaError is not null)
                Console.WriteLine($"      → Dernière erreur UIA : {lastUiaError.GetType().Name} {lastUiaError.Message}");
            Console.WriteLine($"      → Process RigClientAccueil actuels : {allRigProcs.Length}");
            foreach (var p in allRigProcs)
            {
                try { Console.WriteLine($"          PID={p.Id} title='{p.MainWindowTitle}' exited={p.HasExited}"); } catch { }
            }
            if (hwnd == IntPtr.Zero)
                throw new Exception($"Main window non détectée après {mainWindowWaitSeconds}s — process vivant mais sans MainWindowHandle (boot bloqué pré-UI ?)");
            throw new Exception($"UIA3.FromHandle a échoué pendant {mainWindowWaitSeconds}s — hwnd existait mais peers UIA jamais prêts" + (lastUiaError is not null ? $" ({lastUiaError.GetType().Name})" : ""));
        }

        _window = attached;
        Console.WriteLine($"      → Main window UIA OK après {sw.Elapsed.TotalSeconds:F1}s : titre='{SafeText(() => _window.Title)}'");
        Console.WriteLine($"      → [TIME] {DateTime.Now:HH:mm:ss.fff} RIG main window ready PID={_app.ProcessId}");

        // Comportement naturel : on ne force PAS la taille de la fenetre RIG.
        // RIG decide de sa taille (legacy 750x480 par defaut, max si user click maximize).
        // DIAG : log les metrics screen vues depuis ce HDESK pour confirmer 1920x1080.
        if (Headless)
        {
            try
            {
                int cx = GetSystemMetrics(0);  // SM_CXSCREEN
                int cy = GetSystemMetrics(1);  // SM_CYSCREEN
                Console.WriteLine($"      → HDESK screen metrics : {cx}x{cy} (vu depuis le worker attache HDESK)");
            }
            catch { }
        }

        // Mosaïque : si env vars RIG_TILE_INDEX (0-based) et RIG_TILE_PARALLELISM (1-16) set,
        // positionne la fenêtre RIG dans un quadrant de l'écran pour qu'on voie les N
        // workers en simultané sans qu'ils se masquent. SWP_NOACTIVATE = ne vole pas le focus.
        try
        {
            var tileIdxStr = Environment.GetEnvironmentVariable("RIG_TILE_INDEX");
            var tileParStr = Environment.GetEnvironmentVariable("RIG_TILE_PARALLELISM");
            if (int.TryParse(tileIdxStr, out var tileIdx) && int.TryParse(tileParStr, out var tilePar) && tilePar >= 1 && tileIdx >= 0 && tileIdx < tilePar)
            {
                // Grid : 2x2 pour 4, 4x4 pour 16, 3x3 pour 9, etc.
                int gridSide = (int)Math.Ceiling(Math.Sqrt(tilePar));
                int col = tileIdx % gridSide;
                int row = tileIdx / gridSide;
                int screenW = 1920;  // assume primary 1920x1080 — TODO: read SystemParameters
                int screenH = 1080;
                int taskbarH = 50;
                int cellW = screenW / gridSide;
                int cellH = (screenH - taskbarH) / gridSide;
                int x = col * cellW;
                int y = row * cellH;
                SetWindowPos(hwnd, IntPtr.Zero, x, y, cellW, cellH, SWP_NOZORDER | SWP_NOACTIVATE);
                Console.WriteLine($"      → MOSAIC tile [{tileIdx}/{tilePar}] grid {gridSide}x{gridSide} → pos=({x},{y}) size=({cellW}x{cellH})");
            }
        }
        catch (Exception exTile) { Console.WriteLine($"      → (mosaic skip : {exTile.GetType().Name})"); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE = 0xF030;
    // Molette : pour rendre une cellule DataGridView offscreen visible avant de double-cliquer
    // (les coords MSAA sont absolues ecran -> une demande en bas de grille scrollable depasse l'ecran).
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const int WHEEL_DELTA = 120;        // 1 cran de molette
    private const int MK_LBUTTON_FLAG = 0x0001; // wParam low word (boutons enfonces) — aucun pour un scroll
    // Touches clavier pour ouvrir une demande sans coordonnees (la selection MSAA accSelect scrolle la
    // ligne dans la vue ; on l'ouvre ensuite par Entree, ou on navigue Home + Down x index en fallback).
    private const int VK_RETURN_KEY = 0x0D;
    private const int VK_HOME_KEY   = 0x24;
    private const int VK_DOWN_KEY   = 0x28;
    private const int VK_ESCAPE_KEY = 0x1B;   // fallback « Quitter » sur l'écran d'une demande verrouillée
    // accSelect flags (oleacc) : prend le focus + remplace la selection -> un DataGridView WinForms
    // scrolle alors automatiquement la ligne selectionnee dans la zone visible.
    private const int SELFLAG_TAKEFOCUS     = 0x1;
    private const int SELFLAG_TAKESELECTION = 0x2;

    /// <summary>
    /// Cherche le bouton « Se connecter » (variantes : Connexion / OK), click, attend
    /// post-login que la transition FormLogin → FormAccueil soit effective (poll actif
    /// sur le titre de fenêtre + UIA-attachable, pas de sleep blind). Vérifie que le
    /// process est toujours vivant ET qu'une main window est encore accessible.
    /// </summary>
    /// <param name="postLoginTimeoutSeconds">
    /// Timeout MAX (pire cas boot lent). Le poll sort dès que la fenêtre "Console
    /// d'accueil" est UIA-attachable — typiquement &lt; 5 s.
    /// </param>
    public void ClickSeConnecter(int postLoginTimeoutSeconds = 60)
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() doit être appelé avant ClickSeConnecter()");

        // RigButton extends RigPanel (pas Button) → UIA Type=Pane, pas Button.
        // POLL la readiness du bouton (pas one-shot) : sous cold-boot 4× parallèle, Launch()
        // peut accepter la fenêtre AVANT la fin du Load de FormLogin (titre='', btnOk pas encore
        // créé). Root cause 2026-05-29 : 3/4 XEX échouaient ici car la recherche one-shot ratait
        // le bouton qui se rendait ~100ms–2s plus tard. WaitForLoginButton attend la readiness.
        var btn = WaitForLoginButton(20000);
        if (btn is null)
        {
            Console.WriteLine("      → btnOk introuvable après 20s de poll, dump des descendants pour diag :");
            DumpDescendants(_window!, maxDepth: 4);
            throw new Exception("Bouton 'btnOk' / 'Se connecter' introuvable dans FormLogin après 20s (cf. dump ci-dessus)");
        }

        Console.WriteLine($"      → Bouton login trouvé : AutomationId='{SafeText(() => btn.AutomationId)}' Name='{SafeText(() => btn.Name)}' Type={btn.ControlType}");
        // Snapshot du titre pré-click (typiquement "Connection à la base de données Rig")
        // pour détecter la transition vers "Console d'accueil de RIG (…)".
        var preLoginTitle = SafeText(() => _window.Title);
        // Snapshot du HANDLE de la main window pré-click : sur ce build RIG, le titre de la Console
        // d'accueil peut être VIDE (= pré-login) → la détection par titre SEUL donne un faux timeout
        // alors que la Console est bien affichée (prouvé par les self-snaps du run 20260616-132559).
        // Le changement de HANDLE (FormLogin fermée → FormAccueil promue main window) est title-indépendant.
        IntPtr preLoginHwnd = IntPtr.Zero;
        try { preLoginHwnd = Process.GetProcessById(_app.ProcessId).MainWindowHandle; } catch { }

        Interaction.Click(btn);

        // Poll actif : on attend que la main window du process change de titre (FormLogin
        // se ferme → FormAccueil devient main) ET soit UIA-attachable. Pas de Sleep blind.
        Console.WriteLine($"      → Wait post-login (poll titre + UIA, max {postLoginTimeoutSeconds}s) …");
        var sw = Stopwatch.StartNew();
        Window? postWin = null;
        Exception? lastUiaErr = null;
        string lastTitle = preLoginTitle;
        while (sw.Elapsed.TotalSeconds < postLoginTimeoutSeconds && postWin is null)
        {
            if (_app.HasExited)
                throw new Exception($"App fermée après click 'Se connecter' (exit code {_app.ExitCode}) — login refusé ou crash silencieux");

            try
            {
                var p = Process.GetProcessById(_app.ProcessId);
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    Window? candidate = null;
                    try { candidate = _automation!.FromHandle(p.MainWindowHandle).AsWindow(); }
                    catch (Exception ex) { lastUiaErr = ex; }

                    if (candidate is not null)
                    {
                        var title = SafeText(() => candidate.Title);
                        lastTitle = title;
                        // Critère d'acceptance ROBUSTE (title-indépendant) : transition FormLogin → FormAccueil
                        // confirmée par le PREMIER signal positif parmi —
                        //  (1) titre non-vide ≠ pré-login (cas classique) ;
                        //  (2) le HANDLE de la main window a changé (FormLogin fermée → FormAccueil promue) —
                        //      couvre le build où la Console a un titre VIDE (détection par titre seul = faux timeout).
                        bool titleChanged = !string.IsNullOrEmpty(title)
                            && !string.Equals(title, preLoginTitle, StringComparison.Ordinal);
                        bool hwndChanged = preLoginHwnd != IntPtr.Zero
                            && p.MainWindowHandle != IntPtr.Zero
                            && p.MainWindowHandle != preLoginHwnd;
                        if (titleChanged || hwndChanged)
                        {
                            // iter-2 : MainWindowHandle pointe l'overlay UAC (WS_EX_NOREDIRECTIONBITMAP, UIA VIDE),
                            // PAS la console (verifie 2026-06-16 : DumpDescendants vide + OpenProc ne voit aucun btn).
                            // On attache _window a la VRAIE fenetre console RIG (WinForms titree/large), resolue par
                            // ResolveRigConsoleHwnd (meme logique eprouvee que le self-snap). Fallback Zero = on
                            // N'ACCEPTE QUE quand cette console est reellement enumerable, sinon on continue le poll
                            // (accepter l'overlay seul = OpenProc scanne une fenetre vide -> faux echec en cascade).
                            var consoleHwnd = ResolveRigConsoleHwnd(_app.ProcessId, IntPtr.Zero);
                            if (consoleHwnd != IntPtr.Zero && consoleHwnd != preLoginHwnd)
                            {
                                try
                                {
                                    postWin = _automation!.FromHandle(consoleHwnd).AsWindow();
                                    Console.WriteLine($"      → post-login OK via {(titleChanged ? "TITRE" : "HWND-CHANGE")} ; "
                                        + $"console RIG resolue hwnd=0x{consoleHwnd.ToInt64():X} (overlay/MainWindow=0x{p.MainWindowHandle.ToInt64():X})");
                                }
                                catch (Exception ex) { lastUiaErr = ex; }
                            }
                            // sinon : console pas encore enumerable -> poll continue (Thread.Sleep plus bas)
                        }
                    }
                }
            }
            catch (ArgumentException) { break; /* process disparu */ }
            if (postWin is null) Thread.Sleep(150);
        }
        if (postWin is null)
            throw new Exception(
                $"Transition post-login non détectée après {sw.Elapsed.TotalSeconds:F1}s "
                + $"(dernier titre observé = '{lastTitle}', pré-login = '{preLoginTitle}')"
                + (lastUiaErr is not null ? $" — dernière erreur UIA : {lastUiaErr.GetType().Name}" : ""));

        _window = postWin;
        Console.WriteLine($"      → App ouverte post-login en {sw.Elapsed.TotalSeconds:F1}s : titre='{SafeText(() => _window.Title)}'");

        // En HDESK isole : maximize la fenetre Accueil via PostMessage WM_SYSCOMMAND SC_MAXIMIZE.
        // C'est l'equivalent d'un click user sur le bouton maximize -> WinForms set WindowState=Maximized
        // (sticky : persiste a travers les transitions de Form). Plus naturel que SetWindowPos.
        // VERIFIÉ + retry (root cause flake XEX 2026-05-29) : l'ancien fire-and-forget laissait
        // parfois la fenetre a 750x480 (taille legacy par defaut) -> btn3..btn6 du rail HORS
        // de la zone visible -> clics ignores -> scan menu fige -> XEX (sous GÉNÉRAL/btn6) jamais
        // atteint. EnsureWindowMaximized poll la taille reelle et re-poste tant que < seuil.
        if (Headless)
            EnsureWindowMaximized();
    }

    /// <summary>
    /// Garantit que la fenetre RIG est MAXIMISÉE (≈1920x1080) avant toute navigation menu.
    /// Root cause flake XEX intermittent (2026-05-29) : SC_MAXIMIZE etait fire-and-forget ->
    /// la fenetre restait parfois 750x480 -> les boutons rail btn3..btn6 (UIA y=200..588)
    /// tombaient SOUS la zone visible -> PostMessage WM_LBUTTON a des coords hors fenetre ->
    /// clics ignores -> panneau lstSousmenu fige sur le dernier bouton visible (ENDETTEMENT)
    /// -> PROC_XEX (sous GÉNÉRAL) introuvable. VK survivait (btn1 RCS toujours en haut/visible).
    ///
    /// Re-poste SC_MAXIMIZE + poll BoundingRectangle.Width jusqu'a >= minWidth (retry).
    /// </summary>
    private void EnsureWindowMaximized(int minWidth = 1400, int maxMs = 6000)
    {
        if (_window is null) return;
        var sw = Stopwatch.StartNew();
        int attempt = 0;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            double w = 0, h = 0;
            try { var r = _window.BoundingRectangle; w = r.Width; h = r.Height; } catch { }
            if (w >= minWidth)
            {
                if (attempt > 0)
                    Console.WriteLine($"      → Fenetre maximisee ({w:F0}x{h:F0}) apres {attempt} SC_MAXIMIZE en {sw.ElapsedMilliseconds}ms");
                else
                    Console.WriteLine($"      → Fenetre deja maximisee ({w:F0}x{h:F0})");
                return;
            }
            // Pas (encore) maximisee -> (re)poste SC_MAXIMIZE
            attempt++;
            IntPtr hwnd = IntPtr.Zero;
            try { if (_window.Properties.NativeWindowHandle.IsSupported) hwnd = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            if (hwnd != IntPtr.Zero)
            {
                PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero);
                Console.WriteLine($"      → SC_MAXIMIZE tentative {attempt} (fenetre {w:F0}x{h:F0} < {minWidth})");
            }
            Thread.Sleep(400);
        }
        Console.WriteLine($"      → ⚠ Fenetre toujours < {minWidth}px apres {maxMs}ms — scan menu risque d'echouer (btn rail offscreen)");
    }

    /// <summary>
    /// Ouvre le plugin PROC_KBIS depuis la Console d'accueil post-login.
    ///
    /// La Console d'accueil de RIG expose 7 onglets (controls Name=<c>btn1</c>..<c>btn7</c>)
    /// peuplés depuis la table MENU_ONGLET. Cliquer un onglet remplit <c>lstSousmenu</c>,
    /// cliquer un sous-menu remplit <c>lstProcessus</c>. PROC_KBIS apparaît comme ListItem
    /// Name="PROC_KBIS" dans <c>lstProcessus</c>. Double-clic = launch (cf. handler
    /// <c>lstProcessus_DoubleClick</c> dans FORM_ACCUEIL.cs).
    ///
    /// La config (quel onglet, quel sous-menu) dépend du greffe (BDD) — on itère donc
    /// btn1..btn7 et tous les sous-menus jusqu'à trouver PROC_KBIS. Tentative directe
    /// d'abord (si le focus est déjà sur le bon onglet par défaut).
    /// </summary>
    public void OpenProcKbis()
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + ClickSeConnecter() doivent être appelés avant OpenProcKbis()");

        // ⚠ CORRECTION 2026-05-18 : le processus de consultation K-bis est **VK**
        //   ("Visualisation - Extrait RCS"), PAS XXKBIS qui est "Suppression d'un
        //   dossier" (destructeur !). L'ancien matcher fuzzy `contains "kbis"`
        //   attrapait XXKBIS → les scénarios testaient le mauvais processus.
        //
        //   Pièges du libellé/code dans lstProcessus (cf capture greffe 9995) :
        //     VK     → "Visualisation - Extrait RCS"      ← CIBLE
        //     XXKBIS → "Suppression d'un dossier"          ← À EXCLURE
        //     VKREJ  → "Rejets de Kbis XML transmis…"      ← à exclure (contient "VK")
        //     XEX    → "Edition interne d'un Kbis"          ← à exclure (contient "kbis")
        //
        //   Le Name de l'item lstProcessus peut être soit le code seul ("VK"),
        //   soit "code libellé" concaténé. On match donc :
        //     - token code == "VK" exact (1er mot), OU
        //     - libellé contient "visualisation" ET "rcs"
        //   ET on exclut explicitement les codes pièges.
        bool IsKbisName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            var firstToken = LegacyParsing.FirstToken(n);

            // Exclusions dures (codes pièges)
            if (firstToken.Equals("XXKBIS", StringComparison.OrdinalIgnoreCase)) return false;
            if (firstToken.Equals("VKREJ", StringComparison.OrdinalIgnoreCase)) return false;
            if (firstToken.Equals("XEX", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.IndexOf("Suppression", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (n.IndexOf("Rejets", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // Cible : code VK exact OU libellé "Visualisation … RCS"
            if (firstToken.Equals("VK", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.IndexOf("visualisation", StringComparison.OrdinalIgnoreCase) >= 0
             && n.IndexOf("rcs", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 1) Tentative directe par code processus VK — UNIQUEMENT un ListItem
        //    (pas un Text sous-item 'ListViewSubItem-0' qui porte aussi Name='VK'
        //    mais n'est pas lançable au double-clic). TryLaunchKbis vérifie qu'un
        //    tab s'ouvre, sinon retourne false → on enchaîne sur le scan.
        Console.WriteLine("      → Recherche directe du processus VK (ListItem)…");
        var direct = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(li => IsKbisName(SafeText(() => li.Name)));
        if (direct is not null && TryLaunchKbis(direct, source: "direct (ListItem VK)"))
            return;
        Console.WriteLine("      → Direct KO (ou pas de tab ouvert), passage au scan menu…");

        // 2) Itère btn1..btn7 + items lstSousmenu, à chaque sous-menu énumère lstProcessus
        //    et matche tout libellé contenant "kbis" / "k-bis" (le ListView affiche le
        //    libellé métier, pas le code PROC_KBIS).
        Console.WriteLine("      → Pas visible directement, scan onglets × sous-menus × lstProcessus…");
        for (int n = 1; n <= 7; n++)
        {
            var btn = FindByAutomationId("btn" + n);
            if (btn is null)
            {
                Console.WriteLine($"          btn{n} absent — fin onglets disponibles");
                break;
            }
            string btnLabel = SafeText(() => btn.Name);
            Console.WriteLine($"          btn{n} ('{btnLabel}') click…");
            try { Interaction.Click(btn); } catch (Exception ex) { Console.WriteLine($"          click jeté : {ex.GetType().Name}"); continue; }
            Thread.Sleep(500);

            var lstSousmenu = FindByAutomationId("lstSousmenu");
            if (lstSousmenu is null) { Console.WriteLine("          lstSousmenu absent"); continue; }
            var subItems = lstSousmenu.FindAllChildren();
            for (int i = 0; i < subItems.Length; i++)
            {
                string subLabel = SafeText(() => subItems[i].Name);
                try { Interaction.Click(subItems[i]); } catch (Exception ex) { Console.WriteLine($"          sousmenu[{i}] click jeté : {ex.GetType().Name}"); continue; }
                Thread.Sleep(250);

                var lstProc = FindByAutomationId("lstProcessus");
                if (lstProc is null) continue;
                var procItems = lstProc.FindAllChildren();
                foreach (var item in procItems)
                {
                    var nm = SafeText(() => item.Name);
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (IsKbisName(nm))
                    {
                        Console.WriteLine($"          ✓ Match KBIS via btn{n} ('{btnLabel}') > sousmenu[{i}] ('{subLabel}') > item Name='{nm}'");
                        if (TryLaunchKbis(item, source: $"btn{n} > '{subLabel}' > '{nm}'"))
                            return;
                    }
                }
            }
        }

        // 3) Échec total — dump détaillé pour diag.
        Console.WriteLine("      → KBIS introuvable. Dump des items de chaque onglet > sousmenu > lstProcessus :");
        for (int n = 1; n <= 7; n++)
        {
            var btn = FindByAutomationId("btn" + n);
            if (btn is null) break;
            try { Interaction.Click(btn); } catch { continue; }
            Thread.Sleep(300);
            var lstSousmenu = FindByAutomationId("lstSousmenu");
            if (lstSousmenu is null) continue;
            var subItems = lstSousmenu.FindAllChildren();
            Console.WriteLine($"          === btn{n} '{SafeText(() => btn.Name)}' ===");
            for (int i = 0; i < subItems.Length && i < 3; i++) // cap à 3 sous-menus par onglet pour le dump
            {
                try { Interaction.Click(subItems[i]); } catch { continue; }
                Thread.Sleep(200);
                var lstProc = FindByAutomationId("lstProcessus");
                if (lstProc is null) continue;
                var procItems = lstProc.FindAllChildren();
                Console.WriteLine($"            sousmenu[{i}] '{SafeText(() => subItems[i].Name)}' → {procItems.Length} processus :");
                for (int p = 0; p < procItems.Length && p < 6; p++) // cap à 6 items par sous-menu
                    Console.WriteLine($"              - '{SafeText(() => procItems[p].Name)}'");
            }
        }
        throw new Exception("KBIS introuvable dans la Console d'accueil après scan détaillé (cf. dump)");
    }

    /// <summary>
    /// Sélectionne le dernier tab ≠ Accueil du tabControl (ex. le tab ouvert par un processus
    /// qu'on vient de lancer) — utilisé par les scénarios de diag pour que le screenshot final
    /// capture le processus et non le menu d'accueil.
    /// </summary>
    public void SelectLastNonAccueilTab()
    {
        var tabControl = FindByAutomationId("tabControl");
        if (tabControl is null) { Console.WriteLine("      → SelectLastNonAccueilTab : tabControl introuvable"); return; }
        var tabs = tabControl.FindAllChildren();
        var target = tabs.LastOrDefault(t => SafeText(() => t.Name).IndexOf("accueil", StringComparison.OrdinalIgnoreCase) < 0);
        if (target is null) { Console.WriteLine("      → SelectLastNonAccueilTab : aucun tab non-Accueil"); return; }
        Console.WriteLine($"      → Select tab '{SafeText(() => target.Name)}'");
        Interaction.Select(target);
    }

    /// <summary>
    /// Ouvre un processus de la Console d'accueil en scannant onglets btn1..btn7 +
    /// items lstSousmenu, et double-cliquant le 1er item de lstProcessus dont le
    /// Name matche <paramref name="nameMatcher"/>. Pattern générique extrait de
    /// <see cref="OpenProcKbis"/>.
    /// </summary>
    /// <param name="processusLabel">Libellé pour les logs (ex. "PROC_RETAUD")</param>
    /// <param name="nameMatcher">Predicate qui dit si un item Name est le bon processus</param>
    public void OpenProcessus(string processusLabel, Func<string, bool> nameMatcher,
        int? fastPathBtnIndex = null, int? fastPathSousmenuIndex = null)
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException($"Launch() + ClickSeConnecter() doivent être appelés avant Open{processusLabel}()");

        // Si un autre processus est déjà ouvert (ex. XXKBIS), le tabControl pointe dessus
        // et les btn1..btn7 du menu Accueil ne sont plus dans l'arbre UIA. On switch
        // explicitement sur le tab '&Accueil' pour ré-exposer la navigation.
        var tabControl = FindByAutomationId("tabControl");
        if (tabControl is not null)
        {
            var accueilTab = tabControl.FindAllChildren()
                .FirstOrDefault(t => SafeText(() => t.Name).IndexOf("accueil", StringComparison.OrdinalIgnoreCase) >= 0);
            if (accueilTab is not null)
            {
                Console.WriteLine($"      → Click tab '{SafeText(() => accueilTab.Name)}' pour revenir au menu d'accueil");
                Interaction.Select(accueilTab);
                // Poll : on attend que btn1 (premier onglet du menu Accueil) soit
                // accessible — au lieu de Sleep(800) blind.
                WaitForAutomationId("btn1", 800);
            }
        }

        // Defense-in-depth (root cause flake XEX 2026-05-29) : garantit que la fenetre est
        // MAXIMISÉE avant le scan rail. Si elle est restee 750x480 (SC_MAXIMIZE rate au login,
        // ou un PROC l'a de-maximisee), btn3..btn6 sont offscreen → clics ignores → scan fige.
        if (Headless)
            EnsureWindowMaximized();

        // FAST PATH : si on connaît (btn, sousmenu) du processus → click direct
        if (fastPathBtnIndex.HasValue && fastPathSousmenuIndex.HasValue)
        {
            var btnDirect = FindByAutomationId("btn" + fastPathBtnIndex.Value);
            if (btnDirect is not null)
            {
                Console.WriteLine($"      → Fast path {processusLabel} : btn{fastPathBtnIndex.Value} > sousmenu[{fastPathSousmenuIndex.Value}]");
                TryActivateRailTab(btnDirect, $"fast-btn{fastPathBtnIndex.Value}");
                // Poll lstSousmenu présent — timeout extended to 3s for parallel mode.
                var lstSousmenu = WaitForAutomationId("lstSousmenu", 3000);
                if (lstSousmenu is null) Console.WriteLine($"      [DIAG] fast: lstSousmenu NULL après clic btn{fastPathBtnIndex.Value}");
                if (lstSousmenu is not null)
                {
                    var subs = lstSousmenu.FindAllChildren();
                    Console.WriteLine($"      [DIAG] fast: {subs.Length} sous-menus = [{string.Join(" | ", subs.Select(s => SafeText(() => s.Name)))}]");
                    if (fastPathSousmenuIndex.Value < subs.Length)
                    {
                        try { Interaction.Click(subs[fastPathSousmenuIndex.Value]); } catch (Exception ex) { Console.WriteLine($"      [DIAG] fast: clic sousmenu jeté : {ex.Message}"); }
                        // Poll lstProcessus présent (au lieu de Sleep(250) blind)
                        var lstProc = WaitForAutomationId("lstProcessus", 500);
                        if (lstProc is null) Console.WriteLine($"      [DIAG] fast: lstProcessus NULL après clic sousmenu[{fastPathSousmenuIndex.Value}]");
                        if (lstProc is not null)
                        {
                            var fpItems = lstProc.FindAllChildren();
                            Console.WriteLine($"      [DIAG] fast: {fpItems.Length} processus = [{string.Join(" | ", fpItems.Select(p => SafeText(() => p.Name)))}]");
                            foreach (var item in fpItems)
                            {
                                var nm = SafeText(() => item.Name);
                                if (!string.IsNullOrEmpty(nm) && nameMatcher(nm))
                                {
                                    Console.WriteLine($"          ✓ Fast match : Name='{nm}'");
                                    int prevTabCount = SnapshotTabCount();
                                    // Lancement mouse-free : sélection + Entrée sur la
                                    // RigListView (handler lstProcessus_KeyDown). 2× Click
                                    // ne lançait jamais — voir Interaction.ActivateListItem.
                                    Interaction.ActivateListItem(item, lstProc);
                                    WaitForProcessusTabLoaded(prevTabCount, processusLabel, 5000);
                                    return;
                                }
                            }
                        }
                    }
                }
                Console.WriteLine($"      → Fast path raté, fallback scan complet…");
            }
        }

        // ML LOOP fix visible-parallel : scan complet btn1..btn7 avec
        // (a) retry sur chaque FindByAutomationId (mode //: UIA transient KO)
        // (b) PAS de break early sur null btn (continue à n+1)
        // (c) timeouts plus longs pour lstSousmenu/lstProcessus (mode // = UIA lent)
        // (d) whole-scan retry une fois si premier pass vide
        Console.WriteLine($"      → Recherche {processusLabel} : scan onglets × sous-menus × lstProcessus…");
        for (int passNo = 1; passNo <= 2; passNo++)
        {
            if (passNo == 2)
            {
                Console.WriteLine($"      [DIAG] {processusLabel} pass 1 KO — attente 2s puis pass 2…");
                Thread.Sleep(2000);
            }
            bool anyBtnSeen = false;
            for (int n = 1; n <= 7; n++)
            {
                // (a) Retry per-btn find. Mode //: certains btn{n} transientement null.
                var btn = FindByAutomationIdWithRetry("btn" + n, timeoutMs: 2000);
                if (btn is null)
                {
                    // (b) PAS de break — on continue à btn{n+1} (en // un null transient ne
                    // signifie pas que les btn suivants n'existent pas).
                    if (n <= 3) Console.WriteLine($"          btn{n} absent après retry 2s — continue scan");
                    continue;
                }
                anyBtnSeen = true;
                string btnLabel = SafeText(() => btn.Name);
                Console.WriteLine($"      [DIAG] === btn{n} '{btnLabel}' : activate ===");
                // ROOT CAUSE flake XEX (2026-05-29) : après un clic rail (ex. btn2 ENDETTEMENT),
                // RIG ignore parfois les clics suivants (domaine encore en chargement async sous
                // contention parallèle VK) → le panneau lstSousmenu reste FIGÉ sur le bouton
                // précédent (btn3..btn6 lisaient tous les sous-menus endettement → XEX jamais
                // atteint sous GÉNÉRAL). Fix : verify-and-retry — on re-clique le bouton tant que
                // le panneau n'a pas BASCULÉ vers un contenu DIFFÉRENT du bouton précédent.
                string prevSousmenuSig = CurrentListSignature("lstSousmenu");
                AutomationElement[] subItems = System.Array.Empty<AutomationElement>();
                string newSousmenuSig = prevSousmenuSig;
                const int btnMaxAttempts = 3;
                for (int attempt = 1; attempt <= btnMaxAttempts; attempt++)
                {
                    if (!TryActivateRailTab(btn, $"btn{n}")) break;
                    subItems = WaitForListRepopulated("lstSousmenu", prevSousmenuSig, 3000);
                    newSousmenuSig = string.Join("|", subItems.Select(s => SafeText(() => s.Name)));
                    // Bascule réussie = panneau non vide ET différent du bouton précédent.
                    // (2 domaines distincts n'ont jamais les mêmes sous-menus → diff = switch OK.)
                    if (subItems.Length > 0 && newSousmenuSig != prevSousmenuSig) break;
                    if (attempt < btnMaxAttempts)
                        Console.WriteLine($"      [DIAG] btn{n} '{btnLabel}' : panneau FIGÉ (== bouton précédent, clic ignoré par RIG) — re-clic tentative {attempt + 1}/{btnMaxAttempts}");
                    // 800ms : RIG récupère sa réactivité rail après ~1-2s (cf. preuve : btn1
                    // re-switchait OK après la pause inter-passe de 2s). Laisse le domaine finir
                    // son chargement async avant le re-clic.
                    Thread.Sleep(800);
                }
                if (subItems.Length == 0) { Console.WriteLine($"      [DIAG] btn{n} '{btnLabel}' : lstSousmenu vide/NULL après repopulation"); continue; }
                if (newSousmenuSig == prevSousmenuSig)
                    Console.WriteLine($"      [DIAG] btn{n} '{btnLabel}' : ⚠ panneau toujours figé après {btnMaxAttempts} clics — sous-menus possiblement stale");
                Console.WriteLine($"      [DIAG] btn{n} '{btnLabel}' : {subItems.Length} sous-menus = [{string.Join(" | ", subItems.Select(s => SafeText(() => s.Name)))}]");
                for (int i = 0; i < subItems.Length; i++)
                {
                    string subLabel = SafeText(() => subItems[i].Name);
                    // Signature du sous-menu PRÉCÉDENT (contenu actuel de lstProcessus) AVANT clic.
                    string prevProcSig = CurrentListSignature("lstProcessus");
                    if (!TryActivateRailTab(subItems[i], $"sousmenu[{i}]")) { Console.WriteLine($"      [DIAG]   sousmenu[{i}] activation KO — skip"); continue; }
                    // Attendre la REPOPULATION de lstProcessus (même fix : évite le read stale
                    // type '245 processus' / liste du sous-menu précédent).
                    var procItems = WaitForListRepopulated("lstProcessus", prevProcSig, 2000);
                    if (procItems.Length == 0) { Console.WriteLine($"      [DIAG]   sousmenu[{i}] '{subLabel}' : lstProcessus vide/NULL"); continue; }
                    Console.WriteLine($"      [DIAG]   sousmenu[{i}] '{subLabel}' : {procItems.Length} processus = [{string.Join(" | ", procItems.Select(p => SafeText(() => p.Name)))}]");
                    foreach (var item in procItems)
                    {
                        var nm = SafeText(() => item.Name);
                        if (string.IsNullOrEmpty(nm)) continue;
                        if (nameMatcher(nm))
                        {
                            Console.WriteLine($"          ✓ Match {processusLabel} via btn{n} ('{btnLabel}') > sousmenu[{i}] ('{subLabel}') > item Name='{nm}'");
                            int prevTabCount = SnapshotTabCount();
                            // Lancement mouse-free : sélection + Entrée sur la RigListView.
                            // Re-fetch du conteneur lstProcessus (les items viennent de
                            // WaitForListRepopulated qui ne retourne que les enfants).
                            var lstProcContainer = FindByAutomationId("lstProcessus");
                            Interaction.ActivateListItem(item, lstProcContainer);
                            WaitForProcessusTabLoaded(prevTabCount, processusLabel, 5000);
                            return;
                        }
                    }
                }
            }
            if (!anyBtnSeen && passNo == 1)
            {
                Console.WriteLine($"      [DIAG] Pass 1 n'a vu AUCUN btn — Console d'accueil pas rendue ? retry…");
            }
        }
        // Diagnostic screenshot avant throw — voir l'état RIG au moment du fail.
        try { CaptureScreenshot($"open-{processusLabel.ToLowerInvariant()}-failed"); } catch { }
        throw new Exception($"{processusLabel} introuvable dans la Console d'accueil après scan complet btn1..btn7 (2 passes)");
    }

    /// <summary>
    /// Poll actif jusqu'à ce qu'un élément UIA avec l'AutomationId donné soit
    /// présent (remplace les sleeps blind post-click). Retourne null après timeout.
    /// </summary>
    private AutomationElement? WaitForAutomationId(string automationId, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        int iter = 0;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            iter++;
            var el = FindByAutomationId(automationId);
            if (el is not null)
            {
                if (sw.ElapsedMilliseconds > 700)
                    Console.WriteLine($"      [DIAG] WaitForAutomationId('{automationId}') trouvé en {sw.ElapsedMilliseconds}ms ({iter} iter)");
                return el;
            }
            Thread.Sleep(50);
        }
        Console.WriteLine($"      [DIAG] WaitForAutomationId('{automationId}') TIMEOUT {sw.ElapsedMilliseconds}ms (budget {maxMs}ms, {iter} iter) -> null");
        return null;
    }

    /// <summary>Signature courante (noms enfants joints) d'une liste UIA. "" si absente.</summary>
    private string CurrentListSignature(string automationId)
    {
        var el = FindByAutomationId(automationId);
        if (el is null) return "";
        try { return string.Join("|", el.FindAllChildren().Select(s => SafeText(() => s.Name))); }
        catch { return ""; }
    }

    /// <summary>
    /// Attend qu'une liste UIA (lstSousmenu / lstProcessus) soit REPEUPLÉE après un clic de
    /// navigation — pas juste présente. Root cause du flake XEX intermittent (2026-05-29) :
    /// le clic rail est un PostMessage WM_LBUTTON fire-and-forget qui déclenche
    /// _ChargerSousmenu côté RIG, lequel repeuple la liste EN PLACE de façon ASYNCHRONE.
    /// L'ancien WaitForAutomationId ne checkait que l'existence (liste persistante → retour
    /// immédiat) → FindAllChildren lisait les enfants STALE du bouton précédent
    /// (ex. btn6 'GÉNÉRAL' lisait les sous-menus endettement de btn5 → XEX jamais vu).
    ///
    /// Condition de repopulation (condition-based-waiting) :
    ///   - signature (noms enfants joints) STABLE sur 2 lectures consécutives (~120ms), ET
    ///   - soit différente de <paramref name="previousSignature"/> (= contenu du NOUVEAU bouton),
    ///   - soit settle-time écoulé (≥ 700ms) pour le cas où le contenu est légitimement
    ///     identique (re-clic du bouton actif, ou 2 sous-menus au même lstProcessus).
    /// Retourne les enfants, ou la dernière lecture au timeout (best-effort).
    /// </summary>
    private AutomationElement[] WaitForListRepopulated(string automationId, string previousSignature, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        string? lastSig = null;
        AutomationElement[] lastItems = System.Array.Empty<AutomationElement>();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            var el = FindByAutomationId(automationId);
            if (el is not null)
            {
                AutomationElement[] items;
                try { items = el.FindAllChildren(); }
                catch { items = System.Array.Empty<AutomationElement>(); }
                var sig = string.Join("|", items.Select(s => SafeText(() => s.Name)));
                bool stable = sig == lastSig;
                bool changed = sig != previousSignature;
                if (items.Length > 0 && stable && (changed || sw.ElapsedMilliseconds >= 700))
                {
                    if (sw.ElapsedMilliseconds > 300)
                        Console.WriteLine($"      [DIAG] WaitForListRepopulated('{automationId}') stable+{(changed ? "changed" : "settled")} en {sw.ElapsedMilliseconds}ms");
                    return items;
                }
                lastSig = sig;
                lastItems = items;
            }
            Thread.Sleep(120);
        }
        Console.WriteLine($"      [DIAG] WaitForListRepopulated('{automationId}') TIMEOUT {sw.ElapsedMilliseconds}ms — fallback dernière lecture ({lastItems.Length} items)");
        return lastItems;
    }

    /// <summary>
    /// Capture le nombre de TabItems du tabControl principal — utilisé pour
    /// détecter l'ouverture d'un nouveau tab processus (signal de fin de chargement).
    /// </summary>
    private int SnapshotTabCount()
    {
        try
        {
            var tc = FindByAutomationId("tabControl");
            if (tc is null) return -1;
            return tc.FindAllChildren(cf => cf.ByControlType(ControlType.TabItem)).Length;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Poll actif post-DoubleClick processus : attend l'apparition d'un nouveau
    /// TabItem dans le tabControl (= processus chargé). Remplace le Sleep(3500)
    /// blind. Tolérant si le snapshot pré était -1 (UIA momentanément indispo) :
    /// dans ce cas on attend qu'un tabControl existe avec ≥ 2 tabs.
    /// </summary>
    private void WaitForProcessusTabLoaded(int prevTabCount, string processusLabel, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            int now = SnapshotTabCount();
            if (prevTabCount >= 0 && now > prevTabCount)
            {
                Console.WriteLine($"      → {processusLabel} chargé en {sw.Elapsed.TotalSeconds:F1}s (TabControl : {prevTabCount} → {now})");
                return;
            }
            // Fallback : si snapshot pré était KO, on accepte ≥ 2 tabs (Accueil + nouveau)
            if (prevTabCount < 0 && now >= 2)
            {
                Console.WriteLine($"      → {processusLabel} chargé en {sw.Elapsed.TotalSeconds:F1}s ({now} tabs, snapshot pré indispo)");
                return;
            }
            Thread.Sleep(150);
        }
        Console.WriteLine($"      ⚠ Timeout {sw.Elapsed.TotalSeconds:F1}s : nouveau TabItem pas détecté (prev={prevTabCount}, current={SnapshotTabCount()}). Continue quand même.");
    }

    /// <summary>Wrapper : ouvre PROC_RETAUD. Fast path btn3 ('AFFAIRES JUDICIAIRES') > sousmenu[3] ('Audiences et cabinets').</summary>
    public void OpenProcRetaud()
    {
        OpenProcessus("PROC_RETAUD", nm =>
            nm.IndexOf("retaud", StringComparison.OrdinalIgnoreCase) >= 0
         || nm.IndexOf("retour audience", StringComparison.OrdinalIgnoreCase) >= 0
         || nm.IndexOf("retour cabinet", StringComparison.OrdinalIgnoreCase) >= 0,
            fastPathBtnIndex: 3, fastPathSousmenuIndex: 3);
    }

    /// <summary>Wrapper : ouvre PROC_PREAUD. Fast path btn3 ('AFFAIRES JUDICIAIRES') > sousmenu[3] ('Audiences et cabinets').</summary>
    public void OpenProcPreaud()
    {
        OpenProcessus("PROC_PREAUD", nm =>
            nm.IndexOf("preaud", StringComparison.OrdinalIgnoreCase) >= 0
         || nm.IndexOf("plumitif", StringComparison.OrdinalIgnoreCase) >= 0,
            fastPathBtnIndex: 3, fastPathSousmenuIndex: 3);
    }

    /// <summary>
    /// Vérifie qu'un bouton dont le Name matche est présent dans la pageTab active
    /// (typiquement la toolbar tbAutomate après ouverture d'un processus).
    /// Le smoke valide juste l'existence visuelle — il ne click PAS le bouton (qui
    /// requiert souvent une audience chargée). C'est la preuve que le binary post-merge
    /// est bien déployé.
    /// </summary>
    public AutomationElement VerifyButtonPresent(string buttonLabel, params string[] nameSubstrings)
    {
        if (_window is null) throw new InvalidOperationException("_window null");

        // Scan ALL descendants (Button + Pane + autres) — on matche juste par Name substring,
        // évite les PropertyNotSupportedException si certains controls n'ont pas de ControlType
        // ou si certaines branches UIA throw.
        AutomationElement? match = null;
        try
        {
            foreach (var c in _window.FindAllDescendants())
            {
                var nm = SafeText(() => c.Name);
                if (string.IsNullOrEmpty(nm)) continue;
                foreach (var sub in nameSubstrings)
                {
                    if (nm.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                    { match = c; break; }
                }
                if (match is not null) break;
            }
        }
        catch (Exception ex) { Console.WriteLine($"      → Scan jeté ({ex.GetType().Name}), partial dump…"); }
        if (match is null)
        {
            Console.WriteLine($"      → Bouton '{buttonLabel}' introuvable (cherché : {string.Join(", ", nameSubstrings)})");
            Console.WriteLine($"      → Dump des Button + Pane avec Name non vide :");
            int n = 0;
            foreach (var c in _window.FindAllDescendants())
            {
                string ct = "?"; try { ct = c.ControlType.ToString(); } catch { continue; /* skip unsupported */ }
                if (ct != "Button" && ct != "Pane") continue;
                var nm = SafeText(() => c.Name);
                if (string.IsNullOrEmpty(nm) || nm.StartsWith("Veuillez patienter", StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"          [{n++}] {ct} Name='{nm}'");
                if (n > 30) break;
            }
            throw new Exception($"Bouton '{buttonLabel}' introuvable dans la window — preuve que le binary post-merge n'est pas chargé OU que l'UI n'est pas dans la bonne phase.");
        }
        string matchType = "?"; try { matchType = match.ControlType.ToString(); } catch { }
        Console.WriteLine($"      → ✓ Bouton '{buttonLabel}' présent : Type={matchType} AutomationId='{SafeText(() => match.AutomationId)}' Name='{SafeText(() => match.Name)}'");
        return match;
    }

    /// <summary>
    /// ID de la dernière audience créée pendant ce smoke run (via le scénario "Oui,
    /// créer une nouvelle audience"). Sert au cleanup SQL post-run pour ne pas
    /// polluer RIG_DEV. Null si pas de création (refus ou refus orchestrateur).
    /// </summary>
    public int? LastCreatedAudienceId { get; private set; }

    /// <summary>
    /// Compteurs lus sur la dernière FormRaptureImportRecap affichée (4 stat tiles :
    /// erreur bloquante, avertissements, affaires bloquées, modifications détectées).
    /// Null sur les champs non lus. Renseigné par <see cref="ReadRecapCounters"/>
    /// pendant la capture de la recap, et consommé par SmokeRunner Program.cs pour
    /// asserter contre scenario.expectedWarnings / scenario.expectedModifications.
    /// </summary>
    public sealed class RecapCounters
    {
        public int? Errors;
        public int? Warnings;
        public int? Blocked;
        public int? Modifications;
        public override string ToString() =>
            $"err={Errors?.ToString() ?? "?"} warn={Warnings?.ToString() ?? "?"} blocked={Blocked?.ToString() ?? "?"} modif={Modifications?.ToString() ?? "?"}";
    }
    public RecapCounters LastRecapCounters { get; private set; }

    /// <summary>
    /// Texte du dernier DialogBox d'erreur GÉNÉRIQUE capturé (cas ERROR_PARSE / json-malformé : RIG ne
    /// montre PAS de recap mais un DialogBox.Show — scout 2026-07-06 FORM_RETAUD:223-225). Renseigné dans la
    /// branche Refus de la gestion popup AVANT fermeture ; consommé par SmokeRunner pour --expected-error-contains.
    /// </summary>
    public string LastErrorDialogText { get; private set; }

    /// <summary>
    /// Lit les 4 stat tiles de la recap (label "modifications détectées" / "avertissements"
    /// / "erreur bloquante" / "affaires bloquées") + le label de valeur adjacent (chiffre).
    /// Best-effort : retourne null sur les champs qu'on ne trouve pas. Le matching se fait
    /// par texte du label (UIA Name d'un Label WinForms = le texte affiché).
    /// </summary>
    public RecapCounters ReadRecapCounters(AutomationElement recap)
    {
        var c = new RecapCounters();
        if (recap is null) return c;
        try
        {
            // Récupère tous les Text/Pane descendants (Label WinForms = ControlType.Text en UIA)
            var labels = recap.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(e => new { El = e, Name = SafeText(() => e.Name) ?? "", Loc = TryBounds(e) })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .ToList();
            // Trouve les "value tiles" : labels dont le texte = uniquement un nombre (0..N).
            // En face de chaque, un "label tile" texte = "modifications détectées", "avertissements", etc.
            // En WinForms, lblStatXxxValue est à Y=top, lblStatXxxLabel est juste en dessous (même X).
            int? ParseLabelByLabelText(string contains)
            {
                // Trouve le label "label tile" matchant
                var labelTile = labels.FirstOrDefault(x => x.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0
                                                          && !int.TryParse(x.Name.Trim(), out _));
                if (labelTile == null) return null;
                // Trouve le label numérique le plus proche (proche en X, juste au-dessus en Y)
                var valueTile = labels.FirstOrDefault(x =>
                    int.TryParse(x.Name.Trim(), out _)
                    && Math.Abs(x.Loc.X - labelTile.Loc.X) < 100
                    && x.Loc.Y < labelTile.Loc.Y);
                if (valueTile != null && int.TryParse(valueTile.Name.Trim(), out var v)) return v;
                return null;
            }
            c.Errors = ParseLabelByLabelText("erreur bloquante");
            c.Warnings = ParseLabelByLabelText("avertissement");
            c.Blocked = ParseLabelByLabelText("affaires bloquées");
            c.Modifications = ParseLabelByLabelText("modifications détectées");
            Console.WriteLine($"      → Recap counters : {c}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ ReadRecapCounters threw : {ex.GetType().Name}: {ex.Message}");
        }
        LastRecapCounters = c;
        return c;
    }

    private static (int X, int Y) TryBounds(AutomationElement e)
    {
        try { var r = e.BoundingRectangle; return ((int)r.X, (int)r.Y); }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Click 'Importer Rapture' dans PROC_RETAUD → OpenFileDialog → tape le path
    /// du JSON sample → Ouvrir → vérifie qu'un écran récap ou progression apparaît
    /// (preuve que le pipeline d'import a démarré sans crash).
    ///
    /// <paramref name="acceptCreate"/> : si true, accepte la création d'audience
    /// quand le JSON ne matche aucune audience RIG (clic 'Oui' sur "Aucune
    /// audience RIG ne correspond... Créer ?"). L'ID de l'audience créée est
    /// extrait du MessageBox de confirmation et stocké dans
    /// <see cref="LastCreatedAudienceId"/> pour cleanup SQL ultérieur.
    /// Si false (défaut), refuse la création (clic 'Non' → import annulé).
    /// </summary>
    public void ClickImporterRaptureAndOpenJson(string jsonPath, bool acceptCreate = false, bool clickImporter = false)
    {
        if (_window is null) throw new InvalidOperationException("_window null");
        if (!File.Exists(jsonPath)) throw new FileNotFoundException("JSON sample introuvable", jsonPath);
        Console.WriteLine($"      → JSON source : {jsonPath} ({new FileInfo(jsonPath).Length} octets)");

        // 1) Find Importer Rapture button (Toolbar button — AutomationId='IMPORT' standard).
        // ML LOOP S1.2 — Retry pattern : en mode //, l'UIA cache peut être
        // momentanément KO juste après navigation (PhaseChanged async, RIG
        // re-render). On poll jusqu'à 20s avec sleep 250ms avant de throw.
        // UIA FindAllDescendants sur RIG en mode // peut prendre 5-8s par
        // appel (contention), donc 5s ne donne qu'une tentative. 20s = au
        // moins 2-3 vraies tentatives + tampon.
        // Filtre ProcessId pour éviter qu'un FindFirst tombe sur un bouton
        // d'une AUTRE instance RIG en parallèle (cross-process UIA shortcut).
        AutomationElement? btn = null;
        var btnSw = Stopwatch.StartNew();
        int btnAttempts = 0;
        int? appPid = null;
        try { appPid = _app?.ProcessId; } catch { }
        while (btnSw.Elapsed.TotalSeconds < 20 && btn is null)
        {
            btnAttempts++;
            try
            {
                btn = _window.FindFirstDescendant(cf => cf.ByAutomationId("IMPORT"));
                if (btn != null && appPid.HasValue)
                {
                    // Vérif ProcessId : si UIA a sauté sur un autre RigClientAccueil en //,
                    // on rejette et re-cherche.
                    try
                    {
                        var pid = btn.Properties.ProcessId.ValueOrDefault;
                        if (pid != 0 && pid != appPid.Value)
                        {
                            Console.WriteLine($"      ⚠ btn 'IMPORT' trouvé mais pid={pid} ≠ _app.pid={appPid} — reject (cross-process UIA)");
                            btn = null;
                        }
                    }
                    catch { }
                }
                if (btn is null)
                {
                    btn = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(b =>
                        {
                            if (SafeText(() => b.Name).IndexOf("importer rapture", StringComparison.OrdinalIgnoreCase) < 0) return false;
                            if (!appPid.HasValue) return true;
                            try
                            {
                                var pid = b.Properties.ProcessId.ValueOrDefault;
                                return pid == 0 || pid == appPid.Value;
                            }
                            catch { return true; }
                        });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ⚠ attempt#{btnAttempts} find 'Importer Rapture' threw : {ex.GetType().Name} — retry");
                btn = null;
            }
            if (btn is null) Thread.Sleep(250);
        }
        if (btn is null)
            throw new Exception($"Bouton 'Importer Rapture' introuvable après {btnAttempts} tentatives en {btnSw.Elapsed.TotalSeconds:F1}s — fix PhaseChanged() pas effective ? Mode // = UIA cache contaminé ?");
        if (btnAttempts > 1)
            Console.WriteLine($"      ✓ Bouton 'Importer Rapture' trouvé au {btnAttempts}e essai ({btnSw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine($"      → Click 'Importer Rapture' (AutomationId={SafeText(() => btn.AutomationId)})");
        // Le handler du bouton ouvre un OpenFileDialog modal côté WinForms : InvokePattern.Invoke()
        // ne retourne donc qu'à la fermeture de la modale → on doit fire-and-forget.
        //
        // Stratégie : (1) attendre que la BoundingRectangle soit valide (max 5s), (2) tirer Click()
        // dans un Task background sans attendre, (3) la suite de la méthode poll déjà le dialog.
        // Si Click() pose problème (NoClickablePointException), fallback Focus+Space sur le thread courant.
        var clickWaitSw = Stopwatch.StartNew();
        while (clickWaitSw.Elapsed.TotalSeconds < 5)
        {
            try
            {
                var rect = btn.BoundingRectangle;
                if (rect.Width > 0 && rect.Height > 0) break;
            }
            catch { /* keep waiting */ }
            Thread.Sleep(200);
        }
        // ⚠ Le bouton 'Importer Rapture' (toolbar RIG) n'a PAS de hwnd natif :
        // Interaction.Click retombe alors sur InvokePattern.Invoke(), qui est
        // SYNCHRONE et bloque jusqu'à la fermeture de l'OpenFileDialog modal ouvert
        // par le handler. Appelé sur le thread courant, ça gèle le driver ~60s
        // jusqu'au timeout UIA. On tire donc le clic dans un Task background : le
        // thread courant continue et poll le dialog (étape 2 ci-dessous). La preuve
        // que le clic a réussi = le dialog apparaît (sinon throw après 10s).
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Interaction.Click(btn); }
            catch (Exception ex)
            { Console.WriteLine($"      → (bg) Interaction.Click a jeté : {ex.GetType().Name}: {ex.Message}"); }
        });
        Console.WriteLine("      → Click 'Importer Rapture' tiré en background (handler = OpenFileDialog modal)");

        // 2) Attendre OpenFileDialog ('Importer Rapture' ou 'Ouvrir' ou 'Choisir')
        // Timing : sur le user desktop (RIG_DRIVER_HEADLESS=0), l'OpenFileDialog
        // Windows met plus de temps à s'initialiser que sur un HDESK séparé (file system
        // enum, shell icons, etc.). 10s s'est avéré trop court → on monte à 30s.
        // Log instrumenté pour diagnostiquer quelles fenêtres sont vues à chaque tour.
        var sw = Stopwatch.StartNew();
        AutomationElement? dialog = null;
        int iter = 0;
        var seenTitles = new System.Collections.Generic.HashSet<string>();
        while (sw.Elapsed.TotalSeconds < 30 && dialog is null)
        {
            iter++;
            try
            {
                foreach (var w in _app!.GetAllTopLevelWindows(_automation!))
                {
                    var t = SafeText(() => w.Title);
                    if (!string.IsNullOrEmpty(t) && seenTitles.Add(t))
                        Console.WriteLine($"      → [poll#{iter} {sw.Elapsed.TotalSeconds:F1}s] top-level window seen : '{t}'");
                    if (t.IndexOf("Ouvrir", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.IndexOf("Importer", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.IndexOf("Choisir", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.IndexOf("Rapture", StringComparison.OrdinalIgnoreCase) >= 0)
                    { dialog = w; break; }
                }
                if (dialog is null)
                {
                    foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                    {
                        var nm = SafeText(() => w.Name);
                        if (!string.IsNullOrEmpty(nm) && seenTitles.Add("[child]" + nm))
                            Console.WriteLine($"      → [poll#{iter} {sw.Elapsed.TotalSeconds:F1}s] descendant Window : '{nm}'");
                        if (nm.IndexOf("Ouvrir", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.IndexOf("Importer", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.IndexOf("Choisir", StringComparison.OrdinalIgnoreCase) >= 0)
                        { dialog = w; break; }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"      → [poll#{iter}] enum exception (continuing) : {ex.GetType().Name}: {ex.Message}"); }
            if (dialog is null) Thread.Sleep(300);
        }
        if (dialog is null)
            throw new Exception($"OpenFileDialog pas apparu après 30s click 'Importer Rapture' (polls={iter})");
        Console.WriteLine($"      → OpenFileDialog DÉTECTÉ après {sw.Elapsed.TotalSeconds:F1}s ({iter} polls)");
        Console.WriteLine($"      → OpenFileDialog détecté : Name='{SafeText(() => dialog.Name)}'");

        // 3) Trouver le champ 'Nom du fichier'.
        // ⚠ AutomationId='1148' = ComboBoxEx WRAPPER (avec dropdown autocomplete historique).
        //    ValuePattern.SetValue sur le wrapper N'ÉCRIT PAS dans le edit visible (bug
        //    découvert via screenshot 2026-05-22 14:53 : path renseigné mais champ vide).
        //    On cherche d'abord le ComboBoxEx, puis l'EDIT enfant dont le hwnd accepte
        //    WM_SETTEXT correctement (ou ValuePattern sur l'edit lui-même).
        var fileNameCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148"));
        AutomationElement? fileNameEdit = null;
        if (fileNameCombo is not null)
        {
            // L'Edit interne du ComboBoxEx — où la valeur est réellement stockée.
            fileNameEdit = fileNameCombo.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        }
        fileNameEdit ??= dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (fileNameEdit is null)
            throw new Exception("Champ 'Nom du fichier' (edit inner) introuvable dans OpenFileDialog");

        // 4) Renseigne le chemin sur l'EDIT INTERNE (pas sur le wrapper ComboBoxEx).
        //    Force WM_SETTEXT pour bypass ValuePattern qui ne propage pas sur le wrapper.
        Interaction.SetText(fileNameEdit, jsonPath);
        Console.WriteLine($"      → Path renseigné sur Edit inner ComboBoxEx : {jsonPath}");
        Thread.Sleep(200);

        // DEBUG (règle 16 CLAUDE.md) : screenshot du dialog APRÈS SetValue, AVANT submit,
        // pour observer que la valeur est BIEN affichée dans le champ.
        try
        {
            var dbgDir = AuditPaths.Combine("screenshots-loop");
            Directory.CreateDirectory(dbgDir);
            var dbgPath = Path.Combine(dbgDir, $"dialog-after-setvalue-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            Interaction.CaptureWindow(dialog, dbgPath);
            Console.WriteLine($"      → 📸 Dialog après SetValue : {dbgPath}");
        }
        catch (Exception exShot) { Console.WriteLine($"      → (debug shot a jeté, non-bloquant : {exShot.GetType().Name})"); }

        // Mémoriser le titre du file dialog pour pouvoir filtrer dans la détection
        // post-import (sinon le dialogue d'origine matche "Import" → faux positif).
        var fileDialogTitle = SafeText(() => dialog.Name);

        // 5) Submit OpenFileDialog : ENTER posté directement sur le HWND DU DIALOG
        //    (pas sur l'edit interne qui peut ne pas avoir de hwnd via FlaUI).
        //    Le dialog reçoit WM_KEYDOWN VK_RETURN → invoque son default button (Ouvrir)
        //    avec la valeur courante de l'edit field (commit automatique).
        //    Mouse-free ET focus-free (PostMessage ciblé sur hwnd dialog).
        var dialogHwnd = (IntPtr)dialog.Properties.NativeWindowHandle.ValueOrDefault;
        Console.WriteLine($"      → Submit file dialog via Enter sur hwnd dialog 0x{dialogHwnd.ToInt64():X}");
        if (dialogHwnd == IntPtr.Zero)
            throw new Exception("Dialog OpenFileDialog sans hwnd natif — impossible de poster WM_KEYDOWN");
        Interaction.PressKey(dialogHwnd, 0x0D); // VK_RETURN

        // 5.b) Attendre la FERMETURE du file dialog (preuve que Ouvrir a été pris en
        //      compte). Si le dialog reste ouvert >10s, soit le bouton Ouvrir n'a
        //      pas été cliqué, soit RIG a crashé/freezé et la modale OS reste à l'écran.
        var closeSw = Stopwatch.StartNew();
        bool fileDialogClosed = false;
        while (closeSw.Elapsed.TotalSeconds < 10)
        {
            try
            {
                // Le dialog est "fermé" quand UIA ne le retrouve plus par titre.
                var stillThere = _app!.GetAllTopLevelWindows(_automation!)
                    .Any(w => string.Equals(SafeText(() => w.Title), fileDialogTitle, StringComparison.Ordinal));
                if (!stillThere)
                {
                    fileDialogClosed = true;
                    break;
                }
            }
            catch { }
            Thread.Sleep(300);
        }
        if (!fileDialogClosed)
        {
            // RIG est-il encore vivant ?
            bool rigAlive;
            try { rigAlive = _app is not null && !_app.HasExited; } catch { rigAlive = false; }
            Console.WriteLine($"      → ⚠ File dialog '{fileDialogTitle}' toujours présent après 10s. RIG alive={rigAlive}");
            // Cleanup best-effort pour pas laisser un modal en zombie
            try
            {
                var btnX = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b =>
                    {
                        var n = SafeText(() => b.Name);
                        return n.Equals("Annuler", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("Fermer", StringComparison.OrdinalIgnoreCase);
                    });
                if (btnX is not null) { try { Interaction.Click(btnX); } catch { } }
            }
            catch { }
            throw new Exception(
                $"File dialog '{fileDialogTitle}' n'a pas fermé après click 'Ouvrir' — RIG a probablement crashé/freezé en traitant le JSON. " +
                $"RIG.HasExited={!rigAlive}. Voir C:\\rig\\logs\\ pour l'erreur exacte.");
        }

        Console.WriteLine($"      → File dialog fermé après {closeSw.Elapsed.TotalSeconds:F1}s. Poll pipeline import (top-level windows)…");

        // 5.c) Poll : on attend qu'un NOUVEAU top-level window apparaisse (MessageBox
        //      "Aucune audience...", "Audience différente...", FormRaptureImportRecap, ou
        //      DialogBox erreur). Le pipeline peut prendre du temps (Mapper.Load fait du
        //      référentiel DB lourd). Poll 20s, log toutes les 2s les top-levels.
        var pollSw = Stopwatch.StartNew();
        AutomationElement? popup = null;
        bool MatchesPopupTitle(string t) =>
               t.IndexOf("Audience non trouvée", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("Audience différente", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("Import JSON Rapture", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("Récap", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("Recap", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("DialogBox", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("Rapture", StringComparison.OrdinalIgnoreCase) >= 0;

        while (pollSw.Elapsed.TotalSeconds < 20)
        {
            // RIG mort ?
            if (_app is not null && _app.HasExited)
                throw new Exception($"RigClientAccueil a crashé pendant le pipeline import (ExitCode={_app.ExitCode})");

            // ⚠ Bug trouvé : WinForms MessageBox.Show(_ownerWindow, ...) avec un owner =
            // FORM_LIVAUD apparaît comme DESCENDANT de la main window, PAS comme top-level.
            // On doit polléger les deux. (FormRaptureImportRecap.ShowDialog(_ownerWindow)
            // pareil — modal owned, donc descendant.)
            try
            {
                foreach (var w in _app!.GetAllTopLevelWindows(_automation!))
                {
                    var t = SafeText(() => w.Title);
                    if (string.Equals(t, fileDialogTitle, StringComparison.Ordinal)) continue;
                    if (MatchesPopupTitle(t)) { popup = w; Console.WriteLine($"      → Popup TOP-LEVEL après {pollSw.Elapsed.TotalSeconds:F1}s : '{t}'"); break; }
                }
            }
            catch { }
            if (popup is null)
            {
                try
                {
                    foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                    {
                        var nm = SafeText(() => w.Name);
                        if (string.Equals(nm, fileDialogTitle, StringComparison.Ordinal)) continue;
                        if (nm.Equals("Live Audience", StringComparison.OrdinalIgnoreCase)) continue;
                        if (nm.Equals("Live Cabinet", StringComparison.OrdinalIgnoreCase)) continue;
                        if (MatchesPopupTitle(nm)) { popup = w; Console.WriteLine($"      → Popup DESCENDANT après {pollSw.Elapsed.TotalSeconds:F1}s : Name='{nm}'"); break; }
                    }
                }
                catch { }
            }
            if (popup is not null) break;

            // Log de progrès toutes les 2s
            if ((int)pollSw.Elapsed.TotalSeconds % 2 == 0)
            {
                try
                {
                    var titles = _app!.GetAllTopLevelWindows(_automation!).Select(w => SafeText(() => w.Title)).ToList();
                    var descs = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                        .Select(w => SafeText(() => w.Name)).ToList();
                    Console.WriteLine($"      → [poll {pollSw.Elapsed.TotalSeconds:F0}s] top=[{string.Join(" | ", titles)}] desc=[{string.Join(" | ", descs)}]");
                }
                catch { }
            }
            Thread.Sleep(300);
        }

        if (popup is null)
        {
            // Pas de popup en 20s — vraie anomalie. Dump complet.
            Console.WriteLine($"      → ⚠ Aucune popup post-import en 20s. Tous les top-level :");
            try { foreach (var w in _app!.GetAllTopLevelWindows(_automation!)) Console.WriteLine($"          - '{SafeText(() => w.Title)}'"); } catch { }
            Console.WriteLine("      → Descendants Window de la main window :");
            foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                Console.WriteLine($"          - Name='{SafeText(() => w.Name)}'");
            throw new Exception(
                "Aucune popup post-import en 30s — le pipeline a complété SILENCIEUSEMENT, ou s'est planté sans DialogBox. " +
                "Probables causes : exception swallowed dans Mapper.Load / Validator / Diff, ou crash de la UI thread. " +
                "Vérifier les logs C:\\rig\\logs\\ et l'event viewer Windows.");
        }

        Console.WriteLine($"      → ✓ Popup post-import : Name='{SafeText(() => popup.Name)}'");

        // 7) Routage selon le titre de la popup + acceptCreate :
        //    a) "Audience non trouvée" → si acceptCreate=true click 'Oui' (créer),
        //       sinon click 'Non' (refuser l'import).
        //    b) "Audience différente" → click 'Oui' pour switcher (rare en smoke).
        //    c) Autres → click Annuler/Fermer/OK pour pas polluer BDD.
        var popupName = SafeText(() => popup.Name);
        bool isAudienceNotFound = popupName.IndexOf("Audience non trouvée", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isAudienceDifferent = popupName.IndexOf("Audience différente", StringComparison.OrdinalIgnoreCase) >= 0
                                || popupName.IndexOf("Audience differente", StringComparison.OrdinalIgnoreCase) >= 0;

        // 7a) Cas A direct : l'audience ouverte matche le JSON (date+greffe) →
        // l'Orchestrateur n'affiche AUCUNE popup intermédiaire et la 1ère "popup"
        // détectée par notre poll EST déjà le FormRaptureImportRecap. On capture
        // la fenêtre window-only puis on Annule, sans passer par les branches
        // YES-create / Audience-différente.
        bool popupIsRecapDirect = false;
        try
        {
            bool hasGrid = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Table)).Any()
                        || popup.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid)).Any();
            bool hasRecapBtns = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Any(b => SafeText(() => b.Name).IndexOf("Annuler", StringComparison.OrdinalIgnoreCase) >= 0)
                && popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .Any(b => SafeText(() => b.Name).IndexOf("Importer", StringComparison.OrdinalIgnoreCase) >= 0);
            popupIsRecapDirect = hasGrid && hasRecapBtns;
        }
        catch { }
        if (popupIsRecapDirect)
        {
            Console.WriteLine($"      → ✓ Recap atteinte DIRECTEMENT (Cas A : audience ouverte matche par date+greffe)");
            try
            {
                Thread.Sleep(900);
                var shotPath = Path.Combine(ScreenshotDir(),
                    $"smoke-rapture-recap-VIEW-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                Interaction.CaptureWindow(popup, shotPath);
                Console.WriteLine($"      📸 Screenshot recap : {shotPath}");
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Screenshot recap échoué : {ex.Message}"); }
            // Lit les 4 stat tiles (modifications / avertissements / erreur / bloquées)
            ReadRecapCounters(popup);
            if (clickImporter)
            {
                ClickRecapImporterAndConfirm(popup);
            }
            else
            {
                Console.WriteLine($"      → Annuler (clickImporter=false)");
                ClickButtonByNames(popup, "Annuler", "&Annuler", "Cancel", "Fermer", "&Fermer");
            }
            return;
        }

        if (isAudienceDifferent)
        {
            // ── Cas A bis : JSON cible une audience EXISTANTE différente de celle
            // actuellement ouverte. L'Orchestrator demande "Importer dans l'audience
            // TROUVÉE ?" → on clique Oui pour switcher sur l'audience matchée, puis
            // on attend que la recap apparaisse pour la traiter normalement.
            Console.WriteLine($"      → ✓ Popup 'Audience différente' (Cas A bis) — click Oui pour switcher");
            ClickButtonByNames(popup, "Oui", "&Oui", "Yes");

            // L'Orchestrator continue avec l'audience trouvée : Mapper.Load → Validator
            // → Diff.Compute → FormRaptureImportRecap. Diff.Compute prend ~20s sur 72
            // affaires donc cap raisonnable à 45s (au lieu de 90s historique).
            Console.WriteLine("      → Wait FormRaptureImportRecap (Cas A bis post-switch)…");
            AutomationElement? recapBis = null;
            var swBis = Stopwatch.StartNew();
            while (swBis.Elapsed.TotalSeconds < 45 && recapBis is null)
            {
                Thread.Sleep(300);
                recapBis = WaitForPopupByNameContains("Import JSON Rapture", excludingName: popupName, timeoutSec: 1);
            }
            if (recapBis is null)
            {
                throw new Exception("Cas A bis : FormRaptureImportRecap pas apparu après 'Oui' switch en 45s");
            }
            Console.WriteLine($"      → ✓ FormRaptureImportRecap (Cas A bis) après {swBis.Elapsed.TotalSeconds:F1}s");
            // Capture + click bouton selon clickImporter
            try
            {
                Thread.Sleep(900);
                var shotPath = Path.Combine(ScreenshotDir(),
                    $"smoke-rapture-recap-VIEW-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                Interaction.CaptureWindow(recapBis, shotPath);
                Console.WriteLine($"      📸 Screenshot recap (Cas A bis) : {shotPath}");
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Screenshot recap échoué : {ex.Message}"); }
            ReadRecapCounters(recapBis);
            if (clickImporter)
            {
                ClickRecapImporterAndConfirm(recapBis);
            }
            else
            {
                Console.WriteLine($"      → Annuler (clickImporter=false)");
                ClickButtonByNames(recapBis, "Annuler", "&Annuler", "Cancel", "Fermer", "&Fermer");
            }
            return;
        }

        if (isAudienceNotFound && acceptCreate)
        {
            // ── Scénario YES : on accepte la création de l'audience depuis le JSON.
            ClickButtonByNames(popup, "Oui", "&Oui", "Yes");

            // L'orchestrateur appelle Creator.CreateFromJson, puis affiche une popup
            // MessageBox "Audience créée (ID=NNNN).\r\nL'import va se poursuivre..."
            // On capture l'ID pour le cleanup SQL, et on clique OK pour continuer.
            var createdPopup = WaitForPopupByNameContains("Import JSON Rapture", excludingName: popupName, timeoutSec: 15);
            if (createdPopup is null)
                throw new Exception("Popup de confirmation 'Audience créée' pas apparue en 15s — Creator.CreateFromJson a peut-être throw");
            var createdText = ExtractStaticText(createdPopup);
            Console.WriteLine($"      → Popup 'Audience créée' : {createdText}");
            // Match "ID=12345" dans le texte (logique pure testée xUnit, cf. LegacyParsing)
            var newIdOpt = LegacyParsing.ExtractAudienceId(createdText);
            if (newIdOpt.HasValue)
            {
                LastCreatedAudienceId = newIdOpt.Value;
                Console.WriteLine($"      → ✓ LastCreatedAudienceId={newIdOpt.Value} (sera cleanup en SQL post-smoke)");
            }
            else
            {
                Console.WriteLine($"      → ⚠ Impossible d'extraire l'ID de la popup, pas de cleanup possible");
            }
            ClickButtonByNames(createdPopup, "OK", "&OK");

            // L'orchestrateur continue (sur le thread UI WinForms, BLOQUÉ) :
            //   Mapper.Load → Validator.Validate → Diff.Compute → FormRaptureImportRecap.ShowDialog
            // Diff.Compute fait 1 SELECT * APPEL_AFFAIRE puis itère 44 affaires × N champs en
            // mémoire, ce qui peut prendre 20-30s. On poll jusqu'à 45s pour voir la recap apparaître.
            // Le titre attendu est "Import JSON Rapture" (cf FormRaptureImportRecap.cs:68).
            Console.WriteLine("      → Wait FormRaptureImportRecap (Diff.Compute peut prendre 20-30s sur 44 affaires)…");
            AutomationElement? recap = null;
            var recapSw = Stopwatch.StartNew();
            int lastLogSec = -1;
            while (recapSw.Elapsed.TotalSeconds < 45)
            {
                try
                {
                    // Cherche tout window descendant ou top-level dont le Name == "Import JSON Rapture"
                    // mais qui n'est PAS la popup "Audience créée" déjà fermée. Discriminateur : le
                    // recap form a beaucoup de descendants (DataGridView, boutons), la MessageBox en a peu.
                    var candidates = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                        .Where(w =>
                        {
                            var nm = SafeText(() => w.Name);
                            if (nm.IndexOf("Rapture", StringComparison.OrdinalIgnoreCase) < 0) return false;
                            if (nm.Equals("Live Audience", StringComparison.OrdinalIgnoreCase)) return false;
                            if (nm.Equals("Live Cabinet", StringComparison.OrdinalIgnoreCase)) return false;
                            return true;
                        })
                        .ToList();
                    // Le recap est celui qui a un DataGridView descendant ou un bouton "Annuler"/"Importer"
                    foreach (var c in candidates)
                    {
                        bool hasGrid = c.FindAllDescendants(cf => cf.ByControlType(ControlType.Table)).Any()
                                    || c.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid)).Any();
                        bool hasRecapButtons = c.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                            .Any(b => SafeText(() => b.Name).IndexOf("Annuler", StringComparison.OrdinalIgnoreCase) >= 0
                                  || SafeText(() => b.Name).IndexOf("Importer", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (hasGrid || hasRecapButtons) { recap = c; break; }
                    }
                }
                catch { }
                if (recap is not null) break;

                // Log toutes les 5s : top-levels + descendants Window avec leur Name
                int sec = (int)recapSw.Elapsed.TotalSeconds;
                if (sec / 5 != lastLogSec / 5)
                {
                    lastLogSec = sec;
                    try
                    {
                        var descs = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                            .Select(w => SafeText(() => w.Name)).ToList();
                        Console.WriteLine($"      → [recap poll {sec}s] descendants Window=[{string.Join(" | ", descs)}]");
                    }
                    catch { }
                }
                Thread.Sleep(300);
            }
            if (recap is null)
            {
                Console.WriteLine("      ⚠ FormRaptureImportRecap pas détecté en 45s. Top-level + descendants à ce moment :");
                try { foreach (var w in _app!.GetAllTopLevelWindows(_automation!)) Console.WriteLine($"          top: '{SafeText(() => w.Title)}'"); } catch { }
                foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                    Console.WriteLine($"          desc: Name='{SafeText(() => w.Name)}'");
            }
            else
            {
                Console.WriteLine($"      → ✓ FormRaptureImportRecap détecté après {recapSw.Elapsed.TotalSeconds:F1}s : Name='{SafeText(() => recap.Name)}'");
                // Capture la récap PENDANT qu'elle est ouverte (gate visuel : layout
                // de FormRaptureImportRecap) AVANT de l'annuler.
                try
                {
                    Thread.Sleep(900);
                    // Capture la FENÊTRE recap uniquement (pas le bureau multi-moniteur).
                    try
                    {
                        var shotPath = Path.Combine(ScreenshotDir(),
                            $"smoke-rapture-recap-VIEW-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                        Interaction.CaptureWindow(recap, shotPath);
                        Console.WriteLine($"      📸 Screenshot recap (fenêtre) : {shotPath}");
                    }
                    catch (Exception ex) { Console.WriteLine($"      ⚠ Screenshot recap échoué : {ex.Message}"); }
                }
                catch { }
                ReadRecapCounters(recap);
                if (clickImporter)
                {
                    ClickRecapImporterAndConfirm(recap);
                }
                else
                {
                    Console.WriteLine($"      → Annuler (clickImporter=false)");
                    ClickButtonByNames(recap, "Annuler", "&Annuler", "Cancel", "Fermer", "&Fermer");
                }
            }
            return;
        }

        // Refus (défaut) : capture d'abord le TEXTE du dialog (ex. ERROR_PARSE json-malformé : DialogBox
        // générique sans recap — scout 2026-07-06) pour permettre l'assertion --expected-error-contains,
        // PUIS clique Non / Annuler / Fermer / OK.
        try
        {
            LastErrorDialogText = ExtractStaticText(popup);
            if (!string.IsNullOrEmpty(LastErrorDialogText))
                Console.WriteLine($"      → Dialog text capturé : {LastErrorDialogText.Replace("\r\n", " | ")}");
        }
        catch { }
        var btnRefuse = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b =>
            {
                var n = SafeText(() => b.Name);
                return n.Equals("Non", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("&Non", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("No", StringComparison.OrdinalIgnoreCase)
                    || n.IndexOf("Annuler", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Fermer", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.Equals("OK", StringComparison.OrdinalIgnoreCase);
            });
        if (btnRefuse is not null)
        {
            Console.WriteLine($"      → Cleanup : click '{SafeText(() => btnRefuse.Name)}'");
            Interaction.Click(btnRefuse);
        }
    }

    /// <summary>
    /// Mode APPLY de la recap : trouve le bouton "Importer (N champ(s) coché(s))" qui
    /// matche un préfixe (le compteur N varie), click, gère les 2 popups successifs
    /// du flux <c>BtnImporter_Click</c> :
    ///   1. Confirmation : "Appliquer N modification(s) en base ?" → click Oui
    ///   2. Résultat     : "Import terminé. Appliqués: X / Skipped: Y / Erreurs: Z" → click OK
    /// Puis attend la fermeture du form recap (DialogResult.OK).
    ///
    /// ⚠ Appelle ApplyService → ÉCRITURE RÉELLE en base. Doit être encadré
    /// par snapshot+restore côté SmokeRunner pour rester net-zero sur RIG_DEV.
    /// </summary>
    private void ClickRecapImporterAndConfirm(AutomationElement recap)
    {
        // 1. Trouver le bouton "Importer (...)" — Name préfixé, suffixe variable
        var importer = recap.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => SafeText(() => b.Name).StartsWith("Importer", StringComparison.OrdinalIgnoreCase));
        if (importer is null)
            throw new Exception("Bouton 'Importer (...)' introuvable dans la recap");
        var importerLabel = SafeText(() => importer.Name);
        if (!importer.IsEnabled)
        {
            // Bouton Importer désactivé = 0 modif applicable (header en erreur OU
            // 0 affaire matchable OU pré-validation bloque). Pour les scénarios de
            // test négatif (erreurs header/affaire, JSON vide, audience nouvellement
            // créée sans cascade), c'est le résultat ATTENDU. On clique Annuler au
            // lieu de throw — le smoke marque PASS (le test négatif a réussi : le
            // pipeline a correctement refusé l'import).
            Console.WriteLine($"      → '{importerLabel}' DÉSACTIVÉ (0 modif applicable — test négatif réussi). Annuler.");
            ClickButtonByNames(recap, "Annuler", "&Annuler", "Cancel", "Fermer", "&Fermer");
            return;
        }
        Console.WriteLine($"      → Click '{importerLabel}' (APPLY mode — écriture base)");
        // Interaction.Click poste WM_LBUTTON* : non-bloquant, ne vole pas la souris,
        // et ne deadlocke pas sur le MessageBox.Show synchrone du handler.
        Interaction.Click(importer);
        Console.WriteLine($"      → Click posté sur '{importerLabel}' — poll popup confirmation");

        // 2. Popup confirmation "Appliquer N modification(s) en base ?" → Oui
        var confirm = WaitForPopupByNameContains("Confirmation import Rapture", excludingName: SafeText(() => recap.Name), timeoutSec: 10);
        if (confirm is null)
            throw new Exception("Popup 'Confirmation import Rapture' pas apparue en 10s post-click Importer");
        Console.WriteLine($"      → Popup confirmation détectée — click Oui");
        ClickButtonByNames(confirm, "Oui", "&Oui", "Yes");

        // 3. Popup résultat "Import terminé..." → OK
        //    ApplyService.Apply tourne sync (peut prendre 1-5s sur 30 modifs).
        var result = WaitForPopupByNameContains("Résultat de l'import", excludingName: SafeText(() => recap.Name), timeoutSec: 60);
        if (result is null)
            throw new Exception("Popup 'Résultat de l'import' pas apparue en 60s — ApplyService a-t-il throw ?");
        var resultText = ExtractStaticText(result);
        Console.WriteLine($"      → Popup résultat : {resultText.Replace("\r\n", " | ")}");
        ClickButtonByNames(result, "OK", "&OK");

        // 4. La recap se ferme avec DialogResult.OK. Poll sa disparition pour confirmer.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 5)
        {
            if (!recap.IsAvailable) break;
            Thread.Sleep(150);
        }
        Console.WriteLine($"      → ✓ Recap fermée (apply terminé)");
    }

    /// <summary>
    /// Click un bouton dont le Name match l'un des candidats (case-insensitive).
    /// Throw si aucun match. Helper pour le flow YES create audience.
    /// </summary>
    private void ClickButtonByNames(AutomationElement parent, params string[] candidates)
    {
        var btn = parent.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b =>
            {
                var n = SafeText(() => b.Name);
                return candidates.Any(c => n.Equals(c, StringComparison.OrdinalIgnoreCase));
            });
        if (btn is null)
        {
            var available = parent.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(b => "'" + SafeText(() => b.Name) + "'");
            throw new Exception($"Aucun bouton parmi [{string.Join(", ", candidates)}] dans la popup. Disponibles : {string.Join(", ", available)}");
        }
        Console.WriteLine($"      → Click '{SafeText(() => btn.Name)}' dans popup '{SafeText(() => parent.Name)}'");
        Interaction.Click(btn);
        Thread.Sleep(300); // laisse la modale se fermer
    }

    /// <summary>
    /// Poll les top-level + descendants pour trouver une popup dont le Name CONTIENT
    /// <paramref name="needle"/>, en excluant celle dont le Name == excludingName.
    /// Returns null si timeout.
    /// </summary>
    private AutomationElement? WaitForPopupByNameContains(string needle, string excludingName, int timeoutSec)
    {
        if (_window is null || _app is null || _automation is null) return null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            foreach (var w in SafeGet(() => _app.GetAllTopLevelWindows(_automation).Cast<AutomationElement>().ToList()) ?? new List<AutomationElement>())
            {
                var t = SafeText(() => w.Name);
                if (string.IsNullOrEmpty(t) || string.Equals(t, excludingName, StringComparison.Ordinal)) continue;
                if (t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return w;
            }
            foreach (var w in SafeGet(() => _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)).ToList()) ?? new List<AutomationElement>())
            {
                var nm = SafeText(() => w.Name);
                if (string.IsNullOrEmpty(nm) || string.Equals(nm, excludingName, StringComparison.Ordinal)) continue;
                if (nm.Equals("Live Audience", StringComparison.OrdinalIgnoreCase)) continue;
                if (nm.Equals("Live Cabinet", StringComparison.OrdinalIgnoreCase)) continue;
                if (nm.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return w;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    /// <summary>Concat des Text descendants de la popup, pour extraire le message body d'une MessageBox.</summary>
    private string ExtractStaticText(AutomationElement parent)
    {
        try
        {
            var texts = parent.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => SafeText(() => t.Name))
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(" ⏎ ", texts);
        }
        catch { return ""; }
    }

    private static T? SafeGet<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    /// <summary>
    /// Click 'Export JSON' Plumitif → SaveFileDialog → tape un path target → Save →
    /// vérifie que le fichier JSON est bien produit sur disque.
    /// </summary>
    public void ClickExportJsonAndSaveTo(string outputPath)
    {
        if (_window is null) throw new InvalidOperationException("_window null");

        // 1) Préparer le dossier target + cleanup ancien fichier si existant
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(outputPath)) File.Delete(outputPath);
        Console.WriteLine($"      → Target export : {outputPath}");

        // 2) Trouver le bouton Export JSON (Pane=Ult_Button avec AutomationId='BtnExportJsonPlum')
        var btn = _window.FindFirstDescendant(cf => cf.ByAutomationId("BtnExportJsonPlum"));
        if (btn is null)
            throw new Exception("Bouton BtnExportJsonPlum introuvable — PROC_PREAUD pas ouvert ou audience pas sélectionnée");
        Console.WriteLine($"      → Click 'Export JSON'");
        Interaction.Click(btn);

        // 3) Attendre soit SaveFileDialog soit DialogBox "Aucune affaire" (descendant de _window,
        //    pas top-level — pareil pattern que la modale RCS de KBIS smoke).
        var sw = Stopwatch.StartNew();
        AutomationElement? dialog = null;
        bool isError = false;
        while (sw.Elapsed.TotalSeconds < 10 && dialog is null)
        {
            try
            {
                // a) Top-level (SaveFileDialog Windows standard)
                foreach (var w in _app!.GetAllTopLevelWindows(_automation!))
                {
                    var t = SafeText(() => w.Title);
                    if (t.IndexOf("Export JSON", StringComparison.OrdinalIgnoreCase) >= 0
                     || t.IndexOf("Enregistrer", StringComparison.OrdinalIgnoreCase) >= 0)
                    { dialog = w; break; }
                }
                // b) Descendant (modale custom RIG ou DialogBox "Aucune affaire")
                if (dialog is null)
                {
                    foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                    {
                        var nm = SafeText(() => w.Name);
                        if (nm.IndexOf("Export JSON", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.IndexOf("Aucune affaire", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.IndexOf("DialogBox", StringComparison.OrdinalIgnoreCase) >= 0)
                        { dialog = w; if (nm.IndexOf("Aucune", StringComparison.OrdinalIgnoreCase) >= 0) isError = true; break; }
                    }
                }
            }
            catch { }
            if (dialog is null) Thread.Sleep(300);
        }
        if (dialog is null)
        {
            Console.WriteLine("      → Aucun dialog détecté. Top-level windows :");
            foreach (var w in _app!.GetAllTopLevelWindows(_automation!))
                Console.WriteLine($"          - '{SafeText(() => w.Title)}'");
            Console.WriteLine("      → Descendants Window de _window :");
            foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                Console.WriteLine($"          - Name='{SafeText(() => w.Name)}'");
            throw new Exception("Aucun dialog post-click Export JSON (ni SaveFileDialog ni DialogBox d'erreur)");
        }
        if (isError)
        {
            Console.WriteLine($"      → ⚠ DialogBox d'erreur détecté : '{SafeText(() => dialog.Name)}'");
            // Cleanup : close it
            var okBtn = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => SafeText(() => b.Name).Equals("OK", StringComparison.OrdinalIgnoreCase));
            if (okBtn is not null) { try { Interaction.Click(okBtn); } catch { } }
            throw new Exception("Click Export JSON déclenche DialogBox 'Aucune affaire à exporter' — _audienceCabinet n'est pas bindé à OPE_EDIT_PLUMITIF, peut-être que la sélection d'audience ne propage pas au control export.");
        }
        Console.WriteLine($"      → SaveFileDialog détecté : Title/Name='{SafeText(() => dialog.Name)}'");

        // 4) Trouver le champ "Nom du fichier" (Edit avec AutomationId='1148' standard Windows)
        var fileNameEdit = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148"))
                        ?? dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        if (fileNameEdit is null)
            throw new Exception("Champ 'Nom du fichier' introuvable dans SaveFileDialog");

        // 5) Renseigne le chemin via SetValue (ValuePattern) — remplace click + Ctrl+A + Delete + Type.
        //    SetValue écrase le contenu intégral du champ sans synthèse clavier globale.
        Interaction.SetText(fileNameEdit, outputPath);
        Console.WriteLine($"      → Path renseigné via ValuePattern : {outputPath}");
        Thread.Sleep(500);

        // 6) Click bouton "Enregistrer" (= Save) du dialog
        var btnSave = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b =>
            {
                var n = SafeText(() => b.Name);
                return n.Equals("Enregistrer", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Save", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("&Enregistrer", StringComparison.OrdinalIgnoreCase);
            });
        if (btnSave is null)
        {
            Console.WriteLine("      → Bouton Enregistrer introuvable, fallback PressEnter sur le champ");
            Interaction.PressEnter(fileNameEdit);
        }
        else
        {
            Console.WriteLine($"      → Click '{SafeText(() => btnSave.Name)}'");
            Interaction.Click(btnSave);
        }
        Thread.Sleep(2000); // laisse l'écriture + le DialogBox de confirmation

        // 7) Si un DialogBox "Export JSON terminé" apparaît, ferme-le. Le DialogBox est
        //    un descendant de _window (custom RIG, pas top-level Windows). Son OK est
        //    un Pane Name='Ok' (k minuscule, pas un vrai Button UIA).
        Thread.Sleep(500);
        try
        {
            AutomationElement? okEl = null;
            foreach (var c in _window.FindAllDescendants())
            {
                var nm = SafeText(() => c.Name);
                if (nm.Equals("Ok", StringComparison.OrdinalIgnoreCase) || nm.Equals("OK", StringComparison.Ordinal))
                { okEl = c; break; }
            }
            if (okEl is not null)
            {
                Console.WriteLine($"      → Cleanup dialog : click '{SafeText(() => okEl.Name)}'");
                try { Interaction.Click(okEl); } catch { }
                Thread.Sleep(500);
            }
        }
        catch { }

        // 8) Verify fichier sur disque
        Thread.Sleep(500);
        if (!File.Exists(outputPath))
            throw new Exception($"Export JSON terminé mais fichier introuvable : {outputPath}");
        var size = new FileInfo(outputPath).Length;
        Console.WriteLine($"      → ✓ Fichier JSON produit : {outputPath} ({size} octets)");
        if (size < 50)
            throw new Exception($"Fichier produit trop petit ({size} octets) — probablement vide ou tronqué");

        // 9) Sanity : le contenu commence par '{' (JSON objet)
        var firstChar = File.ReadAllText(outputPath).TrimStart().FirstOrDefault();
        if (firstChar != '{')
            throw new Exception($"Fichier produit ne commence pas par '{{' (1er char : '{firstChar}') — pas un JSON valide");
        Console.WriteLine($"      → ✓ Contenu commence par '{{' (JSON valide)");
    }

    /// <summary>
    /// Après ouverture de PROC_RETAUD, sélectionne la 1ère audience dans la grille de résultats
    /// et clique 'Valider la sélection' pour passer en phase Saisie. C'est dans cette phase
    /// que les boutons 'Importer Rapture' / 'Voir données Rapture' deviennent visibles
    /// (cf. FORM_RETAUD.cs : eButtonVisibleOnPhase.Saisie).
    /// </summary>
    public void SelectFirstAudienceInRetaud()
    {
        if (_window is null) throw new InvalidOperationException("_window null");

        // ML LOOP S1.2 — retry+pid filter pour mode visible-parallèle.
        var btnValider = FindButtonWithRetry("Valider la sélection", timeoutSec: 20.0);
        if (btnValider is null)
            throw new Exception("Bouton 'Valider la sélection' introuvable dans la phase Recherche de PROC_RETAUD (20s, pid-filtered)");

        // Pick row : prefer non-INT (PROC_LIVAUD interactive). Pour PROC_RETAUD (retour
        // post-audience) il faut une audience type CX/AU ou PLD ou PC, pas INT.
        // 1) Liste toutes les rows de la grille
        var rows = _window.FindAllDescendants().Where(c =>
        {
            try { var ct = c.ControlType.ToString(); return ct == "DataItem" || ct == "ListItem"; }
            catch { return false; }
        }).ToList();
        Console.WriteLine($"      → {rows.Count} rows dans la grille audiences");

        // 2) Pour chaque row, construit un "résumé" = Name + Names des cells enfants
        // ⚠ SAFETY 2026-05-22 (anti-mail-spam) : whitelist chambre obligatoire. Cf.
        //    SelectAudienceInRetaudByDateHeure pour explication. Valider une row
        //    avec chambre tronquée UIA ("Mise " au lieu de "Mise en état") déclenche
        //    RIG.METIER.TableRef.CHAMBRE.GetCHAMBRE("Mise ") → throw → mail envoyé.
        var knownChambreCodes = new[] { "REF", "AU", "CX", "MD", "PC", "TC", "CC", "JI", "JE", "FT", "CLOT", "DCP" };
        AutomationElement? targetRow = null;
        string targetSummary = "";
        for (int i = 0; i < rows.Count; i++)
        {
            var rowName = SafeText(() => rows[i].Name);
            var cells = rows[i].FindAllChildren();
            var cellNames = cells.Select(c => SafeText(() => c.Name)).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var cellsContent = string.Join(" | ", cellNames);
            var summary = $"'{rowName}' [{cellsContent}]";
            bool isInteractive = summary.IndexOf("INT", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasKnownChambre = cellNames.Any(c =>
                knownChambreCodes.Any(k => c.Trim().Equals(k, StringComparison.Ordinal)));
            string statusTag = isInteractive ? "⊘ INT" : (!hasKnownChambre ? "⊘ chambre-suspecte" : "✓ valide");
            Console.WriteLine($"          row[{i}] {statusTag} {summary}");
            if (!isInteractive && hasKnownChambre && targetRow is null)
            {
                targetRow = rows[i];
                targetSummary = summary;
            }
        }

        if (targetRow is null)
        {
            // DIAG-MSAA 2026-06-04 : la grille "Recherche d'audience" est un DataGridView WinForms
            // AVEUGLE à UIA (row.FindAllChildren() = []). Probe READ-ONLY (aucune sélection → aucun
            // risque mail-spam) pour confirmer la lisibilité MSAA + trouver le hwnd, exactement comme
            // CollectCellsMsaa le fait pour la grille des demandes (DCADEMAT).
            try
            {
                var gridCandidates = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Table))
                    .Concat(_window.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid)))
                    .ToList();
                Console.WriteLine($"      [DIAG-MSAA] {gridCandidates.Count} grille(s) Table/DataGrid candidate(s)");
                foreach (var g in gridCandidates)
                {
                    IntPtr h = IntPtr.Zero;
                    try { h = g.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                    var aid = SafeText(() => g.AutomationId);
                    if (h == IntPtr.Zero) { Console.WriteLine($"      [DIAG-MSAA] aid='{aid}' hwnd=0 (skip)"); continue; }
                    var cells = CollectCellsMsaa(h, _ => true, 20, out int scanned);
                    Console.WriteLine($"      [DIAG-MSAA] aid='{aid}' hwnd={h} scanned={scanned} rows-concat={cells.Count}");
                    foreach (var c in cells.Take(12))
                        Console.WriteLine($"      [DIAG-MSAA]   [{c.text}]");
                }
            }
            catch (Exception dex) { Console.WriteLine($"      [DIAG-MSAA] probe jeté : {dex.Message}"); }

            throw new Exception($"Aucune audience VALIDE (non-INT + chambre whitelistée) trouvée parmi les {rows.Count} rows. " +
                                "Évite mail-spam RIG. Hardcoder un IdAudCab via env si nécessaire.");
        }

        Console.WriteLine($"      → Sélection row non-INT : {targetSummary}");
        try { Interaction.Select(targetRow); } catch { }
        Thread.Sleep(500);

        Console.WriteLine("      → Click 'Valider la sélection' → passage en phase Saisie PROC_RETAUD");
        Interaction.Click(btnValider);
        Thread.Sleep(1500);
    }

    /// <summary>
    /// Dans la fenêtre 'Recherche d'audience' (phase Recherche PROC_PREAUD/RETAUD),
    /// règle la période Du/Au sur <paramref name="dateFr"/> (dd/MM/yyyy), clique
    /// 'Rechercher' et attend que la grille liste au moins une row portant cette date.
    /// Nécessaire dès que l'audience cible est HORS de la fenêtre de recherche par
    /// défaut (semaine courante) — ex. export d'audiences passées. Les champs Du/Au
    /// sont identifiés comme les 2 premiers Edit dont la valeur est une date
    /// dd/MM/yyyy (ordre de tabulation WinForms : Du puis Au).
    /// </summary>
    public void SearchAudiencesByDate(string dateFr)
    {
        if (_window is null) throw new InvalidOperationException("_window null");
        var searchWin = _window.FindFirstDescendant(cf => cf.ByName("Recherche d'audience")) ?? _window;

        var edits = searchWin.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        var dateEdits = new System.Collections.Generic.List<AutomationElement>();
        foreach (var e in edits)
        {
            string v = SafeText(() =>
                e.Patterns.Value.IsSupported ? e.Patterns.Value.Pattern.Value.ValueOrDefault : e.Name);
            if (!string.IsNullOrEmpty(v) &&
                System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), @"^\d{2}/\d{2}/\d{4}$"))
            {
                dateEdits.Add(e);
                if (dateEdits.Count == 2) break;
            }
        }
        if (dateEdits.Count < 2)
            throw new Exception($"Champs date Du/Au introuvables dans 'Recherche d'audience' ({dateEdits.Count}/2 détectés).");

        Console.WriteLine($"      → Période de recherche : Du={dateFr} Au={dateFr}");
        // ⚠ Ult_Date ne committe le texte vers .Valeur (lu par la requête) QUE sur
        // perte de focus (DoMetierMajFromScreen, cf. Ult_Date_Internal.cs:190) — un
        // SetText seul change l'AFFICHAGE mais la recherche repart sur les anciennes
        // dates. On force donc le cycle focus : click Du → set → click Au (Leave Du
        // committe) → set → re-click Du (Leave Au committe). Les clicks PostMessage
        // suffisent : RIG déplace son focus interne en traitant WM_LBUTTONDOWN.
        Interaction.Click(dateEdits[0]);
        Thread.Sleep(150);
        Interaction.SetText(dateEdits[0], dateFr);
        Interaction.Click(dateEdits[1]);
        Thread.Sleep(150);
        Interaction.SetText(dateEdits[1], dateFr);
        Interaction.Click(dateEdits[0]);
        Thread.Sleep(300);

        // 'Rechercher' est un bouton custom RIG (souvent ControlType=Pane, comme
        // BtnExportJsonPlum) → cherche Button PUIS Pane par Name. "Rechercher" ne
        // matche pas la window "Recherche d'audience" (pas de 'r' final).
        AutomationElement? btnRechercher = null;
        var swBtn = Stopwatch.StartNew();
        while (swBtn.Elapsed.TotalSeconds < 10 && btnRechercher is null)
        {
            btnRechercher = searchWin.FindAllDescendants()
                .FirstOrDefault(el =>
                {
                    try
                    {
                        var ct = el.ControlType.ToString();
                        if (ct != "Button" && ct != "Pane") return false;
                        return SafeText(() => el.Name).IndexOf("Rechercher", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                });
            if (btnRechercher is null) Thread.Sleep(250);
        }
        if (btnRechercher is null)
            throw new Exception("Bouton 'Rechercher' introuvable dans 'Recherche d'audience' (10s)");
        Console.WriteLine($"      → 'Rechercher' trouvé : Type={SafeText(() => btnRechercher.ControlType.ToString())} " +
                          $"AutomationId='{SafeText(() => btnRechercher.AutomationId)}' Name='{SafeText(() => btnRechercher.Name)}'");

        // Poll-jusqu'à-condition locale : la grille contient une row datée dateFr.
        bool WaitGridShowsDate(double maxSec)
        {
            var swPoll = Stopwatch.StartNew();
            while (swPoll.Elapsed.TotalSeconds < maxSec)
            {
                var rows = _window.FindAllDescendants().Where(c =>
                {
                    try { var ct = c.ControlType.ToString(); return ct == "DataItem" || ct == "ListItem"; }
                    catch { return false; }
                }).ToList();
                bool found = rows.Any(r =>
                {
                    try
                    {
                        return r.FindAllChildren().Any(c =>
                            (SafeText(() => c.Name) ?? "").IndexOf(dateFr, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    catch { return false; }
                });
                if (found) return true;
                Thread.Sleep(400);
            }
            return false;
        }

        // Déclenchement multi-API (modèle OpenReclamationViaMenuMultiTry).
        // 1) F7 dans le champ Du — chemin utilisateur OFFICIEL : operation_KeyIntercepted
        //    (OPE_RECHERCHE_AUDCAB.cs:244) fait Focus() (→ LostFocus → COMMITTE la date,
        //    cf. son commentaire « Il faut faire appeler le ChangementValeur de l'ult »)
        //    PUIS Processus.Rechercher(). 2) click PostMessage toolbar ; 3) Invoke ;
        //    4) Entrée. Après chaque essai, on vérifie que la grille liste dateFr.
        const int VK_F7 = 0x76;
        Console.WriteLine("      → F7 dans le champ Du (déclencheur officiel, committe la date)");
        try
        {
            Interaction.Click(dateEdits[0]);
            Thread.Sleep(150);
            Interaction.PressKey(dateEdits[0], VK_F7);
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ F7 a jeté : {ex.Message}"); }
        if (WaitGridShowsDate(8))
        {
            Console.WriteLine($"      → Grille rafraîchie (F7) : audience(s) du {dateFr} listée(s).");
            return;
        }
        Console.WriteLine("      → Pas de refresh après F7 — essai click 'Rechercher' (PostMessage)");
        try { Interaction.Click(btnRechercher); } catch (Exception ex) { Console.WriteLine($"      ⓘ Click a jeté : {ex.Message}"); }
        if (WaitGridShowsDate(8))
        {
            Console.WriteLine($"      → Grille rafraîchie : audience(s) du {dateFr} listée(s).");
            return;
        }
        Console.WriteLine("      → Pas de refresh après click — essai InvokePattern");
        try
        {
            if (btnRechercher.Patterns.Invoke.IsSupported)
                btnRechercher.Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ InvokePattern a jeté : {ex.Message}"); }
        if (WaitGridShowsDate(8))
        {
            Console.WriteLine($"      → Grille rafraîchie (Invoke) : audience(s) du {dateFr} listée(s).");
            return;
        }
        Console.WriteLine("      → Pas de refresh après Invoke — essai touche Entrée dans le champ Du");
        try { Interaction.Click(dateEdits[0]); Thread.Sleep(150); Interaction.PressKey(dateEdits[0], 0x0D); }
        catch (Exception ex) { Console.WriteLine($"      ⓘ PressKey a jeté : {ex.Message}"); }
        if (WaitGridShowsDate(8))
        {
            Console.WriteLine($"      → Grille rafraîchie (Entrée) : audience(s) du {dateFr} listée(s).");
            return;
        }
        throw new Exception($"Après 'Rechercher' (click + Invoke + Entrée), aucune row du {dateFr} dans la grille — date correcte ? audience visible en PREAUD ?");
    }

    /// <summary>
    /// Sélectionne dans la grille RETAUD la ligne dont les cellules contiennent À LA FOIS
    /// la date (ex "15/05/2026") ET l'heure (ex "09:00"). Sert au process E2E où l'on
    /// veut cibler une audience PRÉCISE (peuplée, dans la fenêtre RETAUD) pour qu'un
    /// JSON Rapture matche par date+greffe (Orchestrator Cas A) → recap directe.
    /// </summary>
    public void SelectAudienceInRetaudByDateHeure(string dateFr, string heure)
    {
        if (_window is null) throw new InvalidOperationException("_window null");
        // ML LOOP S1.2 — retry+pid filter pour mode visible-parallèle.
        var btnValider = FindButtonWithRetry("Valider la sélection", timeoutSec: 20.0);
        if (btnValider is null)
            throw new Exception("Bouton 'Valider la sélection' introuvable dans la phase Recherche de PROC_RETAUD (20s, pid-filtered)");
        var rows = _window.FindAllDescendants().Where(c =>
        {
            try { var ct = c.ControlType.ToString(); return ct == "DataItem" || ct == "ListItem"; }
            catch { return false; }
        }).ToList();
        Console.WriteLine($"      → {rows.Count} rows dans la grille, recherche '{dateFr}' + '{heure}'");
        // Collecte tous les matches, puis preferer celui avec le plus d'affaires.
        // ⚠ SAFETY 2026-05-22 : les cellules UIA retournent le texte AFFICHÉ (peut être
        // tronqué visuellement par largeur de colonne, ex "Mise " au lieu de "Mise en
        // état"). Valider une telle row → RIG GetCHAMBRE("Mise ") → throw → mail envoyé.
        // → on PRÉ-FILTRE en blacklistant les rows dont le content révèle troncature
        //    (espace de padding final dans une cellule courte, ou substring "audience i").
        var matches = new System.Collections.Generic.List<(AutomationElement row, string content, int affaireCount)>();
        // Whitelist des codes chambre courts valides (col 4 dans la grille RETAUD greffe 9995).
        // À étendre si nouveau code apparaît. Si content NE contient PAS l'un de ces codes,
        // la row est suspecte (probablement tronquée par UIA) → on skip.
        var knownChambreCodes = new[] { "REF", "AU", "CX", "MD", "PC", "TC", "CC", "JI", "JE", "FT", "CLOT", "DCP" };
        for (int i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].FindAllChildren();
            var cellNames = cells.Select(c => SafeText(() => c.Name)).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var content = string.Join(" | ", cellNames);
            bool hasDate = content.IndexOf(dateFr, StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasHeure = content.IndexOf(heure, StringComparison.OrdinalIgnoreCase) >= 0;
            bool isInteractive = content.IndexOf("audience i", StringComparison.OrdinalIgnoreCase) >= 0
                              || content.IndexOf("interactive", StringComparison.OrdinalIgnoreCase) >= 0;
            // Garde anti-mail-spam : refuse les rows dont aucune cellule ne matche un code
            // chambre connu (= signal d'UIA truncation, RIG plantera sur GetCHAMBRE).
            bool hasKnownChambre = cellNames.Any(c =>
                knownChambreCodes.Any(k => c.Trim().Equals(k, StringComparison.Ordinal)));
            if (hasDate && hasHeure && !isInteractive && hasKnownChambre)
            {
                int affaireCount = cellNames.Select(n =>
                    int.TryParse(n, out var v) ? v : 0
                ).Sum();
                Console.WriteLine($"          row[{i}] ✓ MATCH {dateFr} {heure} (affaires={affaireCount}) : [{content}]");
                matches.Add((rows[i], content, affaireCount));
            }
            else if (hasDate && hasHeure && !isInteractive && !hasKnownChambre)
            {
                // SKIP row suspecte — log pour diag
                Console.WriteLine($"          row[{i}] ⊘ SKIP (chambre non whitelistée, probable troncature UIA) : [{content}]");
            }
        }
        if (matches.Count == 0)
            throw new Exception($"Aucune row VALIDE (date '{dateFr}' + heure '{heure}' + chambre whitelistée) trouvée. Évite mail-spam RIG.");
        var best = matches.OrderByDescending(m => m.affaireCount).First();
        var targetRow = best.row;
        var targetSummary = best.content;
        if (matches.Count > 1)
            Console.WriteLine($"      → {matches.Count} matches : choisi celui avec affaires={best.affaireCount} (vs autres)");
        Console.WriteLine($"      → Sélection : {targetSummary}");
        try { Interaction.Select(targetRow); } catch { }
        Thread.Sleep(500); // sleep-ok: settle sélection row avant Valider (FlaUI, pas de signal pollable)
        Console.WriteLine("      → Click 'Valider la sélection' → passage en phase Saisie PROC_RETAUD");
        Interaction.Click(btnValider);
        Thread.Sleep(1500); // sleep-ok: settle transition phase Saisie post-Valider (rendu WinForms RIG, pas de signal pollable)
    }

    /// <summary>
    /// Sélectionne la row RETAUD par date+heure+CHAMBRE EXACTE (code DB résolu via --audience-id).
    /// ⚠ Finding scout 2026-07-06 : SelectAudienceInRetaudByDateHeure filtre par whitelist fuzzy + exclusion
    /// "interactive" comme garde anti-mail-spam contre une chambre TRONQUÉE par UIA (ex "Mise " → GetCHAMBRE throw).
    /// Ici on connaît la valeur EXACTE attendue (résolue en DB par ID) → on matche DESSUS : c'est SÛR (pas de
    /// devinette de chambre) ET ça autorise les audiences INT (RIG FORM_RETAUD : bouton "Importer Rapture" =
    /// eButtonVisibleOnPhase.Toujours, ImporterJsonRapture teste juste audCab==null → aucun blocage sur le type).
    /// Les méthodes fuzzy existantes restent inchangées (chemins sans ID).
    /// </summary>
    public void SelectAudienceInRetaudByDateHeureChambre(string dateFr, string heure, string expectedChambre)
    {
        if (_window is null) throw new InvalidOperationException("_window null");
        var btnValider = FindButtonWithRetry("Valider la sélection", timeoutSec: 20.0);
        if (btnValider is null)
            throw new Exception("Bouton 'Valider la sélection' introuvable dans la phase Recherche de PROC_RETAUD (20s, pid-filtered)");
        var rows = _window.FindAllDescendants().Where(c =>
        {
            try { var ct = c.ControlType.ToString(); return ct == "DataItem" || ct == "ListItem"; }
            catch { return false; }
        }).ToList();
        var ec = (expectedChambre ?? "").Trim();
        Console.WriteLine($"      → {rows.Count} rows dans la grille, recherche '{dateFr}' + '{heure}' + chambre EXACTE '{ec}'");
        var matches = new System.Collections.Generic.List<(AutomationElement row, string content, int affaireCount)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].FindAllChildren();
            var cellNames = cells.Select(c => SafeText(() => c.Name)).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var content = string.Join(" | ", cellNames);
            bool hasDate = content.IndexOf(dateFr, StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasHeure = content.IndexOf(heure, StringComparison.OrdinalIgnoreCase) >= 0;
            // Match chambre EXACTE (pas de whitelist, pas d'exclusion INT) : une cellule = ec, OU le content
            // contient ec, OU (tolérance troncature UIA) une cellule est un préfixe non-trivial de ec.
            bool hasChambre = ec.Length > 0 && (
                   cellNames.Any(c => c.Trim().Equals(ec, StringComparison.OrdinalIgnoreCase))
                || content.IndexOf(ec, StringComparison.OrdinalIgnoreCase) >= 0
                || cellNames.Any(c => { var t = c.Trim(); return t.Length >= 2 && ec.StartsWith(t, StringComparison.OrdinalIgnoreCase); }));
            if (hasDate && hasHeure && hasChambre)
            {
                int affaireCount = cellNames.Select(n => int.TryParse(n, out var v) ? v : 0).Sum();
                Console.WriteLine($"          row[{i}] ✓ MATCH {dateFr} {heure} chambre~'{ec}' (affaires={affaireCount}) : [{content}]");
                matches.Add((rows[i], content, affaireCount));
            }
        }
        if (matches.Count == 0)
            throw new Exception($"Aucune row (date '{dateFr}' + heure '{heure}' + chambre '{ec}') dans la grille RETAUD.");
        var best = matches.OrderByDescending(m => m.affaireCount).First();
        if (matches.Count > 1)
            Console.WriteLine($"      → {matches.Count} matches : choisi affaires={best.affaireCount}");
        Console.WriteLine($"      → Sélection (chambre exacte, INT autorisé) : {best.content}");
        try { Interaction.Select(best.row); } catch { }
        Thread.Sleep(500); // sleep-ok: settle sélection row avant Valider (FlaUI, pas de signal pollable)
        Console.WriteLine("      → Click 'Valider la sélection' → passage en phase Saisie PROC_RETAUD");
        Interaction.Click(btnValider);
        Thread.Sleep(1500); // sleep-ok: settle transition phase Saisie post-Valider (rendu WinForms RIG, pas de signal pollable)
    }

    /// <summary>
    /// Variante READ-ONLY de SelectAudienceInRetaudByDateHeure sans whitelist chambre.
    /// Utilisee UNIQUEMENT par DriveRetaudPubsEnAttente (aucun mail envoye, aucune mutation).
    /// La contrainte hasKnownChambre est retiree (autorisee pour visualisation).
    /// En cas d'echec, capture un screenshot et retourne false sans throw.
    /// </summary>
    private bool SelectAudienceInRetaudByDateHeureNoWhitelist(string dateFr, string heure, int audienceId)
    {
        if (_window is null)
        {
            Console.WriteLine("      ⚠ SelectAudienceInRetaudByDateHeureNoWhitelist : _window null");
            return false;
        }

        var btnValider = FindButtonWithRetry("Valider la sélection", timeoutSec: 20.0);
        if (btnValider is null)
        {
            Console.WriteLine($"      ⚠ 'Valider la sélection' introuvable (20s) — capture diag.");
            CaptureScreenshot($"retaud-pubs-diag-valider-absent-{audienceId}");
            return false;
        }

        // Poll borné ~20s : re-énumère la grille jusqu'à trouver la ligne date+heure.
        // Le refresh post-recherche peut arriver après la 1re énumération, et le RigListView
        // peut n'exposer le texte des colonnes qu'au niveau DESCENDANT (pas enfants directs)
        // ou via LegacyIAccessible.Value → on lit le texte de ligne de façon ROBUSTE.
        AutomationElement? targetRow = null;
        string targetSummary = "";
        var swFind = Stopwatch.StartNew();
        bool dumped = false;
        int lastRowCount = -1;
        while (swFind.Elapsed.TotalSeconds < 20 && targetRow is null)
        {
            var rows = _window.FindAllDescendants().Where(c =>
            {
                try { var ct = c.ControlType.ToString(); return ct == "DataItem" || ct == "ListItem"; }
                catch { return false; }
            }).ToList();
            lastRowCount = rows.Count;

            for (int i = 0; i < rows.Count; i++)
            {
                var parts = new System.Collections.Generic.List<string>();
                try { foreach (var c in rows[i].FindAllChildren()) { var n = SafeText(() => c.Name); if (!string.IsNullOrEmpty(n)) parts.Add(n); } } catch { }
                try { foreach (var c in rows[i].FindAllDescendants()) { var n = SafeText(() => c.Name); if (!string.IsNullOrEmpty(n)) parts.Add(n); } } catch { }
                try { var rn = SafeText(() => rows[i].Name); if (!string.IsNullOrEmpty(rn)) parts.Add(rn); } catch { }
                try { if (rows[i].Patterns.LegacyIAccessible.IsSupported) { var v = SafeText(() => rows[i].Patterns.LegacyIAccessible.Pattern.Value); if (!string.IsNullOrEmpty(v)) parts.Add(v); } } catch { }
                var content = string.Join(" | ", parts.Distinct());

                if (!dumped)
                    Console.WriteLine($"          [DIAG] row[{i}] = [{content}]");

                bool hasDate  = content.IndexOf(dateFr, StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasHeure = content.IndexOf(heure,  StringComparison.OrdinalIgnoreCase) >= 0;
                bool isInteractive = content.IndexOf("audience i",  StringComparison.OrdinalIgnoreCase) >= 0
                                  || content.IndexOf("interactive", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hasDate && hasHeure && !isInteractive)
                {
                    targetRow = rows[i];
                    targetSummary = content;
                    Console.WriteLine($"          row[{i}] ✓ MATCH {dateFr} {heure} (no-whitelist) : [{content}]");
                    break;
                }
            }
            dumped = true;
            if (targetRow is null) Thread.Sleep(500);
        }

        if (targetRow is null)
        {
            // UIA n'expose souvent que la 1re colonne (jour) des lignes de ce RigListView
            // → matching date+heure impossible. Fallback : la grille a été filtrée sur dateFr
            // (SearchAudiencesByDate), donc TOUTES les lignes sont du bon jour ; on prend la
            // DERNIÈRE (heure la plus tardive en tri ascendant = audience cible 15:00).
            // Lecture seule → sélectionner/charger une audience n'envoie aucun mail.
            var rows2 = _window.FindAllDescendants().Where(c =>
            {
                try { var ct = c.ControlType.ToString(); return ct == "DataItem" || ct == "ListItem"; }
                catch { return false; }
            }).ToList();
            if (rows2.Count > 0)
            {
                targetRow = rows2[rows2.Count - 1];
                targetSummary = (SafeText(() => targetRow.Name) ?? $"row#{rows2.Count - 1}") + $" (fallback index, {rows2.Count} lignes)";
                Console.WriteLine($"      → Fallback index : UIA n'expose que le jour → sélection de la DERNIÈRE ligne ({rows2.Count} lignes) : [{targetSummary}]");
            }
            else
            {
                Console.WriteLine($"      ⚠ Aucune ligne dans la grille ({lastRowCount} vues) — capture diag.");
                CaptureScreenshot($"retaud-pubs-diag-norow-{audienceId}");
                return false;
            }
        }

        Console.WriteLine($"      → Sélection (no-whitelist) : {targetSummary}");
        try { Interaction.Select(targetRow); } catch { }
        Thread.Sleep(400);

        Console.WriteLine("      → Click 'Valider la sélection' (no-whitelist) → phase Saisie PROC_RETAUD");
        try
        {
            Interaction.Click(btnValider);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ Click 'Valider la sélection' a jeté : {ex.GetType().Name}: {ex.Message}");
            CaptureScreenshot($"retaud-pubs-diag-valider-click-fail-{audienceId}");
            return false;
        }

        // Poll borné 6s : attend que 'Valider la sélection' disparaisse
        // (preuve que RIG a transitionné vers la phase Saisie).
        var swPost = Stopwatch.StartNew();
        while (swPost.Elapsed.TotalSeconds < 6)
        {
            var stillPresent = FindButtonWithRetry("Valider la sélection", timeoutSec: 0.1);
            if (stillPresent is null)
            {
                Console.WriteLine($"      → Transition phase Saisie confirmée ({swPost.Elapsed.TotalSeconds:F1}s).");
                break;
            }
            Thread.Sleep(400);
        }
        return true;
    }

    private bool TryLaunchKbis(AutomationElement item, string source)
    {
        Console.WriteLine($"      → Processus VK candidat via {source} : Type={item.ControlType} Name='{item.Name}'");
        int TabCount()
        {
            try
            {
                var tc = FindByAutomationId("tabControl");
                return tc?.FindAllChildren().Length ?? -1;
            }
            catch { return -1; }
        }
        int before = TabCount();
        try
        {
            var kbisList = FindByAutomationId("lstProcessus");
            if (kbisList is null)
                throw new InvalidOperationException("lstProcessus introuvable pour lancer KBIS");
            Interaction.ActivateListItem(item, kbisList);
            Console.WriteLine("      → Launch via Entrée (RigListView KeyDown handler)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      → Double-clic jeté ({ex.GetType().Name}), fallback Click+Enter");
            try { Interaction.Click(item); Thread.Sleep(200); Interaction.PressEnter(item); }
            catch { /* best-effort */ }
        }
        // Vérifie qu'un NOUVEAU tab est apparu (preuve que le processus s'est lancé).
        // VK = automate lourd (COM/Vintasoft) → on laisse jusqu'à 8s.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 8)
        {
            int now = TabCount();
            if (now > before && before >= 0)
            {
                Console.WriteLine($"      → ✓ Tab ouvert (tabs {before}→{now}) après {sw.Elapsed.TotalSeconds:F1}s");
                return true;
            }
            Thread.Sleep(400);
        }
        Console.WriteLine($"      → ✗ Aucun nouveau tab après 8s (tabs restés à {before}) — launch raté via {source}");
        return false;
    }

    /// <summary>
    /// Cherche un SIREN dans le plugin KBIS et vérifie qu'il s'affiche.
    ///
    /// Étapes :
    /// 1. Trouve la zone de saisie : essaie d'abord les AutomationId connus
    ///    (<c>txtAffichage</c>, <c>ultNumeroMetier</c>, <c>txtNumIdent</c>…), sinon
    ///    fallback sur le 1er control Edit du tab actif.
    /// 2. Tape le SIREN (env <c>RIG_LEGACY_SIREN</c> ou défaut <paramref name="defaultSiren"/>).
    /// 3. Trouve le bouton recherche : <c>RECHERCHER</c> AutomationId (RigToolBar ToolStripButton)
    ///    ou Name "Rechercher".
    /// 4. Vérifie qu'au moins une indication post-recherche apparaît (un Name contenant
    ///    le SIREN, une dénomination, ou des controls dossier comme <c>ultNumGestion</c>).
    ///
    /// Dump UIA détaillé du tab à chaque étape qui échoue.
    /// </summary>
    public void SearchKbisSiren(string defaultSiren, int waitResultSeconds = 15)
    {
        if (_window is null || _app is null || _automation is null)
            throw new InvalidOperationException("ClickSeConnecter() + OpenProcKbis() doivent être appelés avant SearchKbisSiren()");

        var siren = Environment.GetEnvironmentVariable("RIG_LEGACY_SIREN");
        if (string.IsNullOrWhiteSpace(siren)) siren = defaultSiren;
        Console.WriteLine($"      → SIREN cible : '{siren}' (env RIG_LEGACY_SIREN sinon défaut)");

        // Pattern VK (Visualisation - Extrait RCS) = automate VB6 :
        //   1. Click "Rechercher..." dans le tab VK → ouvre une fenêtre modale RigOcxRecherche
        //   2. Dans la modale : tape SIREN dans un Edit + click Valider/OK
        //   3. La modale se ferme, le dossier s'affiche dans le tab VK

        // 1) Click Rechercher... (substring "echerch" → match "Rechercher...")
        var rechercheBtn = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => SafeText(() => b.Name).IndexOf("echerch", StringComparison.OrdinalIgnoreCase) >= 0);
        if (rechercheBtn is null)
            throw new Exception("Bouton 'Rechercher...' introuvable dans le tab VK");
        Console.WriteLine($"      → Click 'Rechercher...' AutomationId='{SafeText(() => rechercheBtn.AutomationId)}'");
        Interaction.Click(rechercheBtn);
        Thread.Sleep(1500);

        // 2) Détecte la modale "Recherche d'un dossier RCS". Note : elle apparaît comme
        //    Window DESCENDANT de la main window (child window UI), PAS comme top-level
        //    du process. On la trouve par Name partiel "Recherche".
        var sw = Stopwatch.StartNew();
        AutomationElement? popup = null;
        while (sw.Elapsed.TotalSeconds < 8 && popup is null)
        {
            popup = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                .FirstOrDefault(w =>
                {
                    var t = SafeText(() => w.Name);
                    return t.IndexOf("recherch", StringComparison.OrdinalIgnoreCase) >= 0
                        && t.IndexOf("RigFormAutomateVB6", StringComparison.OrdinalIgnoreCase) < 0;
                });
            if (popup is null) Thread.Sleep(500);
        }

        if (popup is null)
        {
            Console.WriteLine("      → Modale 'Recherche…' introuvable. Dump :");
            DumpDescendants(_window, maxDepth: 4, maxLines: 100);
            throw new Exception("Modale 'Recherche d'un dossier RCS' introuvable après click sur 'Rechercher...'");
        }
        Console.WriteLine($"      → Modale trouvée : Name='{SafeText(() => popup.Name)}'. Dump enrichi avec positions :");
        DumpDescendantsWithPositions(popup, maxDepth: 5, maxLines: 60);

        // 3) Cible le champ "Numéro Ident." par position : dans le groupe 'Criteres Entreprise',
        //    les Edits forment une grille 2 colonnes × 4 lignes :
        //      Y=204 (Raison Sociale | Adresse)
        //      Y=228 (Numéro Ident.  | Code postal)   ← SIREN va ici
        //      Y=252 (Dirigeant      | Ville)
        //      Y=276 (Activité       | Date Immat)
        //    Le SIREN field = colonne gauche (X ≈ 125), 2ème ligne (Y ≈ 228), large (W ≈ 257).
        //    Le Code postal a une width étroite (73px) → easy à exclure.
        var popupEdits = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        Console.WriteLine($"      → Popup : {popupEdits.Length} Edits.");

        // Sort par (Y, X) ascendant
        var sortedEdits = popupEdits
            .Select(e => { var r = e.BoundingRectangle; return (edit: e, x: (int)r.X, y: (int)r.Y, w: (int)r.Width); })
            .Where(t => t.w > 100) // exclut Code postal (73px) et autres petits champs
            .OrderBy(t => t.y).ThenBy(t => t.x)
            .ToList();
        Console.WriteLine($"      → {sortedEdits.Count} Edits 'larges' triés par (Y,X) :");
        for (int i = 0; i < sortedEdits.Count; i++)
            Console.WriteLine($"          [{i}] id='{sortedEdits[i].edit.AutomationId}' ({sortedEdits[i].x},{sortedEdits[i].y} w={sortedEdits[i].w})");

        // SIREN = 3ème dans l'ordre (Y, X) = (Y=228, X≈125)
        if (sortedEdits.Count < 3)
            throw new Exception($"Layout inattendu : seulement {sortedEdits.Count} Edits 'larges' dans le popup (attendu ≥ 3)");
        var sirenInput = sortedEdits[2].edit;
        Console.WriteLine($"      → Champ 'Numéro Ident.' (SIREN) : Edit '{sirenInput.AutomationId}' à ({sortedEdits[2].x},{sortedEdits[2].y})");

        try
        {
            Interaction.SetText(sirenInput, siren);
            Thread.Sleep(300);
            string? readBack = null;
            try { readBack = sirenInput.AsTextBox().Text; } catch { }
            Console.WriteLine($"      → SIREN renseigné via SetText : readback='{readBack ?? "<no VP>"}'");
            if (readBack is null || !readBack.Contains(siren))
                throw new Exception($"Le SIREN n'a pas été accepté par le champ Numéro Ident. (readback='{readBack}')");
        }
        catch (Exception ex) { throw new Exception("Échec saisie SIREN : " + ex.Message); }

        // 4) Click 'Rechercher (F7)' pour lancer la recherche.
        var btnRechercher = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => SafeText(() => b.Name).IndexOf("Rechercher (F7)", StringComparison.OrdinalIgnoreCase) >= 0);
        if (btnRechercher is not null)
        {
            Console.WriteLine($"      → Click 'Rechercher (F7)'");
            Interaction.Click(btnRechercher);
        }
        Thread.Sleep(3000); // laisse la BDD répondre

        // 4b) Détecte le popup d'erreur "Vos critères n'ont permis de trouver aucun dossier"
        var errorPopup = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
            .FirstOrDefault(w =>
            {
                var n = SafeText(() => w.Name);
                return n.IndexOf("RIG_Client_RechRCS", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("aucun dossier", StringComparison.OrdinalIgnoreCase) >= 0;
            });
        if (errorPopup is not null)
        {
            Console.WriteLine($"      → ⚠ Popup d'erreur détecté : '{SafeText(() => errorPopup.Name)}'");
            // Dump pour voir le message exact
            var msgTexts = errorPopup.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            foreach (var t in msgTexts)
            {
                var msg = SafeText(() => t.Name);
                if (!string.IsNullOrEmpty(msg) && msg.Length > 5)
                    Console.WriteLine($"          message : '{msg}'");
            }
            // Cleanup : click OK pour fermer
            var okBtn = errorPopup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => SafeText(() => b.Name).Equals("OK", StringComparison.OrdinalIgnoreCase));
            if (okBtn is not null) { try { Interaction.Click(okBtn); } catch { } }
            // Fermer aussi le popup de recherche (sinon le RigClientAccueil reste bloqué)
            Thread.Sleep(500);
            var btnAnnuler = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => SafeText(() => b.Name).IndexOf("Annuler", StringComparison.OrdinalIgnoreCase) >= 0);
            if (btnAnnuler is not null) { try { Interaction.Click(btnAnnuler); } catch { } }
            throw new Exception($"SIREN '{siren}' introuvable dans RIG_DEV — override via $env:RIG_LEGACY_SIREN = <SIREN existant en BDD> puis relance.");
        }

        // 5) Click 'Valider la recherche (F12)' pour fermer le popup et charger le dossier
        var btnValider = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => SafeText(() => b.Name).IndexOf("Valider", StringComparison.OrdinalIgnoreCase) >= 0);
        if (btnValider is not null)
        {
            Console.WriteLine($"      → Click 'Valider' : Name='{SafeText(() => btnValider.Name)}'");
            Interaction.Click(btnValider);
        }
        else
        {
            // Fallback : bouton 'Valider' introuvable → on envoie F12 directement sur le champ SIREN,
            // ce qui reproduit exactement le comportement original (VK_F12) de la modale RigOcxRecherche.
            Console.WriteLine("      → 'Valider' introuvable, fallback PressF12 sur le champ SIREN (comportement F12 original préservé)");
            try { Interaction.PressF12(sirenInput); } catch { /* best-effort */ }
        }
        Thread.Sleep(2000);

        // 5) Vérifie que la popup est fermée + que le dossier s'affiche dans le tab VK.
        //    On cible spécifiquement la pageTab VK pour exclure le popup encore visible.
        Console.WriteLine($"      → Wait {waitResultSeconds}s + check résultat dans le tab VK…");
        bool resultDetected = false;
        string? matchedHint = null;
        var sw2 = Stopwatch.StartNew();
        while (sw2.Elapsed.TotalSeconds < waitResultSeconds && !resultDetected)
        {
            // Cible la pageTab VK (descendant), pas la fenêtre globale (qui contient le popup).
            var kbisTab = _window.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Pane).And(cf.ByAutomationId("pagetabVK")));
            if (kbisTab is null) { Thread.Sleep(500); continue; }

            // Match strict : un Edit DANS le tab VK a maintenant le SIREN comme valeur
            var tabEdits = kbisTab.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            foreach (var e in tabEdits)
            {
                string? v = null;
                try { v = e.AsTextBox().Text; } catch { }
                if (!string.IsNullOrEmpty(v) && v.Contains(siren))
                {
                    resultDetected = true;
                    matchedHint = $"Edit '{SafeText(() => e.AutomationId)}' du tab VK value='{v}'";
                    break;
                }
            }
            // Ou un libellé/control contient le SIREN
            if (!resultDetected)
            {
                var byNameSiren = kbisTab.FindFirstDescendant(cf => cf.ByName(siren));
                if (byNameSiren is not null) { resultDetected = true; matchedHint = $"Element Name='{siren}' dans tab VK"; }
            }
            if (!resultDetected) Thread.Sleep(500);
        }

        if (!resultDetected)
        {
            Console.WriteLine("      → SIREN pas affiché dans le tab VK. Dump tab :");
            var kbisTab = _window.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Pane).And(cf.ByAutomationId("pagetabVK")));
            if (kbisTab is not null) DumpDescendants(kbisTab, maxDepth: 4, maxLines: 80);
            throw new Exception($"Le SIREN '{siren}' ne s'affiche pas dans le tab VK après {waitResultSeconds}s — la validation n'a pas chargé le dossier (Valider F12 nécessite peut-être de sélectionner un résultat avant).");
        }
        Console.WriteLine($"      → ✓ Dossier KBIS chargé : {matchedHint}");
    }

    /// <summary>
    /// Vérifie qu'un nouveau tab a été ajouté au tabControl principal après le double-clic
    /// sur l'item KBIS, et que ce tab a un libellé contenant "kbis" / "k-bis". À l'état
    /// initial le tabControl contient 2 tabs (&amp;Accueil, &amp;Demandes) — après ouverture
    /// de PROC_KBIS un 3ème apparaît.
    /// </summary>
    public void VerifyKbisTabOpened(int waitSeconds = 10)
    {
        if (_window is null) throw new InvalidOperationException("ClickSeConnecter() doit être appelé avant VerifyKbisTabOpened()");

        var sw = Stopwatch.StartNew();
        AutomationElement? kbisTab = null;
        int tabCount = 0;
        string tabNames = "";
        while (sw.Elapsed.TotalSeconds < waitSeconds && kbisTab is null)
        {
            var tabControl = FindByAutomationId("tabControl");
            if (tabControl is not null)
            {
                var tabs = tabControl.FindAllChildren();
                tabCount = tabs.Length;
                tabNames = string.Join(" / ", tabs.Select(t => "'" + SafeText(() => t.Name) + "'"));
                // Le processus VK ("Visualisation - Extrait RCS") n'a PAS "kbis"
                // dans le Name du tab. On matche : AutomationId == pagetabVK,
                // OU Name contient "VK" / "visualisation" / "rcs" / "extrait".
                kbisTab = tabs.FirstOrDefault(t =>
                {
                    var n = SafeText(() => t.Name);
                    var aid = SafeText(() => t.AutomationId);
                    if (aid.Equals("pagetabVK", StringComparison.OrdinalIgnoreCase)) return true;
                    return n.Equals("VK", StringComparison.OrdinalIgnoreCase)
                        || n.IndexOf("visualisation", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("extrait rcs", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("rcs", StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }
            if (kbisTab is null) Thread.Sleep(400);
        }
        Console.WriteLine($"      → tabControl après KBIS open : {tabCount} tabs : {tabNames}");
        if (kbisTab is null)
            throw new Exception($"Pas de tab KBIS dans tabControl après {waitSeconds}s — le plugin n'a pas chargé (TypeLoadException ? dépendance native manquante ? cf. logs RIG)");
        Console.WriteLine($"      → Tab KBIS détecté : '{SafeText(() => kbisTab.Name)}' (en {sw.Elapsed.TotalSeconds:F1}s)");
    }

    private AutomationElement? FindByAutomationId(string id)
    {
        if (_window is null) return null;
        var sw = Stopwatch.StartNew();
        var el = _window.FindFirstDescendant(cf => cf.ByAutomationId(id));
        sw.Stop();
        if (sw.ElapsedMilliseconds > 700)
            Console.WriteLine($"      [DIAG] FindByAutomationId('{id}') = {sw.ElapsedMilliseconds}ms -> {(el is null ? "null" : "ok")}");
        return el;
    }

    /// <summary>
    /// Mode visible-parallèle fix : retry pattern + filter ProcessId pour
    /// FindByAutomationId. En mode //, 4 RIG processes hammer simultanément
    /// la UIA tree → un FindFirstDescendant peut transientement return null
    /// même quand l'élément existe. Cf. bug "PROC_RETAUD introuvable après
    /// scan complet btn1..btn7" du 2026-05-26.
    ///
    /// Poll jusqu'à <paramref name="timeoutMs"/>ms, sleep <paramref name="pollMs"/>ms entre essais.
    /// Filtre par _app.ProcessId si dispo (évite cross-process UIA shortcut).
    /// </summary>
    /// <summary>
    /// Active une rail-tab btn{n} de la Console d'accueil RIG. Essaye dans
    /// l'ordre : SelectionItem.Select() → Invoke.Invoke() → LegacyIAccessible
    /// → Interaction.Click fallback (PostMessage WM_LBUTTON).
    ///
    /// ML LOOP fix visible-parallel : les btn{n} hors viewport ne reçoivent
    /// pas de PostMessage WM_LBUTTON (coords offscreen). UIA patterns
    /// (Select/Invoke) bypass le hit-test visuel → switch quel que soit
    /// l'état du scroll/viewport.
    /// </summary>
    private bool TryActivateRailTab(AutomationElement btn, string label)
    {
        if (btn is null) return false;

        // ITER 5 — Interaction.Click PRIMARY. Analyse FORM_ACCUEIL.cs (Explore agent) :
        //   btn1..btn7 wirent tous le même handler `toolbarOnglet_Click` sur l'event `.Click`.
        //   RigPanel = Panel WinForms standard, fenêtré, propre hwnd. SEUL WM_LBUTTONDOWN/UP
        //   posté sur ce hwnd raise OnClick → toolbarOnglet_Click → _SelectMenuOngletByOnglet
        //   → _ChargerSousmenu (populate lstSousmenu). Les UIA patterns Invoke/SelectionItem/
        //   LegacyIAccessible NE FIRENT PAS .Click sur un Panel (pas de provider Invoke natif).
        //   D'où le bug iter 2-4 : les patterns retournaient true (provider présent) mais
        //   .Click ne fire jamais — lstSousmenu reste sur son état boot (RCS).
        // DIAG : log hwnd + rect pour confirmer que btn3+ exposent bien leur hwnd.
        int hwnd = 0;
        try { if (btn.Properties.NativeWindowHandle.IsSupported) hwnd = btn.Properties.NativeWindowHandle.ValueOrDefault.ToInt32(); } catch { }
        var br = btn.BoundingRectangle;
        Console.WriteLine($"      [DIAG] {label} hwnd=0x{hwnd:X} rect=({(int)br.X},{(int)br.Y},{(int)br.Width}x{(int)br.Height})");

        try
        {
            Interaction.Click(btn);
            Console.WriteLine($"      [DIAG] {label} activé via Interaction.Click (WM_LBUTTONDOWN/UP)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      [DIAG] {label} Interaction.Click jeté : {ex.GetType().Name}: {ex.Message}");
        }

        // Fallback UIA patterns (rarement utile pour RigPanel mais ne coûte rien d'essayer).
        try
        {
            try { btn.Focus(); } catch { }
            if (btn.Patterns.Invoke.IsSupported)
            { btn.Patterns.Invoke.Pattern.Invoke(); Console.WriteLine($"      [DIAG] {label} fallback Invoke"); return true; }
            if (btn.Patterns.LegacyIAccessible.IsSupported)
            { btn.Patterns.LegacyIAccessible.Pattern.DoDefaultAction(); Console.WriteLine($"      [DIAG] {label} fallback LegacyIAccessible"); return true; }
            if (btn.Patterns.SelectionItem.IsSupported)
            { btn.Patterns.SelectionItem.Pattern.Select(); Console.WriteLine($"      [DIAG] {label} fallback SelectionItem"); return true; }
        }
        catch (Exception ex) { Console.WriteLine($"      [DIAG] {label} fallback UIA jeté : {ex.GetType().Name}: {ex.Message}"); }
        return false;
    }

    private AutomationElement? FindByAutomationIdWithRetry(string id, int timeoutMs = 2000, int pollMs = 100)
    {
        if (_window is null) return null;
        int? appPid = null;
        try { appPid = _app?.ProcessId; } catch { }

        var sw = Stopwatch.StartNew();
        int attempts = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            attempts++;
            try
            {
                var el = _window.FindFirstDescendant(cf => cf.ByAutomationId(id));
                if (el != null)
                {
                    if (appPid.HasValue)
                    {
                        try
                        {
                            var pid = el.Properties.ProcessId.ValueOrDefault;
                            if (pid != 0 && pid != appPid.Value)
                            {
                                Console.WriteLine($"      ⚠ FindByAutomationIdWithRetry('{id}') trouvé mais pid={pid} ≠ _app.pid={appPid} — reject");
                                el = null;
                            }
                        }
                        catch { }
                    }
                    if (el != null)
                    {
                        if (attempts > 1)
                            Console.WriteLine($"      [DIAG] FindByAutomationIdWithRetry('{id}') trouvé au {attempts}e essai en {sw.ElapsedMilliseconds}ms");
                        return el;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ⚠ FindByAutomationIdWithRetry('{id}') attempt#{attempts} threw {ex.GetType().Name}: {ex.Message}");
            }
            Thread.Sleep(pollMs);
        }
        return null;
    }

    private AutomationElement? FindByName(string name)
    {
        if (_window is null) return null;
        return _window.FindFirstDescendant(cf => cf.ByName(name));
    }

    /// <summary>
    /// Attend (poll) que le bouton login de FormLogin soit RENDU, puis le retourne.
    /// Root cause 2026-05-29 (3/4 XEX en échec au login sous stress 4× parallèle) :
    /// <see cref="Launch"/> accepte la main window dès qu'elle est UIA-attachable, SANS attendre
    /// la fin du Load de FormLogin. Sous cold-boot 4× simultané (CPU/UIA saturés), la fenêtre est
    /// capturée pré-Load (titre='', btnOk pas encore créé) ; la recherche ONE-SHOT du bouton dans
    /// <see cref="ClickSeConnecter"/> ratait alors le bouton, qui se rend ~100ms–2s plus tard (le
    /// self-snap montre la FormLogin bien rendue APRÈS l'échec). Ici on poll le bouton (btnOk /
    /// "Se connecter" / variantes) jusqu'à readiness — condition-based-waiting, pas de sleep blind.
    /// </summary>
    private AutomationElement? WaitForLoginButton(int timeoutMs = 20000, int pollMs = 200)
    {
        if (_window is null) return null;
        var sw = Stopwatch.StartNew();
        int attempts = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            attempts++;
            AutomationElement? btn = null;
            try
            {
                // RigButton extends RigPanel → UIA Type=Pane. AutomationId = WinForms control.Name
                // (btnOk) ; le designer dit btnOk.Text="Se &connecter" → UIA Name = "Se connecter".
                // FormLogin est un ShowDialog() MODAL → souvent une toplevel UIA HORS de l'arbre de
                // _window (surtout sur HDESK). On cherche depuis la racine desktop du HDESK (qui ne
                // contient que les fenêtres RIG → pas de faux positif), fallback _window si GetDesktop KO.
                AutomationElement root = _window;
                try { var d = _automation.GetDesktop(); if (d is not null) root = d; } catch { }
                btn = root.FindFirstDescendant(cf => cf.ByAutomationId("btnOk"))
                   ?? root.FindFirstDescendant(cf => cf.ByName("Se connecter"))
                   ?? root.FindFirstDescendant(cf => cf.ByName("Connexion"))
                   ?? root.FindFirstDescendant(cf => cf.ByName("OK"));
            }
            catch (Exception ex)
            {
                // Tree UIA transitoirement instable sous contention 4× → re-essaie au tour suivant.
                if (attempts == 1)
                    Console.WriteLine($"      [DIAG] WaitForLoginButton attempt#1 threw {ex.GetType().Name}: {ex.Message}");
            }
            if (btn is not null)
            {
                if (attempts > 1)
                    Console.WriteLine($"      → Bouton login rendu au {attempts}e essai en {sw.ElapsedMilliseconds}ms (FormLogin Load terminé)");
                return btn;
            }
            Thread.Sleep(pollMs);
        }
        return null;
    }

    /// <summary>Dump récursif des descendants UIA (AutomationId, Name, ControlType) — diag uniquement.</summary>
    private static void DumpDescendants(AutomationElement el, int maxDepth, int depth = 0, int maxLines = 80)
    {
        if (depth > maxDepth) return;
        try
        {
            foreach (var child in el.FindAllChildren())
            {
                if (maxLines-- <= 0) return;
                string id = "", nm = "", ct = "";
                try { id = child.AutomationId ?? ""; } catch { }
                try { nm = child.Name ?? ""; } catch { }
                try { ct = child.ControlType.ToString(); } catch { }
                Console.WriteLine($"          {new string(' ', depth * 2)}[{ct}] id='{id}' name='{nm}'");
                DumpDescendants(child, maxDepth, depth + 1, maxLines);
            }
        }
        catch { }
    }

    /// <summary>
    /// Clique le bouton "K-bis" / "Visualiser K-bis" dans la toolbar tbAutomate du tab VK
    /// (devenu visible après chargement du dossier), puis observe ce qui se passe :
    ///   - Nouveau process (SumatraPDF, AcroRd32, msedge, …) ?
    ///   - Nouveau top-level window dans le process RIG ?
    ///   - Nouveau fichier PDF en zone temp ?
    /// Le smoke réussit si AU MOINS UN signal "le K-bis s'est affiché" est détecté.
    /// </summary>
    /// <returns>Chemin du PDF K-bis généré (le plus récent apparu depuis le click), ou null si
    /// aucun fichier sur disque (rendu OCX/viewer embarqué) ou path-B idempotent.</returns>
    public string? OpenKbisDocument(int waitSeconds = 30)
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("SearchKbisSiren() doit avoir réussi avant OpenKbisDocument()");

        // 1) Cible la pageTab VK. Le bouton K-bis peut être :
        //    - Un vrai Button (UIA Type=Button)
        //    - Un RigButton custom (extends RigPanel → UIA Type=Pane) avec un Name "K-bis"…
        //    - Dans tbAutomate, OU dans un toolbar/panel du form chargé après dossier
        //    On cherche donc sur TOUS les controls par substring Name "kbis"/"k-bis".
        var pageTab = _window.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Pane).And(cf.ByAutomationId("pagetabVK")));
        var kbisRoot = pageTab ?? _window;

        var allControls = kbisRoot.FindAllDescendants();
        Console.WriteLine($"      → pageTab : {allControls.Length} descendants total.");

        // 2 états possibles selon que ce SIREN a déjà été chargé via cet automate VK :
        //
        // (A) PREMIER PASSAGE : le bouton "Charger ce dossier" est visible → click pour
        //     déclencher la génération du K-bis (crée une demande de modification en BDD).
        //
        // (B) PASSAGE SUIVANT : bouton absent + message rouge "Il y a déjà une demande de
        //     modification en cours sur ce dossier" + numéro de demande (ex. D2613500028) →
        //     preuve qu'on a déjà chargé. C'est un succès aussi (smoke idempotent).
        //
        // Note : le side-effect "crée une demande" n'est pas idéal — on accepte les 2 états
        // pour ne pas saturer la BDD avec une demande par run.
        // Note : le label rouge est splité en 3 Text UIA séparés :
        //   "Il y a déjà une demande de" / "modification en cours sur ce" / "dossier"
        // On matche juste le 1er fragment "déjà une demande".
        var existingDemandeText = allControls.Where(c =>
            c.ControlType == ControlType.Text
            && SafeText(() => c.Name).IndexOf("déjà une demande", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault();
        if (existingDemandeText is not null)
        {
            // Cherche le numéro de demande (ex. 'D2613500028') affiché à côté
            var demandeNumber = allControls
                .Select(c => SafeText(() => c.Name))
                .Where(n => LegacyParsing.IsNumDemandeToken(n))
                .FirstOrDefault();
            // Aussi : peut être dans un Edit (value via ValuePattern)
            if (string.IsNullOrEmpty(demandeNumber))
            {
                foreach (var e in allControls.Where(c => c.ControlType == ControlType.Edit))
                {
                    string? v = null; try { v = e.AsTextBox().Text; } catch { }
                    if (LegacyParsing.IsNumDemandeToken(v))
                    { demandeNumber = v; break; }
                }
            }
            Console.WriteLine($"      → ✓ Dossier déjà chargé via demande de modification existante : '{demandeNumber ?? "(numéro non extrait)"}'");
            if (!string.IsNullOrWhiteSpace(demandeNumber))
            {
                try { VerifyKbisDemandeInDb(demandeNumber); }
                catch (Exception ex)
                {
                    throw new Exception($"UI affiche une demande '{demandeNumber}' mais vérif BDD a échoué : {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("      → ⚠ Numéro de demande non extrait depuis l'UI — preuve BDD non vérifiée");
            }
            Console.WriteLine($"      → Smoke idempotent (path B) : run précédent a déjà déclenché le K-bis. PASS.");
            return null; // path B : pas de nouveau fichier PDF à vérifier
        }

        var kbisCandidates = allControls.Where(c =>
        {
            if (c.ControlType == ControlType.Window) return false;
            if (c.ControlType != ControlType.Button && c.ControlType != ControlType.Pane) return false;
            var n = SafeText(() => c.Name);
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("kbis", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("k-bis", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("k bis", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("visualis", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("charger ce dossier", StringComparison.OrdinalIgnoreCase) >= 0;
        }).ToList();

        AutomationElement? kbisBtn = null;
        if (kbisCandidates.Count > 0)
        {
            Console.WriteLine($"      → {kbisCandidates.Count} candidat(s) 'kbis'/'charger' :");
            foreach (var c in kbisCandidates)
                Console.WriteLine($"          - Type={c.ControlType} AutomationId='{SafeText(() => c.AutomationId)}' Name='{SafeText(() => c.Name)}'");
            kbisBtn = kbisCandidates.FirstOrDefault(c => c.ControlType == ControlType.Button)
                   ?? kbisCandidates.FirstOrDefault(c => c.ControlType == ControlType.Pane)
                   ?? kbisCandidates.First();
        }

        // Fallback : pas de match Name "kbis" — dump TOUT le contenu utile de la pageTab
        // (Button, Pane, Text) avec leurs positions, pour voir où est le bouton K-bis.
        if (kbisBtn is null)
        {
            Console.WriteLine("      → Aucun match Name. Dump complet (Button/Pane/Text avec Name OR id non vide) :");
            int n = 0;
            foreach (var c in allControls)
            {
                if (c.ControlType != ControlType.Button
                 && c.ControlType != ControlType.Pane
                 && c.ControlType != ControlType.Text) continue;
                var nm = SafeText(() => c.Name);
                var id = SafeText(() => c.AutomationId);
                if (string.IsNullOrEmpty(nm) && string.IsNullOrEmpty(id)) continue;
                // Strip "Veuillez patienter / Traitement en cours" = bruit
                if (nm.StartsWith("Veuillez patienter", StringComparison.OrdinalIgnoreCase)) nm = "<patienter>";
                System.Drawing.Rectangle r = default;
                try { var bb = c.BoundingRectangle; r = new System.Drawing.Rectangle((int)bb.X, (int)bb.Y, (int)bb.Width, (int)bb.Height); } catch { }
                Console.WriteLine($"          [{n++}] {c.ControlType} id='{id}' name='{nm}' ({r.X},{r.Y} {r.Width}×{r.Height})");
                if (n > 80) break;
            }
            throw new Exception("Bouton K-bis introuvable. Vu les controls ci-dessus, identifie-le pour corriger le matcher.");
        }

        Console.WriteLine($"      → Bouton K-bis retenu : Type={kbisBtn.ControlType} Name='{SafeText(() => kbisBtn.Name)}'");

        // 3) Capture baseline AVANT click : process tree + top-level windows + fichiers PDF temp
        var procsBefore = Process.GetProcesses()
            .Select(p => { try { return (Id: p.Id, Name: p.ProcessName); } catch { return (Id: -1, Name: ""); } })
            .Where(t => t.Id > 0).ToHashSet();
        Window[] windowsBefore;
        try { windowsBefore = _app.GetAllTopLevelWindows(_automation); } catch { windowsBefore = Array.Empty<Window>(); }
        var tempDir = Path.GetTempPath();
        var pdfsBefore = SafePdfList(tempDir);
        Console.WriteLine($"      → Baseline avant click : {procsBefore.Count} processes, {windowsBefore.Length} top-level windows, {pdfsBefore.Count} PDFs dans temp");

        // 4) Click le bouton K-bis
        Console.WriteLine($"      → Click K-bis…");
        Interaction.Click(kbisBtn);

        // 5) Poll jusqu'à détection d'un signal (process / window / fichier PDF
        //    n'importe où — temp ET aussi quelques dossiers RIG susceptibles d'héberger
        //    le PDF généré par Apache FOP ou Document.AffichageFichier).
        var extraPdfDirs = new[] { tempDir, @"C:\rig\Temp", @"C:\rig\Cache", @"C:\rig\PDF" };
        var pdfsBeforeAll = extraPdfDirs.SelectMany(d => SafePdfList(d)).ToHashSet();
        Console.WriteLine($"          + PDFs baseline ailleurs : {pdfsBeforeAll.Count - pdfsBefore.Count} hors %TEMP%");
        var sw = Stopwatch.StartNew();
        string? signal = null;
        int? pdfViewerPid = null; // PID du viewer PDF lancé (Acrobat/Edge…) → sert à récupérer le chemin du PDF via sa cmdline
        while (sw.Elapsed.TotalSeconds < waitSeconds && signal is null)
        {
            // Nouveau process ? ⚠ Un process hôte générique (dllhost = COM surrogate,
            // conhost, svchost, RuntimeBroker…) N'EST PAS une preuve que le K-bis est
            // rendu — il peut spawner pour mille raisons. On exige un process
            // SIGNIFICATIF : le générateur K-bis (KBisXML2PDF) ou un viewer PDF
            // réel (Acrobat, Edge, Foxit, Sumatra…). Sinon ce signal est ignoré.
            var procsNow = Process.GetProcesses()
                .Select(p => { try { return (Id: p.Id, Name: p.ProcessName); } catch { return (Id: -1, Name: ""); } })
                .Where(t => t.Id > 0).ToHashSet();
            bool IsGenericHost(string n) =>
                   n.StartsWith("RigClientAccueil", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Rig.Wpf.Kbis.SmokeRunner", StringComparison.OrdinalIgnoreCase)
                || n.Equals("dllhost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("conhost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("svchost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase)
                || n.Equals("backgroundTaskHost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("SearchProtocolHost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("SearchFilterHost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("WmiPrvSE", StringComparison.OrdinalIgnoreCase)
                || n.Equals("audiodg", StringComparison.OrdinalIgnoreCase)
                || n.Equals("csrss", StringComparison.OrdinalIgnoreCase)
                || n.Equals("taskhostw", StringComparison.OrdinalIgnoreCase)
                || n.Equals("sihost", StringComparison.OrdinalIgnoreCase)
                || n.Equals("ctfmon", StringComparison.OrdinalIgnoreCase);
            // Logique pure extraite + testée en xUnit (cf. KbisTextChecks). RigAffichageDoc =
            // viewer interne RIG (signal le + fort), Acrobat/Edge/Foxit… = viewers externes.
            bool IsMeaningfulKbisProc(string n) => KbisTextChecks.IsMeaningfulKbisProc(n);
            var newProcs = procsNow.Except(procsBefore)
                .Where(t => !IsGenericHost(t.Name)).ToList();
            var meaningfulProcs = newProcs.Where(t => IsMeaningfulKbisProc(t.Name)).ToList();
            if (meaningfulProcs.Count > 0)
            {
                pdfViewerPid = meaningfulProcs[0].Id; // viewer PDF (Acrobat/Edge/Foxit…) → cmdline = chemin du K-bis
                signal = $"process K-bis significatif : {string.Join(", ", meaningfulProcs.Select(t => $"{t.Name} (PID {t.Id})"))}";
                break;
            }
            // Process non-générique mais non-whitelisté : on le LOGUE mais on NE
            // considère PAS ça comme preuve suffisante (continue à poller un signal fort).
            if (newProcs.Count > 0)
                Console.WriteLine($"      → ⓘ process non-déterminant ignoré : {string.Join(", ", newProcs.Select(t => $"{t.Name}(PID {t.Id})"))}");
            // Nouvelle top-level window AVEC un titre non-vide (une fenêtre sans
            // titre est souvent un host COM transitoire, pas le doc K-bis).
            try
            {
                var windowsNow = _app.GetAllTopLevelWindows(_automation);
                if (windowsNow.Length > windowsBefore.Length)
                {
                    var newWin = windowsNow.Skip(windowsBefore.Length)
                        .FirstOrDefault(w => !string.IsNullOrWhiteSpace(SafeText(() => w.Title)));
                    if (newWin is not null)
                    {
                        signal = $"nouvelle fenêtre document : '{SafeText(() => newWin.Title)}'";
                        break;
                    }
                }
            }
            catch { }
            // Nouveau PDF (temp + autres dossiers RIG potentiels) ?
            var pdfsNow = extraPdfDirs.SelectMany(d => SafePdfList(d)).ToHashSet();
            var newPdfs = pdfsNow.Except(pdfsBeforeAll).ToList();
            if (newPdfs.Count > 0)
            {
                signal = $"nouveau(x) PDF : {string.Join(", ", newPdfs.Take(3).Select(Path.GetFileName))}";
                break;
            }
            // Tab VK s'est enrichi de nouveaux controls → render embedé
            var newDescCount = kbisRoot.FindAllDescendants().Length;
            if (newDescCount > allControls.Length + 5)
            {
                signal = $"tab VK s'est enrichi de {newDescCount - allControls.Length} nouveaux controls (probable rendu K-bis embedé)";
                break;
            }
            // Nouveau TabItem dans le tabControl principal (K-bis dans un onglet séparé) ?
            var tabControl = _window.FindFirstDescendant(cf => cf.ByAutomationId("tabControl"));
            if (tabControl is not null)
            {
                var tabsNow = tabControl.FindAllChildren();
                if (tabsNow.Length > 3) // initial = 3 (Accueil, Demandes, VK)
                {
                    var newTab = tabsNow.Skip(3).FirstOrDefault();
                    signal = $"nouveau tab apparu : '{SafeText(() => newTab?.Name)}' ({tabsNow.Length} tabs au total)";
                    break;
                }
            }
            Thread.Sleep(500);
        }

        if (signal is null)
        {
            // Dump post-action : qu'est-ce qui a changé visuellement ?
            Console.WriteLine($"      → Aucun signal après {waitSeconds}s. État final de pagetabVK :");
            var finalDescs = kbisRoot.FindAllDescendants();
            Console.WriteLine($"          {finalDescs.Length} descendants (avant click : {allControls.Length})");
            var newOrChanged = finalDescs.Where(c =>
            {
                if (c.ControlType != ControlType.Button && c.ControlType != ControlType.Pane && c.ControlType != ControlType.Text) return false;
                var nm = SafeText(() => c.Name);
                return !string.IsNullOrEmpty(nm)
                    && !nm.StartsWith("Veuillez patienter", StringComparison.OrdinalIgnoreCase);
            }).Take(40).ToList();
            foreach (var c in newOrChanged)
                Console.WriteLine($"          {c.ControlType} id='{SafeText(() => c.AutomationId)}' name='{SafeText(() => c.Name)}'");
            // Liste aussi les windows ENFANTS (popups)
            var childWindows = kbisRoot.FindAllDescendants(cf => cf.ByControlType(ControlType.Window));
            Console.WriteLine($"          Windows enfants : {childWindows.Length}");
            foreach (var w in childWindows.Take(5))
                Console.WriteLine($"             - Name='{SafeText(() => w.Name)}'");
            throw new Exception($"Aucun signal K-bis après {waitSeconds}s — click 'Charger ce dossier' n'a pas produit d'effet visible (process / window / PDF / nouveaux controls). Le PDF peut s'afficher dans un OCX (Vintasoft) invisible à UIA.");
        }
        // Délai de stabilisation : un signal "process/PDF/window apparu" ≠ "K-bis
        // rendu et lisible". On laisse le viewer/FOP finir le rendu avant de
        // déclarer succès ET avant le screenshot de fin (rule 15 : le screenshot
        // doit montrer le K-bis RÉELLEMENT à l'écran, pas un viewer vide).
        Console.WriteLine($"      → Signal détecté : {signal} — stabilisation 4s avant validation…");
        Thread.Sleep(4000);
        Console.WriteLine($"      → ✓ K-bis ouvert : {signal}");

        // En Mode A/C (desktop composé par le DWM) : re-pointe le self-snap sur le viewer pour capturer
        // la VRAIE page K-bis. En Mode B (HDESK) : no-op (AcroPDF non rendu). Voir RepointSnapToKbisViewer.
        try { RepointSnapToKbisViewer(pdfViewerPid); }
        catch (Exception exSnap) { Console.WriteLine($"      ⓘ re-point self-snap K-bis ignoré : {exSnap.Message}"); }

        // Vérif BDD complémentaire : récupère le numéro de demande qui vient d'apparaître
        // dans l'UI puis note le record DEMANDE eventuel (VK = visualisation, normalement aucun).
        Thread.Sleep(500); // laisse l'UI se rafraîchir
        var refreshed = kbisRoot.FindAllDescendants();
        string? newDemandeNumber = refreshed
            .Select(c => SafeText(() => c.Name))
            .Where(n => LegacyParsing.IsNumDemandeToken(n))
            .FirstOrDefault();
        if (string.IsNullOrEmpty(newDemandeNumber))
        {
            foreach (var e in refreshed.Where(c => c.ControlType == ControlType.Edit))
            {
                string? v = null; try { v = e.AsTextBox().Text; } catch { }
                if (LegacyParsing.IsNumDemandeToken(v))
                { newDemandeNumber = v; break; }
            }
        }
        if (!string.IsNullOrEmpty(newDemandeNumber))
        {
            Console.WriteLine($"      → Nouveau numéro de demande après click : '{newDemandeNumber}'");
            try { VerifyKbisDemandeInDb(newDemandeNumber); }
            catch (Exception ex) { Console.WriteLine($"      → ⚠ Vérif BDD échouée : {ex.Message}"); }
        }
        else
        {
            Console.WriteLine("      → ⓘ Numéro de demande pas encore extractible (UI peut-être pas full refresh)");
        }

        // Capture le PDF généré (le plus récent apparu depuis le click) pour vérif de CONTENU.
        // Si rien sur disque → rendu OCX/viewer embarqué → null (contenu non extractible par texte).
        try
        {
            var pdfsAfter = extraPdfDirs.SelectMany(d => SafePdfList(d)).ToHashSet();
            var fresh = pdfsAfter.Except(pdfsBeforeAll)
                .Select(p => { DateTime when; try { when = File.GetLastWriteTimeUtc(p); } catch { when = DateTime.MinValue; } return (Path: p, When: when); })
                .OrderByDescending(t => t.When)
                .ToList();
            if (fresh.Count > 0)
            {
                Console.WriteLine($"      → PDF K-bis capturé sur disque : {fresh[0].Path}");
                return fresh[0].Path;
            }
            Console.WriteLine("      → Aucun fichier PDF dans les dossiers scannés.");
        }
        catch (Exception ex) { Console.WriteLine($"      → ⚠ scan disque PDF échoué : {ex.Message}"); }

        // Fallback : RIG passe le PDF en ARGUMENT au viewer (Acrobat/Edge/Foxit…). On lit la ligne
        // de commande du process viewer pour récupérer le chemin réel du K-bis, où qu'il soit.
        if (pdfViewerPid is int vpid)
        {
            var fromCmd = GetPdfPathFromProcessCmdline(vpid);
            if (!string.IsNullOrEmpty(fromCmd) && File.Exists(fromCmd))
            {
                Console.WriteLine($"      → PDF K-bis récupéré via cmdline du viewer (PID {vpid}) : {fromCmd}");
                return fromCmd;
            }
            Console.WriteLine($"      → Chemin PDF non extrait de la cmdline du viewer (PID {vpid}).");
        }
        return null;
    }

    /// <summary>
    /// En Mode A/C (desktop composé par le DWM) : re-pointe le self-snap sur la fenêtre du viewer K-bis
    /// (AcroPDF/IE), la maximise et attend qu'elle soit RÉELLEMENT rendue (fraction de blanc) → le
    /// screenshot final de la tuile montre la VRAIE page K-bis. En Mode B (HDESK) : no-op — la page n'y
    /// est jamais peinte (AcroPDF = composition DWM, absente d'un CreateDesktop, prouvé) → le snap reste
    /// sur la console RIG (et 0 attente inutile).
    /// </summary>
    private void RepointSnapToKbisViewer(int? viewerPid)
    {
        if (_snapHwnd == IntPtr.Zero || string.IsNullOrEmpty(_snapDir)) return; // self-snap inactif
        if (Headless)
        {
            Console.WriteLine("      ⓘ Mode B (HDESK) : viewer AcroPDF non rendu (composition DWM) → pas de re-point, snap conservé sur la console RIG.");
            return;
        }
        // Mode A/C : desktop composé → la page se rend → capturable. Trouve le hwnd du viewer.
        IntPtr viewerHwnd = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 3 && viewerHwnd == IntPtr.Zero)
        {
            if (viewerPid is int pid)
            {
                try { using var p = Process.GetProcessById(pid); p.Refresh(); viewerHwnd = p.MainWindowHandle; } catch { }
            }
            if (viewerHwnd == IntPtr.Zero)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (KbisTextChecks.IsMeaningfulKbisProc(p.ProcessName))
                        {
                            p.Refresh();
                            var h = p.MainWindowHandle;
                            if (h != IntPtr.Zero) { viewerHwnd = h; break; }
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            if (viewerHwnd == IntPtr.Zero) Thread.Sleep(250);
        }
        if (viewerHwnd == IntPtr.Zero)
        {
            Console.WriteLine("      ⓘ Self-snap : fenêtre viewer K-bis introuvable — snap conservé sur la console RIG.");
            return;
        }
        Interaction.MaximizeWindow(viewerHwnd);
        _snapHwnd = viewerHwnd; // les prochains SnapTick captureront le viewer K-bis maximisé
        Console.WriteLine($"      → Self-snap re-pointé + viewer K-bis maximisé (hwnd=0x{viewerHwnd.ToInt64():X}).");
        // Attend que la page soit RÉELLEMENT rendue (fraction de blanc), plafond 10 s. Sur Desktop 1
        // composé, AcroPDF peint en ~2-3 s → le poll sort tôt ; les ticks périodiques capturent la page.
        var probe = Path.Combine(Path.GetTempPath(), $"kbis-render-probe-{Process.GetCurrentProcess().Id}.png");
        var swRender = Stopwatch.StartNew();
        double whiteFrac = 0;
        while (swRender.Elapsed.TotalSeconds < 10)
        {
            Thread.Sleep(600);
            try { Interaction.CaptureWindowByHwnd(viewerHwnd, probe); whiteFrac = Interaction.WhitePixelFraction(probe); } catch { }
            if (whiteFrac >= 0.30) break;
        }
        try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        Console.WriteLine(whiteFrac >= 0.30
            ? $"      → Page K-bis rendue ({whiteFrac:P0} de blanc) — le screenshot final montrera le K-bis."
            : $"      → ⚠ Page K-bis pas confirmée rendue après 10s ({whiteFrac:P0} de blanc).");
        try { SnapTick(); } catch { }
    }

    /// <summary>Lit la ligne de commande d'un process (WMI) et en extrait le 1er chemin .pdf —
    /// le viewer PDF (Acrobat/Edge/Foxit…) est lancé par RIG AVEC le fichier K-bis en argument.</summary>
    private static string? GetPdfPathFromProcessCmdline(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementBaseObject mo in searcher.Get())
            {
                var cmd = mo["CommandLine"]?.ToString();
                if (string.IsNullOrEmpty(cmd)) continue;
                Console.WriteLine($"      → cmdline viewer PID {pid} : {cmd}");
                var path = KbisTextChecks.ExtractPdfPathFromCmdline(cmd); // logique pure testée xUnit
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch (Exception ex) { Console.WriteLine($"      → ⚠ lecture cmdline PID {pid} échouée : {ex.Message}"); }
        return null;
    }

    private static System.Collections.Generic.HashSet<string> SafePdfList(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly).ToHashSet(); }
        catch { return new System.Collections.Generic.HashSet<string>(); }
    }

    /// <summary>
    /// Vérifie le CONTENU du PDF K-bis SANS analyse d'image : extrait la couche texte (PdfPig)
    /// et l'examine. 1er passage = EMPIRIQUE (dump du texte pour voir le contenu réel) ; les
    /// assertions sur les vraies données (dénomination, SIREN — cross-check BDD) sont ajoutées
    /// une fois le format réel connu. Throw si le PDF n'a pas de couche texte exploitable.
    /// </summary>
    public void VerifyKbisPdfContent(string? pdfPath, string numGestion)
    {
        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
        {
            Console.WriteLine("      → ⚠ Pas de fichier PDF capturé → contenu non vérifié par texte (rendu OCX embarqué ?).");
            return;
        }

        string text;
        try
        {
            var sb = new System.Text.StringBuilder();
            using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath))
            {
                int np = 0;
                foreach (var page in doc.GetPages()) { sb.AppendLine(page.Text); np++; }
                Console.WriteLine($"      → PDF ouvert : {np} page(s), {sb.Length} caractères de texte.");
            }
            text = sb.ToString();
        }
        catch (Exception ex)
        {
            throw new Exception($"Extraction texte PDF échouée ({Path.GetFileName(pdfPath)}) : {ex.GetType().Name}: {ex.Message}");
        }

        // Dump complet du texte dans un sidecar UTF-8 (diagnostic permanent + inspection humaine).
        try
        {
            var sidecar = pdfPath + ".extracted.txt";
            File.WriteAllText(sidecar, text, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"      → Texte PDF dumpé : {sidecar}");
        }
        catch (Exception ex) { Console.WriteLine($"      → ⚠ dump sidecar échoué : {ex.Message}"); }

        if (text.Trim().Length < 200)
            throw new Exception($"PDF K-bis quasi vide ({text.Length} chars) — génération échouée ou PDF image (pas de couche texte).");

        // Toute la logique de contenu est PURE + testée en xUnit (cf. KbisTextChecks / KbisTextChecksTests).
        // Marqueurs structurels d'un VRAI extrait K-bis (≥2 attendus, sinon page d'erreur/PDF parasite).
        var found = KbisTextChecks.FindKbisMarkers(text);
        Console.WriteLine($"      → Marqueurs K-bis trouvés ({found.Count}) : {string.Join(", ", found)}");
        if (found.Count < 2)
            throw new Exception($"PDF a du texte ({text.Length} chars) mais PAS les marqueurs d'un K-bis (trouvés: {string.Join(",", found)}) — pas le bon document ?");

        // BONNES DONNÉES (1) : le K-bis correspond au dossier DEMANDÉ (son numéro de gestion y figure).
        bool ngFound = KbisTextChecks.ContainsNumGestion(text, numGestion);
        Console.WriteLine($"      → Numéro de gestion '{numGestion}' présent dans le PDF : {(ngFound ? "OUI" : "NON")}");
        if (!ngFound)
            throw new Exception($"K-bis généré NE correspond PAS au dossier demandé : numéro de gestion '{numGestion}' absent du PDF (mauvais dossier / PDF stale).");

        // BONNES DONNÉES (2) : un numéro SIREN (9 chiffres) figure = données d'immatriculation réelles.
        var siren = KbisTextChecks.FindSiren(text);
        Console.WriteLine($"      → SIREN détecté dans le PDF : {siren ?? "(aucun)"}");
        if (string.IsNullOrEmpty(siren))
            throw new Exception("K-bis sans numéro SIREN (9 chiffres) — données d'immatriculation manquantes.");

        // Complétude : le K-bis doit aller jusqu'au bout (pas tronqué).
        bool complete = KbisTextChecks.IsComplete(text);
        Console.WriteLine($"      → 'FIN DE L'EXTRAIT' présent (K-bis complet) : {(complete ? "OUI" : "non")}");

        Console.WriteLine($"      → ✓ PDF K-bis CONFORME : dossier {numGestion}, SIREN {siren}, {text.Length} chars, {found.Count} marqueurs.");

        // Snap FINAL de la tuile = rendu du VRAI PDF de RIG (moteur Windows), PAS un screenshot du
        // viewer : RigAffichageDoc (ActiveX AcroPDF dans un WebBrowser IE) n'est pas capturable sur
        // un HDESK non composité par le DWM (vérifié 2026-05-29).
        WriteRealPdfSnap(pdfPath);
    }

    /// <summary>
    /// Rend le VRAI PDF généré par RIG en image et l'écrit comme snap FINAL de la tuile, via le
    /// helper PS <c>render-kbis-pdf.ps1</c> (moteur PDF intégré à Windows, headless, indépendant du
    /// desktop). On ne screenshote PAS le viewer : son contrôle AcroPDF n'est pas capturable sur un
    /// HDESK non composité par le DWM. On fige d'abord le timer self-snap pour que ce rendu reste le
    /// snap le plus récent (donc l'image figée affichée sur la tuile).
    /// </summary>
    private void WriteRealPdfSnap(string? pdfPath)
    {
        if (string.IsNullOrEmpty(_snapDir) || string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return;
        const string script = @"C:\Code RIG\render-kbis-pdf.ps1";
        if (!File.Exists(script)) { Console.WriteLine($"      ⓘ {script} absent — snap PDF non généré."); return; }
        try
        {
            StopPeriodicSnap(); // fige le timer self-snap pour que le rendu PDF soit le snap le plus récent
            var pdfSnapSeq = System.Threading.Interlocked.Increment(ref _snapSeq);
            var pdfSnapNow = DateTime.Now;
            var pdfSnapName = Observability.SnapFileName(pdfSnapSeq, pdfSnapNow);   // #4 (source unique)
            var outPng = Path.Combine(_snapDir!, pdfSnapName);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -PdfPath \"{pdfPath}\" -OutPng \"{outPng}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = Process.Start(psi))
            {
                string so = proc!.StandardOutput.ReadToEnd();
                string se = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30000);
                if (File.Exists(outPng))
                {
                    // #2/#3 : publie l'index et écrit la ligne JSONL UNIQUEMENT après que le PNG existe
                    // réellement sur disque — sinon un [snap=NNNN] tagué par un Pass/Fail pendant le rendu
                    // pointerait une entrée d'index vers un fichier pas encore écrit (ou jamais, si échec PS).
                    LastSnapSeq = pdfSnapSeq;
                    AppendSnapIndex(pdfSnapSeq, pdfSnapNow, pdfSnapName);
                    Console.WriteLine($"      → Snap final = rendu du VRAI PDF K-bis de RIG (moteur Windows) : {so.Trim()}");
                }
                else Console.WriteLine($"      ⚠ rendu PDF K-bis sans PNG. out='{so.Trim()}' err='{se.Trim()}'");
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ rendu snap PDF ignoré : {ex.Message}"); }
    }

    /// <summary>
    /// Vérif BDD INFORMATIVE d'un éventuel record DEMANDE. VK = "Visualisation -
    /// Extrait RCS" est un processus de CONSULTATION : il ne crée normalement
    /// AUCUNE demande (contrairement à l'ex-cible erronée XXKBIS = "Suppression
    /// d'un dossier" qui, elle, était un workflow demande). Si une demande est
    /// trouvée, on logue son code sans faire échouer le scénario — le vrai signal
    /// de succès VK est le K-bis rendu (process KBisXML2PDF/dllhost), vérifié en amont.
    /// </summary>
    public static void VerifyKbisDemandeInDb(string demandeNumber)
    {
        if (string.IsNullOrWhiteSpace(demandeNumber))
            throw new ArgumentException("Numéro de demande vide", nameof(demandeNumber));

        // Connection string : env var (CI override) ou défaut SQL-DEV/DEV/RIG_DEV
        var connStr = Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
            ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";

        Console.WriteLine($"      → Query BDD : SELECT FROM DEMANDE WHERE DMND_NUM_DEMANDE='{demandeNumber}'");
        using var conn = new SqlConnection(connStr);
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT TOP 1 DMND_NUM_DEMANDE, DMND_CODE_PROSS, DMND_ETAT_DEMANDE, DMND_NOM_UTILISATEUR " +
            "FROM DEMANDE WHERE DMND_NUM_DEMANDE = @num", conn);
        cmd.Parameters.AddWithValue("@num", demandeNumber);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new Exception($"Demande '{demandeNumber}' INTROUVABLE en BDD — l'UI a affiché le numéro mais aucun record persisté.");
        var codeProcess = reader.IsDBNull(1) ? "(NULL)" : reader.GetString(1);
        var etat = reader.IsDBNull(2) ? "(NULL)" : reader.GetString(2);
        var user = reader.IsDBNull(3) ? "(NULL)" : reader.GetString(3);
        Console.WriteLine($"      → ✓ Record BDD : DMND_NUM='{demandeNumber}' DMND_CODE_PROSS='{codeProcess}' DMND_ETAT='{etat}' user='{user}'");
        // VK = "Visualisation - Extrait RCS" : processus de CONSULTATION, pas un
        // workflow DEMANDE (contrairement à l'ancien XXKBIS = "Suppression d'un
        // dossier"). VK ne crée normalement pas de DEMANDE — le signal de succès
        // est le K-bis rendu (process KBisXML2PDF / PDF), détecté en amont.
        // Si une demande est tout de même trouvée, on la logue à titre informatif
        // sans faire échouer le scénario (le mismatch de code n'est plus une erreur).
        if (!string.Equals(codeProcess, "VK", StringComparison.Ordinal))
            Console.WriteLine($"      → ⓘ DMND_CODE_PROSS='{codeProcess}' (≠ 'VK') — info seulement, VK est un processus de visualisation sans workflow demande.");
    }

    /// <summary>Dump avec BoundingRectangle (x, y, w, h) — sert à corréler Edit ↔ Text-label par proximité.</summary>
    private static void DumpDescendantsWithPositions(AutomationElement el, int maxDepth, int depth = 0, int maxLines = 80)
    {
        if (depth > maxDepth) return;
        try
        {
            foreach (var child in el.FindAllChildren())
            {
                if (maxLines-- <= 0) return;
                string id = "", nm = "", ct = "", pos = "";
                try { id = child.AutomationId ?? ""; } catch { }
                try { nm = child.Name ?? ""; } catch { }
                try { ct = child.ControlType.ToString(); } catch { }
                try { var r = child.BoundingRectangle; pos = $"({r.X},{r.Y} {r.Width}×{r.Height})"; } catch { }
                Console.WriteLine($"          {new string(' ', depth * 2)}[{ct}] id='{id}' name='{nm}' {pos}");
                DumpDescendantsWithPositions(child, maxDepth, depth + 1, maxLines);
            }
        }
        catch { }
    }

    private static string SafeText(Func<string?> getter)
    {
        try { return (getter() ?? "").Trim(); }
        catch { return ""; }
    }

    /// <summary>Dossier screenshots scopé par run (PID du SmokeRunner) -> anti-collision parallèle.</summary>
    private static string ScreenshotDir()
    {
        var runId = Process.GetCurrentProcess().Id.ToString();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop", "JsonRapture", "screenshots", runId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Capture l'état visuel à la fin d'un run (succès OU échec) et l'écrit en PNG.
    /// Best-effort : si la fenêtre RIG est fermée/morte, log + return null (jamais throw
    /// — c'est un artefact de diagnostic, pas une étape bloquante).
    ///
    /// L'image sert de gate visuel FINAL (analysé par l'agent) PAR-DESSUS les
    /// assertions SQL/log/UIA — pas à leur place.
    /// </summary>
    /// <param name="fullScreen">Conservé pour compat de signature mais ignoré — Interaction.CaptureWindow capture la fenêtre via PrintWindow.</param>
    /// <returns>Path absolu du PNG produit, ou null si capture impossible.</returns>
    public string? CaptureScreenshot(string label, bool fullScreen = false)
    {
        if (_window is null) return null;
        try
        {
            var shotPath = Path.Combine(ScreenshotDir(),
                $"smoke-{string.Join("_", (label ?? "run").Split(Path.GetInvalidFileNameChars()))}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            Interaction.CaptureWindow(_window, shotPath);
            Console.WriteLine($"      📸 Screenshot : {shotPath}");
            return shotPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ CaptureScreenshot a throw (non-bloquant) : {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopPeriodicSnap();
        int rigPid = -1;
        try { if (_app is not null && !_app.HasExited) rigPid = _app.ProcessId; } catch { }
        try
        {
            if (_app is not null && !_app.HasExited)
            {
                var closeOk = false;
                try { _app.Close(); closeOk = true; } catch { }
                if (!closeOk || !_app.HasExited)
                {
                    Console.WriteLine("      ⓘ app.Close failed/timed out — killing");
                    try { _app.Kill(); } catch { }
                }
            }
        }
        catch { /* best-effort */ }
        // .NET Fx 4.8 : Process.Kill() ne tue PAS l'arbre -> RIG legacy peut laisser des enfants vivants
        // ("Application failed to exit") = contention serveur RIG sur le scenario SUIVANT en batch (Mecanisme B,
        // fix 2026-06-19). On tue l'ARBRE de NOTRE RIG par PID (cible ce worker uniquement, jamais un RIG de travail user).
        if (rigPid > 0)
        {
            try
            {
                using var tk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill", Arguments = $"/T /F /PID {rigPid}",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                tk?.WaitForExit(3000);
            }
            catch { }
        }
        try { _app?.Dispose(); } catch { }
        try { _automation?.Dispose(); } catch { }
        // _desktop est possédé par Program.cs (via RunAttached) — NE PAS le disposer ici.
    }

    /// <summary>
    /// Demarre un Timer background qui PrintWindow le hwnd RIG toutes les
    /// <paramref name="intervalMs"/> ms (default 5000) et sauve en PNG sous
    /// %LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\&lt;runStamp&gt;\&lt;scenarioId&gt;\.
    ///
    /// Permet d'observer un scenario qui tourne en HDESK isole (RIG_DRIVER_HEADLESS=1,
    /// invisible sur le desktop user) via les PNGs lus depuis l'exterieur. Zero vol
    /// de focus user puisque RIG est sur un Desktop Windows separe.
    ///
    /// Le hwnd est CACHE au demarrage cote thread HDESK pour eviter les UIA
    /// cross-thread vers _window dans le Timer callback (qui tourne sur le thread pool).
    /// PrintWindow direct par hwnd ne necessite pas l'affinite HDESK du thread.
    /// </summary>
    public void StartPeriodicSnap(string scenarioId, int intervalMs = 500)
    {
        if (_window is null) { Console.WriteLine("      ⓘ StartPeriodicSnap : _window null, skip"); return; }
        // Cache hwnd cote thread HDESK courant.
        IntPtr hwnd = IntPtr.Zero;
        try { if (_window.Properties.NativeWindowHandle.IsSupported) hwnd = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        // FIX 2026-06-16 : MainWindowHandle (_window) peut résoudre un overlay UAC (WS_EX_NOREDIRECTIONBITMAP
        // → PrintWindow NOIR). On résout la VRAIE fenêtre console RIG par énumération (fallback = MainWindow).
        try { hwnd = ResolveRigConsoleHwnd(_app.ProcessId, hwnd); } catch { }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("      ⓘ StartPeriodicSnap : hwnd zero, skip"); return; }
        Console.WriteLine($"      ⓘ StartPeriodicSnap : snap hwnd=0x{hwnd.ToInt64():X} (console RIG résolue)");
        // FIX 2026-06-16 : EnsureWindowMaximized() maximisait `_window` (= overlay UAC, MainWindowHandle) et
        // PAS la vraie console → RIG restait en petite résolution (750x480). On maximise la CONSOLE résolue.
        if (Headless) { try { Interaction.MaximizeWindow(hwnd); } catch { } }
        _snapHwnd = hwnd;
        var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
        if (string.IsNullOrEmpty(runStamp)) runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _snapDir = System.IO.Path.Combine(local, "rig-wpf-testviewer", "self-snaps", runStamp, scenarioId);
        System.IO.Directory.CreateDirectory(_snapDir);
        // Discovery file : le LiveViewer cote TV lit ce fichier.
        // Format 3 lignes : hwnd (int64) / PID (int) / desktopName (string).
        // - hwnd : pour PrintWindow cross-desktop
        // - PID : pour validation lifecycle (Process.GetProcessById)
        // - desktopName : pour bouton "Ouvrir HDESK" (SwitchDesktop) en mode B (HEADLESS=1).
        //                 Vide en mode A (HEADLESS=0, _desktop.DesktopName=null).
        try
        {
            var discoveryPath = System.IO.Path.Combine(_snapDir, "hwnd.txt");
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var deskName = _desktop?.DesktopName ?? "";
            System.IO.File.WriteAllText(discoveryPath, $"{hwnd.ToInt64()}{System.Environment.NewLine}{pid}{System.Environment.NewLine}{deskName}");
        }
        catch (Exception exDisco) { Console.WriteLine($"      ⓘ hwnd.txt write a jete : {exDisco.GetType().Name}: {exDisco.Message}"); }
        _snapSeq = 0;
        LastSnapSeq = -1;   // #2 : reset le tag [snap=] au début d'un nouveau cycle de self-snap
        _snapStopped = false;
        _snapReMaximizedOnce = false;   // self-heal : ré-arme le log "re-maximisee" pour ce cycle
        Console.WriteLine($"      ⓘ Self-snap demarre : hwnd=0x{hwnd.ToInt64():X} interval={intervalMs}ms dir={_snapDir}");
        _snapTimer = new System.Threading.Timer(_ => SnapTick(), null, intervalMs, intervalMs);
    }

    private void SnapTick()
    {
        if (_snapStopped || _snapHwnd == IntPtr.Zero || string.IsNullOrEmpty(_snapDir)) return;
        try
        {
            // Self-heal taille (fix « box minuscule » 2026-06-23) : RIG est maximisé au login + au snap-start
            // (L3582) UNE fois, mais un PROC ouvert APRÈS le login le dé-maximise → il retombe à sa taille
            // legacy 750x480 et SnapTick capture alors tout le run en petit (preuve : un run = 1 frame 1920x1080
            // puis 382 frames 750x480). On re-maximise la console capturée si elle est repassée sous le seuil.
            // ShowWindow(SW_MAXIMIZE) = même mécanisme prouvé qu'au démarrage (cross-desktop OK), Win32-only donc
            // sûr depuis ce callback threadpool (pas d'UIA cross-thread). Borné : no-op quand w>=1400 → zéro spam,
            // zéro effet quand déjà maximisé. Gate Headless : en non-headless la mosaïque tuile volontairement
            // (<1400px) → ne pas la combattre (cf. RIG_TILE_*, MainWindowViewModel L3165).
            if (Headless && GetWindowRect(_snapHwnd, out var wr))
            {
                int w = wr.Right - wr.Left;
                if (w > 0 && w < 1400)
                {
                    Interaction.MaximizeWindow(_snapHwnd);
                    if (!_snapReMaximizedOnce)
                    {
                        _snapReMaximizedOnce = true;
                        Console.WriteLine($"      → self-heal : console RIG re-maximisee (etait {w}px < 1400, de-maximisee post-login)");
                    }
                }
            }
            int seq = System.Threading.Interlocked.Increment(ref _snapSeq);
            var now = DateTime.Now;
            var fileName = Observability.SnapFileName(seq, now);   // #4 : ms dans le nom (source unique)
            var path = System.IO.Path.Combine(_snapDir!, fileName);
            Interaction.CaptureWindowByHwnd(_snapHwnd, path);
            LastSnapSeq = seq;                            // #2 : publie l'index courant pour le tag [snap=NNNN]
            AppendSnapIndex(seq, now, fileName);          // #3 : ligne JSONL ts↔fichier
        }
        catch { /* best-effort, ne pas casser le scenario */ }
    }

    private readonly object _snapIndexLock = new object();

    /// <summary>#3 — append une ligne JSONL {seq, ts (ms), file} dans snap-index.jsonl du dossier de
    /// snaps, pour une corrélation déterministe index→fichier→horodatage (lisible par Claude / jq / Node).
    /// Best-effort : un échec d'écriture ne casse jamais le scénario. Sérialisé par _snapIndexLock : le
    /// callback Timer SnapTick (threadpool) et le snap PDF (thread principal) peuvent se chevaucher — car
    /// Timer.Dispose() ne draine PAS un callback déjà en vol — et sans lock FileShare.None ferait perdre
    /// une ligne (IOException silencieusement avalée).</summary>
    private void AppendSnapIndex(int seq, DateTime ts, string fileName)
    {
        if (string.IsNullOrEmpty(_snapDir)) return;
        try
        {
            var line = Observability.SnapIndexLine(seq, ts, fileName);   // #3 (source unique)
            lock (_snapIndexLock)
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(_snapDir!, "snap-index.jsonl"),
                    line + System.Environment.NewLine);
            }
        }
        catch { /* best-effort, ne pas casser le scenario */ }
    }

    /// <summary>Arrete le Timer self-snap. Idempotent. Appele depuis Dispose.</summary>
    public void StopPeriodicSnap()
    {
        _snapStopped = true;
        try { _snapTimer?.Dispose(); } catch { }
        _snapTimer = null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // KBIS VK / XEX helpers — 2026-05-28
    //
    // Workflow VK (consultation K-bis via PDF) :
    //   1. OpenProcKbis (existant) → tab pagetabVK
    //   2. EnterNumGestionInActiveTab("2024B00001") → Tab → dossier chargé
    //   3. OpenKbisDocument (existant) → click "K-bis" → PDF ouvert
    //
    // Workflow XEX (édition Brouillon Word) :
    //   1. OpenProcXex → tab pagetabXEX (ou similaire)
    //   2. EnterNumGestionInActiveTab("2024B00001") → Tab
    //   3. ClickValiderInActiveForm → tableau d'édition popup
    //   4. WaitForTableauEdition → vérifie popup affiché (imprimante/proximité visible)
    //   5. UncheckImprimanteAndValidate → décoche imprimante + Alt+V → .doc s'ouvre
    //
    // ⚠ Aucun AutomationId stable connu pour XEX → tout en find by Name substring.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ouvre PROC_XEX ("Edition interne d'un Kbis") depuis la Console d'accueil.
    /// Pattern : scan onglets btn1..btn7 × sousmenu × lstProcessus, matche un item
    /// dont le 1er token Name = "XEX" exact (case-insensitive). Pas de fast path
    /// connu — découverte live au 1er run, log les coordonnées (btn, sousmenu) pour
    /// optimisation future.
    /// </summary>
    public void OpenProcXex()
    {
        OpenProcessus("PROC_XEX", n =>
        {
            if (string.IsNullOrWhiteSpace(n)) return false;
            var firstToken = LegacyParsing.FirstToken(n);
            // Match XEX exact ET libellé contient "edition" + "kbis" (en backup).
            if (firstToken.Equals("XEX", StringComparison.OrdinalIgnoreCase)) return true;
            return n.IndexOf("edition interne", StringComparison.OrdinalIgnoreCase) >= 0
                && n.IndexOf("kbis", StringComparison.OrdinalIgnoreCase) >= 0;
        });
    }

    /// <summary>
    /// Module ALERTES RCS — ouvre une alerte depuis la tuile "Alertes RCS" de la Console d'accueil
    /// (PAS un PROC). Flux : maximize → page "Alertes RCS" (lblAlertesPage1) → double-clic sur l'item
    /// de lstAlertes dont le Name contient <paramref name="alerteNameSubstring"/> ("interrompue" /
    /// "réclamation") → PROC_DEMANDE (grille ultDgvResultats) s'ouvre dans un onglet.
    /// ÉTAPE 1 : s'arrête à la grille visible. Best-effort + dumps (UI Alertes pas encore éprouvée).
    /// </summary>
    public void OpenAlerteRcs(string alerteNameSubstring)
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + ClickSeConnecter() doivent être appelés avant OpenAlerteRcs()");

        EnsureWindowMaximized();

        // 1) Activer la page "Alertes RCS" (lblAlertesPage1). Best-effort : si absent, on suppose
        //    qu'on est déjà sur la page RCS (état par défaut de la tuile).
        var pageRcs = FindByAutomationId("lblAlertesPage1") ?? FindByName("Alertes RCS");
        if (pageRcs is not null)
        {
            Console.WriteLine($"      → Page 'Alertes RCS' (AutomationId='{SafeText(() => pageRcs.AutomationId)}') — clic");
            try { Interaction.Click(pageRcs); } catch (Exception ex) { Console.WriteLine($"      ⓘ clic page RCS jeté : {ex.Message}"); }
            Thread.Sleep(800);
        }
        else Console.WriteLine("      → lblAlertesPage1/'Alertes RCS' introuvable — page RCS supposée active par défaut.");

        // 2) Trouver l'item d'alerte dans lstAlertes (RigListView) par Name substring (poll 8s).
        var lst = FindByAutomationId("lstAlertes");
        if (lst is null)
        {
            Console.WriteLine("      → lstAlertes introuvable. Dump descendants pour diag :");
            DumpDescendants(_window!, maxDepth: 5);
            throw new Exception("Tuile 'Alertes RCS' / lstAlertes introuvable dans la Console d'accueil (cf. dump).");
        }

        // ⚠ La liste des alertes est PAGINÉE (run live : 4 pages « 1 2 3 4 » sous la tuile). lstAlertes
        // n'expose QUE la page courante → si l'alerte cherchée est sur une autre page, l'ancien scan
        // mono-page renvoyait « introuvable » à tort. On scanne donc la page courante puis on pagine
        // (bouton « suivant » de la tuile) jusqu'à MaxPages, en ré-interrogeant lstAlertes à chaque page.
        const int maxAlertePages = 8;     // garde-fou (la tuile en a ~4) — couvre une croissance future
        AutomationElement? alerte = null;
        var seenAllItems = new List<string>();
        for (int page = 1; page <= maxAlertePages && alerte is null; page++)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < (page == 1 ? 8000 : 3000) && alerte is null)
            {
                AutomationElement[] items;
                try { items = lst.FindAllChildren(); } catch { items = System.Array.Empty<AutomationElement>(); }
                alerte = items.FirstOrDefault(it =>
                    SafeText(() => it.Name).IndexOf(alerteNameSubstring, StringComparison.OrdinalIgnoreCase) >= 0);
                if (alerte is null) Thread.Sleep(300);
            }
            if (alerte is not null)
            {
                if (page > 1) Console.WriteLine($"      → Alerte trouvée en page {page} de lstAlertes.");
                break;
            }
            // Mémorise les items de cette page pour le dump final, puis tente de paginer.
            try { foreach (var it in lst.FindAllChildren()) { var n = SafeText(() => it.Name); if (n.Length > 0) seenAllItems.Add($"[p{page}] {n}"); } } catch { }
            if (page < maxAlertePages && TryGoToNextAlertePage(page))
                Thread.Sleep(600);   // laisser lstAlertes se rafraîchir après changement de page
            else
                break;               // plus de page suivante → on sort (alerte restera null → throw plus bas)
        }
        if (alerte is null)
        {
            Console.WriteLine($"      → Aucune alerte contenant '{alerteNameSubstring}' dans lstAlertes (toutes pages). Items vus :");
            foreach (var n in seenAllItems) Console.WriteLine($"          - '{n}'");
            throw new Exception($"Alerte RCS '{alerteNameSubstring}' introuvable dans lstAlertes (toutes pages parcourues). "
                + "La base DEV a-t-elle de telles demandes, ou le libellé de l'alerte diffère-t-il ? "
                + "Override possible via RIG_DCADEMAT_ALERTE_<KIND>.");
        }

        Console.WriteLine($"      → Alerte trouvée : '{SafeText(() => alerte.Name)}' — activation (Select + Entrée)");
        // _ExecuteAlerte() répond au double-clic OU à Entrée (FORM_ACCUEIL). Le double-clic écran
        // n'active pas toujours (il sélectionne seulement) → Select + Entrée (pattern lstProcessus
        // fiable) en priorité, vrai double-clic écran en fallback.
        try { Interaction.ActivateListItem(alerte, lst); }
        catch (Exception ex) { Console.WriteLine($"      ⓘ ActivateListItem jeté : {ex.Message}"); }

        const int gridBaseBudgetMs = 8000;   // 1re passe (post Entrée)
        var swGrid = Stopwatch.StartNew();
        var grid = WaitForDemandeGrid(gridBaseBudgetMs);
        if (grid is null)
        {
            Console.WriteLine("      → Grille pas vue après Entrée — fallback double-clic écran sur la ligne.");
            var r = alerte.BoundingRectangle;
            Interaction.ClickAtScreenPoint((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), _app.ProcessId, doubleClick: true);
            grid = WaitForDemandeGrid(10000);   // 2e passe (post double-clic)
        }
        // DÉTERMINISME (fix flap form-validation, run 17:29 STAMP 171521) : si la grille n'est toujours
        // pas là MAIS RIG affiche encore l'overlay « Veuillez patienter / Traitement en cours » (la grille
        // est en train de charger), on NE renonce PAS — on prolonge l'attente TANT QUE l'overlay persiste,
        // jusqu'à un plafond dur. C'est purement ADDITIF : ne s'exécute que sur l'ancien chemin d'échec
        // (les 2 passes ont renvoyé null) → aucun impact sur le chemin nominal des scénarios qui voient la
        // grille tout de suite. On ne throw QUE si RIG ne charge plus (overlay absent = écran figé/planté =
        // vrai échec) OU si le plafond dur est atteint. Le run 17:04 (succès) voyait la grille à ~6,4s.
        // fix-ok: 2026-06-16 — l'alerte 'interrompue' (form-interrompue) charge sa grille PROC_DEMANDE en >45s
        // (reproduce run 20260616-153933 : cap atteint avec overlay « Traitement en cours » ENCORE present).
        // Les autres alertes chargent en ~6-50s. Bump 45s->90s puis 90s->180s (2026-06-19) pour couvrir la
        // grille TRES lourde de l'alerte 'reclamation' (911 demandes) qui depassait 90s par intermittence.
        const long gridHardCapMs = 180000;
        bool announcedGrace = false;
        while (grid is null
               && LegacyParsing.ShouldKeepWaitingForGrid(swGrid.ElapsedMilliseconds, baseMaxMs: 18000, hardCapMs: gridHardCapMs, overlayPresent: IsLoadingOverlayOnScreen()))
        {
            if (!announcedGrace)
            {
                Console.WriteLine("      → Grille pas encore là mais overlay « Traitement en cours » présent — "
                    + $"RIG charge encore : prolongation de l'attente (plafond {gridHardCapMs}ms) au lieu de renoncer.");
                announcedGrace = true;
            }
            grid = WaitForDemandeGrid(3000);   // ré-essai court tant que l'overlay est là
        }
        if (announcedGrace && grid is not null)
            Console.WriteLine($"      ✓ Grille apparue après prolongation (chargement RIG lent), à {swGrid.ElapsedMilliseconds}ms.");
        if (grid is null)
        {
            // À ce stade : soit RIG ne charge plus (overlay disparu sans grille = écran figé), soit plafond
            // dur atteint malgré l'overlay. On distingue les deux dans le message pour le diagnostic.
            bool stillLoading = IsLoadingOverlayOnScreen();
            Console.WriteLine($"      → Grille de demandes pas détectée après {swGrid.ElapsedMilliseconds}ms "
                + $"(overlay chargement {(stillLoading ? "ENCORE présent → RIG anormalement lent/figé au plafond dur" : "disparu → écran stabilisé sans grille")}). Dump :");
            DumpDescendants(_window!, maxDepth: 3);
            throw new Exception("La liste des demandes (PROC_DEMANDE) ne s'est pas ouverte après activation de l'alerte"
                + (stillLoading ? " (RIG toujours en « Traitement en cours » au plafond dur — chargement anormalement lent ou figé)."
                                : " (l'écran s'est stabilisé sans grille — alerte sans demande affichable ou navigation interrompue)."));
        }
        Console.WriteLine($"      → Grille des demandes ouverte ('{SafeText(() => grid.Name)}' type={grid.ControlType}, alerte '{alerteNameSubstring}') — Étape 1 OK.");
    }

    /// <summary>
    /// Passe à la page suivante de la liste des alertes RCS (tuile paginée « 1 2 3 4 »). Best-effort :
    /// cherche d'abord un Button « suivant » (Name « ›/>/Suivant/Page suivante »), sinon le bouton de
    /// numéro de page (currentPage+1) dans le pager. Clic via Interaction.Click (mouse-free). Retourne
    /// true si un contrôle de pagination a été actionné, false s'il n'y en a pas (= dernière page / pas
    /// de pager). NON bloquant : toute exception est avalée et renvoie false.
    /// </summary>
    private bool TryGoToNextAlertePage(int currentPage)
    {
        if (_window is null) return false;
        try
        {
            var clickables = _window.FindAllDescendants()
                .Where(c =>
                {
                    ControlType ct; try { ct = c.ControlType; } catch { return false; }
                    if (ct != ControlType.Button && ct != ControlType.Hyperlink && ct != ControlType.Pane) return false;
                    try { return c.IsAvailable && !c.IsOffscreen; } catch { return false; }
                })
                .ToList();

            // a) Bouton « page suivante » explicite (flèche ou libellé).
            var next = clickables.FirstOrDefault(c =>
            {
                var n = SafeText(() => c.Name).Trim();
                if (n.Length == 0) return false;
                var nl = n.ToLowerInvariant();
                return n == "›" || n == ">" || n == "»"
                    || nl == "suivant" || nl.Contains("page suivante") || nl.Contains("suivante")
                    || nl == "next";
            });

            // b) Sinon, le bouton dont le Name == numéro de la page cible (currentPage+1).
            if (next is null)
            {
                string target = (currentPage + 1).ToString();
                next = clickables.FirstOrDefault(c => SafeText(() => c.Name).Trim() == target);
            }

            if (next is null) return false;
            Console.WriteLine($"      → Pagination alertes : clic « {SafeText(() => next.Name)} » (page {currentPage} → {currentPage + 1}).");
            try { Interaction.Click(next); }
            catch (Exception ex) { Console.WriteLine($"      ⓘ clic pagination jeté : {ex.Message}"); return false; }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⓘ TryGoToNextAlertePage jeté : {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Poll l'apparition de la grille des demandes (PROC_DEMANDE) après activation d'une
    /// alerte : AutomationId connu (ultDgvResultats/_dgvDemandes), sinon un control Table/DataGrid
    /// (la grille de demandes ; l'accueil n'expose pas de Table par défaut, faible faux-positif).</summary>
    private AutomationElement? WaitForDemandeGrid(int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            // Ordre : ultDgvResultats (rapide) → Table/DataGrid par ControlType (rapide) → _dgvDemandes
            // EN DERNIER (lookup ~100s quand absent, car FindFirstDescendant walk tout l'arbre d'un
            // onglet PROC ouvert) : on ne le tente que si les voies rapides ont échoué.
            var byId = FindByAutomationId("ultDgvResultats");
            if (byId is not null) return byId;
            try
            {
                var table = _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Table))
                         ?? _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid));
                if (table is not null) return table;
            }
            catch { }
            Thread.Sleep(300);
        }
        // Voies rapides épuisées sur tout le timeout → un ultime essai sur l'AutomationId lent.
        return FindByAutomationId("_dgvDemandes");
    }

    private static bool IsJ00Liaison(string s)
    {
        s = s.Trim();
        if (s.Length < 4 || (s[0] != 'J' && s[0] != 'j')) return false;
        for (int i = 1; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
        return true;
    }

    /// <summary>
    /// Dans la grille des demandes (après <see cref="OpenAlerteRcs"/>), trouve la cellule à
    /// double-cliquer pour ouvrir la 1ère demande du type voulu :
    ///   dcademat=true  → 1ère cellule "Traitement" == "DCADEMAT"
    ///   dcademat=false → 1ère cellule "N° Liaison" en J00… (formalités INPI)
    /// Override env : RIG_ALERTES_DCADEMAT / RIG_ALERTES_LIAISON (substring à chercher).
    /// </summary>
    /// <summary>Centre écran de la cellule de la 1ère demande du type voulu, lue via MSAA (oleacc) :
    /// le DemandeRigDataGridView custom n'expose NI Grid NI Table pattern UIA (0 descendant), mais
    /// supporte LegacyIAccessible. On lit accName/accValue + accLocation par cellule.
    ///   dcademat=true  → cellule "Traitement" == "DCADEMAT"
    ///   dcademat=false → cellule "N° Liaison" en J00… (formalités INPI)
    /// Override env : RIG_ALERTES_DCADEMAT / RIG_ALERTES_LIAISON (substring).</summary>
    private (int cx, int cy, string text)? FindDemandeCell(bool dcademat)
    {
        var list = FindDemandeCells(dcademat, 1);
        return list.Count > 0 ? list[0] : ((int cx, int cy, string text)?)null;
    }

    /// <summary>Jusqu'à <paramref name="max"/> cellules-lignes des demandes du type voulu, non « en
    /// cours », lues via MSAA (le DemandeRigDataGridView custom n'expose ni Grid ni Table pattern UIA).
    /// Permet de réessayer une autre demande si la 1ère ne produit pas de signal d'ouverture.</summary>
    private List<(int cx, int cy, string text)> FindDemandeCells(bool dcademat, int max)
    {
        IntPtr hwnd = ResolveDemandeGridHwnd();
        if (hwnd == IntPtr.Zero) { Console.WriteLine("      → grille introuvable pour FindDemandeCells"); return new(); }

        var Match = BuildDemandeMatch(dcademat);
        var cells = CollectCellsMsaa(hwnd, Match, max, out int scanned);
        Console.WriteLine($"      → {cells.Count} demande(s) {(dcademat ? "DCADEMAT" : "formalités J00")} via MSAA ({scanned} cellules scannées)"
            + (cells.Count > 0 ? $" ; 1ère : '{cells[0].text}' @ ({cells[0].cx},{cells[0].cy})" : $" — aucune (hwnd=0x{hwnd.ToInt64():X}, override RIG_ALERTES_* ?)"));
        return cells;
    }

    /// <summary>Prédicat de sélection d'une demande (ligne du DataGridView) du type voulu, non « en
    /// cours ». Extrait de <see cref="FindDemandeCells"/> pour être réutilisé par le scroll-into-view
    /// (qui re-lit les cellules après chaque cran de molette pour retrouver la MÊME demande par son texte).</summary>
    private static Func<string, bool> BuildDemandeMatch(bool dcademat, bool includeMyEnCours = false)
    {
        // La logique de matching est PURE et testee (LegacyParsing.DemandeRowMatches) — ici on ne lit que
        // l'override env, le flag d'inclusion des demandes "en cours par moi" (reprise interrompue) et le
        // jeton de l'utilisateur courant (pour SAUTER pre-open les demandes verrouillees par un AUTRE user).
        var overrideVal = Environment.GetEnvironmentVariable(dcademat ? "RIG_ALERTES_DCADEMAT" : "RIG_ALERTES_LIAISON");
        return nm => LegacyParsing.DemandeRowMatches(nm, dcademat, includeMyEnCours, overrideVal, CurrentGridUserToken());
    }

    /// <summary>
    /// Jeton de l'utilisateur RIG courant tel qu'il apparait dans la colonne "Utilisateur" de la grille des
    /// demandes (proprietaire/verrou d'une ligne). Sert a DETECTER + SAUTER les demandes verrouillees par un
    /// AUTRE utilisateur AVANT de tenter l'ouverture (cf. <see cref="LegacyParsing.IsLockedByOtherUser"/> —
    /// rouvrir une demande tenue par un autre user ne produit AUCUN signal d'ouverture, preuve run live).
    /// Source : env RIG_LEGACY_GRID_USER si fournie (override explicite), sinon le surname derive de la
    /// session Windows (<c>Environment.UserName</c> = "raphael.vilain" -&gt; "vilain", qui matche le 1er mot
    /// de "VILAIN Raphel" affiche dans la grille). Le login RIG smoke est mono-compte (base 9995, titre
    /// "VILAIN") aligne sur la session Windows. Retour vide -&gt; filtre verrou-autrui inactif (jamais de skip
    /// par defaut faute d'identite).
    /// </summary>
    private static string CurrentGridUserToken()
    {
        var ovr = Environment.GetEnvironmentVariable("RIG_LEGACY_GRID_USER");
        if (!string.IsNullOrWhiteSpace(ovr)) return ovr.Trim();
        return LegacyParsing.CurrentUserSurname(Environment.UserName);
    }

    /// <summary>HWND du DataGridView des demandes (ultDgvResultats / Table / _dgvDemandes), sinon du
    /// Pane wrapper, sinon la fenêtre. Ordre rapide d'abord (ultDgvResultats) ; _dgvDemandes en dernier
    /// (lookup lent quand absent). Sert à AccessibleObjectFromWindow (MSAA) ET au scroll molette.
    /// IntPtr.Zero si rien d'exploitable. Extrait de <see cref="FindDemandeCells"/>.</summary>
    private IntPtr ResolveDemandeGridHwnd()
    {
        AutomationElement? grid = FindByAutomationId("ultDgvResultats");
        if (grid is null) { try { grid = _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Table)) ?? _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid)); } catch { } }
        if (grid is null) grid = FindByAutomationId("_dgvDemandes");
        if (grid is null) return IntPtr.Zero;

        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = grid.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        if (hwnd == IntPtr.Zero)
        {
            try { var inner = grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.Table)); if (inner != null) hwnd = inner.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        }
        if (hwnd == IntPtr.Zero) { try { hwnd = _window!.Properties.NativeWindowHandle.ValueOrDefault; } catch { } }
        return hwnd;
    }

    // ── MSAA (oleacc) : lecture des cellules quand UIA est aveugle ──────────────
    private static readonly Guid IID_IAccessible = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleChildren(Accessibility.IAccessible paccContainer,
        int iChildStart, int cChildren,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct)] object[] rgvarChildren,
        out int pcObtained);

    private static string SafeAcc(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }

    /// <summary>Parcourt l'arbre MSAA du client de <paramref name="hwnd"/> (DataGridView WinForms) et
    /// collecte jusqu'à <paramref name="max"/> cellules-lignes dont (accName + accValue) matchent, avec
    /// leur centre écran (accLocation). Lecture seule.</summary>
    private List<(int cx, int cy, string text)> CollectCellsMsaa(IntPtr hwnd, Func<string, bool> match, int max, out int scanned)
    {
        scanned = 0;
        var results = new List<(int cx, int cy, string text)>();
        const uint OBJID_CLIENT = 0xFFFFFFFC;
        var iid = IID_IAccessible;
        Accessibility.IAccessible? root = null;
        try
        {
            if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var obj) != 0 || obj is not Accessibility.IAccessible a)
            { Console.WriteLine("      ⓘ AccessibleObjectFromWindow : pas d'IAccessible."); return results; }
            root = a;
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ AccessibleObjectFromWindow jeté : {ex.Message}"); return results; }

        int localScanned = 0;
        void Walk(Accessibility.IAccessible node, int depth)
        {
            if (results.Count >= max || depth > 8) return;
            int count; try { count = node.accChildCount; } catch { return; }
            if (count <= 0) return;
            var kids = new object[count]; int got;
            try { if (AccessibleChildren(node, 0, count, kids, out got) != 0) return; } catch { return; }
            for (int i = 0; i < got && results.Count < max; i++)
            {
                var k = kids[i];
                if (k is Accessibility.IAccessible childAcc)
                {
                    string text = (SafeAcc(() => childAcc.get_accName(0)) + " " + SafeAcc(() => childAcc.get_accValue(0))).Trim();
                    if (!string.IsNullOrWhiteSpace(text)) localScanned++;
                    // Une LIGNE a un accName concaténé par ';' (toutes les colonnes) → on matche au niveau
                    // ligne (centre ~ milieu de grille = CellDoubleClick fiable) et on NE descend PAS dedans :
                    // sinon une ligne « en cours » (skippée) verrait ses CELLULES matcher le J00/DCADEMAT
                    // (elles n'ont pas le ';X;') → bypass du skip + clic sur cellule de gauche (inopérant).
                    if (text.IndexOf(';') >= 0)
                    {
                        if (match(text))
                        {
                            try { childAcc.accLocation(out int l, out int t, out int w, out int h, 0); results.Add((l + w / 2, t + h / 2, text)); } catch { }
                        }
                    }
                    else
                    {
                        Walk(childAcc, depth + 1); // conteneur (table/client/groupe) → on descend
                    }
                }
                else if (k is int childId && childId != 0)
                {
                    string text = (SafeAcc(() => node.get_accName(childId)) + " " + SafeAcc(() => node.get_accValue(childId))).Trim();
                    if (!string.IsNullOrWhiteSpace(text)) localScanned++;
                    if (text.IndexOf(';') >= 0 && match(text))
                    {
                        try { node.accLocation(out int l, out int t, out int w, out int h, childId); results.Add((l + w / 2, t + h / 2, text)); } catch { }
                    }
                }
            }
        }
        try { Walk(root!, 0); } catch (Exception ex) { Console.WriteLine($"      ⓘ Walk MSAA jeté : {ex.Message}"); }
        scanned = localScanned;
        return results;
    }

    /// <summary>Cellule-ligne de demande RETROUVÉE via MSAA avec, EN PLUS du centre écran et du texte
    /// agrégé, le couple (<see cref="Node"/>, <see cref="ChildId"/>) IAccessible nécessaire pour appeler
    /// <c>accSelect</c> (sélection native qui scrolle la ligne dans la vue) et RE-LIRE <c>accLocation</c>
    /// après sélection, ainsi que l'index 0-based de ligne (<see cref="RowIndex"/>, parsé du Name MSAA
    /// "Ligne N" si exposé, sinon -1) pour le fallback clavier Home + Down x index + Entrée.</summary>
    private sealed class DemandeCellHit
    {
        public int Cx;
        public int Cy;
        public string Text = "";
        public Accessibility.IAccessible Node = null!;   // IAccessible sur lequel appeler accSelect/accLocation
        public object ChildId = (object)0;               // CHILDID_SELF (0) ou l'id enfant entier
        public int RowIndex = -1;                         // 0-based (Down count apres Home), -1 si inconnu
    }

    /// <summary>Variante de <see cref="CollectCellsMsaa"/> qui CONSERVE l'IAccessible + childId de chaque
    /// ligne matchée (pour <c>accSelect</c> + re-lecture <c>accLocation</c>) et l'index de ligne (Name MSAA
    /// "Ligne N"). Même parcours/filtre (lignes au Name concaténé ';', skip des conteneurs) que
    /// <see cref="CollectCellsMsaa"/> — on garde les deux pour ne pas changer la signature des nombreux
    /// appelants de l'originale. Lecture seule (la sélection se fait dans l'appelant via accSelect).</summary>
    private List<DemandeCellHit> CollectDemandeCellsMsaaRich(IntPtr hwnd, Func<string, bool> match, int max)
    {
        var results = new List<DemandeCellHit>();
        if (hwnd == IntPtr.Zero) return results;
        const uint OBJID_CLIENT = 0xFFFFFFFC;
        var iid = IID_IAccessible;
        Accessibility.IAccessible? root = null;
        try
        {
            if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var obj) != 0 || obj is not Accessibility.IAccessible a)
            { Console.WriteLine("      ⓘ AccessibleObjectFromWindow (rich) : pas d'IAccessible."); return results; }
            root = a;
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ AccessibleObjectFromWindow (rich) jeté : {ex.Message}"); return results; }

        void Walk(Accessibility.IAccessible node, int depth)
        {
            if (results.Count >= max || depth > 8) return;
            int count; try { count = node.accChildCount; } catch { return; }
            if (count <= 0) return;
            var kids = new object[count]; int got;
            try { if (AccessibleChildren(node, 0, count, kids, out got) != 0) return; } catch { return; }
            for (int i = 0; i < got && results.Count < max; i++)
            {
                var k = kids[i];
                if (k is Accessibility.IAccessible childAcc)
                {
                    string name = SafeAcc(() => childAcc.get_accName(0));
                    string text = (name + " " + SafeAcc(() => childAcc.get_accValue(0))).Trim();
                    if (text.IndexOf(';') >= 0)
                    {
                        if (match(text))
                        {
                            try
                            {
                                childAcc.accLocation(out int l, out int t, out int w, out int h, 0);
                                results.Add(new DemandeCellHit { Cx = l + w / 2, Cy = t + h / 2, Text = text,
                                    Node = childAcc, ChildId = (object)0, RowIndex = LegacyParsing.ParseLigneIndex(name) });
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        Walk(childAcc, depth + 1);
                    }
                }
                else if (k is int childId && childId != 0)
                {
                    string name = SafeAcc(() => node.get_accName(childId));
                    string text = (name + " " + SafeAcc(() => node.get_accValue(childId))).Trim();
                    if (text.IndexOf(';') >= 0 && match(text))
                    {
                        try
                        {
                            node.accLocation(out int l, out int t, out int w, out int h, childId);
                            results.Add(new DemandeCellHit { Cx = l + w / 2, Cy = t + h / 2, Text = text,
                                Node = node, ChildId = (object)childId, RowIndex = LegacyParsing.ParseLigneIndex(name) });
                        }
                        catch { }
                    }
                }
            }
        }
        try { Walk(root!, 0); } catch (Exception ex) { Console.WriteLine($"      ⓘ Walk MSAA (rich) jeté : {ex.Message}"); }
        return results;
    }

    /// <summary>
    /// Bande visible [top,bottom] OÙ un clic sur une cellule de la grille est fiable : intersection du
    /// rectangle ÉCRAN de la grille (<paramref name="gridHwnd"/>) et de l'écran courant (0..hauteur).
    /// Si le hwnd grille n'a pas de rect exploitable, repli sur tout l'écran. Sert au scroll-into-view.
    /// </summary>
    private (int top, int bottom) GetGridVisibleBand(IntPtr gridHwnd)
    {
        int screenH;
        try { screenH = GetSystemMetrics(1); } catch { screenH = 1080; }       // SM_CYSCREEN
        if (screenH <= 0) screenH = 1080;
        int top = 0, bottom = screenH;
        if (gridHwnd != IntPtr.Zero && GetWindowRect(gridHwnd, out var r) && r.Bottom > r.Top)
        {
            top = Math.Max(0, r.Top);
            bottom = Math.Min(screenH, r.Bottom);
        }
        if (bottom <= top) { top = 0; bottom = screenH; }                       // garde-fou rect degenere
        return (top, bottom);
    }

    /// <summary>
    /// SÉLECTIONNE nativement la ligne <paramref name="hit"/> via MSAA <c>accSelect</c>
    /// (SELFLAG_TAKEFOCUS | SELFLAG_TAKESELECTION) : un DataGridView WinForms scrolle alors AUTOMATIQUEMENT
    /// la ligne sélectionnée dans la zone visible (corrige le bug run live : les coords MSAA sont absolues
    /// écran, une ligne en bas de grille a Y &gt; hauteur écran → l'ancien scroll molette WM_MOUSEWHEEL ne
    /// faisait RIEN et le double-clic tapait dans le vide). Déclenche le trigger d'ouverture dans cet ordre :
    ///   1. accSelect réussi → RE-LIRE accLocation ; si la ligne est maintenant dans la bande visible
    ///      [<paramref name="top"/>,<paramref name="bottom"/>] → double-clic aux NOUVELLES coords ;
    ///   2. sinon (ou re-lecture impossible) → Entrée (VK_RETURN) sur le hwnd grille : RIG ouvre la ligne
    ///      SÉLECTIONNÉE (ReprendreProcessus se déclenche sur Entrée dans ce DGV) — pas besoin de coords ;
    ///   3. fallback si accSelect échoue (HRESULT non nul → exception) OU n'a pas pu être tenté : navigation
    ///      clavier Home + Down × <see cref="DemandeCellHit.RowIndex"/> + Entrée (index lu du Name "Ligne N").
    /// Renvoie l'<see cref="Action"/> trigger à passer à <see cref="VerifyDocumentOpened"/> (qui mesure le
    /// signal d'ouverture). Ne vérifie PAS lui-même l'ouverture — c'est l'appelant qui supervise.
    /// </summary>
    private Action BuildOpenDemandeTrigger(IntPtr gridHwnd, DemandeCellHit hit, int top, int bottom)
    {
        // (1) accSelect : sélection native qui scrolle la ligne dans la vue.
        bool selected = false;
        try
        {
            hit.Node.accSelect(SELFLAG_TAKEFOCUS | SELFLAG_TAKESELECTION, hit.ChildId);
            selected = true;
            Console.WriteLine($"      → accSelect(TAKEFOCUS|TAKESELECTION, childId={hit.ChildId}) OK → ligne sélectionnée (scroll natif DGV).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      → ⚠ accSelect a jeté ({ex.GetType().Name}: {ex.Message}) → fallback navigation clavier.");
        }

        // (2) accSelect OK → re-lire accLocation : la ligne a-t-elle été ramenée dans la bande visible ?
        if (selected)
        {
            // Laisser le DGV traiter le scroll induit par la sélection (poll court, pas de sleep fixe long).
            int newCy = hit.Cy, newCx = hit.Cx; bool visible = false;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1200)
            {
                try
                {
                    hit.Node.accLocation(out int l, out int t, out int w, out int h, hit.ChildId);
                    newCx = l + w / 2; newCy = t + h / 2;
                    if (LegacyParsing.IsCellYVisible(newCy, top, bottom)) { visible = true; break; }
                }
                catch { }
                Thread.Sleep(120);
            }

            if (visible)
            {
                int cx = newCx, cy = newCy;
                Console.WriteLine($"      → Ligne visible après accSelect (Y {hit.Cy} → {cy}, bande {top}-{bottom}) → double-clic aux nouvelles coords.");
                return () => { Console.WriteLine("      → Double-clic → ReprendreProcessus"); Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: true); };
            }

            // (2b) Sélectionnée mais toujours hors bande (ou re-lecture KO) → ouvrir par Entrée la ligne sélectionnée.
            Console.WriteLine($"      → Ligne sélectionnée mais hors bande visible (Y={newCy}, bande {top}-{bottom}) → Entrée sur la grille sélectionnée (ouvre la ligne sans coords).");
            return () => PressEnterOnGrid(gridHwnd);
        }

        // (3) Fallback clavier : Home (1re ligne) + Down × index + Entrée. Index lu du Name MSAA "Ligne N".
        int rowIndex = hit.RowIndex;
        Console.WriteLine($"      → Fallback clavier : Home + Down × {(rowIndex >= 0 ? rowIndex.ToString() : "?")} + Entrée (index ligne = {(rowIndex >= 0 ? rowIndex.ToString() : "inconnu ('Ligne N' absent)")}).");
        return () => OpenDemandeByKeyboard(gridHwnd, rowIndex);
    }

    /// <summary>Donne le focus clavier à la grille puis poste Entrée (VK_RETURN) : RIG ouvre la ligne
    /// actuellement SÉLECTIONNÉE (ReprendreProcessus). Mouse-free / focus-free (ForceFocus = AttachThreadInput
    /// + SetFocus, pas de SetForegroundWindow).</summary>
    private void PressEnterOnGrid(IntPtr gridHwnd)
    {
        if (gridHwnd == IntPtr.Zero) { Console.WriteLine("      ⚠ Entrée non envoyée : hwnd grille nul."); return; }
        Interaction.ForceFocus(gridHwnd);
        Console.WriteLine("      → PostKey VK_RETURN sur la grille (ouvre la ligne sélectionnée).");
        Interaction.PostKey(gridHwnd, VK_RETURN_KEY);
    }

    /// <summary>Fallback d'ouverture 100 % clavier quand accSelect a échoué : focus grille, Home (va sur la
    /// 1re ligne data), Down × <paramref name="rowIndex0Based"/> pour atteindre la ligne cible, puis Entrée
    /// (ReprendreProcessus). Si <paramref name="rowIndex0Based"/> &lt; 0 (Name "Ligne N" non exposé), on
    /// tente quand même Home + Entrée (1re ligne) — best-effort, l'appelant supervise le signal d'ouverture
    /// et réessaiera une autre demande si rien ne s'ouvre.</summary>
    private void OpenDemandeByKeyboard(IntPtr gridHwnd, int rowIndex0Based)
    {
        if (gridHwnd == IntPtr.Zero) { Console.WriteLine("      ⚠ Fallback clavier impossible : hwnd grille nul."); return; }
        Interaction.ForceFocus(gridHwnd);
        Interaction.PostKey(gridHwnd, VK_HOME_KEY);          // 1re ligne data
        Thread.Sleep(80);
        int downs = rowIndex0Based > 0 ? rowIndex0Based : 0;
        for (int d = 0; d < downs; d++) { Interaction.PostKey(gridHwnd, VK_DOWN_KEY); Thread.Sleep(20); }
        Console.WriteLine($"      → Clavier : Home + {downs} × Down + Entrée.");
        Thread.Sleep(80);
        Interaction.PostKey(gridHwnd, VK_RETURN_KEY);        // ouvre la ligne sélectionnée
    }

    /// <summary>Ouvre une demande (formalités J00 ou DCADEMAT) en la SÉLECTIONNANT via MSAA accSelect (qui
    /// scrolle la ligne dans la vue), puis double-clic aux nouvelles coords si elle est visible, sinon Entrée
    /// sur la ligne sélectionnée (fallback clavier Home+Down si accSelect échoue). Vérifie qu'un document/onglet
    /// s'ouvre sans crash (DocDemat / RigAffichageDoc si l'étape interrompue le rouvre, sinon l'onglet de la
    /// demande). Réessaie sur les demandes suivantes du même type si la 1ère ne produit pas de signal
    /// (robustesse données DEV).
    /// ⚠ CORRIGE le bug run live : les coords MSAA (accLocation) sont absolues écran ; une demande en bas de
    /// grille a Y &gt; hauteur écran (1585/4513/5657 sur 1080) ; l'ancien scroll molette WM_MOUSEWHEEL ne
    /// faisait RIEN et le double-clic tapait dans le vide → aucune demande ouvrable. accSelect scrolle
    /// nativement la ligne, et l'ouverture par Entrée ne dépend plus d'aucune coordonnée écran.
    /// THROW si AUCUNE demande ne produit de signal d'ouverture (l'appelant NE doit PAS enchaîner l'étape
    /// réclamation/validation si rien n'est ouvert — sinon il cherche des contrôles absents pendant ~111 s).
    /// <paramref name="allowMyEnCours"/> : si true, on N'EXCLUT PAS les demandes déjà « en cours par MOI »
    /// (colonne En cours = "X") du matching — réservé à la REPRISE non mutante (scénario interrompue :
    /// rouvrir une demande, y compris une déjà ouverte par ma session = la reprise). Les actions MUTANTES
    /// (validation/réclamation/refus) gardent allowMyEnCours=false (idempotence). Le verrou par un AUTRE
    /// user reste détecté post-open (IsDemandeLockedOnScreen) et la demande est sautée dans les deux cas.</summary>
    public void OpenFirstDemandeAndVerify(bool dcademat, bool allowMyEnCours = false)
    {
        IntPtr gridHwnd = ResolveDemandeGridHwnd();
        if (gridHwnd == IntPtr.Zero)
            throw new Exception("Grille des demandes introuvable (hwnd nul) — impossible d'ouvrir une demande.");

        var Match = BuildDemandeMatch(dcademat, allowMyEnCours);
        // On collecte JUSQU'À N demandes : les données DEV au hasard contiennent souvent des demandes
        // VERROUILLÉES par un autre user (écran sans formulaire, juste « Une demande est en cours sur ce
        // dossier » + bouton Quitter). On en saute jusqu'à N pour avoir une chance d'en trouver une
        // exploitable. ⚠ 2026-06-04 : l'alerte « réclamation » (911 demandes) DCADEMAT est fortement
        // verrouillée par une AUTRE session (run live dca-reclamation/dca-refus FAIL = top demandes lockées,
        // alors que les formalités J00 de la même alerte sont libres → form-reclamation/form-refus verts).
        // On échantillonne donc PLUS profond en DCADEMAT (24) qu'en J00 (12) pour traverser le bloc verrouillé
        // et atteindre une demande DCADEMAT libre plus bas dans la grille. Overridable RIG_DCADEMAT_MAX_DEMANDES
        // (borné [3..60] pour ne pas exploser le temps de run).
        int maxAttempts = dcademat ? 24 : 12;
        var maxEnv = Environment.GetEnvironmentVariable("RIG_DCADEMAT_MAX_DEMANDES");
        if (!string.IsNullOrWhiteSpace(maxEnv) && int.TryParse(maxEnv, out var mp)) maxAttempts = Math.Max(3, Math.Min(60, mp));
        var hits = CollectDemandeCellsMsaaRich(gridHwnd, Match, maxAttempts);
        Console.WriteLine($"      → {hits.Count} demande(s) {(dcademat ? "DCADEMAT" : "formalités J00")} via MSAA (rich)"
            + (hits.Count > 0 ? $" ; 1ère : '{LegacyParsing.Truncate(hits[0].Text, 50)}' @ ({hits[0].Cx},{hits[0].Cy}) rowIndex={hits[0].RowIndex}" : ""));
        if (hits.Count == 0)
        {
            // 0 candidate retenue. Distinguer « aucune demande de ce type dans la grille » (vraie absence de
            // données) de « il y a des demandes de ce type mais TOUTES verrouillées par un AUTRE user » (limite
            // DONNÉES : verrou-autrui détecté pré-open via la colonne Utilisateur, cf. CurrentGridUserToken /
            // LegacyParsing.IsLockedByOtherUser). On RECOMPTE en ignorant le filtre verrou-autrui pour savoir
            // combien de lignes du type existaient avant ce filtre.
            string currentUser = CurrentGridUserToken();
            var ovrType = Environment.GetEnvironmentVariable(dcademat ? "RIG_ALERTES_DCADEMAT" : "RIG_ALERTES_LIAISON");
            Func<string, bool> typeOnly = nm => LegacyParsing.DemandeRowMatches(nm, dcademat, includeMyEnCours: allowMyEnCours, overrideSubstring: ovrType, currentUserToken: null);
            var typeHits = CollectDemandeCellsMsaaRich(gridHwnd, typeOnly, maxAttempts);
            int lockedByOthers = typeHits.Count(h => LegacyParsing.IsLockedByOtherUser(h.Text, currentUser));
            if (typeHits.Count > 0 && lockedByOthers >= typeHits.Count)
            {
                try { CaptureScreenshot("open-demande-0-candidate-non-verrouillee"); } catch { }
                throw new Exception($"0 candidate non-verrouillée parmi {typeHits.Count} {(dcademat ? "demande(s) DCADEMAT" : "formalité(s) J00")} "
                    + $"interrompue(s) : toutes détenues par un AUTRE utilisateur (colonne Utilisateur ≠ « {currentUser} »). "
                    + "Limite DONNÉES (verrous transitoires par d'autres sessions sur l'environnement DEV), PAS un échec logique : "
                    + "rouvrir une demande tenue par un autre user ne produit aucun signal d'ouverture. "
                    + "Relancer (verrous libérés) ou override RIG_ALERTES_* / RIG_LEGACY_GRID_USER.");
            }
            throw new Exception($"Aucune demande {(dcademat ? "DCADEMAT" : "formalités J00")} (non « en cours ») dans la grille (données DEV ? override RIG_ALERTES_*).");
        }

        var (top, bottom) = GetGridVisibleBand(gridHwnd);
        Exception? last = null;
        int lockedCount = 0;       // demandes ouvertes MAIS verrouillées par un autre user (sautées)
        int noSignalCount = 0;     // demandes sans signal d'ouverture (sélection/Entrée KO)
        int selfLockCount = 0;     // demandes « en cours par MOI » (En cours=X, owner=moi) refusées par RIG (garde modale)
        // Reprise (scénario interrompue) : autorise-t-on à lever NOTRE PROPRE verrou « en cours » stale via le
        // geste RIG « Supprimer l'état en cours » ? C'est une ÉCRITURE SQL → OFF par défaut (hard rule #13).
        // ON uniquement si l'opérateur a posé RIG_LEGACY_CLEAR_MY_ENCOURS=1 (et seulement en mode reprise).
        string token = CurrentGridUserToken();
        bool clearMyEnCoursAuthorized = allowMyEnCours
            && IsEnvFlagOn(Environment.GetEnvironmentVariable("RIG_LEGACY_CLEAR_MY_ENCOURS"));
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            bool isMyEnCoursSelfLock = LegacyParsing.IsMyEnCoursSelfLock(hit.Text, token);
            try
            {
                Console.WriteLine($"      → Tentative {i + 1}/{hits.Count} : demande ('{LegacyParsing.Truncate(hit.Text, 50)}') Y={hit.Cy} rowIndex={hit.RowIndex}"
                    + $" [Utilisateur='{LegacyParsing.ExtractUtilisateurColumn(hit.Text)}'{(isMyEnCoursSelfLock ? ", En cours=X (verrou self)" : "")}]");

                // (Reprise + autorisé) Si la ligne porte MON propre verrou « En cours »=X, RIG refusera de la
                // rouvrir (garde « déjà en cours d'exécution ») → on lève le verrou AVANT d'ouvrir (geste RIG natif).
                if (isMyEnCoursSelfLock && clearMyEnCoursAuthorized)
                    TryClearMyEnCoursViaMenu(hit);

                var trigger = BuildOpenDemandeTrigger(gridHwnd, hit, top, bottom);
                try { VerifyDocumentOpened(trigger, "Demande (reprise)", waitSeconds: 15); }
                catch (Exception openEx)
                {
                    // Pas de signal d'ouverture. Cause la PLUS fréquente sur les formalités interrompues : la garde
                    // modale RIG « déjà en cours d'exécution » (DMND_EN_COURS=1). On la détecte + on la ferme, et
                    // si c'est NOTRE verrou + autorisé, on lève le verrou puis on RE-tente l'ouverture UNE fois.
                    bool guard = IsDejaEnCoursGuardOnScreen();
                    if (guard)
                    {
                        Console.WriteLine($"      ⓘ Tentative {i + 1} : RIG a refusé l'ouverture — garde modale « demande déjà en cours d'exécution » "
                            + $"(DMND_EN_COURS=1 ; {LegacyParsing.Truncate(openEx.Message, 70)}). On ferme la boîte.");
                        DismissDejaEnCoursGuard();
                    }
                    if (isMyEnCoursSelfLock && clearMyEnCoursAuthorized)
                    {
                        // 2e essai : lever le verrou self puis rouvrir (la 1re levée a pu échouer / l'ordre menu→open
                        // a pu être contrarié par la garde). Une seule reprise par demande (anti-boucle).
                        if (TryClearMyEnCoursViaMenu(hit))
                        {
                            var trigger2 = BuildOpenDemandeTrigger(gridHwnd, hit, top, bottom);
                            VerifyDocumentOpened(trigger2, "Demande (reprise, après levée du verrou)", waitSeconds: 15);
                        }
                        else throw; // verrou non levable (mur HDESK) → propage l'échec d'ouverture initial
                    }
                    else
                        throw; // pas de reprise autorisée → on remonte (compté ci-dessous selon le type de verrou)
                }

                // ── Signal d'ouverture reçu, MAIS la demande peut être VERROUILLÉE (lockée par un autre user) :
                //    l'écran DCADEMAT n'affiche alors aucun formulaire, juste un message rouge « Une demande est
                //    en cours sur ce dossier — D… par <user> » + bouton « Quitter » (cf. run live, screenshot).
                //    On détecte le verrou ; si présent → on ferme (Quitter) et on essaie la demande SUIVANTE.
                if (IsDemandeLockedOnScreen(out string lockMsg))
                {
                    lockedCount++;
                    Console.WriteLine($"      ⚠ Tentative {i + 1} : demande VERROUILLÉE par un autre user — « {LegacyParsing.Truncate(lockMsg, 90)} ». "
                        + "On clique « Quitter » et on essaie la demande suivante.");
                    QuitterDemandeVerrouillee();
                    last = new Exception($"demande verrouillée ({LegacyParsing.Truncate(lockMsg, 90)})");
                    continue;
                }

                Console.WriteLine($"      ✓ Demande ouverte et exploitable (non verrouillée) à la tentative {i + 1}/{hits.Count}.");
                return; // signal reçu + non verrouillée → succès
            }
            catch (Exception ex)
            {
                last = ex; noSignalCount++;
                // Compte les candidates « en cours par MOI » (verrou self stale) qui n'ont pas pu être ouvertes :
                // sert au message de sortie spécifique (toutes self-lock → autoriser RIG_LEGACY_CLEAR_MY_ENCOURS).
                if (isMyEnCoursSelfLock) selfLockCount++;
                Console.WriteLine($"      ⚠ Tentative {i + 1} sans signal : {ex.Message}");
            }
        }

        // Skip propre (pas d'exception « brute » : message orienté cause/donnée, capté par TryStep comme un Fail lisible).
        // Cas spécifique reprise : TOUTES les candidates étaient « en cours par MOI » (verrou self stale) et le
        // déverrouillage n'était pas autorisé → ce n'est PAS un bug logique mais un état de DONNÉES + un GESTE
        // explicitement gardé (écriture SQL). Message actionnable distinct.
        if (allowMyEnCours && selfLockCount > 0 && selfLockCount >= hits.Count)
        {
            try { CaptureScreenshot("open-demande-toutes-en-cours-par-moi"); } catch { }
            throw new Exception($"Les {hits.Count} {(dcademat ? "demande(s) DCADEMAT" : "formalité(s) J00")} interrompue(s) candidates sont "
                + "TOUTES « en cours par MOI » (colonne « En cours »=X, owner=utilisateur courant = verrou self laissé par un "
                + "run smoke précédent ; DMND_EN_COURS=1). RIG refuse de rouvrir une demande en cours (garde modale « déjà en "
                + "cours d'exécution », FormRigClientAccueil._ReprendreProcessus) → le double-clic n'ouvre rien. Mécanisme de "
                + "reprise = LEVER le verrou via le menu « Supprimer l'état en cours » (écrit en base : DMND_EN_COURS=0). Ce "
                + "geste est gardé : relancer avec RIG_LEGACY_CLEAR_MY_ENCOURS=1 (autorisation d'écriture SQL) pour ouvrir, "
                + "OU faire lever ces verrous self orphelins une fois côté données.");
        }
        if (lockedCount > 0 && lockedCount + noSignalCount >= hits.Count)
        {
            try { CaptureScreenshot("open-demande-toutes-verrouillees"); } catch { }
            throw new Exception($"Toutes les {hits.Count} demande(s) {(dcademat ? "DCADEMAT" : "formalités J00")} essayées sont "
                + $"verrouillées ou non exploitables (données DEV) : {lockedCount} verrouillée(s) par un autre user "
                + $"(« Une demande est en cours sur ce dossier »), {noSignalCount} sans signal d'ouverture. "
                + "Aucune demande exploitable parmi l'échantillon au hasard — relancer (autres données) ou override "
                + "RIG_ALERTES_* vers un dossier non verrouillé.");
        }
        throw new Exception($"Aucune des {hits.Count} demande(s) {(dcademat ? "DCADEMAT" : "formalités J00")} n'a produit de signal d'ouverture "
            + $"(sélection MSAA + Entrée + fallback clavier tous tentés ; {lockedCount} verrouillée(s) sautée(s), {selfLockCount} « en cours par moi » refusée(s)). "
            + $"Dernière erreur : {last?.Message}");
    }

    /// <summary>true si la variable d'environnement vaut un « ON » explicite (1 / true / yes / on, casse
    /// indifférente). null/vide/autre -&gt; false. Pur (hors lecture de l'argument déjà résolu).</summary>
    private static bool IsEnvFlagOn(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var t = v.Trim().ToLowerInvariant();
        return t == "1" || t == "true" || t == "yes" || t == "on";
    }

    /// <summary>
    /// true si l'écran DCADEMAT actuellement affiché est celui d'une demande VERROUILLÉE par un autre
    /// utilisateur : pas de formulaire (Configurer le dépôt / Réclamation / combo absents), seulement un
    /// message rouge « Une demande est en cours sur ce dossier — D… du … DCADEMAT par &lt;user&gt; » +
    /// bouton « Quitter ». accSelect a bien OUVERT la demande, mais elle est lockée.
    ///
    /// Le message UIA est souvent FRAGMENTÉ en plusieurs Text/Pane → on AGRÈGE le Name de tous les
    /// descendants Text/Pane (+ valeur Edit) puis on délègue à la logique pure
    /// <see cref="LegacyParsing.IsDossierLocked"/> (testée xUnit) sur le texte joint. Lecture seule.
    /// Retourne aussi (out) le 1er fragment qui contient le marqueur, pour le log.
    /// </summary>
    private bool IsDemandeLockedOnScreen(out string lockMessage)
    {
        lockMessage = "";
        if (_window is null) return false;
        var sb = new System.Text.StringBuilder();
        string? firstFragment = null;
        try
        {
            foreach (var c in _window.FindAllDescendants())
            {
                ControlType ct;
                try { ct = c.ControlType; } catch { continue; }
                if (ct != ControlType.Text && ct != ControlType.Pane && ct != ControlType.Edit) continue;
                var n = SafeText(() => c.Name);
                if (n.Length > 0)
                {
                    sb.Append(n).Append(' ');
                    if (firstFragment is null && n.IndexOf("en cours sur ce dossier", StringComparison.OrdinalIgnoreCase) >= 0)
                        firstFragment = n;
                }
                // Certains messages passent par la valeur d'un Edit (ValuePattern).
                if (ct == ControlType.Edit)
                {
                    try { if (c.Patterns.Value.IsSupported) { var v = SafeText(() => c.Patterns.Value.Pattern.Value.Value); if (v.Length > 0) sb.Append(v).Append(' '); } } catch { }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ IsDemandeLockedOnScreen scan jeté : {ex.GetType().Name}"); }

        string agg = sb.ToString();
        if (LegacyParsing.IsDossierLocked(agg))   // logique pure testée xUnit (robuste à la fragmentation : texte agrégé)
        {
            // Préfère le fragment exact pour le log ; sinon, une fenêtre autour du marqueur sur le texte agrégé.
            lockMessage = firstFragment ?? ExtractLockSnippet(agg);
            return true;
        }
        return false;
    }

    /// <summary>Extrait un extrait lisible (~110 chars) autour du marqueur de verrou dans le texte agrégé,
    /// pour le log quand le message est fragmenté (pas de Text unique contenant toute la phrase).</summary>
    private static string ExtractLockSnippet(string agg)
    {
        int idx = agg.IndexOf("Une demande est en cours", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = agg.IndexOf("demande est en cours sur ce dossier", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return LegacyParsing.Truncate(agg, 110);
        return LegacyParsing.Truncate(agg.Substring(idx), 110);
    }

    /// <summary>Ferme l'onglet/écran d'une demande VERROUILLÉE en cliquant le bouton « Quitter » (le seul
    /// contrôle actionnable de cet écran). On cible d'abord un Button/Pane dont le Name == « Quitter »
    /// (variantes « &amp;Quitter », « Quitter (Échap) »), sinon AutomationId contenant « quitter ». Best-effort :
    /// si introuvable, on tente VK_ESCAPE sur la fenêtre (Quitter est souvent mappé sur Échap). NON bloquant :
    /// on logue et on continue (l'appelant réessaiera de toute façon la demande suivante).</summary>
    private void QuitterDemandeVerrouillee()
    {
        if (_window is null) return;
        AutomationElement? quitBtn = null;
        try
        {
            quitBtn = _window.FindAllDescendants()
                .Where(c => { try { return c.IsAvailable && !c.IsOffscreen; } catch { return false; } })
                .FirstOrDefault(c =>
                {
                    ControlType ct; try { ct = c.ControlType; } catch { return false; }
                    if (ct != ControlType.Button && ct != ControlType.Pane) return false;
                    var n = SafeText(() => c.Name).Replace("&", "");
                    if (string.IsNullOrEmpty(n)) return false;
                    var nl = n.ToLowerInvariant();
                    // « Quitter » exact ou en tête (évite « Ne pas quitter » / longues phrases).
                    return nl == "quitter" || nl.StartsWith("quitter ") || nl.StartsWith("quitter(");
                });
            if (quitBtn is null)
            {
                var byId = _window.FindAllDescendants()
                    .FirstOrDefault(c => { var id = SafeText(() => c.AutomationId).ToLowerInvariant(); return id.Contains("quitter") || id.Contains("btnquitter") || id.Contains("cmdquitter"); });
                if (byId is not null) quitBtn = byId;
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ Recherche bouton « Quitter » jetée : {ex.GetType().Name}"); }

        if (quitBtn is not null)
        {
            Console.WriteLine($"      → Click « Quitter » : Type={SafeText(() => quitBtn.ControlType.ToString())} Id='{SafeText(() => quitBtn.AutomationId)}' Name='{SafeText(() => quitBtn.Name)}'");
            try { Interaction.Click(quitBtn); }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Click « Quitter » a jeté : {ex.Message} — fallback Échap."); TryEscapeOnWindow(); }
        }
        else
        {
            Console.WriteLine("      → Bouton « Quitter » introuvable — fallback VK_ESCAPE sur la fenêtre (Quitter ≈ Échap).");
            TryEscapeOnWindow();
        }

        // Laisser RIG fermer l'onglet/écran avant de réessayer la demande suivante (poll court, pas de sleep fixe long) :
        // on attend que le marqueur de verrou DISPARAISSE de l'écran (max ~4s).
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000 && IsDemandeLockedOnScreen(out _)) Thread.Sleep(250);
        Console.WriteLine(sw.ElapsedMilliseconds < 4000
            ? "      ✓ Écran de demande verrouillée fermé (marqueur de verrou disparu)."
            : "      → ⚠ Marqueur de verrou toujours présent après « Quitter » (4s) — on tente quand même la demande suivante.");
    }

    /// <summary>Poste VK_ESCAPE sur le hwnd de la fenêtre RIG (focus-free) — fallback pour fermer l'écran de
    /// demande verrouillée quand le bouton « Quitter » n'est pas localisable.</summary>
    private void TryEscapeOnWindow()
    {
        try
        {
            IntPtr h = IntPtr.Zero;
            try { if (_window is not null && _window.Properties.NativeWindowHandle.IsSupported) h = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            if (h != IntPtr.Zero) { Interaction.ForceFocus(h); Interaction.PostKey(h, VK_ESCAPE_KEY); }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ Fallback Échap jeté : {ex.GetType().Name}"); }
    }

    /// <summary>
    /// Attend que l'overlay de CHARGEMENT « Veuillez patienter… / Traitement en cours… » DISPARAISSE de
    /// l'écran avant de poursuivre (poll court, plafond <paramref name="maxMs"/>, sleeps 250ms). Tant qu'un
    /// Text/Pane UIA contient un libellé d'attente (cf. <see cref="LegacyParsing.IsLoadingOverlay"/>, logique
    /// pure testée xUnit, appliquée au texte AGRÉGÉ des descendants → robuste à la fragmentation UIA), on
    /// attend. Non bloquant : si l'overlay est toujours là au plafond, on logue et on rend la main (l'appelant
    /// tentera quand même de localiser ses contrôles ; sa propre vérif tranchera). Lecture seule.
    /// </summary>
    private void WaitForLoadingOverlayToClear(int maxMs = 15000)
    {
        if (_window is null) return;
        var sw = Stopwatch.StartNew();
        bool sawOverlay = false;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (!IsLoadingOverlayOnScreen()) break;
            sawOverlay = true;
            Thread.Sleep(250);
        }
        if (!sawOverlay)
            Console.WriteLine("      → (1b) Pas d'overlay « Veuillez patienter / Traitement en cours » — écran prêt.");
        else if (sw.ElapsedMilliseconds < maxMs)
            Console.WriteLine($"      ✓ (1b) Overlay de chargement disparu après {sw.ElapsedMilliseconds}ms — on cherche le combo.");
        else
            Console.WriteLine($"      → ⚠ (1b) Overlay « Veuillez patienter / Traitement en cours » toujours présent après {maxMs}ms — "
                + "on continue quand même (la recherche du combo / sa vérif tranchera).");
    }

    /// <summary>true si l'écran affiche actuellement l'overlay de chargement (« Veuillez patienter » /
    /// « Traitement en cours »). Agrège le Name des descendants Text/Pane puis délègue à la logique pure
    /// <see cref="LegacyParsing.IsLoadingOverlay"/>. Lecture seule.</summary>
    private bool IsLoadingOverlayOnScreen()
    {
        if (_window is null) return false;
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var c in _window.FindAllDescendants())
            {
                ControlType ct;
                try { ct = c.ControlType; } catch { continue; }
                if (ct != ControlType.Text && ct != ControlType.Pane) continue;
                var n = SafeText(() => c.Name);
                if (n.Length > 0) sb.Append(n).Append(' ');
                // Court-circuit : dès qu'un fragment seul matche, inutile de tout agréger.
                if (LegacyParsing.IsLoadingOverlay(n)) return true;
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ IsLoadingOverlayOnScreen scan jeté : {ex.GetType().Name}"); }
        return LegacyParsing.IsLoadingOverlay(sb.ToString());
    }

    /// <summary>true si l'écran affiche actuellement la GARDE modale RIG « Vous ne pouvez pas traiter une
    /// demande qui est déjà en cours d'exécution… » (cf. <see cref="LegacyParsing.IsDejaEnCoursGuard"/>).
    /// C'est la réponse de <c>ReprendreProcessus</c> quand on double-clique une demande dont
    /// <c>DMND_EN_COURS=1</c> : RIG ouvre cette boîte au lieu d'ouvrir la demande → AUCUN signal d'ouverture.
    /// Agrège le Name de tous les descendants Text/Pane/Edit (+ valeur Edit) puis délègue à la logique pure.
    /// Lecture seule. Si vrai, on DISMISS la boîte (Échap/Entrée) pour ne pas laisser un modal bloquant.</summary>
    private bool IsDejaEnCoursGuardOnScreen()
    {
        if (_window is null) return false;
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var c in _window.FindAllDescendants())
            {
                ControlType ct;
                try { ct = c.ControlType; } catch { continue; }
                if (ct != ControlType.Text && ct != ControlType.Pane && ct != ControlType.Edit) continue;
                var n = SafeText(() => c.Name);
                if (n.Length > 0) { sb.Append(n).Append(' '); if (LegacyParsing.IsDejaEnCoursGuard(n)) return true; }
                if (ct == ControlType.Edit)
                {
                    try { if (c.Patterns.Value.IsSupported) { var v = SafeText(() => c.Patterns.Value.Pattern.Value.Value); if (v.Length > 0) sb.Append(v).Append(' '); } } catch { }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ IsDejaEnCoursGuardOnScreen scan jeté : {ex.GetType().Name}"); }
        return LegacyParsing.IsDejaEnCoursGuard(sb.ToString());
    }

    /// <summary>Ferme la boîte modale « déjà en cours d'exécution » (DialogBox RIG à bouton unique « OK ») :
    /// clique « OK » si localisable, sinon poste Entrée puis Échap sur la fenêtre (best-effort, focus-free).
    /// Non bloquant. Sert à ne pas laisser un modal ouvert après une tentative d'ouverture refusée.</summary>
    private void DismissDejaEnCoursGuard()
    {
        if (_window is null) return;
        AutomationElement? okBtn = null;
        try
        {
            okBtn = _window.FindAllDescendants()
                .Where(c => { try { return c.IsAvailable && !c.IsOffscreen; } catch { return false; } })
                .FirstOrDefault(c =>
                {
                    ControlType ct; try { ct = c.ControlType; } catch { return false; }
                    if (ct != ControlType.Button && ct != ControlType.Pane) return false;
                    var nl = SafeText(() => c.Name).Replace("&", "").Trim().ToLowerInvariant();
                    return nl == "ok" || nl == "fermer" || nl.StartsWith("ok ") || nl.StartsWith("ok(");
                });
        }
        catch { }
        if (okBtn is not null) { try { Interaction.Click(okBtn); } catch { TryEscapeOnWindow(); } }
        else
        {
            try
            {
                IntPtr h = IntPtr.Zero;
                try { if (_window.Properties.NativeWindowHandle.IsSupported) h = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                if (h != IntPtr.Zero) { Interaction.ForceFocus(h); Interaction.PostKey(h, VK_RETURN_KEY); }
            }
            catch { }
            TryEscapeOnWindow();
        }
        // Laisser la boîte se fermer (poll court, plafond 3s).
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && IsDejaEnCoursGuardOnScreen()) Thread.Sleep(200);
    }

    /// <summary>
    /// MÉCANISME DE REPRISE d'une formalité interrompue VERROUILLÉE « en cours par MOI » (verrou self stale,
    /// colonne « En cours »=X, cf. <see cref="LegacyParsing.IsMyEnCoursSelfLock"/>) : RIG refuse de la rouvrir
    /// tant que <c>DMND_EN_COURS=1</c>. On lève le verrou via le GESTE RIG natif = menu contextuel de la grille
    /// « <b>Supprimer l'état en cours</b> » (<c>OPE_RESULTATS.tsmiDeleteEtatEnCoursDemande</c> →
    /// <c>Demande.Cloturer</c> remet <c>DMND_EN_COURS=0</c>), puis on laisse l'appelant re-tenter l'ouverture.
    ///
    /// ⚠ ÉCRITURE SQL : « Supprimer l'état en cours » modifie la base (clôt le verrou ; pour un user non-Amitel
    /// crée aussi une LIGNE_RAPP_GENE). Ce geste n'est donc exécuté QUE si l'opérateur l'a explicitement
    /// autorisé via l'env <c>RIG_LEGACY_CLEAR_MY_ENCOURS=1</c> (défaut OFF). Sans ce flag, on NE touche RIEN.
    ///
    /// Réutilise <see cref="DoOpenMenuAndClickItem"/> (sélection ligne + ouverture du ContextMenuStrip via
    /// VK_APPS/clic-droit + clic de l'item par MSAA) — message-based, marche sur HDESK. Retourne true si le
    /// geste a pu être déclenché (clic de l'item OK), false sinon (menu non matérialisé sur HDESK / item
    /// absent). Best-effort : l'appelant supervise ensuite le signal d'ouverture re-tenté.</summary>
    private bool TryClearMyEnCoursViaMenu(DemandeCellHit hit)
    {
        Console.WriteLine($"      → Reprise : levée du verrou self « En cours »=X via menu « Supprimer l'état en cours » "
            + $"(autorisé par RIG_LEGACY_CLEAR_MY_ENCOURS) sur ('{LegacyParsing.Truncate(hit.Text, 50)}').");
        try
        {
            DoOpenMenuAndClickItem(hit.Cx, hit.Cy, "Supprimer l'état en cours");
            // Laisser RIG exécuter le Cloturer (poll court) ; pas de vérif visuelle possible sur HDESK.
            Thread.Sleep(800);
            Console.WriteLine("      ✓ Geste « Supprimer l'état en cours » déclenché — on re-tente l'ouverture de la demande.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ « Supprimer l'état en cours » non déclenché ({ex.Message}) — "
                + "menu contextuel non matérialisé (mur HDESK) ou item absent. Verrou NON levé.");
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // DCADEMAT validation — step "Action" (Étape 3)
    // Après OpenFirstDemandeAndVerify(dcademat:true) → écran "Configurer le dépôt".
    // La grille "Exercices" a une 1re colonne "DCA" (case à cocher). Workflow :
    //   a. cocher la case DCA de la 1re ligne d'exercice (si pas déjà cochée),
    //   b. cliquer "Valider" (ClickValiderInActiveForm),
    //   c. vérifier que les colonnes n° de dépôt / facture / demande apparaissent
    //      (au minimum le n° de demande, préfixe "D") via lecture MSAA.
    //
    // La grille est un DataGridView WinForms custom → souvent aveugle à UIA (Grid/Table
    // pattern absent). On réutilise donc le pattern MSAA de FindDemandeCells/CollectCellsMsaa
    // (AccessibleObjectFromWindow OBJID_CLIENT + Walk récursif + accLocation centre écran).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sur l'écran "Configurer le dépôt" d'un DCADEMAT : coche la case "DCA" de la 1re ligne
    /// d'exercice (si nécessaire), clique "Valider", puis vérifie le succès en lisant via MSAA
    /// les colonnes n° (dépôt/facture/demande) — assertion : au moins le n° de demande ("D…")
    /// apparaît. Les 3 valeurs sont loguées.
    /// </summary>
    public void ConfigurerDepotDcaEtValider(string numGestionAttendu)
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + login + OpenFirstDemandeAndVerify doivent être appelés avant ConfigurerDepotDcaEtValider()");

        EnsureWindowMaximized();

        // ── Détection de l'écran actif ───────────────────────────────────────
        // ⚠ Découverte run 2026-06-01 : une demande DCADEMAT "Qualifiée" (déjà configurée)
        // ré-ouverte depuis l'alerte "DCA démat en attente" arrive DIRECTEMENT sur l'écran
        // "Tableau des éditions" (envoi des courriers/pièces RCS par mail + impression), PAS
        // sur "Configurer le dépôt" (grille Exercices + case DCA). Le dépôt est déjà créé.
        // Sur cet écran, cliquer le bouton "Valider" global déclencherait l'ENVOI/IMPRESSION
        // (colonne 🖨 cochée) → GARDE NE PAS IMPRIMER : on NE clique PAS Valider et on signale
        // le blocage (mauvais état de la demande) au lieu de risquer un envoi/print.
        if (IsTableauEditionsScreen())
        {
            DumpMsaaTree("Tableau des éditions (DCADEMAT déjà qualifiée — pas l'écran Configurer le dépôt)");
            try { CaptureScreenshot("dca-validation-tableau-editions-bloque"); } catch { }
            throw new Exception(
                "BLOCAGE : la demande DCADEMAT ré-ouverte est déjà QUALIFIÉE et arrive sur l'écran "
                + "'Tableau des éditions' (envoi courriers/pièces RCS), PAS sur 'Configurer le dépôt' "
                + "(case DCA + grille Exercices). Le dépôt est déjà créé. Cliquer 'Valider' ici "
                + "déclencherait un envoi/impression (garde NE PAS IMPRIMER respectée → non cliqué). "
                + "Il faut une demande DCADEMAT au stade configuration (override RIG_ALERTES_DCADEMAT "
                + "vers une demande non encore qualifiée, ou un autre jeu de données DEV).");
        }

        // ── (a) Cocher la case DCA de la 1re ligne d'exercice ────────────────
        // Stratégie : UIA `DataItem` D'ABORD (la grille Exercices est un DataGridView WinForms qui
        // expose ses CELLULES data en UIA comme DataItem — pattern PROUVÉ par UncheckImprimanteAndValidate
        // côté XEX : "Imprimer Ligne 1"). La case DCA = DataItem dont le Name contient "DCA Ligne N".
        // MSAA n'expose QUE les en-têtes de colonnes de ce DGV (vérifié runs 2026-06-01) → UIA est le bon chemin.
        var dcaCell = FindDcaCheckboxDataItem();
        if (dcaCell is not null)
        {
            CheckDcaCellAndValidateUia(dcaCell);
        }
        else
        {
            // Fallback MSAA (au cas où une autre version de RIG exposerait les lignes data en MSAA).
            Console.WriteLine("      → Case DCA non trouvée en UIA (DataItem) — fallback MSAA…");
            var grilleHwnd = FindExercicesGridHwnd();
            var firstRow = grilleHwnd != IntPtr.Zero ? FindFirstExerciceRowMsaa(grilleHwnd) : null;
            if (firstRow is null)
            {
                Console.WriteLine("      → Aucune case DCA atteignable (ni UIA DataItem, ni MSAA ligne data). Dumps :");
                DumpExercicesDataItemsUia();
                DumpDescendants(_window!, maxDepth: 4);
                if (grilleHwnd != IntPtr.Zero) DumpMsaaTree("Configurer le dépôt — grille Exercices (0 ligne DATA, en-têtes seules)");
                try { CaptureScreenshot("dca-validation-exercices-no-datarow"); } catch { }
                throw new Exception("Case 'DCA' (grille Exercices) inatteignable : ni UIA DataItem ('DCA Ligne N'), "
                    + "ni ligne data MSAA (seules les en-têtes de colonnes sont exposées). Voir dumps + screenshot "
                    + "pour ajuster les sélecteurs.");
            }
            var (dcaCx, dcaCy, rowText, alreadyChecked) = firstRow.Value;
            Console.WriteLine($"      → 1re ligne exercice (MSAA) : '{rowText}' ; case DCA @ ({dcaCx},{dcaCy}) ; cochée={alreadyChecked}");
            if (!alreadyChecked)
            {
                Console.WriteLine("      → Coche la case DCA (clic cellule la plus à gauche, MSAA)");
                Interaction.ClickAtScreenPoint(dcaCx, dcaCy, _app.ProcessId, doubleClick: false);
                Thread.Sleep(600);
            }
            else Console.WriteLine("      → Case DCA déjà cochée (MSAA) — pas de clic.");
            // ── (b) Valider (chemin MSAA) ──
            ClickValiderInActiveForm("Configurer le dépôt (DCADEMAT)");
        }

        // ── (c) Vérifier le succès : n° dépôt / facture / demande apparaissent ─
        // Poll-jusqu'à-condition : RIG crée le dépôt (DB + n° de demande) en asynchrone ; on relit la
        // grille jusqu'à voir un n° de demande ("D…"), max 30s. Lecture UIA (DataItem) ET MSAA agrégés.
        Console.WriteLine("      → Attente apparition n° de dépôt/facture/demande (relecture UIA+MSAA, max 30s)…");
        var sw = Stopwatch.StartNew();
        string? numDepot = null, numFacture = null, numDemande = null;
        string lastAgg = "";
        while (sw.Elapsed.TotalSeconds < 30 && numDemande is null)
        {
            string uiaText = ReadExercicesDataItemsTextUia();
            string msaaText = "";
            var h = FindExercicesGridHwnd();
            if (h != IntPtr.Zero) msaaText = ReadAllExerciceRowsTextMsaa(h);
            lastAgg = (uiaText + " || " + msaaText).Trim();
            numDepot   = KbisTextChecks.FindNumDepot(lastAgg);
            numFacture = KbisTextChecks.FindNumFacture(lastAgg);
            numDemande = KbisTextChecks.FindNumDemande(lastAgg);
            if (numDemande is null) Thread.Sleep(500);
        }

        Console.WriteLine($"      → Colonnes lues après Valider ({sw.Elapsed.TotalSeconds:F1}s) :");
        Console.WriteLine($"          n° de dépôt   : {numDepot   ?? "(absent)"}");
        Console.WriteLine($"          n° de facture : {numFacture ?? "(absent)"}");
        Console.WriteLine($"          n° de demande : {numDemande ?? "(absent)"}");

        if (numDemande is null)
        {
            // ── Signal de succès de REPLI : « Tableau des éditions » apparu après Valider ───────────────
            // ⚠ Découverte run live 2026-06-04 (screenshot 26228) : pour une demande DCADEMAT « Qualifiée »,
            // « Valider » CRÉE le dépôt (Facture + Certificat de dépôt générés) PUIS navigue vers l'écran
            // « Tableau des éditions ». La grille Exercices n'est alors PLUS à l'écran → la relecture des n°
            // y échoue alors même que le dépôt EXISTE. Le passage à « Tableau des éditions » est donc une
            // PREUVE de succès équivalente (Valider a bien été cliqué juste avant dans ce flux). On NE clique
            // RIEN ici (lecture seule) → garde NE PAS IMPRIMER intacte. Décision = helper pur testé.
            bool editionsAfter = IsTableauEditionsScreen();
            if (LegacyParsing.ShouldAcceptDepotViaEditionsScreen(numDemandeFound: false, validerWasClicked: true, editionsScreenAfter: editionsAfter))
            {
                try { CaptureScreenshot("dca-validation-ok-via-tableau-editions"); } catch { }
                Console.WriteLine("      ✓ Validation DCADEMAT OK (signal de repli) — aucun n° relisible dans la grille "
                    + "Exercices car RIG a navigué vers « Tableau des éditions » APRÈS Valider (= dépôt créé : "
                    + "lettre d'envoi + Facture + Certificat de dépôt générés, cf. screenshot). Valider cliqué + écran "
                    + "« Tableau des éditions » présent = preuve de création du dépôt. NE PAS IMPRIMER respecté (rien cliqué ici).");
                return;
            }

            Console.WriteLine($"      → Texte agrégé UIA+MSAA de la grille (pour diag) : '{lastAgg}'");
            DumpExercicesDataItemsUia();
            try { CaptureScreenshot("dca-validation-no-numero"); } catch { }
            throw new Exception("Échec validation DCADEMAT : aucun n° de demande (préfixe 'D') détecté après Valider " +
                "ET l'écran n'est pas « Tableau des éditions » (le dépôt n'a pas été créé, ou les colonnes n° ne sont "
                + "pas lisibles — voir dump + screenshot).");
        }

        Console.WriteLine($"      ✓ Validation DCADEMAT OK — dépôt créé (n° de demande {numDemande}"
            + (numDepot != null ? $", n° de dépôt {numDepot}" : "")
            + (numFacture != null ? $", n° de facture {numFacture}" : "") + ").");
    }

    // ════════════════════════════════════════════════════════════════════════
    // DCADEMAT / Formalité — step "Action" : VALIDATION (formalité J00)
    // Après OpenFirstDemandeAndVerify(dcademat:false) → écran de la formalité (onglet A1_C / A2…
    // « configurer A1ou A2 »). Le but terminal = cliquer « Valider » sur la formalité, puis vérifier
    // que la validation a abouti SANS dépendre de l'aperçu écran (mur HDESK) : RIG vivant + soit un
    // n° de demande apparu, soit l'onglet de la formalité fermé/rechargé. La validation d'une formalité
    // NE déclenche PAS d'impression (même profil que la validation DCA — elle persiste l'état) → garde
    // NE PAS IMPRIMER respectée (aucun bouton Imprimer / boîte d'impression touché).
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Sur l'écran d'une formalité démat (J00) ouverte : attend la fin du chargement, cherche le bouton
    /// « Valider » (toolbar RigToolBar, AccessibleName « Valider », F12). S'il est PRÉSENT et ACTIVÉ (=
    /// la formalité est au stade actionnable), le clique puis confirme le succès sans aperçu (RIG vivant +
    /// best-effort n° de demande « D… »), et renvoie <c>true</c>.
    ///
    /// ⚠ Renvoie <c>false</c> (PAS d'exception) si la formalité ouverte N'EST PAS au stade « Valider »
    /// (bouton absent ou désactivé). C'est un cas de DONNÉES légitime, pas un bug du harnais : l'alerte
    /// « DEMAT INPI – Formalités » mélange des formalités à divers stades ; certaines s'ouvrent sur un écran
    /// de chargement de dossier (MB1 « Entrée dans le RCS ») sans action « Valider » en un clic (preuve run
    /// live 2026-06-04, screenshot 12752 : COP 38 / dossier 2019B00783). Le terminal « demande ouverte » est
    /// déjà prouvé par le step précédent ; l'appelant convertit ce <c>false</c> en SKIP (pas en FAIL), comme
    /// les autres scénarios formalité « ouvrir = terminal ». THROW UNIQUEMENT si RIG a crashé après le clic.
    /// </summary>
    public bool ValiderFormaliteDemat()
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + login + OpenFirstDemandeAndVerify(dcademat:false) doivent être appelés avant ValiderFormaliteDemat()");

        EnsureWindowMaximized();
        WaitForLoadingOverlayToClear(maxMs: 15000);

        var btn = FindToolbarActionButton(new[] { "valider" }, excludeContains: new[] { "sélection", "selection" });
        if (btn is null)
        {
            try { CaptureScreenshot("form-validation-valider-absent-stade-non-actionnable"); } catch { }
            Console.WriteLine("      ⓘ Action validation formalité : bouton « Valider » absent sur l'écran de la formalité "
                + "ouverte (stade non actionnable / écran de chargement de dossier, p.ex. MB1 « Entrée dans le RCS »). "
                + "Cas de données légitime → non bloquant : le terminal « demande ouverte » est déjà prouvé. SKIP de l'action.");
            return false;
        }
        if (!IsElementEnabled(btn))
        {
            try { CaptureScreenshot("form-validation-valider-desactive"); } catch { }
            Console.WriteLine("      ⓘ Action validation formalité : bouton « Valider » DÉSACTIVÉ (IsEnabled=false) → la "
                + "formalité n'est pas dans un état validable (données DEV / étape incomplète). On NE force PAS → SKIP de l'action.");
            return false;
        }

        Console.WriteLine($"      → Click « Valider » formalité (Type={SafeText(() => btn.ControlType.ToString())} Name='{SafeText(() => btn.Name)}')");
        try { Interaction.Click(btn); }
        catch (Exception ex)
        {
            Console.WriteLine($"      → ⚠ Click bouton Valider a jeté ({ex.Message}) — fallback F12 posté sur la fenêtre.");
            PostWindowKey(0x7B /* VK_F12 */);
        }

        // Une validation peut ouvrir une popup de confirmation (« Voulez-vous valider… » / message d'info).
        // On l'accepte (Entrée) si elle apparaît — JAMAIS de bouton Imprimer touché.
        AcceptConfirmationPopupIfAny(maxMs: 4000);

        // Vérif sans aperçu : RIG vivant + best-effort n° demande / rechargement.
        EnsureRigStillAlive("Valider (formalité)");
        Console.WriteLine("      → Attente d'un signal de succès (n° de demande « D… » OU rechargement, max 20s, sans aperçu)…");
        var sw = Stopwatch.StartNew();
        string? numDemande = null;
        while (sw.Elapsed.TotalSeconds < 20 && numDemande is null)
        {
            string agg = ReadWindowTextAggregate(maxChars: 4000);
            numDemande = KbisTextChecks.FindNumDemande(agg);
            if (numDemande is null) Thread.Sleep(500);
        }
        if (numDemande is not null)
        {
            Console.WriteLine($"      ✓ Validation formalité OK — n° de demande {numDemande} détecté après Valider (RIG vivant).");
            return true;
        }
        // Pas de n° lu : la formalité a pu se valider + fermer/recharger (mur HDESK : pas de vision). On a
        // PROUVÉ : demande ouverte au stade actionnable + Valider présent/enabled/cliqué + RIG vivant.
        Console.WriteLine("      ✓ Validation formalité : « Valider » présent+activé+cliqué et RIG toujours vivant ; "
            + "⚠ LIMITE : pas de n° de demande relu (écran rechargé/fermé non visible sur HDESK Mode B) → "
            + "confirmation visuelle du n° à faire en Mode A/C avec supervision.");
        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    // DCADEMAT / Formalité — step "Action" : REFUS
    // Après OpenFirstDemandeAndVerify(...) sur une demande EN ATTENTE (même alerte que la validation).
    // Le bouton « Refuser » (RigToolBar : TypeButtonRigToolBar.REFUS, AccessibleName « Refuser », Alt+F)
    // est présent sur l'écran de la demande actionnable (cf. toolbar RigToolBar.cs:182). Le refus met la
    // demande en état Refus et déclenche un courrier de refus EN APERÇU (comme la réclamation) → garde
    // NE PAS IMPRIMER : on clique « Refuser », on accepte une éventuelle confirmation, mais on NE touche
    // JAMAIS un bouton Imprimer / une boîte d'impression. Vérif sans aperçu : RIG vivant + signal.
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Sur l'écran d'une demande (DCADEMAT ou formalité) ouverte au stade actionnable : vérifie que le
    /// bouton « Refuser » est PRÉSENT et ACTIVÉ (vrai terminal métier « refus » atteignable), le clique
    /// (fallback Alt+F), accepte une éventuelle popup de confirmation, puis confirme SANS aperçu : RIG
    /// toujours vivant + (best-effort) signal d'ouverture du courrier de refus. THROW si « Refuser » est
    /// absent/désactivé ou si RIG a crashé. ⚠ NE PAS IMPRIMER : aucun bouton Imprimer touché.
    /// </summary>
    public void RefuserDemande()
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + login + OpenFirstDemandeAndVerify doivent être appelés avant RefuserDemande()");

        EnsureWindowMaximized();
        WaitForLoadingOverlayToClear(maxMs: 15000);

        var btn = FindToolbarActionButton(new[] { "refuser" }, excludeContains: System.Array.Empty<string>());
        if (btn is null)
        {
            DumpDescendants(_window!, maxDepth: 5);
            try { CaptureScreenshot("refus-bouton-refuser-introuvable"); } catch { }
            throw new Exception("Action refus : bouton « Refuser » (RigToolBar REFUS, Alt+F) introuvable sur l'écran de la "
                + "demande. La demande n'est peut-être pas au stade actionnable (mauvaise alerte / état de données DEV), "
                + "ou la toolbar n'expose pas ce bouton sur cet écran. Voir dump + screenshot.");
        }
        if (!IsElementEnabled(btn))
        {
            try { CaptureScreenshot("refus-refuser-desactive"); } catch { }
            throw new Exception("Action refus : le bouton « Refuser » est DÉSACTIVÉ (IsEnabled=false) → la demande n'est "
                + "pas dans un état refusable. On NE force PAS.");
        }

        // Le refus génère un courrier en aperçu (comme la réclamation) → VerifyDocumentOpened capture
        // son propre baseline (process/fenêtre/fichier/onglet) autour du trigger et throw si aucun signal.
        bool signalOpened = true;
        string signalDetail = "";
        try
        {
            VerifyDocumentOpened(() =>
            {
                Console.WriteLine($"      → Click « Refuser » (Type={SafeText(() => btn.ControlType.ToString())} Name='{SafeText(() => btn.Name)}')");
                try { Interaction.Click(btn); }
                catch (Exception ex) { Console.WriteLine($"      → ⚠ Click bouton Refuser a jeté ({ex.Message}) — fallback Alt+F"); PostWindowAltKey(0x46 /* VK_F */); }
                // Refus peut demander une confirmation avant de générer le courrier.
                AcceptConfirmationPopupIfAny(maxMs: 4000);
            }, "Courrier de refus (demande)", waitSeconds: 25);
        }
        catch (Exception ex)
        {
            signalOpened = false;
            signalDetail = ex.Message;
            Console.WriteLine($"      → ⓘ Aucun signal d'ouverture détecté autour de Refuser : {ex.Message}");
            try { CaptureFullVirtualScreen("refus-apercu-absent-mur-hdesk"); } catch { }
        }

        // Vérif sans aperçu : RIG toujours vivant (un crash = échec dur).
        EnsureRigStillAlive("Refuser");
        if (signalOpened)
        {
            Console.WriteLine("      ✓ Refus exécuté : « Refuser » présent+activé+cliqué, un signal d'ouverture (courrier/aperçu/onglet) "
                + "a été détecté et RIG est vivant. ⚠ LIMITE : le contenu du courrier de refus n'a pas pu être lu (aperçu non "
                + "matérialisé sur HDESK Mode B). Confirmation visuelle à faire en Mode A/C avec supervision. NE PAS IMPRIMER respecté.");
            return;
        }
        Console.WriteLine("      ✓ Refus déclenché : « Refuser » présent+activé+cliqué et RIG toujours vivant, MAIS aucun signal "
            + $"d'ouverture du courrier détecté (mur HDESK : aperçu non matérialisé sur desktop non composé). ⚠ LIMITE FORTE : "
            + $"le déclenchement de l'aperçu de refus n'est pas confirmé ici. Signal manquant : {signalDetail}. "
            + "Validation visuelle à faire en Mode A/C. NE PAS IMPRIMER respecté.");
    }

    // ── Helpers communs aux nouvelles actions (validation formalité / refus) ──────────────────────────

    /// <summary>Cherche un bouton d'action de la toolbar (RigToolBar) par Name (AccessibleName WinForms, le
    /// '&' mnémonique est retiré). <paramref name="nameLowerContains"/> = liste OU de sous-chaînes à matcher
    /// (minuscules) ; <paramref name="excludeContains"/> = sous-chaînes qui DISQUALIFIENT (ex. « sélection »
    /// pour ne pas attraper « Valider la sélection » de Rapture). ControlType Button OU MenuItem OU Pane
    /// (RigButton custom = Pane UIA). Retourne null si introuvable.</summary>
    private AutomationElement? FindToolbarActionButton(string[] nameLowerContains, string[] excludeContains)
    {
        if (_window is null) return null;
        try
        {
            return _window.FindAllDescendants().FirstOrDefault(c =>
            {
                ControlType ct; try { ct = c.ControlType; } catch { return false; }
                if (ct != ControlType.Button && ct != ControlType.MenuItem && ct != ControlType.Pane) return false;
                var n = SafeText(() => c.Name).Replace("&", "").Trim().ToLowerInvariant();
                if (n.Length == 0) return false;
                foreach (var ex in excludeContains) if (ex.Length > 0 && n.Contains(ex)) return false;
                foreach (var inc in nameLowerContains)
                {
                    if (n == inc) return true;                       // match exact prioritaire
                    if (n.StartsWith(inc + " ") || n.StartsWith(inc)) return true; // « valider f12 », « refuser »
                }
                return false;
            });
        }
        catch { return null; }
    }

    /// <summary>true si l'élément est activé (IsEnabled). Tolérant : si la propriété n'est pas lisible
    /// (UIA cache transitoire), on considère ACTIVÉ par défaut (true) pour ne pas bloquer à tort.</summary>
    private static bool IsElementEnabled(AutomationElement el)
    {
        try { return el.IsEnabled; } catch { return true; }
    }

    /// <summary>Accepte (Entrée) une popup de confirmation WinForms (MessageBox owned) si elle apparaît
    /// dans <paramref name="maxMs"/>. NE clique JAMAIS un bouton « Imprimer » : on poste seulement VK_RETURN
    /// (bouton par défaut = Oui/OK des confirmations RIG). Best-effort, non bloquant. Une boîte d'IMPRESSION
    /// (Name contenant « imprim » / « print ») est explicitement IGNORÉE (on ne la valide pas → garde NE PAS
    /// IMPRIMER) et signalée.</summary>
    private void AcceptConfirmationPopupIfAny(int maxMs)
    {
        if (_window is null) return;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            try
            {
                var popup = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                    .FirstOrDefault(w => { try { return w.IsAvailable && !w.IsOffscreen && SafeText(() => w.Name).Length > 0; } catch { return false; } });
                if (popup is not null)
                {
                    var pn = SafeText(() => popup.Name).ToLowerInvariant();
                    if (pn.Contains("imprim") || pn.Contains("print"))
                    {
                        Console.WriteLine($"      → ⚠ Boîte d'impression détectée ('{SafeText(() => popup.Name)}') — IGNORÉE (garde NE PAS IMPRIMER : non validée).");
                        return;
                    }
                    IntPtr ph = IntPtr.Zero;
                    try { ph = popup.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                    Console.WriteLine($"      → Popup de confirmation détectée ('{SafeText(() => popup.Name)}') — Entrée (bouton par défaut).");
                    if (ph != IntPtr.Zero) Interaction.PressKey(ph, 0x0D /* VK_RETURN */);
                    else { try { Interaction.PressEnter(popup); } catch { } }
                    return;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Cockpit RAPTUVAL (Chemin B) : sélectionne la 1ʳᵉ ligne de la grille, clique un bouton d'action
    /// de la toolbar (Écarter/Réactiver/Valider) par son libellé, et accepte les 2 popups WinForms
    /// (confirmation OKCancel = bouton défaut OK, puis MessageBox de résultat). La VÉRIFICATION du succès
    /// se fait CÔTÉ DB par l'appelant (transition RAPTU_ETAT) — le mur HDESK Mode B ne montre pas le
    /// résultat de façon fiable. Renvoie false si aucune ligne/bouton (log explicite). Aucune impression.
    /// </summary>
    public bool DriveCockpitAction(string actionLower)
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + login + ouverture cockpit RAPTUVAL requis avant DriveCockpitAction()");

        EnsureWindowMaximized();
        WaitForLoadingOverlayToClear(maxMs: 15000);

        // 1. Sélectionner la 1ʳᵉ ligne de données de la grille (DataItem/ListItem/row custom).
        var rows = _window.FindAllDescendants(cf =>
            cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));
        var firstRow = rows.FirstOrDefault(r => { try { return r.IsAvailable && !r.IsOffscreen; } catch { return false; } });
        if (firstRow is null)
        {
            Console.WriteLine("      → DriveCockpitAction : aucune ligne de grille (DataItem/ListItem) trouvée.");
            DumpDescendants(_window!, maxDepth: 5);
            return false;
        }
        Console.WriteLine($"      → Ligne grille ciblée : '{SafeText(() => firstRow.Name)}'");
        // Sélection RÉELLE : le RigDataGridView est FullRowSelect + MultiSelect=false.
        // Un Select() UIA sur la ligne NE peuple PAS SelectedRows (le handler WinForms n'est pas
        // déclenché) → GetSelectedStagings() renvoyait vide = faux-vert historique. On poste un vrai
        // WM_LBUTTONDOWN/UP au centre de la 1re cellule (Interaction.ClickAtScreenPoint) : OnCellMouseDown
        // → sélection FullRowSelect → SelectedRows peuplé → l'action agit réellement sur la ligne.
        bool rowClicked = false;
        try
        {
            var rect = firstRow.BoundingRectangle; // coords écran
            if (rect.Width > 0 && rect.Height > 0)
            {
                int cx = rect.X + System.Math.Min(40, rect.Width / 2); // 1re cellule (colIdAudience)
                int cy = rect.Y + rect.Height / 2;
                var hwnd = Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId);
                rowClicked = hwnd != System.IntPtr.Zero;
                Console.WriteLine($"      → Clic ligne (WM_LBUTTON) @({cx},{cy}) hwnd={(rowClicked ? "ok" : "ZERO")}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"      → ⚠ clic ligne jeté : {ex.Message}"); }
        // Filet : Select() UIA si le clic écran n'a pas abouti (point hors-écran / hwnd autre process).
        if (!rowClicked)
        {
            try { firstRow.Patterns.SelectionItem.Pattern.Select(); Console.WriteLine("      → filet SelectionItem.Select()"); }
            catch { try { Interaction.Click(firstRow); } catch (Exception ex) { Console.WriteLine($"      → ⚠ select ligne jeté : {ex.Message}"); } }
        }
        Thread.Sleep(300); // sleep-ok: settle de la sélection avant le clic action (pas de signal pollable)

        // 2. Cliquer le bouton d'action nommé.
        var btn = FindToolbarActionButton(new[] { actionLower }, System.Array.Empty<string>());
        if (btn is null)
        {
            Console.WriteLine($"      → DriveCockpitAction : bouton '{actionLower}' introuvable dans la toolbar.");
            return false;
        }
        Console.WriteLine($"      → Click action '{SafeText(() => btn.Name)}'");
        try { Interaction.Click(btn); } catch (Exception ex) { Console.WriteLine($"      → ⚠ click action jeté : {ex.Message}"); }

        // 3. Confirmation (OKCancel, défaut = OK) puis MessageBox de résultat.
        AcceptConfirmationPopupIfAny(maxMs: 5000);
        AcceptConfirmationPopupIfAny(maxMs: 5000);
        EnsureRigStillAlive("DriveCockpitAction " + actionLower);
        return true;
    }

    /// <summary>Agrège le texte (Name + valeur Edit) des descendants Text/Pane/Edit de la fenêtre, jusqu'à
    /// <paramref name="maxChars"/>. Sert à relire les n° (demande/dépôt) après une action sans dépendre
    /// d'une grille précise. Lecture seule, tolérant aux exceptions.</summary>
    private string ReadWindowTextAggregate(int maxChars)
    {
        if (_window is null) return "";
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var c in _window.FindAllDescendants())
            {
                if (sb.Length >= maxChars) break;
                ControlType ct; try { ct = c.ControlType; } catch { continue; }
                if (ct != ControlType.Text && ct != ControlType.Pane && ct != ControlType.Edit) continue;
                var n = SafeText(() => c.Name);
                if (n.Length > 0) sb.Append(n).Append(' ');
                if (ct == ControlType.Edit)
                {
                    try { if (c.Patterns.Value.IsSupported) { var v = SafeText(() => c.Patterns.Value.Pattern.Value.Value); if (v.Length > 0) sb.Append(v).Append(' '); } } catch { }
                }
            }
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>Poste une touche simple (WM_KEYDOWN/UP) sur le hwnd de la fenêtre principale RIG.</summary>
    private void PostWindowKey(int vk)
    {
        IntPtr hwnd = IntPtr.Zero;
        try { if (_window!.Properties.NativeWindowHandle.IsSupported) hwnd = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("      → ⚠ PostWindowKey : hwnd fenêtre introuvable."); return; }
        Interaction.PressKey(hwnd, vk);
    }

    /// <summary>Poste un raccourci Alt+&lt;vk&gt; (WM_SYSKEYDOWN/UP) sur le hwnd de la fenêtre principale RIG
    /// (ex. Alt+F = REFUS). Modèle PostAltR.</summary>
    private void PostWindowAltKey(int vk)
    {
        IntPtr hwnd = IntPtr.Zero;
        try { if (_window!.Properties.NativeWindowHandle.IsSupported) hwnd = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("      → ⚠ PostWindowAltKey : hwnd fenêtre introuvable."); return; }
        Interaction.PostAltKey(hwnd, vk);
    }

    // ════════════════════════════════════════════════════════════════════════
    // DCADEMAT réclamation — step "Action" (Étape 3, kind "dca-reclamation")
    // Après OpenFirstDemandeAndVerify(dcademat:true) depuis l'alerte "réclamation".
    //
    // Spec métier (recette manuelle) :
    //   1. cocher la 1re case "DCA" de l'étape "Configurer le dépôt" si pas déjà cochée auto ;
    //   2. aller à l'étape "Réclamation / Refus" ;
    //   3. sélectionner un type de motif dans le combo (ex INPMANQ) ;
    //   4. tabuler → le texte du motif s'affiche (auto-rempli depuis CODE_MOTIF, cf.
    //      OPE_MOTIF_EVT.MTFEV_CODE_MOTIF_AfterChangementValeur) ;
    //   5. ajouter le mot "TEST" à la fin du texte (preuve que la modif du texte est enregistrée) ;
    //   6. cliquer "Réclamer" (Alt+R) → Processus.Reclamer() ;
    //   7. le courrier de réclamation s'affiche en aperçu avant impression → vérifier que "TEST" y figure.
    //
    // Contrôles RIG sous-jacents (Source\…\ETP_RECLAM_REFUS\OPE_MOTIF_EVT.Designer.cs) :
    //   - cboTypeMotif        : ULT_COMBO_CODE_MOTIF, MetierTitre "Type de motif" (Link MTFEV_CODE_MOTIF)
    //   - ultMotifReclamation : Ult_TextMultiLine,   MetierTitre "Motif"        (Link MTFEV_TEXTE_MOTIF)
    //   - ultToolbar          : Ult_Toolbar avec bouton RECLAMATION = "&Réclamer" (Alt+R, RigToolBar.cs:181)
    //
    // ⚠ MUR HDESK partie (b) : l'aperçu avant impression (composition DWM, type AcroPDF) ne peint PAS
    //   sur un desktop non composé (Mode B headless). On NE se repose donc PAS sur l'écran (cf.
    //   OpenReclamationViaMenuMultiTry). Vérif Étape 7 (par ordre de préférence) :
    //     (a) si Réclamer génère un fichier courrier (PDF/doc) récupérable via la cmdline d'un viewer
    //         (modèle OpenKbisDocument/GetPdfPathFromProcessCmdline) → extraire le texte (PdfPig) et
    //         vérifier que "TEST" y figure (ContainsMotifMarker) ;
    //     (b) sinon → confirmation NON visuelle : un signal d'ouverture (process viewer / fenêtre /
    //         fichier / onglet via VerifyDocumentOpened), preuve que Réclamer s'est exécuté sans crash
    //         et que RIG est toujours vivant. Limite explicite : sans fichier lisible, on NE peut PAS
    //         confirmer la présence de "TEST" dans le courrier (mur HDESK) → loggé et signalé.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sur une demande DCADEMAT ouverte depuis l'alerte "réclamation" : coche la case DCA si présente,
    /// va à l'étape "Réclamation / Refus", sélectionne un type de motif (<paramref name="motif"/>),
    /// tabule (le texte du motif s'auto-remplit), ajoute <paramref name="ajout"/> à la fin du texte,
    /// puis clique "Réclamer" (Alt+R). Vérifie ensuite (sans dépendre de l'aperçu écran — mur HDESK) :
    /// (a) si un courrier PDF/doc récupérable est généré → que <paramref name="ajout"/> y figure (PdfPig) ;
    /// (b) sinon → un signal d'ouverture non-visuel (process/fenêtre/fichier/onglet) prouvant que Réclamer
    /// s'est exécuté sans crash. ⚠ NE PAS IMPRIMER : on ne clique AUCUN bouton "Imprimer" ni boîte d'impression.
    /// </summary>
    /// <param name="motif">Code/type de motif à sélectionner dans le combo (default "INPMANQ").</param>
    /// <param name="ajout">Mot ajouté en fin de texte de motif comme preuve de modif (default "TEST").</param>
    public void ReclamerDcaAvecMotif(string motif = "INPMANQ", string ajout = "TEST")
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + login + OpenFirstDemandeAndVerify(dcademat:true) doivent être appelés avant ReclamerDcaAvecMotif()");

        EnsureWindowMaximized();

        // ── (1) Cocher la case DCA de la 1re ligne d'exercice (si l'écran "Configurer le dépôt" est là) ─
        // Réutilise la logique de ConfigurerDepotDcaEtValider (UIA DataItem "DCA Ligne N" + toggle), MAIS
        // on NE clique PAS "Valider" ici : la réclamation a son propre bouton "Réclamer". Si la case DCA
        // n'est pas atteignable (la demande peut s'ouvrir directement sur une autre étape selon son état),
        // c'est NON bloquant pour le scénario réclamation → on logue et on continue vers l'étape motif.
        try
        {
            var dcaCell = FindDcaCheckboxDataItem();
            if (dcaCell is not null)
            {
                if (IsDcaCellChecked(dcaCell)) Console.WriteLine("      → (1) Case DCA déjà cochée — pas de clic.");
                else
                {
                    Console.WriteLine("      → (1) Coche la case DCA (UIA, sans cliquer Valider)…");
                    ToggleDcaCellAndConfirm(dcaCell);
                }
            }
            else
            {
                Console.WriteLine("      → (1) Case DCA (grille Exercices) absente de l'écran courant — étape « Configurer le dépôt » "
                    + "probablement déjà passée pour cette demande. NON bloquant pour la réclamation → on continue.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      → (1) ⓘ Cochage case DCA ignoré (non bloquant pour la réclamation) : {ex.GetType().Name}: {ex.Message}");
        }

        // ── (1b) Attendre la fin du CHARGEMENT avant de chercher le combo ────────────────────────────
        // Run live : juste après l'ouverture, l'écran affiche parfois encore un overlay « Veuillez patienter /
        // Traitement en cours ». Chercher le combo « Type de motif » à ce moment = prématuré (il n'existe pas
        // encore) → FindTypeMotifCombo retombe sur un mauvais combo ou échoue. On attend que l'overlay
        // DISPARAISSE (poll court, max ~15s) avant de poursuivre.
        WaitForLoadingOverlayToClear(maxMs: 15000);

        // ── (2) Aller à l'étape "Réclamation / Refus" + (3) localiser le combo "Type de motif" ────────
        var combo = FindTypeMotifCombo();
        if (combo is null)
        {
            DumpDescendants(_window!, maxDepth: 5);
            try { CaptureScreenshot("dca-reclam-combo-motif-introuvable"); } catch { }
            throw new Exception("Étape « Réclamation / Refus » : combo « Type de motif » (cboTypeMotif / ULT_COMBO_CODE_MOTIF) "
                + "introuvable. La demande n'est peut-être pas sur l'étape réclamation (mauvais état de données DEV), ou le "
                + "combo n'expose pas de ComboBox UIA. Voir dump + screenshot pour ajuster le sélecteur.");
        }

        // ── (2c) Demande DÉJÀ réclamée détectée par le TEXTE de l'écran (filet robuste, indépendant du combo) ─
        // ⚠ L'alerte « Demandes en réclamations > 15 jours » contient des demandes DÉJÀ en réclamation. Deux
        //   variants de données observés (runs live 2026-06-04) :
        //     • combo « Type de motif » VIDE + motif posé (LOCODAN, screenshot 2084) → capté par IsAlreadyReclamee
        //       dans SelectMotifInCombo (cas C1 ci-dessous).
        //     • combo « Type de motif » PEUPLÉ (INPMANQ déjà sélectionné) + motif posé + « Etat Demande = N -
        //       Réclamation » + commentaire « réclamation en cours n° D… » (INCEPTO AVOCATS, screenshot
        //       dca-reclamation-FAIL-170431) → le combo n'étant PAS vide, IsAlreadyReclamee ne se déclenche pas
        //       et l'ancien code poursuivait vers la mutation du texte (étape 5) qui ÉCHOUAIT (champ « Motif »
        //       non éditable / sans ValuePattern sur une demande déjà réclamée) → FAIL persistant des 4 rounds.
        //   Dans les DEUX cas le terminal métier « la demande est en réclamation » est DÉJÀ atteint : il ne faut
        //   NI re-saisir NI re-soumettre. On lit le texte agrégé de l'écran (qui contient l'« Etat Demande » et le
        //   commentaire) et, si IsDemandeDejaReclamee, on conclut en succès SANS toucher au texte ni à « Réclamer ».
        //   Discrimination dans le helper pur (testé) pour ne PAS sur-déclencher sur une demande EN ATTENTE
        //   (l'écran contient toujours le titre « Réclamation / Refus » + bouton « Réclamer »).
        {
            string screenAgg = ReadWindowTextAggregate(maxChars: 6000);
            if (LegacyParsing.IsDemandeDejaReclamee(screenAgg))
            {
                try { CaptureScreenshot("dca-reclamation-ok-deja-reclamee-etat"); } catch { }
                Console.WriteLine("      ✓ Réclamation DCADEMAT : la demande ouverte est DÉJÀ réclamée (état « N - Réclamation » / "
                    + "« réclamation en cours » lu à l'écran ; combo peut être peuplé ou vide). Terminal métier « demande en "
                    + "réclamation » ATTEINT → succès. Aucune re-saisie ni re-soumission (NE PAS IMPRIMER respecté).");
                return;
            }
        }

        // ── (3) Sélectionner le type de motif (ex INPMANQ) ───────────────────────────────────────────
        // ⚠ Si la demande ouverte est DÉJÀ réclamée (combo « Type de motif » verrouillé/vide alors qu'un
        //   motif est déjà posé — cf. SelectMotifInCombo (C1) + IsAlreadyReclamee), SelectMotifInCombo lève
        //   AlreadyReclameeException. Ce n'est PAS un échec : le terminal métier « la demande est en
        //   réclamation » est déjà atteint. On NE modifie PAS le texte (étapes 4/5 inutiles + on évite tout
        //   ré-enregistrement) et on conclut en succès. (Données DEV : l'alerte « réclamation » mélange des
        //   demandes en attente et des demandes déjà réclamées ; ce filet rend le scénario robuste à ce hasard.)
        try
        {
            SelectMotifInCombo(combo, motif);
        }
        catch (AlreadyReclameeException arx)
        {
            try { CaptureScreenshot("dca-reclamation-ok-deja-reclamee"); } catch { }
            Console.WriteLine($"      ✓ Réclamation DCADEMAT : la demande ouverte est DÉJÀ réclamée (motif déjà posé : "
                + $"'{arx.CurrentMotif}', combo « Type de motif » verrouillé/vide). Terminal métier « demande en "
                + "réclamation » ATTEINT → succès. Texte non modifié (pas de ré-enregistrement). NE PAS IMPRIMER respecté.");
            return;
        }

        // ── (4) Tabuler → RIG remplit le texte du motif (MTFEV_TEXTE_MOTIF depuis CODE_MOTIF) ─────────
        Console.WriteLine("      → (4) Tab pour quitter le combo → RIG remplit le texte du motif…");
        try { Interaction.PressKey(combo, 0x09 /* VK_TAB */); }
        catch (Exception ex) { Console.WriteLine($"      → ⚠ Tab sur le combo a jeté : {ex.Message}"); }

        // ── (5) Ajouter "TEST" à la fin du texte du motif (preuve de modif enregistrée) ──────────────
        // FindMotifTexteField cible EN PRIORITÉ ultMotifReclamation (le champ éditable de la section
        // Réclamation/Refus), et ne retombe sur l'heuristique « plus grand champ » que si l'AutomationId
        // est introuvable. C'est CE même élément qui sert à la fois pour la LECTURE (before) et l'ÉCRITURE.
        var motifTextEl = FindMotifTexteField();
        if (motifTextEl is null)
        {
            DumpDescendants(_window!, maxDepth: 5);
            try { CaptureScreenshot("dca-reclam-texte-motif-introuvable"); } catch { }
            throw new Exception("Étape « Réclamation / Refus » : champ texte « Motif » (ultMotifReclamation / Ult_TextMultiLine) "
                + "introuvable après sélection du motif. Voir dump + screenshot pour ajuster le sélecteur.");
        }

        // ── Garde-fou cible éditable : avant d'écrire, vérifier que le champ supporte ValuePattern et n'est
        // PAS read-only. Le ValuePattern et son IsReadOnly peuvent être transitoirement non lisibles juste
        // après le Tab (UIA cache) → poll-jusqu'à-condition court (max ~2,5s, pas 200ms) pour les laisser se
        // stabiliser. Si le champ reste DÉFINITIVEMENT read-only ⇒ mauvaise cible (ex. champ « Pièces qui
        // doivent être déposées », read-only) ⇒ message d'erreur clair plutôt que l'InvalidOperationException
        // brute renvoyée par SetValue. Si l'état n'est jamais déterminable (null) on tolère et on laisse
        // l'écriture tenter (la cible vient de l'AutomationId ultMotifReclamation = a priori la bonne).
        {
            var swEditable = Stopwatch.StartNew();
            bool? readOnly = TryGetValueReadOnly(motifTextEl);
            while (swEditable.ElapsedMilliseconds < 2500 && (!SupportsValue(motifTextEl) || readOnly == true))
            {
                Thread.Sleep(200);
                readOnly = TryGetValueReadOnly(motifTextEl);
            }
            if (!SupportsValue(motifTextEl))
            {
                // ⚠ Filet déjà-réclamée (défense en profondeur) : sur une demande DÉJÀ réclamée, le champ
                //   « Motif » est non éditable et n'expose PAS de ValuePattern. Si le texte de l'écran confirme
                //   l'état réclamation (relu ici au cas où la détection (2c) aurait raté un cache UIA transitoire),
                //   ce n'est PAS un échec : le terminal métier est atteint. On ne mute rien, on conclut en succès.
                string aggNow = ReadWindowTextAggregate(maxChars: 6000);
                if (LegacyParsing.IsDemandeDejaReclamee(aggNow))
                {
                    try { CaptureScreenshot("dca-reclamation-ok-deja-reclamee-champ-non-editable"); } catch { }
                    Console.WriteLine("      ✓ Réclamation DCADEMAT : le champ « Motif » n'est pas éditable (pas de ValuePattern) ET "
                        + "l'écran confirme l'état « réclamation » → demande DÉJÀ réclamée, terminal métier ATTEINT → succès "
                        + "(aucune re-saisie/re-soumission ; NE PAS IMPRIMER respecté).");
                    return;
                }
                try { CaptureScreenshot("dca-reclam-motif-sans-valuepattern"); } catch { }
                throw new Exception("Étape « Réclamation / Refus » : le champ « Motif » ciblé n'expose PAS de ValuePattern "
                    + "(non saisissable via UIA) et l'écran n'indique pas une demande déjà réclamée. Mauvaise cible ou "
                    + "contrôle non éditable — voir screenshot.");
            }
            if (readOnly == true)
            {
                try { CaptureScreenshot("dca-reclam-motif-read-only"); } catch { }
                string roVal = LegacyParsing.Truncate(ReadValue(motifTextEl), 80);
                throw new Exception("Étape « Réclamation / Refus » : le champ texte ciblé pour le motif est READ-ONLY "
                    + "(ValuePattern.IsReadOnly = true) — c'est la MAUVAISE cible (probablement « Pièces qui doivent être "
                    + "déposées », read-only, et non le champ « Motif » ultMotifReclamation). On n'écrit PAS dans un champ "
                    + $"non éditable. Valeur du champ read-only ciblé : '{roVal}'. Vérifier que l'AutomationId "
                    + "'ultMotifReclamation' est bien exposé sur l'étape Réclamation/Refus (voir screenshot + dump).");
            }
            Console.WriteLine($"      → (5) Cible « Motif » éditable confirmée (ValuePattern supporté, IsReadOnly="
                + $"{(readOnly.HasValue ? readOnly.Value.ToString().ToLowerInvariant() : "indéterminé")}).");
        }

        // Poll-jusqu'à-condition : le texte s'auto-remplit en asynchrone après le Tab. On attend qu'il
        // soit non vide (max ~6s), sinon on ajoute quand même le marqueur (le motif peut n'avoir aucun
        // texte développé selon le CODE_MOTIF — non bloquant, on prouve juste la modif).
        // ⚠ before lit le MÊME élément motifTextEl (ultMotifReclamation) → on lit bien « Pièces manquantes… »
        // et non le texte « - Bilan, compte de résultat… » du champ read-only voisin.
        string before = ReadValue(motifTextEl);
        var swText = Stopwatch.StartNew();
        while (string.IsNullOrWhiteSpace(before) && swText.ElapsedMilliseconds < 6000)
        {
            Thread.Sleep(400);
            before = ReadValue(motifTextEl);
        }
        Console.WriteLine($"      → (5) Texte du motif AVANT ajout ({swText.ElapsedMilliseconds}ms) : "
            + (string.IsNullOrWhiteSpace(before) ? "(vide)" : $"'{LegacyParsing.Truncate(before, 80)}'"));

        string after = LegacyParsing.AppendMotifMarker(before, ajout);  // logique pure testée xUnit
        Console.WriteLine($"      → (5) Écriture du texte modifié (ajout de '{ajout}' en fin) dans 'ultMotifReclamation'…");
        try { Interaction.SetText(motifTextEl, after); }
        catch (InvalidOperationException ex)
        {
            // SetValue jette InvalidOperationException si le contrôle est devenu non éditable entre la garde
            // ci-dessus et l'écriture (état asynchrone). Message orienté cause (état/read-only), pas brut.
            try { CaptureScreenshot("dca-reclam-set-texte-echec"); } catch { }
            bool? roNow = TryGetValueReadOnly(motifTextEl);
            throw new Exception("Échec écriture du texte de motif (ValuePattern.SetValue a jeté InvalidOperationException : "
                + $"« {ex.Message} »). Le champ ciblé est probablement non éditable / dans un état refusant SetValue "
                + $"(IsReadOnly relu = {(roNow.HasValue ? roNow.Value.ToString().ToLowerInvariant() : "indéterminé")}). "
                + "Vérifier que la cible est bien 'ultMotifReclamation' (champ « Motif » éditable) et non un champ read-only.");
        }
        catch (Exception ex)
        {
            try { CaptureScreenshot("dca-reclam-set-texte-echec"); } catch { }
            throw new Exception($"Échec écriture du texte de motif modifié (SetValue/WM_SETTEXT) : {ex.GetType().Name}: {ex.Message}");
        }

        // Confirme via relecture que le marqueur est bien présent dans le champ (poll court).
        var swConfirm = Stopwatch.StartNew();
        bool markerInField = false;
        string reread = "";
        while (swConfirm.ElapsedMilliseconds < 3000)
        {
            reread = ReadValue(motifTextEl);
            if (LegacyParsing.ContainsMotifMarker(reread, ajout)) { markerInField = true; break; }
            Thread.Sleep(300);
        }
        if (markerInField)
            Console.WriteLine($"      ✓ (5) Texte modifié confirmé dans le champ (contient '{ajout}') : '{LegacyParsing.Truncate(reread, 80)}'");
        else
            Console.WriteLine($"      → ⚠ (5) Marqueur '{ajout}' NON relu dans le champ après SetText (binding RIG asynchrone ?) — "
                + $"valeur relue : '{LegacyParsing.Truncate(reread, 80)}'. On clique Réclamer quand même ; la vérif finale tranchera.");

        // ── (6) + (7) Cliquer "Réclamer" (Alt+R) puis vérifier le courrier sans dépendre de l'aperçu ─
        ClickReclamerEtVerifier(ajout);
    }

    /// <summary>Coche une cellule DCA UIA (sans cliquer Valider) : TogglePattern si dispo, sinon clic écran
    /// au centre ; confirme l'état via poll (max 4s, 1 re-clic). Extrait de CheckDcaCellAndValidateUia pour
    /// le scénario réclamation (qui n'enchaîne pas sur Valider). Non bloquant si l'état n'est pas confirmé.</summary>
    private void ToggleDcaCellAndConfirm(AutomationElement dcaCell)
    {
        var r = dcaCell.BoundingRectangle;
        int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
        bool toggled = false;
        try { if (dcaCell.Patterns.Toggle.IsSupported) { dcaCell.Patterns.Toggle.Pattern.Toggle(); toggled = true; } } catch { }
        if (!toggled) Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: false);

        var sw = Stopwatch.StartNew();
        bool now = false; int reclicks = 0;
        while (sw.ElapsedMilliseconds < 4000)
        {
            Thread.Sleep(300);
            var fresh = FindDcaCheckboxDataItem();
            now = fresh is not null && IsDcaCellChecked(fresh);
            if (now) break;
            if (sw.ElapsedMilliseconds > 1500 && reclicks == 0)
            {
                reclicks++;
                Console.WriteLine("      → Case DCA toujours décochée après 1,5s — re-clic (double)");
                Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: true);
            }
        }
        Console.WriteLine(now
            ? $"      ✓ (1) Case DCA cochée (confirmé UIA en {sw.ElapsedMilliseconds}ms)"
            : "      → ⚠ (1) État coché de la case DCA non confirmé via UIA (non bloquant pour la réclamation).");
    }

    /// <summary>Localise le combo « Type de motif » de l'étape Réclamation/Refus. Stratégie :
    /// 1) AutomationId "cboTypeMotif" (Name du contrôle RIG) ; 2) un ComboBox visible voisin d'un label
    /// « Type de motif » ; 3) le 1er ComboBox visible non-trivial. Retourne null si rien trouvé.</summary>
    private AutomationElement? FindTypeMotifCombo()
    {
        if (_window is null) return null;

        // 1) Par AutomationId (le Name WinForms "cboTypeMotif" est souvent exposé comme AutomationId).
        var byId = FindByAutomationId("cboTypeMotif");
        if (byId is not null && IsUsableCombo(byId))
        {
            Console.WriteLine("      → (2/3) Combo « Type de motif » trouvé via AutomationId 'cboTypeMotif'.");
            return byId;
        }

        // Liste des ComboBox visibles (le combo RIG peut s'exposer comme ComboBox ou Pane wrapper).
        List<AutomationElement> combos;
        try
        {
            combos = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                .Where(c => { try { return c.IsAvailable && !c.IsOffscreen; } catch { return false; } })
                .ToList();
        }
        catch { combos = new List<AutomationElement>(); }

        // 2) ComboBox le plus proche d'un label "Type de motif" / "motif".
        try
        {
            var labels = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(t => { try { return t.IsAvailable && !t.IsOffscreen; } catch { return false; } })
                .Where(t =>
                {
                    var n = SafeText(() => t.Name).ToLowerInvariant();
                    return n.Contains("type de motif") || n == "motif" || n.Contains("type motif");
                })
                .ToList();
            foreach (var lbl in labels)
            {
                var lr = lbl.BoundingRectangle;
                var near = combos
                    .Where(c => { var cr = c.BoundingRectangle; return Math.Abs(cr.Y - lr.Y) < 60 || (cr.Y > lr.Y && cr.Y < lr.Y + 60); })
                    .OrderBy(c => { var cr = c.BoundingRectangle; return (cr.Y - lr.Y) * (cr.Y - lr.Y) + (cr.X - lr.X) * (cr.X - lr.X); })
                    .FirstOrDefault();
                if (near is not null)
                {
                    Console.WriteLine($"      → (2/3) Combo « Type de motif » trouvé près du label '{SafeText(() => lbl.Name)}'.");
                    return near;
                }
            }
        }
        catch { }

        // 3) Fallback : 1er ComboBox visible (l'étape réclamation n'a qu'un combo motif par opération).
        var first = combos.OrderBy(c => { try { return c.BoundingRectangle.Y; } catch { return double.MaxValue; } }).FirstOrDefault();
        if (first is not null) Console.WriteLine("      → (2/3) ⚠ Combo « Type de motif » non identifié formellement — fallback 1er ComboBox visible.");
        return first;
    }

    private bool IsUsableCombo(AutomationElement el)
    {
        try { return el.IsAvailable && !el.IsOffscreen; } catch { return false; }
    }

    /// <summary>Sélectionne dans le combo l'item correspondant à <paramref name="motif"/>. Robustesse pour le
    /// combo RCS custom (ULT_COMBO_CODE_MOTIF) qui ne peuple ses items via UIA FindAll que dropdown OUVERT
    /// (lazy : fermé, FindAll(ListItem)=0). Ordre des 3 cas :
    ///   (A) Lire d'abord la valeur COURANTE (ValuePattern.Value, sinon Name de l'élément sélectionné) :
    ///       si elle correspond déjà au motif (cf. <see cref="LegacyParsing.MotifAlreadySelected"/>) →
    ///       sélection DÉJÀ FAITE, on ne tente PAS d'énumérer, on continue (cas du run live : demande déjà
    ///       en réclamation, combo affichant "INPMANQ - …").
    ///   (B) Sinon : Expand le dropdown → poll court que les items se peuplent → énumérer → SelectionItem.Select()
    ///       (fallbacks Click item, puis ValuePattern.SetValue du code).
    ///   (C) Si après ouverture le combo a TOUJOURS 0 item → échouer (vrai problème de données),
    ///       message distinct du faux négatif "combo fermé".</summary>
    private void SelectMotifInCombo(AutomationElement combo, string motif)
    {
        Console.WriteLine($"      → (3) Sélection du type de motif '{motif}' dans le combo…");

        // ── (A) Valeur courante déjà bonne ? (combo lazy : ne PAS ouvrir/énumérer si déjà sélectionné) ──
        string current = ReadComboCurrentValue(combo);
        Console.WriteLine($"      → (3A) Valeur courante du combo : "
            + (string.IsNullOrWhiteSpace(current) ? "(vide)" : $"'{LegacyParsing.Truncate(current, 60)}'"));
        if (LegacyParsing.MotifAlreadySelected(current, motif))  // logique pure testée xUnit
        {
            Console.WriteLine($"      ✓ (3A) Motif '{motif}' DÉJÀ sélectionné dans le combo (valeur courante correspond) — "
                + "pas d'énumération du dropdown (combo RCS lazy). On continue.");
            return;
        }

        // ── (B) Pas (encore) sélectionné : ouvrir le dropdown puis attendre que les items se peuplent ──
        Console.WriteLine("      → (3B) Valeur courante absente ou différente → ouverture du dropdown pour énumérer…");
        bool expanded = false;
        try { combo.Patterns.ExpandCollapse.Pattern.Expand(); expanded = true; } catch (Exception ex) { Console.WriteLine($"      → ⚠ (3B) Expand a jeté : {ex.Message}"); }

        // Poll-jusqu'à-condition : le combo RCS peuple ses ListItem en lazy juste après Expand. On attend
        // qu'au moins 1 item apparaisse (max ~3s, poll 150ms), au lieu d'un Thread.Sleep fixe trop court.
        AutomationElement[] items = System.Array.Empty<AutomationElement>();
        var swItems = Stopwatch.StartNew();
        while (swItems.ElapsedMilliseconds < 3000)
        {
            try { items = combo.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)); }
            catch { items = System.Array.Empty<AutomationElement>(); }
            if (items.Length == 0)
            {
                // Certains ComboBox n'exposent leurs items que sous un popup List séparé.
                try { items = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)); }
                catch { items = System.Array.Empty<AutomationElement>(); }
            }
            if (items.Length > 0) break;
            Thread.Sleep(150);
        }
        Console.WriteLine($"      → (3B) {items.Length} item(s) dans le combo de motifs (dropdown ouvert={expanded}, {swItems.ElapsedMilliseconds}ms).");

        AutomationElement? match =
            // 1) match exact sur le code (le texte d'item RCS commence souvent par le code, ex "INPMANQ ...").
            items.FirstOrDefault(i => SafeText(() => i.Name).Trim().Equals(motif, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => SafeText(() => i.Name).Trim().StartsWith(motif + " ", StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => SafeText(() => i.Name).IndexOf(motif, StringComparison.OrdinalIgnoreCase) >= 0);

        if (match is not null)
        {
            Console.WriteLine($"      → (3) Item motif retenu : '{SafeText(() => match.Name)}'");
            bool selected = false;
            try { if (match.Patterns.SelectionItem.IsSupported) { match.Patterns.SelectionItem.Pattern.Select(); selected = true; } } catch { }
            if (!selected) { try { Interaction.Click(match); selected = true; } catch { } }
            try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
            if (selected) { Console.WriteLine($"      ✓ (3) Type de motif '{motif}' sélectionné."); return; }
        }

        // Fallback A : combo éditable → SetValue du code directement.
        try
        {
            if (combo.Patterns.Value.IsSupported)
            {
                Console.WriteLine($"      → (3) ⚠ Item non sélectionnable par liste — fallback ValuePattern.SetValue('{motif}').");
                combo.Patterns.Value.Pattern.SetValue(motif);
                try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
                Console.WriteLine($"      ✓ (3) Type de motif '{motif}' saisi via ValuePattern.");
                return;
            }
        }
        catch (Exception ex) { Console.WriteLine($"      → ⚠ (3) ValuePattern.SetValue a jeté : {ex.Message}"); }

        // ── (C) Échec : on distingue "combo vide même dropdown ouvert" (vrai problème de données) du
        // cas "items présents mais motif absent" (mauvais code passé / item attendu manquant). ──────
        try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
        try { CaptureScreenshot("dca-reclam-motif-introuvable-dans-combo"); } catch { }
        if (items.Length == 0)
        {
            // (C) Dropdown ouvert ET 0 item. Deux sous-cas (décision = helper pur testé IsAlreadyReclamee) :
            //   (C1) une valeur de motif est DÉJÀ présente ('current' non vide, ex « 9LIB ») => la demande
            //        ouverte est DÉJÀ réclamée : RIG verrouille alors le combo « Type de motif » (plus rien à
            //        re-choisir). Ce N'EST PAS un échec — la réclamation EXISTE (terminal métier de ce scénario
            //        atteint). On remonte un signal TYPÉ (AlreadyReclameeException) que ReclamerDcaAvecMotif
            //        convertit en succès terminal. (preuve run live 2026-06-04, screenshot 2084 : LOCODAN en
            //        état « N - Réclamation », motif « 9LIB / Veuillez » déjà posé, combo vide.)
            //   (C2) AUCUNE valeur posée => vrai écran vide / mauvais état de données (combo réellement sans
            //        option) => on garde l'échec d'origine.
            if (LegacyParsing.IsAlreadyReclamee(items.Length, current))
                throw new AlreadyReclameeException(string.IsNullOrWhiteSpace(current) ? "(vide)" : LegacyParsing.Truncate(current, 40));
            throw new Exception($"Combo « Type de motif » VIDE même dropdown ouvert (0 item après Expand + poll) — "
                + $"et la valeur courante ('{(string.IsNullOrWhiteSpace(current) ? "(vide)" : LegacyParsing.Truncate(current, 40))}') "
                + $"ne correspond pas au motif '{motif}'. Vrai problème de données : aucun motif disponible pour ce DCADEMAT "
                + "en base DEV (table CODE_MOTIF / R_CODEMOTIF_DCA_RECL). NB : la demande devrait idéalement partir d'un état "
                + "« en attente » (non encore réclamée) pour tester la 1re mise en réclamation.");
        }
        var sample = string.Join(" | ", items.Take(20).Select(i => "'" + LegacyParsing.Truncate(SafeText(() => i.Name), 40) + "'"));
        throw new Exception($"Type de motif '{motif}' introuvable parmi les {items.Length} item(s) du combo "
            + $"(items vus : {sample}). Vérifier que ce code motif existe pour le DCADEMAT en base DEV "
            + "(table CODE_MOTIF / R_CODEMOTIF_DCA_RECL), ou ajuster le code passé à ReclamerDcaAvecMotif.");
    }

    /// <summary>Lit la valeur AFFICHÉE d'un combo (ce qui est sélectionné, sans ouvrir le dropdown).
    /// 1) ValuePattern.Value (texte de la zone éditable du combo) ; 2) Name de l'élément ListItem
    /// sélectionné (SelectionPattern.Selection) ; 3) Name du combo lui-même en dernier recours.
    /// Retourne "" si rien de lisible. Pour le combo RCS lazy, c'est ce qui permet de savoir si le
    /// motif est déjà sélectionné SANS forcer l'ouverture (qui seule peuple les ListItem via UIA).</summary>
    private string ReadComboCurrentValue(AutomationElement combo)
    {
        // 1) ValuePattern : la plupart des ComboBox exposent le texte affiché ici.
        try { if (combo.Patterns.Value.IsSupported) { var v = (combo.Patterns.Value.Pattern.Value.Value ?? "").Trim(); if (v.Length > 0) return v; } } catch { }

        // 2) SelectionPattern : l'item actuellement sélectionné (son Name = texte affiché).
        try
        {
            if (combo.Patterns.Selection.IsSupported)
            {
                var sel = combo.Patterns.Selection.Pattern.Selection.Value;
                if (sel is not null && sel.Length > 0)
                {
                    var n = SafeText(() => sel[0].Name);
                    if (n.Length > 0) return n;
                }
            }
        }
        catch { }

        // 3) Dernier recours : le Name du combo (certains contrôles WinForms exposent la valeur ici).
        return SafeText(() => combo.Name);
    }

    /// <summary>Localise le champ texte « Motif » (ultMotifReclamation / Ult_TextMultiLine, multiline) de
    /// l'étape Réclamation/Refus. 1) AutomationId "ultMotifReclamation" (avec retry anti-transient UIA) —
    /// dès qu'il est trouvé on le RETOURNE TEL QUEL (c'est LE champ éditable « Motif » de la section
    /// Réclamation/Refus) ; 2) SEULEMENT s'il est introuvable par AutomationId : un Edit/Document multiligne
    /// avec ValuePattern voisin d'un label « Motif » ; 3) le plus grand Edit avec ValuePattern visible.
    /// Retourne null si rien trouvé.
    /// ⚠ Bug run live (corrigé) : l'ancienne garde « byId != null && SupportsValue(byId) » pouvait
    /// court-circuiter sur un faux négatif transitoire (UIA cache juste après le Tab) → on tombait dans le
    /// fallback « plus grand champ texte visible » qui sélectionnait le champ READ-ONLY « Pièces qui doivent
    /// être déposées » → InvalidOperationException sur SetValue. On ne gate donc PLUS la cible AutomationId sur
    /// SupportsValue ; la vérif ValuePattern/read-only est faite au site d'écriture (étape 5) avec message clair.</summary>
    private AutomationElement? FindMotifTexteField()
    {
        if (_window is null) return null;

        // 1) Par AutomationId, avec retry (en mode parallèle, FindFirstDescendant peut return null
        // transitoirement même quand l'élément existe — cf. FindByAutomationIdWithRetry). Si trouvé →
        // c'est LE bon champ : on le retourne directement, AUCUN fallback (ne PAS retomber sur le plus
        // grand champ texte, qui est le champ read-only « Pièces qui doivent être déposées »).
        var byId = FindByAutomationIdWithRetry("ultMotifReclamation", timeoutMs: 2500, pollMs: 120);
        if (byId is not null)
        {
            Console.WriteLine("      → (5) Champ texte « Motif » trouvé via AutomationId 'ultMotifReclamation' (cible directe, pas de fallback).");
            return byId;
        }
        Console.WriteLine("      → (5) ⚠ AutomationId 'ultMotifReclamation' introuvable (après retry) — fallback heuristique label/plus-grand-champ.");

        // Candidats : éléments avec ValuePattern, visibles, plutôt hauts (multiline) — exclut les Edits
        // mono-ligne étroits (combos, montants).
        List<AutomationElement> valueEls;
        try
        {
            valueEls = _window.FindAllDescendants()
                .Where(c =>
                {
                    try
                    {
                        if (!c.IsAvailable || c.IsOffscreen) return false;
                        if (!c.Patterns.Value.IsSupported) return false;
                        if (c.ControlType == ControlType.ComboBox) return false; // pas le combo motif
                        var r = c.BoundingRectangle;
                        return r.Width >= 120 && r.Height >= 30;
                    }
                    catch { return false; }
                })
                .ToList();
        }
        catch { valueEls = new List<AutomationElement>(); }

        // 2) Le plus proche d'un label "Motif" (mais pas "Type de motif" / "Montant").
        try
        {
            var motifLabels = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(t => { try { return t.IsAvailable && !t.IsOffscreen; } catch { return false; } })
                .Where(t =>
                {
                    var n = SafeText(() => t.Name).Trim().ToLowerInvariant();
                    return n == "motif" || (n.Contains("motif") && !n.Contains("type") && !n.Contains("montant"));
                })
                .ToList();
            foreach (var lbl in motifLabels)
            {
                var lr = lbl.BoundingRectangle;
                var near = valueEls
                    .Where(c => { var cr = c.BoundingRectangle; return cr.Y >= lr.Y - 30 && cr.Y < lr.Y + 80; })
                    .OrderBy(c => { var cr = c.BoundingRectangle; return (cr.Y - lr.Y) * (cr.Y - lr.Y) + (cr.X - lr.X) * (cr.X - lr.X); })
                    .FirstOrDefault();
                if (near is not null)
                {
                    Console.WriteLine($"      → (5) Champ texte « Motif » trouvé près du label '{SafeText(() => lbl.Name)}'.");
                    return near;
                }
            }
        }
        catch { }

        // 3) Fallback : le plus GRAND champ à ValuePattern (le multiline motif domine en surface).
        var biggest = valueEls.OrderByDescending(c => { try { var r = c.BoundingRectangle; return r.Width * r.Height; } catch { return 0; } }).FirstOrDefault();
        if (biggest is not null) Console.WriteLine("      → (5) ⚠ Champ « Motif » non identifié formellement — fallback plus grand champ texte visible.");
        return biggest;
    }

    private bool SupportsValue(AutomationElement el)
    {
        try { return el.Patterns.Value.IsSupported; } catch { return false; }
    }

    /// <summary>État read-only d'un champ via ValuePattern.IsReadOnly. Retourne true/false si déterminable,
    /// ou null si le ValuePattern n'est pas supporté / l'état n'est pas lisible (transitoire). null = on ne
    /// sait pas (on laissera l'écriture tenter), true = champ NON éditable (mauvaise cible → erreur claire).</summary>
    private bool? TryGetValueReadOnly(AutomationElement el)
    {
        try
        {
            if (!el.Patterns.Value.IsSupported) return null;
            return el.Patterns.Value.Pattern.IsReadOnly.Value;
        }
        catch { return null; }
    }

    /// <summary>Lit la valeur d'un champ : ValuePattern d'abord, sinon Name. "" si rien.</summary>
    private string ReadValue(AutomationElement el)
    {
        try { if (el.Patterns.Value.IsSupported) return (el.Patterns.Value.Pattern.Value.Value ?? "").Trim(); } catch { }
        return SafeText(() => el.Name);
    }

    /// <summary>Localise le bouton « Réclamer » (ToolStripButton RECLAMATION, Text "Réclamer", Alt+R) de la
    /// toolbar de l'étape Réclamation/Refus. Match par Name "Réclamer" (le '&' mnémonique est retiré de
    /// l'AccessibleName WinForms ; on tolère quand même un Name avec '&'). Retourne null si introuvable.</summary>
    private AutomationElement? FindReclamerButton()
    {
        if (_window is null) return null;
        try
        {
            return _window.FindAllDescendants()
                .FirstOrDefault(c =>
                {
                    if (c.ControlType != ControlType.Button && c.ControlType != ControlType.MenuItem) return false;
                    var n = SafeText(() => c.Name).Replace("&", "").Trim().ToLowerInvariant();
                    // "Réclamer" (exact) — éviter "Réclamation" libellés d'onglet/titre éventuels.
                    return n == "réclamer" || n == "reclamer";
                });
        }
        catch { return null; }
    }

    /// <summary>
    /// (6) Clique « Réclamer » puis (7) vérifie le courrier SANS se reposer sur l'aperçu écran (mur HDESK).
    /// Capture une baseline (PDFs disque + process viewer) AVANT le clic, déclenche Réclamer (bouton, fallback
    /// Alt+R), puis :
    ///   (a) si un fichier courrier (PDF/doc) apparaît / est récupérable via la cmdline d'un viewer → on en
    ///       extrait le texte (PdfPig pour les PDF) et on VÉRIFIE que <paramref name="marker"/> y figure ;
    ///   (b) sinon → on accepte un signal d'ouverture non-visuel (process viewer / fenêtre / fichier / onglet)
    ///       comme preuve que Réclamer s'est exécuté sans crash, en signalant la LIMITE : la présence de
    ///       "<paramref name="marker"/>" dans le courrier n'a PAS pu être confirmée (mur HDESK).
    /// ⚠ NE PAS IMPRIMER : aucun bouton « Imprimer » ni boîte d'impression n'est touché.
    /// </summary>
    private void ClickReclamerEtVerifier(string marker)
    {
        // Baseline disque/process AVANT le clic (modèle OpenKbisDocument).
        var docDirsBefore = DocOutputDirs.SelectMany(SafeDocList).ToHashSet();
        var procsBefore = Process.GetProcesses().Select(p => { try { return p.Id; } catch { return -1; } }).Where(i => i > 0).ToHashSet();

        var reclamerBtn = FindReclamerButton();

        // (6) Déclenche Réclamer via VerifyDocumentOpened (capture process/fenêtre/fichier/onglet autour du
        // trigger). Trigger = clic du bouton si trouvé, sinon Alt+R posté sur la fenêtre (RigFormAutomateVB6
        // mappe Alt+R → RECLAMATION). VerifyDocumentOpened throw si AUCUN signal après le timeout.
        bool signalOpened = true;
        string signalDetail = "";
        try
        {
            VerifyDocumentOpened(() =>
            {
                if (reclamerBtn is not null)
                {
                    Console.WriteLine($"      → (6) Clic « Réclamer » (Type={SafeText(() => reclamerBtn.ControlType.ToString())} Name='{SafeText(() => reclamerBtn.Name)}')");
                    try { Interaction.Click(reclamerBtn); }
                    catch (Exception ex) { Console.WriteLine($"      → ⚠ Click bouton Réclamer a jeté ({ex.Message}) — fallback Alt+R"); PostAltR(); }
                }
                else
                {
                    Console.WriteLine("      → (6) ⚠ Bouton « Réclamer » introuvable par Name — fallback raccourci Alt+R posté sur la fenêtre.");
                    PostAltR();
                }
            }, "Courrier de réclamation (DCADEMAT)", waitSeconds: 25);
        }
        catch (Exception ex)
        {
            // Aucun signal d'ouverture détecté. En HDESK (Mode B), l'aperçu avant impression ne peint pas
            // (mur HDESK partie b). On NE throw PAS systématiquement : si RIG est vivant et n'a pas crashé,
            // la réclamation a pu s'exécuter sans matérialiser de fenêtre/fichier détectable. On bascule sur
            // la vérif "RIG vivant" plus bas. On retient l'info pour le rapport.
            signalOpened = false;
            signalDetail = ex.Message;
            Console.WriteLine($"      → (6/7) ⓘ Aucun signal d'ouverture détecté autour de Réclamer : {ex.Message}");
            CaptureFullVirtualScreen("dca-reclam-apercu-absent-mur-hdesk");
        }

        // ── (6b) TERMINAL-OK : RIG a auto-ouvert un PROCESSUS DE SUIVI (MB1 / « Création processus » /
        // facturation) après une réclamation actée → SUCCÈS terminal, on SORT (pas de busy-poll). ───────
        // ⚠ Preuve run live 2026-06-04 (log CLI-MB1-D2608301626) : une réclamation DCADEMAT qui RÉUSSIT
        //   déclenche côté RIG la création automatique d'un processus de suivi « MB1 » (CREATION PROCESSUS,
        //   facturation) et RIG parke le focus sur une ult en attente de saisie. Le driver attendait sa
        //   condition terminale habituelle (aperçu/courrier) qui n'arrive jamais sur cet écran → busy-poll
        //   (worker observé à 13+ min, cpuSec=378). OR l'apparition du suivi MB1 PROUVE que la réclamation a
        //   abouti (RIG ne le crée qu'après une réclamation actée). On lit le texte agrégé et, si
        //   IsReclamationFollowupProcessOpened (helper pur testé, discriminant pour ne PAS matcher l'écran
        //   de réclamation lui-même), on conclut en succès SANS toucher au nouveau processus (NE PAS IMPRIMER
        //   respecté : aucune saisie/validation dans le suivi MB1) et on SORT immédiatement (pas de poll 7a).
        {
            string aggAfterReclamer = ReadWindowTextAggregate(maxChars: 6000);
            if (LegacyParsing.IsReclamationFollowupProcessOpened(aggAfterReclamer))
            {
                EnsureRigStillAlive();   // un crash resterait un échec dur
                try { CaptureScreenshot("dca-reclamation-ok-suivi-mb1-cree"); } catch { }
                Console.WriteLine($"      ✓ (6b) Réclamation DCADEMAT ACTÉE : RIG a auto-ouvert un processus de SUIVI "
                    + "(« Création processus » / MB1 facturation / « Entrée dans le RCS ») sur la demande après « Réclamer » "
                    + "— c'est la preuve que la réclamation a abouti (RIG ne crée le suivi qu'après une réclamation actée). "
                    + "Terminal métier ATTEINT → succès. On NE saisit/valide RIEN dans le nouveau processus, on SORT "
                    + "(pas de busy-poll). NE PAS IMPRIMER respecté.");
                return;
            }
        }

        // (7a) Tente de récupérer un fichier courrier généré par Réclamer et d'y vérifier le marqueur.
        Console.WriteLine("      → (7a) Recherche d'un fichier courrier récupérable (disque + cmdline viewer)…");
        string? courrierFile = TryCaptureGeneratedDocument(docDirsBefore, procsBefore, DocOutputDirs, waitSeconds: 20);
        if (!string.IsNullOrEmpty(courrierFile))
        {
            Console.WriteLine($"      → (7a) Fichier courrier candidat : {courrierFile}");
            string? extracted = TryExtractText(courrierFile!);
            if (extracted is not null)
            {
                bool hasMarker = LegacyParsing.ContainsMotifMarker(extracted, marker);  // logique pure testée xUnit
                // Sidecar texte pour inspection humaine.
                try { File.WriteAllText(courrierFile + ".extracted.txt", extracted, new System.Text.UTF8Encoding(false)); } catch { }
                if (hasMarker)
                {
                    Console.WriteLine($"      ✓ (7) VÉRIFIÉ : le marqueur '{marker}' figure dans le courrier de réclamation généré "
                        + $"({Path.GetFileName(courrierFile)}, {extracted.Length} chars). La modif du texte de motif est bien prise en compte.");
                    return;
                }
                // Fichier lisible mais marqueur absent → ÉCHEC dur : la modif n'a PAS été propagée au courrier.
                try { CaptureScreenshot("dca-reclam-marqueur-absent-du-courrier"); } catch { }
                throw new Exception($"Courrier de réclamation généré ({Path.GetFileName(courrierFile)}, {extracted.Length} chars de texte) "
                    + $"mais le marqueur '{marker}' NE s'y trouve PAS → la modification du texte de motif n'a pas été reportée dans le courrier. "
                    + $"Extrait début : '{LegacyParsing.Truncate(extracted, 200)}'");
            }
            Console.WriteLine($"      → (7a) ⓘ Fichier courrier trouvé mais texte non extractible (pas de couche texte / format non géré) → "
                + $"impossible de confirmer '{marker}' par ce chemin. On passe à la confirmation non-visuelle.");
        }
        else
        {
            Console.WriteLine("      → (7a) Aucun fichier courrier récupérable (l'aperçu RIG peut rendre dans un OCX/composition DWM "
                + "non matérialisé en fichier sur HDESK).");
        }

        // (7b) Confirmation NON visuelle : RIG toujours vivant + (si on l'a) un signal d'ouverture.
        // LIMITE explicite : sans fichier courrier lisible, on NE peut PAS prouver que "marker" figure dans
        // le courrier (mur HDESK). On valide donc le sous-objectif « Réclamer s'est exécuté sans crash » :
        //   - le marqueur a été écrit + relu dans le champ Motif AVANT le clic (loggé en (5)) ;
        //   - Réclamer a été déclenché (bouton/Alt+R) sans exception ;
        //   - RIG est toujours vivant (process non terminé, fenêtre principale répond).
        EnsureRigStillAlive();
        if (signalOpened)
        {
            Console.WriteLine($"      ✓ (7b) Réclamation exécutée : un signal d'ouverture a été détecté (courrier/aperçu/onglet) et RIG est vivant. "
                + $"⚠ LIMITE : le contenu du courrier n'a pas pu être lu (aperçu non matérialisé en fichier sur HDESK) → la présence de "
                + $"'{marker}' DANS LE COURRIER n'est pas confirmée par cette exécution (elle l'est dans le champ de saisie, étape 5). "
                + $"Pour confirmer visuellement l'aperçu : rejouer en Mode A/C (desktop composé).");
            return;
        }

        // Pas de fichier lisible ET pas de signal d'ouverture : on tolère SI RIG est vivant (mur HDESK dur),
        // mais on le signale fortement. Si RIG était mort, EnsureRigStillAlive() aurait déjà throw.
        Console.WriteLine($"      ✓ (7b) Réclamer déclenché sans crash et RIG toujours vivant, MAIS aucun signal d'ouverture NI fichier "
            + $"courrier détecté (mur HDESK : aperçu avant impression non matérialisé sur desktop non composé). "
            + $"⚠ LIMITE FORTE : ni le déclenchement de l'aperçu, ni la présence de '{marker}' dans le courrier ne sont confirmés ici "
            + $"(seule la modif du champ de saisie l'est, étape 5). Signal manquant détaillé : {signalDetail}. "
            + $"Validation visuelle à faire en Mode A/C avec supervision.");
    }

    /// <summary>Poste le raccourci Alt+R sur la fenêtre RIG (déclenche RECLAMATION via RigFormAutomateVB6).
    /// Combine WM_SYSKEYDOWN/UP (R avec contexte ALT) — fallback du clic bouton si le Name n'est pas trouvé.</summary>
    private void PostAltR()
    {
        IntPtr hwnd = IntPtr.Zero;
        try { if (_window!.Properties.NativeWindowHandle.IsSupported) hwnd = _window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("      → ⚠ Alt+R : hwnd fenêtre introuvable."); return; }
        Interaction.PostAltKey(hwnd, 0x52 /* VK_R */);
    }

    /// <summary>Vérifie que RIG n'a pas crashé après une action (Réclamer / Valider / Refuser) : process
    /// vivant + fenêtre principale lisible. Throw si le process est terminé (= crash, échec dur du scénario).
    /// <paramref name="actionLabel"/> nomme l'action dans les messages (défaut « Réclamer »).</summary>
    private void EnsureRigStillAlive(string actionLabel = "Réclamer")
    {
        try { if (_app is not null && _app.HasExited) throw new Exception($"Le process RigClientAccueil s'est terminé (crash) après {actionLabel}."); }
        catch (InvalidOperationException) { /* HasExited peut jeter si déjà disposed — traité comme mort */ throw new Exception($"Process RIG inaccessible après {actionLabel} (probable crash)."); }
        // Sanity UIA : la fenêtre principale répond toujours (lecture d'un titre/rect).
        try { var _ = _window!.BoundingRectangle; }
        catch (Exception ex) { throw new Exception($"Fenêtre principale RIG ne répond plus après {actionLabel} (probable crash) : {ex.GetType().Name}."); }
        Console.WriteLine($"      → RIG toujours vivant après {actionLabel} (process actif + fenêtre principale répond).");
    }

    /// <summary>
    /// Récupère un document (courrier de réclamation) généré par RIG, modèle OpenKbisDocument :
    /// 1) poll les dossiers doc connus pour un fichier PDF/doc apparu depuis la baseline (le plus récent) ;
    /// 2) sinon, repère un nouveau process viewer (Acrobat/Edge/Word/DocDemat…) et extrait le chemin du
    ///    document de sa ligne de commande (GetPdfPathFromProcessCmdline pour les PDF). Retourne le chemin
    ///    ou null. Poll-jusqu'à-condition (max <paramref name="waitSeconds"/>s).
    /// </summary>
    private string? TryCaptureGeneratedDocument(HashSet<string> docsBefore, HashSet<int> procsBefore, string[] docDirs, int waitSeconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < waitSeconds)
        {
            // 1) Nouveau fichier doc sur disque (le plus récent).
            try
            {
                var fresh = docDirs.SelectMany(SafeDocList).Except(docsBefore)
                    .Select(p => { DateTime when; try { when = File.GetLastWriteTimeUtc(p); } catch { when = DateTime.MinValue; } return (Path: p, When: when); })
                    .OrderByDescending(t => t.When)
                    .ToList();
                if (fresh.Count > 0 && File.Exists(fresh[0].Path)) return fresh[0].Path;
            }
            catch { }

            // 2) Nouveau process viewer → chemin via cmdline (PDF). Modèle GetPdfPathFromProcessCmdline.
            // Helper pur testé en xUnit (KbisTextChecks) : couvre RigAffichageDoc/PROC_DOC_DEMAT/WINWORD/
            // Vintasoft/Acro/Foxit/Sumatra/msedge/chrome/DocDemat (corrige l'oubli historique PROC_DOC + Vintasoft).
            try
            {
                var newViewers = Process.GetProcesses()
                    .Select(p => { try { return (id: p.Id, name: p.ProcessName); } catch { return (id: -1, name: ""); } })
                    .Where(t => t.id > 0 && !procsBefore.Contains(t.id) && KbisTextChecks.IsMeaningfulDocDematProc(t.name))
                    .ToList();
                foreach (var v in newViewers)
                {
                    var fromCmd = GetPdfPathFromProcessCmdline(v.id);
                    if (!string.IsNullOrEmpty(fromCmd) && File.Exists(fromCmd)) return fromCmd;
                }
            }
            catch { }

            Thread.Sleep(500);
        }
        return null;
    }

    /// <summary>Extrait la couche texte d'un document généré : PdfPig pour les .pdf, lecture brute pour .txt/.rtf.
    /// Les .doc/.docx ne sont PAS dépaquetés ici (pas de dépendance Word) → null (le contenu ne peut pas être
    /// vérifié par ce chemin). Retourne null si extraction impossible.</summary>
    private string? TryExtractText(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf")
            {
                var sb = new System.Text.StringBuilder();
                using (var doc = UglyToad.PdfPig.PdfDocument.Open(path))
                    foreach (var page in doc.GetPages()) sb.AppendLine(page.Text);
                var txt = sb.ToString();
                Console.WriteLine($"      → (7a) PDF courrier ouvert : {txt.Length} caractères de texte.");
                return txt;
            }
            if (ext == ".txt" || ext == ".rtf")
                return File.ReadAllText(path);
            Console.WriteLine($"      → (7a) ⓘ Extension '{ext}' non gérée pour l'extraction texte (pas de dépendance Word) → contenu non vérifiable par ce chemin.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      → (7a) ⓘ Extraction texte échouée ({Path.GetFileName(path)}) : {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Cherche la case "DCA" de la 1re ligne DATA de la grille Exercices via UIA `DataItem` (pattern XEX :
    /// le DataGridView WinForms expose ses cellules comme DataItem "Col Ligne N"). On vise "DCA Ligne N" avec
    /// N>=1 (data), fallback "DCA Ligne 0". Retourne la cellule UIA ou null. Visible (non offscreen) requis.
    /// </summary>
    private AutomationElement? FindDcaCheckboxDataItem()
    {
        if (_window is null) return null;
        List<AutomationElement> all;
        try
        {
            all = _window.FindAllDescendants()
                .Where(c => { try { return c.IsAvailable && !c.IsOffscreen; } catch { return false; } })
                .ToList();
        }
        catch { return null; }

        bool IsDataItem(AutomationElement c)
        { try { return c.ControlType.ToString().IndexOf("DataItem", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } }

        // "DCA Ligne N" data rows (N>=1), trié par Y (la 1re ligne en haut). Exclut "DCACO …" (autre colonne).
        var dcaCells = all.Where(c =>
        {
            if (!IsDataItem(c)) return false;
            var n = SafeText(() => c.Name);
            if (n.StartsWith("DCACO", StringComparison.OrdinalIgnoreCase)) return false; // exclusion COLONNE
            return LegacyParsing.IsDataRowName(n, "DCA ");
        }).OrderBy(c => { try { return c.BoundingRectangle.Y; } catch { return double.MaxValue; } }).ToList();

        var cell = dcaCells.FirstOrDefault();
        if (cell is null)
        {
            // Fallback : "DCA Ligne 0" (selon l'indexation des lignes du DGV).
            cell = all.FirstOrDefault(c => IsDataItem(c)
                && SafeText(() => c.Name).Equals("DCA Ligne 0", StringComparison.OrdinalIgnoreCase));
        }
        if (cell is not null)
        {
            var r = cell.BoundingRectangle;
            string toggle = "?"; try { if (cell.Patterns.Toggle.IsSupported) toggle = cell.Patterns.Toggle.Pattern.ToggleState.Value.ToString(); } catch { }
            Console.WriteLine($"      → Case DCA (UIA DataItem) : Name='{SafeText(() => cell.Name)}' Rect=({r.X},{r.Y} {r.Width}×{r.Height}) Toggle={toggle}");
        }
        return cell;
    }

    /// <summary>État coché d'une cellule DCA UIA (TogglePattern.On, sinon Value "1"/"true"/"vrai"/"oui").</summary>
    private bool IsDcaCellChecked(AutomationElement cell)
    {
        try { if (cell.Patterns.Toggle.IsSupported) return cell.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On; } catch { }
        try
        {
            if (cell.Patterns.Value.IsSupported)
            {
                var v = (cell.Patterns.Value.Pattern.Value.Value ?? "").Trim().ToLowerInvariant();
                return v == "1" || v == "true" || v == "vrai" || v == "oui" || v == "coché" || v == "coche";
            }
        }
        catch { }
        return false;
    }

    /// <summary>Coche (si nécessaire) la case DCA UIA puis clique Valider. Toggle via TogglePattern si
    /// dispo, sinon clic écran au centre de la cellule (pattern XEX). Confirme l'état via poll.</summary>
    private void CheckDcaCellAndValidateUia(AutomationElement dcaCell)
    {
        bool already = IsDcaCellChecked(dcaCell);
        if (already) Console.WriteLine("      → Case DCA déjà cochée (UIA) — pas de clic.");
        else
        {
            Console.WriteLine("      → Coche la case DCA (UIA)…");
            var r = dcaCell.BoundingRectangle;
            int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
            bool toggled = false;
            try { if (dcaCell.Patterns.Toggle.IsSupported) { dcaCell.Patterns.Toggle.Pattern.Toggle(); toggled = true; } } catch { }
            if (!toggled)
            {
                // Pas de TogglePattern → clic écran sur la cellule (CheckBoxCell DGV) ; double pour fiabiliser.
                Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: false);
            }
            // Poll-jusqu'à-condition (max 4s), re-clic une fois si besoin.
            var swChk = Stopwatch.StartNew();
            bool now = false; int reclicks = 0;
            while (swChk.ElapsedMilliseconds < 4000)
            {
                Thread.Sleep(300);
                var fresh = FindDcaCheckboxDataItem();
                now = fresh is not null && IsDcaCellChecked(fresh);
                if (now) break;
                if (swChk.ElapsedMilliseconds > 1500 && reclicks == 0)
                {
                    reclicks++;
                    Console.WriteLine("      → Case DCA toujours décochée après 1,5s — re-clic (double)");
                    Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: true);
                }
            }
            Console.WriteLine(now
                ? $"      ✓ Case DCA cochée (confirmé UIA en {swChk.ElapsedMilliseconds}ms)"
                : "      ⚠ État coché non confirmé via UIA — on tente Valider quand même (le succès = apparition des n°)");
        }

        // ── (b) Valider ──
        ClickValiderInActiveForm("Configurer le dépôt (DCADEMAT)");
    }

    /// <summary>Texte agrégé des DataItem visibles de la grille Exercices (UIA) — pour détecter les n°
    /// dépôt/facture/demande après Valider quand ils s'affichent comme cellules DataItem.</summary>
    private string ReadExercicesDataItemsTextUia()
    {
        if (_window is null) return "";
        try
        {
            var names = _window.FindAllDescendants()
                .Where(c => { try { return c.IsAvailable && !c.IsOffscreen && c.ControlType.ToString().IndexOf("DataItem", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } })
                .Select(c =>
                {
                    var nm = SafeText(() => c.Name);
                    string val = ""; try { if (c.Patterns.Value.IsSupported) val = c.Patterns.Value.Pattern.Value.Value ?? ""; } catch { }
                    return (nm + " " + val).Trim();
                })
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(" ; ", names);
        }
        catch { return ""; }
    }

    /// <summary>Dump diagnostic des DataItem visibles de la grille Exercices (Name + rect + Toggle/Value) —
    /// pour ajuster les sélecteurs si la case DCA / les n° ne sont pas trouvés.</summary>
    private void DumpExercicesDataItemsUia()
    {
        if (_window is null) return;
        Console.WriteLine("      [DUMP UIA DataItems] grille Exercices (visibles) :");
        try
        {
            int n = 0;
            foreach (var c in _window.FindAllDescendants())
            {
                bool di; try { di = c.IsAvailable && !c.IsOffscreen && c.ControlType.ToString().IndexOf("DataItem", StringComparison.OrdinalIgnoreCase) >= 0; } catch { continue; }
                if (!di) continue;
                var r = c.BoundingRectangle;
                string toggle = "-"; try { if (c.Patterns.Toggle.IsSupported) toggle = c.Patterns.Toggle.Pattern.ToggleState.Value.ToString(); } catch { }
                string val = "-"; try { if (c.Patterns.Value.IsSupported) val = c.Patterns.Value.Pattern.Value.Value ?? ""; } catch { }
                Console.WriteLine($"      [DUMP UIA DataItems]   Name='{SafeText(() => c.Name)}' Rect=({(int)r.X},{(int)r.Y} {(int)r.Width}×{(int)r.Height}) Toggle={toggle} Value='{val}'");
                if (++n > 60) { Console.WriteLine("      [DUMP UIA DataItems]   … (tronqué à 60)"); break; }
            }
            if (n == 0) Console.WriteLine("      [DUMP UIA DataItems]   (aucun DataItem visible)");
        }
        catch (Exception ex) { Console.WriteLine($"      [DUMP UIA DataItems] scan jeté : {ex.GetType().Name}"); }
    }

    /// <summary>HWND de la grille "Exercices" (DataGridView WinForms) de l'écran "Configurer le dépôt".
    /// Essaye AutomationId connus, sinon un Table/DataGrid descendant, sinon la fenêtre active. Le hwnd
    /// sert à AccessibleObjectFromWindow (MSAA) car la grille est souvent aveugle à UIA.</summary>
    private IntPtr FindExercicesGridHwnd()
    {
        AutomationElement? grid =
               FindByAutomationId("dgvExercices")
            ?? FindByAutomationId("dgvExercice")
            ?? FindByAutomationId("ultraGridExercices")
            ?? FindByAutomationId("gridExercices")
            ?? _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Table))
            ?? _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid));

        IntPtr hwnd = IntPtr.Zero;
        if (grid is not null)
        {
            try { hwnd = grid.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            if (hwnd == IntPtr.Zero)
            {
                try { var inner = grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.Table)); if (inner != null) hwnd = inner.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            }
        }
        if (hwnd == IntPtr.Zero) { try { hwnd = _window!.Properties.NativeWindowHandle.ValueOrDefault; } catch { } }
        return hwnd;
    }

    /// <summary>true si l'écran actif est le "Tableau des éditions" DCADEMAT (envoi courriers/pièces RCS),
    /// PAS "Configurer le dépôt". Détecté via UIA : présence des libellés "Tableau des éditions" /
    /// "Détail de l'édition" / "Modèle du courriel" / "Corps du courriel". Sur cet écran, on ne clique
    /// PAS Valider (garde NE PAS IMPRIMER : la colonne 🖨 est cochée → un envoi/impression partirait).</summary>
    private bool IsTableauEditionsScreen()
    {
        if (_window is null) return false;
        string[] markers = { "Tableau des éditions", "Détail de l'édition", "Modèle du courriel", "Corps du courriel" };
        try
        {
            int hits = 0;
            foreach (var c in _window.FindAllDescendants())
            {
                var n = SafeText(() => c.Name);
                if (string.IsNullOrEmpty(n)) continue;
                foreach (var m in markers)
                    if (n.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { hits++; break; }
                if (hits >= 2) { Console.WriteLine("      → Écran détecté : 'Tableau des éditions' (DCADEMAT déjà qualifiée)."); return true; }
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ IsTableauEditionsScreen scan jeté : {ex.GetType().Name}"); }
        return false;
    }

    /// <summary>Lit via MSAA la 1re ligne "data" de la grille Exercices : retourne le centre écran de la
    /// cellule la plus à GAUCHE (= case "DCA"), le texte agrégé de la ligne, et l'état coché (heuristique
    /// sur accState CHECKED / accValue). Réutilise le pattern oleacc de CollectCellsMsaa.</summary>
    private (int dcaCx, int dcaCy, string rowText, bool checkedState)? FindFirstExerciceRowMsaa(IntPtr hwnd)
    {
        var rows = CollectExerciceRowsMsaa(hwnd, max: 1);
        return rows.Count > 0 ? rows[0] : ((int, int, string, bool)?)null;
    }

    /// <summary>Texte agrégé (toutes lignes data jointes) de la grille Exercices via MSAA — sert à
    /// détecter les n° de dépôt/facture/demande après Valider.</summary>
    private string ReadAllExerciceRowsTextMsaa(IntPtr hwnd)
    {
        var rows = CollectExerciceRowsMsaa(hwnd, max: 12);
        return string.Join(" || ", rows.Select(r => r.rowText));
    }

    private const int STATE_SYSTEM_CHECKED = 0x10;

    /// <summary>
    /// Parcourt l'arbre MSAA du client de <paramref name="hwnd"/> (DataGridView Exercices) et collecte
    /// jusqu'à <paramref name="max"/> LIGNES data : pour chaque ligne, le texte agrégé (accName+accValue
    /// de la ligne et de ses cellules), le centre écran de la cellule la plus à gauche (case DCA), et un
    /// état coché best-effort (STATE_SYSTEM_CHECKED sur la ligne/1re cellule, ou accValue "1"/"true"/"vrai"/"x"/"oui").
    /// Lecture seule. Pattern identique à CollectCellsMsaa mais SANS le filtre ';' (la grille Exercices
    /// n'a pas forcément le même format de ligne que la grille des demandes).
    /// </summary>
    private List<(int dcaCx, int dcaCy, string rowText, bool checkedState)> CollectExerciceRowsMsaa(IntPtr hwnd, int max)
    {
        var results = new List<(int, int, string, bool)>();
        if (hwnd == IntPtr.Zero) return results;
        const uint OBJID_CLIENT = 0xFFFFFFFC;
        var iid = IID_IAccessible;
        Accessibility.IAccessible? root = null;
        try
        {
            if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var obj) != 0 || obj is not Accessibility.IAccessible a)
            { Console.WriteLine("      ⓘ AccessibleObjectFromWindow (Exercices) : pas d'IAccessible."); return results; }
            root = a;
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ AccessibleObjectFromWindow (Exercices) jeté : {ex.Message}"); return results; }

        bool LooksChecked(Accessibility.IAccessible node, object childId, string value)
        {
            try
            {
                var st = node.get_accState(childId);
                if (st is int si && (si & STATE_SYSTEM_CHECKED) != 0) return true;
            }
            catch { }
            var v = (value ?? "").Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "vrai" || v == "x" || v == "oui" || v == "checked" || v == "coché" || v == "coche";
        }

        // Une "ligne" du DGV = un IAccessible de role ROW (0x1C) ; ses enfants = cellules.
        // Mais selon le custom DGV, la ligne peut être un enfant simple avec accName concaténé.
        const int ROLE_SYSTEM_ROW = 0x1C;
        const int ROLE_SYSTEM_ROWHEADER = 0x20;
        const int ROLE_SYSTEM_COLUMNHEADER = 0x19;

        // La bande d'en-tête de colonnes du DGV custom passe parfois par MSAA comme une "ligne"
        // dont le texte agrège les TITRES de colonnes (souvent doublés : "DCA DCA", "montant facturé
        // montant facturé"). On l'écarte pour ne garder que les vraies lignes data (sinon on cliquerait
        // sur l'en-tête au lieu d'une case DCA de ligne — cf. run 2026-06-01).
        bool IsHeaderRowText(string txt)
        {
            if (string.IsNullOrWhiteSpace(txt)) return false;
            string[] colTitles = { "comptes confidentiels", "présentation simplifiée", "montant facturé",
                                    "dispense annexe", "dérogation clôture", "non approbation", "Date de clôture" };
            int titleHits = colTitles.Count(t => txt.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            return titleHits >= 2;
        }

        void Walk(Accessibility.IAccessible node, int depth)
        {
            if (results.Count >= max || depth > 8) return;
            int count; try { count = node.accChildCount; } catch { return; }
            if (count <= 0) return;
            var kids = new object[count]; int got;
            try { if (AccessibleChildren(node, 0, count, kids, out got) != 0) return; } catch { return; }

            for (int i = 0; i < got && results.Count < max; i++)
            {
                var k = kids[i];
                if (k is Accessibility.IAccessible childAcc)
                {
                    int role = 0; try { role = Convert.ToInt32(childAcc.get_accRole(0)); } catch { }
                    if (role == ROLE_SYSTEM_COLUMNHEADER || role == ROLE_SYSTEM_ROWHEADER) continue; // skip headers

                    if (role == ROLE_SYSTEM_ROW)
                    {
                        // Ligne data : agrège les cellules, prend la 1re cellule (gauche) comme case DCA.
                        var (rowText, leftCx, leftCy, isChecked) = ReadRowCells(childAcc);
                        if (IsHeaderRowText(rowText)) { Console.WriteLine("      → (ligne d'en-tête de colonnes ignorée)"); continue; }
                        if (leftCx != int.MinValue)
                            results.Add((leftCx, leftCy, rowText, isChecked));
                        continue;
                    }

                    // Conteneur (table/client/groupe) → descendre.
                    Walk(childAcc, depth + 1);
                }
                else if (k is int childId && childId != 0)
                {
                    // Cellule/élément simple porté par childId : si la ligne entière est exposée ici (accName
                    // concaténé par ';'), on la traite comme une ligne ; sinon on l'ignore (cellule isolée).
                    string text = (SafeAcc(() => node.get_accName(childId)) + " " + SafeAcc(() => node.get_accValue(childId))).Trim();
                    if (text.IndexOf(';') >= 0 && !IsHeaderRowText(text))
                    {
                        try
                        {
                            node.accLocation(out int l, out int t, out int w, out int h, childId);
                            // case DCA = extrême gauche de la ligne (la 1re colonne) ; on vise ~10px du bord gauche.
                            int leftCx = l + Math.Min(12, w / 2);
                            int leftCy = t + h / 2;
                            bool isChecked = LooksChecked(node, childId, SafeAcc(() => node.get_accValue(childId)));
                            results.Add((leftCx, leftCy, text, isChecked));
                        }
                        catch { }
                    }
                    else if (text.IndexOf(';') >= 0)
                    {
                        Console.WriteLine("      → (ligne d'en-tête de colonnes ignorée)");
                    }
                }
            }
        }

        // Lit les cellules d'une LIGNE (IAccessible role ROW) : texte agrégé + centre de la 1re cellule.
        (string rowText, int leftCx, int leftCy, bool isChecked) ReadRowCells(Accessibility.IAccessible row)
        {
            int leftCx = int.MinValue, leftCy = 0; bool isChecked = false; int minX = int.MaxValue;
            var parts = new List<string>();
            // accValue de la ligne elle-même (parfois concaténé)
            var rowOwn = (SafeAcc(() => row.get_accName(0)) + " " + SafeAcc(() => row.get_accValue(0))).Trim();
            if (!string.IsNullOrWhiteSpace(rowOwn)) parts.Add(rowOwn);

            int cc; try { cc = row.accChildCount; } catch { cc = 0; }
            if (cc > 0)
            {
                var cells = new object[cc]; int cgot;
                try
                {
                    if (AccessibleChildren(row, 0, cc, cells, out cgot) == 0)
                    {
                        for (int j = 0; j < cgot; j++)
                        {
                            object cid = cells[j] is Accessibility.IAccessible ? (object)0 : (cells[j] is int ci ? ci : (j + 1));
                            Accessibility.IAccessible cellNode = cells[j] as Accessibility.IAccessible ?? row;
                            string cval = SafeAcc(() => cellNode.get_accValue(cid));
                            string cname = SafeAcc(() => cellNode.get_accName(cid));
                            string ctext = (cname + " " + cval).Trim();
                            if (!string.IsNullOrWhiteSpace(ctext)) parts.Add(ctext);
                            try
                            {
                                cellNode.accLocation(out int l, out int t, out int w, out int h, cid);
                                if (l < minX && w > 0 && h > 0)
                                {
                                    minX = l; leftCx = l + Math.Min(12, w / 2); leftCy = t + h / 2;
                                    isChecked = LooksChecked(cellNode, cid, cval);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            // Fallback localisation : si aucune cellule n'a donné de coords, utilise la ligne entière (bord gauche).
            if (leftCx == int.MinValue)
            {
                try { row.accLocation(out int l, out int t, out int w, out int h, 0); leftCx = l + Math.Min(12, w / 2); leftCy = t + h / 2; isChecked = LooksChecked(row, 0, SafeAcc(() => row.get_accValue(0))); }
                catch { }
            }
            return (string.Join(";", parts), leftCx, leftCy, isChecked);
        }

        try { Walk(root!, 0); } catch (Exception ex) { Console.WriteLine($"      ⓘ Walk MSAA (Exercices) jeté : {ex.Message}"); }
        return results;
    }

    /// <summary>Dump best-effort de l'arbre MSAA du client de la fenêtre active (rôles + noms + valeurs)
    /// quand la grille Exercices n'est atteignable ni en UIA ni en MSAA — pour ajuster les sélecteurs.</summary>
    private void DumpMsaaTree(string context)
    {
        Console.WriteLine($"      [DUMP MSAA] {context}");
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = _window!.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("      [DUMP MSAA] hwnd fenêtre nul — impossible."); return; }
        const uint OBJID_CLIENT = 0xFFFFFFFC;
        var iid = IID_IAccessible;
        Accessibility.IAccessible? root = null;
        try
        {
            if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var obj) != 0 || obj is not Accessibility.IAccessible a)
            { Console.WriteLine("      [DUMP MSAA] pas d'IAccessible sur la fenêtre."); return; }
            root = a;
        }
        catch (Exception ex) { Console.WriteLine($"      [DUMP MSAA] AccessibleObjectFromWindow jeté : {ex.Message}"); return; }

        int printed = 0;
        void Walk(Accessibility.IAccessible node, int depth)
        {
            if (printed > 120 || depth > 6) return;
            int count; try { count = node.accChildCount; } catch { return; }
            if (count <= 0) return;
            var kids = new object[count]; int got;
            try { if (AccessibleChildren(node, 0, count, kids, out got) != 0) return; } catch { return; }
            for (int i = 0; i < got && printed <= 120; i++)
            {
                var k = kids[i];
                if (k is Accessibility.IAccessible childAcc)
                {
                    int role = 0; try { role = Convert.ToInt32(childAcc.get_accRole(0)); } catch { }
                    string nm = SafeAcc(() => childAcc.get_accName(0));
                    string vl = SafeAcc(() => childAcc.get_accValue(0));
                    if (!string.IsNullOrWhiteSpace(nm) || !string.IsNullOrWhiteSpace(vl))
                    { Console.WriteLine($"      [DUMP MSAA] {new string(' ', depth * 2)}role={role} name='{LegacyParsing.Truncate(nm)}' value='{LegacyParsing.Truncate(vl)}'"); printed++; }
                    Walk(childAcc, depth + 1);
                }
                else if (k is int cid && cid != 0)
                {
                    string nm = SafeAcc(() => node.get_accName(cid));
                    string vl = SafeAcc(() => node.get_accValue(cid));
                    if (!string.IsNullOrWhiteSpace(nm) || !string.IsNullOrWhiteSpace(vl))
                    { Console.WriteLine($"      [DUMP MSAA] {new string(' ', depth * 2)}cid={cid} name='{LegacyParsing.Truncate(nm)}' value='{LegacyParsing.Truncate(vl)}'"); printed++; }
                }
            }
        }
        try { Walk(root!, 0); } catch (Exception ex) { Console.WriteLine($"      [DUMP MSAA] Walk jeté : {ex.Message}"); }
        if (printed == 0) Console.WriteLine("      [DUMP MSAA] (aucun nœud nommé — fenêtre vide côté MSAA)");
    }

    // ── MSAA par point (oleacc) : lire le ContextMenuStrip ouvert ───────────────
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int X; public int Y; }

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleObjectFromPoint(PT pt,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppacc,
        [MarshalAs(UnmanagedType.Struct)] out object pvarChild);

    private const int ROLE_SYSTEM_MENUPOPUP = 0x0F;

    /// <summary>Lit le ContextMenuStrip ouvert via MSAA par point : sonde plusieurs points autour de la
    /// cellule (<paramref name="cx"/>,<paramref name="cy"/>), pour chacun remonte au conteneur MENUPOPUP ;
    /// dès qu'un MENUPOPUP est trouvé, énumère ses items et retourne le centre écran de celui dont le nom
    /// contient <paramref name="itemSub"/>. <paramref name="menuDump"/> = libellés de tous les items lus.
    /// À appeler sur le thread HDESK-attaché (AccessibleObjectFromPoint = desktop-affine).</summary>
    private (int x, int y)? FindMenuItemCenterViaMsaa(int cx, int cy, string itemSub, out string menuDump)
    {
        menuDump = "";
        // Le menu ouvert par VK_APPS apparaît à PointToScreen(cellule) → autour de (cx,cy), surtout en-dessous.
        var probes = new (int x, int y)[]
        {
            (cx, cy + 12), (cx, cy + 28), (cx, cy + 44), (cx - 60, cy + 12), (cx + 40, cy + 12),
            (cx, cy - 6), (cx, cy), (cx - 60, cy + 44),
        };
        foreach (var (px, py) in probes)
        {
            Accessibility.IAccessible container;
            try
            {
                if (AccessibleObjectFromPoint(new PT { X = px, Y = py }, out var accObj, out _) != 0 || accObj is not Accessibility.IAccessible a)
                    continue;
                container = a;
            }
            catch { continue; }

            int role = 0;
            for (int up = 0; up < 5; up++)
            {
                try { role = Convert.ToInt32(container.get_accRole(0)); } catch { role = 0; }
                if (role == ROLE_SYSTEM_MENUPOPUP) break;
                try { if (container.accParent is Accessibility.IAccessible par) container = par; else break; } catch { break; }
            }
            if (role != ROLE_SYSTEM_MENUPOPUP) continue; // ce point n'est pas dans le menu → essaie le suivant

            int count; try { count = container.accChildCount; } catch { count = 0; }
            if (count <= 0) return null;
            var kids = new object[count]; int got;
            try { if (AccessibleChildren(container, 0, count, kids, out got) != 0) return null; } catch { return null; }
            var names = new List<string>();
            (int x, int y)? found = null;
            for (int i = 0; i < got; i++)
            {
                string name; (int, int)? loc = null;
                if (kids[i] is Accessibility.IAccessible ia)
                {
                    name = SafeAcc(() => ia.get_accName(0));
                    try { ia.accLocation(out int l, out int t, out int w, out int h, 0); loc = (l + w / 2, t + h / 2); } catch { }
                }
                else
                {
                    int cid = kids[i] is int ci ? ci : (i + 1);
                    name = SafeAcc(() => container.get_accName(cid));
                    try { container.accLocation(out int l, out int t, out int w, out int h, cid); loc = (l + w / 2, t + h / 2); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                if (found is null && !string.IsNullOrWhiteSpace(name)
                    && name.IndexOf(itemSub, StringComparison.OrdinalIgnoreCase) >= 0 && loc.HasValue)
                    found = loc;
            }
            menuDump = string.Join(" | ", names);
            Console.WriteLine($"      [DIAG menu] MENUPOPUP trouvé via sonde ({px},{py}) — {names.Count} items");
            return found;
        }
        Console.WriteLine("      [DIAG menu] aucun MENUPOPUP trouvé aux points sondés (menu non ouvert ?).");
        return null;
    }

    /// <summary>Sélectionne la ligne (PostMessage clic gauche → fixe CurrentCell), ouvre le
    /// ContextMenuStrip via la touche Apps/Menu (VK_APPS → rdgvDemandes_KeyDown ouvre le menu à
    /// PointToScreen(cellule) = position DÉTERMINISTE, contrairement au clic-droit qui l'ouvre à
    /// MousePosition / curseur réel). Puis, si <paramref name="itemSub"/> non null, clique l'item.
    /// Message-based → marche sur HDESK isolé (SendInput y serait ignoré). Thread HDESK-attaché.</summary>
    private void DoOpenMenuAndClickItem(int cx, int cy, string? itemSub)
    {
        var gridHwnd = Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: false); // sélectionne + CurrentCell
        Thread.Sleep(400);
        // Le handler RIG MouseDown(Right) ouvre le menu à MousePosition. On positionne le curseur HDESK
        // sur la cellule (SetCursorPos marche sur HDESK), puis on déclenche le handler via WM_RBUTTONDOWN
        // posté (message → marche sur HDESK ; mouse_event/SendInput y serait ignoré).
        bool moved = Interaction.MoveCursor(cx, cy);
        Console.WriteLine($"      → Curseur HDESK déplacé sur cellule ({cx},{cy}) : {moved} ; WM_RBUTTONDOWN → menu");
        Interaction.RightClickAtScreenPoint(cx, cy, _app!.ProcessId);
        Thread.Sleep(1000);
        var hit = FindMenuItemCenterViaMsaa(cx, cy, itemSub ?? "￿", out var dump);
        Console.WriteLine($"      → Menu contextuel (MSAA) : {dump}");
        if (itemSub is null) { if (gridHwnd != IntPtr.Zero) Interaction.PostKey(gridHwnd, 0x1B); return; } // observation seule
        if (hit is null) { if (gridHwnd != IntPtr.Zero) Interaction.PostKey(gridHwnd, 0x1B); throw new Exception($"Item menu '{itemSub}' introuvable (menu lu : {dump})."); }
        Console.WriteLine($"      → Clic item '{itemSub}' @ {hit.Value.x},{hit.Value.y}");
        Interaction.ClickAtScreenPoint(hit.Value.x, hit.Value.y, _app!.ProcessId, doubleClick: false); // clique l'item
    }

    /// <summary>OBSERVATION : ouvre le menu contextuel d'une demande (WM_CONTEXTMENU) et dump ses items
    /// via MSAA (lecture par point sur le thread HDESK-attaché du scénario).</summary>
    public void RightClickDemandeAndDumpMenu(bool dcademat)
    {
        var hit = FindDemandeCell(dcademat);
        if (hit is null) throw new Exception($"Aucune demande {(dcademat ? "DCADEMAT" : "formalités J00")} trouvée pour menu contextuel.");
        var (cx, cy, txt) = hit.Value;
        Console.WriteLine($"      → Demande ('{txt}') @ {cx},{cy} — WM_CONTEXTMENU + dump MSAA");
        DoOpenMenuAndClickItem(cx, cy, null);
    }

    // ── Capture diagnostique HDESK (PrintWindow) : console RIG + tout popup éventuel ────────────
    // ⚠ BitBlt du DC desktop (GetDC(NULL)) est NOIR sur un HDESK CreateDesktop non composé (testé) :
    // pas de surface peinte au niveau desktop. SEUL PrintWindow par-hwnd rend du contenu sur HDESK
    // (prouvé par le self-snap). On capture donc la console RIG (preuve de la grille) ET, s'il existe,
    // tout hwnd popup/menu top-level du process RIG (un ContextMenuStrip OUVERT serait ainsi capturé ;
    // son absence dans la capture + l'absence MSAA = double preuve qu'aucun menu ne s'est ouvert).
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Capture diagnostique (PrintWindow, HDESK-compatible) : la console RIG + tout popup
    /// visible du même process (menu / aperçu). Best-effort, ne jette jamais. Retourne le chemin du
    /// PNG de la console (preuve principale de la grille).</summary>
    public string? CaptureFullVirtualScreen(string label)
    {
        if (string.IsNullOrEmpty(_snapDir)) { Console.WriteLine("      ⓘ CaptureFullVirtualScreen : _snapDir null (self-snap pas démarré)."); return null; }
        var safe = string.Join("_", (label ?? "diag").Split(System.IO.Path.GetInvalidFileNameChars()));
        var stamp = DateTime.Now.ToString("HHmmss-fff");
        string? mainPath = null;

        // 1) La console RIG (hwnd caché par le self-snap, sinon hwnd de _window).
        IntPtr mainHwnd = _snapHwnd;
        if (mainHwnd == IntPtr.Zero) { try { mainHwnd = _window!.Properties.NativeWindowHandle.ValueOrDefault; } catch { } }
        if (mainHwnd != IntPtr.Zero)
        {
            try
            {
                mainPath = System.IO.Path.Combine(_snapDir!, $"diag-{safe}-{stamp}.png");
                Interaction.CaptureWindowByHwnd(mainHwnd, mainPath);
                Console.WriteLine($"      📸 Diag console RIG (PrintWindow) : {mainPath}");
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ CaptureFullVirtualScreen (console) jeté : {ex.GetType().Name}: {ex.Message}"); }
        }

        // 2) Tout popup/menu top-level du même process (preuve d'un menu ouvert, le cas échéant).
        try
        {
            int rigPid = _app?.ProcessId ?? 0;
            if (rigPid > 0)
            {
                int popupSeq = 0;
                EnumWindows((h, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(h)) return true;
                        GetWindowThreadProcessId(h, out uint wpid);
                        if (wpid != (uint)rigPid) return true;
                        if (h == mainHwnd) return true;
                        var sbc = new System.Text.StringBuilder(256);
                        GetClassName(h, sbc, sbc.Capacity);
                        var cls = sbc.ToString();
                        // ContextMenuStrip WinForms = classe "WindowsForms10.Window.*" sans titre ; les
                        // vrais menus natifs = "#32768". On capture tout popup non-principal du process.
                        var pPath = System.IO.Path.Combine(_snapDir!, $"diag-{safe}-popup{++popupSeq}-{cls.Replace('.', '_')}-{stamp}.png");
                        try { Interaction.CaptureWindowByHwnd(h, pPath); Console.WriteLine($"      📸 Diag POPUP process RIG (cls='{cls}') : {pPath}"); } catch { }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ CaptureFullVirtualScreen (enum popups) : {ex.Message}"); }

        return mainPath;
    }

    /// <summary>Sonde MSAA autour de (cx,cy) pour savoir si un ContextMenuStrip (MENUPOPUP) est
    /// actuellement ouvert. Retourne true + le dump des items s'il l'est. Lecture seule, idempotent
    /// (réutilise <see cref="FindMenuItemCenterViaMsaa"/> avec un itemSub introuvable).</summary>
    private bool IsContextMenuOpen(int cx, int cy, out string dump)
    {
        // U+FFFF = sentinelle qu'aucun item ne contient → on ne récupère que le dump + le fait
        // qu'un MENUPOPUP a été trouvé (dump non vide ⇔ menu ouvert).
        FindMenuItemCenterViaMsaa(cx, cy, "￿", out dump);
        return !string.IsNullOrEmpty(dump);
    }

    // ── Détection robuste du ContextMenuStrip RIG par ÉNUMÉRATION de fenêtres (pas par point) ───
    // OBSERVATION clé (run 3, 2026-06-01) : le menu S'OUVRE bien (capture PrintWindow du popup =
    // "Reprendre les impressions" etc.), mais FindMenuItemCenterViaMsaa (sonde par point autour de
    // la cellule) ne le voyait pas : le ContextMenuStrip WinForms est un hwnd TOP-LEVEL distinct,
    // classe "WindowsForms10.Window.*", positionné ailleurs que les points sondés. On le retrouve
    // donc par EnumWindows (visible + même process + classe WinForms + non-principal + non SysShadow)
    // et on lit ses items via AccessibleObjectFromWindow sur CE hwnd.
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);

    /// <summary>Résout le hwnd de la VRAIE fenêtre console RIG à snapper. MainWindowHandle pointe parfois sur
    /// un overlay (ex. UAC_InputIndicatorOverlayWnd, WS_EX_NOREDIRECTIONBITMAP) qui rend NOIR à PrintWindow.
    /// On énumère les top-level du process RIG (desktop courant = HDESK) et on prend la fenêtre WinForms
    /// VISIBLE dont le titre évoque la console ("Console"/"accueil"/"RIG"), fallback = la plus grande WinForms
    /// visible, fallback = le hwnd fourni. Vérifié 2026-06-16 : la console (WindowsForms10.Window.8, 750x480)
    /// se capture 100% non-noir sur HDESK, l'overlay UAC rend noir.</summary>
    private static IntPtr ResolveRigConsoleHwnd(int rigPid, IntPtr fallback)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0; bool bestTitled = false;
        EnumWindows((h, l) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != (uint)rigPid || !IsWindowVisible(h)) return true;
            var cls = new System.Text.StringBuilder(256); GetClassName(h, cls, 256);
            if (cls.ToString().IndexOf("WindowsForms", StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (!GetWindowRect(h, out RECT r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area < 120 * 120) return true;
            var tit = new System.Text.StringBuilder(256); GetWindowText(h, tit, 256);
            string t = tit.ToString();
            // fix-ok: 2026-06-16 (cause confirmee DIAG3) — la fenetre LOGIN "Connexion a RIG_DEV-..." contient
            // "RIG" → l'ancien match bare "RIG" la prenait pour la console (=> _window sur le login, btn1..btn7
            // introuvables). On EXCLUT le login, et on matche la console par "Console"/"accueil".
            if (t.IndexOf("Connexion", StringComparison.OrdinalIgnoreCase) >= 0
             || t.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            bool titled = t.IndexOf("Console", StringComparison.OrdinalIgnoreCase) >= 0
                       || t.IndexOf("accueil", StringComparison.OrdinalIgnoreCase) >= 0;
            if ((titled && !bestTitled) || (titled == bestTitled && area > bestArea))
            { best = h; bestArea = area; bestTitled = titled; }
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : fallback;
    }

    /// <summary>Énumère les fenêtres pour trouver le hwnd du ContextMenuStrip ouvert : visible, du
    /// process RIG, classe "WindowsForms10.Window.*" (le ToolStripDropDown WinForms), distinct de la
    /// console principale et d'une ombre (SysShadow). Retourne IntPtr.Zero si aucun.</summary>
    private IntPtr FindMenuPopupHwnd()
    {
        IntPtr found = IntPtr.Zero;
        int rigPid = _app?.ProcessId ?? 0;
        if (rigPid <= 0) return IntPtr.Zero;
        IntPtr mainHwnd = _snapHwnd;
        if (mainHwnd == IntPtr.Zero) { try { mainHwnd = _window!.Properties.NativeWindowHandle.ValueOrDefault; } catch { } }
        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    GetWindowThreadProcessId(h, out uint wpid);
                    if (wpid != (uint)rigPid) return true;
                    if (h == mainHwnd) return true;
                    var sb = new System.Text.StringBuilder(256);
                    GetClassName(h, sb, sb.Capacity);
                    var cls = sb.ToString();
                    if (cls.IndexOf("WindowsForms", StringComparison.OrdinalIgnoreCase) < 0) return true; // ni SysShadow ni #32768 wrapper
                    // Heuristique : un menu a une certaine hauteur (plusieurs items). On exige >40px.
                    if (GetWindowRect(h, out var r))
                    {
                        int hgt = r.Bottom - r.Top, wid = r.Right - r.Left;
                        if (hgt < 40 || wid < 30) return true;
                    }
                    found = h;
                    return false; // stop : 1er popup WinForms trouvé
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ FindMenuPopupHwnd jeté : {ex.Message}"); }
        return found;
    }

    /// <summary>Lit les items du ContextMenuStrip via MSAA sur le hwnd du popup
    /// (AccessibleObjectFromWindow + OBJID_CLIENT). Retourne le centre écran de l'item dont le nom
    /// contient <paramref name="itemSub"/> et remplit <paramref name="dump"/> (tous les libellés lus).
    /// itemSub = U+FFFF (introuvable) → sert juste à récupérer le dump (détection présence menu).</summary>
    private (int x, int y)? ReadMenuItemFromHwnd(IntPtr menuHwnd, string itemSub, out string dump)
    {
        dump = "";
        if (menuHwnd == IntPtr.Zero) return null;
        const uint OBJID_CLIENT = 0xFFFFFFFC;
        var iid = IID_IAccessible;
        Accessibility.IAccessible? root;
        try
        {
            if (AccessibleObjectFromWindow(menuHwnd, OBJID_CLIENT, ref iid, out var obj) != 0 || obj is not Accessibility.IAccessible a)
            { Console.WriteLine("      ⓘ ReadMenuItemFromHwnd : pas d'IAccessible sur le hwnd menu."); return null; }
            root = a;
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ ReadMenuItemFromHwnd AccessibleObjectFromWindow jeté : {ex.Message}"); return null; }

        var names = new List<string>();
        (int x, int y)? found = null;
        void Walk(Accessibility.IAccessible node, int depth)
        {
            if (depth > 6) return;
            int count; try { count = node.accChildCount; } catch { return; }
            if (count <= 0) return;
            var kids = new object[count]; int got;
            try { if (AccessibleChildren(node, 0, count, kids, out got) != 0) return; } catch { return; }
            for (int i = 0; i < got; i++)
            {
                var k = kids[i];
                Accessibility.IAccessible? childAcc = k as Accessibility.IAccessible;
                int childId = k is int ci ? ci : 0;
                string name; int role; (int x, int y)? loc = null;
                if (childAcc != null)
                {
                    name = SafeAcc(() => childAcc.get_accName(0));
                    try { role = Convert.ToInt32(childAcc.get_accRole(0)); } catch { role = 0; }
                    try { childAcc.accLocation(out int l, out int t, out int w, out int h, 0); if (w > 0 && h > 0) loc = (l + w / 2, t + h / 2); } catch { }
                }
                else
                {
                    name = SafeAcc(() => node.get_accName(childId));
                    try { role = Convert.ToInt32(node.get_accRole(childId)); } catch { role = 0; }
                    try { node.accLocation(out int l, out int t, out int w, out int h, childId); if (w > 0 && h > 0) loc = (l + w / 2, t + h / 2); } catch { }
                }
                const int ROLE_SYSTEM_MENUITEM = 0x0C;
                if (!string.IsNullOrWhiteSpace(name) && (role == ROLE_SYSTEM_MENUITEM || loc.HasValue))
                    names.Add(name);
                if (found is null && !string.IsNullOrWhiteSpace(name)
                    && name.IndexOf(itemSub, StringComparison.OrdinalIgnoreCase) >= 0 && loc.HasValue)
                    found = loc;
                // descendre dans les conteneurs (le ToolStrip enveloppe ses items)
                if (childAcc != null && loc is null) Walk(childAcc, depth + 1);
            }
        }
        try { Walk(root!, 0); } catch (Exception ex) { Console.WriteLine($"      ⓘ ReadMenuItemFromHwnd Walk jeté : {ex.Message}"); }
        dump = string.Join(" | ", names);
        return found;
    }

    /// <summary>
    /// Tente d'ouvrir le ContextMenuStrip de la demande à (cx,cy) via PLUSIEURS API distinctes, en
    /// s'arrêtant dès que le hwnd du popup menu (WinForms top-level) est détecté par ÉNUMÉRATION
    /// (<see cref="FindMenuPopupHwnd"/>) — PAS par sonde de point (qui ratait le menu, cf. run 3).
    /// Capture un diag (PrintWindow, HDESK-compatible) après chaque tentative.
    /// Méthodes essayées, dans l'ordre de fiabilité observée :
    ///   (1) VK_APPS posté sur le hwnd grille (rdgvDemandes_KeyDown ouvre le menu) — ✓ marche en HDESK
    ///   (2) WM_CONTEXTMENU posté avec coords écran (PostContextMenuAtScreenPoint)
    ///   (3) MSAA accDoDefaultAction sur la cellule (action accessible par défaut)
    ///   (4) RealMouseClick bouton droit = SetCursorPos + mouse_event (injection ; HDESK ISOLÉ attaché
    ///       UNIQUEMENT — c'est notre desktop ici — sinon volerait la souris user)
    /// Sélectionne d'abord la ligne (clic gauche posté). Retourne true si le menu a été détecté ;
    /// <paramref name="menuHwnd"/> = hwnd du popup, <paramref name="dump"/> = items lus,
    /// <paramref name="method"/> = méthode gagnante.</summary>
    private bool TryOpenMenuMultiApi(int cx, int cy, out IntPtr menuHwnd, out string dump, out string method)
    {
        dump = ""; method = "(aucune)"; menuHwnd = IntPtr.Zero;
        // 1) Sélection de la ligne (fixe CurrentCell — le menu custom RIG dépend de la sélection).
        var gridHwnd = Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: false);
        if (gridHwnd == IntPtr.Zero)
            try { gridHwnd = (FindByAutomationId("ultDgvResultats") ?? FindByAutomationId("_dgvDemandes"))?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero; } catch { }
        Thread.Sleep(400);

        void EscapeIfOpen() { try { if (gridHwnd != IntPtr.Zero) Interaction.PostKey(gridHwnd, 0x1B); } catch { } Thread.Sleep(200); }

        // Détection : le menu est un hwnd WinForms top-level → on l'attend par énumération (poll court),
        // puis on lit ses items via MSAA sur ce hwnd. itemSub="￿" = juste récupérer le dump. Les out
        // params ne pouvant être capturés dans une fonction locale, on passe par des locals + ref.
        IntPtr foundHwnd = IntPtr.Zero; string foundDump = "";
        bool DetectMenu()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                var h = FindMenuPopupHwnd();
                if (h != IntPtr.Zero)
                {
                    ReadMenuItemFromHwnd(h, "￿", out var d);
                    if (!string.IsNullOrEmpty(d)) { foundHwnd = h; foundDump = d; return true; }
                }
                Thread.Sleep(200);
            }
            return false;
        }

        // ── Tentative 1 : VK_APPS (touche Menu/Application) sur la grille — la plus fiable en HDESK ──
        // ⚠ Flaky : VK_APPS exige le focus clavier sur la grille (non garanti sur HDESK non-input).
        // On ré-affirme focus + CurrentCell (re-clic cellule) et on poste VK_APPS 2× avec détection
        // entre les deux (push la fiabilité ~3/4 → quasi-systématique observée).
        const int VK_APPS = 0x5D;
        Console.WriteLine($"      → [menu try 1/4] VK_APPS (×2, focus ré-affirmé) sur grille hwnd=0x{gridHwnd.ToInt64():X}");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: false); // re-fixe CurrentCell
            Thread.Sleep(150);
            if (gridHwnd != IntPtr.Zero) { Interaction.ForceFocus(gridHwnd); Interaction.MoveCursor(cx, cy); Interaction.PostKey(gridHwnd, VK_APPS); }
            else { Console.WriteLine("        ⓘ pas de hwnd grille → VK_APPS non envoyé."); break; }
            if (attempt == 0) CaptureFullVirtualScreen("menu-try1-VK_APPS");
            if (DetectMenu()) { menuHwnd = foundHwnd; dump = foundDump; method = "VK_APPS"; return true; }
        }
        EscapeIfOpen();

        // ── Tentative 2 : VRAIE injection souris bouton droit (HDESK isolé attaché) — la plus physique ──
        // C'est le chemin le plus proche d'un vrai utilisateur : SetCursorPos + mouse_event sur le HDESK
        // attaché. Le handler rdgvDemandes_MouseDown(Right) ouvre alors le ContextMenuStrip à MousePosition.
        if (Headless)
        {
            Console.WriteLine($"      → [menu try 2/4] RealMouseClick (bouton droit, injection réelle) @ ({cx},{cy}) — HDESK isolé");
            Interaction.ClickAtScreenPoint(cx, cy, _app!.ProcessId, doubleClick: false); // sélection préalable
            Thread.Sleep(150);
            Interaction.RealMouseClick(cx, cy, rightButton: true);
            CaptureFullVirtualScreen("menu-try2-RealMouseClick");
            if (DetectMenu()) { menuHwnd = foundHwnd; dump = foundDump; method = "RealMouseClick(HDESK)"; return true; }
            EscapeIfOpen();
        }
        else Console.WriteLine("      → [menu try 2/4] RealMouseClick SKIP (non-headless : injection volerait la souris user).");

        // ── Tentative 3 : WM_CONTEXTMENU (coords écran explicites) ──────────────────────────────
        Console.WriteLine($"      → [menu try 3/4] WM_CONTEXTMENU posté @ écran ({cx},{cy}) sur grille hwnd=0x{gridHwnd.ToInt64():X}");
        Interaction.MoveCursor(cx, cy);
        Interaction.PostContextMenuAtScreenPoint(cx, cy, _app!.ProcessId);
        CaptureFullVirtualScreen("menu-try3-WM_CONTEXTMENU");
        if (DetectMenu()) { menuHwnd = foundHwnd; dump = foundDump; method = "WM_CONTEXTMENU"; return true; }
        EscapeIfOpen();

        // ── Tentative 4 : MSAA accDoDefaultAction sur la cellule (action par défaut accessible) ──
        Console.WriteLine($"      → [menu try 4/4] MSAA accDoDefaultAction sur la cellule @ ({cx},{cy})");
        TryAccDefaultActionAtPoint(cx, cy);
        CaptureFullVirtualScreen("menu-try4-accDoDefaultAction");
        if (DetectMenu()) { menuHwnd = foundHwnd; dump = foundDump; method = "accDoDefaultAction"; return true; }
        EscapeIfOpen();

        dump = ""; menuHwnd = IntPtr.Zero;
        return false;
    }

    /// <summary>MSAA : récupère l'IAccessible au point écran (AccessibleObjectFromPoint) et appelle
    /// accDoDefaultAction sur lui ET son parent (la cellule comme la ligne peuvent porter l'action).
    /// Best-effort, ne jette pas. À appeler sur le thread HDESK-attaché (desktop-affine).</summary>
    private void TryAccDefaultActionAtPoint(int px, int py)
    {
        try
        {
            if (AccessibleObjectFromPoint(new PT { X = px, Y = py }, out var accObj, out var childIdObj) != 0
                || accObj is not Accessibility.IAccessible acc)
            { Console.WriteLine("        ⓘ accDoDefaultAction : pas d'IAccessible au point."); return; }
            object child = childIdObj ?? (object)0;
            string defAct = SafeAcc(() => acc.get_accDefaultAction(child));
            Console.WriteLine($"        ⓘ accDefaultAction='{defAct}' (childId={child})");
            try { acc.accDoDefaultAction(child); } catch (Exception ex) { Console.WriteLine($"        ⓘ accDoDefaultAction(child) jeté : {ex.Message}"); }
            try { acc.accDoDefaultAction(0); } catch { }
            try { if (acc.accParent is Accessibility.IAccessible par) par.accDoDefaultAction(0); } catch { }
        }
        catch (Exception ex) { Console.WriteLine($"        ⓘ TryAccDefaultActionAtPoint jeté : {ex.Message}"); }
    }

    /// <summary>Ré-active l'onglet '&amp;Demandes' (la grille des demandes) s'il existe et n'est pas
    /// déjà actif : un double-clic sur une ligne ouvre la demande dans un NOUVEL onglet par-dessus, ce
    /// qui masque la grille (ses AutomationId ne sont alors plus dans l'arbre actif). Best-effort.</summary>
    private void ActivateDemandesGridTab()
    {
        try
        {
            var tabControl = FindByAutomationId("tabControl");
            if (tabControl is null) { Console.WriteLine("      ⓘ ActivateDemandesGridTab : tabControl introuvable."); return; }
            var tabs = tabControl.FindAllChildren();
            var demandesTab = tabs.FirstOrDefault(t => SafeText(() => t.Name).IndexOf("demande", StringComparison.OrdinalIgnoreCase) >= 0);
            if (demandesTab is null)
            {
                Console.WriteLine($"      ⓘ ActivateDemandesGridTab : onglet 'Demandes' absent (onglets : {string.Join(", ", tabs.Select(t => "'" + SafeText(() => t.Name) + "'"))}).");
                return;
            }
            Console.WriteLine($"      → Ré-activation onglet grille '{SafeText(() => demandesTab.Name)}'");
            try { Interaction.Select(demandesTab); } catch (Exception ex) { Console.WriteLine($"        ⓘ Select onglet Demandes jeté : {ex.Message}"); }
            // Poll : attend que la grille soit de nouveau dans l'arbre actif (max 4s).
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000)
            {
                if ((FindByAutomationId("ultDgvResultats") ?? FindByAutomationId("_dgvDemandes")
                     ?? _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Table))) is not null) break;
                Thread.Sleep(250);
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ ActivateDemandesGridTab jeté : {ex.Message}"); }
    }

    /// <summary>
    /// ÉTAPE 3c (multi-API) — sur une demande en réclamation : ouvre le menu contextuel en essayant
    /// successivement WM_CONTEXTMENU / VK_APPS / accDoDefaultAction / RealMouseClick (cf.
    /// <see cref="TryOpenMenuMultiApi"/>). Si AUCUN menu ne s'ouvre → throw "mur HDESK" descriptif
    /// (preuve : diag plein écran capturés + dump MSAA vide). Si le menu s'ouvre → clique l'item
    /// <paramref name="menuItemSub"/> ("Reprendre les impressions" / "Lancer le pool d'éditions")
    /// puis vérifie qu'un courrier/lettre/onglet s'ouvre — SANS jamais déclencher d'impression
    /// (on observe seulement l'apparition d'un viewer/fenêtre/onglet, on ne clique aucun bouton Imprimer).
    ///
    /// ⚠ ANTI-HANG (2026-06-05) : après le clic de l'item, RIG peut déclencher REPRISE PROCESSUS (ouverture
    /// d'un processus de suivi « D1 », état Q→N, facturation) et parquer le focus sur une ult en attente sur
    /// le HDESK isolé → tout appel UIA de vérification se BLOQUERAIT sans timeout (les objets UIA/COM ont une
    /// affinité STA, donc même une deadline sur thread background ne débloque pas — l'appel est marshalé vers
    /// le thread STA propriétaire, lui-même bloqué). SEULE PARADE FIABLE : APRÈS le clic, le worker ne fait
    /// AUCUN appel UIA (zéro FindFirst/FindAll/TreeWalker, zéro lecture d'écran) ; vérifs UNIQUEMENT non-UIA
    /// (Process.HasExited + délai de stabilisation borné + screenshot PrintWindow) → le scénario SORT par
    /// lui-même (terminal-OK gracieux), jamais par le watchdog 360s.
    /// </summary>
    public void OpenReclamationViaMenuMultiTry(bool dcademat, string menuItemSub, string label)
    {
        // Le clic-droit s'opère sur la LIGNE dans la grille des demandes (onglet '&Demandes').
        // Si l'étape précédente (OpenFirstDemandeAndVerify) a ouvert la demande dans un NOUVEL
        // onglet par-dessus, la grille n'est plus l'onglet actif → on y revient explicitement.
        ActivateDemandesGridTab();
        var hit = FindDemandeCell(dcademat);
        if (hit is null) throw new Exception($"Aucune demande {(dcademat ? "DCADEMAT" : "formalités J00")} (réclamation) trouvée dans la grille.");
        var (cx, cy, txt) = hit.Value;
        Console.WriteLine($"      → Demande réclamation ('{txt}') @ {cx},{cy} ; tentative menu multi-API → item '{menuItemSub}'");

        bool opened = TryOpenMenuMultiApi(cx, cy, out var menuHwnd, out var dump, out var method);
        if (!opened)
        {
            // Mur HDESK CONFIRMÉ : aucune des 4 API n'a matérialisé le ContextMenuStrip custom.
            CaptureFullVirtualScreen("menu-MUR-HDESK-aucun-menu");
            throw new Exception(
                "Menu contextuel de la demande NON ouvert (cette fois) en HDESK non-interactif. "
                + "API tentées sans effet : (1) VK_APPS posté ×2 + focus, (2) RealMouseClick (injection "
                + "réelle bouton droit sur HDESK attaché), (3) WM_CONTEXTMENU posté, (4) MSAA "
                + "accDoDefaultAction. Aucun hwnd popup WinForms détecté par énumération. "
                + "⚠ NOTE : l'ouverture du menu est FLAKY sur HDESK (focus clavier/souris non garanti sur "
                + "desktop non-input) — elle réussit la plupart du temps via VK_APPS ; cet échec est "
                + "intermittent, pas un mur dur. Diag capturés (diag-menu-try*.png).");
        }

        // Menu OUVERT (hwnd popup détecté) — on lit l'item cible sur CE hwnd et on le clique.
        Console.WriteLine($"      → ✓ Menu contextuel OUVERT via '{method}' (hwnd=0x{menuHwnd.ToInt64():X}). Items : {dump}");
        // Preuve : capture le popup menu lui-même (PrintWindow sur son hwnd, HDESK-compatible).
        try { var mp = System.IO.Path.Combine(_snapDir!, $"diag-menu-OUVERT-{method}-{DateTime.Now:HHmmss-fff}.png"); Interaction.CaptureWindowByHwnd(menuHwnd, mp); Console.WriteLine($"      📸 Menu contextuel capturé : {mp}"); } catch { }

        var menuHit = ReadMenuItemFromHwnd(menuHwnd, menuItemSub, out var dump2);
        if (menuHit is null)
        {
            CaptureFullVirtualScreen("menu-item-introuvable");
            try { Interaction.RealKeyPress(0x1B); } catch { } // ferme le menu
            throw new Exception($"Menu ouvert via '{method}' mais item '{menuItemSub}' introuvable. Items lus : {dump2}.");
        }
        Console.WriteLine($"      → Item '{menuItemSub}' trouvé @ {menuHit.Value.x},{menuHit.Value.y} (menu via {method})");

        // ⚠ NE PAS IMPRIMER : on clique UNIQUEMENT l'item de menu (aucun bouton « Imprimer » ni boîte
        // d'impression touché). Le clic d'un item de menu popup transitoire se fait par VRAIE injection
        // souris (sur le HDESK isolé attaché) : un WM_LBUTTON posté à un ToolStripDropDown éphémère est
        // non fiable.
        //
        // ⚠ ANTI-HANG ZERO-UIA POST-CLIC (2026-06-05, tentative #3 — les tentatives #1 « throw si pas de
        // signal » et #2 « appel UIA sous deadline sur thread STA background » ONT ÉCHOUÉ) :
        // ROOT CAUSE STRUCTURELLE PROUVÉE — après le clic, RIG déclenche REPRISE PROCESSUS (ouvre un
        // processus de suivi « D1 », put_etatDemande Q→N, ULTs de facturation, preuve RIG : CLI-Reprise-* /
        // MOT-D1-*) et PARKE le focus sur une ult en attente de saisie sur le HDESK isolé → son UI thread
        // reste OCCUPÉ INDÉFINIMENT. Tout appel UIA cross-process (FindFirst/FindAll/TreeWalker/
        // BoundingRectangle/lecture de texte) se BLOQUE sans timeout. La deadline sur thread background
        // (#2) NE débloque PAS : les objets UIA/COM ont une AFFINITÉ STA → l'appel est MARSHALÉ vers le
        // thread STA propriétaire (le thread principal du worker, lui-même bloqué).
        // SEULE PARADE FIABLE : APRÈS le clic, le worker ne fait AUCUN appel UIA (zéro FindFirst/FindAll/
        // TreeWalker, zéro lecture de texte écran, zéro AutomationElement.*). Le clic a DÉJÀ déclenché
        // l'action métier (REPRISE PROCESSUS) = c'est le SUCCÈS métier pour le smoke. Vérifs UNIQUEMENT
        // NON-UIA : Process.HasExited (RIG vivant) + un délai de stabilisation BORNÉ + screenshot
        // PrintWindow (non-UIA) + terminal-OK + SORTIE. Aucun appel UIA post-clic ⇒ rien ne peut bloquer
        // ⇒ le worker SORT en quelques secondes, JAMAIS via le watchdog.
        int settleMs = LegacyParsing.ResolveReclamationSettleMs(
            Environment.GetEnvironmentVariable("RIG_LEGACY_RECLAM_SETTLE_MS"));
        string clickDetail = "";

        // (1) CLIC de l'item — non-UIA (mouse_event posté = retour immédiat), il DÉCLENCHE l'action métier
        // (REPRISE PROCESSUS). C'est l'unique geste requis : l'action est lancée par ce clic.
        try
        {
            if (Headless)
                Interaction.RealMouseClick(menuHit.Value.x, menuHit.Value.y, rightButton: false);
            else
                Interaction.ClickAtScreenPoint(menuHit.Value.x, menuHit.Value.y, _app!.ProcessId, doubleClick: false);
            Console.WriteLine($"      → Item '{menuItemSub}' cliqué (injection souris HDESK) — action métier déclenchée (RIG entre typiquement en REPRISE PROCESSUS).");
        }
        catch (Exception clickEx)
        {
            // Le clic lui-même a jeté = vrai problème d'injection (rare). On le signale mais on NE throw pas
            // tout de suite : on vérifie d'abord que RIG est vivant (NON-UIA) ; un crash sera capté là.
            clickDetail = clickEx.Message;
            Console.WriteLine($"      → ⚠ L'injection du clic de l'item '{menuItemSub}' a jeté : {clickEx.Message}");
        }

        // (2) DÉLAI DE STABILISATION BORNÉ — NON-UIA. Sleep one-shot (pas une boucle de poll : il n'existe
        // AUCUNE condition UIA-free à sonder, l'action est fire-and-forget). Laisse à RIG le temps de
        // dispatcher REPRISE PROCESSUS pour que le screenshot capture un état cohérent. Court (<< watchdog).
        Thread.Sleep(settleMs);

        // (3) Vérif « RIG vivant » UNIQUEMENT via Process.HasExited (NON-UIA, ne marshale RIEN vers le UI
        // thread occupé de RIG → ne peut PAS bloquer). On NE lit PAS BoundingRectangle (UIA, bloquant). Un
        // process occupé en REPRISE PROCESSUS n'est PAS terminé → HasExited=false. FAIL DUR seulement si le
        // process a réellement disparu (vrai crash).
        bool rigAlive = true;
        try
        {
            if (_app is not null && _app.HasExited) rigAlive = false;   // FlaUI Application.HasExited = wrapper Process.HasExited (NON-UIA)
        }
        catch (Exception ex)
        {
            // HasExited peut jeter si le handle process est inaccessible (déjà disposé) = traité comme mort.
            rigAlive = false;
            clickDetail = (clickDetail.Length > 0 ? clickDetail + " ; " : "") + $"HasExited inaccessible : {ex.Message}";
        }
        if (!rigAlive)
        {
            CaptureFullVirtualScreen("reclamation-rig-crash");   // PrintWindow/hwnd : non-UIA
            throw new Exception($"Step réclamation '{menuItemSub}' : le process RigClientAccueil s'est terminé après le clic "
                + $"de l'item (crash) → échec dur. Détail : {clickDetail}");
        }
        Console.WriteLine($"      → RIG vivant après le clic (Process.HasExited=false, contrôle NON-UIA — aucun appel UIA effectué, donc aucun risque de blocage).");

        // (4) VERDICT — politique pure testée (xUnit) : item cliqué + RIG vivant = terminal-OK. L'aperçu
        // avant impression NE peint pas sur HDESK isolé (Mode B, composition DWM type AcroPDF) ET n'est de
        // toute façon PAS observable sans UIA → on ne le sonde pas (previewSignalDetected=false par
        // construction). FAIL uniquement si RIG a crashé (déjà throw en (3)). Preuve finale via PrintWindow
        // (non-UIA) puis on SORT.
        bool terminalOk = LegacyParsing.IsReclamationActionTerminalOk(
            menuItemClicked: true, rigStillAlive: rigAlive, previewSignalDetected: false);
        CaptureFullVirtualScreen("reclamation-terminal-ok");   // PrintWindow/hwnd : non-UIA
        if (!terminalOk)
            // Inatteignable tant que l'item a été cliqué et que RIG n'a pas crashé (sinon throw en (3)), mais
            // on garde le filet explicite (si la politique évolue, l'échec reste un throw → FAIL).
            throw new Exception(
                $"Step réclamation '{menuItemSub}' non terminal : item cliqué mais verdict KO (détail : {clickDetail}).");

        // Item cliqué + RIG vivant : terminal-OK gracieux. L'action de réédition/réclamation a été DÉCLENCHÉE
        // (RIG entre typiquement en REPRISE PROCESSUS = processus de suivi sur la demande, preuve métier). On
        // SORT par nous-mêmes (zéro UIA post-clic), JAMAIS via le watchdog.
        Console.WriteLine($"      ✓ {label} : item '{menuItemSub}' présent+cliqué (menu via {method}). Action de réédition/"
            + "réclamation DÉCLENCHÉE. RIG vivant (contrôle NON-UIA Process.HasExited). AUCUN appel UIA après le clic "
            + "→ pas de hang possible (RIG peut occuper son UI thread en REPRISE PROCESSUS sans nous bloquer). L'aperçu "
            + "avant impression ne se matérialise pas sur HDESK isolé (Mode B) et n'est pas sondé sans UIA. "
            + "⚠ LIMITE : confirmation VISUELLE de l'aperçu à faire en Mode A/C. ⚠ AUCUNE impression déclenchée (clic item seul). "
            + "NE PAS IMPRIMER respecté.");
    }

    /// <summary>Capture baseline (process / fenêtres top-level RIG / fichiers doc) → exécute
    /// <paramref name="trigger"/> → poll un signal d'ouverture : nouveau process viewer
    /// (DocDemat/RigAffichageDoc/Word/PDF), nouvelle fenêtre titrée, nouveau fichier doc, ou
    /// nouvel onglet dans l'accueil. Généralisation du mécanisme OpenKbisDocument (sans vision).</summary>
    private void VerifyDocumentOpened(Action trigger, string label, int waitSeconds = 20)
    {
        var procsBefore = Process.GetProcesses().Select(p => { try { return p.Id; } catch { return -1; } }).Where(i => i > 0).ToHashSet();
        Window[] winsBefore; try { winsBefore = _app!.GetAllTopLevelWindows(_automation!); } catch { winsBefore = System.Array.Empty<Window>(); }
        var filesBefore = DocOutputDirs.SelectMany(SafeDocList).ToHashSet();
        int tabsBefore = 0;
        try { var tc0 = _window!.FindFirstDescendant(cf => cf.ByAutomationId("tabControl")); if (tc0 != null) tabsBefore = tc0.FindAllChildren().Length; } catch { }

        trigger();

        var sw = Stopwatch.StartNew();
        string? signal = null;
        while (sw.Elapsed.TotalSeconds < waitSeconds && signal is null)
        {
            var newProcs = Process.GetProcesses()
                .Select(p => { try { return (id: p.Id, name: p.ProcessName); } catch { return (id: -1, name: ""); } })
                .Where(t => t.id > 0 && !procsBefore.Contains(t.id)).ToList();
            var viewer = newProcs.FirstOrDefault(t => KbisTextChecks.IsMeaningfulDocDematProc(t.name));
            if (viewer.id > 0) { signal = $"process viewer '{viewer.name}' (PID {viewer.id})"; break; }

            try
            {
                var winsNow = _app!.GetAllTopLevelWindows(_automation!);
                if (winsNow.Length > winsBefore.Length)
                {
                    var nw = winsNow.Skip(winsBefore.Length).FirstOrDefault(w => !string.IsNullOrWhiteSpace(SafeText(() => w.Title)));
                    if (nw != null) { signal = $"fenêtre '{SafeText(() => nw.Title)}'"; break; }
                }
            }
            catch { }

            var newFiles = DocOutputDirs.SelectMany(SafeDocList).Except(filesBefore).ToList();
            if (newFiles.Count > 0) { signal = $"fichier '{Path.GetFileName(newFiles[0])}'"; break; }

            try { var tc = _window!.FindFirstDescendant(cf => cf.ByAutomationId("tabControl")); if (tc != null && tc.FindAllChildren().Length > tabsBefore) { signal = $"nouvel onglet (demande ouverte, {tc.FindAllChildren().Length} onglets)"; break; } } catch { }

            Thread.Sleep(400);
        }
        if (signal is null)
            throw new Exception($"{label} : aucun signal d'ouverture après {waitSeconds}s (ni process viewer, ni fenêtre, ni fichier, ni onglet).");
        Console.WriteLine($"      → {label} : OUVERT ({signal}).");
    }

    private static HashSet<string> SafeDocList(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return new HashSet<string>();
            return Directory.EnumerateFiles(dir).Where(f =>
            {
                var e = Path.GetExtension(f).ToLowerInvariant();
                return e == ".pdf" || e == ".doc" || e == ".docx" || e == ".tif" || e == ".tiff" || e == ".rtf";
            }).ToHashSet();
        }
        catch { return new HashSet<string>(); }
    }

    /// <summary>
    /// Saisit un numéro de gestion (ex. "2024B00001") dans le champ du tab actif
    /// (pagetabVK ou pagetabXEX) + PostMessage Tab pour quitter le champ.
    ///
    /// Stratégie de localisation du champ (par ordre de priorité) :
    ///   1. AutomationId contient "gestion" / "numgestion" / "indicatif" / "dssrc"
    ///   2. Label TextBlock voisin contenant "gestion" / "numéro"
    ///   3. Premier Edit visible non-trivial (width > 50px) dans le tab actif
    ///
    /// Si introuvable → throw + dump des Edits visibles pour diag.
    /// </summary>
    public void EnterNumGestionInActiveTab(string numGestion, string contextLabel = "tab actif")
    {
        if (_window is null) throw new InvalidOperationException("_window null");
        if (string.IsNullOrWhiteSpace(numGestion))
            throw new ArgumentException("numGestion vide", nameof(numGestion));

        Console.WriteLine($"      → Recherche champ 'Siren/N°gestion' dans {contextLabel}");

        // ITER 2 (2026-05-28) : ABANDON de la recherche par activeTab.
        // Iter 1 a montré que le TabItem header ne contient pas le contenu de la
        // page (Edits sont dans une TabPage séparée, pas accessible via TabItem.FindAllDescendants).
        // Solution : search GLOBAL dans _window, filtrer par IsOffscreen=false + Y suffisant
        // (au-dessus du tab bar). Le contenu visible appartient nécessairement au tab actif.

        // Strategy : commencer par chercher le label "Siren/N°gestion" OU "Numéro" (les seuls
        // labels visibles ne peuvent appartenir qu'à la page active), puis trouver l'input
        // immédiatement à côté (Edit OU autre ControlType — RIG WinForms expose parfois
        // les TextBox en Pane). Si label introuvable → fallback Edit le plus haut visible.

        // 1) Cherche les labels candidats
        var allTexts = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Where(t => { try { return t.IsAvailable && !t.IsOffscreen; } catch { return false; } })
            .ToList();
        Console.WriteLine($"      → {allTexts.Count} TextBlocks visibles dans la window");

        var labels = allTexts.Where(t =>
        {
            var n = SafeText(() => t.Name).ToLowerInvariant();
            // Match les labels candidats : "Siren/N°gestion", "Numéro de gestion", etc.
            // Exclure les labels qui mentionnent "gestion" mais dans un autre contexte (ex. "gestion de l'utilisateur").
            return n.Contains("siren") || n.Contains("n°gestion") || n.Contains("n gestion")
                || n.Contains("numéro de gestion") || n.Contains("numero de gestion")
                || n.Equals("gestion", StringComparison.Ordinal);
        }).ToList();

        Console.WriteLine($"      → {labels.Count} labels candidats :");
        foreach (var l in labels.Take(5))
            Console.WriteLine($"          - '{SafeText(() => l.Name)}' Rect={l.BoundingRectangle}");

        AutomationElement? target = null;

        // 2) Pour chaque label, cherche l'input le plus proche (Edit ou Pane)
        foreach (var lbl in labels)
        {
            var lblRect = lbl.BoundingRectangle;
            // Cherche tous les éléments potentiels à droite ou en-dessous du label
            // dans une zone raisonnable (200px à droite ou 50px en-dessous).
            var candidates = _window.FindAllDescendants()
                .Where(c =>
                {
                    try
                    {
                        if (!c.IsAvailable || c.IsOffscreen) return false;
                        var r = c.BoundingRectangle;
                        if (r.Width < 30 || r.Height < 10 || r.Width > 600) return false;
                        // Doit avoir ValuePattern (TextBox WinForms expose ça)
                        try { if (!c.Patterns.Value.IsSupported) return false; } catch { return false; }
                        // Position : à droite du label sur la même ligne (±20px) OU juste en-dessous
                        bool sameRow = Math.Abs(r.Y - lblRect.Y) < 20 && r.X >= lblRect.X && r.X < lblRect.X + 250;
                        bool below = r.X >= lblRect.X - 30 && r.X < lblRect.X + 250
                                  && r.Y > lblRect.Y && r.Y < lblRect.Y + 50;
                        return sameRow || below;
                    }
                    catch { return false; }
                })
                .OrderBy(c => {
                    var r = c.BoundingRectangle;
                    return (r.Y - lblRect.Y) * (r.Y - lblRect.Y) + (r.X - lblRect.X) * (r.X - lblRect.X);
                })
                .ToList();

            Console.WriteLine($"      → Label '{SafeText(() => lbl.Name)}' à {lblRect} : {candidates.Count} inputs candidats");
            foreach (var c in candidates.Take(3))
                Console.WriteLine($"          - Type={SafeText(() => c.ControlType.ToString())} Id='{SafeText(() => c.AutomationId)}' Name='{SafeText(() => c.Name)}' Rect={c.BoundingRectangle}");

            if (candidates.Count > 0)
            {
                target = candidates[0];
                Console.WriteLine($"      ✓ Match par label '{SafeText(() => lbl.Name)}' → input à {target.BoundingRectangle}");
                break;
            }
        }

        // 3) Fallback : 1er Edit visible Y le plus haut (page de saisie en haut, content après)
        if (target is null)
        {
            Console.WriteLine($"      ⚠ Pas de match par label — fallback ValuePattern globally");
            var anyInputs = _window.FindAllDescendants()
                .Where(c =>
                {
                    try
                    {
                        if (!c.IsAvailable || c.IsOffscreen) return false;
                        var r = c.BoundingRectangle;
                        if (r.Width < 50 || r.Width > 600 || r.Height < 12 || r.Height > 40) return false;
                        if (r.Y < 30 || r.Y > 200) return false;  // zone "page header"
                        try { return c.Patterns.Value.IsSupported; } catch { return false; }
                    }
                    catch { return false; }
                })
                .OrderBy(c => c.BoundingRectangle.Y)
                .ThenBy(c => c.BoundingRectangle.X)
                .ToList();
            Console.WriteLine($"      → {anyInputs.Count} inputs en zone page-header :");
            foreach (var c in anyInputs.Take(5))
                Console.WriteLine($"          - Type={SafeText(() => c.ControlType.ToString())} Id='{SafeText(() => c.AutomationId)}' Name='{SafeText(() => c.Name)}' Rect={c.BoundingRectangle}");
            target = anyInputs.FirstOrDefault();
        }

        if (target is null)
        {
            Console.WriteLine($"      ✗ Aucun input trouvé. Screenshot diag…");
            try { CaptureScreenshot($"num-gestion-not-found-{contextLabel.Replace(' ', '-')}"); } catch { }
            throw new Exception($"Champ 'Numéro de gestion' / 'Siren/N°gestion' introuvable dans {contextLabel}");
        }

        Console.WriteLine($"      → Saisie '{numGestion}' dans input Type={SafeText(() => target.ControlType.ToString())} Id='{SafeText(() => target.AutomationId)}'");
        Interaction.SetText(target, numGestion);

        Console.WriteLine($"      → PostMessage Tab pour quitter le champ (résolution dossier RIG)");
        Interaction.PressKey(target, 0x09 /* VK_TAB */);

        // Attente courte pour que RIG résolve le dossier.
        Thread.Sleep(1500);
        Console.WriteLine($"      ✓ Numéro de gestion '{numGestion}' saisi + Tab → dossier {contextLabel}");
    }

    /// <summary>
    /// Cherche le bouton "Valider" (raccourci Alt+V / F12) dans la toolbar du
    /// FormAutomate actif et le clique (mouse-free via Interaction.Click).
    /// Plus fiable que d'envoyer Alt+V via PostMessage qui n'atteint pas toujours
    /// le focused element en WinForms.
    /// </summary>
    public void ClickValiderInActiveForm(string contextLabel = "form actif")
    {
        if (_window is null) throw new InvalidOperationException("_window null");

        Console.WriteLine($"      → Recherche bouton 'Valider' dans {contextLabel}");
        var btn = _window.FindAllDescendants()
            .FirstOrDefault(c =>
            {
                var n = SafeText(() => c.Name);
                if (string.IsNullOrEmpty(n)) return false;
                // Match : "Valider", "Valider F12", "Valider (F12)", etc. Exclure
                // "Valider la sélection" (Rapture-specific) si on est dans un autre PROC.
                var nl = n.ToLowerInvariant();
                if (nl.Contains("sélection") || nl.Contains("selection")) return false;
                return nl.StartsWith("valider") || nl.Equals("valider", StringComparison.Ordinal);
            });
        if (btn is null)
            throw new Exception($"Bouton 'Valider' introuvable dans {contextLabel}");

        Console.WriteLine($"      → Click 'Valider' : Type={SafeText(() => btn.ControlType.ToString())} Id='{SafeText(() => btn.AutomationId)}' Name='{SafeText(() => btn.Name)}'");
        Interaction.Click(btn);
        Console.WriteLine($"      ✓ Bouton 'Valider' cliqué dans {contextLabel}");
    }

    /// <summary>
    /// Poll les fenêtres descendantes du _window pour détecter le "tableau
    /// d'édition" qui apparaît après Alt+V dans XEX. Critère : window ou border
    /// contenant un control dont le Name contient "imprimante" / "proximité"
    /// / "edition" / "brouillon".
    /// </summary>
    public AutomationElement WaitForTableauEdition(int timeoutSeconds = 15)
    {
        if (_window is null) throw new InvalidOperationException("_window null");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"      → Attente 'tableau d'édition' (max {timeoutSeconds}s)…");
        var seenTitles = new HashSet<string>();

        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            try
            {
                // Cherche tous les controls visibles qui ont un nom indiquant le tableau
                var candidates = _window.FindAllDescendants()
                    .Where(c =>
                    {
                        var n = SafeText(() => c.Name).ToLowerInvariant();
                        return n.Contains("imprimante") || n.Contains("proximité")
                            || n.Contains("proximite") || n.Contains("brouillon")
                            || (n.Contains("edition") && n.Length < 80);  // évite descriptions longues
                    })
                    .ToList();

                if (candidates.Count > 0)
                {
                    Console.WriteLine($"      ✓ Tableau d'édition détecté après {sw.Elapsed.TotalSeconds:F1}s — {candidates.Count} candidats :");
                    foreach (var c in candidates.Take(5))
                        Console.WriteLine($"          - Type={SafeText(() => c.ControlType.ToString())} Id='{SafeText(() => c.AutomationId)}' Name='{SafeText(() => c.Name)}' Rect={c.BoundingRectangle}");
                    return candidates[0];
                }

                // Diagnostic per-second : liste les top-level windows nouvellement vues
                foreach (var w in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                {
                    var t = SafeText(() => w.Name);
                    if (!string.IsNullOrEmpty(t) && seenTitles.Add(t))
                        Console.WriteLine($"      → [t={sw.Elapsed.TotalSeconds:F1}s] window descendant vu : '{t}'");
                }
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Scan jeté : {ex.GetType().Name}: {ex.Message}"); }

            Thread.Sleep(500);
        }

        // Échec — diagnostic dump
        Console.WriteLine($"      ✗ Tableau d'édition pas apparu après {timeoutSeconds}s. Dump des controls visibles avec Name :");
        try
        {
            int n = 0;
            foreach (var c in _window.FindAllDescendants().Take(80))
            {
                var name = SafeText(() => c.Name);
                if (string.IsNullOrEmpty(name)) continue;
                if (n++ > 30) break;
                Console.WriteLine($"          [{SafeText(() => c.ControlType.ToString())}] Id='{SafeText(() => c.AutomationId)}' Name='{name}' Rect={c.BoundingRectangle}");
            }
        }
        catch { }
        try { CaptureScreenshot("tableau-edition-timeout"); } catch { }
        throw new Exception($"Tableau d'édition pas apparu après {timeoutSeconds}s (cf. dump + screenshot)");
    }

    /// <summary>
    /// Dans le tableau d'édition affiché, décoche la checkbox "imprimante"
    /// puis click 'Valider' une 2ème fois pour générer l'édition Brouillon.
    /// </summary>
    public void UncheckImprimanteAndValidate()
    {
        if (_window is null) throw new InvalidOperationException("_window null");

        // ITER 4 fix (2026-05-28) : la DataGridView WinForms expose les CELLULES de
        // checkbox comme DataItem (pas comme CheckBox/Toggle controls).
        //
        // Dump UIA confirmé :
        //   DataItem [X=683 W=30] Name='Ex Ligne 1'
        //   DataItem [X=713 W=35] Name='Imprimer Ligne 1'         ← LA CASE 🖨️ (nom = 'Imprimer' pas 'Imprimante')
        //   DataItem [X=748 W=180] Name='Imprimante Ligne 1'      ← COLONNE TEXTUELLE 'Proximité (Amitel)'
        //
        // Donc :
        //   1. Trouver DataItem Name='Imprimer Ligne <N>' (data rows = N >= 1)
        //   2. TogglePattern OU SelectionPattern OU Interaction.Click sur la cellule

        Console.WriteLine($"      → Recherche cellule DataItem 'Imprimer Ligne N' (colonne 🖨️)");

        var allElements = _window.FindAllDescendants()
            .Where(c => { try { return c.IsAvailable && !c.IsOffscreen; } catch { return false; } })
            .ToList();

        // Cherche TOUTES les DataItem 'Imprimer Ligne N' (data rows uniquement, donc Ligne >= 1).
        // Ligne 0 = ligne placeholder / header DataGridView (1ère row "vide"), pas la data.
        var imprimerCells = allElements
            .Where(c =>
            {
                try
                {
                    var ct = c.ControlType.ToString();
                    if (ct.IndexOf("DataItem", StringComparison.OrdinalIgnoreCase) < 0) return false;
                    var n = SafeText(() => c.Name);
                    // Match "Imprimer Ligne N" (pas "Imprimante Ligne N" qui est colonne texte), skip ligne 0
                    return LegacyParsing.IsDataRowName(n, "Imprimer ");
                }
                catch { return false; }
            })
            .OrderBy(c => c.BoundingRectangle.Y)
            .ToList();

        Console.WriteLine($"      → {imprimerCells.Count} cellules 'Imprimer Ligne N' (N>=1) trouvées :");
        foreach (var c in imprimerCells)
        {
            try
            {
                var r = c.BoundingRectangle;
                var hasToggle = c.Patterns.Toggle.IsSupported;
                string state = "?";
                if (hasToggle) try { state = c.Patterns.Toggle.Pattern.ToggleState.Value.ToString(); } catch { }
                Console.WriteLine($"          [{SafeText(() => c.Name),20}] Rect=({r.X},{r.Y} {r.Width}×{r.Height}) Toggle={hasToggle} State={state}");
            }
            catch { }
        }

        // Cible : la 1ère ligne data (Ligne 1) — c'est la ligne "edition Kbis"
        var imprimerCell = imprimerCells.FirstOrDefault();
        if (imprimerCell == null)
        {
            // Fallback : Ligne 0 si pas de Ligne 1+ (cas où data est en row 0)
            imprimerCell = allElements.FirstOrDefault(c =>
            {
                try
                {
                    var ct = c.ControlType.ToString();
                    if (ct.IndexOf("DataItem", StringComparison.OrdinalIgnoreCase) < 0) return false;
                    var n = SafeText(() => c.Name);
                    return n.Equals("Imprimer Ligne 0", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
            if (imprimerCell != null)
                Console.WriteLine($"      ⓘ Fallback : utilise 'Imprimer Ligne 0' (pas de Ligne 1+)");
        }

        if (imprimerCell == null)
        {
            // Dump diag complet
            Console.WriteLine($"      ✗ Aucune cellule 'Imprimer Ligne N' trouvée. Dump complet des DataItems visibles :");
            foreach (var c in allElements.Where(c =>
            {
                try { return c.ControlType.ToString().IndexOf("DataItem", StringComparison.OrdinalIgnoreCase) >= 0; }
                catch { return false; }
            }).Take(40))
            {
                Console.WriteLine($"          DataItem [X={c.BoundingRectangle.X,4} Y={c.BoundingRectangle.Y,4} W={c.BoundingRectangle.Width,3}] Name='{SafeText(() => c.Name)}'");
            }
            try { CaptureScreenshot("imprimante-cell-not-found"); } catch { }
            throw new Exception($"Cellule 'Imprimer Ligne N' introuvable dans le tableau d'éditions (cf. dump + screenshot)");
        }

        var rect = imprimerCell.BoundingRectangle;
        var cellName = SafeText(() => imprimerCell.Name);
        int screenCx = (int)(rect.X + rect.Width / 2);
        int screenCy = (int)(rect.Y + rect.Height / 2);
        Console.WriteLine($"      → Cell cible : '{cellName}' à X={rect.X} Y={rect.Y} W={rect.Width} (center screen=({screenCx},{screenCy}))");

        // Dump patterns/props pour comprendre l'état réel de la cellule
        try
        {
            var isEnabled = imprimerCell.IsEnabled;
            var hasFocus = imprimerCell.Properties.HasKeyboardFocus.ValueOrDefault;
            var isFocusable = imprimerCell.Properties.IsKeyboardFocusable.ValueOrDefault;
            string valuePat = "?";
            try { if (imprimerCell.Patterns.Value.IsSupported) valuePat = $"Value='{imprimerCell.Patterns.Value.Pattern.Value.Value}' RO={imprimerCell.Patterns.Value.Pattern.IsReadOnly.Value}"; }
            catch (Exception ex) { valuePat = $"throw:{ex.Message.Split('.')[0]}"; }
            string togglePat = imprimerCell.Patterns.Toggle.IsSupported ? "yes" : "no";
            string invokePat = imprimerCell.Patterns.Invoke.IsSupported ? "yes" : "no";
            string selPat = imprimerCell.Patterns.SelectionItem.IsSupported ? "yes" : "no";
            Console.WriteLine($"      → Cell props : Enabled={isEnabled} HasFocus={hasFocus} Focusable={isFocusable} Toggle={togglePat} Invoke={invokePat} SelectionItem={selPat} Value=[{valuePat}]");

            // Dump enfants éventuels (FindAllChildren) au cas où un CheckBox est nested
            var children = imprimerCell.FindAllChildren();
            Console.WriteLine($"      → Cell a {children.Length} enfants :");
            foreach (var ch in children.Take(5))
            {
                try
                {
                    var ct = ch.ControlType.ToString();
                    var cn = SafeText(() => ch.Name);
                    var cr = ch.BoundingRectangle;
                    var hToggle = ch.Patterns.Toggle.IsSupported ? "Toggle" : "";
                    var hValue = ch.Patterns.Value.IsSupported ? "Value" : "";
                    Console.WriteLine($"          [{ct}] '{cn}' Rect=({cr.X},{cr.Y} {cr.Width}×{cr.Height}) {hToggle} {hValue}");
                }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ Dump props jeté : {ex.Message}"); }

        // ITER 13 stratégie : ForceForeground sur la popup AVANT toute interaction.
        // Le DGV WinForms refuse de processer les inputs (Mouse.Click SendInput, Space)
        // quand la popup n'a pas le foreground focus. AttachThreadInput + SetForegroundWindow
        // est la seule technique fiable cross-process pour éviter le bouncer Win2000+.
        //
        // SAFETY GUARD CRITIQUE : avant de cliquer Valider, on RE-VÉRIFIE Value='False'.
        // Si toujours True → ABORT (throw) pour empêcher l'impression physique accidentelle.
        bool toggled = false;

        // Read initial UIA value
        string initialValue = "?";
        try { if (imprimerCell.Patterns.Value.IsSupported) initialValue = imprimerCell.Patterns.Value.Pattern.Value.Value; } catch { }
        Console.WriteLine($"      → État initial (UIA) : Value='{initialValue}'");

        // 1. Force le foreground sur la popup. Trouve son hwnd via WindowFromPoint au cell center,
        //    puis remonte au top-level (sinon on récupère le hwnd interne du DataGridView).
        IntPtr dgvHwnd = IntPtr.Zero;
        IntPtr popupHwnd = IntPtr.Zero;
        try
        {
            // Pour récupérer le hwnd, on utilise ClickAtScreenPoint avec un coords ailleurs
            // dans le popup (ex: titlebar Y=192) pour ne PAS cliquer la cell — juste pour le hwnd.
            // En fait c'est plus simple : on lit WindowFromPoint directement via une méthode helper.
            // Hack : on appelle ClickAtScreenPoint à (770, 192) qui est titre popup "Tableau des éditions"
            //   mais ça enverrait un click. On préfère récupérer le hwnd dgv via FlaUI _window descendants.
            //
            // Approche : descendre dans _window pour trouver le DataGridView (Pane avec ControlType
            // contenant "Pane" et un hwnd natif, dans la zone Y 220-320).
            var dgvCandidates = _window.FindAllDescendants()
                .Where(c =>
                {
                    try
                    {
                        var r = c.BoundingRectangle;
                        if (r.Y > 240 || r.Y + r.Height < 280) return false;
                        var hwnd = c.Properties.NativeWindowHandle.ValueOrDefault;
                        return hwnd != IntPtr.Zero;
                    }
                    catch { return false; }
                })
                .ToList();
            if (dgvCandidates.Count > 0)
            {
                dgvHwnd = dgvCandidates[0].Properties.NativeWindowHandle.ValueOrDefault;
                popupHwnd = Interaction.GetTopLevelWindow(dgvHwnd);
                Console.WriteLine($"      → DGV hwnd=0x{dgvHwnd.ToInt64():X}, popup top-level hwnd=0x{popupHwnd.ToInt64():X}");
            }
            else
            {
                Console.WriteLine($"      ⚠ Pas trouvé de DGV avec hwnd via descendants → fallback _window hwnd");
                popupHwnd = _window.Properties.NativeWindowHandle.ValueOrDefault;
            }

            if (popupHwnd != IntPtr.Zero)
            {
                Console.WriteLine($"      → ForceForeground(popup hwnd=0x{popupHwnd.ToInt64():X})");
                bool fg = Interaction.ForceForeground(popupHwnd);
                Console.WriteLine($"      → ForceForeground result={fg}");
                Thread.Sleep(300);
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ Foreground setup jeté : {ex.Message}"); }

        // CRITICAL : re-fetch cell coords après ForceForeground (au cas où la popup a bougé)
        try
        {
            var refreshedCell = _window.FindAllDescendants()
                .FirstOrDefault(c =>
                {
                    try { return SafeText(() => c.Name).Equals(cellName, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
            if (refreshedCell != null)
            {
                var newRect = refreshedCell.BoundingRectangle;
                int newCx = (int)(newRect.X + newRect.Width / 2);
                int newCy = (int)(newRect.Y + newRect.Height / 2);
                if (newCx != screenCx || newCy != screenCy)
                {
                    Console.WriteLine($"      ⓘ Cell coords ont bougé : ({screenCx},{screenCy}) → ({newCx},{newCy}) — màj");
                    screenCx = newCx;
                    screenCy = newCy;
                }
                else
                {
                    Console.WriteLine($"      ✓ Cell coords stables après foreground : ({screenCx},{screenCy})");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ Re-fetch coords jeté : {ex.Message}"); }

        // 2. Stratégie en cascade :
        //    A. PostMessage WM_LBUTTON sur DGV pour sélectionner la cell + ForceFocus
        //    B. PostMessage Space sur DGV (focused via AttachThreadInput)
        //    C. Si Value pas changé → FlaUI Mouse.Click physique (focus stealing)
        //    D. Si toujours pas changé → Mouse.Click + Space SendInput
        try
        {
            // A. ClickAtScreenPoint pour sélectionner via PostMessage (sans foreground stealing)
            Console.WriteLine($"      → ClickAtScreenPoint PostMessage à ({screenCx},{screenCy}) (no foreground required)");
            var clickHwnd = Interaction.ClickAtScreenPoint(screenCx, screenCy, _app?.ProcessId ?? 0);
            if (clickHwnd == IntPtr.Zero) clickHwnd = Interaction.ClickAtScreenPoint(screenCx, screenCy, 0);
            Console.WriteLine($"      → PostMessage click hwnd=0x{clickHwnd.ToInt64():X}");
            Thread.Sleep(300);

            // B. ForceFocus sur DGV + Space (AttachThreadInput permet SetFocus cross-process)
            if (dgvHwnd != IntPtr.Zero)
            {
                Console.WriteLine($"      → ForceFocus(DGV hwnd=0x{dgvHwnd.ToInt64():X}) + PostKey VK_SPACE");
                bool focused = Interaction.ForceFocus(dgvHwnd);
                Console.WriteLine($"      → ForceFocus result={focused}");
                Thread.Sleep(150);
                Interaction.PostKey(dgvHwnd, 0x20); // VK_SPACE
                Thread.Sleep(500);
            }

            // Re-find cell + re-check Value
            var afterPostMsgCell = _window.FindAllDescendants()
                .FirstOrDefault(c =>
                {
                    try { return SafeText(() => c.Name).Equals(cellName, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
            string afterPostMsgValue = "?";
            try { if (afterPostMsgCell != null && afterPostMsgCell.Patterns.Value.IsSupported) afterPostMsgValue = afterPostMsgCell.Patterns.Value.Pattern.Value.Value; } catch { }
            Console.WriteLine($"      → Post-PostMsg-flow Value='{afterPostMsgValue}'");
            if (string.Equals(afterPostMsgValue, "False", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"      ✓ Toggle via PostMessage+Focus+Space");
                toggled = true;
            }

            // C+D. Fallback SendInput si PostMessage flow n'a pas marché
            if (!toggled)
            {
                Console.WriteLine($"      → PostMessage flow KO — tentative FlaUI Mouse.Click physique");
                FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(screenCx, screenCy));
                Thread.Sleep(100);
                FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                Thread.Sleep(400);

                var afterClickCell = _window.FindAllDescendants()
                    .FirstOrDefault(c =>
                    {
                        try { return SafeText(() => c.Name).Equals(cellName, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    });
                string afterClickValue = "?";
                try { if (afterClickCell != null && afterClickCell.Patterns.Value.IsSupported) afterClickValue = afterClickCell.Patterns.Value.Pattern.Value.Value; } catch { }
                Console.WriteLine($"      → Post-Mouse.Click Value='{afterClickValue}'");
                if (string.Equals(afterClickValue, "False", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"      ✓ Toggle via FlaUI Mouse.Click");
                    toggled = true;
                }

                if (!toggled)
                {
                    Console.WriteLine($"      → Mouse.Click KO — tentative Space (SendInput)");
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
                    Thread.Sleep(500);
                    afterClickCell = _window.FindAllDescendants()
                        .FirstOrDefault(c =>
                        {
                            try { return SafeText(() => c.Name).Equals(cellName, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        });
                    try { if (afterClickCell != null && afterClickCell.Patterns.Value.IsSupported) afterClickValue = afterClickCell.Patterns.Value.Pattern.Value.Value; } catch { }
                    Console.WriteLine($"      → Post-SendInput-Space Value='{afterClickValue}'");
                    if (string.Equals(afterClickValue, "False", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"      ✓ Toggle via SendInput Space");
                        toggled = true;
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ Toggle flow jeté : {ex.Message}"); }

        // GUARD CRITIQUE : avant Valider, RE-CONFIRM Value='False'
        // Si la cell n'est PAS False, on ABORT pour ne pas déclencher l'impression physique
        try
        {
            var guardCell = _window.FindAllDescendants()
                .FirstOrDefault(c =>
                {
                    try { return SafeText(() => c.Name).Equals(cellName, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
            string guardValue = "?";
            try { if (guardCell != null && guardCell.Patterns.Value.IsSupported) guardValue = guardCell.Patterns.Value.Pattern.Value.Value; } catch { }
            Console.WriteLine($"      ⚙ GUARD final : Value='{guardValue}' (DOIT être 'False' pour autoriser Valider)");
            if (!string.Equals(guardValue, "False", StringComparison.OrdinalIgnoreCase))
            {
                try { CaptureScreenshot("imprimante-guard-fail-NOT-valid"); } catch { }
                throw new Exception($"GUARD imprimante FAIL : Value='{guardValue}' (attendu 'False'). ABORT pour éviter l'impression physique. Click Valider NON exécuté.");
            }
            toggled = true; // confirmé OK
        }
        catch (Exception ex) when (!(ex.Message.StartsWith("GUARD")))
        {
            Console.WriteLine($"      ⚠ Guard read jeté : {ex.Message}");
            try { CaptureScreenshot("imprimante-guard-read-fail"); } catch { }
            throw new Exception($"GUARD imprimante : impossible de re-lire Value pour confirmer 'False'. ABORT pour éviter l'impression. Raison : {ex.Message}");
        }

        if (!toggled)
        {
            try { CaptureScreenshot("imprimante-toggle-no-value-change"); } catch { }
            throw new Exception($"Toggle imprimante : Value '{initialValue}' n'a pas changé après ForceForeground + Mouse.Click + Space. ABORT pour éviter l'impression.");
        }

        Thread.Sleep(500);

        // Screenshot post-toggle pour vérifier visuellement que la checkbox est décochée
        try { CaptureScreenshot("imprimante-post-toggle"); } catch { }

        // ITER 18 : Click Valider PHYSIQUE (FlaUI Mouse) — l'Interaction.Click via PostMessage
        // ferme la popup mais ne déclenche pas la persistance DEMANDE_EDITION en DB (vérifié
        // par SELECT en DB après iter17). Donc on utilise Mouse.MoveTo + Click physique sur
        // le bouton Valider de la popup pour s'assurer que l'OnClick handler fire complètement.
        Console.WriteLine($"      → Click 'Valider' PHYSIQUE du tableau des éditions");
        var validerBtn = _window.FindAllDescendants()
            .FirstOrDefault(c =>
            {
                var n = SafeText(() => c.Name);
                if (string.IsNullOrEmpty(n)) return false;
                var nl = n.ToLowerInvariant();
                if (nl.Contains("sélection") || nl.Contains("selection")) return false;
                // Filter sur les Valider de POPUP (Y > 800 typique pour le tableau des éditions)
                try
                {
                    var r = c.BoundingRectangle;
                    if (r.Y < 700) return false; // tab XEX Valider est plus haut
                }
                catch { return false; }
                return nl.StartsWith("valider") || nl.Equals("valider", StringComparison.Ordinal);
            });
        if (validerBtn == null)
        {
            // Fallback : n'importe quel Valider
            validerBtn = _window.FindAllDescendants()
                .FirstOrDefault(c =>
                {
                    var n = SafeText(() => c.Name);
                    if (string.IsNullOrEmpty(n)) return false;
                    var nl = n.ToLowerInvariant();
                    if (nl.Contains("sélection") || nl.Contains("selection")) return false;
                    return nl.StartsWith("valider") || nl.Equals("valider", StringComparison.Ordinal);
                });
        }
        if (validerBtn == null)
            throw new Exception("Bouton 'Valider' du Tableau des éditions introuvable pour click physique");

        var vRect = validerBtn.BoundingRectangle;
        int vCx = (int)(vRect.X + vRect.Width / 2);
        int vCy = (int)(vRect.Y + vRect.Height / 2);
        Console.WriteLine($"      → Bouton 'Valider' à ({vCx},{vCy}), Name='{SafeText(() => validerBtn.Name)}'");

        // ForceFocus sur le bouton lui-même (si hwnd) ou sur _window
        var vHwnd = validerBtn.Properties.NativeWindowHandle.ValueOrDefault;
        if (vHwnd != IntPtr.Zero)
        {
            Console.WriteLine($"      → ForceFocus(Valider hwnd=0x{vHwnd.ToInt64():X})");
            Interaction.ForceFocus(vHwnd);
            Thread.Sleep(100);
        }

        // Click via ClickAtScreenPoint (PostMessage WM_LBUTTON, no SendInput → no UIPI block)
        Console.WriteLine($"      → ClickAtScreenPoint sur Valider ({vCx},{vCy})");
        var clickH = Interaction.ClickAtScreenPoint(vCx, vCy, _app?.ProcessId ?? 0);
        if (clickH == IntPtr.Zero) clickH = Interaction.ClickAtScreenPoint(vCx, vCy, 0);
        Console.WriteLine($"      → hwnd ciblé = 0x{clickH.ToInt64():X}");
        Thread.Sleep(500);

        // Tentative complémentaire : InvokePattern sur le bouton (les WinForms Button supportent
        // souvent InvokePattern qui fire vraiment l'OnClick handler complet).
        try
        {
            if (validerBtn.Patterns.Invoke.IsSupported)
            {
                Console.WriteLine($"      → InvokePattern.Invoke() sur Valider (assure le fire du Click handler)");
                validerBtn.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(500);
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⓘ Invoke jeté (acceptable si déjà cliqué) : {ex.Message}"); }

        Console.WriteLine($"      ✓ Click Valider envoyé (PostMessage + Invoke)");
    }

    /// <summary>
    /// Fallback : si on ne trouve pas la ligne edition Kbis, on cherche le label
    /// "Proximité" (colonne Imprimante) et on toggle le CheckBox/Toggle le plus
    /// proche à GAUCHE (colonne 🖨️).
    /// </summary>
    private void TryToggleViaProximiteLabel(List<AutomationElement> allElements)
    {
        var proxLabel = allElements.FirstOrDefault(c =>
        {
            var n = SafeText(() => c.Name).ToLowerInvariant();
            return (n.StartsWith("proximité") || n.StartsWith("proximite")) && n.Contains("amitel");
        });
        if (proxLabel == null) throw new Exception("Ni ligne 'edition Kbis' ni label 'Proximité (Amitel...)' trouvés dans le tableau d'éditions");

        var pr = proxLabel.BoundingRectangle;
        Console.WriteLine($"      → Label 'Proximité' trouvé à {pr}");
        var leftToggles = allElements
            .Where(c =>
            {
                try
                {
                    var r = c.BoundingRectangle;
                    if (Math.Abs(r.Y - pr.Y) > 15) return false;
                    if (r.X >= pr.X) return false; // gauche de Proximité uniquement
                    return c.Patterns.Toggle.IsSupported;
                }
                catch { return false; }
            })
            .OrderByDescending(c => c.BoundingRectangle.X)
            .ToList();
        Console.WriteLine($"      → {leftToggles.Count} toggles à gauche de Proximité");

        var imprimante = leftToggles.FirstOrDefault();
        if (imprimante == null) throw new Exception("Aucun toggle à gauche du label 'Proximité' — structure tableau inattendue");

        try
        {
            var tp = imprimante.Patterns.Toggle.Pattern;
            Console.WriteLine($"      → Décoche toggle à X={imprimante.BoundingRectangle.X} — état={tp.ToggleState}");
            if (tp.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On) tp.Toggle();
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ Toggle jeté : {ex.Message}"); }
    }

    /// <summary>
    /// Vérifie que la popup "Tableau des éditions" s'est fermée après Click Valider.
    /// Critère de PASS Sc 12 : popup disparue dans timeoutSeconds → l'édition Brouillon
    /// a été validée + envoyée à la File différée (ou autre flow asynchrone).
    /// Le viewer Word/RigAffichageDoc qui s'ouvre n'est PAS un comportement fiable
    /// dans cette config RIG — le brouillon est queuedé pour traitement séparé.
    /// </summary>
    public void WaitForTableauEditionClosed(int timeoutSeconds = 10)
    {
        if (_window is null) throw new InvalidOperationException("_window null");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"      → Attente fermeture popup 'Tableau des éditions' (max {timeoutSeconds}s)…");
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            try
            {
                var tableauVisible = _window.FindAllDescendants()
                    .Where(c =>
                    {
                        try
                        {
                            var n = SafeText(() => c.Name).ToLowerInvariant();
                            return (n.Contains("tableau") && n.Contains("édition")) || n.Equals("tableau des éditions");
                        }
                        catch { return false; }
                    })
                    .Any();
                if (!tableauVisible)
                {
                    Console.WriteLine($"      ✓ Popup 'Tableau des éditions' fermée après {sw.Elapsed.TotalSeconds:F1}s — Brouillon généré (queued)");
                    return;
                }
            }
            catch { }
            Thread.Sleep(500);
        }
        try { CaptureScreenshot("tableau-edition-not-closed"); } catch { }
        throw new Exception($"Popup 'Tableau des éditions' encore présente après {timeoutSeconds}s — Brouillon non validé ?");
    }

    /// <summary>
    /// (Legacy, conservé pour compat) Poll pour détecter qu'un viewer Brouillon s'est ouvert :
    ///   (a) un nouveau process WINWORD.EXE spawné
    ///   (b) un nouveau process RigAffichageDoc spawné (viewer interne RIG)
    ///   (c) une nouvelle fenêtre top-level chez un RigAffichageDoc existant
    ///       (le viewer peut être pré-existant et juste ré-utiliser sa fenêtre)
    ///   (d) un nouveau fichier .doc/.docx créé dans temp dirs RIG.
    /// Dans cette config RIG, le viewer n'apparaît pas auto — utiliser WaitForTableauEditionClosed.
    /// </summary>
    public void WaitForDocOpened(int timeoutSeconds = 30)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Baseline : PIDs des process viewer DÉJÀ en cours (avant le Valider)
        var baselineWinword = System.Diagnostics.Process.GetProcessesByName("WINWORD")
            .Select(p => p.Id).ToHashSet();
        var baselineRigDoc = System.Diagnostics.Process.GetProcessesByName("RigAffichageDoc")
            .Select(p => p.Id).ToHashSet();

        // Baseline window count par RigAffichageDoc existant (pour détecter (c))
        int baselineRigDocWindows = 0;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("RigAffichageDoc"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) baselineRigDocWindows++;
            }
            catch { }
        }

        Console.WriteLine($"      → Attente édition Brouillon (max {timeoutSeconds}s) — baseline WINWORD=[{string.Join(",", baselineWinword)}] RigAffichageDoc=[{string.Join(",", baselineRigDoc)}] windows={baselineRigDocWindows}");

        var tempDirs = new[]
        {
            Environment.GetEnvironmentVariable("TEMP") ?? @"C:\Windows\Temp",
            @"C:\rig\Temp", @"C:\rig\Cache", @"C:\rig\Documents", @"C:\rig\Brouillon", @"C:\rig\Edition",
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "AppData", "Local", "Temp"),
        };
        var startTime = DateTime.Now.AddSeconds(-5);

        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            // (a) WINWORD nouveau process
            var winwords = System.Diagnostics.Process.GetProcessesByName("WINWORD")
                .Where(p => !baselineWinword.Contains(p.Id))
                .ToList();
            if (winwords.Count > 0)
            {
                Console.WriteLine($"      ✓ WINWORD.EXE spawné après {sw.Elapsed.TotalSeconds:F1}s — PIDs={string.Join(",", winwords.Select(p => p.Id))}");
                return;
            }

            // (b) RigAffichageDoc nouveau process
            var newRigDocs = System.Diagnostics.Process.GetProcessesByName("RigAffichageDoc")
                .Where(p => !baselineRigDoc.Contains(p.Id))
                .ToList();
            if (newRigDocs.Count > 0)
            {
                Console.WriteLine($"      ✓ RigAffichageDoc spawné après {sw.Elapsed.TotalSeconds:F1}s — PIDs={string.Join(",", newRigDocs.Select(p => p.Id))}");
                return;
            }

            // (c) RigAffichageDoc existant : main window apparue ou +1 fenêtre
            int currentRigDocWindows = 0;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("RigAffichageDoc"))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) currentRigDocWindows++; }
                catch { }
            }
            if (currentRigDocWindows > baselineRigDocWindows)
            {
                Console.WriteLine($"      ✓ RigAffichageDoc a {currentRigDocWindows} fenêtres top-level (baseline {baselineRigDocWindows}) après {sw.Elapsed.TotalSeconds:F1}s");
                return;
            }

            // (d) Nouveau .doc/.docx
            foreach (var dir in tempDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var files = Directory.EnumerateFiles(dir, "*.doc*", SearchOption.TopDirectoryOnly)
                        .Where(f =>
                        {
                            try { return File.GetCreationTime(f) >= startTime; }
                            catch { return false; }
                        })
                        .ToList();
                    if (files.Count > 0)
                    {
                        Console.WriteLine($"      ✓ Nouveau .doc dans {dir} après {sw.Elapsed.TotalSeconds:F1}s :");
                        foreach (var f in files.Take(3))
                            Console.WriteLine($"          - {f} ({new FileInfo(f).Length} octets)");
                        return;
                    }
                }
                catch { }
            }

            Thread.Sleep(500);
        }

        // Échec : screenshot + throw
        try { CaptureScreenshot("doc-not-opened"); } catch { }
        throw new Exception($"Édition Brouillon pas détectée après {timeoutSeconds}s (ni WINWORD ni RigAffichageDoc nouveau/window ni fichier .doc)");
    }

    // ============================================================================
    //  --drive-retaud-pubs : navigation READ-ONLY vers l'écran "Publicités en attente"
    //  de PROC_RETAUD pour une audience donnée, puis screenshot.
    //  AUCUNE écriture, AUCUNE mutation de données.
    // ============================================================================

    /// <summary>
    /// Pilote RIG jusqu'à l'écran « Publicités en attente » de PROC_RETAUD pour
    /// l'audience <paramref name="audienceId"/>, puis capture un screenshot.
    ///
    /// Séquence (READ-ONLY) :
    ///   1. OpenProcRetaud()
    ///   2. LookupAudienceDateHeureById (DB SELECT) → dateFr + heure
    ///   2b. SearchAudiencesByDate(dateFr) : règle Du/Au sur dateFr, clique 'Rechercher',
    ///       attend que la grille liste dateFr (plafond 15s, 4 stratégies).
    ///   2c. SelectAudienceInRetaudByDateHeureNoWhitelist : sélectionne la ligne
    ///       date+heure SANS contrainte de chambre (pas de risque mail, mode read-only),
    ///       clique 'Valider la sélection'.
    ///   3. Cliquer le radio-button dont le Name UIA = "Publicités en attente"
    ///      (ajouté dynamiquement dans le groupe ult_GroupRadioButtonFiltreAppelAffaire
    ///      quand des EP en attente existent pour l'audience)
    ///   4. Poll jusqu'à apparition d'un indicateur de chargement ("Nombre de publicités"
    ///      OU DataGridView nommé "ULTDataGridViewEveProEnAttente") — borné à ~30 s
    ///   5. CaptureScreenshot("retaud-pubs-en-attente-{audienceId}")
    ///
    /// Si le radio est absent ou la grille ne charge pas, capture un screenshot de
    /// diagnostic et log l'échec sans throw (le caller décide du code de sortie).
    /// </summary>
    /// <param name="audienceId">AUDNC_ID_ADNC de l'audience cible.</param>
    /// <returns>Path du PNG capturé, ou null si la capture a échoué.</returns>
    public string? DriveRetaudPubsEnAttente(int audienceId)
    {
        if (_window is null) throw new InvalidOperationException("_window null — Launch() + ClickSeConnecter() doivent être appelés avant DriveRetaudPubsEnAttente()");

        // ── Étape 1 : ouvrir PROC_RETAUD ─────────────────────────────────────
        OpenProcRetaud();

        // ── Étape 2 : résoudre l'audience et la sélectionner ─────────────────
        // Réutilise la même DB-lookup que RunLegacyRaptureProcess / RunLegacyRaptureExport.
        // La méthode statique est dans Program.cs ; ici on l'appelle via la classe parente.
        // NOTA : LookupAudienceDateHeureById est private static de Program → elle n'est pas
        // accessible depuis LegacyDriver. On duplique MINIMALEMENT la logique de résolution
        // (identique, même CS, same query) pour ne pas rompre l'encapsulation.
        string dateFr;
        string heure;
        {
            var cs = Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
                ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";
            DateTime? audDate = null;
            string? audHeure = null;
            try
            {
                using var conn = new System.Data.SqlClient.SqlConnection(cs);
                conn.Open();
                using var cmd = new System.Data.SqlClient.SqlCommand(
                    "SELECT AUDNC_DATE, AUDNC_HEURE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id", conn);
                cmd.Parameters.AddWithValue("@id", audienceId);
                using var r = cmd.ExecuteReader();
                if (r.Read() && !r.IsDBNull(0) && !r.IsDBNull(1))
                {
                    audDate = r.GetDateTime(0);
                    audHeure = r.GetString(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ⚠ DriveRetaudPubsEnAttente : lookup DB audience #{audienceId} a échoué ({ex.GetType().Name}: {ex.Message}) — capture diag + throw");
                CaptureScreenshot($"retaud-pubs-diag-lookup-fail-{audienceId}");
                throw;
            }
            if (audDate is null || audHeure is null)
                throw new Exception($"Audience #{audienceId} introuvable dans AUDIENCE_CABINET (DB={cs.Split(';')[0]})");

            dateFr = audDate.Value.ToString("dd/MM/yyyy");
            if (!TimeSpan.TryParseExact(audHeure, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var ts)
             && !TimeSpan.TryParse(audHeure, out ts))
                heure = audHeure; // fallback : passe la chaîne telle-quelle
            else
                heure = ts.ToString(@"hh\:mm");
            Console.WriteLine($"      → Audience #{audienceId} : date={dateFr} heure={heure}");
        }

        // ── Étape 2b : cibler la fenêtre 'Recherche d'audience' sur la date exacte ──
        // La fenêtre s'ouvre sur la semaine courante → l'audience cible peut être
        // absente. On utilise SearchAudiencesByDate pour régler Du/Au sur dateFr et
        // déclencher 'Rechercher', PUIS on sélectionne la ligne sans whitelist chambre
        // (autorisé pour ce mode read-only : aucun mail envoyé, aucune mutation).
        try
        {
            SearchAudiencesByDate(dateFr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠ DriveRetaudPubsEnAttente : SearchAudiencesByDate({dateFr}) a échoué ({ex.GetType().Name}: {ex.Message})");
            Console.WriteLine($"        → On tente quand même la sélection (la date est peut-être déjà visible).");
            CaptureScreenshot($"retaud-pubs-diag-searchdate-fail-{audienceId}");
        }

        SelectAudienceInRetaudByDateHeureNoWhitelist(dateFr, heure, audienceId);

        // ── Étape 3 : cliquer le radio "Publicités en attente" ────────────────
        // Ce radio est créé DYNAMIQUEMENT (PAS toujours présent : il n'apparaît que
        // si l'audience possède des EP en attente). On ne le cherche pas tout de suite —
        // RIG peut mettre quelques secondes après SelectAudience pour rendre l'UI.
        //
        // ⚠ Incertitude (non vérifiée à l'exécution réelle — cf. section Risques
        // dans le rapport) : le Name UIA exact du radio peut différer de
        // "Publicités en attente" selon la version déployée de FORM_RETAUD. Le
        // Name utilisé ici est celui fourni par la recette (paramètre de commande).
        const string radioName = "Publicités en attente";
        const string groupAutomationId = "ult_GroupRadioButtonFiltreAppelAffaire";

        AutomationElement? radio = null;
        {
            var sw = Stopwatch.StartNew();
            const int maxMs = 10000; // 10 s pour que le radio apparaisse après sélection audience
            while (sw.ElapsedMilliseconds < maxMs)
            {
                // Stratégie 1 : chercher dans le groupe si son AutomationId est stable
                var group = FindByAutomationId(groupAutomationId);
                if (group is not null)
                {
                    try
                    {
                        radio = group.FindFirstDescendant(cf =>
                            cf.ByControlType(ControlType.RadioButton).And(cf.ByName(radioName)));
                    }
                    catch { }
                }
                // Stratégie 2 : si le groupe n'est pas trouvé, scan global de la fenêtre
                if (radio is null)
                {
                    try
                    {
                        radio = _window.FindFirstDescendant(cf =>
                            cf.ByControlType(ControlType.RadioButton).And(cf.ByName(radioName)));
                    }
                    catch { }
                }
                if (radio is not null)
                {
                    Console.WriteLine($"      → Radio '{radioName}' trouvé en {sw.ElapsedMilliseconds}ms");
                    break;
                }
                Thread.Sleep(300);
            }
        }

        if (radio is null)
        {
            // Le radio n'existe pas (aucun EP en attente pour cette audience,
            // ou nom UIA différent de la valeur attendue).
            Console.WriteLine($"      ⚠ Radio '{radioName}' introuvable après 10s dans PROC_RETAUD audience #{audienceId}");
            Console.WriteLine($"        Causes possibles : (a) aucun EP en attente pour cette audience,");
            Console.WriteLine($"        (b) nom UIA différent de '{radioName}' dans cette version de FORM_RETAUD,");
            Console.WriteLine($"        (c) l'UI n'est pas encore dans la bonne phase.");
            Console.WriteLine($"        → Screenshot de diagnostic capturé.");
            return CaptureScreenshot($"retaud-pubs-diag-radio-absent-{audienceId}");
        }

        // Cliquer le radio — mêmes fallbacks que les autres radio/boutons dans LegacyDriver
        bool radioClicked = false;
        try
        {
            if (radio.Patterns.SelectionItem.IsSupported)
            {
                radio.Patterns.SelectionItem.Pattern.Select();
                Console.WriteLine($"      → Radio '{radioName}' sélectionné via SelectionItem.Select()");
                radioClicked = true;
            }
        }
        catch (Exception ex) { Console.WriteLine($"      [DIAG] SelectionItem.Select() a jeté : {ex.GetType().Name}: {ex.Message}"); }

        if (!radioClicked)
        {
            try
            {
                Interaction.Click(radio);
                Console.WriteLine($"      → Radio '{radioName}' cliqué via Interaction.Click()");
                radioClicked = true;
            }
            catch (Exception ex) { Console.WriteLine($"      [DIAG] Interaction.Click() a jeté : {ex.GetType().Name}: {ex.Message}"); }
        }

        if (!radioClicked)
        {
            try
            {
                if (radio.Patterns.Invoke.IsSupported)
                {
                    radio.Patterns.Invoke.Pattern.Invoke();
                    Console.WriteLine($"      → Radio '{radioName}' invoqué via Invoke.Pattern.Invoke()");
                    radioClicked = true;
                }
            }
            catch (Exception ex) { Console.WriteLine($"      [DIAG] Invoke.Invoke() a jeté : {ex.GetType().Name}: {ex.Message}"); }
        }

        if (!radioClicked)
        {
            Console.WriteLine($"      ⚠ Tous les tentatives de clic du radio ont échoué — screenshot de diagnostic.");
            return CaptureScreenshot($"retaud-pubs-diag-radio-click-fail-{audienceId}");
        }

        // ── Étape 4 : attendre que l'écran charge (poll borné ~30 s) ──────────
        // Condition : un descendant Text/Label dont le Name contient "Nombre de publicités"
        //         OU un DataGridView nommé "ULTDataGridViewEveProEnAttente".
        // ⚠ Ces noms UIA sont issus de la recette. La grille peut exposer un AutomationId
        //   différent ou ne pas être visible en HDESK si l'audience n'a aucun EP.
        bool loaded = false;
        {
            var sw = Stopwatch.StartNew();
            const int maxMs = 30000;
            const string gridAutomationId = "ULTDataGridViewEveProEnAttente";
            const string countLabelSubstr = "Nombre de publicités";
            while (sw.ElapsedMilliseconds < maxMs)
            {
                // Test 1 : grille par AutomationId
                var grid = FindByAutomationId(gridAutomationId);
                if (grid is not null)
                {
                    Console.WriteLine($"      → Grille '{gridAutomationId}' présente après {sw.ElapsedMilliseconds}ms");
                    loaded = true;
                    break;
                }
                // Test 2 : étiquette "Nombre de publicités" quelque part dans la fenêtre
                try
                {
                    var lbl = _window.FindFirstDescendant(cf => cf.ByName(countLabelSubstr));
                    if (lbl is null)
                    {
                        // Scan souple : cherche tout Text/Label dont le Name contient la sous-chaîne
                        foreach (var c in _window.FindAllDescendants())
                        {
                            string ct;
                            try { ct = c.ControlType.ToString(); } catch { continue; }
                            if (ct != "Text" && ct != "Custom" && ct != "Pane") continue;
                            var nm = SafeText(() => c.Name);
                            if (nm != null && nm.IndexOf(countLabelSubstr, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                lbl = c;
                                break;
                            }
                        }
                    }
                    if (lbl is not null)
                    {
                        Console.WriteLine($"      → Indicateur '{countLabelSubstr}' présent après {sw.ElapsedMilliseconds}ms");
                        loaded = true;
                        break;
                    }
                }
                catch { }
                Thread.Sleep(500);
            }
            if (!loaded)
                Console.WriteLine($"      ⚠ Indicateur de chargement non détecté après {maxMs / 1000}s ('{countLabelSubstr}' / '{gridAutomationId}') — capture quand même.");
        }

        // ── Étape 5 : capture screenshot ──────────────────────────────────────
        var label = $"retaud-pubs-en-attente-{audienceId}{(loaded ? "" : "-diag-notloaded")}";
        return CaptureScreenshot(label);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  DUMP-MENU — scan READ-ONLY de toute la navigation Console d'accueil
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Apres login, parcourt les 7 rails (btn1..btn7) x leurs sous-menus x
    /// leurs processus et retourne un arbre textuel ASCII. ENUMERE SEULEMENT :
    /// aucun double-clic / invoke sur un item de lstProcessus (pas d'ouverture
    /// de PROC, pas de risque mail/etat). La selection d'un sous-menu est un
    /// clic simple (equivalent a la mise en surbrillance dans la liste).
    /// </summary>
    /// <returns>
    /// Arbre au format :
    ///   RAIL 1 [label]
    ///     SOUSMENU: nom
    ///       PROC: nom1
    ///       PROC: nom2
    ///     ...
    /// </returns>
    public string DumpMenuTree()
    {
        if (_app is null || _automation is null || _window is null)
            throw new InvalidOperationException("Launch() + ClickSeConnecter() doivent etre appeles avant DumpMenuTree()");

        // Garantit la fenetre maximisee (btn3..btn7 hors viewport sinon)
        if (Headless)
            EnsureWindowMaximized();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== RIG MENU TREE ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        int totalRails = 0;
        int totalSousmenus = 0;
        int totalProcessus = 0;

        string prevSousmenuSig = CurrentListSignature("lstSousmenu");

        for (int n = 1; n <= 7; n++)
        {
            var btn = FindByAutomationIdWithRetry("btn" + n, timeoutMs: 2500);
            if (btn is null)
            {
                Console.WriteLine($"   [dump] btn{n} absent apres retry — skip");
                sb.AppendLine($"RAIL {n} [(absent)]");
                continue;
            }

            string railLabel = SafeText(() => btn.Name);
            // ASCII-only : remplace les accentes
            railLabel = ToAscii(railLabel);
            Console.WriteLine($"   [dump] === btn{n} '{railLabel}' : activation...");

            // Clic avec retry (meme pattern que OpenProcessus)
            AutomationElement[] subItems = System.Array.Empty<AutomationElement>();
            string newSig = prevSousmenuSig;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!TryActivateRailTab(btn, $"dump-btn{n}")) break;
                subItems = WaitForListRepopulated("lstSousmenu", prevSousmenuSig, 3000);
                newSig = string.Join("|", subItems.Select(s => SafeText(() => s.Name)));
                if (subItems.Length > 0 && newSig != prevSousmenuSig) break;
                if (attempt < 3)
                {
                    Console.WriteLine($"   [dump] btn{n} panneau fige (tentative {attempt}/3) — re-clic");
                    Thread.Sleep(800); // sleep-ok: RIG async rail reload, pas de signal observable
                }
            }

            if (subItems.Length == 0)
            {
                Console.WriteLine($"   [dump] btn{n} lstSousmenu vide apres repopulation");
                sb.AppendLine($"RAIL {n} [{railLabel}] (vide)");
                prevSousmenuSig = newSig;
                continue;
            }

            totalRails++;
            prevSousmenuSig = newSig;
            sb.AppendLine($"RAIL {n} [{railLabel}]");
            Console.WriteLine($"   [dump] btn{n} '{railLabel}' : {subItems.Length} sous-menus");

            string prevProcSig = CurrentListSignature("lstProcessus");

            for (int i = 0; i < subItems.Length; i++)
            {
                string subLabel = SafeText(() => subItems[i].Name);
                subLabel = ToAscii(subLabel);

                // Clic simple (JAMAIS double-clic ni Invoke) pour charger lstProcessus
                bool activated = false;
                try
                {
                    Interaction.Click(subItems[i]);
                    activated = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   [dump]   sousmenu[{i}] clic jete : {ex.Message}");
                }

                AutomationElement[] procItems = System.Array.Empty<AutomationElement>();
                if (activated)
                {
                    procItems = WaitForListRepopulated("lstProcessus", prevProcSig, 2000);
                    prevProcSig = string.Join("|", procItems.Select(p => SafeText(() => p.Name)));
                }

                sb.AppendLine($"  SOUSMENU: {subLabel}");
                totalSousmenus++;

                if (procItems.Length == 0)
                {
                    sb.AppendLine($"    (vide)");
                    Console.WriteLine($"   [dump]   sousmenu[{i}] '{subLabel}' : lstProcessus vide");
                    continue;
                }

                Console.WriteLine($"   [dump]   sousmenu[{i}] '{subLabel}' : {procItems.Length} processus");
                foreach (var pItem in procItems)
                {
                    string procName = ToAscii(SafeText(() => pItem.Name));
                    if (string.IsNullOrWhiteSpace(procName)) continue;
                    sb.AppendLine($"    PROC: {procName}");
                    totalProcessus++;
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine($"=== TOTAUX : {totalRails} rails, {totalSousmenus} sous-menus, {totalProcessus} processus ===");

        // Log resume console
        Console.WriteLine($"   [dump] Arbre complet : {totalRails} rails non vides, {totalSousmenus} sous-menus, {totalProcessus} processus");

        return sb.ToString();
    }

    /// <summary>
    /// Transliteration ASCII best-effort : remplace les caracteres accentues
    /// les plus courants (francais) par leur equivalent ASCII. Garantit une
    /// sortie propre dans les fichiers texte mono-encodage (UTF-8 sans BOM mais
    /// lisible sans police speciale).
    /// </summary>
    private static string ToAscii(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Normalise en NFD puis garde les caracteres ASCII (supprime les diacritiques)
        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
