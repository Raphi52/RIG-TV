using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Contrôle d'un ARBRE de process Windows (net48, sans System.Management) :
///   - <see cref="KillTree"/>   : Stop = tue le process + tous ses descendants
///   - <see cref="SuspendTree"/>/<see cref="ResumeTree"/> : Pause/Reprise =
///     gèle/dégèle INSTANTANÉMENT toute la lignée (SmokeRunner → RigClientAccueil
///     → RaptureImportDiag → …) via NtSuspendProcess/NtResumeProcess (ntdll).
///
/// La lignée est reconstruite par snapshot Toolhelp (kernel32), donc aucune
/// dépendance externe. Suspendre = suspendre tous les threads de chaque process
/// de l'arbre : une automatisation FlaUI synchrone + la fenêtre RIG sont gelées
/// pile où elles en sont, et reprennent à l'identique au Resume.
/// </summary>
internal static class ProcessTreeControl
{
    // ── Toolhelp snapshot (énumération process + parent) ────────────────────
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ── Suspend/Resume (process-level, via ntdll) ───────────────────────────
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("ntdll.dll")] private static extern uint NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern uint NtResumeProcess(IntPtr processHandle);

    /// <summary>
    /// Renvoie le PID racine + tous ses descendants (transitifs), à partir d'un
    /// snapshot unique de la table des process. Inclut toujours <paramref name="rootPid"/>.
    /// </summary>
    private static List<int> DescendantsAndSelf(int rootPid)
    {
        var result = new List<int> { rootPid };
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var byParent = new Dictionary<int, List<int>>();
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref pe))
            {
                do
                {
                    int pid = (int)pe.th32ProcessID;
                    int ppid = (int)pe.th32ParentProcessID;
                    if (!byParent.TryGetValue(ppid, out var kids)) { kids = new List<int>(); byParent[ppid] = kids; }
                    kids.Add(pid);
                } while (Process32NextW(snap, ref pe));
            }
            // BFS depuis la racine.
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (byParent.TryGetValue(cur, out var kids))
                    foreach (var k in kids)
                        if (seen.Add(k)) { result.Add(k); queue.Enqueue(k); }
            }
        }
        finally { CloseHandle(snap); }
        return result;
    }

    /// <summary>Stop : tue le process et tout son arbre (taskkill /F /T, robuste aux petits-enfants déjà morts).</summary>
    public static void KillTree(int rootPid)
    {
        if (rootPid <= 0) return;
        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {rootPid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var k = Process.Start(psi);
            k?.WaitForExit(8000);
        }
        catch
        {
            // Fallback : kill best-effort process par process.
            foreach (var pid in DescendantsAndSelf(rootPid))
                try { Process.GetProcessById(pid).Kill(); } catch { }
        }
    }

    public static void SuspendTree(int rootPid) => ForEachInTree(rootPid, NtSuspendProcess);
    public static void ResumeTree(int rootPid) => ForEachInTree(rootPid, NtResumeProcess);

    private static void ForEachInTree(int rootPid, Func<IntPtr, uint> op)
    {
        if (rootPid <= 0) return;
        foreach (var pid in DescendantsAndSelf(rootPid))
        {
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, (uint)pid);
            if (h == IntPtr.Zero) continue;
            try { op(h); } catch { } finally { CloseHandle(h); }
        }
    }
}
