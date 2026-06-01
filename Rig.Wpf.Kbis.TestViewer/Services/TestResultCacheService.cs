// SPDX-License-Identifier: Proprietary
// ML LOOP Phase 4 — Cache des résultats de tests E2E.
//
// Mémorise (codeHash × scenarioId → status/duration/detail) dans
// %LOCALAPPDATA%\rig-wpf-kbis\test-cache.json. Pendant un batch, si une
// entrée PASS existe pour le couple (codeHash courant, scenario), on skip
// la re-exécution du SmokeRunner et on émet le résultat cached.
//
// Pourquoi pas SQLite tout de suite ?
//   - 16 scénarios × N itérations = quelques dizaines d'entrées. JSON suffit
//     largement en perf et n'introduit pas de dépendance NuGet/native lib.
//   - L'API ci-dessous (Get/Set/ComputeCodeHash/Clear) est volontairement
//     identique à ce qu'on aurait avec SQLite — swap futur trivial.
//
// Invalidation :
//   - Tout changement dans LegacyDriver.cs / TestViewerDriver.cs /
//     Interaction.cs / SmokeRunner Program.cs (les pieces qui pilotent
//     l'exécution réelle) → nouveau codeHash → cache invalidé pour tous.
//   - Tout changement du JSON scenario → scenarioHash différent →
//     l'entrée précédente n'est jamais matchée (mais reste en archive).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Une entrée de cache : status du test pour un couple (codeHash × scenarioId).
/// </summary>
public sealed class CachedTestResult
{
    [JsonPropertyName("status")]      public string Status { get; set; } = "";
    [JsonPropertyName("duration_s")]  public double DurationS { get; set; }
    [JsonPropertyName("detail")]      public string Detail { get; set; } = "";
    [JsonPropertyName("cached_at")]   public string CachedAt { get; set; } = "";
}

/// <summary>
/// POCO racine du fichier JSON cache. Map clé → résultat.
/// Clé = SHA-256 de (codeHash + ":" + scenarioId), 64 hex chars.
/// </summary>
public sealed class TestResultCacheFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("entries")]        public Dictionary<string, CachedTestResult> Entries { get; set; } = new();
}

/// <summary>
/// Service de cache test results. Singleton instancié au démarrage MainWindow,
/// même pattern que <see cref="GlobalSettingsService"/>. Thread-safe en lecture,
/// sérialise les écritures via lock.
/// </summary>
public sealed class TestResultCacheService
{
    private static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rig-wpf-kbis");

