# Boucle de travail rig-testing — Plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> ⚠️ **CONTRAINTE REPO (CLAUDE.md règle 6)** : aucun `git commit` sans « go » explicite de l'utilisateur. Les steps « Checkpoint » de ce plan **s'arrêtent et demandent l'autorisation** — ils ne committent pas en silence.
>
> ⚠️ **TDD** : ce legacy n'a pas de projet de test unitaire et le build est circulaire (CLAUDE.md). La vérification se fait donc par **exécution réelle de commandes** (steps « Verify »), conformément au protocole CLAUDE.md règle 15 — pas de xUnit.

**Goal:** Ajouter un mode `--loop` à `Rig.Wpf.Kbis.SmokeRunner` qui exécute les scénarios rig-testing en parallèle, émet un `result.json` structuré, range les artefacts, plus une skill `/rig-loop` et un playbook — pour rendre la boucle de travail reproductible et partageable par l'équipe.

**Architecture:** 3 couches. (1) *Mécanique* : nouveau mode `SmokeRunner --loop {run|build|bench}`, déterministe, émet du JSON. (2) *Mémoire* : dossier `runs/` horodaté + `baseline.json` versionné. (3) *Jugement* : skill `/rig-loop` mince qui appelle le CLI et lit la mémoire. Le mode `--loop run` porte l'orchestration parallèle qui vit aujourd'hui dans le ViewModel du TestViewer (`RunAllRaptureScenariosAsync`).

**Tech Stack:** C# .NET Framework 4.8, `Rig.Wpf.Kbis.SmokeRunner` (WPF exe). Sérialisation JSON via `DataContractJsonSerializer` (System.Runtime.Serialization — pas de System.Text.Json dans ce projet). Markdown pour skill + playbook.

---

## Décisions verrouillées (raffinements de la spec)

- **Le CLI est un mode de SmokeRunner** : `SmokeRunner --loop <verbe>`. Validé par l'utilisateur.
- **`baseline.json` et `LOOP.md` sont versionnés dans le repo** (`Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\loop\`) — sinon ils ne sont pas partagés par l'équipe, ce qui contredirait le but du projet. La spec §6 les plaçait sous `Audit/` (non-tracké) ; ce plan corrige.
- **Les artefacts volatils** (`runs/<id>/`, `.bench-lock`) vont dans `%LOCALAPPDATA%\rig-wpf-testviewer\loop\` — réutilise la convention du dossier de logs existant (`Logger.cs`). Pas de chemin `C:\` en dur, par-utilisateur.
- **Le worker est `--rapture-selfdrive`** (headless in-process, déjà existant). `--loop run` spawn N× `SmokeRunner --rapture-selfdrive`, exactement comme le fait aujourd'hui le TestViewer.

## Structure de fichiers

```
RigApplication-testing/
├── Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/
│   ├── Program.cs                          ← MODIFIER : ajouter le dispatch --loop
│   ├── LoopMode.cs                         ← CRÉER : RunLoop + verbes run/build/bench
│   ├── LoopResult.cs                       ← CRÉER : DTO result.json + writer
│   ├── LoopBaseline.cs                     ← CRÉER : modèle + loader de baseline.json
│   ├── BenchLock.cs                        ← MODIFIER/CRÉER : lock du banc (--loop bench)
│   └── loop/
│       ├── baseline.json                   ← CRÉER : couche Mémoire (versionnée)
│       └── LOOP.md                         ← CRÉER : playbook 1 page (versionné)
└── .claude/skills/rig-loop/
    └── SKILL.md                            ← CRÉER : skill /rig-loop (versionnée)

%LOCALAPPDATA%/rig-wpf-testviewer/loop/      ← runtime, NON versionné
├── .bench-lock
└── runs/<runId>/{result.json, history…, screenshots/, logs/}
```

Note : `AudienceLock.cs` existe déjà dans SmokeRunner (mutex par audience) — ne pas le confondre avec `BenchLock.cs` (lock global du banc). On crée bien un fichier distinct.

---

## Task 1: Squelette du mode `--loop` + routage des verbes

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopMode.cs`
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs` (bloc dispatch, après la ligne 125 `--legacy-rapture`)

- [ ] **Step 1: Créer `LoopMode.cs` avec le routeur de verbes**

```csharp
using System;
using System.Linq;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Mode --loop : CLI déterministe de la boucle rig-testing.
    /// Verbes : run | build | bench. Émet du JSON, range les artefacts.
    /// </summary>
    internal static class LoopMode
    {
        public static int RunLoop(string[] args)
        {
            // Le verbe est le 1er argument non-option après --loop.
            string verb = args.SkipWhile(a => !a.Equals("--loop", StringComparison.OrdinalIgnoreCase))
                              .Skip(1)
                              .FirstOrDefault(a => !a.StartsWith("-"));
            switch ((verb ?? "").ToLowerInvariant())
            {
                case "run":   return LoopRun.Execute(args);
                case "build": return LoopBuild.Execute(args);
                case "bench": return LoopBench.Execute(args);
                default:
                    Console.WriteLine("Usage : SmokeRunner --loop {run|build|bench} [options]");
                    Console.WriteLine("  run    [--scenarios all|<id,id>] [--apply]");
                    Console.WriteLine("  build");
                    Console.WriteLine("  bench  {acquire|release|status}");
                    return string.IsNullOrEmpty(verb) ? 0 : 64; // 64 = EX_USAGE
            }
        }
    }
}
```

Note : `LoopRun`, `LoopBuild`, `LoopBench` sont créés aux Tasks 3, 8, 7. Pour que ce fichier compile dès maintenant, créer 3 stubs temporaires en bas de `LoopMode.cs` :

```csharp
    internal static class LoopRun   { public static int Execute(string[] a) { Console.WriteLine("TODO run");   return 0; } }
    internal static class LoopBuild { public static int Execute(string[] a) { Console.WriteLine("TODO build"); return 0; } }
    internal static class LoopBench { public static int Execute(string[] a) { Console.WriteLine("TODO bench"); return 0; } }
