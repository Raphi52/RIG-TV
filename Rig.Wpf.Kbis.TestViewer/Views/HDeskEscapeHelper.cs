// HDeskEscapeHelper — switch sur un HDESK isolé + active l'agent Ctrl+Alt+Backspace
// pour revenir. Logique extraite de LiveViewerWindow.xaml.cs.BtnOpenHdesk_Click pour
// réutilisation depuis FocusView (zone Focus tab Smoke Import).
//
// Réutilise le HDeskEscapeAgent partagé (Views/HDeskEscapeAgent.cs).
using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Rig.Wpf.Kbis.TestViewer.Views
{
    public static class HDeskEscapeHelper
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SwitchDesktop(IntPtr hDesktop);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        // Acces desktop : DESKTOP_SWITCHDESKTOP (SwitchDesktop), DESKTOP_HOOKCONTROL
        // (SetThreadDesktop dans l'agent), DESKTOP_CREATEWINDOW + DESKTOP_WRITEOBJECTS
        // (creer fenetre+classe sur ce HDESK), DESKTOP_READOBJECTS + DESKTOP_ENUMERATE.
        // Sans ces flags, SetThreadDesktop renvoie silencieusement false → trap.
        private const uint DESKTOP_READOBJECTS   = 0x0001;
        private const uint DESKTOP_CREATEWINDOW  = 0x0002;
        private const uint DESKTOP_HOOKCONTROL   = 0x0008;
        private const uint DESKTOP_ENUMERATE     = 0x0040;
        private const uint DESKTOP_WRITEOBJECTS  = 0x0080;
        private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        private const uint DESKTOP_FULL_ACCESS = DESKTOP_SWITCHDESKTOP | DESKTOP_HOOKCONTROL
            | DESKTOP_CREATEWINDOW | DESKTOP_WRITEOBJECTS | DESKTOP_READOBJECTS | DESKTOP_ENUMERATE;

        /// <summary>
        /// Confirme + ouvre le HDESK + démarre l'agent escape + SwitchDesktop.
        /// Si l'agent ne peut pas démarrer, abort + MessageBox (jamais trap l'user
        /// sans hotkey actif). Ctrl+Alt+Backspace pour revenir Default.
        /// </summary>
        public static void SwitchToHDesk(string desktopName)
        {
            if (string.IsNullOrEmpty(desktopName))
            {
                MessageBox.Show("HDESK name absent (mode A actif ?). Impossible de switch.",
                    "Switch HDESK", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var confirm = MessageBox.Show(
                $"Switch sur HDESK '{desktopName}' ?\n\n" +
                "Tu vas voir RIG en plein ecran et pouvoir interagir avec.\n" +
                "Ctrl+Alt+Backspace pour revenir Default.",
                "Switch HDESK", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            // DESKTOP_FULL_ACCESS au lieu de DESKTOP_SWITCHDESKTOP seul : sinon SetThreadDesktop
            // dans l'agent thread renvoie silencieusement false et le user se retrouve coince.
            IntPtr hDefault = OpenDesktop("Default", 0, false, DESKTOP_FULL_ACCESS);
            IntPtr hRig = OpenDesktop(desktopName, 0, false, DESKTOP_FULL_ACCESS);
            if (hRig == IntPtr.Zero || hDefault == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                MessageBox.Show($"OpenDesktop a echoue (Win32 err={err}).",
                    "Switch HDESK", MessageBoxButton.OK, MessageBoxImage.Error);
                if (hRig != IntPtr.Zero) CloseDesktop(hRig);
                if (hDefault != IntPtr.Zero) CloseDesktop(hDefault);
                return;
            }

            // CRITICAL : demarrer l'agent AVANT SwitchDesktop pour que Ctrl+Alt+Backspace soit
            // deja enregistre quand le user atterrit sur le HDESK isole.
            var agent = new HDeskEscapeAgent(hRig, hDefault);
            agent.Start();
            if (!agent.WaitReady(TimeSpan.FromMilliseconds(1500)))
            {
                var why = agent.StartupError ?? "(timeout sans erreur reportée)";
                MessageBox.Show(
                    $"L'agent hotkey n'a pas pu démarrer sur le HDESK '{desktopName}'.\n\n" +
                    $"Raison : {why}\n\n" +
                    "Le switch est annulé pour éviter de te bloquer.",
                    "Switch HDESK", MessageBoxButton.OK, MessageBoxImage.Error);
                agent.Stop();
                CloseDesktop(hRig);
                CloseDesktop(hDefault);
                return;
            }

            if (!SwitchDesktop(hRig))
            {
                int err = Marshal.GetLastWin32Error();
                MessageBox.Show($"SwitchDesktop a echoue (Win32 err={err}).",
                    "Switch HDESK", MessageBoxButton.OK, MessageBoxImage.Error);
                agent.Stop();
                CloseDesktop(hRig);
                CloseDesktop(hDefault);
                return;
            }
            // hRig : CloseDesktop ok — SwitchDesktop a déjà changé le foreground et l'agent
            // thread a fait SetThreadDesktop(hRig) qui retient sa propre reference.
            CloseDesktop(hRig);
            // hDefault : NE PAS fermer. L'agent en a besoin pour SwitchDesktop(hDefault)
            // au moment du Ctrl+Alt+Backspace. Le handle est "leak" pour la durée de vie
            // du switch (jusqu'au retour Default). Coût = 1 handle Win32 jusqu'au prochain
            // GC du process — acceptable.
        }

        /// <summary>Force le retour sur le HDESK "Default" sans nécessiter Ctrl+Alt+Backspace.
        /// Appelé par le bouton ✕ du toolbar LIVE pour ne pas trapper l'user si le HDESK
        /// isolé est sur le point d'être détruit par taskkill du worker. No-op silencieux
        /// si l'user est déjà sur Default (SwitchDesktop renvoie true mais ne change rien).</summary>
        public static void SwitchToDefault()
        {
            IntPtr hDefault = OpenDesktop("Default", 0, false, DESKTOP_SWITCHDESKTOP);
            if (hDefault == IntPtr.Zero) return; // best-effort
            try { SwitchDesktop(hDefault); }
            finally { CloseDesktop(hDefault); }
        }
    }
}
