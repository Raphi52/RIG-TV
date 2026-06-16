# Driver de tests input-free + parallélisable — Plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rendre le driver de tests Rig Testing input-free (zéro synthèse d'input global) et parallélisable, pour qu'un run ne monopolise plus souris/clavier et que plusieurs runs tournent en concurrence.

**Architecture:** 3 unités nouvelles dans `Rig.Wpf.Kbis.SmokeRunner` — `AudienceLock` (verrou cross-process par audience), `Interaction` (clics/saisies via patterns UIA + messages fenêtre postés), `RigDesktop` (desktop Windows isolé HDESK). `LegacyDriver` est converti pour ne plus utiliser FlaUI `.Click()`/`Mouse.*`/`Keyboard.*`. Le batch « All scenarios » du TestViewer passe à un pool de N workers.

**Tech Stack:** .NET Framework 4.8, C#, FlaUI/UIA3, Win32 P/Invoke (`user32.dll`, `kernel32.dll`), xUnit (`Rig.Rapture.Tests`).

**Spec source:** `docs/superpowers/specs/2026-05-21-input-free-parallel-driver-design.md`

---

## Contraintes repo (CLAUDE.md)

- **Règle 6 — pas de commit sans « go ».** Chaque étape « Commit » ci-dessous est précédée d'un **⚠ PAUSE** : ne pas exécuter le `git commit` sans autorisation explicite de l'utilisateur dans le chat. Présenter le `git status`/`git diff`, attendre « go ».
- **Règle 2 — builds.** Builds isolés via `dotnet build` (ces projets WPF/console .NET 4.8 se buildent par `dotnet build`, déjà utilisé dans la session). Sur exit code non-nul → rapport d'échec structuré avant toute autre action.
- **Règle 15 — vérification.** Verif finale = build vert → relancer « All scenarios » degré 3 headless → screenshots → analyse vs attendu → non-régression 16/16 + zéro interférence souris.
- **TDD partiel assumé (CLAUDE.md 5.3).** `AudienceLock` = logique isolée → test xUnit. `RigDesktop`/`Interaction`/`LegacyDriver` = interop Win32/FlaUI/UI → exception TDD documentée, vérif par build + smoke.

## Structure de fichiers

| Fichier | Rôle | Action |
|---|---|---|
| `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/AudienceLock.cs` | Verrou nommé cross-process par audience | Créer |
| `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Interaction.cs` | Helper d'interaction input-free | Créer |
| `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/RigDesktop.cs` | Cycle de vie du desktop isolé HDESK | Créer |
| `Source/Wpf/Rig.Rapture.Tests/AudienceLockTests.cs` | Test xUnit de `AudienceLock` | Créer |
| `Source/Wpf/Rig.Rapture.Tests/Rig.Rapture.Tests.csproj` | Ajout `ProjectReference` vers SmokeRunner | Modifier |
| `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs` | Conversion input-free + `Launch()` HDESK | Modifier |
| `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs` | `AudienceLock` autour de snapshot/apply/restore | Modifier |
| `Source/Wpf/Rig.Wpf.Kbis.TestViewer/ViewModels/MainWindowViewModel.cs` | Pool de N workers pour le batch | Modifier |

---

## Task 1: AudienceLock — verrou cross-process par audience

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/AudienceLock.cs`
- Modify: `Source/Wpf/Rig.Rapture.Tests/Rig.Rapture.Tests.csproj`
- Test: `Source/Wpf/Rig.Rapture.Tests/AudienceLockTests.cs`

- [ ] **Step 1: Écrire le test qui échoue**

Créer `Source/Wpf/Rig.Rapture.Tests/AudienceLockTests.cs` :

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Rig.Wpf.Kbis.SmokeRunner;
using Xunit;

namespace Rig.Rapture.Tests
{
    public class AudienceLockTests
    {
        [Fact]
        public void Acquire_meme_audience_pendant_qu_un_lock_est_tenu_throw_TimeoutException()
        {
            int aud = 990001; // ID dédié au test, jamais utilisé en base
            using (AudienceLock.Acquire(aud, TimeSpan.FromSeconds(5)))
            {
                // Un autre thread tente d'acquérir le MÊME id -> doit timeout.
                var task = Task.Run(() =>
                    Assert.Throws<TimeoutException>(() =>
                        AudienceLock.Acquire(aud, TimeSpan.FromMilliseconds(300))));
                Assert.True(task.Wait(TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public void Acquire_audiences_differentes_ne_se_bloquent_pas()
        {
            using (AudienceLock.Acquire(990002, TimeSpan.FromSeconds(2)))
            using (AudienceLock.Acquire(990003, TimeSpan.FromSeconds(2)))
            {
                // Les deux verrous coexistent : aucune exception.
                Assert.True(true);
            }
        }

        [Fact]
        public void Acquire_apres_release_reussit()
        {
            int aud = 990004;
            using (AudienceLock.Acquire(aud, TimeSpan.FromSeconds(2))) { }
            // Le verrou est relâché -> ré-acquisition immédiate OK.
            using (AudienceLock.Acquire(aud, TimeSpan.FromMilliseconds(500))) { }
            Assert.True(true);
        }
    }
}
```

