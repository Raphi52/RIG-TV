using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Lit le <c>manifest.json</c> du dossier RaptureScenarios (embarqué par défaut
/// dans <c>bin\Release\net48\RaptureScenarios\</c>) ou d'un dossier custom choisi
/// par l'utilisateur. Expose la liste typée des scénarios pour le combo du tab
/// Smoke Import. Re-loadable au runtime quand l'utilisateur change le path.
/// </summary>
public sealed class RaptureScenarioCatalog
{
    /// <summary>Nom du manifest dans chaque dossier de scénarios.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Localise le dossier RaptureScenarios par défaut : à côté du .exe TestViewer.
    /// Suit le zip puisque le csproj fait CopyToOutputDirectory=PreserveNewest sur
    /// <c>RaptureScenarios\**\*.*</c>.
    /// </summary>
    public static string DefaultScenariosDir()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "RaptureScenarios");
    }

    /// <summary>
    /// Charge le manifest depuis <paramref name="scenariosDir"/>. Tolérant : si le
    /// dossier ou le manifest n'existe pas, retourne une liste vide (l'UI affichera
    /// un placeholder "aucun scénario disponible").
    /// </summary>
    public static IReadOnlyList<RaptureScenario> Load(string scenariosDir)
    {
        try
        {
            if (string.IsNullOrEmpty(scenariosDir) || !Directory.Exists(scenariosDir))
                return Array.Empty<RaptureScenario>();
            var manifestPath = Path.Combine(scenariosDir, ManifestFileName);
            if (!File.Exists(manifestPath)) return Array.Empty<RaptureScenario>();

            var json = File.ReadAllText(manifestPath);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var manifest = JsonSerializer.Deserialize<RaptureScenarioManifest>(json, opts);
            if (manifest?.Scenarios is null) return Array.Empty<RaptureScenario>();

            // Résout chaque jsonFile par rapport au dossier du manifest, garde
            // uniquement les scénarios dont le JSON existe sur disque (les autres
            // sont des entrées orphelines à supprimer du manifest).
            var resolved = new List<RaptureScenario>();
            foreach (var s in manifest.Scenarios)
            {
                if (string.IsNullOrEmpty(s.JsonFile)) continue;
                var path = Path.Combine(scenariosDir, s.JsonFile);
                if (!File.Exists(path))
                {
                    // On garde quand même dans la liste avec un flag IsBroken,
                    // pour que l'utilisateur voie l'entrée et puisse corriger.
                    s.ResolvedJsonPath = path;
                    s.IsBroken = true;
                }
                else
                {
                    s.ResolvedJsonPath = path;
                    s.IsBroken = false;
                }
                resolved.Add(s);
            }
            return resolved;
        }
        catch (Exception ex)
        {
            Log.Error("RaptureScenarioCatalog.Load", ex);
            return Array.Empty<RaptureScenario>();
        }
    }
}

/// <summary>Format du fichier <c>manifest.json</c>.</summary>
public sealed class RaptureScenarioManifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("scenarios")] public List<RaptureScenario> Scenarios { get; set; } = new();
}

