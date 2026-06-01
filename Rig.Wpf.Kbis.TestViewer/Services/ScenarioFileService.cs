using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Lit / écrit les fichiers <c>.verified.json</c> (snapshots Verify). Sur disque
/// le dossier s'appelle <c>Goldens/</c> (convention Verify) mais côté UI on
/// parle de « scénarios ».
///
/// Format Verify pour les réponses HTTP : objet plat top-level avec
/// <c>StatusCode</c> (int), <c>Body</c> (JSON-string échappé, optionnel) et
/// les headers individuellement (ex : <c>WwwAuthenticate</c>, <c>ContentType</c>).
/// Aucun wrap <c>Headers: {…}</c> — chaque header est une clé top-level.
/// </summary>
public sealed class ScenarioFileService
{
    private readonly TestingPaths _paths;

    public ScenarioFileService(TestingPaths paths) { _paths = paths; }

    public bool DirExists => Directory.Exists(_paths.RegressionGoldensDir);
    public string Dir => _paths.RegressionGoldensDir;

    public IReadOnlyList<ScenarioFileInfo> List()
    {
        if (!DirExists) return Array.Empty<ScenarioFileInfo>();
        return Directory.EnumerateFiles(_paths.RegressionGoldensDir, "*.verified.json")
            .Select(p => new ScenarioFileInfo(Path.GetFileName(p), new FileInfo(p).Length))
            .OrderBy(g => g.FileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Lecture pretty-printed brute (legacy — pour debug).</summary>
    public string? Read(string fileName)
    {
        var path = SafePath(fileName);
        if (path is null || !File.Exists(path)) return null;
        var raw = File.ReadAllText(path);
        return TryPrettyPrint(raw);
    }

    /// <summary>Lecture structurée : sépare StatusCode / Body / headers individuels.</summary>
    public ScenarioContent? ReadStructured(string fileName)
    {
        var path = SafePath(fileName);
        if (path is null || !File.Exists(path)) return null;
        var raw = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var content = new ScenarioContent { FileName = fileName };

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "StatusCode" && prop.Value.ValueKind == JsonValueKind.Number)
            {
                content.StatusCode = prop.Value.GetInt32();
            }
            else if (prop.Name == "Body" && prop.Value.ValueKind == JsonValueKind.String)
            {
                var rawBody = prop.Value.GetString() ?? "";
                content.HasBody = true;
                content.BodyPretty = LooksLikeJson(rawBody) ? PrettyOrFallback(rawBody) : rawBody;
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                content.Headers.Add(new ScenarioHeader(prop.Name, prop.Value.GetString() ?? ""));
            }
            else
            {
                // Type complexe (objet, array, number) — non éditable en v1, conservé pour round-trip.
                content.RawExtras[prop.Name] = prop.Value.GetRawText();
            }
        }
        return content;
    }

    /// <summary>
    /// Réécrit le snapshot avec normalisation Verify-compatible : indentation 2 espaces,
    /// Body compacté (re-serialize sans whitespace) puis échappé dans une string JSON.
    /// </summary>
    public void Write(string fileName, ScenarioContent content)
    {
        var path = SafePath(fileName);
        if (path is null) throw new ArgumentException("Nom de fichier invalide", nameof(fileName));

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            // Ordre des clés : StatusCode d'abord, puis headers triés, Body en dernier
            // (cohérent avec ce que Verify produit en pratique sur ces tests).
            if (content.StatusCode.HasValue)
                writer.WriteNumber("StatusCode", content.StatusCode.Value);

            foreach (var kv in content.RawExtras.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(kv.Key);
                using var doc = JsonDocument.Parse(kv.Value);
                doc.WriteTo(writer);
            }

            foreach (var h in content.Headers)
                writer.WriteString(h.Name, h.Value);

            if (content.HasBody)
            {
                string compactBody;
                try
                {
                    using var bodyDoc = JsonDocument.Parse(content.BodyPretty);
                    compactBody = JsonSerializer.Serialize(bodyDoc.RootElement,
                        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }
                catch
                {
                    // Body pas du JSON valide — on persiste tel quel comme string.
                    compactBody = content.BodyPretty;
                }
                writer.WriteString("Body", compactBody);
            }

            writer.WriteEndObject();
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string? SafePath(string fileName)
    {
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')) return null;
        return Path.Combine(_paths.RegressionGoldensDir, fileName);
    }

    private static string PrettyOrFallback(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch { return raw; }
    }

    private static bool LooksLikeJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.AsSpan().TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[');
    }

    private static string TryPrettyPrint(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteElement(doc.RootElement, writer);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return raw; }
    }

    private static void WriteElement(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    w.WritePropertyName(prop.Name);
                    WriteElement(prop.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteElement(item, w);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                var s = el.GetString();
                if (LooksLikeJson(s))
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(s!);
                        WriteElement(nested.RootElement, w);
                        return;
                    }
                    catch { }
                }
                w.WriteStringValue(s);
                break;
            case JsonValueKind.Number: w.WriteRawValue(el.GetRawText()); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            case JsonValueKind.Null: w.WriteNullValue(); break;
            default: w.WriteRawValue(el.GetRawText()); break;
        }
    }
}