- [ ] **Step 2: Ajouter la ProjectReference dans le csproj de test**

Dans `Source/Wpf/Rig.Rapture.Tests/Rig.Rapture.Tests.csproj`, à l'intérieur du premier `<ItemGroup>` contenant des `<ProjectReference>` (ou en créer un), ajouter :

```xml
    <ProjectReference Include="..\Rig.Wpf.Kbis.SmokeRunner\Rig.Wpf.Kbis.SmokeRunner.csproj" />
```

- [ ] **Step 3: Lancer le test pour vérifier qu'il échoue**

Run: `dotnet build "Source/Wpf/Rig.Rapture.Tests/Rig.Rapture.Tests.csproj" -c Debug`
Expected: ÉCHEC compilation — `AudienceLock` n'existe pas (`CS0246: type or namespace 'AudienceLock' not found`).

- [ ] **Step 4: Créer AudienceLock**

Créer `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/AudienceLock.cs` :

```csharp
using System;
using System.Threading;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Verrou cross-process par audience. Un run pose un Mutex Windows nommé
    /// Global\RigSmokeAud_{id} avant snapshot, le libère après restore. Runs sur
    /// audiences différentes -> parallèles ; même audience -> sérialisés.
    ///
    /// ⚠ Mutex est thread-affine : Acquire et Dispose DOIVENT se faire sur le même
    /// thread. Le flux snapshot/apply/restore de Program.cs est synchrone sur un
    /// seul thread -> contrainte respectée par construction.
    /// </summary>
    public sealed class AudienceLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _acquired;

        private AudienceLock(Mutex mutex)
        {
            _mutex = mutex;
            _acquired = true;
        }

        /// <summary>
        /// Acquiert le verrou de l'audience. Bloque jusqu'à obtention ou timeout.
        /// </summary>
        /// <exception cref="TimeoutException">si le verrou n'est pas obtenu dans le délai.</exception>
        public static AudienceLock Acquire(int audienceId, TimeSpan timeout)
        {
            var name = "Global\\RigSmokeAud_" + audienceId;
            var mutex = new Mutex(false, name);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // Run précédent mort sans libérer -> on hérite du verrou.
                acquired = true;
            }
            if (!acquired)
            {
                mutex.Dispose();
                throw new TimeoutException(
                    $"Audience {audienceId} verrouillée par un autre run depuis plus de " +
                    $"{timeout.TotalMinutes:F0} min.");
            }
            return new AudienceLock(mutex);
        }

        public void Dispose()
        {
            if (_acquired)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _acquired = false;
            }
            _mutex.Dispose();
        }
    }
}
```

- [ ] **Step 5: Lancer les tests pour vérifier qu'ils passent**

Run: `dotnet test "Source/Wpf/Rig.Rapture.Tests/Rig.Rapture.Tests.csproj" --filter "FullyQualifiedName~AudienceLockTests"`
Expected: PASS — 3 tests verts.

- [ ] **Step 6: ⚠ PAUSE — Commit**

Demander l'autorisation explicite de l'utilisateur (CLAUDE.md règle 6). Présenter `git status` + `git diff`. Sur « go » uniquement :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/AudienceLock.cs" "Source/Wpf/Rig.Rapture.Tests/AudienceLockTests.cs" "Source/Wpf/Rig.Rapture.Tests/Rig.Rapture.Tests.csproj"
git commit -m "feat(smokerunner): AudienceLock — verrou cross-process par audience"
```

---

## Task 2: Interaction — helper d'interaction input-free

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Interaction.cs`

Pas de test xUnit (interop Win32 + FlaUI sur fenêtres réelles — exception TDD CLAUDE.md 5.3). Vérif = build + usage en Task 4.

- [ ] **Step 1: Créer Interaction**

Créer `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Interaction.cs` :

