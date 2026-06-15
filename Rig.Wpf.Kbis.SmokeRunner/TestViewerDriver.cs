using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Pilote FlaUI/UIA3 pour Rig Testing (Rig.Wpf.Kbis.TestViewer.exe — WPF .NET 4.8).
///
/// Pendant au LegacyDriver côté RigClientAccueil legacy : permet à l'agent (ou à un
/// dev qui veut un loop scripté) de driver le TestViewer programmatiquement plutôt
/// que click-by-click manuel. Cas d'usage principal : itérer un import Rapture sur
/// plusieurs JSON sans recompiler, en regardant les logs streamer dans la zone
/// noire en bas de l'onglet "Smoke Import" du module RAPTURE.
///
/// Stratégie :
///   1. Attache à un TestViewer déjà lancé (préféré, pour que l'user voie le run)
///      ou launch une nouvelle instance si rien ne tourne
///   2. Click la nav pill "RAPTURE" pour basculer sur ce module
///   3. (Le TabItem "Smoke Import" est le 1er → déjà sélectionné)
///   4. Set le ComboBox des JSONs sur le fixture demandé (partial match sur nom)
///   5. Click "▶ Run smoke import"
///   6. Poll la box noire des logs jusqu'à voir le summary "✓ N ✗ N ⊘ N" ou timeout
///   7. Sort en émettant le stdout final + exit code (0 si ✗ = 0, 1 sinon)
/// </summary>
public sealed class TestViewerDriver : IDisposable
{
    private readonly string _exePath;
    private readonly bool _weLaunched;
    private readonly Application _app;
    private readonly UIA3Automation _automation;
    private readonly Window _window;

    public TestViewerDriver(string? exePath = null)
    {
        _exePath = exePath ?? DefaultExePath();
        _automation = new UIA3Automation();

        // Attache à une instance déjà ouverte si possible (l'user veut voir le run en live).
        var running = Process.GetProcessesByName("Rig.Wpf.Kbis.TestViewer").FirstOrDefault();
        if (running is not null)
        {
            Console.WriteLine($"   → Attache à TestViewer existant PID={running.Id}");
            _app = Application.Attach(running.Id);
            _weLaunched = false;
        }
        else
        {
            if (!File.Exists(_exePath))
                throw new FileNotFoundException("TestViewer.exe introuvable", _exePath);
            Console.WriteLine($"   → Launch TestViewer {_exePath}");
            _app = Application.Launch(_exePath);
            _weLaunched = true;
        }

        // GetMainWindow polls jusqu'à apparition (max ~10s par défaut côté FlaUI).
        _window = _app.GetMainWindow(_automation)
            ?? throw new InvalidOperationException("Main window TestViewer introuvable");
        Console.WriteLine($"   → Main window OK : title='{SafeText(() => _window.Title)}'");

        // Garde-fou anti-clic-parasite : ramène la fenêtre on-screen AVANT toute
        // interaction. Sans ça, si le TestViewer s'ouvre hors-écran (X négatif /
        // VirtualScreen fantôme), un fallback Mouse.Click au centre de son rect
        // part dans une AUTRE app visible (incident Teams, 2026-05-19).
        BringWindowOnScreen();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    // SM_*VIRTUALSCREEN — coords du virtual screen (union de tous les monitors)
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// Si la fenêtre TestViewer est hors de l'écran (coordonnées négatives /
    /// VirtualScreen fantôme), la ramène en haut-gauche de l'écran réel et la met
    /// au premier plan. Retourne true si la fenêtre est (désormais) on-screen —
    /// les fallbacks Mouse.Click ne doivent JAMAIS cliquer si false.
    /// </summary>
    private bool BringWindowOnScreen()
    {
        try
        {
            // SetForeground() volerait le focus user → désactivé (règle focus-free 2026-05-22).
            // Les PostMessage marchent sur des windows non-foreground. UIA enumère aussi.
            Thread.Sleep(150);
            var r = _window.BoundingRectangle;
            // Compute virtual screen via GetSystemMetrics (sum de TOUS les monitors).
            // Évite le faux-positif "X=1912 Y=-8" quand TV est sur le 2ème écran avec
            // taskbar offset Windows : ce N'EST PAS off-screen, juste sur monitor 2.
            // Bug fix 2026-05-28 : le SetWindowPos cross-monitor cassait le rendering WPF
            // (transparence, bascule visible entre écrans).
            int vsX = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vsY = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vsW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vsH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int vsRight = vsX + vsW;
            int vsBottom = vsY + vsH;
            // Tolérance -100 pour la bordure Windows (titlebar peut être -8 sur Y).
            bool offScreen = r.X < vsX - 100 || r.Y < vsY - 100
                             || r.X > vsRight || r.Y > vsBottom
                             || r.Width <= 0 || r.Height <= 0;
            if (!offScreen)
            {
                Console.WriteLine($"   → TestViewer on-screen OK ({r.X},{r.Y}) — virtualScreen=[{vsX},{vsY} → {vsRight},{vsBottom}] — pas de repositionnement");
                return true;
            }

            var hwnd = _window.Properties.NativeWindowHandle.ValueOrDefault;
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine($"   ⚠ TestViewer hors-écran ({r.X},{r.Y}) et HWND introuvable — clics souris désactivés");
                return false;
            }
            SetWindowPos(hwnd, IntPtr.Zero, 60, 40, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
            Thread.Sleep(350);
            // SetForeground() volerait le focus user → désactivé (règle focus-free 2026-05-22).
            // Les PostMessage marchent sur des windows non-foreground. UIA enumère aussi.
            var r2 = _window.BoundingRectangle;
            Console.WriteLine($"   → TestViewer repositionné on-screen : ({r.X},{r.Y}) → ({r2.X},{r2.Y})");
            return r2.X >= 0 && r2.Y >= 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠ BringWindowOnScreen : {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Dump diagnostic des Buttons + Text + ComboBox visibles dans la fenêtre.</summary>
    public void DumpUiaTree(int maxPerType = 30)
    {
        Console.WriteLine($"   ▶ UIA dump fenêtre '{SafeText(() => _window.Title)}'");
        var win = _window.BoundingRectangle;
        Console.WriteLine($"   ▶ Window rect : X={win.X} Y={win.Y} W={win.Width} H={win.Height}");
        var buttons = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        Console.WriteLine($"   ▶ {buttons.Length} Button trouvés :");
        foreach (var b in buttons.Take(maxPerType))
        {
            var r = b.BoundingRectangle;
            Console.WriteLine($"      Button Name='{SafeText(() => b.Name)}' Rect={r.X},{r.Y} {r.Width}x{r.Height} ChildText='{ChildTextOf(b)}'");
        }
        var combos = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox));
        Console.WriteLine($"   ▶ {combos.Length} ComboBox :");
        foreach (var c in combos.Take(maxPerType))
        {
            var r = c.BoundingRectangle;
            Console.WriteLine($"      ComboBox Name='{SafeText(() => c.Name)}' AutomationId='{SafeText(() => c.AutomationId)}' Rect={r.X},{r.Y} {r.Width}x{r.Height}");
        }
        var tabs = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
        Console.WriteLine($"   ▶ {tabs.Length} TabItem :");
        foreach (var t in tabs.Take(maxPerType))
            Console.WriteLine($"      Tab Name='{SafeText(() => t.Name)}' ChildText='{ChildTextOf(t)}'");
    }

    private string ChildTextOf(AutomationElement el)
    {
        try
        {
            var texts = el.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => SafeText(() => t.Name))
                .Where(s => !string.IsNullOrEmpty(s))
                .Take(3);
            return string.Join(" / ", texts);
        }
        catch { return ""; }
    }

    /// <summary>Path par défaut du TestViewer dans le repo. Override par env <c>RIG_TESTVIEWER_EXE</c>.</summary>
    public static string DefaultExePath()
    {
        var ovr = Environment.GetEnvironmentVariable("RIG_TESTVIEWER_EXE");
        if (!string.IsNullOrEmpty(ovr)) return ovr;

        // Résout relativement à l'exe SmokeRunner courant, en conservant la MÊME config + TFM :
        //   <root>\Rig.Wpf.Kbis.SmokeRunner\bin\<cfg>\<tfm>\  →  <root>\Rig.Wpf.Kbis.TestViewer\bin\<cfg>\<tfm>\…TestViewer.exe
        // Robuste au clone aplati RIG-TV comme à l'arbo historique (le path en dur ci-dessous ne valait plus
        // que pour l'ancien checkout RigApplication-testing\Source\Wpf — d'où le « TestViewer.exe introuvable »).
        try
        {
            var tfmDir = System.IO.Path.GetDirectoryName(typeof(TestViewerDriver).Assembly.Location)?.TrimEnd('\\', '/');
            var tfm    = System.IO.Path.GetFileName(tfmDir);                                  // net48
            var cfgDir = System.IO.Path.GetDirectoryName(tfmDir);                             // …\bin\<cfg>
            var cfg    = System.IO.Path.GetFileName(cfgDir);                                  // Debug / Release
            var binDir = System.IO.Path.GetDirectoryName(cfgDir);                             // …\bin
            var root   = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(binDir)); // <root>
            if (root != null)
            {
                var candidate = System.IO.Path.Combine(root, "Rig.Wpf.Kbis.TestViewer", "bin", cfg, tfm, "Rig.Wpf.Kbis.TestViewer.exe");
                if (System.IO.File.Exists(candidate)) return candidate;
            }
        }
        catch { /* retombe sur le checkout historique ci-dessous */ }

        return @"C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer\bin\Debug\net48\Rig.Wpf.Kbis.TestViewer.exe";
    }