```
(Ces stubs seront remplacés par de vrais fichiers/classes aux Tasks suivantes — supprimer le stub correspondant à chaque Task.)

- [ ] **Step 2: Brancher le dispatch dans `Program.cs`**

Dans `Program.cs`, méthode `Main`, juste après le bloc `--legacy-rapture` (ligne 125) et AVANT `var legacyMode = ...` (ligne 126), insérer :

```csharp
        // --loop = CLI de la boucle rig-testing (run/build/bench). Émet du JSON.
        if (args.Any(a => a.Equals("--loop", StringComparison.OrdinalIgnoreCase)))
            return LoopMode.RunLoop(args);
```

- [ ] **Step 3: Verify — build + usage**

Run :
```
powershell -ExecutionPolicy Bypass -File "C:\Code RIG\RigApplication-testing\Source\CompilationLivraison\safeBuild.ps1" "Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\Rig.Wpf.Kbis.SmokeRunner.csproj" Build Release "" v48 -AllowComInteropRegistration -Property "RegisterForComInterop=false"
```
Expected : build OK, 0 erreur.

Puis :
```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop
```
Expected : affiche le bloc « Usage : SmokeRunner --loop {run|build|bench} », exit 0.
```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop badverb
```
Expected : affiche Usage, exit 64.

- [ ] **Step 4: Checkpoint** — demander le « go » utilisateur avant tout `git commit`. Si refusé, continuer sans commiter.

---

## Task 2: Schéma `result.json` + writer

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopResult.cs`

