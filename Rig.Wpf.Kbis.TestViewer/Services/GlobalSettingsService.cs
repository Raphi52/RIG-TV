// SPDX-License-Identifier: Proprietary
// Service de gestion des settings globaux de Rig Testing.
// Persisté en JSON dans %LOCALAPPDATA%\rig-wpf-kbis\global-settings.json.
// À enrichir progressivement (Parallelism = 1er setting, d'autres viendront).
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Modèle POCO des settings globaux. Sérialisé/désérialisé tel quel.
/// Ajouter ici tout nouveau setting global. Garder les types simples
/// (int, bool, string, double) pour rester JSON-friendly.
/// </summary>
public sealed class GlobalSettings
{
    /// <summary>
    /// Nombre max d'instances RigClientAccueil simultanées lancées par
    /// le batch "All scenarios". Default = 4 (équilibre CPU/RAM/stabilité).
    /// Min 1, max 32 (au-delà, OS/UIA commencent à coincer sur le focus stealing).
    /// Surcharge l'env var RIG_SMOKE_PARALLELISM si présent en mémoire VM.
    /// </summary>
    [JsonPropertyName("parallelism")]
    public int Parallelism { get; set; } = 4;

    /// <summary>
    /// Si vrai, les screenshots itératifs (règle 16 CLAUDE.md) sont écrits dans
    /// <c>%USERPROFILE%\Code RIG\Audit\screenshots-loop\</c>. Permet aux agents
    /// IA de capturer l'état UI sans se reposer uniquement sur les logs.
    /// </summary>
    [JsonPropertyName("screenshotsLoopEnabled")]
    public bool ScreenshotsLoopEnabled { get; set; } = true;

    /// <summary>
    /// Si vrai (default), les workers --legacy-rapture-process lancent
    /// RigClientAccueil sur un Desktop Windows SÉPARÉ (HDESK invisible).
    /// Évite le vol de souris/focus pendant les tests.
    /// Si faux, les fenêtres RIG s'ouvrent sur le DESKTOP UTILISATEUR.
    /// Utilisable pour démontrer N instances RIG visibles en parallèle, mais
    /// ATTENTION : focus stealing + souris partagée pendant le test.
    /// Propagé aux workers via <c>ProcessStartInfo.EnvironmentVariables["RIG_DRIVER_HEADLESS"]</c>.
    /// </summary>
    [JsonPropertyName("headlessMode")]
    public bool HeadlessMode { get; set; } = true;

    /// <summary>
    /// Si vrai, le batch "All scenarios" interroge un cache de résultats
    /// (clé = hash code drivers + scenario JSON, valeur = PASS/FAIL connu).
    /// Sur cache-hit PASS, le scénario est skippé et le résultat émis sans
    /// relancer SmokeRunner. Gain massif (~5-10×) sur itérations cosmétiques
    /// où les drivers ne changent pas.
    ///
    /// ⚠ DÉSACTIVÉ par défaut : le cache PEUT masquer une régression si
    /// l'invalidation est mal câblée. À activer consciemment quand l'agent IA
    /// itère vite et veut juste re-vérifier ce qu'il vient de toucher.
    /// </summary>
    [JsonPropertyName("useTestResultCache")]
    public bool UseTestResultCache { get; set; } = false;

    /// <summary>
    /// Schéma de validation : numéro de version. Bump à chaque ajout de field
    /// (pour piloter une éventuelle migration des fichiers existants).
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;
}

/// <summary>
/// Charge/sauvegarde les <see cref="GlobalSettings"/> dans un fichier JSON
/// sous <c>%LOCALAPPDATA%\rig-wpf-kbis\global-settings.json</c>. Singleton
/// instancié au démarrage de MainWindow. Thread-safe en lecture, sérialise
/// les écritures (Save).
/// </summary>
public sealed class GlobalSettingsService
{
    private static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rig-wpf-kbis");

    private static readonly string DefaultPath = Path.Combine(DefaultDir, "global-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _writeLock = new();
    public string FilePath { get; }
    public GlobalSettings Current { get; private set; }

    public GlobalSettingsService(string? customPath = null)
    {
        FilePath = customPath ?? DefaultPath;
        Current = Load();
    }

    /// <summary>
    /// Charge depuis le disque. Si le fichier n'existe pas ou est corrompu,
    /// retourne un <see cref="GlobalSettings"/> par défaut (et écrit la valeur
    /// par défaut sur disque pour les prochaines fois).
    /// </summary>
    public GlobalSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new GlobalSettings();
                Save(fresh);
                return fresh;
            }
            var json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<GlobalSettings>(json, JsonOpts) ?? new GlobalSettings();
            // Clamp Parallelism dans une fourchette sûre (corruption manuelle)
            if (parsed.Parallelism < 1) parsed.Parallelism = 1;
            if (parsed.Parallelism > 32) parsed.Parallelism = 32;
            Current = parsed;
            return parsed;
        }
        catch (Exception)
        {
            // Quoi qu'il se passe, on ne crash pas l'app — fallback default.
            var fallback = new GlobalSettings();
            Current = fallback;
            return fallback;
        }
    }

    /// <summary>
    /// Sauvegarde sur disque. Crée le dossier parent au besoin. Thread-safe
    /// (lock sur _writeLock). Met aussi à jour <see cref="Current"/> en mémoire.
    /// </summary>
    public void Save(GlobalSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        // Clamp defensive avant écriture
        if (settings.Parallelism < 1) settings.Parallelism = 1;
        if (settings.Parallelism > 32) settings.Parallelism = 32;
        lock (_writeLock)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(FilePath, json);
            Current = settings;
        }
    }
}
