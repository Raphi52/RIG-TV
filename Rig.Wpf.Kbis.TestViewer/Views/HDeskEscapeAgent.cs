// HDeskEscapeAgent — agent thread vivant DANS un HDESK isolé pour intercepter
// Ctrl+Alt+Backspace et SwitchDesktop back to Default.
//
// RegisterHotKey est scope au desktop dans lequel vit le hwnd. Pour intercepter
// Ctrl+Alt+Backspace pendant que l'user est sur le HDESK isolé, il faut une
// fenêtre qui VIT sur ce HDESK. On la crée dans un thread dédié via SetThreadDesktop.
//
// Extrait de LiveViewerWindow.xaml.cs (nested private class) vers fichier top-level
// pour être partagé avec HDeskEscapeHelper (FocusView → Ouvrir HDESK).
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rig.Wpf.Kbis.TestViewer.Views
{
    internal sealed class HDeskEscapeAgent
    {
        // ── Win32 P/Invokes ──
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SwitchDesktop(IntPtr hDesktop);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern bool TranslateMessage([In] ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int pt_x; public int pt_y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public uint cbSize; public uint style;
            [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
            public int cbClsExtra; public int cbWndExtra;
            public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_HOTKEY = 0x0312;
        private const int WM_CLOSE = 0x0010;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
        private const uint VK_BACK = 0x08;
        private const int HOTKEY_ID = 9001;

        // ── State ──
        private readonly IntPtr _hRig;
        private readonly IntPtr _hDefault;
        private Thread? _thread;
        private IntPtr _agentHwnd;
        private string _className = "";
        private WndProcDelegate? _wndProcPin; // garde la delegate vivante (anti-GC)
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        /// <summary>Null si l'agent a démarré correctement, sinon message d'erreur Win32.</summary>
        public string? StartupError { get; private set; }

        public HDeskEscapeAgent(IntPtr hRig, IntPtr hDefault) { _hRig = hRig; _hDefault = hDefault; }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "HDeskEscapeAgent" };
            _thread.Start();
        }

        /// <summary>True ssi l'agent a complété TOUT le setup (SetThreadDesktop + window + RegisterHotKey).</summary>
        public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout) && StartupError == null;

        public void Stop()
        {
            // Demande clean exit via WM_CLOSE. La WndProc switchera back si nécessaire.
            if (_agentHwnd != IntPtr.Zero) PostMessage(_agentHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(1));
        }

        private void Run()
        {
            IntPtr hInst = IntPtr.Zero;
            bool classRegistered = false;
            bool hotkeyRegistered = false;
            try
            {
                if (!SetThreadDesktop(_hRig))
                {
                    StartupError = $"SetThreadDesktop a echoue (Win32 err={Marshal.GetLastWin32Error()}). Manque-t-il DESKTOP_HOOKCONTROL dans OpenDesktop ?";
                    return;
                }
                _className = "RigKbisHDeskEsc_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                _wndProcPin = (hwnd, msg, wp, lp) =>
                {
                    if (msg == WM_HOTKEY || msg == WM_CLOSE)
                    {
                        SwitchDesktop(_hDefault);
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                    }
                    return DefWindowProc(hwnd, msg, wp, lp);
                };
                hInst = GetModuleHandle(null);
                var wcx = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = _wndProcPin,
                    hInstance = hInst,
                    lpszClassName = _className
                };
                if (RegisterClassEx(ref wcx) == 0)
                {
                    StartupError = $"RegisterClassEx a echoue (Win32 err={Marshal.GetLastWin32Error()}). Manque-t-il DESKTOP_WRITEOBJECTS ?";
                    return;
                }
                classRegistered = true;
                _agentHwnd = CreateWindowEx(0, _className, "HDeskEsc", 0, 0, 0, 0, 0,
                    HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);
                if (_agentHwnd == IntPtr.Zero)
                {
                    StartupError = $"CreateWindowEx a echoue (Win32 err={Marshal.GetLastWin32Error()}). Manque-t-il DESKTOP_CREATEWINDOW ?";
                    return;
                }
                if (!RegisterHotKey(_agentHwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_BACK))
                {
                    StartupError = $"RegisterHotKey a echoue (Win32 err={Marshal.GetLastWin32Error()})";
                    DestroyWindow(_agentHwnd);
                    _agentHwnd = IntPtr.Zero;
                    return;
                }
                hotkeyRegistered = true;
            }
            finally
            {
                // Tjs signaler le caller, qu'il y ait succes ou echec. Le caller distingue via StartupError.
                _ready.Set();
            }

            // Message pump. GetMessage return 0 sur WM_QUIT (posté par WndProc après WM_HOTKEY ou WM_CLOSE).
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            // Cleanup ordonné.
            if (hotkeyRegistered && _agentHwnd != IntPtr.Zero) UnregisterHotKey(_agentHwnd, HOTKEY_ID);
            if (_agentHwnd != IntPtr.Zero) { DestroyWindow(_agentHwnd); _agentHwnd = IntPtr.Zero; }
            if (classRegistered) UnregisterClass(_className, hInst);
        }
    }
}