- [ ] **Step 1: Créer les DTO + le writer**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>Verdict d'un scénario. PASS/FAIL/FLAKY/SKIPPED.</summary>
    [DataContract]
    internal sealed class LoopScenarioResult
    {
        [DataMember(Name = "id")]          public string Id { get; set; }
        [DataMember(Name = "verdict")]     public string Verdict { get; set; }   // PASS|FAIL|FLAKY|SKIPPED
        [DataMember(Name = "retried")]     public bool Retried { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
        [DataMember(Name = "failReason")] public string FailReason { get; set; } // null si PASS
        [DataMember(Name = "artifacts")]  public List<string> Artifacts { get; set; } = new List<string>();
    }

    [DataContract]
    internal sealed class LoopSummary
    {
        [DataMember(Name = "passed")]     public int Passed { get; set; }
        [DataMember(Name = "failed")]     public int Failed { get; set; }
        [DataMember(Name = "flaky")]      public int Flaky { get; set; }
        [DataMember(Name = "skipped")]    public int Skipped { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
    }

    [DataContract]
    internal sealed class LoopRunResult
    {
        [DataMember(Name = "runId")]      public string RunId { get; set; }
        [DataMember(Name = "startedAt")] public string StartedAt { get; set; } // ISO 8601
        [DataMember(Name = "summary")]   public LoopSummary Summary { get; set; }
        [DataMember(Name = "scenarios")] public List<LoopScenarioResult> Scenarios { get; set; } = new List<LoopScenarioResult>();

        /// <summary>Écrit le result.json (indenté, UTF-8 sans BOM).</summary>
        public void WriteTo(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(LoopRunResult));
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                    ser.WriteObject(w, this);
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        public static LoopRunResult ReadFrom(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(LoopRunResult));
            using (var fs = File.OpenRead(path))
                return (LoopRunResult)ser.ReadObject(fs);
        }
    }
}
```

- [ ] **Step 2: Verify — round-trip**

Ajouter temporairement dans `LoopMode.RunLoop`, branche `default`, un cas `case "selftest":` qui crée un `LoopRunResult` factice, le `WriteTo` un fichier temp, le `ReadFrom`, et imprime `OK` si `Scenarios.Count` correspond. Build, puis :
```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop selftest
```
Expected : `result.json round-trip OK`. **Retirer ce `case "selftest"` après vérification.**

- [ ] **Step 3: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 3: `--loop run` — orchestration parallèle

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopRun.cs`
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopMode.cs` (supprimer le stub `LoopRun`)

Cette task porte la logique de `MainWindowViewModel.RunAllRaptureScenariosAsync` (TestViewer, lignes 1177-1337) dans SmokeRunner. Différences : pas de UI/`Emit` vers un ViewModel (on écrit en `Console`), on charge le manifest directement, on retourne un `LoopRunResult` au lieu d'un `ConcurrentBag`.

- [ ] **Step 1: Créer `LoopRun.cs` — chargement manifest + orchestration**

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>DTO minimal d'un scénario du manifest.json (sous-ensemble utilisé par le loop).</summary>
    [DataContract]
    internal sealed class LoopScenario
    {
        [DataMember(Name = "id")]                public string Id { get; set; }
        [DataMember(Name = "jsonFile")]          public string JsonFile { get; set; }
        [DataMember(Name = "defaultAudienceId")] public int? DefaultAudienceId { get; set; }
    }

    [DataContract]
    internal sealed class LoopManifest
    {
        [DataMember(Name = "scenarios")] public List<LoopScenario> Scenarios { get; set; } = new List<LoopScenario>();
    }

    internal static class LoopRun
    {
        public static int Execute(string[] args)
        {
            // ── Args ──
            string scenariosArg = ArgVal(args, "--scenarios") ?? "all";
            bool apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            int parallelism = 4;
            var envPar = Environment.GetEnvironmentVariable("RIG_SMOKE_PARALLELISM");
            if (!string.IsNullOrEmpty(envPar) && int.TryParse(envPar, out var ep) && ep > 0) parallelism = ep;

            // ── Localiser le manifest + le binaire ──
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string scenariosDir = FindScenariosDir(exeDir);
            if (scenariosDir == null) { Console.WriteLine("ERREUR : dossier RaptureScenarios introuvable."); return 66; }
            string manifestPath = Path.Combine(scenariosDir, "manifest.json");
            if (!File.Exists(manifestPath)) { Console.WriteLine("ERREUR : manifest.json introuvable : " + manifestPath); return 66; }

            var manifest = LoadManifest(manifestPath);
            var selected = SelectScenarios(manifest.Scenarios, scenariosArg);
            if (selected.Count == 0) { Console.WriteLine("ERREUR : aucun scénario sélectionné (" + scenariosArg + ")."); return 64; }

            string smokeExe = Path.Combine(exeDir, "Rig.Wpf.Kbis.SmokeRunner.exe");
            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Console.WriteLine($"╔═══ LOOP RUN ({selected.Count} scénarios) — parallelism={parallelism} apply={apply} runId={runId} ═══╗");

            var batchSw = Stopwatch.StartNew();
            var results = new ConcurrentBag<LoopScenarioResult>();
            var audienceLocks = new ConcurrentDictionary<int, SemaphoreSlim>();

            using (var semaphore = new SemaphoreSlim(parallelism, parallelism))
            {
                var tasks = selected.Select(s => Task.Run(async () =>
                {
                    string jsonPath = Path.Combine(scenariosDir, s.JsonFile ?? "");
                    if (string.IsNullOrEmpty(s.JsonFile) || !File.Exists(jsonPath))
                    {
                        results.Add(new LoopScenarioResult { Id = s.Id, Verdict = "SKIPPED",
                            FailReason = "JSON introuvable : " + jsonPath });
                        Console.WriteLine($"   ⊘ [{s.Id}] SKIPPED — JSON introuvable");
                        return;
                    }
                    await semaphore.WaitAsync();
                    SemaphoreSlim audLock = null;
                    if (apply && s.DefaultAudienceId.HasValue)
                        audLock = audienceLocks.GetOrAdd(s.DefaultAudienceId.Value, _ => new SemaphoreSlim(1, 1));
                    if (audLock != null) await audLock.WaitAsync();
                    try
                    {
                        var r = await RunOneScenario(s, jsonPath, smokeExe, apply);
                        results.Add(r);
                        Console.WriteLine($"   {VerdictIcon(r.Verdict)} [{s.Id}] {r.Verdict} en {r.DurationMs / 1000.0:F1}s");
                    }
                    finally
                    {
                        audLock?.Release();
                        semaphore.Release();
                    }
                }));
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
            foreach (var lk in audienceLocks.Values) lk.Dispose();
            batchSw.Stop();

            // ── Agrégation ──
            var ordered = results.OrderBy(r => selected.FindIndex(s => s.Id == r.Id)).ToList();
            var result = new LoopRunResult
            {
                RunId = runId,
                StartedAt = DateTime.Now.ToString("o"),
                Scenarios = ordered,
                Summary = new LoopSummary
                {
                    Passed     = ordered.Count(r => r.Verdict == "PASS"),
                    Failed     = ordered.Count(r => r.Verdict == "FAIL"),
                    Flaky      = ordered.Count(r => r.Verdict == "FLAKY"),
                    Skipped    = ordered.Count(r => r.Verdict == "SKIPPED"),
                    DurationMs = batchSw.ElapsedMilliseconds,
                },
            };

            // ── Persistance (Task 5 la complète : runs/<id>/) ──
            LoopArtifacts.Persist(result);

            Console.WriteLine($"╠═══ RECAP — {result.Summary.Passed} PASS / {result.Summary.Failed} FAIL / {result.Summary.Flaky} FLAKY / {result.Summary.Skipped} SKIP en {batchSw.Elapsed.TotalSeconds:F1}s ═══╣");
            Console.WriteLine($"╚═══ result.json : {LoopArtifacts.RunDir(runId)} ═══╝");
            return result.Summary.Failed > 0 ? 1 : 0;
        }

        // RunOneScenario : Task 4 ajoute le retry-once. Version Task 3 = un seul essai.
        private static async Task<LoopScenarioResult> RunOneScenario(
            LoopScenario s, string jsonPath, string smokeExe, bool apply)
        {
            var sw = Stopwatch.StartNew();
            var (exitCode, failReason) = await SpawnWorker(s, jsonPath, smokeExe, apply);
            sw.Stop();
            return new LoopScenarioResult
            {
                Id = s.Id,
                Verdict = exitCode == 0 ? "PASS" : "FAIL",
                Retried = false,
                DurationMs = sw.ElapsedMilliseconds,
                FailReason = exitCode == 0 ? null : failReason,
            };
        }

        /// <summary>Spawn un worker SmokeRunner --rapture-selfdrive. Retourne (exitCode, failReason).</summary>
        private static async Task<(int exitCode, string failReason)> SpawnWorker(
            LoopScenario s, string jsonPath, string smokeExe, bool apply)
        {
            string a = $"--rapture-selfdrive --json \"{jsonPath}\" --scenario-id \"{s.Id}\"";
            if (apply) a += " --apply";
            if (s.DefaultAudienceId.HasValue) a += $" --audience-id {s.DefaultAudienceId.Value}";
            if (s.Id == "cas-b-multi-match") a += " --cas-b-auto-setup";

            var psi = new ProcessStartInfo(smokeExe, a)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(smokeExe),
            };
            psi.EnvironmentVariables["RIG_DRIVER_HEADLESS"] = "1";

            using (var proc = Process.Start(psi))
            {
                if (proc == null) return (-1, "Process.Start a retourné null");
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var exitTcs = new TaskCompletionSource<bool>();
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, __) => exitTcs.TrySetResult(true);
                if (proc.HasExited) exitTcs.TrySetResult(true);
                var winner = await Task.WhenAny(exitTcs.Task, Task.Delay(180_000));
                bool exited = winner == exitTcs.Task;
                if (!exited) { try { proc.Kill(); } catch { } }
                string stdout = await stdoutTask;
                await stderrTask;
                if (!exited) return (-1, "Timeout 180s — worker tué");
                if (proc.ExitCode != 0)
                    return (proc.ExitCode, ExtractFailReason(stdout) ?? $"exit={proc.ExitCode}");
                return (0, null);
            }
        }

        /// <summary>Extrait la 1ère ligne « Exception: » ou « ⚠ » du stdout worker comme cause.</summary>
        private static string ExtractFailReason(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.StartsWith("Exception:") || t.Contains("DIVERGE") || t.StartsWith("⚠"))
                    return t.Length > 200 ? t.Substring(0, 200) : t;
            }
            return null;
        }

        private static string VerdictIcon(string v) =>
            v == "PASS" ? "✓" : v == "FLAKY" ? "~" : v == "SKIPPED" ? "⊘" : "✗";

        private static List<LoopScenario> SelectScenarios(List<LoopScenario> all, string arg)
        {
            if (arg.Equals("all", StringComparison.OrdinalIgnoreCase)) return all;
            var ids = new HashSet<string>(arg.Split(','), StringComparer.OrdinalIgnoreCase);
            return all.Where(s => ids.Contains(s.Id)).ToList();
        }

        private static LoopManifest LoadManifest(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(LoopManifest));
            using (var fs = File.OpenRead(path))
                return (LoopManifest)ser.ReadObject(fs);
        }

        private static string FindScenariosDir(string exeDir)
        {
            // 1) à côté de l'exe ; 2) remonter vers le TestViewer (dev)
            string p1 = Path.Combine(exeDir, "RaptureScenarios");
            if (Directory.Exists(p1)) return p1;
            var dir = new DirectoryInfo(exeDir);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string cand = Path.Combine(dir.FullName,
                    "Rig.Wpf.Kbis.TestViewer", "bin", "Release", "net48", "RaptureScenarios");
                if (Directory.Exists(cand)) return cand;
            }
            return null;
        }

        internal static string ArgVal(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(name.Length + 1);
            }
            return null;
        }
    }
}
```