/// <summary>
/// Un scénario du catalogue : identifiant + JSON par défaut + audience cible +
/// stats attendues. Override possible côté UI sur le JsonPath et l'AudienceId.
/// </summary>
public sealed class RaptureScenario
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("jsonFile")] public string JsonFile { get; set; } = "";
    [JsonPropertyName("defaultAudienceId")] public int? DefaultAudienceId { get; set; }
    [JsonPropertyName("audienceDate")] public string? AudienceDate { get; set; }
    [JsonPropertyName("audienceHeure")] public string? AudienceHeure { get; set; }
    [JsonPropertyName("audienceChambre")] public string? AudienceChambre { get; set; }
    [JsonPropertyName("expectedCase")] public string? ExpectedCase { get; set; }
    [JsonPropertyName("expectedWarnings")] public int? ExpectedWarnings { get; set; }
    /// <summary>
    /// Compteur "modifications détectées" attendu sur la recap (= tile UI, AVANT
    /// décochage de l'utilisateur). Si non spécifié, le UI assertion s'appuie sur
    /// <see cref="ExpectedModifications"/> par compat.
    /// </summary>
    [JsonPropertyName("expectedDetectedModifications")] public int? ExpectedDetectedModifications { get; set; }
    /// <summary>
    /// Compteur DB AUDIT_IMPORT_RAPTURE attendu (= modifications réellement appliquées
    /// après clic Importer). Peut différer de ExpectedDetectedModifications quand des
    /// lignes sont décochées par défaut. Si non spécifié, fallback sur ExpectedModifications.
    /// </summary>
    [JsonPropertyName("expectedAppliedModifications")] public int? ExpectedAppliedModifications { get; set; }
    /// <summary>
    /// Legacy : utilisé pour les 2 assertions (UI + DB) si les fields plus précis
    /// ci-dessus ne sont pas renseignés.
    /// </summary>
    [JsonPropertyName("expectedModifications")] public int? ExpectedModifications { get; set; }

    /// <summary>Helper : valeur à utiliser pour l'assertion UI (recap counter "modif détectées").</summary>
    [JsonIgnore] public int? EffectiveExpectedDetected =>
        ExpectedDetectedModifications ?? ExpectedModifications;
    /// <summary>Helper : valeur à utiliser pour l'assertion DB (AUDIT count).</summary>
    [JsonIgnore] public int? EffectiveExpectedApplied =>
        ExpectedAppliedModifications ?? ExpectedModifications;
    [JsonPropertyName("expectedValidationErrors")] public int? ExpectedValidationErrors { get; set; }
    [JsonPropertyName("expectedMessageContains")] public string? ExpectedMessageContains { get; set; }
    // Assertions ajoutées 2026-07-06 : compteurs UI recap "erreur bloquante"/"affaires bloquées" (cas-err qui
    // affichent la recap) ; affaires ignorées par le Diff (cas affaire-non-trouvee) ; texte du DialogBox
    // ERROR_PARSE (json-malformé, pas de recap). Passés aux workers par MainWindowViewModel.
    [JsonPropertyName("expectedErrors")] public int? ExpectedErrors { get; set; }
    [JsonPropertyName("expectedBlocked")] public int? ExpectedBlocked { get; set; }
    [JsonPropertyName("expectedAffairesIgnorees")] public int? ExpectedAffairesIgnorees { get; set; }
    [JsonPropertyName("expectedErrorContains")] public string? ExpectedErrorContains { get; set; }
    // Compteurs VISIBLE-spécifiques : la tuile UI recap "modifications détectées"/"avertissements" peut DIFFÉRER
    // du diff selfdrive (mesuré 2026-07-06 : subset UI=1 vs selfdrive=31 ; a-bis UI=23 vs 1). Le chemin visible
    // utilise ces valeurs si présentes, sinon fallback sur les valeurs selfdrive.
    [JsonPropertyName("expectedVisibleDetectedModifications")] public int? ExpectedVisibleDetectedModifications { get; set; }
    [JsonPropertyName("expectedVisibleWarnings")] public int? ExpectedVisibleWarnings { get; set; }
    [JsonIgnore] public int? EffectiveVisibleDetected => ExpectedVisibleDetectedModifications ?? EffectiveExpectedDetected;
    [JsonIgnore] public int? EffectiveVisibleWarnings => ExpectedVisibleWarnings ?? ExpectedWarnings;
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }

    /// <summary>Chemin absolu du JSON (résolu par <see cref="RaptureScenarioCatalog.Load"/>).</summary>
    [JsonIgnore] public string? ResolvedJsonPath { get; set; }

    /// <summary>True si le JSON référencé est introuvable sur disque (entrée invalide du manifest).</summary>
    [JsonIgnore] public bool IsBroken { get; set; }

    /// <summary>Format display pour combo / tooltip.</summary>
    [JsonIgnore] public string DisplayName =>
        IsBroken ? $"⚠ {Name} (JSON introuvable)" : Name;

    public override string ToString() => DisplayName;

    /// <summary>Sous-titre court pour l'UI : "audience 28590 · 10 affaires · Cas A".</summary>
    [JsonIgnore] public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (DefaultAudienceId.HasValue) parts.Add($"audience {DefaultAudienceId}");
            else if (!string.IsNullOrEmpty(AudienceDate)) parts.Add($"{AudienceDate} {AudienceHeure}");
            if (!string.IsNullOrEmpty(ExpectedCase)) parts.Add($"Cas {ExpectedCase}");
            return string.Join(" · ", parts);
        }
    }
}
