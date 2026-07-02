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

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>DTO minimal d'un scénario du manifest.json (sous-ensemble utilisé par le loop).</summary>
[DataContract]
internal sealed class LoopScenario
{
    [DataMember(Name = "id")]                public string Id { get; set; }
    [DataMember(Name = "jsonFile")]          public string JsonFile { get; set; }
    [DataMember(Name = "defaultAudienceId")] public int? DefaultAudienceId { get; set; }
    [DataMember(Name = "expectedAppliedModifications")] public int? ExpectedAppliedModifications { get; set; }
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
        string scenariosArg = ArgVal(args, "--scenarios") ?? "all";
        bool apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        int parallelism = 4;
        var envPar = Environment.GetEnvironmentVariable("RIG_SMOKE_PARALLELISM");
        if (!string.IsNullOrEmpty(envPar) && int.TryParse(envPar, out var ep) && ep > 0) parallelism = ep;

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

        var startedAt = DateTime.Now;
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
                catch (Exception ex)
                {
                    // Une exception inattendue (ex. Win32Exception de Process.Start)
                    // ne doit PAS avorter le batch ni faire disparaître le scénario
                    // du RECAP : on l'enregistre comme FAIL explicite.
                    results.Add(new LoopScenarioResult { Id = s.Id, Verdict = "FAIL",
                        DurationMs = 0, FailReason = "Exception inattendue : " + ex.Message });
                    Console.WriteLine($"   ✗ [{s.Id}] EXCEPTION — {ex.Message}");
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

        var ordered = results.OrderBy(r => selected.FindIndex(s => s.Id == r.Id)).ToList();
        var result = new LoopRunResult
        {
            RunId = runId,
            StartedAt = startedAt.ToString("o"),
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

        LoopArtifacts.Persist(result);

        Console.WriteLine($"╠═══ RECAP — {result.Summary.Passed} PASS / {result.Summary.Failed} FAIL / {result.Summary.Flaky} FLAKY / {result.Summary.Skipped} SKIP en {batchSw.Elapsed.TotalSeconds:F1}s ═══╣");
        Console.WriteLine($"╚═══ result.json : {Path.Combine(LoopArtifacts.RunDir(runId), "result.json")} ═══╝");
        return result.Summary.Failed > 0 ? 1 : 0;
    }

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

    /// <summary>Spawn un worker SmokeRunner --rapture-selfdrive. Retourne (exitCode, failReason).</summary>
    private static async Task<(int exitCode, string failReason)> SpawnWorker(
        LoopScenario s, string jsonPath, string smokeExe, bool apply)
    {
        string a = $"--rapture-selfdrive --json \"{jsonPath}\" --scenario-id \"{s.Id}\"";
        if (apply) a += " --apply";
        if (s.DefaultAudienceId.HasValue) a += $" --audience-id {s.DefaultAudienceId.Value}";
        // Sans ce flag, un scénario apply qui n'écrit plus rien (régression) passerait vert (0 diff = 0 erreur).
        if (apply && s.ExpectedAppliedModifications.HasValue) a += $" --expected-applied-modifications {s.ExpectedAppliedModifications.Value}";
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
            // Ordre critique : EnableRaisingEvents AVANT l'abonnement à Exited,
            // et le garde HasExited APRÈS — sinon un process qui meurt entre
            // Process.Start et l'abonnement ne déclencherait jamais exitTcs (deadlock 180s).
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

    /// <summary>Extrait la 1ère ligne « Exception: » ou « DIVERGE » ou « ⚠ » du stdout worker comme cause.</summary>
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