- [ ] **Step 2: Retirer le stub `LoopRun` de `LoopMode.cs`**

Supprimer la ligne `internal static class LoopRun { ... }` ajoutée en Task 1 Step 1.

- [ ] **Step 3: Stub temporaire `LoopArtifacts`**

`LoopRun` référence `LoopArtifacts.Persist` et `LoopArtifacts.RunDir` (créés en Task 5). Pour compiler maintenant, ajouter en bas de `LoopRun.cs` :

```csharp
    internal static class LoopArtifacts
    {
        public static void Persist(LoopRunResult r) { /* Task 5 */ }
        public static string RunDir(string runId) => runId; // Task 5
    }
```

- [ ] **Step 4: Verify — run d'un scénario sans DB**

Build (commande Task 1 Step 3). Puis lancer un scénario qui ne touche pas la DB :
```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop run --scenarios cas-err-affaires-vide
```
Expected : ligne `▶ LOOP RUN (1 scénarios)`, le worker se lance, une ligne `✓ [cas-err-affaires-vide] PASS` ou `✗ … FAIL`, un `RECAP`, exit 0 si PASS.

- [ ] **Step 5: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 4: Retry-once sur FAIL → verdict `FLAKY`

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopRun.cs` (méthode `RunOneScenario`)

- [ ] **Step 1: Remplacer `RunOneScenario` par la version avec retry**

```csharp
        // Un FAIL est re-tenté UNE fois. 2e essai PASS → FLAKY. 2e essai FAIL → FAIL.
        private static async Task<LoopScenarioResult> RunOneScenario(
            LoopScenario s, string jsonPath, string smokeExe, bool apply)
        {
            var sw = Stopwatch.StartNew();
            var (exit1, reason1) = await SpawnWorker(s, jsonPath, smokeExe, apply);
            if (exit1 == 0)
            {
                sw.Stop();
                return new LoopScenarioResult { Id = s.Id, Verdict = "PASS",
                    Retried = false, DurationMs = sw.ElapsedMilliseconds };
            }
            // 1er essai KO → retry once
            Console.WriteLine($"   ~ [{s.Id}] échec 1er essai ({reason1}) — retry…");
            var (exit2, reason2) = await SpawnWorker(s, jsonPath, smokeExe, apply);
            sw.Stop();
            if (exit2 == 0)
                return new LoopScenarioResult { Id = s.Id, Verdict = "FLAKY",
                    Retried = true, DurationMs = sw.ElapsedMilliseconds,
                    FailReason = "1er essai KO (" + reason1 + "), 2e OK" };
            return new LoopScenarioResult { Id = s.Id, Verdict = "FAIL",
                Retried = true, DurationMs = sw.ElapsedMilliseconds, FailReason = reason2 ?? reason1 };
        }
