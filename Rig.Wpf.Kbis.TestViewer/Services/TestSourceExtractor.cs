using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Retrouve la source d'une méthode de test xUnit dans <c>Scenarios/</c> :
///  - le fichier .cs qui la contient,
///  - les lignes du corps de la méthode (signature → accolade fermante),
///  - le commentaire <c>/// &lt;summary&gt;</c> attaché.
/// Sert à enrichir le panneau de détail du scénario (contexte « pourquoi ce test »).
/// </summary>
public sealed class TestSourceExtractor
{
    private readonly TestingPaths _paths;

    public TestSourceExtractor(TestingPaths paths) { _paths = paths; }

    public TestSourceSnippet? Find(string className, string methodName)
    {
        if (!Directory.Exists(_paths.RegressionScenariosDir)) return null;

        var files = Directory.EnumerateFiles(_paths.RegressionScenariosDir, "*.cs", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                // Match la signature : "public async Task <methodName>(" ou "public void <methodName>("
                var sig = Regex.Match(trimmed,
                    @"^\s*public\s+(?:async\s+)?(?:Task|void)\s+" + Regex.Escape(methodName) + @"\s*\(");
                if (!sig.Success) continue;

                // On a trouvé la signature. Extract le summary qui précède (lignes /// avant le sig).
                var summary = ExtractSummaryAbove(lines, i);
                // Extract le corps de la méthode jusqu'à l'accolade fermante.
                var bodyLines = ExtractMethodBody(lines, i);
                // Class containing the method.
                var detectedClass = DetectContainingClass(lines, i);

                return new TestSourceSnippet(
                    FilePath:   file,
                    LineNumber: i + 1,
                    ClassName:  detectedClass ?? className,
                    Summary:    summary,
                    Body:       string.Join("\n", bodyLines));
            }
        }
        return null;
    }

    private static string? DetectContainingClass(string[] lines, int methodLineIdx)
    {
        // Cherche la dernière déclaration de classe au-dessus.
        for (int j = methodLineIdx; j >= 0; j--)
        {
            var m = Regex.Match(lines[j], @"^\s*(?:public|internal)\s+(?:partial\s+|sealed\s+|abstract\s+)*class\s+(?<n>\w+)");
            if (m.Success) return m.Groups["n"].Value;
        }
        return null;
    }

    private static string? ExtractSummaryAbove(string[] lines, int methodLineIdx)
    {
        var current = new System.Collections.Generic.List<string>();
        bool inSummary = false;
        // On scan vers le bas depuis 30 lignes au-dessus pour gérer attributs intercalés.
        int start = Math.Max(0, methodLineIdx - 30);
        for (int i = start; i < methodLineIdx; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("///"))
            {
                var c = t.TrimStart('/').Trim();
                if (c.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
                {
                    current.Clear();
                    inSummary = true;
                    var after = c.Substring(c.IndexOf('>') + 1);
                    if (!string.IsNullOrWhiteSpace(after)) current.Add(after.Trim());
                    if (c.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase) >= 0) inSummary = false;
                }
                else if (c.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var before = c.Substring(0, c.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(before)) current.Add(before.Trim());
                    inSummary = false;
                }
                else if (inSummary) current.Add(c);
            }
            else if (t.Length == 0 || t.StartsWith("[") || t.StartsWith("//"))
            {
                // Attribut [Theory] ou ligne vide : continuer
            }
            else
            {
                // Du vrai code → reset
                current.Clear();
                inSummary = false;
            }
        }
        return current.Count > 0 ? string.Join(" ", current).Trim() : null;
    }

    private static System.Collections.Generic.List<string> ExtractMethodBody(string[] lines, int sigLine)
    {
        // Capture de la signature jusqu'à la fermeture { … }
        var output = new System.Collections.Generic.List<string>();
        int braceDepth = 0;
        bool entered = false;
        // Capture aussi les attributs juste au-dessus pour le contexte.
        int from = sigLine;
        for (int k = sigLine - 1; k >= 0; k--)
        {
            var t = lines[k].TrimStart();
            if (t.StartsWith("[") || t.Length == 0) { from = k; continue; }
            break;
        }
        // Drop trailing blank lines from attribute scan
        while (from < sigLine && lines[from].Trim().Length == 0) from++;

        for (int i = from; i < lines.Length; i++)
        {
            output.Add(lines[i]);
            foreach (var ch in lines[i])
            {
                if (ch == '{') { braceDepth++; entered = true; }
                else if (ch == '}') { braceDepth--; }
            }
            if (entered && braceDepth == 0) break;
            if (output.Count > 80) break; // garde-fou
        }
        return output;
    }
}

public sealed class TestSourceSnippet
{
    public string FilePath { get; }
    public int LineNumber { get; }
    public string ClassName { get; }
    public string? Summary { get; }
    public string Body { get; }
    public TestSourceSnippet(string FilePath, int LineNumber, string ClassName, string? Summary, string Body)
    {
        this.FilePath = FilePath;
        this.LineNumber = LineNumber;
        this.ClassName = ClassName;
        this.Summary = Summary;
        this.Body = Body;
    }
}