```csharp
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
        private const uint WM_SETTEXT     = 0x000C;
        private const uint WM_KEYDOWN     = 0x0100;
        private const uint WM_KEYUP       = 0x0101;
        private const int  MK_LBUTTON     = 0x0001;
        private const int  VK_RETURN      = 0x0D;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

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
            IntPtr hwnd = NativeHandleOf(element);
            if (hwnd == IntPtr.Zero)
            {
                if (element.Patterns.Invoke.IsSupported)
                { element.Patterns.Invoke.Pattern.Invoke(); return; }
                if (element.Patterns.SelectionItem.IsSupported)
                { element.Patterns.SelectionItem.Pattern.Select(); return; }
                throw new InvalidOperationException(
                    "Interaction.Click : élément sans hwnd ni pattern exploitable " +
                    $"(Name='{Safe(() => element.Name)}', " +
                    $"Type='{Safe(() => element.ControlType.ToString())}', " +
                    $"AutomationId='{Safe(() => element.AutomationId)}').");
            }
            var r = element.BoundingRectangle;
            var p = new POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
            ScreenToClient(hwnd, ref p);
            IntPtr lParam = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
        }

        /// <summary>Invoke synchrone (InvokePattern), pour boutons connus sûrs sans modale.</summary>
        public static void Invoke(AutomationElement element)
        {
            if (element.Patterns.Invoke.IsSupported)
            { element.Patterns.Invoke.Pattern.Invoke(); return; }
            Click(element);
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

        /// <summary>Poste ENTRÉE (WM_KEYDOWN+UP VK_RETURN) sur le hwnd de l'élément.</summary>
        public static void PressEnter(AutomationElement element)
        {
            IntPtr hwnd = NativeHandleOf(element);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Interaction.PressEnter : élément sans hwnd natif.");
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
        }

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
                    try { PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(pngPath, ImageFormat.Png);
            }
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
```

- [ ] **Step 2: Vérifier que `System.Drawing` est référencé**

`Interaction.cs` utilise `System.Drawing`. Vérifier dans `Rig.Wpf.Kbis.SmokeRunner.csproj` la présence d'une `<Reference Include="System.Drawing" />` (projet .NET 4.8 classique) OU que c'est un SDK-style projet (auto-référencé). Si absent et projet non-SDK, ajouter dans un `<ItemGroup>` de références :

```xml
    <Reference Include="System.Drawing" />
```

- [ ] **Step 3: Build pour vérifier la compilation**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj" -c Release`
Expected: PASS — 0 erreur (warnings nullable tolérés).

- [ ] **Step 4: ⚠ PAUSE — Commit**

Autorisation explicite requise (règle 6). Sur « go » :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Interaction.cs" "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj"
git commit -m "feat(smokerunner): Interaction — helper d'interaction input-free"
```

---

## Task 3: RigDesktop — desktop Windows isolé

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/RigDesktop.cs`

Pas de test xUnit (interop HDESK + lancement de process — exception TDD). Vérif = build + usage en Task 5.

- [ ] **Step 1: Créer RigDesktop**

Créer `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/RigDesktop.cs` :

```csharp
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
            string lpApplicationName, string lpCommandLine,
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
        public string DesktopName { get; }

        private RigDesktop(bool headless, string desktopName, IntPtr hDesktop)
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
            si.lpDesktop = DesktopName;
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_SHOW;

            string cmdLine = "\"" + exePath + "\"";
            bool ok = CreateProcess(
                exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                0, IntPtr.Zero, workingDir, ref si, out var pi);
            if (!ok)
                throw new InvalidOperationException(
                    $"CreateProcess('{exePath}') sur desktop '{DesktopName}' a échoué " +
                    $"(Win32 error {Marshal.GetLastWin32Error()}).");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return Process.GetProcessById(pi.dwProcessId);
        }

        public void Dispose()
        {
            if (_threadAttached && _hPrevDesktop != IntPtr.Zero)
            {
                try { SetThreadDesktop(_hPrevDesktop); } catch { }
                _threadAttached = false;
            }
            if (_hDesktop != IntPtr.Zero)
            {
                try { CloseDesktop(_hDesktop); } catch { }
                _hDesktop = IntPtr.Zero;
            }
        }
    }
}
```

- [ ] **Step 2: Build pour vérifier la compilation**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj" -c Release`
Expected: PASS — 0 erreur.

- [ ] **Step 3: ⚠ PAUSE — Commit**

Autorisation explicite requise (règle 6). Sur « go » :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/RigDesktop.cs"
git commit -m "feat(smokerunner): RigDesktop — desktop Windows isolé HDESK"
```

---

## Task 4: Convertir LegacyDriver en input-free

Remplacer tout appel synthétisant de l'input global par `Interaction.*`. RIG reste sur
le desktop courant pour cette task (le HDESK arrive en Task 5) — `Interaction`
fonctionne identiquement sur les deux desktops. **Gain immédiat : la souris est libérée
dès cette task**, même sans HDESK, car `PostMessage` ne déplace pas le curseur.

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs`

**Table de conversion** (appliquer partout dans le fichier) :

| Avant | Après |
|---|---|
| `try { x.AsButton().Invoke(); } catch { x.Click(); }` | `Interaction.Click(x);` |
| `x.Click();` | `Interaction.Click(x);` |
| `x.DoubleClick();` | `Interaction.Click(x); Interaction.Click(x);` |
| `try { x.AsTabItem().Select(); } catch { x.Click(); }` | `Interaction.Select(x);` |
| `FlaUI.Core.Input.Mouse.MoveTo(...); ... Mouse.Click(...)` | `Interaction.Click(element);` |
| `FlaUI.Core.Input.Keyboard.Type(path)` (champ fichier) | `Interaction.SetText(fileNameEdit, path);` |
| `FlaUI.Core.Input.Keyboard.Type(VirtualKeyShort.RETURN)` | `Interaction.PressEnter(element);` |
| `FlaUI.Core.Capturing.Capture.Element(e)` | `Interaction.CaptureWindow(e, path);` |
| `recap.SetForeground();` | *(supprimer la ligne)* |