```

- [ ] **Step 2: Verify — retry visible**

Lancer un scénario connu fragile (`cas-diff-replacement-note`, ~20 % de flake observé) plusieurs fois :
```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop run --scenarios cas-diff-replacement-note
```
Expected : la plupart du temps `✓ PASS` ; en cas d'échec du 1er essai, une ligne `~ […] échec 1er essai … — retry…` puis verdict `FLAKY` ou `FAIL`. Le `result.json` (Task 5) portera `verdict:"FLAKY"`, `retried:true`.

- [ ] **Step 3: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 5: Dossier `runs/<runId>/` + émission `result.json` + `history.jsonl`

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopArtifacts.cs`
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopRun.cs` (supprimer le stub `LoopArtifacts`)

- [ ] **Step 1: Créer `LoopArtifacts.cs`**

```csharp
using System;
using System.IO;
using System.Text;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Couche Mémoire : range les artefacts d'un run dans
    /// %LOCALAPPDATA%\rig-wpf-testviewer\loop\runs\<runId>\ et tient history.jsonl.
    /// </summary>
    internal static class LoopArtifacts
    {
        public static string LoopRoot()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rig-wpf-testviewer", "loop");
            Directory.CreateDirectory(root);
            return root;
        }

        public static string RunDir(string runId)
        {
            string d = Path.Combine(LoopRoot(), "runs", runId);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Écrit result.json dans runs/<id>/ et append 1 ligne dans history.jsonl.</summary>
        public static void Persist(LoopRunResult result)
        {
            string dir = RunDir(result.RunId);
            string resultPath = Path.Combine(dir, "result.json");
            result.WriteTo(resultPath);

            // history.jsonl : 1 ligne JSON compacte = le summary + runId.
            string histLine = string.Format(
                "{{\"runId\":\"{0}\",\"startedAt\":\"{1}\",\"passed\":{2},\"failed\":{3},\"flaky\":{4},\"skipped\":{5},\"durationMs\":{6}}}",
                result.RunId, result.StartedAt,
                result.Summary.Passed, result.Summary.Failed, result.Summary.Flaky,
                result.Summary.Skipped, result.Summary.DurationMs);
            File.AppendAllText(Path.Combine(LoopRoot(), "history.jsonl"),
                histLine + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
```

- [ ] **Step 2: Retirer le stub `LoopArtifacts` de `LoopRun.cs`**

Supprimer le `internal static class LoopArtifacts { … }` ajouté en Task 3 Step 3.

- [ ] **Step 3: Corriger le message final de `LoopRun.Execute`**

Dans `LoopRun.Execute`, remplacer la ligne :
```csharp
            Console.WriteLine($"╚═══ result.json : {LoopArtifacts.RunDir(runId)} ═══╝");
```
par :
```csharp
            Console.WriteLine($"╚═══ result.json : {Path.Combine(LoopArtifacts.RunDir(runId), "result.json")} ═══╝");
```

- [ ] **Step 4: Verify — result.json produit**

```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop run --scenarios cas-err-affaires-vide
```
Puis vérifier le fichier :
```
powershell -Command "Get-Content \"$env:LOCALAPPDATA\rig-wpf-testviewer\loop\runs\*\result.json\" | Select-Object -Last 1"
```
Expected : un JSON valide avec `runId`, `summary` (passed/failed/flaky/skipped/durationMs), `scenarios[]` avec `id`, `verdict`, `durationMs`. Et `history.jsonl` contient 1 ligne pour ce run.

- [ ] **Step 5: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 6: `baseline.json` — couche Mémoire (triage)

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/loop/baseline.json`

- [ ] **Step 1: Créer `loop/baseline.json` avec le contenu de départ**

Contenu factuel issu des observations de session (flake `cas-diff-replacement-note` ~20 %, scénarios neutralisés pour data-drift) :

```json
{
  "$schema": "baseline du triage rig-loop — lu par la skill /rig-loop, pas par le CLI",
  "version": 1,
  "knownFlakes": [
    {
      "id": "cas-diff-replacement-note",
      "rate": "~20%",
      "cause": "OpenFileDialog absent après 10s — timing FlaUI, clic Importer Rapture parfois perdu"
    }
  ],
  "dataDriftScenarios": [
    { "id": "cas-a-contentieux-10",   "cause": "audience 28590 (22/05) hors fenêtre RETAUD active" },
    { "id": "cas-err-affaires-vide",  "cause": "audience 28590 hors fenêtre RETAUD → fallback Cas C" },
    { "id": "cas-diff-sans-changement","cause": "audience 28590 hors fenêtre RETAUD → fallback Cas C" },
    { "id": "cas-valid-plumitif-resolu","cause": "10 warnings d'origine non élucidée — diag à faire" }
  ],
  "preservedBugs": []
}
```

- [ ] **Step 2: Verify — JSON valide**

```
powershell -Command "Get-Content 'C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\loop\baseline.json' -Raw | ConvertFrom-Json | Out-Null; if ($?) { 'baseline.json valide' }"
```
Expected : `baseline.json valide`.

- [ ] **Step 3: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 7: `--loop bench` — lock du banc de test

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopBench.cs`
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopMode.cs` (supprimer le stub `LoopBench`)

- [ ] **Step 1: Créer `LoopBench.cs`**

```csharp
using System;
using System.IO;
using System.Linq;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// --loop bench {acquire|release|status} : lock coopératif du banc de test.
    /// Empêche 2 collègues de lancer 16 RIG chacun en même temps.
    /// Fichier : %LOCALAPPDATA%\rig-wpf-testviewer\loop\.bench-lock
    /// </summary>
    internal static class LoopBench
    {
        private static string LockPath() => Path.Combine(LoopArtifacts.LoopRoot(), ".bench-lock");

        public static int Execute(string[] args)
        {
            string sub = args.SkipWhile(a => !a.Equals("bench", StringComparison.OrdinalIgnoreCase))
                             .Skip(1).FirstOrDefault(a => !a.StartsWith("-"));
            switch ((sub ?? "status").ToLowerInvariant())
            {
                case "acquire": return Acquire();
                case "release": return Release();
                case "status":  return Status();
                default:
                    Console.WriteLine("Usage : --loop bench {acquire|release|status}");
                    return 64;
            }
        }

        private static int Acquire()
        {
            string path = LockPath();
            if (File.Exists(path) && !IsExpired(path))
            {
                Console.WriteLine("⛔ Banc OCCUPÉ — " + File.ReadAllText(path).Trim());
                return 1;
            }
            // Lock 2h par défaut.
            string who = Environment.UserName;
            string until = DateTime.Now.AddHours(2).ToString("o");
            File.WriteAllText(path, $"{who} jusqu'à {until}");
            Console.WriteLine($"✓ Banc acquis par {who} jusqu'à {until}");
            return 0;
        }

        private static int Release()
        {
            string path = LockPath();
            if (File.Exists(path)) File.Delete(path);
            Console.WriteLine("✓ Banc libéré");
            return 0;
        }

        private static int Status()
        {
            string path = LockPath();
            if (!File.Exists(path) || IsExpired(path)) { Console.WriteLine("✓ Banc LIBRE"); return 0; }
            Console.WriteLine("⛔ Banc OCCUPÉ — " + File.ReadAllText(path).Trim());
            return 1;
        }

        // Le lock contient « …jusqu'à <ISO> » ; périmé si la date est dépassée.
        private static bool IsExpired(string path)
        {
            try
            {
                string content = File.ReadAllText(path);
                int i = content.LastIndexOf("jusqu'à ", StringComparison.Ordinal);
                if (i < 0) return false;
                string iso = content.Substring(i + 8).Trim();
                return DateTime.TryParse(iso, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt) && dt < DateTime.Now;
            }
            catch { return false; }
        }
    }
}
```

- [ ] **Step 2: Retirer le stub `LoopBench` de `LoopMode.cs`**

- [ ] **Step 3: Verify — acquire / status / release**

```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop bench acquire
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop bench acquire
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop bench status
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop bench release
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop bench status
```
Expected : 1er acquire `✓ Banc acquis` (exit 0) ; 2e acquire `⛔ Banc OCCUPÉ` (exit 1) ; status `⛔ OCCUPÉ` ; release `✓ Banc libéré` ; status final `✓ Banc LIBRE`.

- [ ] **Step 4: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 8: `--loop build` — build + deploy en une commande

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopBuild.cs`
- Modify: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LoopMode.cs` (supprimer le stub `LoopBuild`)

- [ ] **Step 1: Créer `LoopBuild.cs`**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// --loop build : encapsule build PROC_RETAUD + deploy en UNE commande
    /// au verdict pass/fail unique. Évite la chasse aux erreurs du build circulaire.
    /// </summary>
    internal static class LoopBuild
    {
        public static int Execute(string[] args)
        {
            // Localiser la racine du repo (remonter jusqu'à voir Source\CompilationLivraison).
            string repo = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (repo == null) { Console.WriteLine("ERREUR : racine repo introuvable."); return 66; }

            string safeBuild = Path.Combine(repo, "Source", "CompilationLivraison", "safeBuild.ps1");
            string proj = Path.Combine(repo, "Source", "RIG", "DLL", "Processus",
                                       "PROC_RETAUD", "PROC_RETAUD.csproj");
            if (!File.Exists(safeBuild) || !File.Exists(proj))
            {
                Console.WriteLine("ERREUR : safeBuild.ps1 ou PROC_RETAUD.csproj introuvable.");
                return 66;
            }

            Console.WriteLine("▶ LOOP BUILD — PROC_RETAUD (Release)…");
            int rc = RunPs(safeBuild,
                $"\"{proj}\" Build Release \"\" v48 -AllowComInteropRegistration -Property \"RegisterForComInterop=false\"");
            if (rc != 0) { Console.WriteLine($"✗ BUILD ÉCHEC (exit {rc})"); return 1; }

            string deploy = Path.Combine(repo, "Source", "CompilationLivraison", "deploy-proc-retaud.ps1");
            if (File.Exists(deploy))
            {
                Console.WriteLine("▶ LOOP BUILD — deploy…");
                int rd = RunPs(deploy, "");
                if (rd != 0) { Console.WriteLine($"✗ DEPLOY ÉCHEC (exit {rd})"); return 1; }
            }
            else
            {
                Console.WriteLine("⚠ deploy-proc-retaud.ps1 absent — build seul, pas de deploy.");
            }
            Console.WriteLine("✓ LOOP BUILD OK");
            return 0;
        }

        private static int RunPs(string script, string scriptArgs)
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-ExecutionPolicy Bypass -File \"{script}\" {scriptArgs}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("   " + e.Data); };
                p.BeginOutputReadLine();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine("   [stderr] " + err.Trim());
                return p.ExitCode;
            }
        }

        private static string FindRepoRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Source", "CompilationLivraison")))
                    return dir.FullName;
            }
            return null;
        }
    }
}
```

- [ ] **Step 2: Retirer le stub `LoopBuild` de `LoopMode.cs`**

- [ ] **Step 3: Verify — build verdict unique**

```
"<bin>\Rig.Wpf.Kbis.SmokeRunner.exe" --loop build
```
Expected : lignes `▶ LOOP BUILD — PROC_RETAUD…` puis sortie du build relayée, et soit `✓ LOOP BUILD OK` (exit 0) soit `✗ BUILD ÉCHEC` (exit 1). Un seul verdict, pas de log à fouiller.

- [ ] **Step 4: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 9: Skill `/rig-loop` — couche Jugement

**Files:**
- Create: `.claude/skills/rig-loop/SKILL.md`

- [ ] **Step 1: Créer `.claude/skills/rig-loop/SKILL.md`**

```markdown
---
name: rig-loop
description: Use when working on rig-testing — running the test loop, executing rig-testing scenarios, triaging test results, or iterating on Rapture import tests. Orchestrates the 6-phase loop via the SmokeRunner --loop CLI.
---

