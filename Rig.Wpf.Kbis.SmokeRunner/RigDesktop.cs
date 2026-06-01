using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Cycle de vie d'un desktop Windows isolé (objet HDESK) pour exécuter
    /// RigClientAccueil.exe hors du desktop interactif de l'utilisateur. Nom unique
    /// par run -> plusieurs runs parallèles, chacun son HDESK.
    /// headless=false : no-op, tout se passe sur le desktop courant (mode debug).
    /// </summary>
    public sealed class RigDesktop : IDisposable
    {
        private const uint GENERIC_ALL = 0x10000000;
        private const uint STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice,
            IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName, System.Text.StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private readonly bool _headless;
        private IntPtr _hDesktop = IntPtr.Zero;
        private IntPtr _hPrevDesktop = IntPtr.Zero;
        private bool _threadAttached;

        /// <summary>Nom du desktop ("RigSmoke_{runId}"), null si headless=false.</summary>
        public string? DesktopName { get; }

        private RigDesktop(bool headless, string? desktopName, IntPtr hDesktop)
        {
            _headless = headless;
            DesktopName = desktopName;
            _hDesktop = hDesktop;
        }

        /// <summary>
        /// Crée le desktop isolé. runId rend le nom unique. headless=false ->
        /// RigDesktop no-op (desktop courant).
        /// </summary>
        public static RigDesktop Create(bool headless, string runId)
        {
            if (!headless)
                return new RigDesktop(false, null, IntPtr.Zero);

            string name = "RigSmoke_" + runId;
            IntPtr h = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"CreateDesktop('{name}') a échoué (Win32 error {Marshal.GetLastWin32Error()}).");
            return new RigDesktop(true, name, h);
        }

        /// <summary>
        /// Attache le thread courant au desktop isolé. À appeler AVANT
        /// new UIA3Automation() sinon FlaUI n'énumère pas le HDESK. No-op si headless=false.
        /// </summary>
        public void AttachCurrentThread()
        {
            if (!_headless) return;
            if (_threadAttached) return;
            _hPrevDesktop = GetThreadDesktop(GetCurrentThreadId());
            if (!SetThreadDesktop(_hDesktop))
                throw new InvalidOperationException(
                    $"SetThreadDesktop a échoué (Win32 error {Marshal.GetLastWin32Error()}).");
            _threadAttached = true;
        }

        /// <summary>
        /// Lance un process sur le desktop isolé (STARTUPINFO.lpDesktop). Sur
        /// headless=false, délègue à Process.Start classique.
        /// </summary>
        public Process LaunchProcess(string exePath, string workingDir)
        {
            if (!_headless)
            {
                return Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                });
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = DesktopName!; // non-null garanti : ce chemin n'est atteint que si _headless=true
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_SHOW;

            var cmdLine = new System.Text.StringBuilder("\"" + exePath + "\"");
            bool ok = CreateProcess(
                exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                0, IntPtr.Zero, workingDir, ref si, out var pi);
            if (!ok)
                throw new InvalidOperationException(
                    $"CreateProcess('{exePath}') sur desktop '{DesktopName}' a échoué " +
                    $"(Win32 error {Marshal.GetLastWin32Error()}).");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            try
            {
                return Process.GetProcessById(pi.dwProcessId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"CreateProcess a réussi (PID={pi.dwProcessId}) mais le process n'a pas pu " +
                    $"être récupéré ({ex.GetType().Name}) — probablement déjà terminé.", ex);
            }
        }

        /// <summary>
        /// Exécute <paramref name="body"/> sur un thread STA DÉDIÉ qui s'attache d'abord au desktop
        /// isolé. SetThreadDesktop ne peut pas déplacer un thread qui possède déjà
        /// une fenêtre (fenêtre OLE de l'apartment STA) -> on utilise un thread NEUF
        /// qui appelle SetThreadDesktop avant toute init COM. Bloque jusqu'à la fin.
        /// </summary>
        public void RunAttached(Action body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            Exception captured = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    AttachCurrentThread();   // SetThreadDesktop sur ce thread NEUF -> clean -> OK
                    body();
                }
                catch (Exception ex) { captured = ex; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = false;
            t.Start();
            t.Join();
            if (captured != null)
                throw new InvalidOperationException(
                    "RigDesktop.RunAttached : l'action sur le thread desktop a échoué : " + captured.Message,
                    captured);
        }

        public void Dispose()
        {
            // Le thread de RunAttached est déjà mort (Join) -> pas de restauration de
            // thread desktop nécessaire. On ferme juste le HDESK.
            if (_hDesktop != IntPtr.Zero)
            {
                try { CloseDesktop(_hDesktop); }
                catch (Exception ex)
                {
                    Console.WriteLine($"      ⚠ RigDesktop.Dispose : CloseDesktop a échoué : {ex.Message}");
                }
                _hDesktop = IntPtr.Zero;
            }
        }
    }
}
