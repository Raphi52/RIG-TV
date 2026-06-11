using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Découvre les tests via <c>dotnet test --list-tests</c> et enrichit chaque
/// test avec sa description extraite du <c>/// &lt;summary&gt;</c> du source C#.
///
/// Port direct de <c>TestCatalogService</c> du Blazor TestViewer.
/// </summary>
public sealed class RegressionCatalog
{
    private readonly string _projectDir;
    private readonly string _scenariosDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<RegressionTestInfo>? _cache;

    /// <summary>Constructeur historique KBIS : prend les dirs depuis <see cref="TestingPaths"/>.</summary>
    public RegressionCatalog(TestingPaths paths)
        : this(paths.RegressionProjectDir, paths.RegressionScenariosDir) { }

    /// <summary>Constructeur multi-projet : pour instancier N catalogs (KBIS, Rapture, …).</summary>
    public RegressionCatalog(string projectDir, string scenariosDir)
    {
        _projectDir = projectDir;
        _scenariosDir = scenariosDir;
    }

    public async Task<IReadOnlyList<RegressionTestInfo>> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cache is not null) return _cache;
            _cache = await DiscoverAsync(ct).ConfigureAwait(false);
            return _cache;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<RegressionTestInfo>> DiscoverAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_projectDir)) return new List<RegressionTestInfo>();

        var fqns = await ListTestFullNamesAsync(ct).ConfigureAwait(false);
        var summaries = ParseSummariesFromSources();

        var tests = new List<RegressionTestInfo>();
        foreach (var fqn in fqns)
        {
            // FQN: Rig.Kbis.RegressionTests.Scenarios.Kbis.<Class>.<Method>(params)
            var m = Regex.Match(fqn, @"^(?<ns>.+)\.(?<cls>[^.]+)\.(?<method>[^.(]+)(?<params>\(.*\))?$");
            if (!m.Success) continue;
            var className  = m.Groups["cls"].Value;
            var methodName = m.Groups["method"].Value;
            var paramStr   = m.Groups["params"].Value;
            var summary    = summaries.TryGetValue(methodName, out var s) ? s : "";
            var category   = ClassifyByClassName(className);

            tests.Add(new RegressionTestInfo(
                FullyQualifiedName: fqn,
                ClassName:          className,
                MethodName:         methodName,
                Parameters:         paramStr,
                Description:        summary,
                Category:           category));
        }
        return tests
            .OrderBy(t => t.Category)
            .ThenBy(t => t.ClassName)
            .ThenBy(t => t.MethodName)
            .ThenBy(t => t.Parameters)
            .ToList();
    }

    private async Task<List<string>> ListTestFullNamesAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "test --list-tests --nologo --no-build --verbosity quiet",
            WorkingDirectory = _projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start a renvoyé null pour dotnet test --list-tests");

        var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await Task.Run(p.WaitForExit, ct).ConfigureAwait(false);

        return stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("    "))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l.Contains('.'))
            .ToList();
    }

    private Dictionary<string, string> ParseSummariesFromSources()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_scenariosDir)) return result;

        foreach (var file in Directory.EnumerateFiles(_scenariosDir, "*.cs", SearchOption.AllDirectories))
        {
            ExtractFromFile(file, result);
        }
        return result;
    }

    private static readonly Regex MethodDeclRegex = new(
        @"^\s*public\s+(?:async\s+)?(?:Task|void)\s+(?<name>\w+)\s*\(",
        RegexOptions.Compiled);

    internal static void ExtractFromFile(string path, Dictionary<string, string> bucket)
    {
        var lines = File.ReadAllLines(path);
        var current = new List<string>();
        bool inSummary = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("///"))
            {
                var content = line.TrimStart('/').Trim();
                if (content.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
                {
                    current.Clear();
                    inSummary = true;
                    var after = content.Substring(content.IndexOf('>') + 1);
                    if (!string.IsNullOrWhiteSpace(after)) current.Add(after.Trim());
                    if (content.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase) >= 0) inSummary = false;
                }
                else if (content.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var idx = content.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
                    var before = content.Substring(0, idx);
                    if (!string.IsNullOrWhiteSpace(before)) current.Add(before.Trim());
                    inSummary = false;
                }
                else if (inSummary) { current.Add(content); }
                continue;
            }
            if (line.Length == 0 || line.StartsWith("[")) continue;
            var match = MethodDeclRegex.Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (current.Count > 0 && !bucket.ContainsKey(name))
                {
                    bucket[name] = string.Join(" ", current).Trim();
                }
                current.Clear();
                inSummary = false;
            }
            else if (current.Count > 0 && !line.StartsWith("///"))
            {
                if (!line.StartsWith("public ") && !line.StartsWith("private ") && !line.StartsWith("internal "))
                {
                    current.Clear();
                }
            }
        }
    }

    internal static string ClassifyByClassName(string className) => className switch
    {
        "FoundActiveScenarios"      => "Lecture",
        "ShapeScenarios"            => "Lecture (patterns)",
        "OrderByInvariantScenarios" => "Lecture (invariants)",
        "NotFoundScenarios"         => "Erreurs",
        "AuthScenarios"             => "Auth & Validation",
        _                           => "Autres"
    };
}

public sealed class RegressionTestInfo
{
    public string FullyQualifiedName { get; }
    public string ClassName { get; }
    public string MethodName { get; }
    public string Parameters { get; }
    public string Description { get; }
    public string Category { get; }
    public RegressionTestInfo(
        string FullyQualifiedName, string ClassName, string MethodName,
        string Parameters, string Description, string Category)
    {
        this.FullyQualifiedName = FullyQualifiedName;
        this.ClassName = ClassName;
        this.MethodName = MethodName;
        this.Parameters = Parameters;
        this.Description = Description;
        this.Category = Category;
    }
    public string DisplayName => string.IsNullOrEmpty(Parameters) ? MethodName : MethodName + Parameters;
    public string? Backend
    {
        get
        {
            var m = Regex.Match(Parameters, @"backend:\s*""(?<b>[^""]+)""");
            return m.Success ? m.Groups["b"].Value : null;
        }
    }
}