# Boucle de travail rig-testing

Orchestre la boucle de test rig-testing en 6 phases. Le travail mécanique est fait
par le CLI `SmokeRunner --loop` ; cette skill ne fait que **piloter** et **interpréter**.

CLI : `<repo>\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\bin\Release\net48\Rig.Wpf.Kbis.SmokeRunner.exe`
Forme courte employée ci-dessous : `rig-loop <verbe>` = `SmokeRunner.exe --loop <verbe>`.

## Les 6 phases

0. **Acquérir le banc** — `rig-loop bench status`. Si occupé, STOP et prévenir l'utilisateur.
   Sinon `rig-loop bench acquire`.
1. **Cadrer** — l'utilisateur énonce le besoin. Le traduire en scénarios concrets
   (entrées du manifest + fixtures JSON). NE PAS deviner le besoin : demander si flou.
2. **Écrire** — écrire/étendre les scénarios dans `RaptureScenarios\manifest.json`
   + fixtures JSON + compteurs attendus (`expectedWarnings`, `expectedDetectedModifications`,
   `expectedAppliedModifications`).
3. **Exécuter** — `rig-loop run --scenarios all` (ou `--scenarios id1,id2`).
   Si le besoin implique une écriture DB : `--apply`. Ne pas babysitter — le CLI gère
   parallélisme + retry.