    private static readonly string DefaultPath = Path.Combine(DefaultDir, "test-cache.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _writeLock = new();
    private TestResultCacheFile _data;

    public string FilePath { get; }

    /// <summary>
    /// ML LOOP S4.1 — Dernière erreur rencontrée à la sérialisation cache.
    /// Permet aux consommateurs (UI Settings, ml-loop.ps1) de signaler une
    /// dégradation silencieuse (disque plein, permissions, etc.) sans
    /// crash sur la chaîne de Set/Get.
    /// </summary>
    public string? LastPersistError { get; private set; }

    public TestResultCacheService(string? customPath = null)
    {
        FilePath = customPath ?? DefaultPath;
        _data = Load();
    }

    public int CountEntries
    {
        get
        {
            lock (_writeLock) return _data.Entries.Count;
        }
    }

    /// <summary>
    /// Recherche une entrée pour (codeHash, scenarioId). Retourne null si
    /// pas de hit. La clé combinée codeHash + ":" + scenarioId garantit
    /// l'invalidation auto au moindre changement des drivers.
    /// </summary>
    public CachedTestResult? Get(string codeHash, string scenarioId)
    {
        if (string.IsNullOrEmpty(codeHash) || string.IsNullOrEmpty(scenarioId)) return null;
        var key = MakeKey(codeHash, scenarioId);
        lock (_writeLock)
        {
            return _data.Entries.TryGetValue(key, out var r) ? r : null;
        }
    }

    /// <summary>
    /// Stocke / met à jour un résultat. Persist immédiatement sur disque
    /// (fail-safe : si l'app crash entre deux scénarios, on garde le
    /// progrès dans le cache).
    /// </summary>
    public void Set(string codeHash, string scenarioId, string status, double durationS, string detail)
    {
        if (string.IsNullOrEmpty(codeHash) || string.IsNullOrEmpty(scenarioId)) return;
        var key = MakeKey(codeHash, scenarioId);
        var entry = new CachedTestResult
        {
            Status = status,
            DurationS = Math.Round(durationS, 2),
            Detail = detail ?? "",
            CachedAt = DateTime.UtcNow.ToString("o"),
        };
        lock (_writeLock)
        {
            _data.Entries[key] = entry;
            Persist();
        }
    }

    /// <summary>Vide entièrement le cache (sur demande user / clear button).</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _data = new TestResultCacheFile();
            Persist();
        }
    }

    /// <summary>
    /// Calcule un hash SHA-256 stable des fichiers code qui influencent
    /// l'exécution réelle d'un scenario. Si N'IMPORTE QUEL de ces fichiers
    /// change, le hash change et le cache est implicitement invalidé.
    ///
    /// Liste : LegacyDriver.cs, TestViewerDriver.cs, Interaction.cs,
    /// SmokeRunner Program.cs. Volontairement large pour éviter de garder
    /// des résultats stales si on touche au pipeline.
    /// </summary>
    public static string ComputeCodeHash(IEnumerable<string> sourceFilePaths)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var path in sourceFilePaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var hash = sha.ComputeHash(bytes);
                sb.Append(Path.GetFileName(path)).Append(':').Append(Convert.ToBase64String(hash)).Append(';');
            }
            catch
            {
                // Lecture échouée → on inclut le nom pour casser tout cache existant.
                sb.Append(Path.GetFileName(path)).Append(":READ_ERR;");
            }
        }
        var aggregate = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return BitConverter.ToString(aggregate).Replace("-", "").ToLowerInvariant().Substring(0, 16);
    }

    /// <summary>
    /// Snapshot des entrées sous forme de stats simples (pour UI/log).
    /// </summary>
    public (int total, int pass, int fail) Stats()
    {
        lock (_writeLock)
        {
            var entries = _data.Entries.Values.ToList();
            return (entries.Count,
                    entries.Count(e => e.Status == "PASS"),
                    entries.Count(e => e.Status == "FAIL"));
        }
    }

    private static string MakeKey(string codeHash, string scenarioId)
        => codeHash + ":" + scenarioId;

    private TestResultCacheFile Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new TestResultCacheFile();
            var json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<TestResultCacheFile>(json, JsonOpts);
            return parsed ?? new TestResultCacheFile();
        }
        catch
        {
            // Cache corrompu → repartir clean. C'est un cache, pas du data sensible.
            return new TestResultCacheFile();
        }
    }

    private void Persist()
    {
        // ML LOOP S4.1 — Persist défensif : disque plein, permissions, file lock
        // par antivirus, network share offline → ne crash pas le batch. L'état
        // in-memory reste correct ; un retry au Set suivant peut réussir.
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_data, JsonOpts);
            // Atomic write : .tmp puis Move pour éviter qu'un reader voie un
            // fichier corrompu mid-write (cas du Get concurrent en multi-batch).
            var tmpPath = FilePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(FilePath))
            {
                try { File.Replace(tmpPath, FilePath, null); }
                catch
                {
                    // Replace peut échouer si dest sous lock — fallback Delete+Move
                    File.Delete(FilePath);
                    File.Move(tmpPath, FilePath);
                }
            }
            else
            {
                File.Move(tmpPath, FilePath);
            }
            LastPersistError = null;
        }
        catch (Exception ex)
        {
            LastPersistError = ex.GetType().Name + ": " + ex.Message;
            // Pas de log direct (pas de dépendance Log dans Services\) — caller
            // peut lire LastPersistError ou inspecter via Stats avant/après.
        }
    }
}