public sealed class ScenarioFileInfo
{
    public string FileName { get; }
    public long Size { get; }
    public ScenarioFileInfo(string FileName, long Size)
    {
        this.FileName = FileName; this.Size = Size;
        Metadata = ScenarioFileMetadata.Parse(FileName);
    }
    public ScenarioFileMetadata Metadata { get; }
    public string PrettyName => Metadata.PrettyName;
    public string SizeLabel => Size < 1024 ? $"{Size} o" : $"{Size / 1024.0:F1} Ko";
}

/// <summary>Parsing du filename <c>Class.Method_paramName=value_….verified.json</c>.</summary>
public sealed class ScenarioFileMetadata
{
    public string ClassName { get; }
    public string MethodName { get; }
    public string? Backend { get; }
    public IReadOnlyDictionary<string, string> OtherParams { get; }

    private ScenarioFileMetadata(string className, string methodName, string? backend,
        IReadOnlyDictionary<string, string> otherParams)
    {
        ClassName = className; MethodName = methodName; Backend = backend; OtherParams = otherParams;
    }

    public string PrettyName
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(MethodName);
            if (Backend is not null) sb.Append(" · ").Append(Backend);
            foreach (var kv in OtherParams) sb.Append(" · ").Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }
    }

    public static ScenarioFileMetadata Parse(string fileName)
    {
        var name = fileName.EndsWith(".verified.json", StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(0, fileName.Length - ".verified.json".Length)
            : fileName;

        var dotIdx = name.IndexOf('.');
        if (dotIdx < 0) return new ScenarioFileMetadata("Unknown", name, null,
            new Dictionary<string, string>());

        var className = name.Substring(0, dotIdx);
        var rest = name.Substring(dotIdx + 1);

        // Le premier marqueur "_<word>=" sépare la méthode des paramètres.
        var paramMarker = Regex.Match(rest, @"_(\w+)=");
        string methodName;
        string paramsStr;
        if (paramMarker.Success)
        {
            methodName = rest.Substring(0, paramMarker.Index);
            paramsStr = rest.Substring(paramMarker.Index + 1);
        }
        else
        {
            methodName = rest;
            paramsStr = "";
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seg in paramsStr.Split('_'))
        {
            var eq = seg.IndexOf('=');
            if (eq > 0) parsed[seg.Substring(0, eq)] = seg.Substring(eq + 1);
        }

        parsed.TryGetValue("backend", out var backend);
        parsed.Remove("backend");

        return new ScenarioFileMetadata(className, methodName, backend, parsed);
    }
}

/// <summary>Représentation structurée d'un snapshot Verify, éditable.</summary>
public sealed class ScenarioContent
{
    public string FileName { get; set; } = "";
    public int? StatusCode { get; set; }
    public List<ScenarioHeader> Headers { get; } = new();
    public bool HasBody { get; set; }
    public string BodyPretty { get; set; } = "";
    /// <summary>Top-level keys non-string non-StatusCode (rare — objets / arrays / numbers).</summary>
    public Dictionary<string, string> RawExtras { get; } = new();
}

public sealed class ScenarioHeader
{
    public string Name { get; set; }
    public string Value { get; set; }
    public ScenarioHeader(string name, string value) { Name = name; Value = value; }
}