- [ ] **Step 1: Convertir les helpers de clic génériques**

Dans `LegacyDriver.cs`, méthode `ClickButtonByNames` (~ligne 1314) : remplacer le corps du clic :

```csharp
        Console.WriteLine($"      → Click '{SafeText(() => btn.Name)}' dans popup '{SafeText(() => parent.Name)}'");
        Interaction.Click(btn);
        Thread.Sleep(300); // laisse la modale se fermer
```

- [ ] **Step 2: Convertir le clic recap Importer**

Dans `ClickRecapImporterAndConfirm` (~ligne 1271-1286), remplacer le bloc
`recap.SetForeground()` … `Mouse.Click(...)` par :

```csharp
        Console.WriteLine($"      → Click '{importerLabel}' (APPLY mode — écriture base)");
        // Interaction.Click poste WM_LBUTTON* : non-bloquant, ne vole pas la souris,
        // et ne deadlocke pas sur le MessageBox.Show synchrone du handler.
        Interaction.Click(importer);
        Console.WriteLine($"      → Click posté sur '{importerLabel}' — poll popup confirmation");
```

- [ ] **Step 3: Convertir la saisie du chemin dans l'OpenFileDialog**

Dans `ClickImporterRaptureAndOpenJson` (~lignes 807-818), remplacer le bloc
`fileNameEdit.Click()` + `Keyboard.Press/Type` par :

```csharp
        // SetValue remplace le contenu intégral du champ -> pas besoin de Ctrl+A/Delete.
        Interaction.SetText(fileNameEdit, jsonPath);
        Console.WriteLine($"      → Path renseigné via ValuePattern : {jsonPath}");
        Thread.Sleep(300);
```

Puis, plus bas, là où le dialogue est soumis via `Keyboard` ENTER (~ligne 825+), remplacer
par un clic sur le bouton « Ouvrir » du dialogue :

```csharp
        // Soumission : clic sur le bouton "Ouvrir" du file dialog (pas de clavier global).
        var openBtn = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b =>
            {
                var n = SafeText(() => b.Name);
                return n.Equals("Ouvrir", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("&Ouvrir", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Open", StringComparison.OrdinalIgnoreCase);
            });
        if (openBtn is null)
            throw new Exception("Bouton 'Ouvrir' introuvable dans l'OpenFileDialog");
        Interaction.Click(openBtn);
```

- [ ] **Step 4: Convertir les `.Click()` / `.DoubleClick()` / `.Select()` restants**

Parcourir `LegacyDriver.cs` et convertir chaque site selon la table ci-dessus. Sites
connus (numéros indicatifs, vérifier le contexte) : ~186, 320, 329, 355, 363, 401, 415,
423, 435, 453, 461, 474, 741, 884, 1232, 1329, 1400, 1451, 1466, 1498, 1518, 1592, 1596,
1637, 1640, 1665, 1720, 1796, 1822, 1827, 1837, 2108.

Pour les lignes de grille (`targetRow.Click()` ~1592, ~1637) utiliser `Interaction.Select(targetRow)`.
Pour les boutons utiliser `Interaction.Click(...)`. Pour `item.DoubleClick()` (~435, 474)
utiliser deux `Interaction.Click(item)` consécutifs.

- [ ] **Step 5: Convertir les captures d'écran (sous-dossier par run)**