4. **Observer & trier** — lire le `result.json` (chemin affiché en fin de run). Pour
   chaque scénario non-PASS, appliquer l'ARBRE DE TRIAGE ci-dessous. Ne lire les
   screenshots que pour les cas qui exigent une confirmation visuelle.
5. **Proposer → GATE HUMAIN** — présenter à l'utilisateur : résumé PASS/FAIL/FLAKY,
   triage, et une PROPOSITION de suite. NE JAMAIS relancer un cycle complet sans
   validation humaine du nouveau besoin.
6. **Boucler ou clore** — si l'utilisateur valide une suite → retour phase 1.
   Sinon → `rig-loop bench release` + résumer.

## Arbre de triage (phase 4)

Lire `<repo>\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\loop\baseline.json`. Pour chaque
scénario `FAIL` ou `FLAKY` :

1. `verdict == "FLAKY"` OU `id` ∈ `baseline.knownFlakes` → **FLAKE** : ne pas remonter
   comme bug. Mentionner « flake connu ».
2. `id` ∈ `baseline.dataDriftScenarios` → **DATA-DRIFT** : ce n'est pas un bug du code,
   c'est une fixture/audience périmée. Proposer la correction de la donnée.
3. `failReason` parle d'un compteur attendu (`DIVERGE`, `attendus=`) → **MANIFEST STALE** :
   le compteur du manifest ne correspond plus. Proposer de corriger le manifest.
