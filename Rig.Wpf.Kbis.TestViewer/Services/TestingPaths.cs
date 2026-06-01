using System;
using System.IO;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Configuration centrale des chemins pour le TestViewer. Tous les modules
/// (KBIS aujourd'hui, MANDATAIRE/IPE/… demain) déclarent leurs chemins ici,
/// sans hardcoder dans chaque service.
/// </summary>
public sealed class TestingPaths
{
    public TestingPaths(
        string smokeRunnerExe,
        string smokeRendersDir,
        string regressionProjectDir,
        string regressionScenariosDir,
        string regressionGoldensDir,
        string raptureRegressionProjectDir)
    {
        SmokeRunnerExe = smokeRunnerExe;
        SmokeRendersDir = smokeRendersDir;
        RegressionProjectDir = regressionProjectDir;
        RegressionScenariosDir = regressionScenariosDir;
        RegressionGoldensDir = regressionGoldensDir;
        RaptureRegressionProjectDir = raptureRegressionProjectDir;
    }

    /// <summary>Chemin vers <c>Rig.Wpf.Kbis.SmokeRunner.exe</c> (net48 console).</summary>
    public string SmokeRunnerExe { get; }

    /// <summary>Dossier où le SmokeRunner pose ses PNG (<c>artifacts/renders/</c>).</summary>
    public string SmokeRendersDir { get; }

    /// <summary>Dossier du projet xUnit <c>Rig.Kbis.RegressionTests</c> (.NET 8).</summary>
    public string RegressionProjectDir { get; }

    /// <summary>Dossier <c>Scenarios/</c> du projet xUnit, pour extraire les /// summary.</summary>
    public string RegressionScenariosDir { get; }

    /// <summary>Dossier <c>Goldens/</c> du projet xUnit, contenant les .verified.json.</summary>
    public string RegressionGoldensDir { get; }

    /// <summary>Dossier du projet xUnit Rapture (net48) : <c>Source/Wpf/Rig.Rapture.Tests</c>.</summary>
    public string RaptureRegressionProjectDir { get; }

    /// <summary>
    /// Résout les chemins en partant du dossier <see cref="AppContext.BaseDirectory"/>
    /// du TestViewer. Le mapping codé en dur ici suit la structure du repo :
    ///   <c>C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer\bin\…\net48\</c>
    /// pour le TestViewer, et <c>C:\Code RIG\Audit\poc-kbis\tests\…\</c> pour les xUnit.
    /// </summary>
    public static TestingPaths ResolveDefault()
    {
        var baseDir = AppContext.BaseDirectory; // …\Rig.Wpf.Kbis.TestViewer\bin\[<platform>\]<cfg>\net48\

        // SmokeRunner sibling — on cherche d'abord le SAME layout que le TestViewer
        // (x86\Debug, Debug, x86\Release, Release), puis on fallback aux autres combos.
        var smokeExe = ResolveSiblingExe(baseDir,
            thisProject:   "Rig.Wpf.Kbis.TestViewer",
            siblingProject:"Rig.Wpf.Kbis.SmokeRunner",
            exeFileName:   "Rig.Wpf.Kbis.SmokeRunner.exe",
            envOverride:   "RIG_KBIS_SMOKERUNNER_EXE");

        var smokeRenders = !string.IsNullOrEmpty(smokeExe)
            ? Path.Combine(Path.GetDirectoryName(smokeExe)!, "artifacts", "renders")
            : "";

        // Projet xUnit regression (.NET 8) sous Audit/. Remonte au repo root via .. .. .. .. .. ..
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..")); // …\RigApplication-testing\
        // Le 'C:\Code RIG\' est au-dessus du worktree
        var codeRigRoot = Path.GetFullPath(Path.Combine(repoRoot, ".."));
        var regressionRoot = FindFirstExisting(
            FromEnv("RIG_KBIS_REGRESSION_PROJECT_DIR"),
            Path.Combine(codeRigRoot, "Audit", "poc-kbis", "tests", "Rig.Kbis.RegressionTests"),
            @"C:\Code RIG\Audit\poc-kbis\tests\Rig.Kbis.RegressionTests");

        // Projet xUnit Rapture (net48) sous Source/Wpf/Rig.Rapture.Tests.
        // Path résolu via scan de baseDir pour trouver le sibling project (même approche
        // robuste que ResolveSiblingExe : handle x86 vs AnyCPU × Debug vs Release).
        var raptureRoot = FindFirstExisting(
            FromEnv("RIG_RAPTURE_REGRESSION_PROJECT_DIR"),
            ResolveSiblingProject(baseDir, "Rig.Wpf.Kbis.TestViewer", "Rig.Rapture.Tests"));

        return new TestingPaths(
            smokeRunnerExe:             smokeExe         ?? "",
            smokeRendersDir:            smokeRenders,
            regressionProjectDir:       regressionRoot   ?? "",
            regressionScenariosDir:     regressionRoot is null ? "" : Path.Combine(regressionRoot, "Scenarios"),
            regressionGoldensDir:       regressionRoot is null ? "" : Path.Combine(regressionRoot, "Goldens"),
            raptureRegressionProjectDir: raptureRoot     ?? "");
    }