Le spec exige des screenshots dans un sous-dossier `screenshots\{runId}\` pour éviter
les collisions de noms entre runs parallèles. Ajouter d'abord un helper privé à
`LegacyDriver` (près de `CaptureScreenshot`) :

```csharp
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
```

Remplacer le corps de `CaptureScreenshot` par une capture de `_window` via ce helper :

```csharp
    public void CaptureScreenshot(string label, bool fullScreen = false)
    {
        try
        {
            var shotPath = Path.Combine(ScreenshotDir(),
                $"smoke-{label}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            if (_window is not null)
            {
                Interaction.CaptureWindow(_window, shotPath);
                Console.WriteLine($"      📸 Screenshot : {shotPath}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"      ⚠ Screenshot échoué : {ex.Message}"); }
    }
```

(Le paramètre `fullScreen` est conservé pour compat de signature mais ignoré.)

Puis, pour chaque capture inline `FlaUI.Core.Capturing.Capture.Element(x)` (~lignes 944,
996, 1192) : remplacer par `Interaction.CaptureWindow(x, shotPath)` où `shotPath` est
construit via `ScreenshotDir()` (et non plus un `Path.Combine(... "screenshots")` à plat) ;
supprimer le `catch { CaptureScreenshot(..., fullScreen: true); }` (plus de capture plein
écran — elle capturerait le mauvais desktop). Exemple de bloc converti :

```csharp
            try
            {
                Thread.Sleep(900);
                var shotPath = Path.Combine(ScreenshotDir(),
                    $"smoke-rapture-recap-VIEW-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                Interaction.CaptureWindow(popup, shotPath);
                Console.WriteLine($"      📸 Screenshot recap : {shotPath}");
            }
            catch (Exception ex) { Console.WriteLine($"      ⚠ Screenshot recap échoué : {ex.Message}"); }
```

- [ ] **Step 6: Supprimer les forçages de premier plan**

Supprimer toute ligne `SetForeground()`, et tout bloc `AttachThreadInput` /
`SetForegroundWindow` (P/Invoke) du fichier. Supprimer les `using`/`DllImport` devenus
inutilisés (le build signalera les warnings).

- [ ] **Step 7: Build**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj" -c Release`
Expected: PASS — 0 erreur. Si des `Mouse`/`Keyboard`/`Capture` subsistent, le build reste
vert (ce sont des classes valides) mais une **recherche `grep` finale** doit confirmer 0
occurrence de `\.Click()`, `\.DoubleClick()`, `FlaUI.Core.Input.Mouse`, `FlaUI.Core.Input.Keyboard`,
`Capture.Element`, `Capture.Screen`, `SetForeground` dans `LegacyDriver.cs`.

- [ ] **Step 8: Smoke — un scénario, souris libre**

Lancer UN scénario via TestViewer (RAPTURE → Smoke Import → `cas-a-pc-clotures` → Start E2E,
Apply réel coché). Pendant le run : bouger la souris dans une autre fenêtre.
Expected : le scénario passe (`✓ passed`, `✗ failed 0` dans le stdout dumpé) ET la souris
n'est jamais happée par RIG. Lire le screenshot final produit.

- [ ] **Step 9: ⚠ PAUSE — Commit**

Autorisation explicite requise (règle 6). Sur « go » :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs"
git commit -m "refactor(smokerunner): LegacyDriver input-free via Interaction"
```

---

## Task 5: Lancer RIG sur HDESK + flag RIG_DRIVER_HEADLESS

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs`

- [ ] **Step 1: Ajouter les champs HDESK à LegacyDriver**

Dans `LegacyDriver`, près des champs existants (`_app`, `_automation`, `_window`), ajouter :

```csharp
    private RigDesktop _desktop;
    private static bool Headless =>
        (Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") != "0";
```

- [ ] **Step 2: Convertir `Launch()` pour passer par RigDesktop**

Dans la méthode `Launch()`, remplacer le `Process.Start` de `RigClientAccueil.exe` par le
flux RigDesktop. Ordre impératif : `Create` → `AttachCurrentThread` → `LaunchProcess` →
création de `UIA3Automation`. Exemple de structure :

```csharp
    public void Launch()
    {
        string runId = Process.GetCurrentProcess().Id.ToString();
        _desktop = RigDesktop.Create(Headless, runId);
        _desktop.AttachCurrentThread();           // AVANT new UIA3Automation()
        Console.WriteLine($"      → RigDesktop : headless={Headless}, desktop='{_desktop.DesktopName ?? "(courant)"}'");

        var workDir = Path.GetDirectoryName(_rigExePath) ?? Environment.CurrentDirectory;
        _app = FlaUI.Core.Application.Attach(
            _desktop.LaunchProcess(_rigExePath, workDir));
        _automation = new FlaUI.UIA3.UIA3Automation();
        // ... suite inchangée : attente de la fenêtre principale ...
    }
```

Note : si le code actuel fait `FlaUI.Core.Application.Launch(...)`, le remplacer par
`Application.Attach(process)` où `process` vient de `_desktop.LaunchProcess(...)`.
Adapter au code réel de `Launch()`.

- [ ] **Step 3: Libérer le desktop au Dispose**

Dans `Dispose()` de `LegacyDriver`, après le kill du process RIG et la libération de
`_automation`, ajouter en dernier :

```csharp
        try { _desktop?.Dispose(); } catch { }
        _desktop = null;
```

- [ ] **Step 4: Build**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj" -c Release`
Expected: PASS — 0 erreur.

- [ ] **Step 5: Smoke — RIG invisible**

Lancer un scénario via TestViewer (`RIG_DRIVER_HEADLESS` non défini → défaut ON).
Expected : le scénario passe, **aucune fenêtre RIG n'apparaît** sur le desktop, le
screenshot final est produit et lisible (capture PrintWindow du HDESK).
Vérifier aussi le mode debug : env `RIG_DRIVER_HEADLESS=0` → RIG visible sur le desktop normal, scénario passe.

- [ ] **Step 6: ⚠ PAUSE — Commit**

Autorisation explicite requise (règle 6). Sur « go » :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs"
git commit -m "feat(smokerunner): RIG lancé sur desktop HDESK isolé (flag RIG_DRIVER_HEADLESS)"
```

---

## Task 6: AudienceLock autour de snapshot/apply/restore

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs`

- [ ] **Step 1: Encadrer la section critique DB**

Dans `Program.cs`, méthode `RunLegacyRaptureProcess`, la section va du `TryStep("Snapshot DB pre-apply...")`
jusqu'au `TryStep("Restore DB net-zero...")` inclus. Encadrer cette section par un
`using (AudienceLock.Acquire(...))` quand `applyReal && applyAudienceId.HasValue`.
Structure :

```csharp
        AudienceLock audienceLock = null;
        if (applyReal && applyAudienceId.HasValue)
        {
            Console.WriteLine($"   AudienceLock : acquisition du verrou audience #{applyAudienceId.Value}…");
            try
            {
                audienceLock = AudienceLock.Acquire(applyAudienceId.Value, TimeSpan.FromMinutes(5));
                Console.WriteLine($"      → ✓ Verrou audience #{applyAudienceId.Value} acquis");
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"      ✗ {ex.Message}");
                return PrintSummaryAndExit(sw);
            }
        }
        try
        {
            // ... TryStep Snapshot ... TryStep Importer ... TryStep Verify ... TryStep Restore ...
        }
        finally
        {
            if (audienceLock != null)
            {
                audienceLock.Dispose();
                Console.WriteLine($"      → Verrou audience #{applyAudienceId.Value} relâché");
            }
        }
```

Adapter les bornes exactes du `try` au code réel : tout ce qui lit/écrit l'audience
(`SnapshotForApply`, le clic Importer, `QueryAuditCounts`, `RestoreAfterApply`) doit être
DANS le `try`. Le `driver.CaptureScreenshot(...)` et le cleanup audience peuvent rester
en dehors.

- [ ] **Step 2: Verrouiller aussi le setup Cas B**

Dans le bloc `casBAutoSetup` (`RunCasBSetupSql`), le clone de l'audience 28590 lit cette
audience. Encadrer l'appel `RunCasBSetupSql()` par
`using (AudienceLock.Acquire(28590, TimeSpan.FromMinutes(5)))` pour qu'un clone ne parte
pas pendant qu'un autre run applique sur 28590.

- [ ] **Step 3: Build**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj" -c Release`
Expected: PASS — 0 erreur.

- [ ] **Step 4: Smoke — net-zero préservé**

Lancer 1 scénario apply (`cas-a-subset`) puis « Reset DB ». Vérifier dans le stdout :
`✓ NET-ZERO confirmé` et `→ Verrou audience #28625 relâché`.

- [ ] **Step 5: ⚠ PAUSE — Commit**

Autorisation explicite requise (règle 6). Sur « go » :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs"
git commit -m "feat(smokerunner): AudienceLock autour de snapshot/apply/restore"
```

---

## Task 7: Pool de N workers pour le batch « All scenarios »

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Remplacer RunAllRaptureScenariosAsync par une version parallèle**

Dans `MainWindowViewModel.cs`, remplacer intégralement le corps de
`RunAllRaptureScenariosAsync` par la version pool-de-workers ci-dessous. Le degré vient
de `RIG_SMOKE_PARALLELISM` (défaut 3) ; `RIG_DRIVER_HEADLESS=0` force degré 1.

```csharp
    private async Task RunAllRaptureScenariosAsync()
    {
        var real = RaptureScenarios.Where(s => s.Id != AllScenariosSentinelId).ToList();
        if (real.Count == 0)
        {
            Log.Info("RunAllRaptureScenarios — aucun scénario réel à itérer");
            return;
        }

        // Degré de parallélisme : RIG_SMOKE_PARALLELISM (défaut 3). Headless OFF -> 1
        // (la souris ne se parallélise pas).
        int degree = 3;
        var rawDeg = Environment.GetEnvironmentVariable("RIG_SMOKE_PARALLELISM");
        if (!string.IsNullOrEmpty(rawDeg) && int.TryParse(rawDeg, out var d) && d >= 1)
            degree = d;
        if ((Environment.GetEnvironmentVariable("RIG_DRIVER_HEADLESS") ?? "1") == "0")
            degree = 1;
        degree = Math.Min(degree, real.Count);

        Log.Info($"╔═══ RUN ALL SCENARIOS ({real.Count}) — degré={degree}, applyReal={RaptureSmokeApplyReal} ═══╗");
        var batchSw = System.Diagnostics.Stopwatch.StartNew();
        var results = new System.Collections.Concurrent.ConcurrentBag<(string id, bool ok, TimeSpan dur, string detail)>();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Services.RaptureScenario>(real);
        int done = 0;

        // Pool de proxies : un par worker, tous pointant le même exe.
        var pool = new List<SmokeRunnerProxy>();
        for (int i = 0; i < degree; i++)
            pool.Add(new SmokeRunnerProxy(_raptureSmokeProxy.ExePath, _raptureSmokeProxy.RendersDir));
        _batchPool = pool; // champ pour Stop/Pause

        async Task Worker(SmokeRunnerProxy proxy)
        {
            while (queue.TryDequeue(out var s))
            {
                int idx = Interlocked.Increment(ref done);
                Log.Info($"━━━ [{idx}/{real.Count}] {s.Id} : {s.Name}");
                if (string.IsNullOrEmpty(s.ResolvedJsonPath) || !File.Exists(s.ResolvedJsonPath))
                {
                    Log.Info($"   ⚠ JSON introuvable ({s.ResolvedJsonPath}) — skip");
                    results.Add((s.Id, false, TimeSpan.Zero, "JSON introuvable"));
                    continue;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                proxy.FixedArgs = "--legacy-rapture-process";
                var extra = $"--json \"{s.ResolvedJsonPath}\"";
                if (RaptureSmokeApplyReal) extra += " --apply";
                if (s.DefaultAudienceId.HasValue) extra += $" --audience-id {s.DefaultAudienceId.Value}";
                if (s.Id == "cas-diff-replacement-note" && RaptureSmokeApplyReal) extra += " --idempotence-2run";
                if (s.Id == "cas-b-multi-match") extra += " --cas-b-auto-setup";
                extra += $" --scenario-id \"{s.Id}\"";
                if (s.ExpectedWarnings.HasValue) extra += $" --expected-warnings {s.ExpectedWarnings.Value}";
                if (s.EffectiveExpectedDetected.HasValue) extra += $" --expected-detected-modifications {s.EffectiveExpectedDetected.Value}";
                if (s.EffectiveExpectedApplied.HasValue) extra += $" --expected-applied-modifications {s.EffectiveExpectedApplied.Value}";
                proxy.ExtraArgs = extra;
                bool ok = false; string detail = "";
                try
                {
                    await proxy.RunAsync(enableUi: false);
                    DumpProxyRunToDisk(proxy);
                    ok = proxy.LastExitCode.GetValueOrDefault(-1) == 0;
                    detail = $"exit={proxy.LastExitCode?.ToString() ?? "(null)"}";
                }
                catch (Exception ex)
                {
                    Log.Error($"Scenario {s.Id} threw", ex);
                    detail = ex.GetType().Name + ": " + ex.Message;
                }
                sw.Stop();
                results.Add((s.Id, ok, sw.Elapsed, detail));
                Log.Info($"   → [{s.Id}] {(ok ? "✓ PASS" : "✗ FAIL")} en {sw.Elapsed.TotalSeconds:F1}s ({detail})");
            }
        }

        try
        {
            await Task.WhenAll(pool.Select(Worker));
        }
        finally
        {
            _batchPool = null;
        }
        batchSw.Stop();

        var ordered = results.OrderBy(r => r.id).ToList();
        Log.Info($"╠═══ RECAP ALL SCENARIOS — {ordered.Count(r => r.ok)} PASS / {ordered.Count(r => !r.ok)} FAIL " +
                 $"en {batchSw.Elapsed.TotalMinutes:F1} min (degré {degree}) ═══╣");
        foreach (var r in ordered)
            Log.Info($"   {(r.ok ? "✓" : "✗")} {r.id,-40} {r.dur.TotalSeconds,6:F1}s  {r.detail}");
        Log.Info($"╚═══════════════════════════════════════════════════════════════════════╝");
    }
```

- [ ] **Step 2: Ajouter le champ `_batchPool` et le helper `DumpProxyRunToDisk`**

Près du champ `_raptureSmokeProxy` (~ligne 33), ajouter :

```csharp
    private volatile List<SmokeRunnerProxy> _batchPool;
```

Et un helper de dump par proxy (le `DumpRaptureSmokeRunToDisk` existant ne gère que
`_raptureSmokeProxy`) :

```csharp
    private void DumpProxyRunToDisk(SmokeRunnerProxy proxy)
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            if (!string.IsNullOrEmpty(proxy.LastFullStdout))
                File.WriteAllText(Path.Combine(dir, $"rapture-smoke-{stamp}.stdout.log"), proxy.LastFullStdout!);
            if (!string.IsNullOrEmpty(proxy.LastStderr))
                File.WriteAllText(Path.Combine(dir, $"rapture-smoke-{stamp}.stderr.log"), proxy.LastStderr!);
        }
        catch (Exception ex) { Log.Error("DumpProxyRunToDisk", ex); }
    }
```

(Le suffixe `-fff` en millisecondes évite les collisions de noms entre workers parallèles.)

- [ ] **Step 3: Étendre Stop/Pause au pool**

Dans `StopRaptureSmoke()`, remplacer `_raptureSmokeProxy.Stop();` par :

```csharp
        var pool = _batchPool;
        if (pool != null) { foreach (var p in pool) p.Stop(); }
        else _raptureSmokeProxy.Stop();
```

Dans `TogglePauseRaptureSmoke()`, idem — itérer `_batchPool` s'il est non-null (Pause/Resume
chaque proxy), sinon agir sur `_raptureSmokeProxy`.

- [ ] **Step 4: Build**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.TestViewer/Rig.Wpf.Kbis.TestViewer.csproj" -c Release`
Expected: PASS — 0 erreur.

- [ ] **Step 5: ⚠ PAUSE — Commit**

Autorisation explicite requise (règle 6). Sur « go » :

```bash
git add "Source/Wpf/Rig.Wpf.Kbis.TestViewer/ViewModels/MainWindowViewModel.cs"
git commit -m "feat(testviewer): batch All scenarios parallélisé (pool de N workers)"
```

---

## Task 8: Vérification finale (CLAUDE.md règle 15)

**Files:** aucun — vérification end-to-end.

- [ ] **Step 1: Build complet vert**

Run: `dotnet build "Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Rig.Wpf.Kbis.SmokeRunner.csproj" -c Release`
puis `dotnet build "Source/Wpf/Rig.Wpf.Kbis.TestViewer/Rig.Wpf.Kbis.TestViewer.csproj" -c Release`
Expected: PASS — 0 erreur sur les deux.

- [ ] **Step 2: Lancer « All scenarios » degré 3 headless**

Tuer toute instance RIG/TestViewer résiduelle, lancer TestViewer, RAPTURE → Smoke Import
→ « All scenarios » → Apply réel coché → Start E2E. `RIG_DRIVER_HEADLESS` non défini
(défaut ON), `RIG_SMOKE_PARALLELISM` non défini (défaut 3).

- [ ] **Step 3: Vérifier zéro interférence souris pendant le run**

Pendant le batch : bouger la souris, cliquer, taper dans Notepad/Teams.
Expected : **aucune interférence** — la souris/le clavier répondent normalement à
l'utilisateur. *(Critère de succès #1.)*

- [ ] **Step 4: Vérifier l'isolation visuelle**

Expected : **aucune fenêtre RigClientAccueil** n'apparaît sur le desktop. Confirmer que
3 process `RigClientAccueil` coexistent (`Get-Process RigClientAccueil`) sur 3 HDESK.

- [ ] **Step 5: Vérifier la non-régression**

Analyser le recap du batch dans les logs + chaque `rapture-smoke-*.stdout.log`.
Expected : **16 PASS / 0 FAIL** (identique au batch de référence du 2026-05-20).

- [ ] **Step 6: Vérifier la sérialisation par audience**

Dans les stdout, repérer 2 scénarios sur la même audience (ex. `cas-a-subset` et
`cas-valid-plumitif-resolu`, audience 28625) : l'un doit logguer une attente sur
`AudienceLock` pendant que l'autre tient le verrou. Les scénarios sur audiences
différentes se chevauchent (timestamps de démarrage rapprochés).

- [ ] **Step 7: Vérifier le gain de temps**

Expected : durée totale du batch nettement < 8 min (cible ~3 min à degré 3).
Le recap loggue `en X.X min (degré 3)`.

- [ ] **Step 8: Vérifier les screenshots**

Lire (tool Read) au moins un screenshot de recap produit. Expected : la fenêtre
`FormRaptureImportRecap` est visible et lisible (capture PrintWindow du HDESK).

- [ ] **Step 9: Test run indépendant concurrent**

Pendant qu'un batch tourne, lancer un 2e run (un scénario seul via un 2e TestViewer, ou
relancer un scénario). Expected : pas de corruption — les deux runs terminent, l'audit
reste net-zero des deux côtés (vérifier via « Reset DB » à blanc : 0 résidu).

- [ ] **Step 10: ⚠ PAUSE — Commit final**

Si des ajustements ont été nécessaires en Task 8, autorisation explicite requise
(règle 6). Sur « go » :

```bash
git add -A
git commit -m "test(smokerunner): vérification finale driver input-free parallélisé"
```

---

## Notes d'implémentation

- **Risque grille COM** : si `Interaction.Click`/`Select` lève l'exception « élément sans
  hwnd ni pattern » sur une grille RigAutomate, le hwnd de la grille parente reste
  joignable — poster `WM_LBUTTON*` aux coords-client de la ligne via le hwnd de la grille
  (déjà le comportement de `Click` quand `NativeWindowHandle` remonte le hwnd conteneur).
- **PrintWindow noir** : un contrôle COM peut se rendre en noir. Best-effort assumé — la
  recap WinForms se capture bien, les assertions SQL/UIA restent le gate primaire.
- **Ordre Task 4 avant Task 5** : convertir les interactions AVANT de basculer sur HDESK.
  Sinon les `.Click()` souris n'atteignent pas le HDESK et tout casse.