    /// <summary>
    /// Click la nav pill du module donné (par nom court : "RAPTURE" / "KBIS" / "Global" / etc).
    /// </summary>
    public void SwitchToModule(string moduleName)
    {
        // Les pills sont des ToggleButton WPF avec deux TextBlock enfants (Icon + Name).
        // WPF UIA peut concaténer les TextBlock dans le Name du Button, OU les exposer
        // comme enfants séparés. On essaie plusieurs stratégies.
        AutomationElement? pill = null;

        // 1) Stratégie primaire : trouver un Text node = moduleName puis remonter à
        //    l'ancêtre ToggleButton/Button. Plus fiable que filtrer sur Name='' qui peut
        //    aussi match des boutons d'icône sans label.
        var textElement = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(t => SafeText(() => t.Name).Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        if (textElement is not null)
        {
            var anc = textElement.Parent;
            while (anc is not null
                   && anc.ControlType != ControlType.Button
                   && anc.ControlType != ControlType.RadioButton
                   && anc.ControlType != ControlType.Window)
                anc = anc.Parent;
            if (anc is not null && (anc.ControlType == ControlType.Button || anc.ControlType == ControlType.RadioButton))
                pill = anc;
        }

        // 2) Fallback : Button dont le Name contient moduleName (cas où WPF a synthétisé le Name)
        if (pill is null)
        {
            pill = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b =>
                {
                    var n = SafeText(() => b.Name);
                    return !string.IsNullOrEmpty(n)
                        && n.IndexOf(moduleName, StringComparison.OrdinalIgnoreCase) >= 0;
                });
        }

        // 3) Dernière chance : n'importe quel descendant (Pane inclus) dont le Name match
        if (pill is null)
        {
            pill = _window.FindAllDescendants()
                .FirstOrDefault(b => SafeText(() => b.Name).IndexOf(moduleName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (pill is null)
        {
            // Diagnostic — dump les Names des controls clickables principaux
            Console.WriteLine("   → Dump descendants Button/RadioButton + Text :");
            foreach (var c in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Take(20))
                Console.WriteLine($"      Button '{SafeText(() => c.Name)}'");
            foreach (var c in _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Take(20))
                Console.WriteLine($"      Text   '{SafeText(() => c.Name)}'");
            throw new Exception($"Module pill '{moduleName}' introuvable dans la nav. Modules connus : KBIS / RAPTURE / Global.");
        }
        var rect = pill.BoundingRectangle;
        Console.WriteLine($"   → Click nav pill '{moduleName}' (ControlType={pill.ControlType}, Name='{SafeText(() => pill.Name)}', Rect={rect.X},{rect.Y} {rect.Width}x{rect.Height})");
        // Bring window to foreground d'abord — sinon les clicks souris sur une fenêtre
        // non-focused sont parfois ignorés (Windows policy anti-hijack).
        // SetForeground() volerait le focus user → désactivé (règle focus-free 2026-05-22).

        // Cascade STRICTEMENT mouse-free ET focus-free (règle user 2026-05-22 :
        // permettre la parallélisation N workers sans vol de souris/clavier/focus mutuel).
        //
        // ⚠ Ordre IMPORTANT : on commence par PostClickAtElement (PostMessage WM_LBUTTON
        // sur hwnd Window, ne touche NI focus NI foreground). Les patterns UIA suivants
        // n'utilisent PAS Focus() pour ne pas voler le focus utilisateur :
        //   1. PostClickAtElement (PostMessage hit-test, fire-and-forget) — premier choix
        //   2. InvokePattern.Invoke (UIA, pas de focus)
        //   3. TogglePattern.Toggle (UIA, peut muter IsChecked SANS firer Command WPF)
        //
        // On VÉRIFIE après chaque tentative que la bascule UI a eu lieu, sinon on enchaîne.
        bool HasSmokeImportTab() => _window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
            .Any(t =>
            {
                var name = SafeText(() => t.Name);
                if (name.IndexOf("Smoke Import", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                var childText = string.Join(" / ", t.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(x => SafeText(() => x.Name)));
                return childText.IndexOf("Smoke Import", StringComparison.OrdinalIgnoreCase) >= 0;
            });
        bool PollSwitchOk(int maxMs = 1500)
        {
            var swp = System.Diagnostics.Stopwatch.StartNew();
            while (swp.ElapsedMilliseconds < maxMs)
            {
                if (HasSmokeImportTab()) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        bool ok = false;
        // Tentative 1 : PostClickAtElement (PostMessage WM_LBUTTON, idéal — pas de vol focus)
        try
        {
            bool posted = PostClickAtElement(pill);
            Console.WriteLine($"      → PostClickAtElement(pill) {(posted ? "posté" : "skip")} (mouse-free + focus-free)");
            if (posted) ok = PollSwitchOk();
            Console.WriteLine($"      → Vérif #1 (PostClick) : TabItem 'Smoke Import' visible ? {ok}");
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ PostClick : {ex.GetType().Name}: {ex.Message}"); }

        // Tentative 1bis : si PostClick a échoué, c'est probablement parce que TestViewer
        // n'est pas active (WPF dispatch les mouse events seulement quand window active).
        // On utilise alors Focus + WM_KEYDOWN Space — mini vol de focus user (instantané),
        // inévitable pour piloter une UI WPF visible. SCOPE : setup initial TestViewer
        // seulement (~3-5s, 4-5 toggles). Durant les 5min du batch RIG legacy : zéro vol.
        if (!ok)
        {
            try
            {
                pill.Focus();
                Thread.Sleep(40);
                var hwnd = (IntPtr)_window.Properties.NativeWindowHandle.ValueOrDefault;
                if (hwnd != IntPtr.Zero) Interaction.PressKey(hwnd, 0x20); // VK_SPACE
                Console.WriteLine("      → Focus + WM_KEYDOWN Space (vol focus instantané, setup-only)");
                ok = PollSwitchOk();
                Console.WriteLine($"      → Vérif #1bis (Focus+Space) : TabItem 'Smoke Import' visible ? {ok}");
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Focus+Space : {ex.GetType().Name}: {ex.Message}"); }
        }

        // Tentative 2 : Invoke pattern
        if (!ok)
        {
            try
            {
                if (pill.Patterns.Invoke.IsSupported)
                {
                    pill.Patterns.Invoke.Pattern.Invoke();
                    Console.WriteLine("      → InvokePattern.Invoke() invoqué");
                    ok = PollSwitchOk();
                    Console.WriteLine($"      → Vérif #2 (Invoke) : TabItem 'Smoke Import' visible ? {ok}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Invoke : {ex.GetType().Name}: {ex.Message}"); }
        }

        // Tentative 3 : Toggle pattern (peut juste muter IsChecked sans firer Command)
        if (!ok)
        {
            try
            {
                if (pill.Patterns.Toggle.IsSupported)
                {
                    pill.Patterns.Toggle.Pattern.Toggle();
                    Console.WriteLine("      → TogglePattern.Toggle() invoqué");
                    ok = PollSwitchOk();
                    Console.WriteLine($"      → Vérif #3 (Toggle) : TabItem 'Smoke Import' visible ? {ok}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Toggle : {ex.GetType().Name}: {ex.Message}"); }
        }

        // Tentative 4 : PostMessage WM_LBUTTONDOWN/UP sur le hwnd Window WPF, coords client du pill.
        // WPF dispatch ces messages via HwndSource.InputManager → click event sur le visual cible.
        // Aucun mouvement de souris physique.
        if (!ok)
        {
            try
            {
                var winHwnd = (IntPtr)_window.Properties.NativeWindowHandle.ValueOrDefault;
                var r = pill.BoundingRectangle;
                if (winHwnd != IntPtr.Zero && r.Width > 0 && r.Height > 0)
                {
                    var cx = (int)(r.X + r.Width / 2);
                    var cy = (int)(r.Y + r.Height / 2);
                    var pt = new POINT { X = cx, Y = cy };
                    if (ScreenToClient(winHwnd, ref pt))
                    {
                        int lparam = (pt.Y << 16) | (pt.X & 0xFFFF);
                        const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, MK_LBUTTON = 0x0001;
                        PostMessage(winHwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, (IntPtr)lparam);
                        Thread.Sleep(30);
                        PostMessage(winHwnd, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lparam);
                        Console.WriteLine($"      → PostMessage WM_LBUTTON DOWN+UP à client ({pt.X},{pt.Y}) sur hwnd 0x{winHwnd.ToInt64():X}");
                        ok = PollSwitchOk();
                        Console.WriteLine($"      → Vérif #4 (PostMessage) : TabItem 'Smoke Import' visible ? {ok}");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ PostMessage : {ex.GetType().Name}: {ex.Message}"); }
        }

        if (!ok)
            Console.WriteLine("      ✗ Module pas basculé après cascade complète (Focus+Space / Invoke / Toggle / PostMessage). Continue quand même — la step suivante détectera l'absence d'élément.");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Poste WM_LBUTTONDOWN+WM_LBUTTONUP sur le hwnd Window aux coords client du
    /// centre de l'élément. WPF dispatch le click au visual cible via HwndSource.
    /// InputManager → fire le Click event WPF → exécute le Command bound.
    ///
    /// ⚠ AVANTAGE PRIMORDIAL : ne touche NI au focus NI au foreground window
    /// (vs Focus()+Keyboard.Type qui volent les deux). Permet de piloter
    /// TestViewer en parallèle de l'activité utilisateur — focus de saisie
    /// dans Outlook/Notepad/etc. PRÉSERVÉ. Essentiel pour parallelisme N workers.
    ///
    /// Renvoie false si pas de hwnd Window dispo ou élément hors-écran.
    /// </summary>
    private bool PostClickAtElement(AutomationElement element)
    {
        if (element is null) return false;
        var hwnd = (IntPtr)_window.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero) return false;
        int cx, cy;
        try
        {
            var r = element.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return false;
            cx = (int)(r.X + r.Width / 2);
            cy = (int)(r.Y + r.Height / 2);
        }
        catch { return false; }
        var pt = new POINT { X = cx, Y = cy };
        if (!ScreenToClient(hwnd, ref pt)) return false;
        int lparam = (pt.Y << 16) | (pt.X & 0xFFFF);
        const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        const int MK_LBUTTON = 0x0001;
        PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, (IntPtr)lparam);
        Thread.Sleep(30);
        PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lparam);
        return true;
    }

    /// <summary>
    /// Sélectionne dans le ComboBox des JSON fixtures l'entrée dont le path contient
    /// <paramref name="partialName"/> (case-insensitive). Renvoie le path effectivement
    /// sélectionné. Throw si aucun match.
    /// </summary>
    public string SelectJsonFixture(string partialName)
    {
        var combo = FindRaptureJsonCombo();
        // Expand pour faire apparaître les items (sinon FindAllChildren sur combo collapsed
        // peut être vide selon le template WPF).
        try { combo.AsComboBox().Expand(); } catch { /* fallback */ }
        Thread.Sleep(200);

        var items = combo.AsComboBox().Items;
        Console.WriteLine($"   → ComboBox JSON : {items.Length} items");
        var match = items.FirstOrDefault(i =>
        {
            var t = SafeText(() => i.Text);
            return t.IndexOf(partialName, StringComparison.OrdinalIgnoreCase) >= 0;
        });
        if (match is null)
        {
            try { combo.AsComboBox().Collapse(); } catch { }
            var sample = string.Join(", ", items.Take(5).Select(i => Path.GetFileName(SafeText(() => i.Text))));
            throw new Exception($"JSON '{partialName}' introuvable dans le ComboBox (premiers items : {sample})");
        }
        Console.WriteLine($"   → Sélection : '{SafeText(() => match.Text)}'");
        match.Select();
        Thread.Sleep(300);
        try { combo.AsComboBox().Collapse(); } catch { }
        return SafeText(() => match.Text);
    }

    /// <summary>
    /// Trouve un bouton par AutomationId (stable, indépendant du libellé affiché),
    /// avec repli sur un substring du Name. Évite que renommer un bouton casse le driver.
    /// </summary>
    private AutomationElement? FindButton(string automationId, string nameFallback)
    {
        var btn = _window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (btn is null)
            btn = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => SafeText(() => b.Name).IndexOf(nameFallback, StringComparison.OrdinalIgnoreCase) >= 0);
        return btn;
    }

    /// <summary>
    /// Coche/décoche "Accepter création audience" (scénario YES + cleanup SQL).
    /// Trouvé par AutomationId stable ; idempotent (ne toggle que si nécessaire).
    /// </summary>
    public void SetRaptureAcceptCreate(bool want)
    {
        var chk = _window.FindFirstDescendant(cf => cf.ByAutomationId("ChkRaptureAcceptCreate"))
               ?? _window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                    .FirstOrDefault(c => SafeText(() => c.Name).IndexOf("Accepter création audience", StringComparison.OrdinalIgnoreCase) >= 0);
        if (chk is null)
            throw new Exception("CheckBox 'Accepter création audience' introuvable (AutomationId=ChkRaptureAcceptCreate).");
        var cb = chk.AsCheckBox();
        bool current = cb.IsChecked == true;
        if (current == want)
        {
            Console.WriteLine($"   → 'Accepter création audience' déjà = {want}");
            return;
        }
        // Mouse-free toggle : Toggle pattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { cb.Toggle(); }
        catch { try { chk.Patterns.Toggle.Pattern.Toggle(); } catch { try { PostClickAtElement(chk); } catch { } } }
        Thread.Sleep(200);
        Console.WriteLine($"   → 'Accepter création audience' → {(cb.IsChecked == true)} (voulu {want})");
    }

    public void SetRaptureApplyReal(bool want)
    {
        var chk = _window.FindFirstDescendant(cf => cf.ByAutomationId("ChkRaptureApplyReal"))
               ?? _window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                    .FirstOrDefault(c => SafeText(() => c.Name).IndexOf("Apply", StringComparison.OrdinalIgnoreCase) >= 0);
        if (chk is null)
            throw new Exception("CheckBox 'Apply réel' introuvable (AutomationId=ChkRaptureApplyReal).");
        var cb = chk.AsCheckBox();
        bool current = cb.IsChecked == true;
        if (current == want)
        {
            Console.WriteLine($"   → 'Apply réel' déjà = {want}");
            return;
        }
        // Mouse-free toggle : Toggle pattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { cb.Toggle(); }
        catch { try { chk.Patterns.Toggle.Pattern.Toggle(); } catch { try { PostClickAtElement(chk); } catch { } } }
        Thread.Sleep(200);
        Console.WriteLine($"   → 'Apply réel' → {(cb.IsChecked == true)} (voulu {want})");
    }

    /// <summary>Toggle la CheckBox 'Mode visible' (ChkRaptureVisibleMode).</summary>
    public void SetRaptureVisibleMode(bool want)
    {
        var chk = _window.FindFirstDescendant(cf => cf.ByAutomationId("ChkRaptureVisibleMode"))
               ?? _window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                    .FirstOrDefault(c => SafeText(() => c.Name).IndexOf("Mode visible", StringComparison.OrdinalIgnoreCase) >= 0);
        if (chk is null)
            throw new Exception("CheckBox 'Mode visible' introuvable (AutomationId=ChkRaptureVisibleMode).");
        var cb = chk.AsCheckBox();
        bool current = cb.IsChecked == true;
        if (current == want)
        {
            Console.WriteLine($"   → 'Mode visible' déjà = {want}");
            return;
        }
        // Mouse-free toggle : Toggle pattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { cb.Toggle(); }
        catch { try { chk.Patterns.Toggle.Pattern.Toggle(); } catch { try { PostClickAtElement(chk); } catch { } } }
        Thread.Sleep(200);
        Console.WriteLine($"   → 'Mode visible' → {(cb.IsChecked == true)} (voulu {want})");
    }

    /// <summary>Click "⏹ Stop" (tue l'arbre de process du run Rapture).</summary>
    public void ClickRaptureStop()
    {
        var btn = FindButton("BtnRaptureStop", "Stop");
        if (btn is null) throw new Exception("Bouton Stop introuvable (AutomationId=BtnRaptureStop).");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>Click le bouton Pause/Reprendre (toggle freeze de l'arbre de process).</summary>
    public void ClickRapturePauseResume()
    {
        var btn = FindButton("BtnRapturePause", "Pause");
        if (btn is null) throw new Exception("Bouton Pause/Reprendre introuvable (AutomationId=BtnRapturePause).");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>Click le smoke UI réel Rapture (lance RigClientAccueil). Async — voir <see cref="WaitForSmokeCompletion"/>.</summary>
    public void ClickRunSmokeImport()
    {
        var btn = FindButton("BtnRaptureSmokeUi", "Smoke UI");
        if (btn is null)
            throw new Exception("Bouton Smoke UI Rapture introuvable (AutomationId=BtnRaptureSmokeUi). Onglet 'Smoke Import' visible ?");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>Click le diag pipeline headless Rapture (mode --rapture-diag). Async — voir <see cref="WaitForSmokeCompletion"/>.</summary>
    public void ClickDiagnosticImport()
    {
        var btn = FindButton("BtnRaptureDiag", "Diag pipeline");
        if (btn is null)
            throw new Exception("Bouton Diag pipeline introuvable (AutomationId=BtnRaptureDiag). Onglet 'Smoke Import' du module RAPTURE visible ?");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>
    /// Click le bouton « 🎯 Process E2E » (mode SmokeRunner <c>--legacy-rapture-process</c>) :
    /// lance RIG via FlaUI, login RIG, ouvre PROC_RETAUD, sélectionne une audience peuplée
    /// CIBLÉE qui matche le JSON par date+heure, clique « Importer Rapture » → recap directe
    /// Cas A. Async — voir <see cref="WaitForSmokeCompletion"/>.
    /// </summary>
    public void ClickRaptureProcessE2E()
    {
        var btn = FindButton("BtnRaptureProcessE2E", "Start E2E");
        if (btn is null)
            throw new Exception("Bouton Start E2E (Import) introuvable (AutomationId=BtnRaptureProcessE2E). Onglet 'Smoke Import' du module RAPTURE visible ?");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>
    /// Click le bouton « ▶ Start E2E » du tab Smoke Export (AutomationId
    /// <c>BtnRaptureExportE2E</c>). Pilote l'export Plumitif via RIG legacy en FlaUI.
    /// Symétrique de <see cref="ClickRaptureProcessE2E"/> côté import.
    /// </summary>
    public void ClickRaptureExportE2E()
    {
        var btn = FindButton("BtnRaptureExportE2E", "Start E2E");
        if (btn is null)
            throw new Exception("Bouton Start E2E (Export) introuvable (AutomationId=BtnRaptureExportE2E). Onglet 'Smoke Export' du module RAPTURE visible ?");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>
    /// Sélectionne un TabItem par le texte de son header (descendant Text). Le module
    /// KBIS a plusieurs sous-onglets (Smoke WPF / xUnit / Scénarios / Smoke Legacy) ;
    /// le bouton "Run smoke RIG" vit dans "Smoke Legacy".
    /// </summary>
    public void SelectTab(string headerContains)
    {
        var tab = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
            .FirstOrDefault(t =>
            {
                if (SafeText(() => t.Name).IndexOf(headerContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                // Header custom (StackPanel + TextBlock) → Name vide, on scanne les Text enfants
                var childText = string.Join(" ", t.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(x => SafeText(() => x.Name)));
                return childText.IndexOf(headerContains, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        if (tab is null)
        {
            var avail = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
                .Select(t => "'" + SafeText(() => t.Name) + "'");
            throw new Exception($"TabItem contenant '{headerContains}' introuvable. Tabs : {string.Join(", ", avail)}");
        }
        Console.WriteLine($"   → Select tab '{headerContains}'");
        // Mouse-free select : SelectionItem pattern UIA, sinon Focus (sans Mouse.Click).
        try { tab.Patterns.SelectionItem.Pattern.Select(); }
        catch { try { tab.AsTabItem().Select(); } catch { try { PostClickAtElement(tab); } catch { } } }
        Thread.Sleep(500);
    }

    /// <summary>
    /// Trouve un bouton dont le Name contient <paramref name="needle"/> dans l'arbre
    /// courant (tab réalisé uniquement, WPF virtualise les TabControl).
    /// </summary>
    private AutomationElement? FindButton(string needle) =>
        _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => SafeText(() => b.Name).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// Click "▶ Run smoke RIG" (smoke legacy KBIS/VK = RunLegacyCommand → SmokeRunner
    /// --legacy). Le bouton vit dans le sous-onglet "Smoke Legacy" du module KBIS,
    /// dont le contenu n'est réalisé qu'une fois l'onglet sélectionné (virtualisation
    /// WPF + headers custom non lisibles en UIA). Stratégie : si le bouton n'est pas
    /// déjà visible, on itère les TabItems, on sélectionne chacun, et on re-cherche
    /// le bouton jusqu'à le trouver. Async côté UI — poll via WaitForSmokeCompletion.
    /// </summary>
    /// <summary>
    /// Sélectionne le sous-onglet "Smoke Legacy" (réalise son contenu, virtualisation
    /// WPF) en itérant les TabItems jusqu'à voir le bouton "Run smoke RIG". Retourne
    /// le bouton, ou null si introuvable.
    /// </summary>
    public AutomationElement? EnsureLegacyTabRealized()
    {
        var btn = FindButton("Run smoke RIG");
        if (btn is not null) return btn;
        var tabs = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).ToList();
        Console.WriteLine($"   → Bouton pas visible — itère {tabs.Count} TabItems pour réaliser 'Smoke Legacy'…");
        foreach (var t in tabs)
        {
            // Mouse-free select : SelectionItem pattern UIA, sinon Focus (sans Mouse.Click).
            try { t.Patterns.SelectionItem.Pattern.Select(); }
            catch { try { t.AsTabItem().Select(); } catch { try { PostClickAtElement(t); } catch { } } }
            Thread.Sleep(500);
            btn = FindButton("Run smoke RIG");
            if (btn is not null)
            {
                Console.WriteLine("   → Onglet réalisant 'Run smoke RIG' sélectionné");
                return btn;
            }
        }
        return null;
    }

    public void ClickRunSmokeLegacy()
    {
        var btn = EnsureLegacyTabRealized();
        if (btn is null)
            throw new Exception("Bouton 'Run smoke RIG' introuvable après itération des onglets. Module KBIS sélectionné ?");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        // Mouse-free invoke : InvokePattern UIA, sinon Focus+Space (clavier). Jamais Mouse.Click.
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>
    /// Click "▶ Stress ×N" (StressSelectedScenarioCommand → X copies parallèles du scénario
    /// sélectionné). Le bouton vit dans le même onglet "Smoke Legacy" que "Run smoke RIG" →
    /// on réalise l'onglet via EnsureLegacyTabRealized, puis on cherche le bouton "Stress".
    /// Le scénario/count/loop sont pilotés par env (RIG_KBIS_STRESS_SCENARIO/COUNT/LOOP) lus
    /// par la VM, donc pas besoin de sélectionner une tuile via UIA.
    /// </summary>
    public void ClickRunKbisStress()
    {
        EnsureLegacyTabRealized(); // réalise l'onglet Smoke Legacy (où vivent Run smoke RIG ET Stress)
        var btn = FindButton("Stress");
        if (btn is null)
            throw new Exception("Bouton 'Stress' introuvable. Module KBIS / onglet Smoke Legacy sélectionné ?");
        Console.WriteLine($"   → Click '{SafeText(() => btn.Name)}'");
        try { btn.AsButton().Invoke(); }
        catch { try { PostClickAtElement(btn); } catch { } }
    }

    /// <summary>
    /// Poll la zone de log noire jusqu'à voir le summary marker ou jusqu'à timeout.
    /// <paramref name="summaryMarker"/>: texte spécifique à détecter (ex. "RECAP ALL SCENARIOS" pour batch).
    /// Si null, fallback sur "passed"+"failed"+"skipped" (single-scenario mode).
    /// </summary>
    public string WaitForSmokeCompletion(TimeSpan timeout, string summaryMarker = null)
    {
        // ML LOOP S1.1 — Refactor : BATCH mode = fichier sentinel EXCLUSIVEMENT.
        // Avant : on bailed à 120s sans changement du log box → faux positif quand
        // les workers tournent silencieusement (login RIG, attente audience, etc.)
        // entre 30s et 50s d'exécution → la driver coupait à ~50s prématurément.
        // Maintenant : en batch mode, seul le sentinel fichier décide. Le log box
        // est juste lu pour diagnostic (tail des lignes). Aucune sortie tant que
        // le sentinel ne porte pas un timestamp > baseline.
        var sw = Stopwatch.StartNew();
        // Driver : sentinel PARTAGÉ + gate run_stamp-change (I4) — hang-proof (un exact-match sur un sentinel
        // stampé pourrait pendre si l'env ne se propage pas au TV enfant). L'isolation concurrente du VERDICT
        // est portée par le JSON stampé lu par check-batch-green ; le driver-wait reste best-effort.
        string sentinelPath = @"C:\Code RIG\Audit\last-batch-end.txt";
        bool batchMode = !string.IsNullOrEmpty(summaryMarker);
        DateTime? baselineSentinelTs = batchMode ? TryReadSentinelTs(sentinelPath) : null;
        // Verdict de confiance (fix M2) : capturer aussi le run_stamp du sentinel AVANT le run. La
        // complétion n'est acceptée que si un sentinel porte un run_stamp DIFFÉRENT (= un nouveau run l'a
        // écrit). Sinon un clic « Start E2E » avalé + sentinel périmé d'un run antérieur ferait croire à
        // une complétion alors qu'aucun batch n'a tourné (faux-vert sur le chemin --drive-testviewer).
        string baselineRunStamp = batchMode ? TryReadSentinelRunStamp(sentinelPath) : "";
        Console.WriteLine($"      → WaitForSmokeCompletion mode={(batchMode ? "BATCH (file sentinel only)" : "SINGLE")}, baseline={baselineSentinelTs?.ToString("o") ?? "(none)"}, baselineStamp={(string.IsNullOrEmpty(baselineRunStamp) ? "(none)" : baselineRunStamp)}, timeout={timeout.TotalSeconds:F0}s");

        var lastLen = 0;
        var lastChange = sw.Elapsed;
        string text = "";
        int comRetries = 0;

        while (sw.Elapsed < timeout)
        {
            // -- DIAGNOSTIC ONLY : tail log box new lines pour visibilité --
            try
            {
                var box = FindLogTextBox();
                var newText = SafeText(() => box?.AsTextBox().Text ?? "") ?? "";
                comRetries = 0;
                if (newText.Length != lastLen)
                {
                    lastLen = newText.Length;
                    lastChange = sw.Elapsed;
                    var lines = newText.Replace("\r", "").Split('\n');
                    var tail = lines.Skip(Math.Max(0, lines.Length - 2)).ToList();
                    foreach (var l in tail) Console.WriteLine("      | " + l);
                    text = newText;
                }
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                comRetries++;
                Console.WriteLine($"      ⚠ COM transient ({comRetries}): {ex.HResult:X8}");
                if (comRetries > 10) throw;
                Thread.Sleep(2000);
                continue;
            }

            // -- EXIT CONDITION : fichier sentinel (batch) ou texte log (single) --
            if (batchMode)
            {
                var ts = TryReadSentinelTs(sentinelPath);
                var rs = TryReadSentinelRunStamp(sentinelPath);
                bool newerTs = ts.HasValue && (baselineSentinelTs == null || ts.Value > baselineSentinelTs.Value);
                // Gate run_stamp : si le sentinel en porte un, il DOIT différer de la baseline (nouveau run).
                // Pas de run_stamp (ancien format) → on retombe sur le seul critère timestamp (rétrocompat).
                bool freshStamp = string.IsNullOrEmpty(rs) ? newerTs : !string.Equals(rs, baselineRunStamp, StringComparison.Ordinal);
                if (newerTs && freshStamp)
                {
                    Console.WriteLine($"   → Sentinel batch-end DETECTED après {sw.Elapsed.TotalSeconds:F1}s (ts={ts.Value:o}, baseline={baselineSentinelTs?.ToString("o") ?? "(none)"}, run_stamp={rs}, baselineStamp={baselineRunStamp})");
                    return text;
                }
                // PAS de no-change bail en BATCH mode : les workers peuvent être
                // silencieux pendant les phases longues (login, audience selection,
                // import lui-même). La sortie est UNIQUEMENT le sentinel.
            }
            else
            {
                if (text.Contains("passed") && text.Contains("failed") && text.Contains("skipped"))
                {
                    Console.WriteLine($"   → Summary détectée après {sw.Elapsed.TotalSeconds:F1}s");
                    return text;
                }
                // No-change bail KEPT pour single mode : un scénario unique produit
                // de l'output continu, 60s sans signe = vraisemblablement stuck.
                if (sw.Elapsed - lastChange > TimeSpan.FromSeconds(60))
                {
                    Console.WriteLine($"   ⚠ Aucun changement depuis 60s (single mode) — run probablement bloqué");
                    return text;
                }
            }

            Thread.Sleep(500); // 800→500ms : plus réactif au sentinel sans saturer
        }
        Console.WriteLine($"   ⚠ Timeout {timeout.TotalSeconds}s atteint sans détection sentinel/recap");
        return text;
    }

    /// <summary>
    /// Lit le sentinel fichier <c>Audit\last-batch-end.txt</c> et extrait son
    /// timestamp ISO 8601 (préfixe avant le '|'). Retourne null si fichier
    /// absent, illisible, ou format invalide. Helper utilisé par
    /// <see cref="WaitForSmokeCompletion"/> à la fois pour baseline et pour
    /// la détection per-tick.
    /// </summary>
    private static DateTime? TryReadSentinelTs(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var content = System.IO.File.ReadAllText(path).Trim();
            var firstPipe = content.IndexOf('|');
            var tsStr = firstPipe > 0 ? content.Substring(0, firstPipe) : content;
            return DateTime.TryParse(tsStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
                ? (DateTime?)ts
                : null;
        }
        catch { return null; }
    }

    /// <summary>Lit le champ <c>run_stamp=</c> du sentinel (verdict de confiance, fix M2). "" si absent
    /// ou illisible. Parsing pur délégué à <see cref="Observability.ParseSentinelRunStamp"/> (testé xUnit).</summary>
    private static string TryReadSentinelRunStamp(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return "";
            return Observability.ParseSentinelRunStamp(System.IO.File.ReadAllText(path).Trim());
        }
        catch { return ""; }
    }

    private AutomationElement FindRaptureJsonCombo()
    {
        // Sous le module RAPTURE on n'a qu'UN ComboBox visible (celui des JSONs). Sur les
        // autres modules il peut y en avoir d'autres, mais on est déjà basculé sur RAPTURE.
        var combo = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .FirstOrDefault(c =>
            {
                // Filtre : visible + a au moins 1 item (cf. expand/collapse non-déterministe)
                try { return c.IsAvailable && c.AsComboBox().Items.Length >= 0; }
                catch { return false; }
            });
        if (combo is null)
            throw new Exception("ComboBox JSON fixtures introuvable. Es-tu bien sur l'onglet Smoke Import du module RAPTURE ?");
        return combo;
    }

    private AutomationElement? FindLogTextBox()
    {
        // La box noire est un TextBox IsReadOnly avec FontFamily=Consolas, bindée sur
        // RaptureSmokeLastStdout. On la trouve par sa taille (la plus grande TextBox
        // de la fenêtre) car elle n'a pas d'AutomationId.
        var boxes = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .Where(e =>
            {
                try { return e.IsAvailable && !e.AsTextBox().IsReadOnly == false; } // IsReadOnly == true (logbox)
                catch { return false; }
            })
            .ToList();
        // Plus grande aire visible = la log box. (Les autres TextBox readonly sont des
        // status courts.)
        return boxes
            .Where(b => { try { var r = b.BoundingRectangle; return r.Width > 200 && r.Height > 100; } catch { return false; } })
            .OrderByDescending(b => { try { var r = b.BoundingRectangle; return r.Width * r.Height; } catch { return 0d; } })
            .FirstOrDefault();
    }

    private static string SafeText(Func<string?> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Capture plein écran le TestViewer + dump le contenu textuel du tab actif
    /// (ListItem/Text/Button). Diag pour comprendre pourquoi une liste de scénarios
    /// semble vide côté GUI.
    /// </summary>
    public string? CaptureAndDumpActiveTab(string label)
    {
        // Screenshot
        string? path = null;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "JsonRapture", "screenshots");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"testviewer-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            // Cadre la FENÊTRE TestViewer (pas tout le bureau multi-moniteur — sinon
            // capture minuscule illisible pour le gate visuel rule 15). Foreground
            // d'abord pour qu'elle ne soit pas masquée. Fallback plein écran.
            FlaUI.Core.Capturing.CaptureImage? img = null;
            // SetForeground() volerait le focus user → désactivé (règle focus-free 2026-05-22).
            try { img = FlaUI.Core.Capturing.Capture.Element(_window); }
            catch { }
            if (img is null) img = FlaUI.Core.Capturing.Capture.Screen();
            img.ToFile(path);
            Console.WriteLine($"   📸 Screenshot (fenêtre TestViewer) : {path}");
        }
        catch (Exception ex) { Console.WriteLine($"   ⚠ Screenshot KO : {ex.GetType().Name}"); }

        // Dump UIA contextuel : ListItem (scénarios xUnit) + Text "KBIS legacy".
        // Spécifique au module KBIS — n'a aucun sens hors contexte KBIS (ex. drive
        // RAPTURE qui pollue la console B2 avec des "0 ListItem" / "0 Text 'KBIS
        // legacy'"). On reconnaît un contexte KBIS au tag : "kbis-*" ou contient
        // explicitement "kbis" (insensible casse).
        bool isKbisContext = !string.IsNullOrEmpty(label)
                          && label.IndexOf("kbis", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isKbisContext) return path;
        try
        {
            var items = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            Console.WriteLine($"   ▶ {items.Length} ListItem dans la fenêtre :");
            foreach (var it in items.Take(40))
            {
                var childText = string.Join(" | ", it.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(t => SafeText(() => t.Name)).Where(s => !string.IsNullOrWhiteSpace(s)).Take(2));
                Console.WriteLine($"      ListItem Name='{SafeText(() => it.Name)}' Text=[{childText}]");
            }
            // Compte aussi les Text contenant "KBIS legacy"
            var kbisTexts = _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => SafeText(() => t.Name))
                .Where(s => s.IndexOf("KBIS legacy", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            Console.WriteLine($"   ▶ {kbisTexts.Count} Text contenant 'KBIS legacy' : {string.Join(" / ", kbisTexts.Take(8))}");
        }
        catch (Exception ex) { Console.WriteLine($"   ⚠ Dump KO : {ex.GetType().Name}: {ex.Message}"); }
        return path;
    }

    public void Dispose()
    {
        // On NE close PAS le TestViewer s'il était déjà ouvert quand on a attaché : c'est
        // sa fenêtre que l'utilisateur regarde. On close uniquement si on l'a démarré.
        try
        {
            if (_weLaunched) _app.Close();
        }
        catch { }
        try { _automation.Dispose(); } catch { }
    }
}