4. Sinon → **VRAI BUG** : remonter en priorité avec `failReason` + artefacts.

## Garde-fous

- Un `FAIL` n'est jamais déclaré « vrai bug » sans que le `retried:true` du result.json
  confirme que le retry-once a aussi échoué.
- La phase 5 est TOUJOURS un gate humain — la skill propose, l'humain décide.
- Avant tout `rig-loop run` : vérifier `rig-loop bench status` (phase 0).
- Si un nouveau flake apparaît de façon répétée, proposer de l'ajouter à `baseline.json`.

## Playbook

Référence détaillée : `<repo>\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\loop\LOOP.md`.
```

- [ ] **Step 2: Verify — skill découvrable**

Dans une session Claude Code à la racine du repo, lancer `/rig-loop` ou vérifier que
la skill apparaît dans la liste des skills disponibles. Expected : la skill se charge
et affiche les 6 phases.

- [ ] **Step 3: Checkpoint** — demander le « go » avant `git commit`.

---

## Task 10: Playbook `LOOP.md`

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/loop/LOOP.md`

- [ ] **Step 1: Créer `loop/LOOP.md`**

```markdown
# Boucle de travail rig-testing — Playbook

Filet d'onboarding. La skill `/rig-loop` automatise tout ceci ; ce doc sert aux
nouveaux et à ceux qui n'utilisent pas Claude Code.

## Le CLI

`SmokeRunner.exe` (dossier `bin\Release\net48\`), mode `--loop` :

| Commande | Effet |
|---|---|
| `--loop run [--scenarios all\|<id,id>] [--apply]` | lance les scénarios en parallèle, retry-once sur échec, écrit `result.json` |
| `--loop build` | build PROC_RETAUD + deploy, verdict unique |
| `--loop bench acquire\|release\|status` | lock du banc de test |

## Les 6 phases

0. **Acquérir le banc** — `--loop bench status`, puis `acquire`. Évite que 2 personnes
   lancent 16 RIG en même temps (machine saturée + DB corrompue).
1. **Cadrer** — transformer le besoin en scénarios concrets.
2. **Écrire** — éditer `RaptureScenarios\manifest.json` + fixtures JSON.
3. **Exécuter** — `--loop run`.
4. **Observer & trier** — lire `result.json` ; classer chaque échec via `baseline.json` :
   flake / data-drift / manifest périmé / vrai bug.
5. **Proposer** — formuler la suite. Décision = humaine.
6. **Boucler ou clore** — `--loop bench release` à la fin.

## Artefacts

- `result.json` + logs + screenshots : `%LOCALAPPDATA%\rig-wpf-testviewer\loop\runs\<runId>\`
- Historique : `%LOCALAPPDATA%\rig-wpf-testviewer\loop\history.jsonl`
- Triage : `loop\baseline.json` (versionné — flakes connus, data-drift connus)

## Owner

`baseline.json`, la skill et le CLI ont un **owner désigné** : _(à renseigner)_.
Tout nouveau flake récurrent → l'ajouter à `baseline.json` via une PR.
```

- [ ] **Step 2: Verify — lisible**

Ouvrir `loop\LOOP.md`, vérifier qu'il rend bien et que les 6 phases + le tableau CLI
sont cohérents avec les Tasks 3/7/8.

- [ ] **Step 3: Checkpoint** — demander le « go » avant `git commit`.

---

## Vérification finale (spec §11)

Après les 10 tasks, rejouer la checklist de la spec :

1. `--loop build` → verdict pass/fail unique lisible. ✅ Task 8.
2. `--loop run --scenarios all` → `result.json` valide + `runs/<id>/`. ✅ Tasks 3+5.
3. Un FAIL avec compteur manifest faux → classé « MANIFEST STALE ». ✅ skill Task 9.
4. `cas-diff-replacement-note` flaky → `verdict:"FLAKY"` après retry, pas remonté bug. ✅ Tasks 4+9.
5. `--loop bench acquire` ×2 → 2e refusé. ✅ Task 7.
6. Un collègue suit `LOOP.md` seul et complète une boucle. ✅ Task 10.

## Self-review du plan

- **Couverture spec** : §3.1 CLI → Tasks 1/3/7/8 ; §3.2 Mémoire → Tasks 5/6 ; §3.3 skill
  → Task 9 ; §4 phases → skill+playbook ; §7 adoption (lock/owner/playbook) → Tasks 7/10 ;
  §8 garde-fous (retry, gate) → Tasks 4/9. Aucune section orpheline.
- **Types** : `LoopRunResult` / `LoopScenarioResult` / `LoopSummary` (Task 2) utilisés
  identiques en Tasks 3/5. `LoopScenario` / `LoopManifest` (Task 3) cohérents.
  `LoopArtifacts.LoopRoot/RunDir/Persist` (Task 5) appelés par Tasks 3/7. Cohérent.
- **Pas de placeholder** : tous les steps de code donnent le code complet. Les stubs
  temporaires (Task 1/3) sont explicitement supprimés aux Tasks 3/5/7/8.