    private static string? FromEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string? FindFirstExisting(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (File.Exists(c) || Directory.Exists(c)) return c;
        }
        // Aucun candidat trouvé — on rend quand même le dernier path pour qu'on l'affiche dans le diagnostic.
        for (int i = candidates.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(candidates[i])) return candidates[i];
        }
        return null;
    }

    /// <summary>
    /// Résout le chemin d'un projet sibling (par exemple <c>Rig.Rapture.Tests</c>) à partir
    /// du baseDir du TestViewer. Pareil idée que <see cref="ResolveSiblingExe"/> mais on
    /// retourne juste le dossier projet (sans assumption sur bin/exe).
    ///
    /// Robust à x86 vs AnyCPU × Debug vs Release : on parse le baseDir pour trouver le
    /// marker <c>&lt;thisProject&gt;\bin\</c>, on extrait le parent (le dossier <c>Wpf\</c>),
    /// et on retourne <c>&lt;parent&gt;\&lt;siblingProject&gt;</c>.
    /// </summary>
    private static string ResolveSiblingProject(string baseDir, string thisProject, string siblingProject)
    {
        var marker = thisProject + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var idx = baseDir.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Fallback: structure inattendue, on remonte 4 niveaux et tente.
            return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", siblingProject));
        }
        var parentFolder = baseDir.Substring(0, idx); // …\Source\Wpf\
        return Path.Combine(parentFolder, siblingProject);
    }

    /// <summary>
    /// Résout le chemin d'un exe sibling à partir du baseDir du TestViewer.
    /// On extrait le suffix bin\…\netxx\ du baseDir et on l'applique sur le projet
    /// sibling — ainsi un TestViewer buildé en x86\Debug retrouve un SmokeRunner
    /// x86\Debug, etc. En dernier ressort on essaie les combos courants.
    /// </summary>
    private static string ResolveSiblingExe(
        string baseDir, string thisProject, string siblingProject,
        string exeFileName, string envOverride)
    {
        // 1) Env var override (debug / scénarios spéciaux).
        var env = FromEnv(envOverride);
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env!;

        // 2) Localise le projet TestViewer dans le baseDir.
        var marker = thisProject + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var idx = baseDir.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Structure inattendue — on rend un chemin "expected" pour diag.
            return Path.Combine(baseDir, "..", siblingProject, "bin", "Debug", "net48", exeFileName);
        }

        var wpfFolder = baseDir.Substring(0, idx); // …\Wpf\
        var binSuffix = baseDir.Substring(idx + thisProject.Length + 1); // bin\…\netxx\
        var siblingBinRoot = Path.Combine(wpfFolder, siblingProject);

        // 3) Préfère le même layout que le TestViewer.
        var sameLayout = Path.Combine(siblingBinRoot, binSuffix, exeFileName);
        if (File.Exists(sameLayout)) return sameLayout;

        // 4) Sinon scanne les combos habituels.
        foreach (var platform in new[] { "x86", "" })
        foreach (var config   in new[] { "Debug", "Release" })
        foreach (var tfm      in new[] { "net48", "net472" })
        {
            var path = string.IsNullOrEmpty(platform)
                ? Path.Combine(siblingBinRoot, "bin", config, tfm, exeFileName)
                : Path.Combine(siblingBinRoot, "bin", platform, config, tfm, exeFileName);
            if (File.Exists(path)) return path;
        }

        // 5) Rien trouvé — on rend le sameLayout pour que l'erreur dans l'UI pointe
        //    sur le path attendu (utile pour diagnostiquer "rebuild manquant").
        return sameLayout;
    }
}
