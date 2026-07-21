using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Helper d'interaction "input-free" : pilote des AutomationElement sans
    /// synthèse d'input global (pas de curseur déplacé, pas de SendInput). Tout
    /// passe par patterns UIA ou messages fenêtre postés -> fonctionne sur un
    /// desktop HDESK non-interactif et ne vole jamais la souris de l'utilisateur.
    /// </summary>
    public static class Interaction
    {
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP   = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP   = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_SETTEXT     = 0x000C;
        private const uint WM_KEYDOWN     = 0x0100;
        private const uint WM_KEYUP       = 0x0101;
        private const uint WM_SYSKEYDOWN  = 0x0104;
        private const uint WM_SYSKEYUP    = 0x0105;
        private const int  MK_LBUTTON     = 0x0001;
        private const int  MK_RBUTTON     = 0x0002;
        private const int  VK_RETURN      = 0x0D;
        private const int  VK_F12         = 0x7B;
        private const int  VK_ESCAPE      = 0x1B;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP    = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP   = 0x0010;

        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12; // ALT
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// Clic non-bloquant. Poste WM_LBUTTONDOWN+UP sur le hwnd natif aux
        /// coordonnées-client du centre de l'élément. Fire-and-forget : retourne
        /// immédiatement même si le handler ouvre une MessageBox modale (résout
        /// le deadlock historique du bouton Importer recap).
        /// </summary>
        public static void Click(AutomationElement element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            // 1) hwnd direct si l'élément en expose un (Button standard, Edit, etc.)
            IntPtr hwnd = NativeHandleOf(element);

            // 2) Toolbar items et autres custom controls n'ont pas de hwnd propre :
            //    on walk UP l'arbre UIA pour trouver un ancêtre avec hwnd natif
            //    (typiquement la window parent). PostMessage sur cet ancêtre aux
            //    coords client du bouton fait que WinForms hit-test et dispatch
            //    le click au bon contrôle. AVANTAGE vs InvokePattern : asynchrone
            //    (fire-and-forget) → pas de blocage si handler ouvre une modale.
            if (hwnd == IntPtr.Zero)
            {
                var walker = element;
                for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++)
                {
                    try { walker = walker.Parent; }
                    catch { break; }
                    if (walker == null) break;
                    hwnd = NativeHandleOf(walker);
                }
            }

            // 3) Vraie fin de chaîne : pattern UIA si même le walk-up n'a rien trouvé.
            //    ⚠ InvokePattern est SYNCHRONE — il bloque tant que le handler n'a pas
            //    retourné. Si le handler ouvre une modale (OpenFileDialog, MessageBox),
            //    Invoke timeout après ~60s. On ne l'utilise qu'en dernier recours.
            if (hwnd == IntPtr.Zero)
            {
                if (element.Patterns.Invoke.IsSupported)
                { element.Patterns.Invoke.Pattern.Invoke(); return; }
                if (element.Patterns.SelectionItem.IsSupported)
                { element.Patterns.SelectionItem.Pattern.Select(); return; }
                throw new InvalidOperationException(
                    "Interaction.Click : élément sans hwnd (ni dans ancêtres) ni pattern exploitable " +
                    $"(Name='{Safe(() => element.Name)}', " +
                    $"Type='{Safe(() => element.ControlType.ToString())}', " +
                    $"AutomationId='{Safe(() => element.AutomationId)}').");
            }

            var r = element.BoundingRectangle;
            var p = new POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
            if (!ScreenToClient(hwnd, ref p))
                throw new InvalidOperationException(
                    $"Interaction.Click : ScreenToClient a échoué (hwnd invalide ?) pour " +
                    $"'{Safe(() => element.Name)}'.");
            // lParam = client coords packées (LOWORD=x, HIWORD=y). Le receveur re-signe
            // via (short) -> les coords négatives passent correctement.
            IntPtr lParam = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
        }

        /// <summary>
        /// Active (« lance ») un item de liste, mouse-free. Le RigListView de la
        /// Console d'accueil RIG lance le processus aussi bien sur DoubleClick que
        /// sur KeyDown/Entrée (handler <c>lstProcessus_KeyDown</c> →
        /// <c>_ExecuteProcessusFromMenu</c>). On emprunte le chemin Entrée : il ne
        /// dépend d'aucune coordonnée de hit-test, juste du hwnd de la ListView.
        ///
        /// ⚠ Pourquoi pas 2× <see cref="Click"/> : un item de liste n'a pas de hwnd
        /// propre — <see cref="Click"/> retombe alors sur SelectionItemPattern.Select()
        /// qui ne fait que SÉLECTIONNER. Deux Select successifs ≠ double-clic :
        /// l'event de launch ne part jamais (RETAUD surligné mais jamais ouvert).
        /// </summary>
        /// <param name="item">L'item de liste à lancer (doit exposer SelectionItemPattern).</param>
        /// <param name="listContainer">La ListView conteneur (doit avoir un hwnd natif).</param>
        public static void ActivateListItem(AutomationElement item, AutomationElement listContainer)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (listContainer == null) throw new ArgumentNullException(nameof(listContainer));

            // 1) Sélectionne l'item : le handler Enter lit listContainer.SelectedItems.
            if (item.Patterns.SelectionItem.IsSupported)
                item.Patterns.SelectionItem.Pattern.Select();
            else
                throw new InvalidOperationException(
                    $"Interaction.ActivateListItem : item sans SelectionItemPattern ('{Safe(() => item.Name)}').");

            // 2) Poste Entrée sur le hwnd de la ListView conteneur → KeyDown → launch.
            IntPtr hwnd = NativeHandleOf(listContainer);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"Interaction.ActivateListItem : conteneur sans hwnd natif ('{Safe(() => listContainer.Name)}').");
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)0x00000001);
            PostMessage(hwnd, WM_KEYUP,   (IntPtr)VK_RETURN, unchecked((IntPtr)(int)0xC0000001));
        }

        /// <summary>Saisit du texte : ValuePattern.SetValue, sinon WM_SETTEXT.</summary>
        public static void SetText(AutomationElement element, string text)
        {
            if (element.Patterns.Value.IsSupported)
            {
                element.Patterns.Value.Pattern.SetValue(text ?? "");
                return;
            }
            IntPtr hwnd = NativeHandleOf(element);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Interaction.SetText : élément sans ValuePattern ni hwnd " +
                    $"(Name='{Safe(() => element.Name)}').");
            SendMessage(hwnd, WM_SETTEXT, IntPtr.Zero, text ?? "");
        }

        /// <summary>Sélectionne une ligne/cellule : SelectionItemPattern, sinon Click.</summary>
        public static void Select(AutomationElement element)
        {
            if (element.Patterns.SelectionItem.IsSupported)
            { element.Patterns.SelectionItem.Pattern.Select(); return; }
            Click(element);
        }

        /// <summary>Poste une touche (WM_KEYDOWN+UP) sur le hwnd de l'élément.</summary>
        public static void PressKey(AutomationElement element, int virtualKey)
        {
            IntPtr hwnd = NativeHandleOf(element);
            // Si l'élément n'a pas de hwnd propre (toolbar item, control WPF custom),
            // walk-up pour trouver un ancêtre avec hwnd natif (la Window parent).
            if (hwnd == IntPtr.Zero)
            {
                var walker = element;
                for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++)
                {
                    try { walker = walker.Parent; }
                    catch { break; }
                    if (walker == null) break;
                    hwnd = NativeHandleOf(walker);
                }
            }
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Interaction.PressKey : élément sans hwnd natif (ni dans ancêtres).");
            PressKey(hwnd, virtualKey);
        }

        /// <summary>
        /// Surcharge : poste une touche (WM_KEYDOWN+UP) **directement sur un hwnd**.
        /// Utilisé pour cibler un hwnd Window sans passer par un AutomationElement.
        /// Le WM_KEYDOWN va au focused element dans cette window (via WPF InputManager
        /// ou WinForms message loop). 100 % mouse-free ET focus-free (pas de SendInput).
        /// </summary>
        public static void PressKey(IntPtr hwnd, int virtualKey)
        {
            if (hwnd == IntPtr.Zero)
                throw new ArgumentException("hwnd nul", nameof(hwnd));
            // lParam WM_KEYDOWN : bits 0-15 = repeat count (1). WM_KEYUP : transition+up flags (0xC0000001).
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)virtualKey, (IntPtr)0x00000001);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)virtualKey, unchecked((IntPtr)(int)0xC0000001));
        }

        /// <summary>Poste ENTRÉE sur le hwnd de l'élément.</summary>
        public static void PressEnter(AutomationElement element) => PressKey(element, VK_RETURN);

        /// <summary>Poste ESCAPE sur le hwnd de l'élément (ferme dropdown / annule).</summary>
        public static void PressEscape(AutomationElement element) => PressKey(element, VK_ESCAPE);

        /// <summary>Poste la touche F12 sur le hwnd de l'élément.</summary>
        public static void PressF12(AutomationElement element) => PressKey(element, VK_F12);

        /// <summary>
        /// Overload thread-safe : capture par hwnd cache. A utiliser depuis un thread
        /// background (Timer callback) qui ne peut pas faire d'appels UIA cross-thread
        /// vers l'AutomationElement de l'HDESK. Cache le hwnd au demarrage du loop
        /// (cote thread attache HDESK), puis cette overload PrintWindow direct.
        /// </summary>
        public static void CaptureWindowByHwnd(IntPtr hwnd, string pngPath)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd zero", nameof(hwnd));
            // GetWindowRect en client-space via P/Invoke direct (pas d'UIA call).
            var nativeRect = new NATIVE_RECT();
            if (!GetWindowRect(hwnd, out nativeRect))
                throw new InvalidOperationException($"CaptureWindowByHwnd : GetWindowRect a echoue pour hwnd=0x{hwnd.ToInt64():X}");
            int w = Math.Max(1, nativeRect.Right - nativeRect.Left);
            int h = Math.Max(1, nativeRect.Bottom - nativeRect.Top);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(pngPath, ImageFormat.Png);
            }
        }

        /// <summary>
        /// Maximise une fenêtre par hwnd (SW_MAXIMIZE). Sert à agrandir le viewer K-bis
        /// (RigAffichageDoc) avant le self-snap : sur un HDESK la fenêtre s'ouvre petite et la
        /// zone de rendu PDF est minuscule (snap gris). Maximisée, la page remplit la fenêtre et
        /// PrintWindow capture tout le K-bis (vérifié 2026-05-29 : capture nette du contenu).
        /// </summary>
        public static void MaximizeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { ShowWindow(hwnd, 3 /* SW_MAXIMIZE */); } catch { }
        }

        /// <summary>
        /// Fraction de pixels quasi-blancs dans un PNG. Heuristique "la page PDF est rendue" :
        /// sur un HDESK le viewer K-bis peint la page de façon paresseuse → tant qu'elle charge la
        /// zone est grise (~0 % blanc) ; rendue, le papier blanc domine (>30 %). Échantillonne
        /// 1 pixel sur 16 (pas de 4 en x et y) pour rester rapide (~100 ms sur 1080p).
        /// </summary>
        public static double WhitePixelFraction(string pngPath)
        {
            try
            {
                using (var bmp = new Bitmap(pngPath))
                {
                    long white = 0, total = 0;
                    for (int y = 0; y < bmp.Height; y += 4)
                        for (int x = 0; x < bmp.Width; x += 4)
                        {
                            var c = bmp.GetPixel(x, y);
                            total++;
                            if (c.R > 210 && c.G > 210 && c.B > 210) white++;
                        }
                    return total > 0 ? (double)white / total : 0.0;
                }
            }
            catch { return 0.0; }
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NATIVE_RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct NATIVE_RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// Capture la fenêtre/élément via PrintWindow -> PNG. Fonctionne sur un
        /// HDESK non visible (PrintWindow rend indépendamment de la visibilité).
        /// </summary>
        public static void CaptureWindow(AutomationElement element, string pngPath)
        {
            IntPtr hwnd = NativeHandleOf(element);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Interaction.CaptureWindow : élément sans hwnd natif.");
            var r = element.BoundingRectangle;
            int w = Math.Max(1, (int)r.Width);
            int h = Math.Max(1, (int)r.Height);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool printed;
                    try { printed = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                    if (!printed)
                        Console.WriteLine($"      ⚠ Interaction.CaptureWindow : PrintWindow a échoué (capture potentiellement noire) -> {pngPath}");
                }
                bmp.Save(pngPath, ImageFormat.Png);
            }
        }

        /// <summary>
        /// Click absolu en coords ÉCRAN : trouve le hwnd réel à cette position
        /// (WindowFromPoint), convertit en coords client, et poste WM_LBUTTONDOWN+UP.
        /// Bypass complet de FlaUI/UIA — utile pour cellules DataGridView/RigGrid
        /// dont les patterns UIA sont défaillants (Toggle/Invoke throw, walker.Parent throw).
        /// </summary>
        /// <param name="screenX">X absolu écran (centre cellule)</param>
        /// <param name="screenY">Y absolu écran (centre cellule)</param>
        /// <param name="expectedProcessId">Si > 0, vérifie que le hwnd trouvé appartient au bon process (anti hit-test sur fenêtre d'un autre process).</param>
        /// <returns>hwnd ciblé (IntPtr.Zero si introuvable ou mauvais process)</returns>
        public static IntPtr ClickAtScreenPoint(int screenX, int screenY, int expectedProcessId = 0, bool doubleClick = false)
        {
            var pt = new POINT { X = screenX, Y = screenY };
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (expectedProcessId > 0)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != (uint)expectedProcessId) return IntPtr.Zero;
            }
            var client = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(hwnd, ref client)) return IntPtr.Zero;
            IntPtr lParam = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
            if (doubleClick)
            {
                // 2nd click rapide pour générer WM_LBUTTONDBLCLK (DGV CheckBoxCell toggle).
                // WM_LBUTTONDBLCLK est synthétisé par Windows lui-même quand 2 clicks arrivent
                // dans la fenêtre de temps system double-click. Mais Windows ne fait ça que
                // pour SendInput, pas PostMessage. On envoie donc explicitement WM_LBUTTONDBLCLK.
                PostMessage(hwnd, WM_LBUTTONDBLCLK, (IntPtr)MK_LBUTTON, lParam);
                PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
            }
            return hwnd;
        }

        /// <summary>
        /// Clic DROIT en coords ÉCRAN : poste WM_RBUTTONDOWN+UP → déclenche le MouseDown(Right)
        /// WinForms qui ouvre le ContextMenuStrip (cf. rdgvDemandes_MouseDown côté RIG). Le menu
        /// apparaît près du point cliqué. Bypass FlaUI/UIA comme <see cref="ClickAtScreenPoint"/>.
        /// </summary>
        public static IntPtr RightClickAtScreenPoint(int screenX, int screenY, int expectedProcessId = 0)
        {
            var pt = new POINT { X = screenX, Y = screenY };
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (expectedProcessId > 0)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != (uint)expectedProcessId) return IntPtr.Zero;
            }
            var client = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(hwnd, ref client)) return IntPtr.Zero;
            IntPtr lParam = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
            PostMessage(hwnd, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, lParam);
            PostMessage(hwnd, WM_RBUTTONUP, IntPtr.Zero, lParam);
            return hwnd;
        }

        /// <summary>
        /// Ouvre le ContextMenuStrip d'un contrôle WinForms en postant WM_CONTEXTMENU avec des coords
        /// ÉCRAN explicites. Control.WmContextMenu affiche le ContextMenuStrip assigné À CETTE POSITION,
        /// indépendamment du curseur réel (contrairement à WM_RBUTTON* / cursor-dependent). Déterministe.
        /// </summary>
        /// <returns>hwnd ciblé (la grille), ou IntPtr.Zero si le point n'appartient pas au bon process.</returns>
        public static IntPtr PostContextMenuAtScreenPoint(int screenX, int screenY, int expectedProcessId = 0)
        {
            var pt = new POINT { X = screenX, Y = screenY };
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (expectedProcessId > 0)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != (uint)expectedProcessId) return IntPtr.Zero;
            }
            // lParam = coords ÉCRAN (LOWORD=x, HIWORD=y). wParam = hwnd source (convention WM_CONTEXTMENU).
            IntPtr lParam = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
            PostMessage(hwnd, WM_CONTEXTMENU, hwnd, lParam);
            return hwnd;
        }

        /// <summary>
        /// VRAI clic souris injecté (SetCursorPos + mouse_event). ⚠ À N'UTILISER QUE depuis un thread
        /// attaché à un HDESK ISOLÉ (RigDesktop.RunAttached) : l'injection cible le desktop du thread
        /// appelant. Sur le Default desktop, cela volerait la souris de l'utilisateur. Nécessaire pour
        /// ouvrir les ContextMenuStrip WinForms que PostMessage (WM_RBUTTON/WM_CONTEXTMENU) n'ouvre pas.
        /// </summary>
        public static void RealMouseClick(int screenX, int screenY, bool rightButton)
        {
            SetCursorPos(screenX, screenY);
            System.Threading.Thread.Sleep(50);
            if (rightButton)
            {
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            }
            else
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
        }

        /// <summary>VRAIE frappe clavier injectée (keybd_event down+up). Mêmes contraintes HDESK que
        /// <see cref="RealMouseClick"/>. Sert ex. à fermer un menu (Escape) après injection souris.</summary>
        public static void RealKeyPress(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>VRAIE frappe Ctrl+touche injectée (keybd_event, Ctrl maintenu). Nécessaire pour les
        /// raccourcis applicatifs qui testent <c>GetKeyState(VK_CONTROL)</c> (ex. Ctrl+F7 « copier une adresse »
        /// du champ adresse RIG) — un WM_KEYDOWN POSTÉ ne met PAS à jour l'état clavier. Contraintes HDESK
        /// identiques à <see cref="RealMouseClick"/> (thread attaché).</summary>
        public static void RealCtrlKeyPress(byte vk)
        {
            const byte VK_CONTROL_LOCAL = 0x11;
            keybd_event(VK_CONTROL_LOCAL, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(40); // sleep-ok: maintien Ctrl inter-touches (combo clavier réel), aucune condition à poller
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(40); // sleep-ok: relâche Ctrl après la touche (combo clavier réel)
            keybd_event(VK_CONTROL_LOCAL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>Positionne le curseur (SetCursorPos SEUL, sans injection de bouton). Contrairement à
        /// mouse_event, SetCursorPos met à jour la position de curseur INTERNE du desktop du thread
        /// appelant (lisible via Control.MousePosition/GetCursorPos) même sur un HDESK non-input. Permet
        /// au handler RIG MouseDown(Right) — qui ouvre le menu à MousePosition — de le positionner
        /// sur la cellule, alors que le WM_RBUTTONDOWN posté (message) déclenche réellement le handler.</summary>
        public static bool MoveCursor(int screenX, int screenY) => SetCursorPos(screenX, screenY);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessagePtr(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Position courante du curseur du desktop du thread appelant (GetCursorPos).</summary>
        public static bool TryGetCursor(out int x, out int y)
        {
            if (GetCursorPos(out POINT p)) { x = p.X; y = p.Y; return true; }
            x = 0; y = 0; return false;
        }

        /// <summary>
        /// Clic "case peinte" HDESK-compatible : le WndProc RIG (Ult_Container) toggle sa case en lisant
        /// Control.MousePosition (curseur RÉEL du desktop), pas le lParam. Donc : AttachThreadInput au
        /// thread UI cible (partage d'état d'input), SetCursorPos sur la case, READBACK GetCursorPos
        /// (diagnostic), puis SendMessage SYNCHRONE WM_LBUTTONDOWN/UP au hwnd exact de l'élément —
        /// le handler s'exécute pendant que le curseur est encore posé. Retourne un diag loggable.
        /// </summary>
        public static string ClickPaintedCheckBox(AutomationElement element, int screenX, int screenY)
        {
            IntPtr hwnd = NativeHandleOf(element);
            if (hwnd == IntPtr.Zero) return "hwnd=0 (élément sans handle natif)";
            uint targetThread = GetWindowThreadProcessId(hwnd, out _);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            if (targetThread != 0 && targetThread != myThread)
            {
                try { attached = AttachThreadInput(myThread, targetThread, true); } catch { }
            }
            try
            {
                bool setOk = SetCursorPos(screenX, screenY);
                GetCursorPos(out POINT rb);
                POINT client = new POINT { X = screenX, Y = screenY };
                ScreenToClient(hwnd, ref client);
                IntPtr lParam = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
                SendMessagePtr(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
                SendMessagePtr(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
                return $"setOk={setOk} readback=({rb.X},{rb.Y}) cible=({screenX},{screenY}) attach={attached}";
            }
            finally
            {
                if (attached) { try { AttachThreadInput(myThread, targetThread, false); } catch { } }
            }
        }

        /// <summary>
        /// Poste une touche clavier (VK_*) sur un hwnd : WM_KEYDOWN puis WM_KEYUP avec
        /// le lParam standard (repeat=1, scan=0, ext=0, ctx=0, prev=0, transition=0
        /// pour KEYDOWN ; transition=1 + prev=1 pour KEYUP).
        /// </summary>
        public static void PostKey(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd zero", nameof(hwnd));
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vkCode, (IntPtr)0x00000001);
            PostMessage(hwnd, WM_KEYUP,   (IntPtr)vkCode, unchecked((IntPtr)(int)0xC0000001));
        }

        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>Comme <see cref="PostKey"/> mais avec le SCAN CODE réel (MapVirtualKey) dans le lParam
        /// (bits 16-23). Nécessaire pour les contrôles qui traduisent la touche via <c>ToAscii</c> (ex. le combo
        /// C++ RIG <c>CEditCombo.OnKeyDown</c>) : sans scan code, ToAscii ne traduit PAS les chiffres ('9' perdu,
        /// bug res16). Poste WM_KEYDOWN + WM_KEYUP.</summary>
        /// <summary>Expose PostMessage (usage ciblé : WM_CLOSE d'un viewer externe, remarque #1).</summary>
        public static void PostMessagePublic(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd zero", nameof(hwnd));
            PostMessage(hwnd, msg, wParam, lParam);
        }

        /// <summary>Poste un caractère (WM_CHAR) sur un hwnd — voie fiable pour ÉCRIRE dans un EDIT Win32/
        /// WinForms sans focus réel : l'EDIT insère le caractère au caret et notifie EN_CHANGE au parent
        /// (déclenche la logique applicative, ex. « choix de la localité via le code postal »).</summary>
        public static void PostChar(IntPtr hwnd, char ch)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd zero", nameof(hwnd));
            const uint WM_CHAR_LOCAL = 0x0102;
            PostMessage(hwnd, WM_CHAR_LOCAL, (IntPtr)ch, (IntPtr)0x00000001);
        }

        public static void PostKeyScan(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd zero", nameof(hwnd));
            uint scan = MapVirtualKey((uint)vkCode, 0 /* MAPVK_VK_TO_VSC */);
            int downL = unchecked((int)(0x00000001u | (scan << 16)));
            int upL   = unchecked((int)(0xC0000001u | (scan << 16)));
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vkCode, (IntPtr)downL);
            PostMessage(hwnd, WM_KEYUP,   (IntPtr)vkCode, (IntPtr)upL);
        }

        /// <summary>
        /// Poste un raccourci ALT+touche (mnémonique WinForms, ex Alt+R pour "&amp;Réclamer") sur un hwnd via
        /// WM_SYSKEYDOWN/WM_SYSKEYUP. Le bit 29 du lParam (contexte ALT) DOIT être à 1 pour que la message loop
        /// WinForms traite l'accélérateur (sinon le caractère est ignoré). Séquence : SYSKEYDOWN(ALT) →
        /// SYSKEYDOWN(touche, ctx ALT) → SYSKEYUP(touche, ctx ALT) → KEYUP(ALT). 100 % message-based (pas de
        /// SendInput global) → ne vole pas le focus utilisateur. Best-effort sur un HDESK (le focus clavier
        /// n'y est pas garanti) — d'où l'usage en FALLBACK du clic direct du bouton.
        /// </summary>
        public static void PostAltKey(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd zero", nameof(hwnd));
            // lParam avec bit 29 (0x20000000) = contexte ALT enfoncé ; bit 0 = repeat=1.
            IntPtr downCtxAlt = unchecked((IntPtr)(int)0x20000001);
            // KEYUP : bits 31 (transition) + 30 (prev down) + 29 (ALT ctx) + repeat.
            IntPtr upCtxAlt   = unchecked((IntPtr)(int)0xE0000001);
            const int VK_MENU_LOCAL = 0x12; // ALT
            PostMessage(hwnd, WM_SYSKEYDOWN, (IntPtr)VK_MENU_LOCAL, (IntPtr)0x20000001);
            PostMessage(hwnd, WM_SYSKEYDOWN, (IntPtr)vkCode,        downCtxAlt);
            PostMessage(hwnd, WM_SYSKEYUP,   (IntPtr)vkCode,        upCtxAlt);
            PostMessage(hwnd, WM_KEYUP,      (IntPtr)VK_MENU_LOCAL, unchecked((IntPtr)(int)0xC0000001));
        }

        /// <summary>
        /// Force la mise en foreground d'une fenêtre cross-process. Combine plusieurs
        /// techniques pour passer les bouncers de SetForegroundWindow Win10+ :
        ///   1. Si la fenêtre est déjà foreground → return true immédiatement
        ///   2. ALT key tap (donne au calling process la permission temporaire de Steal le foreground)
        ///   3. AttachThreadInput au thread du window cible
        ///   4. ShowWindow SW_RESTORE + BringWindowToTop + SetForegroundWindow
        ///   5. Vérification finale via GetForegroundWindow
        /// </summary>
        /// <returns>true si la window est maintenant foreground.</returns>
        public static bool ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // Check si déjà foreground
                if (GetForegroundWindow() == hwnd) return true;

                // ALT key tap : Windows considère que mon process a fait une "user-initiated"
                // action et autorise SetForegroundWindow. C'est le hack standard documenté
                // par Raymond Chen et utilisé par AutoHotkey/Sikuli.
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                uint myThread = GetCurrentThreadId();
                uint targetThread = GetWindowThreadProcessId(hwnd, out _);
                if (targetThread == 0) return false;

                if (myThread != targetThread)
                    AttachThreadInput(myThread, targetThread, true);

                try
                {
                    // PAS de ShowWindow ici : SW_RESTORE peut réduire une window maximisée
                    // à sa "normal size" et casser les coords UIA déjà capturées.
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (myThread != targetThread)
                        AttachThreadInput(myThread, targetThread, false);
                }

                // Verify
                System.Threading.Thread.Sleep(100);
                return GetForegroundWindow() == hwnd;
            }
            catch { return false; }
        }

        /// <summary>
        /// Renvoie le hwnd ancêtre racine (top-level window) d'un hwnd enfant.
        /// Utile quand WindowFromPoint retourne un control interne et qu'on veut
        /// la fenêtre top-level pour SetForegroundWindow.
        /// </summary>
        public static IntPtr GetTopLevelWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            try { return GetAncestor(hwnd, GA_ROOT); } catch { return hwnd; }
        }

        /// <summary>
        /// AttachThreadInput + SetFocus + détache. Donne le keyboard focus à un
        /// control cross-process SANS avoir besoin de foreground (contournement
        /// du bouncer SetForegroundWindow).
        /// </summary>
        public static bool ForceFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                uint myThread = GetCurrentThreadId();
                uint targetThread = GetWindowThreadProcessId(hwnd, out _);
                if (targetThread == 0) return false;

                bool attached = false;
                if (myThread != targetThread)
                {
                    AttachThreadInput(myThread, targetThread, true);
                    attached = true;
                }
                try
                {
                    SetFocus(hwnd);
                    return true;
                }
                finally
                {
                    if (attached) AttachThreadInput(myThread, targetThread, false);
                }
            }
            catch { return false; }
        }

        private static IntPtr NativeHandleOf(AutomationElement element)
        {
            try
            {
                var prop = element.Properties.NativeWindowHandle;
                if (prop.IsSupported) return prop.ValueOrDefault;
            }
            catch { }
            return IntPtr.Zero;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return "?"; }
        }
    }
}
